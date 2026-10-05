using System.Diagnostics;
using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class ImmutableApkAdmissionConcurrencyTests
{
    [Fact]
    public async Task IndependentAdmissionsOverlapWhileTheirBytesRemainImmutable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var source = new Source();
        using var first = await ImmutableApkAdmission.CreateAsync(source.Path, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var second = await ImmutableApkAdmission.CreateAsync(source.Path, timeout.Token);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(Source.Bytes, await File.ReadAllBytesAsync(first.Path));
        Assert.Equal(Source.Bytes, await File.ReadAllBytesAsync(second.Path));
        Assert.ThrowsAny<Exception>(() => File.WriteAllBytes(first.Path, [9]));
        Assert.ThrowsAny<Exception>(() => Directory.Move(System.IO.Path.GetDirectoryName(first.Path)!, first.Path + ".moved"));
        second.Dispose();
        Assert.Equal(Source.Bytes, await File.ReadAllBytesAsync(first.Path));
    }

    [Fact]
    public async Task IndependentNativeProcessKeepsItsAdmissionWhileAnotherAdmits()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var source = new Source();
        static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = "$assembly=[Reflection.Assembly]::LoadFrom(" + Literal(typeof(AdbClient).Assembly.Location) + ");" +
            "$type=$assembly.GetType('QuestIonAbleFileManager.Core.ImmutableApkAdmission');" +
            "$method=$type.GetMethod('CreateAsync',[Reflection.BindingFlags]'Public,Static');" +
            "$task=$method.Invoke($null,@(" + Literal(source.Path) + ",[Threading.CancellationToken]::None));" +
            "$admission=$task.GetAwaiter().GetResult();try{" +
            "$path=$type.GetProperty('Path').GetValue($admission);[Console]::Out.WriteLine('READY:'+ $path);" +
            "[Console]::Out.Flush();[void][Console]::In.ReadLine()}finally{([IDisposable]$admission).Dispose()}";
        var start = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var child = Process.Start(start)!;
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.StartsWith("READY:", ready);
            var retainedPath = ready![6..];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var own = await ImmutableApkAdmission.CreateAsync(source.Path, timeout.Token);
            Assert.Equal(Source.Bytes, await File.ReadAllBytesAsync(retainedPath));
            Assert.NotEqual(retainedPath, own.Path);
            Assert.False(child.HasExited);
            own.Dispose();
            Assert.True(File.Exists(retainedPath));
            await child.StandardInput.WriteLineAsync("release");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, child.ExitCode);
            Assert.Equal("", await error);
            Assert.False(File.Exists(retainedPath));
        }
        finally
        {
            if (!child.HasExited)
            {
                await child.StandardInput.WriteLineAsync("release");
                if (!child.WaitForExit(15000)) child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
        }
    }

    [Fact]
    public async Task CleanupContentionLeavesReclaimableDebtWithoutMaskingResult()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var source = new Source();
        var admission = await ImmutableApkAdmission.CreateAsync(source.Path, CancellationToken.None);
        var retainedPath = admission.Path;
        var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(retainedPath))!;
        using (var otherOwner = new LocalApiArtifactStager(LocalApiStateSettings.CreateForTests(root)))
        {
            admission.Dispose();
            Assert.True(File.Exists(retainedPath));
        }
        using var next = await ImmutableApkAdmission.CreateAsync(source.Path, CancellationToken.None);
        Assert.False(File.Exists(retainedPath));
        Assert.Equal(Source.Bytes, await File.ReadAllBytesAsync(next.Path));
    }

    [Theory]
    [InlineData(2, "staged_file_capacity")]
    [InlineData(3, "staged_byte_capacity")]
    public async Task ActiveFilesRemainCountedAgainstClosedSharedCapacity(int maximumFiles, string code)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var source = new Source();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qfm-inventory-" + Guid.NewGuid().ToString("N"));
        var settings = LocalApiStateSettings.CreateForTests(root,
            new LocalApiStateLimits(MaximumStagedBytes: 8, MaximumStagedFiles: maximumFiles, MaximumSingleArtifactBytes: 4));
        try
        {
            using var first = new LocalApiArtifactStager(settings);
            using var live = await first.StageAsync(source.Path, CancellationToken.None);
            first.ReleaseTransientOwnerLease();
            using var second = new LocalApiArtifactStager(settings);
            second.CleanupTransientArtifacts();
            Assert.True(File.Exists(live.Path));
            using var alsoLive = await second.StageAsync(source.Path, CancellationToken.None);
            var failure = await Assert.ThrowsAsync<LocalApiException>(() => second.StageAsync(source.Path, CancellationToken.None));
            Assert.Equal(code, failure.Code);
            Assert.Equal("staged_cleanup_pending", Assert.Throws<LocalApiException>(second.CleanupOrphanedArtifacts).Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancelledAndFailedCopiesReleaseAdmissionExactlyOnce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var source = new Source();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImmutableApkAdmission.CreateAsync(source.Path, cancelled.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => ImmutableApkAdmission.CreateAsync(source.Path + ".missing", CancellationToken.None));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var next = await ImmutableApkAdmission.CreateAsync(source.Path, timeout.Token);
        Assert.Equal(Source.Bytes, await File.ReadAllBytesAsync(next.Path));
    }

    private sealed class Source : IDisposable
    {
        internal static readonly byte[] Bytes = [0x50, 0x4b, 0x03, 0x04];
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qfm-overlap-" + Guid.NewGuid().ToString("N") + ".apk");
        public Source() => File.WriteAllBytes(Path, Bytes);
        public void Dispose() => File.Delete(Path);
    }
}
