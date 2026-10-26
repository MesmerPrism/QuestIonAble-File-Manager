using System.Security.Cryptography;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserProfileRequest(string RequestId, string Action, string? DeviceId = null,
    bool ReplaceExisting = false, bool Confirmed = false)
{
    public bool Mutation => Action is "profile.import" or "profile.revoke";
    public OperatorCommand ToOperatorCommand() => Action switch
    {
        "profile.list" => OperatorCommands.ListQuestConnectivityProfiles(),
        "profile.status" => OperatorCommands.QuestConnectivityProfileStatus(DeviceId!),
        "profile.revoke" => OperatorCommands.RevokeQuestConnectivityProfile(DeviceId!, Confirmed),
        "profile.import" => OperatorCommands.ImportQuestConnectivityProfileStdin(ReplaceExisting, Confirmed),
        _ => throw new BrowserRequestException("action_unsupported")
    };
}

public static class BrowserProfileCommands
{
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(new[] { "profile.list", "profile.status", "profile.revoke", "profile.import" });
    public static bool IsProfileAction(ReadOnlyMemory<byte> body)
    {
        if (body.Length is <= 0 or > BrowserCommands.MaximumRequestBytes) return false;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString()?.StartsWith("profile.", StringComparison.Ordinal) == true;
        }
        catch (JsonException) { return false; }
    }
    public static BrowserProfileRequest Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length is <= 0 or > BrowserCommands.MaximumRequestBytes) throw new BrowserRequestException("body_size_invalid", 413);
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new BrowserRequestException("request_invalid");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject()) if (!names.Add(field.Name)) throw new BrowserRequestException("duplicate_field");
            var action = Text(root, "action");
            if (action == "profile.import") throw new BrowserRequestException("sensitive_endpoint_required");
            if (action is not ("profile.list" or "profile.status" or "profile.revoke")) throw new BrowserRequestException("action_unsupported");
            var expected = new HashSet<string>(["requestId", "action"], StringComparer.Ordinal);
            if (action != "profile.list") expected.Add("deviceId");
            if (action == "profile.revoke") expected.Add("confirmed");
            if (!names.SetEquals(expected)) throw new BrowserRequestException("fields_invalid");
            var confirmed = action == "profile.revoke";
            if (confirmed && root.GetProperty("confirmed").ValueKind != JsonValueKind.True) throw new BrowserRequestException("confirmation_required");
            var request = new BrowserProfileRequest(Id(Text(root, "requestId")), action, action == "profile.list" ? null : DeviceId(Text(root, "deviceId")), Confirmed: confirmed);
            Validate(request); return request;
        }
        catch (BrowserRequestException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new BrowserRequestException("request_invalid"); }
    }
    internal static void Validate(BrowserProfileRequest request)
    {
        if (Id(request.RequestId) != request.RequestId) throw new BrowserRequestException("request_id_invalid");
        if (!Actions.Contains(request.Action, StringComparer.Ordinal)) throw new BrowserRequestException("action_unsupported");
        if (request.Action == "profile.list" ? request.DeviceId is not null : DeviceId(request.DeviceId!) != request.DeviceId) throw new BrowserRequestException("profileDeviceIdInvalid");
        if (request.Mutation && !request.Confirmed) throw new BrowserRequestException("confirmation_required");
        if (request.Action != "profile.import" && request.ReplaceExisting) throw new BrowserRequestException("fields_invalid");
    }
    internal static string DeviceId(string value) => QuestConnectivityProfileManagementContract.IsDeviceId(value) ? value : throw new BrowserRequestException("profileDeviceIdInvalid");
    internal static string Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString("D") : throw new BrowserRequestException("request_id_invalid");
    private static string Text(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.String && root.GetProperty(name).GetString() is { Length: > 0 and <= 256 } value ? value : throw new BrowserRequestException("field_invalid");
}

public sealed partial class BrowserCommands
{
    private TaskCompletionSource? _profileCompletion;
    private bool _profilesClosing;
    public Task DrainProfilesAsync()
    {
        lock (_admission) { _profilesClosing = true; return _profileCompletion?.Task ?? Task.CompletedTask; }
    }
    internal void CheckProfileAdmission(BrowserProfileRequest request)
    {
        BrowserProfileCommands.Validate(request);
        lock (_admission)
        {
            if (_requests.ContainsKey(request.RequestId)) throw new BrowserRequestException("request_already_consumed", 409);
            if (_profilesClosing) throw new BrowserRequestException("busy", 409);
            if (_profiles is null) throw new BrowserRequestException("windows_credential_manager_required", 503);
            _profiles.EnsureSupported();
            if (_running || _operationGate.IsBusy) throw new BrowserRequestException("busy", 409);
            if (_requests.Count >= MaximumRequests) throw new BrowserRequestException("session_capacity_reached", 409);
        }
    }
    // Owns this received array until actual completion. It is never retained in
    // a command, ledger entry, background queue or public result.
    internal async Task<BrowserCommandOutcome> ExecuteProfileImportAsync(BrowserProfileRequest request, byte[] privateDocument)
    {
        try
        {
            if (request.Action != "profile.import") throw new BrowserRequestException("action_unsupported");
            CheckProfileAdmission(request);
            if (QuestConnectivityProfileEnrollmentDocument.GetValidatedDeviceId(privateDocument) != request.DeviceId)
                throw new BrowserRequestException("profile_device_id_mismatch");
            return await ExecuteProfileAsync(request, privateDocument, CancellationToken.None).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(privateDocument); }
    }
    private async Task<BrowserCommandOutcome> ExecuteProfileAsync(BrowserProfileRequest request, byte[]? privateDocument, CancellationToken stopping)
    {
        CheckProfileAdmission(request);
        IDisposable gate; TaskCompletionSource completion;
        lock (_admission)
        {
            CheckProfileAdmission(request);
            gate = _operationGate.TryAcquire() ?? throw new BrowserRequestException("busy", 409);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _profileCompletion = completion;
            _requests[request.RequestId] = new(request.RequestId, request.Action, "running", Profile: BrowserProfileProjection.Empty(request, request.Mutation ? "unknown" : "readOnly"));
            _running = true;
        }
        try
        {
            using var input = privateDocument is null ? null : new MemoryStream(privateDocument, writable: false);
            // Native credential calls are synchronous. A page/lifetime timeout
            // cannot detach their verification/rollback or free their gate.
            var outcome = await _profiles!.ExecuteAsync(request, input, request.Mutation ? CancellationToken.None : stopping).ConfigureAwait(false);
            _requests[request.RequestId] = outcome; return outcome;
        }
        finally
        {
            if (privateDocument is not null) CryptographicOperations.ZeroMemory(privateDocument);
            lock (_admission) { _running = false; gate.Dispose(); _profileCompletion = null; completion.TrySetResult(); }
        }
    }
}
