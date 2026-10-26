using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.BrowserHost;

/// <summary>Closed browser projection of the existing retained owner; frontdoor authentication stays in Program.</summary>
public static class BrowserRetainedRoutes
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void Map(WebApplication app, BrowserRetainedOperations operations)
    {
        foreach (var action in new[] { "preflight", "execute", "status", "cancel" })
        {
            var routeAction = action;
            app.MapPost("/browser/v1/retained/" + routeAction, (HttpContext context) =>
                InvokeAsync(context, operations, routeAction, app.Lifetime.ApplicationStopping));
            app.MapPost("/browser/v2/retained/" + routeAction, (HttpContext context) =>
                InvokeV2Async(context, operations, routeAction, app.Lifetime.ApplicationStopping));
        }
    }

    internal static async Task InvokeAsync(HttpContext context, BrowserRetainedOperations operations, string action,
        CancellationToken stopping = default) => await InvokeVersionAsync(context, operations, action, false, stopping);

    internal static async Task InvokeV2Async(HttpContext context, BrowserRetainedOperations operations, string action,
        CancellationToken stopping = default) => await InvokeVersionAsync(context, operations, action, true, stopping);

    private static async Task InvokeVersionAsync(HttpContext context, BrowserRetainedOperations operations, string action, bool v2,
        CancellationToken stopping = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (!string.Equals(context.Request.ContentType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new BrowserRetainedException("content_type_invalid", 415, false);
            if (context.Request.ContentLength > BrowserCommands.MaximumRequestBytes)
                throw new BrowserRetainedException("body_size_invalid", 413, false);
            var body = await ReadBodyAsync(context);
            object result = action switch
            {
                "preflight" => v2 ? await operations.PreflightV2Async(body, context.RequestAborted) : await operations.PreflightAsync(body, context.RequestAborted),
                // Once admitted, only owner lifetime and explicit retained cancellation affect execution.
                "execute" => v2 ? await operations.ExecuteV2Async(body, stopping) : await operations.ExecuteAsync(body, stopping),
                "status" => v2 ? operations.GetV2Status(body) : operations.GetStatus(body),
                "cancel" => v2 ? operations.CancelV2(body) : operations.Cancel(body),
                _ => throw new BrowserRetainedException("command_not_allowed", 404, false)
            };
            await context.Response.WriteAsJsonAsync(result, Json, CancellationToken.None);
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            var error = BrowserRetainedOperations.Translate(exception);
            context.Response.StatusCode = error.StatusCode;
            // Omit dispatched entirely when the original may be consumed/running/unknown.
            var response = new Dictionary<string, object?> { ["error"] = error.Code };
            if (error.Dispatched is not null) response["dispatched"] = error.Dispatched;
            if (error.Code is "state_in_use" or "retained_owner_unavailable" or "retained_configuration_missing" or
                "retained_configuration_invalid" or "retained_platform_unsupported")
                response["alternative"] = operations.Capability.Alternative;
            await context.Response.WriteAsJsonAsync(response, Json, CancellationToken.None);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var body = new MemoryStream(); var buffer = new byte[1024]; int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (count > BrowserCommands.MaximumRequestBytes - body.Length)
                    throw new BrowserRetainedException("body_size_invalid", 413, false);
                body.Write(buffer, 0, count);
            }
            return body.ToArray();
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        { throw new BrowserRetainedException("body_size_invalid", 413, false); }
        catch (OperationCanceledException) { throw new BrowserRetainedException("request_body_timeout", 408, false); }
    }
}
