using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserArtifactCommandRequest(string RequestId, string Action, string ArtifactId, string Sha256, long SizeBytes,
    string? Serial = null, string? Verification = null)
{
    public OperatorCommand ToOperatorCommand(BrowserArtifactLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Receipt.Id != ArtifactId || lease.Receipt.Sha256 != Sha256 || lease.Receipt.SizeBytes != SizeBytes)
            throw new BrowserRequestException("artifact_binding_mismatch");
        return (Action, Verification) switch
        {
            ("apk.inspect", null) when Serial is null => OperatorCommands.InspectApk(lease.Path),
            ("apk.preflight", null) when Serial is not null => OperatorCommands.PreflightInspectedApp(Serial, lease.Path),
            ("runtime.observe", "exact") when Serial is not null => OperatorCommands.ObserveInspectedApp(Serial, lease.Path),
            ("runtime.observe", "development-metadata") when Serial is not null => OperatorCommands.ObserveDevelopmentApp(Serial, lease.Path, null),
            _ => throw new BrowserRequestException("artifact_binding_mismatch")
        };
    }
}

/// <summary>Read-only APK facts bound to process-owned staged bytes, never a caller path.</summary>
public static class BrowserArtifactCommands
{
    public const long MaximumArtifactBytes = BrowserArtifactStore.MaximumFileBytes;
    private static readonly string[] Fields = ["requestId", "action", "artifactId", "sha256", "sizeBytes"];

    public static bool IsArtifactAction(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString() is "apk.inspect" or "apk.preflight" or "runtime.observe";
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
            var action = String(root, "action");
            var expected = new HashSet<string>(Fields, StringComparer.Ordinal);
            if (action is "apk.preflight" or "runtime.observe") expected.Add("serial");
            if (action == "runtime.observe") expected.Add("verification");
            if (!names.SetEquals(expected)) throw new BrowserRequestException("fields_invalid");
            var requestId = String(root, "requestId");
            if (!Guid.TryParseExact(requestId, "D", out var id) || id == Guid.Empty) throw new BrowserRequestException("request_id_invalid");
            if (action is not ("apk.inspect" or "apk.preflight" or "runtime.observe")) throw new BrowserRequestException("action_unsupported");
            var artifactId = RequireArtifactId(String(root, "artifactId"));
            var hash = RequireHash(String(root, "sha256"));
            if (root.GetProperty("sizeBytes").ValueKind != JsonValueKind.Number || !root.GetProperty("sizeBytes").TryGetInt64(out var size) ||
                size is < 0 or > MaximumArtifactBytes) throw new BrowserRequestException("artifact_size_invalid");
            var serial = action == "apk.inspect" ? null : AndroidInput.RequireSerial(String(root, "serial"));
            var verification = action == "runtime.observe" ? String(root, "verification") : null;
            if (action == "runtime.observe" && verification is not ("exact" or "development-metadata"))
                throw new BrowserRequestException("verification_invalid");
            return new(id.ToString("D"), action, artifactId, hash, size, serial, verification);
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
