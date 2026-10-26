using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserArtifactCapacityTests
{
    private const long OneGiB = 1024L * 1024 * 1024;
    private const long TwoGiB = 2 * OneGiB;

    [Fact]
    public void DefaultQuotaAndPublicCapabilityAgreeWithCoreSingleArtifactCeiling()
    {
        var limits = new BrowserArtifactLimits();
        Assert.Equal(OneGiB, limits.MaximumFileBytes); Assert.Equal(TwoGiB, limits.MaximumTotalBytes); Assert.Equal(8, limits.MaximumFiles);
        Assert.Equal(QuestIonAbleFileManager.Core.LocalApiStateLimits.DefaultMaximumSingleArtifactBytes, limits.MaximumFileBytes);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(BrowserArtifactStore.Capability,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(OneGiB, json.RootElement.GetProperty("maximumFileBytes").GetInt64());
        Assert.Equal(TwoGiB, json.RootElement.GetProperty("maximumTotalBytes").GetInt64());
        Assert.Equal(8, json.RootElement.GetProperty("maximumFiles").GetInt32());
        Assert.Equal(300, json.RootElement.GetProperty("transferTimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task ExactMaximumMetadataEntersBoundedReadAndOneByteMoreRejectsBeforeReading()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        using var exact = new ReadProbe();
        await Assert.ThrowsAsync<ProbeReachedException>(() => store.StageAsync("limit.apk", exact, OneGiB));
        Assert.Equal(1, exact.Reads); Assert.Equal(0, await store.CleanupAsync());
        using var larger = new ReadProbe();
        var rejected = await Assert.ThrowsAsync<BrowserArtifactException>(() => store.StageAsync("too-large.apk", larger, OneGiB + 1));
        Assert.Equal("artifact_size_limit", rejected.Code); Assert.Equal(0, larger.Reads); Assert.Equal(0, await store.CleanupAsync());
    }

    [Fact]
    public async Task ScaledOneAndTwoGiBQuotasAcceptExactAggregateAndRecoverOnlyOwnedCapacity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(Path.GetTempPath(), new(4, 8, 8));
        var first = await store.StageAsync("a.bin", new MemoryStream([1, 2, 3, 4]), 4);
        var second = await store.StageAsync("b.bin", new MemoryStream([5, 6, 7, 8]), 4);
        using var beyond = new ReadProbe();
        var rejected = await Assert.ThrowsAsync<BrowserArtifactException>(() => store.StageAsync("full.bin", beyond, 1));
        Assert.Equal("artifact_byte_capacity", rejected.Code); Assert.Equal(0, beyond.Reads);
        await using var lease = await store.AcquireAsync(first.Id, first.Sha256, first.SizeBytes);
        Assert.Equal(1, await store.CleanupAsync());
        var replacement = await store.StageAsync("replacement.bin", new MemoryStream([9, 10, 11, 12]), 4);
        Assert.Equal(4, replacement.SizeBytes);
        lease.Dispose(); Assert.Equal(2, await store.CleanupAsync());
    }

    [Fact]
    public async Task EightFileBoundaryIsIndependentOfBytesAndMaximumQuotaConfigurationIsBounded()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(Path.GetTempPath(), new(OneGiB, 8, TwoGiB));
        for (var index = 0; index < 8; index++) await store.StageAsync($"empty-{index}.bin", new MemoryStream(), 0);
        var rejected = await Assert.ThrowsAsync<BrowserArtifactException>(() => store.StageAsync("ninth.bin", new MemoryStream(), 0));
        Assert.Equal("artifact_file_capacity", rejected.Code); Assert.Equal(8, await store.CleanupAsync());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrowserArtifactStore(Path.GetTempPath(), new(OneGiB + 1, 8, TwoGiB)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrowserArtifactStore(Path.GetTempPath(), new(OneGiB, 8, TwoGiB + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrowserArtifactStore(Path.GetTempPath(), new(OneGiB, 9, TwoGiB)));
    }

    [Theory]
    [InlineData(268435457)]
    [InlineData(1073741824)]
    public void TransientAndRetainedParsersAcceptBindingsAboveFormerCeilingThroughExactNewLimit(long size)
    {
        var artifactId = "00000000000040008000000000000002"; var sha256 = new string('a', 64);
        var inspect = JsonSerializer.SerializeToUtf8Bytes(new { requestId = "00000000-0000-4000-8000-000000000001", action = "apk.inspect", artifactId, sha256, sizeBytes = size });
        Assert.Equal(size, BrowserArtifactCommands.Parse(inspect).SizeBytes);
        var retained = JsonSerializer.SerializeToUtf8Bytes(new { artifactId, sha256, sizeBytes = size, command = "apk.inspect", serial = (string?)null });
        Assert.Equal(size, BrowserRetainedOperations.ParsePreflight(retained).SizeBytes);
        var invalid = JsonSerializer.SerializeToUtf8Bytes(new { artifactId, sha256, sizeBytes = OneGiB + 1, command = "apk.inspect", serial = (string?)null });
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParsePreflight(invalid));
    }

    private sealed class ProbeReachedException : Exception;
    private sealed class ReadProbe : Stream
    {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Reads++; throw new ProbeReachedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; throw new ProbeReachedException(); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
