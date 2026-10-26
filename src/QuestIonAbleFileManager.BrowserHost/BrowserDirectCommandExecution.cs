using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed partial class BrowserDirectConnections
{
    private async Task<BrowserCommandOutcome> ExecuteCommandAsync(Operation operation,CancellationToken cancellation)
    {
        var entry=operation.Entry;var request=operation.Request;var source=operation.CommandMetadata!;
        // This existing signed Status validates the adopted session/generation.
        // It never repeats Adopt, logical Invoke(Status), or staging inventory.
        var statusCommand=KioskDirectOperatorCommand.Status();
        var status=await entry.Session!.ExecuteAsync(statusCommand,cancellation).ConfigureAwait(false);
        BrowserDirectProjection.Result(status,statusCommand,entry.Receipt!,entry.RemoteSession,entry.RemoteGeneration);
        RequireCommandLive(entry);
        var kind=BrowserDirectCommands.CommandName(source.Command);
        var command=request.Action switch
        {
            "kiosk.direct.invoke"=>KioskDirectOperatorCommand.Invoke(kind,source.Value,true,source.LogicalRequestId),
            "kiosk.direct.request-status"=>KioskDirectOperatorCommand.RequestStatus(source.LogicalRequestId),
            "kiosk.direct.request-result"=>KioskDirectOperatorCommand.RequestResult(source.LogicalRequestId,kind,source.Value),
            "kiosk.direct.request-cancel"=>KioskDirectOperatorCommand.Cancel(source.LogicalRequestId,true),
            _=>throw new InvalidDataException("direct_command_unsupported")
        };
        var result=await entry.Session.ExecuteAsync(command,cancellation).ConfigureAwait(false);
        RequireCommandLive(entry);
        return BrowserDirectCommandProjection.Project(request,source,command,result);
    }
    private void RequireCommandLive(Entry entry)
    {
        lock(_sync)
        {
            Expire(entry);
            if(entry.State!="connected" || entry.Revoked || entry.CloseAttempted)throw new InvalidDataException("direct_command_connection_unavailable");
        }
    }
}
