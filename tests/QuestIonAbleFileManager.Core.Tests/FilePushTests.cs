using System.Security.Cryptography;
using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class FilePushTests
{
    [Theory]
    [InlineData("41ed", 4096)] // directory size can equal the source size
    [InlineData("a1ff", 4096)] // final symbolic link
    [InlineData("11a4", 4096)] // FIFO
    [InlineData("c1a4", 4096)] // socket
    public async Task Push_RejectsNonRegularPreimageBeforeAnyPush(string mode, int size)
    {
        var source = Path.Combine(Path.GetTempPath(), "qfm-file-push-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(source, new byte[size]);
        var runner = new PushRunner($"qfm-file-push:v1:{mode}:{size}\n", $"qfm-file-push:v1:81a4:{size}\n");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("adb-test", runner)
                .PushFileAsync("fixture-target", source, "/sdcard/Download/destination.bin"));
            Assert.Equal(0, runner.PushCount);
        }
        finally { File.Delete(source); }
    }


    [Theory]
    [InlineData("qfm-file-push:v1:absent\n")]
    [InlineData("qfm-file-push:v1:81a4:19\n")]
    public async Task Push_AllowsAbsentOrRegularPreimageAndConfirmsOnlyRegularPostimageSize(string before)
    {
        using var fixture = new PushFixture(before, "qfm-file-push:v1:81a4:4\n");
        var result = await new OperatorCommandExecutor(fixture.Client).ExecuteAsync(
            OperatorCommands.PushFile("fixture-target", fixture.Source, "/sdcard/Download/file.bin"));
        Assert.Equal(OperatorMutationStage.Confirmed, result.MutationReceipt!.Stage);
        Assert.Contains("size", result.MutationReceipt.ObservedState, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Runner.PushCount);
        Assert.Equal(3, fixture.Runner.Calls.Count);
        Assert.Equal(["-s", "fixture-target", "push", fixture.Source, "/sdcard/Download/file.bin"], fixture.Runner.Calls[1]);
    }

    [Theory]
    [InlineData("qfm-file-push:v1:absent\n")]
    [InlineData("qfm-file-push:v1:41ed:4\n")]
    [InlineData("qfm-file-push:v1:a1ff:4\n")]
    [InlineData("qfm-file-push:v1:11a4:4\n")]
    [InlineData("qfm-file-push:v1:c1a4:4\n")]
    [InlineData("qfm-file-push:v1:81a4:3\n")]
    [InlineData("qfm-file-push:v1:81a4:5\n")]
    public async Task Push_PostDispatchTypeAbsenceOrSizeFailureCannotConfirm(string after)
    {
        using var fixture = new PushFixture("qfm-file-push:v1:absent\n", after);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OperatorCommandExecutor(fixture.Client).ExecuteAsync(
            OperatorCommands.PushFile("fixture-target", fixture.Source, "/sdcard/Download/file.bin")));
        Assert.Equal(1, fixture.Runner.PushCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("4\n")]
    [InlineData("qfm-file-push:v1:81a4:04\n")]
    [InlineData("qfm-file-push:v1:81a4:-4\n")]
    [InlineData("qfm-file-push:v1:81a4:9223372036854775808\n")]
    [InlineData("qfm-file-push:v1:81a4:4\nextra\n")]
    [InlineData("qfm-file-push:v1:81a4:4\nqfm-file-push:v1:81a4:4\n")]
    [InlineData("qfm-file-push:v1:rejected\n")]
    public async Task Push_RejectsMalformedOrRejectedPreimageWithoutDispatch(string before)
    {
        using var fixture = new PushFixture(before, "qfm-file-push:v1:81a4:4\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.PushFileAsync(
            "fixture-target", fixture.Source, "/sdcard/Download/file.bin"));
        Assert.Equal(0, fixture.Runner.PushCount);
    }

    [Fact]
    public async Task Push_ZeroByteRegularFileCanBeConfirmed()
    {
        using var fixture = new PushFixture("qfm-file-push:v1:absent\n", "qfm-file-push:v1:8000:0\n");
        await File.WriteAllBytesAsync(fixture.Source, []);
        await fixture.Client.PushFileAsync("fixture-target", fixture.Source, "/sdcard/Download/empty.bin");
        Assert.Equal(1, fixture.Runner.PushCount);
    }

    [Theory]
    [InlineData("stderr")]
    [InlineData("count")]
    [InlineData("oversize")]
    [InlineData("exit")]
    public async Task Push_TransportAndBoundFailuresPreventDispatch(string fault)
    {
        using var fixture = new PushFixture("qfm-file-push:v1:81a4:4\n", "qfm-file-push:v1:81a4:4\n");
        fixture.Runner.Fault = fault;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.PushFileAsync(
            "fixture-target", fixture.Source, "/sdcard/Download/file.bin"));
        Assert.Equal(0, fixture.Runner.PushCount);
    }

    [Fact]
    public async Task Push_KeepsOneFixedSerialScopedBoundedReadAndQuotesOnlyTheDestination()
    {
        using var fixture = new PushFixture("qfm-file-push:v1:absent\n", "qfm-file-push:v1:81a4:4\n");
        const string remote = "/sdcard/Download/a'$(untrusted); name.bin";
        await fixture.Client.PushFileAsync("fixture-target", fixture.Source, remote);
        Assert.Equal(1, fixture.Runner.PushCount);
        foreach (var call in fixture.Runner.Calls.Where(call => call.Contains("exec-out")))
        {
            Assert.Equal(["-s", "fixture-target", "exec-out", "sh", "-c"], call.Take(5));
            Assert.StartsWith("destination='/sdcard/Download/a'\\''$(untrusted); name.bin'; ", call[5], StringComparison.Ordinal);
            Assert.Contains("[ -L \"$destination\" ]", call[5], StringComparison.Ordinal);
            Assert.Contains("[ ! -f \"$destination\" ]", call[5], StringComparison.Ordinal);
            Assert.Contains("metadata=$(stat -c '%f:%s'", call[5], StringComparison.Ordinal);
            // ADB exec-out can lose a remote exit status: publish the complete
            // success frame only after the last final-inode guard has succeeded.
            var publish = call[5].IndexOf("printf 'qfm-file-push:v1:%s", StringComparison.Ordinal);
            Assert.True(publish > call[5].LastIndexOf("[ ! -f", StringComparison.Ordinal));
            Assert.DoesNotContain("stat -L", call[5], StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Source, call[5], StringComparison.Ordinal);
        }
        Assert.Equal(2, fixture.Runner.ReadBounds.Count);
        Assert.All(fixture.Runner.ReadBounds, bound =>
        {
            Assert.Equal(1024, bound.Bytes);
            Assert.Equal(TimeSpan.FromSeconds(15), bound.Timeout);
        });
    }

    [Fact]
    public async Task Push_CancellationDuringReadbackCannotReturnConfirmedOrRetry()
    {
        using var fixture = new PushFixture("qfm-file-push:v1:absent\n", "qfm-file-push:v1:81a4:4\n");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runner.BeforeRead = async (afterPush, token) =>
        {
            if (!afterPush) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Client.PushFileAsync("fixture-target", fixture.Source,
            "/sdcard/Download/file.bin", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, fixture.Runner.PushCount);
    }

    private sealed class PushFixture : IDisposable
    {
        public string Source { get; } = Path.Combine(Path.GetTempPath(), "qfm-file-push-" + Guid.NewGuid().ToString("N") + ".bin");
        public PushRunner Runner { get; }
        public AdbClient Client { get; }
        public PushFixture(string before, string after)
        {
            File.WriteAllBytes(Source, [1, 2, 3, 4]);
            Runner = new(before, after);
            Client = new("adb-test", Runner);
        }
        public void Dispose() => File.Delete(Source);
    }

    private sealed class PushRunner(string before, string after) : IStreamingCommandRunner
    {
        public int PushCount;
        public string? Fault { get; set; }
        public Func<bool, CancellationToken, Task>? BeforeRead { get; set; }
        public List<(long Bytes, TimeSpan Timeout)> ReadBounds { get; } = [];
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            if (arguments.Contains("push")) PushCount++;
            var output = arguments.Any(arg => arg.StartsWith("stat -c %s", StringComparison.Ordinal)) ? "4096\n" : "Success\n";
            return Task.FromResult(new CommandResult(fileName, arguments, 0, output, "", TimeSpan.Zero));
        }
        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments,
            Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            ReadBounds.Add((maximumBytes, timeout));
            if (BeforeRead is not null) await BeforeRead(PushCount != 0, cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(Fault == "oversize" ? new string('x', 1025) : PushCount == 0 ? before : after);
            await destination.WriteAsync(bytes, cancellationToken);
            return new(new CommandResult(fileName, arguments, Fault == "exit" ? 1 : 0, "", Fault == "stderr" ? "unexpected" : "", TimeSpan.Zero),
                bytes.Length + (Fault == "count" ? 1 : 0),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}
