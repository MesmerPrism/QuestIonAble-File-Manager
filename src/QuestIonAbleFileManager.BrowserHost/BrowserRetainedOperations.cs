using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserRetainedCapability(bool Supported, string Code, string Reason, string Alternative,
    IReadOnlyList<string>? ContractVersions = null);
public sealed class BrowserRetainedException(string code, int statusCode = 400, bool? dispatched = null) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public bool? Dispatched { get; } = dispatched;
}

public sealed record BrowserRetainedPreflightRequest(string ArtifactId, string Sha256, long SizeBytes,
    string Command, string? Serial, LocalApiInstallOptions? InstallOptions, int? ExpiresInSeconds,
    string ContractVersion = LocalApiContract.Version);

public sealed record BrowserRetainedInstallOptions(bool ReplaceExisting, bool GrantRuntimePermissions);
public sealed record BrowserRetainedV2PreflightResult(string ContractVersion, string OperationId, string Command,
    string CommandDigest, DateTimeOffset ExpiresAt, ApkArtifactInspection Artifact, QuestDevice? Target,
    BrowserRetainedInstallOptions InstallOptions);

// A test seam around the existing owner methods, never a second registry or journal.
internal sealed record BrowserRetainedOwnerCalls(
    Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiPreflightResult>> Preflight,
    Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiOperationStatus>> Execute,
    Func<ReadOnlyMemory<byte>, LocalApiOperationStatus> Status,
    Func<ReadOnlyMemory<byte>, LocalApiOperationStatus> Cancel, Action Dispose,
    Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiPreflightResult>>? PreflightV2 = null,
    Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiOperationStatus>>? ExecuteV2 = null,
    Func<ReadOnlyMemory<byte>, LocalApiOperationStatus>? StatusV2 = null,
    Func<ReadOnlyMemory<byte>, LocalApiOperationStatus>? CancelV2 = null,
    Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiV3PreflightResult>>? PreflightV3 = null,
    Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiV3OperationStatus>>? ExecuteV3 = null,
    Func<ReadOnlyMemory<byte>, LocalApiV3OperationStatus>? StatusV3 = null,
    Func<ReadOnlyMemory<byte>, LocalApiV3OperationStatus>? CancelV3 = null,
    Func<string, string, string, long, string, CancellationToken, Task<BrowserRetainedOutputLease>>? AcquireDiagnosticOutput = null);

/// <summary>In-process adapter to the existing explicitly configured, versioned retained owner.</summary>
public sealed partial class BrowserRetainedOperations : IDisposable
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
    private volatile bool _closing;
    private bool _disposing;
    private TaskCompletionSource? _drained;

    public BrowserRetainedOperations(Func<BrowserArtifactStore> artifacts, Func<IDisposable?>? tryAcquireOperation = null)
        : this(artifacts, tryAcquireOperation, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows()) { }

    internal BrowserRetainedOperations(Func<BrowserArtifactStore> artifacts, Func<IDisposable?>? tryAcquireOperation,
        Func<string, string?> readEnvironment, bool platformSupported)
    {
        _artifacts = artifacts;
        _tryAcquireOperation = tryAcquireOperation ?? TryAcquireLocal;
        _configuredCapability = () => ConfigurationCapability(readEnvironment, platformSupported, v3: true);
        _owner = new(() =>
        {
            // Settings and integrity key come from the configured existing owner, once.
            var settings = LocalApiStateSettings.FromEnvironment(readEnvironment);
            var registry = new LocalApiCommandRegistry(AdbClient.CreateDefault(), settings);
            return new(registry.PreflightAsync, registry.ExecuteAsync, registry.GetStatus, registry.Cancel, registry.Dispose,
                registry.PreflightV2Async, registry.ExecuteV2Async, registry.GetV2Status, registry.CancelV2,
                registry.PreflightV3Async, registry.ExecuteV3Async, registry.GetV3Status, registry.CancelV3,
                async (id, digest, output, size, hash, token) =>
                {
                    var lease = await registry.AcquireDiagnosticOutputAsync(id, digest, output, size, hash, token).ConfigureAwait(false);
                    return new(lease.Descriptor, lease.Stream, lease);
                });
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal BrowserRetainedOperations(Func<BrowserArtifactStore> artifacts, BrowserRetainedOwnerCalls owner,
        Func<IDisposable?>? tryAcquireOperation = null) : this(artifacts, () => owner, tryAcquireOperation, HasV3(owner)) { }

    internal BrowserRetainedOperations(Func<BrowserArtifactStore> artifacts, Func<BrowserRetainedOwnerCalls> ownerFactory,
        Func<IDisposable?>? tryAcquireOperation = null, bool supportsV3 = false)
    {
        _artifacts = artifacts;
        _tryAcquireOperation = tryAcquireOperation ?? TryAcquireLocal;
        _configuredCapability = () => Configured(supportsV3);
        _owner = new(ownerFactory);
    }

    public BrowserRetainedCapability Capability => _closing ? new(false, "retained_owner_closed", "The retained owner is closing.", "Wait for the current owner to finish safely.") : _ownerFailure ?? (_owner.IsValueCreated
        ? new(true, "available", "The configured retained owner is open in this process.", "Preserve its state and key; reconcile original operation IDs before any new recovery action.",
            OwnerVersions(_owner.Value))
        : _configuredCapability());

    public async Task<LocalApiPreflightResult> PreflightAsync(ReadOnlyMemory<byte> body, CancellationToken cancellation = default)
    {
        try { return await PreflightOwnedAsync(ParsePreflight(body), cancellation).ConfigureAwait(false); }
        catch (Exception exception) { throw Translate(exception); }
    }

    public async Task<BrowserRetainedV2PreflightResult> PreflightV2Async(ReadOnlyMemory<byte> body, CancellationToken cancellation = default)
    {
        try
        {
            var request = ParseV2Preflight(body);
            var result = await PreflightOwnedAsync(request, cancellation).ConfigureAwait(false);
            var options = request.InstallOptions ?? new LocalApiInstallOptions();
            // These are the exact normalized request options acknowledged by the owner,
            // not an independent options readback. The owner digest binds its actual command.
            return new(result.ContractVersion, result.OperationId, result.Command, result.CommandDigest, result.ExpiresAt,
                result.Artifact, result.Target, new(options.ReplaceExisting, options.GrantRuntimePermissions));
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    private async Task<LocalApiPreflightResult> PreflightOwnedAsync(BrowserRetainedPreflightRequest request, CancellationToken cancellation)
    {
        try
        {
            using var call = EnterCall(); EnsureSupported();
            using var gate = _tryAcquireOperation() ?? throw new BrowserRetainedException("operation_busy", 409);
            await using var lease = await _artifacts().AcquireAsync(request.ArtifactId, request.Sha256, request.SizeBytes, cancellation).ConfigureAwait(false);
            if (lease.Receipt.Id != request.ArtifactId || lease.Receipt.Sha256 != request.Sha256 || lease.Receipt.SizeBytes != request.SizeBytes)
                throw new BrowserRetainedException("artifact_binding_mismatch", 409);
            var owner = OwnerForVersion(request.ContractVersion);
            var ownerRequest = request.ContractVersion == LocalApiContract.Version
                ? JsonSerializer.SerializeToUtf8Bytes(new LocalApiPreflightRequest(LocalApiContract.Version, request.Command, lease.Path,
                    request.Serial, request.InstallOptions, request.ExpiresInSeconds), Json)
                : JsonSerializer.SerializeToUtf8Bytes(new LocalApiV2PreflightRequest(LocalApiV2Contract.Version, request.Command, lease.Path,
                    request.Serial!, request.InstallOptions, request.ExpiresInSeconds), Json);
            // Await the owner's durable staging copy and journal commit before releasing this source lease.
            var result = await owner.Preflight(ownerRequest, cancellation).ConfigureAwait(false);
            if (result.ContractVersion != request.ContractVersion || result.Command != request.Command ||
                !ValidOperationId(result.OperationId) || !ValidHash(result.CommandDigest) ||
                result.Artifact.Sha256 != request.Sha256 || result.Artifact.SizeBytes != request.SizeBytes ||
                result.Artifact.Path != $"retained://{result.OperationId}/base.apk" || result.Target?.Serial != request.Serial ||
                (request.ContractVersion == LocalApiV2Contract.Version && result.Target?.IsReady != true))
                throw new BrowserRetainedException("artifact_binding_mismatch", 409);
            return result;
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    /// <param name="stopping">Owner/application lifetime only; never a page's RequestAborted token.</param>
    public Task<LocalApiOperationStatus> ExecuteAsync(ReadOnlyMemory<byte> body, CancellationToken stopping = default) =>
        ExecuteForVersionAsync(body, LocalApiContract.Version, stopping);
    public Task<LocalApiOperationStatus> ExecuteV2Async(ReadOnlyMemory<byte> body, CancellationToken stopping = default) =>
        ExecuteForVersionAsync(body, LocalApiV2Contract.Version, stopping);

    private async Task<LocalApiOperationStatus> ExecuteForVersionAsync(ReadOnlyMemory<byte> body, string version, CancellationToken stopping)
    {
        try
        {
            var binding = ParseBinding(body, version);
            using var call = EnterCall(); EnsureSupported();
            var owner = OwnerForVersion(version);
            var current = VerifyBinding(owner.Status(OwnerOperationBody(binding)), binding);
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
                    var request = version == LocalApiContract.Version
                        ? JsonSerializer.SerializeToUtf8Bytes(new LocalApiExecuteRequest(version, binding.OperationId, binding.CommandDigest), Json)
                        : JsonSerializer.SerializeToUtf8Bytes(new LocalApiV2ExecuteRequest(version, binding.OperationId, binding.CommandDigest), Json);
                    // Core awaits executor acknowledgement/terminal state; a disconnected page cannot release the gate early.
                    return VerifyBinding(await owner.Execute(request, stopping).ConfigureAwait(false), binding);
                }
                finally { lock (_sync) _executing.Remove(binding.OperationId); }
            }
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    public LocalApiOperationStatus GetStatus(ReadOnlyMemory<byte> body) => GetStatusForVersion(body, LocalApiContract.Version);
    public LocalApiOperationStatus GetV2Status(ReadOnlyMemory<byte> body) => GetStatusForVersion(body, LocalApiV2Contract.Version);
    private LocalApiOperationStatus GetStatusForVersion(ReadOnlyMemory<byte> body, string version)
    {
        try
        {
            var binding = ParseBinding(body, version); using var call = EnterCall(); EnsureSupported();
            return VerifyBinding(OwnerForVersion(version).Status(OwnerOperationBody(binding)), binding);
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    public LocalApiOperationStatus Cancel(ReadOnlyMemory<byte> body) => CancelForVersion(body, LocalApiContract.Version);
    public LocalApiOperationStatus CancelV2(ReadOnlyMemory<byte> body) => CancelForVersion(body, LocalApiV2Contract.Version);
    private LocalApiOperationStatus CancelForVersion(ReadOnlyMemory<byte> body, string version)
    {
        try
        {
            var binding = ParseBinding(body, version); using var call = EnterCall(); EnsureSupported();
            var owner = OwnerForVersion(version); VerifyBinding(owner.Status(OwnerOperationBody(binding)), binding);
            return VerifyBinding(owner.Cancel(OwnerOperationBody(binding)), binding);
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    public static BrowserRetainedPreflightRequest ParsePreflight(ReadOnlyMemory<byte> body) => ParsePreflightForVersion(body, LocalApiContract.Version);
    public static BrowserRetainedPreflightRequest ParseV2Preflight(ReadOnlyMemory<byte> body) => ParsePreflightForVersion(body, LocalApiV2Contract.Version);
    private static BrowserRetainedPreflightRequest ParsePreflightForVersion(ReadOnlyMemory<byte> body, string version)
    {
        try
        {
            using var document = ParseObject(body);
            var root = document.RootElement;
            var allowed = new HashSet<string>(["artifactId", "sha256", "sizeBytes", "command", "serial"], StringComparer.Ordinal);
            if (version == LocalApiV2Contract.Version) allowed.Add("contractVersion");
            if (root.TryGetProperty("installOptions", out _)) allowed.Add("installOptions");
            if (root.TryGetProperty("expiresInSeconds", out _)) allowed.Add("expiresInSeconds");
            RequireFields(root, allowed);
            if (version == LocalApiV2Contract.Version && Text(root, "contractVersion") != version)
                throw new BrowserRetainedException("contract_unsupported");
            var id = BrowserArtifactCommands.RequireArtifactId(Text(root, "artifactId"));
            var hash = BrowserArtifactCommands.RequireHash(Text(root, "sha256"));
            if (!root.GetProperty("sizeBytes").TryGetInt64(out var size) || size is < 0 or > BrowserArtifactCommands.MaximumArtifactBytes)
                throw new BrowserRetainedException("artifact_size_invalid");
            var command = Text(root, "command");
            if (!(version == LocalApiContract.Version ? LocalApiContract.Commands : LocalApiV2Contract.Commands).Contains(command, StringComparer.Ordinal))
                throw new BrowserRetainedException("command_not_allowed");
            var serial = root.GetProperty("serial").ValueKind == JsonValueKind.Null ? null : Text(root, "serial");
            if ((command == "apk.inspect") != (serial is null)) throw new BrowserRetainedException("serial_invalid");
            if (version == LocalApiV2Contract.Version) serial = AndroidInput.RequireSerial(serial!);
            LocalApiInstallOptions? options = null;
            if (root.TryGetProperty("installOptions", out var optionElement))
            {
                if (command is not ("apk.install-inspected" or "apk.deploy") || optionElement.ValueKind != JsonValueKind.Object)
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
            return new(id, hash, size, command, serial, options, expires, version);
        }
        catch (Exception exception) { throw Translate(exception); }
    }

    private sealed record Binding(string OperationId, string CommandDigest, string ContractVersion);
    private static Binding ParseBinding(ReadOnlyMemory<byte> body, string version)
    {
        using var document = ParseObject(body); var root = document.RootElement;
        RequireFields(root, version == LocalApiContract.Version ? ["operationId", "commandDigest"] : ["contractVersion", "operationId", "commandDigest"]);
        if (version != LocalApiContract.Version && Text(root, "contractVersion") != version)
            throw new BrowserRetainedException("contract_unsupported");
        var id = Text(root, "operationId");
        if (!id.StartsWith("api-", StringComparison.Ordinal)) throw new BrowserRetainedException("operation_binding_invalid");
        BrowserArtifactCommands.RequireArtifactId(id[4..]);
        return new(id, BrowserArtifactCommands.RequireHash(Text(root, "commandDigest")), version);
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
    private static byte[] OwnerOperationBody(Binding binding) => binding.ContractVersion == LocalApiContract.Version
        ? JsonSerializer.SerializeToUtf8Bytes(new LocalApiOperationRequest(binding.ContractVersion, binding.OperationId), Json)
        : JsonSerializer.SerializeToUtf8Bytes(new LocalApiV2OperationRequest(binding.ContractVersion, binding.OperationId), Json);
    private static LocalApiOperationStatus VerifyBinding(LocalApiOperationStatus value, Binding binding)
    {
        if (value.OperationId != binding.OperationId || value.CommandDigest != binding.CommandDigest)
            throw new BrowserRetainedException("digest_mismatch", 409);
        if (value.ContractVersion != binding.ContractVersion) throw new BrowserRetainedException("operation_version_mismatch", 409);
        if (!(binding.ContractVersion == LocalApiContract.Version ? LocalApiContract.Commands : LocalApiV2Contract.Commands).Contains(value.Command, StringComparer.Ordinal))
            throw new BrowserRetainedException("operation_binding_mismatch", 409);
        return value;
    }

    private static bool ValidOperationId(string id) => id.StartsWith("api-", StringComparison.Ordinal) &&
        Guid.TryParseExact(id[4..], "N", out var parsed) && parsed != Guid.Empty && id[4..] == parsed.ToString("N");
    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(static character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
    private static bool HasV2(BrowserRetainedOwnerCalls owner) => owner.PreflightV2 is not null && owner.ExecuteV2 is not null && owner.StatusV2 is not null && owner.CancelV2 is not null;
    private BrowserRetainedOwnerCalls OwnerForVersion(string version)
    {
        var owner = Owner();
        if (version == LocalApiContract.Version) return owner;
        if (!HasV2(owner)) throw new BrowserRetainedException("retained_contract_unavailable", 501);
        return owner with { Preflight = owner.PreflightV2!, Execute = owner.ExecuteV2!, Status = owner.StatusV2!, Cancel = owner.CancelV2! };
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
    private static BrowserRetainedCapability ConfigurationCapability(Func<string, string?> read, bool supported, bool v3 = false)
    {
        if (!supported) return new(false, "retained_platform_unsupported", "The retained LocalApi owner requires Windows protected staging.", "Use the configured Windows companion; portable read/control routes remain separate.");
        var state = read(LocalApiStateSettings.StateDirectoryEnvironmentVariable);
        var secret = read(LocalApiStateSettings.JournalSecretEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(secret))
            return new(false, "retained_configuration_missing", "Retained operations need the explicitly configured existing durable owner.", "Configure QUESTIONABLE_FILE_MANAGER_API_STATE and QUESTIONABLE_FILE_MANAGER_API_JOURNAL_SECRET privately, preserving the existing state and key.");
        if (Encoding.UTF8.GetByteCount(secret) is < 32 or > 512)
            return new(false, "retained_configuration_invalid", "The private journal configuration is invalid.", "Use the existing valid 32–512 byte journal secret; never replace a key to bypass retained uncertainty.");
        return Configured(v3);
    }
    private static BrowserRetainedCapability Configured(bool v3 = false) => new(true, "configured_not_opened", "Configuration is present; the owner is validated on an explicit request.", "Preserve the configured state and journal secret across browser sessions and companion restarts.", v3 ? [LocalApiContract.Version, LocalApiV2Contract.Version, LocalApiV3Contract.Version] : [LocalApiContract.Version, LocalApiV2Contract.Version]);
    private IDisposable? TryAcquireLocal() => Interlocked.CompareExchange(ref _localBusy, 1, 0) == 0
        ? new Release(() => Interlocked.Exchange(ref _localBusy, 0)) : null;
    private IDisposable EnterCall()
    {
        lock (_sync)
        {
            if (_disposed || _closing) throw new BrowserRetainedException("retained_owner_closed", 503);
            _calls++; return new Release(() =>
            {
                TaskCompletionSource? drained;
                lock (_sync) { _calls--; drained = _calls == 0 ? _drained : null; }
                drained?.TrySetResult();
            });
        }
    }
    public Task DrainAsync()
    {
        lock (_sync)
        {
            _closing = true;
            if (_calls == 0) return Task.CompletedTask;
            return (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }
    public void Dispose()
    {
        BrowserRetainedOwnerCalls? owner;
        lock (_sync)
        {
            if (_disposed) return;
            if (_calls != 0 || _disposing) throw new BrowserRetainedException("retained_operation_in_use", 409);
            _closing = true; _disposing = true;
            owner = _owner.IsValueCreated ? _owner.Value : null;
        }
        try { owner?.Dispose(); lock (_sync) _disposed = true; }
        finally { lock (_sync) _disposing = false; }
    }
    internal static BrowserRetainedException Translate(Exception exception) => exception switch
    {
        BrowserRetainedException retained => retained,
        BrowserRequestException request when request.Code is "artifact_id_invalid" or "artifact_hash_invalid" => new(request.Code),
        JsonException or InvalidOperationException when exception is not LocalApiException => new("request_invalid"),
        ArgumentException => new("request_invalid"),
        LocalApiException api when api.Code is "operation_consumed" or "running_capacity" or "operation_expired" or "operation_not_running" or "digest_mismatch" or "state_in_use" or "operation_version_mismatch" => new(api.Code, 409),
        LocalApiException api when api.Code is "operation_unknown" => new(api.Code, 404),
        LocalApiException api when api.Code is "target_not_ready" or "split_not_supported" or "expiry_invalid" or "command_not_allowed" or "contract_unsupported" => new(api.Code),
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
