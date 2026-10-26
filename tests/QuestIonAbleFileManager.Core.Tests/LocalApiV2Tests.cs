using System.Reflection;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed partial class LocalApiTests
{
    [Fact]
    public void V1Digest_AuthenticatedLegacyBindingFixture()
    {
        var artifact = new ApkArtifactInspection(@"C:\fixture\base.apk", 4, new string('b', 64),
            new ApkArtifactIdentity("com.example.app", 42, "1.2.3", new string('a', 64), null));
        var command = OperatorCommands.InstallApk("QUEST123", artifact.Path);
        var digest = (string)typeof(LocalApiCommandRegistry).GetMethod("ComputeCommandDigest",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [command, artifact])!;
        Assert.Equal("239d9d1aa5b289c44fbd1359c3f50ac2b6c9a8b571cf4352ad0535320d75c3f1", digest);
    }

    [Fact]
    public async Task V1Journal_LeavesLegacyRowUntaggedAndStrictlyReopens()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(root);
            LocalApiPreflightResult preflight;
            using (var registry = new LocalApiCommandRegistry(CreateClient(CreateRunner()), settings))
                preflight = await registry.PreflightAsync(Preflight("apk.inspect", apk));
            using var envelope = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "operations.v1.json")));
            using var payload = JsonDocument.Parse(Convert.FromBase64String(envelope.RootElement.GetProperty("payload").GetString()!));
            Assert.Equal(new[] { "operationId", "commandName", "commandDigest", "expiresAt", "stagedPath", "artifact",
                "serial", "installOptions", "stage", "consumed", "dispatched", "terminalAt", "resultEvidence",
                "mutationEvidence", "errorCode", "error" }, payload.RootElement[0].EnumerateObject().Select(p => p.Name));
            using var restarted = new LocalApiCommandRegistry(CreateClient(CreateRunner()), settings);
            Assert.Equal(preflight.CommandDigest, restarted.GetStatus(Operation(preflight.OperationId)).CommandDigest);
        }
        finally { File.Delete(apk); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task V2Deploy_PreflightAdmitsOneStagedApkWithExplicitVersion()
    {
        var root = NewStateRoot();
        var apk = await CreateApkAsync();
        try
        {
            using var registry = new LocalApiCommandRegistry(CreateClient(CreateRunner()), LocalApiStateSettings.CreateForTests(root));
            var preflight = await registry.PreflightV2Async(V2Preflight(apk));
            Assert.Equal("questionable.file_manager.local_api.v2", preflight.ContractVersion);
            Assert.Equal("apk.deploy", preflight.Command);
            Assert.Equal("QUEST123", preflight.Target!.Serial);
            Assert.StartsWith("retained://", preflight.Artifact.Path);
        }
        finally { File.Delete(apk); Directory.Delete(root, true); }
    }


    [Theory]
    [InlineData("apk.inspect")]
    [InlineData("apk.install-inspected")]
    [InlineData("app.launch-resolved")]
    [InlineData("runtime.observe")]
    [InlineData("adb.raw")]
    public async Task V2Preflight_RejectsEveryOtherAction(string command)
    {
        using var fixture = new V2Fixture();
        var rejected = await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.PreflightV2Async(
            V2Preflight(fixture.Apk, command: command)));
        Assert.Equal("command_not_allowed", rejected.Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "staged"), "*.apk"));
    }

    [Theory]
    [InlineData("allowDowngrade", "downgrade_not_allowed")]
    [InlineData("allowTestPackages", "test_only_not_allowed")]
    [InlineData("arbitrary", "request_invalid")]
    public async Task V2Preflight_RejectsUnsupportedOptions(string option, string code)
    {
        using var fixture = new V2Fixture();
        var body = V2Preflight(fixture.Apk);
        var node = System.Text.Json.Nodes.JsonNode.Parse(body.Span)!;
        node["installOptions"] = new System.Text.Json.Nodes.JsonObject { [option] = true };
        var rejected = await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.PreflightV2Async(Bytes(node.ToJsonString())));
        Assert.Equal(code, rejected.Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "staged"), "*.apk"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V2Deploy_ProjectsOnlyExactInstallAndLaunchAcknowledgement(bool launchObserved)
    {
        using var fixture = new V2Fixture(launchObserved);
        var preflight = await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        // Caller source can change; only the private immutable staged bytes execute.
        await File.WriteAllBytesAsync(fixture.Apk, [9, 8, 7]);
        var result = await fixture.Registry.ExecuteV2Async(V2Execute(preflight));
        Assert.True(result.Stage == (launchObserved ? LocalApiOperationStage.Completed : LocalApiOperationStage.OutcomeUnknownRecoveryRequired), result.Error);
        Assert.Null(result.Error);
        Assert.Equal(launchObserved ? OperatorMutationStage.Confirmed : OperatorMutationStage.Pending, result.MutationEvidence!.Stage);
        var json = Assert.IsType<JsonElement>(result.Result);
        var boundary = json.GetProperty("inspectedApkDeploymentResult").GetProperty("claimBoundary");
        Assert.True(boundary.GetProperty("exactInstalledBytesConfirmed").GetBoolean());
        Assert.Equal(launchObserved, boundary.GetProperty("qfmOwnedInstallLaunchEffectConfirmed").GetBoolean());
        Assert.Equal("unknown", boundary.GetProperty("applicationReadiness").GetString());
        Assert.Equal("unknown", boundary.GetProperty("openXrReadiness").GetString());
        Assert.DoesNotContain(fixture.Root, json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApkAdmission", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Runner.InstallCount);
        Assert.Equal(1, fixture.Runner.LaunchCount);
        var replay = await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV2Async(V2Execute(preflight)));
        Assert.Equal("operation_consumed", replay.Code);
    }

    [Fact]
    public async Task V2AndV1_MixedJournalReconstructsExactCommandsWithoutReplaying()
    {
        using var fixture = new V2Fixture();
        var legacy = await fixture.Registry.PreflightAsync(Preflight("apk.inspect", fixture.Apk));
        var deploy = await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        fixture.Reopen();
        Assert.Equal(LocalApiContract.Version, fixture.Registry.GetStatus(Operation(legacy.OperationId)).ContractVersion);
        Assert.Equal(LocalApiV2Contract.Version, fixture.Registry.GetV2Status(V2Operation(deploy.OperationId)).ContractVersion);
        Assert.Equal(0, fixture.Runner.InstallCount);
        Assert.Equal(LocalApiOperationStage.Completed, (await fixture.Registry.ExecuteV2Async(V2Execute(deploy))).Stage);
        fixture.Reopen();
        Assert.Equal(LocalApiOperationStage.Completed, fixture.Registry.GetV2Status(V2Operation(deploy.OperationId)).Stage);
        var replay = await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV2Async(V2Execute(deploy)));
        Assert.Equal("operation_consumed", replay.Code);
        fixture.Registry.Dispose();
        using var journal = new LocalApiJournal(fixture.Settings);
        var entries = journal.Load();
        Assert.Null(entries.Single(e => e.OperationId == legacy.OperationId).OperationVersion);
        Assert.Equal(LocalApiV2Contract.Version, entries.Single(e => e.OperationId == deploy.OperationId).OperationVersion);
    }

    [Fact]
    public async Task VersionMismatch_RejectsBeforeDispatchExpirationPersistenceOrCancellation()
    {
        using var fixture = new V2Fixture();
        var legacy = await fixture.Registry.PreflightAsync(Preflight("apk.inspect", fixture.Apk));
        var deploy = await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        var before = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "operations.v1.json"));
        Assert.Equal("operation_version_mismatch", (await Assert.ThrowsAsync<LocalApiException>(
            () => fixture.Registry.ExecuteAsync(Execute(deploy)))).Code);
        Assert.Equal("operation_version_mismatch", Assert.Throws<LocalApiException>(
            () => fixture.Registry.GetStatus(Operation(deploy.OperationId))).Code);
        Assert.Equal("operation_version_mismatch", Assert.Throws<LocalApiException>(
            () => fixture.Registry.Cancel(Operation(deploy.OperationId))).Code);
        Assert.Equal("operation_version_mismatch", (await Assert.ThrowsAsync<LocalApiException>(
            () => fixture.Registry.ExecuteV2Async(V2Execute(legacy)))).Code);
        Assert.Equal("operation_version_mismatch", Assert.Throws<LocalApiException>(
            () => fixture.Registry.GetV2Status(V2Operation(legacy.OperationId))).Code);
        Assert.Equal("operation_version_mismatch", Assert.Throws<LocalApiException>(
            () => fixture.Registry.CancelV2(V2Operation(legacy.OperationId))).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "operations.v1.json")));
        Assert.Equal(0, fixture.Runner.InstallCount);
        Assert.Equal(LocalApiOperationStage.Preflighted, fixture.Registry.GetV2Status(V2Operation(deploy.OperationId)).Stage);
    }

    [Fact]
    public async Task V2Execute_RejectsChangedDigestAndAdditionalExecuteArguments()
    {
        using var fixture = new V2Fixture();
        var deploy = await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        Assert.Equal("digest_mismatch", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV2Async(
            V2Execute(deploy with { CommandDigest = new string('0', 64) })))).Code);
        var body = System.Text.Json.Nodes.JsonNode.Parse(V2Execute(deploy).Span)!;
        body["serial"] = "OTHER";
        Assert.Equal("request_invalid", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV2Async(Bytes(body.ToJsonString())))).Code);
        Assert.Equal(0, fixture.Runner.InstallCount);
    }

    [Theory]
    [InlineData("options")]
    [InlineData("serial")]
    [InlineData("digest")]
    [InlineData("bytes")]
    [InlineData("disallowedOptions")]
    [InlineData("action")]
    public async Task V2Reopen_RejectsAlteredRetainedBindingsWithoutDeletingSource(string changed)
    {
        using var fixture = new V2Fixture();
        await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        fixture.Registry.Dispose();
        LocalApiJournalEntry entry;
        using (var journal = new LocalApiJournal(fixture.Settings))
        {
            entry = Assert.Single(journal.Load());
            var altered = changed switch
            {
                "options" => entry with { InstallOptions = new LocalApiInstallOptions(GrantRuntimePermissions: true) },
                "disallowedOptions" => entry with { InstallOptions = new LocalApiInstallOptions(AllowDowngrade: true) },
                "action" => entry with { CommandName = "apk.install-inspected" },
                "serial" => entry with { Serial = "OTHER" },
                "digest" => entry with { CommandDigest = new string('0', 64) },
                _ => entry
            };
            journal.Save([altered]);
        }
        if (changed == "bytes") await File.WriteAllBytesAsync(entry.StagedPath, [4, 3, 2, 1]);
        Assert.Equal("journal_damaged", Assert.Throws<LocalApiException>(() =>
            new LocalApiCommandRegistry(CreateClient(fixture.Runner), fixture.Settings)).Code);
        Assert.True(File.Exists(fixture.Apk));
        Assert.True(File.Exists(entry.StagedPath));
        Assert.Equal(0, fixture.Runner.InstallCount);
    }

    [Fact]
    public async Task UnknownJournalVersion_RejectsBeforeOrphanCleanupAndOldReaderRejectsV2Tag()
    {
        using var fixture = new V2Fixture();
        await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        fixture.Registry.Dispose();
        using (var journal = new LocalApiJournal(fixture.Settings))
        {
            var entry = Assert.Single(journal.Load());
            // A legacy strict row reader has no operationVersion field.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LegacyJournalEntry>(bytes, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }));
            journal.Save([entry with { OperationVersion = "questionable.file_manager.local_api.v99" }]);
        }
        var orphan = Path.Combine(fixture.Root, "staged", "untracked.apk");
        await File.WriteAllBytesAsync(orphan, [8, 9]);
        Assert.Equal("journal_version_unsupported", Assert.Throws<LocalApiException>(() =>
            new LocalApiCommandRegistry(CreateClient(fixture.Runner), fixture.Settings)).Code);
        Assert.True(File.Exists(orphan));
        Assert.True(File.Exists(fixture.Apk));
    }

    [Fact]
    public async Task V2ConsumePersistenceFailure_DoesNotDispatchAndAllowsOneRetry()
    {
        var fail = false;
        using var fixture = new V2Fixture(settings => settings with { JournalFault = phase => fail && phase == "before_save" });
        var deploy = await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        fail = true;
        Assert.Equal("journal_persist_failed", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV2Async(V2Execute(deploy)))).Code);
        Assert.Equal(0, fixture.Runner.InstallCount);
        fail = false;
        Assert.Equal(LocalApiOperationStage.Completed, (await fixture.Registry.ExecuteV2Async(V2Execute(deploy))).Stage);
        Assert.Equal(1, fixture.Runner.InstallCount);
    }


    [Fact]
    public async Task V2Execute_PageLossDoesNotCancelAdmittedEffectAndConcurrentReplayCannotDispatch()
    {
        using var fixture = new V2Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runner.BeforeCommand = async (arguments, token) =>
        {
            if (!arguments.Contains("install")) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var deploy = await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        using var disconnected = new CancellationTokenSource();
        var execution = fixture.Registry.ExecuteV2Async(V2Execute(deploy), disconnected.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disconnected.Cancel();
            Assert.False(execution.IsCompleted);
            Assert.Equal(LocalApiOperationStage.Running, fixture.Registry.GetV2Status(V2Operation(deploy.OperationId)).Stage);
            Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<LocalApiException>(
                () => fixture.Registry.ExecuteV2Async(V2Execute(deploy)))).Code);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(LocalApiOperationStage.Completed, (await execution).Stage);
        Assert.Equal(1, fixture.Runner.InstallCount);
        Assert.Equal(1, fixture.Runner.LaunchCount);
    }

    [Fact]
    public async Task V2Cancel_AfterDispatchRemainsPendingUntilOwnerAcknowledgesThenUnknown()
    {
        using var fixture = new V2Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runner.BeforeCommand = async (arguments, token) =>
        {
            if (!arguments.Contains("install")) return;
            entered.TrySetResult();
            await acknowledge.Task; // An in-flight owner has not acknowledged cancellation yet.
            token.ThrowIfCancellationRequested();
        };
        var deploy = await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        var execution = fixture.Registry.ExecuteV2Async(V2Execute(deploy));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(LocalApiOperationStage.CancellationRequested, fixture.Registry.CancelV2(V2Operation(deploy.OperationId)).Stage);
            Assert.False(execution.IsCompleted);
            Assert.Equal(LocalApiOperationStage.CancellationRequested, fixture.Registry.GetV2Status(V2Operation(deploy.OperationId)).Stage);
        }
        finally { acknowledge.TrySetResult(); }
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, (await execution).Stage);
        Assert.Equal(OperatorMutationStage.Pending, fixture.Registry.GetV2Status(V2Operation(deploy.OperationId)).MutationEvidence!.Stage);
        Assert.Equal(0, fixture.Runner.LaunchCount);
        fixture.Reopen();
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, fixture.Registry.GetV2Status(V2Operation(deploy.OperationId)).Stage);
        Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV2Async(V2Execute(deploy)))).Code);
    }

    [Fact]
    public async Task V2Restart_DispatchedMutationBecomesUnknownWithoutReplay()
    {
        using var fixture = new V2Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runner.BeforeCommand = async (arguments, _) =>
        {
            if (!arguments.Contains("install")) return;
            entered.TrySetResult();
            await release.Task;
            throw new OperationCanceledException();
        };
        var deploy = await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        var interrupted = fixture.Registry.ExecuteV2Async(V2Execute(deploy));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Reopen();
            var recovered = fixture.Registry.GetV2Status(V2Operation(deploy.OperationId));
            Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, recovered.Stage);
            Assert.Equal("restart_recovery_required", recovered.ErrorCode);
            Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV2Async(V2Execute(deploy)))).Code);
            Assert.Equal(0, fixture.Runner.InstallCount);
        }
        finally { release.TrySetResult(); await interrupted; }
        Assert.Equal(0, fixture.Runner.LaunchCount);
    }

    [Fact]
    public async Task V2TerminalPersistenceFailure_RemainsUnknownAndCannotReplay()
    {
        var fail = false;
        using var fixture = new V2Fixture(settings => settings with { JournalFault = phase => fail && phase == "before_save" });
        fixture.Runner.BeforeCommand = (arguments, _) =>
        {
            if (arguments.Contains("install")) fail = true;
            return Task.CompletedTask;
        };
        var deploy = await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        var result = await fixture.Registry.ExecuteV2Async(V2Execute(deploy));
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, result.Stage);
        Assert.Equal("journal_persist_failed", result.ErrorCode);
        fail = false;
        fixture.Reopen();
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, fixture.Registry.GetV2Status(V2Operation(deploy.OperationId)).Stage);
        Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV2Async(V2Execute(deploy)))).Code);
        Assert.Equal(1, fixture.Runner.InstallCount);
    }

    [Fact]
    public async Task V2PreflightExpiryAndPreExecutionCancellation_PreserveExistingSemantics()
    {
        using var fixture = new V2Fixture();
        var deploy = await fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk));
        Assert.Equal("operation_not_running", Assert.Throws<LocalApiException>(() => fixture.Registry.CancelV2(V2Operation(deploy.OperationId))).Code);
        fixture.Registry.Dispose();
        using (var journal = new LocalApiJournal(fixture.Settings))
        {
            var entry = Assert.Single(journal.Load());
            journal.Save([entry with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }]);
        }
        fixture.Reopen();
        Assert.Equal(LocalApiOperationStage.Expired, fixture.Registry.GetV2Status(V2Operation(deploy.OperationId)).Stage);
        Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV2Async(V2Execute(deploy)))).Code);
        Assert.Equal(0, fixture.Runner.InstallCount);
    }

    [Fact]
    public async Task V2SharesV1CapacityAndNeverDeletesCallerSourceOnRejection()
    {
        using var fixture = new V2Fixture(settings => settings with { Limits = new LocalApiStateLimits(MaximumRetainedOperations: 1) });
        await fixture.Registry.PreflightAsync(Preflight("apk.inspect", fixture.Apk));
        Assert.Equal("operation_capacity", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.PreflightV2Async(V2Preflight(fixture.Apk)))).Code);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(fixture.Apk));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "staged"), "*.apk"));
    }

    private static ReadOnlyMemory<byte> V2Preflight(string apk, string command = "apk.deploy") => Bytes(JsonSerializer.Serialize(new
    {
        contractVersion = LocalApiV2Contract.Version, command, apkPath = apk, serial = "QUEST123"
    }));

    private static ReadOnlyMemory<byte> V2Execute(LocalApiPreflightResult preflight) => Bytes(JsonSerializer.Serialize(new
    {
        contractVersion = LocalApiV2Contract.Version, operationId = preflight.OperationId, commandDigest = preflight.CommandDigest
    }));

    private static ReadOnlyMemory<byte> V2Operation(string id) => Bytes(JsonSerializer.Serialize(new
    { contractVersion = LocalApiV2Contract.Version, operationId = id }));

    // Exact pre-versioned row shape, retained to exercise old-reader fail-closed behavior.
    private sealed record LegacyJournalEntry(
        string OperationId, string CommandName, string CommandDigest, DateTimeOffset ExpiresAt,
        string StagedPath, ApkArtifactInspection Artifact, string? Serial, LocalApiInstallOptions? InstallOptions,
        LocalApiOperationStage Stage, bool Consumed, bool Dispatched, DateTimeOffset? TerminalAt,
        JsonElement? ResultEvidence, OperatorMutationReceipt? MutationEvidence, string? ErrorCode, string? Error);

    private sealed class V2Fixture : IDisposable
    {
        public string Root { get; } = NewStateRoot();
        public string Apk { get; } = Path.Combine(Path.GetTempPath(), "qfm-v2-" + Guid.NewGuid().ToString("N") + ".apk");
        public V2Runner Runner { get; }
        public LocalApiStateSettings Settings { get; }
        public LocalApiCommandRegistry Registry { get; private set; }
        public V2Fixture(bool launchObserved = true) : this(null, launchObserved) { }
        public V2Fixture(Func<LocalApiStateSettings, LocalApiStateSettings>? configure, bool launchObserved = true)
        {
            File.WriteAllBytes(Apk, [1, 2, 3, 4]);
            Runner = new V2Runner(launchObserved);
            var settings = LocalApiStateSettings.CreateForTests(Root);
            Settings = configure?.Invoke(settings) ?? settings;
            Registry = new LocalApiCommandRegistry(CreateClient(Runner), Settings);
        }
        public void Reopen()
        {
            Registry.Dispose();
            Registry = new LocalApiCommandRegistry(CreateClient(Runner), Settings);
        }
        public void Dispose()
        {
            Registry.Dispose();
            File.Delete(Apk);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class V2Runner(bool launchObserved) : IStreamingCommandRunner
    {
        public int InstallCount;
        public int LaunchCount;
        public Func<IReadOnlyList<string>, CancellationToken, Task>? BeforeCommand;
        public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (BeforeCommand is not null) await BeforeCommand(arguments, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments.Contains("install")) Interlocked.Increment(ref InstallCount);
            if (arguments.Contains("start")) Interlocked.Increment(ref LaunchCount);
            var output = fileName switch
            {
                "aapt2" => "package: name='com.example.app' versionCode='42' versionName='1.2.3'\n",
                "apksigner" => "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n",
                _ when arguments.SequenceEqual(["devices", "-l"]) => "List of devices attached\nQUEST123 device model:Quest_3\n",
                _ when arguments.Any(a => a.StartsWith("pm path ", StringComparison.Ordinal)) => "package:/data/app/example/base.apk\n",
                _ when arguments.Contains("query-activities") => "com.example.app/.Main\n",
                _ when arguments.Contains("package") => "  Activity #0 ActivityInfo{abc com.example.app/.Main}\n    exported=true\n",
                _ when arguments.Contains("activities") => launchObserved ? "mResumedActivity: ActivityRecord{1 com.example.app/.Main}\n" : "",
                _ when arguments.Contains("pidof") => "123\n",
                _ => "Success\n"
            };
            return new CommandResult(fileName, arguments, 0, output, "", TimeSpan.Zero);
        }
        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments,
            Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (arguments.Count == 6 && arguments[2] == "exec-out" && arguments[5].Contains("qfm-installed-digest:v1", StringComparison.Ordinal))
            {
                var unsupported = Encoding.UTF8.GetBytes("qfm-installed-digest:unsupported\n");
                await destination.WriteAsync(unsupported, cancellationToken);
                return new StreamingCommandResult(new CommandResult(fileName, arguments, 90, "", "", TimeSpan.Zero),
                    unsupported.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(unsupported)).ToLowerInvariant());
            }
            var result = await RunAsync(fileName, arguments, timeout, cancellationToken);
            var bytes = arguments.Contains("exec-out") ? new byte[] { 1, 2, 3, 4 } : Encoding.UTF8.GetBytes(result.StandardOutput);
            if (bytes.Length > maximumBytes) throw new FleetTransferLimitException(maximumBytes);
            await destination.WriteAsync(bytes, cancellationToken);
            return new StreamingCommandResult(result, bytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}
