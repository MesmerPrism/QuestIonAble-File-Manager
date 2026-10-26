using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserTransferBrokerTests
{
    private static byte[] Pull(string id) => JsonSerializer.SerializeToUtf8Bytes(new
    {requestId=id,action="files.pull",serial="QUEST-FIXTURE",fileName="sample.bin",remotePath="/sdcard/sample.bin"});
    private static byte[] Devices(string id) => JsonSerializer.SerializeToUtf8Bytes(new {requestId=id,action="devices"});
    private static BrowserArtifactTransferOutcome Capture(BrowserTransferRequest request) =>
        new(new(new string('a',32),request.FileName,3,new string('b',64)),request.Serial,request.RemotePath);

    [Fact]
    public async Task ReadCaptureConsumesTheSameRegistryAndGateAsOrdinaryCommands()
    {
        var gate = new BrowserOperationGate(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var captures=0;
        var commands = new BrowserCommands((command,_)=>Task.FromResult(new OperatorExecutionResult(command)),operationGate:gate,
            capture:async(request,_)=>{captures++;entered.SetResult();await finished.Task;return Capture(request);});
        var id=Guid.NewGuid().ToString("D"); var running=commands.ExecuteAsync(Pull(id));
        await Task.WhenAny(entered.Task, running); Assert.True(entered.Task.IsCompleted, "Closed capture action must reach its typed handler");
        Assert.True(gate.IsBusy); Assert.Equal("running",commands.Get(id).State);
        Assert.Equal("request_already_consumed",(await Assert.ThrowsAsync<BrowserRequestException>(()=>commands.ExecuteAsync(Pull(id)))).Code);
        Assert.Equal("busy",(await Assert.ThrowsAsync<BrowserRequestException>(()=>commands.ExecuteAsync(Devices(Guid.NewGuid().ToString("D"))))).Code);
        finished.SetResult();var result=await running;
        Assert.Equal("completed",result.State);Assert.NotNull(result.ArtifactTransfer);Assert.Null(result.Result);Assert.False(gate.IsBusy);
        Assert.Equal("request_already_consumed",(await Assert.ThrowsAsync<BrowserRequestException>(()=>commands.ExecuteAsync(Devices(id)))).Code);
        var ordinaryId=Guid.NewGuid().ToString("D");await commands.ExecuteAsync(Devices(ordinaryId));
        Assert.Equal("request_already_consumed",(await Assert.ThrowsAsync<BrowserRequestException>(()=>commands.ExecuteAsync(Pull(ordinaryId)))).Code);
        Assert.Equal(1,captures);
    }

    [Fact]
    public async Task InvalidCapturedScopeCannotPublishReceiptAndFailuresArePathFree()
    {
        var commands = new BrowserCommands((_,_)=>throw new InvalidOperationException(),capture:(request,_)=>Task.FromResult(Capture(request) with {Serial="OTHER"}));
        var result=await commands.ExecuteAsync(Pull(Guid.NewGuid().ToString("D")));
        Assert.Equal("failed",result.State);Assert.Null(result.ArtifactTransfer);
        commands=new BrowserCommands((_,_)=>throw new InvalidOperationException(),capture:(_,_)=>throw new IOException("C:/private/owner-key"));
        result=await commands.ExecuteAsync(Pull(Guid.NewGuid().ToString("D")));
        Assert.Equal("failed",result.State);Assert.DoesNotContain("private",JsonSerializer.Serialize(result));Assert.Null(result.ArtifactTransfer);
    }
}
