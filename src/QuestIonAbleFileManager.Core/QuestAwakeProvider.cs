using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.Core;

public static class QuestAwakeContract
{
    public const string Version = "questionable.file_manager.fleet_awake_provider.v1";
    public const string ReceiptSchema = "questionable.file_manager.quest_awake_receipt.v1";
    public const int MinimumHoldDurationMilliseconds = 60_000;
    public const int MaximumHoldDurationMilliseconds = 28_800_000;
    public const int MinimumWatchdogIntervalMilliseconds = 1_000;
    public const int MaximumWatchdogIntervalMilliseconds = 60_000;
    public const int DefaultWatchdogIntervalMilliseconds = 5_000;
    public const int MaximumRequestBytes = 16 * 1024;
    public const int MaximumIdentifierLength = 256;

    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(
        [
            "status",
            "applyBounded",
            "repairOnce",
            "startDeviceWatchdog",
            "stopWatchdogs",
            "restoreNormal"
        ],
        StringComparer.Ordinal);
}

public sealed record QuestAwakeProviderRequest(
    string ContractVersion,
    string RequestId,
    string OperationId,
    string PreviewId,
    string DeviceId,
    ulong IdentityRevision,
    string Action,
    int DurationMilliseconds,
    int WatchdogIntervalMilliseconds,
    string WatchdogGeneration,
    long IssuedAtUnixMilliseconds,
    long ExpiresAtUnixMilliseconds,
    string Serial)
{
    public void Validate(DateTimeOffset now)
    {
        if (ContractVersion != QuestAwakeContract.Version)
            throw new QuestAwakeProviderException("contractUnsupported", "The provider contract version is unsupported.");
        foreach (var (name, value) in new[]
        {
            ("requestId", RequestId),
            ("operationId", OperationId),
            ("previewId", PreviewId),
            ("deviceId", DeviceId),
            ("watchdogGeneration", WatchdogGeneration)
        })
        {
            if (!IsPortableIdentifier(value))
                throw new QuestAwakeProviderException("identifierInvalid", $"{name} is not a bounded portable identifier.");
        }
        try
        {
            AndroidInput.RequireSerial(Serial);
        }
        catch (ArgumentException)
        {
            throw new QuestAwakeProviderException(
                "serialInvalid",
                "The provider request must bind one valid exact Quest serial.");
        }
        if (IdentityRevision == 0)
            throw new QuestAwakeProviderException("identityRevisionInvalid", "Identity revision must be greater than zero.");
        if (!QuestAwakeContract.Actions.Contains(Action))
            throw new QuestAwakeProviderException("actionUnsupported", "The requested awake action is unsupported.");
        if (DurationMilliseconds is < QuestAwakeContract.MinimumHoldDurationMilliseconds
            or > QuestAwakeContract.MaximumHoldDurationMilliseconds)
            throw new QuestAwakeProviderException("durationInvalid", "Keep-awake duration must be between one minute and eight hours.");
        if (WatchdogIntervalMilliseconds is < QuestAwakeContract.MinimumWatchdogIntervalMilliseconds
            or > QuestAwakeContract.MaximumWatchdogIntervalMilliseconds)
            throw new QuestAwakeProviderException("watchdogIntervalInvalid", "Watchdog interval is outside the supported bound.");
        var nowMilliseconds = now.ToUnixTimeMilliseconds();
        if (IssuedAtUnixMilliseconds < 0 ||
            ExpiresAtUnixMilliseconds <= IssuedAtUnixMilliseconds ||
            nowMilliseconds < IssuedAtUnixMilliseconds - 30_000 ||
            nowMilliseconds > ExpiresAtUnixMilliseconds)
            throw new QuestAwakeProviderException("requestExpired", "The provider request is outside its accepted time window.");
    }

    private static bool IsPortableIdentifier(string? value) =>
        value is not null &&
        value.Length is > 0 and <= QuestAwakeContract.MaximumIdentifierLength &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

public sealed record QuestDeviceAwakeWatchdogStatus(
    bool ReportedActive,
    [property: JsonIgnore] bool ProcessAlive,
    bool Fresh,
    string Generation,
    string BootId,
    int IntervalMilliseconds,
    long LastPollUnixMilliseconds,
    int ProximityRepairCount,
    int StayOnRepairCount,
    int WakeRepairCount,
    string LastAction,
    string LastError)
{
    public static QuestDeviceAwakeWatchdogStatus Inactive(string bootId = "") =>
        new(false, false, false, string.Empty, bootId, 0, 0, 0, 0, 0, "inactive", string.Empty);
}

public sealed record QuestAwakePowerReadback(
    string Wakefulness,
    string DisplayState,
    bool StayOn,
    bool? AutoSleepDisabled,
    string ProximityState,
    int? ProximityHoldDurationMilliseconds,
    int? ProximityHoldRemainingMilliseconds,
    long CapturedAtUnixMilliseconds)
{
    public static QuestAwakePowerReadback From(QuestControlStatus status) =>
        new(
            status.Wakefulness,
            status.DisplayState,
            status.StayOn,
            status.AutoSleepDisabled,
            status.ProximityState,
            status.ProximityHoldDurationMilliseconds,
            status.ProximityHoldRemainingMilliseconds,
            status.CapturedAt.ToUnixTimeMilliseconds());
}

public sealed record QuestAwakeProviderReceipt(
    string Schema,
    string ContractVersion,
    string RequestId,
    string OperationId,
    string PreviewId,
    string DeviceId,
    ulong IdentityRevision,
    string Action,
    string WatchdogGeneration,
    int RequestedDurationMilliseconds,
    int RequestedWatchdogIntervalMilliseconds,
    bool StayOnEffective,
    bool ProximityHoldEffective,
    bool WakeEffective,
    bool DeviceWatchdogEffective,
    bool SettingsRestored,
    bool Effective,
    bool SettingsLeftUnchanged,
    string Outcome,
    int RepairCount,
    QuestAwakePowerReadback PowerReadback,
    QuestDeviceAwakeWatchdogStatus DeviceWatchdog,
    string EvidenceSha256,
    long ObservedAtUnixMilliseconds);

public sealed record QuestAwakeProviderResponse(
    string ContractVersion,
    string Status,
    QuestAwakeProviderReceipt? Receipt = null,
    string? Error = null,
    string? Message = null);

public sealed class QuestAwakeProviderException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class QuestAwakeProviderController(
    AdbClient client,
    TimeProvider? timeProvider = null)
{
    private readonly AdbClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<QuestAwakeProviderReceipt> ExecuteAsync(
        QuestAwakeProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        request.Validate(now);
        IReadOnlyList<CommandResult> commands;
        QuestControlStatus power;
        QuestDeviceAwakeWatchdogStatus watchdog;
        var settingsLeftUnchanged = request.Action is "status" or "stopWatchdogs";
        void RequireCurrentMutationWindow() => request.Validate(_timeProvider.GetUtcNow());
        switch (request.Action)
        {
            case "status":
                commands = [];
                power = await _client.GetQuestControlStatusAsync(request.Serial, cancellationToken)
                    .ConfigureAwait(false);
                watchdog = await _client.GetQuestDeviceAwakeWatchdogStatusAsync(
                    request.Serial,
                    request.WatchdogIntervalMilliseconds,
                    cancellationToken).ConfigureAwait(false);
                break;
            case "applyBounded":
                {
                    var result = await _client.SetQuestKeepAwakeAsync(
                        request.Serial,
                        true,
                        request.DurationMilliseconds,
                        cancellationToken,
                        RequireCurrentMutationWindow).ConfigureAwait(false);
                    commands = result.Commands;
                    power = result.EffectiveStatus;
                    watchdog = await _client.GetQuestDeviceAwakeWatchdogStatusAsync(
                        request.Serial,
                        request.WatchdogIntervalMilliseconds,
                        cancellationToken).ConfigureAwait(false);
                    break;
                }
            case "repairOnce":
                {
                    var result = await _client.RepairQuestAwakeAsync(
                        request.Serial,
                        request.DurationMilliseconds,
                        cancellationToken,
                        RequireCurrentMutationWindow).ConfigureAwait(false);
                    commands = result.Commands;
                    power = result.EffectiveStatus;
                    watchdog = await _client.GetQuestDeviceAwakeWatchdogStatusAsync(
                        request.Serial,
                        request.WatchdogIntervalMilliseconds,
                        cancellationToken).ConfigureAwait(false);
                    break;
                }
            case "startDeviceWatchdog":
                {
                    var started = await _client.StartQuestDeviceAwakeWatchdogAsync(
                        request.Serial,
                        request.WatchdogGeneration,
                        request.DurationMilliseconds,
                        request.WatchdogIntervalMilliseconds,
                        cancellationToken,
                        RequireCurrentMutationWindow).ConfigureAwait(false);
                    commands = started.Commands;
                    power = started.EffectiveStatus;
                    watchdog = started.Watchdog;
                    break;
                }
            case "stopWatchdogs":
                {
                    var stopped = await _client.StopQuestDeviceAwakeWatchdogAsync(
                        request.Serial,
                        request.WatchdogGeneration,
                        request.WatchdogIntervalMilliseconds,
                        cancellationToken,
                        RequireCurrentMutationWindow).ConfigureAwait(false);
                    commands = stopped.Commands;
                    watchdog = stopped.Watchdog;
                    power = stopped.EffectiveStatus;
                    break;
                }
            case "restoreNormal":
                {
                    var stopped = await _client.StopQuestDeviceAwakeWatchdogAsync(
                        request.Serial,
                        request.WatchdogGeneration,
                        request.WatchdogIntervalMilliseconds,
                        cancellationToken,
                        RequireCurrentMutationWindow).ConfigureAwait(false);
                    if (stopped.Watchdog.ReportedActive || stopped.Watchdog.ProcessAlive)
                        throw new QuestAwakeProviderException(
                            "deviceWatchdogStopUnconfirmed",
                            "Normal settings cannot be restored until the device watchdog is proven inactive.");
                    RequireCurrentMutationWindow();
                    var restored = await _client.SetQuestKeepAwakeAsync(
                        request.Serial,
                        false,
                        request.DurationMilliseconds,
                        cancellationToken,
                        RequireCurrentMutationWindow).ConfigureAwait(false);
                    commands = stopped.Commands.Concat(restored.Commands).ToArray();
                    watchdog = stopped.Watchdog;
                    power = restored.EffectiveStatus;
                    break;
                }
            default:
                throw new QuestAwakeProviderException("actionUnsupported", "The requested awake action is unsupported.");
        }

        var stayOnEffective = power.StayOn;
        var proximityHoldEffective =
            string.Equals(power.ProximityState.Trim(), "CLOSE", StringComparison.OrdinalIgnoreCase) &&
            power.ProximityHoldDurationMilliseconds == request.DurationMilliseconds &&
            power.ProximityHoldRemainingMilliseconds is > 0;
        var wakeEffective =
            string.Equals(power.Wakefulness, "Awake", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(power.DisplayState, "ON", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(power.DisplayState, "ON_SUSPEND", StringComparison.OrdinalIgnoreCase));
        var deviceWatchdogEffective = watchdog.ReportedActive && watchdog.Fresh &&
            string.Equals(watchdog.Generation, request.WatchdogGeneration, StringComparison.Ordinal) &&
            watchdog.IntervalMilliseconds == request.WatchdogIntervalMilliseconds;
        var settingsRestored =
            !power.StayOn &&
            power.AutoSleepDisabled != true &&
            !string.Equals(power.ProximityState.Trim(), "CLOSE", StringComparison.OrdinalIgnoreCase);
        var effective = request.Action switch
        {
            "status" => true,
            "applyBounded" or "repairOnce" => stayOnEffective && proximityHoldEffective && wakeEffective,
            "startDeviceWatchdog" =>
                stayOnEffective && proximityHoldEffective && wakeEffective && deviceWatchdogEffective,
            "stopWatchdogs" => !watchdog.ReportedActive && !watchdog.ProcessAlive,
            "restoreNormal" =>
                !watchdog.ReportedActive && !watchdog.ProcessAlive && settingsRestored,
            _ => false
        };
        var outcome = request.Action switch
        {
            "status" => "observed",
            "stopWatchdogs" when effective => "watchdogsStoppedSettingsUnchanged",
            "restoreNormal" when effective => "restored",
            _ when effective => "effective",
            _ => "readbackMismatch"
        };
        var repairs = request.Action switch
        {
            "repairOnce" => commands.Count,
            "status" or "startDeviceWatchdog" =>
                watchdog.ProximityRepairCount +
                watchdog.StayOnRepairCount +
                watchdog.WakeRepairCount,
            _ => 0
        };
        var evidence = ComputeEvidenceDigest(request, power, watchdog, commands);
        return new QuestAwakeProviderReceipt(
            QuestAwakeContract.ReceiptSchema,
            QuestAwakeContract.Version,
            request.RequestId,
            request.OperationId,
            request.PreviewId,
            request.DeviceId,
            request.IdentityRevision,
            request.Action,
            request.WatchdogGeneration,
            request.DurationMilliseconds,
            request.WatchdogIntervalMilliseconds,
            stayOnEffective,
            proximityHoldEffective,
            wakeEffective,
            deviceWatchdogEffective,
            settingsRestored,
            effective,
            settingsLeftUnchanged,
            outcome,
            repairs,
            QuestAwakePowerReadback.From(power),
            watchdog,
            evidence,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private static string ComputeEvidenceDigest(
        QuestAwakeProviderRequest request,
        QuestControlStatus power,
        QuestDeviceAwakeWatchdogStatus watchdog,
        IReadOnlyList<CommandResult> commands)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.RequestId,
            request.OperationId,
            request.PreviewId,
            request.DeviceId,
            request.IdentityRevision,
            request.Action,
            request.DurationMilliseconds,
            request.WatchdogIntervalMilliseconds,
            request.WatchdogGeneration,
            power,
            watchdog,
            watchdogProcessAlive = watchdog.ProcessAlive,
            commands = commands.Select(static command => new
            {
                argumentsSha256 = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", command.Arguments))))
                    .ToLowerInvariant(),
                command.ExitCode,
                durationMilliseconds = (long)command.Duration.TotalMilliseconds,
                outputSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    command.StandardOutput + "\0" + command.StandardError))).ToLowerInvariant()
            })
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}

public sealed class QuestAwakeProviderSubprocessHost(
    Func<QuestAwakeProviderController> controllerFactory,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly Func<QuestAwakeProviderController> _controllerFactory =
        controllerFactory ?? throw new ArgumentNullException(nameof(controllerFactory));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public static QuestAwakeProviderSubprocessHost CreateWindows() =>
        new(() => new QuestAwakeProviderController(AdbClient.CreateDefault()));

    public async Task<int> RunAsync(
        string[] arguments,
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        if (ProviderCapabilityDiscoveryContract.HasExactDescribeArguments(
                arguments))
        {
            await ProviderCapabilityDiscoveryProjection.WriteAsync(
                    output,
                    ProviderCapabilityDiscoveryProjection.CreateAwake(
                        _timeProvider.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        if (arguments.Length != 3 ||
            arguments[0] != "integration" ||
            arguments[1] != "quest-awake" ||
            arguments[2] != "--json")
        {
            await WriteAsync(
                output,
                new QuestAwakeProviderResponse(
                    QuestAwakeContract.Version,
                    "rejected",
                    Error: "providerArgumentsInvalid",
                    Message: "Expected exactly: integration quest-awake --json"),
                cancellationToken).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var bytes = await ReadBoundedAsync(input, cancellationToken).ConfigureAwait(false);
            RejectDuplicateProperties(bytes);
            var request = JsonSerializer.Deserialize<QuestAwakeProviderRequest>(bytes, Json) ??
                throw new QuestAwakeProviderException("requestInvalid", "The provider request is required.");
            request.Validate(_timeProvider.GetUtcNow());
            var receipt = await _controllerFactory()
                .ExecuteAsync(request, cancellationToken)
                .ConfigureAwait(false);
            await WriteAsync(
                output,
                new QuestAwakeProviderResponse(
                    QuestAwakeContract.Version,
                    receipt.Effective ? "verified" : "pending",
                    receipt),
                cancellationToken).ConfigureAwait(false);
            return receipt.Effective ? 0 : 3;
        }
        catch (QuestAwakeProviderException exception)
        {
            await WriteAsync(
                output,
                new QuestAwakeProviderResponse(
                    QuestAwakeContract.Version,
                    "rejected",
                    Error: exception.Code,
                    Message: Bound(exception.Message)),
                cancellationToken).ConfigureAwait(false);
            return 2;
        }
        catch (JsonException)
        {
            await WriteAsync(
                output,
                new QuestAwakeProviderResponse(
                    QuestAwakeContract.Version,
                    "rejected",
                    Error: "requestInvalid",
                    Message: "The provider request must be strict valid JSON."),
                cancellationToken).ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException)
        {
            await WriteAsync(
                output,
                new QuestAwakeProviderResponse(
                    QuestAwakeContract.Version,
                    "cancelled",
                    Error: "cancelled",
                    Message: "The provider request was cancelled."),
                CancellationToken.None).ConfigureAwait(false);
            return 4;
        }
        catch (Exception)
        {
            await WriteAsync(
                output,
                new QuestAwakeProviderResponse(
                    QuestAwakeContract.Version,
                    "failed",
                    Error: "providerFailed",
                    Message: "The awake provider could not complete the typed request."),
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
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (memory.Length + count > QuestAwakeContract.MaximumRequestBytes)
                throw new QuestAwakeProviderException("requestOversized", "The provider request exceeds 16 KiB.");
            memory.Write(buffer, 0, count);
        }
        if (memory.Length < 2)
            throw new QuestAwakeProviderException("requestInvalid", "The provider request is required.");
        return memory.ToArray();
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
                throw new QuestAwakeProviderException("requestInvalid", "Duplicate JSON properties are not allowed.");
        }
    }

    private static async Task WriteAsync(
        Stream output,
        QuestAwakeProviderResponse response,
        CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(output, response, Json, cancellationToken)
            .ConfigureAwait(false);
        await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static string Bound(string value) =>
        value.Length <= 512 ? value : value[..512];
}
