using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserKioskRequest(string RequestId, string Action, string Serial, string ProductChannel,
    string? CatalogRequestId = null, string? Key = null, string? OriginalProviderRequestId = null, string? OriginalExpectedCommand = null)
{
    public bool Mutation => Action is "kiosk.catalog.open" or "kiosk.select";
    public string Operation => Action["kiosk.".Length..];
    public string? ProviderRequestId => Action == "kiosk.installation" ? null : Action == "kiosk.original-result"
        ? OriginalProviderRequestId : "pc-" + Guid.Parse(RequestId).ToString("N");
    public RustyKioskCommand? ExpectedCommand => Action == "kiosk.installation" ? null : Action == "kiosk.select" || OriginalExpectedCommand == "select"
        ? RustyKioskCommand.Select : RustyKioskCommand.Status;
    public OperatorCommand ToOperatorCommand()
    {
        var product = RustyKioskProductContract.Parse(ProductChannel);
        return Action switch
        {
            "kiosk.installation" => OperatorCommands.InspectRustyKioskInstallation(Serial, product),
            "kiosk.catalog.open" => OperatorCommands.OpenRustyKioskCatalog(Serial, ProviderRequestId!, true, product),
            "kiosk.select" => OperatorCommands.SelectRustyKioskApp(Serial, Key!, ProviderRequestId!, true, product),
            "kiosk.original-result" => OperatorCommands.ReadRustyKioskRequest(Serial, ProviderRequestId!, ExpectedCommand!.Value, Key, product),
            _ => throw new BrowserRequestException("action_unsupported")
        };
    }
}

public static class BrowserKioskCommands
{
    public static readonly IReadOnlyList<string> Actions = ["kiosk.installation", "kiosk.catalog.open", "kiosk.select", "kiosk.original-result"];
    public static bool IsKioskAction(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) return false;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString()?.StartsWith("kiosk.", StringComparison.Ordinal) == true;
        }
        catch (JsonException) { return false; }
    }
    public static BrowserKioskRequest Parse(ReadOnlyMemory<byte> body)
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
            var expected = new HashSet<string>(["requestId", "action", "serial", "productChannel"], StringComparer.Ordinal);
            if (action is "kiosk.catalog.open" or "kiosk.select") expected.Add("confirmed");
            if (action == "kiosk.select") { expected.Add("catalogRequestId"); expected.Add("key"); }
            if (action == "kiosk.original-result")
            {
                expected.Add("providerRequestId"); expected.Add("expectedCommand");
                if (Text(root, "expectedCommand") == "select") expected.Add("key");
            }
            if (!names.SetEquals(expected)) throw new BrowserRequestException("fields_invalid");
            var id = BrowserId(Text(root, "requestId"));
            var serial = AndroidInput.RequireSerial(Text(root, "serial"));
            var channel = Text(root, "productChannel");
            if (channel is not ("stable" or "labs")) throw new BrowserRequestException("product_channel_invalid");
            if (action is "kiosk.catalog.open" or "kiosk.select" && root.GetProperty("confirmed").ValueKind != JsonValueKind.True)
                throw new BrowserRequestException("confirmation_required");
            string? catalog = null, key = null, provider = null, command = null;
            if (action == "kiosk.select") catalog = BrowserId(Text(root, "catalogRequestId"));
            if (action == "kiosk.original-result")
            {
                provider = Text(root, "providerRequestId");
                if (!IsProviderId(provider)) throw new BrowserRequestException("provider_request_id_invalid");
                command = Text(root, "expectedCommand");
                if (command is not ("status" or "select")) throw new BrowserRequestException("kiosk_command_invalid");
            }
            if (action == "kiosk.select" || command == "select")
            {
                key = Text(root, "key");
                if (!IsSelectableKey(key)) throw new BrowserRequestException("kiosk_key_invalid");
            }
            return new(id, action, serial, channel, catalog, key, provider, command);
        }
        catch (BrowserRequestException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new BrowserRequestException("request_invalid"); }
    }
    internal static bool IsProviderId(string? value) => value is { Length: 35 } && value.StartsWith("pc-", StringComparison.Ordinal) &&
        Guid.TryParseExact(value[3..], "N", out var id) && id != Guid.Empty && value[3..] == id.ToString("N");
    internal static bool IsSelectableKey(string? value) => value is { Length: > 0 and <= 160 } &&
        !string.IsNullOrWhiteSpace(value) && value.Trim() == value && !value.Any(char.IsControl);
    private static string BrowserId(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        ? id.ToString("D") : throw new BrowserRequestException("request_id_invalid");
    private static string Text(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.String &&
        root.GetProperty(name).GetString() is { Length: > 0 and <= 256 } text && !string.IsNullOrWhiteSpace(text)
        ? text : throw new BrowserRequestException("field_invalid");
}

public sealed partial class BrowserCommands
{
    private async Task<BrowserCommandOutcome> ExecuteKioskAsync(BrowserKioskRequest request, CancellationToken stopping)
    {
        IDisposable gate;
        BrowserKioskReview? review = null;
        var command = request.ToOperatorCommand();
        lock (_admission)
        {
            if (_requests.ContainsKey(request.RequestId)) throw new BrowserRequestException("request_already_consumed", 409);
            if (_running || _operationGate.IsBusy) throw new BrowserRequestException("busy", 409);
            if (_requests.Count >= MaximumRequests) throw new BrowserRequestException("session_capacity_reached", 409);
            if (request.Action == "kiosk.select") review = ReviewKioskSelection(request);
            gate = _operationGate.TryAcquire() ?? throw new BrowserRequestException("busy", 409);
            _requests[request.RequestId] = new(request.RequestId, request.Action, "running"); _running = true;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var projection = new BrowserKioskProjection(request, command, review);
            BrowserCommandOutcome outcome;
            try { outcome = projection.Project(await _execute(command, timeout.Token).ConfigureAwait(false)); }
            catch (OperatorMutationExecutionException exception) { outcome = projection.Failure("kiosk_readback_unknown", exception.MutationReceipt); }
            catch (Exception) { outcome = projection.Failure("kiosk_operation_failed"); }
            _requests[request.RequestId] = outcome;
            return outcome;
        }
        finally { lock (_admission) { _running = false; gate.Dispose(); } }
    }

    private BrowserKioskReview ReviewKioskSelection(BrowserKioskRequest request)
    {
        if (!_requests.TryGetValue(request.CatalogRequestId!, out var cached) || cached.Action != "kiosk.catalog.open" || cached.State != "completed" ||
            cached.Kiosk is not { Operation: "catalog.open", ProviderState: "completed", ActivityOpenRequested: true, Result: { Accepted: true, Completed: true } } kiosk ||
            kiosk.Serial != request.Serial || kiosk.ProductChannel != request.ProductChannel || kiosk.CatalogRequestId != request.CatalogRequestId ||
            kiosk.ProviderRequestId != "pc-" + Guid.Parse(request.CatalogRequestId!).ToString("N"))
            throw new BrowserRequestException("kiosk_catalog_unavailable", 409);
        // A proven no-selection outcome retires only the reviewed catalog's
        // admission authority. Keep both immutable historical outcomes intact.
        if (_requests.Values.Any(outcome => outcome.Action == "kiosk.select" && outcome.State == "completed" &&
                outcome.Kiosk is { ProviderState: "installationOnly", ActivityOpenRequested: false, Result: null,
                    Installation.HostOperatorAvailable: false } noOp && noOp.CatalogRequestId == request.CatalogRequestId))
            throw new BrowserRequestException("kiosk_catalog_unavailable", 409);
        var entries = kiosk.Result.Entries.Where(row => row.Key == request.Key).ToArray();
        if (entries.Length != 1 || !entries[0].Selectable || !entries[0].Installed || !entries[0].Launchable || entries[0].PackageName is null)
            throw new BrowserRequestException("kiosk_selection_not_reviewed", 409);
        return new(entries[0].Key, entries[0].PackageName!);
    }
}

internal sealed record BrowserKioskReview(string Key, string PackageName);
