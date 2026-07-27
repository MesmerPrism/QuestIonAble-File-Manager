using QuestIonAbleFileManager.Core;
using System.Security.Cryptography;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class OperatorCommandTests
{
    [Fact]
    public void FactoriesExposeExactCliArgumentsUsedByGui()
    {
        var localPath = Path.GetFullPath(Path.Combine("Test Data", "example file.txt"));
        var apkPath = Path.GetFullPath(Path.Combine("Test Data", "example app.apk"));
        var commands = new (OperatorCommand Command, string[] Expected)[]
        {
            (OperatorCommands.DiscoverDevices(), ["devices"]),
            (OperatorCommands.ListFiles("QUEST123", "/sdcard/My Files"),
                ["files", "list", "--serial", "QUEST123", "--path", "/sdcard/My Files"]),
            (OperatorCommands.PullFile("QUEST123", "/sdcard/My Files/example.txt", localPath),
                ["files", "pull", "--serial", "QUEST123", "--remote", "/sdcard/My Files/example.txt", "--output", localPath]),
            (OperatorCommands.PushFile("QUEST123", localPath, "/sdcard/Download/example.txt"),
                ["files", "push", "--serial", "QUEST123", "--file", localPath, "--remote", "/sdcard/Download/example.txt"]),
            (OperatorCommands.ListPackages("QUEST123"),
                ["apk", "list", "--serial", "QUEST123"]),
            (OperatorCommands.InspectApk(apkPath),
                ["apk", "inspect", "--file", apkPath]),
            (OperatorCommands.ExportApk("QUEST123", "com.example.app", apkPath, overwrite: true),
                ["apk", "export", "--serial", "QUEST123", "--package", "com.example.app", "--output", apkPath, "--overwrite"]),
            (OperatorCommands.InstallApk(
                    "QUEST123",
                    apkPath,
                    new ApkInstallOptions(false, true, true, true)),
                ["apk", "install", "--serial", "QUEST123", "--file", apkPath, "--no-replace", "--downgrade", "--grant-runtime-permissions", "--test-only"]),
            (OperatorCommands.LaunchInspectedApp("QUEST123", apkPath),
                ["apk", "launch", "--serial", "QUEST123", "--file", apkPath]),
            (OperatorCommands.ObserveInspectedApp("QUEST123", apkPath),
                ["apk", "observe", "--serial", "QUEST123", "--file", apkPath]),
            (OperatorCommands.EnableWifiAdb("QUEST123", 5555, operatorConfirmed: true),
                ["wifi", "enable", "--serial", "QUEST123", "--port", "5555", "--confirm-wifi-adb"]),
            (OperatorCommands.ConnectWifiAdb("192.0.2.42", 5555, operatorConfirmed: true),
                ["wifi", "connect", "--host", "192.0.2.42", "--port", "5555", "--confirm-wifi-adb"]),
            (OperatorCommands.DisconnectWifiAdb("192.0.2.42", 5555, operatorConfirmed: true),
                ["wifi", "disconnect", "--host", "192.0.2.42", "--port", "5555", "--confirm-wifi-adb"]),
            (OperatorCommands.InstallApkMany(
                    ["192.0.2.42:5555", "192.0.2.43:5555"],
                    apkPath,
                    new ApkInstallOptions(false, true, true, true),
                    maxParallelism: 2),
                [
                    "apk", "install-many",
                    "--serial", "192.0.2.42:5555",
                    "--serial", "192.0.2.43:5555",
                    "--file", apkPath,
                    "--parallelism", "2",
                    "--no-replace", "--downgrade", "--grant-runtime-permissions", "--test-only"
                ])
        };

        foreach (var (command, expected) in commands)
        {
            Assert.Equal(expected, command.CliArguments);
        }
    }

    [Fact]
    public void WifiCommandFactoriesRequireExplicitOperatorApproval()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => OperatorCommands.EnableWifiAdb("QUEST123"));

        Assert.Contains("confirmation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(
            () => OperatorCommands.ConnectWifiAdb("192.0.2.42"));
        Assert.Throws<InvalidOperationException>(
            () => OperatorCommands.DisconnectWifiAdb("192.0.2.42"));
    }

    [Fact]
    public void PowerShellCommandQuotesHumanInputsWithoutChangingArguments()
    {
        var command = OperatorCommands.ListFiles("QUEST123", "/sdcard/It's here");

        var rendered = command.ToPowerShellCommand(
            ".\\meta quest file manager.exe",
            "C:\\Android SDK\\platform-tools\\adb.exe");

        Assert.Equal(
            "& '.\\meta quest file manager.exe' files list --serial QUEST123 --path '/sdcard/It''s here' --adb 'C:\\Android SDK\\platform-tools\\adb.exe'",
            rendered);
    }

    [Fact]
    public async Task BundleFactoryExposesFolderRouteAndSnapshotsDeterministicApkSet()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"mqfm-bundle-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var baseApk = Path.Combine(tempRoot, "base-master.apk");
        var languageApk = Path.Combine(tempRoot, "base-de.apk");
        var splitApk = Path.Combine(tempRoot, "split_config.en.apk");
        await File.WriteAllBytesAsync(splitApk, [2]);
        await File.WriteAllBytesAsync(baseApk, [1]);
        await File.WriteAllBytesAsync(languageApk, [3]);
        await File.WriteAllTextAsync(Path.Combine(tempRoot, "notes.txt"), "ignored");

        try
        {
            var command = OperatorCommands.InstallApkBundle(
                "QUEST123",
                tempRoot,
                new ApkInstallOptions(false, true, true, true));

            Assert.Equal(
                [
                    "apk", "install-bundle", "--serial", "QUEST123", "--folder", Path.GetFullPath(tempRoot),
                    "--no-replace", "--downgrade", "--grant-runtime-permissions", "--test-only"
                ],
                command.CliArguments);
            Assert.Equal([baseApk, languageApk, splitApk], command.ApkBundle!.ApkPaths);

            var parallelCommand = OperatorCommands.InstallApkBundleMany(
                ["192.0.2.42:5555", "192.0.2.43:5555"],
                tempRoot,
                new ApkInstallOptions(false, true, true, true),
                maxParallelism: 2);
            Assert.Equal(
                [
                    "apk", "install-bundle-many",
                    "--serial", "192.0.2.42:5555",
                    "--serial", "192.0.2.43:5555",
                    "--folder", Path.GetFullPath(tempRoot),
                    "--parallelism", "2",
                    "--no-replace", "--downgrade", "--grant-runtime-permissions", "--test-only"
                ],
                parallelCommand.CliArguments);
            Assert.Equal(command.ApkBundle.ApkPaths, parallelCommand.ApkBundle!.ApkPaths);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecutorRunsWifiAndParallelGuiCommandsThroughTypedCliContracts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"mqfm-new-operator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var apkPath = Path.Combine(tempRoot, "input.apk");
        var bundlePath = Path.Combine(tempRoot, "bundle");
        Directory.CreateDirectory(bundlePath);
        var baseApk = Path.Combine(bundlePath, "base.apk");
        var splitApk = Path.Combine(bundlePath, "split_config.en.apk");
        await File.WriteAllBytesAsync(apkPath, [1]);
        await File.WriteAllBytesAsync(baseApk, [1]);
        await File.WriteAllBytesAsync(splitApk, [2]);

        var disconnected = false;
        var runner = new RecordingCommandRunner((_, arguments) =>
        {
            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", "ip route"]))
            {
                return Success(
                    "192.0.2.0/24 dev wlan0 proto kernel scope link src 192.0.2.42 metric 303\n");
            }

            if (arguments.Count >= 2 && arguments[0] == "connect")
            {
                return Success($"connected to {arguments[1]}\n");
            }

            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success(
                    "List of devices attached\n" +
                    "192.0.2.42:5555 device model:Quest_3\n" +
                    (disconnected ? string.Empty : "192.0.2.43:5555 device model:Quest_3\n"));
            }

            if (arguments.SequenceEqual(["disconnect", "192.0.2.43:5555"]))
            {
                disconnected = true;
                return Success("disconnected 192.0.2.43:5555\n");
            }

            return Success("Success\n");
        });
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner));

        try
        {
            var enabled = await executor.ExecuteAsync(
                OperatorCommands.EnableWifiAdb("QUEST123", operatorConfirmed: true));
            var connected = await executor.ExecuteAsync(
                OperatorCommands.ConnectWifiAdb("192.0.2.43", operatorConfirmed: true));
            await executor.ExecuteAsync(
                OperatorCommands.DisconnectWifiAdb("192.0.2.43", operatorConfirmed: true));
            var progress = new RecordingProgress<OperatorProgress>();
            var single = await executor.ExecuteAsync(
                OperatorCommands.InstallApkMany(
                    ["192.0.2.42:5555", "192.0.2.43:5555"],
                    apkPath,
                    maxParallelism: 2),
                progress: progress);
            var bundle = await executor.ExecuteAsync(
                OperatorCommands.InstallApkBundleMany(
                    ["192.0.2.42:5555", "192.0.2.43:5555"],
                    bundlePath,
                    maxParallelism: 2));

            Assert.Equal("192.0.2.42:5555", enabled.WifiAdbEnableResult!.Endpoint);
            Assert.Equal("192.0.2.43:5555", connected.WifiAdbConnectionResult!.Endpoint);
            Assert.True(single.ParallelApkInstallResult!.Succeeded);
            Assert.True(bundle.ParallelApkInstallResult!.Succeeded);
            Assert.Contains(progress.Values, static value => value.Stage == "mutation-sent");
            Assert.Contains(progress.Values, static value => value.Stage == "mutation-pending");
            Assert.Contains(progress.Values, static value => value.Stage == "mutation-confirmed");
            Assert.Equal(2, progress.Values[^1].CompletedUnits);
            Assert.Equal(2, progress.Values[^1].TotalUnits);
            Assert.Contains(
                runner.Calls,
                static call => call.Arguments.SequenceEqual(["connect", "192.0.2.42:5555"]));
            Assert.Contains(
                runner.Calls,
                static call => call.Arguments.SequenceEqual(["disconnect", "192.0.2.43:5555"]));
            Assert.Equal(
                2,
                runner.Calls.Count(call => call.Arguments.Count > 2 && call.Arguments[2] == "install"));
            Assert.Equal(
                2,
                runner.Calls.Count(call => call.Arguments.Count > 2 && call.Arguments[2] == "install-multiple"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecutorRunsEveryGuiCommandThroughItsTypedCliContract()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"mqfm-operator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var inputPath = Path.Combine(tempRoot, "input.apk");
        var bundlePath = Path.Combine(tempRoot, "bundle");
        Directory.CreateDirectory(bundlePath);
        var baseApkPath = Path.Combine(bundlePath, "base.apk");
        var splitApkPath = Path.Combine(bundlePath, "split_config.en.apk");
        var pullPath = Path.Combine(tempRoot, "pulled.txt");
        var exportPath = Path.Combine(tempRoot, "exported.apk");
        await File.WriteAllBytesAsync(inputPath, [0x50, 0x4b, 0x03, 0x04]);
        await File.WriteAllBytesAsync(baseApkPath, [0x50, 0x4b, 0x03, 0x04]);
        await File.WriteAllBytesAsync(splitApkPath, [0x50, 0x4b, 0x03, 0x04]);

        var runner = new RecordingCommandRunner((fileName, arguments) =>
        {
            if (fileName == "aapt2-test")
            {
                return Success("package: name='com.example.app' versionCode='42' versionName='1.2.3'\n");
            }

            if (fileName == "apksigner-test")
            {
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            }

            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success("List of devices attached\nQUEST123 device model:Quest_3\n");
            }

            if (arguments.Contains("pm list packages -3", StringComparer.Ordinal))
            {
                return Success("package:com.example.app\n");
            }

            if (arguments.Any(static value => value.StartsWith("pm path ", StringComparison.Ordinal)))
            {
                return Success("package:/data/app/example/base.apk\n");
            }

            if (arguments.Contains("pull", StringComparer.Ordinal))
            {
                File.WriteAllBytes(arguments[^1], [0x50, 0x4b, 0x03, 0x04]);
                return Success("1 file pulled\n");
            }

            if (arguments.Any(static value => value.StartsWith("ls -1Ap", StringComparison.Ordinal)))
            {
                return Success("Download/\nexample.txt\n");
            }

            if (arguments.Any(static value => value.StartsWith("stat -c %s", StringComparison.Ordinal)))
            {
                return Success("4\n");
            }

            return Success("Success\n");
        });
        var executor = new OperatorCommandExecutor(new AdbClient(
            "adb-test",
            runner,
            new AndroidBuildToolPaths("aapt2-test", "apksigner-test")));

        try
        {
            var devices = await executor.ExecuteAsync(OperatorCommands.DiscoverDevices());
            var files = await executor.ExecuteAsync(OperatorCommands.ListFiles("QUEST123", "/sdcard"));
            await executor.ExecuteAsync(OperatorCommands.PullFile("QUEST123", "/sdcard/example.txt", pullPath));
            await executor.ExecuteAsync(OperatorCommands.PushFile("QUEST123", inputPath, "/sdcard/Download/input.apk"));
            var packages = await executor.ExecuteAsync(OperatorCommands.ListPackages("QUEST123"));
            var export = await executor.ExecuteAsync(
                OperatorCommands.ExportApk("QUEST123", "com.example.app", exportPath));
            await executor.ExecuteAsync(OperatorCommands.InstallApk(
                "QUEST123",
                inputPath,
                new ApkInstallOptions(true, true, false, true)));
            var bundle = await executor.ExecuteAsync(OperatorCommands.InstallApkBundle(
                "QUEST123",
                bundlePath,
                new ApkInstallOptions(true, false, true, false)));

            Assert.Single(devices.Devices!);
            Assert.Equal(2, files.RemoteEntries!.Count);
            Assert.Equal(["com.example.app"], packages.Packages);
            Assert.Equal(exportPath, export.ApkExportResult!.OutputPath);
            Assert.Equal(2, bundle.ApkBundleInstallResult!.ApkPaths.Count);
            Assert.Equal(new[] { "devices", "-l" }, runner.Calls[0].Arguments);
            Assert.All(
                runner.Calls.Where(static call => call.FileName == "adb-test").Skip(1),
                static call => Assert.Equal(new[] { "-s", "QUEST123" }, call.Arguments.Take(2)));
            Assert.Contains(
                runner.Calls,
                static call => call.Arguments.SequenceEqual(
                    ["-s", "QUEST123", "install", "-r", "-d", "-t", call.Arguments[^1]]));
            Assert.Contains(
                runner.Calls,
                call => call.Arguments.SequenceEqual(
                    ["-s", "QUEST123", "install-multiple", "-r", "-g", baseApkPath, splitApkPath]));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static CommandResult Success(string output) =>
        new("adb-test", Array.Empty<string>(), 0, output, string.Empty, TimeSpan.Zero);

    private sealed class RecordingCommandRunner(
        Func<string, IReadOnlyList<string>, CommandResult> handler) : IStreamingCommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add((fileName, arguments.ToArray()));
            }
            var handled = handler(fileName, arguments);
            return Task.FromResult(handled with { FileName = fileName, Arguments = arguments.ToArray() });
        }

        public async Task<StreamingCommandResult> RunToStreamAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            Stream destination,
            long maximumBytes,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add((fileName, arguments.ToArray()));
            }
            var handled = handler(fileName, arguments) with
            {
                FileName = fileName,
                Arguments = arguments.ToArray()
            };
            byte[] bytes = [0x50, 0x4b, 0x03, 0x04];
            if (handled.Succeeded)
            {
                if (bytes.Length > maximumBytes)
                    throw new FleetTransferLimitException(maximumBytes);
                await destination.WriteAsync(bytes, cancellationToken);
            }
            return new StreamingCommandResult(
                handled,
                handled.Succeeded ? bytes.Length : 0,
                Convert.ToHexString(SHA256.HashData(handled.Succeeded ? bytes : []))
                    .ToLowerInvariant());
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
