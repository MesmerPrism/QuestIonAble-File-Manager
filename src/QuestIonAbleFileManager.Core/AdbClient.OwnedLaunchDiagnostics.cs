using System.Security.Cryptography;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    // Sealing uses the same closed same-package component grammar as dispatch.
    internal static bool IsLaunchDiagnosticComponent(string packageName, string component) =>
        !component.Any(char.IsControl) && TryNormalizeComponent(component, packageName, out _);

    internal bool SupportsOwnedLaunchDiagnostics => _runner is ITruncatingCommandRunner &&
        _runner is IArmedCaptureCommandRunner &&
        (_runner is not DiagnosticRequiredCommandRunner required || required.SupportsArmedCapture);

    internal async Task<ApkLaunchDiagnosticBundleResult> CaptureAdmittedApkLaunchDiagnosticsAsync(
        string serial, LocalApiDiagnosticInputLease input, LocalApiDiagnosticCaptureSink output,
        Action? dispatchObserver, CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        if (!output.Reservation.Intent.IsLaunch)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_command_mismatch");
        if (!SupportsOwnedLaunchDiagnostics)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_runner_unsupported");
        input.Verify();
        output.Reservation.VerifyInput(input);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300), _launchReadbackTimeProvider);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var owned = CreateOwnedDiagnosticClient(operation.Token);
        using var captureUse = await output.Reservation.BeginCaptureAsync(input, operation.Token).ConfigureAwait(false);
        return await RunLaunchDiagnosticWithFenceAsync(observer => owned.CaptureAdmittedLaunchDiagnosticsCoreAsync(
            serial, output.Reservation.ReportedInputPath, input.Path, output.Reservation.ReportedOutputPath,
            (IArmedCaptureCommandRunner)owned._runner, observer, output, operation.Token), dispatchObserver).ConfigureAwait(false);
    }

    private async Task<ArmedCaptureCommandResult<ApkLaunchDiagnosticAttempt>> RunOwnedLaunchCaptureAsync(
        IArmedCaptureCommandRunner runner, IReadOnlyList<string> arguments, OwnedLaunchBuffer destination,
        Func<CancellationToken, Task<ApkLaunchDiagnosticAttempt>> action, Func<bool> dispatchObserved,
        CancellationToken cancellationToken)
    {
        var gate = new object();
        var open = true;
        var called = false;
        var invalidCallback = false;
        Task<ApkLaunchDiagnosticAttempt>? actualTask = null;
        ApkLaunchDiagnosticAttempt? actual = null;
        ArmedCaptureCommandResult<ApkLaunchDiagnosticAttempt>? returned = null;
        var completedAtReturn = false;
        Exception? runnerFailure = null;
        Task<ApkLaunchDiagnosticAttempt> Invoke(CancellationToken actionToken)
        {
            lock (gate)
            {
                if (!open || called)
                {
                    invalidCallback = true;
                    throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_action_provenance");
                }
                called = true;
                actualTask = action(actionToken);
                return actualTask;
            }
        }
        try
        {
            returned = await runner.RunArmedCaptureAsync(AdbPath, arguments, destination,
                MaximumLaunchDiagnosticBytes, LaunchDiagnosticPostActionWindow, Invoke, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { runnerFailure = exception; }
        finally
        {
            // No retained callback or pipe may acquire fresh ownership after
            // runner acknowledgement. A callback already started is still ours.
            lock (gate)
            {
                open = false;
                completedAtReturn = actualTask?.IsCompletedSuccessfully == true;
                destination.Revoke();
            }
            if (actualTask is not null)
                actual = await actualTask.ConfigureAwait(false);
        }
        if (runnerFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(runnerFailure).Throw();
        if (!called || !completedAtReturn || invalidCallback || actual is null || returned is null ||
            !ReferenceEquals(actual, returned.ActionResult) || actual.DispatchAttempted != dispatchObserved())
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_action_provenance");
        var bytes = destination.GetSealedBytes();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (destination.Overflowed || returned.BytesWritten != bytes.LongLength || returned.Sha256 != hash)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_capture_provenance");
        return returned;
    }

    // The runner receives only a revocable write-only view. The fixed backing
    // capacity prevents even a broken injected runner from allocating a prefix
    // beyond the policy cap. Snapshotting happens only after revocation.
    private sealed class OwnedLaunchBuffer(int maximumBytes) : Stream
    {
        private readonly object _gate = new();
        private readonly byte[] _bytes = new byte[maximumBytes];
        private int _count;
        private bool _revoked, _disposed;
        internal bool Overflowed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite { get { lock (_gate) return !_revoked && !_disposed; } }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        internal void Revoke() { lock (_gate) _revoked = true; }
        internal byte[] TakeSealedBytes() { Revoke(); return GetSealedBytes(); }
        internal byte[] GetSealedBytes()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_revoked) throw new InvalidOperationException("Capture storage is still writable.");
                return _bytes.AsSpan(0, _count).ToArray();
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || _revoked, this);
                if (buffer.Length > _bytes.Length - _count)
                {
                    Overflowed = true;
                    throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_output_limit");
                }
                buffer.CopyTo(_bytes.AsSpan(_count));
                _count += buffer.Length;
            }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        protected override void Dispose(bool disposing) { lock (_gate) { _revoked = true; _disposed = true; } base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
