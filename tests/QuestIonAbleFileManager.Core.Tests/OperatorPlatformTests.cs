using System.Runtime.InteropServices;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class OperatorPlatformTests
{
    [Theory]
    [InlineData("WINDOWS", "windows", true, true)]
    [InlineData("LINUX", "linux", true, false)]
    [InlineData("OSX", "macos", true, false)]
    [InlineData("OTHER", "unsupported", false, false)]
    public void PlatformCapabilitiesDoNotAdvertiseUnimplementedNativeSafetyBoundaries(
        string platform, string name, bool adbSupported, bool nativeSupported)
    {
        var capabilities = OperatorPlatformCapabilities.ForPlatform(OSPlatform.Create(platform));
        Assert.Equal(name, capabilities.Platform);
        Assert.Equal(adbSupported, capabilities.HostAdbSupported);
        Assert.Equal(nativeSupported, capabilities.ConnectivityProfilesSupported);
        Assert.Equal(nativeSupported, capabilities.FleetInstallerSupported);
        Assert.Equal(nativeSupported, capabilities.SecureLocalApiStateSupported);
        Assert.Equal(nativeSupported ? null : "windows_credential_manager_required",
            capabilities.ConnectivityProfilesUnavailableReason);
        Assert.Equal(nativeSupported ? null : "windows_fleet_installer_required",
            capabilities.FleetInstallerUnavailableReason);
        Assert.Equal(nativeSupported ? null : "windows_secure_state_required",
            capabilities.SecureLocalApiStateUnavailableReason);
    }

    [Fact]
    public async Task AdbDiscoveryDoesNotInitializeOptionalNativeServices()
    {
        var executor = new OperatorCommandExecutor(new AdbClient("fake-adb", new DiscoveryRunner()),
            fleetInstallerFactory: () => throw new InvalidOperationException("Unexpected Fleet initialization."),
            connectivityProfilesFactory: () => throw new InvalidOperationException("Unexpected credential initialization."));

        var result = await executor.ExecuteAsync(OperatorCommands.DiscoverDevices());

        Assert.Equal("QUEST123", Assert.Single(result.Devices!).Serial);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalServiceFactoryIsAccessedOnlyByItsOwnRoute(bool fleet)
    {
        var expected = new InvalidOperationException("Selected native service is unavailable.");
        var executor = new OperatorCommandExecutor(client: null,
            fleetInstallerFactory: () => throw (fleet ? expected : new Exception("Wrong service.")),
            connectivityProfilesFactory: () => throw (!fleet ? expected : new Exception("Wrong service.")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            fleet ? OperatorCommands.FleetInstallStatus() : OperatorCommands.ListQuestConnectivityProfiles()));

        Assert.Same(expected, error);
    }

    [Fact]
    public async Task DefaultNativeRoutesRemainExplicitlyUnsupportedOutsideWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        var executor = new OperatorCommandExecutor(new AdbClient("fake-adb", new DiscoveryRunner()));

        var profiles = await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            executor.ExecuteAsync(OperatorCommands.ListQuestConnectivityProfiles()));
        var fleet = await Assert.ThrowsAsync<PlatformNotSupportedException>(() =>
            executor.ExecuteAsync(OperatorCommands.FleetInstallStatus()));

        Assert.Equal("windows_credential_manager_required", profiles.Message);
        Assert.Equal("windows_fleet_installer_required", fleet.Message);
    }

    private sealed class DiscoveryRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Assert.Equal(new[] { "devices", "-l" }, arguments);
            return Task.FromResult(new CommandResult(fileName, arguments, 0,
                "List of devices attached\nQUEST123 device model:Quest\n", "", TimeSpan.Zero));
        }
    }
}
