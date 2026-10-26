using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserFleetTests
{
    private const string Private = "PRIVATE_PATH_SENTINEL";
    private static byte[] Body(string? id = null, string action = "fleet.status") => JsonSerializer.SerializeToUtf8Bytes(new
    { requestId = id ?? Guid.NewGuid().ToString("D"), action });
    private static FleetInstallerStatusReceipt Ready(string channel = "labs", string status = "ready") => new(
        FleetInstallerContract.StatusSchema, status, true, "embedded_pages_metadata",
        channel == "labs" ? "rusty-fleet-labs" : "rusty-fleet", "1.2.3", channel,
        "release_2026.10-26", new string('a', 64), "1.0.0", "guided_installer_completed", 1800000000000);
    private static BrowserCommands Broker(BrowserFleet fleet, BrowserOperationGate? gate = null) =>
        new((_, _) => throw new InvalidOperationException("ADB activation forbidden"), operationGate: gate, fleetStatus: fleet);

    [Fact]
    public void CapabilityAndParserAreInertAndStatusFactoryIsExact()
    {
        var calls = 0;
        var fleet = new BrowserFleet((_, _) => { calls++; throw new InvalidOperationException(); }, true);
        Assert.True(fleet.Capability.Supported); Assert.Equal("platform_supported", fleet.Capability.Code);
        var request = BrowserFleetCommands.Parse(Body());
        var command = request.ToOperatorCommand();
        Assert.Equal(OperatorCommandKind.FleetInstallStatus, command.Kind);
        Assert.Equal(new[] { "fleet", "status", "--json" }, command.CliArguments);
        Assert.Null(command.Serial); Assert.False(command.OperatorConfirmed);
        Assert.Equal(new[] { "fleet.status" }, BrowserFleetCommands.Actions);
        Assert.Contains("fleet.status", Broker(fleet).AvailableActions);
        Assert.DoesNotContain("fleet.install", Broker(fleet).AvailableActions); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("confirmed", "true")]
    [InlineData("serial", "\"QUEST123\"")]
    [InlineData("productChannel", "\"labs\"")]
    [InlineData("descriptorId", "\"release\"")]
    [InlineData("url", "\"https://example.invalid\"")]
    [InlineData("path", "\"PRIVATE_PATH_SENTINEL\"")]
    [InlineData("arguments", "[]")]
    [InlineData("elevated", "true")]
    public void StatusBodyRejectsEveryAdditionalAuthority(string field, string json)
    {
        var body = Encoding.UTF8.GetString(Body());
        var exception = Assert.Throws<BrowserRequestException>(() => BrowserFleetCommands.Parse(Encoding.UTF8.GetBytes(body[..^1] + ",\"" + field + "\":" + json + "}")));
        Assert.Equal("fields_invalid", exception.Code);
    }

    [Theory]
    [InlineData("fleet.install")]
    [InlineData("fleet.repair")]
    [InlineData("fleet.Status")]
    public async Task NoOtherFleetActionCanActivateTheOwner(string action)
    {
        var calls = 0;
        var broker = Broker(new((_, _) => { calls++; throw new InvalidOperationException(); }, true));
        Assert.Equal("action_unsupported", (await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(Body(action: action)))).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DuplicateCaseAndCanonicalRequestBoundsRemainStrict()
    {
        var body = Encoding.UTF8.GetString(Body());
        Assert.Equal("duplicate_field", Assert.Throws<BrowserRequestException>(() => BrowserFleetCommands.Parse(
            Encoding.UTF8.GetBytes(body[..^1] + ",\"action\":\"fleet.status\"}"))).Code);
        Assert.Throws<BrowserRequestException>(() => BrowserFleetCommands.Parse(Encoding.UTF8.GetBytes(body.Replace("requestId", "RequestId", StringComparison.Ordinal))));
        foreach (var id in new[] { "00000000-0000-0000-0000-000000000000", "ABCDEFAB-1234-4234-8234-123456789ABC", "invalid" })
            Assert.Equal("request_id_invalid", Assert.Throws<BrowserRequestException>(() => BrowserFleetCommands.Parse(Body(id))).Code);
        Assert.Equal("body_size_invalid", Assert.Throws<BrowserRequestException>(() => BrowserFleetCommands.Parse(new byte[4097])).Code);
    }

    [Fact]
    public async Task UnsupportedAndUncomposedCapabilitiesNeverTouchTheOwnerOrAdb()
    {
        var calls = 0; var fleet = new BrowserFleet((_, _) => { calls++; throw new InvalidOperationException(); }, false);
        Assert.Equal("windows_fleet_installer_required", fleet.Capability.Code);
        var broker = Broker(fleet); Assert.DoesNotContain("fleet.status", broker.AvailableActions);
        Assert.Equal("windows_fleet_installer_required", (await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(Body()))).Code);
        var missing = new BrowserCommands((_, _) => throw new InvalidOperationException("No ADB"));
        Assert.Equal("fleet_status_unavailable", (await Assert.ThrowsAsync<BrowserRequestException>(() => missing.ExecuteAsync(Body()))).Code);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("stable", "ready")]
    [InlineData("labs", "ready")]
    [InlineData("labs", "already_handed_off")]
    [InlineData("labs", "not_newer_than_last_handoff")]
    public async Task ExactConfiguredStatusIsOnlyAReadProjection(string channel, string status)
    {
        var broker = Broker(new((command, _) => Task.FromResult(new OperatorExecutionResult(command, FleetInstallerStatus: Ready(channel, status))), true));
        var result = await broker.ExecuteAsync(Body());
        Assert.Equal("completed", result.State); Assert.Null(result.Result); Assert.Null(result.Mutation); Assert.Null(result.Error);
        Assert.Equal("status", result.Fleet!.Operation); Assert.Equal(status, result.Fleet.Status.Status);
        Assert.Equal(channel, result.Fleet.Status.Channel); Assert.Equal("release_2026.10-26", result.Fleet.Status.DescriptorId);
        Assert.Same(result, broker.Get(result.RequestId));
    }

    [Fact]
    public async Task ExplicitlyInjectedUnconfiguredCoreOwnerDoesNotNeedAdbProfileNetworkOrReplayState()
    {
        var executor = new OperatorCommandExecutor(client: null, new FleetInstallerHandoff(null));
        var broker = Broker(new((command, token) => executor.ExecuteAsync(command, token), true));
        var result = await broker.ExecuteAsync(Body());
        Assert.Equal("completed", result.State); var status = result.Fleet!.Status;
        Assert.False(status.Configured); Assert.Equal("not_configured", status.Status); Assert.Equal("none", status.SourceKind);
        Assert.Null(status.Product); Assert.Null(status.Version); Assert.Null(status.Channel); Assert.Null(status.DescriptorId);
        Assert.Null(status.DescriptorSha256); Assert.Null(status.HighestHandoffVersion); Assert.Null(status.LastOutcome);
    }

    [Fact]
    public async Task ArbitraryLocalHistoryNeverEscapesTheWhitelist()
    {
        var broker = Broker(new((command, _) => Task.FromResult(new OperatorExecutionResult(command, FleetInstallerStatus: Ready() with { LastOutcome = Private })), true));
        var result = await broker.ExecuteAsync(Body()); Assert.Equal("completed", result.State);
        Assert.Equal("unknown", result.Fleet!.Status.LastOutcome); Assert.DoesNotContain(Private, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("status")]
    [InlineData("source")]
    [InlineData("product")]
    [InlineData("channel")]
    [InlineData("version")]
    [InlineData("descriptor")]
    [InlineData("hash")]
    [InlineData("highest")]
    [InlineData("time")]
    [InlineData("unconfigured")]
    [InlineData("command")]
    [InlineData("raw")]
    [InlineData("missing")]
    public async Task MalformedOrCrossedOwnerFactsDoNotPublishTypedStatus(string field)
    {
        var broker = Broker(new((command, _) =>
        {
            var status = field switch
            {
                "schema" => Ready() with { Schema = Private }, "status" => Ready() with { Status = Private },
                "source" => Ready() with { SourceKind = Private }, "product" => Ready() with { Product = "rusty-fleet" },
                "channel" => Ready() with { Channel = "other" }, "version" => Ready() with { Version = "01.2.3" },
                "descriptor" => Ready() with { DescriptorId = "file:///" + Private }, "hash" => Ready() with { DescriptorSha256 = new string('A', 64) },
                "highest" => Ready() with { HighestHandoffVersion = Private }, "time" => Ready() with { ObservedAtMs = -1 },
                "unconfigured" => Ready() with { Configured = false }, _ => Ready()
            };
            return Task.FromResult(new OperatorExecutionResult(field == "command" ? OperatorCommands.FleetInstallStatus() : command,
                CommandResult: field == "raw" ? new("", [], 0, Private, Private, TimeSpan.Zero) : null,
                FleetInstallerStatus: field == "missing" ? null : status));
        }, true));
        var result = await broker.ExecuteAsync(Body()); Assert.Equal("failed", result.State);
        Assert.Equal("fleet_status_binding_invalid", result.Error); Assert.Null(result.Fleet); Assert.Null(result.Result); Assert.Null(result.Mutation);
        Assert.DoesNotContain(Private, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("fleet_installer_configuration_incomplete", "fleet_configuration_invalid")]
    [InlineData("fleet_embedded_configuration_invalid", "fleet_configuration_invalid")]
    [InlineData("fleet_descriptor_signature_invalid", "fleet_release_rejected")]
    [InlineData("fleet_source_failed", "fleet_release_unavailable")]
    [InlineData("fleet_installer_recovery_required", "fleet_replay_unavailable")]
    [InlineData("fleet_installer_initialization_unavailable", "fleet_replay_unavailable")]
    [InlineData("PRIVATE_PATH_SENTINEL", "fleet_status_unavailable")]
    public async Task OwnerErrorsAreCachedFixedReadFailures(string code, string expected)
    {
        var broker = Broker(new((_, _) => throw new FleetInstallerException(code, Private), true));
        var result = await broker.ExecuteAsync(Body()); Assert.Equal("failed", result.State); Assert.Equal(expected, result.Error);
        Assert.Null(result.Fleet); Assert.Null(result.Result); Assert.DoesNotContain(Private, JsonSerializer.Serialize(result));
        var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(Body(result.RequestId)));
        Assert.Equal("request_already_consumed", duplicate.Code); Assert.Null(duplicate.Dispatched);
    }

    [Fact]
    public async Task SharedGateAndApplicationCancellationAwaitActualReadCompletion()
    {
        var gate = new BrowserOperationGate(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        var calls = 0;
        var broker = Broker(new(async (_, token) =>
        { calls++; entered.SetResult(); await release.Task; token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }, true), gate);
        var body = Body(); var pending = broker.ExecuteAsync(body, stopping.Token); await entered.Task;
        stopping.Cancel(); Assert.False(pending.IsCompleted); Assert.True(gate.IsBusy);
        var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(body)); Assert.Null(duplicate.Dispatched);
        Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(Body()))).Code);
        release.SetResult(); var result = await pending;
        Assert.Equal("failed", result.State); Assert.Equal("fleet_status_cancelled", result.Error); Assert.False(gate.IsBusy); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExistingUnknownMutationIsNotReconciledByAReleaseStatus()
    {
        var fleet = new BrowserFleet((command, _) => Task.FromResult(new OperatorExecutionResult(command, FleetInstallerStatus: Ready())), true);
        var broker = new BrowserCommands((_, _) => throw new IOException(Private), fleetStatus: fleet);
        var original = await broker.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new
        { requestId = Guid.NewGuid().ToString("D"), action = "device.normal", serial = "QUEST123", confirmed = true }));
        Assert.Equal("outcomeUnknown", original.State);
        Assert.Equal("completed", (await broker.ExecuteAsync(Body())).State);
        Assert.Same(original, broker.Get(original.RequestId));
    }

    [Fact]
    public async Task CapacityNeverActivatesAnotherReadAndConsumedIdStillWins()
    {
        var calls = 0;
        var broker = Broker(new((command, _) => { calls++; return Task.FromResult(new OperatorExecutionResult(command, FleetInstallerStatus: Ready())); }, true));
        var first = Body(); await broker.ExecuteAsync(first);
        for (var index = 1; index < BrowserCommands.MaximumRequests; index++) await broker.ExecuteAsync(Body());
        Assert.Equal("session_capacity_reached", (await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(Body()))).Code);
        var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(first));
        Assert.Null(duplicate.Dispatched); Assert.Equal(BrowserCommands.MaximumRequests, calls);
    }
}
