using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace QuestIonAbleFileManager.BrowserHost;

/// <summary>Separate process-local work quota. Reservations include undeleted cleanup debt.</summary>
internal sealed class BrowserApkSetWorkStore
{
    internal const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly List<Work> _work = [];
    private readonly List<SafeFileHandle> _ancestors = [];
    private string? _root;
    private BrowserArtifactFileSafety.Identity? _rootIdentity;
    private SafeFileHandle? _rootHandle;
    private FileStream? _marker;
    private string? _rootMarkerDebt;
    private long _reserved;
    private volatile bool _rootCreationFailed;
    private bool _closing;
    private readonly long _maximumBytes;
    private readonly Action<string>? _beforeCopy;
    private readonly Action<string>? _beforeSnapshot;
    private readonly Action<string>? _beforeCleanup;
    private readonly Func<string, FileStream> _createMarker;
    private readonly Func<string, bool> _cleanupCreationDebt;
    private readonly Func<FileStream, BrowserArtifactFileSafety.Identity> _readCopyIdentity;

    // Internal bounded fault seams exercise ownership failures without changing
    // the shared native file-safety primitives or the public host contract.
    internal BrowserApkSetWorkStore(long maximumBytes = MaximumBytes,
        Action<string>? beforeCopy = null, Action<string>? beforeSnapshot = null,
        Action<string>? beforeCleanup = null, Func<string, FileStream>? createMarker = null,
        Func<string, bool>? cleanupCreationDebt = null,
        Func<FileStream, BrowserArtifactFileSafety.Identity>? readCopyIdentity = null)
    {
        if (maximumBytes is < 1 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
        _beforeCopy = beforeCopy; _beforeSnapshot = beforeSnapshot; _beforeCleanup = beforeCleanup;
        _createMarker = createMarker ?? BrowserArtifactFileSafety.CreateRootMarker;
        _cleanupCreationDebt = cleanupCreationDebt ?? BrowserArtifactFileSafety.TryCleanupCreationDebt;
        _readCopyIdentity = readCopyIdentity ?? (file => BrowserArtifactFileSafety.ReadIdentity(file.SafeFileHandle));
    }

    internal long ReservedBytes { get { lock (_gate) return _reserved; } }

    internal bool CleanupPending
    {
        get { lock (_gate) return _rootCreationFailed || _work.Any(item => item.CleanupPending); }
    }

    internal Work Reserve(long bytes)
    {
        lock (_gate)
        {
            if (_closing) throw new BrowserRequestException("busy", 409);
            if (_rootCreationFailed || _work.Any(item => item.CleanupPending))
                throw new BrowserRequestException("apk_set_work_cleanup_pending", 409);
            if (bytes < 0 || bytes > _maximumBytes - _reserved)
                throw new BrowserRequestException("apk_set_work_capacity", 409);
            var work = new Work(this, bytes);
            _work.Add(work);
            _reserved += bytes;
            return work;
        }
    }

    private void EnsureRoot()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (_rootHandle is not null && !_rootCreationFailed)
        {
            BrowserArtifactFileSafety.ValidatePrivateDirectory(_root!);
            if (_rootIdentity is not { } identity ||
                !BrowserArtifactFileSafety.SameObject(BrowserArtifactFileSafety.Validate(_rootHandle, _root!, true), identity))
                throw new BrowserArtifactException("artifact_identity_changed");
            return;
        }
        if (_root is not null) throw new BrowserArtifactException("apk_set_work_cleanup_pending");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (parent.StartsWith(@"\\", StringComparison.Ordinal))
            throw new BrowserArtifactException("artifact_root_invalid");
        var volume = new DriveInfo(Path.GetPathRoot(parent)!);
        BrowserArtifactStore.ValidateVolume(volume.DriveType, volume.DriveFormat);
        try
        {
            var current = Path.GetPathRoot(parent)!;
            _ancestors.Add(BrowserArtifactFileSafety.OpenDirectory(current));
            foreach (var part in Path.GetRelativePath(current, parent).Split(Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                current = Path.Combine(current, part);
                _ancestors.Add(BrowserArtifactFileSafety.OpenDirectory(current));
            }
            _root = Path.Combine(parent, "QfmBrowserApkSets-" + Guid.NewGuid().ToString("N"));
            BrowserArtifactFileSafety.CreatePrivateDirectory(_root);
            _rootHandle = BrowserArtifactFileSafety.OpenDirectory(_root, allowWriteSharing: false);
            _rootIdentity = BrowserArtifactFileSafety.ReadIdentity(_rootHandle);
            try { _marker = _createMarker(_root); }
            catch (BrowserArtifactException exception) when (exception.CleanupDebtId is not null)
            { _rootMarkerDebt = exception.CleanupDebtId; throw; }
        }
        catch
        {
            // Keep any partly created root and ancestor custody for exact shutdown
            // cleanup. An object whose identity was never established is never deleted.
            _rootCreationFailed = true;
            throw;
        }
    }

    private void Release(Work work)
    {
        lock (_gate)
        {
            if (_work.Remove(work)) _reserved -= work.ReservedBytes;
        }
    }

    private bool CleanupRootMarkerDebt()
    {
        if (_rootMarkerDebt is null) return true;
        if (!_cleanupCreationDebt(_rootMarkerDebt)) return false;
        _rootMarkerDebt = null;
        return true;
    }

    internal bool Cleanup()
    {
        lock (_gate) _closing = true;
        Work[] owned;
        lock (_gate) owned = _work.ToArray();
        foreach (var work in owned) work.Cleanup();
        lock (_gate)
        {
            if (_work.Count != 0) return false;
            try
            {
                if (!CleanupRootMarkerDebt()) return false;
                if (_marker is not null)
                { BrowserArtifactFileSafety.DeleteCreatedFile(_marker); _marker = null; }
                if (_root is not null)
                {
                    if (_rootIdentity is null) return false;
                    _rootHandle?.Dispose(); _rootHandle = null;
                    try { BrowserArtifactFileSafety.DeleteExpected(_root, _rootIdentity.Value); }
                    catch
                    {
                        _rootHandle = BrowserArtifactFileSafety.OpenDirectory(_root, allowWriteSharing: false);
                        throw;
                    }
                    _root = null;
                }
                foreach (var handle in _ancestors) handle.Dispose();
                _ancestors.Clear();
                _rootCreationFailed = false;
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    internal sealed class Work(BrowserApkSetWorkStore owner, long reservedBytes)
    {
        private readonly List<Copy> _copies = [];
        private string? _folder;
        private BrowserArtifactFileSafety.Identity? _identity;
        private SafeFileHandle? _directory;
        private FileStream? _marker;
        private string? _markerDebt;
        private bool _released;
        private volatile bool _cleanupPending;
        internal long ReservedBytes { get; } = reservedBytes;
        internal bool CleanupPending => _cleanupPending;
        internal string Folder => _folder ?? throw new BrowserArtifactException("apk_set_not_prepared");
        internal IReadOnlyList<string> Paths => _copies.Select(copy => copy.Path).ToArray();

        internal async Task PrepareAsync(IReadOnlyList<BrowserArtifactLease> sources, CancellationToken cancellation,
            Action? workHeld = null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            owner.EnsureRoot();
            _folder = Path.Combine(owner._root!, Guid.NewGuid().ToString("N"));
            BrowserArtifactFileSafety.CreatePrivateDirectory(_folder);
            _directory = BrowserArtifactFileSafety.OpenDirectory(_folder, allowWriteSharing: false);
            _identity = BrowserArtifactFileSafety.ReadIdentity(_directory);
            try { _marker = owner._createMarker(_folder); }
            catch (BrowserArtifactException exception) when (exception.CleanupDebtId is not null)
            { _markerDebt = exception.CleanupDebtId; throw; }
            workHeld?.Invoke();
            owner._beforeCopy?.Invoke(_folder);
            foreach (var source in sources)
            {
                cancellation.ThrowIfCancellationRequested();
                BrowserArtifactStore.ValidateFileName(source.Receipt.FileName);
                var path = Path.Combine(_folder, source.Receipt.FileName);
                var file = BrowserArtifactFileSafety.CreateFile(path);
                var copy = new Copy(path, file);
                _copies.Add(copy); // Register ownership before any fallible validation/write.
                copy.Identity = owner._readCopyIdentity(file);
                BrowserArtifactFileSafety.Validate(file.SafeFileHandle, path);
                source.Stream.Position = 0;
                var buffer = new byte[64 * 1024];
                long remaining = source.Receipt.SizeBytes;
                while (remaining > 0)
                {
                    var count = await source.Stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellation)
                        .ConfigureAwait(false);
                    if (count == 0) throw new BrowserArtifactException("artifact_integrity_mismatch");
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    remaining -= count;
                }
                if (await source.Stream.ReadAsync(buffer.AsMemory(0, 1), cancellation).ConfigureAwait(false) != 0)
                    throw new BrowserArtifactException("artifact_integrity_mismatch");
                await file.FlushAsync(cancellation).ConfigureAwait(false);
                file.Flush(true);
                copy.Identity = BrowserArtifactFileSafety.Validate(file.SafeFileHandle, path);
                if (file.Length != source.Receipt.SizeBytes || await HashAsync(file, cancellation).ConfigureAwait(false) != source.Receipt.Sha256)
                    throw new BrowserArtifactException("artifact_integrity_mismatch");
                file.Dispose(); copy.Handle = null;
                var retained = BrowserArtifactFileSafety.OpenRead(path);
                copy.Handle = retained;
                if (BrowserArtifactFileSafety.Validate(retained.SafeFileHandle, path) != copy.Identity ||
                    retained.Length != source.Receipt.SizeBytes || await HashAsync(retained, cancellation).ConfigureAwait(false) != source.Receipt.Sha256)
                    throw new BrowserArtifactException("artifact_identity_changed");
            }
            owner._beforeSnapshot?.Invoke(_folder);
            Validate();
        }

        internal void Validate()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            BrowserArtifactFileSafety.ValidatePrivateDirectory(Folder);
            // NTFS may grow a directory's index as selected children are created.
            // Its allocation length is mutable metadata, unlike volume/file ID.
            if (_directory is null || _identity is not { } identity ||
                !BrowserArtifactFileSafety.SameObject(BrowserArtifactFileSafety.Validate(_directory, Folder, true), identity))
                throw new BrowserArtifactException("artifact_identity_changed");
            foreach (var copy in _copies)
                if (copy.Handle is null || BrowserArtifactFileSafety.Validate(copy.Handle.SafeFileHandle, copy.Path) != copy.Identity)
                    throw new BrowserArtifactException("artifact_identity_changed");
            var actual = Directory.EnumerateFiles(Folder).Select(Path.GetFullPath).ToArray();
            var expected = Paths.Append(Path.Combine(Folder, BrowserArtifactFileSafety.RootMarkerName));
            if (!actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
                throw new BrowserArtifactException("apk_set_membership_changed");
            if (Directory.EnumerateDirectories(Folder).Any()) throw new BrowserArtifactException("apk_set_membership_changed");
        }

        internal bool Cleanup()
        {
            if (_released) return true;
            try
            {
                if (!owner.CleanupRootMarkerDebt()) throw new BrowserArtifactException("artifact_create_cleanup_pending");
                if (_markerDebt is not null)
                {
                    if (!owner._cleanupCreationDebt(_markerDebt)) throw new BrowserArtifactException("artifact_create_cleanup_pending");
                    _markerDebt = null;
                }
                if (_folder is not null) owner._beforeCleanup?.Invoke(_folder);
                foreach (var copy in _copies.ToArray())
                {
                    // CreateFile deliberately lacks DELETE access. If its initial
                    // identity query failed transiently, recover identity using the
                    // still-owned creation handle before closing it. An unavailable
                    // or invalid object keeps its handle and charged cleanup debt.
                    if (copy.Identity is null && copy.Handle is not null)
                    {
                        var observed = owner._readCopyIdentity(copy.Handle);
                        var validated = BrowserArtifactFileSafety.Validate(copy.Handle.SafeFileHandle, copy.Path);
                        if (!BrowserArtifactFileSafety.SameObject(observed, validated))
                            throw new BrowserArtifactException("artifact_identity_changed");
                        copy.Identity = validated;
                    }
                    if (copy.Identity is null) throw new BrowserArtifactException("artifact_identity_unavailable");
                    copy.Handle?.Dispose(); copy.Handle = null;
                    BrowserArtifactFileSafety.DeleteExpected(copy.Path, copy.Identity.Value);
                    _copies.Remove(copy);
                }
                if (_marker is not null)
                { BrowserArtifactFileSafety.DeleteCreatedFile(_marker); _marker = null; }
                if (_folder is not null)
                {
                    if (_identity is null) throw new BrowserArtifactException("artifact_identity_unavailable");
                    _directory?.Dispose(); _directory = null;
                    try { BrowserArtifactFileSafety.DeleteExpected(_folder, _identity.Value); }
                    catch
                    {
                        _directory = BrowserArtifactFileSafety.OpenDirectory(_folder, allowWriteSharing: false);
                        throw;
                    }
                    _folder = null;
                }
                _released = true;
                _cleanupPending = false;
                owner.Release(this);
                return true;
            }
            catch (Exception) { _cleanupPending = true; return false; }
        }

        private static async Task<string> HashAsync(FileStream file, CancellationToken cancellation)
        {
            file.Position = 0;
            var hash = await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false);
            file.Position = 0;
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private sealed class Copy(string path, FileStream handle)
        {
            internal string Path { get; } = path;
            internal FileStream? Handle { get; set; } = handle;
            internal BrowserArtifactFileSafety.Identity? Identity { get; set; }
        }
    }
}
