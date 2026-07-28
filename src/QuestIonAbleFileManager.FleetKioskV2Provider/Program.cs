using QuestIonAbleFileManager.Core;

using var cancellationSource = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};
Console.CancelKeyPress += cancelHandler;
try
{
    await using var input = Console.OpenStandardInput();
    await using var output = Console.OpenStandardOutput();
    return await RustyKioskV2CatalogSubprocessHost
        .CreateWindows()
        .RunAsync(args, input, output, cancellationSource.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
