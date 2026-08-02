using System.Collections.ObjectModel;

namespace QuestIonAbleFileManager.Core;

public enum OperatorMutationStage
{
    Sent,
    Pending,
    Confirmed,
    Failed,
    TimedOut,
    PendingWearerAction,
    Rejected,
    Expired,
    Cancelled,
    CleanupUnknown
}

public sealed record OperatorMutationTransition(
    OperatorMutationStage Stage,
    DateTimeOffset At,
    string Message);

public sealed record OperatorMutationReceipt(
    string OperationId,
    OperatorCommandKind CommandKind,
    string Target,
    string DesiredState,
    OperatorMutationStage Stage,
    string ObservedState,
    bool HeadsetReadback,
    IReadOnlyList<OperatorMutationTransition> Transitions)
{
    public bool IsTerminal => Stage is
        OperatorMutationStage.Confirmed or
        OperatorMutationStage.Failed or
        OperatorMutationStage.Rejected or
        OperatorMutationStage.Expired or
        OperatorMutationStage.Cancelled or
        OperatorMutationStage.CleanupUnknown;
}

public static class OperatorMutationReconciler
{
    public static OperatorMutationReceipt Reconcile(
        OperatorMutationReceipt receipt,
        OperatorCommand originalCommand,
        OperatorExecutionResult latestReadback)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(originalCommand);
        ArgumentNullException.ThrowIfNull(latestReadback);
        if (receipt.IsTerminal)
        {
            return receipt;
        }

        var observation = OperatorMutations.Observe(originalCommand, latestReadback);
        var sentAt = receipt.Transitions.FirstOrDefault(static transition =>
            transition.Stage == OperatorMutationStage.Sent)?.At;
        if (observation.Stage == OperatorMutationStage.Pending &&
            sentAt is not null &&
            DateTimeOffset.UtcNow - sentAt > TimeSpan.FromMinutes(5))
        {
            observation = observation with
            {
                Stage = OperatorMutationStage.TimedOut,
                Message = "No matching state was observed within five minutes; the operation remains reconcilable on refresh."
            };
        }
        var transitions = receipt.Transitions
            .Append(new OperatorMutationTransition(
                observation.Stage,
                DateTimeOffset.UtcNow,
                observation.Message))
            .ToArray();
        return receipt with
        {
            Stage = observation.Stage,
            ObservedState = observation.ObservedState,
            HeadsetReadback = observation.HeadsetReadback,
            Transitions = new ReadOnlyCollection<OperatorMutationTransition>(transitions)
        };
    }
}

internal sealed class OperatorMutationTracker
{
    private readonly OperatorCommand _command;
    private readonly IProgress<OperatorProgress>? _progress;
    private readonly List<OperatorMutationTransition> _transitions = [];

    public OperatorMutationTracker(OperatorCommand command, IProgress<OperatorProgress>? progress)
    {
        _command = command;
        _progress = progress;
        OperationId = "pc-" + Guid.NewGuid().ToString("N");
    }

    public string OperationId { get; }

    public void Sent()
    {
        var message = $"Sent {OperatorMutations.DesiredState(_command)} to the headset.";
        Add(OperatorMutationStage.Sent, message);
    }

    public void Pending()
    {
        const string message = "Pending headset result and effective-state readback.";
        Add(OperatorMutationStage.Pending, message);
    }

    public OperatorMutationReceipt Complete(OperatorMutationObservation observation)
    {
        Add(observation.Stage, observation.Message);
        return new OperatorMutationReceipt(
            OperationId,
            _command.Kind,
            _command.Serial ?? _command.WifiHost ?? "multiple-headsets",
            OperatorMutations.DesiredState(_command),
            observation.Stage,
            observation.ObservedState,
            observation.HeadsetReadback,
            new ReadOnlyCollection<OperatorMutationTransition>(_transitions.ToArray()));
    }

    public OperatorMutationReceipt Failed(Exception exception)
    {
        var stage = exception is TimeoutException
            ? OperatorMutationStage.TimedOut
            : OperatorMutationStage.Failed;
        var observation = new OperatorMutationObservation(
            stage,
            exception.Message,
            "No matching effective state was confirmed.",
            HeadsetReadback: false);
        return Complete(observation);
    }

    private void Add(OperatorMutationStage stage, string message)
    {
        _transitions.Add(new OperatorMutationTransition(stage, DateTimeOffset.UtcNow, message));
        _progress?.Report(new OperatorProgress(
            "mutation-" + stage.ToString().ToLowerInvariant(),
            $"{stage}: {message}",
            (int)stage,
            (int)OperatorMutationStage.Confirmed));
    }
}

internal sealed record OperatorMutationObservation(
    OperatorMutationStage Stage,
    string Message,
    string ObservedState,
    bool HeadsetReadback)
{
    public static OperatorMutationObservation Confirmed(string observedState) =>
        new(
            OperatorMutationStage.Confirmed,
            "The headset reported the requested effective state.",
            observedState,
            HeadsetReadback: true);

    public static OperatorMutationObservation Pending(string observedState, string message) =>
        new(OperatorMutationStage.Pending, message, observedState, HeadsetReadback: true);
}

internal static class OperatorMutations
{
    public static bool RequiresHeadsetStateChange(OperatorCommand command) => command.Kind switch
    {
        OperatorCommandKind.PushFile or
        OperatorCommandKind.InstallApk or
        OperatorCommandKind.LaunchInspectedApp or
        OperatorCommandKind.InstallApkBundle or
        OperatorCommandKind.EnableWifiAdb or
        OperatorCommandKind.DisconnectWifiAdb or
        OperatorCommandKind.InstallApkMany or
        OperatorCommandKind.InstallApkBundleMany or
        OperatorCommandKind.InstallRustyKiosk or
        OperatorCommandKind.ProvisionRustyKiosk or
        OperatorCommandKind.PushRustyKioskTags or
        OperatorCommandKind.SetQuestKeepAwake or
        OperatorCommandKind.SetQuestPerformance => true,
        OperatorCommandKind.InvokeRustyKiosk => command.RustyKioskCommand is not
            RustyKioskCommand.Status and not
            RustyKioskCommand.CheckSetupHelper,
        _ => false
    };

    public static string DesiredState(OperatorCommand command) => command.Kind switch
    {
        OperatorCommandKind.PushFile => $"file present at {command.RemotePath}",
        OperatorCommandKind.InstallApk => $"inspected APK installed on {command.Serial}: {Path.GetFileName(command.LocalPath)}",
        OperatorCommandKind.LaunchInspectedApp => $"resolved exported launcher started on {command.Serial}",
        OperatorCommandKind.InstallApkBundle => "APK package set installed",
        OperatorCommandKind.EnableWifiAdb => $"Wi-Fi ADB enabled on port {command.WifiPort}",
        OperatorCommandKind.DisconnectWifiAdb => "Wi-Fi ADB endpoint disconnected from this PC",
        OperatorCommandKind.InstallApkMany => "APK installed on every selected headset",
        OperatorCommandKind.InstallApkBundleMany => "APK package set installed on every selected headset",
        OperatorCommandKind.InstallRustyKiosk => "Rusty Kiosk installed and USB authority provisioned",
        OperatorCommandKind.ProvisionRustyKiosk => "Rusty Kiosk USB authority provisioned",
        OperatorCommandKind.PushRustyKioskTags => "tag file hotloaded by Rusty Kiosk",
        OperatorCommandKind.SetQuestKeepAwake => command.Enabled == true
            ? "keep-awake enabled"
            : "normal power policy restored",
        OperatorCommandKind.SetQuestPerformance => command.ClearPerformance
            ? "application-controlled CPU/GPU levels restored"
            : $"CPU/GPU overrides set to {command.CpuLevel?.ToString() ?? "unchanged"}/{command.GpuLevel?.ToString() ?? "unchanged"}",
        OperatorCommandKind.InvokeRustyKiosk => KioskDesiredState(command),
        _ => command.Kind.ToString()
    };

    public static OperatorMutationObservation Observe(
        OperatorCommand command,
        OperatorExecutionResult result)
    {
        return command.Kind switch
        {
            OperatorCommandKind.InstallRustyKiosk => ObserveKioskInstall(result),
            OperatorCommandKind.ProvisionRustyKiosk => ObserveKioskProvision(result),
            OperatorCommandKind.InvokeRustyKiosk => ObserveKioskCommand(command, result),
            OperatorCommandKind.PushRustyKioskTags => ObserveKioskTagHotload(result),
            OperatorCommandKind.SetQuestKeepAwake => ObserveKeepAwake(command, result),
            OperatorCommandKind.SetQuestPerformance => ObservePerformance(command, result),
            OperatorCommandKind.InstallApkMany or OperatorCommandKind.InstallApkBundleMany =>
                result.ParallelApkInstallResult is { Succeeded: true } parallel
                    ? OperatorMutationObservation.Confirmed(
                        $"Package Manager confirmed all {parallel.Targets.Count} target installs; inventories refreshed.")
                    : OperatorMutationObservation.Pending(
                        "At least one target did not confirm installation.",
                        "Waiting for every selected headset to confirm installation."),
            OperatorCommandKind.PushFile => OperatorMutationObservation.Confirmed(
                "Remote file size matches the local source."),
            OperatorCommandKind.InstallApk => ObserveInspectedInstall(command, result),
            OperatorCommandKind.LaunchInspectedApp => ObserveResolvedLaunch(result),
            OperatorCommandKind.InstallApkBundle =>
                OperatorMutationObservation.Confirmed(
                    "Android Package Manager completed the install and the installed-package inventory was read back."),
            OperatorCommandKind.EnableWifiAdb => OperatorMutationObservation.Confirmed(
                $"Ready Wi-Fi ADB endpoint: {result.WifiAdbEnableResult?.Endpoint}"),
            OperatorCommandKind.DisconnectWifiAdb => OperatorMutationObservation.Confirmed(
                "The endpoint is absent from the refreshed ADB device inventory."),
            _ => OperatorMutationObservation.Confirmed("The effective headset state was read back.")
        };
    }

    private static OperatorMutationObservation ObserveResolvedLaunch(OperatorExecutionResult result)
    {
        var launch = result.ResolvedAppLaunchResult ??
            throw new InvalidOperationException("Resolved launch returned no structured result.");
        return launch.ComponentObservedResumed
            ? OperatorMutationObservation.Confirmed(
                $"Exact resolved component {launch.Component} was observed resumed.")
            : OperatorMutationObservation.Pending(
                $"Resolved component {launch.Component} was not observed resumed.",
                "Launch was sent, but exact resumed-activity readback is still pending.");
    }

    private static OperatorMutationObservation ObserveInspectedInstall(
        OperatorCommand command,
        OperatorExecutionResult result)
    {
        var install = result.InspectedApkInstallResult;
        var observation = result.AppRuntimeObservation;
        var artifact = install?.Artifact ?? observation?.Artifact ??
            throw new InvalidOperationException("Inspected APK install returned no artifact-bound readback.");
        var installed = install?.Installed ?? observation?.Installed;
        if (installed is null)
        {
            return OperatorMutationObservation.Pending(
                "The inspected package is not currently installed.",
                "Waiting for exact package/version/signer readback on the selected serial.");
        }
        var exactSerial = string.Equals(command.Serial, installed.Serial, StringComparison.Ordinal);
        var expected = artifact.Identity;
        var actual = installed.Identity;
        var matches = exactSerial && actual is not null &&
            expected.PackageName == actual.PackageName &&
            expected.VersionCode == actual.VersionCode &&
            expected.VersionName == actual.VersionName &&
            expected.SignerSha256 == actual.SignerSha256 &&
            artifact.Sha256 == installed.BaseApkSha256 &&
            artifact.SizeBytes == installed.BaseApkSizeBytes;
        return matches
            ? OperatorMutationObservation.Confirmed(
                $"{actual!.PackageName} versionCode={actual.VersionCode} signer={actual.SignerSha256} " +
                $"on serial {installed.Serial}; installed base sha256={installed.BaseApkSha256} " +
                $"size={installed.BaseApkSizeBytes}; artifact sha256={artifact.Sha256} size={artifact.SizeBytes}.")
            : OperatorMutationObservation.Pending(
                "Installed identity or base APK bytes do not match the inspected artifact and selected serial.",
                "Waiting for exact package/version/signer/base-APK digest and size readback on the selected serial.");
    }

    private static OperatorMutationObservation ObserveKioskInstall(OperatorExecutionResult result)
    {
        var install = result.RustyKioskInstallResult ??
            throw new InvalidOperationException("Rusty Kiosk installation returned no verification result.");
        return install.HelperReady && install.SameSignerControlGranted
            ? OperatorMutationObservation.Confirmed("Both APKs and their same-signer setup authority are ready.")
            : OperatorMutationObservation.Pending(
                "Kiosk installation is incomplete.",
                "Waiting for both APKs and the same-signer authority to read back as ready.");
    }

    private static OperatorMutationObservation ObserveKioskProvision(OperatorExecutionResult result)
    {
        var provision = result.RustyKioskProvisionResult ??
            throw new InvalidOperationException("Rusty Kiosk provisioning returned no verification result.");
        return provision.HelperReady && provision.SameSignerControlGranted
            ? OperatorMutationObservation.Confirmed("The helper and same-signer control permission are ready.")
            : OperatorMutationObservation.Pending(
                "Kiosk provisioning is incomplete.",
                "Waiting for helper authority readback.");
    }

    private static OperatorMutationObservation ObserveKioskTagHotload(OperatorExecutionResult result)
    {
        var kiosk = result.RustyKioskOperatorResult;
        return kiosk is { Accepted: true, Completed: true }
            ? OperatorMutationObservation.Confirmed(
                $"Rusty Kiosk reloaded {kiosk.State.InstalledCount + kiosk.State.NotInstalledCount} tag-list entries.")
            : OperatorMutationObservation.Pending(
                "The file was transferred but Rusty Kiosk has not confirmed reload.",
                "Waiting for Rusty Kiosk hotload readback.");
    }

    private static OperatorMutationObservation ObserveKeepAwake(
        OperatorCommand command,
        OperatorExecutionResult result)
    {
        var status = result.QuestControlStatus ??
            throw new InvalidOperationException("Keep-awake returned no effective-state readback.");
        var requested = command.Enabled == true;
        var matches = requested
            ? status.StayOn &&
              string.Equals(status.Wakefulness, "Awake", StringComparison.OrdinalIgnoreCase) &&
              (string.Equals(status.DisplayState, "ON", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status.DisplayState, "ON_SUSPEND", StringComparison.OrdinalIgnoreCase)) &&
              string.Equals(status.ProximityState.Trim(), "CLOSE", StringComparison.OrdinalIgnoreCase) &&
              status.ProximityHoldDurationMilliseconds == command.DurationMilliseconds &&
              status.ProximityHoldRemainingMilliseconds is > 0
            : !status.StayOn &&
              status.AutoSleepDisabled != true &&
              !string.Equals(status.ProximityState.Trim(), "CLOSE", StringComparison.OrdinalIgnoreCase);
        return matches
            ? OperatorMutationObservation.Confirmed(
                requested
                    ? "Stay-on, wake/display, and the exact bounded proximity hold are effective."
                    : "Normal stay-on and proximity behavior is restored.")
            : OperatorMutationObservation.Pending(
                "One or more independent power-policy readbacks do not match the request.",
                "The requested power policy has not appeared in complete effective-state readback.");
    }

    private static OperatorMutationObservation ObservePerformance(
        OperatorCommand command,
        OperatorExecutionResult result)
    {
        var status = result.QuestControlStatus ??
            throw new InvalidOperationException("Performance change returned no effective-state readback.");
        var matches = command.ClearPerformance
            ? string.IsNullOrWhiteSpace(status.CpuLevel) && string.IsNullOrWhiteSpace(status.GpuLevel)
            : (command.CpuLevel is null || status.CpuLevel == command.CpuLevel.Value.ToString()) &&
              (command.GpuLevel is null || status.GpuLevel == command.GpuLevel.Value.ToString());
        return matches
            ? OperatorMutationObservation.Confirmed(
                $"CPU/GPU readback is {DisplayOverride(status.CpuLevel)}/{DisplayOverride(status.GpuLevel)}.")
            : OperatorMutationObservation.Pending(
                $"CPU/GPU currently reads {DisplayOverride(status.CpuLevel)}/{DisplayOverride(status.GpuLevel)}.",
                "The requested CPU/GPU override has not appeared in effective-state readback.");
    }

    private static OperatorMutationObservation ObserveKioskCommand(
        OperatorCommand command,
        OperatorExecutionResult result)
    {
        var kiosk = result.RustyKioskOperatorResult ??
            throw new InvalidOperationException("Rusty Kiosk returned no structured state.");
        if (!kiosk.Accepted || !kiosk.Completed)
        {
            return OperatorMutationObservation.Pending(kiosk.Message, "Rusty Kiosk has not completed the request.");
        }

        var value = command.RustyKioskValue;
        var state = kiosk.State;
        var confirmed = RustyKioskReadback.Confirms(command, result.Command, kiosk);
        var observed = KioskObservedState(state);
        return confirmed
            ? OperatorMutationObservation.Confirmed(observed)
            : OperatorMutationObservation.Pending(
                observed,
                command.RustyKioskCommand == RustyKioskCommand.RequestWifiAdb
                    ? "Meta's wearer approval is still pending; refresh after accepting or declining the prompt."
                    : "The requested Rusty Kiosk state has not appeared in headset readback.");
    }

    private static string KioskDesiredState(OperatorCommand command) =>
        $"Rusty Kiosk {command.RustyKioskCommand?.ToWireName()}" +
        (string.IsNullOrWhiteSpace(command.RustyKioskValue) ? string.Empty : $" = {command.RustyKioskValue}");

    private static string KioskObservedState(RustyKioskState state) =>
        $"Wi-Fi ADB={(state.WifiAdbEnabled ? "on" : "off")}; " +
        $"Accessibility={(state.AccessibilityEnabled ? "on" : "off")}; " +
        $"guard={(state.GuardArmed ? "armed" : "inactive")}; " +
        $"requirement={state.SelectedLaunchRequirement?.ToWireName() ?? "unknown"}; " +
        $"pending-launch={state.PendingRequirementLaunch?.ToString().ToLowerInvariant() ?? "unknown"}; " +
        $"passthrough={state.PassthroughStyle?.ToWireName() ?? "unknown"}; " +
        $"last-dispatch={state.LastDispatchedOptionId ?? "none"}@{state.LastDispatchedOptionPackage ?? "none"}; " +
        $"selected={state.SelectedKey ?? "none"}.";

    private static string DisplayOverride(string value) =>
        string.IsNullOrWhiteSpace(value) ? "app" : value;
}

public static class RustyKioskReadback
{
    public static bool Confirms(
        RustyKioskCommand command,
        string? value,
        RustyKioskOperatorResult result)
        => Confirms(command, value, result, allowStatusSnapshot: false);

    public static bool Confirms(
        OperatorCommand originalCommand,
        OperatorCommand readbackCommand,
        RustyKioskOperatorResult result)
    {
        ArgumentNullException.ThrowIfNull(originalCommand);
        ArgumentNullException.ThrowIfNull(readbackCommand);
        var command = originalCommand.RustyKioskCommand ??
            throw new InvalidOperationException("The original operation is missing its typed Rusty Kiosk command.");
        return Confirms(
            command,
            originalCommand.RustyKioskValue,
            result,
            IsMatchingStatusSnapshot(originalCommand, readbackCommand, result));
    }

    private static bool Confirms(
        RustyKioskCommand command,
        string? value,
        RustyKioskOperatorResult result,
        bool allowStatusSnapshot)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted ||
            !result.Completed ||
            (result.Command != command && !allowStatusSnapshot))
        {
            return false;
        }

        var state = result.State;
        return command switch
        {
            RustyKioskCommand.ShowControls => state.ControlsOpen == true,
            RustyKioskCommand.ShowApps => state.ControlsOpen == false,
            RustyKioskCommand.FocusSearch or RustyKioskCommand.FocusTagEditor => false,
            RustyKioskCommand.RequestWifiAdb => state.WifiAdbEnabled,
            RustyKioskCommand.EnableWifiAfterBoot => state.RequestWifiAdbAfterBoot,
            RustyKioskCommand.DisableWifiAfterBoot => !state.RequestWifiAdbAfterBoot,
            RustyKioskCommand.DisableWifiAdb => !state.WifiAdbEnabled,
            RustyKioskCommand.EnableAccessibility => state.AccessibilityEnabled,
            RustyKioskCommand.DisableAccessibility => !state.AccessibilityEnabled,
            RustyKioskCommand.LaunchKiosk => state.GuardArmed,
            RustyKioskCommand.LaunchNormal => !state.GuardArmed,
            RustyKioskCommand.LaunchOption =>
                !string.IsNullOrWhiteSpace(value) &&
                string.Equals(state.LastDispatchedOptionId, value, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(state.SelectedPackage) &&
                string.Equals(
                    state.LastDispatchedOptionPackage,
                    state.SelectedPackage,
                    StringComparison.Ordinal),
            RustyKioskCommand.SetSearch => string.Equals(state.Search, value ?? string.Empty, StringComparison.Ordinal),
            RustyKioskCommand.FilterTag => string.Equals(state.TagFilter ?? string.Empty, value ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            RustyKioskCommand.Select => string.Equals(state.SelectedKey, value, StringComparison.Ordinal),
            RustyKioskCommand.AddTag => state.Entries.Any(entry =>
                string.Equals(entry.Key, state.SelectedKey, StringComparison.Ordinal) &&
                entry.Tags.Contains(value ?? string.Empty, StringComparer.OrdinalIgnoreCase)),
            RustyKioskCommand.RemoveTag => state.Entries.Any(entry =>
                string.Equals(entry.Key, state.SelectedKey, StringComparison.Ordinal) &&
                !entry.Tags.Contains(value ?? string.Empty, StringComparer.OrdinalIgnoreCase)),
            RustyKioskCommand.SetLaunchRequirement => value is not null &&
                state.SelectedLaunchRequirement == RustyKioskCommands.ParseLaunchRequirement(value) &&
                state.Entries.Any(entry =>
                    string.Equals(entry.Key, state.SelectedKey, StringComparison.Ordinal) &&
                    entry.LaunchRequirement == state.SelectedLaunchRequirement),
            RustyKioskCommand.CancelPendingLaunch => state.PendingRequirementLaunch == false,
            RustyKioskCommand.PassthroughNatural =>
                state.SystemPassthroughEnabled == true &&
                state.PassthroughStyle == RustyKioskPassthroughStyle.Natural,
            RustyKioskCommand.PassthroughContour =>
                state.SystemPassthroughEnabled == true &&
                state.PassthroughStyle == RustyKioskPassthroughStyle.ContourLut &&
                state.PassthroughLutApplied == true,
            RustyKioskCommand.Reload or RustyKioskCommand.ExitMetaHome => true,
            _ => true
        };
    }

    private static bool IsMatchingStatusSnapshot(
        OperatorCommand originalCommand,
        OperatorCommand readbackCommand,
        RustyKioskOperatorResult result)
    {
        if (result.Command != RustyKioskCommand.Status ||
            readbackCommand.Kind != OperatorCommandKind.InspectRustyKiosk ||
            readbackCommand.RustyKioskCommand != RustyKioskCommand.Status ||
            string.IsNullOrWhiteSpace(originalCommand.Serial) ||
            !string.Equals(originalCommand.Serial, readbackCommand.Serial, StringComparison.Ordinal) ||
            originalCommand.RustyKioskProduct is null ||
            readbackCommand.RustyKioskProduct is null)
        {
            return false;
        }

        try
        {
            return RustyKioskProductContract.RequireKnown(originalCommand.RustyKioskProduct) ==
                RustyKioskProductContract.RequireKnown(readbackCommand.RustyKioskProduct);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public static class RustyKioskCliExitCodes
{
    public static int For(OperatorMutationReceipt? receipt, bool accepted) => receipt?.Stage switch
    {
        OperatorMutationStage.Confirmed or OperatorMutationStage.Cancelled => accepted ? 0 : 1,
        OperatorMutationStage.Pending or
        OperatorMutationStage.PendingWearerAction or
        OperatorMutationStage.TimedOut => 3,
        OperatorMutationStage.Rejected or OperatorMutationStage.Expired => 2,
        null => accepted ? 0 : 1,
        _ => 1
    };
}
