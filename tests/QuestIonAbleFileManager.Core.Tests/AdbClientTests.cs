using System.Security.Cryptography;
using System.Collections.Concurrent;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class AdbClientTests
{
    [Fact]
    public async Task ListRemoteDirectory_UsesSerialScopedAdbAndQuotedPath()
    {
        var runner = new RecordingCommandRunner(
            (_, _) => Success("Folder/\nfile with spaces.txt\n"));
        var client = new AdbClient("adb-test", runner);

        var entries = await client.ListRemoteDirectoryAsync("QUEST123", "/sdcard/My Files");

        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "-s", "QUEST123", "shell", "ls -1Ap -- '/sdcard/My Files'" }, runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task InstallApk_ProjectsOnlyExplicitOptions()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mqfm-{Guid.NewGuid():N}.apk");
        await File.WriteAllBytesAsync(tempFile, [1, 2, 3]);

        try
        {
            var runner = new RecordingCommandRunner((_, _) => Success("Success\n"));
            var client = new AdbClient("adb-test", runner);

            await client.InstallApkAsync(
                "QUEST123",
                tempFile,
                new ApkInstallOptions(
                    ReplaceExisting: true,
                    AllowDowngrade: true,
                    GrantRuntimePermissions: false,
                    AllowTestPackages: true));

            Assert.Equal(
                new[] { "-s", "QUEST123", "install", "-r", "-d", "-t", Path.GetFullPath(tempFile) },
                runner.Calls[0].Arguments);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task InstallApkBundle_UsesOneSerialScopedInstallMultipleCallForEveryPart()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"mqfm-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var baseApk = Path.Combine(tempRoot, "base.apk");
        var splitApk = Path.Combine(tempRoot, "split_config.arm64_v8a.apk");
        await File.WriteAllBytesAsync(baseApk, [1]);
        await File.WriteAllBytesAsync(splitApk, [2]);

        try
        {
            var runner = new RecordingCommandRunner((_, _) => Success("Success\n"));
            var client = new AdbClient("adb-test", runner);

            var result = await client.InstallApkBundleAsync(
                "QUEST123",
                [baseApk, splitApk],
                new ApkInstallOptions(
                    ReplaceExisting: true,
                    AllowDowngrade: true,
                    GrantRuntimePermissions: true,
                    AllowTestPackages: true));

            Assert.Equal(2, result.ApkPaths.Count);
            Assert.Equal(2, runner.Calls.Count);
            Assert.Equal(
                [
                    "-s", "QUEST123", "install-multiple", "-r", "-d", "-g", "-t",
                    Path.GetFullPath(baseApk), Path.GetFullPath(splitApk)
                ],
                runner.Calls[0].Arguments);
            Assert.Equal(
                ["-s", "QUEST123", "shell", "pm list packages -3"],
                runner.Calls[1].Arguments);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InstallApkBundle_RejectsIncompleteSetBeforeRunningAdb()
    {
        var runner = new RecordingCommandRunner((_, _) => Success("Success\n"));
        var client = new AdbClient("adb-test", runner);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.InstallApkBundleAsync("QUEST123", ["only-one.apk"]));

        Assert.Contains("at least two", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ExportSingleApk_WritesApkAndChecksum()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"mqfm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var outputPath = Path.Combine(tempRoot, "com.example.app.apk");
        var expectedBytes = new byte[] { 0x50, 0x4b, 0x03, 0x04, 0x2a };
        var runner = new RecordingCommandRunner((_, arguments) =>
        {
            if (arguments.Contains("pull", StringComparer.Ordinal))
            {
                File.WriteAllBytes(arguments[^1], expectedBytes);
                return Success("1 file pulled\n");
            }

            return Success("package:/data/app/example/base.apk\n");
        });
        var client = new AdbClient("adb-test", runner);

        try
        {
            var result = await client.ExportSingleApkAsync(
                "QUEST123",
                "com.example.app",
                outputPath);

            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(outputPath));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expectedBytes)), result.Sha256);
            Assert.Contains(result.Sha256.ToLowerInvariant(), await File.ReadAllTextAsync(result.ChecksumPath), StringComparison.Ordinal);
            Assert.All(runner.Calls, static call => Assert.Equal("QUEST123", call.Arguments[1]));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportSingleApk_RejectsSplitPackageBeforePull()
    {
        var runner = new RecordingCommandRunner((_, _) => Success("""
            package:/data/app/example/base.apk
            package:/data/app/example/split_config.arm64_v8a.apk
            """));
        var client = new AdbClient("adb-test", runner);

        var exception = await Assert.ThrowsAsync<SplitPackageException>(
            () => client.ExportSingleApkAsync(
                "QUEST123",
                "com.example.app",
                Path.Combine(Path.GetTempPath(), $"mqfm-{Guid.NewGuid():N}.apk")));

        Assert.Equal(2, exception.ApkPaths.Count);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task EnableWifiAdb_ReadsAddressBeforeMutationAndConnectsExactEndpoint()
    {
        var runner = new RecordingCommandRunner((_, arguments) =>
        {
            if (arguments.SequenceEqual(
                    ["-s", "QUEST123", "shell", "getprop", "ro.serialno"]))
            {
                return Success("stable-quest-identity\n");
            }

            if (arguments.SequenceEqual(
                    ["-s", "192.0.2.42:5555", "shell", "getprop", "ro.serialno"]))
            {
                return Success("stable-quest-identity\n");
            }

            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", "ip route"]))
            {
                return Success(
                    "192.0.2.0/24 dev wlan0 proto kernel scope link src 192.0.2.42 metric 303\n");
            }

            if (arguments.SequenceEqual(["-s", "QUEST123", "tcpip", "5555"]))
            {
                return Success("restarting in TCP mode port: 5555\n");
            }

            if (arguments.SequenceEqual(["connect", "192.0.2.42:5555"]))
            {
                return Success("connected to 192.0.2.42:5555\n");
            }

            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success(
                    "List of devices attached\n192.0.2.42:5555 device product:eureka model:Quest_3\n");
            }

            return new CommandResult("adb-test", arguments, 1, string.Empty, "unexpected command", TimeSpan.Zero);
        });
        var client = new AdbClient("adb-test", runner);
        var progress = new RecordingProgress<OperatorProgress>();

        var result = await client.EnableWifiAdbAndConnectAsync(
            "QUEST123",
            progress: progress);

        Assert.Equal("192.0.2.42:5555", result.Endpoint);
        Assert.Equal(64, result.DeviceIdentitySha256.Length);
        Assert.Equal(
            [0, 1, 2, 3, 4, 5],
            progress.Values.Select(static value => value.CompletedUnits));
        Assert.All(progress.Values, static value => Assert.Equal(5, value.TotalUnits));
        Assert.Equal(
            [
                new[] { "-s", "QUEST123", "shell", "getprop", "ro.serialno" },
                new[] { "-s", "QUEST123", "shell", "ip route" },
                new[] { "-s", "QUEST123", "tcpip", "5555" },
                new[] { "connect", "192.0.2.42:5555" },
                new[] { "devices", "-l" },
                new[] { "-s", "192.0.2.42:5555", "shell", "getprop", "ro.serialno" }
            ],
            runner.Calls.Select(static call => call.Arguments));
    }

    [Fact]
    public async Task EnableWifiAdb_RejectsNetworkIdentityMismatch()
    {
        var runner = WifiIdentityRunner(
            "selected-device",
            "different-device",
            "connected to 192.0.2.42:5555\n");
        var client = new AdbClient("adb-test", runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EnableWifiAdbAndConnectAsync("QUEST123"));

        Assert.Contains("did not match", exception.Message);
        Assert.Contains(
            runner.Calls,
            static call => call.Arguments.SequenceEqual(
                [
                    "-s",
                    "192.0.2.42:5555",
                    "shell",
                    "getprop",
                    "ro.serialno"
                ]));
    }

    [Fact]
    public async Task EnableWifiAdb_RejectsReadyStaleEndpointForAnotherDevice()
    {
        var runner = WifiIdentityRunner(
            "selected-device",
            "stale-endpoint-device",
            "already connected to 192.0.2.42:5555\n");
        var client = new AdbClient("adb-test", runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EnableWifiAdbAndConnectAsync("QUEST123"));

        Assert.Contains("did not match", exception.Message);
        Assert.Contains(
            runner.Calls,
            static call => call.Arguments.SequenceEqual(
                ["connect", "192.0.2.42:5555"]));
    }

    [Fact]
    public async Task EnableWifiAdb_StopsBeforeMutationWithoutStableUsbIdentity()
    {
        var runner = new RecordingCommandRunner((_, arguments) =>
        {
            if (arguments.Count >= 5 &&
                arguments[0] == "-s" &&
                arguments[1] == "QUEST123" &&
                arguments[2] == "shell" &&
                arguments[3] == "getprop")
            {
                return Success("\n");
            }
            return new CommandResult(
                "adb-test",
                arguments,
                1,
                string.Empty,
                "unexpected command",
                TimeSpan.Zero);
        });
        var client = new AdbClient("adb-test", runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EnableWifiAdbAndConnectAsync("QUEST123"));

        Assert.Contains("stable device identity", exception.Message);
        Assert.DoesNotContain(
            runner.Calls,
            static call => call.Arguments.Contains(
                "tcpip",
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task ParallelInstall_IsBoundedSerialScopedAndPreservesPartialFailure()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mqfm-many-{Guid.NewGuid():N}.apk");
        await File.WriteAllBytesAsync(tempFile, [1, 2, 3]);
        var runner = new ParallelCommandRunner("192.0.2.12:5555");
        var client = new AdbClient("adb-test", runner);
        var progress = new RecordingProgress<OperatorProgress>();

        try
        {
            var result = await client.InstallApkOnManyWifiDevicesAsync(
                [
                    "192.0.2.10:5555",
                    "192.0.2.11:5555",
                    "192.0.2.12:5555",
                    "192.0.2.13:5555"
                ],
                tempFile,
                new ApkInstallOptions(ReplaceExisting: true),
                maxParallelism: 2,
                progress: progress);

            Assert.Equal(2, runner.MaxObservedConcurrency);
            Assert.Equal(3, result.SucceededCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Equal(0, progress.Values[0].CompletedUnits);
            Assert.Equal(4, progress.Values[^1].CompletedUnits);
            Assert.All(progress.Values, static value => Assert.Equal(4, value.TotalUnits));
            Assert.Equal(
                "192.0.2.12:5555",
                Assert.Single(result.Targets, static target => !target.Succeeded).Serial);
            var installCalls = runner.Calls.Where(call => call[2] == "install").ToArray();
            Assert.Equal(4, installCalls.Length);
            Assert.All(
                installCalls,
                call =>
                {
                    Assert.Equal("-s", call[0]);
                    Assert.Equal("install", call[2]);
                    Assert.Equal(Path.GetFullPath(tempFile), call[^1]);
                });
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParallelBundleInstall_SendsCompleteSetToEveryWifiTarget()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"mqfm-many-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var baseApk = Path.Combine(tempRoot, "base.apk");
        var splitApk = Path.Combine(tempRoot, "split_config.en.apk");
        await File.WriteAllBytesAsync(baseApk, [1]);
        await File.WriteAllBytesAsync(splitApk, [2]);
        var runner = new ParallelCommandRunner();
        var client = new AdbClient("adb-test", runner);

        try
        {
            var result = await client.InstallApkBundleOnManyWifiDevicesAsync(
                ["192.0.2.20:5555", "192.0.2.21:5555"],
                [baseApk, splitApk],
                maxParallelism: 2);

            Assert.True(result.Succeeded);
            var installCalls = runner.Calls.Where(call => call[2] == "install-multiple").ToArray();
            Assert.Equal(2, installCalls.Length);
            Assert.All(
                installCalls,
                call => Assert.Equal(
                    ["install-multiple", "-r", Path.GetFullPath(baseApk), Path.GetFullPath(splitApk)],
                    call.Skip(2)));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static CommandResult Success(string output) =>
        new("adb-test", Array.Empty<string>(), 0, output, string.Empty, TimeSpan.Zero);

    private static RecordingCommandRunner WifiIdentityRunner(
        string usbIdentity,
        string networkIdentity,
        string connectOutput) =>
        new((_, arguments) =>
        {
            if (arguments.SequenceEqual(
                    ["-s", "QUEST123", "shell", "getprop", "ro.serialno"]))
            {
                return Success(usbIdentity + "\n");
            }
            if (arguments.SequenceEqual(
                    [
                        "-s",
                        "192.0.2.42:5555",
                        "shell",
                        "getprop",
                        "ro.serialno"
                    ]))
            {
                return Success(networkIdentity + "\n");
            }
            if (arguments.SequenceEqual(
                    ["-s", "QUEST123", "shell", "ip route"]))
            {
                return Success(
                    "192.0.2.0/24 dev wlan0 proto kernel scope link " +
                    "src 192.0.2.42 metric 303\n");
            }
            if (arguments.SequenceEqual(
                    ["-s", "QUEST123", "tcpip", "5555"]))
            {
                return Success("restarting in TCP mode port: 5555\n");
            }
            if (arguments.SequenceEqual(
                    ["connect", "192.0.2.42:5555"]))
            {
                return Success(connectOutput);
            }
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success(
                    "List of devices attached\n" +
                    "192.0.2.42:5555 device product:eureka model:Quest_3\n");
            }
            return new CommandResult(
                "adb-test",
                arguments,
                1,
                string.Empty,
                "unexpected command",
                TimeSpan.Zero);
        });

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
            var handled = handler(fileName, arguments);
            return Task.FromResult(handled with { FileName = fileName, Arguments = arguments.ToArray() });
        }
    }

    private sealed class ParallelCommandRunner(string? failingSerial = null) : ICommandRunner
    {
        private int _active;
        private int _maxObservedConcurrency;

        public ConcurrentBag<IReadOnlyList<string>> Calls { get; } = [];

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

        public async Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var maximum = Volatile.Read(ref _maxObservedConcurrency);
                if (active <= maximum ||
                    Interlocked.CompareExchange(ref _maxObservedConcurrency, active, maximum) == maximum)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(75, cancellationToken);
                var failed = failingSerial is not null &&
                    string.Equals(arguments[1], failingSerial, StringComparison.Ordinal);
                return new CommandResult(
                    fileName,
                    arguments.ToArray(),
                    failed ? 1 : 0,
                    failed ? string.Empty : "Success\n",
                    failed ? "Failure [INSTALL_FAILED_TEST]" : string.Empty,
                    TimeSpan.FromMilliseconds(75));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        private readonly object _gate = new();
        private readonly List<T> _values = [];

        public IReadOnlyList<T> Values
        {
            get
            {
                lock (_gate)
                {
                    return _values.ToArray();
                }
            }
        }

        public void Report(T value)
        {
            lock (_gate)
            {
                _values.Add(value);
            }
        }
    }
}
