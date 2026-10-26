using System.Globalization;
using Microsoft.AspNetCore.Http.Features;

namespace QuestIonAbleFileManager.BrowserHost;

/// <summary>Local-only byte transport. Program's origin, bearer and session middleware must run first.</summary>
public static class BrowserArtifactRoutes
{
    public const string HashHeader = "X-Qfm-Artifact-Sha256";
    public const string SizeHeader = "X-Qfm-Artifact-Size-Bytes";
    public const string NameHeader = "X-Qfm-File-Name";

    public static void Map(WebApplication app, Func<BrowserArtifactStore> store)
    {
        app.MapPost("/browser/v1/artifacts", (HttpContext context) => UploadAsync(context, store));
        app.MapGet("/browser/v1/artifacts/{id}", (HttpContext context, string id) => DownloadAsync(context, id, store));
        app.MapDelete("/browser/v1/artifacts/{id}", (HttpContext context, string id) => RemoveAsync(context, id, store));
        app.MapPost("/browser/v1/artifacts/cleanup", (HttpContext context) => CleanupAsync(context, store));
    }

    internal static Task UploadAsync(HttpContext context, Func<BrowserArtifactStore> store) => HandleAsync(context, async cancellation =>
    {
        if (!string.Equals(context.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            throw new RouteException("content_type_invalid", 415);
        if (context.Request.ContentLength is < 0 or > BrowserArtifactCommands.MaximumArtifactBytes)
            throw new RouteException("artifact_size_limit", 413);
        var fileName = BrowserArtifactStore.ValidateFileName(Header(context, NameHeader));
        var hash = OptionalHeader(context, HashHeader);
        if (hash is not null) BrowserArtifactCommands.RequireHash(hash);
        EnsureSupported();
        // The host default remains 4 KiB. Only this authenticated upload endpoint widens it.
        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is not null)
        {
            if (bodyLimit.IsReadOnly) throw new RouteException("artifact_body_already_started", 409);
            bodyLimit.MaxRequestBodySize = BrowserArtifactCommands.MaximumArtifactBytes;
        }
        var receipt = await store().StageAsync(fileName, context.Request.Body, context.Request.ContentLength, hash, cancellation);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new { state = "staged", scope = "localOnly", artifact = receipt, deviceDispatched = false }, cancellation);
    });

    internal static Task DownloadAsync(HttpContext context, string id, Func<BrowserArtifactStore> store) => HandleAsync(context, async cancellation =>
    {
        BrowserArtifactCommands.RequireArtifactId(id);
        var hash = BrowserArtifactCommands.RequireHash(Header(context, HashHeader));
        if (!long.TryParse(Header(context, SizeHeader), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size is < 0 or > BrowserArtifactCommands.MaximumArtifactBytes) throw new RouteException("artifact_size_invalid", 400);
        EnsureSupported();
        await using var lease = await store().AcquireAsync(id, hash, size, cancellation);
        var fileName = BrowserArtifactStore.ValidateFileName(lease.Receipt.FileName);
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = lease.Receipt.SizeBytes;
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        context.Response.Headers[HashHeader] = lease.Receipt.Sha256;
        context.Response.Headers[SizeHeader] = lease.Receipt.SizeBytes.ToString(CultureInfo.InvariantCulture);
        // Copy exactly the validated receipt size. Never turn a local path into a file result.
        var buffer = new byte[64 * 1024];
        var remaining = lease.Receipt.SizeBytes;
        while (remaining > 0)
        {
            var count = await lease.Stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellation);
            if (count == 0) throw new BrowserArtifactException("artifact_integrity_mismatch");
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), cancellation);
            remaining -= count;
        }
    });

    internal static Task RemoveAsync(HttpContext context, string id, Func<BrowserArtifactStore> store) => HandleAsync(context, async cancellation =>
    {
        BrowserArtifactCommands.RequireArtifactId(id); EnsureEmptyBody(context); EnsureSupported();
        await store().RemoveAsync(id, cancellation);
        await context.Response.WriteAsJsonAsync(new { state = "removed", scope = "localOnly", artifactId = id, deviceDispatched = false }, cancellation);
    });

    internal static Task CleanupAsync(HttpContext context, Func<BrowserArtifactStore> store) => HandleAsync(context, async cancellation =>
    {
        EnsureEmptyBody(context); EnsureSupported();
        var removed = await store().CleanupAsync(cancellation);
        await context.Response.WriteAsJsonAsync(new { state = "cleaned", scope = "localOnly", removedCount = removed, deviceDispatched = false }, cancellation);
    });

    private static async Task HandleAsync(HttpContext context, Func<CancellationToken, Task> operation)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(BrowserArtifactStore.TransferTimeoutSeconds));
        try { await operation(deadline.Token); }
        catch (Exception exception)
        {
            if (context.Response.HasStarted) { context.Abort(); return; }
            var (code, status) = exception switch
            {
                RouteException route => (route.Code, route.StatusCode),
                BrowserRequestException request when request.Code is "artifact_id_invalid" or "artifact_hash_invalid" => (request.Code, 400),
                BrowserArtifactException artifact => artifact.Code switch
                {
                    "artifact_name_invalid" or "artifact_hash_invalid" or "artifact_size_invalid" => (artifact.Code, 400),
                    "artifact_not_found" => (artifact.Code, 404),
                    "artifact_integrity_mismatch" or "artifact_identity_changed" or "artifact_in_use" => (artifact.Code, 409),
                    "artifact_size_limit" or "artifact_file_capacity" or "artifact_byte_capacity" => (artifact.Code, 413),
                    _ => ("artifact_staging_unavailable", 503)
                },
                BadHttpRequestException bad when bad.StatusCode == 413 => ("artifact_size_limit", 413),
                OperationCanceledException => ("artifact_operation_timeout", 408),
                PlatformNotSupportedException => ("native_staging_unavailable", 501),
                _ => ("artifact_operation_failed", 500)
            };
            context.Response.ContentLength = null;
            context.Response.Headers.Remove("Content-Disposition");
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { error = code, deviceDispatched = false }, CancellationToken.None);
        }
    }

    private static void EnsureSupported()
    {
        if (!BrowserArtifactStore.Capability.Supported) throw new RouteException("native_staging_unavailable", 501);
    }
    private static string Header(HttpContext context, string name) => OptionalHeader(context, name) ?? throw new RouteException("artifact_header_invalid", 400);
    private static string? OptionalHeader(HttpContext context, string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count == 0) return null;
        if (values.Count != 1 || string.IsNullOrEmpty(values[0])) throw new RouteException("artifact_header_invalid", 400);
        return values[0];
    }
    private static void EnsureEmptyBody(HttpContext context)
    {
        if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
            throw new RouteException("artifact_body_invalid", 400);
    }
    private sealed class RouteException(string code, int statusCode) : Exception
    { internal string Code { get; } = code; internal int StatusCode { get; } = statusCode; }
}
