using System.Collections.Concurrent;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserCommandOutcome(
    string RequestId, string Action, string State,
    OperatorExecutionResult? Result = null, string? Error = null,
    OperatorMutationReceipt? Mutation = null);

public sealed record BrowserCommandRequest(string RequestId, string Action, OperatorCommand Command, bool Mutation);

public sealed class BrowserCommands
{
    public const string ContractVersion = "questionable.file_manager.browser.v1";
    public const int MaximumRequestBytes = 4096;
    public const int MaximumRequests = 256;
    public static readonly string[] Actions =
        ["devices", "device.status", "wifi.enable", "wifi.connect", "device.keep-awake", "device.normal"];
    private readonly Func<OperatorCommand, CancellationToken, Task<OperatorExecutionResult>> _execute;
    private readonly ConcurrentDictionary<string, BrowserCommandOutcome> _requests = new(StringComparer.Ordinal);
    private readonly object _admission = new();
    private bool _running;
    public string SessionId { get; } = Guid.NewGuid().ToString("D");

    public BrowserCommands(OperatorCommandExecutor executor)
        : this((command, cancellation) => ExecuteOwnedAsync(executor, command, cancellation)) { }

    public static async Task<OperatorExecutionResult> ExecuteOwnedAsync(
        OperatorCommandExecutor executor, OperatorCommand command, CancellationToken cancellation)
    {
        if (command.Serial is not null && command.Kind != OperatorCommandKind.ConnectWifiAdb)
        {
            var targets = await executor.ExecuteAsync(OperatorCommands.DiscoverDevices(), cancellation).ConfigureAwait(false);
            if (targets.Devices?.Count(device => device.IsReady && device.Serial == command.Serial) != 1)
                throw new BrowserRequestException("target_not_ready");
        }
        return await executor.ExecuteAsync(command, cancellation).ConfigureAwait(false);
    }

    public BrowserCommands(Func<OperatorCommand, CancellationToken, Task<OperatorExecutionResult>> execute) =>
        _execute = execute;

    public void RequireSession(string session)
    {
        if (!string.Equals(SessionId, session, StringComparison.Ordinal))
            throw new BrowserRequestException("session_changed", 409);
    }

    public BrowserCommandOutcome Get(string requestId) =>
        _requests.TryGetValue(requestId, out var value) ? value :
            throw new BrowserRequestException("request_not_found", 404);

    public async Task<BrowserCommandOutcome> ExecuteAsync(ReadOnlyMemory<byte> body, CancellationToken stopping = default)
    {
        var request = Parse(body);
        lock (_admission)
        {
            if (_requests.ContainsKey(request.RequestId))
                throw new BrowserRequestException("request_already_consumed", 409);
            if (_running) throw new BrowserRequestException("busy", 409);
            if (_requests.Count >= MaximumRequests)
                throw new BrowserRequestException("session_capacity_reached", 409);
            _requests[request.RequestId] = new(request.RequestId, request.Action, "running");
            _running = true;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            BrowserCommandOutcome outcome;
            try
            {
                var result = await _execute(request.Command, timeout.Token).ConfigureAwait(false);
                // Core classifies connect as host transport, not headset mutation;
                // its exact ready endpoint result is the confirmation contract.
                var connected = request.Command.Kind == OperatorCommandKind.ConnectWifiAdb &&
                    result.WifiAdbConnectionResult is { Device.IsReady: true } connection &&
                    connection.Endpoint == $"{request.Command.WifiHost}:{request.Command.WifiPort}" &&
                    string.Equals(connection.Device.Serial, connection.Endpoint, StringComparison.OrdinalIgnoreCase);
                var state = request.Mutation
                    ? result.MutationReceipt?.Stage == OperatorMutationStage.Confirmed || connected ? "completed" : "pending"
                    : "completed";
                outcome = new(request.RequestId, request.Action, state, result);
            }
            catch (OperatorMutationExecutionException exception)
            {
                outcome = new(request.RequestId, request.Action, "outcomeUnknown",
                    Error: "mutation_readback_unknown", Mutation: exception.MutationReceipt);
            }
            catch (Exception)
            {
                // Never expose exception text, paths or arbitrary process output as an error.
                // Once a mutation is admitted any failure is conservatively uncertain.
                outcome = new(request.RequestId, request.Action,
                    request.Mutation ? "outcomeUnknown" : "failed", Error: "operation_failed");
            }
            _requests[request.RequestId] = outcome;
            return outcome;
        }
        finally { lock (_admission) { _running = false; } }
    }

    public static BrowserCommandRequest Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > MaximumRequestBytes) throw new BrowserRequestException("body_size_invalid", 413);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new BrowserRequestException("request_invalid");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) throw new BrowserRequestException("duplicate_field");
            var requestId = RequiredString(root, "requestId");
            if (!Guid.TryParseExact(requestId, "D", out var id) || id == Guid.Empty)
                throw new BrowserRequestException("request_id_invalid");
            var action = RequiredString(root, "action");
            var allowed = action switch
            {
                "devices" => new[] { "requestId", "action" },
                "device.status" => ["requestId", "action", "serial"],
                "wifi.enable" => ["requestId", "action", "serial", "port", "confirmed"],
                "wifi.connect" => ["requestId", "action", "serial", "host", "port", "confirmed"],
                "device.keep-awake" => ["requestId", "action", "serial", "durationMilliseconds", "confirmed"],
                "device.normal" => ["requestId", "action", "serial", "confirmed"],
                _ => throw new BrowserRequestException("action_unsupported")
            };
            if (!names.SetEquals(allowed)) throw new BrowserRequestException("fields_invalid");
            var mutation = action is not ("devices" or "device.status");
            if (mutation && root.GetProperty("confirmed").ValueKind != JsonValueKind.True)
                throw new BrowserRequestException("confirmation_required");
            var serial = action == "devices" ? null : RequiredString(root, "serial");
            OperatorCommand command = action switch
            {
                "devices" => OperatorCommands.DiscoverDevices(),
                "device.status" => OperatorCommands.ReadQuestControls(serial!),
                "wifi.enable" => OperatorCommands.EnableWifiAdb(serial!, Integer(root, "port", 1, 65535), true),
                "wifi.connect" => Connect(root, serial!),
                "device.keep-awake" => OperatorCommands.SetQuestKeepAwake(serial!, true,
                    Integer(root, "durationMilliseconds", 60000, 28800000), true),
                "device.normal" => OperatorCommands.SetQuestKeepAwake(serial!, false, 60000, true),
                _ => throw new BrowserRequestException("action_unsupported")
            };
            return new(id.ToString("D"), action, command, mutation);
        }
        catch (BrowserRequestException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { throw new BrowserRequestException("request_invalid"); }
    }

    private static OperatorCommand Connect(JsonElement root, string serial)
    {
        var host = RequiredString(root, "host");
        var port = Integer(root, "port", 1, 65535);
        var command = OperatorCommands.ConnectWifiAdb(host, port, true);
        // This confirms one endpoint, not canonical headset or boot identity.
        if (!string.Equals(serial, $"{command.WifiHost}:{port}", StringComparison.Ordinal))
            throw new BrowserRequestException("endpoint_target_mismatch");
        return command;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 256 } text)
            throw new BrowserRequestException("field_invalid");
        return text;
    }

    private static int Integer(JsonElement root, string name, int minimum, int maximum)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw new BrowserRequestException("number_invalid");
        return number;
    }
}
