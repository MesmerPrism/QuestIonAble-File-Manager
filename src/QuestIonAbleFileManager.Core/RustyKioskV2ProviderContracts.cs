using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public static class RustyKioskV2ProviderContract
{
    public const string RequestSchema = "questionable.file_manager.fleet_kiosk_v2_catalog_request.v1";
    public const string ResponseSchema = "questionable.file_manager.fleet_kiosk_v2_catalog_response.v1";
    public const string CapabilityId = "rusty-kiosk.direct-operator";
    public const string RouteId = "kiosk.encrypted.v2";
    public const string CatalogSummaryScope = "kiosk.catalog-summary";
    public const string CatalogDetailScope = "kiosk.catalog-detail";
    public const string AppLaunchScope = "kiosk.launch-catalog-entry-normal";
    public const string VerifiedStatus = "verified";
    public const string FailedStatus = "failed";
    public const string RejectedStatus = "rejected";
    public const string UnavailableStatus = "unavailable";
    public const int MaximumRequestBytes = 64 * 1024;
    public const int MaximumRequestEnvelopeBytes = 1024 * 1024;
    public const int MaximumResponseEnvelopeBytes = 2 * 1024 * 1024;
    public const int MaximumOwnerCatalogBytes = 768 * 1024;
    public const int MaximumOwnerGrantReceiptBytes = 64 * 1024;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan MaximumExchangeLifetime = TimeSpan.FromSeconds(30);

    public static int ExitCodeForStatus(string status) =>
        status switch
        {
            VerifiedStatus => 0,
            FailedStatus => 1,
            RejectedStatus => 2,
            UnavailableStatus => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
}

public sealed record RustyKioskV2CatalogProviderRequest(
    string Schema,
    string ProfileId,
    string RequestId,
    string DeviceId,
    ulong IdentityRevision,
    string CapabilityId,
    ulong CapabilityEvidenceRevision,
    string RouteId,
    string? ExpectedOwnerEpoch,
    IReadOnlyList<string> Scopes,
    long IssuedAtMs,
    long ExpiresAtMs)
{
    public static RustyKioskV2CatalogProviderRequest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > RustyKioskV2ProviderContract.MaximumRequestBytes)
        {
            throw RustyKioskV2ProviderException.Rejected("request_size_invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(
                utf8Json.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 5
                });
            var root = document.RootElement;
            StrictJson.RequireObjectFields(
                root,
                [
                    "schema",
                    "profile_id",
                    "request_id",
                    "device_id",
                    "identity_revision",
                    "capability_id",
                    "capability_evidence_revision",
                    "route_id",
                    "expected_owner_epoch",
                    "scopes",
                    "issued_at_ms",
                    "expires_at_ms"
                ],
                [
                    "schema",
                    "profile_id",
                    "request_id",
                    "device_id",
                    "identity_revision",
                    "capability_id",
                    "capability_evidence_revision",
                    "route_id",
                    "scopes",
                    "issued_at_ms",
                    "expires_at_ms"
                ]);

            var scopes = StrictJson.RequiredStringArray(root, "scopes", 1, 1, 96);
            if (scopes.Count != 1 ||
                !string.Equals(
                    scopes[0],
                    RustyKioskV2ProviderContract.CatalogSummaryScope,
                    StringComparison.Ordinal))
            {
                throw RustyKioskV2ProviderException.Rejected("scope_not_read_only_catalog");
            }

            var request = new RustyKioskV2CatalogProviderRequest(
                StrictJson.RequiredString(root, "schema", 128),
                StrictJson.RequiredToken(root, "profile_id", 8, 128),
                StrictJson.RequiredKioskRequestId(root, "request_id"),
                StrictJson.RequiredToken(root, "device_id", 1, 256),
                StrictJson.RequiredUInt64(root, "identity_revision"),
                StrictJson.RequiredString(root, "capability_id", 128),
                StrictJson.RequiredUInt64(root, "capability_evidence_revision"),
                StrictJson.RequiredString(root, "route_id", 128),
                StrictJson.OptionalKioskOwnerEpoch(root, "expected_owner_epoch"),
                scopes,
                StrictJson.RequiredPositiveInt64(root, "issued_at_ms"),
                StrictJson.RequiredPositiveInt64(root, "expires_at_ms"));
            request.Validate();
            return request;
        }
        catch (RustyKioskV2ProviderException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            InvalidOperationException or
            FormatException or
            OverflowException)
        {
            throw RustyKioskV2ProviderException.Rejected("request_json_invalid");
        }
    }

    public void Validate()
    {
        if (!string.Equals(Schema, RustyKioskV2ProviderContract.RequestSchema, StringComparison.Ordinal) ||
            !StrictJson.IsOuterToken(ProfileId, 8, 128) ||
            !StrictJson.IsKioskRequestId(RequestId) ||
            !StrictJson.IsOuterToken(DeviceId, 1, 256) ||
            IdentityRevision == 0 ||
            !string.Equals(CapabilityId, RustyKioskV2ProviderContract.CapabilityId, StringComparison.Ordinal) ||
            CapabilityEvidenceRevision == 0 ||
            !string.Equals(RouteId, RustyKioskV2ProviderContract.RouteId, StringComparison.Ordinal) ||
            (ExpectedOwnerEpoch is not null && !StrictJson.IsKioskOwnerEpoch(ExpectedOwnerEpoch)) ||
            Scopes.Count != 1 ||
            !string.Equals(
                Scopes[0],
                RustyKioskV2ProviderContract.CatalogSummaryScope,
                StringComparison.Ordinal) ||
            IssuedAtMs <= 0 ||
            ExpiresAtMs <= IssuedAtMs ||
            ExpiresAtMs - IssuedAtMs >
                (long)RustyKioskV2ProviderContract.MaximumExchangeLifetime.TotalMilliseconds)
        {
            throw RustyKioskV2ProviderException.Rejected("request_binding_invalid");
        }
    }
}

public sealed record RustyKioskV2VerifiedCatalogExchange(
    byte[] RequestEnvelope,
    byte[] ResponseEnvelope,
    byte[] OwnerCatalogJson,
    byte[] OwnerGrantReceipt,
    string ResponseRequestId,
    string ReplayIdentity,
    string CapabilityId,
    ulong CapabilityEvidenceRevision,
    ulong GrantRevision,
    string RouteId,
    string DeviceId,
    ulong IdentityRevision,
    string KioskOwnerEpoch,
    long IssuedAtMs,
    long ExpiresAtMs,
    IReadOnlyList<string> Scopes) : IDisposable
{
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(RequestEnvelope);
        CryptographicOperations.ZeroMemory(ResponseEnvelope);
        CryptographicOperations.ZeroMemory(OwnerCatalogJson);
        CryptographicOperations.ZeroMemory(OwnerGrantReceipt);
    }
}

public sealed record RustyKioskV2CatalogProviderResponse(
    string Schema,
    string Status,
    string ProfileId,
    string RequestId,
    RustyKioskV2VerifiedCatalogExchange? Exchange,
    string? ErrorCode)
{
    public static RustyKioskV2CatalogProviderResponse Verified(
        string profileId,
        string requestId,
        RustyKioskV2VerifiedCatalogExchange exchange)
    {
        var response = new RustyKioskV2CatalogProviderResponse(
            RustyKioskV2ProviderContract.ResponseSchema,
            RustyKioskV2ProviderContract.VerifiedStatus,
            profileId,
            requestId,
            exchange,
            null);
        response.ValidateShape();
        return response;
    }

    public static RustyKioskV2CatalogProviderResponse Failure(
        string status,
        string profileId,
        string requestId,
        string errorCode)
    {
        if (status is not (
            RustyKioskV2ProviderContract.UnavailableStatus or
            RustyKioskV2ProviderContract.RejectedStatus or
            RustyKioskV2ProviderContract.FailedStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (!StrictJson.IsOuterToken(profileId, 8, 128) ||
            !StrictJson.IsKioskRequestId(requestId) ||
            string.IsNullOrEmpty(errorCode) ||
            errorCode.Length > 64 ||
            errorCode.Any(static character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
        {
            throw new ArgumentException("Invalid bounded provider failure response.");
        }
        var response = new RustyKioskV2CatalogProviderResponse(
            RustyKioskV2ProviderContract.ResponseSchema,
            status,
            profileId,
            requestId,
            null,
            errorCode);
        response.ValidateShape();
        return response;
    }

    public byte[] ToUtf8Json()
    {
        ValidateShape();
        if (Exchange is { } bounded &&
            (bounded.RequestEnvelope.Length is 0 or > RustyKioskV2ProviderContract.MaximumRequestEnvelopeBytes ||
             bounded.ResponseEnvelope.Length is 0 or > RustyKioskV2ProviderContract.MaximumResponseEnvelopeBytes ||
             bounded.OwnerCatalogJson.Length is 0 or > RustyKioskV2ProviderContract.MaximumOwnerCatalogBytes ||
             bounded.OwnerGrantReceipt.Length is 0 or > RustyKioskV2ProviderContract.MaximumOwnerGrantReceiptBytes))
        {
            throw RustyKioskV2ProviderException.Failed("response_evidence_size_invalid");
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("status", Status);
            writer.WriteString("profile_id", ProfileId);
            writer.WriteString("request_id", RequestId);
            if (Exchange is { } exchange)
            {
                writer.WriteString("request_envelope_base64url", Base64Url.Encode(exchange.RequestEnvelope));
                writer.WriteString("response_envelope_base64url", Base64Url.Encode(exchange.ResponseEnvelope));
                writer.WriteString("owner_catalog_json_base64url", Base64Url.Encode(exchange.OwnerCatalogJson));
                writer.WriteString("owner_grant_receipt_base64url", Base64Url.Encode(exchange.OwnerGrantReceipt));
                writer.WriteString("response_request_id", exchange.ResponseRequestId);
                writer.WriteString("replay_identity", exchange.ReplayIdentity);
                writer.WriteString("capability_id", exchange.CapabilityId);
                writer.WriteNumber("capability_evidence_revision", exchange.CapabilityEvidenceRevision);
                writer.WriteNumber("grant_revision", exchange.GrantRevision);
                writer.WriteString("route_id", exchange.RouteId);
                writer.WriteString("device_id", exchange.DeviceId);
                writer.WriteNumber("identity_revision", exchange.IdentityRevision);
                writer.WriteString("kiosk_owner_epoch", exchange.KioskOwnerEpoch);
                writer.WriteNumber("issued_at_ms", exchange.IssuedAtMs);
                writer.WriteNumber("expires_at_ms", exchange.ExpiresAtMs);
                writer.WriteStartArray("scopes");
                foreach (var scope in exchange.Scopes)
                {
                    writer.WriteStringValue(scope);
                }
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteString("error_code", ErrorCode);
            }
            writer.WriteEndObject();
        }
        if (stream.Length > RustyKioskV2ProviderContract.MaximumResponseBytes)
        {
            throw RustyKioskV2ProviderException.Failed("response_size_invalid");
        }
        return stream.ToArray();
    }

    private void ValidateShape()
    {
        if (Schema != RustyKioskV2ProviderContract.ResponseSchema ||
            !StrictJson.IsOuterToken(ProfileId, 8, 128) ||
            !StrictJson.IsKioskRequestId(RequestId))
        {
            throw RustyKioskV2ProviderException.Failed("response_binding_invalid");
        }
        if (Exchange is { } exchange)
        {
            if (Status != RustyKioskV2ProviderContract.VerifiedStatus ||
                ErrorCode is not null ||
                exchange.ResponseRequestId != RequestId ||
                !StrictJson.IsKioskRequestId(exchange.ResponseRequestId) ||
                !StrictJson.IsSha256(exchange.ReplayIdentity) ||
                exchange.CapabilityId != RustyKioskV2ProviderContract.CapabilityId ||
                exchange.CapabilityEvidenceRevision == 0 ||
                exchange.GrantRevision == 0 ||
                exchange.RouteId != RustyKioskV2ProviderContract.RouteId ||
                !StrictJson.IsOuterToken(exchange.DeviceId, 1, 256) ||
                exchange.IdentityRevision == 0 ||
                !StrictJson.IsKioskOwnerEpoch(exchange.KioskOwnerEpoch) ||
                exchange.IssuedAtMs <= 0 ||
                exchange.ExpiresAtMs <= exchange.IssuedAtMs ||
                exchange.ExpiresAtMs - exchange.IssuedAtMs >
                    (long)RustyKioskV2ProviderContract.MaximumExchangeLifetime.TotalMilliseconds ||
                exchange.Scopes.Count != 1 ||
                exchange.Scopes[0] != RustyKioskV2ProviderContract.CatalogSummaryScope)
            {
                throw RustyKioskV2ProviderException.Failed("response_binding_invalid");
            }
            return;
        }
        if (Status is not (
                RustyKioskV2ProviderContract.UnavailableStatus or
                RustyKioskV2ProviderContract.RejectedStatus or
                RustyKioskV2ProviderContract.FailedStatus) ||
            string.IsNullOrEmpty(ErrorCode) ||
            ErrorCode.Length > 64 ||
            ErrorCode.Any(static character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
        {
            throw RustyKioskV2ProviderException.Failed("response_binding_invalid");
        }
    }
}

public sealed class RustyKioskV2ProviderException : Exception
{
    private RustyKioskV2ProviderException(string status, string code)
        : base(code)
    {
        Status = status;
        Code = code;
    }

    public string Status { get; }
    public string Code { get; }

    public static RustyKioskV2ProviderException Unavailable(string code) => new("unavailable", code);
    public static RustyKioskV2ProviderException Rejected(string code) => new("rejected", code);
    public static RustyKioskV2ProviderException Failed(string code) => new("failed", code);
}

internal static partial class StrictJson
{
    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenCharacters();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Characters();

    [GeneratedRegex("^[1-9][0-9]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveIntegerCharacters();

    public static void RequireExactObject(JsonElement element, IReadOnlyCollection<string> fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException();
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !fields.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException();
            }
        }
        if (seen.Count != fields.Count)
        {
            throw new InvalidOperationException();
        }
    }

    public static void RequireObjectFields(
        JsonElement element,
        IReadOnlyCollection<string> allowedFields,
        IReadOnlyCollection<string> requiredFields)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException();
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name) ||
                !allowedFields.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException();
            }
        }
        if (requiredFields.Any(required => !seen.Contains(required)))
        {
            throw new InvalidOperationException();
        }
    }

    public static JsonElement RequiredProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidOperationException();

    public static string RequiredString(JsonElement element, string name, int maximumLength)
    {
        var property = RequiredProperty(element, name);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException();
        }
        var value = property.GetString() ?? throw new InvalidOperationException();
        if (value.Length == 0 || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static string RequiredToken(
        JsonElement element,
        string name,
        int minimumLength,
        int maximumLength)
    {
        var value = RequiredString(element, name, maximumLength);
        if (value.Length < minimumLength || !TokenCharacters().IsMatch(value))
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static string RequiredKioskToken(JsonElement element, string name)
    {
        var value = RequiredString(element, name, 128);
        if (!IsKioskToken(value))
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static string RequiredKioskRequestId(JsonElement element, string name)
    {
        var value = RequiredString(element, name, 96);
        if (!IsKioskRequestId(value))
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static string? OptionalKioskToken(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException();
        }
        var value = property.GetString() ?? throw new InvalidOperationException();
        if (!IsKioskToken(value))
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static string? OptionalKioskOwnerEpoch(JsonElement element, string name)
    {
        var value = OptionalKioskToken(element, name);
        if (value is not null && !IsKioskOwnerEpoch(value))
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static bool IsKioskToken(string value) =>
        value.Length is >= 16 and <= 128 &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public static bool IsKioskRequestId(string value) =>
        value.Length is >= 8 and <= 96 &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public static bool IsKioskOwnerEpoch(string value) =>
        value.Length is >= 16 and <= 96 &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public static string RequiredKioskOwnerEpoch(JsonElement element, string name)
    {
        var value = RequiredString(element, name, 96);
        if (!IsKioskOwnerEpoch(value))
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static bool IsOuterToken(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength &&
        value.Length <= maximumLength &&
        TokenCharacters().IsMatch(value);

    public static bool IsSha256(string value) => Sha256Characters().IsMatch(value);

    public static string RequiredSha256(JsonElement element, string name)
    {
        var value = RequiredString(element, name, 64);
        if (!Sha256Characters().IsMatch(value))
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static long RequiredPositiveInt64(JsonElement element, string name)
    {
        var property = RequiredProperty(element, name);
        var raw = property.GetRawText();
        if (property.ValueKind != JsonValueKind.Number ||
            !PositiveIntegerCharacters().IsMatch(raw) ||
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static ulong RequiredUInt64(JsonElement element, string name)
    {
        var property = RequiredProperty(element, name);
        var raw = property.GetRawText();
        if (property.ValueKind != JsonValueKind.Number ||
            !PositiveIntegerCharacters().IsMatch(raw) ||
            !ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value == 0)
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static int RequiredNonNegativeInt32(JsonElement element, string name)
    {
        var property = RequiredProperty(element, name);
        var raw = property.GetRawText();
        if (property.ValueKind != JsonValueKind.Number ||
            !Regex.IsMatch(raw, "^(0|[1-9][0-9]{0,9})$", RegexOptions.CultureInvariant) ||
            !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < 0)
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    public static IReadOnlyList<string> RequiredStringArray(
        JsonElement element,
        string name,
        int minimumCount,
        int maximumCount,
        int maximumStringLength)
    {
        var property = RequiredProperty(element, name);
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException();
        }
        var values = property.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException();
            }
            var value = item.GetString() ?? throw new InvalidOperationException();
            if (value.Length == 0 || value.Length > maximumStringLength || value.Any(char.IsControl))
            {
                throw new InvalidOperationException();
            }
            return value;
        }).ToArray();
        if (values.Length < minimumCount || values.Length > maximumCount)
        {
            throw new InvalidOperationException();
        }
        return values;
    }

    public static bool RequiredBoolean(JsonElement element, string name)
    {
        var property = RequiredProperty(element, name);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException()
        };
    }

    public static void RequireUniqueObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException();
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidOperationException();
            }
        }
    }
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static byte[] Decode(string value, int exactBytes = 0, int maximumBytes = int.MaxValue)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Contains('=', StringComparison.Ordinal) ||
            value.Any(static character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw RustyKioskV2ProviderException.Rejected("base64url_invalid");
        }
        var padding = (value.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw RustyKioskV2ProviderException.Rejected("base64url_invalid")
        };
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + padding);
        }
        catch (FormatException)
        {
            throw RustyKioskV2ProviderException.Rejected("base64url_invalid");
        }
        if ((exactBytes != 0 && decoded.Length != exactBytes) ||
            decoded.Length > maximumBytes ||
            !string.Equals(Encode(decoded), value, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw RustyKioskV2ProviderException.Rejected("base64url_invalid");
        }
        return decoded;
    }
}
