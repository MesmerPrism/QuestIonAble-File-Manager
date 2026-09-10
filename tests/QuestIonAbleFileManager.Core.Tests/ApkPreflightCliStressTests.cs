using System.Diagnostics;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class ApkPreflightCliStressTests
{
    [Fact]
    public async Task Preflight_MapsEmptyAdmissionToInputRejectedNoEffectJson()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "qfm-empty-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var apk = Path.Combine(temporary, "empty.apk");
        var adb = Path.Combine(temporary, "adb.exe");
        await File.WriteAllBytesAsync(apk, []);
        await File.WriteAllBytesAsync(adb, []);
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(2, await CliApplication.RunAsync(
                ["apk", "preflight", "--serial", "TEST123", "--file", apk, "--adb", adb, "--json"]));
        }
        finally
        {
            Console.SetOut(originalOut);
            Directory.Delete(temporary, recursive: true);
        }

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("input_rejected", json.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.False(json.RootElement.GetProperty("failure").GetProperty("state_change_possible").GetBoolean());
        Assert.DoesNotContain(temporary, output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Preflight_MapsOverLimitAdmissionToTypedNoEffectJson()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("QFM_RUN_APK_ADMISSION_STRESS"), "1", StringComparison.Ordinal))
            return;

        var cli = RequireEnvironment("QFM_APK_ADMISSION_CLI");
        var temporary = Path.Combine(Path.GetTempPath(), "qfm-admission-stress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var apk = Path.Combine(temporary, "over-limit.apk");
        var adb = Path.Combine(temporary, "adb.exe");
        await File.WriteAllBytesAsync(adb, []);
        try
        {
            await using (var stream = new FileStream(apk, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(LocalApiStateLimits.DefaultMaximumSingleArtifactBytes + 1);

            var result = await RunCliAsync(cli,
                "apk", "preflight", "--serial", "TEST123", "--file", apk, "--adb", adb, "--json");
            Assert.Equal(1, result.ExitCode);
            using var json = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("artifact_capacity_exceeded", json.RootElement.GetProperty("failure").GetProperty("code").GetString());
            Assert.False(json.RootElement.GetProperty("failure").GetProperty("state_change_possible").GetBoolean());
            Assert.DoesNotContain(temporary, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Inspect_StreamsTheConfiguredLargeArtifactThroughTheRealCli()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("QFM_RUN_APK_ADMISSION_STRESS"), "1", StringComparison.Ordinal))
            return;

        var cli = RequireEnvironment("QFM_APK_ADMISSION_CLI");
        var apk = RequireEnvironment("QFM_APK_ADMISSION_STRESS_APK");
        var adb = RequireEnvironment("QFM_APK_ADMISSION_STRESS_ADB");
        Assert.InRange(new FileInfo(apk).Length, 580_000_000, LocalApiStateLimits.DefaultMaximumSingleArtifactBytes);

        var result = await RunCliAsync(cli, "apk", "inspect", "--file", apk, "--adb", adb);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task DirectAdmission_CrashOrphanIsReclaimedBeforeTypedNoEffectPreflight()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("QFM_RUN_APK_ADMISSION_STRESS"), "1", StringComparison.Ordinal))
            return;

        var cli = RequireEnvironment("QFM_APK_ADMISSION_CLI");
        var apk = RequireEnvironment("QFM_APK_ADMISSION_STRESS_APK");
        var adb = RequireEnvironment("QFM_APK_ADMISSION_STRESS_ADB");
        var dotnet = RequireEnvironment("DOTNET_HOST_PATH");
        var parent = RequireDirectory("QFM_APK_ADMISSION_STRESS_ROOT");
        var root = Path.Combine(parent, "qfm-direct-admission-orphan-" + Guid.NewGuid().ToString("N"));
        var witness = Path.Combine(parent, "orphan-witness-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(root);
        Process? first = null;
        WindowsProcessJob? firstJob = null;
        Task<(string Text, bool Truncated)>? firstOutput = null;
        Task<(string Text, bool Truncated)>? firstError = null;
        var succeeded = false;
        try
        {
            firstJob = WindowsProcessJob.Create();
            first = StartCli(dotnet, cli, root, firstJob,
                "apk", "inspect", "--file", apk, "--adb", adb);
            firstOutput = ReadBoundedAsync(first.StandardOutput, CancellationToken.None);
            firstError = ReadBoundedAsync(first.StandardError, CancellationToken.None);
            var stageDirectory = Path.Combine(root, "QuestIonAbleFileManager.ApkAdmission", "staged");
            var staged = await WaitForPartialStagedApkAsync(
                first,
                stageDirectory,
                new FileInfo(apk).Length,
                TimeSpan.FromMinutes(2));
            var observedBytes = new FileInfo(staged).Length;
            var sourceBytes = new FileInfo(apk).Length;
            Assert.InRange(observedBytes, 1, sourceBytes - 1);

            Assert.False(first.HasExited, "Inspection exited before tree termination after the staged boundary.");
            firstJob.Terminate();
            using (var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                await WaitForStoppedAndEmptyAsync(first, firstJob, exitTimeout.Token);
            Assert.True(first.HasExited, "The terminated inspection process did not exit.");
            Assert.True(
                File.Exists(staged),
                $"Tree termination did not leave the observed staged-orphan witness after {observedBytes} of {sourceBytes} bytes were observed.");
            var orphanBytes = new FileInfo(staged).Length;
            await File.WriteAllTextAsync(
                witness,
                JsonSerializer.Serialize(new
                {
                    root,
                    staged,
                    sourceBytes,
                    observedBytes,
                    orphanExists = File.Exists(staged),
                    orphanBytes,
                    processExited = first.HasExited,
                    jobEmpty = true
                }));
            using (var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                await Task.WhenAll(firstOutput, firstError).WaitAsync(drainTimeout.Token);
            var firstStreams = await Task.WhenAll(firstOutput, firstError);
            Assert.All(firstStreams, stream => Assert.False(stream.Truncated, "Inspection output exceeded the 16 KiB test bound."));

            var empty = Path.Combine(root, "empty.apk");
            var placeholderAdb = Path.Combine(root, "adb.exe");
            await File.WriteAllBytesAsync(empty, []);
            await File.WriteAllBytesAsync(placeholderAdb, []);
            var followUp = await RunIsolatedCliAsync(
                cli,
                dotnet,
                root,
                "apk", "preflight", "--serial", "TEST123", "--file", empty,
                "--adb", placeholderAdb, "--json");

            Assert.Equal(2, followUp.ExitCode);
            Assert.False(followUp.StandardErrorTruncated, followUp.StandardError);
            Assert.False(followUp.StandardOutputTruncated, followUp.StandardOutput);
            Assert.True(string.IsNullOrEmpty(followUp.StandardError), followUp.StandardError);
            using var json = JsonDocument.Parse(followUp.StandardOutput);
            Assert.Equal("input_rejected", json.RootElement.GetProperty("failure").GetProperty("code").GetString());
            Assert.False(json.RootElement.GetProperty("failure").GetProperty("state_change_possible").GetBoolean());
            Assert.Empty(Directory.EnumerateFiles(stageDirectory, "*.apk"));
            succeeded = true;
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "failure.txt"),
                $"root={root}\ncli={cli}\napk={apk}\nadb={adb}\n" +
                $"{exception.GetType().FullName}: {exception.Message[..Math.Min(exception.Message.Length, 4096)]}");
            throw;
        }
        finally
        {
            try
            {
                if (firstJob is not null)
                {
                    firstJob.Terminate();
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    if (first is not null)
                        await WaitForStoppedAndEmptyAsync(first, firstJob, cleanupTimeout.Token);
                }
                if (firstOutput is not null && firstError is not null)
                {
                    using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await Task.WhenAll(firstOutput, firstError).WaitAsync(drainTimeout.Token);
                }
            }
            finally
            {
                first?.Dispose();
                firstJob?.Dispose();
                if (succeeded && Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string RequireEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && File.Exists(value)
            ? value
            : throw new InvalidOperationException($"Set {name} to the built QFM CLI artifact when opting into this stress test.");

    private static string RequireDirectory(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && Directory.Exists(value)
            ? value
            : throw new InvalidOperationException($"Set {name} to an existing private test-root directory when opting into this stress test.");

    private static async Task<(int ExitCode, string StandardOutput)> RunCliAsync(string cli, params string[] arguments)
    {
        var isDll = string.Equals(Path.GetExtension(cli), ".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo
        {
            FileName = isDll ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" : cli,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (isDll) start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the QFM CLI stress process.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var streams = await Task.WhenAll(outputTask, errorTask);
        var output = streams[0];
        var error = streams[1];
        Assert.True(string.IsNullOrEmpty(error), error);
        return (process.ExitCode, output);
    }

    private static Process StartCli(
        string dotnet,
        string cli,
        string temporaryRoot,
        WindowsProcessJob job,
        params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = dotnet,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["TEMP"] = temporaryRoot;
        start.Environment["TMP"] = temporaryRoot;
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the QFM CLI stress process.");
        try
        {
            job.Assign(process);
            return process;
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(30000);
            process.Dispose();
            throw;
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError, bool StandardOutputTruncated, bool StandardErrorTruncated)> RunIsolatedCliAsync(
        string cli,
        string dotnet,
        string temporaryRoot,
        params string[] arguments)
    {
        using var job = WindowsProcessJob.Create();
        using var process = StartCli(dotnet, cli, temporaryRoot, job, arguments);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var error = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await WaitForStoppedAndEmptyAsync(process, job, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            job.Terminate();
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await WaitForStoppedAndEmptyAsync(process, job, cleanupTimeout.Token);
            await Task.WhenAll(output, error).WaitAsync(cleanupTimeout.Token);
            throw;
        }
        var streams = await Task.WhenAll(output, error);
        return (process.ExitCode, streams[0].Text, streams[1].Text, streams[0].Truncated, streams[1].Truncated);
    }

    private static async Task WaitForStoppedAndEmptyAsync(
        Process process,
        WindowsProcessJob job,
        CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken);
        await job.WaitForEmptyAsync(cancellationToken);
    }

    private static async Task<string> WaitForPartialStagedApkAsync(
        Process process,
        string stageDirectory,
        long sourceBytes,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Directory.Exists(stageDirectory))
            {
                var staged = Directory.EnumerateFiles(stageDirectory, "*.apk").FirstOrDefault();
                if (staged is not null)
                {
                    var bytes = new FileInfo(staged).Length;
                    if (bytes > 0 && bytes < sourceBytes) return staged;
                    if (bytes >= sourceBytes)
                        throw new Xunit.Sdk.XunitException(
                            "Inspection completed immutable staging before the partial-copy termination boundary was observed.");
                }
            }
            if (process.HasExited)
                throw new Xunit.Sdk.XunitException("Inspection exited before a non-empty staged artifact was observed.");
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("A non-empty staged artifact was not observed before the bounded inspection deadline.");
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        const int maximumCharacters = 16 * 1024;
        var buffer = new char[1024];
        var output = new StringWriter();
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            var available = maximumCharacters - output.GetStringBuilder().Length;
            if (available > 0) output.Write(buffer, 0, Math.Min(available, read));
            truncated |= read > available;
        }
        return (output.ToString(), truncated);
    }
}
