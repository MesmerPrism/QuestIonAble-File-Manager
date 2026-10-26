using System.Text;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectRunnerTests
{
    [Fact]
    public async Task OrdinaryCallsAreBoundedAndSensitiveCallsKeepReviewedParserAndLimits()
    {
        var source=new Runner();var bounded=new BrowserDirectConnections.BoundedRunner(source);using var cancellation=new CancellationTokenSource();
        var result=await bounded.RunAsync("adb",["devices","-l"],TimeSpan.FromSeconds(20),cancellation.Token);
        Assert.Equal("fixed facts",result.StandardOutput);Assert.Equal(1024*1024,source.Maximum);Assert.Equal(0,source.Ordinary);
        var sensitive=await bounded.RunSensitiveAsync("adb",["fixed-sensitive"],16384,16384,TimeSpan.FromSeconds(20),
            bytes=>Encoding.UTF8.GetString(bytes.Span),cancellation.Token);
        Assert.Equal("parsed-in-memory",sensitive.Value);Assert.Equal(1,source.Sensitive);
    }
    [Theory]
    [InlineData("size")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    [InlineData("utf8")]
    public async Task BoundedOrdinaryCallsFailClosedOnMalformedAcknowledgements(string mismatch)
    {
        var bounded=new BrowserDirectConnections.BoundedRunner(new Runner{Mismatch=mismatch});
        await Assert.ThrowsAnyAsync<Exception>(()=>bounded.RunAsync("adb",["devices","-l"],TimeSpan.FromSeconds(20)));
    }
    [Fact]
    public void RunnerWithoutBothReviewedCapabilitiesCannotActivate() => Assert.Throws<NotSupportedException>(()=>new BrowserDirectConnections.BoundedRunner(new PlainRunner()));
    private sealed class PlainRunner:ICommandRunner
    {public Task<CommandResult> RunAsync(string file,IReadOnlyList<string> args,TimeSpan timeout,CancellationToken token=default)=>throw new Exception("must not run");}
    private sealed class Runner:IStreamingCommandRunner,ISensitiveCommandRunner
    {
        public string? Mismatch;public long Maximum;public int Ordinary,Sensitive;
        public Task<CommandResult> RunAsync(string file,IReadOnlyList<string> args,TimeSpan timeout,CancellationToken token=default){Ordinary++;throw new Exception("unbounded fallback");}
        public async Task<StreamingCommandResult> RunToStreamAsync(string file,IReadOnlyList<string> args,Stream output,long maximum,TimeSpan timeout,CancellationToken token=default)
        {
            Maximum=maximum;var bytes=Mismatch=="utf8"?new byte[]{255}:Encoding.UTF8.GetBytes("fixed facts");await output.WriteAsync(bytes,token);
            return new(new(file,args,0,Mismatch=="stdout"?"unexpected":"",Mismatch=="stderr"?"unexpected":"",TimeSpan.Zero),bytes.Length+(Mismatch=="size"?1:0),"not-used");
        }
        public Task<SensitiveCommandResult<T>> RunSensitiveAsync<T>(string file,IReadOnlyList<string> args,int maxOut,int maxErr,TimeSpan timeout,Func<ReadOnlyMemory<byte>,T> parse,CancellationToken token=default)
        {
            Sensitive++;Assert.Equal(16384,maxOut);Assert.Equal(16384,maxErr);Assert.Equal(TimeSpan.FromSeconds(20),timeout);
            return Task.FromResult(new SensitiveCommandResult<T>(parse("parsed-in-memory"u8.ToArray()),0,TimeSpan.Zero));
        }
    }
}
