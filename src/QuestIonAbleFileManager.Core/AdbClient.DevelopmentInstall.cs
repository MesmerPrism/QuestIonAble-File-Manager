namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    public async Task<DevelopmentApkInstallResult> InstallDevelopmentApkAsync(
        string serial, string apkPath, ApkInstallOptions? options = null,
        CancellationToken cancellationToken = default, Action? deviceDispatchObserved = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        var reportedPath = Path.GetFullPath(apkPath);
        if (!File.Exists(reportedPath))
            throw new FileNotFoundException("The APK to install was not found.", reportedPath);
        if (!string.Equals(Path.GetExtension(reportedPath), ".apk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The install input must be an .apk file.", nameof(apkPath));
        using var admission = await ImmutableApkAdmission.CreateAsync(reportedPath, cancellationToken).ConfigureAwait(false);
        var artifact = await CreateApkInspector().InspectAsync(admission.Path, cancellationToken).ConfigureAwait(false);
        RejectSplitArtifact(artifact);
        var targets = (await GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(device => string.Equals(device.Serial, serial, StringComparison.Ordinal)).ToArray();
        if (targets.Length != 1 || !targets[0].IsReady)
            throw new InvalidOperationException("Development installation requires one exact ready serial.");
        var arguments = CreateInstallArguments("install", options);
        arguments.Add(admission.Path);
        cancellationToken.ThrowIfCancellationRequested();
        deviceDispatchObserved?.Invoke();
        var commandResult = await RunForDeviceAsync(serial, arguments, TransferTimeout, cancellationToken).ConfigureAwait(false);
        commandResult.EnsureSuccess("Install development APK");
        var metadata = await ReadDevelopmentMetadataAsync(serial, artifact.Identity.PackageName, cancellationToken).ConfigureAwait(false);
        if (metadata.VersionCode != artifact.Identity.VersionCode ||
            !string.Equals(metadata.VersionName, artifact.Identity.VersionName, StringComparison.Ordinal))
            throw new InvalidDataException("Installed development metadata differs from the local APK version.");
        var after = await ReadDevelopmentMetadataAsync(serial, artifact.Identity.PackageName, cancellationToken).ConfigureAwait(false);
        if (metadata.VersionCode != after.VersionCode || metadata.VersionName != after.VersionName ||
            metadata.LastUpdateTime != after.LastUpdateTime || !metadata.ApkPaths.SequenceEqual(after.ApkPaths, StringComparer.Ordinal))
            throw new InvalidDataException("Installed development metadata changed during confirmation.");
        return new(serial, artifact with { Path = reportedPath }, metadata, commandResult);
    }
}
