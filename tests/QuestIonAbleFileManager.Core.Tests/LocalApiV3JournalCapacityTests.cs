using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LocalApiV3JournalCapacityTests
{
    [Fact]
    public void PromiseIncludesDefaultEncoderEscapingOfBase64PlusBeforeAnyRowOrFileAdmission()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { versionName = new string('~', 200) });
        var base64 = Convert.ToBase64String(json);
        Assert.Contains('+', base64);
        var encoded = JsonSerializer.Serialize(base64);
        Assert.Contains("\\u002B", encoded);
        Assert.True(Encoding.UTF8.GetByteCount(encoded) > base64.Length + 2);
        var root = Path.Combine(Path.GetTempPath(), "qfm-v3-capacity-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var journal = new LocalApiJournal(LocalApiStateSettings.CreateForTests(root, new(MaximumJournalBytes: 128 * 1024)));
            Assert.Equal("journal_capacity", Assert.Throws<LocalApiException>(() => journal.EnsureV3Capacity([], 1)).Code);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FutureRowSizingIncludesLongStagedPathTwiceWithoutDroppingRealArtifactFacts()
    {
        var intent = LocalApiDiagnosticOutputIntent.Create("api-" + new string('a', 32));
        var inputLeaf = new string('c', 32) + ".apk";
        var stagedPath = @"C:\" + new string('s', 20_000) + @"\staged\" + inputLeaf;
        var artifact = new ApkArtifactInspection(@"C:\source\base.apk", 4, new string('d', 64),
            new("com.example.app", 42, new string('~', 25_000), new string('a', 64), null));
        var resources = new[] { new LocalApiDiagnosticResourceSnapshot("input", -1, inputLeaf, 4, "createPending") }
            .Concat(intent.PayloadLeaves.Select((leaf, slot) => new LocalApiDiagnosticResourceSnapshot("payload", slot, leaf, 262144, "planned")))
            .Append(new("archive", 14, intent.ArchiveLeaf, 4194304, "planned")).ToArray();
        var now = DateTimeOffset.UtcNow;
        var row = new LocalApiJournalEntry(intent.OperationId, "apk.diagnose", new string('e', 64), now.AddMinutes(2), stagedPath, artifact,
            "QUEST123", null, LocalApiOperationStage.Preparing, false, false, null, null, null, null, null, LocalApiV3Contract.Version,
            new(intent.OutputId, intent.PlannedLeafDigest, "reserved", resources, now, now.AddMinutes(5)));
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(row, options).Length < 65536);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(row with { Artifact = artifact with { Path = stagedPath } }, options).Length > 65536);
        Assert.Equal("diagnostic_metadata_capacity", Assert.Throws<LocalApiException>(() => LocalApiJournal.EnsureV3FutureRowFits(row)).Code);
        Assert.Equal(25_000, row.Artifact.Identity.VersionName!.Length);
        Assert.Equal(@"C:\source\base.apk", row.Artifact.Path);
    }
}
