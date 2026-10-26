namespace QuestIonAbleFileManager.Core;

// A separate owned-only adapter closes inspector/identity/runtime/UID/probe
// calls without modifying public command collection or copying those algorithms.
internal sealed class DiagnosticRequiredCommandRunner : ITruncatingCommandRunner, IStreamingCommandRunner
{
    private readonly ITruncatingCommandRunner _capture;
    private readonly IStreamingCommandRunner _streaming;
    internal DiagnosticRequiredCommandRunner(ICommandRunner runner)
    {
        _capture = runner as ITruncatingCommandRunner ?? throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_runner_unsupported");
        _streaming = runner as IStreamingCommandRunner ?? throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_runner_unsupported");
    }

    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        var result = await _capture.RunTruncatingAsync(fileName, arguments,
            LocalApiDiagnosticOutputIntent.MaximumRequiredStandardOutputBytes,
            LocalApiDiagnosticOutputIntent.MaximumRequiredStandardErrorBytes, timeout, cancellationToken).ConfigureAwait(false);
        if (result.Truncated) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_output_limit");
        return result.CommandResult;
    }

    public Task<TruncatingCommandResult> RunTruncatingAsync(string fileName, IReadOnlyList<string> arguments,
        int maximumStandardOutputBytes, int maximumStandardErrorBytes, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        if (maximumStandardOutputBytes is < 1 or > LocalApiDiagnosticOutputIntent.MaximumOptionalStandardOutputBytes ||
            maximumStandardErrorBytes is < 1 or > LocalApiDiagnosticOutputIntent.MaximumOptionalStandardErrorBytes)
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_output_limit");
        return _capture.RunTruncatingAsync(fileName, arguments, maximumStandardOutputBytes, maximumStandardErrorBytes, timeout, cancellationToken);
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
            var result = await _streaming.RunToStreamAsync(fileName, arguments, destination, maximumBytes, timeout, cancellationToken).ConfigureAwait(false);
            // Legacy streaming has bounded stderr but no typed overflow bit.
            // Reject every nonempty stderr conservatively; do not infer overflow
            // from a literal marker or let focus turn it into Unknown+continue.
            if (result.CommandResult.StandardError.Length != 0)
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_stream_unusable");
            return result;
        }
        catch (FleetTransferLimitException) { throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_required_output_limit"); }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(LocalApiDiagnosticOutputIntent.MaximumProcessTimeoutSeconds))
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_timeout_invalid");
    }
}
