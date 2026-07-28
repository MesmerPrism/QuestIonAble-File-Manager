using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.Core;

internal sealed record LocalApiJournalEntry(
    string OperationId,
    string CommandName,
    string CommandDigest,
    DateTimeOffset ExpiresAt,
    string StagedPath,
    ApkArtifactInspection Artifact,
    string? Serial,
    LocalApiInstallOptions? InstallOptions,
    LocalApiOperationStage Stage,
    bool Consumed,
    bool Dispatched,
    DateTimeOffset? TerminalAt,
    JsonElement? ResultEvidence,
    OperatorMutationReceipt? MutationEvidence,
    string? ErrorCode,
    string? Error);

internal sealed class LocalApiJournal : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;
    private readonly string _anchorPath;
    private readonly byte[] _key;
    private readonly long _maximumBytes;
    private long _sequence;
    private string _lastEnvelopeHash = new('0', 64);
    private FileStream? _anchorHandle;
    private readonly Func<string, bool>? _fault;

    public LocalApiJournal(LocalApiStateSettings settings)
    {
        Directory.CreateDirectory(settings.StateDirectory);
        _path = Path.Combine(settings.StateDirectory, "operations.v1.json");
        _anchorPath = Path.Combine(settings.StateDirectory, "operations.v1.anchor");
        _key = settings.JournalIntegrityKey.ToArray();
        _maximumBytes = settings.Limits.MaximumJournalBytes;
        _fault = settings.JournalFault;
        CleanupBoundedTemps();
    }

    public IReadOnlyList<LocalApiJournalEntry> Load()
    {
        var journalExists = File.Exists(_path);
        var anchorExists = File.Exists(_anchorPath);
        if (!journalExists && !anchorExists) return [];
        if (journalExists != anchorExists) throw Damage();
        var bytes = ReadSecure(_path);
        if (bytes.LongLength > _maximumBytes) throw Damage();
        try
        {
            var envelope = JsonSerializer.Deserialize<JournalEnvelope>(bytes, Json) ?? throw Damage();
            var payload = Convert.FromBase64String(envelope.Payload);
            var supplied = Convert.FromHexString(envelope.Hmac);
            var envelopePayload = JsonSerializer.SerializeToUtf8Bytes(
                new { envelope.Sequence, envelope.PreviousEnvelopeHash, envelope.Payload }, Json);
            var expected = HMACSHA256.HashData(_key, envelopePayload);
            if (supplied.Length != expected.Length ||
                !CryptographicOperations.FixedTimeEquals(expected, supplied))
                throw Damage();
            var anchorBytes = ReadSecure(_anchorPath);
            var anchor = JsonSerializer.Deserialize<JournalAnchor>(anchorBytes, Json) ?? throw Damage();
            var anchorPayload = JsonSerializer.SerializeToUtf8Bytes(
                new { anchor.Sequence, anchor.EnvelopeHash }, Json);
            var anchorExpected = HMACSHA256.HashData(_key, anchorPayload);
            var anchorSupplied = Convert.FromHexString(anchor.Hmac);
            var envelopeHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (anchorSupplied.Length != anchorExpected.Length ||
                !CryptographicOperations.FixedTimeEquals(anchorExpected, anchorSupplied) ||
                anchor.Sequence != envelope.Sequence ||
                !string.Equals(anchor.EnvelopeHash, envelopeHash, StringComparison.Ordinal))
                throw Damage();
            _sequence = envelope.Sequence;
            _lastEnvelopeHash = envelopeHash;
            _anchorHandle = FleetWindowsFileSafety.OpenRetainedReadOnlyFile(_anchorPath);
            FleetWindowsFileSafety.ValidateFile(_anchorHandle.SafeFileHandle, _anchorPath, requireSingleLink: true);
            return JsonSerializer.Deserialize<LocalApiJournalEntry[]>(payload, Json) ?? [];
        }
        catch (Exception exception) when (exception is not LocalApiException)
        {
            throw Damage();
        }
    }

    public void Save(IReadOnlyList<LocalApiJournalEntry> entries)
    {
        Fault("before_save");
        var payload = JsonSerializer.SerializeToUtf8Bytes(entries, Json);
        var nextSequence = checked(_sequence + 1);
        var payloadBase64 = Convert.ToBase64String(payload);
        var envelopeMacInput = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                Sequence = nextSequence,
                PreviousEnvelopeHash = _lastEnvelopeHash,
                Payload = payloadBase64
            }, Json);
        var envelope = new JournalEnvelope(
            nextSequence,
            _lastEnvelopeHash,
            payloadBase64,
            Convert.ToHexString(HMACSHA256.HashData(_key, envelopeMacInput)).ToLowerInvariant());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        if (bytes.LongLength > _maximumBytes)
            throw new LocalApiException("journal_capacity", "The durable API journal capacity is exhausted.");
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var anchorTemp = _anchorPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteSecure(temp, bytes);
            File.Move(temp, _path, overwrite: true);
            _ = ReadSecure(_path);
            Fault("after_journal");
            var envelopeHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var anchorPayload = JsonSerializer.SerializeToUtf8Bytes(
                new { Sequence = nextSequence, EnvelopeHash = envelopeHash }, Json);
            var anchor = new JournalAnchor(
                nextSequence,
                envelopeHash,
                Convert.ToHexString(HMACSHA256.HashData(_key, anchorPayload)).ToLowerInvariant());
            WriteSecure(anchorTemp, JsonSerializer.SerializeToUtf8Bytes(anchor, Json));
            _anchorHandle?.Dispose();
            _anchorHandle = null;
            File.Move(anchorTemp, _anchorPath, overwrite: true);
            _ = ReadSecure(_anchorPath);
            _anchorHandle = FleetWindowsFileSafety.OpenRetainedReadOnlyFile(_anchorPath);
            FleetWindowsFileSafety.ValidateFile(_anchorHandle.SafeFileHandle, _anchorPath, requireSingleLink: true);
            _sequence = nextSequence;
            _lastEnvelopeHash = envelopeHash;
        }
        finally
        {
            TryDeleteTemp(temp);
            TryDeleteTemp(anchorTemp);
        }
    }

    public void Dispose() => _anchorHandle?.Dispose();

    private void Fault(string phase)
    {
        if (_fault?.Invoke(phase) == true)
            throw new LocalApiException("journal_persist_failed", "Injected durable journal failure.");
    }

    private void CleanupBoundedTemps()
    {
        var names = Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, "operations.v1.*.tmp")
            .Take(9).ToArray();
        if (names.Length > 8)
            throw new LocalApiException("journal_temp_capacity", "Too many journal recovery files remain.");
        foreach (var name in names) TryDeleteTemp(name);
    }

    private static LocalApiException Damage() =>
        new("journal_damaged", "The durable local API journal failed integrity validation.");

    private static byte[] ReadSecure(string path)
    {
        using var stream = FleetWindowsFileSafety.OpenReadOnlyFile(path);
        FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, path, requireSingleLink: true);
        if (stream.Length > int.MaxValue) throw Damage();
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, path, requireSingleLink: true);
        return bytes;
    }

    private static void WriteSecure(string path, byte[] bytes)
    {
        using var stream = FleetWindowsFileSafety.CreateNewOwnedFile(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, path, requireSingleLink: true);
    }

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var stream = FleetWindowsFileSafety.OpenRetainedReadOnlyFile(path);
            FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, path, requireSingleLink: true);
            FleetWindowsFileSafety.MarkDelete(stream.SafeFileHandle);
        }
        catch
        {
            // Bounded random temp names are retried/cleaned by the next state-directory maintenance pass.
        }
    }

    private sealed record JournalEnvelope(long Sequence, string PreviousEnvelopeHash, string Payload, string Hmac);
    private sealed record JournalAnchor(long Sequence, string EnvelopeHash, string Hmac);
}
