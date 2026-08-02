using QuestIonAbleFileManager.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class RustyKioskDirectClientTests
{
    private const string EmptySha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void RequestSignature_MatchesAndroidCrossClientVector()
    {
        var signature = RustyKioskDirectAuth.SignRequest(
            "0123-4567-89AB-CDEF",
            "POST",
            "/v1/kiosk/invoke",
            "http_12345678",
            1_784_650_000L,
            EmptySha);

        Assert.Equal("f35ef975435590bf944f26e5055267d3615c6f1916f4a8b3986389900b588989", signature);
    }

    [Fact]
    public void ResponseSignature_MatchesAndroidCrossClientVector()
    {
        var signature = RustyKioskDirectAuth.SignResponse(
            "0123-4567-89AB-CDEF",
            "http_12345678",
            200,
            EmptySha);

        Assert.Equal("0a4418fe4677bfac1a12047ef8ea842e3ebaca7e758b8a190a4de009eaf9babb", signature);
    }

    [Fact]
    public void Endpoint_RequiresHttpAddressAndCompletePairingCode()
    {
        var endpoint = RustyKioskDirectEndpoint.Parse(
            "http://192.168.137.42:39873",
            "0123-4567-89AB-CDEF-0123-4567-89");

        Assert.Equal("http://192.168.137.42:39873/", endpoint.BaseUri.ToString());
        Assert.DoesNotContain(endpoint.PairingCode, endpoint.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint.PairingCode, JsonSerializer.Serialize(endpoint), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RustyKioskDirectEndpoint.Parse("https://example.com", "short"));
    }

    [Fact]
    public async Task Status_VerifiesSignedResponseBeforeReturningReadback()
    {
        const string code = "0123-4567-89AB-CDEF-0123-4567-89";
        var handler = new SignedResponseHandler(code, tamperBodyAfterSigning: false);
        var client = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", code),
            new HttpClient(handler));

        var status = await client.GetStatusAsync();

        Assert.Equal(RustyKioskDirectClient.ContractSchema, status.Schema);
        Assert.True(status.InstallerAllowed);
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("X-Rusty-Signature"));
    }

    [Fact]
    public async Task Status_RejectsBodyChangedAfterSignature()
    {
        const string code = "0123-4567-89AB-CDEF-0123-4567-89";
        var client = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", code),
            new HttpClient(new SignedResponseHandler(code, tamperBodyAfterSigning: true)));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetStatusAsync());
    }

    [Fact]
    public async Task Install_RejectsMissingMalformedAndDuplicateUploadCommitmentsBeforeRequest()
    {
        const string code = "0123-4567-89AB-CDEF-0123-4567-89";
        using var client = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", code),
            new HttpClient(new SignedResponseHandler(code, tamperBodyAfterSigning: false)));

        await Assert.ThrowsAsync<ArgumentException>(() => client.RequestInstallAsync(
            [new RustyKioskStagedFile("app.apk", 10, 0)]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RequestInstallAsync(
            [new RustyKioskStagedFile("app.apk", 10, 0, new string('A', 64))]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RequestInstallAsync(
            [
                new RustyKioskStagedFile("app.apk", 10, 0, new string('a', 64)),
                new RustyKioskStagedFile("app.apk", 10, 0, new string('b', 64))
            ]));
    }

    [Fact]
    public async Task DownloadFromStaging_ClosesTemporaryFileBeforeAtomicMove()
    {
        const string code = "0123-4567-89AB-CDEF-0123-4567-89";
        var expected = Encoding.UTF8.GetBytes("bounded direct staging payload");
        var client = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", code),
            new HttpClient(new StagingDownloadHandler(code, expected)));
        var testRoot = Path.Combine(Path.GetTempPath(), "qfm-direct-download-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(testRoot, "roundtrip.txt");

        try
        {
            var result = await client.DownloadFromStagingAsync("roundtrip.txt", output);
            Assert.Equal(output, result);
            Assert.Equal(expected, await File.ReadAllBytesAsync(output));
            Assert.Empty(Directory.GetFiles(testRoot, "*.part"));
        }
        finally
        {
            client.Dispose();
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadFromStaging_InvalidDigestPreservesExistingOutputAndCleansPartial()
    {
        const string code = "0123-4567-89AB-CDEF-0123-4567-89";
        var payload = Encoding.UTF8.GetBytes("untrusted replacement");
        var wrongSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("different bytes"))).ToLowerInvariant();
        var client = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", code),
            new HttpClient(new StagingDownloadHandler(code, payload, signedSha: wrongSha)));
        var testRoot = Path.Combine(Path.GetTempPath(), "qfm-direct-download-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(testRoot, "roundtrip.txt");
        var original = Encoding.UTF8.GetBytes("retained original");

        try
        {
            Directory.CreateDirectory(testRoot);
            await File.WriteAllBytesAsync(output, original);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.DownloadFromStagingAsync("roundtrip.txt", output, overwrite: true));
            Assert.Equal(original, await File.ReadAllBytesAsync(output));
            Assert.Empty(Directory.GetFiles(testRoot, "*.part"));
        }
        finally
        {
            client.Dispose();
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadFromStaging_RejectsOversizedDeclaredLengthBeforeCreatingOutput()
    {
        const string code = "0123-4567-89AB-CDEF-0123-4567-89";
        var client = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", code),
            new HttpClient(new StagingDownloadHandler(
                code,
                Encoding.UTF8.GetBytes("small body"),
                declaredLength: 2L * 1024L * 1024L * 1024L + 1L)));
        var testRoot = Path.Combine(Path.GetTempPath(), "qfm-direct-download-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(testRoot, "roundtrip.txt");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.DownloadFromStagingAsync("roundtrip.txt", output));
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(testRoot));
        }
        finally
        {
            client.Dispose();
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadFromStaging_RejectsUnknownLengthWhenStreamExceedsBound()
    {
        const string code = "0123-4567-89AB-CDEF-0123-4567-89";
        var client = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", code),
            new HttpClient(new StagingDownloadHandler(code, Encoding.UTF8.GetBytes("ninebytes"), unknownLength: true)),
            maxJsonResponseBytes: 1024,
            maxStagedFileBytes: 8);
        var testRoot = Path.Combine(Path.GetTempPath(), "qfm-direct-download-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(testRoot, "roundtrip.txt");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.DownloadFromStagingAsync("roundtrip.txt", output));
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(testRoot));
        }
        finally
        {
            client.Dispose();
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Status_RejectsJsonResponseOverConfiguredBound()
    {
        const string code = "0123-4567-89AB-CDEF-0123-4567-89";
        using var client = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", code),
            new HttpClient(new SignedResponseHandler(code, tamperBodyAfterSigning: false)),
            maxJsonResponseBytes: 64,
            maxStagedFileBytes: 1024);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetStatusAsync());
    }

    [Fact]
    public async Task InvokeKiosk_WaitsForExactCompletedResult()
    {
        var handler = new KioskInvokeHandler(incompletePolls: 1);
        var client = CreateInvokeClient(handler);

        var result = await client.InvokeKioskAsync(
            RustyKioskCommand.Status,
            timeout: TimeSpan.FromSeconds(1));

        Assert.True(result.Completed);
        Assert.Equal(RustyKioskCommand.Status, result.Command);
        Assert.Equal(2, handler.ResultPollCount);
    }

    [Fact]
    public async Task InvokeKiosk_RejectsCrossedLogicalRequestResult()
    {
        var client = CreateInvokeClient(new KioskInvokeHandler(
            resultRequestIdOverride: "kiosk_other_active_0001"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.InvokeKioskAsync(RustyKioskCommand.Status));

        Assert.Contains("different request", exception.Message);
    }

    [Fact]
    public async Task InvokeKiosk_RejectsStaleLogicalRequestResult()
    {
        var client = CreateInvokeClient(new KioskInvokeHandler(
            resultRequestIdOverride: "kiosk_stale_0001"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.InvokeKioskAsync(
                RustyKioskCommand.RequestWifiAdb));

        Assert.Contains("different request", exception.Message);
    }

    [Fact]
    public async Task InvokeKiosk_RejectsWrongTypedCommandResult()
    {
        var client = CreateInvokeClient(new KioskInvokeHandler(
            resultCommandOverride: RustyKioskCommand.Reload.ToWireName()));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.InvokeKioskAsync(RustyKioskCommand.Status));

        Assert.Contains("different typed command", exception.Message);
    }

    [Fact]
    public async Task InvokeKiosk_NeverReturnsAcceptedIncompleteResult()
    {
        var handler = new KioskInvokeHandler(incompletePolls: int.MaxValue);
        var client = CreateInvokeClient(handler);

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.InvokeKioskAsync(
                RustyKioskCommand.Status,
                timeout: TimeSpan.FromMilliseconds(10)));

        Assert.True(handler.ResultPollCount >= 1);
    }

    [Fact]
    public async Task RequestLifecycle_StatusAndCancelUseExactNonEnqueueRoutes()
    {
        const string requestId = "kiosk_lifecycle_0001";
        var handler = new LifecycleHandler(requestId);
        using var client = CreateInvokeClient(handler);

        var status = await client.ReadKioskRequestStatusAsync(requestId);
        var cancelled = await client.CancelKioskRequestAsync(requestId);

        Assert.Equal(OperatorMutationStage.Pending, status.MutationStage);
        Assert.Equal(OperatorMutationStage.Cancelled, cancelled.MutationStage);
        Assert.Equal(0, handler.InvokeCount);
        Assert.Equal(
            $"/v1/kiosk/request-status?request_id={requestId}",
            handler.RequestTargets[0]);
        Assert.Equal("/v1/kiosk/cancel", handler.RequestTargets[1]);
        Assert.Equal(requestId, handler.CancelledRequestId);
    }

    [Fact]
    public async Task RequestLifecycle_RejectsCrossedStatusIdentity()
    {
        using var client = CreateInvokeClient(new LifecycleHandler(
            "kiosk_lifecycle_0001",
            returnedRequestId: "kiosk_lifecycle_0002"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ReadKioskRequestStatusAsync("kiosk_lifecycle_0001"));

        Assert.Contains("different request id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dispose_ZerosOwnedAuthenticationKeyAndRejectsFurtherUse()
    {
        var secret = Enumerable.Range(1, 32).Select(index => (byte)index).ToArray();
        var client = new RustyKioskDirectClient(
            new Uri("http://192.0.2.1:39873"),
            secret,
            sessionId: "session_test_0001",
            expectedBridgeGeneration: 41,
            new HttpClient(new SessionStatusHandlerForClient(secret)));

        client.Dispose();

        Assert.All(secret, value => Assert.Equal(0, value));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetStatusAsync());
    }

    [Fact]
    public async Task TagWriteRequiresMatchingSignedSizeAndDigestReadback()
    {
        const string code = "0123-4567-89AB-CDEF-0123-4567-89";
        var tags = Encoding.UTF8.GetBytes(
            "{\"schema\":\"rusty.kiosk.app_tags.v1\",\"apps\":[]}");
        using var confirmed = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", code),
            new HttpClient(new TagWriteHandler(code)));
        await confirmed.WriteTagsAsync(tags);

        using var mismatched = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", code),
            new HttpClient(new TagWriteHandler(code, mismatchDigest: true)));
        await Assert.ThrowsAsync<InvalidDataException>(() => mismatched.WriteTagsAsync(tags));
    }

    [Fact]
    public void SharedDirectCommandSerializationContainsNoTransportCredentialFields()
    {
        var command = KioskDirectOperatorCommand.Invoke(
            RustyKioskCommand.AddTag,
            "example-tag",
            operatorConfirmed: true);

        var json = JsonSerializer.Serialize(command);

        Assert.DoesNotContain("endpoint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pairing", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session_secret", json, StringComparison.OrdinalIgnoreCase);
    }

    private static RustyKioskDirectClient CreateInvokeClient(
        HttpMessageHandler handler)
    {
        const string code = "0123-4567-89AB-CDEF-0123-4567-89";
        return new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse(
                "http://192.0.2.1:39873",
                code),
            new HttpClient(handler));
    }

    private sealed class SignedResponseHandler(
        string pairingCode,
        bool tamperBodyAfterSigning) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            var requestId = request.Headers.GetValues("X-Rusty-Request-Id").Single();
            var signedBytes = Encoding.UTF8.GetBytes(
                "{\"accepted\":true,\"schema\":\"rusty.kiosk.direct_operator.v2\",\"endpoint\":\"http://192.0.2.1:39873\",\"installer_allowed\":true,\"staging_directory_kind\":\"app-owned\",\"message\":\"ready\"}");
            var returnedBytes = tamperBodyAfterSigning
                ? Encoding.UTF8.GetBytes("{\"accepted\":false,\"message\":\"tampered\"}")
                : signedBytes;
            var sha = Convert.ToHexString(SHA256.HashData(signedBytes)).ToLowerInvariant();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(returnedBytes)
            };
            response.Headers.TryAddWithoutValidation("X-Rusty-Request-Id", requestId);
            response.Headers.TryAddWithoutValidation("X-Rusty-Content-Sha256", sha);
            response.Headers.TryAddWithoutValidation(
                "X-Rusty-Signature",
                RustyKioskDirectAuth.SignResponse(pairingCode, requestId, 200, sha));
            return Task.FromResult(response);
        }
    }

    private sealed class KioskInvokeHandler(
        string? resultRequestIdOverride = null,
        string? resultCommandOverride = null,
        int incompletePolls = 0) : HttpMessageHandler
    {
        private const string PairingCode =
            "0123-4567-89AB-CDEF-0123-4567-89";
        private string? _logicalRequestId;
        private string? _logicalCommand;
        private int _resultPollCount;

        public int ResultPollCount =>
            Volatile.Read(ref _resultPollCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var transportRequestId = request.Headers
                .GetValues("X-Rusty-Request-Id")
                .Single();
            byte[] body;
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == "/v1/kiosk/invoke")
            {
                var requestBytes = await request.Content!
                    .ReadAsByteArrayAsync(cancellationToken);
                using var document = JsonDocument.Parse(requestBytes);
                _logicalRequestId = document.RootElement
                    .GetProperty("request_id")
                    .GetString();
                _logicalCommand = document.RootElement
                    .GetProperty("command")
                    .GetString();
                body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    accepted = true,
                    message = "accepted"
                });
            }
            else
            {
                var poll = Interlocked.Increment(ref _resultPollCount);
                body = BuildResult(
                    resultRequestIdOverride ?? _logicalRequestId ??
                        throw new InvalidOperationException(
                            "Invoke request was not observed."),
                    resultCommandOverride ?? _logicalCommand ??
                        throw new InvalidOperationException(
                            "Invoke command was not observed."),
                    poll > incompletePolls);
            }

            var sha = Convert.ToHexString(SHA256.HashData(body))
                .ToLowerInvariant();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            };
            response.Headers.TryAddWithoutValidation(
                "X-Rusty-Request-Id",
                transportRequestId);
            response.Headers.TryAddWithoutValidation(
                "X-Rusty-Content-Sha256",
                sha);
            response.Headers.TryAddWithoutValidation(
                "X-Rusty-Signature",
                RustyKioskDirectAuth.SignResponse(
                    PairingCode,
                    transportRequestId,
                    200,
                    sha));
            return response;
        }

        private static byte[] BuildResult(
            string requestId,
            string command,
            bool completed) =>
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = RustyKioskContract.ResultSchema,
                request_id = requestId,
                command,
                accepted = true,
                completed,
                message = completed ? "complete" : "pending",
                state = new
                {
                    installed_count = 0,
                    not_installed_count = 0,
                    visible_count = 0,
                    visible_entries_truncated = false,
                    entries = Array.Empty<object>(),
                    search = "",
                    tag_filter = (string?)null,
                    selected_key = (string?)null,
                    selected_name = (string?)null,
                    selected_package = (string?)null,
                    selected_installed = false,
                    selected_launchable = false,
                    wifi_adb_enabled = false,
                    setup_helper_installed = true,
                    setup_helper_ready = true,
                    request_wifi_adb_after_boot = false,
                    accessibility_enabled = false,
                    guard_armed = false,
                    operation_in_progress =
                        completed ? null : "request-pending",
                    status_line = completed ? "complete" : "pending",
                    tag_file_path = RustyKioskContract.TagFilePath
                }
            });
    }

    private sealed class LifecycleHandler(
        string expectedRequestId,
        string? returnedRequestId = null) : HttpMessageHandler
    {
        private const string PairingCode = "0123-4567-89AB-CDEF-0123-4567-89";

        public int InvokeCount { get; private set; }
        public List<string> RequestTargets { get; } = [];
        public string? CancelledRequestId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = request.RequestUri?.PathAndQuery ?? string.Empty;
            RequestTargets.Add(target);
            if (target == "/v1/kiosk/invoke") InvokeCount++;
            var operationState = "pending";
            if (target == "/v1/kiosk/cancel")
            {
                using var bodyDocument = JsonDocument.Parse(
                    await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                CancelledRequestId = bodyDocument.RootElement.GetProperty("request_id").GetString();
                operationState = "cancelled";
            }
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                request_id = returnedRequestId ?? expectedRequestId,
                operation_state = operationState,
                accepted = true,
                completed = operationState == "cancelled",
                message = operationState
            });
            return SignedResponse(request, body, PairingCode);
        }
    }

    private sealed class SessionStatusHandlerForClient(byte[] secret) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = Encoding.UTF8.GetBytes(
                "{\"accepted\":true,\"schema\":\"rusty.kiosk.direct_operator.v2\",\"installer_allowed\":true,\"staging_directory_kind\":\"app-owned\",\"message\":\"ready\",\"bridge_generation\":41,\"session_id\":\"session_test_0001\"}");
            return Task.FromResult(SignedResponse(request, body, secret));
        }
    }

    private sealed class TagWriteHandler(
        string pairingCode,
        bool mismatchDigest = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/v1/tags", request.RequestUri?.AbsolutePath);
            var content = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                accepted = true,
                bytes = content.Length,
                sha256 = mismatchDigest ? new string('0', 64) : digest,
                message = "stored"
            });
            return SignedResponse(request, body, pairingCode);
        }
    }

    private static HttpResponseMessage SignedResponse(
        HttpRequestMessage request,
        byte[] body,
        string pairingCode)
    {
        var requestId = request.Headers.GetValues("X-Rusty-Request-Id").Single();
        var sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
        };
        response.Headers.TryAddWithoutValidation("X-Rusty-Request-Id", requestId);
        response.Headers.TryAddWithoutValidation("X-Rusty-Content-Sha256", sha);
        response.Headers.TryAddWithoutValidation(
            "X-Rusty-Signature",
            RustyKioskDirectAuth.SignResponse(pairingCode, requestId, 200, sha));
        return response;
    }

    private static HttpResponseMessage SignedResponse(
        HttpRequestMessage request,
        byte[] body,
        byte[] secret)
    {
        var requestId = request.Headers.GetValues("X-Rusty-Request-Id").Single();
        var sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
        };
        response.Headers.TryAddWithoutValidation("X-Rusty-Request-Id", requestId);
        response.Headers.TryAddWithoutValidation("X-Rusty-Content-Sha256", sha);
        response.Headers.TryAddWithoutValidation(
            "X-Rusty-Signature",
            RustyKioskDirectAuth.SignResponse(secret, requestId, 200, sha));
        return response;
    }

    private sealed class StagingDownloadHandler(
        string pairingCode,
        byte[] payload,
        string? signedSha = null,
        long? declaredLength = null,
        bool unknownLength = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/staging/files/roundtrip.txt", request.RequestUri?.AbsolutePath);
            var requestId = request.Headers.GetValues("X-Rusty-Request-Id").Single();
            var sha = signedSha ?? Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = unknownLength
                    ? new StreamContent(new MemoryStream(payload, writable: false))
                    : new ByteArrayContent(payload)
            };
            if (declaredLength is { } length) response.Content.Headers.ContentLength = length;
            response.Headers.TryAddWithoutValidation("X-Rusty-Request-Id", requestId);
            response.Headers.TryAddWithoutValidation("X-Rusty-Content-Sha256", sha);
            response.Headers.TryAddWithoutValidation(
                "X-Rusty-Signature",
                RustyKioskDirectAuth.SignResponse(pairingCode, requestId, 200, sha));
            return Task.FromResult(response);
        }
    }
}
