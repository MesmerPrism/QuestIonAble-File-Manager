using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDiagnosticOutputReference(string ContractVersion, string OperationId, string CommandDigest,
    string OutputId, long SizeBytes, string Sha256);
public sealed record BrowserDiagnosticOutputResult(string State, string Scope, BrowserArtifactReceipt Artifact,
    BrowserDiagnosticOutputReference Source, bool DeviceDispatched);

// Private custody seam around the actual Native lease. Tests can inject owned
// streams without opening Native state, keys, SDK tools or an ADB client.
internal sealed class BrowserRetainedOutputLease(LocalApiV3ArchiveDescriptor descriptor, Stream stream, IDisposable owner) : IDisposable
{
    private IDisposable? _owner = owner;
    internal LocalApiV3ArchiveDescriptor Descriptor { get; } = descriptor;
    internal Stream Stream => _owner is not null ? stream : throw new ObjectDisposedException(nameof(BrowserRetainedOutputLease));
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}

public sealed partial class BrowserRetainedOperations
{
    private static bool HasV3(BrowserRetainedOwnerCalls owner) => owner.PreflightV3 is not null && owner.ExecuteV3 is not null &&
        owner.StatusV3 is not null && owner.CancelV3 is not null && owner.AcquireDiagnosticOutput is not null;
    private static IReadOnlyList<string> OwnerVersions(BrowserRetainedOwnerCalls owner) =>
        [LocalApiContract.Version, .. HasV2(owner) ? new[] { LocalApiV2Contract.Version } : [], .. HasV3(owner) ? new[] { LocalApiV3Contract.Version } : []];
    private BrowserRetainedOwnerCalls OwnerV3()
    {
        var owner = Owner();
        if (!HasV3(owner)) throw new BrowserRetainedException("retained_contract_unavailable", 501);
        return owner;
    }
    private void EnsureV3Supported()
    {
        EnsureSupported();
        if (Capability.ContractVersions?.Contains(LocalApiV3Contract.Version, StringComparer.Ordinal) != true)
            throw new BrowserRetainedException("retained_contract_unavailable", 501);
    }

    public static BrowserRetainedPreflightRequest ParseV3Preflight(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var json = ParseObject(body); var root = json.RootElement;
            var fields = new List<string> { "contractVersion", "artifactId", "sha256", "sizeBytes", "command", "serial" };
            if (root.TryGetProperty("expiresInSeconds", out _)) fields.Add("expiresInSeconds");
            RequireFields(root, fields);
            if (Text(root, "contractVersion") != LocalApiV3Contract.Version) throw new BrowserRetainedException("contract_unsupported");
            if (Text(root, "command") != "apk.diagnose") throw new BrowserRetainedException("command_not_allowed");
            var artifact = BrowserArtifactCommands.RequireArtifactId(Text(root, "artifactId"));
            var hash = BrowserArtifactCommands.RequireHash(Text(root, "sha256"));
            var size = Size(root, "sizeBytes", BrowserArtifactStore.MaximumFileBytes);
            var serial = Text(root, "serial");
            int? expires = null;
            if (root.TryGetProperty("expiresInSeconds", out var expiry))
            {
                if (!expiry.TryGetInt32(out var value) || value is < 1 or > 300) throw new BrowserRetainedException("expiry_invalid");
                expires = value;
            }
            new LocalApiV3PreflightRequest(LocalApiV3Contract.Version, "apk.diagnose", "base.apk", serial, expires).Validate();
            return new(artifact, hash, size, "apk.diagnose", serial, null, expires, LocalApiV3Contract.Version);
        }
        catch (Exception exception) { throw BeforeV3Admission(exception); }
    }

    private static long Size(JsonElement root, string name, long maximum)
    {
        if (!root.GetProperty(name).TryGetInt64(out var value) || value < 1 || value > maximum)
            throw new BrowserRetainedException("artifact_size_invalid");
        return value;
    }
    private static Binding ParseV3Binding(ReadOnlyMemory<byte> body)
    {
        try { return ParseBinding(body, LocalApiV3Contract.Version); }
        catch (Exception exception) { throw BeforeV3Admission(exception); }
    }
    public static BrowserDiagnosticOutputReference ParseDiagnosticOutput(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var json = ParseObject(body); var root = json.RootElement;
            RequireFields(root, ["contractVersion", "operationId", "commandDigest", "outputId", "sizeBytes", "sha256"]);
            var version = Text(root, "contractVersion"); var id = Text(root, "operationId"); var digest = Text(root, "commandDigest");
            new LocalApiV3OperationRequest(version, id, digest).Validate();
            var output = Text(root, "outputId");
            if (!output.StartsWith("diag-", StringComparison.Ordinal)) throw new BrowserRetainedException("diagnostic_output_binding_invalid");
            BrowserArtifactCommands.RequireArtifactId(output[5..]);
            return new(version, id, digest, output, Size(root, "sizeBytes", 4 * 1024 * 1024), BrowserArtifactCommands.RequireHash(Text(root, "sha256")));
        }
        catch (Exception exception) { throw BeforeV3Admission(exception); }
    }
    private static byte[] V3Body(Binding binding) => JsonSerializer.SerializeToUtf8Bytes(
        new LocalApiV3OperationRequest(LocalApiV3Contract.Version, binding.OperationId, binding.CommandDigest), Json);

    public async Task<LocalApiV3PreflightResult> PreflightV3Async(ReadOnlyMemory<byte> body, CancellationToken cancellation = default)
    {
        var request = ParseV3Preflight(body);
        try
        {
            using var call = EnterCall(); EnsureV3Supported();
            using var gate = _tryAcquireOperation() ?? throw new BrowserRetainedException("operation_busy", 409);
            await using var source = await _artifacts().AcquireAsync(request.ArtifactId, request.Sha256, request.SizeBytes, cancellation).ConfigureAwait(false);
            if (source.Receipt.Id != request.ArtifactId || source.Receipt.Sha256 != request.Sha256 || source.Receipt.SizeBytes != request.SizeBytes)
                throw new BrowserRetainedException("artifact_binding_mismatch", 409);
            if (!source.Receipt.FileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                throw new BrowserRetainedException("artifact_filename_invalid", 400, false);
            var owner = OwnerV3();
            var nativeRequest = JsonSerializer.SerializeToUtf8Bytes(new LocalApiV3PreflightRequest(LocalApiV3Contract.Version,
                "apk.diagnose", source.Path, request.Serial!, request.ExpiresInSeconds), Json);
            var value = await owner.PreflightV3!(nativeRequest, cancellation).ConfigureAwait(false);
            try { value.Validate(); }
            catch { throw new BrowserRetainedException("retained_operation_binding_invalid", 409); }
            if (value.ContractVersion != LocalApiV3Contract.Version || value.Command != "apk.diagnose" || value.Serial != request.Serial ||
                value.Artifact.SizeBytes != request.SizeBytes || value.Artifact.Sha256 != request.Sha256)
                throw new BrowserRetainedException("artifact_binding_mismatch", 409);
            return value;
        }
        catch (Exception exception) { throw V3Error(exception); }
    }

    public async Task<LocalApiV3OperationStatus> ExecuteV3Async(ReadOnlyMemory<byte> body, CancellationToken stopping = default)
    {
        var binding = ParseV3Binding(body);
        try
        {
            using var call = EnterCall(); EnsureV3Supported();
            var owner = OwnerV3();
            var nativeBody = V3Body(binding);
            var current = VerifyV3(owner.StatusV3!(nativeBody), binding);
            IDisposable gate;
            lock (_sync)
            {
                if (_executing.Contains(binding.OperationId) || current.Stage != LocalApiOperationStage.Preflighted || current.Consumed)
                    throw new BrowserRetainedException("operation_consumed", 409);
                gate = _tryAcquireOperation() ?? throw new BrowserRetainedException("operation_busy", 409);
                _executing.Add(binding.OperationId);
            }
            using (gate)
            {
                try
                {
                    // Native retains its own execution token. HTTP/page/lifetime loss
                    // does not release this gate or signal cancellation; CancelV3 does.
                    var value = await owner.ExecuteV3!(nativeBody, CancellationToken.None).ConfigureAwait(false);
                    return VerifyV3(value, binding, current);
                }
                finally { lock (_sync) _executing.Remove(binding.OperationId); }
            }
        }
        catch (Exception exception) { throw V3Error(exception); }
    }

    public LocalApiV3OperationStatus GetV3Status(ReadOnlyMemory<byte> body)
    {
        var binding = ParseV3Binding(body);
        try
        {
            using var call = EnterCall(); EnsureV3Supported();
            return VerifyV3(OwnerV3().StatusV3!(V3Body(binding)), binding);
        }
        catch (Exception exception) { throw V3Error(exception); }
    }
    public LocalApiV3OperationStatus CancelV3(ReadOnlyMemory<byte> body)
    {
        var binding = ParseV3Binding(body);
        try
        {
            using var call = EnterCall(); EnsureV3Supported();
            var owner = OwnerV3(); var nativeBody = V3Body(binding);
            var current = VerifyV3(owner.StatusV3!(nativeBody), binding);
            return VerifyV3(owner.CancelV3!(nativeBody), binding, current);
        }
        catch (Exception exception) { throw V3Error(exception); }
    }

    private static LocalApiV3OperationStatus VerifyV3(LocalApiV3OperationStatus value, Binding binding, LocalApiV3OperationStatus? original = null)
    {
        try { value.Validate(); }
        catch { throw new BrowserRetainedException("retained_operation_binding_invalid", 409); }
        if (value.ContractVersion != LocalApiV3Contract.Version || value.OperationId != binding.OperationId || value.CommandDigest != binding.CommandDigest ||
            value.Command != "apk.diagnose" || value.Stage == LocalApiOperationStage.CleanupDebt ||
            (value.Stage is LocalApiOperationStage.Preparing or LocalApiOperationStage.Preflighted && value.Consumed) ||
            (value.Stage is LocalApiOperationStage.Running or LocalApiOperationStage.CancellationRequested or LocalApiOperationStage.Completed or LocalApiOperationStage.Expired && !value.Consumed) ||
            (original is not null && (value.Serial != original.Serial || value.ExpiresAt != original.ExpiresAt || value.Output.Intent != original.Output.Intent)))
            throw new BrowserRetainedException("retained_operation_binding_invalid", 409);
        return value;
    }

    public async Task<BrowserDiagnosticOutputResult> PrepareDiagnosticOutputAsync(ReadOnlyMemory<byte> body, CancellationToken cancellation = default)
    {
        var source = ParseDiagnosticOutput(body);
        var binding = new Binding(source.OperationId, source.CommandDigest, LocalApiV3Contract.Version);
        try
        {
            using var call = EnterCall(); EnsureV3Supported();
            using var gate = _tryAcquireOperation() ?? throw new BrowserRetainedException("operation_busy", 409);
            var owner = OwnerV3(); var status = VerifyV3(owner.StatusV3!(V3Body(binding)), binding);
            var descriptor = status.Output.Archive;
            if (!status.Output.Available || descriptor is null)
                throw new BrowserRetainedException("diagnostic_output_unavailable", 409);
            if (descriptor.OutputId != source.OutputId || descriptor.SizeBytes != source.SizeBytes || descriptor.Sha256 != source.Sha256)
                throw new BrowserRetainedException("diagnostic_output_binding_invalid", 409);
            using var native = await owner.AcquireDiagnosticOutput!(source.OperationId, source.CommandDigest,
                source.OutputId, source.SizeBytes, source.Sha256, cancellation).ConfigureAwait(false);
            if (native.Descriptor != descriptor || !native.Stream.CanRead || native.Stream.CanWrite || !native.Stream.CanSeek ||
                native.Stream.Position != 0 || native.Stream.Length != descriptor.SizeBytes)
                throw new BrowserRetainedException("diagnostic_output_binding_invalid", 409);
            var artifact = await _artifacts().StageAsync("apk-diagnostics.zip", native.Stream, source.SizeBytes, source.Sha256, cancellation).ConfigureAwait(false);
            if (artifact.FileName != "apk-diagnostics.zip" || artifact.SizeBytes != source.SizeBytes || artifact.Sha256 != source.Sha256)
                throw new BrowserRetainedException("diagnostic_output_binding_invalid", 409);
            return new("staged", "localOnly", artifact, source, false);
        }
        catch (Exception exception) { throw V3Error(exception); }
    }

    private static BrowserRetainedException BeforeV3Admission(Exception exception)
    {
        var error = V3Error(exception);
        return new(error.Code, error.StatusCode, false);
    }
    private static BrowserRetainedException V3Error(Exception exception) => exception switch
    {
        LocalApiException { Code: "diagnostic_output_unavailable" or "diagnostic_output_binding_invalid" or "diagnostic_descriptor_invalid" } => new("diagnostic_output_unavailable", 409),
        LocalApiException { Code: "operation_preparing" } => new("operation_preparing", 409),
        LocalApiException { Code: "serial_invalid" or "request_invalid" } api => new(api.Code),
        BrowserArtifactException { Code: "artifact_file_capacity" or "artifact_byte_capacity" or "artifact_size_limit" or "artifact_capacity" } => new("artifact_capacity", 409),
        KeyNotFoundException => new("request_invalid"),
        _ => Translate(exception)
    };
}
