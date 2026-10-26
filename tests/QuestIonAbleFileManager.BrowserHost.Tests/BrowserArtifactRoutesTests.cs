using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using QuestIonAbleFileManager.BrowserHost;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserArtifactRoutesTests
{
    private const string Secret = "private-filesystem-secret-never-return";
    private const string Hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private static DefaultHttpContext Context(string contentType = "application/octet-stream", long? length = 3)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.ContentType = contentType; context.Request.ContentLength = length;
        context.Request.Headers["X-Qfm-File-Name"] = "fixture.apk";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("abc")); context.Response.Body = new MemoryStream();
        return context;
    }
    private static string Output(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    [Fact]
    public async Task MappingRoutesDoesNotCreateAStoreOrStartCleanup()
    {
        await using var app = WebApplication.CreateBuilder().Build();
        var accesses = 0;
        BrowserArtifactRoutes.Map(app, () => { accesses++; throw new IOException(Secret); });
        Assert.Equal(0, accesses);
    }

    [Theory]
    [InlineData("application/json", 3, 415)]
    [InlineData("application/octet-stream; charset=utf-8", 3, 415)]
    [InlineData("application/octet-stream", 1073741825, 413)]
    public async Task RejectedUploadMetadataNeverInitializesStoreOrReadsBody(string contentType, long length, int expected)
    {
        var context = Context(contentType, length); var accesses = 0;
        await BrowserArtifactRoutes.UploadAsync(context, () => { accesses++; throw new IOException(Secret); });
        Assert.Equal(expected, context.Response.StatusCode); Assert.Equal(0, accesses); Assert.Equal(0, context.Request.Body.Position);
        Assert.DoesNotContain(Secret, Output(context)); Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData("../fixture.apk", null)]
    [InlineData("fixture.apk", "INVALID")]
    public async Task InvalidNamesAndHashesRejectBeforeStoreInitialization(string name, string? hash)
    {
        var context = Context(); context.Request.Headers["X-Qfm-File-Name"] = name;
        if (hash is not null) context.Request.Headers["X-Qfm-Artifact-Sha256"] = hash;
        await BrowserArtifactRoutes.UploadAsync(context, () => throw new IOException(Secret));
        Assert.Equal(400, context.Response.StatusCode); Assert.Equal(0, context.Request.Body.Position); Assert.DoesNotContain(Secret, Output(context));
    }

    [Fact]
    public async Task MissingOrRepeatedUploadHeadersRejectBeforeReadingBody()
    {
        var context = Context(); context.Request.Headers.Remove("X-Qfm-File-Name");
        await BrowserArtifactRoutes.UploadAsync(context, () => throw new IOException(Secret));
        Assert.Equal(400, context.Response.StatusCode); Assert.Equal(0, context.Request.Body.Position);
        context = Context(); context.Request.Headers["X-Qfm-File-Name"] = new Microsoft.Extensions.Primitives.StringValues(["a.apk", "b.apk"]);
        await BrowserArtifactRoutes.UploadAsync(context, () => throw new IOException(Secret));
        Assert.Equal(400, context.Response.StatusCode); Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task DownloadRequiresExactOpaqueIdHashAndSizeBeforeOpeningStore()
    {
        var context = Context();
        await BrowserArtifactRoutes.DownloadAsync(context, new string('a', 32), () => throw new IOException(Secret));
        Assert.Equal(400, context.Response.StatusCode); Assert.DoesNotContain(Secret, Output(context));
        context = Context(); context.Request.Headers["X-Qfm-Artifact-Sha256"] = Hash;
        context.Request.Headers["X-Qfm-Artifact-Size-Bytes"] = "1073741825";
        await BrowserArtifactRoutes.DownloadAsync(context, new string('a', 32), () => throw new IOException(Secret));
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExactOneGiBTransportMetadataReachesStoreWithoutAllocatingOrReadingBody()
    {
        if (!OperatingSystem.IsWindows()) return;
        const long maximum = 1024L * 1024 * 1024;
        var context = Context(length: maximum); var accesses = 0;
        var limit = new BodyLimit { MaxRequestBodySize = 4096 }; context.Features.Set<IHttpMaxRequestBodySizeFeature>(limit);
        await BrowserArtifactRoutes.UploadAsync(context, () => { accesses++; throw new IOException(Secret); });
        Assert.Equal(1, accesses); Assert.Equal(500, context.Response.StatusCode); Assert.Equal(maximum, limit.MaxRequestBodySize);
        Assert.Equal(0, context.Request.Body.Position); Assert.DoesNotContain(Secret, Output(context));
        context = Context(); context.Request.Headers["X-Qfm-Artifact-Sha256"] = Hash;
        context.Request.Headers["X-Qfm-Artifact-Size-Bytes"] = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await BrowserArtifactRoutes.DownloadAsync(context, new string('a', 32), () => { accesses++; throw new IOException(Secret); });
        Assert.Equal(2, accesses); Assert.Equal(500, context.Response.StatusCode); Assert.DoesNotContain(Secret, Output(context));
    }

    [Fact]
    public async Task SuccessfulUploadDownloadAndCleanupRemainLocalAndReleaseTheLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var context = Context(); var limit = new BodyLimit { MaxRequestBodySize = 4096 }; context.Features.Set<IHttpMaxRequestBodySizeFeature>(limit);
        context.Request.Headers["X-Qfm-Artifact-Sha256"] = Hash;
        await BrowserArtifactRoutes.UploadAsync(context, () => store);
        Assert.Equal(201, context.Response.StatusCode); Assert.Equal(1073741824, limit.MaxRequestBodySize);
        using var json = System.Text.Json.JsonDocument.Parse(Output(context));
        Assert.Equal("localOnly", json.RootElement.GetProperty("scope").GetString()); Assert.False(json.RootElement.GetProperty("deviceDispatched").GetBoolean());
        var id = json.RootElement.GetProperty("artifact").GetProperty("id").GetString()!;
        var download = Context(); download.Request.Headers["X-Qfm-Artifact-Sha256"] = Hash; download.Request.Headers["X-Qfm-Artifact-Size-Bytes"] = "3";
        await BrowserArtifactRoutes.DownloadAsync(download, id, () => store);
        Assert.Equal(200, download.Response.StatusCode); Assert.Equal("abc", Output(download)); Assert.Equal(3, download.Response.ContentLength);
        Assert.Equal("application/octet-stream", download.Response.ContentType); Assert.Contains("attachment", download.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("no-store", download.Response.Headers.CacheControl);
        await store.RemoveAsync(id); Assert.Equal(0, await store.CleanupAsync());
        Assert.DoesNotContain("Path", Output(context));
    }

    [Fact]
    public async Task DownloadWriteFailureReleasesLeaseAndNeverIncludesExceptionText()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("fixture.apk", new MemoryStream(Encoding.UTF8.GetBytes("abc")));
        var context = Context(); context.Request.Headers["X-Qfm-Artifact-Sha256"] = receipt.Sha256;
        context.Request.Headers["X-Qfm-Artifact-Size-Bytes"] = receipt.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var output = new FailFirstWrite(); context.Response.Body = output;
        await BrowserArtifactRoutes.DownloadAsync(context, receipt.Id, () => store);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(output.ToArray()));
        await store.RemoveAsync(receipt.Id);
        Assert.Equal(0, await store.CleanupAsync());
    }

    [Fact]
    public async Task ExplicitRemoveRejectsLeasesAndCleanupRetainsThem()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("fixture.apk", new MemoryStream(Encoding.UTF8.GetBytes("abc")));
        await using var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes);
        var context = Context(length: 0);
        await BrowserArtifactRoutes.RemoveAsync(context, receipt.Id, () => store);
        Assert.Equal(409, context.Response.StatusCode); Assert.Contains("artifact_in_use", Output(context));
        context = Context(length: 0);
        await BrowserArtifactRoutes.CleanupAsync(context, () => store);
        Assert.Contains("\"removedCount\":0", Output(context));
        lease.Dispose();
        context = Context(length: 0);
        await BrowserArtifactRoutes.CleanupAsync(context, () => store);
        Assert.Contains("\"removedCount\":1", Output(context));
    }

    [Fact]
    public async Task ExceptionsNeverExposeMessagesAndUnsupportedPlatformsStayExplicit()
    {
        var context = Context();
        await BrowserArtifactRoutes.UploadAsync(context, () => throw new IOException(Secret));
        Assert.Equal(OperatingSystem.IsWindows() ? 500 : 501, context.Response.StatusCode); Assert.DoesNotContain(Secret, Output(context));
    }

    private sealed class BodyLimit : IHttpMaxRequestBodySizeFeature
    { public bool IsReadOnly => false; public long? MaxRequestBodySize { get; set; } }
    private sealed class FailFirstWrite : MemoryStream
    {
        private bool _failed;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_failed) { _failed = true; throw new IOException(Secret); }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
