using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.Core;

public static class QuestConnectivityContract
{
    public const string RequestSchema =
        "rusty.fleet.quest_wifi_adb_owner_invocation.v1";
    public const string ResponseSchema =
        "questionable.file_manager.quest_wifi_adb_provider_response.v1";
    public const string ReceiptSchema =
        "questionable.file_manager.quest_wifi_adb_receipt.v1";
    public const int MaximumRequestBytes = 16 * 1024;
    public const int MaximumIdentifierLength = 256;
    public static readonly TimeSpan MaximumRequestLifetime =
        TimeSpan.FromMinutes(2);

    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(
        [
            "status",
            "request_wireless_adb",
            "enable_request_after_boot",
            "disable_request_after_boot",
            "disable_wireless_adb",
            "enable_classic_tcpip_from_usb"
        ],
        StringComparer.Ordinal);

    public static bool IsIdentifier(string? value) =>
        value is not null &&
        value.Length is > 0 and <= MaximumIdentifierLength &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-');
}

public sealed record QuestConnectivityProviderRequest(
    string Schema,
    string RequestId,
    string OperationId,
    string PreviewId,
    string DeviceId,
    ulong IdentityRevision,
    string Action,
    long IssuedAtMs,
    long ExpiresAtMs)
{
    public void Validate(DateTimeOffset now)
    {
        if (!string.Equals(
                Schema,
                QuestConnectivityContract.RequestSchema,
                StringComparison.Ordinal))
        {
            throw QuestConnectivityProviderException.Rejected(
                "requestSchemaUnsupported");
        }

        foreach (var value in new[]
                 {
                     RequestId,
                     OperationId,
                     PreviewId,
                     DeviceId
                 })
        {
            if (!QuestConnectivityContract.IsIdentifier(value))
                throw QuestConnectivityProviderException.Rejected(
                    "requestBindingInvalid");
        }

        if (IdentityRevision == 0 ||
            !QuestConnectivityContract.Actions.Contains(Action))
        {
            throw QuestConnectivityProviderException.Rejected(
                "requestBindingInvalid");
        }

        var nowMs = now.ToUnixTimeMilliseconds();
        if (IssuedAtMs <= 0 ||
            ExpiresAtMs <= IssuedAtMs ||
            ExpiresAtMs - IssuedAtMs >
                QuestConnectivityContract.MaximumRequestLifetime.TotalMilliseconds ||
            nowMs < IssuedAtMs - 30_000 ||
            nowMs > ExpiresAtMs)
        {
            throw QuestConnectivityProviderException.Rejected(
                "requestFreshnessInvalid");
        }
    }
}

public sealed record QuestConnectivityProviderReceipt(
    string Schema,
    string RequestId,
    string OperationId,
    string PreviewId,
    string DeviceId,
    ulong IdentityRevision,
    string Action,
    string RouteMode,
    bool RequestDelivered,
    bool KioskSettingApplied,
    bool? RequestAfterBootEnabled,
    string WearerApproval,
    bool ListenerDiscovered,
    bool EffectApplied,
    string Outcome,
    string EvidenceSha256,
    long ObservedAtMs);

public sealed record QuestConnectivityProviderResponse(
    string Schema,
    string Status,
    QuestConnectivityProviderReceipt? Receipt = null,
    string? Error = null,
    string? Message = null);

public sealed class QuestConnectivityProviderException
    : InvalidOperationException
{
    private QuestConnectivityProviderException(
        string code,
        string message,
        bool unavailable)
        : base(message)
    {
        Code = code;
        IsUnavailable = unavailable;
    }

    public string Code { get; }
    public bool IsUnavailable { get; }

    public static QuestConnectivityProviderException Rejected(string code) =>
        new(code, "The connectivity provider rejected the typed request.", false);

    public static QuestConnectivityProviderException Unavailable(string code) =>
        new(code, "The File Manager-owned connectivity profile is unavailable.", true);
}

public sealed record QuestConnectivityEffectOwnerResult(
    RustyKioskOperatorResult? KioskResult,
    WifiAdbEnableResult? ClassicResult);

public interface IQuestConnectivityEffectOwner
{
    Task<QuestConnectivityEffectOwnerResult> InvokeKioskAsync(
        QuestConnectivityProviderProfile profile,
        RustyKioskCommand command,
        CancellationToken cancellationToken);

    Task<QuestConnectivityEffectOwnerResult> EnableClassicTcpipFromUsbAsync(
        QuestConnectivityProviderProfile profile,
        CancellationToken cancellationToken);
}

public sealed class QuestConnectivityEffectOwner(
    Func<AdbClient>? adbClientFactory = null,
    Func<RustyKioskDirectEndpoint, RustyKioskDirectClient>? kioskClientFactory = null)
    : IQuestConnectivityEffectOwner
{
    private readonly Func<AdbClient> _adbClientFactory =
        adbClientFactory ?? (() => AdbClient.CreateDefault());
    private readonly Func<RustyKioskDirectEndpoint, RustyKioskDirectClient>
        _kioskClientFactory =
            kioskClientFactory ?? (endpoint => new RustyKioskDirectClient(endpoint));

    public async Task<QuestConnectivityEffectOwnerResult> InvokeKioskAsync(
        QuestConnectivityProviderProfile profile,
        RustyKioskCommand command,
        CancellationToken cancellationToken)
    {
        var client = _kioskClientFactory(profile.CreateDirectEndpoint());
        var result = await client.InvokeKioskAsync(
            command,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new QuestConnectivityEffectOwnerResult(result, null);
    }

    public async Task<QuestConnectivityEffectOwnerResult>
        EnableClassicTcpipFromUsbAsync(
            QuestConnectivityProviderProfile profile,
            CancellationToken cancellationToken)
    {
        var result = await _adbClientFactory()
            .EnableWifiAdbAndConnectAsync(
                profile.UsbSerial,
                5555,
                cancellationToken)
            .ConfigureAwait(false);
        return new QuestConnectivityEffectOwnerResult(null, result);
    }
}

public sealed class QuestConnectivityProviderController(
    IQuestConnectivityProviderProfileStore profileStore,
    IQuestConnectivityEffectOwner effectOwner,
    TimeProvider? timeProvider = null)
{
    private readonly IQuestConnectivityProviderProfileStore _profileStore =
        profileStore ?? throw new ArgumentNullException(nameof(profileStore));
    private readonly IQuestConnectivityEffectOwner _effectOwner =
        effectOwner ?? throw new ArgumentNullException(nameof(effectOwner));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? TimeProvider.System;

    public async Task<QuestConnectivityProviderReceipt> ExecuteAsync(
        QuestConnectivityProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate(_timeProvider.GetUtcNow());
        using var profile = _profileStore.Open(request.DeviceId);
        if (!string.Equals(
                profile.DeviceId,
                request.DeviceId,
                StringComparison.Ordinal))
        {
            throw QuestConnectivityProviderException.Unavailable(
                "providerProfileBindingInvalid");
        }

        request.Validate(_timeProvider.GetUtcNow());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var remaining = request.ExpiresAtMs -
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        deadline.CancelAfter(TimeSpan.FromMilliseconds(
            Math.Min(20_000, Math.Max(1, remaining))));

        QuestConnectivityEffectOwnerResult result;
        if (request.Action == "enable_classic_tcpip_from_usb")
        {
            result = await _effectOwner.EnableClassicTcpipFromUsbAsync(
                profile,
                deadline.Token).ConfigureAwait(false);
        }
        else
        {
            var command = request.Action switch
            {
                "status" => RustyKioskCommand.Status,
                "request_wireless_adb" => RustyKioskCommand.RequestWifiAdb,
                "enable_request_after_boot" =>
                    RustyKioskCommand.EnableWifiAfterBoot,
                "disable_request_after_boot" =>
                    RustyKioskCommand.DisableWifiAfterBoot,
                "disable_wireless_adb" =>
                    RustyKioskCommand.DisableWifiAdb,
                _ => throw QuestConnectivityProviderException.Rejected(
                    "actionUnsupported")
            };
            result = await _effectOwner.InvokeKioskAsync(
                profile,
                command,
                deadline.Token).ConfigureAwait(false);
        }

        return CreateReceipt(request, profile, result);
    }

    private QuestConnectivityProviderReceipt CreateReceipt(
        QuestConnectivityProviderRequest request,
        QuestConnectivityProviderProfile profile,
        QuestConnectivityEffectOwnerResult result)
    {
        var classic = result.ClassicResult;
        var kiosk = result.KioskResult;
        var routeMode = classic is null ? "modern_tls" : "classic_tcpip";
        var requestDelivered =
            classic is not null || kiosk?.Accepted == true;
        bool kioskSettingApplied;
        bool? requestAfterBootEnabled;
        string wearerApproval;
        bool listenerDiscovered;
        bool effectApplied;
        string outcome;

        if (classic is not null)
        {
            kioskSettingApplied = false;
            requestAfterBootEnabled = null;
            wearerApproval = "not_applicable";
            listenerDiscovered = classic.Connection.Device.IsReady;
            effectApplied = listenerDiscovered;
            outcome = effectApplied
                ? "classic_tcpip_ready"
                : "classic_tcpip_readback_mismatch";
        }
        else
        {
            var state = kiosk?.State ??
                throw new InvalidOperationException(
                    "Kiosk effect-owner readback was not returned.");
            requestAfterBootEnabled = state.RequestWifiAdbAfterBoot;
            listenerDiscovered = false;
            switch (request.Action)
            {
                case "status":
                    kioskSettingApplied = state.WifiAdbEnabled;
                    wearerApproval = "unknown";
                    effectApplied = true;
                    outcome = state.WifiAdbEnabled
                        ? "observed_wireless_adb_enabled"
                        : "observed_wireless_adb_disabled";
                    break;
                case "request_wireless_adb":
                    kioskSettingApplied = state.WifiAdbEnabled;
                    wearerApproval =
                        requestDelivered ? "pending" : "unknown";
                    effectApplied = kioskSettingApplied;
                    outcome = effectApplied
                        ? "wireless_adb_request_applied"
                        : requestDelivered
                            ? "wearer_approval_pending"
                            : "request_not_delivered";
                    break;
                case "enable_request_after_boot":
                    kioskSettingApplied = state.RequestWifiAdbAfterBoot;
                    wearerApproval = "not_applicable";
                    effectApplied = state.RequestWifiAdbAfterBoot;
                    outcome = effectApplied
                        ? "request_after_boot_enabled"
                        : "readback_mismatch";
                    break;
                case "disable_request_after_boot":
                    kioskSettingApplied = !state.RequestWifiAdbAfterBoot;
                    wearerApproval = "not_applicable";
                    effectApplied = !state.RequestWifiAdbAfterBoot;
                    outcome = effectApplied
                        ? "request_after_boot_disabled"
                        : "readback_mismatch";
                    break;
                case "disable_wireless_adb":
                    kioskSettingApplied = !state.WifiAdbEnabled;
                    wearerApproval = "not_applicable";
                    effectApplied = !state.WifiAdbEnabled;
                    outcome = effectApplied
                        ? "wireless_adb_disabled"
                        : "readback_mismatch";
                    break;
                default:
                    throw QuestConnectivityProviderException.Rejected(
                        "actionUnsupported");
            }
        }

        var evidenceSha256 = ComputeEvidenceDigest(
            request,
            profile,
            result);
        return new QuestConnectivityProviderReceipt(
            QuestConnectivityContract.ReceiptSchema,
            request.RequestId,
            request.OperationId,
            request.PreviewId,
            request.DeviceId,
            request.IdentityRevision,
            request.Action,
            routeMode,
            requestDelivered,
            kioskSettingApplied,
            requestAfterBootEnabled,
            wearerApproval,
            listenerDiscovered,
            effectApplied,
            outcome,
            evidenceSha256,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private static string ComputeEvidenceDigest(
        QuestConnectivityProviderRequest request,
        QuestConnectivityProviderProfile profile,
        QuestConnectivityEffectOwnerResult result)
    {
        static object? CommandEvidence(CommandResult? command) =>
            command is null
                ? null
                : new
                {
                    argumentsSha256 = Sha256(
                        Encoding.UTF8.GetBytes(
                            string.Join("\0", command.Arguments))),
                    command.ExitCode,
                    outputSha256 = Sha256(
                        Encoding.UTF8.GetBytes(
                            command.StandardOutput + "\0" +
                            command.StandardError))
                };

        var classic = result.ClassicResult;
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.Schema,
            request.RequestId,
            request.OperationId,
            request.PreviewId,
            request.DeviceId,
            request.IdentityRevision,
            request.Action,
            request.IssuedAtMs,
            request.ExpiresAtMs,
            profileBindingSha256 = Sha256(
                Encoding.UTF8.GetBytes(
                    profile.DeviceId + "\0" +
                    profile.UsbSerial + "\0" +
                    profile.Endpoint.AbsoluteUri)),
            kioskRawSha256 = result.KioskResult is null
                ? null
                : Sha256(Encoding.UTF8.GetBytes(result.KioskResult.RawJson)),
            kioskAccepted = result.KioskResult?.Accepted,
            kioskCompleted = result.KioskResult?.Completed,
            kioskState = result.KioskResult?.State,
            classicAddressProbe = CommandEvidence(classic?.AddressProbe),
            classicTcpip = CommandEvidence(classic?.TcpIpCommand),
            classicConnect = CommandEvidence(
                classic?.Connection.CommandResult),
            classicDeviceIdentitySha256 =
                classic?.DeviceIdentitySha256,
            classicDeviceReady = classic?.Connection.Device.IsReady
        });
        return Sha256(canonical);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class QuestConnectivityProviderSubprocessHost(
    Func<QuestConnectivityProviderController> controllerFactory,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly Func<QuestConnectivityProviderController>
        _controllerFactory =
            controllerFactory ??
            throw new ArgumentNullException(nameof(controllerFactory));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? TimeProvider.System;
    private readonly object _replayGate = new();
    private readonly HashSet<string> _requestIds =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _operationIds =
        new(StringComparer.Ordinal);

    public static QuestConnectivityProviderSubprocessHost CreateWindows() =>
        new(() => new QuestConnectivityProviderController(
            new WindowsCredentialQuestConnectivityProviderProfileStore(),
            new QuestConnectivityEffectOwner()));

    public async Task<int> RunAsync(
        string[] arguments,
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Length != 3 ||
            arguments[0] != "integration" ||
            arguments[1] != "quest-connectivity" ||
            arguments[2] != "--json")
        {
            await WriteAsync(
                output,
                new QuestConnectivityProviderResponse(
                    QuestConnectivityContract.ResponseSchema,
                    "rejected",
                    Error: "providerArgumentsInvalid",
                    Message:
                    "Expected exactly: integration quest-connectivity --json"),
                cancellationToken).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var bytes = await ReadBoundedAsync(input, cancellationToken)
                .ConfigureAwait(false);
            RejectDuplicateProperties(bytes);
            var request = JsonSerializer.Deserialize<
                    QuestConnectivityProviderRequest>(bytes, Json) ??
                throw QuestConnectivityProviderException.Rejected(
                    "requestJsonInvalid");
            request.Validate(_timeProvider.GetUtcNow());
            AdmitOnce(request);
            var receipt = await _controllerFactory()
                .ExecuteAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var verified =
                request.Action == "status" || receipt.EffectApplied;
            await WriteAsync(
                output,
                new QuestConnectivityProviderResponse(
                    QuestConnectivityContract.ResponseSchema,
                    verified ? "verified" : "pending",
                    receipt),
                cancellationToken).ConfigureAwait(false);
            return verified ? 0 : 3;
        }
        catch (QuestConnectivityProviderException exception)
        {
            var status = exception.IsUnavailable ? "failed" : "rejected";
            await WriteAsync(
                output,
                new QuestConnectivityProviderResponse(
                    QuestConnectivityContract.ResponseSchema,
                    status,
                    Error: exception.Code,
                    Message: exception.Message),
                cancellationToken).ConfigureAwait(false);
            return exception.IsUnavailable ? 1 : 2;
        }
        catch (JsonException)
        {
            await WriteAsync(
                output,
                new QuestConnectivityProviderResponse(
                    QuestConnectivityContract.ResponseSchema,
                    "rejected",
                    Error: "requestJsonInvalid",
                    Message: "The provider request must be strict valid JSON."),
                cancellationToken).ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException)
        {
            await WriteAsync(
                output,
                new QuestConnectivityProviderResponse(
                    QuestConnectivityContract.ResponseSchema,
                    "cancelled",
                    Error: "cancelled",
                    Message: "The provider request was cancelled."),
                CancellationToken.None).ConfigureAwait(false);
            return 4;
        }
        catch
        {
            await WriteAsync(
                output,
                new QuestConnectivityProviderResponse(
                    QuestConnectivityContract.ResponseSchema,
                    "failed",
                    Error: "providerFailed",
                    Message:
                    "The connectivity provider could not complete the typed request."),
                CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
                break;
            if (memory.Length + count >
                QuestConnectivityContract.MaximumRequestBytes)
            {
                throw QuestConnectivityProviderException.Rejected(
                    "requestOversized");
            }
            memory.Write(buffer, 0, count);
        }

        if (memory.Length < 2)
            throw QuestConnectivityProviderException.Rejected(
                "requestJsonInvalid");
        return memory.ToArray();
    }

    private void AdmitOnce(QuestConnectivityProviderRequest request)
    {
        lock (_replayGate)
        {
            if (_requestIds.Contains(request.RequestId))
            {
                throw QuestConnectivityProviderException.Rejected(
                    "requestReplay");
            }
            if (_operationIds.Contains(request.OperationId))
            {
                throw QuestConnectivityProviderException.Rejected(
                    "operationReplay");
            }
            _requestIds.Add(request.RequestId);
            _operationIds.Add(request.OperationId);
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        var scopes = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                scopes.Push(new HashSet<string>(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject)
                scopes.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName &&
                     scopes.TryPeek(out var scope) &&
                     !scope.Add(reader.GetString() ?? string.Empty))
            {
                throw QuestConnectivityProviderException.Rejected(
                    "requestJsonInvalid");
            }
        }
    }

    private static async Task WriteAsync(
        Stream output,
        QuestConnectivityProviderResponse response,
        CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(
                output,
                response,
                Json,
                cancellationToken)
            .ConfigureAwait(false);
        await output.WriteAsync(
            "\n"u8.ToArray(),
            cancellationToken).ConfigureAwait(false);
    }
}
