using System.Globalization;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public static partial class QuestControlParser
{
    public static QuestControlStatus Parse(
        string batteryOutput,
        string trackingOutput,
        string powerOutput,
        string proximityOutput,
        string cpuLevel,
        string gpuLevel,
        DateTimeOffset capturedAt,
        string displayOutput = "")
    {
        var batteryLevel = ParseIntegerLine(batteryOutput, "level");
        var batteryState = ParseBatteryState(ParseIntegerLine(batteryOutput, "status"));
        var wakefulness = MatchGroup(WakefulnessRegex(), powerOutput);
        var interactiveText = MatchGroup(InteractiveRegex(), powerOutput);
        var interactive = bool.TryParse(interactiveText, out var parsedInteractive)
            ? parsedInteractive
            : (bool?)null;
        var displayState = MatchGroup(DisplayStateRegex(), powerOutput);
        if (displayState.Length == 0)
        {
            displayState = ParseDefaultDisplayState(displayOutput);
        }
        var proximityState = MatchGroup(ProximityStateRegex(), proximityOutput);
        var stayOn = powerOutput.Contains("mStayOn=true", StringComparison.OrdinalIgnoreCase);
        var autoSleepText = MatchGroup(AutoSleepDisabledRegex(), proximityOutput);
        var autoSleepDisabled = bool.TryParse(autoSleepText, out var parsedAutoSleep)
            ? parsedAutoSleep
            : (bool?)null;
        var keepAwake = stayOn || autoSleepDisabled == true;
        var (holdDurationMilliseconds, holdRemainingMilliseconds) =
            ParseLatestProximityHold(proximityOutput);

        return new QuestControlStatus(
            batteryLevel,
            batteryState,
            ParseControllerPower(trackingOutput),
            wakefulness,
            interactive,
            displayState,
            stayOn,
            autoSleepDisabled,
            keepAwake,
            proximityState,
            cpuLevel.Trim(),
            gpuLevel.Trim(),
            capturedAt,
            holdDurationMilliseconds,
            holdRemainingMilliseconds);
    }

    public static IReadOnlyList<QuestControllerPower> ParseControllerPower(string output)
    {
        var controllers = new Dictionary<string, QuestControllerPower>(StringComparer.OrdinalIgnoreCase);
        var currentHand = string.Empty;
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Contains("left", StringComparison.OrdinalIgnoreCase))
            {
                currentHand = "Left";
            }
            else if (line.Contains("right", StringComparison.OrdinalIgnoreCase))
            {
                currentHand = "Right";
            }

            var entryIndex = line.IndexOf("[id:", StringComparison.OrdinalIgnoreCase);
            if (currentHand.Length == 0 || entryIndex < 0)
            {
                continue;
            }

            var entry = line[entryIndex..];
            var batteryText = ExtractSegment(entry, "battery:").TrimEnd('%');
            controllers[currentHand] = new QuestControllerPower(
                currentHand,
                int.TryParse(batteryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var battery)
                    ? battery
                    : null,
                ExtractSegment(entry, "conn:"),
                ExtractSegment(entry, "id:"));
        }

        return controllers.Values
            .OrderBy(static controller => controller.Hand == "Left" ? 0 : 1)
            .ToArray();
    }

    private static int? ParseIntegerLine(string output, string name)
    {
        var match = Regex.Match(
            output,
            $@"(?m)^\s*{Regex.Escape(name)}:\s*(?<value>\d+)\s*$",
            RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(
            match.Groups["value"].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
                ? parsed
                : null;
    }

    private static (int? DurationMilliseconds, int? RemainingMilliseconds)
        ParseLatestProximityHold(string output)
    {
        Match? match = null;
        var ageSeconds = double.MaxValue;
        foreach (Match candidate in ProximityBroadcastRegex().Matches(output))
        {
            if (double.TryParse(
                    candidate.Groups["age"].Value.Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var candidateAgeSeconds) &&
                candidateAgeSeconds >= 0d &&
                candidateAgeSeconds < ageSeconds)
            {
                match = candidate;
                ageSeconds = candidateAgeSeconds;
            }
        }

        if (match is null ||
            !string.Equals(match.Groups["action"].Value, "prox_close", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(
                match.Groups["duration"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var durationMilliseconds))
        {
            return (null, null);
        }

        var elapsedMilliseconds = (long)Math.Ceiling(Math.Max(0d, ageSeconds) * 1000d);
        var remainingMilliseconds = (int)Math.Clamp(
            (long)durationMilliseconds - elapsedMilliseconds,
            0L,
            int.MaxValue);
        return (durationMilliseconds, remainingMilliseconds);
    }

    private static string ParseDefaultDisplayState(string output)
    {
        var inController = false;
        var isDefaultDisplay = false;
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.Equals(line, "Display Power Controller:", StringComparison.OrdinalIgnoreCase))
            {
                inController = true;
                isDefaultDisplay = false;
                continue;
            }
            if (!inController)
            {
                continue;
            }
            if (line.StartsWith("mDisplayId=", StringComparison.OrdinalIgnoreCase))
            {
                isDefaultDisplay = string.Equals(
                    line["mDisplayId=".Length..].Trim(),
                    "0",
                    StringComparison.Ordinal);
                continue;
            }
            if (isDefaultDisplay &&
                line.StartsWith("mScreenState=", StringComparison.OrdinalIgnoreCase))
            {
                return line["mScreenState=".Length..].Trim();
            }
        }

        return string.Empty;
    }

    private static string ParseBatteryState(int? state) => state switch
    {
        2 => "charging",
        3 => "discharging",
        4 => "not charging",
        5 => "full",
        _ => "unknown"
    };

    private static string ExtractSegment(string input, string label)
    {
        var start = input.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += label.Length;
        var end = input.IndexOfAny([',', ']'], start);
        if (end < 0)
        {
            end = input.Length;
        }

        return input[start..end].Trim();
    }

    private static string MatchGroup(Regex regex, string input)
    {
        var match = regex.Match(input);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    [GeneratedRegex(@"(?m)^\s*mWakefulness=(?<value>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WakefulnessRegex();

    [GeneratedRegex(@"(?m)^\s*mInteractive=(?<value>true|false)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InteractiveRegex();

    [GeneratedRegex(@"(?m)^\s*Display Power:.*?state=(?<value>[^\s\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DisplayStateRegex();

    [GeneratedRegex(@"Virtual proximity state:\s*(?<value>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProximityStateRegex();

    [GeneratedRegex(
        @"^\s*(?:\d+(?:[.,]\d+)?s|\d{2,}:\d{2}:\d{2}(?:[.,]\d+)?)\s+\((?<age>\d+(?:[.,]\d+)?)s ago\)\s+-\s+received com\.oculus\.vrpowermanager\.(?<action>prox_close|automation_disable) broadcast:\s+duration=(?<duration>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ProximityBroadcastRegex();

    [GeneratedRegex(@"isAutosleepDisabled:\s*(?<value>true|false)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoSleepDisabledRegex();
}
