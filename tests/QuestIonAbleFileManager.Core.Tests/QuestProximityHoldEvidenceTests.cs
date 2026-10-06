using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class QuestProximityHoldEvidenceTests
{
    [Theory]
    [InlineData("", QuestProximityHoldParseAvailability.NoMatchingBroadcast, null, null)]
    [InlineData("unrecognized private history", QuestProximityHoldParseAvailability.NoMatchingBroadcast, null, null)]
    [InlineData("1.0s (0.5s ago) - received com.oculus.vrpowermanager.prox_close broadcast: duration=28800000", QuestProximityHoldParseAvailability.CloseBroadcastObserved, 28800000, 28799500)]
    [InlineData("1.0s (0.5s ago) - received com.oculus.vrpowermanager.automation_disable broadcast: duration=0", QuestProximityHoldParseAvailability.LatestAutomationDisable, null, null)]
    [InlineData("1.0s (0.5s ago) - received com.oculus.vrpowermanager.prox_close broadcast: duration=999999999999999", QuestProximityHoldParseAvailability.DurationUnavailable, null, null)]
    [InlineData("1.0s (invalid ago) - received com.oculus.vrpowermanager.prox_close broadcast: duration=28800000", QuestProximityHoldParseAvailability.NoMatchingBroadcast, null, null)]
    public void Parse_ClassifiesHistoryWithoutChangingNullableHold(
        string history, QuestProximityHoldParseAvailability expected, int? duration, int? remaining)
    {
        var status = Parse(history);
        Assert.Equal(expected, status.ProximityHoldEvidence?.ParseAvailability);
        Assert.Null(status.ProximityHoldEvidence?.ReadSucceeded);
        Assert.Equal(duration, status.ProximityHoldDurationMilliseconds);
        Assert.Equal(remaining, status.ProximityHoldRemainingMilliseconds);
        Assert.True(status.KeepAwakeActive);
    }

    [Fact]
    public void Parse_LaterShortBroadcastRetainsExistingYoungestAgeSemantics()
    {
        var status = Parse("""
            1.0s (10s ago) - received com.oculus.vrpowermanager.prox_close broadcast: duration=28800000
            2.0s (0.25s ago) - received com.oculus.vrpowermanager.prox_close broadcast: duration=600000
            """);
        Assert.Equal(600000, status.ProximityHoldDurationMilliseconds);
        Assert.Equal(599750, status.ProximityHoldRemainingMilliseconds);
        Assert.Equal(QuestProximityHoldParseAvailability.CloseBroadcastObserved,
            status.ProximityHoldEvidence?.ParseAvailability);
    }

    [Theory]
    [InlineData(true, QuestProximityHoldParseAvailability.NoMatchingBroadcast)]
    [InlineData(false, QuestProximityHoldParseAvailability.ReadUnavailable)]
    public async Task CoreStatus_DistinguishesFailedProximityReadWithoutExportingRawDiagnostics(
        bool proximitySucceeded, QuestProximityHoldParseAvailability expected)
    {
        var runner = new StatusRunner(proximitySucceeded);
        var status = await new AdbClient("adb-test", runner).GetQuestControlStatusAsync("QUEST-TEST");
        Assert.Equal(proximitySucceeded, status.ProximityHoldEvidence?.ReadSucceeded);
        Assert.Equal(expected, status.ProximityHoldEvidence?.ParseAvailability);
        Assert.Null(status.ProximityHoldDurationMilliseconds);
        Assert.Null(status.ProximityHoldRemainingMilliseconds);
        Assert.True(status.KeepAwakeActive);
        var projected = QuestAwakePowerReadback.From(status);
        var json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain("private diagnostic", json, StringComparison.Ordinal);
        Assert.DoesNotContain("unrecognized private history", json, StringComparison.Ordinal);
        Assert.DoesNotContain("QUEST-TEST", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProximityHoldEvidence", JsonSerializer.Serialize(projected), StringComparison.Ordinal);
        Assert.Equal(7, runner.Calls);
    }

    private static QuestControlStatus Parse(string history) => QuestControlParser.Parse(
        "level: 80\nstatus: 3", "", "mWakefulness=Awake\nmStayOn=true", history, "", "",
        DateTimeOffset.UnixEpoch);

    private sealed class StatusRunner(bool proximitySucceeded) : ICommandRunner
    {
        public int Calls { get; private set; }

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            var proximity = arguments.Contains("vrpowermanager");
            var output = arguments.Contains("battery") ? "level: 80\nstatus: 3" :
                arguments.Contains("power") ? "mWakefulness=Awake\nmStayOn=true" :
                proximity ? "unrecognized private history" : "";
            return Task.FromResult(new CommandResult(fileName, arguments,
                proximity && !proximitySucceeded ? 1 : 0, output,
                proximity && !proximitySucceeded ? "private diagnostic" : "", TimeSpan.Zero));
        }
    }
}
