using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

/// <summary>Bounded launch evidence; application readiness and wearer visibility remain unknown.</summary>
public sealed record LocalApiV3LaunchSummary(string DiagnosticContract, DateTimeOffset CapturedAt,
    string Disposition, bool LaunchMayHaveDispatched, bool DispatchAttempted,
    bool PostActionWindowElapsed, bool OutputLimitReached, bool CaptureExitedEarly,
    bool ProcessTreeCleanupSucceeded, int CaptureExitCode, long CaptureSizeBytes, int CurrentPackageProcessCount)
{
    public string ApplicationReadiness => "unknown";
    public bool ApplicationReadinessAuthority => false;
    public string OpenXrReadiness => "unknown";
    public bool OpenXrReadinessAuthority => false;
    public string WearerVisibility => "unknown";
    public bool WearerVisibilityAuthority => false;

    public void Validate()
    {
        if (DiagnosticContract != "questionable.file_manager.apk_launch_diagnostic_bundle.v1" || CapturedAt == default ||
            Disposition is not ("completed" or "launchPending" or "rejectedBeforeDispatch" or "outcomeUnknown") ||
            !LaunchMayHaveDispatched || CaptureSizeBytes is < 0 or > LocalApiDiagnosticOutputIntent.MaximumArmedCaptureBytes ||
            CurrentPackageProcessCount is < 0 or > LocalApiDiagnosticOutputIntent.MaximumRequiredStandardOutputBytes ||
            (Disposition is "completed" or "launchPending" && !DispatchAttempted) ||
            (Disposition == "rejectedBeforeDispatch" && DispatchAttempted) ||
            (Disposition != "outcomeUnknown" && (!PostActionWindowElapsed || OutputLimitReached || CaptureExitedEarly ||
                !ProcessTreeCleanupSucceeded)) || (Disposition == "completed" && CurrentPackageProcessCount == 0) ||
            JsonSerializer.SerializeToUtf8Bytes(this, JsonSerializerOptions.Web).Length > 4096)
            throw LocalApiV3Contract.Failure("diagnostic_launch_proof_invalid");
    }
}

// Projects the existing Core classification and actual tracker receipt. This
// neither classifies raw process output nor derives dispatch from owner intent.
internal static class LocalApiV3LaunchProof
{
    internal static (LocalApiV3LaunchSummary Summary, OperatorMutationReceipt Mutation, LocalApiOperationStage Stage)
        Project(OperatorExecutionResult result, string serial, bool marker)
    {
        var launch = result.ApkLaunchDiagnosticBundleResult;
        var receipt = result.MutationReceipt;
        if (!marker || result.Command.Kind != OperatorCommandKind.LaunchDiagnoseInspectedApp || result.Command.Serial != serial ||
            result.ApkDiagnosticBundleResult is not null || launch is null || receipt is null ||
            launch.Attempt is null || launch.Capture is null || launch.Attempt.CurrentPackageProcessIds is null ||
            !Enum.IsDefined(launch.Disposition)) throw Invalid();
        // The safe projection must retain the actual input/target identity joins,
        // while keeping all paths, UID/PIDs, fences and command output private.
        LocalApiV3ApkFacts.FromArtifact(launch.Artifact).Validate();
        RequireInstalled(launch.Artifact, launch.InstalledBeforeDispatch, serial);
        if (launch.InstalledAfterCapture is { } after)
        {
            if (after.Serial != serial) throw Invalid();
            // Core deliberately retains partial/drifted post-readback facts as
            // unknown evidence when its actual readback failed. The sealer
            // validates that same failure/disposition join before publication.
            if (launch.OwnedPostReadbackFailure is null) RequireInstalled(launch.Artifact, after, serial);
        }
        if (launch.Capture.RelativePath != "logcat-uid-post-fence.txt" ||
            launch.ManifestRelativePath != "launch-diagnostic-manifest.json" ||
            launch.ManifestSizeBytes is < 1 or > LocalApiDiagnosticOutputIntent.MaximumPayloadFileBytes ||
            !LocalApiV3Contract.IsHash(launch.ManifestSha256) || !LocalApiV3Contract.IsHash(launch.Capture.Sha256)) throw Invalid();
        var summary = new LocalApiV3LaunchSummary(launch.DiagnosticContract, launch.HostFenceCreatedAt,
            launch.Disposition switch
            {
                ApkLaunchDiagnosticDisposition.Completed => "completed",
                ApkLaunchDiagnosticDisposition.LaunchPending => "launchPending",
                ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch => "rejectedBeforeDispatch",
                ApkLaunchDiagnosticDisposition.OutcomeUnknown => "outcomeUnknown",
                _ => throw Invalid()
            }, marker, launch.Attempt.DispatchAttempted, launch.Capture.PostActionWindowElapsed,
            launch.Capture.OutputLimitReached, launch.Capture.CaptureExitedEarly, launch.Capture.ProcessTreeCleanupSucceeded,
            launch.Capture.CaptureExitCode, launch.Capture.SizeBytes, launch.Attempt.CurrentPackageProcessIds.Count);
        RequireReceiptStructure(receipt, serial, summary.DispatchAttempted);
        var safe = receipt with
        {
            DesiredState = "launch-diagnose",
            ObservedState = Message(receipt.Stage),
            Transitions = Array.AsReadOnly(receipt.Transitions.Select(t => t with { Message = Message(t.Stage) }).ToArray())
        };
        var stage = ExpectedStage(summary, safe);
        Validate(summary, safe, serial, marker, stage);
        return (summary, safe, stage);
    }

    internal static void Validate(LocalApiV3LaunchSummary summary, OperatorMutationReceipt mutation,
        string serial, bool marker, LocalApiOperationStage stage)
    {
        if (summary is null || mutation is null || !marker || summary.LaunchMayHaveDispatched != marker) throw Invalid();
        summary.Validate();
        RequireReceiptStructure(mutation, serial, summary.DispatchAttempted);
        if (mutation.DesiredState != "launch-diagnose" || mutation.ObservedState != Message(mutation.Stage) ||
            mutation.Transitions.Any(t => t.Message != Message(t.Stage)) ||
            JsonSerializer.SerializeToUtf8Bytes(mutation, JsonSerializerOptions.Web).Length > 4096 ||
            (stage != ExpectedStage(summary, mutation) && stage != LocalApiOperationStage.OutcomeUnknownRecoveryRequired)) throw Invalid();
    }

    private static LocalApiOperationStage ExpectedStage(LocalApiV3LaunchSummary summary, OperatorMutationReceipt mutation)
    {
        // OutcomeUnknown can carry the tracker's actual Rejected receipt when
        // dispatch never happened but capture settlement remains uncertain.
        return (summary.Disposition, summary.DispatchAttempted, mutation.Stage, mutation.HeadsetReadback) switch
        {
            ("completed", true, OperatorMutationStage.Confirmed, true) => LocalApiOperationStage.Completed,
            ("rejectedBeforeDispatch", false, OperatorMutationStage.Rejected, true) => LocalApiOperationStage.Failed,
            ("launchPending" or "outcomeUnknown", true, OperatorMutationStage.Pending, true) => LocalApiOperationStage.OutcomeUnknownRecoveryRequired,
            ("outcomeUnknown", false, OperatorMutationStage.Rejected, true) => LocalApiOperationStage.OutcomeUnknownRecoveryRequired,
            _ => throw Invalid()
        };
    }

    private static void RequireReceiptStructure(OperatorMutationReceipt receipt, string serial, bool attempted)
    {
        if (receipt.OperationId is null || !Regex.IsMatch(receipt.OperationId, "\\Apc-[0-9a-f]{32}\\z", RegexOptions.CultureInvariant) ||
            receipt.OperationId[3..].All(c => c == '0') || receipt.CommandKind != OperatorCommandKind.LaunchDiagnoseInspectedApp ||
            receipt.Target != serial || receipt.Transitions is null || receipt.Transitions.Count is < 1 or > 3 ||
            receipt.Transitions.Any(t => t is null || t.At == default) || receipt.Transitions[^1].Stage != receipt.Stage) throw Invalid();
        try { _ = AndroidInput.RequireSerial(serial); }
        catch (ArgumentException) { throw Invalid(); }
        if (serial.Any(char.IsControl)) throw Invalid();
        for (var i = 1; i < receipt.Transitions.Count; i++)
            if (receipt.Transitions[i].At < receipt.Transitions[i - 1].At) throw Invalid();
        var stages = receipt.Transitions.Select(t => t.Stage).ToArray();
        if (attempted)
        {
            if (stages.Length is < 2 or > 3 || stages[0] != OperatorMutationStage.Sent || stages[1] != OperatorMutationStage.Pending ||
                (stages.Length == 3 && stages[2] is not (OperatorMutationStage.Pending or OperatorMutationStage.Confirmed))) throw Invalid();
        }
        else if (stages.Length != 1 || stages[0] != OperatorMutationStage.Rejected) throw Invalid();
    }

    private static void RequireInstalled(ApkArtifactInspection artifact, InstalledApkIdentity installed, string serial)
    {
        if (installed is null || installed.Serial != serial || installed.Identity != artifact.Identity || installed.ApkPaths is null ||
            installed.ApkPaths.Count != 1 || installed.BaseApkSizeBytes != artifact.SizeBytes || installed.BaseApkSha256 != artifact.Sha256)
            throw Invalid();
    }

    private static string Message(OperatorMutationStage stage) => stage switch
    {
        OperatorMutationStage.Sent => "launch-sent",
        OperatorMutationStage.Pending => "launch-pending",
        OperatorMutationStage.Confirmed => "launch-confirmed",
        OperatorMutationStage.Rejected => "launch-rejected",
        _ => throw Invalid()
    };

    private static LocalApiException Invalid() => LocalApiV3Contract.Failure("diagnostic_launch_proof_invalid");
}
