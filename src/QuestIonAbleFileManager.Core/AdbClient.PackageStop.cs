using System.Globalization;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    /// <summary>
    /// Sends the one fixed current-user package-stop command and then proves
    /// only package/process/activity quiescence for that same package.
    /// </summary>
    public async Task<PackageStopResult> StopPackageAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        packageName = AndroidInput.RequirePackageName(packageName);

        // A package absence is a pre-dispatch rejection, not an idempotent
        // success: the owner must never claim a stop result without a package
        // check on the exact selected serial.
        await EnsurePackagePresentForStopAsync(serial, packageName, cancellationToken).ConfigureAwait(false);

        CommandResult stop;
        try
        {
            stop = await RunForDeviceAsync(
                serial,
                ["shell", "am", "force-stop", "--user", "current", packageName],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            stop.EnsureSuccess("Force-stop exact package for current user");
        }
        catch (Exception exception)
        {
            // Invocation began, so Android state may have changed even when
            // the host lost the command result or cancellation arrived.
            throw new PackageStopDispatchException(exception);
        }

        try
        {
            await EnsurePackagePresentForStopAsync(serial, packageName, cancellationToken).ConfigureAwait(false);
            var activities = await RunForDeviceAsync(
                serial,
                ["shell", "dumpsys", "activity", "activities"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            activities.EnsureSuccess("Read package-stop activity state");
            RequireSilentStandardError(activities, "Read package-stop activity state");

            var processes = await RunForDeviceAsync(
                serial,
                ["shell", "pidof", packageName],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            var quiescence = ReadPackageStopQuiescence(
                packageName,
                activities.StandardOutput,
                ReadPackageProcessIds(processes));

            return new PackageStopResult(
                serial,
                packageName,
                PackagePresentBeforeDispatch: true,
                PackagePresentAfterDispatch: true,
                stop,
                quiescence);
        }
        catch (Exception exception)
        {
            throw new PackageStopReadbackException(exception);
        }
    }

    private static PackageStopQuiescence ReadPackageStopQuiescence(
        string packageName,
        string activitiesOutput,
        IReadOnlyList<int> processIds)
    {
        var lines = activitiesOutput.ReplaceLineEndings("\n").Split('\n');
        var packageToken = packageName + "/";
        var targetComponent = new Regex(
            Regex.Escape(packageName) +
            @"/(?<activity>\.[A-Za-z0-9_.$]+|[A-Za-z][A-Za-z0-9_.$]*(?:\.[A-Za-z0-9_.$]+)*)",
            RegexOptions.CultureInvariant);
        var foreground = new SortedSet<string>(StringComparer.Ordinal);
        var topResumed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var isForeground = line.Contains("mResumedActivity", StringComparison.Ordinal);
            var isTopResumed = line.Contains("topResumedActivity", StringComparison.OrdinalIgnoreCase);
            if ((!isForeground && !isTopResumed) ||
                !line.Contains(packageToken, StringComparison.Ordinal))
            {
                continue;
            }

            var matches = targetComponent.Matches(line);
            if (matches.Count == 0)
            {
                throw new InvalidDataException(
                    "Package-stop activity readback contained an unparseable exact-package component.");
            }
            foreach (Match match in matches)
            {
                var activity = match.Groups["activity"].Value;
                var canonical = packageName + "/" +
                    (activity.StartsWith(".", StringComparison.Ordinal)
                        ? packageName + activity
                        : activity);
                if (isForeground)
                {
                    foreground.Add(canonical);
                }
                if (isTopResumed)
                {
                    topResumed.Add(canonical);
                }
            }
        }

        return new PackageStopQuiescence(
            processIds,
            foreground.ToArray(),
            topResumed.ToArray());
    }

    private async Task EnsurePackagePresentForStopAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken)
    {
        var result = await RunForDeviceAsync(
            serial,
            ["shell", $"pm path --user current {AndroidInput.ShellQuote(packageName)}"],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (await IsConfirmedSilentCurrentUserPackageAbsenceForStopAsync(
                serial,
                packageName,
                result,
                cancellationToken).ConfigureAwait(false))
        {
            throw new PackageNotInstalledException(serial, packageName);
        }
        result.EnsureSuccess($"Inspect package {packageName} for exact-package stop");
        RequireSilentStandardError(result, "Inspect package for exact-package stop");

        var lines = result.StandardOutput.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 ||
            lines.Any(static line =>
                !line.StartsWith("package:/", StringComparison.Ordinal) ||
                !line["package:".Length..].EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) ||
            AdbOutputParser.ParsePackagePaths(result.StandardOutput).Count == 0)
        {
            throw new InvalidDataException(
                "Package-stop package check did not return a valid installed APK path.");
        }
    }

    // This route dispatches only to the current Android user. Keep its
    // absence proof on that same user; the legacy unscoped deployment check
    // intentionally retains its established cross-user compatibility.
    private async Task<bool> IsConfirmedSilentCurrentUserPackageAbsenceForStopAsync(
        string serial,
        string packageName,
        CommandResult packagePath,
        CancellationToken cancellationToken)
    {
        if (packagePath.ExitCode != 1 ||
            !string.IsNullOrWhiteSpace(packagePath.StandardOutput) ||
            !string.IsNullOrWhiteSpace(packagePath.StandardError))
        {
            return false;
        }

        var packageList = await RunForDeviceAsync(
            serial,
            ["shell", $"pm list packages --user current {AndroidInput.ShellQuote(packageName)}"],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        packageList.EnsureSuccess($"Confirm current-user package {packageName} absence");
        return string.IsNullOrWhiteSpace(packageList.StandardOutput) &&
            string.IsNullOrWhiteSpace(packageList.StandardError);
    }

    private static IReadOnlyList<int> ReadPackageProcessIds(CommandResult result)
    {
        if (result.Succeeded)
        {
            RequireSilentStandardError(result, "Read package-stop process state");
            var values = result.StandardOutput.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 0)
            {
                throw new InvalidDataException(
                    "Package-stop process readback succeeded without process identifiers.");
            }

            var processIds = new SortedSet<int>();
            foreach (var value in values)
            {
                if (!int.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var processId) || processId <= 0)
                {
                    throw new InvalidDataException(
                        "Package-stop process readback contained an invalid process identifier.");
                }
                processIds.Add(processId);
            }
            return processIds.ToArray();
        }

        if (result.ExitCode == 1 &&
            string.IsNullOrWhiteSpace(result.StandardOutput) &&
            string.IsNullOrWhiteSpace(result.StandardError))
        {
            return [];
        }

        result.EnsureSuccess("Read package-stop process state");
        throw new InvalidDataException("Package-stop process readback was not recognized.");
    }

    private static void RequireSilentStandardError(CommandResult result, string operation)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new InvalidDataException($"{operation} returned unexpected standard-error output.");
        }
    }
}
