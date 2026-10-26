using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserTransferRequest(string RequestId, string Action, string Serial, string FileName,
    string? RemotePath = null, string? PackageName = null);

public static class BrowserTransferCommands
{
    public static bool IsTransferAction(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String &&
                action.GetString() is "files.pull" or "apk.export";
        }
        catch (JsonException) { return false; }
    }

    public static BrowserTransferRequest Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) throw new BrowserRequestException("body_size_invalid", 413);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new BrowserRequestException("request_invalid");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!fields.Add(property.Name)) throw new BrowserRequestException("duplicate_field");
            var id = Text(root, "requestId");
            if (!Guid.TryParseExact(id, "D", out var requestId) || requestId == Guid.Empty)
                throw new BrowserRequestException("request_id_invalid");
            var action = Text(root, "action");
            string[] allowed = action switch
            {
                "files.pull" => ["requestId", "action", "serial", "fileName", "remotePath"],
                "apk.export" => ["requestId", "action", "serial", "fileName", "package"],
                _ => throw new BrowserRequestException("action_unsupported")
            };
            if (!fields.SetEquals(allowed)) throw new BrowserRequestException("fields_invalid");
            var serial = AndroidInput.RequireSerial(Text(root, "serial"));
            var fileName = BrowserArtifactStore.ValidateFileName(Text(root, "fileName"));
            if (action == "apk.export" && !fileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                throw new BrowserRequestException("export_name_invalid");
            return new(requestId.ToString("D"), action, serial, fileName,
                action == "files.pull" ? AndroidInput.RequireRemotePath(Text(root, "remotePath")) : null,
                action == "apk.export" ? AndroidInput.RequirePackageName(Text(root, "package")) : null);
        }
        catch (BrowserRequestException) { throw; }
        catch (BrowserArtifactException exception) { throw new BrowserRequestException(exception.Code); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { throw new BrowserRequestException("request_invalid"); }
    }

    private static string Text(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.String &&
        root.GetProperty(name).GetString() is { Length: > 0 and <= 256 } value ? value : throw new BrowserRequestException("field_invalid");
}
