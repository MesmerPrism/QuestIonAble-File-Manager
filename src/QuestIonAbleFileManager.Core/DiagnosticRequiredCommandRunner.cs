using System.Text;

namespace QuestIonAbleFileManager.Core;

// A separate owned-only adapter closes inspector/identity/runtime/UID/probe
// calls without modifying public command collection or copying those algorithms.
internal sealed class DiagnosticRequiredCommandRunner : ITruncatingCommandRunner, IArmedCaptureCommandRunner
{
    private readonly ITruncatingCommandRunner _capture;
    private readonly IStreamingCommandRunner _streaming;
    private readonly IArmedCaptureCommandRunner? _armed;
    private readonly CancellationToken _operationToken;
    internal bool SupportsArmedCapture => _armed is not null &&
        (_armed is not DiagnosticRequiredCommandRunner nested || nested.SupportsArmedCapture);
    internal DiagnosticRequiredCommandRunner(ICommandRunner runner) : this(runner, default) { }
    internal DiagnosticRequiredCommandRunner(ICommandRunner runner, CancellationToken operationToken)
    {
        _capture = runner as ITruncatingCommandRunner ?? throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_runner_unsupported");
        _streaming = runner as IStreamingCommandRunner ?? throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_runner_unsupported");
        _armed = runner as IArmedCaptureCommandRunner;
        _operationToken = operationToken;
    }

    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operationToken);
        linked.Token.ThrowIfCancellationRequested();
        var result = await _capture.RunTruncatingAsync(fileName, arguments,
            LocalApiDiagnosticOutputIntent.MaximumRequiredStandardOutputBytes,
            LocalApiDiagnosticOutputIntent.MaximumRequiredStandardErrorBytes, timeout, linked.Token).ConfigureAwait(false);
        if (result.Truncated ||
            ExceedsUtf8Limit(result.CommandResult.StandardOutput, LocalApiDiagnosticOutputIntent.MaximumRequiredStandardOutputBytes) ||
            ExceedsUtf8Limit(result.CommandResult.StandardError, LocalApiDiagnosticOutputIntent.MaximumRequiredStandardErrorBytes))
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_output_limit");
        return result.CommandResult;
    }

    public async Task<TruncatingCommandResult> RunTruncatingAsync(string fileName, IReadOnlyList<string> arguments,
        int maximumStandardOutputBytes, int maximumStandardErrorBytes, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        if (maximumStandardOutputBytes is < 1 or > LocalApiDiagnosticOutputIntent.MaximumOptionalStandardOutputBytes ||
            maximumStandardErrorBytes is < 1 or > LocalApiDiagnosticOutputIntent.MaximumOptionalStandardErrorBytes)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_output_limit");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operationToken);
        linked.Token.ThrowIfCancellationRequested();
        return await _capture.RunTruncatingAsync(fileName, arguments, maximumStandardOutputBytes, maximumStandardErrorBytes, timeout, linked.Token).ConfigureAwait(false);
    }

    public async Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments,
        Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        if (maximumBytes < 1 || maximumBytes > LocalApiDiagnosticOutputIntent.MaximumInputArtifactBytes + LocalApiDiagnosticOutputIntent.MaximumBinaryFramingBytes)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_output_limit");
        // The fixed focus reader historically permits a larger source buffer.
        // Owned diagnostics use the frozen required-text budget, never parse a
        // prefix after overflow. APK body hashing remains a bounded byte stream.
        if (arguments.Count == 6 && arguments[2] == "shell" && arguments[3] == "dumpsys" &&
            arguments[4] == "window" && arguments[5] == "windows")
            maximumBytes = Math.Min(maximumBytes, LocalApiDiagnosticOutputIntent.MaximumRequiredStandardOutputBytes);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operationToken);
            linked.Token.ThrowIfCancellationRequested();
            var result = await _streaming.RunToStreamAsync(fileName, arguments, destination, maximumBytes, timeout, linked.Token).ConfigureAwait(false);
            // Legacy streaming has bounded stderr but no typed overflow bit.
            // Reject every nonempty stderr conservatively; do not infer overflow
            // from a literal marker or let focus turn it into Unknown+continue.
            if (result.CommandResult.StandardError.Length != 0)
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_stream_unusable");
            return result;
        }
        catch (FleetTransferLimitException) { throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_output_limit"); }
    }

    public async Task<ArmedCaptureCommandResult<T>> RunArmedCaptureAsync<T>(string fileName,
        IReadOnlyList<string> arguments, Stream destination, long maximumBytes, TimeSpan postActionWindow,
        Func<CancellationToken, Task<T>> armedAction, CancellationToken cancellationToken = default)
    {
        if (!SupportsArmedCapture) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_runner_unsupported");
        if (maximumBytes != 262144 || postActionWindow != TimeSpan.FromSeconds(10))
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_output_limit");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operationToken);
        linked.Token.ThrowIfCancellationRequested();
        return await _armed!.RunArmedCaptureAsync(fileName, arguments, destination, maximumBytes,
            postActionWindow, armedAction, linked.Token).ConfigureAwait(false);
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(LocalApiDiagnosticOutputIntent.MaximumProcessTimeoutSeconds))
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_timeout_invalid");
    }

    private static bool ExceedsUtf8Limit(string value, int maximumBytes) =>
        value.Length > maximumBytes || Encoding.UTF8.GetByteCount(value) > maximumBytes;
}
