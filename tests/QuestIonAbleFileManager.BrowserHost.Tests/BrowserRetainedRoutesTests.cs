using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserRetainedRoutesTests
{
    private const string OperationId = "api-00000000000040008000000000000001";
    private static readonly string Digest = new('a', 64);
    private static readonly byte[] Bound = JsonSerializer.SerializeToUtf8Bytes(new { operationId = OperationId, commandDigest = Digest });
    private static DefaultHttpContext Context(byte[] body, string type = "application/json")
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.ContentType = type; context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body); context.Response.Body = new MemoryStream(); return context;
    }
    private static string Output(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    private static LocalApiOperationStatus Status(LocalApiOperationStage stage) => new(LocalApiContract.Version, OperationId,
        "apk.install-inspected", Digest, DateTimeOffset.UtcNow.AddMinutes(2), stage, null, null, null, null);
    private static BrowserRetainedOwnerCalls Owner(Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiOperationStatus>> execute,
        Func<ReadOnlyMemory<byte>, LocalApiOperationStatus>? status = null) => new(
        (_, _) => throw new InvalidOperationException(), execute, status ?? (_ => Status(LocalApiOperationStage.Preflighted)),
        _ => Status(LocalApiOperationStage.CancellationRequested), () => { });

    [Theory]
    [InlineData("application/octet-stream", 3, 415)]
    [InlineData("application/json; charset=utf-8", 3, 415)]
    [InlineData("application/json", 4097, 413)]
    public async Task InvalidTransportMetadataIsRejectedWithoutReadingOrCreatingOwner(string contentType, int size, int expected)
    {
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException("no store"), null, _ => null, true);
        var context = Context(new byte[size], contentType);
        await BrowserRetainedRoutes.InvokeAsync(context, operations, "execute");
        Assert.Equal(expected, context.Response.StatusCode); Assert.Equal(0, context.Request.Body.Position);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task PageDisconnectDoesNotCancelTheOwnerOrReleaseSharedGateBeforeAcknowledgement()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<LocalApiOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ownerToken = default; var held = false; var current = Status(LocalApiOperationStage.Preflighted);
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException(), Owner(async (_, token) =>
        { ownerToken = token; running.SetResult(); current = await acknowledged.Task; return current; }, _ => current), () => { held = true; return new Release(() => held = false); });
        var context = Context(Bound); using var page = new CancellationTokenSource(); context.RequestAborted = page.Token;
        using var ownerLifetime = new CancellationTokenSource();
        var request = BrowserRetainedRoutes.InvokeAsync(context, operations, "execute", ownerLifetime.Token);
        await running.Task; page.Cancel(); Assert.True(held); Assert.False(ownerToken.IsCancellationRequested); Assert.False(request.IsCompleted);
        acknowledged.SetResult(Status(LocalApiOperationStage.OutcomeUnknownRecoveryRequired)); await request;
        Assert.False(held); Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, operations.GetStatus(Bound).Stage);
    }

    [Fact]
    public async Task ConsumedErrorsAndExceptionMessagesNeverBecomeNoDispatchOrPrivateDataClaims()
    {
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException(),
            Owner((_, _) => throw new LocalApiException("operation_consumed", "private-path-secret-never-project")));
        var context = Context(Bound); await BrowserRetainedRoutes.InvokeAsync(context, operations, "execute");
        Assert.Equal(409, context.Response.StatusCode); Assert.Contains("operation_consumed", Output(context));
        Assert.DoesNotContain("false", Output(context)); Assert.DoesNotContain("private-path-secret", Output(context));
    }

    [Fact]
    public async Task UnknownOwnerStageIsProjectedAsUnknownAndNeverPromotedToCompleted()
    {
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException(),
            Owner((_, _) => Task.FromResult(Status(LocalApiOperationStage.OutcomeUnknownRecoveryRequired))));
        var context = Context(Bound); await BrowserRetainedRoutes.InvokeAsync(context, operations, "execute");
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Contains("outcomeUnknownRecoveryRequired", Output(context)); Assert.DoesNotContain("\"stage\":\"completed\"", Output(context));
    }

    [Fact]
    public async Task MissingConfigurationReturnsClearPathFreeAlternative()
    {
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException(), null, _ => null, true);
        var context = Context(Bound); await BrowserRetainedRoutes.InvokeAsync(context, operations, "status");
        Assert.Equal(503, context.Response.StatusCode); Assert.Contains("retained_configuration_missing", Output(context));
        Assert.Contains("QUESTIONABLE_FILE_MANAGER_API_STATE", Output(context));
    }
    private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }
}
