using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public enum ApkPropertyMutationAction
{
    Clear,
    Restore
}

public enum ApkPropertyMutationDisposition
{
    Confirmed,
    StillDivergent,
    CleanupUnknown
}

public sealed record ApkPropertyFileIdentity(
    string Path,
    string Sha256,
    long SizeBytes);

public sealed record ApkPropertyManifestIdentity(
    string Schema,
    string OwnerPackage,
    string Scope,
    IReadOnlyList<string> Prefixes,
    int PropertyCount,
    ApkPropertyFileIdentity File);

public sealed record ApkPropertyObservationResult(
    ApkArtifactInspection Artifact,
    InstalledApkIdentity Installed,
    ApkPropertyManifestIdentity Manifest,
    ApkPropertyFileIdentity Snapshot,
    DateTimeOffset ObservedAt,
    int SetPropertyCount,
    int UnsetPropertyCount);

public sealed record ApkPropertyMutationResult(
    ApkPropertyMutationAction Action,
    ApkArtifactInspection Artifact,
    InstalledApkIdentity InstalledBeforeDispatch,
    InstalledApkIdentity? InstalledAfterReadback,
    ApkPropertyManifestIdentity Manifest,
    ApkPropertyFileIdentity Snapshot,
    ApkPropertyMutationDisposition Disposition,
    int CommandsSent,
    IReadOnlyList<string> DivergentProperties,
    string Detail)
{
    public bool Confirmed => Disposition == ApkPropertyMutationDisposition.Confirmed &&
                             DivergentProperties.Count == 0 &&
                             InstalledAfterReadback?.Identity is not null;
}

public sealed partial class AdbClient
{
    private const string AndroidPropertyManifestSchema =
        "rusty.quest.android_property_manifest.v1";
    private const string ApkPropertySnapshotSchema =
        "questionable.file_manager.apk_property_snapshot.v1";
    private const int MaximumPropertyManifestBytes = 1024 * 1024;
    private const int MaximumPropertySnapshotBytes = 2 * 1024 * 1024;
    private const int MaximumManagedProperties = 1024;
    private const int MaximumPropertyValueLength = 4096;
    private static readonly Regex ManagedPropertyPattern = new(
        "^debug\\.rustyquest\\.[A-Za-z0-9_.-]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly JsonSerializerOptions PropertySnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public async Task<ApkPropertyObservationResult> ObserveExactApkPropertiesAsync(
        string serial,
        string apkPath,
        string propertyManifestPath,
        string outputSnapshotPath,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        var reportedApk = RequireExistingFile(apkPath, ".apk", "APK property observation");
        var reportedManifest = RequireExistingFile(
            propertyManifestPath,
            ".json",
            "APK property manifest");
        var reportedOutput = RequireCreateNewOutput(outputSnapshotPath);

        using var admission = await ImmutableApkAdmission.CreateManyAsync(
            [reportedApk, reportedManifest],
            cancellationToken).ConfigureAwait(false);
        var admitted = admission.Paths;
        var artifact = await CreateApkInspector()
            .InspectAsync(admitted[0], cancellationToken).ConfigureAwait(false);
        RejectSplitArtifact(artifact);
        artifact = artifact with { Path = reportedApk };
        var manifest = await ReadPropertyManifestAsync(
            admitted[1],
            reportedManifest,
            artifact.Identity.PackageName,
            cancellationToken).ConfigureAwait(false);

        await RequireExactReadySerialAsync(serial, cancellationToken).ConfigureAwait(false);
        var installed = await ReadInstalledIdentityAsync(
            serial,
            artifact,
            cancellationToken).ConfigureAwait(false);
        EnsureSameArtifact(artifact, installed);
        var values = await ReadPropertiesAsync(
            serial,
            manifest.Properties,
            cancellationToken).ConfigureAwait(false);
        var observedAt = DateTimeOffset.UtcNow;
        var snapshotDocument = CreateSnapshotDocument(
            serial,
            artifact,
            manifest.Identity,
            values,
            observedAt);
        var snapshot = WriteCreateNewSnapshot(reportedOutput, snapshotDocument);

        return new ApkPropertyObservationResult(
            artifact,
            installed,
            manifest.Identity,
            snapshot,
            observedAt,
            values.Count(static value => value.IsSet),
            values.Count(static value => !value.IsSet));
    }

    public Task<ApkPropertyMutationResult> ClearExactApkPropertiesAsync(
        string serial,
        string apkPath,
        string propertyManifestPath,
        string snapshotPath,
        CancellationToken cancellationToken = default) =>
        ClearExactApkPropertiesAsync(
            serial,
            apkPath,
            propertyManifestPath,
            snapshotPath,
            cancellationToken,
            dispatchProgress: null);

    internal Task<ApkPropertyMutationResult> ClearExactApkPropertiesAsync(
        string serial,
        string apkPath,
        string propertyManifestPath,
        string snapshotPath,
        CancellationToken cancellationToken,
        Action<OperatorMutationStage>? dispatchProgress) =>
        MutateExactApkPropertiesAsync(
            ApkPropertyMutationAction.Clear,
            serial,
            apkPath,
            propertyManifestPath,
            snapshotPath,
            cancellationToken,
            dispatchProgress);

    public Task<ApkPropertyMutationResult> RestoreExactApkPropertiesAsync(
        string serial,
        string apkPath,
        string propertyManifestPath,
        string snapshotPath,
        CancellationToken cancellationToken = default) =>
        RestoreExactApkPropertiesAsync(
            serial,
            apkPath,
            propertyManifestPath,
            snapshotPath,
            cancellationToken,
            dispatchProgress: null);

    internal Task<ApkPropertyMutationResult> RestoreExactApkPropertiesAsync(
        string serial,
        string apkPath,
        string propertyManifestPath,
        string snapshotPath,
        CancellationToken cancellationToken,
        Action<OperatorMutationStage>? dispatchProgress) =>
        MutateExactApkPropertiesAsync(
            ApkPropertyMutationAction.Restore,
            serial,
            apkPath,
            propertyManifestPath,
            snapshotPath,
            cancellationToken,
            dispatchProgress);

    private async Task<ApkPropertyMutationResult> MutateExactApkPropertiesAsync(
        ApkPropertyMutationAction action,
        string serial,
        string apkPath,
        string propertyManifestPath,
        string snapshotPath,
        CancellationToken cancellationToken,
        Action<OperatorMutationStage>? dispatchProgress)
    {
        serial = AndroidInput.RequireSerial(serial);
        var reportedApk = RequireExistingFile(apkPath, ".apk", "APK property mutation");
        var reportedManifest = RequireExistingFile(
            propertyManifestPath,
            ".json",
            "APK property manifest");
        var reportedSnapshot = RequireExistingFile(
            snapshotPath,
            ".json",
            "APK property snapshot");

        using var admission = await ImmutableApkAdmission.CreateManyAsync(
            [reportedApk, reportedManifest, reportedSnapshot],
            cancellationToken).ConfigureAwait(false);
        var admitted = admission.Paths;
        var artifact = await CreateApkInspector()
            .InspectAsync(admitted[0], cancellationToken).ConfigureAwait(false);
        RejectSplitArtifact(artifact);
        artifact = artifact with { Path = reportedApk };
        var manifest = await ReadPropertyManifestAsync(
            admitted[1],
            reportedManifest,
            artifact.Identity.PackageName,
            cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadSnapshotAsync(
            admitted[2],
            reportedSnapshot,
            serial,
            artifact,
            manifest,
            cancellationToken).ConfigureAwait(false);
        var installedBefore = await ReadInstalledIdentityAsync(
            serial,
            artifact,
            cancellationToken).ConfigureAwait(false);
        EnsureSameArtifact(artifact, installedBefore);

        if (action == ApkPropertyMutationAction.Clear)
        {
            var current = await ReadPropertiesAsync(
                serial,
                manifest.Properties,
                cancellationToken).ConfigureAwait(false);
            if (!PropertyValuesEqual(current, snapshot.Values))
            {
                throw new InvalidDataException(
                    "The exact property snapshot is stale; current property bytes differ before clear dispatch.");
            }
        }

        // This is deliberately the final pre-dispatch probe. No property effect is
        // allowed between this exact ready-serial readback and the fixed loop below.
        await RequireExactReadySerialAsync(serial, cancellationToken).ConfigureAwait(false);
        var desired = action == ApkPropertyMutationAction.Clear
            ? manifest.Properties.Select(static name => new PropertyValue(name, string.Empty)).ToArray()
            : snapshot.Values.ToArray();
        var commandsSent = 0;
        var pendingReported = false;
        void ReportPending()
        {
            if (pendingReported)
                return;
            pendingReported = true;
            dispatchProgress?.Invoke(OperatorMutationStage.Pending);
        }
        try
        {
            foreach (var value in desired)
            {
                if (commandsSent == 0)
                    dispatchProgress?.Invoke(OperatorMutationStage.Sent);
                var command = await RunForDeviceAsync(
                    serial,
                    ["shell", "setprop", value.Name, value.Value],
                    InspectionTimeout,
                    cancellationToken).ConfigureAwait(false);
                commandsSent++;
                if (!command.Succeeded ||
                    !string.IsNullOrWhiteSpace(command.StandardOutput) ||
                    !string.IsNullOrWhiteSpace(command.StandardError))
                {
                    ReportPending();
                    return UnknownMutation(
                        action,
                        artifact,
                        installedBefore,
                        manifest.Identity,
                        snapshot.File,
                        commandsSent,
                        "A fixed property command returned an unsuccessful or non-silent result.");
                }
            }
        }
        catch (Exception exception) when (
            exception is AdbCommandException or TimeoutException or IOException or
            OperationCanceledException)
        {
            ReportPending();
            return UnknownMutation(
                action,
                artifact,
                installedBefore,
                manifest.Identity,
                snapshot.File,
                commandsSent,
                "Property dispatch began but did not return a trustworthy terminal result.");
        }

        ReportPending();
        IReadOnlyList<PropertyValue> readback;
        InstalledApkIdentity installedAfter;
        try
        {
            readback = await ReadPropertiesAsync(
                serial,
                manifest.Properties,
                CancellationToken.None).ConfigureAwait(false);
            installedAfter = await ReadInstalledIdentityAsync(
                serial,
                artifact,
                CancellationToken.None).ConfigureAwait(false);
            EnsureSameArtifact(artifact, installedAfter);
        }
        catch (Exception exception) when (
            exception is AdbCommandException or TimeoutException or IOException or
            InvalidDataException or OperationCanceledException or PackageNotInstalledException)
        {
            return UnknownMutation(
                action,
                artifact,
                installedBefore,
                manifest.Identity,
                snapshot.File,
                commandsSent,
                "Property mutation completed, but exact property or installed-artifact readback did not complete.");
        }

        var expected = desired.ToDictionary(static value => value.Name, StringComparer.Ordinal);
        var divergent = readback
            .Where(value => !expected.TryGetValue(value.Name, out var required) ||
                            !string.Equals(value.Value, required.Value, StringComparison.Ordinal))
            .Select(static value => value.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var disposition = divergent.Length == 0
            ? ApkPropertyMutationDisposition.Confirmed
            : ApkPropertyMutationDisposition.StillDivergent;
        var detail = divergent.Length == 0
            ? action == ApkPropertyMutationAction.Clear
                ? "Every manifest property read back unset while the exact APK remained installed."
                : "Every manifest property read back at its exact snapshot value while the exact APK remained installed."
            : "One or more manifest properties did not read back at the requested exact value.";
        return new ApkPropertyMutationResult(
            action,
            artifact,
            installedBefore,
            installedAfter,
            manifest.Identity,
            snapshot.File,
            disposition,
            commandsSent,
            new ReadOnlyCollection<string>(divergent),
            detail);
    }

    private static ApkPropertyMutationResult UnknownMutation(
        ApkPropertyMutationAction action,
        ApkArtifactInspection artifact,
        InstalledApkIdentity installedBefore,
        ApkPropertyManifestIdentity manifest,
        ApkPropertyFileIdentity snapshot,
        int commandsSent,
        string detail) =>
        new(
            action,
            artifact,
            installedBefore,
            InstalledAfterReadback: null,
            manifest,
            snapshot,
            ApkPropertyMutationDisposition.CleanupUnknown,
            commandsSent,
            Array.Empty<string>(),
            detail);

    private async Task RequireExactReadySerialAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var matching = (await GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(device => string.Equals(device.Serial, serial, StringComparison.Ordinal))
            .ToArray();
        if (matching.Length != 1 || !matching[0].IsReady)
        {
            throw new InvalidDataException(
                "ADB discovery did not return exactly one ready row for the selected serial.");
        }
    }

    private async Task<IReadOnlyList<PropertyValue>> ReadPropertiesAsync(
        string serial,
        IReadOnlyList<string> properties,
        CancellationToken cancellationToken)
    {
        var values = new List<PropertyValue>(properties.Count);
        foreach (var property in properties)
        {
            var result = await RunForDeviceAsync(
                serial,
                ["shell", "getprop", property],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            result.EnsureSuccess($"Read managed property {property}");
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                throw new InvalidDataException("Managed property readback returned standard-error bytes.");
            }
            var value = ParsePropertyValue(result.StandardOutput);
            values.Add(new PropertyValue(property, value));
        }
        return new ReadOnlyCollection<PropertyValue>(values);
    }

    private static string ParsePropertyValue(string output)
    {
        var normalized = output.ReplaceLineEndings("\n");
        if (normalized.EndsWith('\n'))
            normalized = normalized[..^1];
        if (normalized.Contains('\n') || normalized.Contains('\0') ||
            normalized.Length > MaximumPropertyValueLength)
        {
            throw new InvalidDataException("Managed property readback was multiline, contained NUL, or exceeded its bound.");
        }
        return normalized;
    }

    private static async Task<PropertyManifest> ReadPropertyManifestAsync(
        string admittedPath,
        string reportedPath,
        string artifactPackage,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedFileAsync(
            admittedPath,
            MaximumPropertyManifestBytes,
            "property manifest",
            cancellationToken).ConfigureAwait(false);
        using var document = ParseStrictJson(bytes, "property manifest", maximumDepth: 8);
        var root = document.RootElement;
        RequireObjectProperties(
            root,
            ["schema", "owner_package", "scope", "prefixes", "properties"],
            "property manifest");
        RequireString(root, "schema", AndroidPropertyManifestSchema);
        var owner = AndroidInput.RequirePackageName(RequireString(root, "owner_package"));
        if (!string.Equals(owner, artifactPackage, StringComparison.Ordinal))
            throw new InvalidDataException("Property manifest owner_package does not equal the inspected APK package.");
        RequireString(root, "scope", "complete-source-consumer-surface");
        var prefixes = RequireOrderedUniqueStrings(root, "prefixes", 1, 32);
        if (prefixes.Any(prefix => !prefix.StartsWith("debug.rustyquest.", StringComparison.Ordinal) ||
                                   !prefix.EndsWith(".", StringComparison.Ordinal)))
            throw new InvalidDataException("Property manifest prefixes must be closed debug.rustyquest prefixes.");

        var propertyArray = root.GetProperty("properties");
        if (propertyArray.ValueKind != JsonValueKind.Array ||
            propertyArray.GetArrayLength() is < 1 or > MaximumManagedProperties)
            throw new InvalidDataException("Property manifest must contain 1..1024 properties.");
        var properties = new List<string>(propertyArray.GetArrayLength());
        foreach (var item in propertyArray.EnumerateArray())
        {
            RequireObjectProperties(item, ["name"], "property manifest item");
            var name = RequireString(item, "name");
            if (!ManagedPropertyPattern.IsMatch(name) ||
                !prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
                throw new InvalidDataException("Property manifest contains a name outside its closed prefixes.");
            properties.Add(name);
        }
        RequireOrdinallySortedUnique(properties, "property manifest properties");
        var file = new ApkPropertyFileIdentity(
            reportedPath,
            Sha256(bytes),
            bytes.LongLength);
        var identity = new ApkPropertyManifestIdentity(
            AndroidPropertyManifestSchema,
            owner,
            "complete-source-consumer-surface",
            new ReadOnlyCollection<string>(prefixes.ToArray()),
            properties.Count,
            file);
        return new PropertyManifest(
            identity,
            new ReadOnlyCollection<string>(properties));
    }

    private static SnapshotMaterial CreateSnapshotDocument(
        string serial,
        ApkArtifactInspection artifact,
        ApkPropertyManifestIdentity manifest,
        IReadOnlyList<PropertyValue> values,
        DateTimeOffset observedAt) =>
        new(
            ApkPropertySnapshotSchema,
            observedAt,
            serial,
            new SnapshotApk(
                artifact.Identity.PackageName,
                artifact.Identity.VersionCode,
                artifact.Identity.VersionName,
                artifact.Identity.SignerSha256,
                artifact.Sha256,
                artifact.SizeBytes),
            new SnapshotManifest(
                manifest.Schema,
                manifest.OwnerPackage,
                manifest.Scope,
                manifest.File.Sha256,
                manifest.File.SizeBytes,
                manifest.PropertyCount),
            values.Select(static value => new SnapshotProperty(value.Name, value.Value)).ToArray());

    private static ApkPropertyFileIdentity WriteCreateNewSnapshot(
        string outputPath,
        SnapshotMaterial snapshot)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(snapshot, PropertySnapshotJson);
        var bytes = new byte[serialized.Length + 1];
        serialized.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';
        using (var output = new FileStream(
                   outputPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        var observed = File.ReadAllBytes(outputPath);
        if (!observed.AsSpan().SequenceEqual(bytes))
            throw new IOException("Create-new property snapshot readback did not match written bytes.");
        return new ApkPropertyFileIdentity(outputPath, Sha256(bytes), bytes.LongLength);
    }

    private static async Task<Snapshot> ReadSnapshotAsync(
        string admittedPath,
        string reportedPath,
        string serial,
        ApkArtifactInspection artifact,
        PropertyManifest manifest,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedFileAsync(
            admittedPath,
            MaximumPropertySnapshotBytes,
            "property snapshot",
            cancellationToken).ConfigureAwait(false);
        ValidateSnapshotShape(bytes);
        SnapshotMaterial material;
        try
        {
            material = JsonSerializer.Deserialize<SnapshotMaterial>(bytes, PropertySnapshotJson) ??
                throw new InvalidDataException("Property snapshot JSON was null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Property snapshot JSON is malformed or open-ended.", exception);
        }
        if (material.Schema != ApkPropertySnapshotSchema ||
            material.ObservedAt == default ||
            !string.Equals(material.Serial, serial, StringComparison.Ordinal) ||
            material.Apk is null || material.Manifest is null || material.Properties is null)
            throw new InvalidDataException("Property snapshot root identity is incomplete or does not match the selected serial.");
        if (material.Apk.PackageName != artifact.Identity.PackageName ||
            material.Apk.VersionCode != artifact.Identity.VersionCode ||
            material.Apk.VersionName != artifact.Identity.VersionName ||
            material.Apk.SignerSha256 != artifact.Identity.SignerSha256 ||
            material.Apk.Sha256 != artifact.Sha256 ||
            material.Apk.SizeBytes != artifact.SizeBytes)
            throw new InvalidDataException("Property snapshot APK identity does not match the immutable inspected artifact.");
        var expectedManifest = manifest.Identity;
        if (material.Manifest.Schema != expectedManifest.Schema ||
            material.Manifest.OwnerPackage != expectedManifest.OwnerPackage ||
            material.Manifest.Scope != expectedManifest.Scope ||
            material.Manifest.Sha256 != expectedManifest.File.Sha256 ||
            material.Manifest.SizeBytes != expectedManifest.File.SizeBytes ||
            material.Manifest.PropertyCount != expectedManifest.PropertyCount)
            throw new InvalidDataException("Property snapshot manifest identity does not match the immutable closed manifest.");
        if (material.Properties.Count != manifest.Properties.Count)
            throw new InvalidDataException("Property snapshot does not contain the complete manifest property set.");
        var values = new List<PropertyValue>(material.Properties.Count);
        for (var index = 0; index < material.Properties.Count; index++)
        {
            var property = material.Properties[index] ??
                throw new InvalidDataException("Property snapshot contains a null property record.");
            if (!string.Equals(property.Name, manifest.Properties[index], StringComparison.Ordinal) ||
                property.Value is null || property.Value.Contains('\n') || property.Value.Contains('\r') ||
                property.Value.Contains('\0') || property.Value.Length > MaximumPropertyValueLength)
                throw new InvalidDataException("Property snapshot values are incomplete, reordered, malformed, or out of bounds.");
            values.Add(new PropertyValue(property.Name, property.Value));
        }
        return new Snapshot(
            new ApkPropertyFileIdentity(reportedPath, Sha256(bytes), bytes.LongLength),
            new ReadOnlyCollection<PropertyValue>(values));
    }

    private static void ValidateSnapshotShape(byte[] bytes)
    {
        using var document = ParseStrictJson(bytes, "property snapshot", maximumDepth: 8);
        var root = document.RootElement;
        RequireObjectProperties(
            root,
            ["schema", "observed_at", "serial", "apk", "manifest", "properties"],
            "property snapshot");
        if (root.GetProperty("apk").ValueKind != JsonValueKind.Object ||
            root.GetProperty("manifest").ValueKind != JsonValueKind.Object ||
            root.GetProperty("properties").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Property snapshot nested records are malformed.");
        RequireObjectProperties(
            root.GetProperty("apk"),
            ["package_name", "version_code", "version_name", "signer_sha256", "sha256", "size_bytes"],
            "property snapshot APK identity");
        RequireObjectProperties(
            root.GetProperty("manifest"),
            ["schema", "owner_package", "scope", "sha256", "size_bytes", "property_count"],
            "property snapshot manifest identity");
        foreach (var property in root.GetProperty("properties").EnumerateArray())
            RequireObjectProperties(property, ["name", "value"], "property snapshot value");
    }

    private static bool PropertyValuesEqual(
        IReadOnlyList<PropertyValue> left,
        IReadOnlyList<PropertyValue> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            string.Equals(pair.First.Value, pair.Second.Value, StringComparison.Ordinal));

    private static string RequireExistingFile(string path, string extension, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"The {description} input was not found.", full);
        if (!string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The {description} input must be a {extension} file.", nameof(path));
        return full;
    }

    private static string RequireCreateNewOutput(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The property snapshot output must be a .json file.", nameof(path));
        var parent = Path.GetDirectoryName(full) ??
            throw new ArgumentException("The property snapshot output has no parent directory.", nameof(path));
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The property snapshot parent directory does not exist.");
        for (var current = new DirectoryInfo(parent); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "The property snapshot output ancestry must not contain a reparse point.");
        }
        if (File.Exists(full) || Directory.Exists(full))
            throw new IOException("The property snapshot output already exists; overwrite is not allowed.");
        return full;
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        string description,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length is < 1 || info.Length > maximumBytes)
            throw new InvalidDataException($"The {description} byte length is outside its fixed bound.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != info.Length)
            throw new IOException($"The {description} changed while being read.");
        return bytes;
    }

    private static JsonDocument ParseStrictJson(byte[] bytes, string description, int maximumDepth)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maximumDepth
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {description} JSON is malformed.", exception);
        }
    }

    private static void RequireObjectProperties(
        JsonElement element,
        IReadOnlyList<string> expected,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The {description} must be an object.");
        var actual = element.EnumerateObject().Select(static property => property.Name).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException($"The {description} property set or order is not canonical.");
    }

    private static string RequireString(JsonElement root, string name, string? exact = null)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"JSON property {name} must be a string.");
        var result = value.GetString() ?? string.Empty;
        if (result.Length == 0 || (exact is not null && !string.Equals(result, exact, StringComparison.Ordinal)))
            throw new InvalidDataException($"JSON property {name} has an unsupported value.");
        return result;
    }

    private static IReadOnlyList<string> RequireOrderedUniqueStrings(
        JsonElement root,
        string name,
        int minimum,
        int maximum)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() < minimum || array.GetArrayLength() > maximum)
            throw new InvalidDataException($"JSON property {name} has an invalid array bound.");
        var values = array.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString())
                ? item.GetString()!
                : throw new InvalidDataException($"JSON property {name} contains a non-string value.")).ToArray();
        RequireOrdinallySortedUnique(values, name);
        return values;
    }

    private static void RequireOrdinallySortedUnique(IReadOnlyList<string> values, string description)
    {
        for (var index = 1; index < values.Count; index++)
        {
            if (StringComparer.Ordinal.Compare(values[index - 1], values[index]) >= 0)
                throw new InvalidDataException($"The {description} must be ordinally sorted and unique.");
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record PropertyManifest(
        ApkPropertyManifestIdentity Identity,
        IReadOnlyList<string> Properties);

    private sealed record PropertyValue(string Name, string Value)
    {
        public bool IsSet => Value.Length > 0;
    }

    private sealed record Snapshot(
        ApkPropertyFileIdentity File,
        IReadOnlyList<PropertyValue> Values);

    private sealed record SnapshotMaterial(
        string Schema,
        DateTimeOffset ObservedAt,
        string Serial,
        SnapshotApk Apk,
        SnapshotManifest Manifest,
        IReadOnlyList<SnapshotProperty> Properties);

    private sealed record SnapshotApk(
        string PackageName,
        long VersionCode,
        string? VersionName,
        string SignerSha256,
        string Sha256,
        long SizeBytes);

    private sealed record SnapshotManifest(
        string Schema,
        string OwnerPackage,
        string Scope,
        string Sha256,
        long SizeBytes,
        int PropertyCount);

    private sealed record SnapshotProperty(string Name, string Value);
}
