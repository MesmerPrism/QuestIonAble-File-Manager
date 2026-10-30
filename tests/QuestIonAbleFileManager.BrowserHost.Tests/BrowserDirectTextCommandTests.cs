using System.Text.Json;
using System.Text;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectTextCommandTests
{
    [Theory]
    [InlineData("set-search", null, RustyKioskCommand.SetSearch)]
    [InlineData("filter-tag", null, RustyKioskCommand.FilterTag)]
    [InlineData("add-tag", "tag", RustyKioskCommand.AddTag)]
    [InlineData("remove-tag", "tag", RustyKioskCommand.RemoveTag)]
    public void TextCommandsMapExistingCoreValuesWithoutNormalization(string wire, string? value, RustyKioskCommand expected)
    {
        var request = BrowserDirectCommands.Parse(BrowserDirectCommandTests.Body(command: wire, value: value,
            id: BrowserDirectCommandTests.Original));
        Assert.Equal(expected, request.Command);
        Assert.Equal(value, request.Value);
        Assert.True(request.Mutation);
        Assert.Equal(BrowserDirectCommandTests.Handle, request.ConnectionId);
        Assert.Equal("kiosk_00000000000040008000000000000003", request.LogicalRequestId);
        var core = KioskDirectOperatorCommand.Invoke(expected, request.Value, true, request.LogicalRequestId);
        Assert.Equal(expected, core.KioskCommand);
        Assert.Equal(value, core.Value);
        Assert.Equal(request.LogicalRequestId, core.RequestId);
    }

    public static IEnumerable<object[]> CanonicalValues()
    {
        foreach (var wire in Wires)
        foreach (var value in new[] { "tag", "Straße", "σ", "\uFEFF", "a\tb\r\nc\0d", new string('x', 160), string.Concat(Enumerable.Repeat("😀", 80)) })
            yield return [wire, value];
    }

    [Theory]
    [MemberData(nameof(CanonicalValues))]
    public void CanonicalTextRetainsExactUtf16IdentityIncludingInternalControls(string wire, string value)
    {
        var parsed = BrowserDirectCommands.Parse(BrowserDirectCommandTests.Body(command: wire, value: value));
        Assert.Equal(value, parsed.Value);
    }

    public static IEnumerable<object?[]> NoncanonicalValues()
    {
        foreach (var wire in Wires)
        {
            foreach (var value in new[] { "", " ", "\t\r\n", " tag", "tag ", "\u0085tag", "tag\u0085", "\u00A0tag", "tag\u3000", new string('x', 161), string.Concat(Enumerable.Repeat("😀", 80)) + "x" })
                yield return [wire, value];
            if (wire is "add-tag" or "remove-tag") yield return [wire, null];
            foreach (var whitespace in "\u0009\u000A\u000B\u000C\u000D\u0020\u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000")
                yield return [wire, whitespace + "tag" + whitespace];
        }
    }

    [Theory]
    [MemberData(nameof(NoncanonicalValues))]
    public void NoncanonicalOrOversizeValuesRejectBeforeNativeAdmission(string wire, string? value) =>
        Assert.Equal("direct_value_invalid", Assert.Throws<BrowserRequestException>(() =>
            BrowserDirectCommands.Parse(BrowserDirectCommandTests.Body(command: wire, value: value))).Code);

    [Theory]
    [InlineData("set-search")]
    [InlineData("filter-tag")]
    [InlineData("add-tag")]
    [InlineData("remove-tag")]
    public async Task InvalidTextAuthorityDoesNotConsumeRequestOrAccessNative(string wire)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach (var variant in new[] { "missing", "number", "bool", "array", "object", "unconfirmed", "extra", "surrogate-high", "surrogate-low" })
            {
                var id = Guid.NewGuid().ToString("D");
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(BrowserDirectCommandTests.Body(command: wire, value: "tag", id: id, handle: f.Handle))!;
                if (variant == "missing") fields.Remove("value");
                if (variant == "number") fields["value"] = JsonSerializer.SerializeToElement(1);
                if (variant == "bool") fields["value"] = JsonSerializer.SerializeToElement(true);
                if (variant == "array") fields["value"] = JsonSerializer.SerializeToElement(new[] { "tag" });
                if (variant == "object") fields["value"] = JsonSerializer.SerializeToElement(new { value = "tag" });
                if (variant == "unconfirmed") fields["confirmed"] = JsonSerializer.SerializeToElement(false);
                if (variant == "extra") fields["selectedKey"] = JsonSerializer.SerializeToElement("selected-key");
                var json = JsonSerializer.Serialize(fields);
                if (variant.StartsWith("surrogate-", StringComparison.Ordinal)) json = json.Replace("\"value\":\"tag\"", variant == "surrogate-high" ? "\"value\":\"\\uD800\"" : "\"value\":\"\\uDC00\"", StringComparison.Ordinal);
                var before = f.Native.Core.Count;
                await Assert.ThrowsAsync<BrowserRequestException>(() => f.Broker.ExecuteAsync(Encoding.UTF8.GetBytes(json)));
                Assert.Equal(before, f.Native.Core.Count);
                Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => f.Broker.Get(id)).Code);
            }
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("set-search", "σ", "σ", true)]
    [InlineData("set-search", "σ", "Σ", false)]
    [InlineData("set-search", null, "", true)]
    [InlineData("filter-tag", "σ", "Σ", true)]
    [InlineData("filter-tag", "ß", "SS", false)]
    [InlineData("filter-tag", null, null, true)]
    [InlineData("filter-tag", null, "", true)]
    public async Task EffectiveTextUsesCoreComparisonAndLiteralBoundedProjection(string wire, string? requested, string? effective, bool matches)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            f.Native.Transform = (command, result) => WithText(command, result, wire, effective);
            var outcome = await f.Invoke(wire, requested); var d = outcome.KioskDirectCommand!;
            Assert.Equal(matches ? "completed" : "pending", outcome.State);
            Assert.Equal(matches, d.ReconcilesOriginal);
            Assert.Equal(matches ? OperatorMutationStage.Confirmed : OperatorMutationStage.PendingWearerAction, d.CoreReceipt!.Stage);
            var json = JsonSerializer.SerializeToElement(d.TypedResult, Web);
            Assert.Equal(matches, json.GetProperty("valueMatches").GetBoolean());
            Assert.Equal(effective ?? "", json.GetProperty(wire == "set-search" ? "search" : "tagFilter").GetString());
            Assert.False(json.TryGetProperty(wire == "set-search" ? "tagFilter" : "search", out _));
            Assert.False(json.TryGetProperty("requestedTagPresent", out _));
            Assert.DoesNotContain("private-sentinel", json.GetRawText());
            Assert.Null(outcome.Result); Assert.Null(outcome.Mutation);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("set-search", "oversize")]
    [InlineData("set-search", "surrogate")]
    [InlineData("set-search", "null")]
    [InlineData("filter-tag", "oversize")]
    [InlineData("filter-tag", "surrogate")]
    public async Task UnsafeEffectiveTextCannotEscapeOrReconcile(string wire, string variant)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            var effective = variant == "oversize" ? new string('x', 161) : variant == "surrogate" ? "\uD800" : null;
            f.Native.Transform = (command, result) => WithText(command, result, wire, effective);
            var outcome = await f.Invoke(wire, "tag"); var d = outcome.KioskDirectCommand!;
            Assert.Equal("outcomeUnknown", outcome.State); Assert.False(d.ReconcilesOriginal);
            Assert.Null(d.TypedResult); Assert.Null(d.CoreReceipt);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("set-search")]
    [InlineData("filter-tag")]
    public async Task MaximumEmojiAndInternalControlProofIsReturnedWithoutTruncation(string wire)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach (var value in new[] { string.Concat(Enumerable.Repeat("😀", 80)), "\uFEFF", "a\tb\r\nc\0d" })
            {
                f.Native.Transform = (command, result) => WithText(command, result, wire, value);
                var outcome = await f.Invoke(wire, value); var d = outcome.KioskDirectCommand!;
                Assert.True(d.ReconcilesOriginal); Assert.True(d.TypedResult!.ValueMatches);
                Assert.Equal(value, wire == "set-search" ? d.TypedResult.Search : d.TypedResult.TagFilter);
            }
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("add-tag", true)]
    [InlineData("remove-tag", false)]
    public async Task TagsConfirmOnlyRequestedMembershipInUniqueReturnedSelection(string wire, bool present)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            f.Native.Transform = (command, result) => WithTags(command, result, present);
            var outcome = await f.Invoke(wire, "σ"); var d = outcome.KioskDirectCommand!;
            Assert.Equal("completed", outcome.State); Assert.True(d.ReconcilesOriginal);
            Assert.Equal("selected-key", d.TypedResult!.SelectedKey); Assert.Equal("selected-key", d.TypedResult.SelectedEntry!.Key);
            Assert.False(d.TypedResult.SelectedInstalled); Assert.False(d.TypedResult.SelectedLaunchable);
            Assert.Equal(OperatorMutationStage.Confirmed, d.CoreReceipt!.Stage);
            var json = JsonSerializer.SerializeToElement(d.TypedResult, Web);
            Assert.Equal(present, json.GetProperty("requestedTagPresent").GetBoolean());
            foreach (var field in new[] { "search", "tagFilter", "valueMatches", "tags", "tagFilePath" }) Assert.False(json.TryGetProperty(field, out _));
            Assert.DoesNotContain("private-sentinel", json.GetRawText());
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("add-tag", true)]
    [InlineData("remove-tag", false)]
    public async Task ContraryMembershipRetainsPendingActualReceipt(string wire, bool desired)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            f.Native.Transform = (command, result) => WithTags(command, result, !desired);
            var outcome = await f.Invoke(wire, "σ"); var d = outcome.KioskDirectCommand!;
            Assert.Equal("pending", outcome.State); Assert.False(d.ReconcilesOriginal);
            Assert.Equal(OperatorMutationStage.PendingWearerAction, d.CoreReceipt!.Stage);
            Assert.Equal(!desired, d.TypedResult!.RequestedTagPresent);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("add-tag", true)]
    [InlineData("remove-tag", false)]
    public async Task MissingWrongDuplicateOrMalformedSelectedRowsRemainUnknown(string wire, bool desired)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach (var variant in new[] { "missing", "wrong-key", "duplicate", "duplicate-contrary", "no-selection", "malformed-key" })
            {
                OperatorMutationStage? actualStage = null;
                f.Native.Transform = (command, result) =>
                {
                    var prepared = WithTags(command, result, desired); var typed = prepared.KioskResult!; var state = typed.State;
                    var row = state.Entries[0];
                    state = variant switch
                    {
                        "missing" => state with { Entries = [] },
                        "wrong-key" => state with { Entries = [row with { Key = "other-key" }] },
                        "duplicate" => state with { Entries = [row, row] },
                        "duplicate-contrary" => state with { Entries = [row, row with { Tags = desired ? [] : ["Σ"] }] },
                        "no-selection" => state with { SelectedKey = null },
                        _ => state with { SelectedKey = "\uD800", Entries = [row with { Key = "\uD800" }] }
                    };
                    var final = RebindStage(command, prepared, typed with { State = state }); actualStage = final.Mutation.Stage; return final;
                };
                var outcome = await f.Invoke(wire, "σ"); var d = outcome.KioskDirectCommand!;
                Assert.Equal("outcomeUnknown", outcome.State); Assert.False(d.ReconcilesOriginal);
                if (variant == "malformed-key") { Assert.Null(d.TypedResult); Assert.Null(d.CoreReceipt); }
                else { Assert.Null(d.TypedResult!.SelectedEntry); Assert.Null(d.TypedResult.RequestedTagPresent); Assert.Equal(actualStage, d.CoreReceipt!.Stage); }
            }
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("set-search")]
    [InlineData("filter-tag")]
    [InlineData("add-tag")]
    [InlineData("remove-tag")]
    public async Task MatchingTextProofCannotBypassAnyTypedOrActualCoreBinding(string wire)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach (var variant in new[] { "schema", "request", "command", "accepted", "completed", "native-command", "native-value", "receipt-id", "receipt-action", "receipt-request", "receipt-stage", "invalid-stage" })
            {
                f.Native.Transform = (command, result) =>
                {
                    result = Matching(command, result, wire, "tag"); var typed = result.KioskResult!;
                    return variant switch
                    {
                        "schema" => result with { KioskResult = typed with { Schema = "crossed" } },
                        "request" => result with { KioskResult = typed with { RequestId = "crossed" } },
                        "command" => result with { KioskResult = typed with { Command = RustyKioskCommand.Status } },
                        "accepted" => result with { KioskResult = typed with { Accepted = false } },
                        "completed" => result with { KioskResult = typed with { Completed = false } },
                        "native-command" => result with { Command = command with { KioskCommand = RustyKioskCommand.Status } },
                        "native-value" => result with { Command = command with { Value = "crossed" } },
                        "receipt-id" => result with { Mutation = result.Mutation with { ActionId = "crossed" } },
                        "receipt-action" => result with { Mutation = result.Mutation with { Action = KioskDirectOperatorAction.Status } },
                        "receipt-request" => result with { Mutation = result.Mutation with { RequestId = "crossed" } },
                        "receipt-stage" => result with { Mutation = result.Mutation with { Stage = OperatorMutationStage.PendingWearerAction } },
                        _ => result with { Mutation = result.Mutation with { Stage = (OperatorMutationStage)999 } }
                    };
                };
                var outcome = await f.Invoke(wire, "tag"); var d = outcome.KioskDirectCommand!;
                Assert.Equal("outcomeUnknown", outcome.State); Assert.False(d.ReconcilesOriginal);
                Assert.Null(d.TypedResult); Assert.Null(d.CoreReceipt);
            }
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("set-search")]
    [InlineData("filter-tag")]
    [InlineData("add-tag")]
    [InlineData("remove-tag")]
    public async Task LostReplyKeepsOriginalAndOnlyBoundResultCanConfirmWithoutAnotherInvoke(string wire)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            f.Native.Transform = (_, _) => throw new IOException("lost reply");
            var original = await f.Invoke(wire, "tag"); Assert.Equal("outcomeUnknown", original.State);
            f.Native.Transform = null; f.Native.Lifecycle = "confirmed";
            Assert.False((await f.Recover("kiosk.direct.request-status", original.RequestId)).KioskDirectCommand!.ReconcilesOriginal);
            f.Native.Lifecycle = "cancelled";
            var cancelled = await f.Recover("kiosk.direct.request-cancel", original.RequestId);
            Assert.True(cancelled.KioskDirectCommand!.CancellationAcknowledged); Assert.False(cancelled.KioskDirectCommand.ReconcilesOriginal);
            f.Native.Lifecycle = null; f.Native.Transform = (command, result) => Matching(command, result, wire, "tag");
            var recovered = await f.Recover("kiosk.direct.request-result", original.RequestId); var d = recovered.KioskDirectCommand!;
            Assert.True(d.ReconcilesOriginal); Assert.Equal(original.KioskDirectCommand!.LogicalRequestId, d.LogicalRequestId);
            Assert.Equal("tag", d.Value); Assert.Equal(wire, d.TypedResult!.Command); Assert.Same(original, f.Broker.Get(original.RequestId));
            Assert.Single(f.Native.Core, c => c.Action == KioskDirectOperatorAction.Invoke);
            Assert.Single(f.Native.Core, c => c.Action == KioskDirectOperatorAction.RequestResult);
            Assert.Equal("direct_original_mismatch", (await Assert.ThrowsAsync<BrowserRequestException>(() =>
                f.Broker.ExecuteAsync(BrowserDirectCommandTests.Body("kiosk.direct.request-result", handle: Guid.NewGuid().ToString("D"), original: original.RequestId)))).Code);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("set-search")]
    [InlineData("filter-tag")]
    [InlineData("add-tag")]
    [InlineData("remove-tag")]
    public async Task CrossedSignedSessionOrGenerationCannotInvokeTextCommand(string wire)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach (var mismatch in new[] { "status-session", "status-generation" })
            {
                f.Native.SignedMismatch = mismatch;
                var outcome = await f.Invoke(wire, "tag");
                Assert.Equal("outcomeUnknown", outcome.State); Assert.False(outcome.KioskDirectCommand!.ReconcilesOriginal);
                Assert.DoesNotContain(f.Native.Core, c => c.Action == KioskDirectOperatorAction.Invoke);
            }
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("set-search")]
    [InlineData("filter-tag")]
    [InlineData("add-tag")]
    [InlineData("remove-tag")]
    public async Task CancellationAndExpiryRetainCustodyUntilOriginalTaskActuallySettles(string wire)
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create(); using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            f.Native.BeforeCommand = async (_, _) => { entered.SetResult(); await release.Task; };
            f.Native.Transform = (command, result) => Matching(command, result, wire, "tag");
            var id = Guid.NewGuid().ToString("D");
            var pending = f.Broker.ExecuteAsync(BrowserDirectCommandTests.Body(command: wire, value: "tag", id: id, handle: f.Handle), stopping.Token);
            await entered.Task; stopping.Cancel(); f.Clock.Advance(TimeSpan.FromMinutes(3));
            Assert.True(f.Gate.IsBusy); Assert.False(pending.IsCompleted); Assert.Equal("running", f.Broker.Get(id).State);
            release.SetResult(); var original = await pending;
            Assert.Equal("outcomeUnknown", original.State); Assert.False(original.KioskDirectCommand!.ReconcilesOriginal); Assert.False(f.Gate.IsBusy);
            Assert.Equal("direct_connection_expired", (await Assert.ThrowsAsync<BrowserRequestException>(() => f.Recover("kiosk.direct.request-result", id))).Code);
            Assert.Same(original, f.Broker.Get(id)); Assert.Single(f.Native.Core, c => c.Action == KioskDirectOperatorAction.Invoke);
        }
        finally { release.TrySetResult(); Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Fact]
    public async Task AllExistingEighteenSerializedResultsOmitEveryTextProofField()
    {
        var f = await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            foreach (var wire in new[] { "status", "show-controls", "show-apps", "reload", "exit-meta-home", "enable-wifi-adb-after-boot", "disable-wifi-adb-after-boot", "enable-accessibility", "disable-accessibility", "passthrough-natural", "passthrough-contour", "request-wifi-adb", "disable-wifi-adb", "check-setup-helper", "set-launch-requirement", "cancel-pending-launch", "launch-normal", "launch-kiosk" })
            {
                var result = await f.Invoke(wire, wire == "set-launch-requirement" ? "any" : null);
                var typed = Assert.IsType<BrowserDirectTypedResult>(result.KioskDirectCommand!.TypedResult);
                var json = JsonSerializer.SerializeToElement(typed, Web);
                foreach (var field in new[] { "search", "tagFilter", "valueMatches", "requestedTagPresent" }) Assert.False(json.TryGetProperty(field, out _));
            }
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    private static readonly string[] Wires = ["set-search", "filter-tag", "add-tag", "remove-tag"];
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static KioskDirectOperatorResult WithText(KioskDirectOperatorCommand command, KioskDirectOperatorResult result, string wire, string? effective)
    {
        var typed = result.KioskResult!;
        return RebindStage(command, result, typed with { State = wire == "set-search" ? typed.State with { Search = effective! } : typed.State with { TagFilter = effective } });
    }
    private static KioskDirectOperatorResult WithTags(KioskDirectOperatorCommand command, KioskDirectOperatorResult result, bool present, string tag = "Σ")
    {
        var typed = result.KioskResult!; var row = typed.State.Entries[0] with { Tags = present ? [tag] : [], Installed = false, Launchable = false };
        return RebindStage(command, result, typed with { State = typed.State with { Entries = [row], SelectedInstalled = false, SelectedLaunchable = false } });
    }
    private static KioskDirectOperatorResult Matching(KioskDirectOperatorCommand command, KioskDirectOperatorResult result, string wire, string value) =>
        wire is "set-search" or "filter-tag" ? WithText(command, result, wire, value) : WithTags(command, result, wire == "add-tag", value);
    private static KioskDirectOperatorResult RebindStage(KioskDirectOperatorCommand command, KioskDirectOperatorResult result, RustyKioskOperatorResult typed) =>
        result with { KioskResult = typed, Mutation = result.Mutation with { Stage = RustyKioskReadback.Confirms(typed.Command, command.Value, typed) ? OperatorMutationStage.Confirmed : OperatorMutationStage.PendingWearerAction } };
}
