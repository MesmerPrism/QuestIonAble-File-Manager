using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core;

public sealed class RustyKioskV2CatalogProvider : IDisposable
{
    private const string ProfileName = "rusty-kiosk-direct-v2";
    private const string ProfileSchema = "rusty.kiosk.direct_operator.v2";
    private const string SessionOpenSchema = "rusty.kiosk.direct_operator.session_open.v2";
    private const string SessionReceiptSchema = "rusty.kiosk.direct_operator.session_open_receipt.v2";
    private const string RequestEnvelopeSchema = "rusty.kiosk.direct_operator.request_envelope.v2";
    private const string ResponseEnvelopeSchema = "rusty.kiosk.direct_operator.response_envelope.v2";
    private const string CatalogRequestSchema = "rusty.kiosk.catalog_request.v2";
    private const string CatalogSnapshotSchema = "rusty.kiosk.catalog_snapshot.v1";
    private const string CatalogSummaryCapability = "catalog-summary";
    private const string ContractPath = "/v2/contract";
    private const string SessionOpenPath = "/v2/session/open";
    private const string CatalogSummaryPath = "/v2/catalog/summary";
    private const int KeyBytes = 32;
    private const int KdfSaltBytes = 32;
    private const int HandshakeNonceBytes = 32;
    private const int NoncePrefixBytes = 4;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const long MaximumSessionLifetimeMs = 90_000;
    private const long MaximumRequestLifetimeMs = 30_000;
    private const int MaximumContractBytes = 64 * 1024;
    private const int MaximumSessionReceiptBytes = 64 * 1024;
    private const int MaximumCatalogPlaintextBytes = 768 * 1024;

    private readonly IRustyKioskV2ProviderProfileStore _profileStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<long> _now;

    public RustyKioskV2CatalogProvider(
        IRustyKioskV2ProviderProfileStore profileStore,
        HttpClient? httpClient = null,
        Func<long>? now = null)
    {
        _profileStore = profileStore;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (httpClient is null)
        {
            _httpClient = new HttpClient(
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false,
                    ConnectTimeout = TimeSpan.FromSeconds(3)
                })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
        }
    }

    public async Task<RustyKioskV2VerifiedCatalogExchange> FetchAsync(
        string selectedProfileId,
        RustyKioskV2CatalogProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        var currentTime = _now();
        if (!string.Equals(selectedProfileId, request.ProfileId, StringComparison.Ordinal) ||
            currentTime < request.IssuedAtMs - 5_000 ||
            currentTime > request.ExpiresAtMs)
        {
            throw RustyKioskV2ProviderException.Rejected("request_freshness_invalid");
        }

        using var profile = _profileStore.Open(selectedProfileId);
        if (!string.Equals(profile.DeviceId, request.DeviceId, StringComparison.Ordinal))
        {
            throw RustyKioskV2ProviderException.Rejected("profile_device_mismatch");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = request.ExpiresAtMs - currentTime;
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(8_000, Math.Max(1, remaining))));

        try
        {
            var publicProfile = await FetchPublicProfileAsync(profile.Endpoint, timeout.Token);
            if (request.ExpectedOwnerEpoch is not null &&
                !string.Equals(
                    publicProfile.OwnerEpoch,
                    request.ExpectedOwnerEpoch,
                    StringComparison.Ordinal))
            {
                throw RustyKioskV2ProviderException.Rejected("kiosk_owner_epoch_mismatch");
            }
            if (!publicProfile.SummaryGranted ||
                !publicProfile.AvailableCapabilities.Contains(
                    CatalogSummaryCapability,
                    StringComparer.Ordinal))
            {
                throw RustyKioskV2ProviderException.Rejected("catalog_summary_grant_unavailable");
            }

            var pairingCode = Encoding.ASCII.GetBytes(profile.PairingCode);
            byte[]? pairingSecret = null;
            byte[]? handshakeKey = null;
            SessionKeys? sessionKeys = null;
            try
            {
                pairingSecret = PairingSecret(pairingCode, publicProfile);
                handshakeKey = HandshakeKey(pairingSecret, publicProfile);
                var sessionRequestId = RandomToken(18);
                var clientNonce = RandomBytes(HandshakeNonceBytes);
                try
                {
                    var sessionIssuedAt = _now();
                    var sessionExpiresAt = Math.Min(
                        request.ExpiresAtMs,
                        checked(sessionIssuedAt + MaximumSessionLifetimeMs));
                    if (sessionExpiresAt <= sessionIssuedAt)
                    {
                        throw RustyKioskV2ProviderException.Rejected(
                            "request_expired_during_exchange");
                    }
                    var sessionRequest = BuildSessionOpenRequest(
                        publicProfile,
                        sessionRequestId,
                        sessionIssuedAt,
                        sessionExpiresAt,
                        Base64Url.Encode(clientNonce),
                        handshakeKey);
                    byte[] rawSessionReceipt;
                    try
                    {
                        rawSessionReceipt = await SendJsonAsync(
                            profile.Endpoint,
                            SessionOpenPath,
                            sessionRequest,
                            MaximumSessionReceiptBytes,
                            timeout.Token);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(sessionRequest);
                    }
                    var sessionReceipt = ParseSessionReceipt(rawSessionReceipt, publicProfile);
                    VerifySessionReceipt(
                        sessionReceipt,
                        publicProfile,
                        sessionRequestId,
                        sessionIssuedAt,
                        request.ExpiresAtMs,
                        handshakeKey);
                    sessionKeys = DeriveSessionKeys(
                        pairingSecret,
                        publicProfile,
                        sessionReceipt.SessionId,
                        Base64Url.Encode(clientNonce),
                        sessionReceipt.ServerNonce,
                        [CatalogSummaryCapability]);

                    var requestCounter = 1L;
                    var catalogRequestId = request.RequestId;
                    var envelopeIssuedAt = _now();
                    var envelopeExpiresAt = Math.Min(
                        Math.Min(request.ExpiresAtMs, sessionReceipt.ExpiresAtMs),
                        checked(envelopeIssuedAt + MaximumRequestLifetimeMs));
                    if (envelopeExpiresAt <= envelopeIssuedAt)
                    {
                        throw RustyKioskV2ProviderException.Rejected(
                            "request_expired_during_exchange");
                    }
                    var requestNonce = Nonce(
                        Base64Url.Decode(sessionReceipt.RequestNoncePrefix, NoncePrefixBytes),
                        requestCounter);
                    var catalogPlaintext = Encoding.UTF8.GetBytes(
                        "{\"schema\":\"rusty.kiosk.catalog_request.v2\"}");
                    byte[]? requestCiphertext = null;
                    byte[]? requestAad = null;
                    try
                    {
                        var unsignedEnvelope = new RequestEnvelope(
                            sessionReceipt.SessionId,
                            catalogRequestId,
                            requestCounter,
                            envelopeIssuedAt,
                            envelopeExpiresAt,
                            CatalogSummaryCapability,
                            Base64Url.Encode(requestNonce),
                            string.Empty);
                        requestAad = RequestAad("POST", CatalogSummaryPath, unsignedEnvelope);
                        requestCiphertext = Encrypt(
                            sessionKeys.RequestKey,
                            requestNonce,
                            requestAad,
                            catalogPlaintext);
                        var requestEnvelope = unsignedEnvelope with
                        {
                            Ciphertext = Base64Url.Encode(requestCiphertext)
                        };
                        var rawRequestEnvelope = WriteRequestEnvelope(requestEnvelope);
                        if (rawRequestEnvelope.Length >
                            RustyKioskV2ProviderContract.MaximumRequestEnvelopeBytes)
                        {
                            throw RustyKioskV2ProviderException.Failed("request_envelope_oversized");
                        }
                        var requestDigest = Sha256HexConcat(requestAad, requestCiphertext);
                        var rawResponseEnvelope = await SendJsonAsync(
                            profile.Endpoint,
                            CatalogSummaryPath,
                            rawRequestEnvelope,
                            RustyKioskV2ProviderContract.MaximumResponseEnvelopeBytes,
                            timeout.Token);
                        var responseEnvelope = ParseResponseEnvelope(rawResponseEnvelope);
                        ValidateResponseEnvelope(
                            responseEnvelope,
                            sessionReceipt,
                            catalogRequestId,
                            requestCounter,
                            requestDigest,
                            envelopeIssuedAt,
                            request.ExpiresAtMs);
                        var responseNonce = Base64Url.Decode(responseEnvelope.Nonce, NonceBytes);
                        var expectedResponseNonce = Nonce(
                            Base64Url.Decode(
                                sessionReceipt.ResponseNoncePrefix,
                                NoncePrefixBytes),
                            responseEnvelope.ResponseCounter);
                        try
                        {
                            if (!CryptographicOperations.FixedTimeEquals(
                                    responseNonce,
                                    expectedResponseNonce))
                            {
                                throw RustyKioskV2ProviderException.Rejected(
                                    "response_nonce_binding_invalid");
                            }
                            var responseCiphertext = Base64Url.Decode(
                                responseEnvelope.Ciphertext,
                                maximumBytes: MaximumCatalogPlaintextBytes + TagBytes);
                            byte[]? ownerCatalog = null;
                            try
                            {
                                ownerCatalog = Decrypt(
                                    sessionKeys.ResponseKey,
                                    responseNonce,
                                    ResponseAad(responseEnvelope),
                                    responseCiphertext);
                                ValidateOwnerCatalog(
                                    ownerCatalog,
                                    publicProfile.OwnerEpoch,
                                    request.IssuedAtMs,
                                    _now());
                                var replayIdentity = FramedReplayIdentity(
                                    rawSessionReceipt,
                                    rawRequestEnvelope,
                                    rawResponseEnvelope);
                                return new RustyKioskV2VerifiedCatalogExchange(
                                    rawRequestEnvelope,
                                    rawResponseEnvelope,
                                    ownerCatalog,
                                    rawSessionReceipt,
                                    catalogRequestId,
                                    replayIdentity,
                                    request.CapabilityId,
                                    request.CapabilityEvidenceRevision,
                                    GrantRevision(publicProfile.SummaryGrantEpoch),
                                    request.RouteId,
                                    request.DeviceId,
                                    request.IdentityRevision,
                                    publicProfile.OwnerEpoch,
                                    request.IssuedAtMs,
                                    request.ExpiresAtMs,
                                    request.Scopes);
                            }
                            catch
                            {
                                if (ownerCatalog is not null)
                                {
                                    CryptographicOperations.ZeroMemory(ownerCatalog);
                                }
                                throw;
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(responseCiphertext);
                            }
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(responseNonce);
                            CryptographicOperations.ZeroMemory(expectedResponseNonce);
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(catalogPlaintext);
                        CryptographicOperations.ZeroMemory(requestNonce);
                        if (requestCiphertext is not null)
                        {
                            CryptographicOperations.ZeroMemory(requestCiphertext);
                        }
                        if (requestAad is not null)
                        {
                            CryptographicOperations.ZeroMemory(requestAad);
                        }
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clientNonce);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pairingCode);
                if (pairingSecret is not null)
                {
                    CryptographicOperations.ZeroMemory(pairingSecret);
                }
                if (handshakeKey is not null)
                {
                    CryptographicOperations.ZeroMemory(handshakeKey);
                }
                sessionKeys?.Dispose();
            }
        }
        catch (RustyKioskV2ProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw RustyKioskV2ProviderException.Failed("provider_timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            IOException or
            CryptographicException or
            JsonException or
            InvalidOperationException or
            OverflowException)
        {
            throw RustyKioskV2ProviderException.Failed("provider_exchange_failed");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<PublicProfile> FetchPublicProfileAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, ContractPath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        RequireOkJson(response);
        var bytes = await ReadBoundedAsync(response, MaximumContractBytes, cancellationToken);
        try
        {
            return ParsePublicProfile(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<byte[]> SendJsonAsync(
        Uri endpoint,
        string path,
        byte[] body,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path))
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        RequireOkJson(response);
        return await ReadBoundedAsync(response, maximumResponseBytes, cancellationToken);
    }

    private static void RequireOkJson(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentType?.MediaType is not "application/json")
        {
            throw RustyKioskV2ProviderException.Rejected("kiosk_response_rejected");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is null or < 1 ||
            contentLength > maximumBytes)
        {
            throw RustyKioskV2ProviderException.Rejected("kiosk_response_size_invalid");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream((int)contentLength.Value);
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    break;
                }
                if (output.Length + count > maximumBytes)
                {
                    throw RustyKioskV2ProviderException.Rejected("kiosk_response_size_invalid");
                }
                output.Write(buffer, 0, count);
            }
            if (output.Length != response.Content.Headers.ContentLength)
            {
                throw RustyKioskV2ProviderException.Rejected("kiosk_response_size_invalid");
            }
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static PublicProfile ParsePublicProfile(ReadOnlySpan<byte> bytes)
    {
        using var document = ParseStrict(bytes, 4);
        var root = document.RootElement;
        StrictJson.RequireExactObject(
            root,
            [
                "schema", "profile", "session_open_schema", "request_envelope_schema",
                "response_envelope_schema", "kdf", "aead", "key_bits", "nonce_bytes",
                "session_lifetime_ms", "request_lifetime_ms", "pairing_id", "key_epoch",
                "owner_epoch", "kdf_salt", "catalog_summary_granted",
                "catalog_summary_grant_epoch", "catalog_detail_granted",
                "catalog_detail_grant_epoch", "available_capabilities",
                "defined_but_inactive_capabilities", "launch_issue_schema",
                "launch_execute_schema", "normal_launch_scope", "launch_authority_active",
                "direct_v1_catalog_allowed", "direct_v1_launch_allowed",
                "catalog_payload_cleartext", "arbitrary_intents", "arbitrary_targets"
            ]);
        if (StrictJson.RequiredString(root, "schema", 128) != ProfileSchema ||
            StrictJson.RequiredString(root, "profile", 128) != ProfileName ||
            StrictJson.RequiredString(root, "session_open_schema", 128) != SessionOpenSchema ||
            StrictJson.RequiredString(root, "request_envelope_schema", 128) != RequestEnvelopeSchema ||
            StrictJson.RequiredString(root, "response_envelope_schema", 128) != ResponseEnvelopeSchema ||
            StrictJson.RequiredString(root, "kdf", 64) != "HKDF-SHA256" ||
            StrictJson.RequiredString(root, "aead", 64) != "AES-256-GCM" ||
            StrictJson.RequiredPositiveInt64(root, "key_bits") != 256 ||
            StrictJson.RequiredPositiveInt64(root, "nonce_bytes") != NonceBytes ||
            StrictJson.RequiredPositiveInt64(root, "session_lifetime_ms") != MaximumSessionLifetimeMs ||
            StrictJson.RequiredPositiveInt64(root, "request_lifetime_ms") != MaximumRequestLifetimeMs ||
            StrictJson.RequiredBoolean(root, "launch_authority_active") ||
            StrictJson.RequiredBoolean(root, "direct_v1_catalog_allowed") ||
            StrictJson.RequiredBoolean(root, "direct_v1_launch_allowed") ||
            StrictJson.RequiredBoolean(root, "catalog_payload_cleartext") ||
            StrictJson.RequiredBoolean(root, "arbitrary_intents") ||
            StrictJson.RequiredBoolean(root, "arbitrary_targets") ||
            StrictJson.RequiredString(root, "launch_issue_schema", 128) !=
                "rusty.kiosk.launch_reference_issue.v1" ||
            StrictJson.RequiredString(root, "launch_execute_schema", 128) !=
                "rusty.kiosk.launch_reference_execute.v1")
        {
            throw RustyKioskV2ProviderException.Rejected("kiosk_contract_invalid");
        }
        var summaryGranted = StrictJson.RequiredBoolean(root, "catalog_summary_granted");
        var detailGranted = StrictJson.RequiredBoolean(root, "catalog_detail_granted");
        if (detailGranted && !summaryGranted)
        {
            throw RustyKioskV2ProviderException.Rejected("kiosk_contract_invalid");
        }
        var available = StrictJson.RequiredStringArray(root, "available_capabilities", 0, 2, 64);
        var inactive = StrictJson.RequiredStringArray(
            root,
            "defined_but_inactive_capabilities",
            1,
            1,
            64);
        if (!inactive.SequenceEqual(["app-launch"], StringComparer.Ordinal) ||
            StrictJson.RequiredString(root, "normal_launch_scope", 128) !=
                RustyKioskV2ProviderContract.AppLaunchScope)
        {
            throw RustyKioskV2ProviderException.Rejected("kiosk_contract_invalid");
        }
        var saltText = StrictJson.RequiredString(root, "kdf_salt", 64);
        var salt = Base64Url.Decode(saltText, KdfSaltBytes);
        CryptographicOperations.ZeroMemory(salt);
        var expectedAvailable = new List<string>();
        if (summaryGranted)
        {
            expectedAvailable.Add(CatalogSummaryCapability);
        }
        if (detailGranted)
        {
            expectedAvailable.Add("catalog-detail");
        }
        if (!available.SequenceEqual(expectedAvailable, StringComparer.Ordinal))
        {
            throw RustyKioskV2ProviderException.Rejected("kiosk_contract_invalid");
        }
        return new PublicProfile(
            StrictJson.RequiredKioskToken(root, "pairing_id"),
            StrictJson.RequiredKioskToken(root, "key_epoch"),
            StrictJson.RequiredKioskOwnerEpoch(root, "owner_epoch"),
            saltText,
            summaryGranted,
            StrictJson.RequiredKioskToken(root, "catalog_summary_grant_epoch"),
            detailGranted,
            StrictJson.RequiredKioskToken(root, "catalog_detail_grant_epoch"),
            available);
    }

    private static byte[] BuildSessionOpenRequest(
        PublicProfile profile,
        string requestId,
        long issuedAtMs,
        long expiresAtMs,
        string clientNonce,
        byte[] handshakeKey)
    {
        var proofParts = new[]
        {
            SessionOpenSchema, ProfileName, profile.PairingId, profile.KeyEpoch,
            profile.OwnerEpoch, profile.SummaryGrantEpoch, profile.DetailGrantEpoch,
            requestId, issuedAtMs.ToString(CultureInfo.InvariantCulture),
            expiresAtMs.ToString(CultureInfo.InvariantCulture), CatalogSummaryCapability,
            clientNonce
        };
        var proof = Proof(handshakeKey, proofParts);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", SessionOpenSchema);
            writer.WriteString("profile", ProfileName);
            writer.WriteString("pairing_id", profile.PairingId);
            writer.WriteString("key_epoch", profile.KeyEpoch);
            writer.WriteString("owner_epoch", profile.OwnerEpoch);
            writer.WriteString("catalog_summary_grant_epoch", profile.SummaryGrantEpoch);
            writer.WriteString("catalog_detail_grant_epoch", profile.DetailGrantEpoch);
            writer.WriteString("request_id", requestId);
            writer.WriteNumber("issued_at_ms", issuedAtMs);
            writer.WriteNumber("expires_at_ms", expiresAtMs);
            writer.WriteStartArray("capabilities");
            writer.WriteStringValue(CatalogSummaryCapability);
            writer.WriteEndArray();
            writer.WriteString("client_nonce", clientNonce);
            writer.WriteString("proof", proof);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static SessionReceipt ParseSessionReceipt(
        ReadOnlySpan<byte> bytes,
        PublicProfile profile)
    {
        using var document = ParseStrict(bytes, 3);
        var root = document.RootElement;
        StrictJson.RequireExactObject(
            root,
            [
                "schema", "profile", "pairing_id", "key_epoch", "owner_epoch",
                "catalog_summary_grant_epoch", "catalog_detail_grant_epoch",
                "request_id", "response_id", "session_id", "issued_at_ms",
                "expires_at_ms", "capabilities", "server_nonce",
                "request_nonce_prefix", "response_nonce_prefix", "proof"
            ]);
        if (StrictJson.RequiredString(root, "schema", 128) != SessionReceiptSchema ||
            StrictJson.RequiredString(root, "profile", 128) != ProfileName)
        {
            throw RustyKioskV2ProviderException.Rejected("session_receipt_invalid");
        }
        var capabilities = StrictJson.RequiredStringArray(root, "capabilities", 1, 1, 64);
        if (!capabilities.SequenceEqual([CatalogSummaryCapability], StringComparer.Ordinal))
        {
            throw RustyKioskV2ProviderException.Rejected("session_receipt_invalid");
        }
        return new SessionReceipt(
            StrictJson.RequiredKioskToken(root, "pairing_id"),
            StrictJson.RequiredKioskToken(root, "key_epoch"),
            StrictJson.RequiredKioskOwnerEpoch(root, "owner_epoch"),
            StrictJson.RequiredKioskToken(root, "catalog_summary_grant_epoch"),
            StrictJson.RequiredKioskToken(root, "catalog_detail_grant_epoch"),
            StrictJson.RequiredKioskRequestId(root, "request_id"),
            StrictJson.RequiredKioskRequestId(root, "response_id"),
            StrictJson.RequiredKioskToken(root, "session_id"),
            StrictJson.RequiredPositiveInt64(root, "issued_at_ms"),
            StrictJson.RequiredPositiveInt64(root, "expires_at_ms"),
            StrictJson.RequiredString(root, "server_nonce", 64),
            StrictJson.RequiredString(root, "request_nonce_prefix", 16),
            StrictJson.RequiredString(root, "response_nonce_prefix", 16),
            StrictJson.RequiredSha256(root, "proof"));
    }

    private void VerifySessionReceipt(
        SessionReceipt receipt,
        PublicProfile profile,
        string requestId,
        long requestIssuedAt,
        long outerExpiresAt,
        byte[] handshakeKey)
    {
        var serverNonce = Base64Url.Decode(receipt.ServerNonce, HandshakeNonceBytes);
        var requestPrefix = Base64Url.Decode(receipt.RequestNoncePrefix, NoncePrefixBytes);
        var responsePrefix = Base64Url.Decode(receipt.ResponseNoncePrefix, NoncePrefixBytes);
        CryptographicOperations.ZeroMemory(serverNonce);
        CryptographicOperations.ZeroMemory(requestPrefix);
        CryptographicOperations.ZeroMemory(responsePrefix);
        var now = _now();
        if (receipt.PairingId != profile.PairingId ||
            receipt.KeyEpoch != profile.KeyEpoch ||
            receipt.OwnerEpoch != profile.OwnerEpoch ||
            receipt.SummaryGrantEpoch != profile.SummaryGrantEpoch ||
            receipt.DetailGrantEpoch != profile.DetailGrantEpoch ||
            receipt.RequestId != requestId ||
            receipt.IssuedAtMs < requestIssuedAt - 5_000 ||
            receipt.IssuedAtMs > now + 5_000 ||
            receipt.ExpiresAtMs < receipt.IssuedAtMs ||
            receipt.ExpiresAtMs < now ||
            receipt.ExpiresAtMs > outerExpiresAt ||
            receipt.ExpiresAtMs - receipt.IssuedAtMs > MaximumSessionLifetimeMs)
        {
            throw RustyKioskV2ProviderException.Rejected("session_receipt_binding_invalid");
        }
        var parts = new[]
        {
            SessionReceiptSchema, ProfileName, receipt.PairingId, receipt.KeyEpoch,
            receipt.OwnerEpoch, receipt.SummaryGrantEpoch, receipt.DetailGrantEpoch,
            receipt.RequestId, receipt.ResponseId, receipt.SessionId,
            receipt.IssuedAtMs.ToString(CultureInfo.InvariantCulture),
            receipt.ExpiresAtMs.ToString(CultureInfo.InvariantCulture),
            CatalogSummaryCapability, receipt.ServerNonce, receipt.RequestNoncePrefix,
            receipt.ResponseNoncePrefix
        };
        if (!VerifyProof(handshakeKey, parts, receipt.Proof))
        {
            throw RustyKioskV2ProviderException.Rejected("session_receipt_proof_invalid");
        }
    }

    private static ResponseEnvelope ParseResponseEnvelope(ReadOnlySpan<byte> bytes)
    {
        using var document = ParseStrict(bytes, 3);
        var root = document.RootElement;
        StrictJson.RequireExactObject(
            root,
            [
                "schema", "profile", "session_id", "request_id", "response_id",
                "request_counter", "request_digest", "response_counter", "issued_at_ms",
                "expires_at_ms", "capability", "nonce", "ciphertext"
            ]);
        if (StrictJson.RequiredString(root, "schema", 128) != ResponseEnvelopeSchema ||
            StrictJson.RequiredString(root, "profile", 128) != ProfileName)
        {
            throw RustyKioskV2ProviderException.Rejected("response_envelope_invalid");
        }
        return new ResponseEnvelope(
            StrictJson.RequiredKioskToken(root, "session_id"),
            StrictJson.RequiredKioskRequestId(root, "request_id"),
            StrictJson.RequiredKioskRequestId(root, "response_id"),
            StrictJson.RequiredPositiveInt64(root, "request_counter"),
            StrictJson.RequiredSha256(root, "request_digest"),
            StrictJson.RequiredPositiveInt64(root, "response_counter"),
            StrictJson.RequiredPositiveInt64(root, "issued_at_ms"),
            StrictJson.RequiredPositiveInt64(root, "expires_at_ms"),
            StrictJson.RequiredString(root, "capability", 64),
            StrictJson.RequiredString(root, "nonce", 32),
            StrictJson.RequiredString(root, "ciphertext", 1_048_600));
    }

    private void ValidateResponseEnvelope(
        ResponseEnvelope response,
        SessionReceipt session,
        string requestId,
        long requestCounter,
        string requestDigest,
        long requestIssuedAt,
        long outerExpiresAt)
    {
        var now = _now();
        if (response.SessionId != session.SessionId ||
            response.RequestId != requestId ||
            response.RequestCounter != requestCounter ||
            response.RequestDigest != requestDigest ||
            response.ResponseCounter != 1 ||
            response.Capability != CatalogSummaryCapability ||
            response.IssuedAtMs < requestIssuedAt ||
            response.IssuedAtMs > now + 5_000 ||
            response.ExpiresAtMs < now ||
            response.ExpiresAtMs > session.ExpiresAtMs ||
            response.ExpiresAtMs > outerExpiresAt ||
            response.ExpiresAtMs < response.IssuedAtMs ||
            response.ExpiresAtMs - response.IssuedAtMs > MaximumRequestLifetimeMs)
        {
            throw RustyKioskV2ProviderException.Rejected("response_envelope_binding_invalid");
        }
    }

    private static void ValidateOwnerCatalog(
        ReadOnlySpan<byte> ownerCatalog,
        string ownerEpoch,
        long outerIssuedAt,
        long receivedAt)
    {
        if (ownerCatalog.Length is 0 or > MaximumCatalogPlaintextBytes)
        {
            throw RustyKioskV2ProviderException.Rejected("owner_catalog_size_invalid");
        }
        var catalogCopy = ownerCatalog.ToArray();
        try
        {
            using var document = JsonDocument.Parse(
                catalogCopy,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            var root = document.RootElement;
            StrictJson.RequireExactObject(
                root,
                [
                    "schema", "owner_epoch", "catalog_revision", "snapshot_digest",
                    "observed_at_ms", "fresh_until_ms", "tag_revision",
                    "installed_observation_revision", "search_semantics", "complete",
                    "truncated", "total_candidate_count", "permission_limited_count",
                    "entries"
                ]);
            if (StrictJson.RequiredString(root, "schema", 128) != CatalogSnapshotSchema ||
                StrictJson.RequiredKioskOwnerEpoch(root, "owner_epoch") != ownerEpoch ||
                StrictJson.RequiredString(root, "search_semantics", 128) !=
                    "rusty.kiosk.catalog_search.v1")
            {
                throw RustyKioskV2ProviderException.Rejected("owner_catalog_binding_invalid");
            }
            StrictJson.RequiredSha256(root, "catalog_revision");
            StrictJson.RequiredSha256(root, "snapshot_digest");
            StrictJson.RequiredSha256(root, "tag_revision");
            StrictJson.RequiredSha256(root, "installed_observation_revision");
            var observedAt = StrictJson.RequiredPositiveInt64(root, "observed_at_ms");
            var freshUntil = StrictJson.RequiredPositiveInt64(root, "fresh_until_ms");
            var complete = StrictJson.RequiredBoolean(root, "complete");
            var truncated = StrictJson.RequiredBoolean(root, "truncated");
            var total = StrictJson.RequiredNonNegativeInt32(root, "total_candidate_count");
            var limited = StrictJson.RequiredNonNegativeInt32(root, "permission_limited_count");
            var entries = StrictJson.RequiredProperty(root, "entries");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 500)
            {
                throw new InvalidOperationException();
            }
            foreach (var entry in entries.EnumerateArray())
            {
                StrictJson.RequireExactObject(
                    entry,
                    [
                        "catalog_entry_id", "entry_digest", "app_identity",
                        "disambiguator", "label", "tags", "installed", "launchable",
                        "source"
                    ]);
                StrictJson.RequiredSha256(entry, "catalog_entry_id");
                StrictJson.RequiredSha256(entry, "entry_digest");
                StrictJson.RequiredSha256(entry, "app_identity");
                StrictJson.RequiredString(entry, "disambiguator", 12);
                StrictJson.RequiredString(entry, "label", 160);
                StrictJson.RequiredStringArray(entry, "tags", 0, 64, 40);
                if (!StrictJson.RequiredBoolean(entry, "installed") ||
                    !StrictJson.RequiredBoolean(entry, "launchable"))
                {
                    throw new InvalidOperationException();
                }
                StrictJson.RequiredString(entry, "source", 64);
            }
            if (observedAt < outerIssuedAt ||
                observedAt > receivedAt + 5_000 ||
                freshUntil <= observedAt ||
                freshUntil - observedAt != 300_000 ||
                freshUntil < receivedAt ||
                total < entries.GetArrayLength() ||
                limited > total ||
                (complete && (truncated || limited != 0 || total > 500)) ||
                truncated != (!complete && total > 500))
            {
                throw RustyKioskV2ProviderException.Rejected("owner_catalog_freshness_invalid");
            }
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
            throw RustyKioskV2ProviderException.Rejected("owner_catalog_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(catalogCopy);
        }
    }

    private static JsonDocument ParseStrict(ReadOnlySpan<byte> bytes, int maxDepth)
    {
        try
        {
            return JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = maxDepth
                });
        }
        catch (JsonException)
        {
            throw RustyKioskV2ProviderException.Rejected("kiosk_json_invalid");
        }
    }

    private static byte[] WriteRequestEnvelope(RequestEnvelope envelope)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", RequestEnvelopeSchema);
            writer.WriteString("profile", ProfileName);
            writer.WriteString("session_id", envelope.SessionId);
            writer.WriteString("request_id", envelope.RequestId);
            writer.WriteNumber("counter", envelope.Counter);
            writer.WriteNumber("issued_at_ms", envelope.IssuedAtMs);
            writer.WriteNumber("expires_at_ms", envelope.ExpiresAtMs);
            writer.WriteString("capability", envelope.Capability);
            writer.WriteString("nonce", envelope.Nonce);
            writer.WriteString("ciphertext", envelope.Ciphertext);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] PairingSecret(byte[] pairingCode, PublicProfile profile)
    {
        var salt = Base64Url.Decode(profile.KdfSalt, KdfSaltBytes);
        var context = Canonical(
            [ProfileName, "extract-v1", profile.PairingId, profile.KeyEpoch, profile.OwnerEpoch]);
        var extractSalt = Concat(context, salt);
        try
        {
            return HMACSHA256.HashData(extractSalt, pairingCode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(context);
            CryptographicOperations.ZeroMemory(extractSalt);
        }
    }

    private static byte[] HandshakeKey(byte[] pairingSecret, PublicProfile profile) =>
        HkdfExpand(
            pairingSecret,
            Canonical(
                [
                    ProfileName, "handshake-key-v1", profile.PairingId, profile.KeyEpoch,
                    profile.OwnerEpoch, profile.SummaryGrantEpoch, profile.DetailGrantEpoch
                ]),
            KeyBytes);

    private static SessionKeys DeriveSessionKeys(
        byte[] pairingSecret,
        PublicProfile profile,
        string sessionId,
        string clientNonce,
        string serverNonce,
        IReadOnlyList<string> capabilities)
    {
        var common = new[]
        {
            ProfileName, profile.PairingId, profile.KeyEpoch, profile.OwnerEpoch,
            profile.SummaryGrantEpoch, profile.DetailGrantEpoch, sessionId, clientNonce,
            serverNonce, string.Join(',', capabilities)
        };
        return new SessionKeys(
            HkdfExpand(
                pairingSecret,
                Canonical(["request-key-v1", .. common]),
                KeyBytes),
            HkdfExpand(
                pairingSecret,
                Canonical(["response-key-v1", .. common]),
                KeyBytes));
    }

    private static byte[] RequestAad(string method, string path, RequestEnvelope envelope) =>
        Canonical(
            [
                ProfileName, "request", RequestEnvelopeSchema, method.ToUpperInvariant(), path,
                envelope.SessionId, envelope.RequestId,
                envelope.Counter.ToString(CultureInfo.InvariantCulture),
                envelope.IssuedAtMs.ToString(CultureInfo.InvariantCulture),
                envelope.ExpiresAtMs.ToString(CultureInfo.InvariantCulture),
                envelope.Capability, envelope.Nonce
            ]);

    private static byte[] ResponseAad(ResponseEnvelope envelope) =>
        Canonical(
            [
                ProfileName, "response", ResponseEnvelopeSchema, envelope.SessionId,
                envelope.RequestId, envelope.ResponseId,
                envelope.RequestCounter.ToString(CultureInfo.InvariantCulture),
                envelope.RequestDigest,
                envelope.ResponseCounter.ToString(CultureInfo.InvariantCulture),
                envelope.IssuedAtMs.ToString(CultureInfo.InvariantCulture),
                envelope.ExpiresAtMs.ToString(CultureInfo.InvariantCulture),
                envelope.Capability, envelope.Nonce
            ]);

    private static byte[] Nonce(byte[] prefix, long counter)
    {
        if (prefix.Length != NoncePrefixBytes || counter <= 0)
        {
            throw RustyKioskV2ProviderException.Rejected("nonce_invalid");
        }
        var nonce = new byte[NonceBytes];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(NoncePrefixBytes), counter);
        CryptographicOperations.ZeroMemory(prefix);
        return nonce;
    }

    private static byte[] Encrypt(byte[] key, byte[] nonce, byte[] aad, byte[] plaintext)
    {
        var output = new byte[plaintext.Length + TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(0, plaintext.Length),
            output.AsSpan(plaintext.Length, TagBytes),
            aad);
        return output;
    }

    private static byte[] Decrypt(byte[] key, byte[] nonce, byte[] aad, byte[] ciphertext)
    {
        if (ciphertext.Length < TagBytes)
        {
            throw RustyKioskV2ProviderException.Rejected("ciphertext_invalid");
        }
        var plaintext = new byte[ciphertext.Length - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(
                nonce,
                ciphertext.AsSpan(0, plaintext.Length),
                ciphertext.AsSpan(plaintext.Length, TagBytes),
                plaintext,
                aad);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw RustyKioskV2ProviderException.Rejected(
                "response_authentication_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    private static byte[] HkdfExpand(byte[] pseudoRandomKey, byte[] info, int length)
    {
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var written = 0;
        byte counter = 1;
        try
        {
            while (written < length)
            {
                var input = new byte[previous.Length + info.Length + 1];
                previous.CopyTo(input, 0);
                info.CopyTo(input, previous.Length);
                input[^1] = counter;
                var block = HMACSHA256.HashData(pseudoRandomKey, input);
                CryptographicOperations.ZeroMemory(input);
                if (previous.Length != 0)
                {
                    CryptographicOperations.ZeroMemory(previous);
                }
                previous = block;
                var count = Math.Min(block.Length, length - written);
                block.AsSpan(0, count).CopyTo(output.AsSpan(written));
                written += count;
                counter++;
            }
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(info);
            if (previous.Length != 0)
            {
                CryptographicOperations.ZeroMemory(previous);
            }
        }
    }

    private static string Proof(byte[] key, IReadOnlyList<string> parts)
    {
        var canonical = Canonical(parts);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(key, canonical)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static bool VerifyProof(
        byte[] key,
        IReadOnlyList<string> parts,
        string supplied)
    {
        var expected = Convert.FromHexString(Proof(key, parts));
        var actual = Convert.FromHexString(supplied);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static byte[] Canonical(IReadOnlyList<string> parts)
    {
        using var output = new MemoryStream();
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            var length = Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture));
            output.Write(length);
            output.WriteByte((byte)':');
            output.Write(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(length);
        }
        return output.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(static part => part.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Sha256HexConcat(params byte[][] parts)
    {
        var combined = Concat(parts);
        try
        {
            return Sha256Hex(combined);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(combined);
        }
    }

    private static string FramedReplayIdentity(params byte[][] documents)
    {
        var digests = documents.Select(static document => Sha256Hex(document)).ToArray();
        var framed = Canonical(digests);
        try
        {
            return Sha256Hex(framed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(framed);
        }
    }

    private static ulong GrantRevision(string grantEpoch)
    {
        var epochBytes = Encoding.UTF8.GetBytes(grantEpoch);
        var digest = SHA256.HashData(epochBytes);
        try
        {
            var value = BinaryPrimitives.ReadUInt64BigEndian(digest);
            return value == 0 ? 1UL : value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(epochBytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string RandomToken(int bytes)
    {
        var random = RandomBytes(bytes);
        try
        {
            return Base64Url.Encode(random);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
        }
    }

    private sealed record PublicProfile(
        string PairingId,
        string KeyEpoch,
        string OwnerEpoch,
        string KdfSalt,
        bool SummaryGranted,
        string SummaryGrantEpoch,
        bool DetailGranted,
        string DetailGrantEpoch,
        IReadOnlyList<string> AvailableCapabilities);

    private sealed record SessionReceipt(
        string PairingId,
        string KeyEpoch,
        string OwnerEpoch,
        string SummaryGrantEpoch,
        string DetailGrantEpoch,
        string RequestId,
        string ResponseId,
        string SessionId,
        long IssuedAtMs,
        long ExpiresAtMs,
        string ServerNonce,
        string RequestNoncePrefix,
        string ResponseNoncePrefix,
        string Proof);

    private sealed record RequestEnvelope(
        string SessionId,
        string RequestId,
        long Counter,
        long IssuedAtMs,
        long ExpiresAtMs,
        string Capability,
        string Nonce,
        string Ciphertext);

    private sealed record ResponseEnvelope(
        string SessionId,
        string RequestId,
        string ResponseId,
        long RequestCounter,
        string RequestDigest,
        long ResponseCounter,
        long IssuedAtMs,
        long ExpiresAtMs,
        string Capability,
        string Nonce,
        string Ciphertext);

    private sealed class SessionKeys(byte[] requestKey, byte[] responseKey) : IDisposable
    {
        public byte[] RequestKey { get; } = requestKey;
        public byte[] ResponseKey { get; } = responseKey;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(RequestKey);
            CryptographicOperations.ZeroMemory(ResponseKey);
        }
    }
}
