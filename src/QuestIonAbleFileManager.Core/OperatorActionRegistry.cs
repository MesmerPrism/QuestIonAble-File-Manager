using System.Collections.ObjectModel;

namespace QuestIonAbleFileManager.Core;

public enum OperatorActionProjection
{
    SharedCoreCli,
    InteractiveOnly
}

public sealed record OperatorActionDescriptor(
    string Id,
    string WpfHandler,
    OperatorActionProjection Projection,
    string? CliRoute,
    string? CoreOperation,
    bool MutatesHeadset,
    bool RequiresConfirmation,
    string? InteractiveReason = null);

/// <summary>
/// Code-owned parity manifest. Tests bind every MainWindow click handler to one
/// shared Core+CLI operation or an explicit non-operation UI-only reason.
/// </summary>
public static class OperatorActionRegistry
{
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
            Shared("kiosk.refresh", "OnRefreshKiosk", "kiosk status", "OperatorCommands.InspectRustyKiosk", false, false),
            Shared("kiosk.direct.connect.manual", "OnConnectKioskDirect", "kiosk-direct status --credential-stdin", "KioskDirectOperatorCommand.Status", false, false),
            Shared("kiosk.direct.connect.usb", "OnConnectKioskDirectUsb", "kiosk-direct status --serial --product-channel --confirm-kiosk-direct-bootstrap", "RustyKioskUsbDirectLinkBootstrapper.ConnectAsync", true, true),
            Interactive("kiosk.direct.disconnect", "OnDisconnectKioskDirect", "This clears the current process-memory WPF session; every CLI Direct Link command already performs the same cleanup atomically on exit."),
            Shared("kiosk.direct.staging.list", "OnRefreshKioskDirectStaging", "kiosk-direct files list", "KioskDirectOperatorCommand.ListStaging", false, false),
            Shared("kiosk.direct.staging.upload", "OnUploadKioskDirectFile", "kiosk-direct files upload", "KioskDirectOperatorCommand.Upload", true, true),
            Shared("kiosk.direct.staging.download", "OnDownloadKioskDirectFile", "kiosk-direct files download", "KioskDirectOperatorCommand.Download", false, false),
            Shared("kiosk.direct.staging.delete", "OnDeleteKioskDirectFile", "kiosk-direct files delete", "KioskDirectOperatorCommand.Delete", true, true),
            Interactive("kiosk.direct.apks.choose", "OnChooseKioskDirectApks", "The native file picker only selects the typed Direct Link install input."),
            Shared("kiosk.direct.install", "OnInstallKioskDirectApks", "kiosk-direct install", "KioskDirectOperatorCommand.Install", true, true),
            Shared("kiosk.direct.install.status", "OnRefreshKioskDirectInstall", "kiosk-direct install-status", "KioskDirectOperatorCommand.InstallStatus", false, false),
            Shared("kiosk.launch.normal", "OnKioskLaunchNormal", "kiosk-direct command", "KioskDirectOperatorCommand.Invoke", true, true),
            Shared("kiosk.launch.guarded", "OnKioskLaunchGuarded", "kiosk-direct command", "KioskDirectOperatorCommand.Invoke", true, true),
            Shared("kiosk.tag.add", "OnAddKioskTag", "kiosk-direct command", "KioskDirectOperatorCommand.Invoke", true, true),
            Shared("kiosk.tag.remove", "OnRemoveKioskTag", "kiosk-direct command", "KioskDirectOperatorCommand.Invoke", true, true),
            Shared("kiosk.tags.export", "OnExportKioskTags", "kiosk-direct tags export", "KioskDirectOperatorCommand.ExportTags", false, false),
            Shared("kiosk.tags.import", "OnImportKioskTags", "kiosk-direct tags import", "KioskDirectOperatorCommand.ImportTags", true, true),
            Shared("kiosk.wifi.request", "OnKioskRequestWifiAdb", "kiosk-direct command", "KioskDirectOperatorCommand.Invoke", true, true),
            Shared("kiosk.wifi.disable", "OnKioskDisableWifiAdb", "kiosk-direct command", "KioskDirectOperatorCommand.Invoke", true, true),
            Shared("kiosk.wifi.boot.enable", "OnKioskEnableAutoWifi", "kiosk-direct command", "KioskDirectOperatorCommand.Invoke", true, true),
            Shared("kiosk.wifi.boot.disable", "OnKioskDisableAutoWifi", "kiosk-direct command", "KioskDirectOperatorCommand.Invoke", true, true),
            Shared("kiosk.accessibility.enable", "OnKioskEnableAccessibility", "kiosk-direct command", "KioskDirectOperatorCommand.Invoke", true, true),
            Shared("kiosk.accessibility.disable", "OnKioskDisableAccessibility", "kiosk-direct command", "KioskDirectOperatorCommand.Invoke", true, true),
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
        bool confirmation) =>
        new(id, handler, OperatorActionProjection.SharedCoreCli, cli, core, mutates, confirmation);

    private static OperatorActionDescriptor Interactive(string id, string handler, string reason) =>
        new(id, handler, OperatorActionProjection.InteractiveOnly, null, null, false, false, reason);
}
