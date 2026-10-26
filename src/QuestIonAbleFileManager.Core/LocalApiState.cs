using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace QuestIonAbleFileManager.Core;

public sealed record LocalApiStateLimits(
    int MaximumRetainedOperations = 256,
    int MaximumRunningOperations = 4,
    long MaximumStagedBytes = LocalApiStateLimits.DefaultMaximumStagedBytes,
    int MaximumStagedFiles = LocalApiStateLimits.DefaultMaximumStagedFiles,
    int MaximumResultBytes = 64 * 1024,
    int MaximumOutputCharacters = 4 * 1024,
    long MaximumJournalBytes = 4L * 1024 * 1024,
    TimeSpan? TerminalRetention = null,
    long MaximumSingleArtifactBytes = LocalApiStateLimits.DefaultMaximumSingleArtifactBytes)
{
    public const long DefaultMaximumSingleArtifactBytes = 1024L * 1024 * 1024;
    public const long DefaultMaximumStagedBytes = 2L * 1024 * 1024 * 1024;
    public const int DefaultMaximumStagedFiles = 256;

    public TimeSpan EffectiveTerminalRetention => TerminalRetention ?? TimeSpan.FromHours(24);
    public long EffectiveMaximumSingleArtifactBytes =>
        Math.Min(MaximumSingleArtifactBytes, MaximumStagedBytes);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumRetainedOperations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumRunningOperations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumStagedBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumStagedBytes, DefaultMaximumStagedBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumStagedFiles, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumStagedFiles, DefaultMaximumStagedFiles);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumResultBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumOutputCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumJournalBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumSingleArtifactBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            MaximumSingleArtifactBytes,
            DefaultMaximumSingleArtifactBytes);
    }
}

public sealed record LocalApiStateSettings(
    string StateDirectory,
    byte[] JournalIntegrityKey,
    LocalApiStateLimits Limits)
{
    internal Func<string, bool>? JournalFault { get; init; }
    internal Func<string, bool>? CleanupFault { get; init; }
    internal Action<string, string>? DiagnosticFault { get; init; }

    public const string StateDirectoryEnvironmentVariable = "QUESTIONABLE_FILE_MANAGER_API_STATE";
    public const string JournalSecretEnvironmentVariable = "QUESTIONABLE_FILE_MANAGER_API_JOURNAL_SECRET";

    public static LocalApiStateSettings FromEnvironment(
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var directory = readEnvironment(StateDirectoryEnvironmentVariable);
        var secret = readEnvironment(JournalSecretEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
            throw new LocalApiException("state_missing", $"Set {StateDirectoryEnvironmentVariable}.");
        if (string.IsNullOrWhiteSpace(secret))
            throw new LocalApiException("journal_secret_missing", $"Set {JournalSecretEnvironmentVariable}.");
        var secretBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        if (secretBytes.Length is < 32 or > 512)
            throw new LocalApiException(
                "journal_secret_invalid",
                "The journal integrity secret must be 32..512 UTF-8 bytes.");
        var key = System.Security.Cryptography.SHA256.HashData(
            secretBytes);
        return new LocalApiStateSettings(Path.GetFullPath(directory), key, new LocalApiStateLimits());
    }

    internal static LocalApiStateSettings CreateForTests(string directory, LocalApiStateLimits? limits = null) =>
        new(Path.GetFullPath(directory), new byte[32], limits ?? new LocalApiStateLimits());
}

internal sealed class LocalApiStagedArtifact : IDisposable
{
    private readonly FileStream _retainedHandle;
    private readonly FleetWindowsFileIdentity _identity;
    private readonly object _borrowGate = new();
    private int _diagnosticBorrows;
    private bool _disposeRequested;

    public LocalApiStagedArtifact(
        string path,
        long sizeBytes,
        FileStream retainedHandle,
        FleetWindowsFileIdentity identity)
    {
        Path = path;
        SizeBytes = sizeBytes;
        _retainedHandle = retainedHandle;
        _identity = identity;
    }

    public string Path { get; }
    public long SizeBytes { get; }
    internal LocalApiDiagnosticIdentity DiagnosticIdentity => LocalApiDiagnosticIdentity.From(_identity);
    public void Dispose()
    {
        lock (_borrowGate)
        {
            _disposeRequested = true;
            if (_diagnosticBorrows == 0) _retainedHandle.Dispose();
        }
    }

    internal LocalApiDiagnosticInputLease BorrowDiagnosticInput()
    {
        lock (_borrowGate)
        {
            if (_disposeRequested) throw new ObjectDisposedException(nameof(LocalApiStagedArtifact));
            var stream = FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(Path);
            SafeFileHandle? rootGuard = null;
            LocalApiDiagnosticInputLease? pathCustody = null;
            try
            {
                FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, Path, true);
                if (FleetWindowsFileSafety.GetIdentity(stream.SafeFileHandle) != _identity)
                    throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_input_changed");
                // A loan may outlive its stager. It owns its own path chain and
                // private root guard, rather than borrowing the stager's locks.
                pathCustody = LocalApiDiagnosticFileSafety.BorrowSource(Path);
                rootGuard = LocalApiDiagnosticFileSafety.OpenGuard(System.IO.Path.GetDirectoryName(Path)!);
                _diagnosticBorrows++;
                return new(Path, stream, LocalApiDiagnosticIdentity.From(_identity), () =>
                {
                    rootGuard.Dispose(); pathCustody.Dispose();
                    lock (_borrowGate)
                    {
                        _diagnosticBorrows--;
                        if (_disposeRequested && _diagnosticBorrows == 0) _retainedHandle.Dispose();
                    }
                }, () =>
                {
                    pathCustody.Verify();
                    LocalApiDiagnosticFileSafety.ValidateGuard(rootGuard, System.IO.Path.GetDirectoryName(Path)!);
                });
            }
            catch { stream.Dispose(); rootGuard?.Dispose(); pathCustody?.Dispose(); throw; }
        }
    }

    public bool TryDelete(out string? error)
        => TryDelete(out error, out _);

    internal bool TryDelete(out string? error, out bool sharingProtected)
    {
        lock (_borrowGate)
        {
            if (_diagnosticBorrows != 0)
            { error = "Owned diagnostic input is leased."; sharingProtected = true; return false; }
            _disposeRequested = true;
        }
        sharingProtected = false;
        try
        {
            _retainedHandle.Dispose();
            using var deletion = FleetWindowsFileSafety.OpenFileForDeletion(Path);
            FleetWindowsFileSafety.ValidateFile(deletion.SafeFileHandle, Path, requireSingleLink: true);
            if (FleetWindowsFileSafety.GetIdentity(deletion.SafeFileHandle) != _identity)
                throw new LocalApiException(
                    "staged_artifact_changed",
                    "The staged artifact identity changed before cleanup.");
            FleetWindowsFileSafety.MarkDelete(deletion.SafeFileHandle);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            sharingProtected = exception is Win32Exception { NativeErrorCode: 32 or 33 };
            _retainedHandle.Dispose();
            error = exception.Message;
            return false;
        }
    }
}

internal sealed class LocalApiArtifactStager : IDisposable
{
    private readonly string _stageDirectory;
    private readonly LocalApiStateLimits _limits;
    private readonly List<SafeFileHandle> _directoryHandles = [];
    private readonly FileStream _ownerLease;
    private readonly SemaphoreSlim _stageGate = new(1, 1);
    private readonly List<LocalApiDiagnosticOutputReservation> _diagnosticOutputs = [];
    private readonly object _diagnosticCapacityGate = new();
    private SafeFileHandle? _diagnosticRootGuard;
    private bool _diagnosticInventoryEnabled;
    private readonly LocalApiDiagnosticLifetime _diagnosticLifetime = new();
    private volatile bool _disposed;
    private readonly List<(SafeFileHandle Handle, FileStream? Stream, LocalApiDiagnosticResourceSnapshot Resource)> _diagnosticCreationDebts = [];
    private readonly Action<string, string>? _diagnosticFault;
    private readonly Dictionary<string, long> _pendingDiagnosticInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _diagnosticDebtGate = new(1, 1);

    public LocalApiArtifactStager(LocalApiStateSettings settings)
    {
        settings.Limits.Validate();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The local API secure state boundary requires Windows.");
        if (!Path.IsPathFullyQualified(settings.StateDirectory) ||
            settings.StateDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new LocalApiException("state_root_invalid", "The API state root must be a local absolute Windows path.");
        var root = Path.GetPathRoot(settings.StateDirectory)
            ?? throw new LocalApiException("state_root_invalid", "The API state root is invalid.");
        if (new DriveInfo(root).DriveType is DriveType.Network or DriveType.NoRootDirectory)
            throw new LocalApiException("state_root_nonlocal", "The API state root must be on a local drive.");
        _stageDirectory = Path.Combine(settings.StateDirectory, "staged");
        _limits = settings.Limits;
        _diagnosticFault = settings.DiagnosticFault;
        CreateOrValidatePrivateDirectory(settings.StateDirectory);
        CreateOrValidatePrivateDirectory(_stageDirectory);
        _ownerLease = AcquireOwnerLease(
            Path.Combine(settings.StateDirectory, "api.owner.lock"));
        FleetWindowsFileSafety.ValidateFile(
            _ownerLease.SafeFileHandle,
            Path.Combine(settings.StateDirectory, "api.owner.lock"),
            requireSingleLink: true);
        RetainAncestorHandles(settings.StateDirectory);
        var stagedHandle = FleetWindowsFileSafety.OpenDirectory(_stageDirectory, allowDelete: false);
        FleetWindowsFileSafety.ValidateDirectory(stagedHandle, _stageDirectory);
        _directoryHandles.Add(stagedHandle);
    }

    public async Task<LocalApiStagedArtifact> StageAsync(
        string sourcePath,
        CancellationToken cancellationToken,
        Func<long, bool>? reserveBytes = null)
    {
        await _stageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await StageCoreAsync(sourcePath, cancellationToken, reserveBytes).ConfigureAwait(false);
        }
        finally
        {
            _stageGate.Release();
        }
    }

    private async Task<LocalApiStagedArtifact> StageCoreAsync(
        string sourcePath,
        CancellationToken cancellationToken,
        Func<long, bool>? reserveBytes,
        bool diagnostic = false,
        Func<LocalApiDiagnosticResourceSnapshot, CancellationToken, Task>? persistDiagnosticInput = null)
    {
        var fullSource = Path.GetFullPath(sourcePath);
        var protectedCreation = diagnostic || _diagnosticInventoryEnabled;

        var inventory = GetInventory();
        if (inventory.FileCount >= _limits.MaximumStagedFiles)
            throw new LocalApiException("staged_file_capacity", "The staged-file capacity is exhausted.");
        var stagedBytes = inventory.SizeBytes;

        await using var source = FleetWindowsFileSafety.OpenReadOnlyFile(fullSource);
        FleetWindowsFileSafety.ValidateFile(source.SafeFileHandle, fullSource, requireSingleLink: true);
        var before = FleetWindowsFileSafety.GetIdentity(source.SafeFileHandle);
        if (before.NumberOfLinks != 1)
            throw new LocalApiException("source_hardlink_rejected", "The APK source must have exactly one hard link.");
        if (source.Length <= 0)
            throw new LocalApiException("staged_artifact_invalid", "The staged APK must contain at least one byte.");
        var availableBytes = stagedBytes >= _limits.MaximumStagedBytes
            ? 0
            : _limits.MaximumStagedBytes - stagedBytes;
        if (source.Length > _limits.EffectiveMaximumSingleArtifactBytes)
            throw Capacity("staged_artifact_capacity", source.Length,
                Math.Min(_limits.EffectiveMaximumSingleArtifactBytes, availableBytes),
                _limits.EffectiveMaximumSingleArtifactBytes);
        if (source.Length > availableBytes)
            throw Capacity("staged_byte_capacity", source.Length, availableBytes,
                _limits.MaximumStagedBytes);
        if (reserveBytes is not null && !reserveBytes(source.Length))
            throw Capacity("staged_byte_capacity", source.Length, availableBytes,
                _limits.MaximumStagedBytes);

        var stagedPath = Path.Combine(_stageDirectory, Guid.NewGuid().ToString("N") + ".apk");
        LocalApiDiagnosticResourceSnapshot? diagnosticInput = diagnostic ? new("input", -1,
            Path.GetFileName(stagedPath), source.Length, "createPending") : null;
        if (diagnostic) lock (_diagnosticCapacityGate) _pendingDiagnosticInputs.Add(stagedPath, source.Length);
        FileStream? staged = null;
        SafeFileHandle? created = null;
        FleetWindowsFileIdentity? stagedIdentity = null;
        try
        {
            if (diagnostic) await persistDiagnosticInput!(diagnosticInput!, cancellationToken).ConfigureAwait(false);
            if (protectedCreation)
            {
                created = FleetWindowsFileSafety.CreateNewOwnedFileHandle(stagedPath);
                _diagnosticFault?.Invoke("input-created", stagedPath);
                staged = new FileStream(created, FileAccess.ReadWrite, 65536, false);
                created = null;
            }
            else staged = FleetWindowsFileSafety.CreateNewRetainedReadableFile(stagedPath);
            // An empty root can accept a metadata-only junction transition.
            // No input byte may be copied until the newly created exact handle
            // and the still-original guarded root have both been verified.
            if (protectedCreation) LocalApiDiagnosticFileSafety.ValidateGuard(_diagnosticRootGuard!, _stageDirectory);
            FleetWindowsFileSafety.ValidateFile(staged.SafeFileHandle, stagedPath, requireSingleLink: true);
            if (diagnostic)
            {
                diagnosticInput = diagnosticInput! with { State = "owned",
                    Identity = LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(staged.SafeFileHandle)) };
                await persistDiagnosticInput!(diagnosticInput, cancellationToken).ConfigureAwait(false);
            }
            await source.CopyToAsync(staged, 64 * 1024, cancellationToken).ConfigureAwait(false);
            await staged.FlushAsync(cancellationToken).ConfigureAwait(false);
            staged.Flush(flushToDisk: true);
            var after = FleetWindowsFileSafety.GetIdentity(source.SafeFileHandle);
            if (before != after || staged.Length != source.Length)
                throw new LocalApiException("source_changed", "The APK source changed while it was staged.");
            FleetWindowsFileSafety.ValidateFile(staged.SafeFileHandle, stagedPath, requireSingleLink: true);
            stagedIdentity = FleetWindowsFileSafety.GetIdentity(staged.SafeFileHandle);
            var stagedLength = staged.Length;
            staged.Dispose();
            staged = null;
            var retained = FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(stagedPath);
            try
            {
            FleetWindowsFileSafety.ValidateFile(retained.SafeFileHandle, stagedPath, requireSingleLink: true);
            if (FleetWindowsFileSafety.GetIdentity(retained.SafeFileHandle) != stagedIdentity.Value)
            {
                retained.Dispose();
                throw new LocalApiException(
                    "staged_artifact_changed",
                    "The staged artifact identity changed while its immutable handle was retained.");
            }
            if (protectedCreation) LocalApiDiagnosticFileSafety.ValidateGuard(_diagnosticRootGuard!, _stageDirectory);
            return new LocalApiStagedArtifact(
                stagedPath,
                stagedLength,
                retained,
                stagedIdentity.Value);
            }
            catch { retained.Dispose(); throw; }
        }
        catch
        {
            if (staged is not null || created is not null)
            {
                if (protectedCreation)
                {
                    // Roll back OUR create-owned handle even if an ancestor was
                    // swapped. Never open a guessed foreign path for rollback.
                    var handle = staged?.SafeFileHandle ?? created!;
                    try { _diagnosticFault?.Invoke("input-rollback", stagedPath); FleetWindowsFileSafety.MarkDelete(handle); }
                    catch
                    {
                        var debt = (diagnosticInput ?? new("input", -1, Path.GetFileName(stagedPath), source.Length, "identityUnknown")) with
                        { State = "identityUnknown", DebtCode = "diagnostic_create_cleanup_debt" };
                        int debtIndex;
                        lock (_diagnosticCapacityGate)
                        {
                            _pendingDiagnosticInputs.Remove(stagedPath);
                            debtIndex = _diagnosticCreationDebts.Count;
                            _diagnosticCreationDebts.Add((handle, staged, debt));
                        }
                        staged = null; created = null; // retain exact native ownership, including a failed prevalidation create
                        try
                        {
                            _diagnosticFault?.Invoke("input-identity", stagedPath);
                            var identity = LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(handle));
                            lock (_diagnosticCapacityGate)
                            {
                                var owned = _diagnosticCreationDebts[debtIndex];
                                _diagnosticCreationDebts[debtIndex] = (owned.Handle, owned.Stream, owned.Resource with { Identity = identity, State = "owned" });
                            }
                        }
                        catch { /* Exact create-owned handle debt is already retained. */ }
                    }
                }
                staged?.Dispose();
                created?.Dispose();
            }
            if (protectedCreation) throw;
            try
            {
                if (File.Exists(stagedPath))
                {
                    using var deletion = FleetWindowsFileSafety.OpenFileForDeletion(stagedPath);
                    FleetWindowsFileSafety.ValidateFile(
                        deletion.SafeFileHandle,
                        stagedPath,
                        requireSingleLink: true);
                    if (stagedIdentity is null ||
                        FleetWindowsFileSafety.GetIdentity(deletion.SafeFileHandle) == stagedIdentity.Value)
                    {
                        FleetWindowsFileSafety.MarkDelete(deletion.SafeFileHandle);
                    }
                }
            }
            catch
            {
                // The original staging failure remains authoritative.
            }
            throw;
        }
        finally { if (diagnostic) lock (_diagnosticCapacityGate) _pendingDiagnosticInputs.Remove(stagedPath); }
    }

    public LocalApiStageInventory GetInventory()
    {
        if (_diagnosticInventoryEnabled) LocalApiDiagnosticFileSafety.ValidateGuard(_diagnosticRootGuard!, _stageDirectory);
        if (_diagnosticInventoryEnabled && Directory.EnumerateDirectories(_stageDirectory).Any())
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_inventory_unknown");
        var paths = Directory.EnumerateFiles(_stageDirectory, _diagnosticInventoryEnabled ? "*" : "*.apk")
            .Take(_limits.MaximumStagedFiles + 1)
            .ToArray();
        long bytes = 0;
        var measuredLengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            // Attribute reads also account for create-owned writers which deny
            // a second data read handle. Custody is verified by the reservation.
            long length;
            if (!_diagnosticInventoryEnabled)
            {
                using var stream = FleetWindowsFileSafety.OpenReadOnlyFile(path);
                FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, path, requireSingleLink: true);
                length = stream.Length;
            }
            else
            {
                length = LocalApiDiagnosticFileSafety.ReadInventoryLength(path);
            }
            measuredLengths.Add(path, length);
            _diagnosticFault?.Invoke("inventory-measured", path);
            if (length > _limits.MaximumStagedBytes - bytes)
            {
                bytes = _limits.MaximumStagedBytes;
            }
            else
            {
                bytes += length;
            }
        }
        var files = paths.Length;
        lock (_diagnosticCapacityGate)
        {
            foreach (var reservation in _diagnosticOutputs)
            {
                var materialized = paths.Where(path => reservation.Leaves.Contains(Path.GetFileName(path), StringComparer.Ordinal)).ToArray();
                var actualBytes = materialized.Sum(path => measuredLengths[path]);
                bytes = Math.Min(_limits.MaximumStagedBytes, bytes + Math.Max(0, reservation.ReservedBytes - actualBytes));
                files += Math.Max(0, reservation.ReservedFiles - materialized.Length);
                if (!paths.Any(path => string.Equals(Path.GetFileName(path), reservation.InputLeaf, StringComparison.Ordinal)) &&
                    reservation.MissingInputReservationBytes > 0)
                {
                    bytes = Math.Min(_limits.MaximumStagedBytes, bytes + reservation.MissingInputReservationBytes);
                    files++;
                }
            }
            foreach (var debt in _diagnosticCreationDebts)
            {
                var materialized = paths.FirstOrDefault(path => string.Equals(Path.GetFileName(path), debt.Resource.Leaf, StringComparison.Ordinal));
                bytes = Math.Min(_limits.MaximumStagedBytes, bytes + Math.Max(0, debt.Resource.MaximumBytes - (materialized is null ? 0 : measuredLengths[materialized])));
                if (materialized is null) files++;
            }
            foreach (var pending in _pendingDiagnosticInputs)
            {
                var materialized = measuredLengths.TryGetValue(pending.Key, out var observed);
                bytes = Math.Min(_limits.MaximumStagedBytes, bytes + Math.Max(0, pending.Value - (materialized ? observed : 0)));
                if (!materialized) files++;
            }
        }
        return new LocalApiStageInventory(files, bytes);
    }

    // Registry must authenticate and validate ALL versions/policies first, then
    // restore these capacity promises BEFORE legacy preflight/orphan cleanup.
    // Empty recovery preserves legacy mode. Nonempty authenticated v3 recovery
    // activates all-kind accounting; a fresh diagnostic staging admission does
    // so itself before checking capacity. Empty recovery adds no NTFS gate.
    // This stores no new authority: only the existing owner's immutable resource
    // snapshots and quota reservations, and never permits replay/publication.
    internal async Task<IReadOnlyList<LocalApiDiagnosticOutputReservation>> RegisterDiagnosticRecoveryAsync(
        IReadOnlyList<LocalApiDiagnosticOutputSnapshot> authenticatedSnapshots,
        Func<LocalApiDiagnosticOutputSnapshot, CancellationToken, Task> persist,
        CancellationToken cancellationToken = default)
    {
        if (authenticatedSnapshots.Count > _limits.MaximumRetainedOperations)
            throw LocalApiDiagnosticOutputIntent.Failure("operation_capacity");
        foreach (var snapshot in authenticatedSnapshots) LocalApiDiagnosticOutputReservation.ValidateRecovery(snapshot);
        if (authenticatedSnapshots.Select(s => s.Intent.OutputId).Distinct(StringComparer.Ordinal).Count() != authenticatedSnapshots.Count)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_invalid");
        var leaves = authenticatedSnapshots.SelectMany(snapshot => snapshot.Resources.Select(resource => resource.Leaf)).ToArray();
        if (leaves.Distinct(StringComparer.OrdinalIgnoreCase).Count() != leaves.Length)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_invalid");
        var restored = new List<LocalApiDiagnosticOutputReservation>();
        await _stageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_diagnosticCapacityGate)
                if (_diagnosticOutputs.Count != 0) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_recovery_order_invalid");
            foreach (var snapshot in authenticatedSnapshots)
            {
                var reservation = LocalApiDiagnosticOutputReservation.RestoreForCleanup(_stageDirectory, snapshot, persist,
                    _diagnosticLifetime.AcquireRead, _diagnosticLifetime.AcquireOperation,
                    () => LocalApiDiagnosticFileSafety.ValidateGuard(_diagnosticRootGuard!, _stageDirectory));
                restored.Add(reservation);
                reservation.PinRecoveryWitness();
            }
            if (restored.Count != 0)
            {
                _diagnosticRootGuard ??= LocalApiDiagnosticFileSafety.OpenGuard(_stageDirectory);
                LocalApiDiagnosticFileSafety.ValidateGuard(_diagnosticRootGuard, _stageDirectory);
                lock (_diagnosticCapacityGate) _diagnosticOutputs.AddRange(restored);
                _diagnosticInventoryEnabled = true;
            }
            return restored;
        }
        catch { foreach (var reservation in restored) reservation.Dispose(); throw; }
        finally { _stageGate.Release(); }
    }

    internal async Task<LocalApiStagedArtifact> StageDiagnosticInputAsync(string sourcePath,
        Func<LocalApiDiagnosticResourceSnapshot, CancellationToken, Task> persistInput,
        CancellationToken cancellationToken = default, Func<long, bool>? reserveBytes = null)
    {
        using var source = BorrowDiagnosticSource(sourcePath);
        return await StageDiagnosticInputAsync(source, persistInput, cancellationToken, reserveBytes).ConfigureAwait(false);
    }

    internal static LocalApiDiagnosticInputLease BorrowDiagnosticSource(string sourcePath) => LocalApiDiagnosticFileSafety.BorrowSource(sourcePath);

    // The v3 registry must hold this across consume/dispatch, capture, sealing,
    // and its FINAL journal acknowledgement, not only individual IO calls.
    internal LocalApiDiagnosticUse AcquireDiagnosticOperation() => new(_diagnosticLifetime.AcquireOperation());

    // Source-first v3 preparation keeps this SAME lease across bounded source
    // inspection, the Preparing journal row, copy, and staged reinspection.
    internal async Task<LocalApiStagedArtifact> StageDiagnosticInputAsync(LocalApiDiagnosticInputLease source,
        Func<LocalApiDiagnosticResourceSnapshot, CancellationToken, Task> persistInput,
        CancellationToken cancellationToken = default, Func<long, bool>? reserveBytes = null)
    {
        ArgumentNullException.ThrowIfNull(persistInput);
        source.Verify();
        await _stageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Action? releaseUse = null;
        var ownsStageGate = true;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            releaseUse = _diagnosticLifetime.AcquireOperation();
            _diagnosticRootGuard ??= LocalApiDiagnosticFileSafety.OpenGuard(_stageDirectory);
            LocalApiDiagnosticFileSafety.ValidateGuard(_diagnosticRootGuard, _stageDirectory);
            _diagnosticInventoryEnabled = true;
            var staged = await StageCoreAsync(source.Path, cancellationToken, reserveBytes, diagnostic: true,
                persistDiagnosticInput: async (resource, token) =>
                {
                    // Pending byte/slot reservation, source handle and lifetime
                    // use survive this release. No stager gate→registry callback.
                    ownsStageGate = false; _stageGate.Release();
                    try { await persistInput(resource, token).ConfigureAwait(false); }
                    finally
                    {
                        await _stageGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        ownsStageGate = true;
                    }
                }).ConfigureAwait(false);
            try { source.Verify(); return staged; }
            catch { staged.Dispose(); throw; }
        }
        finally { if (ownsStageGate) _stageGate.Release(); releaseUse?.Invoke(); }
    }

    internal IReadOnlyList<LocalApiDiagnosticResourceSnapshot> DiagnosticCreationDebts
    { get { lock (_diagnosticCapacityGate) return _diagnosticCreationDebts.Select(debt => debt.Resource).ToArray(); } }

    internal async Task<bool> CleanupDiagnosticCreationDebtsAsync(
        Func<LocalApiDiagnosticResourceSnapshot, CancellationToken, Task> persist, CancellationToken cancellationToken = default)
    {
        await _diagnosticDebtGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _stageGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { _diagnosticDebtGate.Release(); throw; }
        Action? releaseUse = null;
        async Task Persist(LocalApiDiagnosticResourceSnapshot resource)
        {
            _stageGate.Release();
            try { await persist(resource, cancellationToken).ConfigureAwait(false); }
            finally { await _stageGate.WaitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            releaseUse = _diagnosticLifetime.AcquireOperation();
            for (var index = _diagnosticCreationDebts.Count - 1; index >= 0; index--)
            {
                var debt = _diagnosticCreationDebts[index];
                var pending = debt.Resource.State == "removed" ? debt.Resource : debt.Resource with
                {
                    State = debt.Resource.Identity is not null && debt.Resource.State != "removed"
                        ? "deletePending" : debt.Resource.State,
                    DebtCode = "diagnostic_owned_handle_delete_pending"
                };
                await Persist(pending).ConfigureAwait(false);
                debt.Resource = pending;
                lock (_diagnosticCapacityGate) _diagnosticCreationDebts[index] = debt;
                if (debt.Resource.State != "removed")
                {
                    try
                    {
                        _diagnosticFault?.Invoke("input-delete", debt.Resource.Leaf);
                        FleetWindowsFileSafety.MarkDelete(debt.Handle); // known CREATE_NEW handle, no pathname or identity query
                        debt.Stream?.Dispose(); debt.Handle.Dispose();
                    }
                    catch { continue; }
                    debt.Resource = debt.Resource with { State = "removed", DebtCode = null };
                    lock (_diagnosticCapacityGate) _diagnosticCreationDebts[index] = debt;
                }
                await Persist(debt.Resource).ConfigureAwait(false);
                lock (_diagnosticCapacityGate) _diagnosticCreationDebts.RemoveAt(index);
            }
            return _diagnosticCreationDebts.Count == 0;
        }
        finally { _stageGate.Release(); releaseUse?.Invoke(); _diagnosticDebtGate.Release(); }
    }

    internal async Task<LocalApiDiagnosticOutputReservation> ReserveDiagnosticOutputAsync(
        LocalApiDiagnosticOutputIntent intent, LocalApiStagedArtifact input,
        Func<LocalApiDiagnosticOutputSnapshot, CancellationToken, Task> persist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persist);
        LocalApiDiagnosticOutputReservation reservation;
        Action? releaseUse = null;
        await _stageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            releaseUse = _diagnosticLifetime.AcquireOperation();
            if (!string.Equals(Path.GetDirectoryName(input.Path), _stageDirectory, StringComparison.OrdinalIgnoreCase))
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_input_invalid");
            var lease = input.BorrowDiagnosticInput();
            try
            {
                _diagnosticRootGuard ??= LocalApiDiagnosticFileSafety.OpenGuard(_stageDirectory);
                LocalApiDiagnosticFileSafety.ValidateGuard(_diagnosticRootGuard, _stageDirectory);
                _diagnosticInventoryEnabled = true;
                var inventory = GetInventory();
                if (inventory.FileCount > _limits.MaximumStagedFiles - LocalApiDiagnosticOutputIntent.ReservedOutputFiles)
                    throw LocalApiDiagnosticOutputIntent.Failure("staged_file_capacity");
                if (inventory.SizeBytes > _limits.MaximumStagedBytes - LocalApiDiagnosticOutputIntent.ReservedOutputBytes)
                    throw LocalApiDiagnosticOutputIntent.Failure("staged_byte_capacity");
                reservation = new(_stageDirectory, intent, lease, persist, _diagnosticLifetime.AcquireRead, _diagnosticLifetime.AcquireOperation,
                    () => LocalApiDiagnosticFileSafety.ValidateGuard(_diagnosticRootGuard!, _stageDirectory), _diagnosticFault);
                lock (_diagnosticCapacityGate) _diagnosticOutputs.Add(reservation);
            }
            catch { lease.Dispose(); throw; }
        }
        catch { releaseUse?.Invoke(); throw; }
        finally { _stageGate.Release(); }
        // No stager monitor/semaphore is held while the registry journals intent.
        try { await reservation.CommitReservationAsync(cancellationToken).ConfigureAwait(false); return reservation; }
        catch
        {
            lock (_diagnosticCapacityGate) _diagnosticOutputs.Remove(reservation);
            reservation.Dispose(); throw;
        }
        finally { releaseUse?.Invoke(); }
    }

    // Caller supplies ONLY a resource authenticated by the existing journal.
    // This reopens recorded bytes; it cannot seal/adopt a pathname or publish.
    internal async Task<LocalApiDiagnosticCandidateReadLease> ReopenDiagnosticArchiveAsync(
        LocalApiDiagnosticOutputIntent intent, LocalApiDiagnosticResourceSnapshot resource,
        CancellationToken cancellationToken = default)
    {
        if (resource.Role != "archive" || resource.Slot != 14 || resource.Leaf != intent.ArchiveLeaf ||
            resource.State != "sealed" || resource.Identity is null || resource.SizeBytes is not > 0 ||
            resource.SizeBytes > LocalApiDiagnosticOutputIntent.MaximumArchiveBytes || resource.Sha256 is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(resource.Sha256, "\\A[0-9a-f]{64}\\z"))
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_output_unavailable");
        await _stageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Action? releaseRoot = null;
        FileStream? stream = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LocalApiDiagnosticOutputReservation? reservation;
            lock (_diagnosticCapacityGate) reservation = _diagnosticOutputs.SingleOrDefault(r => r.Intent.OutputId == intent.OutputId);
            if (reservation is null) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_recovery_order_invalid");
            releaseRoot = await reservation.PinAuthenticatedArchiveReadAsync(cancellationToken).ConfigureAwait(false);
            var path = Path.Combine(_stageDirectory, resource.Leaf);
            stream = FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(path);
            FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, path, true);
            if (LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(stream.SafeFileHandle)) != resource.Identity ||
                stream.Length != resource.SizeBytes) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_changed");
            // The archive handle is now the nonempty witness BEFORE the root
            // guard is accepted. No input file is required or recreated.
            _diagnosticRootGuard ??= LocalApiDiagnosticFileSafety.OpenGuard(_stageDirectory);
            LocalApiDiagnosticFileSafety.ValidateGuard(_diagnosticRootGuard, _stageDirectory);
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (hash != resource.Sha256) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_resource_changed");
            stream.Position = 0; _diagnosticInventoryEnabled = true;
            return new(stream, releaseRoot);
        }
        catch { stream?.Dispose(); releaseRoot?.Invoke(); throw; }
        finally { _stageGate.Release(); }
    }

    public LocalApiStagedArtifact Reopen(string stagedPath)
    {
        var full = Path.GetFullPath(stagedPath);
        var root = Path.GetFullPath(_stageDirectory) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new LocalApiException("staged_artifact_invalid", "The staged artifact boundary is invalid.");
        var stream = FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(full);
        FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, full, requireSingleLink: true);
        var identity = FleetWindowsFileSafety.GetIdentity(stream.SafeFileHandle);
        return new LocalApiStagedArtifact(full, stream.Length, stream, identity);
    }

    public bool TryCleanupDebt(string stagedPath)
    {
        try
        {
            using var staged = Reopen(stagedPath);
            return staged.TryDelete(out _);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    public void CleanupOrphanedArtifacts()
    {
        foreach (var path in Directory.EnumerateFiles(_stageDirectory, "*.apk")
                     .Take(_limits.MaximumStagedFiles + 1))
        {
            if (!TryCleanupDebt(path))
            {
                throw new LocalApiException(
                    "staged_cleanup_pending",
                    "A prior staged APK could not be cleaned safely.");
            }
        }
        if (Directory.EnumerateFiles(_stageDirectory, "*.apk").Any())
        {
            throw new LocalApiException(
                "staged_cleanup_pending",
                "The staged APK workspace could not be proven empty.");
        }
    }

    // Only transient CLI/Core admission uses this inventory. A sharing-protected
    // file stays physically counted; configured API recovery keeps its strict cleanup.
    internal void CleanupTransientArtifacts()
    {
        foreach (var path in Directory.EnumerateFiles(_stageDirectory, "*.apk")
                     .Take(_limits.MaximumStagedFiles + 1))
        {
            try
            {
                using var artifact = Reopen(path);
                if (!artifact.TryDelete(out _, out var sharingProtected) && !sharingProtected)
                    throw new LocalApiException("staged_cleanup_pending", "A prior transient APK could not be cleaned safely.");
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
            {
                // Another admission completed cleanup before this inventory lease.
            }
        }
    }

    internal void ReleaseTransientOwnerLease() => _ownerLease.Dispose();

    public void CleanupUntrackedArtifacts(IReadOnlySet<string> retainedPaths)
    {
        var retained = retainedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_stageDirectory, "*.apk"))
        {
            var full = Path.GetFullPath(path);
            if (retained.Contains(full)) continue;
            // A v3 input name is owned by its authenticated resource state even
            // when removed/createPending. Legacy glob cleanup may not adopt a
            // foreign replacement or guess ownership of a preidentity create.
            lock (_diagnosticCapacityGate)
                if (_diagnosticOutputs.Any(output => string.Equals(output.InputLeaf, Path.GetFileName(full), StringComparison.OrdinalIgnoreCase)))
                    continue;
            if (!TryCleanupDebt(full))
            {
                throw new LocalApiException(
                    "staged_cleanup_pending",
                    "An unjournaled staged APK could not be cleaned safely during recovery.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _stageGate.Wait();
        try
        {
            if (_disposed) return;
            _diagnosticLifetime.BeginDispose(); // refuse before closing ANY root/ancestor/input witness
            _disposed = true;
            foreach (var reservation in _diagnosticOutputs) reservation.Dispose();
            foreach (var debt in _diagnosticCreationDebts) { debt.Stream?.Dispose(); debt.Handle.Dispose(); }
            _diagnosticRootGuard?.Dispose();
            foreach (var handle in _directoryHandles.AsEnumerable().Reverse()) handle.Dispose();
            _ownerLease.Dispose();
        }
        finally { _stageGate.Release(); }
    }

    private static LocalApiException Capacity(
        string code,
        long requestedBytes,
        long availableBytes,
        long limitBytes) =>
        new(
            code,
            $"Staging requests {requestedBytes} bytes; {availableBytes} bytes are available within the {limitBytes}-byte limit.",
            new LocalApiStagingCapacity(requestedBytes, availableBytes, limitBytes));

    private static FileStream AcquireOwnerLease(string path)
    {
        try
        {
            return FleetWindowsFileSafety.OpenOrCreateExclusiveFile(path);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 32 or 33)
        {
            throw new LocalApiException(
                "state_in_use",
                "The configured API state root already has an active owner.");
        }
    }

    private void RetainAncestorHandles(string stateDirectory)
    {
        var root = Path.GetPathRoot(stateDirectory)!;
        var current = Path.TrimEndingDirectorySeparator(root);
        foreach (var segment in Path.GetRelativePath(root, stateDirectory)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var handle = FleetWindowsFileSafety.OpenDirectory(current, allowDelete: false);
            FleetWindowsFileSafety.ValidateDirectory(handle, current);
            _directoryHandles.Add(handle);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void CreateOrValidatePrivateDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var current = WindowsIdentity.GetCurrent().User
                ?? throw new LocalApiException("state_owner_unknown", "The current Windows user SID is unavailable.");
            security.SetOwner(current);
            security.AddAccessRule(new FileSystemAccessRule(
                current, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            var info = new DirectoryInfo(path);
            info.Create(security);
        }
        var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new LocalApiException("state_owner_unknown", "The state directory owner cannot be proven.");
        var currentOwner = WindowsIdentity.GetCurrent().User!;
        var allowedOwners = new[]
        {
            currentOwner,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        if (!allowedOwners.Contains(owner))
            throw new LocalApiException("state_owner_invalid", "The state directory owner is not trusted.");
        if (!acl.AreAccessRulesProtected)
            throw new LocalApiException("state_acl_inherited", "The state directory must use a protected access list.");
        const FileSystemRights writableRights =
            FileSystemRights.Write |
            FileSystemRights.Modify |
            FileSystemRights.FullControl |
            FileSystemRights.CreateFiles |
            FileSystemRights.CreateDirectories |
            FileSystemRights.AppendData |
            FileSystemRights.WriteData |
            FileSystemRights.WriteAttributes |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var writable = (rule.FileSystemRights & writableRights) != 0;
            if (rule.AccessControlType == AccessControlType.Allow && writable &&
                !allowedOwners.Contains((SecurityIdentifier)rule.IdentityReference))
                throw new LocalApiException(
                    "state_acl_untrusted_writer",
                    "The state directory is writable by an untrusted principal.");
        }
    }
}

internal readonly record struct LocalApiStageInventory(int FileCount, long SizeBytes);
