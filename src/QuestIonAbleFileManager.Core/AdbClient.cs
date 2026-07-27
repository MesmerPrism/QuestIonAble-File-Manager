using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed class AdbClient
{
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(5);
    private readonly ICommandRunner _runner;
    private readonly AndroidBuildToolPaths? _buildTools;

    public AdbClient(string adbPath, ICommandRunner? runner = null, AndroidBuildToolPaths? buildTools = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        AdbPath = adbPath;
        _runner = runner ?? new CommandRunner();
        _buildTools = buildTools;
    }

    public string AdbPath { get; }

    internal ApkArtifactInspector CreateApkInspector() =>
        new(_runner, _buildTools ?? AndroidBuildToolPaths.FindFromAdb(AdbPath));

    public async Task<ApkArtifactInspection> InspectApkAsync(
        string apkPath,
        CancellationToken cancellationToken = default)
    {
        var reportedPath = Path.GetFullPath(apkPath);
        using var admission = await ImmutableApkAdmission.CreateAsync(
            reportedPath,
            cancellationToken).ConfigureAwait(false);
        var inspected = await CreateApkInspector()
            .InspectAsync(admission.Path, cancellationToken).ConfigureAwait(false);
        return inspected with { Path = reportedPath };
    }

    public static AdbClient CreateDefault(string? explicitAdbPath = null, ICommandRunner? runner = null) =>
        new(AdbLocator.FindOrThrow(explicitAdbPath), runner);

    public async Task<IReadOnlyList<QuestDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            new[] { "devices", "-l" },
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("ADB device discovery");
        return AdbOutputParser.ParseDevices(result.StandardOutput);
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListRemoteDirectoryAsync(
        string serial,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        var command = $"ls -1Ap -- {AndroidInput.ShellQuote(remotePath)}";
        var result = await RunForDeviceAsync(
            serial,
            new[] { "shell", command },
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"List {remotePath}");
        return AdbOutputParser.ParseRemoteDirectory(remotePath, result.StandardOutput);
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListRemoteDirectoryBoundedAsync(
        string serial,
        string remotePath,
        int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        if (maximumEntries is < 1 or > FleetIntegrationContract.MaximumListEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                $"The integration list limit must be between 1 and {FleetIntegrationContract.MaximumListEntries}.");
        }

        var command = BuildCanonicalRemoteProof(remotePath) +
            BuildOpenedRemoteHandleProof() +
            "if [ ! -d /proc/self/fd/3 ]; then " +
            "printf 'qfm-integration:path-not-directory\\n' >&2; exit 43; fi; " +
            "count=0; " +
            "for entry in /proc/self/fd/3/* /proc/self/fd/3/.[!.]* /proc/self/fd/3/..?*; do " +
            "if [ ! -e \"$entry\" ] && [ ! -L \"$entry\" ]; then continue; fi; " +
            "count=$((count + 1)); " +
            $"if [ \"$count\" -gt {maximumEntries.ToString(System.Globalization.CultureInfo.InvariantCulture)} ]; then " +
            "printf 'qfm-integration:maximum-entries\\n' >&2; exit 50; fi; " +
            "if [ -L \"$entry\" ] || { [ ! -f \"$entry\" ] && [ ! -d \"$entry\" ]; }; then " +
            "printf 'qfm-integration:unsupported-entry-type\\n' >&2; exit 51; fi; " +
            "name=${entry##*/}; " +
            "case \"$name\" in *[[:cntrl:]]*) " +
            "printf 'qfm-integration:entry-name-unrepresentable\\n' >&2; exit 52;; esac; " +
            "if [ -d \"$entry\" ]; then printf '%s/\\n' \"$name\"; " +
            "else printf '%s\\n' \"$name\"; fi; " +
            "done";
        var result = await RunForDeviceAsync(
            serial,
            new[] { "shell", command },
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        ThrowFleetRemoteError(result, "List remote integration path");
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new AdbCommandException(
                "List remote integration path",
                result with { ExitCode = result.ExitCode == 0 ? 1 : result.ExitCode });
        }
        result.EnsureSuccess($"List {remotePath}");
        return AdbOutputParser.ParseRemoteDirectory(remotePath, result.StandardOutput);
    }

    public async Task<StreamingCommandResult> StreamRemoteFileBoundedAsync(
        string serial,
        string remotePath,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        ArgumentNullException.ThrowIfNull(destination);
        if (maximumBytes is < 1 or > FleetIntegrationContract.MaximumPullBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                $"The integration pull limit must be between 1 and {FleetIntegrationContract.MaximumPullBytes}.");
        }

        var invariantMaximum = maximumBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var command = BuildCanonicalRemoteProof(remotePath) +
            BuildOpenedRemoteHandleProof() +
            "if [ ! -f /proc/self/fd/3 ]; then " +
            "printf 'qfm-integration:path-not-file\\n' >&2; exit 44; fi; " +
            "size=$(stat -c %s -- /proc/self/fd/3) || { " +
            "printf 'qfm-integration:size-unavailable\\n' >&2; exit 45; }; " +
            "case \"$size\" in ''|*[!0-9]*) " +
            "printf 'qfm-integration:size-invalid\\n' >&2; exit 46;; esac; " +
            $"if [ \"$size\" -gt {invariantMaximum} ]; then " +
            "printf 'qfm-integration:maximum-bytes\\n' >&2; exit 47; fi; " +
            "exec cat <&3";
        if (_runner is not IStreamingCommandRunner streamingRunner)
        {
            throw new InvalidOperationException(
                "The configured command runner does not support bounded binary streaming.");
        }

        var arguments = new[]
        {
            "-s",
            serial,
            "exec-out",
            "sh",
            "-c",
            command
        };
        var result = await streamingRunner.RunToStreamAsync(
            AdbPath,
            arguments,
            destination,
            maximumBytes,
            TransferTimeout,
            cancellationToken).ConfigureAwait(false);
        ThrowFleetRemoteError(
            result.CommandResult,
            "Stream remote integration file",
            maximumBytes);
        if (!string.IsNullOrWhiteSpace(result.CommandResult.StandardError))
        {
            throw new AdbCommandException(
                "Stream remote integration file",
                result.CommandResult with
                {
                    ExitCode = result.CommandResult.ExitCode == 0
                        ? 1
                        : result.CommandResult.ExitCode
                });
        }
        result.CommandResult.EnsureSuccess($"Stream {remotePath}");
        return result;
    }

    public async Task<StreamingCommandResult> StreamLocalFileToRemoteNoOverwriteAsync(
        string serial,
        string remotePath,
        string operationId,
        Stream source,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(source);
        if (expectedBytes is < 1 or > FleetIntegrationContract.MaximumPushBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        }
        if (expectedSha256.Length != 64 ||
            expectedSha256.Any(static character =>
                !char.IsAsciiDigit(character) &&
                (character < 'a' || character > 'f')))
        {
            throw new ArgumentException("Expected SHA-256 must be lowercase hexadecimal.", nameof(expectedSha256));
        }
        if (operationId.Length > 64 ||
            !char.IsAsciiLetterOrDigit(operationId[0]) ||
            operationId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException("The operation ID is not safe for remote staging.", nameof(operationId));
        }
        if (_runner is not IStreamingCommandRunner streamingRunner)
        {
            throw new InvalidOperationException(
                "The configured command runner does not support bounded binary streaming.");
        }

        var slash = remotePath.LastIndexOf('/');
        if (slash < FleetIntegrationContract.RemoteRoot.Length)
        {
            throw new ArgumentException("The integration push target must have a parent below /sdcard.", nameof(remotePath));
        }
        var remoteParent = remotePath[..slash];
        var remoteName = remotePath[(slash + 1)..];
        var parentRelative = string.Equals(
                remoteParent,
                FleetIntegrationContract.RemoteRoot,
                StringComparison.Ordinal)
            ? string.Empty
            : remoteParent[(FleetIntegrationContract.RemoteRoot.Length + 1)..];
        FleetPathPolicy.ValidateRelativePath(parentRelative, allowEmpty: true);
        FleetPathPolicy.ValidateRelativePath(remoteName, allowEmpty: false);
        var partialName = $".qfm-{operationId}.partial";
        var invariantBytes = expectedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var command =
            $"root=$(realpath {AndroidInput.ShellQuote(FleetIntegrationContract.RemoteRoot)}) || {{ " +
            "printf 'qfm-integration:root-unavailable\\n' >&2; exit 40; }; " +
            $"parent=$(realpath {AndroidInput.ShellQuote(remoteParent)}) || {{ " +
            "printf 'qfm-integration:parent-absent\\n' >&2; exit 60; }; " +
            (parentRelative.Length == 0
                ? "expected_parent=\"$root\"; "
                : $"expected_parent=\"$root\"/{AndroidInput.ShellQuote(parentRelative)}; ") +
            "if [ \"$parent\" != \"$expected_parent\" ]; then " +
            "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; " +
            $"candidate=\"$parent\"/{AndroidInput.ShellQuote(remoteName)}; " +
            $"partial=\"$parent\"/{AndroidInput.ShellQuote(partialName)}; " +
            "if [ -e \"$candidate\" ] || [ -L \"$candidate\" ]; then " +
            "printf 'qfm-integration:destination-exists\\n' >&2; exit 61; fi; " +
            "if [ -e \"$partial\" ] || [ -L \"$partial\" ]; then " +
            "printf 'qfm-integration:partial-exists\\n' >&2; exit 62; fi; " +
            "committed=0; partial_id=''; final_id=''; " +
            "cleanup() { " +
            "if [ \"$committed\" != 1 ]; then " +
            "if [ -n \"$final_id\" ] && [ -e \"$candidate\" ]; then " +
            "current=$(stat -c %d:%i -- \"$candidate\" 2>/dev/null || true); " +
            "if [ \"$current\" = \"$final_id\" ]; then rm -f -- \"$candidate\" || true; fi; fi; " +
            "if [ -n \"$partial_id\" ] && [ -e \"$partial\" ]; then " +
            "current=$(stat -c %d:%i -- \"$partial\" 2>/dev/null || true); " +
            "if [ \"$current\" = \"$partial_id\" ]; then rm -f -- \"$partial\" || true; fi; fi; fi; }; " +
            "trap cleanup EXIT; trap 'exit 67' HUP INT TERM; set -C; " +
            "exec 3>\"$partial\" || { printf 'qfm-integration:partial-exists\\n' >&2; exit 62; }; " +
            "partial_id=$(stat -c %d:%i -- /proc/self/fd/3) || exit 63; " +
            "opened=$(realpath /proc/self/fd/3) || exit 63; " +
            "if [ \"$opened\" != \"$partial\" ]; then " +
            "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; " +
            "cat >&3; exec 3>&-; " +
            "size=$(stat -c %s -- \"$partial\") || exit 63; " +
            "digest=$(sha256sum \"$partial\") || exit 63; digest=${digest%% *}; " +
            $"if [ \"$size\" != {invariantBytes} ]; then " +
            "printf 'qfm-integration:push-size-mismatch\\n' >&2; exit 63; fi; " +
            $"if [ \"$digest\" != {AndroidInput.ShellQuote(expectedSha256)} ]; then " +
            "printf 'qfm-integration:push-digest-mismatch\\n' >&2; exit 64; fi; " +
            "current=$(stat -c %d:%i -- \"$partial\") || exit 65; " +
            "if [ \"$current\" != \"$partial_id\" ]; then " +
            "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; " +
            "if ln -T -- \"$partial\" \"$candidate\" 2>/dev/null; then :; " +
            "elif [ -e \"$candidate\" ] || [ -L \"$candidate\" ]; then " +
            "printf 'qfm-integration:destination-exists\\n' >&2; exit 61; " +
            "else printf 'qfm-integration:atomic-publish-unavailable\\n' >&2; exit 68; fi; " +
            "final_id=$(stat -c %d:%i -- \"$candidate\") || exit 65; " +
            "if [ \"$final_id\" != \"$partial_id\" ]; then " +
            "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; " +
            "exec 4<\"$candidate\" || exit 65; " +
            "published_id=$(stat -c %d:%i -- /proc/self/fd/4) || exit 65; " +
            "opened=$(realpath /proc/self/fd/4) || exit 65; " +
            "if [ \"$opened\" != \"$candidate\" ]; then " +
            "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; " +
            "if [ \"$published_id\" != \"$partial_id\" ]; then " +
            "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; " +
            "final_size=$(stat -c %s -- /proc/self/fd/4) || exit 65; " +
            "final_digest=$(sha256sum <&4) || exit 65; final_digest=${final_digest%% *}; " +
            "exec 4<&-; " +
            $"if [ \"$final_size\" != {invariantBytes} ] || " +
            $"[ \"$final_digest\" != {AndroidInput.ShellQuote(expectedSha256)} ]; then " +
            "printf 'qfm-integration:push-readback-mismatch\\n' >&2; exit 65; fi; " +
            "current=$(stat -c %d:%i -- \"$candidate\") || exit 65; " +
            "if [ \"$current\" != \"$final_id\" ]; then " +
            "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; " +
            "current=$(stat -c %d:%i -- \"$partial\") || exit 65; " +
            "if [ \"$current\" != \"$partial_id\" ]; then " +
            "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; " +
            "rm -f -- \"$partial\" || { printf 'qfm-integration:partial-cleanup-failed\\n' >&2; exit 66; }; " +
            "current=$(stat -c %d:%i -- \"$candidate\") || exit 65; " +
            "if [ \"$current\" != \"$final_id\" ]; then " +
            "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; " +
            "committed=1; trap - EXIT HUP INT TERM; " +
            "printf 'qfm-integration:push-complete:%s:%s\\n' \"$final_size\" \"$final_digest\"";

        var arguments = new[] { "-s", serial, "exec-in", "sh", "-c", command };
        var result = await streamingRunner.RunFromStreamAsync(
            AdbPath,
            arguments,
            source,
            expectedBytes,
            TransferTimeout,
            cancellationToken).ConfigureAwait(false);
        ThrowFleetRemoteError(result.CommandResult, "Push remote integration file", expectedBytes);
        result.CommandResult.EnsureSuccess($"Push {remotePath}");
        var expectedReceipt =
            $"qfm-integration:push-complete:{invariantBytes}:{expectedSha256}";
        if (!string.Equals(
                result.CommandResult.StandardOutput.Trim(),
                expectedReceipt,
                StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(result.CommandResult.StandardError) ||
            result.BytesWritten != expectedBytes ||
            !string.Equals(result.Sha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "push_evidence_mismatch",
                "The push stream, remote readback, and expected size/SHA-256 did not agree.",
                retryable: false);
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> GetThirdPartyPackageNamesAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var result = await RunForDeviceAsync(
            serial,
            new[] { "shell", "pm list packages -3" },
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("List third-party packages");
        return AdbOutputParser.ParsePackageNames(result.StandardOutput);
    }

    public async Task<QuestPackage> InspectPackageAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        packageName = AndroidInput.RequirePackageName(packageName);
        var result = await RunForDeviceAsync(
            serial,
            new[] { "shell", $"pm path {AndroidInput.ShellQuote(packageName)}" },
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"Inspect package {packageName}");

        var lines = result.StandardOutput.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            throw new PackageNotInstalledException(serial, packageName);
        }
        if (lines.Any(static line =>
                !line.StartsWith("package:/", StringComparison.Ordinal) ||
                !line["package:".Length..].EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Package path inspection returned malformed or unexpected non-empty output.");
        }
        var paths = AdbOutputParser.ParsePackagePaths(result.StandardOutput);
        if (paths.Count == 0)
        {
            throw new InvalidDataException(
                "Package path inspection did not return a valid installed APK path.");
        }

        return new QuestPackage(packageName, paths);
    }

    public async Task<CommandResult> PullFileAsync(
        string serial,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var fullLocalPath = Path.GetFullPath(localPath);
        var parent = Path.GetDirectoryName(fullLocalPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var result = await RunForDeviceAsync(
            serial,
            new[] { "pull", remotePath, fullLocalPath },
            TransferTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.EnsureSuccess($"Pull {remotePath}");
    }

    public async Task<CommandResult> PushFileAsync(
        string serial,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var fullLocalPath = Path.GetFullPath(localPath);
        if (!File.Exists(fullLocalPath))
        {
            throw new FileNotFoundException("The local file to push was not found.", fullLocalPath);
        }

        var result = await RunForDeviceAsync(
            serial,
            new[] { "push", fullLocalPath, remotePath },
            TransferTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"Push {Path.GetFileName(fullLocalPath)}");
        await VerifyRemoteFileSizeAsync(
            serial,
            remotePath,
            new FileInfo(fullLocalPath).Length,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CommandResult> InstallApkAsync(
        string serial,
        string apkPath,
        ApkInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullApkPath = Path.GetFullPath(apkPath);
        if (!File.Exists(fullApkPath))
        {
            throw new FileNotFoundException("The APK to install was not found.", fullApkPath);
        }

        if (!string.Equals(Path.GetExtension(fullApkPath), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The install input must be an .apk file.", nameof(apkPath));
        }

        var arguments = CreateInstallArguments("install", options);
        arguments.Add(fullApkPath);
        var result = await RunForDeviceAsync(
            serial,
            arguments,
            TransferTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"Install {Path.GetFileName(fullApkPath)}");
        await GetThirdPartyPackageNamesAsync(serial, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<InspectedApkInstallResult> InstallInspectedApkAsync(
        string serial,
        string apkPath,
        ApkInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var reportedPath = Path.GetFullPath(apkPath);
        using var admission = await ImmutableApkAdmission.CreateAsync(
            reportedPath,
            cancellationToken).ConfigureAwait(false);
        var inspector = CreateApkInspector();
        var artifact = await inspector.InspectAsync(admission.Path, cancellationToken).ConfigureAwait(false);
        RejectSplitArtifact(artifact);
        var current = await inspector.InspectAsync(admission.Path, cancellationToken).ConfigureAwait(false);
        if (current.Sha256 != artifact.Sha256 || current.SizeBytes != artifact.SizeBytes ||
            current.Identity != artifact.Identity)
        {
            throw new InvalidDataException("The APK changed while it was being admitted for installation.");
        }

        var arguments = CreateInstallArguments("install", options);
        arguments.Add(admission.Path);
        var commandResult = await RunForDeviceAsync(
            serial, arguments, TransferTimeout, cancellationToken).ConfigureAwait(false);
        commandResult.EnsureSuccess($"Install {Path.GetFileName(artifact.Path)}");
        var installed = await ReadInstalledIdentityAsync(
            serial, artifact, cancellationToken).ConfigureAwait(false);
        return new InspectedApkInstallResult(
            artifact with { Path = reportedPath },
            installed,
            commandResult);
    }

    public async Task<InstalledApkIdentity> ReadInstalledIdentityAsync(
        string serial,
        ApkArtifactInspection expectedArtifact,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentNullException.ThrowIfNull(expectedArtifact);
        var packageName = AndroidInput.RequirePackageName(expectedArtifact.Identity.PackageName);
        var package = await InspectPackageAsync(serial, packageName, cancellationToken).ConfigureAwait(false);
        var basePaths = package.ApkPaths.Where(path =>
            string.Equals(Path.GetFileName(path), "base.apk", StringComparison.OrdinalIgnoreCase) ||
            package.ApkPaths.Count == 1).ToArray();
        if (basePaths.Length != 1)
        {
            throw new InvalidDataException("Installed package readback did not identify exactly one base APK.");
        }

        var streamed = await StreamInstalledBaseApkAsync(
            serial,
            basePaths[0],
            expectedArtifact.SizeBytes,
            cancellationToken).ConfigureAwait(false);
        var exactBytes = streamed.BytesWritten == expectedArtifact.SizeBytes &&
            string.Equals(streamed.Sha256, expectedArtifact.Sha256, StringComparison.OrdinalIgnoreCase);
        return new InstalledApkIdentity(
            serial,
            exactBytes ? expectedArtifact.Identity : null,
            package.ApkPaths,
            streamed.Sha256,
            streamed.BytesWritten);
    }

    public async Task<ResolvedAppLaunchResult> LaunchInspectedAppAsync(
        string serial,
        string apkPath,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var reportedPath = Path.GetFullPath(apkPath);
        using var admission = await ImmutableApkAdmission.CreateAsync(
            reportedPath,
            cancellationToken).ConfigureAwait(false);
        var inspector = CreateApkInspector();
        var artifact = await inspector.InspectAsync(admission.Path, cancellationToken).ConfigureAwait(false);
        RejectSplitArtifact(artifact);
        var installed = await ReadInstalledIdentityAsync(
            serial, artifact, cancellationToken).ConfigureAwait(false);
        EnsureSameArtifact(artifact, installed);
        var query = await RunForDeviceAsync(
            serial,
            ["shell", "cmd", "package", "query-activities", "--brief", "--components",
             "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER",
             artifact.Identity.PackageName],
            InspectionTimeout, cancellationToken).ConfigureAwait(false);
        query.EnsureSuccess("Resolve exported launcher activity");
        var components = query.StandardOutput.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(artifact.Identity.PackageName + "/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (components.Length != 1 || !IsSafeComponent(components[0], artifact.Identity.PackageName))
        {
            throw new InvalidDataException(
                "Package must resolve to exactly one exported launcher activity.");
        }
        var packageDump = await RunForDeviceAsync(
            serial,
            ["shell", "dumpsys", "package", artifact.Identity.PackageName],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        packageDump.EnsureSuccess("Prove launcher activity export state");
        if (!ProvesExportedActivity(packageDump.StandardOutput, components[0]))
        {
            throw new InvalidDataException(
                "The resolved launcher activity was not proven exported before dispatch.");
        }

        var start = await RunForDeviceAsync(
            serial, ["shell", "am", "start", "-n", components[0]],
            InspectionTimeout, cancellationToken).ConfigureAwait(false);
        start.EnsureSuccess("Start resolved launcher activity");
        var activities = await RunForDeviceAsync(
            serial, ["shell", "dumpsys", "activity", "activities"],
            InspectionTimeout, cancellationToken).ConfigureAwait(false);
        activities.EnsureSuccess("Read back launched activity");
        var observed = activities.StandardOutput.ReplaceLineEndings("\n").Split('\n')
            .Any(line => (line.Contains("mResumedActivity", StringComparison.Ordinal) ||
                          line.Contains("topResumedActivity", StringComparison.OrdinalIgnoreCase)) &&
                         line.Contains(components[0], StringComparison.Ordinal));
        return new ResolvedAppLaunchResult(
            artifact with { Path = reportedPath },
            installed,
            components[0],
            start,
            observed);
    }

    public async Task<AppRuntimeObservation> ObserveInspectedAppAsync(
        string serial,
        string apkPath,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var reportedPath = Path.GetFullPath(apkPath);
        using var admission = await ImmutableApkAdmission.CreateAsync(
            reportedPath,
            cancellationToken).ConfigureAwait(false);
        var inspector = CreateApkInspector();
        var artifact = await inspector.InspectAsync(admission.Path, cancellationToken).ConfigureAwait(false);
        RejectSplitArtifact(artifact);
        InstalledApkIdentity? installed = null;
        try
        {
            installed = await ReadInstalledIdentityAsync(
                serial, artifact, cancellationToken).ConfigureAwait(false);
            EnsureSameArtifact(artifact, installed);
        }
        catch (PackageNotInstalledException)
        {
            // Absence is a structured observation, not an execution failure.
        }

        var activities = await RunForDeviceAsync(
            serial, ["shell", "dumpsys", "activity", "activities"],
            InspectionTimeout, cancellationToken).ConfigureAwait(false);
        activities.EnsureSuccess("Read activity state");
        var processes = await RunForDeviceAsync(
            serial, ["shell", "pidof", artifact.Identity.PackageName],
            InspectionTimeout, cancellationToken).ConfigureAwait(false);
        var packageToken = artifact.Identity.PackageName + "/";
        var lines = activities.StandardOutput.ReplaceLineEndings("\n").Split('\n');
        var pids = processes.Succeeded
            ? processes.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out var pid) ? pid : -1)
                .Where(static pid => pid > 0).Distinct().Order().ToArray()
            : [];
        return new AppRuntimeObservation(
            artifact with { Path = reportedPath },
            installed,
            lines.Any(line => line.Contains("mResumedActivity", StringComparison.Ordinal) &&
                              line.Contains(packageToken, StringComparison.Ordinal)),
            lines.Any(line => line.Contains("topResumedActivity", StringComparison.OrdinalIgnoreCase) &&
                              line.Contains(packageToken, StringComparison.Ordinal)),
            pids);
    }

    private static void RejectSplitArtifact(ApkArtifactInspection artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.Identity.SplitName))
        {
            throw new InvalidDataException("A split APK cannot be used as a standalone inspected deployment artifact.");
        }
    }

    private static void EnsureSameArtifact(
        ApkArtifactInspection expectedArtifact,
        InstalledApkIdentity installed)
    {
        var expected = expectedArtifact.Identity;
        var actual = installed.Identity;
        if (actual is null ||
            !string.Equals(expected.PackageName, actual.PackageName, StringComparison.Ordinal) ||
            expected.VersionCode != actual.VersionCode ||
            !string.Equals(expected.VersionName, actual.VersionName, StringComparison.Ordinal) ||
            !string.Equals(expected.SignerSha256, actual.SignerSha256, StringComparison.Ordinal) ||
            !string.Equals(expectedArtifact.Sha256, installed.BaseApkSha256, StringComparison.Ordinal) ||
            expectedArtifact.SizeBytes != installed.BaseApkSizeBytes)
        {
            throw new InvalidDataException(
                "Installed package/version/signer/base-APK digest and size readback does not match the inspected APK.");
        }
    }

    private static bool IsSafeComponent(string component, string packageName)
    {
        var slash = component.IndexOf('/');
        if (slash <= 0 || slash == component.Length - 1 ||
            !string.Equals(component[..slash], packageName, StringComparison.Ordinal))
        {
            return false;
        }
        var activity = component[(slash + 1)..];
        return Regex.IsMatch(activity, @"^(?:\.[A-Za-z0-9_$]+|[A-Za-z][A-Za-z0-9_$]*(?:\.[A-Za-z0-9_$]+)+)$",
            RegexOptions.CultureInvariant);
    }

    private static bool ProvesExportedActivity(string packageDump, string component)
    {
        var lines = packageDump.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var activity = Regex.Match(
                lines[index],
                @"ActivityInfo\{[^}\r\n]*\s(?<component>[^\s}]+)\}",
                RegexOptions.CultureInvariant);
            if (!activity.Success ||
                !string.Equals(
                    activity.Groups["component"].Value,
                    component,
                    StringComparison.Ordinal))
            {
                continue;
            }
            var activityIndent = lines[index].TakeWhile(char.IsWhiteSpace).Count();
            for (var detail = index + 1; detail < lines.Length; detail++)
            {
                var value = lines[detail].Trim();
                if (value.Length == 0) continue;
                var detailIndent = lines[detail].TakeWhile(char.IsWhiteSpace).Count();
                if (detailIndent <= activityIndent ||
                    value.StartsWith("Activity #", StringComparison.Ordinal) ||
                    value.Contains("ActivityInfo{", StringComparison.Ordinal))
                    break;
                if (value.StartsWith("exported=", StringComparison.Ordinal))
                    return string.Equals(value, "exported=true", StringComparison.Ordinal);
            }
            return false;
        }
        return false;
    }

    public async Task<ApkBundleInstallResult> InstallApkBundleAsync(
        string serial,
        IReadOnlyList<string> apkPaths,
        ApkInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentNullException.ThrowIfNull(apkPaths);
        if (apkPaths.Count < 2)
        {
            throw new InvalidDataException(
                "An APK bundle install requires at least two APK files.");
        }

        var normalizedPaths = apkPaths.Select(ValidateInstallApkPath).ToArray();
        if (normalizedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedPaths.Length)
        {
            throw new InvalidDataException("The APK bundle contains the same APK path more than once.");
        }

        var arguments = CreateInstallArguments("install-multiple", options);
        arguments.AddRange(normalizedPaths);
        var result = await RunForDeviceAsync(
            serial,
            arguments,
            TransferTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"Install APK bundle ({normalizedPaths.Length} parts)");
        await GetThirdPartyPackageNamesAsync(serial, cancellationToken).ConfigureAwait(false);
        return new ApkBundleInstallResult(normalizedPaths, result);
    }

    public async Task<WifiAdbEnableResult> EnableWifiAdbAndConnectAsync(
        string usbSerial,
        int port = 5555,
        CancellationToken cancellationToken = default,
        IProgress<OperatorProgress>? progress = null)
    {
        usbSerial = AndroidInput.RequireUsbSerial(usbSerial);
        port = AndroidInput.RequireTcpPort(port);

        progress?.Report(new OperatorProgress(
            "wifi-address",
            "Reading the headset Wi-Fi address…",
            0,
            3));
        var addressProbe = await RunForDeviceAsync(
            usbSerial,
            new[] { "shell", "ip route" },
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        addressProbe.EnsureSuccess("Read the headset Wi-Fi address");
        var host = AdbOutputParser.ParseWifiIpv4Address(addressProbe.StandardOutput);

        progress?.Report(new OperatorProgress(
            "wifi-enable",
            "Enabling Wi-Fi ADB on the selected headset…",
            1,
            3));
        var tcpIpCommand = await RunForDeviceAsync(
            usbSerial,
            new[] { "tcpip", port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ConnectionTimeout,
            cancellationToken).ConfigureAwait(false);
        tcpIpCommand.EnsureSuccess($"Enable Wi-Fi ADB on TCP port {port}");

        progress?.Report(new OperatorProgress(
            "wifi-connect",
            "Connecting to the headset over Wi-Fi…",
            2,
            3));
        var connection = await ConnectWifiAdbAsync(host, port, cancellationToken).ConfigureAwait(false);
        progress?.Report(new OperatorProgress(
            "wifi-ready",
            "Wi-Fi ADB is connected and ready.",
            3,
            3));
        return new WifiAdbEnableResult(
            usbSerial,
            host,
            port,
            connection.Endpoint,
            addressProbe,
            tcpIpCommand,
            connection);
    }

    public async Task<WifiAdbConnectionResult> ConnectWifiAdbAsync(
        string host,
        int port = 5555,
        CancellationToken cancellationToken = default,
        IProgress<OperatorProgress>? progress = null)
    {
        host = AndroidInput.RequireWifiHost(host);
        port = AndroidInput.RequireTcpPort(port);
        var endpoint = AndroidInput.CreateWifiEndpoint(host, port);
        progress?.Report(new OperatorProgress(
            "wifi-connect",
            "Connecting to the Wi-Fi ADB endpoint…",
            0,
            2));
        var result = await RunAsync(
            new[] { "connect", endpoint },
            ConnectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"Connect to Wi-Fi ADB at {endpoint}");
        if (!AdbOutputParser.IsSuccessfulWifiConnect(result.StandardOutput, endpoint))
        {
            throw new InvalidOperationException(
                $"ADB did not confirm a connection to {endpoint}: {result.CondensedOutput}");
        }

        progress?.Report(new OperatorProgress(
            "wifi-verify",
            "Verifying the connected headset…",
            1,
            2));
        QuestDevice? device = null;
        for (var attempt = 0; attempt < 4 && device is null; attempt++)
        {
            var devices = await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            device = devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Serial, endpoint, StringComparison.OrdinalIgnoreCase));
            if (device is null && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }

        if (device is null)
        {
            throw new InvalidOperationException(
                $"ADB reported a connection to {endpoint}, but that endpoint did not appear in the device list.");
        }

        if (!device.IsReady)
        {
            throw new InvalidOperationException(
                $"Wi-Fi ADB endpoint {endpoint} is {device.State}. Approve debugging in the headset and reconnect.");
        }

        progress?.Report(new OperatorProgress(
            "wifi-ready",
            "Wi-Fi ADB is connected and ready.",
            2,
            2));
        return new WifiAdbConnectionResult(host, port, endpoint, result, device);
    }

    public async Task<CommandResult> DisconnectWifiAdbAsync(
        string host,
        int port = 5555,
        CancellationToken cancellationToken = default,
        IProgress<OperatorProgress>? progress = null)
    {
        var endpoint = AndroidInput.CreateWifiEndpoint(host, port);
        progress?.Report(new OperatorProgress(
            "wifi-disconnect",
            "Disconnecting the Wi-Fi ADB endpoint…",
            0,
            1));
        var result = await RunAsync(
            new[] { "disconnect", endpoint },
            ConnectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"Disconnect Wi-Fi ADB endpoint {endpoint}");
        var devices = await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (devices.Any(device =>
                string.Equals(device.Serial, endpoint, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"ADB accepted the disconnect request, but {endpoint} is still present in device readback.");
        }

        progress?.Report(new OperatorProgress(
            "wifi-disconnected",
            "The Wi-Fi ADB endpoint is disconnected.",
            1,
            1));
        return result;
    }

    public async Task<ParallelApkInstallResult> InstallApkOnManyWifiDevicesAsync(
        IReadOnlyList<string> serials,
        string apkPath,
        ApkInstallOptions? options = null,
        int maxParallelism = 4,
        CancellationToken cancellationToken = default,
        IProgress<OperatorProgress>? progress = null)
    {
        var targets = ValidateWifiInstallTargets(serials);
        var normalizedPath = ValidateInstallApkPath(apkPath);
        var arguments = CreateInstallArguments("install", options);
        arguments.Add(normalizedPath);
        return await InstallOnManyWifiDevicesAsync(
            targets,
            [normalizedPath],
            arguments,
            maxParallelism,
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    public async Task<ParallelApkInstallResult> InstallApkBundleOnManyWifiDevicesAsync(
        IReadOnlyList<string> serials,
        IReadOnlyList<string> apkPaths,
        ApkInstallOptions? options = null,
        int maxParallelism = 4,
        CancellationToken cancellationToken = default,
        IProgress<OperatorProgress>? progress = null)
    {
        var targets = ValidateWifiInstallTargets(serials);
        ArgumentNullException.ThrowIfNull(apkPaths);
        if (apkPaths.Count < 2)
        {
            throw new InvalidDataException("An APK bundle install requires at least two APK files.");
        }

        var normalizedPaths = apkPaths.Select(ValidateInstallApkPath).ToArray();
        if (normalizedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedPaths.Length)
        {
            throw new InvalidDataException("The APK bundle contains the same APK path more than once.");
        }

        var arguments = CreateInstallArguments("install-multiple", options);
        arguments.AddRange(normalizedPaths);
        return await InstallOnManyWifiDevicesAsync(
            targets,
            normalizedPaths,
            arguments,
            maxParallelism,
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    public async Task<ApkExportResult> ExportSingleApkAsync(
        string serial,
        string packageName,
        string outputPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var package = await InspectPackageAsync(serial, packageName, cancellationToken)
            .ConfigureAwait(false);
        if (package.ApkPaths.Count != 1)
        {
            throw new SplitPackageException(package.PackageName, package.ApkPaths);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullOutputPath), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The export destination must end in .apk.", nameof(outputPath));
        }

        var checksumPath = fullOutputPath + ".sha256";
        if (!overwrite && (File.Exists(fullOutputPath) || File.Exists(checksumPath)))
        {
            throw new IOException("The APK or checksum destination already exists. Choose another path or allow overwrite.");
        }

        await PullFileAsync(
            serial,
            package.ApkPaths[0],
            fullOutputPath,
            cancellationToken).ConfigureAwait(false);

        if (!File.Exists(fullOutputPath))
        {
            throw new IOException("ADB reported success but the exported APK was not created.");
        }

        var sha256 = await ComputeSha256Async(fullOutputPath, cancellationToken).ConfigureAwait(false);
        var checksumLine = $"{sha256.ToLowerInvariant()}  {Path.GetFileName(fullOutputPath)}{Environment.NewLine}";
        await File.WriteAllTextAsync(checksumPath, checksumLine, cancellationToken).ConfigureAwait(false);

        return new ApkExportResult(
            package.PackageName,
            package.ApkPaths[0],
            fullOutputPath,
            checksumPath,
            sha256,
            new FileInfo(fullOutputPath).Length);
    }

    public async Task<RustyKioskInstallationStatus> GetRustyKioskInstallationStatusAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var mainDump = await GetPackageDumpAsync(serial, RustyKioskContract.MainPackage, cancellationToken)
            .ConfigureAwait(false);
        var helperDump = await GetPackageDumpAsync(serial, RustyKioskContract.SetupHelperPackage, cancellationToken)
            .ConfigureAwait(false);
        var mainInstalled = mainDump.Succeeded && mainDump.StandardOutput.Contains(
            $"Package [{RustyKioskContract.MainPackage}]",
            StringComparison.Ordinal);
        var helperInstalled = helperDump.Succeeded && helperDump.StandardOutput.Contains(
            $"Package [{RustyKioskContract.SetupHelperPackage}]",
            StringComparison.Ordinal);
        var helperReady = helperInstalled && HasGrantedPermission(
            helperDump.StandardOutput,
            RustyKioskContract.WriteSecureSettingsPermission);
        var controlGranted = mainInstalled && HasGrantedPermission(
            mainDump.StandardOutput,
            RustyKioskContract.SetupControlPermission);
        var operatorAvailable = false;
        if (mainInstalled)
        {
            var contract = await RunForDeviceAsync(
                serial,
                [
                    "shell", "content", "call",
                    "--uri", RustyKioskContract.OperatorUri,
                    "--method", "contract"
                ],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            operatorAvailable = contract.Succeeded &&
                BundleBoolean(contract.StandardOutput, "accepted") == true &&
                string.Equals(
                    BundleValue(contract.StandardOutput, "schema"),
                    RustyKioskContract.HostOperatorSchema,
                    StringComparison.Ordinal);
        }

        return new RustyKioskInstallationStatus(
            mainInstalled,
            mainInstalled ? ParsePackageVersion(mainDump.StandardOutput) : null,
            helperInstalled,
            helperInstalled ? ParsePackageVersion(helperDump.StandardOutput) : null,
            helperReady,
            controlGranted,
            operatorAvailable);
    }

    public async Task<RustyKioskInstallResult> InstallRustyKioskAsync(
        string serial,
        RustyKioskBundle bundle,
        CancellationToken cancellationToken = default,
        IProgress<OperatorProgress>? progress = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentNullException.ThrowIfNull(bundle);
        progress?.Report(new OperatorProgress("kiosk-helper-install", "Installing Rusty Kiosk Setup…", 0, 4));
        var helperInstall = await InstallApkAsync(
            serial,
            bundle.SetupHelperApkPath,
            new ApkInstallOptions(ReplaceExisting: true, AllowDowngrade: true),
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new OperatorProgress("kiosk-helper-grant", "Provisioning the fixed setup helper…", 1, 4));
        var settingsGrant = await RunForDeviceAsync(
            serial,
            [
                "shell", "pm", "grant",
                RustyKioskContract.SetupHelperPackage,
                RustyKioskContract.WriteSecureSettingsPermission
            ],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        settingsGrant.EnsureSuccess("Provision Rusty Kiosk Setup");
        progress?.Report(new OperatorProgress("kiosk-main-install", "Installing Rusty Kiosk…", 2, 4));
        var mainInstall = await InstallApkAsync(
            serial,
            bundle.MainApkPath,
            new ApkInstallOptions(ReplaceExisting: true, AllowDowngrade: true),
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new OperatorProgress("kiosk-verify", "Verifying Rusty Kiosk setup authority…", 3, 4));
        var status = await GetRustyKioskInstallationStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        if (!status.SetupHelperReady || !status.SameSignerControlGranted || !status.HostOperatorAvailable)
        {
            throw new InvalidOperationException(
                "Rusty Kiosk installed, but its helper grant, same-signer control, or typed host operator did not verify.");
        }

        progress?.Report(new OperatorProgress("kiosk-ready", "Rusty Kiosk is installed and provisioned.", 4, 4));
        return new RustyKioskInstallResult(
            bundle,
            helperInstall,
            settingsGrant,
            mainInstall,
            status.SetupHelperReady,
            status.SameSignerControlGranted);
    }

    public async Task<RustyKioskProvisionResult> ProvisionRustyKioskAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var before = await GetRustyKioskInstallationStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        if (!before.SetupHelperInstalled || !before.MainInstalled)
        {
            throw new InvalidOperationException("Install both Rusty Kiosk APKs before provisioning the setup helper.");
        }

        var grant = await RunForDeviceAsync(
            serial,
            [
                "shell", "pm", "grant",
                RustyKioskContract.SetupHelperPackage,
                RustyKioskContract.WriteSecureSettingsPermission
            ],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        grant.EnsureSuccess("Provision Rusty Kiosk Setup");
        var status = await GetRustyKioskInstallationStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        if (!status.SetupHelperReady || !status.SameSignerControlGranted)
        {
            throw new InvalidOperationException("Rusty Kiosk Setup authority did not read back as ready.");
        }

        return new RustyKioskProvisionResult(
            grant,
            status.SetupHelperReady,
            status.SameSignerControlGranted,
            status);
    }

    public async Task<RustyKioskOperatorResult> InvokeRustyKioskAsync(
        string serial,
        RustyKioskCommand command,
        string? value = null,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        value = value?.Trim();
        if (command.RequiresValue() && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{command.ToWireName()} requires a value.", nameof(value));
        }

        if (!command.AllowsValue() && !string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{command.ToWireName()} does not accept a value.", nameof(value));
        }

        if ((value?.Length ?? 0) > 160)
        {
            throw new ArgumentException("Rusty Kiosk operator values may not exceed 160 characters.", nameof(value));
        }

        var requestId = "pc-" + Guid.NewGuid().ToString("N");
        var invokeArguments = new List<string>
        {
            "shell", "content", "call",
            "--uri", RustyKioskContract.OperatorUri,
            "--method", "invoke",
            "--arg", command.ToWireName(),
            "--extra", $"request_id:s:{requestId}"
        };
        if (!string.IsNullOrWhiteSpace(value))
        {
            invokeArguments.Add("--extra");
            invokeArguments.Add(
                "value_base64:s:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
        }

        var invoke = await RunForDeviceAsync(
            serial,
            invokeArguments,
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        invoke.EnsureSuccess($"Admit Rusty Kiosk {command.ToWireName()}");
        if (BundleBoolean(invoke.StandardOutput, "accepted") != true)
        {
            throw new InvalidOperationException(
                BundleValue(invoke.StandardOutput, "message") ??
                "Rusty Kiosk rejected the typed host request.");
        }

        var launch = await RunForDeviceAsync(
            serial,
            [
                "shell", "am", "start", "-W",
                "-n", RustyKioskContract.MainPackage + "/" + RustyKioskContract.MainActivity,
                "--es", RustyKioskContract.PendingRequestExtra, requestId
            ],
            ConnectionTimeout,
            cancellationToken).ConfigureAwait(false);
        launch.EnsureSuccess("Open Rusty Kiosk for typed host execution");
        if (launch.CondensedOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
            launch.CondensedOutput.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Rusty Kiosk could not execute the admitted request: {launch.CondensedOutput}");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RunForDeviceAsync(
                serial,
                [
                    "shell", "content", "call",
                    "--uri", RustyKioskContract.OperatorUri,
                    "--method", "result",
                    "--arg", requestId
                ],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            result.EnsureSuccess($"Read Rusty Kiosk {command.ToWireName()} result");
            if (BundleBoolean(result.StandardOutput, "completed") == true)
            {
                var encoded = BundleValue(result.StandardOutput, "result_base64")
                    ?? throw new InvalidDataException("Rusty Kiosk completed without a structured result.");
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var parsed = RustyKioskOperatorResult.Parse(json);
                if (!string.Equals(parsed.RequestId, requestId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Rusty Kiosk returned a mismatched request id.");
                }

                return parsed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Rusty Kiosk {command.ToWireName()} did not return a matching result within 15 seconds.");
    }

    public async Task<CommandResult> PullRustyKioskTagFileAsync(
        string serial,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var fullLocalPath = Path.GetFullPath(localPath);
        var parent = Path.GetDirectoryName(fullLocalPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using var output = new MemoryStream();
        int? expectedBytes = null;
        string? expectedSha = null;
        CommandResult? lastResult = null;
        while (expectedBytes is null || output.Length < expectedBytes.Value)
        {
            var offset = checked((int)output.Length);
            lastResult = await CallRustyKioskProviderAsync(
                serial,
                "tag-read",
                [$"offset:i:{offset}"],
                cancellationToken).ConfigureAwait(false);
            EnsureAcceptedProviderResult(lastResult, "Read Rusty Kiosk tag file");
            var total = BundleInteger(lastResult.StandardOutput, "total_bytes") ??
                throw new InvalidDataException("Rusty Kiosk tag readback omitted its total byte count.");
            var sha = BundleValue(lastResult.StandardOutput, "sha256") ??
                throw new InvalidDataException("Rusty Kiosk tag readback omitted its SHA-256.");
            expectedBytes ??= total;
            expectedSha ??= sha;
            if (total != expectedBytes ||
                !string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase) ||
                total is < 1 or > RustyKioskContract.MaxTagFileBytes)
            {
                throw new InvalidDataException("Rusty Kiosk tag file changed during the bounded export.");
            }

            var encoded = BundleValue(lastResult.StandardOutput, "data_base64") ?? string.Empty;
            var chunk = Convert.FromBase64String(encoded);
            if (chunk.Length == 0 || output.Length + chunk.Length > expectedBytes)
            {
                throw new InvalidDataException("Rusty Kiosk returned an invalid tag-file chunk.");
            }

            await output.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }

        var bytes = output.ToArray();
        var actualSha = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Rusty Kiosk tag-file SHA-256 readback did not match its bytes.");
        }

        var temporaryPath = fullLocalPath + ".incoming";
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
        RustyKioskTagFile.ValidateAndRead(temporaryPath);
        File.Move(temporaryPath, fullLocalPath, overwrite: true);
        return lastResult ?? throw new InvalidDataException("Rusty Kiosk returned no tag-file chunks.");
    }

    public async Task<CommandResult> PushRustyKioskTagFileAsync(
        string serial,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var json = RustyKioskTagFile.ValidateAndRead(localPath);
        var bytes = Encoding.UTF8.GetBytes(json);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var transferId = "pc-" + Guid.NewGuid().ToString("N");
        var common = new[]
        {
            $"transfer_id:s:{transferId}",
            $"total_bytes:i:{bytes.Length}",
            $"sha256:s:{sha}"
        };
        var begin = await CallRustyKioskProviderAsync(
            serial,
            "tag-write-begin",
            common,
            cancellationToken).ConfigureAwait(false);
        EnsureAcceptedProviderResult(begin, "Begin Rusty Kiosk tag transfer");

        const int chunkBytes = 6 * 1024;
        for (var offset = 0; offset < bytes.Length; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, bytes.Length - offset);
            var encoded = Convert.ToBase64String(bytes, offset, length);
            var chunk = await CallRustyKioskProviderAsync(
                serial,
                "tag-write-chunk",
                [
                    $"transfer_id:s:{transferId}",
                    $"offset:i:{offset}",
                    $"data_base64:s:{encoded}"
                ],
                cancellationToken).ConfigureAwait(false);
            EnsureAcceptedProviderResult(chunk, "Transfer Rusty Kiosk tag chunk");
            if (BundleInteger(chunk.StandardOutput, "offset") != offset + length)
            {
                throw new InvalidDataException("Rusty Kiosk did not acknowledge the complete ordered tag chunk.");
            }
        }

        var commit = await CallRustyKioskProviderAsync(
            serial,
            "tag-write-commit",
            common,
            cancellationToken).ConfigureAwait(false);
        EnsureAcceptedProviderResult(commit, "Commit Rusty Kiosk tag file");
        return commit;
    }

    public async Task<QuestControlStatus> GetQuestControlStatusAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var batteryTask = RunForDeviceAsync(serial, ["shell", "dumpsys", "battery"], InspectionTimeout, cancellationToken);
        var trackingTask = RunForDeviceAsync(serial, ["shell", "dumpsys", "tracking"], InspectionTimeout, cancellationToken);
        var powerTask = RunForDeviceAsync(serial, ["shell", "dumpsys", "power"], InspectionTimeout, cancellationToken);
        var proximityTask = RunForDeviceAsync(serial, ["shell", "dumpsys", "vrpowermanager"], InspectionTimeout, cancellationToken);
        var cpuTask = RunForDeviceAsync(serial, ["shell", "getprop", "debug.oculus.cpuLevel"], InspectionTimeout, cancellationToken);
        var gpuTask = RunForDeviceAsync(serial, ["shell", "getprop", "debug.oculus.gpuLevel"], InspectionTimeout, cancellationToken);
        await Task.WhenAll(batteryTask, trackingTask, powerTask, proximityTask, cpuTask, gpuTask).ConfigureAwait(false);
        var battery = await batteryTask.ConfigureAwait(false);
        var tracking = await trackingTask.ConfigureAwait(false);
        var power = await powerTask.ConfigureAwait(false);
        var proximity = await proximityTask.ConfigureAwait(false);
        var cpu = await cpuTask.ConfigureAwait(false);
        var gpu = await gpuTask.ConfigureAwait(false);
        battery.EnsureSuccess("Read headset battery");
        power.EnsureSuccess("Read headset power state");
        return QuestControlParser.Parse(
            battery.StandardOutput,
            tracking.Succeeded ? tracking.StandardOutput : string.Empty,
            power.StandardOutput,
            proximity.Succeeded ? proximity.StandardOutput : string.Empty,
            cpu.Succeeded ? cpu.StandardOutput : string.Empty,
            gpu.Succeeded ? gpu.StandardOutput : string.Empty,
            DateTimeOffset.Now);
    }

    public async Task<QuestKeepAwakeResult> SetQuestKeepAwakeAsync(
        string serial,
        bool enabled,
        int durationMilliseconds = 28_800_000,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        if (durationMilliseconds is < 60_000 or > 86_400_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMilliseconds),
                "Keep-awake duration must be between one minute and 24 hours.");
        }

        var commands = new List<CommandResult>();
        if (enabled)
        {
            commands.Add((await RunForDeviceAsync(
                serial,
                ["shell", "svc", "power", "stayon", "true"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess("Enable Quest stay-awake"));
            commands.Add((await RunForDeviceAsync(
                serial,
                ["shell", "input", "keyevent", "KEYCODE_WAKEUP"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess("Wake Quest display"));
            commands.Add((await RunForDeviceAsync(
                serial,
                [
                    "shell", "am", "broadcast",
                    "-a", "com.oculus.vrpowermanager.prox_close",
                    "--ei", "duration", durationMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess("Enable Quest proximity hold"));
        }
        else
        {
            commands.Add((await RunForDeviceAsync(
                serial,
                ["shell", "am", "broadcast", "-a", "com.oculus.vrpowermanager.automation_disable"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess("Restore normal Quest proximity"));
            commands.Add((await RunForDeviceAsync(
                serial,
                ["shell", "svc", "power", "stayon", "false"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess("Restore normal Quest stay-awake policy"));
        }

        var status = await GetQuestControlStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        return new QuestKeepAwakeResult(enabled, commands, status);
    }

    public async Task<QuestPerformanceResult> SetQuestPerformanceLevelsAsync(
        string serial,
        int? cpuLevel,
        int? gpuLevel,
        bool clear,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        if (!clear && cpuLevel is null && gpuLevel is null)
        {
            throw new ArgumentException("Choose at least one CPU or GPU level, or clear both overrides.");
        }

        ValidatePerformanceLevel(cpuLevel, nameof(cpuLevel));
        ValidatePerformanceLevel(gpuLevel, nameof(gpuLevel));
        var commands = new List<CommandResult>();
        if (clear || cpuLevel is not null)
        {
            commands.Add((await RunForDeviceAsync(
                serial,
                clear
                    ? ["shell", "setprop debug.oculus.cpuLevel ''"]
                    : ["shell", "setprop", "debug.oculus.cpuLevel", cpuLevel!.Value.ToString()],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess(clear ? "Clear Quest CPU override" : "Set Quest CPU level"));
        }

        if (clear || gpuLevel is not null)
        {
            commands.Add((await RunForDeviceAsync(
                serial,
                clear
                    ? ["shell", "setprop debug.oculus.gpuLevel ''"]
                    : ["shell", "setprop", "debug.oculus.gpuLevel", gpuLevel!.Value.ToString()],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess(clear ? "Clear Quest GPU override" : "Set Quest GPU level"));
        }

        var status = await GetQuestControlStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        return new QuestPerformanceResult(cpuLevel, gpuLevel, clear, commands, status);
    }

    private async Task<CommandResult> GetPackageDumpAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken) =>
        await RunForDeviceAsync(
            serial,
            ["shell", "dumpsys", "package", packageName],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);

    private async Task VerifyRemoteFileSizeAsync(
        string serial,
        string remotePath,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        var verify = await RunForDeviceAsync(
            serial,
            ["shell", $"stat -c %s -- {AndroidInput.ShellQuote(remotePath)}"],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        verify.EnsureSuccess($"Verify {remotePath}");
        if (!long.TryParse(
                verify.StandardOutput.Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var actualBytes) ||
            actualBytes != expectedBytes)
        {
            throw new InvalidDataException(
                $"The headset reported {actualBytes} bytes at {remotePath}; expected {expectedBytes} bytes.");
        }
    }

    private async Task<CommandResult> CallRustyKioskProviderAsync(
        string serial,
        string method,
        IReadOnlyList<string> typedExtras,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "shell", "content", "call",
            "--uri", RustyKioskContract.OperatorUri,
            "--method", method
        };
        foreach (var extra in typedExtras)
        {
            arguments.Add("--extra");
            arguments.Add(extra);
        }

        return await RunForDeviceAsync(
            serial,
            arguments,
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureAcceptedProviderResult(CommandResult result, string operation)
    {
        result.EnsureSuccess(operation);
        if (BundleBoolean(result.StandardOutput, "accepted") != true)
        {
            throw new InvalidOperationException(
                BundleValue(result.StandardOutput, "message") ?? $"{operation} was rejected.");
        }
    }

    private static bool HasGrantedPermission(string packageDump, string permission) =>
        Regex.IsMatch(
            packageDump,
            $@"(?m)^\s*{Regex.Escape(permission)}:\s+granted=true\s*$",
            RegexOptions.CultureInvariant);

    private static string? ParsePackageVersion(string packageDump)
    {
        var match = Regex.Match(packageDump, @"(?m)^\s*versionName=(?<value>\S+)\s*$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string? BundleValue(string output, string key)
    {
        var match = Regex.Match(
            output,
            $@"(?:^|[{{,]\s*){Regex.Escape(key)}=(?<value>.*?)(?=,\s*[A-Za-z0-9_]+=|}}\]|}}$)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static bool? BundleBoolean(string output, string key) =>
        bool.TryParse(BundleValue(output, key), out var value) ? value : null;

    private static int? BundleInteger(string output, string key) =>
        int.TryParse(
            BundleValue(output, key),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    private static void ValidatePerformanceLevel(int? value, string parameterName)
    {
        if (value is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Quest CPU/GPU level must be between 0 and 5.");
        }
    }

    private Task<CommandResult> RunForDeviceAsync(
        string serial,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var scoped = new List<string> { "-s", AndroidInput.RequireSerial(serial) };
        scoped.AddRange(arguments);
        return RunAsync(scoped, timeout, cancellationToken);
    }

    private async Task<StreamingCommandResult> StreamInstalledBaseApkAsync(
        string serial,
        string remotePath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        serial = AndroidInput.RequireSerial(serial);
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        if (maximumBytes is < 1 or > FleetIntegrationContract.MaximumPullBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (_runner is not IStreamingCommandRunner streamingRunner)
        {
            throw new InvalidOperationException(
                "The configured command runner does not support bounded installed-APK readback.");
        }

        var invariantMaximum = maximumBytes.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var command =
            $"candidate=$(realpath {AndroidInput.ShellQuote(remotePath)}) || {{ " +
            "printf 'qfm-integration:path-absent\\n' >&2; exit 41; }; " +
            "expected=\"$candidate\"; " +
            BuildOpenedRemoteHandleProof() +
            "if [ ! -f /proc/self/fd/3 ]; then " +
            "printf 'qfm-integration:path-not-file\\n' >&2; exit 44; fi; " +
            "size=$(stat -c %s -- /proc/self/fd/3) || { " +
            "printf 'qfm-integration:size-unavailable\\n' >&2; exit 45; }; " +
            "case \"$size\" in ''|*[!0-9]*) " +
            "printf 'qfm-integration:size-invalid\\n' >&2; exit 46;; esac; " +
            $"if [ \"$size\" -gt {invariantMaximum} ]; then " +
            "printf 'qfm-integration:maximum-bytes\\n' >&2; exit 47; fi; " +
            "exec cat <&3";
        var arguments = new[] { "-s", serial, "exec-out", "sh", "-c", command };
        var result = await streamingRunner.RunToStreamAsync(
            AdbPath,
            arguments,
            Stream.Null,
            maximumBytes,
            TransferTimeout,
            cancellationToken).ConfigureAwait(false);
        ThrowFleetRemoteError(
            result.CommandResult,
            "Read back installed base APK",
            maximumBytes);
        if (!string.IsNullOrWhiteSpace(result.CommandResult.StandardError))
        {
            throw new AdbCommandException(
                "Read back installed base APK",
                result.CommandResult with
                {
                    ExitCode = result.CommandResult.ExitCode == 0
                        ? 1
                        : result.CommandResult.ExitCode
                });
        }
        result.CommandResult.EnsureSuccess("Read back installed base APK");
        return result;
    }

    private static List<string> CreateInstallArguments(
        string installCommand,
        ApkInstallOptions? options)
    {
        options ??= new ApkInstallOptions();
        var arguments = new List<string> { installCommand };
        if (options.ReplaceExisting)
        {
            arguments.Add("-r");
        }

        if (options.AllowDowngrade)
        {
            arguments.Add("-d");
        }

        if (options.GrantRuntimePermissions)
        {
            arguments.Add("-g");
        }

        if (options.AllowTestPackages)
        {
            arguments.Add("-t");
        }

        return arguments;
    }

    private static string ValidateInstallApkPath(string apkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var fullApkPath = Path.GetFullPath(apkPath);
        if (!File.Exists(fullApkPath))
        {
            throw new FileNotFoundException("An APK bundle part was not found.", fullApkPath);
        }

        if (!string.Equals(Path.GetExtension(fullApkPath), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Every APK bundle input must end in .apk: {fullApkPath}",
                nameof(apkPath));
        }

        return fullApkPath;
    }

    private static IReadOnlyList<string> ValidateWifiInstallTargets(IReadOnlyList<string> serials)
    {
        ArgumentNullException.ThrowIfNull(serials);
        if (serials.Count < 2)
        {
            throw new ArgumentException(
                "Parallel installation requires at least two Wi-Fi ADB targets.",
                nameof(serials));
        }

        var targets = serials.Select(AndroidInput.RequireWifiSerial).ToArray();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
        {
            throw new ArgumentException(
                "Each Wi-Fi ADB target may appear only once.",
                nameof(serials));
        }

        return targets;
    }

    private async Task<ParallelApkInstallResult> InstallOnManyWifiDevicesAsync(
        IReadOnlyList<string> serials,
        IReadOnlyList<string> apkPaths,
        IReadOnlyList<string> installArguments,
        int maxParallelism,
        CancellationToken cancellationToken,
        IProgress<OperatorProgress>? progress)
    {
        maxParallelism = AndroidInput.RequireParallelism(maxParallelism);
        progress?.Report(new OperatorProgress(
            "parallel-install",
            $"Starting installation on {serials.Count} headsets…",
            0,
            serials.Count));
        using var gate = new SemaphoreSlim(Math.Min(maxParallelism, serials.Count));
        var progressGate = new object();
        var completedCount = 0;
        var tasks = serials.Select(InstallOneAsync).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new ParallelApkInstallResult(apkPaths.ToArray(), maxParallelism, results);

        async Task<TargetApkInstallResult> InstallOneAsync(string serial)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            TargetApkInstallResult targetResult;
            try
            {
                var result = await RunForDeviceAsync(
                    serial,
                    installArguments,
                    TransferTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    await GetThirdPartyPackageNamesAsync(serial, cancellationToken).ConfigureAwait(false);
                }

                targetResult = new TargetApkInstallResult(
                    serial,
                    result,
                    result.Succeeded ? null : result.CondensedOutput);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                targetResult = new TargetApkInstallResult(serial, null, exception.Message);
            }
            finally
            {
                gate.Release();
            }

            lock (progressGate)
            {
                completedCount++;
                progress?.Report(new OperatorProgress(
                    "parallel-install",
                    $"Finished {completedCount} of {serials.Count} headset installs…",
                    completedCount,
                    serials.Count));
            }
            return targetResult;
        }
    }

    private Task<CommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        _runner.RunAsync(AdbPath, arguments, timeout, cancellationToken);

    private static string BuildCanonicalRemoteProof(string remotePath)
    {
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        string relativePath;
        if (string.Equals(remotePath, FleetIntegrationContract.RemoteRoot, StringComparison.Ordinal))
        {
            relativePath = string.Empty;
        }
        else
        {
            var prefix = FleetIntegrationContract.RemoteRoot + "/";
            if (!remotePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The integration path must be below {FleetIntegrationContract.RemoteRoot}.",
                    nameof(remotePath));
            }
            relativePath = remotePath[prefix.Length..];
            FleetPathPolicy.ValidateRelativePath(relativePath, allowEmpty: false);
        }

        var expected = relativePath.Length == 0
            ? "expected=\"$root\"; "
            : $"expected=\"$root\"/{AndroidInput.ShellQuote(relativePath)}; ";
        return
            $"root=$(realpath {AndroidInput.ShellQuote(FleetIntegrationContract.RemoteRoot)}) || {{ " +
            "printf 'qfm-integration:root-unavailable\\n' >&2; exit 40; }; " +
            $"candidate=$(realpath {AndroidInput.ShellQuote(remotePath)}) || {{ " +
            "printf 'qfm-integration:path-absent\\n' >&2; exit 41; }; " +
            expected +
            "if [ \"$candidate\" != \"$expected\" ]; then " +
            "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; ";
    }

    private static string BuildOpenedRemoteHandleProof() =>
        "exec 3<\"$candidate\" || { " +
        "printf 'qfm-integration:path-open-failed\\n' >&2; exit 48; }; " +
        "opened=$(realpath /proc/self/fd/3) || { " +
        "printf 'qfm-integration:path-open-proof-failed\\n' >&2; exit 49; }; " +
        "if [ \"$opened\" != \"$expected\" ]; then " +
        "printf 'qfm-integration:path-indirection\\n' >&2; exit 42; fi; ";

    private static void ThrowFleetRemoteError(
        CommandResult result,
        string operation,
        long? maximumBytes = null)
    {
        var code = result.StandardError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line =>
                line.StartsWith("qfm-integration:", StringComparison.Ordinal));
        if (code is null)
        {
            return;
        }

        throw code switch
        {
            "qfm-integration:path-indirection" => new FleetRemotePathException(
                "remote_path_indirection",
                "The remote path contains a symlink or canonical indirection and was rejected."),
            "qfm-integration:path-absent" => new FleetRemotePathException(
                "remote_path_absent",
                "The remote path is absent."),
            "qfm-integration:parent-absent" => new FleetRemotePathException(
                "remote_parent_absent",
                "The remote push parent is absent."),
            "qfm-integration:destination-exists" => new FleetRemotePathException(
                "remote_destination_collision",
                "The remote push destination already exists; overwrite is forbidden."),
            "qfm-integration:partial-exists" => new FleetRemotePathException(
                "remote_partial_collision",
                "The operation-owned remote partial path already exists; replay or orphan reuse is forbidden."),
            "qfm-integration:atomic-publish-unavailable" => new FleetRemotePathException(
                "remote_atomic_publish_unavailable",
                "The remote filesystem could not atomically publish the verified payload without replacing a destination."),
            "qfm-integration:path-not-directory" => new FleetRemotePathException(
                "remote_path_not_directory",
                "The remote list target is not a directory."),
            "qfm-integration:path-not-file" => new FleetRemotePathException(
                "remote_path_not_file",
                "The remote pull target is not a regular file."),
            "qfm-integration:maximum-entries" => new FleetRemotePathException(
                "entry_limit_exceeded",
                "The remote directory contains more entries than the requested limit."),
            "qfm-integration:unsupported-entry-type" => new FleetRemotePathException(
                "remote_entry_type_unsupported",
                "The remote directory contains a symbolic link or another unsupported entry type."),
            "qfm-integration:entry-name-unrepresentable" => new FleetRemotePathException(
                "remote_entry_name_rejected",
                "The remote directory contains an entry name that cannot be represented safely."),
            "qfm-integration:maximum-bytes" => new FleetTransferLimitException(
                maximumBytes
                ?? throw new InvalidOperationException(
                    "A maximum-bytes proof failure did not retain its request bound.")),
            "qfm-integration:root-unavailable" => new FleetRemotePathException(
                "remote_root_unavailable",
                $"The canonical {FleetIntegrationContract.RemoteRoot} root is unavailable."),
            "qfm-integration:size-unavailable" or
            "qfm-integration:size-invalid" => new FleetRemotePathException(
                "remote_size_unavailable",
                "The remote file size could not be proven."),
            "qfm-integration:path-open-failed" or
            "qfm-integration:path-open-proof-failed" => new FleetRemotePathException(
                "remote_path_open_failed",
                "The remote path could not be opened and rebound to the canonical proof."),
            "qfm-integration:push-size-mismatch" or
            "qfm-integration:push-digest-mismatch" or
            "qfm-integration:push-readback-mismatch" => new FleetRemotePathException(
                "remote_push_readback_mismatch",
                "The remote push staging or final readback did not match the requested size and SHA-256."),
            "qfm-integration:partial-cleanup-failed" => new FleetRemotePathException(
                "remote_cleanup_required",
                "The remote payload committed, but its operation-owned partial file could not be removed."),
            _ => new FleetRemotePathException(
                "remote_proof_failed",
                $"{operation} failed its remote containment proof.")
        };
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
