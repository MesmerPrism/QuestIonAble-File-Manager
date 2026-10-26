using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class OwnedDiagnosticCaptureTests
{
    [Theory]
    [InlineData("aapt2")]
    [InlineData("apksigner")]
    [InlineData("activities")]
    [InlineData("pidof")]
    [InlineData("uid")]
    public async Task RequiredOverflowNeverParsesValidPrefixOrWritesPayloads(string capture)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(capture);
        var error = await Assert.ThrowsAsync<LocalApiException>(fixture.Capture);
        Assert.Equal("diagnostic_required_output_limit", error.Code);
        Assert.Single(Directory.GetFiles(fixture.StageDirectory));
        Assert.DoesNotContain(fixture.Source.Calls, call => call.Arguments.Contains("install") || call.Arguments.Contains("am"));
    }

    [Theory]
    [InlineData("trailer")]
    [InlineData("extra")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("payload")]
    [InlineData("payload-after-optional")]
    [InlineData("runner-hash")]
    [InlineData("overflow")]
    public async Task OwnedUnsupportedRemoteDigestRequiresExactFramedBodyAndCounterProof(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create("none", scenario);
        await Assert.ThrowsAnyAsync<Exception>(fixture.Capture);
        Assert.Single(Directory.GetFiles(fixture.StageDirectory));
        Assert.Contains(fixture.Source.Calls, call => call.Arguments.Any(arg => arg.Contains("qfm-byte-transfer:v2:", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.Source.Calls, call => call.Arguments.Any(arg => arg.EndsWith("exec cat <&3", StringComparison.Ordinal)));
        Assert.Equal(scenario == "payload-after-optional", fixture.Runner.Captures.Any(call => call.Arguments.Contains("logcat")));
    }

    [Fact]
    public async Task RequiredFocusStreamOverflowCannotBecomeAnUnavailableFactAndContinueCapture()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create("none", streamFailure: (file, args) =>
            args.SequenceEqual(new[] { "-s", "QUEST123", "shell", "dumpsys", "window", "windows" }) ? new FleetTransferLimitException(262144) : null);
        var error = await Assert.ThrowsAsync<LocalApiException>(fixture.Capture);
        Assert.Equal("diagnostic_required_output_limit", error.Code);
        Assert.Single(Directory.GetFiles(fixture.StageDirectory));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(262145)]
    public async Task RequiredFocusStderrIsRejectedWithoutGuessingWhetherTheStreamingReaderTruncatedIt(int size)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create("none", focusStderr: new string('e', size));
        var error = await Assert.ThrowsAsync<LocalApiException>(fixture.Capture);
        Assert.Equal("diagnostic_required_stream_unusable", error.Code);
        Assert.Single(Directory.GetFiles(fixture.StageDirectory));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("source")]
    [InlineData("output")]
    public async Task CrossedImmutableCommandCannotStartAnyInspectorOrDeviceRead(string crossing)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create("none");
        var command = crossing == "kind" ? OperatorCommands.ObserveInspectedApp("QUEST123", fixture.Input.Path)
            : OperatorCommands.DiagnoseInspectedApp("QUEST123", crossing == "source" ? Path.Combine(fixture.Root, "other.apk") : fixture.Input.Path,
                crossing == "output" ? Path.Combine(fixture.Root, "other-output") : fixture.Output.PlannedOutputPath);
        var client = new AdbClient("adb", fixture.Runner, new("aapt2", "apksigner"));
        Assert.Equal("diagnostic_command_mismatch", (await Assert.ThrowsAsync<LocalApiException>(() =>
            client.ExecuteOwnedDiagnosticAsync(command, fixture.Input, fixture.Output.CreateSink()))).Code);
        Assert.Empty(fixture.Source.Calls);
    }

    [Fact]
    public async Task CancellationDuringArchiveIdentityAcknowledgementNeverReturnsACandidate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        using var fixture = await Fixture.Create("none", persist: async (snapshot, token) =>
        {
            if (snapshot.Resources.Any(resource => resource.Role == "archive" && resource.State == "owned"))
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        });
        var captured = await fixture.Capture();
        var seal = fixture.Output.SealAsync(captured, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seal);
        Assert.Null(fixture.Output.LastAcknowledgedSnapshot!.Resources.Single(resource => resource.Role == "archive").Sha256);
        Assert.True(await fixture.Output.CleanupAsync());
        Assert.Single(Directory.GetFiles(fixture.StageDirectory));
    }

    [Fact]
    public async Task FailedFinalArchiveAcknowledgementRetainsSealedBytesWithoutCandidateReadAuthority()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create("none", persist: (snapshot, _) =>
            snapshot.Phase == "sealed" ? Task.FromException(new IOException("synthetic final commit failed")) : Task.CompletedTask);
        var captured = await fixture.Capture();
        await Assert.ThrowsAsync<IOException>(() => fixture.Output.SealAsync(captured, default));
        var archive = fixture.Output.LastAcknowledgedSnapshot!.Resources.Single(resource => resource.Role == "archive");
        Assert.Equal("sealing", fixture.Output.LastAcknowledgedSnapshot.Phase);
        Assert.NotNull(archive.Sha256);
        Assert.Equal("diagnostic_output_unavailable", (await Assert.ThrowsAsync<LocalApiException>(() =>
            fixture.Output.OpenSealedCandidateAsync(archive.SizeBytes!.Value, archive.Sha256!))).Code);
    }

    [Fact]
    public async Task OriginalCaptureCannotBeReplayedAfterItsReadFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create("aapt2");
        await Assert.ThrowsAsync<LocalApiException>(fixture.Capture);
        var calls = fixture.Source.Calls.Count;
        Assert.Equal("diagnostic_capture_consumed", (await Assert.ThrowsAsync<LocalApiException>(fixture.Capture)).Code);
        Assert.Equal(calls, fixture.Source.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedArchiveReopenRejectsChangedBytesOrReplacementIdentity(bool replaceIdentity)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create("none");
        var result = await fixture.Capture(); await fixture.Output.SealAsync(result, default);
        var recorded = fixture.Output.LastAcknowledgedSnapshot! with { Phase = "available" }; // simulated authenticated owner commit
        var archive = recorded.Resources.Single(resource => resource.Role == "archive");
        fixture.ReleaseForRestart();
        var path = Path.Combine(fixture.StageDirectory, archive.Leaf);
        var bytes = await File.ReadAllBytesAsync(path);
        if (replaceIdentity)
        {
            File.Move(path, Path.Combine(fixture.Root, "original-archive.zip"));
            await File.WriteAllBytesAsync(path, bytes); // same hash/length, different native identity
        }
        else { bytes[0] ^= 1; await File.WriteAllBytesAsync(path, bytes); }
        using var restarted = new LocalApiArtifactStager(LocalApiStateSettings.CreateForTests(Path.Combine(fixture.Root, "state")));
        await restarted.RegisterDiagnosticRecoveryAsync([recorded], (_, _) => Task.CompletedTask);
        Assert.Equal("diagnostic_resource_changed", (await Assert.ThrowsAsync<LocalApiException>(() =>
            restarted.ReopenDiagnosticArchiveAsync(recorded.Intent, archive))).Code);
        Assert.True(File.Exists(path)); // no attempted pathname adoption/removal
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root, _apk;
        private readonly LocalApiArtifactStager _stager;
        private readonly LocalApiStagedArtifact _staged;
        private readonly LocalApiDiagnosticInputLease _input;
        private readonly LocalApiDiagnosticOutputReservation _output;
        internal InspectedDeploymentTests.FakeRunner Source { get; }
        internal InspectedDeploymentTests.TruncatingDiagnosticRunner Runner { get; }
        internal string StageDirectory => Path.Combine(_root, "state", "staged");
        internal string Root => _root;
        internal LocalApiDiagnosticInputLease Input => _input;
        internal LocalApiDiagnosticOutputReservation Output => _output;
        private Fixture(string root, string apk, LocalApiArtifactStager stager, LocalApiStagedArtifact staged,
            LocalApiDiagnosticInputLease input, LocalApiDiagnosticOutputReservation output,
            InspectedDeploymentTests.FakeRunner source, InspectedDeploymentTests.TruncatingDiagnosticRunner runner)
        { _root = root; _apk = apk; _stager = stager; _staged = staged; _input = input; _output = output; Source = source; Runner = runner; }
        internal static async Task<Fixture> Create(string capture, string? scenario = null,
            Func<string, IReadOnlyList<string>, Exception?>? streamFailure = null, string? focusStderr = null,
            Func<LocalApiDiagnosticOutputSnapshot, CancellationToken, Task>? persist = null)
        {
            var apk = await InspectedDeploymentTests.CreateApkAsync();
            var root = Path.Combine(Path.GetTempPath(), "qfm-owned-callgraph-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var stager = new LocalApiArtifactStager(LocalApiStateSettings.CreateForTests(Path.Combine(root, "state")));
            var staged = await stager.StageDiagnosticInputAsync(apk, (_, _) => Task.CompletedTask);
            var input = staged.BorrowDiagnosticInput();
            var output = await stager.ReserveDiagnosticOutputAsync(LocalApiDiagnosticOutputIntent.Create("api-" + Guid.NewGuid().ToString("N")), staged, persist ?? ((_, _) => Task.CompletedTask));
            var source = InspectedDeploymentTests.CreateDeploymentRunner(apk, pidofOutput: "", commandFailure: streamFailure,
                windowFocusResult: focusStderr is null ? null : new("", [], 0, "", focusStderr, TimeSpan.Zero));
            return new(root, apk, stager, staged, input, output, source, new(source, capture, scenario));
        }
        internal Task<ApkDiagnosticBundleResult> Capture() => new AdbClient("adb", Runner, new("aapt2", "apksigner"))
            .CaptureAdmittedApkDiagnosticsAsync("QUEST123", _input, _output.CreateSink());
        internal void ReleaseForRestart() { _output.Dispose(); _input.Dispose(); _staged.Dispose(); _stager.Dispose(); }
        public void Dispose()
        { ReleaseForRestart(); File.Delete(_apk); Directory.Delete(_root, true); }
    }
}
