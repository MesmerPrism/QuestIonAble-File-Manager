using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserArtifactPushCommandsTests
{
    internal const string Serial = "fixture-target";
    internal const string RemotePath = "/sdcard/Download/literal file.bin";
    internal static byte[] Body(BrowserArtifactReceipt? receipt = null, string? id = null, string remote = RemotePath, bool confirmed = true) =>
        JsonSerializer.SerializeToUtf8Bytes(new { requestId = id ?? Guid.NewGuid().ToString("D"), action = "files.push",
            artifactId = receipt?.Id ?? "00000000000040008000000000000001", sha256 = receipt?.Sha256 ?? new string('a', 64),
            sizeBytes = receipt?.SizeBytes ?? 3, serial = Serial, remotePath = remote, confirmOverwrite = confirmed });

    [Fact]
    public async Task PushBindsLiteralRemoteAndProtectedGenericSourceToExistingFactory()
    {
        Assert.True(BrowserArtifactCommands.IsArtifactAction(Body()));
        BrowserArtifactCommands.Parse(Body());
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.bin", new MemoryStream([1, 2, 3]));
        await using var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes);
        const string literal = "/sdcard/Download/literal ' \" ; $() `name.bin";
        var command = BrowserArtifactCommands.Parse(Body(receipt, remote: literal)).ToOperatorCommand(lease);
        var expected = OperatorCommands.PushFile(Serial, lease.Path, literal);
        Assert.Equal(expected.Kind, command.Kind); Assert.Equal(expected.Serial, command.Serial);
        Assert.Equal(expected.CliArguments, command.CliArguments); Assert.Equal(literal, command.RemotePath); Assert.Equal(lease.Path, command.LocalPath);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/sdcard/Download/")]
    [InlineData("relative.bin")]
    [InlineData("/sdcard/./file.bin")]
    [InlineData("/sdcard/../file.bin")]
    [InlineData("/sdcard/Download/.")]
    [InlineData("/sdcard/Download/..")]
    [InlineData("/sdcard//file.bin")]
    [InlineData("/sdcard/tab\tfile.bin")]
    [InlineData("/sdcard/delete\u007ffile.bin")]
    [InlineData("/sdcard/c1\u0085file.bin")]
    public void PushRejectsDirectorySyntaxTraversalControlsAndRelativePaths(string remote) =>
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Body(remote: remote)));

    [Fact]
    public void PushRequiresBooleanTrueAndClosedCaseSensitiveFields()
    {
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Body(confirmed: false)));
        var valid = Encoding.UTF8.GetString(Body());
        foreach (var replacement in new[] { "null", "1", "\"true\"" })
            Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Encoding.UTF8.GetBytes(valid.Replace("\"confirmOverwrite\":true", "\"confirmOverwrite\":" + replacement, StringComparison.Ordinal))));
        foreach (var extra in new[] { "\"localPath\":\"private.bin\"", "\"path\":\"/sdcard/file\"", "\"package\":\"example.app\"", "\"verification\":\"sha256\"", "\"confirmOverwrite\":true", "\"ConfirmOverwrite\":true" })
            Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Encoding.UTF8.GetBytes(valid[..^1] + "," + extra + "}")));
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(valid)!;
        fields.Remove("confirmOverwrite"); Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Body(remote: "/" + new string('a', 256))));
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(new byte[4097]));
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Encoding.UTF8.GetBytes(valid.Replace("\"sizeBytes\":3", "\"sizeBytes\":1073741825", StringComparison.Ordinal))));
        Assert.Equal(1073741824, BrowserArtifactCommands.Parse(Encoding.UTF8.GetBytes(valid.Replace("\"sizeBytes\":3", "\"sizeBytes\":1073741824", StringComparison.Ordinal))).SizeBytes);
    }
}
