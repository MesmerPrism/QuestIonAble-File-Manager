using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed record ActorProcessRow(uint Uid, int Pid, int ParentPid, string Name, string Arguments);
public sealed record ActorRawSection(string Name, int ExitCode, int Bytes, string Sha256, string Base64);
public sealed record ActorCaptureEvidence(int ObservedBytes, bool EvidenceTruncated, string Sha256, string Base64,
    StreamingCommandResult? ReturnedCapture);
public sealed record DeviceActorObservation(
    string RequestedSerial, int RequestedPid, string CanonicalSerial, string BootId,
    IReadOnlyList<ActorProcessRow> Processes, ulong ProcessStartTicks, IReadOnlyList<uint> ProcessUids,
    IReadOnlyList<ActorRawSection> RawSections, StreamingCommandResult Capture,
    DateTimeOffset ObservedAtUtc)
{
    public string ObservationScope => "sequential fixed identity reads; not an atomic process snapshot or authority grant";
}

public sealed partial class AdbClient
{
    internal const int ActorObservationMaximumBytes = 8 * 1024 * 1024 + 70 * 1024;
    private static readonly UTF8Encoding ActorUtf8 = new(false, true);
    private static readonly string[] ActorSectionNames = ["serial", "boot", "inventory", "stat", "status"];
    private static readonly int[] ActorSectionLimits = [256, 128, 8 * 1024 * 1024, 4096, 65536];

    /// <summary>Closed read-only transport facts. Lease, expected actor and protected UID policy remain with the caller.</summary>
    public async Task<DeviceActorObservation> ObserveDeviceActorAsync(
        string serial, int pid, CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        if (_runner is not IStreamingCommandRunner runner)
            throw new InvalidOperationException("Bounded actor observation requires a streaming command runner.");
        using var output = new MemoryStream();
        var arguments = new[] { "-s", serial, "exec-out", "sh", "-c", BuildActorObservationScript(pid) };
        StreamingCommandResult? capture = null;
        try
        {
            capture = await runner.RunToStreamAsync(
                AdbPath, arguments, output, ActorObservationMaximumBytes, InspectionTimeout, cancellationToken)
                .ConfigureAwait(false);
            capture.CommandResult.EnsureSuccess("Observe fixed device actor identity");
            var raw = output.ToArray();
            if (raw.Length > ActorObservationMaximumBytes || capture.BytesWritten != raw.Length ||
                capture.Sha256 != Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() ||
                capture.CommandResult.StandardError.Length != 0)
                throw new InvalidDataException("Actor observation stream was incomplete or inconsistent.");
            return ParseActorObservation(serial, pid, raw, capture);
        }
        catch (Exception error)
        {
            // Preserve original nonzero/cancellation/limit/parser exception type.
            // Partial bytes and returned native metadata are evidence, never facts.
            var raw = output.ToArray(); var retained = raw.AsSpan(0, Math.Min(raw.Length, ActorObservationMaximumBytes));
            error.Data[typeof(ActorCaptureEvidence)] = new ActorCaptureEvidence(raw.Length, retained.Length != raw.Length,
                Convert.ToHexString(SHA256.HashData(retained)).ToLowerInvariant(), Convert.ToBase64String(retained), capture);
            throw;
        }
    }

    internal static string BuildActorObservationScript(int pid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        var number = pid.ToString(CultureInfo.InvariantCulture);
        // The sentinel preserves trailing newlines inside command substitution.
        // No caller shell/path text enters this fixed program; only positive decimal PID.
        return "LC_ALL=C; export LC_ALL; " +
            "emit() { name=$1; shift; value=$(\"$@\"; rc=$?; printf '\\001'; exit \"$rc\"); rc=$?; " +
            "value=${value%?}; [ \"$rc\" -eq 0 ] || { printf '%s:%s:0\\n\\n' \"$name\" \"$rc\"; exit \"$rc\"; }; " +
            "length=$(printf '%s' \"$value\" | wc -c) || exit 71; " +
            "printf '%s:0:%s\\n' \"$name\" \"$length\" || exit 72; " +
            "printf '%s\\n' \"$value\" || exit 73; }; " +
            "printf 'QFM_ACTOR_V1\\n' || exit 74; " +
            "emit serial getprop ro.serialno || exit $?; " +
            "emit boot cat /proc/sys/kernel/random/boot_id || exit $?; " +
            "emit inventory ps -A -o UID,PID,PPID,NAME,ARGS || exit $?; " +
            $"emit stat cat /proc/{number}/stat || exit $?; " +
            $"emit status cat /proc/{number}/status || exit $?; " +
            "printf 'END\\n' || exit 75";
    }

    internal static DeviceActorObservation ParseActorObservation(
        string serial, int pid, byte[] raw, StreamingCommandResult capture)
    {
        var cursor = 0;
        string Line()
        {
            var end = Array.IndexOf(raw, (byte)'\n', cursor);
            if (end < cursor || end - cursor > 100) throw new InvalidDataException("Missing actor frame header.");
            var line = Encoding.ASCII.GetString(raw, cursor, end - cursor); cursor = end + 1; return line;
        }
        if (Line() != "QFM_ACTOR_V1") throw new InvalidDataException("Actor frame version mismatch.");
        var sections = new List<ActorRawSection>(); var texts = new List<string>();
        for (var i = 0; i < ActorSectionNames.Length; i++)
        {
            var fields = Line().Split(':');
            if (fields.Length != 3 || fields[0] != ActorSectionNames[i] || fields[1] != "0" ||
                !int.TryParse(fields[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ||
                bytes <= 0 || bytes > ActorSectionLimits[i] || bytes > raw.Length - cursor)
                throw new InvalidDataException("Actor frame section failed or exceeded its bound.");
            var data = raw.AsSpan(cursor, bytes).ToArray(); cursor += bytes;
            if (cursor >= raw.Length || raw[cursor++] != '\n') throw new InvalidDataException("Actor frame section truncated.");
            string text;
            try { text = ActorUtf8.GetString(data); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Actor section is not UTF-8.", error); }
            if (text.Contains('\0')) throw new InvalidDataException("Actor section contains NUL.");
            texts.Add(i < 2 ? text.Trim() : text);
            sections.Add(new(fields[0], 0, bytes, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), Convert.ToBase64String(data)));
        }
        if (Line() != "END" || cursor != raw.Length) throw new InvalidDataException("Actor frame has trailing or missing data.");
        if (texts[0].Length == 0 || texts[0].Any(char.IsWhiteSpace) || texts[0].Any(char.IsControl) ||
            !Regex.IsMatch(texts[1], "^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Device canonical or boot identity malformed.");
        var rows = ParseActorInventory(texts[2]);
        var stat = texts[3]; var close = stat.LastIndexOf(')'); var firstSpace = stat.IndexOf(' ');
        if (firstSpace < 1 || stat[..firstSpace] != pid.ToString(CultureInfo.InvariantCulture) || close < firstSpace)
            throw new InvalidDataException("Actor proc stat PID mismatch.");
        var statFields = stat[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (statFields.Length < 20 || !ulong.TryParse(statFields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var birth))
            throw new InvalidDataException("Actor process birth missing.");
        var uidLines = Regex.Matches(texts[4], @"^Uid:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (uidLines.Count != 1) throw new InvalidDataException("Actor proc UID vector missing or ambiguous.");
        var uids = new List<uint>();
        foreach (var group in uidLines[0].Groups.Cast<Group>().Skip(1))
        {
            if (!uint.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
                throw new InvalidDataException("Actor proc UID outside numeric domain.");
            uids.Add(uid);
        }
        return new(serial, pid, texts[0], texts[1], rows, birth, uids.AsReadOnly(), sections.AsReadOnly(), capture, DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<ActorProcessRow> ParseActorInventory(string text)
    {
        var lines = text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length < 2 || !Regex.IsMatch(lines[0], @"^\s*UID\s+PID\s+PPID\s+NAME\s+ARGS\s*$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Complete numeric process inventory header required.");
        var rows = new List<ActorProcessRow>(); var pids = new HashSet<int>();
        foreach (var line in lines.Skip(1))
        {
            var match = Regex.Match(line, @"^\s*(\d+)\s+(\d+)\s+(\d+)\s+(\S+)(?:\s+(.*))?$", RegexOptions.CultureInvariant);
            if (!match.Success || !uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ||
                !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var process) || process <= 0 ||
                !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parent) || parent < 0 || !pids.Add(process))
                throw new InvalidDataException("Full numeric process inventory malformed or duplicated.");
            rows.Add(new(uid, process, parent, match.Groups[4].Value, match.Groups[5].Value));
        }
        return rows.AsReadOnly();
    }
}
