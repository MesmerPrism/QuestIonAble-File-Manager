using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    private const int MaximumDiagnosticProcessCount = 8;
    private const int DiagnosticLogLineCount = 400;
    private const int MaximumDiagnosticTextFileBytes = 256 * 1024;
    // Five fixed captures + eight PID corroborations + the manifest.
    private const int MaximumDiagnosticFileCount = 14;
    private static readonly UTF8Encoding DiagnosticUtf8 = new(false);
    private static readonly JsonSerializerOptions DiagnosticJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
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
            throw new FileNotFoundException("The APK to diagnose was not found.", reportedPath);
        if (!string.Equals(Path.GetExtension(reportedPath), ".apk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The diagnostic input must be an .apk file.", nameof(apkPath));

        var fullOutputDirectory = ValidateDiagnosticOutputDirectory(outputDirectory);
        using var admission = await ImmutableApkAdmission.CreateAsync(reportedPath, cancellationToken).ConfigureAwait(false);
        var artifact = await CreateApkInspector().InspectAsync(admission.Path, cancellationToken).ConfigureAwait(false);
        RejectSplitArtifact(artifact);
        var installed = await ReadInstalledIdentityAsync(serial, artifact, cancellationToken).ConfigureAwait(false);
        EnsureSameArtifact(artifact, installed);
        var runtime = await ObserveAdmittedAppAsync(
            serial, reportedPath, artifact, cancellationToken, installed).ConfigureAwait(false);

        var packageName = artifact.Identity.PackageName;
        var currentUserUid = await ReadCurrentUserPackageUidAsync(
            serial, packageName, cancellationToken).ConfigureAwait(false);
        var package = await RunForDeviceAsync(
            serial, ["shell", "dumpsys", "package", packageName], InspectionTimeout, cancellationToken).ConfigureAwait(false);
        var memory = await RunForDeviceAsync(
            serial, ["shell", "dumpsys", "meminfo", packageName], InspectionTimeout, cancellationToken).ConfigureAwait(false);
        var model = await ReadDiagnosticPropertyAsync(serial, "ro.product.model", cancellationToken).ConfigureAwait(false);
        var androidRelease = await ReadDiagnosticPropertyAsync(serial, "ro.build.version.release", cancellationToken).ConfigureAwait(false);
        var apiLevel = await ReadDiagnosticPropertyAsync(serial, "ro.build.version.sdk", cancellationToken).ConfigureAwait(false);
        var buildFingerprint = await ReadDiagnosticPropertyAsync(serial, "ro.build.fingerprint", cancellationToken).ConfigureAwait(false);

        var uidLogcat = await RunForDeviceAsync(
            serial,
            ["shell", "logcat", "-d", "-v", "threadtime", "-t",
             DiagnosticLogLineCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
             $"--uid={currentUserUid}"],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        var pidLogcat = new List<(int Pid, CommandResult Result)>();
        foreach (var pid in runtime.ProcessIds.Distinct().Order().Take(MaximumDiagnosticProcessCount))
        {
            var result = await RunForDeviceAsync(
                serial,
                ["shell", "logcat", "-d", "-v", "threadtime", "-t",
                 DiagnosticLogLineCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 $"--pid={pid}"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            pidLogcat.Add((pid, result));
        }

        // Logs are bound only if the inspected base APK still owns this package
        // after the bounded capture set has completed.
        var installedAfterCapture = await ReadInstalledIdentityAsync(
            serial, artifact, cancellationToken).ConfigureAwait(false);
        EnsureSameArtifact(artifact, installedAfterCapture);

        var capturedAt = DateTimeOffset.UtcNow;
        var device = new ApkDiagnosticDeviceFacts(
            BoundDiagnosticValue(model.StandardOutput),
            BoundDiagnosticValue(androidRelease.StandardOutput),
            BoundDiagnosticValue(apiLevel.StandardOutput),
            BoundDiagnosticValue(buildFingerprint.StandardOutput));
        var stagingDirectory = Path.Combine(
            Path.GetDirectoryName(fullOutputDirectory)!,
            $".{Path.GetFileName(fullOutputDirectory)}.qfm-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var files = new List<ApkDiagnosticBundleFile>
            {
                await WriteDiagnosticJsonAsync(
                    stagingDirectory, "runtime", "runtime.json", runtime, cancellationToken,
                    observationSource: "fixed package/activity/global-focus/pid observation",
                    commandSemantic: "fixed serial-scoped runtime and WindowManager focus observation; it has no application or OpenXR readiness authority").ConfigureAwait(false),
                await WriteDiagnosticJsonAsync(
                    stagingDirectory, "device", "device.json",
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
                    FirstFailure(model, androidRelease, apiLevel, buildFingerprint),
                    "fixed device properties",
                    "four fixed serial-scoped getprop readbacks").ConfigureAwait(false),
                await WriteDiagnosticCommandAsync(
                    stagingDirectory, "package", "package.txt", package,
                    "exact inspected package",
                    "fixed serial-scoped dumpsys package for the inspected package",
                    cancellationToken).ConfigureAwait(false),
                await WriteDiagnosticCommandAsync(
                    stagingDirectory, "memory", "meminfo.txt", memory,
                    "exact inspected package",
                    "fixed serial-scoped dumpsys meminfo for the inspected package",
                    cancellationToken).ConfigureAwait(false),
                await WriteDiagnosticCommandAsync(
                    stagingDirectory, "logcat_uid", $"logcat-uid-{currentUserUid}.txt", uidLogcat,
                    "current-user package UID",
                    "fixed serial-scoped recent UID-filtered logcat derived from current-user package readback",
                    cancellationToken).ConfigureAwait(false)
            };
            foreach (var (pid, result) in pidLogcat)
            {
                files.Add(await WriteDiagnosticCommandAsync(
                    stagingDirectory, "logcat_pid", $"logcat-pid-{pid}.txt", result,
                    "pidof corroboration",
                    "fixed serial-scoped recent PID-filtered logcat; optional corroboration only",
                    cancellationToken).ConfigureAwait(false));
            }
            if (files.Count + 1 > MaximumDiagnosticFileCount)
                throw new InvalidDataException("The fixed diagnostic capture set exceeded its file limit.");

            var normalizedArtifact = artifact with { Path = reportedPath };
            var manifest = new
            {
                schema = "questionable.file_manager.apk_diagnostic_manifest.v3",
                diagnosticContract = "questionable.file_manager.apk_diagnostic_bundle.v3",
                capturedAt,
                artifact = normalizedArtifact,
                installed = installedAfterCapture,
                runtime = new
                {
                    runtime.ObservationContract,
                    runtime.IsForeground,
                    runtime.IsTopResumed,
                    runtime.ProcessAlive,
                    runtime.ProcessIds,
                    runtime.ActivityObservationSource,
                    runtime.ProcessObservationSource,
                    runtime.ProcessObservationQuality,
                    runtime.ForegroundComponents,
                    runtime.TopResumedComponents,
                    runtime.BlockingSystemComponents,
                    runtime.CurrentFocus,
                    runtime.FocusedApp,
                    runtime.GlobalFocus,
                    runtime.ApplicationReadiness,
                    runtime.ApplicationReadinessAuthority,
                    runtime.OpenXrReadiness,
                    runtime.OpenXrReadinessAuthority
                },
                currentUserPackage = new
                {
                    userSelector = "current",
                    uid = currentUserUid,
                    source = "fixed serial-scoped pm list packages --user current -U for inspected package"
                },
                device,
                limits = new
                {
                    maximumProcessLogs = MaximumDiagnosticProcessCount,
                    recentLogLinesPerCapture = DiagnosticLogLineCount,
                    maximumTextFileBytes = MaximumDiagnosticTextFileBytes,
                    maximumFiles = MaximumDiagnosticFileCount
                },
                files,
                limitations = new
                {
                    applicationReadiness = "unknown",
                    applicationReadinessAuthority = false,
                    openXrReadiness = "unknown",
                    openXrReadinessAuthority = false,
                    wearerVisibility = "unknown",
                    panelPausedState = "unknown",
                    focusedOrSubmittedFrameStability = "unknown",
                    appOwnedHandoffMarkers = "unknown"
                }
            };
            files.Add(await WriteDiagnosticJsonAsync(
                stagingDirectory, "manifest", "diagnostic-manifest.json", manifest, cancellationToken,
                observationSource: "private diagnostic manifest",
                commandSemantic: "fixed diagnostic capture inventory and hashes").ConfigureAwait(false));
            Directory.Move(stagingDirectory, fullOutputDirectory);
            return new ApkDiagnosticBundleResult(
                normalizedArtifact, installedAfterCapture, runtime, device, fullOutputDirectory, capturedAt, files);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
    }

    private async Task<int> ReadCurrentUserPackageUidAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken)
    {
        var package = await RunForDeviceAsync(
            serial,
            ["shell", "pm", "list", "packages", "--user", "current", "-U", packageName],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        package.EnsureSuccess("Read current-user inspected package UID");
        var matches = Regex.Matches(
                package.StandardOutput,
                $@"(?m)^package:{Regex.Escape(packageName)}\s+uid:(?<uid>[0-9]+)\s*$",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();
        if (matches.Length != 1 ||
            !int.TryParse(
                matches[0].Groups["uid"].Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var uid) ||
            uid < 10_000)
        {
            throw new InvalidDataException(
                "Current-user package/UID readback was malformed, ambiguous, or did not identify an application UID.");
        }
        return uid;
    }

    private async Task<CommandResult> ReadDiagnosticPropertyAsync(
        string serial,
        string property,
        CancellationToken cancellationToken) =>
        await RunForDeviceAsync(serial, ["shell", "getprop", property], InspectionTimeout, cancellationToken)
            .ConfigureAwait(false);

    private static string ValidateDiagnosticOutputDirectory(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullPath = Path.GetFullPath(outputDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
            throw new ArgumentException("The diagnostic output must name a new non-root directory.", nameof(outputDirectory));
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The diagnostic output parent directory does not exist.");
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
            throw new ArgumentException("The diagnostic output path already exists; diagnostics never overwrite.", nameof(outputDirectory));
        return fullPath;
    }

    private static async Task<ApkDiagnosticBundleFile> WriteDiagnosticCommandAsync(
        string directory,
        string kind,
        string relativePath,
        CommandResult result,
        string observationSource,
        string commandSemantic,
        CancellationToken cancellationToken)
    {
        var content =
            $"exit_code={result.ExitCode}\n" +
            $"duration_milliseconds={(long)result.Duration.TotalMilliseconds}\n" +
            "[stdout]\n" + result.StandardOutput.ReplaceLineEndings("\n") +
            "\n[stderr]\n" + result.StandardError.ReplaceLineEndings("\n");
        var (bytes, truncated) = BoundDiagnosticText(content);
        return await WriteDiagnosticBytesAsync(
            directory, kind, relativePath, bytes, cancellationToken, result.ExitCode,
            observationSource, commandSemantic, truncated).ConfigureAwait(false);
    }

    private static async Task<ApkDiagnosticBundleFile> WriteDiagnosticJsonAsync<T>(
        string directory,
        string kind,
        string relativePath,
        T value,
        CancellationToken cancellationToken,
        int? commandExitCode = null,
        string? observationSource = null,
        string? commandSemantic = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, DiagnosticJson);
        if (bytes.LongLength > MaximumDiagnosticTextFileBytes)
            throw new InvalidDataException("A fixed diagnostic JSON file exceeded its byte limit.");
        return await WriteDiagnosticBytesAsync(
            directory, kind, relativePath, bytes, cancellationToken, commandExitCode,
            observationSource, commandSemantic, truncated: false).ConfigureAwait(false);
    }

    private static async Task<ApkDiagnosticBundleFile> WriteDiagnosticBytesAsync(
        string directory,
        string kind,
        string relativePath,
        byte[] bytes,
        CancellationToken cancellationToken,
        int? commandExitCode,
        string? observationSource,
        string? commandSemantic,
        bool truncated)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, relativePath), bytes, cancellationToken).ConfigureAwait(false);
        return new ApkDiagnosticBundleFile(
            kind, relativePath, bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), commandExitCode)
        {
            ObservationSource = observationSource,
            CommandSemantic = commandSemantic,
            Truncated = truncated
        };
    }

    private static (byte[] Bytes, bool Truncated) BoundDiagnosticText(string content)
    {
        var bytes = DiagnosticUtf8.GetBytes(content);
        if (bytes.Length <= MaximumDiagnosticTextFileBytes)
            return (bytes, false);

        const string marker = "\n[diagnostic capture truncated at fixed byte limit]\n";
        var markerBytes = DiagnosticUtf8.GetBytes(marker);
        var maximumPrefixBytes = MaximumDiagnosticTextFileBytes - markerBytes.Length;
        var prefixLength = Math.Min(content.Length, maximumPrefixBytes);
        while (prefixLength > 0 && DiagnosticUtf8.GetByteCount(content.AsSpan(0, prefixLength)) > maximumPrefixBytes)
            prefixLength--;
        return (DiagnosticUtf8.GetBytes(content[..prefixLength] + marker), true);
    }

    private static string BoundDiagnosticValue(string value)
    {
        var trimmed = value.Trim();
        var maximumBytes = MaximumDiagnosticTextFileBytes / 16;
        if (DiagnosticUtf8.GetByteCount(trimmed) <= maximumBytes)
            return trimmed;

        var prefixLimit = maximumBytes - 32;
        var length = Math.Min(trimmed.Length, prefixLimit);
        while (length > 0 && DiagnosticUtf8.GetByteCount(trimmed.AsSpan(0, length)) > prefixLimit)
            length--;
        return trimmed[..length] + " [truncated]";
    }

    private static int? FirstFailure(params CommandResult[] results) =>
        results.FirstOrDefault(static result => !result.Succeeded)?.ExitCode;
}
