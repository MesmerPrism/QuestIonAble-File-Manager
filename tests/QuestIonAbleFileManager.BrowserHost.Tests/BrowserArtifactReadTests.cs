using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserArtifactReadTests
{
    private const string Serial = "fixture-target";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static byte[] Request(string action, string extra = "", BrowserArtifactReceipt? artifact = null, string? requestId = null) =>
        Encoding.UTF8.GetBytes($$"""{"requestId":"{{requestId ?? Guid.NewGuid().ToString("D")}}","action":"{{action}}","artifactId":"{{artifact?.Id ?? "00000000000040008000000000000002"}}","sha256":"{{artifact?.Sha256 ?? Hash}}","sizeBytes":{{artifact?.SizeBytes ?? 3}}{{extra}}} """);

    [Theory]
    [InlineData("apk.preflight", ",\"serial\":\"fixture-target\"")]
    [InlineData("runtime.observe", ",\"serial\":\"fixture-target\",\"verification\":\"exact\"")]
    [InlineData("runtime.observe", ",\"serial\":\"fixture-target\",\"verification\":\"development-metadata\"")]
    public async Task ClosedReadRequestsBuildTheExistingCoreFactories(string action, string extra)
    {
        var body = Request(action, extra);
        Assert.True(BrowserArtifactCommands.IsArtifactAction(body));
        var request = BrowserArtifactCommands.Parse(body);
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("fixture.apk", new MemoryStream([1, 2, 3]));
        await using var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes);
        request = BrowserArtifactCommands.Parse(Request(action, extra, receipt));
        var command = request.ToOperatorCommand(lease);
        var expected = action == "apk.preflight" ? OperatorCommands.PreflightInspectedApp(Serial, lease.Path) :
            extra.Contains("development-metadata", StringComparison.Ordinal) ? OperatorCommands.ObserveDevelopmentApp(Serial, lease.Path, null) :
            OperatorCommands.ObserveInspectedApp(Serial, lease.Path);
        Assert.Equal(expected.Kind, command.Kind); Assert.Equal(expected.CliArguments, command.CliArguments);
        Assert.Equal(expected.Serial, command.Serial); Assert.Null(command.ReportedInstallReference);
    }

    [Theory]
    [InlineData("apk.preflight", "")]
    [InlineData("apk.preflight", ",\"serial\":null")]
    [InlineData("apk.preflight", ",\"serial\":\"fixture-target\",\"verification\":\"exact\"")]
    [InlineData("runtime.observe", ",\"serial\":\"fixture-target\"")]
    [InlineData("runtime.observe", ",\"serial\":\"fixture-target\",\"verification\":\"EXACT\"")]
    [InlineData("runtime.observe", ",\"serial\":\"fixture-target\",\"verification\":null")]
    [InlineData("runtime.observe", ",\"serial\":\"fixture-target\",\"verification\":\"development-metadata\",\"reportedInstallReference\":\"invented\"")]
    [InlineData("runtime.observe", ",\"serial\":\"fixture-target\",\"verification\":\"exact\",\"serial\":\"other-target\"")]
    [InlineData("runtime.observe", ",\"serial\":\"fixture target\",\"verification\":\"exact\"")]
    [InlineData("runtime.observe", ",\"serial\":\"fixture-target\",\"verification\":\"exact\",\"apkPath\":\"private.apk\"")]
    [InlineData("apk.inspect", ",\"serial\":\"fixture-target\"")]
    public void ReadRequestsRejectMissingExtraDuplicateInvalidOrCallerPathFields(string action, string extra) =>
        Assert.Throws<BrowserRequestException>(() => BrowserArtifactCommands.Parse(Request(action, extra)));
}
