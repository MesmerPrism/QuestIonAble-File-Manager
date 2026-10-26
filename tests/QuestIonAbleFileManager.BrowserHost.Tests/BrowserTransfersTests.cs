using System.Security.Cryptography;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserTransfersTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    private static BrowserTransferRequest Request(string action = "files.pull") => new(Guid.NewGuid().ToString("D"), action,
        "QUEST-FIXTURE", action == "files.pull" ? "fixture.bin" : "fixture.apk", action == "files.pull" ? "/sdcard/fixture.bin" : null,
        action == "apk.export" ? "example.fixture" : null);
    private static StreamingCommandResult Evidence(string hash) => new(new("private-adb-path", ["private-source"], 0, "", "", TimeSpan.Zero), Bytes.Length, hash);
    private static BrowserTransferCalls Calls(Func<CancellationToken, Task<IReadOnlyList<QuestDevice>>>? discover = null,
        bool wrongHash = false, bool split = false) => new(
        discover ?? (_ => Task.FromResult<IReadOnlyList<QuestDevice>>([new("QUEST-FIXTURE", "device", "Fixture", null)])),
        async (serial, path, stream, maximum, cancellation) =>
        { Assert.Equal("QUEST-FIXTURE", serial); Assert.Equal("/sdcard/fixture.bin", path); Assert.Equal(BrowserArtifactCommands.MaximumArtifactBytes, maximum); await stream.WriteAsync(Bytes, cancellation); return Evidence(wrongHash ? new string('f',64) : Hash); },
        async (serial, package, stream, maximum, cancellation) =>
        { Assert.Equal("QUEST-FIXTURE", serial); Assert.Equal("example.fixture", package); if(split)throw new SplitPackageException(package,["/data/app/base.apk","/data/app/split.apk"]); await stream.WriteAsync(Bytes,cancellation); return new(package,"/data/app/fixture/base.apk",Evidence(Hash)); });

    [Theory]
    [InlineData("files.pull")]
    [InlineData("apk.export")]
    public async Task CapturesOnlyCompleteExactCoreBytesAsOpaquePathFreeReceipts(string action)
    {
        if(!OperatingSystem.IsWindows())return;
        using var store=new BrowserArtifactStore();var transfer=new BrowserTransfers(()=>store,()=>Calls());var request=Request(action);
        var result=await transfer.ExecuteAsync(request,CancellationToken.None);
        Assert.Equal(Hash,result.Artifact.Sha256);Assert.Equal(Bytes.Length,result.Artifact.SizeBytes);Assert.Equal(request.Serial,result.Serial);
        await using(var lease=await store.AcquireAsync(result.Artifact.Id,Hash,Bytes.Length))
        {var actual=new MemoryStream();await lease.Stream.CopyToAsync(actual);Assert.Equal(Bytes,actual.ToArray());}
        Assert.DoesNotContain("private-adb-path",JsonSerializer.Serialize(result));Assert.DoesNotContain("private-source",JsonSerializer.Serialize(result));
        Assert.Equal(action=="apk.export"?$"{Hash}  fixture.apk\n":null,result.ChecksumText);
        await store.RemoveAsync(result.Artifact.Id);
    }

    [Fact]
    public async Task TargetReadinessPrecedesOpeningTheStoreAndWrongBytesPublishNoSuccess()
    {
        var absent=new BrowserTransfers(()=>throw new InvalidOperationException("Store must remain unopened"),
            ()=>Calls(_=>Task.FromResult<IReadOnlyList<QuestDevice>>([])));
        var error=await Assert.ThrowsAsync<BrowserRequestException>(()=>absent.ExecuteAsync(Request(),CancellationToken.None));Assert.Equal("target_not_ready",error.Code);
        if(!OperatingSystem.IsWindows())return;
        using var store=new BrowserArtifactStore();var wrong=new BrowserTransfers(()=>store,()=>Calls(wrongHash:true));
        await Assert.ThrowsAsync<BrowserArtifactException>(()=>wrong.ExecuteAsync(Request(),CancellationToken.None));
        Assert.Equal(0,await store.CleanupAsync());
    }

    [Fact]
    public async Task SplitExportAndCancellationCannotLeaveACompletedCapture()
    {
        if(!OperatingSystem.IsWindows())return;
        using var store=new BrowserArtifactStore();var transfer=new BrowserTransfers(()=>store,()=>Calls(split:true));
        await Assert.ThrowsAsync<SplitPackageException>(()=>transfer.ExecuteAsync(Request("apk.export"),CancellationToken.None));
        Assert.Equal(0,await store.CleanupAsync());using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>transfer.ExecuteAsync(Request(),cancellation.Token));
        Assert.Equal(0,await store.CleanupAsync());
    }
}
