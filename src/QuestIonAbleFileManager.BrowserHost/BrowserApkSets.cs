using System.Collections.ObjectModel;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserApkSetCapability(bool Supported, string Code, bool WorkingCopyCleanupPending,
    int MaximumParts = 8, long MaximumFileBytes = BrowserArtifactStore.MaximumFileBytes,
    long MaximumSourceBytes = BrowserArtifactStore.MaximumTotalBytes,
    long MaximumWorkingCopyBytes = BrowserApkSetWorkStore.MaximumBytes,
    long MaximumSourceAndWorkingBytes = BrowserArtifactStore.MaximumTotalBytes + BrowserApkSetWorkStore.MaximumBytes,
    int MaximumConcurrentOperations = 1);

public sealed record BrowserApkSetOutcome(string Serial, IReadOnlyList<BrowserArtifactReceipt> Sources,
    int PartCount, ApkInstallOptions Options, bool ExecutionStarted, bool? PackageManagerCompleted,
    bool? InventoryRefreshed, string SourceCleanup)
{
    public string ContractVersion => "questionable.file_manager.browser_apk_set.v1";
}

internal sealed record BrowserApkSetRequest(string RequestId, string Serial,
    IReadOnlyList<BrowserArtifactReceipt> Sources, ApkInstallOptions Options)
{
    internal const string Action = "apk.install-set";
}

/// <summary>Closed ordinary single-target set input; Android owns split/package compatibility checks.</summary>
internal static class BrowserApkSetCommands
{
    internal static bool IsAction(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 5 });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString() == BrowserApkSetRequest.Action;
        }
        catch (JsonException) { return false; }
    }

    internal static BrowserApkSetRequest Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes)
            throw new BrowserRequestException("body_size_invalid", 413);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            Fields(root, "requestId", "action", "serial", "sources", "options", "confirmed");
            var requestId = String(root, "requestId");
            if (!Guid.TryParseExact(requestId, "D", out var id) || id == Guid.Empty || requestId != id.ToString("D"))
                throw new BrowserRequestException("request_id_invalid");
            if (String(root, "action") != BrowserApkSetRequest.Action) throw new BrowserRequestException("action_unsupported");
            if (root.GetProperty("confirmed").ValueKind != JsonValueKind.True)
                throw new BrowserRequestException("confirmation_required");
            var serial = AndroidInput.RequireSerial(String(root, "serial"));
            var options = root.GetProperty("options");
            Fields(options, "replaceExisting", "allowDowngrade", "grantRuntimePermissions", "allowTestPackages");
            bool Flag(string name) => options.GetProperty(name).ValueKind switch
            {
                JsonValueKind.True => true, JsonValueKind.False => false,
                _ => throw new BrowserRequestException("options_invalid")
            };
            var installOptions = new ApkInstallOptions(Flag("replaceExisting"), Flag("allowDowngrade"),
                Flag("grantRuntimePermissions"), Flag("allowTestPackages"));
            var sources = root.GetProperty("sources");
            if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() is < 2 or > BrowserArtifactStore.MaximumFiles)
                throw new BrowserRequestException("apk_set_parts_invalid");
            List<BrowserArtifactReceipt> receipts = [];
            HashSet<string> ids = new(StringComparer.Ordinal);
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var source in sources.EnumerateArray())
            {
                Fields(source, "id", "fileName", "sizeBytes", "sha256");
                var sourceId = BrowserArtifactCommands.RequireArtifactId(String(source, "id"));
                var name = BrowserArtifactStore.ValidateFileName(String(source, "fileName"));
                var hash = BrowserArtifactCommands.RequireHash(String(source, "sha256"));
                if (!string.Equals(Path.GetExtension(name), ".apk", StringComparison.OrdinalIgnoreCase))
                    throw new BrowserRequestException("apk_set_name_invalid");
                if (!ids.Add(sourceId) || !names.Add(name)) throw new BrowserRequestException("apk_set_duplicate_source");
                if (source.GetProperty("sizeBytes").ValueKind != JsonValueKind.Number ||
                    !source.GetProperty("sizeBytes").TryGetInt64(out var size) || size is < 0 or > BrowserArtifactStore.MaximumFileBytes)
                    throw new BrowserRequestException("artifact_size_invalid");
                total = checked(total + size);
                if (total > BrowserArtifactStore.MaximumTotalBytes) throw new BrowserRequestException("apk_set_source_capacity");
                receipts.Add(new(sourceId, name, size, hash));
            }
            return new(requestId, serial, new ReadOnlyCollection<BrowserArtifactReceipt>(receipts), installOptions);
        }
        catch (BrowserRequestException) { throw; }
        catch (BrowserArtifactException) { throw new BrowserRequestException("apk_set_name_invalid"); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new BrowserRequestException("request_invalid"); }
    }

    private static string String(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String && root.GetProperty(name).GetString() is { Length: > 0 and <= 256 } value
            ? value : throw new BrowserRequestException("field_invalid");

    private static void Fields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new BrowserRequestException("request_invalid");
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new BrowserRequestException("duplicate_field");
        if (!names.SetEquals(expected)) throw new BrowserRequestException("fields_invalid");
    }
}

public sealed partial class BrowserCommands
{
    private readonly BrowserApkSetWorkStore _apkSetWork = new();
    private TaskCompletionSource? _apkSetPreflight;
    private TaskCompletionSource? _apkSetCompletion;
    private bool _apkSetsClosing;

    internal BrowserCommands(Func<OperatorCommand, CancellationToken, Task<OperatorExecutionResult>> execute,
        BrowserArtifactStore artifactStore, BrowserApkSetWorkStore workStore, BrowserOperationGate? operationGate = null)
        : this(execute, () => artifactStore, operationGate) => _apkSetWork = workStore;

    public BrowserApkSetCapability ApkSetCapability => new(
        _artifactStore is not null && BrowserArtifactStore.Capability.Supported,
        _artifactStore is not null && BrowserArtifactStore.Capability.Supported ? "platform_supported" : "artifact_staging_unavailable",
        _apkSetWork.CleanupPending);

    public Task DrainApkSetsAsync()
    {
        lock (_admission)
        {
            _apkSetsClosing = true;
            return Task.WhenAll(_apkSetPreflight?.Task ?? Task.CompletedTask, _apkSetCompletion?.Task ?? Task.CompletedTask);
        }
    }

    public async Task<bool> CleanupApkSetWorkAsync()
    {
        await DrainApkSetsAsync().ConfigureAwait(false);
        return _apkSetWork.Cleanup();
    }

    private void CheckApkSetAdmission(BrowserApkSetRequest request)
    {
        if (_requests.ContainsKey(request.RequestId)) throw new BrowserRequestException("request_already_consumed", 409);
        if (_apkSetsClosing || _running || _operationGate.IsBusy) throw new BrowserRequestException("busy", 409);
        if (_requests.Count >= MaximumRequests) throw new BrowserRequestException("session_capacity_reached", 409);
        if (!ApkSetCapability.Supported) throw new BrowserRequestException("artifact_staging_unavailable", 503);
        if (_apkSetWork.CleanupPending) throw new BrowserRequestException("apk_set_work_cleanup_pending", 409);
    }

    private async Task<BrowserCommandOutcome> ExecuteApkSetAsync(BrowserApkSetRequest request, CancellationToken stopping)
    {
        TaskCompletionSource preflight;
        lock (_admission)
        {
            CheckApkSetAdmission(request);
            if (_apkSetPreflight is not null) throw new BrowserRequestException("busy", 409);
            preflight = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _apkSetPreflight = preflight; // Drain owns this slot before the first await.
        }
        List<BrowserArtifactLease> sources = [];
        BrowserApkSetWorkStore.Work? work = null;
        IDisposable? gate = null;
        TaskCompletionSource? completion = null;
        var admitted = false;
        try
        {
            foreach (var receipt in request.Sources)
            {
                var lease = await _artifactStore!().AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes, stopping)
                    .ConfigureAwait(false);
                sources.Add(lease);
                if (lease.Receipt != receipt) throw new BrowserRequestException("apk_set_source_binding_mismatch", 409);
            }
            work = _apkSetWork.Reserve(request.Sources.Sum(source => source.SizeBytes));
            lock (_admission)
            {
                CheckApkSetAdmission(request);
                gate = _operationGate.TryAcquire() ?? throw new BrowserRequestException("busy", 409);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _apkSetCompletion = completion;
                _requests[request.RequestId] = ApkSetOutcome(request, "running", false, "notStarted");
                _running = true;
                admitted = true;
            }
        }
        catch (Exception exception)
        {
            // Every exception in this block is before ID/gate admission and Core entry.
            if (exception is BrowserRequestException) throw;
            throw new BrowserRequestException(exception is BrowserArtifactException artifact ? artifact.Code :
                exception is OperationCanceledException ? "apk_set_preflight_cancelled" : "apk_set_source_unavailable", 409);
        }
        finally
        {
            if (!admitted)
            {
                work?.Cleanup();
                foreach (var source in sources) source.Dispose();
            }
            lock (_admission)
            {
                _apkSetPreflight = null;
                preflight.TrySetResult();
            }
        }

        // The registered completion now owns all preparation/execution/cleanup custody.
        var started = false;
        var outcome = ApkSetOutcome(request, "preparationFailed", false, "notStarted", "apk_set_preparation_failed");
        try
        {
            try
            {
                await work!.PrepareAsync(sources, stopping, () =>
                    _requests[request.RequestId] = ApkSetOutcome(request, "running", false, "held")).ConfigureAwait(false);
                _requests[request.RequestId] = ApkSetOutcome(request, "running", false, "held");
                var command = OperatorCommands.InstallApkBundle(request.Serial, work.Folder, request.Options);
                work.Validate();
                if (command.ApkBundle is null || !SamePaths(command.ApkBundle.ApkPaths, work.Paths))
                    throw new BrowserArtifactException("apk_set_membership_changed");
                // Conservative marker immediately before the delegate, including its
                // ready-device lookup. This is deliberately not an ADB dispatch claim.
                started = true;
                _requests[request.RequestId] = ApkSetOutcome(request, "running", true, "held");
                var result = await _execute(command, stopping).ConfigureAwait(false);
                outcome = ProjectApkSet(request, command, result);
            }
            catch (Exception)
            {
                outcome = ApkSetOutcome(request, started ? "outcomeUnknown" : "preparationFailed", started, "held",
                    started ? "apk_set_outcome_unknown" : "apk_set_preparation_failed");
            }
        }
        finally
        {
            // Native settlement precedes this block. Local cleanup never changes
            // Android facts, and failed exact deletion keeps quota/ancestor custody.
            var cleaned = work!.Cleanup();
            foreach (var source in sources) source.Dispose();
            outcome = outcome with { ApkSet = outcome.ApkSet! with { SourceCleanup = cleaned ? "completed" : "pending" } };
            _requests[request.RequestId] = outcome;
            lock (_admission)
            {
                _running = false;
                gate!.Dispose();
                _apkSetCompletion = null;
                completion!.TrySetResult();
            }
        }
        return outcome;
    }

    private static BrowserCommandOutcome ApkSetOutcome(BrowserApkSetRequest request, string state,
        bool started, string cleanup, string? error = null, OperatorMutationReceipt? mutation = null) =>
        new(request.RequestId, BrowserApkSetRequest.Action, state, Error: error, Mutation: mutation)
        {
            ApkSet = new(request.Serial, Array.AsReadOnly(request.Sources.ToArray()), request.Sources.Count,
                request.Options with { }, started, state == "completed" ? true : null,
                state == "completed" ? true : null, cleanup)
        };

    private static bool SamePaths(IEnumerable<string> actual, IEnumerable<string> expected) =>
        actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static BrowserCommandOutcome ProjectApkSet(BrowserApkSetRequest request,
        OperatorCommand command, OperatorExecutionResult result)
    {
        BrowserCommandOutcome Unknown() => ApkSetOutcome(request, "outcomeUnknown", true, "held", "apk_set_binding_mismatch");
        var bundle = command.ApkBundle!;
        var returned = result.Command;
        if (returned is null || returned.Kind != OperatorCommandKind.InstallApkBundle ||
            returned.Serial != request.Serial || returned.InstallOptions != request.Options ||
            returned.LocalPath != command.LocalPath || returned.ApkBundle is null ||
            returned.ApkBundle.FolderPath != bundle.FolderPath ||
            !returned.ApkBundle.ApkPaths.SequenceEqual(bundle.ApkPaths, StringComparer.Ordinal) ||
            !returned.CliArguments.SequenceEqual(command.CliArguments, StringComparer.Ordinal) ||
            result.ApkBundleInstallResult is not { CommandResult: { Succeeded: true } dispatch } installed ||
            !installed.ApkPaths.SequenceEqual(bundle.ApkPaths, StringComparer.Ordinal) ||
            !ReferenceEquals(result.CommandResult, dispatch) || !ApkSetReceiptBound(request, result.MutationReceipt))
            return Unknown();
        List<string> arguments = ["-s", request.Serial, "install-multiple"];
        if (request.Options.ReplaceExisting) arguments.Add("-r");
        if (request.Options.AllowDowngrade) arguments.Add("-d");
        if (request.Options.GrantRuntimePermissions) arguments.Add("-g");
        if (request.Options.AllowTestPackages) arguments.Add("-t");
        arguments.AddRange(bundle.ApkPaths);
        if (dispatch.Arguments is null || !dispatch.Arguments.SequenceEqual(arguments, StringComparer.Ordinal)) return Unknown();
        var receipt = result.MutationReceipt! with
        {
            DesiredState = "apk-set-install",
            ObservedState = "package-manager-completed-inventory-refreshed",
            Transitions = result.MutationReceipt!.Transitions.Select(transition => transition with
            { Message = "apk-set-" + JsonNamingPolicy.CamelCase.ConvertName(transition.Stage.ToString()) }).ToArray()
        };
        return ApkSetOutcome(request, "completed", true, "held", mutation: receipt);
    }

    private static bool ApkSetReceiptBound(BrowserApkSetRequest request, OperatorMutationReceipt? receipt)
    {
        if (receipt is null || receipt.CommandKind != OperatorCommandKind.InstallApkBundle || receipt.Target != request.Serial ||
            receipt.DesiredState != "APK package set installed" || receipt.Stage != OperatorMutationStage.Confirmed ||
            !receipt.HeadsetReadback || receipt.ObservedState is null || receipt.OperationId is null ||
            !receipt.OperationId.StartsWith("pc-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(receipt.OperationId[3..], "N", out var id) || id == Guid.Empty ||
            receipt.OperationId[3..] != id.ToString("N") || receipt.Transitions is not { Count: 3 } transitions)
            return false;
        OperatorMutationStage[] stages = [OperatorMutationStage.Sent, OperatorMutationStage.Pending, OperatorMutationStage.Confirmed];
        for (var index = 0; index < stages.Length; index++)
            if (transitions[index] is not { Message: not null } transition || transition.Stage != stages[index] ||
                transition.At == default || index > 0 && transition.At < transitions[index - 1].At) return false;
        return true;
    }
}
