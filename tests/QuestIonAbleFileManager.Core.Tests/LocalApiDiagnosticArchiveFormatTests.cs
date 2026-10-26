using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuestIonAbleFileManager.Core;
using Xunit.Abstractions;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class LocalApiDiagnosticArchiveFormatTests(ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullFixedCaptureSetProducesBoundedCanonicalStoredZip(bool fillEveryPayloadToItsLimit)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        using var input = await fixture.Stage();
        using var captured = await fixture.Reserve(input);
        var source = InspectedDeploymentTests.CreateDeploymentRunner(fixture.Source,
            pidofOutput: "11 12 13 14 15 16 17 18\n");
        var runner = new InspectedDeploymentTests.TruncatingDiagnosticRunner(source, "none");
        using var inputLease = input.BorrowDiagnosticInput();
        var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
            .CaptureAdmittedApkDiagnosticsAsync("QUEST123", inputLease, captured.CreateSink());
        Assert.Equal(14, result.Files.Count);
        Assert.Equal(8, result.Runtime.ProcessIds.Count);
        Assert.DoesNotContain(source.Calls, call => call.Arguments.Contains("install") || call.Arguments.Contains("am"));

        using var output = await fixture.Reserve(input);
        var (reemitted, contents) = await Reemit(fixture, captured, output, result, fillEveryPayloadToItsLimit);
        var candidate = await output.SealAsync(reemitted, default);
        testOutput.WriteLine($"Payloads={contents.Count}; payloadBytes={contents.Values.Sum(content => (long)content.Length)}; archiveBytes={candidate.SizeBytes}; everyPayloadAtCap={fillEveryPayloadToItsLimit}");
        Assert.InRange(candidate.SizeBytes, 1, 4194304);
        Assert.Equal("sealed", output.LastAcknowledgedSnapshot!.Phase);
        Assert.Equal(14, contents.Count);
        Assert.Equal(fillEveryPayloadToItsLimit ? 3670016L : contents.Values.Sum(bytes => (long)bytes.Length),
            reemitted.Files.Sum(file => file.SizeBytes));
        if (fillEveryPayloadToItsLimit)
        {
            Assert.All(contents.Values, bytes => Assert.Equal(262144, bytes.Length));
            Assert.InRange(candidate.SizeBytes, 3670016, 4194304);
        }

        using var lease = await output.OpenSealedCandidateAsync(candidate.SizeBytes, candidate.Sha256);
        using var buffer = new MemoryStream();
        await lease.Stream.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        Assert.Equal(candidate.SizeBytes, bytes.LongLength);
        Assert.Equal(candidate.Sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        AssertZipRecords(bytes, contents);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(contents.Keys.Order(StringComparer.Ordinal), archive.Entries.Select(entry => entry.FullName));
        foreach (var entry in archive.Entries)
        {
            Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime);
            Assert.Equal(entry.Length, entry.CompressedLength);
            using var extracted = new MemoryStream();
            using var stream = entry.Open();
            await stream.CopyToAsync(extracted);
            Assert.Equal(contents[entry.FullName], extracted.ToArray());
        }
    }

    private static async Task<(ApkDiagnosticBundleResult Result, Dictionary<string, byte[]> Contents)> Reemit(
        Fixture fixture, LocalApiDiagnosticOutputReservation captured, LocalApiDiagnosticOutputReservation output,
        ApkDiagnosticBundleResult result, bool atLimit)
    {
        var resources = captured.LastAcknowledgedSnapshot!.Resources.Where(resource => resource.Role == "payload")
            .ToDictionary(resource => resource.EntryName!, StringComparer.Ordinal);
        var artifact = result.Artifact with { Path = output.ReportedInputPath };
        var runtime = result.Runtime with { Artifact = artifact };
        var files = new List<ApkDiagnosticBundleFile>();
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in result.Files.Where(file => file.RelativePath != "diagnostic-manifest.json"))
        {
            var bytes = file.RelativePath == "runtime.json"
                ? JsonSerializer.SerializeToUtf8Bytes(runtime, JsonSerializerOptions.Web)
                : await File.ReadAllBytesAsync(Path.Combine(fixture.StageDirectory, resources[file.RelativePath].Leaf));
            if (atLimit) bytes = Pad(bytes);
            contents.Add(file.RelativePath, bytes);
            files.Add(await output.CreateSink().WriteAsync(file.CaptureKind, file.RelativePath, bytes,
                file.CommandExitCode, file.ObservationSource, file.CommandSemantic, file.Truncated, default));
        }
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture.StageDirectory,
            resources["diagnostic-manifest.json"].Leaf)))!;
        manifest["artifact"] = JsonSerializer.SerializeToNode(artifact, JsonSerializerOptions.Web);
        manifest["files"] = JsonSerializer.SerializeToNode(files, JsonSerializerOptions.Web);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonSerializerOptions.Web);
        if (atLimit) manifestBytes = Pad(manifestBytes);
        // Whitespace padding keeps all JSON payloads and the manifest valid at the exact byte cap.
        foreach (var name in new[] { "runtime.json", "device.json" })
            using (JsonDocument.Parse(contents[name])) { }
        using (JsonDocument.Parse(manifestBytes)) { }
        contents.Add("diagnostic-manifest.json", manifestBytes);
        files.Add(await output.CreateSink().WriteAsync("manifest", "diagnostic-manifest.json", manifestBytes,
            null, "private diagnostic manifest", "fixed diagnostic capture inventory and hashes", false, default));
        return (result with { Artifact = artifact, Runtime = runtime, OutputDirectory = output.ReportedOutputPath, Files = files }, contents);
    }

    private static byte[] Pad(byte[] original)
    {
        Assert.InRange(original.Length, 1, 262144);
        var bytes = new byte[262144];
        bytes.AsSpan().Fill((byte)' ');
        original.CopyTo(bytes, 0);
        return bytes;
    }

    private static void AssertZipRecords(byte[] bytes, IReadOnlyDictionary<string, byte[]> contents)
    {
        var end = bytes.Length - 22;
        Assert.Equal(0x06054b50u, U32(bytes, end));
        Assert.Equal(0, U16(bytes, end + 4)); // Single disk.
        Assert.Equal(0, U16(bytes, end + 6));
        Assert.Equal(14, U16(bytes, end + 8));
        Assert.Equal(14, U16(bytes, end + 10));
        Assert.Equal(0, U16(bytes, end + 20)); // No archive comment/trailing bytes.
        var centralSize = checked((int)U32(bytes, end + 12));
        var centralStart = checked((int)U32(bytes, end + 16));
        Assert.Equal(end, centralStart + centralSize);
        var position = centralStart;
        var nextLocal = 0;
        var names = new List<string>();
        for (var index = 0; index < 14; index++)
        {
            Assert.Equal(0x02014b50u, U32(bytes, position));
            var flags = U16(bytes, position + 8);
            Assert.Equal(0, flags & ~0x0808); // Only UTF-8 and data-descriptor flags; no encryption.
            Assert.Equal(0, U16(bytes, position + 10)); // STORE, not deflate at level zero.
            Assert.Equal(0, U16(bytes, position + 12)); // DOS time midnight.
            Assert.Equal(0x21, U16(bytes, position + 14)); // 1980-01-01.
            var crc = U32(bytes, position + 16);
            var compressed = U32(bytes, position + 20);
            var length = U32(bytes, position + 24);
            Assert.Equal(length, compressed);
            var nameLength = U16(bytes, position + 28);
            Assert.Equal(0, U16(bytes, position + 30)); // No central extra fields, including ZIP64.
            Assert.Equal(0, U16(bytes, position + 32)); // No entry comments.
            Assert.Equal(0, U16(bytes, position + 34)); // No other disk.
            Assert.Equal(0u, U32(bytes, position + 38) & 0x10u); // Not a DOS directory.
            var local = checked((int)U32(bytes, position + 42));
            Assert.Equal(nextLocal, local); // No hidden local entries or prepended data.
            var name = Encoding.UTF8.GetString(bytes, position + 46, nameLength);
            Assert.DoesNotContain('/', name);
            Assert.DoesNotContain('\\', name);
            Assert.DoesNotContain("..", name, StringComparison.Ordinal);
            Assert.True(contents.TryGetValue(name, out var expected), name);
            Assert.DoesNotContain(name, names);
            names.Add(name);
            Assert.Equal((uint)expected.Length, length);
            Assert.Equal(Crc32(expected), crc);

            Assert.Equal(0x04034b50u, U32(bytes, local));
            Assert.Equal(flags, U16(bytes, local + 6));
            Assert.Equal(0, U16(bytes, local + 8));
            Assert.Equal(0, U16(bytes, local + 10));
            Assert.Equal(0x21, U16(bytes, local + 12));
            Assert.Equal(nameLength, U16(bytes, local + 26));
            Assert.Equal(0, U16(bytes, local + 28)); // No local extra fields.
            Assert.Equal(name, Encoding.UTF8.GetString(bytes, local + 30, nameLength));
            var data = local + 30 + nameLength;
            Assert.Equal(expected, bytes.AsSpan(data, checked((int)length)).ToArray());
            nextLocal = checked(data + (int)length);
            if ((flags & 8) != 0)
            {
                Assert.Equal(0u, U32(bytes, local + 14));
                Assert.Equal(0u, U32(bytes, local + 18));
                Assert.Equal(0u, U32(bytes, local + 22));
                if (U32(bytes, nextLocal) == 0x08074b50u) nextLocal += 4;
                Assert.Equal(crc, U32(bytes, nextLocal));
                Assert.Equal(compressed, U32(bytes, nextLocal + 4));
                Assert.Equal(length, U32(bytes, nextLocal + 8));
                nextLocal += 12;
            }
            else
            {
                Assert.Equal(crc, U32(bytes, local + 14));
                Assert.Equal(compressed, U32(bytes, local + 18));
                Assert.Equal(length, U32(bytes, local + 22));
            }
            position += 46 + nameLength;
        }
        Assert.Equal(centralStart, nextLocal);
        Assert.Equal(end, position);
        Assert.Equal(contents.Keys.Order(StringComparer.Ordinal), names);
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static uint Crc32(byte[] bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return ~crc;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "qfm-diagnostic-zip-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(_root, "source.apk");
        internal string StageDirectory => Path.Combine(_root, "state", "staged");
        private LocalApiArtifactStager Stager { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Stager = new(LocalApiStateSettings.CreateForTests(Path.Combine(_root, "state")));
        }
        internal async Task<LocalApiStagedArtifact> Stage()
        {
            await File.WriteAllBytesAsync(Source, [0x50, 0x4b, 0x03, 0x04]);
            return await Stager.StageDiagnosticInputAsync(Source, (_, _) => Task.CompletedTask);
        }
        internal Task<LocalApiDiagnosticOutputReservation> Reserve(LocalApiStagedArtifact input) =>
            Stager.ReserveDiagnosticOutputAsync(LocalApiDiagnosticOutputIntent.Create("api-" + Guid.NewGuid().ToString("N")),
                input, (_, _) => Task.CompletedTask);
        public void Dispose() { Stager.Dispose(); Directory.Delete(_root, true); }
    }
}
