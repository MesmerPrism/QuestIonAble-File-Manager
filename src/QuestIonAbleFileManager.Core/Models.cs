using System.Collections.ObjectModel;

namespace QuestIonAbleFileManager.Core;

public sealed record CommandResult(
    string FileName,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;

    public string CondensedOutput
    {
        get
        {
            var output = string.Join(
                Environment.NewLine,
                new[] { StandardError.Trim(), StandardOutput.Trim() }
                    .Where(static value => value.Length > 0));
            return output.Length == 0 ? $"Command exited with code {ExitCode}." : output;
        }
    }

    public CommandResult EnsureSuccess(string operation)
    {
        if (!Succeeded)
        {
            throw new AdbCommandException(operation, this);
        }

        return this;
    }
}

public sealed record StreamingCommandResult(
    CommandResult CommandResult,
    long BytesWritten,
    string Sha256);

public sealed record QuestDevice(
    string Serial,
    string State,
    string? Model,
    string? Product)
{
    public bool IsReady => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    public bool IsWifiConnection => AndroidInput.TryParseWifiEndpoint(Serial, out _, out _);

    public string DisplayName
    {
        get
        {
            var transport = IsWifiConnection ? "Wi-Fi" : "USB";
            var label = string.IsNullOrWhiteSpace(Model) ? Serial : $"{Model} — {Serial}";
            label = $"{label} [{transport}]";
            return IsReady ? label : $"{label} ({State})";
        }
    }
}

public sealed record RemoteEntry(string Name, string FullPath, bool IsDirectory)
{
    public string TypeLabel => IsDirectory ? "Folder" : "File";
}

public sealed record QuestPackage(string PackageName, IReadOnlyList<string> ApkPaths)
{
    public bool IsSplitPackage => ApkPaths.Count > 1;
}

public sealed record ApkInstallOptions(
    bool ReplaceExisting = true,
    bool AllowDowngrade = false,
    bool GrantRuntimePermissions = false,
    bool AllowTestPackages = false);

public sealed record ApkArtifactIdentity(
    string PackageName,
    long VersionCode,
    string? VersionName,
    string SignerSha256,
    string? SplitName);

public sealed record ApkArtifactInspection(
    string Path,
    long SizeBytes,
    string Sha256,
    ApkArtifactIdentity Identity);

public sealed record ApkArtifactManifestFacts(
    int MinimumSdkVersion,
    int? TargetSdkVersion,
    IReadOnlyList<string> LauncherActivities);

public enum InstalledApkMatch
{
    Absent,
    Exact,
    Different,
    Unverified
}

public sealed record ApkPreflightCheck(
    string Id,
    bool Passed,
    string Detail);

public sealed record ApkPreflightNextCommand(
    string Purpose,
    IReadOnlyList<string> Arguments,
    bool Ready);

public sealed record ApkPreflightResult(
    ApkArtifactInspection Artifact,
    ApkArtifactManifestFacts Manifest,
    string Serial,
    QuestDevice? Device,
    int? DeviceApiLevel,
    InstalledApkMatch InstalledMatch,
    InstalledApkIdentity? Installed,
    string? LauncherComponent,
    bool ReadyForDeploy,
    bool ReadyForLaunch,
    bool ReadyForDiagnose,
    IReadOnlyList<ApkPreflightCheck> Checks,
    IReadOnlyList<ApkPreflightNextCommand> NextCommands)
{
    public string PreflightContract { get; init; } =
        "questionable.file_manager.apk_preflight.v1";
}

public sealed record InstalledApkIdentity(
    string Serial,
    ApkArtifactIdentity? Identity,
    IReadOnlyList<string> ApkPaths,
    string BaseApkSha256,
    long BaseApkSizeBytes);

public sealed record InspectedApkInstallResult(
    ApkArtifactInspection Artifact,
    InstalledApkIdentity Installed,
    CommandResult CommandResult);

public sealed record ResolvedAppLaunchResult(
    ApkArtifactInspection Artifact,
    InstalledApkIdentity Installed,
    string Component,
    CommandResult CommandResult,
    bool ComponentObservedResumed)
{
    // These are additive proof facts. Component remains the exact component
    // dispatched to Android; an alias target is never substituted for it.
    public bool LauncherIsActivityAlias { get; init; }

    public string? LauncherTargetActivity { get; init; }
}

public sealed record AppRuntimeObservation(
    ApkArtifactInspection Artifact,
    InstalledApkIdentity? Installed,
    bool IsForeground,
    bool IsTopResumed,
    IReadOnlyList<int> ProcessIds)
{
    public string ObservationContract { get; init; } =
        "questionable.file_manager.app_runtime_observation.v4";

    public IReadOnlyList<string> ForegroundComponents { get; init; } = [];

    public IReadOnlyList<string> TopResumedComponents { get; init; } = [];

    public IReadOnlyList<string> BlockingSystemComponents { get; init; } = [];

    /// <summary>Fixed source for the resumed and top-resumed activity facts.</summary>
    public string ActivityObservationSource { get; init; } =
        "fixed serial-scoped dumpsys activity activities";

    /// <summary>
    /// QFM currently uses only the fixed serial-scoped <c>pidof</c> probe for
    /// this package dimension. A missing PID is an observation limitation, not
    /// an application or OpenXR failure.
    /// </summary>
    public RuntimeProcessObservationQuality ProcessObservationQuality { get; init; } =
        RuntimeProcessObservationQuality.PidofUnavailable;

    public string ProcessObservationSource { get; init; } =
        "fixed serial-scoped pidof derived from inspected package";

    /// <summary>
    /// The latest Android window-manager focus fact. This is deliberately
    /// independent from resumed activity and process readback. It reports only
    /// what the fixed window dump represented; it does not establish whether
    /// the inspected app, a panel handoff, or an OpenXR session is ready.
    /// </summary>
    public AndroidGlobalFocusFact CurrentFocus { get; init; } =
        AndroidGlobalFocusFact.Unknown(
            "fixed serial-scoped dumpsys window windows mCurrentFocus");

    /// <summary>
    /// The latest Android activity-manager focused-app fact. This is a second,
    /// separately parsed source rather than a projection of
    /// <see cref="CurrentFocus"/> or of resumed activity readback.
    /// </summary>
    public AndroidGlobalFocusFact FocusedApp { get; init; } =
        AndroidGlobalFocusFact.Unknown(
            "fixed serial-scoped dumpsys window windows mFocusedApp");

    /// <summary>
    /// Android foreground, activity, and PID facts do not make QFM an
    /// application-runtime authority.
    /// </summary>
    public string ApplicationReadiness { get; init; } = "unknown";

    public bool ApplicationReadinessAuthority { get; init; }

    public string OpenXrReadiness { get; init; } = "unknown";

    public bool OpenXrReadinessAuthority { get; init; }

    public bool ProcessAlive => ProcessIds.Count > 0;
}

public enum RuntimeProcessObservationQuality
{
    PidofReportedProcesses,
    PidofReportedNoProcesses,
    PidofOutputUnusable,
    PidofUnavailable
}

/// <summary>
/// The outcome of parsing one fixed Android global-focus field. The state is
/// retained even when a component is unavailable, so callers do not have to
/// reinterpret missing, malformed, duplicate, or unavailable readback as
/// application state.
/// </summary>
public enum AndroidGlobalFocusObservationState
{
    Observed,
    Absent,
    Malformed,
    Multiple,
    Unknown
}

/// <summary>
/// One raw, bounded Android global-focus fact. A component is present only
/// when exactly one strict same-line component token was read from the fixed
/// field. The source never contains the raw window dump.
/// </summary>
public sealed record AndroidGlobalFocusFact(
    AndroidGlobalFocusObservationState State,
    string? Component,
    string ObservationSource)
{
    /// <summary>
    /// Exit evidence for the fixed source only. It is absent when no command
    /// was attempted, and it never carries raw command output.
    /// </summary>
    public int? SourceExitCode { get; init; }

    public static AndroidGlobalFocusFact Unknown(
        string observationSource,
        int? sourceExitCode = null) =>
        new(AndroidGlobalFocusObservationState.Unknown, null, observationSource)
        {
            SourceExitCode = sourceExitCode
        };
}

/// <summary>
/// Exact-package readback after a fixed current-user force-stop request. These
/// are Android process/activity facts only; they do not establish application,
/// OpenXR, or wearer-visible behavior.
/// </summary>
public sealed record PackageStopQuiescence(
    IReadOnlyList<int> ProcessIds,
    IReadOnlyList<string> ForegroundComponents,
    IReadOnlyList<string> TopResumedComponents)
{
    public bool IsQuiescent =>
        ProcessIds.Count == 0 &&
        ForegroundComponents.Count == 0 &&
        TopResumedComponents.Count == 0;
}

public sealed record PackageStopResult(
    string Serial,
    string PackageName,
    bool PackagePresentBeforeDispatch,
    bool PackagePresentAfterDispatch,
    CommandResult StopCommand,
    PackageStopQuiescence Quiescence);

/// <summary>
/// One forwarding record observed in a shared ADB daemon inventory. It is not
/// a transport, device-health, ownership, reachability, or application-state
/// assertion.
/// </summary>
public sealed record AdbForwardMapping(
    string LocalEndpoint,
    string RemoteEndpoint);

/// <summary>
/// A filtered projection of the process-wide <c>adb forward --list</c>
/// snapshot. The fixed observation is intentionally not serial-scoped at the
/// ADB command layer; <see cref="RequestedSerial"/> filters its output only.
/// </summary>
public sealed record AdbForwardInventoryResult(
    string RequestedSerial,
    IReadOnlyList<AdbForwardMapping> Forwards)
{
    public string ObservationScope { get; init; } =
        "shared-adb-forward-list filtered to requested exact serial";
}

public sealed record InspectedApkDeploymentResult(
    InspectedApkInstallResult Install,
    ResolvedAppLaunchResult Launch,
    AppRuntimeObservation Runtime)
{
    public string DeploymentContract { get; init; } =
        "questionable.file_manager.apk_deployment.v2";

    /// <summary>
    /// Separates QFM's install/launch readback from app-owned runtime truth.
    /// In particular, an empty <c>pidof</c> result does not negate an otherwise
    /// confirmed install/launch effect.
    /// </summary>
    public QfmDeploymentClaimBoundary ClaimBoundary { get; init; } =
        new(
            ExactInstalledBytesConfirmed: false,
            ResolvedComponentObserved: false,
            ProcessObservationQuality: RuntimeProcessObservationQuality.PidofUnavailable,
            IsForeground: false,
            IsTopResumed: false,
            BlockingSystemComponents: [],
            QfmOwnedInstallLaunchEffectConfirmed: false);
}

public sealed record QfmDeploymentClaimBoundary(
    bool ExactInstalledBytesConfirmed,
    bool ResolvedComponentObserved,
    RuntimeProcessObservationQuality ProcessObservationQuality,
    bool IsForeground,
    bool IsTopResumed,
    IReadOnlyList<string> BlockingSystemComponents,
    bool QfmOwnedInstallLaunchEffectConfirmed)
{
    /// <summary>
    /// Raw Android focus evidence is copied into the deployment receipt so it
    /// remains structurally distinct from QFM's install/launch confirmation.
    /// It does not change the QFM-owned effect claim.
    /// </summary>
    public AndroidGlobalFocusFact CurrentFocus { get; init; } =
        AndroidGlobalFocusFact.Unknown(
            "fixed serial-scoped dumpsys window windows mCurrentFocus");

    public AndroidGlobalFocusFact FocusedApp { get; init; } =
        AndroidGlobalFocusFact.Unknown(
            "fixed serial-scoped dumpsys window windows mFocusedApp");

    public string ApplicationReadiness { get; init; } = "unknown";

    public bool ApplicationReadinessAuthority { get; init; }

    public string OpenXrReadiness { get; init; } = "unknown";

    public bool OpenXrReadinessAuthority { get; init; }
}

public sealed record ApkDiagnosticBundleFile(
    string CaptureKind,
    string RelativePath,
    long SizeBytes,
    string Sha256,
    int? CommandExitCode = null)
{
    public bool Succeeded => CommandExitCode is null or 0;

    /// <summary>Fixed capture family; never caller-provided filtering.</summary>
    public string? ObservationSource { get; init; }

    /// <summary>Sanitized semantic description of the fixed command.</summary>
    public string? CommandSemantic { get; init; }

    public bool Truncated { get; init; }
}

public sealed record ApkDiagnosticDeviceFacts(
    string Model,
    string AndroidRelease,
    string ApiLevel,
    string BuildFingerprint);

public sealed record ApkDiagnosticBundleResult(
    ApkArtifactInspection Artifact,
    InstalledApkIdentity Installed,
    AppRuntimeObservation Runtime,
    ApkDiagnosticDeviceFacts Device,
    string OutputDirectory,
    DateTimeOffset CapturedAt,
    IReadOnlyList<ApkDiagnosticBundleFile> Files)
{
    public string DiagnosticContract { get; init; } =
        "questionable.file_manager.apk_diagnostic_bundle.v2";

    public int FailedCaptureCount => Files.Count(static file => !file.Succeeded);
}

public sealed record ApkExportResult(
    string PackageName,
    string SourcePath,
    string OutputPath,
    string ChecksumPath,
    string Sha256,
    long SizeBytes);

public sealed record ApkBundleInstallResult(
    IReadOnlyList<string> ApkPaths,
    CommandResult CommandResult);

public sealed record WifiAdbConnectionResult(
    string Host,
    int Port,
    string Endpoint,
    CommandResult CommandResult,
    QuestDevice Device);

public sealed record WifiAdbEnableResult(
    string UsbSerial,
    string Host,
    int Port,
    string Endpoint,
    string DeviceIdentitySha256,
    CommandResult AddressProbe,
    CommandResult TcpIpCommand,
    WifiAdbConnectionResult Connection);

public sealed record TargetApkInstallResult(
    string Serial,
    CommandResult? CommandResult,
    string? Error)
{
    public bool Succeeded => CommandResult?.Succeeded == true && string.IsNullOrWhiteSpace(Error);

    public string Summary => Succeeded
        ? "Installed successfully."
        : !string.IsNullOrWhiteSpace(Error)
            ? Error
            : CommandResult?.CondensedOutput ?? "Installation did not return a result.";
}

public sealed record ParallelApkInstallResult(
    IReadOnlyList<string> ApkPaths,
    int MaxParallelism,
    IReadOnlyList<TargetApkInstallResult> Targets)
{
    public int SucceededCount => Targets.Count(static target => target.Succeeded);

    public int FailedCount => Targets.Count - SucceededCount;

    public bool Succeeded => Targets.Count > 0 && FailedCount == 0;
}

public sealed record OperatorProgress(
    string Stage,
    string Message,
    int CompletedUnits,
    int TotalUnits)
{
    public bool IsIndeterminate => TotalUnits <= 0;

    public double Percentage => IsIndeterminate
        ? 0
        : Math.Clamp(CompletedUnits * 100d / TotalUnits, 0, 100);
}

public sealed class AdbCommandException : InvalidOperationException
{
    public AdbCommandException(string operation, CommandResult result)
        : base($"{operation} failed: {result.CondensedOutput}")
    {
        Result = result;
    }

    public CommandResult Result { get; }
}

public sealed class SplitPackageException : InvalidOperationException
{
    public SplitPackageException(string packageName, IReadOnlyList<string> apkPaths)
        : base(
            $"{packageName} is installed as {apkPaths.Count} APK parts. " +
            "Single-APK export was refused so the backup cannot be incomplete.")
    {
        PackageName = packageName;
        ApkPaths = new ReadOnlyCollection<string>(apkPaths.ToArray());
    }

    public string PackageName { get; }

    public IReadOnlyList<string> ApkPaths { get; }
}

public sealed class PackageNotInstalledException(string serial, string packageName)
    : InvalidOperationException($"The package is not installed on the selected serial.")
{
    public string Serial { get; } = serial;
    public string PackageName { get; } = packageName;
}

public sealed class PackageStopDispatchException(Exception innerException)
    : InvalidOperationException("The fixed package-stop dispatch did not complete.", innerException);

public sealed class PackageStopReadbackException(Exception innerException)
    : InvalidOperationException("Package-stop readback did not complete.", innerException);

public sealed class FleetTransferLimitException : InvalidOperationException
{
    public FleetTransferLimitException(long maximumBytes)
        : base($"The remote file exceeded the hard transfer limit of {maximumBytes} bytes.")
    {
        MaximumBytes = maximumBytes;
    }

    public long MaximumBytes { get; }
}

public sealed class FleetRemotePathException : InvalidOperationException
{
    public FleetRemotePathException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
