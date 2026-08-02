using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.Core;

public enum RustyKioskCommand
{
    Status,
    ShowControls,
    ShowApps,
    Reload,
    FocusSearch,
    FocusTagEditor,
    SetSearch,
    Select,
    FilterTag,
    AddTag,
    RemoveTag,
    SetLaunchRequirement,
    CancelPendingLaunch,
    LaunchNormal,
    LaunchKiosk,
    CheckSetupHelper,
    RequestWifiAdb,
    EnableWifiAfterBoot,
    DisableWifiAfterBoot,
    DisableWifiAdb,
    EnableAccessibility,
    DisableAccessibility,
    PassthroughNatural,
    PassthroughContour,
    ExitMetaHome
}

public enum RustyKioskLaunchRequirement
{
    Any,
    WifiOn,
    WifiOff
}

public enum RustyKioskPassthroughStyle
{
    Natural,
    ContourLut
}

public static class RustyKioskCommands
{
    public static string ToWireName(this RustyKioskCommand command) => command switch
    {
        RustyKioskCommand.Status => "status",
        RustyKioskCommand.ShowControls => "show-controls",
        RustyKioskCommand.ShowApps => "show-apps",
        RustyKioskCommand.Reload => "reload",
        RustyKioskCommand.FocusSearch => "focus-search",
        RustyKioskCommand.FocusTagEditor => "focus-tag-editor",
        RustyKioskCommand.SetSearch => "set-search",
        RustyKioskCommand.Select => "select",
        RustyKioskCommand.FilterTag => "filter-tag",
        RustyKioskCommand.AddTag => "add-tag",
        RustyKioskCommand.RemoveTag => "remove-tag",
        RustyKioskCommand.SetLaunchRequirement => "set-launch-requirement",
        RustyKioskCommand.CancelPendingLaunch => "cancel-pending-launch",
        RustyKioskCommand.LaunchNormal => "launch-normal",
        RustyKioskCommand.LaunchKiosk => "launch-kiosk",
        RustyKioskCommand.CheckSetupHelper => "check-setup-helper",
        RustyKioskCommand.RequestWifiAdb => "request-wifi-adb",
        RustyKioskCommand.EnableWifiAfterBoot => "enable-wifi-adb-after-boot",
        RustyKioskCommand.DisableWifiAfterBoot => "disable-wifi-adb-after-boot",
        RustyKioskCommand.DisableWifiAdb => "disable-wifi-adb",
        RustyKioskCommand.EnableAccessibility => "enable-accessibility",
        RustyKioskCommand.DisableAccessibility => "disable-accessibility",
        RustyKioskCommand.PassthroughNatural => "passthrough-natural",
        RustyKioskCommand.PassthroughContour => "passthrough-contour",
        RustyKioskCommand.ExitMetaHome => "exit-meta-home",
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    public static bool RequiresValue(this RustyKioskCommand command) => command is
        RustyKioskCommand.Select or
        RustyKioskCommand.AddTag or
        RustyKioskCommand.RemoveTag or
        RustyKioskCommand.SetLaunchRequirement;

    public static bool AllowsValue(this RustyKioskCommand command) =>
        command.RequiresValue() || command is RustyKioskCommand.SetSearch or RustyKioskCommand.FilterTag;

    public static string? ValidateValue(this RustyKioskCommand command, string? value)
    {
        var normalized = value?.Trim();
        if (normalized?.Length > RustyKioskContract.MaxCommandValueLength)
        {
            throw new ArgumentException(
                $"Rusty Kiosk operator values may not exceed {RustyKioskContract.MaxCommandValueLength} characters.",
                nameof(value));
        }
        if (command.RequiresValue() && string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException($"{command.ToWireName()} requires a value.", nameof(value));
        }
        if (!command.AllowsValue() && !string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException($"{command.ToWireName()} does not accept a value.", nameof(value));
        }
        if (command == RustyKioskCommand.SetLaunchRequirement)
        {
            _ = ParseLaunchRequirement(normalized!);
        }
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string ToWireName(this RustyKioskLaunchRequirement requirement) => requirement switch
    {
        RustyKioskLaunchRequirement.Any => "any",
        RustyKioskLaunchRequirement.WifiOn => "wifi-on",
        RustyKioskLaunchRequirement.WifiOff => "wifi-off",
        _ => throw new ArgumentOutOfRangeException(nameof(requirement))
    };

    public static RustyKioskLaunchRequirement ParseLaunchRequirement(string value) => value switch
    {
        "any" => RustyKioskLaunchRequirement.Any,
        "wifi-on" => RustyKioskLaunchRequirement.WifiOn,
        "wifi-off" => RustyKioskLaunchRequirement.WifiOff,
        _ => throw new ArgumentException(
            "Rusty Kiosk launch requirement must be exactly any, wifi-on, or wifi-off.",
            nameof(value))
    };

    public static string ToWireName(this RustyKioskPassthroughStyle style) => style switch
    {
        RustyKioskPassthroughStyle.Natural => "natural",
        RustyKioskPassthroughStyle.ContourLut => "contour-lut",
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };

    public static RustyKioskPassthroughStyle ParsePassthroughStyle(string value) => value switch
    {
        "natural" => RustyKioskPassthroughStyle.Natural,
        "contour-lut" => RustyKioskPassthroughStyle.ContourLut,
        _ => throw new InvalidDataException($"Rusty Kiosk returned an unknown passthrough style: {value}")
    };

    public static RustyKioskCommand Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        foreach (var command in Enum.GetValues<RustyKioskCommand>())
        {
            if (string.Equals(command.ToWireName(), normalized, StringComparison.Ordinal))
            {
                return command;
            }
        }

        throw new ArgumentException($"Unknown Rusty Kiosk command: {value}", nameof(value));
    }
}

public sealed record RustyKioskAppEntry(
    string Key,
    string Name,
    string? PackageName,
    bool Installed,
    bool Launchable,
    IReadOnlyList<string> Tags,
    RustyKioskLaunchRequirement LaunchRequirement = RustyKioskLaunchRequirement.Any)
{
    public string StatusLabel => !Installed ? "Not installed" : Launchable ? "Installed" : "Installed, no public launch activity";

    public string TagLabel => Tags.Count == 0 ? "No tags" : string.Join(", ", Tags);

    public string LaunchRequirementLabel => $"Launch requirement: {LaunchRequirement.ToWireName()}";

    public string DisplayLabel => $"{Name} — {StatusLabel}";
}

public sealed record RustyKioskState(
    int InstalledCount,
    int NotInstalledCount,
    int VisibleCount,
    bool VisibleEntriesTruncated,
    IReadOnlyList<RustyKioskAppEntry> Entries,
    string Search,
    string? TagFilter,
    string? SelectedKey,
    string? SelectedName,
    string? SelectedPackage,
    bool SelectedInstalled,
    bool SelectedLaunchable,
    bool WifiAdbEnabled,
    bool SetupHelperInstalled,
    bool SetupHelperReady,
    bool RequestWifiAdbAfterBoot,
    bool AccessibilityEnabled,
    bool GuardArmed,
    string? OperationInProgress,
    string StatusLine,
    string TagFilePath,
    long SearchFocusRequest = 0,
    long TagFocusRequest = 0,
    bool? ControlsOpen = null,
    RustyKioskLaunchRequirement? SelectedLaunchRequirement = null,
    bool? PendingRequirementLaunch = null,
    string? PendingRequirementLaunchId = null,
    RustyKioskPassthroughStyle? PassthroughStyle = null,
    bool? SystemPassthroughEnabled = null,
    bool? PassthroughLutApplied = null)
{
    public IReadOnlyList<string> Tags => Entries
        .SelectMany(static entry => entry.Tags)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(static tag => tag, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record RustyKioskOperatorResult(
    string Schema,
    string RequestId,
    RustyKioskCommand Command,
    bool Accepted,
    bool Completed,
    string Message,
    RustyKioskState State,
    [property: JsonIgnore] string RawJson)
{
    public static RustyKioskOperatorResult Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var schema = RequiredString(root, "schema");
        if (!string.Equals(schema, RustyKioskContract.ResultSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported Rusty Kiosk result schema: {schema}");
        }

        var state = root.GetProperty("state");
        var entryProperty = state.TryGetProperty("entries", out var completeEntries)
            ? completeEntries
            : state.GetProperty("visible_entries");
        var entries = entryProperty
            .EnumerateArray()
            .Select(static entry => new RustyKioskAppEntry(
                RequiredString(entry, "key"),
                RequiredString(entry, "name"),
                OptionalString(entry, "package"),
                entry.GetProperty("installed").GetBoolean(),
                entry.GetProperty("launchable").GetBoolean(),
                new ReadOnlyCollection<string>(entry.GetProperty("tags")
                    .EnumerateArray()
                    .Select(static tag => tag.GetString() ?? string.Empty)
                    .Where(static tag => tag.Length > 0)
                    .ToArray()),
                entry.TryGetProperty("launch_requirement", out var launchRequirement)
                    ? ParseResultLaunchRequirement(launchRequirement)
                    : RustyKioskLaunchRequirement.Any))
            .ToArray();

        var command = RustyKioskCommands.Parse(RequiredString(root, "command"));
        var parsedState = new RustyKioskState(
            state.GetProperty("installed_count").GetInt32(),
            state.GetProperty("not_installed_count").GetInt32(),
            state.GetProperty("visible_count").GetInt32(),
            state.GetProperty("visible_entries_truncated").GetBoolean(),
            new ReadOnlyCollection<RustyKioskAppEntry>(entries),
            RequiredString(state, "search"),
            OptionalString(state, "tag_filter"),
            OptionalString(state, "selected_key"),
            OptionalString(state, "selected_name"),
            OptionalString(state, "selected_package"),
            state.GetProperty("selected_installed").GetBoolean(),
            state.GetProperty("selected_launchable").GetBoolean(),
            state.GetProperty("wifi_adb_enabled").GetBoolean(),
            state.GetProperty("setup_helper_installed").GetBoolean(),
            state.GetProperty("setup_helper_ready").GetBoolean(),
            state.GetProperty("request_wifi_adb_after_boot").GetBoolean(),
            state.GetProperty("accessibility_enabled").GetBoolean(),
            state.GetProperty("guard_armed").GetBoolean(),
            OptionalString(state, "operation_in_progress"),
            RequiredString(state, "status_line"),
            OptionalString(state, "tag_file_path") ?? RustyKioskContract.TagFilePath,
            OptionalInt64(state, "search_focus_request") ?? 0,
            OptionalInt64(state, "tag_focus_request") ?? 0,
            OptionalBoolean(state, "controls_open"),
            OptionalString(state, "selected_launch_requirement") is { } selectedRequirement
                ? ParseResultLaunchRequirement(selectedRequirement)
                : null,
            OptionalBoolean(state, "pending_requirement_launch"),
            OptionalString(state, "pending_requirement_launch_id"),
            OptionalString(state, "passthrough_style") is { } passthroughStyle
                ? RustyKioskCommands.ParsePassthroughStyle(passthroughStyle)
                : null,
            OptionalBoolean(state, "system_passthrough_enabled"),
            OptionalBoolean(state, "passthrough_lut_applied"));

        return new RustyKioskOperatorResult(
            schema,
            RequiredString(root, "request_id"),
            command,
            root.GetProperty("accepted").GetBoolean(),
            root.GetProperty("completed").GetBoolean(),
            RequiredString(root, "message"),
            parsedState,
            json);
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        OptionalString(element, propertyName) ??
        throw new InvalidDataException($"Rusty Kiosk result is missing {propertyName}.");

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.GetString();
    }

    private static bool? OptionalBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetBoolean()
            : null;

    private static long? OptionalInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetInt64()
            : null;

    private static RustyKioskLaunchRequirement ParseResultLaunchRequirement(JsonElement property) =>
        property.ValueKind == JsonValueKind.String
            ? ParseResultLaunchRequirement(property.GetString()!)
            : throw new InvalidDataException("Rusty Kiosk launch_requirement must be a string.");

    private static RustyKioskLaunchRequirement ParseResultLaunchRequirement(string value)
    {
        try
        {
            return RustyKioskCommands.ParseLaunchRequirement(value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Rusty Kiosk returned an unknown launch requirement.", exception);
        }
    }
}

public sealed record RustyKioskBundle(
    string MainApkPath,
    string SetupHelperApkPath,
    string Source)
{
    public static RustyKioskBundle FromDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Rusty Kiosk bundle folder was not found: {directory}");
        }

        var main = Path.Combine(directory, RustyKioskContract.MainApkFileName);
        var helper = Path.Combine(directory, RustyKioskContract.SetupHelperApkFileName);
        if (!File.Exists(main) || !File.Exists(helper))
        {
            throw new FileNotFoundException(
                $"The bundle must contain {RustyKioskContract.MainApkFileName} and {RustyKioskContract.SetupHelperApkFileName}.");
        }

        return new RustyKioskBundle(main, helper, directory);
    }
}

public static class RustyKioskBundleLocator
{
    public const string EnvironmentVariable = "QUESTIONABLE_FILE_MANAGER_KIOSK_BUNDLE";
    public const string LegacyEnvironmentVariable = "META_QUEST_FILE_MANAGER_KIOSK_BUNDLE";

    public static RustyKioskBundle? TryFind(string? explicitDirectory = null)
    {
        var candidates = new[]
        {
            explicitDirectory,
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            Environment.GetEnvironmentVariable(LegacyEnvironmentVariable),
            Path.Combine(AppContext.BaseDirectory, "kiosk")
        };
        foreach (var candidate in candidates.Where(static candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            try
            {
                return RustyKioskBundle.FromDirectory(candidate!);
            }
            catch (IOException)
            {
            }
        }

        return null;
    }
}

public sealed record RustyKioskInstallResult(
    RustyKioskBundle Bundle,
    CommandResult HelperInstall,
    CommandResult SettingsGrant,
    CommandResult MainInstall,
    bool HelperReady,
    bool SameSignerControlGranted);

public sealed record RustyKioskProvisionResult(
    CommandResult SettingsGrant,
    bool HelperReady,
    bool SameSignerControlGranted,
    RustyKioskInstallationStatus Status);

public sealed record RustyKioskInstallationStatus(
    bool MainInstalled,
    string? MainVersion,
    bool SetupHelperInstalled,
    string? SetupHelperVersion,
    bool SetupHelperReady,
    bool SameSignerControlGranted,
    bool HostOperatorAvailable);

public enum RustyKioskProductChannel
{
    Stable,
    Labs
}

public sealed record RustyKioskProductContract(
    RustyKioskProductChannel Channel,
    string WireName,
    string MainPackage,
    string SetupHelperPackage,
    string OperatorAuthority,
    string SetupControlPermission,
    string SetupHelperControlAction)
{
    public string OperatorUri => "content://" + OperatorAuthority;

    public string MainActivity => MainPackage + "/.RustyKioskActivity";

    public static RustyKioskProductContract For(RustyKioskProductChannel channel) => channel switch
    {
        RustyKioskProductChannel.Stable => new(
            channel,
            "stable",
            "io.github.mesmerprism.rustykiosk",
            "io.github.mesmerprism.rustykiosk.setuphelper",
            "io.github.mesmerprism.rustykiosk.operator",
            "io.github.mesmerprism.rustykiosk.permission.SETUP_CONTROL",
            "io.github.mesmerprism.rustykiosk.setuphelper.action.CONTROL"),
        RustyKioskProductChannel.Labs => new(
            channel,
            "labs",
            "io.github.mesmerprism.rustykiosk.labs",
            "io.github.mesmerprism.rustykiosk.setuphelper.labs",
            "io.github.mesmerprism.rustykiosk.labs.operator",
            "io.github.mesmerprism.rustykiosk.labs.permission.SETUP_CONTROL",
            "io.github.mesmerprism.rustykiosk.setuphelper.labs.action.CONTROL"),
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    public static RustyKioskProductContract Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim() switch
        {
            "stable" => For(RustyKioskProductChannel.Stable),
            "labs" => For(RustyKioskProductChannel.Labs),
            _ => throw new ArgumentException(
                "Rusty Kiosk product channel must be exactly stable or labs.",
                nameof(value))
        };
    }
}

public static class RustyKioskContract
{
    public const string MainPackage = "io.github.mesmerprism.rustykiosk";
    public const string SetupHelperPackage = "io.github.mesmerprism.rustykiosk.setuphelper";
    public const string MainActivity = ".RustyKioskActivity";
    public const string OperatorAuthority = "io.github.mesmerprism.rustykiosk.operator";
    public const string OperatorUri = "content://" + OperatorAuthority;
    public const string PendingRequestExtra = "rusty_kiosk_pending_cli_request_id";
    public const string WriteSecureSettingsPermission = "android.permission.WRITE_SECURE_SETTINGS";
    public const string SetupControlPermission = "io.github.mesmerprism.rustykiosk.permission.SETUP_CONTROL";
    public const string ResultSchema = "rusty.kiosk.cli_result.v1";
    public const string HostOperatorSchema = "rusty.kiosk.host_operator.v2";
    public const string HostOperatorSuccessorSchema = "rusty.kiosk.host_operator.v4";
    public const string DirectUsbBootstrapSchema = "rusty.kiosk.direct_usb_bootstrap.v2";
    public const string TagFileSchema = "rusty.kiosk.app_tags.v1";
    public const string TagFileSuccessorSchema = "rusty.kiosk.app_tags.v2";
    public const string TagFilePath = "/sdcard/Android/data/io.github.mesmerprism.rustykiosk/files/tags/app-tags.v1.json";
    public const string MainApkFileName = "rusty-kiosk.apk";
    public const string SetupHelperApkFileName = "rusty-kiosk-setup-helper.apk";
    public const int MaxTagFileBytes = 256 * 1024;
    public const int MaxCommandValueLength = 160;
}

public static class RustyKioskTagFile
{
    public static string ValidateAndRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Rusty Kiosk tag file was not found.", fullPath);
        }

        if (info.Length > RustyKioskContract.MaxTagFileBytes)
        {
            throw new InvalidDataException($"Rusty Kiosk tag file exceeds {RustyKioskContract.MaxTagFileBytes} bytes.");
        }

        return Validate(File.ReadAllText(fullPath));
    }

    public static string Validate(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (Encoding.UTF8.GetByteCount(json) > RustyKioskContract.MaxTagFileBytes)
        {
            throw new InvalidDataException($"Rusty Kiosk tag file exceeds {RustyKioskContract.MaxTagFileBytes} bytes.");
        }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String ||
            (schema.GetString() != RustyKioskContract.TagFileSchema &&
             schema.GetString() != RustyKioskContract.TagFileSuccessorSchema))
        {
            throw new InvalidDataException("The file is not a supported Rusty Kiosk tag file.");
        }

        var strictSuccessor = schema.GetString() == RustyKioskContract.TagFileSuccessorSchema;
        if (strictSuccessor)
        {
            RequireExactFields(root, ["schema", "apps"], ["schema", "apps"]);
        }

        if (!root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Rusty Kiosk tag file must contain an apps array.");
        }

        if (apps.GetArrayLength() > 500)
        {
            throw new InvalidDataException("The Rusty Kiosk tag file contains too many app records.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in apps.EnumerateArray())
        {
            if (app.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Every Rusty Kiosk tag entry must be an object.");
            }
            if (strictSuccessor)
            {
                RequireExactFields(app, ["name"], ["name", "package", "tags", "requirements"]);
            }
            if (!app.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()) ||
                name.GetString()!.Trim().Length > 160)
            {
                throw new InvalidDataException("Every Rusty Kiosk tag entry requires a bounded app name.");
            }

            if (!app.TryGetProperty("tags", out var tags))
            {
                if (!strictSuccessor)
                {
                    throw new InvalidDataException("Every Rusty Kiosk tag entry requires a tags array.");
                }
                tags = default;
            }
            if (tags.ValueKind != JsonValueKind.Undefined && tags.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Every Rusty Kiosk tag entry requires a tags array.");
            }

            if (tags.ValueKind == JsonValueKind.Array &&
                (tags.GetArrayLength() > 64 || tags.EnumerateArray().Any(static tag =>
                    tag.ValueKind != JsonValueKind.String || tag.GetString()!.Trim().Length > 40)))
            {
                throw new InvalidDataException("Rusty Kiosk tags must be at most 64 strings no longer than 40 characters.");
            }

            if (strictSuccessor)
            {
                var packageName = app.TryGetProperty("package", out var package)
                    ? package.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(package.GetString())
                        ? package.GetString()!.Trim()
                        : throw new InvalidDataException("Rusty Kiosk v2 package values must be non-empty strings.")
                    : null;
                if (packageName is not null && !System.Text.RegularExpressions.Regex.IsMatch(
                        packageName,
                        "^[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z][A-Za-z0-9_]*)+$"))
                {
                    throw new InvalidDataException("Rusty Kiosk v2 package values must be Android package names.");
                }
                var identity = packageName is null
                    ? "name:" + string.Join(' ', name.GetString()!.Trim().Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant()
                    : "package:" + packageName;
                if (!identities.Add(identity))
                {
                    throw new InvalidDataException("Rusty Kiosk v2 app identities must be unique.");
                }

                if (app.TryGetProperty("requirements", out var requirements))
                {
                    if (requirements.ValueKind != JsonValueKind.Array || requirements.GetArrayLength() > 1)
                    {
                        throw new InvalidDataException("A Rusty Kiosk app may have at most one launch requirement.");
                    }
                    foreach (var requirement in requirements.EnumerateArray())
                    {
                        if (requirement.ValueKind != JsonValueKind.String ||
                            requirement.GetString() is not ("wifi-on" or "wifi-off"))
                        {
                            throw new InvalidDataException(
                                "Rusty Kiosk active requirements must be exactly wifi-on or wifi-off.");
                        }
                    }
                }
            }
        }

        return json;
    }

    private static void RequireExactFields(
        JsonElement element,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> allowed)
    {
        var actual = element.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (required.Any(field => !actual.Contains(field)) || actual.Any(field => !allowed.Contains(field)))
        {
            throw new InvalidDataException("Rusty Kiosk tag-file fields do not match the strict v2 schema.");
        }
    }
}
