using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LocalApiV3LaunchRegistryTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task ActualOwnedLaunchPublishesProofAndExactArchiveAndRestartDoesNotReplay()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        var ready = await fixture.Preflight();
        Assert.Equal("apk.launch-diagnose", ready.Command);
        Assert.False(Proof(fixture.Status(ready)).GetProperty("launchMayHaveDispatched").GetBoolean());
        var status = await fixture.Registry.ExecuteV3Async(Binding(ready));
        Assert.Equal(LocalApiOperationStage.Completed, status.Stage);
        Assert.True(status.Output.Available); Assert.Null(status.DiagnosticSummary);
        Assert.Equal("completed", Proof(status).GetProperty("launchSummary").GetProperty("disposition").GetString());
        Assert.Equal(OperatorMutationStage.Confirmed, Mutation(status).Stage);
        Assert.True(Mutation(status).HeadsetReadback);
        Assert.Equal(1, fixture.Runner.ArmCount); Assert.Equal(1, fixture.Runner.LaunchCount);
        status.Validate();
        var archive = status.Output.Archive!;
        fixture.Reopen();
        var restored = fixture.Status(ready); restored.Validate();
        Assert.Equal(JsonSerializer.Serialize(status, Json), JsonSerializer.Serialize(restored, Json));
        using var read = await fixture.Registry.AcquireDiagnosticOutputAsync(ready.OperationId, ready.CommandDigest,
            archive.OutputId, archive.SizeBytes, archive.Sha256);
        Assert.Equal(archive.SizeBytes, read.Stream.Length);
        Assert.Equal(1, fixture.Runner.ArmCount); Assert.Equal(1, fixture.Runner.LaunchCount);
        Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV3Async(Binding(ready)))).Code);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("post-pid-failure")]
    public async Task BoundUnconfirmedLaunchCanPublishArchiveWithoutResolvingOriginal(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight(); fixture.Runner.Mode = mode;
        var status = await fixture.Registry.ExecuteV3Async(Binding(ready));
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, status.Stage);
        Assert.True(status.Output.Available); Assert.Equal(OperatorMutationStage.Pending, Mutation(status).Stage);
        Assert.True(Proof(status).GetProperty("launchMayHaveDispatched").GetBoolean()); status.Validate();
        var archive = status.Output.Archive!;
        fixture.Clock.Now = fixture.Clock.Now.AddDays(30); fixture.Reopen();
        var restored = fixture.Status(ready); Assert.Equal(status.Stage, restored.Stage); Assert.True(restored.Output.Available);
        using var read = await fixture.Registry.AcquireDiagnosticOutputAsync(ready.OperationId, ready.CommandDigest,
            archive.OutputId, archive.SizeBytes, archive.Sha256);
        Assert.Equal(archive.SizeBytes, read.Stream.Length);
        Assert.Equal(1, fixture.Runner.ArmCount); Assert.Equal(1, fixture.Runner.LaunchCount);
    }

    [Theory]
    [InlineData("before_save")]
    [InlineData("after_journal")]
    public async Task FailedDurableLaunchMarkerCannotArmOrLaunchAndRestartCannotReplay(string phase)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight(); var failed = false;
        fixture.JournalFault = value =>
        {
            if (failed || value != phase) return false;
            var metadata = fixture.RowJson().GetProperty("diagnostic");
            return failed = phase == "after_journal" ? metadata.GetProperty("launchMayHaveDispatched").GetBoolean() :
                metadata.GetProperty("outputStarted").GetBoolean() && !metadata.GetProperty("launchMayHaveDispatched").GetBoolean();
        };
        var status = await fixture.Registry.ExecuteV3Async(Binding(ready));
        Assert.True(failed); Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, status.Stage);
        Assert.False(status.Output.Available); Assert.Equal(0, fixture.Runner.ArmCount); Assert.Equal(0, fixture.Runner.LaunchCount);
        fixture.JournalFault = null; fixture.Reopen();
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, fixture.Status(ready).Stage);
        Assert.Equal(0, fixture.Runner.ArmCount); Assert.Equal(0, fixture.Runner.LaunchCount);
    }

    [Fact]
    public async Task CancellationRetainsActualCaptureOwnerAndCannotAcknowledgeNoLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight(); fixture.Runner.Mode = "held-capture";
        var pending = fixture.Registry.ExecuteV3Async(Binding(ready));
        try
        {
            await fixture.Runner.CaptureHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(Proof(fixture.Status(ready)).GetProperty("launchMayHaveDispatched").GetBoolean());
            Assert.Equal(LocalApiOperationStage.CancellationRequested, fixture.Registry.CancelV3(Binding(ready)).Stage);
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, fixture.Runner.LaunchCount);
        }
        finally { fixture.Runner.ReleaseCapture.TrySetResult(); }
        var status = await pending;
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, status.Stage);
        Assert.True(Proof(status).GetProperty("launchMayHaveDispatched").GetBoolean());
        Assert.True(fixture.Runner.CaptureSettled); Assert.False(status.Output.Available);
    }

    [Fact]
    public async Task RestoredReadyLaunchApprovalStaysInertUntilExplicitExecute()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight();
        var discovered = fixture.Runner.DiscoveryCount; fixture.Reopen();
        Assert.Equal(discovered, fixture.Runner.DiscoveryCount); Assert.Equal(0, fixture.Runner.ArmCount);
        Assert.Equal(LocalApiOperationStage.Preflighted, fixture.Status(ready).Stage);
        Assert.Equal(LocalApiOperationStage.Completed, (await fixture.Registry.ExecuteV3Async(Binding(ready))).Stage);
        Assert.Equal(1, fixture.Runner.LaunchCount);
    }

    [Theory]
    [InlineData("before_save")]
    [InlineData("after_journal")]
    public async Task FailedPublicationNeverReappearsAfterAnotherSaveOrRestart(string phase)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight(); var failed = false;
        fixture.JournalFault = value =>
        {
            if (failed || value != phase) return false;
            var metadata = fixture.RowJson().GetProperty("diagnostic");
            return failed = phase == "after_journal" ? metadata.GetProperty("archive").ValueKind != JsonValueKind.Null :
                metadata.GetProperty("resourcePhase").GetString() == "sealed";
        };
        var status = await fixture.Registry.ExecuteV3Async(Binding(ready));
        Assert.True(failed); Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, status.Stage);
        Assert.False(status.Output.Available); Assert.Null(status.Output.Archive);
        fixture.JournalFault = null;
        await fixture.Preflight();
        Assert.False(fixture.Status(ready).Output.Available); fixture.Reopen();
        Assert.False(fixture.Status(ready).Output.Available);
        Assert.Equal(1, fixture.Runner.ArmCount); Assert.Equal(1, fixture.Runner.LaunchCount);
    }

    [Fact]
    public async Task LostArchiveCustodyPreservesAcknowledgedActualLaunchProof()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight();
        var published = await fixture.Registry.ExecuteV3Async(Binding(ready)); var descriptor = published.Output.Archive!;
        fixture.Registry.Dispose();
        var row = JsonSerializer.Deserialize<LocalApiJournalEntry>(fixture.RowJson(), Json)!;
        var archive = row.Diagnostic!.Resources.Single(r => r.Role == "archive");
        await File.WriteAllTextAsync(Path.Combine(fixture.Settings.StateDirectory, "staged", archive.Leaf), "custody lost");
        fixture.Reopen();
        await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.AcquireDiagnosticOutputAsync(ready.OperationId,
            ready.CommandDigest, descriptor.OutputId, descriptor.SizeBytes, descriptor.Sha256));
        var lost = fixture.Status(ready); lost.Validate();
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, lost.Stage); Assert.False(lost.Output.Available);
        Assert.Equal(JsonSerializer.Serialize(Mutation(published), Json), JsonSerializer.Serialize(Mutation(lost), Json));
        Assert.Equal(Proof(published).GetProperty("launchSummary").GetRawText(), Proof(lost).GetProperty("launchSummary").GetRawText());
        fixture.Reopen(); fixture.Status(ready).Validate();
        Assert.Equal(OperatorMutationStage.Confirmed, Mutation(fixture.Status(ready)).Stage);
        Assert.Equal(1, fixture.Runner.LaunchCount);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("policy")]
    [InlineData("marker")]
    [InlineData("summary")]
    [InlineData("receipt")]
    public async Task CrossedAuthenticatedLaunchMetadataRejectsBeforeAnyCleanup(string crossing)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight();
        await fixture.Registry.ExecuteV3Async(Binding(ready)); fixture.Registry.Dispose();
        using (var journal = new LocalApiJournal(fixture.Settings))
        {
            var row = Assert.Single(journal.Load()); var metadata = row.Diagnostic!;
            var changed = crossing switch
            {
                "action" => row with { CommandName = "apk.diagnose" },
                "policy" => row with { Diagnostic = metadata with { OutputPolicy = null } },
                "marker" => row with { Diagnostic = metadata with { LaunchMayHaveDispatched = false } },
                "summary" => row with { Diagnostic = metadata with { LaunchSummary = metadata.LaunchSummary! with { DispatchAttempted = false } } },
                _ => row with { Diagnostic = metadata with { LaunchMutation = metadata.LaunchMutation! with { Target = "OTHER" } } }
            };
            journal.Save([changed]);
        }
        var orphan = Path.Combine(fixture.Settings.StateDirectory, "staged", "foreign.apk");
        var temp = Path.Combine(fixture.Settings.StateDirectory, "operations.v1.synthetic.tmp");
        await File.WriteAllTextAsync(orphan, "preserve foreign"); await File.WriteAllTextAsync(temp, "preserve temp");
        LocalApiCommandRegistry? incorrectlyAccepted = null;
        var rejected = Record.Exception(() => incorrectlyAccepted = new(fixture.Client, fixture.Settings));
        incorrectlyAccepted?.Dispose(); Assert.IsType<LocalApiException>(rejected);
        Assert.Equal("preserve foreign", await File.ReadAllTextAsync(orphan)); Assert.Equal("preserve temp", await File.ReadAllTextAsync(temp));
        Assert.Equal(1, fixture.Runner.LaunchCount);
    }

    [Fact]
    public async Task RestartWithConservativeIntentDoesNotInspectArmLaunchOrPromoteCandidate()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight(); fixture.Registry.Dispose();
        using (var journal = new LocalApiJournal(fixture.Settings))
        {
            var row = Assert.Single(journal.Load());
            journal.Save([row with { Stage = LocalApiOperationStage.Running, Consumed = true, Dispatched = true,
                Diagnostic = row.Diagnostic! with { OutputStarted = true, LaunchMayHaveDispatched = true } }]);
        }
        var calls = fixture.Runner.DiscoveryCount; fixture.Reopen();
        var status = fixture.Status(ready); status.Validate();
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, status.Stage); Assert.False(status.Output.Available);
        Assert.Equal(calls, fixture.Runner.DiscoveryCount); Assert.Equal(0, fixture.Runner.ArmCount); Assert.Equal(0, fixture.Runner.LaunchCount);
        Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV3Async(Binding(ready)))).Code);
    }

    [Fact]
    public async Task UnsupportedArmedRunnerIsRejectedBeforeInspectionOrNativeAllocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(armed: false);
        var before = fixture.JournalBytes();
        var error = await Assert.ThrowsAsync<LocalApiException>(() => fixture.Preflight());
        Assert.Equal("diagnostic_runner_unsupported", error.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Settings.StateDirectory, "staged")));
        var after = fixture.JournalBytes();
        Assert.Equal(before.Journal, after.Journal); Assert.Equal(before.Anchor, after.Anchor);
        Assert.Equal(0, fixture.Adapter.CallCount);
        Assert.Equal(0, fixture.Runner.DiscoveryCount); Assert.Equal(0, fixture.Runner.ArmCount);
    }

    [Theory]
    [InlineData("normal", LocalApiOperationStage.Failed, "rejectedBeforeDispatch")]
    [InlineData("early", LocalApiOperationStage.OutcomeUnknownRecoveryRequired, "outcomeUnknown")]
    public async Task ActualRejectedReceiptDoesNotEraseIndependentCaptureUncertainty(string mode, LocalApiOperationStage stage, string disposition)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(rejectBeforeDispatch: true); var ready = await fixture.Preflight(); fixture.Runner.Mode = mode;
        var status = await fixture.Registry.ExecuteV3Async(Binding(ready)); status.Validate();
        Assert.Equal(stage, status.Stage); Assert.True(status.Output.Available);
        Assert.Equal(disposition, Proof(status).GetProperty("launchSummary").GetProperty("disposition").GetString());
        Assert.False(Proof(status).GetProperty("launchSummary").GetProperty("dispatchAttempted").GetBoolean());
        Assert.Equal(OperatorMutationStage.Rejected, Mutation(status).Stage);
        Assert.Single(Mutation(status).Transitions); Assert.Equal(0, fixture.Runner.LaunchCount); Assert.Equal(1, fixture.Runner.ArmCount);
        fixture.Reopen(); fixture.Status(ready).Validate();
        Assert.Equal(stage, fixture.Status(ready).Stage); Assert.True(fixture.Status(ready).Output.Available);
    }

    [Fact]
    public async Task ClosedBoundaryExceptionAfterConservativeMarkerDoesNotInventBundleOrRejectedReceipt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight(); fixture.Runner.Mode = "no-callback";
        var status = await fixture.Registry.ExecuteV3Async(Binding(ready)); status.Validate();
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, status.Stage); Assert.False(status.Output.Available);
        Assert.Null(status.LaunchSummary); Assert.Null(status.Mutation); Assert.True(status.LaunchMayHaveDispatched);
        Assert.Equal(0, fixture.Runner.LaunchCount); Assert.Equal(1, fixture.Runner.ArmCount);
        fixture.Reopen(); Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, fixture.Status(ready).Stage);
        Assert.Null(fixture.Status(ready).Mutation);
    }

    [Fact]
    public async Task FutureSafeReceiptMustFitBeforeFirstNativeInputAcknowledgement()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        var before = fixture.JournalBytes();
        await Assert.ThrowsAsync<LocalApiException>(() => fixture.Preflight(new string('s', 4096)));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Settings.StateDirectory, "staged")));
        var after = fixture.JournalBytes();
        Assert.Equal(before.Journal, after.Journal); Assert.Equal(before.Anchor, after.Anchor);
        Assert.Equal(0, fixture.Runner.DiscoveryCount); Assert.Equal(0, fixture.Runner.ArmCount);
    }

    private static JsonElement Proof(LocalApiV3OperationStatus status) => JsonSerializer.SerializeToElement(status, Json);
    private static OperatorMutationReceipt Mutation(LocalApiV3OperationStatus status) =>
        JsonSerializer.Deserialize<OperatorMutationReceipt>(Proof(status).GetProperty("mutation"), Json)!;
    private static byte[] Binding(LocalApiV3PreflightResult value) => JsonSerializer.SerializeToUtf8Bytes(new
    { contractVersion = LocalApiV3Contract.Version, value.OperationId, value.CommandDigest }, Json);

    private sealed class Fixture : IDisposable
    {
        internal string Root = "", Apk = "";
        internal OwnedLaunchDiagnosticCaptureTests.Runner Runner = new();
        internal LocalApiStateSettings Settings = null!;
        internal LocalApiCommandRegistry Registry = null!;
        internal AdbClient Client = null!;
        internal BoundedRunner Adapter = null!;
        internal Func<string, bool>? JournalFault;
        internal Clock Clock = new();
        internal static async Task<Fixture> Create(bool armed = true, bool rejectBeforeDispatch = false)
        {
            var value = new Fixture { Root = Path.Combine(Path.GetTempPath(), "qfm-v3-launch-" + Guid.NewGuid().ToString("N")) };
            Directory.CreateDirectory(value.Root);
            value.Apk = Path.Combine(value.Root, "source.apk");
            await File.WriteAllBytesAsync(value.Apk, OwnedLaunchDiagnosticCaptureTests.Runner.Apk);
            value.Settings = LocalApiStateSettings.CreateForTests(Path.Combine(value.Root, "state")) with
            { JournalFault = phase => value.JournalFault?.Invoke(phase) == true };
            value.Adapter = armed ? new ArmedRunner(value.Runner, rejectBeforeDispatch) : new BoundedRunner(value.Runner);
            value.Client = new("adb", value.Adapter, new("aapt2", "apksigner"));
            value.Registry = new(value.Client, value.Settings, timeProvider: value.Clock);
            return value;
        }
        internal Task<LocalApiV3PreflightResult> Preflight(string serial = "QUEST123") => Registry.PreflightV3Async(JsonSerializer.SerializeToUtf8Bytes(new
        { contractVersion = LocalApiV3Contract.Version, command = "apk.launch-diagnose", apkPath = Apk, serial }));
        internal LocalApiV3OperationStatus Status(LocalApiV3PreflightResult ready) => Registry.GetV3Status(Binding(ready));
        internal (byte[] Journal, byte[] Anchor) JournalBytes()
        {
            // The live owner pins DELETE access on the anchor. Observe its bytes
            // with matching sharing without opening a second journal owner.
            using var anchor = File.Open(Path.Combine(Settings.StateDirectory, "operations.v1.anchor"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream(); anchor.CopyTo(bytes);
            return (File.ReadAllBytes(Path.Combine(Settings.StateDirectory, "operations.v1.json")), bytes.ToArray());
        }
        internal JsonElement RowJson()
        {
            using var envelope = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Settings.StateDirectory, "operations.v1.json")));
            using var payload = JsonDocument.Parse(Convert.FromBase64String(envelope.RootElement.GetProperty("payload").GetString()!));
            return payload.RootElement.EnumerateArray().First(row => row.TryGetProperty("operationVersion", out var version) && version.GetString() == LocalApiV3Contract.Version).Clone();
        }
        internal void Reopen() { Registry.Dispose(); Registry = new(Client, Settings, timeProvider: Clock); }
        public void Dispose() { Registry.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Clock : TimeProvider
    { internal DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    // Reuse the owned tracer's tested framing/capture engine. These delegates
    // remove a capability or alter only the exact ready-device readback.
    private class BoundedRunner(OwnedLaunchDiagnosticCaptureTests.Runner inner, bool rejectWhenArmed = false) : ITruncatingCommandRunner, IStreamingCommandRunner
    {
        internal int CallCount;
        public Task<CommandResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken c = default)
        { Interlocked.Increment(ref CallCount); return inner.RunAsync(f, a, t, c); }
        public Task<StreamingCommandResult> RunToStreamAsync(string f, IReadOnlyList<string> a, Stream d, long m, TimeSpan t, CancellationToken c = default)
        { Interlocked.Increment(ref CallCount); return inner.RunToStreamAsync(f, a, d, m, t, c); }
        public async Task<TruncatingCommandResult> RunTruncatingAsync(string f, IReadOnlyList<string> a, int o, int e, TimeSpan t, CancellationToken c = default)
        {
            Interlocked.Increment(ref CallCount);
            var result = await inner.RunTruncatingAsync(f, a, o, e, t, c);
            return rejectWhenArmed && inner.ArmCount == 1 && a.SequenceEqual(["devices", "-l"])
                ? result with { CommandResult = result.CommandResult with { StandardOutput = "List of devices attached\nQUEST123 offline\n" } }
                : result;
        }
    }
    private sealed class ArmedRunner : BoundedRunner, IArmedCaptureCommandRunner
    {
        private readonly OwnedLaunchDiagnosticCaptureTests.Runner _inner;
        internal ArmedRunner(OwnedLaunchDiagnosticCaptureTests.Runner inner, bool rejectWhenArmed) : base(inner, rejectWhenArmed) => _inner = inner;
        public Task<ArmedCaptureCommandResult<T>> RunArmedCaptureAsync<T>(string f, IReadOnlyList<string> a, Stream d,
            long m, TimeSpan w, Func<CancellationToken, Task<T>> action, CancellationToken c = default) =>
            _inner.RunArmedCaptureAsync(f, a, d, m, w, action, c);
    }
}
