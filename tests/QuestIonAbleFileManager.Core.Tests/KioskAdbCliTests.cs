using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class KioskAdbCliTests
{
    [Theory]
    [InlineData("installation", "kiosk_installation")]
    [InlineData("catalog-open", "kiosk_catalog_open")]
    [InlineData("select", "kiosk_select")]
    [InlineData("original-result", "kiosk_original_result")]
    public void NewRoutesAreExplicitlyClassifiedWithoutChangingOldStatus(string action, string route)
    {
        Assert.True(CliApplication.TryClassifyAgentRoute(["kiosk", action, "--serial", "fixture-target", "--product-channel", "stable", "--json"], out var actual));
        Assert.Equal(route, actual);
        Assert.False(CliApplication.TryClassifyAgentRoute(["kiosk", "status", "--serial", "fixture-target", "--json"], out _));
    }

    [Theory]
    [InlineData("catalog-open")]
    [InlineData("select")]
    [InlineData("original-result")]
    public async Task InvalidNewRequestsReturnOneSanitizedJsonBeforeAdb(string action)
    {
        var savedOut = Console.Out; var savedError = Console.Error; using var output = new StringWriter(); using var error = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            var exit = await CliApplication.RunAsync(["kiosk", action, "--serial", "PRIVATE-DO-NOT-EMIT", "--request-id", "private-invalid", "--json"]);
            Assert.Equal(2, exit);
        }
        finally { Console.SetOut(savedOut); Console.SetError(savedError); }
        Assert.Equal("", error.ToString()); Assert.DoesNotContain("PRIVATE-DO-NOT-EMIT", output.ToString());
        Assert.DoesNotContain("private-invalid", output.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("questionable.file_manager.kiosk_adb_result.v1", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal("input_rejected", json.RootElement.GetProperty("failure").GetProperty("code").GetString());
    }

    [Fact]
    public void TypedCliRoundTripsOnlyTheNewClosedFactoriesAndRequiresExactChannel()
    {
        const string id = "pc-00000000000040008000000000000001";
        var product = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
        foreach (var original in new[]
        {
            OperatorCommands.InspectRustyKioskInstallation("fixture-target", product),
            OperatorCommands.OpenRustyKioskCatalog("fixture-target", id, true, product),
            OperatorCommands.SelectRustyKioskApp("fixture-target", "package:example.app", id, true, product),
            OperatorCommands.SelectRustyKioskApp("fixture-target", "--opaque-key", id, true, product),
            OperatorCommands.ReadRustyKioskRequest("fixture-target", id, RustyKioskCommand.Status, product: product),
            OperatorCommands.ReadRustyKioskRequest("fixture-target", id, RustyKioskCommand.Select, "package:example.app", product)
        })
        {
            var parsed = OperatorCommands.ParseKioskAdbCliArguments([.. original.CliArguments, "--json"]);
            Assert.Equal(original.Kind, parsed.Kind); Assert.Equal(original.CliArguments, parsed.CliArguments);
            Assert.Equal(original.RustyKioskProviderRequestId, parsed.RustyKioskProviderRequestId); Assert.Equal(product, parsed.RustyKioskProduct);
            Assert.True((int)parsed.Kind > (int)OperatorCommandKind.ObserveDeviceActorIdentity);
        }
        Assert.True(OperatorActionRegistry.AgentRoutes.Single(route => route.Id == "kiosk_catalog_open").RequiresConfirmation);
        Assert.True(OperatorActionRegistry.AgentRoutes.Single(route => route.Id == "kiosk_select").RequiresConfirmation);
        var valid = OperatorCommands.InspectRustyKioskInstallation("fixture-target").CliArguments;
        foreach (var extra in new[] { new[] { "--product-channel", "labs" }, new[] { "--path", "private" }, new[] { "--key", "app" } })
            Assert.Throws<ArgumentException>(() => OperatorCommands.ParseKioskAdbCliArguments([.. valid, .. extra]));
        Assert.Throws<ArgumentException>(() => OperatorCommands.ParseKioskAdbCliArguments(["kiosk", "installation", "--serial", "fixture-target"]));
        Assert.Throws<ArgumentException>(() => OperatorCommands.ParseKioskAdbCliArguments(["kiosk", "installation", "--serial", "fixture-target", "--product-channel", "Stable"]));
    }
}
