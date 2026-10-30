using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserPackageStopTests
{
    private const string Serial = "QUEST123";
    private const string Package = "com.example.app";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void PackageStopAdmitsOnlyTheExistingExactTypedCoreCommand()
    {
        var requestId = Guid.NewGuid().ToString("D");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            requestId, action = "apk.stop", serial = "QUEST123",
            package = "com.example.app", confirmed = true
        });

        var request = BrowserCommands.Parse(body);

        Assert.Equal(requestId, request.RequestId);
        Assert.Equal("apk.stop", request.Action);
        Assert.True(request.Mutation);
        Assert.Equal(OperatorCommandKind.StopPackage, request.Command.Kind);
        Assert.Equal(OperatorCommands.StopPackage("QUEST123", "com.example.app", true).CliArguments,
            request.Command.CliArguments);
        Assert.Equal("QUEST123", request.Command.Serial);
        Assert.Equal("com.example.app", request.Command.PackageName);
        Assert.True(request.Command.OperatorConfirmed);
    }

    [Theory]
    [InlineData("\"confirmed\":false")]
    [InlineData("\"confirmed\":\"true\"")]
    [InlineData("\"confirmed\":true,\"user\":\"current\"")]
    [InlineData("\"confirmed\":true,\"component\":\"com.example.app/.Main\"")]
    [InlineData("\"confirmed\":true,\"shell\":\"am force-stop\"")]
    [InlineData("\"confirmed\":true,\"package\":\"com.example.other\"")]
    [InlineData("\"Confirmed\":true")]
    [InlineData("")]
    public async Task InvalidStopRequestsNeverReachExecutor(string approval)
    {
        var calls = 0;
        var commands = new BrowserCommands((command, _) =>
        {
            calls++;
            return Task.FromResult(new OperatorExecutionResult(command));
        });
        var text = $$"""{"requestId":"{{Guid.NewGuid():D}}","action":"apk.stop","serial":"QUEST123","package":"com.example.app"{{(approval.Length == 0 ? "" : "," + approval)}}} """;
        await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RealCoreReceiptAndCoherentReadbackBecomeOnlyTheClosedSafeProjection()
    {
        var runner = new StopRunner();
        var commands = new BrowserCommands(new OperatorCommandExecutor(new AdbClient("adb-test", runner)));
        Assert.Contains("apk.stop", commands.AvailableActions);

        var outcome = await commands.ExecuteAsync(Request());

        Assert.Equal("completed", outcome.State);
        Assert.Null(outcome.Result);
        var stop = Assert.IsType<BrowserPackageStopOutcome>(outcome.PackageStop);
        Assert.Equal("questionable.file_manager.browser_package_stop.v1", stop.ContractVersion);
        Assert.Equal(Serial, stop.Serial);
        Assert.Equal(Package, stop.PackageName);
        Assert.True(stop.PackagePresentBeforeDispatch);
        Assert.True(stop.PackagePresentAfterDispatch);
        Assert.True(stop.ProcessAbsenceVerified);
        Assert.Equal("verified_absent", stop.ProcessEvidenceDisposition);
        Assert.Equal(0, stop.ProcessCount);
        Assert.Equal(0, stop.ForegroundComponentCount);
        Assert.Equal(0, stop.TopResumedComponentCount);
        Assert.True(stop.IsQuiescent);
        var receipt = Assert.IsType<OperatorMutationReceipt>(outcome.Mutation);
        Assert.Equal(OperatorCommandKind.StopPackage, receipt.CommandKind);
        Assert.Equal(Serial, receipt.Target);
        Assert.Equal("package-stop", receipt.DesiredState);
        Assert.Equal("package-quiescent", receipt.ObservedState);
        Assert.True(receipt.HeadsetReadback);
        Assert.Equal(new[] { "package-stop-sent", "package-stop-pending", "package-stop-confirmed" },
            receipt.Transitions.Select(transition => transition.Message));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(outcome, Json));
        Assert.Equal(new[] { "contractVersion", "serial", "packageName", "packagePresentBeforeDispatch",
            "packagePresentAfterDispatch", "processAbsenceVerified", "processEvidenceDisposition", "processCount",
            "foregroundComponentCount", "topResumedComponentCount", "isQuiescent" }.Order(),
            json.RootElement.GetProperty("packageStop").EnumerateObject().Select(property => property.Name).Order());
        var serialized = json.RootElement.GetRawText();
        Assert.DoesNotContain("private-output-marker", serialized);
        Assert.DoesNotContain("force-stop", serialized);
        Assert.DoesNotContain("processIds", serialized);
        Assert.DoesNotContain("processEvidence\"", serialized);
        Assert.DoesNotContain("/data/app", serialized);
        Assert.Single(runner.Calls, call => call.Contains("force-stop"));
        Assert.Equal(2, runner.Calls.Count(call => call.Contains("pm path --user current 'com.example.app'")));
    }

    [Fact]
    public async Task ReceiptIdentityStagesAndTimesArePreservedButEverySourceMessageIsReplaced()
    {
        var source = Result();
        var receipt = source.MutationReceipt! with
        {
            ObservedState = "private-output-marker /private/source",
            Transitions = source.MutationReceipt!.Transitions.Select(transition =>
                transition with { Message = "private-output-marker /private/source" }).ToArray()
        };
        source = source with { MutationReceipt = receipt };
        var commands = Commands(source);

        var outcome = await commands.ExecuteAsync(Request());

        Assert.Equal("completed", outcome.State);
        Assert.Equal(receipt.OperationId, outcome.Mutation!.OperationId);
        Assert.Equal(receipt.Stage, outcome.Mutation.Stage);
        Assert.Equal(receipt.Transitions.Select(transition => (transition.Stage, transition.At)),
            outcome.Mutation.Transitions.Select(transition => (transition.Stage, transition.At)));
        Assert.DoesNotContain("private-output-marker", JsonSerializer.Serialize(outcome, Json));
    }

    [Theory]
    [InlineData("old-proof")]
    [InlineData("unverified-proof")]
    [InlineData("incoherent-proof")]
    [InlineData("process-present")]
    [InlineData("foreground-present")]
    [InlineData("top-resumed-present")]
    [InlineData("quiescent-but-pending-receipt")]
    public async Task ActualPendingReceiptStaysPendingAndCannotInventConfirmation(string evidence)
    {
        var result = Result(OperatorMutationStage.Pending);
        var stop = result.PackageStopResult!;
        var quiescence = stop.Quiescence;
        quiescence = evidence switch
        {
            "old-proof" => new([], [], []),
            "unverified-proof" => quiescence with { ProcessAbsenceVerified = false },
            "incoherent-proof" => quiescence with { ProcessEvidence = quiescence.ProcessEvidence! with { CompletePostInventories = 1 } },
            "process-present" => quiescence with { ProcessIds = [12345] },
            "foreground-present" => quiescence with { ForegroundComponents = ["com.example.app/.Main"] },
            "top-resumed-present" => quiescence with { TopResumedComponents = ["com.example.app/.Main"] },
            _ => quiescence
        };
        result = result with { PackageStopResult = stop with { Quiescence = quiescence } };

        var outcome = await Commands(result).ExecuteAsync(Request());

        Assert.Equal("pending", outcome.State);
        Assert.Equal(OperatorMutationStage.Pending, outcome.Mutation!.Stage);
        Assert.Equal("package-stop-pending", outcome.Mutation.ObservedState);
        Assert.Equal(quiescence.IsQuiescent, outcome.PackageStop!.IsQuiescent);
        Assert.Equal(quiescence.ProcessAbsenceVerified, outcome.PackageStop.ProcessAbsenceVerified);
        Assert.Null(outcome.Result);
    }

    [Theory]
    [InlineData("command-kind")]
    [InlineData("command-serial")]
    [InlineData("command-package")]
    [InlineData("missing-stop")]
    [InlineData("stop-serial")]
    [InlineData("stop-package")]
    [InlineData("before-presence")]
    [InlineData("after-presence")]
    [InlineData("stop-exit")]
    [InlineData("stop-arguments")]
    [InlineData("missing-receipt")]
    [InlineData("receipt-kind")]
    [InlineData("receipt-target")]
    [InlineData("receipt-desired")]
    [InlineData("receipt-id")]
    [InlineData("receipt-readback")]
    [InlineData("receipt-stage")]
    [InlineData("receipt-final-stage")]
    [InlineData("receipt-history")]
    [InlineData("receipt-order")]
    [InlineData("receipt-empty")]
    [InlineData("receipt-default-time")]
    [InlineData("missing-quiescence")]
    [InlineData("old-proof")]
    [InlineData("false-proof")]
    [InlineData("incoherent-proof")]
    [InlineData("process-present")]
    public async Task InvalidOrUnprovenConfirmedBindingsRemainUnknownWithoutLeakingAnything(string mismatch)
    {
        var result = Result();
        var stop = result.PackageStopResult!;
        var receipt = result.MutationReceipt!;
        result = mismatch switch
        {
            "command-kind" => result with { Command = OperatorCommands.ListPackages(Serial) },
            "command-serial" => result with { Command = OperatorCommands.StopPackage("QUEST456", Package, true) },
            "command-package" => result with { Command = OperatorCommands.StopPackage(Serial, "com.example.other", true) },
            "missing-stop" => result with { PackageStopResult = null },
            "stop-serial" => result with { PackageStopResult = stop with { Serial = "QUEST456" } },
            "stop-package" => result with { PackageStopResult = stop with { PackageName = "com.example.other" } },
            "before-presence" => result with { PackageStopResult = stop with { PackagePresentBeforeDispatch = false } },
            "after-presence" => result with { PackageStopResult = stop with { PackagePresentAfterDispatch = false } },
            "stop-exit" => result with { PackageStopResult = stop with { StopCommand = stop.StopCommand with { ExitCode = 1 } } },
            "stop-arguments" => result with { PackageStopResult = stop with { StopCommand = stop.StopCommand with { Arguments = ["shell", "am", "force-stop", Package] } } },
            "missing-receipt" => result with { MutationReceipt = null },
            "receipt-kind" => result with { MutationReceipt = receipt with { CommandKind = OperatorCommandKind.InstallApk } },
            "receipt-target" => result with { MutationReceipt = receipt with { Target = "QUEST456" } },
            "receipt-desired" => result with { MutationReceipt = receipt with { DesiredState = "other-package" } },
            "receipt-id" => result with { MutationReceipt = receipt with { OperationId = "private-output-marker" } },
            "receipt-readback" => result with { MutationReceipt = receipt with { HeadsetReadback = false } },
            "receipt-stage" => result with { MutationReceipt = receipt with { Stage = OperatorMutationStage.Failed } },
            "receipt-final-stage" => result with { MutationReceipt = receipt with { Transitions = receipt.Transitions.Take(2).ToArray() } },
            "receipt-history" => result with { MutationReceipt = receipt with { Transitions = receipt.Transitions.Select(transition => transition with { Stage = OperatorMutationStage.Confirmed }).ToArray() } },
            "receipt-order" => result with { MutationReceipt = receipt with { Transitions = receipt.Transitions.Reverse().ToArray() } },
            "receipt-empty" => result with { MutationReceipt = receipt with { Transitions = [] } },
            "receipt-default-time" => result with { MutationReceipt = receipt with { Transitions = receipt.Transitions.Select(transition => transition with { At = default }).ToArray() } },
            "missing-quiescence" => result with { PackageStopResult = stop with { Quiescence = null! } },
            "old-proof" => result with { PackageStopResult = stop with { Quiescence = new([], [], []) } },
            "false-proof" => result with { PackageStopResult = stop with { Quiescence = stop.Quiescence with { ProcessAbsenceVerified = false } } },
            "incoherent-proof" => result with { PackageStopResult = stop with { Quiescence = stop.Quiescence with { ProcessEvidence = stop.Quiescence.ProcessEvidence! with { CompletePostInventories = 1 } } } },
            "process-present" => result with { PackageStopResult = stop with { Quiescence = stop.Quiescence with { ProcessIds = [12345] } } },
            _ => throw new InvalidOperationException("Unknown test fixture")
        };

        var commands = Commands(result);
        var outcome = await commands.ExecuteAsync(Request());

        Assert.Equal("outcomeUnknown", outcome.State);
        Assert.Equal("package_stop_binding_mismatch", outcome.Error);
        Assert.Null(outcome.Result);
        Assert.Null(outcome.PackageStop);
        Assert.Null(outcome.Mutation);
        Assert.Equal(outcome, commands.Get(outcome.RequestId));
        Assert.DoesNotContain("private-output-marker", JsonSerializer.Serialize(outcome, Json));
    }

    [Fact]
    public async Task AdmittedExceptionsNeverExposeTheirSourceReceiptOrInventConfirmation()
    {
        var commands = new BrowserCommands((_, _) => throw new OperatorMutationExecutionException(
            Result().MutationReceipt!, new IOException("private-output-marker")));

        var outcome = await commands.ExecuteAsync(Request());

        Assert.Equal("outcomeUnknown", outcome.State);
        Assert.Equal("package_stop_outcome_unknown", outcome.Error);
        Assert.Null(outcome.Result);
        Assert.Null(outcome.Mutation);
        Assert.Null(outcome.PackageStop);
        Assert.DoesNotContain("private-output-marker", JsonSerializer.Serialize(outcome, Json));
    }

    [Fact]
    public async Task StopRetainsItsOriginalTaskGateAndRequestUntilActualSettlementAfterShutdownCancellation()
    {
        using var stopping = new CancellationTokenSource();
        var gate = new BrowserOperationGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var commands = new BrowserCommands(async (_, cancellation) =>
        {
            calls++;
            // Exact token forwarding also excludes an implicit elapsed timer.
            Assert.Equal(stopping.Token, cancellation);
            using var registration = cancellation.Register(() => cancelled.TrySetResult());
            entered.SetResult();
            await settled.Task;
            cancellation.ThrowIfCancellationRequested();
            return Result();
        }, operationGate: gate);
        var body = Request();
        var request = BrowserCommands.Parse(body);
        var original = commands.ExecuteAsync(body, stopping.Token);
        await entered.Task;
        Assert.Equal("running", commands.Get(request.RequestId).State);
        Assert.Null(commands.Get(request.RequestId).PackageStop);
        Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(
            () => commands.ExecuteAsync(body))).Code);
        Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(
            () => commands.ExecuteAsync(Request()))).Code);
        var drain = commands.DrainPackageStopsAsync();

        stopping.Cancel();
        await cancelled.Task;

        Assert.True(gate.IsBusy);
        Assert.False(original.IsCompleted);
        Assert.False(drain.IsCompleted);
        Assert.Equal("running", commands.Get(request.RequestId).State);
        settled.SetResult();
        Assert.Equal("outcomeUnknown", (await original).State);
        await drain;
        Assert.False(gate.IsBusy);
        Assert.Equal(1, calls);
        Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(
            () => commands.ExecuteAsync(body))).Code);
        Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(
            () => commands.ExecuteAsync(Request()))).Code);
    }

    [Fact]
    public async Task AbandonedWaitDoesNotCancelOriginalOrAllowAnySecondOperation()
    {
        var gate = new BrowserOperationGate();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new BrowserCommands(async (_, cancellation) =>
        {
            Assert.Equal(CancellationToken.None, cancellation);
            await finish.Task;
            return Result();
        }, operationGate: gate);
        var other = new BrowserCommands((command, _) => Task.FromResult(new OperatorExecutionResult(command)), operationGate: gate);
        var body = Request();
        var id = BrowserCommands.Parse(body).RequestId;
        var original = commands.ExecuteAsync(body);
        using var disconnectedWait = new CancellationTokenSource();
        disconnectedWait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.WaitAsync(disconnectedWait.Token));
        Assert.False(original.IsCompleted);
        Assert.Equal("running", commands.Get(id).State);
        var read = JsonSerializer.SerializeToUtf8Bytes(new { requestId = Guid.NewGuid().ToString("D"), action = "devices" });
        Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => other.ExecuteAsync(read))).Code);
        finish.SetResult();
        Assert.Equal("completed", (await original).State);
        Assert.Equal("completed", commands.Get(id).State);
        Assert.Equal("completed", (await other.ExecuteAsync(read)).State);
    }

    [Fact]
    public async Task PackageStopUsesTheExistingBoundedRequestCapacity()
    {
        var calls = 0;
        var commands = new BrowserCommands((_, _) => { calls++; return Task.FromResult(Result()); });
        for (var i = 0; i < BrowserCommands.MaximumRequests; i++)
            Assert.Equal("completed", (await commands.ExecuteAsync(Request())).State);
        Assert.Equal("session_capacity_reached", (await Assert.ThrowsAsync<BrowserRequestException>(
            () => commands.ExecuteAsync(Request()))).Code);
        Assert.Equal(BrowserCommands.MaximumRequests, calls);
    }

    private static byte[] Request() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        requestId = Guid.NewGuid().ToString("D"), action = "apk.stop", serial = Serial,
        package = Package, confirmed = true
    });

    private static BrowserCommands Commands(OperatorExecutionResult result) => new((_, _) => Task.FromResult(result));

    private static OperatorExecutionResult Result(OperatorMutationStage stage = OperatorMutationStage.Confirmed)
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(-1);
        var quiescence = new PackageStopQuiescence([], [], [])
        {
            ProcessAbsenceVerified = true,
            ProcessEvidence = new(0, 10226, [new(12345, 123456)], true, 2)
        };
        return new(OperatorCommands.StopPackage(Serial, Package, true),
            PackageStopResult: new(Serial, Package, true, true,
                new("adb-test", ["-s", Serial, "shell", "am", "force-stop", "--user", "current", Package],
                    0, "private-output-marker", "private-output-marker", TimeSpan.Zero), quiescence),
            MutationReceipt: new("pc-" + Guid.NewGuid().ToString("N"), OperatorCommandKind.StopPackage, Serial,
                $"exact package {Package} quiescent for the current Android user on {Serial}", stage,
                "private-output-marker", true,
                [new(OperatorMutationStage.Sent, at, "private-output-marker"),
                 new(OperatorMutationStage.Pending, at.AddMilliseconds(1), "private-output-marker"),
                 new(stage, at.AddMilliseconds(2), "private-output-marker")]));
    }

    private sealed class StopRunner : ICommandRunner
    {
        private const string ProcessInventory =
            "printf 'QFM_STOP_UID_BEGIN\\n'; pm list packages --user current -U; " +
            "r=$?; printf 'QFM_STOP_UID_END=%s\\n' \"$r\"; " +
            "printf 'QFM_STOP_PS_BEGIN\\n'; ps -A -o UID,PID,ARGS; " +
            "r=$?; printf 'QFM_STOP_PS_END=%s\\n' \"$r\"; exit \"$r\"";
        public List<string[]> Calls { get; } = [];
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            CommandResult Reply(int exit = 0, string output = "", string error = "") =>
                new(fileName, arguments.ToArray(), exit, output, error, TimeSpan.Zero);
            if (arguments.SequenceEqual(["devices", "-l"]))
                return Task.FromResult(Reply(output: "List of devices attached\nQUEST123 device product:hollywood model:Quest_2 device:hollywood\n"));
            Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
            if (arguments.SequenceEqual(["-s", Serial, "shell", "pm path --user current 'com.example.app'"]))
                return Task.FromResult(Reply(output: "package:/data/app/example/base.apk\n"));
            if (arguments.SequenceEqual(["-s", Serial, "shell", "am", "force-stop", "--user", "current", Package]))
                return Task.FromResult(Reply(output: "private-output-marker", error: "private-output-marker"));
            if (arguments.SequenceEqual(["-s", Serial, "shell", "dumpsys", "activity", "activities"]))
                return Task.FromResult(Reply());
            if (arguments.SequenceEqual(["-s", Serial, "shell", "pidof", Package]))
                return Task.FromResult(Reply(1));
            if (arguments.SequenceEqual(["-s", Serial, "shell", "am", "get-current-user"]))
                return Task.FromResult(Reply(output: "0\n"));
            if (arguments.SequenceEqual(["-s", Serial, "shell", ProcessInventory]))
                return Task.FromResult(Reply(output: "QFM_STOP_UID_BEGIN\npackage:com.example.app uid:10226\nQFM_STOP_UID_END=0\nQFM_STOP_PS_BEGIN\nUID PID ARGS\n0 1 init\nQFM_STOP_PS_END=0\n"));
            throw new InvalidOperationException("Unexpected fake runner command");
        }
    }
}
