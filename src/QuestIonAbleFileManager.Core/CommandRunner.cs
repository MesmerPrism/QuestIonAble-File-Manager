using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace QuestIonAbleFileManager.Core;

public sealed class CommandRunner : IStreamingCommandRunner, ISensitiveCommandRunner
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

    public async Task<SensitiveCommandResult<T>> RunSensitiveAsync<T>(
        string fileName,
        IReadOnlyList<string> arguments,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes,
        TimeSpan timeout,
        Func<ReadOnlyMemory<byte>, T> parseStandardOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumStandardOutputBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumStandardErrorBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(parseStandardOutput);

        var startInfo = CreateStartInfo(fileName, arguments);
        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        StartProcess(process, fileName);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var outputTask = ReadBoundedSensitiveBytesAsync(
            process.StandardOutput.BaseStream,
            maximumStandardOutputBytes,
            linkedSource.Token);
        var errorTask = ReadBoundedSensitiveBytesAsync(
            process.StandardError.BaseStream,
            maximumStandardErrorBytes,
            linkedSource.Token);
        byte[]? output = null;
        byte[]? error = null;
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            output = await outputTask.ConfigureAwait(false);
            error = await errorTask.ConfigureAwait(false);
            stopwatch.Stop();
            if (process.ExitCode != 0)
            {
                throw new SensitiveCommandException(
                    $"{Path.GetFileName(fileName)} rejected the sensitive request with exit code {process.ExitCode}; output was withheld.");
            }

            T value;
            try
            {
                value = parseStandardOutput(output);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new SensitiveCommandException(
                    $"{Path.GetFileName(fileName)} returned a malformed sensitive response; output was withheld.");
            }
            return new SensitiveCommandResult<T>(value, process.ExitCode, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            await ClearSensitiveTaskAsync(outputTask).ConfigureAwait(false);
            await ClearSensitiveTaskAsync(errorTask).ConfigureAwait(false);
            throw new TimeoutException(
                $"{Path.GetFileName(fileName)} timed out while handling a sensitive response; output was withheld.");
        }
        catch
        {
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            await ClearSensitiveTaskAsync(outputTask).ConfigureAwait(false);
            await ClearSensitiveTaskAsync(errorTask).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (output is not null)
            {
                CryptographicOperations.ZeroMemory(output);
            }
            if (error is not null)
            {
                CryptographicOperations.ZeroMemory(error);
            }
        }
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

    private static async Task<byte[]> ReadBoundedSensitiveBytesAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 4 * 1024));
        var buffer = new byte[Math.Min(StreamBufferBytes, maximumBytes + 1)];
        try
        {
            while (true)
            {
                var remainingWithSentinel = maximumBytes - checked((int)output.Length) + 1;
                var readLength = Math.Min(buffer.Length, remainingWithSentinel);
                var count = await stream.ReadAsync(
                    buffer.AsMemory(0, readLength),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    return output.ToArray();
                }
                if (output.Length + count > maximumBytes)
                {
                    throw new SensitiveCommandException(
                        "A sensitive process stream exceeded its fixed byte limit; output was withheld.");
                }
                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var segment) && segment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(segment.Array);
            }
        }
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

    private static async Task ClearSensitiveTaskAsync(Task<byte[]> task)
    {
        try
        {
            var value = await task.ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(value);
        }
        catch
        {
            // The primary process failure remains authoritative. The bounded reader clears its
            // internal buffer even when it cannot return a completed byte array.
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
