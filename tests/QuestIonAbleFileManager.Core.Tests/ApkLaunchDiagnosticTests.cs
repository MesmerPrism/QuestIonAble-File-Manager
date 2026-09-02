using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class ApkLaunchDiagnosticTests
{
    [Fact]
    public async Task ExactLaunchUsesOnePreArmedUidCaptureAndPublishesCreateNewBundle()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk));
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchAndCaptureInspectedApkAsync("QUEST123", apk, output);

            Assert.Equal(ApkLaunchDiagnosticDisposition.Completed, result.Disposition);
            Assert.True(result.Attempt.DispatchAttempted);
            Assert.True(result.Attempt.Launch!.ComponentObservedResumed);
            Assert.Equal([123, 456], result.Attempt.CurrentPackageProcessIds);
            Assert.Equal(1, runner.PidReadbackCount);
            Assert.Equal(1, runner.LaunchCount);
            Assert.True(runner.CaptureWasArmedBeforeLaunch);
            Assert.Equal(2, runner.DeviceDiscoveryCount);
            Assert.Contains("--uid=10234", runner.CaptureArguments);
            Assert.Contains("1700000000.123456789", runner.CaptureArguments);
            Assert.DoesNotContain(runner.CaptureArguments, value =>
                value.Contains("filter", StringComparison.OrdinalIgnoreCase));
            Assert.True(result.Capture.PostActionWindowElapsed);
            Assert.True(result.Capture.ProcessTreeCleanupSucceeded);
            Assert.False(result.Capture.OutputLimitReached);
            var logPath = Path.Combine(output, result.Capture.RelativePath);
            Assert.Equal("app-owned marker\n", await File.ReadAllTextAsync(logPath));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(logPath))).ToLowerInvariant(),
                result.Capture.Sha256);
            var manifestPath = Path.Combine(output, result.ManifestRelativePath);
            Assert.True(File.Exists(manifestPath));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath))).ToLowerInvariant(),
                result.ManifestSha256);
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
            Assert.Equal(
                "questionable.file_manager.apk_launch_diagnostic_manifest.v1",
                manifest.RootElement.GetProperty("schema").GetString());
            Assert.Equal("completed", manifest.RootElement.GetProperty("effectDisposition").GetString());
            Assert.False(manifest.RootElement.GetProperty("limitations").GetProperty("screenshotOrRecording").GetBoolean());
            await Assert.ThrowsAsync<ArgumentException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .LaunchAndCaptureInspectedApkAsync("QUEST123", apk, output));
            Assert.Equal(1, runner.LaunchCount);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task ExactSerialDriftAfterArmRejectsBeforeLaunchAndRetainsTypedEvidence()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk))
            {
                DriftReadySerialOnSecondDiscovery = true
            };
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchAndCaptureInspectedApkAsync("QUEST123", apk, output);

            Assert.Equal(ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch, result.Disposition);
            Assert.False(result.Attempt.DispatchAttempted);
            Assert.Equal(0, runner.LaunchCount);
            Assert.True(result.Capture.ProcessTreeCleanupSucceeded);
            Assert.True(Directory.Exists(output));
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task EarlyCaptureExitKeepsSuccessfulLaunchOutcomeUnknown()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk))
            {
                CaptureExitedEarly = true
            };
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchAndCaptureInspectedApkAsync("QUEST123", apk, output);

            Assert.Equal(ApkLaunchDiagnosticDisposition.OutcomeUnknown, result.Disposition);
            Assert.True(result.Attempt.DispatchAttempted);
            Assert.Equal(1, runner.LaunchCount);
            Assert.True(result.Capture.CaptureExitedEarly);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedPostCapturePidIdentityFailsClosedWithoutRetryingLaunch()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk))
            {
                MalformedPidReadback = true
            };
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchAndCaptureInspectedApkAsync("QUEST123", apk, output);

            Assert.Equal(ApkLaunchDiagnosticDisposition.OutcomeUnknown, result.Disposition);
            Assert.True(result.Attempt.DispatchAttempted);
            Assert.Equal(1, runner.LaunchCount);
            Assert.Equal(1, runner.PidReadbackCount);
            Assert.True(Directory.Exists(output));
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task MissingPostCapturePackageProcessKeepsResumedLaunchOutcomeUnknown()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk)) { NoPackageProcesses = true };
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchAndCaptureInspectedApkAsync("QUEST123", apk, output);

            Assert.Equal(ApkLaunchDiagnosticDisposition.OutcomeUnknown, result.Disposition);
            Assert.True(result.Attempt.Launch!.ComponentObservedResumed);
            Assert.Empty(result.Attempt.CurrentPackageProcessIds);
            Assert.Equal(1, runner.LaunchCount);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task FailedCaptureCleanupOverridesPredispatchRejection()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk))
            {
                DriftReadySerialOnSecondDiscovery = true,
                CaptureCleanupSucceeded = false
            };
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchAndCaptureInspectedApkAsync("QUEST123", apk, output);

            Assert.Equal(ApkLaunchDiagnosticDisposition.OutcomeUnknown, result.Disposition);
            Assert.False(result.Attempt.DispatchAttempted);
            Assert.Equal(0, runner.LaunchCount);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task InstalledSplitSetRejectsBeforeLoggerOrLaunch()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk)) { InstalledSplitPresent = true };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .LaunchAndCaptureInspectedApkAsync("QUEST123", apk, output));

            Assert.Equal(0, runner.LaunchCount);
            Assert.Empty(runner.CaptureArguments);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task SharedPackageUidRejectsBeforeLoggerOrLaunch()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk)) { SharedUidPackagePresent = true };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .LaunchAndCaptureInspectedApkAsync("QUEST123", apk, output));

            Assert.Equal(0, runner.LaunchCount);
            Assert.Empty(runner.CaptureArguments);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCollisionAfterLaunchRetainsBundleWithoutOverwritingRequestedPath()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        string? retained = null;
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk)) { PublishCollisionPath = output };
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchAndCaptureInspectedApkAsync("QUEST123", apk, output);
            retained = result.OutputDirectory;

            Assert.Equal(ApkLaunchDiagnosticDisposition.OutcomeUnknown, result.Disposition);
            Assert.False(result.PublishedAtRequestedPath);
            Assert.Equal(1, runner.LaunchCount);
            Assert.True(Directory.Exists(output));
            Assert.Empty(Directory.EnumerateFileSystemEntries(output));
            Assert.NotEqual(output, retained);
            Assert.True(File.Exists(Path.Combine(retained, result.ManifestRelativePath)));
            Assert.True(File.Exists(Path.Combine(retained, result.Capture.RelativePath)));
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            if (!string.IsNullOrWhiteSpace(retained) && Directory.Exists(retained))
                Directory.Delete(retained, recursive: true);
        }
    }

    [Fact]
    public async Task RealRunnerStopsAndHashesCaptureAtOutputLimit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var destination = new MemoryStream();
        var result = await new CommandRunner().RunArmedCaptureAsync(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/d", "/s", "/c", "for /L %i in (1,1,1000000) do @echo line-%i"],
            destination,
            1024,
            TimeSpan.FromSeconds(2),
            _ => Task.FromResult("armed"));

        Assert.Equal("armed", result.ActionResult);
        Assert.True(result.OutputLimitReached);
        Assert.True(result.ProcessTreeCleanupSucceeded);
        Assert.Equal(1024, result.BytesWritten);
        Assert.Equal(1024, destination.Length);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(destination.ToArray())).ToLowerInvariant(),
            result.Sha256);
    }

    [Fact]
    public async Task PredispatchRejectionEmitsOnlyTerminalRejectedMutation()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk))
            {
                DriftReadySerialOnSecondDiscovery = true
            };
            var progress = new RecordingProgress();
            var execution = await new OperatorCommandExecutor(
                    new AdbClient("adb", runner, new("aapt2", "apksigner")))
                .ExecuteAsync(
                    OperatorCommands.LaunchDiagnoseInspectedApp("QUEST123", apk, output),
                    progress: progress);

            var receipt = Assert.IsType<OperatorMutationReceipt>(execution.MutationReceipt);
            Assert.Equal(OperatorMutationStage.Rejected, receipt.Stage);
            Assert.Equal([OperatorMutationStage.Rejected], receipt.Transitions.Select(static item => item.Stage));
            Assert.DoesNotContain(progress.Values, static item =>
                item.Stage is "mutation-sent" or "mutation-pending");
            Assert.False(execution.ApkLaunchDiagnosticBundleResult!.Attempt.DispatchAttempted);
            Assert.Equal(0, runner.LaunchCount);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task PredispatchAdmissionExceptionEmitsNoMutationProgress()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk))
            {
                InstalledSplitPresent = true
            };
            var progress = new RecordingProgress();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new OperatorCommandExecutor(new AdbClient("adb", runner, new("aapt2", "apksigner")))
                    .ExecuteAsync(
                        OperatorCommands.LaunchDiagnoseInspectedApp("QUEST123", apk, output),
                        progress: progress));

            Assert.DoesNotContain(progress.Values, static item =>
                item.Stage.StartsWith("mutation-", StringComparison.Ordinal));
            Assert.Equal(0, runner.LaunchCount);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task PredispatchCaptureCleanupUncertaintyStillRejectsHeadsetMutation()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk))
            {
                DriftReadySerialOnSecondDiscovery = true,
                CaptureCleanupSucceeded = false
            };
            var execution = await new OperatorCommandExecutor(
                    new AdbClient("adb", runner, new("aapt2", "apksigner")))
                .ExecuteAsync(OperatorCommands.LaunchDiagnoseInspectedApp("QUEST123", apk, output));

            Assert.Equal(
                ApkLaunchDiagnosticDisposition.OutcomeUnknown,
                execution.ApkLaunchDiagnosticBundleResult!.Disposition);
            var receipt = Assert.IsType<OperatorMutationReceipt>(execution.MutationReceipt);
            Assert.Equal(OperatorMutationStage.Rejected, receipt.Stage);
            Assert.Equal([OperatorMutationStage.Rejected], receipt.Transitions.Select(static item => item.Stage));
            Assert.False(execution.ApkLaunchDiagnosticBundleResult.Attempt.DispatchAttempted);
            Assert.Equal(0, runner.LaunchCount);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task DispatchObserverStartsMutationOnlyAtExactLauncherDispatch()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk));
            var execution = await new OperatorCommandExecutor(
                    new AdbClient("adb", runner, new("aapt2", "apksigner")))
                .ExecuteAsync(OperatorCommands.LaunchDiagnoseInspectedApp("QUEST123", apk, output));

            var receipt = Assert.IsType<OperatorMutationReceipt>(execution.MutationReceipt);
            Assert.Equal(OperatorMutationStage.Confirmed, receipt.Stage);
            Assert.Equal(
                [OperatorMutationStage.Sent, OperatorMutationStage.Pending, OperatorMutationStage.Confirmed],
                receipt.Transitions.Select(static item => item.Stage));
            Assert.Equal(1, runner.LaunchCount);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task PostdispatchAmbiguityRetainsSentPendingMutation()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk)) { CaptureExitedEarly = true };
            var execution = await new OperatorCommandExecutor(
                    new AdbClient("adb", runner, new("aapt2", "apksigner")))
                .ExecuteAsync(OperatorCommands.LaunchDiagnoseInspectedApp("QUEST123", apk, output));

            var receipt = Assert.IsType<OperatorMutationReceipt>(execution.MutationReceipt);
            Assert.Equal(OperatorMutationStage.Pending, receipt.Stage);
            Assert.Equal(
                [OperatorMutationStage.Sent, OperatorMutationStage.Pending, OperatorMutationStage.Pending],
                receipt.Transitions.Select(static item => item.Stage));
            Assert.True(execution.ApkLaunchDiagnosticBundleResult!.Attempt.DispatchAttempted);
            Assert.Equal(1, runner.LaunchCount);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task PostdispatchExceptionCarriesReconcilablePendingMutation()
    {
        var apk = await CreateApkAsync();
        var output = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var runner = new LaunchDiagnosticRunner(File.ReadAllBytes(apk)) { ThrowAfterDispatch = true };
            var exception = await Assert.ThrowsAsync<OperatorMutationExecutionException>(() =>
                new OperatorCommandExecutor(new AdbClient("adb", runner, new("aapt2", "apksigner")))
                    .ExecuteAsync(OperatorCommands.LaunchDiagnoseInspectedApp("QUEST123", apk, output)));

            Assert.Equal(OperatorMutationStage.Pending, exception.MutationReceipt.Stage);
            Assert.Equal(
                [OperatorMutationStage.Sent, OperatorMutationStage.Pending],
                exception.MutationReceipt.Transitions.Select(static item => item.Stage));
            Assert.Equal(1, runner.LaunchCount);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void PostdispatchFailureJsonRetainsPendingMutationWithoutPrivateDetail()
    {
        var receipt = new OperatorMutationReceipt(
            "pc-test",
            OperatorCommandKind.LaunchDiagnoseInspectedApp,
            "QUEST123",
            "launch exact inspected APK and capture bounded evidence",
            OperatorMutationStage.Pending,
            "No matching effective state was confirmed.",
            HeadsetReadback: false,
            [
                new(OperatorMutationStage.Sent, DateTimeOffset.UnixEpoch, "sent"),
                new(OperatorMutationStage.Pending, DateTimeOffset.UnixEpoch.AddSeconds(1), "pending")
            ]);
        var exception = new OperatorMutationExecutionException(
            receipt,
            new InvalidOperationException("private failure detail"));
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(3, CliApplication.WriteApkLaunchDiagnosticFailure(exception));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("launch_pending", json.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.True(json.RootElement.GetProperty("failure").GetProperty("state_change_possible").GetBoolean());
        Assert.Equal("pending", json.RootElement.GetProperty("mutation").GetProperty("Stage").GetString());
        Assert.DoesNotContain("private failure detail", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationResistantPipeDrainHasBoundedTerminalJoin()
    {
        using var drainSource = new CancellationTokenSource();
        var cancellationResistantDrain = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ownershipRevoked = false;
        var started = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            CommandRunner.JoinArmedCaptureDrainsAsync(
                cancellationResistantDrain.Task,
                drainSource,
                () => ownershipRevoked = true,
                TimeSpan.FromMilliseconds(25)));
        started.Stop();

        Assert.Contains("stream-ownership revocation", exception.Message, StringComparison.Ordinal);
        Assert.True(drainSource.IsCancellationRequested);
        Assert.True(ownershipRevoked);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
        cancellationResistantDrain.SetResult();
    }

    [Fact]
    public async Task CancellationResistantDestinationCannotWriteAfterOwnershipRevocation()
    {
        using var drainSource = new CancellationTokenSource();
        using var destination = new CancellationResistantDestinationStream();
        var drain = destination.WriteAsync(Encoding.UTF8.GetBytes("late evidence"), CancellationToken.None).AsTask();

        var joined = await CommandRunner.JoinArmedCaptureDrainsAsync(
            drain,
            drainSource,
            destination.Dispose,
            TimeSpan.FromMilliseconds(25));

        Assert.False(joined);
        Assert.True(drain.IsCompleted);
        Assert.Equal(0, destination.CommittedBytes);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await destination.WriteAsync(Encoding.UTF8.GetBytes("post terminal"), CancellationToken.None));
        Assert.Equal(0, destination.CommittedBytes);
    }

    [Fact]
    public void CliParserRejectsEveryWidenedLaunchDiagnosticVector()
    {
        var valid = new[]
        {
            "apk", "launch-diagnose", "--serial", "QUEST123", "--file", "example.apk",
            "--output", "capture", "--json"
        };
        var command = OperatorCommands.ParseLaunchDiagnosticCliArguments(valid);
        Assert.Equal(OperatorCommandKind.LaunchDiagnoseInspectedApp, command.Kind);
        foreach (var extra in new[] { "--package", "--uid", "--pid", "--tag", "--duration", "--command", "--adb-arg" })
        {
            Assert.Throws<ArgumentException>(() =>
                OperatorCommands.ParseLaunchDiagnosticCliArguments([.. valid, extra, "value"]));
        }
    }

    private static async Task<string> CreateApkAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qfm-launch-diagnostic-{Guid.NewGuid():N}.apk");
        await File.WriteAllBytesAsync(path, [0x50, 0x4b, 0x03, 0x04]);
        return path;
    }

    private sealed class RecordingProgress : IProgress<OperatorProgress>
    {
        public List<OperatorProgress> Values { get; } = [];

        public void Report(OperatorProgress value) => Values.Add(value);
    }

    private sealed class LaunchDiagnosticRunner(byte[] apkBytes) : IArmedCaptureCommandRunner
    {
        private bool _captureArmed;

        public bool DriftReadySerialOnSecondDiscovery { get; init; }

        public bool CaptureExitedEarly { get; init; }

        public bool MalformedPidReadback { get; init; }

        public bool NoPackageProcesses { get; init; }

        public bool CaptureCleanupSucceeded { get; init; } = true;

        public bool InstalledSplitPresent { get; init; }

        public bool SharedUidPackagePresent { get; init; }

        public string? PublishCollisionPath { get; init; }

        public bool ThrowAfterDispatch { get; init; }

        public int LaunchCount { get; private set; }

        public int DeviceDiscoveryCount { get; private set; }

        public int PidReadbackCount { get; private set; }

        public bool CaptureWasArmedBeforeLaunch { get; private set; }

        public IReadOnlyList<string> CaptureArguments { get; private set; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (fileName == "aapt2")
                return Result(fileName, arguments, "package: name='com.example.app' versionCode='42' versionName='1.2.3'\n");
            if (fileName == "apksigner")
                return Result(fileName, arguments, "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                DeviceDiscoveryCount++;
                var state = DriftReadySerialOnSecondDiscovery && DeviceDiscoveryCount == 2 ? "offline" : "device";
                return Result(fileName, arguments,
                    $"List of devices attached\nQUEST123 {state} product:eureka model:Quest_3 transport_id:1\n");
            }
            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", "pm path 'com.example.app'"]))
                return Result(
                    fileName,
                    arguments,
                    InstalledSplitPresent
                        ? "package:/data/app/example/base.apk\npackage:/data/app/example/split_config.arm64_v8a.apk\n"
                        : "package:/data/app/example/base.apk\n");
            if (arguments.SequenceEqual([
                    "-s", "QUEST123", "shell", "pm", "list", "packages", "--user", "current", "-U"]))
                return Result(
                    fileName,
                    arguments,
                    SharedUidPackagePresent
                        ? "package:com.example.app uid:10234\npackage:com.example.shared uid:10234\n"
                        : "package:com.example.app uid:10234\npackage:com.example.other uid:10235\n");
            if (arguments.Contains("query-activities"))
                return Result(fileName, arguments, "com.example.app/.Main\n");
            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", "dumpsys", "package", "com.example.app"]))
                return Result(fileName, arguments,
                    "  Activity #0 ActivityInfo{abc com.example.app/.Main}\n    exported=true\n");
            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", "date", "+%s.%N"]))
                return Result(fileName, arguments, "1700000000.123456789\n");
            if (arguments.Count >= 6 && arguments[2] == "shell" && arguments[3] == "am" && arguments[4] == "start")
            {
                LaunchCount++;
                CaptureWasArmedBeforeLaunch = _captureArmed;
                return Result(fileName, arguments, "Starting: Intent\n");
            }
            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", "dumpsys", "activity", "activities"]))
                return Result(fileName, arguments,
                    "mResumedActivity: ActivityRecord{123 u0 com.example.app/.Main t1}\n");
            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", "ps", "-A", "-o", "UID,PID,ARGS"]))
            {
                PidReadbackCount++;
                return Result(
                    fileName,
                    arguments,
                    MalformedPidReadback ? "UID PID ARGS\nwrong 456 com.example.app\n" :
                    NoPackageProcesses ? "UID PID ARGS\n1000 22 system_server\n" :
                    "UID PID ARGS\n10234 456 com.example.app:worker --nice-name\n10234 123 com.example.app\n");
            }
            return Result(fileName, arguments, "Success\n");
        }

        public async Task<StreamingCommandResult> RunToStreamAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            Stream destination,
            long maximumBytes,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Assert.True(apkBytes.LongLength <= maximumBytes);
            await destination.WriteAsync(apkBytes, cancellationToken);
            return new StreamingCommandResult(
                new CommandResult(fileName, arguments.ToArray(), 0, "", "", TimeSpan.Zero),
                apkBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(apkBytes)).ToLowerInvariant());
        }

        public async Task<ArmedCaptureCommandResult<T>> RunArmedCaptureAsync<T>(
            string fileName,
            IReadOnlyList<string> arguments,
            Stream destination,
            long maximumBytes,
            TimeSpan postActionWindow,
            Func<CancellationToken, Task<T>> armedAction,
            CancellationToken cancellationToken = default)
        {
            CaptureArguments = arguments.ToArray();
            _captureArmed = true;
            var action = await armedAction(cancellationToken);
            if (ThrowAfterDispatch)
                throw new IOException("post-dispatch capture failure");
            var bytes = Encoding.UTF8.GetBytes("app-owned marker\n");
            await destination.WriteAsync(bytes, cancellationToken);
            if (!string.IsNullOrWhiteSpace(PublishCollisionPath))
                Directory.CreateDirectory(PublishCollisionPath);
            _captureArmed = false;
            return new ArmedCaptureCommandResult<T>(
                action,
                new CommandResult(fileName, arguments.ToArray(), -1, "", "", postActionWindow),
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                PostActionWindowElapsed: !CaptureExitedEarly,
                OutputLimitReached: false,
                CaptureExitedEarly,
                ProcessTreeCleanupSucceeded: CaptureCleanupSucceeded);
        }

        private static Task<CommandResult> Result(
            string fileName,
            IReadOnlyList<string> arguments,
            string output) => Task.FromResult(
                new CommandResult(fileName, arguments.ToArray(), 0, output, "", TimeSpan.Zero));
    }

    private sealed class CancellationResistantDestinationStream : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _isDisposed;

        public int CommittedBytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_isDisposed;
        public override long Length => CommittedBytes;
        public override long Position { get => CommittedBytes; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(CancellationResistantDestinationStream));
            await _disposed.Task.ConfigureAwait(false);
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(CancellationResistantDestinationStream));
            CommittedBytes += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            _isDisposed = true;
            _disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
