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
