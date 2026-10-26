using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserFleetRequest(string RequestId, string Action)
{
    public OperatorCommand ToOperatorCommand() => Action == "fleet.status"
        ? OperatorCommands.FleetInstallStatus() : throw new BrowserRequestException("action_unsupported");
}

public static class BrowserFleetCommands
{
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(new[] { "fleet.status" });
    public static bool IsFleetAction(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) return false;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString()?.StartsWith("fleet.", StringComparison.Ordinal) == true;
        }
        catch (JsonException) { return false; }
    }
    public static BrowserFleetRequest Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) throw new BrowserRequestException("body_size_invalid", 413);
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new BrowserRequestException("request_invalid");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) if (!names.Add(property.Name)) throw new BrowserRequestException("duplicate_field");
            if (root.GetProperty("action").GetString() != "fleet.status") throw new BrowserRequestException("action_unsupported");
            if (!names.SetEquals(["requestId", "action"])) throw new BrowserRequestException("fields_invalid");
            var id = root.GetProperty("requestId").GetString();
            if (!Guid.TryParseExact(id, "D", out var guid) || guid == Guid.Empty || guid.ToString("D") != id)
                throw new BrowserRequestException("request_id_invalid");
            return new(id!, "fleet.status");
        }
        catch (BrowserRequestException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new BrowserRequestException("request_invalid"); }
    }
}

public sealed partial class BrowserCommands
{
    private async Task<BrowserCommandOutcome> ExecuteFleetStatusAsync(BrowserFleetRequest request, CancellationToken stopping)
    {
        IDisposable lease;
        lock (_admission)
        {
            if (_requests.ContainsKey(request.RequestId)) throw new BrowserRequestException("request_already_consumed", 409);
            if (_fleetStatus is null) throw new BrowserRequestException("fleet_status_unavailable", 503);
            _fleetStatus.EnsureSupported();
            if (_running || _operationGate.IsBusy) throw new BrowserRequestException("busy", 409);
            if (_requests.Count >= MaximumRequests) throw new BrowserRequestException("session_capacity_reached", 409);
            lease = _operationGate.TryAcquire() ?? throw new BrowserRequestException("busy", 409);
            _requests[request.RequestId] = new(request.RequestId, request.Action, "running");
            _running = true;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var result = await _fleetStatus.ExecuteAsync(request, timeout.Token).ConfigureAwait(false);
            if (result.Error == "fleet_status_cancelled" && timeout.IsCancellationRequested && !stopping.IsCancellationRequested)
                result = result with { Error = "fleet_status_timeout" };
            _requests[request.RequestId] = result;
            return result;
        }
        finally { lock (_admission) { _running = false; lease.Dispose(); } }
    }
}
