using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using QuestIonAbleFileManager.BrowserHost;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserProfileRoutesTests
{
    private static DefaultHttpContext Context(BrowserProfileTests.Fixture fixture, byte[]? body = null)
    {
        body ??= BrowserProfileTests.Document();
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.ContentType = "application/json"; context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body); context.Response.Body = new MemoryStream();
        context.Request.Headers["X-Qfm-Session"] = fixture.Commands.SessionId;
        context.Request.Headers["X-Qfm-Request-Id"] = Guid.NewGuid().ToString("D");
        context.Request.Headers["X-Qfm-Profile-Device-Id"] = BrowserProfileTests.DeviceId;
        context.Request.Headers["X-Qfm-Confirm-Profile-Write"] = "true";
        context.Request.Headers["X-Qfm-Replace-Existing"] = "false";
        return context;
    }
    private static string Output(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    [Fact]
    public async Task SensitiveEndpointUsesOnlyItsFixedBodyAndHeadersAndZerosEveryOwnedCopy()
    {
        using var fixture = new BrowserProfileTests.Fixture(); var context = Context(fixture);
        await BrowserProfileRoutes.ImportAsync(context, fixture.Commands, fixture.Profiles);
        Assert.Equal(200, context.Response.StatusCode); Assert.Equal("no-store", context.Response.Headers.CacheControl);
        using var json = JsonDocument.Parse(Output(context));
        Assert.Equal("confirmed", json.RootElement.GetProperty("profile").GetProperty("effect").GetString());
        Assert.All(fixture.Reader.Returned, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        Assert.DoesNotContain(BrowserProfileTests.Secret, Output(context)); Assert.DoesNotContain("192.168.", Output(context));
    }
    [Theory]
    [InlineData("X-Qfm-Request-Id")]
    [InlineData("X-Qfm-Profile-Device-Id")]
    [InlineData("X-Qfm-Confirm-Profile-Write")]
    [InlineData("X-Qfm-Replace-Existing")]
    [InlineData("X-Qfm-Session")]
    [InlineData("Authorization")]
    [InlineData("Origin")]
    public async Task DuplicateHeadersRejectBeforeAnySensitiveReadOrOwner(string header)
    {
        using var fixture = new BrowserProfileTests.Fixture(); var context = Context(fixture);
        context.Request.Headers[header] = new StringValues(["value", "value"]);
        await BrowserProfileRoutes.ImportAsync(context, fixture.Commands, fixture.Profiles);
        Assert.Equal(400, context.Response.StatusCode); Assert.Empty(fixture.Calls); Assert.Empty(fixture.Reader.Returned);
        Assert.Contains("\"dispatched\":false", Output(context));
    }
    [Theory]
    [InlineData("X-Qfm-Confirm-Profile-Write", "false")]
    [InlineData("X-Qfm-Replace-Existing", "TRUE")]
    [InlineData("X-Qfm-Replace-Existing", "")]
    [InlineData("X-Qfm-Endpoint", "secret")]
    [InlineData("X-Qfm-Profile-Extra", "secret")]
    [InlineData("X-Qfm-Request-Id", "00000000-0000-0000-0000-000000000000")]
    [InlineData("X-Qfm-Profile-Device-Id", "UPPER")]
    public async Task ClosedHeaderValuesAndNoSecretOptionsAreEnforced(string header, string value)
    {
        using var fixture = new BrowserProfileTests.Fixture(); var context = Context(fixture); context.Request.Headers[header] = value;
        await BrowserProfileRoutes.ImportAsync(context, fixture.Commands, fixture.Profiles);
        Assert.Equal(400, context.Response.StatusCode); Assert.Empty(fixture.Calls); Assert.Equal(0, context.Request.Body.Position);
    }
    [Theory]
    [InlineData("application/json; charset=utf-8", 10, null, 415)]
    [InlineData("application/octet-stream", 10, null, 415)]
    [InlineData("application/json", 4097, null, 413)]
    [InlineData("application/json", 0, null, 413)]
    [InlineData("application/json", 10, "gzip", 415)]
    public async Task TransportBoundsRejectBeforeReading(string type, long length, string? encoding, int expected)
    {
        using var fixture = new BrowserProfileTests.Fixture(); var context = Context(fixture); context.Request.ContentType = type;
        context.Request.ContentLength = length; if (encoding is not null) context.Request.Headers.ContentEncoding = encoding;
        await BrowserProfileRoutes.ImportAsync(context, fixture.Commands, fixture.Profiles);
        Assert.Equal(expected, context.Response.StatusCode); Assert.Empty(fixture.Reader.Returned); Assert.Empty(fixture.Calls);
    }
    [Fact]
    public async Task ChunkedOversizeMismatchedIdAndUnknownFieldsCannotReachOwner()
    {
        using var fixture = new BrowserProfileTests.Fixture();
        foreach (var bytes in new[] { new byte[4097], BrowserProfileTests.Document("other-id"), Encoding.UTF8.GetBytes("{\"private\":\"secret\"}") })
        {
            var context = Context(fixture, bytes); context.Request.ContentLength = null;
            await BrowserProfileRoutes.ImportAsync(context, fixture.Commands, fixture.Profiles);
            Assert.True(context.Response.StatusCode is 400 or 413); Assert.Empty(fixture.Calls);
            Assert.All(fixture.Reader.Returned, returned => Assert.All(returned, value => Assert.Equal(0, value)));
        }
    }
    [Fact]
    public async Task UnsupportedBeforeBodyAndConsumedBeforeRepeatReadNeverBecomeNativeActivation()
    {
        using var unsupported = new BrowserProfileTests.Fixture(supported: false); var denied = Context(unsupported);
        await BrowserProfileRoutes.ImportAsync(denied, unsupported.Commands, unsupported.Profiles);
        Assert.Equal(503, denied.Response.StatusCode); Assert.Empty(unsupported.Reader.Returned); Assert.Contains("alternative", Output(denied));
        using var fixture = new BrowserProfileTests.Fixture(); var first = Context(fixture);
        await BrowserProfileRoutes.ImportAsync(first, fixture.Commands, fixture.Profiles);
        var copies = fixture.Reader.Returned.Count; var duplicate = Context(fixture);
        duplicate.Request.Headers["X-Qfm-Request-Id"] = first.Request.Headers["X-Qfm-Request-Id"];
        await BrowserProfileRoutes.ImportAsync(duplicate, fixture.Commands, fixture.Profiles);
        Assert.Equal(409, duplicate.Response.StatusCode); Assert.Equal(copies, fixture.Reader.Returned.Count); Assert.DoesNotContain("dispatched", Output(duplicate));
    }
    [Fact]
    public async Task StalledBodyHasFiniteReceiveTimeoutWithoutConsumingItsId()
    {
        using var fixture = new BrowserProfileTests.Fixture(); var context = Context(fixture); context.Request.ContentLength = null; context.Request.Body = new StalledStream();
        await BrowserProfileRoutes.ImportAsync(context, fixture.Commands, fixture.Profiles, receiveTimeout: TimeSpan.FromMilliseconds(20));
        Assert.Equal(408, context.Response.StatusCode); Assert.Empty(fixture.Calls); Assert.Contains("\"dispatched\":false", Output(context));
        Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => fixture.Commands.Get(context.Request.Headers["X-Qfm-Request-Id"].ToString())).Code);
    }
    [Fact]
    public async Task AbortedPageAndLifetimeCannotCancelAdmittedCoreWriteAndLookupRemainsSanitized()
    {
        using var fixture = new BrowserProfileTests.Fixture(); var context = Context(fixture);
        using var page = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource(); context.RequestAborted = page.Token;
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        fixture.Store.BeforeWrite = () => { started.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var operation = Task.Run(() => BrowserProfileRoutes.ImportAsync(context, fixture.Commands, fixture.Profiles, lifetime.Token));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10))); page.Cancel(); lifetime.Cancel();
        var drain = fixture.Commands.DrainProfilesAsync(); Assert.False(drain.IsCompleted); Assert.True(fixture.Gate.IsBusy); Assert.False(operation.IsCompleted);
        release.Set(); await operation; await drain;
        Assert.Equal(200, context.Response.StatusCode); Assert.DoesNotContain("\"dispatched\":false", Output(context));
        Assert.Equal("completed", fixture.Commands.Get(context.Request.Headers["X-Qfm-Request-Id"].ToString()).State);
        Assert.All(fixture.Reader.Returned, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }
    [Fact]
    public async Task DroppedResponseAfterConfirmedWriteNeverBecomesANoDispatchError()
    {
        using var fixture = new BrowserProfileTests.Fixture(); var context = Context(fixture); context.Response.Body = new BrokenResponse();
        await Assert.ThrowsAsync<IOException>(() => BrowserProfileRoutes.ImportAsync(context, fixture.Commands, fixture.Profiles));
        Assert.Equal(1, fixture.Store.Writes);
        Assert.Equal("completed", fixture.Commands.Get(context.Request.Headers["X-Qfm-Request-Id"].ToString()).State);
        Assert.All(fixture.Reader.Returned, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        Assert.False(fixture.Gate.IsBusy); Assert.True(fixture.Commands.DrainProfilesAsync().IsCompleted);
    }
    [Fact]
    public async Task LyingDeclaredLengthAndEscapedDuplicatePrivateFieldsFailBeforeStore()
    {
        using var fixture = new BrowserProfileTests.Fixture(); var length = Context(fixture); length.Request.ContentLength--;
        await BrowserProfileRoutes.ImportAsync(length, fixture.Commands, fixture.Profiles);
        Assert.Equal(400, length.Response.StatusCode); Assert.Empty(fixture.Calls);
        var text = Encoding.UTF8.GetString(BrowserProfileTests.Document()).Replace("\"device_id\":", "\"device_id\":\"other\",\"device_id\":", StringComparison.Ordinal);
        var duplicate = Context(fixture, Encoding.UTF8.GetBytes(text)); await BrowserProfileRoutes.ImportAsync(duplicate, fixture.Commands, fixture.Profiles);
        Assert.Equal(400, duplicate.Response.StatusCode); Assert.Contains("profileDocumentInvalid", Output(duplicate)); Assert.Empty(fixture.Calls);
        Assert.All(fixture.Reader.Returned, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }
    [Fact]
    public async Task BodyFinishingAfterEmptyDrainCannotActivateStore()
    {
        using var fixture = new BrowserProfileTests.Fixture(); var context = Context(fixture);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reader.BeforeRead = async () => { reading.SetResult(); await release.Task; };
        var operation = BrowserProfileRoutes.ImportAsync(context, fixture.Commands, fixture.Profiles);
        await reading.Task; await fixture.Commands.DrainProfilesAsync(); release.SetResult(); await operation;
        Assert.Equal(409, context.Response.StatusCode); Assert.Empty(fixture.Calls); Assert.Equal(0, fixture.Store.Writes);
        Assert.All(fixture.Reader.Returned, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }
    [Fact]
    public async Task UnsupportedCompositionStillReportsConsumedIdWithoutFalseNoDispatch()
    {
        using var fixture = new BrowserProfileTests.Fixture(supported: false); var context = Context(fixture);
        var id = context.Request.Headers["X-Qfm-Request-Id"].ToString();
        await fixture.Commands.ExecuteAsync(BrowserProfileTests.Body("devices", id));
        await BrowserProfileRoutes.ImportAsync(context, fixture.Commands, fixture.Profiles);
        Assert.Equal(409, context.Response.StatusCode); Assert.Contains("request_already_consumed", Output(context)); Assert.DoesNotContain("dispatched", Output(context));
        Assert.Empty(fixture.Reader.Returned); Assert.Empty(fixture.Calls);
    }
    private sealed class BrokenResponse : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("private response failure"));
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
