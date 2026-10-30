using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectSelectionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static byte[] Select(string handle,string catalog,string? id=null,string key="selected-key") =>
        JsonSerializer.SerializeToUtf8Bytes(new { requestId=id ?? Guid.NewGuid().ToString("D"),action="kiosk.direct.invoke",connectionId=handle,command="select",value=key,confirmed=true,catalogRequestId=catalog });
    private static ConcurrentDictionary<string,BrowserCommandOutcome> Ledger(BrowserCommands broker) =>
        (ConcurrentDictionary<string,BrowserCommandOutcome>)typeof(BrowserCommands).GetField("_requests",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(broker)!;
    private static async Task<(BrowserDirectCommandBrokerTests.Fixture Fixture,BrowserCommandOutcome Catalog)> Create()
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create(execute:(command,_) =>
        {
            var reply=BrowserKioskBrokerTests.Reply(command); var facts=reply.RustyKioskAdbResult!; var result=facts.Result!;
            return Task.FromResult(reply with { RustyKioskAdbResult=facts with { Result=result with { State=result.State with
                { Entries=[new("selected-key","App","com.example.current",true,true,[])] } } } });
        });
        var catalog=await fixture.Broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open",serial:"USB_TARGET",channel:"stable"));
        Assert.Equal("completed",catalog.State);
        return (fixture,catalog);
    }

    [Theory]
    [InlineData("status","selected-key",true)]
    [InlineData("select","selected-key",false)]
    [InlineData("select"," selected-key",true)]
    [InlineData("select","selected-key ",true)]
    [InlineData("select","",true)]
    [InlineData("select",null,true)]
    [InlineData("select","bad\nkey",true)]
    public void SelectOnlyCatalogFieldAndCanonicalKeyAreClosed(string command,string? value,bool catalog)
    {
        var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(BrowserDirectCommandTests.Body(command:command,value:value))!;
        if(catalog)fields["catalogRequestId"]=JsonSerializer.SerializeToElement(BrowserDirectCommandTests.Handle);
        Assert.Throws<BrowserRequestException>(()=>BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
    }

    [Fact]
    public void SelectKeyUsesExistingCoreTrimAndLosslessUtf16Guards()
    {
        var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(Select(BrowserDirectCommandTests.Handle,BrowserDirectCommandTests.Original,key:"\ufeffkey\ufeff"))!;
        Assert.Equal("\ufeffkey\ufeff",BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)).Value);
        fields["value"]=JsonSerializer.SerializeToElement(new string('a',161));
        Assert.Throws<BrowserRequestException>(()=>BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
    }

    [Theory]
    [InlineData("missing")][InlineData("action")][InlineData("state")][InlineData("operation")][InlineData("provider-state")]
    [InlineData("activity")][InlineData("accepted")][InlineData("completed")][InlineData("catalog-id")][InlineData("provider-id")]
    [InlineData("serial")][InlineData("channel")][InlineData("row-missing")][InlineData("duplicate")][InlineData("selectable")]
    [InlineData("installed")][InlineData("launchable")][InlineData("package")][InlineData("retired")]
    public async Task InvalidCatalogIsRejectedBeforeNativeGateOrRequestConsumption(string variant)
    {
        var (f,catalog)=await Create();
        try
        {
            var k=catalog.Kiosk!; var r=k.Result!; var row=Assert.Single(r.Entries); var ledger=Ledger(f.Broker);
            ledger[catalog.RequestId]=variant switch
            {
                "action" => catalog with { Action="kiosk.original-result" },
                "state" => catalog with { State="outcomeUnknown" },
                "operation" => catalog with { Kiosk=k with { Operation="select" } },
                "provider-state" => catalog with { Kiosk=k with { ProviderState="pending" } },
                "activity" => catalog with { Kiosk=k with { ActivityOpenRequested=false } },
                "accepted" => catalog with { Kiosk=k with { Result=r with { Accepted=false } } },
                "completed" => catalog with { Kiosk=k with { Result=r with { Completed=false } } },
                "catalog-id" => catalog with { Kiosk=k with { CatalogRequestId=Guid.NewGuid().ToString("D") } },
                "provider-id" => catalog with { Kiosk=k with { ProviderRequestId="pc-"+Guid.NewGuid().ToString("N") } },
                "serial" => catalog with { Kiosk=k with { Serial="OTHER_TARGET" } },
                "channel" => catalog with { Kiosk=k with { ProductChannel="labs" } },
                "row-missing" => catalog with { Kiosk=k with { Result=r with { Entries=[] } } },
                "duplicate" => catalog with { Kiosk=k with { Result=r with { Entries=[row,row] } } },
                "selectable" => catalog with { Kiosk=k with { Result=r with { Entries=[row with { Selectable=false }] } } },
                "installed" => catalog with { Kiosk=k with { Result=r with { Entries=[row with { Installed=false }] } } },
                "launchable" => catalog with { Kiosk=k with { Result=r with { Entries=[row with { Launchable=false }] } } },
                "package" => catalog with { Kiosk=k with { Result=r with { Entries=[row with { PackageName=null }] } } },
                _ => catalog
            };
            if(variant=="retired")
            {
                var id=Guid.NewGuid().ToString("D"); ledger[id]=new(id,"kiosk.select","completed",Kiosk:k with
                { Operation="select",ProviderState="installationOnly",ActivityOpenRequested=false,Result=null,Installation=k.Installation! with { HostOperatorAvailable=false } });
            }
            var before=f.Native.Core.Count; var request=Guid.NewGuid().ToString("D");
            var error=await Assert.ThrowsAsync<BrowserRequestException>(()=>f.Broker.ExecuteAsync(Select(f.Handle,variant=="missing"?Guid.NewGuid().ToString("D"):catalog.RequestId,request)));
            Assert.False(error.Dispatched); Assert.Equal(before,f.Native.Core.Count); Assert.False(f.Gate.IsBusy);
            Assert.Equal("request_not_found",Assert.Throws<BrowserRequestException>(()=>f.Broker.Get(request)).Code);
            ledger[catalog.RequestId]=catalog; foreach(var item in ledger.Where(item=>item.Value.Action=="kiosk.select").ToArray())ledger.TryRemove(item.Key,out _);
            Assert.Equal("completed",(await f.Broker.ExecuteAsync(Select(f.Handle,catalog.RequestId,request))).State);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Fact]
    public async Task ExactReviewPersistsAcrossOriginalCachedStatusResultAndCancelWithoutReacquiringCatalog()
    {
        var (f,catalog)=await Create();
        try
        {
            // A truncated but completed catalog retains the existing reviewed-row rule.
            Ledger(f.Broker)[catalog.RequestId]=catalog with { Kiosk=catalog.Kiosk! with { Result=catalog.Kiosk.Result! with { VisibleEntriesTruncated=true,ProjectionTruncated=true } } };
            var original=await f.Broker.ExecuteAsync(Select(f.Handle,catalog.RequestId)); var d=original.KioskDirectCommand!;
            var review=new BrowserDirectSelectionReview(catalog.RequestId,"USB_TARGET","stable","selected-key","com.example.current");
            Assert.Equal(review,d.SelectionReview); Assert.True(d.ReconcilesOriginal); Assert.Equal("com.example.current",d.TypedResult!.SelectedEntry!.PackageName);
            Assert.Same(original,f.Broker.Get(original.RequestId));
            Ledger(f.Broker).TryRemove(catalog.RequestId,out _);
            foreach(var action in new[]{"kiosk.direct.request-status","kiosk.direct.request-result","kiosk.direct.request-cancel"})
            {
                var recovered=await f.Recover(action,original.RequestId); Assert.Equal(review,recovered.KioskDirectCommand!.SelectionReview);
                if(action.EndsWith("request-result",StringComparison.Ordinal))Assert.True(recovered.KioskDirectCommand.ReconcilesOriginal);
            }
            Assert.Single(f.Native.Core,item=>item.Action==KioskDirectOperatorAction.Invoke);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("package")][InlineData("key")][InlineData("installed")][InlineData("launchable")][InlineData("row-package")]
    [InlineData("row-installed")][InlineData("row-launchable")][InlineData("row-missing")][InlineData("row-duplicate")]
    public async Task SelectDriftRetainsActualCoreReceiptAndUnknownOriginal(string variant)
    {
        var (f,catalog)=await Create();
        try
        {
            f.Native.Transform=(_,result)=>
            {
                var t=result.KioskResult!; var s=t.State; var row=Assert.Single(s.Entries);
                s=variant switch
                {
                    "package"=>s with { SelectedPackage="com.example.other" }, "key"=>s with { SelectedKey="other-key" },
                    "installed"=>s with { SelectedInstalled=false }, "launchable"=>s with { SelectedLaunchable=false },
                    "row-package"=>s with { Entries=[row with { PackageName="com.example.other" }] },
                    "row-installed"=>s with { Entries=[row with { Installed=false }] },
                    "row-launchable"=>s with { Entries=[row with { Launchable=false }] },
                    "row-missing"=>s with { Entries=[] }, _=>s with { Entries=[row,row] }
                };
                t=t with { State=s };
                return result with { KioskResult=t,Mutation=result.Mutation with { Stage=RustyKioskReadback.Confirms(t.Command,"selected-key",t)?OperatorMutationStage.Confirmed:OperatorMutationStage.PendingWearerAction } };
            };
            var outcome=await f.Broker.ExecuteAsync(Select(f.Handle,catalog.RequestId)); var facts=outcome.KioskDirectCommand!;
            Assert.Equal("outcomeUnknown",outcome.State); Assert.Equal("unknown",facts.OriginalEffect); Assert.False(facts.ReconcilesOriginal);
            Assert.Equal(variant=="key"?OperatorMutationStage.PendingWearerAction:OperatorMutationStage.Confirmed,facts.CoreReceipt!.Stage);
            Assert.NotNull(facts.SelectionReview); Assert.NotNull(facts.TypedResult);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Fact]
    public async Task OldCommandAndSelectedRowWireOmitAllSelectFacts()
    {
        var f=await BrowserDirectCommandBrokerTests.Fixture.Create();
        try
        {
            var outcome=await f.Invoke(); var json=JsonSerializer.Serialize(outcome.KioskDirectCommand,Web);
            foreach(var field in new[]{"selectionReview","catalogRequestId","selectable","packageName","installed","launchable"})Assert.DoesNotContain("\""+field+"\"",json);
            var row=new BrowserDirectSelectedEntry("key","any"); var (key,requirement)=row; Assert.Equal("key",key);Assert.Equal("any",requirement);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }
}
