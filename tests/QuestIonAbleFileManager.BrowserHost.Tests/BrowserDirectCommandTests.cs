using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectCommandTests
{
    internal const string Handle = "00000000-0000-4000-8000-000000000002";
    internal const string Original = "00000000-0000-4000-8000-000000000003";
    internal static byte[] Body(string action = "kiosk.direct.invoke", string command = "status", string? value = null,
        string? id = null, string? handle = null, string? original = null)
    {
        var fields = new Dictionary<string, object?> { ["requestId"] = id ?? Guid.NewGuid().ToString("D"), ["action"] = action, ["connectionId"] = handle ?? Handle };
        if (action == "kiosk.direct.invoke") { fields["command"] = command; fields["value"] = value; }
        else fields["originalRequestId"] = original ?? Original;
        if (action is "kiosk.direct.invoke" or "kiosk.direct.request-cancel") fields["confirmed"] = true;
        return JsonSerializer.SerializeToUtf8Bytes(fields);
    }
    [Theory]
    [InlineData("status", null, RustyKioskCommand.Status)]
    [InlineData("show-controls", null, RustyKioskCommand.ShowControls)]
    [InlineData("show-apps", null, RustyKioskCommand.ShowApps)]
    [InlineData("check-setup-helper", null, RustyKioskCommand.CheckSetupHelper)]
    [InlineData("set-launch-requirement", "any", RustyKioskCommand.SetLaunchRequirement)]
    [InlineData("set-launch-requirement", "wifi-on", RustyKioskCommand.SetLaunchRequirement)]
    [InlineData("set-launch-requirement", "wifi-off", RustyKioskCommand.SetLaunchRequirement)]
    [InlineData("cancel-pending-launch", null, RustyKioskCommand.CancelPendingLaunch)]
    [InlineData("launch-normal", null, RustyKioskCommand.LaunchNormal)]
    [InlineData("launch-kiosk", null, RustyKioskCommand.LaunchKiosk)]
    public void ExactEightCommandsRetainCoreCanonicalValuesAndDerivedLogicalId(string wire, string? value, RustyKioskCommand expected)
    {
        var request = BrowserDirectCommands.Parse(Body(command: wire, value: value, id: Original));
        Assert.Equal(expected, request.Command); Assert.Equal(value, request.Value); Assert.True(request.Mutation);
        Assert.Equal("kiosk_00000000000040008000000000000003", request.LogicalRequestId);
        var core = KioskDirectOperatorCommand.Invoke(expected, request.Value, true, request.LogicalRequestId);
        Assert.Equal(value, core.Value); Assert.Equal(request.LogicalRequestId, core.RequestId);
    }
    [Fact]
    public void AllTwentySixEnumsHaveAnExplicitInOrOutBoundary()
    {
        var initial = new[] { RustyKioskCommand.Status, RustyKioskCommand.ShowControls, RustyKioskCommand.ShowApps,
            RustyKioskCommand.CheckSetupHelper, RustyKioskCommand.SetLaunchRequirement, RustyKioskCommand.CancelPendingLaunch,
            RustyKioskCommand.LaunchNormal, RustyKioskCommand.LaunchKiosk, RustyKioskCommand.Reload };
        Assert.Equal(26, Enum.GetValues<RustyKioskCommand>().Length);
        foreach (var command in Enum.GetValues<RustyKioskCommand>())
        {
            var body = Body(command: command.ToWireName(), value: command == RustyKioskCommand.SetLaunchRequirement ? "any" : null);
            if (initial.Contains(command)) Assert.Equal(command, BrowserDirectCommands.Parse(body).Command);
            else Assert.Equal("direct_command_unsupported", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(body)).Code);
        }
    }
    [Theory]
    [InlineData("status", "")]
    [InlineData("status", " ")]
    [InlineData("status", "arbitrary")]
    [InlineData("set-launch-requirement", null)]
    [InlineData("set-launch-requirement", " any")]
    [InlineData("set-launch-requirement", "ANY")]
    [InlineData("set-launch-requirement", "wifi-on ")]
    [InlineData("set-launch-requirement", "file:///private")]
    public void ValuesCannotNormalizeIntoBroaderAuthority(string command, string? value) =>
        Assert.Equal("direct_value_invalid", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Body(command: command, value: value))).Code);

    [Theory]
    [InlineData("kiosk.direct.request-status", false)]
    [InlineData("kiosk.direct.request-result", false)]
    [InlineData("kiosk.direct.request-cancel", true)]
    public void RecoveryContainsOnlyOriginalBrowserIdentityAndOpaqueConnection(string action, bool mutation)
    {
        var request = BrowserDirectCommands.Parse(Body(action));
        Assert.Equal(Original, request.OriginalRequestId); Assert.Equal(Handle, request.ConnectionId);
        Assert.Null(request.Command); Assert.Null(request.Value); Assert.Equal(mutation, request.Mutation);
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Body(action))!;
        fields["command"] = JsonSerializer.SerializeToElement("status");
        Assert.Equal("fields_invalid", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields))).Code);
    }
    [Theory]
    [InlineData("logicalRequestId", "kiosk_attacker")]
    [InlineData("endpoint", "http://example.invalid")]
    [InlineData("originalRequestId", Original)]
    [InlineData("confirmed", false)]
    [InlineData("value", 1)]
    public void InvokeRejectsExtraOrUnconfirmedAuthority(string name, object value)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Body())!;
        fields[name] = JsonSerializer.SerializeToElement(value);
        Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
    }
    [Fact]
    public void DuplicateMissingAndNoncanonicalFieldsFailClosed()
    {
        var text = Encoding.UTF8.GetString(Body());
        Assert.Equal("duplicate_field", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Encoding.UTF8.GetBytes(text.Replace("\"value\":null", "\"value\":null,\"value\":null", StringComparison.Ordinal)))).Code);
        Assert.Equal("fields_invalid", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Encoding.UTF8.GetBytes(text.Replace(",\"value\":null", "", StringComparison.Ordinal)))).Code);
        Assert.Equal("direct_command_unsupported", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Body(command: "Status"))).Code);
    }

    [Fact]
    public async Task ReloadUsesOneNullValueCoreInvokeAndBoundAcknowledgement()
    {
        var parsed = BrowserDirectCommands.Parse(Body(command: "reload", id: Original));
        Assert.Equal(RustyKioskCommand.Reload, parsed.Command); Assert.Null(parsed.Value);
        var fixture = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            var outcome = await fixture.Invoke("reload");
            Assert.Equal("completed", outcome.State); Assert.Null(outcome.Result); Assert.Null(outcome.Mutation);
            var facts = outcome.KioskDirectCommand!;
            Assert.Equal("reload", facts.Command); Assert.Null(facts.Value);
            Assert.Equal("confirmed", facts.OriginalEffect); Assert.True(facts.ReconcilesOriginal);
            Assert.Equal("reload", facts.TypedResult!.Command); Assert.True(facts.TypedResult.Accepted); Assert.True(facts.TypedResult.Completed);
            Assert.Equal(OperatorMutationStage.Confirmed, facts.CoreReceipt!.Stage);
            var invoke = Assert.Single(fixture.Native.Core, item => item.Action == KioskDirectOperatorAction.Invoke);
            Assert.Equal(RustyKioskCommand.Reload, invoke.KioskCommand); Assert.Null(invoke.Value);
            Assert.Equal(facts.LogicalRequestId, invoke.RequestId); Assert.Equal(invoke.ActionId, facts.CoreReceipt.ActionId);
            Assert.Equal(KioskDirectOperatorAction.Invoke, facts.CoreReceipt.Action);
            Assert.Equal(facts.LogicalRequestId, facts.CoreReceipt.RequestId);
        }
        finally { Assert.True(await fixture.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("value", "", "direct_value_invalid")]
    [InlineData("value", " ", "direct_value_invalid")]
    [InlineData("value", "reload", "direct_value_invalid")]
    [InlineData("value", true, "direct_value_invalid")]
    [InlineData("confirmed", false, "confirmation_required")]
    [InlineData("command", "Reload", "direct_command_unsupported")]
    [InlineData("command", "focus-search", "direct_command_unsupported")]
    [InlineData("command", "focus-tag-editor", "direct_command_unsupported")]
    [InlineData("endpoint", "https://example.invalid", "fields_invalid")]
    public async Task InvalidReloadAuthorityIsRejectedBeforeNativeAccessOrLedgerAdmission(string name, object value, string code)
    {
        var fixture = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            var id = Guid.NewGuid().ToString("D");
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Body(command: "reload", id: id, handle: fixture.Handle))!;
            fields[name] = JsonSerializer.SerializeToElement(value);
            var before = fixture.Native.Core.Count;
            var error = await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(fields)));
            Assert.Equal(code, error.Code); Assert.True(error.Dispatched == false); Assert.Equal(before, fixture.Native.Core.Count);
            Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => fixture.Broker.Get(id)).Code);
        }
        finally { Assert.True(await fixture.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("command")]
    [InlineData("request-id")]
    [InlineData("accepted")]
    [InlineData("completed")]
    [InlineData("receipt-id")]
    [InlineData("receipt-stage")]
    [InlineData("invalid-stage")]
    public async Task ReloadCannotConfirmCrossedOrUnacknowledgedCoreEvidence(string variant)
    {
        var fixture = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            fixture.Native.Transform = (_, result) => variant switch
            {
                "command" => result with { KioskResult = result.KioskResult! with { Command = RustyKioskCommand.Status } },
                "request-id" => result with { KioskResult = result.KioskResult! with { RequestId = "crossed-request" } },
                "accepted" => result with { KioskResult = result.KioskResult! with { Accepted = false } },
                "completed" => result with { KioskResult = result.KioskResult! with { Completed = false } },
                "receipt-id" => result with { Mutation = result.Mutation with { ActionId = "crossed-action" } },
                "receipt-stage" => result with { Mutation = result.Mutation with { Stage = OperatorMutationStage.PendingWearerAction } },
                _ => result with { Mutation = result.Mutation with { Stage = (OperatorMutationStage)999 } }
            };
            var outcome = await fixture.Invoke("reload");
            Assert.Equal("outcomeUnknown", outcome.State); Assert.Equal("unknown", outcome.KioskDirectCommand!.OriginalEffect);
            Assert.False(outcome.KioskDirectCommand.ReconcilesOriginal); Assert.Null(outcome.KioskDirectCommand.TypedResult);
            Assert.Null(outcome.KioskDirectCommand.CoreReceipt);
        }
        finally { Assert.True(await fixture.Owner.ShutdownAsync()); }
    }

    [Fact]
    public async Task LostReloadAcknowledgementRecoversSameLogicalResultWithoutReplayOrRewritingOriginal()
    {
        var fixture = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            fixture.Native.Transform = (_, _) => throw new IOException("lost acknowledgement");
            var id = Guid.NewGuid().ToString("D"); var body = Body(command: "reload", id: id, handle: fixture.Handle);
            var original = await fixture.Broker.ExecuteAsync(body);
            Assert.Equal("outcomeUnknown", original.State); Assert.Same(original, fixture.Broker.Get(id));
            fixture.Native.Transform = null;
            fixture.Native.Lifecycle = "confirmed";
            var lifecycle = await fixture.Recover("kiosk.direct.request-status", id);
            Assert.False(lifecycle.KioskDirectCommand!.ReconcilesOriginal);
            fixture.Native.Lifecycle = null;
            var recovered = await fixture.Recover("kiosk.direct.request-result", id);
            Assert.Equal("completed", recovered.State); Assert.True(recovered.KioskDirectCommand!.ReconcilesOriginal);
            Assert.Equal("reload", recovered.KioskDirectCommand.TypedResult!.Command);
            Assert.Equal(original.KioskDirectCommand!.LogicalRequestId, recovered.KioskDirectCommand.LogicalRequestId);
            Assert.Equal(id, recovered.KioskDirectCommand.OriginalRequestId); Assert.Same(original, fixture.Broker.Get(id));
            var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(body));
            Assert.Equal("request_already_consumed", duplicate.Code); Assert.Null(duplicate.Dispatched);
            Assert.Single(fixture.Native.Core, item => item.Action == KioskDirectOperatorAction.Invoke);
            Assert.Single(fixture.Native.Core, item => item.Action == KioskDirectOperatorAction.RequestResult);
        }
        finally { Assert.True(await fixture.Owner.ShutdownAsync()); }
    }
}
