using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

if (!OperatorPlatformCapabilities.Current.HostAdbSupported || args.Length != 2 || args[0] != "--listen")
{
    Console.Error.WriteLine("Usage: questionable-file-manager-browser --listen http://127.0.0.1:<port>/");
    return 2;
}
BrowserSecurity security;
try
{
    security = new(args[1], Environment.GetEnvironmentVariable(BrowserSecurity.CredentialVariable) ?? "");
}
catch (Exception exception) when (exception is BrowserRequestException or LocalApiException)
{
    Console.Error.WriteLine("Explicit numeric loopback listener and private browser bearer configuration required.");
    return 2;
}
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = [], WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Parse(security.ListenUri.Host.Trim('[', ']')), security.ListenUri.Port);
    options.Limits.MaxRequestBodySize = BrowserCommands.MaximumRequestBytes;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
// Resolve the selected host ADB only for an explicit operation, never page load/startup.
var artifacts = new Lazy<BrowserArtifactStore>(() => new BrowserArtifactStore());
var operationGate = new BrowserOperationGate();
var retained = new BrowserRetainedOperations(() => artifacts.Value, operationGate.TryAcquire);
var transfers = new BrowserTransfers(() => artifacts.Value);
var directConnections = new BrowserDirectConnections();
// Local profile operations never create an ADB client or activate Fleet.
var profileExecutor = new Lazy<OperatorCommandExecutor>(() => new(
    client: null,
    fleetInstallerFactory: () => throw new InvalidOperationException("profile_fleet_activation_forbidden"),
    connectivityProfilesFactory: QuestConnectivityProfileManager.CreateWindows));
var profiles = new BrowserProfiles(
    (command, input, cancellation) => profileExecutor.Value.ExecuteAsync(command, cancellation, privateInput: input),
    new WindowsPrivateProfileInputReader(), OperatorPlatformCapabilities.Current.ConnectivityProfilesSupported);
var commands = new BrowserCommands(async (command, cancellation) =>
{
    var executor = new OperatorCommandExecutor(AdbClient.CreateDefault());
    return await BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation);
}, () => artifacts.Value, operationGate, BrowserArtifactStore.Capability.Supported ? transfers.ExecuteAsync : null,
    directConnections, profiles);
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; worker-src 'self'; manifest-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    var staticNavigation = BrowserSecurity.IsStaticNavigation(context.Request.Method,
        context.Request.Path.Value ?? "", context.Request.Headers["Sec-Fetch-Mode"].ToString(),
        context.Request.Headers["Sec-Fetch-Dest"].ToString());
    if (!security.AllowsRequest(context.Request.Host.Value ?? "", context.Request.Headers.Origin.ToString(),
            context.Request.Headers["Sec-Fetch-Site"].ToString(), context.Connection.RemoteIpAddress, staticNavigation) ||
        context.Request.QueryString.HasValue)
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { error = "origin_or_host_rejected", dispatched = false });
        return;
    }
    if (context.Request.Path.StartsWithSegments("/browser"))
    {
        if (!security.Authenticate(context.Request.Headers.Authorization.ToString()))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized", dispatched = false });
            return;
        }
        if (context.Request.Path != "/browser/v1/session")
        {
            try { commands.RequireSession(context.Request.Headers["X-Qfm-Session"].ToString()); }
            catch (BrowserRequestException exception)
            {
                context.Response.StatusCode = exception.StatusCode;
                await context.Response.WriteAsJsonAsync(RequestError(exception));
                return;
            }
        }
    }
    else if (context.Request.Method != "GET" || !BrowserSecurity.IsStaticPath(context.Request.Path.Value ?? ""))
    {
        context.Response.StatusCode = 404;
        return;
    }
    try { await next(context); }
    catch (Exception)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "request_failed" });
    }
});
// Serve a worker bound to the exact shipped shell bytes, before ordinary static files.
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/sw.js")
    {
        context.Response.ContentType = "text/javascript; charset=utf-8";
        await context.Response.WriteAsync(BrowserShellAssets.RenderWorker(app.Environment.WebRootPath));
        return;
    }
    await next(context);
});
app.UseDefaultFiles();
var staticTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions { ServeUnknownFileTypes = true, DefaultContentType = "text/plain", ContentTypeProvider = staticTypes });
app.MapGet("/browser/v1/session", () => new
{
    contractVersion = BrowserCommands.ContractVersion, sessionId = commands.SessionId,
    actions = commands.AvailableActions, maxRequests = BrowserCommands.MaximumRequests,
    platformCapabilities = OperatorPlatformCapabilities.Current,
    artifactStaging = BrowserArtifactStore.Capability,
    retainedOperations = retained.Capability,
    connectivityProfiles = profiles.Capability
});
BrowserArtifactRoutes.Map(app, () => artifacts.Value);
BrowserRetainedRoutes.Map(app, retained);
BrowserProfileRoutes.Map(app, commands, profiles);
app.MapGet("/browser/v1/requests/{requestId}", (string requestId) =>
{
    try { return Results.Json(commands.Get(requestId)); }
    catch (BrowserRequestException exception) { return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode); }
});
app.MapPost("/browser/v1/execute", async (HttpContext context) =>
{
    if (!string.Equals(context.Request.ContentType, "application/json", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { error = "content_type_invalid", dispatched = false }, statusCode: 415);
    try
    {
        if (context.Request.ContentLength > BrowserCommands.MaximumRequestBytes)
            throw new BrowserRequestException("body_size_invalid", 413);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int count;
        while ((count = await context.Request.Body.ReadAsync(chunk, deadline.Token)) != 0)
        {
            if (buffer.Length + count > BrowserCommands.MaximumRequestBytes)
                throw new BrowserRequestException("body_size_invalid", 413);
            buffer.Write(chunk, 0, count);
        }
        // A disconnected page cannot abort a potentially dispatched mutation.
        return Results.Json(await commands.ExecuteAsync(buffer.ToArray(), app.Lifetime.ApplicationStopping));
    }
    catch (BrowserRequestException exception)
    { return Results.Json(RequestError(exception), statusCode: exception.StatusCode); }
    catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
    { return Results.Json(new { error = "body_size_invalid", dispatched = false }, statusCode: 413); }
    catch (OperationCanceledException)
    { return Results.Json(new { error = "request_body_timeout", dispatched = false }, statusCode: 408); }
});
var shutdownCode = 0;
try { await app.RunAsync(); }
finally
{
    // An admitted private write retains its bytes/gate through real verification
    // and rollback; cooperative cancellation is not an acknowledgement.
    await commands.DrainProfilesAsync();
    try
    {
        if (!await directConnections.ShutdownAsync())
        { Console.Error.WriteLine("direct_connection_cleanup_incomplete"); shutdownCode = 3; }
    }
    catch (Exception)
    { Console.Error.WriteLine("direct_connection_cleanup_incomplete"); shutdownCode = 3; }
    try { retained.Dispose(); }
    catch (BrowserRetainedException)
    { Console.Error.WriteLine("retained_owner_cleanup_incomplete"); shutdownCode = 3; }
    if (artifacts.IsValueCreated)
    {
        try { artifacts.Value.Dispose(); }
        catch (Exception exception) when (exception is BrowserArtifactException or IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("artifact_cleanup_incomplete"); shutdownCode = 3; }
    }
}
return shutdownCode;

static Dictionary<string, object> RequestError(BrowserRequestException exception)
{
    var response = new Dictionary<string, object> { ["error"] = exception.Code };
    if (exception.Dispatched is { } dispatched) response["dispatched"] = dispatched;
    return response;
}
