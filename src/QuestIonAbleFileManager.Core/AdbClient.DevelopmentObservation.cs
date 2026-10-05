using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    public async Task<DevelopmentAppRuntimeObservation> ObserveDevelopmentAppAsync(
        string serial, string apkPath, string? reportedInstallReference = null,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        ValidateReportedDevelopmentInstallReference(reportedInstallReference);
        var reportedPath = Path.GetFullPath(apkPath);
        using var admission = await ImmutableApkAdmission.CreateAsync(reportedPath, cancellationToken).ConfigureAwait(false);
        var artifact = await CreateApkInspector().InspectAsync(admission.Path, cancellationToken).ConfigureAwait(false);
        RejectSplitArtifact(artifact);
        var before = await ReadDevelopmentMetadataAsync(serial, artifact.Identity.PackageName, cancellationToken).ConfigureAwait(false);
        if (before.VersionCode != artifact.Identity.VersionCode ||
            !string.Equals(before.VersionName, artifact.Identity.VersionName, StringComparison.Ordinal))
            throw new InvalidDataException("Installed development metadata differs from the expected APK version.");
        var runtime = await ObservePackageRuntimeAsync(serial, reportedPath, artifact, null,
            cancellationToken).ConfigureAwait(false);
        var after = await ReadDevelopmentMetadataAsync(serial, artifact.Identity.PackageName, cancellationToken).ConfigureAwait(false);
        if (before.VersionCode != after.VersionCode || before.VersionName != after.VersionName ||
            before.LastUpdateTime != after.LastUpdateTime ||
            !before.ApkPaths.SequenceEqual(after.ApkPaths, StringComparer.Ordinal))
            throw new InvalidDataException("Installed development metadata changed during runtime observation.");
        return new(artifact with { Path = reportedPath }, before,
            runtime with { ObservationContract = "questionable.file_manager.development_runtime_facts.v1" },
            reportedInstallReference);
    }

    internal static void ValidateReportedDevelopmentInstallReference(string? reference)
    {
        if (reference is not null && !Regex.IsMatch(reference, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,255}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Reported development install reference must be a bounded opaque identifier.", nameof(reference));
    }

    private async Task<InstalledPackageDevelopmentMetadata> ReadDevelopmentMetadataAsync(
        string serial, string packageName, CancellationToken cancellationToken)
    {
        var package = await InspectPackageAsync(serial, packageName, cancellationToken).ConfigureAwait(false);
        if (package.ApkPaths.Count != 1 || !string.Equals(Path.GetFileName(package.ApkPaths[0]), "base.apk", StringComparison.Ordinal))
            throw new InvalidDataException("Development metadata observation requires one standalone base APK.");
        if (_runner is not IStreamingCommandRunner streaming)
            throw new InvalidOperationException("Development metadata observation requires bounded text readback.");
        using var output = new MemoryStream();
        var captured = await streaming.RunToStreamAsync(AdbPath,
            ["-s", serial, "shell", "dumpsys", "package", packageName], output, 256 * 1024,
            InspectionTimeout, cancellationToken).ConfigureAwait(false);
        captured.CommandResult.EnsureSuccess("Read installed development package metadata");
        if (captured.CommandResult.StandardError.Length != 0 || captured.BytesWritten != output.Length || output.Length > 256 * 1024)
            throw new InvalidDataException("Development metadata readback was incomplete or included errors.");
        var lines = new UTF8Encoding(false, true).GetString(output.ToArray()).ReplaceLineEndings("\n").Split('\n');
        var headers = lines.Select((text, index) => (text, index)).Where(row =>
            Regex.IsMatch(row.text, @"^\s*Package \[" + Regex.Escape(packageName) + @"\] \([A-Za-z0-9]+\):\s*$",
                RegexOptions.CultureInvariant)).ToArray();
        if (headers.Length != 1)
            throw new InvalidDataException("Development metadata package section was absent or ambiguous.");
        var indent = headers[0].text.Length - headers[0].text.TrimStart().Length;
        var section = lines.Skip(headers[0].index + 1).TakeWhile(line =>
            string.IsNullOrWhiteSpace(line) || line.Length - line.TrimStart().Length > indent).ToArray();
        string Single(string pattern)
        {
            var matches = section.Select(line => Regex.Match(line, pattern, RegexOptions.CultureInvariant))
                .Where(match => match.Success).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Development metadata field was absent or ambiguous.");
            return matches[0].Groups[1].Value;
        }
        var code = Single(@"^\s*versionCode=([1-9][0-9]*)(?:\s+.*)?$");
        var name = Single(@"^\s*versionName=(.*?)\s*$");
        var update = Single(@"^\s*lastUpdateTime=([0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?(?: [+-][0-9]{4})?)\s*$");
        if (!long.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var versionCode) ||
            !DateTime.TryParseExact(update[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            throw new InvalidDataException("Development version or last-update timestamp was malformed.");
        return new(packageName, versionCode, name == "null" ? null : name, package.ApkPaths, update);
    }
}
