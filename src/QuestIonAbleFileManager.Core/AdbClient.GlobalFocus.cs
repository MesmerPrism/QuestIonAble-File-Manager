using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    private const int MaximumGlobalFocusSourceLines = 4096;
    private const int MaximumGlobalFocusRecordsPerField = 8;
    private const int MaximumGlobalFocusLineCharacters = 2048;
    // Permit the bounded parser's full line budget plus a small fixed field and
    // newline allowance. The sentinel byte is consumed by the streaming runner
    // only to prove that the fixed source exceeded this limit.
    private const int MaximumGlobalFocusSourceBytes =
        (MaximumGlobalFocusSourceLines * (MaximumGlobalFocusLineCharacters + 32)) + 1;
    private static readonly Regex GlobalFocusLineRegex = new(
        @"^\s*(?<field>mCurrentFocus|mFocusedApp)\s*=\s*(?<value>.*)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RuntimeComponentRegex = new(
        @"(?<package>[A-Za-z][A-Za-z0-9_.]*)/(?<activity>\.[A-Za-z0-9_.$]+|[A-Za-z][A-Za-z0-9_.$]*(?:\.[A-Za-z0-9_.$]+)*)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads only the fixed WindowManager surface needed to distinguish the
    /// global <c>mCurrentFocus</c> and <c>mFocusedApp</c> observations from the
    /// ActivityManager resumed/top-resumed facts. A source-command failure is
    /// retained as an unavailable fact rather than being mistaken for absence.
    /// Timeout and caller cancellation remain command failures and propagate.
    /// </summary>
    private async Task<AndroidGlobalFocusObservation> ObserveGlobalAndroidFocusAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        serial = AndroidInput.RequireSerial(serial);
        if (_runner is not IStreamingCommandRunner streamingRunner)
        {
            throw new InvalidOperationException(
                "The configured command runner does not support bounded Android focus observation.");
        }

        var arguments = new[]
        {
            "-s", serial, "shell", "dumpsys", "window", "windows"
        };
        using var source = new MemoryStream();
        try
        {
            var streamed = await streamingRunner.RunToStreamAsync(
                AdbPath,
                arguments,
                source,
                MaximumGlobalFocusSourceBytes,
                InspectionTimeout,
                cancellationToken).ConfigureAwait(false);
            return ParseGlobalAndroidFocus(
                streamed.CommandResult,
                DecodeBoundedGlobalFocusSource(source));
        }
        catch (FleetTransferLimitException)
        {
            return UnknownGlobalFocusObservation();
        }
    }

    private static string DecodeBoundedGlobalFocusSource(MemoryStream source)
    {
        var buffer = source.GetBuffer();
        try
        {
            return System.Text.Encoding.UTF8.GetString(buffer, 0, checked((int)source.Length));
        }
        finally
        {
            Array.Clear(buffer, 0, checked((int)source.Length));
        }
    }

    private static AndroidGlobalFocusObservation ParseGlobalAndroidFocus(
        CommandResult focus,
        string standardOutput)
    {
        const string currentFocusSource =
            "fixed serial-scoped dumpsys window windows mCurrentFocus";
        const string focusedAppSource =
            "fixed serial-scoped dumpsys window windows mFocusedApp";

        if (!focus.Succeeded)
        {
            return new AndroidGlobalFocusObservation(
                UnavailableGlobalFocusFact(currentFocusSource, focus.ExitCode),
                UnavailableGlobalFocusFact(focusedAppSource, focus.ExitCode));
        }

        if (!string.IsNullOrWhiteSpace(focus.StandardError))
        {
            return UnknownGlobalFocusObservation(recordsTruncated: false);
        }

        var currentFocus = new GlobalFocusAccumulator(currentFocusSource, "Window");
        var focusedApp = new GlobalFocusAccumulator(focusedAppSource, "ActivityRecord");
        using var reader = new StringReader(standardOutput);
        var sourceTruncated = false;
        for (var lineCount = 0; lineCount < MaximumGlobalFocusSourceLines; lineCount++)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                return new AndroidGlobalFocusObservation(
                    currentFocus.Build(sourceTruncated),
                    focusedApp.Build(sourceTruncated));
            }

            var match = GlobalFocusLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var accumulator = string.Equals(
                match.Groups["field"].Value,
                "mCurrentFocus",
                StringComparison.Ordinal)
                ? currentFocus
                : focusedApp;
            accumulator.Add(match.Groups["value"].Value);
        }

        sourceTruncated = reader.ReadLine() is not null;
        return new AndroidGlobalFocusObservation(
            currentFocus.Build(sourceTruncated),
            focusedApp.Build(sourceTruncated));
    }

    private static AndroidGlobalFocusRecord UnavailableGlobalFocusFact(
        string source,
        int exitCode) =>
        new(AndroidGlobalFocusRecordState.Unavailable, 0, [])
        {
            ObservationSource = source,
            SourceExitCode = exitCode
        };

    private static AndroidGlobalFocusObservation UnknownGlobalFocusObservation(
        bool recordsTruncated = true)
    {
        const string currentFocusSource =
            "fixed serial-scoped dumpsys window windows mCurrentFocus";
        const string focusedAppSource =
            "fixed serial-scoped dumpsys window windows mFocusedApp";
        return new AndroidGlobalFocusObservation(
            UnknownGlobalFocusFact(currentFocusSource, recordsTruncated),
            UnknownGlobalFocusFact(focusedAppSource, recordsTruncated));
    }

    private static AndroidGlobalFocusRecord UnknownGlobalFocusFact(
        string source,
        bool recordsTruncated) =>
        new(AndroidGlobalFocusRecordState.Unknown, 0, [])
        {
            ObservationSource = source,
            RecordsTruncated = recordsTruncated
        };

    private static AndroidGlobalFocusFact ToLegacyGlobalFocusFact(
        AndroidGlobalFocusRecord fact)
    {
        var state = fact.State is AndroidGlobalFocusRecordState.Unknown or
                AndroidGlobalFocusRecordState.Unavailable
            ? AndroidGlobalFocusObservationState.Unknown
            : fact.RecordCount == 0
                ? AndroidGlobalFocusObservationState.Absent
                : fact.RecordCount != 1
                    ? AndroidGlobalFocusObservationState.Multiple
                    : fact.State switch
                    {
                        AndroidGlobalFocusRecordState.Reported when fact.Components.Count == 1 =>
                            AndroidGlobalFocusObservationState.Observed,
                        AndroidGlobalFocusRecordState.Empty or AndroidGlobalFocusRecordState.Absent =>
                            AndroidGlobalFocusObservationState.Absent,
                        AndroidGlobalFocusRecordState.Malformed =>
                            AndroidGlobalFocusObservationState.Malformed,
                        _ => AndroidGlobalFocusObservationState.Unknown
                    };
        return new AndroidGlobalFocusFact(
            state,
            state == AndroidGlobalFocusObservationState.Observed
                ? fact.Components[0]
                : null,
            fact.ObservationSource)
        {
            SourceExitCode = fact.SourceExitCode
        };
    }

    private sealed class GlobalFocusAccumulator(string source, string recordType)
    {
        private readonly List<string> _components = [];
        private int _recordCount;
        private int _emptyRecordCount;
        private int _malformedRecordCount;
        private bool _recordsTruncated;

        public void Add(string value)
        {
            _recordCount++;
            if (_recordCount > MaximumGlobalFocusRecordsPerField)
            {
                _recordsTruncated = true;
                return;
            }

            if (value.Length > MaximumGlobalFocusLineCharacters)
            {
                _malformedRecordCount++;
                return;
            }

            value = value.Trim();
            if (string.Equals(value, "null", StringComparison.Ordinal))
            {
                _emptyRecordCount++;
                return;
            }

            var matches = RuntimeComponentRegex.Matches(value);
            if (!value.StartsWith(recordType + "{", StringComparison.Ordinal) ||
                !value.EndsWith('}') ||
                matches.Count != 1 ||
                !TryReadRuntimeComponent(value, out var component))
            {
                _malformedRecordCount++;
                return;
            }

            _components.Add(component);
        }

        public AndroidGlobalFocusRecord Build(bool sourceTruncated)
        {
            var recordsTruncated = _recordsTruncated || sourceTruncated;
            var state = recordsTruncated
                ? AndroidGlobalFocusRecordState.Unknown
                : _malformedRecordCount > 0
                    ? AndroidGlobalFocusRecordState.Malformed
                    : _components.Count > 0
                    ? AndroidGlobalFocusRecordState.Reported
                    : _emptyRecordCount > 0
                        ? AndroidGlobalFocusRecordState.Empty
                        : AndroidGlobalFocusRecordState.Absent;
            return new AndroidGlobalFocusRecord(state, _recordCount, _components.ToArray())
            {
                ObservationSource = source,
                EmptyRecordCount = _emptyRecordCount,
                MalformedRecordCount = _malformedRecordCount,
                RecordsTruncated = recordsTruncated
            };
        }
    }
}
