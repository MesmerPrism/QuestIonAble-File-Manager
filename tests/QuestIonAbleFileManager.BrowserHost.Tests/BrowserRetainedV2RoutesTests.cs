using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserRetainedV2RoutesTests
{
    private static DefaultHttpContext Context(byte[] body, string contentType = "application/json")
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.ContentType = contentType; context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body); context.Response.Body = new MemoryStream(); return context;
    }
    private static string Output(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    [Fact]
    public async Task BothExplicitRouteFamiliesUseTheSameConfiguredAdapterWithoutOpeningIt()
    {
        var opened = false;
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException("no stage"),
            (Func<BrowserRetainedOwnerCalls>)(() => { opened = true; throw new InvalidOperationException("not an operation"); }));
        await using var app = WebApplication.CreateBuilder().Build();
        BrowserRetainedRoutes.Map(app, operations);
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText).ToArray();
        foreach (var version in new[] { "v1", "v2" })
            foreach (var action in new[] { "preflight", "execute", "status", "cancel" })
                Assert.Contains($"/browser/{version}/retained/{action}", routes);
        Assert.False(opened);
        var capability = JsonSerializer.SerializeToElement(operations.Capability, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal([LocalApiContract.Version, LocalApiV2Contract.Version], capability.GetProperty("contractVersions").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [Theory]
    [InlineData("preflight")]
    [InlineData("execute")]
    [InlineData("status")]
    [InlineData("cancel")]
    public async Task V2RoutesRejectUnversionedOrCrossedBodiesBeforeOwnerCreation(string action)
    {
        var opened = false;
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException("no stage"),
            (Func<BrowserRetainedOwnerCalls>)(() => { opened = true; throw new InvalidOperationException("no owner call"); }));
        foreach (var body in new[]
        {
            Encoding.UTF8.GetBytes("{\"operationId\":\"" + BrowserRetainedV2OperationsTests.OperationId + "\",\"commandDigest\":\"" + BrowserRetainedV2OperationsTests.Digest + "\"}"),
            BrowserRetainedV2OperationsTests.Bound(LocalApiContract.Version)
        })
        {
            var context = Context(body); await BrowserRetainedRoutes.InvokeV2Async(context, operations, action);
            Assert.Equal(400, context.Response.StatusCode); Assert.Equal("no-store", context.Response.Headers.CacheControl); Assert.False(opened);
            Assert.DoesNotContain("dispatched", Output(context));
        }
    }

    [Theory]
    [InlineData(LocalApiOperationStage.Cancelled)]
    [InlineData(LocalApiOperationStage.OutcomeUnknownRecoveryRequired)]
    public async Task V2PageLossAndCancellationRequestKeepGateUntilActualOwnerAcknowledgement(LocalApiOperationStage acknowledgement)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<LocalApiOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new BrowserOperationGate(); var current = BrowserRetainedV2OperationsTests.Status(LocalApiOperationStage.Preflighted);
        var executions = 0; var cancellations = 0; CancellationToken executedToken = default;
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException("no source reacquisition"),
            BrowserRetainedV2OperationsTests.Owner(execute: async (body, token) =>
            {
                executions++; executedToken = token;
                using var request = JsonDocument.Parse(body); Assert.Equal(LocalApiV2Contract.Version, request.RootElement.GetProperty("contractVersion").GetString());
                current = BrowserRetainedV2OperationsTests.Status(LocalApiOperationStage.Running); started.SetResult();
                current = await acknowledged.Task; return current;
            }, status: body => { CheckMetadata(body); return current; }, cancel: body =>
            {
                CheckMetadata(body); cancellations++;
                if (current.Stage == LocalApiOperationStage.Running)
                    current = BrowserRetainedV2OperationsTests.Status(LocalApiOperationStage.CancellationRequested);
                return current;
            }), gate.TryAcquire);
        using var page = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
        var context = Context(BrowserRetainedV2OperationsTests.Bound()); context.RequestAborted = page.Token;
        var executing = BrowserRetainedRoutes.InvokeV2Async(context, operations, "execute", lifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); page.Cancel();
        Assert.True(gate.IsBusy); Assert.False(executedToken.IsCancellationRequested); Assert.False(executing.IsCompleted);
        var cancel = Context(BrowserRetainedV2OperationsTests.Bound());
        await BrowserRetainedRoutes.InvokeV2Async(cancel, operations, "cancel");
        Assert.Contains("cancellationRequested", Output(cancel)); Assert.True(gate.IsBusy); Assert.False(executing.IsCompleted);
        var duplicate = Context(BrowserRetainedV2OperationsTests.Bound()); await BrowserRetainedRoutes.InvokeV2Async(duplicate, operations, "execute");
        Assert.Equal(409, duplicate.Response.StatusCode); Assert.Contains("operation_consumed", Output(duplicate)); Assert.DoesNotContain("dispatched", Output(duplicate));
        acknowledged.SetResult(BrowserRetainedV2OperationsTests.Status(acknowledgement)); await executing;
        Assert.False(gate.IsBusy); Assert.Equal(1, executions);
        var status = Context(BrowserRetainedV2OperationsTests.Bound()); await BrowserRetainedRoutes.InvokeV2Async(status, operations, "status");
        Assert.Equal(acknowledgement, operations.GetV2Status(BrowserRetainedV2OperationsTests.Bound()).Stage);
        var late = Context(BrowserRetainedV2OperationsTests.Bound()); await BrowserRetainedRoutes.InvokeV2Async(late, operations, "cancel");
        Assert.Equal(acknowledgement, operations.GetV2Status(BrowserRetainedV2OperationsTests.Bound()).Stage); Assert.Equal(2, cancellations);
        Assert.DoesNotContain("\"stage\":\"completed\"", Output(late));
    }

    [Fact]
    public async Task V2TransportAndVersionFailuresRemainSanitizedAndNeverFallbackToV1()
    {
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException(),
            BrowserRetainedV2OperationsTests.Owner(execute: (_, _) => throw new LocalApiException("operation_version_mismatch", "private owner key path")));
        var context = Context(BrowserRetainedV2OperationsTests.Bound()); await BrowserRetainedRoutes.InvokeV2Async(context, operations, "execute");
        Assert.Equal(409, context.Response.StatusCode); Assert.Contains("operation_version_mismatch", Output(context));
        Assert.DoesNotContain("private", Output(context)); Assert.DoesNotContain("dispatched", Output(context));
        var legacy = Context(BrowserRetainedV2OperationsTests.Bound()); await BrowserRetainedRoutes.InvokeAsync(legacy, operations, "status");
        Assert.Equal(400, legacy.Response.StatusCode); Assert.DoesNotContain("dispatched", Output(legacy));
        var type = Context(BrowserRetainedV2OperationsTests.Bound(), "application/octet-stream"); await BrowserRetainedRoutes.InvokeV2Async(type, operations, "execute");
        Assert.Equal(415, type.Response.StatusCode); Assert.Equal(0, type.Request.Body.Position);
        var large = Context(new byte[4097]); await BrowserRetainedRoutes.InvokeV2Async(large, operations, "execute");
        Assert.Equal(413, large.Response.StatusCode); Assert.Equal(0, large.Request.Body.Position);
    }

    private static void CheckMetadata(ReadOnlyMemory<byte> body)
    {
        using var request = JsonDocument.Parse(body);
        Assert.Equal(["contractVersion", "operationId"], request.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(LocalApiV2Contract.Version, request.RootElement.GetProperty("contractVersion").GetString());
    }
}
