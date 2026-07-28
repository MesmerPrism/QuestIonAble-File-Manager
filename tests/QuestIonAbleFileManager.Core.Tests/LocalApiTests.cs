using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LocalApiTests
{
    [Theory]
    [InlineData("http://0.0.0.0:8123/")]
    [InlineData("http://192.0.2.1:8123/")]
    [InlineData("http://localhost:8123/")]
    [InlineData("https://127.0.0.1:8123/")]
    public void ListenAddress_RejectsNonExplicitLoopback(string value) =>
        Assert.Throws<LocalApiException>(() => LocalApiSecurity.RequireExplicitLoopback(value));

    [Fact]
    public void InstallPreflight_RejectsTestOnlyPackages()
    {
        var rejected = Assert.Throws<LocalApiException>(() =>
            new LocalApiPreflightRequest(
                LocalApiContract.Version,
                "apk.install-inspected",
                Path.GetFullPath("example.apk"),
                "QUEST123",
                new LocalApiInstallOptions(AllowTestPackages: true))
            .Validate());
        Assert.Equal("test_only_not_allowed", rejected.Code);
    }

    [Fact]
    public void ListenAddress_AcceptsExplicitLoopback()
    {
        Assert.Equal("127.0.0.1", LocalApiSecurity.RequireExplicitLoopback(
            "http://127.0.0.1:8123/").Host);
        Assert.Equal("[::1]", LocalApiSecurity.RequireExplicitLoopback(
            "http://[::1]:8123/").Host);
    }

    [Fact]
    public void Credential_IsPrivateBoundedAndComparedWithoutProjection()
    {
        var credential = new string('x', 32);
        Assert.Equal(credential, LocalApiSecurity.ReadCredentialFromEnvironment(
            name => name == LocalApiContract.CredentialEnvironmentVariable ? credential : null));
        Assert.True(LocalApiSecurity.FixedTimeEquals(credential, credential));
        Assert.False(LocalApiSecurity.FixedTimeEquals(credential, new string('y', 32)));
        Assert.True(LocalApiSecurity.AuthenticateBearer(credential, "Bearer " + credential));
        Assert.False(LocalApiSecurity.AuthenticateBearer(credential, credential));
        Assert.False(LocalApiSecurity.AuthenticateBearer(credential, "Basic " + credential));
        Assert.False(LocalApiSecurity.AuthenticateBearer(credential, "Bearer " + new string('y', 32)));
        Assert.False(LocalApiSecurity.AuthenticateBearer(
            credential, "Bearer " + new string('y', LocalApiContract.MaximumAuthorizationHeaderBytes + 1)));
        Assert.Throws<LocalApiException>(() => LocalApiSecurity.ValidateCredential("short"));
        Assert.Throws<LocalApiException>(() =>
            LocalApiSecurity.ValidateCredential(new string('x', 513)));
        Assert.DoesNotContain(
            credential,
            JsonSerializer.Serialize(new LocalApiCommandRegistry(
                CreateClient(CreateRunner())).GetCapabilities()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_StrictlyRejectsUnknownMissingAndBroadCommandShapes()
    {
        var registry = new LocalApiCommandRegistry(CreateClient(CreateRunner()));
        var unknown = """
            {"contractVersion":"questionable.file_manager.local_api.v1","command":"apk.inspect",
             "apkPath":"example.apk","shell":"id"}
            """;
        await Assert.ThrowsAsync<LocalApiException>(() => registry.PreflightAsync(Bytes(unknown)));
        await Assert.ThrowsAsync<LocalApiException>(() => registry.PreflightAsync(Bytes(
            """{"contractVersion":"questionable.file_manager.local_api.v1","command":"apk.inspect"}""")));
        await Assert.ThrowsAsync<LocalApiException>(() => registry.PreflightAsync(Bytes(
            """
            {"contractVersion":"questionable.file_manager.local_api.v1","command":"adb.raw",
             "apkPath":"example.apk","serial":"QUEST123"}
            """)));
        await Assert.ThrowsAsync<LocalApiException>(() => registry.PreflightAsync(Bytes(
            """
            {"contractVersion":"questionable.file_manager.local_api.v1","command":"app.launch-resolved",
             "apkPath":"example.apk","serial":"QUEST123","component":"com.example/.Main"}
            """)));
        await Assert.ThrowsAsync<LocalApiException>(() => registry.PreflightAsync(
            new byte[LocalApiContract.MaximumRequestBytes + 1]));
        await Assert.ThrowsAsync<LocalApiException>(() => registry.PreflightAsync(Bytes(
            """
            {"contractVersion":"questionable.file_manager.local_api.v1","command":"apk.inspect",
             "command":"runtime.observe","apkPath":"example.apk"}
            """)));
        await Assert.ThrowsAsync<LocalApiException>(() => registry.PreflightAsync(Bytes(
            """
            {"contractVersion":"questionable.file_manager.local_api.v1","command":"apk.install-inspected",
             "apkPath":"example.apk","serial":"QUEST123",
             "installOptions":{"allowDowngrade":true}}
            """)));
    }

    [Fact]
    public async Task DurableJournal_PreservesReplayTombstoneAcrossRestart()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(root);
            using var first = new LocalApiCommandRegistry(CreateClient(CreateRunner()), stateSettings: settings);
            var preflight = await first.PreflightAsync(Preflight("apk.inspect", apk));
            Assert.Equal(LocalApiOperationStage.Completed,
                (await first.ExecuteAsync(Execute(preflight))).Stage);

            first.Dispose();
            using var restarted = new LocalApiCommandRegistry(CreateClient(CreateRunner()), stateSettings: settings);
            var replay = await Assert.ThrowsAsync<LocalApiException>(
                () => restarted.ExecuteAsync(Execute(preflight)));

            Assert.Equal("operation_consumed", replay.Code);
            var durable = restarted.GetStatus(Operation(preflight.OperationId));
            Assert.Equal(LocalApiOperationStage.Completed, durable.Stage);
            Assert.IsType<JsonElement>(durable.Result);
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DurableJournal_FailsClosedOnDamage()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(root);
            using (var registry = new LocalApiCommandRegistry(CreateClient(CreateRunner()), stateSettings: settings))
            {
                await registry.PreflightAsync(Preflight("apk.inspect", apk));
            }
            var journal = Path.Combine(root, "operations.v1.json");
            var bytes = await File.ReadAllBytesAsync(journal);
            bytes[^2] ^= 0x01;
            await File.WriteAllBytesAsync(journal, bytes);

            var exception = Assert.Throws<LocalApiException>(
                () => new LocalApiCommandRegistry(CreateClient(CreateRunner()), stateSettings: settings));
            Assert.Equal("journal_damaged", exception.Code);
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restart_MarksDispatchedMutationRecoveryRequiredAndNeverReexecutes()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        var installEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? installedSource = null;
        var runner = new AsyncRunner(async (file, arguments, _) =>
        {
            if (arguments.Contains("install"))
            {
                installedSource = arguments[^1];
                installEntered.TrySetResult();
                await releaseInstall.Task;
                return ToolResult(file, arguments);
            }
            if (arguments.Any(value => value.StartsWith("pm path ", StringComparison.Ordinal)))
                return new CommandResult(file, arguments, 0,
                    "package:/data/app/example/base.apk\n", "", TimeSpan.Zero);
            if (arguments.Count >= 3 && arguments[2] == "pull")
            {
                File.Copy(installedSource!, arguments[^1], overwrite: true);
                return ToolResult(file, arguments);
            }
            return ToolResult(file, arguments);
        });
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(root);
            using var first = new LocalApiCommandRegistry(CreateClient(runner), stateSettings: settings);
            var preflight = await first.PreflightAsync(
                Preflight("apk.install-inspected", apk, "QUEST123"));
            var execution = first.ExecuteAsync(Execute(preflight));
            await installEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            first.Dispose();
            using var restarted = new LocalApiCommandRegistry(CreateClient(CreateRunner()), stateSettings: settings);
            var recovered = restarted.GetStatus(Operation(preflight.OperationId));

            Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, recovered.Stage);
            Assert.Equal("restart_recovery_required", recovered.ErrorCode);
            var replay = await Assert.ThrowsAsync<LocalApiException>(
                () => restarted.ExecuteAsync(Execute(preflight)));
            Assert.Equal("operation_consumed", replay.Code);
            releaseInstall.TrySetResult();
            await execution;
        }
        finally
        {
            releaseInstall.TrySetResult();
            File.Delete(apk);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Capacity_IsStableAndDoesNotEvictLiveOperation()
    {
        var root = NewStateRoot();
        var firstApk = await CreateApkAsync();
        var secondApk = await CreateApkAsync();
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(
                root, new LocalApiStateLimits(MaximumRetainedOperations: 1));
            using var registry = new LocalApiCommandRegistry(CreateClient(CreateRunner()), stateSettings: settings);
            var retained = await registry.PreflightAsync(Preflight("apk.inspect", firstApk));

            var exception = await Assert.ThrowsAsync<LocalApiException>(
                () => registry.PreflightAsync(Preflight("apk.inspect", secondApk)));

            Assert.Equal("operation_capacity", exception.Code);
            Assert.Equal(LocalApiOperationStage.Preflighted,
                registry.GetStatus(Operation(retained.OperationId)).Stage);
        }
        finally
        {
            File.Delete(firstApk);
            File.Delete(secondApk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DurableJournal_RejectsValidOldJournalRollback()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        var oldJournal = Array.Empty<byte>();
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(root);
            LocalApiPreflightResult preflight;
            using (var registry = new LocalApiCommandRegistry(CreateClient(CreateRunner()), stateSettings: settings))
            {
                preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk));
                oldJournal = await File.ReadAllBytesAsync(Path.Combine(root, "operations.v1.json"));
                await registry.ExecuteAsync(Execute(preflight));
            }
            await File.WriteAllBytesAsync(Path.Combine(root, "operations.v1.json"), oldJournal);

            var damaged = Assert.Throws<LocalApiException>(
                () => new LocalApiCommandRegistry(CreateClient(CreateRunner()), stateSettings: settings));
            Assert.Equal("journal_damaged", damaged.Code);
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, 100, "operation_capacity")]
    [InlineData(3, 6, "staged_byte_capacity")]
    public async Task ConcurrentPreflights_ReserveOperationAndByteCapacityAtomically(
        int maximumOperations,
        long maximumBytes,
        string expectedCode)
    {
        var root = NewStateRoot();
        var firstApk = await CreateApkAsync();
        var secondApk = await CreateApkAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aaptCalls = 0;
        var runner = new AsyncRunner(async (file, arguments, _) =>
        {
            if (file == "aapt2" && Interlocked.Increment(ref aaptCalls) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return ToolResult(file, arguments);
        });
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(
                root, new LocalApiStateLimits(
                    MaximumRetainedOperations: maximumOperations,
                    MaximumStagedBytes: maximumBytes));
            using var registry = new LocalApiCommandRegistry(CreateClient(runner), stateSettings: settings);
            var first = registry.PreflightAsync(Preflight("apk.inspect", firstApk));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var rejected = await Assert.ThrowsAsync<LocalApiException>(
                () => registry.PreflightAsync(Preflight("apk.inspect", secondApk)));
            Assert.Equal(expectedCode, rejected.Code);
            release.TrySetResult();
            await first;
        }
        finally
        {
            release.TrySetResult();
            File.Delete(firstApk);
            File.Delete(secondApk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConsumePersistenceFailure_RollsBackWithoutDispatch()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        var fail = false;
        var runner = CreateRunner();
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(root) with
            {
                JournalFault = phase => fail && phase == "before_save"
            };
            using var registry = new LocalApiCommandRegistry(CreateClient(runner), stateSettings: settings);
            var preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk));
            var callsBefore = runner.Calls.Count;
            fail = true;

            var exception = await Assert.ThrowsAsync<LocalApiException>(
                () => registry.ExecuteAsync(Execute(preflight)));
            Assert.Equal("journal_persist_failed", exception.Code);
            Assert.Equal(callsBefore, runner.Calls.Count);
            fail = false;
            Assert.Equal(LocalApiOperationStage.Completed,
                (await registry.ExecuteAsync(Execute(preflight))).Stage);
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupDebt_IsDurableBeforeDeletionAndRetriedAfterRestart()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        var failCleanup = true;
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(
                root, new LocalApiStateLimits(TerminalRetention: TimeSpan.Zero)) with
            {
                CleanupFault = _ => failCleanup
            };
            string completedId;
            using (var registry = new LocalApiCommandRegistry(
                       CreateClient(CreateRunner()), timeProvider: time, stateSettings: settings))
            {
                var preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk));
                completedId = preflight.OperationId;
                await registry.ExecuteAsync(Execute(preflight));
                time.Advance(TimeSpan.FromTicks(1));
                await registry.PreflightAsync(Preflight("apk.inspect", apk));
                Assert.Equal(LocalApiOperationStage.CleanupDebt,
                    registry.GetStatus(Operation(completedId)).Stage);
            }

            failCleanup = false;
            using var restarted = new LocalApiCommandRegistry(
                CreateClient(CreateRunner()), timeProvider: time, stateSettings: settings);
            var missing = Assert.Throws<LocalApiException>(
                () => restarted.GetStatus(Operation(completedId)));
            Assert.Equal("operation_unknown", missing.Code);
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupDebt_MissingAfterSecondSaveFailureCompletesOnRestart()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var failSweepSave = false;
        var sweepSaves = 0;
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(
                root,
                new LocalApiStateLimits(TerminalRetention: TimeSpan.Zero)) with
            {
                JournalFault = phase =>
                    failSweepSave &&
                    phase == "before_save" &&
                    Interlocked.Increment(ref sweepSaves) == 2
            };
            string completedId;
            using (var registry = new LocalApiCommandRegistry(
                       CreateClient(CreateRunner()), timeProvider: time, stateSettings: settings))
            {
                var completed = await registry.PreflightAsync(Preflight("apk.inspect", apk));
                completedId = completed.OperationId;
                await registry.ExecuteAsync(Execute(completed));
                time.Advance(TimeSpan.FromTicks(1));
                failSweepSave = true;
                var failure = await Assert.ThrowsAsync<LocalApiException>(
                    () => registry.PreflightAsync(Preflight("apk.inspect", apk)));
                Assert.Equal("journal_persist_failed", failure.Code);
            }

            failSweepSave = false;
            using var restarted = new LocalApiCommandRegistry(
                CreateClient(CreateRunner()), timeProvider: time, stateSettings: settings);
            var missing = Assert.Throws<LocalApiException>(
                () => restarted.GetStatus(Operation(completedId)));
            Assert.Equal("operation_unknown", missing.Code);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Startup_ReclaimsUnjournaledCrashOrphanBeforeAdmission()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        try
        {
            using (var bootstrap = new LocalApiCommandRegistry(
                       CreateClient(CreateRunner()),
                       stateSettings: LocalApiStateSettings.CreateForTests(root)))
            {
            }
            await File.WriteAllBytesAsync(Path.Combine(root, "staged", "orphan.apk"), [7]);
            var settings = LocalApiStateSettings.CreateForTests(
                root,
                new LocalApiStateLimits(MaximumStagedFiles: 1));
            using var registry = new LocalApiCommandRegistry(
                CreateClient(CreateRunner()),
                stateSettings: settings);
            await registry.PreflightAsync(Preflight("apk.inspect", apk));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(Path.Combine(root, "staged"), "*.apk"),
                path => string.Equals(
                    Path.GetFileName(path),
                    "orphan.apk",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "staged"), "*.apk"));
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StateRoot_RejectsSecondActiveOwner()
    {
        var root = NewStateRoot();
        try
        {
            using var owner = new LocalApiCommandRegistry(
                CreateClient(CreateRunner()),
                stateSettings: LocalApiStateSettings.CreateForTests(root));
            var inUse = Assert.Throws<LocalApiException>(
                () => new LocalApiCommandRegistry(
                    CreateClient(CreateRunner()),
                    stateSettings: LocalApiStateSettings.CreateForTests(root)));
            Assert.Equal("state_in_use", inUse.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void StateRoot_RejectsArbitraryWritablePrincipal()
    {
        var root = NewStateRoot();
        try
        {
            using (var bootstrap = new LocalApiCommandRegistry(
                       CreateClient(CreateRunner()),
                       stateSettings: LocalApiStateSettings.CreateForTests(root)))
            {
            }
            var info = new DirectoryInfo(root);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(acl);

            var rejected = Assert.Throws<LocalApiException>(
                () => new LocalApiCommandRegistry(
                    CreateClient(CreateRunner()),
                    stateSettings: LocalApiStateSettings.CreateForTests(root)));
            Assert.Equal("state_acl_untrusted_writer", rejected.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelBeforeExecute_IsRejectedWithoutPoisoningOperation()
    {
        var apk = await CreateApkAsync();
        try
        {
            using var registry = new LocalApiCommandRegistry(CreateClient(CreateRunner()));
            var preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk));
            var rejected = Assert.Throws<LocalApiException>(
                () => registry.Cancel(Operation(preflight.OperationId)));
            Assert.Equal("operation_not_running", rejected.Code);
            Assert.Equal(LocalApiOperationStage.Preflighted,
                registry.GetStatus(Operation(preflight.OperationId)).Stage);
            Assert.Equal(LocalApiOperationStage.Completed,
                (await registry.ExecuteAsync(Execute(preflight))).Stage);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task OversizedExecutorFailure_IsBoundedInStatusAndJournal()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        var failExecution = false;
        var runner = new AsyncRunner((file, arguments, _) =>
        {
            if (failExecution && file == "aapt2")
                return Task.FromResult(new CommandResult(
                    file, arguments, 1, "", new string('x', 100_000), TimeSpan.Zero));
            return Task.FromResult(ToolResult(file, arguments));
        });
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(
                root, new LocalApiStateLimits(MaximumOutputCharacters: 128));
            using var registry = new LocalApiCommandRegistry(CreateClient(runner), stateSettings: settings);
            var preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk));
            failExecution = true;
            var status = await registry.ExecuteAsync(Execute(preflight));
            Assert.Equal(LocalApiOperationStage.Failed, status.Stage);
            Assert.True(status.Error!.Length <= 139);
            Assert.DoesNotContain(new string('x', 256), await File.ReadAllTextAsync(
                Path.Combine(root, "operations.v1.json")));
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Install_UsesStagedImmutableBytesAndBoundsRetainedOutput()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        string? installedSource = null;
        var runner = new AsyncRunner((file, arguments, _) =>
        {
            if (arguments.Contains("install"))
            {
                installedSource = arguments[^1];
                return Task.FromResult(new CommandResult(
                    file, arguments, 0, new string('x', 100_000), "", TimeSpan.Zero));
            }
            if (arguments.Any(value => value.StartsWith("pm path ", StringComparison.Ordinal)))
                return Task.FromResult(new CommandResult(
                    file, arguments, 0, "package:/data/app/example/base.apk\n", "", TimeSpan.Zero));
            if (arguments.Count >= 3 && arguments[2] == "pull")
            {
                File.Copy(installedSource!, arguments[^1], overwrite: true);
                return Task.FromResult(ToolResult(file, arguments));
            }
            return Task.FromResult(ToolResult(file, arguments));
        });
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(root);
            using var registry = new LocalApiCommandRegistry(CreateClient(runner), stateSettings: settings);
            var preflight = await registry.PreflightAsync(
                Preflight("apk.install-inspected", apk, "QUEST123"));
            Assert.StartsWith("retained://", preflight.Artifact.Path, StringComparison.Ordinal);
            Assert.DoesNotContain(root, JsonSerializer.Serialize(preflight),
                StringComparison.OrdinalIgnoreCase);
            await File.WriteAllBytesAsync(apk, [9, 9, 9, 9]);

            var status = await registry.ExecuteAsync(Execute(preflight));
            var result = Assert.IsType<JsonElement>(status.Result);
            var install = result.GetProperty("inspectedApkInstallResult");

            Assert.Equal(LocalApiOperationStage.Completed, status.Stage);
            Assert.NotEqual(Path.GetFullPath(apk), installedSource);
            Assert.Contains("QuestIonAbleFileManager.ApkAdmission", installedSource!,
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("retained://", install.GetProperty("artifact").GetProperty("path").GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(root, result.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "QuestIonAbleFileManager.ApkAdmission",
                result.GetRawText(),
                StringComparison.OrdinalIgnoreCase);
            Assert.True(
                result.GetProperty("commandResult").GetProperty("standardOutput").GetString()!.Length < 5000);
            Assert.Equal(preflight.Artifact.Sha256,
                install.GetProperty("installed").GetProperty("baseApkSha256").GetString());
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ErrorPaths_AreSanitizedBeforeJournalRecovery()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        var fail = false;
        var runner = new AsyncRunner((file, arguments, _) =>
        {
            if (fail && file == "aapt2")
                throw new InvalidOperationException($"inspection failed for {arguments[^1]}");
            return Task.FromResult(ToolResult(file, arguments));
        });
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(root);
            string operationId;
            using (var registry = new LocalApiCommandRegistry(
                       CreateClient(runner),
                       stateSettings: settings))
            {
                var preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk));
                operationId = preflight.OperationId;
                fail = true;
                var failed = await registry.ExecuteAsync(Execute(preflight));
                Assert.Equal(LocalApiOperationStage.Failed, failed.Stage);
                Assert.DoesNotContain(root, failed.Error, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "QuestIonAbleFileManager.ApkAdmission",
                    failed.Error,
                    StringComparison.OrdinalIgnoreCase);
            }

            fail = false;
            using var restarted = new LocalApiCommandRegistry(
                CreateClient(runner),
                stateSettings: settings);
            var recovered = restarted.GetStatus(Operation(operationId));
            Assert.DoesNotContain(root, recovered.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "QuestIonAbleFileManager.ApkAdmission",
                recovered.Error,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(apk);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StagedHandle_BlocksSubstitutionDuringManifestAndSignerInspection()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        var blocked = 0;
        var runner = new AsyncRunner((file, arguments, _) =>
        {
            if (file is "aapt2" or "apksigner")
            {
                var stagedPath = arguments[^1];
                Assert.Throws<IOException>(() => File.WriteAllBytes(stagedPath, [9, 9, 9, 9]));
                Assert.Throws<IOException>(() => File.Delete(stagedPath));
                Assert.Throws<IOException>(() => File.Move(
                    stagedPath,
                    stagedPath + ".moved",
                    overwrite: true));
                Interlocked.Increment(ref blocked);
            }
            return Task.FromResult(ToolResult(file, arguments));
        });
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(root);
            using var registry = new LocalApiCommandRegistry(CreateClient(runner), stateSettings: settings);
            await registry.PreflightAsync(Preflight("apk.inspect", apk));
            Assert.Equal(2, blocked);
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunningCapacity_RejectsConcurrentExecutionWithoutConsumingSecond()
    {
        var root = NewStateRoot();
        var firstApk = await CreateApkAsync();
        var secondApk = await CreateApkAsync();
        var block = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new AsyncRunner(async (file, arguments, _) =>
        {
            if (file == "aapt2" && block)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return ToolResult(file, arguments);
        });
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(
                root, new LocalApiStateLimits(MaximumRunningOperations: 1));
            using var registry = new LocalApiCommandRegistry(CreateClient(runner), stateSettings: settings);
            var first = await registry.PreflightAsync(Preflight("apk.inspect", firstApk));
            var second = await registry.PreflightAsync(Preflight("apk.inspect", secondApk));
            block = true;
            var running = registry.ExecuteAsync(Execute(first));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var capacity = await Assert.ThrowsAsync<LocalApiException>(
                () => registry.ExecuteAsync(Execute(second)));
            Assert.Equal("running_capacity", capacity.Code);
            Assert.Equal(LocalApiOperationStage.Preflighted,
                registry.GetStatus(Operation(second.OperationId)).Stage);
            release.TrySetResult();
            await running;
        }
        finally
        {
            release.TrySetResult();
            File.Delete(firstApk);
            File.Delete(secondApk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sweep_EvictsOnlyTerminalPastRetention()
    {
        var root = NewStateRoot();
        var firstApk = await CreateApkAsync();
        var secondApk = await CreateApkAsync();
        try
        {
            var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-27T12:00:00Z"));
            var settings = LocalApiStateSettings.CreateForTests(
                root,
                new LocalApiStateLimits(
                    MaximumRetainedOperations: 1,
                    TerminalRetention: TimeSpan.FromSeconds(1)));
            using var registry = new LocalApiCommandRegistry(
                CreateClient(CreateRunner()), timeProvider: time, stateSettings: settings);
            var first = await registry.PreflightAsync(Preflight("apk.inspect", firstApk));
            await registry.ExecuteAsync(Execute(first));
            time.Advance(TimeSpan.FromSeconds(2));

            var second = await registry.PreflightAsync(Preflight("apk.inspect", secondApk));

            Assert.Equal(LocalApiOperationStage.Preflighted,
                registry.GetStatus(Operation(second.OperationId)).Stage);
            var removed = Assert.Throws<LocalApiException>(
                () => registry.GetStatus(Operation(first.OperationId)));
            Assert.Equal("operation_unknown", removed.Code);
        }
        finally
        {
            File.Delete(firstApk);
            File.Delete(secondApk);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Preflight_RetainsExactCommandAndExecuteIsOneUse()
    {
        var apk = await CreateApkAsync();
        try
        {
            var runner = CreateRunner();
            var registry = new LocalApiCommandRegistry(CreateClient(runner));
            var preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk));

            var status = await registry.ExecuteAsync(Execute(preflight));
            var result = Assert.IsType<JsonElement>(status.Result);

            Assert.Equal(LocalApiOperationStage.Completed, status.Stage);
            Assert.Equal(
                "inspectApk",
                result.GetProperty("command").GetProperty("kind").GetString());
            Assert.StartsWith(
                "retained://",
                result.GetProperty("command").GetProperty("localPath").GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetTempPath(), result.GetRawText(),
                StringComparison.OrdinalIgnoreCase);
            var replay = await Assert.ThrowsAsync<LocalApiException>(
                () => registry.ExecuteAsync(Execute(preflight)));
            Assert.Equal("operation_consumed", replay.Code);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Execute_RejectsDigestMismatchAndExpiredCommand()
    {
        var apk = await CreateApkAsync();
        try
        {
            var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-27T12:00:00Z"));
            var registry = new LocalApiCommandRegistry(CreateClient(CreateRunner()), timeProvider: time);
            var preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk, expires: 1));
            var mismatch = await Assert.ThrowsAsync<LocalApiException>(() =>
                registry.ExecuteAsync(Bytes(JsonSerializer.Serialize(new
                {
                    contractVersion = LocalApiContract.Version,
                    operationId = preflight.OperationId,
                    commandDigest = new string('0', 64)
                }))));
            Assert.Equal("digest_mismatch", mismatch.Code);

            time.Advance(TimeSpan.FromSeconds(2));
            var expired = await Assert.ThrowsAsync<LocalApiException>(
                () => registry.ExecuteAsync(Execute(preflight)));
            Assert.Equal("operation_expired", expired.Code);
            Assert.Equal(LocalApiOperationStage.Expired,
                registry.GetStatus(Operation(preflight.OperationId)).Stage);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Execute_UsesStagedBytesWhenCallerPathChangesAfterPreflight()
    {
        var apk = await CreateApkAsync();
        try
        {
            var registry = new LocalApiCommandRegistry(CreateClient(CreateRunner()));
            var preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk));
            await File.WriteAllBytesAsync(apk, [9, 8, 7, 6]);

            var status = await registry.ExecuteAsync(Execute(preflight));
            var result = Assert.IsType<JsonElement>(status.Result);

            Assert.Equal(LocalApiOperationStage.Completed, status.Stage);
            Assert.StartsWith(
                "retained://",
                result.GetProperty("command").GetProperty("localPath").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(
                preflight.Artifact.Sha256,
                result.GetProperty("apkArtifactInspection").GetProperty("sha256").GetString());
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Preflight_TargetCheckIsReadOnlyAndExact()
    {
        var apk = await CreateApkAsync();
        try
        {
            var runner = CreateRunner();
            var registry = new LocalApiCommandRegistry(CreateClient(runner));
            var preflight = await registry.PreflightAsync(
                Preflight("runtime.observe", apk, "QUEST123"));

            Assert.Equal("QUEST123", preflight.Target!.Serial);
            Assert.Contains(runner.Calls, call =>
                call.Arguments.SequenceEqual(["devices", "-l"]));
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Contains("install") || call.Arguments.Contains("start"));
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Cancellation_OwnsOnlyRetainedInFlightToken()
    {
        var apk = await CreateApkAsync();
        var executeInspection = false;
        var runner = new AsyncRunner(async (file, arguments, token) =>
        {
            if (file == "aapt2" && executeInspection)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return ToolResult(file, arguments);
        });
        try
        {
            var registry = new LocalApiCommandRegistry(CreateClient(runner));
            var preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk));
            executeInspection = true;
            var execution = registry.ExecuteAsync(Execute(preflight));
            await WaitUntilAsync(() =>
                registry.GetStatus(Operation(preflight.OperationId)).Stage == LocalApiOperationStage.Running);

            var cancelled = registry.Cancel(Operation(preflight.OperationId));
            var completed = await execution;

            Assert.Equal(LocalApiOperationStage.CancellationRequested, cancelled.Stage);
            Assert.Equal(LocalApiOperationStage.Cancelled, completed.Stage);
            Assert.Equal(LocalApiOperationStage.Cancelled,
                registry.GetStatus(Operation(preflight.OperationId)).Stage);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task Cancellation_AfterMutationDispatchRequiresRecovery()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        var installEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new AsyncRunner(async (file, arguments, token) =>
        {
            if (arguments.Contains("install"))
            {
                installEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return ToolResult(file, arguments);
        });
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(root);
            using var registry = new LocalApiCommandRegistry(CreateClient(runner), stateSettings: settings);
            var preflight = await registry.PreflightAsync(
                Preflight("apk.install-inspected", apk, "QUEST123"));
            var execution = registry.ExecuteAsync(Execute(preflight));
            await installEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var requested = registry.Cancel(Operation(preflight.OperationId));
            var terminal = await execution;

            Assert.Equal(LocalApiOperationStage.CancellationRequested, requested.Stage);
            Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, terminal.Stage);
            Assert.NotEqual(LocalApiOperationStage.Cancelled, terminal.Stage);
            Assert.Equal(OperatorMutationStage.Pending, terminal.MutationEvidence!.Stage);
        }
        finally
        {
            File.Delete(apk);
            Directory.Delete(root, recursive: true);
        }
    }

    private static AdbClient CreateClient(ICommandRunner runner) =>
        new("adb", runner, new AndroidBuildToolPaths("aapt2", "apksigner"));

    private static AsyncRunner CreateRunner() =>
        new((file, arguments, _) => Task.FromResult(ToolResult(file, arguments)));

    private static CommandResult ToolResult(string file, IReadOnlyList<string> arguments)
    {
        var output = file switch
        {
            "aapt2" => "package: name='com.example.app' versionCode='42' versionName='1.2.3'\n",
            "apksigner" => "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n",
            _ when arguments.SequenceEqual(["devices", "-l"]) =>
                "List of devices attached\nQUEST123 device model:Quest_3\n",
            _ => "Success\n"
        };
        return new CommandResult(file, arguments, 0, output, "", TimeSpan.Zero);
    }

    private static ReadOnlyMemory<byte> Preflight(
        string command,
        string apk,
        string? serial = null,
        int? expires = null) =>
        Bytes(JsonSerializer.Serialize(new
        {
            contractVersion = LocalApiContract.Version,
            command,
            apkPath = apk,
            serial,
            expiresInSeconds = expires
        }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));

    private static ReadOnlyMemory<byte> Execute(LocalApiPreflightResult preflight) =>
        Bytes(JsonSerializer.Serialize(new
        {
            contractVersion = LocalApiContract.Version,
            operationId = preflight.OperationId,
            commandDigest = preflight.CommandDigest
        }));

    private static ReadOnlyMemory<byte> Operation(string id) =>
        Bytes(JsonSerializer.Serialize(new
        {
            contractVersion = LocalApiContract.Version,
            operationId = id
        }));

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static async Task<string> CreateApkAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qfm-local-api-{Guid.NewGuid():N}.apk");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        return path;
    }

    private static string NewStateRoot()
    {
        return Path.Combine(Path.GetTempPath(), $"qfm-api-state-test-{Guid.NewGuid():N}");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var index = 0; index < 100 && !predicate(); index++)
        {
            await Task.Delay(5);
        }
        Assert.True(predicate());
    }

    private sealed class AsyncRunner(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<CommandResult>> handler)
        : IStreamingCommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public async Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return await handler(fileName, arguments, cancellationToken);
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
            var result = await handler(fileName, arguments, cancellationToken);
            var bytes = new byte[] { 1, 2, 3, 4 };
            if (result.Succeeded)
            {
                if (bytes.Length > maximumBytes)
                    throw new FleetTransferLimitException(maximumBytes);
                await destination.WriteAsync(bytes, cancellationToken);
            }
            return new StreamingCommandResult(
                result,
                result.Succeeded ? bytes.Length : 0,
                Convert.ToHexString(SHA256.HashData(result.Succeeded ? bytes : []))
                    .ToLowerInvariant());
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
