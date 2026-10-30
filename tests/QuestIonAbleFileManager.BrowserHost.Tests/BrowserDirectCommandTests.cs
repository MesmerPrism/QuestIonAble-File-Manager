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
    [Fact]
    public void SelectAcceptsOnlyReviewedCatalogEnvelopeUsingExistingCoreCommand()
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Body(command: "select", value: "app-key", id: Original))!;
        fields["catalogRequestId"] = JsonSerializer.SerializeToElement(Handle);
        var parsed = BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields));
        Assert.Equal(RustyKioskCommand.Select, parsed.Command);
        Assert.Equal("app-key", parsed.Value);
        Assert.Equal("app-key", KioskDirectOperatorCommand.Invoke(parsed.Command!.Value, parsed.Value, true, parsed.LogicalRequestId).Value);
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
    public void MetaHomeAdmitsOnlyTheExistingNullValueCoreCommand()
    {
        var parsed = BrowserDirectCommands.Parse(Body(command: "exit-meta-home", id: Original));
        Assert.Equal(RustyKioskCommand.ExitMetaHome, parsed.Command); Assert.Null(parsed.Value);
        Assert.True(parsed.Mutation); Assert.Equal(Handle, parsed.ConnectionId);
        Assert.Equal("kiosk_00000000000040008000000000000003", parsed.LogicalRequestId);
        var core = KioskDirectOperatorCommand.Invoke(parsed.Command!.Value, parsed.Value, true, parsed.LogicalRequestId);
        Assert.Equal(RustyKioskCommand.ExitMetaHome, core.KioskCommand); Assert.Null(core.Value);
        Assert.Equal(parsed.LogicalRequestId, core.RequestId);
    }

    [Fact]
    public void AllTwentySixEnumsHaveAnExplicitInOrOutBoundary()
    {
        var initial = new[] { RustyKioskCommand.Status, RustyKioskCommand.ShowControls, RustyKioskCommand.ShowApps,
            RustyKioskCommand.CheckSetupHelper, RustyKioskCommand.SetLaunchRequirement, RustyKioskCommand.CancelPendingLaunch,
            RustyKioskCommand.LaunchNormal, RustyKioskCommand.LaunchKiosk, RustyKioskCommand.Reload,
            RustyKioskCommand.EnableWifiAfterBoot, RustyKioskCommand.DisableWifiAfterBoot,
            RustyKioskCommand.EnableAccessibility, RustyKioskCommand.DisableAccessibility, RustyKioskCommand.PassthroughNatural, RustyKioskCommand.PassthroughContour,
            RustyKioskCommand.RequestWifiAdb, RustyKioskCommand.DisableWifiAdb, RustyKioskCommand.ExitMetaHome,
            RustyKioskCommand.SetSearch, RustyKioskCommand.FilterTag, RustyKioskCommand.AddTag, RustyKioskCommand.RemoveTag, RustyKioskCommand.Select };
        Assert.Equal(26, Enum.GetValues<RustyKioskCommand>().Length);
        foreach (var command in Enum.GetValues<RustyKioskCommand>())
        {
            var body = Body(command: command.ToWireName(), value: command == RustyKioskCommand.SetLaunchRequirement ? "any" :
                command is RustyKioskCommand.AddTag or RustyKioskCommand.RemoveTag ? "tag" : command==RustyKioskCommand.Select ? "app-key" : null);
            if(command==RustyKioskCommand.Select) { var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(body)!; fields["catalogRequestId"]=JsonSerializer.SerializeToElement(Handle); body=JsonSerializer.SerializeToUtf8Bytes(fields); }
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

    [Theory]
    [InlineData("reload", RustyKioskCommand.Reload)]
    [InlineData("exit-meta-home", RustyKioskCommand.ExitMetaHome)]
    public async Task AcknowledgementOnlyCommandsUseOneNullValueCoreInvokeAndBoundAcknowledgement(string wire, RustyKioskCommand expected)
    {
        var parsed = BrowserDirectCommands.Parse(Body(command: wire, id: Original));
        Assert.Equal(expected, parsed.Command); Assert.Null(parsed.Value);
        var fixture = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            var outcome = await fixture.Invoke(wire);
            Assert.Equal("completed", outcome.State); Assert.Null(outcome.Result); Assert.Null(outcome.Mutation);
            var facts = outcome.KioskDirectCommand!;
            Assert.Equal(wire, facts.Command); Assert.Null(facts.Value);
            Assert.Equal("confirmed", facts.OriginalEffect); Assert.True(facts.ReconcilesOriginal);
            Assert.Equal(wire, facts.TypedResult!.Command); Assert.True(facts.TypedResult.Accepted); Assert.True(facts.TypedResult.Completed);
            Assert.Equal(OperatorMutationStage.Confirmed, facts.CoreReceipt!.Stage);
            var invoke = Assert.Single(fixture.Native.Core, item => item.Action == KioskDirectOperatorAction.Invoke);
            Assert.Equal(expected, invoke.KioskCommand); Assert.Null(invoke.Value);
            Assert.Equal(facts.LogicalRequestId, invoke.RequestId); Assert.Equal(invoke.ActionId, facts.CoreReceipt.ActionId);
            Assert.Equal(KioskDirectOperatorAction.Invoke, facts.CoreReceipt.Action);
            Assert.Equal(facts.LogicalRequestId, facts.CoreReceipt.RequestId);
        }
        finally { Assert.True(await fixture.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("missing-value", "fields_invalid")]
    [InlineData("string-value", "direct_value_invalid")]
    [InlineData("bool-value", "direct_value_invalid")]
    [InlineData("array-value", "direct_value_invalid")]
    [InlineData("unconfirmed", "confirmation_required")]
    [InlineData("missing-confirmation", "fields_invalid")]
    [InlineData("case-command", "direct_command_unsupported")]
    [InlineData("logical-id", "fields_invalid")]
    [InlineData("endpoint", "fields_invalid")]
    [InlineData("connection-close", "fields_invalid")]
    public async Task InvalidMetaHomeAuthorityCannotAccessNativeOrConsumeItsRequest(string variant, string code)
    {
        var fixture = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            var id = Guid.NewGuid().ToString("D");
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Body(command: "exit-meta-home", id: id, handle: fixture.Handle))!;
            switch (variant)
            {
                case "missing-value": fields.Remove("value"); break;
                case "string-value": fields["value"] = JsonSerializer.SerializeToElement(""); break;
                case "bool-value": fields["value"] = JsonSerializer.SerializeToElement(false); break;
                case "array-value": fields["value"] = JsonSerializer.SerializeToElement(Array.Empty<string>()); break;
                case "unconfirmed": fields["confirmed"] = JsonSerializer.SerializeToElement(false); break;
                case "missing-confirmation": fields.Remove("confirmed"); break;
                case "case-command": fields["command"] = JsonSerializer.SerializeToElement("Exit-Meta-Home"); break;
                case "logical-id": fields["logicalRequestId"] = JsonSerializer.SerializeToElement("kiosk_attacker"); break;
                case "endpoint": fields["endpoint"] = JsonSerializer.SerializeToElement("https://example.invalid"); break;
                default: fields["connectionClose"] = JsonSerializer.SerializeToElement(true); break;
            }
            var before = fixture.Native.Core.Count;
            var error = await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(fields)));
            Assert.Equal(code, error.Code); Assert.True(error.Dispatched == false); Assert.Equal(before, fixture.Native.Core.Count);
            Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => fixture.Broker.Get(id)).Code);
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
    [InlineData("reload", "command")]
    [InlineData("reload", "request-id")]
    [InlineData("reload", "accepted")]
    [InlineData("reload", "completed")]
    [InlineData("reload", "receipt-id")]
    [InlineData("reload", "receipt-stage")]
    [InlineData("reload", "invalid-stage")]
    [InlineData("exit-meta-home", "command")]
    [InlineData("exit-meta-home", "request-id")]
    [InlineData("exit-meta-home", "accepted")]
    [InlineData("exit-meta-home", "completed")]
    [InlineData("exit-meta-home", "receipt-id")]
    [InlineData("exit-meta-home", "receipt-request")]
    [InlineData("exit-meta-home", "receipt-action")]
    [InlineData("exit-meta-home", "native-command")]
    [InlineData("exit-meta-home", "receipt-stage")]
    [InlineData("exit-meta-home", "invalid-stage")]
    public async Task AcknowledgementOnlyCommandsCannotConfirmCrossedOrUnacknowledgedCoreEvidence(string wire, string variant)
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
                "receipt-request" => result with { Mutation = result.Mutation with { RequestId = "crossed-request" } },
                "receipt-action" => result with { Mutation = result.Mutation with { Action = KioskDirectOperatorAction.Status } },
                "native-command" => result with { Command = result.Command with { KioskCommand = RustyKioskCommand.Status } },
                "receipt-stage" => result with { Mutation = result.Mutation with { Stage = OperatorMutationStage.PendingWearerAction } },
                _ => result with { Mutation = result.Mutation with { Stage = (OperatorMutationStage)999 } }
            };
            var outcome = await fixture.Invoke(wire);
            Assert.Equal("outcomeUnknown", outcome.State); Assert.Equal("unknown", outcome.KioskDirectCommand!.OriginalEffect);
            Assert.False(outcome.KioskDirectCommand.ReconcilesOriginal); Assert.Null(outcome.KioskDirectCommand.TypedResult);
            Assert.Null(outcome.KioskDirectCommand.CoreReceipt);
        }
        finally { Assert.True(await fixture.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("reload")]
    [InlineData("exit-meta-home")]
    public async Task LostAcknowledgementRecoversSameLogicalResultWithoutReplayOrRewritingOriginal(string wire)
    {
        var fixture = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            fixture.Native.Transform = (_, _) => throw new IOException("lost acknowledgement");
            var id = Guid.NewGuid().ToString("D"); var body = Body(command: wire, id: id, handle: fixture.Handle);
            var original = await fixture.Broker.ExecuteAsync(body);
            Assert.Equal("outcomeUnknown", original.State); Assert.Same(original, fixture.Broker.Get(id));
            fixture.Native.Transform = null;
            fixture.Native.Lifecycle = "confirmed";
            var lifecycle = await fixture.Recover("kiosk.direct.request-status", id);
            Assert.False(lifecycle.KioskDirectCommand!.ReconcilesOriginal);
            fixture.Native.Lifecycle = null;
            var recovered = await fixture.Recover("kiosk.direct.request-result", id);
            Assert.Equal("completed", recovered.State); Assert.True(recovered.KioskDirectCommand!.ReconcilesOriginal);
            Assert.Equal(wire, recovered.KioskDirectCommand.TypedResult!.Command);
            Assert.Equal(original.KioskDirectCommand!.LogicalRequestId, recovered.KioskDirectCommand.LogicalRequestId);
            Assert.Equal(id, recovered.KioskDirectCommand.OriginalRequestId); Assert.Same(original, fixture.Broker.Get(id));
            var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(body));
            Assert.Equal("request_already_consumed", duplicate.Code); Assert.Null(duplicate.Dispatched);
            Assert.Single(fixture.Native.Core, item => item.Action == KioskDirectOperatorAction.Invoke);
            Assert.Single(fixture.Native.Core, item => item.Action == KioskDirectOperatorAction.RequestResult);
        }
        finally { Assert.True(await fixture.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("enable-wifi-adb-after-boot",RustyKioskCommand.EnableWifiAfterBoot)]
    [InlineData("disable-wifi-adb-after-boot",RustyKioskCommand.DisableWifiAfterBoot)]
    public void BootPreferencePairUsesExistingCoreEnumsAndLiteralNull(string wire,RustyKioskCommand expected)
    {
        var parsed=BrowserDirectCommands.Parse(Body(command:wire));Assert.Equal(expected,parsed.Command);Assert.Null(parsed.Value);Assert.True(parsed.Mutation);
    }

    [Theory]
    [InlineData("enable-wifi-adb-after-boot",true)]
    [InlineData("disable-wifi-adb-after-boot",false)]
    public async Task BootPreferenceConfirmsOnlyItsMatchingBooleanAndFullCoreReceipt(string wire,bool desired)
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            BootReadback(fixture,desired);var result=await fixture.Invoke(wire);var d=result.KioskDirectCommand!;
            Assert.Equal("completed",result.State);Assert.Equal("confirmed",d.OriginalEffect);Assert.True(d.ReconcilesOriginal);
            var json=JsonSerializer.SerializeToElement(d.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(desired,json.GetProperty("requestWifiAdbAfterBoot").GetBoolean());
            Assert.Equal(OperatorMutationStage.Confirmed,d.CoreReceipt!.Stage);Assert.Equal(d.LogicalRequestId,d.TypedResult!.RequestId);
            Assert.Equal(wire,d.TypedResult.Command);Assert.True(d.TypedResult.Accepted);Assert.True(d.TypedResult.Completed);
            Assert.Null(result.Result);Assert.Null(result.Mutation);Assert.DoesNotContain("private-sentinel",json.GetRawText());
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("enable-wifi-adb-after-boot",true)]
    [InlineData("disable-wifi-adb-after-boot",false)]
    public async Task ContraryBootPreferenceReadbackRetainsPendingOriginal(string wire,bool desired)
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            BootReadback(fixture,!desired);var result=await fixture.Invoke(wire);var d=result.KioskDirectCommand!;
            Assert.Equal("pending",result.State);Assert.Equal("pendingWearerAction",d.OriginalEffect);Assert.False(d.ReconcilesOriginal);
            Assert.Equal(OperatorMutationStage.PendingWearerAction,d.CoreReceipt!.Stage);
            Assert.Equal(!desired,JsonSerializer.SerializeToElement(d.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("requestWifiAdbAfterBoot").GetBoolean());
            Assert.Same(result,fixture.Broker.Get(result.RequestId));
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("enable-wifi-adb-after-boot",true)]
    [InlineData("disable-wifi-adb-after-boot",false)]
    public async Task PendingMarkerCannotConfirmEvenWhenPreferenceMatches(string wire,bool desired)
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            BootReadback(fixture,desired,OperatorMutationStage.PendingWearerAction);var result=await fixture.Invoke(wire);
            Assert.Equal("outcomeUnknown",result.State);Assert.False(result.KioskDirectCommand!.ReconcilesOriginal);Assert.Null(result.KioskDirectCommand.TypedResult);Assert.Null(result.KioskDirectCommand.CoreReceipt);
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("enable-wifi-adb-after-boot",true)]
    [InlineData("disable-wifi-adb-after-boot",false)]
    public async Task OnlyLiteralNullPreferenceValueReachesTheOwner(string wire,bool desired)
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach(var value in new object[]{""," ","null",desired,1})
            {
                var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(Body(command:wire,handle:fixture.Handle))!;
                fields["value"]=JsonSerializer.SerializeToElement(value);var before=fixture.Native.Core.Count;
                Assert.Equal("direct_value_invalid",(await Assert.ThrowsAsync<BrowserRequestException>(()=>fixture.Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(fields)))).Code);
                Assert.Equal(before,fixture.Native.Core.Count);Assert.Equal("request_not_found",Assert.Throws<BrowserRequestException>(()=>fixture.Broker.Get(fields["requestId"].GetString()!)).Code);
            }
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("enable-wifi-adb-after-boot",true,false)]
    [InlineData("disable-wifi-adb-after-boot",false,false)]
    [InlineData("enable-wifi-adb-after-boot",true,true)]
    [InlineData("disable-wifi-adb-after-boot",false,true)]
    public async Task MissingOrMalformedRequiredCoreBooleanCannotBecomeFalseOrConfirmation(string wire,bool desired,bool malformed)
    {
        // Positive control proves this synthetic protocol fixture reaches the
        // required Core boolean rather than failing an unrelated missing field.
        var valid=BootProtocol(wire,"kiosk_fixture_123",desired);
        Assert.Equal(desired,RustyKioskOperatorResult.Parse(valid).State.RequestWifiAdbAfterBoot);
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            fixture.Native.Transform=(command,result)=>result with{KioskResult=RustyKioskOperatorResult.Parse(BootProtocol(wire,command.RequestId!,malformed?"false":null))};
            var result=await fixture.Invoke(wire);Assert.Equal("outcomeUnknown",result.State);Assert.Equal("unknown",result.KioskDirectCommand!.OriginalEffect);
            Assert.False(result.KioskDirectCommand.ReconcilesOriginal);Assert.Null(result.KioskDirectCommand.TypedResult);Assert.Null(result.KioskDirectCommand.CoreReceipt);
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("enable-wifi-adb-after-boot",true)]
    [InlineData("disable-wifi-adb-after-boot",false)]
    public async Task BootPreferenceTypedRecoveryUsesOriginalLogicalIdWithoutReplay(string wire,bool desired)
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            fixture.Native.Transform=(_,_)=>throw new IOException("lost reply");var original=await fixture.Invoke(wire);Assert.Equal("outcomeUnknown",original.State);
            fixture.Native.Transform=null;fixture.Native.Lifecycle="confirmed";var status=await fixture.Recover("kiosk.direct.request-status",original.RequestId);Assert.False(status.KioskDirectCommand!.ReconcilesOriginal);
            fixture.Native.Lifecycle=null;BootReadback(fixture,desired);var recovered=await fixture.Recover("kiosk.direct.request-result",original.RequestId);
            Assert.True(recovered.KioskDirectCommand!.ReconcilesOriginal);Assert.Equal(original.KioskDirectCommand!.LogicalRequestId,recovered.KioskDirectCommand.LogicalRequestId);
            Assert.Equal(desired,JsonSerializer.SerializeToElement(recovered.KioskDirectCommand.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("requestWifiAdbAfterBoot").GetBoolean());
            Assert.Same(original,fixture.Broker.Get(original.RequestId));Assert.Single(fixture.Native.Core,c=>c.Action==KioskDirectOperatorAction.Invoke);Assert.Single(fixture.Native.Core,c=>c.Action==KioskDirectOperatorAction.RequestResult);
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Fact]
    public async Task ExistingNineCommandsNeverExposeTheNewBooleanAndTypedConstructorWireIsUnchanged()
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            BootReadback(fixture,true);
            foreach(var wire in new[]{"status","show-controls","show-apps","check-setup-helper","set-launch-requirement","cancel-pending-launch","launch-normal","launch-kiosk","reload"})
            {
                var result=await fixture.Invoke(wire,wire=="set-launch-requirement"?"any":null);Assert.NotNull(result.KioskDirectCommand!.TypedResult);
                Assert.DoesNotContain("requestWifiAdbAfterBoot",JsonSerializer.Serialize(result,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
        var old=new BrowserDirectTypedResult("schema","kiosk_original","status",true,true,null,null,null,false,false,null,null,false,false,false,null);
        var (schema,id,command,accepted,completed,controls,key,package,installed,launchable,requirement,pending,guard,helperInstalled,helperReady,entry)=old;
        Assert.Equal(old,new BrowserDirectTypedResult(schema,id,command,accepted,completed,controls,key,package,installed,launchable,requirement,pending,guard,helperInstalled,helperReady,entry));
        Assert.Single(typeof(BrowserDirectTypedResult).GetConstructors(),constructor=>constructor.GetParameters().Length==16);
        const string literal="{\"schema\":\"schema\",\"requestId\":\"kiosk_original\",\"command\":\"status\",\"accepted\":true,\"completed\":true,\"controlsOpen\":null,\"selectedKey\":null,\"selectedPackage\":null,\"selectedInstalled\":false,\"selectedLaunchable\":false,\"selectedLaunchRequirement\":null,\"pendingRequirementLaunch\":null,\"guardArmed\":false,\"setupHelperInstalled\":false,\"setupHelperReady\":false,\"selectedEntry\":null}";
        Assert.Equal(literal,JsonSerializer.Serialize(old,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static void BootReadback(BrowserDirectCommandBrokerTests.Fixture fixture,bool observed,OperatorMutationStage? forcedStage=null) =>
        fixture.Native.Transform=(command,result)=>result.KioskResult is not { } typed?result:result with
        {
            KioskResult=typed with{State=typed.State with{RequestWifiAdbAfterBoot=observed}},
            Mutation=result.Mutation with{Stage=forcedStage??(RustyKioskReadback.Confirms(typed.Command,command.Value,typed with{State=typed.State with{RequestWifiAdbAfterBoot=observed}})?OperatorMutationStage.Confirmed:OperatorMutationStage.PendingWearerAction)}
        };

    private static string BootProtocol(string wire,string id,object? preference)
    {
        var state=new Dictionary<string,object?>{{"installed_count",0},{"not_installed_count",0},{"visible_count",0},{"visible_entries_truncated",false},{"entries",Array.Empty<object>()},
            {"search","private-search"},{"selected_installed",false},{"selected_launchable",false},{"wifi_adb_enabled",false},{"setup_helper_installed",false},{"setup_helper_ready",false},
            {"accessibility_enabled",false},{"guard_armed",false},{"status_line","private-sentinel"}};
        if(preference is not null)state["request_wifi_adb_after_boot"]=preference;
        return JsonSerializer.Serialize(new{schema=RustyKioskContract.ResultSchema,request_id=id,command=wire,accepted=true,completed=true,message="private-sentinel",state});
    }

    [Theory]
    [InlineData("enable-accessibility",RustyKioskCommand.EnableAccessibility)]
    [InlineData("disable-accessibility",RustyKioskCommand.DisableAccessibility)]
    [InlineData("passthrough-natural",RustyKioskCommand.PassthroughNatural)]
    [InlineData("passthrough-contour",RustyKioskCommand.PassthroughContour)]
    public void AccessibilityAndPassthroughMapOnlyExistingNullValueCoreCommands(string wire,RustyKioskCommand expected)
    {
        var parsed=BrowserDirectCommands.Parse(Body(command:wire));Assert.Equal(expected,parsed.Command);Assert.Null(parsed.Value);Assert.True(parsed.Mutation);
    }

    [Theory]
    [InlineData("enable-accessibility")]
    [InlineData("disable-accessibility")]
    [InlineData("passthrough-natural")]
    [InlineData("passthrough-contour")]
    public async Task AccessibilityAndPassthroughExposeOnlyMatchingCommandRelevantFacts(string wire)
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            SetControls(fixture,wire);var result=await fixture.Invoke(wire);var d=result.KioskDirectCommand!;
            Assert.Equal("completed",result.State);Assert.Equal("confirmed",d.OriginalEffect);Assert.True(d.ReconcilesOriginal);Assert.Equal(OperatorMutationStage.Confirmed,d.CoreReceipt!.Stage);
            Assert.Equal(d.LogicalRequestId,d.TypedResult!.RequestId);Assert.Equal(wire,d.TypedResult.Command);Assert.True(d.TypedResult.Accepted);Assert.True(d.TypedResult.Completed);
            var json=JsonSerializer.SerializeToElement(d.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web));Assert.False(json.TryGetProperty("requestWifiAdbAfterBoot",out _));
            if(wire.EndsWith("accessibility",StringComparison.Ordinal))
            {
                Assert.Equal(wire=="enable-accessibility",json.GetProperty("accessibilityEnabled").GetBoolean());
                foreach(var field in new[]{"systemPassthroughEnabled","passthroughStyle","passthroughLutApplied"})Assert.False(json.TryGetProperty(field,out _));
            }
            else
            {
                Assert.True(json.GetProperty("systemPassthroughEnabled").GetBoolean());Assert.Equal(wire=="passthrough-natural"?"natural":"contour-lut",json.GetProperty("passthroughStyle").GetString());
                Assert.False(json.TryGetProperty("accessibilityEnabled",out _));
                if(wire=="passthrough-contour")Assert.True(json.GetProperty("passthroughLutApplied").GetBoolean());else Assert.False(json.TryGetProperty("passthroughLutApplied",out _));
            }
            Assert.DoesNotContain("private-sentinel",json.GetRawText());Assert.Null(result.Result);Assert.Null(result.Mutation);
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("enable-accessibility","access")]
    [InlineData("disable-accessibility","access")]
    [InlineData("passthrough-natural","system")]
    [InlineData("passthrough-natural","system-null")]
    [InlineData("passthrough-natural","style")]
    [InlineData("passthrough-natural","style-null")]
    [InlineData("passthrough-contour","system")]
    [InlineData("passthrough-contour","system-null")]
    [InlineData("passthrough-contour","style")]
    [InlineData("passthrough-contour","style-null")]
    [InlineData("passthrough-contour","lut")]
    [InlineData("passthrough-contour","lut-null")]
    public async Task ContraryOrUnavailableControlsNeverConfirmOrInventFalse(string wire,string variant)
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            SetControls(fixture,wire,variant);var result=await fixture.Invoke(wire);var d=result.KioskDirectCommand!;
            Assert.Equal("pending",result.State);Assert.Equal("pendingWearerAction",d.OriginalEffect);Assert.False(d.ReconcilesOriginal);Assert.Equal(OperatorMutationStage.PendingWearerAction,d.CoreReceipt!.Stage);
            var json=JsonSerializer.SerializeToElement(d.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if(variant=="system-null")Assert.False(json.TryGetProperty("systemPassthroughEnabled",out _));
            if(variant=="style-null")Assert.False(json.TryGetProperty("passthroughStyle",out _));
            if(variant=="lut-null")Assert.False(json.TryGetProperty("passthroughLutApplied",out _));
            if(variant=="system")Assert.False(json.GetProperty("systemPassthroughEnabled").GetBoolean());
            if(variant=="lut")Assert.False(json.GetProperty("passthroughLutApplied").GetBoolean());
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("enable-accessibility")]
    [InlineData("disable-accessibility")]
    [InlineData("passthrough-natural")]
    [InlineData("passthrough-contour")]
    public async Task ControlProofStillRequiresCompleteAcceptedAndMatchingCoreTuple(string wire)
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach(var variant in new[]{"accepted","completed","marker","pending-marker"})
            {
                fixture.Native.Transform=(command,result)=>
                {
                    var value=ControlsResult(command,result,wire);
                    return variant switch
                    {
                        "accepted"=>value with{KioskResult=value.KioskResult! with{Accepted=false}},
                        "completed"=>value with{KioskResult=value.KioskResult! with{Completed=false}},
                        "marker"=>value with{Mutation=value.Mutation with{RequestId="crossed-request"}},
                        _=>value with{Mutation=value.Mutation with{Stage=OperatorMutationStage.PendingWearerAction}}
                    };
                };
                var result=await fixture.Invoke(wire);Assert.Equal("outcomeUnknown",result.State);Assert.False(result.KioskDirectCommand!.ReconcilesOriginal);Assert.Null(result.KioskDirectCommand.TypedResult);Assert.Null(result.KioskDirectCommand.CoreReceipt);
            }
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("passthrough-natural")]
    [InlineData("passthrough-contour")]
    public async Task UnknownTypedStyleCannotEscapeClosedWireEnum(string wire)
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            SetControls(fixture,wire,"unknown-style");var result=await fixture.Invoke(wire);
            Assert.Equal("outcomeUnknown",result.State);Assert.Null(result.KioskDirectCommand!.TypedResult);Assert.Null(result.KioskDirectCommand.CoreReceipt);Assert.False(result.KioskDirectCommand.ReconcilesOriginal);
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("enable-accessibility","missing-access")]
    [InlineData("disable-accessibility","missing-access")]
    [InlineData("enable-accessibility","bad-access")]
    [InlineData("disable-accessibility","bad-access")]
    [InlineData("passthrough-natural","bad-system")]
    [InlineData("passthrough-contour","bad-system")]
    [InlineData("passthrough-natural","bad-style")]
    [InlineData("passthrough-contour","bad-style")]
    [InlineData("passthrough-contour","bad-lut")]
    public async Task MissingRequiredOrMalformedProtocolFactsCannotAcknowledgeControls(string wire,string variant)
    {
        var valid=RustyKioskOperatorResult.Parse(ControlsProtocol(wire,"kiosk_fixture_123",null));Assert.True(RustyKioskReadback.Confirms(valid.Command,null,valid));
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            fixture.Native.Transform=(command,result)=>result with{KioskResult=RustyKioskOperatorResult.Parse(ControlsProtocol(wire,command.RequestId!,variant))};
            var result=await fixture.Invoke(wire);Assert.Equal("outcomeUnknown",result.State);Assert.Null(result.KioskDirectCommand!.TypedResult);Assert.Null(result.KioskDirectCommand.CoreReceipt);Assert.False(result.KioskDirectCommand.ReconcilesOriginal);
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("enable-accessibility")]
    [InlineData("disable-accessibility")]
    [InlineData("passthrough-natural")]
    [InlineData("passthrough-contour")]
    public async Task NewControlsKeepLiteralNullAndRecoverSameOriginalWithoutReplay(string wire)
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach(var value in new object[]{""," ","null",false,"natural",1})
            {
                var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(Body(command:wire,handle:fixture.Handle))!;fields["value"]=JsonSerializer.SerializeToElement(value);var before=fixture.Native.Core.Count;
                Assert.Equal("direct_value_invalid",(await Assert.ThrowsAsync<BrowserRequestException>(()=>fixture.Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(fields)))).Code);Assert.Equal(before,fixture.Native.Core.Count);
            }
            fixture.Native.Transform=(_,_)=>throw new IOException("lost reply");var original=await fixture.Invoke(wire);Assert.Equal("outcomeUnknown",original.State);
            fixture.Native.Transform=null;fixture.Native.Lifecycle="confirmed";Assert.False((await fixture.Recover("kiosk.direct.request-status",original.RequestId)).KioskDirectCommand!.ReconcilesOriginal);
            fixture.Native.Lifecycle=null;SetControls(fixture,wire);var read=await fixture.Recover("kiosk.direct.request-result",original.RequestId);
            Assert.True(read.KioskDirectCommand!.ReconcilesOriginal);Assert.Equal(original.KioskDirectCommand!.LogicalRequestId,read.KioskDirectCommand.LogicalRequestId);Assert.Same(original,fixture.Broker.Get(original.RequestId));
            Assert.Single(fixture.Native.Core,c=>c.Action==KioskDirectOperatorAction.Invoke);Assert.Single(fixture.Native.Core,c=>c.Action==KioskDirectOperatorAction.RequestResult);
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    [Fact]
    public async Task OldElevenOutputsNeverGainUnrelatedControlFields()
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            SetControls(fixture,"passthrough-contour");
            foreach(var wire in new[]{"status","show-controls","show-apps","check-setup-helper","set-launch-requirement","cancel-pending-launch","launch-normal","launch-kiosk","reload","enable-wifi-adb-after-boot","disable-wifi-adb-after-boot"})
            {
                var result=await fixture.Invoke(wire,wire=="set-launch-requirement"?"any":null);Assert.NotNull(result.KioskDirectCommand!.TypedResult);
                var json=JsonSerializer.SerializeToElement(result.KioskDirectCommand.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web));
                foreach(var field in new[]{"accessibilityEnabled","systemPassthroughEnabled","passthroughStyle","passthroughLutApplied"})Assert.False(json.TryGetProperty(field,out _));
                Assert.Equal(wire.EndsWith("after-boot",StringComparison.Ordinal),json.TryGetProperty("requestWifiAdbAfterBoot",out _));
            }
        }
        finally{Assert.True(await fixture.Owner.ShutdownAsync());}
    }

    private static void SetControls(BrowserDirectCommandBrokerTests.Fixture fixture,string wire,string? variant=null)=>
        fixture.Native.Transform=(command,result)=>ControlsResult(command,result,wire,variant);

    private static KioskDirectOperatorResult ControlsResult(KioskDirectOperatorCommand command,KioskDirectOperatorResult result,string wire,string? variant=null)
    {
        if(result.KioskResult is not { } typed)return result;
        var access=wire=="enable-accessibility";bool? system=true,lut=true;
        RustyKioskPassthroughStyle? style=wire=="passthrough-natural"?RustyKioskPassthroughStyle.Natural:RustyKioskPassthroughStyle.ContourLut;
        if(variant=="access")access=!access;if(variant=="system")system=false;if(variant=="system-null")system=null;if(variant=="lut")lut=false;if(variant=="lut-null")lut=null;
        if(variant=="style")style=style==RustyKioskPassthroughStyle.Natural?RustyKioskPassthroughStyle.ContourLut:RustyKioskPassthroughStyle.Natural;
        if(variant=="style-null")style=null;if(variant=="unknown-style")style=(RustyKioskPassthroughStyle)999;
        typed=typed with{State=typed.State with{AccessibilityEnabled=access,SystemPassthroughEnabled=system,PassthroughStyle=style,PassthroughLutApplied=lut}};
        return result with{KioskResult=typed,Mutation=result.Mutation with{Stage=RustyKioskReadback.Confirms(typed.Command,command.Value,typed)?OperatorMutationStage.Confirmed:OperatorMutationStage.PendingWearerAction}};
    }

    private static string ControlsProtocol(string wire,string id,string? variant)
    {
        var root=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(BootProtocol(wire,id,false))!;
        var state=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(root["state"].GetRawText())!;
        state["accessibility_enabled"]=JsonSerializer.SerializeToElement(wire=="enable-accessibility");state["system_passthrough_enabled"]=JsonSerializer.SerializeToElement(true);
        state["passthrough_style"]=JsonSerializer.SerializeToElement(wire=="passthrough-natural"?"natural":"contour-lut");state["passthrough_lut_applied"]=JsonSerializer.SerializeToElement(true);
        if(variant=="missing-access")state.Remove("accessibility_enabled");if(variant=="bad-access")state["accessibility_enabled"]=JsonSerializer.SerializeToElement("false");
        if(variant=="bad-system")state["system_passthrough_enabled"]=JsonSerializer.SerializeToElement("true");if(variant=="bad-style")state["passthrough_style"]=JsonSerializer.SerializeToElement("future-style");
        if(variant=="bad-lut")state["passthrough_lut_applied"]=JsonSerializer.SerializeToElement("true");root["state"]=JsonSerializer.SerializeToElement(state);return JsonSerializer.Serialize(root);
    }

    [Theory]
    [InlineData("request-wifi-adb",RustyKioskCommand.RequestWifiAdb)]
    [InlineData("disable-wifi-adb",RustyKioskCommand.DisableWifiAdb)]
    public void CurrentWifiAdbPairMapsExistingCoreEnumsWithoutValueAuthority(string wire,RustyKioskCommand expected)
    {
        var request=BrowserDirectCommands.Parse(Body(command:wire));Assert.Equal(expected,request.Command);Assert.Null(request.Value);Assert.True(request.Mutation);
    }

    [Theory]
    [InlineData("request-wifi-adb",true)]
    [InlineData("disable-wifi-adb",false)]
    public async Task CurrentWifiAdbConfirmsOnlyMatchingReportedBoolean(string wire,bool desired)
    {
        var f=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            SetWifiAdb(f,desired);var result=await f.Invoke(wire);var d=result.KioskDirectCommand!;
            Assert.Equal("completed",result.State);Assert.Equal("confirmed",d.OriginalEffect);Assert.True(d.ReconcilesOriginal);
            Assert.Equal(wire,d.TypedResult!.Command);Assert.Equal(d.LogicalRequestId,d.TypedResult.RequestId);Assert.True(d.TypedResult.Accepted);Assert.True(d.TypedResult.Completed);Assert.Equal(OperatorMutationStage.Confirmed,d.CoreReceipt!.Stage);
            var json=JsonSerializer.SerializeToElement(d.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web));Assert.Equal(desired,json.GetProperty("wifiAdbEnabled").GetBoolean());
            foreach(var field in new[]{"requestWifiAdbAfterBoot","accessibilityEnabled","systemPassthroughEnabled","passthroughStyle","passthroughLutApplied","endpoint","pairingCode"})Assert.False(json.TryGetProperty(field,out _));
            Assert.DoesNotContain("private-sentinel",json.GetRawText());Assert.Null(result.Result);Assert.Null(result.Mutation);
        }
        finally{Assert.True(await f.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("request-wifi-adb",true)]
    [InlineData("disable-wifi-adb",false)]
    public async Task ContraryCurrentWifiFlagNeverAcknowledgesTheOriginal(string wire,bool desired)
    {
        var f=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            SetWifiAdb(f,!desired);var result=await f.Invoke(wire);var d=result.KioskDirectCommand!;
            Assert.Equal("pending",result.State);Assert.Equal("pendingWearerAction",d.OriginalEffect);Assert.False(d.ReconcilesOriginal);Assert.Equal(OperatorMutationStage.PendingWearerAction,d.CoreReceipt!.Stage);
            Assert.Equal(!desired,JsonSerializer.SerializeToElement(d.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("wifiAdbEnabled").GetBoolean());Assert.Same(result,f.Broker.Get(result.RequestId));
        }
        finally{Assert.True(await f.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("request-wifi-adb",true)]
    [InlineData("disable-wifi-adb",false)]
    public async Task MatchingWifiFlagCannotBypassFullTypedAndCoreProof(string wire,bool desired)
    {
        var f=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach(var variant in new[]{"accepted","completed","command","marker-request","pending-marker"})
            {
                f.Native.Transform=(command,result)=>
                {
                    var value=WifiAdbResult(command,result,desired);
                    return variant switch
                    {
                        "accepted"=>value with{KioskResult=value.KioskResult! with{Accepted=false}},
                        "completed"=>value with{KioskResult=value.KioskResult! with{Completed=false}},
                        "command"=>value with{KioskResult=value.KioskResult! with{Command=RustyKioskCommand.Status}},
                        "marker-request"=>value with{Mutation=value.Mutation with{RequestId="crossed-request"}},
                        _=>value with{Mutation=value.Mutation with{Stage=OperatorMutationStage.PendingWearerAction}}
                    };
                };
                var result=await f.Invoke(wire);Assert.Equal("outcomeUnknown",result.State);Assert.False(result.KioskDirectCommand!.ReconcilesOriginal);Assert.Null(result.KioskDirectCommand.TypedResult);Assert.Null(result.KioskDirectCommand.CoreReceipt);
            }
        }
        finally{Assert.True(await f.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("request-wifi-adb",true,false)]
    [InlineData("disable-wifi-adb",false,false)]
    [InlineData("request-wifi-adb",true,true)]
    [InlineData("disable-wifi-adb",false,true)]
    public async Task RequiredWifiProtocolBooleanNeverDefaultsOrCoerces(string wire,bool desired,bool malformed)
    {
        var valid=RustyKioskOperatorResult.Parse(WifiProtocol(wire,"kiosk_fixture_123",desired));Assert.True(RustyKioskReadback.Confirms(valid.Command,null,valid));
        var f=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            f.Native.Transform=(command,result)=>result with{KioskResult=RustyKioskOperatorResult.Parse(WifiProtocol(wire,command.RequestId!,malformed?"false":null))};
            var result=await f.Invoke(wire);Assert.Equal("outcomeUnknown",result.State);Assert.Null(result.KioskDirectCommand!.TypedResult);Assert.Null(result.KioskDirectCommand.CoreReceipt);Assert.False(result.KioskDirectCommand.ReconcilesOriginal);
        }
        finally{Assert.True(await f.Owner.ShutdownAsync());}
    }

    [Theory]
    [InlineData("request-wifi-adb",true)]
    [InlineData("disable-wifi-adb",false)]
    public async Task WifiPairKeepsLiteralNullAndOriginalResultRecoveryWithoutReplay(string wire,bool desired)
    {
        var f=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach(var value in new object[]{""," ","null",false,true,1})
            {
                var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(Body(command:wire,handle:f.Handle))!;fields["value"]=JsonSerializer.SerializeToElement(value);var before=f.Native.Core.Count;
                Assert.Equal("direct_value_invalid",(await Assert.ThrowsAsync<BrowserRequestException>(()=>f.Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(fields)))).Code);Assert.Equal(before,f.Native.Core.Count);
            }
            f.Native.Transform=(_,_)=>throw new IOException("lost reply");var original=await f.Invoke(wire);Assert.Equal("outcomeUnknown",original.State);
            f.Native.Transform=null;f.Native.Lifecycle="confirmed";Assert.False((await f.Recover("kiosk.direct.request-status",original.RequestId)).KioskDirectCommand!.ReconcilesOriginal);
            f.Native.Lifecycle=null;SetWifiAdb(f,desired);var read=await f.Recover("kiosk.direct.request-result",original.RequestId);
            Assert.True(read.KioskDirectCommand!.ReconcilesOriginal);Assert.Equal(original.KioskDirectCommand!.LogicalRequestId,read.KioskDirectCommand.LogicalRequestId);
            Assert.Equal(desired,JsonSerializer.SerializeToElement(read.KioskDirectCommand.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("wifiAdbEnabled").GetBoolean());
            Assert.Same(original,f.Broker.Get(original.RequestId));Assert.Single(f.Native.Core,c=>c.Action==KioskDirectOperatorAction.Invoke);Assert.Single(f.Native.Core,c=>c.Action==KioskDirectOperatorAction.RequestResult);
        }
        finally{Assert.True(await f.Owner.ShutdownAsync());}
    }

    [Fact]
    public async Task OldFifteenCommandsNeverGainCurrentWifiWireProperty()
    {
        var f=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            SetWifiAdb(f,true);
            foreach(var wire in new[]{"status","show-controls","show-apps","check-setup-helper","set-launch-requirement","cancel-pending-launch","launch-normal","launch-kiosk","reload","enable-wifi-adb-after-boot","disable-wifi-adb-after-boot","enable-accessibility","disable-accessibility","passthrough-natural","passthrough-contour"})
            {
                var result=await f.Invoke(wire,wire=="set-launch-requirement"?"any":null);Assert.NotNull(result.KioskDirectCommand!.TypedResult);
                Assert.False(JsonSerializer.SerializeToElement(result.KioskDirectCommand.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web)).TryGetProperty("wifiAdbEnabled",out _));
            }
        }
        finally{Assert.True(await f.Owner.ShutdownAsync());}
    }

    private static void SetWifiAdb(BrowserDirectCommandBrokerTests.Fixture f,bool enabled)=>f.Native.Transform=(command,result)=>WifiAdbResult(command,result,enabled);
    private static KioskDirectOperatorResult WifiAdbResult(KioskDirectOperatorCommand command,KioskDirectOperatorResult result,bool enabled)
    {
        if(result.KioskResult is not { } typed)return result;typed=typed with{State=typed.State with{WifiAdbEnabled=enabled}};
        return result with{KioskResult=typed,Mutation=result.Mutation with{Stage=RustyKioskReadback.Confirms(typed.Command,command.Value,typed)?OperatorMutationStage.Confirmed:OperatorMutationStage.PendingWearerAction}};
    }
    private static string WifiProtocol(string wire,string id,object? enabled)
    {
        var root=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(BootProtocol(wire,id,false))!;var state=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(root["state"].GetRawText())!;
        if(enabled is null)state.Remove("wifi_adb_enabled");else state["wifi_adb_enabled"]=JsonSerializer.SerializeToElement(enabled);root["state"]=JsonSerializer.SerializeToElement(state);return JsonSerializer.Serialize(root);
    }
}
