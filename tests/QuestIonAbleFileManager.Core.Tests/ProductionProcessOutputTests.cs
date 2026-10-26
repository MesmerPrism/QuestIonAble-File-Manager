using System.Diagnostics;
using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

// These real-process assertions share the existing isolation for redirected
// Windows pipes. Shell-engine startup is not the behavior under test: compile
// the closed producer during finite setup, before starting a runtime deadline.
[Collection("Command runner deadlines")]
public sealed class ProductionProcessOutputTests
{
    [Fact]
    public async Task ProductionRunnerPreservesBoundedDigestBytesEvenOnUnsupportedExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OwnedOutputFixture();
        using var destination = new MemoryStream();
        StreamingCommandResult? result = null;
        var timer = Stopwatch.StartNew();
        var failure = await Record.ExceptionAsync(async () => result = await new CommandRunner().RunToStreamAsync(
            fixture.Executable, ["digest-unsupported", fixture.DirectoryPath], destination, 4096, TimeSpan.FromSeconds(15)));
        Assert.True(failure is null, fixture.Describe("digest exit/read", timer.Elapsed, failure));
        Assert.NotNull(result);
        Assert.Equal(90, result.CommandResult.ExitCode);
        Assert.Equal("", result.CommandResult.StandardError);
        Assert.Equal("qfm-installed-digest:unsupported\n", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(destination.Length, result.BytesWritten);
    }

    [Fact]
    public async Task ProductionRunnerStopsDigestOutputAtIts4096ByteBound()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OwnedOutputFixture();
        using var destination = new MemoryStream();
        var timer = Stopwatch.StartNew();
        var failure = await Record.ExceptionAsync(() => new CommandRunner().RunToStreamAsync(
            fixture.Executable, ["digest-overflow", fixture.DirectoryPath], destination, 4096, TimeSpan.FromSeconds(15)));
        Assert.True(failure?.GetType() == typeof(FleetTransferLimitException), fixture.Describe("digest output limit", timer.Elapsed, failure));
        var error = Assert.IsType<FleetTransferLimitException>(failure);
        Assert.Equal(4096, error.MaximumBytes);
        Assert.True(destination.Length <= 4096);
    }

    [Fact]
    public async Task OversizedProcessOutputKillsPromptlyAndWorkingDirectoryIsFixed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new OwnedOutputFixture();
        using var runCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var startup = Stopwatch.StartNew();
        var runTask = FleetInstallerProcessRunner.RunProcessAsync(
            fixture.Executable, ["installer-overflow", fixture.DirectoryPath], TimeSpan.FromSeconds(30),
            visible: false, runCancellation.Token);
        try
        {
            var startupFailure = await Record.ExceptionAsync(async () =>
            {
                using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (!File.Exists(fixture.Started) && !runTask.IsCompleted)
                    await Task.Delay(25, startupTimeout.Token);
            });
            Assert.True(startupFailure is null && File.Exists(fixture.Started), fixture.Describe("installer startup", startup.Elapsed, startupFailure ?? runTask.Exception?.GetBaseException()));
            var stopwatch = Stopwatch.StartNew();
            var failure = await Record.ExceptionAsync(() => runTask);
            Assert.True(failure?.GetType() == typeof(FleetInstallerException), fixture.Describe("installer output limit/drain", stopwatch.Elapsed, failure));
            Assert.Equal("fleet_installer_output_oversized", Assert.IsType<FleetInstallerException>(failure).Code);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), fixture.Describe("installer prompt kill", stopwatch.Elapsed, failure));
        }
        finally
        {
            runCancellation.Cancel();
            try { await runTask; }
            catch { /* The asserted output failure, timeout, or cancellation is expected. */ }
        }

        Assert.NotEqual(Environment.CurrentDirectory, fixture.DirectoryPath);
        CommandResult? result = null;
        var cwdTimer = Stopwatch.StartNew();
        var cwdFailure = await Record.ExceptionAsync(async () => result = await FleetInstallerProcessRunner.RunProcessAsync(
            fixture.Executable, ["cwd", fixture.DirectoryPath], TimeSpan.FromSeconds(5), visible: false, CancellationToken.None));
        Assert.True(cwdFailure is null, fixture.Describe("installer working directory", cwdTimer.Elapsed, cwdFailure));
        Assert.NotNull(result);
        Assert.Equal(fixture.DirectoryPath, result.StandardOutput.Trim(), ignoreCase: true);
    }

    private sealed class OwnedOutputFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "qfm-process-output-" + Guid.NewGuid().ToString("N"));
        public string Executable { get; }
        public string Started => Path.Combine(DirectoryPath, "started.txt");
        private readonly TimeSpan compilationElapsed;

        public OwnedOutputFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            Executable = Path.Combine(DirectoryPath, "output-producer.exe");
            try
            {
                var source = Path.Combine(DirectoryPath, "output-producer.cs");
                File.WriteAllText(source, HostSource);
                var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
                if (!File.Exists(compiler)) compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
                Assert.True(File.Exists(compiler), "Process fixture setup requires the Windows .NET Framework compiler.");
                var info = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "/nologo", "/target:exe", "/out:" + Executable, source }) info.ArgumentList.Add(argument);
                var setup = Stopwatch.StartNew();
                using var build = Process.Start(info)!;
                if (!build.WaitForExit(60000))
                {
                    build.Kill();
                    Assert.True(build.WaitForExit(5000), "The owned compiler did not exit after its setup deadline.");
                    Assert.Fail("Process fixture setup compilation exceeded its one-minute budget.");
                }
                compilationElapsed = setup.Elapsed;
                Assert.True(build.ExitCode == 0, "Process fixture setup compilation failed: " + build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd());
            }
            catch { Dispose(); throw; }
        }

        public string Describe(string phase, TimeSpan elapsed, Exception? failure)
        {
            var producer = File.Exists(Path.Combine(DirectoryPath, "written.txt")) ? "payload-written" : File.Exists(Started) ? "started-no-write-completion-marker" : "not-started";
            return $"Process fixture phase={phase}; producer={producer}; compilation={compilationElapsed}; runtime={elapsed}; failure={failure?.GetType().Name ?? "none"}.";
        }

        public void Dispose()
        {
            // Invocations above settle before disposal. Windows can retain a
            // short executable lock after exit; keep that owned scratch if so.
            try { Directory.Delete(DirectoryPath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private const string HostSource = """
            using System;
            using System.IO;
            using System.Text;
            using System.Threading;
            class OutputProducer
            {
                static void Mark(string directory, string name)
                {
                    File.WriteAllText(Path.Combine(directory, name), "ready");
                }
                static int Main(string[] args)
                {
                    if (args.Length != 2) return 2;
                    string mode = args[0], directory = args[1];
                    if (mode == "cwd") { Console.Out.Write(Environment.CurrentDirectory); return 0; }
                    if (mode != "digest-unsupported" && mode != "digest-overflow" && mode != "installer-overflow") return 2;
                    Mark(directory, "started.txt");
                    byte[] bytes = mode == "digest-unsupported" ? Encoding.UTF8.GetBytes("qfm-installed-digest:unsupported\n") : Encoding.ASCII.GetBytes(new string('x', mode == "digest-overflow" ? 4097 : 70000));
                    using (var output = Console.OpenStandardOutput()) { output.Write(bytes, 0, bytes.Length); output.Flush(); }
                    Mark(directory, "written.txt");
                    if (mode == "installer-overflow") Thread.Sleep(60000);
                    return mode == "digest-unsupported" ? 90 : 0;
                }
            }
            """;
    }
}
