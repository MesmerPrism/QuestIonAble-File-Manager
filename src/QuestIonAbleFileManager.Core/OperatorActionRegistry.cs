using System.Collections.ObjectModel;

namespace QuestIonAbleFileManager.Core;

public enum OperatorActionProjection
{
    SharedCoreCli,
    InteractiveOnly
}

public sealed record OperatorActionRouteDescriptor(
    string Id,
    string CliRoute,
    string CoreOperation,
    bool RequiresConfirmation,
    string ReadbackContract);

public sealed record OperatorActionDescriptor(
    string Id,
    string WpfHandler,
    OperatorActionProjection Projection,
    IReadOnlyList<OperatorActionRouteDescriptor> Routes,
    bool MutatesHeadset,
    string? InteractiveReason = null);

/// <summary>
/// Code-owned parity manifest. Tests bind every MainWindow click handler to one
/// shared Core+CLI operation or an explicit non-operation UI-only reason.
/// </summary>
public static class OperatorActionRegistry
{
    public static IReadOnlyList<OperatorActionRouteDescriptor> AgentRoutes { get; } =
        new ReadOnlyCollection<OperatorActionRouteDescriptor>(
        [
            new(
                "apk_preflight",
                "apk preflight --serial --file --json",
                "OperatorCommands.PreflightInspectedApp",
                false,
                "immutable artifact identity, exact serial/API compatibility, installed-byte match, and exported-launcher readiness"),
            new(
                "apk_deploy",
                "apk deploy --serial --file --json",
                "OperatorCommands.DeployInspectedApp",
                false,
                "exact installed base-APK bytes, resolved exported launch, and runtime observation"),
            new(
                "apk_diagnose",
                "apk diagnose --serial --file --output --json",
                "OperatorCommands.DiagnoseInspectedApp",
                false,
                "exact installed base-APK bytes plus a fixed bounded private diagnostic bundle")
        ]);

    public static IReadOnlyList<OperatorActionDescriptor> Actions { get; } =
        new ReadOnlyCollection<OperatorActionDescriptor>(
        [
            Shared("devices.refresh", "OnRefreshDevices", "devices", "OperatorCommands.DiscoverDevices", false, false),
            Shared("files.open", "OnRemoteEntryDoubleClick", "files list", "OperatorCommands.ListFiles", false, false),
            Shared("files.path", "OnGoToRemotePath", "files list", "OperatorCommands.ListFiles", false, false),
            Shared("files.up", "OnGoUp", "files list", "OperatorCommands.ListFiles", false, false),
            Shared("files.pull", "OnPullSelected", "files pull", "OperatorCommands.PullFile", false, false),
            Shared("files.push", "OnPushFile", "files push", "OperatorCommands.PushFile", true, true),
            Shared("apk.refresh", "OnRefreshPackages", "apk list", "OperatorCommands.ListPackages", false, false),
            Shared("apk.export", "OnExportPackage", "apk export", "OperatorCommands.ExportApk", false, false),
            Interactive("apk.choose", "OnBrowseInstallApk", "The native file picker only selects the typed apk install input."),
            Interactive("apk.bundle.choose", "OnBrowseInstallApkBundle", "The native folder picker only selects the typed bundle input."),
            Shared("apk.install", "OnInstallApk", "apk install", "OperatorCommands.InstallApk", true, true),
            Shared("apk.bundle.install", "OnInstallApkBundle", "apk install-bundle", "OperatorCommands.InstallApkBundle", true, true),
            Shared("apk.many.install", "OnInstallApkMany", "apk install-many", "OperatorCommands.InstallApkMany", true, true),
            Shared("apk.bundle.many.install", "OnInstallApkBundleMany", "apk install-bundle-many", "OperatorCommands.InstallApkBundleMany", true, true),
            Interactive("wifi.targets.all", "OnSelectAllWifiTargets", "This changes only local checked-list selection."),
            Interactive("wifi.targets.clear", "OnClearWifiTargets", "This changes only local checked-list selection."),
            Shared("wifi.enable", "OnEnableWifiAdb", "wifi enable", "OperatorCommands.EnableWifiAdb", true, true),
            Shared("wifi.connect", "OnConnectWifiAdb", "wifi connect", "OperatorCommands.ConnectWifiAdb", true, true),
            Shared("wifi.disconnect", "OnDisconnectWifiAdb", "wifi disconnect", "OperatorCommands.DisconnectWifiAdb", true, true),
            Interactive("kiosk.bundle.choose", "OnBrowseKioskBundle", "The native folder picker only selects the typed Kiosk install input."),
            Shared("kiosk.install", "OnInstallKiosk", "kiosk install", "OperatorCommands.InstallRustyKiosk", true, true),
            Shared("kiosk.provision", "OnProvisionKiosk", "kiosk provision", "OperatorCommands.ProvisionRustyKiosk", true, true),
            DualKiosk(
                "kiosk.refresh",
                "OnRefreshKiosk",
                "kiosk-direct status + kiosk-direct command --command status",
                "KioskDirectOperatorCommand.Status + KioskDirectOperatorCommand.Invoke(status)",
                "kiosk status",
                "OperatorCommands.InspectRustyKiosk",
                false,
                false,
                "signed Direct Link status plus matching typed Kiosk state readback",
                "DUMP-protected host-provider installation and typed Kiosk state readback"),
            Shared(
                "kiosk.direct.connect.manual",
                "OnConnectKioskDirect",
                "kiosk-direct status --credential-stdin",
                "KioskDirectOperatorCommand.Adopt",
                false,
                false,
                "one client lease confirms signed Direct Link status, completed typed Kiosk status, and signed staging inventory before WPF adoption"),
            Shared(
                "kiosk.direct.connect.usb",
                "OnConnectKioskDirectUsb",
                "kiosk-direct status --serial --product-channel --confirm-kiosk-direct-bootstrap",
                "RustyKioskUsbDirectLinkBootstrapper.ConnectAsync + KioskDirectOperatorCommand.Adopt",
                true,
                true,
                "one authorized-USB session confirms exact bootstrap identity, signed Direct Link status, completed typed Kiosk status, and signed staging inventory before WPF adoption"),
            Interactive("kiosk.direct.disconnect", "OnDisconnectKioskDirect", "This clears the current process-memory WPF session; every CLI Direct Link command already performs the same cleanup atomically on exit."),
            Shared("kiosk.direct.staging.list", "OnRefreshKioskDirectStaging", "kiosk-direct files list", "KioskDirectOperatorCommand.ListStaging", false, false),
            Shared("kiosk.direct.staging.upload", "OnUploadKioskDirectFile", "kiosk-direct files upload", "KioskDirectOperatorCommand.Upload", true, true),
            Shared("kiosk.direct.staging.download", "OnDownloadKioskDirectFile", "kiosk-direct files download", "KioskDirectOperatorCommand.Download", false, false),
            Shared("kiosk.direct.staging.delete", "OnDeleteKioskDirectFile", "kiosk-direct files delete", "KioskDirectOperatorCommand.Delete", true, true),
            Interactive("kiosk.direct.apks.choose", "OnChooseKioskDirectApks", "The native file picker only selects the typed Direct Link install input."),
            Shared("kiosk.direct.install", "OnInstallKioskDirectApks", "kiosk-direct install", "KioskDirectOperatorCommand.Install", true, true),
            Shared("kiosk.direct.install.status", "OnRefreshKioskDirectInstall", "kiosk-direct install-status", "KioskDirectOperatorCommand.InstallStatus", false, false),
            DualKioskCommand("kiosk.panel.controls", "OnKioskShowControls"),
            DualKioskCommand("kiosk.panel.apps", "OnKioskShowApps"),
            DualKioskCommand("kiosk.focus.search", "OnKioskFocusSearch"),
            DualKioskCommand("kiosk.focus.tag-editor", "OnKioskFocusTagEditor"),
            DualKioskCommand("kiosk.launch.normal", "OnKioskLaunchNormal"),
            DualKioskCommand("kiosk.launch.guarded", "OnKioskLaunchGuarded"),
            DualKioskCommand("kiosk.launch.options.read", "OnReadKioskLaunchOptions"),
            DualKioskCommand("kiosk.launch.option", "OnKioskLaunchOption"),
            DualKioskCommand("kiosk.launch.requirement.set", "OnSetKioskLaunchRequirement"),
            DualKioskCommand("kiosk.launch.pending.cancel", "OnCancelKioskPendingLaunch"),
            DualKioskCommand("kiosk.tag.add", "OnAddKioskTag"),
            DualKioskCommand("kiosk.tag.remove", "OnRemoveKioskTag"),
            DualKiosk(
                "kiosk.tags.export",
                "OnExportKioskTags",
                "kiosk-direct tags export",
                "KioskDirectOperatorCommand.ExportTags",
                "kiosk tags export",
                "OperatorCommands.PullRustyKioskTags",
                false,
                false,
                "signed bounded rusty.kiosk.app_tags.v1-or-v2 document readback",
                "bounded provider-chunk readback with exact size, SHA-256, and schema validation"),
            DualKiosk(
                "kiosk.tags.import",
                "OnImportKioskTags",
                "kiosk-direct tags import",
                "KioskDirectOperatorCommand.ImportTags",
                "kiosk tags import",
                "OperatorCommands.PushRustyKioskTags",
                true,
                true,
                "signed strict v1-or-v2 replacement size/SHA readback plus typed reload result",
                "provider chunk strict v1-or-v2 commit size/SHA readback plus typed reload result"),
            DualKioskCommand("kiosk.wifi.request", "OnKioskRequestWifiAdb"),
            DualKioskCommand("kiosk.wifi.disable", "OnKioskDisableWifiAdb"),
            DualKioskCommand("kiosk.wifi.boot.enable", "OnKioskEnableAutoWifi"),
            DualKioskCommand("kiosk.wifi.boot.disable", "OnKioskDisableAutoWifi"),
            DualKioskCommand("kiosk.accessibility.enable", "OnKioskEnableAccessibility"),
            DualKioskCommand("kiosk.accessibility.disable", "OnKioskDisableAccessibility"),
            DualKioskCommand("kiosk.passthrough.natural", "OnKioskPassthroughNatural"),
            DualKioskCommand("kiosk.passthrough.contour", "OnKioskPassthroughContour"),
            Interactive("kiosk.open-adb-installer", "OnOpenAdbApkInstaller", "This only switches the visible WPF tab."),
            Shared("device.refresh", "OnRefreshQuestControls", "device status", "OperatorCommands.ReadQuestControls", false, false),
            Shared("device.awake.enable", "OnEnableKeepAwake", "device keep-awake --on", "OperatorCommands.SetQuestKeepAwake", true, true),
            Shared("device.awake.disable", "OnDisableKeepAwake", "device keep-awake --off", "OperatorCommands.SetQuestKeepAwake", true, true),
            Shared("device.performance.set", "OnApplyPerformance", "device performance", "OperatorCommands.SetQuestPerformance", true, true),
            Shared("device.performance.clear", "OnClearPerformance", "device performance --clear", "OperatorCommands.SetQuestPerformance", true, true),
            Shared("fleet.status", "OnRefreshFleetInstaller", "fleet status", "OperatorCommands.FleetInstallStatus", false, false),
            Shared("fleet.install", "OnInstallFleet", "fleet install", "OperatorCommands.FleetInstall", false, true),
            Shared("profile.list", "OnRefreshConnectivityProfiles", "connectivity-profile list", "OperatorCommands.ListQuestConnectivityProfiles", false, false),
            Shared("profile.status", "OnCheckConnectivityProfileStatus", "connectivity-profile status", "OperatorCommands.QuestConnectivityProfileStatus", false, false),
            Interactive("profile.choose", "OnChooseConnectivityProfile", "The native file picker only selects the private profile input."),
            Shared("profile.import", "OnImportConnectivityProfile", "connectivity-profile import", "OperatorCommands.ImportQuestConnectivityProfileFile", false, true),
            Shared("profile.bind-kiosk", "OnSaveEnteredKioskLinkForFleet", "connectivity-profile import --stdin", "OperatorCommands.ImportQuestConnectivityProfileStdin", false, true),
            Shared("profile.revoke", "OnRevokeConnectivityProfile", "connectivity-profile revoke", "OperatorCommands.RevokeQuestConnectivityProfile", false, true)
        ]);

    private static OperatorActionDescriptor Shared(
        string id,
        string handler,
        string cli,
        string core,
        bool mutates,
        bool confirmation,
        string readbackContract = "typed Core result and operation-specific effective-state readback") =>
        new(
            id,
            handler,
            OperatorActionProjection.SharedCoreCli,
            [new OperatorActionRouteDescriptor(
                RouteId(core),
                cli,
                core,
                confirmation,
                readbackContract)],
            mutates);

    private static string RouteId(string coreOperation) =>
        coreOperation.StartsWith("KioskDirectOperatorCommand.", StringComparison.Ordinal)
            ? "direct_link"
            : coreOperation.StartsWith("RustyKioskUsbDirectLinkBootstrapper.", StringComparison.Ordinal)
                ? "authorized_usb_bootstrap"
                : coreOperation.Contains("RustyKiosk", StringComparison.Ordinal)
                    ? "adb_host_provider"
                    : "shared_core";

    private static OperatorActionDescriptor DualKioskCommand(string id, string handler) =>
        DualKiosk(
            id,
            handler,
            "kiosk-direct command",
            "KioskDirectOperatorCommand.Invoke",
            "kiosk command",
            "OperatorCommands.InvokeRustyKiosk",
            true,
            true,
            "signed typed Kiosk result accepted only when RustyKioskReadback.Confirms matches",
            "DUMP-protected typed Kiosk result accepted only when RustyKioskReadback.Confirms matches");

    private static OperatorActionDescriptor DualKiosk(
        string id,
        string handler,
        string directCli,
        string directCore,
        string adbCli,
        string adbCore,
        bool mutates,
        bool confirmation,
        string directReadback,
        string adbReadback) =>
        new(
            id,
            handler,
            OperatorActionProjection.SharedCoreCli,
            [
                new OperatorActionRouteDescriptor(
                    "direct_link",
                    directCli,
                    directCore,
                    confirmation,
                    directReadback),
                new OperatorActionRouteDescriptor(
                    "adb_host_provider",
                    adbCli,
                    adbCore,
                    confirmation,
                    adbReadback)
            ],
            mutates);

    private static OperatorActionDescriptor Interactive(string id, string handler, string reason) =>
        new(id, handler, OperatorActionProjection.InteractiveOnly, [], false, reason);
}
