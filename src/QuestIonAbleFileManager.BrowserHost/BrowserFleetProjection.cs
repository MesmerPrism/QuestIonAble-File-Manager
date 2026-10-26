using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

internal static class BrowserFleetProjection
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    internal static BrowserCommandOutcome Project(BrowserFleetRequest request, OperatorCommand command, OperatorExecutionResult result)
    {
        if (!ReferenceEquals(result.Command, command) || result.FleetInstallerStatus is not { } status ||
            result with { FleetInstallerStatus = null } != new OperatorExecutionResult(command) || !Valid(status))
            return new(request.RequestId, request.Action, "failed", Error: "fleet_status_binding_invalid");
        var history = status.LastOutcome is null or "guided_installer_failed" or "guided_installer_completed" ? status.LastOutcome : "unknown";
        var projected = new BrowserFleetStatus(status.Schema, status.Status, status.Configured, status.SourceKind,
            status.Product, status.Version, status.Channel, status.DescriptorId, status.DescriptorSha256,
            status.HighestHandoffVersion, history, status.ObservedAtMs);
        return new(request.RequestId, request.Action, "completed", Fleet: new("status", projected));
    }
    private static bool Valid(FleetInstallerStatusReceipt status)
    {
        if (status.Schema != FleetInstallerContract.StatusSchema || status.ObservedAtMs is < 0 or > MaximumSafeInteger) return false;
        if (!status.Configured)
            return status.Status == "not_configured" && status.SourceKind == "none" && status.Product is null && status.Version is null &&
                status.Channel is null && status.DescriptorId is null && status.DescriptorSha256 is null && status.HighestHandoffVersion is null && status.LastOutcome is null;
        return status.Status is "ready" or "already_handed_off" or "not_newer_than_last_handoff" &&
            status.SourceKind is "embedded_pages_metadata" or "environment_pages_metadata" or "environment_local_fixture" &&
            (status.Channel == "stable" && status.Product == "rusty-fleet" || status.Channel == "labs" && status.Product == "rusty-fleet-labs") &&
            VersionValid(status.Version) && Identifier(status.DescriptorId) && Hash(status.DescriptorSha256) &&
            (status.HighestHandoffVersion is null || VersionValid(status.HighestHandoffVersion));
    }
    // Match Core's IsThreePartVersion/IsIdentifier exactly; their canonical
    // integer representation also provides these finite string bounds.
    private static bool VersionValid(string? value) => value is { Length: >= 5 and <= 32 } && value.Split('.').Length == 3 &&
        Version.TryParse(value, out var version) && version.Major >= 0 && version.Minor >= 0 && version.Build >= 0 && version.ToString(3) == value;
    private static bool Identifier(string? value) => value is { Length: >= 1 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));
    internal static string Error(Exception exception) => exception switch
    {
        OperationCanceledException => "fleet_status_cancelled", TimeoutException => "fleet_status_timeout",
        HttpRequestException or IOException => "fleet_release_unavailable",
        FleetInstallerException { Code: "fleet_installer_configuration_incomplete" or "fleet_embedded_configuration_invalid" or "fleet_descriptor_public_key_invalid" or
            "fleet_descriptor_signer_pin_mismatch" or "fleet_descriptor_source_invalid" or "fleet_fixture_path_invalid" or "fleet_trust_policy_invalid" or "fleet_installer_not_configured" } => "fleet_configuration_invalid",
        FleetInstallerException { Code: "fleet_installer_anchor_invalid" or "fleet_installer_anchor_missing" or "fleet_installer_initialization_invalid" or
            "fleet_installer_initialization_unavailable" or "fleet_installer_recovery_required" or "fleet_installer_state_invalid" or "fleet_installer_state_missing" or
            "fleet_stage_root_invalid" or "fleet_stage_root_nonlocal" } => "fleet_replay_unavailable",
        FleetInstallerException { Code: "fleet_source_failed" } => "fleet_release_unavailable",
        FleetInstallerException { Code: "fleet_descriptor_binding_invalid" or "fleet_descriptor_invalid" or "fleet_descriptor_payload_invalid" or
            "fleet_descriptor_payload_noncanonical" or "fleet_descriptor_signature_invalid" or "fleet_descriptor_signer_mismatch" or
            "fleet_descriptor_size_invalid" or "fleet_descriptor_stale" or "fleet_source_redirect_rejected" or "fleet_asset_source_invalid" or
            "fleet_asset_binding_invalid" or "fleet_asset_name_invalid" } => "fleet_release_rejected",
        _ => "fleet_status_unavailable"
    };
}
