using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserArtifactCommandsTests
{
    private const string RequestId = "00000000-0000-4000-8000-000000000001";
    private const string ArtifactId = "00000000000040008000000000000002";
    private const string Hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private static byte[] Body(string extra = "", string action = "apk.inspect", string hash = Hash, long size = 3) =>
        Encoding.UTF8.GetBytes($$"""{"requestId":"{{RequestId}}","action":"{{action}}","artifactId":"{{ArtifactId}}","sha256":"{{hash}}","sizeBytes":{{size}}{{extra}}}""");

    [Fact]
    public void ValidInspectRetainsOnlyExactArtifactBinding()
    {
        var request = BrowserArtifactCommands.Parse(Body());
        Assert.Equal(RequestId, request.RequestId); Assert.Equal("apk.inspect", request.Action);
        Assert.Equal(ArtifactId, request.ArtifactId); Assert.Equal(Hash, request.Sha256); Assert.Equal(3, request.SizeBytes);
        Assert.True(BrowserArtifactCommands.IsArtifactAction(Body()));
        Assert.False(BrowserArtifactCommands.IsArtifactAction(Body(action: "devices")));
        Assert.False(BrowserArtifactCommands.IsArtifactAction(Encoding.UTF8.GetBytes("invalid json")));
        Assert.DoesNotContain("Path", JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData(",\"serial\":\"fixture-target\"")]
    [InlineData(",\"path\":\"private.apk\"")]
    [InlineData(",\"confirmed\":true")]
    [InlineData(",\"requestId\":\"00000000-0000-4000-8000-000000000003\"")]
    [InlineData(",\"ArtifactId\":\"different-case\"")]
    public void UnknownDuplicateAndCallerPathFieldsReject(string extra) =>
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Body(extra)));

    [Theory]
    [InlineData(-1)]
    [InlineData(268435457)]
    public void InvalidSizesReject(long size) => Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Body(size: size)));

    [Fact]
    public void InvalidHashesActionsIdsDepthAndBodySizeReject()
    {
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Body(hash: Hash.ToUpperInvariant())));
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Body(action: "apk.install")));
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Body()).Replace(ArtifactId, "../app.apk"))));
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Body()).Replace(RequestId, Guid.Empty.ToString("D")))));
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Encoding.UTF8.GetBytes("{\"nested\":{\"nested\":{\"nested\":{\"nested\":{}}}}}")));
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(new byte[4097]));
        Assert.Equal(0, BrowserArtifactCommands.Parse(Body(size: 0)).SizeBytes);
        Assert.Equal(268435456, BrowserArtifactCommands.Parse(Body(size: 268435456)).SizeBytes);
    }

    [Fact]
    public async Task OperatorCommandRequiresTheExactLiveLeaseReceipt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("fixture.apk", new MemoryStream(Encoding.UTF8.GetBytes("abc")));
        await using var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes);
        var request = new BrowserArtifactCommandRequest(RequestId, "apk.inspect", receipt.Id, receipt.Sha256, receipt.SizeBytes);
        var command = request.ToOperatorCommand(lease);
        Assert.Equal(OperatorCommandKind.InspectApk, command.Kind); Assert.Equal(lease.Path, command.LocalPath); Assert.Null(command.Serial);
        Assert.Equal(["apk", "inspect", "--file", lease.Path], command.CliArguments);
        Assert.Throws<BrowserRequestException>(() => (request with { ArtifactId = ArtifactId }).ToOperatorCommand(lease));
        Assert.Throws<BrowserRequestException>(() => (request with { Sha256 = new string('0', 64) }).ToOperatorCommand(lease));
        Assert.Throws<BrowserRequestException>(() => (request with { SizeBytes = 4 }).ToOperatorCommand(lease));
        Assert.Throws<BrowserRequestException>(() => (request with { Action = "apk.install" }).ToOperatorCommand(lease));
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => request.ToOperatorCommand(lease));
    }
}
