using System.Globalization;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    public async Task<ApkPreflightResult> PreflightInspectedApkAsync(
        string serial,
        string apkPath,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var reportedPath = Path.GetFullPath(apkPath);
        if (!File.Exists(reportedPath))
        {
            throw new FileNotFoundException("The APK to preflight was not found.", reportedPath);
        }
        if (!string.Equals(Path.GetExtension(reportedPath), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The preflight input must be an .apk file.", nameof(apkPath));
        }
        using var admission = await ImmutableApkAdmission.CreateAsync(
            reportedPath,
            cancellationToken).ConfigureAwait(false);
        var inspected = await CreateApkInspector()
            .InspectManifestAsync(admission.Path, cancellationToken).ConfigureAwait(false);
        var artifact = inspected.Artifact with { Path = reportedPath };
        RejectSplitArtifact(artifact);

        var checks = new List<ApkPreflightCheck>
        {
            new(
                "artifact.single_base",
                true,
                "The immutable input is one signed base APK with a stable package identity."),
            new(
                "artifact.launcher_declared",
                inspected.Manifest.LauncherActivities.Count == 1,
                inspected.Manifest.LauncherActivities.Count == 1
                    ? "The APK declares exactly one launcher activity."
                    : "The APK must declare exactly one launcher activity for the fixed deploy route.")
        };

        var matchingDevices = (await GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(device => string.Equals(device.Serial, serial, StringComparison.Ordinal))
            .ToArray();
        var device = matchingDevices.Length == 1 ? matchingDevices[0] : null;
        checks.Add(new ApkPreflightCheck(
            "device.exact_serial",
            device is not null,
            device is not null
                ? "ADB discovery returned exactly the selected serial."
                : "ADB discovery did not return exactly one row for the selected serial."));
        checks.Add(new ApkPreflightCheck(
            "device.ready",
            device?.IsReady == true,
            device?.IsReady == true
                ? "The selected serial is authorized and ready."
                : "The selected serial is absent, unauthorized, offline, or otherwise not ready."));

        int? deviceApiLevel = null;
        var installedMatch = InstalledApkMatch.Unverified;
        InstalledApkIdentity? installed = null;
        string installedDetail = "Installed state was not queried because the selected serial is not ready.";
        string? launcherComponent = null;
        var launcherProven = false;
        var launcherDetail = "Launcher export proof requires exact installed bytes on a ready serial.";

        if (device?.IsReady == true)
        {
            var apiResult = await RunForDeviceAsync(
                serial,
                ["shell", "getprop", "ro.build.version.sdk"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            apiResult.EnsureSuccess("Read device API level");
            if (!int.TryParse(
                    apiResult.StandardOutput.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedApiLevel) || parsedApiLevel < 1)
            {
                throw new InvalidDataException("The selected device returned an invalid Android API level.");
            }
            deviceApiLevel = parsedApiLevel;

            try
            {
                installed = await ReadInstalledIdentityAsync(
                    serial,
                    artifact,
                    cancellationToken).ConfigureAwait(false);
                if (installed.Identity is null)
                {
                    installedMatch = InstalledApkMatch.Different;
                    installedDetail = "The package is installed, but its base APK bytes differ from the inspected artifact.";
                }
                else
                {
                    EnsureSameArtifact(artifact, installed);
                    installedMatch = InstalledApkMatch.Exact;
                    installedDetail = "The installed base APK exactly matches the inspected artifact bytes.";
                }
            }
            catch (PackageNotInstalledException)
            {
                installedMatch = InstalledApkMatch.Absent;
                installedDetail = "The inspected package is not installed on the selected serial.";
            }
            catch (FleetTransferLimitException)
            {
                installedMatch = InstalledApkMatch.Different;
                installedDetail = "The installed base APK exceeds the inspected artifact size and therefore differs.";
            }
            catch (InvalidDataException)
            {
                installedMatch = InstalledApkMatch.Unverified;
                installedDetail = "Installed package layout or exact-byte evidence could not be proven.";
            }

            if (installedMatch == InstalledApkMatch.Exact)
            {
                try
                {
                    var resolved = await ResolveExportedLauncherAsync(
                        serial,
                        artifact.Identity.PackageName,
                        cancellationToken).ConfigureAwait(false);
                    launcherComponent = resolved.Wire;
                    launcherProven = true;
                    launcherDetail = "Exactly one same-package launcher activity is installed and proven exported.";
                }
                catch (InvalidDataException)
                {
                    launcherDetail = "A unique same-package exported launcher activity could not be proven.";
                }
            }
        }

        var sdkCompatible = deviceApiLevel is not null &&
            deviceApiLevel.Value >= inspected.Manifest.MinimumSdkVersion;
        checks.Add(new ApkPreflightCheck(
            "device.api_compatible",
            sdkCompatible,
            deviceApiLevel is null
                ? "Device API compatibility was not available."
                : sdkCompatible
                    ? $"Device API {deviceApiLevel.Value} satisfies minSdk {inspected.Manifest.MinimumSdkVersion}."
                    : $"Device API {deviceApiLevel.Value} is below minSdk {inspected.Manifest.MinimumSdkVersion}."));
        checks.Add(new ApkPreflightCheck(
            "installed.exact_bytes",
            installedMatch == InstalledApkMatch.Exact,
            installedDetail));
        checks.Add(new ApkPreflightCheck(
            "launcher.exported",
            launcherProven,
            launcherDetail));

        var readyForDeploy = device?.IsReady == true &&
            sdkCompatible &&
            inspected.Manifest.LauncherActivities.Count == 1;
        var readyForLaunch = installedMatch == InstalledApkMatch.Exact && launcherProven;
        var readyForDiagnose = installedMatch == InstalledApkMatch.Exact;
        return new ApkPreflightResult(
            artifact,
            inspected.Manifest,
            serial,
            device,
            deviceApiLevel,
            installedMatch,
            installed,
            launcherComponent,
            readyForDeploy,
            readyForLaunch,
            readyForDiagnose,
            checks,
            CreatePreflightNextCommands(
                serial,
                reportedPath,
                readyForDeploy,
                readyForLaunch,
                readyForDiagnose));
    }

    private static IReadOnlyList<ApkPreflightNextCommand> CreatePreflightNextCommands(
        string serial,
        string apkPath,
        bool readyForDeploy,
        bool readyForLaunch,
        bool readyForDiagnose) =>
        [
            new(
                "install_launch_observe",
                ["apk", "deploy", "--serial", serial, "--file", apkPath, "--json"],
                readyForDeploy),
            new(
                "launch_exact_installed_artifact",
                ["apk", "launch", "--serial", serial, "--file", apkPath, "--json"],
                readyForLaunch),
            new(
                "capture_private_diagnostics",
                [
                    "apk", "diagnose", "--serial", serial, "--file", apkPath,
                    "--output", "<new-private-output-directory>", "--json"
                ],
                readyForDiagnose)
        ];
}
