using System.Globalization;
using System.Security.Cryptography;

namespace QuestIonAbleFileManager.Core;

public sealed record BoundedApkExportResult(
    string PackageName, string RemoteApkPath, StreamingCommandResult Transfer);

public sealed partial class AdbClient
{
    public const long MaximumBoundedReadBytes = 1024L * 1024 * 1024;
    private const string BoundedReadFrame = "qfm-byte-transfer:v1:";

    /// <summary>
    /// Copies one shell-readable regular file through a fixed opened remote
    /// handle. The receipt describes captured bytes, not an application snapshot.
    /// The caller owns the destination and must discard partial output on failure.
    /// </summary>
    public async Task<StreamingCommandResult> PullFileBoundedAsync(
        string serial, string remotePath, Stream destination, long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        ValidateBoundedReadDestination(destination, maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (_runner is not IStreamingCommandRunner runner)
            throw new InvalidOperationException("The configured command runner does not support bounded binary streaming.");

        // Ordinary operator reads retain their absolute-path scope. No Fleet
        // remote-root/authority policy or caller-selected shell text is admitted.
        var command = $"candidate=$(realpath {AndroidInput.ShellQuote(remotePath)}) || exit 41; " +
            "expected=\"$candidate\"; " + BuildOpenedRemoteHandleProof() +
            "if [ ! -f /proc/self/fd/3 ]; then printf 'qfm-byte-transfer:not-regular\\n' >&2; exit 44; fi; " +
            "before=$(stat -L -c %d:%i:%s:%Y:%Z -- /proc/self/fd/3) || exit 45; " +
            "size=$(stat -L -c %s -- /proc/self/fd/3) || exit 45; " +
            "case \"$size\" in ''|*[!0-9]*) exit 46;; esac; " +
            $"if [ \"$size\" -gt {maximumBytes.ToString(CultureInfo.InvariantCulture)} ]; then " +
            "printf 'qfm-byte-transfer:maximum-bytes\\n' >&2; exit 47; fi; " +
            "cat <&3 || exit 50; " +
            "after=$(stat -L -c %d:%i:%s:%Y:%Z -- /proc/self/fd/3) || exit 45; " +
            "if [ \"$before\" != \"$after\" ]; then printf 'qfm-byte-transfer:source-changed\\n' >&2; exit 51; fi; " +
            $"printf '{BoundedReadFrame}%s\\n' \"$size\" >&2";
        // Raw shell-v2 preserves binary stdout, separate stderr and remote exit
        // status. exec-out's legacy exec protocol cannot carry this evidence.
        // -T also fails closed on devices without shell-v2; -n closes stdin.
        var arguments = new[] { "-s", serial, "shell", "-T", "-n", command };
        using var sink = new BoundedReadSink(destination, maximumBytes, cancellationToken);
        var result = await runner.RunToStreamAsync(AdbPath, arguments, sink, maximumBytes,
            TransferTimeout, cancellationToken).ConfigureAwait(false);
        result.CommandResult.EnsureSuccess("Read bounded remote file");
        cancellationToken.ThrowIfCancellationRequested();
        var captured = await sink.CompleteAsync().ConfigureAwait(false);
        var frame = result.CommandResult.StandardError.ReplaceLineEndings("\n").TrimEnd('\n');
        if (!frame.StartsWith(BoundedReadFrame, StringComparison.Ordinal) || frame.Length > 64 ||
            !long.TryParse(frame[BoundedReadFrame.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var expected) ||
            expected < 0 || expected > maximumBytes || expected != captured.BytesWritten ||
            result.BytesWritten != captured.BytesWritten || result.Sha256 != captured.Sha256 ||
            !string.IsNullOrEmpty(result.CommandResult.StandardOutput))
            throw new InvalidDataException("Bounded remote file bytes did not match the complete transfer evidence.");
        return result with { CommandResult = result.CommandResult with { StandardError = string.Empty } };
    }

    /// <summary>Exports one installed base APK to caller-owned bounded output without creating a checksum file.</summary>
    public async Task<BoundedApkExportResult> ExportSingleApkBoundedAsync(
        string serial, string packageName, Stream destination, long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        packageName = AndroidInput.RequirePackageName(packageName);
        ValidateBoundedReadDestination(destination, maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var package = await InspectPackageAsync(serial, packageName, cancellationToken).ConfigureAwait(false);
        if (package.ApkPaths.Count != 1) throw new SplitPackageException(package.PackageName, package.ApkPaths);
        var transfer = await PullFileBoundedAsync(serial, package.ApkPaths[0], destination, maximumBytes,
            cancellationToken).ConfigureAwait(false);
        var refreshed = await InspectPackageAsync(serial, packageName, cancellationToken).ConfigureAwait(false);
        if (refreshed.PackageName != package.PackageName || !refreshed.ApkPaths.SequenceEqual(package.ApkPaths, StringComparer.Ordinal))
            throw new InvalidDataException("The installed package APK path set changed during bounded export.");
        return new(package.PackageName, package.ApkPaths[0], transfer);
    }

    private static void ValidateBoundedReadDestination(Stream destination, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("A writable destination is required.", nameof(destination));
        if (maximumBytes is < 1 or > MaximumBoundedReadBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    }

    // Independently count/hash accepted writes. Even a producer that catches a
    // sink failure cannot turn an overflow or partial write into a receipt.
    private sealed class BoundedReadSink(Stream destination, long maximumBytes, CancellationToken lifetime) : Stream
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _written;
        private Exception? _fault;
        private bool _completed;

        private void Check(int count)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            lifetime.ThrowIfCancellationRequested();
            if (_fault is not null) throw _fault;
            if (count > maximumBytes - _written) throw _fault = new IOException("The bounded remote read exceeded its byte limit.");
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _write.Wait(lifetime);
            try
            {
                Check(buffer.Length);
                try { destination.Write(buffer); }
                catch (Exception error) { _fault = error; throw; }
                _hash.AppendData(buffer); _written += buffer.Length;
            }
            finally { _write.Release(); }
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Check(buffer.Length);
                try { await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { _fault = error; throw; }
                _hash.AppendData(buffer.Span); _written += buffer.Length;
            }
            finally { _write.Release(); }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        internal async Task<(long BytesWritten, string Sha256)> CompleteAsync()
        {
            await _write.WaitAsync(lifetime).ConfigureAwait(false);
            try
            {
                Check(0); _completed = true;
                return (_written, Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant());
            }
            finally { _write.Release(); }
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_completed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _completed = true; _hash.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
