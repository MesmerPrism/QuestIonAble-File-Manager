using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.Core;

public static class ProviderCapabilityDiscoveryContract
{
    public const string Schema =
        "rusty.quest.workflow.provider_capability_discovery.v1";
    public const string ProviderVersion = "0.1.0";
    public const string Placement = "windows-host-process";
    public const string DescriptorAvailable = "descriptor-available";
    public const string DescriptionAuthentication = "none";
    public const int MaximumAgeSeconds = 300;
    public const string DescribeArgument = "--describe-json";

    public static bool HasExactDescribeArguments(
        IReadOnlyList<string> arguments) =>
        arguments.Count == 1 &&
        string.Equals(
            arguments[0],
            DescribeArgument,
            StringComparison.Ordinal);
}

public sealed record ProviderCapabilityDiscoveryDescriptor(
    string Schema,
    ProviderCapabilityDiscoveryProvider Provider,
    string Placement,
    ProviderCapabilityDiscoveryAvailability Availability,
    string DescriptionAuthentication,
    bool AuthorizesExecution,
    bool TargetSpecific,
    IReadOnlyList<ProviderCapabilityDiscoveryCapability> Capabilities,
    IReadOnlyList<string> Exclusions);

public sealed record ProviderCapabilityDiscoveryProvider(
    string Id,
    string Version);

public sealed record ProviderCapabilityDiscoveryAvailability(
    string Status,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int MaximumAgeSeconds);

public sealed record ProviderCapabilityDiscoveryCapability(
    string Id,
    IReadOnlyList<string> ContractVersions,
    IReadOnlyList<ProviderCapabilityDiscoveryAction> Actions,
    string EffectOwner,
    string ReceiptSchema,
    IReadOnlyList<string> Exclusions);

public sealed record ProviderCapabilityDiscoveryAction(
    string Id,
    string Kind,
    IReadOnlyList<string> AuthenticationRequirements);

public static class ProviderCapabilityDiscoveryProjection
{
    private const string Observe = "observe";
    private const string Effect = "effect";
    private const string Cleanup = "cleanup";
    private const string ProcessAccessControl = "process-access-control";
    private const string CallerAuthorityExternal = "caller-authority-external";
    private const string ExactTargetBinding = "exact-target-binding";
    private const string CurrentIdentityRevision = "current-identity-revision";
    private const string EffectOwnerProfile = "effect-owner-profile";
    private const string OwnerSessionGrant = "owner-session-grant";
    private const string WearerApproval = "wearer-approval";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly string[] DescriptorExclusions =
    [
        "no-backend-probe",
        "no-credentials",
        "no-endpoints",
        "no-execution-authority",
        "no-invocation",
        "no-target-data"
    ];

    public static ProviderCapabilityDiscoveryDescriptor CreateAwake(
        DateTimeOffset observedAtUtc) =>
        Create(
            "questionable-file-manager.quest-awake-provider",
            observedAtUtc,
            [
                new ProviderCapabilityDiscoveryCapability(
                    "questionable-file-manager.quest-awake",
                    [QuestAwakeContract.Version],
                    QuestAwakeContract.Actions
                        .Order(StringComparer.Ordinal)
                        .Select(ProjectAwakeAction)
                        .ToArray(),
                    "questionable-file-manager.quest-awake",
                    QuestAwakeContract.ReceiptSchema,
                    [
                        "no-fleet-policy",
                        "no-generic-adb",
                        "no-generic-shell",
                        "no-target-resolution",
                        "no-windows-watchdog"
                    ])
            ]);

    public static ProviderCapabilityDiscoveryDescriptor CreateConnectivity(
        DateTimeOffset observedAtUtc)
    {
        const string classicAction =
            "enable_classic_tcpip_from_usb";
        return Create(
            "questionable-file-manager.quest-connectivity-provider",
            observedAtUtc,
            [
                new ProviderCapabilityDiscoveryCapability(
                    "questionable-file-manager.quest-connectivity.wireless-adb",
                    [QuestConnectivityContract.RequestSchema],
                    QuestConnectivityContract.Actions
                        .Where(action => action != classicAction)
                        .Order(StringComparer.Ordinal)
                        .Select(ProjectConnectivityAction)
                        .ToArray(),
                    "rusty-kiosk.wireless-adb",
                    QuestConnectivityContract.ReceiptSchema,
                    [
                        "no-endpoints",
                        "no-listener-usability-claim",
                        "no-profile-values",
                        "no-target-resolution",
                        "no-wearer-approval-claim"
                    ]),
                new ProviderCapabilityDiscoveryCapability(
                    "questionable-file-manager.quest-connectivity.classic-tcpip",
                    [QuestConnectivityContract.RequestSchema],
                    QuestConnectivityContract.Actions
                        .Where(action => action == classicAction)
                        .Order(StringComparer.Ordinal)
                        .Select(ProjectConnectivityAction)
                        .ToArray(),
                    "questionable-file-manager.quest-connectivity.classic-tcpip",
                    QuestConnectivityContract.ReceiptSchema,
                    [
                        "no-adb-daemon-lifecycle",
                        "no-endpoints",
                        "no-profile-values",
                        "no-target-resolution"
                    ])
            ]);
    }

    public static ProviderCapabilityDiscoveryDescriptor CreateKioskCatalog(
        DateTimeOffset observedAtUtc) =>
        Create(
            "questionable-file-manager.kiosk-v2-catalog-provider",
            observedAtUtc,
            [
                new ProviderCapabilityDiscoveryCapability(
                    "rusty-kiosk.catalog-summary",
                    [RustyKioskV2ProviderContract.RequestSchema],
                    [
                        new ProviderCapabilityDiscoveryAction(
                            RustyKioskV2ProviderContract.CatalogSummaryScope,
                            Observe,
                            [
                                ProcessAccessControl,
                                CallerAuthorityExternal,
                                ExactTargetBinding,
                                CurrentIdentityRevision,
                                EffectOwnerProfile,
                                OwnerSessionGrant
                            ])
                    ],
                    "rusty-kiosk.catalog",
                    RustyKioskV2ProviderContract.ResponseSchema,
                    [
                        "no-catalog-data",
                        "no-credentials",
                        "no-endpoints",
                        "no-owner-session-data",
                        "no-target-resolution"
                    ])
            ]);

    public static async Task WriteAsync(
        Stream output,
        ProviderCapabilityDiscoveryDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!output.CanWrite)
            throw new InvalidOperationException(
                "Provider discovery output is not writable.");

        await JsonSerializer.SerializeAsync(
                output,
                descriptor,
                Json,
                cancellationToken)
            .ConfigureAwait(false);
        await output.WriteAsync(
                "\n"u8.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static byte[] ToUtf8Json(
        ProviderCapabilityDiscoveryDescriptor descriptor) =>
        JsonSerializer.SerializeToUtf8Bytes(descriptor, Json);

    private static ProviderCapabilityDiscoveryDescriptor Create(
        string providerId,
        DateTimeOffset observedAtUtc,
        IReadOnlyList<ProviderCapabilityDiscoveryCapability> capabilities)
    {
        var observed = observedAtUtc.ToUniversalTime();
        return new ProviderCapabilityDiscoveryDescriptor(
            ProviderCapabilityDiscoveryContract.Schema,
            new ProviderCapabilityDiscoveryProvider(
                providerId,
                ProviderCapabilityDiscoveryContract.ProviderVersion),
            ProviderCapabilityDiscoveryContract.Placement,
            new ProviderCapabilityDiscoveryAvailability(
                ProviderCapabilityDiscoveryContract.DescriptorAvailable,
                observed,
                observed.AddSeconds(
                    ProviderCapabilityDiscoveryContract.MaximumAgeSeconds),
                ProviderCapabilityDiscoveryContract.MaximumAgeSeconds),
            ProviderCapabilityDiscoveryContract.DescriptionAuthentication,
            AuthorizesExecution: false,
            TargetSpecific: false,
            capabilities,
            DescriptorExclusions);
    }

    private static ProviderCapabilityDiscoveryAction ProjectAwakeAction(
        string action) =>
        action switch
        {
            "status" => Action(action, Observe, AwakeAuthentication()),
            "applyBounded" or
            "repairOnce" or
            "startDeviceWatchdog" =>
                Action(action, Effect, AwakeAuthentication()),
            "stopWatchdogs" or
            "restoreNormal" =>
                Action(action, Cleanup, AwakeAuthentication()),
            _ => throw new InvalidOperationException(
                $"Unclassified Quest awake action '{action}'.")
        };

    private static ProviderCapabilityDiscoveryAction ProjectConnectivityAction(
        string action) =>
        action switch
        {
            "status" =>
                Action(action, Observe, ModernConnectivityAuthentication()),
            "request_wireless_adb" =>
                Action(
                    action,
                    Effect,
                    [
                        .. ModernConnectivityAuthentication(),
                        WearerApproval
                    ]),
            "enable_request_after_boot" =>
                Action(action, Effect, ModernConnectivityAuthentication()),
            "disable_request_after_boot" or
            "disable_wireless_adb" =>
                Action(action, Cleanup, ModernConnectivityAuthentication()),
            "enable_classic_tcpip_from_usb" =>
                Action(
                    action,
                    Effect,
                    [
                        ProcessAccessControl,
                        CallerAuthorityExternal,
                        ExactTargetBinding,
                        CurrentIdentityRevision,
                        EffectOwnerProfile
                    ]),
            _ => throw new InvalidOperationException(
                $"Unclassified Quest connectivity action '{action}'.")
        };

    private static ProviderCapabilityDiscoveryAction Action(
        string id,
        string kind,
        IReadOnlyList<string> authenticationRequirements) =>
        new(id, kind, authenticationRequirements);

    private static string[] AwakeAuthentication() =>
    [
        ProcessAccessControl,
        CallerAuthorityExternal,
        ExactTargetBinding,
        CurrentIdentityRevision
    ];

    private static string[] ModernConnectivityAuthentication() =>
    [
        ProcessAccessControl,
        CallerAuthorityExternal,
        ExactTargetBinding,
        CurrentIdentityRevision,
        EffectOwnerProfile,
        OwnerSessionGrant
    ];
}
