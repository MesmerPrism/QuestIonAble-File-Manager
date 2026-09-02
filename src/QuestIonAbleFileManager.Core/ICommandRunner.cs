namespace QuestIonAbleFileManager.Core;

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface IStreamingCommandRunner : ICommandRunner
{
    Task<StreamingCommandResult> RunToStreamAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Stream destination,
        long maximumBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<StreamingCommandResult> RunFromStreamAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Stream source,
        long maximumBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The configured command runner does not support input streaming.");
}

/// <summary>
/// Starts one fixed bounded capture process before invoking one typed action.
/// The action is not represented as command text and remains owned by the
/// caller. Implementations must stop the capture process tree, finish every
/// stream-reader task before returning or, on a bounded terminal failure,
/// revoke every pipe, destination, and digest owner before returning. Cleanup
/// uncertainty must remain typed.
/// </summary>
public interface IArmedCaptureCommandRunner : IStreamingCommandRunner
{
    Task<ArmedCaptureCommandResult<T>> RunArmedCaptureAsync<T>(
        string fileName,
        IReadOnlyList<string> arguments,
        Stream destination,
        long maximumBytes,
        TimeSpan postActionWindow,
        Func<CancellationToken, Task<T>> armedAction,
        CancellationToken cancellationToken = default);
}

public sealed record ArmedCaptureCommandResult<T>(
    T ActionResult,
    CommandResult CommandResult,
    long BytesWritten,
    string Sha256,
    bool PostActionWindowElapsed,
    bool OutputLimitReached,
    bool CaptureExitedEarly,
    bool ProcessTreeCleanupSucceeded);

/// <summary>
/// Runs a process whose standard streams may contain a short-lived credential.
/// The raw streams are bounded, passed only to the in-memory parser, and cleared
/// before this method returns. Neither stream is projected into CommandResult.
/// </summary>
public interface ISensitiveCommandRunner : ICommandRunner
{
    Task<SensitiveCommandResult<T>> RunSensitiveAsync<T>(
        string fileName,
        IReadOnlyList<string> arguments,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes,
        TimeSpan timeout,
        Func<ReadOnlyMemory<byte>, T> parseStandardOutput,
        CancellationToken cancellationToken = default);
}

public sealed record SensitiveCommandResult<T>(
    T Value,
    int ExitCode,
    TimeSpan Duration);

public sealed class SensitiveCommandException(string message) : InvalidOperationException(message);
