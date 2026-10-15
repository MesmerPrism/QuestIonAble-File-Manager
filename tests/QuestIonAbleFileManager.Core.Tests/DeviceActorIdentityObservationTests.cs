using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class DeviceActorIdentityObservationTests
{
    private const string Boot = "11111111-2222-3333-4444-555555555555";
    private static string Stat(ulong birth = 987654) => "123 (name with ) parentheses) " +
        string.Join(' ', new[] { "S" }.Concat(Enumerable.Repeat("0", 18)).Append(birth.ToString()));
    private static byte[][] Sections => [
        B("QUEST123\n"), B(Boot + "\n"),
        B("UID PID PPID NAME ARGS\n0 1 0 init init\n10123 123 1 com.example.app com.example.app \n"),
        B(Stat() + "\n"), [.. B("com.example.app"), 0, .. B("argument"), 0, 255],
        B("package:com.example.app uid:10123\npackage:com.example.shared uid:10123\n"),
        B("Uid:\t10123 10124 10125 10126\n"), B(Stat(987655) + "\n")];
    private static readonly string[] Names = ["serial", "boot", "inventory", "initial_stat", "cmdline", "package_uids", "status", "final_stat"];
    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Frame(byte[][]? sections = null)
    {
        sections ??= Sections;
        using var output = new MemoryStream();
        output.Write(B("QFM_ACTOR_IDENTITY_V1\n"));
        for (var i = 0; i < 8; i++)
        {
            output.Write(B($"{Names[i]}:0:{sections[i].Length}\n")); output.Write(sections[i]); output.WriteByte(10);
        }
        output.Write(B("END\n")); return output.ToArray();
    }

    [Fact]
    public async Task ActualTypedExecutorKeepsBinaryAndAllFactsWithoutCallerPolicy()
    {
        var raw = Frame(); var transport = new StreamRunner(raw);
        var command = OperatorCommands.ParseDeviceActorIdentityCliArguments(
            ["device", "actor-identity-observe", "--serial", "QUEST123", "--pid", "123", "--json"]);
        var result = (await new OperatorCommandExecutor(new AdbClient("adb-test", transport)).ExecuteAsync(command)).DeviceActorIdentityObservation!;
        Assert.False(OperatorMutations.RequiresHeadsetStateChange(command));
        Assert.Equal(AdbClient.ActorIdentityMaximumBytes, transport.MaximumBytes);
        Assert.Equal(["-s", "QUEST123", "exec-out", "sh", "-c"], Assert.Single(transport.Calls).Take(5));
        Assert.Equal(AdbClient.BuildActorIdentityScript(123), transport.Calls[0][5]);
        Assert.Equal(Sections[4], Convert.FromBase64String(result.CommandLine.Base64));
        Assert.Equal((ulong)987654, result.InitialProcessStartTicks); Assert.Equal((ulong)987655, result.FinalProcessStartTicks);
        Assert.Equal(2, result.PackageUids.Count); Assert.Equal(result.PackageUids[0].Uid, result.PackageUids[1].Uid);
        Assert.Equal(new uint[] { 10123, 10124, 10125, 10126 }, result.ProcessUids);
        Assert.Equal("com.example.app ", result.Processes[1].Arguments);
        Assert.Equal(8, result.RawSections.Count);
        for (var i = 0; i < 8; i++) Assert.Equal(Sections[i], Convert.FromBase64String(result.RawSections[i].Base64));
        Assert.Contains("caller decides", result.ObservationScope);
    }

    [Theory]
    [InlineData("0")][InlineData("-1")][InlineData("01")][InlineData("1;id")][InlineData("2147483648")]
    public async Task ActualCliRejectsPidBeforeFactory(string pid)
    {
        var original = Console.Out; using var output = new StringWriter(); var calls = 0;
        try
        {
            Console.SetOut(output);
            Assert.Equal(2, await CliApplication.RunDeviceActorIdentityObservationJsonAsync(
                ["device", "actor-identity-observe", "--serial", "QUEST123", "--pid", pid, "--json"],
                () => { calls++; throw new Exception("Factory must not run"); }));
        }
        finally { Console.SetOut(original); }
        Assert.Equal(0, calls); using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task ClosedCliDispatcherAndArgumentShapeRefuseBeforeAdb()
    {
        var original = Console.Out; using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(2, await CliApplication.RunAsync(["device", "actor-identity-observe", "--serial", "SECRET", "--pid", "01", "--json"]));
            Assert.DoesNotContain("SECRET", output.ToString());
        }
        finally { Console.SetOut(original); }
        foreach (var args in new[] {
            new[] { "device", "ACTOR-IDENTITY-OBSERVE", "--serial", "QUEST123", "--pid", "123", "--json" },
            new[] { "device", "actor-identity-observe", "--serial", "QUEST123", "--pid", "123", "--json", "--path", "/proc" },
            new[] { "device", "actor-identity-observe", "--serial", "bad serial", "--pid", "123", "--json" } })
            Assert.Throws<ArgumentException>(() => OperatorCommands.ParseDeviceActorIdentityCliArguments(args));
        var transport = new StreamRunner(Frame());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new AdbClient("adb-test", transport).ObserveDeviceActorIdentityAsync("QUEST123", 0));
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task EmptyOpaqueCmdlineIsFactButNulInTextIsNeverRelaxed()
    {
        var sections = Sections; sections[4] = [];
        var result = await new AdbClient("adb-test", new StreamRunner(Frame(sections))).ObserveDeviceActorIdentityAsync("QUEST123", 123);
        Assert.Equal(0, result.CommandLine.Bytes);
        foreach (var i in new[] { 0, 1, 2, 3, 5, 6, 7 })
        {
            sections = Sections; sections[i] = [.. sections[i], 0];
            await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("adb-test", new StreamRunner(Frame(sections))).ObserveDeviceActorIdentityAsync("QUEST123", 123));
        }
    }

    [Fact]
    public async Task MalformedRegistryInventoryStatusAndBothStatFactsRefuse()
    {
        foreach (var (index, value) in new[] {
            (2, "UID PID ARGS\n10123 123 app\n"), (2, "UID PID PPID NAME ARGS\n"),
            (2, Encoding.UTF8.GetString(Sections[2]) + "10124 123 1 other other\n"),
            (5, "package:com.example.app uid:1\npackage:com.example.app uid:2\n"),
            (5, "package:com.example.app uid:1\n\n"), (5, "package:bad uid:notnumeric\n"),
            (5, "\n"), (6, "Uid: 1 2 3\n"), (6, "Uid: 1 1 1 1\nUid: 1 1 1 1\n"),
            (3, Stat().Replace("123 (", "124 (")), (7, Stat().Replace("123 (", "124 (")),
            (3, "123 (short) S 0"), (7, "123 (short) S 0") })
        {
            var sections = Sections; sections[index] = B(value);
            await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("adb-test", new StreamRunner(Frame(sections))).ObserveDeviceActorIdentityAsync("QUEST123", 123));
        }
    }

    [Fact]
    public async Task ByteFramingBoundsUtf8OrderAndEndAreStrict()
    {
        var valid = Frame();
        foreach (var raw in new[] { valid[..^1], valid.Concat(B("junk")).ToArray(),
            Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(valid).Replace("status:0:", "final_stat:0:")),
            Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(valid).Replace("initial_stat:0:", "initial_stat:17:")) })
            await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("adb-test", new StreamRunner(raw)).ObserveDeviceActorIdentityAsync("QUEST123", 123));
        var limits = new[] { 256, 128, 8*1024*1024, 4096, 65536, 8*1024*1024, 65536, 4096 };
        for (var i = 0; i < 8; i++)
        {
            var sections = Sections; sections[i] = new byte[limits[i]+1];
            await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("adb-test", new StreamRunner(Frame(sections))).ObserveDeviceActorIdentityAsync("QUEST123", 123));
        }
        var bad = Sections; bad[5] = [255];
        await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("adb-test", new StreamRunner(Frame(bad))).ObserveDeviceActorIdentityAsync("QUEST123", 123));
    }

    [Fact]
    public async Task Native17CountDigestStderrCancellationAndLimitPreserveFailureWithoutRetry()
    {
        foreach (var failure in new[] { "exit17", "stderr", "count", "digest", "cancel", "limit" })
        {
            var runner = new StreamRunner(Frame(), failure);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => new AdbClient("adb-test", runner).ObserveDeviceActorIdentityAsync("QUEST123", 123));
            var evidence = Assert.IsType<ActorCaptureEvidence>(error.Data[typeof(ActorCaptureEvidence)]);
            Assert.Single(runner.Calls);
            if (failure is "cancel" or "limit") Assert.Null(evidence.ReturnedCapture);
            else Assert.Equal(Frame(), Convert.FromBase64String(evidence.Base64));
            if (failure == "exit17") Assert.Equal(17, evidence.ReturnedCapture!.CommandResult.ExitCode);
        }
    }

    [Fact]
    public async Task ActualCliSuccessEnvelopeUsesTheSharedTypedRoute()
    {
        var runner = new StreamRunner(Frame()); var original = Console.Out; using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, await CliApplication.RunDeviceActorIdentityObservationJsonAsync(
                ["device", "actor-identity-observe", "--serial", "QUEST123", "--pid", "123", "--json"],
                () => new AdbClient("adb-test", runner)));
        }
        finally { Console.SetOut(original); }
        using var doc = JsonDocument.Parse(output.ToString()); var result = doc.RootElement.GetProperty("result");
        Assert.Equal("questionable.file_manager.device_actor_identity_observation.v1", doc.RootElement.GetProperty("schema").GetString());
        Assert.True(doc.RootElement.GetProperty("succeeded").GetBoolean()); Assert.Equal(8, result.GetProperty("RawSections").GetArrayLength());
        Assert.Equal(Sections[4], Convert.FromBase64String(result.GetProperty("CommandLine").GetProperty("Base64").GetString()!));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task ActualCliHostZeroFailedMidframeRetainsRawAndNeverReturnsFacts()
    {
        var raw = B("QFM_ACTOR_IDENTITY_V1\nserial:0:9\nQUEST123\n\nboot:17:0\n\n");
        var runner = new StreamRunner(raw); var original = Console.Out; using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(1, await CliApplication.RunDeviceActorIdentityObservationJsonAsync(
                ["device", "actor-identity-observe", "--serial", "QUEST123", "--pid", "123", "--json"],
                () => new AdbClient("adb-test", runner)));
        }
        finally { Console.SetOut(original); }
        Assert.Single(runner.Calls); using var doc = JsonDocument.Parse(output.ToString()); var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("result").ValueKind);
        var evidence = root.GetProperty("failure").GetProperty("capture_evidence");
        Assert.Equal(raw, Convert.FromBase64String(evidence.GetProperty("Base64").GetString()!));
        Assert.Equal(0, evidence.GetProperty("ReturnedCapture").GetProperty("CommandResult").GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public async Task RealFixedProgramPreservesNulAndOrderedReadsThroughBoundedHostStreams()
    {
        var shell = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe") : "/bin/sh";
        shell = Environment.GetEnvironmentVariable("QFM_TEST_POSIX_SHELL") ?? shell;
        var prelude = "getprop() { printf 'QUEST123\\n'; }; " +
            "ps() { printf 'UID PID PPID NAME ARGS\\n10123 123 1 app app\\n'; }; " +
            "pm() { printf 'package:com.example.app uid:10123\\n'; }; " +
            "base64() { if [ \"$1\" = /proc/123/cmdline ]; then printf 'app\\000arg\\000\\377' | command base64; else command base64 \"$@\"; fi; }; " +
            "cat() { case \"$1\" in /proc/sys/kernel/random/boot_id) printf '" + Boot + "\\n';; " +
            "/proc/123/stat) printf '%s\\n' '" + Stat() + "';; /proc/123/status) printf 'Uid: 10123 10123 10123 10123\\n';; *) return 91;; esac; }; ";
        var transport = new HostTransport(shell, prelude);
        var value = await new AdbClient("unused-adb", transport).ObserveDeviceActorIdentityAsync("QUEST123", 123);
        Assert.Equal(new byte[] { 97, 112, 112, 0, 97, 114, 103, 0, 255 }, Convert.FromBase64String(value.CommandLine.Base64));
        Assert.Equal(Names, value.RawSections.Select(s => s.Name)); Assert.Equal(1, transport.Calls);
        var program = AdbClient.BuildActorIdentityScript(123);
        var positions = new[] { "emit serial", "emit boot", "emit inventory", "emit initial_stat", "emit_cmdline /proc/123", "emit package_uids", "emit status", "emit final_stat" }.Select(s => program.LastIndexOf(s, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0)); Assert.Equal(positions.Order(), positions);
        var fail = new HostTransport(shell, prelude.Replace("printf 'package:com.example.app uid:10123\\n'", "return 17"));
        var error = await Assert.ThrowsAsync<AdbCommandException>(() => new AdbClient("unused-adb", fail).ObserveDeviceActorIdentityAsync("QUEST123", 123));
        Assert.Equal(17, error.Result.ExitCode); var capture = Assert.IsType<ActorCaptureEvidence>(error.Data[typeof(ActorCaptureEvidence)]);
        var prefix = Encoding.UTF8.GetString(Convert.FromBase64String(capture.Base64));
        Assert.Contains("package_uids:17:0", prefix); Assert.DoesNotContain("status:0:", prefix); Assert.Equal(1, fail.Calls);
    }

    private sealed class HostTransport(string shell, string prelude) : IStreamingCommandRunner
    {
        public int Calls { get; private set; }
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new Xunit.Sdk.XunitException("Unexpected unbounded transport");
        public Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments, Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++; Assert.Equal(AdbClient.BuildActorIdentityScript(123), arguments[5]);
            return new CommandRunner().RunToStreamAsync(shell, ["-c", prelude + arguments[5]], destination, maximumBytes, timeout, cancellationToken);
        }
    }
    private sealed class StreamRunner(byte[] raw, string? failure = null) : IStreamingCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public long MaximumBytes { get; private set; }
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new Xunit.Sdk.XunitException("Unexpected unbounded transport");
        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments, Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray()); MaximumBytes = maximumBytes;
            if (failure == "cancel") throw new OperationCanceledException();
            if (failure == "limit") throw new FleetTransferLimitException(maximumBytes);
            await destination.WriteAsync(raw, cancellationToken);
            return new(new(fileName, arguments, failure == "exit17" ? 17 : 0, "", failure == "stderr" ? "native error" : "", TimeSpan.Zero),
                raw.Length + (failure == "count" ? 1 : 0), failure == "digest" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant());
        }
    }
}
