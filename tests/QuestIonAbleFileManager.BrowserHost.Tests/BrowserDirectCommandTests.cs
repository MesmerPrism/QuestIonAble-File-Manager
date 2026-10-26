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
            RustyKioskCommand.LaunchNormal, RustyKioskCommand.LaunchKiosk };
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
}
