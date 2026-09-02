namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    /// <summary>
    /// Removes one exact installed base APK and its app-private data. This is a
    /// cleanup primitive only: callers must separately prove that the pre-run
    /// installed-state snapshot was absent and that this run owned the install.
    /// </summary>
    public async Task<ExactApkUninstallResult> UninstallExactApkAsync(
        string serial,
        string apkPath,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var reportedPath = Path.GetFullPath(apkPath);
        if (!File.Exists(reportedPath))
        {
            throw new FileNotFoundException("The APK to uninstall was not found.", reportedPath);
        }
        if (!string.Equals(Path.GetExtension(reportedPath), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The uninstall input must be an .apk file.", nameof(apkPath));
        }

        using var admission = await ImmutableApkAdmission.CreateAsync(
            reportedPath,
            cancellationToken).ConfigureAwait(false);
        var inspector = CreateApkInspector();
        var admittedArtifact = await inspector
            .InspectAsync(admission.Path, cancellationToken)
            .ConfigureAwait(false);
        RejectSplitArtifact(admittedArtifact);
        var artifact = admittedArtifact with { Path = reportedPath };

        // Re-prove the immutable input and the selected serial before the final
        // installed-identity read. Installed identity is intentionally the last
        // bounded device observation before the only mutating command, closing
        // the route over the freshest exact preimage QFM can attest.
        var currentArtifact = await inspector
            .InspectAsync(admission.Path, cancellationToken)
            .ConfigureAwait(false);
        if (currentArtifact.SizeBytes != admittedArtifact.SizeBytes ||
            !string.Equals(currentArtifact.Sha256, admittedArtifact.Sha256, StringComparison.Ordinal) ||
            currentArtifact.Identity != admittedArtifact.Identity)
        {
            throw new InvalidDataException(
                "The APK changed while it was being admitted for exact uninstall.");
        }
        var selectedRows = (await GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(device => string.Equals(device.Serial, serial, StringComparison.Ordinal))
            .ToArray();
        if (selectedRows.Length != 1 || !selectedRows[0].IsReady)
        {
            throw new InvalidDataException(
                "ADB discovery did not return exactly one ready row for the selected serial immediately before uninstall.");
        }

        var installed = await ReadInstalledIdentityAsync(
            serial,
            artifact,
            cancellationToken).ConfigureAwait(false);
        EnsureSameArtifact(artifact, installed);
        if (installed.ApkPaths.Count != 1)
        {
            throw new InvalidDataException(
                "Exact inspected-APK uninstall rejects installed split package sets.");
        }

        CommandResult? uninstall = null;
        try
        {
            uninstall = await RunForDeviceAsync(
                serial,
                ["uninstall", artifact.Identity.PackageName],
                TransferTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new ExactApkUninstallResult(
                serial,
                artifact,
                installed,
                UninstallCommand: null,
                ExactApkUninstallDisposition.CleanupUnknown,
                UnscopedPackageAbsent: null,
                CurrentUserPackageAbsent: null,
                "The fixed uninstall command began but did not return a trustworthy terminal result.");
        }

        if (!uninstall.Succeeded ||
            !string.Equals(uninstall.StandardOutput.Trim(), "Success", StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(uninstall.StandardError))
        {
            return new ExactApkUninstallResult(
                serial,
                artifact,
                installed,
                uninstall,
                ExactApkUninstallDisposition.CleanupUnknown,
                UnscopedPackageAbsent: null,
                CurrentUserPackageAbsent: null,
                "The fixed uninstall command returned an unrecognized or unsuccessful terminal result.");
        }

        try
        {
            // Once mutation has begun, finish the bounded absence readback even
            // if the caller's cancellation token changes state.
            var unscopedAbsent = await ReadUnscopedPackageAbsenceAsync(
                serial,
                artifact.Identity.PackageName,
                CancellationToken.None).ConfigureAwait(false);
            var currentUserAbsent = await ReadCurrentUserPackageAbsenceAsync(
                serial,
                artifact.Identity.PackageName,
                CancellationToken.None).ConfigureAwait(false);
            if (unscopedAbsent && currentUserAbsent)
            {
                return new ExactApkUninstallResult(
                    serial,
                    artifact,
                    installed,
                    uninstall,
                    ExactApkUninstallDisposition.ConfirmedAbsent,
                    UnscopedPackageAbsent: true,
                    CurrentUserPackageAbsent: true,
                    "Both fixed package-manager scopes confirmed package absence after uninstall.");
            }

            return new ExactApkUninstallResult(
                serial,
                artifact,
                installed,
                uninstall,
                ExactApkUninstallDisposition.StillPresent,
                unscopedAbsent,
                currentUserAbsent,
                "Package absence was not confirmed in both fixed package-manager scopes.");
        }
        catch (Exception)
        {
            return new ExactApkUninstallResult(
                serial,
                artifact,
                installed,
                uninstall,
                ExactApkUninstallDisposition.CleanupUnknown,
                UnscopedPackageAbsent: null,
                CurrentUserPackageAbsent: null,
                "The uninstall command succeeded, but fixed package-absence readback did not complete.");
        }
    }

    private async Task<bool> ReadUnscopedPackageAbsenceAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken)
    {
        var packagePath = await RunForDeviceAsync(
            serial,
            ["shell", $"pm path {AndroidInput.ShellQuote(packageName)}"],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (await IsConfirmedSilentPackageAbsenceAsync(
            serial,
            packageName,
            packagePath,
            cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        RequireRecognizedPackagePresence(packagePath, "unscoped");
        return false;
    }

    private async Task<bool> ReadCurrentUserPackageAbsenceAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken)
    {
        var packagePath = await RunForDeviceAsync(
            serial,
            ["shell", $"pm path --user current {AndroidInput.ShellQuote(packageName)}"],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (await IsConfirmedSilentCurrentUserPackageAbsenceForStopAsync(
            serial,
            packageName,
            packagePath,
            cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        RequireRecognizedPackagePresence(packagePath, "current-user");
        return false;
    }

    private static void RequireRecognizedPackagePresence(
        CommandResult packagePath,
        string scope)
    {
        packagePath.EnsureSuccess($"Read {scope} package presence after exact uninstall");
        RequireSilentStandardError(packagePath, $"Read {scope} package presence after exact uninstall");
        var lines = packagePath.StandardOutput.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 ||
            lines.Any(static line =>
                !line.StartsWith("package:/", StringComparison.Ordinal) ||
                !line["package:".Length..].EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) ||
            AdbOutputParser.ParsePackagePaths(packagePath.StandardOutput).Count == 0)
        {
            throw new InvalidDataException(
                $"The {scope} post-uninstall package readback was malformed or incomplete.");
        }
    }
}
