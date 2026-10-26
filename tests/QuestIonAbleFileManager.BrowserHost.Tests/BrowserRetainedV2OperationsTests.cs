using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserRetainedV2OperationsTests
{
    internal const string OperationId = "api-00000000000040008000000000000001";
    internal const string LegacyId = "api-00000000000040008000000000000002";
    internal static readonly string Digest = new('a', 64);
    internal static byte[] Bound(string version = LocalApiV2Contract.Version, string id = OperationId, string? digest = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new { contractVersion = version, operationId = id, commandDigest = digest ?? Digest });
    internal static LocalApiOperationStatus Status(LocalApiOperationStage stage, string version = LocalApiV2Contract.Version, string id = OperationId) =>
        new(version, id, version == LocalApiV2Contract.Version ? "apk.deploy" : "apk.install-inspected", Digest,
            DateTimeOffset.UtcNow.AddMinutes(2), stage, null, null, null, null);
    internal static BrowserRetainedOwnerCalls Owner(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiPreflightResult>>? preflight = null,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiOperationStatus>>? execute = null,
        Func<ReadOnlyMemory<byte>, LocalApiOperationStatus>? status = null,
        Func<ReadOnlyMemory<byte>, LocalApiOperationStatus>? cancel = null) => new(
        (_, _) => throw new InvalidOperationException("v1 preflight must not handle v2"),
        (_, _) => throw new InvalidOperationException("v1 execute must not handle v2"),
        _ => Status(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, LocalApiContract.Version, LegacyId),
        _ => throw new InvalidOperationException("v1 cancel must not handle v2"), () => { },
        preflight ?? ((_, _) => throw new InvalidOperationException("unexpected v2 preflight")),
        execute ?? ((_, _) => Task.FromResult(Status(LocalApiOperationStage.Completed))),
        status ?? (_ => Status(LocalApiOperationStage.Preflighted)),
        cancel ?? (_ => Status(LocalApiOperationStage.CancellationRequested)));

    private static byte[] Preflight(string extra = "", string version = LocalApiV2Contract.Version, string command = "apk.deploy", BrowserArtifactReceipt? artifact = null) =>
        Encoding.UTF8.GetBytes($$"""{"contractVersion":"{{version}}","artifactId":"{{artifact?.Id ?? "00000000000040008000000000000003"}}","sha256":"{{artifact?.Sha256 ?? Digest}}","sizeBytes":{{artifact?.SizeBytes ?? 3}},"command":"{{command}}","serial":"fixture-target"{{extra}}} """);

    [Fact]
    public void V2PreflightRequiresExplicitVersionExactDeployAndOnlyRestrictedOptions()
    {
        var parsed = BrowserRetainedOperations.ParseV2Preflight(Preflight());
        Assert.Equal(LocalApiV2Contract.Version, parsed.ContractVersion); Assert.Equal("apk.deploy", parsed.Command);
        Assert.Equal("fixture-target", parsed.Serial); Assert.Null(parsed.InstallOptions); Assert.Null(parsed.ExpiresInSeconds);
        var options = BrowserRetainedOperations.ParseV2Preflight(Preflight(",\"installOptions\":{\"replaceExisting\":false,\"grantRuntimePermissions\":true},\"expiresInSeconds\":300"));
        Assert.False(options.InstallOptions!.ReplaceExisting); Assert.True(options.InstallOptions.GrantRuntimePermissions);
        Assert.False(options.InstallOptions.AllowDowngrade); Assert.False(options.InstallOptions.AllowTestPackages); Assert.Equal(300, options.ExpiresInSeconds);
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParsePreflight(Preflight()));
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParseV2Preflight(BrowserRetainedOperationsTests.PreflightBody("apk.install-inspected", "fixture-target")));
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParseV2Preflight(new byte[4097]));
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParseV2Preflight(Encoding.UTF8.GetBytes("{\"nested\":{\"nested\":{\"nested\":{\"nested\":{}}}}}")));
        var ceiling = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Preflight()).Replace("\"sizeBytes\":3", "\"sizeBytes\":1073741824", StringComparison.Ordinal));
        Assert.Equal(1073741824, BrowserRetainedOperations.ParseV2Preflight(ceiling).SizeBytes);
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParseV2Preflight(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ceiling).Replace("1073741824", "1073741825", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData(",\"path\":\"private.apk\"")]
    [InlineData(",\"contractVersion\":\"questionable.file_manager.local_api.v2\"")]
    [InlineData(",\"serial\":null")]
    [InlineData(",\"serial\":\"other-target\"")]
    [InlineData(",\"installOptions\":{\"replaceExisting\":true,\"grantRuntimePermissions\":false,\"allowDowngrade\":true}")]
    [InlineData(",\"installOptions\":{\"replaceExisting\":true,\"grantRuntimePermissions\":false,\"allowTestPackages\":false}")]
    [InlineData(",\"installOptions\":{\"replaceExisting\":true,\"replaceExisting\":false,\"grantRuntimePermissions\":false}")]
    [InlineData(",\"expiresInSeconds\":301")]
    [InlineData(",\"expiresInSeconds\":0")]
    [InlineData(",\"ContractVersion\":\"questionable.file_manager.local_api.v2\"")]
    public void V2PreflightRejectsExtrasDuplicatesOptionsAndExpiry(string extra) =>
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParseV2Preflight(Preflight(extra)));

    [Theory]
    [InlineData(LocalApiContract.Version, "apk.deploy")]
    [InlineData("questionable.file_manager.local_api.V2", "apk.deploy")]
    [InlineData(LocalApiV2Contract.Version, "apk.inspect")]
    [InlineData(LocalApiV2Contract.Version, "apk.install-inspected")]
    [InlineData(LocalApiV2Contract.Version, "app.launch-resolved")]
    [InlineData(LocalApiV2Contract.Version, "runtime.observe")]
    public void V2NeverInfersVersionsOrWidensItsCommandSet(string version, string command) =>
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParseV2Preflight(Preflight(version: version, command: command)));

    [Fact]
    public async Task V2PreflightHoldsExactSourceUntilBoundOwnerCommitAndReturnsAcknowledgedRequestOptions()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var artifact = await store.StageAsync("fixture.apk", new MemoryStream([1, 2, 3]));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource<LocalApiPreflightResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new BrowserOperationGate(); var opens = 0;
        using var operations = new BrowserRetainedOperations(() => store, () =>
        {
            opens++; return Owner(preflight: async (body, _) =>
            {
                using var json = JsonDocument.Parse(body); var root = json.RootElement;
                Assert.Equal(LocalApiV2Contract.Version, root.GetProperty("contractVersion").GetString()); Assert.Equal("apk.deploy", root.GetProperty("command").GetString());
                Assert.Equal("fixture-target", root.GetProperty("serial").GetString());
                var path = root.GetProperty("apkPath").GetString()!;
                Assert.True(File.Exists(path)); Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
                var options = root.GetProperty("installOptions"); Assert.False(options.GetProperty("replaceExisting").GetBoolean());
                Assert.True(options.GetProperty("grantRuntimePermissions").GetBoolean()); Assert.False(options.GetProperty("allowDowngrade").GetBoolean());
                Assert.False(options.GetProperty("allowTestPackages").GetBoolean());
                started.SetResult(); return await committed.Task;
            });
        }, gate.TryAcquire);
        var pending = operations.PreflightV2Async(Preflight(",\"installOptions\":{\"replaceExisting\":false,\"grantRuntimePermissions\":true}", artifact: artifact));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(gate.IsBusy);
        try { Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => store.RemoveAsync(artifact.Id))).Code); }
        finally { committed.SetResult(Receipt(artifact)); }
        var result = await pending;
        Assert.Equal(LocalApiV2Contract.Version, result.ContractVersion); Assert.Equal("apk.deploy", result.Command);
        Assert.False(result.InstallOptions.ReplaceExisting); Assert.True(result.InstallOptions.GrantRuntimePermissions);
        Assert.Equal("retained://" + OperationId + "/base.apk", result.Artifact.Path);
        Assert.False(gate.IsBusy); Assert.Equal(1, opens);
        Assert.Equal([LocalApiContract.Version, LocalApiV2Contract.Version], operations.Capability.ContractVersions);
        await store.RemoveAsync(artifact.Id);
        var legacyBody = JsonSerializer.SerializeToUtf8Bytes(new { operationId = LegacyId, commandDigest = Digest });
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, operations.GetStatus(legacyBody).Stage); Assert.Equal(1, opens);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("command")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("path")]
    [InlineData("target")]
    [InlineData("not-ready")]
    [InlineData("id")]
    [InlineData("digest")]
    public async Task V2PreflightRejectsCrossedOwnerReceiptBeforePublishingApproval(string mismatch)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var artifact = await store.StageAsync("fixture.apk", new MemoryStream([1, 2, 3]));
        var good = Receipt(artifact);
        var crossed = mismatch switch
        {
            "version" => good with { ContractVersion = LocalApiContract.Version }, "command" => good with { Command = "apk.install-inspected" },
            "hash" => good with { Artifact = good.Artifact with { Sha256 = new string('b', 64) } }, "size" => good with { Artifact = good.Artifact with { SizeBytes = 4 } },
            "path" => good with { Artifact = good.Artifact with { Path = "private-host-source.apk" } }, "target" => good with { Target = new("other-target", "device", null, null) },
            "not-ready" => good with { Target = new("fixture-target", "offline", null, null) }, "id" => good with { OperationId = "invented" },
            "digest" => good with { CommandDigest = new string('A', 64) }, _ => throw new InvalidOperationException()
        };
        using var operations = new BrowserRetainedOperations(() => store, Owner(preflight: (_, _) => Task.FromResult(crossed)));
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PreflightV2Async(Preflight(artifact: artifact)));
        Assert.Equal("artifact_binding_mismatch", error.Code); Assert.Null(error.Dispatched); await store.RemoveAsync(artifact.Id);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("cancel")]
    [InlineData("execute")]
    public async Task V2BindingRequiresVersionAndRejectsCrossVersionOrDigestResponsesWithoutFallback(string action)
    {
        var executed = 0; var cancelled = 0;
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException("no source reacquisition"), Owner(
            execute: (_, _) => { executed++; return Task.FromResult(Status(LocalApiOperationStage.Completed)); },
            status: _ => Status(LocalApiOperationStage.Preflighted, LocalApiContract.Version), cancel: _ => { cancelled++; return Status(LocalApiOperationStage.Cancelled); }));
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => Invoke(operations, action, Bound()));
        Assert.Equal("operation_version_mismatch", error.Code); Assert.Null(error.Dispatched); Assert.Equal(0, executed); Assert.Equal(0, cancelled);
        Assert.Equal("contract_unsupported", (await Assert.ThrowsAsync<BrowserRetainedException>(() => Invoke(operations, action, Bound(LocalApiContract.Version)))).Code);
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new { operationId = OperationId, commandDigest = Digest });
        await Assert.ThrowsAsync<BrowserRetainedException>(() => Invoke(operations, action, legacy));
        await Assert.ThrowsAsync<BrowserRetainedException>(() => Invoke(operations, action, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Bound())[..^1] + ",\"contractVersion\":\"" + LocalApiV2Contract.Version + "\"}")));
        using var mismatch = new BrowserRetainedOperations(() => throw new InvalidOperationException(), Owner());
        Assert.Equal("digest_mismatch", (await Assert.ThrowsAsync<BrowserRetainedException>(() => Invoke(mismatch, action, Bound(digest: new string('b', 64))))).Code);
        await Assert.ThrowsAsync<BrowserRetainedException>(() => mismatch.ExecuteAsync(Bound()));
    }

    [Theory]
    [InlineData("execute")]
    [InlineData("cancel")]
    public async Task V2AlsoVerifiesThePostAdmissionAcknowledgementVersion(string action)
    {
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException(), Owner(
            execute: (_, _) => Task.FromResult(Status(LocalApiOperationStage.Completed, LocalApiContract.Version)),
            cancel: _ => Status(LocalApiOperationStage.Cancelled, LocalApiContract.Version)));
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => Invoke(operations, action, Bound()));
        Assert.Equal("operation_version_mismatch", error.Code); Assert.Null(error.Dispatched);
    }

    [Fact]
    public async Task V2UsesTheSharedGateAndNeverFallsBackWhenOptionalOwnerMethodsAreAbsent()
    {
        var calls = 0;
        using var busy = new BrowserRetainedOperations(() => throw new InvalidOperationException(), Owner(execute: (_, _) => { calls++; return Task.FromResult(Status(LocalApiOperationStage.Completed)); }), () => null);
        var rejected = await Assert.ThrowsAsync<BrowserRetainedException>(() => busy.ExecuteV2Async(Bound()));
        Assert.Equal("operation_busy", rejected.Code); Assert.Null(rejected.Dispatched); Assert.Equal(0, calls);
        using var legacy = new BrowserRetainedOperations(() => throw new InvalidOperationException(), Owner() with { PreflightV2 = null });
        Assert.Equal("retained_contract_unavailable", (await Assert.ThrowsAsync<BrowserRetainedException>(() => legacy.ExecuteV2Async(Bound()))).Code);
        Assert.Equal([LocalApiContract.Version], legacy.Capability.ContractVersions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyPreflightDoesNotOpenTheOwnerOrAcquireSourceCustody(bool v2)
    {
        var opened = false;
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException("no source acquisition"),
            () => { opened = true; throw new InvalidOperationException("busy requests must not initialize owner"); }, () => null);
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => v2 ? operations.PreflightV2Async(Preflight()) :
            (Task)operations.PreflightAsync(BrowserRetainedOperationsTests.PreflightBody("apk.install-inspected", "fixture-target")));
        Assert.Equal("operation_busy", error.Code); Assert.False(opened); Assert.Null(error.Dispatched);
    }

    internal static Task<LocalApiOperationStatus> Invoke(BrowserRetainedOperations operations, string action, byte[] body) => action switch
    { "execute" => operations.ExecuteV2Async(body), "status" => Task.FromResult(operations.GetV2Status(body)), "cancel" => Task.FromResult(operations.CancelV2(body)), _ => throw new InvalidOperationException() };
    private static LocalApiPreflightResult Receipt(BrowserArtifactReceipt artifact) => new(LocalApiV2Contract.Version, OperationId, "apk.deploy", Digest,
        DateTimeOffset.UtcNow.AddMinutes(2), new("retained://" + OperationId + "/base.apk", artifact.SizeBytes, artifact.Sha256, new("example.fixture", 1, "1", new string('c', 64), null)),
        new("fixture-target", "device", null, null));
}
