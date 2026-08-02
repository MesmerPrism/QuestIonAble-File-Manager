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
                Assert.NotEmpty(action.Routes);
                Assert.Equal(
                    action.Routes.Count,
                    action.Routes.Select(route => route.Id).Distinct(StringComparer.Ordinal).Count());
                Assert.All(action.Routes, route =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(route.CliRoute));
                    Assert.False(string.IsNullOrWhiteSpace(route.CoreOperation));
                    Assert.False(string.IsNullOrWhiteSpace(route.ReadbackContract));
                    if (action.MutatesHeadset) Assert.True(route.RequiresConfirmation);
                });
            });
        Assert.All(
            OperatorActionRegistry.Actions.Where(action => action.Projection == OperatorActionProjection.InteractiveOnly),
            action =>
            {
                Assert.Empty(action.Routes);
                Assert.False(string.IsNullOrWhiteSpace(action.InteractiveReason));
            });
    }

    [Fact]
    public void WpfSessionDisconnectIsNotAdvertisedAsANonexistentCliRoute()
    {
        var disconnect = Assert.Single(
            OperatorActionRegistry.Actions,
            action => action.Id == "kiosk.direct.disconnect");

        Assert.Equal(OperatorActionProjection.InteractiveOnly, disconnect.Projection);
        Assert.Empty(disconnect.Routes);
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
            .SelectMany(action => action.Routes)
            .Where(route =>
                route.CoreOperation.StartsWith("KioskDirectOperatorCommand.", StringComparison.Ordinal) &&
                !route.CoreOperation.Contains(" + ", StringComparison.Ordinal))
            .Select(route => route.CoreOperation)
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
    public void DynamicKioskActionsDeclareAndImplementDirectAndAdbProviderRoutes()
    {
        var dynamic = OperatorActionRegistry.Actions.Where(action =>
            action.Id == "kiosk.refresh" ||
            action.Id.StartsWith("kiosk.launch.", StringComparison.Ordinal) ||
            action.Id.StartsWith("kiosk.tag.", StringComparison.Ordinal) ||
            action.Id.StartsWith("kiosk.tags.", StringComparison.Ordinal) ||
            action.Id.StartsWith("kiosk.wifi.", StringComparison.Ordinal) ||
            action.Id.StartsWith("kiosk.accessibility.", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(dynamic);
        Assert.All(dynamic, action =>
        {
            Assert.Equal(2, action.Routes.Count);
            Assert.Contains(action.Routes, route => route.Id == "direct_link");
            Assert.Contains(action.Routes, route => route.Id == "adb_host_provider");
            Assert.All(action.Routes, route =>
            {
                Assert.Equal(action.MutatesHeadset, route.RequiresConfirmation);
                Assert.Contains("readback", route.ReadbackContract, StringComparison.OrdinalIgnoreCase);
            });
        });

        var root = FindRepositoryRoot();
        var wpf = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.App",
            "MainWindow.xaml.cs"));
        var commandRoute = SourceMethod(wpf, "private async Task RunKioskCommandAsync", "private async Task SetKeepAwakeAsync");
        Assert.Contains("KioskDirectOperatorCommand.Invoke(", commandRoute, StringComparison.Ordinal);
        Assert.Contains("OperatorCommands.InvokeRustyKiosk(", commandRoute, StringComparison.Ordinal);
        Assert.Contains("RustyKioskReadback.Confirms(", commandRoute, StringComparison.Ordinal);

        var refreshRoute = SourceMethod(wpf, "private async Task RefreshKioskAsync", "private async Task RefreshQuestControlsAsync");
        Assert.Contains("KioskDirectOperatorCommand.Status()", refreshRoute, StringComparison.Ordinal);
        Assert.Contains("KioskDirectOperatorCommand.Invoke(", refreshRoute, StringComparison.Ordinal);
        Assert.Contains("OperatorCommands.InspectRustyKiosk(", refreshRoute, StringComparison.Ordinal);
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
    public void WpfClearsBothCredentialProjectionsOnTimeoutDeactivationAndProfileOutcome()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.App",
            "MainWindow.xaml.cs"));
        var clearMethod = SourceMethod(
            source,
            "private void ClearManualPairingInput()",
            "private async void OnClosing");
        Assert.Contains("RemaskPairingCode();", clearMethod, StringComparison.Ordinal);
        Assert.Contains("KioskDirectPairingCodeBox.Clear();", clearMethod, StringComparison.Ordinal);

        var remaskMethod = SourceMethod(
            source,
            "private void RemaskPairingCode()",
            "private void ClearManualPairingInput()");
        Assert.Contains("KioskDirectPairingCodeRevealText.Text = string.Empty;", remaskMethod, StringComparison.Ordinal);

        Assert.Contains("_pairingCodeRevealTimer.Tick += (_, _) => ClearManualPairingInput();", source, StringComparison.Ordinal);
        var deactivationMethod = SourceMethod(
            source,
            "private void OnDeactivated",
            "private void RemaskPairingCode()");
        Assert.Contains("ClearManualPairingInput();", deactivationMethod, StringComparison.Ordinal);
        var profileMethod = SourceMethod(
            source,
            "private async void OnSaveEnteredKioskLinkForFleet",
            "private async Task RefreshConnectivityProfilesAsync");
        Assert.Contains("ClearManualPairingInput();", profileMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfAdoptsDirectSessionOnlyAfterAllRequiredReadbacks()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.App",
            "MainWindow.xaml.cs"));
        var method = SourceMethod(
            source,
            "private async Task AdoptKioskDirectClientAsync",
            "private async Task DisconnectKioskDirectAsync");

        var status = method.IndexOf("KioskDirectOperatorCommand.Status()", StringComparison.Ordinal);
        var kiosk = method.IndexOf("KioskDirectOperatorCommand.Invoke(", StringComparison.Ordinal);
        var staging = method.IndexOf("KioskDirectOperatorCommand.ListStaging()", StringComparison.Ordinal);
        var publish = method.IndexOf("_rustyKioskDirectClient = client;", StringComparison.Ordinal);
        Assert.True(status >= 0 && kiosk > status && staging > kiosk && publish > staging);
        Assert.Contains("_rustyKioskDirectClient = null;", method, StringComparison.Ordinal);
        Assert.Contains("KioskDirectStatusText.Text = \"Direct link: not connected\";", method, StringComparison.Ordinal);
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
    public void CliJsonFailureClosesBeforeOneSanitizedJsonAndDoesNotUsePlaintextStderr()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.Cli",
            "Program.cs"));
        var runMethod = SourceMethod(
            source,
            "private static async Task<int> RunKioskDirectAsync",
            "private static void RejectDirectCredentialArguments");
        var close = runMethod.IndexOf("await lease.CloseAsync()", StringComparison.Ordinal);
        var write = runMethod.IndexOf("WriteDirectFailure(lease, exception);", StringComparison.Ordinal);
        Assert.True(close >= 0 && write > close);
        Assert.DoesNotContain("Console.Error", runMethod, StringComparison.Ordinal);

        var failureMethod = SourceMethod(
            source,
            "private static void WriteDirectFailure",
            "private static async Task<int> CompleteDirectAsync");
        Assert.Contains("succeeded = false", failureMethod, StringComparison.Ordinal);
        Assert.Contains("cleanup = lease?.CleanupReceipt", failureMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", failureMethod, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(failureMethod, "WriteJson\\(").Cast<Match>());
    }

    [Fact]
    public void SharedBootstrapFixturePinsNoSecretStatusAndGenerationBoundCleanup()
    {
        var root = FindRepositoryRoot();
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            root,
            "references",
            "rusty-kiosk-direct-usb-bootstrap-contract.v2.json")));
        var rootElement = fixture.RootElement;

        Assert.Equal("rusty.kiosk.host_operator.v4", rootElement.GetProperty("host_provider_schema").GetString());
        Assert.Equal("rusty.kiosk.direct_usb_bootstrap.v2", rootElement.GetProperty("bootstrap_result_schema").GetString());
        Assert.Equal("rusty.kiosk.direct_operator.v2", rootElement.GetProperty("direct_operator_schema").GetString());
        Assert.Equal("content-provider-arg", rootElement.GetProperty("enable").GetProperty("operation_id_transport").GetString());
        Assert.Equal("long", rootElement.GetProperty("disable").GetProperty("extras").GetProperty("expected_bridge_generation").GetString());
        Assert.False(rootElement.GetProperty("status_returns_secret").GetBoolean());
        Assert.Equal(
            "direct_enabled=false-and-direct_running=false-and-current-generation-stop-applied",
            rootElement.GetProperty("status").GetProperty("disable_confirmed_when").GetString());
        Assert.Equal("direct-recover-disable",
            rootElement.GetProperty("provider_methods").GetProperty("recover_disable").GetString());
        Assert.Equal("idempotent-operation-id-only-redispatch-without-credentials",
            rootElement.GetProperty("recover_disable").GetProperty("pending_stop_retry").GetString());
        Assert.Equal("same-opened-handle-count-and-digest-verified-before-packageinstaller-commit",
            rootElement.GetProperty("direct_install").GetProperty("copy_rule").GetString());
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

    private static string SourceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source[start..end];
    }
}
