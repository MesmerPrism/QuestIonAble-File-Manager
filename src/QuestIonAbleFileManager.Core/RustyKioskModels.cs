using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.Core;

public enum RustyKioskCommand
{
    Status,
    Reload,
    SetSearch,
    Select,
    FilterTag,
    AddTag,
    RemoveTag,
    LaunchNormal,
    LaunchKiosk,
    CheckSetupHelper,
    RequestWifiAdb,
    EnableWifiAfterBoot,
    DisableWifiAfterBoot,
    DisableWifiAdb,
    EnableAccessibility,
    DisableAccessibility,
    ExitMetaHome
}

public static class RustyKioskCommands
{
    public static string ToWireName(this RustyKioskCommand command) => command switch
    {
        RustyKioskCommand.Status => "status",
        RustyKioskCommand.Reload => "reload",
        RustyKioskCommand.SetSearch => "set-search",
        RustyKioskCommand.Select => "select",
        RustyKioskCommand.FilterTag => "filter-tag",
        RustyKioskCommand.AddTag => "add-tag",
        RustyKioskCommand.RemoveTag => "remove-tag",
        RustyKioskCommand.LaunchNormal => "launch-normal",
        RustyKioskCommand.LaunchKiosk => "launch-kiosk",
        RustyKioskCommand.CheckSetupHelper => "check-setup-helper",
        RustyKioskCommand.RequestWifiAdb => "request-wifi-adb",
        RustyKioskCommand.EnableWifiAfterBoot => "enable-wifi-adb-after-boot",
        RustyKioskCommand.DisableWifiAfterBoot => "disable-wifi-adb-after-boot",
        RustyKioskCommand.DisableWifiAdb => "disable-wifi-adb",
        RustyKioskCommand.EnableAccessibility => "enable-accessibility",
        RustyKioskCommand.DisableAccessibility => "disable-accessibility",
        RustyKioskCommand.ExitMetaHome => "exit-meta-home",
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    public static bool RequiresValue(this RustyKioskCommand command) => command is
        RustyKioskCommand.Select or
        RustyKioskCommand.AddTag or
        RustyKioskCommand.RemoveTag;

    public static bool AllowsValue(this RustyKioskCommand command) =>
        command.RequiresValue() || command is RustyKioskCommand.SetSearch or RustyKioskCommand.FilterTag;

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
    IReadOnlyList<string> Tags)
{
    public string StatusLabel => !Installed ? "Not installed" : Launchable ? "Installed" : "Installed, no public launch activity";

    public string TagLabel => Tags.Count == 0 ? "No tags" : string.Join(", ", Tags);

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
    string TagFilePath)
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
                    .ToArray())))
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
            OptionalString(state, "tag_file_path") ?? RustyKioskContract.TagFilePath);

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
    public const string TagFilePath = "/sdcard/Android/data/io.github.mesmerprism.rustykiosk/files/tags/app-tags.v1.json";
    public const string MainApkFileName = "rusty-kiosk.apk";
    public const string SetupHelperApkFileName = "rusty-kiosk-setup-helper.apk";
    public const int MaxTagFileBytes = 256 * 1024;
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

        var json = File.ReadAllText(fullPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("schema", out var schema) ||
            !string.Equals(schema.GetString(), RustyKioskContract.TagFileSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The file is not a rusty.kiosk.app_tags.v1 tag file.");
        }

        if (!root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Rusty Kiosk tag file must contain an apps array.");
        }

        foreach (var app in apps.EnumerateArray())
        {
            if (!app.TryGetProperty("name", out var name) ||
                string.IsNullOrWhiteSpace(name.GetString()) ||
                name.GetString()!.Trim().Length > 160)
            {
                throw new InvalidDataException("Every Rusty Kiosk tag entry requires a bounded app name.");
            }

            if (!app.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Every Rusty Kiosk tag entry requires a tags array.");
            }

            if (tags.EnumerateArray().Any(static tag => tag.ValueKind != JsonValueKind.String || tag.GetString()!.Trim().Length > 40))
            {
                throw new InvalidDataException("Rusty Kiosk tags must be strings no longer than 40 characters.");
            }
        }

        return json;
    }
}
