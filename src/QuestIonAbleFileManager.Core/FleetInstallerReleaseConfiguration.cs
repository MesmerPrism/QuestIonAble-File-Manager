// Fleet installer release trust is reviewed source, never an MSBuild,
// environment, generated-obj, or release-script input. The release gate
// validates this exact complete eight-field block in the clean tagged commit
// and in the compiled binaries before any official signing build.

using System.Reflection;

[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.ConfigurationVersion", "2")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.DescriptorUri", "https://mesmerprism.com/Rusty-Fleet/metadata/labs/release.json")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.DescriptorPublicKeySpkiBase64", "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAyl1cFVrX60MXjRyAuZFafYCk5fe6otYMSKO03YItlAZcBzGfyVWloKR49Pexcts6YWvlsPquWong9FzpauVV7HdkL2bIPtzdW18yRVKZFrZt/sXvNtQLYlL8rwaW67/Y6K/mlheanXivcXfzHZghKIAuDYQs/G8q/qEzpFulP/H+sKE8q7xcAR/GMH4IrHxF7jmzP/8j89AAwR5W/c4uyJC5RL5Lwzcp5yWMBsYMXHzM1PE1HxiRzNXj8WhllqRkuh0KftKUqE9kYJQvddhRRCHUiPwjo2QE4Q7UtwANI+KMmi3QZo/FzRKrnayaJvcmz9USYP0XzvM35xywm7nsTTKzK/qp4tPV24YJM3FLEEYqGpltw+S7Q3oqBD/9myi9WHtpGVNnoUNboGUGCQZlOpE3i3muicFiSmCDswR/IwHy+AD434Do7P14und+Y6Rpif4dM5zBDUhqB0SBh0Y1IsgD6BxlVwZFgS47Jvq4BjJPmtYh6oMvKJZGxI3GWroFAgMBAAE=")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.DescriptorSignerSpkiSha256", "0b3ef04dc5481d5e0a0a243df298c31052501e014a6e27516c48b95846657d0c")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.InstallerSignerCertificateSha256", "baead63c37e32085c3af19b4c739a6a308d700529f107d40e14fec2c94fe7ddf")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.ProvisioningSetupSignerCertificateSha256", "baead63c37e32085c3af19b4c739a6a308d700529f107d40e14fec2c94fe7ddf")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.Channel", "labs")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.StateRootRelativePath", "QuestIonAbleFileManagerLabs/FleetInstaller")]

namespace QuestIonAbleFileManager.Core;

internal static class FleetInstallerReleaseConfiguration
{
    internal const string Authority =
        "checked-in-reviewed-release-source";
}
