using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

internal sealed record BrowserDirectConnectContext(string RequestId, string Serial, string ProductChannel, string UsbOperationId);
internal interface IBrowserDirectSession
{
    RustyKioskUsbDirectLinkReceipt Receipt { get; }
    Task<KioskDirectOperatorResult> ExecuteAsync(KioskDirectOperatorCommand command, CancellationToken cancellationToken);
    Task<RustyKioskUsbDirectCleanupReceipt> CloseAsync();
    void Revoke();
}

/// <summary>One native memory-only connection. Handles and receipts never contain its credential or endpoint.</summary>
public sealed partial class BrowserDirectConnections
{
    private readonly object _sync = new();
    private readonly Func<BrowserDirectConnectContext,CancellationToken,Task<IBrowserDirectSession>> _connect;
    private readonly TimeProvider _clock;
    private Entry? _entry;
    private Operation? _active;
    private string? _hostSession;
    private bool _stopping;
    private Task<bool>? _shutdown;
    private BrowserOperationGate? _sharedGate;

    public BrowserDirectConnections(Func<string>? resolveAdbPath = null, TimeProvider? timeProvider = null)
    {
        _clock=timeProvider ?? TimeProvider.System;
        _connect=async (context,token)=>
        {
            var path=(resolveAdbPath ?? (()=>AdbClient.CreateDefault().AdbPath))();
            var client=new AdbClient(path,new BoundedRunner(new CommandRunner()));
            var session=await new RustyKioskUsbDirectLinkBootstrapper(client,()=>_clock.GetUtcNow()).ConnectWithOperationIdAsync(
                context.Serial,RustyKioskProductContract.Parse(context.ProductChannel).Channel,true,context.UsbOperationId,cancellationToken:token).ConfigureAwait(false);
            return new NativeSession(session);
        };
    }
    internal BrowserDirectConnections(Func<BrowserDirectConnectContext,CancellationToken,Task<IBrowserDirectSession>> connect,TimeProvider clock)
    { _connect=connect; _clock=clock; }

    internal void BindGate(BrowserOperationGate gate)
    {
        lock(_sync)
        {
            if(_sharedGate is not null && !ReferenceEquals(_sharedGate,gate))throw new InvalidOperationException("direct_owner_already_bound");
            _sharedGate=gate;
        }
    }

    internal Operation Reserve(BrowserDirectRequest request,string hostSession,BrowserDirectCommandOutcome? original=null)
    {
        lock(_sync)
        {
            if(_stopping)throw new BrowserRequestException("direct_owner_stopping",409);
            if(_active is not null)throw new BrowserRequestException("busy",409);
            if(_hostSession is not null && _hostSession!=hostSession)throw new BrowserRequestException("session_changed",409);
            Entry entry;
            if(request.Action=="kiosk.direct.connect-usb")
            {
                if(_entry is not null)throw new BrowserRequestException("direct_connection_exists",409);
                entry=new(new(request.RequestId,request.Serial!,request.ProductChannel!,request.UsbOperationId));
            }
            else
            {
                entry=_entry is { } found && found.Id==request.ConnectionId ? found : throw new BrowserRequestException("direct_connection_not_found",404);
                Expire(entry);
                var requiresLive=request.Action=="kiosk.direct.status" || request.IsCommand || request.Action is "kiosk.direct.tags-export" or "kiosk.direct.staging-list" or "kiosk.direct.staging-upload";
                if(requiresLive && entry.State=="expired")throw new BrowserRequestException("direct_connection_expired",409);
                if(entry.Session is null || requiresLive && (entry.State!="connected" || entry.Revoked || !entry.Published))
                    throw new BrowserRequestException("direct_connection_not_found",409);
            }
            var metadata=request.IsCommand ? BrowserDirectCommandProjection.Metadata(request,entry,original) : null;
            var operation=new Operation(request,entry,metadata);
            _hostSession=hostSession;
            if(request.Action=="kiosk.direct.connect-usb")_entry=entry;
            return _active=operation;
        }
    }

    internal async Task<BrowserCommandOutcome> ExecuteAsync(Operation operation,CancellationToken cancellation,
        Func<KioskDirectTagExportLease,CancellationToken,Task<BrowserArtifactReceipt>>? stageTagExport=null,
        Func<CancellationToken,Task<BrowserArtifactLease>>? acquireUpload=null)
    {
        var request=operation.Request; var entry=operation.Entry;
        try
        {
            if(request.Action=="kiosk.direct.tags-export")return await ExecuteTagExportAsync(operation,stageTagExport,cancellation).ConfigureAwait(false);
            if(request.Action=="kiosk.direct.staging-list")return await ExecuteStagingListAsync(operation,cancellation).ConfigureAwait(false);
            if(request.Action=="kiosk.direct.staging-upload")return await ExecuteUploadAsync(operation,acquireUpload,cancellation).ConfigureAwait(false);
            if(request.IsCommand)return await ExecuteCommandAsync(operation,cancellation).ConfigureAwait(false);
            if(request.Action=="kiosk.direct.connect-usb")
            {
                entry.Session=await _connect(entry.Context,cancellation).ConfigureAwait(false);
                var receipt=entry.Session.Receipt;
                BrowserDirectProjection.Bootstrap(receipt,entry.Context,_clock.GetUtcNow());
                lock(_sync)
                {
                    entry.Receipt=receipt;
                    entry.Timer=_clock.CreateTimer(_=>{lock(_sync)Expire(entry);},null,receipt.ExpiresAtUtc-_clock.GetUtcNow(),Timeout.InfiniteTimeSpan);
                }
                var command=KioskDirectOperatorCommand.Adopt();
                var result=await entry.Session.ExecuteAsync(command,cancellation).ConfigureAwait(false);
                var status=BrowserDirectProjection.Result(result,command,receipt);
                lock(_sync)
                {
                    Expire(entry);
                    if(entry.Revoked || entry.State=="expired")throw new InvalidDataException("direct_expired_during_adoption");
                    entry.RemoteSession=status.SessionId; entry.RemoteGeneration=status.BridgeGeneration;
                    entry.Adoption=new(result.Mutation.Stage,true,true,true); entry.State="connected"; entry.Published=true;
                }
                return Outcome(request,entry,"completed");
            }
            if(request.Action=="kiosk.direct.close")
            {
                await CloseAsync(entry).ConfigureAwait(false);
                return Outcome(request,entry,entry.Cleanup?.Stage==OperatorMutationStage.Confirmed?"completed":"outcomeUnknown",
                    entry.Cleanup?.Stage==OperatorMutationStage.Confirmed?null:"direct_cleanup_unknown");
            }
            var read=KioskDirectOperatorCommand.Status();
            var observed=await entry.Session!.ExecuteAsync(read,cancellation).ConfigureAwait(false);
            var verified=BrowserDirectProjection.Result(observed,read,entry.Receipt!,entry.RemoteSession,entry.RemoteGeneration);
            lock(_sync)
            {
                Expire(entry);
                if(entry.State!="connected")throw new InvalidDataException("direct_status_after_expiry");
            }
            return Outcome(request,entry,"completed",status:new(verified.InstallerAllowed,_clock.GetUtcNow()));
        }
        catch(Exception exception)
        {
            if(request.Action=="kiosk.direct.connect-usb")
            {
                if(entry.Session is not null)await CloseAsync(entry).ConfigureAwait(false);
                else if(exception is RustyKioskUsbDirectBootstrapException bootstrap)
                {
                    try { entry.Cleanup=BrowserDirectProjection.Cleanup(bootstrap.CleanupReceipt,entry.Context.UsbOperationId,null); }
                    catch { /* An unbound acknowledgement is never confirmation. */ }
                    RetireIfClean(entry);
                }
                else entry.State="unavailable";
            }
            if(request.IsCommand)return new(request.RequestId,request.Action,request.Mutation?"outcomeUnknown":"failed",
                Error:"direct_command_unknown",KioskDirectCommand:operation.CommandMetadata);
            return Outcome(request,entry,request.Mutation?"outcomeUnknown":"failed",
                request.Mutation?"direct_operation_unknown":"direct_status_unavailable");
        }
    }

    internal void Complete(Operation operation)
    {
        // ExecuteDirectAsync has cached the terminal outcome before Complete.
        // Release source custody only after actual Core settlement/publication.
        try{Interlocked.Exchange(ref operation.UploadSourceLease,null)?.Dispose();}
        finally
        {
            lock(_sync){if(ReferenceEquals(_active,operation))_active=null;}
            operation.Completion.TrySetResult();
        }
    }

    public Task<bool> ShutdownAsync()
    {
        lock(_sync){_stopping=true;return _shutdown ??= ShutdownCoreAsync(_active?.Completion.Task);}
    }
    private async Task<bool> ShutdownCoreAsync(Task? active)
    {
        if(active is not null)await active.ConfigureAwait(false);
        Entry? entry;lock(_sync)entry=_entry;
        if(entry is null)return true;
        if(entry.Session is null)return entry.Cleanup?.Stage==OperatorMutationStage.Confirmed;
        lock(_sync)Revoke(entry);
        IDisposable? gate=null;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90),_clock);
        try
        {
            // Shutdown resource cleanup must not overlap another adapter's active
            // operation. This waits only for cleanup custody, never user admission.
            if(_sharedGate is not null)
                while((gate=_sharedGate.TryAcquire()) is null)await Task.Delay(10,deadline.Token).ConfigureAwait(false);
            await CloseAsync(entry).ConfigureAwait(false);
            return entry.Cleanup?.Stage==OperatorMutationStage.Confirmed;
        }
        catch(OperationCanceledException){return false;}
        finally{gate?.Dispose();}
    }
    private async Task CloseAsync(Entry entry)
    {
        lock(_sync)
        {
            if(entry.CloseAttempted)return;
            entry.CloseAttempted=true; entry.State="closing"; entry.Timer?.Dispose();
            Revoke(entry);
        }
        try
        {
            var receipt=await entry.Session!.CloseAsync().ConfigureAwait(false);
            entry.Cleanup=BrowserDirectProjection.Cleanup(receipt,entry.Context.UsbOperationId,entry.Receipt?.EnabledByRequest);
        }
        catch { /* Preserve uncertain cleanup and never repeat Close after a lost acknowledgement. */ }
        RetireIfClean(entry);
    }
    private void RetireIfClean(Entry entry)
    {
        lock(_sync)
        {
            entry.State=entry.Cleanup?.Stage==OperatorMutationStage.Confirmed?"closed":"cleanupUnknown";
            if(entry.State=="closed" && ReferenceEquals(_entry,entry))_entry=null;
        }
    }
    private void Expire(Entry entry)
    {
        if(entry.Receipt is not null && _clock.GetUtcNow()>=entry.Receipt.ExpiresAtUtc && entry.State is "opening" or "connected")
        { entry.State="expired"; Revoke(entry); }
    }
    private static void Revoke(Entry entry)
    {
        if(entry.Revoked)return;
        entry.Revoked=true;
        try{entry.Session?.Revoke();}
        catch{entry.State="cleanupUnknown";} // Never let resource disposal escape a timer callback.
    }
    private static BrowserCommandOutcome Outcome(BrowserDirectRequest request,Entry entry,string state,string? error=null,BrowserDirectStatus? status=null) =>
        new(request.RequestId,request.Action,state,Error:error,KioskDirectConnection:new(entry.Published?entry.Id:null,entry.Context.RequestId,
            entry.Context.UsbOperationId,entry.Context.Serial,entry.Context.ProductChannel,entry.State,entry.Receipt?.ExpiresAtUtc,entry.Receipt?.EnabledByRequest,
            entry.Receipt is { } receipt ? new(receipt.Schema,receipt.OperationId,receipt.Stage) : null,entry.Adoption,status,entry.Cleanup));

    internal sealed class Entry(BrowserDirectConnectContext context)
    {
        internal readonly BrowserDirectConnectContext Context=context;
        internal readonly string Id=Guid.NewGuid().ToString("D");
        internal string State="opening"; internal bool Published,Revoked,CloseAttempted;
        internal IBrowserDirectSession? Session; internal RustyKioskUsbDirectLinkReceipt? Receipt; internal ITimer? Timer;
        internal string? RemoteSession; internal long? RemoteGeneration;
        internal BrowserDirectAdoption? Adoption; internal BrowserDirectCleanup? Cleanup;
    }
    internal sealed record Operation(BrowserDirectRequest Request,Entry Entry,BrowserDirectCommandOutcome? CommandMetadata=null)
    {
        internal TaskCompletionSource Completion {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BrowserArtifactLease? UploadSourceLease;
    }

    private sealed class NativeSession(RustyKioskUsbDirectLinkSession session):IBrowserDirectSession
    {
        private readonly KioskDirectOperatorExecutor executor=new(session.Client);
        public RustyKioskUsbDirectLinkReceipt Receipt=>session.Receipt;
        public Task<KioskDirectOperatorResult> ExecuteAsync(KioskDirectOperatorCommand command,CancellationToken token)=>executor.ExecuteAsync(command,token);
        public Task<RustyKioskUsbDirectCleanupReceipt> CloseAsync()=>session.CloseAsync();
        public void Revoke()=>session.Client.Dispose();
    }

    internal sealed class BoundedRunner:ICommandRunner,ISensitiveCommandRunner
    {
        private readonly IStreamingCommandRunner streaming; private readonly ISensitiveCommandRunner sensitive;
        internal BoundedRunner(ICommandRunner runner)
        {
            streaming=runner as IStreamingCommandRunner ?? throw new NotSupportedException("direct_bounded_runner_required");
            sensitive=runner as ISensitiveCommandRunner ?? throw new NotSupportedException("direct_sensitive_runner_required");
        }
        public async Task<CommandResult> RunAsync(string fileName,IReadOnlyList<string> arguments,TimeSpan timeout,CancellationToken token=default)
        {
            using var output=new MemoryStream();
            var result=await streaming.RunToStreamAsync(fileName,arguments,output,1024*1024,timeout,token).ConfigureAwait(false);
            if(output.Length>1024*1024 || result.BytesWritten!=output.Length || result.CommandResult.StandardOutput.Length!=0 || result.CommandResult.StandardError.Length!=0)
                throw new InvalidDataException("direct_bounded_output_invalid");
            return result.CommandResult with {StandardOutput=new UTF8Encoding(false,true).GetString(output.ToArray())};
        }
        public Task<SensitiveCommandResult<T>> RunSensitiveAsync<T>(string fileName,IReadOnlyList<string> arguments,int maxOutput,int maxError,TimeSpan timeout,
            Func<ReadOnlyMemory<byte>,T> parser,CancellationToken token=default)=>sensitive.RunSensitiveAsync(fileName,arguments,maxOutput,maxError,timeout,parser,token);
    }
}
