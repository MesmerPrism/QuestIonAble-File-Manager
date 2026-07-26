using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class RustyKioskV2CatalogProviderTests
{
    private const long Now = 1_900_000_000_000;
    private const string ProfileId = "profile.quest.lab.001";
    private const string DeviceId = "device.quest.lab.001";
    private const string OwnerEpoch = "OwnerEpoch_0000000000000001";
    private const string PairingCode = "0123-4567-89AB-CDEF-GHJK-MNPQ-RS";
    private static readonly Uri Endpoint = new("http://192.168.1.44:39873/");

    [Theory]
    [InlineData("verified", 0)]
    [InlineData("failed", 1)]
    [InlineData("rejected", 2)]
    [InlineData("unavailable", 3)]
    public void SubprocessStatus_HasExactStableExitCode(string status, int exitCode)
    {
        Assert.Equal(exitCode, RustyKioskV2ProviderContract.ExitCodeForStatus(status));
    }

    [Fact]
    public void SubprocessStatus_RejectsUnknownExitCodeMapping()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RustyKioskV2ProviderContract.ExitCodeForStatus("unknown"));
    }

    [Fact]
    public async Task SubprocessHost_RejectsEveryNonExactArgumentShapeBeforeInputOrProvider()
    {
        var providerFactoryCalls = 0;
        var host = new RustyKioskV2CatalogSubprocessHost(() =>
        {
            providerFactoryCalls++;
            throw new InvalidOperationException("Provider factory must remain unreachable.");
        });
        IReadOnlyList<string>[] rejectedArguments =
        [
            [],
            ["--help"],
            ["devices", "--json"],
            ["files", "list", "--serial", "must-not-run", "--path", "/sdcard", "--json"],
            ["apk", "list", "--serial", "must-not-run", "--json"],
            ["wifi", "connect", "--host", "127.0.0.1"],
            ["kiosk", "status", "--serial", "must-not-run", "--json"],
            ["kiosk-direct", "status", "--endpoint", "http://127.0.0.1:39873", "--json"],
            ["device", "status", "--serial", "must-not-run", "--json"],
            ["integration", "capabilities", "--json"],
            ["integration", "kiosk-v2-catalog"],
            ["integration", "kiosk-v2-catalog", "--json", "extra"],
            ["Integration", "kiosk-v2-catalog", "--json"],
            ["integration", "KIOSK-V2-CATALOG", "--json"],
            ["integration", "kiosk-v2-catalog", "--JSON"]
        ];
        const string expected =
            "{\"schema\":\"questionable.file_manager.fleet_kiosk_v2_catalog_response.v1\"," +
            "\"status\":\"rejected\",\"profile_id\":\"unavailable\"," +
            "\"request_id\":\"unavailable\",\"error_code\":\"provider_arguments_invalid\"}\n";

        foreach (var arguments in rejectedArguments)
        {
            await using var input = new ThrowOnReadStream();
            await using var output = new MemoryStream();

            var exitCode = await host.RunAsync(arguments, input, output);

            Assert.Equal(2, exitCode);
            Assert.Equal(expected, Encoding.UTF8.GetString(output.ToArray()));
        }
        Assert.Equal(0, providerFactoryCalls);
    }

    [Fact]
    public async Task SubprocessHost_ExactArgumentsProceedToStrictRequestParserOnly()
    {
        var providerFactoryCalls = 0;
        var host = new RustyKioskV2CatalogSubprocessHost(() =>
        {
            providerFactoryCalls++;
            throw new InvalidOperationException("Invalid JSON must not reach the provider.");
        });
        await using var input = new MemoryStream("{}"u8.ToArray());
        await using var output = new MemoryStream();

        var exitCode = await host.RunAsync(
            ["integration", "kiosk-v2-catalog", "--json"],
            input,
            output);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, providerFactoryCalls);
        Assert.Equal(
            "{\"schema\":\"questionable.file_manager.fleet_kiosk_v2_catalog_response.v1\"," +
            "\"status\":\"rejected\",\"profile_id\":\"unavailable\"," +
            "\"request_id\":\"unavailable\",\"error_code\":\"request_json_invalid\"}\n",
            Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Fetch_PerformsKioskV2ExchangeAndExportsOnlyClosedEvidence()
    {
        var handler = new KioskV2Handler(Now, detailGranted: true);
        using var provider = CreateProvider(handler);
        var request = Request();

        using var exchange = await provider.FetchAsync(ProfileId, request);

        Assert.True(handler.SessionProofVerified);
        Assert.True(handler.RequestAeadVerified);
        Assert.Equal(request.RequestId, exchange.ResponseRequestId);
        Assert.Equal(request.DeviceId, exchange.DeviceId);
        Assert.Equal(request.IdentityRevision, exchange.IdentityRevision);
        Assert.Equal(request.CapabilityEvidenceRevision, exchange.CapabilityEvidenceRevision);
        Assert.Equal(OwnerEpoch, exchange.KioskOwnerEpoch);
        Assert.Equal([RustyKioskV2ProviderContract.CatalogSummaryScope], exchange.Scopes);
        Assert.NotEqual(0UL, exchange.GrantRevision);
        Assert.Equal(handler.OwnerCatalog, exchange.OwnerCatalogJson);
        Assert.Equal(handler.RawSessionReceipt, exchange.OwnerGrantReceipt);
        Assert.Equal(FramedReplayIdentity(
            handler.RawSessionReceipt,
            handler.RawRequestEnvelope,
            handler.RawResponseEnvelope), exchange.ReplayIdentity);

        var wire = RustyKioskV2CatalogProviderResponse
            .Verified(ProfileId, request.RequestId, exchange)
            .ToUtf8Json();
        var json = Encoding.UTF8.GetString(wire);
        Assert.Contains("\"status\":\"verified\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Endpoint.ToString(), json, StringComparison.Ordinal);
        Assert.DoesNotContain(PairingCode, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Visible App", json, StringComparison.Ordinal);
        Assert.DoesNotContain("com.example.visible", json, StringComparison.Ordinal);
        Assert.DoesNotContain("manifold_revocation_barrier", json, StringComparison.Ordinal);
        CryptographicOperations.ZeroMemory(wire);
    }

    [Fact]
    public void KioskKotlinCryptoFixture_MatchesExactCrossLanguageVector()
    {
        // Fixed from the Kiosk DirectOperatorV2Crypto canonical/HKDF implementation.
        var fixture = KioskV2Handler.FixedFixture();

        Assert.Equal(
            "b776e1c3c11e8dbbfda2a5115145fa170e905fa4915c57b92dbd549bfa641ac6",
            Convert.ToHexString(fixture.PairingSecret).ToLowerInvariant());
        Assert.Equal(
            "6b564f4965f0fdf67500dc5c767289d2d9a13b4583b9ecfde0d6426e6c73a399",
            Convert.ToHexString(fixture.HandshakeKey).ToLowerInvariant());
        Assert.Equal(
            "cc973422180a74ea20c8e0a453fa1234e6faa5829fe376df6069c2d2cdeee45a",
            fixture.HandshakeProof);
        Assert.Equal(
            "853abd9519b06a37555264571d8330b1ebb5455182f0b2d4f2772028fa5fbce2",
            Convert.ToHexString(fixture.RequestKey).ToLowerInvariant());
        Assert.Equal(
            "5f07a0d4eca746e7edafe03b89ce3f654ee807239ffd38b904fefe5446fa1314",
            Convert.ToHexString(fixture.ResponseKey).ToLowerInvariant());
        Assert.Equal("AQIDBAAAAAAAAAAB", Convert.ToBase64String(fixture.RequestNonce)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        Assert.Equal(
            "b553553c4b9bd2e8ce0cff81f6cf1eda30129aad88e5e9f065f7f7649a4339f2",
            Sha256Hex(fixture.RequestAad));
        Assert.Equal(
            "e3b3099ec016ebb83d1f708ee4f0c0e228e85d9cf9731db5fb87e718d89b255b" +
            "a7b600dc4c9b3b4faecbada128043004c76fc9c0fe9837d7767c7b",
            Convert.ToHexString(fixture.RequestCiphertext).ToLowerInvariant());
        Assert.Equal(
            "1909568d8aa3e65498ff9a68d47e83a7b1d558af333c64664c37179b68a3038e",
            fixture.RequestDigest);
        Assert.Equal("BQYHCAAAAAAAAAAB", Convert.ToBase64String(fixture.ResponseNonce)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        Assert.Equal(
            "951609bb1ea65429186ec5c0ccbc3f5f17b2b747ff8ae1cf9c0a3bd12560fd95",
            Sha256Hex(fixture.ResponseAad));
        Assert.Equal(
            "d44e5c512305f83a934f7e6250aae41a637d1e983872c56f059a6bd6b7694d99" +
            "457dc385d7e39e22aaaf2ce0716b42be375b0dd55c273528f135bf7cf93248828" +
            "a90b660826d9a9c86a5ff548187d2fca49b495cf4d773157b9dd1d90a01c3135" +
            "ebcc7ced532857e5ba56055dc7a967657c2713fcc6df5a5afd7a9e7ed7eb1367" +
            "a58d5966f4ad54dc05272ff85ee37c737248f318fd5cafc7a9969bd22fd7dde6" +
            "eca0fb5761e",
            Convert.ToHexString(fixture.ResponseCiphertext).ToLowerInvariant());
        Assert.Equal(fixture.OwnerCatalog, KioskV2Handler.DecryptFixtureResponse(fixture));
        fixture.Dispose();
    }

    [Theory]
    [InlineData(Tamper.SessionProof, "session_receipt_proof_invalid")]
    [InlineData(Tamper.RequestDigest, "response_envelope_binding_invalid")]
    [InlineData(Tamper.ResponseNonce, "response_nonce_binding_invalid")]
    [InlineData(Tamper.ResponseCiphertext, "response_authentication_invalid")]
    [InlineData(Tamper.ResponseCounter, "response_envelope_binding_invalid")]
    [InlineData(Tamper.SessionExpiryOrder, "session_receipt_binding_invalid")]
    [InlineData(Tamper.ResponseIssuedBeforeRequest, "response_envelope_binding_invalid")]
    [InlineData(Tamper.OwnerDetailLeak, "owner_catalog_invalid")]
    public async Task Fetch_FailsClosedOnTamperedSessionOrEnvelope(
        Tamper tamper,
        string expectedCode)
    {
        using var provider = CreateProvider(new KioskV2Handler(Now, tamper: tamper));

        var exception = await Assert.ThrowsAsync<RustyKioskV2ProviderException>(
            () => provider.FetchAsync(ProfileId, Request()));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain(PairingCode, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Endpoint.Host, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_RejectsExpiredFutureAndProfileDeviceMismatchBeforeNetwork()
    {
        var handler = new KioskV2Handler(Now);
        using var provider = CreateProvider(handler);
        foreach (var request in new[]
        {
            Request(issuedAt: Now - 29_000, expiresAt: Now - 1),
            Request(issuedAt: Now + 5_001, expiresAt: Now + 20_000)
        })
        {
            var exception = await Assert.ThrowsAsync<RustyKioskV2ProviderException>(
                () => provider.FetchAsync(ProfileId, request));
            Assert.Equal("request_freshness_invalid", exception.Code);
        }

        using var wrongProfileProvider = new RustyKioskV2CatalogProvider(
            new FakeProfileStore("device.quest.other"),
            new HttpClient(handler),
            () => Now);
        var mismatch = await Assert.ThrowsAsync<RustyKioskV2ProviderException>(
            () => wrongProfileProvider.FetchAsync(ProfileId, Request()));
        Assert.Equal("profile_device_mismatch", mismatch.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fetch_BootstrapsAuthenticatedOwnerButRejectsPinnedRotation()
    {
        foreach (var bootstrap in new[]
        {
            RustyKioskV2CatalogProviderRequest.Parse(
                RequestJson(expectedOwnerEpoch: null, omitExpectedOwnerEpoch: true)),
            RustyKioskV2CatalogProviderRequest.Parse(
                RequestJson(expectedOwnerEpoch: null))
        })
        {
            using var provider = CreateProvider(new KioskV2Handler(Now));
            using var exchange = await provider.FetchAsync(ProfileId, bootstrap);
            Assert.Equal(OwnerEpoch, exchange.KioskOwnerEpoch);
        }

        using var pinnedProvider = CreateProvider(new KioskV2Handler(Now));
        var mismatch = await Assert.ThrowsAsync<RustyKioskV2ProviderException>(
            () => pinnedProvider.FetchAsync(
                ProfileId,
                Request(expectedOwnerEpoch: "OwnerEpoch_0000000000000002")));
        Assert.Equal("kiosk_owner_epoch_mismatch", mismatch.Code);
    }

    [Fact]
    public void RequestParser_RejectsBarrierDetailLaunchDuplicateTrailingAndLongLifetime()
    {
        var valid = Encoding.UTF8.GetString(RequestJson());
        var damaged = new[]
        {
            valid.Replace(
                "\"expires_at_ms\":1900000025000",
                "\"expires_at_ms\":1900000025000,\"manifold_revocation_barrier\":{}",
                StringComparison.Ordinal),
            valid.Replace(
                "\"kiosk.catalog-summary\"",
                "\"kiosk.catalog-detail\"",
                StringComparison.Ordinal),
            valid.Replace(
                "\"kiosk.catalog-summary\"",
                "\"kiosk.launch-catalog-entry-normal\"",
                StringComparison.Ordinal),
            valid.Replace(
                "\"request_id\":\"catalog-request-0001\"",
                "\"request_id\":\"catalog-request-0001\",\"request_id\":\"duplicate-request\"",
                StringComparison.Ordinal),
            valid + "{}",
            valid.Replace(
                "\"expires_at_ms\":1900000025000",
                "\"expires_at_ms\":1900000030001",
                StringComparison.Ordinal)
        };

        foreach (var json in damaged)
        {
            Assert.Throws<RustyKioskV2ProviderException>(
                () => RustyKioskV2CatalogProviderRequest.Parse(Encoding.UTF8.GetBytes(json)));
        }
    }

    [Fact]
    public async Task Fetch_RevalidatesConstructedCoreRequestsBeforeStoreOrNetwork()
    {
        var handler = new KioskV2Handler(Now);
        using var provider = CreateProvider(handler);
        var valid = Request();
        var invalid = new[]
        {
            valid with { RequestId = "request.with.dot" },
            valid with { ProfileId = "bad profile" },
            valid with { DeviceId = string.Empty },
            valid with { IdentityRevision = 0 },
            valid with { CapabilityEvidenceRevision = 0 },
            valid with { ExpectedOwnerEpoch = "owner.with.dot.0001" },
            valid with { Scopes = [RustyKioskV2ProviderContract.CatalogDetailScope] },
            valid with { IssuedAtMs = 0 }
        };

        foreach (var request in invalid)
        {
            var exception = await Assert.ThrowsAsync<RustyKioskV2ProviderException>(
                () => provider.FetchAsync(request.ProfileId, request));
            Assert.Equal("request_binding_invalid", exception.Code);
        }
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void VerifiedExchange_DisposeClearsAllExportedEvidence()
    {
        var exchange = new RustyKioskV2VerifiedCatalogExchange(
            [1], [2], [3], [4], "request0001", "replay0001",
            RustyKioskV2ProviderContract.CapabilityId, 1, 1,
            RustyKioskV2ProviderContract.RouteId, DeviceId, 1, OwnerEpoch,
            Now, Now + 1, [RustyKioskV2ProviderContract.CatalogSummaryScope]);

        exchange.Dispose();

        Assert.Equal(0, exchange.RequestEnvelope[0]);
        Assert.Equal(0, exchange.ResponseEnvelope[0]);
        Assert.Equal(0, exchange.OwnerCatalogJson[0]);
        Assert.Equal(0, exchange.OwnerGrantReceipt[0]);
    }

    [Fact]
    public void FailureResponse_IsStrictBoundedAndContainsNoSensitiveMaterial()
    {
        var bytes = RustyKioskV2CatalogProviderResponse.Failure(
            "unavailable",
            ProfileId,
            "catalog-request-0001",
            "provider_profile_unavailable").ToUtf8Json();
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Equal(
            "{\"schema\":\"questionable.file_manager.fleet_kiosk_v2_catalog_response.v1\"," +
            "\"status\":\"unavailable\",\"profile_id\":\"profile.quest.lab.001\"," +
            "\"request_id\":\"catalog-request-0001\",\"error_code\":\"provider_profile_unavailable\"}",
            json);
        Assert.DoesNotContain(PairingCode, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Endpoint.Host, json, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponseSerializer_RevalidatesDirectlyConstructedRecords()
    {
        var bypass = new RustyKioskV2CatalogProviderResponse(
            "wrong.schema",
            "verified",
            ProfileId,
            "catalog-request-0001",
            null,
            null);

        var exception = Assert.Throws<RustyKioskV2ProviderException>(bypass.ToUtf8Json);

        Assert.Equal("response_binding_invalid", exception.Code);
    }

    private static RustyKioskV2CatalogProvider CreateProvider(KioskV2Handler handler) =>
        new(
            new FakeProfileStore(DeviceId),
            new HttpClient(handler),
            () => Now);

    private static RustyKioskV2CatalogProviderRequest Request(
        long? issuedAt = null,
        long? expiresAt = null,
        string? expectedOwnerEpoch = OwnerEpoch) =>
        RustyKioskV2CatalogProviderRequest.Parse(
            RequestJson(
                issuedAt ?? Now - 1_000,
                expiresAt ?? Now + 25_000,
                expectedOwnerEpoch));

    private static byte[] RequestJson(
        long issuedAt = Now - 1_000,
        long expiresAt = Now + 25_000,
        string? expectedOwnerEpoch = OwnerEpoch,
        bool omitExpectedOwnerEpoch = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", RustyKioskV2ProviderContract.RequestSchema);
            writer.WriteString("profile_id", ProfileId);
            writer.WriteString("request_id", "catalog-request-0001");
            writer.WriteString("device_id", DeviceId);
            writer.WriteNumber("identity_revision", 7);
            writer.WriteString("capability_id", RustyKioskV2ProviderContract.CapabilityId);
            writer.WriteNumber("capability_evidence_revision", 11);
            writer.WriteString("route_id", RustyKioskV2ProviderContract.RouteId);
            if (!omitExpectedOwnerEpoch)
            {
                writer.WriteString("expected_owner_epoch", expectedOwnerEpoch);
            }
            writer.WriteStartArray("scopes");
            writer.WriteStringValue(RustyKioskV2ProviderContract.CatalogSummaryScope);
            writer.WriteEndArray();
            writer.WriteNumber("issued_at_ms", issuedAt);
            writer.WriteNumber("expires_at_ms", expiresAt);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private sealed class FakeProfileStore(string deviceId) : IRustyKioskV2ProviderProfileStore
    {
        public RustyKioskV2ProviderProfile Open(string profileId) =>
            new(profileId, Endpoint, PairingCode.ToCharArray(), deviceId);
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Rejected arguments must not read stdin.");
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Rejected arguments must not read stdin.");
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    public enum Tamper
    {
        None,
        SessionProof,
        RequestDigest,
        ResponseNonce,
        ResponseCiphertext,
        ResponseCounter,
        SessionExpiryOrder,
        ResponseIssuedBeforeRequest,
        OwnerDetailLeak
    }

    private sealed class KioskV2Handler : HttpMessageHandler
    {
        private const string PairingId = "PairingIdentity_000000000001";
        private const string KeyEpoch = "KeyEpoch_000000000000000001";
        private const string SummaryEpoch = "SummaryGrant_000000000000001";
        private const string DetailEpoch = "DetailGrant_000000000000000";
        private const string SessionId = "SessionIdentity_000000000001";
        private const string SessionResponseId = "SessionResponse_00000000001";
        private const string CatalogResponseId = "CatalogResponse_00000000001";
        private const string ClientCapability = "catalog-summary";
        private const string Profile = "rusty-kiosk-direct-v2";
        private const string SessionOpenSchema = "rusty.kiosk.direct_operator.session_open.v2";
        private const string SessionReceiptSchema =
            "rusty.kiosk.direct_operator.session_open_receipt.v2";
        private const string RequestSchema = "rusty.kiosk.direct_operator.request_envelope.v2";
        private const string ResponseSchema = "rusty.kiosk.direct_operator.response_envelope.v2";
        private readonly long _now;
        private readonly bool _detailGranted;
        private readonly Tamper _tamper;
        private readonly byte[] _kdfSalt = Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray();
        private readonly byte[] _serverNonce = Enumerable.Range(32, 32).Select(static i => (byte)i).ToArray();
        private readonly byte[] _requestPrefix = [1, 2, 3, 4];
        private readonly byte[] _responsePrefix = [5, 6, 7, 8];
        private byte[]? _requestKey;
        private byte[]? _responseKey;

        public KioskV2Handler(long now, bool detailGranted = false, Tamper tamper = Tamper.None)
        {
            _now = now;
            _detailGranted = detailGranted;
            _tamper = tamper;
            OwnerCatalog = BuildOwnerCatalog(now, tamper == Tamper.OwnerDetailLeak);
        }

        public int Calls { get; private set; }
        public bool SessionProofVerified { get; private set; }
        public bool RequestAeadVerified { get; private set; }
        public byte[] OwnerCatalog { get; }
        public byte[] RawSessionReceipt { get; private set; } = [];
        public byte[] RawRequestEnvelope { get; private set; } = [];
        public byte[] RawResponseEnvelope { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return request.RequestUri!.AbsolutePath switch
            {
                "/v2/contract" => Json(BuildContract()),
                "/v2/session/open" => Json(
                    OpenSession(await request.Content!.ReadAsByteArrayAsync(cancellationToken))),
                "/v2/catalog/summary" => Json(
                    Catalog(await request.Content!.ReadAsByteArrayAsync(cancellationToken))),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }

        public static CryptoFixture FixedFixture()
        {
            var salt = Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray();
            var secret = PairingSecret(PairingCode, salt);
            var handshake = HandshakeKey(secret);
            var keys = SessionKeys(
                secret,
                SessionId,
                "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",
                "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8");
            var handshakeProof = Proof(
                handshake,
                [
                    SessionOpenSchema, Profile, PairingId, KeyEpoch, OwnerEpoch,
                    SummaryEpoch, DetailEpoch, "SessionOpenRequest_000000001",
                    "1900000000000", "1900000025000", ClientCapability,
                    "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"
                ]);
            var requestNonce = Nonce([1, 2, 3, 4], 1);
            var requestNonceText = B64(requestNonce);
            var requestAad = Canonical(
                [
                    Profile, "request", RequestSchema, "POST", "/v2/catalog/summary",
                    SessionId, "catalog-request-0001", "1", "1900000000000",
                    "1900000025000", ClientCapability, requestNonceText
                ]);
            var requestPlaintext = Encoding.UTF8.GetBytes(
                "{\"schema\":\"rusty.kiosk.catalog_request.v2\"}");
            var requestCiphertext = Encrypt(
                keys.Request,
                requestNonce,
                requestAad,
                requestPlaintext);
            var requestDigest = Sha256Hex(requestAad, requestCiphertext);
            var responseNonce = Nonce([5, 6, 7, 8], 1);
            var responseAad = Canonical(
                [
                    Profile, "response", ResponseSchema, SessionId,
                    "catalog-request-0001", CatalogResponseId, "1", requestDigest, "1",
                    "1900000000000", "1900000025000", ClientCapability,
                    B64(responseNonce)
                ]);
            var ownerCatalog = Encoding.UTF8.GetBytes(
                "{\"schema\":\"rusty.kiosk.catalog_snapshot.v1\"," +
                "\"owner_epoch\":\"OwnerEpoch_0000000000000001\"," +
                "\"observed_at_ms\":1900000000000,\"fresh_until_ms\":1900000300000}");
            var responseCiphertext = Encrypt(
                keys.Response,
                responseNonce,
                responseAad,
                ownerCatalog);
            return new CryptoFixture(
                secret,
                handshake,
                handshakeProof,
                keys.Request,
                keys.Response,
                requestNonce,
                requestAad,
                requestCiphertext,
                requestDigest,
                responseNonce,
                responseAad,
                responseCiphertext,
                ownerCatalog);
        }

        public static byte[] DecryptFixtureResponse(CryptoFixture fixture) =>
            Decrypt(
                fixture.ResponseKey,
                fixture.ResponseNonce,
                fixture.ResponseAad,
                fixture.ResponseCiphertext);

        private byte[] BuildContract()
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("schema", "rusty.kiosk.direct_operator.v2");
                writer.WriteString("profile", Profile);
                writer.WriteString("session_open_schema", SessionOpenSchema);
                writer.WriteString("request_envelope_schema", RequestSchema);
                writer.WriteString("response_envelope_schema", ResponseSchema);
                writer.WriteString("kdf", "HKDF-SHA256");
                writer.WriteString("aead", "AES-256-GCM");
                writer.WriteNumber("key_bits", 256);
                writer.WriteNumber("nonce_bytes", 12);
                writer.WriteNumber("session_lifetime_ms", 90_000);
                writer.WriteNumber("request_lifetime_ms", 30_000);
                writer.WriteString("pairing_id", PairingId);
                writer.WriteString("key_epoch", KeyEpoch);
                writer.WriteString("owner_epoch", OwnerEpoch);
                writer.WriteString("kdf_salt", B64(_kdfSalt));
                writer.WriteBoolean("catalog_summary_granted", true);
                writer.WriteString("catalog_summary_grant_epoch", SummaryEpoch);
                writer.WriteBoolean("catalog_detail_granted", _detailGranted);
                writer.WriteString("catalog_detail_grant_epoch", DetailEpoch);
                writer.WriteStartArray("available_capabilities");
                writer.WriteStringValue("catalog-summary");
                if (_detailGranted) writer.WriteStringValue("catalog-detail");
                writer.WriteEndArray();
                writer.WriteStartArray("defined_but_inactive_capabilities");
                writer.WriteStringValue("app-launch");
                writer.WriteEndArray();
                writer.WriteString("launch_issue_schema", "rusty.kiosk.launch_reference_issue.v1");
                writer.WriteString("launch_execute_schema", "rusty.kiosk.launch_reference_execute.v1");
                writer.WriteString("normal_launch_scope", RustyKioskV2ProviderContract.AppLaunchScope);
                writer.WriteBoolean("launch_authority_active", false);
                writer.WriteBoolean("direct_v1_catalog_allowed", false);
                writer.WriteBoolean("direct_v1_launch_allowed", false);
                writer.WriteBoolean("catalog_payload_cleartext", false);
                writer.WriteBoolean("arbitrary_intents", false);
                writer.WriteBoolean("arbitrary_targets", false);
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }

        private byte[] OpenSession(byte[] body)
        {
            using var root = JsonDocument.Parse(body);
            var json = root.RootElement;
            var requestId = json.GetProperty("request_id").GetString()!;
            var clientNonce = json.GetProperty("client_nonce").GetString()!;
            var issued = json.GetProperty("issued_at_ms").GetInt64();
            var expires = json.GetProperty("expires_at_ms").GetInt64();
            var secret = PairingSecret(PairingCode, _kdfSalt);
            var handshake = HandshakeKey(secret);
            var openParts = new[]
            {
                SessionOpenSchema, Profile, PairingId, KeyEpoch, OwnerEpoch, SummaryEpoch,
                DetailEpoch, requestId, issued.ToString(CultureInfo.InvariantCulture),
                expires.ToString(CultureInfo.InvariantCulture), ClientCapability, clientNonce
            };
            SessionProofVerified = string.Equals(
                Proof(handshake, openParts),
                json.GetProperty("proof").GetString(),
                StringComparison.Ordinal);
            var keys = SessionKeys(secret, SessionId, clientNonce, B64(_serverNonce));
            _requestKey = keys.Request;
            _responseKey = keys.Response;
            var receiptExpires =
                _tamper == Tamper.SessionExpiryOrder ? _now - 1 : expires;
            var receiptParts = new[]
            {
                SessionReceiptSchema, Profile, PairingId, KeyEpoch, OwnerEpoch, SummaryEpoch,
                DetailEpoch, requestId, SessionResponseId, SessionId,
                _now.ToString(CultureInfo.InvariantCulture),
                receiptExpires.ToString(CultureInfo.InvariantCulture), ClientCapability,
                B64(_serverNonce), B64(_requestPrefix), B64(_responsePrefix)
            };
            var proof = Proof(handshake, receiptParts);
            if (_tamper == Tamper.SessionProof)
            {
                proof = new string('0', 64);
            }
            RawSessionReceipt = JsonBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("schema", SessionReceiptSchema);
                writer.WriteString("profile", Profile);
                writer.WriteString("pairing_id", PairingId);
                writer.WriteString("key_epoch", KeyEpoch);
                writer.WriteString("owner_epoch", OwnerEpoch);
                writer.WriteString("catalog_summary_grant_epoch", SummaryEpoch);
                writer.WriteString("catalog_detail_grant_epoch", DetailEpoch);
                writer.WriteString("request_id", requestId);
                writer.WriteString("response_id", SessionResponseId);
                writer.WriteString("session_id", SessionId);
                writer.WriteNumber("issued_at_ms", _now);
                writer.WriteNumber("expires_at_ms", receiptExpires);
                writer.WriteStartArray("capabilities");
                writer.WriteStringValue(ClientCapability);
                writer.WriteEndArray();
                writer.WriteString("server_nonce", B64(_serverNonce));
                writer.WriteString("request_nonce_prefix", B64(_requestPrefix));
                writer.WriteString("response_nonce_prefix", B64(_responsePrefix));
                writer.WriteString("proof", proof);
                writer.WriteEndObject();
            });
            return RawSessionReceipt;
        }

        private byte[] Catalog(byte[] body)
        {
            RawRequestEnvelope = body;
            using var root = JsonDocument.Parse(body);
            var json = root.RootElement;
            var requestId = json.GetProperty("request_id").GetString()!;
            var requestCounter = json.GetProperty("counter").GetInt64();
            var issued = json.GetProperty("issued_at_ms").GetInt64();
            var expires = json.GetProperty("expires_at_ms").GetInt64();
            var nonceText = json.GetProperty("nonce").GetString()!;
            var ciphertextText = json.GetProperty("ciphertext").GetString()!;
            var aad = Canonical(
                [
                    Profile, "request", RequestSchema, "POST", "/v2/catalog/summary",
                    SessionId, requestId, requestCounter.ToString(CultureInfo.InvariantCulture),
                    issued.ToString(CultureInfo.InvariantCulture),
                    expires.ToString(CultureInfo.InvariantCulture), ClientCapability, nonceText
                ]);
            var ciphertext = Decode(ciphertextText);
            var plaintext = Decrypt(_requestKey!, Decode(nonceText), aad, ciphertext);
            RequestAeadVerified =
                Encoding.UTF8.GetString(plaintext) ==
                "{\"schema\":\"rusty.kiosk.catalog_request.v2\"}";
            var digest = Sha256Hex(aad, ciphertext);
            if (_tamper == Tamper.RequestDigest) digest = new string('0', 64);
            var responseCounter = _tamper == Tamper.ResponseCounter ? 2 : 1;
            var responseNonce = Nonce(_responsePrefix, responseCounter);
            if (_tamper == Tamper.ResponseNonce) responseNonce[0] ^= 0x40;
            var responseNonceText = B64(responseNonce);
            var responseIssued =
                _tamper == Tamper.ResponseIssuedBeforeRequest ? _now - 1 : _now;
            var responseAad = Canonical(
                [
                    Profile, "response", ResponseSchema, SessionId, requestId,
                    CatalogResponseId, requestCounter.ToString(CultureInfo.InvariantCulture),
                    digest, responseCounter.ToString(CultureInfo.InvariantCulture),
                    responseIssued.ToString(CultureInfo.InvariantCulture),
                    expires.ToString(CultureInfo.InvariantCulture), ClientCapability,
                    responseNonceText
                ]);
            var responseCiphertext = Encrypt(
                _responseKey!,
                responseNonce,
                responseAad,
                OwnerCatalog);
            if (_tamper == Tamper.ResponseCiphertext) responseCiphertext[0] ^= 0x01;
            RawResponseEnvelope = JsonBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("schema", ResponseSchema);
                writer.WriteString("profile", Profile);
                writer.WriteString("session_id", SessionId);
                writer.WriteString("request_id", requestId);
                writer.WriteString("response_id", CatalogResponseId);
                writer.WriteNumber("request_counter", requestCounter);
                writer.WriteString("request_digest", digest);
                writer.WriteNumber("response_counter", responseCounter);
                writer.WriteNumber("issued_at_ms", responseIssued);
                writer.WriteNumber("expires_at_ms", expires);
                writer.WriteString("capability", ClientCapability);
                writer.WriteString("nonce", responseNonceText);
                writer.WriteString("ciphertext", B64(responseCiphertext));
                writer.WriteEndObject();
            });
            return RawResponseEnvelope;
        }

        private static byte[] BuildOwnerCatalog(long now, bool detailLeak) =>
            Encoding.UTF8.GetBytes(
                $"{{\"schema\":\"rusty.kiosk.catalog_snapshot.v1\"," +
                $"\"owner_epoch\":\"{OwnerEpoch}\",\"observed_at_ms\":{now}," +
                $"\"fresh_until_ms\":{now + 300_000}," +
                "\"catalog_revision\":\"" + new string('a', 64) + "\"," +
                "\"snapshot_digest\":\"" + new string('b', 64) + "\"," +
                "\"tag_revision\":\"" + new string('c', 64) + "\"," +
                "\"installed_observation_revision\":\"" + new string('d', 64) + "\"," +
                "\"search_semantics\":\"rusty.kiosk.catalog_search.v1\"," +
                "\"complete\":true,\"truncated\":false,\"total_candidate_count\":1," +
                "\"permission_limited_count\":0,\"entries\":[{" +
                "\"catalog_entry_id\":\"" + new string('e', 64) + "\"," +
                "\"entry_digest\":\"" + new string('f', 64) + "\"," +
                "\"app_identity\":\"" + new string('1', 64) + "\"," +
                "\"disambiguator\":\"111111111111\",\"label\":\"Visible App\"," +
                "\"tags\":[\"demo\"],\"installed\":true,\"launchable\":true," +
                "\"source\":\"installed\"" +
                (detailLeak ? ",\"package\":\"com.example.visible\"" : string.Empty) +
                "}]}");

        private static HttpResponseMessage Json(byte[] body) =>
            new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new("application/json") }
                }
            };

        private static byte[] JsonBytes(Action<Utf8JsonWriter> write)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) write(writer);
            return stream.ToArray();
        }

        private static byte[] PairingSecret(string pairingCode, byte[] salt)
        {
            var extractSalt = Join(
                Canonical([Profile, "extract-v1", PairingId, KeyEpoch, OwnerEpoch]),
                salt);
            return HMACSHA256.HashData(extractSalt, Encoding.ASCII.GetBytes(pairingCode));
        }

        private static byte[] HandshakeKey(byte[] secret) =>
            Expand(
                secret,
                Canonical(
                    [
                        Profile, "handshake-key-v1", PairingId, KeyEpoch, OwnerEpoch,
                        SummaryEpoch, DetailEpoch
                    ]),
                32);

        private static (byte[] Request, byte[] Response) SessionKeys(
            byte[] secret,
            string sessionId,
            string clientNonce,
            string serverNonce)
        {
            var common = new[]
            {
                Profile, PairingId, KeyEpoch, OwnerEpoch, SummaryEpoch, DetailEpoch,
                sessionId, clientNonce, serverNonce, ClientCapability
            };
            return (
                Expand(secret, Canonical(["request-key-v1", .. common]), 32),
                Expand(secret, Canonical(["response-key-v1", .. common]), 32));
        }

        private static byte[] Expand(byte[] key, byte[] info, int length)
        {
            var output = new byte[length];
            var previous = Array.Empty<byte>();
            var written = 0;
            for (byte counter = 1; written < length; counter++)
            {
                previous = HMACSHA256.HashData(key, Join(previous, info, [counter]));
                var count = Math.Min(previous.Length, length - written);
                previous.AsSpan(0, count).CopyTo(output.AsSpan(written));
                written += count;
            }
            return output;
        }

        private static string Proof(byte[] key, IReadOnlyList<string> parts) =>
            Convert.ToHexString(HMACSHA256.HashData(key, Canonical(parts))).ToLowerInvariant();

        private static byte[] Nonce(byte[] prefix, long counter)
        {
            var nonce = new byte[12];
            prefix.CopyTo(nonce, 0);
            BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), counter);
            return nonce;
        }

        private static byte[] Encrypt(
            byte[] key,
            byte[] nonce,
            byte[] aad,
            byte[] plaintext)
        {
            var output = new byte[plaintext.Length + 16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(
                nonce,
                plaintext,
                output.AsSpan(0, plaintext.Length),
                output.AsSpan(plaintext.Length, 16),
                aad);
            return output;
        }

        private static byte[] Decrypt(
            byte[] key,
            byte[] nonce,
            byte[] aad,
            byte[] ciphertext)
        {
            var plaintext = new byte[ciphertext.Length - 16];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(
                nonce,
                ciphertext.AsSpan(0, plaintext.Length),
                ciphertext.AsSpan(plaintext.Length, 16),
                plaintext,
                aad);
            return plaintext;
        }

        private static byte[] Canonical(IReadOnlyList<string> parts)
        {
            using var stream = new MemoryStream();
            foreach (var part in parts)
            {
                var bytes = Encoding.UTF8.GetBytes(part);
                stream.Write(Encoding.ASCII.GetBytes(
                    bytes.Length.ToString(CultureInfo.InvariantCulture)));
                stream.WriteByte((byte)':');
                stream.Write(bytes);
            }
            return stream.ToArray();
        }

        private static string B64(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Decode(string value)
        {
            var padding = (value.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new InvalidOperationException()
            };
            return Convert.FromBase64String(
                value.Replace('-', '+').Replace('_', '/') + padding);
        }

        private static byte[] Join(params byte[][] parts)
        {
            var output = new byte[parts.Sum(static part => part.Length)];
            var offset = 0;
            foreach (var part in parts)
            {
                part.CopyTo(output, offset);
                offset += part.Length;
            }
            return output;
        }
    }

    private sealed record CryptoFixture(
        byte[] PairingSecret,
        byte[] HandshakeKey,
        string HandshakeProof,
        byte[] RequestKey,
        byte[] ResponseKey,
        byte[] RequestNonce,
        byte[] RequestAad,
        byte[] RequestCiphertext,
        string RequestDigest,
        byte[] ResponseNonce,
        byte[] ResponseAad,
        byte[] ResponseCiphertext,
        byte[] OwnerCatalog) : IDisposable
    {
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(PairingSecret);
            CryptographicOperations.ZeroMemory(HandshakeKey);
            CryptographicOperations.ZeroMemory(RequestKey);
            CryptographicOperations.ZeroMemory(ResponseKey);
            CryptographicOperations.ZeroMemory(RequestNonce);
            CryptographicOperations.ZeroMemory(RequestAad);
            CryptographicOperations.ZeroMemory(RequestCiphertext);
            CryptographicOperations.ZeroMemory(ResponseNonce);
            CryptographicOperations.ZeroMemory(ResponseAad);
            CryptographicOperations.ZeroMemory(ResponseCiphertext);
            CryptographicOperations.ZeroMemory(OwnerCatalog);
        }
    }

    private static string Sha256Hex(params byte[][] parts)
    {
        var length = parts.Sum(static part => part.Length);
        var combined = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(combined, offset);
            offset += part.Length;
        }
        return Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
    }

    private static string FramedReplayIdentity(params byte[][] documents)
    {
        var digests = documents.Select(static document => Sha256Hex(document)).ToArray();
        using var stream = new MemoryStream();
        foreach (var digest in digests)
        {
            var bytes = Encoding.UTF8.GetBytes(digest);
            stream.Write(Encoding.ASCII.GetBytes(
                bytes.Length.ToString(CultureInfo.InvariantCulture)));
            stream.WriteByte((byte)':');
            stream.Write(bytes);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}
