using System.Security.Cryptography;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

/// <summary>Bounded private stdin transport. Existing Program origin/bearer/session middleware must run first.</summary>
public static class BrowserProfileRoutes
{
    private static readonly HashSet<string> Headers = new(["X-Qfm-Session", "X-Qfm-Request-Id", "X-Qfm-Profile-Device-Id",
        "X-Qfm-Confirm-Profile-Write", "X-Qfm-Replace-Existing"], StringComparer.OrdinalIgnoreCase);
    public static void Map(WebApplication app, BrowserCommands commands, BrowserProfiles profiles) =>
        app.MapPost("/browser/v1/profiles/import", (HttpContext context) => ImportAsync(context, commands, profiles, app.Lifetime.ApplicationStopping));

    internal static async Task ImportAsync(HttpContext context, BrowserCommands commands, BrowserProfiles profiles,
        CancellationToken stopping = default, TimeSpan? receiveTimeout = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        byte[]? document = null; BrowserCommandOutcome? outcome = null;
        Dictionary<string, object>? error = null; var status = 200;
        try
        {
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.StartsWith("X-Qfm-", StringComparison.OrdinalIgnoreCase) && !Headers.Contains(header.Key) ||
                    (Headers.Contains(header.Key) || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                     header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)) && header.Value.Count != 1)
                    throw new BrowserRequestException("profile_header_invalid");
            }
            commands.RequireSession(Header(context, "X-Qfm-Session"));
            if (!string.Equals(context.Request.ContentType, "application/json", StringComparison.OrdinalIgnoreCase)) throw new BrowserRequestException("content_type_invalid", 415);
            if (!string.IsNullOrEmpty(context.Request.Headers.ContentEncoding.ToString())) throw new BrowserRequestException("content_encoding_invalid", 415);
            if (context.Request.ContentLength is <= 0 or > QuestConnectivityProfileManagementContract.MaximumPrivateInputBytes)
                throw new BrowserRequestException("body_size_invalid", 413);
            var id = BrowserProfileCommands.Id(Header(context, "X-Qfm-Request-Id"));
            var device = BrowserProfileCommands.DeviceId(Header(context, "X-Qfm-Profile-Device-Id"));
            if (Header(context, "X-Qfm-Confirm-Profile-Write") != "true") throw new BrowserRequestException("confirmation_required");
            var replace = Header(context, "X-Qfm-Replace-Existing") switch
            { "true" => true, "false" => false, _ => throw new BrowserRequestException("profile_replace_flag_invalid") };
            var request = new BrowserProfileRequest(id, "profile.import", device, replace, true);
            commands.CheckProfileAdmission(request);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping))
            {
                deadline.CancelAfter(receiveTimeout ?? TimeSpan.FromSeconds(5));
                document = await profiles.ReadPrivateAsync(context.Request.Body, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (context.Request.ContentLength is { } declared && declared != document.Length) throw new BrowserRequestException("body_size_invalid", 400);
            }
            // This helper owns/clears the received array before completing its
            // drain/gate signal. After admission no HTTP/lifetime token enters
            // Core, and all effect failures become cached sanitized outcomes.
            outcome = await commands.ExecuteProfileImportAsync(request, document).ConfigureAwait(false);
        }
        catch (BrowserRequestException exception)
        {
            status = exception.StatusCode; error = new() { ["error"] = exception.Code };
            if (exception.Dispatched is { } dispatched) error["dispatched"] = dispatched;
            if (exception.Code == "windows_credential_manager_required") error["alternative"] = profiles.Capability.Alternative;
        }
        catch (QuestConnectivityProfileManagementException exception)
        {
            var code = exception.Code is "profileInputSizeInvalid" or "profileSchemaInvalid" or "profileDeviceIdInvalid" or
                "profileTargetInvalid" or "profilePairingCodeInvalid" or "profileDocumentInvalid" ? exception.Code : "profileDocumentInvalid";
            status = code == "profileInputSizeInvalid" ? 413 : 400;
            error = new() { ["error"] = code, ["dispatched"] = false };
        }
        catch (OperationCanceledException) { status = 408; error = new() { ["error"] = "request_body_timeout", ["dispatched"] = false }; }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        { status = 413; error = new() { ["error"] = "body_size_invalid", ["dispatched"] = false }; }
        catch (IOException) { status = 400; error = new() { ["error"] = "profileDocumentInvalid", ["dispatched"] = false }; }
        finally { if (document is not null) CryptographicOperations.ZeroMemory(document); }

        // Keep response transport outside the preadmission catch region: a
        // dropped response after a write can never become dispatched:false.
        context.Response.StatusCode = status;
        if (error is not null) await context.Response.WriteAsJsonAsync(error, CancellationToken.None).ConfigureAwait(false);
        else await context.Response.WriteAsJsonAsync(outcome!, CancellationToken.None).ConfigureAwait(false);
    }
    private static string Header(HttpContext context, string name) => context.Request.Headers[name] is { Count: 1 } values &&
        values[0] is { Length: > 0 } value && !value.Contains(',') ? value : throw new BrowserRequestException("profile_header_invalid");
}
