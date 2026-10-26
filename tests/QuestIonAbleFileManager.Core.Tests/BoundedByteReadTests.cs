using System.Security.Cryptography;
using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class BoundedByteReadTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public async Task PullReturnsOnlyCompleteBoundedBytesAndDoesNotOwnDestination(string text)
    {
        var runner = new Runner(Encoding.UTF8.GetBytes(text));
        using var destination = new MemoryStream();
        var result = await new AdbClient("fixture-adb", runner)
            .PullFileBoundedAsync("FIXTURE", "/data/local/tmp/a file.bin", destination, 8);
        Assert.Equal(text, Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(destination.Length, result.BytesWritten);
        Assert.Equal(Hash(destination.ToArray()), result.Sha256);
        Assert.Empty(result.CommandResult.StandardError);
        Assert.True(destination.CanWrite);
        Assert.Single(runner.StreamCalls);
        Assert.Equal(new[] { "-s", "FIXTURE", "shell", "-T", "-n" }, runner.StreamCalls[0].Take(5));
        Assert.Equal(8, runner.MaximumBytes);
        Assert.Equal(TimeSpan.FromMinutes(5), runner.Timeout);
        Assert.Contains("[ ! -f /proc/self/fd/3 ]", runner.StreamCalls[0][5]);
        Assert.Contains("stat -L -c %s -- /proc/self/fd/3", runner.StreamCalls[0][5]);
        Assert.Equal(2, runner.StreamCalls[0][5].Split("stat -L -c %d:%i:%s:%Y:%Z -- /proc/self/fd/3").Length - 1);
        Assert.Contains("-gt 8", runner.StreamCalls[0][5]);
    }

    [Fact]
    public async Task PullQuotesOneAbsolutePathWithoutAddingCallerCommands()
    {
        const string remote = "/sdcard/a 'quoted'; $(touch nope).bin";
        var runner = new Runner([1]);
        using var destination = new MemoryStream();
        await new AdbClient("fixture-adb", runner).PullFileBoundedAsync("FIXTURE", remote, destination, 1);
        Assert.Single(runner.StreamCalls);
        Assert.Contains("candidate=$(realpath '/sdcard/a '\\''quoted'\\''; $(touch nope).bin')", runner.StreamCalls[0][5]);
        Assert.Empty(runner.NativeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1073741825)]
    public async Task InvalidLimitRejectsBeforeAnyNativeRead(long maximum)
    {
        var runner = new Runner([1]); using var destination = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new AdbClient("fixture-adb", runner)
            .PullFileBoundedAsync("FIXTURE", "/sdcard/file", destination, maximum));
        Assert.Empty(runner.StreamCalls);
    }

    [Theory]
    [InlineData("relative/file")]
    [InlineData("/sdcard/file\ncommand")]
    [InlineData("/sdcard/file\0command")]
    public async Task InvalidPathRejectsBeforeAnyNativeRead(string remote)
    {
        var runner = new Runner([1]); using var destination = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => new AdbClient("fixture-adb", runner)
            .PullFileBoundedAsync("FIXTURE", remote, destination, 1));
        Assert.Empty(runner.StreamCalls);
    }

    [Theory]
    [InlineData("FIXTURE;other")]
    [InlineData("FIXTURE\nother")]
    public async Task InvalidSerialCannotSelectANativeTransport(string serial)
    {
        var runner = new Runner([1]); using var destination = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => new AdbClient("fixture-adb", runner)
            .PullFileBoundedAsync(serial, "/sdcard/file", destination, 1));
        Assert.Empty(runner.StreamCalls); Assert.Empty(runner.NativeCalls);
    }

    [Fact]
    public async Task ReadOnlyDestinationAndInvalidExportInputsRejectBeforeNativeWork()
    {
        var runner = new Runner([1]); var client = new AdbClient("fixture-adb", runner);
        using var destination = new MemoryStream([1], writable: false);
        await Assert.ThrowsAsync<ArgumentException>(() => client.PullFileBoundedAsync("FIXTURE", "/sdcard/file", destination, 1));
        using var writable = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExportSingleApkBoundedAsync("FIXTURE", "bad;package", writable, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ExportSingleApkBoundedAsync("FIXTURE", "com.example.fixture", writable, 0));
        Assert.Empty(runner.StreamCalls); Assert.Empty(runner.NativeCalls);
    }

    [Fact]
    public async Task SwallowedPartialDestinationWriteRemainsAnUnsuccessfulTransfer()
    {
        var runner = new Runner([1, 2, 3]) { Scenario = "swallowed-destination" };
        using var destination = new FailingDestination();
        await Assert.ThrowsAsync<IOException>(() => new AdbClient("fixture-adb", runner)
            .PullFileBoundedAsync("FIXTURE", "/sdcard/file", destination, 4));
        Assert.Equal(1, destination.Length);
        Assert.True(destination.CanWrite);
    }

    [Theory]
    [InlineData("overflow")]
    [InlineData("swallowed-overflow")]
    [InlineData("native-error")]
    [InlineData("short")]
    [InlineData("missing-frame")]
    [InlineData("stderr-error")]
    [InlineData("wrong-count")]
    [InlineData("wrong-hash")]
    public async Task PartialOrUnverifiableTransferNeverReturnsSuccess(string scenario)
    {
        var runner = new Runner(scenario.Contains("overflow", StringComparison.Ordinal) ? [1, 2, 3, 4, 5] : [1, 2, 3]) { Scenario = scenario };
        using var destination = new MemoryStream();
        await Assert.ThrowsAnyAsync<Exception>(() => new AdbClient("fixture-adb", runner)
            .PullFileBoundedAsync("FIXTURE", "/sdcard/file", destination, 4));
        Assert.InRange(destination.Length, 0, 4);
        Assert.True(destination.CanWrite);
        Assert.Single(runner.StreamCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCannotReturnSuccessfulPartialBytes(bool duringTransfer)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new Runner([1, 2, 3]) { CancelDuringTransfer = duringTransfer ? cancellation.Cancel : null };
        if (!duringTransfer) cancellation.Cancel();
        using var destination = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AdbClient("fixture-adb", runner)
            .PullFileBoundedAsync("FIXTURE", "/sdcard/file", destination, 4, cancellation.Token));
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public async Task ExportDerivesOneBasePathAndChecksItAgainAfterStreaming()
    {
        var runner = new Runner([1, 2, 3]); using var destination = new MemoryStream();
        var result = await new AdbClient("fixture-adb", runner)
            .ExportSingleApkBoundedAsync("FIXTURE", "com.example.fixture", destination, 8);
        Assert.Equal("com.example.fixture", result.PackageName);
        Assert.Equal("/data/app/example/base.apk", result.RemoteApkPath);
        Assert.Equal(3, result.Transfer.BytesWritten);
        Assert.Equal(2, runner.NativeCalls.Count);
        Assert.All(runner.NativeCalls, call => Assert.Equal(new[] { "-s", "FIXTURE", "shell", "pm path 'com.example.fixture'" }, call));
        Assert.Contains("realpath '/data/app/example/base.apk'", Assert.Single(runner.StreamCalls)[5]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportRejectsSplitsBeforeTransferAndPathDriftAfterTransfer(bool drift)
    {
        var runner = new Runner([1]) { Scenario = drift ? "path-drift" : "split" };
        using var destination = new MemoryStream();
        if (drift)
            await Assert.ThrowsAsync<InvalidDataException>(() => new AdbClient("fixture-adb", runner)
                .ExportSingleApkBoundedAsync("FIXTURE", "com.example.fixture", destination, 8));
        else
            await Assert.ThrowsAsync<SplitPackageException>(() => new AdbClient("fixture-adb", runner)
                .ExportSingleApkBoundedAsync("FIXTURE", "com.example.fixture", destination, 8));
        Assert.Equal(drift ? 1 : 0, runner.StreamCalls.Count);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FailingDestination : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            base.Write(buffer.Span[..1]);
            throw new IOException("Injected partial destination write.");
        }
    }

    private sealed class Runner(byte[] bytes) : IStreamingCommandRunner
    {
        public string Scenario { get; init; } = "success";
        public Action? CancelDuringTransfer { get; init; }
        public List<string[]> NativeCalls { get; } = [];
        public List<string[]> StreamCalls { get; } = [];
        public long MaximumBytes { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); NativeCalls.Add(arguments.ToArray());
            Assert.Equal(new[] { "-s", "FIXTURE", "shell", "pm path 'com.example.fixture'" }, arguments);
            var path = Scenario == "path-drift" && NativeCalls.Count > 1 ? "/data/app/replaced/base.apk" : "/data/app/example/base.apk";
            return Task.FromResult(new CommandResult(fileName, arguments, 0, "package:" + path + "\n" +
                (Scenario == "split" ? "package:/data/app/example/split.apk\n" : ""), "", TimeSpan.Zero));
        }
        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments, Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); StreamCalls.Add(arguments.ToArray()); MaximumBytes = maximumBytes; Timeout = timeout;
            if (Scenario is "swallowed-overflow" or "swallowed-destination") { try { await destination.WriteAsync(bytes, cancellationToken); } catch (IOException) { } }
            else await destination.WriteAsync(bytes, cancellationToken);
            CancelDuringTransfer?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            var declared = Scenario == "short" ? bytes.Length + 1 : bytes.Length;
            var stderr = Scenario == "missing-frame" ? "" : $"qfm-byte-transfer:v1:{declared}\n";
            if (Scenario == "stderr-error") stderr += "Permission denied\n";
            return new(new(fileName, arguments, Scenario == "native-error" ? 1 : 0, "", stderr, TimeSpan.Zero),
                Scenario == "wrong-count" ? bytes.Length + 1 : bytes.Length, Scenario == "wrong-hash" ? new string('0', 64) : Hash(bytes));
        }
    }
}
