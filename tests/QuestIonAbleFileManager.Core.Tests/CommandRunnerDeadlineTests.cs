using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class CommandRunnerDeadlineTests
{
    [Theory]
    [InlineData("ordinary")]
    [InlineData("stream-in")]
    [InlineData("stream-out")]
    public async Task Deadline_CoversFinalPipeDrainAfterParentExit(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new HeldPipeFixture(mode);
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => Invoke(mode, fixture, TimeSpan.FromSeconds(3)));
        Assert.True(File.Exists(fixture.Marker), "The actual parent reached its exit boundary before the deadline.");
        var parentId = int.Parse(File.ReadAllText(fixture.Marker));
        Assert.True(HasExited(parentId), "This is a drain timeout, not a still-running parent timeout.");
        Assert.True(File.Exists(fixture.ChildReady), "The descendant actually opened its inherited pipes.");
        Assert.False(HasExited(fixture.ChildId), "The descendant still holds the pipes at the drain deadline.");
        fixture.AssertChildIdentity();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6), "The deadline must not await the child's eight-second natural exit.");
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("stream-in")]
    [InlineData("stream-out")]
    public async Task Deadline_PreservesExplicitCallerCancellationDuringDrain(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new HeldPipeFixture(mode);
        using var cancellation = new CancellationTokenSource();
        var invocation = Invoke(mode, fixture, TimeSpan.FromSeconds(15), cancellation.Token);
        await fixture.WaitForParentExitAsync(invocation);
        cancellation.CancelAfter(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.True(File.Exists(fixture.Marker));
        Assert.True(HasExited(int.Parse(File.ReadAllText(fixture.Marker))));
        Assert.False(HasExited(fixture.ChildId));
        fixture.AssertChildIdentity();
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("stream-in")]
    [InlineData("stream-out")]
    public async Task Deadline_NormalCompletionRetainsOutputAndTransferHash(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new CommandRunner();
        var arguments = Arguments("[Console]::Out.Write('ok'); [Console]::Error.Write('note')");
        if (mode == "ordinary")
        {
            var result = await runner.RunAsync(PowerShell, arguments, TimeSpan.FromSeconds(10));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("ok", result.StandardOutput);
            Assert.Equal("note", result.StandardError);
        }
        else
        {
            using var bytes = mode == "stream-in" ? new MemoryStream(Encoding.UTF8.GetBytes("input")) : new MemoryStream();
            var result = mode == "stream-in"
                ? await runner.RunFromStreamAsync(PowerShell, arguments, bytes, 32, TimeSpan.FromSeconds(10))
                : await runner.RunToStreamAsync(PowerShell, arguments, bytes, 32, TimeSpan.FromSeconds(10));
            Assert.Equal(0, result.CommandResult.ExitCode);
            Assert.Equal("note", result.CommandResult.StandardError);
            var expected = Encoding.UTF8.GetBytes(mode == "stream-in" ? "input" : "ok");
            Assert.Equal(expected.Length, result.BytesWritten);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), result.Sha256);
            if (mode == "stream-out") Assert.Equal(expected, bytes.ToArray());
            else Assert.Equal("ok", result.CommandResult.StandardOutput);
        }
    }

    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string[] Arguments(string script) => ["-NoProfile", "-NonInteractive", "-Command", script];
    private static async Task Invoke(string mode, HeldPipeFixture fixture, TimeSpan timeout, CancellationToken token = default)
    {
        var runner = new CommandRunner();
        using var bytes = new MemoryStream();
        if (mode == "ordinary") await runner.RunAsync(fixture.Executable, fixture.Arguments, timeout, token);
        else if (mode == "stream-in") await runner.RunFromStreamAsync(fixture.Executable, fixture.Arguments, bytes, 32, timeout, token);
        else await runner.RunToStreamAsync(fixture.Executable, fixture.Arguments, bytes, 32, timeout, token);
    }
    private static bool HasExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    private sealed class HeldPipeFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "qfm-drain-" + Guid.NewGuid().ToString("N"));
        public string Marker { get; }
        public string ChildReady { get; }
        public string Executable { get; }
        public string[] Arguments { get; }
        public int ChildId => int.Parse(File.ReadAllText(ChildReady).Split('|')[0]);
        public HeldPipeFixture(string mode)
        {
            Directory.CreateDirectory(directory);
            Marker = Path.Combine(directory, "parent-exited.txt");
            ChildReady = Path.Combine(directory, "child-ready.txt");
            Executable = Path.Combine(directory, "held-pipe.exe");
            Arguments = ["parent", mode, directory];
            // Compile before the measured invocation: nested PowerShell startup must not
            // consume the three-second drain prerequisite. This host owns only this fixture.
            var source = Path.Combine(directory, "held-pipe.cs");
            File.WriteAllText(source, HostSource);
            var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
            if (!File.Exists(compiler)) compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe");
            Assert.True(File.Exists(compiler), "The Windows .NET Framework compiler is required by this process fixture.");
            var info = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/nologo", "/target:exe", "/out:" + Executable, source }) info.ArgumentList.Add(argument);
            using var build = Process.Start(info)!;
            if (!build.WaitForExit(10000))
            {
                build.Kill();
                build.WaitForExit();
                throw new TimeoutException("The owned drain fixture did not compile.");
            }
            Assert.True(build.ExitCode == 0, build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd());
        }
        public async Task WaitForParentExitAsync(Task invocation)
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Marker) || !HasExited(int.Parse(File.ReadAllText(Marker))))
            {
                Assert.False(invocation.IsCompleted, "The fixture must enter actual pipe drain before caller cancellation.");
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "The fixture parent did not exit.");
                await Task.Delay(10);
            }
            Assert.True(File.Exists(ChildReady));
        }
        public void AssertChildIdentity()
        {
            var identity = File.ReadAllText(ChildReady).Split('|');
            using var child = Process.GetProcessById(int.Parse(identity[0]));
            Assert.Equal(long.Parse(identity[1]), child.StartTime.ToUniversalTime().Ticks);
        }
        public void Dispose()
        {
            // Release only this child's fixture file; never kill a PID that could be reused.
            File.WriteAllText(Path.Combine(directory, "release.txt"), "release");
            if (File.Exists(ChildReady))
            {
                var identity = File.ReadAllText(ChildReady).Split('|');
                try
                {
                    using var child = Process.GetProcessById(int.Parse(identity[0]));
                    if (child.StartTime.ToUniversalTime().Ticks == long.Parse(identity[1]))
                        Assert.True(child.WaitForExit(10000), "The released owned fixture child did not exit.");
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
        private const string HostSource = """
            using System;
            using System.Diagnostics;
            using System.IO;
            using System.Threading;
            class HeldPipeHost
            {
                static void Publish(string path, string value)
                {
                    File.WriteAllText(path + ".tmp", value);
                    File.Move(path + ".tmp", path);
                }
                static int Main(string[] args)
                {
                    string directory = args[2];
                    string ready = Path.Combine(directory, "child-ready.txt");
                    string release = Path.Combine(directory, "release.txt");
                    if (args[0] == "child")
                    {
                        if (!Console.IsOutputRedirected || !Console.IsErrorRedirected) return 2;
                        var self = Process.GetCurrentProcess();
                        Publish(ready, self.Id + "|" + self.StartTime.ToUniversalTime().Ticks);
                        var timer = Stopwatch.StartNew();
                        while (!File.Exists(release) && timer.ElapsedMilliseconds < 8000) Thread.Sleep(10);
                        return 0;
                    }
                    var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName);
                    info.UseShellExecute = false;
                    info.CreateNoWindow = true;
                    // Framework Process uses inherited standard handles when at least
                    // one stream is redirected. The unused stdin pipe enables inheritance
                    // of the parent's actual runner-owned stdout and stderr handles.
                    info.RedirectStandardInput = true;
                    // Stream-out has its own stdout transfer deadline; retain only stderr
                    // from the descendant, matching the original fixture's redirected stdout.
                    info.RedirectStandardOutput = args[1] == "stream-out";
                    info.Arguments = "child " + args[1] + " \"" + directory + "\"";
                    using (var child = Process.Start(info))
                    {
                        var timer = Stopwatch.StartNew();
                        while (!File.Exists(ready))
                        {
                            if (child.HasExited || timer.ElapsedMilliseconds >= 10000) return 3;
                            Thread.Sleep(10);
                        }
                        Publish(Path.Combine(directory, "parent-exited.txt"), Process.GetCurrentProcess().Id.ToString());
                    }
                    return 0;
                }
            }
            """;
    }
}
