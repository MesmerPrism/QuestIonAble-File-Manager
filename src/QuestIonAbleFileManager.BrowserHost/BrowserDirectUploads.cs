using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectUploadCoreReceipt(string ActionId, KioskDirectOperatorAction Action, OperatorMutationStage Stage);
public sealed record BrowserDirectUploadOutcome(string ConnectionId, string ConnectRequestId, string UsbOperationId,
    string Serial, string ProductChannel, string ArtifactId, string Sha256, long SizeBytes, string StagedName,
    string Disposition, string Verification, BrowserDirectUploadCoreReceipt? CoreReceipt);

public sealed partial class BrowserDirectConnections
{
    private async Task<BrowserCommandOutcome> ExecuteUploadAsync(Operation operation,
        Func<CancellationToken, Task<BrowserArtifactLease>>? acquire, CancellationToken cancellation)
    {
        var entry=operation.Entry; var request=operation.Request; var invoked=false;
        BrowserCommandOutcome Outcome(string state,string disposition,string verification,BrowserDirectUploadCoreReceipt? receipt=null,string? error=null) =>
            new(request.RequestId,request.Action,state,Error:error)
            {
                KioskDirectUpload=new(entry.Id,entry.Context.RequestId,entry.Context.UsbOperationId,entry.Context.Serial,entry.Context.ProductChannel,
                    request.ArtifactId!,request.ArtifactSha256!,request.ArtifactSizeBytes!.Value,request.StagedName!,disposition,verification,receipt)
            };
        try
        {
            var statusCommand=KioskDirectOperatorCommand.Status();
            var status=await entry.Session!.ExecuteAsync(statusCommand,cancellation).ConfigureAwait(false);
            BrowserDirectProjection.Result(status,statusCommand,entry.Receipt!,entry.RemoteSession,entry.RemoteGeneration);
            RequireUploadLive(entry,cancellation);
            if(acquire is null)throw new InvalidOperationException("direct_upload_source_unavailable");
            var lease=await acquire(cancellation).ConfigureAwait(false);
            operation.UploadSourceLease=lease; // released by Complete, after terminal cache publication
            if(lease is null || lease.Receipt.Id!=request.ArtifactId || lease.Receipt.Sha256!=request.ArtifactSha256 ||
                lease.Receipt.SizeBytes!=request.ArtifactSizeBytes || !lease.Stream.CanRead || lease.Stream.CanWrite ||
                !lease.Stream.CanSeek || lease.Stream.Position!=0 || lease.Stream.Length!=request.ArtifactSizeBytes)
                throw new InvalidDataException("direct_upload_source_binding_invalid");
            var command=KioskDirectOperatorCommand.Upload(lease.Path,request.StagedName,operatorConfirmed:true);
            if(command.LocalPath!=lease.Path || command.StagedName!=request.StagedName)
                throw new InvalidDataException("direct_upload_source_path_invalid");
            RequireUploadLive(entry,cancellation);
            invoked=true; // From this exact boundary, no later failure proves no upload effect.
            var result=await entry.Session.ExecuteAsync(command,cancellation).ConfigureAwait(false);
            using var unexpectedTags=result.TagExport;
            if(result.Command!=command || result.Mutation.ActionId!=command.ActionId || result.Mutation.Action!=KioskDirectOperatorAction.UploadStaging ||
                result.Mutation.Stage!=OperatorMutationStage.Confirmed || result.Mutation.RequestId is not null ||
                result.StagedFile is not { } file || file.Name!=request.StagedName || file.Bytes!=request.ArtifactSizeBytes || file.Sha256!=request.ArtifactSha256 ||
                result.Status is not null || result.KioskResult is not null || result.RequestReceipt is not null || result.StagedFiles is not null ||
                result.LocalFileName is not null || result.InstallReceipt is not null || unexpectedTags is not null)
                throw new InvalidDataException("direct_upload_result_binding_invalid");
            RequireUploadLive(entry,cancellation);
            return Outcome("completed","confirmed","signed-upload-size-sha256",new(command.ActionId,command.Action,result.Mutation.Stage));
        }
        catch
        {
            return invoked ? Outcome("outcomeUnknown","outcomeUnknown","none",error:"direct_upload_unknown") :
                Outcome("failed","notDispatched","none",error:"direct_upload_not_dispatched");
        }
    }

    private void RequireUploadLive(Entry entry,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock(_sync)
        {
            Expire(entry);
            if(_stopping || !ReferenceEquals(_entry,entry) || !entry.Published || entry.State!="connected" || entry.Revoked || entry.CloseAttempted)
                throw new InvalidDataException("direct_upload_connection_unavailable");
        }
    }
}
