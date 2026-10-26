using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QuestIonAbleFileManager.Core;

public sealed record BoundedApkExportResult(
    string PackageName, string RemoteApkPath, StreamingCommandResult Transfer);

public sealed partial class AdbClient
{
    public const long MaximumBoundedReadBytes = 1024L * 1024 * 1024;
    private const string BoundedReadFrame = "qfm-byte-transfer:v2:";
    private const string BoundedReadCompleteFrame = "qfm-byte-transfer:complete:v2:";
    private const int MaximumReadFramingBytes = 256;

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
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var command = $"candidate=$(realpath {AndroidInput.ShellQuote(remotePath)}) || exit 41; " +
            "expected=\"$candidate\"; " + BuildOpenedRemoteHandleProof() +
            // The shell owns FD 3; Android may close it in external stat children.
            "if [ ! -f /proc/$$/fd/3 ]; then printf 'qfm-byte-transfer:not-regular\\n' >&2; exit 44; fi; " +
            "before=$(stat -L -c %d:%i:%s:%Y:%Z -- /proc/$$/fd/3) || exit 45; " +
            "size=$(stat -L -c %s -- /proc/$$/fd/3) || exit 45; " +
            "case \"$size\" in ''|*[!0-9]*) exit 46;; esac; " +
            $"if [ \"$size\" -gt {maximumBytes.ToString(CultureInfo.InvariantCulture)} ]; then " +
            "printf 'qfm-byte-transfer:maximum-bytes\\n' >&2; exit 47; fi; " +
            $"printf '{BoundedReadFrame}{nonce}:%s\\n' \"$size\" || exit 52; " +
            "cat <&3 || exit 50; " +
            "after=$(stat -L -c %d:%i:%s:%Y:%Z -- /proc/$$/fd/3) || exit 45; " +
            "if [ \"$before\" != \"$after\" ]; then printf 'qfm-byte-transfer:source-changed\\n' >&2; exit 51; fi; " +
            $"printf '\\n{BoundedReadCompleteFrame}{nonce}:%s\\n' \"$size\"";
        // exec-out sets Windows stdout to binary, whereas shell-v2's stdio
        // callback may expand LF to CRLF. Its exec protocol has no remote exit
        // status: require the exact terminal frame after successful cat and
        // stable metadata, rather than treating local adb exit zero as proof.
        var arguments = new[] { "-s", serial, "exec-out", "sh", "-c", command };
        using var sink = new BoundedReadSink(destination, maximumBytes, nonce, cancellationToken);
        var result = await runner.RunToStreamAsync(AdbPath, arguments, sink, maximumBytes + MaximumReadFramingBytes,
            TransferTimeout, cancellationToken).ConfigureAwait(false);
        result.CommandResult.EnsureSuccess("Read bounded remote file");
        cancellationToken.ThrowIfCancellationRequested();
        var captured = await sink.CompleteAsync().ConfigureAwait(false);
        if (result.BytesWritten != captured.WireBytes || result.Sha256 != captured.WireSha256 ||
            !string.IsNullOrEmpty(result.CommandResult.StandardError) ||
            !string.IsNullOrEmpty(result.CommandResult.StandardOutput))
            throw new InvalidDataException("Bounded remote file bytes did not match the complete transfer evidence.");
        return result with { BytesWritten = captured.BytesWritten, Sha256 = captured.Sha256 };
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

    // Parse only bounded framing and stream payload unchanged. Independently
    // count/hash both wire and payload; swallowed faults cannot become receipts.
    private sealed class BoundedReadSink(Stream destination, long maximumBytes, string nonce, CancellationToken lifetime) : Stream
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly IncrementalHash _wireHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _header = new byte[96];
        private int _headerLength;
        private byte[]? _trailer;
        private int _trailerLength;
        private long _expected = -1;
        private long _written;
        private long _wireWritten;
        private Exception? _fault;
        private bool _completed;

        private void Check(int count)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            lifetime.ThrowIfCancellationRequested();
            if (_fault is not null) throw _fault;
            if (count > maximumBytes + MaximumReadFramingBytes - _wireWritten)
                throw _fault = new IOException("The bounded remote read exceeded its wire byte limit.");
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) =>
            AcceptAsync(buffer.ToArray(), true, lifetime).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            AcceptAsync(buffer, false, cancellationToken);
        private async ValueTask AcceptAsync(ReadOnlyMemory<byte> buffer, bool synchronous, CancellationToken cancellationToken)
        {
            await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Check(buffer.Length);
                var offset = 0;
                while (offset < buffer.Length)
                {
                    if (_expected < 0) ReadHeader(buffer.Span[offset++]);
                    else if (_written < _expected)
                    {
                        var count = (int)Math.Min(buffer.Length - offset, _expected - _written);
                        var payload = buffer.Slice(offset, count);
                        if (synchronous) destination.Write(payload.Span);
                        else await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                        _hash.AppendData(payload.Span); _written += count; offset += count;
                    }
                    else
                    {
                        if (_trailerLength >= _trailer!.Length || buffer.Span[offset++] != _trailer[_trailerLength++])
                            throw new InvalidDataException("The bounded remote read completion frame did not match.");
                    }
                }
                _wireHash.AppendData(buffer.Span); _wireWritten += buffer.Length;
            }
            catch (Exception error) { _fault = error; throw; }
            finally { _write.Release(); }
        }
        private void ReadHeader(byte value)
        {
            if (_headerLength == _header.Length) throw new InvalidDataException("The bounded remote read header exceeded its limit.");
            _header[_headerLength++] = value;
            if (value != (byte)'\n') return;
            var header = Encoding.ASCII.GetString(_header, 0, _headerLength - 1);
            var prefix = BoundedReadFrame + nonce + ":";
            if (!header.StartsWith(prefix, StringComparison.Ordinal) ||
                !long.TryParse(header[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var expected) ||
                expected < 0 || expected > maximumBytes || header[prefix.Length..] != expected.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("The bounded remote read header did not match its byte limit and request.");
            _expected = expected;
            _trailer = Encoding.ASCII.GetBytes($"\n{BoundedReadCompleteFrame}{nonce}:{expected.ToString(CultureInfo.InvariantCulture)}\n");
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        internal async Task<(long BytesWritten, string Sha256, long WireBytes, string WireSha256)> CompleteAsync()
        {
            await _write.WaitAsync(lifetime).ConfigureAwait(false);
            try
            {
                Check(0);
                if (_expected < 0 || _written != _expected || _trailerLength != _trailer!.Length)
                    throw new InvalidDataException("The bounded remote read ended without complete payload and terminal evidence.");
                _completed = true;
                return (_written, Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant(),
                    _wireWritten, Convert.ToHexString(_wireHash.GetHashAndReset()).ToLowerInvariant());
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
            if (disposing) { _completed = true; _hash.Dispose(); _wireHash.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
