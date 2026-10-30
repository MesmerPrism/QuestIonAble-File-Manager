using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;
using System.Text.Json;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed partial class BrowserDiagnosticsTests
{
    [Fact]
    public void LaunchPreflightKeepsTheExplicitActionAndClosedArtifactBinding()
    {
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            contractVersion = Version, artifactId = new string('3', 32), sha256 = Hash,
            sizeBytes = Bytes.Length, command = "apk.launch-diagnose", serial = Serial
        });
        var parsed = BrowserRetainedOperations.ParseV3Preflight(body);
        Assert.Equal("apk.launch-diagnose", parsed.Command);
        Assert.Equal(Serial, parsed.Serial);
        Assert.Null(parsed.InstallOptions);
    }

    [Fact]
    public void ProductionLaunchCapabilityDescribesConfigurationWithoutOpeningItsOwner()
    {
        var state = Path.Combine(Path.GetTempPath(), "qfm-unopened-launch-" + Guid.NewGuid().ToString("N"));
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException("No store access."),
            null, key => key == LocalApiStateSettings.StateDirectoryEnvironmentVariable ? state :
                key == LocalApiStateSettings.JournalSecretEnvironmentVariable ? new string('k', 32) : null, true);
        var commands = DiagnosticCommands(operations.Capability);
        Assert.Contains("apk.launch-diagnose", commands!);
        Assert.Equal("configured_not_opened", operations.Capability.Code);
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public void ExistingInjectedV3FactoryKeepsAbsentPositiveLaunchCapability()
    {
        var opened = 0;
        using var operations = new BrowserRetainedOperations(() => throw new InvalidOperationException("No store access."),
            () => { opened++; return Owner(); }, supportsV3: true);
        Assert.Null(DiagnosticCommands(operations.Capability));
        Assert.Equal(0, opened);
    }

    [Theory]
    [InlineData("apk.Launch-diagnose")]
    [InlineData("apk.launch-diagnose ")]
    [InlineData("apk.launch-diagnose\n")]
    [InlineData("app.launch-resolved")]
    [InlineData("apk.install-inspected")]
    public void LaunchParserAdmitsOnlyTheTwoExactDiagnosticActions(string command)
    {
        var error = Assert.Throws<BrowserRetainedException>(() => BrowserRetainedOperations.ParseV3Preflight(LaunchPreflight(command: command)));
        Assert.Equal("command_not_allowed", error.Code);
        Assert.Equal(false, error.Dispatched);
    }

    [Fact]
    public void AdditiveCapabilityPreservesConstructorDeconstructionAndAbsentJson()
    {
        var capability = new BrowserRetainedCapability(true, "available", "reason", "alternative", [Version]);
        var (supported, code, reason, alternative, versions) = capability;
        Assert.True(supported); Assert.Equal("available", code); Assert.Equal("reason", reason);
        Assert.Equal("alternative", alternative); Assert.Equal(new[] { Version }, versions);
        Assert.Equal("{\"supported\":true,\"code\":\"available\",\"reason\":\"reason\",\"alternative\":\"alternative\",\"contractVersions\":[\"" + Version + "\"]}",
            JsonSerializer.Serialize(capability, Json));
    }

    [Fact]
    public void DeclaredCapabilityCopiesClosedListsBeforeLazyActivation()
    {
        var configured = new List<string> { "apk.diagnose", "apk.launch-diagnose" };
        var declared = new List<string>(configured);
        var owner = Owner() with { DiagnosticCommands = declared };
        var opened = 0;
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"),
            () => { opened++; return owner; }, null, true, configured);
        configured.Clear(); declared.Clear();
        Assert.Equal(new[] { "apk.diagnose", "apk.launch-diagnose" }, operations.Capability.DiagnosticCommands);
        Assert.Equal(0, opened);
        Assert.Equal("apk.diagnose", operations.GetV3Status(Binding()).Command);
        Assert.Equal(new[] { "apk.diagnose", "apk.launch-diagnose" }, operations.Capability.DiagnosticCommands);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)operations.Capability.DiagnosticCommands!).Clear());
        Assert.Null((new BrowserRetainedCapability(true, "available", "reason", "alternative")
            { DiagnosticCommands = ["apk.diagnose", "apk.deploy"] }).DiagnosticCommands);
        Assert.Null((owner with { DiagnosticCommands = ["apk.launch-diagnose", "apk.launch-diagnose"] }).DiagnosticCommands);
    }

    [Fact]
    public async Task MissingPositiveLaunchCapabilityRejectsBeforeOwnerStoreOrGateActivation()
    {
        var opened = 0; var stores = 0; var gates = 0;
        using var operations = new BrowserRetainedOperations(() => { stores++; throw new Exception("no store"); },
            () => { opened++; return LaunchOwner(); }, () => { gates++; return new Release(() => { }); }, supportsV3: true);
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PreflightV3Async(LaunchPreflight()));
        Assert.Equal("retained_command_unavailable", error.Code); Assert.Equal(501, error.StatusCode); Assert.Equal(false, error.Dispatched);
        Assert.Equal(0, opened); Assert.Equal(0, stores); Assert.Equal(0, gates);
    }

    [Fact]
    public async Task OpenedOwnerCannotInheritConfiguredLaunchAuthorityItDidNotDeclare()
    {
        var executes = 0; var cancels = 0; var acquires = 0;
        var owner = Owner(status: _ => LaunchStatus(),
            execute: (_, _) => { executes++; return Task.FromResult(LaunchStatus()); },
            cancel: _ => { cancels++; return LaunchStatus(); },
            acquire: (_, _, _, _, _, _) => { acquires++; return Task.FromResult(Lease(Bytes)); });
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"), () => owner, null, true,
            ["apk.diagnose", "apk.launch-diagnose"]);
        Assert.Contains("apk.launch-diagnose", operations.Capability.DiagnosticCommands!);
        Assert.Equal("retained_command_unavailable", (await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.ExecuteV3Async(Binding()))).Code);
        Assert.Null(operations.Capability.DiagnosticCommands);
        Assert.Equal("retained_command_unavailable", Assert.Throws<BrowserRetainedException>(() => operations.GetV3Status(Binding())).Code);
        Assert.Equal("retained_command_unavailable", Assert.Throws<BrowserRetainedException>(() => operations.CancelV3(Binding())).Code);
        Assert.Equal("retained_command_unavailable", (await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PrepareDiagnosticOutputAsync(OutputBody()))).Code);
        Assert.Equal(0, executes); Assert.Equal(0, cancels); Assert.Equal(0, acquires);
    }

    [Fact]
    public async Task LaunchPreflightForwardsTheExactActionAndBindsItsOwnOutputPolicy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var source = await store.StageAsync("base.apk", new MemoryStream(Bytes));
        var calls = 0;
        using var operations = new BrowserRetainedOperations(() => store, LaunchOwner(preflight: (body, _) =>
        {
            calls++; using var json = JsonDocument.Parse(body);
            Assert.Equal("apk.launch-diagnose", json.RootElement.GetProperty("command").GetString());
            Assert.Equal(Serial, json.RootElement.GetProperty("serial").GetString());
            Assert.Equal(new[] { "contractVersion", "command", "apkPath", "serial", "expiresInSeconds" }, json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.True(File.Exists(json.RootElement.GetProperty("apkPath").GetString()));
            return Task.FromResult(LaunchPrepared(source));
        }));
        var result = await operations.PreflightV3Async(LaunchPreflight(source));
        result.Validate(); Assert.Equal("apk.launch-diagnose", result.Command); Assert.Equal(1, calls);
        Assert.Equal("exact-installed-launch-diagnostic-output.v1", result.OutputIntent.PolicyVersion);
        Assert.DoesNotContain("apkPath", JsonSerializer.Serialize(result, Json));
        await store.RemoveAsync(source.Id);
    }

    [Fact]
    public async Task ConfiguredLaunchPreflightRejectsAnOpenedReadOnlyOwnerBeforeNativePreparation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var source = await store.StageAsync("base.apk", new MemoryStream(Bytes));
        var nativeCalls = 0;
        using var operations = new BrowserRetainedOperations(() => store,
            () => Owner(preflight: (_, _) => { nativeCalls++; return Task.FromResult(LaunchPrepared(source)); }), null, true,
            ["apk.diagnose", "apk.launch-diagnose"]);
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PreflightV3Async(LaunchPreflight(source)));
        Assert.Equal("retained_command_unavailable", error.Code); Assert.Equal(false, error.Dispatched); Assert.Equal(0, nativeCalls);
        await store.RemoveAsync(source.Id);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("policy")]
    [InlineData("digest")]
    [InlineData("size")]
    [InlineData("sha")]
    [InlineData("serial")]
    public async Task LaunchPreflightRejectsCrossedActionArtifactAndIntent(string field)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var source = await store.StageAsync("base.apk", new MemoryStream(Bytes));
        var response = LaunchPrepared(source);
        response = field switch
        {
            "action" => Prepared(source.SizeBytes, source.Sha256),
            "policy" => response with { OutputIntent = Intent() },
            "digest" => response with { OutputIntent = response.OutputIntent with { PlannedLeafDigest = Plan } },
            "size" => response with { Artifact = response.Artifact with { SizeBytes = source.SizeBytes + 1 } },
            "sha" => response with { Artifact = response.Artifact with { Sha256 = new string('e', 64) } },
            _ => response with { Serial = "OTHER" }
        };
        using var operations = new BrowserRetainedOperations(() => store, LaunchOwner(preflight: (_, _) => Task.FromResult(response)));
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PreflightV3Async(LaunchPreflight(source)));
        Assert.Contains(error.Code, new[] { "artifact_binding_mismatch", "retained_operation_binding_invalid" });
        Assert.Null(error.Dispatched);
        await store.RemoveAsync(source.Id);
    }

    [Theory]
    [InlineData("execute")]
    [InlineData("cancel")]
    public async Task LaunchLifecycleRejectsAValidReadOnlyReplyCrossingTheOriginalAction(string route)
    {
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"),
            LaunchOwner(execute: (_, _) => Task.FromResult(Status(LocalApiOperationStage.Completed, true)),
                cancel: _ => Status(LocalApiOperationStage.CancellationRequested)));
        var error = route == "execute"
            ? await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.ExecuteV3Async(Binding()))
            : Assert.Throws<BrowserRetainedException>(() => operations.CancelV3(Binding()));
        Assert.Equal("retained_operation_binding_invalid", error.Code); Assert.Null(error.Dispatched);
    }

    [Theory]
    [InlineData("execute")]
    [InlineData("cancel")]
    public async Task ReadOnlyLifecycleCannotTurnIntoAnOtherwiseValidLaunchReply(string route)
    {
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"),
            LaunchOwner(status: _ => Status(), execute: (_, _) => Task.FromResult(LaunchStatus("completed", true)),
                cancel: _ => LaunchStatus()));
        var error = route == "execute"
            ? await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.ExecuteV3Async(Binding()))
            : Assert.Throws<BrowserRetainedException>(() => operations.CancelV3(Binding()));
        Assert.Equal("retained_operation_binding_invalid", error.Code); Assert.Null(error.Dispatched);
    }

    [Fact]
    public async Task LaunchExecuteAndCancellationKeepOnlyTheOriginalVersionIdAndDigest()
    {
        var sent = 0; var cancelled = 0;
        void Check(ReadOnlyMemory<byte> body)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(new[] { "contractVersion", "operationId", "commandDigest" }, json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(Version, json.RootElement.GetProperty("contractVersion").GetString());
            Assert.Equal(Operation, json.RootElement.GetProperty("operationId").GetString());
            Assert.Equal(Digest, json.RootElement.GetProperty("commandDigest").GetString());
        }
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no source"), LaunchOwner(
            status: body => { Check(body); return LaunchStatus(); },
            execute: (body, token) => { Check(body); Assert.False(token.CanBeCanceled); sent++; return Task.FromResult(LaunchStatus("launchPending", true)); },
            cancel: body => { Check(body); cancelled++; return LaunchStatus() with { Stage = LocalApiOperationStage.CancellationRequested, Consumed = true }; }));
        var pending = await operations.ExecuteV3Async(Binding());
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, pending.Stage); Assert.True(pending.Output.Available);
        Assert.Equal(LocalApiOperationStage.CancellationRequested, operations.CancelV3(Binding()).Stage);
        Assert.Equal(1, sent); Assert.Equal(1, cancelled);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("launchPending")]
    [InlineData("rejectedBeforeDispatch")]
    [InlineData("outcomeUnknown")]
    public async Task ProvenLaunchArchivesRemainExplicitLocalCopiesWithoutLaunchOrCapture(string disposition)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var releases = 0; var leases = 0;
        var status = LaunchStatus(disposition, true);
        using var operations = new BrowserRetainedOperations(() => store, LaunchOwner(status: _ => status,
            execute: (_, _) => throw new Exception("output must never launch"),
            acquire: (id, digest, output, size, hash, _) =>
            {
                leases++; Assert.Equal(Operation, id); Assert.Equal(Digest, digest); Assert.Equal(Output, output);
                Assert.Equal(Bytes.Length, size); Assert.Equal(Hash, hash);
                return Task.FromResult(Lease(Bytes, () => releases++));
            }));
        Assert.Equal(status, operations.GetV3Status(Binding()));
        var copy = await operations.PrepareDiagnosticOutputAsync(OutputBody());
        Assert.False(copy.DeviceDispatched); Assert.Equal("localOnly", copy.Scope); Assert.Equal(1, leases); Assert.Equal(1, releases);
        Assert.Equal(status, operations.GetV3Status(Binding()));
        await using (var lease = await store.AcquireAsync(copy.Artifact.Id, Hash, Bytes.Length))
            Assert.Equal(Bytes, await ReadAll(lease.Stream));
        await store.RemoveAsync(copy.Artifact.Id);
        Assert.Equal(status, operations.GetV3Status(Binding()));
    }

    [Theory]
    [InlineData("proof")]
    [InlineData("mutation")]
    [InlineData("stage")]
    [InlineData("policy")]
    [InlineData("marker")]
    [InlineData("readback")]
    [InlineData("private")]
    public async Task LaunchOutputRejectsCrossedOrPrivateProofBeforeAcquiringAnyLease(string field)
    {
        var status = LaunchStatus("launchPending", true);
        status = field switch
        {
            "proof" => status with { LaunchSummary = null },
            "mutation" => status with { Mutation = null },
            "stage" => status with { Stage = LocalApiOperationStage.Completed },
            "policy" => status with { Output = status.Output with { Intent = Intent() } },
            "marker" => status with { LaunchMayHaveDispatched = false },
            "readback" => status with { Mutation = status.Mutation! with { HeadsetReadback = false } },
            _ => status with { Mutation = status.Mutation! with { ObservedState = "PRIVATE_CAPTURE_PATH" } }
        };
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"), LaunchOwner(status: _ => status,
            acquire: (_, _, _, _, _, _) => throw new Exception("invalid proof must never acquire")));
        var error = await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PrepareDiagnosticOutputAsync(OutputBody()));
        Assert.Equal("retained_operation_binding_invalid", error.Code); Assert.Null(error.Dispatched);
        Assert.DoesNotContain("PRIVATE", error.Message);
    }

    [Fact]
    public async Task SafeLaunchStatusWireKeepsEffectCaptureAndArchiveFactsIndependent()
    {
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"), LaunchOwner(status: _ => LaunchStatus("launchPending", true)));
        var context = Context(Binding()); await BrowserRetainedRoutes.InvokeV3Async(context, operations, "status");
        Assert.Equal(200, context.Response.StatusCode);
        using var document = JsonDocument.Parse(Response(context)); var root = document.RootElement;
        Assert.Equal("outcomeUnknownRecoveryRequired", root.GetProperty("stage").GetString());
        Assert.True(root.GetProperty("output").GetProperty("available").GetBoolean());
        Assert.True(root.GetProperty("launchMayHaveDispatched").GetBoolean());
        var summary = root.GetProperty("launchSummary");
        Assert.Equal("launchPending", summary.GetProperty("disposition").GetString());
        Assert.True(summary.GetProperty("dispatchAttempted").GetBoolean());
        Assert.Equal("pending", root.GetProperty("mutation").GetProperty("stage").GetString());
        foreach (var axis in new[] { "applicationReadiness", "openXrReadiness", "wearerVisibility" })
        { Assert.Equal("unknown", summary.GetProperty(axis).GetString()); Assert.False(summary.GetProperty(axis + "Authority").GetBoolean()); }
        Assert.DoesNotContain("apkPath", Response(context)); Assert.DoesNotContain("pid", Response(context), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uid", Response(context), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmedLaunchWithUnavailableArchiveCannotExecuteAgainOrRecaptureForOutput()
    {
        var value = LaunchStatus("completed"); var executes = 0; var acquires = 0;
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no store"), LaunchOwner(status: _ => value,
            execute: (_, _) => { executes++; return Task.FromResult(value); },
            acquire: (_, _, _, _, _, _) => { acquires++; return Task.FromResult(Lease(Bytes)); }));
        Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.ExecuteV3Async(Binding()))).Code);
        Assert.Equal("diagnostic_output_unavailable", (await Assert.ThrowsAsync<BrowserRetainedException>(() => operations.PrepareDiagnosticOutputAsync(OutputBody()))).Code);
        Assert.Equal(0, executes); Assert.Equal(0, acquires); Assert.Equal(value, operations.GetV3Status(Binding()));
    }

    [Fact]
    public async Task LostLaunchReplyKeepsTheGateAndDrainUntilTheOwnerAcknowledgesUnknown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<LocalApiV3OperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new BrowserOperationGate(); var executions = 0;
        using var operations = new BrowserRetainedOperations(() => throw new Exception("no source"), LaunchOwner(execute: (_, token) =>
        { Assert.False(token.CanBeCanceled); executions++; entered.SetResult(); return acknowledged.Task; }), gate.TryAcquire);
        using var stopping = new CancellationTokenSource();
        var pending = operations.ExecuteV3Async(Binding(), stopping.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stopping.Cancel();
            Assert.False(pending.IsCompleted); Assert.True(gate.IsBusy);
            var drain = operations.DrainAsync(); Assert.False(drain.IsCompleted);
            acknowledged.SetResult(LaunchStatus("outcomeUnknown"));
            var result = await pending; await drain;
            Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, result.Stage);
            Assert.True(result.LaunchMayHaveDispatched); Assert.False(gate.IsBusy); Assert.Equal(1, executions);
        }
        finally { acknowledged.TrySetResult(LaunchStatus("outcomeUnknown")); await pending; }
    }

    private static async Task<byte[]> ReadAll(Stream stream)
    { using var memory = new MemoryStream(); await stream.CopyToAsync(memory); return memory.ToArray(); }

    private static byte[] LaunchPreflight(BrowserArtifactReceipt? source = null, string command = "apk.launch-diagnose") =>
        JsonSerializer.SerializeToUtf8Bytes(new { contractVersion = Version, artifactId = source?.Id ?? new string('3', 32),
            sha256 = source?.Sha256 ?? Hash, sizeBytes = source?.SizeBytes ?? Bytes.Length, command, serial = Serial });

    private static LocalApiV3OutputIntent LaunchIntent() => Intent() with
    {
        PolicyVersion = "exact-installed-launch-diagnostic-output.v1",
        PlannedLeafDigest = "0cea6ab058514a8569d577782ebccf4cbd546a10c99f3d317cfbfd5b251f71db",
        Limits = Intent().Limits with { MaximumArmedCaptureBytes = 262144, PostActionWindowMilliseconds = 10000, MaximumLaunchOperationSeconds = 300 }
    };

    private static LocalApiV3PreflightResult LaunchPrepared(BrowserArtifactReceipt source) => Prepared(source.SizeBytes, source.Sha256) with
    { Command = "apk.launch-diagnose", OutputIntent = LaunchIntent() };

    private static LocalApiV3OperationStatus LaunchStatus(string? disposition = null, bool available = false)
    {
        var stage = disposition switch
        {
            "completed" => LocalApiOperationStage.Completed,
            "rejectedBeforeDispatch" => LocalApiOperationStage.Failed,
            null => LocalApiOperationStage.Preflighted,
            _ => LocalApiOperationStage.OutcomeUnknownRecoveryRequired
        };
        var value = new LocalApiV3OperationStatus(Version, Operation, "apk.launch-diagnose", Digest, Expires, stage,
            disposition is not null, Serial, new(LaunchIntent(), available ? "available" : disposition is null ? "preflighted" : "unavailable",
                available, 1, 0, Archive: available ? Descriptor() : null)) { LaunchMayHaveDispatched = disposition is not null };
        if (disposition is null) return value;
        var rejected = disposition == "rejectedBeforeDispatch";
        var confirmed = disposition == "completed";
        var mutationStage = rejected ? OperatorMutationStage.Rejected : confirmed ? OperatorMutationStage.Confirmed : OperatorMutationStage.Pending;
        var transitions = rejected
            ? new[] { new OperatorMutationTransition(OperatorMutationStage.Rejected, DateTimeOffset.UnixEpoch, "launch-rejected") }
            : new[] { new OperatorMutationTransition(OperatorMutationStage.Sent, DateTimeOffset.UnixEpoch, "launch-sent"),
                new OperatorMutationTransition(OperatorMutationStage.Pending, DateTimeOffset.UnixEpoch, "launch-pending") }
                .Concat(confirmed ? new[] { new OperatorMutationTransition(OperatorMutationStage.Confirmed, DateTimeOffset.UnixEpoch, "launch-confirmed") } : []).ToArray();
        return value with
        {
            LaunchSummary = new("questionable.file_manager.apk_launch_diagnostic_bundle.v1", DateTimeOffset.UnixEpoch,
                disposition, true, !rejected, true, false, false, true, 0, 42, confirmed ? 1 : 0),
            Mutation = new("pc-" + new string('5', 32), OperatorCommandKind.LaunchDiagnoseInspectedApp, Serial, "launch-diagnose", mutationStage,
                rejected ? "launch-rejected" : confirmed ? "launch-confirmed" : "launch-pending", true, transitions)
        };
    }

    private static BrowserRetainedOwnerCalls LaunchOwner(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiV3PreflightResult>>? preflight = null,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<LocalApiV3OperationStatus>>? execute = null,
        Func<ReadOnlyMemory<byte>, LocalApiV3OperationStatus>? status = null,
        Func<ReadOnlyMemory<byte>, LocalApiV3OperationStatus>? cancel = null,
        Func<string, string, string, long, string, CancellationToken, Task<BrowserRetainedOutputLease>>? acquire = null) =>
        Owner(preflight, execute ?? ((_, _) => Task.FromResult(LaunchStatus("completed", true))), status ?? (_ => LaunchStatus()), cancel, acquire)
            with { DiagnosticCommands = ["apk.diagnose", "apk.launch-diagnose"] };

    private static IReadOnlyList<string>? DiagnosticCommands(BrowserRetainedCapability value)
    {
        var property = typeof(BrowserRetainedCapability).GetProperty("DiagnosticCommands");
        Assert.NotNull(property);
        return (IReadOnlyList<string>?)property.GetValue(value);
    }
}
