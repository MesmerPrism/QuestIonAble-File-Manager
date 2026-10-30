using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserApkSetTests
{
    private const string Serial = "QUEST123";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Theory]
    [InlineData("top-duplicate")]
    [InlineData("source-duplicate")]
    [InlineData("option-duplicate")]
    [InlineData("top-extra")]
    [InlineData("source-extra")]
    [InlineData("option-extra")]
    [InlineData("option-string")]
    [InlineData("missing-option")]
    [InlineData("no-confirmation")]
    [InlineData("string-confirmation")]
    [InlineData("one-part")]
    [InlineData("nine-parts")]
    [InlineData("same-id")]
    [InlineData("same-name")]
    [InlineData("non-apk")]
    [InlineData("reserved-name")]
    [InlineData("traversal")]
    [InlineData("unicode-name")]
    [InlineData("name-too-long")]
    [InlineData("bad-hash")]
    [InlineData("bad-source-id")]
    [InlineData("bad-request-id")]
    [InlineData("bad-serial")]
    [InlineData("negative-size")]
    [InlineData("oversize")]
    [InlineData("source-capacity")]
    public async Task ClosedMalformedRequestsNeverConsumeAnIdOrReachTheExecutor(string fault)
    {
        var sources = FakeSources();
        var requestId = Guid.NewGuid().ToString("D");
        var text = Encoding.UTF8.GetString(SetRequest(sources, requestId: requestId));
        text = fault switch
        {
            "top-duplicate" => text.Replace("\"action\":", "\"requestId\":\"" + requestId + "\",\"action\":"),
            "source-duplicate" => text.Replace("\"fileName\":", "\"id\":\"" + sources[0].Id + "\",\"fileName\":"),
            "option-duplicate" => text.Replace("\"allowDowngrade\":", "\"replaceExisting\":true,\"allowDowngrade\":"),
            "top-extra" => text.Insert(1, "\"folder\":\"private-output-marker\","),
            "source-extra" => text.Replace("\"fileName\":", "\"path\":\"private-output-marker\",\"fileName\":"),
            "option-extra" => text.Replace("\"allowDowngrade\":", "\"argv\":[],\"allowDowngrade\":"),
            "option-string" => text.Replace("\"replaceExisting\":true", "\"replaceExisting\":\"true\""),
            "missing-option" => text.Replace("\"allowDowngrade\":false,", ""),
            "no-confirmation" => text.Replace("\"confirmed\":true", "\"confirmed\":false"),
            "string-confirmation" => text.Replace("\"confirmed\":true", "\"confirmed\":\"true\""),
            "one-part" => Encoding.UTF8.GetString(SetRequest(sources.Take(1).ToArray(), requestId: requestId)),
            "nine-parts" => Encoding.UTF8.GetString(SetRequest(FakeSources(9), requestId: requestId)),
            "same-id" => text.Replace(sources[1].Id, sources[0].Id),
            "same-name" => text.Replace(sources[1].FileName, "BASE.APK"),
            "non-apk" => text.Replace("base.apk", "base.zip"),
            "reserved-name" => text.Replace("base.apk", "CON.apk"),
            "traversal" => text.Replace("base.apk", "../base.apk"),
            "unicode-name" => text.Replace("base.apk", "bаse.apk"),
            "name-too-long" => text.Replace("base.apk", new string('a', 125) + ".apk"),
            "bad-hash" => text.Replace(new string('a', 64), new string('A', 64)),
            "bad-source-id" => text.Replace(sources[0].Id, Guid.Empty.ToString("N")),
            "bad-request-id" => text.Replace(requestId, Guid.Empty.ToString("D")),
            "bad-serial" => text.Replace(Serial, "bad serial"),
            "negative-size" => text.Replace("\"sizeBytes\":3", "\"sizeBytes\":-1"),
            "oversize" => text.Replace("\"sizeBytes\":3", "\"sizeBytes\":1073741825"),
            "source-capacity" => Encoding.UTF8.GetString(SetRequest(FakeSources(3).Select(source => source with
                { SizeBytes = BrowserArtifactStore.MaximumFileBytes }).ToArray(), requestId: requestId)),
            _ => throw new InvalidOperationException()
        };
        var calls = 0;
        var commands = new BrowserCommands((command, _) => { calls++; return Task.FromResult(new OperatorExecutionResult(command)); });
        await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => commands.Get(requestId)).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void MaximumShapeUsesUtf8ByteBudgetWithoutWideningFilenameOrBodyBounds()
    {
        var sources = FakeSources(8).Select((source, index) => source with
        {
            FileName = index.ToString() + new string('a', 123) + ".apk", SizeBytes = 1073741824
        }).ToArray();
        // This intentionally overestimates the legal total, as in the accepted
        // plan fixture; it measures serialization, not source admission.
        var id = "11111111-1111-4111-8111-111111111111";
        var serial = new string('A', 256);
        var options = new ApkInstallOptions(false, false, false, false);
        var post = SetRequest(sources, options, id, serial);
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        { requestId = id, action = "apk.install-set", sessionId = id, serial, sources, options }, Json);
        Assert.Equal(2757, post.Length);
        Assert.Equal(2791, original.Length);
        Assert.Equal(post.Length, Encoding.UTF8.GetByteCount(Encoding.UTF8.GetString(post)));
        Assert.All(sources, source => Assert.Equal(128, BrowserArtifactStore.ValidateFileName(source.FileName).Length));
        var valid = SetRequest(FakeSources(8));
        Assert.Equal(8, BrowserApkSetCommands.Parse(valid).Sources.Count);
        Assert.Equal("body_size_invalid", Assert.Throws<BrowserRequestException>(() =>
            BrowserApkSetCommands.Parse(new byte[4097])).Code);
        Assert.Equal(4096, BrowserCommands.MaximumRequestBytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("name")]
    [InlineData("size")]
    [InlineData("hash")]
    public async Task PartialSourceAcquisitionUnwindsWithoutConsumingIdOrSharedGate(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        var invalid = sources.ToArray();
        invalid[1] = fault switch
        {
            "missing" => invalid[1] with { Id = Guid.NewGuid().ToString("N") },
            "name" => invalid[1] with { FileName = "other.apk" },
            "size" => invalid[1] with { SizeBytes = 1 },
            "hash" => invalid[1] with { Sha256 = new string('0', 64) },
            _ => throw new InvalidOperationException()
        };
        var gate = new BrowserOperationGate();
        var calls = 0;
        var runner = new BundleRunner();
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner));
        var commands = new BrowserCommands((command, cancellation) =>
        { calls++; return BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation); }, () => store, gate);
        var id = Guid.NewGuid().ToString("D");
        try
        {
            await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(SetRequest(invalid, requestId: id)));
            Assert.Equal(0, calls);
            Assert.False(gate.IsBusy);
            Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => commands.Get(id)).Code);
            Assert.Equal("completed", (await commands.ExecuteAsync(SetRequest(sources, requestId: id))).State);
            Assert.Equal(1, calls);
            await store.RemoveAsync(sources[0].Id);
            await store.RemoveAsync(sources[1].Id);
        }
        finally { Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    [Fact]
    public async Task DrainJoinsUnadmittedSourceAcquisitionAndBlocksItsLaterAdmission()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        var entered = Signal();
        var release = Signal();
        var capture = store.CaptureAsync("unrelated.apk", async (stream, _) =>
        { entered.SetResult(); await release.Task; await stream.WriteAsync(new byte[] { 9 }); });
        await entered.Task;
        var gate = new BrowserOperationGate();
        var commands = new BrowserCommands((command, _) => throw new InvalidOperationException("executor must not start"), () => store, gate);
        var request = commands.ExecuteAsync(SetRequest(sources));
        Assert.False(request.IsCompleted);
        Assert.False(gate.IsBusy);
        var drain = commands.DrainApkSetsAsync();
        Assert.False(drain.IsCompleted);
        release.SetResult();
        await capture;
        Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => request)).Code);
        await drain;
        Assert.False(gate.IsBusy);
        Assert.Equal(3, await store.CleanupAsync());
        Assert.True(await commands.CleanupApkSetWorkAsync());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    public async Task AllSixteenFlagCombinationsBindThroughRealCore(int flags)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        var runner = new BundleRunner();
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner));
        var commands = new BrowserCommands((command, cancellation) => BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation), () => store);
        var options = new ApkInstallOptions((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0);
        try
        {
            var outcome = await commands.ExecuteAsync(SetRequest(sources, options));
            Assert.Equal("completed", outcome.State);
            Assert.Equal(options, outcome.ApkSet!.Options);
            List<string> expected = ["-s", Serial, "install-multiple"];
            if (options.ReplaceExisting) expected.Add("-r");
            if (options.AllowDowngrade) expected.Add("-d");
            if (options.GrantRuntimePermissions) expected.Add("-g");
            if (options.AllowTestPackages) expected.Add("-t");
            var call = Assert.Single(runner.Calls, call => call.Contains("install-multiple"));
            Assert.Equal(expected, call.Take(expected.Count));
            Assert.Equal(2, call.Length - expected.Count);
        }
        finally { Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    [Fact]
    public async Task RealCorePmSuccessThenInventoryFailureRemainsUnknownWithoutInventingFacts()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        var runner = new BundleRunner { FailInventory = true };
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner));
        var commands = new BrowserCommands((command, cancellation) => BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation), () => store);
        try
        {
            var outcome = await commands.ExecuteAsync(SetRequest(sources));
            Assert.Equal("outcomeUnknown", outcome.State);
            Assert.True(outcome.ApkSet!.ExecutionStarted);
            Assert.Null(outcome.ApkSet.PackageManagerCompleted);
            Assert.Null(outcome.ApkSet.InventoryRefreshed);
            Assert.Null(outcome.Mutation);
            Assert.Equal("completed", outcome.ApkSet.SourceCleanup);
            var json = JsonSerializer.Serialize(outcome, Json);
            Assert.DoesNotContain("dispatched", json);
            Assert.DoesNotContain("private-output-marker", json);
            Assert.Single(runner.Calls, call => call.Contains("install-multiple"));
        }
        finally { Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    [Fact]
    public async Task HostCancellationAndAbandonedWaitRetainGateSourcesAndCopiesUntilActualSettlement()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        using var stopping = new CancellationTokenSource();
        var sources = await StagePair(store);
        var entered = Signal(); var cancelled = Signal(); var processExit = Signal(); var readersDrained = Signal();
        var gate = new BrowserOperationGate();
        string[] copyPaths = [];
        var commands = new BrowserCommands(async (command, cancellation) =>
        {
            Assert.Equal(stopping.Token, cancellation);
            copyPaths = command.ApkBundle!.ApkPaths.ToArray();
            using var registration = cancellation.Register(() => cancelled.TrySetResult());
            entered.SetResult();
            await processExit.Task;
            await readersDrained.Task;
            cancellation.ThrowIfCancellationRequested();
            return new OperatorExecutionResult(command);
        }, () => store, gate);
        var body = SetRequest(sources);
        var id = BrowserApkSetCommands.Parse(body).RequestId;
        var task = commands.ExecuteAsync(body, stopping.Token);
        await entered.Task;
        using var abandoned = new CancellationTokenSource(); abandoned.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(abandoned.Token));
        var drain = commands.DrainApkSetsAsync();
        stopping.Cancel(); await cancelled.Task;
        Assert.True(gate.IsBusy); Assert.False(task.IsCompleted); Assert.False(drain.IsCompleted);
        Assert.True(commands.Get(id).ApkSet!.ExecutionStarted);
        Assert.Equal("held", commands.Get(id).ApkSet!.SourceCleanup);
        Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => store.RemoveAsync(sources[0].Id))).Code);
        Assert.All(copyPaths, path => Assert.Throws<IOException>(() => File.Delete(path)));
        processExit.SetResult();
        Assert.False(task.IsCompleted); Assert.False(drain.IsCompleted); Assert.True(gate.IsBusy);
        readersDrained.SetResult();
        Assert.Equal("outcomeUnknown", (await task).State);
        await drain;
        Assert.False(gate.IsBusy);
        Assert.All(copyPaths, path => Assert.False(File.Exists(path)));
        Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(body))).Code);
        Assert.Equal(2, await store.CleanupAsync());
        Assert.True(await commands.CleanupApkSetWorkAsync());
    }

    [Theory]
    [InlineData("command-kind")]
    [InlineData("command-serial")]
    [InlineData("command-options")]
    [InlineData("command-folder")]
    [InlineData("missing-bundle-result")]
    [InlineData("bundle-membership")]
    [InlineData("bundle-order")]
    [InlineData("different-common-result")]
    [InlineData("native-exit")]
    [InlineData("native-arguments")]
    [InlineData("receipt-missing")]
    [InlineData("receipt-id")]
    [InlineData("receipt-kind")]
    [InlineData("receipt-target")]
    [InlineData("receipt-desired")]
    [InlineData("receipt-stage")]
    [InlineData("receipt-readback")]
    [InlineData("receipt-history")]
    [InlineData("receipt-order")]
    [InlineData("receipt-time")]
    [InlineData("receipt-id-case")]
    [InlineData("receipt-message-null")]
    [InlineData("receipt-observed-null")]
    [InlineData("receipt-final-stage")]
    public async Task MismatchedActualCoreBindingsRemainUnknownAndNeverLeakRawProofs(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        string sourceRoot;
        await using (var source = await store.AcquireAsync(sources[0].Id, sources[0].Sha256, sources[0].SizeBytes))
            sourceRoot = Path.GetDirectoryName(source.Path)!;
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", new BundleRunner()));
        var commands = new BrowserCommands(async (command, cancellation) =>
        {
            var result = await BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation);
            var installed = result.ApkBundleInstallResult!;
            var receipt = result.MutationReceipt!;
            var alteredDispatch = installed.CommandResult with
            { Arguments = fault == "native-exit" ? installed.CommandResult.Arguments : ["private-output-marker"],
                StandardOutput = "private-output-marker", ExitCode = fault == "native-exit" ? 1 : 0 };
            return fault switch
            {
                "command-kind" => result with { Command = OperatorCommands.DiscoverDevices() },
                "command-serial" => result with { Command = OperatorCommands.InstallApkBundle("QUEST456", command.LocalPath!, command.InstallOptions) },
                "command-options" => result with { Command = OperatorCommands.InstallApkBundle(Serial, command.LocalPath!, new ApkInstallOptions(false, true, true, true)) },
                // Only construct this adversarial returned snapshot; never execute
                // a command against the shared upload root.
                "command-folder" => result with { Command = OperatorCommands.InstallApkBundle(Serial, sourceRoot, command.InstallOptions) },
                "missing-bundle-result" => result with { ApkBundleInstallResult = null },
                "bundle-membership" => result with { ApkBundleInstallResult = installed with { ApkPaths = [installed.ApkPaths[0]] } },
                "bundle-order" => result with { ApkBundleInstallResult = installed with { ApkPaths = installed.ApkPaths.Reverse().ToArray() } },
                "different-common-result" => result with { CommandResult = installed.CommandResult with { } },
                "native-exit" or "native-arguments" => result with
                { CommandResult = alteredDispatch, ApkBundleInstallResult = installed with { CommandResult = alteredDispatch } },
                "receipt-missing" => result with { MutationReceipt = null },
                "receipt-id" => result with { MutationReceipt = receipt with { OperationId = "pc-" + Guid.Empty.ToString("N") } },
                "receipt-kind" => result with { MutationReceipt = receipt with { CommandKind = OperatorCommandKind.InstallApk } },
                "receipt-target" => result with { MutationReceipt = receipt with { Target = "QUEST456" } },
                "receipt-desired" => result with { MutationReceipt = receipt with { DesiredState = "private-output-marker" } },
                "receipt-stage" => result with { MutationReceipt = receipt with { Stage = OperatorMutationStage.Pending } },
                "receipt-readback" => result with { MutationReceipt = receipt with { HeadsetReadback = false } },
                "receipt-history" => result with { MutationReceipt = receipt with { Transitions = receipt.Transitions.Take(2).ToArray() } },
                "receipt-order" => result with { MutationReceipt = receipt with { Transitions = receipt.Transitions.Reverse().ToArray() } },
                "receipt-time" => result with { MutationReceipt = receipt with
                { Transitions = receipt.Transitions.Select(transition => transition with { At = default }).ToArray() } },
                "receipt-id-case" => result with { MutationReceipt = receipt with { OperationId = "pc-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" } },
                "receipt-message-null" => result with { MutationReceipt = receipt with
                { Transitions = receipt.Transitions.Select(transition => transition with { Message = null! }).ToArray() } },
                "receipt-observed-null" => result with { MutationReceipt = receipt with { ObservedState = null! } },
                "receipt-final-stage" => result with { MutationReceipt = receipt with
                { Transitions = receipt.Transitions.Select((transition, index) => index == 2 ? transition with { Stage = OperatorMutationStage.Pending } : transition).ToArray() } },
                _ => throw new InvalidOperationException()
            };
        }, () => store);
        try
        {
            var outcome = await commands.ExecuteAsync(SetRequest(sources));
            Assert.Equal("outcomeUnknown", outcome.State);
            Assert.Equal("apk_set_binding_mismatch", outcome.Error);
            Assert.True(outcome.ApkSet!.ExecutionStarted);
            Assert.Null(outcome.ApkSet.PackageManagerCompleted);
            Assert.Null(outcome.ApkSet.InventoryRefreshed);
            Assert.Null(outcome.Mutation);
            Assert.Null(outcome.Result);
            Assert.DoesNotContain("private-output-marker", JsonSerializer.Serialize(outcome, Json));
        }
        finally { Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupDebtPreservesDeviceEvidenceButReleasesSharedGateForUnrelatedActions(bool unknown)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", new BundleRunner()));
        var gate = new BrowserOperationGate();
        FileStream? externalRead = null;
        var commands = new BrowserCommands(async (command, cancellation) =>
        {
            var result = await BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation);
            if (command.Kind != OperatorCommandKind.InstallApkBundle) return result;
            externalRead = File.Open(command.ApkBundle!.ApkPaths[0], FileMode.Open, FileAccess.Read, FileShare.Read);
            if (unknown) throw new IOException("private-output-marker");
            return result;
        }, () => store, gate);
        var body = SetRequest(sources);
        try
        {
            var outcome = await commands.ExecuteAsync(body);
            Assert.Equal(unknown ? "outcomeUnknown" : "completed", outcome.State);
            Assert.Equal("pending", outcome.ApkSet!.SourceCleanup);
            Assert.True(outcome.ApkSet.ExecutionStarted);
            Assert.Equal(unknown ? (bool?)null : true, outcome.ApkSet.PackageManagerCompleted);
            Assert.Equal(unknown ? (bool?)null : true, outcome.ApkSet.InventoryRefreshed);
            Assert.Equal(!unknown, outcome.Mutation is not null);
            Assert.True(commands.ApkSetCapability.WorkingCopyCleanupPending);
            Assert.False(gate.IsBusy);
            Assert.Equal("apk_set_work_cleanup_pending", (await Assert.ThrowsAsync<BrowserRequestException>(() =>
                commands.ExecuteAsync(SetRequest(sources)))).Code);
            var read = JsonSerializer.SerializeToUtf8Bytes(new { requestId = Guid.NewGuid().ToString("D"), action = "devices" });
            Assert.Equal("completed", (await commands.ExecuteAsync(read)).State);
            // Browser-global unknown custody is a UI policy; the broker's shared
            // native gate always reflects actual process settlement, not cleanup.
            Assert.Equal(outcome, commands.Get(outcome.RequestId));
            Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(body))).Code);
        }
        finally
        {
            externalRead?.Dispose();
            Assert.True(await commands.CleanupApkSetWorkAsync());
            Assert.False(commands.ApkSetCapability.WorkingCopyCleanupPending);
        }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("hash")]
    [InlineData("second-source-closed")]
    public async Task ExactCopyBoundsAndPartialCreationUnwindBeforeAnyCoreEntry(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipts = await StagePair(store);
        await using var first = await store.AcquireAsync(receipts[0].Id, receipts[0].Sha256, receipts[0].SizeBytes);
        await using var second = await store.AcquireAsync(receipts[1].Id, receipts[1].Sha256, receipts[1].SizeBytes);
        var changed = first.Receipt with
        {
            SizeBytes = fault == "short" ? 4 : fault == "extra" ? 2 : first.Receipt.SizeBytes,
            Sha256 = fault == "hash" ? new string('0', 64) : first.Receipt.Sha256
        };
        await using var adversarial = new BrowserArtifactLease(changed, first.Path,
            BrowserArtifactFileSafety.OpenRead(first.Path), () => { });
        var workStore = new BrowserApkSetWorkStore();
        var work = workStore.Reserve(changed.SizeBytes + second.Receipt.SizeBytes);
        if (fault == "second-source-closed") second.Dispose();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => work.PrepareAsync([adversarial, second], CancellationToken.None));
            Assert.True(work.Cleanup());
            Assert.False(workStore.CleanupPending);
        }
        finally { Assert.True(workStore.Cleanup()); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCorePreparationFailureHasTerminalNonexecutionAndIndependentCleanupDebt(bool cleanupFails)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        var allowCleanup = !cleanupFails;
        var workStore = new BrowserApkSetWorkStore(beforeSnapshot: _ => throw new IOException("private-output-marker"),
            beforeCleanup: _ => { if (!allowCleanup) throw new IOException("private-output-marker"); });
        var gate = new BrowserOperationGate();
        var calls = 0;
        var commands = new BrowserCommands((command, _) =>
        { calls++; return Task.FromResult(new OperatorExecutionResult(command)); }, store, workStore, gate);
        var body = SetRequest(sources);
        try
        {
            var outcome = await commands.ExecuteAsync(body);
            Assert.Equal("preparationFailed", outcome.State);
            Assert.False(outcome.ApkSet!.ExecutionStarted);
            Assert.Null(outcome.ApkSet.PackageManagerCompleted);
            Assert.Null(outcome.ApkSet.InventoryRefreshed);
            Assert.Null(outcome.Mutation);
            Assert.Equal(cleanupFails ? "pending" : "completed", outcome.ApkSet.SourceCleanup);
            Assert.Equal(cleanupFails, commands.ApkSetCapability.WorkingCopyCleanupPending);
            Assert.Equal(cleanupFails ? 5 : 0, workStore.ReservedBytes);
            Assert.False(gate.IsBusy);
            Assert.Equal(0, calls);
            Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(body))).Code);
            if (cleanupFails)
                Assert.Equal("apk_set_work_cleanup_pending", (await Assert.ThrowsAsync<BrowserRequestException>(() =>
                    commands.ExecuteAsync(SetRequest(sources)))).Code);
            var unrelated = JsonSerializer.SerializeToUtf8Bytes(new { requestId = Guid.NewGuid().ToString("D"), action = "devices" });
            Assert.Equal("completed", (await commands.ExecuteAsync(unrelated)).State);
            Assert.Equal(1, calls);
            Assert.DoesNotContain("private-output-marker", JsonSerializer.Serialize(outcome, Json));
        }
        finally
        {
            allowCleanup = true;
            Assert.True(await commands.CleanupApkSetWorkAsync());
            Assert.Equal(0, workStore.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("unexpected-file")]
    [InlineData("unexpected-directory")]
    [InlineData("existing-selected-name")]
    public async Task SnapshotAndCreateNewFailuresNeverDeleteUnexpectedObjects(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        string? unexpected = null;
        var workStore = new BrowserApkSetWorkStore(
            beforeCopy: fault == "existing-selected-name" ? folder =>
            { unexpected = Path.Combine(folder, "base.apk"); File.WriteAllBytes(unexpected, [9]); } : null,
            beforeSnapshot: fault != "existing-selected-name" ? folder =>
            {
                unexpected = Path.Combine(folder, fault == "unexpected-file" ? "unrelated.apk" : "unrelated");
                if (fault == "unexpected-file") File.WriteAllBytes(unexpected, [9]);
                else Directory.CreateDirectory(unexpected);
            } : null);
        var calls = 0;
        var commands = new BrowserCommands((command, _) =>
        { calls++; return Task.FromResult(new OperatorExecutionResult(command)); }, store, workStore);
        try
        {
            var outcome = await commands.ExecuteAsync(SetRequest(sources));
            Assert.Equal("preparationFailed", outcome.State);
            Assert.False(outcome.ApkSet!.ExecutionStarted);
            Assert.Equal("pending", outcome.ApkSet.SourceCleanup);
            Assert.Equal(0, calls);
            Assert.NotNull(unexpected);
            Assert.True(fault == "unexpected-directory" ? Directory.Exists(unexpected) : File.Exists(unexpected));
            Assert.Equal(5, workStore.ReservedBytes);
        }
        finally
        {
            // Only the test-created object is removed. The work owner must retry
            // its own identity-checked objects and must never adopt this child.
            if (unexpected is not null)
            {
                if (fault == "unexpected-directory") Directory.Delete(unexpected);
                else File.Delete(unexpected);
            }
            Assert.True(await commands.CleanupApkSetWorkAsync());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MarkerRollbackTokensStayOwnedChargedAndRetryExactCleanup(int failedMarker)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        FileStream? debtHandle = null;
        var calls = 0; var retries = 0; var permitCleanup = false;
        var token = Guid.NewGuid().ToString("N");
        var workStore = new BrowserApkSetWorkStore(createMarker: folder =>
        {
            var marker = BrowserArtifactFileSafety.CreateRootMarker(folder);
            if (++calls != failedMarker) return marker;
            debtHandle = marker;
            // Model the existing primitive's exact retained-handle debt contract.
            throw new BrowserArtifactException("artifact_create_cleanup_pending", token);
        }, cleanupCreationDebt: id =>
        {
            Assert.Equal(token, id); retries++;
            if (!permitCleanup) return false;
            BrowserArtifactFileSafety.DeleteCreatedFile(debtHandle!); debtHandle = null;
            return true;
        });
        var commands = new BrowserCommands((command, _) => throw new InvalidOperationException("Core must not start"), store, workStore);
        try
        {
            var outcome = await commands.ExecuteAsync(SetRequest(sources));
            Assert.Equal("preparationFailed", outcome.State);
            Assert.False(outcome.ApkSet!.ExecutionStarted);
            Assert.Equal("pending", outcome.ApkSet.SourceCleanup);
            Assert.True(commands.ApkSetCapability.WorkingCopyCleanupPending);
            Assert.Equal(5, workStore.ReservedBytes);
            Assert.True(retries > 0);
            Assert.False(await commands.CleanupApkSetWorkAsync());
            Assert.Equal(5, workStore.ReservedBytes);
        }
        finally
        {
            permitCleanup = true;
            Assert.True(await commands.CleanupApkSetWorkAsync());
            Assert.Null(debtHandle);
            Assert.Equal(0, workStore.ReservedBytes);
        }
    }

    [Fact]
    public void WorkReservationsAreAtomicBoundedAndIndependentOfTheUploadQuota()
    {
        var workStore = new BrowserApkSetWorkStore(maximumBytes: 5);
        var first = workStore.Reserve(3);
        var second = workStore.Reserve(2);
        Assert.Equal(5, workStore.ReservedBytes);
        Assert.Equal("apk_set_work_capacity", Assert.Throws<BrowserRequestException>(() => workStore.Reserve(1)).Code);
        Assert.True(first.Cleanup());
        Assert.Equal(2, workStore.ReservedBytes);
        var third = workStore.Reserve(3);
        Assert.Equal(5, workStore.ReservedBytes);
        Assert.True(second.Cleanup()); Assert.True(third.Cleanup()); Assert.True(workStore.Cleanup());
        Assert.Equal(2L * 1024 * 1024 * 1024, BrowserArtifactStore.MaximumTotalBytes);
        Assert.Equal(2L * 1024 * 1024 * 1024, BrowserApkSetWorkStore.MaximumBytes);
    }

    [Fact]
    public async Task RacingWorkReservationsNeverExceedTheAdvertisedBudget()
    {
        var workStore = new BrowserApkSetWorkStore(maximumBytes: 5);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            try { return workStore.Reserve(3); }
            catch (BrowserRequestException exception) { Assert.Equal("apk_set_work_capacity", exception.Code); return null; }
        })));
        var admitted = Assert.Single(attempts, work => work is not null);
        Assert.Equal(3, workStore.ReservedBytes);
        Assert.True(admitted!.Cleanup()); Assert.True(workStore.Cleanup());
    }

    [Fact]
    public void CapabilityIsPureAndAdvertisesBothSeparateProcessLocalBudgets()
    {
        var calls = 0;
        var commands = new BrowserCommands((command, _) => throw new InvalidOperationException(), () =>
        { calls++; throw new InvalidOperationException("session must not initialize staging"); });
        var capability = commands.ApkSetCapability;
        Assert.Equal(OperatingSystem.IsWindows(), capability.Supported);
        Assert.False(capability.WorkingCopyCleanupPending);
        Assert.Equal(8, capability.MaximumParts);
        Assert.Equal(1073741824L, capability.MaximumFileBytes);
        Assert.Equal(2147483648L, capability.MaximumSourceBytes);
        Assert.Equal(2147483648L, capability.MaximumWorkingCopyBytes);
        Assert.Equal(4294967296L, capability.MaximumSourceAndWorkingBytes);
        Assert.Equal(1, capability.MaximumConcurrentOperations);
        Assert.Equal(OperatingSystem.IsWindows(), commands.AvailableActions.Contains("apk.install-set"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EightSelectedPartsFitExistingUploadPoolAndOneCoreSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        List<BrowserArtifactReceipt> sources = [.. await StagePair(store)];
        for (var index = 2; index < 8; index++)
            sources.Add(await store.StageAsync($"z-part{index}.apk", new MemoryStream([(byte)index])));
        var runner = new BundleRunner();
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner));
        var commands = new BrowserCommands((command, cancellation) => BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation), () => store);
        try
        {
            var outcome = await commands.ExecuteAsync(SetRequest(sources.AsEnumerable().Reverse().ToArray()));
            Assert.Equal("completed", outcome.State);
            Assert.Equal(8, outcome.ApkSet!.PartCount);
            var dispatch = Assert.Single(runner.Calls, call => call.Contains("install-multiple"));
            Assert.Equal(sources.Select(source => source.FileName), dispatch.Skip(4).Select(Path.GetFileName));
            Assert.Equal(12, dispatch.Length);
            Assert.Equal(8, await store.CleanupAsync());
        }
        finally { Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    [Fact]
    public async Task OneHostReusesItsOwnedRootAcrossChangingDirectoryIndexSizes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        List<BrowserArtifactReceipt> sources = [.. await StagePair(store)];
        for (var index = 2; index < 8; index++)
            sources.Add(await store.StageAsync($"z-part{index}.apk", new MemoryStream([(byte)index])));
        var runner = new BundleRunner();
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner));
        var commands = new BrowserCommands((command, cancellation) => BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation), () => store);
        try
        {
            foreach (var count in new[] { 2, 8, 2, 8 })
                Assert.Equal("completed", (await commands.ExecuteAsync(SetRequest(sources.Take(count).ToArray()))).State);
            Assert.Equal(4, runner.Calls.Count(call => call.Contains("install-multiple")));
            Assert.False(commands.ApkSetCapability.WorkingCopyCleanupPending);
        }
        finally { Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    [Fact]
    public async Task WorkCapacityRejectionIsPreAdmissionAndUnwindsSourcesAtTheExactTinyBoundary()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        var workStore = new BrowserApkSetWorkStore(maximumBytes: 4);
        var gate = new BrowserOperationGate();
        var commands = new BrowserCommands((command, _) => throw new InvalidOperationException(), store, workStore, gate);
        var id = Guid.NewGuid().ToString("D");
        try
        {
            Assert.Equal("apk_set_work_capacity", (await Assert.ThrowsAsync<BrowserRequestException>(() =>
                commands.ExecuteAsync(SetRequest(sources, requestId: id)))).Code);
            Assert.False(gate.IsBusy);
            Assert.Equal(0, workStore.ReservedBytes);
            Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => commands.Get(id)).Code);
            Assert.Equal(2, await store.CleanupAsync());
        }
        finally { Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    [Fact]
    public async Task CachedProofIsClosedAndSourcesAreReadOnlySnapshotsOfTheOriginal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", new BundleRunner()));
        OperatorMutationReceipt? actualReceipt = null;
        var commands = new BrowserCommands(async (command, cancellation) =>
        {
            var result = await BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation);
            actualReceipt = result.MutationReceipt;
            return result with { MutationReceipt = result.MutationReceipt! with
            {
                ObservedState = "private-output-marker /private/path",
                Transitions = result.MutationReceipt!.Transitions.Select(transition => transition with
                { Message = "private-output-marker /private/path" }).ToArray()
            } };
        }, () => store);
        try
        {
            var body = SetRequest(sources);
            var outcome = await commands.ExecuteAsync(body);
            Assert.Equal("completed", outcome.State);
            Array.Clear(sources);
            Assert.Equal(2, outcome.ApkSet!.Sources.Count);
            Assert.All(outcome.ApkSet.Sources, source => Assert.NotNull(source));
            Assert.Throws<NotSupportedException>(() => ((IList<BrowserArtifactReceipt>)outcome.ApkSet.Sources)[0] = FakeSources()[0]);
            Assert.Equal(actualReceipt!.OperationId, outcome.Mutation!.OperationId);
            Assert.Equal(new[] { "apk-set-sent", "apk-set-pending", "apk-set-confirmed" },
                outcome.Mutation.Transitions.Select(transition => transition.Message));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(outcome, Json);
            using var document = JsonDocument.Parse(bytes);
            Assert.Equal(new[] { "contractVersion", "serial", "sources", "partCount", "options", "executionStarted",
                "packageManagerCompleted", "inventoryRefreshed", "sourceCleanup" }.Order(),
                document.RootElement.GetProperty("apkSet").EnumerateObject().Select(property => property.Name).Order());
            Assert.DoesNotContain("private-output-marker", Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain("/private/path", Encoding.UTF8.GetString(bytes));
            Assert.All(document.RootElement.EnumerateObject().Where(property => property.Name is not
                ("requestId" or "action" or "state" or "mutation" or "apkSet")), property => Assert.Equal(JsonValueKind.Null, property.Value.ValueKind));
        }
        finally { Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    [Fact]
    public async Task TransientInitialCopyIdentityFailureRetriesTheRetainedCreationHandleForExactCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var sources = await StagePair(store);
        var identityReads = 0;
        var workStore = new BrowserApkSetWorkStore(readCopyIdentity: file =>
        {
            if (++identityReads == 1) throw new BrowserArtifactException("artifact_identity_unavailable");
            return BrowserArtifactFileSafety.ReadIdentity(file.SafeFileHandle);
        });
        var commands = new BrowserCommands((command, _) => throw new InvalidOperationException("Core must not start"), store, workStore);
        try
        {
            var outcome = await commands.ExecuteAsync(SetRequest(sources));
            Assert.Equal("preparationFailed", outcome.State);
            Assert.False(outcome.ApkSet!.ExecutionStarted);
            Assert.Equal("completed", outcome.ApkSet.SourceCleanup);
            Assert.Equal(2, identityReads);
            Assert.Equal(0, workStore.ReservedBytes);
            Assert.False(commands.ApkSetCapability.WorkingCopyCleanupPending);
            Assert.Equal(2, await store.CleanupAsync());
        }
        finally { Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    [Fact]
    public async Task TwoPartTracerUsesOneRealCoreInstallMultipleAndClosedBoundCompletion()
    {
        // Protected staging deliberately has no advisory-lock substitute on Unix.
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var baseApk = await store.StageAsync("base.apk", new MemoryStream([1, 2, 3]));
        var splitApk = await store.StageAsync("split_config.arm64_v8a.apk", new MemoryStream([4, 5]));
        var unrelated = await store.StageAsync("unrelated.apk", new MemoryStream([9]));
        string sourceRoot;
        await using (var lease = await store.AcquireAsync(baseApk.Id, baseApk.Sha256, baseApk.SizeBytes))
            sourceRoot = Path.GetDirectoryName(lease.Path)!;
        var runner = new BundleRunner();
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner));
        OperatorExecutionResult? actual = null;
        var commands = new BrowserCommands(async (command, cancellation) =>
        {
            actual = await BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation);
            return actual;
        }, () => store);
        var requestId = Guid.NewGuid().ToString("D");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            requestId, action = "apk.install-set", serial = Serial,
            sources = new[] { splitApk, baseApk },
            options = new { replaceExisting = true, allowDowngrade = false,
                grantRuntimePermissions = false, allowTestPackages = false }, confirmed = true
        }, Json);

        try
        {
        var outcome = await commands.ExecuteAsync(body);

        Assert.Equal("completed", outcome.State);
        Assert.Null(outcome.Result);
        Assert.Equal(outcome, commands.Get(requestId));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(outcome, Json));
        var proof = document.RootElement.GetProperty("apkSet");
        Assert.Equal("questionable.file_manager.browser_apk_set.v1", proof.GetProperty("contractVersion").GetString());
        Assert.Equal(Serial, proof.GetProperty("serial").GetString());
        Assert.Equal(2, proof.GetProperty("partCount").GetInt32());
        Assert.True(proof.GetProperty("executionStarted").GetBoolean());
        Assert.True(proof.GetProperty("packageManagerCompleted").GetBoolean());
        Assert.True(proof.GetProperty("inventoryRefreshed").GetBoolean());
        Assert.Equal("completed", proof.GetProperty("sourceCleanup").GetString());
        Assert.Equal(new[] { baseApk.Id, splitApk.Id }.Order(), proof.GetProperty("sources")
            .EnumerateArray().Select(source => source.GetProperty("id").GetString()).Order());
        Assert.Equal(OperatorMutationStage.Confirmed, outcome.Mutation!.Stage);
        Assert.Equal("apk-set-install", outcome.Mutation.DesiredState);
        Assert.Equal(actual!.MutationReceipt!.OperationId, outcome.Mutation.OperationId);
        Assert.Equal(actual.MutationReceipt.Transitions.Select(transition => (transition.Stage, transition.At)),
            outcome.Mutation.Transitions.Select(transition => (transition.Stage, transition.At)));
        var install = Assert.Single(runner.Calls, call => call.Contains("install-multiple"));
        Assert.Equal(new[] { "-s", Serial, "install-multiple", "-r" }, install.Take(4));
        Assert.Equal(new[] { "base.apk", "split_config.arm64_v8a.apk" }, install.Skip(4).Select(Path.GetFileName));
        Assert.All(install.Skip(4), path => Assert.NotEqual(sourceRoot, Path.GetDirectoryName(path)));
        Assert.All(install.Skip(4), path => Assert.False(File.Exists(path)));
        await using var unrelatedLease = await store.AcquireAsync(unrelated.Id, unrelated.Sha256, unrelated.SizeBytes);
        Assert.DoesNotContain("private-output-marker", document.RootElement.GetRawText());
        Assert.DoesNotContain(install[4], document.RootElement.GetRawText());
        }
        finally { Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static BrowserArtifactReceipt[] FakeSources(int count = 2) => Enumerable.Range(0, count)
        .Select(index => new BrowserArtifactReceipt((index + 1).ToString("x32"), index == 0 ? "base.apk" : $"split{index}.apk", 3, new string('a', 64)))
        .ToArray();

    private static async Task<BrowserArtifactReceipt[]> StagePair(BrowserArtifactStore store) =>
        [await store.StageAsync("base.apk", new MemoryStream([1, 2, 3])),
         await store.StageAsync("split_config.arm64_v8a.apk", new MemoryStream([4, 5]))];

    private static byte[] SetRequest(IReadOnlyList<BrowserArtifactReceipt> sources, ApkInstallOptions? options = null,
        string? requestId = null, string serial = Serial) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            requestId = requestId ?? Guid.NewGuid().ToString("D"), action = "apk.install-set", serial, sources,
            options = options ?? new ApkInstallOptions(), confirmed = true
        }, Json);

    private sealed class BundleRunner : ICommandRunner
    {
        internal bool FailInventory { get; init; }
        public List<string[]> Calls { get; } = [];
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            string output;
            if (arguments.SequenceEqual(["devices", "-l"]))
                output = "List of devices attached\nQUEST123 device product:hollywood model:Quest_2 device:hollywood\n";
            else if (arguments.Count >= 3 && arguments[2] == "install-multiple")
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
                var paths = arguments.Skip(3).SkipWhile(argument => argument.StartsWith('-')).ToArray();
                Assert.All(paths, path => Assert.True(File.Exists(path)));
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(paths[0]));
                Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(paths[1]));
                Assert.All(paths, path => Assert.Throws<IOException>(() =>
                    File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read).Dispose()));
                output = "Success\nprivate-output-marker";
            }
            else if (arguments.SequenceEqual(["-s", Serial, "shell", "pm list packages -3"]))
            {
                if (FailInventory) throw new IOException("private-output-marker inventory failed");
                output = "package:com.example.app\n";
            }
            else throw new InvalidOperationException("Unexpected fake runner command");
            return Task.FromResult(new CommandResult(fileName, arguments.ToArray(), 0, output, "", TimeSpan.Zero));
        }
    }
}
