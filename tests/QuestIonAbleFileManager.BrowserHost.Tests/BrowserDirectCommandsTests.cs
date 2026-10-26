using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectCommandsTests
{
    internal const string Id = "00000000-0000-4000-8000-000000000001";
    internal static byte[] Body(string action = "kiosk.direct.connect-usb", string id = Id, string? connection = null) =>
        JsonSerializer.SerializeToUtf8Bytes(action == "kiosk.direct.connect-usb"
            ? new Dictionary<string, object> { ["requestId"] = id, ["action"] = action, ["serial"] = "USB_TARGET", ["productChannel"] = "stable", ["confirmed"] = true }
            : new Dictionary<string, object> { ["requestId"] = id, ["action"] = action, ["connectionId"] = connection ?? Id }.Concat(
                action == "kiosk.direct.close" ? new Dictionary<string, object> { ["confirmed"] = true } : []).ToDictionary());

    [Theory]
    [InlineData("kiosk.direct.connect-usb")]
    [InlineData("kiosk.direct.status")]
    [InlineData("kiosk.direct.close")]
    public void ClosedActionsDeriveOriginalUsbIdentity(string action)
    {
        var request = BrowserDirectCommands.Parse(Body(action));
        Assert.Equal(action, request.Action); Assert.Equal(Id, request.RequestId);
        Assert.Equal("usb_00000000000040008000000000000001", request.UsbOperationId);
        Assert.True(BrowserDirectCommands.IsDirectAction(Body(action)));
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("pairingCode")]
    [InlineData("operationId")]
    [InlineData("command")]
    [InlineData("localPath")]
    [InlineData("sessionId")]
    public void RejectsEveryUnownedField(string field)
    {
        var body = Encoding.UTF8.GetString(Body()).Replace("}", $",\"{field}\":\"private\"}}");
        Assert.Equal("fields_invalid", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Encoding.UTF8.GetBytes(body))).Code);
    }

    [Theory]
    [InlineData("\"confirmed\":true", "\"confirmed\":false")]
    [InlineData("\"productChannel\":\"stable\"", "\"productChannel\":\"Stable\"")]
    [InlineData(Id, "00000000-0000-0000-0000-000000000000")]
    [InlineData("\"action\":", "\"action\":\"kiosk.direct.status\",\"action\":")]
    public void InvalidApprovalScopeAndDuplicateReject(string before, string after) =>
        Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Body()).Replace(before, after))));

    [Fact]
    public void BoundsMalformedAndDeepBodies()
    {
        foreach (var body in new[] { "", "[]", "{", new string(' ',4097), "{\"action\":{\"a\":{\"b\":{\"c\":{}}}}}" })
            Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Encoding.UTF8.GetBytes(body)));
        Assert.False(BrowserDirectCommands.IsDirectAction("{"u8.ToArray()));
    }
}
