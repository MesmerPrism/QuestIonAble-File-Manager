using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectUploadTests
{
    private const string Action = "kiosk.direct.staging-upload";
    private const string Handle = "00000000-0000-4000-8000-000000000002";
    private static byte[] Body(string? handle = null, string? id = null, BrowserArtifactReceipt? artifact = null, string name = "my app (1)+@.apk") =>
        JsonSerializer.SerializeToUtf8Bytes(new { requestId = id ?? Guid.NewGuid().ToString("D"), action = Action, connectionId = handle ?? Handle,
            artifactId = artifact?.Id ?? new string('a', 32), sha256 = artifact?.Sha256 ?? new string('b', 64), sizeBytes = artifact?.SizeBytes ?? 4, name, confirmed = true });

    [Fact]
    public void UploadIsClosedConfirmedMutationWithNoCallerPath()
    {
        var parsed = BrowserDirectCommands.Parse(Body());
        Assert.Equal(Action, parsed.Action); Assert.Equal(Handle, parsed.ConnectionId); Assert.True(parsed.Mutation);
        Assert.False(parsed.Recovery); Assert.False(parsed.IsCommand); Assert.Null(parsed.Command); Assert.Null(parsed.Value);
        Assert.Equal(new string('a', 32), parsed.ArtifactId); Assert.Equal(new string('b', 64), parsed.ArtifactSha256); Assert.Equal(4, parsed.ArtifactSizeBytes);
        Assert.Equal("my app (1)+@.apk", parsed.StagedName);
    }

    [Theory]
    [InlineData("path", "\"C:/private\"")]
    [InlineData("serial", "\"OTHER\"")]
    [InlineData("command", "\"install\"")]
    [InlineData("overwrite", "true")]
    public void ExtraAuthorityIsRejected(string name, string value)
    {
        var body = Encoding.UTF8.GetString(Body());
        Assert.Equal("fields_invalid", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Encoding.UTF8.GetBytes(body[..^1] + ",\"" + name + "\":" + value + "}"))).Code);
    }

    [Theory]
    [InlineData("requestId", "\"FFFFFFFF-FFFF-4FFF-8FFF-FFFFFFFFFFFF\"", "request_id_invalid")]
    [InlineData("connectionId", "\"00000000-0000-0000-0000-000000000000\"", "request_id_invalid")]
    [InlineData("artifactId", "\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"", "artifact_id_invalid")]
    [InlineData("artifactId", "\"00000000000000000000000000000000\"", "artifact_id_invalid")]
    [InlineData("sha256", "\"bad\"", "artifact_hash_invalid")]
    [InlineData("sizeBytes", "0", "artifact_size_invalid")]
    [InlineData("sizeBytes", "-1", "artifact_size_invalid")]
    [InlineData("sizeBytes", "1073741825", "artifact_size_invalid")]
    [InlineData("sizeBytes", "1.5", "artifact_size_invalid")]
    [InlineData("confirmed", "false", "confirmation_required")]
    public void InvalidBoundFieldsFailClosed(string name, string value, string code)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Body())!; fields[name] = JsonSerializer.Deserialize<JsonElement>(value);
        Assert.Equal(code, Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields))).Code);
    }

    [Theory]
    [InlineData(" leading.apk")]
    [InlineData("trailing.apk ")]
    [InlineData("bad\n")]
    [InlineData("../bad.apk")]
    [InlineData("folder\\bad.apk")]
    [InlineData("café.apk")]
    public void RemoteNameCannotBeTrimmedRemappedOrEscapeItsLiteralScope(string name) =>
        Assert.Equal("direct_upload_name_invalid", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Body(name: name))).Code);

    [Fact]
    public async Task MissingStagingIsUnconsumedAndDoesNotActivateOwner()
    {
        var calls = 0; var owner = new BrowserDirectConnections(() => { calls++; throw new InvalidOperationException(); });
        var broker = new BrowserCommands((_, _) => throw new InvalidOperationException(), directConnections: owner); var id = Guid.NewGuid().ToString("D");
        Assert.DoesNotContain(Action, broker.AvailableActions); Assert.Contains("kiosk.direct.staging-list", broker.AvailableActions);
        var error = await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(Body(id: id)));
        Assert.Equal("artifact_staging_unavailable", error.Code); Assert.False(error.Dispatched); Assert.Equal(0, calls);
        Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => broker.Get(id)).Code); Assert.True(await owner.ShutdownAsync());
        Assert.DoesNotContain("kioskDirectUpload", JsonSerializer.Serialize(new BrowserCommandOutcome(id, "devices", "completed"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("labs")]
    public async Task UploadUsesExactSourceLeaseAndSignedReceiptAndOriginalGetNeverRepeatsIt(string channel)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = await Fixture.Create(channel); var body = Body(f.Handle, artifact: f.Artifact); var result = await f.Broker.ExecuteAsync(body);
        Assert.Equal("completed", result.State); Assert.Null(result.Result); Assert.Null(result.Mutation); var d = Assert.IsType<BrowserDirectUploadOutcome>(result.KioskDirectUpload);
        Assert.Equal(f.Handle, d.ConnectionId); Assert.Equal(f.Connection.ConnectRequestId, d.ConnectRequestId); Assert.Equal(f.Connection.UsbOperationId, d.UsbOperationId);
        Assert.Equal("USB_TARGET", d.Serial); Assert.Equal(channel, d.ProductChannel); Assert.Equal(f.Artifact.Id, d.ArtifactId); Assert.Equal(f.Artifact.Sha256, d.Sha256); Assert.Equal(f.Artifact.SizeBytes, d.SizeBytes);
        Assert.Equal("my app (1)+@.apk", d.StagedName); Assert.Equal("confirmed", d.Disposition); Assert.Equal("signed-upload-size-sha256", d.Verification);
        var command = Assert.Single(f.Native.Calls, c => c.Action == KioskDirectOperatorAction.UploadStaging); Assert.True(command.OperatorConfirmed); Assert.Equal(d.StagedName, command.StagedName);
        Assert.Equal(command.ActionId, d.CoreReceipt!.ActionId); Assert.Equal(KioskDirectOperatorAction.UploadStaging, d.CoreReceipt.Action); Assert.Equal(OperatorMutationStage.Confirmed, d.CoreReceipt.Stage);
        Assert.Equal(new[] { KioskDirectOperatorAction.Adopt, KioskDirectOperatorAction.Status, KioskDirectOperatorAction.UploadStaging }, f.Native.Calls.Select(c => c.Action));
        Assert.Equal(f.Bytes, f.Native.ReadBytes); Assert.Same(result, f.Broker.Get(result.RequestId));
        var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => f.Broker.ExecuteAsync(body)); Assert.Null(duplicate.Dispatched); Assert.Equal("request_already_consumed", duplicate.Code);
        Assert.Single(f.Native.Calls, c => c.Action == KioskDirectOperatorAction.UploadStaging);
        await f.Broker.ExecuteAsync(BrowserDirectCommandsTests.Body("kiosk.direct.close", Guid.NewGuid().ToString("D"), f.Handle)); Assert.Same(result, f.Broker.Get(result.RequestId));
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var hidden in new[] { "private-sentinel", "localPath", "modifiedAtMs", "endpoint", "installReceipt" }) Assert.DoesNotContain(hidden, json);
        await f.Store.RemoveAsync(f.Artifact.Id); // terminal cache publication released the source lease
    }

    [Theory]
    [InlineData("status-session")]
    [InlineData("status-generation")]
    [InlineData("missing-source")]
    [InlineData("wrong-hash")]
    public async Task BeforeCoreUploadFailureIsBoundNotDispatched(string variant)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = await Fixture.Create(); var artifact = f.Artifact;
        if (variant.StartsWith("status-", StringComparison.Ordinal)) f.Native.Base.Mismatch = variant;
        if (variant == "missing-source") await f.Store.RemoveAsync(artifact.Id);
        if (variant == "wrong-hash") artifact = artifact with { Sha256 = new string('f', 64) };
        var result = await f.Broker.ExecuteAsync(Body(f.Handle, artifact: artifact));
        Assert.Equal("failed", result.State); var d = Assert.IsType<BrowserDirectUploadOutcome>(result.KioskDirectUpload);
        Assert.Equal("notDispatched", d.Disposition); Assert.Equal("none", d.Verification); Assert.Null(d.CoreReceipt);
        Assert.Equal(artifact.Id, d.ArtifactId); Assert.Equal(artifact.Sha256, d.Sha256); Assert.DoesNotContain(f.Native.Calls, c => c.Action == KioskDirectOperatorAction.UploadStaging);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("command")]
    [InlineData("action-id")]
    [InlineData("action")]
    [InlineData("request-id")]
    [InlineData("pending")]
    [InlineData("name")]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("extra-tags")]
    public async Task AfterInvocationCrossedOrLostAcknowledgementAlwaysRemainsUnknown(string variant)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = await Fixture.Create(); f.Native.Variant = variant; var result = await f.Broker.ExecuteAsync(Body(f.Handle, artifact: f.Artifact));
        Assert.Equal("outcomeUnknown", result.State); Assert.Equal("direct_upload_unknown", result.Error); var d = Assert.IsType<BrowserDirectUploadOutcome>(result.KioskDirectUpload);
        Assert.Equal("outcomeUnknown", d.Disposition); Assert.Equal("none", d.Verification); Assert.Null(d.CoreReceipt); Assert.Equal(f.Artifact.Id, d.ArtifactId);
        if (f.Native.UnexpectedStream is not null) Assert.False(f.Native.UnexpectedStream.CanRead);
        Assert.Null(result.Result); Assert.Null(result.Mutation); Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(result));
        Assert.Single(f.Native.Calls, c => c.Action == KioskDirectOperatorAction.UploadStaging); await f.Store.RemoveAsync(f.Artifact.Id);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("shutdown")]
    [InlineData("expiry")]
    public async Task HeldActualCoreTaskKeepsLeaseGateAndOwnerUntilItSettles(string interruption)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = await Fixture.Create(); using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Native.BeforeUpload = async _ => { entered.SetResult(); await release.Task; }; Task<bool>? shutdown = null;
        try
        {
            var body = Body(f.Handle, artifact: f.Artifact); var pending = f.Broker.ExecuteAsync(body, stopping.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(f.Gate.IsBusy); Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => f.Store.RemoveAsync(f.Artifact.Id))).Code);
            Assert.Equal(0, await f.Store.CleanupAsync()); Assert.Null((await Assert.ThrowsAsync<BrowserRequestException>(() => f.Broker.ExecuteAsync(body))).Dispatched);
            Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => f.Broker.ExecuteAsync(Body(f.Handle, artifact: f.Artifact)))).Code);
            if (interruption == "cancel") stopping.Cancel();
            if (interruption == "shutdown") { shutdown = f.Owner.ShutdownAsync(); Assert.False(shutdown.IsCompleted); }
            if (interruption == "expiry") f.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.False(pending.IsCompleted); Assert.True(f.Gate.IsBusy); Assert.Equal(0, f.Native.Base.Closes);
            release.SetResult(); var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("outcomeUnknown", result.State); Assert.Equal("outcomeUnknown", result.KioskDirectUpload!.Disposition);
            Assert.Same(result, f.Broker.Get(result.RequestId)); Assert.False(f.Gate.IsBusy); await f.Store.RemoveAsync(f.Artifact.Id);
            if (shutdown is not null) Assert.True(await shutdown);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task SourceLeaseSurvivesOwnerReturnUntilOuterTerminalPublicationCompletes()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = await Fixture.Create(); var parsed = BrowserDirectCommands.Parse(Body(f.Handle, artifact: f.Artifact)); var operation = f.Owner.Reserve(parsed, f.Broker.SessionId);
        try
        {
            var outcome = await f.Owner.ExecuteAsync(operation, CancellationToken.None, acquireUpload: token => f.Store.AcquireAsync(f.Artifact.Id, f.Artifact.Sha256, f.Artifact.SizeBytes, token));
            Assert.Equal("completed", outcome.State); Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => f.Store.RemoveAsync(f.Artifact.Id))).Code);
        }
        finally { f.Owner.Complete(operation); }
        await f.Store.RemoveAsync(f.Artifact.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossedOrExpiredAcquisitionDoesNotInvokeAndRetainsActualLeaseThroughCompletion(bool expired)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = await Fixture.Create();
        var actual = expired ? f.Artifact : await f.Store.StageAsync("other.apk", new MemoryStream(f.Bytes));
        var request = BrowserDirectCommands.Parse(Body(f.Handle, artifact: f.Artifact)); var operation = f.Owner.Reserve(request, f.Broker.SessionId);
        try
        {
            var result = await f.Owner.ExecuteAsync(operation, CancellationToken.None, acquireUpload: async token =>
            {
                var lease = await f.Store.AcquireAsync(actual.Id, actual.Sha256, actual.SizeBytes, token);
                if (expired) f.Clock.Advance(TimeSpan.FromMinutes(2)); return lease;
            });
            Assert.Equal("failed", result.State); Assert.Equal("notDispatched", result.KioskDirectUpload!.Disposition);
            Assert.Equal(f.Artifact.Id, result.KioskDirectUpload.ArtifactId); Assert.Null(result.KioskDirectUpload.CoreReceipt);
            Assert.DoesNotContain(f.Native.Calls, c => c.Action == KioskDirectOperatorAction.UploadStaging);
            Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => f.Store.RemoveAsync(actual.Id))).Code);
        }
        finally { f.Owner.Complete(operation); }
        await f.Store.RemoveAsync(actual.Id);
    }

    [Fact]
    public void LegacyRequestConstructorDeconstructAndNullWireFieldsRemainCompatible()
    {
        var request = BrowserDirectCommands.Parse(BrowserDirectCommandsTests.Body("kiosk.direct.status", Guid.NewGuid().ToString("D"), Handle));
        var (id, action, serial, channel, connection, command, value, original) = request;
        var old = new BrowserDirectRequest(id, action, serial, channel, connection, command, value, original); Assert.Equal(request, old);
        Assert.Single(typeof(BrowserDirectRequest).GetConstructors(), constructor => constructor.GetParameters().Length == 8);
        var serialized = JsonSerializer.Serialize(old); Assert.DoesNotContain("ArtifactId", serialized); Assert.DoesNotContain("ArtifactSha256", serialized);
        Assert.DoesNotContain("ArtifactSizeBytes", serialized); Assert.DoesNotContain("StagedName", serialized);
    }

    [Fact]
    public async Task CancelledSourceAcquisitionNeverInvokesUploadAndLeavesExistingBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var f = await Fixture.Create(); using var stopping = new CancellationTokenSource();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = f.Store.CaptureAsync("hold.bin", async (_, _) => { held.SetResult(); await release.Task; }); await held.Task;
        try
        {
            var pending = f.Broker.ExecuteAsync(Body(f.Handle, artifact: f.Artifact), stopping.Token); stopping.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("failed", result.State); Assert.Equal("notDispatched", result.KioskDirectUpload!.Disposition);
            Assert.DoesNotContain(f.Native.Calls, c => c.Action == KioskDirectOperatorAction.UploadStaging); Assert.False(f.Gate.IsBusy);
        }
        finally { release.TrySetResult(); await producer; }
        await using var lease = await f.Store.AcquireAsync(f.Artifact.Id, f.Artifact.Sha256, f.Artifact.SizeBytes); Assert.Equal(4, lease.Stream.Length);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly BrowserDirectConnectionsTests.Clock Clock = new(); internal readonly BrowserOperationGate Gate = new(); internal readonly byte[] Bytes = [1, 2, 3, 4];
        internal BrowserArtifactStore Store = null!; internal BrowserArtifactReceipt Artifact = null!; internal BrowserDirectConnections Owner = null!; internal BrowserCommands Broker = null!;
        internal NativeSession Native = null!; internal BrowserDirectConnectionOutcome Connection = null!; private string _root = null!; internal string Handle => Connection.ConnectionId!;
        internal static async Task<Fixture> Create(string channel = "stable")
        {
            var f = new Fixture(); f._root = Path.Combine(Path.GetTempPath(), "QfmDirectUpload-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(f._root);
            f.Store = new(f._root, new()); f.Artifact = await f.Store.StageAsync("source.apk", new MemoryStream(f.Bytes));
            f.Owner = new((context, _) => Task.FromResult<IBrowserDirectSession>(f.Native = new(context, f.Clock)), f.Clock);
            f.Broker = new((_, _) => throw new InvalidOperationException("no ordinary executor"), () => f.Store, f.Gate, directConnections: f.Owner);
            f.Connection = (await f.Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new { requestId = Guid.NewGuid().ToString("D"), action = "kiosk.direct.connect-usb", serial = "USB_TARGET", productChannel = channel, confirmed = true }))).KioskDirectConnection!;
            return f;
        }
        public async ValueTask DisposeAsync() { await Owner.ShutdownAsync(); Store.Dispose(); Directory.Delete(_root, true); }
    }

    private sealed class NativeSession(BrowserDirectConnectContext context, BrowserDirectConnectionsTests.Clock clock) : IBrowserDirectSession
    {
        internal readonly BrowserDirectConnectionsTests.FakeSession Base = new(context, clock); internal readonly List<KioskDirectOperatorCommand> Calls = [];
        internal string? Variant; internal byte[]? ReadBytes; internal Stream? UnexpectedStream; internal Func<CancellationToken, Task>? BeforeUpload;
        public RustyKioskUsbDirectLinkReceipt Receipt => Base.Receipt;
        public async Task<KioskDirectOperatorResult> ExecuteAsync(KioskDirectOperatorCommand command, CancellationToken token)
        {
            Calls.Add(command); if (command.Action != KioskDirectOperatorAction.UploadStaging) return await Base.ExecuteAsync(command, token);
            if (BeforeUpload is not null) await BeforeUpload(token);
            if (Variant == "throw") throw new IOException("private-sentinel");
            ReadBytes = await File.ReadAllBytesAsync(command.LocalPath!, CancellationToken.None); var hash = Convert.ToHexStringLower(SHA256.HashData(ReadBytes));
            var result = new KioskDirectOperatorResult(command, new(command.ActionId, command.Action, OperatorMutationStage.Confirmed, "private-sentinel"), StagedFile: new(command.StagedName!, ReadBytes.Length, -123, hash));
            result = Variant switch
            {
                "command" => result with { Command = command with { LocalPath = "private-path" } },
                "action-id" => result with { Mutation = result.Mutation with { ActionId = "private-sentinel" } },
                "action" => result with { Mutation = result.Mutation with { Action = KioskDirectOperatorAction.Install } },
                "request-id" => result with { Mutation = result.Mutation with { RequestId = "private-sentinel" } },
                "pending" => result with { Mutation = result.Mutation with { Stage = OperatorMutationStage.Pending } },
                "name" => result with { StagedFile = result.StagedFile! with { Name = "other.apk" } },
                "size" => result with { StagedFile = result.StagedFile! with { Bytes = 5 } },
                "hash" => result with { StagedFile = result.StagedFile! with { Sha256 = new string('f', 64) } },
                "extra-tags" => result with { TagExport = KioskDirectTagExportLease.Create(Encoding.UTF8.GetBytes("{\"schema\":\"rusty.kiosk.app_tags.v1\",\"apps\":[]}")) },
                _ => result
            };
            UnexpectedStream = result.TagExport?.Stream; return result;
        }
        public Task<RustyKioskUsbDirectCleanupReceipt> CloseAsync() => Base.CloseAsync(); public void Revoke() => Base.Revoke();
    }
}
