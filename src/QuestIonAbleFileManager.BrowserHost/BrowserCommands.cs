using System.Collections.Concurrent;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserCommandOutcome(
    string RequestId, string Action, string State,
    OperatorExecutionResult? Result = null, string? Error = null,
    OperatorMutationReceipt? Mutation = null,
    BrowserArtifactInspectionOutcome? ArtifactInspection = null,
    BrowserArtifactTransferOutcome? ArtifactTransfer = null,
    BrowserArtifactReadOutcome? ArtifactRead = null,
    BrowserArtifactPushOutcome? ArtifactPush = null,
    BrowserKioskOutcome? Kiosk = null,
    BrowserDirectConnectionOutcome? KioskDirectConnection = null,
    BrowserProfileOutcome? Profile = null);

public sealed record BrowserArtifactInspectionOutcome(BrowserArtifactReceipt Artifact, ApkArtifactInspection Inspection);
public sealed record BrowserArtifactTransferOutcome(BrowserArtifactReceipt Artifact, string Serial,
    string? SourcePath = null, string? PackageName = null, string? ChecksumText = null);

public sealed record BrowserCommandRequest(string RequestId, string Action, OperatorCommand Command, bool Mutation);

public sealed partial class BrowserCommands
{
    public const string ContractVersion = "questionable.file_manager.browser.v1";
    public const int MaximumRequestBytes = 4096;
    public const int MaximumRequests = 256;
    public static readonly string[] Actions =
        ["devices", "device.status", "wifi.enable", "wifi.connect", "wifi.disconnect", "device.keep-awake", "device.normal",
         "files.list", "apk.list", "apk.permissions", "device.performance", "device.performance-clear",
         "device.actor-observe", "device.actor-identity", "adb.forwards",
         "kiosk.installation", "kiosk.catalog.open", "kiosk.select", "kiosk.original-result"];
    private readonly Func<OperatorCommand, CancellationToken, Task<OperatorExecutionResult>> _execute;
    private readonly Func<BrowserArtifactStore>? _artifactStore;
    private readonly BrowserOperationGate _operationGate;
    private readonly Func<BrowserTransferRequest, CancellationToken, Task<BrowserArtifactTransferOutcome>>? _capture;
    private readonly BrowserDirectConnections? _directConnections;
    private readonly BrowserProfiles? _profiles;
    public IReadOnlyList<string> AvailableActions => [.. ArtifactActions, .. _directConnections is null ? [] : BrowserDirectCommands.Actions,
        .. _profiles?.Capability.Supported == true ? BrowserProfileCommands.Actions : []];
    private IReadOnlyList<string> ArtifactActions => _artifactStore is not null && BrowserArtifactStore.Capability.Supported
        ? _capture is not null ? [.. Actions, "apk.inspect", "apk.preflight", "runtime.observe", "files.push", "files.pull", "apk.export"] : [.. Actions, "apk.inspect", "apk.preflight", "runtime.observe", "files.push"] : Actions;
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

    public BrowserCommands(Func<OperatorCommand, CancellationToken, Task<OperatorExecutionResult>> execute,
        Func<BrowserArtifactStore>? artifactStore = null, BrowserOperationGate? operationGate = null,
        Func<BrowserTransferRequest, CancellationToken, Task<BrowserArtifactTransferOutcome>>? capture = null,
        BrowserDirectConnections? directConnections = null, BrowserProfiles? profiles = null)
    {
        _execute = execute;
        _artifactStore = artifactStore;
        _operationGate = operationGate ?? new BrowserOperationGate();
        _capture = capture;
        _directConnections = directConnections;
        _directConnections?.BindGate(_operationGate);
        _profiles = profiles;
    }

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
        if (BrowserProfileCommands.IsProfileAction(body))
            return await ExecuteProfileAsync(BrowserProfileCommands.Parse(body), null, stopping).ConfigureAwait(false);
        if (BrowserDirectCommands.IsDirectAction(body))
            return await ExecuteDirectAsync(BrowserDirectCommands.Parse(body), stopping).ConfigureAwait(false);
        if (BrowserKioskCommands.IsKioskAction(body))
            return await ExecuteKioskAsync(BrowserKioskCommands.Parse(body), stopping).ConfigureAwait(false);
        if (BrowserTransferCommands.IsTransferAction(body))
            return await ExecuteTransferAsync(BrowserTransferCommands.Parse(body), stopping).ConfigureAwait(false);
        var artifact = BrowserArtifactCommands.IsArtifactAction(body) ? BrowserArtifactCommands.Parse(body) : null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(artifact is null ? 90 : BrowserArtifactStore.TransferTimeoutSeconds));
        if (artifact is not null)
        {
            // A consumed ID remains consumed even after its artifact was removed.
            lock (_admission)
            {
                if (_requests.ContainsKey(artifact.RequestId)) throw new BrowserRequestException("request_already_consumed", 409);
                if (_running || _operationGate.IsBusy) throw new BrowserRequestException("busy", 409);
                if (_requests.Count >= MaximumRequests) throw new BrowserRequestException("session_capacity_reached", 409);
            }
        }
        await using var artifactLease = await AcquireArtifactAsync(artifact, timeout.Token).ConfigureAwait(false);
        var request = artifact is null ? Parse(body) : new BrowserCommandRequest(
            artifact.RequestId, artifact.Action, artifact.ToOperatorCommand(artifactLease!), artifact.Action == "files.push");
        IDisposable operationLease;
        lock (_admission)
        {
            if (_requests.ContainsKey(request.RequestId))
                throw new BrowserRequestException("request_already_consumed", 409);
            if (_running) throw new BrowserRequestException("busy", 409);
            if (_requests.Count >= MaximumRequests)
                throw new BrowserRequestException("session_capacity_reached", 409);
            operationLease = _operationGate.TryAcquire() ?? throw new BrowserRequestException("busy", 409);
            _requests[request.RequestId] = new(request.RequestId, request.Action, "running");
            _running = true;
        }
        try
        {
            var push = artifact?.Action == "files.push" ? new BrowserArtifactPushProjection(artifact, artifactLease!) : null;
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
                if (push is not null) outcome = push.Project(result);
                else if (artifactLease is not null)
                {
                    var projection = new BrowserArtifactReadProjection(artifactLease);
                    if (artifact!.Action != "apk.inspect")
                    {
                        var read = projection.Project(artifact, result);
                        outcome = read is null ? new(request.RequestId, request.Action, "failed", Error: "artifact_read_binding_mismatch") :
                            new(request.RequestId, request.Action, state, ArtifactRead: read);
                    }
                    else
                    {
                        var inspection = result.ApkArtifactInspection;
                        if (inspection is null || inspection.Sha256 != artifactLease.Receipt.Sha256 ||
                            inspection.SizeBytes != artifactLease.Receipt.SizeBytes)
                            outcome = new(request.RequestId, request.Action, "failed", Error: "artifact_inspection_binding_mismatch");
                        else
                            // Core commands and inspector source paths stay inside the connector.
                            outcome = new(request.RequestId, request.Action, state, ArtifactInspection: new(
                                artifactLease.Receipt, projection.Inspection(inspection)));
                    }
                }
                else outcome = new(request.RequestId, request.Action, state, result);
            }
            catch (OperatorMutationExecutionException exception)
            {
                outcome = push is not null ? push.Unknown("mutation_readback_unknown", exception.MutationReceipt) :
                    new(request.RequestId, request.Action, "outcomeUnknown", Error: "mutation_readback_unknown", Mutation: exception.MutationReceipt);
            }
            catch (Exception)
            {
                // Never expose exception text, paths or arbitrary process output as an error.
                // Once a mutation is admitted any failure is conservatively uncertain.
                outcome = push is not null ? push.Unknown("operation_failed") : new(request.RequestId, request.Action,
                    request.Mutation ? "outcomeUnknown" : "failed", Error: "operation_failed");
            }
            _requests[request.RequestId] = outcome;
            return outcome;
        }
        finally { lock (_admission) { _running = false; operationLease.Dispose(); } }
    }

    private async Task<BrowserCommandOutcome> ExecuteTransferAsync(BrowserTransferRequest request, CancellationToken stopping)
    {
        IDisposable operationLease;
        lock (_admission)
        {
            if (_requests.ContainsKey(request.RequestId)) throw new BrowserRequestException("request_already_consumed", 409);
            if (_capture is null) throw new BrowserRequestException("artifact_staging_unavailable", 503);
            if (_running) throw new BrowserRequestException("busy", 409);
            if (_requests.Count >= MaximumRequests) throw new BrowserRequestException("session_capacity_reached", 409);
            operationLease = _operationGate.TryAcquire() ?? throw new BrowserRequestException("busy", 409);
            _requests[request.RequestId] = new(request.RequestId, request.Action, "running"); _running = true;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(BrowserArtifactStore.TransferTimeoutSeconds));
            BrowserCommandOutcome outcome;
            try
            {
                var capture = await _capture(request, timeout.Token).ConfigureAwait(false);
                BrowserArtifactCommands.RequireArtifactId(capture.Artifact.Id);
                BrowserArtifactCommands.RequireHash(capture.Artifact.Sha256);
                if (capture.Serial != request.Serial || capture.Artifact.FileName != request.FileName ||
                    capture.Artifact.SizeBytes is < 0 or > BrowserArtifactCommands.MaximumArtifactBytes ||
                    (request.Action == "files.pull" && (capture.SourcePath != request.RemotePath || capture.PackageName is not null || capture.ChecksumText is not null)) ||
                    (request.Action == "apk.export" && (capture.PackageName != request.PackageName ||
                        capture.ChecksumText != $"{capture.Artifact.Sha256}  {capture.Artifact.FileName}\n")))
                    throw new BrowserRequestException("capture_binding_invalid");
                outcome = new(request.RequestId, request.Action, "completed", ArtifactTransfer: capture);
            }
            catch (Exception exception)
            {
                var code = exception switch
                {
                    SplitPackageException => "split_export_unsupported",
                    BrowserRequestException error when error.Code == "target_not_ready" => error.Code,
                    BrowserArtifactException error when error.Code is "artifact_size_limit" or "artifact_byte_capacity" or "artifact_file_capacity" => error.Code,
                    OperationCanceledException => "capture_cancelled",
                    _ => "capture_failed"
                };
                outcome = new(request.RequestId, request.Action, "failed", Error: code);
            }
            _requests[request.RequestId] = outcome; return outcome;
        }
        finally { lock (_admission) { _running = false; operationLease.Dispose(); } }
    }

    private async Task<BrowserArtifactLease?> AcquireArtifactAsync(BrowserArtifactCommandRequest? artifact,
        CancellationToken cancellation)
    {
        if (artifact is null) return null;
        if (_artifactStore is null || !BrowserArtifactStore.Capability.Supported)
            throw new BrowserRequestException("artifact_staging_unavailable", 503);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(BrowserArtifactStore.TransferTimeoutSeconds));
            return await _artifactStore().AcquireAsync(artifact.ArtifactId, artifact.Sha256,
                artifact.SizeBytes, timeout.Token).ConfigureAwait(false);
        }
        catch (BrowserArtifactException exception) { throw new BrowserRequestException(exception.Code, 409); }
        catch (PlatformNotSupportedException) { throw new BrowserRequestException("artifact_staging_unavailable", 503); }
        catch (OperationCanceledException) { throw new BrowserRequestException("artifact_operation_timeout", 408); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new BrowserRequestException("artifact_access_failed", 409); }
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
                "device.status" or "apk.list" or "adb.forwards" => ["requestId", "action", "serial"],
                "files.list" => ["requestId", "action", "serial", "path"],
                "apk.permissions" => ["requestId", "action", "serial", "package"],
                "device.actor-observe" or "device.actor-identity" => ["requestId", "action", "serial", "pid"],
                "wifi.enable" => ["requestId", "action", "serial", "port", "confirmed"],
                "wifi.connect" or "wifi.disconnect" => ["requestId", "action", "serial", "host", "port", "confirmed"],
                "device.keep-awake" => ["requestId", "action", "serial", "durationMilliseconds", "confirmed"],
                "device.normal" or "device.performance-clear" => ["requestId", "action", "serial", "confirmed"],
                "device.performance" => ["requestId", "action", "serial", "cpu", "gpu", "confirmed"],
                _ => throw new BrowserRequestException("action_unsupported")
            };
            if (!names.SetEquals(allowed)) throw new BrowserRequestException("fields_invalid");
            var mutation = action is "wifi.enable" or "wifi.connect" or "wifi.disconnect" or
                "device.keep-awake" or "device.normal" or "device.performance" or "device.performance-clear";
            if (mutation && root.GetProperty("confirmed").ValueKind != JsonValueKind.True)
                throw new BrowserRequestException("confirmation_required");
            var serial = action == "devices" ? null : RequiredString(root, "serial");
            OperatorCommand command = action switch
            {
                "devices" => OperatorCommands.DiscoverDevices(),
                "device.status" => OperatorCommands.ReadQuestControls(serial!),
                "files.list" => OperatorCommands.ListFiles(serial!, RequiredString(root, "path")),
                "apk.list" => OperatorCommands.ListPackages(serial!),
                "apk.permissions" => OperatorCommands.ObservePackagePermissions(serial!, RequiredString(root, "package")),
                "device.actor-observe" => OperatorCommands.ObserveDeviceActor(serial!, Integer(root, "pid", 1, int.MaxValue)),
                "device.actor-identity" => OperatorCommands.ObserveDeviceActorIdentity(serial!, Integer(root, "pid", 1, int.MaxValue)),
                "adb.forwards" => OperatorCommands.InventoryAdbForwards(serial!),
                "wifi.enable" => OperatorCommands.EnableWifiAdb(serial!, Integer(root, "port", 1, 65535), true),
                "wifi.connect" => WifiEndpoint(root, serial!, disconnect: false),
                "wifi.disconnect" => WifiEndpoint(root, serial!, disconnect: true),
                "device.performance" => OperatorCommands.SetQuestPerformance(serial!,
                    PerformanceLevel(root, "cpu"), PerformanceLevel(root, "gpu"), operatorConfirmed: true),
                "device.performance-clear" => OperatorCommands.SetQuestPerformance(serial!, null, null, clear: true, operatorConfirmed: true),
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

    private static OperatorCommand WifiEndpoint(JsonElement root, string serial, bool disconnect)
    {
        var host = RequiredString(root, "host");
        var port = Integer(root, "port", 1, 65535);
        var command = disconnect ? OperatorCommands.DisconnectWifiAdb(host, port, true) :
            OperatorCommands.ConnectWifiAdb(host, port, true);
        // This confirms one endpoint, not canonical headset or boot identity.
        if (!string.Equals(serial, $"{command.WifiHost}:{port}", StringComparison.Ordinal))
            throw new BrowserRequestException("endpoint_target_mismatch");
        return command;
    }

    private static int? PerformanceLevel(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Integer(root, name, 0, 5);

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
