using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed record ActorPackageUid(string PackageName, uint Uid);
public sealed record DeviceActorIdentityObservation(
    string RequestedSerial, int RequestedPid, string CanonicalSerial, string BootId,
    IReadOnlyList<ActorProcessRow> Processes, ulong InitialProcessStartTicks,
    ActorRawSection CommandLine, IReadOnlyList<ActorPackageUid> PackageUids,
    IReadOnlyList<uint> ProcessUids, ulong FinalProcessStartTicks,
    IReadOnlyList<ActorRawSection> RawSections, StreamingCommandResult Capture,
    DateTimeOffset ObservedAtUtc)
{
    public string ObservationScope => "sequential fixed identity reads; caller decides package, UID exclusivity and final birth equality; not an atomic snapshot or authority grant";
}

public sealed partial class AdbClient
{
    // Sum of eight payload bounds plus bounded framing overhead.
    internal const int ActorIdentityMaximumBytes = 2 * 8 * 1024 * 1024 + 2 * 65536 + 2 * 4096 + 256 + 128 + 1024;
    private static readonly string[] IdentitySectionNames = ["serial", "boot", "inventory", "initial_stat", "cmdline", "package_uids", "status", "final_stat"];
    private static readonly int[] IdentitySectionLimits = [256, 128, 8 * 1024 * 1024, 4096, 65536, 8 * 1024 * 1024, 65536, 4096];

    public async Task<DeviceActorIdentityObservation> ObserveDeviceActorIdentityAsync(
        string serial, int pid, CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        if (_runner is not IStreamingCommandRunner runner)
            throw new InvalidOperationException("Bounded actor identity requires a streaming command runner.");
        using var output = new MemoryStream();
        string[] arguments = ["-s", serial, "exec-out", "sh", "-c", BuildActorIdentityScript(pid)];
        StreamingCommandResult? capture = null;
        try
        {
            capture = await runner.RunToStreamAsync(AdbPath, arguments, output,
                ActorIdentityMaximumBytes, InspectionTimeout, cancellationToken).ConfigureAwait(false);
            capture.CommandResult.EnsureSuccess("Observe fixed device actor identity");
            var raw = output.ToArray();
            if (raw.Length > ActorIdentityMaximumBytes || capture.BytesWritten != raw.Length ||
                capture.Sha256 != Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() ||
                capture.CommandResult.StandardError.Length != 0)
                throw new InvalidDataException("Actor identity stream was incomplete or inconsistent.");
            return ParseActorIdentity(serial, pid, raw, capture);
        }
        catch (Exception error)
        {
            var raw = output.ToArray();
            var retained = raw.AsSpan(0, Math.Min(raw.Length, ActorIdentityMaximumBytes));
            error.Data[typeof(ActorCaptureEvidence)] = new ActorCaptureEvidence(raw.Length, retained.Length != raw.Length,
                Convert.ToHexString(SHA256.HashData(retained)).ToLowerInvariant(), Convert.ToBase64String(retained), capture);
            throw;
        }
    }

    internal static string BuildActorIdentityScript(int pid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        var number = pid.ToString(CultureInfo.InvariantCulture);
        return "LC_ALL=C; export LC_ALL; " +
            "emit() { name=$1; shift; value=$(\"$@\"; rc=$?; printf '\\001'; exit \"$rc\"); rc=$?; " +
            "value=${value%?}; [ \"$rc\" -eq 0 ] || { printf '%s:%s:0\\n\\n' \"$name\" \"$rc\"; exit \"$rc\"; }; " +
            "length=$(printf '%s' \"$value\" | wc -c) || exit 71; " +
            "printf '%s:0:%s\\n' \"$name\" \"$length\" || exit 72; printf '%s\\n' \"$value\" || exit 73; }; " +
            // One proc read, held as base64 because shell variables lose NUL.
            // Decode the same encoded bytes for both frame length and payload.
            "emit_cmdline() { encoded=$(base64 \"$1\"; rc=$?; printf '\\001'; exit \"$rc\"); rc=$?; encoded=${encoded%?}; " +
            "[ \"$rc\" -eq 0 ] || { printf 'cmdline:%s:0\\n\\n' \"$rc\"; exit \"$rc\"; }; " +
            "length=$(printf '%s' \"$encoded\" | base64 -d | wc -c) || exit 71; " +
            "printf 'cmdline:0:%s\\n' \"$length\" || exit 72; " +
            "printf '%s' \"$encoded\" | base64 -d || exit $?; printf '\\n' || exit 73; }; " +
            "printf 'QFM_ACTOR_IDENTITY_V1\\n' || exit 74; " +
            "emit serial getprop ro.serialno || exit $?; " +
            "emit boot cat /proc/sys/kernel/random/boot_id || exit $?; " +
            "emit inventory ps -A -o UID,PID,PPID,NAME,ARGS || exit $?; " +
            $"emit initial_stat cat /proc/{number}/stat || exit $?; " +
            $"emit_cmdline /proc/{number}/cmdline || exit $?; " +
            "emit package_uids pm list packages -U || exit $?; " +
            $"emit status cat /proc/{number}/status || exit $?; " +
            $"emit final_stat cat /proc/{number}/stat || exit $?; " +
            "printf 'END\\n' || exit 75";
    }

    internal static DeviceActorIdentityObservation ParseActorIdentity(
        string serial, int pid, byte[] raw, StreamingCommandResult capture)
    {
        var cursor = 0;
        string Line()
        {
            var end = Array.IndexOf(raw, (byte)'\n', cursor);
            if (end < cursor || end - cursor > 100) throw new InvalidDataException("Missing actor identity frame header.");
            var line = Encoding.ASCII.GetString(raw, cursor, end - cursor); cursor = end + 1; return line;
        }
        if (Line() != "QFM_ACTOR_IDENTITY_V1") throw new InvalidDataException("Actor identity frame version mismatch.");
        var sections = new List<ActorRawSection>(); var texts = new string[8];
        for (var i = 0; i < IdentitySectionNames.Length; i++)
        {
            var fields = Line().Split(':');
            if (fields.Length != 3 || fields[0] != IdentitySectionNames[i] || fields[1] != "0" ||
                !int.TryParse(fields[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
                size < (i == 4 ? 0 : 1) || size > IdentitySectionLimits[i] || size > raw.Length - cursor)
                throw new InvalidDataException("Actor identity section failed or exceeded its bound.");
            var data = raw.AsSpan(cursor, size).ToArray(); cursor += size;
            if (cursor >= raw.Length || raw[cursor++] != '\n') throw new InvalidDataException("Actor identity section truncated.");
            sections.Add(new(IdentitySectionNames[i], 0, size,
                Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), Convert.ToBase64String(data)));
            if (i == 4) continue; // Opaque binary cmdline, including NUL and non-UTF8.
            try { texts[i] = ActorUtf8.GetString(data); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Actor identity text is not UTF-8.", error); }
            if (texts[i].Contains('\0')) throw new InvalidDataException("Actor identity text contains NUL.");
        }
        if (Line() != "END" || cursor != raw.Length) throw new InvalidDataException("Actor identity frame has trailing or missing data.");
        var canonical = texts[0].Trim(); var boot = texts[1].Trim();
        if (canonical.Length == 0 || canonical.Any(char.IsWhiteSpace) || canonical.Any(char.IsControl) ||
            !Regex.IsMatch(boot, "^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Actor identity canonical or boot malformed.");
        var rows = ParseActorInventory(texts[2]);
        ulong Birth(string stat)
        {
            var close = stat.LastIndexOf(')'); var space = stat.IndexOf(' ');
            if (space < 1 || stat[..space] != pid.ToString(CultureInfo.InvariantCulture) || close < space)
                throw new InvalidDataException("Actor identity proc PID mismatch.");
            var fields = stat[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 20 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var birth))
                throw new InvalidDataException("Actor identity proc birth missing.");
            return birth;
        }
        var packages = new List<ActorPackageUid>(); var names = new HashSet<string>(StringComparer.Ordinal);
        var registry = texts[5].EndsWith('\n') ? texts[5][..^1] : texts[5];
        foreach (var line in registry.Split('\n'))
        {
            var match = Regex.Match(line, @"^package:([A-Za-z0-9_.]+) uid:(\d+)\r?$", RegexOptions.CultureInvariant);
            if (!match.Success || !names.Add(match.Groups[1].Value) ||
                !uint.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
                throw new InvalidDataException("Complete package UID registry malformed or duplicated.");
            packages.Add(new(match.Groups[1].Value, uid));
        }
        if (packages.Count == 0) throw new InvalidDataException("Complete package UID registry empty.");
        var matches = Regex.Matches(texts[6], @"^Uid:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (matches.Count != 1) throw new InvalidDataException("Actor identity UID vector missing or ambiguous.");
        var uids = new List<uint>();
        foreach (var group in matches[0].Groups.Cast<Group>().Skip(1))
        {
            if (!uint.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
                throw new InvalidDataException("Actor identity UID outside numeric domain.");
            uids.Add(uid);
        }
        return new(serial, pid, canonical, boot, rows, Birth(texts[3]), sections[4], packages.AsReadOnly(),
            uids.AsReadOnly(), Birth(texts[7]), sections.AsReadOnly(), capture, DateTimeOffset.UtcNow);
    }
}
