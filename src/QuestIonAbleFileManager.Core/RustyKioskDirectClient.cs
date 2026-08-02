using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.Core;

public sealed record RustyKioskDirectEndpoint(
    Uri BaseUri,
    [property: JsonIgnore] string PairingCode)
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

    public override string ToString() => BaseUri.AbsoluteUri;
}

public sealed record RustyKioskDirectStatus(
    string Schema,
    string? Endpoint,
    bool InstallerAllowed,
    string StagingDirectoryKind,
    string Message,
    long? BridgeGeneration = null,
    string? SessionId = null);

public sealed record RustyKioskStagedFile(
    string Name,
    long Bytes,
    long ModifiedAtMs,
    string? Sha256 = null)
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

public sealed record RustyKioskDirectRequestReceipt(
    string RequestId,
    string OperationState,
    bool Accepted,
    bool Completed,
    string Message,
    long? EnqueuedAtMs,
    long? ExpiresAtMs)
{
    public OperatorMutationStage MutationStage => OperationState switch
    {
        "pending" => OperatorMutationStage.Pending,
        "pending_wearer_action" => OperatorMutationStage.PendingWearerAction,
        "confirmed" => OperatorMutationStage.Confirmed,
        "rejected" => OperatorMutationStage.Rejected,
        "expired" => OperatorMutationStage.Expired,
        "cancelled" => OperatorMutationStage.Cancelled,
        "unknown" => OperatorMutationStage.Failed,
        _ => throw new InvalidDataException("Rusty Kiosk returned an unknown request lifecycle state.")
    };
}

/// <summary>
/// Bounded, authenticated local transport for Rusty Kiosk. This is not an ADB or shell client.
/// Every request has an expiring HMAC envelope and replay id; every successful or authenticated
/// error response is independently signed and verified before it is returned to callers.
/// </summary>
public sealed class RustyKioskDirectClient : IDisposable
{
    public const string ContractSchema = "rusty.kiosk.direct_operator.v2";
    private const int MaxTagBytes = 256 * 1024;
    private const int MaxJsonResponseBytes = 1024 * 1024;
    private const long MaxStagedFileBytes = 2L * 1024L * 1024L * 1024L;
    private readonly Uri _baseUri;
    private readonly byte[] _authenticationKey;
    private readonly string? _sessionId;
    private readonly long? _expectedBridgeGeneration;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly int _maxJsonResponseBytes;
    private readonly long _maxStagedFileBytes;
    private int _disposed;

    public RustyKioskDirectClient(
        RustyKioskDirectEndpoint endpoint,
        HttpClient? httpClient = null)
        : this(endpoint, httpClient, MaxJsonResponseBytes, MaxStagedFileBytes)
    {
    }

    internal RustyKioskDirectClient(
        RustyKioskDirectEndpoint endpoint,
        HttpClient? httpClient,
        int maxJsonResponseBytes,
        long maxStagedFileBytes)
        : this(
            endpoint.BaseUri,
            Encoding.UTF8.GetBytes(endpoint.PairingCode),
            sessionId: null,
            expectedBridgeGeneration: null,
            httpClient,
            maxJsonResponseBytes,
            maxStagedFileBytes)
    {
    }

    internal RustyKioskDirectClient(
        Uri baseUri,
        byte[] authenticationKey,
        string? sessionId,
        long? expectedBridgeGeneration,
        HttpClient? httpClient = null,
        int maxJsonResponseBytes = MaxJsonResponseBytes,
        long maxStagedFileBytes = MaxStagedFileBytes)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(authenticationKey);
        if (authenticationKey.Length is < 16 or > 128)
        {
            throw new ArgumentException(
                "The direct-link authentication key must contain 16 to 128 bytes.",
                nameof(authenticationKey));
        }
        if (sessionId is not null &&
            !System.Text.RegularExpressions.Regex.IsMatch(sessionId, "^[A-Za-z0-9_-]{8,64}$"))
        {
            throw new ArgumentException("The direct-link session id is invalid.", nameof(sessionId));
        }
        if (expectedBridgeGeneration is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedBridgeGeneration));
        }
        if (maxJsonResponseBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxJsonResponseBytes));
        }
        if (maxStagedFileBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStagedFileBytes));
        }
        _baseUri = baseUri;
        _authenticationKey = authenticationKey;
        _sessionId = sessionId;
        _expectedBridgeGeneration = expectedBridgeGeneration;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _maxJsonResponseBytes = maxJsonResponseBytes;
        _maxStagedFileBytes = maxStagedFileBytes;
    }

    public Uri BaseUri => _baseUri;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        CryptographicOperations.ZeroMemory(_authenticationKey);
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

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

        var status = new RustyKioskDirectStatus(
            schema,
            OptionalString(root, "endpoint"),
            root.GetProperty("installer_allowed").GetBoolean(),
            RequiredString(root, "staging_directory_kind"),
            RequiredString(root, "message"),
            root.TryGetProperty("bridge_generation", out var generation) &&
            generation.ValueKind == JsonValueKind.Number
                ? generation.GetInt64()
                : null,
            OptionalString(root, "session_id"));
        if (_sessionId is not null &&
            (!string.Equals(status.SessionId, _sessionId, StringComparison.Ordinal) ||
             status.BridgeGeneration != _expectedBridgeGeneration))
        {
            throw new InvalidDataException(
                "Rusty Kiosk direct-link status did not match the authorized USB session generation.");
        }
        return status;
    }

    public async Task<RustyKioskOperatorResult> InvokeKioskAsync(
        RustyKioskCommand command,
        string? value = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var admitted = await AdmitKioskRequestAsync(
                command,
                value,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return await WaitForKioskResultAsync(
                admitted.RequestId,
                command,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RustyKioskDirectRequestReceipt> AdmitKioskRequestAsync(
        RustyKioskCommand command,
        string? value = null,
        string? requestId = null,
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

        requestId ??= NewRequestId("kiosk");
        ValidateRequestId(requestId);
        var payload = new Dictionary<string, object?>
        {
            ["request_id"] = requestId,
            ["command"] = command.ToWireName(),
            ["value"] = string.IsNullOrWhiteSpace(value) ? null : value.Trim()
        };
        using var admitted = await SendJsonAsync(HttpMethod.Post, "v1/kiosk/invoke", payload, cancellationToken)
            .ConfigureAwait(false);
        if (!admitted.RootElement.GetProperty("accepted").GetBoolean())
        {
            throw new InvalidOperationException(RequiredString(admitted.RootElement, "message"));
        }
        var returnedId = OptionalString(admitted.RootElement, "request_id") ?? requestId;
        if (!string.Equals(returnedId, requestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Rusty Kiosk admitted a different action request id.");
        }
        return new RustyKioskDirectRequestReceipt(
            requestId,
            OptionalString(admitted.RootElement, "operation_state") ?? "pending",
            Accepted: true,
            admitted.RootElement.TryGetProperty("completed", out var completed) && completed.GetBoolean(),
            RequiredString(admitted.RootElement, "message"),
            admitted.RootElement.TryGetProperty("enqueued_at_ms", out var enqueued) && enqueued.ValueKind == JsonValueKind.Number
                ? enqueued.GetInt64()
                : null,
            admitted.RootElement.TryGetProperty("expires_at_ms", out var expires) && expires.ValueKind == JsonValueKind.Number
                ? expires.GetInt64()
                : null);
    }

    public async Task<RustyKioskOperatorResult> WaitForKioskResultAsync(
        string requestId,
        RustyKioskCommand command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(requestId);
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

    public async Task<RustyKioskDirectRequestReceipt> ReadKioskRequestStatusAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(requestId);
        using var response = await SendJsonAsync(
                HttpMethod.Get,
                "v1/kiosk/request-status?request_id=" + Uri.EscapeDataString(requestId),
                null,
                cancellationToken)
            .ConfigureAwait(false);
        return ParseRequestReceipt(response.RootElement, requestId);
    }

    public async Task<RustyKioskDirectRequestReceipt> CancelKioskRequestAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(requestId);
        using var response = await SendJsonAsync(
                HttpMethod.Post,
                "v1/kiosk/cancel",
                new Dictionary<string, object?> { ["request_id"] = requestId },
                cancellationToken)
            .ConfigureAwait(false);
        return ParseRequestReceipt(response.RootElement, requestId);
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
        var expectedSha = Convert.ToHexString(SHA256.HashData(validatedJson)).ToLowerInvariant();
        var returnedSha = RequiredString(response.RootElement, "sha256").ToLowerInvariant();
        if (response.RootElement.GetProperty("bytes").GetInt64() != validatedJson.Length ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(returnedSha),
                Encoding.ASCII.GetBytes(expectedSha)))
        {
            throw new InvalidDataException(
                "Rusty Kiosk tag replacement readback did not match the validated document.");
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
        var staged = new RustyKioskStagedFile(
            RequiredString(root, "name"),
            root.GetProperty("bytes").GetInt64(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RequiredString(root, "sha256").ToLowerInvariant());
        if (!string.Equals(staged.Name, name, StringComparison.Ordinal) ||
            staged.Bytes != info.Length ||
            staged.Sha256 is null ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(staged.Sha256),
                Encoding.ASCII.GetBytes(contentSha)))
        {
            throw new InvalidDataException(
                "Rusty Kiosk staging readback did not match the uploaded filename, size, and digest.");
        }
        return staged;
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
        ThrowIfDisposed();
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
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, relativePath));
        var requestTarget = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        request.Headers.TryAddWithoutValidation("X-Rusty-Request-Id", requestId);
        request.Headers.TryAddWithoutValidation("X-Rusty-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Rusty-Content-Sha256", contentSha);
        request.Headers.TryAddWithoutValidation(
            "X-Rusty-Signature",
            RustyKioskDirectAuth.SignRequest(
                _authenticationKey,
                "GET",
                requestTarget,
                requestId,
                timestamp,
                contentSha));
        AddSessionHeader(request);

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBytes = await ReadBoundedBytesAsync(
                    response.Content,
                    _maxJsonResponseBytes,
                    linked.Token)
                .ConfigureAwait(false);
            VerifyResponse(response, requestId, errorBytes);
            throw new InvalidOperationException(
                TryReadMessage(errorBytes) ?? $"Rusty Kiosk direct link returned HTTP {(int)response.StatusCode}.");
        }

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is { } responseLength &&
            (responseLength < 0 || responseLength > _maxStagedFileBytes))
        {
            throw new InvalidDataException("The staged download exceeds the bounded size limit.");
        }

        var temporary = fullOutput + "." + requestId + ".part";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            await using (var output = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                {
                    if (copied > _maxStagedFileBytes - read)
                    {
                        throw new InvalidDataException("The staged download exceeds the bounded size limit.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(copied);
                }
                await output.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            if (declaredLength is { } expectedLength && copied != expectedLength)
            {
                throw new InvalidDataException("The staged download did not match its declared byte count.");
            }
            var actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            VerifyResponseDigest(response, requestId, actualSha);
            File.Move(temporary, fullOutput, overwrite);
            return fullOutput;
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Cleanup failure must not replace the authenticated transfer failure.
            }
            throw;
        }
    }

    public async Task<RustyKioskDirectInstallReceipt> RequestInstallAsync(
        IReadOnlyList<RustyKioskStagedFile> stagedApks,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedApks);
        if (stagedApks.Count is < 1 or > 32)
        {
            throw new ArgumentException("Choose between one and 32 staged APK parts.", nameof(stagedApks));
        }
        var commitments = stagedApks.Select(file => new
        {
            name = ValidateStagedName(file.Name),
            bytes = file.Bytes,
            sha256 = file.Sha256
        }).ToArray();
        if (commitments.Any(static file =>
                !file.name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ||
                file.bytes <= 0 ||
                file.sha256 is null ||
                !System.Text.RegularExpressions.Regex.IsMatch(file.sha256, "^[a-f0-9]{64}$")))
        {
            throw new ArgumentException(
                "Every install part requires an APK name, positive byte count, and lowercase SHA-256 commitment.",
                nameof(stagedApks));
        }
        if (commitments.Select(static file => file.name).Distinct(StringComparer.Ordinal).Count() != commitments.Length)
        {
            throw new ArgumentException("Every staged APK part name must be distinct.", nameof(stagedApks));
        }
        requestId ??= NewRequestId("install");
        using var response = await SendJsonAsync(
                HttpMethod.Post,
                "v1/install",
                new Dictionary<string, object?>
                {
                    ["request_id"] = requestId,
                    ["files"] = commitments
                },
                cancellationToken,
                timeout: TimeSpan.FromMinutes(20))
            .ConfigureAwait(false);
        var receipt = ParseInstallReceipt(response.RootElement);
        if (!string.Equals(receipt.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Rusty Kiosk returned an install receipt for a different request id.");
        }
        return receipt;
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
        var receipt = ParseInstallReceipt(response.RootElement);
        if (!string.Equals(receipt.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Rusty Kiosk returned install status for a different request id.");
        }
        return receipt;
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
        ThrowIfDisposed();
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath))
        {
            Content = content
        };
        var authRequestId = NewRequestId("http");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestTarget = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        var signature = RustyKioskDirectAuth.SignRequest(
            _authenticationKey,
            method.Method,
            requestTarget,
            authRequestId,
            timestamp,
            contentSha);
        request.Headers.TryAddWithoutValidation("X-Rusty-Request-Id", authRequestId);
        request.Headers.TryAddWithoutValidation("X-Rusty-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Rusty-Content-Sha256", contentSha);
        request.Headers.TryAddWithoutValidation("X-Rusty-Signature", signature);
        AddSessionHeader(request);

        using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var effectiveToken = linked.Token;
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveToken)
            .ConfigureAwait(false);
        var bytes = await ReadBoundedBytesAsync(
                response.Content,
                _maxJsonResponseBytes,
                effectiveToken)
            .ConfigureAwait(false);
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
            _authenticationKey,
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

    private static RustyKioskDirectRequestReceipt ParseRequestReceipt(
        JsonElement root,
        string expectedRequestId)
    {
        var requestId = RequiredString(root, "request_id");
        if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Rusty Kiosk returned lifecycle state for a different request id.");
        }
        var receipt = new RustyKioskDirectRequestReceipt(
            requestId,
            RequiredString(root, "operation_state"),
            root.GetProperty("accepted").GetBoolean(),
            root.GetProperty("completed").GetBoolean(),
            RequiredString(root, "message"),
            root.TryGetProperty("enqueued_at_ms", out var enqueued) && enqueued.ValueKind == JsonValueKind.Number
                ? enqueued.GetInt64()
                : null,
            root.TryGetProperty("expires_at_ms", out var expires) && expires.ValueKind == JsonValueKind.Number
                ? expires.GetInt64()
                : null);
        _ = receipt.MutationStage;
        return receipt;
    }

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

    private void AddSessionHeader(HttpRequestMessage request)
    {
        if (_sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Rusty-Session-Id", _sessionId);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is { } boundedLength &&
            (boundedLength < 0 || boundedLength > maximumBytes))
        {
            throw new InvalidDataException("The direct-link response exceeds the bounded size limit.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(
            declaredLength is { } length
                ? checked((int)length)
                : Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length > maximumBytes - read)
            {
                throw new InvalidDataException("The direct-link response exceeds the bounded size limit.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
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
        WithUtf8Key(pairingCode, key => SignRequest(
            key,
            method,
            requestTarget,
            requestId,
            timestampSeconds,
            contentSha256));

    internal static string SignRequest(
        byte[] authenticationKey,
        string method,
        string requestTarget,
        string requestId,
        long timestampSeconds,
        string contentSha256) =>
        Hmac(
            authenticationKey,
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
        WithUtf8Key(pairingCode, key => SignResponse(
            key,
            requestId,
            statusCode,
            contentSha256));

    internal static string SignResponse(
        byte[] authenticationKey,
        string requestId,
        int statusCode,
        string contentSha256) =>
        Hmac(
            authenticationKey,
            string.Join('\n',
                "RESPONSE",
                requestId,
                statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contentSha256.ToLowerInvariant()));

    private static string Hmac(byte[] key, string canonical)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string WithUtf8Key(string value, Func<byte[], string> action)
    {
        var key = Encoding.UTF8.GetBytes(value);
        try
        {
            return action(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
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
