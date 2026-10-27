using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectInstallTests
{
    private const string Install = "kiosk.direct.install", Status = "kiosk.direct.install-status";
    private const string Handle = "00000000-0000-4000-8000-000000000002", Inspection = "00000000-0000-4000-8000-000000000003";
    private static byte[] Body(string? handle=null,string? id=null,BrowserArtifactReceipt? artifact=null,string? inspection=null) => JsonSerializer.SerializeToUtf8Bytes(new
        {requestId=id??Guid.NewGuid().ToString("D"),action=Install,connectionId=handle??Handle,artifactId=artifact?.Id??new string('a',32),sha256=artifact?.Sha256??new string('b',64),sizeBytes=artifact?.SizeBytes??4,inspectionRequestId=inspection??Inspection,confirmed=true});
    private static byte[] Read(string handle,string original) => JsonSerializer.SerializeToUtf8Bytes(new {requestId=Guid.NewGuid().ToString("D"),action=Status,connectionId=handle,originalRequestId=original});

    [Fact]
    public void InstallAndStatusAreSeparateClosedTypedRequests()
    {
        var install=BrowserDirectCommands.Parse(Body()); Assert.True(install.Mutation); Assert.Null(install.Command); Assert.False(install.Recovery);
        var status=BrowserDirectCommands.Parse(Read(Handle,Inspection)); Assert.False(status.Mutation); Assert.Equal(Inspection,status.OriginalRequestId); Assert.Null(status.ArtifactId);
    }
    [Theory]
    [InlineData("path","\"C:/private.apk\"")]
    [InlineData("packageName","\"com.example.app\"")]
    [InlineData("installRequestId","\"install_attacker\"")]
    [InlineData("options","{}")]
    public void CallerCannotExpandInstallAuthority(string name,string value)
    {
        var text=Encoding.UTF8.GetString(Body());
        Assert.Equal("fields_invalid",Assert.Throws<BrowserRequestException>(()=>BrowserDirectCommands.Parse(Encoding.UTF8.GetBytes(text[..^1]+",\""+name+"\":"+value+"}"))).Code);
    }

    [Theory]
    [InlineData("installed",true,7,OperatorMutationStage.Confirmed,"completed","installed",true)]
    [InlineData("failed",true,0,OperatorMutationStage.Failed,"failed","failed",true)]
    [InlineData("staging-session",false,7,OperatorMutationStage.Pending,"pending","pending",false)]
    [InlineData("pending-android",false,7,OperatorMutationStage.Pending,"pending","pending",false)]
    [InlineData("cleanup-required",false,7,OperatorMutationStage.Pending,"pending","pending",false)]
    [InlineData("pending-wearer-confirmation",false,7,OperatorMutationStage.PendingWearerAction,"pending","pendingWearerAction",false)]
    [InlineData("needs-wearer-permission",false,null,OperatorMutationStage.PendingWearerAction,"pending","pendingWearerAction",false)]
    public async Task ClosedInitialReceiptTableBindsOnlyTheExpectedInspectedSource(string state,bool completed,int? session,OperatorMutationStage stage,string top,string effect,bool reconciles)
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create();f.Native.State=state;f.Native.Completed=completed;f.Native.Session=session;f.Native.Stage=stage;
        var id=Guid.NewGuid().ToString("D");var body=Body(f.Handle,id,f.Artifact,f.InspectionId);var result=await f.Broker.ExecuteAsync(body);
        Assert.Equal(top,result.State);Assert.Null(result.Result);Assert.Null(result.Mutation);var d=Assert.IsType<BrowserDirectInstallOutcome>(result.KioskDirectInstall);
        AssertSource(f,d,id);Assert.Equal("install",d.Operation);Assert.Equal(effect,d.OriginalEffect);Assert.Equal(reconciles,d.ReconcilesOriginal);
        Assert.Equal(state,d.Receipt!.State);Assert.Equal(completed,d.Receipt.Completed);Assert.Equal(session,d.Receipt.SessionId);Assert.Null(d.Receipt.PackageName);
        Assert.Equal(stage,d.CoreReceipt!.Stage);Assert.Equal(KioskDirectOperatorAction.Install,d.CoreReceipt.Action);Assert.Equal(d.InstallRequestId,d.CoreReceipt.RequestId);
        var command=Assert.Single(f.Native.Calls,c=>c.Action==KioskDirectOperatorAction.Install);Assert.Equal(d.InstallRequestId,command.RequestId);Assert.True(command.OperatorConfirmed);
        Assert.Single(command.LocalApkPaths!);Assert.EndsWith(".apk",command.LocalApkPaths![0],StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(f.Native.Calls,c=>c.Action is KioskDirectOperatorAction.UploadStaging or KioskDirectOperatorAction.Invoke or KioskDirectOperatorAction.ListStaging);
        Assert.Same(result,f.Broker.Get(id));Assert.Null((await Assert.ThrowsAsync<BrowserRequestException>(()=>f.Broker.ExecuteAsync(body))).Dispatched);
        var json=JsonSerializer.Serialize(result);foreach(var hidden in new[]{"private-sentinel","private-version","LocalPath","LocalApkPaths","Message","endpoint"})Assert.DoesNotContain(hidden,json,StringComparison.OrdinalIgnoreCase);
        await f.Store.RemoveAsync(f.Artifact.Id);
    }

    [Theory]
    [InlineData("installed",true,0,OperatorMutationStage.Confirmed,"completed","installed",true)]
    [InlineData("failed",true,0,OperatorMutationStage.Failed,"failed","failed",true)]
    [InlineData("pending-android",false,0,OperatorMutationStage.Pending,"pending","pending",false)]
    [InlineData("needs-wearer-permission",false,null,OperatorMutationStage.PendingWearerAction,"pending","pendingWearerAction",false)]
    [InlineData("pending",false,null,OperatorMutationStage.Pending,"outcomeUnknown","unknown",false)]
    public async Task StatusUsesOriginalLogicalIdAndNoArtifactOrInstallReplay(string state,bool completed,int? session,OperatorMutationStage stage,string top,string effect,bool reconciles)
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create();f.Native.ThrowInstall=true;
        var original=await f.Broker.ExecuteAsync(Body(f.Handle,artifact:f.Artifact,inspection:f.InspectionId));Assert.Equal("outcomeUnknown",original.State);
        await f.Store.RemoveAsync(f.Artifact.Id);f.Native.ThrowInstall=false;f.Native.State=state;f.Native.Completed=completed;f.Native.Session=session;f.Native.Stage=stage;f.Native.Allowed=false;
        var result=await f.Broker.ExecuteAsync(Read(f.Handle,original.RequestId));Assert.Equal(top,result.State);var d=Assert.IsType<BrowserDirectInstallOutcome>(result.KioskDirectInstall);
        AssertSource(f,d,original.RequestId);Assert.Equal("installStatus",d.Operation);Assert.Equal(effect,d.OriginalEffect);Assert.Equal(reconciles,d.ReconcilesOriginal);
        Assert.Equal(state,d.Receipt!.State);Assert.Equal(stage,d.CoreReceipt!.Stage);Assert.Equal(KioskDirectOperatorAction.InstallStatus,d.CoreReceipt.Action);
        var command=Assert.Single(f.Native.Calls,c=>c.Action==KioskDirectOperatorAction.InstallStatus);Assert.Null(command.LocalApkPaths);Assert.False(command.OperatorConfirmed);
        Assert.Single(f.Native.Calls,c=>c.Action==KioskDirectOperatorAction.Install);Assert.Equal(1,f.Inspections);Assert.Same(original,f.Broker.Get(original.RequestId));
        Assert.Equal("direct_install_original_unavailable",(await Assert.ThrowsAsync<BrowserRequestException>(()=>f.Broker.ExecuteAsync(Read(f.Handle,result.RequestId)))).Code);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("completed")]
    [InlineData("session")]
    [InlineData("package")]
    [InlineData("request")]
    [InlineData("command")]
    [InlineData("marker-id")]
    [InlineData("marker-action")]
    [InlineData("marker-request")]
    [InlineData("marker-stage")]
    [InlineData("extra-tags")]
    [InlineData("initial-synthetic")]
    public async Task ContradictoryOrCrossedInstallEvidenceIsUnknownWithoutProof(string variant)
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create();f.Native.Variant=variant;
        var result=await f.Broker.ExecuteAsync(Body(f.Handle,artifact:f.Artifact,inspection:f.InspectionId));Assert.Equal("outcomeUnknown",result.State);
        var d=Assert.IsType<BrowserDirectInstallOutcome>(result.KioskDirectInstall);Assert.Equal("unknown",d.OriginalEffect);Assert.False(d.ReconcilesOriginal);Assert.Null(d.CoreReceipt);Assert.Null(d.Receipt);
        if(f.Native.UnexpectedStream is not null)Assert.False(f.Native.UnexpectedStream.CanRead);
    }

    [Theory]
    [InlineData("installed",true,null,OperatorMutationStage.Confirmed)]
    [InlineData("failed",true,null,OperatorMutationStage.Failed)]
    [InlineData("failed",false,0,OperatorMutationStage.Failed)]
    [InlineData("needs-wearer-permission",false,0,OperatorMutationStage.PendingWearerAction)]
    [InlineData("pending-wearer-confirmation",false,null,OperatorMutationStage.PendingWearerAction)]
    [InlineData("cleanup-required",true,0,OperatorMutationStage.Failed)]
    [InlineData("pending-android",false,0,OperatorMutationStage.Confirmed)]
    [InlineData("unsupported",true,0,OperatorMutationStage.Failed)]
    public async Task ReceiptCompletionSessionAndCoreStageCannotContradictTheClosedProtocol(string state,bool completed,int? session,OperatorMutationStage stage)
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create();f.Native.State=state;f.Native.Completed=completed;f.Native.Session=session;f.Native.Stage=stage;
        var result=await f.Broker.ExecuteAsync(Body(f.Handle,artifact:f.Artifact,inspection:f.InspectionId));Assert.Equal("outcomeUnknown",result.State);
        Assert.Null(result.KioskDirectInstall!.CoreReceipt);Assert.Null(result.KioskDirectInstall.Receipt);Assert.False(result.KioskDirectInstall.ReconcilesOriginal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9007199254740991)]
    public async Task ExpectedVersionBoundsAndMatchingOptionalReceiptPackageStayHonest(long version)
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create();f.Identity=f.Identity with{VersionCode=version};await f.Inspect();
        f.Native.State="installed";f.Native.Completed=true;f.Native.Session=int.MaxValue;f.Native.Stage=OperatorMutationStage.Confirmed;f.Native.PackageName="com.example.app";
        var result=await f.Broker.ExecuteAsync(Body(f.Handle,artifact:f.Artifact,inspection:f.InspectionId));Assert.Equal("completed",result.State);
        Assert.Equal(version,result.KioskDirectInstall!.VersionCode);Assert.Equal("com.example.app",result.KioskDirectInstall.Receipt!.PackageName);Assert.Equal(int.MaxValue,result.KioskDirectInstall.Receipt.SessionId);
    }

    [Theory]
    [InlineData("split")]
    [InlineData("version-negative")]
    [InlineData("version-huge")]
    [InlineData("signer")]
    [InlineData("package")]
    [InlineData("extension")]
    [InlineData("missing")]
    [InlineData("crossed-artifact")]
    [InlineData("failed-inspection")]
    public async Task AdmissionRequiresExactCompletedSingleApkInspection(string variant)
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create(variant=="extension"?"source.bin":"source.apk");
        f.Identity=variant switch
        {
            "split"=>f.Identity with{SplitName="feature"},"version-negative"=>f.Identity with{VersionCode=-1},"version-huge"=>f.Identity with{VersionCode=9007199254740992},
            "signer"=>f.Identity with{SignerSha256=new string('A',64)},"package"=>f.Identity with{PackageName="bad package"},_=>f.Identity
        };
        if(variant=="failed-inspection")f.FailInspection=true;
        if(variant is not ("missing" or "crossed-artifact"))await f.Inspect();
        var inspection=variant=="missing"?Guid.NewGuid().ToString("D"):f.InspectionId;var source=variant=="crossed-artifact"?f.Artifact with{Id=new string('f',32)}:f.Artifact;
        var before=f.Native.Calls.Count;var id=Guid.NewGuid().ToString("D");
        await Assert.ThrowsAsync<BrowserRequestException>(()=>f.Broker.ExecuteAsync(Body(f.Handle,id,source,inspection)));Assert.Equal(before,f.Native.Calls.Count);
        Assert.Equal("request_not_found",Assert.Throws<BrowserRequestException>(()=>f.Broker.Get(id)).Code);
    }

    [Theory]
    [InlineData("installer")]
    [InlineData("session")]
    [InlineData("source")]
    public async Task PreInvocationFailureIsKnownNotDispatchedAndStatusCannotBorrowIt(string variant)
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create();if(variant=="installer")f.Native.Allowed=false;if(variant=="session")f.Native.Base.Mismatch="status-session";
        if(variant=="source")await f.Store.RemoveAsync(f.Artifact.Id);
        var result=await f.Broker.ExecuteAsync(Body(f.Handle,artifact:f.Artifact,inspection:f.InspectionId));Assert.Equal("failed",result.State);
        var d=Assert.IsType<BrowserDirectInstallOutcome>(result.KioskDirectInstall);Assert.Equal("notDispatched",d.OriginalEffect);Assert.True(d.ReconcilesOriginal);Assert.Null(d.CoreReceipt);Assert.Null(d.Receipt);
        Assert.DoesNotContain(f.Native.Calls,c=>c.Action==KioskDirectOperatorAction.Install);
        Assert.Equal(variant=="installer"?"direct_install_permission_required":"direct_install_not_dispatched",result.Error);
        Assert.Equal("direct_install_original_mismatch",(await Assert.ThrowsAsync<BrowserRequestException>(()=>f.Broker.ExecuteAsync(Read(f.Handle,result.RequestId)))).Code);
    }

    [Fact]
    public async Task RunningMetadataAndKnownIdExistBeforeAcquisitionAwaits()
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held=f.Store.CaptureAsync("held.bin",async(_,_)=>{entered.SetResult();await release.Task;});await entered.Task;
        Task<BrowserCommandOutcome>? pending=null;
        try
        {
            var id=Guid.NewGuid().ToString("D");pending=f.Broker.ExecuteAsync(Body(f.Handle,id,f.Artifact,f.InspectionId));var running=f.Broker.Get(id);
            Assert.Equal("running",running.State);AssertSource(f,running.KioskDirectInstall!,id);Assert.Equal("unknown",running.KioskDirectInstall!.OriginalEffect);Assert.Null(running.KioskDirectInstall.CoreReceipt);
            Assert.Single(f.Native.Calls);Assert.True(f.Gate.IsBusy);release.SetResult();await held;await pending;
        }
        finally{release.TrySetResult();await held;if(pending is not null)await pending;}
    }

    [Fact]
    public async Task InstallSourceLeaseRemainsOwnedUntilOuterPublicationCallsComplete()
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create();var request=BrowserDirectCommands.Parse(Body(f.Handle,artifact:f.Artifact,inspection:f.InspectionId));
        var source=new BrowserDirectInstallSource(f.InspectionId,f.Artifact.Id,f.Artifact.Sha256,f.Artifact.SizeBytes,f.Identity.PackageName,f.Identity.VersionCode,f.Identity.SignerSha256);
        var operation=f.Owner.Reserve(request,f.Broker.SessionId,inspection:source);
        try
        {
            var outcome=await f.Owner.ExecuteAsync(operation,CancellationToken.None,acquireUpload:token=>f.Store.AcquireAsync(f.Artifact.Id,f.Artifact.Sha256,f.Artifact.SizeBytes,token));
            Assert.Equal("pending",outcome.State);Assert.Equal("artifact_in_use",(await Assert.ThrowsAsync<BrowserArtifactException>(()=>f.Store.RemoveAsync(f.Artifact.Id))).Code);
        }
        finally{f.Owner.Complete(operation);}
        await f.Store.RemoveAsync(f.Artifact.Id);
    }

    [Fact]
    public async Task MissingStagingDisablesOnlyInitialInstallWithoutActivatingOwner()
    {
        var calls=0;var owner=new BrowserDirectConnections(()=>{calls++;throw new InvalidOperationException();});var broker=new BrowserCommands((_,_)=>throw new InvalidOperationException(),directConnections:owner);
        Assert.DoesNotContain(Install,broker.AvailableActions);Assert.Contains(Status,broker.AvailableActions);
        Assert.Equal("artifact_staging_unavailable",(await Assert.ThrowsAsync<BrowserRequestException>(()=>broker.ExecuteAsync(Body()))).Code);Assert.Equal(0,calls);Assert.True(await owner.ShutdownAsync());
        Assert.DoesNotContain("kioskDirectInstall",JsonSerializer.Serialize(new BrowserCommandOutcome("id","devices","completed"),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("shutdown")]
    [InlineData("expiry")]
    public async Task ActualCoreTaskKeepsSourceGateAndUnknownUntilItSettles(string interruption)
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create();using var stop=new CancellationTokenSource();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Native.BeforeInstall=async _=>{entered.SetResult();await release.Task;};Task<bool>? shutdown=null;Task<BrowserCommandOutcome>? pending=null;
        try
        {
            var body=Body(f.Handle,artifact:f.Artifact,inspection:f.InspectionId);pending=f.Broker.ExecuteAsync(body,stop.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(f.Gate.IsBusy);Assert.Equal("artifact_in_use",(await Assert.ThrowsAsync<BrowserArtifactException>(()=>f.Store.RemoveAsync(f.Artifact.Id))).Code);
            Assert.Null((await Assert.ThrowsAsync<BrowserRequestException>(()=>f.Broker.ExecuteAsync(body))).Dispatched);
            var originalId=JsonDocument.Parse(body).RootElement.GetProperty("requestId").GetString()!;
            Assert.Equal("busy",(await Assert.ThrowsAsync<BrowserRequestException>(()=>f.Broker.ExecuteAsync(Read(f.Handle,originalId)))).Code);
            if(interruption=="cancel")stop.Cancel();if(interruption=="shutdown")shutdown=f.Owner.ShutdownAsync();if(interruption=="expiry")f.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.False(pending.IsCompleted);Assert.Equal(0,f.Native.Base.Closes);release.SetResult();var result=await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("outcomeUnknown",result.State);Assert.Equal("unknown",result.KioskDirectInstall!.OriginalEffect);Assert.Null(result.KioskDirectInstall.Receipt);
            Assert.Same(result,f.Broker.Get(result.RequestId));await f.Store.RemoveAsync(f.Artifact.Id);if(shutdown is not null)Assert.True(await shutdown);
        }
        finally{release.TrySetResult();if(pending is not null)await pending;}
    }

    [Fact]
    public async Task StatusRejectsForeignConnectionAndHostAndOriginalCachedGetNeedsNoProvider()
    {
        if(!OperatingSystem.IsWindows())return;await using var f=await Fixture.Create();var original=await f.Broker.ExecuteAsync(Body(f.Handle,artifact:f.Artifact,inspection:f.InspectionId));var before=f.Native.Calls.Count;
        Assert.Same(original,f.Broker.Get(original.RequestId));Assert.Equal(before,f.Native.Calls.Count);
        await Assert.ThrowsAsync<BrowserRequestException>(()=>f.Broker.ExecuteAsync(Read(Guid.NewGuid().ToString("D"),original.RequestId)));
        var foreign=new BrowserCommands((_,_)=>throw new InvalidOperationException(),operationGate:f.Gate,directConnections:f.Owner);
        Assert.Equal("direct_install_original_unavailable",(await Assert.ThrowsAsync<BrowserRequestException>(()=>foreign.ExecuteAsync(Read(f.Handle,original.RequestId)))).Code);Assert.Equal(before,f.Native.Calls.Count);
    }

    private static void AssertSource(Fixture f,BrowserDirectInstallOutcome d,string original)
    {
        Assert.Equal(f.Handle,d.ConnectionId);Assert.Equal(f.Connection.ConnectRequestId,d.ConnectRequestId);Assert.Equal(f.Connection.UsbOperationId,d.UsbOperationId);Assert.Equal("USB_TARGET",d.Serial);Assert.Equal("stable",d.ProductChannel);
        Assert.Equal(original,d.OriginalRequestId);Assert.Equal("install_"+Guid.Parse(original).ToString("N"),d.InstallRequestId);Assert.Equal(f.InspectionId,d.InspectionRequestId);
        Assert.Equal(f.Artifact.Id,d.ArtifactId);Assert.Equal(f.Artifact.Sha256,d.Sha256);Assert.Equal(f.Artifact.SizeBytes,d.SizeBytes);Assert.Equal("com.example.app",d.PackageName);Assert.Equal(1,d.VersionCode);Assert.Equal(new string('a',64),d.SignerSha256);
    }

    private sealed class Fixture:IAsyncDisposable
    {
        internal readonly BrowserDirectConnectionsTests.Clock Clock=new();internal readonly BrowserOperationGate Gate=new();internal BrowserArtifactStore Store=null!;internal BrowserArtifactReceipt Artifact=null!;
        internal BrowserDirectConnections Owner=null!;internal BrowserCommands Broker=null!;internal NativeSession Native=null!;internal BrowserDirectConnectionOutcome Connection=null!;
        internal string InspectionId=null!;internal int Inspections;internal bool FailInspection;internal ApkArtifactIdentity Identity=new("com.example.app",1,"private-version",new string('a',64),null);private string _root=null!;internal string Handle=>Connection.ConnectionId!;
        internal static async Task<Fixture> Create(string fileName="source.apk")
        {
            var f=new Fixture();f._root=Path.Combine(Path.GetTempPath(),"QfmDirectInstall-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(f._root);f.Store=new(f._root,new());f.Artifact=await f.Store.StageAsync(fileName,new MemoryStream([1,2,3,4]));
            f.Owner=new((context,_)=>Task.FromResult<IBrowserDirectSession>(f.Native=new(context,f.Clock)),f.Clock);
            f.Broker=new((command,_)=>{f.Inspections++;if(f.FailInspection)throw new InvalidDataException("private-sentinel");return Task.FromResult(new OperatorExecutionResult(command,ApkArtifactInspection:new(command.LocalPath!,f.Artifact.SizeBytes,f.Artifact.Sha256,f.Identity)));},()=>f.Store,f.Gate,directConnections:f.Owner);
            f.Connection=(await f.Broker.ExecuteAsync(BrowserDirectCommandsTests.Body(id:Guid.NewGuid().ToString("D")))).KioskDirectConnection!;await f.Inspect();return f;
        }
        internal async Task Inspect(){InspectionId=Guid.NewGuid().ToString("D");await Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new{requestId=InspectionId,action="apk.inspect",artifactId=Artifact.Id,sha256=Artifact.Sha256,sizeBytes=Artifact.SizeBytes}));}
        public async ValueTask DisposeAsync(){await Owner.ShutdownAsync();Store.Dispose();Directory.Delete(_root,true);}
    }

    private sealed class NativeSession(BrowserDirectConnectContext context,BrowserDirectConnectionsTests.Clock clock):IBrowserDirectSession
    {
        internal readonly BrowserDirectConnectionsTests.FakeSession Base=new(context,clock);internal readonly List<KioskDirectOperatorCommand> Calls=[];
        internal string State="pending-wearer-confirmation";internal bool Completed,ThrowInstall,Allowed=true;internal int? Session=7;internal OperatorMutationStage Stage=OperatorMutationStage.PendingWearerAction;
        internal string? Variant,PackageName;internal Stream? UnexpectedStream;internal Func<CancellationToken,Task>? BeforeInstall;
        public RustyKioskUsbDirectLinkReceipt Receipt=>Base.Receipt;
        public async Task<KioskDirectOperatorResult> ExecuteAsync(KioskDirectOperatorCommand command,CancellationToken token)
        {
            Calls.Add(command);
            if(command.Action is not(KioskDirectOperatorAction.Install or KioskDirectOperatorAction.InstallStatus))
            {var value=await Base.ExecuteAsync(command,token);return command.Action==KioskDirectOperatorAction.Status?value with{Status=value.Status! with{InstallerAllowed=Allowed}}:value;}
            if(command.Action==KioskDirectOperatorAction.Install){if(BeforeInstall is not null)await BeforeInstall(token);if(ThrowInstall)throw new IOException("private-sentinel");}
            var receipt=new RustyKioskDirectInstallReceipt(command.RequestId!,State,Completed,"private-sentinel",Session,PackageName);
            var result=new KioskDirectOperatorResult(command,new(command.ActionId,command.Action,Stage,"private-sentinel",command.RequestId),InstallReceipt:receipt);
            result=Variant switch
            {
                "state"=>result with{InstallReceipt=receipt with{State="unsupported"}},"completed"=>result with{InstallReceipt=receipt with{Completed=true}},
                "session"=>result with{InstallReceipt=receipt with{SessionId=-1}},"package"=>result with{InstallReceipt=receipt with{PackageName="com.other.app"}},"request"=>result with{InstallReceipt=receipt with{RequestId="install_crossed"}},
                "command"=>result with{Command=command with{OperatorConfirmed=false}},"marker-id"=>result with{Mutation=result.Mutation with{ActionId="crossed"}},
                "marker-action"=>result with{Mutation=result.Mutation with{Action=KioskDirectOperatorAction.InstallStatus}},"marker-request"=>result with{Mutation=result.Mutation with{RequestId="install_crossed"}},
                "marker-stage"=>result with{Mutation=result.Mutation with{Stage=(OperatorMutationStage)999}},
                "extra-tags"=>result with{TagExport=KioskDirectTagExportLease.Create(Encoding.UTF8.GetBytes("{\"schema\":\"rusty.kiosk.app_tags.v1\",\"apps\":[]}"))},
                "initial-synthetic"=>result with{InstallReceipt=receipt with{State="pending",SessionId=null},Mutation=result.Mutation with{Stage=OperatorMutationStage.Pending}},_=>result
            };UnexpectedStream=result.TagExport?.Stream;return result;
        }
        public Task<RustyKioskUsbDirectCleanupReceipt> CloseAsync()=>Base.CloseAsync();public void Revoke()=>Base.Revoke();
    }
}
