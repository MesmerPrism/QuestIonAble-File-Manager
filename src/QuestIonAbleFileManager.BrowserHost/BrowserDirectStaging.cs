using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectStagingFile(string Name, long Bytes, long ModifiedAtMs);
public sealed record BrowserDirectStagingCoreReceipt(string ActionId, KioskDirectOperatorAction Action, OperatorMutationStage Stage);
public sealed record BrowserDirectStagingOutcome(string ConnectionId, string ConnectRequestId, string UsbOperationId,
    string Serial, string ProductChannel, int TotalCount, int ReturnedCount, int OmittedCount, bool Complete,
    IReadOnlyList<BrowserDirectStagingFile> Files, BrowserDirectStagingCoreReceipt CoreReceipt);

public sealed partial class BrowserDirectConnections
{
    private const int MaximumStagingRows = 128;
    private const long MaximumStagingInteger = 9007199254740991;

    private async Task<BrowserCommandOutcome> ExecuteStagingListAsync(Operation operation, CancellationToken cancellation)
    {
        var entry = operation.Entry;
        try
        {
            // The same adopted owner supplies this signed session/generation
            // read. It does not repeat Adopt, Invoke, or connection bootstrap.
            var statusCommand = KioskDirectOperatorCommand.Status();
            var status = await entry.Session!.ExecuteAsync(statusCommand, cancellation).ConfigureAwait(false);
            BrowserDirectProjection.Result(status, statusCommand, entry.Receipt!, entry.RemoteSession, entry.RemoteGeneration);
            RequireStagingListLive(entry, cancellation);
            var command = KioskDirectOperatorCommand.ListStaging();
            var result = await entry.Session.ExecuteAsync(command, cancellation).ConfigureAwait(false);
            using var unexpectedTags = result.TagExport;
            if (result.Command != command || result.Mutation.ActionId != command.ActionId ||
                result.Mutation.Action != KioskDirectOperatorAction.ListStaging || result.Mutation.Stage != OperatorMutationStage.Confirmed ||
                result.Mutation.RequestId is not null || result.StagedFiles is not { } files ||
                result.Status is not null || result.KioskResult is not null || result.RequestReceipt is not null ||
                result.StagedFile is not null || result.LocalFileName is not null || result.InstallReceipt is not null || unexpectedTags is not null)
                throw new InvalidDataException("direct_staging_binding_invalid");

            // Validate the whole bounded Core response before showing a prefix.
            // Names are literal metadata, never transfer/install authority.
            var names = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<BrowserDirectStagingFile>(Math.Min(files.Count, MaximumStagingRows));
            foreach (var file in files)
            {
                if (file is null || !ValidStagingName(file.Name) || !names.Add(file.Name) ||
                    file.Bytes is < 0 or > MaximumStagingInteger || file.ModifiedAtMs is < 0 or > MaximumStagingInteger || file.Sha256 is not null)
                    throw new InvalidDataException("direct_staging_metadata_invalid");
                if (rows.Count < MaximumStagingRows) rows.Add(new(file.Name, file.Bytes, file.ModifiedAtMs));
            }
            RequireStagingListLive(entry, cancellation);
            var omitted = files.Count - rows.Count;
            return new(operation.Request.RequestId, operation.Request.Action, "completed")
            {
                KioskDirectStaging = new(entry.Id, entry.Context.RequestId, entry.Context.UsbOperationId,
                    entry.Context.Serial, entry.Context.ProductChannel, files.Count, rows.Count, omitted, omitted == 0,
                    rows.AsReadOnly(), new(command.ActionId, command.Action, result.Mutation.Stage))
            };
        }
        catch (OperationCanceledException)
        {
            return new(operation.Request.RequestId, operation.Request.Action, "failed", Error: "direct_staging_list_cancelled");
        }
        catch
        {
            return new(operation.Request.RequestId, operation.Request.Action, "failed", Error: "direct_staging_list_failed");
        }
    }

    private void RequireStagingListLive(Entry entry, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_sync)
        {
            Expire(entry);
            if (_stopping || !ReferenceEquals(_entry, entry) || !entry.Published || entry.State != "connected" || entry.Revoked || entry.CloseAttempted)
                throw new InvalidDataException("direct_staging_connection_unavailable");
        }
    }

    private static bool ValidStagingName(string? name) => name is { Length: >= 1 and <= 160 } && char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ' ' or '(' or ')' or '+' or '@' or '-');
}
