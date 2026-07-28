using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.Core;

public static class LocalApiContract
{
    public const string Version = "questionable.file_manager.local_api.v1";
    public const string CredentialEnvironmentVariable = "QUESTIONABLE_FILE_MANAGER_API_BEARER";
    public const int MaximumRequestBytes = 16 * 1024;
    public const int MinimumCredentialBytes = 32;
    public const int MaximumCredentialBytes = 512;
    public const int MaximumAuthorizationHeaderBytes = 1024;
    public static readonly TimeSpan DefaultPreflightLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumPreflightLifetime = TimeSpan.FromMinutes(5);
    public static readonly IReadOnlyList<string> Commands =
    [
        "apk.inspect",
        "apk.install-inspected",
        "app.launch-resolved",
        "runtime.observe"
    ];
}

public sealed class LocalApiException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class LocalApiSecurity
{
    public static string ReadCredentialFromEnvironment(
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var value = readEnvironment(LocalApiContract.CredentialEnvironmentVariable);
        if (value is null)
        {
            throw new LocalApiException(
                "credential_missing",
                $"Set {LocalApiContract.CredentialEnvironmentVariable} before starting the local API.");
        }
        ValidateCredential(value);
        return value;
    }

    public static void ValidateCredential(string credential)
    {
        var count = Encoding.UTF8.GetByteCount(credential);
        if (count is < LocalApiContract.MinimumCredentialBytes or > LocalApiContract.MaximumCredentialBytes)
        {
            throw new LocalApiException(
                "credential_invalid",
                $"The bearer credential must be {LocalApiContract.MinimumCredentialBytes}.." +
                $"{LocalApiContract.MaximumCredentialBytes} UTF-8 bytes.");
        }
    }

    public static bool FixedTimeEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    public static bool AuthenticateBearer(string expected, string authorizationHeader)
    {
        const string prefix = "Bearer ";
        var boundedLength = authorizationHeader.Length <= LocalApiContract.MaximumAuthorizationHeaderBytes;
        var headerBytes = boundedLength ? Encoding.UTF8.GetByteCount(authorizationHeader) : int.MaxValue;
        var shaped = boundedLength &&
                     headerBytes <= LocalApiContract.MaximumAuthorizationHeaderBytes &&
                     authorizationHeader.StartsWith(prefix, StringComparison.Ordinal);
        var supplied = shaped ? authorizationHeader[prefix.Length..] : string.Empty;
        var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var equal = CryptographicOperations.FixedTimeEquals(expectedDigest, suppliedDigest);
        return shaped && equal;
    }

    public static Uri RequireExplicitLoopback(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.Port is < 1 or > 65535 ||
            !IPAddress.TryParse(uri.Host, out var address) ||
            !IPAddress.IsLoopback(address) ||
            uri.AbsolutePath != "/")
        {
            throw new LocalApiException(
                "listen_address_invalid",
                "The API listen address must be an explicit HTTP loopback IP address and port.");
        }
        return uri;
    }
}

public sealed record LocalApiCapabilities(
    string ContractVersion,
    IReadOnlyList<string> Commands,
    int MaximumRequestBytes,
    int PreflightLifetimeSeconds);

public enum LocalApiOperationStage
{
    Preflighted,
    Running,
    CancellationRequested,
    Completed,
    Failed,
    Cancelled,
    Expired,
    OutcomeUnknownRecoveryRequired,
    CleanupDebt
}

public sealed record LocalApiPreflightResult(
    string ContractVersion,
    string OperationId,
    string Command,
    string CommandDigest,
    DateTimeOffset ExpiresAt,
    ApkArtifactInspection Artifact,
    QuestDevice? Target);

public sealed record LocalApiOperationStatus(
    string ContractVersion,
    string OperationId,
    string Command,
    string CommandDigest,
    DateTimeOffset ExpiresAt,
    LocalApiOperationStage Stage,
    object? Result,
    OperatorMutationReceipt? MutationEvidence,
    string? ErrorCode,
    string? Error);

public sealed class LocalApiCommandRegistry : IDisposable
{
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly OperatorCommandExecutor _executor;
    private readonly AdbClient _client;
    private readonly Dictionary<string, RetainedOperation> _operations = new(StringComparer.Ordinal);
    private readonly object _operationsGate = new();
    private readonly TimeProvider _timeProvider;
    private readonly LocalApiStateSettings _settings;
    private readonly LocalApiArtifactStager _stager;
    private readonly LocalApiJournal _journal;
    private int _reservedPreflights;
    private long _reservedStageBytes;

    private static string ResponseArtifactPath(string operationId) =>
        $"retained://{operationId}/base.apk";

    internal LocalApiCommandRegistry(
        AdbClient client,
        OperatorCommandExecutor? executor = null,
        TimeProvider? timeProvider = null)
        : this(
            client,
            LocalApiStateSettings.CreateForTests(
                Path.Combine(Path.GetTempPath(), "qfm-api-state-" + Guid.NewGuid().ToString("N"))),
            executor,
            timeProvider)
    {
    }

    public LocalApiCommandRegistry(
        AdbClient client,
        LocalApiStateSettings stateSettings,
        OperatorCommandExecutor? executor = null,
        TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _executor = executor ?? new OperatorCommandExecutor(client);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _settings = stateSettings ?? throw new ArgumentNullException(nameof(stateSettings));
        _stager = new LocalApiArtifactStager(_settings);
        LocalApiJournal? journal = null;
        try
        {
            journal = new LocalApiJournal(_settings);
            _journal = journal;
            LoadJournal();
        }
        catch
        {
            journal?.Dispose();
            _stager.Dispose();
            throw;
        }
    }

    public LocalApiCapabilities GetCapabilities() =>
        new(
            LocalApiContract.Version,
            LocalApiContract.Commands,
            LocalApiContract.MaximumRequestBytes,
            (int)LocalApiContract.DefaultPreflightLifetime.TotalSeconds);

    public async Task<LocalApiPreflightResult> PreflightAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        EnsureBodyBound(body);
        RejectDuplicateProperties(body.Span);
        LocalApiPreflightRequest request;
        try
        {
            request = JsonSerializer.Deserialize<LocalApiPreflightRequest>(body.Span, StrictJson) ??
                throw Input("request_invalid", "The preflight request is required.");
        }
        catch (JsonException exception)
        {
            throw Input("request_invalid", $"The preflight JSON is invalid: {exception.Message}");
        }
        request.Validate();
        LocalApiStagedArtifact staged;
        long reservedBytes = 0;
        long untrackedStagedBytes;
        lock (_operationsGate)
        {
            SweepLocked(_timeProvider.GetUtcNow());
            var inventory = _stager.GetInventory();
            if (_operations.Count + _reservedPreflights >= _settings.Limits.MaximumRetainedOperations)
                throw Input("operation_capacity", "The retained-operation capacity is exhausted.");
            if (inventory.FileCount + _reservedPreflights >= _settings.Limits.MaximumStagedFiles)
                throw Input("staged_file_capacity", "The staged-file capacity is exhausted.");
            var retainedBytes = _operations.Values.Sum(static operation => operation.Artifact.SizeBytes);
            untrackedStagedBytes = Math.Max(0, inventory.SizeBytes - retainedBytes);
            _reservedPreflights++;
        }
        try
        {
            staged = await _stager.StageAsync(
                request.ApkPath,
                cancellationToken,
                size =>
                {
                    lock (_operationsGate)
                    {
                        var retainedBytes = _operations.Values.Sum(static operation => operation.Artifact.SizeBytes);
                        if (retainedBytes + untrackedStagedBytes + _reservedStageBytes + size >
                            _settings.Limits.MaximumStagedBytes)
                            return false;
                        _reservedStageBytes += size;
                        reservedBytes = size;
                        return true;
                    }
                }).ConfigureAwait(false);
        }
        catch
        {
            lock (_operationsGate)
            {
                _reservedPreflights--;
                _reservedStageBytes -= reservedBytes;
            }
            throw;
        }
        try
        {
            var stagedRequest = request with { ApkPath = staged.Path };
            var command = BuildCommand(stagedRequest);
            var artifact = await _client.CreateApkInspector()
                .InspectAsync(staged.Path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(artifact.Identity.SplitName))
            {
                throw Input("split_not_supported", "The local API accepts one base APK only.");
            }

            QuestDevice? target = null;
            if (command.Serial is not null)
            {
                var devices = await _client.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
                var matches = devices.Where(device =>
                    string.Equals(device.Serial, command.Serial, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1 || !matches[0].IsReady)
                {
                    throw Input("target_not_ready", "The exact selected serial is not uniquely ready.");
                }
                target = matches[0];
            }

            var lifetime = TimeSpan.FromSeconds(request.ExpiresInSeconds ??
                (int)LocalApiContract.DefaultPreflightLifetime.TotalSeconds);
            if (lifetime <= TimeSpan.Zero || lifetime > LocalApiContract.MaximumPreflightLifetime)
            {
                throw Input("expiry_invalid", "Preflight expiry is outside the allowed bound.");
            }
            var now = _timeProvider.GetUtcNow();
            var id = "api-" + Guid.NewGuid().ToString("N");
            var digest = ComputeCommandDigest(command, artifact);
            var retained = new RetainedOperation(
                id, request.Command, digest, now.Add(lifetime), command, artifact, staged,
                maximumResultBytes: _settings.Limits.MaximumResultBytes,
                maximumOutputCharacters: _settings.Limits.MaximumOutputCharacters);
            lock (_operationsGate)
            {
                if (_operations.ContainsKey(id))
                    throw new LocalApiException("operation_collision", "Could not allocate an operation identifier.");
                _operations.Add(id, retained);
                try
                {
                    PersistLocked();
                }
                catch
                {
                    _operations.Remove(id);
                    throw;
                }
                _reservedPreflights--;
                _reservedStageBytes -= reservedBytes;
            }
            return new LocalApiPreflightResult(
                LocalApiContract.Version,
                id,
                request.Command,
                digest,
                retained.ExpiresAt,
                artifact with { Path = ResponseArtifactPath(id) },
                target);
        }
        catch
        {
            lock (_operationsGate)
            {
                if (_reservedPreflights > 0) _reservedPreflights--;
                _reservedStageBytes = Math.Max(0, _reservedStageBytes - reservedBytes);
            }
            staged.TryDelete(out _);
            throw;
        }
    }

    public Task<LocalApiOperationStatus> ExecuteAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var request = ParseBound<LocalApiExecuteRequest>(body);
        request.Validate();
        RetainedOperation operation;
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(request.OperationId, out operation!))
                throw Input("operation_unknown", "The operation identifier is unknown.");
            if (!FixedDigestEquals(operation.CommandDigest, request.CommandDigest))
                throw Input("digest_mismatch", "The retained command digest does not match.");
            if (_operations.Values.Count(static item => item.IsRunning) >=
                _settings.Limits.MaximumRunningOperations)
                throw Input("running_capacity", "The running-operation capacity is exhausted.");
            var consume = operation.TryConsume(_timeProvider.GetUtcNow());
            if (consume == ConsumeDecision.Expired)
                throw Input("operation_expired", "The retained command has expired.");
            if (consume == ConsumeDecision.Consumed)
                throw Input("operation_consumed", "The retained command is one-use and has already been consumed.");
            try
            {
                PersistLocked();
            }
            catch
            {
                operation.RollbackConsume();
                throw Input("journal_persist_failed", "The operation was not dispatched because consume persistence failed.");
            }
        }
        return RunRetainedAsync(operation, cancellationToken);
    }

    public LocalApiOperationStatus GetStatus(ReadOnlyMemory<byte> body)
    {
        var request = ParseBound<LocalApiOperationRequest>(body);
        request.Validate();
        RetainedOperation operation;
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(request.OperationId, out operation!))
                throw Input("operation_unknown", "The operation identifier is unknown.");
            operation.ExpireIfNeeded(_timeProvider.GetUtcNow());
            PersistLocked();
        }
        return operation.Snapshot();
    }

    public LocalApiOperationStatus Cancel(ReadOnlyMemory<byte> body)
    {
        var request = ParseBound<LocalApiOperationRequest>(body);
        request.Validate();
        RetainedOperation operation;
        lock (_operationsGate)
        {
            if (!_operations.TryGetValue(request.OperationId, out operation!))
                throw Input("operation_unknown", "The operation identifier is unknown.");
            var requested = operation.RequestCancellation();
            PersistLocked();
            return requested;
        }
    }

    private async Task<LocalApiOperationStatus> RunRetainedAsync(
        RetainedOperation operation,
        CancellationToken requestCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellation, operation.Cancellation.Token);
        try
        {
            var currentArtifact = await _client.CreateApkInspector()
                .InspectAsync(operation.Artifact.Path, linked.Token).ConfigureAwait(false);
            if (currentArtifact != operation.Artifact)
            {
                throw new LocalApiException(
                    "artifact_changed",
                    "The retained APK artifact changed after preflight.");
            }
            operation.MarkDispatched();
            lock (_operationsGate) PersistLocked();
            var result = await _executor.ExecuteAsync(
                operation.Command, linked.Token).ConfigureAwait(false);
            operation.Complete(result, _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            operation.AcknowledgeCancellation(_timeProvider.GetUtcNow());
        }
        catch (Exception exception)
        {
            operation.Fail(exception, _timeProvider.GetUtcNow());
        }
        lock (_operationsGate)
        {
            try
            {
                PersistLocked();
            }
            catch
            {
                operation.MarkPersistenceRecovery(_timeProvider.GetUtcNow());
            }
        }
        return operation.Snapshot();
    }

    private void LoadJournal()
    {
        lock (_operationsGate)
        {
            var entries = _journal.Load();
            _stager.CleanupUntrackedArtifacts(
                entries.Select(static entry => Path.GetFullPath(entry.StagedPath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
            foreach (var entry in entries)
            {
                if (entry.Stage == LocalApiOperationStage.CleanupDebt &&
                    _stager.TryCleanupDebt(entry.StagedPath))
                {
                    continue;
                }
                var staged = _stager.Reopen(entry.StagedPath);
                ApkArtifactInspection inspected;
                try
                {
                    inspected = _client.CreateApkInspector()
                        .InspectAsync(staged.Path, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    staged.Dispose();
                    throw new LocalApiException(
                        "journal_damaged",
                        "A retained staged artifact could not be re-inspected during recovery.");
                }
                if (inspected != entry.Artifact)
                {
                    staged.Dispose();
                    throw new LocalApiException(
                        "journal_damaged",
                        "A retained staged artifact does not match its durable binding.");
                }
                var request = new LocalApiPreflightRequest(
                    LocalApiContract.Version,
                    entry.CommandName,
                    entry.StagedPath,
                    entry.Serial,
                    entry.InstallOptions);
                var command = BuildCommand(request);
                if (!FixedDigestEquals(entry.CommandDigest, ComputeCommandDigest(command, inspected)))
                {
                    staged.Dispose();
                    throw new LocalApiException(
                        "journal_damaged",
                        "A retained command does not match its durable artifact binding.");
                }
                var stage = entry.Stage;
                var errorCode = entry.ErrorCode;
                var error = entry.Error;
                var terminalAt = entry.TerminalAt;
                if (stage is LocalApiOperationStage.Running or LocalApiOperationStage.CancellationRequested)
                {
                    if (OperatorMutations.RequiresHeadsetStateChange(command))
                    {
                        stage = LocalApiOperationStage.OutcomeUnknownRecoveryRequired;
                        errorCode = "restart_recovery_required";
                        error = "The process restarted after mutation dispatch; typed readback reconciliation is required.";
                    }
                    else
                    {
                        stage = LocalApiOperationStage.Failed;
                        errorCode = "interrupted_read_only";
                        error = "The read-only operation was interrupted by process restart.";
                    }
                    terminalAt ??= _timeProvider.GetUtcNow();
                }
                _operations.Add(entry.OperationId, new RetainedOperation(
                    entry.OperationId, entry.CommandName, entry.CommandDigest, entry.ExpiresAt,
                    command, entry.Artifact, staged, entry.Consumed, entry.Dispatched, stage,
                    terminalAt, entry.MutationEvidence, errorCode, error,
                    _settings.Limits.MaximumResultBytes, entry.ResultEvidence,
                    _settings.Limits.MaximumOutputCharacters));
            }
            SweepLocked(_timeProvider.GetUtcNow());
            PersistLocked();
        }
    }

    private void PersistLocked() =>
        _journal.Save(_operations.Values.Select(static operation => operation.ToJournal()).ToArray());

    public void Dispose()
    {
        lock (_operationsGate)
        {
            foreach (var operation in _operations.Values) operation.ReleaseHandles();
            _journal.Dispose();
            _stager.Dispose();
        }
    }

    private void SweepLocked(DateTimeOffset now)
    {
        foreach (var item in _operations.Values)
            item.ExpireIfNeeded(now);
        var cutoff = now - _settings.Limits.EffectiveTerminalRetention;
        var candidates = _operations.Values
            .Where(item => item.IsSweepable(cutoff) || item.IsCleanupDebt)
            .ToArray();
        if (candidates.Length == 0) return;
        foreach (var item in candidates) item.MarkCleanupDebt(now);
        PersistLocked();
        foreach (var item in candidates)
        {
            if (item.TryCleanup(_settings.CleanupFault))
                _operations.Remove(item.Id);
        }
        PersistLocked();
    }

    private static OperatorCommand BuildCommand(LocalApiPreflightRequest request) =>
        request.Command switch
        {
            "apk.inspect" => OperatorCommands.InspectApk(request.ApkPath),
            "apk.install-inspected" => OperatorCommands.InstallApk(
                RequireSerial(request), request.ApkPath, request.InstallOptions?.ToCore()),
            "app.launch-resolved" => OperatorCommands.LaunchInspectedApp(
                RequireSerial(request), request.ApkPath),
            "runtime.observe" => OperatorCommands.ObserveInspectedApp(
                RequireSerial(request), request.ApkPath),
            _ => throw Input("command_not_allowed", "The requested command is not implemented.")
        };

    private static string RequireSerial(LocalApiPreflightRequest request) =>
        !string.IsNullOrWhiteSpace(request.Serial)
            ? request.Serial
            : throw Input("serial_required", "This command requires an exact serial.");

    private static string ComputeCommandDigest(
        OperatorCommand command,
        ApkArtifactInspection artifact)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = command.Kind.ToString(),
            arguments = command.CliArguments,
            artifact = new
            {
                artifact.SizeBytes,
                artifact.Sha256,
                artifact.Identity
            }
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static bool FixedDigestEquals(string expected, string supplied)
    {
        var shaped = supplied.Length == 64;
        var expectedToken = SHA256.HashData(Encoding.ASCII.GetBytes(expected));
        var suppliedToken = SHA256.HashData(Encoding.ASCII.GetBytes(
            supplied.Length <= 128 ? supplied : supplied[..128]));
        return shaped && CryptographicOperations.FixedTimeEquals(expectedToken, suppliedToken);
    }

    private static T ParseBound<T>(ReadOnlyMemory<byte> body) where T : class
    {
        EnsureBodyBound(body);
        RejectDuplicateProperties(body.Span);
        try
        {
            return JsonSerializer.Deserialize<T>(body.Span, StrictJson) ??
                throw Input("request_invalid", "The request body is required.");
        }
        catch (JsonException exception)
        {
            throw Input("request_invalid", $"The request JSON is invalid: {exception.Message}");
        }
    }

    private static void EnsureBodyBound(ReadOnlyMemory<byte> body)
    {
        if (body.Length is < 2 or > LocalApiContract.MaximumRequestBytes)
        {
            throw Input("request_size_invalid", "The request body is outside the allowed size bound.");
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 16
            });
            var objects = new Stack<HashSet<string>?>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                    objects.Push(new HashSet<string>(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.StartArray)
                    objects.Push(null);
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName &&
                         objects.Peek() is { } names &&
                         !names.Add(reader.GetString()!))
                    throw Input("duplicate_property", "Duplicate JSON properties are not allowed.");
            }
        }
        catch (JsonException exception)
        {
            throw Input("request_invalid", $"The request JSON is invalid: {exception.Message}");
        }
    }

    private static LocalApiException Input(string code, string message) => new(code, message);

    private enum ConsumeDecision { Started, Expired, Consumed }

    private sealed class RetainedOperation(
        string id,
        string commandName,
        string digest,
        DateTimeOffset expiresAt,
        OperatorCommand command,
        ApkArtifactInspection artifact,
        LocalApiStagedArtifact staged,
        bool consumed = false,
        bool dispatched = false,
        LocalApiOperationStage stage = LocalApiOperationStage.Preflighted,
        DateTimeOffset? terminalAt = null,
        OperatorMutationReceipt? mutationEvidence = null,
        string? errorCode = null,
        string? error = null,
        int maximumResultBytes = 64 * 1024,
        JsonElement? resultEvidence = null,
        int maximumOutputCharacters = 4 * 1024)
    {
        private static readonly JsonSerializerOptions ResultJson = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        private readonly object _gate = new();
        private bool _consumed = consumed;
        private bool _dispatched = dispatched;
        private DateTimeOffset? _terminalAt = terminalAt;
        private LocalApiOperationStage _stage = stage;
        private object? _result = resultEvidence;
        private OperatorMutationReceipt? _mutationEvidence = mutationEvidence;
        private string? _errorCode = errorCode;
        private string? _error = error;

        public string Id { get; } = id;
        public string CommandName { get; } = commandName;
        public string CommandDigest { get; } = digest;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public OperatorCommand Command { get; } = command;
        public ApkArtifactInspection Artifact { get; } = artifact;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool IsRunning
        {
            get { lock (_gate) return _stage is LocalApiOperationStage.Running or LocalApiOperationStage.CancellationRequested; }
        }
        public bool IsCleanupDebt
        {
            get { lock (_gate) return _stage == LocalApiOperationStage.CleanupDebt; }
        }

        public ConsumeDecision TryConsume(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_consumed) return ConsumeDecision.Consumed;
                if (now >= ExpiresAt)
                {
                    _consumed = true;
                    _stage = LocalApiOperationStage.Expired;
                    _terminalAt = now;
                    return ConsumeDecision.Expired;
                }
                _consumed = true;
                _stage = LocalApiOperationStage.Running;
                return ConsumeDecision.Started;
            }
        }

        public void RollbackConsume()
        {
            lock (_gate)
            {
                if (_stage == LocalApiOperationStage.Running && !_dispatched)
                {
                    _consumed = false;
                    _stage = LocalApiOperationStage.Preflighted;
                }
            }
        }

        public void MarkDispatched()
        {
            lock (_gate)
            {
                _dispatched = true;
                if (OperatorMutations.RequiresHeadsetStateChange(Command))
                {
                    var now = DateTimeOffset.UtcNow;
                    _mutationEvidence = new OperatorMutationReceipt(
                        Id,
                        Command.Kind,
                        Command.Serial ?? "local-api",
                        OperatorMutations.DesiredState(Command),
                        OperatorMutationStage.Pending,
                        "Dispatch began; exact effective-state readback is not yet conclusive.",
                        HeadsetReadback: false,
                        [
                            new OperatorMutationTransition(
                                OperatorMutationStage.Sent, now, "The retained typed mutation was dispatched."),
                            new OperatorMutationTransition(
                                OperatorMutationStage.Pending, now, "Waiting for exact typed readback.")
                        ]);
                }
            }
        }

        public void Complete(OperatorExecutionResult result, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (IsTerminal(_stage)) return;
                var boundedResult = BoundResult(result, maximumResultBytes, maximumOutputCharacters);
                if (!ReferenceEquals(boundedResult, result) &&
                    JsonSerializer.SerializeToUtf8Bytes(boundedResult).Length > maximumResultBytes)
                {
                    boundedResult = new OperatorExecutionResult(
                        result.Command,
                        MutationReceipt: result.MutationReceipt);
                    _errorCode = "result_evidence_bounded";
                    _error = "Detailed result evidence exceeded the retained-output bound.";
                }
                _result = SanitizeResultForResponse(
                    boundedResult,
                    staged.Path,
                    ResponseArtifactPath(Id));
                _mutationEvidence = result.MutationReceipt;
                _stage = result.MutationReceipt is { Stage: not OperatorMutationStage.Confirmed }
                    ? LocalApiOperationStage.OutcomeUnknownRecoveryRequired
                    : LocalApiOperationStage.Completed;
                _terminalAt = now;
            }
        }

        public void Fail(Exception exception, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (IsTerminal(_stage)) return;
                _errorCode = "execution_failed";
                _error = BoundError(
                    SanitizePrivateText(
                        exception.Message,
                        staged.Path,
                        ResponseArtifactPath(Id)),
                    maximumOutputCharacters);
                _stage = _dispatched && OperatorMutations.RequiresHeadsetStateChange(Command)
                    ? LocalApiOperationStage.OutcomeUnknownRecoveryRequired
                    : LocalApiOperationStage.Failed;
                _terminalAt = now;
            }
        }

        public LocalApiOperationStatus RequestCancellation()
        {
            LocalApiOperationStatus snapshot;
            lock (_gate)
            {
                if (_stage == LocalApiOperationStage.Preflighted)
                    throw new LocalApiException(
                        "operation_not_running",
                        "A preflighted operation cannot be cancelled before one-use execution begins.");
                if (_stage == LocalApiOperationStage.Running)
                {
                    _stage = LocalApiOperationStage.CancellationRequested;
                }
                snapshot = SnapshotLocked();
            }
            Cancellation.Cancel();
            return snapshot;
        }

        public void MarkPersistenceRecovery(DateTimeOffset now)
        {
            lock (_gate)
            {
                _stage = _dispatched && OperatorMutations.RequiresHeadsetStateChange(Command)
                    ? LocalApiOperationStage.OutcomeUnknownRecoveryRequired
                    : LocalApiOperationStage.Failed;
                _errorCode = "journal_persist_failed";
                _error = "The terminal transition could not be durably journaled; restart recovery is required.";
                _terminalAt = now;
            }
        }

        public void AcknowledgeCancellation(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (IsTerminal(_stage)) return;
                _stage = _dispatched && OperatorMutations.RequiresHeadsetStateChange(Command)
                    ? LocalApiOperationStage.OutcomeUnknownRecoveryRequired
                    : LocalApiOperationStage.Cancelled;
                _terminalAt = now;
            }
        }

        public void ExpireIfNeeded(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_stage == LocalApiOperationStage.Preflighted && now >= ExpiresAt)
                {
                    _stage = LocalApiOperationStage.Expired;
                    _consumed = true;
                    _terminalAt = now;
                }
            }
        }

        public bool IsSweepable(DateTimeOffset cutoff)
        {
            lock (_gate)
                return _terminalAt is not null && _terminalAt < cutoff;
        }

        public void MarkCleanupDebt(DateTimeOffset now)
        {
            lock (_gate)
            {
                _stage = LocalApiOperationStage.CleanupDebt;
                _errorCode = "cleanup_pending";
                _error = "Durable state was tombstoned before staged-evidence cleanup.";
                _terminalAt ??= now;
            }
        }

        public bool TryCleanup(Func<string, bool>? fault = null)
        {
            if (fault?.Invoke(Id) == true)
            {
                lock (_gate)
                {
                    _errorCode = "cleanup_debt";
                    _error = "Injected owned-handle cleanup failure.";
                }
                return false;
            }
            if (staged.TryDelete(out var failure))
            {
                Cancellation.Dispose();
                return true;
            }
            lock (_gate)
            {
                _errorCode = "cleanup_debt";
                _error = BoundError(
                    SanitizePrivateText(
                        failure ?? "Owned-handle cleanup failed.",
                        staged.Path,
                        ResponseArtifactPath(Id)),
                    maximumOutputCharacters);
            }
            return false;
        }

        public void Dispose()
        {
            staged.TryDelete(out _);
            Cancellation.Dispose();
        }

        public void ReleaseHandles()
        {
            staged.Dispose();
            Cancellation.Dispose();
        }

        private static OperatorExecutionResult BoundResult(
            OperatorExecutionResult result,
            int maximumResultBytes,
            int maximumOutputCharacters)
        {
            string Bound(string value) =>
                value.Length <= maximumOutputCharacters
                    ? value
                    : value[..maximumOutputCharacters] + "[truncated]";
            CommandResult? BoundCommand(CommandResult? command) => command is null
                ? null
                : command with
                {
                    StandardOutput = Bound(command.StandardOutput),
                    StandardError = Bound(command.StandardError)
                };
            var bounded = result with
            {
                CommandResult = BoundCommand(result.CommandResult),
                InspectedApkInstallResult = result.InspectedApkInstallResult is { } install
                    ? install with { CommandResult = BoundCommand(install.CommandResult)! }
                    : null,
                ResolvedAppLaunchResult = result.ResolvedAppLaunchResult is { } launch
                    ? launch with { CommandResult = BoundCommand(launch.CommandResult)! }
                    : null
            };
            return JsonSerializer.SerializeToUtf8Bytes(bounded).Length <= maximumResultBytes
                ? bounded
                : new OperatorExecutionResult(
                    result.Command,
                    MutationReceipt: result.MutationReceipt);
        }

        private static string BoundError(string value, int maximumCharacters) =>
            value.Length <= maximumCharacters
                ? value
                : value[..maximumCharacters] + "[truncated]";

        public LocalApiOperationStatus Snapshot()
        {
            lock (_gate)
            {
                return SnapshotLocked();
            }
        }

        private LocalApiOperationStatus SnapshotLocked()
        {
            var publicPath = ResponseArtifactPath(Id);
            return new LocalApiOperationStatus(
                LocalApiContract.Version,
                Id,
                CommandName,
                CommandDigest,
                ExpiresAt,
                _stage,
                SanitizeResultForResponse(_result, staged.Path, publicPath),
                _mutationEvidence,
                _errorCode,
                _error is null
                    ? null
                    : SanitizePrivateText(_error, staged.Path, publicPath));
        }

        private static object? SanitizeResultForResponse(
            object? result,
            string privatePath,
            string publicPath)
        {
            if (result is null) return null;
            var node = JsonSerializer.SerializeToNode(
                result,
                result.GetType(),
                ResultJson);
            SanitizeNode(node, privatePath, publicPath);
            return JsonSerializer.SerializeToElement(node, ResultJson);
        }

        private static void SanitizeNode(JsonNode? node, string privatePath, string publicPath)
        {
            if (node is JsonObject objectNode)
            {
                foreach (var key in objectNode.Select(static pair => pair.Key).ToArray())
                {
                    if (objectNode[key] is JsonValue value &&
                        value.TryGetValue<string>(out var text))
                    {
                        objectNode[key] = SanitizePrivateText(text, privatePath, publicPath);
                    }
                    else
                    {
                        SanitizeNode(objectNode[key], privatePath, publicPath);
                    }
                }
            }
            else if (node is JsonArray arrayNode)
            {
                for (var index = 0; index < arrayNode.Count; index++)
                {
                    if (arrayNode[index] is JsonValue value &&
                        value.TryGetValue<string>(out var text))
                    {
                        arrayNode[index] = SanitizePrivateText(text, privatePath, publicPath);
                    }
                    else
                    {
                        SanitizeNode(arrayNode[index], privatePath, publicPath);
                    }
                }
            }
        }

        private static string SanitizePrivateText(
            string text,
            string stagedPath,
            string publicPath)
        {
            var stagedDirectory = Path.GetDirectoryName(stagedPath)!;
            var stateRoot = Path.GetDirectoryName(stagedDirectory)!;
            var admissionRoot = Path.Combine(
                Path.GetTempPath(),
                "QuestIonAbleFileManager.ApkAdmission");
            if (text.StartsWith(stagedPath, StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith(admissionRoot, StringComparison.OrdinalIgnoreCase))
            {
                return publicPath;
            }
            return text
                .Replace(stagedPath, publicPath, StringComparison.OrdinalIgnoreCase)
                .Replace(
                    admissionRoot,
                    publicPath + "#execution",
                    StringComparison.OrdinalIgnoreCase)
                .Replace(
                    stateRoot,
                    publicPath + "#state",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTerminal(LocalApiOperationStage stage) => stage is
            LocalApiOperationStage.Completed or LocalApiOperationStage.Failed or
            LocalApiOperationStage.Cancelled or LocalApiOperationStage.Expired or
            LocalApiOperationStage.OutcomeUnknownRecoveryRequired or
            LocalApiOperationStage.CleanupDebt;

        public LocalApiJournalEntry ToJournal()
        {
            lock (_gate)
            {
                var options = Command.InstallOptions is { } core
                    ? new LocalApiInstallOptions(
                        core.ReplaceExisting, core.AllowDowngrade,
                        core.GrantRuntimePermissions, core.AllowTestPackages)
                    : null;
                JsonElement? resultEvidence = _result is null
                    ? null
                    : JsonSerializer.SerializeToElement(_result, ResultJson);
                return new LocalApiJournalEntry(
                    Id, CommandName, CommandDigest, ExpiresAt, staged.Path, Artifact,
                    Command.Serial, options, _stage, _consumed, _dispatched, _terminalAt,
                    resultEvidence, _mutationEvidence, _errorCode, _error);
            }
        }
    }
}

public sealed record LocalApiPreflightRequest(
    string ContractVersion,
    string Command,
    string ApkPath,
    string? Serial = null,
    LocalApiInstallOptions? InstallOptions = null,
    int? ExpiresInSeconds = null)
{
    public void Validate()
    {
        if (ContractVersion != LocalApiContract.Version)
            throw new LocalApiException("contract_unsupported", "The contract version is unsupported.");
        if (!LocalApiContract.Commands.Contains(Command, StringComparer.Ordinal))
            throw new LocalApiException("command_not_allowed", "The requested command is not implemented.");
        if (string.IsNullOrWhiteSpace(ApkPath))
            throw new LocalApiException("apk_path_required", "The local APK file is required.");
        if (Command == "apk.inspect" && (Serial is not null || InstallOptions is not null))
            throw new LocalApiException("fields_not_allowed", "Inspect accepts only the local APK field.");
        if (Command != "apk.install-inspected" && InstallOptions is not null)
            throw new LocalApiException("fields_not_allowed", "Install options are allowed only for inspected install.");
        if (InstallOptions?.AllowDowngrade == true)
            throw new LocalApiException("downgrade_not_allowed", "Downgrade is not allowed through the local API.");
        if (InstallOptions?.AllowTestPackages == true)
            throw new LocalApiException(
                "test_only_not_allowed",
                "Android test-only packages are not allowed through the local API.");
    }
}

public sealed record LocalApiInstallOptions(
    bool ReplaceExisting = true,
    bool AllowDowngrade = false,
    bool GrantRuntimePermissions = false,
    bool AllowTestPackages = false)
{
    public ApkInstallOptions ToCore() =>
        new(ReplaceExisting, AllowDowngrade, GrantRuntimePermissions, AllowTestPackages);
}

public sealed record LocalApiExecuteRequest(
    string ContractVersion,
    string OperationId,
    string CommandDigest)
{
    public void Validate()
    {
        if (ContractVersion != LocalApiContract.Version)
            throw new LocalApiException("contract_unsupported", "The contract version is unsupported.");
        if (string.IsNullOrWhiteSpace(OperationId) || string.IsNullOrWhiteSpace(CommandDigest))
            throw new LocalApiException("request_invalid", "Operation id and command digest are required.");
    }
}

public sealed record LocalApiOperationRequest(string ContractVersion, string OperationId)
{
    public void Validate()
    {
        if (ContractVersion != LocalApiContract.Version)
            throw new LocalApiException("contract_unsupported", "The contract version is unsupported.");
        if (string.IsNullOrWhiteSpace(OperationId))
            throw new LocalApiException("request_invalid", "Operation id is required.");
    }
}
