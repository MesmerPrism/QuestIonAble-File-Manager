using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class QuestAwakeProviderTests
{
    private const string Serial = "QUEST123";
    private const string Generation = "watchdog-generation-1";
    private static readonly DateTimeOffset ContractNow =
        DateTimeOffset.FromUnixTimeMilliseconds(1_900_000_000_000);

    [Fact]
    public void KeepAwakeContract_AdmitsEightHoursAndRejectsFormerTwentyFourHourBound()
    {
        var admitted = Request("status");

        admitted.Validate(ContractNow);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OperatorCommands.SetQuestKeepAwake(
                Serial,
                enabled: true,
                durationMilliseconds: 86_400_000,
                operatorConfirmed: true));
        var exception = Assert.Throws<QuestAwakeProviderException>(
            () => (admitted with { DurationMilliseconds = 86_400_000 }).Validate(ContractNow));
        Assert.Equal("durationInvalid", exception.Code);
    }

    [Fact]
    public void PowerParser_BindsExactLatestProximityHoldAndRejectsLaterRestore()
    {
        var active = ParsePower(
            """
            Virtual proximity state: CLOSE
              1.0s (2.25s ago) - received com.oculus.vrpowermanager.prox_close broadcast: duration=28800000
            """);
        var restored = ParsePower(
            """
            Virtual proximity state: DISABLED
              1.0s (2.25s ago) - received com.oculus.vrpowermanager.prox_close broadcast: duration=28800000
              2.0s (0.5s ago) - received com.oculus.vrpowermanager.automation_disable broadcast: duration=0
            """);

        Assert.Equal(28_800_000, active.ProximityHoldDurationMilliseconds);
        Assert.Equal(28_797_750, active.ProximityHoldRemainingMilliseconds);
        Assert.Null(restored.ProximityHoldDurationMilliseconds);
        Assert.Null(restored.ProximityHoldRemainingMilliseconds);
    }

    [Fact]
    public async Task RepairOnce_IsReadOnlyWhenEveryIndependentReadbackMatches()
    {
        var runner = new AwakeRunner
        {
            StayOn = true,
            Awake = true,
            ProximityClose = true,
            ProximityDurationMilliseconds = 28_800_000
        };
        var client = new AdbClient("adb-test", runner);

        var result = await client.RepairQuestAwakeAsync(Serial, 28_800_000);

        Assert.Empty(result.Commands);
        Assert.DoesNotContain(runner.Calls, IsMutation);
    }

    [Fact]
    public async Task RepairOnce_ChangesOnlyDriftedFactsAndRepairsWrongHoldDuration()
    {
        var runner = new AwakeRunner
        {
            StayOn = false,
            Awake = true,
            ProximityClose = true,
            ProximityDurationMilliseconds = 60_000
        };
        var client = new AdbClient("adb-test", runner);

        var result = await client.RepairQuestAwakeAsync(Serial, 28_800_000);

        Assert.Equal(2, result.Commands.Count);
        Assert.Contains(runner.Calls, static arguments =>
            arguments.SequenceEqual(
                ["-s", Serial, "shell", "svc", "power", "stayon", "true"]));
        Assert.Contains(runner.Calls, static arguments =>
            arguments.Contains("com.oculus.vrpowermanager.prox_close", StringComparer.Ordinal));
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.Contains("KEYCODE_WAKEUP", StringComparer.Ordinal));
        Assert.True(result.EffectiveStatus.StayOn);
        Assert.Equal(28_800_000, result.EffectiveStatus.ProximityHoldDurationMilliseconds);
    }

    [Fact]
    public async Task RepairOnce_RechecksDeadlineAfterReadBeforeMutation()
    {
        var time = new MutableTimeProvider(ContractNow);
        var runner = new AwakeRunner
        {
            StayOn = false,
            Awake = false,
            ProximityClose = false,
            OnPowerRead = () => time.Advance(TimeSpan.FromMinutes(2))
        };
        var controller = new QuestAwakeProviderController(
            new AdbClient("adb-test", runner),
            time);

        var exception = await Assert.ThrowsAsync<QuestAwakeProviderException>(
            () => controller.ExecuteAsync(
                Request(
                    "repairOnce",
                    ContractNow,
                    expiresAfter: TimeSpan.FromMinutes(1))));

        Assert.Equal("requestExpired", exception.Code);
        Assert.DoesNotContain(runner.Calls, IsMutation);
    }

    [Fact]
    public async Task RepairOnce_RechecksDeadlineBeforeEveryMutation()
    {
        var time = new MutableTimeProvider(ContractNow);
        var runner = new AwakeRunner
        {
            StayOn = false,
            Awake = false,
            ProximityClose = false,
            OnStayOnEnabledMutation = () => time.Advance(TimeSpan.FromMinutes(2))
        };
        var controller = new QuestAwakeProviderController(
            new AdbClient("adb-test", runner),
            time);

        var exception = await Assert.ThrowsAsync<QuestAwakeProviderException>(
            () => controller.ExecuteAsync(
                Request(
                    "repairOnce",
                    ContractNow,
                    expiresAfter: TimeSpan.FromMinutes(1))));

        Assert.Equal("requestExpired", exception.Code);
        Assert.True(runner.StayOn);
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.Contains("KEYCODE_WAKEUP", StringComparer.Ordinal));
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.Contains("com.oculus.vrpowermanager.prox_close", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyBounded_RechecksDeadlineBeforeEveryMutation()
    {
        var time = new MutableTimeProvider(ContractNow);
        var runner = new AwakeRunner
        {
            StayOn = false,
            Awake = false,
            ProximityClose = false,
            OnStayOnEnabledMutation = () => time.Advance(TimeSpan.FromMinutes(2))
        };
        var controller = new QuestAwakeProviderController(
            new AdbClient("adb-test", runner),
            time);

        var exception = await Assert.ThrowsAsync<QuestAwakeProviderException>(
            () => controller.ExecuteAsync(
                Request(
                    "applyBounded",
                    ContractNow,
                    expiresAfter: TimeSpan.FromMinutes(1))));

        Assert.Equal("requestExpired", exception.Code);
        Assert.True(runner.StayOn);
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.Contains("KEYCODE_WAKEUP", StringComparer.Ordinal));
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.Contains("com.oculus.vrpowermanager.prox_close", StringComparer.Ordinal));
    }

    [Fact]
    public async Task LocalKeepAwake_RemainsPendingUntilExactHoldReadbackMatches()
    {
        var runner = new AwakeRunner
        {
            StayOn = false,
            Awake = false,
            ProximityClose = true,
            ProximityDurationMilliseconds = 60_000,
            IgnoreProximityMutation = true
        };
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner));

        var execution = await executor.ExecuteAsync(
            OperatorCommands.SetQuestKeepAwake(
                Serial,
                enabled: true,
                durationMilliseconds: 28_800_000,
                operatorConfirmed: true));

        Assert.Equal(OperatorMutationStage.Pending, execution.MutationReceipt?.Stage);
        Assert.True(execution.QuestControlStatus?.StayOn);
        Assert.Equal("Awake", execution.QuestControlStatus?.Wakefulness);
        Assert.Equal(60_000, execution.QuestControlStatus?.ProximityHoldDurationMilliseconds);
    }

    [Fact]
    public async Task StopWatchdogs_StopsOnlyTheHelperAndPreservesPowerSettings()
    {
        var runner = AwakeRunner.WithActiveWatchdog();
        var controller = new QuestAwakeProviderController(new AdbClient("adb-test", runner));

        var receipt = await controller.ExecuteAsync(
            Request(
                "stopWatchdogs",
                DateTimeOffset.UtcNow,
                expiresAfter: TimeSpan.FromMinutes(1)));

        Assert.True(receipt.Effective);
        Assert.True(receipt.SettingsLeftUnchanged);
        Assert.False(receipt.SettingsRestored);
        Assert.True(receipt.StayOnEffective);
        Assert.True(receipt.ProximityHoldEffective);
        Assert.True(receipt.WakeEffective);
        Assert.False(receipt.DeviceWatchdogEffective);
        Assert.Equal("watchdogsStoppedSettingsUnchanged", receipt.Outcome);
        Assert.Contains(runner.Calls, static arguments =>
            arguments.Any(argument =>
                argument.Contains("questionable-file-manager-awake-watchdog.stop", StringComparison.Ordinal)));
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.Contains("com.oculus.vrpowermanager.automation_disable", StringComparer.Ordinal));
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.SequenceEqual(
                ["-s", Serial, "shell", "svc", "power", "stayon", "false"]));
    }

    [Fact]
    public async Task StartDeviceWatchdog_RequiresCurrentProcessHeartbeatAndPowerReadbacks()
    {
        var runner = new AwakeRunner
        {
            StayOn = false,
            Awake = false,
            ProximityClose = false
        };
        var controller = new QuestAwakeProviderController(new AdbClient("adb-test", runner));

        var receipt = await controller.ExecuteAsync(
            Request(
                "startDeviceWatchdog",
                DateTimeOffset.UtcNow,
                expiresAfter: TimeSpan.FromMinutes(1)));

        Assert.True(receipt.Effective);
        Assert.True(receipt.DeviceWatchdogEffective);
        Assert.True(receipt.StayOnEffective);
        Assert.True(receipt.ProximityHoldEffective);
        Assert.True(receipt.WakeEffective);
        Assert.Equal(Generation, receipt.DeviceWatchdog.Generation);
        Assert.Contains(runner.Calls, static arguments =>
            arguments.Any(argument =>
                argument.Contains(
                    "nohup sh /data/local/tmp/questionable-file-manager-awake-watchdog.sh",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DeviceWatchdog_IsNotFreshAcrossAHeadsetBootIdentityChange()
    {
        var runner = AwakeRunner.WithActiveWatchdog();
        runner.ReportedBootId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var client = new AdbClient("adb-test", runner);

        var status = await client.GetQuestDeviceAwakeWatchdogStatusAsync(Serial, 1_000);
        var exception = await Assert.ThrowsAsync<QuestAwakeProviderException>(
            () => client.StartQuestDeviceAwakeWatchdogAsync(
                Serial,
                Generation,
                28_800_000,
                1_000));

        Assert.True(status.ReportedActive);
        Assert.False(status.Fresh);
        Assert.Equal("11111111-2222-3333-4444-555555555555", status.BootId);
        Assert.Equal("deviceWatchdogStale", exception.Code);
    }

    [Fact]
    public async Task SameGenerationWatchdogReuse_RejectsDifferentPollingInterval()
    {
        var runner = AwakeRunner.WithActiveWatchdog();
        runner.ReportedWatchdogIntervalMilliseconds = 5_000;
        var client = new AdbClient("adb-test", runner);

        var exception = await Assert.ThrowsAsync<QuestAwakeProviderException>(
            () => client.StartQuestDeviceAwakeWatchdogAsync(
                Serial,
                Generation,
                28_800_000,
                1_000));

        Assert.Equal("deviceWatchdogIntervalMismatch", exception.Code);
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.Any(argument =>
                argument.Contains("nohup sh", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WatchdogReceipt_BindsRequestedPollingInterval()
    {
        var runner = AwakeRunner.WithActiveWatchdog();
        runner.ReportedWatchdogIntervalMilliseconds = 5_000;
        var controller = new QuestAwakeProviderController(new AdbClient("adb-test", runner));

        var receipt = await controller.ExecuteAsync(
            Request(
                "status",
                DateTimeOffset.UtcNow,
                expiresAfter: TimeSpan.FromMinutes(1)));

        Assert.True(receipt.Effective);
        Assert.False(receipt.DeviceWatchdogEffective);
        Assert.Equal(5_000, receipt.DeviceWatchdog.IntervalMilliseconds);
        Assert.Equal(1_000, receipt.RequestedWatchdogIntervalMilliseconds);
    }

    [Fact]
    public async Task RestoreNormal_StopsTheHelperThenRestoresPowerAndProximity()
    {
        var runner = AwakeRunner.WithActiveWatchdog();
        var controller = new QuestAwakeProviderController(new AdbClient("adb-test", runner));

        var receipt = await controller.ExecuteAsync(
            Request(
                "restoreNormal",
                DateTimeOffset.UtcNow,
                expiresAfter: TimeSpan.FromMinutes(1)));

        Assert.True(receipt.Effective);
        Assert.False(receipt.SettingsLeftUnchanged);
        Assert.True(receipt.SettingsRestored);
        Assert.False(receipt.StayOnEffective);
        Assert.False(receipt.ProximityHoldEffective);
        Assert.False(receipt.DeviceWatchdogEffective);
        Assert.Equal("restored", receipt.Outcome);
        var stopIndex = runner.FindCall("questionable-file-manager-awake-watchdog.stop");
        var restoreProximityIndex = runner.FindCall("com.oculus.vrpowermanager.automation_disable");
        var restoreStayOnIndex = runner.FindExact(
            ["-s", Serial, "shell", "svc", "power", "stayon", "false"]);
        Assert.InRange(stopIndex, 0, int.MaxValue);
        Assert.True(restoreProximityIndex > stopIndex);
        Assert.True(restoreStayOnIndex > restoreProximityIndex);
    }

    [Fact]
    public async Task RestoreNormal_DoesNotMutateSettingsWhenWatchdogStopIsUnconfirmed()
    {
        var runner = AwakeRunner.WithActiveWatchdog();
        runner.IgnoreWatchdogStopMutation = true;
        var controller = new QuestAwakeProviderController(new AdbClient("adb-test", runner));

        var exception = await Assert.ThrowsAsync<QuestAwakeProviderException>(
            () => controller.ExecuteAsync(
                Request(
                    "restoreNormal",
                    DateTimeOffset.UtcNow,
                    expiresAfter: TimeSpan.FromMinutes(1))));

        Assert.Equal("deviceWatchdogStopUnconfirmed", exception.Code);
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.Contains("com.oculus.vrpowermanager.automation_disable", StringComparer.Ordinal));
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.SequenceEqual(
                ["-s", Serial, "shell", "svc", "power", "stayon", "false"]));
        Assert.True(runner.StayOn);
        Assert.True(runner.ProximityClose);
    }

    [Fact]
    public async Task RestoreNormal_DamagedInactiveStatusWithLiveProcessFailsClosed()
    {
        var runner = AwakeRunner.WithActiveWatchdog();
        runner.WatchdogActive = false;
        runner.IgnoreWatchdogStopMutation = true;
        var controller = new QuestAwakeProviderController(new AdbClient("adb-test", runner));

        var exception = await Assert.ThrowsAsync<QuestAwakeProviderException>(
            () => controller.ExecuteAsync(
                Request(
                    "restoreNormal",
                    DateTimeOffset.UtcNow,
                    expiresAfter: TimeSpan.FromMinutes(1))));

        Assert.Equal("deviceWatchdogStopUnconfirmed", exception.Code);
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.Contains("com.oculus.vrpowermanager.automation_disable", StringComparer.Ordinal));
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.SequenceEqual(
                ["-s", Serial, "shell", "svc", "power", "stayon", "false"]));
        Assert.True(runner.WatchdogProcessAlive);
    }

    [Fact]
    public async Task RestoreNormal_RechecksDeadlineAfterStopBeforeSettingsMutation()
    {
        var time = new MutableTimeProvider(ContractNow);
        var runner = AwakeRunner.WithActiveWatchdog();
        runner.OnWatchdogStopMutation = () =>
            time.Advance(TimeSpan.FromMinutes(2));
        var controller = new QuestAwakeProviderController(
            new AdbClient("adb-test", runner),
            time);

        var exception = await Assert.ThrowsAsync<QuestAwakeProviderException>(
            () => controller.ExecuteAsync(
                Request(
                    "restoreNormal",
                    ContractNow,
                    expiresAfter: TimeSpan.FromMinutes(1))));

        Assert.Equal("requestExpired", exception.Code);
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.Contains("com.oculus.vrpowermanager.automation_disable", StringComparer.Ordinal));
        Assert.DoesNotContain(runner.Calls, static arguments =>
            arguments.SequenceEqual(
                ["-s", Serial, "shell", "svc", "power", "stayon", "false"]));
    }

    [Fact]
    public async Task ProviderHost_RejectsArgumentsAndInvalidJsonBeforeAdbInitialization()
    {
        var factoryCalls = 0;
        var host = new QuestAwakeProviderSubprocessHost(
            () =>
            {
                factoryCalls++;
                throw new InvalidOperationException("ADB initialization must remain unreachable.");
            },
            new FixedTimeProvider(ContractNow));

        await AssertRejectedAsync(
            host,
            ["device", "status", "--serial", "must-not-run"],
            "{}",
            "providerArgumentsInvalid");
        await AssertRejectedAsync(
            host,
            ["integration", "quest-awake", "--json"],
            """{"contractVersion":"questionable.file_manager.fleet_awake_provider.v1","unexpected":true}""",
            "requestInvalid");
        await AssertRejectedAsync(
            host,
            ["integration", "quest-awake", "--json"],
            JsonSerializer.Serialize(Request("status") with { DurationMilliseconds = 86_400_000 }, JsonOptions),
            "durationInvalid");
        await AssertRejectedAsync(
            host,
            ["integration", "quest-awake", "--json"],
            """
            {
              "contractVersion":"questionable.file_manager.fleet_awake_provider.v1",
              "contractVersion":"questionable.file_manager.fleet_awake_provider.v1"
            }
            """,
            "requestInvalid");
        await AssertRejectedAsync(
            host,
            ["integration", "quest-awake", "--json"],
            "{\"padding\":\"" + new string('x', QuestAwakeContract.MaximumRequestBytes) + "\"}",
            "requestOversized");

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task ProviderResponse_BindsRequestAndExcludesPrivateDeviceIdentifiers()
    {
        var runner = new AwakeRunner
        {
            StayOn = true,
            Awake = true,
            ProximityClose = true,
            ProximityDurationMilliseconds = 28_800_000,
            ControllerIdentifier = "private-controller-identifier"
        };
        var request = Request("status");
        var host = new QuestAwakeProviderSubprocessHost(
            () => new QuestAwakeProviderController(
                new AdbClient("adb-test", runner),
                new FixedTimeProvider(ContractNow)),
            new FixedTimeProvider(ContractNow));
        await using var input = new MemoryStream(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions)));
        await using var output = new MemoryStream();

        var exitCode = await host.RunAsync(
            ["integration", "quest-awake", "--json"],
            input,
            output);
        var json = Encoding.UTF8.GetString(output.ToArray());
        var response = JsonSerializer.Deserialize<QuestAwakeProviderResponse>(json, JsonOptions);

        Assert.Equal(0, exitCode);
        Assert.Equal("verified", response?.Status);
        var receipt = Assert.IsType<QuestAwakeProviderReceipt>(response?.Receipt);
        Assert.Equal(request.RequestId, receipt.RequestId);
        Assert.Equal(request.OperationId, receipt.OperationId);
        Assert.Equal(request.PreviewId, receipt.PreviewId);
        Assert.Equal(request.DeviceId, receipt.DeviceId);
        Assert.Equal(request.IdentityRevision, receipt.IdentityRevision);
        Assert.Equal(request.Action, receipt.Action);
        Assert.Equal(request.WatchdogGeneration, receipt.WatchdogGeneration);
        Assert.DoesNotContain("processAlive", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Serial, json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-controller-identifier", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderFailure_DoesNotReturnRawAdbOrPrivatePathDetails()
    {
        const string privateDetail = @"QUEST123 failed through C:\private\adb.exe";
        var host = new QuestAwakeProviderSubprocessHost(
            () => new QuestAwakeProviderController(
                new AdbClient("adb-test", new ThrowingRunner(privateDetail)),
                new FixedTimeProvider(ContractNow)),
            new FixedTimeProvider(ContractNow));
        await using var input = new MemoryStream(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Request("status"), JsonOptions)));
        await using var output = new MemoryStream();

        var exitCode = await host.RunAsync(
            ["integration", "quest-awake", "--json"],
            input,
            output);
        var json = Encoding.UTF8.GetString(output.ToArray());
        var response = JsonSerializer.Deserialize<QuestAwakeProviderResponse>(json, JsonOptions);

        Assert.Equal(1, exitCode);
        Assert.Equal("failed", response?.Status);
        Assert.Equal("providerFailed", response?.Error);
        Assert.DoesNotContain(privateDetail, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Serial, json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\private", json, StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false
    };

    private static QuestAwakeProviderRequest Request(
        string action,
        DateTimeOffset? now = null,
        TimeSpan? expiresAfter = null)
    {
        var issuedAt = now ?? ContractNow;
        return new QuestAwakeProviderRequest(
            QuestAwakeContract.Version,
            "request-1",
            "operation-1",
            "preview-1",
            "device-1",
            7,
            action,
            28_800_000,
            1_000,
            Generation,
            issuedAt.ToUnixTimeMilliseconds(),
            issuedAt.Add(expiresAfter ?? TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds(),
            Serial);
    }

    private static QuestControlStatus ParsePower(string proximity) =>
        QuestControlParser.Parse(
            "level: 80\nstatus: 3\n",
            string.Empty,
            "mWakefulness=Awake\nmInteractive=true\nmStayOn=true\nDisplay Power: state=ON\n",
            proximity,
            string.Empty,
            string.Empty,
            ContractNow);

    private static bool IsMutation(IReadOnlyList<string> arguments) =>
        arguments.Contains("stayon", StringComparer.Ordinal) ||
        arguments.Contains("KEYCODE_WAKEUP", StringComparer.Ordinal) ||
        arguments.Contains("broadcast", StringComparer.Ordinal);

    private static async Task AssertRejectedAsync(
        QuestAwakeProviderSubprocessHost host,
        string[] arguments,
        string inputJson,
        string expectedError)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(inputJson));
        await using var output = new MemoryStream();

        var exitCode = await host.RunAsync(arguments, input, output);
        var response = JsonSerializer.Deserialize<QuestAwakeProviderResponse>(
            Encoding.UTF8.GetString(output.ToArray()),
            JsonOptions);

        Assert.Equal(2, exitCode);
        Assert.Equal("rejected", response?.Status);
        Assert.Equal(expectedError, response?.Error);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ThrowingRunner(string message) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CommandResult>(new InvalidOperationException(message));
    }

    private sealed class AwakeRunner : ICommandRunner
    {
        private readonly object _gate = new();
        private readonly List<IReadOnlyList<string>> _calls = [];

        public bool StayOn { get; set; }
        public bool Awake { get; set; }
        public bool ProximityClose { get; set; }
        public int? ProximityDurationMilliseconds { get; set; }
        public bool WatchdogActive { get; set; }
        public bool WatchdogProcessAlive { get; set; }
        public bool IgnoreProximityMutation { get; set; }
        public bool IgnoreWatchdogStopMutation { get; set; }
        public Action? OnStayOnEnabledMutation { get; set; }
        public Action? OnWatchdogStopMutation { get; set; }
        public Action? OnPowerRead { get; set; }
        public int ReportedWatchdogIntervalMilliseconds { get; set; } = 1_000;
        public string ReportedBootId { get; set; } =
            "11111111-2222-3333-4444-555555555555";
        public string ControllerIdentifier { get; set; } = "controller-placeholder";

        public IReadOnlyList<IReadOnlyList<string>> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.Select(static call => call.ToArray()).ToArray();
                }
            }
        }

        public static AwakeRunner WithActiveWatchdog() =>
            new()
            {
                StayOn = true,
                Awake = true,
                ProximityClose = true,
                ProximityDurationMilliseconds = 28_800_000,
                WatchdogActive = true,
                WatchdogProcessAlive = true
            };

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var snapshot = arguments.ToArray();
            lock (_gate)
            {
                _calls.Add(snapshot);
            }

            var output = Handle(snapshot);
            return Task.FromResult(
                new CommandResult(
                    fileName,
                    snapshot,
                    0,
                    output,
                    string.Empty,
                    TimeSpan.Zero));
        }

        public int FindCall(string fragment)
        {
            lock (_gate)
            {
                return _calls.FindIndex(arguments =>
                    arguments.Any(argument =>
                        argument.Contains(fragment, StringComparison.Ordinal)));
            }
        }

        public int FindExact(IReadOnlyList<string> expected)
        {
            lock (_gate)
            {
                return _calls.FindIndex(arguments => arguments.SequenceEqual(expected));
            }
        }

        private string Handle(IReadOnlyList<string> arguments)
        {
            if (arguments.SequenceEqual(
                    ["-s", Serial, "shell", "svc", "power", "stayon", "true"]))
            {
                StayOn = true;
                OnStayOnEnabledMutation?.Invoke();
                return string.Empty;
            }
            if (arguments.SequenceEqual(
                    ["-s", Serial, "shell", "svc", "power", "stayon", "false"]))
            {
                StayOn = false;
                return string.Empty;
            }
            if (arguments.Contains("KEYCODE_WAKEUP", StringComparer.Ordinal))
            {
                Awake = true;
                return string.Empty;
            }
            if (arguments.Contains("com.oculus.vrpowermanager.prox_close", StringComparer.Ordinal))
            {
                if (!IgnoreProximityMutation)
                {
                    ProximityClose = true;
                    var durationIndex = arguments
                        .Select((argument, index) => (argument, index))
                        .First(pair => pair.argument == "duration")
                        .index;
                    ProximityDurationMilliseconds = int.Parse(arguments[durationIndex + 1]);
                }
                return "Broadcast completed: result=0\n";
            }
            if (arguments.Contains("com.oculus.vrpowermanager.automation_disable", StringComparer.Ordinal))
            {
                ProximityClose = false;
                ProximityDurationMilliseconds = null;
                return "Broadcast completed: result=0\n";
            }
            if (arguments.Any(argument =>
                    argument.Contains(
                        "nohup sh /data/local/tmp/questionable-file-manager-awake-watchdog.sh",
                        StringComparison.Ordinal)))
            {
                WatchdogActive = true;
                WatchdogProcessAlive = true;
                StayOn = true;
                Awake = true;
                ProximityClose = true;
                ProximityDurationMilliseconds = 28_800_000;
                return string.Empty;
            }
            if (arguments.Any(argument =>
                    argument.Contains("questionable-file-manager-awake-watchdog.stop", StringComparison.Ordinal)) &&
                arguments.Contains("-c", StringComparer.Ordinal))
            {
                if (!IgnoreWatchdogStopMutation)
                {
                    WatchdogActive = false;
                    WatchdogProcessAlive = false;
                }
                OnWatchdogStopMutation?.Invoke();
                return string.Empty;
            }
            if (arguments.SequenceEqual(
                    ["-s", Serial, "shell", "cat", "/proc/sys/kernel/random/boot_id"]))
            {
                return "11111111-2222-3333-4444-555555555555\n";
            }
            if (arguments.Any(argument =>
                    argument.Contains("questionable-file-manager-awake-watchdog.status", StringComparison.Ordinal)))
            {
                var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return $"""
                       current_boot_id=11111111-2222-3333-4444-555555555555
                       active={WatchdogActive.ToString().ToLowerInvariant()}
                       generation={Generation}
                       boot_id={ReportedBootId}
                       interval_ms={ReportedWatchdogIntervalMilliseconds}
                       last_poll_epoch_seconds={nowSeconds}
                       proximity_repairs=0
                       stay_on_repairs=0
                       wake_repairs=0
                       last_action={(WatchdogActive ? "started" : "stopped")}
                       last_error=
                       process_alive={WatchdogProcessAlive.ToString().ToLowerInvariant()}

                       """;
            }
            if (arguments.Contains("battery", StringComparer.Ordinal))
                return "level: 80\nstatus: 3\n";
            if (arguments.Contains("tracking", StringComparer.Ordinal))
                return $"left [id:{ControllerIdentifier}, battery:80%, conn:connected]\n";
            if (arguments.Contains("power", StringComparer.Ordinal))
            {
                OnPowerRead?.Invoke();
                return
                    $"mWakefulness={(Awake ? "Awake" : "Asleep")}\n" +
                    $"mInteractive={(Awake ? "true" : "false")}\n" +
                    $"mStayOn={(StayOn ? "true" : "false")}\n" +
                    $"Display Power: state={(Awake ? "ON" : "OFF")}\n";
            }
            if (arguments.Contains("vrpowermanager", StringComparer.Ordinal))
            {
                if (!ProximityClose)
                    return "Virtual proximity state: DISABLED\nisAutosleepDisabled: false\n";
                return
                    "Virtual proximity state: CLOSE\n" +
                    "isAutosleepDisabled: true\n" +
                    $"  1.0s (1.0s ago) - received com.oculus.vrpowermanager.prox_close broadcast: duration={ProximityDurationMilliseconds}\n";
            }
            if (arguments.Contains("debug.oculus.cpuLevel", StringComparer.Ordinal) ||
                arguments.Contains("debug.oculus.gpuLevel", StringComparer.Ordinal))
                return string.Empty;

            throw new InvalidOperationException(
                $"Unexpected test command shape: {string.Join(' ', arguments)}");
        }
    }
}
