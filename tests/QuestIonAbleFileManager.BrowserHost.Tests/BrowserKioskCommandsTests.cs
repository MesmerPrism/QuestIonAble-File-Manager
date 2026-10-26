using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserKioskCommandsTests
{
    internal const string Serial = "fixture-target";
    internal const string Key = "fixture-app";
    internal const string PackageName = "com.example.fixture";
    internal static readonly string Id = "00000000-0000-4000-8000-000000000001";
    internal static readonly string CatalogId = "00000000-0000-4000-8000-000000000002";
    internal static readonly string ProviderId = "pc-00000000000040008000000000000001";
    internal static byte[] Body(string action, string? id = null, string channel = "labs", string? serial = null,
        string? catalog = null, string? key = null, string? provider = null, string expected = "status")
    {
        var fields = new Dictionary<string, object?> { ["requestId"] = id ?? Guid.NewGuid().ToString("D"), ["action"] = action,
            ["serial"] = serial ?? Serial, ["productChannel"] = channel };
        if (action is "kiosk.catalog.open" or "kiosk.select") fields["confirmed"] = true;
        if (action == "kiosk.select") { fields["catalogRequestId"] = catalog ?? CatalogId; fields["key"] = key ?? Key; }
        if (action == "kiosk.original-result")
        {
            fields["providerRequestId"] = provider ?? ProviderId; fields["expectedCommand"] = expected;
            if (expected == "select") fields["key"] = key ?? Key;
        }
        return JsonSerializer.SerializeToUtf8Bytes(fields);
    }

    [Theory]
    [InlineData("stable", "kiosk.installation", "InspectRustyKioskInstallation")]
    [InlineData("labs", "kiosk.installation", "InspectRustyKioskInstallation")]
    [InlineData("stable", "kiosk.catalog.open", "OpenRustyKioskCatalog")]
    [InlineData("labs", "kiosk.catalog.open", "OpenRustyKioskCatalog")]
    [InlineData("stable", "kiosk.select", "SelectRustyKioskApp")]
    [InlineData("labs", "kiosk.select", "SelectRustyKioskApp")]
    [InlineData("stable", "kiosk.original-result", "ReadRustyKioskRequest")]
    [InlineData("labs", "kiosk.original-result", "ReadRustyKioskRequest")]
    public void ClosedActionsPreserveExactChannelTargetAndDerivedProviderIdentity(string channel, string action, string kind)
    {
        var request = BrowserKioskCommands.Parse(Body(action, Id, channel));
        var command = request.ToOperatorCommand();
        Assert.Equal(kind, command.Kind.ToString()); Assert.Equal(Serial, command.Serial);
        Assert.Equal(channel, command.RustyKioskProduct!.WireName);
        Assert.Equal(action is "kiosk.catalog.open" or "kiosk.select", request.Mutation);
        Assert.Equal(action == "kiosk.installation" ? null : ProviderId, command.RustyKioskProviderRequestId);
        if (request.Mutation) Assert.True(command.OperatorConfirmed);
    }

    [Theory]
    [InlineData("productChannel", "Labs")]
    [InlineData("productChannel", " labs")]
    [InlineData("productChannel", "labs ")]
    [InlineData("serial", "")]
    [InlineData("requestId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("confirmed", false)]
    [InlineData("confirmed", "true")]
    [InlineData("endpoint", "http://example.invalid")]
    [InlineData("providerRequestId", "pc-00000000000040008000000000000001")]
    [InlineData("command", "launch-normal")]
    [InlineData("shell", "id")]
    public void MutationRejectsCrossedMissingOrWidenedFields(string field, object value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Body("kiosk.catalog.open"))!;
        node[field] = JsonSerializer.SerializeToNode(value);
        Assert.Throws<BrowserRequestException>(() => BrowserKioskCommands.Parse(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Theory]
    [InlineData(" fixture-app")]
    [InlineData("fixture-app ")]
    [InlineData("fixture\napp")]
    [InlineData("\t")]
    public void SelectKeysCannotChangeUnderCoreValueNormalization(string key) =>
        Assert.Throws<BrowserRequestException>(() => BrowserKioskCommands.Parse(Body("kiosk.select", key: key)));

    [Fact]
    public void BoundsDuplicatesAndClosedOriginalResultFactsAreStrict()
    {
        Assert.Throws<BrowserRequestException>(() => BrowserKioskCommands.Parse(new byte[4097]));
        Assert.Throws<BrowserRequestException>(() => BrowserKioskCommands.Parse(Body("kiosk.select", key: new string('k', 161))));
        var valid = Encoding.UTF8.GetString(Body("kiosk.catalog.open"));
        Assert.Throws<BrowserRequestException>(() => BrowserKioskCommands.Parse(Encoding.UTF8.GetBytes(valid[..^1] + ",\"confirmed\":true}")));
        foreach (var action in new[] { "kiosk.launch", "kiosk.direct", "kiosk.tags", "kiosk.provision" })
            Assert.Throws<BrowserRequestException>(() => BrowserKioskCommands.Parse(Body(action)));
        foreach (var provider in new[] { "invented", "pc-00000000000000000000000000000000", "pc-0000000000004000800000000000000A" })
            Assert.Throws<BrowserRequestException>(() => BrowserKioskCommands.Parse(Body("kiosk.original-result", provider: provider)));
        Assert.Throws<BrowserRequestException>(() => BrowserKioskCommands.Parse(Body("kiosk.original-result", expected: "launch-normal")));
        Assert.Equal(RustyKioskCommand.Select, BrowserKioskCommands.Parse(Body("kiosk.original-result", expected: "select")).ExpectedCommand);
    }
}
