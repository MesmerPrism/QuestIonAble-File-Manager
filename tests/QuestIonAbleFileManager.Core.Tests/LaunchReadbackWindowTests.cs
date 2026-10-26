using System.Security.Cryptography;
using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LaunchReadbackWindowTests
{
    [Fact]
    public async Task DelayedResumptionUsesFreshReadbackAfterExactlyOneStart()
    {
        using var fixture = new Fixture();
        var runner = new LaunchRunner(fixture.Bytes, [Absent, Resumed]);
        var clock = new Clock();
        var client = Client(runner, clock);
        var result = await client.LaunchInspectedAppAsync("FIXTURE", fixture.Path);
        Assert.True(result.ComponentObservedResumed);
        Assert.Equal(1, runner.Starts); Assert.Equal(2, runner.ActivityReads);
        Assert.Equal("com.example.fixture/.Main", result.Component);
        Assert.All(runner.ReadbackTimeouts, timeout => Assert.InRange(timeout, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PersistentAbsenceHasFiniteReadOnlyWindowAndRemainsUnconfirmed()
    {
        using var fixture = new Fixture(); var runner = new LaunchRunner(fixture.Bytes, [Absent]); var clock = new Clock();
        var result = await Client(runner, clock).LaunchInspectedAppAsync("FIXTURE", fixture.Path);
        Assert.False(result.ComponentObservedResumed); Assert.Equal(1, runner.Starts);
        Assert.InRange(runner.ActivityReads, 2, 11); Assert.Equal(TimeSpan.FromSeconds(5).Ticks, clock.Ticks);
    }

    [Fact]
    public async Task DelayedAliasTargetResumptionPreservesTheSingleAliasDispatch()
    {
        using var fixture = new Fixture();
        var runner = new LaunchRunner(fixture.Bytes, [Absent, "mResumedActivity: ActivityRecord{b u0 com.example.fixture/.RealMain t2}\n"]) { Alias = true };
        var result = await Client(runner, new Clock()).LaunchInspectedAppAsync("FIXTURE", fixture.Path);
        Assert.True(result.ComponentObservedResumed); Assert.True(result.LauncherIsActivityAlias);
        Assert.Equal("com.example.fixture/.LaunchAlias", result.Component);
        Assert.Equal("com.example.fixture/com.example.fixture.RealMain", result.LauncherTargetActivity);
        Assert.Equal(1, runner.Starts); Assert.Equal(2, runner.ActivityReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadbackFailureOrCancellationCannotConfirmOrRedispatch(bool cancelled)
    {
        using var fixture = new Fixture();
        var runner = new LaunchRunner(fixture.Bytes, [Absent])
        {
            ReadbackFailure = cancelled ? new OperationCanceledException() : new IOException("fixture readback failure")
        };
        var error = await Assert.ThrowsAsync<InspectedAppLaunchException>(() => Client(runner, new Clock()).LaunchInspectedAppAsync("FIXTURE", fixture.Path));
        Assert.Equal(InspectedAppLaunchStage.Readback, error.Stage); Assert.True(error.DispatchAttempted);
        Assert.Equal(1, runner.Starts); Assert.Equal(1, runner.ActivityReads);
    }

    [Fact]
    public async Task CallerCancellationDuringReadOnlyWaitCannotConfirmOrRedispatch()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        var runner = new LaunchRunner(fixture.Bytes, [Absent, Resumed]); var clock = new Clock { BeforeDelay = cancellation.Cancel };
        var error = await Assert.ThrowsAsync<InspectedAppLaunchException>(() => Client(runner, clock).LaunchInspectedAppAsync("FIXTURE", fixture.Path, cancellation.Token));
        Assert.Equal(InspectedAppLaunchStage.Readback, error.Stage); Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal(1, runner.Starts); Assert.Equal(1, runner.ActivityReads);
    }

    [Fact]
    public async Task FailedFreshCommandAndObservationArrivingAfterDeadlineRemainUnconfirmed()
    {
        using var fixture = new Fixture(); var failed = new LaunchRunner(fixture.Bytes, [Resumed]) { ReadbackExitCode = 1 };
        var error = await Assert.ThrowsAsync<InspectedAppLaunchException>(() => Client(failed, new Clock()).LaunchInspectedAppAsync("FIXTURE", fixture.Path));
        Assert.Equal(InspectedAppLaunchStage.Readback, error.Stage); Assert.Equal(1, failed.Starts);
        var clock = new Clock(); var late = new LaunchRunner(fixture.Bytes, [Resumed]) { BeforeReadback = () => clock.Ticks += TimeSpan.FromSeconds(6).Ticks };
        var result = await Client(late, clock).LaunchInspectedAppAsync("FIXTURE", fixture.Path);
        Assert.False(result.ComponentObservedResumed); Assert.Equal(1, late.Starts); Assert.Equal(1, late.ActivityReads);
    }

    [Fact]
    public async Task ObservationCountIsAlsoFiniteWhenInjectedClockDoesNotAdvance()
    {
        using var fixture = new Fixture(); var runner = new LaunchRunner(fixture.Bytes, [Absent]); var clock = new Clock { AdvanceOnDelay = false };
        var result = await Client(runner, clock).LaunchInspectedAppAsync("FIXTURE", fixture.Path);
        Assert.False(result.ComponentObservedResumed); Assert.Equal(1, runner.Starts); Assert.Equal(11, runner.ActivityReads);
    }

    private static AdbClient Client(LaunchRunner runner, Clock clock) => new("adb", runner, new("aapt2", "apksigner"), clock, clock.Delay);
    private sealed class Clock : TimeProvider
    {
        public long Ticks { get; set; }
        public bool AdvanceOnDelay { get; init; } = true;
        public Action? BeforeDelay { get; init; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public Task Delay(TimeSpan duration, CancellationToken cancellation)
        {
            BeforeDelay?.Invoke(); cancellation.ThrowIfCancellationRequested();
            if (AdvanceOnDelay) Ticks += duration.Ticks;
            return Task.CompletedTask;
        }
    }

    private const string Absent = "topResumedActivity=ActivityRecord{a u0 com.other.app/.Main t1}\n";
    private const string Resumed = "topResumedActivity=ActivityRecord{b u0 com.example.fixture/com.example.fixture.Main t2}\n";
    private sealed class Fixture : IDisposable
    {
        public byte[] Bytes { get; } = [0x50, 0x4b, 0x03, 0x04];
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qfm-launch-window-" + Guid.NewGuid().ToString("N") + ".apk");
        public Fixture() => File.WriteAllBytes(Path, Bytes);
        public void Dispose() => File.Delete(Path);
    }
    private sealed class LaunchRunner(byte[] apk, string[] activities) : IStreamingCommandRunner
    {
        public int Starts { get; private set; }
        public int ActivityReads { get; private set; }
        public bool Alias { get; init; }
        public Exception? ReadbackFailure { get; init; }
        public int ReadbackExitCode { get; init; }
        public Action? BeforeReadback { get; init; }
        public List<TimeSpan> ReadbackTimeouts { get; } = [];
        public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string output;
            if (file == "aapt2") output = "package: name='com.example.fixture' versionCode='1' versionName='1.0'\n";
            else if (file == "apksigner") output = "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n";
            else if (arguments.SequenceEqual(["-s", "FIXTURE", "shell", "pm path 'com.example.fixture'"])) output = "package:/data/app/example/base.apk\n";
            else if (arguments.Contains("query-activities")) output = Alias ? "com.example.fixture/.LaunchAlias\n" : "com.example.fixture/.Main\n";
            else if (arguments.SequenceEqual(["-s", "FIXTURE", "shell", "dumpsys", "package", "com.example.fixture"])) output = Alias
                ? "  Activity #0 ActivityInfo{a com.example.fixture/.LaunchAlias}\n    exported=true\n    isAlias=true\n    targetActivity=.RealMain\n"
                : "  Activity #0 ActivityInfo{a com.example.fixture/.Main}\n    exported=true\n";
            else if (arguments.SequenceEqual(["-s", "FIXTURE", "shell", "am", "start", "-n", Alias ? "com.example.fixture/.LaunchAlias" : "com.example.fixture/.Main"])) { Starts++; output = "Starting: Intent\n"; }
            else if (arguments.SequenceEqual(["-s", "FIXTURE", "shell", "dumpsys", "activity", "activities"]))
            {
                output = activities[Math.Min(ActivityReads, activities.Length - 1)]; ActivityReads++; ReadbackTimeouts.Add(timeout); BeforeReadback?.Invoke();
                if (ReadbackFailure is not null) throw ReadbackFailure;
                return Task.FromResult(new CommandResult(file, arguments, ReadbackExitCode, output, "", TimeSpan.Zero));
            }
            else throw new InvalidOperationException("Unexpected fixed command: " + string.Join(" ", arguments));
            return Task.FromResult(new CommandResult(file, arguments, 0, output, "", TimeSpan.Zero));
        }
        public async Task<StreamingCommandResult> RunToStreamAsync(string file, IReadOnlyList<string> arguments, Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var unsupported = arguments.Any(argument => argument.Contains("qfm-installed-digest:v1", StringComparison.Ordinal));
            var bytes = unsupported ? Encoding.UTF8.GetBytes("qfm-installed-digest:unsupported\n") : apk;
            await destination.WriteAsync(bytes, cancellationToken);
            return new(new(file, arguments, unsupported ? 90 : 0, "", "", TimeSpan.Zero), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}
