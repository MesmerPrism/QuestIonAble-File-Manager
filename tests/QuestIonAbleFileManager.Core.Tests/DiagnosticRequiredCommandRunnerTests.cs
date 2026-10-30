using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class DiagnosticRequiredCommandRunnerTests
{
    [Fact]
    public void ArmedCapabilityRequiresActualUnderlyingSupportIncludingNestedWrappers()
    {
        var unsupported = new DiagnosticRequiredCommandRunner(new RequiredRunner(false));
        var nestedUnsupported = new DiagnosticRequiredCommandRunner(unsupported);
        Assert.False(Supports(unsupported));
        Assert.False(Supports(nestedUnsupported));
        var supported = new DiagnosticRequiredCommandRunner(new ArmedRunner());
        Assert.True(Supports(supported));
        Assert.True(Supports(new DiagnosticRequiredCommandRunner(supported)));
    }

    [Fact]
    public async Task ArmedAdapterForwardsExactFixedBudgetAndActualAction()
    {
        var source = new ArmedRunner();
        var adapter = Assert.IsAssignableFrom<IArmedCaptureCommandRunner>(new DiagnosticRequiredCommandRunner(source));
        using var destination = new MemoryStream();
        var actionCalls = 0;
        var proof = new object();
        var result = await adapter.RunArmedCaptureAsync("adb", ["-s", "QUEST123", "shell", "logcat"],
            destination, 262144, TimeSpan.FromSeconds(10), _ => { actionCalls++; return Task.FromResult(proof); });
        Assert.Same(proof, result.ActionResult);
        Assert.Equal(1, actionCalls);
        Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData(262145, 10000)]
    [InlineData(262144, 10001)]
    [InlineData(262144, 9999)]
    [InlineData(0, 10000)]
    public async Task ArmedAdapterRejectsNonPolicyBudgetBeforeUnderlyingRunner(long bytes, int milliseconds)
    {
        var source = new ArmedRunner();
        var adapter = Assert.IsAssignableFrom<IArmedCaptureCommandRunner>(new DiagnosticRequiredCommandRunner(source));
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<LocalApiException>(() => adapter.RunArmedCaptureAsync("adb", [], destination,
            bytes, TimeSpan.FromMilliseconds(milliseconds), _ => Task.FromResult(1)));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task NestedRequiredNoneTokenReadStillAcknowledgesOwnedOperationCancellation()
    {
        using var operation = new CancellationTokenSource();
        var source = new BlockingRequiredRunner();
        var inner = new DiagnosticRequiredCommandRunner(source, operation.Token);
        var outer = new DiagnosticRequiredCommandRunner(inner);
        var read = outer.RunAsync("adb", ["post-read"], TimeSpan.FromSeconds(15), CancellationToken.None);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        operation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(source.Settled);
    }

    private sealed class BlockingRequiredRunner : ITruncatingCommandRunner, IStreamingCommandRunner
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Settled;
        public async Task<TruncatingCommandResult> RunTruncatingAsync(string f, IReadOnlyList<string> a,
            int o, int e, TimeSpan t, CancellationToken c = default)
        {
            Assert.True(c.CanBeCanceled); Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, c); throw new InvalidOperationException(); }
            finally { Settled = true; }
        }
        public Task<CommandResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken c = default) => throw new InvalidOperationException();
        public Task<StreamingCommandResult> RunToStreamAsync(string f, IReadOnlyList<string> a, Stream d, long m, TimeSpan t, CancellationToken c = default) => throw new InvalidOperationException();
    }

    private static bool Supports(DiagnosticRequiredCommandRunner runner) =>
        (bool?)typeof(DiagnosticRequiredCommandRunner).GetProperty("SupportsArmedCapture",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(runner) ?? false;

    private sealed class ArmedRunner : ITruncatingCommandRunner, IArmedCaptureCommandRunner
    {
        internal int Calls;
        public async Task<ArmedCaptureCommandResult<T>> RunArmedCaptureAsync<T>(string fileName,
            IReadOnlyList<string> arguments, Stream destination, long maximumBytes, TimeSpan postActionWindow,
            Func<CancellationToken, Task<T>> armedAction, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal(262144, maximumBytes); Assert.Equal(TimeSpan.FromSeconds(10), postActionWindow);
            return new(await armedAction(cancellationToken), new(fileName, arguments, 0, "", "", TimeSpan.Zero),
                0, new string('0', 64), true, false, false, true);
        }
        public Task<CommandResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken c = default) => throw new InvalidOperationException();
        public Task<TruncatingCommandResult> RunTruncatingAsync(string f, IReadOnlyList<string> a, int o, int e, TimeSpan t, CancellationToken c = default) => throw new InvalidOperationException();
        public Task<StreamingCommandResult> RunToStreamAsync(string f, IReadOnlyList<string> a, Stream d, long m, TimeSpan t, CancellationToken c = default) => throw new InvalidOperationException();
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualRequiredUtf8BytesAreBoundedEvenWhenRunnerReportsNoTruncation(bool stderr)
    {
        var adapter = new DiagnosticRequiredCommandRunner(new UnreportedOverflowRunner(stderr));
        var failure = await Assert.ThrowsAsync<LocalApiException>(() =>
            adapter.RunAsync("adb", ["fixed-required-read"], TimeSpan.FromSeconds(15)));
        Assert.Equal("diagnostic_required_output_limit", failure.Code);
    }

    private sealed class UnreportedOverflowRunner(bool stderr) : ITruncatingCommandRunner, IStreamingCommandRunner
    {
        public Task<TruncatingCommandResult> RunTruncatingAsync(string f, IReadOnlyList<string> a,
            int o, int e, TimeSpan t, CancellationToken c = default)
        {
            // The character count fits; the UTF-8 byte count exceeds the cap.
            var oversized = new string('\u00e9', 131073);
            return Task.FromResult(new TruncatingCommandResult(new(f, a, 0,
                stderr ? "" : oversized, stderr ? oversized : "", TimeSpan.Zero), false, false));
        }
        public Task<CommandResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken c = default) => throw new InvalidOperationException();
        public Task<StreamingCommandResult> RunToStreamAsync(string f, IReadOnlyList<string> a, Stream d, long m, TimeSpan t, CancellationToken c = default) => throw new InvalidOperationException();
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
