using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace QuestIonAbleFileManager.Core;

public sealed class CommandRunner : IStreamingCommandRunner
{
    private const int StreamBufferBytes = 64 * 1024;
    private const int MaximumStandardErrorCharacters = 64 * 1024;

    public async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start {fileName}.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not start {fileName}. Verify the configured ADB path.",
                exception);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var outputTask = process.StandardOutput.ReadToEndAsync(linkedSource.Token);
        var errorTask = process.StandardError.ReadToEndAsync(linkedSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{Path.GetFileName(fileName)} timed out after {timeout}.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var standardOutput = await outputTask.ConfigureAwait(false);
        var standardError = await errorTask.ConfigureAwait(false);
        stopwatch.Stop();

        return new CommandResult(
            fileName,
            arguments.ToArray(),
            process.ExitCode,
            standardOutput,
            standardError,
            stopwatch.Elapsed);
    }

    public async Task<StreamingCommandResult> RunToStreamAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Stream destination,
        long maximumBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The streaming destination must be writable.", nameof(destination));
        }

        var startInfo = CreateStartInfo(fileName, arguments);
        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        StartProcess(process, fileName);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var errorTask = ReadBoundedStandardErrorAsync(
            process.StandardError,
            linkedSource.Token);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StreamBufferBytes];
        long bytesWritten = 0;

        try
        {
            while (true)
            {
                var remainingWithSentinel = maximumBytes - bytesWritten + 1;
                var readLength = (int)Math.Min(buffer.Length, remainingWithSentinel);
                var count = await process.StandardOutput.BaseStream.ReadAsync(
                    buffer.AsMemory(0, readLength),
                    linkedSource.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                if (bytesWritten + count > maximumBytes)
                {
                    TryKill(process);
                    await WaitAfterKillAsync(process).ConfigureAwait(false);
                    throw new FleetTransferLimitException(maximumBytes);
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, count),
                    linkedSource.Token).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, count);
                bytesWritten += count;
            }

            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            await destination.FlushAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            await IgnoreFailureAsync(errorTask).ConfigureAwait(false);
            throw new TimeoutException($"{Path.GetFileName(fileName)} timed out after {timeout}.");
        }
        catch
        {
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            await IgnoreFailureAsync(errorTask).ConfigureAwait(false);
            throw;
        }

        var standardError = await errorTask.ConfigureAwait(false);
        stopwatch.Stop();
        var commandResult = new CommandResult(
            fileName,
            arguments.ToArray(),
            process.ExitCode,
            string.Empty,
            standardError,
            stopwatch.Elapsed);
        return new StreamingCommandResult(
            commandResult,
            bytesWritten,
            Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
    }

    public async Task<StreamingCommandResult> RunFromStreamAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Stream source,
        long maximumBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (!source.CanRead)
        {
            throw new ArgumentException("The streaming source must be readable.", nameof(source));
        }

        var startInfo = CreateStartInfo(fileName, arguments, redirectStandardInput: true);
        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        StartProcess(process, fileName);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var outputTask = ReadBoundedStandardErrorAsync(process.StandardOutput, linkedSource.Token);
        var errorTask = ReadBoundedStandardErrorAsync(process.StandardError, linkedSource.Token);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StreamBufferBytes];
        long bytesWritten = 0;

        try
        {
            while (true)
            {
                var remainingWithSentinel = maximumBytes - bytesWritten + 1;
                var readLength = (int)Math.Min(buffer.Length, remainingWithSentinel);
                var count = await source.ReadAsync(
                    buffer.AsMemory(0, readLength),
                    linkedSource.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                if (bytesWritten + count > maximumBytes)
                {
                    throw new FleetTransferLimitException(maximumBytes);
                }
                await process.StandardInput.BaseStream.WriteAsync(
                    buffer.AsMemory(0, count),
                    linkedSource.Token).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, count);
                bytesWritten += count;
            }
            await process.StandardInput.BaseStream.FlushAsync(linkedSource.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            await IgnoreFailureAsync(outputTask).ConfigureAwait(false);
            await IgnoreFailureAsync(errorTask).ConfigureAwait(false);
            throw new TimeoutException($"{Path.GetFileName(fileName)} timed out after {timeout}.");
        }
        catch
        {
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            await IgnoreFailureAsync(outputTask).ConfigureAwait(false);
            await IgnoreFailureAsync(errorTask).ConfigureAwait(false);
            throw;
        }

        var standardOutput = await outputTask.ConfigureAwait(false);
        var standardError = await errorTask.ConfigureAwait(false);
        stopwatch.Stop();
        return new StreamingCommandResult(
            new CommandResult(
                fileName,
                arguments.ToArray(),
                process.ExitCode,
                standardOutput,
                standardError,
                stopwatch.Elapsed),
            bytesWritten,
            Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
    }

    private static ProcessStartInfo CreateStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        bool redirectStandardInput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static void StartProcess(Process process, string fileName)
    {
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start {fileName}.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not start {fileName}. Verify the configured ADB path.",
                exception);
        }
    }

    private static async Task<string> ReadBoundedStandardErrorAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4 * 1024];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var available = MaximumStandardErrorCharacters - builder.Length;
            if (available > 0)
            {
                builder.Append(buffer, 0, Math.Min(available, count));
            }
            if (count > available)
            {
                truncated = true;
            }
        }
        if (truncated)
        {
            builder.AppendLine().Append("[standard error truncated]");
        }
        return builder.ToString();
    }

    private static async Task WaitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // The original limit, timeout, cancellation, or process failure remains authoritative.
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Process cleanup must not replace the bounded-transfer failure.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Cleanup must not hide the original timeout or cancellation.
        }
    }
}
