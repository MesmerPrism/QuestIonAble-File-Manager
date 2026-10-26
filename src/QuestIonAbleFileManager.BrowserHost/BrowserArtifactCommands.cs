using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserArtifactCommandRequest(string RequestId, string Action, string ArtifactId, string Sha256, long SizeBytes)
{
    public OperatorCommand ToOperatorCommand(BrowserArtifactLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (Action != "apk.inspect" || lease.Receipt.Id != ArtifactId || lease.Receipt.Sha256 != Sha256 || lease.Receipt.SizeBytes != SizeBytes)
            throw new BrowserRequestException("artifact_binding_mismatch");
        return OperatorCommands.InspectApk(lease.Path);
    }
}

/// <summary>Read-only APK inspection bound to process-owned staged bytes, never a caller path.</summary>
public static class BrowserArtifactCommands
{
    public const long MaximumArtifactBytes = 256L * 1024 * 1024;
    private static readonly string[] Fields = ["requestId", "action", "artifactId", "sha256", "sizeBytes"];

    public static bool IsArtifactAction(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString() == "apk.inspect";
        }
        catch (JsonException) { return false; }
    }

    public static BrowserArtifactCommandRequest Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) throw new BrowserRequestException("body_size_invalid", 413);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new BrowserRequestException("request_invalid");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) throw new BrowserRequestException("duplicate_field");
            if (!names.SetEquals(Fields)) throw new BrowserRequestException("fields_invalid");
            var requestId = String(root, "requestId");
            if (!Guid.TryParseExact(requestId, "D", out var id) || id == Guid.Empty) throw new BrowserRequestException("request_id_invalid");
            var action = String(root, "action");
            if (action != "apk.inspect") throw new BrowserRequestException("action_unsupported");
            var artifactId = RequireArtifactId(String(root, "artifactId"));
            var hash = RequireHash(String(root, "sha256"));
            if (root.GetProperty("sizeBytes").ValueKind != JsonValueKind.Number || !root.GetProperty("sizeBytes").TryGetInt64(out var size) ||
                size is < 0 or > MaximumArtifactBytes) throw new BrowserRequestException("artifact_size_invalid");
            return new(id.ToString("D"), action, artifactId, hash, size);
        }
        catch (BrowserRequestException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { throw new BrowserRequestException("request_invalid"); }
    }

    internal static string RequireArtifactId(string value)
    {
        if (value.Length != 32 || !Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty || value != id.ToString("N"))
            throw new BrowserRequestException("artifact_id_invalid");
        return value;
    }

    internal static string RequireHash(string value)
    {
        if (value.Length != 64 || value.Any(static character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
            throw new BrowserRequestException("artifact_hash_invalid");
        return value;
    }

    private static string String(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String && root.GetProperty(name).GetString() is { Length: > 0 and <= 256 } value
            ? value : throw new BrowserRequestException("field_invalid");
}
