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
        await Assert.ThrowsAsync<TimeoutException>(() => Invoke(mode, fixture.Script, TimeSpan.FromSeconds(3)));
        Assert.True(File.Exists(fixture.Marker), "The actual parent reached its exit boundary before the deadline.");
        var parentId = int.Parse(File.ReadAllText(fixture.Marker));
        Assert.True(HasExited(parentId), "This is a drain timeout, not a still-running parent timeout.");
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
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(mode, fixture.Script, TimeSpan.FromSeconds(15), cancellation.Token));
        Assert.True(File.Exists(fixture.Marker));
        Assert.True(HasExited(int.Parse(File.ReadAllText(fixture.Marker))));
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
    private static async Task Invoke(string mode, string script, TimeSpan timeout, CancellationToken token = default)
    {
        var runner = new CommandRunner();
        using var bytes = new MemoryStream();
        if (mode == "ordinary") await runner.RunAsync(PowerShell, Arguments(script), timeout, token);
        else if (mode == "stream-in") await runner.RunFromStreamAsync(PowerShell, Arguments(script), bytes, 32, timeout, token);
        else await runner.RunToStreamAsync(PowerShell, Arguments(script), bytes, 32, timeout, token);
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
        public string Script { get; }
        public HeldPipeFixture(string mode)
        {
            Directory.CreateDirectory(directory);
            Marker = Path.Combine(directory, "parent-exited.txt");
            static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
            // The bounded child holds inherited pipes after the parent exits. Stream-out redirects
            // child stdout; this covers its existing transfer deadline (the baseline already passes).
            var redirect = mode == "stream-out" ? " -RedirectStandardOutput " + Literal(Path.Combine(directory, "child-out.txt")) : "";
            Script = "Start-Process -FilePath " + Literal(PowerShell) + " -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Milliseconds 8000' -NoNewWindow" + redirect + " | Out-Null; [IO.File]::WriteAllText(" + Literal(Marker) + ", [string]$PID)";
        }
        public void Dispose()
        {
            // The finite child may still own its redirected file; never kill unrelated processes.
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
