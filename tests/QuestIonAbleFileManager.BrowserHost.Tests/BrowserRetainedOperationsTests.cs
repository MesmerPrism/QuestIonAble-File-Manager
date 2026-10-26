using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserRetainedOperationsTests
{
    private const string OperationId = "api-00000000000040008000000000000001";
    private static readonly string Digest = new('a', 64);
    private static byte[] Bound(string? digest = null) => JsonSerializer.SerializeToUtf8Bytes(new { operationId = OperationId, commandDigest = digest ?? Digest });
    private static LocalApiOperationStatus Status(LocalApiOperationStage stage) => new(LocalApiContract.Version, OperationId,
        "apk.install-inspected", Digest, DateTimeOffset.UtcNow.AddMinutes(2), stage, null, null, null, null);
    private static BrowserRetainedOwnerCalls Owner(Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiOperationStatus>>? execute = null,
        Func<ReadOnlyMemory<byte>, LocalApiOperationStatus>? status = null, Func<ReadOnlyMemory<byte>, LocalApiOperationStatus>? cancel = null,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiPreflightResult>>? preflight = null) => new(
        preflight ?? ((_, _) => throw new InvalidOperationException("unexpected preflight")),
        execute ?? ((_, _) => Task.FromResult(Status(LocalApiOperationStage.Completed))),
        status ?? (_ => Status(LocalApiOperationStage.Preflighted)), cancel ?? (_ => Status(LocalApiOperationStage.CancellationRequested)), () => { });

    [Fact]
    public void CapabilityDoesNotCreateOwnerAndMissingConfigOrPlatformStayExplicit()
    {
        var reads = 0;
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException("no store"), null,
            _ => { reads++; return null; }, platformSupported: true);
        Assert.False(operations.Capability.Supported); Assert.Equal("retained_configuration_missing", operations.Capability.Code);
        Assert.True(reads > 0); Assert.Contains("QUESTIONABLE_FILE_MANAGER_API_STATE", operations.Capability.Alternative);
        using var unsupported = new BrowserRetainedOperations(() => throw new InvalidOperationException("no store"), null,
            _ => throw new InvalidOperationException("no environment read"), platformSupported: false);
        Assert.False(unsupported.Capability.Supported); Assert.Equal("retained_platform_unsupported", unsupported.Capability.Code);
    }

    [Theory]
    [InlineData("\"path\":\"host.apk\"")]
    [InlineData("\"allowDowngrade\":true")]
    [InlineData("\"command\":\"apk.inspect\"")]
    [InlineData("\"ContractVersion\":\"private\"")]
    public void ClosedPreflightRejectsExtraDuplicateAndAuthorityFields(string extra)
    {
        var valid = PreflightBody("apk.inspect", null);
        var body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid)[..^1] + "," + extra + "}");
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParsePreflight(body));
    }

    [Fact]
    public void PreflightOnlyProjectsExistingFourCommandsAndTwoInstallOptions()
    {
        foreach (var command in LocalApiContract.Commands)
        {
            var request = BrowserRetainedOperations.ParsePreflight(PreflightBody(command, command == "apk.inspect" ? null : "FIXTURE"));
            Assert.Equal(command, request.Command); Assert.Null(request.ExpiresInSeconds);
        }
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParsePreflight(PreflightBody("apk.install-inspected", null)));
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParsePreflight(PreflightBody("apk.inspect", "FIXTURE")));
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParsePreflight(PreflightBody("apk.install", "FIXTURE")));
        foreach (var bad in new[] { "{\"allowDowngrade\":true}", "{\"replaceExisting\":true,\"grantRuntimePermissions\":false,\"allowTestPackages\":true}", "{\"replaceExisting\":true,\"replaceExisting\":false,\"grantRuntimePermissions\":false}" })
            Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParsePreflight(PreflightBody("apk.install-inspected", "FIXTURE", bad)));
        var options = BrowserRetainedOperations.ParsePreflight(PreflightBody("apk.install-inspected", "FIXTURE", "{\"replaceExisting\":false,\"grantRuntimePermissions\":true}"));
        Assert.False(options.InstallOptions!.ReplaceExisting); Assert.True(options.InstallOptions.GrantRuntimePermissions);
        Assert.False(options.InstallOptions.AllowDowngrade); Assert.False(options.InstallOptions.AllowTestPackages);
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParsePreflight(PreflightBody("runtime.observe", "FIXTURE", expires: 301)));
        Assert.Equal(300, BrowserRetainedOperations.ParsePreflight(PreflightBody("runtime.observe", "FIXTURE", expires: 300)).ExpiresInSeconds);
        Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParsePreflight(new byte[4097]));
    }

    [Fact]
    public async Task ExecuteKeepsSharedGateUntilOwnerAcknowledgesAndStatusCancelRemainAvailable()
    {
        var completion = new TaskCompletionSource<LocalApiOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var held = false; var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException("execution must not reacquire upload"),
            Owner((body, _) => { calls++; using var parsed = JsonDocument.Parse(body); Assert.Equal(LocalApiContract.Version, parsed.RootElement.GetProperty("contractVersion").GetString()); observed.SetResult(); return completion.Task; }),
            () => { if (held) return null; held = true; return new Release(() => held = false); });
        var executing = operations.ExecuteAsync(Bound()); await observed.Task;
        Assert.True(held); Assert.False(executing.IsCompleted);
        Assert.Equal(LocalApiOperationStage.Preflighted, operations.GetStatus(Bound()).Stage);
        Assert.Equal(LocalApiOperationStage.CancellationRequested, operations.Cancel(Bound()).Stage);
        var duplicate = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.ExecuteAsync(Bound()));
        Assert.Equal("operation_consumed", duplicate.Code); Assert.Null(duplicate.Dispatched); Assert.Equal(1, calls); Assert.True(held);
        Assert.Throws<BrowserRetainedException>(() => operations.Dispose());
        completion.SetResult(Status(LocalApiOperationStage.OutcomeUnknownRecoveryRequired));
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, (await executing).Stage);
        Assert.False(held);
    }

    [Fact]
    public async Task ExistingConsumedStateAndDigestMismatchNeverExecuteOrClaimNotDispatched()
    {
        using var consumed = new BrowserRetainedOperations(() => throw new InvalidOperationException(), Owner(status: _ => Status(LocalApiOperationStage.Running)), () => null);
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => consumed.ExecuteAsync(Bound()));
        Assert.Equal("operation_consumed", error.Code); Assert.Null(error.Dispatched);
        Assert.Equal("digest_mismatch", Assert.Throws<BrowserRetainedException>(() => consumed.GetStatus(Bound(new string('b', 64)))).Code);
        Assert.Equal("digest_mismatch", Assert.Throws<BrowserRetainedException>(() => consumed.Cancel(Bound(new string('b', 64)))).Code);
        await Assert.ThrowsAsync<BrowserRetainedException>(() => consumed.ExecuteAsync(Encoding.UTF8.GetBytes("{\"operationId\":\"missing-digest\"}")));
    }

    [Fact]
    public void StatusAndCancelUseOnlyExistingCoreMetadataDtoAfterDigestVerification()
    {
        var statusCalls = 0; var cancelled = false;
        LocalApiOperationStatus Metadata(ReadOnlyMemory<byte> body)
        {
            statusCalls++; using var json = JsonDocument.Parse(body);
            Assert.Equal(["contractVersion", "operationId"], json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(LocalApiContract.Version, json.RootElement.GetProperty("contractVersion").GetString());
            Assert.Equal(OperationId, json.RootElement.GetProperty("operationId").GetString());
            return Status(LocalApiOperationStage.Running);
        }
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException(), Owner(status: Metadata,
            cancel: body => { cancelled = true; Metadata(body); return Status(LocalApiOperationStage.CancellationRequested); }));
        Assert.Equal(LocalApiOperationStage.Running, operations.GetStatus(Bound()).Stage);
        Assert.Equal(LocalApiOperationStage.CancellationRequested, operations.Cancel(Bound()).Stage);
        Assert.True(cancelled); Assert.Equal(3, statusCalls);
        var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Bound())[..^1] + ",\"commandDigest\":\"" + Digest + "\"}");
        Assert.Throws<BrowserRetainedException>(() => operations.GetStatus(duplicate));
        Assert.Throws<BrowserRetainedException>(() => operations.Cancel(Encoding.UTF8.GetBytes("{\"operationId\":\"" + OperationId + "\",\"commandDigest\":\"" + Digest + "\",\"serial\":\"other\"}")));
    }

    [Fact]
    public async Task OwnerConflictFailsClosedWithStableAlternativeAndNoFreshOwnerRetry()
    {
        var attempts = 0;
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException(), (Func<BrowserRetainedOwnerCalls>)(() =>
        {
            attempts++; throw new LocalApiException("state_in_use", "private-configured-path-never-project");
        }));
        var conflict = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.ExecuteAsync(Bound()));
        Assert.Equal("state_in_use", conflict.Code); Assert.Null(conflict.Dispatched);
        Assert.False(operations.Capability.Supported); Assert.Contains("same state", operations.Capability.Alternative);
        Assert.DoesNotContain("private-configured-path", JsonSerializer.Serialize(operations.Capability));
        await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.ExecuteAsync(Bound()));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task BusySharedGateNeverQueuesOrExecutesACommand()
    {
        var executeCalls = 0;
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException(), Owner((_, _) =>
        { executeCalls++; return Task.FromResult(Status(LocalApiOperationStage.Completed)); }), () => null);
        var rejected = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.ExecuteAsync(Bound()));
        Assert.Equal("operation_busy", rejected.Code); Assert.Null(rejected.Dispatched); Assert.Equal(0, executeCalls);
    }

    [Fact]
    public async Task PreflightHoldsSourceLeaseUntilOwnerHasCommittedItsIndependentCopy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("fixture.apk", new MemoryStream([1, 2, 3]));
        var committed = new TaskCompletionSource<LocalApiPreflightResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        using var operations = new BrowserRetainedOperations(() => store, Owner(preflight: async (body, _) =>
        {
            using var parsed = JsonDocument.Parse(body); var path = parsed.RootElement.GetProperty("apkPath").GetString()!;
            Assert.EndsWith(".apk", path); Assert.True(File.Exists(path)); Assert.Equal(0, await store.CleanupAsync());
            Assert.Equal(LocalApiContract.Version, parsed.RootElement.GetProperty("contractVersion").GetString());
            started.SetResult(); return await committed.Task;
        }), () => { held = true; return new Release(() => held = false); });
        var preflight = operations.PreflightAsync(JsonSerializer.SerializeToUtf8Bytes(new { artifactId = receipt.Id, sha256 = receipt.Sha256, sizeBytes = receipt.SizeBytes, command = "apk.inspect", serial = (string?)null }));
        await started.Task; Assert.True(held);
        Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => store.RemoveAsync(receipt.Id))).Code);
        committed.SetResult(new(LocalApiContract.Version, OperationId, "apk.inspect", Digest, DateTimeOffset.UtcNow.AddMinutes(2),
            new("retained://" + OperationId + "/base.apk", receipt.SizeBytes, receipt.Sha256, new("example.fixture", 1, "1", new string('c', 64), null)), null));
        var result = await preflight; Assert.StartsWith("retained://", result.Artifact.Path); Assert.False(held);
        await store.RemoveAsync(receipt.Id);
    }

    internal static byte[] PreflightBody(string command, string? serial, string? options = null, int? expires = null)
    {
        var body = JsonSerializer.Serialize(new { artifactId = "00000000000040008000000000000002", sha256 = new string('a', 64), sizeBytes = 3, command, serial });
        if (options is not null) body = body[..^1] + ",\"installOptions\":" + options + "}";
        if (expires is not null) body = body[..^1] + ",\"expiresInSeconds\":" + expires.Value + "}";
        return Encoding.UTF8.GetBytes(body);
    }
    private sealed class Release(Action action) : IDisposable { public void Dispose() => action(); }
}
