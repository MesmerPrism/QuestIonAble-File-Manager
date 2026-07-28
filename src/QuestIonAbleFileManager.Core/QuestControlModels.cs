namespace QuestIonAbleFileManager.Core;

public sealed record QuestControllerPower(
    string Hand,
    int? BatteryLevel,
    string ConnectionState,
    string DeviceId)
{
    public string DisplayLabel => BatteryLevel is int level
        ? $"{Hand} {level}% ({ConnectionState})"
        : $"{Hand} battery unavailable ({ConnectionState})";
}

public sealed record QuestControlStatus(
    int? HeadsetBatteryLevel,
    string HeadsetBatteryState,
    IReadOnlyList<QuestControllerPower> Controllers,
    string Wakefulness,
    bool? Interactive,
    string DisplayState,
    bool StayOn,
    bool? AutoSleepDisabled,
    bool KeepAwakeActive,
    string ProximityState,
    string CpuLevel,
    string GpuLevel,
    DateTimeOffset CapturedAt,
    int? ProximityHoldDurationMilliseconds = null,
    int? ProximityHoldRemainingMilliseconds = null)
{
    public string HeadsetBatteryLabel => HeadsetBatteryLevel is int level
        ? $"{level}% {HeadsetBatteryState}".Trim()
        : "Unavailable";

    public string ControllerBatteryLabel => Controllers.Count == 0
        ? "Controller batteries unavailable"
        : string.Join(" · ", Controllers.Select(static controller => controller.DisplayLabel));
}

public sealed record QuestKeepAwakeResult(
    bool RequestedEnabled,
    IReadOnlyList<CommandResult> Commands,
    QuestControlStatus EffectiveStatus);

public sealed record QuestPerformanceResult(
    int? RequestedCpuLevel,
    int? RequestedGpuLevel,
    bool Cleared,
    IReadOnlyList<CommandResult> Commands,
    QuestControlStatus EffectiveStatus);
