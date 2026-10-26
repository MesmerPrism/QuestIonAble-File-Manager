using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class DiagnosticRequiredCommandRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredOverflowFailsBeforeIdentityPrefixCanBeParsed(bool stderr)
    {
        var source = new RequiredRunner(stderr);
        var owned = new AdbClient("adb", source, new("aapt2", "apksigner")).CreateOwnedDiagnosticClient();
        var file = Path.Combine(Path.GetTempPath(), "qfm-inspector-bound-" + Guid.NewGuid().ToString("N") + ".apk");
        try
        {
            await File.WriteAllBytesAsync(file, [1]);
            var failure = await Assert.ThrowsAsync<LocalApiException>(() => owned.CreateApkInspector().InspectAsync(file));
            Assert.Equal("diagnostic_required_output_limit", failure.Code);
            Assert.Equal(1, source.RequiredCalls);
            Assert.Equal(0, source.UnboundedCalls);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void OwnedClientRejectsLegacyCollectionBeforeAnyCommand() =>
        Assert.Throws<LocalApiException>(() => new AdbClient("adb", new LegacyRunner()).CreateOwnedDiagnosticClient());

    private sealed class LegacyRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Legacy process must not start.");
    }
    private sealed class RequiredRunner(bool stderr) : ITruncatingCommandRunner, IStreamingCommandRunner
    {
        internal int RequiredCalls, UnboundedCalls;
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        { UnboundedCalls++; throw new InvalidOperationException("Unbounded reads forbidden."); }
        public Task<TruncatingCommandResult> RunTruncatingAsync(string fileName, IReadOnlyList<string> arguments, int maximumStandardOutputBytes, int maximumStandardErrorBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            RequiredCalls++;
            Assert.Equal(262144, maximumStandardOutputBytes); Assert.Equal(262144, maximumStandardErrorBytes);
            return Task.FromResult(new TruncatingCommandResult(new(fileName, arguments, 0,
                "package: name='com.example.app' versionCode='1'", "", TimeSpan.Zero), !stderr, stderr));
        }
        public Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments, Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No stream before inspector acknowledgement.");
    }
}
