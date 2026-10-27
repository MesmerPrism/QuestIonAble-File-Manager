using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserDirectStagingTests
{
    private const string Action = "kiosk.direct.staging-list";
    private const string Handle = "00000000-0000-4000-8000-000000000002";
    private static byte[] Body(string? handle = null, string? id = null) => JsonSerializer.SerializeToUtf8Bytes(new
        { requestId = id ?? Guid.NewGuid().ToString("D"), action = Action, connectionId = handle ?? Handle });

    [Fact]
    public void InventoryIsAClosedReadWithoutCallerTransferAuthority()
    {
        var request = BrowserDirectCommands.Parse(Body());
        Assert.Equal(Action, request.Action); Assert.Equal(Handle, request.ConnectionId);
        Assert.False(request.Mutation); Assert.False(request.Recovery); Assert.False(request.IsCommand);
        Assert.Null(request.Command); Assert.Null(request.Value); Assert.Null(request.OriginalRequestId);
    }

    [Theory]
    [InlineData("confirmed", "true")]
    [InlineData("path", "\"C:/private\"")]
    [InlineData("name", "\"example.apk\"")]
    [InlineData("serial", "\"OTHER\"")]
    [InlineData("command", "\"reload\"")]
    [InlineData("value", "null")]
    [InlineData("limit", "128")]
    public void ExtraFieldsAreRejectedBeforeOwnerAdmission(string name, string json)
    {
        var text = Encoding.UTF8.GetString(Body());
        var bytes = Encoding.UTF8.GetBytes(text[..^1] + ",\"" + name + "\":" + json + "}");
        Assert.Equal("fields_invalid", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(bytes)).Code);
    }

    [Theory]
    [InlineData("requestId", "FFFFFFFF-FFFF-4FFF-8FFF-FFFFFFFFFFFF")]
    [InlineData("connectionId", "FFFFFFFF-FFFF-4FFF-8FFF-FFFFFFFFFFFF")]
    [InlineData("connectionId", "00000000-0000-0000-0000-000000000000")]
    public void InvalidIdsAndDuplicateFieldsFailClosed(string name, string value)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Body())!; fields[name] = JsonSerializer.SerializeToElement(value);
        Assert.Equal("request_id_invalid", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(JsonSerializer.SerializeToUtf8Bytes(fields))).Code);
        var text = Encoding.UTF8.GetString(Body());
        Assert.Equal("duplicate_field", Assert.Throws<BrowserRequestException>(() => BrowserDirectCommands.Parse(Encoding.UTF8.GetBytes(text[..^1] + ",\"connectionId\":\"" + Handle + "\"}"))).Code);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("labs")]
    public async Task ListUsesSignedOriginalScopeWithoutAnyArtifactStore(string channel)
    {
        await using var f = await Fixture.Create(channel);
        Assert.Contains(Action, f.Broker.AvailableActions); Assert.Equal(0, f.StoreCalls);
        var names = new[] { "z.apk", "my app (1)+@.apk", "A.apk", "a.apk", "literal ", new string('x', 160) };
        f.Native.Files = names.Select((name, index) => new RustyKioskStagedFile(name, index, 9007199254740991)).ToArray();
        var body = Body(f.Handle); var outcome = await f.Broker.ExecuteAsync(body);
        Assert.Equal("completed", outcome.State); Assert.Null(outcome.Result); Assert.Null(outcome.Mutation);
        var value = Assert.IsType<BrowserDirectStagingOutcome>(outcome.KioskDirectStaging);
        Assert.Equal(f.Handle, value.ConnectionId); Assert.Equal(f.Connection.ConnectRequestId, value.ConnectRequestId); Assert.Equal(f.Connection.UsbOperationId, value.UsbOperationId);
        Assert.Equal("USB_TARGET", value.Serial); Assert.Equal(channel, value.ProductChannel);
        Assert.Equal(names.Length, value.TotalCount); Assert.Equal(names.Length, value.ReturnedCount); Assert.Equal(0, value.OmittedCount); Assert.True(value.Complete);
        Assert.Equal(names, value.Files.Select(item => item.Name)); Assert.Equal(Enumerable.Range(0, names.Length).Select(i => (long)i), value.Files.Select(item => item.Bytes));
        Assert.All(value.Files, item => Assert.Equal(9007199254740991, item.ModifiedAtMs));
        var command = Assert.Single(f.Native.Calls, command => command.Action == KioskDirectOperatorAction.ListStaging);
        Assert.Equal(command.ActionId, value.CoreReceipt.ActionId); Assert.Equal(KioskDirectOperatorAction.ListStaging, value.CoreReceipt.Action); Assert.Equal(OperatorMutationStage.Confirmed, value.CoreReceipt.Stage);
        Assert.Equal(new[] { KioskDirectOperatorAction.Adopt, KioskDirectOperatorAction.Status, KioskDirectOperatorAction.ListStaging }, f.Native.Calls.Select(c => c.Action));
        Assert.Equal(0, f.StoreCalls); Assert.Same(outcome, f.Broker.Get(outcome.RequestId));
        var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => f.Broker.ExecuteAsync(body)); Assert.Equal("request_already_consumed", duplicate.Code); Assert.Null(duplicate.Dispatched);
        Assert.Single(f.Native.Calls, c => c.Action == KioskDirectOperatorAction.ListStaging);
        await f.Broker.ExecuteAsync(BrowserDirectCommandsTests.Body("kiosk.direct.close", Guid.NewGuid().ToString("D"), f.Handle));
        Assert.Same(outcome, f.Broker.Get(outcome.RequestId)); Assert.Single(f.Native.Calls, c => c.Action == KioskDirectOperatorAction.ListStaging);
        // The published ledger snapshot owns primitive metadata, not the source array.
        ((RustyKioskStagedFile[])f.Native.Files)[0] = new("replacement.apk", 500, 500); Assert.Equal("z.apk", value.Files[0].Name);
        var json = JsonSerializer.Serialize(outcome, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("sha256", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("private-sentinel", json);
        Assert.DoesNotContain("endpoint", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("localPath", json);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(1000)]
    public async Task WholeListCountsAnd128ProviderOrderedRowsAreHonest(int total)
    {
        await using var f = await Fixture.Create();
        f.Native.Files = Enumerable.Range(0, total).Select(i => new RustyKioskStagedFile("f" + (total - i).ToString("D4") + ".apk", 9007199254740991, 0)).ToArray();
        var result = await f.Broker.ExecuteAsync(Body(f.Handle)); var value = Assert.IsType<BrowserDirectStagingOutcome>(result.KioskDirectStaging);
        Assert.Equal(total, value.TotalCount); Assert.Equal(Math.Min(128, total), value.ReturnedCount); Assert.Equal(total - value.ReturnedCount, value.OmittedCount);
        Assert.Equal(total <= 128, value.Complete); Assert.Equal(f.Native.Files.Take(128).Select(x => x.Name), value.Files.Select(x => x.Name));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= 262144);
    }

    [Fact]
    public async Task MaximumWidthAllowedNamesHaveBoundedActualWebEncodingWithoutTruncatingValidRows()
    {
        await using var f = await Fixture.Create();
        f.Native.Files = Enumerable.Range(0, 128).Select(i => new RustyKioskStagedFile(i.ToString("D3") + new string('+', 157), 9007199254740991, 9007199254740991)).ToArray();
        var result = await f.Broker.ExecuteAsync(Body(f.Handle)); Assert.Equal("completed", result.State);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var encoded = JsonSerializer.SerializeToUtf8Bytes(result, options);
        // Default Web escaping expands '+'. There is no 64 KiB server cap.
        Assert.InRange(encoded.Length, 130000, 262144);
        Assert.Equal(128, result.KioskDirectStaging!.ReturnedCount); Assert.True(result.KioskDirectStaging.Complete);
        using var json = JsonDocument.Parse(encoded);
        Assert.Equal("listStaging", json.RootElement.GetProperty("kioskDirectStaging").GetProperty("coreReceipt").GetProperty("action").GetString());
        Assert.Equal(f.Native.Files.Select(file => file.Name), json.RootElement.GetProperty("kioskDirectStaging").GetProperty("files").EnumerateArray().Select(file => file.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-row")]
    [InlineData("null-name")]
    [InlineData("long-name")]
    [InlineData("duplicate")]
    [InlineData("bad-name")]
    [InlineData("negative-bytes")]
    [InlineData("huge-bytes")]
    [InlineData("negative-time")]
    [InlineData("huge-time")]
    [InlineData("unexpected-hash")]
    public async Task InvalidWholeListTailCannotHideBehindTruncation(string variant)
    {
        await using var f = await Fixture.Create();
        var files = Enumerable.Range(0, 129).Select(i => new RustyKioskStagedFile("file" + i + ".apk", 0, 0)).ToArray();
        files[^1] = variant switch
        {
            "null-row" => null!, "null-name" => new(null!, 0, 0), "long-name" => new(new string('x', 161), 0, 0),
            "duplicate" => files[0], "bad-name" => new("../private.apk", 1, 1),
            "negative-bytes" => new("tail.apk", -1, 0), "huge-bytes" => new("tail.apk", 9007199254740992, 0),
            "negative-time" => new("tail.apk", 0, -1), "huge-time" => new("tail.apk", 0, 9007199254740992),
            "unexpected-hash" => new("tail.apk", 0, 0, new string('a', 64)), _ => files[^1]
        };
        f.Native.Files = variant == "null-list" ? null : files;
        var result = await f.Broker.ExecuteAsync(Body(f.Handle));
        Assert.Equal("failed", result.State); Assert.Equal("direct_staging_list_failed", result.Error); Assert.Null(result.KioskDirectStaging);
        Assert.Null(result.Result); Assert.Null(result.Mutation); Assert.False(f.Gate.IsBusy);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" leading.apk")]
    [InlineData("folder/file.apk")]
    [InlineData("folder\\file.apk")]
    [InlineData("bad\n")]
    [InlineData("bad\r")]
    [InlineData("bad\0")]
    [InlineData("café.apk")]
    public async Task NamesMustBeLiteralWholeInputBoundedAscii(string name)
    {
        await using var f = await Fixture.Create(); f.Native.Files = [new(name, 1, 1)];
        var result = await f.Broker.ExecuteAsync(Body(f.Handle)); Assert.Equal("failed", result.State); Assert.Null(result.KioskDirectStaging);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("action-id")]
    [InlineData("action")]
    [InlineData("request-id")]
    [InlineData("pending")]
    [InlineData("invalid-stage")]
    [InlineData("extra-file")]
    [InlineData("extra-tags")]
    public async Task CrossedOrUnexpectedCoreFactsNeverBecomeInventory(string variant)
    {
        await using var f = await Fixture.Create(); f.Native.Variant = variant;
        var result = await f.Broker.ExecuteAsync(Body(f.Handle)); Assert.Equal("failed", result.State); Assert.Equal("direct_staging_list_failed", result.Error);
        Assert.Null(result.KioskDirectStaging); if (f.Native.UnexpectedStream is not null) Assert.False(f.Native.UnexpectedStream.CanRead);
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("status-session")]
    [InlineData("status-generation")]
    public async Task FreshCrossedStatusRejectsBeforeNativeList(string variant)
    {
        await using var f = await Fixture.Create(); f.Native.Base.Mismatch = variant;
        var result = await f.Broker.ExecuteAsync(Body(f.Handle)); Assert.Equal("failed", result.State); Assert.Null(result.KioskDirectStaging);
        Assert.DoesNotContain(f.Native.Calls, c => c.Action == KioskDirectOperatorAction.ListStaging);
    }

    [Fact]
    public async Task HeldListKeepsSharedGateAndShutdownUntilOriginalCompletes()
    {
        await using var f = await Fixture.Create(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Native.BeforeList = async _ => { entered.SetResult(); await release.Task; };
        try
        {
            var body = Body(f.Handle); var pending = f.Broker.ExecuteAsync(body); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(f.Gate.IsBusy); Assert.False(pending.IsCompleted);
            Assert.Null((await Assert.ThrowsAsync<BrowserRequestException>(() => f.Broker.ExecuteAsync(body))).Dispatched);
            Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => f.Broker.ExecuteAsync(Body(f.Handle)))).Code);
            var shutdown = f.Owner.ShutdownAsync(); Assert.False(shutdown.IsCompleted); Assert.Equal(0, f.Native.Base.Closes);
            release.SetResult(); var result = await pending; Assert.Equal("failed", result.State); Assert.Null(result.KioskDirectStaging);
            Assert.True(await shutdown); Assert.False(f.Gate.IsBusy); Assert.Equal(1, f.Native.Base.Closes);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancellationAndProviderFailureAreFixedReadFailuresWithoutReplay()
    {
        await using var f = await Fixture.Create(); using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Native.BeforeList = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        var body = Body(f.Handle); var pending = f.Broker.ExecuteAsync(body, stopping.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stopping.Cancel();
        var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("failed", cancelled.State); Assert.Equal("direct_staging_list_cancelled", cancelled.Error);
        Assert.Same(cancelled, f.Broker.Get(cancelled.RequestId)); Assert.False(f.Gate.IsBusy);
        f.Native.BeforeList = _ => throw new IOException("private-sentinel"); var failed = await f.Broker.ExecuteAsync(Body(f.Handle));
        Assert.Equal("direct_staging_list_failed", failed.Error); Assert.Null(failed.KioskDirectStaging); Assert.Equal(0, f.Native.Base.Closes);
    }

    [Fact]
    public async Task ExpiryAndForeignHostNeverPublishOrReuseAnotherConnection()
    {
        await using var f = await Fixture.Create(); f.Native.AfterList = () => f.Clock.Advance(TimeSpan.FromMinutes(2));
        var result = await f.Broker.ExecuteAsync(Body(f.Handle)); Assert.Equal("failed", result.State); Assert.Null(result.KioskDirectStaging);
        var before = f.Native.Calls.Count;
        Assert.Equal("direct_connection_expired", (await Assert.ThrowsAsync<BrowserRequestException>(() => f.Broker.ExecuteAsync(Body(f.Handle)))).Code);
        Assert.Equal("direct_connection_not_found", (await Assert.ThrowsAsync<BrowserRequestException>(() => f.Broker.ExecuteAsync(Body(Guid.NewGuid().ToString("D"))))).Code);
        var foreign = new BrowserCommands((_, _) => throw new InvalidOperationException(), operationGate: f.Gate, directConnections: f.Owner);
        Assert.Equal("session_changed", (await Assert.ThrowsAsync<BrowserRequestException>(() => foreign.ExecuteAsync(Body(f.Handle)))).Code); Assert.Equal(before, f.Native.Calls.Count);
    }

    [Fact]
    public void LegacyOutcomeWireOmitsNewNullPropertyAndCapabilitiesStayLazy()
    {
        var calls = 0; var owner = new BrowserDirectConnections(() => { calls++; throw new InvalidOperationException(); });
        var broker = new BrowserCommands((_, _) => throw new InvalidOperationException(), directConnections: owner);
        Assert.Contains(Action, broker.AvailableActions); Assert.DoesNotContain("kiosk.direct.tags-export", broker.AvailableActions); Assert.Equal(0, calls);
        Assert.DoesNotContain("kioskDirectStaging", JsonSerializer.Serialize(new BrowserCommandOutcome("id", "devices", "completed"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly BrowserDirectConnectionsTests.Clock Clock = new(); internal readonly BrowserOperationGate Gate = new();
        internal BrowserDirectConnections Owner = null!; internal BrowserCommands Broker = null!; internal NativeSession Native = null!;
        internal BrowserDirectConnectionOutcome Connection = null!; internal int StoreCalls; internal string Handle => Connection.ConnectionId!;
        internal static async Task<Fixture> Create(string channel = "stable")
        {
            var f = new Fixture(); f.Owner = new((context, _) => Task.FromResult<IBrowserDirectSession>(f.Native = new(context, f.Clock)), f.Clock);
            f.Broker = new((_, _) => throw new InvalidOperationException("no ordinary executor"), () => { f.StoreCalls++; throw new InvalidOperationException("no artifact store"); }, f.Gate, directConnections: f.Owner);
            f.Connection = (await f.Broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new { requestId = Guid.NewGuid().ToString("D"), action = "kiosk.direct.connect-usb", serial = "USB_TARGET", productChannel = channel, confirmed = true }))).KioskDirectConnection!;
            return f;
        }
        public async ValueTask DisposeAsync() { await Owner.ShutdownAsync(); }
    }

    private sealed class NativeSession(BrowserDirectConnectContext context, BrowserDirectConnectionsTests.Clock clock) : IBrowserDirectSession
    {
        internal readonly BrowserDirectConnectionsTests.FakeSession Base = new(context, clock); internal readonly List<KioskDirectOperatorCommand> Calls = [];
        internal IReadOnlyList<RustyKioskStagedFile>? Files = []; internal string? Variant; internal Stream? UnexpectedStream;
        internal Func<CancellationToken, Task>? BeforeList; internal System.Action? AfterList;
        public RustyKioskUsbDirectLinkReceipt Receipt => Base.Receipt;
        public async Task<KioskDirectOperatorResult> ExecuteAsync(KioskDirectOperatorCommand command, CancellationToken token)
        {
            Calls.Add(command); if (command.Action != KioskDirectOperatorAction.ListStaging) return await Base.ExecuteAsync(command, token);
            if (BeforeList is not null) await BeforeList(token);
            var result = new KioskDirectOperatorResult(command, new(command.ActionId, command.Action, OperatorMutationStage.Confirmed, "private-sentinel"), StagedFiles: Files);
            result = Variant switch
            {
                "command" => result with { Command = command with { LocalPath = "private-path" } },
                "action-id" => result with { Mutation = result.Mutation with { ActionId = "private-sentinel" } },
                "action" => result with { Mutation = result.Mutation with { Action = KioskDirectOperatorAction.DeleteStaging } },
                "request-id" => result with { Mutation = result.Mutation with { RequestId = "private-sentinel" } },
                "pending" => result with { Mutation = result.Mutation with { Stage = OperatorMutationStage.Pending } },
                "invalid-stage" => result with { Mutation = result.Mutation with { Stage = (OperatorMutationStage)999 } },
                "extra-file" => result with { StagedFile = new("private-sentinel", 0, 0) },
                "extra-tags" => result with { TagExport = KioskDirectTagExportLease.Create(Encoding.UTF8.GetBytes("{\"schema\":\"rusty.kiosk.app_tags.v1\",\"apps\":[]}")) },
                _ => result
            };
            UnexpectedStream = result.TagExport?.Stream; AfterList?.Invoke(); return result;
        }
        public Task<RustyKioskUsbDirectCleanupReceipt> CloseAsync() => Base.CloseAsync(); public void Revoke() => Base.Revoke();
    }
}
