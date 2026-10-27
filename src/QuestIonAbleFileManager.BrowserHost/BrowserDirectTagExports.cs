using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectTagsCoreReceipt(string ActionId, KioskDirectOperatorAction Action, OperatorMutationStage Stage);
public sealed record BrowserDirectTagsOutcome(string ConnectionId, string ConnectRequestId, string UsbOperationId,
    string Serial, string ProductChannel, string Schema, long SizeBytes, string Sha256,
    BrowserArtifactReceipt Artifact, BrowserDirectTagsCoreReceipt CoreReceipt);

public sealed partial class BrowserDirectConnections
{
    private async Task<BrowserCommandOutcome> ExecuteTagExportAsync(Operation operation,
        Func<KioskDirectTagExportLease, CancellationToken, Task<BrowserArtifactReceipt>>? stage,
        CancellationToken cancellation)
    {
        var entry = operation.Entry;
        try
        {
            if (stage is null) throw new InvalidOperationException("direct_tags_staging_unavailable");
            // Reuse the adopted owner. This signed read checks its original
            // session/generation without Adopt, Invoke, or staging inventory.
            var statusCommand = KioskDirectOperatorCommand.Status();
            var status = await entry.Session!.ExecuteAsync(statusCommand, cancellation).ConfigureAwait(false);
            BrowserDirectProjection.Result(status, statusCommand, entry.Receipt!, entry.RemoteSession, entry.RemoteGeneration);
            RequireTagExportLive(entry, cancellation);

            var command = KioskDirectOperatorCommand.ExportTags();
            var result = await entry.Session.ExecuteAsync(command, cancellation).ConfigureAwait(false);
            using var lease = result.TagExport;
            if (result.Command != command || result.Mutation.ActionId != command.ActionId ||
                result.Mutation.Action != KioskDirectOperatorAction.ExportTags || result.Mutation.RequestId is not null ||
                result.Mutation.Stage != OperatorMutationStage.Confirmed || lease is null ||
                result.Status is not null || result.KioskResult is not null || result.RequestReceipt is not null ||
                result.StagedFiles is not null || result.StagedFile is not null || result.LocalFileName is not null || result.InstallReceipt is not null ||
                lease.Schema is not (RustyKioskContract.TagFileSchema or RustyKioskContract.TagFileSuccessorSchema) ||
                lease.SizeBytes is < 1 or > RustyKioskContract.MaxTagFileBytes || !IsTagHash(lease.Sha256) ||
                !lease.Stream.CanRead || lease.Stream.CanWrite || !lease.Stream.CanSeek ||
                lease.Stream.Position != 0 || lease.Stream.Length != lease.SizeBytes)
                throw new InvalidDataException("direct_tags_export_binding_invalid");
            RequireTagExportLive(entry, cancellation);

            // The shared operation remains active, and the Core lease remains
            // alive, until the existing protected store acknowledges exact bytes.
            var artifact = await stage(lease, cancellation).ConfigureAwait(false);
            if (!Guid.TryParseExact(artifact.Id, "N", out var id) || id == Guid.Empty || id.ToString("N") != artifact.Id ||
                artifact.FileName != "app-tags.json" || artifact.SizeBytes != lease.SizeBytes || artifact.Sha256 != lease.Sha256)
                throw new InvalidDataException("direct_tags_artifact_binding_invalid");
            RequireTagExportLive(entry, cancellation);
            return new(operation.Request.RequestId, operation.Request.Action, "completed")
            {
                KioskDirectTags = new(entry.Id, entry.Context.RequestId, entry.Context.UsbOperationId,
                    entry.Context.Serial, entry.Context.ProductChannel, lease.Schema, lease.SizeBytes, lease.Sha256,
                    artifact, new(command.ActionId, command.Action, result.Mutation.Stage))
            };
        }
        catch (OperationCanceledException)
        {
            return new(operation.Request.RequestId, operation.Request.Action, "failed", Error: "direct_tags_export_cancelled");
        }
        catch
        {
            // A failed/lost read or staging acknowledgement may leave bounded
            // transient bytes. The existing store retains their cleanup custody.
            return new(operation.Request.RequestId, operation.Request.Action, "failed", Error: "direct_tags_export_failed");
        }
    }

    private void RequireTagExportLive(Entry entry, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_sync)
        {
            Expire(entry);
            if (_stopping || !ReferenceEquals(_entry, entry) || !entry.Published || entry.State != "connected" || entry.Revoked || entry.CloseAttempted)
                throw new InvalidDataException("direct_tags_connection_unavailable");
        }
    }

    private static bool IsTagHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
