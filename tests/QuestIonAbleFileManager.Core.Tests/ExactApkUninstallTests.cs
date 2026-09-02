using System.Security.Cryptography;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class ExactApkUninstallTests
{
    private const string Serial = "QUEST123";
    private const string Package = "com.example.app";

    [Fact]
    public void ClosedFixturePinsPreimageOutcomesAndReadbackScopes()
    {
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "QuestIonAbleFileManager.Core.Tests",
            "Fixtures",
            "exact-apk-uninstall.v1.json")));
        var root = fixture.RootElement;
        Assert.Equal(
            "questionable.file_manager.exact_apk_uninstall_fixture.v1",
            root.GetProperty("schema").GetString());
        Assert.Equal(
            ["confirmed-absent", "still-present", "cleanup-unknown"],
            root.GetProperty("terminal_outcomes").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());
        Assert.Equal(
            ["unscoped", "current-user"],
            root.GetProperty("post_readback_scopes").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());
        Assert.Equal(7, root.GetProperty("rejected_preimages").GetArrayLength());
    }

    [Fact]
    public void FactoryAndParserRequireOneClosedConfirmedArtifactRoute()
    {
        var path = Path.GetFullPath("example.apk");
        var command = OperatorCommands.UninstallExactApk(Serial, path, operatorConfirmed: true);

        Assert.Equal(OperatorCommandKind.UninstallExactApk, command.Kind);
        Assert.Equal(
            ["apk", "uninstall", "--serial", Serial, "--file", path,
             "--confirm-exact-apk-uninstall"],
            command.CliArguments);
        Assert.True(command.OperatorConfirmed);
        Assert.Null(command.PackageName);
        Assert.Throws<InvalidOperationException>(() =>
            OperatorCommands.UninstallExactApk(Serial, path));

        var parsed = OperatorCommands.ParseExactApkUninstallCliArguments(
            ["apk", "uninstall", "--serial", Serial, "--file", path,
             "--confirm-exact-apk-uninstall", "--json"]);
        Assert.Equal(command.Kind, parsed.Kind);
        Assert.Equal(command.CliArguments, parsed.CliArguments);

        var rejected = new[]
        {
            new[] { "apk", "uninstall", "--serial", Serial, "--file", path, "--json" },
            new[] { "apk", "uninstall", "--serial", Serial, "--package", Package,
                "--confirm-exact-apk-uninstall", "--json" },
            new[] { "apk", "uninstall", "--serial", Serial, "--file", path,
                "--user", "current", "--confirm-exact-apk-uninstall", "--json" },
            new[] { "apk", "UNINSTALL", "--serial", Serial, "--file", path,
                "--confirm-exact-apk-uninstall", "--json" },
            new[] { "apk", "uninstall", "--serial", Serial, "--file", path,
                "--confirm-exact-apk-uninstall", "--json", "--adb", "adb.exe" }
        };
        foreach (var arguments in rejected)
        {
            Assert.Throws<ArgumentException>(() =>
                OperatorCommands.ParseExactApkUninstallCliArguments(arguments));
        }
    }

    [Fact]
    public async Task ExactSingleBaseDispatchesOnceAndConfirmsBothAbsenceScopes()
    {
        var apk = await CreateApkAsync();
        try
        {
            var runner = new UninstallRunner(File.ReadAllBytes(apk));
            var executor = new OperatorCommandExecutor(new AdbClient(
                "adb-test", runner, new("aapt2", "apksigner")));

            var execution = await executor.ExecuteAsync(
                OperatorCommands.UninstallExactApk(Serial, apk, operatorConfirmed: true));
            var result = Assert.IsType<ExactApkUninstallResult>(execution.ExactApkUninstallResult);

            Assert.True(result.Confirmed);
            Assert.Equal(ExactApkUninstallDisposition.ConfirmedAbsent, result.Disposition);
            Assert.True(result.UnscopedPackageAbsent);
            Assert.True(result.CurrentUserPackageAbsent);
            Assert.Equal(Package, result.Artifact.Identity.PackageName);
            Assert.Equal(["/data/app/example/base.apk"], result.InstalledBeforeDispatch.ApkPaths);
            Assert.Equal(1, runner.UninstallCalls);
            Assert.Contains(runner.Calls, call => call.Arguments.SequenceEqual(
                ["-s", Serial, "uninstall", Package]));
            Assert.Equal(
                [OperatorMutationStage.Sent, OperatorMutationStage.Pending,
                 OperatorMutationStage.Confirmed],
                execution.MutationReceipt!.Transitions.Select(static transition => transition.Stage));
            Assert.True(execution.MutationReceipt.HeadsetReadback);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task CallerPathMutationCannotChangeTheRetainedArtifactOrDerivedPackage()
    {
        var apk = await CreateApkAsync();
        var original = File.ReadAllBytes(apk);
        var expectedSha = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        try
        {
            var runner = new UninstallRunner(original) { MutatePathOnSecondSigner = apk };

            var result = await Client(runner).UninstallExactApkAsync(Serial, apk);

            Assert.True(result.Confirmed);
            Assert.Equal(expectedSha, result.Artifact.Sha256);
            Assert.Equal(Package, result.Artifact.Identity.PackageName);
            Assert.Equal([9, 9, 9, 9], File.ReadAllBytes(apk));
            Assert.Equal(1, runner.UninstallCalls);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task AbsentDifferentSplitUnverifiedAndUnreadyPreimagesRejectBeforeDispatch()
    {
        var apk = await CreateApkAsync();
        try
        {
            var absent = new UninstallRunner(File.ReadAllBytes(apk)) { InitiallyAbsent = true };
            await Assert.ThrowsAsync<PackageNotInstalledException>(() =>
                Client(absent).UninstallExactApkAsync(Serial, apk));
            Assert.Equal(0, absent.UninstallCalls);

            var different = new UninstallRunner([9, 9, 9, 9]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Client(different).UninstallExactApkAsync(Serial, apk));
            Assert.Equal(0, different.UninstallCalls);

            var split = new UninstallRunner(File.ReadAllBytes(apk)) { InstalledSplit = true };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Client(split).UninstallExactApkAsync(Serial, apk));
            Assert.Equal(0, split.UninstallCalls);

            var duplicate = new UninstallRunner(File.ReadAllBytes(apk)) { DuplicateSerial = true };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Client(duplicate).UninstallExactApkAsync(Serial, apk));
            Assert.Equal(0, duplicate.UninstallCalls);

            var offline = new UninstallRunner(File.ReadAllBytes(apk)) { OfflineSerial = true };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Client(offline).UninstallExactApkAsync(Serial, apk));
            Assert.Equal(0, offline.UninstallCalls);

            var missing = new UninstallRunner(File.ReadAllBytes(apk)) { MissingSerial = true };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Client(missing).UninstallExactApkAsync(Serial, apk));
            Assert.Equal(0, missing.UninstallCalls);

            var unverified = new UninstallRunner(File.ReadAllBytes(apk)) { UnverifiedArtifact = true };
            await Assert.ThrowsAnyAsync<Exception>(() =>
                Client(unverified).UninstallExactApkAsync(Serial, apk));
            Assert.Equal(0, unverified.UninstallCalls);

            var raced = new UninstallRunner(File.ReadAllBytes(apk))
            {
                ReplaceInstalledAfterDeviceDiscovery = true
            };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Client(raced).UninstallExactApkAsync(Serial, apk));
            Assert.Equal(0, raced.UninstallCalls);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task DispatchAndReadbackAmbiguityRetainNonConfirmedMutationReceiptsWithoutReplay()
    {
        var apk = await CreateApkAsync();
        try
        {
            var dispatch = new UninstallRunner(File.ReadAllBytes(apk))
            {
                UninstallException = new TimeoutException("synthetic transport loss")
            };
            var dispatchExecution = await new OperatorCommandExecutor(Client(dispatch)).ExecuteAsync(
                OperatorCommands.UninstallExactApk(Serial, apk, operatorConfirmed: true));
            Assert.Equal(1, dispatch.UninstallCalls);
            Assert.Equal(
                ExactApkUninstallDisposition.CleanupUnknown,
                dispatchExecution.ExactApkUninstallResult!.Disposition);
            Assert.Equal(OperatorMutationStage.CleanupUnknown, dispatchExecution.MutationReceipt!.Stage);

            var rejected = new UninstallRunner(File.ReadAllBytes(apk))
            {
                UninstallCommandFailure = true
            };
            var rejectedExecution = await new OperatorCommandExecutor(Client(rejected)).ExecuteAsync(
                OperatorCommands.UninstallExactApk(Serial, apk, operatorConfirmed: true));
            Assert.Equal(1, rejected.UninstallCalls);
            Assert.Equal(
                ExactApkUninstallDisposition.CleanupUnknown,
                rejectedExecution.ExactApkUninstallResult!.Disposition);
            Assert.Null(rejectedExecution.ExactApkUninstallResult.UnscopedPackageAbsent);
            Assert.Null(rejectedExecution.ExactApkUninstallResult.CurrentUserPackageAbsent);
            Assert.Equal(OperatorMutationStage.CleanupUnknown, rejectedExecution.MutationReceipt!.Stage);

            var stillPresent = new UninstallRunner(File.ReadAllBytes(apk)) { RemainsInstalled = true };
            var presentExecution = await new OperatorCommandExecutor(Client(stillPresent)).ExecuteAsync(
                OperatorCommands.UninstallExactApk(Serial, apk, operatorConfirmed: true));
            Assert.Equal(1, stillPresent.UninstallCalls);
            Assert.Equal(
                ExactApkUninstallDisposition.StillPresent,
                presentExecution.ExactApkUninstallResult!.Disposition);
            Assert.Equal(OperatorMutationStage.Pending, presentExecution.MutationReceipt!.Stage);

            var partial = new UninstallRunner(File.ReadAllBytes(apk))
            {
                RemainsCurrentUserInstalled = true
            };
            var partialExecution = await new OperatorCommandExecutor(Client(partial)).ExecuteAsync(
                OperatorCommands.UninstallExactApk(Serial, apk, operatorConfirmed: true));
            Assert.Equal(1, partial.UninstallCalls);
            Assert.Equal(
                ExactApkUninstallDisposition.StillPresent,
                partialExecution.ExactApkUninstallResult!.Disposition);
            Assert.True(partialExecution.ExactApkUninstallResult.UnscopedPackageAbsent);
            Assert.False(partialExecution.ExactApkUninstallResult.CurrentUserPackageAbsent);
            Assert.False(partialExecution.ExactApkUninstallResult.Confirmed);
            Assert.Equal(OperatorMutationStage.Pending, partialExecution.MutationReceipt!.Stage);

            var reversePartial = new UninstallRunner(File.ReadAllBytes(apk))
            {
                RemainsUnscopedInstalled = true
            };
            var reversePartialExecution = await new OperatorCommandExecutor(Client(reversePartial)).ExecuteAsync(
                OperatorCommands.UninstallExactApk(Serial, apk, operatorConfirmed: true));
            Assert.Equal(1, reversePartial.UninstallCalls);
            Assert.Equal(
                ExactApkUninstallDisposition.StillPresent,
                reversePartialExecution.ExactApkUninstallResult!.Disposition);
            Assert.False(reversePartialExecution.ExactApkUninstallResult.UnscopedPackageAbsent);
            Assert.True(reversePartialExecution.ExactApkUninstallResult.CurrentUserPackageAbsent);
            Assert.False(reversePartialExecution.ExactApkUninstallResult.Confirmed);
            Assert.Equal(OperatorMutationStage.Pending, reversePartialExecution.MutationReceipt!.Stage);

            var readback = new UninstallRunner(File.ReadAllBytes(apk)) { DamagePostReadback = true };
            var readbackExecution = await new OperatorCommandExecutor(Client(readback)).ExecuteAsync(
                OperatorCommands.UninstallExactApk(Serial, apk, operatorConfirmed: true));
            Assert.Equal(1, readback.UninstallCalls);
            Assert.Equal(
                ExactApkUninstallDisposition.CleanupUnknown,
                readbackExecution.ExactApkUninstallResult!.Disposition);
            Assert.Equal(OperatorMutationStage.CleanupUnknown, readbackExecution.MutationReceipt!.Stage);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    private static AdbClient Client(UninstallRunner runner) =>
        new("adb-test", runner, new("aapt2", "apksigner"));

    private static async Task<string> CreateApkAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qfm-exact-uninstall-{Guid.NewGuid():N}.apk");
        await File.WriteAllBytesAsync(path, [0x50, 0x4b, 0x03, 0x04]);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "QuestIonAbleFileManager.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static CommandResult Result(int exitCode, string stdout = "", string stderr = "") =>
        new("", [], exitCode, stdout, stderr, TimeSpan.Zero);

    private sealed class UninstallRunner(byte[] streamedBytes) : IStreamingCommandRunner
    {
        private bool _dispatchCompleted;
        private bool _deviceDiscoveryCompleted;
        private int _signerCalls;

        public bool InitiallyAbsent { get; init; }
        public bool InstalledSplit { get; init; }
        public bool DuplicateSerial { get; init; }
        public bool OfflineSerial { get; init; }
        public bool MissingSerial { get; init; }
        public bool UnverifiedArtifact { get; init; }
        public bool ReplaceInstalledAfterDeviceDiscovery { get; init; }
        public bool RemainsInstalled { get; init; }
        public bool RemainsUnscopedInstalled { get; init; }
        public bool RemainsCurrentUserInstalled { get; init; }
        public bool DamagePostReadback { get; init; }
        public bool UninstallCommandFailure { get; init; }
        public Exception? UninstallException { get; init; }
        public string? MutatePathOnSecondSigner { get; init; }
        public int UninstallCalls { get; private set; }
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            if (fileName == "aapt2")
            {
                return Task.FromResult(Result(0,
                    "package: name='com.example.app' versionCode='42' versionName='1.2.3'\n"));
            }
            if (fileName == "apksigner")
            {
                _signerCalls++;
                if (_signerCalls == 2 && MutatePathOnSecondSigner is not null)
                {
                    File.WriteAllBytes(MutatePathOnSecondSigner, [9, 9, 9, 9]);
                }
                return Task.FromResult(UnverifiedArtifact
                    ? Result(1, stderr: "synthetic signer failure")
                    : Result(0,
                    "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n"));
            }
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                _deviceDiscoveryCompleted = true;
                var state = OfflineSerial ? "offline" : "device";
                var observedSerial = MissingSerial ? "OTHER123" : Serial;
                var rows = DuplicateSerial
                    ? $"{Serial} {state} product:eureka model:Quest_3 transport_id:1\n" +
                      $"{Serial} {state} product:eureka model:Quest_3 transport_id:2\n"
                    : $"{observedSerial} {state} product:eureka model:Quest_3 transport_id:1\n";
                return Task.FromResult(Result(0, "List of devices attached\n" + rows));
            }
            if (arguments.SequenceEqual(["-s", Serial, "uninstall", Package]))
            {
                UninstallCalls++;
                if (UninstallException is not null)
                    return Task.FromException<CommandResult>(UninstallException);
                _dispatchCompleted = true;
                return Task.FromResult(UninstallCommandFailure
                    ? Result(1, stderr: "synthetic uninstall failure")
                    : Result(0, "Success\n"));
            }

            var absentAfterDispatch = _dispatchCompleted && !RemainsInstalled;
            if (arguments.SequenceEqual(["-s", Serial, "shell", $"pm path '{Package}'"]))
            {
                if (InitiallyAbsent || (absentAfterDispatch && !RemainsUnscopedInstalled))
                {
                    return Task.FromResult(DamagePostReadback && _dispatchCompleted
                        ? Result(1, stderr: "device offline")
                        : Result(1));
                }
                return Task.FromResult(Result(0, InstalledPaths()));
            }
            if (arguments.SequenceEqual(["-s", Serial, "shell", $"pm list packages '{Package}'"]))
            {
                return Task.FromResult(Result(
                    0,
                    InitiallyAbsent || (absentAfterDispatch && !RemainsUnscopedInstalled)
                        ? ""
                        : $"package:{Package}\n"));
            }
            if (arguments.SequenceEqual(
                    ["-s", Serial, "shell", $"pm path --user current '{Package}'"]))
            {
                return Task.FromResult(absentAfterDispatch && !RemainsCurrentUserInstalled
                    ? Result(1)
                    : Result(0, InstalledPaths()));
            }
            if (arguments.SequenceEqual(
                    ["-s", Serial, "shell", $"pm list packages --user current '{Package}'"]))
            {
                return Task.FromResult(Result(
                    0,
                    absentAfterDispatch && !RemainsCurrentUserInstalled
                        ? ""
                        : $"package:{Package}\n"));
            }
            return Task.FromResult(Result(1, stderr: "unexpected command: " + string.Join(" ", arguments)));
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
            var observedBytes = ReplaceInstalledAfterDeviceDiscovery && _deviceDiscoveryCompleted
                ? new byte[] { 9, 9, 9, 9 }
                : streamedBytes;
            if (observedBytes.LongLength > maximumBytes)
                throw new FleetTransferLimitException(maximumBytes);
            await destination.WriteAsync(observedBytes, cancellationToken);
            var command = Result(0) with { FileName = fileName, Arguments = arguments.ToArray() };
            return new StreamingCommandResult(
                command,
                observedBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(observedBytes)).ToLowerInvariant());
        }

        private string InstalledPaths() => InstalledSplit
            ? "package:/data/app/example/base.apk\npackage:/data/app/example/split_config.en.apk\n"
            : "package:/data/app/example/base.apk\n";
    }
}
