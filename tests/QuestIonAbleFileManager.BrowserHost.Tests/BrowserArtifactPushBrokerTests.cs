using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserArtifactPushBrokerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private const string Marker = "pc-00000000000040008000000000000001";

    [Fact]
    public async Task PushKeepsGenericSourceAndSharedGateUntilReadbackThenPublishesOnlyBoundSizeFacts()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.bin", new MemoryStream([1, 2, 3]));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new BrowserOperationGate(); var calls = 0; string? path = null;
        var registry = new BrowserCommands(async (command, _) =>
        {
            calls++; path = command.LocalPath; started.SetResult(); await finish.Task;
            return Result(command);
        }, () => store, gate);
        Assert.Contains("files.push", registry.AvailableActions);
        var id = Guid.NewGuid().ToString("D"); var body = BrowserArtifactPushCommandsTests.Body(receipt, id);
        var pending = registry.ExecuteAsync(body); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(gate.IsBusy); Assert.Equal("running", registry.Get(id).State);
            Assert.Throws<IOException>(() => File.Open(path!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => store.RemoveAsync(receipt.Id))).Code);
            var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(body));
            Assert.Equal("request_already_consumed", duplicate.Code); Assert.Null(duplicate.Dispatched); Assert.Equal(1, calls);
        }
        finally { finish.SetResult(); }
        var outcome = await pending; Assert.False(gate.IsBusy); Assert.Equal("completed", outcome.State); Assert.Null(outcome.Result);
        Assert.Equal(Marker, outcome.Mutation!.OperationId); Assert.Equal(OperatorCommandKind.PushFile, outcome.Mutation.CommandKind);
        Assert.Equal(OperatorMutationStage.Confirmed, outcome.Mutation.Stage); Assert.True(outcome.Mutation.HeadsetReadback);
        var json = JsonSerializer.SerializeToElement(outcome, Json); var push = json.GetProperty("artifactPush");
        Assert.Equal("remote-size", push.GetProperty("verification").GetString()); Assert.True(push.GetProperty("remoteSizeVerified").GetBoolean());
        Assert.Equal(receipt.Id, push.GetProperty("artifact").GetProperty("id").GetString());
        Assert.Equal(BrowserArtifactPushCommandsTests.Serial, push.GetProperty("serial").GetString());
        Assert.Equal(BrowserArtifactPushCommandsTests.RemotePath, push.GetProperty("remotePath").GetString());
        foreach (var text in Strings(json))
        {
            Assert.DoesNotContain(path!, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetDirectoryName(path!)!, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-native-output", text, StringComparison.Ordinal);
        }
        await store.RemoveAsync(receipt.Id);
        var consumed = await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(body));
        Assert.Null(consumed.Dispatched); Assert.Equal("request_already_consumed", consumed.Code); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("serial")]
    [InlineData("local")]
    [InlineData("remote")]
    [InlineData("receipt-kind")]
    [InlineData("receipt-target")]
    [InlineData("receipt-destination")]
    [InlineData("receipt-id")]
    [InlineData("readback")]
    [InlineData("missing")]
    [InlineData("failed-command")]
    public async Task CrossedOrMissingPostDispatchProofIsUnknownAndCannotPublishConfirmation(string mismatch)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.bin", new MemoryStream([1, 2, 3]));
        var registry = new BrowserCommands((command, _) =>
        {
            var good = Result(command);
            return Task.FromResult(mismatch switch
            {
                "kind" => good with { Command = OperatorCommands.PullFile(command.Serial!, command.RemotePath!, command.LocalPath!) },
                "serial" => good with { Command = OperatorCommands.PushFile("other-target", command.LocalPath!, command.RemotePath!) },
                "local" => good with { Command = OperatorCommands.PushFile(command.Serial!, command.LocalPath! + ".other", command.RemotePath!) },
                "remote" => good with { Command = OperatorCommands.PushFile(command.Serial!, command.LocalPath!, command.RemotePath! + ".other") },
                "receipt-kind" => good with { MutationReceipt = good.MutationReceipt! with { CommandKind = OperatorCommandKind.InstallApk } },
                "receipt-target" => good with { MutationReceipt = good.MutationReceipt! with { Target = "other-target" } },
                "receipt-destination" => good with { MutationReceipt = good.MutationReceipt! with { DesiredState = "file present at /sdcard/other.bin" } },
                "receipt-id" => good with { MutationReceipt = good.MutationReceipt! with { OperationId = "invented" } },
                "readback" => good with { MutationReceipt = good.MutationReceipt! with { HeadsetReadback = false } },
                "missing" => good with { MutationReceipt = null },
                "failed-command" => good with { CommandResult = good.CommandResult! with { ExitCode = 1 } },
                _ => throw new InvalidOperationException()
            });
        }, () => store);
        var outcome = await registry.ExecuteAsync(BrowserArtifactPushCommandsTests.Body(receipt));
        Assert.Equal("outcomeUnknown", outcome.State); Assert.Null(outcome.Result); Assert.Null(outcome.Mutation);
        Assert.False(JsonSerializer.SerializeToElement(outcome, Json).GetProperty("artifactPush").GetProperty("remoteSizeVerified").GetBoolean());
        await store.RemoveAsync(receipt.Id);
    }

    [Theory]
    [InlineData("readback-failure")]
    [InlineData("cancelled")]
    [InlineData("marked-pending")]
    public async Task AdmittedFailuresStayUnknownConsumedAndDoNotChangeAfterAFreshRead(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.bin", new MemoryStream([1, 2, 3]));
        var registry = new BrowserCommands((command, _) =>
        {
            if (command.Kind != OperatorCommandKind.PushFile) return Task.FromResult(new OperatorExecutionResult(command));
            if (failure == "cancelled") throw new OperationCanceledException("private-native-output");
            if (failure == "marked-pending") throw new OperatorMutationExecutionException(Receipt(command) with { Stage = OperatorMutationStage.Pending, HeadsetReadback = false }, new IOException("private-native-output"));
            throw new IOException("private-native-output");
        }, () => store);
        var id = Guid.NewGuid().ToString("D"); var body = BrowserArtifactPushCommandsTests.Body(receipt, id);
        var unknown = await registry.ExecuteAsync(body); Assert.Equal("outcomeUnknown", unknown.State); Assert.Null(unknown.Result);
        Assert.False(JsonSerializer.SerializeToElement(unknown, Json).GetProperty("artifactPush").GetProperty("remoteSizeVerified").GetBoolean());
        Assert.DoesNotContain("private-native-output", JsonSerializer.Serialize(unknown));
        if (failure == "marked-pending") Assert.Equal(Marker, unknown.Mutation!.OperationId);
        var fresh = await registry.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new { requestId = Guid.NewGuid().ToString("D"), action = "device.status", serial = BrowserArtifactPushCommandsTests.Serial }));
        Assert.Equal("completed", fresh.State); Assert.Same(unknown, registry.Get(id)); Assert.Equal("outcomeUnknown", registry.Get(id).State);
        Assert.Null((await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(body))).Dispatched); await store.RemoveAsync(receipt.Id);
    }

    [Fact]
    public async Task ValidPendingMarkerNeverClaimsRemoteSizeConfirmation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.bin", new MemoryStream([1, 2, 3]));
        var registry = new BrowserCommands((command, _) => Task.FromResult(Result(command) with { MutationReceipt = Receipt(command) with { Stage = OperatorMutationStage.Pending, HeadsetReadback = false } }), () => store);
        var outcome = await registry.ExecuteAsync(BrowserArtifactPushCommandsTests.Body(receipt));
        Assert.Equal("pending", outcome.State); Assert.Equal(Marker, outcome.Mutation!.OperationId); Assert.Null(outcome.Result);
        Assert.False(JsonSerializer.SerializeToElement(outcome, Json).GetProperty("artifactPush").GetProperty("remoteSizeVerified").GetBoolean());
    }

    [Theory]
    [InlineData("null-id")]
    [InlineData("null-transitions")]
    [InlineData("null-text")]
    public async Task MalformedExceptionReceiptStillPublishesOnlyUnknown(string malformed)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var artifact = await store.StageAsync("fixture.bin", new MemoryStream([1, 2, 3]));
        var registry = new BrowserCommands((command, _) =>
        {
            var pending = Receipt(command) with { Stage = OperatorMutationStage.Pending, HeadsetReadback = false };
            var crossed = malformed switch
            {
                "null-id" => pending with { OperationId = null! },
                "null-transitions" => pending with { Transitions = null! },
                _ => pending with { ObservedState = null! }
            };
            throw new OperatorMutationExecutionException(crossed, new IOException("private-native-output"));
        }, () => store);
        var outcome = await registry.ExecuteAsync(BrowserArtifactPushCommandsTests.Body(artifact));
        Assert.Equal("outcomeUnknown", outcome.State); Assert.Null(outcome.Result); Assert.Null(outcome.Mutation);
        Assert.Same(outcome, registry.Get(outcome.RequestId)); await store.RemoveAsync(artifact.Id);
    }

    [Fact]
    public async Task BusyOrChangedSourceRejectBeforeDispatchAndNeverQueue()
    {
        Assert.DoesNotContain("files.push", new BrowserCommands((_, _) => throw new InvalidOperationException()).AvailableActions);
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.bin", new MemoryStream([1, 2, 3]));
        var gate = new BrowserOperationGate(); var calls = 0;
        var registry = new BrowserCommands((command, _) => { calls++; return Task.FromResult(Result(command)); }, () => store, gate);
        using (gate.TryAcquire())
            Assert.False((await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(BrowserArtifactPushCommandsTests.Body(receipt)))).Dispatched);
        Assert.Equal(0, calls);
        var changed = receipt with { Sha256 = new string('0', 64) };
        Assert.False((await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(BrowserArtifactPushCommandsTests.Body(changed)))).Dispatched);
        Assert.Equal(0, calls); await store.RemoveAsync(receipt.Id);
    }

    [Fact]
    public async Task CancellationDoesNotReleaseGateOrSourceBeforeAnIgnoringRunnerAcknowledges()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.bin", new MemoryStream([1, 2, 3]));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new BrowserOperationGate(); using var lifetime = new CancellationTokenSource();
        var registry = new BrowserCommands(async (_, token) => { started.SetResult(); await acknowledged.Task; token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }, () => store, gate);
        var pending = registry.ExecuteAsync(BrowserArtifactPushCommandsTests.Body(receipt), lifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); lifetime.Cancel(); Assert.True(gate.IsBusy); Assert.False(pending.IsCompleted);
        try { Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => store.RemoveAsync(receipt.Id))).Code); }
        finally { acknowledged.SetResult(); }
        Assert.Equal("outcomeUnknown", (await pending).State); Assert.False(gate.IsBusy); await store.RemoveAsync(receipt.Id);
    }

    private static OperatorMutationReceipt Receipt(OperatorCommand command) => new(Marker, OperatorCommandKind.PushFile, command.Serial!,
        "file present at " + command.RemotePath, OperatorMutationStage.Confirmed, "Remote file size matches the local source. " + command.LocalPath, true,
        [new(OperatorMutationStage.Sent, DateTimeOffset.UtcNow, "source " + command.LocalPath), new(OperatorMutationStage.Pending, DateTimeOffset.UtcNow, "root " + Path.GetDirectoryName(command.LocalPath)),
         new(OperatorMutationStage.Confirmed, DateTimeOffset.UtcNow, "Remote file size matches.")]);
    private static OperatorExecutionResult Result(OperatorCommand command) => new(command,
        CommandResult: new("private-native-output", command.CliArguments, 0, "private-native-output", "", TimeSpan.Zero), MutationReceipt: Receipt(command));
    private static IEnumerable<string> Strings(JsonElement value) => value.ValueKind switch
    { JsonValueKind.String => [value.GetString()!], JsonValueKind.Array => value.EnumerateArray().SelectMany(Strings), JsonValueKind.Object => value.EnumerateObject().SelectMany(property => Strings(property.Value)), _ => [] };
}
