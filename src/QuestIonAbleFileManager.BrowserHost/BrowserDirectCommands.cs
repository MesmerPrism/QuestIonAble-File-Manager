using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectRequest(string RequestId, string Action, string? Serial = null,
    string? ProductChannel = null, string? ConnectionId = null)
{
    public string UsbOperationId => "usb_" + Guid.Parse(RequestId).ToString("N");
    public bool Mutation => Action != "kiosk.direct.status";
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
            gate=_operationGate.TryAcquire() ?? throw new BrowserRequestException("busy",409);
            try{operation=_directConnections.Reserve(request,SessionId);}
            catch{gate.Dispose();throw;}
            _requests[request.RequestId]=new(request.RequestId,request.Action,"running");_running=true;
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
    public static readonly IReadOnlyList<string> Actions = ["kiosk.direct.connect-usb", "kiosk.direct.status", "kiosk.direct.close"];
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
            if (action != "kiosk.direct.status") expected.Add("confirmed");
            if (!names.SetEquals(expected)) throw new BrowserRequestException("fields_invalid");
            var id = Id(Text(root, "requestId"));
            if (action != "kiosk.direct.status" && root.GetProperty("confirmed").ValueKind != JsonValueKind.True)
                throw new BrowserRequestException("confirmation_required");
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
}
