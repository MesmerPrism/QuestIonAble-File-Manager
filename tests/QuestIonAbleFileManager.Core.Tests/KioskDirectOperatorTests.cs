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
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class DirectOperatorHandler(bool mismatchedStagingReadback = false) : HttpMessageHandler
    {
        public int UploadCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int ListCount { get; private set; }
        public int InstallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            object body;
            if (request.Method == HttpMethod.Put && request.RequestUri?.AbsolutePath.StartsWith(
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
                body = new
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
