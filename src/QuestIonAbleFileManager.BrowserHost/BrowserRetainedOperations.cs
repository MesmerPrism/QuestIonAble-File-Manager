using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserRetainedCapability(bool Supported, string Code, string Reason, string Alternative);
public sealed class BrowserRetainedException(string code, int statusCode = 400, bool? dispatched = null) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public bool? Dispatched { get; } = dispatched;
}

public sealed record BrowserRetainedPreflightRequest(string ArtifactId, string Sha256, long SizeBytes,
    string Command, string? Serial, LocalApiInstallOptions? InstallOptions, int? ExpiresInSeconds);

// A test seam around the existing owner methods, never a second registry or journal.
internal sealed record BrowserRetainedOwnerCalls(
    Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiPreflightResult>> Preflight,
    Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiOperationStatus>> Execute,
    Func<ReadOnlyMemory<byte>, LocalApiOperationStatus> Status,
    Func<ReadOnlyMemory<byte>, LocalApiOperationStatus> Cancel, Action Dispose);

/// <summary>In-process adapter to the existing explicitly configured LocalApi v1 retained owner.</summary>
public sealed class BrowserRetainedOperations : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Func<BrowserArtifactStore> _artifacts;
    private readonly Func<IDisposable?> _tryAcquireOperation;
    private readonly Lazy<BrowserRetainedOwnerCalls> _owner;
    private readonly Func<BrowserRetainedCapability> _configuredCapability;
    private readonly object _sync = new();
    private readonly HashSet<string> _executing = new(StringComparer.Ordinal);
    private volatile BrowserRetainedCapability? _ownerFailure;
    private int _calls;
    private int _localBusy;
    private bool _disposed;

    public BrowserRetainedOperations(Func<BrowserArtifactStore> artifacts, Func<IDisposable?>? tryAcquireOperation = null)
        : this(artifacts, tryAcquireOperation, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows()) { }

    internal BrowserRetainedOperations(Func<BrowserArtifactStore> artifacts, Func<IDisposable?>? tryAcquireOperation,
        Func<string, string?> readEnvironment, bool platformSupported)
    {
        _artifacts = artifacts;
        _tryAcquireOperation = tryAcquireOperation ?? TryAcquireLocal;
        _configuredCapability = () => ConfigurationCapability(readEnvironment, platformSupported);
        _owner = new(() =>
        {
            // Settings and integrity key come from the configured existing owner, once.
            var settings = LocalApiStateSettings.FromEnvironment(readEnvironment);
            var registry = new LocalApiCommandRegistry(AdbClient.CreateDefault(), settings);
            return new(registry.PreflightAsync, registry.ExecuteAsync, registry.GetStatus, registry.Cancel, registry.Dispose);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal BrowserRetainedOperations(Func<BrowserArtifactStore> artifacts, BrowserRetainedOwnerCalls owner,
        Func<IDisposable?>? tryAcquireOperation = null) : this(artifacts, () => owner, tryAcquireOperation) { }

    internal BrowserRetainedOperations(Func<BrowserArtifactStore> artifacts, Func<BrowserRetainedOwnerCalls> ownerFactory,
        Func<IDisposable?>? tryAcquireOperation = null)
    {
        _artifacts = artifacts;
        _tryAcquireOperation = tryAcquireOperation ?? TryAcquireLocal;
        _configuredCapability = () => Configured();
        _owner = new(ownerFactory);
    }

    public BrowserRetainedCapability Capability => _ownerFailure ?? (_owner.IsValueCreated
        ? new(true, "available", "The configured retained owner is open in this process.", "Preserve its state and key; reconcile original operation IDs before any new recovery action.")
        : _configuredCapability());

    public async Task<LocalApiPreflightResult> PreflightAsync(ReadOnlyMemory<byte> body, CancellationToken cancellation = default)
    {
        try
        {
            var request = ParsePreflight(body);
            using var call = EnterCall(); EnsureSupported();
            using var gate = _tryAcquireOperation() ?? throw new BrowserRetainedException("operation_busy", 409);
            await using var lease = await _artifacts().AcquireAsync(request.ArtifactId, request.Sha256, request.SizeBytes, cancellation).ConfigureAwait(false);
            if (lease.Receipt.Id != request.ArtifactId || lease.Receipt.Sha256 != request.Sha256 || lease.Receipt.SizeBytes != request.SizeBytes)
                throw new BrowserRetainedException("artifact_binding_mismatch", 409);
            var ownerRequest = new LocalApiPreflightRequest(LocalApiContract.Version, request.Command, lease.Path,
                request.Serial, request.InstallOptions, request.ExpiresInSeconds);
            // Await the owner's durable staging copy and journal commit before releasing this source lease.
            var result = await Owner().Preflight(JsonSerializer.SerializeToUtf8Bytes(ownerRequest, Json), cancellation).ConfigureAwait(false);
            if (result.Command != request.Command || result.Artifact.Sha256 != request.Sha256 || result.Artifact.SizeBytes != request.SizeBytes ||
                result.Artifact.Path != $"retained://{result.OperationId}/base.apk" || result.Target?.Serial != request.Serial)
                throw new BrowserRetainedException("artifact_binding_mismatch", 409);
            return result;
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    /// <param name="stopping">Owner/application lifetime only; never a page's RequestAborted token.</param>
    public async Task<LocalApiOperationStatus> ExecuteAsync(ReadOnlyMemory<byte> body, CancellationToken stopping = default)
    {
        try
        {
            var binding = ParseBinding(body);
            using var call = EnterCall(); EnsureSupported();
            var owner = Owner();
            var current = VerifyBinding(owner.Status(OwnerOperationBody(binding.OperationId)), binding);
            IDisposable gate;
            lock (_sync)
            {
                if (_executing.Contains(binding.OperationId) || current.Stage != LocalApiOperationStage.Preflighted)
                    throw new BrowserRetainedException("operation_consumed", 409);
                gate = _tryAcquireOperation() ?? throw new BrowserRetainedException("operation_busy", 409);
                _executing.Add(binding.OperationId);
            }
            using (gate)
            {
                try
                {
                    var request = new LocalApiExecuteRequest(LocalApiContract.Version, binding.OperationId, binding.CommandDigest);
                    // Core awaits executor acknowledgement/terminal state; a disconnected page cannot release the gate early.
                    return await owner.Execute(JsonSerializer.SerializeToUtf8Bytes(request, Json), stopping).ConfigureAwait(false);
                }
                finally { lock (_sync) _executing.Remove(binding.OperationId); }
            }
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    public LocalApiOperationStatus GetStatus(ReadOnlyMemory<byte> body)
    {
        try
        {
            var binding = ParseBinding(body); using var call = EnterCall(); EnsureSupported();
            return VerifyBinding(Owner().Status(OwnerOperationBody(binding.OperationId)), binding);
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    public LocalApiOperationStatus Cancel(ReadOnlyMemory<byte> body)
    {
        try
        {
            var binding = ParseBinding(body); using var call = EnterCall(); EnsureSupported();
            var owner = Owner(); VerifyBinding(owner.Status(OwnerOperationBody(binding.OperationId)), binding);
            return VerifyBinding(owner.Cancel(OwnerOperationBody(binding.OperationId)), binding);
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    public static BrowserRetainedPreflightRequest ParsePreflight(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = ParseObject(body);
            var root = document.RootElement;
            var allowed = new HashSet<string>(["artifactId", "sha256", "sizeBytes", "command", "serial"], StringComparer.Ordinal);
            if (root.TryGetProperty("installOptions", out _)) allowed.Add("installOptions");
            if (root.TryGetProperty("expiresInSeconds", out _)) allowed.Add("expiresInSeconds");
            RequireFields(root, allowed);
            var id = BrowserArtifactCommands.RequireArtifactId(Text(root, "artifactId"));
            var hash = BrowserArtifactCommands.RequireHash(Text(root, "sha256"));
            if (!root.GetProperty("sizeBytes").TryGetInt64(out var size) || size is < 0 or > BrowserArtifactCommands.MaximumArtifactBytes)
                throw new BrowserRetainedException("artifact_size_invalid");
            var command = Text(root, "command");
            if (!LocalApiContract.Commands.Contains(command, StringComparer.Ordinal)) throw new BrowserRetainedException("command_not_allowed");
            var serial = root.GetProperty("serial").ValueKind == JsonValueKind.Null ? null : Text(root, "serial");
            if ((command == "apk.inspect") != (serial is null)) throw new BrowserRetainedException("serial_invalid");
            LocalApiInstallOptions? options = null;
            if (root.TryGetProperty("installOptions", out var optionElement))
            {
                if (command != "apk.install-inspected" || optionElement.ValueKind != JsonValueKind.Object)
                    throw new BrowserRetainedException("fields_not_allowed");
                RequireFields(optionElement, ["replaceExisting", "grantRuntimePermissions"]);
                options = new(Boolean(optionElement, "replaceExisting"), GrantRuntimePermissions: Boolean(optionElement, "grantRuntimePermissions"));
            }
            int? expires = null;
            if (root.TryGetProperty("expiresInSeconds", out var expiry))
            {
                if (!expiry.TryGetInt32(out var seconds) || seconds <= 0 || seconds > LocalApiContract.MaximumPreflightLifetime.TotalSeconds)
                    throw new BrowserRetainedException("expiry_invalid");
                expires = seconds;
            }
            return new(id, hash, size, command, serial, options, expires);
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    private sealed record Binding(string OperationId, string CommandDigest);
    private static Binding ParseBinding(ReadOnlyMemory<byte> body)
    {
        using var document = ParseObject(body); var root = document.RootElement;
        RequireFields(root, ["operationId", "commandDigest"]);
        var id = Text(root, "operationId");
        if (!id.StartsWith("api-", StringComparison.Ordinal)) throw new BrowserRetainedException("operation_binding_invalid");
        BrowserArtifactCommands.RequireArtifactId(id[4..]);
        return new(id, BrowserArtifactCommands.RequireHash(Text(root, "commandDigest")));
    }
    private static JsonDocument ParseObject(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) throw new BrowserRetainedException("body_size_invalid", 413);
        var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose(); throw new BrowserRetainedException("request_invalid");
    }
    private static void RequireFields(JsonElement root, IEnumerable<string> allowed)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name)) throw new BrowserRetainedException("duplicate_field");
        if (!names.SetEquals(allowed)) throw new BrowserRetainedException("fields_invalid");
    }
    private static string Text(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String && root.GetProperty(name).GetString() is { Length: > 0 and <= 256 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new BrowserRetainedException("field_invalid");
    private static bool Boolean(JsonElement root, string name) => root.GetProperty(name).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new BrowserRetainedException("field_invalid") };
    private static byte[] OwnerOperationBody(string id) => JsonSerializer.SerializeToUtf8Bytes(new LocalApiOperationRequest(LocalApiContract.Version, id), Json);
    private static LocalApiOperationStatus VerifyBinding(LocalApiOperationStatus value, Binding binding)
    {
        if (value.OperationId != binding.OperationId || value.CommandDigest != binding.CommandDigest)
            throw new BrowserRetainedException("digest_mismatch", 409);
        return value;
    }

    private BrowserRetainedOwnerCalls Owner()
    {
        try { return _owner.Value; }
        catch (Exception exception)
        {
            _ownerFailure = exception is LocalApiException { Code: "state_in_use" }
                ? new(false, "state_in_use", "The configured durable owner is already in use.", "Close the other owner, then restart this companion with the same state and journal secret.")
                : new(false, "retained_owner_unavailable", "The configured retained owner could not be opened safely.", "Check the existing private state and key configuration; preserve it and use the qualified owner recovery route.");
            throw new BrowserRetainedException(_ownerFailure.Code, _ownerFailure.Code == "state_in_use" ? 409 : 503);
        }
    }
    private void EnsureSupported()
    {
        var capability = Capability;
        if (!capability.Supported) throw new BrowserRetainedException(capability.Code, capability.Code switch { "retained_platform_unsupported" => 501, "state_in_use" => 409, _ => 503 });
    }
    private static BrowserRetainedCapability ConfigurationCapability(Func<string, string?> read, bool supported)
    {
        if (!supported) return new(false, "retained_platform_unsupported", "The retained LocalApi owner requires Windows protected staging.", "Use the configured Windows companion; portable read/control routes remain separate.");
        var state = read(LocalApiStateSettings.StateDirectoryEnvironmentVariable);
        var secret = read(LocalApiStateSettings.JournalSecretEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(secret))
            return new(false, "retained_configuration_missing", "Retained operations need the explicitly configured existing durable owner.", "Configure QUESTIONABLE_FILE_MANAGER_API_STATE and QUESTIONABLE_FILE_MANAGER_API_JOURNAL_SECRET privately, preserving the existing state and key.");
        if (Encoding.UTF8.GetByteCount(secret) is < 32 or > 512)
            return new(false, "retained_configuration_invalid", "The private journal configuration is invalid.", "Use the existing valid 32–512 byte journal secret; never replace a key to bypass retained uncertainty.");
        return Configured();
    }
    private static BrowserRetainedCapability Configured() => new(true, "configured_not_opened", "Configuration is present; the owner is validated on an explicit request.", "Preserve the configured state and journal secret across browser sessions and companion restarts.");
    private IDisposable? TryAcquireLocal() => Interlocked.CompareExchange(ref _localBusy, 1, 0) == 0
        ? new Release(() => Interlocked.Exchange(ref _localBusy, 0)) : null;
    private IDisposable EnterCall()
    {
        lock (_sync)
        {
            if (_disposed) throw new BrowserRetainedException("retained_owner_closed", 503);
            _calls++; return new Release(() => { lock (_sync) _calls--; });
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_calls != 0) throw new BrowserRetainedException("retained_operation_in_use", 409);
            if (_owner.IsValueCreated) _owner.Value.Dispose();
            _disposed = true;
        }
    }
    internal static BrowserRetainedException Translate(Exception exception) => exception switch
    {
        BrowserRetainedException retained => retained,
        BrowserRequestException request when request.Code is "artifact_id_invalid" or "artifact_hash_invalid" => new(request.Code),
        JsonException or InvalidOperationException when exception is not LocalApiException => new("request_invalid"),
        LocalApiException api when api.Code is "operation_consumed" or "running_capacity" or "operation_expired" or "operation_not_running" or "digest_mismatch" or "state_in_use" => new(api.Code, 409),
        LocalApiException api when api.Code is "operation_unknown" => new(api.Code, 404),
        LocalApiException api when api.Code is "target_not_ready" or "split_not_supported" or "expiry_invalid" or "command_not_allowed" => new(api.Code),
        BrowserArtifactException artifact when artifact.Code is "artifact_not_found" => new(artifact.Code, 404),
        BrowserArtifactException artifact when artifact.Code is "artifact_integrity_mismatch" or "artifact_in_use" => new(artifact.Code, 409),
        OperationCanceledException => new("retained_operation_cancelled", 408),
        _ => new("retained_owner_failure", 503)
    };
    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
