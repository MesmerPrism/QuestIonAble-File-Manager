using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class DeviceActorObservationTests
{
    private const string Boot = "11111111-2222-3333-4444-555555555555";
    private static string Stat => "123 (name with ) parentheses) " + string.Join(' ', new[] { "S" }.Concat(Enumerable.Repeat("0", 18)).Append("987654"));
    private static string[] Sections => ["QUEST123\n", Boot + "\n", "UID PID PPID NAME ARGS\n0 1 0 init init\n10123 123 1 com.example.app com.example.app\n", Stat + "\n", "Name:\tapp\nUid:\t10123 10123 10123 10123\n"];
    private static byte[] Frame(string[]? sections = null)
    {
        sections ??= Sections;
        using var output = new MemoryStream();
        void Put(string value) => output.Write(Encoding.UTF8.GetBytes(value));
        Put("QFM_ACTOR_V1\n");
        var names = new[] { "serial", "boot", "inventory", "stat", "status" };
        for (var i = 0; i < 5; i++)
        {
            var raw = Encoding.UTF8.GetBytes(sections[i]);
            Put($"{names[i]}:0:{raw.Length}\n"); output.Write(raw); Put("\n");
        }
        Put("END\n"); return output.ToArray();
    }

    [Fact]
    public async Task TypedExecutorUsesExactlyOneBoundedScopedReadAndRetainsAllRawFacts()
    {
        var raw = Frame(); var runner = new StreamRunner(raw);
        var command = OperatorCommands.ParseDeviceActorObservationCliArguments(["device", "actor-observe", "--serial", "QUEST123", "--pid", "123", "--json"]);
        var result = (await new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(command)).DeviceActorObservation!;
        Assert.False(OperatorMutations.RequiresHeadsetStateChange(command));
        Assert.False(command.OperatorConfirmed);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(["-s", "QUEST123", "exec-out", "sh", "-c"], call.Take(5));
        Assert.Equal(AdbClient.BuildActorObservationScript(123), call[5]);
        Assert.Equal(AdbClient.ActorObservationMaximumBytes, runner.MaximumBytes);
        Assert.Equal("QUEST123", result.CanonicalSerial); Assert.Equal(Boot, result.BootId);
        Assert.Equal(2, result.Processes.Count); Assert.Equal((uint)0, result.Processes[0].Uid);
        Assert.Equal((ulong)987654, result.ProcessStartTicks);
        Assert.Equal(new uint[] { 10123, 10123, 10123, 10123 }, result.ProcessUids);
        Assert.Equal(5, result.RawSections.Count);
        for (var i = 0; i < 5; i++)
            Assert.Equal(Encoding.UTF8.GetBytes(Sections[i]), Convert.FromBase64String(result.RawSections[i].Base64));
        Assert.Contains("not an atomic", result.ObservationScope);
        Assert.Equal(raw.Length, result.Capture.BytesWritten);
    }

    [Theory]
    [InlineData("0")][InlineData("-1")][InlineData("01")][InlineData("+123")]
    [InlineData("123;id")][InlineData("2147483648")][InlineData("1/../../status")]
    public void PidAndFreeFormInputsRefuseBeforeTransport(string pid)
    {
        Assert.Throws<ArgumentException>(() => OperatorCommands.ParseDeviceActorObservationCliArguments(
            ["device", "actor-observe", "--serial", "QUEST123", "--pid", pid, "--json"]));
    }

    [Fact]
    public async Task InvalidSerialAndUnsupportedRunnerRefuseBeforeAnyCommand()
    {
        var runner = new StreamRunner(Frame()); var client = new AdbClient("adb-test", runner);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ObserveDeviceActorAsync("bad serial", 123));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ObserveDeviceActorAsync("QUEST123", 0));
        Assert.Empty(runner.Calls);
        Assert.Throws<ArgumentException>(() => OperatorCommands.ParseDeviceActorObservationCliArguments(
            ["device", "actor-observe", "--serial", "QUEST123", "--pid", "123", "--json", "--path", "/proc"]));
        Assert.Throws<ArgumentException>(() => OperatorCommands.ParseDeviceActorObservationCliArguments(
            ["device", "ACTOR-OBSERVE", "--serial", "QUEST123", "--pid", "123", "--json"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AdbClient("adb-test", new NoRunner()).ObserveDeviceActorAsync("QUEST123", 123));
    }

    [Fact]
    public async Task MalformedDuplicateAndPartialInventoriesAreNeverFilteredIntoSuccess()
    {
        foreach (var inventory in new[] { "UID PID ARGS\n10123 123 app\n", "UID PID PPID NAME ARGS\n", "UID PID PPID NAME ARGS\nbad row\n", Sections[2]+"10124 123 1 other other\n", Sections[2]+"u0_a123 999 1 other other\n" })
        {
            var sections = Sections; sections[2] = inventory;
            await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("adb-test", new StreamRunner(Frame(sections))).ObserveDeviceActorAsync("QUEST123", 123));
        }
    }

    [Fact]
    public async Task CallerPolicyFactsRemainVisibleIncludingForeignUidAdditionalActorAndDifferentUids()
    {
        var sections = Sections;
        sections[0] = "OTHER\n"; sections[1] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\n";
        sections[2] += "10123 456 1 com.example.app:other com.example.app:other\n10191 777 1 protected protected\n";
        sections[4] = "Uid:\t10123 10124 10125 10126\n";
        var result = await new AdbClient("adb-test", new StreamRunner(Frame(sections))).ObserveDeviceActorAsync("QUEST123", 123);
        Assert.Equal("OTHER", result.CanonicalSerial); Assert.NotEqual(Boot, result.BootId);
        Assert.Equal(4, result.Processes.Count); Assert.Equal(new uint[] { 10123, 10124, 10125, 10126 }, result.ProcessUids);
        // Observation is not caller admission: no protected UID/package/expected actor API.
    }

    [Fact]
    public async Task FramingUsesByteLengthsSoInventoryArgumentsCannotForgeSections()
    {
        var sections = Sections; sections[2] += "10124 234 1 other status:0:5 END QFM_ACTOR_V1 café\n";
        var result = await new AdbClient("adb-test", new StreamRunner(Frame(sections))).ObserveDeviceActorAsync("QUEST123", 123);
        Assert.Contains("café", result.Processes[^1].Arguments); Assert.Equal(5, result.RawSections.Count);
        var valid = Encoding.UTF8.GetString(Frame());
        foreach (var damaged in new[] { valid[..^1], valid+"junk", valid.Replace("boot:0:","serial:0:"), valid.Replace("inventory:0:","inventory:1:"), valid.Replace("stat:0:","stat:0:999999"), valid.Replace("QFM_ACTOR_V1","OTHER") })
            await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("adb-test", new StreamRunner(Encoding.UTF8.GetBytes(damaged))).ObserveDeviceActorAsync("QUEST123", 123));
    }

    [Fact]
    public async Task WrongProcPidBirthUidAndUtf8OrSectionBoundsAreRefused()
    {
        foreach (var (section, value) in new[] { (0,"bad serial\n"),(1,"not-a-boot\n"),(3,Stat.Replace("123 (","124 (")),(3,"123 (name) S 0"),(4,"Uid: no\n"),(4,Sections[4]+"Uid: 1 1 1 1\n"),(0,new string('a',257)) })
        {
            var sections = Sections; sections[section] = value;
            await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("adb-test", new StreamRunner(Frame(sections))).ObserveDeviceActorAsync("QUEST123",123));
        }
        var bytes = Frame(); bytes[Array.IndexOf(bytes,(byte)'Q',20)] = 255;
        await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("adb-test",new StreamRunner(bytes)).ObserveDeviceActorAsync("QUEST123",123));
    }

    [Fact]
    public async Task NonzeroStderrCountDigestCancellationAndOverflowNeverReturnObservationOrRetry()
    {
        foreach (var failure in new[] { "exit", "stderr", "count", "digest", "cancel", "limit" })
        {
            var runner = new StreamRunner(Frame(),failure);
            await Assert.ThrowsAnyAsync<Exception>(() => new AdbClient("adb-test",runner).ObserveDeviceActorAsync("QUEST123",123));
            Assert.Single(runner.Calls);
        }
    }

    [Fact]
    public async Task RealCliHandlerAndDispatcherEmitOneTypedEnvelopeAndRefuseBeforeClientCreation()
    {
        var original = Console.Out; using var output = new StringWriter(); var created = 0;
        try
        {
            Console.SetOut(output);
            var runner = new StreamRunner(Frame());
            Assert.Equal(0, await CliApplication.RunDeviceActorObservationJsonAsync(
                ["device","actor-observe","--serial","QUEST123","--pid","123","--json"],
                () => { created++; return new AdbClient("adb-test",runner); }));
            Assert.Single(runner.Calls); Assert.Equal(1,created);
            using (var parsed = JsonDocument.Parse(output.ToString()))
                Assert.True(parsed.RootElement.GetProperty("succeeded").GetBoolean());
            output.GetStringBuilder().Clear();
            Assert.Equal(2,await CliApplication.RunAsync(["device","actor-observe","--serial","PRIVATE","--pid","1;bad","--json"]));
            using var refused = JsonDocument.Parse(output.ToString());
            Assert.False(refused.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.False(refused.RootElement.GetProperty("failure").GetProperty("state_change_possible").GetBoolean());
            Assert.DoesNotContain("PRIVATE",output.ToString());
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public async Task FinalInventoryArgumentsRetainTrailingSpacesForCallerPolicy()
    {
        var sections = Sections;
        sections[2] = "UID PID PPID NAME ARGS\n10123 123 1 com.example.app com.example.app \n";
        var result = await new AdbClient("adb-test", new StreamRunner(Frame(sections)))
            .ObserveDeviceActorAsync("QUEST123", 123);
        var row = Assert.Single(result.Processes);
        Assert.Equal("com.example.app ", row.Arguments);
        Assert.NotEqual("com.example.app", row.Arguments);
        Assert.Equal(sections[2], Encoding.UTF8.GetString(Convert.FromBase64String(result.RawSections[2].Base64)));
    }

    [Fact]
    public async Task ExecOutHostZeroWithFailedMiddleFrameAndNoEndIsCliFailureWithRawEvidence()
    {
        var raw = Encoding.UTF8.GetBytes("QFM_ACTOR_V1\nserial:0:9\nQUEST123\n\nboot:17:0\n\n");
        var runner = new StreamRunner(raw); // realistic exec-out: host0 despite remote read17
        var original = Console.Out; using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(1, await CliApplication.RunDeviceActorObservationJsonAsync(
                ["device", "actor-observe", "--serial", "QUEST123", "--pid", "123", "--json"],
                () => new AdbClient("adb-test", runner)));
        }
        finally { Console.SetOut(original); }
        Assert.Single(runner.Calls);
        using var document = JsonDocument.Parse(output.ToString()); var envelope = document.RootElement;
        Assert.False(envelope.GetProperty("succeeded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("result").ValueKind);
        var failure = envelope.GetProperty("failure");
        Assert.Equal("actor_output_rejected", failure.GetProperty("code").GetString());
        var evidence = failure.GetProperty("capture_evidence");
        Assert.Equal(raw, Convert.FromBase64String(evidence.GetProperty("Base64").GetString()!));
        Assert.Equal(0, evidence.GetProperty("ReturnedCapture").GetProperty("CommandResult").GetProperty("ExitCode").GetInt32());
        Assert.False(evidence.GetProperty("EvidenceTruncated").GetBoolean());
    }

    [Fact]
    public async Task ExactFixedProgramRunsThroughRealBoundedProcessStreamsAndStopsOnMiddleFailure()
    {
        // Host shell functions replace only device utilities. The owner-generated
        // program itself, byte framing and real CommandRunner are unchanged.
        var shell = FindHostShell();
        var prelude = "getprop() { printf 'QUEST123\\n'; }; " +
            "ps() { printf 'UID PID PPID NAME ARGS\\n0 1 0 init init\\n10123 123 1 app app\\n'; }; " +
            "cat() { case \"$1\" in /proc/sys/kernel/random/boot_id) printf '"+Boot+"\\n';; " +
            "/proc/123/stat) printf '%s\\n' '"+Stat+"';; " +
            "/proc/123/status) printf 'Uid:\\t10123 10123 10123 10123\\n';; *) return 91;; esac; }; ";
        var runner = new HostShellTransport(shell, prelude);
        var result = await new AdbClient("unused-adb",runner).ObserveDeviceActorAsync("QUEST123",123);
        Assert.Equal(1,runner.Calls); Assert.Equal((ulong)987654,result.ProcessStartTicks);
        Assert.Equal("QUEST123\n", Encoding.UTF8.GetString(Convert.FromBase64String(result.RawSections[0].Base64)));
        Assert.Equal(2,result.Processes.Count);
        var failed = new HostShellTransport(shell,prelude.Replace("printf '"+Boot+"\\n'","return 17"));
        var error=await Assert.ThrowsAsync<AdbCommandException>(() => new AdbClient("unused-adb",failed).ObserveDeviceActorAsync("QUEST123",123));
        Assert.Equal(1,failed.Calls);
        Assert.Equal(17,error.Result.ExitCode);
        var evidence=Assert.IsType<ActorCaptureEvidence>(error.Data[typeof(ActorCaptureEvidence)]);
        Assert.Equal(17,evidence.ReturnedCapture!.CommandResult.ExitCode);
        var partial=Encoding.UTF8.GetString(Convert.FromBase64String(evidence.Base64));
        Assert.Contains("boot:17:0",partial); Assert.DoesNotContain("inventory:0:",partial);
    }

    private static string FindHostShell()
    {
        var explicitShell=Environment.GetEnvironmentVariable("QFM_TEST_POSIX_SHELL");
        var candidates=new List<string>();
        if (!string.IsNullOrEmpty(explicitShell)) candidates.Add(explicitShell);
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Git","bin","bash.exe"));
        }
        else candidates.Add("/bin/sh");
        return candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Set QFM_TEST_POSIX_SHELL to a host POSIX shell for the fixed-program host control; no ADB is used.");
    }

    private sealed class HostShellTransport(string shell,string prelude) : IStreamingCommandRunner
    {
        public int Calls { get; private set; }
        public Task<CommandResult> RunAsync(string fileName,IReadOnlyList<string> arguments,TimeSpan timeout,CancellationToken cancellationToken=default) => throw new Xunit.Sdk.XunitException("Unexpected unbounded transport.");
        public Task<StreamingCommandResult> RunToStreamAsync(string fileName,IReadOnlyList<string> arguments,Stream destination,long maximumBytes,TimeSpan timeout,CancellationToken cancellationToken=default)
        {
            Calls++; Assert.Equal(["-s","QUEST123","exec-out","sh","-c"],arguments.Take(5));
            Assert.Equal(AdbClient.BuildActorObservationScript(123),arguments[5]);
            return new CommandRunner().RunToStreamAsync(shell,["-c",prelude+arguments[5]],destination,maximumBytes,timeout,cancellationToken);
        }
    }

    private sealed class NoRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string fileName,IReadOnlyList<string> arguments,TimeSpan timeout,CancellationToken cancellationToken=default) => throw new Xunit.Sdk.XunitException("Unexpected unbounded command.");
    }
    private sealed class StreamRunner(byte[] bytes, string? failure = null) : IStreamingCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public long MaximumBytes { get; private set; }
        public Task<CommandResult> RunAsync(string fileName,IReadOnlyList<string> arguments,TimeSpan timeout,CancellationToken cancellationToken=default) => throw new Xunit.Sdk.XunitException("Unexpected unbounded command.");
        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName,IReadOnlyList<string> arguments,Stream destination,long maximumBytes,TimeSpan timeout,CancellationToken cancellationToken=default)
        {
            Calls.Add(arguments.ToArray()); MaximumBytes=maximumBytes;
            if (failure=="cancel") throw new OperationCanceledException();
            if (failure=="limit") throw new FleetTransferLimitException(maximumBytes);
            await destination.WriteAsync(bytes,cancellationToken);
            return new(new(fileName,arguments,failure=="exit"?1:0,"",failure=="stderr"?"private error":"",TimeSpan.Zero),
                bytes.Length+(failure=="count"?1:0),failure=="digest"?new string('0',64):Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}
