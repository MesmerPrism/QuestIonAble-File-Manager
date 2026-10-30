using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class OwnedLaunchDiagnosticCaptureTests
{
    [Fact]
    public async Task BorrowedInputLaunchProducesActualReceiptAndOnlyTwoOwnedPayloads()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        var result = await fixture.Execute();
        var launch = Assert.IsType<ApkLaunchDiagnosticBundleResult>(result.ApkLaunchDiagnosticBundleResult);
        Assert.Equal(ApkLaunchDiagnosticDisposition.Completed, launch.Disposition);
        Assert.Equal(OperatorMutationStage.Confirmed, result.MutationReceipt!.Stage);
        Assert.True(result.MutationReceipt.HeadsetReadback);
        Assert.Equal(1, fixture.Runner.LaunchCount);
        Assert.Equal(1, fixture.Runner.ArmCount);
        Assert.True(fixture.Runner.ArmedBeforeLaunch);
        Assert.False(launch.PublishedAtRequestedPath);
        Assert.Equal(fixture.Output.Intent.OutputId, launch.BundleLeafName);
        Assert.Equal(fixture.Output.ReportedInputPath, launch.Artifact.Path);
        Assert.Equal(fixture.Output.ReportedOutputPath, launch.OutputDirectory);
        Assert.Equal(3, Directory.GetFiles(fixture.StageRoot).Length);
        Assert.Empty(Directory.GetDirectories(fixture.StageRoot));
        Assert.Single(Directory.GetFiles(fixture.StageRoot, "*.apk"));
        var seal = typeof(LocalApiDiagnosticOutputReservation).GetMethod("SealAsync", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(ApkLaunchDiagnosticBundleResult), typeof(CancellationToken)], null);
        Assert.NotNull(seal);
        var candidate = await (Task<LocalApiDiagnosticSealedOutput>)seal.Invoke(fixture.Output, [launch, CancellationToken.None])!;
        Assert.InRange(candidate.SizeBytes, 1, 4194304);
    }

    [Theory]
    [InlineData("pending", ApkLaunchDiagnosticDisposition.LaunchPending)]
    [InlineData("early", ApkLaunchDiagnosticDisposition.OutcomeUnknown)]
    [InlineData("reject", ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch)]
    public async Task ExistingClassifierRemainsAuthoritative(string mode, ApkLaunchDiagnosticDisposition expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); fixture.Runner.Mode = mode;
        var result = await fixture.Execute();
        Assert.Equal(expected, result.ApkLaunchDiagnosticBundleResult!.Disposition);
        Assert.Equal(mode == "reject" ? OperatorMutationStage.Rejected : OperatorMutationStage.Pending, result.MutationReceipt!.Stage);
        Assert.Equal(mode == "reject" ? 0 : 1, fixture.Runner.LaunchCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandKindCannotCrossReservationPolicyBeforeArming(bool launchPolicy)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(launchPolicy);
        var command = launchPolicy
            ? OperatorCommands.DiagnoseInspectedApp("QUEST123", fixture.Input.Path, fixture.Output.PlannedOutputPath)
            : OperatorCommands.LaunchDiagnoseInspectedApp("QUEST123", fixture.Input.Path, fixture.Output.PlannedOutputPath);
        using var loan = fixture.Input.BorrowDiagnosticInput();
        await Assert.ThrowsAsync<LocalApiException>(() => fixture.Client.ExecuteOwnedDiagnosticAsync(command, loan, fixture.Output.CreateSink()));
        Assert.Equal(0, fixture.Runner.ArmCount); Assert.Equal(0, fixture.Runner.LaunchCount);
        Assert.Single(Directory.GetFiles(fixture.StageRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticCommandsRejectInstallOptionsBeforeCaptureOwnership(bool launchPolicy)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(launchPolicy);
        var expected = launchPolicy
            ? OperatorCommands.LaunchDiagnoseInspectedApp("QUEST123", fixture.Input.Path, fixture.Output.PlannedOutputPath)
            : OperatorCommands.DiagnoseInspectedApp("QUEST123", fixture.Input.Path, fixture.Output.PlannedOutputPath);
        var crossed = new OperatorCommand(expected.Kind, expected.CliArguments, serial: expected.Serial,
            localPath: expected.LocalPath, outputPath: expected.OutputPath, installOptions: new());
        using var loan = fixture.Input.BorrowDiagnosticInput();
        var failure = await Assert.ThrowsAsync<LocalApiException>(() =>
            fixture.Client.ExecuteOwnedDiagnosticAsync(crossed, loan, fixture.Output.CreateSink()));
        Assert.Equal("diagnostic_command_mismatch", failure.Code);
        Assert.Equal(0, fixture.Runner.ArmCount);
        Assert.Equal(0, fixture.Runner.DiscoveryCount);
        Assert.Single(Directory.GetFiles(fixture.StageRoot));
    }

    [Theory]
    [InlineData("com.example.app/.Main", true)]
    [InlineData("com.example.app/com.example.app.Main", true)]
    [InlineData("com.example.app/com.example.shared.Main", true)]
    [InlineData("com.other.app/.Main", false)]
    [InlineData("com.example.app/Main", false)]
    [InlineData("com.example.app/.Main/extra", false)]
    [InlineData("com.example.app/.Main extra", false)]
    [InlineData("com.example.app/.Main\n", false)]
    public void LaunchComponentSealerBridgeKeepsExistingSamePackageGrammar(string component, bool valid) =>
        Assert.Equal(valid, AdbClient.IsLaunchDiagnosticComponent("com.example.app", component));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectAdmittedEntriesBindPolicyBeforeCaptureOwnership(bool launchPolicy)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(launchPolicy);
        using var loan = fixture.Input.BorrowDiagnosticInput();
        if (launchPolicy)
            await Assert.ThrowsAsync<LocalApiException>(() => fixture.Client.CaptureAdmittedApkDiagnosticsAsync(
                "QUEST123", loan, fixture.Output.CreateSink()));
        else
            await Assert.ThrowsAsync<LocalApiException>(() => fixture.Client.CaptureAdmittedApkLaunchDiagnosticsAsync(
                "QUEST123", loan, fixture.Output.CreateSink(), null));
        Assert.Equal(0, fixture.Runner.ArmCount);
        Assert.Equal(0, fixture.Runner.DiscoveryCount);
        Assert.Single(Directory.GetFiles(fixture.StageRoot));
    }

    [Fact]
    public async Task UnsupportedUnderlyingArmedCapabilityRejectsBeforeAnyCaptureOrCommand()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        using var loan = fixture.Input.BorrowDiagnosticInput();
        var client = new AdbClient("adb", new DiagnosticRequiredCommandRunner(new UnsupportedRunner()), new("aapt2", "apksigner"));
        Assert.False(client.SupportsOwnedLaunchDiagnostics);
        await Assert.ThrowsAsync<LocalApiException>(() => client.CaptureAdmittedApkLaunchDiagnosticsAsync(
            "QUEST123", loan, fixture.Output.CreateSink(), null));
        Assert.Single(Directory.GetFiles(fixture.StageRoot));
    }

    private sealed class UnsupportedRunner : ITruncatingCommandRunner, IStreamingCommandRunner
    {
        public Task<CommandResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken c = default) => throw new InvalidOperationException();
        public Task<TruncatingCommandResult> RunTruncatingAsync(string f, IReadOnlyList<string> a, int o, int e, TimeSpan t, CancellationToken c = default) => throw new InvalidOperationException();
        public Task<StreamingCommandResult> RunToStreamAsync(string f, IReadOnlyList<string> a, Stream d, long m, TimeSpan t, CancellationToken c = default) => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData("no-callback")]
    [InlineData("substitute")]
    [InlineData("oversize")]
    [InlineData("bad-hash")]
    [InlineData("double-callback")]
    [InlineData("manifest-overflow")]
    public async Task NoncompliantArmedRunnerCannotManufactureConfirmedEvidence(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); fixture.Runner.Mode = mode;
        if (mode == "no-callback")
        {
            var failure = await Assert.ThrowsAsync<ApkLaunchDiagnosticExecutionException>(() => fixture.Execute());
            Assert.Equal("diagnostic_action_provenance", Assert.IsType<LocalApiException>(failure.InnerException).Code);
            Assert.Equal(0, fixture.Runner.LaunchCount);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<OperatorMutationExecutionException>(() => fixture.Execute());
            Assert.Equal(OperatorMutationStage.Pending, failure.MutationReceipt.Stage);
            var launchFailure = Assert.IsType<ApkLaunchDiagnosticExecutionException>(failure.InnerException);
            var code = Assert.IsType<LocalApiException>(launchFailure.InnerException).Code;
            Assert.Equal(mode switch
            {
                "substitute" or "double-callback" => "diagnostic_action_provenance",
                "bad-hash" => "diagnostic_capture_provenance",
                "manifest-overflow" => "diagnostic_manifest_invalid",
                _ => "diagnostic_required_output_limit"
            }, code);
            Assert.Equal(1, fixture.Runner.LaunchCount);
        }
        Assert.Equal(1, fixture.Runner.ArmCount);
        Assert.True(fixture.LastSnapshot!.Resources.All(r => r.Role != "archive" || r.State == "planned"));
    }

    [Fact]
    public async Task StartedCallbackIsJoinedEvenWhenRunnerReturnsBeforeIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); fixture.Runner.Mode = "early-return";
        var pending = fixture.Execute();
        try
        {
            await fixture.Runner.LaunchEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(pending.IsCompleted);
            Assert.False(fixture.Input.TryDelete(out _));
        }
        finally { fixture.Runner.ReleaseLaunch.TrySetResult(); }
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.Equal(1, fixture.Runner.LaunchCount);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Runner.RetainedAction!());
        Assert.Equal(1, fixture.Runner.LaunchCount);
    }

    [Fact]
    public async Task SettledCaptureRevokesRetainedDestinationAndAction()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        await fixture.Execute();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Runner.RetainedDestination!.WriteAsync(new byte[] { 1 }).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Runner.RetainedAction!());
        Assert.Equal(1, fixture.Runner.LaunchCount);
    }

    [Fact]
    public async Task WholeDeadlineUsesInjectedClockButHoldsOwnersUntilCaptureActuallySettles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        var clock = new DeadlineClock(); fixture.Clock = clock;
        fixture.Runner.Mode = "held-capture";
        var pending = fixture.Execute();
        try
        {
            await fixture.Runner.CaptureHeld.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(TimeSpan.FromSeconds(300), clock.DueTime);
            clock.Fire();
            Assert.False(pending.IsCompleted);
            Assert.False(fixture.Input.TryDelete(out _));
        }
        finally { fixture.Runner.ReleaseCapture.TrySetResult(); }
        var failure = await Assert.ThrowsAsync<OperatorMutationExecutionException>(() => pending);
        Assert.Equal(OperatorMutationStage.Pending, failure.MutationReceipt.Stage);
        Assert.True(fixture.Runner.CaptureSettled);
    }

    [Fact]
    public async Task WholeDeadlineReachesNoneTokenPostReadThroughNestedRequiredAdapters()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        var clock = new DeadlineClock(); fixture.Clock = clock; fixture.Nested = true;
        fixture.Runner.OnPostRead = token =>
        {
            Assert.True(token.CanBeCanceled); clock.Fire(); token.ThrowIfCancellationRequested();
        };
        var failure = await Assert.ThrowsAsync<OperatorMutationExecutionException>(() => fixture.Execute());
        Assert.Equal(OperatorMutationStage.Pending, failure.MutationReceipt.Stage);
        Assert.Equal(1, fixture.Runner.LaunchCount);
        Assert.True(fixture.Runner.PostReadAttempted);
    }

    [Fact]
    public async Task FailedPidPostReadKeepsActualFailureEvenWithInstalledIdentityAndUidPresent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); fixture.Runner.Mode = "post-pid-failure";
        var result = (await fixture.Execute()).ApkLaunchDiagnosticBundleResult!;
        Assert.NotNull(result.InstalledAfterCapture);
        Assert.NotNull(result.CurrentUserUidAfterCapture);
        Assert.NotNull(result.OwnedPostReadbackFailure);
        Assert.Equal(ApkLaunchDiagnosticDisposition.OutcomeUnknown, result.Disposition);
        var candidate = await fixture.Output.SealAsync(result, CancellationToken.None);
        Assert.True(candidate.SizeBytes > 0);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        internal TimeSpan DueTime;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback; _state = state; DueTime = dueTime; return new TimerLease();
        }
        internal void Fire() => _callback!(_state);
        private sealed class TimerLease : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "qfm-owned-launch-test-" + Guid.NewGuid().ToString("N"));
        internal string StageRoot => Path.Combine(Root, "state", "staged");
        internal LocalApiArtifactStager Stager = null!;
        internal LocalApiStagedArtifact Input = null!;
        internal LocalApiDiagnosticOutputReservation Output = null!;
        internal LocalApiDiagnosticOutputSnapshot? LastSnapshot;
        internal readonly Runner Runner = new();
        internal TimeProvider Clock = TimeProvider.System;
        internal bool Nested;
        internal AdbClient Client => new("adb", Nested ? new DiagnosticRequiredCommandRunner(Runner) : Runner,
            new("aapt2", "apksigner"), Clock, Task.Delay);
        internal static async Task<Fixture> Create(bool launch = true)
        {
            var value = new Fixture(); Directory.CreateDirectory(value.Root);
            try
            {
                value.Stager = new(LocalApiStateSettings.CreateForTests(Path.Combine(value.Root, "state")));
                var source = Path.Combine(value.Root, "source.apk"); await File.WriteAllBytesAsync(source, OwnedLaunchDiagnosticCaptureTests.Runner.Apk);
                value.Input = await value.Stager.StageDiagnosticInputAsync(source, (_, _) => Task.CompletedTask);
                var id = "api-" + Guid.NewGuid().ToString("N");
                LocalApiDiagnosticOutputIntent intent;
                if (launch)
                {
                    var factory = typeof(LocalApiDiagnosticOutputIntent).GetMethod("CreateForLaunch", BindingFlags.Static | BindingFlags.NonPublic);
                    Assert.NotNull(factory);
                    intent = (LocalApiDiagnosticOutputIntent)factory.Invoke(null, [id])!;
                }
                else intent = LocalApiDiagnosticOutputIntent.Create(id);
                value.Output = await value.Stager.ReserveDiagnosticOutputAsync(intent, value.Input,
                    (snapshot, _) => { value.LastSnapshot = snapshot; return Task.CompletedTask; });
                return value;
            }
            catch { value.Dispose(); throw; }
        }
        internal async Task<OperatorExecutionResult> Execute()
        {
            using var loan = Input.BorrowDiagnosticInput();
            return await Client.ExecuteOwnedDiagnosticAsync(OperatorCommands.LaunchDiagnoseInspectedApp("QUEST123", Input.Path,
                Output.PlannedOutputPath), loan, Output.CreateSink());
        }
        public void Dispose()
        {
            Output?.Dispose(); Input?.Dispose(); Stager?.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    internal sealed class Runner : ITruncatingCommandRunner, IArmedCaptureCommandRunner
    {
        internal static readonly byte[] Apk = [0x50, 0x4b, 3, 4];
        internal string Mode = "normal";
        internal int LaunchCount, ArmCount, DiscoveryCount;
        internal bool ArmedBeforeLaunch;
        internal readonly TaskCompletionSource LaunchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseLaunch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<Task>? RetainedAction;
        internal Stream? RetainedDestination;
        internal readonly TaskCompletionSource CaptureHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CaptureSettled, PostReadAttempted;
        internal Action<CancellationToken>? OnPostRead;
        public Task<CommandResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken c = default) =>
            throw new InvalidOperationException("Owned required reads must be bounded.");
        public async Task<TruncatingCommandResult> RunTruncatingAsync(string f, IReadOnlyList<string> a, int o, int e, TimeSpan t, CancellationToken c = default)
        {
            c.ThrowIfCancellationRequested(); Assert.Equal(262144, o); Assert.Equal(262144, e);
            var text = "Success\n";
            if (ArmCount == 1 && CaptureSettled) { PostReadAttempted = true; OnPostRead?.Invoke(c); }
            if (f == "aapt2") text = "package: name='com.example.app' versionCode='42' versionName='" +
                (Mode == "manifest-overflow" ? new string('x', 150000) : "1.2.3") + "'\n";
            else if (f == "apksigner") text = "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n";
            else if (a.SequenceEqual(["devices", "-l"])) { DiscoveryCount++; text = $"List of devices attached\nQUEST123 {(Mode == "reject" && DiscoveryCount == 2 ? "offline" : "device")} product:eureka model:Quest_3 transport_id:1\n"; }
            else if (a.Contains("pm path 'com.example.app'")) text = "package:/data/app/example/base.apk\n";
            else if (a.Contains("--user")) text = "package:com.example.app uid:10234\npackage:com.example.other uid:10235\n";
            else if (a.Contains("query-activities")) text = "com.example.app/.Main\n";
            else if (a.SequenceEqual(["-s", "QUEST123", "shell", "dumpsys", "package", "com.example.app"])) text = "  Activity #0 ActivityInfo{abc com.example.app/.Main}\n    exported=true\n";
            else if (a.Contains("+%s.%N")) text = "1700000000.123456789\n";
            else if (a.Contains("start"))
            {
                LaunchCount++; ArmedBeforeLaunch = ArmCount == 1; LaunchEntered.TrySetResult();
                if (Mode == "early-return") await ReleaseLaunch.Task;
            }
            else if (a.Contains("activities")) text = Mode == "pending" ? "" : "mResumedActivity: ActivityRecord{123 u0 com.example.app/.Main t1}\n";
            else if (a.Contains("UID,PID,ARGS"))
            {
                if (Mode == "post-pid-failure") throw new InvalidDataException("fixture PID read failed");
                text = "UID PID ARGS\n10234 123 com.example.app\n";
            }
            return new(new(f, a.ToArray(), 0, text, "", TimeSpan.Zero), false, false);
        }
        public async Task<StreamingCommandResult> RunToStreamAsync(string f, IReadOnlyList<string> a, Stream d, long m, TimeSpan t, CancellationToken c = default)
        {
            var unsupported = a.Count == 6 && a[2] == "exec-out" && a[5].Contains("qfm-installed-digest:v1", StringComparison.Ordinal);
            var bytes = unsupported ? Encoding.UTF8.GetBytes("qfm-installed-digest:unsupported\n") : Apk;
            if (a.Count == 6 && a[2] == "exec-out" && a[5].Contains("qfm-byte-transfer:v2:", StringComparison.Ordinal))
            {
                var nonce = Regex.Match(a[5], "qfm-byte-transfer:v2:([0-9a-f]{32}):").Groups[1].Value;
                bytes = Encoding.UTF8.GetBytes($"qfm-byte-transfer:v2:{nonce}:{Apk.Length}\n")
                    .Concat(Apk)
                    .Concat(Encoding.UTF8.GetBytes($"\nqfm-byte-transfer:complete:v2:{nonce}:{Apk.Length}\n")).ToArray();
            }
            await d.WriteAsync(bytes, c);
            return new(new(f, a.ToArray(), unsupported ? 90 : 0, "", "", TimeSpan.Zero), bytes.Length, Hash(bytes));
        }
        public async Task<ArmedCaptureCommandResult<T>> RunArmedCaptureAsync<T>(string f, IReadOnlyList<string> a, Stream d,
            long m, TimeSpan window, Func<CancellationToken, Task<T>> action, CancellationToken c = default)
        {
            ArmCount++; Assert.Equal(262144, m); Assert.Equal(TimeSpan.FromSeconds(10), window);
            Assert.False(d.CanSeek);
            RetainedDestination = d;
            RetainedAction = async () => { await action(CancellationToken.None); };
            T actual;
            if (Mode == "no-callback") actual = (T)(object)new ApkLaunchDiagnosticAttempt(false, null, "fake", "fake", []);
            else if (Mode == "early-return") { _ = action(CancellationToken.None); actual = (T)(object)new ApkLaunchDiagnosticAttempt(false, null, "fake", "fake", []); }
            else actual = await action(c);
            if (Mode == "double-callback") await action(c);
            if (Mode == "substitute") actual = (T)(object)(((ApkLaunchDiagnosticAttempt)(object)actual!) with { FailureMessage = "substitute" });
            var bytes = Mode == "oversize" ? new byte[262145] : Encoding.UTF8.GetBytes("app-owned marker\n");
            await d.WriteAsync(bytes, c);
            if (Mode == "held-capture") { CaptureHeld.TrySetResult(); await ReleaseCapture.Task; }
            CaptureSettled = true;
            return new(actual, new(f, a.ToArray(), -1, "", "", TimeSpan.Zero), bytes.Length,
                Mode == "bad-hash" ? new string('f', 64) : Hash(bytes), Mode != "early", false, Mode == "early", true);
        }
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
