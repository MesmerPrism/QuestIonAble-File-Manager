using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    private const int MaximumLaunchDiagnosticBytes = 256 * 1024;
    private static readonly TimeSpan LaunchDiagnosticPostActionWindow = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding LaunchDiagnosticUtf8 = new(false, true);
    private static readonly JsonSerializerOptions LaunchDiagnosticJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public Task<ApkLaunchDiagnosticBundleResult> LaunchAndCaptureInspectedApkAsync(
        string serial,
        string apkPath,
        string outputDirectory,
        CancellationToken cancellationToken = default) =>
        LaunchAndCaptureInspectedApkAsync(
            serial,
            apkPath,
            outputDirectory,
            dispatchObserver: null,
            cancellationToken);

    internal async Task<ApkLaunchDiagnosticBundleResult> LaunchAndCaptureInspectedApkAsync(
        string serial,
        string apkPath,
        string outputDirectory,
        Action? dispatchObserver,
        CancellationToken cancellationToken)
    {
        serial = AndroidInput.RequireSerial(serial);
        var reportedPath = Path.GetFullPath(apkPath);
        if (!File.Exists(reportedPath))
            throw new FileNotFoundException("The APK to launch-diagnose was not found.", reportedPath);
        if (!string.Equals(Path.GetExtension(reportedPath), ".apk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The launch-diagnostic input must be an .apk file.", nameof(apkPath));
        var fullOutputDirectory = ValidateLaunchDiagnosticOutputDirectory(outputDirectory);
        if (_runner is not IArmedCaptureCommandRunner armedRunner)
        {
            throw new NotSupportedException(
                "The configured command runner does not support a pre-armed bounded capture.");
        }

        using var admission = await ImmutableApkAdmission.CreateAsync(
            reportedPath,
            cancellationToken).ConfigureAwait(false);
        var artifact = await CreateApkInspector().InspectAsync(
            admission.Path,
            cancellationToken).ConfigureAwait(false);
        RejectSplitArtifact(artifact);
        await RequireExactReadySerialAsync(serial, cancellationToken).ConfigureAwait(false);
        var installed = await ReadInstalledIdentityAsync(serial, artifact, cancellationToken).ConfigureAwait(false);
        EnsureExactStandaloneInstalledArtifact(artifact, installed);
        var uid = await ReadUniqueCurrentUserPackageUidAsync(
            serial,
            artifact.Identity.PackageName,
            cancellationToken).ConfigureAwait(false);
        var launcher = await ResolveExportedLauncherAsync(
            serial,
            artifact.Identity.PackageName,
            cancellationToken).ConfigureAwait(false);
        var deviceFence = await ReadLaunchDiagnosticDeviceFenceAsync(
            serial,
            cancellationToken).ConfigureAwait(false);
        var hostFence = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var hostFenceAt = DateTimeOffset.UtcNow;

        var deviceEffectPossible = false;
        var stagingDirectory = Path.Combine(
            Path.GetDirectoryName(fullOutputDirectory)!,
            $".{Path.GetFileName(fullOutputDirectory)}.qfm-launch-diagnostic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            const string logRelativePath = "logcat-uid-post-fence.txt";
            var logPath = Path.Combine(stagingDirectory, logRelativePath);
            ArmedCaptureCommandResult<ApkLaunchDiagnosticAttempt> capture;
            await using (var logStream = new FileStream(
                logPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                capture = await armedRunner.RunArmedCaptureAsync(
                    AdbPath,
                    [
                        "-s", serial, "shell", "logcat", "-v", "epoch",
                        "-T", deviceFence, $"--uid={uid.ToString(CultureInfo.InvariantCulture)}"
                    ],
                    logStream,
                    MaximumLaunchDiagnosticBytes,
                    LaunchDiagnosticPostActionWindow,
                    async actionToken => await DispatchLaunchDiagnosticAsync(
                        serial,
                        reportedPath,
                        artifact,
                        launcher,
                        () =>
                        {
                            deviceEffectPossible = true;
                            dispatchObserver?.Invoke();
                        },
                        actionToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                await logStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var launchAttempt = capture.ActionResult;
            InstalledApkIdentity? installedAfter = null;
            int? uidAfter = null;
            string? postReadbackFailure = null;
            try
            {
                installedAfter = await ReadInstalledIdentityAsync(
                    serial,
                    artifact,
                    CancellationToken.None).ConfigureAwait(false);
                EnsureExactStandaloneInstalledArtifact(artifact, installedAfter);
                uidAfter = await ReadUniqueCurrentUserPackageUidAsync(
                    serial,
                    artifact.Identity.PackageName,
                    CancellationToken.None).ConfigureAwait(false);
                if (uidAfter != uid)
                    throw new InvalidDataException("The current-user package UID changed during launch diagnostics.");
                launchAttempt = launchAttempt with
                {
                    CurrentPackageProcessIds = await ReadLaunchDiagnosticPackagePidsAsync(
                        serial,
                        artifact.Identity.PackageName,
                        uid,
                        CancellationToken.None).ConfigureAwait(false)
                };
            }
            catch (Exception exception)
            {
                postReadbackFailure = ClassifyLaunchDiagnosticException(exception).Message;
            }

            var captureFacts = new ApkLaunchDiagnosticCapture(
                logRelativePath,
                capture.BytesWritten,
                capture.Sha256,
                capture.PostActionWindowElapsed,
                capture.OutputLimitReached,
                capture.CaptureExitedEarly,
                capture.ProcessTreeCleanupSucceeded,
                capture.CommandResult.ExitCode);
            var (effectDisposition, effectDispositionDetail) = ClassifyLaunchDiagnosticDisposition(
                launchAttempt,
                captureFacts,
                installedAfter,
                uidAfter,
                postReadbackFailure);
            var normalizedArtifact = artifact with { Path = reportedPath };
            const string manifestRelativePath = "launch-diagnostic-manifest.json";
            var manifestValue = new
            {
                schema = "questionable.file_manager.apk_launch_diagnostic_manifest.v1",
                diagnosticContract = "questionable.file_manager.apk_launch_diagnostic_bundle.v1",
                hostFence = new { id = hostFence, createdAt = hostFenceAt },
                deviceFence = new
                {
                    epoch = deviceFence,
                    source = "fixed serial-scoped device UTC epoch readback",
                    logSelection = "fixed epoch output at or after fence, filtered to derived current-user UID"
                },
                artifact = normalizedArtifact,
                installedBeforeDispatch = installed,
                installedAfterCapture = installedAfter,
                currentUserUidBeforeDispatch = uid,
                currentUserUidAfterCapture = uidAfter,
                launch = launchAttempt,
                capture = captureFacts,
                effectDisposition,
                effectDispositionDetail,
                postReadbackFailure,
                limitations = new
                {
                    applicationReadiness = "unknown",
                    openXrReadiness = "unknown",
                    wearerVisibility = "unknown",
                    screenshotOrRecording = false,
                    genericLogFilter = false,
                    retryPerformed = false
                }
            };
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifestValue,
                LaunchDiagnosticJson);
            var manifestSha = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            await WriteLaunchDiagnosticCreateNewAsync(
                Path.Combine(stagingDirectory, manifestRelativePath),
                manifestBytes).ConfigureAwait(false);
            var disposition = effectDisposition;
            var dispositionDetail = effectDispositionDetail;
            var publishedAtRequestedPath = true;
            var actualOutputDirectory = fullOutputDirectory;
            try
            {
                Directory.Move(stagingDirectory, fullOutputDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                publishedAtRequestedPath = false;
                actualOutputDirectory = stagingDirectory;
                disposition = ApkLaunchDiagnosticDisposition.OutcomeUnknown;
                dispositionDetail =
                    "The launch evidence was retained in a collision-safe sibling, but publication at the requested no-overwrite path was not confirmed.";
            }
            return new ApkLaunchDiagnosticBundleResult(
                normalizedArtifact,
                installed,
                installedAfter,
                uid,
                uidAfter,
                hostFence,
                hostFenceAt,
                deviceFence,
                launchAttempt,
                captureFacts,
                disposition,
                dispositionDetail,
                actualOutputDirectory,
                manifestRelativePath,
                manifestBytes.LongLength,
                manifestSha,
                publishedAtRequestedPath,
                Path.GetFileName(actualOutputDirectory));
        }
        catch
        {
            if (!deviceEffectPossible && Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
    }

    private async Task<ApkLaunchDiagnosticAttempt> DispatchLaunchDiagnosticAsync(
        string serial,
        string reportedPath,
        ApkArtifactInspection artifact,
        ResolvedLauncherComponent launcher,
        Action markDeviceEffectPossible,
        CancellationToken cancellationToken)
    {
        var dispatchAttempted = false;
        try
        {
            await RequireExactReadySerialAsync(serial, cancellationToken).ConfigureAwait(false);
            var installed = await ReadInstalledIdentityAsync(serial, artifact, cancellationToken).ConfigureAwait(false);
            EnsureExactStandaloneInstalledArtifact(artifact, installed);
            dispatchAttempted = true;
            markDeviceEffectPossible();
            var start = await RunForDeviceAsync(
                serial,
                ["shell", "am", "start", "-n", launcher.Wire],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!start.Succeeded)
            {
                return new ApkLaunchDiagnosticAttempt(
                    true,
                    null,
                    "launcher_dispatch_rejected",
                    "The fixed resolved launcher command returned a nonzero exit code.",
                    []);
            }
            var activities = await RunForDeviceAsync(
                serial,
                ["shell", "dumpsys", "activity", "activities"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            activities.EnsureSuccess("Read back launched activity");
            var observed = activities.StandardOutput.ReplaceLineEndings("\n").Split('\n')
                .Any(line =>
                    (line.Contains("mResumedActivity", StringComparison.Ordinal) ||
                     line.Contains("topResumedActivity", StringComparison.OrdinalIgnoreCase)) &&
                    TryReadRuntimeComponent(line, out var observedComponent) &&
                    (string.Equals(observedComponent, launcher.Canonical, StringComparison.Ordinal) ||
                     (launcher.IsActivityAlias &&
                      string.Equals(observedComponent, launcher.TargetActivity, StringComparison.Ordinal))));
            var launch = new ResolvedAppLaunchResult(
                artifact with { Path = reportedPath },
                installed,
                launcher.Wire,
                start,
                observed)
            {
                LauncherIsActivityAlias = launcher.IsActivityAlias,
                LauncherTargetActivity = launcher.TargetActivity
            };
            return new ApkLaunchDiagnosticAttempt(true, launch, null, null, []);
        }
        catch (Exception exception)
        {
            var failure = ClassifyLaunchDiagnosticException(exception);
            return new ApkLaunchDiagnosticAttempt(
                dispatchAttempted,
                null,
                failure.Code,
                failure.Message,
                []);
        }
    }

    private async Task RequireExactReadySerialAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var matches = (await GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(device => string.Equals(device.Serial, serial, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 || !matches[0].IsReady)
            throw new InvalidDataException("The exact selected serial is not uniquely ready.");
    }

    private async Task<string> ReadLaunchDiagnosticDeviceFenceAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var result = await RunForDeviceAsync(
            serial,
            ["shell", "date", "+%s.%N"],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Read launch-diagnostic device time fence");
        var value = result.StandardOutput.Trim();
        if (!Regex.IsMatch(value, @"^[0-9]{10,}\.[0-9]{1,9}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The device launch-time fence was malformed.");
        return value;
    }

    private async Task<int> ReadUniqueCurrentUserPackageUidAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken)
    {
        var result = await RunForDeviceAsync(
            serial,
            ["shell", "pm", "list", "packages", "--user", "current", "-U"],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Read current-user package UID inventory");
        var inventory = new List<(string PackageName, int Uid)>();
        foreach (var line in result.StandardOutput.ReplaceLineEndings("\n")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(
                line,
                @"^package:(?<package>[A-Za-z0-9._]+)\s+uid:(?<uid>[0-9]+)$",
                RegexOptions.CultureInvariant);
            if (!match.Success ||
                !int.TryParse(match.Groups["uid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var observedUid) ||
                observedUid < 10_000)
            {
                throw new InvalidDataException("The current-user package UID inventory was malformed.");
            }
            inventory.Add((match.Groups["package"].Value, observedUid));
        }
        var packageMatches = inventory
            .Where(item => string.Equals(item.PackageName, packageName, StringComparison.Ordinal))
            .ToArray();
        if (packageMatches.Length != 1 ||
            inventory.Any(item => item.Uid == packageMatches[0].Uid &&
                                  !string.Equals(item.PackageName, packageName, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The current-user package UID was absent, ambiguous, or shared with another installed package.");
        }
        return packageMatches[0].Uid;
    }

    private async Task<IReadOnlyList<int>> ReadLaunchDiagnosticPackagePidsAsync(
        string serial,
        string packageName,
        int uid,
        CancellationToken cancellationToken)
    {
        var result = await RunForDeviceAsync(
            serial,
            ["shell", "ps", "-A", "-o", "UID,PID,ARGS"],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Read current package process identities");
        var matches = new List<int>();
        foreach (var line in result.StandardOutput.ReplaceLineEndings("\n").Split('\n'))
        {
            var tokens = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || string.Equals(tokens[0], "UID", StringComparison.OrdinalIgnoreCase))
                continue;
            var processName = tokens.Length == 3
                ? tokens[2].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                : null;
            if (tokens.Length < 3 ||
                !(string.Equals(processName, packageName, StringComparison.Ordinal) ||
                  processName?.StartsWith(packageName + ":", StringComparison.Ordinal) == true))
            {
                continue;
            }
            if (!int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var observedUid) ||
                observedUid != uid ||
                !int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                pid <= 0)
            {
                throw new InvalidDataException("A current package process identity was malformed or UID-mismatched.");
            }
            matches.Add(pid);
        }
        return matches.Distinct().Order().ToArray();
    }

    private static (ApkLaunchDiagnosticDisposition Disposition, string Detail)
        ClassifyLaunchDiagnosticDisposition(
            ApkLaunchDiagnosticAttempt attempt,
            ApkLaunchDiagnosticCapture capture,
            InstalledApkIdentity? installedAfter,
            int? uidAfter,
            string? postReadbackFailure)
    {
        if (!capture.ProcessTreeCleanupSucceeded || !capture.PostActionWindowElapsed ||
            capture.OutputLimitReached || capture.CaptureExitedEarly)
        {
            return (ApkLaunchDiagnosticDisposition.OutcomeUnknown,
                "The bounded capture window, limit, process lifetime, or cleanup could not be confirmed.");
        }
        if (!attempt.DispatchAttempted)
            return (ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch,
                attempt.FailureMessage ?? "Launch admission rejected before dispatch.");
        if (attempt.Launch is null || postReadbackFailure is not null || installedAfter?.Identity is null || uidAfter is null)
            return (ApkLaunchDiagnosticDisposition.OutcomeUnknown,
                attempt.FailureMessage ?? postReadbackFailure ?? "Launch or exact post-capture identity could not be confirmed.");
        if (!attempt.Launch.ComponentObservedResumed)
            return (ApkLaunchDiagnosticDisposition.LaunchPending,
                "The fixed launcher was dispatched, but exact resumed-component readback is pending.");
        if (attempt.CurrentPackageProcessIds.Count == 0)
            return (ApkLaunchDiagnosticDisposition.OutcomeUnknown,
                "The resumed package had no current UID-bound package process at the post-capture readback.");
        return (ApkLaunchDiagnosticDisposition.Completed,
            "Exact installed bytes, one resolved launcher dispatch, resumed-component readback, current UID/PIDs, and bounded post-fence UID logs were retained.");
    }

    private static void EnsureExactStandaloneInstalledArtifact(
        ApkArtifactInspection artifact,
        InstalledApkIdentity installed)
    {
        EnsureSameArtifact(artifact, installed);
        if (installed.ApkPaths.Count != 1)
            throw new InvalidDataException(
                "Launch diagnostics require one standalone installed base APK with no installed splits.");
    }

    private static (string Code, string Message) ClassifyLaunchDiagnosticException(Exception exception) => exception switch
    {
        OperationCanceledException => ("operation_cancelled", "The bounded operation was cancelled; launch outcome may be unknown."),
        TimeoutException => ("operation_timed_out", "A bounded device command timed out; launch outcome may be unknown."),
        InvalidDataException => ("device_readback_rejected", "A fixed device readback was malformed, ambiguous, or drifted."),
        PackageNotInstalledException => ("package_not_installed", "The exact inspected package is not installed."),
        _ => ("device_operation_failed", "A fixed launch-diagnostic operation failed; launch outcome may be unknown.")
    };

    private static string ValidateLaunchDiagnosticOutputDirectory(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullPath = Path.GetFullPath(outputDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
            throw new ArgumentException("The launch-diagnostic output must name a new non-root directory.", nameof(outputDirectory));
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The launch-diagnostic output parent directory does not exist.");
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
            throw new ArgumentException("The launch-diagnostic output path already exists; evidence never overwrites.", nameof(outputDirectory));
        return fullPath;
    }

    private static async Task WriteLaunchDiagnosticCreateNewAsync(string path, byte[] bytes)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
