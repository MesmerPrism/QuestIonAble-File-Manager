using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;
using Xunit;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserOperationGateTests
{
    [Fact]
    public async Task ExternalOperationBlocksWithoutConsumingTransientRequest()
    {
        var gate=new BrowserOperationGate();var external=gate.TryAcquire();Assert.NotNull(external);
        var calls=0;
        var commands=new BrowserCommands((command,_)=>{calls++;return Task.FromResult(new OperatorExecutionResult(command));},operationGate:gate);
        var request=JsonSerializer.SerializeToUtf8Bytes(new {requestId=Guid.NewGuid().ToString("D"),action="devices"});
        Assert.Equal("busy",(await Assert.ThrowsAsync<BrowserRequestException>(()=>commands.ExecuteAsync(request))).Code);
        Assert.Equal(0,calls);external.Dispose();
        Assert.Equal("completed",(await commands.ExecuteAsync(request)).State);Assert.Equal(1,calls);Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task SeparateAdaptersShareOneActiveCoreOperation()
    {
        var gate=new BrowserOperationGate();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=new BrowserCommands(async(command,_)=>{entered.SetResult();await finish.Task;return new OperatorExecutionResult(command);},operationGate:gate);
        var second=new BrowserCommands((command,_)=>Task.FromResult(new OperatorExecutionResult(command)),operationGate:gate);
        byte[] Request()=>JsonSerializer.SerializeToUtf8Bytes(new {requestId=Guid.NewGuid().ToString("D"),action="devices"});
        var original=Request();var running=first.ExecuteAsync(original);await entered.Task;
        Assert.Equal("request_already_consumed",(await Assert.ThrowsAsync<BrowserRequestException>(()=>first.ExecuteAsync(original))).Code);
        var next=Request();Assert.Equal("busy",(await Assert.ThrowsAsync<BrowserRequestException>(()=>second.ExecuteAsync(next))).Code);
        finish.SetResult();await running;Assert.Equal("completed",(await second.ExecuteAsync(next)).State);
    }

    [Fact]
    public void OldScopeCannotReleaseItsSuccessor()
    {
        var gate=new BrowserOperationGate();var first=gate.TryAcquire()!;first.Dispose();
        using var second=gate.TryAcquire();Assert.NotNull(second);first.Dispose();Assert.True(gate.IsBusy);Assert.Null(gate.TryAcquire());
    }
}
