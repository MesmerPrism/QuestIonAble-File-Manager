using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectRequest(string RequestId, string Action, string? Serial = null,
    string? ProductChannel = null, string? ConnectionId = null, RustyKioskCommand? Command = null,
    string? Value = null, string? OriginalRequestId = null)
{
    public string UsbOperationId => "usb_" + Guid.Parse(RequestId).ToString("N");
    public string LogicalRequestId => "kiosk_" + Guid.Parse(RequestId).ToString("N");
    public bool Mutation => Action is "kiosk.direct.connect-usb" or "kiosk.direct.close" or "kiosk.direct.invoke" or "kiosk.direct.request-cancel";
    public bool IsCommand => Action is "kiosk.direct.invoke" or "kiosk.direct.request-status" or "kiosk.direct.request-result" or "kiosk.direct.request-cancel";
    public bool Recovery => Action is "kiosk.direct.request-status" or "kiosk.direct.request-result" or "kiosk.direct.request-cancel";
}

public sealed partial class BrowserCommands
{
    private async Task<BrowserCommandOutcome> ExecuteDirectAsync(BrowserDirectRequest request,CancellationToken stopping)
    {
        IDisposable gate; BrowserDirectConnections.Operation operation;
        lock(_admission)
        {
            if(_requests.ContainsKey(request.RequestId))throw new BrowserRequestException("request_already_consumed",409);
            if(_running || _operationGate.IsBusy)throw new BrowserRequestException("busy",409);
            if(_requests.Count>=MaximumRequests)throw new BrowserRequestException("session_capacity_reached",409);
            if(_directConnections is null)throw new BrowserRequestException("direct_unavailable",409);
            BrowserDirectCommandOutcome? original=null;
            if(request.Recovery)
            {
                if(!_requests.TryGetValue(request.OriginalRequestId!,out var prior) || prior.Action!="kiosk.direct.invoke" || prior.KioskDirectCommand is not { } source)
                    throw new BrowserRequestException("direct_original_unavailable",404);
                if(source.Operation!="invoke" || source.OriginalRequestId!=request.OriginalRequestId || source.ConnectionId!=request.ConnectionId)
                    throw new BrowserRequestException("direct_original_mismatch",409);
                original=source;
            }
            gate=_operationGate.TryAcquire() ?? throw new BrowserRequestException("busy",409);
            try{operation=_directConnections.Reserve(request,SessionId,original);}
            catch{gate.Dispose();throw;}
            _requests[request.RequestId]=new(request.RequestId,request.Action,"running",KioskDirectCommand:operation.CommandMetadata);_running=true;
        }
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var outcome=await _directConnections.ExecuteAsync(operation,timeout.Token).ConfigureAwait(false);
            _requests[request.RequestId]=outcome;return outcome;
        }
        finally
        {
            lock(_admission){_running=false;gate.Dispose();}
            _directConnections.Complete(operation);
        }
    }
}

public static class BrowserDirectCommands
{
    public static readonly IReadOnlyList<string> Actions = ["kiosk.direct.connect-usb", "kiosk.direct.status", "kiosk.direct.close",
        "kiosk.direct.invoke", "kiosk.direct.request-status", "kiosk.direct.request-result", "kiosk.direct.request-cancel"];
    public static bool IsDirectAction(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) return false;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString()?.StartsWith("kiosk.direct.", StringComparison.Ordinal) == true;
        }
        catch (JsonException) { return false; }
    }
    public static BrowserDirectRequest Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) throw new BrowserRequestException("body_size_invalid", 413);
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new BrowserRequestException("request_invalid");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject()) if (!names.Add(field.Name)) throw new BrowserRequestException("duplicate_field");
            var action = Text(root, "action");
            if (!Actions.Contains(action, StringComparer.Ordinal)) throw new BrowserRequestException("action_unsupported");
            var expected = new HashSet<string>(["requestId", "action"], StringComparer.Ordinal);
            if (action == "kiosk.direct.connect-usb") { expected.Add("serial"); expected.Add("productChannel"); }
            else expected.Add("connectionId");
            if (action == "kiosk.direct.invoke") { expected.Add("command"); expected.Add("value"); }
            if (action is "kiosk.direct.request-status" or "kiosk.direct.request-result" or "kiosk.direct.request-cancel") expected.Add("originalRequestId");
            var mutation = action is "kiosk.direct.connect-usb" or "kiosk.direct.close" or "kiosk.direct.invoke" or "kiosk.direct.request-cancel";
            if (mutation) expected.Add("confirmed");
            if (!names.SetEquals(expected)) throw new BrowserRequestException("fields_invalid");
            var id = Id(Text(root, "requestId"));
            if (mutation && root.GetProperty("confirmed").ValueKind != JsonValueKind.True)
                throw new BrowserRequestException("confirmation_required");
            if (action is "kiosk.direct.invoke" or "kiosk.direct.request-status" or "kiosk.direct.request-result" or "kiosk.direct.request-cancel")
            {
                var connection = CanonicalId(Text(root, "connectionId"));
                _ = CanonicalId(Text(root, "requestId"));
                if (action != "kiosk.direct.invoke") return new(id, action, ConnectionId: connection, OriginalRequestId: CanonicalId(Text(root, "originalRequestId")));
                var command = CommandName(Text(root, "command"));
                var item = root.GetProperty("value");
                var value = item.ValueKind == JsonValueKind.Null ? null : item.ValueKind == JsonValueKind.String ? item.GetString() : throw new BrowserRequestException("direct_value_invalid");
                ValidateValue(command, value);
                return new(id, action, ConnectionId: connection, Command: command, Value: value);
            }
            if (action != "kiosk.direct.connect-usb") return new(id, action, ConnectionId: Id(Text(root, "connectionId")));
            var serial = AndroidInput.RequireSerial(Text(root, "serial"));
            var channel = Text(root, "productChannel");
            if (channel is not ("stable" or "labs")) throw new BrowserRequestException("product_channel_invalid");
            return new(id, action, serial, channel);
        }
        catch (BrowserRequestException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new BrowserRequestException("request_invalid"); }
    }
    private static string Text(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.String &&
        root.GetProperty(name).GetString() is { Length: > 0 and <= 256 } value && !string.IsNullOrWhiteSpace(value)
        ? value : throw new BrowserRequestException("field_invalid");
    private static string Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        ? id.ToString("D") : throw new BrowserRequestException("request_id_invalid");
    private static string CanonicalId(string value) => Id(value) == value ? value : throw new BrowserRequestException("request_id_invalid");
    internal static RustyKioskCommand CommandName(string value) => value switch
    {
        "status" => RustyKioskCommand.Status,
        "show-controls" => RustyKioskCommand.ShowControls,
        "show-apps" => RustyKioskCommand.ShowApps,
        "reload" => RustyKioskCommand.Reload,
        "check-setup-helper" => RustyKioskCommand.CheckSetupHelper,
        "set-launch-requirement" => RustyKioskCommand.SetLaunchRequirement,
        "cancel-pending-launch" => RustyKioskCommand.CancelPendingLaunch,
        "launch-normal" => RustyKioskCommand.LaunchNormal,
        "launch-kiosk" => RustyKioskCommand.LaunchKiosk,
        _ => throw new BrowserRequestException("direct_command_unsupported")
    };
    internal static void ValidateValue(RustyKioskCommand command, string? value)
    {
        if (command == RustyKioskCommand.SetLaunchRequirement ? value is not ("any" or "wifi-on" or "wifi-off") : value is not null)
            throw new BrowserRequestException("direct_value_invalid");
        if (KioskDirectOperatorCommand.Invoke(command, value, true).Value != value) throw new BrowserRequestException("direct_value_invalid");
    }
}
