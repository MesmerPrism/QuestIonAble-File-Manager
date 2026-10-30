using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

internal sealed partial class LocalApiDiagnosticOutputReservation
{
    private const string LaunchLogEntry = "logcat-uid-post-fence.txt";
    private const string LaunchManifestEntry = "launch-diagnostic-manifest.json";
    private static readonly JsonSerializerOptions LaunchManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal Task<LocalApiDiagnosticSealedOutput> SealAsync(ApkLaunchDiagnosticBundleResult result, CancellationToken token) =>
        SealCoreAsync(result.Artifact, true, payloads => ValidateLaunchManifest(payloads, result), token);

    private void ValidateLaunchManifest(Resource[] payloads, ApkLaunchDiagnosticBundleResult result)
    {
        try
        {
            OwnedLaunchManifestBounds.RequireValid(result.Artifact, result.InstalledBeforeDispatch,
                result.InstalledAfterCapture, result.Attempt, result.Capture, result.HostLaunchFence,
                result.DeviceLaunchFenceEpoch, result.DispositionDetail, result.OwnedPostReadbackFailure);
            if (payloads.Length != 2 ||
                !payloads.Any(r => r.EntryName == LaunchLogEntry) || !payloads.Any(r => r.EntryName == LaunchManifestEntry) ||
                result.Artifact.Path != ReportedInputPath || result.Artifact.Identity.SplitName is not null ||
                result.OutputDirectory != ReportedOutputPath || result.BundleLeafName != Intent.OutputId ||
                result.PublishedAtRequestedPath || result.ManifestRelativePath != LaunchManifestEntry ||
                result.DiagnosticContract != "questionable.file_manager.apk_launch_diagnostic_bundle.v1" ||
                !ExactStandaloneLaunchIdentity(result.Artifact, result.InstalledBeforeDispatch) ||
                result.CurrentUserUidBeforeDispatch < 10000 ||
                !Regex.IsMatch(result.HostLaunchFence, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(result.DeviceLaunchFenceEpoch, "\\A[0-9]{10,}\\.[0-9]{1,9}\\z", RegexOptions.CultureInvariant) ||
                !Enum.IsDefined(result.Disposition) || result.Attempt.CurrentPackageProcessIds.Any(pid => pid <= 0) ||
                result.Attempt.CurrentPackageProcessIds.Distinct().Count() != result.Attempt.CurrentPackageProcessIds.Count ||
                result.InstalledAfterCapture is { } after && after.Serial != result.InstalledBeforeDispatch.Serial)
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");

            // Readback failure may retain partial or drifted after-facts. With
            // no failure, they must be the exact before identity and UID. Reuse
            // Core's classifier so sealing cannot invent another effect rule.
            if ((result.OwnedPostReadbackFailure is null &&
                 (result.InstalledAfterCapture is not { } confirmedAfter ||
                  !ExactStandaloneLaunchIdentity(result.Artifact, confirmedAfter) ||
                  result.CurrentUserUidAfterCapture != result.CurrentUserUidBeforeDispatch)) ||
                AdbClient.ClassifyLaunchDiagnosticDisposition(result.Attempt, result.Capture,
                    result.InstalledAfterCapture, result.CurrentUserUidAfterCapture, result.OwnedPostReadbackFailure).Disposition != result.Disposition)
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");

            if (result.Attempt.Launch is { } launch &&
                (!result.Attempt.DispatchAttempted || launch.Artifact != result.Artifact ||
                 !AdbClient.IsLaunchDiagnosticComponent(result.Artifact.Identity.PackageName, launch.Component) ||
                 (launch.LauncherIsActivityAlias
                     ? launch.LauncherTargetActivity is null ||
                       !AdbClient.IsLaunchDiagnosticComponent(result.Artifact.Identity.PackageName, launch.LauncherTargetActivity)
                     : launch.LauncherTargetActivity is not null) ||
                 !ExactStandaloneLaunchIdentity(result.Artifact, launch.Installed) ||
                 launch.Installed.Serial != result.InstalledBeforeDispatch.Serial || !launch.CommandResult.Succeeded ||
                 !launch.CommandResult.Arguments.SequenceEqual(new[] { "-s", result.InstalledBeforeDispatch.Serial,
                     "shell", "am", "start", "-n", launch.Component }, StringComparer.Ordinal)))
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");

            var log = payloads.Single(r => r.EntryName == LaunchLogEntry);
            var manifest = payloads.Single(r => r.EntryName == LaunchManifestEntry);
            if (result.Capture.RelativePath != LaunchLogEntry || result.Capture.SizeBytes != log.SizeBytes ||
                result.Capture.Sha256 != log.Sha256 || result.ManifestSizeBytes != manifest.SizeBytes ||
                result.ManifestSha256 != manifest.Sha256)
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");

            manifest.Stream!.Position = 0;
            using var actual = JsonDocument.Parse(manifest.Stream);
            // Compare every private fact, including nested command/identity tuples,
            // to the invocation's typed result. Duplicate properties are rejected
            // recursively rather than accepting JsonDocument's last-value lookup.
            // Bound the expected representation too; a typed result is not a grant
            // to allocate an unbounded comparison buffer.
            using var expectedBytes = new MemoryStream();
            using (var bounded = new BoundedArchiveStream(expectedBytes, LocalApiDiagnosticOutputIntent.MaximumPayloadFileBytes))
                JsonSerializer.Serialize(bounded, new
                {
                    schema = "questionable.file_manager.apk_launch_diagnostic_manifest.v1",
                    diagnosticContract = result.DiagnosticContract,
                    hostFence = new { id = result.HostLaunchFence, createdAt = result.HostFenceCreatedAt },
                    deviceFence = new
                    {
                        epoch = result.DeviceLaunchFenceEpoch,
                        source = "fixed serial-scoped device UTC epoch readback",
                        logSelection = "fixed epoch output at or after fence, filtered to derived current-user UID"
                    },
                    artifact = result.Artifact,
                    installedBeforeDispatch = result.InstalledBeforeDispatch,
                    installedAfterCapture = result.InstalledAfterCapture,
                    currentUserUidBeforeDispatch = result.CurrentUserUidBeforeDispatch,
                    currentUserUidAfterCapture = result.CurrentUserUidAfterCapture,
                    launch = result.Attempt,
                    capture = result.Capture,
                    effectDisposition = result.Disposition,
                    effectDispositionDetail = result.DispositionDetail,
                    postReadbackFailure = result.OwnedPostReadbackFailure,
                    limitations = new
                    {
                        applicationReadiness = "unknown", openXrReadiness = "unknown", wearerVisibility = "unknown",
                        screenshotOrRecording = false, genericLogFilter = false, retryPerformed = false
                    }
                }, LaunchManifestJson);
            expectedBytes.Position = 0;
            using var expected = JsonDocument.Parse(expectedBytes);
            if (!SameLaunchJson(actual.RootElement, expected.RootElement))
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
        {
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");
        }
    }

    private static bool ExactStandaloneLaunchIdentity(ApkArtifactInspection artifact, InstalledApkIdentity installed) =>
        !string.IsNullOrWhiteSpace(installed.Serial) && installed.Identity == artifact.Identity &&
        installed.ApkPaths.Count == 1 && !string.IsNullOrWhiteSpace(installed.ApkPaths[0]) &&
        installed.BaseApkSha256 == artifact.Sha256 && installed.BaseApkSizeBytes == artifact.SizeBytes &&
        ((installed.VerificationMethod == "same-opened-handle-device-sha256" && installed.VerificationFallbackReason is null) ||
         (installed.VerificationMethod == "host-streamed-sha256" && installed.VerificationFallbackReason == "remote-handle-digest-capability-unsupported"));

    private static bool SameLaunchJson(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind) return false;
        switch (actual.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = actual.EnumerateObject().ToArray();
                if (properties.Length != expected.EnumerateObject().Count() ||
                    properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length) return false;
                return properties.All(p => expected.TryGetProperty(p.Name, out var value) && SameLaunchJson(p.Value, value));
            case JsonValueKind.Array:
                var items = actual.EnumerateArray().ToArray();
                var expectedItems = expected.EnumerateArray().ToArray();
                return items.Length == expectedItems.Length && items.Zip(expectedItems).All(pair => SameLaunchJson(pair.First, pair.Second));
            case JsonValueKind.String:
                return actual.GetString() == expected.GetString();
            case JsonValueKind.Number:
                return actual.GetRawText() == expected.GetRawText();
            default:
                return true;
        }
    }
}
