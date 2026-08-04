using QuestIonAbleFileManager.Core;
using System.Security.Cryptography;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class InspectedDeploymentTests
{
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
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", "QUEST123", "exec-out", "cat", "/data/app/example/base.apk"]));
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Count >= 3 && call.Arguments[2] == "pull");
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
            var exception = await Assert.ThrowsAsync<InstalledApkMismatchException>(
                () => client.LaunchInspectedAppAsync("QUEST123", apk));
            Assert.Contains("digest and size", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, exception.Expected.SizeBytes);
            Assert.Equal(4, exception.Installed.BaseApkSizeBytes);
            Assert.NotEqual(exception.Expected.Sha256, exception.Installed.BaseApkSha256);
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("query-activities"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Launch_ReturnsInstalledEvidenceWhenInstalledApkIsLargerThanExpected()
    {
        var apk = await CreateApkAsync();
        var installedBytes = new byte[] { 0x50, 0x4b, 0x03, 0x04, 0x05 };
        var runner = CreateDeploymentRunner(apk, installedBytes: installedBytes);
        try
        {
            var exception = await Assert.ThrowsAsync<InstalledApkMismatchException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .LaunchInspectedAppAsync("QUEST123", apk));
            Assert.Equal(4, exception.Expected.SizeBytes);
            Assert.Equal(5, exception.Installed.BaseApkSizeBytes);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(installedBytes)).ToLowerInvariant(),
                exception.Installed.BaseApkSha256);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Theory]
    [InlineData("/data/app/example/base.apk$(id).apk")]
    [InlineData("/data/app/../../data/local/tmp/payload.apk")]
    public async Task Observe_RejectsPackageManagerPathOutsideConstrainedInstalledApkGrammar(
        string installedPath)
    {
        var apk = await CreateApkAsync();
        var runner = new FakeRunner((file, arguments) =>
        {
            if (file == "aapt2")
                return Success("package: name='com.example.app' versionCode='42'\n");
            if (file == "apksigner")
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            if (arguments.Any(value => value.StartsWith("pm path ", StringComparison.Ordinal)))
                return Success($"package:{installedPath}\n");
            return Success("");
        });
        try
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                new AdbClient("adb", runner, new("aapt2", "apksigner"))
                    .ObserveInspectedAppAsync("QUEST123", apk));
            Assert.Contains("constrained package-manager", exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("exec-out"));
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
        string launcherOutput = "com.example.app/.Main\n",
        string activities = "",
        bool launcherExported = true,
        bool probeInstallImmutability = false,
        string? packageDump = null,
        byte[]? installedBytes = null)
    {
        return new FakeRunner((file, arguments) =>
        {
            if (file == "aapt2")
            {
                return Success(
                    "package: name='com.example.app' versionCode='42' versionName='1.2.3'" +
                    (splitName is null ? "" : $" split='{splitName}'") + "\n");
            }
            if (file == "apksigner")
            {
                return Success("Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n");
            }
            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", "pm path 'com.example.app'"]))
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
                    ["-s", "QUEST123", "shell", "dumpsys", "package", "com.example.app"]))
            {
                return Success(packageDump ??
                    "  Activity #0 ActivityInfo{abc com.example.app/.Main}\n" +
                    $"    exported={launcherExported.ToString().ToLowerInvariant()}\n");
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
        }, installedBytes ?? File.ReadAllBytes(sourceApk));
    }

    private static async Task<string> CreateApkAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qfm-public-test-{Guid.NewGuid():N}.apk");
        await File.WriteAllBytesAsync(path, [0x50, 0x4b, 0x03, 0x04]);
        return path;
    }

    private static CommandResult Success(string output) =>
        new("", [], 0, output, "", TimeSpan.Zero);

    private sealed class FakeRunner(
        Func<string, IReadOnlyList<string>, CommandResult> handler,
        byte[]? streamedBytes = null) : IStreamingCommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

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
