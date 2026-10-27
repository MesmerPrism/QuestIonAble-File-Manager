using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectTagExportTests
{
    private const string Action = "kiosk.direct.tags-export";
    private const string Handle = "00000000-0000-4000-8000-000000000002";
    private static byte[] Body(string? handle = null, string? id = null) => JsonSerializer.SerializeToUtf8Bytes(new
        { requestId = id ?? Guid.NewGuid().ToString("D"), action = Action, connectionId = handle ?? Handle });

    [Fact]
    public void ExportIsAClosedReadWithoutCallerOutputAuthority()
    {
        var request = BrowserDirectCommands.Parse(Body());
        Assert.Equal(Action, request.Action); Assert.Equal(Handle, request.ConnectionId);
        Assert.False(request.Mutation); Assert.False(request.Recovery); Assert.False(request.IsCommand);
        Assert.Null(request.Command); Assert.Null(request.Value); Assert.Null(request.OriginalRequestId);
    }

    [Theory]
    [InlineData("confirmed", "true")]
    [InlineData("path", "\"C:/private.json\"")]
    [InlineData("fileName", "\"private.json\"")]
    [InlineData("serial", "\"OTHER\"")]
    [InlineData("command", "\"reload\"")]
    [InlineData("value", "null")]
    public void ExtraFieldsAreRejected(string name, string json)
    {
        var text = Encoding.UTF8.GetString(Body());
        var bytes = Encoding.UTF8.GetBytes(text[..^1] + ",\"" + name + "\":" + json + "}");
        Assert.Equal("fields_invalid", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(bytes)).Code);
    }

    [Theory]
    [InlineData("requestId")]
    [InlineData("connectionId")]
    public void NoncanonicalIdsAndDuplicateFieldsCannotEnterOwner(string field)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Body())!;
        fields[field] = JsonSerializer.SerializeToElement("FFFFFFFF-FFFF-4FFF-8FFF-FFFFFFFFFFFF");
        Assert.Equal("request_id_invalid", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields))).Code);
        var text = Encoding.UTF8.GetString(Body());
        Assert.Equal("duplicate_field", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Encoding.UTF8.GetBytes(text[..^1] + ",\"connectionId\":\"" + Handle + "\"}"))).Code);
    }

    [Fact]
    public async Task MissingStagingNeverActivatesOwnerOrConsumesIdAndOldCapabilitiesStayPresent()
    {
        var calls = 0; var owner = new BrowserDirectConnections(() => { calls++; throw new InvalidOperationException(); });
        var broker = new BrowserCommands((_, _) => throw new InvalidOperationException(), directConnections: owner);
        Assert.DoesNotContain(Action, broker.AvailableActions); Assert.Contains("kiosk.direct.status", broker.AvailableActions);
        var id = Guid.NewGuid().ToString("D");
        var error = await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(Body(id: id)));
        Assert.Equal("artifact_staging_unavailable", error.Code); Assert.False(error.Dispatched);
        Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => broker.Get(id)).Code);
        Assert.Equal(0, calls); Assert.True(await owner.ShutdownAsync());
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacy = JsonSerializer.Serialize(new BrowserCommandOutcome(id, "devices", "completed"), options);
        Assert.DoesNotContain("kioskDirectTags", legacy);
    }

    [Theory]
    [InlineData("stable", "rusty.kiosk.app_tags.v1")]
    [InlineData("labs", "rusty.kiosk.app_tags.v2")]
    public async Task SignedTypedExportStagesExactBytesAndOriginalGetNeverReexports(string channel, string schema)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.Create(channel, schema);
        Assert.Contains(Action, fixture.Broker.AvailableActions);
        var body = Body(fixture.Handle); var result = await fixture.Broker.ExecuteAsync(body);
        Assert.Equal("completed", result.State); Assert.Null(result.Result); Assert.Null(result.Mutation);
        var facts = Assert.IsType<BrowserDirectTagsOutcome>(result.KioskDirectTags);
        Assert.Equal(fixture.Handle, facts.ConnectionId); Assert.Equal(fixture.Connection.ConnectRequestId, facts.ConnectRequestId);
        Assert.Equal(fixture.Connection.UsbOperationId, facts.UsbOperationId); Assert.Equal("USB_TARGET", facts.Serial); Assert.Equal(channel, facts.ProductChannel);
        Assert.Equal(schema, facts.Schema); Assert.Equal(fixture.Native.Bytes.LongLength, facts.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(fixture.Native.Bytes)), facts.Sha256);
        Assert.Equal("app-tags.json", facts.Artifact.FileName); Assert.Equal(facts.SizeBytes, facts.Artifact.SizeBytes); Assert.Equal(facts.Sha256, facts.Artifact.Sha256);
        Assert.Equal(KioskDirectOperatorAction.ExportTags, facts.CoreReceipt.Action); Assert.Equal(OperatorMutationStage.Confirmed, facts.CoreReceipt.Stage);
        var command = Assert.Single(fixture.Native.Calls, c => c.Action == KioskDirectOperatorAction.ExportTags);
        Assert.Null(command.LocalPath); Assert.Equal(command.ActionId, facts.CoreReceipt.ActionId);
        Assert.Equal(1, fixture.Native.Base.Adopts); Assert.Equal(1, fixture.Native.Base.Statuses);
        Assert.False(fixture.Native.LastStream!.CanRead);
        await using var lease = await fixture.Store.AcquireAsync(facts.Artifact.Id, facts.Sha256, facts.SizeBytes);
        using var bytes = new MemoryStream(); await lease.Stream.CopyToAsync(bytes); Assert.Equal(fixture.Native.Bytes, bytes.ToArray());
        Assert.Same(result, fixture.Broker.Get(result.RequestId));
        var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(body));
        Assert.Equal("request_already_consumed", duplicate.Code); Assert.Null(duplicate.Dispatched);
        Assert.Single(fixture.Native.Calls, c => c.Action == KioskDirectOperatorAction.ExportTags);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var secret in new[] { "private-sentinel", "private-tag", "private-name", "localPath", "endpoint", "authenticationKey" }) Assert.DoesNotContain(secret, json);
    }

    [Theory]
    [InlineData("status-session")]
    [InlineData("status-generation")]
    public async Task CrossedSignedSessionStopsBeforeTagRead(string mismatch)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.Create(); fixture.Native.Base.Mismatch = mismatch;
        var result = await fixture.Broker.ExecuteAsync(Body(fixture.Handle));
        Assert.Equal("failed", result.State); Assert.Equal("direct_tags_export_failed", result.Error); Assert.Null(result.KioskDirectTags);
        Assert.DoesNotContain(fixture.Native.Calls, c => c.Action == KioskDirectOperatorAction.ExportTags);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("action-id")]
    [InlineData("action")]
    [InlineData("request-id")]
    [InlineData("pending")]
    [InlineData("invalid-stage")]
    [InlineData("missing-lease")]
    [InlineData("extra-result")]
    [InlineData("stream-position")]
    public async Task CrossedExportEvidenceCannotPublishAndAlwaysReleasesLease(string variant)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.Create(); fixture.Native.Variant = variant;
        var result = await fixture.Broker.ExecuteAsync(Body(fixture.Handle));
        Assert.Equal("failed", result.State); Assert.Equal("direct_tags_export_failed", result.Error);
        Assert.Null(result.KioskDirectTags); Assert.Null(result.Result); Assert.Null(result.Mutation);
        if (fixture.Native.LastStream is not null) Assert.False(fixture.Native.LastStream.CanRead);
        Assert.False(fixture.Gate.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAcknowledgementHoldsLeaseSharedGateAndShutdown(bool shutdown)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = fixture.Store.CaptureAsync("held.bin", async (_, _) => { entered.SetResult(); await release.Task; });
        await entered.Task;
        try
        {
            var body = Body(fixture.Handle); var pending = fixture.Broker.ExecuteAsync(body);
            await fixture.Native.Exported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fixture.Gate.IsBusy); Assert.False(pending.IsCompleted); Assert.True(fixture.Native.LastStream!.CanRead);
            var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(body)); Assert.Null(duplicate.Dispatched);
            var busy = await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(Body(fixture.Handle))); Assert.Equal("busy", busy.Code);
            var close = shutdown ? fixture.Owner.ShutdownAsync() : null;
            if (close is not null) { Assert.False(close.IsCompleted); Assert.Equal(0, fixture.Native.Base.Closes); }
            release.SetResult(); await held; var result = await pending;
            Assert.Equal(shutdown ? "failed" : "completed", result.State); Assert.False(fixture.Native.LastStream.CanRead); Assert.False(fixture.Gate.IsBusy);
            if (close is not null) { Assert.True(await close); Assert.Equal(1, fixture.Native.Base.Closes); }
        }
        finally { release.TrySetResult(); await held; }
    }

    [Fact]
    public async Task CancellationWhileWaitingForStoreReleasesOnlyReadLeaseAndNeverPublishes()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.Create(); using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = fixture.Store.CaptureAsync("held.bin", async (_, _) => { entered.SetResult(); await release.Task; }); await entered.Task;
        try
        {
            var pending = fixture.Broker.ExecuteAsync(Body(fixture.Handle), stopping.Token); await fixture.Native.Exported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping.Cancel(); var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("failed", result.State); Assert.Equal("direct_tags_export_cancelled", result.Error); Assert.Null(result.KioskDirectTags);
            Assert.False(fixture.Native.LastStream!.CanRead); Assert.False(fixture.Gate.IsBusy); Assert.Equal(0, fixture.Native.Base.Closes);
        }
        finally { release.TrySetResult(); await held; }
    }

    [Fact]
    public async Task ExpiryAfterNativeReadDoesNotPublishAndExpiredOrForeignHandlesNeverExport()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.Create(); fixture.Native.BeforeReturn = () => fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var result = await fixture.Broker.ExecuteAsync(Body(fixture.Handle));
        Assert.Equal("failed", result.State); Assert.Null(result.KioskDirectTags); Assert.False(fixture.Native.LastStream!.CanRead);
        var before = fixture.Native.Calls.Count;
        Assert.Equal("direct_connection_expired", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(Body(fixture.Handle)))).Code);
        Assert.Equal("direct_connection_not_found", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Broker.ExecuteAsync(Body(Guid.NewGuid().ToString("D"))))).Code);
        var foreign = new BrowserCommands((_, _) => throw new InvalidOperationException(), () => fixture.Store, fixture.Gate, directConnections: fixture.Owner);
        Assert.Equal("session_changed", (await Assert.ThrowsAsync<BrowserRequestException>(() => foreign.ExecuteAsync(Body(fixture.Handle)))).Code);
        Assert.Equal(before, fixture.Native.Calls.Count);
    }

    [Fact]
    public async Task StoreCapacityFailureLeavesExistingOwnedBytesIntactAndNoRawError()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.Create(maximumFiles: 1);
        var old = await fixture.Store.StageAsync("existing.json", new MemoryStream([1, 2, 3]));
        var result = await fixture.Broker.ExecuteAsync(Body(fixture.Handle));
        Assert.Equal("failed", result.State); Assert.Equal("direct_tags_export_failed", result.Error); Assert.Null(result.KioskDirectTags);
        Assert.False(fixture.Native.LastStream!.CanRead);
        await using var lease = await fixture.Store.AcquireAsync(old.Id, old.Sha256, old.SizeBytes); Assert.Equal(3, lease.Stream.Length);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("size")]
    [InlineData("hash")]
    public async Task StagingReceiptMustJoinTheExactLease(string variant)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.Create();
        var request = BrowserDirectCommands.Parse(Body(fixture.Handle)); var operation = fixture.Owner.Reserve(request, fixture.Broker.SessionId);
        try
        {
            var result = await fixture.Owner.ExecuteAsync(operation, CancellationToken.None, (lease, _) => Task.FromResult(new BrowserArtifactReceipt(
                variant == "id" ? new string('0', 32) : Guid.NewGuid().ToString("N"), variant == "name" ? "other.json" : "app-tags.json",
                lease.SizeBytes + (variant == "size" ? 1 : 0), variant == "hash" ? new string('0', 64) : lease.Sha256)));
            Assert.Equal("failed", result.State); Assert.Null(result.KioskDirectTags); Assert.False(fixture.Native.LastStream!.CanRead);
        }
        finally { fixture.Owner.Complete(operation); }
    }

    [Fact]
    public async Task ExpiryAcrossSuccessfulStageLeavesExactTransientBytesAccountedButUnpublished()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await Fixture.Create(); BrowserArtifactReceipt? staged = null;
        var request = BrowserDirectCommands.Parse(Body(fixture.Handle)); var operation = fixture.Owner.Reserve(request, fixture.Broker.SessionId);
        try
        {
            var result = await fixture.Owner.ExecuteAsync(operation, CancellationToken.None, async (lease, token) =>
            {
                staged = await fixture.Store.StageAsync("app-tags.json", lease.Stream, lease.SizeBytes, lease.Sha256, token);
                fixture.Clock.Advance(TimeSpan.FromMinutes(2)); return staged;
            });
            Assert.Equal("failed", result.State); Assert.Null(result.KioskDirectTags); Assert.False(fixture.Native.LastStream!.CanRead);
            Assert.NotNull(staged); await using var preserved = await fixture.Store.AcquireAsync(staged.Id, staged.Sha256, staged.SizeBytes);
            Assert.Equal(staged.SizeBytes, preserved.Stream.Length);
        }
        finally { fixture.Owner.Complete(operation); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly BrowserDirectConnectionsTests.Clock Clock = new(); internal readonly BrowserOperationGate Gate = new();
        internal BrowserArtifactStore Store = null!; internal BrowserDirectConnections Owner = null!; internal BrowserCommands Broker = null!;
        internal NativeSession Native = null!; internal BrowserDirectConnectionOutcome Connection = null!;
        private string _root = null!; internal string Handle => Connection.ConnectionId!;
        internal static async Task<Fixture> Create(string channel = "stable", string schema = "rusty.kiosk.app_tags.v2", int maximumFiles = 8)
        {
            var f = new Fixture(); f._root = Path.Combine(Path.GetTempPath(), "QfmTagExport-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(f._root);
            f.Store = new BrowserArtifactStore(f._root, new(MaximumFiles: maximumFiles));
            f.Owner = new((context, _) => Task.FromResult<IBrowserDirectSession>(f.Native = new(context, f.Clock, schema)), f.Clock);
            f.Broker = new((_, _) => throw new InvalidOperationException("no ordinary executor"), () => f.Store, f.Gate, directConnections: f.Owner);
            var connect = JsonSerializer.SerializeToUtf8Bytes(new { requestId = Guid.NewGuid().ToString("D"), action = "kiosk.direct.connect-usb", serial = "USB_TARGET", productChannel = channel, confirmed = true });
            f.Connection = (await f.Broker.ExecuteAsync(connect)).KioskDirectConnection!; return f;
        }
        public async ValueTask DisposeAsync() { await Owner.ShutdownAsync(); Store.Dispose(); Directory.Delete(_root, true); }
    }

    private sealed class NativeSession(BrowserDirectConnectContext context, BrowserDirectConnectionsTests.Clock clock, string schema) : IBrowserDirectSession
    {
        internal readonly BrowserDirectConnectionsTests.FakeSession Base = new(context, clock);
        internal readonly List<KioskDirectOperatorCommand> Calls = []; internal readonly TaskCompletionSource Exported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly byte[] Bytes = Encoding.UTF8.GetBytes("{\"schema\":\"" + schema + "\",\"apps\":[{\"name\":\"private-name\",\"tags\":[\"private-tag\"]}]}");
        internal string? Variant; internal Stream? LastStream; internal System.Action? BeforeReturn;
        public RustyKioskUsbDirectLinkReceipt Receipt => Base.Receipt;
        public async Task<KioskDirectOperatorResult> ExecuteAsync(KioskDirectOperatorCommand command, CancellationToken token)
        {
            Calls.Add(command); if (command.Action != KioskDirectOperatorAction.ExportTags) return await Base.ExecuteAsync(command, token);
            var lease = Variant == "missing-lease" ? null : KioskDirectTagExportLease.Create(Bytes);
            LastStream = lease?.Stream; if (Variant == "stream-position") LastStream!.Position = 1;
            var result = new KioskDirectOperatorResult(command, new(command.ActionId, command.Action, OperatorMutationStage.Confirmed, "private-sentinel")) { TagExport = lease };
            result = Variant switch
            {
                "command" => result with { Command = command with { LocalPath = "private-path" } },
                "action-id" => result with { Mutation = result.Mutation with { ActionId = "private-sentinel" } },
                "action" => result with { Mutation = result.Mutation with { Action = KioskDirectOperatorAction.ImportTags } },
                "request-id" => result with { Mutation = result.Mutation with { RequestId = "private-sentinel" } },
                "pending" => result with { Mutation = result.Mutation with { Stage = OperatorMutationStage.Pending } },
                "invalid-stage" => result with { Mutation = result.Mutation with { Stage = (OperatorMutationStage)999 } },
                "extra-result" => result with { LocalFileName = "private-path.json" },
                _ => result
            };
            BeforeReturn?.Invoke(); Exported.TrySetResult(); return result;
        }
        public Task<RustyKioskUsbDirectCleanupReceipt> CloseAsync() => Base.CloseAsync();
        public void Revoke() => Base.Revoke();
    }
}
