using QuestIonAbleFileManager.Core;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class PackageStopProcessEvidenceTests
{
    // Reproduce a process name far beyond Linux comm without copying private actors.
    private static readonly string Package = "com.example.long_package." + new string('x', 160);

    [Theory]
    [InlineData("absent", true)]
    [InlineData("long-name-still-present", false)]
    [InlineData("late-process", false)]
    [InlineData("pid-reused", true)]
    [InlineData("same-birth", null)]
    [InlineData("shared-uid", null)]
    [InlineData("secondary-system-uid", null)]
    [InlineData("uid-drift", null)]
    [InlineData("user-drift", null)]
    [InlineData("truncated", null)]
    [InlineData("denied", null)]
    [InlineData("bad-header", null)]
    [InlineData("duplicate-pid", null)]
    [InlineData("empty-inventory", null)]
    [InlineData("isolated-conflict", null)]
    [InlineData("birth-race", null)]
    [InlineData("oversized", null)]
    public async Task FixedStopNeverUsesSilentPidofAsAbsence(string scenario, bool? verified)
    {
        var stopped = false; var stops = 0; var inventories = 0; var statReads = 0;
        var runner = new Runner(args =>
        {
            if (args.Contains("force-stop")) { stopped = true; stops++; return Result(); }
            if (args[^1].StartsWith("pm path --user current", StringComparison.Ordinal))
                return Result("package:/data/app/example/base.apk\n");
            if (args[^1] == "get-current-user") return Result(scenario == "secondary-system-uid" ? "1\n" : stopped && scenario == "user-drift" ? "10\n" : "0\n");
            if (args[^1] == AdbClient.StopProcessInventoryCommand)
            {
                inventories++;
                if (stopped && scenario == "denied") return Result("", 1, "Permission denied");
                var uid = scenario == "secondary-system-uid" ? 100100 : stopped && scenario == "uid-drift" ? 10227 : 10226;
                var packages = $"package:{Package} uid:{uid}\n";
                if (scenario == "shared-uid") packages += "package:com.example.shared uid:10226\n";
                var rows = "0 1 init\n";
                if (!stopped || scenario == "long-name-still-present" || scenario == "late-process" && inventories == 3)
                    rows += $"10226 17527 {Package}\n";
                if (stopped && scenario is "pid-reused" or "same-birth") rows += "2000 17527 unrelated\n";
                if (stopped && scenario == "isolated-conflict") rows += $"99001 99 {Package}:isolated\n";
                if (stopped && scenario == "duplicate-pid") rows += "0 1 duplicate\n";
                if (stopped && scenario == "empty-inventory") rows = "";
                var header = stopped && scenario == "bad-header" ? "PID NAME" : "UID PID ARGS";
                var tail = stopped && scenario == "truncated" ? "" : "QFM_STOP_PS_END=0\n";
                var text = $"QFM_STOP_UID_BEGIN\n{packages}QFM_STOP_UID_END=0\nQFM_STOP_PS_BEGIN\n{header}\n{rows}{tail}";
                if (stopped && scenario == "oversized") text += new string('x', 262145);
                return Result(text);
            }
            if (args[^1] == "/proc/17527/stat")
            {
                statReads++;
                var ticks = stopped && scenario == "pid-reused" ? 200 : scenario == "birth-race" && statReads == 2 ? 101 : 100;
                return Result("17527 (long package) S " + string.Join(" ", Enumerable.Repeat("0", 18)) + $" {ticks}\n");
            }
            if (args[^1] == "/proc/17527/status") return Result(stopped ? "Uid:\t2000\t2000\t2000\t2000\n" : "Uid:\t10226\t10226\t10226\t10226\n");
            if (args.Contains("activities")) return Result("mResumedActivity: ActivityRecord{1 u0 com.other/.Main}\n");
            if (args.Contains("pidof")) return Result("", 1);
            throw new InvalidOperationException(string.Join(" ", args));
        });
        var result = await new OperatorCommandExecutor(new AdbClient("adb-test", runner))
            .ExecuteAsync(OperatorCommands.StopPackage("QUEST123", Package, true));
        Assert.Equal(1, stops);
        Assert.Equal(verified, result.PackageStopResult!.Quiescence.ProcessAbsenceVerified);
        Assert.Equal(verified == true ? OperatorMutationStage.Confirmed : OperatorMutationStage.Pending, result.MutationReceipt!.Stage);
        if (verified == true)
        {
            var proof = result.PackageStopResult.Quiescence.ProcessEvidence!;
            Assert.Equal(10226, proof.PackageUid); Assert.Equal(0, proof.CurrentUser);
            Assert.Equal([new PackageStopProcessIdentity(17527, 100)], proof.BeforeDispatch);
            Assert.True(proof.KnownBirthsRetired); Assert.Equal(2, proof.CompletePostInventories);
        }
    }

    [Fact]
    public void OldOrUnsupportedEvidenceDoesNotConfirmQuiescence()
    {
        var old = new PackageStopQuiescence([], [], []);
        Assert.False(old.IsQuiescent);
        Assert.False((old with { ProcessAbsenceVerified = true }).IsQuiescent);
    }

    [Theory]
    [InlineData(0, 110226, "[]")]
    [InlineData(1, 100100, "[]")]
    [InlineData(0, 10226, "null")]
    [InlineData(0, 10226, "[null]")]
    [InlineData(0, 10226, "[{\"ProcessId\":0,\"StartTicks\":100}]")]
    [InlineData(0, 10226, "[{\"ProcessId\":17527,\"StartTicks\":0}]")]
    [InlineData(0, 10226, "[{\"ProcessId\":17527,\"StartTicks\":100},{\"ProcessId\":17527,\"StartTicks\":200}]")]
    public void MalformedDeserializedEvidenceCannotConfirm(int user, int uid, string births)
    {
        var json = "{\"ProcessIds\":[],\"ForegroundComponents\":[],\"TopResumedComponents\":[],\"ProcessAbsenceVerified\":true,\"ProcessEvidence\":{" +
            $"\"CurrentUser\":{user},\"PackageUid\":{uid},\"BeforeDispatch\":{births}," +
            "\"KnownBirthsRetired\":true,\"CompletePostInventories\":2}}";
        var decoded = JsonSerializer.Deserialize<PackageStopQuiescence>(json)!;
        Assert.False(decoded.IsQuiescent);
        Assert.Equal("unverified", decoded.ProcessEvidence!.Disposition);
    }

    private static CommandResult Result(string text = "", int exit = 0, string error = "") => new("adb-test", [], exit, text, error, TimeSpan.Zero);
    private sealed class Runner(Func<IReadOnlyList<string>, CommandResult> read) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(read(args)); }
    }
}
