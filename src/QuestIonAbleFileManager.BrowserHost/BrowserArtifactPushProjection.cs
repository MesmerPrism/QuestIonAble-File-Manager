using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserArtifactPushOutcome(BrowserArtifactReceipt Artifact, string Serial, string RemotePath,
    string Verification, bool RemoteSizeVerified);

/// <summary>Only the closed Core push receipt can confirm remote size. No raw process result is public.</summary>
internal sealed class BrowserArtifactPushProjection(BrowserArtifactCommandRequest request, BrowserArtifactLease lease)
{
    public BrowserCommandOutcome Project(OperatorExecutionResult result)
    {
        if (result.Command.Kind != OperatorCommandKind.PushFile || result.Command.Serial != request.Serial ||
            result.Command.LocalPath != lease.Path || result.Command.RemotePath != request.RemotePath ||
            result.CommandResult?.Succeeded != true || !Bound(result.MutationReceipt))
            return Unknown("artifact_push_binding_mismatch");
        var receipt = result.MutationReceipt!;
        if (receipt.Stage == OperatorMutationStage.Confirmed)
        {
            if (!receipt.HeadsetReadback) return Unknown("artifact_push_binding_mismatch");
            return Outcome("completed", true, Sanitize(receipt));
        }
        return receipt.Stage is OperatorMutationStage.Sent or OperatorMutationStage.Pending or OperatorMutationStage.PendingWearerAction
            ? Outcome("pending", false, Sanitize(receipt))
            : Unknown("mutation_readback_unknown", receipt);
    }

    public BrowserCommandOutcome Unknown(string error, OperatorMutationReceipt? receipt = null) =>
        Outcome("outcomeUnknown", false, Bound(receipt) && receipt!.Stage != OperatorMutationStage.Confirmed ? Sanitize(receipt) : null, error);

    private BrowserCommandOutcome Outcome(string state, bool verified, OperatorMutationReceipt? receipt, string? error = null) =>
        new(request.RequestId, request.Action, state, Error: error, Mutation: receipt,
            ArtifactPush: new(lease.Receipt, request.Serial!, request.RemotePath!, "remote-size", verified));

    private bool Bound(OperatorMutationReceipt? receipt) => receipt is not null &&
        receipt.CommandKind == OperatorCommandKind.PushFile && receipt.Target == request.Serial &&
        receipt.DesiredState == "file present at " + request.RemotePath &&
        receipt.OperationId is not null && receipt.OperationId.StartsWith("pc-", StringComparison.Ordinal) &&
        Guid.TryParseExact(receipt.OperationId[3..], "N", out var marker) && marker != Guid.Empty &&
        receipt.OperationId[3..] == marker.ToString("N") && Enum.IsDefined(receipt.Stage) && receipt.ObservedState is not null &&
        receipt.Transitions is not null && receipt.Transitions.All(static transition => transition is not null && transition.Message is not null);

    private OperatorMutationReceipt Sanitize(OperatorMutationReceipt receipt) => receipt with
    {
        ObservedState = Text(receipt.ObservedState),
        Transitions = receipt.Transitions.Select(transition => transition with { Message = Text(transition.Message) }).ToArray()
    };

    private string Text(string value)
    {
        // Native source paths can occur inside diagnostic prose as well as Path fields.
        foreach (var replacement in new[]
        {
            (lease.Path, lease.Receipt.FileName),
            (Path.GetDirectoryName(lease.Path)!, "[private-staging]"),
            (Path.Combine(Path.GetTempPath(), "QuestIonAbleFileManager.ApkAdmission"), "[private-admission]")
        })
        {
            value = value.Replace(replacement.Item1, replacement.Item2, StringComparison.OrdinalIgnoreCase)
                .Replace(replacement.Item1.Replace('\\', '/'), replacement.Item2, StringComparison.OrdinalIgnoreCase)
                .Replace(replacement.Item1.Replace('/', '\\'), replacement.Item2, StringComparison.OrdinalIgnoreCase);
        }
        return value;
    }
}
