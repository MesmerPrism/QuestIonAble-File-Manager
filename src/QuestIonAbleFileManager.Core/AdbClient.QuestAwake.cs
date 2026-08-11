using System.Globalization;
using System.Text;

namespace QuestIonAbleFileManager.Core;

public sealed record QuestAwakeRepairResult(
    IReadOnlyList<CommandResult> Commands,
    QuestControlStatus EffectiveStatus);

public sealed record QuestDeviceAwakeWatchdogResult(
    IReadOnlyList<CommandResult> Commands,
    QuestControlStatus EffectiveStatus,
    QuestDeviceAwakeWatchdogStatus Watchdog);

public sealed partial class AdbClient
{
    private const string AwakeWatchdogScriptPath =
        "/data/local/tmp/questionable-file-manager-awake-watchdog.sh";
    private const string AwakeWatchdogStopPath =
        "/data/local/tmp/questionable-file-manager-awake-watchdog.stop";
    private const string AwakeWatchdogStatusPath =
        "/data/local/tmp/questionable-file-manager-awake-watchdog.status";
    private const string AwakeWatchdogPidPath =
        "/data/local/tmp/questionable-file-manager-awake-watchdog.pid";

    public async Task<QuestAwakeRepairResult> RepairQuestAwakeAsync(
        string serial,
        int durationMilliseconds = QuestAwakeContract.MaximumHoldDurationMilliseconds,
        CancellationToken cancellationToken = default,
        Action? beforeMutation = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        ValidateAwakeDuration(durationMilliseconds);
        var before = await GetQuestControlStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        var commands = new List<CommandResult>();
        var stayOnDrifted = !before.StayOn;
        var wakeDrifted = !IsWakeEffective(before);
        var proximityDrifted =
            !string.Equals(before.ProximityState.Trim(), "CLOSE", StringComparison.OrdinalIgnoreCase) ||
            before.ProximityHoldDurationMilliseconds != durationMilliseconds ||
            before.ProximityHoldRemainingMilliseconds is null or <= 0;
        if (stayOnDrifted)
        {
            beforeMutation?.Invoke();
            commands.Add((await RunForDeviceAsync(
                serial,
                ["shell", "svc", "power", "stayon", "true"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess("Repair Quest stay-awake"));
        }
        if (wakeDrifted)
        {
            beforeMutation?.Invoke();
            commands.Add((await RunForDeviceAsync(
                serial,
                ["shell", "input", "keyevent", "KEYCODE_WAKEUP"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess("Repair Quest wakefulness"));
        }
        if (proximityDrifted)
        {
            beforeMutation?.Invoke();
            commands.Add((await RunForDeviceAsync(
                serial,
                [
                    "shell", "am", "broadcast",
                    "-a", "com.oculus.vrpowermanager.prox_close",
                    "--ei", "duration", durationMilliseconds.ToString(CultureInfo.InvariantCulture)
                ],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess("Repair Quest proximity hold"));
        }
        var after = commands.Count == 0
            ? before
            : await GetQuestControlStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        return new QuestAwakeRepairResult(commands, after);
    }

    public async Task<QuestDeviceAwakeWatchdogResult> StartQuestDeviceAwakeWatchdogAsync(
        string serial,
        string generation,
        int durationMilliseconds,
        int intervalMilliseconds,
        CancellationToken cancellationToken = default,
        Action? beforeMutation = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        ValidateGeneration(generation);
        ValidateAwakeDuration(durationMilliseconds);
        ValidateWatchdogInterval(intervalMilliseconds);
        var current = await GetQuestDeviceAwakeWatchdogStatusAsync(
            serial,
            intervalMilliseconds,
            cancellationToken).ConfigureAwait(false);
        if (current.ReportedActive || current.ProcessAlive)
        {
            if (!string.Equals(current.Generation, generation, StringComparison.Ordinal))
                throw new QuestAwakeProviderException(
                    "deviceWatchdogConflict",
                    "A different device watchdog generation is already active.");
            if (current.IntervalMilliseconds != intervalMilliseconds)
                throw new QuestAwakeProviderException(
                    "deviceWatchdogIntervalMismatch",
                    "The matching device watchdog uses a different polling interval.");
            if (!current.Fresh)
                throw new QuestAwakeProviderException(
                    "deviceWatchdogStale",
                    "The matching device watchdog is active but its heartbeat is stale; stop it before replacement.");
            var existingPower = await GetQuestControlStatusAsync(serial, cancellationToken)
                .ConfigureAwait(false);
            return new QuestDeviceAwakeWatchdogResult([], existingPower, current);
        }

        var bootId = current.BootId;
        var script = BuildDeviceWatchdogScript(
            generation,
            bootId,
            durationMilliseconds,
            intervalMilliseconds);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var launch =
            "umask 077; " +
            $"printf '%s' '{encoded}' | base64 -d > {AwakeWatchdogScriptPath} && " +
            $"chmod 700 {AwakeWatchdogScriptPath} && " +
            $"rm -f {AwakeWatchdogStopPath} {AwakeWatchdogStatusPath} {AwakeWatchdogPidPath} && " +
            $"(nohup sh {AwakeWatchdogScriptPath} </dev/null >/dev/null 2>&1 &)";
        beforeMutation?.Invoke();
        var command = (await RunForDeviceAsync(
            serial,
            ["shell", launch],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false)).EnsureSuccess("Start Quest device awake watchdog");

        QuestDeviceAwakeWatchdogStatus status = QuestDeviceAwakeWatchdogStatus.Inactive(bootId);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            status = await GetQuestDeviceAwakeWatchdogStatusAsync(
                serial,
                intervalMilliseconds,
                cancellationToken).ConfigureAwait(false);
            if (status.ReportedActive && status.Fresh &&
                string.Equals(status.Generation, generation, StringComparison.Ordinal))
                break;
        }
        var power = await GetQuestControlStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        return new QuestDeviceAwakeWatchdogResult([command], power, status);
    }

    public async Task<QuestDeviceAwakeWatchdogResult> StopQuestDeviceAwakeWatchdogAsync(
        string serial,
        string expectedGeneration,
        int intervalMilliseconds,
        CancellationToken cancellationToken = default,
        Action? beforeMutation = null)
    {
        serial = AndroidInput.RequireSerial(serial);
        ValidateGeneration(expectedGeneration);
        ValidateWatchdogInterval(intervalMilliseconds);
        var before = await GetQuestDeviceAwakeWatchdogStatusAsync(
            serial,
            intervalMilliseconds,
            cancellationToken).ConfigureAwait(false);
        if ((before.ReportedActive || before.ProcessAlive) &&
            !string.Equals(before.Generation, expectedGeneration, StringComparison.Ordinal))
            throw new QuestAwakeProviderException(
                "deviceWatchdogGenerationMismatch",
                "The active device watchdog does not match the requested generation.");
        var commands = new List<CommandResult>();
        if (before.ReportedActive || before.ProcessAlive)
        {
            beforeMutation?.Invoke();
            commands.Add((await RunForDeviceAsync(
                serial,
                ["shell", $"umask 077; : > {AwakeWatchdogStopPath}"],
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false)).EnsureSuccess("Stop Quest device awake watchdog"));
        }
        var status = before;
        var waitMilliseconds = Math.Min(intervalMilliseconds * 2, 10_000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(waitMilliseconds);
        while ((status.ReportedActive || status.ProcessAlive) &&
               DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(Math.Min(250, intervalMilliseconds), cancellationToken).ConfigureAwait(false);
            status = await GetQuestDeviceAwakeWatchdogStatusAsync(
                serial,
                intervalMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }
        var power = await GetQuestControlStatusAsync(serial, cancellationToken).ConfigureAwait(false);
        return new QuestDeviceAwakeWatchdogResult(commands, power, status);
    }

    public async Task<QuestDeviceAwakeWatchdogStatus> GetQuestDeviceAwakeWatchdogStatusAsync(
        string serial,
        int expectedIntervalMilliseconds = QuestAwakeContract.DefaultWatchdogIntervalMilliseconds,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial);
        ValidateWatchdogInterval(expectedIntervalMilliseconds);
        var result = await RunForDeviceAsync(
            serial,
            [
                "shell",
                "printf 'current_boot_id='; cat /proc/sys/kernel/random/boot_id; " +
                $"if [ -f {AwakeWatchdogStatusPath} ]; then cat {AwakeWatchdogStatusPath}; fi; " +
                $"process_alive=false; if [ -f {AwakeWatchdogPidPath} ]; then " +
                $"pid=$(cat {AwakeWatchdogPidPath} 2>/dev/null); " +
                "case \"$pid\" in ''|*[!0-9]*) ;; *) " +
                $"if kill -0 \"$pid\" 2>/dev/null && " +
                $"tr '\\000' ' ' < \"/proc/$pid/cmdline\" 2>/dev/null | " +
                $"grep -F '{AwakeWatchdogScriptPath}' >/dev/null 2>&1; then process_alive=true; fi ;; esac; fi; " +
                "printf 'process_alive=%s\\n' \"$process_alive\""
            ],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Read Quest device awake watchdog");
        var values = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split('=', 2))
            .Where(static fields => fields.Length == 2)
            .ToDictionary(static fields => fields[0].Trim(), static fields => fields[1].Trim(), StringComparer.Ordinal);
        var currentBootId = values.GetValueOrDefault("current_boot_id", string.Empty);
        ValidateBootId(currentBootId);
        var processAlive = values.TryGetValue("process_alive", out var processAliveText) &&
            string.Equals(processAliveText, "true", StringComparison.Ordinal);
        var active = values.TryGetValue("active", out var activeText) &&
            string.Equals(activeText, "true", StringComparison.Ordinal) &&
            processAlive;
        var generation = values.GetValueOrDefault("generation", string.Empty);
        var reportedBoot = values.GetValueOrDefault("boot_id", string.Empty);
        var interval = ParseNonnegative(values, "interval_ms");
        var lastPollSeconds = ParseLong(values, "last_poll_epoch_seconds");
        var lastPollMilliseconds = lastPollSeconds > 0
            ? checked(lastPollSeconds * 1000)
            : 0;
        var freshnessWindow = Math.Max(
            (long)Math.Max(interval, expectedIntervalMilliseconds) * 3,
            15_000L);
        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastPollMilliseconds;
        var fresh = active &&
            lastPollMilliseconds > 0 &&
            age is >= -30_000 &&
            age <= freshnessWindow &&
            string.Equals(currentBootId, reportedBoot, StringComparison.Ordinal);
        return new QuestDeviceAwakeWatchdogStatus(
            active,
            processAlive,
            fresh,
            generation,
            currentBootId,
            interval,
            lastPollMilliseconds,
            ParseNonnegative(values, "proximity_repairs"),
            ParseNonnegative(values, "stay_on_repairs"),
            ParseNonnegative(values, "wake_repairs"),
            values.GetValueOrDefault("last_action", string.Empty),
            values.GetValueOrDefault("last_error", string.Empty));
    }

    private static void ValidateBootId(string bootId)
    {
        if (bootId.Length is < 8 or > 128 ||
            bootId.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new QuestAwakeProviderException("bootIdentityInvalid", "Quest boot identity is unavailable or malformed.");
    }

    private static string BuildDeviceWatchdogScript(
        string generation,
        string bootId,
        int durationMilliseconds,
        int intervalMilliseconds)
    {
        var intervalSeconds = Math.Max(1, (intervalMilliseconds + 999) / 1000);
        var holdSeconds = Math.Max(1, durationMilliseconds / 1000);
        return $$"""
            #!/system/bin/sh
            printf '%s\n' "$$" > {{AwakeWatchdogPidPath}}
            proximity_repairs=0
            stay_on_repairs=0
            wake_repairs=0
            last_action=started
            last_error=
            while [ ! -f {{AwakeWatchdogStopPath}} ]; do
              power="$(dumpsys power 2>/dev/null)"
              proximity="$(dumpsys vrpowermanager 2>/dev/null)"
              case "$power" in *"mStayOn=true"*) ;; *) svc power stayon true >/dev/null 2>&1 && stay_on_repairs=$((stay_on_repairs + 1)) && last_action=reapplied_stay_on ;; esac
              case "$power" in *"mWakefulness=Awake"*) wakefulness_effective=true ;; *) wakefulness_effective=false ;; esac
              case "$power" in *"Display Power:"*"state=ON"*|*"mHoldingDisplaySuspendBlocker=true"*) display_effective=true ;; *) display_effective=false ;; esac
              if [ "$wakefulness_effective" != true ] || [ "$display_effective" != true ]; then
                input keyevent 224 >/dev/null 2>&1 && wake_repairs=$((wake_repairs + 1)) && last_action=reapplied_wake
              fi
              latest_proximity="$(printf '%s\n' "$proximity" | grep 'received com.oculus.vrpowermanager.' | head -n 1)"
              proximity_age_seconds="$(printf '%s\n' "$latest_proximity" | sed -n 's/.*(\([0-9][0-9]*\)[.,][0-9][0-9]*s ago).*/\1/p')"
              case "$proximity" in *"Virtual proximity state: CLOSE"*) proximity_close=true ;; *) proximity_close=false ;; esac
              case "$latest_proximity" in *"prox_close broadcast: duration={{durationMilliseconds}}"*) proximity_duration=true ;; *) proximity_duration=false ;; esac
              case "$proximity_age_seconds" in ''|*[!0-9]*) proximity_fresh=false ;; *) if [ "$proximity_age_seconds" -lt {{holdSeconds}} ]; then proximity_fresh=true; else proximity_fresh=false; fi ;; esac
              if [ "$proximity_close" != true ] || [ "$proximity_duration" != true ] || [ "$proximity_fresh" != true ]; then
                am broadcast -a com.oculus.vrpowermanager.prox_close --ei duration {{durationMilliseconds}} >/dev/null 2>&1 && proximity_repairs=$((proximity_repairs + 1)) && last_action=reapplied_proximity
              fi
              now="$(date +%s)"
              temporary="{{AwakeWatchdogStatusPath}}.tmp"
              {
                printf 'active=true\n'
                printf 'generation={{generation}}\n'
                printf 'boot_id={{bootId}}\n'
                printf 'interval_ms={{intervalMilliseconds}}\n'
                printf 'last_poll_epoch_seconds=%s\n' "$now"
                printf 'proximity_repairs=%s\n' "$proximity_repairs"
                printf 'stay_on_repairs=%s\n' "$stay_on_repairs"
                printf 'wake_repairs=%s\n' "$wake_repairs"
                printf 'last_action=%s\n' "$last_action"
                printf 'last_error=%s\n' "$last_error"
              } > "$temporary"
              mv "$temporary" {{AwakeWatchdogStatusPath}}
              remaining_sleep={{intervalSeconds}}
              while [ "$remaining_sleep" -gt 0 ] && [ ! -f {{AwakeWatchdogStopPath}} ]; do
                sleep 1
                remaining_sleep=$((remaining_sleep - 1))
              done
            done
            now="$(date +%s)"
            {
              printf 'active=false\n'
              printf 'generation={{generation}}\n'
              printf 'boot_id={{bootId}}\n'
              printf 'interval_ms={{intervalMilliseconds}}\n'
              printf 'last_poll_epoch_seconds=%s\n' "$now"
              printf 'proximity_repairs=%s\n' "$proximity_repairs"
              printf 'stay_on_repairs=%s\n' "$stay_on_repairs"
              printf 'wake_repairs=%s\n' "$wake_repairs"
              printf 'last_action=stopped\n'
              printf 'last_error=%s\n' "$last_error"
            } > {{AwakeWatchdogStatusPath}}
            rm -f {{AwakeWatchdogStopPath}}
            rm -f {{AwakeWatchdogPidPath}}
            """;
    }

    private static bool IsWakeEffective(QuestControlStatus status) =>
        string.Equals(status.Wakefulness, "Awake", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(status.DisplayState, "ON", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(status.DisplayState, "ON_SUSPEND", StringComparison.OrdinalIgnoreCase));

    private static int ParseNonnegative(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) &&
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
        value >= 0
            ? value
            : 0;

    private static long ParseLong(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) &&
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static void ValidateAwakeDuration(int durationMilliseconds)
    {
        if (durationMilliseconds is < QuestAwakeContract.MinimumHoldDurationMilliseconds
            or > QuestAwakeContract.MaximumHoldDurationMilliseconds)
            throw new ArgumentOutOfRangeException(
                nameof(durationMilliseconds),
                "Keep-awake duration must be between one minute and eight hours.");
    }

    private static void ValidateWatchdogInterval(int intervalMilliseconds)
    {
        if (intervalMilliseconds is < QuestAwakeContract.MinimumWatchdogIntervalMilliseconds
            or > QuestAwakeContract.MaximumWatchdogIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(
                nameof(intervalMilliseconds),
                "Watchdog interval must be between one and sixty seconds.");
    }

    private static void ValidateGeneration(string generation)
    {
        if (generation.Length is < 1 or > QuestAwakeContract.MaximumIdentifierLength ||
            generation.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new ArgumentException("Watchdog generation must be a bounded portable identifier.", nameof(generation));
    }
}
