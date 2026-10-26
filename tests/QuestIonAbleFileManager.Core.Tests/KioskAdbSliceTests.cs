using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class KioskAdbSliceTests
{
    private const string Serial = "fixture-target";
    private const string Id = "pc-00000000000040008000000000000001";
    private const string Key = "package:example.fixture";
    [Fact]
    public void NewClosedFactoriesAreAdditiveAndLegacyVectorsStayUnchanged()
    {
        Assert.NotNull(typeof(OperatorCommands).GetMethod("InspectRustyKioskInstallation"));
        Assert.NotNull(typeof(OperatorCommands).GetMethod("OpenRustyKioskCatalog"));
        Assert.NotNull(typeof(OperatorCommands).GetMethod("SelectRustyKioskApp"));
        Assert.NotNull(typeof(OperatorCommands).GetMethod("ReadRustyKioskRequest"));
        Assert.Equal(20, (int)OperatorCommandKind.InspectRustyKiosk);
        Assert.Equal(["kiosk", "status", "--serial", "fixture-target", "--product-channel", "stable"],
            OperatorCommands.InspectRustyKiosk("fixture-target").CliArguments);
    }

    [Theory]
    [InlineData(RustyKioskProductChannel.Stable)]
    [InlineData(RustyKioskProductChannel.Labs)]
    public async Task PassiveInstallationHasNoAdmissionActivityOrMutation(RustyKioskProductChannel channel)
    {
        var product = RustyKioskProductContract.For(channel); var runner = new Runner(product);
        var result = await new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(OperatorCommands.InspectRustyKioskInstallation(Serial, product));
        var facts = result.RustyKioskAdbResult!;
        Assert.Equal(Serial, facts.Serial); Assert.Equal(product.WireName, facts.ProductChannel); Assert.True(facts.Installation!.HostOperatorAvailable);
        Assert.Equal(RustyKioskAdbResultState.InstallationOnly, facts.State); Assert.False(facts.ActivityOpenRequested); Assert.Null(facts.ProviderRequestId);
        Assert.Null(facts.ExpectedCommand); Assert.Null(facts.Result); Assert.Null(result.MutationReceipt);
        Assert.DoesNotContain(runner.Calls, call => call.Contains("invoke") || call.Contains("am") || call.Contains("result"));
        Assert.All(runner.Bounds, bound => { Assert.Equal(1048576, bound.Bytes); Assert.InRange(bound.Timeout.TotalSeconds, 0.001, 20); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableOperatorReturnsBoundInstallationOnlyWithoutOpening(bool select)
    {
        var runner = new Runner() { Available = false };
        var command = select ? OperatorCommands.SelectRustyKioskApp(Serial, Key, Id, true) : OperatorCommands.OpenRustyKioskCatalog(Serial, Id, true);
        var result = await new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(command);
        var facts = result.RustyKioskAdbResult!;
        Assert.Equal(Id, facts.ProviderRequestId); Assert.Equal(RustyKioskAdbResultState.InstallationOnly, facts.State);
        Assert.False(facts.Installation!.HostOperatorAvailable); Assert.False(facts.ActivityOpenRequested); Assert.Null(facts.Result);
        Assert.Equal(OperatorMutationStage.Confirmed, result.MutationReceipt!.Stage);
        Assert.Equal(select ? "Rusty Kiosk installation inspected; selection " + Key + " requested if available (stable)" :
            "Rusty Kiosk installation inspected; catalog requested if available (stable)", result.MutationReceipt.DesiredState);
        Assert.DoesNotContain("foreground", result.MutationReceipt.ObservedState, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(select ? "selection not performed" : "catalog not requested", result.MutationReceipt.ObservedState);
        Assert.All(result.MutationReceipt.Transitions, transition => Assert.Contains("operator is unavailable", transition.Message));
        Assert.DoesNotContain(runner.Calls, call => call.Contains("invoke") || call.Contains("am"));
    }

    [Theory]
    [InlineData(RustyKioskProductChannel.Stable, false)]
    [InlineData(RustyKioskProductChannel.Labs, false)]
    [InlineData(RustyKioskProductChannel.Stable, true)]
    [InlineData(RustyKioskProductChannel.Labs, true)]
    public async Task ExplicitCatalogAndSelectionHavePreallocatedIdsAndHonestBoundReceipts(RustyKioskProductChannel channel, bool select)
    {
        var product = RustyKioskProductContract.For(channel); var runner = new Runner(product) { Command = select ? RustyKioskCommand.Select : RustyKioskCommand.Status };
        var command = select ? OperatorCommands.SelectRustyKioskApp(Serial, Key, Id, true, product) : OperatorCommands.OpenRustyKioskCatalog(Serial, Id, true, product);
        Assert.Equal(Id, command.RustyKioskProviderRequestId); Assert.Empty(runner.Calls);
        var result = await new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(command);
        var facts = result.RustyKioskAdbResult!;
        Assert.Equal(RustyKioskAdbResultState.Completed, facts.State); Assert.True(facts.ActivityOpenRequested); Assert.Equal(Id, facts.Result!.RequestId);
        Assert.Equal(runner.Command, facts.ExpectedCommand); Assert.Equal(select ? Key : null, facts.Value);
        Assert.Equal(OperatorMutationStage.Confirmed, result.MutationReceipt!.Stage); Assert.True(result.MutationReceipt.HeadsetReadback);
        Assert.Equal(command.Kind, result.MutationReceipt.CommandKind); Assert.Equal(Serial, result.MutationReceipt.Target);
        Assert.Single(runner.Calls, call => call.Contains("invoke")); Assert.Single(runner.Calls, call => call.Contains("am"));
        Assert.All(runner.Calls, call => Assert.Equal(["-s", Serial], call.Take(2)));
        Assert.Contains(runner.Calls, call => call.Contains(product.MainActivity));
        Assert.Contains(runner.Calls, call => call.Contains("request_id:s:" + Id));
        Assert.DoesNotContain("OpenXR", result.MutationReceipt.ObservedState, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalLookupReadsOnlyItsFixedProviderResultAndDoesNotInferMissing(bool completed)
    {
        var runner = new Runner() { Pending = !completed, Command = RustyKioskCommand.Select };
        var command = OperatorCommands.ReadRustyKioskRequest(Serial, Id, RustyKioskCommand.Select, Key);
        var result = await new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(command);
        Assert.Null(result.MutationReceipt); Assert.False(result.RustyKioskAdbResult!.ActivityOpenRequested); Assert.Null(result.RustyKioskAdbResult.Installation);
        Assert.Equal(completed ? RustyKioskAdbResultState.Completed : RustyKioskAdbResultState.Pending, result.RustyKioskAdbResult.State);
        var call = Assert.Single(runner.Calls); Assert.Contains("result", call); Assert.Contains(Id, call);
        Assert.DoesNotContain("invoke", call); Assert.DoesNotContain("am", call); Assert.DoesNotContain("contract", call);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("command")]
    [InlineData("text")]
    [InlineData("count")]
    [InlineData("stderr")]
    [InlineData("overflow")]
    [InlineData("utf8")]
    [InlineData("cancel")]
    public async Task CrossedMalformedOrUnboundedResultCannotConfirmPossibleActivityEffect(string fault)
    {
        var runner = new Runner() { Fault = fault };
        var command = OperatorCommands.OpenRustyKioskCatalog(Serial, Id, true);
        var error = await Assert.ThrowsAsync<OperatorMutationExecutionException>(() => new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(command));
        Assert.Equal(OperatorMutationStage.Pending, error.MutationReceipt.Stage); Assert.Equal(command.Kind, error.MutationReceipt.CommandKind);
        Assert.StartsWith("pc-", error.MutationReceipt.OperationId); Assert.DoesNotContain("private-untrusted-text", error.MutationReceipt.ObservedState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongSelectionOrRejectedProviderResultNeverConfirmsSelection(bool rejected)
    {
        var runner = new Runner() { Command = RustyKioskCommand.Select, SelectedKey = "other-key", Accepted = !rejected };
        var result = await new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(OperatorCommands.SelectRustyKioskApp(Serial, Key, Id, true));
        Assert.NotEqual(OperatorMutationStage.Confirmed, result.MutationReceipt!.Stage);
        Assert.Equal(rejected ? RustyKioskAdbResultState.Rejected : RustyKioskAdbResultState.Pending, result.RustyKioskAdbResult!.State);
    }

    [Fact]
    public async Task CatalogIsBoundAndTruncationIsExplicitWithoutChangingSourceCounts()
    {
        var runner = new Runner() { Entries = 600 };
        var result = await new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(OperatorCommands.OpenRustyKioskCatalog(Serial, Id, true));
        var state = result.RustyKioskAdbResult!.Result!.State;
        Assert.Equal(512, state.Entries.Count); Assert.True(state.VisibleEntriesTruncated); Assert.Equal(600, state.VisibleCount);
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("main-absent")]
    public async Task CrossedChannelOrAbsentMainCannotOpenActivity(string fault)
    {
        var runner = new Runner() { Fault = fault };
        var result = await new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(OperatorCommands.OpenRustyKioskCatalog(Serial, Id, true));
        Assert.Equal(RustyKioskAdbResultState.InstallationOnly, result.RustyKioskAdbResult!.State);
        Assert.False(result.RustyKioskAdbResult.ActivityOpenRequested); Assert.Null(result.RustyKioskAdbResult.Result);
        Assert.DoesNotContain(runner.Calls, call => call.Contains("invoke") || call.Contains("am"));
    }

    [Fact]
    public async Task PendingAndRejectedAreFactsAndNeverAbsenceOrConfirmation()
    {
        foreach (var completed in new[] { false, true })
        {
            var runner = new Runner() { Command = RustyKioskCommand.Select, Completed = completed, RejectAdmission = completed };
            var result = await new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(OperatorCommands.SelectRustyKioskApp(Serial, Key, Id, true));
            Assert.Equal(completed ? RustyKioskAdbResultState.Rejected : RustyKioskAdbResultState.Pending, result.RustyKioskAdbResult!.State);
            Assert.NotEqual(OperatorMutationStage.Confirmed, result.MutationReceipt!.Stage);
            if (completed) { Assert.False(result.RustyKioskAdbResult.ActivityOpenRequested); Assert.DoesNotContain(runner.Calls, call => call.Contains("am")); }
        }
    }

    [Theory]
    [InlineData("id")]
    [InlineData("command")]
    public async Task CrossedOriginalFactsThrowWithoutDispatchOrMutation(string fault)
    {
        var runner = new Runner() { Fault = fault };
        await Assert.ThrowsAsync<InvalidDataException>(() => new OperatorCommandExecutor(new AdbClient("adb-test", runner))
            .ExecuteAsync(OperatorCommands.ReadRustyKioskRequest(Serial, Id, RustyKioskCommand.Status)));
        var call = Assert.Single(runner.Calls); Assert.Contains("result", call); Assert.DoesNotContain("am", call); Assert.DoesNotContain("invoke", call);
    }

    [Fact]
    public async Task OversizedInstallationTextFailsBeforeAnyActivityOrAdmission()
    {
        var runner = new Runner() { Fault = "version" };
        await Assert.ThrowsAsync<InvalidDataException>(() => new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(OperatorCommands.OpenRustyKioskCatalog(Serial, Id, true)));
        Assert.DoesNotContain(runner.Calls, call => call.Contains("invoke") || call.Contains("am"));
    }

    [Fact]
    public async Task NonStreamingRunnerAndInvalidClosedInputsFailBeforeCalls()
    {
        var runner = new NoStream();
        await Assert.ThrowsAsync<NotSupportedException>(() => new OperatorCommandExecutor(new AdbClient("adb-test", runner)).ExecuteAsync(OperatorCommands.InspectRustyKioskInstallation(Serial)));
        Assert.Equal(0, runner.Calls);
        Assert.Throws<InvalidOperationException>(() => OperatorCommands.OpenRustyKioskCatalog(Serial, Id));
        Assert.Throws<ArgumentException>(() => OperatorCommands.ReadRustyKioskRequest(Serial, Id, RustyKioskCommand.LaunchNormal));
        Assert.Throws<ArgumentException>(() => OperatorCommands.ReadRustyKioskRequest(Serial, Id, RustyKioskCommand.Status, Key));
        Assert.Throws<ArgumentException>(() => OperatorCommands.SelectRustyKioskApp(Serial, " " + Key, Id, true));
        Assert.Throws<ArgumentException>(() => OperatorCommands.OpenRustyKioskCatalog(Serial, "pc-" + new string('0', 32), true));
        Assert.Throws<ArgumentException>(() => OperatorCommands.OpenRustyKioskCatalog(Serial, Id.ToUpperInvariant(), true));
    }

    private sealed class NoStream : ICommandRunner
    {
        public int Calls;
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Unbounded calls forbidden"); }
    }

    private sealed class Runner(RustyKioskProductContract? product = null) : IStreamingCommandRunner
    {
        private readonly RustyKioskProductContract _product = product ?? RustyKioskProductContract.For(RustyKioskProductChannel.Stable);
        public bool Available = true, Pending, Accepted = true, Completed = true, RejectAdmission;
        public int Entries = 1;
        public string SelectedKey = Key;
        public RustyKioskCommand Command = RustyKioskCommand.Status;
        public string? Fault;
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<(long Bytes, TimeSpan Timeout)> Bounds { get; } = [];
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("New Kiosk routes must use bounded streaming calls");
        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments, Stream destination, long maximumBytes,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray()); Bounds.Add((maximumBytes, timeout));
            string output;
            if (arguments.Contains("dumpsys"))
            {
                var package = arguments.Last(); var permission = package == _product.MainPackage ? _product.SetupControlPermission : RustyKioskContract.WriteSecureSettingsPermission;
                output = Fault == "main-absent" && package == _product.MainPackage ? "Unable to find package\n" : $"Package [{package}]\nversionName={(Fault == "version" ? new string('x', 257) : "1")}\n {permission}: granted=true\n";
            }
            else if (arguments.Contains("contract")) output = $"Bundle[{{accepted={Available.ToString().ToLowerInvariant()}, schema={RustyKioskContract.HostOperatorSuccessorSchema}, package={_product.MainPackage}, product_channel={(Fault == "channel" ? "crossed" : _product.WireName)}}}]";
            else if (arguments.Contains("invoke")) output = RejectAdmission ? "Bundle[{accepted=false}]" : "Bundle[{accepted=true}]";
            else if (arguments.Contains("am")) output = "Status: ok\n";
            else if (arguments.Contains("result")) output = Pending ? "Bundle[{accepted=false, completed=false}]" :
                "Bundle[{accepted=true, completed=true, result_base64=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(ResultJson())) + "}]";
            else throw new InvalidOperationException("Unexpected typed call");
            var isResult = arguments.Contains("result");
            if (isResult && Fault == "cancel") throw new OperationCanceledException(cancellationToken);
            var bytes = isResult && Fault == "utf8" ? new byte[] { 255 } : Encoding.UTF8.GetBytes(isResult && Fault == "overflow" ? new string('x', 1048577) : output);
            await destination.WriteAsync(bytes, cancellationToken);
            return new(new(fileName, arguments, 0, "", isResult && Fault == "stderr" ? "private-untrusted-text" : "", TimeSpan.Zero),
                bytes.Length + (isResult && Fault == "count" ? 1 : 0), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        private string ResultJson() => JsonSerializer.Serialize(new
        {
            schema = RustyKioskContract.ResultSchema, request_id = Fault == "id" ? "pc-" + new string('2', 32) : Id,
            command = Fault == "command" ? "show-apps" : Command.ToWireName(), accepted = Accepted, completed = Completed, message = "Result received",
            state = new
            {
                installed_count = Entries, not_installed_count = 0, visible_count = Entries, visible_entries_truncated = false,
                entries = Enumerable.Range(0, Entries).Select(index => new { key = index == 0 ? Key : "package:example.app" + index, name = Fault == "text" ? new string('x', 257) : "Example", package = "example.fixture", installed = true, launchable = true, tags = Array.Empty<string>() }).ToArray(),
                search = "", tag_filter = (string?)null, selected_key = SelectedKey, selected_name = "Example", selected_package = "example.fixture", selected_installed = true, selected_launchable = true,
                wifi_adb_enabled = false, setup_helper_installed = true, setup_helper_ready = true, request_wifi_adb_after_boot = false,
                accessibility_enabled = false, guard_armed = false, operation_in_progress = (string?)null, status_line = "Ready"
            }
        });
    }
}
