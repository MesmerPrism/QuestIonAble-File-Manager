using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        Assert.Equal(new[] { "-s", "FIXTURE", "exec-out", "sh", "-c" }, runner.StreamCalls[0].Take(5));
        Assert.InRange(runner.MaximumBytes, 8, 8 + 256);
        Assert.Equal(TimeSpan.FromMinutes(5), runner.Timeout);
        Assert.Contains("[ ! -f /proc/$$/fd/3 ]", runner.StreamCalls[0][5]);
        Assert.Contains("stat -L -c %s -- /proc/$$/fd/3", runner.StreamCalls[0][5]);
        Assert.Equal(2, runner.StreamCalls[0][5].Split("stat -L -c %d:%i:%s:%Y:%Z -- /proc/$$/fd/3").Length - 1);
        Assert.DoesNotContain("/proc/self/fd/3", runner.StreamCalls[0][5]);
        Assert.Contains("-gt 8", runner.StreamCalls[0][5]);
    }

    [Fact]
    public async Task FramingNeverTransformsBinaryPayloadAndSurvivesSingleByteWireChunks()
    {
        var bytes = Enumerable.Range(0, 256).Select(value => (byte)value).Concat(new byte[] { 13, 10, 0, 10, 13 }).ToArray();
        var runner = new Runner(bytes) { ChunkBytes = 1 };
        using var destination = new MemoryStream();
        var result = await new AdbClient("fixture-adb", runner)
            .PullFileBoundedAsync("FIXTURE", "/sdcard/binary.bin", destination, bytes.Length);
        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(bytes.Length, result.BytesWritten);
        Assert.Equal(Hash(bytes), result.Sha256);
        Assert.True(runner.MaximumBytes > bytes.Length);
    }

    [Fact]
    public async Task ShellOwnedDescriptorSurvivesExternalStatClosingItsChildCopy()
    {
        // Linux exercises the actual generated shell against procfs. Windows
        // still checks the exact owner-PID probes above; no ADB is involved.
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "qfm-bounded-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "opened file.bin");
        try
        {
            byte[] bytes = [1, 2, 3];
            await File.WriteAllBytesAsync(source, bytes);
            using var destination = new MemoryStream();
            var result = await new AdbClient("fixture-adb", new ClosedChildDescriptorRunner())
                .PullFileBoundedAsync("FIXTURE", source, destination, bytes.Length);
            Assert.Equal(bytes, destination.ToArray());
            Assert.Equal(bytes.Length, result.BytesWritten);
            Assert.Equal(Hash(bytes), result.Sha256);
        }
        finally { File.Delete(source); Directory.Delete(directory); }
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
    [InlineData("missing-trailer")]
    [InlineData("wrong-header-nonce")]
    [InlineData("wrong-trailer-nonce")]
    [InlineData("extra-bytes")]
    [InlineData("invalid-size")]
    [InlineData("long-header")]
    [InlineData("merged-prefix-error")]
    [InlineData("merged-payload-error")]
    [InlineData("merged-trailer-error")]
    [InlineData("header-only")]
    [InlineData("truncated-payload")]
    [InlineData("windows-text-expansion")]
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

    private sealed class ClosedChildDescriptorRunner : IStreamingCommandRunner
    {
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments,
            Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            // Prove the external child has no FD 3, then execute real stat with
            // that descriptor closed. The opening shell must retain its copy.
            const string closedChild = "stat() { /usr/bin/test ! -e /proc/self/fd/3 3<&- || return 69; command stat \"$@\" 3<&-; }; ";
            return new CommandRunner().RunToStreamAsync("/bin/bash", ["-c", closedChild + arguments[5]],
                destination, maximumBytes, TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

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
        public int ChunkBytes { get; init; } = int.MaxValue;
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
            var nonce = Regex.Match(arguments[5], "qfm-byte-transfer:v2:([0-9a-f]{32}):").Groups[1].Value;
            Assert.Equal(32, nonce.Length);
            var declared = Scenario == "short" ? bytes.Length + 1 : bytes.Length;
            var header = $"qfm-byte-transfer:v2:{nonce}:{declared}\n";
            var trailer = $"\nqfm-byte-transfer:complete:v2:{nonce}:{declared}\n";
            if (Scenario == "wrong-header-nonce") header = header.Replace(nonce, new string('0', 32), StringComparison.Ordinal);
            if (Scenario == "wrong-trailer-nonce") trailer = trailer.Replace(nonce, new string('0', 32), StringComparison.Ordinal);
            if (Scenario == "invalid-size") header = $"qfm-byte-transfer:v2:{nonce}:-1\n";
            if (Scenario == "long-header") header = new string('x', 300) + "\n";
            if (Scenario == "missing-frame") header = "";
            if (Scenario == "missing-trailer") trailer = "";
            if (Scenario == "merged-prefix-error") header = "Permission denied\n" + header;
            if (Scenario == "merged-payload-error") header += "Permission denied\n";
            if (Scenario == "merged-trailer-error") trailer = "Permission denied\n" + trailer;
            var wire = Encoding.ASCII.GetBytes(header).Concat(bytes).Concat(Encoding.ASCII.GetBytes(trailer))
                .Concat(Scenario == "extra-bytes" ? new byte[] { 0 } : []).ToArray();
            if (Scenario == "header-only") wire = Encoding.ASCII.GetBytes(header);
            if (Scenario == "truncated-payload") wire = Encoding.ASCII.GetBytes(header).Concat(bytes.Take(1)).ToArray();
            if (Scenario == "windows-text-expansion") wire = wire.SelectMany(value => value == 10 ? new byte[] { 13, 10 } : [value]).ToArray();
            for (var offset = 0; offset < wire.Length; offset += Math.Min(ChunkBytes, wire.Length - offset))
            {
                var chunk = wire.AsMemory(offset, Math.Min(ChunkBytes, wire.Length - offset));
                if (Scenario is "swallowed-overflow" or "swallowed-destination")
                { try { await destination.WriteAsync(chunk, cancellationToken); } catch (IOException) { } }
                else await destination.WriteAsync(chunk, cancellationToken);
            }
            CancelDuringTransfer?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            var stderr = Scenario == "stderr-error" ? "Permission denied\n" : "";
            return new(new(fileName, arguments, Scenario == "native-error" ? 1 : 0, "", stderr, TimeSpan.Zero),
                Scenario == "wrong-count" ? wire.Length + 1 : wire.Length, Scenario == "wrong-hash" ? new string('0', 64) : Hash(wire));
        }
    }
}
