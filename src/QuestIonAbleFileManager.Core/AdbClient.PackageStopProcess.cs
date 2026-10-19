using System.Globalization;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    // Fixed read-only framing makes missing tails and nonzero subcommand exits
    // unusable. No package or caller-supplied command text enters this script.
    internal const string StopProcessInventoryCommand =
        "printf 'QFM_STOP_UID_BEGIN\\n'; pm list packages --user current -U; " +
        "r=$?; printf 'QFM_STOP_UID_END=%s\\n' \"$r\"; " +
        "printf 'QFM_STOP_PS_BEGIN\\n'; ps -A -o UID,PID,ARGS; " +
        "r=$?; printf 'QFM_STOP_PS_END=%s\\n' \"$r\"; exit \"$r\"";

    private sealed record StopSnapshot(int User, int Uid,
        IReadOnlyDictionary<int, int> Processes,
        IReadOnlyList<PackageStopProcessIdentity> Births);

    private async Task<string> ReadStopFactAsync(string serial, CancellationToken token, params string[] arguments)
    {
        var result = await RunForDeviceAsync(serial, arguments, InspectionTimeout, token).ConfigureAwait(false);
        result.EnsureSuccess("Read fixed package-stop process evidence");
        if (!string.IsNullOrWhiteSpace(result.StandardError) || result.StandardOutput.Length > 262144)
            throw new InvalidDataException("Package-stop evidence was denied or oversized.");
        return result.StandardOutput;
    }

    private async Task<StopSnapshot> ReadStopSnapshotAsync(string serial, string package, bool readBirths, CancellationToken token)
    {
        var userText = await ReadStopFactAsync(serial, token, "shell", "am", "get-current-user").ConfigureAwait(false);
        if (!int.TryParse(userText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var user) || user < 0)
            throw new InvalidDataException("Current Android user was unusable.");
        var text = await ReadStopFactAsync(serial, token, "shell", StopProcessInventoryCommand).ConfigureAwait(false);
        var lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var uidEnd = Array.IndexOf(lines, "QFM_STOP_UID_END=0");
        if (lines.Length < 6 || lines[0] != "QFM_STOP_UID_BEGIN" || uidEnd < 2 || uidEnd > lines.Length - 4 ||
            lines[uidEnd + 1] != "QFM_STOP_PS_BEGIN" || lines[^1] != "QFM_STOP_PS_END=0")
            throw new InvalidDataException("Package-stop inventory was incomplete.");
        var packages = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines[1..uidEnd])
        {
            var match = Regex.Match(line, @"^package:(?<p>[A-Za-z0-9._]+)\s+uid:(?<u>[0-9]+)$", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups["u"].Value, out var uid) ||
                uid < 0 || !packages.TryAdd(match.Groups["p"].Value, uid))
                throw new InvalidDataException("Package-stop UID inventory was malformed.");
        }
        if (!packages.TryGetValue(package, out var targetUid) || targetUid < 10000 || targetUid % 100000 < 10000 ||
            targetUid / 100000 != user || packages.Count(row => row.Value == targetUid) != 1)
            throw new InvalidDataException("Package-stop UID was absent, shared or user-mismatched.");
        if (!Regex.IsMatch(lines[uidEnd + 2], @"^\s*UID\s+PID\s+ARGS\s*$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Package-stop process inventory header was malformed.");
        var processes = new Dictionary<int, int>();
        foreach (var line in lines[(uidEnd + 3)..^1])
        {
            var match = Regex.Match(line, @"^\s*(?<u>[0-9]+)\s+(?<p>[0-9]+)\s+(?<a>\S.*)$", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups["u"].Value, out var uid) ||
                !int.TryParse(match.Groups["p"].Value, out var pid) || pid <= 0 || !processes.TryAdd(pid, uid))
                throw new InvalidDataException("Package-stop process inventory row was malformed.");
            var name = match.Groups["a"].Value.Split((char[]?)null, 2)[0];
            if ((name == package || name.StartsWith(package + ":", StringComparison.Ordinal)) && uid != targetUid)
                throw new InvalidDataException("Package process used a conflicting or isolated UID.");
        }
        if (processes.Count == 0)
            throw new InvalidDataException("Whole-device process inventory was empty.");
        var births = new List<PackageStopProcessIdentity>();
        if (readBirths)
            foreach (var pid in processes.Where(row => row.Value == targetUid).Select(row => row.Key).Order())
                births.Add(new(pid, await ReadStopBirthAsync(serial, pid, targetUid, token).ConfigureAwait(false)));
        var userAfter = await ReadStopFactAsync(serial, token, "shell", "am", "get-current-user").ConfigureAwait(false);
        if (userAfter.Trim() != user.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("Current user changed during process observation.");
        return new(user, targetUid, processes, births);
    }

    private async Task<ulong> ReadStopBirthAsync(string serial, int pid, int uid, CancellationToken token)
    {
        var path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture);
        var before = ReadProcessBirth(await ReadStopFactAsync(serial, token, "shell", "cat", path + "/stat").ConfigureAwait(false), pid);
        var status = await ReadStopFactAsync(serial, token, "shell", "cat", path + "/status").ConfigureAwait(false);
        var matches = Regex.Matches(status, @"(?m)^Uid:\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$", RegexOptions.CultureInvariant);
        if (matches.Count != 1 || Enumerable.Range(1, 4).Any(i =>
                !int.TryParse(matches[0].Groups[i].Value, out var observed) || observed != uid))
            throw new InvalidDataException("Known process UID was not exact.");
        var after = ReadProcessBirth(await ReadStopFactAsync(serial, token, "shell", "cat", path + "/stat").ConfigureAwait(false), pid);
        if (before != after) throw new InvalidDataException("Known process birth changed during observation.");
        return before;
    }

    private async Task<StopSnapshot?> TryReadStopSnapshotAsync(string serial, string package, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(InspectionTimeout);
        try { return await ReadStopSnapshotAsync(serial, package, true, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception e) when (e is InvalidDataException or AdbCommandException or IOException or TimeoutException) { return null; }
    }

    private async Task<(bool? Verified, PackageStopProcessEvidence? Evidence)> TryProveStopProcessAbsenceAsync(
        string serial, string package, StopSnapshot? before, CancellationToken token)
    {
        if (before is null) return (null, null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(InspectionTimeout);
        try
        {
            for (var phase = 0; phase < 2; phase++)
            {
                var after = await ReadStopSnapshotAsync(serial, package, false, deadline.Token).ConfigureAwait(false);
                if (after.User != before.User || after.Uid != before.Uid)
                    return (null, null);
                if (after.Processes.Values.Contains(before.Uid))
                    return (false, new(before.User, before.Uid, before.Births, false, phase + 1));
                foreach (var birth in before.Births)
                    if (after.Processes.TryGetValue(birth.ProcessId, out var replacementUid) &&
                        await ReadStopBirthAsync(serial, birth.ProcessId, replacementUid, deadline.Token).ConfigureAwait(false) == birth.StartTicks)
                        return (null, null);
            }
            return (true, new(before.User, before.Uid, before.Births, true, 2));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (null, null); }
        catch (Exception e) when (e is InvalidDataException or AdbCommandException or IOException or TimeoutException) { return (null, null); }
    }
}
