using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    private const int MaximumPermissionObservationSourceLines = 4096;
    private const int MaximumPermissionObservationLineCharacters = 1024;
    private const int MaximumPermissionObservationRecords = 128;
    private const int MaximumPermissionObservationSourceBytes =
        (MaximumPermissionObservationSourceLines *
         (MaximumPermissionObservationLineCharacters + 32)) + 1;
    private static readonly Regex PermissionNameRegex = new(
        "^[A-Za-z][A-Za-z0-9_.]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex PermissionGrantRegex = new(
        "^(?<name>[A-Za-z][A-Za-z0-9_.]+):\\s+granted=(?<granted>true|false)(?:,|$)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex AppOpRegex = new(
        "^(?<operation>[A-Za-z][A-Za-z0-9_.:-]*):\\s*(?<mode>[A-Za-z][A-Za-z0-9_-]*)(?:;|$)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// Observes bounded raw manifest, effective-grant, and app-op facts for
    /// one exact installed package. This method has no permission mutation or
    /// policy/admission behavior.
    /// </summary>
    public async Task<ApkPermissionObservation> ObservePackagePermissionsAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        packageName = AndroidInput.RequirePackageName(packageName);
        try
        {
            await InspectPackageAsync(serial, packageName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PackageNotInstalledException)
        {
            return PackageNotInstalledPermissionObservation(serial, packageName);
        }

        var packageDump = await ReadBoundedPermissionSourceAsync(
            serial,
            ["shell", "dumpsys", "package", packageName],
            cancellationToken).ConfigureAwait(false);
        var appOps = await ReadBoundedPermissionSourceAsync(
            serial,
            ["shell", "cmd", "appops", "get", "--uid", packageName],
            cancellationToken).ConfigureAwait(false);

        var manifest = ParseManifestDeclaredPermissions(packageDump);
        var grants = ParseEffectivePermissionGrants(packageDump);
        var parsedAppOps = ParseAppOps(appOps);
        return new ApkPermissionObservation(
            serial,
            packageName,
            ApkPermissionObservationState.Reported,
            manifest.State,
            manifest.Values,
            grants.State,
            grants.Values,
            parsedAppOps.State,
            parsedAppOps.Values,
            PermissionObservationProvider())
        {
            PackageSourceExitCode = packageDump.Result.Succeeded
                ? null
                : packageDump.Result.ExitCode,
            AppOpSourceExitCode = appOps.Result.Succeeded
                ? null
                : appOps.Result.ExitCode
        };
    }

    private async Task<BoundedPermissionSource> ReadBoundedPermissionSourceAsync(
        string serial,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (_runner is not IStreamingCommandRunner streamingRunner)
        {
            throw new InvalidOperationException(
                "The configured command runner does not support bounded permission observation.");
        }

        var scoped = new List<string> { "-s", AndroidInput.RequireSerial(serial) };
        scoped.AddRange(arguments);
        using var source = new MemoryStream();
        try
        {
            var streamed = await streamingRunner.RunToStreamAsync(
                AdbPath,
                scoped,
                source,
                MaximumPermissionObservationSourceBytes,
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            return new BoundedPermissionSource(
                streamed.CommandResult,
                DecodeBoundedPermissionSource(source),
                Truncated: false);
        }
        catch (FleetTransferLimitException)
        {
            return new BoundedPermissionSource(
                new CommandResult(AdbPath, scoped, 0, string.Empty, string.Empty, TimeSpan.Zero),
                string.Empty,
                Truncated: true);
        }
    }

    private static string DecodeBoundedPermissionSource(MemoryStream source)
    {
        var buffer = source.GetBuffer();
        try
        {
            return System.Text.Encoding.UTF8.GetString(
                buffer, 0, checked((int)source.Length));
        }
        finally
        {
            Array.Clear(buffer, 0, checked((int)source.Length));
        }
    }

    private static PermissionObservationFact<ApkManifestDeclaredPermission>
        ParseManifestDeclaredPermissions(BoundedPermissionSource source)
    {
        if (!source.Result.Succeeded)
            return PermissionObservationFact<ApkManifestDeclaredPermission>.Unavailable;
        if (source.Truncated || !string.IsNullOrWhiteSpace(source.Result.StandardError))
            return PermissionObservationFact<ApkManifestDeclaredPermission>.Unknown;

        var values = new List<ApkManifestDeclaredPermission>();
        var section = ParsePackageDumpSection(
            source.Output,
            "requested permissions:",
            value =>
            {
                if (!PermissionNameRegex.IsMatch(value))
                    return false;
                values.Add(new ApkManifestDeclaredPermission(value));
                return true;
            });
        var reported = section.State == ApkPermissionObservationState.Reported;
        return new PermissionObservationFact<ApkManifestDeclaredPermission>(
            section.State,
            reported
                ? values.OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray()
                : []);
    }

    private static PermissionObservationFact<ApkEffectivePermissionGrant>
        ParseEffectivePermissionGrants(BoundedPermissionSource source)
    {
        if (!source.Result.Succeeded)
            return PermissionObservationFact<ApkEffectivePermissionGrant>.Unavailable;
        if (source.Truncated || !string.IsNullOrWhiteSpace(source.Result.StandardError))
            return PermissionObservationFact<ApkEffectivePermissionGrant>.Unknown;

        var values = new List<ApkEffectivePermissionGrant>();
        var install = ParsePackageDumpSection(
            source.Output,
            "install permissions:",
            value => AddPermissionGrant(value, "install", values));
        var runtime = ParsePackageDumpSection(
            source.Output,
            "runtime permissions:",
            value => AddPermissionGrant(value, "runtime", values));
        var state = CombinePermissionSections(install, runtime);
        return new PermissionObservationFact<ApkEffectivePermissionGrant>(
            state,
            state == ApkPermissionObservationState.Reported
                ? values.OrderBy(static value => value.Name, StringComparer.Ordinal)
                    .ThenBy(static value => value.Source, StringComparer.Ordinal)
                    .ToArray()
                : []);
    }

    private static bool AddPermissionGrant(
        string value,
        string source,
        ICollection<ApkEffectivePermissionGrant> grants)
    {
        var match = PermissionGrantRegex.Match(value);
        if (!match.Success)
            return false;
        grants.Add(new ApkEffectivePermissionGrant(
            match.Groups["name"].Value,
            string.Equals(match.Groups["granted"].Value, "true", StringComparison.Ordinal),
            source));
        return true;
    }

    private static PermissionObservationFact<ApkPermissionAppOp> ParseAppOps(
        BoundedPermissionSource source)
    {
        if (!source.Result.Succeeded)
            return PermissionObservationFact<ApkPermissionAppOp>.Unavailable;
        if (source.Truncated || !string.IsNullOrWhiteSpace(source.Result.StandardError))
            return PermissionObservationFact<ApkPermissionAppOp>.Unknown;
        if (source.Output.Length == 0)
            return new PermissionObservationFact<ApkPermissionAppOp>(
                ApkPermissionObservationState.Empty,
                []);

        var values = new List<ApkPermissionAppOp>();
        var malformed = false;
        var nonEmpty = false;
        using var reader = new StringReader(source.Output);
        for (var lineCount = 0; lineCount < MaximumPermissionObservationSourceLines; lineCount++)
        {
            var line = reader.ReadLine();
            if (line is null)
                break;
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith("Uid mode:", StringComparison.Ordinal))
                continue;
            nonEmpty = true;
            if (string.Equals(value, "No operations.", StringComparison.Ordinal))
                continue;
            var match = AppOpRegex.Match(value);
            if (!match.Success || values.Count >= MaximumPermissionObservationRecords)
            {
                malformed = true;
                continue;
            }
            values.Add(new ApkPermissionAppOp(
                match.Groups["operation"].Value,
                match.Groups["mode"].Value));
        }
        if (reader.ReadLine() is not null)
            return new PermissionObservationFact<ApkPermissionAppOp>(ApkPermissionObservationState.Unknown, []);
        var state = malformed
            ? ApkPermissionObservationState.Malformed
            : values.Count > 0
                ? ApkPermissionObservationState.Reported
                : nonEmpty
                    ? ApkPermissionObservationState.Absent
                    : ApkPermissionObservationState.Empty;
        return new PermissionObservationFact<ApkPermissionAppOp>(
            state,
            state == ApkPermissionObservationState.Reported
                ? values.OrderBy(static value => value.Operation, StringComparer.Ordinal).ToArray()
                : []);
    }

    private static ParsedPermissionSection ParsePackageDumpSection(
        string output,
        string header,
        Func<string, bool> tryAdd)
    {
        var found = false;
        var inSection = false;
        var headerIndent = 0;
        var records = 0;
        var malformed = false;
        using var reader = new StringReader(output);
        for (var lineCount = 0; lineCount < MaximumPermissionObservationSourceLines; lineCount++)
        {
            var line = reader.ReadLine();
            if (line is null)
                break;
            var trimmed = line.Trim();
            var indent = line.Length - line.TrimStart().Length;
            if (!inSection)
            {
                if (string.Equals(trimmed, header, StringComparison.Ordinal))
                {
                    found = true;
                    inSection = true;
                    headerIndent = indent;
                }
                continue;
            }
            if (trimmed.Length == 0)
                continue;
            if (indent <= headerIndent && trimmed.EndsWith(':'))
            {
                inSection = false;
                continue;
            }
            records++;
            if (records > MaximumPermissionObservationRecords ||
                trimmed.Length > MaximumPermissionObservationLineCharacters ||
                !tryAdd(trimmed))
            {
                malformed = true;
            }
        }
        if (reader.ReadLine() is not null)
            return new ParsedPermissionSection(ApkPermissionObservationState.Unknown);
        return new ParsedPermissionSection(
            !found
                ? ApkPermissionObservationState.Absent
                : malformed
                    ? ApkPermissionObservationState.Malformed
                    : records == 0
                        ? ApkPermissionObservationState.Empty
                        : ApkPermissionObservationState.Reported);
    }

    private static ApkPermissionObservationState CombinePermissionSections(
        ParsedPermissionSection install,
        ParsedPermissionSection runtime)
    {
        var states = new[] { install.State, runtime.State };
        if (states.Contains(ApkPermissionObservationState.Unknown))
            return ApkPermissionObservationState.Unknown;
        if (states.Contains(ApkPermissionObservationState.Malformed))
            return ApkPermissionObservationState.Malformed;
        if (states.Contains(ApkPermissionObservationState.Reported))
            return ApkPermissionObservationState.Reported;
        if (states.All(static state => state == ApkPermissionObservationState.Absent))
            return ApkPermissionObservationState.Absent;
        return ApkPermissionObservationState.Empty;
    }

    private static ApkPermissionObservation PackageNotInstalledPermissionObservation(
        string serial,
        string packageName) =>
        new(
            serial,
            packageName,
            ApkPermissionObservationState.PackageNotInstalled,
            ApkPermissionObservationState.PackageNotInstalled,
            [],
            ApkPermissionObservationState.PackageNotInstalled,
            [],
            ApkPermissionObservationState.PackageNotInstalled,
            [],
            PermissionObservationProvider());

    private static ApkPermissionObservationProvider PermissionObservationProvider() =>
        new(
            "questionable-file-manager",
            ProviderCapabilityDiscoveryContract.ProviderVersion,
            "https://github.com/MesmerPrism/QuestIonAble-File-Manager",
            "windows-portable-cli");

    private sealed record BoundedPermissionSource(
        CommandResult Result,
        string Output,
        bool Truncated);

    private sealed record ParsedPermissionSection(ApkPermissionObservationState State);

    private sealed record PermissionObservationFact<T>(
        ApkPermissionObservationState State,
        IReadOnlyList<T> Values)
    {
        public static PermissionObservationFact<T> Unavailable { get; } =
            new(ApkPermissionObservationState.Unavailable, []);
    }
}
