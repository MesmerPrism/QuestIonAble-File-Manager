using QuestIonAbleFileManager.Core;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class OperatorActionRegistryTests
{
    [Fact]
    public void RegistryCoversEveryWpfClickActionExactlyOnce()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.App",
            "MainWindow.xaml"));
        var handlers = Regex.Matches(
                xaml,
                "(?:Click|MouseDoubleClick)=\"(?<handler>On[A-Za-z0-9]+)\"",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["handler"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var registered = OperatorActionRegistry.Actions
            .Select(action => action.WpfHandler)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(handlers, registered);
        Assert.Equal(
            OperatorActionRegistry.Actions.Count,
            OperatorActionRegistry.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            OperatorActionRegistry.Actions.Count,
            OperatorActionRegistry.Actions.Select(action => action.WpfHandler).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            OperatorActionRegistry.Actions.Where(action => action.Projection == OperatorActionProjection.SharedCoreCli),
            action =>
            {
                Assert.False(string.IsNullOrWhiteSpace(action.CliRoute));
                Assert.False(string.IsNullOrWhiteSpace(action.CoreOperation));
                if (action.MutatesHeadset) Assert.True(action.RequiresConfirmation);
            });
        Assert.All(
            OperatorActionRegistry.Actions.Where(action => action.Projection == OperatorActionProjection.InteractiveOnly),
            action => Assert.False(string.IsNullOrWhiteSpace(action.InteractiveReason)));
    }

    [Fact]
    public void WpfSessionDisconnectIsNotAdvertisedAsANonexistentCliRoute()
    {
        var disconnect = Assert.Single(
            OperatorActionRegistry.Actions,
            action => action.Id == "kiosk.direct.disconnect");

        Assert.Equal(OperatorActionProjection.InteractiveOnly, disconnect.Projection);
        Assert.Null(disconnect.CliRoute);
        Assert.Contains("CLI", disconnect.InteractiveReason, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySharedDirectWpfActionAndCliRouteUseTheSameCoreFactory()
    {
        var root = FindRepositoryRoot();
        var wpf = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.App",
            "MainWindow.xaml.cs"));
        var cli = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.Cli",
            "Program.cs"));
        var factories = OperatorActionRegistry.Actions
            .Where(action =>
                action.Projection == OperatorActionProjection.SharedCoreCli &&
                action.CoreOperation?.StartsWith("KioskDirectOperatorCommand.", StringComparison.Ordinal) == true)
            .Select(action => action.CoreOperation!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(factories);
        foreach (var factory in factories)
        {
            Assert.Contains(factory + "(", wpf, StringComparison.Ordinal);
            Assert.Contains(factory + "(", cli, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CliHelpUsesOnlyStdinOrAuthorizedUsbAuthentication()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.Cli",
            "Program.cs"));
        var helpStart = program.IndexOf("private static void WriteHelp()", StringComparison.Ordinal);
        var help = program[helpStart..];

        Assert.Contains("--credential-stdin", help, StringComparison.Ordinal);
        Assert.Contains("--confirm-kiosk-direct-bootstrap", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--pairing-code", help, StringComparison.Ordinal);
        Assert.DoesNotContain("RUSTY_KIOSK_PAIRING_CODE", help, StringComparison.Ordinal);
        Assert.DoesNotContain("kiosk-direct disconnect", help, StringComparison.Ordinal);
    }

    [Fact]
    public void CliSourceRejectsPairingCredentialAndSecretArgumentNamesBeforeDispatch()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.Cli",
            "Program.cs"));
        var runStart = program.IndexOf(
            "private static async Task<int> RunKioskDirectAsync",
            StringComparison.Ordinal);
        var rejectCall = program.IndexOf(
            "RejectDirectCredentialArguments(arguments);",
            runStart,
            StringComparison.Ordinal);
        var clientCreation = program.IndexOf(
            "CreateKioskDirectClientAsync(arguments)",
            runStart,
            StringComparison.Ordinal);
        var rejectStart = program.IndexOf(
            "private static void RejectDirectCredentialArguments",
            StringComparison.Ordinal);
        var rejectEnd = program.IndexOf(
            "private static async Task<KioskDirectClientLease>",
            rejectStart,
            StringComparison.Ordinal);
        var rejectMethod = program[rejectStart..rejectEnd];

        Assert.True(rejectCall >= 0 && clientCreation > rejectCall);
        Assert.Contains("pairing", rejectMethod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credential", rejectMethod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secret", rejectMethod, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--credential-stdin", rejectMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfExplicitDisconnectClearsUnsubmittedCredentialBeforeTransportCleanup()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.App",
            "MainWindow.xaml.cs"));
        var methodStart = source.IndexOf(
            "private async Task DisconnectKioskDirectAsync(bool updateUi)",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void OnKioskDirectRevealChanged",
            methodStart,
            StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        var clearIndex = method.IndexOf("ClearManualPairingInput();", StringComparison.Ordinal);
        var cleanupIndex = method.IndexOf("usb.CloseAsync()", StringComparison.Ordinal);
        Assert.True(clearIndex >= 0 && cleanupIndex > clearIndex);
    }

    [Fact]
    public void CliCompletesUsbCleanupBeforeWritingItsOnlyFinalResult()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.Cli",
            "Program.cs"));
        var methodStart = source.IndexOf(
            "private static async Task<int> CompleteDirectAsync(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private static int DirectExitCode",
            methodStart,
            StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        var cleanupIndex = method.IndexOf("lease.CloseAsync()", StringComparison.Ordinal);
        var outputIndex = method.IndexOf("WriteDirectResult(result, lease, json)", StringComparison.Ordinal);
        Assert.True(cleanupIndex >= 0 && outputIndex > cleanupIndex);
        Assert.Contains("CleanupUnknown", method, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedBootstrapFixturePinsNoSecretStatusAndGenerationBoundCleanup()
    {
        var root = FindRepositoryRoot();
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            root,
            "references",
            "rusty-kiosk-direct-usb-bootstrap-contract.v1.json")));
        var rootElement = fixture.RootElement;

        Assert.Equal("rusty.kiosk.host_operator.v3", rootElement.GetProperty("host_provider_schema").GetString());
        Assert.Equal("content-provider-arg", rootElement.GetProperty("enable").GetProperty("operation_id_transport").GetString());
        Assert.Equal("long", rootElement.GetProperty("disable").GetProperty("extras").GetProperty("expected_bridge_generation").GetString());
        Assert.False(rootElement.GetProperty("status_returns_secret").GetBoolean());
        Assert.Equal(
            "direct_enabled=false-and-direct_running=false",
            rootElement.GetProperty("status").GetProperty("disable_confirmed_when").GetString());
        Assert.False(rootElement.GetProperty("persistent_pairing_code_exported").GetBoolean());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "QuestIonAbleFileManager.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the File Manager repository root.");
    }
}
