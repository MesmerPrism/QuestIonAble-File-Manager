using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core;

public sealed record RustyKioskDirectEndpoint(Uri BaseUri, string PairingCode)
{
    public static RustyKioskDirectEndpoint Parse(string endpoint, string pairingCode)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Enter the headset's complete http:// address from Rusty Kiosk.", nameof(endpoint));
        }

        var normalizedCode = pairingCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedCode, "^[0-9A-HJKMNP-TV-Z]{2}(?:[0-9A-HJKMNP-TV-Z-]{14,38})$"))
        {
            throw new ArgumentException("Enter the complete pairing code shown by Rusty Kiosk.", nameof(pairingCode));
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + "/"
        };
        return new RustyKioskDirectEndpoint(builder.Uri, normalizedCode);
    }
}

public sealed record RustyKioskDirectStatus(
    string Schema,
    string? Endpoint,
    bool InstallerAllowed,
    string StagingDirectoryKind,
    string Message);

public sealed record RustyKioskStagedFile(string Name, long Bytes, long ModifiedAtMs)
{
    public string DisplayLabel => $"{Name} · {Bytes:N0} bytes";
}

public sealed record RustyKioskDirectInstallReceipt(
    string RequestId,
    string State,
    bool Completed,
    string Message,
    int? SessionId,
    string? PackageName)
{
    public bool Installed => Completed && string.Equals(State, "installed", StringComparison.Ordinal);
    public bool Failed => Completed && !Installed;
    public bool NeedsWearerAction => State is "pending-wearer-confirmation" or "needs-wearer-permission";
}

/// <summary>
/// Bounded, authenticated local transport for Rusty Kiosk. This is not an ADB or shell client.
/// Every request has an expiring HMAC envelope and replay id; every successful or authenticated
/// error response is independently signed and verified before it is returned to callers.
/// </summary>
public sealed class RustyKioskDirectClient
{
    public const string ContractSchema = "rusty.kiosk.direct_operator.v1";
    private const int MaxTagBytes = 256 * 1024;
    private const long MaxStagedFileBytes = 2L * 1024L * 1024L * 1024L;
    private readonly RustyKioskDirectEndpoint _endpoint;
    private readonly HttpClient _httpClient;

    public RustyKioskDirectClient(
        RustyKioskDirectEndpoint endpoint,
        HttpClient? httpClient = null)
    {
        _endpoint = endpoint;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public RustyKioskDirectEndpoint Endpoint => _endpoint;

    public async Task<RustyKioskDirectStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var json = await SendJsonAsync(HttpMethod.Get, "v1/status", null, cancellationToken)
            .ConfigureAwait(false);
        var root = json.RootElement;
        var schema = RequiredString(root, "schema");
        if (!string.Equals(schema, ContractSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported Rusty Kiosk direct-link schema: {schema}");
        }

        return new RustyKioskDirectStatus(
            schema,
            OptionalString(root, "endpoint"),
            root.GetProperty("installer_allowed").GetBoolean(),
            RequiredString(root, "staging_directory_kind"),
            RequiredString(root, "message"));
    }

    public async Task<RustyKioskOperatorResult> InvokeKioskAsync(
        RustyKioskCommand command,
        string? value = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (command.RequiresValue() && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{command.ToWireName()} requires a value.", nameof(value));
        }
        if (!command.AllowsValue() && !string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{command.ToWireName()} does not accept a value.", nameof(value));
        }

        var requestId = NewRequestId("kiosk");
        var payload = new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["command"] = command.ToWireName(),
            ["value"] = string.IsNullOrWhiteSpace(value) ? null : value.Trim()
        };
        using (var admitted = await SendJsonAsync(HttpMethod.Post, "v1/kiosk/invoke", payload, cancellationToken)
                   .ConfigureAwait(false))
        {
            if (!admitted.RootElement.GetProperty("accepted").GetBoolean())
            {
                throw new InvalidOperationException(RequiredString(admitted.RootElement, "message"));
            }
        }

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(12));
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var result = await SendJsonAsync(
                    HttpMethod.Get,
                    $"v1/kiosk/result?request_id={Uri.EscapeDataString(requestId)}",
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.RootElement.TryGetProperty("schema", out var schema) &&
                string.Equals(schema.GetString(), RustyKioskContract.ResultSchema, StringComparison.Ordinal))
            {
                var parsed = RustyKioskOperatorResult.Parse(result.RootElement.GetRawText());
                if (!string.Equals(
                        parsed.RequestId,
                        requestId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Rusty Kiosk returned a result for a different request.");
                }
                if (parsed.Command != command)
                {
                    throw new InvalidDataException(
                        "Rusty Kiosk returned a result for a different typed command.");
                }
                if (!parsed.Accepted)
                {
                    throw new InvalidOperationException(parsed.Message);
                }
                if (parsed.Completed)
                {
                    return parsed;
                }
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException("Rusty Kiosk admitted the direct request but did not publish matching readback in time.");
    }

    public async Task<byte[]> ReadTagsAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await SendBytesAsync(HttpMethod.Get, "v1/tags", null, cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length is < 1 or > MaxTagBytes)
        {
            throw new InvalidDataException("Rusty Kiosk returned an empty or oversized tag file.");
        }
        using var json = JsonDocument.Parse(bytes);
        var schema = RequiredString(json.RootElement, "schema");
        if (!string.Equals(schema, RustyKioskContract.TagFileSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported Rusty Kiosk tag schema: {schema}");
        }
        return bytes;
    }

    public async Task WriteTagsAsync(byte[] validatedJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validatedJson);
        if (validatedJson.Length is < 1 or > MaxTagBytes)
        {
            throw new ArgumentException("The tag file is empty or exceeds the bounded size.", nameof(validatedJson));
        }
        using var parsed = JsonDocument.Parse(validatedJson);
        if (!string.Equals(
                RequiredString(parsed.RootElement, "schema"),
                RustyKioskContract.TagFileSchema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The tag file does not use Rusty Kiosk's supported schema.");
        }

        using var response = await SendJsonBytesAsync(HttpMethod.Put, "v1/tags", validatedJson, cancellationToken)
            .ConfigureAwait(false);
        if (!response.RootElement.GetProperty("accepted").GetBoolean())
        {
            throw new InvalidOperationException(RequiredString(response.RootElement, "message"));
        }
    }

    public async Task<IReadOnlyList<RustyKioskStagedFile>> ListStagingAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendJsonAsync(HttpMethod.Get, "v1/staging", null, cancellationToken)
            .ConfigureAwait(false);
        return response.RootElement.GetProperty("files")
            .EnumerateArray()
            .Select(static file => new RustyKioskStagedFile(
                RequiredString(file, "name"),
                file.GetProperty("bytes").GetInt64(),
                file.GetProperty("modified_at_ms").GetInt64()))
            .ToArray();
    }

    public async Task<RustyKioskStagedFile> UploadToStagingAsync(
        string localPath,
        string? stagedName = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(localPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is < 1 or > MaxStagedFileBytes)
        {
            throw new ArgumentException("The local file is missing, empty, or exceeds the direct-link limit.", nameof(localPath));
        }
        var name = ValidateStagedName(stagedName ?? info.Name);
        var contentSha = await Sha256FileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var path = "v1/staging/files/" + Uri.EscapeDataString(name);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var content = new ProgressStreamContent(stream, info.Length, progress);
        using var response = await SendJsonContentAsync(
                HttpMethod.Put,
                path,
                content,
                contentSha,
                cancellationToken,
                timeout: TimeSpan.FromMinutes(20))
            .ConfigureAwait(false);
        var root = response.RootElement;
        return new RustyKioskStagedFile(
            RequiredString(root, "name"),
            root.GetProperty("bytes").GetInt64(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public async Task DeleteStagedAsync(string stagedName, CancellationToken cancellationToken = default)
    {
        var name = ValidateStagedName(stagedName);
        using var response = await SendJsonAsync(
                HttpMethod.Delete,
                "v1/staging/files/" + Uri.EscapeDataString(name),
                null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> DownloadFromStagingAsync(
        string stagedName,
        string outputPath,
        bool overwrite = false,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var name = ValidateStagedName(stagedName);
        var fullOutput = Path.GetFullPath(outputPath);
        if (File.Exists(fullOutput) && !overwrite)
        {
            throw new IOException($"The output file already exists: {fullOutput}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        var requestId = NewRequestId("http");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var contentSha = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        var relativePath = "v1/staging/files/" + Uri.EscapeDataString(name);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint.BaseUri, relativePath));
        var requestTarget = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        request.Headers.TryAddWithoutValidation("X-Rusty-Request-Id", requestId);
        request.Headers.TryAddWithoutValidation("X-Rusty-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Rusty-Content-Sha256", contentSha);
        request.Headers.TryAddWithoutValidation(
            "X-Rusty-Signature",
            RustyKioskDirectAuth.SignRequest(
                _endpoint.PairingCode,
                "GET",
                requestTarget,
                requestId,
                timestamp,
                contentSha));

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            VerifyResponse(response, requestId, errorBytes);
            throw new InvalidOperationException(
                TryReadMessage(errorBytes) ?? $"Rusty Kiosk direct link returned HTTP {(int)response.StatusCode}.");
        }

        var temporary = fullOutput + "." + requestId + ".part";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                copied += read;
                progress?.Report(copied);
            }
            await output.FlushAsync(linked.Token).ConfigureAwait(false);
            var actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            VerifyResponseDigest(response, requestId, actualSha);
            File.Move(temporary, fullOutput, overwrite);
            return fullOutput;
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public async Task<RustyKioskDirectInstallReceipt> RequestInstallAsync(
        IReadOnlyList<string> stagedApkNames,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedApkNames);
        if (stagedApkNames.Count is < 1 or > 32)
        {
            throw new ArgumentException("Choose between one and 32 staged APK parts.", nameof(stagedApkNames));
        }
        var names = stagedApkNames.Select(ValidateStagedName).ToArray();
        if (names.Any(static name => !name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Every local install part must be an APK.", nameof(stagedApkNames));
        }
        requestId ??= NewRequestId("install");
        using var response = await SendJsonAsync(
                HttpMethod.Post,
                "v1/install",
                new Dictionary<string, object?>
                {
                    ["request_id"] = requestId,
                    ["files"] = names
                },
                cancellationToken,
                timeout: TimeSpan.FromMinutes(20))
            .ConfigureAwait(false);
        return ParseInstallReceipt(response.RootElement);
    }

    public async Task<RustyKioskDirectInstallReceipt> ReadInstallReceiptAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(requestId);
        using var response = await SendJsonAsync(
                HttpMethod.Get,
                "v1/install/" + requestId,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        return ParseInstallReceipt(response.RootElement);
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var bytes = payload is null ? [] : JsonSerializer.SerializeToUtf8Bytes(payload);
        return await SendJsonBytesAsync(method, relativePath, bytes, cancellationToken, timeout)
            .ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendJsonBytesAsync(
        HttpMethod method,
        string relativePath,
        byte[] bytes,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var content = bytes.Length == 0 ? null : new ByteArrayContent(bytes);
        content?.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
        return await SendJsonContentAsync(
                method,
                relativePath,
                content,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                cancellationToken,
                timeout)
            .ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendJsonContentAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        string contentSha,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var bytes = await SendContentAsync(method, relativePath, content, contentSha, cancellationToken, timeout)
            .ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Rusty Kiosk returned a non-JSON direct-link response.", exception);
        }
    }

    private Task<byte[]> SendBytesAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        CancellationToken cancellationToken) =>
        SendContentAsync(
            method,
            relativePath,
            content,
            Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(),
            cancellationToken,
            null);

    private async Task<byte[]> SendContentAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        string contentSha,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        using var request = new HttpRequestMessage(method, new Uri(_endpoint.BaseUri, relativePath))
        {
            Content = content
        };
        var authRequestId = NewRequestId("http");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestTarget = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        var signature = RustyKioskDirectAuth.SignRequest(
            _endpoint.PairingCode,
            method.Method,
            requestTarget,
            authRequestId,
            timestamp,
            contentSha);
        request.Headers.TryAddWithoutValidation("X-Rusty-Request-Id", authRequestId);
        request.Headers.TryAddWithoutValidation("X-Rusty-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Rusty-Content-Sha256", contentSha);
        request.Headers.TryAddWithoutValidation("X-Rusty-Signature", signature);

        using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var effectiveToken = linked.Token;
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveToken)
            .ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(effectiveToken).ConfigureAwait(false);
        VerifyResponse(response, authRequestId, bytes);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryReadMessage(bytes) ?? $"Rusty Kiosk direct link returned HTTP {(int)response.StatusCode}.";
            throw new InvalidOperationException(message);
        }
        return bytes;
    }

    private void VerifyResponse(HttpResponseMessage response, string requestId, byte[] bytes)
    {
        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        VerifyResponseDigest(response, requestId, actualSha);
    }

    private void VerifyResponseDigest(HttpResponseMessage response, string requestId, string actualSha)
    {
        var returnedId = RequiredHeader(response.Headers, "X-Rusty-Request-Id");
        var contentSha = RequiredHeader(response.Headers, "X-Rusty-Content-Sha256").ToLowerInvariant();
        var signature = RequiredHeader(response.Headers, "X-Rusty-Signature").ToLowerInvariant();
        if (!string.Equals(returnedId, requestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The direct-link response id did not match the request.");
        }
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualSha),
                Encoding.ASCII.GetBytes(contentSha)))
        {
            throw new InvalidDataException("The direct-link response body failed its signed digest check.");
        }
        var expected = RustyKioskDirectAuth.SignResponse(
            _endpoint.PairingCode,
            requestId,
            (int)response.StatusCode,
            contentSha);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(signature)))
        {
            throw new InvalidDataException("The direct-link response signature was not accepted.");
        }
    }

    private static RustyKioskDirectInstallReceipt ParseInstallReceipt(JsonElement root) =>
        new(
            RequiredString(root, "request_id"),
            RequiredString(root, "state"),
            root.GetProperty("completed").GetBoolean(),
            RequiredString(root, "message"),
            root.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.Number
                ? session.GetInt32()
                : null,
            OptionalString(root, "package"));

    private static string ValidateStagedName(string value)
    {
        var name = value?.Trim() ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._ ()+@-]{0,159}$") ||
            name is "." or "..")
        {
            throw new ArgumentException("Use a single staging filename without folders or path separators.", nameof(value));
        }
        return name;
    }

    private static void ValidateRequestId(string requestId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(requestId ?? string.Empty, "^[A-Za-z0-9_-]{8,64}$"))
        {
            throw new ArgumentException("The direct-link request id is invalid.", nameof(requestId));
        }
    }

    private static string NewRequestId(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}";

    private static string RequiredHeader(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) && values.SingleOrDefault() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Rusty Kiosk omitted the signed response header {name}.");

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Rusty Kiosk omitted {name}.");

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? TryReadMessage(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            return OptionalString(json.RootElement, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public static class RustyKioskDirectAuth
{
    public static string SignRequest(
        string pairingCode,
        string method,
        string requestTarget,
        string requestId,
        long timestampSeconds,
        string contentSha256) =>
        Hmac(
            pairingCode,
            string.Join('\n',
                method.ToUpperInvariant(),
                requestTarget,
                requestId,
                timestampSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contentSha256.ToLowerInvariant()));

    public static string SignResponse(
        string pairingCode,
        string requestId,
        int statusCode,
        string contentSha256) =>
        Hmac(
            pairingCode,
            string.Join('\n',
                "RESPONSE",
                requestId,
                statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contentSha256.ToLowerInvariant()));

    private static string Hmac(string key, string canonical)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

internal sealed class ProgressStreamContent(
    Stream source,
    long length,
    IProgress<long>? progress) : HttpContent
{
    protected override bool TryComputeLength(out long computedLength)
    {
        computedLength = length;
        return true;
    }

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context)
    {
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            copied += read;
            progress?.Report(copied);
        }
    }
}
