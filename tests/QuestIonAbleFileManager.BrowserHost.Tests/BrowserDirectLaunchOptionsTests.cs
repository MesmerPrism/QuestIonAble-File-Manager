using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectLaunchOptionsTests
{
    private static RustyKioskLaunchOptionsBinding Binding()
    {
        var b=new RustyKioskLaunchOptionsBinding("com.example.current",10001,new string('a',64),42,123456,
            "com.example.current.options","com.example.current.Provider","com.example.current.MainActivity","");
        return Sign(b);
    }
    private static RustyKioskLaunchOptionsBinding Sign(RustyKioskLaunchOptionsBinding b)
    {
        var text=string.Join('\0',RustyKioskContract.AppLaunchOptionsSchema,b.PackageName,b.Uid.ToString(CultureInfo.InvariantCulture),b.SigningIdentity,
            b.LastUpdateTimeMilliseconds.ToString(CultureInfo.InvariantCulture),b.VersionCode.ToString(CultureInfo.InvariantCulture),b.ProviderAuthority,b.ProviderClass,b.OwnerActivity);
        return b with { BindingSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() };
    }
    private static async Task<(BrowserDirectCommandBrokerTests.Fixture Fixture,BrowserCommandOutcome Catalog)> Create()
    {
        var f=await BrowserDirectCommandBrokerTests.Fixture.Create(execute:(command,_)=>
        {
            var reply=BrowserKioskBrokerTests.Reply(command);var facts=reply.RustyKioskAdbResult!;var typed=facts.Result!;
            return Task.FromResult(reply with { RustyKioskAdbResult=facts with { Result=typed with { State=typed.State with { Entries=[new("selected-key","App","com.example.current",true,true,[])] } } } });
        });
        var catalog=await f.Broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open",serial:"USB_TARGET",channel:"stable"));
        return (f,catalog);
    }
    private static Task<BrowserCommandOutcome> Select(BrowserDirectCommandBrokerTests.Fixture f,string catalog) => f.Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new
    { requestId=Guid.NewGuid().ToString("D"),action="kiosk.direct.invoke",connectionId=f.Handle,command="select",value="selected-key",confirmed=true,catalogRequestId=catalog }));

    [Fact]
    public async Task SelectProjectsOptionsReturnedByThatSameOriginalTypedResult()
    {
        var fixture=await BrowserDirectCommandBrokerTests.Fixture.Create(execute:(command,_)=>
        {
            var reply=BrowserKioskBrokerTests.Reply(command); var facts=reply.RustyKioskAdbResult!; var result=facts.Result!;
            return Task.FromResult(reply with { RustyKioskAdbResult=facts with { Result=result with { State=result.State with
                { Entries=[new("selected-key","App","com.example.current",true,true,[])] } } } });
        });
        try
        {
            var binding=new RustyKioskLaunchOptionsBinding("com.example.current",10001,new string('a',64),42,123456,
                "com.example.current.options","com.example.current.Provider","com.example.current.MainActivity","");
            var canonical=string.Join('\0',RustyKioskContract.AppLaunchOptionsSchema,binding.PackageName,
                binding.Uid.ToString(CultureInfo.InvariantCulture),binding.SigningIdentity,binding.LastUpdateTimeMilliseconds.ToString(CultureInfo.InvariantCulture),
                binding.VersionCode.ToString(CultureInfo.InvariantCulture),binding.ProviderAuthority,binding.ProviderClass,binding.OwnerActivity);
            binding=binding with { BindingSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant() };
            fixture.Native.Transform=(_,result)=>result with { KioskResult=result.KioskResult! with { State=result.KioskResult.State with
                { SelectedLaunchOptionsStatus=RustyKioskLaunchOptionsStatus.Ready,SelectedLaunchOptionsBinding=binding,
                    SelectedLaunchOptions=[new(1,"  opaque\toption  ","Example option","")] } } };
            var catalog=await fixture.Broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open",serial:"USB_TARGET",channel:"stable"));
            var outcome=await fixture.Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new
            { requestId=Guid.NewGuid().ToString("D"),action="kiosk.direct.invoke",connectionId=fixture.Handle,command="select",value="selected-key",confirmed=true,catalogRequestId=catalog.RequestId }));
            Assert.True(outcome.KioskDirectCommand!.ReconcilesOriginal);
            var json=JsonSerializer.SerializeToElement(outcome.KioskDirectCommand.TypedResult,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.True(json.TryGetProperty("launchOptions",out var options),"The same original Select typed result must retain its returned option observation.");
            Assert.Equal("ready",options.GetProperty("state").GetString());
            Assert.Equal("  opaque\toption  ",options.GetProperty("options")[0].GetProperty("optionId").GetString());
            Assert.Single(fixture.Native.Core,item=>item.Action==KioskDirectOperatorAction.Invoke);
        }
        finally { Assert.True(await fixture.Owner.ShutdownAsync()); }
    }

    [Theory]
    [InlineData("ready","ready")][InlineData("ready-empty","ready")][InlineData("none","none")][InlineData("rejected","rejected")]
    [InlineData("missing","missing")][InlineData("missing-rows","unavailable")][InlineData("missing-binding","unavailable")]
    [InlineData("none-rows","unavailable")][InlineData("rejected-binding","unavailable")][InlineData("invalid-status","unavailable")]
    [InlineData("no-binding","unavailable")][InlineData("duplicate","unavailable")][InlineData("schema","unavailable")]
    [InlineData("blank-id","unavailable")][InlineData("blank-label","unavailable")][InlineData("long-id","unavailable")]
    [InlineData("long-label","unavailable")][InlineData("long-description","unavailable")][InlineData("long-count","unavailable")]
    [InlineData("bad-id-utf16","unavailable")][InlineData("bad-label-utf16","unavailable")][InlineData("bad-description-utf16","unavailable")]
    [InlineData("null-row","unavailable")][InlineData("null-description","unavailable")][InlineData("digest","unavailable")]
    [InlineData("binding-package","unavailable")][InlineData("binding-uid","unavailable")][InlineData("binding-version","unavailable")]
    [InlineData("binding-update","unavailable")][InlineData("binding-signers","unavailable")][InlineData("binding-authority","unavailable")]
    [InlineData("binding-class","unavailable")][InlineData("binding-activity","unavailable")][InlineData("binding-utf16","unavailable")]
    public async Task OptionalProjectionConstraintsNeverEraseValidTypedSelection(string variant,string expected)
    {
        var (f,catalog)=await Create();
        try
        {
            IReadOnlyList<RustyKioskLaunchOption> rows=[new(1," \t\ufeffopaque\n ","\ufeff","")];var binding=Binding();
            RustyKioskLaunchOptionsStatus? status=RustyKioskLaunchOptionsStatus.Ready;
            switch(variant)
            {
                case "ready-empty":rows=[];break;
                case "none":status=RustyKioskLaunchOptionsStatus.None;rows=[];binding=null!;break;
                case "rejected":status=RustyKioskLaunchOptionsStatus.Rejected;rows=[];binding=null!;break;
                case "missing":status=null;rows=[];binding=null!;break;
                case "missing-rows":status=null;binding=null!;break;
                case "missing-binding":status=null;rows=[];break;
                case "none-rows":status=RustyKioskLaunchOptionsStatus.None;binding=null!;break;
                case "rejected-binding":status=RustyKioskLaunchOptionsStatus.Rejected;rows=[];break;
                case "invalid-status":status=(RustyKioskLaunchOptionsStatus)999;break;
                case "no-binding":binding=null!;break;
                case "duplicate":rows=[rows[0],rows[0]];break;
                case "schema":rows=[rows[0] with { SchemaVersion=2 }];break;
                case "blank-id":rows=[rows[0] with { OptionId="\u0085" }];break;
                case "blank-label":rows=[rows[0] with { DisplayLabel="\u0085" }];break;
                case "long-id":rows=[rows[0] with { OptionId=new('a',161) }];break;
                case "long-label":rows=[rows[0] with { DisplayLabel=new('a',97) }];break;
                case "long-description":rows=[rows[0] with { Description=new('a',161) }];break;
                case "long-count":rows=Enumerable.Range(0,65).Select(i=>new RustyKioskLaunchOption(1,"option"+i,"label","")).ToArray();break;
                case "bad-id-utf16":rows=[rows[0] with { OptionId="\ud800" }];break;
                case "bad-label-utf16":rows=[rows[0] with { DisplayLabel="\udc00" }];break;
                case "bad-description-utf16":rows=[rows[0] with { Description="\ud800" }];break;
                case "null-row":rows=[null!];break;
                case "null-description":rows=[rows[0] with { Description=null! }];break;
                case "digest":binding=binding with { BindingSha256=new('0',64) };break;
                case "binding-package":binding=Sign(binding with { PackageName="com.example.other" });break;
                case "binding-uid":binding=Sign(binding with { Uid=0 });break;
                case "binding-version":binding=Sign(binding with { VersionCode=-1 });break;
                case "binding-update":binding=Sign(binding with { LastUpdateTimeMilliseconds=0 });break;
                case "binding-signers":binding=Sign(binding with { SigningIdentity="PRIVATE-SIGNER" });break;
                case "binding-authority":binding=Sign(binding with { ProviderAuthority=new('a',256) });break;
                case "binding-class":binding=Sign(binding with { ProviderClass=" " });break;
                case "binding-activity":binding=Sign(binding with { OwnerActivity=new('a',513) });break;
                case "binding-utf16":binding=Sign(binding with { ProviderClass="\ud800" });break;
            }
            f.Native.Transform=(_,result)=>result with { KioskResult=result.KioskResult! with { State=result.KioskResult.State with
                { SelectedLaunchOptionsStatus=status,SelectedLaunchOptionsBinding=binding,SelectedLaunchOptions=rows } } };
            var outcome=await Select(f,catalog.RequestId);var d=outcome.KioskDirectCommand!;var observation=d.TypedResult!.LaunchOptions!;
            Assert.Equal("completed",outcome.State);Assert.True(d.ReconcilesOriginal);Assert.Equal(OperatorMutationStage.Confirmed,d.CoreReceipt!.Stage);
            Assert.Equal(expected,observation.State);
            if(expected=="ready") { Assert.Equal("com.example.current",observation.PackageName);Assert.Equal(binding.BindingSha256,observation.BindingSha256);Assert.Equal(rows.Select(r=>r.OptionId),observation.Options.Select(r=>r.OptionId)); }
            else { Assert.Null(observation.PackageName);Assert.Null(observation.BindingSha256);Assert.Empty(observation.Options); }
            Assert.DoesNotContain("PRIVATE-SIGNER",JsonSerializer.Serialize(observation));
            Assert.Single(f.Native.Core,item=>item.Action==KioskDirectOperatorAction.Invoke);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Fact]
    public async Task CapturesMaximumRowsAndExactOpaqueValuesWithoutFollowingOrPersistingMutableOwnerRows()
    {
        var (f,catalog)=await Create();
        try
        {
            var rows=Enumerable.Range(0,64).Select(i=>new RustyKioskLaunchOption(1,i==0?"\ufeff":i==1?"A":"a"+i,new('l',96),new('d',160))).ToList();
            rows[2]=rows[2] with { OptionId="a" };rows[3]=rows[3] with { OptionId=" \tcontrol\n " };rows[4]=rows[4] with { OptionId=new('i',160) };
            f.Native.Transform=(_,result)=>result with { KioskResult=result.KioskResult! with { State=result.KioskResult.State with
                { SelectedLaunchOptionsStatus=RustyKioskLaunchOptionsStatus.Ready,SelectedLaunchOptionsBinding=Binding(),SelectedLaunchOptions=rows } } };
            var original=await Select(f,catalog.RequestId);var observation=original.KioskDirectCommand!.TypedResult!.LaunchOptions!;
            Assert.Equal("ready",observation.State);Assert.Equal(64,observation.Options.Count);Assert.Equal(" \tcontrol\n ",observation.Options[3].OptionId);
            var json=JsonSerializer.Serialize(observation);rows.Clear();Assert.Equal(json,JsonSerializer.Serialize(f.Broker.Get(original.RequestId).KioskDirectCommand!.TypedResult!.LaunchOptions));
            var status=await f.Recover("kiosk.direct.request-status",original.RequestId);Assert.Null(status.KioskDirectCommand!.TypedResult);
            var cancel=await f.Recover("kiosk.direct.request-cancel",original.RequestId);Assert.Null(cancel.KioskDirectCommand!.TypedResult);
            var result=await f.Recover("kiosk.direct.request-result",original.RequestId);Assert.Equal("ready",result.KioskDirectCommand!.TypedResult!.LaunchOptions!.State);
            Assert.Empty(result.KioskDirectCommand.TypedResult.LaunchOptions.Options);Assert.Equal(json,JsonSerializer.Serialize(observation));
            Assert.Single(f.Native.Core,item=>item.Action==KioskDirectOperatorAction.Invoke);
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }

    [Fact]
    public async Task InvalidSelectionAndAllOldCommandsNeverProjectReturnedOptions()
    {
        var (f,catalog)=await Create();
        try
        {
            f.Native.Transform=(_,result)=>result with { KioskResult=result.KioskResult! with { State=result.KioskResult.State with
                { SelectedPackage="com.example.other",SelectedLaunchOptionsStatus=RustyKioskLaunchOptionsStatus.Ready,SelectedLaunchOptionsBinding=Binding(),SelectedLaunchOptions=[new(1,"option","label","")] } } };
            var selected=await Select(f,catalog.RequestId);Assert.False(selected.KioskDirectCommand!.ReconcilesOriginal);Assert.Null(selected.KioskDirectCommand.TypedResult!.LaunchOptions);
            foreach(var command in Enum.GetValues<RustyKioskCommand>().Where(c=>c is not (RustyKioskCommand.Select or RustyKioskCommand.FocusSearch or RustyKioskCommand.FocusTagEditor or RustyKioskCommand.LaunchOption)))
            {
                var value=command==RustyKioskCommand.SetLaunchRequirement?"any":command is RustyKioskCommand.AddTag or RustyKioskCommand.RemoveTag?"tag":null;
                var old=await f.Invoke(command.ToWireName(),value);
                Assert.DoesNotContain("\"launchOptions\"",JsonSerializer.Serialize(old,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
        }
        finally { Assert.True(await f.Owner.ShutdownAsync()); }
    }
}
