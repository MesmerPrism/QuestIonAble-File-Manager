using System.Collections.ObjectModel;

namespace QuestIonAbleFileManager.Core;

public sealed record CommandResult(
    string FileName,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;

    public string CondensedOutput
    {
        get
        {
            var output = string.Join(
                Environment.NewLine,
                new[] { StandardError.Trim(), StandardOutput.Trim() }
                    .Where(static value => value.Length > 0));
            return output.Length == 0 ? $"Command exited with code {ExitCode}." : output;
        }
    }

    public CommandResult EnsureSuccess(string operation)
    {
        if (!Succeeded)
        {
            throw new AdbCommandException(operation, this);
        }

        return this;
    }
}

public sealed record StreamingCommandResult(
    CommandResult CommandResult,
    long BytesWritten,
    string Sha256);

public sealed record QuestDevice(
    string Serial,
    string State,
    string? Model,
    string? Product)
{
    public bool IsReady => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    public bool IsWifiConnection => AndroidInput.TryParseWifiEndpoint(Serial, out _, out _);

    public string DisplayName
    {
        get
        {
            var transport = IsWifiConnection ? "Wi-Fi" : "USB";
            var label = string.IsNullOrWhiteSpace(Model) ? Serial : $"{Model} — {Serial}";
            label = $"{label} [{transport}]";
            return IsReady ? label : $"{label} ({State})";
        }
    }
}

public sealed record RemoteEntry(string Name, string FullPath, bool IsDirectory)
{
    public string TypeLabel => IsDirectory ? "Folder" : "File";
}

public sealed record QuestPackage(string PackageName, IReadOnlyList<string> ApkPaths)
{
    public bool IsSplitPackage => ApkPaths.Count > 1;
}

public sealed record ApkInstallOptions(
    bool ReplaceExisting = true,
    bool AllowDowngrade = false,
    bool GrantRuntimePermissions = false,
    bool AllowTestPackages = false);

public sealed record ApkExportResult(
    string PackageName,
    string SourcePath,
    string OutputPath,
    string ChecksumPath,
    string Sha256,
    long SizeBytes);

public sealed record ApkBundleInstallResult(
    IReadOnlyList<string> ApkPaths,
    CommandResult CommandResult);

public sealed record WifiAdbConnectionResult(
    string Host,
    int Port,
    string Endpoint,
    CommandResult CommandResult,
    QuestDevice Device);

public sealed record WifiAdbEnableResult(
    string UsbSerial,
    string Host,
    int Port,
    string Endpoint,
    CommandResult AddressProbe,
    CommandResult TcpIpCommand,
    WifiAdbConnectionResult Connection);

public sealed record TargetApkInstallResult(
    string Serial,
    CommandResult? CommandResult,
    string? Error)
{
    public bool Succeeded => CommandResult?.Succeeded == true && string.IsNullOrWhiteSpace(Error);

    public string Summary => Succeeded
        ? "Installed successfully."
        : !string.IsNullOrWhiteSpace(Error)
            ? Error
            : CommandResult?.CondensedOutput ?? "Installation did not return a result.";
}

public sealed record ParallelApkInstallResult(
    IReadOnlyList<string> ApkPaths,
    int MaxParallelism,
    IReadOnlyList<TargetApkInstallResult> Targets)
{
    public int SucceededCount => Targets.Count(static target => target.Succeeded);

    public int FailedCount => Targets.Count - SucceededCount;

    public bool Succeeded => Targets.Count > 0 && FailedCount == 0;
}

public sealed record OperatorProgress(
    string Stage,
    string Message,
    int CompletedUnits,
    int TotalUnits)
{
    public bool IsIndeterminate => TotalUnits <= 0;

    public double Percentage => IsIndeterminate
        ? 0
        : Math.Clamp(CompletedUnits * 100d / TotalUnits, 0, 100);
}

public sealed class AdbCommandException : InvalidOperationException
{
    public AdbCommandException(string operation, CommandResult result)
        : base($"{operation} failed: {result.CondensedOutput}")
    {
        Result = result;
    }

    public CommandResult Result { get; }
}

public sealed class SplitPackageException : InvalidOperationException
{
    public SplitPackageException(string packageName, IReadOnlyList<string> apkPaths)
        : base(
            $"{packageName} is installed as {apkPaths.Count} APK parts. " +
            "Single-APK export was refused so the backup cannot be incomplete.")
    {
        PackageName = packageName;
        ApkPaths = new ReadOnlyCollection<string>(apkPaths.ToArray());
    }

    public string PackageName { get; }

    public IReadOnlyList<string> ApkPaths { get; }
}

public sealed class FleetTransferLimitException : InvalidOperationException
{
    public FleetTransferLimitException(long maximumBytes)
        : base($"The remote file exceeded the hard transfer limit of {maximumBytes} bytes.")
    {
        MaximumBytes = maximumBytes;
    }

    public long MaximumBytes { get; }
}

public sealed class FleetRemotePathException : InvalidOperationException
{
    public FleetRemotePathException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
