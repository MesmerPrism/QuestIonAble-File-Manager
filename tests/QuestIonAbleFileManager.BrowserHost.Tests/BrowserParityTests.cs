using System.Security.Cryptography;
using System.Text;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserParityTests
{
    private static byte[] Request(string action, string fields) => Encoding.UTF8.GetBytes(
        $$"""{"requestId":"{{Guid.NewGuid():D}}","action":"{{action}}",{{fields}}} """);

    public static TheoryData<string, string, OperatorCommand> Commands => new()
    {
        { "files.list", "\"serial\":\"QUEST123\",\"path\":\"/sdcard\"", OperatorCommands.ListFiles("QUEST123", "/sdcard") },
        { "apk.list", "\"serial\":\"QUEST123\"", OperatorCommands.ListPackages("QUEST123") },
        { "apk.permissions", "\"serial\":\"QUEST123\",\"package\":\"com.example.app\"", OperatorCommands.ObservePackagePermissions("QUEST123", "com.example.app") },
        { "device.performance", "\"serial\":\"QUEST123\",\"cpu\":0,\"gpu\":5,\"confirmed\":true", OperatorCommands.SetQuestPerformance("QUEST123", 0, 5, operatorConfirmed: true) },
        { "device.performance", "\"serial\":\"QUEST123\",\"cpu\":null,\"gpu\":2,\"confirmed\":true", OperatorCommands.SetQuestPerformance("QUEST123", null, 2, operatorConfirmed: true) },
        { "device.performance-clear", "\"serial\":\"QUEST123\",\"confirmed\":true", OperatorCommands.SetQuestPerformance("QUEST123", null, null, clear: true, operatorConfirmed: true) },
        { "wifi.disconnect", "\"serial\":\"192.0.2.1:5555\",\"host\":\"192.0.2.1\",\"port\":5555,\"confirmed\":true", OperatorCommands.DisconnectWifiAdb("192.0.2.1", 5555, true) },
        { "device.actor-observe", "\"serial\":\"QUEST123\",\"pid\":123", OperatorCommands.ObserveDeviceActor("QUEST123", 123) },
        { "device.actor-identity", "\"serial\":\"QUEST123\",\"pid\":2147483647", OperatorCommands.ObserveDeviceActorIdentity("QUEST123", int.MaxValue) },
        { "adb.forwards", "\"serial\":\"QUEST123\"", OperatorCommands.InventoryAdbForwards("QUEST123") }
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void ClosedRequestsUseExactExistingCoreCliCommands(string action, string fields, OperatorCommand expected)
    {
        var request = BrowserCommands.Parse(Request(action, fields));
        Assert.Contains(action, BrowserCommands.Actions);
        Assert.Equal(expected.Kind, request.Command.Kind);
        Assert.Equal(expected.CliArguments, request.Command.CliArguments);
        Assert.Equal(expected.Serial, request.Command.Serial);
        Assert.Equal(action is "device.performance" or "device.performance-clear" or "wifi.disconnect", request.Mutation);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task UnknownFieldsCannotReachExecutor(string action, string fields, OperatorCommand _)
    {
        var registry = new BrowserCommands((_, _) => throw new Xunit.Sdk.XunitException("Invalid request dispatched"));
        var error = await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(
            Request(action, fields + ",\"shell\":\"id\"")));
        Assert.Equal("fields_invalid", error.Code);
    }

    [Theory]
    [InlineData("device.performance", "\"serial\":\"QUEST123\",\"cpu\":-1,\"gpu\":0,\"confirmed\":true")]
    [InlineData("device.performance", "\"serial\":\"QUEST123\",\"cpu\":0,\"gpu\":6,\"confirmed\":true")]
    [InlineData("device.performance", "\"serial\":\"QUEST123\",\"cpu\":null,\"gpu\":null,\"confirmed\":true")]
    [InlineData("device.performance", "\"serial\":\"QUEST123\",\"cpu\":\"2\",\"gpu\":2,\"confirmed\":true")]
    [InlineData("device.performance", "\"serial\":\"QUEST123\",\"cpu\":1.5,\"gpu\":2,\"confirmed\":true")]
    [InlineData("device.performance", "\"serial\":\"QUEST123\",\"cpu\":2,\"gpu\":2,\"confirmed\":false")]
    [InlineData("device.performance-clear", "\"serial\":\"QUEST123\",\"confirmed\":false")]
    [InlineData("device.performance-clear", "\"serial\":\"QUEST123\",\"confirmed\":true,\"cpu\":0")]
    [InlineData("wifi.disconnect", "\"serial\":\"other:5555\",\"host\":\"192.0.2.1\",\"port\":5555,\"confirmed\":true")]
    [InlineData("wifi.disconnect", "\"serial\":\"192.0.2.1:5555\",\"host\":\"192.0.2.1\",\"port\":5555,\"confirmed\":false")]
    [InlineData("wifi.disconnect", "\"serial\":\"192.0.2.1:0\",\"host\":\"192.0.2.1\",\"port\":0,\"confirmed\":true")]
    [InlineData("wifi.disconnect", "\"serial\":\"192.0.2.1:65536\",\"host\":\"192.0.2.1\",\"port\":65536,\"confirmed\":true")]
    [InlineData("device.actor-observe", "\"serial\":\"QUEST123\",\"pid\":0")]
    [InlineData("device.actor-identity", "\"serial\":\"QUEST123\",\"pid\":2147483648")]
    [InlineData("device.actor-observe", "\"serial\":\"QUEST123\",\"pid\":\"123;id\"")]
    [InlineData("apk.permissions", "\"serial\":\"QUEST123\",\"package\":\"com.example.app;id\"")]
    [InlineData("files.list", "\"serial\":\"QUEST123\",\"path\":\"C:/private\"")]
    [InlineData("files.list", "\"serial\":\"QUEST123\",\"path\":\"/sdcard\\nwhoami\"")]
    public async Task InvalidBoundsAndUnconfirmedMutationsCannotDispatch(string action, string fields)
    {
        var registry = new BrowserCommands((_, _) => throw new Xunit.Sdk.XunitException("Invalid request dispatched"));
        await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(Request(action, fields)));
    }

    [Theory]
    [InlineData("files.list", "\"path\":\"/sdcard\"")]
    [InlineData("apk.list", "")]
    [InlineData("apk.permissions", "\"package\":\"com.example.app\"")]
    [InlineData("device.performance", "\"cpu\":0,\"gpu\":5,\"confirmed\":true")]
    [InlineData("device.performance-clear", "\"confirmed\":true")]
    [InlineData("device.actor-observe", "\"pid\":123")]
    [InlineData("device.actor-identity", "\"pid\":123")]
    [InlineData("adb.forwards", "")]
    public async Task FreshDiscoveryRequiresExactReadyTargetBeforeOperation(string action, string fields)
    {
        var runner = new Runner { Discovery = "OTHER device model:Quest\nQUEST123 offline\n" };
        var registry = Host(runner);
        var result = await registry.ExecuteAsync(Request(action, "\"serial\":\"QUEST123\"" + (fields.Length == 0 ? "" : "," + fields)));
        Assert.Null(result.Result);
        Assert.Equal("operation_failed", result.Error);
        Assert.Equal(new[] { "devices", "-l" }, Assert.Single(runner.Calls));
    }

    [Fact]
    public async Task ListingAndPermissionResultsRemainTypedAndUnknownEvidenceStaysUnknown()
    {
        var runner = new Runner();
        var registry = Host(runner);
        var files = await registry.ExecuteAsync(Request("files.list", "\"serial\":\"QUEST123\",\"path\":\"/sdcard\""));
        Assert.Equal("completed", files.State);
        Assert.Equal("/sdcard/Documents", Assert.Single(files.Result!.RemoteEntries!).FullPath);
        Assert.True(files.Result.RemoteEntries![0].IsDirectory);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(new[] { "-s", "QUEST123", "shell", "ls -1Ap -- '/sdcard'" }));
        var packages = await registry.ExecuteAsync(Request("apk.list", "\"serial\":\"QUEST123\""));
        Assert.Equal("com.example.app", Assert.Single(packages.Result!.Packages!));
        var permissions = await registry.ExecuteAsync(Request("apk.permissions", "\"serial\":\"QUEST123\",\"package\":\"com.example.app\""));
        Assert.Equal("completed", permissions.State);
        var observation = Assert.IsType<ApkPermissionObservation>(permissions.Result!.ApkPermissionObservation);
        Assert.Equal(ApkPermissionObservationState.Unknown, observation.EffectiveGrantState);
        Assert.Equal(ApkPermissionObservationState.Unknown, observation.AppOpState);
        Assert.Empty(observation.EffectiveGrants);
        Assert.Null(permissions.Result.MutationReceipt);
        var forwards = await registry.ExecuteAsync(Request("adb.forwards", "\"serial\":\"QUEST123\""));
        var inventory = Assert.IsType<AdbForwardInventoryResult>(forwards.Result!.AdbForwardInventoryResult);
        Assert.Equal("tcp:8128", Assert.Single(inventory.Forwards).LocalEndpoint);
        Assert.Null(forwards.Result.MutationReceipt);
    }

    [Theory]
    [InlineData("5", "completed", OperatorMutationStage.Confirmed)]
    [InlineData("4", "pending", OperatorMutationStage.Pending)]
    public async Task PerformanceCompletionRequiresSharedCoreEffectiveReadback(string gpu, string state, OperatorMutationStage stage)
    {
        var runner = new Runner { Cpu = "0", Gpu = gpu };
        var result = await Host(runner).ExecuteAsync(Request("device.performance", "\"serial\":\"QUEST123\",\"cpu\":0,\"gpu\":5,\"confirmed\":true"));
        Assert.Equal(state, result.State);
        Assert.Equal(stage, result.Result!.MutationReceipt!.Stage);
        Assert.Equal(gpu, result.Result.QuestPerformanceResult!.EffectiveStatus.GpuLevel);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(new[] { "-s", "QUEST123", "shell", "setprop", "debug.oculus.cpuLevel", "0" }));
    }

    [Fact]
    public async Task ClearUsesExistingFixedCommandsAndIndependentReadback()
    {
        var runner = new Runner();
        var result = await Host(runner).ExecuteAsync(Request("device.performance-clear", "\"serial\":\"QUEST123\",\"confirmed\":true"));
        Assert.Equal("completed", result.State);
        Assert.True(result.Result!.QuestPerformanceResult!.Cleared);
        Assert.Equal(OperatorMutationStage.Confirmed, result.Result.MutationReceipt!.Stage);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(new[] { "-s", "QUEST123", "shell", "setprop debug.oculus.cpuLevel ''" }));
    }

    [Theory]
    [InlineData(false, "completed")]
    [InlineData(true, "outcomeUnknown")]
    public async Task DisconnectAllowsRecoveryAndRequiresEndpointAbsenceReadback(bool remains, string state)
    {
        var runner = new Runner { Discovery = remains ? "192.0.2.1:5555 offline\n" : "" };
        var registry = Host(runner);
        var request = Request("wifi.disconnect", "\"serial\":\"192.0.2.1:5555\",\"host\":\"192.0.2.1\",\"port\":5555,\"confirmed\":true");
        var result = await registry.ExecuteAsync(request);
        Assert.Equal(state, result.State);
        Assert.Equal(new[] { "disconnect", "192.0.2.1:5555" }, runner.Calls[0]);
        Assert.Equal(new[] { "devices", "-l" }, runner.Calls[1]);
        if (!remains)
        {
            Assert.Equal(OperatorMutationStage.Confirmed, result.Result!.MutationReceipt!.Stage);
            Assert.NotNull(result.Result.CommandResult);
        }
        else Assert.Null(result.Result);
        var replay = await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(request));
        Assert.Equal("request_already_consumed", replay.Code);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActorReadsUseBoundedCoreTransportAndPreserveIdentityFacts(bool identity)
    {
        var runner = new Runner { ActorIdentity = identity };
        var result = await Host(runner).ExecuteAsync(Request(identity ? "device.actor-identity" : "device.actor-observe",
            "\"serial\":\"QUEST123\",\"pid\":123"));
        Assert.Equal("completed", result.State);
        Assert.Null(result.Result!.MutationReceipt);
        Assert.Equal(new[] { "devices", "-l" }, runner.Calls[0]);
        Assert.Equal(new[] { "-s", "QUEST123", "exec-out", "sh", "-c" }, runner.Calls[1].Take(5));
        Assert.InRange(runner.StreamMaximum, 1, 17 * 1024 * 1024);
        if (identity)
        {
            var observation = Assert.IsType<DeviceActorIdentityObservation>(result.Result.DeviceActorIdentityObservation);
            Assert.Equal("QUEST123", observation.CanonicalSerial);
            Assert.Equal((ulong)987654, observation.InitialProcessStartTicks);
            Assert.Equal((ulong)987655, observation.FinalProcessStartTicks);
            Assert.Equal(new byte[] { 97, 0, 255 }, Convert.FromBase64String(observation.CommandLine.Base64));
            Assert.Equal(8, observation.RawSections.Count);
        }
        else
        {
            var observation = Assert.IsType<DeviceActorObservation>(result.Result.DeviceActorObservation);
            Assert.Equal("QUEST123", observation.CanonicalSerial);
            Assert.Equal((ulong)987654, observation.ProcessStartTicks);
            Assert.Equal(5, observation.RawSections.Count);
        }
    }

    private static BrowserCommands Host(Runner runner) => new(new OperatorCommandExecutor(new AdbClient("fake-adb", runner)));

    private sealed class Runner : IStreamingCommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public string Discovery { get; init; } = "QUEST123 device model:Quest\n";
        public string Cpu { get; init; } = "";
        public string Gpu { get; init; } = "";
        public bool ActorIdentity { get; init; }
        public long StreamMaximum { get; private set; }
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            var joined = string.Join(' ', arguments);
            var output = joined switch
            {
                "devices -l" => "List of devices attached\n" + Discovery,
                "-s QUEST123 shell ls -1Ap -- '/sdcard'" => "Documents/\n",
                "-s QUEST123 shell pm list packages -3" => "package:com.example.app\n",
                "-s QUEST123 shell pm path 'com.example.app'" => "package:/data/app/example/base.apk\n",
                "-s QUEST123 shell getprop debug.oculus.cpuLevel" => Cpu,
                "-s QUEST123 shell getprop debug.oculus.gpuLevel" => Gpu,
                "forward --list" => "QUEST123 tcp:8128 tcp:8129\nOTHER tcp:1 tcp:2\n",
                _ => ""
            };
            return Task.FromResult(new CommandResult(fileName, arguments, 0, output, "", TimeSpan.Zero));
        }

        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments,
            Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            StreamMaximum = maximumBytes;
            if (arguments.Contains("dumpsys") || arguments.Contains("appops"))
                return new(new(fileName, arguments, 0, "", "source warning", TimeSpan.Zero), 0,
                    Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant());
            var raw = ActorFrame(ActorIdentity);
            await destination.WriteAsync(raw, cancellationToken);
            return new(new(fileName, arguments, 0, "", "", TimeSpan.Zero), raw.Length,
                Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant());
        }

        private static byte[] ActorFrame(bool identity)
        {
            static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
            static string Stat(string ticks) => "123 (app) " + string.Join(' ', new[] { "S" }.Concat(Enumerable.Repeat("0", 18)).Append(ticks)) + "\n";
            byte[][] sections = [Bytes("QUEST123\n"), Bytes("11111111-2222-3333-4444-555555555555\n"),
                Bytes("UID PID PPID NAME ARGS\n10123 123 1 com.example.app com.example.app\n"), Bytes(Stat("987654"))];
            string[] names;
            if (identity)
            {
                sections = [.. sections, [97, 0, 255], Bytes("package:com.example.app uid:10123\n"),
                    Bytes("Uid:\t10123 10123 10123 10123\n"), Bytes(Stat("987655"))];
                names = ["serial", "boot", "inventory", "initial_stat", "cmdline", "package_uids", "status", "final_stat"];
            }
            else
            {
                sections = [.. sections, Bytes("Uid:\t10123 10123 10123 10123\n")];
                names = ["serial", "boot", "inventory", "stat", "status"];
            }
            using var output = new MemoryStream();
            output.Write(Bytes(identity ? "QFM_ACTOR_IDENTITY_V1\n" : "QFM_ACTOR_V1\n"));
            for (var i = 0; i < sections.Length; i++)
            {
                output.Write(Bytes($"{names[i]}:0:{sections[i].Length}\n"));
                output.Write(sections[i]);
                output.WriteByte(10);
            }
            output.Write(Bytes("END\n"));
            return output.ToArray();
        }
    }
}
