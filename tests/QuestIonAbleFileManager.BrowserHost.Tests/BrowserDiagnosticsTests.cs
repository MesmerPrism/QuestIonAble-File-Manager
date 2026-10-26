using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDiagnosticsTests
{
    [Fact]
    public void RetainedDiagnosticsHasExplicitVersionedMethodsAndJoinedDrain()
    {
        foreach (var name in new[] { "PreflightV3Async", "ExecuteV3Async", "GetV3Status", "CancelV3", "PrepareDiagnosticOutputAsync", "DrainAsync" })
            Assert.NotNull(typeof(BrowserRetainedOperations).GetMethod(name));
    }

    private const string Version = LocalApiV3Contract.Version;
    private const string Operation = "api-11111111111111111111111111111111";
    private const string Output = "diag-22222222222222222222222222222222";
    private const string Plan = "4f109825aca0feb4a2600fa9f26a60d0f5e0227193d2025e0f3d134489c2e6f0";
    private const string Serial = "QUEST-01";
    private static readonly string Digest = new('a', 64);
    private static readonly byte[] Bytes = [80, 75, 3, 4, 0, 255, 10, 13, 42];
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    private static readonly DateTimeOffset Expires = DateTimeOffset.UtcNow.AddMinutes(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static LocalApiV3OutputIntent Intent() => new(Output, "exact-installed-diagnostic-output.v1", "zip-store.v1", Plan,
        new(8, 400, 14, 262144, 3670016, 4194304, 8388608, 15, 262144, 262144, 262144, 262144, 1073741824, 256, 300));
    private static LocalApiV3ArchiveDescriptor Descriptor() => new(Output, "apk-diagnostics.zip", Bytes.Length, Hash, DateTimeOffset.UnixEpoch);
    private static LocalApiV3OperationStatus Status(LocalApiOperationStage stage = LocalApiOperationStage.Preflighted, bool available = false) => new(
        Version, Operation, "apk.diagnose", Digest, Expires, stage, stage is not (LocalApiOperationStage.Preparing or LocalApiOperationStage.Preflighted), Serial,
        new(Intent(), available ? "available" : stage == LocalApiOperationStage.Preflighted ? "preflighted" : "unavailable", available, 1, 0, Archive: available ? Descriptor() : null),
        available ? new("questionable.file_manager.apk_diagnostic_bundle.v3", DateTimeOffset.UnixEpoch, "partial", 5, 1, 0) : null);
    private static LocalApiV3PreflightResult Prepared(long size, string hash) => new(Version, Operation, "apk.diagnose", Digest, Expires,
        new(size, hash, new("com.example.app", 42, "1.2.3", new string('c', 64), null)), Serial, Intent());
    private static byte[] Binding() => JsonSerializer.SerializeToUtf8Bytes(new { contractVersion = Version, operationId = Operation, commandDigest = Digest });
    private static byte[] OutputBody() => JsonSerializer.SerializeToUtf8Bytes(new { contractVersion = Version, operationId = Operation, commandDigest = Digest,
        outputId = Output, sizeBytes = Bytes.Length, sha256 = Hash });
    private static byte[] Preflight(BrowserArtifactReceipt? source = null) => JsonSerializer.SerializeToUtf8Bytes(new
    { contractVersion = Version, artifactId = source?.Id ?? new string('3', 32), sha256 = source?.Sha256 ?? Hash,
        sizeBytes = source?.SizeBytes ?? Bytes.Length, command = "apk.diagnose", serial = Serial });
    private static BrowserRetainedOwnerCalls Owner(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiV3PreflightResult>>? preflight = null,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiV3OperationStatus>>? execute = null,
        Func<ReadOnlyMemory<byte>, LocalApiV3OperationStatus>? status = null,
        Func<ReadOnlyMemory<byte>, LocalApiV3OperationStatus>? cancel = null,
        Func<string, string, string, long, string, CancellationToken, Task<BrowserRetainedOutputLease>>? acquire = null,
        Action? dispose = null) => new((_, _) => throw new InvalidOperationException("legacy preflight forbidden"),
        (_, _) => throw new InvalidOperationException("legacy execute forbidden"), _ => throw new InvalidOperationException("legacy status forbidden"),
        _ => throw new InvalidOperationException("legacy cancel forbidden"), dispose ?? (() => { }),
        PreflightV3: preflight ?? ((_, _) => Task.FromResult(Prepared(Bytes.Length, Hash))),
        ExecuteV3: execute ?? ((_, _) => Task.FromResult(Status(LocalApiOperationStage.Completed, true))),
        StatusV3: status ?? (_ => Status()), CancelV3: cancel ?? (_ => Status(LocalApiOperationStage.CancellationRequested)),
        AcquireDiagnosticOutput: acquire ?? ((_, _, _, _, _, _) => Task.FromResult(Lease(Bytes))));
    private static BrowserRetainedOutputLease Lease(byte[] bytes, Action? disposed = null, LocalApiV3ArchiveDescriptor? descriptor = null, Stream? stream = null)
    {
        stream ??= new MemoryStream(bytes, writable: false);
        return new(descriptor ?? Descriptor(), stream, new Release(() => { stream.Dispose(); disposed?.Invoke(); }));
    }

    [Fact]
    public async Task CapabilityAndDrainAreInertAndOnlyConcreteV3DelegatesAdvertiseSupport()
    {
        var factories = 0;
        using var operations = new BrowserRetainedOperations(() => throw new Exception("store forbidden"),
            () => { factories++; return Owner(); }, supportsV3: true);
        Assert.Contains(Version, operations.Capability.ContractVersions!); Assert.Equal("configured_not_opened", operations.Capability.Code);
        Assert.Equal(0, factories); await operations.DrainAsync(); Assert.Equal(0, factories);
        Assert.Equal("retained_owner_closed", Assert.Throws<BrowserRetainedException>(() => operations.GetV3Status(Binding())).Code);
        using var legacy = new BrowserRetainedOperations(() => throw new Exception("store forbidden"),
            () => { factories++; return Owner() with { StatusV3 = null }; });
        Assert.DoesNotContain(Version, legacy.Capability.ContractVersions!);
        Assert.Equal("retained_contract_unavailable", Assert.Throws<BrowserRetainedException>(() => legacy.GetV3Status(Binding())).Code);
        Assert.Equal(0, factories);
    }

    [Fact]
    public void UnsupportedAndMissingConfigurationRejectWithoutOptionalOwnerOrStoreActivation()
    {
        using var unsupported = new BrowserRetainedOperations(() => throw new Exception("store forbidden"), null,
            _ => throw new Exception("environment forbidden on unsupported platform"), platformSupported: false);
        Assert.Equal("retained_platform_unsupported", Assert.Throws<BrowserRetainedException>(() => unsupported.GetV3Status(Binding())).Code);
        using var missing = new BrowserRetainedOperations(() => throw new Exception("store forbidden"), null, _ => null, platformSupported: true);
        Assert.Equal("retained_configuration_missing", Assert.Throws<BrowserRetainedException>(() => missing.GetV3Status(Binding())).Code);
        using var configured = new BrowserRetainedOperations(() => throw new Exception("store forbidden"), null,
            name => name == LocalApiStateSettings.StateDirectoryEnvironmentVariable ? @"C:\synthetic-state-not-opened" : new string('s', 32), platformSupported: true);
        Assert.Contains(Version, configured.Capability.ContractVersions!); Assert.Equal("configured_not_opened", configured.Capability.Code);
        var owner = typeof(BrowserRetainedOperations).GetField("_owner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(configured)!;
        Assert.False((bool)owner.GetType().GetProperty("IsValueCreated")!.GetValue(owner)!);
    }

    [Theory]
    [InlineData("outputPath", "\"C:/private/host\"")]
    [InlineData("installOptions", "{}")]
    [InlineData("verification", "\"exact\"")]
    [InlineData("uid", "10234")]
    [InlineData("pid", "123")]
    [InlineData("filter", "\"all\"")]
    [InlineData("confirmed", "true")]
    [InlineData("command", "\"apk.diagnose\"")]
    public async Task ClosedPreflightRejectsExtraOrDuplicateAuthorityBeforeAnyActivation(string name, string json)
    {
        var opened = false;
        using var operations = new BrowserRetainedOperations(() => throw new Exception("store forbidden"), () => { opened = true; return Owner(); }, supportsV3: true);
        var body = Encoding.UTF8.GetString(Preflight());
        var exception = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PreflightV3Async(Encoding.UTF8.GetBytes(body[..^1] + ",\"" + name + "\":" + json + "}")));
        Assert.Equal(false, exception.Dispatched); Assert.False(opened);
    }

    [Theory]
    [InlineData("contractVersion", "\"questionable.file_manager.local_api.v2\"")]
    [InlineData("command", "\"apk.launch-diagnose\"")]
    [InlineData("serial", "\"QUEST-01\\n\"")]
    [InlineData("sizeBytes", "0")]
    [InlineData("sizeBytes", "1073741825")]
    [InlineData("artifactId", "\"00000000000000000000000000000000\"")]
    [InlineData("sha256", "\"ABC\"")]
    public void ClosedPreflightRejectsCrossedVersionSerialAndBounds(string field, string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Preflight())!; node[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        Assert.Equal(false, Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParseV3Preflight(JsonSerializer.SerializeToUtf8Bytes(node))).Dispatched);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void ExpiryAndBodyBoundsRejectBeforeOwner(int seconds)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Preflight())!; node["expiresInSeconds"] = seconds;
        Assert.Equal("expiry_invalid", Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParseV3Preflight(JsonSerializer.SerializeToUtf8Bytes(node))).Code);
        Assert.Equal(413, Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParseV3Preflight(new byte[4097])).StatusCode);
    }

    [Fact]
    public async Task SourceLeaseAndSharedGateRemainUntilNativePreflightAcknowledgesItsCopy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var source = await store.StageAsync("base.apk", new MemoryStream(Bytes));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<LocalApiV3PreflightResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new BrowserOperationGate(); var disposed = false;
        using var operations = new BrowserRetainedOperations(() => store, Owner(preflight: (body, _) =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(new[] { "contractVersion", "command", "apkPath", "serial", "expiresInSeconds" }, json.RootElement.EnumerateObject().Select(item => item.Name));
            Assert.True(File.Exists(json.RootElement.GetProperty("apkPath").GetString())); entered.SetResult(); return acknowledged.Task;
        }, dispose: () => disposed = true), gate.TryAcquire);
        var pending = operations.PreflightV3Async(Preflight(source));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(gate.IsBusy); Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => store.RemoveAsync(source.Id))).Code);
            var drain = operations.DrainAsync(); Assert.False(drain.IsCompleted); Assert.False(disposed);
            Assert.Equal("retained_operation_in_use", Assert.Throws<BrowserRetainedException>(operations.Dispose).Code);
            acknowledged.SetResult(Prepared(source.SizeBytes, source.Sha256));
            var result = await pending; result.Validate(); await drain;
            Assert.False(gate.IsBusy); Assert.False(disposed); operations.Dispose(); Assert.True(disposed);
            Assert.DoesNotContain("apkPath", JsonSerializer.Serialize(result, Json));
            await store.RemoveAsync(source.Id);
        }
        finally { acknowledged.TrySetResult(Prepared(source.SizeBytes, source.Sha256)); await pending; }
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("size")]
    [InlineData("serial")]
    [InlineData("version")]
    [InlineData("intent")]
    public async Task CrossedPreflightNeverBecomesAReviewedBrowserReceipt(string field)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var source = await store.StageAsync("base.apk", new MemoryStream(Bytes));
        var value = Prepared(source.SizeBytes, source.Sha256);
        value = field switch
        {
            "sha" => value with { Artifact = value.Artifact with { Sha256 = new string('f', 64) } },
            "size" => value with { Artifact = value.Artifact with { SizeBytes = source.SizeBytes + 1 } },
            "serial" => value with { Serial = "OTHER" }, "version" => value with { ContractVersion = LocalApiV2Contract.Version },
            _ => value with { OutputIntent = value.OutputIntent with { PlannedLeafDigest = new string('f', 64) } }
        };
        using var operations = new BrowserRetainedOperations(() => store, Owner(preflight: (_, _) => Task.FromResult(value)));
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PreflightV3Async(Preflight(source)));
        Assert.Null(error.Dispatched); Assert.DoesNotContain("private", error.Message);
    }

    [Theory]
    [InlineData("generic.zip")]
    [InlineData("generic.txt")]
    public async Task NonApkUploadLabelRejectsBeforeNativeActivationWithDefinitivePredispatch(string fileName)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var source = await store.StageAsync(fileName, new MemoryStream(Bytes)); var nativeOpened = false;
        using var operations = new BrowserRetainedOperations(() => store, () => { nativeOpened = true; return Owner(); }, supportsV3: true);
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PreflightV3Async(Preflight(source)));
        Assert.Equal("artifact_filename_invalid", error.Code); Assert.Equal(false, error.Dispatched); Assert.False(nativeOpened);
        await store.RemoveAsync(source.Id);
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("expiry")]
    [InlineData("digest")]
    [InlineData("version")]
    [InlineData("command")]
    [InlineData("stage")]
    [InlineData("consumed")]
    [InlineData("limits")]
    [InlineData("error")]
    public async Task ExecuteRejectsCrossedNativeMetadataWithoutRawOrFalseDispatchClaims(string field)
    {
        var response = Status(LocalApiOperationStage.Completed, true);
        response = field switch
        {
            "serial" => response with { Serial = "OTHER" }, "expiry" => response with { ExpiresAt = Expires.AddSeconds(1) },
            "digest" => response with { CommandDigest = new string('b', 64) }, "version" => response with { ContractVersion = LocalApiV2Contract.Version },
            "command" => response with { Command = "apk.deploy" }, "stage" => response with { Stage = (LocalApiOperationStage)999 },
            "consumed" => response with { Consumed = false }, "error" => response with { ErrorCode = "PRIVATE_HOST_PATH" },
            _ => response with { Output = response.Output with { Intent = response.Output.Intent with { Limits = response.Output.Intent.Limits with { MaximumArchiveBytes = 999 } } } }
        };
        using var operations = new BrowserRetainedOperations(() => throw new Exception("source must not be reacquired"), Owner(execute: (_, _) => Task.FromResult(response)));
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.ExecuteV3Async(Binding()));
        Assert.Equal("retained_operation_binding_invalid", error.Code); Assert.Null(error.Dispatched); Assert.DoesNotContain("PRIVATE_HOST_PATH", error.Message);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("execute")]
    [InlineData("cancel")]
    public async Task LifecycleBodiesAreOriginalOnlyAndNativeCancelReplyMustKeepScope(string action)
    {
        void Check(ReadOnlyMemory<byte> body)
        {
            using var parsed = JsonDocument.Parse(body);
            Assert.Equal(new[] { "contractVersion", "operationId", "commandDigest" }, parsed.RootElement.EnumerateObject().Select(item => item.Name));
            Assert.Equal(Operation, parsed.RootElement.GetProperty("operationId").GetString());
        }
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no source/current target"), Owner(status: body => { Check(body); return Status(); },
            execute: (body, _) => { Check(body); return Task.FromResult(Status(LocalApiOperationStage.OutcomeUnknownRecoveryRequired)); },
            cancel: body => { Check(body); return Status(LocalApiOperationStage.CancellationRequested) with { Serial = "OTHER" }; }));
        if (action == "status") Assert.Equal(Operation, operations.GetV3Status(Binding()).OperationId);
        else if (action == "execute") Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, (await operations.ExecuteV3Async(Binding())).Stage);
        else Assert.Equal("retained_operation_binding_invalid", Assert.Throws<BrowserRetainedException>(() => operations.CancelV3(Binding())).Code);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("execute")]
    [InlineData("cancel")]
    [InlineData("output")]
    public async Task ExtraCurrentTargetOrSourceFieldsNeverReachNativeLifecycle(string action)
    {
        var opened = false;
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"), () => { opened = true; return Owner(); }, supportsV3: true);
        var body = System.Text.Json.Nodes.JsonNode.Parse(action == "output" ? OutputBody() : Binding())!; body["serial"] = "CURRENT_OTHER_TARGET";
        var context = Context(JsonSerializer.SerializeToUtf8Bytes(body)); await BrowserRetainedRoutes.InvokeV3Async(context, operations, action);
        Assert.Equal(400, context.Response.StatusCode); Assert.False(opened);
        using var response = JsonDocument.Parse(Response(context)); Assert.False(response.RootElement.GetProperty("dispatched").GetBoolean());
    }

    [Fact]
    public async Task ExplicitExecutionIgnoresPageAndLifetimeTokensAndDrainJoinsActualOwnerAck()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<LocalApiV3OperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new BrowserOperationGate(); var current = Status(); var calls = 0; var disposed = false; CancellationToken nativeToken = default;
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no upload"), Owner(execute: (_, token) =>
        { calls++; nativeToken = token; current = Status(LocalApiOperationStage.Running); entered.SetResult(); return acknowledged.Task; }, status: _ => current,
            cancel: _ => current = Status(LocalApiOperationStage.CancellationRequested), dispose: () => disposed = true), gate.TryAcquire);
        using var stopped = new CancellationTokenSource();
        var pending = operations.ExecuteV3Async(Binding(), stopped.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stopped.Cancel();
            Assert.False(nativeToken.CanBeCanceled); Assert.True(gate.IsBusy);
            Assert.Equal(LocalApiOperationStage.Running, operations.GetV3Status(Binding()).Stage);
            Assert.Equal(LocalApiOperationStage.CancellationRequested, operations.CancelV3(Binding()).Stage);
            Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.ExecuteV3Async(Binding()))).Code);
            var drain = operations.DrainAsync(); Assert.False(drain.IsCompleted); Assert.False(disposed);
            Assert.Equal("retained_owner_closed", Assert.Throws<BrowserRetainedException>(() => operations.GetV3Status(Binding())).Code);
            acknowledged.SetResult(Status(LocalApiOperationStage.OutcomeUnknownRecoveryRequired));
            Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, (await pending).Stage); await drain;
            Assert.Equal(1, calls); Assert.False(gate.IsBusy); Assert.False(disposed); operations.Dispose(); Assert.True(disposed);
        }
        finally { acknowledged.TrySetResult(Status(LocalApiOperationStage.OutcomeUnknownRecoveryRequired)); await pending; }
    }

    [Fact]
    public async Task PublishedOutputCopiesExactBytesIntoIndependentTransientCustodyWithoutCapture()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var nativeReleased = false; var acquireCalls = 0;
        using var operations = new BrowserRetainedOperations(() => store, Owner(status: _ => Status(LocalApiOperationStage.Completed, true),
            acquire: (id, digest, output, size, hash, _) =>
            {
                acquireCalls++; Assert.Equal(Operation, id); Assert.Equal(Digest, digest); Assert.Equal(Output, output); Assert.Equal(Bytes.Length, size); Assert.Equal(Hash, hash);
                return Task.FromResult(Lease(Bytes, () => nativeReleased = true));
            }, execute: (_, _) => throw new Exception("copy cannot dispatch capture")));
        var result = await operations.PrepareDiagnosticOutputAsync(OutputBody());
        Assert.True(nativeReleased); Assert.Equal(1, acquireCalls);
        Assert.Equal("staged", result.State); Assert.Equal("localOnly", result.Scope); Assert.False(result.DeviceDispatched);
        Assert.Equal("apk-diagnostics.zip", result.Artifact.FileName); Assert.Equal(Hash, result.Artifact.Sha256); Assert.Equal(Bytes.Length, result.Artifact.SizeBytes);
        Assert.Equal(new BrowserDiagnosticOutputReference(Version, Operation, Digest, Output, Bytes.Length, Hash), result.Source);
        await using (var lease = await store.AcquireAsync(result.Artifact.Id, Hash, Bytes.Length))
            Assert.Equal(Hash, Convert.ToHexString(await SHA256.HashDataAsync(lease.Stream)).ToLowerInvariant());
        await store.RemoveAsync(result.Artifact.Id);
        var again = await operations.PrepareDiagnosticOutputAsync(OutputBody()); Assert.Equal(2, acquireCalls); Assert.NotEqual(result.Artifact.Id, again.Artifact.Id);
        Assert.DoesNotContain("path", JsonSerializer.Serialize(again, Json), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("missing")]
    [InlineData("pending")]
    public async Task UnavailableOutputNeverAcquiresOrCaptures(string state)
    {
        var value = Status(state == "unknown" ? LocalApiOperationStage.OutcomeUnknownRecoveryRequired : state == "pending" ? LocalApiOperationStage.Running : LocalApiOperationStage.Completed);
        if (state == "expired") value = value with { ErrorCode = "operation_expired" };
        using var operations = new BrowserRetainedOperations(() => throw new Exception("store forbidden"), Owner(status: _ => value,
            acquire: (_, _, _, _, _, _) => throw new Exception("lease forbidden")));
        Assert.Equal("diagnostic_output_unavailable", (await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PrepareDiagnosticOutputAsync(OutputBody()))).Code);
    }

    [Theory]
    [InlineData("bodySha")]
    [InlineData("bodySize")]
    [InlineData("bodyOutput")]
    [InlineData("leaseSha")]
    [InlineData("leaseLength")]
    [InlineData("bytes")]
    public async Task OutputCrossingsAndChangedBytesNeverProduceATransientReceipt(string field)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var released = false;
        using var operations = new BrowserRetainedOperations(() => store, Owner(status: _ => Status(LocalApiOperationStage.Completed, true),
            acquire: (_, _, _, _, _, _) => Task.FromResult(Lease(field == "bytes" ? Enumerable.Repeat((byte)1, Bytes.Length).ToArray() :
                field == "leaseLength" ? [1] : Bytes, () => released = true, field == "leaseSha" ? Descriptor() with { Sha256 = new string('f', 64) } : null))));
        var body = System.Text.Json.Nodes.JsonNode.Parse(OutputBody())!;
        if (field == "bodySha") body["sha256"] = new string('f', 64);
        if (field == "bodySize") body["sizeBytes"] = Bytes.Length + 1;
        if (field == "bodyOutput") body["outputId"] = "diag-" + new string('4', 32);
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PrepareDiagnosticOutputAsync(JsonSerializer.SerializeToUtf8Bytes(body)));
        Assert.Null(error.Dispatched); Assert.Equal(field.StartsWith("body", StringComparison.Ordinal) ? false : true, released);
        Assert.Equal(0, await store.CleanupAsync());
    }

    [Fact]
    public async Task DrainIncludesLateNativeLeaseReleaseAfterIndependentStoreAcknowledgement()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var gate = new BrowserOperationGate(); var ownerDisposed = false;
        var releasing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operations = new BrowserRetainedOperations(() => store, Owner(status: _ => Status(LocalApiOperationStage.Completed, true),
            acquire: (_, _, _, _, _, _) => Task.FromResult(Lease(Bytes, () =>
            { releasing.TrySetResult(); releaseAck.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); })), dispose: () => ownerDisposed = true), gate.TryAcquire);
        var copying = Task.Run(() => operations.PrepareDiagnosticOutputAsync(OutputBody()));
        try
        {
            await releasing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(gate.IsBusy); Assert.False(copying.IsCompleted);
            var drain = operations.DrainAsync(); Assert.False(drain.IsCompleted); Assert.False(ownerDisposed);
            Assert.Equal("retained_operation_in_use", Assert.Throws<BrowserRetainedException>(operations.Dispose).Code);
            releaseAck.SetResult(); var result = await copying; await drain;
            await using var staged = await store.AcquireAsync(result.Artifact.Id, Hash, Bytes.Length);
            Assert.Equal(Hash, Convert.ToHexString(await SHA256.HashDataAsync(staged.Stream)).ToLowerInvariant());
            Assert.False(gate.IsBusy); Assert.False(ownerDisposed); operations.Dispose(); Assert.True(ownerDisposed);
        }
        finally { releaseAck.TrySetResult(); await copying; }
    }

    [Fact]
    public async Task CopyReadFailureReleasesNativeLeaseAndDrainWithoutPublishingOrReexecuting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var gate = new BrowserOperationGate(); var released = false;
        using var operations = new BrowserRetainedOperations(() => store, Owner(status: _ => Status(LocalApiOperationStage.Completed, true),
            acquire: (_, _, _, _, _, _) => Task.FromResult(Lease(Bytes, () => released = true, stream: new BrokenReadStream(Bytes)))), gate.TryAcquire);
        var failure = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PrepareDiagnosticOutputAsync(OutputBody()));
        Assert.Equal("retained_owner_failure", failure.Code); Assert.Null(failure.Dispatched);
        Assert.True(released); Assert.False(gate.IsBusy); Assert.Equal(0, await store.CleanupAsync()); await operations.DrainAsync();
    }

    [Fact]
    public async Task DrainIncludesExistingLegacyCallsAndNeverOpensAnIdleOwner()
    {
        var ack = new TaskCompletionSource<LocalApiOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var disposed = false;
        LocalApiOperationStatus Legacy(LocalApiOperationStage stage) => new(LocalApiContract.Version, Operation, "apk.inspect", Digest, Expires, stage, null, null, null, null);
        var owner = new BrowserRetainedOwnerCalls((_, _) => throw new Exception("no preflight"), (_, _) => { entered.SetResult(); return ack.Task; },
            _ => Legacy(LocalApiOperationStage.Preflighted), _ => Legacy(LocalApiOperationStage.CancellationRequested), () => disposed = true);
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no source"), owner);
        var pending = operations.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new { operationId = Operation, commandDigest = Digest }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var drain = operations.DrainAsync();
            Assert.False(drain.IsCompleted); Assert.False(disposed); ack.SetResult(Legacy(LocalApiOperationStage.Completed));
            await pending; await drain; Assert.False(disposed); operations.Dispose(); Assert.True(disposed);
        }
        finally { ack.TrySetResult(Legacy(LocalApiOperationStage.Completed)); await pending; }
    }

    [Fact]
    public async Task FiveV3RoutesAreClosedAndMappingDoesNotActivateAnyOwner()
    {
        var opened = false;
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"), () => { opened = true; return Owner(); }, supportsV3: true);
        await using var app = WebApplication.CreateBuilder().Build(); BrowserRetainedRoutes.Map(app, operations);
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!).Where(path => path.StartsWith("/browser/v3/", StringComparison.Ordinal)).Order().ToArray();
        Assert.Equal(new[] { "cancel", "execute", "output", "preflight", "status" }.Select(action => "/browser/v3/retained/" + action), routes);
        Assert.False(opened);
    }

    [Theory]
    [InlineData("content-type", 415, "content_type_invalid")]
    [InlineData("content-encoding", 415, "content_encoding_invalid")]
    [InlineData("length", 413, "body_size_invalid")]
    [InlineData("stream-size", 413, "body_size_invalid")]
    [InlineData("case", 400, "fields_invalid")]
    [InlineData("duplicate", 400, "duplicate_field")]
    [InlineData("version", 400, "contract_unsupported")]
    [InlineData("timeout", 408, "request_body_timeout")]
    public async Task RouteFrontdoorErrorsAreNoStorePredispatchAndNeverActivateOwner(string kind, int expectedStatus, string expectedCode)
    {
        var opened = false;
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"), () => { opened = true; return Owner(); }, supportsV3: true);
        var context = Context(Binding());
        if (kind == "content-type") context.Request.ContentType = "text/plain";
        if (kind == "content-encoding") context.Request.Headers.ContentEncoding = "gzip";
        if (kind == "length") context.Request.ContentLength = 4097;
        if (kind == "stream-size") { context.Request.ContentLength = null; context.Request.Body = new MemoryStream(new byte[4097]); }
        if (kind == "case") context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Binding()).Replace("operationId", "OperationId", StringComparison.Ordinal)));
        if (kind == "duplicate") context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Binding())[..^1] + ",\"operationId\":\"" + Operation + "\"}"));
        if (kind == "version") context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Binding()).Replace(Version, LocalApiV2Contract.Version, StringComparison.Ordinal)));
        if (kind == "timeout") context.Request.Body = new DeadlineBodyStream();
        await BrowserRetainedRoutes.InvokeV3Async(context, operations, "status");
        Assert.Equal(expectedStatus, context.Response.StatusCode); Assert.Equal("no-store", context.Response.Headers.CacheControl);
        using var response = JsonDocument.Parse(Response(context));
        Assert.Equal(expectedCode, response.RootElement.GetProperty("error").GetString()); Assert.False(response.RootElement.GetProperty("dispatched").GetBoolean());
        Assert.False(opened);
    }

    [Fact]
    public async Task RoutePageLossCannotCancelAdmittedExecutionOrReleaseGateBeforeCamelCaseAck()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ack = new TaskCompletionSource<LocalApiV3OperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new BrowserOperationGate(); var nativeToken = CancellationToken.None;
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"), Owner(execute: (_, token) =>
        { nativeToken = token; entered.SetResult(); return ack.Task; }), gate.TryAcquire);
        using var page = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
        var context = Context(Binding()); context.RequestAborted = page.Token;
        var pending = BrowserRetainedRoutes.InvokeV3Async(context, operations, "execute", lifetime.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); page.Cancel(); lifetime.Cancel();
            Assert.True(gate.IsBusy); Assert.False(nativeToken.CanBeCanceled); Assert.False(pending.IsCompleted);
            ack.SetResult(Status(LocalApiOperationStage.OutcomeUnknownRecoveryRequired)); await pending;
            // The disconnected HTTP page need not receive a body. Its lost reply
            // cannot shorten Native acknowledgement or the shared gate lifetime.
            Assert.False(gate.IsBusy); await operations.DrainAsync();
        }
        finally { ack.TrySetResult(Status(LocalApiOperationStage.OutcomeUnknownRecoveryRequired)); await pending; }
    }

    [Fact]
    public async Task LiveStatusRouteReturnsOnlyCamelCaseSafeNativeDto()
    {
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"), Owner(status: _ => Status(LocalApiOperationStage.OutcomeUnknownRecoveryRequired)));
        var context = Context(Binding()); await BrowserRetainedRoutes.InvokeV3Async(context, operations, "status");
        using var response = JsonDocument.Parse(Response(context));
        Assert.Equal("outcomeUnknownRecoveryRequired", response.RootElement.GetProperty("stage").GetString());
        Assert.Equal(Version, response.RootElement.GetProperty("contractVersion").GetString());
        Assert.False(response.RootElement.TryGetProperty("result", out _)); Assert.False(response.RootElement.TryGetProperty("mutation", out _));
    }

    [Fact]
    public async Task NativeExceptionsAreFixedAndNeverClaimDefinitivePredispatchAfterExecute()
    {
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no source"), Owner(execute: (_, _) =>
            throw new LocalApiException("PRIVATE_NATIVE_ERROR", "C:/private/native/state and private captured logs")));
        var context = Context(Binding()); await BrowserRetainedRoutes.InvokeV3Async(context, operations, "execute");
        Assert.Equal(503, context.Response.StatusCode); Assert.DoesNotContain("PRIVATE", Response(context)); Assert.DoesNotContain("private", Response(context));
        using var response = JsonDocument.Parse(Response(context)); Assert.Equal("retained_owner_failure", response.RootElement.GetProperty("error").GetString());
        Assert.False(response.RootElement.TryGetProperty("dispatched", out _));
    }

    private static DefaultHttpContext Context(byte[] body)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.ContentType = "application/json"; context.Request.ContentLength = body.Length; context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream(); return context;
    }
    private static string Response(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    private sealed class BrokenReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _source = new(bytes, writable: false);
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => true;
        public override long Length => _source.Length; public override long Position { get => _source.Position; set => _source.Position = value; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("PRIVATE_READ_ERROR");
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("PRIVATE_READ_ERROR");
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _source.Dispose(); base.Dispose(disposing); }
    }
    private sealed class DeadlineBodyStream : Stream
    {
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Assert.True(cancellationToken.CanBeCanceled); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Release(Action release) : IDisposable
    { private Action? _release = release; public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(); }
}
