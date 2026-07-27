using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using QuestIonAbleFileManager.Core;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The dedicated local API secure state boundary requires Windows.");
    return 2;
}

if (args.Length != 2 || args[0] != "--listen")
{
    Console.Error.WriteLine(
        "Usage: questionable-file-manager-api --listen http://<loopback-ip>:<port>/");
    return 2;
}

Uri listenUri;
string credential;
try
{
    listenUri = LocalApiSecurity.RequireExplicitLoopback(args[1]);
    credential = LocalApiSecurity.ReadCredentialFromEnvironment();
}
catch (LocalApiException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

var builder = WebApplication.CreateSlimBuilder(args: []);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
    options.Listen(IPAddress.Parse(listenUri.DnsSafeHost), listenUri.Port));
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = LocalApiContract.MaximumRequestBytes;
    options.ValueLengthLimit = LocalApiContract.MaximumRequestBytes;
});

var client = AdbClient.CreateDefault();
using var registry = new LocalApiCommandRegistry(
    client,
    LocalApiStateSettings.FromEnvironment());
var app = builder.Build();

app.Use(async (context, next) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!LocalApiSecurity.AuthenticateBearer(credential, header))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            contractVersion = LocalApiContract.Version,
            error = "unauthorized"
        });
        return;
    }
    await next(context);
});

app.MapGet("/v1/capabilities", () => Results.Json(registry.GetCapabilities()));
app.MapPost("/v1/preflight", (Func<HttpContext, Task<IResult>>)(async context =>
    await InvokeAsync(context, body => registry.PreflightAsync(body, context.RequestAborted))));
app.MapPost("/v1/execute", (Func<HttpContext, Task<IResult>>)(async context =>
    await InvokeAsync(context, body => registry.ExecuteAsync(body, context.RequestAborted))));
app.MapPost("/v1/status", (Func<HttpContext, Task<IResult>>)(async context =>
    await InvokeSyncAsync(context, registry.GetStatus)));
app.MapPost("/v1/cancel", (Func<HttpContext, Task<IResult>>)(async context =>
    await InvokeSyncAsync(context, registry.Cancel)));

await app.RunAsync();
return 0;

static async Task<IResult> InvokeAsync<T>(
    HttpContext context,
    Func<ReadOnlyMemory<byte>, Task<T>> action)
{
    try
    {
        var body = await ReadBoundedBodyAsync(context.Request, context.RequestAborted);
        return Results.Json(await action(body));
    }
    catch (LocalApiException exception)
    {
        return Results.Json(new
        {
            contractVersion = LocalApiContract.Version,
            error = exception.Code,
            message = exception.Message
        }, statusCode: StatusCodes.Status400BadRequest);
    }
}

static async Task<IResult> InvokeSyncAsync<T>(
    HttpContext context,
    Func<ReadOnlyMemory<byte>, T> action)
{
    try
    {
        var body = await ReadBoundedBodyAsync(context.Request, context.RequestAborted);
        return Results.Json(action(body));
    }
    catch (LocalApiException exception)
    {
        return Results.Json(new
        {
            contractVersion = LocalApiContract.Version,
            error = exception.Code,
            message = exception.Message
        }, statusCode: StatusCodes.Status400BadRequest);
    }
}

static async Task<ReadOnlyMemory<byte>> ReadBoundedBodyAsync(
    HttpRequest request,
    CancellationToken cancellationToken)
{
    if (request.ContentLength is > LocalApiContract.MaximumRequestBytes)
    {
        throw new LocalApiException("request_size_invalid", "The request body exceeds the allowed size.");
    }
    using var memory = new MemoryStream();
    var buffer = new byte[4096];
    while (true)
    {
        var count = await request.Body.ReadAsync(buffer, cancellationToken);
        if (count == 0) break;
        if (memory.Length + count > LocalApiContract.MaximumRequestBytes)
        {
            throw new LocalApiException("request_size_invalid", "The request body exceeds the allowed size.");
        }
        memory.Write(buffer, 0, count);
    }
    return memory.ToArray();
}
