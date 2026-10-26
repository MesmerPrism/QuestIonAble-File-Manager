using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserKioskBrokerTests
{
    [Fact]
    public async Task InstallationUsesTypedHostAdbWithoutArtifactStoreAndRejectsMissingProof()
    {
        OperatorCommand? observed = null;
        var broker = new BrowserCommands((command, _) =>
        {
            observed = command;
            return Task.FromResult(new OperatorExecutionResult(command));
        });
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            requestId = Guid.NewGuid().ToString("D"), action = "kiosk.installation", serial = "fixture-target", productChannel = "labs"
        });
        var outcome = await broker.ExecuteAsync(body);
        Assert.NotNull(observed);
        Assert.Equal("InspectRustyKioskInstallation", observed.Kind.ToString());
        Assert.Equal("fixture-target", observed.Serial);
        Assert.Equal("labs", observed.RustyKioskProduct!.WireName);
        Assert.Equal("failed", outcome.State);
        Assert.Null(outcome.Result);
        Assert.Contains("kiosk.installation", broker.AvailableActions);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("labs")]
    public async Task PassiveInstallationPublishesOnlyBoundInstallationFactsWithoutAnActivity(string channel)
    {
        var broker = new BrowserCommands((command, _) => Task.FromResult(Reply(command)),
            artifactStore: () => throw new InvalidOperationException("No staged source belongs to Kiosk installation."));
        var outcome = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.installation", channel: channel));
        Assert.Equal("completed", outcome.State); Assert.Null(outcome.Result); Assert.Null(outcome.Mutation);
        Assert.Equal(channel, outcome.Kiosk!.ProductChannel); Assert.Equal("installation", outcome.Kiosk.Operation);
        Assert.Equal("installationOnly", outcome.Kiosk.ProviderState); Assert.Null(outcome.Kiosk.ProviderRequestId);
        Assert.False(outcome.Kiosk.ActivityOpenRequested); Assert.Null(outcome.Kiosk.Result);
        Assert.True(outcome.Kiosk.Installation!.MainInstalled); Assert.False(outcome.Kiosk.OriginalFactsOnly);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("labs")]
    public async Task CatalogAndSelectionPublishOnlyReviewedBoundFactsAndSanitizedMarkers(string channel)
    {
        var calls = new List<OperatorCommand>();
        var broker = new BrowserCommands((command, _) => { calls.Add(command); return Task.FromResult(Reply(command)); });
        var catalog = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open", channel: channel));
        Assert.Equal("completed", catalog.State); Assert.Null(catalog.Result);
        Assert.Equal(catalog.RequestId, catalog.Kiosk!.CatalogRequestId);
        Assert.True(catalog.Kiosk.Result!.Entries.Single().Selectable);
        var selected = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.select", channel: channel, catalog: catalog.RequestId));
        Assert.Equal("completed", selected.State); Assert.Equal(BrowserKioskCommandsTests.Key, selected.Kiosk!.Result!.SelectedKey);
        Assert.Equal(BrowserKioskCommandsTests.PackageName, selected.Kiosk.ReviewedPackage);
        Assert.Equal(catalog.RequestId, selected.Kiosk.CatalogRequestId);
        Assert.Equal(OperatorMutationStage.Confirmed, selected.Mutation!.Stage);
        Assert.Equal(OperatorCommandKind.SelectRustyKioskApp, selected.Mutation.CommandKind);
        Assert.Equal(BrowserKioskCommandsTests.Serial, selected.Mutation.Target);
        Assert.Equal("pc-" + Guid.Parse(selected.RequestId).ToString("N"), selected.Kiosk.ProviderRequestId);
        Assert.Equal(2, calls.Count); Assert.All(calls, command => Assert.Equal(channel, command.RustyKioskProduct!.WireName));
        var json = JsonSerializer.Serialize(selected);
        foreach (var excluded in new[] { "PRIVATE-RAW", "PRIVATE-MESSAGE", "PRIVATE-STATUS", "PRIVATE-TAGS", "native.exe", "--serial", "TagFilePath", "LaunchOptions" })
            Assert.DoesNotContain(excluded, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE-MARKER", json); Assert.Null(selected.Result);
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("channel")]
    [InlineData("provider")]
    [InlineData("expected-command")]
    [InlineData("value")]
    [InlineData("result-id")]
    [InlineData("result-command")]
    [InlineData("schema")]
    [InlineData("command-target")]
    [InlineData("marker-target")]
    [InlineData("marker-kind")]
    [InlineData("marker-id")]
    [InlineData("marker-readback")]
    [InlineData("no-installation")]
    public async Task CrossedMutationResultsNeverProduceCompletedCatalog(string mismatch)
    {
        var broker = new BrowserCommands((command, _) =>
        {
            var reply = Reply(command); var facts = reply.RustyKioskAdbResult!;
            return Task.FromResult(mismatch switch
            {
                "serial" => reply with { RustyKioskAdbResult = facts with { Serial = "other-target" } },
                "channel" => reply with { RustyKioskAdbResult = facts with { ProductChannel = "stable" } },
                "provider" => reply with { RustyKioskAdbResult = facts with { ProviderRequestId = "pc-00000000000040008000000000000009" } },
                "expected-command" => reply with { RustyKioskAdbResult = facts with { ExpectedCommand = RustyKioskCommand.Select } },
                "value" => reply with { RustyKioskAdbResult = facts with { Value = "other" } },
                "result-id" => reply with { RustyKioskAdbResult = facts with { Result = facts.Result! with { RequestId = "pc-00000000000040008000000000000009" } } },
                "result-command" => reply with { RustyKioskAdbResult = facts with { Result = facts.Result! with { Command = RustyKioskCommand.LaunchNormal } } },
                "schema" => reply with { RustyKioskAdbResult = facts with { Result = facts.Result! with { Schema = "unknown" } } },
                "command-target" => reply with { Command = OperatorCommands.OpenRustyKioskCatalog("other-target", command.RustyKioskProviderRequestId!, true, command.RustyKioskProduct) },
                "marker-target" => reply with { MutationReceipt = reply.MutationReceipt! with { Target = "other-target" } },
                "marker-kind" => reply with { MutationReceipt = reply.MutationReceipt! with { CommandKind = OperatorCommandKind.InvokeRustyKiosk } },
                "marker-id" => reply with { MutationReceipt = reply.MutationReceipt! with { OperationId = "invented" } },
                "marker-readback" => reply with { MutationReceipt = reply.MutationReceipt! with { HeadsetReadback = false } },
                _ => reply with { RustyKioskAdbResult = facts with { Installation = null } }
            });
        });
        var outcome = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open"));
        Assert.Equal("outcomeUnknown", outcome.State); Assert.Null(outcome.Result);
        Assert.NotNull(outcome.Kiosk); Assert.NotNull(outcome.Error);
        Assert.Same(outcome, broker.Get(outcome.RequestId));
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("channel")]
    [InlineData("missing")]
    [InlineData("uninstalled")]
    [InlineData("unlaunchable")]
    [InlineData("duplicate")]
    [InlineData("invalid-package")]
    public async Task SelectionRequiresTheSameCompletedEligibleCatalogRowBeforeAnyDispatch(string mismatch)
    {
        var calls = 0;
        var broker = new BrowserCommands((command, _) =>
        {
            calls++; var reply = Reply(command);
            var rows = reply.RustyKioskAdbResult!.Result!.State.Entries;
            var row = rows.Single();
            var changed = mismatch switch
            {
                "uninstalled" => new[] { row with { Installed = false } },
                "unlaunchable" => new[] { row with { Launchable = false } },
                "duplicate" => new[] { row, row },
                "invalid-package" => new[] { row with { PackageName = null } },
                _ => rows
            };
            return Task.FromResult(reply with { RustyKioskAdbResult = reply.RustyKioskAdbResult with
                { Result = reply.RustyKioskAdbResult.Result! with { State = reply.RustyKioskAdbResult.Result.State with { Entries = changed } } } });
        });
        var catalog = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open"));
        Assert.Equal("completed", catalog.State);
        var request = BrowserKioskCommandsTests.Body("kiosk.select", catalog: mismatch == "missing" ? Guid.NewGuid().ToString("D") : catalog.RequestId,
            channel: mismatch == "channel" ? "stable" : "labs", serial: mismatch == "serial" ? "other-target" : null);
        var rejected = await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(request));
        Assert.False(rejected.Dispatched); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("selected-key")]
    [InlineData("selected-package")]
    [InlineData("selected-installed")]
    [InlineData("selected-launchable")]
    public async Task ChangedSelectionReadbackRemainsUnknownAndConsumed(string mismatch)
    {
        var broker = new BrowserCommands((command, _) =>
        {
            var reply = Reply(command);
            if (command.Kind == OperatorCommandKind.SelectRustyKioskApp)
            {
                var state = reply.RustyKioskAdbResult!.Result!.State;
                state = mismatch switch
                {
                    "selected-key" => state with { SelectedKey = "other" },
                    "selected-package" => state with { SelectedPackage = "com.example.other" },
                    "selected-installed" => state with { SelectedInstalled = false },
                    _ => state with { SelectedLaunchable = false }
                };
                reply = reply with { RustyKioskAdbResult = reply.RustyKioskAdbResult with { Result = reply.RustyKioskAdbResult.Result with { State = state } } };
            }
            return Task.FromResult(reply);
        });
        var catalog = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open"));
        var body = BrowserKioskCommandsTests.Body("kiosk.select", catalog: catalog.RequestId);
        var outcome = await broker.ExecuteAsync(body);
        Assert.Equal("outcomeUnknown", outcome.State); Assert.Null(outcome.Mutation);
        Assert.Null((await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(body))).Dispatched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvenUnavailableNoOpCompletesConditionallyAndRetiresSelectionCatalog(bool selection)
    {
        var unavailable = !selection;
        var broker = new BrowserCommands((command, _) =>
        {
            var reply = Reply(command);
            if (unavailable) reply = reply with { RustyKioskAdbResult = reply.RustyKioskAdbResult! with
                { State = RustyKioskAdbResultState.InstallationOnly, ActivityOpenRequested = false, Result = null,
                    Installation = Installation with { HostOperatorAvailable = false } } };
            return Task.FromResult(reply);
        });
        var catalog = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open"));
        if (selection)
        {
            unavailable = true;
            var noOp = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.select", catalog: catalog.RequestId));
            Assert.Equal("completed", noOp.State); Assert.Null(noOp.Kiosk!.Result); Assert.Contains("not performed", noOp.Mutation!.ObservedState);
            Assert.Equal("kiosk_catalog_unavailable", (await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(
                BrowserKioskCommandsTests.Body("kiosk.select", catalog: catalog.RequestId)))).Code);
            unavailable = false;
            var fresh = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open"));
            Assert.Equal("completed", (await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.select", catalog: fresh.RequestId))).State);
        }
        else
        {
            Assert.Equal("completed", catalog.State); Assert.Equal("installationOnly", catalog.Kiosk!.ProviderState);
            Assert.False(catalog.Kiosk.ActivityOpenRequested); Assert.False(catalog.Kiosk.Installation!.HostOperatorAvailable);
            Assert.Null(catalog.Kiosk.Result); Assert.Null(catalog.Kiosk.CatalogRequestId);
            Assert.Equal(OperatorMutationStage.Confirmed, catalog.Mutation!.Stage);
        }
    }

    [Theory]
    [InlineData(RustyKioskAdbResultState.Pending)]
    [InlineData(RustyKioskAdbResultState.Rejected)]
    [InlineData(RustyKioskAdbResultState.Missing)]
    [InlineData(RustyKioskAdbResultState.Completed)]
    public async Task SeparateOriginalProviderFactsNeverRewriteTheUnknownOriginal(RustyKioskAdbResultState providerState)
    {
        var calls = new List<OperatorCommand>();
        var broker = new BrowserCommands((command, _) =>
        {
            calls.Add(command);
            if (command.Kind == OperatorCommandKind.OpenRustyKioskCatalog) throw new IOException("PRIVATE-MESSAGE");
            var reply = Reply(command);
            return Task.FromResult(reply with { RustyKioskAdbResult = reply.RustyKioskAdbResult! with { State = providerState, Result = providerState == RustyKioskAdbResultState.Completed ? reply.RustyKioskAdbResult.Result : null } });
        });
        var original = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open"));
        Assert.Equal("outcomeUnknown", original.State);
        var facts = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.original-result", provider: original.Kiosk!.ProviderRequestId));
        Assert.Equal("completed", facts.State); Assert.True(facts.Kiosk!.OriginalFactsOnly); Assert.False(facts.Kiosk.ActivityOpenRequested);
        Assert.Equal(providerState.ToString().ToLowerInvariant(), facts.Kiosk.ProviderState);
        Assert.Null(facts.Mutation); Assert.Null(facts.Result); Assert.Same(original, broker.Get(original.RequestId));
        Assert.Equal(OperatorCommandKind.ReadRustyKioskRequest, calls[1].Kind);
        Assert.DoesNotContain("--confirm-kiosk-control", calls[1].CliArguments);
        var restarted = new BrowserCommands((command, _) => Task.FromResult(Reply(command)));
        Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => restarted.Get(original.RequestId)).Code);
        Assert.Equal("completed", (await restarted.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.original-result", provider: original.Kiosk.ProviderRequestId))).State);
    }

    [Fact]
    public async Task CatalogProjectionBoundsLabelsRowsAndDuplicateSelectionWithoutExposingOwnerPaths()
    {
        var broker = new BrowserCommands((command, _) =>
        {
            var reply = Reply(command); var facts = reply.RustyKioskAdbResult!; var row = facts.Result!.State.Entries.Single();
            var rows = Enumerable.Range(0, 140).Select(i => row with { Key = "key-" + i, Name = new string('n', 300) }).ToArray();
            rows[139] = rows[0];
            return Task.FromResult(reply with { RustyKioskAdbResult = facts with { Result = facts.Result with { State = facts.Result.State with { Entries = rows, VisibleEntriesTruncated = true } } } });
        });
        var catalog = await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open"));
        Assert.Equal("completed", catalog.State);
        Assert.Equal(128, catalog.Kiosk!.Result!.Entries.Count); Assert.True(catalog.Kiosk.Result.ProjectionTruncated);
        Assert.True(catalog.Kiosk.Result.VisibleEntriesTruncated); Assert.False(catalog.Kiosk.Result.Entries[0].Selectable);
        Assert.All(catalog.Kiosk.Result.Entries, row => Assert.InRange(row.Name.Length, 0, 256));
    }

    [Fact]
    public async Task SharedGateAndOneUseLedgerSurviveCancellationUntilOwnerAcknowledges()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new BrowserOperationGate(); using var lifetime = new CancellationTokenSource();
        var calls = 0;
        var broker = new BrowserCommands(async (command, token) =>
        {
            calls++; started.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return Reply(command);
        }, operationGate: gate);
        var body = BrowserKioskCommandsTests.Body("kiosk.catalog.open", BrowserKioskCommandsTests.Id);
        var pending = broker.ExecuteAsync(body, lifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); lifetime.Cancel();
        try
        {
            Assert.True(gate.IsBusy); Assert.False(pending.IsCompleted);
            Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(body))).Code);
            Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.installation")))).Code);
        }
        finally { release.TrySetResult(); }
        var outcome = await pending; Assert.Equal("outcomeUnknown", outcome.State); Assert.False(gate.IsBusy); Assert.Equal(1, calls);
        Assert.Same(outcome, broker.Get(BrowserKioskCommandsTests.Id));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingExceptionMarkersAreBoundAndSanitizedWithoutLosingTheOriginal(bool malformed)
    {
        var broker = new BrowserCommands((command, _) =>
        {
            var marker = Reply(command).MutationReceipt! with { Stage = OperatorMutationStage.Pending, HeadsetReadback = false };
            if (malformed) marker = marker with { Transitions = null! };
            throw new OperatorMutationExecutionException(marker, new IOException("PRIVATE-MESSAGE"));
        });
        var body = BrowserKioskCommandsTests.Body("kiosk.catalog.open");
        var outcome = await broker.ExecuteAsync(body);
        Assert.Equal("outcomeUnknown", outcome.State); Assert.Null(outcome.Kiosk!.ActivityOpenRequested);
        if (malformed) Assert.Null(outcome.Mutation);
        else { Assert.Equal(OperatorMutationStage.Pending, outcome.Mutation!.Stage); Assert.False(outcome.Mutation.HeadsetReadback); }
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(outcome));
        Assert.Null((await Assert.ThrowsAsync<BrowserRequestException>(() => broker.ExecuteAsync(body))).Dispatched);
    }

    [Fact]
    public async Task PartialNoOpenProofCannotReleaseAnUncertainOriginal()
    {
        foreach (var mismatch in new[] { "marker", "available", "activity", "result" })
        {
            var broker = new BrowserCommands((command, _) =>
            {
                var reply = Reply(command);
                var facts = reply.RustyKioskAdbResult! with { State = RustyKioskAdbResultState.InstallationOnly,
                    ActivityOpenRequested = false, Result = null, Installation = Installation with { HostOperatorAvailable = false } };
                return Task.FromResult(reply with { MutationReceipt = mismatch == "marker" ? null : reply.MutationReceipt,
                    RustyKioskAdbResult = mismatch switch
                    {
                        "available" => facts with { Installation = Installation },
                        "activity" => facts with { ActivityOpenRequested = true },
                        "result" => facts with { Result = reply.RustyKioskAdbResult.Result },
                        _ => facts
                    } });
            });
            Assert.Equal("outcomeUnknown", (await broker.ExecuteAsync(BrowserKioskCommandsTests.Body("kiosk.catalog.open"))).State);
        }
    }

    private static readonly RustyKioskInstallationStatus Installation = new(true, "1.0", true, "1.0", true, true, true);
    internal static OperatorExecutionResult Reply(OperatorCommand command)
    {
        var passive = command.Kind == OperatorCommandKind.InspectRustyKioskInstallation;
        var read = command.Kind == OperatorCommandKind.ReadRustyKioskRequest;
        var selected = command.RustyKioskCommand == RustyKioskCommand.Select;
        var state = new RustyKioskState(1, 0, 1, false,
            [new(BrowserKioskCommandsTests.Key, "Fixture", BrowserKioskCommandsTests.PackageName, true, true, [])], "", null,
            selected ? command.RustyKioskValue : null, selected ? "Fixture" : null, selected ? BrowserKioskCommandsTests.PackageName : null,
            selected, selected, false, true, true, false, false, false, null, "PRIVATE-STATUS", "PRIVATE-TAGS");
        var result = passive ? null : new RustyKioskOperatorResult(RustyKioskContract.ResultSchema, command.RustyKioskProviderRequestId!,
            command.RustyKioskCommand!.Value, true, true, "PRIVATE-MESSAGE", state, "PRIVATE-RAW");
        var facts = new RustyKioskAdbResult(command.Serial!, command.RustyKioskProduct!.WireName, command.RustyKioskProviderRequestId,
            command.RustyKioskCommand, command.RustyKioskValue, read ? null : Installation, !passive && !read,
            passive ? RustyKioskAdbResultState.InstallationOnly : RustyKioskAdbResultState.Completed, result, "PRIVATE-MESSAGE");
        var marker = passive || read ? null : new OperatorMutationReceipt("pc-00000000000040008000000000000077", command.Kind, command.Serial!,
            selected ? "Rusty Kiosk installation inspected; selection " + command.RustyKioskValue + " requested if available (" + command.RustyKioskProduct.WireName + ")"
                : "Rusty Kiosk installation inspected; catalog requested if available (" + command.RustyKioskProduct.WireName + ")",
            OperatorMutationStage.Confirmed, "PRIVATE-MARKER", true,
            [new(OperatorMutationStage.Sent, DateTimeOffset.UtcNow, "PRIVATE-MARKER"), new(OperatorMutationStage.Confirmed, DateTimeOffset.UtcNow, "PRIVATE-MARKER")]);
        return new(command, CommandResult: new("native.exe", command.CliArguments, 0, "PRIVATE-RAW", "", TimeSpan.Zero),
            MutationReceipt: marker, RustyKioskAdbResult: facts);
    }
}
