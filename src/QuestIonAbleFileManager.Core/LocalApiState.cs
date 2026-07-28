using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace QuestIonAbleFileManager.Core;

public sealed record LocalApiStateLimits(
    int MaximumRetainedOperations = 256,
    int MaximumRunningOperations = 4,
    long MaximumStagedBytes = 512L * 1024 * 1024,
    int MaximumStagedFiles = 256,
    int MaximumResultBytes = 64 * 1024,
    int MaximumOutputCharacters = 4 * 1024,
    long MaximumJournalBytes = 4L * 1024 * 1024,
    TimeSpan? TerminalRetention = null)
{
    public TimeSpan EffectiveTerminalRetention => TerminalRetention ?? TimeSpan.FromHours(24);
}

public sealed record LocalApiStateSettings(
    string StateDirectory,
    byte[] JournalIntegrityKey,
    LocalApiStateLimits Limits)
{
    internal Func<string, bool>? JournalFault { get; init; }
    internal Func<string, bool>? CleanupFault { get; init; }

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
    public void Dispose() => _retainedHandle.Dispose();

    public bool TryDelete(out string? error)
    {
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

    public LocalApiArtifactStager(LocalApiStateSettings settings)
    {
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
        Func<long, bool>? reserveBytes)
    {
        var fullSource = Path.GetFullPath(sourcePath);

        var stagedFiles = Directory.EnumerateFiles(_stageDirectory, "*.apk").Take(_limits.MaximumStagedFiles + 1).ToArray();
        if (stagedFiles.Length >= _limits.MaximumStagedFiles)
            throw new LocalApiException("staged_file_capacity", "The staged-file capacity is exhausted.");
        var stagedBytes = stagedFiles.Sum(path => new FileInfo(path).Length);

        await using var source = FleetWindowsFileSafety.OpenReadOnlyFile(fullSource);
        FleetWindowsFileSafety.ValidateFile(source.SafeFileHandle, fullSource, requireSingleLink: true);
        var before = FleetWindowsFileSafety.GetIdentity(source.SafeFileHandle);
        if (before.NumberOfLinks != 1)
            throw new LocalApiException("source_hardlink_rejected", "The APK source must have exactly one hard link.");
        if (source.Length <= 0 || source.Length > _limits.MaximumStagedBytes - stagedBytes)
            throw new LocalApiException("staged_byte_capacity", "The staged-byte capacity is exhausted.");
        if (reserveBytes is not null && !reserveBytes(source.Length))
            throw new LocalApiException("staged_byte_capacity", "The staged-byte capacity is exhausted.");

        var stagedPath = Path.Combine(_stageDirectory, Guid.NewGuid().ToString("N") + ".apk");
        FileStream? staged = null;
        FleetWindowsFileIdentity? stagedIdentity = null;
        try
        {
            staged = FleetWindowsFileSafety.CreateNewRetainedReadableFile(stagedPath);
            FleetWindowsFileSafety.ValidateFile(staged.SafeFileHandle, stagedPath, requireSingleLink: true);
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
            FleetWindowsFileSafety.ValidateFile(retained.SafeFileHandle, stagedPath, requireSingleLink: true);
            if (FleetWindowsFileSafety.GetIdentity(retained.SafeFileHandle) != stagedIdentity.Value)
            {
                retained.Dispose();
                throw new LocalApiException(
                    "staged_artifact_changed",
                    "The staged artifact identity changed while its immutable handle was retained.");
            }
            return new LocalApiStagedArtifact(
                stagedPath,
                stagedLength,
                retained,
                stagedIdentity.Value);
        }
        catch
        {
            if (staged is not null)
            {
                staged.Dispose();
            }
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
    }

    public LocalApiStageInventory GetInventory()
    {
        var paths = Directory.EnumerateFiles(_stageDirectory, "*.apk")
            .Take(_limits.MaximumStagedFiles + 1)
            .ToArray();
        long bytes = 0;
        foreach (var path in paths)
        {
            using var stream = FleetWindowsFileSafety.OpenReadOnlyFile(path);
            FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, path, requireSingleLink: true);
            checked { bytes += stream.Length; }
        }
        return new LocalApiStageInventory(paths.Length, bytes);
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

    public void CleanupUntrackedArtifacts(IReadOnlySet<string> retainedPaths)
    {
        var retained = retainedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_stageDirectory, "*.apk"))
        {
            var full = Path.GetFullPath(path);
            if (retained.Contains(full)) continue;
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
        foreach (var handle in _directoryHandles.AsEnumerable().Reverse()) handle.Dispose();
        _ownerLease.Dispose();
        _stageGate.Dispose();
    }

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
