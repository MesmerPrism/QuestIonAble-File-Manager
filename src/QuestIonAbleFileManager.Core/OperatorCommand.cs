using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public enum OperatorCommandKind
{
    ConnectivityProfileStatus,
    ConnectivityProfileList,
    ConnectivityProfileImport,
    ConnectivityProfileRevoke,
    DiscoverDevices,
    ListFiles,
    PullFile,
    PushFile,
    ListPackages,
    ExportApk,
    InspectApk,
    InstallApk,
    LaunchInspectedApp,
    ObserveInspectedApp,
    InstallApkBundle,
    EnableWifiAdb,
    ConnectWifiAdb,
    DisconnectWifiAdb,
    InstallApkMany,
    InstallApkBundleMany,
    InspectRustyKiosk,
    InstallRustyKiosk,
    ProvisionRustyKiosk,
    InvokeRustyKiosk,
    PullRustyKioskTags,
    PushRustyKioskTags,
    ReadQuestControls,
    SetQuestKeepAwake,
    SetQuestPerformance,
    FleetInstallStatus,
    FleetInstall,
    PreflightInspectedApp,
    DeployInspectedApp,
    DiagnoseInspectedApp,
    StopPackage,
    UninstallExactApk,
    InventoryAdbForwards,
    ObservePackagePermissions
}

public enum QuestConnectivityProfileInputKind
{
    None,
    PrivateFile,
    StandardInput
}

public sealed class OperatorCommand
{
    internal OperatorCommand(
        OperatorCommandKind kind,
        IReadOnlyList<string> cliArguments,
        string? serial = null,
        string? remotePath = null,
        string? localPath = null,
        string? packageName = null,
        ApkInstallOptions? installOptions = null,
        ApkBundleInput? apkBundle = null,
        IReadOnlyList<string>? serials = null,
        string? wifiHost = null,
        int wifiPort = 5555,
        int maxParallelism = 4,
        bool operatorConfirmed = false,
        bool overwrite = false,
        RustyKioskBundle? rustyKioskBundle = null,
        RustyKioskCommand? rustyKioskCommand = null,
        string? rustyKioskValue = null,
        RustyKioskProductContract? rustyKioskProduct = null,
        bool? enabled = null,
        int durationMilliseconds = 28_800_000,
        int? cpuLevel = null,
        int? gpuLevel = null,
        bool clearPerformance = false,
        string? connectivityDeviceId = null,
        QuestConnectivityProfileInputKind connectivityProfileInputKind =
            QuestConnectivityProfileInputKind.None,
        bool replaceExisting = false,
        string? outputPath = null)
    {
        Kind = kind;
        CliArguments = new ReadOnlyCollection<string>(cliArguments.ToArray());
        Serial = serial;
        RemotePath = remotePath;
        LocalPath = localPath;
        PackageName = packageName;
        InstallOptions = installOptions;
        ApkBundle = apkBundle;
        Serials = serials is null
            ? null
            : new ReadOnlyCollection<string>(serials.ToArray());
        WifiHost = wifiHost;
        WifiPort = wifiPort;
        MaxParallelism = maxParallelism;
        OperatorConfirmed = operatorConfirmed;
        Overwrite = overwrite;
        RustyKioskBundle = rustyKioskBundle;
        RustyKioskCommand = rustyKioskCommand;
        RustyKioskValue = rustyKioskValue;
        RustyKioskProduct = rustyKioskProduct;
        Enabled = enabled;
        DurationMilliseconds = durationMilliseconds;
        CpuLevel = cpuLevel;
        GpuLevel = gpuLevel;
        ClearPerformance = clearPerformance;
        ConnectivityDeviceId = connectivityDeviceId;
        ConnectivityProfileInputKind = connectivityProfileInputKind;
        ReplaceExisting = replaceExisting;
        OutputPath = outputPath;
    }

    public OperatorCommandKind Kind { get; }

    public IReadOnlyList<string> CliArguments { get; }

    public string? Serial { get; }

    public string? RemotePath { get; }

    public string? LocalPath { get; }

    public string? PackageName { get; }

    public ApkInstallOptions? InstallOptions { get; }

    public ApkBundleInput? ApkBundle { get; }

    public IReadOnlyList<string>? Serials { get; }

    public string? WifiHost { get; }

    public int WifiPort { get; }

    public int MaxParallelism { get; }

    public bool OperatorConfirmed { get; }

    public bool Overwrite { get; }

    public RustyKioskBundle? RustyKioskBundle { get; }

    public RustyKioskCommand? RustyKioskCommand { get; }

    public string? RustyKioskValue { get; }

    public RustyKioskProductContract? RustyKioskProduct { get; }

    public bool? Enabled { get; }

    public int DurationMilliseconds { get; }

    public int? CpuLevel { get; }

    public int? GpuLevel { get; }

    public bool ClearPerformance { get; }

    public string? ConnectivityDeviceId { get; }

    public QuestConnectivityProfileInputKind ConnectivityProfileInputKind { get; }

    public bool ReplaceExisting { get; }

    public string? OutputPath { get; }

    public string ToPowerShellCommand(
        string cliExecutable = ".\\questionable-file-manager.exe",
        string? adbPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliExecutable);
        var arguments = CliArguments.ToList();
        if (!string.IsNullOrWhiteSpace(adbPath))
        {
            arguments.Add("--adb");
            arguments.Add(adbPath);
        }

        return $"& {PowerShellCliFormatter.Quote(cliExecutable)} " +
               string.Join(" ", arguments.Select(PowerShellCliFormatter.FormatArgument));
    }
}

public static class OperatorCommands
{
    public static OperatorCommand ParseConnectivityProfileCliArguments(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.SequenceEqual(
                ["connectivity-profile", "list", "--json"],
                StringComparer.Ordinal))
        {
            return ListQuestConnectivityProfiles();
        }

        if (arguments.Count == 5 &&
            arguments[0] == "connectivity-profile" &&
            arguments[1] == "status" &&
            arguments[2] == "--device-id" &&
            arguments[4] == "--json")
        {
            return QuestConnectivityProfileStatus(arguments[3]);
        }

        if (arguments.Count == 6 &&
            arguments[0] == "connectivity-profile" &&
            arguments[1] == "revoke" &&
            arguments[2] == "--device-id" &&
            arguments[4] == "--confirm-profile-revoke" &&
            arguments[5] == "--json")
        {
            return RevokeQuestConnectivityProfile(
                arguments[3],
                operatorConfirmed: true);
        }

        var importingFile = arguments.Count is 6 or 7 &&
                            arguments[0] == "connectivity-profile" &&
                            arguments[1] == "import" &&
                            arguments[2] == "--file" &&
                            arguments[4] == "--confirm-profile-write";
        if (importingFile &&
            ((arguments.Count == 6 && arguments[5] == "--json") ||
             (arguments.Count == 7 &&
              arguments[5] == "--replace-existing" &&
              arguments[6] == "--json")))
        {
            return ImportQuestConnectivityProfileFile(
                arguments[3],
                replaceExisting: arguments.Count == 7,
                operatorConfirmed: true);
        }

        var importingStdin = arguments.Count is 5 or 6 &&
                             arguments[0] == "connectivity-profile" &&
                             arguments[1] == "import" &&
                             arguments[2] == "--stdin" &&
                             arguments[3] == "--confirm-profile-write";
        if (importingStdin &&
            ((arguments.Count == 5 && arguments[4] == "--json") ||
             (arguments.Count == 6 &&
              arguments[4] == "--replace-existing" &&
              arguments[5] == "--json")))
        {
            return ImportQuestConnectivityProfileStdin(
                replaceExisting: arguments.Count == 6,
                operatorConfirmed: true);
        }

        throw new ArgumentException(
            "Use an exact connectivity-profile status, list, import, or revoke command. " +
            "Secrets are accepted only through --file or --stdin.",
            nameof(arguments));
    }

    public static OperatorCommand QuestConnectivityProfileStatus(string deviceId)
    {
        deviceId = RequireConnectivityDeviceId(deviceId);
        return new OperatorCommand(
            OperatorCommandKind.ConnectivityProfileStatus,
            ["connectivity-profile", "status", "--device-id", deviceId, "--json"],
            connectivityDeviceId: deviceId);
    }

    public static OperatorCommand ListQuestConnectivityProfiles() =>
        new(
            OperatorCommandKind.ConnectivityProfileList,
            ["connectivity-profile", "list", "--json"]);

    public static OperatorCommand ImportQuestConnectivityProfileFile(
        string privateJsonPath,
        bool replaceExisting = false,
        bool operatorConfirmed = false)
    {
        RequireApproval(operatorConfirmed, "Connectivity profile write");
        ArgumentException.ThrowIfNullOrWhiteSpace(privateJsonPath);
        var fullPath = Path.GetFullPath(privateJsonPath);
        var arguments = new List<string>
        {
            "connectivity-profile", "import", "--file", fullPath,
            "--confirm-profile-write"
        };
        if (replaceExisting)
            arguments.Add("--replace-existing");
        arguments.Add("--json");
        return new OperatorCommand(
            OperatorCommandKind.ConnectivityProfileImport,
            arguments,
            localPath: fullPath,
            operatorConfirmed: true,
            connectivityProfileInputKind: QuestConnectivityProfileInputKind.PrivateFile,
            replaceExisting: replaceExisting);
    }

    public static OperatorCommand ImportQuestConnectivityProfileStdin(
        bool replaceExisting = false,
        bool operatorConfirmed = false)
    {
        RequireApproval(operatorConfirmed, "Connectivity profile write");
        var arguments = new List<string>
        {
            "connectivity-profile", "import", "--stdin", "--confirm-profile-write"
        };
        if (replaceExisting)
            arguments.Add("--replace-existing");
        arguments.Add("--json");
        return new OperatorCommand(
            OperatorCommandKind.ConnectivityProfileImport,
            arguments,
            operatorConfirmed: true,
            connectivityProfileInputKind: QuestConnectivityProfileInputKind.StandardInput,
            replaceExisting: replaceExisting);
    }

    public static OperatorCommand RevokeQuestConnectivityProfile(
        string deviceId,
        bool operatorConfirmed = false)
    {
        RequireApproval(operatorConfirmed, "Connectivity profile revocation");
        deviceId = RequireConnectivityDeviceId(deviceId);
        return new OperatorCommand(
            OperatorCommandKind.ConnectivityProfileRevoke,
            [
                "connectivity-profile", "revoke", "--device-id", deviceId,
                "--confirm-profile-revoke", "--json"
            ],
            operatorConfirmed: true,
            connectivityDeviceId: deviceId);
    }

    public static OperatorCommand ParseFleetCliArguments(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.SequenceEqual(
                ["fleet", "status", "--json"],
                StringComparer.Ordinal))
        {
            return FleetInstallStatus();
        }
        if (arguments.SequenceEqual(
                ["fleet", "install", "--confirm-fleet-install", "--json"],
                StringComparer.Ordinal))
        {
            return FleetInstall(operatorConfirmed: true);
        }
        throw new ArgumentException(
            "Use exactly 'fleet status --json' or " +
            "'fleet install --confirm-fleet-install --json'.",
            nameof(arguments));
    }

    public static OperatorCommand FleetInstallStatus() =>
        new(
            OperatorCommandKind.FleetInstallStatus,
            ["fleet", "status", "--json"]);

    public static OperatorCommand FleetInstall(bool operatorConfirmed = false)
    {
        RequireApproval(operatorConfirmed, "Fleet guided installation");
        return new OperatorCommand(
            OperatorCommandKind.FleetInstall,
            ["fleet", "install", "--confirm-fleet-install", "--json"],
            operatorConfirmed: true);
    }

    public static OperatorCommand DiscoverDevices() =>
        new(OperatorCommandKind.DiscoverDevices, ["devices"]);

    public static OperatorCommand EnableWifiAdb(
        string usbSerial,
        int port = 5555,
        bool operatorConfirmed = false)
    {
        RequireWifiApproval(operatorConfirmed);
        usbSerial = AndroidInput.RequireUsbSerial(usbSerial);
        port = AndroidInput.RequireTcpPort(port);
        return new OperatorCommand(
            OperatorCommandKind.EnableWifiAdb,
            [
                "wifi", "enable", "--serial", usbSerial,
                "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--confirm-wifi-adb"
            ],
            serial: usbSerial,
            wifiPort: port,
            operatorConfirmed: true);
    }

    public static OperatorCommand ConnectWifiAdb(
        string host,
        int port = 5555,
        bool operatorConfirmed = false)
    {
        RequireWifiApproval(operatorConfirmed);
        host = AndroidInput.RequireWifiHost(host);
        port = AndroidInput.RequireTcpPort(port);
        return new OperatorCommand(
            OperatorCommandKind.ConnectWifiAdb,
            [
                "wifi", "connect", "--host", host,
                "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--confirm-wifi-adb"
            ],
            wifiHost: host,
            wifiPort: port,
            operatorConfirmed: true);
    }

    public static OperatorCommand DisconnectWifiAdb(
        string host,
        int port = 5555,
        bool operatorConfirmed = false)
    {
        RequireWifiApproval(operatorConfirmed);
        host = AndroidInput.RequireWifiHost(host);
        port = AndroidInput.RequireTcpPort(port);
        return new OperatorCommand(
            OperatorCommandKind.DisconnectWifiAdb,
            [
                "wifi", "disconnect", "--host", host,
                "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--confirm-wifi-adb"
            ],
            wifiHost: host,
            wifiPort: port,
            operatorConfirmed: true);
    }

    public static OperatorCommand ListFiles(string serial, string remotePath)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        return new OperatorCommand(
            OperatorCommandKind.ListFiles,
            ["files", "list", "--serial", serial, "--path", remotePath],
            serial: serial,
            remotePath: remotePath);
    }

    public static OperatorCommand PullFile(string serial, string remotePath, string outputPath)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        return new OperatorCommand(
            OperatorCommandKind.PullFile,
            ["files", "pull", "--serial", serial, "--remote", remotePath, "--output", fullOutputPath],
            serial: serial,
            remotePath: remotePath,
            localPath: fullOutputPath);
    }

    public static OperatorCommand PushFile(string serial, string localPath, string remotePath)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var fullLocalPath = Path.GetFullPath(localPath);
        return new OperatorCommand(
            OperatorCommandKind.PushFile,
            ["files", "push", "--serial", serial, "--file", fullLocalPath, "--remote", remotePath],
            serial: serial,
            remotePath: remotePath,
            localPath: fullLocalPath);
    }

    public static OperatorCommand ListPackages(string serial)
    {
        serial = AndroidInput.RequireSerial(serial);
        return new OperatorCommand(
            OperatorCommandKind.ListPackages,
            ["apk", "list", "--serial", serial],
            serial: serial);
    }

    public static OperatorCommand ExportApk(
        string serial,
        string packageName,
        string outputPath,
        bool overwrite = false)
    {
        serial = AndroidInput.RequireSerial(serial);
        packageName = AndroidInput.RequirePackageName(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var arguments = new List<string>
        {
            "apk", "export", "--serial", serial, "--package", packageName, "--output", fullOutputPath
        };
        if (overwrite)
        {
            arguments.Add("--overwrite");
        }

        return new OperatorCommand(
            OperatorCommandKind.ExportApk,
            arguments,
            serial: serial,
            localPath: fullOutputPath,
            packageName: packageName,
            overwrite: overwrite);
    }

    public static OperatorCommand InstallApk(
        string serial,
        string apkPath,
        ApkInstallOptions? options = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullApkPath = Path.GetFullPath(apkPath);
        options ??= new ApkInstallOptions();
        var arguments = new List<string>
        {
            "apk", "install", "--serial", serial, "--file", fullApkPath
        };
        if (!options.ReplaceExisting)
        {
            arguments.Add("--no-replace");
        }

        if (options.AllowDowngrade)
        {
            arguments.Add("--downgrade");
        }

        if (options.GrantRuntimePermissions)
        {
            arguments.Add("--grant-runtime-permissions");
        }

        if (options.AllowTestPackages)
        {
            arguments.Add("--test-only");
        }

        return new OperatorCommand(
            OperatorCommandKind.InstallApk,
            arguments,
            serial: serial,
            localPath: fullApkPath,
            installOptions: options);
    }

    public static OperatorCommand PreflightInspectedApp(string serial, string apkPath)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullApkPath = Path.GetFullPath(apkPath);
        return new OperatorCommand(
            OperatorCommandKind.PreflightInspectedApp,
            ["apk", "preflight", "--serial", serial, "--file", fullApkPath],
            serial: serial,
            localPath: fullApkPath);
    }

    public static OperatorCommand DeployInspectedApp(
        string serial,
        string apkPath,
        ApkInstallOptions? options = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullApkPath = Path.GetFullPath(apkPath);
        options ??= new ApkInstallOptions();
        var arguments = new List<string>
        {
            "apk", "deploy", "--serial", serial, "--file", fullApkPath
        };
        AddInstallOptionArguments(arguments, options);
        return new OperatorCommand(
            OperatorCommandKind.DeployInspectedApp,
            arguments,
            serial: serial,
            localPath: fullApkPath,
            installOptions: options);
    }

    public static OperatorCommand DiagnoseInspectedApp(
        string serial,
        string apkPath,
        string outputDirectory)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullApkPath = Path.GetFullPath(apkPath);
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        return new OperatorCommand(
            OperatorCommandKind.DiagnoseInspectedApp,
            [
                "apk", "diagnose", "--serial", serial, "--file", fullApkPath,
                "--output", fullOutputDirectory
            ],
            serial: serial,
            localPath: fullApkPath,
            outputPath: fullOutputDirectory);
    }

    public static OperatorCommand StopPackage(
        string serial,
        string packageName,
        bool operatorConfirmed = false)
    {
        RequireApproval(operatorConfirmed, "Exact-package current-user stop");
        serial = AndroidInput.RequireSerial(serial);
        packageName = AndroidInput.RequirePackageName(packageName);
        return new OperatorCommand(
            OperatorCommandKind.StopPackage,
            [
                "apk", "stop", "--serial", serial, "--package", packageName,
                "--confirm-package-stop"
            ],
            serial: serial,
            packageName: packageName,
            operatorConfirmed: true);
    }

    public static OperatorCommand ParsePackageStopCliArguments(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 8 ||
            !arguments.SequenceEqual(
                [
                    "apk", "stop", "--serial", arguments.Count > 3 ? arguments[3] : string.Empty,
                    "--package", arguments.Count > 5 ? arguments[5] : string.Empty,
                    "--confirm-package-stop", "--json"
                ],
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Use exactly apk stop --serial <quest-serial> --package <package> --confirm-package-stop --json.",
                nameof(arguments));
        }

        return StopPackage(arguments[3], arguments[5], operatorConfirmed: true);
    }

    public static OperatorCommand UninstallExactApk(
        string serial,
        string apkPath,
        bool operatorConfirmed = false)
    {
        RequireApproval(
            operatorConfirmed,
            "Exact inspected-APK uninstall, including app-private data removal");
        serial = AndroidInput.RequireSerial(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullApkPath = Path.GetFullPath(apkPath);
        return new OperatorCommand(
            OperatorCommandKind.UninstallExactApk,
            [
                "apk", "uninstall", "--serial", serial, "--file", fullApkPath,
                "--confirm-exact-apk-uninstall"
            ],
            serial: serial,
            localPath: fullApkPath,
            operatorConfirmed: true);
    }

    public static OperatorCommand ParseExactApkUninstallCliArguments(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 8 ||
            !arguments.SequenceEqual(
                [
                    "apk", "uninstall", "--serial",
                    arguments.Count > 3 ? arguments[3] : string.Empty,
                    "--file", arguments.Count > 5 ? arguments[5] : string.Empty,
                    "--confirm-exact-apk-uninstall", "--json"
                ],
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Use exactly apk uninstall --serial <quest-serial> --file <apk> " +
                "--confirm-exact-apk-uninstall --json.",
                nameof(arguments));
        }

        return UninstallExactApk(arguments[3], arguments[5], operatorConfirmed: true);
    }

    public static OperatorCommand InventoryAdbForwards(string serial)
    {
        serial = AndroidInput.RequireSerial(serial);
        return new OperatorCommand(
            OperatorCommandKind.InventoryAdbForwards,
            ["adb", "forwards", "--serial", serial],
            serial: serial);
    }

    public static OperatorCommand ParseAdbForwardInventoryCliArguments(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 5 ||
            !arguments.SequenceEqual(
                [
                    "adb", "forwards", "--serial",
                    arguments.Count > 3 ? arguments[3] : string.Empty,
                    "--json"
                ],
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Use exactly adb forwards --serial <quest-serial> --json.",
                nameof(arguments));
        }

        return InventoryAdbForwards(arguments[3]);
    }

    public static OperatorCommand ObservePackagePermissions(
        string serial,
        string packageName)
    {
        serial = AndroidInput.RequireSerial(serial);
        packageName = AndroidInput.RequirePackageName(packageName);
        return new OperatorCommand(
            OperatorCommandKind.ObservePackagePermissions,
            ["apk", "permissions", "--serial", serial, "--package", packageName],
            serial: serial,
            packageName: packageName);
    }

    public static OperatorCommand ParsePackagePermissionObservationCliArguments(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 7 ||
            !arguments.SequenceEqual(
                [
                    "apk", "permissions", "--serial",
                    arguments.Count > 3 ? arguments[3] : string.Empty,
                    "--package", arguments.Count > 5 ? arguments[5] : string.Empty,
                    "--json"
                ],
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Use exactly apk permissions --serial <quest-serial> --package <package> --json.",
                nameof(arguments));
        }

        return ObservePackagePermissions(arguments[3], arguments[5]);
    }

    public static OperatorCommand InspectApk(string apkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullPath = Path.GetFullPath(apkPath);
        return new OperatorCommand(
            OperatorCommandKind.InspectApk,
            ["apk", "inspect", "--file", fullPath],
            localPath: fullPath);
    }

    public static OperatorCommand LaunchInspectedApp(string serial, string apkPath)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullPath = Path.GetFullPath(apkPath);
        return new OperatorCommand(
            OperatorCommandKind.LaunchInspectedApp,
            ["apk", "launch", "--serial", serial, "--file", fullPath],
            serial: serial,
            localPath: fullPath);
    }

    public static OperatorCommand ObserveInspectedApp(string serial, string apkPath)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullPath = Path.GetFullPath(apkPath);
        return new OperatorCommand(
            OperatorCommandKind.ObserveInspectedApp,
            ["apk", "observe", "--serial", serial, "--file", fullPath],
            serial: serial,
            localPath: fullPath);
    }

    public static OperatorCommand InstallApkBundle(
        string serial,
        string folderPath,
        ApkInstallOptions? options = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        var bundle = ApkBundleInput.FromFolder(folderPath);
        options ??= new ApkInstallOptions();
        var arguments = new List<string>
        {
            "apk", "install-bundle", "--serial", serial, "--folder", bundle.FolderPath
        };
        if (!options.ReplaceExisting)
        {
            arguments.Add("--no-replace");
        }

        if (options.AllowDowngrade)
        {
            arguments.Add("--downgrade");
        }

        if (options.GrantRuntimePermissions)
        {
            arguments.Add("--grant-runtime-permissions");
        }

        if (options.AllowTestPackages)
        {
            arguments.Add("--test-only");
        }

        return new OperatorCommand(
            OperatorCommandKind.InstallApkBundle,
            arguments,
            serial: serial,
            localPath: bundle.FolderPath,
            installOptions: options,
            apkBundle: bundle);
    }

    public static OperatorCommand InstallApkMany(
        IReadOnlyList<string> serials,
        string apkPath,
        ApkInstallOptions? options = null,
        int maxParallelism = 4)
    {
        var targets = ValidateWifiTargets(serials);
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullApkPath = Path.GetFullPath(apkPath);
        options ??= new ApkInstallOptions();
        maxParallelism = AndroidInput.RequireParallelism(maxParallelism);
        var arguments = new List<string> { "apk", "install-many" };
        AddSerialArguments(arguments, targets);
        arguments.AddRange(
        [
            "--file", fullApkPath,
            "--parallelism", maxParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ]);
        AddInstallOptionArguments(arguments, options);
        return new OperatorCommand(
            OperatorCommandKind.InstallApkMany,
            arguments,
            localPath: fullApkPath,
            installOptions: options,
            serials: targets,
            maxParallelism: maxParallelism);
    }

    public static OperatorCommand InstallApkBundleMany(
        IReadOnlyList<string> serials,
        string folderPath,
        ApkInstallOptions? options = null,
        int maxParallelism = 4)
    {
        var targets = ValidateWifiTargets(serials);
        var bundle = ApkBundleInput.FromFolder(folderPath);
        options ??= new ApkInstallOptions();
        maxParallelism = AndroidInput.RequireParallelism(maxParallelism);
        var arguments = new List<string> { "apk", "install-bundle-many" };
        AddSerialArguments(arguments, targets);
        arguments.AddRange(
        [
            "--folder", bundle.FolderPath,
            "--parallelism", maxParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ]);
        AddInstallOptionArguments(arguments, options);
        return new OperatorCommand(
            OperatorCommandKind.InstallApkBundleMany,
            arguments,
            localPath: bundle.FolderPath,
            installOptions: options,
            apkBundle: bundle,
            serials: targets,
            maxParallelism: maxParallelism);
    }

    public static OperatorCommand InstallRustyKiosk(
        string serial,
        RustyKioskBundle bundle,
        bool operatorConfirmed = false,
        RustyKioskProductContract? product = null)
    {
        RequireApproval(operatorConfirmed, "Rusty Kiosk installation and USB setup");
        serial = AndroidInput.RequireSerial(serial);
        ArgumentNullException.ThrowIfNull(bundle);
        product = RustyKioskProductContract.RequireKnown(
            product ?? RustyKioskProductContract.For(RustyKioskProductChannel.Stable));
        bundle.ValidateProductSelection(product);
        return new OperatorCommand(
            OperatorCommandKind.InstallRustyKiosk,
            [
                "kiosk", "install", "--serial", serial,
                "--product-channel", product.WireName,
                "--bundle", bundle.Source,
                "--confirm-kiosk-setup"
            ],
            serial: serial,
            operatorConfirmed: true,
            rustyKioskBundle: bundle,
            rustyKioskProduct: product);
    }

    public static OperatorCommand InspectRustyKiosk(
        string serial,
        RustyKioskProductContract? product = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        product = RustyKioskProductContract.RequireKnown(
            product ?? RustyKioskProductContract.For(RustyKioskProductChannel.Stable));
        return new OperatorCommand(
            OperatorCommandKind.InspectRustyKiosk,
            ["kiosk", "status", "--serial", serial, "--product-channel", product.WireName],
            serial: serial,
            rustyKioskCommand: RustyKioskCommand.Status,
            rustyKioskProduct: product);
    }

    public static OperatorCommand ProvisionRustyKiosk(
        string serial,
        bool operatorConfirmed = false,
        RustyKioskProductContract? product = null)
    {
        RequireApproval(operatorConfirmed, "Rusty Kiosk USB setup");
        serial = AndroidInput.RequireSerial(serial);
        product = RustyKioskProductContract.RequireKnown(
            product ?? RustyKioskProductContract.For(RustyKioskProductChannel.Stable));
        return new OperatorCommand(
            OperatorCommandKind.ProvisionRustyKiosk,
            [
                "kiosk", "provision", "--serial", serial,
                "--product-channel", product.WireName,
                "--confirm-kiosk-setup"
            ],
            serial: serial,
            operatorConfirmed: true,
            rustyKioskProduct: product);
    }

    public static RustyKioskProductContract ParseRequiredKioskSetupProductChannel(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var indices = arguments
            .Select(static (argument, index) => (argument, index))
            .Where(static entry => string.Equals(
                entry.argument,
                "--product-channel",
                StringComparison.OrdinalIgnoreCase))
            .Select(static entry => entry.index)
            .ToArray();
        if (indices.Length == 0)
        {
            throw new ArgumentException(
                "Rusty Kiosk install and provision require --product-channel <stable|labs>.",
                nameof(arguments));
        }
        if (indices.Length != 1)
        {
            throw new ArgumentException(
                "Rusty Kiosk setup accepts exactly one --product-channel option.",
                nameof(arguments));
        }

        var index = indices[0];
        if (index + 1 >= arguments.Count ||
            arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Option --product-channel requires stable or labs.",
                nameof(arguments));
        }
        return RustyKioskProductContract.Parse(arguments[index + 1]);
    }

    public static OperatorCommand InvokeRustyKiosk(
        string serial,
        RustyKioskCommand command,
        string? value = null,
        bool operatorConfirmed = false,
        RustyKioskProductContract? product = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        product = RustyKioskProductContract.RequireKnown(
            product ?? RustyKioskProductContract.For(RustyKioskProductChannel.Stable));
        value = command.ValidateValue(value);

        if (RequiresKioskControlApproval(command))
        {
            RequireApproval(operatorConfirmed, $"Rusty Kiosk {command.ToWireName()}");
        }

        var arguments = new List<string>
        {
            "kiosk", "command", "--serial", serial,
            "--product-channel", product.WireName,
            "--command", command.ToWireName()
        };
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments.Add("--value");
            arguments.Add(value);
        }

        if (RequiresKioskControlApproval(command))
        {
            arguments.Add("--confirm-kiosk-control");
        }

        return new OperatorCommand(
            OperatorCommandKind.InvokeRustyKiosk,
            arguments,
            serial: serial,
            operatorConfirmed: operatorConfirmed,
            rustyKioskCommand: command,
            rustyKioskValue: value,
            rustyKioskProduct: product);
    }

    public static OperatorCommand PullRustyKioskTags(
        string serial,
        string outputPath,
        RustyKioskProductContract? product = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        product = RustyKioskProductContract.RequireKnown(
            product ?? RustyKioskProductContract.For(RustyKioskProductChannel.Stable));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        return new OperatorCommand(
            OperatorCommandKind.PullRustyKioskTags,
            [
                "kiosk", "tags", "export", "--serial", serial,
                "--product-channel", product.WireName,
                "--output", fullPath
            ],
            serial: serial,
            localPath: fullPath,
            rustyKioskProduct: product);
    }

    public static OperatorCommand PushRustyKioskTags(
        string serial,
        string inputPath,
        bool operatorConfirmed = false,
        RustyKioskProductContract? product = null)
    {
        RequireApproval(operatorConfirmed, "Rusty Kiosk tag-file replacement");
        serial = AndroidInput.RequireSerial(serial);
        product = RustyKioskProductContract.RequireKnown(
            product ?? RustyKioskProductContract.For(RustyKioskProductChannel.Stable));
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var fullPath = Path.GetFullPath(inputPath);
        RustyKioskTagFile.ValidateAndRead(fullPath);
        return new OperatorCommand(
            OperatorCommandKind.PushRustyKioskTags,
            [
                "kiosk", "tags", "import", "--serial", serial,
                "--product-channel", product.WireName,
                "--file", fullPath,
                "--confirm-kiosk-control"
            ],
            serial: serial,
            localPath: fullPath,
            operatorConfirmed: true,
            rustyKioskProduct: product);
    }

    public static OperatorCommand ReadQuestControls(string serial)
    {
        serial = AndroidInput.RequireSerial(serial);
        return new OperatorCommand(
            OperatorCommandKind.ReadQuestControls,
            ["device", "status", "--serial", serial],
            serial: serial);
    }

    public static OperatorCommand SetQuestKeepAwake(
        string serial,
        bool enabled,
        int durationMilliseconds = 28_800_000,
        bool operatorConfirmed = false)
    {
        RequireApproval(operatorConfirmed, "Quest keep-awake policy change");
        serial = AndroidInput.RequireSerial(serial);
        if (durationMilliseconds is < 60_000 or > QuestAwakeContract.MaximumHoldDurationMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMilliseconds),
                "Keep-awake duration must be between one minute and eight hours.");
        }

        return new OperatorCommand(
            OperatorCommandKind.SetQuestKeepAwake,
            [
                "device", "keep-awake", "--serial", serial,
                enabled ? "--on" : "--off",
                "--duration-ms", durationMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--confirm-device-settings"
            ],
            serial: serial,
            operatorConfirmed: true,
            enabled: enabled,
            durationMilliseconds: durationMilliseconds);
    }

    public static OperatorCommand SetQuestPerformance(
        string serial,
        int? cpuLevel,
        int? gpuLevel,
        bool clear = false,
        bool operatorConfirmed = false)
    {
        RequireApproval(operatorConfirmed, "Quest CPU/GPU override change");
        serial = AndroidInput.RequireSerial(serial);
        ValidatePerformanceLevel(cpuLevel, nameof(cpuLevel));
        ValidatePerformanceLevel(gpuLevel, nameof(gpuLevel));
        if (!clear && cpuLevel is null && gpuLevel is null)
        {
            throw new ArgumentException("Choose a CPU or GPU level, or clear both overrides.");
        }

        var arguments = new List<string> { "device", "performance", "--serial", serial };
        if (clear)
        {
            arguments.Add("--clear");
        }
        else
        {
            if (cpuLevel is not null)
            {
                arguments.Add("--cpu");
                arguments.Add(cpuLevel.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (gpuLevel is not null)
            {
                arguments.Add("--gpu");
                arguments.Add(gpuLevel.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        arguments.Add("--confirm-device-settings");
        return new OperatorCommand(
            OperatorCommandKind.SetQuestPerformance,
            arguments,
            serial: serial,
            operatorConfirmed: true,
            cpuLevel: cpuLevel,
            gpuLevel: gpuLevel,
            clearPerformance: clear);
    }

    private static IReadOnlyList<string> ValidateWifiTargets(IReadOnlyList<string> serials)
    {
        ArgumentNullException.ThrowIfNull(serials);
        if (serials.Count < 2)
        {
            throw new ArgumentException(
                "Select at least two connected Wi-Fi ADB headsets.",
                nameof(serials));
        }

        var targets = serials.Select(AndroidInput.RequireWifiSerial).ToArray();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
        {
            throw new ArgumentException("Each Wi-Fi headset may be selected only once.", nameof(serials));
        }

        return targets;
    }

    private static void AddSerialArguments(List<string> arguments, IReadOnlyList<string> serials)
    {
        foreach (var serial in serials)
        {
            arguments.Add("--serial");
            arguments.Add(serial);
        }
    }

    private static void AddInstallOptionArguments(List<string> arguments, ApkInstallOptions options)
    {
        if (!options.ReplaceExisting)
        {
            arguments.Add("--no-replace");
        }

        if (options.AllowDowngrade)
        {
            arguments.Add("--downgrade");
        }

        if (options.GrantRuntimePermissions)
        {
            arguments.Add("--grant-runtime-permissions");
        }

        if (options.AllowTestPackages)
        {
            arguments.Add("--test-only");
        }
    }

    private static void RequireWifiApproval(bool operatorConfirmed)
    {
        if (!operatorConfirmed)
        {
            throw new InvalidOperationException(
                "Wi-Fi ADB changes require explicit operator confirmation.");
        }
    }

    private static bool RequiresKioskControlApproval(RustyKioskCommand command) => command is
        RustyKioskCommand.RequestWifiAdb or
        RustyKioskCommand.EnableWifiAfterBoot or
        RustyKioskCommand.DisableWifiAfterBoot or
        RustyKioskCommand.DisableWifiAdb or
        RustyKioskCommand.EnableAccessibility or
        RustyKioskCommand.DisableAccessibility or
        RustyKioskCommand.SetLaunchRequirement or
        RustyKioskCommand.LaunchOption or
        RustyKioskCommand.CancelPendingLaunch or
        RustyKioskCommand.PassthroughNatural or
        RustyKioskCommand.PassthroughContour or
        RustyKioskCommand.ExitMetaHome;

    private static void RequireApproval(bool operatorConfirmed, string operation)
    {
        if (!operatorConfirmed)
        {
            throw new InvalidOperationException($"{operation} requires explicit operator confirmation.");
        }
    }

    private static void ValidatePerformanceLevel(int? value, string parameterName)
    {
        if (value is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Quest CPU/GPU level must be between 0 and 5.");
        }
    }

    private static string RequireConnectivityDeviceId(string value)
    {
        if (!QuestConnectivityProfileManagementContract.IsDeviceId(value))
        {
            throw new ArgumentException(
                "Fleet device ID must be a lowercase 1–256 character identifier.",
                nameof(value));
        }

        return value;
    }
}

public sealed record OperatorExecutionResult(
    OperatorCommand Command,
    IReadOnlyList<QuestDevice>? Devices = null,
    IReadOnlyList<RemoteEntry>? RemoteEntries = null,
    IReadOnlyList<string>? Packages = null,
    CommandResult? CommandResult = null,
    ApkExportResult? ApkExportResult = null,
    ApkArtifactInspection? ApkArtifactInspection = null,
    InspectedApkInstallResult? InspectedApkInstallResult = null,
    ResolvedAppLaunchResult? ResolvedAppLaunchResult = null,
    AppRuntimeObservation? AppRuntimeObservation = null,
    ApkBundleInstallResult? ApkBundleInstallResult = null,
    WifiAdbEnableResult? WifiAdbEnableResult = null,
    WifiAdbConnectionResult? WifiAdbConnectionResult = null,
    ParallelApkInstallResult? ParallelApkInstallResult = null,
    RustyKioskInstallResult? RustyKioskInstallResult = null,
    RustyKioskProvisionResult? RustyKioskProvisionResult = null,
    RustyKioskOperatorResult? RustyKioskOperatorResult = null,
    RustyKioskInstallationStatus? RustyKioskInstallationStatus = null,
    QuestControlStatus? QuestControlStatus = null,
    QuestKeepAwakeResult? QuestKeepAwakeResult = null,
    QuestPerformanceResult? QuestPerformanceResult = null,
    FleetInstallerStatusReceipt? FleetInstallerStatus = null,
    FleetInstallerHandoffReceipt? FleetInstallerHandoff = null,
    QuestConnectivityProfileStatusReceipt? ConnectivityProfileStatus = null,
    QuestConnectivityProfileListReceipt? ConnectivityProfileList = null,
    QuestConnectivityProfileMutationReceipt? ConnectivityProfileMutation = null,
    OperatorMutationReceipt? MutationReceipt = null,
    ApkPreflightResult? ApkPreflightResult = null,
    InspectedApkDeploymentResult? InspectedApkDeploymentResult = null,
    ApkDiagnosticBundleResult? ApkDiagnosticBundleResult = null,
    PackageStopResult? PackageStopResult = null,
    ExactApkUninstallResult? ExactApkUninstallResult = null,
    AdbForwardInventoryResult? AdbForwardInventoryResult = null,
    ApkPermissionObservation? ApkPermissionObservation = null);

public sealed class OperatorCommandExecutor
{
    private readonly AdbClient? _client;
    private readonly FleetInstallerHandoff _fleetInstaller;
    private readonly QuestConnectivityProfileManager _connectivityProfiles;

    public OperatorCommandExecutor(AdbClient client)
        : this(
            client ?? throw new ArgumentNullException(nameof(client)),
            new FleetInstallerHandoff(null),
            QuestConnectivityProfileManager.CreateWindows())
    {
    }

    public OperatorCommandExecutor(
        AdbClient? client,
        FleetInstallerHandoff fleetInstaller,
        QuestConnectivityProfileManager? connectivityProfiles = null)
    {
        _client = client;
        _fleetInstaller = fleetInstaller ??
            throw new ArgumentNullException(nameof(fleetInstaller));
        _connectivityProfiles = connectivityProfiles ??
            QuestConnectivityProfileManager.CreateWindows();
    }

    public async Task<OperatorExecutionResult> ExecuteAsync(
        OperatorCommand command,
        CancellationToken cancellationToken = default,
        IProgress<OperatorProgress>? progress = null,
        Stream? privateInput = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!OperatorMutations.RequiresHeadsetStateChange(command))
        {
            return await ExecuteCoreAsync(
                command,
                cancellationToken,
                progress,
                privateInput).ConfigureAwait(false);
        }

        var tracker = new OperatorMutationTracker(command, progress);
        tracker.Sent();
        tracker.Pending();
        try
        {
            var result = await ExecuteCoreAsync(
                command,
                cancellationToken,
                progress,
                privateInput).ConfigureAwait(false);
            var receipt = tracker.Complete(OperatorMutations.Observe(command, result));
            return result with { MutationReceipt = receipt };
        }
        catch (Exception exception)
        {
            tracker.Failed(exception);
            throw;
        }
    }

    private async Task<OperatorExecutionResult> ExecuteCoreAsync(
        OperatorCommand command,
        CancellationToken cancellationToken,
        IProgress<OperatorProgress>? progress,
        Stream? privateInput)
    {
        progress?.Report(new OperatorProgress(
            command.Kind.ToString(),
            StartingMessage(command.Kind),
            0,
            0));
        if (command.Kind == OperatorCommandKind.ConnectivityProfileStatus)
        {
            return new OperatorExecutionResult(
                command,
                ConnectivityProfileStatus: _connectivityProfiles.GetStatus(
                    Require(command.ConnectivityDeviceId, nameof(command.ConnectivityDeviceId))));
        }
        if (command.Kind == OperatorCommandKind.ConnectivityProfileList)
        {
            return new OperatorExecutionResult(
                command,
                ConnectivityProfileList: _connectivityProfiles.List());
        }
        if (command.Kind == OperatorCommandKind.ConnectivityProfileImport)
        {
            if (!command.OperatorConfirmed)
                throw new InvalidOperationException(
                    "Connectivity profile write requires explicit operator confirmation.");
            return new OperatorExecutionResult(
                command,
                ConnectivityProfileMutation: await _connectivityProfiles.ImportAsync(
                    command,
                    privateInput,
                    cancellationToken).ConfigureAwait(false));
        }
        if (command.Kind == OperatorCommandKind.ConnectivityProfileRevoke)
        {
            if (!command.OperatorConfirmed)
                throw new InvalidOperationException(
                    "Connectivity profile revocation requires explicit operator confirmation.");
            return new OperatorExecutionResult(
                command,
                ConnectivityProfileMutation: _connectivityProfiles.Revoke(
                    Require(command.ConnectivityDeviceId, nameof(command.ConnectivityDeviceId)),
                    command.OperatorConfirmed));
        }
        if (command.Kind == OperatorCommandKind.FleetInstallStatus)
        {
            return new OperatorExecutionResult(
                command,
                FleetInstallerStatus: await _fleetInstaller
                    .GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false));
        }
        if (command.Kind == OperatorCommandKind.FleetInstall)
        {
            if (!command.OperatorConfirmed)
            {
                throw new InvalidOperationException(
                    "Fleet guided installation requires explicit operator confirmation.");
            }
            return new OperatorExecutionResult(
                command,
                FleetInstallerHandoff: await _fleetInstaller
                    .InstallAsync(cancellationToken, progress)
                    .ConfigureAwait(false));
        }

        var client = _client ??
            throw new InvalidOperationException(
                "This operator command requires a configured ADB client.");
        switch (command.Kind)
        {
            case OperatorCommandKind.DiscoverDevices:
                return new OperatorExecutionResult(
                    command,
                    Devices: await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.ListFiles:
                return new OperatorExecutionResult(
                    command,
                    RemoteEntries: await client.ListRemoteDirectoryAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.RemotePath, nameof(command.RemotePath)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.PullFile:
                return new OperatorExecutionResult(
                    command,
                    CommandResult: await client.PullFileAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.RemotePath, nameof(command.RemotePath)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.PushFile:
                return new OperatorExecutionResult(
                    command,
                    CommandResult: await client.PushFileAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        Require(command.RemotePath, nameof(command.RemotePath)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.ListPackages:
                return new OperatorExecutionResult(
                    command,
                    Packages: await client.GetThirdPartyPackageNamesAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.ExportApk:
                return new OperatorExecutionResult(
                    command,
                    ApkExportResult: await client.ExportSingleApkAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.PackageName, nameof(command.PackageName)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        command.Overwrite,
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.InspectApk:
                return new OperatorExecutionResult(
                    command,
                    ApkArtifactInspection: await client.InspectApkAsync(
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.InstallApk:
                {
                    var install = await client.InstallInspectedApkAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        command.InstallOptions,
                        cancellationToken).ConfigureAwait(false);
                    return new OperatorExecutionResult(
                        command,
                        CommandResult: install.CommandResult,
                        ApkArtifactInspection: install.Artifact,
                        InspectedApkInstallResult: install);
                }

            case OperatorCommandKind.PreflightInspectedApp:
                return new OperatorExecutionResult(
                    command,
                    ApkPreflightResult: await client.PreflightInspectedApkAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.DeployInspectedApp:
                {
                    var deployment = await client.DeployInspectedApkAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        command.InstallOptions,
                        cancellationToken).ConfigureAwait(false);
                    return new OperatorExecutionResult(
                        command,
                        CommandResult: deployment.Install.CommandResult,
                        ApkArtifactInspection: deployment.Install.Artifact,
                        InspectedApkInstallResult: deployment.Install,
                        ResolvedAppLaunchResult: deployment.Launch,
                        AppRuntimeObservation: deployment.Runtime,
                        InspectedApkDeploymentResult: deployment);
                }

            case OperatorCommandKind.DiagnoseInspectedApp:
                return new OperatorExecutionResult(
                    command,
                    ApkDiagnosticBundleResult: await client.CaptureInspectedApkDiagnosticsAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        Require(command.OutputPath, nameof(command.OutputPath)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.StopPackage:
                if (!command.OperatorConfirmed)
                {
                    throw new InvalidOperationException(
                        "Exact-package current-user stop requires explicit operator confirmation.");
                }
                return new OperatorExecutionResult(
                    command,
                    PackageStopResult: await client.StopPackageAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.PackageName, nameof(command.PackageName)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.UninstallExactApk:
                if (!command.OperatorConfirmed)
                {
                    throw new InvalidOperationException(
                        "Exact inspected-APK uninstall requires explicit confirmation.");
                }
                return new OperatorExecutionResult(
                    command,
                    ExactApkUninstallResult: await client.UninstallExactApkAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.InventoryAdbForwards:
                return new OperatorExecutionResult(
                    command,
                    AdbForwardInventoryResult: await client.GetForwardInventoryAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.ObservePackagePermissions:
                return new OperatorExecutionResult(
                    command,
                    ApkPermissionObservation: await client.ObservePackagePermissionsAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.PackageName, nameof(command.PackageName)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.LaunchInspectedApp:
                return new OperatorExecutionResult(
                    command,
                    ResolvedAppLaunchResult: await client.LaunchInspectedAppAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.ObserveInspectedApp:
                return new OperatorExecutionResult(
                    command,
                    AppRuntimeObservation: await client.ObserveInspectedAppAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.InstallApkBundle:
                {
                    var bundle = command.ApkBundle ??
                        throw new InvalidOperationException("The operator command is missing its APK bundle.");
                    var result = await client.InstallApkBundleAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        bundle.ApkPaths,
                        command.InstallOptions,
                        cancellationToken).ConfigureAwait(false);
                    return new OperatorExecutionResult(
                        command,
                        CommandResult: result.CommandResult,
                        ApkBundleInstallResult: result);
                }

            case OperatorCommandKind.EnableWifiAdb:
                EnsureWifiApproval(command);
                return new OperatorExecutionResult(
                    command,
                    WifiAdbEnableResult: await client.EnableWifiAdbAndConnectAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        command.WifiPort,
                        cancellationToken,
                        progress).ConfigureAwait(false));

            case OperatorCommandKind.ConnectWifiAdb:
                EnsureWifiApproval(command);
                return new OperatorExecutionResult(
                    command,
                    WifiAdbConnectionResult: await client.ConnectWifiAdbAsync(
                        Require(command.WifiHost, nameof(command.WifiHost)),
                        command.WifiPort,
                        cancellationToken,
                        progress).ConfigureAwait(false));

            case OperatorCommandKind.DisconnectWifiAdb:
                EnsureWifiApproval(command);
                return new OperatorExecutionResult(
                    command,
                    CommandResult: await client.DisconnectWifiAdbAsync(
                        Require(command.WifiHost, nameof(command.WifiHost)),
                        command.WifiPort,
                        cancellationToken,
                        progress).ConfigureAwait(false));

            case OperatorCommandKind.InstallApkMany:
                return new OperatorExecutionResult(
                    command,
                    ParallelApkInstallResult: await client.InstallApkOnManyWifiDevicesAsync(
                        Require(command.Serials, nameof(command.Serials)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        command.InstallOptions,
                        command.MaxParallelism,
                        cancellationToken,
                        progress).ConfigureAwait(false));

            case OperatorCommandKind.InstallApkBundleMany:
                {
                    var bundle = command.ApkBundle ??
                        throw new InvalidOperationException("The operator command is missing its APK bundle.");
                    return new OperatorExecutionResult(
                        command,
                        ParallelApkInstallResult: await client.InstallApkBundleOnManyWifiDevicesAsync(
                            Require(command.Serials, nameof(command.Serials)),
                            bundle.ApkPaths,
                            command.InstallOptions,
                            command.MaxParallelism,
                            cancellationToken,
                            progress).ConfigureAwait(false));
                }

            case OperatorCommandKind.InstallRustyKiosk:
                return new OperatorExecutionResult(
                    command,
                    RustyKioskInstallResult: await client.InstallRustyKioskAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        command.RustyKioskBundle ??
                            throw new InvalidOperationException("The operator command is missing its Rusty Kiosk bundle."),
                        cancellationToken,
                        progress,
                        command.RustyKioskProduct ??
                            throw new InvalidOperationException("The operator command is missing its Rusty Kiosk product channel.")).ConfigureAwait(false));

            case OperatorCommandKind.InspectRustyKiosk:
                {
                    var serial = Require(command.Serial, nameof(command.Serial));
                    var product = command.RustyKioskProduct ??
                        throw new InvalidOperationException("The operator command is missing its Rusty Kiosk product channel.");
                    var status = await client.GetRustyKioskInstallationStatusAsync(
                        serial,
                        product,
                        cancellationToken).ConfigureAwait(false);
                    var operatorResult = status.HostOperatorAvailable
                        ? await client.InvokeRustyKioskAsync(
                            serial,
                            RustyKioskCommand.Status,
                            cancellationToken: cancellationToken,
                            product: product).ConfigureAwait(false)
                        : null;
                    return new OperatorExecutionResult(
                        command,
                        RustyKioskInstallationStatus: status,
                        RustyKioskOperatorResult: operatorResult);
                }

            case OperatorCommandKind.ProvisionRustyKiosk:
                return new OperatorExecutionResult(
                    command,
                    RustyKioskProvisionResult: await client.ProvisionRustyKioskAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        cancellationToken,
                        command.RustyKioskProduct ??
                            throw new InvalidOperationException("The operator command is missing its Rusty Kiosk product channel.")).ConfigureAwait(false));

            case OperatorCommandKind.InvokeRustyKiosk:
                {
                    var serial = Require(command.Serial, nameof(command.Serial));
                    var product = command.RustyKioskProduct ??
                        throw new InvalidOperationException("The operator command is missing its Rusty Kiosk product channel.");
                    var result = await client.InvokeRustyKioskAsync(
                        serial,
                        command.RustyKioskCommand ??
                            throw new InvalidOperationException("The operator command is missing its Rusty Kiosk action."),
                        command.RustyKioskValue,
                        cancellationToken,
                        product).ConfigureAwait(false);
                    return new OperatorExecutionResult(
                        command,
                        RustyKioskOperatorResult: result,
                        RustyKioskInstallationStatus: await client.GetRustyKioskInstallationStatusAsync(
                            serial,
                            product,
                            cancellationToken).ConfigureAwait(false));
                }

            case OperatorCommandKind.PullRustyKioskTags:
                return new OperatorExecutionResult(
                    command,
                    CommandResult: await client.PullRustyKioskTagFileAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        cancellationToken,
                        command.RustyKioskProduct ??
                            throw new InvalidOperationException("The operator command is missing its Rusty Kiosk product channel.")).ConfigureAwait(false));

            case OperatorCommandKind.PushRustyKioskTags:
                {
                    var serial = Require(command.Serial, nameof(command.Serial));
                    var product = command.RustyKioskProduct ??
                        throw new InvalidOperationException("The operator command is missing its Rusty Kiosk product channel.");
                    var transfer = await client.PushRustyKioskTagFileAsync(
                        serial,
                        Require(command.LocalPath, nameof(command.LocalPath)),
                        cancellationToken,
                        product).ConfigureAwait(false);
                    var hotload = await client.InvokeRustyKioskAsync(
                        serial,
                        RustyKioskCommand.Reload,
                        cancellationToken: cancellationToken,
                        product: product).ConfigureAwait(false);
                    return new OperatorExecutionResult(
                        command,
                        CommandResult: transfer,
                        RustyKioskOperatorResult: hotload,
                        RustyKioskInstallationStatus: await client.GetRustyKioskInstallationStatusAsync(
                            serial,
                            product,
                            cancellationToken).ConfigureAwait(false));
                }

            case OperatorCommandKind.ReadQuestControls:
                return new OperatorExecutionResult(
                    command,
                    QuestControlStatus: await client.GetQuestControlStatusAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        cancellationToken).ConfigureAwait(false));

            case OperatorCommandKind.SetQuestKeepAwake:
                {
                    var keepAwake = await client.SetQuestKeepAwakeAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        command.Enabled ??
                            throw new InvalidOperationException("The operator command is missing its keep-awake choice."),
                        command.DurationMilliseconds,
                        cancellationToken).ConfigureAwait(false);
                    return new OperatorExecutionResult(
                        command,
                        QuestControlStatus: keepAwake.EffectiveStatus,
                        QuestKeepAwakeResult: keepAwake);
                }

            case OperatorCommandKind.SetQuestPerformance:
                {
                    var performance = await client.SetQuestPerformanceLevelsAsync(
                        Require(command.Serial, nameof(command.Serial)),
                        command.CpuLevel,
                        command.GpuLevel,
                        command.ClearPerformance,
                        cancellationToken).ConfigureAwait(false);
                    return new OperatorExecutionResult(
                        command,
                        QuestControlStatus: performance.EffectiveStatus,
                        QuestPerformanceResult: performance);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "Unknown operator command.");
        }
    }

    private static string Require(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"The operator command is missing {name}.");

    private static IReadOnlyList<string> Require(IReadOnlyList<string>? value, string name) =>
        value is { Count: > 0 }
            ? value
            : throw new InvalidOperationException($"The operator command is missing {name}.");

    private static void EnsureWifiApproval(OperatorCommand command)
    {
        if (!command.OperatorConfirmed)
        {
            throw new InvalidOperationException(
                "Wi-Fi ADB changes require explicit operator confirmation.");
        }
    }

    private static string StartingMessage(OperatorCommandKind kind) => kind switch
    {
        OperatorCommandKind.ConnectivityProfileStatus => "Checking the private connectivity profile…",
        OperatorCommandKind.ConnectivityProfileList => "Listing private connectivity profile IDs…",
        OperatorCommandKind.ConnectivityProfileImport => "Validating and storing the private connectivity profile…",
        OperatorCommandKind.ConnectivityProfileRevoke => "Revoking the private connectivity profile…",
        OperatorCommandKind.DiscoverDevices => "Looking for authorized headsets…",
        OperatorCommandKind.ListFiles => "Listing the device folder…",
        OperatorCommandKind.PullFile => "Copying the selected file from the headset…",
        OperatorCommandKind.PushFile => "Copying the selected file to the headset…",
        OperatorCommandKind.ListPackages => "Loading third-party packages…",
        OperatorCommandKind.ExportApk => "Exporting and hashing the installed APK…",
        OperatorCommandKind.InstallApk => "Installing the APK…",
        OperatorCommandKind.PreflightInspectedApp => "Checking APK and selected Quest readiness…",
        OperatorCommandKind.DeployInspectedApp => "Installing, launching, and observing the inspected APK…",
        OperatorCommandKind.DiagnoseInspectedApp => "Capturing bounded inspected APK diagnostics…",
        OperatorCommandKind.StopPackage => "Stopping one exact package for the current Android user…",
        OperatorCommandKind.UninstallExactApk =>
            "Removing one exact inspected APK and its app-private data…",
        OperatorCommandKind.InventoryAdbForwards => "Reading the shared ADB forwarding inventory…",
        OperatorCommandKind.ObservePackagePermissions => "Reading bounded exact-package permission facts…",
        OperatorCommandKind.InstallApkBundle => "Installing the complete APK package set…",
        OperatorCommandKind.EnableWifiAdb => "Preparing Wi-Fi ADB…",
        OperatorCommandKind.ConnectWifiAdb => "Connecting to Wi-Fi ADB…",
        OperatorCommandKind.DisconnectWifiAdb => "Disconnecting Wi-Fi ADB…",
        OperatorCommandKind.InstallApkMany => "Preparing the parallel APK install…",
        OperatorCommandKind.InstallApkBundleMany => "Preparing the parallel APK bundle install…",
        OperatorCommandKind.InspectRustyKiosk => "Checking the optional Rusty Kiosk integration…",
        OperatorCommandKind.InstallRustyKiosk => "Installing and provisioning Rusty Kiosk…",
        OperatorCommandKind.ProvisionRustyKiosk => "Provisioning Rusty Kiosk Setup…",
        OperatorCommandKind.InvokeRustyKiosk => "Running the typed Rusty Kiosk action…",
        OperatorCommandKind.PullRustyKioskTags => "Exporting the Rusty Kiosk tag file…",
        OperatorCommandKind.PushRustyKioskTags => "Importing the Rusty Kiosk tag file…",
        OperatorCommandKind.ReadQuestControls => "Reading Quest power and performance status…",
        OperatorCommandKind.SetQuestKeepAwake => "Changing Quest keep-awake policy…",
        OperatorCommandKind.SetQuestPerformance => "Changing Quest CPU/GPU overrides…",
        OperatorCommandKind.FleetInstallStatus => "Checking the trusted Fleet installer release…",
        OperatorCommandKind.FleetInstall => "Verifying and opening the Fleet guided installer…",
        _ => "Working…"
    };
}

internal static partial class PowerShellCliFormatter
{
    public static string FormatArgument(string value) =>
        SafeArgumentPattern().IsMatch(value) ? value : Quote(value);

    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    [GeneratedRegex("^[A-Za-z0-9_./:\\\\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeArgumentPattern();
}
