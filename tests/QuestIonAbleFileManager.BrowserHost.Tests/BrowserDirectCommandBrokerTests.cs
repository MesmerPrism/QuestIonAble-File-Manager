using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectCommandBrokerTests
{
    [Theory]
    [InlineData("status", null)]
    [InlineData("show-controls", null)]
    [InlineData("show-apps", null)]
    [InlineData("check-setup-helper", null)]
    [InlineData("set-launch-requirement", "wifi-on")]
    [InlineData("cancel-pending-launch", null)]
    [InlineData("launch-normal", null)]
    [InlineData("launch-kiosk", null)]
    public async Task EachCommandUsesOneInvokeAndFreshSignedStatusWithoutAdoptionOrStagingReplay(string command, string? value)
    {
        // A legitimate opaque ID may contain the private port's digits.
        var fixture = await Fixture.Create("b39873f6-fb8d-45b0-959d-000000000001");
        var outcome = await fixture.Invoke(command, value);
        Assert.Equal("completed", outcome.State); Assert.Null(outcome.Result); Assert.Null(outcome.Mutation);
        var facts = outcome.KioskDirectCommand!;
        Assert.True(facts.ReconcilesOriginal); Assert.Equal("confirmed", facts.OriginalEffect);
        Assert.Equal("kiosk_" + Guid.Parse(outcome.RequestId).ToString("N"), facts.LogicalRequestId);
        Assert.Equal(outcome.RequestId, facts.OriginalRequestId); Assert.Equal(command, facts.Command); Assert.Equal(value, facts.Value);
        Assert.Equal(1, fixture.Native.Core.Count(item => item.Action == KioskDirectOperatorAction.Invoke));
        Assert.Equal(new[] { KioskDirectOperatorAction.Adopt, KioskDirectOperatorAction.Status, KioskDirectOperatorAction.Invoke }, fixture.Native.Core.Select(item => item.Action));
        Assert.Equal(OperatorMutationStage.Confirmed, facts.CoreReceipt!.Stage);
        Assert.False(facts.TypedResult!.SetupHelperReady); // helper ack is not readiness
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(outcome));
        Assert.DoesNotContain("private_session", JsonSerializer.Serialize(outcome));
        Assert.DoesNotContain(":39873", JsonSerializer.Serialize(outcome));
        Assert.True(await fixture.Owner.ShutdownAsync());
    }
    [Theory]
    [InlineData(true, "pendingWearerAction")]
    [InlineData(null, "unknown")]
    public async Task GuardModeConfirmationDoesNotConfirmLaunchWhenRequirementPendingIsTrueOrMissing(bool? pending, string effect)
    {
        var fixture = await Fixture.Create(); fixture.Native.LaunchPending = pending;
        var result = await fixture.Invoke("launch-kiosk");
        Assert.NotEqual("completed", result.State); Assert.False(result.KioskDirectCommand!.ReconcilesOriginal);
        Assert.Equal(effect, result.KioskDirectCommand.OriginalEffect); Assert.Equal(OperatorMutationStage.Confirmed, result.KioskDirectCommand.CoreReceipt!.Stage);
        Assert.True(await fixture.Owner.ShutdownAsync());
    }
    [Fact]
    public async Task RunningMetadataExistsBeforeInvokeAndDuplicateCannotDispatchAgain()
    {
        var fixture = await Fixture.Create(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Native.BeforeCommand = async (_, _) => { started.SetResult(); await finish.Task; };
        var id = Guid.NewGuid().ToString("D"); var body = BrowserDirectCommandTests.Body(id: id, handle: fixture.Handle);
        var operation = fixture.Broker.ExecuteAsync(body); await started.Task;
        var running = fixture.Broker.Get(id); Assert.Equal("running", running.State);
        Assert.Equal(id, running.KioskDirectCommand!.OriginalRequestId); Assert.Equal("kiosk_" + Guid.Parse(id).ToString("N"), running.KioskDirectCommand.LogicalRequestId);
        Assert.Equal("unknown", running.KioskDirectCommand.OriginalEffect); Assert.True(fixture.Gate.IsBusy);
        Assert.Null((await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(body))).Dispatched);
        finish.SetResult(); await operation; Assert.Equal(1, fixture.Native.Core.Count(item => item.Action == KioskDirectOperatorAction.Invoke));
        Assert.True(await fixture.Owner.ShutdownAsync());
    }
    [Fact]
    public async Task RecoveryUsesOriginalLedgerOnlyAndTypedResultDoesNotRewriteHistoricalUnknown()
    {
        var fixture = await Fixture.Create(); fixture.Native.Lifecycle = "confirmed";
        var original = await fixture.Invoke("show-controls");
        Assert.Equal("outcomeUnknown", original.State); Assert.Equal("confirmed", original.KioskDirectCommand!.Lifecycle!.OperationState);
        fixture.Native.Lifecycle = null;
        var status = await fixture.Recover("kiosk.direct.request-status", original.RequestId);
        Assert.False(status.KioskDirectCommand!.ReconcilesOriginal);
        var read = await fixture.Recover("kiosk.direct.request-result", original.RequestId);
        Assert.Equal("completed", read.State); Assert.True(read.KioskDirectCommand!.ReconcilesOriginal);
        Assert.Equal(original.RequestId, read.KioskDirectCommand.OriginalRequestId);
        Assert.Same(original, fixture.Broker.Get(original.RequestId));
        Assert.Equal(1, fixture.Native.Core.Count(item => item.Action == KioskDirectOperatorAction.Invoke));
        Assert.True(await fixture.Owner.ShutdownAsync());
    }
    [Theory]
    [InlineData("pending", "pending")]
    [InlineData("pending_wearer_action", "pendingWearerAction")]
    [InlineData("confirmed", "unknown")]
    [InlineData("unknown", "unknown")]
    [InlineData("rejected", "unknown")]
    [InlineData("expired", "unknown")]
    [InlineData("cancelled", "unknown")]
    public async Task LifecycleFactsNeverConfirmOriginal(string lifecycle, string effect)
    {
        var fixture = await Fixture.Create(); fixture.Native.Lifecycle = lifecycle;
        var outcome = await fixture.Invoke();
        Assert.False(outcome.KioskDirectCommand!.ReconcilesOriginal); Assert.Equal(effect, outcome.KioskDirectCommand.OriginalEffect);
        Assert.Equal(lifecycle, outcome.KioskDirectCommand.Lifecycle!.OperationState);
        Assert.True(await fixture.Owner.ShutdownAsync());
    }
    [Theory]
    [InlineData("cancelled", true, true)]
    [InlineData("cancelled", false, false)]
    [InlineData("confirmed", true, false)]
    [InlineData("pending", false, false)]
    [InlineData("unknown", false, false)]
    public async Task CancellationAcknowledgesOnlyItsOwnExactCancelledCompletedRecord(string state, bool completed, bool acknowledged)
    {
        var fixture = await Fixture.Create(); var original = await fixture.Invoke();
        fixture.Native.Lifecycle = state; fixture.Native.LifecycleCompleted = completed;
        var cancellation = await fixture.Recover("kiosk.direct.request-cancel", original.RequestId);
        Assert.Equal(acknowledged, cancellation.KioskDirectCommand!.CancellationAcknowledged);
        Assert.False(cancellation.KioskDirectCommand.ReconcilesOriginal); Assert.Same(original, fixture.Broker.Get(original.RequestId));
        Assert.Equal(1, fixture.Native.Core.Count(item => item.Action == KioskDirectOperatorAction.CancelRequest));
        Assert.True(await fixture.Owner.ShutdownAsync());
    }
    [Fact]
    public async Task UnavailableOrCrossActionOriginalNeverTouchesNativeSession()
    {
        var fixture = await Fixture.Create(); var before = fixture.Native.Core.Count;
        Assert.Equal("direct_original_unavailable", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Recover("kiosk.direct.request-result", Guid.NewGuid().ToString("D")))).Code);
        var connectId = fixture.Connection.ConnectRequestId;
        Assert.Equal("direct_original_unavailable", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Recover("kiosk.direct.request-result", connectId))).Code);
        Assert.Equal(before, fixture.Native.Core.Count); Assert.True(await fixture.Owner.ShutdownAsync());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("show-controls")]
    [InlineData("show-apps")]
    [InlineData("check-setup-helper")]
    [InlineData("set-launch-requirement")]
    [InlineData("cancel-pending-launch")]
    [InlineData("launch-normal")]
    [InlineData("launch-kiosk")]
    public async Task EachCommandRejectsMissingOrContraryOperationSpecificEvidence(string wire)
    {
        var fixture = await Fixture.Create();
        fixture.Native.Transform = (command, result) =>
        {
            var typed = result.KioskResult!;
            var state = typed.State;
            typed = wire switch
            {
                "status" or "check-setup-helper" => typed with { Accepted = false },
                "show-controls" => typed with { State = state with { ControlsOpen = false } },
                "show-apps" => typed with { State = state with { ControlsOpen = true } },
                "set-launch-requirement" => typed with { State = state with { SelectedLaunchRequirement = RustyKioskLaunchRequirement.Any } },
                "cancel-pending-launch" => typed with { State = state with { PendingRequirementLaunch = true } },
                "launch-normal" => typed with { State = state with { GuardArmed = true } },
                _ => typed with { State = state with { GuardArmed = false } }
            };
            return result with { KioskResult = typed, Mutation = result.Mutation with
            { Stage = RustyKioskReadback.Confirms(typed.Command, command.Value, typed) ? OperatorMutationStage.Confirmed : OperatorMutationStage.PendingWearerAction } };
        };
        var outcome = await fixture.Invoke(wire, wire == "set-launch-requirement" ? "wifi-on" : null);
        Assert.NotEqual("completed", outcome.State); Assert.False(outcome.KioskDirectCommand!.ReconcilesOriginal);
        if (wire is "status" or "check-setup-helper") { Assert.Equal("unknown", outcome.KioskDirectCommand.OriginalEffect); Assert.Null(outcome.KioskDirectCommand.TypedResult); }
        else Assert.Equal("pendingWearerAction", outcome.KioskDirectCommand.OriginalEffect);
        Assert.True(await fixture.Owner.ShutdownAsync());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("schema")]
    [InlineData("command")]
    [InlineData("completed")]
    [InlineData("receipt-id")]
    [InlineData("receipt-action")]
    [InlineData("receipt-request")]
    [InlineData("native-command")]
    [InlineData("selected-key")]
    [InlineData("selected-package")]
    public async Task CrossedTypedIdentityAndReceiptNeverEscapeOrReconcile(string variant)
    {
        var fixture = await Fixture.Create();
        fixture.Native.Transform = (command, result) =>
        {
            var typed = result.KioskResult!;
            return variant switch
            {
                "id" => result with { KioskResult = typed with { RequestId = "private-sentinel" } },
                "schema" => result with { KioskResult = typed with { Schema = "private-sentinel" } },
                "command" => result with { KioskResult = typed with { Command = RustyKioskCommand.Reload } },
                "completed" => result with { KioskResult = typed with { Completed = false }, Mutation = result.Mutation with { Stage = OperatorMutationStage.PendingWearerAction } },
                "receipt-id" => result with { Mutation = result.Mutation with { ActionId = "private-sentinel" } },
                "receipt-action" => result with { Mutation = result.Mutation with { Action = KioskDirectOperatorAction.Status } },
                "receipt-request" => result with { Mutation = result.Mutation with { RequestId = "private-sentinel" } },
                "native-command" => result with { Command = command with { Value = "private-sentinel" } },
                "selected-key" => result with { KioskResult = typed with { State = typed.State with { SelectedKey = new string('x', 161) } } },
                _ => result with { KioskResult = typed with { State = typed.State with { SelectedPackage = "file:///private-sentinel" } } }
            };
        };
        var outcome = await fixture.Invoke();
        Assert.Equal("outcomeUnknown", outcome.State); Assert.Equal("unknown", outcome.KioskDirectCommand!.OriginalEffect);
        Assert.Null(outcome.KioskDirectCommand.TypedResult); Assert.Null(outcome.KioskDirectCommand.CoreReceipt);
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(outcome));
        Assert.True(await fixture.Owner.ShutdownAsync());
    }

    [Theory]
    [InlineData("state")]
    [InlineData("enqueue")]
    [InlineData("expiry")]
    public async Task MalformedLifecycleCannotBecomeACompletedRead(string variant)
    {
        var fixture = await Fixture.Create(); var original = await fixture.Invoke();
        fixture.Native.Transform = (_, result) => result with { RequestReceipt = result.RequestReceipt! with
        { OperationState = variant == "state" ? "private-sentinel" : "pending", EnqueuedAtMs = variant == "enqueue" ? -1 : 1, ExpiresAtMs = variant == "expiry" ? -1 : 2 } };
        var read = await fixture.Recover("kiosk.direct.request-status", original.RequestId);
        Assert.Equal("failed", read.State); Assert.Null(read.KioskDirectCommand!.Lifecycle); Assert.False(read.KioskDirectCommand.ReconcilesOriginal);
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(read)); Assert.True(await fixture.Owner.ShutdownAsync());
    }

    [Fact]
    public async Task DuplicateSelectedPolicyRowsCannotNormalizeIntoUniquePolicyProof()
    {
        var fixture = await Fixture.Create(); fixture.Native.Transform = (_, result) =>
        {
            var typed = result.KioskResult!;
            return result with { KioskResult = typed with { State = typed.State with { Entries = [typed.State.Entries[0], typed.State.Entries[0]] } } };
        };
        var outcome = await fixture.Invoke("set-launch-requirement", "wifi-off");
        Assert.Equal("outcomeUnknown", outcome.State); Assert.Null(outcome.KioskDirectCommand!.TypedResult!.SelectedEntry);
        Assert.False(outcome.KioskDirectCommand.ReconcilesOriginal); Assert.Equal(OperatorMutationStage.Confirmed, outcome.KioskDirectCommand.CoreReceipt!.Stage);
        Assert.True(await fixture.Owner.ShutdownAsync());
    }

    [Theory]
    [InlineData("status-session")]
    [InlineData("status-generation")]
    public async Task SignedStatusMustMatchAdoptedBindingBeforeAnyLogicalOperation(string mismatch)
    {
        var fixture = await Fixture.Create(); fixture.Native.SignedMismatch = mismatch;
        var outcome = await fixture.Invoke();
        Assert.Equal("outcomeUnknown", outcome.State); Assert.NotNull(outcome.KioskDirectCommand);
        Assert.DoesNotContain(fixture.Native.Core, item => item.Action == KioskDirectOperatorAction.Invoke);
        Assert.True(await fixture.Owner.ShutdownAsync());
    }

    [Fact]
    public async Task ExpiryDuringInvokeKeepsUnknownAndBlocksEveryNativeRecoveryButNotOwnedClose()
    {
        var fixture = await Fixture.Create(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Native.BeforeCommand = async (_, _) => { entered.SetResult(); await finish.Task; };
        var pending = fixture.Invoke(); await entered.Task; fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, fixture.Native.Revocations); Assert.True(fixture.Gate.IsBusy); finish.SetResult();
        var original = await pending; Assert.Equal("outcomeUnknown", original.State);
        foreach (var action in new[] { "kiosk.direct.request-status", "kiosk.direct.request-result", "kiosk.direct.request-cancel" })
            Assert.Equal("direct_connection_expired", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Recover(action, original.RequestId))).Code);
        var close = await fixture.Broker.ExecuteAsync(BrowserDirectCommandsTests.Body("kiosk.direct.close", Guid.NewGuid().ToString("D"), fixture.Handle));
        Assert.Equal("completed", close.State); Assert.Same(original, fixture.Broker.Get(original.RequestId));
        Assert.True(await fixture.Owner.ShutdownAsync());
    }

    [Fact]
    public async Task CancellationLossAndLaterLifecycleNeverRewriteEitherOriginal()
    {
        var fixture = await Fixture.Create(); var original = await fixture.Invoke();
        fixture.Native.BeforeCommand = (command, _) => command.Action == KioskDirectOperatorAction.CancelRequest ? throw new IOException("private-sentinel") : Task.CompletedTask;
        var cancel = await fixture.Recover("kiosk.direct.request-cancel", original.RequestId);
        Assert.Equal("outcomeUnknown", cancel.State); Assert.False(cancel.KioskDirectCommand!.CancellationAcknowledged);
        fixture.Native.Lifecycle = "cancelled";
        var read = await fixture.Recover("kiosk.direct.request-status", original.RequestId);
        Assert.False(read.KioskDirectCommand!.CancellationAcknowledged); Assert.False(read.KioskDirectCommand.ReconcilesOriginal);
        Assert.Same(cancel, fixture.Broker.Get(cancel.RequestId)); Assert.Same(original, fixture.Broker.Get(original.RequestId));
        Assert.Equal(1, fixture.Native.Core.Count(item => item.Action == KioskDirectOperatorAction.CancelRequest)); Assert.True(await fixture.Owner.ShutdownAsync());
    }

    [Fact]
    public async Task ShutdownAndCooperativeCancellationKeepCustodyUntilActualCoreTaskSettles()
    {
        var fixture = await Fixture.Create(); using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Native.BeforeCommand = async (_, token) => { entered.SetResult(); await release.Task; token.ThrowIfCancellationRequested(); };
        var id = Guid.NewGuid().ToString("D"); var pending = fixture.Broker.ExecuteAsync(BrowserDirectCommandTests.Body(id: id, handle: fixture.Handle), stopping.Token);
        await entered.Task; stopping.Cancel(); var shutdown = fixture.Owner.ShutdownAsync();
        Assert.True(fixture.Gate.IsBusy); Assert.False(pending.IsCompleted); Assert.False(shutdown.IsCompleted);
        Assert.Equal("running", fixture.Broker.Get(id).State); release.SetResult();
        Assert.Equal("outcomeUnknown", (await pending).State); Assert.True(await shutdown); Assert.False(fixture.Gate.IsBusy);
        Assert.Equal(1, fixture.Native.Core.Count(item => item.Action == KioskDirectOperatorAction.Invoke));
    }

    [Fact]
    public async Task ReplacementConnectionAndNewHostCannotRecoverOldLogicalRequest()
    {
        var fixture = await Fixture.Create(); var original = await fixture.Invoke();
        var close = await fixture.Broker.ExecuteAsync(BrowserDirectCommandsTests.Body("kiosk.direct.close", Guid.NewGuid().ToString("D"), fixture.Handle));
        Assert.Equal("completed", close.State);
        fixture.Connection = (await fixture.Broker.ExecuteAsync(BrowserDirectCommandsTests.Body(id: Guid.NewGuid().ToString("D")))).KioskDirectConnection!;
        var before = fixture.Native.Core.Count;
        Assert.Equal("direct_original_mismatch", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Recover("kiosk.direct.request-result", original.RequestId))).Code);
        var newHost = new BrowserCommands((_, _) => throw new InvalidOperationException("no ADB"), operationGate: fixture.Gate, directConnections: fixture.Owner);
        Assert.Equal("direct_original_unavailable", (await Assert.ThrowsAsync<BrowserRequestException>(() => newHost.ExecuteAsync(BrowserDirectCommandTests.Body("kiosk.direct.request-result", handle: fixture.Handle, original: original.RequestId)))).Code);
        Assert.Equal(before, fixture.Native.Core.Count); Assert.Same(original, fixture.Broker.Get(original.RequestId));
        Assert.True(await fixture.Owner.ShutdownAsync());
    }

    [Fact]
    public async Task CapacityAndConsumedIdChecksHappenBeforeNativeConnectionAccess()
    {
        var fixture = await Fixture.Create(); var id = Guid.NewGuid().ToString("D");
        var originalBody = BrowserDirectCommandTests.Body(id: id, handle: fixture.Handle); await fixture.Broker.ExecuteAsync(originalBody);
        for (var index = 2; index < BrowserCommands.MaximumRequests; index++)
            await fixture.Broker.ExecuteAsync(BrowserDirectCommandsTests.Body("kiosk.direct.status", Guid.NewGuid().ToString("D"), fixture.Handle));
        var before = fixture.Native.Core.Count;
        Assert.Equal("session_capacity_reached", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Invoke())).Code);
        var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(originalBody));
        Assert.Equal("request_already_consumed", duplicate.Code); Assert.Null(duplicate.Dispatched); Assert.Equal(before, fixture.Native.Core.Count);
        Assert.True(await fixture.Owner.ShutdownAsync());
    }

    [Fact]
    public async Task SharedGateRejectsNewLogicalRequestWithoutConsumingOrTouchingNative()
    {
        var fixture = await Fixture.Create(); var id = Guid.NewGuid().ToString("D");
        var body = BrowserDirectCommandTests.Body(id: id, handle: fixture.Handle); var before = fixture.Native.Core.Count;
        using (fixture.Gate.TryAcquire()) Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(body))).Code);
        Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => fixture.Broker.Get(id)).Code); Assert.Equal(before, fixture.Native.Core.Count);
        Assert.Equal("completed", (await fixture.Broker.ExecuteAsync(body)).State); Assert.True(await fixture.Owner.ShutdownAsync());
    }

    internal sealed class Fixture
    {
        internal readonly BrowserDirectConnectionsTests.Clock Clock = new();
        internal readonly BrowserOperationGate Gate = new();
        internal BrowserDirectConnections Owner = null!; internal BrowserCommands Broker = null!; internal NativeSession Native = null!;
        internal BrowserDirectConnectionOutcome Connection = null!; internal string Handle => Connection.ConnectionId!;
        internal static async Task<Fixture> Create(string? connectRequestId = null)
        {
            var fixture = new Fixture();
            fixture.Owner = new((context, _) => Task.FromResult<IBrowserDirectSession>(fixture.Native = new(context, fixture.Clock)), fixture.Clock);
            fixture.Broker = new((_, _) => throw new InvalidOperationException("no ADB path"), operationGate: fixture.Gate, directConnections: fixture.Owner);
            fixture.Connection = (await fixture.Broker.ExecuteAsync(BrowserDirectCommandsTests.Body(id: connectRequestId ?? Guid.NewGuid().ToString("D")))).KioskDirectConnection!;
            return fixture;
        }
        internal Task<BrowserCommandOutcome> Invoke(string command = "status", string? value = null) =>
            Broker.ExecuteAsync(BrowserDirectCommandTests.Body(command: command, value: value, handle: Handle));
        internal Task<BrowserCommandOutcome> Recover(string action, string original) =>
            Broker.ExecuteAsync(BrowserDirectCommandTests.Body(action, handle: Handle, original: original));
    }
    internal sealed class NativeSession(BrowserDirectConnectContext context, BrowserDirectConnectionsTests.Clock clock) : IBrowserDirectSession
    {
        private readonly BrowserDirectConnectionsTests.FakeSession _base = new(context, clock);
        internal List<KioskDirectOperatorCommand> Core { get; } = [];
        internal Func<KioskDirectOperatorCommand, CancellationToken, Task>? BeforeCommand;
        internal Func<KioskDirectOperatorCommand, KioskDirectOperatorResult, KioskDirectOperatorResult>? Transform;
        internal string? SignedMismatch; internal int Revocations => _base.Revocations;
        internal string? Lifecycle; internal bool LifecycleCompleted = true; internal bool? LaunchPending = false;
        private RustyKioskCommand _originalCommand; private string? _originalValue;
        public RustyKioskUsbDirectLinkReceipt Receipt => _base.Receipt;
        public async Task<KioskDirectOperatorResult> ExecuteAsync(KioskDirectOperatorCommand command, CancellationToken cancellationToken)
        {
            Core.Add(command);
            if (command.Action is KioskDirectOperatorAction.Adopt or KioskDirectOperatorAction.Status) { _base.Mismatch = SignedMismatch; return await _base.ExecuteAsync(command, cancellationToken); }
            if (BeforeCommand is not null) await BeforeCommand(command, cancellationToken);
            if (command.Action == KioskDirectOperatorAction.Invoke) { _originalCommand = command.KioskCommand!.Value; _originalValue = command.Value; }
            if (Lifecycle is not null || command.Action is KioskDirectOperatorAction.RequestStatus or KioskDirectOperatorAction.CancelRequest)
            {
                var receipt = new RustyKioskDirectRequestReceipt(command.RequestId!, Lifecycle ?? "pending", true, LifecycleCompleted, "private-sentinel", 1, 2);
                var result = new KioskDirectOperatorResult(command, new(command.ActionId, command.Action, receipt.MutationStage, "private-sentinel", command.RequestId), RequestReceipt: receipt);
                return Transform?.Invoke(command, result) ?? result;
            }
            var kind = command.KioskCommand ?? _originalCommand; var value = command.Value ?? _originalValue;
            var requirement = kind == RustyKioskCommand.SetLaunchRequirement ? RustyKioskCommands.ParseLaunchRequirement(value!) : RustyKioskLaunchRequirement.Any;
            var state = new RustyKioskState(1, 0, 1, false, [new("selected-key", "private-sentinel", "com.example.current", true, true, ["private-sentinel"], requirement)],
                "private-sentinel", "private-sentinel", "selected-key", "private-sentinel", "com.example.current", true, true, false, true, false, false, false,
                kind == RustyKioskCommand.LaunchKiosk, null, "private-sentinel", "private-sentinel", ControlsOpen: kind == RustyKioskCommand.ShowControls,
                SelectedLaunchRequirement: requirement, PendingRequirementLaunch: kind == RustyKioskCommand.CancelPendingLaunch ? false : LaunchPending);
            var kiosk = new RustyKioskOperatorResult(RustyKioskContract.ResultSchema, command.RequestId!, kind, true, true, "private-sentinel", state, "private-sentinel");
            var stage = RustyKioskReadback.Confirms(kind, value, kiosk) ? OperatorMutationStage.Confirmed : OperatorMutationStage.PendingWearerAction;
            var outcome = new KioskDirectOperatorResult(command, new(command.ActionId, command.Action, stage, "private-sentinel", command.RequestId), KioskResult: kiosk);
            return Transform?.Invoke(command, outcome) ?? outcome;
        }
        public Task<RustyKioskUsbDirectCleanupReceipt> CloseAsync() => _base.CloseAsync();
        public void Revoke() => _base.Revoke();
    }
}
