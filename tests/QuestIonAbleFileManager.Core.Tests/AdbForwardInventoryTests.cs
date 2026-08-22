using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class AdbForwardInventoryTests
{
    private const string Serial = "QUEST123";

    [Fact]
    public void FactoryAndExactCliParserAdmitOnlyTheClosedReadOnlyRoute()
    {
        var command = OperatorCommands.InventoryAdbForwards(Serial);

        Assert.Equal(OperatorCommandKind.InventoryAdbForwards, command.Kind);
        Assert.Equal(["adb", "forwards", "--serial", Serial], command.CliArguments);
        Assert.False(command.OperatorConfirmed);
        Assert.False(OperatorMutations.RequiresHeadsetStateChange(command));

        var parsed = OperatorCommands.ParseAdbForwardInventoryCliArguments(
            ["adb", "forwards", "--serial", Serial, "--json"]);
        Assert.Equal(command.Kind, parsed.Kind);
        Assert.Equal(command.CliArguments, parsed.CliArguments);

        Assert.Throws<ArgumentException>(() => OperatorCommands.ParseAdbForwardInventoryCliArguments(
            ["adb", "forwards", "--serial", Serial, "--json", "--adb", "other"]));
        Assert.Throws<ArgumentException>(() => OperatorCommands.ParseAdbForwardInventoryCliArguments(
            ["adb", "FORWARDS", "--serial", Serial, "--json"]));
        Assert.Throws<ArgumentException>(() => OperatorCommands.ParseAdbForwardInventoryCliArguments(
            ["adb", "forwards", "--serial", "bad serial", "--json"]));
    }

    [Fact]
    public async Task InventoryUsesOneSharedCommandAndExactSerialProjection()
    {
        var runner = new RecordingCommandRunner((_, arguments) =>
            arguments.SequenceEqual(["forward", "--list"])
                ? Success("OTHER456 tcp:6100 tcp:7100\nQUEST123 tcp:7101 tcp:7102\n")
                : Failure(arguments));
        var client = new AdbClient("adb-test", runner);

        var result = await client.GetForwardInventoryAsync(Serial);

        Assert.Equal(Serial, result.RequestedSerial);
        Assert.Equal("shared-adb-forward-list filtered to requested exact serial", result.ObservationScope);
        Assert.Equal([new AdbForwardMapping("tcp:7101", "tcp:7102")], result.Forwards);
        Assert.Equal(["forward", "--list"], Assert.Single(runner.Calls).Arguments);
        Assert.DoesNotContain("-s", runner.Calls.SelectMany(static call => call.Arguments));

        var execution = await new OperatorCommandExecutor(client).ExecuteAsync(
            OperatorCommands.InventoryAdbForwards(Serial));
        Assert.NotNull(execution.AdbForwardInventoryResult);
    }

    [Fact]
    public void ParserUsesThePublicFixtureForExactFilteringAndDamage()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "QuestIonAbleFileManager.Core.Tests",
            "Fixtures",
            "adb-forward-inventory.v1.json")));
        var root = fixture.RootElement;
        Assert.Equal(
            "questionable.file_manager.adb_forward_inventory_fixture.v1",
            root.GetProperty("schema").GetString());
        Assert.Equal(Serial, root.GetProperty("requestedSerial").GetString());

        foreach (var testCase in root.GetProperty("cases").EnumerateArray())
        {
            var output = testCase.GetProperty("stdout").GetString()!;
            if (testCase.GetProperty("expected").GetString() == "rejected")
            {
                Assert.Throws<InvalidDataException>(
                    () => AdbOutputParser.ParseForwardInventory(output, Serial));
                continue;
            }

            var expected = testCase.GetProperty("forwards").EnumerateArray()
                .Select(static value => new AdbForwardMapping(
                    value.GetProperty("localEndpoint").GetString()!,
                    value.GetProperty("remoteEndpoint").GetString()!))
                .ToArray();
            Assert.Equal(expected, AdbOutputParser.ParseForwardInventory(output, Serial));
        }
    }

    [Fact]
    public async Task InventoryRejectsCommandAndStreamDamageWithoutDispatchingAnyMutation()
    {
        var commandFailure = new AdbClient("adb-test", new RecordingCommandRunner((_, _) =>
            Result(1, stderr: "daemon unavailable")));
        await Assert.ThrowsAsync<AdbCommandException>(
            () => commandFailure.GetForwardInventoryAsync(Serial));

        var stderrDamage = new AdbClient("adb-test", new RecordingCommandRunner((_, _) =>
            Success("QUEST123 tcp:6100 tcp:7100\n", "unexpected warning")));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => stderrDamage.GetForwardInventoryAsync(Serial));

        var cancelled = new AdbClient("adb-test", new RecordingCommandRunner((_, _) =>
            throw new OperationCanceledException()));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cancelled.GetForwardInventoryAsync(Serial));
    }

    private static CommandResult Success(string output = "", string stderr = "") => Result(0, output, stderr);

    private static CommandResult Result(int exitCode, string output = "", string stderr = "") =>
        new("adb-test", [], exitCode, output, stderr, TimeSpan.Zero);

    private static CommandResult Failure(IReadOnlyList<string> arguments) =>
        Result(1, stderr: "unexpected command: " + string.Join(" ", arguments));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "QuestIonAbleFileManager.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the File Manager repository root.");
    }

    private sealed class RecordingCommandRunner(
        Func<string, IReadOnlyList<string>, CommandResult> handler) : ICommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            var result = handler(fileName, arguments);
            return Task.FromResult(result with { FileName = fileName, Arguments = arguments.ToArray() });
        }
    }
}
