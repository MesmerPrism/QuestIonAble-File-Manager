using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using QuestIonAbleFileManager.Core;

[assembly: InternalsVisibleTo("QuestIonAbleFileManager.BrowserHost.Tests")]

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserArtifactCapability(bool Supported, string Code, string Reason, string Alternative,
    long MaximumFileBytes = BrowserArtifactStore.MaximumFileBytes,
    long MaximumTotalBytes = BrowserArtifactStore.MaximumTotalBytes,
    int MaximumFiles = BrowserArtifactStore.MaximumFiles,
    int TransferTimeoutSeconds = BrowserArtifactStore.TransferTimeoutSeconds);
public sealed record BrowserArtifactReceipt(string Id, string FileName, long SizeBytes, string Sha256);
internal sealed record BrowserArtifactLimits(long MaximumFileBytes = BrowserArtifactStore.MaximumFileBytes,
    int MaximumFiles = BrowserArtifactStore.MaximumFiles, long MaximumTotalBytes = BrowserArtifactStore.MaximumTotalBytes);
public sealed class BrowserArtifactException(string code, string? cleanupDebtId = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? CleanupDebtId { get; } = cleanupDebtId;
}

public sealed class BrowserArtifactLease : IDisposable, IAsyncDisposable
{
    private readonly string _path;
    private readonly FileStream _stream;
    private Action? _release;
    internal BrowserArtifactLease(BrowserArtifactReceipt receipt, string path, FileStream stream, Action release)
    { Receipt = receipt; _path = path; _stream = stream; _release = release; }
    public BrowserArtifactReceipt Receipt { get; }
    [JsonIgnore] public string Path { get { ObjectDisposedException.ThrowIf(_release is null, this); return _path; } }
    [JsonIgnore] public Stream Stream { get { ObjectDisposedException.ThrowIf(_release is null, this); return _stream; } }
    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is null) return;
        try { _stream.Dispose(); } finally { release(); }
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>
/// Process-owned transient bytes, not durable LocalApi authority. Receipts never
/// contain paths. Keep the lease until every consumer or unresolved operation
/// releases its custody; cleanup never retires a leased artifact.
/// </summary>
public sealed class BrowserArtifactStore : IDisposable
{
    public const long MaximumFileBytes = LocalApiStateLimits.DefaultMaximumSingleArtifactBytes;
    public const long MaximumTotalBytes = LocalApiStateLimits.DefaultMaximumStagedBytes;
    public const int MaximumFiles = 8;
    public const int TransferTimeoutSeconds = 5 * 60;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<SafeFileHandle> _directories = [];
    private readonly string _root;
    private readonly BrowserArtifactFileSafety.Identity _rootIdentity;
    private readonly BrowserArtifactLimits _limits;
    private long _bytes;
    private bool _disposed;
    private bool _closing;
    private FileStream? _ownerMarker;

    public static BrowserArtifactCapability Capability => OperatingSystem.IsWindows()
        ? new(true, "platform_supported", "This platform supports protected transient staging; a private root must still pass admission on a local NTFS volume. ReFS is unsupported because this boundary uses NTFS 64-bit file identities.", "Windows companion with a local NTFS transient directory.")
        : new(false, "native_staging_unavailable", "Protected path staging is unavailable on this platform; Unix mode bits and advisory locks do not provide the required deny-write/deny-delete path lease.", "Use the Windows companion for staged uploads/downloads; portable read/control actions remain available.");

    public BrowserArtifactStore() : this(Path.GetTempPath(), new()) { }

    // A failed constructor never returns a usable store. If exact-handle
    // rollback itself failed, this opaque token permits explicit owner retry.
    public static bool TryCleanupCreationDebt(string id) => BrowserArtifactFileSafety.TryCleanupCreationDebt(id);

    internal static void ValidateVolume(DriveType type, string format)
    {
        if (type != DriveType.Fixed || format != "NTFS")
            throw new BrowserArtifactException("artifact_volume_unsupported");
    }

    internal BrowserArtifactStore(string parentDirectory, BrowserArtifactLimits limits)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(Capability.Reason);
        if (limits.MaximumFileBytes is < 1 or > MaximumFileBytes || limits.MaximumFiles is < 1 or > MaximumFiles ||
            limits.MaximumTotalBytes is < 1 or > MaximumTotalBytes)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _limits = limits;
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory));
        if (parent.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(parent))
            throw new BrowserArtifactException("artifact_root_invalid");
        var volume = new DriveInfo(Path.GetPathRoot(parent)!);
        ValidateVolume(volume.DriveType, volume.DriveFormat);
        _root = Path.Combine(parent, "QfmBrowserArtifacts-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Pin every existing ancestor before creating our random private root.
            var current = Path.GetPathRoot(parent)!;
            _directories.Add(BrowserArtifactFileSafety.OpenDirectory(current));
            foreach (var part in Path.GetRelativePath(current, parent).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                current = Path.Combine(current, part);
                _directories.Add(BrowserArtifactFileSafety.OpenDirectory(current));
            }
            BrowserArtifactFileSafety.CreatePrivateDirectory(_root);
            _directories.Add(BrowserArtifactFileSafety.OpenDirectory(_root, allowWriteSharing: false));
            _rootIdentity = BrowserArtifactFileSafety.ReadIdentity(_directories[^1]);
            _ownerMarker = BrowserArtifactFileSafety.CreateRootMarker(_root);
        }
        catch { foreach (var handle in _directories) handle.Dispose(); throw; }
    }

    public static string ValidateFileName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 || !char.IsAsciiLetterOrDigit(name[0]) ||
            name[^1] is '.' or ' ' || name.Contains("..", StringComparison.Ordinal) ||
            name.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-' and not ' '))
            throw new BrowserArtifactException("artifact_name_invalid");
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
            throw new BrowserArtifactException("artifact_name_invalid");
        return name;
    }

    public Task<BrowserArtifactReceipt> StageAsync(string fileName, Stream source, long? expectedLength = null,
        string? expectedSha256 = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("A readable artifact stream is required.", nameof(source));
        return CaptureCoreAsync(fileName, (destination, token) => source.CopyToAsync(destination, 64 * 1024, token),
            expectedLength, expectedSha256, cancellation);
    }

    public Task<BrowserArtifactReceipt> CaptureAsync(string fileName, Func<Stream, CancellationToken, Task> producer,
        CancellationToken cancellation = default) => CaptureCoreAsync(fileName, producer, null, null, cancellation);

    private async Task<BrowserArtifactReceipt> CaptureCoreAsync(string fileName, Func<Stream, CancellationToken, Task> producer,
        long? expectedLength, string? expectedSha256, CancellationToken cancellation)
    {
        ValidateFileName(fileName);
        ArgumentNullException.ThrowIfNull(producer);
        if (expectedLength is < 0) throw new BrowserArtifactException("artifact_size_invalid");
        if (expectedSha256 is not null) ValidateHash(expectedSha256);
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (OperatingSystem.IsWindows()) BrowserArtifactFileSafety.ValidatePrivateDirectory(_root);
            if (_entries.Count >= _limits.MaximumFiles) throw new BrowserArtifactException("artifact_file_capacity");
            if (expectedLength > _limits.MaximumFileBytes) throw new BrowserArtifactException("artifact_size_limit");
            if (expectedLength > _limits.MaximumTotalBytes - _bytes) throw new BrowserArtifactException("artifact_byte_capacity");
            var id = Guid.NewGuid().ToString("N");
            var path = Path.Combine(_root, id + Path.GetExtension(fileName));
            FileStream? file = null;
            BrowserArtifactFileSafety.Identity? identity = null;
            BoundedOutput? output = null;
            try
            {
                file = BrowserArtifactFileSafety.CreateFile(path);
                identity = BrowserArtifactFileSafety.ReadIdentity(file.SafeFileHandle);
                BrowserArtifactFileSafety.Validate(file.SafeFileHandle, path);
                output = new(file, _limits.MaximumFileBytes, _limits.MaximumTotalBytes - _bytes, cancellation);
                await producer(output, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                output.Complete();
                await file.FlushAsync(cancellation).ConfigureAwait(false);
                file.Flush(true);
                identity = BrowserArtifactFileSafety.Validate(file.SafeFileHandle, path);
                var digest = await HashAsync(file, cancellation).ConfigureAwait(false);
                if (file.Length != output.Written || (expectedLength is not null && file.Length != expectedLength) ||
                    (expectedSha256 is not null && digest != expectedSha256))
                    throw new BrowserArtifactException("artifact_integrity_mismatch");
                var receipt = new BrowserArtifactReceipt(id, fileName, file.Length, digest);
                file.Dispose(); file = null;
                var retained = BrowserArtifactFileSafety.OpenRead(path);
                try
                {
                    if (BrowserArtifactFileSafety.Validate(retained.SafeFileHandle, path) != identity)
                        throw new BrowserArtifactException("artifact_identity_changed");
                    // Independently re-read after the write-to-read handle transition.
                    if (await HashAsync(retained, cancellation).ConfigureAwait(false) != digest)
                        throw new BrowserArtifactException("artifact_integrity_mismatch");
                    _entries.Add(id, new(receipt, path, identity.Value, retained));
                    _bytes += receipt.SizeBytes;
                    return receipt;
                }
                catch { retained.Dispose(); throw; }
            }
            catch
            {
                output?.Stop();
                file?.Dispose();
                if (identity is not null)
                {
                    try { BrowserArtifactFileSafety.DeleteExpected(path, identity.Value); }
                    catch (BrowserArtifactException)
                    {
                        // Never delete a replacement or forget cleanup debt. It consumes
                        // capacity and can only be retired by exact owned-object cleanup.
                        var debtBytes = output?.AccountedBytes ?? 0;
                        _entries.Add(id, new(new(id, fileName, debtBytes, ""), path, identity.Value, null));
                        _bytes += debtBytes;
                    }
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserArtifactLease> AcquireAsync(string id, string expectedSha256, long expectedSizeBytes,
        CancellationToken cancellation = default)
    {
        ValidateHash(expectedSha256);
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (OperatingSystem.IsWindows()) BrowserArtifactFileSafety.ValidatePrivateDirectory(_root);
            var entry = Find(id);
            if (entry.Receipt.Sha256 != expectedSha256 || entry.Receipt.SizeBytes != expectedSizeBytes || entry.Retained is null)
                throw new BrowserArtifactException("artifact_integrity_mismatch");
            var stream = BrowserArtifactFileSafety.OpenRead(entry.Path);
            try
            {
                if (BrowserArtifactFileSafety.Validate(stream.SafeFileHandle, entry.Path) != entry.Identity ||
                    stream.Length != expectedSizeBytes || await HashAsync(stream, cancellation).ConfigureAwait(false) != expectedSha256 ||
                    BrowserArtifactFileSafety.Validate(stream.SafeFileHandle, entry.Path) != entry.Identity)
                    throw new BrowserArtifactException("artifact_integrity_mismatch");
                Interlocked.Increment(ref entry.Leases);
                return new(entry.Receipt, entry.Path, stream, () => Release(entry));
            }
            catch { stream.Dispose(); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { ThrowIfDisposed(); Remove(Find(id)); }
        finally { _gate.Release(); }
    }

    public async Task<int> CleanupAsync(CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { ThrowIfDisposed(); return Cleanup(); }
        finally { _gate.Release(); }
    }

    private int Cleanup()
    {
        var removed = 0;
        foreach (var entry in _entries.Values.Where(static entry => Volatile.Read(ref entry.Leases) == 0).ToArray())
        { Remove(entry); removed++; }
        return removed;
    }

    private void Remove(Entry entry)
    {
        if (Volatile.Read(ref entry.Leases) != 0) throw new BrowserArtifactException("artifact_in_use");
        entry.Retained?.Dispose(); entry.Retained = null;
        BrowserArtifactFileSafety.DeleteExpected(entry.Path, entry.Identity);
        _entries.Remove(entry.Receipt.Id);
        _bytes -= entry.Receipt.SizeBytes;
    }

    private Entry Find(string id) => _entries.TryGetValue(id, out var entry) ? entry :
        throw new BrowserArtifactException("artifact_not_found");
    // Acquire/removal remain serialized by _gate. Release can only lower the
    // count after its read handle closes, and must not reenter producer admission.
    private static void Release(Entry entry) => Interlocked.Decrement(ref entry.Leases);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed || _closing, this);
    private static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(static c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f')))
            throw new BrowserArtifactException("artifact_hash_invalid");
    }
    private static async Task<string> HashAsync(FileStream stream, CancellationToken cancellation)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            if (_entries.Values.Any(static entry => Volatile.Read(ref entry.Leases) != 0)) throw new BrowserArtifactException("artifact_in_use");
            // Once closing is admitted, no new bytes or leases can use a root
            // whose marker may have been retired, even if cleanup later fails.
            _closing = true;
            Cleanup();
            if (_ownerMarker is not null)
            {
                BrowserArtifactFileSafety.DeleteCreatedFile(_ownerMarker);
                _ownerMarker = null;
            }
            _directories[^1].Dispose();
            try { BrowserArtifactFileSafety.DeleteExpected(_root, _rootIdentity); }
            catch
            {
                // Keep ancestor custody and leave unexpected contents untouched.
                _directories[^1] = BrowserArtifactFileSafety.OpenDirectory(_root, allowWriteSharing: false);
                throw;
            }
            foreach (var handle in _directories) handle.Dispose();
            _disposed = true;
        }
        finally { _gate.Release(); }
    }

    private sealed class Entry(BrowserArtifactReceipt receipt, string path, BrowserArtifactFileSafety.Identity identity, FileStream? retained)
    {
        internal BrowserArtifactReceipt Receipt { get; } = receipt;
        internal string Path { get; } = path;
        internal BrowserArtifactFileSafety.Identity Identity { get; } = identity;
        internal FileStream? Retained { get; set; } = retained;
        internal int Leases;
    }

    // Producer receives only a non-seekable bounded sink. A swallowed write
    // failure remains sticky and prevents a partial file becoming a receipt.
    private sealed class BoundedOutput(FileStream destination, long fileLimit, long available, CancellationToken lifetime) : Stream
    {
        private readonly object _write = new();
        private Exception? _fault;
        private bool _completed;
        internal long Written { get; private set; }
        internal long AccountedBytes { get; private set; }
        internal void ThrowIfFaulted() { if (_fault is not null) throw _fault; }
        internal void Complete() { lock (_write) { _completed = true; ThrowIfFaulted(); } }
        internal void Stop() { lock (_write) _completed = true; }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_write)
            {
                ObjectDisposedException.ThrowIf(_completed, this);
                lifetime.ThrowIfCancellationRequested();
                ThrowIfFaulted();
                if (buffer.Length > fileLimit - Written) throw _fault = new BrowserArtifactException("artifact_size_limit");
                if (buffer.Length > available - Written) throw _fault = new BrowserArtifactException("artifact_byte_capacity");
                AccountedBytes = Written + buffer.Length;
                try { destination.Write(buffer); }
                catch (Exception exception) { _fault = exception; throw; }
                Written += buffer.Length;
            }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { lock (_write) _completed = true; base.Dispose(disposing); }
    }
}
