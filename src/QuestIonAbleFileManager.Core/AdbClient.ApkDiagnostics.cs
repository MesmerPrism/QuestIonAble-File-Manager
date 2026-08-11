using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    private const int MaximumDiagnosticProcessCount = 8;
    private const int DiagnosticLogLineCount = 400;
    private static readonly UTF8Encoding DiagnosticUtf8 = new(false);
    private static readonly JsonSerializerOptions DiagnosticJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<ApkDiagnosticBundleResult> CaptureInspectedApkDiagnosticsAsync(
        string serial,
        string apkPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var reportedPath = Path.GetFullPath(apkPath);
        if (!File.Exists(reportedPath))
        {
            throw new FileNotFoundException("The APK to diagnose was not found.", reportedPath);
        }
        if (!string.Equals(Path.GetExtension(reportedPath), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The diagnostic input must be an .apk file.", nameof(apkPath));
        }

        var fullOutputDirectory = ValidateDiagnosticOutputDirectory(outputDirectory);
        using var admission = await ImmutableApkAdmission.CreateAsync(
            reportedPath,
            cancellationToken).ConfigureAwait(false);
        var artifact = await CreateApkInspector()
            .InspectAsync(admission.Path, cancellationToken).ConfigureAwait(false);
        RejectSplitArtifact(artifact);
        var installed = await ReadInstalledIdentityAsync(
            serial,
            artifact,
            cancellationToken).ConfigureAwait(false);
        EnsureSameArtifact(artifact, installed);
        var runtime = await ObserveAdmittedAppAsync(
            serial,
            reportedPath,
            artifact,
            cancellationToken,
            installed).ConfigureAwait(false);

        var packageName = artifact.Identity.PackageName;
        var package = await RunForDeviceAsync(
            serial,
            ["shell", "dumpsys", "package", packageName],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        var memory = await RunForDeviceAsync(
            serial,
            ["shell", "dumpsys", "meminfo", packageName],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        var model = await ReadDiagnosticPropertyAsync(
            serial, "ro.product.model", cancellationToken).ConfigureAwait(false);
        var androidRelease = await ReadDiagnosticPropertyAsync(
            serial, "ro.build.version.release", cancellationToken).ConfigureAwait(false);
        var apiLevel = await ReadDiagnosticPropertyAsync(
            serial, "ro.build.version.sdk", cancellationToken).ConfigureAwait(false);
        var buildFingerprint = await ReadDiagnosticPropertyAsync(
            serial, "ro.build.fingerprint", cancellationToken).ConfigureAwait(false);
        var logcat = new List<(int Pid, CommandResult Result)>();
        foreach (var pid in runtime.ProcessIds
                     .Distinct()
                     .Order()
                     .Take(MaximumDiagnosticProcessCount))
        {
            var result = await RunForDeviceAsync(
                serial,
                [
                    "shell", "logcat", "-d", "-t",
                    DiagnosticLogLineCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    $"--pid={pid}"
                ],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            logcat.Add((pid, result));
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var device = new ApkDiagnosticDeviceFacts(
            model.StandardOutput.Trim(),
            androidRelease.StandardOutput.Trim(),
            apiLevel.StandardOutput.Trim(),
            buildFingerprint.StandardOutput.Trim());
        var stagingDirectory = Path.Combine(
            Path.GetDirectoryName(fullOutputDirectory)!,
            $".{Path.GetFileName(fullOutputDirectory)}.qfm-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var files = new List<ApkDiagnosticBundleFile>
            {
                await WriteDiagnosticJsonAsync(
                    stagingDirectory,
                    "runtime",
                    "runtime.json",
                    runtime,
                    cancellationToken).ConfigureAwait(false),
                await WriteDiagnosticJsonAsync(
                    stagingDirectory,
                    "device",
                    "device.json",
                    new
                    {
                        facts = device,
                        commandExitCodes = new
                        {
                            model = model.ExitCode,
                            androidRelease = androidRelease.ExitCode,
                            apiLevel = apiLevel.ExitCode,
                            buildFingerprint = buildFingerprint.ExitCode
                        }
                    },
                    cancellationToken,
                    FirstFailure(model, androidRelease, apiLevel, buildFingerprint)).ConfigureAwait(false),
                await WriteDiagnosticCommandAsync(
                    stagingDirectory,
                    "package",
                    "package.txt",
                    package,
                    cancellationToken).ConfigureAwait(false),
                await WriteDiagnosticCommandAsync(
                    stagingDirectory,
                    "memory",
                    "meminfo.txt",
                    memory,
                    cancellationToken).ConfigureAwait(false)
            };
            foreach (var (pid, result) in logcat)
            {
                files.Add(await WriteDiagnosticCommandAsync(
                    stagingDirectory,
                    "logcat",
                    $"logcat-pid-{pid}.txt",
                    result,
                    cancellationToken).ConfigureAwait(false));
            }

            var normalizedArtifact = artifact with { Path = reportedPath };
            var manifest = new
            {
                schema = "questionable.file_manager.apk_diagnostic_manifest.v1",
                diagnosticContract = "questionable.file_manager.apk_diagnostic_bundle.v1",
                capturedAt,
                artifact = normalizedArtifact,
                installed,
                runtime = new
                {
                    runtime.ObservationContract,
                    runtime.IsForeground,
                    runtime.IsTopResumed,
                    runtime.ProcessAlive,
                    runtime.ProcessIds,
                    runtime.ForegroundComponents,
                    runtime.TopResumedComponents,
                    runtime.BlockingSystemComponents
                },
                device,
                processLogLimit = MaximumDiagnosticProcessCount,
                logLineLimitPerProcess = DiagnosticLogLineCount,
                files
            };
            files.Add(await WriteDiagnosticJsonAsync(
                stagingDirectory,
                "manifest",
                "diagnostic-manifest.json",
                manifest,
                cancellationToken).ConfigureAwait(false));
            Directory.Move(stagingDirectory, fullOutputDirectory);
            return new ApkDiagnosticBundleResult(
                normalizedArtifact,
                installed,
                runtime,
                device,
                fullOutputDirectory,
                capturedAt,
                files);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            throw;
        }
    }

    private async Task<CommandResult> ReadDiagnosticPropertyAsync(
        string serial,
        string property,
        CancellationToken cancellationToken) =>
        await RunForDeviceAsync(
            serial,
            ["shell", "getprop", property],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);

    private static string ValidateDiagnosticOutputDirectory(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullPath = Path.GetFullPath(outputDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) ||
            string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
        {
            throw new ArgumentException(
                "The diagnostic output must name a new non-root directory.",
                nameof(outputDirectory));
        }
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                "The diagnostic output parent directory does not exist.");
        }
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new ArgumentException(
                "The diagnostic output path already exists; diagnostics never overwrite.",
                nameof(outputDirectory));
        }
        return fullPath;
    }

    private static async Task<ApkDiagnosticBundleFile> WriteDiagnosticCommandAsync(
        string directory,
        string kind,
        string relativePath,
        CommandResult result,
        CancellationToken cancellationToken)
    {
        var content =
            $"exit_code={result.ExitCode}\n" +
            $"duration_milliseconds={(long)result.Duration.TotalMilliseconds}\n" +
            "[stdout]\n" + result.StandardOutput.ReplaceLineEndings("\n") +
            "\n[stderr]\n" + result.StandardError.ReplaceLineEndings("\n");
        return await WriteDiagnosticBytesAsync(
            directory,
            kind,
            relativePath,
            DiagnosticUtf8.GetBytes(content),
            cancellationToken,
            result.ExitCode).ConfigureAwait(false);
    }

    private static async Task<ApkDiagnosticBundleFile> WriteDiagnosticJsonAsync<T>(
        string directory,
        string kind,
        string relativePath,
        T value,
        CancellationToken cancellationToken,
        int? commandExitCode = null) =>
        await WriteDiagnosticBytesAsync(
            directory,
            kind,
            relativePath,
            JsonSerializer.SerializeToUtf8Bytes(value, DiagnosticJson),
            cancellationToken,
            commandExitCode).ConfigureAwait(false);

    private static async Task<ApkDiagnosticBundleFile> WriteDiagnosticBytesAsync(
        string directory,
        string kind,
        string relativePath,
        byte[] bytes,
        CancellationToken cancellationToken,
        int? commandExitCode)
    {
        await File.WriteAllBytesAsync(
            Path.Combine(directory, relativePath),
            bytes,
            cancellationToken).ConfigureAwait(false);
        return new ApkDiagnosticBundleFile(
            kind,
            relativePath,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            commandExitCode);
    }

    private static int? FirstFailure(params CommandResult[] results) =>
        results.FirstOrDefault(static result => !result.Succeeded)?.ExitCode;
}
