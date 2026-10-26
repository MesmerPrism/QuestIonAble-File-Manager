using System.Text.Json;

namespace QuestIonAbleFileManager.Core;

// Private authenticated resource authority. Never serialize this type in a public response.
internal sealed record LocalApiV3JournalState(
    string OutputId, string PlannedLeafDigest, string ResourcePhase,
    IReadOnlyList<LocalApiDiagnosticResourceSnapshot> Resources,
    DateTimeOffset PreparationStartedAt, DateTimeOffset PreparationDeadline,
    bool Ready = false, bool OutputStarted = false, bool CleanupStarted = false,
    LocalApiV3DiagnosticSummary? Summary = null, LocalApiV3ArchiveDescriptor? Archive = null)
{
    internal LocalApiDiagnosticOutputSnapshot Snapshot(string operationId) => new(
        LocalApiDiagnosticOutputIntent.Restore(operationId, OutputId, PlannedLeafDigest), ResourcePhase, Resources);

    internal LocalApiV3JournalState WithSnapshot(LocalApiDiagnosticOutputSnapshot snapshot) => this with
    { ResourcePhase = snapshot.Phase, Resources = snapshot.Resources };
}

internal sealed partial class LocalApiJournal
{
    internal const int MaximumV3RowBytes = 64 * 1024;
    internal const long MaximumV3EnvelopeBytes = 4 * 1024 * 1024;

    internal static void EnsureV3FutureRowFits(LocalApiJournalEntry row)
    {
        var metadata = row.Diagnostic!;
        // Sizing-only upper envelope: these synthetic maximum-width fields NEVER
        // enter an operation, journal, callback, or native ownership decision.
        // Keep actual immutable APK facts/paths, escaping and serializer behavior.
        var time = new DateTimeOffset(9999, 12, 30, 23, 59, 59, TimeSpan.FromHours(-14)).AddTicks(9_999_999);
        var resources = metadata.Resources.Select(resource => resource with
        {
            State = "identityUnknown", Identity = new(uint.MaxValue, ulong.MaxValue), SizeBytes = resource.MaximumBytes,
            Sha256 = new string('f', 64), EntryName = resource.Role == "payload" ? new string('e', 64) : null,
            DebtCode = new string('e', 64)
        }).ToArray();
        var artifactPath = JsonSerializer.SerializeToUtf8Bytes(row.Artifact.Path, Json).Length >= JsonSerializer.SerializeToUtf8Bytes(row.StagedPath, Json).Length
            ? row.Artifact.Path : row.StagedPath;
        var estimate = row with
        {
            Artifact = row.Artifact with { Path = artifactPath }, TerminalAt = time, ErrorCode = new string('e', 64),
            Diagnostic = metadata with { ResourcePhase = "outcomeUnknown", Resources = resources,
                Summary = new("questionable.file_manager.apk_diagnostic_bundle.v3", time, "complete", 14, 14, 14),
                Archive = new(metadata.OutputId, "apk-diagnostics.zip", LocalApiDiagnosticOutputIntent.MaximumArchiveBytes, new string('f', 64), time) }
        };
        if (JsonSerializer.SerializeToUtf8Bytes(estimate, Json).Length > MaximumV3RowBytes)
            throw new LocalApiException("diagnostic_metadata_capacity", "The diagnostic source facts leave insufficient durable resource metadata capacity.");
    }

    // Reserve the final encoded size, including base64 and the envelope. Later legacy
    // saves cannot consume space promised to a still-growing diagnostic resource row.
    internal void EnsureV3Capacity(IReadOnlyList<LocalApiJournalEntry> entries, int pendingRows = 0)
    {
        var diagnostic = entries.Where(entry => entry.OperationVersion == LocalApiV3Contract.Version).ToArray();
        if (diagnostic.Length == 0 && pendingRows == 0) return;
        long payloadBytes = JsonSerializer.SerializeToUtf8Bytes(entries, Json).LongLength;
        foreach (var entry in diagnostic)
        {
            var count = JsonSerializer.SerializeToUtf8Bytes(entry, Json).Length;
            if (count > MaximumV3RowBytes)
                throw new LocalApiException("diagnostic_metadata_capacity", "The diagnostic resource metadata limit is exhausted.");
            payloadBytes = checked(payloadBytes + MaximumV3RowBytes - count);
        }
        payloadBytes = checked(payloadBytes + (long)pendingRows * (MaximumV3RowBytes + 1));
        // The unchanged default JSON encoder escapes '+' in Base64 as \u002B.
        // Its JSON payload bytes are ASCII (non-ASCII input is \u-escaped).
        // For a three-byte ASCII group, '+' can occur only in sextet four;
        // '~~~' -> 'fn5+' reaches that maximum. Measure that group's encoded
        // width using the SAME serializer, and reserve every group at that width.
        var groups = checked((payloadBytes + 2) / 3);
        var groupWidth = JsonSerializer.SerializeToUtf8Bytes("fn5+", Json).Length - JsonSerializer.SerializeToUtf8Bytes("", Json).Length;
        var overhead = JsonSerializer.SerializeToUtf8Bytes(new JournalEnvelope(long.MaxValue, new string('f', 64), "", new string('f', 64)), Json).Length;
        if (checked(groups * groupWidth + overhead) > Math.Min(_maximumBytes, MaximumV3EnvelopeBytes))
            throw new LocalApiException("journal_capacity", "The durable API journal capacity is exhausted.");
    }
}
