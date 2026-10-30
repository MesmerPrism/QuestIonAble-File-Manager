using System.Globalization;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserApkInstallIdentity(string PackageName, string VersionCode,
    string? VersionName, string SignerSha256, string? SplitName);

public sealed record BrowserApkInstalledBase(string Serial, BrowserApkInstallIdentity Identity,
    string BaseApkSha256, long BaseApkSizeBytes, string VerificationMethod, string? VerificationFallbackReason);

public sealed record BrowserApkInstallOutcome(string Serial, BrowserArtifactReceipt Source,
    ApkInstallOptions Options, bool ExecutionStarted, bool? InstallerCommandSucceeded,
    bool? ExactInstalledBaseBytes, BrowserApkInstallIdentity? ArtifactIdentity, BrowserApkInstalledBase? Installed)
{
    public string ContractVersion => "questionable.file_manager.browser_apk_install.v1";
}

internal sealed record BrowserApkInstallRequest(string RequestId, string Serial,
    BrowserArtifactReceipt Source, ApkInstallOptions Options)
{
    internal const string Action = "apk.install";
}

internal static class BrowserApkInstallCommands
{
    internal static bool IsAction(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 5 });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString() == BrowserApkInstallRequest.Action;
        }
        catch (JsonException) { return false; }
    }

    internal static BrowserApkInstallRequest Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > BrowserCommands.MaximumRequestBytes)
            throw new BrowserRequestException("body_size_invalid", 413);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            Fields(root, "requestId", "action", "serial", "source", "options", "confirmed");
            var requestId = Text(root, "requestId");
            if (!Guid.TryParseExact(requestId, "D", out var id) || id == Guid.Empty || requestId != id.ToString("D"))
                throw new BrowserRequestException("request_id_invalid");
            if (Text(root, "action") != BrowserApkInstallRequest.Action) throw new BrowserRequestException("action_unsupported");
            if (root.GetProperty("confirmed").ValueKind != JsonValueKind.True)
                throw new BrowserRequestException("confirmation_required");
            var serial = AndroidInput.RequireSerial(Text(root, "serial"));
            var source = root.GetProperty("source");
            Fields(source, "id", "fileName", "sizeBytes", "sha256");
            var sourceId = Text(source, "id");
            if (!Guid.TryParseExact(sourceId, "N", out var artifactId) || artifactId == Guid.Empty || sourceId != artifactId.ToString("N"))
                throw new BrowserRequestException("artifact_id_invalid");
            var name = BrowserArtifactStore.ValidateFileName(Text(source, "fileName"));
            if (!string.Equals(Path.GetExtension(name), ".apk", StringComparison.OrdinalIgnoreCase))
                throw new BrowserRequestException("apk_install_name_invalid");
            var hash = Text(source, "sha256");
            if (!IsHash(hash)) throw new BrowserRequestException("artifact_hash_invalid");
            if (source.GetProperty("sizeBytes").ValueKind != JsonValueKind.Number ||
                !source.GetProperty("sizeBytes").TryGetInt64(out var size) || size is < 1 or > BrowserArtifactStore.MaximumFileBytes)
                throw new BrowserRequestException("artifact_size_invalid");
            var options = root.GetProperty("options");
            Fields(options, "replaceExisting", "allowDowngrade", "grantRuntimePermissions", "allowTestPackages");
            bool Flag(string name) => options.GetProperty(name).ValueKind switch
            {
                JsonValueKind.True => true, JsonValueKind.False => false,
                _ => throw new BrowserRequestException("options_invalid")
            };
            return new(requestId, serial, new(sourceId, name, size, hash),
                new(Flag("replaceExisting"), Flag("allowDowngrade"), Flag("grantRuntimePermissions"), Flag("allowTestPackages")));
        }
        catch (BrowserRequestException) { throw; }
        catch (BrowserArtifactException) { throw new BrowserRequestException("apk_install_name_invalid"); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new BrowserRequestException("request_invalid"); }
    }

    internal static bool IsHash(string? hash) => hash is { Length: 64 } &&
        hash.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static string Text(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.String &&
        value.GetProperty(name).GetString() is { Length: > 0 and <= 256 } text
            ? text : throw new BrowserRequestException("field_invalid");

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
    private TaskCompletionSource? _singleApkPreflight;
    private TaskCompletionSource? _singleApkCompletion;
    private bool _singleApkClosing;

    public Task DrainSingleApkInstallsAsync()
    {
        lock (_admission)
        {
            _singleApkClosing = true;
            return Task.WhenAll(_singleApkPreflight?.Task ?? Task.CompletedTask,
                _singleApkCompletion?.Task ?? Task.CompletedTask);
        }
    }

    private void CheckSingleApkAdmission(BrowserApkInstallRequest request)
    {
        if (_requests.ContainsKey(request.RequestId)) throw new BrowserRequestException("request_already_consumed", 409);
        if (_singleApkClosing || _running || _operationGate.IsBusy) throw new BrowserRequestException("busy", 409);
        if (_requests.Count >= MaximumRequests) throw new BrowserRequestException("session_capacity_reached", 409);
        if (_artifactStore is null || !BrowserArtifactStore.Capability.Supported)
            throw new BrowserRequestException("artifact_staging_unavailable", 503);
    }

    private async Task<BrowserCommandOutcome> ExecuteSingleApkInstallAsync(BrowserApkInstallRequest request,
        CancellationToken stopping)
    {
        TaskCompletionSource preflight;
        lock (_admission)
        {
            CheckSingleApkAdmission(request);
            if (_singleApkPreflight is not null) throw new BrowserRequestException("busy", 409);
            preflight = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _singleApkPreflight = preflight;
        }
        BrowserArtifactLease? source = null;
        IDisposable? gate = null;
        TaskCompletionSource? completion = null;
        OperatorCommand command;
        var admitted = false;
        try
        {
            source = await _artifactStore!().AcquireAsync(request.Source.Id, request.Source.Sha256,
                request.Source.SizeBytes, stopping).ConfigureAwait(false);
            if (source.Receipt != request.Source) throw new BrowserRequestException("apk_install_source_binding_mismatch", 409);
            command = OperatorCommands.InstallApk(request.Serial, source.Path, request.Options);
            lock (_admission)
            {
                CheckSingleApkAdmission(request);
                gate = _operationGate.TryAcquire() ?? throw new BrowserRequestException("busy", 409);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _singleApkCompletion = completion;
                _requests[request.RequestId] = SingleApkOutcome(request, "running", false);
                _running = true;
                admitted = true;
            }
        }
        catch (Exception exception)
        {
            if (exception is BrowserRequestException) throw;
            throw new BrowserRequestException(exception is BrowserArtifactException artifact ? artifact.Code :
                exception is OperationCanceledException ? "apk_install_preflight_cancelled" : "apk_install_source_unavailable", 409);
        }
        finally
        {
            try { if (!admitted) source?.Dispose(); }
            finally
            {
                lock (_admission)
                {
                    _singleApkPreflight = null;
                    preflight.TrySetResult();
                }
            }
        }

        // Registered completion owns the lease and gate until actual executor settlement.
        BrowserCommandOutcome outcome;
        try
        {
            try
            {
                // Core entry includes readiness, tools and admission, not just native dispatch.
                _requests[request.RequestId] = SingleApkOutcome(request, "running", true);
                var result = await _execute(command, stopping).ConfigureAwait(false);
                outcome = ProjectSingleApk(request, command, source!, result);
            }
            catch (Exception)
            { outcome = SingleApkOutcome(request, "outcomeUnknown", true, "apk_install_outcome_unknown"); }
            _requests[request.RequestId] = outcome;
            return outcome;
        }
        finally
        {
            try { source!.Dispose(); }
            finally
            {
                lock (_admission)
                {
                    _running = false;
                    gate!.Dispose();
                    _singleApkCompletion = null;
                    completion!.TrySetResult();
                }
            }
        }
    }

    private static BrowserCommandOutcome SingleApkOutcome(BrowserApkInstallRequest request, string state,
        bool started, string? error = null, OperatorMutationReceipt? mutation = null,
        BrowserApkInstallIdentity? identity = null, BrowserApkInstalledBase? installed = null) =>
        new(request.RequestId, BrowserApkInstallRequest.Action, state, Error: error, Mutation: mutation)
        {
            ApkInstall = new(request.Serial, request.Source with { }, request.Options with { }, started,
                state == "completed" ? true : null, state == "completed" ? true : null, identity, installed)
        };

    private static BrowserCommandOutcome ProjectSingleApk(BrowserApkInstallRequest request, OperatorCommand command,
        BrowserArtifactLease source, OperatorExecutionResult result)
    {
        BrowserCommandOutcome Unknown() => SingleApkOutcome(request, "outcomeUnknown", true, "apk_install_binding_mismatch");
        if (result.Command is not { Kind: OperatorCommandKind.InstallApk } returned ||
            returned.Serial != request.Serial || returned.LocalPath != command.LocalPath ||
            returned.InstallOptions != request.Options || returned.CliArguments is null ||
            !returned.CliArguments.SequenceEqual(command.CliArguments, StringComparer.Ordinal) ||
            result.InspectedApkInstallResult is not { Artifact: not null, Installed: not null,
                CommandResult: { Succeeded: true, Arguments: not null } dispatch } install ||
            !ReferenceEquals(result.CommandResult, dispatch) || result.ApkArtifactInspection != install.Artifact ||
            install.Artifact.Path != source.Path || install.Artifact.SizeBytes != request.Source.SizeBytes ||
            install.Artifact.Sha256 != request.Source.Sha256 ||
            install.Artifact.Identity is not { } identity || !SingleApkIdentityRepresentable(identity) ||
            install.Installed.Serial != request.Serial || install.Installed.Identity != identity ||
            install.Installed.BaseApkSha256 != request.Source.Sha256 ||
            install.Installed.BaseApkSizeBytes != request.Source.SizeBytes ||
            !SingleApkMethodBound(install.Installed) || !SingleApkReceiptBound(command, result.MutationReceipt))
            return Unknown();
        List<string> arguments = ["-s", request.Serial, "install"];
        if (request.Options.ReplaceExisting) arguments.Add("-r");
        if (request.Options.AllowDowngrade) arguments.Add("-d");
        if (request.Options.GrantRuntimePermissions) arguments.Add("-g");
        if (request.Options.AllowTestPackages) arguments.Add("-t");
        if (dispatch.Arguments.Count != arguments.Count + 1 ||
            !dispatch.Arguments.Take(arguments.Count).SequenceEqual(arguments, StringComparer.Ordinal) ||
            !SingleApkAdmittedPathBound(dispatch.Arguments[^1], source.Path)) return Unknown();
        var safeIdentity = new BrowserApkInstallIdentity(identity.PackageName,
            identity.VersionCode.ToString(CultureInfo.InvariantCulture), identity.VersionName, identity.SignerSha256, null);
        var safeInstalled = new BrowserApkInstalledBase(request.Serial, safeIdentity, install.Installed.BaseApkSha256!,
            install.Installed.BaseApkSizeBytes, install.Installed.VerificationMethod, install.Installed.VerificationFallbackReason);
        var receipt = result.MutationReceipt! with
        {
            DesiredState = "apk-install-exact-base",
            ObservedState = "exact-installed-base-bytes-and-identity",
            Transitions = Array.AsReadOnly(result.MutationReceipt!.Transitions.Select(transition => transition with
            { Message = "apk-install-" + JsonNamingPolicy.CamelCase.ConvertName(transition.Stage.ToString()) }).ToArray())
        };
        return SingleApkOutcome(request, "completed", true, mutation: receipt, identity: safeIdentity, installed: safeInstalled);
    }

    private static bool SingleApkIdentityRepresentable(ApkArtifactIdentity identity)
    {
        try
        {
            return identity.VersionCode > 0 && identity.VersionName is not { Length: > 512 } &&
                identity.SplitName is null && BrowserApkInstallCommands.IsHash(identity.SignerSha256) &&
                AndroidInput.RequirePackageName(identity.PackageName) == identity.PackageName;
        }
        catch (ArgumentException) { return false; }
    }

    private static bool SingleApkMethodBound(InstalledApkIdentity installed) =>
        installed.VerificationMethod == "same-opened-handle-device-sha256" && installed.VerificationFallbackReason is null ||
        installed.VerificationMethod == "host-streamed-sha256" &&
        installed.VerificationFallbackReason == "remote-handle-digest-capability-unsupported";

    private static bool SingleApkAdmittedPathBound(string? path, string source)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Any(char.IsControl) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".apk", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (Path.GetFullPath(path) != path || string.Equals(path, source, StringComparison.OrdinalIgnoreCase)) return false;
            var root = Path.GetPathRoot(path)!;
            foreach (var component in path[root.Length..].Split(Path.DirectorySeparatorChar))
            {
                if (component.Length == 0 || component[^1] is '.' or ' ' ||
                    component.Any(character => character is '<' or '>' or ':' or '"' or '/' or '|' or '?' or '*')) return false;
                var stem = component.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                    stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        { return false; }
    }

    private static bool SingleApkReceiptBound(OperatorCommand command, OperatorMutationReceipt? receipt)
    {
        // OperatorMutations is Core-internal; this is its exact current InstallApk desired state.
        if (receipt is null || receipt.CommandKind != OperatorCommandKind.InstallApk || receipt.Target != command.Serial ||
            receipt.DesiredState != $"inspected APK installed on {command.Serial}: {Path.GetFileName(command.LocalPath)}" ||
            receipt.Stage != OperatorMutationStage.Confirmed || !receipt.HeadsetReadback || !receipt.IsTerminal ||
            receipt.ObservedState is null || receipt.OperationId is null || !receipt.OperationId.StartsWith("pc-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(receipt.OperationId[3..], "N", out var id) || id == Guid.Empty ||
            receipt.OperationId[3..] != id.ToString("N") || receipt.Transitions is not { Count: 3 } transitions) return false;
        var stages = new[] { OperatorMutationStage.Sent, OperatorMutationStage.Pending, OperatorMutationStage.Confirmed };
        for (var index = 0; index < stages.Length; index++)
            if (transitions[index] is not { Message: not null } transition || transition.Stage != stages[index] ||
                transition.At == default || index > 0 && transition.At < transitions[index - 1].At) return false;
        return true;
    }
}
