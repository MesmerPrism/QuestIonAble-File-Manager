using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserTransferCommandsTests
{
    private const string Id = "00000000-0000-4000-8000-000000000001";
    private static byte[] Request(string action = "files.pull", string extra = "\"remotePath\":\"/sdcard/Download/sample.bin\"", string fileName = "sample.bin") =>
        Encoding.UTF8.GetBytes($$"""{"requestId":"{{Id}}","action":"{{action}}","serial":"QUEST-FIXTURE","fileName":{{JsonSerializer.Serialize(fileName)}},{{extra}}} """);

    [Fact]
    public void ReadCapturesHaveClosedExactScopeAndNoNativeOutputPath()
    {
        var body = Request(); Assert.True(BrowserTransferCommands.IsTransferAction(body));
        var pull = BrowserTransferCommands.Parse(body);
        Assert.Equal(Id, pull.RequestId); Assert.Equal("QUEST-FIXTURE", pull.Serial);
        Assert.Equal("/sdcard/Download/sample.bin", pull.RemotePath); Assert.Equal("sample.bin", pull.FileName);
        Assert.Null(pull.PackageName);
        var export = BrowserTransferCommands.Parse(Request("apk.export", "\"package\":\"example.fixture\"", "fixture.apk"));
        Assert.Equal("example.fixture", export.PackageName); Assert.Null(export.RemotePath);
    }

    [Theory]
    [InlineData("\"remotePath\":\"/sdcard/file.bin\",\"localPath\":\"C:/private\"")]
    [InlineData("\"remotePath\":\"/sdcard/file.bin\",\"remotePath\":\"/sdcard/other.bin\"")]
    [InlineData("\"remotePath\":\"relative.bin\"")]
    [InlineData("\"path\":\"/sdcard/file.bin\"")]
    [InlineData("\"remotePath\":null")]
    public void InvalidOrExpandedReadRequestsAreRejected(string extra) =>
        Assert.Throws<BrowserRequestException>(() => BrowserTransferCommands.Parse(Request(extra: extra)));

    [Theory]
    [InlineData("../file.bin")]
    [InlineData("C:/private/file.bin")]
    [InlineData("NUL.apk")]
    [InlineData("file.apk:stream")]
    public void DownloadNameIsOnlyAValidatedLabel(string fileName) =>
        Assert.Throws<BrowserRequestException>(() => BrowserTransferCommands.Parse(Request(fileName: fileName)));

    [Fact]
    public void ExportRequiresExactPackageAndApkLabelAndMalformedBodiesFailClosed()
    {
        Assert.Throws<BrowserRequestException>(() => BrowserTransferCommands.Parse(Request("apk.export", "\"package\":\"example.fixture\"", "file.zip")));
        Assert.Throws<BrowserRequestException>(() => BrowserTransferCommands.Parse(Request("apk.export", "\"package\":\"example.fixture;id\"", "file.apk")));
        Assert.Throws<BrowserRequestException>(() => BrowserTransferCommands.Parse(new byte[4097]));
        Assert.False(BrowserTransferCommands.IsTransferAction(Encoding.UTF8.GetBytes("[]")));
        Assert.False(BrowserTransferCommands.IsTransferAction(Request("files.push")));
    }
}
