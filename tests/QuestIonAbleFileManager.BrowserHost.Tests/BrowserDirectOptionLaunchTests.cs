using System.Text.Json;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectOptionLaunchTests
{
    private const string Opaque="  opaque\toption\n  ";
    private static ConcurrentDictionary<string,BrowserCommandOutcome> Ledger(BrowserCommands b) => (ConcurrentDictionary<string,BrowserCommandOutcome>)typeof(BrowserCommands).GetField("_requests",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(b)!;
    private static byte[] Launch(string handle,string source,string? value=Opaque,string? id=null) => JsonSerializer.SerializeToUtf8Bytes(new
    { requestId=id ?? Guid.NewGuid().ToString("D"),action="kiosk.direct.invoke",connectionId=handle,command="launch-option",value,confirmed=true,optionsRequestId=source });
    private static RustyKioskLaunchOptionsBinding Binding()
    {
        var b=new RustyKioskLaunchOptionsBinding("com.example.current",10001,new string('a',64),42,123456,"authority","provider","activity","");
        var text=string.Join('\0',RustyKioskContract.AppLaunchOptionsSchema,b.PackageName,b.Uid.ToString(CultureInfo.InvariantCulture),b.SigningIdentity,
            b.LastUpdateTimeMilliseconds.ToString(CultureInfo.InvariantCulture),b.VersionCode.ToString(CultureInfo.InvariantCulture),b.ProviderAuthority,b.ProviderClass,b.OwnerActivity);
        return b with { BindingSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() };
    }
    private static KioskDirectOperatorResult Valid(KioskDirectOperatorCommand command,KioskDirectOperatorResult result)
    {
        if(result.KioskResult is not { } typed)return result;
        var state=typed.Command==RustyKioskCommand.Select ? typed.State with { SelectedLaunchOptionsStatus=RustyKioskLaunchOptionsStatus.Ready,
            SelectedLaunchOptionsBinding=Binding(),SelectedLaunchOptions=[new(1,Opaque,"label",""),new(1,"\ufeff","FEFF","")] } : typed.State with
            { SelectedKey=null,SelectedInstalled=false,SelectedLaunchable=false,Entries=[],LastDispatchedOptionId=command.Value,LastDispatchedOptionPackage="com.example.current" };
        typed=typed with { State=state };
        return result with { KioskResult=typed,Mutation=result.Mutation with { Stage=RustyKioskReadback.Confirms(typed.Command,command.Value,typed)?OperatorMutationStage.Confirmed:OperatorMutationStage.PendingWearerAction } };
    }
    private static async Task<(BrowserDirectCommandBrokerTests.Fixture F,BrowserCommandOutcome Catalog,BrowserCommandOutcome Source)> Create(bool recovered=false)
    {
        var f=await BrowserDirectCommandBrokerTests.Fixture.Create(execute:(command,_)=>
        {
            var reply=BrowserKioskBrokerTests.Reply(command);var facts=reply.RustyKioskAdbResult!;var typed=facts.Result!;
            return Task.FromResult(reply with { RustyKioskAdbResult=facts with { Result=typed with { State=typed.State with { Entries=[new("selected-key","App","com.example.current",true,true,[])] } } } });
        });
        var catalog=await f.Broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open",serial:"USB_TARGET",channel:"stable"));
        f.Native.Transform=(command,result)=>recovered && command.Action==KioskDirectOperatorAction.Invoke ? throw new IOException("lost Select reply") : Valid(command,result);
        var original=await f.Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new
        { requestId=Guid.NewGuid().ToString("D"),action="kiosk.direct.invoke",connectionId=f.Handle,command="select",value="selected-key",confirmed=true,catalogRequestId=catalog.RequestId }));
        var source=recovered ? await f.Recover("kiosk.direct.request-result",original.RequestId) : original;
        f.Native.Transform=Valid;Assert.True(source.KioskDirectCommand!.ReconcilesOriginal);
        return (f,catalog,source);
    }
    [Fact]
    public void LaunchOptionAcceptsReviewedReturnedOptionsEnvelopeAndPreservesExactOpaqueId()
    {
        const string opaque="  opaque\toption\n  ";
        var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(BrowserDirectCommandTests.Body(command:"launch-option",value:opaque))!;
        fields["optionsRequestId"]=JsonSerializer.SerializeToElement(BrowserDirectCommandTests.Original);
        var parsed=BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields));
        Assert.Equal(RustyKioskCommand.LaunchOption,parsed.Command);
        Assert.Equal(opaque,parsed.Value);
        Assert.Equal(opaque,KioskDirectOperatorCommand.Invoke(parsed.Command!.Value,parsed.Value,true,parsed.LogicalRequestId).Value);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ExactReturnedIdUsesOneLaunchAndRetainsOuterRecoveredSourceSeparateFromUnknownOriginal(bool recovered)
    {
        var (f,catalog,source)=await Create(recovered);
        try
        {
            var selected=source.KioskDirectCommand!;var result=await f.Broker.ExecuteAsync(Launch(f.Handle,source.RequestId));var d=result.KioskDirectCommand!;
            Assert.Equal("completed",result.State);Assert.True(d.ReconcilesOriginal);Assert.Equal(Opaque,d.Value);Assert.Null(d.SelectionReview);
            Assert.Equal(new BrowserDirectLaunchReview(source.RequestId,selected.OriginalRequestId,catalog.RequestId,"selected-key","com.example.current",Binding().BindingSha256),d.LaunchReview);
            Assert.Equal(Opaque,d.TypedResult!.LastDispatchedOptionId);Assert.Equal("com.example.current",d.TypedResult.LastDispatchedOptionPackage);
            Assert.False(d.TypedResult.SelectedInstalled);Assert.False(d.TypedResult.SelectedLaunchable);Assert.Null(d.TypedResult.SelectedEntry);
            if(recovered) { Assert.NotEqual(source.RequestId,selected.OriginalRequestId);Assert.Equal("outcomeUnknown",f.Broker.Get(selected.OriginalRequestId).State); }
            Assert.Single(f.Native.Core,item=>item.Action==KioskDirectOperatorAction.Invoke && item.KioskCommand==RustyKioskCommand.LaunchOption);
            // Neither a missing source nor a later catalog retirement reopens launch recovery admission.
            Ledger(f.Broker).TryRemove(source.RequestId,out _);Ledger(f.Broker).TryRemove(catalog.RequestId,out _);
            foreach(var action in new[]{"kiosk.direct.request-status","kiosk.direct.request-result","kiosk.direct.request-cancel"})
                Assert.Equal(d.LaunchReview,(await f.Recover(action,result.RequestId)).KioskDirectCommand!.LaunchReview);
            Assert.Same(result,f.Broker.Get(result.RequestId));
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("missing")][InlineData("outer-id")][InlineData("action")][InlineData("state")][InlineData("operation")][InlineData("original-id")]
    [InlineData("logical-id")][InlineData("effect")][InlineData("reconciles")][InlineData("cancelled")][InlineData("lifecycle")]
    [InlineData("command")][InlineData("value")][InlineData("selection")][InlineData("selected-key")][InlineData("selected-package")]
    [InlineData("installed")][InlineData("launchable")][InlineData("row")][InlineData("row-package")][InlineData("row-selectable")]
    [InlineData("receipt-id")][InlineData("receipt-action")][InlineData("receipt-request")][InlineData("receipt-stage")]
    [InlineData("typed-schema")][InlineData("typed-request")][InlineData("typed-command")][InlineData("accepted")][InlineData("completed")]
    [InlineData("options-null")][InlineData("options-state")][InlineData("options-package")][InlineData("options-digest")][InlineData("options-empty")]
    [InlineData("option-duplicate")][InlineData("option-schema")][InlineData("option-blank")][InlineData("option-label")][InlineData("option-description")]
    [InlineData("serial")][InlineData("channel")][InlineData("connect")][InlineData("original-missing")][InlineData("original-metadata")]
    public async Task InvalidCachedSourceCannotTouchNativeConsumeIdOrHoldGate(string variant)
    {
        var (f,_,source)=await Create(true);
        try
        {
            var d=source.KioskDirectCommand!;var t=d.TypedResult!;var o=t.LaunchOptions!;var row=o.Options[0];var r=d.CoreReceipt!;var ledger=Ledger(f.Broker);
            var changed=variant switch
            {
                "outer-id"=>source with { RequestId=Guid.NewGuid().ToString("D") },"action"=>source with { Action="kiosk.direct.request-status" },"state"=>source with { State="pending" },
                "operation"=>source with { KioskDirectCommand=d with { Operation="requestCancel" } },"original-id"=>source with { KioskDirectCommand=d with { OriginalRequestId=Guid.NewGuid().ToString("D") } },
                "logical-id"=>source with { KioskDirectCommand=d with { LogicalRequestId="kiosk_crossed" } },"effect"=>source with { KioskDirectCommand=d with { OriginalEffect="unknown" } },
                "reconciles"=>source with { KioskDirectCommand=d with { ReconcilesOriginal=false } },"cancelled"=>source with { KioskDirectCommand=d with { CancellationAcknowledged=true } },
                "lifecycle"=>source with { KioskDirectCommand=d with { Lifecycle=new(d.LogicalRequestId,"confirmed",true,true,1,2) } },"command"=>source with { KioskDirectCommand=d with { Command="status" } },
                "value"=>source with { KioskDirectCommand=d with { Value="other" } },"selection"=>source with { KioskDirectCommand=d with { SelectionReview=null } },
                "selected-key"=>source with { KioskDirectCommand=d with { TypedResult=t with { SelectedKey="other" } } },"selected-package"=>source with { KioskDirectCommand=d with { TypedResult=t with { SelectedPackage="com.example.other" } } },
                "installed"=>source with { KioskDirectCommand=d with { TypedResult=t with { SelectedInstalled=false } } },"launchable"=>source with { KioskDirectCommand=d with { TypedResult=t with { SelectedLaunchable=false } } },
                "row"=>source with { KioskDirectCommand=d with { TypedResult=t with { SelectedEntry=null } } },"row-package"=>source with { KioskDirectCommand=d with { TypedResult=t with { SelectedEntry=t.SelectedEntry! with { PackageName="com.example.other" } } } },
                "row-selectable"=>source with { KioskDirectCommand=d with { TypedResult=t with { SelectedEntry=t.SelectedEntry! with { Selectable=false } } } },
                "receipt-id"=>source with { KioskDirectCommand=d with { CoreReceipt=r with { ActionId="action_crossed" } } },"receipt-action"=>source with { KioskDirectCommand=d with { CoreReceipt=r with { Action=KioskDirectOperatorAction.Invoke } } },
                "receipt-request"=>source with { KioskDirectCommand=d with { CoreReceipt=r with { RequestId="kiosk_crossed" } } },"receipt-stage"=>source with { KioskDirectCommand=d with { CoreReceipt=r with { Stage=OperatorMutationStage.PendingWearerAction } } },
                "typed-schema"=>source with { KioskDirectCommand=d with { TypedResult=t with { Schema="other" } } },"typed-request"=>source with { KioskDirectCommand=d with { TypedResult=t with { RequestId="kiosk_crossed" } } },
                "typed-command"=>source with { KioskDirectCommand=d with { TypedResult=t with { Command="status" } } },"accepted"=>source with { KioskDirectCommand=d with { TypedResult=t with { Accepted=false } } },"completed"=>source with { KioskDirectCommand=d with { TypedResult=t with { Completed=false } } },
                "options-null"=>source with { KioskDirectCommand=d with { TypedResult=t with { LaunchOptions=null } } },
                _=>source
            };
            var badOptions=variant switch
            {
                "options-state"=>o with { State="unavailable" },"options-package"=>o with { PackageName="com.example.other" },"options-digest"=>o with { BindingSha256="bad" },"options-empty"=>o with { Options=[] },
                "option-duplicate"=>o with { Options=[row,row] },"option-schema"=>o with { Options=[row with { SchemaVersion=2 }] },"option-blank"=>o with { Options=[row with { OptionId="\u0085" }] },
                "option-label"=>o with { Options=[row with { DisplayLabel=new('a',97) }] },"option-description"=>o with { Options=[row with { Description=new('a',161) }] },_=>null
            };
            if(badOptions is not null)changed=source with { KioskDirectCommand=d with { TypedResult=t with { LaunchOptions=badOptions } } };
            var original=ledger[d.OriginalRequestId];
            if(variant is "serial" or "channel" or "connect")
            {
                var serial=variant=="serial"?"OTHER_TARGET":d.Serial;var channel=variant=="channel"?"labs":d.ProductChannel;var connect=variant=="connect"?Guid.NewGuid().ToString("D"):d.ConnectRequestId;
                var selection=d.SelectionReview! with { Serial=serial,ProductChannel=channel };
                var scoped=d with { Serial=serial,ProductChannel=channel,ConnectRequestId=connect,UsbOperationId="usb_"+Guid.Parse(connect).ToString("N"),SelectionReview=selection };
                changed=source with { KioskDirectCommand=scoped };ledger[d.OriginalRequestId]=original with { KioskDirectCommand=original.KioskDirectCommand! with
                    { Serial=serial,ProductChannel=channel,ConnectRequestId=connect,UsbOperationId=scoped.UsbOperationId,SelectionReview=selection } };
            }
            if(variant=="original-missing")ledger.TryRemove(d.OriginalRequestId,out _);
            if(variant=="original-metadata")ledger[d.OriginalRequestId]=original with { KioskDirectCommand=original.KioskDirectCommand! with { Value="crossed" } };
            ledger[source.RequestId]=changed;var before=f.Native.Core.Count;var id=Guid.NewGuid().ToString("D");
            var error=await Assert.ThrowsAsync<BrowserRequestException>(()=>f.Broker.ExecuteAsync(Launch(f.Handle,variant=="missing"?Guid.NewGuid().ToString("D"):source.RequestId,id:id)));
            Assert.False(error.Dispatched);Assert.Equal(before,f.Native.Core.Count);Assert.False(f.Gate.IsBusy);Assert.False(ledger.ContainsKey(id));
            ledger[source.RequestId]=source;ledger[d.OriginalRequestId]=original;Assert.True((await f.Broker.ExecuteAsync(Launch(f.Handle,source.RequestId,id:id))).KioskDirectCommand!.ReconcilesOriginal);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("id",OperatorMutationStage.PendingWearerAction)][InlineData("package",OperatorMutationStage.Confirmed)]
    [InlineData("selected-package",OperatorMutationStage.PendingWearerAction)][InlineData("missing-package",OperatorMutationStage.PendingWearerAction)]
    [InlineData("malformed-id",OperatorMutationStage.PendingWearerAction)]
    public async Task ActualDispatchDriftRetainsCoreStageAndUnknownReview(string variant,OperatorMutationStage stage)
    {
        var (f,_,source)=await Create();
        try
        {
            f.Native.Transform=(command,result)=>
            {
                result=Valid(command,result);var t=result.KioskResult!;var s=t.State;
                s=variant switch { "id"=>s with { LastDispatchedOptionId="other" },"package"=>s with { SelectedPackage="com.example.other",LastDispatchedOptionPackage="com.example.other" },
                    "selected-package"=>s with { SelectedPackage="com.example.other" },"missing-package"=>s with { LastDispatchedOptionPackage=null },_=>s with { LastDispatchedOptionId="\ud800" } };
                t=t with { State=s };return result with { KioskResult=t,Mutation=result.Mutation with { Stage=RustyKioskReadback.Confirms(t.Command,command.Value,t)?OperatorMutationStage.Confirmed:OperatorMutationStage.PendingWearerAction } };
            };
            var result=await f.Broker.ExecuteAsync(Launch(f.Handle,source.RequestId));var d=result.KioskDirectCommand!;
            Assert.Equal("outcomeUnknown",result.State);Assert.Equal("unknown",d.OriginalEffect);Assert.False(d.ReconcilesOriginal);Assert.Equal(stage,d.CoreReceipt!.Stage);Assert.NotNull(d.LaunchReview);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("\ufeff",true)][InlineData("  opaque\toption\n  ",true)][InlineData("\u0085",false)][InlineData("",false)][InlineData(null,false)]
    public void OpaqueValuesUseCoreNonblankSemanticsAndCommandScopedFields(string? value,bool valid)
    {
        var body=Launch(BrowserDirectCommandTests.Handle,BrowserDirectCommandTests.Original,value);
        if(valid)Assert.Equal(value,BrowserDirectCommands.Parse(body).Value);else Assert.Throws<BrowserRequestException>(()=>BrowserDirectCommands.Parse(body));
        var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(body)!;fields["command"]=JsonSerializer.SerializeToElement("status");Assert.Throws<BrowserRequestException>(()=>BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
    }
}
