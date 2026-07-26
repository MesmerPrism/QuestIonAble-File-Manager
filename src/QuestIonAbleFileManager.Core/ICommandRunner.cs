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
}
