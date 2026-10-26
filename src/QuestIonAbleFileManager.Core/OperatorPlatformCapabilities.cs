using System.Runtime.InteropServices;

namespace QuestIonAbleFileManager.Core;

/// <summary>
/// Host implementation availability only. This does not discover tools, select
/// a target, activate a provider, or admit an operation.
/// </summary>
public sealed record OperatorPlatformCapabilities(
    string Platform,
    bool HostAdbSupported,
    bool ConnectivityProfilesSupported,
    bool FleetInstallerSupported,
    bool SecureLocalApiStateSupported)
{
    public string? ConnectivityProfilesUnavailableReason =>
        ConnectivityProfilesSupported ? null : "windows_credential_manager_required";
    public string? FleetInstallerUnavailableReason =>
        FleetInstallerSupported ? null : "windows_fleet_installer_required";
    public string? SecureLocalApiStateUnavailableReason =>
        SecureLocalApiStateSupported ? null : "windows_secure_state_required";

    public static OperatorPlatformCapabilities Current { get; } = ForPlatform(
        OperatingSystem.IsWindows() ? OSPlatform.Windows :
        OperatingSystem.IsLinux() ? OSPlatform.Linux :
        OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Create("OTHER"));

    internal static OperatorPlatformCapabilities ForPlatform(OSPlatform platform)
    {
        var windows = platform == OSPlatform.Windows;
        var linux = platform == OSPlatform.Linux;
        var macos = platform == OSPlatform.OSX;
        return new(windows ? "windows" : linux ? "linux" : macos ? "macos" : "unsupported",
            windows || linux || macos, windows, windows, windows);
    }
}
