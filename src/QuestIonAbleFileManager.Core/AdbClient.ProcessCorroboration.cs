using System.Globalization;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    // These reads corroborate presence only. A no-match, denied /proc read or
    // changing identity never proves absence and never authorizes an effect.
    private async Task<RuntimeProcessCorroboration> CorroboratePackageProcessAsync(
        string serial, string packageName, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(InspectionTimeout);
        var probeToken = deadline.Token;
        try
        {
            async Task<string> Read(params string[] arguments)
            {
                var result = await RunForDeviceAsync(serial, arguments, InspectionTimeout,
                    probeToken).ConfigureAwait(false);
                result.EnsureSuccess("Read fixed package process corroboration");
                if (!string.IsNullOrWhiteSpace(result.StandardError) || result.StandardOutput.Length > 262144)
                    throw new InvalidDataException("Process corroboration output was unusable.");
                return result.StandardOutput;
            }
            var memory = await Read("shell", "dumpsys", "meminfo", packageName);
            var pid = ReadExactMemoryProcess(memory, packageName);
            if (pid is null) return new(RuntimeProcessCorroborationState.Inconclusive);
            var uid = await ReadUniqueCurrentUserPackageUidAsync(serial, packageName, probeToken, 262144);
            var path = "/proc/" + pid.Value.ToString(CultureInfo.InvariantCulture);
            var before = ReadProcessBirth(await Read("shell", "cat", path + "/stat"), pid.Value);
            var status = await Read("shell", "cat", path + "/status");
            var matches = Regex.Matches(status, @"(?m)^Uid:\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$",
                RegexOptions.CultureInvariant);
            if (matches.Count != 1 || Enumerable.Range(1, 4).Any(index =>
                    !int.TryParse(matches[0].Groups[index].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var observed) || observed != uid))
                return new(RuntimeProcessCorroborationState.Conflicting);
            var repeated = ReadExactMemoryProcess(await Read("shell", "dumpsys", "meminfo", packageName), packageName);
            var after = ReadProcessBirth(await Read("shell", "cat", path + "/stat"), pid.Value);
            var uidAfter = await ReadUniqueCurrentUserPackageUidAsync(serial, packageName, probeToken, 262144);
            if (repeated != pid || after != before || uidAfter != uid)
                return new(RuntimeProcessCorroborationState.Conflicting);
            return new(RuntimeProcessCorroborationState.VerifiedPresent, pid, before, uid);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(RuntimeProcessCorroborationState.Unavailable); }
        catch (Exception error) when (error is InvalidDataException or AdbCommandException or IOException or TimeoutException)
        {
            return new(RuntimeProcessCorroborationState.Unavailable);
        }
    }

    private static int? ReadExactMemoryProcess(string value, string packageName)
    {
        var headers = value.ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.StartsWith("** MEMINFO", StringComparison.Ordinal)).ToArray();
        if (headers.Length == 0) return null;
        if (headers.Length != 1) throw new InvalidDataException("Ambiguous memory process identity.");
        var match = Regex.Match(headers[0], @"^\*\* MEMINFO in pid (?<pid>[0-9]+) \[" +
            Regex.Escape(packageName) + @"\] \*\*\s*$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            throw new InvalidDataException("Memory process identity was not the exact package.");
        return pid;
    }

    private static ulong ReadProcessBirth(string value, int pid)
    {
        var close = value.LastIndexOf(')');
        if (!value.StartsWith(pid.ToString(CultureInfo.InvariantCulture) + " (", StringComparison.Ordinal) || close < 0)
            throw new InvalidDataException("Process stat PID was not exact.");
        var fields = value[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], NumberStyles.None,
                CultureInfo.InvariantCulture, out var ticks) || ticks == 0)
            throw new InvalidDataException("Process birth was unusable.");
        return ticks;
    }
}
