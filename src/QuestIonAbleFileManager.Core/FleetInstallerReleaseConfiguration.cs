// Fleet installer release trust is reviewed source, never an MSBuild,
// environment, generated-obj, or release-script input. This checked-in file is
// intentionally inert until an official release commit adds the complete eight
// QuestIonAbleFileManager.FleetInstaller.* AssemblyMetadata attributes defined
// in docs/fleet-installer-handoff.md. The release gate validates the exact
// clean tagged commit and compiled metadata before any official signing build.

namespace QuestIonAbleFileManager.Core;

internal static class FleetInstallerReleaseConfiguration
{
    internal const string Authority =
        "checked-in-reviewed-release-source";
}
