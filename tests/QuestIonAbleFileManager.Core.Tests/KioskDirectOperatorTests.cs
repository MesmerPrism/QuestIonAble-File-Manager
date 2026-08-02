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

    private sealed class DirectOperatorHandler(
        bool mismatchedStagingReadback = false,
        bool cleanupRequiredFirst = false) : HttpMessageHandler
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
                body = new { accepted = true, message = "accepted" };
            }
            else if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/kiosk/result")
            {
                KioskResultCount++;
                body = new
                {
                    schema = RustyKioskContract.ResultSchema,
                    request_id = _kioskRequestId,
                    command = _kioskCommand,
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
