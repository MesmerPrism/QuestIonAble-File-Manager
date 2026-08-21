using QuestIonAbleFileManager.Core;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class InspectedDeploymentTests
{
    [Theory]
    [InlineData("apksigner-print-certs-build-tools-34.txt")]
    [InlineData("apksigner-print-certs-build-tools-36.txt")]
    [InlineData("apksigner-print-certs-build-tools-37.txt")]
    public async Task Inspect_AcceptsExactBuildToolsSignerOutputFixtures(string fixtureName)
    {
        var apk = await CreateApkAsync();
        var signerOutput = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "QuestIonAbleFileManager.Core.Tests",
            "Fixtures",
            fixtureName));
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success("package: name='io.github.mesmerprism.rustykiosk.labs' " +
                      "versionCode='60609' versionName='0.6.6-alpha.9'\n")
            : Success(signerOutput));
        try
        {
            var result = await new ApkArtifactInspector(
                runner, new AndroidBuildToolPaths("aapt2", "apksigner")).InspectAsync(apk);

            Assert.Equal(
                "423d20004c79dd140c692e31aa80369cd3677b1ae2688dbd75011a4c83a0f1fb",
                result.Identity.SignerSha256);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Inspect_BuildTools37RejectsMultipleDistinctCurrentSigners()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success("package: name='com.example.app' versionCode='42'\n")
            : Success(
                "V2 Signer #1: certificate SHA-256 digest: " + new string('a', 64) + "\n" +
                "V2 Signer #2: certificate SHA-256 digest: " + new string('b', 64) + "\n"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ApkArtifactInspector(
                    runner, new AndroidBuildToolPaths("aapt2", "apksigner")).InspectAsync(apk));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Inspect_BuildTools37IgnoresSourceStampAndPastLineageSigners()
    {
        var apk = await CreateApkAsync();
        var current = new string('a', 64);
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success("package: name='com.example.app' versionCode='42'\n")
            : Success(
                $"V3.1 Signer: (minSdkVersion=35 (dev release=true), maxSdkVersion=36) " +
                $"certificate SHA-256 digest: {current}\n" +
                $"V3.0 Signer: (minSdkVersion=28, maxSdkVersion=34) " +
                $"certificate SHA-256 digest: {current}\n" +
                $"Source Stamp Signer: certificate SHA-256 digest: {new string('b', 64)}\n" +
                $"Signer #1 in lineage certificate SHA-256 digest: {new string('c', 64)}\n"));
        try
        {
            var result = await new ApkArtifactInspector(
                runner, new AndroidBuildToolPaths("aapt2", "apksigner")).InspectAsync(apk);

            Assert.Equal(current, result.Identity.SignerSha256);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Inspect_BuildTools37AcceptsV30SingleCurrentSigner()
    {
        var apk = await CreateApkAsync();
        var current = new string('a', 64);
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success("package: name='com.example.app' versionCode='42'\n")
            : Success($"V3.0 Signer: certificate SHA-256 digest: {current}\n"));
        try
        {
            var result = await new ApkArtifactInspector(
                runner, new AndroidBuildToolPaths("aapt2", "apksigner")).InspectAsync(apk);

            Assert.Equal(current, result.Identity.SignerSha256);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Inspect_BuildTools37RejectsDistinctV31EffectiveRangeSigners()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success("package: name='com.example.app' versionCode='42'\n")
            : Success(
                $"V3.1 Signer: (minSdkVersion=35, maxSdkVersion=36) " +
                $"certificate SHA-256 digest: {new string('a', 64)}\n" +
                $"V3.0 Signer: (minSdkVersion=28, maxSdkVersion=34) " +
                $"certificate SHA-256 digest: {new string('b', 64)}\n"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ApkArtifactInspector(
                    runner, new AndroidBuildToolPaths("aapt2", "apksigner")).InspectAsync(apk));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Inspect_BuildTools37RejectsV32HybridSignerSetExplicitly()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success("package: name='com.example.app' versionCode='42'\n")
            : Success(
                $"V3.2 Hybrid Classical Signer: (minSdkVersion=35, maxSdkVersion=36) " +
                $"certificate SHA-256 digest: {new string('a', 64)}\n" +
                $"V3.2 Hybrid PQC Signer: (minSdkVersion=35, maxSdkVersion=36) " +
                $"certificate SHA-256 digest: {new string('b', 64)}\n"));
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ApkArtifactInspector(
                    runner, new AndroidBuildToolPaths("aapt2", "apksigner")).InspectAsync(apk));

            Assert.Contains("v3.2 hybrid signers", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Inspect_RejectsWhenOnlyNonCurrentCertificateLinesExist()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success("package: name='com.example.app' versionCode='42'\n")
            : Success(
                $"Source Stamp Signer: certificate SHA-256 digest: {new string('a', 64)}\n" +
                $"Signer #1 in lineage certificate SHA-256 digest: {new string('b', 64)}\n"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ApkArtifactInspector(
                    runner, new AndroidBuildToolPaths("aapt2", "apksigner")).InspectAsync(apk));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Inspect_ReturnsContentAndExactManifestSignerFacts()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success("package: name='com.example.app' versionCode='42' versionName='1.2.3'\n")
            : Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n"));
        try
        {
            var result = await new ApkArtifactInspector(
                runner, new AndroidBuildToolPaths("aapt2", "apksigner")).InspectAsync(apk);

            Assert.Equal("com.example.app", result.Identity.PackageName);
            Assert.Equal(42, result.Identity.VersionCode);
            Assert.Equal("1.2.3", result.Identity.VersionName);
            Assert.Equal(new string('a', 64), result.Identity.SignerSha256);
            Assert.Equal(4, result.SizeBytes);
            Assert.Equal(64, result.Sha256.Length);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task InspectManifest_RejectsUncomparableSdkCodename()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success(
                "package: name='com.example.app' versionCode='42'\n" +
                "sdkVersion:'PreviewCodename'\n")
            : Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n"));
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ApkArtifactInspector(
                    runner, new AndroidBuildToolPaths("aapt2", "apksigner"))
                    .InspectManifestAsync(apk));

            Assert.Contains("minimum SDK", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Inspect_AcceptsCallerOwnedReadOnlyArtifact()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "QuestIonAbleFileManager.LongReadOnly",
            Guid.NewGuid().ToString("N"));
        var root = Path.Combine(
            testRoot,
            new string('a', 80),
            new string('b', 80),
            new string('c', 80));
        var apk = Path.Combine(root, "read-only.apk");
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success("package: name='com.example.readonly' versionCode='7' versionName='1.0'\n")
            : Success("Signer #1 certificate SHA-256 digest: " + new string('b', 64) + "\n"));
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(apk, [1, 2, 3, 4]);
            Assert.True(apk.Length > 260);
            File.SetAttributes(apk, File.GetAttributes(apk) | FileAttributes.ReadOnly);
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .InspectApkAsync(apk);

            Assert.Equal("com.example.readonly", result.Identity.PackageName);
            Assert.Equal(4, result.SizeBytes);
            Assert.True(File.GetAttributes(apk).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(apk, File.GetAttributes(apk) & ~FileAttributes.ReadOnly);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Inspect_RejectsAmbiguousSignerAndSplitArtifact()
    {
        var apk = await CreateApkAsync();
        try
        {
            var ambiguous = new FakeRunner((file, _) => file == "aapt2"
                ? Success("package: name='com.example.app' versionCode='42'\n")
                : Success(
                    "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n" +
                    "Signer #2 certificate SHA-256 digest: " + new string('b', 64) + "\n"));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ApkArtifactInspector(ambiguous, new("aapt2", "apksigner")).InspectAsync(apk));

            var split = CreateDeploymentRunner(apk, splitName: "config.en");
            var client = new AdbClient("adb", split, new("aapt2", "apksigner"));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => client.InstallInspectedApkAsync("QUEST123", apk));
            Assert.DoesNotContain(split.Calls, call => call.Arguments.Contains("install"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Install_ConfirmsOnlyExactArtifactIdentityOnExactSerial()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(apk, probeInstallImmutability: true);
        try
        {
            var executor = new OperatorCommandExecutor(
                new AdbClient("adb", runner, new("aapt2", "apksigner")));
            var execution = await executor.ExecuteAsync(OperatorCommands.InstallApk("QUEST123", apk));

            Assert.Equal(OperatorMutationStage.Confirmed, execution.MutationReceipt!.Stage);
            Assert.Equal("QUEST123", execution.InspectedApkInstallResult!.Installed.Serial);
            Assert.Contains(execution.ApkArtifactInspection!.Sha256, execution.MutationReceipt.ObservedState);
            var install = Assert.Single(runner.Calls, call =>
                call.Arguments.Count >= 4 &&
                call.Arguments[2] == "install");
            Assert.NotEqual(Path.GetFullPath(apk), install.Arguments[^1]);
            Assert.Contains("QuestIonAbleFileManager.ApkAdmission", install.Arguments[^1],
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "pm path 'com.example.app'"]));
            Assert.Contains(runner.Calls, call =>
                call.Arguments.Count >= 3 && call.Arguments[2] == "exec-out");
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Count >= 3 && call.Arguments[2] == "pull");
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Preflight_ProvesExactInstalledArtifactWithoutMutation()
    {
        var apk = await CreateApkAsync();
        var runner = CreatePreflightRunner(apk, installed: true, deviceApiLevel: 32);
        try
        {
            var executor = new OperatorCommandExecutor(
                new AdbClient("adb", runner, new("aapt2", "apksigner")));

            var execution = await executor.ExecuteAsync(
                OperatorCommands.PreflightInspectedApp("QUEST123", apk));
            var result = Assert.IsType<ApkPreflightResult>(execution.ApkPreflightResult);

            Assert.Equal("questionable.file_manager.apk_preflight.v1", result.PreflightContract);
            Assert.Equal(23, result.Manifest.MinimumSdkVersion);
            Assert.Equal(35, result.Manifest.TargetSdkVersion);
            Assert.Equal(["com.example.app.Main"], result.Manifest.LauncherActivities);
            Assert.Equal(32, result.DeviceApiLevel);
            Assert.Equal(InstalledApkMatch.Exact, result.InstalledMatch);
            Assert.Equal("com.example.app/.Main", result.LauncherComponent);
            Assert.True(result.ReadyForDeploy);
            Assert.True(result.ReadyForLaunch);
            Assert.True(result.ReadyForDiagnose);
            Assert.Null(execution.MutationReceipt);
            Assert.All(result.NextCommands, command => Assert.True(command.Ready));
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Contains("install") ||
                call.Arguments.Contains("am") ||
                call.Arguments.Contains("logcat"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Preflight_TreatsAbsentInstallAsDeployReadyButNotLaunchOrDiagnoseReady()
    {
        var apk = await CreateApkAsync();
        var runner = CreatePreflightRunner(apk, installed: false, deviceApiLevel: 32);
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .PreflightInspectedApkAsync("QUEST123", apk);

            Assert.Equal(InstalledApkMatch.Absent, result.InstalledMatch);
            Assert.True(result.ReadyForDeploy);
            Assert.False(result.ReadyForLaunch);
            Assert.False(result.ReadyForDiagnose);
            Assert.Null(result.LauncherComponent);
            Assert.True(result.NextCommands.Single(command =>
                command.Purpose == "install_launch_observe").Ready);
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("query-activities"));
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("dumpsys"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Preflight_TreatsConfirmedSilentPmPathAbsenceAsDeployReady()
    {
        var apk = await CreateApkAsync();
        var runner = CreatePreflightRunner(
            apk,
            installed: false,
            deviceApiLevel: 32,
            packagePathResult: SilentFailure());
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .PreflightInspectedApkAsync("QUEST123", apk);

            Assert.Equal(InstalledApkMatch.Absent, result.InstalledMatch);
            Assert.True(result.ReadyForDeploy);
            Assert.False(result.ReadyForLaunch);
            Assert.False(result.ReadyForDiagnose);
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "pm path 'com.example.app'"]));
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "pm list packages 'com.example.app'"]));
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("install"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Preflight_PropagatesSilentPmPathFailureWhenAbsenceCannotBeConfirmed()
    {
        var apk = await CreateApkAsync();
        var runner = CreatePreflightRunner(
            apk,
            installed: false,
            deviceApiLevel: 32,
            packagePathResult: SilentFailure(),
            packageListResult: SilentFailure());
        try
        {
            await Assert.ThrowsAsync<AdbCommandException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .PreflightInspectedApkAsync("QUEST123", apk));

            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "pm list packages 'com.example.app'"]));
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("install"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Preflight_DoesNotConfirmNonSilentPmPathFailureAsAbsence()
    {
        var apk = await CreateApkAsync();
        var runner = CreatePreflightRunner(
            apk,
            installed: false,
            deviceApiLevel: 32,
            packagePathResult: new CommandResult("", [], 1, "", "unexpected package error\n", TimeSpan.Zero));
        try
        {
            await Assert.ThrowsAsync<AdbCommandException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .PreflightInspectedApkAsync("QUEST123", apk));

            Assert.DoesNotContain(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "pm list packages 'com.example.app'"]));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Preflight_DoesNotConfirmNonOnePmPathFailureAsAbsence()
    {
        var apk = await CreateApkAsync();
        var runner = CreatePreflightRunner(
            apk,
            installed: false,
            deviceApiLevel: 32,
            packagePathResult: new CommandResult("", [], 2, "", "", TimeSpan.Zero));
        try
        {
            await Assert.ThrowsAsync<AdbCommandException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .PreflightInspectedApkAsync("QUEST123", apk));

            Assert.DoesNotContain(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "pm list packages 'com.example.app'"]));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Preflight_DoesNotTreatNonEmptyPackageListAsAbsence()
    {
        var apk = await CreateApkAsync();
        var runner = CreatePreflightRunner(
            apk,
            installed: false,
            deviceApiLevel: 32,
            packagePathResult: SilentFailure(),
            packageListResult: Success("package:com.example.app\n"));
        try
        {
            await Assert.ThrowsAsync<AdbCommandException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .PreflightInspectedApkAsync("QUEST123", apk));

            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "pm list packages 'com.example.app'"]));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Preflight_ReportsApiIncompatibilityWithoutDeviceMutation()
    {
        var apk = await CreateApkAsync();
        var runner = CreatePreflightRunner(apk, installed: false, deviceApiLevel: 22);
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .PreflightInspectedApkAsync("QUEST123", apk);

            Assert.False(result.ReadyForDeploy);
            Assert.Contains(result.Checks, check =>
                check.Id == "device.api_compatible" && !check.Passed);
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Contains("install") || call.Arguments.Contains("start"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Deploy_UsesOneAdmittedArtifactForInstallLaunchAndRuntimeEvidence()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(
            apk,
            activities:
                "mResumedActivity: ActivityRecord{1 com.example.app/.Main}\n" +
                "topResumedActivity=ActivityRecord{2 com.example.app/.Main}\n",
            probeInstallImmutability: true);
        try
        {
            var executor = new OperatorCommandExecutor(
                new AdbClient("adb", runner, new("aapt2", "apksigner")));

            var execution = await executor.ExecuteAsync(
                OperatorCommands.DeployInspectedApp("QUEST123", apk));
            var deployment = Assert.IsType<InspectedApkDeploymentResult>(
                execution.InspectedApkDeploymentResult);

            Assert.Equal("questionable.file_manager.apk_deployment.v1", deployment.DeploymentContract);
            Assert.Equal(Path.GetFullPath(apk), deployment.Install.Artifact.Path);
            Assert.Equal(Path.GetFullPath(apk), deployment.Launch.Artifact.Path);
            Assert.Equal(Path.GetFullPath(apk), deployment.Runtime.Artifact.Path);
            Assert.Equal(deployment.Install.Artifact.Sha256, deployment.Launch.Artifact.Sha256);
            Assert.Equal(deployment.Install.Artifact.Sha256, deployment.Runtime.Artifact.Sha256);
            Assert.True(deployment.Launch.ComponentObservedResumed);
            Assert.True(deployment.Runtime.ProcessAlive);
            Assert.True(deployment.Runtime.IsForeground);
            Assert.True(deployment.Runtime.IsTopResumed);
            Assert.Equal(OperatorMutationStage.Confirmed, execution.MutationReceipt!.Stage);
            Assert.Contains("process-alive=true", execution.MutationReceipt.ObservedState);

            var install = Assert.Single(runner.Calls, call =>
                call.Arguments.Count >= 4 && call.Arguments[2] == "install");
            Assert.NotEqual(Path.GetFullPath(apk), install.Arguments[^1]);
            Assert.Contains(
                "QuestIonAbleFileManager.ApkAdmission",
                install.Arguments[^1],
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, runner.Calls.Count(call => call.FileName == "aapt2"));
            Assert.Equal(2, runner.Calls.Count(call => call.FileName == "apksigner"));
            Assert.Equal(2, runner.Calls.Count(call =>
                call.Arguments.Count >= 3 && call.Arguments[2] == "exec-out"));
            Assert.Single(runner.Calls, call =>
                call.Arguments.Count >= 6 &&
                call.Arguments[3] == "am" &&
                call.Arguments[4] == "start");
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Deploy_RejectsMissingOrNonApkInputBeforeDeviceCommands()
    {
        var runner = new FakeRunner((_, _) => Success("unexpected\n"));
        var client = new AdbClient("adb", runner, new("aapt2", "apksigner"));
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.apk");
        var wrongExtension = Path.Combine(Path.GetTempPath(), $"qfm-public-test-{Guid.NewGuid():N}.zip");
        await File.WriteAllBytesAsync(wrongExtension, [0x50, 0x4b, 0x03, 0x04]);
        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                client.DeployInspectedApkAsync("QUEST123", missing));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                client.DeployInspectedApkAsync("QUEST123", wrongExtension));
            Assert.Empty(runner.Calls);
        }
        finally
        {
            File.Delete(wrongExtension);
        }
    }

    [Fact]
    public async Task Diagnose_WritesAtomicBoundedPackageScopedBundleWithoutMutation()
    {
        var apk = await CreateApkAsync();
        var parent = Path.Combine(Path.GetTempPath(), $"qfm-diagnostic-test-{Guid.NewGuid():N}");
        var output = Path.Combine(parent, "capture");
        Directory.CreateDirectory(parent);
        var runner = CreateDeploymentRunner(
            apk,
            activities:
                "mResumedActivity: ActivityRecord{1 com.example.app/.Main}\n" +
                "topResumedActivity=ActivityRecord{2 com.example.app/.Main}\n");
        try
        {
            var client = new AdbClient("adb", runner, new("aapt2", "apksigner"));

            var result = await client.CaptureInspectedApkDiagnosticsAsync(
                "QUEST123",
                apk,
                output);

            Assert.Equal("questionable.file_manager.apk_diagnostic_bundle.v1", result.DiagnosticContract);
            Assert.Equal(Path.GetFullPath(output), result.OutputDirectory);
            Assert.Equal(Path.GetFullPath(apk), result.Artifact.Path);
            Assert.Equal(result.Artifact.Sha256, result.Installed.BaseApkSha256);
            Assert.Equal(0, result.FailedCaptureCount);
            Assert.Equal(7, result.Files.Count);
            Assert.All(result.Files, file =>
            {
                Assert.True(File.Exists(Path.Combine(output, file.RelativePath)));
                Assert.Equal(64, file.Sha256.Length);
            });
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(output, "diagnostic-manifest.json")));
            Assert.Equal(
                "questionable.file_manager.apk_diagnostic_manifest.v1",
                manifest.RootElement.GetProperty("schema").GetString());
            Assert.Equal(
                "com.example.app",
                manifest.RootElement.GetProperty("artifact")
                    .GetProperty("identity")
                    .GetProperty("packageName")
                    .GetString());
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "dumpsys", "meminfo", "com.example.app"]));
            Assert.Equal(2, runner.Calls.Count(call =>
                call.Arguments.Count == 8 &&
                call.Arguments[2] == "shell" &&
                call.Arguments[3] == "logcat" &&
                call.Arguments[4] == "-d" &&
                call.Arguments[5] == "-t" &&
                call.Arguments[6] == "400" &&
                call.Arguments[7].StartsWith("--pid=", StringComparison.Ordinal)));
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("install"));
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("am"));

            var callCount = runner.Calls.Count;
            await Assert.ThrowsAsync<ArgumentException>(() =>
                client.CaptureInspectedApkDiagnosticsAsync("QUEST123", apk, output));
            Assert.Equal(callCount, runner.Calls.Count);
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Diagnose_RejectsAbsentPackageBeforeRuntimeOrDiagnosticProbes()
    {
        var apk = await CreateApkAsync();
        var parent = Path.Combine(Path.GetTempPath(), $"qfm-diagnostic-absent-{Guid.NewGuid():N}");
        var output = Path.Combine(parent, "capture");
        Directory.CreateDirectory(parent);
        var runner = new FakeRunner((file, arguments) =>
        {
            if (file == "aapt2")
                return Success("package: name='com.example.app' versionCode='42'\n");
            if (file == "apksigner")
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            if (arguments.Any(value => value.StartsWith("pm path ", StringComparison.Ordinal)))
                return Success("");
            return Success("unexpected\n");
        });
        try
        {
            await Assert.ThrowsAsync<PackageNotInstalledException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .CaptureInspectedApkDiagnosticsAsync("QUEST123", apk, output));

            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Contains("dumpsys") ||
                call.Arguments.Contains("pidof") ||
                call.Arguments.Contains("logcat") ||
                call.Arguments.Contains("getprop"));
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task Observe_StreamsExactOpenedApkWithoutAdvancingDescriptorBeforeCat()
    {
        var bytes = Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var apk = await CreateApkAsync(bytes);
        var runner = CreateDeploymentRunner(apk);
        try
        {
            var observation = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .ObserveInspectedAppAsync("QUEST123", apk);

            var installed = Assert.IsType<InstalledApkIdentity>(observation.Installed);
            Assert.Equal(bytes.LongLength, installed.BaseApkSizeBytes);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                installed.BaseApkSha256);
            Assert.Equal(bytes.LongLength, Assert.Single(runner.StreamMaximumBytes));

            var stream = Assert.Single(runner.Calls, call =>
                call.Arguments.Count == 6 && call.Arguments[2] == "exec-out");
            var command = stream.Arguments[5];
            Assert.Contains("exec 3<\"$candidate\"", command, StringComparison.Ordinal);
            Assert.Contains(
                "opened=$(readlink /proc/$$/fd/3)",
                command,
                StringComparison.Ordinal);
            Assert.Single(Regex.Matches(command, "/proc/\\$\\$/fd/3").Cast<Match>());
            Assert.DoesNotContain("/proc/self/fd/3", command, StringComparison.Ordinal);
            Assert.DoesNotContain("stat ", command, StringComparison.Ordinal);
            Assert.DoesNotContain("-f /proc/self/fd/3", command, StringComparison.Ordinal);
            Assert.EndsWith("exec cat <&3", command, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_RejectsAmbiguousLauncherBeforeStart()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(
            apk,
            launcherOutput: "com.example.app/.First\ncom.example.app/.Second\n");
        try
        {
            var client = new AdbClient("adb", runner, new("aapt2", "apksigner"));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => client.LaunchInspectedAppAsync("QUEST123", apk));
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Count >= 5 && call.Arguments[3] == "start");
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_RequiresExplicitExportedProofBeforeDispatch()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(apk, launcherExported: false);
        try
        {
            var client = new AdbClient("adb", runner, new("aapt2", "apksigner"));
            var rejected = await Assert.ThrowsAsync<InvalidDataException>(
                () => client.LaunchInspectedAppAsync("QUEST123", apk));
            Assert.Contains("not proven exported", rejected.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Count >= 5 && call.Arguments[3] == "am" &&
                call.Arguments[4] == "start");
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_DoesNotBorrowExportedProofFromAdjacentActivityInfo()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(
            apk,
            packageDump:
                "  Activity #0 ActivityInfo{abc com.example.app/.Main}\n" +
                "    ActivityInfo{def com.example.app/.Other}\n" +
                "      exported=true\n");
        try
        {
            var rejected = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .LaunchInspectedAppAsync("QUEST123", apk));
            Assert.Contains("not proven exported", rejected.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Count >= 5 && call.Arguments[3] == "am" &&
                call.Arguments[4] == "start");
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_AcceptsSanitizedCurrentQuestResolverProofWithoutActivityInfoBlock()
    {
        var apk = await CreateApkAsync();
        const string packageName = "io.github.mesmerprism.rustyquest.spatial_camera_panel";
        const string activityName = ".SpatialCameraPanelActivity";
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "QuestIonAbleFileManager.Core.Tests",
            "Fixtures",
            "current-quest-launcher-proof.v1.json")));
        var queryOutput = fixture.RootElement.GetProperty("query_stdout").GetString()!;
        var packageDump = fixture.RootElement.GetProperty("package_dump_excerpt").GetString()!;
        var runner = CreateDeploymentRunner(
            apk,
            launcherOutput: queryOutput,
            activities:
                $"mResumedActivity: ActivityRecord{{abc u0 {packageName}/{activityName} t1}}\n",
            packageDump: packageDump,
            packageName: packageName,
            activityName: activityName);
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchInspectedAppAsync("QUEST123", apk);

            Assert.Equal($"{packageName}/{activityName}", result.Component);
            Assert.True(result.ComponentObservedResumed);
            Assert.False(result.LauncherIsActivityAlias);
            Assert.Null(result.LauncherTargetActivity);
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "am", "start", "-n", $"{packageName}/{activityName}"]));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_AcceptsUniquelyProvenExportedAliasAndDispatchesTheAlias()
    {
        var apk = await CreateApkAsync();
        const string aliasComponent = "com.example.app/.LaunchAlias";
        const string targetComponent = "com.example.app/com.example.app.RealMain";
        var runner = CreateDeploymentRunner(
            apk,
            launcherOutput: aliasComponent + "\n",
            activities:
                "topResumedActivity=ActivityRecord{abc u0 com.example.app/.RealMain t1}\n",
            packageDump:
                "  Activity #0 ActivityInfo{abc com.example.app/.LaunchAlias}\n" +
                "    exported=true\n" +
                "    isAlias=true\n" +
                "    targetActivity=.RealMain\n");
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchInspectedAppAsync("QUEST123", apk);

            Assert.Equal(aliasComponent, result.Component);
            Assert.True(result.ComponentObservedResumed);
            Assert.True(result.LauncherIsActivityAlias);
            Assert.Equal(targetComponent, result.LauncherTargetActivity);
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "am", "start", "-n", aliasComponent]));
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "am", "start", "-n", targetComponent]));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_AcceptsUniquelyProvenExportedAliasFromResolverFallback()
    {
        var apk = await CreateApkAsync();
        const string aliasComponent = "com.example.app/.LaunchAlias";
        var runner = CreateDeploymentRunner(
            apk,
            launcherOutput: aliasComponent + "\n",
            activities:
                "mResumedActivity: ActivityRecord{abc u0 com.example.app/.RealMain t1}\n",
            packageDump:
                "Activity Resolver Table:\n" +
                "  Non-Data Actions:\n" +
                "      android.intent.action.MAIN:\n" +
                "        2c9cd07 com.example.app/.LaunchAlias filter 6ce9e34\n" +
                "          Action: \"android.intent.action.MAIN\"\n" +
                "          Category: \"android.intent.category.LAUNCHER\"\n" +
                "          isAlias=true\n" +
                "          targetActivity=.RealMain\n");
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchInspectedAppAsync("QUEST123", apk);

            Assert.Equal(aliasComponent, result.Component);
            Assert.True(result.ComponentObservedResumed);
            Assert.True(result.LauncherIsActivityAlias);
            Assert.Equal("com.example.app/com.example.app.RealMain", result.LauncherTargetActivity);
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "am", "start", "-n", aliasComponent]));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Theory]
    [InlineData(
        "  Activity #0 ActivityInfo{abc com.example.app/.LaunchAlias}\n" +
        "    exported=true\n" +
        "    targetActivity=.RealMain\n")]
    [InlineData(
        "  Activity #0 ActivityInfo{abc com.example.app/.LaunchAlias}\n" +
        "    exported=true\n" +
        "    isAlias=true\n")]
    [InlineData(
        "  Activity #0 ActivityInfo{abc com.example.app/.LaunchAlias}\n" +
        "    exported=true\n" +
        "    isAlias=true\n" +
        "    targetActivity=other.package/.RealMain\n")]
    public async Task Launch_RejectsAliasWithoutCompleteSamePackageProofBeforeDispatch(
        string packageDump)
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(
            apk,
            launcherOutput: "com.example.app/.LaunchAlias\n",
            packageDump: packageDump);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .LaunchInspectedAppAsync("QUEST123", apk));
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Count >= 6 &&
                call.Arguments[3] == "am" && call.Arguments[4] == "start");
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_AcceptsFullClassActivityHeaderWithExactlyOneExportedTrueField()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(
            apk,
            packageDump:
                "  Activity{abc com.example.app/com.example.app.Main}:\n" +
                "    enabled=true exported=true isAlias=false directBootAware=false\n");
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchInspectedAppAsync("QUEST123", apk);

            Assert.Equal("com.example.app/.Main", result.Component);
            Assert.False(result.LauncherIsActivityAlias);
            Assert.Null(result.LauncherTargetActivity);
            Assert.Contains(runner.Calls, call => call.Arguments.Count >= 6 &&
                call.Arguments[3] == "am" && call.Arguments[4] == "start");
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_NormalizesFullQueryAndShorthandResumedReadback()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(
            apk,
            launcherOutput: "com.example.app/com.example.app.Main\n",
            activities:
                "topResumedActivity=ActivityRecord{abc u0 com.example.app/.Main t1}\n");
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchInspectedAppAsync("QUEST123", apk);

            Assert.Equal("com.example.app/com.example.app.Main", result.Component);
            Assert.True(result.ComponentObservedResumed);
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                [
                    "-s", "QUEST123", "shell", "am", "start", "-n",
                    "com.example.app/com.example.app.Main"
                ]));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_DoesNotTreatAComponentPrefixAsExactResumedReadback()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(
            apk,
            activities:
                "mResumedActivity: ActivityRecord{abc u0 com.example.app/.MainOther t1}\n");
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchInspectedAppAsync("QUEST123", apk);

            Assert.False(result.ComponentObservedResumed);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Theory]
    [InlineData("other.package/.Main\n")]
    [InlineData("com.example.app/Main\n")]
    [InlineData("com.example.app/.Main\nwarning\n")]
    public async Task Launch_RejectsCrossPackageMalformedOrAdditionalQueryOutput(string queryOutput)
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(apk, launcherOutput: queryOutput);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .LaunchInspectedAppAsync("QUEST123", apk));
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Count >= 6 &&
                call.Arguments[3] == "am" && call.Arguments[4] == "start");
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_AcceptsBoundLauncherClassOutsideApplicationIdNamespace()
    {
        var apk = await CreateApkAsync();
        const string packageName = "com.example.app.debug";
        const string activityName = "com.example.app.SpatialActivity";
        var component = $"{packageName}/{activityName}";
        var runner = CreateDeploymentRunner(
            apk,
            packageName: packageName,
            activityName: activityName,
            activities: $"topResumedActivity=ActivityRecord{{abc u0 {component} t1}}\n");
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .LaunchInspectedAppAsync("QUEST123", apk);

            Assert.Equal(component, result.Component);
            Assert.True(result.ComponentObservedResumed);
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "am", "start", "-n", component]));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Theory]
    [InlineData(
        "Activity Resolver Table:\n" +
        "  Non-Data Actions:\n" +
        "      android.intent.action.MAIN:\n" +
        "        2c9cd07 com.example.app/.Main filter 6ce9e34\n" +
        "          Action: \"android.intent.action.MAIN\"\n")]
    [InlineData(
        "Activity Resolver Table:\n" +
        "  Non-Data Actions:\n" +
        "      android.intent.action.MAIN:\n" +
        "        2c9cd07 com.example.app/.Other filter 6ce9e34\n" +
        "          Action: \"android.intent.action.MAIN\"\n" +
        "          Category: \"android.intent.category.LAUNCHER\"\n")]
    [InlineData(
        "Activity Resolver Table:\n" +
        "  Non-Data Actions:\n" +
        "      android.intent.action.MAIN:\n" +
        "        2c9cd07 com.example.app/.Main filter 6ce9e34\n" +
        "          Action: \"android.intent.action.MAIN\"\n" +
        "          Category: \"android.intent.category.LAUNCHER\"\n" +
        "          targetActivity=com.example.app.RealMain\n")]
    [InlineData(
        "Activity Resolver Table:\n" +
        "  Non-Data Actions:\n" +
        "      android.intent.action.MAIN:\n" +
        "        2c9cd07 com.example.app/.Main filter 6ce9e34\n" +
        "          Action: \"android.intent.action.MAIN\"\n" +
        "          Category: \"android.intent.category.LAUNCHER\"\n" +
        "        3c9cd08 com.example.app/com.example.app.Main filter 7ce9e35\n" +
        "          Action: \"android.intent.action.MAIN\"\n" +
        "          Category: \"android.intent.category.LAUNCHER\"\n")]
    public async Task Launch_ResolverFallbackRejectsMissingSubstitutedAliasOrAmbiguousProof(
        string packageDump)
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(apk, packageDump: packageDump);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .LaunchInspectedAppAsync("QUEST123", apk));
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Count >= 6 &&
                call.Arguments[3] == "am" && call.Arguments[4] == "start");
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_RejectsDuplicateExportFieldsInMatchingDetailRecord()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(
            apk,
            packageDump:
                "  Activity #0 ActivityInfo{abc com.example.app/.Main}\n" +
                "    exported=true exported=true\n");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .LaunchInspectedAppAsync("QUEST123", apk));
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Count >= 6 &&
                call.Arguments[3] == "am" && call.Arguments[4] == "start");
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_StartFailureReturnsNoLaunchResultAfterProof()
    {
        var apk = await CreateApkAsync();
        var startFailure = new CommandResult(
            "adb",
            [],
            1,
            "",
            "Error type 3",
            TimeSpan.Zero);
        var runner = CreateDeploymentRunner(apk, launchStartResult: startFailure);
        try
        {
            var failure = await Assert.ThrowsAsync<AdbCommandException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .LaunchInspectedAppAsync("QUEST123", apk));

            Assert.Equal(1, failure.Result.ExitCode);
            Assert.True(failure.Result.Arguments.Count >= 6);
            Assert.Equal("am", failure.Result.Arguments[3]);
            Assert.Equal("start", failure.Result.Arguments[4]);
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "shell", "dumpsys", "activity", "activities"]));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Observe_RejectsMalformedNonEmptyPackagePathAsUncertain()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, arguments) =>
        {
            if (file == "aapt2")
                return Success("package: name='com.example.app' versionCode='42'\n");
            if (file == "apksigner")
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            if (arguments.Any(value => value.StartsWith("pm path ", StringComparison.Ordinal)))
                return Success("warning: package database busy\n");
            return Success("");
        });
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .ObserveInspectedAppAsync("QUEST123", apk));
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Contains("dumpsys") || call.Arguments.Contains("pidof"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Install_DoesNotConfirmMatchingIdentitySignerWhenInstalledBytesDiffer()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, arguments) =>
        {
            if (file == "aapt2")
            {
                return Success("package: name='com.example.app' versionCode='42' versionName='1.2.3'\n");
            }
            if (file == "apksigner")
            {
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            }
            if (arguments.Contains("pm path 'com.example.app'"))
            {
                return Success("package:/data/app/example/base.apk\n");
            }
            if (arguments.Count >= 3 && arguments[2] == "pull")
            {
                File.WriteAllBytes(arguments[^1], [0x50, 0x4b, 0x03, 0x05]);
                return Success("1 file pulled\n");
            }
            return Success("Success\n");
        }, [0x50, 0x4b, 0x03, 0x05]);
        try
        {
            var executor = new OperatorCommandExecutor(
                new AdbClient("adb", runner, new("aapt2", "apksigner")));
            var execution = await executor.ExecuteAsync(OperatorCommands.InstallApk("QUEST123", apk));
            Assert.Equal(OperatorMutationStage.Pending, execution.MutationReceipt!.Stage);
            Assert.False(execution.MutationReceipt.IsTerminal);
            Assert.Contains("base APK bytes do not match", execution.MutationReceipt.ObservedState,
                StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(
                execution.InspectedApkInstallResult!.Artifact.Sha256,
                execution.InspectedApkInstallResult.Installed.BaseApkSha256);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_RejectsMatchingIdentitySignerWhenInstalledBytesDiffer()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, arguments) =>
        {
            if (file == "aapt2")
            {
                return Success("package: name='com.example.app' versionCode='42' versionName='1.2.3'\n");
            }
            if (file == "apksigner")
            {
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            }
            if (arguments.Contains("pm path 'com.example.app'"))
            {
                return Success("package:/data/app/example/base.apk\n");
            }
            if (arguments.Count >= 3 && arguments[2] == "pull")
            {
                File.WriteAllBytes(arguments[^1], [0x50, 0x4b, 0x03, 0x05]);
                return Success("1 file pulled\n");
            }
            return Success("Success\n");
        }, [0x50, 0x4b, 0x03, 0x05]);
        try
        {
            var client = new AdbClient("adb", runner, new("aapt2", "apksigner"));
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => client.LaunchInspectedAppAsync("QUEST123", apk));
            Assert.Contains("digest and size", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("query-activities"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public async Task Inspect_RejectsNonPositiveOrMalformedVersionCode(string versionCode)
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, _) => file == "aapt2"
            ? Success($"package: name='com.example.app' versionCode='{versionCode}'\n")
            : Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n"));
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ApkArtifactInspector(runner, new("aapt2", "apksigner")).InspectAsync(apk));
            Assert.Contains("positive integer", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Observe_UsesOnlyBoundedSerialScopedPackageCommands()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(apk, activities:
            "mResumedActivity: ActivityRecord{1 com.example.app/.Main}\n" +
            "topResumedActivity=ActivityRecord{1 com.example.app/.Main}\n");
        try
        {
            var client = new AdbClient("adb", runner, new("aapt2", "apksigner"));
            var result = await client.ObserveInspectedAppAsync("QUEST123", apk);

            Assert.True(result.IsForeground);
            Assert.True(result.IsTopResumed);
            Assert.Equal(
                ["com.example.app/com.example.app.Main"],
                result.ForegroundComponents);
            Assert.Equal(
                ["com.example.app/com.example.app.Main"],
                result.TopResumedComponents);
            Assert.Empty(result.BlockingSystemComponents);
            Assert.True(result.ProcessAlive);
            Assert.Equal([123, 456], result.ProcessIds);
            Assert.All(runner.Calls.Where(call => call.FileName == "adb"),
                call => Assert.Equal(["-s", "QUEST123"], call.Arguments.Take(2)));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Observe_PreservesImmersiveTopResumeAndBlockingSystemOverlayIndependently()
    {
        var apk = await CreateApkAsync();
        var runner = CreateDeploymentRunner(apk, activities:
            "mResumedActivity: ActivityRecord{1 com.oculus.systemux/.guardian.GuardianDialogActivity}\n" +
            "topResumedActivity=ActivityRecord{2 com.example.app/.Main}\n" +
            "topResumedActivity=ActivityRecord{3 com.oculus.systemux/.sensor.SensorLockActivity}\n" +
            "topResumedActivity=ActivityRecord{4 com.oculus.vrshell/.systemdialog.launchcheck.LaunchCheckControllerRequiredDialogActivity}\n");
        try
        {
            var client = new AdbClient("adb", runner, new("aapt2", "apksigner"));
            var result = await client.ObserveInspectedAppAsync("QUEST123", apk);

            Assert.False(result.IsForeground);
            Assert.True(result.IsTopResumed);
            Assert.Equal(
                ["com.oculus.systemux/com.oculus.systemux.guardian.GuardianDialogActivity"],
                result.ForegroundComponents);
            Assert.Equal(
                [
                    "com.example.app/com.example.app.Main",
                    "com.oculus.systemux/com.oculus.systemux.sensor.SensorLockActivity",
                    "com.oculus.vrshell/com.oculus.vrshell.systemdialog.launchcheck.LaunchCheckControllerRequiredDialogActivity"
                ],
                result.TopResumedComponents);
            Assert.Equal(
                [
                    "com.oculus.systemux/com.oculus.systemux.guardian.GuardianDialogActivity",
                    "com.oculus.systemux/com.oculus.systemux.sensor.SensorLockActivity",
                    "com.oculus.vrshell/com.oculus.vrshell.systemdialog.launchcheck.LaunchCheckControllerRequiredDialogActivity"
                ],
                result.BlockingSystemComponents);
            Assert.True(result.ProcessAlive);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Observe_TreatsOnlyTypedPackageAbsenceAsNotInstalled()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, arguments) =>
        {
            if (file == "aapt2")
                return Success("package: name='com.example.app' versionCode='42'\n");
            if (file == "apksigner")
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            if (arguments.Any(value => value.StartsWith("pm path ", StringComparison.Ordinal)))
                return Success("");
            if (arguments.Contains("pidof")) return Success("");
            return Success("");
        });
        try
        {
            var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
                .ObserveInspectedAppAsync("QUEST123", apk);
            Assert.Null(result.Installed);
            Assert.Contains(runner.Calls, call => call.Arguments.Contains("dumpsys"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Observe_PropagatesAmbiguousPackageReadbackAndSkipsRuntimeProbes()
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, arguments) =>
        {
            if (file == "aapt2")
                return Success("package: name='com.example.app' versionCode='42'\n");
            if (file == "apksigner")
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            if (arguments.Any(value => value.StartsWith("pm path ", StringComparison.Ordinal)))
                return new CommandResult(file, arguments, 1, "", "device offline", TimeSpan.Zero);
            return Success("");
        });
        try
        {
            await Assert.ThrowsAsync<AdbCommandException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .ObserveInspectedAppAsync("QUEST123", apk));
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Contains("dumpsys") || call.Arguments.Contains("pidof"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public void Reconciliation_RequiresInstalledBaseDigestAndSize()
    {
        var path = Path.GetFullPath("example.apk");
        var identity = new ApkArtifactIdentity(
            "com.example.app", 42, "1.2.3", new string('a', 64), null);
        var artifact = new ApkArtifactInspection(path, 4, new string('c', 64), identity);
        var command = OperatorCommands.InstallApk("QUEST123", path);
        var receipt = new OperatorMutationReceipt(
            "pc-test",
            OperatorCommandKind.InstallApk,
            "QUEST123",
            "inspected APK installed",
            OperatorMutationStage.Pending,
            "pending",
            true,
            [new OperatorMutationTransition(OperatorMutationStage.Sent, DateTimeOffset.UtcNow, "sent")]);
        var mismatchedInstalled = new InstalledApkIdentity(
            "QUEST123", identity, ["/data/app/example/base.apk"], new string('d', 64), 4);
        var readback = new OperatorExecutionResult(
            OperatorCommands.ObserveInspectedApp("QUEST123", path),
            AppRuntimeObservation: new AppRuntimeObservation(
                artifact, mismatchedInstalled, false, false, []));

        var reconciled = OperatorMutationReconciler.Reconcile(receipt, command, readback);

        Assert.Equal(OperatorMutationStage.Pending, reconciled.Stage);
        Assert.Contains("base APK bytes do not match", reconciled.ObservedState,
            StringComparison.OrdinalIgnoreCase);
    }

    private static FakeRunner CreateDeploymentRunner(
        string sourceApk,
        string? splitName = null,
        string? launcherOutput = null,
        string activities = "",
        bool launcherExported = true,
        bool probeInstallImmutability = false,
        string? packageDump = null,
        string packageName = "com.example.app",
        string activityName = ".Main",
        CommandResult? launchStartResult = null)
    {
        launcherOutput ??= $"{packageName}/{activityName}\n";
        return new FakeRunner((file, arguments) =>
        {
            if (file == "aapt2")
            {
                return Success(
                    $"package: name='{packageName}' versionCode='42' versionName='1.2.3'" +
                    (splitName is null ? "" : $" split='{splitName}'") + "\n");
            }
            if (file == "apksigner")
            {
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            }
            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", $"pm path '{packageName}'"]))
            {
                return Success("package:/data/app/example/base.apk\n");
            }
            if (probeInstallImmutability &&
                arguments.Count >= 4 &&
                arguments[2] == "install")
            {
                var admitted = arguments[^1];
                Assert.Throws<IOException>(() => File.WriteAllBytes(admitted, [9, 9, 9, 9]));
                Assert.Throws<IOException>(() => File.Delete(admitted));
                Assert.Throws<IOException>(() => File.Move(
                    admitted,
                    admitted + ".moved",
                    overwrite: true));
            }
            if (arguments.Count >= 3 && arguments[2] == "pull")
            {
                File.Copy(sourceApk, arguments[^1], overwrite: true);
                return Success("1 file pulled\n");
            }
            if (arguments.Contains("query-activities"))
            {
                return Success(launcherOutput);
            }
            if (arguments.SequenceEqual(
                    ["-s", "QUEST123", "shell", "dumpsys", "package", packageName]))
            {
                return Success(packageDump ??
                    $"  Activity #0 ActivityInfo{{abc {packageName}/{activityName}}}\n" +
                    $"    exported={launcherExported.ToString().ToLowerInvariant()}\n");
            }
            if (arguments.Count >= 6 &&
                arguments[2] == "shell" &&
                arguments[3] == "am" &&
                arguments[4] == "start")
            {
                return launchStartResult ?? Success("Starting: Intent\n");
            }
            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", "dumpsys", "activity", "activities"]))
            {
                return Success(activities);
            }
            if (arguments.Contains("pidof"))
            {
                return Success("456 123\n");
            }
            return Success("Success\n");
        }, File.ReadAllBytes(sourceApk));
    }

    private static FakeRunner CreatePreflightRunner(
        string sourceApk,
        bool installed,
        int deviceApiLevel,
        CommandResult? packagePathResult = null,
        CommandResult? packageListResult = null)
    {
        return new FakeRunner((file, arguments) =>
        {
            if (file == "aapt2")
            {
                return Success(
                    "package: name='com.example.app' versionCode='42' versionName='1.2.3'\n" +
                    "sdkVersion:'23'\n" +
                    "targetSdkVersion:'35'\n" +
                    "launchable-activity: name='com.example.app.Main' label='' icon=''\n");
            }
            if (file == "apksigner")
            {
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            }
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success(
                    "List of devices attached\n" +
                    "QUEST123 device product:eureka model:Quest_3 transport_id:1\n");
            }
            if (arguments.SequenceEqual(
                    ["-s", "QUEST123", "shell", "getprop", "ro.build.version.sdk"]))
            {
                return Success(deviceApiLevel.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            }
            if (arguments.SequenceEqual(
                    ["-s", "QUEST123", "shell", "pm path 'com.example.app'"]))
            {
                return packagePathResult ??
                    Success(installed ? "package:/data/app/example/base.apk\n" : "");
            }
            if (arguments.SequenceEqual(
                    ["-s", "QUEST123", "shell", "pm list packages 'com.example.app'"]))
            {
                return packageListResult ?? Success("");
            }
            if (arguments.Contains("query-activities"))
            {
                return Success("com.example.app/.Main\n");
            }
            if (arguments.SequenceEqual(
                    ["-s", "QUEST123", "shell", "dumpsys", "package", "com.example.app"]))
            {
                return Success(
                    "  Activity #0 ActivityInfo{abc com.example.app/.Main}\n" +
                    "    exported=true\n");
            }
            return Success("unexpected\n");
        }, File.ReadAllBytes(sourceApk));
    }

    private static async Task<string> CreateApkAsync(byte[]? bytes = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qfm-public-test-{Guid.NewGuid():N}.apk");
        await File.WriteAllBytesAsync(path, bytes ?? [0x50, 0x4b, 0x03, 0x04]);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "QuestIonAbleFileManager.slnx")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static CommandResult Success(string output) =>
        new("", [], 0, output, "", TimeSpan.Zero);

    private static CommandResult SilentFailure() =>
        new("", [], 1, "", "", TimeSpan.Zero);

    private sealed class FakeRunner(
        Func<string, IReadOnlyList<string>, CommandResult> handler,
        byte[]? streamedBytes = null) : IStreamingCommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public List<long> StreamMaximumBytes { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return Task.FromResult(handler(fileName, arguments) with
            {
                FileName = fileName,
                Arguments = arguments.ToArray()
            });
        }

        public async Task<StreamingCommandResult> RunToStreamAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            Stream destination,
            long maximumBytes,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            StreamMaximumBytes.Add(maximumBytes);
            var result = handler(fileName, arguments) with
            {
                FileName = fileName,
                Arguments = arguments.ToArray()
            };
            var bytes = streamedBytes ?? [0x50, 0x4b, 0x03, 0x04];
            if (result.Succeeded)
            {
                if (bytes.LongLength > maximumBytes)
                    throw new FleetTransferLimitException(maximumBytes);
                await destination.WriteAsync(bytes, cancellationToken);
            }
            return new StreamingCommandResult(
                result,
                result.Succeeded ? bytes.LongLength : 0,
                Convert.ToHexString(SHA256.HashData(result.Succeeded ? bytes : []))
                    .ToLowerInvariant());
        }
    }
}
