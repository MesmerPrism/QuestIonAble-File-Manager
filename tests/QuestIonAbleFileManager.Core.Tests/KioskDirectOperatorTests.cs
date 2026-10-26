using QuestIonAbleFileManager.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class KioskDirectOperatorTests
{
    private const string PairingCode = "0123-4567-89AB-CDEF-0123-4567-89";

    [Fact]
    public async Task SharedExecutorRequiresConfirmationAndReadsBackStagingMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "qfm-direct-operator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "app.apk");
        await File.WriteAllBytesAsync(local, Encoding.UTF8.GetBytes("test apk bytes"));
        try
        {
            var handler = new DirectOperatorHandler();
            using var client = new RustyKioskDirectClient(
                RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", PairingCode),
                new HttpClient(handler));
            var executor = new KioskDirectOperatorExecutor(client);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                executor.ExecuteAsync(KioskDirectOperatorCommand.Upload(
                    local,
                    stagedName: null,
                    operatorConfirmed: false)));
            var uploaded = await executor.ExecuteAsync(KioskDirectOperatorCommand.Upload(
                local,
                stagedName: null,
                operatorConfirmed: true));
            var deleted = await executor.ExecuteAsync(KioskDirectOperatorCommand.Delete(
                "app.apk",
                operatorConfirmed: true));

            Assert.Equal(OperatorMutationStage.Confirmed, uploaded.Mutation.Stage);
            Assert.Equal(OperatorMutationStage.Confirmed, deleted.Mutation.Stage);
            Assert.StartsWith("action_", uploaded.Mutation.ActionId, StringComparison.Ordinal);
            Assert.Equal(1, handler.UploadCount);
            Assert.Equal(1, handler.DeleteCount);
            Assert.Equal(1, handler.ListCount);

            using var mismatchedClient = new RustyKioskDirectClient(
                RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", PairingCode),
                new HttpClient(new DirectOperatorHandler(mismatchedStagingReadback: true)));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new KioskDirectOperatorExecutor(mismatchedClient).ExecuteAsync(
                    KioskDirectOperatorCommand.Upload(
                        local,
                        stagedName: null,
                        operatorConfirmed: true)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SharedInstallPreservesPendingWearerActionReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "qfm-direct-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "app.apk");
        await File.WriteAllBytesAsync(local, Encoding.UTF8.GetBytes("test apk bytes"));
        try
        {
            var handler = new DirectOperatorHandler();
            using var client = new RustyKioskDirectClient(
                RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", PairingCode),
                new HttpClient(handler));
            var result = await new KioskDirectOperatorExecutor(client).ExecuteAsync(
                KioskDirectOperatorCommand.Install(
                    [local],
                    operatorConfirmed: true,
                    requestId: "install_test_0001"));

            Assert.Equal(OperatorMutationStage.PendingWearerAction, result.Mutation.Stage);
            Assert.Equal("install_test_0001", result.Mutation.RequestId);
            Assert.True(result.InstallReceipt?.NeedsWearerAction);
            Assert.Equal(1, handler.InstallCount);
            using var installRequest = JsonDocument.Parse(Assert.IsType<string>(handler.LastInstallJson));
            var rootElement = installRequest.RootElement;
            Assert.Equal("install_test_0001", rootElement.GetProperty("request_id").GetString());
            var file = Assert.Single(rootElement.GetProperty("files").EnumerateArray());
            Assert.Equal("app.apk", file.GetProperty("name").GetString());
            Assert.Equal(14, file.GetProperty("bytes").GetInt64());
            Assert.Matches("^[a-f0-9]{64}$", file.GetProperty("sha256").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompositeAdoptionReturnsDirectKioskAndStagingReadbacksTogether()
    {
        var handler = new DirectOperatorHandler();
        using var client = new RustyKioskDirectClient(
            RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", PairingCode),
            new HttpClient(handler));

        var result = await new KioskDirectOperatorExecutor(client).ExecuteAsync(
            KioskDirectOperatorCommand.Adopt());

        Assert.Equal(OperatorMutationStage.Confirmed, result.Mutation.Stage);
        Assert.Equal(RustyKioskDirectClient.ContractSchema, result.Status?.Schema);
        Assert.Equal(RustyKioskCommand.Status, result.KioskResult?.Command);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<RustyKioskStagedFile>>(result.StagedFiles));
        Assert.Equal(1, handler.StatusCount);
        Assert.Equal(1, handler.KioskInvokeCount);
        Assert.Equal(1, handler.KioskResultCount);
        Assert.Equal(1, handler.ListCount);
    }

    [Fact]
    public async Task InstallCleanupRequiredRetriesExactBodyWithFreshAuthenticatedTransportIdOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "qfm-direct-install-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "app.apk");
        await File.WriteAllBytesAsync(local, Encoding.UTF8.GetBytes("test apk bytes"));
        try
        {
            var handler = new DirectOperatorHandler(cleanupRequiredFirst: true);
            using var client = new RustyKioskDirectClient(
                RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", PairingCode),
                new HttpClient(handler));

            var result = await new KioskDirectOperatorExecutor(client).ExecuteAsync(
                KioskDirectOperatorCommand.Install(
                    [local],
                    operatorConfirmed: true,
                    requestId: "install_cleanup_0001"));

            Assert.Equal(OperatorMutationStage.Failed, result.Mutation.Stage);
            Assert.Equal(1, handler.UploadCount);
            Assert.Equal(2, handler.InstallCount);
            Assert.Equal(2, handler.InstallBodies.Count);
            Assert.Equal(handler.InstallBodies[0], handler.InstallBodies[1]);
            Assert.Equal(2, handler.InstallTransportRequestIds.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string LogicalRequestId = "kiosk_original_0001";

    [Fact]
    public void LegacyInvokeRetainsItsThreeArgumentClrSignatureAndValueBehavior()
    {
        var legacy = typeof(KioskDirectOperatorCommand).GetMethod(nameof(KioskDirectOperatorCommand.Invoke),
            [typeof(RustyKioskCommand), typeof(string), typeof(bool)]);
        Assert.NotNull(legacy);
        var command = Assert.IsType<KioskDirectOperatorCommand>(legacy.Invoke(null, [RustyKioskCommand.Select, "  fixture-app  ", true]));
        Assert.Equal("fixture-app", command.Value); Assert.Null(command.RequestId);
        Assert.True(command.OperatorConfirmed); Assert.StartsWith("action_", command.ActionId, StringComparison.Ordinal);
    }

    [Fact]
    public void PreallocatedInvokeRetainsExactLogicalIdAndExistingValueNormalization()
    {
        var command = Preallocated(RustyKioskCommand.Select, "  fixture-app  ", true, LogicalRequestId);
        Assert.Equal(LogicalRequestId, command.RequestId); Assert.Equal("fixture-app", command.Value);
        Assert.NotEqual(command.RequestId, command.ActionId); Assert.Equal(RustyKioskCommand.Select, command.KioskCommand);
        Assert.True(command.OperatorConfirmed);
    }

    [Fact]
    public async Task ExecutorForwardsTheRetainedLogicalIdWithoutSubstitutingActionOrTransportIdentity()
    {
        var handler = new DirectOperatorHandler();
        using var client = Client(handler);
        // The retained field exists independently of the new factory overload.
        var command = KioskDirectOperatorCommand.Invoke(RustyKioskCommand.Status, null, true) with { RequestId = LogicalRequestId };
        var result = await new KioskDirectOperatorExecutor(client).ExecuteAsync(command);
        Assert.Equal(LogicalRequestId, Assert.Single(handler.LogicalRequestIds));
        Assert.Equal(LogicalRequestId, Assert.Single(handler.KioskResultIds));
        Assert.Equal(LogicalRequestId, result.Mutation.RequestId); Assert.Equal(LogicalRequestId, result.KioskResult!.RequestId);
        Assert.Equal(command.ActionId, result.Mutation.ActionId); Assert.NotEqual(command.ActionId, result.Mutation.RequestId);
        Assert.Equal(OperatorMutationStage.Confirmed, result.Mutation.Stage);
        Assert.Equal(1, handler.KioskInvokeCount);
        Assert.Equal(2, handler.TransportRequestIds.Count);
        Assert.Equal(2, handler.TransportRequestIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(handler.TransportRequestIds, id => { Assert.StartsWith("http_", id, StringComparison.Ordinal); Assert.NotEqual(LogicalRequestId, id); Assert.NotEqual(command.ActionId, id); });
        using var payload = JsonDocument.Parse(handler.LastKioskInvokeJson!);
        Assert.Equal(["request_id", "command", "value"], payload.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("kiosk has spaces")]
    [InlineData("kiosk.with.dots")]
    [InlineData("kiosk\nrequest_0001")]
    [InlineData("kiosk/original_0001")]
    [InlineData("kiosk_original_0001\n")]
    public void InvalidPreallocatedIdsRejectAtTheFactory(string requestId) =>
        Assert.Throws<ArgumentException>(() => Preallocated(RustyKioskCommand.Status, null, true, requestId));

    [Fact]
    public void OverlongPreallocatedIdRejectsAtTheFactory() =>
        Assert.Throws<ArgumentException>(() => Preallocated(RustyKioskCommand.Status, null, true, new string('x', 65)));

    [Theory]
    [InlineData("AbCd_123")]
    [InlineData("UPPER_Request-001")]
    public void ValidLogicalRequestIdsKeepTheirExactCaseAndSpelling(string requestId) =>
        Assert.Equal(requestId, Preallocated(RustyKioskCommand.Status, null, true, requestId).RequestId);

    [Fact]
    public void MaximumLengthLogicalIdRemainsSupported() =>
        Assert.Equal(new string('a', 64), Preallocated(RustyKioskCommand.Status, null, true, new string('a', 64)).RequestId);

    [Theory]
    [InlineData("status")]
    [InlineData("cancel")]
    [InlineData("install")]
    public void ExistingTypedRecoveryAndInstallAlsoRejectAnInvalidTrailingNewline(string action)
    {
        const string invalid = LogicalRequestId + "\n";
        Assert.Throws<ArgumentException>(() => action switch
        {
            "status" => KioskDirectOperatorCommand.RequestStatus(invalid),
            "cancel" => KioskDirectOperatorCommand.Cancel(invalid, true),
            _ => KioskDirectOperatorCommand.Install([Path.GetFullPath("fixture.apk")], true, invalid)
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyAndExplicitNullInvokeStillGenerateExactlyOneLogicalId(bool explicitNull)
    {
        var handler = new DirectOperatorHandler();
        using var client = Client(handler);
        var command = explicitNull ? Preallocated(RustyKioskCommand.Status, null, true, null) :
            KioskDirectOperatorCommand.Invoke(RustyKioskCommand.Status, null, true);
        Assert.Null(command.RequestId);
        var result = await new KioskDirectOperatorExecutor(client).ExecuteAsync(command);
        var generated = Assert.Single(handler.LogicalRequestIds);
        Assert.Matches("^kiosk_[a-f0-9]{32}$", generated);
        Assert.Equal(generated, Assert.Single(handler.KioskResultIds)); Assert.Equal(generated, result.Mutation.RequestId);
        Assert.Null(command.RequestId); Assert.Equal(1, handler.KioskInvokeCount);
    }

    [Fact]
    public async Task LostAdmissionReplyKeepsOriginalIdUsableForStatusAndCancelWithoutRepeatingInvoke()
    {
        var handler = new DirectOperatorHandler(loseKioskAdmissionReply: true);
        using var client = Client(handler);
        var executor = new KioskDirectOperatorExecutor(client);
        var original = Preallocated(RustyKioskCommand.Select, "fixture-app", true, LogicalRequestId);
        await Assert.ThrowsAsync<HttpRequestException>(() => executor.ExecuteAsync(original));
        Assert.Equal(LogicalRequestId, original.RequestId); Assert.Equal(LogicalRequestId, Assert.Single(handler.LogicalRequestIds));
        Assert.Equal(0, handler.KioskResultCount);
        var status = await executor.ExecuteAsync(KioskDirectOperatorCommand.RequestStatus(original.RequestId!));
        Assert.Equal(LogicalRequestId, status.RequestReceipt!.RequestId); Assert.Equal(OperatorMutationStage.Pending, status.Mutation.Stage);
        var cancelled = await executor.ExecuteAsync(KioskDirectOperatorCommand.Cancel(original.RequestId!, true));
        Assert.Equal(LogicalRequestId, cancelled.RequestReceipt!.RequestId); Assert.Equal(OperatorMutationStage.Cancelled, cancelled.Mutation.Stage);
        Assert.Equal(LogicalRequestId, Assert.Single(handler.RequestStatusIds));
        Assert.Equal(LogicalRequestId, Assert.Single(handler.CancelledRequestIds));
        Assert.Equal(1, handler.KioskInvokeCount); Assert.Equal(3, handler.TransportRequestIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("admission-id")]
    [InlineData("result-id")]
    [InlineData("result-command")]
    public async Task PreallocatedInvokeStillRejectsCrossedAdmissionAndResultIdentities(string crossed)
    {
        var handler = new DirectOperatorHandler(crossedKioskResponse: crossed);
        using var client = Client(handler);
        var command = Preallocated(RustyKioskCommand.Status, null, true, LogicalRequestId);
        await Assert.ThrowsAsync<InvalidDataException>(() => new KioskDirectOperatorExecutor(client).ExecuteAsync(command));
        Assert.Equal(LogicalRequestId, command.RequestId); Assert.Equal(LogicalRequestId, Assert.Single(handler.LogicalRequestIds));
        Assert.Equal(1, handler.KioskInvokeCount);
        Assert.Equal(crossed == "admission-id" ? 0 : 1, handler.KioskResultCount);
    }

    [Fact]
    public async Task PreallocatedInvokeDoesNotBypassExistingOperatorConfirmation()
    {
        var handler = new DirectOperatorHandler();
        using var client = Client(handler);
        var command = Preallocated(RustyKioskCommand.Status, null, false, LogicalRequestId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new KioskDirectOperatorExecutor(client).ExecuteAsync(command));
        Assert.Empty(handler.TransportRequestIds);
    }

    private static RustyKioskDirectClient Client(DirectOperatorHandler handler) => new(
        RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", PairingCode), new HttpClient(handler));

    private static KioskDirectOperatorCommand Preallocated(RustyKioskCommand command, string? value, bool confirmed, string? requestId) =>
        KioskDirectOperatorCommand.Invoke(command, value, confirmed, requestId);

    private sealed class DirectOperatorHandler(
        bool mismatchedStagingReadback = false,
        bool cleanupRequiredFirst = false,
        bool loseKioskAdmissionReply = false,
        string? crossedKioskResponse = null) : HttpMessageHandler
    {
        private string? _kioskRequestId;
        private string? _kioskCommand;

        public int UploadCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int ListCount { get; private set; }
        public int InstallCount { get; private set; }
        public string? LastInstallJson { get; private set; }
        public int StatusCount { get; private set; }
        public int KioskInvokeCount { get; private set; }
        public int KioskResultCount { get; private set; }
        public List<string> InstallBodies { get; } = [];
        public List<string> InstallTransportRequestIds { get; } = [];
        public List<string> LogicalRequestIds { get; } = [];
        public List<string> KioskResultIds { get; } = [];
        public List<string> RequestStatusIds { get; } = [];
        public List<string> CancelledRequestIds { get; } = [];
        public List<string> TransportRequestIds { get; } = [];
        public string? LastKioskInvokeJson { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            TransportRequestIds.Add(request.Headers.GetValues("X-Rusty-Request-Id").Single());
            object body;
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/status")
            {
                StatusCount++;
                body = new
                {
                    accepted = true,
                    schema = RustyKioskDirectClient.ContractSchema,
                    installer_allowed = true,
                    staging_directory_kind = "app-owned",
                    message = "ready"
                };
            }
            else if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/kiosk/invoke")
            {
                KioskInvokeCount++;
                using var document = JsonDocument.Parse(
                    request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult());
                _kioskRequestId = document.RootElement.GetProperty("request_id").GetString();
                _kioskCommand = document.RootElement.GetProperty("command").GetString();
                LastKioskInvokeJson = document.RootElement.GetRawText();
                LogicalRequestIds.Add(_kioskRequestId!);
                if (loseKioskAdmissionReply) throw new HttpRequestException("Synthetic lost admission reply.");
                body = new { accepted = true, message = "accepted", request_id = crossedKioskResponse == "admission-id" ? "kiosk_crossed_0001" : _kioskRequestId };
            }
            else if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/kiosk/result")
            {
                KioskResultCount++;
                KioskResultIds.Add(QueryRequestId(request));
                body = new
                {
                    schema = RustyKioskContract.ResultSchema,
                    request_id = crossedKioskResponse == "result-id" ? "kiosk_crossed_0001" : _kioskRequestId,
                    command = crossedKioskResponse == "result-command" ? "select" : _kioskCommand,
                    accepted = true,
                    completed = true,
                    message = "complete",
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
                        operation_in_progress = (string?)null,
                        status_line = "ready",
                        tag_file_path = RustyKioskContract.TagFilePath
                    }
                };
            }
            else if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/kiosk/request-status")
            {
                var id = QueryRequestId(request); RequireOriginal(id); RequestStatusIds.Add(id);
                body = new { request_id = id, operation_state = "pending", accepted = true, completed = false,
                    message = "Original request is pending.", enqueued_at_ms = 1000L, expires_at_ms = 2000L };
            }
            else if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/kiosk/cancel")
            {
                using var json = JsonDocument.Parse(request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult());
                var id = json.RootElement.GetProperty("request_id").GetString()!; RequireOriginal(id); CancelledRequestIds.Add(id);
                body = new { request_id = id, operation_state = "cancelled", accepted = true, completed = true,
                    message = "Original pending request was cancelled.", enqueued_at_ms = 1000L, expires_at_ms = 2000L };
            }
            else if (request.Method == HttpMethod.Put && request.RequestUri?.AbsolutePath.StartsWith(
                    "/v1/staging/files/",
                    StringComparison.Ordinal) == true)
            {
                UploadCount++;
                var sha = request.Headers.GetValues("X-Rusty-Content-Sha256").Single();
                body = new
                {
                    accepted = true,
                    name = "app.apk",
                    bytes = mismatchedStagingReadback ? 13L : 14L,
                    sha256 = sha,
                    message = "stored"
                };
            }
            else if (request.Method == HttpMethod.Delete)
            {
                DeleteCount++;
                body = new { accepted = true, message = "deleted" };
            }
            else if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/staging")
            {
                ListCount++;
                body = new { accepted = true, files = Array.Empty<object>() };
            }
            else if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/install")
            {
                InstallCount++;
                LastInstallJson = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                InstallBodies.Add(LastInstallJson);
                InstallTransportRequestIds.Add(
                    request.Headers.GetValues("X-Rusty-Request-Id").Single());
                body = cleanupRequiredFirst && InstallCount == 1
                    ? new
                    {
                        accepted = true,
                        request_id = "install_cleanup_0001",
                        state = "cleanup-required",
                        completed = false,
                        message = "Installer cleanup requires retry."
                    }
                    : cleanupRequiredFirst
                        ? new
                        {
                            accepted = true,
                            request_id = "install_cleanup_0001",
                            state = "failed",
                            completed = true,
                            message = "Installer session was confirmed absent."
                        }
                        : new
                        {
                            accepted = true,
                            request_id = "install_test_0001",
                            state = "pending-wearer-confirmation",
                            completed = false,
                            message = "Wearer confirmation required."
                        };
            }
            else
            {
                throw new InvalidOperationException($"Unexpected Direct Link test route: {request.Method} {request.RequestUri}");
            }
            return Task.FromResult(Sign(request, JsonSerializer.SerializeToUtf8Bytes(body)));
        }

        private static string QueryRequestId(HttpRequestMessage request) =>
            Uri.UnescapeDataString(request.RequestUri!.Query["?request_id=".Length..]);
        private void RequireOriginal(string id)
        {
            if (id != _kioskRequestId) throw new InvalidDataException("The synthetic remote owner has no such original request.");
        }

        private static HttpResponseMessage Sign(HttpRequestMessage request, byte[] body)
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
                RustyKioskDirectAuth.SignResponse(PairingCode, requestId, 200, sha));
            return response;
        }
    }
}
