using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;
using Xunit;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserArtifactBrokerTests
{
    [Fact]
    public async Task StagedInspectionUsesSameCoreFactoryAndOneUseRegistryWhileRetainingBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("sample.apk", new MemoryStream([1, 2, 3]));
        OperatorCommand? observed = null;
        var calls = 0;
        var commands = new BrowserCommands((command, _) =>
        {
            calls++; observed = command;
            Assert.Throws<IOException>(() => File.Open(command.LocalPath!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            return Task.FromResult(new OperatorExecutionResult(command, ApkArtifactInspection: new(
                command.LocalPath!, receipt.SizeBytes, receipt.Sha256, new("example.fixture",1,"1",new string('c',64),null))));
        }, () => store);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { requestId=Guid.NewGuid().ToString("D"),action="apk.inspect",
            artifactId=receipt.Id,sha256=receipt.Sha256,sizeBytes=receipt.SizeBytes });
        var outcome = await commands.ExecuteAsync(request);
        Assert.Equal("completed",outcome.State); Assert.Equal(1,calls);
        Assert.Equal(OperatorCommandKind.InspectApk,observed!.Kind);
        Assert.Equal(OperatorCommands.InspectApk(observed.LocalPath!).CliArguments,observed.CliArguments);
        Assert.EndsWith(".apk",observed.LocalPath,StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(outcome.ArtifactInspection);Assert.Null(outcome.Result);
        Assert.Equal(receipt.FileName,outcome.ArtifactInspection.Inspection.Path);
        Assert.DoesNotContain(observed.LocalPath!,JsonSerializer.Serialize(outcome),StringComparison.Ordinal);
        Assert.Equal("request_already_consumed",(await Assert.ThrowsAsync<BrowserRequestException>(()=>commands.ExecuteAsync(request))).Code);
        await store.RemoveAsync(receipt.Id);
        Assert.Equal("request_already_consumed",(await Assert.ThrowsAsync<BrowserRequestException>(()=>commands.ExecuteAsync(request))).Code);
    }

    [Fact]
    public async Task ChangedBindingCannotReachCoreAndUnsupportedStoreIsExplicit()
    {
        var requests = JsonSerializer.SerializeToUtf8Bytes(new {requestId=Guid.NewGuid().ToString("D"),action="apk.inspect",
            artifactId=Guid.NewGuid().ToString("N"),sha256=new string('a',64),sizeBytes=3});
        var commands = new BrowserCommands((_,_)=>throw new InvalidOperationException("Core must not execute"));
        var rejected = await Assert.ThrowsAsync<BrowserRequestException>(()=>commands.ExecuteAsync(requests));
        Assert.Equal("artifact_staging_unavailable",rejected.Code);Assert.False(rejected.Dispatched);
        if (!OperatingSystem.IsWindows()) return;
        using var store=new BrowserArtifactStore();var receipt=await store.StageAsync("sample.apk",new MemoryStream([1,2,3]));
        commands=new BrowserCommands((_,_)=>throw new InvalidOperationException("Core must not execute"),()=>store);
        requests=JsonSerializer.SerializeToUtf8Bytes(new {requestId=Guid.NewGuid().ToString("D"),action="apk.inspect",
            artifactId=receipt.Id,sha256=new string('a',64),sizeBytes=receipt.SizeBytes});
        rejected=await Assert.ThrowsAsync<BrowserRequestException>(()=>commands.ExecuteAsync(requests));
        Assert.False(rejected.Dispatched);
    }
}
