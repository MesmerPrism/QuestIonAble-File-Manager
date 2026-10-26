using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectConnectionsTests
{
    private static byte[] Connect(string? id = null) => BrowserDirectCommandsTests.Body(id: id ?? Guid.NewGuid().ToString("D"));
    private static byte[] On(string action, string handle, string? id = null) => BrowserDirectCommandsTests.Body(action, id ?? Guid.NewGuid().ToString("D"),handle);
    private static BrowserCommands Broker(BrowserDirectConnections owner, BrowserOperationGate? gate = null) =>
        new((_,_) => throw new InvalidOperationException("ordinary executor must not run"), operationGate:gate, directConnections:owner);

    [Fact]
    public async Task ConstructionAndCapabilityNeverResolveAdbOrActivate()
    {
        var calls = 0; var owner = new BrowserDirectConnections(() => { calls++; throw new Exception("must stay lazy"); });
        var broker = Broker(owner);
        Assert.Contains("kiosk.direct.connect-usb",broker.AvailableActions); Assert.Equal(0,calls);
        Assert.True(await owner.ShutdownAsync()); Assert.Equal(0,calls);
    }

    [Fact]
    public async Task ConnectAdoptsOnceThenStatusAndCloseKeepOriginalScopeAndActualReceipts()
    {
        var clock = new Clock(); FakeSession? session = null;
        var owner = new BrowserDirectConnections((context,_) => Task.FromResult<IBrowserDirectSession>(session = new(context,clock)),clock);
        var broker = Broker(owner); var body = Connect(); var connected = await broker.ExecuteAsync(body);
        Assert.Equal("completed", connected.State); Assert.Null(connected.Result); Assert.Null(connected.Mutation);
        var view = Assert.IsType<BrowserDirectConnectionOutcome>(connected.KioskDirectConnection);
        Assert.Equal("connected",view.State); Assert.True(Guid.TryParseExact(view.ConnectionId,"D",out _));
        Assert.Equal("USB_TARGET",view.Serial); Assert.Equal("stable",view.ProductChannel);
        Assert.Equal("usb_"+Guid.Parse(connected.RequestId).ToString("N"),view.UsbOperationId);
        Assert.Equal(OperatorMutationStage.Confirmed,view.Bootstrap!.Stage); Assert.True(view.Adoption!.StagingInventoryRead);
        var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(()=>broker.ExecuteAsync(body));
        Assert.Equal("request_already_consumed",duplicate.Code); Assert.Null(duplicate.Dispatched);
        var status = await broker.ExecuteAsync(On("kiosk.direct.status",view.ConnectionId!));
        Assert.Equal("completed",status.State); Assert.True(status.KioskDirectConnection!.Status!.InstallerAllowed);
        Assert.Equal(view.ConnectRequestId,status.KioskDirectConnection.ConnectRequestId);
        var closed = await broker.ExecuteAsync(On("kiosk.direct.close",view.ConnectionId!));
        Assert.Equal("completed",closed.State); Assert.Equal("owned-stopped",closed.KioskDirectConnection!.Cleanup!.ListenerDisposition);
        Assert.Equal(1,session!.Adopts); Assert.Equal(1,session.Statuses); Assert.Equal(1,session.Closes);
        Assert.True(await owner.ShutdownAsync()); Assert.Equal(1,session.Closes);
    }

    [Fact]
    public async Task ExpiryErasesKeyWithoutUsbCleanupAndExplicitCloseStillWorks()
    {
        var clock = new Clock(); FakeSession? session = null;
        var owner = new BrowserDirectConnections((context,_)=>Task.FromResult<IBrowserDirectSession>(session=new(context,clock)),clock);
        var broker=Broker(owner); var view=(await broker.ExecuteAsync(Connect())).KioskDirectConnection!;
        clock.Advance(TimeSpan.FromMinutes(2)); Assert.Equal(1,session!.Revocations); Assert.Equal(0,session.Closes);
        var error=await Assert.ThrowsAsync<BrowserRequestException>(()=>broker.ExecuteAsync(On("kiosk.direct.status",view.ConnectionId!)));
        Assert.Equal("direct_connection_expired",error.Code); Assert.False(error.Dispatched);
        Assert.Equal("completed",(await broker.ExecuteAsync(On("kiosk.direct.close",view.ConnectionId!))).State);
    }

    [Fact]
    public async Task PendingAdoptionHoldsSharedGateAndShutdownUntilItsCleanupAcknowledgement()
    {
        var clock=new Clock(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); FakeSession? session=null;
        var owner=new BrowserDirectConnections((context,_)=>Task.FromResult<IBrowserDirectSession>(session=new(context,clock)
        { BeforeAdopt=async()=>{entered.SetResult();await finish.Task;} }),clock);
        var gate=new BrowserOperationGate(); var broker=Broker(owner,gate); var body=Connect(); var pending=broker.ExecuteAsync(body);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(gate.IsBusy);
        Assert.Equal("running",broker.Get(JsonDocument.Parse(body).RootElement.GetProperty("requestId").GetString()!).State);
        var duplicate=await Assert.ThrowsAsync<BrowserRequestException>(()=>broker.ExecuteAsync(body)); Assert.Null(duplicate.Dispatched);
        Assert.Equal("busy",(await Assert.ThrowsAsync<BrowserRequestException>(()=>broker.ExecuteAsync(Connect()))).Code);
        var shutdown=owner.ShutdownAsync(); Assert.False(shutdown.IsCompleted); Assert.Equal(0,session!.Closes);
        finish.SetResult(); await pending; Assert.True(await shutdown); Assert.False(gate.IsBusy); Assert.Equal(1,session.Closes);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("id")]
    [InlineData("target")]
    [InlineData("channel")]
    [InlineData("generation")]
    [InlineData("adopt-command")]
    [InlineData("adopt-marker")]
    [InlineData("adopt-status")]
    [InlineData("adopt-kiosk")]
    [InlineData("adopt-staging")]
    public async Task CrossedAcknowledgementsNeverPublishHandleAndCloseBeforeUnknown(string mismatch)
    {
        var clock=new Clock(); FakeSession? session=null;
        var owner=new BrowserDirectConnections((context,_)=>Task.FromResult<IBrowserDirectSession>(session=new(context,clock){Mismatch=mismatch}),clock);
        var outcome=await Broker(owner).ExecuteAsync(Connect());
        Assert.Equal("outcomeUnknown",outcome.State); Assert.Null(outcome.KioskDirectConnection!.ConnectionId);
        Assert.Equal(1,session!.Closes); Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task FailedAdoptCleanupUnknownRetainsSlotAndNeverRepeatsClose()
    {
        var clock=new Clock(); FakeSession? session=null;
        var owner=new BrowserDirectConnections((context,_)=>Task.FromResult<IBrowserDirectSession>(session=new(context,clock)
        { BeforeAdopt=()=>throw new Exception("private-adoption-failure"), CleanupStage=OperatorMutationStage.CleanupUnknown }),clock);
        var broker=Broker(owner); var outcome=await broker.ExecuteAsync(Connect());
        Assert.Equal("outcomeUnknown",outcome.State); Assert.Equal(OperatorMutationStage.CleanupUnknown,outcome.KioskDirectConnection!.Cleanup!.Stage);
        Assert.Equal("direct_connection_exists",(await Assert.ThrowsAsync<BrowserRequestException>(()=>broker.ExecuteAsync(Connect()))).Code);
        Assert.False(await owner.ShutdownAsync()); Assert.Equal(1,session!.Closes);
    }

    [Fact]
    public async Task PreexistingListenerClosePreservesItAndRawPrivateFieldsNeverEscape()
    {
        var clock=new Clock(); var owner=new BrowserDirectConnections((context,_)=>Task.FromResult<IBrowserDirectSession>(new FakeSession(context,clock){Owned=false}),clock);
        var broker=Broker(owner); var connected=await broker.ExecuteAsync(Connect());
        var closed=await broker.ExecuteAsync(On("kiosk.direct.close",connected.KioskDirectConnection!.ConnectionId!));
        Assert.Equal("preexisting-preserved",closed.KioskDirectConnection!.Cleanup!.ListenerDisposition);
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)}};
        foreach(var value in new[]{connected,closed})
        {
            var json=JsonSerializer.Serialize(value,options);
            Assert.DoesNotContain("private-sentinel",json); Assert.DoesNotContain("private_session_123",json); Assert.DoesNotContain("39873",json);
        }
    }

    [Fact]
    public async Task CancellationAfterAdmissionKeepsGateAndOriginalUntilIndependentCleanupSettles()
    {
        var clock=new Clock();using var stopping=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);FakeSession? session=null;
        var owner=new BrowserDirectConnections((context,token)=>Task.FromResult<IBrowserDirectSession>(session=new(context,clock)
        {
            BeforeAdopt=async()=>{entered.SetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);},
            BeforeClose=async()=>{cleanupEntered.SetResult();await finishCleanup.Task;}
        }),clock);
        var gate=new BrowserOperationGate();var broker=Broker(owner,gate);var body=Connect();var operation=broker.ExecuteAsync(body,stopping.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));stopping.Cancel();await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.IsBusy);Assert.False(operation.IsCompleted);Assert.Equal(1,session!.Revocations);
        var duplicate=await Assert.ThrowsAsync<BrowserRequestException>(()=>broker.ExecuteAsync(body));Assert.Null(duplicate.Dispatched);
        var shutdown=owner.ShutdownAsync();Assert.False(shutdown.IsCompleted);
        finishCleanup.SetResult();var result=await operation;Assert.Equal("outcomeUnknown",result.State);Assert.Null(result.Mutation);Assert.False(gate.IsBusy);
        Assert.True(await shutdown);Assert.Equal(1,session.Closes);
    }

    [Fact]
    public async Task HandlesNeverCrossHostSessionsAndExistingLedgerIdsCannotBecomeDirectOperations()
    {
        var clock=new Clock();var calls=0;var owner=new BrowserDirectConnections((context,_)=>{calls++;return Task.FromResult<IBrowserDirectSession>(new FakeSession(context,clock));},clock);
        var gate=new BrowserOperationGate();var broker=Broker(owner,gate);var connected=await broker.ExecuteAsync(Connect());
        Assert.Equal("session_changed",(await Assert.ThrowsAsync<BrowserRequestException>(()=>Broker(owner,gate).ExecuteAsync(On("kiosk.direct.status",connected.KioskDirectConnection!.ConnectionId!)))).Code);
        Assert.Equal(1,calls);
        var ordinaryId=Guid.NewGuid().ToString("D");
        var shared=new BrowserCommands((command,_)=>Task.FromResult(new OperatorExecutionResult(command)),operationGate:gate,directConnections:owner);
        await shared.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new{requestId=ordinaryId,action="devices"}));
        var duplicate=await Assert.ThrowsAsync<BrowserRequestException>(()=>shared.ExecuteAsync(Connect(ordinaryId)));
        Assert.Equal("request_already_consumed",duplicate.Code);Assert.Null(duplicate.Dispatched);Assert.Equal(1,calls);
        Assert.True(await owner.ShutdownAsync());
    }

    [Theory]
    [InlineData("status-session")]
    [InlineData("status-generation")]
    [InlineData("adopt-marker")]
    public async Task CrossedFreshStatusIsReadFailureAndNeverReplacesOriginalConnect(string mismatch)
    {
        var clock=new Clock();FakeSession? session=null;var owner=new BrowserDirectConnections((context,_)=>Task.FromResult<IBrowserDirectSession>(session=new(context,clock)),clock);
        var broker=Broker(owner);var connected=await broker.ExecuteAsync(Connect());session!.Mismatch=mismatch;
        var read=await broker.ExecuteAsync(On("kiosk.direct.status",connected.KioskDirectConnection!.ConnectionId!));
        Assert.Equal("failed",read.State);Assert.Null(read.KioskDirectConnection!.Status);Assert.Same(connected,broker.Get(connected.RequestId));
        Assert.True(await owner.ShutdownAsync());
    }

    [Theory]
    [InlineData("cleanup-id")]
    [InlineData("cleanup-schema")]
    public async Task CrossedCloseReceiptNeverBecomesConfirmedOrRepeated(string mismatch)
    {
        var clock=new Clock();FakeSession? session=null;var owner=new BrowserDirectConnections((context,_)=>Task.FromResult<IBrowserDirectSession>(session=new(context,clock)),clock);
        var broker=Broker(owner);var connected=await broker.ExecuteAsync(Connect());session!.Mismatch=mismatch;
        var close=await broker.ExecuteAsync(On("kiosk.direct.close",connected.KioskDirectConnection!.ConnectionId!));
        Assert.Equal("outcomeUnknown",close.State);Assert.Null(close.KioskDirectConnection!.Cleanup);Assert.False(await owner.ShutdownAsync());Assert.Equal(1,session.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownNeverOverlapsAnotherOperationAndItsGateWaitIsFinite(bool timeOut)
    {
        var clock=new Clock();FakeSession? session=null;var owner=new BrowserDirectConnections((context,_)=>Task.FromResult<IBrowserDirectSession>(session=new(context,clock)),clock);
        var gate=new BrowserOperationGate();await Broker(owner,gate).ExecuteAsync(Connect());using var other=gate.TryAcquire();Assert.NotNull(other);
        var shutdown=owner.ShutdownAsync();Assert.False(shutdown.IsCompleted);Assert.Equal(1,session!.Revocations);Assert.Equal(0,session.Closes);
        if(timeOut)clock.Advance(TimeSpan.FromMinutes(2));else other.Dispose();
        Assert.Equal(!timeOut,await shutdown.WaitAsync(TimeSpan.FromSeconds(3)));Assert.Equal(timeOut?0:1,session.Closes);
    }

    [Fact]
    public async Task ExpiryRevocationFailureCannotEscapeTimerCallbackOrConfirmStatus()
    {
        var clock=new Clock();FakeSession? session=null;var owner=new BrowserDirectConnections((context,_)=>Task.FromResult<IBrowserDirectSession>(session=new(context,clock){ThrowOnRevoke=true}),clock);
        var broker=Broker(owner);var connected=await broker.ExecuteAsync(Connect());clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0,session!.Closes);
        await Assert.ThrowsAsync<BrowserRequestException>(()=>broker.ExecuteAsync(On("kiosk.direct.status",connected.KioskDirectConnection!.ConnectionId!)));
        Assert.Equal("completed",(await broker.ExecuteAsync(On("kiosk.direct.close",connected.KioskDirectConnection!.ConnectionId!))).State);
    }

    [Fact]
    public async Task ReservationBetweenOldGateReleaseAndCompletionCannotLeavePhantomConnection()
    {
        var clock=new Clock();var owner=new BrowserDirectConnections((context,_)=>Task.FromResult<IBrowserDirectSession>(new FakeSession(context,clock)),clock);
        var connect=owner.Reserve(BrowserDirectCommands.Parse(Connect()),"host");
        var connected=await owner.ExecuteAsync(connect,CancellationToken.None);owner.Complete(connect);
        var close=owner.Reserve(BrowserDirectCommands.Parse(On("kiosk.direct.close",connected.KioskDirectConnection!.ConnectionId!)),"host");
        await owner.ExecuteAsync(close,CancellationToken.None);
        // The old broker released its gate, but has not called Complete yet.
        var next=BrowserDirectCommands.Parse(Connect());
        Assert.Equal("busy",Assert.Throws<BrowserRequestException>(()=>owner.Reserve(next,"host")).Code);
        owner.Complete(close);
        var reserved=owner.Reserve(next,"host");
        Assert.Equal("completed",(await owner.ExecuteAsync(reserved,CancellationToken.None)).State);owner.Complete(reserved);
        Assert.True(await owner.ShutdownAsync());
    }

    internal sealed class FakeSession(BrowserDirectConnectContext context, Clock clock) : IBrowserDirectSession
    {
        public bool Owned=true,ThrowOnRevoke; public string? Mismatch; public OperatorMutationStage CleanupStage=OperatorMutationStage.Confirmed;
        public Func<Task>? BeforeAdopt,BeforeClose; public int Adopts,Statuses,Closes,Revocations;
        public RustyKioskUsbDirectLinkReceipt Receipt => new(Mismatch=="schema"?"wrong":RustyKioskUsbDirectLinkBootstrapper.ReceiptSchema,
            Mismatch=="id"?"usb_ffffffffffffffffffffffffffffffff":context.UsbOperationId,
            Mismatch=="channel"?"labs":context.ProductChannel,RustyKioskProductContract.Parse(context.ProductChannel).MainPackage,10042,
            Hash(Mismatch=="target"?"CROSSED":context.Serial),Hash(Mismatch=="generation"?"42":"41"),Owned,clock.Start.AddMinutes(2),OperatorMutationStage.Confirmed,"private-sentinel");
        public async Task<KioskDirectOperatorResult> ExecuteAsync(KioskDirectOperatorCommand command,CancellationToken token)
        {
            if(command.Action==KioskDirectOperatorAction.Adopt){Adopts++;if(BeforeAdopt is not null)await BeforeAdopt();}else Statuses++;
            var state=new RustyKioskState(0,0,0,false,[],"",null,null,null,null,false,false,false,false,false,false,false,false,null,"private-sentinel","private-sentinel");
            return new(Mismatch=="adopt-command"?KioskDirectOperatorCommand.Status():command,
                new(command.ActionId,command.Action,Mismatch=="adopt-marker"?OperatorMutationStage.Pending:OperatorMutationStage.Confirmed,"private-sentinel"),
                new(Mismatch=="adopt-status"?"wrong":RustyKioskDirectClient.ContractSchema,"http://private-sentinel:39873",true,"private-sentinel","private-sentinel",Mismatch=="status-generation"?42:41,Mismatch=="status-session"?"crossed_session_123":"private_session_123"),
                Mismatch=="adopt-kiosk"?null:new(RustyKioskContract.ResultSchema,"status_request_123",RustyKioskCommand.Status,true,true,"private-sentinel",state,"private-sentinel"),
                StagedFiles:Mismatch=="adopt-staging"?null:[new("private-sentinel",1,1)]);
        }
        public async Task<RustyKioskUsbDirectCleanupReceipt> CloseAsync(){Closes++;if(BeforeClose is not null)await BeforeClose();return new(Mismatch=="cleanup-schema"?"wrong":RustyKioskUsbDirectLinkBootstrapper.CleanupReceiptSchema,Mismatch=="cleanup-id"?"usb_ffffffffffffffffffffffffffffffff":context.UsbOperationId,CleanupStage,"private-sentinel");}
        public void Revoke(){Revocations++;if(ThrowOnRevoke)throw new Exception("private-sentinel");}
        private static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Start {get;}=DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        private DateTimeOffset now=DateTimeOffset.FromUnixTimeSeconds(1_800_000_000); private readonly List<Timer> timers=[];
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(TimeSpan time){now+=time;foreach(var timer in timers.ToArray())timer.Fire(now);}
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {var timer=new Timer(callback,state,now+dueTime);timers.Add(timer);return timer;}
        private sealed class Timer(TimerCallback callback,object? state,DateTimeOffset due):ITimer
        { private bool disposed; public void Fire(DateTimeOffset now){if(!disposed&&now>=due){disposed=true;callback(state);}} public bool Change(TimeSpan dueTime,TimeSpan period)=>throw new NotSupportedException(); public void Dispose()=>disposed=true;public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;} }
    }
}
