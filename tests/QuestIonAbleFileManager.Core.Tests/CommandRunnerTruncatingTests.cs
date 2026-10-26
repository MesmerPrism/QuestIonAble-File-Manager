using System.Diagnostics;
using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Command runner deadlines")]
public sealed class CommandRunnerTruncatingTests
{
    [Fact]
    public void ProductionRunnerHasTheOptionalTruncatingCaptureCapability() =>
        Assert.Contains(typeof(CommandRunner).GetInterfaces(), type => type.Name == "ITruncatingCommandRunner");

    [Fact]
    public async Task CaptureDrainsBothOverflowingStreamsAndRetainsActualNonzeroExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = "$bytes=New-Object byte[] (1024*1024); for($i=0;$i -lt $bytes.Length;$i++){$bytes[$i]=120}; " +
            "$out=[Console]::OpenStandardOutput(); $err=[Console]::OpenStandardError(); " +
            "$out.Write($bytes,0,$bytes.Length); $err.Write($bytes,0,$bytes.Length); exit 7";
        var result = await Run(script, TruncatingCommandResult.MaximumStreamBytes, TruncatingCommandResult.MaximumStreamBytes);
        Assert.Equal(7, result.CommandResult.ExitCode);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
        Assert.Equal(256 * 1024, Encoding.UTF8.GetByteCount(result.CommandResult.StandardOutput));
        Assert.Equal(256 * 1024, Encoding.UTF8.GetByteCount(result.CommandResult.StandardError));
        Assert.All(result.CommandResult.StandardOutput, character => Assert.Equal('x', character));
        Assert.All(result.CommandResult.StandardError, character => Assert.Equal('x', character));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactLimitIsCompleteAndOneExtraByteIsTruncated(bool overflow)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await Run("[Console]::Out.Write('12345678" + (overflow ? "9" : "") + "'); [Console]::Error.Write('note')", 8, 4);
        Assert.Equal("12345678", result.CommandResult.StandardOutput);
        Assert.Equal("note", result.CommandResult.StandardError);
        Assert.Equal(overflow, result.StandardOutputTruncated);
        Assert.False(result.StandardErrorTruncated);
        Assert.Equal(0, result.CommandResult.ExitCode);
    }

    [Fact]
    public async Task CutUtf8SequenceIsNotInventedAsAReplacementCharacter()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await Run("$b=[byte[]](65,226,130,172,66); [Console]::OpenStandardOutput().Write($b,0,$b.Length)", 3, 8);
        Assert.Equal("A", result.CommandResult.StandardOutput);
        Assert.True(result.StandardOutputTruncated);
        Assert.DoesNotContain('\uFFFD', result.CommandResult.StandardOutput);
    }

    [Fact]
    public async Task InvalidUtf8ReplacementCannotExpandPastTheRetainedByteLimit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await Run("$b=[byte[]](255,255); [Console]::OpenStandardOutput().Write($b,0,$b.Length)", 4, 8);
        Assert.True(Encoding.UTF8.GetByteCount(result.CommandResult.StandardOutput) <= 4);
        Assert.Equal("\uFFFD", result.CommandResult.StandardOutput);
        Assert.True(result.StandardOutputTruncated);
    }

    [Fact]
    public void AFirstEncodedCharacterLargerThanTheCapIsAnEmptyTruncatedPrefix()
    {
        var decoded = TruncatingCommandResult.DecodePrefix([255], truncated: false, maximumBytes: 1);
        Assert.Equal(string.Empty, decoded.Text); Assert.True(decoded.Truncated);
        var legacy = TruncatingCommandResult.FromLegacy(new CommandResult("fixture", [], 0, "€", "", TimeSpan.Zero), 1, 1);
        Assert.Equal(string.Empty, legacy.CommandResult.StandardOutput); Assert.True(legacy.StandardOutputTruncated);
    }

    [Fact]
    public async Task OrdinaryRunnerStillReturnsItsFullOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await new CommandRunner().RunAsync(PowerShell, Arguments("[Console]::Out.Write('123456789')"), TimeSpan.FromSeconds(10));
        Assert.Equal("123456789", result.StandardOutput);
    }

    [Fact]
    public async Task InvalidLimitsAndPrecancellationDoNotStartAProcess()
    {
        var runner = (ITruncatingCommandRunner)new CommandRunner();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunTruncatingAsync("nonexistent", [], 0, 1, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunTruncatingAsync("nonexistent", [], 1, 256 * 1024 + 1, TimeSpan.FromSeconds(1)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunTruncatingAsync("nonexistent", [], 1, 1, TimeSpan.FromSeconds(1), cancellation.Token));
    }

    private static Task<TruncatingCommandResult> Run(string script, int outputBytes, int errorBytes) =>
        ((ITruncatingCommandRunner)new CommandRunner()).RunTruncatingAsync(PowerShell, Arguments(script), outputBytes, errorBytes, TimeSpan.FromSeconds(15));
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string[] Arguments(string script) => ["-NoProfile", "-NonInteractive", "-Command", script];
}
