using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

// Foundation only: neither this candidate nor a successful callback is a public
// publication receipt. The retained registry must commit availability separately.
internal sealed record LocalApiDiagnosticOutputIntent
{
    internal const string PolicyVersion = "exact-installed-diagnostic-output.v1";
    internal const string LaunchPolicyVersion = "exact-installed-launch-diagnostic-output.v1";
    internal const int MaximumArmedCaptureBytes = 262144, PostActionWindowMilliseconds = 10000, MaximumLaunchOperationSeconds = 300;
    internal const string ArchiveFormat = "zip-store.v1";
    internal const int MaximumProcessLogs = 8, RecentLogLinesPerCapture = 400;
    internal const int MaximumPayloadFiles = 14, MaximumPayloadFileBytes = 262144;
    internal const long MaximumPayloadBytes = 3670016, MaximumArchiveBytes = 4194304;
    internal const long ReservedOutputBytes = 8388608;
    internal const int ReservedOutputFiles = 15;
    internal const int MaximumRequiredStandardOutputBytes = 262144, MaximumRequiredStandardErrorBytes = 262144;
    internal const int MaximumOptionalStandardOutputBytes = 262144, MaximumOptionalStandardErrorBytes = 262144;
    internal const long MaximumInputArtifactBytes = LocalApiStateLimits.DefaultMaximumSingleArtifactBytes;
    internal const int MaximumBinaryFramingBytes = 256, MaximumProcessTimeoutSeconds = 300;

    private LocalApiDiagnosticOutputIntent(string operationId, string outputId, bool isLaunch = false)
    {
        IsLaunch = isLaunch;
        OperationId = operationId; OutputId = outputId;
        PayloadLeaves = Array.AsReadOnly(Enumerable.Range(0, MaximumPayloadFiles).Select(i => $"d-{outputId[5..]}-{i:00}.bin").ToArray());
        ArchiveLeaf = $"d-{outputId[5..]}.zip";
        PlannedLeafDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "questionable.file_manager.diagnostic_output_plan.v1", operationId, outputId,
            payloadLeaves = PayloadLeaves, archiveLeaf = ArchiveLeaf, archiveFormat = ArchiveFormat,
            policyVersion = Policy, limits = Limits
        }))).ToLowerInvariant();
    }

    public string OperationId { get; }
    public string OutputId { get; }
    public IReadOnlyList<string> PayloadLeaves { get; }
    public string ArchiveLeaf { get; }
    public string PlannedLeafDigest { get; }
    internal bool IsLaunch { get; }
    [JsonPropertyName("policyVersion")] public string Policy => IsLaunch ? LaunchPolicyVersion : PolicyVersion;
    [JsonPropertyName("archiveFormat")] public string Format => ArchiveFormat;
    // Explicit ordered limits are part of the future v3 digest, including ALL
    // process retention caps. Never use the legacy post-collection fallback.
    public object Limits => IsLaunch ? new
    {
        maximumProcessLogs = MaximumProcessLogs, recentLogLinesPerCapture = RecentLogLinesPerCapture,
        maximumPayloadFiles = MaximumPayloadFiles, maximumPayloadFileBytes = MaximumPayloadFileBytes,
        maximumPayloadBytes = MaximumPayloadBytes, maximumArchiveBytes = MaximumArchiveBytes,
        reservedOutputBytes = ReservedOutputBytes, reservedOutputFiles = ReservedOutputFiles,
        maximumRequiredStandardOutputBytes = MaximumRequiredStandardOutputBytes,
        maximumRequiredStandardErrorBytes = MaximumRequiredStandardErrorBytes,
        maximumOptionalStandardOutputBytes = MaximumOptionalStandardOutputBytes,
        maximumOptionalStandardErrorBytes = MaximumOptionalStandardErrorBytes,
        maximumInputArtifactBytes = MaximumInputArtifactBytes,
        maximumBinaryFramingBytes = MaximumBinaryFramingBytes,
        maximumProcessTimeoutSeconds = MaximumProcessTimeoutSeconds,
        maximumArmedCaptureBytes = MaximumArmedCaptureBytes,
        postActionWindowMilliseconds = PostActionWindowMilliseconds,
        maximumLaunchOperationSeconds = MaximumLaunchOperationSeconds
    } : new
    {
        maximumProcessLogs = MaximumProcessLogs, recentLogLinesPerCapture = RecentLogLinesPerCapture,
        maximumPayloadFiles = MaximumPayloadFiles, maximumPayloadFileBytes = MaximumPayloadFileBytes,
        maximumPayloadBytes = MaximumPayloadBytes, maximumArchiveBytes = MaximumArchiveBytes,
        reservedOutputBytes = ReservedOutputBytes, reservedOutputFiles = ReservedOutputFiles,
        maximumRequiredStandardOutputBytes = MaximumRequiredStandardOutputBytes,
        maximumRequiredStandardErrorBytes = MaximumRequiredStandardErrorBytes,
        maximumOptionalStandardOutputBytes = MaximumOptionalStandardOutputBytes,
        maximumOptionalStandardErrorBytes = MaximumOptionalStandardErrorBytes,
        maximumInputArtifactBytes = MaximumInputArtifactBytes,
        maximumBinaryFramingBytes = MaximumBinaryFramingBytes,
        maximumProcessTimeoutSeconds = MaximumProcessTimeoutSeconds
    };

    internal static LocalApiDiagnosticOutputIntent Create(string operationId)
        => Create(operationId, false);

    internal static LocalApiDiagnosticOutputIntent CreateForLaunch(string operationId)
        => Create(operationId, true);

    private static LocalApiDiagnosticOutputIntent Create(string operationId, bool isLaunch)
    {
        if (operationId is null || !Regex.IsMatch(operationId, "\\Aapi-[0-9a-f]{32}\\z", RegexOptions.CultureInvariant) || operationId[4..] == new string('0', 32))
            throw Failure("diagnostic_operation_invalid");
        return new(operationId, "diag-" + Guid.NewGuid().ToString("N"), isLaunch);
    }

    // Rebuild only AFTER the existing journal authenticates the source values.
    // Recreating the plan does not adopt any file or permit publication.
    internal static LocalApiDiagnosticOutputIntent Restore(string operationId, string outputId, string expectedPlannedLeafDigest)
        => Restore(operationId, outputId, expectedPlannedLeafDigest, false);

    internal static LocalApiDiagnosticOutputIntent RestoreForLaunch(string operationId, string outputId, string expectedPlannedLeafDigest)
        => Restore(operationId, outputId, expectedPlannedLeafDigest, true);

    private static LocalApiDiagnosticOutputIntent Restore(string operationId, string outputId, string expectedPlannedLeafDigest, bool isLaunch)
    {
        if (operationId is null || outputId is null ||
            !Regex.IsMatch(operationId, "\\Aapi-[0-9a-f]{32}\\z", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(outputId, "\\Adiag-[0-9a-f]{32}\\z", RegexOptions.CultureInvariant) ||
            operationId[4..] == new string('0', 32) || outputId[5..] == new string('0', 32))
            throw Failure("diagnostic_operation_invalid");
        var intent = new LocalApiDiagnosticOutputIntent(operationId, outputId, isLaunch);
        if (intent.PlannedLeafDigest != expectedPlannedLeafDigest) throw Failure("diagnostic_plan_mismatch");
        return intent;
    }

    internal static LocalApiException Failure(string code) => new(code, "The owned diagnostic output could not be verified.");
}

internal readonly record struct LocalApiDiagnosticIdentity(uint VolumeSerialNumber, ulong FileIndex)
{
    internal static LocalApiDiagnosticIdentity From(FleetWindowsFileIdentity value) => new(value.VolumeSerialNumber, value.FileIndex);
}

internal sealed record LocalApiDiagnosticResourceSnapshot(
    string Role, int Slot, string Leaf, long MaximumBytes, string State,
    LocalApiDiagnosticIdentity? Identity = null, long? SizeBytes = null, string? Sha256 = null,
    string? EntryName = null, string? DebtCode = null);

internal sealed record LocalApiDiagnosticOutputSnapshot(
    LocalApiDiagnosticOutputIntent Intent, string Phase, IReadOnlyList<LocalApiDiagnosticResourceSnapshot> Resources);

internal sealed class LocalApiDiagnosticInputLease : IDisposable
{
    private FileStream? _stream;
    private Action? _release;
    private readonly Action? _verifyPath;
    internal LocalApiDiagnosticInputLease(string path, FileStream stream, LocalApiDiagnosticIdentity identity, Action release, Action? verifyPath = null)
    { Path = path; _stream = stream; Identity = identity; _release = release; _verifyPath = verifyPath; }
    internal string Path { get; }
    internal LocalApiDiagnosticIdentity Identity { get; }
    internal long SizeBytes => Stream.Length;
    internal async Task<string> HashAsync(CancellationToken token)
    {
        Verify(); Stream.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(Stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }
    private FileStream Stream => _stream ?? throw new ObjectDisposedException(nameof(LocalApiDiagnosticInputLease));
    internal void Verify()
    {
        _verifyPath?.Invoke();
        FleetWindowsFileSafety.ValidateFile(Stream.SafeFileHandle, Path, true);
        if (LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(Stream.SafeFileHandle)) != Identity)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_input_changed");
    }
    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

internal sealed class LocalApiDiagnosticSealedOutput
{
    internal LocalApiDiagnosticSealedOutput(string outputId, long sizeBytes, string sha256)
    { OutputId = outputId; SizeBytes = sizeBytes; Sha256 = sha256; }
    internal string OutputId { get; }
    internal long SizeBytes { get; }
    internal string Sha256 { get; }
}

internal sealed class LocalApiDiagnosticCandidateReadLease(FileStream stream, Action release) : IDisposable
{
    private Action? _release = release;
    internal Stream Stream => stream;
    public void Dispose() { stream.Dispose(); Interlocked.Exchange(ref _release, null)?.Invoke(); }
}

internal sealed class LocalApiDiagnosticLifetime
{
    private readonly object _gate = new();
    private bool _closing;
    private int _readers;
    private int _operations;
    internal Action AcquireRead()
    {
        lock (_gate)
        {
            if (_closing) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_owner_closed");
            _readers++;
        }
        return () => { lock (_gate) _readers--; };
    }
    internal Action AcquireOperation()
    {
        lock (_gate)
        {
            if (_closing) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_owner_closed");
            _operations++;
        }
        return () => { lock (_gate) _operations--; };
    }
    internal void BeginDispose()
    {
        lock (_gate)
        {
            if (_readers != 0) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_reads_active");
            if (_operations != 0) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_operations_active");
            _closing = true;
        }
    }
}

internal sealed class LocalApiDiagnosticUse(Action release) : IDisposable
{
    private Action? _release = release;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal sealed partial class LocalApiDiagnosticOutputReservation : IDisposable
{
    private readonly string _root;
    private LocalApiDiagnosticInputLease? _input;
    private LocalApiDiagnosticResourceSnapshot _inputSnapshot;
    private readonly long _inputSize;
    private readonly Func<LocalApiDiagnosticOutputSnapshot, CancellationToken, Task> _persist;
    private readonly Func<Action> _acquireRootRead;
    private readonly Func<Action> _acquireRootUse;
    private readonly Action _validateRoot;
    private readonly Action<string, string>? _fault;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Resource[] _resources;
    private string _phase = "reserved";
    private bool _faulted, _disposed;
    private bool _captureStarted;
    private int _activeReads;
    private volatile bool _released;
    private LocalApiDiagnosticSealedOutput? _candidate;
    private Resource? _recoveryWitness;
    private FileStream? _recoveredInputWitness;
    private LocalApiDiagnosticOutputSnapshot? _lastAcknowledged;

    internal LocalApiDiagnosticOutputReservation(string root, LocalApiDiagnosticOutputIntent intent,
        LocalApiDiagnosticInputLease input, Func<LocalApiDiagnosticOutputSnapshot, CancellationToken, Task> persist,
        Func<Action> acquireRootRead, Func<Action> acquireRootUse, Action validateRoot, Action<string, string>? fault)
    {
        _root = root; Intent = intent; _input = input; _inputSize = input.SizeBytes; _persist = persist;
        _inputSnapshot = new("input", -1, System.IO.Path.GetFileName(input.Path), input.SizeBytes,
            "owned", input.Identity, input.SizeBytes);
        _acquireRootRead = acquireRootRead; _validateRoot = validateRoot;
        _acquireRootUse = acquireRootUse;
        _fault = fault;
        _resources = intent.PayloadLeaves.Select((leaf, slot) => new Resource("payload", slot, leaf,
            LocalApiDiagnosticOutputIntent.MaximumPayloadFileBytes)).Append(new Resource("archive", 14,
                intent.ArchiveLeaf, LocalApiDiagnosticOutputIntent.MaximumArchiveBytes)).ToArray();
    }

    private LocalApiDiagnosticOutputReservation(string root, LocalApiDiagnosticOutputSnapshot snapshot,
        Func<LocalApiDiagnosticOutputSnapshot, CancellationToken, Task> persist, Func<Action> acquireRootRead, Func<Action> acquireRootUse, Action validateRoot)
    {
        ValidateRecovery(snapshot);
        _root = root; Intent = snapshot.Intent; _phase = snapshot.Phase;
        _inputSnapshot = snapshot.Resources.Single(r => r.Role == "input");
        _inputSize = _inputSnapshot.MaximumBytes;
        _persist = persist; _acquireRootRead = acquireRootRead; _validateRoot = validateRoot;
        _acquireRootUse = acquireRootUse;
        _faulted = true; // restoration NEVER grants capture, sealing, or replay
        _resources = snapshot.Resources.Where(r => r.Role != "input").OrderBy(r => r.Slot).Select(r => new Resource(r.Role, r.Slot, r.Leaf, r.MaximumBytes)
        { State = r.State, Identity = r.Identity, SizeBytes = r.SizeBytes, Sha256 = r.Sha256, EntryName = r.EntryName, DebtCode = r.DebtCode }).ToArray();
        _released = snapshot.Phase == "removed" && _resources.All(r => r.State is "planned" or "removed");
        _lastAcknowledged = Snapshot();
    }

    internal static LocalApiDiagnosticOutputReservation RestoreForCleanup(string root, LocalApiDiagnosticOutputSnapshot snapshot,
        Func<LocalApiDiagnosticOutputSnapshot, CancellationToken, Task> persist, Func<Action> acquireRootRead, Func<Action> acquireRootUse, Action validateRoot)
        => new(root, snapshot, persist, acquireRootRead, acquireRootUse, validateRoot);

    internal static void ValidateRecovery(LocalApiDiagnosticOutputSnapshot snapshot)
    {
        _ = snapshot.Intent.IsLaunch
            ? LocalApiDiagnosticOutputIntent.RestoreForLaunch(snapshot.Intent.OperationId, snapshot.Intent.OutputId, snapshot.Intent.PlannedLeafDigest)
            : LocalApiDiagnosticOutputIntent.Restore(snapshot.Intent.OperationId, snapshot.Intent.OutputId, snapshot.Intent.PlannedLeafDigest);
        if (snapshot.Phase is not ("reserved" or "capturing" or "sealing" or "sealed" or "available" or "outcomeUnknown" or "removed") ||
            snapshot.Resources.Count != 16 || snapshot.Resources.Select(r => r.Slot).Distinct().Count() != 16)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_invalid");
        foreach (var r in snapshot.Resources)
        {
            var validPlan = r.Role == "input" ? r.Slot == -1 && Regex.IsMatch(r.Leaf, "\\A[0-9a-f]{32}\\.apk\\z") && r.MaximumBytes is > 0 and <= LocalApiStateLimits.DefaultMaximumSingleArtifactBytes
                : r.Role == "payload" ? r.Slot is >= 0 and < 14 && r.Leaf == snapshot.Intent.PayloadLeaves[r.Slot] && r.MaximumBytes == LocalApiDiagnosticOutputIntent.MaximumPayloadFileBytes
                : r.Role == "archive" && r.Slot == 14 && r.Leaf == snapshot.Intent.ArchiveLeaf && r.MaximumBytes == LocalApiDiagnosticOutputIntent.MaximumArchiveBytes;
            if (!validPlan || r.State is not ("planned" or "createPending" or "owned" or "sealed" or "deletePending" or "removed" or "identityUnknown") ||
                (r.State is "owned" or "sealed" or "deletePending" && r.Identity is null) ||
                (r.State is "planned" or "createPending" or "identityUnknown" && r.Identity is not null) ||
                (r.State == "planned" && (r.SizeBytes is not null || r.Sha256 is not null || r.EntryName is not null)) ||
                (r.Role != "payload" && r.EntryName is not null) ||
                (r.SizeBytes is { } bytes && (bytes < 0 || bytes > r.MaximumBytes)) ||
                (r.Sha256 is not null && !Regex.IsMatch(r.Sha256, "\\A[0-9a-f]{64}\\z")) ||
                (r.State == "sealed" && (r.SizeBytes is null || r.Sha256 is null)))
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_invalid");
            if (r.Role == "payload" && r.EntryName is not null)
            {
                var kind = r.EntryName switch
                {
                    "runtime.json" => "runtime", "device.json" => "device", "package.txt" => "package",
                    "meminfo.txt" => "memory", "diagnostic-manifest.json" => "manifest",
                    "logcat-uid-post-fence.txt" => "launch_logcat", "launch-diagnostic-manifest.json" => "launch_manifest",
                    _ when r.EntryName.StartsWith("logcat-uid-", StringComparison.Ordinal) => "logcat_uid",
                    _ when r.EntryName.StartsWith("logcat-pid-", StringComparison.Ordinal) => "logcat_pid", _ => ""
                };
                ValidateEntry(snapshot.Intent.IsLaunch, kind, r.EntryName);
            }
        }
        var identities = snapshot.Resources.Where(r => r.Identity is not null).Select(r => r.Identity).ToArray();
        if (identities.Distinct().Count() != identities.Length ||
            (snapshot.Phase == "removed" && snapshot.Resources.Any(r => r.Role != "input" && r.State is not ("planned" or "removed"))) ||
            (snapshot.Phase is "available" or "sealed" && snapshot.Resources.Single(r => r.Role == "archive").State != "sealed"))
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_invalid");
    }

    // Called only after authenticated policy/plan validation. It pins a recorded
    // native object, not trusted payload content or public availability. Archive
    // download additionally performs independent descriptor SHA/size checks.
    internal void PinRecoveryWitness()
    {
        IEnumerable<LocalApiDiagnosticResourceSnapshot> candidates = new[] { _inputSnapshot }
            .Concat(_resources.OrderByDescending(r => r.Slot).Select(r => r.Snapshot()));
        foreach (var r in candidates.Where(r => r.Identity is not null && r.State is "owned" or "sealed" or "deletePending"))
        {
            FileStream? stream = null;
            try
            {
                var path = System.IO.Path.Combine(_root, r.Leaf);
                stream = FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(path);
                FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, path, true);
                if (LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(stream.SafeFileHandle)) != r.Identity)
                    throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_changed");
                if (r.Role == "input") _recoveredInputWitness = stream;
                else { _recoveryWitness = _resources.Single(resource => resource.Slot == r.Slot); _recoveryWitness.Stream = stream; }
                return;
            }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode is 2 or 3) { stream?.Dispose(); }
            catch (FileNotFoundException) { stream?.Dispose(); }
            catch { stream?.Dispose(); throw; }
        }
        // No recorded object remains at its leaf. Keep full capacity/debt, never
        // adopt a replacement or assert deletion. New input staging still uses
        // create-owned postvalidation even on an empty/idle guarded directory.
    }

    internal LocalApiDiagnosticOutputIntent Intent { get; }
    internal string ReportedInputPath => $"retained://{Intent.OperationId}/base.apk";
    internal string ReportedOutputPath => $"retained-output://{Intent.OperationId}/{Intent.OutputId}";
    internal string PlannedOutputPath => System.IO.Path.Combine(_root, "d-" + Intent.OutputId[5..]);
    internal IReadOnlyList<string> Leaves => Intent.PayloadLeaves.Append(Intent.ArchiveLeaf).ToArray();
    internal long ReservedBytes => _released ? 0 : LocalApiDiagnosticOutputIntent.ReservedOutputBytes;
    internal int ReservedFiles => _released ? 0 : LocalApiDiagnosticOutputIntent.ReservedOutputFiles;
    internal string InputLeaf => _inputSnapshot.Leaf;
    internal long MissingInputReservationBytes => _inputSnapshot.State is "planned" or "removed" ? 0 : _inputSize;
    internal LocalApiDiagnosticCaptureSink CreateSink() => new(this);
    internal LocalApiDiagnosticOutputSnapshot? LastAcknowledgedSnapshot => Volatile.Read(ref _lastAcknowledged);

    // Registry-only first-execution seam, never used during load/status and never
    // for an interrupted capture. The registry additionally binds the authenticated
    // Preflighted/ready/unconsumed/output-not-started original and immutable digest.
    // Success transfers this exact input loan into the existing reservation.
    internal async Task ActivateNeverStartedAsync(LocalApiDiagnosticInputLease input, string expectedSha256,
        DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_disposed || _released || !_faulted || _input is not null || _captureStarted || _candidate is not null ||
                _phase != "reserved" || now >= expiresAt || _activeReads != 0 ||
                _resources.Any(resource => resource.State != "planned" || resource.Identity is not null || resource.DebtCode is not null) ||
                _inputSnapshot.State is not ("owned" or "sealed") || _inputSnapshot.DebtCode is not null ||
                _inputSnapshot.Identity != input.Identity || input.SizeBytes != _inputSize ||
                input.Path != System.IO.Path.Combine(_root, _inputSnapshot.Leaf))
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_first_execution_invalid");
            input.Verify(); _validateRoot();
            if (await input.HashAsync(token).ConfigureAwait(false) != expectedSha256)
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_input_changed");
            input.Verify(); _validateRoot();
            _input = input; _faulted = false;
            _recoveredInputWitness?.Dispose(); _recoveredInputWitness = null;
        }
        finally { _gate.Release(); }
    }
    internal void VerifyInput(LocalApiDiagnosticInputLease input)
    {
        if (_input is null) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_output_closed");
        _input.Verify(); input.Verify();
        if (input.Identity != _input.Identity || input.Path != _input.Path || input.SizeBytes != _inputSize)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_input_changed");
    }

    internal async Task<LocalApiDiagnosticUse> BeginCaptureAsync(LocalApiDiagnosticInputLease input, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (_captureStarted || _phase != "reserved") throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_capture_consumed");
            VerifyInput(input);
            var use = new LocalApiDiagnosticUse(_acquireRootUse());
            _captureStarted = true;
            return use;
        }
        finally { _gate.Release(); }
    }
    internal LocalApiDiagnosticOutputSnapshot Snapshot() => new(Intent, _phase,
        Array.AsReadOnly(new[] { _inputSnapshot }.Concat(_resources.Select(r => r.Snapshot())).ToArray()));

    internal async Task CommitReservationAsync(CancellationToken token)
    { await PersistAsync(token).ConfigureAwait(false); }

    private async Task PersistAsync(CancellationToken token)
    {
        var snapshot = Snapshot();
        await _persist(snapshot, token).ConfigureAwait(false);
        Volatile.Write(ref _lastAcknowledged, snapshot);
    }

    internal async Task<ApkDiagnosticBundleFile> WritePayloadAsync(string kind, string entry, byte[] bytes,
        int? exitCode, string? observationSource, string? semantic, bool truncated, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        Action? releaseUse = null;
        try
        {
            releaseUse = _acquireRootUse();
            EnsureUsable(); ValidateEntry(Intent.IsLaunch, kind, entry);
            if (bytes.LongLength > LocalApiDiagnosticOutputIntent.MaximumPayloadFileBytes ||
                _resources.Where(r => r.Role == "payload").Sum(r => r.SizeBytes ?? 0) + bytes.LongLength > LocalApiDiagnosticOutputIntent.MaximumPayloadBytes)
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_payload_capacity");
            if (_resources.Any(r => r.EntryName == entry)) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_entry_duplicate");
            var resource = _resources.FirstOrDefault(r => r.Role == "payload" && r.State == "planned")
                ?? throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_file_capacity");
            _phase = "capturing";
            await CreateAsync(resource, entry, token).ConfigureAwait(false);
            await resource.Stream!.WriteAsync(bytes, token).ConfigureAwait(false);
            await SealFileAsync(resource, token).ConfigureAwait(false);
            return new(kind, entry, resource.SizeBytes!.Value, resource.Sha256!, exitCode)
            { ObservationSource = observationSource, CommandSemantic = semantic, Truncated = truncated };
        }
        catch { _faulted = true; throw; }
        finally { _gate.Release(); releaseUse?.Invoke(); }
    }

    private async Task CreateAsync(Resource resource, string? entry, CancellationToken token)
    {
        _input!.Verify(); _validateRoot();
        resource.EntryName = entry; resource.State = "createPending";
        await PersistAsync(token).ConfigureAwait(false); // durable permission BEFORE native create
        var path = System.IO.Path.Combine(_root, resource.Leaf);
        Microsoft.Win32.SafeHandles.SafeFileHandle? created = null;
        try
        {
            created = FleetWindowsFileSafety.CreateNewOwnedFileHandle(path);
            _fault?.Invoke("output-created", path);
            resource.Stream = new FileStream(created, FileAccess.ReadWrite, 65536, false);
            created = null;
            _validateRoot();
            FleetWindowsFileSafety.ValidateFile(resource.Stream.SafeFileHandle, path, true);
            resource.Identity = LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(resource.Stream.SafeFileHandle));
            resource.State = "owned";
            await PersistAsync(token).ConfigureAwait(false); // identity accepted BEFORE any write
        }
        catch
        {
            var handle = resource.Stream?.SafeFileHandle ?? created;
            if (handle is not null)
            {
                try { _fault?.Invoke("output-rollback", path); FleetWindowsFileSafety.MarkDelete(handle); resource.State = "removed"; }
                catch
                {
                    // Retain native ownership FIRST. Identity failure must not
                    // lose the create-owned handle or permit pathname cleanup.
                    resource.DebtCode = "diagnostic_create_cleanup_debt";
                    resource.CreatedHandleDebt = handle;
                    created = null;
                    try
                    {
                        _fault?.Invoke("output-identity", path);
                        resource.Identity ??= LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(handle));
                        resource.State = "owned";
                    }
                    catch { resource.State = resource.Identity is null ? "identityUnknown" : "owned"; }
                }
            }
            if (resource.CreatedHandleDebt is null)
            { resource.Stream?.Dispose(); resource.Stream = null; created?.Dispose(); }
            throw;
        }
    }

    private async Task SealFileAsync(Resource resource, CancellationToken token)
    {
        var stream = resource.Stream!;
        await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true);
        resource.SizeBytes = stream.Length;
        if (stream.Length > resource.MaximumBytes) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_payload_capacity");
        stream.Position = 0;
        resource.Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
        stream.Dispose(); resource.Stream = null;
        var path = System.IO.Path.Combine(_root, resource.Leaf);
        resource.Stream = FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(path);
        await VerifyResourceAsync(resource, token).ConfigureAwait(false);
        resource.State = "sealed";
        await PersistAsync(token).ConfigureAwait(false);
    }

    internal Task<LocalApiDiagnosticSealedOutput> SealAsync(ApkDiagnosticBundleResult result, CancellationToken token) =>
        SealCoreAsync(result.Artifact, false, payloads => ValidateManifest(payloads, result), token);

    private async Task<LocalApiDiagnosticSealedOutput> SealCoreAsync(ApkArtifactInspection artifact, bool isLaunch,
        Action<Resource[]> validateManifest, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        Action? releaseUse = null;
        try
        {
            releaseUse = _acquireRootUse();
            EnsureUsable(); _input!.Verify();
            if (Intent.IsLaunch != isLaunch) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_policy_mismatch");
            if (artifact.SizeBytes != _inputSize || artifact.Sha256 != await _input.HashAsync(token).ConfigureAwait(false))
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_input_changed");
            var payloads = _resources.Where(r => r.Role == "payload" && r.State == "sealed").ToArray();
            if (isLaunch)
                foreach (var resource in payloads) await VerifyResourceAsync(resource, token).ConfigureAwait(false);
            validateManifest(payloads);
            _phase = "sealing";
            var archive = _resources[^1];
            await CreateAsync(archive, null, token).ConfigureAwait(false);
            using (var bounded = new BoundedArchiveStream(archive.Stream!, archive.MaximumBytes))
            using (var zip = new ZipArchive(bounded, ZipArchiveMode.Create, true))
                foreach (var resource in payloads.OrderBy(r => r.EntryName, StringComparer.Ordinal))
                {
                    await VerifyResourceAsync(resource, token).ConfigureAwait(false);
                    var entry = zip.CreateEntry(resource.EntryName!, CompressionLevel.NoCompression);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var destination = entry.Open();
                    resource.Stream!.Position = 0;
                    await resource.Stream.CopyToAsync(destination, 65536, token).ConfigureAwait(false);
                }
            await SealFileAsync(archive, token).ConfigureAwait(false);
            // Sealed is intentionally NOT available. Only later registry journal
            // publication can grant external download authority.
            _phase = "sealed";
            await PersistAsync(token).ConfigureAwait(false);
            return _candidate = new(Intent.OutputId, archive.SizeBytes!.Value, archive.Sha256!);
        }
        catch { _faulted = true; throw; }
        finally { _gate.Release(); releaseUse?.Invoke(); }
    }

    private void ValidateManifest(Resource[] payloads, ApkDiagnosticBundleResult result)
    {
        if (payloads.Length is < 6 or > 14 || result.Artifact.Path != ReportedInputPath ||
            result.OutputDirectory != ReportedOutputPath || result.Files.Count != payloads.Length ||
            result.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() != result.Files.Count ||
            result.DiagnosticContract != "questionable.file_manager.apk_diagnostic_bundle.v3" ||
            result.Installed.Identity != result.Artifact.Identity || result.Installed.BaseApkSha256 != result.Artifact.Sha256 ||
            result.Installed.BaseApkSizeBytes != result.Artifact.SizeBytes || result.Runtime.Artifact != result.Artifact)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");
        foreach (var file in result.Files)
            if (!payloads.Any(r => r.EntryName == file.RelativePath && r.SizeBytes == file.SizeBytes && r.Sha256 == file.Sha256))
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");
        foreach (var entry in new[] { "runtime.json", "device.json", "package.txt", "meminfo.txt", "diagnostic-manifest.json" })
            if (!payloads.Any(r => r.EntryName == entry)) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");
        var manifest = payloads.Single(r => r.EntryName == "diagnostic-manifest.json");
        manifest.Stream!.Position = 0;
        using var json = JsonDocument.Parse(manifest.Stream);
        var root = json.RootElement;
        var decodedArtifact = root.GetProperty("artifact").Deserialize<ApkArtifactInspection>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (decodedArtifact != result.Artifact || root.GetProperty("schema").GetString() != "questionable.file_manager.apk_diagnostic_manifest.v3" ||
            root.GetProperty("diagnosticContract").GetString() != result.DiagnosticContract ||
            root.GetProperty("capturedAt").GetDateTimeOffset() != result.CapturedAt ||
            root.GetProperty("installed").GetProperty("serial").GetString() != result.Installed.Serial)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");
        var listed = root.GetProperty("files").EnumerateArray().ToArray();
        if (listed.Length != payloads.Length - 1 ||
            listed.Select(item => item.GetProperty("relativePath").GetString()).Distinct(StringComparer.Ordinal).Count() != listed.Length)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");
        foreach (var item in listed)
            if (!payloads.Any(r => r.EntryName != "diagnostic-manifest.json" &&
                r.EntryName == item.GetProperty("relativePath").GetString() && r.SizeBytes == item.GetProperty("sizeBytes").GetInt64() &&
                r.Sha256 == item.GetProperty("sha256").GetString()))
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");
        var uid = root.GetProperty("currentUserPackage").GetProperty("uid").GetInt32();
        var uidEntries = payloads.Where(r => r.EntryName!.StartsWith("logcat-uid-", StringComparison.Ordinal)).ToArray();
        var pids = result.Runtime.ProcessIds.Distinct().Order().Take(8).Select(pid => $"logcat-pid-{pid}.txt").ToHashSet(StringComparer.Ordinal);
        var pidEntries = payloads.Where(r => r.EntryName!.StartsWith("logcat-pid-", StringComparison.Ordinal)).Select(r => r.EntryName!).ToHashSet(StringComparer.Ordinal);
        if (uid <= 0 || uidEntries.Length != 1 || uidEntries[0].EntryName != $"logcat-uid-{uid}.txt" || !pids.SetEquals(pidEntries))
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");
    }

    private async Task VerifyResourceAsync(Resource resource, CancellationToken token)
    {
        var stream = resource.Stream ?? throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_unavailable");
        FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, System.IO.Path.Combine(_root, resource.Leaf), true);
        if (LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(stream.SafeFileHandle)) != resource.Identity || stream.Length != resource.SizeBytes)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_changed");
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
        if (hash != resource.Sha256) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_changed");
    }

    // This is internal candidate access, NOT public download admission. The
    // later registry must first acknowledge its durable available descriptor.
    internal async Task<LocalApiDiagnosticCandidateReadLease> OpenSealedCandidateAsync(long expectedSize,
        string expectedSha256, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_disposed || _released || _candidate is null || _candidate.SizeBytes != expectedSize || _candidate.Sha256 != expectedSha256)
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_output_unavailable");
            var archive = _resources[^1];
            await VerifyResourceAsync(archive, token).ConfigureAwait(false);
            var releaseRoot = _acquireRootRead();
            FileStream? stream = null;
            try
            {
                stream = FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(System.IO.Path.Combine(_root, archive.Leaf));
                FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, System.IO.Path.Combine(_root, archive.Leaf), true);
                if (LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(stream.SafeFileHandle)) != archive.Identity || stream.Length != expectedSize)
                    throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_changed");
                _validateRoot();
                Interlocked.Increment(ref _activeReads);
                return new(stream, () => { Interlocked.Decrement(ref _activeReads); releaseRoot(); });
            }
            catch { stream?.Dispose(); releaseRoot(); throw; }
        }
        finally { _gate.Release(); }
    }

    internal async Task<Action> PinAuthenticatedArchiveReadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_disposed || _released || _resources[^1].State != "sealed")
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_output_unavailable");
            var releaseRoot = _acquireRootRead();
            Interlocked.Increment(ref _activeReads);
            return () => { Interlocked.Decrement(ref _activeReads); releaseRoot(); };
        }
        finally { _gate.Release(); }
    }

    internal async Task<bool> CleanupAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        Action? releaseUse = null;
        try
        {
            releaseUse = _acquireRootUse();
            if (Volatile.Read(ref _activeReads) != 0) return false;
            _validateRoot();
            foreach (var resource in _resources.Where(r => r.State != "planned" && r.State != "removed"))
            {
                if (ReferenceEquals(resource, _recoveryWitness) && _resources.Any(other => !ReferenceEquals(other, resource) && other.State is not ("planned" or "removed")))
                { resource.DebtCode = "diagnostic_witness_retained"; continue; }
                if (resource.CreatedHandleDebt is not null)
                {
                    if (resource.Identity is not null) resource.State = "deletePending";
                    resource.DebtCode = "diagnostic_owned_handle_delete_pending";
                    await PersistAsync(token).ConfigureAwait(false);
                    try
                    {
                        _fault?.Invoke("output-delete", System.IO.Path.Combine(_root, resource.Leaf));
                        FleetWindowsFileSafety.MarkDelete(resource.CreatedHandleDebt);
                        resource.Stream?.Dispose(); resource.Stream = null;
                        resource.CreatedHandleDebt.Dispose(); resource.CreatedHandleDebt = null;
                        resource.State = "removed"; resource.DebtCode = null;
                    }
                    catch { resource.DebtCode = "diagnostic_cleanup_debt"; }
                    await PersistAsync(token).ConfigureAwait(false);
                    continue;
                }
                if (resource.Identity is null)
                {
                    if (LocalApiDiagnosticFileSafety.ProveAbsent(System.IO.Path.Combine(_root, resource.Leaf)))
                    { _validateRoot(); resource.State = "removed"; resource.DebtCode = null; }
                    else resource.DebtCode = "diagnostic_identity_unknown";
                    await PersistAsync(token).ConfigureAwait(false);
                    continue;
                }
                var previousState = resource.State;
                var persistenceAttempted = false;
                var deleteIntentAcknowledged = false;
                resource.Stream?.Dispose(); resource.Stream = null;
                try
                {
                    using var deletion = FleetWindowsFileSafety.OpenFileForDeletion(System.IO.Path.Combine(_root, resource.Leaf));
                    FleetWindowsFileSafety.ValidateFile(deletion.SafeFileHandle, System.IO.Path.Combine(_root, resource.Leaf), true);
                    if (LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(deletion.SafeFileHandle)) != resource.Identity)
                        throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_changed");
                    // A tombstone means a delete handle was ALREADY bound to
                    // this exact identity. Merely requesting cleanup is weaker.
                    resource.State = "deletePending";
                    persistenceAttempted = true;
                    await PersistAsync(token).ConfigureAwait(false);
                    deleteIntentAcknowledged = true;
                    _fault?.Invoke("output-delete", System.IO.Path.Combine(_root, resource.Leaf));
                    FleetWindowsFileSafety.MarkDelete(deletion.SafeFileHandle);
                    resource.State = "removed"; resource.DebtCode = null;
                }
                catch when (persistenceAttempted && !deleteIntentAcknowledged)
                {
                    resource.State = previousState; resource.DebtCode = "diagnostic_cleanup_persist_failed";
                    ReacquireWitness(resource);
                    throw;
                }
                catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode is 2 or 3)
                {
                    resource.State = previousState == "deletePending" ? "removed" : previousState;
                    resource.DebtCode = previousState == "deletePending" ? null : "diagnostic_missing_unexpected";
                }
                catch (FileNotFoundException)
                {
                    resource.State = previousState == "deletePending" ? "removed" : previousState;
                    resource.DebtCode = previousState == "deletePending" ? null : "diagnostic_missing_unexpected";
                }
                catch
                {
                    resource.DebtCode = "diagnostic_cleanup_debt";
                    ReacquireWitness(resource);
                }
                await PersistAsync(token).ConfigureAwait(false);
            }
            var done = _resources.All(r => r.State is "planned" or "removed");
            if (done) { _phase = "removed"; await PersistAsync(token).ConfigureAwait(false); _released = true; }
            return done;
        }
        finally { _gate.Release(); releaseUse?.Invoke(); }
    }

    private void ReacquireWitness(Resource resource)
    {
        if (!ReferenceEquals(resource, _recoveryWitness)) return;
        try
        {
            var path = System.IO.Path.Combine(_root, resource.Leaf);
            resource.Stream = FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(path);
            FleetWindowsFileSafety.ValidateFile(resource.Stream.SafeFileHandle, path, true);
            if (LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(resource.Stream.SafeFileHandle)) != resource.Identity)
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_changed");
        }
        catch { resource.Stream?.Dispose(); resource.Stream = null; resource.DebtCode = "diagnostic_witness_unavailable"; }
    }

    // Qualified owner policy (not this primitive) decides when destruction is
    // permitted. Input and output cleanup share this gate, so a later output
    // snapshot cannot resurrect an already-acknowledged removed input.
    internal async Task<bool> CleanupInputAsync(LocalApiStagedArtifact? staged,
        Func<LocalApiDiagnosticResourceSnapshot, CancellationToken, Task> persistInput, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(persistInput);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        Action? releaseUse = null;
        try
        {
            releaseUse = _acquireRootUse();
            if (_inputSnapshot.State == "removed") return true;
            if (staged is not null && (staged.Path != System.IO.Path.Combine(_root, _inputSnapshot.Leaf) || staged.DiagnosticIdentity != _inputSnapshot.Identity))
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_input_changed");
            var archive = _resources[^1];
            if (!_released)
            {
                if (archive.State != "sealed" || archive.Stream is null)
                    throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_witness_unavailable");
                await VerifyResourceAsync(archive, token).ConfigureAwait(false);
                _validateRoot();
                _recoveryWitness = archive; // transfer BEFORE either input witness closes
            }
            _faulted = true; // releasing input can never permit another capture
            _input?.Dispose(); _recoveredInputWitness?.Dispose(); _recoveredInputWitness = null;
            staged?.Dispose();
            _validateRoot();
            var path = System.IO.Path.Combine(_root, _inputSnapshot.Leaf);
            async Task Acknowledge(LocalApiDiagnosticResourceSnapshot candidate)
            {
                await persistInput(candidate, token).ConfigureAwait(false);
                _inputSnapshot = candidate;
                var acknowledged = LastAcknowledgedSnapshot;
                if (acknowledged is not null)
                    Volatile.Write(ref _lastAcknowledged, acknowledged with { Resources = Array.AsReadOnly(
                        acknowledged.Resources.Select(resource => resource.Role == "input" ? candidate : resource).ToArray()) });
            }
            if (_inputSnapshot.Identity is null)
            {
                if (!LocalApiDiagnosticFileSafety.ProveAbsent(path)) return false;
                _validateRoot();
                await Acknowledge(_inputSnapshot with { State = "removed", DebtCode = null }).ConfigureAwait(false);
                return true;
            }
            var previous = _inputSnapshot;
            var persistenceAttempted = false;
            var intentAcknowledged = false;
            try
            {
                using (var deletion = FleetWindowsFileSafety.OpenFileForDeletion(path))
                {
                    FleetWindowsFileSafety.ValidateFile(deletion.SafeFileHandle, path, true);
                    if (LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(deletion.SafeFileHandle)) != previous.Identity)
                        throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_input_changed");
                    persistenceAttempted = true;
                    await Acknowledge(previous with { State = "deletePending", DebtCode = null }).ConfigureAwait(false);
                    intentAcknowledged = true;
                    _fault?.Invoke("input-delete", path);
                    FleetWindowsFileSafety.MarkDelete(deletion.SafeFileHandle);
                }
            }
            catch when (persistenceAttempted && !intentAcknowledged) { throw; }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode is 2 or 3)
            {
                if (previous.State != "deletePending")
                { await Acknowledge(previous with { DebtCode = "diagnostic_missing_unexpected" }).ConfigureAwait(false); return false; }
            }
            catch (FileNotFoundException)
            {
                if (previous.State != "deletePending")
                { await Acknowledge(previous with { DebtCode = "diagnostic_missing_unexpected" }).ConfigureAwait(false); return false; }
            }
            catch
            {
                await Acknowledge(_inputSnapshot with { DebtCode = "diagnostic_cleanup_debt" }).ConfigureAwait(false);
                return false;
            }
            await Acknowledge(_inputSnapshot with { State = "removed", DebtCode = null }).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); releaseUse?.Invoke(); }
    }

    private static void ValidateEntry(bool isLaunch, string kind, string entry)
    {
        var valid = isLaunch ? kind switch
        {
            "launch_logcat" => entry == "logcat-uid-post-fence.txt",
            "launch_manifest" => entry == "launch-diagnostic-manifest.json", _ => false
        } : kind switch
        {
            "runtime" => entry == "runtime.json", "device" => entry == "device.json",
            "package" => entry == "package.txt", "memory" => entry == "meminfo.txt",
            "manifest" => entry == "diagnostic-manifest.json",
            "logcat_uid" => ValidNumericEntry(entry, "logcat-uid-"),
            "logcat_pid" => ValidNumericEntry(entry, "logcat-pid-"), _ => false
        };
        if (!valid) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_entry_invalid");
    }

    private static bool ValidNumericEntry(string entry, string prefix) =>
        Regex.IsMatch(entry, "\\A" + prefix + "[1-9][0-9]{0,9}\\.txt\\z", RegexOptions.CultureInvariant) &&
        int.TryParse(entry.AsSpan(prefix.Length, entry.Length - prefix.Length - 4),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0;

    private void EnsureUsable()
    { if (_disposed || _released || _faulted || _candidate is not null) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_output_closed"); }
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            foreach (var resource in _resources) resource.Stream?.Dispose();
            foreach (var resource in _resources) resource.CreatedHandleDebt?.Dispose();
            _input?.Dispose(); // debt/files are retained; disposal never deletes evidence
            _recoveredInputWitness?.Dispose();
        }
        finally { _gate.Release(); }
    }

    private sealed class Resource(string role, int slot, string leaf, long maximumBytes)
    {
        internal string Role = role, Leaf = leaf, State = "planned";
        internal int Slot = slot;
        internal long MaximumBytes = maximumBytes;
        internal LocalApiDiagnosticIdentity? Identity;
        internal long? SizeBytes;
        internal string? Sha256, EntryName, DebtCode;
        internal FileStream? Stream;
        internal Microsoft.Win32.SafeHandles.SafeFileHandle? CreatedHandleDebt;
        internal LocalApiDiagnosticResourceSnapshot Snapshot() => new(Role, Slot, Leaf, MaximumBytes, State, Identity, SizeBytes, Sha256, EntryName, DebtCode);
    }

    private sealed class BoundedArchiveStream(Stream inner, long maximum) : Stream
    {
        private long _written;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => _written; public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximum - _written) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_archive_capacity");
            inner.Write(buffer); _written += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed class LocalApiDiagnosticCaptureSink(LocalApiDiagnosticOutputReservation reservation)
{
    internal LocalApiDiagnosticOutputReservation Reservation => reservation;
    internal Task<ApkDiagnosticBundleFile> WriteAsync(string kind, string entry, byte[] bytes, int? exitCode,
        string? source, string? semantic, bool truncated, CancellationToken token) =>
        reservation.WritePayloadAsync(kind, entry, bytes, exitCode, source, semantic, truncated, token);
}
