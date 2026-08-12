using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed record AndroidBuildToolPaths(
    string Aapt2Path,
    string ApkSignerPath,
    string? JavaPath = null)
{
    public static AndroidBuildToolPaths FindFromAdb(string adbPath)
    {
        var sdkRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
            ?? Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (string.IsNullOrWhiteSpace(sdkRoot))
        {
            var platformTools = Path.GetDirectoryName(Path.GetFullPath(adbPath));
            sdkRoot = string.Equals(Path.GetFileName(platformTools), "platform-tools", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(platformTools)
                : null;
        }

        if (!string.IsNullOrWhiteSpace(sdkRoot))
        {
            var buildTools = Path.Combine(sdkRoot, "build-tools");
            if (Directory.Exists(buildTools))
            {
                foreach (var directory in Directory.GetDirectories(buildTools)
                             .OrderByDescending(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                {
                    var aapt2 = Path.Combine(directory, OperatingSystem.IsWindows() ? "aapt2.exe" : "aapt2");
                    var signer = Path.Combine(directory, OperatingSystem.IsWindows() ? "apksigner.bat" : "apksigner");
                    var signerJar = Path.Combine(directory, "lib", "apksigner.jar");
                    if (File.Exists(aapt2) && File.Exists(signerJar))
                    {
                        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
                        var java = string.IsNullOrWhiteSpace(javaHome)
                            ? (OperatingSystem.IsWindows() ? "java.exe" : "java")
                            : Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
                        return new AndroidBuildToolPaths(aapt2, signerJar, java);
                    }
                    if (File.Exists(aapt2) && File.Exists(signer) && !OperatingSystem.IsWindows())
                    {
                        return new AndroidBuildToolPaths(aapt2, signer);
                    }
                }
            }
        }

        throw new FileNotFoundException(
            "Android SDK Build Tools containing aapt2 and apksigner were not found. Set ANDROID_SDK_ROOT or ANDROID_HOME.");
    }
}

internal sealed class ApkArtifactInspector(
    ICommandRunner runner,
    AndroidBuildToolPaths tools)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Regex PackageLine = new(
        "^package:\\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Attribute = new(
        "(?<key>[A-Za-z][A-Za-z0-9]*)='(?<value>[^']*)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SdkLine = new(
        "^sdkVersion:'(?<value>[^']+)'$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TargetSdkLine = new(
        "^targetSdkVersion:'(?<value>[^']+)'$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LaunchableActivityLine = new(
        "^launchable-activity:\\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SignerLine = new(
        "^(?:" +
        "Signer #[1-9][0-9]*|" +
        "V(?:1|2|3\\.0) Signer(?: #[1-9][0-9]*)?:|" +
        "V3\\.(?:0|1) Signer: \\(minSdkVersion=[0-9]+" +
        "(?: \\(dev release=true\\))?, maxSdkVersion=[0-9]+\\)" +
        ") " +
        "certificate SHA-256 digest:\\s*(?<digest>[0-9a-fA-F:]{64,95})\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HybridSignerLine = new(
        "^V3\\.2 Hybrid (?:Classical|PQC) Signer: \\(minSdkVersion=[0-9]+" +
        "(?: \\(dev release=true\\))?, maxSdkVersion=[0-9]+\\) " +
        "certificate SHA-256 digest:\\s*[0-9a-fA-F:]{64,95}\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<ApkArtifactInspection> InspectAsync(
        string apkPath,
        CancellationToken cancellationToken = default) =>
        (await InspectManifestAsync(apkPath, cancellationToken).ConfigureAwait(false)).Artifact;

    public async Task<ApkArtifactManifestInspection> InspectManifestAsync(
        string apkPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var path = Path.GetFullPath(apkPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The APK to inspect was not found.", path);
        }
        if (!string.Equals(Path.GetExtension(path), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The inspection input must be one .apk file.", nameof(apkPath));
        }

        var info = new FileInfo(path);
        if (info.Length == 0)
        {
            throw new InvalidDataException("The APK is empty.");
        }

        string sha256;
        await using (var stream = new FileStream(
                         path, FileMode.Open, FileAccess.Read,
                         FileShare.Read | FileShare.Write | FileShare.Delete))
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
        }

        var badging = await runner.RunAsync(
            tools.Aapt2Path, ["dump", "badging", path], Timeout, cancellationToken).ConfigureAwait(false);
        badging.EnsureSuccess("Inspect APK manifest");
        var badgingLines = badging.StandardOutput.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .ToArray();
        var packageLines = badgingLines
            .Where(line => PackageLine.IsMatch(line))
            .ToArray();
        if (packageLines.Length != 1)
        {
            throw new InvalidDataException("APK manifest inspection did not produce exactly one package identity.");
        }
        var attributes = Attribute.Matches(packageLines[0])
            .ToDictionary(match => match.Groups["key"].Value, match => match.Groups["value"].Value,
                StringComparer.Ordinal);
        if (!attributes.TryGetValue("name", out var packageValue) ||
            !attributes.TryGetValue("versionCode", out var versionCodeValue))
        {
            throw new InvalidDataException("APK package identity is missing required manifest facts.");
        }

        var signerExecutable = tools.JavaPath ?? tools.ApkSignerPath;
        var signerArguments = tools.JavaPath is null
            ? new[] { "verify", "--print-certs", path }
            : new[] { "-jar", tools.ApkSignerPath, "verify", "--print-certs", path };
        var signer = await runner.RunAsync(
            signerExecutable, signerArguments, Timeout, cancellationToken).ConfigureAwait(false);
        signer.EnsureSuccess("Verify APK signer");
        var signerLines = signer.StandardOutput.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .ToArray();
        if (signerLines.Any(static line => HybridSignerLine.IsMatch(line)))
        {
            throw new InvalidDataException(
                "APK Signature Scheme v3.2 hybrid signers are not supported by the single-signer identity contract.");
        }
        var signerDigests = signerLines
            .Select(static line => SignerLine.Match(line))
            .Where(static match => match.Success)
            .Select(match => match.Groups["digest"].Value.Replace(":", "").ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (signerDigests.Length != 1)
        {
            throw new InvalidDataException("APK must have exactly one unambiguous current signer.");
        }

        var packageName = AndroidInput.RequirePackageName(packageValue);
        if (!long.TryParse(versionCodeValue, NumberStyles.None, CultureInfo.InvariantCulture, out var versionCode) ||
            versionCode <= 0)
        {
            throw new InvalidDataException("APK versionCode must be a positive integer.");
        }
        attributes.TryGetValue("split", out var split);
        var minimumSdkVersion = ParseOptionalSingleSdkValue(badgingLines, SdkLine, "minimum SDK") ?? 1;
        var targetSdkVersion = ParseOptionalSingleSdkValue(badgingLines, TargetSdkLine, "target SDK");
        if (targetSdkVersion is not null && targetSdkVersion.Value < minimumSdkVersion)
        {
            throw new InvalidDataException("APK target SDK cannot be lower than its minimum SDK.");
        }
        var launcherActivities = badgingLines
            .Where(line => LaunchableActivityLine.IsMatch(line))
            .Select(line => Attribute.Matches(line)
                .Cast<Match>()
                .ToDictionary(
                    match => match.Groups["key"].Value,
                    match => match.Groups["value"].Value,
                    StringComparer.Ordinal)
                .GetValueOrDefault("name"))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var artifact = new ApkArtifactInspection(
            path, info.Length, sha256,
            new ApkArtifactIdentity(
                packageName,
                versionCode,
                attributes.GetValueOrDefault("versionName"),
                signerDigests[0],
                split));
        return new ApkArtifactManifestInspection(
            artifact,
            new ApkArtifactManifestFacts(
                minimumSdkVersion,
                targetSdkVersion,
                launcherActivities));
    }

    private static int? ParseOptionalSingleSdkValue(
        IReadOnlyList<string> lines,
        Regex pattern,
        string label)
    {
        var matches = lines
            .Select(line => pattern.Match(line))
            .Where(static match => match.Success)
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException($"APK manifest inspection produced multiple {label} values.");
        }
        if (matches.Length == 0)
        {
            return null;
        }
        if (!int.TryParse(
                matches[0].Groups["value"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) || value < 1)
        {
            throw new InvalidDataException($"APK {label} must be a positive integer.");
        }
        return value;
    }
}

internal sealed record ApkArtifactManifestInspection(
    ApkArtifactInspection Artifact,
    ApkArtifactManifestFacts Manifest);
