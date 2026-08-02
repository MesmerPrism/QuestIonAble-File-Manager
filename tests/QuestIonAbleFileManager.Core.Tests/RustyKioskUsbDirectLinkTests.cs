using QuestIonAbleFileManager.Core;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class RustyKioskUsbDirectLinkTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeMilliseconds(1_785_660_000_000L);

    [Theory]
    [InlineData(RustyKioskProductChannel.Stable, "io.github.mesmerprism.rustykiosk", "io.github.mesmerprism.rustykiosk.operator")]
    [InlineData(RustyKioskProductChannel.Labs, "io.github.mesmerprism.rustykiosk.labs", "io.github.mesmerprism.rustykiosk.labs.operator")]
    public async Task Connect_BindsExactUsbSerialAndFixedProductIdentity(
        RustyKioskProductChannel channel,
        string packageName,
        string authority)
    {
        var runner = new BootstrapRunner(channel, enabledByRequest: true, Now);
        var client = new AdbClient("adb", runner);
        var bootstrapper = new RustyKioskUsbDirectLinkBootstrapper(client, () => Now);
        var session = await bootstrapper.ConnectAsync(
            "USB_TARGET",
            channel,
            operatorConfirmed: true,
            new HttpClient(new SessionStatusHandler(runner.Secret, runner.SessionId, runner.Generation)));

        Assert.Equal(packageName, session.Receipt.PackageName);
        Assert.Equal(OperatorMutationStage.Confirmed, session.Receipt.Stage);
        Assert.DoesNotContain(runner.SecretBase64, string.Join(" ", runner.AllArguments));
        Assert.All(runner.AllArguments.Where(value => value.Contains("content://", StringComparison.Ordinal)),
            value => Assert.Contains(authority, value));
        Assert.Contains("USB_TARGET", runner.AllArguments);

        await session.DisposeAsync();
        Assert.Equal(3, runner.SensitiveCalls);
        Assert.Equal(OperatorMutationStage.Confirmed, session.CleanupReceipt?.Stage);
        Assert.Contains(runner.AllArguments, value =>
            value.Contains("expected_bridge_generation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connect_PreservesPreexistingListenerOnDispose()
    {
        var runner = new BootstrapRunner(RustyKioskProductChannel.Labs, enabledByRequest: false, Now);
        var session = await new RustyKioskUsbDirectLinkBootstrapper(
                new AdbClient("adb", runner),
                () => Now)
            .ConnectAsync(
                "USB_TARGET",
                RustyKioskProductChannel.Labs,
                operatorConfirmed: true,
                new HttpClient(new SessionStatusHandler(runner.Secret, runner.SessionId, runner.Generation)));

        await session.DisposeAsync();
        Assert.Equal(1, runner.SensitiveCalls);
        Assert.Equal(OperatorMutationStage.Confirmed, session.CleanupReceipt?.Stage);
        Assert.Contains("preserved", session.CleanupReceipt?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connect_RetriesTransientListenerStartupUntilExactSessionStatus()
    {
        var runner = new BootstrapRunner(RustyKioskProductChannel.Stable, enabledByRequest: false, Now);
        var handler = new SessionStatusHandler(
            runner.Secret,
            runner.SessionId,
            runner.Generation,
            transientFailures: 2);

        await using var session = await new RustyKioskUsbDirectLinkBootstrapper(
                new AdbClient("adb", runner),
                () => Now)
            .ConnectAsync(
                "USB_TARGET",
                RustyKioskProductChannel.Stable,
                operatorConfirmed: true,
                new HttpClient(handler));

        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(OperatorMutationStage.Confirmed, session.Receipt.Stage);
    }

    [Fact]
    public async Task OwnedCleanup_PollsNoArgStatusUntilDisabledAndStopped()
    {
        var runner = new BootstrapRunner(
            RustyKioskProductChannel.Stable,
            enabledByRequest: true,
            Now,
            pendingCleanupStatusReads: 2);
        var session = await new RustyKioskUsbDirectLinkBootstrapper(
                new AdbClient("adb", runner),
                () => Now)
            .ConnectAsync(
                "USB_TARGET",
                RustyKioskProductChannel.Stable,
                operatorConfirmed: true,
                new HttpClient(new SessionStatusHandler(runner.Secret, runner.SessionId, runner.Generation)));

        var cleanup = await session.CloseAsync();

        Assert.Equal(OperatorMutationStage.Confirmed, cleanup.Stage);
        Assert.Equal(5, runner.SensitiveCalls);
        Assert.Equal(3, runner.AllArgumentSets.Count(arguments => arguments.Contains("direct-status")));
        Assert.All(
            runner.AllArgumentSets.Where(arguments => arguments.Contains("direct-status")),
            arguments => Assert.DoesNotContain("--arg", arguments));
    }

    [Fact]
    public async Task OwnedCleanup_ReportsUnknownWhenPostDisableGenerationChanges()
    {
        var runner = new BootstrapRunner(
            RustyKioskProductChannel.Labs,
            enabledByRequest: true,
            Now,
            pendingCleanupStatusReads: 1,
            crossedCleanupGeneration: true);
        var session = await new RustyKioskUsbDirectLinkBootstrapper(
                new AdbClient("adb", runner),
                () => Now)
            .ConnectAsync(
                "USB_TARGET",
                RustyKioskProductChannel.Labs,
                operatorConfirmed: true,
                new HttpClient(new SessionStatusHandler(runner.Secret, runner.SessionId, runner.Generation)));

        var cleanup = await session.CloseAsync();

        Assert.Equal(OperatorMutationStage.CleanupUnknown, cleanup.Stage);
        Assert.DoesNotContain(runner.SecretBase64, cleanup.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_RejectsWifiAliasEvenWhenAnotherUsbDeviceIsReady()
    {
        var runner = new BootstrapRunner(
            RustyKioskProductChannel.Stable,
            enabledByRequest: false,
            Now,
            devices: "List of devices attached\nUSB_OTHER\tdevice product:quest model:Quest\n192.0.2.4:5555\tdevice product:quest model:Quest\n");
        var bootstrapper = new RustyKioskUsbDirectLinkBootstrapper(new AdbClient("adb", runner), () => Now);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bootstrapper.ConnectAsync(
                "192.0.2.4:5555",
                RustyKioskProductChannel.Stable,
                operatorConfirmed: true));

        Assert.Contains("classic-USB", error.Message);
        Assert.Equal(0, runner.SensitiveCalls);
    }

    [Fact]
    public async Task Connect_RejectsStaleAuthenticatedRunningGenerationAndCleansOwnedListener()
    {
        var runner = new BootstrapRunner(RustyKioskProductChannel.Labs, enabledByRequest: true, Now);
        var bootstrapper = new RustyKioskUsbDirectLinkBootstrapper(new AdbClient("adb", runner), () => Now);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            bootstrapper.ConnectAsync(
                "USB_TARGET",
                RustyKioskProductChannel.Labs,
                operatorConfirmed: true,
                new HttpClient(new SessionStatusHandler(runner.Secret, runner.SessionId, 42))));

        Assert.DoesNotContain(runner.SecretBase64, error.ToString());
        Assert.Equal(3, runner.SensitiveCalls);
    }

    [Fact]
    public async Task SensitiveRunner_ClearsCapturedOutputAndNeverProjectsStreamsOnFailure()
    {
        const string secret = "SENSITIVE_BOOTSTRAP_SECRET_DO_NOT_PROJECT";
        var root = Path.Combine(Path.GetTempPath(), "qfm-sensitive-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var successScript = Path.Combine(root, "success.ps1");
        var failureScript = Path.Combine(root, "failure.ps1");
        try
        {
            await File.WriteAllTextAsync(successScript, $"[Console]::Out.Write('{secret}')");
            await File.WriteAllTextAsync(failureScript, $"[Console]::Out.Write('{secret}'); [Console]::Error.Write('{secret}'); exit 7");
            var runner = new CommandRunner();
            ReadOnlyMemory<byte> captured = default;
            var result = await runner.RunSensitiveAsync(
                "pwsh",
                ["-NoProfile", "-File", successScript],
                1024,
                1024,
                TimeSpan.FromSeconds(10),
                value =>
                {
                    captured = value;
                    return value.Length;
                });
            Assert.Equal(secret.Length, result.Value);
            Assert.All(captured.ToArray(), value => Assert.Equal(0, value));

            var error = await Assert.ThrowsAsync<SensitiveCommandException>(() =>
                runner.RunSensitiveAsync(
                    "pwsh",
                    ["-NoProfile", "-File", failureScript],
                    1024,
                    1024,
                    TimeSpan.FromSeconds(10),
                    _ => true));
            Assert.DoesNotContain(secret, error.ToString());
            Assert.DoesNotContain(secret, string.Join(" ", ["pwsh", "-NoProfile", "-File", failureScript]));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class BootstrapRunner : ISensitiveCommandRunner
    {
        private readonly RustyKioskProductContract _product;
        private readonly bool _enabledByRequest;
        private readonly DateTimeOffset _now;
        private readonly string _devices;
        private readonly bool _crossedCleanupGeneration;
        private int _pendingCleanupStatusReads;

        public BootstrapRunner(
            RustyKioskProductChannel channel,
            bool enabledByRequest,
            DateTimeOffset now,
            string? devices = null,
            int pendingCleanupStatusReads = 0,
            bool crossedCleanupGeneration = false)
        {
            _product = RustyKioskProductContract.For(channel);
            _enabledByRequest = enabledByRequest;
            _now = now;
            _devices = devices ??
                "List of devices attached\nUSB_TARGET\tdevice product:quest model:Quest_3\nUSB_OTHER\tdevice product:quest model:Quest_Pro\n";
            _pendingCleanupStatusReads = pendingCleanupStatusReads;
            _crossedCleanupGeneration = crossedCleanupGeneration;
            Secret = Enumerable.Range(1, 32).Select(index => (byte)index).ToArray();
            SecretBase64 = Convert.ToBase64String(Secret);
        }

        public byte[] Secret { get; }
        public string SecretBase64 { get; }
        public string SessionId { get; } = "session_test_0001";
        public long Generation { get; } = 41;
        public int SensitiveCalls { get; private set; }
        public List<string> AllArguments { get; } = [];
        public List<IReadOnlyList<string>> AllArgumentSets { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            AllArguments.AddRange(arguments);
            AllArgumentSets.Add(arguments.ToArray());
            var output = arguments.SequenceEqual(["devices", "-l"])
                ? _devices
                : arguments.Contains("pm")
                    ? $"package:{_product.MainPackage} uid:10234\n"
                    : $"Result: Bundle[{{accepted=true, completed=true, schema={RustyKioskContract.HostOperatorSuccessorSchema}, package={_product.MainPackage}, product_channel={_product.WireName}}}]";
            return Task.FromResult(new CommandResult(fileName, arguments, 0, output, string.Empty, TimeSpan.Zero));
        }

        public Task<SensitiveCommandResult<T>> RunSensitiveAsync<T>(
            string fileName,
            IReadOnlyList<string> arguments,
            int maximumStandardOutputBytes,
            int maximumStandardErrorBytes,
            TimeSpan timeout,
            Func<ReadOnlyMemory<byte>, T> parseStandardOutput,
            CancellationToken cancellationToken = default)
        {
            SensitiveCalls++;
            AllArguments.AddRange(arguments);
            AllArgumentSets.Add(arguments.ToArray());
            var disabling = arguments.Contains("direct-disable");
            var readingStatus = arguments.Contains("direct-status");
            var argumentList = arguments.ToList();
            var operationId = argumentList.Contains("--arg")
                ? argumentList[argumentList.IndexOf("--arg") + 1]
                : null;
            var disableRunning = _pendingCleanupStatusReads > 0;
            var cleanupRunning = readingStatus && _pendingCleanupStatusReads-- > 0;
            var output = disabling
                ? $"Result: Bundle[{{accepted=true, completed={!disableRunning}, schema={RustyKioskContract.DirectUsbBootstrapSchema}, operation_id={operationId}, product_channel={_product.WireName}, package={_product.MainPackage}, direct_enabled=false, direct_running={disableRunning.ToString().ToLowerInvariant()}, bridge_generation={Generation + 1}, operation_state={(disableRunning ? "pending" : "confirmed")}}}]"
                : readingStatus
                    ? $"Result: Bundle[{{accepted=true, completed={!cleanupRunning}, schema={RustyKioskContract.DirectUsbBootstrapSchema}, product_channel={_product.WireName}, package={_product.MainPackage}, direct_enabled=false, direct_running={cleanupRunning.ToString().ToLowerInvariant()}, bridge_generation={Generation + (_crossedCleanupGeneration ? 2 : 1)}, operation_state={(cleanupRunning ? "pending" : "confirmed")}}}]"
                : $"Result: Bundle[{{accepted=true, completed=false, schema={RustyKioskContract.DirectUsbBootstrapSchema}, operation_id={operationId}, product_channel={_product.WireName}, package={_product.MainPackage}, endpoint=http://192.0.2.44:39873, bridge_generation={Generation}, session_id={SessionId}, session_secret_base64={SecretBase64}, session_capability={RustyKioskDirectClient.ContractSchema}, expires_at_ms={_now.AddMinutes(5).ToUnixTimeMilliseconds()}, enabled_by_request={_enabledByRequest.ToString().ToLowerInvariant()}}}]";
            var bytes = Encoding.ASCII.GetBytes(output);
            try
            {
                return Task.FromResult(new SensitiveCommandResult<T>(
                    parseStandardOutput(bytes),
                    0,
                    TimeSpan.Zero));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private sealed class SessionStatusHandler(
        byte[] secret,
        string sessionId,
        long generation,
        int transientFailures = 0) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _requestCount);
            if (count <= transientFailures)
            {
                throw new HttpRequestException("Listener startup is still pending.");
            }
            Assert.Equal(sessionId, request.Headers.GetValues("X-Rusty-Session-Id").Single());
            var requestId = request.Headers.GetValues("X-Rusty-Request-Id").Single();
            var body = Encoding.UTF8.GetBytes(
                $"{{\"accepted\":true,\"schema\":\"{RustyKioskDirectClient.ContractSchema}\",\"endpoint\":\"http://192.0.2.44:39873\",\"installer_allowed\":true,\"staging_directory_kind\":\"app-owned\",\"message\":\"ready\",\"bridge_generation\":{generation},\"session_id\":\"{sessionId}\"}}");
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
            return Task.FromResult(response);
        }
    }
}
