using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class QuestConnectivityProviderTests
{
    private const string DeviceId = "quest-device-1";
    private const string Serial = "QUEST123";
    private const string PairingCode = "7K3M-P9TX-2Q8D-V4JW";
    private static readonly Uri Endpoint =
        new("http://192.0.2.42:39873/");
    private static readonly DateTimeOffset ContractNow =
        DateTimeOffset.FromUnixTimeMilliseconds(1_900_000_000_000);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void RequestContract_IsStrictFreshAndAdmitsOnlyClosedActions()
    {
        foreach (var action in QuestConnectivityContract.Actions)
            Request(action).Validate(ContractNow);

        var unsupported = Assert.Throws<QuestConnectivityProviderException>(
            () => (Request("status") with { Action = "adb_shell" })
                .Validate(ContractNow));
        Assert.Equal("requestBindingInvalid", unsupported.Code);

        var stale = Assert.Throws<QuestConnectivityProviderException>(
            () => (Request("status") with
            {
                IssuedAtMs = ContractNow.AddMinutes(-3)
                    .ToUnixTimeMilliseconds(),
                ExpiresAtMs = ContractNow.AddMinutes(-2)
                    .ToUnixTimeMilliseconds()
            }).Validate(ContractNow));
        Assert.Equal("requestFreshnessInvalid", stale.Code);
    }

    [Fact]
    public void ProviderProfile_RequiresExactDeviceBindingAndUsbSerial()
    {
        var json = Encoding.UTF8.GetBytes(
            """
            {
              "schema":"questionable.file_manager.quest_connectivity_profile.v1",
              "device_id":"quest-device-1",
              "usb_serial":"QUEST123",
              "endpoint":"http://192.0.2.42:39873/",
              "pairing_code":"7K3M-P9TX-2Q8D-V4JW"
            }
            """);

        using var profile =
            WindowsCredentialQuestConnectivityProviderProfileStore
                .ParseProfile(DeviceId, json);

        Assert.Equal(DeviceId, profile.DeviceId);
        Assert.Equal(Serial, profile.UsbSerial);
        Assert.Equal(Endpoint, profile.Endpoint);
        Assert.Throws<QuestConnectivityProviderException>(
            () => WindowsCredentialQuestConnectivityProviderProfileStore
                .ParseProfile("another-device", json));
        Assert.Throws<QuestConnectivityProviderException>(
            () => WindowsCredentialQuestConnectivityProviderProfileStore
                .ParseProfile(
                    DeviceId,
                    Encoding.UTF8.GetBytes(
                        Encoding.UTF8.GetString(json)
                            .Replace(
                                "\"QUEST123\"",
                                "\"192.0.2.42:5555\"",
                                StringComparison.Ordinal))));
    }

    [Fact]
    public async Task WirelessRequest_RemainsPendingUntilKioskSettingReadback()
    {
        var effect = new FakeEffectOwner(
            command => KioskResult(
                command,
                wifiEnabled: false,
                requestAfterBoot: false));
        var controller = Controller(effect);

        var receipt = await controller.ExecuteAsync(
            Request("request_wireless_adb"));

        Assert.True(receipt.RequestDelivered);
        Assert.False(receipt.KioskSettingApplied);
        Assert.Equal("pending", receipt.WearerApproval);
        Assert.False(receipt.ListenerDiscovered);
        Assert.False(receipt.EffectApplied);
        Assert.Equal("wearer_approval_pending", receipt.Outcome);
    }

    [Fact]
    public async Task WirelessRequest_SettingReadbackDoesNotClaimWearerListenerOrTermux()
    {
        var controller = Controller(new FakeEffectOwner(
            command => KioskResult(
                command,
                wifiEnabled: true,
                requestAfterBoot: false)));

        var receipt = await controller.ExecuteAsync(
            Request("request_wireless_adb"));

        Assert.True(receipt.RequestDelivered);
        Assert.True(receipt.KioskSettingApplied);
        Assert.Equal("pending", receipt.WearerApproval);
        Assert.True(receipt.EffectApplied);
        Assert.False(receipt.ListenerDiscovered);
        Assert.Equal("wireless_adb_request_applied", receipt.Outcome);
    }

    [Theory]
    [InlineData("enable_request_after_boot", true, true,
        "request_after_boot_enabled")]
    [InlineData("disable_request_after_boot", false, true,
        "request_after_boot_disabled")]
    [InlineData("enable_request_after_boot", false, false,
        "readback_mismatch")]
    [InlineData("disable_request_after_boot", true, false,
        "readback_mismatch")]
    public async Task BootRequestActions_RequireExactKioskReadback(
        string action,
        bool requestAfterBoot,
        bool effective,
        string outcome)
    {
        var controller = Controller(new FakeEffectOwner(
            command => KioskResult(
                command,
                wifiEnabled: false,
                requestAfterBoot: requestAfterBoot)));

        var receipt = await controller.ExecuteAsync(Request(action));

        Assert.Equal(requestAfterBoot, receipt.RequestAfterBootEnabled);
        Assert.Equal(effective, receipt.KioskSettingApplied);
        Assert.Equal(effective, receipt.EffectApplied);
        Assert.Equal("not_applicable", receipt.WearerApproval);
        Assert.Equal(outcome, receipt.Outcome);
    }

    [Fact]
    public async Task ClassicTcpip_UsesExactUsbSerialAndVerifiesExactEndpoint()
    {
        var calls = new List<IReadOnlyList<string>>();
        var runner = new DelegatingRunner(arguments =>
        {
            calls.Add(arguments.ToArray());
            if (arguments.SequenceEqual(
                    ["-s", Serial, "shell", "getprop", "ro.serialno"]))
            {
                return Success(arguments, "stable-quest-identity\n");
            }
            if (arguments.SequenceEqual(
                    [
                        "-s",
                        "192.0.2.42:5555",
                        "shell",
                        "getprop",
                        "ro.serialno"
                    ]))
            {
                return Success(arguments, "stable-quest-identity\n");
            }
            if (arguments.SequenceEqual(
                    ["-s", Serial, "shell", "ip route"]))
            {
                return Success(
                    arguments,
                    "192.0.2.0/24 dev wlan0 proto kernel scope link src 192.0.2.42 metric 303\n");
            }
            if (arguments.SequenceEqual(
                    ["-s", Serial, "tcpip", "5555"]))
            {
                return Success(
                    arguments,
                    "restarting in TCP mode port: 5555\n");
            }
            if (arguments.SequenceEqual(
                    ["connect", "192.0.2.42:5555"]))
            {
                return Success(
                    arguments,
                    "connected to 192.0.2.42:5555\n");
            }
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success(
                    arguments,
                    "List of devices attached\n" +
                    "192.0.2.42:5555 device product:eureka model:Quest_3\n");
            }
            return new CommandResult(
                "adb-test",
                arguments,
                1,
                string.Empty,
                "unexpected",
                TimeSpan.Zero);
        });
        var owner = new QuestConnectivityEffectOwner(
            () => new AdbClient("adb-test", runner),
            _ => throw new InvalidOperationException(
                "Kiosk must be unreachable for classic setup."));
        var controller = new QuestConnectivityProviderController(
            new FakeProfileStore(),
            owner,
            new FixedTimeProvider(ContractNow));

        var receipt = await controller.ExecuteAsync(
            Request("enable_classic_tcpip_from_usb"));

        Assert.Equal(
            [
                new[] { "-s", Serial, "shell", "getprop", "ro.serialno" },
                new[] { "-s", Serial, "shell", "ip route" },
                new[] { "-s", Serial, "tcpip", "5555" },
                new[] { "connect", "192.0.2.42:5555" },
                new[] { "devices", "-l" },
                new[]
                {
                    "-s",
                    "192.0.2.42:5555",
                    "shell",
                    "getprop",
                    "ro.serialno"
                }
            ],
            calls);
        Assert.Equal("classic_tcpip", receipt.RouteMode);
        Assert.True(receipt.RequestDelivered);
        Assert.False(receipt.KioskSettingApplied);
        Assert.True(receipt.ListenerDiscovered);
        Assert.True(receipt.EffectApplied);
    }

    [Fact]
    public async Task Subprocess_RejectsBroadArgumentsAndJsonBeforeInitialization()
    {
        var factoryCalls = 0;
        var host = new QuestConnectivityProviderSubprocessHost(
            () =>
            {
                factoryCalls++;
                throw new InvalidOperationException(
                    "Provider initialization must remain unreachable.");
            },
            new FixedTimeProvider(ContractNow));

        await AssertRejectedAsync(
            host,
            ["wifi", "enable"],
            "{}",
            "providerArgumentsInvalid");
        await AssertRejectedAsync(
            host,
            ["integration", "quest-connectivity", "--json"],
            """{"schema":"rusty.fleet.quest_wifi_adb_owner_invocation.v1","unexpected":true}""",
            "requestJsonInvalid");
        await AssertRejectedAsync(
            host,
            ["integration", "quest-connectivity", "--json"],
            """
            {
              "schema":"rusty.fleet.quest_wifi_adb_owner_invocation.v1",
              "schema":"rusty.fleet.quest_wifi_adb_owner_invocation.v1"
            }
            """,
            "requestJsonInvalid");
        await AssertRejectedAsync(
            host,
            ["integration", "quest-connectivity", "--json"],
            "{\"padding\":\"" +
            new string('x', QuestConnectivityContract.MaximumRequestBytes) +
            "\"}",
            "requestOversized");

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Subprocess_RejectsRequestAndOperationReplayBeforeEffectOwner()
    {
        var effect = new FakeEffectOwner(
            command => KioskResult(
                command,
                wifiEnabled: false,
                requestAfterBoot: false));
        var host = new QuestConnectivityProviderSubprocessHost(
            () => Controller(effect),
            new FixedTimeProvider(ContractNow));
        var request = Request("status");

        var first = await ExecuteHostAsync(host, request);
        var repeatedRequest = await ExecuteHostAsync(host, request);
        var repeatedOperation = await ExecuteHostAsync(
            host,
            request with { RequestId = "request-2" });

        Assert.Equal(0, first.ExitCode);
        Assert.Equal("verified", first.Response.Status);
        Assert.Equal(2, repeatedRequest.ExitCode);
        Assert.Equal("requestReplay", repeatedRequest.Response.Error);
        Assert.Equal(2, repeatedOperation.ExitCode);
        Assert.Equal("operationReplay", repeatedOperation.Response.Error);
        Assert.Equal(1, effect.KioskInvocations);
    }

    [Fact]
    public async Task SubprocessReceipt_ExcludesSerialEndpointPairingAndRawOwnerData()
    {
        var privateRaw = Serial + " " + Endpoint + " " + PairingCode;
        var effect = new FakeEffectOwner(
            command => KioskResult(
                command,
                wifiEnabled: true,
                requestAfterBoot: true,
                rawJson: privateRaw));
        var host = new QuestConnectivityProviderSubprocessHost(
            () => Controller(effect),
            new FixedTimeProvider(ContractNow));
        await using var input = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(
                Request("status"),
                Json));
        await using var output = new MemoryStream();

        var exitCode = await host.RunAsync(
            ["integration", "quest-connectivity", "--json"],
            input,
            output);
        var responseJson = Encoding.UTF8.GetString(output.ToArray());
        var response =
            JsonSerializer.Deserialize<QuestConnectivityProviderResponse>(
                responseJson,
                Json);
        using var document = JsonDocument.Parse(responseJson);
        Assert.Equal(
            ["receipt", "schema", "status"],
            document.RootElement.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            [
                "action",
                "device_id",
                "effect_applied",
                "evidence_sha256",
                "identity_revision",
                "kiosk_setting_applied",
                "listener_discovered",
                "observed_at_ms",
                "operation_id",
                "outcome",
                "preview_id",
                "request_after_boot_enabled",
                "request_delivered",
                "request_id",
                "route_mode",
                "schema",
                "wearer_approval"
            ],
            document.RootElement.GetProperty("receipt")
                .EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());

        Assert.Equal(0, exitCode);
        Assert.Equal("verified", response?.Status);
        Assert.True(response?.Receipt?.EffectApplied);
        Assert.True(response?.Receipt?.RequestDelivered);
        Assert.Equal("unknown", response?.Receipt?.WearerApproval);
        Assert.False(response?.Receipt?.ListenerDiscovered);
        Assert.DoesNotContain(
            "termux",
            responseJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Serial, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Endpoint.Host,
            responseJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            PairingCode,
            responseJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            privateRaw,
            responseJson,
            StringComparison.Ordinal);
    }

    private static QuestConnectivityProviderController Controller(
        IQuestConnectivityEffectOwner effectOwner) =>
        new(
            new FakeProfileStore(),
            effectOwner,
            new FixedTimeProvider(ContractNow));

    private static QuestConnectivityProviderRequest Request(string action) =>
        new(
            QuestConnectivityContract.RequestSchema,
            "request-1",
            "operation-1",
            "preview-1",
            DeviceId,
            7,
            action,
            ContractNow.ToUnixTimeMilliseconds(),
            ContractNow.AddMinutes(1).ToUnixTimeMilliseconds());

    private static RustyKioskOperatorResult KioskResult(
        RustyKioskCommand command,
        bool wifiEnabled,
        bool requestAfterBoot,
        string rawJson = "{}")
    {
        var state = new RustyKioskState(
            0,
            0,
            0,
            false,
            [],
            string.Empty,
            null,
            null,
            null,
            null,
            false,
            false,
            wifiEnabled,
            true,
            true,
            requestAfterBoot,
            false,
            false,
            null,
            "ready",
            RustyKioskContract.TagFilePath);
        return new RustyKioskOperatorResult(
            RustyKioskContract.ResultSchema,
            "kiosk_request_0001",
            command,
            true,
            true,
            "bounded",
            state,
            rawJson);
    }

    private static CommandResult Success(
        IReadOnlyList<string> arguments,
        string output) =>
        new(
            "adb-test",
            arguments,
            0,
            output,
            string.Empty,
            TimeSpan.Zero);

    private static async Task AssertRejectedAsync(
        QuestConnectivityProviderSubprocessHost host,
        string[] arguments,
        string requestJson,
        string error)
    {
        await using var input = new MemoryStream(
            Encoding.UTF8.GetBytes(requestJson));
        await using var output = new MemoryStream();

        var exitCode = await host.RunAsync(arguments, input, output);
        var response =
            JsonSerializer.Deserialize<QuestConnectivityProviderResponse>(
                Encoding.UTF8.GetString(output.ToArray()),
                Json);

        Assert.Equal(2, exitCode);
        Assert.Equal("rejected", response?.Status);
        Assert.Equal(error, response?.Error);
    }

    private static async Task<(
        int ExitCode,
        QuestConnectivityProviderResponse Response)> ExecuteHostAsync(
        QuestConnectivityProviderSubprocessHost host,
        QuestConnectivityProviderRequest request)
    {
        await using var input = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(request, Json));
        await using var output = new MemoryStream();
        var exitCode = await host.RunAsync(
            ["integration", "quest-connectivity", "--json"],
            input,
            output);
        var response =
            JsonSerializer.Deserialize<QuestConnectivityProviderResponse>(
                Encoding.UTF8.GetString(output.ToArray()),
                Json) ??
            throw new InvalidDataException(
                "Provider response was not returned.");
        return (exitCode, response);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeProfileStore :
        IQuestConnectivityProviderProfileStore
    {
        public QuestConnectivityProviderProfile Open(string deviceId) =>
            new(
                deviceId,
                Serial,
                Endpoint,
                PairingCode.ToCharArray());
    }

    private sealed class FakeEffectOwner(
        Func<RustyKioskCommand, RustyKioskOperatorResult> kiosk)
        : IQuestConnectivityEffectOwner
    {
        private int _kioskInvocations;

        public int KioskInvocations =>
            Volatile.Read(ref _kioskInvocations);

        public Task<QuestConnectivityEffectOwnerResult> InvokeKioskAsync(
            QuestConnectivityProviderProfile profile,
            RustyKioskCommand command,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _kioskInvocations);
            return Task.FromResult(
                new QuestConnectivityEffectOwnerResult(kiosk(command), null));
        }

        public Task<QuestConnectivityEffectOwnerResult>
            EnableClassicTcpipFromUsbAsync(
                QuestConnectivityProviderProfile profile,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Classic mode was not configured for this test.");
    }

    private sealed class DelegatingRunner(
        Func<IReadOnlyList<string>, CommandResult> run)
        : ICommandRunner
    {
        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(run(arguments));
    }
}
