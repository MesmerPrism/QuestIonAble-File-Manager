using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuestIonAbleFileManager.Core;
using Microsoft.Win32;

namespace QuestIonAbleFileManager.App;

public partial class MainWindow : Window
{
    private readonly AdbClient? _client;
    private readonly OperatorCommandExecutor _operator;
    private readonly string? _fleetConfigurationError;
    private readonly ObservableCollection<WifiInstallTargetChoice> _wifiInstallTargets = [];
    private RustyKioskBundle? _rustyKioskBundle;
    private RustyKioskInstallationStatus? _rustyKioskInstallation;
    private RustyKioskState? _rustyKioskState;
    private RustyKioskDirectClient? _rustyKioskDirectClient;
    private KioskDirectOperatorExecutor? _rustyKioskDirectOperator;
    private RustyKioskUsbDirectLinkSession? _rustyKioskUsbDirectSession;
    private readonly DispatcherTimer _pairingCodeRevealTimer;
    private string[] _rustyKioskDirectApkPaths = [];
    private string? _rustyKioskDirectInstallRequestId;
    private OperatorCommand? _pendingMutationCommand;
    private OperatorMutationReceipt? _lastMutationReceipt;
    private IProgress<OperatorProgress>? _activeProgress;
    private int _progressGeneration;
    private bool _busy;
    private bool _closingAfterDirectCleanup;

    public MainWindow()
    {
        InitializeComponent();
        _pairingCodeRevealTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _pairingCodeRevealTimer.Tick += (_, _) => ClearManualPairingInput();
        WifiInstallTargetsList.ItemsSource = _wifiInstallTargets;
        CpuLevelBox.ItemsSource = Enumerable.Range(0, 6);
        GpuLevelBox.ItemsSource = Enumerable.Range(0, 6);
        CpuLevelBox.SelectedItem = 3;
        GpuLevelBox.SelectedItem = 3;
        KioskTagFilterBox.ItemsSource = new[] { "All tags" };
        KioskTagFilterBox.SelectedIndex = 0;
        SetRustyKioskBundle(RustyKioskBundleLocator.TryFind());

        FleetInstallerHandoff fleetInstaller;
        try
        {
            fleetInstaller = FleetInstallerHandoff.FromEnvironment();
        }
        catch (FleetInstallerException exception)
        {
            _fleetConfigurationError = exception.Message;
            fleetInstaller = new FleetInstallerHandoff(null);
        }
        catch
        {
            _fleetConfigurationError =
                "The optional Fleet installer configuration is incomplete or invalid.";
            fleetInstaller = new FleetInstallerHandoff(null);
        }

        var adbPath = AdbLocator.Find();
        if (adbPath is null)
        {
            AdbPathText.Text = "ADB not found";
            StatusText.Text = "ADB is unavailable. Rusty Kiosk's direct link can still be used after headset setup.";
            RefreshDevicesButton.IsEnabled = false;
        }
        else
        {
            _client = new AdbClient(adbPath);
            AdbPathText.Text = $"ADB: {adbPath}";
        }
        _operator = new OperatorCommandExecutor(_client, fleetInstaller);
        if (_fleetConfigurationError is not null)
        {
            FleetInstallerStatusText.Text =
                $"Fleet installer configuration is invalid: {_fleetConfigurationError}";
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (_client is not null)
        {
            await RunBusyAsync(() => RefreshDevicesAsync(), "Looking for authorized headsets…");
        }
    }

    private async void OnRefreshDevices(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(() => RefreshDevicesAsync(), "Refreshing devices…");

    private async void OnRefreshFleetInstaller(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(
            async () =>
            {
                if (_fleetConfigurationError is not null)
                {
                    throw new FleetInstallerException(
                        "fleet_installer_configuration_invalid",
                        _fleetConfigurationError);
                }

                var execution = await ExecuteOperatorAsync(
                    OperatorCommands.FleetInstallStatus());
                var status = execution.FleetInstallerStatus ??
                    throw new InvalidOperationException(
                        "Fleet installer status did not return a receipt.");
                FleetInstallerStatusText.Text = status.Configured
                    ? $"Trusted release: {status.Product} {status.Version} ({status.Channel}); " +
                      $"source: {status.SourceKind}; status: {status.Status}; " +
                      $"last handoff: {status.LastOutcome ?? "none"}."
                    : "Fleet installer handoff is optional and is not configured.";
                StatusText.Text = "Fleet installer status checked.";
            },
            "Checking the trusted Fleet installer release…");

    private async void OnInstallFleet(object sender, RoutedEventArgs eventArgs)
    {
        if (MessageBox.Show(
                this,
                "Verify and open the Fleet-owned guided installer?\n\n" +
                "File Manager will accept only the configured signed Pages metadata and " +
                "its exact immutable GitHub Release Setup asset, verify size, hash, and " +
                "Windows signer, check Fleet's plan, and then open the visible installer. " +
                "File Manager does not configure devices or Wi-Fi.",
                "Confirm Rusty Fleet installation",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                if (_fleetConfigurationError is not null)
                {
                    throw new FleetInstallerException(
                        "fleet_installer_configuration_invalid",
                        _fleetConfigurationError);
                }

                var execution = await ExecuteOperatorAsync(
                    OperatorCommands.FleetInstall(operatorConfirmed: true));
                var receipt = execution.FleetInstallerHandoff ??
                    throw new InvalidOperationException(
                        "Fleet installer handoff did not return a receipt.");
                FleetInstallerStatusText.Text =
                    $"Verified Fleet {receipt.Version} ({receipt.Channel}); " +
                    $"guided installer exit code: {receipt.GuidedInstallerExitCode}; " +
                    $"private staging cleaned: {receipt.CleanupCompleted}.";
                StatusText.Text = "Fleet guided installer handoff completed.";
            },
            "Verifying the Fleet release and opening its guided installer…");
    }

    private void OnChooseConnectivityProfile(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a protected Fleet connectivity profile",
            Filter = "JSON profile (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            ConnectivityProfilePathBox.Text = dialog.FileName;
    }

    private async void OnImportConnectivityProfile(object sender, RoutedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(ConnectivityProfilePathBox.Text))
        {
            ShowInputMessage("Choose one protected connectivity profile JSON file first.");
            return;
        }

        var replacing = ReplaceConnectivityProfileBox.IsChecked == true;
        var confirmation = replacing
            ? "Validate this private file and replace the existing File Manager-owned " +
              "connectivity profile with the same Fleet device ID?"
            : "Validate this private file and create a File Manager-owned connectivity profile?";
        if (MessageBox.Show(
                this,
                confirmation + "\n\nThe private serial, endpoint, and pairing code remain in " +
                "the current Windows user's Credential Manager and are not displayed or sent to Fleet.",
                replacing ? "Confirm connectivity profile replacement" : "Confirm connectivity profile creation",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var command = OperatorCommands.ImportQuestConnectivityProfileFile(
                    ConnectivityProfilePathBox.Text,
                    replaceExisting: replacing,
                    operatorConfirmed: true);
                var execution = await ExecuteOperatorAsync(command);
                var receipt = execution.ConnectivityProfileMutation ??
                    throw new InvalidOperationException(
                        "Connectivity profile import did not return a receipt.");
                ConnectivityProfileStatusText.Text =
                    $"{receipt.DeviceId}: {receipt.State} ({receipt.ReasonCode}).";
                ConnectivityProfilePathBox.Clear();
                ReplaceConnectivityProfileBox.IsChecked = false;
                await RefreshConnectivityProfilesAsync(receipt.DeviceId);
            },
            "Validating and storing the private connectivity profile…");
    }

    private async void OnRefreshConnectivityProfiles(
        object sender,
        RoutedEventArgs eventArgs) =>
        await RunBusyAsync(
            () => RefreshConnectivityProfilesAsync(),
            "Refreshing private connectivity profile IDs…");

    private async void OnCheckConnectivityProfileStatus(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ConnectivityProfilesBox.SelectedItem is not QuestConnectivityProfileListEntry selected)
        {
            ShowInputMessage("Select one Fleet device ID to check.");
            return;
        }
        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(
                    OperatorCommands.QuestConnectivityProfileStatus(
                        selected.DeviceId));
                var receipt = execution.ConnectivityProfileStatus ??
                    throw new InvalidOperationException(
                        "Connectivity profile status did not return a receipt.");
                ConnectivityProfileStatusText.Text =
                    $"{receipt.DeviceId}: {receipt.State} ({receipt.ReasonCode}).";
            },
            "Checking the selected private connectivity profile…");
    }

    private async void OnSaveEnteredKioskLinkForFleet(
        object sender,
        RoutedEventArgs eventArgs)
    {
        byte[]? privateDocument = null;
        try
        {
            var usbDevice = RequireReadyUsbDevice();
            privateDocument = QuestConnectivityProfileEnrollmentDocument.Create(
                ConnectivityFleetDeviceIdBox.Text,
                usbDevice.Serial,
                KioskDirectEndpointBox.Text,
                KioskDirectPairingCodeBox.Password);
            var deviceId = ConnectivityFleetDeviceIdBox.Text;

            await RunBusyAsync(
                async () =>
                {
                    bool Confirm(QuestConnectivityProfileWriteStage stage)
                    {
                        var replacing = stage == QuestConnectivityProfileWriteStage.Replace;
                        var message = replacing
                            ? $"A connectivity profile already exists for {deviceId}. " +
                              "Replace that exact File Manager-owned record?\n\nNo write " +
                              "has occurred yet. The private fields will remain hidden."
                            : $"Create the File Manager-owned connectivity profile for {deviceId}?\n\n" +
                              "The exact selected USB serial and entered Kiosk endpoint/pairing " +
                              "code will be validated in memory and stored in the current Windows " +
                              "user's Credential Manager. This request will not replace an " +
                              "existing profile without a second confirmation.";
                        return MessageBox.Show(
                            this,
                            message,
                            replacing
                                ? "Confirm connectivity profile replacement"
                                : "Confirm new connectivity profile",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning) == MessageBoxResult.OK;
                    }

                    var receipt = await QuestConnectivityProfileWriteWorkflow.ExecuteAsync(
                        Confirm,
                        async replace =>
                        {
                            await using var privateInput = new MemoryStream(
                                privateDocument,
                                writable: false);
                            var execution = await ExecuteOperatorAsync(
                                OperatorCommands.ImportQuestConnectivityProfileStdin(
                                    replaceExisting: replace,
                                    operatorConfirmed: true),
                                privateInput);
                            return execution.ConnectivityProfileMutation ??
                                throw new InvalidOperationException(
                                    "Connectivity profile import did not return a receipt.");
                        });
                    if (receipt is null)
                    {
                        ConnectivityProfileStatusText.Text =
                            $"{deviceId}: no profile write performed.";
                        return;
                    }
                    ConnectivityProfileStatusText.Text =
                        $"{receipt.DeviceId}: {receipt.State} ({receipt.ReasonCode}).";
                    await RefreshConnectivityProfilesAsync(receipt.DeviceId);
                },
                "Validating and storing the entered Kiosk direct link…");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            QuestConnectivityProfileManagementException or
            InvalidOperationException)
        {
            ShowInputMessage(exception.Message);
        }
        finally
        {
            if (privateDocument is not null)
                CryptographicOperations.ZeroMemory(privateDocument);
            KioskDirectEndpointBox.Clear();
            ClearManualPairingInput();
        }
    }

    private async Task RefreshConnectivityProfilesAsync(string? preferredDeviceId = null)
    {
        preferredDeviceId ??=
            (ConnectivityProfilesBox.SelectedItem as QuestConnectivityProfileListEntry)?.DeviceId;
        var execution = await ExecuteOperatorAsync(
            OperatorCommands.ListQuestConnectivityProfiles());
        var receipt = execution.ConnectivityProfileList ??
            throw new InvalidOperationException(
                "Connectivity profile list did not return a receipt.");
        ConnectivityProfilesBox.ItemsSource = receipt.Profiles;
        ConnectivityProfilesBox.SelectedItem = receipt.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.DeviceId, preferredDeviceId, StringComparison.Ordinal)) ??
            receipt.Profiles.FirstOrDefault();
        ConnectivityProfileStatusText.Text = receipt.Profiles.Count == 0
            ? "No File Manager-owned Fleet connectivity profiles are enrolled."
            : $"{receipt.Profiles.Count} connectivity profile ID" +
              $"{(receipt.Profiles.Count == 1 ? string.Empty : "s")} found; " +
              $"{receipt.Profiles.Count(profile => profile.State == "invalid")} invalid.";
    }

    private async void OnRevokeConnectivityProfile(object sender, RoutedEventArgs eventArgs)
    {
        if (ConnectivityProfilesBox.SelectedItem is not QuestConnectivityProfileListEntry selected)
        {
            ShowInputMessage("Select one Fleet device ID to revoke.");
            return;
        }
        if (MessageBox.Show(
                this,
                $"Revoke the File Manager-owned connectivity profile for {selected.DeviceId}?\n\n" +
                "This removes the private Credential Manager record. It does not change the " +
                "headset or disconnect ADB.",
                "Confirm connectivity profile revocation",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(
                    OperatorCommands.RevokeQuestConnectivityProfile(
                        selected.DeviceId,
                        operatorConfirmed: true));
                var receipt = execution.ConnectivityProfileMutation ??
                    throw new InvalidOperationException(
                        "Connectivity profile revocation did not return a receipt.");
                ConnectivityProfileStatusText.Text =
                    $"{receipt.DeviceId}: {receipt.State} ({receipt.ReasonCode}).";
                await RefreshConnectivityProfilesAsync();
            },
            "Revoking the private connectivity profile…");
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        RemoteEntriesGrid.ItemsSource = null;
        PackagesList.ItemsSource = null;
        ClearKioskProjection();
        if (DeviceBox.SelectedItem is QuestDevice device)
        {
            if (AndroidInput.TryParseWifiEndpoint(device.Serial, out var host, out var port))
            {
                WifiHostBox.Text = host;
                WifiConnectPortBox.Text = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            StatusText.Text = device.IsReady
                ? $"Selected {device.DisplayName}."
                : $"{device.Serial} is {device.State}; authorize or reconnect it before operations.";
        }
    }

    private async void OnGoToRemotePath(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(RefreshRemoteDirectoryAsync, "Listing device path…");

    private async void OnGoUp(object sender, RoutedEventArgs eventArgs)
    {
        await RunBusyAsync(
            async () =>
            {
                var current = AndroidInput.RequireRemotePath(RemotePathBox.Text);
                if (current == "/")
                {
                    StatusText.Text = "Already at the device root.";
                    return;
                }

                var lastSlash = current.LastIndexOf('/');
                RemotePathBox.Text = lastSlash <= 0 ? "/" : current[..lastSlash];
                await RefreshRemoteDirectoryAsync();
            },
            "Opening parent folder…");
    }

    private async void OnRemoteEntryDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (RemoteEntriesGrid.SelectedItem is not RemoteEntry { IsDirectory: true } entry)
        {
            return;
        }

        RemotePathBox.Text = entry.FullPath;
        await RunBusyAsync(RefreshRemoteDirectoryAsync, $"Opening {entry.Name}…");
    }

    private async void OnPullSelected(object sender, RoutedEventArgs eventArgs)
    {
        if (RemoteEntriesGrid.SelectedItem is not RemoteEntry entry)
        {
            ShowInputMessage("Select a file to pull first.");
            return;
        }

        if (entry.IsDirectory)
        {
            ShowInputMessage("Folder pull is not part of the first slice. Open the folder and select a file.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = entry.Name,
            Title = "Save file from Quest",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var command = OperatorCommands.PullFile(
                    RequireReadyDevice().Serial,
                    entry.FullPath,
                    dialog.FileName);
                await ExecuteOperatorAsync(command);
                StatusText.Text = $"Saved {entry.Name} to {dialog.FileName}.";
            },
            $"Pulling {entry.Name}…");
    }

    private async void OnPushFile(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetReadyDevice(out var device))
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose a file to copy to Quest",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string remotePath;
        try
        {
            remotePath = AndroidInput.CombineRemotePath(
                AndroidInput.RequireRemotePath(RemotePathBox.Text),
                Path.GetFileName(dialog.FileName));
        }
        catch (ArgumentException exception)
        {
            ShowInputMessage(exception.Message);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Copy {Path.GetFileName(dialog.FileName)} to\n{remotePath}\n\non {device.Serial}? " +
            "ADB may replace a file with the same name.",
            "Confirm file push",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        var command = OperatorCommands.PushFile(device.Serial, dialog.FileName, remotePath);
        await RunBusyAsync(
            async () =>
            {
                await ExecuteOperatorAsync(command);
                await RefreshRemoteDirectoryAsync();
                StatusText.Text = $"Copied {Path.GetFileName(dialog.FileName)} to {remotePath}.";
            },
            $"Pushing {Path.GetFileName(dialog.FileName)}…");
    }

    private void OnBrowseInstallApk(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an APK",
            Filter = "Android packages (*.apk)|*.apk",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            InstallApkPathBox.Text = dialog.FileName;
        }
    }

    private async void OnInstallApk(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetReadyDevice(out var device))
        {
            return;
        }

        var apkPath = InstallApkPathBox.Text.Trim();
        if (!File.Exists(apkPath))
        {
            ShowInputMessage("Choose an existing APK file first.");
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Install {Path.GetFileName(apkPath)} on {device.Serial}?",
            "Confirm APK install",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        var command = OperatorCommands.InstallApk(device.Serial, apkPath, ReadInstallOptions());
        await RunBusyAsync(
            async () =>
            {
                await ExecuteOperatorAsync(command);
                StatusText.Text = $"Installed {Path.GetFileName(apkPath)} on {device.Serial}.";
            },
            $"Installing {Path.GetFileName(apkPath)}…");
    }

    private async void OnInstallApkMany(object sender, RoutedEventArgs eventArgs)
    {
        var apkPath = InstallApkPathBox.Text.Trim();
        if (!File.Exists(apkPath))
        {
            ShowInputMessage("Choose an existing APK file first.");
            return;
        }

        OperatorCommand command;
        try
        {
            command = OperatorCommands.InstallApkMany(
                GetSelectedWifiSerials(),
                apkPath,
                ReadInstallOptions(),
                ReadParallelism());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            ShowInputMessage(exception.Message);
            return;
        }

        var targets = command.Serials ??
            throw new InvalidOperationException("The parallel install command contains no targets.");
        if (!ConfirmParallelInstall(Path.GetFileName(apkPath), targets))
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(command);
                ShowParallelInstallResult(
                    execution.ParallelApkInstallResult ??
                    throw new InvalidOperationException("Parallel APK installation returned no result."));
            },
            $"Installing {Path.GetFileName(apkPath)} on {targets.Count} headsets…");
    }

    private void OnBrowseInstallApkBundle(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder containing one APK package set",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            InstallApkBundlePathBox.Text = dialog.FolderName;
        }
    }

    private async void OnInstallApkBundle(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetReadyDevice(out var device))
        {
            return;
        }

        OperatorCommand command;
        try
        {
            command = OperatorCommands.InstallApkBundle(
                device.Serial,
                InstallApkBundlePathBox.Text.Trim(),
                ReadInstallOptions());
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException)
        {
            ShowInputMessage(exception.Message);
            return;
        }

        var bundle = command.ApkBundle ??
            throw new InvalidOperationException("The APK bundle command contains no APK set.");
        var confirmation = MessageBox.Show(
            this,
            $"Install all {bundle.ApkPaths.Count} APK parts from\n{bundle.FolderPath}\n\n" +
            $"as one package set on {device.Serial}?",
            "Confirm APK bundle install",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(command);
                var result = execution.ApkBundleInstallResult ??
                    throw new InvalidOperationException("APK bundle installation returned no result.");
                StatusText.Text =
                    $"Installed {result.ApkPaths.Count} APK parts as one package set on {device.Serial}.";
            },
            $"Installing {bundle.ApkPaths.Count} APK parts…");
    }

    private async void OnInstallApkBundleMany(object sender, RoutedEventArgs eventArgs)
    {
        OperatorCommand command;
        try
        {
            command = OperatorCommands.InstallApkBundleMany(
                GetSelectedWifiSerials(),
                InstallApkBundlePathBox.Text.Trim(),
                ReadInstallOptions(),
                ReadParallelism());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            ShowInputMessage(exception.Message);
            return;
        }

        var bundle = command.ApkBundle ??
            throw new InvalidOperationException("The parallel bundle command contains no APK set.");
        var targets = command.Serials ??
            throw new InvalidOperationException("The parallel bundle command contains no targets.");
        if (!ConfirmParallelInstall($"{bundle.ApkPaths.Count}-part APK bundle", targets))
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(command);
                ShowParallelInstallResult(
                    execution.ParallelApkInstallResult ??
                    throw new InvalidOperationException("Parallel APK bundle installation returned no result."));
            },
            $"Installing {bundle.ApkPaths.Count} APK parts on {targets.Count} headsets…");
    }

    private async void OnEnableWifiAdb(object sender, RoutedEventArgs eventArgs)
    {
        QuestDevice device;
        int port;
        try
        {
            device = RequireReadyUsbDevice();
            port = ReadPort(WifiEnablePortBox);
        }
        catch (ArgumentException exception)
        {
            ShowInputMessage(exception.Message);
            return;
        }
        catch (InvalidOperationException exception)
        {
            ShowInputMessage(exception.Message);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Enable Wi-Fi ADB on the selected USB headset using TCP port {port}?\n\n" +
            "The app will bind its device identity, read its Wi-Fi address, change its debugging transport, connect from this PC, and verify the same headset.",
            "Confirm Wi-Fi ADB change",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        var command = OperatorCommands.EnableWifiAdb(device.Serial, port, operatorConfirmed: true);
        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(command);
                var result = execution.WifiAdbEnableResult ??
                    throw new InvalidOperationException("Wi-Fi ADB enablement returned no result.");
                WifiHostBox.Text = result.Host;
                WifiConnectPortBox.Text = result.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await RefreshDevicesAsync(result.Endpoint);
                StatusText.Text = $"Connected to {result.Endpoint} over Wi-Fi ADB.";
            },
            "Enabling and connecting Wi-Fi ADB…");
    }

    private async void OnConnectWifiAdb(object sender, RoutedEventArgs eventArgs)
    {
        string host;
        int port;
        try
        {
            host = AndroidInput.RequireWifiHost(WifiHostBox.Text);
            port = ReadPort(WifiConnectPortBox);
        }
        catch (ArgumentException exception)
        {
            ShowInputMessage(exception.Message);
            return;
        }

        var endpoint = AndroidInput.CreateWifiEndpoint(host, port);
        var confirmation = MessageBox.Show(
            this,
            $"Connect this PC to Wi-Fi ADB at {endpoint}?",
            "Confirm Wi-Fi ADB connection",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        var command = OperatorCommands.ConnectWifiAdb(host, port, operatorConfirmed: true);
        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(command);
                var result = execution.WifiAdbConnectionResult ??
                    throw new InvalidOperationException("Wi-Fi ADB connection returned no result.");
                await RefreshDevicesAsync(result.Endpoint);
                StatusText.Text = $"Connected to {result.Endpoint} over Wi-Fi ADB.";
            },
            $"Connecting to {endpoint}…");
    }

    private async void OnDisconnectWifiAdb(object sender, RoutedEventArgs eventArgs)
    {
        if (DeviceBox.SelectedItem is not QuestDevice device ||
            !AndroidInput.TryParseWifiEndpoint(device.Serial, out var host, out var port))
        {
            ShowInputMessage("Select a Wi-Fi headset above before disconnecting it.");
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Disconnect this PC from Wi-Fi ADB at {device.Serial}?",
            "Confirm Wi-Fi ADB disconnection",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        var command = OperatorCommands.DisconnectWifiAdb(host, port, operatorConfirmed: true);
        await RunBusyAsync(
            async () =>
            {
                await ExecuteOperatorAsync(command);
                await RefreshDevicesAsync();
                StatusText.Text = $"Disconnected {device.Serial}.";
            },
            $"Disconnecting {device.Serial}…");
    }

    private void OnSelectAllWifiTargets(object sender, RoutedEventArgs eventArgs)
    {
        foreach (var target in _wifiInstallTargets)
        {
            target.IsSelected = true;
        }
    }

    private void OnClearWifiTargets(object sender, RoutedEventArgs eventArgs)
    {
        foreach (var target in _wifiInstallTargets)
        {
            target.IsSelected = false;
        }
    }

    private async void OnRefreshPackages(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(RefreshPackagesAsync, "Loading third-party packages…");

    private async void OnExportPackage(object sender, RoutedEventArgs eventArgs)
    {
        if (PackagesList.SelectedItem is not string packageName)
        {
            ShowInputMessage("Select a package to export first.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export installed APK",
            Filter = "Android packages (*.apk)|*.apk",
            DefaultExt = ".apk",
            AddExtension = true,
            FileName = packageName + ".apk",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var command = OperatorCommands.ExportApk(
            RequireReadyDevice().Serial,
            packageName,
            dialog.FileName,
            overwrite: true);
        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(command);
                var result = execution.ApkExportResult ??
                    throw new InvalidOperationException("APK export returned no result.");
                StatusText.Text = $"Exported {packageName}. SHA-256 {result.Sha256[..12]}…";
            },
            $"Exporting {packageName}…");
    }

    private void OnBrowseKioskBundle(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the Rusty Kiosk bundle folder",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SetRustyKioskBundle(RustyKioskBundle.FromDirectory(dialog.FolderName));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            ShowInputMessage(exception.Message);
        }
    }

    private async void OnInstallKiosk(object sender, RoutedEventArgs eventArgs)
    {
        if (_rustyKioskBundle is null)
        {
            ShowInputMessage("Choose a bundle containing both Rusty Kiosk APKs first.");
            return;
        }

        QuestDevice device;
        try
        {
            device = RequireReadyUsbDevice();
        }
        catch (InvalidOperationException exception)
        {
            ShowInputMessage(exception.Message);
            return;
        }

        if (MessageBox.Show(
                this,
                "Install the Rusty Kiosk app and same-signer setup helper, then grant the helper its one-time USB setup authority?\n\n" +
                "This does not enable Wi-Fi ADB or Accessibility. Those remain separate, visible choices.",
                "Confirm Rusty Kiosk setup",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(
                    OperatorCommands.InstallRustyKiosk(
                        device.Serial,
                        _rustyKioskBundle,
                        operatorConfirmed: true,
                        product: SelectedKioskProduct()));
                var install = execution.RustyKioskInstallResult ??
                    throw new InvalidOperationException("Rusty Kiosk installation returned no verification result.");
                KioskInstallStatusText.Text = "Rusty Kiosk: installed";
                KioskSetupStatusText.Text = install.HelperReady && install.SameSignerControlGranted
                    ? "USB setup: confirmed ready"
                    : "USB setup: pending readback";
                await RefreshKioskAsync();
            },
            "Installing Rusty Kiosk…");
    }

    private async void OnProvisionKiosk(object sender, RoutedEventArgs eventArgs)
    {
        QuestDevice device;
        try
        {
            device = RequireReadyUsbDevice();
        }
        catch (InvalidOperationException exception)
        {
            ShowInputMessage(exception.Message);
            return;
        }

        if (MessageBox.Show(
                this,
                "Grant the installed Rusty Kiosk Setup helper its one-time USB authority?\n\n" +
                "No Wi-Fi ADB or Accessibility setting will be enabled automatically.",
                "Confirm Kiosk provisioning",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                await ExecuteOperatorAsync(
                    OperatorCommands.ProvisionRustyKiosk(
                        device.Serial,
                        operatorConfirmed: true,
                        product: SelectedKioskProduct()));
                await RefreshKioskAsync();
            },
            "Provisioning Rusty Kiosk Setup…");
    }

    private async void OnRefreshKiosk(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(RefreshKioskAsync, "Refreshing Rusty Kiosk status…");

    private async void OnConnectKioskDirect(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            RustyKioskDirectEndpoint endpoint;
            try
            {
                endpoint = RustyKioskDirectEndpoint.Parse(
                    KioskDirectEndpointBox.Text,
                    KioskDirectPairingCodeBox.Password);
            }
            catch (ArgumentException exception)
            {
                ShowInputMessage(exception.Message);
                return;
            }

            await RunBusyAsync(
                async () =>
                {
                    await DisconnectKioskDirectAsync(updateUi: false);
                    var client = new RustyKioskDirectClient(endpoint);
                    try
                    {
                        await AdoptKioskDirectClientAsync(client, usbSession: null);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }
                },
                "Authenticating Rusty Kiosk direct link…");
        }
        finally
        {
            ClearManualPairingInput();
        }
    }

    private async void OnConnectKioskDirectUsb(object sender, RoutedEventArgs eventArgs)
    {
        ClearManualPairingInput();
        if (_client is null)
        {
            ShowInputMessage("ADB is unavailable. Use the manual Direct Link fields or configure Platform Tools.");
            return;
        }
        QuestDevice device;
        try
        {
            device = RequireReadyUsbDevice();
        }
        catch (InvalidOperationException exception)
        {
            ShowInputMessage(exception.Message);
            return;
        }
        var product = SelectedKioskProduct();
        if (MessageBox.Show(
                this,
                $"Use the authorized USB connection to {product.WireName} Rusty Kiosk to mint one short-lived Direct Link session?\n\n" +
                "The endpoint and session credential stay in this process. If this request starts the listener, Disconnect or closing this window will stop that owned generation.",
                "Confirm authorized-USB Direct Link",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                await DisconnectKioskDirectAsync(updateUi: false);
                var session = await new RustyKioskUsbDirectLinkBootstrapper(_client)
                    .ConnectAsync(
                        device.Serial,
                        product.Channel,
                        operatorConfirmed: true);
                try
                {
                    await AdoptKioskDirectClientAsync(session.Client, session);
                }
                catch (Exception exception)
                {
                    throw await session.CloseAfterAdoptionFailureAsync(exception);
                }
            },
            "Creating a memory-only Direct Link session over authorized USB…");
    }

    private async void OnDisconnectKioskDirect(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(
            async () => await DisconnectKioskDirectAsync(updateUi: true),
            "Clearing the Direct Link session…");

    private async Task AdoptKioskDirectClientAsync(
        RustyKioskDirectClient client,
        RustyKioskUsbDirectLinkSession? usbSession)
    {
        var directOperator = new KioskDirectOperatorExecutor(client);
        var adoption = await directOperator.ExecuteAsync(KioskDirectOperatorCommand.Adopt());
        var status = adoption.Status ??
            throw new InvalidOperationException("Direct Link returned no signed status.");
        var kiosk = adoption.KioskResult ??
            throw new InvalidOperationException("Direct Link returned no Kiosk readback.");
        var stagedFiles = adoption.StagedFiles ??
            throw new InvalidOperationException("Direct Link returned no staging readback.");

        // Publish the process-memory session only after every required signed readback has
        // succeeded. Callers still own and dispose an unadopted client/session on failure.
        try
        {
            _rustyKioskDirectClient = client;
            _rustyKioskDirectOperator = directOperator;
            _rustyKioskUsbDirectSession = usbSession;
            KioskDirectStatusText.Text =
                $"Direct link: connected · {(usbSession is null ? "manual" : "authorized USB session")} · " +
                $"installer {(status.InstallerAllowed ? "allowed" : "needs wearer permission")}";
            ApplyKioskResult(kiosk);
            KioskDirectStagingList.ItemsSource = stagedFiles;
            StatusText.Text =
                $"Direct staging readback: {stagedFiles.Count} file{(stagedFiles.Count == 1 ? string.Empty : "s")}.";
        }
        catch
        {
            _rustyKioskDirectClient = null;
            _rustyKioskDirectOperator = null;
            _rustyKioskUsbDirectSession = null;
            _rustyKioskDirectInstallRequestId = null;
            KioskDirectStagingList.ItemsSource = null;
            KioskDirectStatusText.Text = "Direct link: not connected";
            throw;
        }
    }

    private async Task DisconnectKioskDirectAsync(bool updateUi)
    {
        ClearManualPairingInput();
        var usb = _rustyKioskUsbDirectSession;
        var client = _rustyKioskDirectClient;
        _rustyKioskUsbDirectSession = null;
        _rustyKioskDirectClient = null;
        _rustyKioskDirectOperator = null;
        _rustyKioskDirectInstallRequestId = null;
        RustyKioskUsbDirectCleanupReceipt? cleanup = null;
        if (usb is not null)
        {
            cleanup = await usb.CloseAsync();
        }
        else
        {
            client?.Dispose();
        }
        KioskDirectStagingList.ItemsSource = null;
        KioskDirectStatusText.Text = "Direct link: not connected";
        KioskDirectInstallStatusText.Text = "Local install: no request sent";
        if (updateUi)
        {
            StatusText.Text = cleanup is null
                ? "Cleared the PC Direct Link credential."
                : $"Cleared the PC Direct Link credential. Cleanup {cleanup.Stage.ToString().ToLowerInvariant()}: {cleanup.Message}";
        }
    }

    private void OnKioskDirectRevealChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (KioskDirectShowPairingCodeBox.IsChecked == true)
        {
            KioskDirectPairingCodeRevealText.Text = KioskDirectPairingCodeBox.Password;
            KioskDirectPairingCodeBox.Visibility = Visibility.Collapsed;
            KioskDirectPairingCodeRevealText.Visibility = Visibility.Visible;
            _pairingCodeRevealTimer.Stop();
            _pairingCodeRevealTimer.Start();
        }
        else
        {
            RemaskPairingCode();
        }
    }

    private void OnKioskDirectRevealLostFocus(object sender, RoutedEventArgs eventArgs) =>
        ClearManualPairingInput();

    private void OnDeactivated(object? sender, EventArgs eventArgs) =>
        ClearManualPairingInput();

    private void RemaskPairingCode()
    {
        _pairingCodeRevealTimer.Stop();
        KioskDirectPairingCodeRevealText.Text = string.Empty;
        KioskDirectPairingCodeRevealText.Visibility = Visibility.Collapsed;
        KioskDirectPairingCodeBox.Visibility = Visibility.Visible;
        KioskDirectShowPairingCodeBox.IsChecked = false;
    }

    private void ClearManualPairingInput()
    {
        RemaskPairingCode();
        KioskDirectPairingCodeBox.Clear();
    }

    private async void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_closingAfterDirectCleanup)
        {
            return;
        }
        eventArgs.Cancel = true;
        _closingAfterDirectCleanup = true;
        try
        {
            ClearManualPairingInput();
            await DisconnectKioskDirectAsync(updateUi: false);
        }
        finally
        {
            Close();
        }
    }

    private async void OnRefreshKioskDirectStaging(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(RefreshKioskDirectStagingAsync, "Refreshing direct staging…");

    private async void OnUploadKioskDirectFile(object sender, RoutedEventArgs eventArgs)
    {
        var client = RequireKioskDirectClient();
        var dialog = new OpenFileDialog
        {
            Title = "Upload a file to Rusty Kiosk's app-owned staging area",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                await RequireKioskDirectOperator().ExecuteAsync(
                    KioskDirectOperatorCommand.Upload(
                        dialog.FileName,
                        stagedName: null,
                        operatorConfirmed: true));
                await RefreshKioskDirectStagingAsync();
                StatusText.Text = $"Confirmed {Path.GetFileName(dialog.FileName)} in direct staging.";
            },
            $"Uploading {Path.GetFileName(dialog.FileName)}…");
    }

    private async void OnDeleteKioskDirectFile(object sender, RoutedEventArgs eventArgs)
    {
        var client = RequireKioskDirectClient();
        if (KioskDirectStagingList.SelectedItem is not RustyKioskStagedFile file)
        {
            ShowInputMessage("Select a staged file first.");
            return;
        }
        if (MessageBox.Show(
                this,
                $"Delete {file.Name} from Rusty Kiosk's app-owned staging area?",
                "Confirm staged-file deletion",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                await RequireKioskDirectOperator().ExecuteAsync(
                    KioskDirectOperatorCommand.Delete(file.Name, operatorConfirmed: true));
                await RefreshKioskDirectStagingAsync();
                StatusText.Text = $"Confirmed deletion of staged file {file.Name}.";
            },
            $"Deleting staged file {file.Name}…");
    }

    private async void OnDownloadKioskDirectFile(object sender, RoutedEventArgs eventArgs)
    {
        var client = RequireKioskDirectClient();
        if (KioskDirectStagingList.SelectedItem is not RustyKioskStagedFile file)
        {
            ShowInputMessage("Select a staged file first.");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Download the verified staged file",
            FileName = file.Name,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var result = await RequireKioskDirectOperator().ExecuteAsync(
                    KioskDirectOperatorCommand.Download(file.Name, dialog.FileName, overwrite: true));
                StatusText.Text =
                    $"Confirmed and downloaded staged file {result.LocalFileName}.";
            },
            $"Downloading and verifying {file.Name}…");
    }

    private void OnChooseKioskDirectApks(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose one APK or one complete base-and-split APK set",
            Filter = "Android packages (*.apk)|*.apk",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        _rustyKioskDirectApkPaths = dialog.FileNames.Select(Path.GetFullPath).ToArray();
        KioskDirectApkPathsBox.Text = string.Join(Environment.NewLine, _rustyKioskDirectApkPaths.Select(Path.GetFileName));
    }

    private void OnOpenAdbApkInstaller(object sender, RoutedEventArgs eventArgs)
    {
        MainTabs.SelectedItem = ApksTab;
        StatusText.Text =
            "ADB installation selected. Choose one APK or a folder containing one complete base-and-split package set.";
    }

    private async void OnInstallKioskDirectApks(object sender, RoutedEventArgs eventArgs)
    {
        var client = RequireKioskDirectClient();
        if (_rustyKioskDirectApkPaths.Length == 0)
        {
            ShowInputMessage("Choose the APK file or complete APK part set first.");
            return;
        }
        if (MessageBox.Show(
                this,
                $"Stage {_rustyKioskDirectApkPaths.Length} APK part(s) and ask Android to install them?\n\n" +
                "The PC will remain pending until the wearer confirms or cancels in the headset.",
                "Confirm local APK installation request",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                KioskDirectInstallStatusText.Text = "Local install: sent · staging verified APK parts";
                var requestId = "install_" + Guid.NewGuid().ToString("N");
                _rustyKioskDirectInstallRequestId = requestId;
                var operation = await RequireKioskDirectOperator().ExecuteAsync(
                    KioskDirectOperatorCommand.Install(
                        _rustyKioskDirectApkPaths,
                        operatorConfirmed: true,
                        requestId));
                var receipt = operation.InstallReceipt ??
                    throw new InvalidOperationException("Direct Link install returned no receipt.");
                ShowDirectInstallReceipt(receipt);
                await RefreshKioskDirectStagingAsync();
            },
            "Staging APKs and opening Android Package Installer…");
    }

    private async void OnRefreshKioskDirectInstall(object sender, RoutedEventArgs eventArgs)
    {
        var client = RequireKioskDirectClient();
        if (string.IsNullOrWhiteSpace(_rustyKioskDirectInstallRequestId))
        {
            ShowInputMessage("No direct APK install request has been sent in this session.");
            return;
        }
        await RunBusyAsync(
            async () =>
            {
                var operation = await RequireKioskDirectOperator().ExecuteAsync(
                    KioskDirectOperatorCommand.InstallStatus(_rustyKioskDirectInstallRequestId));
                ShowDirectInstallReceipt(operation.InstallReceipt ??
                    throw new InvalidOperationException("Direct Link install status returned no receipt."));
            },
            "Reading Android's matching install receipt…");
    }

    private void OnKioskFilterChanged(object sender, EventArgs eventArgs) =>
        UpdateKioskAppProjection();

    private void OnKioskAppSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        var entry = KioskAppsList.SelectedItem as RustyKioskAppEntry;
        var tags = entry?.Tags ?? [];
        KioskSelectedTagsBox.ItemsSource = tags;
        KioskSelectedTagsBox.SelectedIndex = tags.Count > 0 ? 0 : -1;
        var requirement = entry?.LaunchRequirement.ToWireName() ?? "any";
        KioskLaunchRequirementBox.SelectedItem = KioskLaunchRequirementBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, requirement, StringComparison.Ordinal));
        var stateMatchesEntry = entry is not null &&
            string.Equals(entry.Key, _rustyKioskState?.SelectedKey, StringComparison.Ordinal);
        var options = stateMatchesEntry
                ? _rustyKioskState?.SelectedLaunchOptions ?? []
                : [];
        KioskLaunchOptionsBox.ItemsSource = options;
        KioskLaunchOptionsBox.SelectedIndex = options.Count > 0 ? 0 : -1;
        KioskLaunchOptionsStatusText.Text = stateMatchesEntry &&
            _rustyKioskState is { } state
                ? KioskLaunchOptionsStatusLabel(state)
                : entry is null
                    ? "App launch options: select an app"
                    : "App launch options: use Read options for this app";
    }

    private async void OnKioskShowControls(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(
            async () => await RunKioskCommandAsync(RustyKioskCommand.ShowControls),
            "Showing Kiosk controls on the headset…");

    private async void OnKioskShowApps(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(
            async () => await RunKioskCommandAsync(RustyKioskCommand.ShowApps),
            "Showing the Kiosk app catalog on the headset…");

    private async void OnKioskFocusSearch(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(
            async () => await RunKioskCommandAsync(RustyKioskCommand.FocusSearch),
            "Requesting headset search focus…");

    private async void OnKioskFocusTagEditor(object sender, RoutedEventArgs eventArgs)
    {
        if (KioskAppsList.SelectedItem is not RustyKioskAppEntry)
        {
            ShowInputMessage("Select an app first.");
            return;
        }
        await RunBusyAsync(
            async () =>
            {
                await SelectCurrentKioskEntryAsync();
                await RunKioskCommandAsync(RustyKioskCommand.FocusTagEditor);
            },
            "Requesting headset tag-editor focus…");
    }

    private async void OnKioskLaunchNormal(object sender, RoutedEventArgs eventArgs) =>
        await LaunchSelectedKioskAppAsync(guarded: false);

    private async void OnKioskLaunchGuarded(object sender, RoutedEventArgs eventArgs) =>
        await LaunchSelectedKioskAppAsync(guarded: true);

    private async void OnReadKioskLaunchOptions(object sender, RoutedEventArgs eventArgs)
    {
        if (KioskAppsList.SelectedItem is not RustyKioskAppEntry entry)
        {
            ShowInputMessage("Select an app first.");
            return;
        }
        if (MessageBox.Show(
                this,
                $"Select {entry.Name} on the headset and read its validated launch options?",
                "Confirm launch-option discovery",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        await RunBusyAsync(
            SelectCurrentKioskEntryAsync,
            $"Reading {entry.Name}'s launch options…");
    }

    private async void OnKioskLaunchOption(object sender, RoutedEventArgs eventArgs)
    {
        if (KioskAppsList.SelectedItem is not RustyKioskAppEntry entry ||
            !string.Equals(entry.Key, _rustyKioskState?.SelectedKey, StringComparison.Ordinal))
        {
            ShowInputMessage("Use Read options for the selected app before choosing one of its launch options.");
            return;
        }
        if (KioskLaunchOptionsBox.SelectedItem is not RustyKioskLaunchOption option)
        {
            ShowInputMessage("The selected app has no validated launch option to run.");
            return;
        }
        if (MessageBox.Show(
                this,
                $"Launch {entry.Name} with option '{option.DisplayLabel}'?",
                "Confirm app launch option",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () => await RunKioskCommandAsync(RustyKioskCommand.LaunchOption, option.OptionId),
            $"Launching {entry.Name} with {option.DisplayLabel}…");
    }

    private async void OnSetKioskLaunchRequirement(object sender, RoutedEventArgs eventArgs)
    {
        if (KioskAppsList.SelectedItem is not RustyKioskAppEntry entry)
        {
            ShowInputMessage("Select an app first.");
            return;
        }
        if (KioskLaunchRequirementBox.SelectedItem is not ComboBoxItem { Tag: string value })
        {
            ShowInputMessage("Choose a launch requirement first.");
            return;
        }
        _ = RustyKioskCommands.ParseLaunchRequirement(value);
        if (MessageBox.Show(
                this,
                $"Set {entry.Name}'s launch requirement to {value}?",
                "Confirm active launch requirement",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        await RunBusyAsync(
            async () =>
            {
                await SelectCurrentKioskEntryAsync();
                await RunKioskCommandAsync(RustyKioskCommand.SetLaunchRequirement, value);
            },
            $"Setting launch requirement to {value}…");
    }

    private async void OnAddKioskTag(object sender, RoutedEventArgs eventArgs)
    {
        var tag = KioskTagEditorBox.Text.Trim();
        if (tag.Length == 0)
        {
            ShowInputMessage("Enter a tag first.");
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                await SelectCurrentKioskEntryAsync();
                await RunKioskCommandAsync(RustyKioskCommand.AddTag, tag);
                KioskTagEditorBox.Clear();
            },
            $"Adding tag {tag}…");
    }

    private async void OnRemoveKioskTag(object sender, RoutedEventArgs eventArgs)
    {
        if (KioskSelectedTagsBox.SelectedItem is not string tag)
        {
            ShowInputMessage("Select one of the app's tags first.");
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                await SelectCurrentKioskEntryAsync();
                await RunKioskCommandAsync(RustyKioskCommand.RemoveTag, tag);
            },
            $"Removing tag {tag}…");
    }

    private async void OnExportKioskTags(object sender, RoutedEventArgs eventArgs)
    {
        QuestDevice? device = null;
        if (_rustyKioskDirectClient is null)
        {
            if (!TryGetReadyDevice(out var readyDevice))
            {
                return;
            }
            device = readyDevice;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Rusty Kiosk tag file",
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "app-tags.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                if (_rustyKioskDirectClient is { } direct)
                {
                    await RequireKioskDirectOperator().ExecuteAsync(
                        KioskDirectOperatorCommand.ExportTags(dialog.FileName));
                }
                else
                {
                    await ExecuteOperatorAsync(OperatorCommands.PullRustyKioskTags(
                        device!.Serial,
                        dialog.FileName,
                        SelectedKioskProduct()));
                }
                StatusText.Text = $"Exported the Rusty Kiosk tag file to {dialog.FileName}.";
            },
            "Exporting Rusty Kiosk tags…");
    }

    private async void OnImportKioskTags(object sender, RoutedEventArgs eventArgs)
    {
        QuestDevice? device = null;
        if (_rustyKioskDirectClient is null)
        {
            if (!TryGetReadyDevice(out var readyDevice))
            {
                return;
            }
            device = readyDevice;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import a Rusty Kiosk tag file",
            Filter = "JSON files (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            RustyKioskTagFile.ValidateAndRead(dialog.FileName);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or System.Text.Json.JsonException)
        {
            ShowInputMessage(exception.Message);
            return;
        }

        if (MessageBox.Show(
                this,
                "Replace the active Rusty Kiosk tag file with this validated file and hotload it now?",
                "Confirm tag hotload",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                if (_rustyKioskDirectClient is { } direct)
                {
                    KioskSyncStatusText.Text = "PC/headset sync: sent · tag file replacement";
                    var directResult = await RequireKioskDirectOperator().ExecuteAsync(
                        KioskDirectOperatorCommand.ImportTags(
                            dialog.FileName,
                            operatorConfirmed: true));
                    KioskSyncStatusText.Text = "PC/headset sync: pending · reading hotloaded catalogue";
                    ApplyKioskResult(directResult.KioskResult ??
                        throw new InvalidOperationException("Direct tag import returned no Kiosk readback."));
                    KioskSyncStatusText.Text = "PC/headset sync: confirmed · tag file hotloaded and catalogue read back";
                }
                else
                {
                    var execution = await ExecuteOperatorAsync(
                        OperatorCommands.PushRustyKioskTags(
                            device!.Serial,
                            dialog.FileName,
                            operatorConfirmed: true,
                            product: SelectedKioskProduct()));
                    ApplyKioskExecution(execution);
                }
            },
            "Importing and hotloading Rusty Kiosk tags…");
    }

    private async void OnKioskRequestWifiAdb(object sender, RoutedEventArgs eventArgs) =>
        await ConfirmAndRunKioskControlAsync(
            RustyKioskCommand.RequestWifiAdb,
            "Ask Meta to show its Wi-Fi ADB permission prompt in the headset?\n\nThe PC will show this as pending until the headset reports the permission as enabled.");

    private async void OnKioskDisableWifiAdb(object sender, RoutedEventArgs eventArgs) =>
        await ConfirmAndRunKioskControlAsync(
            RustyKioskCommand.DisableWifiAdb,
            "Disable Wi-Fi ADB on the headset? A current wireless ADB connection may close immediately.");

    private async void OnKioskEnableAutoWifi(object sender, RoutedEventArgs eventArgs) =>
        await ConfirmAndRunKioskControlAsync(
            RustyKioskCommand.EnableWifiAfterBoot,
            "Ask for Meta's Wi-Fi ADB permission after each restart until you turn this preference off?");

    private async void OnKioskDisableAutoWifi(object sender, RoutedEventArgs eventArgs) =>
        await ConfirmAndRunKioskControlAsync(
            RustyKioskCommand.DisableWifiAfterBoot,
            "Stop requesting Wi-Fi ADB permission after restart?");

    private async void OnKioskEnableAccessibility(object sender, RoutedEventArgs eventArgs) =>
        await ConfirmAndRunKioskControlAsync(
            RustyKioskCommand.EnableAccessibility,
            "Enable Rusty Kiosk's Accessibility watchdog? Guard behavior remains inactive until an app is launched in Kiosk mode.");

    private async void OnKioskDisableAccessibility(object sender, RoutedEventArgs eventArgs) =>
        await ConfirmAndRunKioskControlAsync(
            RustyKioskCommand.DisableAccessibility,
            "Disable Rusty Kiosk's Accessibility watchdog?");

    private async void OnCancelKioskPendingLaunch(object sender, RoutedEventArgs eventArgs) =>
        await ConfirmAndRunKioskControlAsync(
            RustyKioskCommand.CancelPendingLaunch,
            "Cancel the pending launch that is waiting for its active Wi-Fi requirement?");

    private async void OnKioskPassthroughNatural(object sender, RoutedEventArgs eventArgs) =>
        await ConfirmAndRunKioskControlAsync(
            RustyKioskCommand.PassthroughNatural,
            "Switch Rusty Kiosk to natural system passthrough?");

    private async void OnKioskPassthroughContour(object sender, RoutedEventArgs eventArgs) =>
        await ConfirmAndRunKioskControlAsync(
            RustyKioskCommand.PassthroughContour,
            "Switch Rusty Kiosk to its contour-LUT passthrough style?");

    private async void OnEnableKeepAwake(object sender, RoutedEventArgs eventArgs) =>
        await SetKeepAwakeAsync(enabled: true);

    private async void OnDisableKeepAwake(object sender, RoutedEventArgs eventArgs) =>
        await SetKeepAwakeAsync(enabled: false);

    private async void OnApplyPerformance(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetReadyDevice(out var device))
        {
            return;
        }

        if (CpuLevelBox.SelectedItem is not int cpu || GpuLevelBox.SelectedItem is not int gpu)
        {
            ShowInputMessage("Choose both a CPU and GPU level from 0 through 5.");
            return;
        }

        if (MessageBox.Show(
                this,
                $"Set fixed Quest CPU/GPU levels to {cpu}/{gpu}?\n\nThese overrides remain until cleared or the headset restarts.",
                "Confirm performance override",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(
                    OperatorCommands.SetQuestPerformance(
                        device.Serial,
                        cpu,
                        gpu,
                        clear: false,
                        operatorConfirmed: true));
                ApplyQuestControlStatus(execution.QuestControlStatus);
            },
            "Applying fixed CPU/GPU levels…");
    }

    private async void OnClearPerformance(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetReadyDevice(out var device))
        {
            return;
        }

        if (MessageBox.Show(
                this,
                "Clear the fixed CPU/GPU overrides and restore application-controlled levels?",
                "Confirm performance restore",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(
                    OperatorCommands.SetQuestPerformance(
                        device.Serial,
                        cpuLevel: null,
                        gpuLevel: null,
                        clear: true,
                        operatorConfirmed: true));
                ApplyQuestControlStatus(execution.QuestControlStatus);
            },
            "Restoring app-controlled CPU/GPU levels…");
    }

    private async void OnRefreshQuestControls(object sender, RoutedEventArgs eventArgs) =>
        await RunBusyAsync(RefreshQuestControlsAsync, "Refreshing power and performance status…");

    private async Task RefreshKioskAsync()
    {
        if (_rustyKioskDirectClient is { } direct)
        {
            var statusOperation = await RequireKioskDirectOperator().ExecuteAsync(
                KioskDirectOperatorCommand.Status());
            var directStatus = statusOperation.Status ??
                throw new InvalidOperationException("Direct Link returned no signed status.");
            KioskDirectStatusText.Text =
                $"Direct link: connected · installer {(directStatus.InstallerAllowed ? "allowed" : "needs wearer permission")}";
            var kioskOperation = await RequireKioskDirectOperator().ExecuteAsync(
                KioskDirectOperatorCommand.Invoke(
                    RustyKioskCommand.Status,
                    value: null,
                    operatorConfirmed: true));
            ApplyKioskResult(kioskOperation.KioskResult ??
                throw new InvalidOperationException("Direct Link returned no Kiosk readback."));
            return;
        }

        var command = OperatorCommands.InspectRustyKiosk(
            RequireReadyDevice().Serial,
            SelectedKioskProduct());
        var execution = await ExecuteOperatorAsync(command);
        ApplyKioskExecution(execution);
        ReconcilePendingMutation(execution);
    }

    private async Task RefreshQuestControlsAsync()
    {
        var execution = await ExecuteOperatorAsync(
            OperatorCommands.ReadQuestControls(RequireReadyDevice().Serial));
        ApplyQuestControlStatus(execution.QuestControlStatus);
        ReconcilePendingMutation(execution);
    }

    private async Task RefreshKioskDirectStagingAsync()
    {
        var result = await RequireKioskDirectOperator().ExecuteAsync(
            KioskDirectOperatorCommand.ListStaging());
        var files = result.StagedFiles ?? [];
        KioskDirectStagingList.ItemsSource = files;
        StatusText.Text = $"Direct staging readback: {files.Count} file{(files.Count == 1 ? string.Empty : "s")}.";
    }

    private RustyKioskDirectClient RequireKioskDirectClient() =>
        _rustyKioskDirectClient ?? throw new InvalidOperationException(
            "Connect Rusty Kiosk's direct link with the headset address and pairing code first.");

    private KioskDirectOperatorExecutor RequireKioskDirectOperator() =>
        _rustyKioskDirectOperator ?? throw new InvalidOperationException(
            "Connect Rusty Kiosk's Direct Link first.");

    private void ShowDirectInstallReceipt(RustyKioskDirectInstallReceipt receipt)
    {
        _rustyKioskDirectInstallRequestId = receipt.RequestId;
        var stage = receipt.Installed
            ? "confirmed"
            : receipt.Failed
                ? "failed"
                : "pending";
        KioskDirectInstallStatusText.Text =
            $"Local install: {stage} · {receipt.State} · {receipt.Message}";
        KioskSyncStatusText.Text =
            $"PC/headset sync: {stage} · Android install receipt {receipt.RequestId}";
        StatusText.Text = receipt.Message;
    }

    private async Task LaunchSelectedKioskAppAsync(bool guarded)
    {
        if (KioskAppsList.SelectedItem is not RustyKioskAppEntry entry)
        {
            ShowInputMessage("Select an app first.");
            return;
        }

        if (!entry.Installed || !entry.Launchable)
        {
            ShowInputMessage($"{entry.Name} cannot be launched because it is {entry.StatusLabel.ToLowerInvariant()}.");
            return;
        }

        var mode = guarded ? "Kiosk" : "normal";
        if (MessageBox.Show(
                this,
                $"Launch {entry.Name} in {mode} mode?",
                $"Confirm {mode} launch",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                await SelectCurrentKioskEntryAsync();
                await RunKioskCommandAsync(
                    guarded ? RustyKioskCommand.LaunchKiosk : RustyKioskCommand.LaunchNormal);
            },
            $"Launching {entry.Name}…");
    }

    private async Task SelectCurrentKioskEntryAsync()
    {
        if (KioskAppsList.SelectedItem is not RustyKioskAppEntry entry)
        {
            throw new InvalidOperationException("Select an app first.");
        }

        await RunKioskCommandAsync(RustyKioskCommand.Select, entry.Key);
    }

    private async Task ConfirmAndRunKioskControlAsync(RustyKioskCommand command, string prompt)
    {
        if (_rustyKioskDirectClient is null && !TryGetReadyDevice(out _))
        {
            return;
        }

        if (MessageBox.Show(
                this,
                prompt,
                "Confirm Rusty Kiosk control",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () => await RunKioskCommandAsync(command),
            $"Requesting {command.ToWireName()}…");
    }

    private async Task RunKioskCommandAsync(RustyKioskCommand command, string? value = null)
    {
        if (_rustyKioskDirectClient is { } direct)
        {
            KioskSyncStatusText.Text = $"PC/headset sync: sent · {command.ToWireName()}";
            var operation = await RequireKioskDirectOperator().ExecuteAsync(
                KioskDirectOperatorCommand.Invoke(
                    command,
                    value,
                    operatorConfirmed: true));
            var result = operation.KioskResult;
            if (result is null)
            {
                KioskSyncStatusText.Text =
                    $"PC/headset sync: {operation.Mutation.Stage.ToString().ToLowerInvariant()} · {operation.Mutation.Message}";
                return;
            }
            KioskSyncStatusText.Text = $"PC/headset sync: pending · checking signed effective state for {command.ToWireName()}";
            ApplyKioskResult(result);
            KioskSyncStatusText.Text = RustyKioskReadback.Confirms(command, value, result)
                ? $"PC/headset sync: confirmed · {command.ToWireName()}"
                : $"PC/headset sync: pending · wearer or headset confirmation required for {command.ToWireName()}";
            return;
        }

        var execution = await ExecuteOperatorAsync(
            OperatorCommands.InvokeRustyKiosk(
                RequireReadyDevice().Serial,
                command,
                value,
                operatorConfirmed: true,
                product: SelectedKioskProduct()));
        ApplyKioskExecution(execution);
    }

    private RustyKioskProductContract SelectedKioskProduct()
    {
        var channel = (KioskProductChannelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "stable";
        return RustyKioskProductContract.Parse(channel);
    }

    private async Task SetKeepAwakeAsync(bool enabled)
    {
        if (!TryGetReadyDevice(out var device))
        {
            return;
        }

        if (!int.TryParse(KeepAwakeDurationBox.Text, out var duration) ||
            duration is < QuestAwakeContract.MinimumHoldDurationMilliseconds
                or > QuestAwakeContract.MaximumHoldDurationMilliseconds)
        {
            ShowInputMessage("Keep-awake duration must be a whole number from 60000 through 28800000 milliseconds (eight hours).");
            return;
        }

        var choice = enabled ? "keep the headset awake" : "restore normal power and proximity behavior";
        if (MessageBox.Show(
                this,
                $"Request the headset to {choice}?",
                "Confirm power policy change",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                var execution = await ExecuteOperatorAsync(
                    OperatorCommands.SetQuestKeepAwake(
                        device.Serial,
                        enabled,
                        duration,
                        operatorConfirmed: true));
                ApplyQuestControlStatus(execution.QuestControlStatus);
            },
            enabled ? "Enabling keep-awake…" : "Restoring normal power behavior…");
    }

    private void SetRustyKioskBundle(RustyKioskBundle? bundle)
    {
        _rustyKioskBundle = bundle;
        if (bundle?.DeclaredProduct is { } declaredProduct)
        {
            KioskProductChannelBox.SelectedItem = KioskProductChannelBox.Items
                .OfType<ComboBoxItem>()
                .Single(item => string.Equals(
                    item.Tag as string,
                    declaredProduct.WireName,
                    StringComparison.Ordinal));
        }
        KioskBundlePathBox.Text = bundle?.Source ?? string.Empty;
        KioskBundleStatusText.Text = bundle is null
            ? "Kiosk bundle not located. Normal file-manager features are still fully available."
            : $"Bundle ready: {Path.GetFileName(bundle.MainApkPath)} + {Path.GetFileName(bundle.SetupHelperApkPath)}";
    }

    private void ClearKioskProjection()
    {
        _rustyKioskInstallation = null;
        _rustyKioskState = null;
        KioskAppsList.ItemsSource = null;
        KioskSelectedTagsBox.ItemsSource = null;
        KioskLaunchOptionsBox.ItemsSource = null;
        KioskTagFilterBox.ItemsSource = new[] { "All tags" };
        KioskTagFilterBox.SelectedIndex = 0;
        KioskInstallStatusText.Text = "Rusty Kiosk: not checked";
        KioskSetupStatusText.Text = "USB setup: not checked";
        KioskWifiStatusText.Text = "Wireless debugging: not checked";
        KioskAutoWifiStatusText.Text = "Request after restart: not checked";
        KioskAccessibilityStatusText.Text = "Accessibility guard: not checked";
        KioskPanelStatusText.Text = "Headset panel: not checked";
        KioskPendingLaunchStatusText.Text = "Pending requirement launch: not checked";
        KioskPassthroughStatusText.Text = "Passthrough: not checked";
        KioskLaunchOptionsStatusText.Text = "App launch options: not checked";
        HeadsetBatteryText.Text = "Headset battery: not checked";
        ControllerBatteryText.Text = "Controllers: not checked";
        KeepAwakeStatusText.Text = "Keep awake: not checked";
        PerformanceStatusText.Text = "CPU/GPU: not checked";
    }

    private void ApplyKioskExecution(OperatorExecutionResult execution)
    {
        if (execution.RustyKioskInstallationStatus is { } installation)
        {
            _rustyKioskInstallation = installation;
            KioskInstallStatusText.Text = installation.MainInstalled
                ? $"Rusty Kiosk: {installation.MainVersion ?? "installed"}"
                : "Rusty Kiosk: not installed (file manager remains available)";
            KioskSetupStatusText.Text = installation.SetupHelperReady && installation.SameSignerControlGranted
                ? $"USB setup: confirmed ready ({installation.SetupHelperVersion ?? "helper installed"})"
                : installation.SetupHelperInstalled
                    ? "USB setup: helper installed, provisioning required"
                    : "USB setup: helper not installed";
        }

        if (execution.RustyKioskOperatorResult is not { } kiosk)
        {
            _rustyKioskState = null;
            KioskAppsList.ItemsSource = null;
            return;
        }

        ApplyKioskResult(kiosk);
    }

    private void ApplyKioskResult(RustyKioskOperatorResult kiosk)
    {
        _rustyKioskState = kiosk.State;
        KioskWifiStatusText.Text = $"Wireless debugging: {(kiosk.State.WifiAdbEnabled ? "enabled" : "disabled")}";
        KioskAutoWifiStatusText.Text = $"Request after restart: {(kiosk.State.RequestWifiAdbAfterBoot ? "enabled" : "disabled")}";
        KioskAccessibilityStatusText.Text =
            $"Accessibility guard: {(kiosk.State.AccessibilityEnabled ? "enabled" : "disabled")}; " +
            $"guard {(kiosk.State.GuardArmed ? "armed" : "inactive")}";
        KioskPanelStatusText.Text = kiosk.State.ControlsOpen switch
        {
            true => "Headset panel: controls",
            false => "Headset panel: apps",
            null => "Headset panel: unavailable in this Kiosk version"
        };
        KioskPendingLaunchStatusText.Text = kiosk.State.PendingRequirementLaunch switch
        {
            true => $"Pending requirement launch: {kiosk.State.PendingRequirementLaunchId ?? "waiting for wearer action"}",
            false => "Pending requirement launch: none",
            null => "Pending requirement launch: unavailable in this Kiosk version"
        };
        KioskPassthroughStatusText.Text = kiosk.State.PassthroughStyle is { } style
            ? $"Passthrough: {style.ToWireName()}; system " +
              $"{(kiosk.State.SystemPassthroughEnabled == true ? "enabled" : "disabled")}; " +
              $"LUT {(kiosk.State.PassthroughLutApplied == true ? "applied" : "not applied")}"
            : "Passthrough: unavailable in this Kiosk version";
        KioskLaunchOptionsStatusText.Text = KioskLaunchOptionsStatusLabel(kiosk.State);
        var previousTag = KioskTagFilterBox.SelectedItem as string;
        var tagChoices = new[] { "All tags" }.Concat(kiosk.State.Tags).ToArray();
        KioskTagFilterBox.ItemsSource = tagChoices;
        KioskTagFilterBox.SelectedItem = tagChoices.Contains(previousTag, StringComparer.OrdinalIgnoreCase)
            ? tagChoices.First(tag => string.Equals(tag, previousTag, StringComparison.OrdinalIgnoreCase))
            : "All tags";
        UpdateKioskAppProjection();
        StatusText.Text = kiosk.Message;
    }

    private static string KioskLaunchOptionsStatusLabel(RustyKioskState state)
    {
        if (state.SelectedLaunchOptionsStatus is not { } status)
        {
            return "App launch options: unavailable in this Kiosk version";
        }
        var message = string.IsNullOrWhiteSpace(state.SelectedLaunchOptionsMessage)
            ? string.Empty
            : $" · {state.SelectedLaunchOptionsMessage}";
        var binding = state.SelectedLaunchOptionsBinding is { } exact
            ? $" · owner {exact.PackageName} · UID {exact.Uid} · binding {exact.BindingSha256[..12]}"
            : string.Empty;
        return $"App launch options: {status.ToWireName()}{message}{binding}";
    }

    private void UpdateKioskAppProjection()
    {
        if (_rustyKioskState is null)
        {
            return;
        }

        var selectedKey = (KioskAppsList.SelectedItem as RustyKioskAppEntry)?.Key ??
            _rustyKioskState.SelectedKey;
        var search = KioskSearchBox.Text.Trim();
        var tag = KioskTagFilterBox.SelectedItem as string;
        var entries = _rustyKioskState.Entries
            .Where(entry => search.Length == 0 ||
                            entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            (entry.PackageName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                            entry.Tags.Any(candidate => candidate.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Where(entry => string.IsNullOrWhiteSpace(tag) ||
                            string.Equals(tag, "All tags", StringComparison.Ordinal) ||
                            entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        KioskAppsList.ItemsSource = entries;
        KioskAppsList.SelectedItem = entries.FirstOrDefault(entry =>
            string.Equals(entry.Key, selectedKey, StringComparison.Ordinal));
    }

    private void ApplyQuestControlStatus(QuestControlStatus? status)
    {
        if (status is null)
        {
            return;
        }

        HeadsetBatteryText.Text = $"Headset battery: {status.HeadsetBatteryLabel}";
        ControllerBatteryText.Text = $"Controllers: {status.ControllerBatteryLabel}";
        var hold = status.ProximityHoldDurationMilliseconds is int duration &&
                   status.ProximityHoldRemainingMilliseconds is int remaining
            ? $"{duration} ms hold, {remaining} ms remaining"
            : "no bounded hold observed";
        KeepAwakeStatusText.Text =
            $"Stay-on {(status.StayOn ? "active" : "inactive")}; " +
            $"{status.Wakefulness}/{status.DisplayState}; proximity {status.ProximityState}; {hold}";
        PerformanceStatusText.Text =
            $"CPU/GPU: {DisplayPerformanceLevel(status.CpuLevel)} / {DisplayPerformanceLevel(status.GpuLevel)}";
    }

    private void ReconcilePendingMutation(OperatorExecutionResult latestReadback)
    {
        if (_lastMutationReceipt is not { IsTerminal: false } receipt || _pendingMutationCommand is null)
        {
            return;
        }

        try
        {
            _lastMutationReceipt = OperatorMutationReconciler.Reconcile(
                receipt,
                _pendingMutationCommand,
                latestReadback);
            ShowMutationReceipt(_lastMutationReceipt);
            if (_lastMutationReceipt.IsTerminal)
            {
                _pendingMutationCommand = null;
            }
        }
        catch (InvalidOperationException)
        {
            // This refresh did not contain the evidence type needed by the pending operation.
        }
    }

    private void ShowMutationReceipt(OperatorMutationReceipt receipt)
    {
        KioskSyncStatusText.Text =
            $"PC/headset sync: {receipt.Stage.ToString().ToLowerInvariant()} — {receipt.ObservedState}";
        AutomationProperties.SetName(KioskSyncStatusText, KioskSyncStatusText.Text);
    }

    private static string DisplayPerformanceLevel(string value) =>
        string.IsNullOrWhiteSpace(value) ? "app controlled" : value;

    private async Task RefreshDevicesAsync(string? preferredSerial = null)
    {
        var previousPrimarySerial = preferredSerial ?? (DeviceBox.SelectedItem as QuestDevice)?.Serial;
        var selectedWifiSerials = _wifiInstallTargets
            .Where(static target => target.IsSelected)
            .Select(static target => target.Device.Serial)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var command = OperatorCommands.DiscoverDevices();
        var execution = await ExecuteOperatorAsync(command);
        var devices = execution.Devices ??
            throw new InvalidOperationException("Device discovery returned no device collection.");
        DeviceBox.ItemsSource = devices;
        DeviceBox.SelectedItem = devices.FirstOrDefault(device =>
            string.Equals(device.Serial, previousPrimarySerial, StringComparison.OrdinalIgnoreCase)) ??
            devices.FirstOrDefault(static device => device.IsReady) ??
            devices.FirstOrDefault();

        _wifiInstallTargets.Clear();
        foreach (var device in devices.Where(static candidate => candidate.IsReady && candidate.IsWifiConnection))
        {
            _wifiInstallTargets.Add(new WifiInstallTargetChoice(
                device,
                selectedWifiSerials.Contains(device.Serial)));
        }

        StatusText.Text = devices.Count == 0
            ? "No ADB devices found. Connect a Quest and approve USB debugging in-headset."
            : $"Found {devices.Count} ADB device{(devices.Count == 1 ? string.Empty : "s")}.";
    }

    private async Task RefreshRemoteDirectoryAsync()
    {
        var path = AndroidInput.RequireRemotePath(RemotePathBox.Text);
        RemotePathBox.Text = path;
        var command = OperatorCommands.ListFiles(RequireReadyDevice().Serial, path);
        var execution = await ExecuteOperatorAsync(command);
        var entries = execution.RemoteEntries ??
            throw new InvalidOperationException("File listing returned no entry collection.");
        RemoteEntriesGrid.ItemsSource = entries;
        StatusText.Text = $"{entries.Count} entries in {path}.";
    }

    private async Task RefreshPackagesAsync()
    {
        var command = OperatorCommands.ListPackages(RequireReadyDevice().Serial);
        var execution = await ExecuteOperatorAsync(command);
        var packages = execution.Packages ??
            throw new InvalidOperationException("Package listing returned no package collection.");
        PackagesList.ItemsSource = packages;
        StatusText.Text = $"Loaded {packages.Count} third-party packages.";
    }

    private QuestDevice RequireReadyDevice()
    {
        if (DeviceBox.SelectedItem is not QuestDevice device)
        {
            throw new InvalidOperationException("Select a headset first.");
        }

        if (!device.IsReady)
        {
            throw new InvalidOperationException(
                $"{device.Serial} is {device.State}. Authorize or reconnect it before continuing.");
        }

        return device;
    }

    private QuestDevice RequireReadyUsbDevice()
    {
        var device = RequireReadyDevice();
        if (device.IsWifiConnection)
        {
            throw new InvalidOperationException(
                "Select the directly connected USB headset before enabling Wi-Fi ADB.");
        }

        return device;
    }

    private bool TryGetReadyDevice(out QuestDevice device)
    {
        try
        {
            device = RequireReadyDevice();
            return true;
        }
        catch (InvalidOperationException exception)
        {
            device = null!;
            ShowInputMessage(exception.Message);
            return false;
        }
    }

    private OperatorCommandExecutor RequireOperator() => _operator;

    private async Task<OperatorExecutionResult> ExecuteOperatorAsync(
        OperatorCommand command,
        Stream? privateInput = null)
    {
        var execution = await RequireOperator().ExecuteAsync(
            command,
            progress: _activeProgress,
            privateInput: privateInput);
        if (execution.MutationReceipt is { } receipt)
        {
            _lastMutationReceipt = receipt;
            _pendingMutationCommand = receipt.IsTerminal ? null : command;
            ShowMutationReceipt(receipt);
        }

        return execution;
    }

    private ApkInstallOptions ReadInstallOptions() =>
        new(
            ReplaceExisting: ReplaceExistingBox.IsChecked == true,
            AllowDowngrade: AllowDowngradeBox.IsChecked == true,
            GrantRuntimePermissions: GrantPermissionsBox.IsChecked == true,
            AllowTestPackages: AllowTestPackageBox.IsChecked == true);

    private IReadOnlyList<string> GetSelectedWifiSerials() =>
        _wifiInstallTargets
            .Where(static target => target.IsSelected)
            .Select(static target => target.Device.Serial)
            .ToArray();

    private int ReadParallelism()
    {
        if (!int.TryParse(ParallelismBox.Text, out var value))
        {
            throw new ArgumentException("Parallel limit must be a whole number between 1 and 16.");
        }

        return AndroidInput.RequireParallelism(value);
    }

    private static int ReadPort(TextBox textBox)
    {
        if (!int.TryParse(textBox.Text, out var value))
        {
            throw new ArgumentException("The Wi-Fi ADB port must be a whole number between 1 and 65535.");
        }

        return AndroidInput.RequireTcpPort(value);
    }

    private bool ConfirmParallelInstall(string artifactLabel, IReadOnlyList<string> targets)
    {
        var targetList = string.Join(Environment.NewLine, targets.Select(static serial => $"• {serial}"));
        return MessageBox.Show(
            this,
            $"Install {artifactLabel} on all {targets.Count} checked Wi-Fi headsets in parallel?\n\n" +
            targetList,
            "Confirm parallel APK install",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    private void ShowParallelInstallResult(ParallelApkInstallResult result)
    {
        StatusText.Text =
            $"Installed successfully on {result.SucceededCount} of {result.Targets.Count} Wi-Fi headsets.";
        var details = string.Join(
            Environment.NewLine,
            result.Targets.Select(target =>
                $"{(target.Succeeded ? "✓" : "✗")} {target.Serial}: {target.Summary}"));
        MessageBox.Show(
            this,
            $"{StatusText.Text}\n\n{details}",
            result.Succeeded ? "Parallel install complete" : "Parallel install completed with failures",
            MessageBoxButton.OK,
            result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task RunBusyAsync(Func<Task> action, string progressMessage)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        var progressGeneration = ++_progressGeneration;
        _activeProgress = new Progress<OperatorProgress>(progress =>
        {
            if (_busy && progressGeneration == _progressGeneration)
            {
                UpdateOperationProgress(progress);
            }
        });
        StatusText.Text = progressMessage;
        OperationProgressBar.Value = 0;
        OperationProgressBar.IsIndeterminate = true;
        OperationProgressBar.ToolTip = progressMessage;
        AutomationProperties.SetName(OperationProgressBar, progressMessage);
        OperationProgressBar.Visibility = Visibility.Visible;
        MainTabs.IsEnabled = false;
        RefreshDevicesButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            MessageBox.Show(
                this,
                exception.Message,
                "Operation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _activeProgress = null;
            _progressGeneration++;
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = 0;
            OperationProgressBar.ToolTip = null;
            OperationProgressBar.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
            MainTabs.IsEnabled = true;
            RefreshDevicesButton.IsEnabled = _client is not null;
            _busy = false;
        }
    }

    private void UpdateOperationProgress(OperatorProgress progress)
    {
        StatusText.Text = progress.Message;
        if (progress.Stage.StartsWith("mutation-", StringComparison.Ordinal))
        {
            KioskSyncStatusText.Text = $"PC/headset sync: {progress.Message}";
        }

        AutomationProperties.SetName(OperationProgressBar, progress.Message);
        if (progress.IsIndeterminate)
        {
            OperationProgressBar.IsIndeterminate = true;
            OperationProgressBar.Value = 0;
            OperationProgressBar.ToolTip = progress.Message;
            return;
        }

        OperationProgressBar.IsIndeterminate = false;
        OperationProgressBar.Value = progress.Percentage;
        OperationProgressBar.ToolTip =
            $"{progress.Message} ({progress.CompletedUnits} of {progress.TotalUnits})";
    }

    private void ShowInputMessage(string message) =>
        MessageBox.Show(this, message, "QuestIonAble File Manager", MessageBoxButton.OK, MessageBoxImage.Information);

    private sealed class WifiInstallTargetChoice : INotifyPropertyChanged
    {
        private bool _isSelected;

        public WifiInstallTargetChoice(QuestDevice device, bool isSelected)
        {
            Device = device;
            _isSelected = isSelected;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public QuestDevice Device { get; }

        public string DisplayName => Device.DisplayName;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
