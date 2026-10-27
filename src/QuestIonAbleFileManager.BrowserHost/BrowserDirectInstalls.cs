using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectInstallCoreReceipt(string ActionId,KioskDirectOperatorAction Action,string RequestId,OperatorMutationStage Stage);
public sealed record BrowserDirectInstallReceipt(string RequestId,string State,bool Completed,int? SessionId,string? PackageName);
public sealed record BrowserDirectInstallOutcome(string ConnectionId,string ConnectRequestId,string UsbOperationId,string Serial,string ProductChannel,
    string Operation,string OriginalRequestId,string InstallRequestId,string InspectionRequestId,string ArtifactId,string Sha256,long SizeBytes,
    string PackageName,long VersionCode,string SignerSha256,string OriginalEffect="unknown",bool ReconcilesOriginal=false,
    BrowserDirectInstallCoreReceipt? CoreReceipt=null,BrowserDirectInstallReceipt? Receipt=null);
internal sealed record BrowserDirectInstallSource(string InspectionRequestId,string ArtifactId,string Sha256,long SizeBytes,string PackageName,long VersionCode,string SignerSha256);

public sealed partial class BrowserCommands
{
    private BrowserDirectInstallSource ReadInstallInspection(BrowserDirectRequest request)
    {
        if(!_requests.TryGetValue(request.InspectionRequestId!,out var prior) || prior.RequestId!=request.InspectionRequestId || prior.Action!="apk.inspect" ||
            prior.State!="completed" || prior.ArtifactInspection is not { } inspected)
            throw new BrowserRequestException("direct_install_inspection_unavailable",409);
        var a=inspected.Artifact;var facts=inspected.Inspection;var identity=facts?.Identity;
        if(a is null || facts is null || identity is null || a.Id!=request.ArtifactId || a.Sha256!=request.ArtifactSha256 || a.SizeBytes!=request.ArtifactSizeBytes ||
            facts.Sha256!=a.Sha256 || facts.SizeBytes!=a.SizeBytes || !a.FileName.EndsWith(".apk",StringComparison.OrdinalIgnoreCase) ||
            identity.SplitName is not null || identity.VersionCode is < 0 or > 9007199254740991 ||
            !BrowserDirectInstallValidation.Package(identity.PackageName) || !BrowserDirectInstallValidation.Hash(identity.SignerSha256))
            throw new BrowserRequestException("direct_install_inspection_invalid",409);
        try{BrowserArtifactStore.ValidateFileName(a.FileName);}
        catch(BrowserArtifactException){throw new BrowserRequestException("direct_install_inspection_invalid",409);}
        return new(request.InspectionRequestId!,a.Id,a.Sha256,a.SizeBytes,identity.PackageName,identity.VersionCode,identity.SignerSha256);
    }
}

internal static class BrowserDirectInstallValidation
{
    internal static bool Hash(string? value)=>value is {Length:64} && value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool Package(string? value)
    {
        if(value is not {Length:>=1 and <=256} || value.Any(char.IsControl))return false;
        try{return AndroidInput.RequirePackageName(value)==value;}catch(ArgumentException){return false;}
    }
}

public sealed partial class BrowserDirectConnections
{
    private static BrowserDirectInstallOutcome InstallMetadata(BrowserDirectRequest request,Entry entry,BrowserDirectInstallSource? inspection,BrowserDirectInstallOutcome? original)
    {
        if(request.Action=="kiosk.direct.install")
        {
            if(inspection is null)throw new BrowserRequestException("direct_install_inspection_unavailable",409);
            return new(entry.Id,entry.Context.RequestId,entry.Context.UsbOperationId,entry.Context.Serial,entry.Context.ProductChannel,"install",request.RequestId,
                "install_"+Guid.Parse(request.RequestId).ToString("N"),inspection.InspectionRequestId,inspection.ArtifactId,inspection.Sha256,inspection.SizeBytes,
                inspection.PackageName,inspection.VersionCode,inspection.SignerSha256);
        }
        if(original is null || original.Operation!="install" || original.OriginalRequestId!=request.OriginalRequestId || original.OriginalEffect=="notDispatched" ||
            original.InstallRequestId!="install_"+Guid.Parse(request.OriginalRequestId!).ToString("N") || original.ConnectionId!=entry.Id ||
            original.ConnectRequestId!=entry.Context.RequestId || original.UsbOperationId!=entry.Context.UsbOperationId || original.Serial!=entry.Context.Serial || original.ProductChannel!=entry.Context.ProductChannel)
            throw new BrowserRequestException("direct_install_original_mismatch",409);
        return original with{Operation="installStatus",OriginalEffect="unknown",ReconcilesOriginal=false,CoreReceipt=null,Receipt=null};
    }

    private async Task<BrowserCommandOutcome> ExecuteInstallAsync(Operation operation,Func<CancellationToken,Task<BrowserArtifactLease>>? acquire,CancellationToken cancellation)
    {
        var entry=operation.Entry;var source=operation.InstallMetadata!;var read=operation.Request.Action=="kiosk.direct.install-status";var invoked=false;var permissionRequired=false;
        BrowserCommandOutcome Outcome(string state,BrowserDirectInstallOutcome facts,string? error=null)=>new(operation.Request.RequestId,operation.Request.Action,state,Error:error){KioskDirectInstall=facts};
        try
        {
            KioskDirectOperatorCommand command;
            if(!read)
            {
                if(acquire is null)throw new InvalidOperationException("direct_install_source_unavailable");
                var lease=await acquire(cancellation).ConfigureAwait(false);operation.UploadSourceLease=lease;
                if(lease is null || lease.Receipt.Id!=source.ArtifactId || lease.Receipt.Sha256!=source.Sha256 || lease.Receipt.SizeBytes!=source.SizeBytes ||
                    !lease.Stream.CanRead || lease.Stream.CanWrite || !lease.Stream.CanSeek || lease.Stream.Position!=0 || lease.Stream.Length!=source.SizeBytes ||
                    !lease.Path.EndsWith(".apk",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("direct_install_source_binding_invalid");
                command=KioskDirectOperatorCommand.Install([lease.Path],true,source.InstallRequestId);
                if(command.LocalApkPaths is not {Count:1} paths || paths[0]!=lease.Path || !command.OperatorConfirmed)throw new InvalidDataException("direct_install_source_path_invalid");
            }
            else command=KioskDirectOperatorCommand.InstallStatus(source.InstallRequestId);
            // Source hashing finishes before the immediate pre-invocation signed
            // permission/session/generation read. Status never touches a source.
            var statusCommand=KioskDirectOperatorCommand.Status();
            var resultStatus=await entry.Session!.ExecuteAsync(statusCommand,cancellation).ConfigureAwait(false);
            var status=BrowserDirectProjection.Result(resultStatus,statusCommand,entry.Receipt!,entry.RemoteSession,entry.RemoteGeneration);
            RequireInstallLive(entry,cancellation);
            if(!read && !status.InstallerAllowed){permissionRequired=true;throw new InvalidOperationException("direct_install_permission_required");}
            invoked=true;
            var result=await entry.Session.ExecuteAsync(command,cancellation).ConfigureAwait(false);
            using var unexpectedTags=result.TagExport;
            if(result.Command!=command || result.Mutation.ActionId!=command.ActionId || result.Mutation.Action!=command.Action ||
                result.Mutation.RequestId!=source.InstallRequestId || command.RequestId!=source.InstallRequestId || result.InstallReceipt is not { } receipt || receipt.RequestId!=source.InstallRequestId ||
                result.Status is not null || result.KioskResult is not null || result.RequestReceipt is not null || result.StagedFiles is not null || result.StagedFile is not null ||
                result.LocalFileName is not null || unexpectedTags is not null || receipt.PackageName is { } package && (!BrowserDirectInstallValidation.Package(package) || package!=source.PackageName))
                throw new InvalidDataException("direct_install_result_binding_invalid");
            var tuple=(receipt.State,receipt.Completed,receipt.SessionId,result.Mutation.Stage);
            (string Top,string Effect,bool Reconciles) mapped=tuple switch
            {
                ("installed",true,>=0,OperatorMutationStage.Confirmed)=>("completed","installed",true),
                ("failed",true,>=0,OperatorMutationStage.Failed)=>("failed","failed",true),
                ("staging-session" or "pending-android" or "cleanup-required",false,>=0,OperatorMutationStage.Pending)=>("pending","pending",false),
                ("pending-wearer-confirmation",false,>=0,OperatorMutationStage.PendingWearerAction)=>("pending","pendingWearerAction",false),
                ("needs-wearer-permission",false,null,OperatorMutationStage.PendingWearerAction)=>("pending","pendingWearerAction",false),
                ("pending",false,null,OperatorMutationStage.Pending) when read=>("outcomeUnknown","unknown",false),
                _=>throw new InvalidDataException("direct_install_receipt_invalid")
            };
            RequireInstallLive(entry,cancellation);
            return Outcome(mapped.Top,source with{OriginalEffect=mapped.Effect,ReconcilesOriginal=mapped.Reconciles,
                CoreReceipt=new(command.ActionId,command.Action,source.InstallRequestId,result.Mutation.Stage),
                Receipt=new(receipt.RequestId,receipt.State,receipt.Completed,receipt.SessionId,receipt.PackageName)},mapped.Effect=="unknown"?"direct_install_unknown":null);
        }
        catch
        {
            if(read || invoked)return Outcome("outcomeUnknown",source with{OriginalEffect="unknown",ReconcilesOriginal=false,CoreReceipt=null,Receipt=null},"direct_install_unknown");
            return Outcome("failed",source with{OriginalEffect="notDispatched",ReconcilesOriginal=true,CoreReceipt=null,Receipt=null},permissionRequired?"direct_install_permission_required":"direct_install_not_dispatched");
        }
    }

    private void RequireInstallLive(Entry entry,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();lock(_sync)
        {
            Expire(entry);
            if(_stopping || !ReferenceEquals(_entry,entry) || !entry.Published || entry.State!="connected" || entry.Revoked || entry.CloseAttempted)
                throw new InvalidDataException("direct_install_connection_unavailable");
        }
    }
}
