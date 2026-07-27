using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class ProviderCapabilityDiscoveryTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse(
            "2026-07-27T10:00:00.0000000Z",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

    [Fact]
    public void Projections_AreClosedFreshAndRegistryDerived()
    {
        var awake =
            ProviderCapabilityDiscoveryProjection.CreateAwake(ObservedAt);
        var connectivity =
            ProviderCapabilityDiscoveryProjection.CreateConnectivity(
                ObservedAt);
        var kiosk =
            ProviderCapabilityDiscoveryProjection.CreateKioskCatalog(
                ObservedAt);

        foreach (var descriptor in new[] { awake, connectivity, kiosk })
        {
            Assert.Equal(
                ProviderCapabilityDiscoveryContract.Schema,
                descriptor.Schema);
            Assert.Equal(
                ProviderCapabilityDiscoveryContract.ProviderVersion,
                descriptor.Provider.Version);
            Assert.Equal(
                ProviderCapabilityDiscoveryContract.Placement,
                descriptor.Placement);
            Assert.Equal(
                ProviderCapabilityDiscoveryContract.DescriptorAvailable,
                descriptor.Availability.Status);
            Assert.Equal(ObservedAt, descriptor.Availability.ObservedAtUtc);
            Assert.Equal(
                ObservedAt.AddSeconds(
                    ProviderCapabilityDiscoveryContract.MaximumAgeSeconds),
                descriptor.Availability.ExpiresAtUtc);
            Assert.Equal(
                ProviderCapabilityDiscoveryContract.MaximumAgeSeconds,
                descriptor.Availability.MaximumAgeSeconds);
            Assert.Equal(
                ProviderCapabilityDiscoveryContract.DescriptionAuthentication,
                descriptor.DescriptionAuthentication);
            Assert.False(descriptor.AuthorizesExecution);
            Assert.False(descriptor.TargetSpecific);
            Assert.NotEmpty(descriptor.Capabilities);
            Assert.NotEmpty(descriptor.Exclusions);
        }

        Assert.Equal(
            QuestAwakeContract.Actions.Order(StringComparer.Ordinal),
            awake.Capabilities
                .SelectMany(static capability => capability.Actions)
                .Select(static action => action.Id));
        Assert.Equal(
            QuestConnectivityContract.Actions.Order(StringComparer.Ordinal),
            connectivity.Capabilities
                .SelectMany(static capability => capability.Actions)
                .Select(static action => action.Id)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [RustyKioskV2ProviderContract.CatalogSummaryScope],
            kiosk.Capabilities
                .SelectMany(static capability => capability.Actions)
                .Select(static action => action.Id));

        AssertKinds(
            awake,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = "observe",
                ["applyBounded"] = "effect",
                ["repairOnce"] = "effect",
                ["startDeviceWatchdog"] = "effect",
                ["stopWatchdogs"] = "cleanup",
                ["restoreNormal"] = "cleanup"
            });
        AssertKinds(
            connectivity,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = "observe",
                ["request_wireless_adb"] = "effect",
                ["enable_request_after_boot"] = "effect",
                ["disable_request_after_boot"] = "cleanup",
                ["disable_wireless_adb"] = "cleanup",
                ["enable_classic_tcpip_from_usb"] = "effect"
            });
        AssertKinds(
            kiosk,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RustyKioskV2ProviderContract.CatalogSummaryScope] =
                    "observe"
            });
        foreach (var capability in new[] { awake, connectivity, kiosk }
                     .SelectMany(static descriptor =>
                         descriptor.Capabilities))
        {
            Assert.Equal(
                capability.Actions
                    .Select(static action => action.Id)
                    .Order(StringComparer.Ordinal),
                capability.Actions.Select(static action => action.Id));
        }

        var awakeAuthentication = new[]
        {
            "process-access-control",
            "caller-authority-external",
            "exact-target-binding",
            "current-identity-revision"
        };
        foreach (var action in awake.Capabilities
                     .SelectMany(static capability =>
                         capability.Actions))
        {
            Assert.Equal(
                awakeAuthentication,
                action.AuthenticationRequirements);
        }

        var awakeCapability = Assert.Single(awake.Capabilities);
        Assert.Equal(
            [QuestAwakeContract.Version],
            awakeCapability.ContractVersions);
        Assert.Equal(
            "questionable-file-manager.quest-awake",
            awakeCapability.EffectOwner);
        Assert.Equal(
            QuestAwakeContract.ReceiptSchema,
            awakeCapability.ReceiptSchema);

        Assert.Equal(2, connectivity.Capabilities.Count);
        var modern = Assert.Single(
            connectivity.Capabilities,
            static capability =>
                capability.Id.EndsWith(
                    ".wireless-adb",
                    StringComparison.Ordinal));
        var classic = Assert.Single(
            connectivity.Capabilities,
            static capability =>
                capability.Id.EndsWith(
                    ".classic-tcpip",
                    StringComparison.Ordinal));
        Assert.Equal("rusty-kiosk.wireless-adb", modern.EffectOwner);
        Assert.Equal(
            "questionable-file-manager.quest-connectivity.classic-tcpip",
            classic.EffectOwner);
        Assert.Equal(
            [QuestConnectivityContract.RequestSchema],
            modern.ContractVersions);
        Assert.Equal(
            [QuestConnectivityContract.RequestSchema],
            classic.ContractVersions);
        Assert.Equal(
            QuestConnectivityContract.ReceiptSchema,
            modern.ReceiptSchema);
        Assert.Equal(
            QuestConnectivityContract.ReceiptSchema,
            classic.ReceiptSchema);
        var modernAuthentication = new[]
        {
            "process-access-control",
            "caller-authority-external",
            "exact-target-binding",
            "current-identity-revision",
            "effect-owner-profile",
            "owner-session-grant"
        };
        foreach (var action in modern.Actions)
        {
            Assert.Equal(
                action.Id == "request_wireless_adb"
                    ? [.. modernAuthentication, "wearer-approval"]
                    : modernAuthentication,
                action.AuthenticationRequirements);
        }
        Assert.Equal(
            [
                "process-access-control",
                "caller-authority-external",
                "exact-target-binding",
                "current-identity-revision",
                "effect-owner-profile"
            ],
            Assert.Single(classic.Actions)
                .AuthenticationRequirements);

        var kioskCapability = Assert.Single(kiosk.Capabilities);
        Assert.Equal(
            [RustyKioskV2ProviderContract.RequestSchema],
            kioskCapability.ContractVersions);
        Assert.Equal("rusty-kiosk.catalog", kioskCapability.EffectOwner);
        Assert.Equal(
            RustyKioskV2ProviderContract.ResponseSchema,
            kioskCapability.ReceiptSchema);
        Assert.Equal(
            [
                "process-access-control",
                "caller-authority-external",
                "exact-target-binding",
                "current-identity-revision",
                "effect-owner-profile",
                "owner-session-grant"
            ],
            Assert.Single(kioskCapability.Actions)
                .AuthenticationRequirements);

        foreach (var action in new[] { awake, connectivity, kiosk }
                     .SelectMany(static descriptor =>
                         descriptor.Capabilities)
                     .SelectMany(static capability =>
                         capability.Actions))
        {
            Assert.NotEmpty(action.AuthenticationRequirements);
            Assert.Equal(
                action.AuthenticationRequirements.Count,
                action.AuthenticationRequirements
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }
    }

    [Fact]
    public void SerializedDescriptor_HasExactExternalDtoShape()
    {
        var descriptor =
            ProviderCapabilityDiscoveryProjection.CreateConnectivity(
                ObservedAt);
        using var document = JsonDocument.Parse(
            ProviderCapabilityDiscoveryProjection.ToUtf8Json(descriptor));

        AssertExactProperties(
            document.RootElement,
            "schema",
            "provider",
            "placement",
            "availability",
            "description_authentication",
            "authorizes_execution",
            "target_specific",
            "capabilities",
            "exclusions");
        AssertExactProperties(
            document.RootElement.GetProperty("provider"),
            "id",
            "version");
        AssertExactProperties(
            document.RootElement.GetProperty("availability"),
            "status",
            "observed_at_utc",
            "expires_at_utc",
            "maximum_age_seconds");
        foreach (var capability in document.RootElement
                     .GetProperty("capabilities")
                     .EnumerateArray())
        {
            AssertExactProperties(
                capability,
                "id",
                "contract_versions",
                "actions",
                "effect_owner",
                "receipt_schema",
                "exclusions");
            foreach (var action in capability
                         .GetProperty("actions")
                         .EnumerateArray())
            {
                AssertExactProperties(
                    action,
                    "id",
                    "kind",
                    "authentication_requirements");
            }
        }
    }

    [Fact]
    public async Task ExactDescribeRoutes_DoNotReadInputOrCreateProviders()
    {
        var awakeFactoryCalls = 0;
        var awake = new QuestAwakeProviderSubprocessHost(
            () =>
            {
                awakeFactoryCalls++;
                throw new InvalidOperationException(
                    "Awake provider factory must remain unreachable.");
            },
            new FixedTimeProvider(ObservedAt));
        var awakeJson = await DescribeAsync(
            (input, output) => awake.RunAsync(
                [ProviderCapabilityDiscoveryContract.DescribeArgument],
                input,
                output));

        var connectivityFactoryCalls = 0;
        var connectivity =
            new QuestConnectivityProviderSubprocessHost(
                () =>
                {
                    connectivityFactoryCalls++;
                    throw new InvalidOperationException(
                        "Connectivity provider factory must remain unreachable.");
                },
                new FixedTimeProvider(ObservedAt));
        var connectivityJson = await DescribeAsync(
            (input, output) => connectivity.RunAsync(
                [ProviderCapabilityDiscoveryContract.DescribeArgument],
                input,
                output));

        var kioskFactoryCalls = 0;
        var kiosk = new RustyKioskV2CatalogSubprocessHost(
            () =>
            {
                kioskFactoryCalls++;
                throw new InvalidOperationException(
                    "Kiosk provider factory must remain unreachable.");
            },
            new FixedTimeProvider(ObservedAt));
        var kioskJson = await DescribeAsync(
            (input, output) => kiosk.RunAsync(
                [ProviderCapabilityDiscoveryContract.DescribeArgument],
                input,
                output));

        Assert.Equal(0, awakeFactoryCalls);
        Assert.Equal(0, connectivityFactoryCalls);
        Assert.Equal(0, kioskFactoryCalls);
        AssertDescriptor(awakeJson, "questionable-file-manager.quest-awake-provider");
        AssertDescriptor(
            connectivityJson,
            "questionable-file-manager.quest-connectivity-provider");
        AssertDescriptor(
            kioskJson,
            "questionable-file-manager.kiosk-v2-catalog-provider");
    }

    [Fact]
    public async Task AlternateMixedCaseAndExtraDescribeArgumentsFailClosed()
    {
        IReadOnlyList<string>[] damagedArguments =
        [
            ["--Describe-json"],
            ["--DESCRIBE-JSON"],
            ["--describe-json", "extra"],
            ["integration", "quest-awake", "--json", "--describe-json"]
        ];

        foreach (var arguments in damagedArguments)
        {
            var awake = new QuestAwakeProviderSubprocessHost(
                () => throw new InvalidOperationException(
                    "Awake factory must remain unreachable."),
                new FixedTimeProvider(ObservedAt));
            var connectivity =
                new QuestConnectivityProviderSubprocessHost(
                    () => throw new InvalidOperationException(
                        "Connectivity factory must remain unreachable."),
                    new FixedTimeProvider(ObservedAt));
            var kiosk = new RustyKioskV2CatalogSubprocessHost(
                () => throw new InvalidOperationException(
                    "Kiosk factory must remain unreachable."),
                new FixedTimeProvider(ObservedAt));

            Assert.Equal(
                2,
                await RejectAsync(
                    (input, output) => awake.RunAsync(
                        arguments.ToArray(),
                        input,
                        output)));
            Assert.Equal(
                2,
                await RejectAsync(
                    (input, output) => connectivity.RunAsync(
                        arguments.ToArray(),
                        input,
                        output)));
            Assert.Equal(
                2,
                await RejectAsync(
                    (input, output) => kiosk.RunAsync(
                        arguments,
                        input,
                        output)));
        }
    }

    private static void AssertKinds(
        ProviderCapabilityDiscoveryDescriptor descriptor,
        IReadOnlyDictionary<string, string> expected)
    {
        var actual = descriptor.Capabilities
            .SelectMany(static capability => capability.Actions)
            .ToDictionary(
                static action => action.Id,
                static action => action.Kind,
                StringComparer.Ordinal);
        Assert.Equal(expected.Count, actual.Count);
        foreach (var pair in expected)
            Assert.Equal(pair.Value, actual[pair.Key]);
    }

    private static async Task<string> DescribeAsync(
        Func<Stream, Stream, Task<int>> run)
    {
        await using var input = new PoisonInputStream();
        await using var output = new MemoryStream();

        Assert.Equal(0, await run(input, output));
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<int> RejectAsync(
        Func<Stream, Stream, Task<int>> run)
    {
        await using var input = new PoisonInputStream();
        await using var output = new MemoryStream();
        var exitCode = await run(input, output);
        using var document = JsonDocument.Parse(output.ToArray());
        if (document.RootElement.TryGetProperty(
                "schema",
                out var schema))
        {
            Assert.NotEqual(
                ProviderCapabilityDiscoveryContract.Schema,
                schema.GetString());
        }
        return exitCode;
    }

    private static void AssertDescriptor(
        string json,
        string providerId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            ProviderCapabilityDiscoveryContract.Schema,
            root.GetProperty("schema").GetString());
        Assert.Equal(
            providerId,
            root.GetProperty("provider").GetProperty("id").GetString());
        Assert.Equal(
            ObservedAt,
            root.GetProperty("availability")
                .GetProperty("observed_at_utc")
                .GetDateTimeOffset());
        Assert.False(root.GetProperty("authorizes_execution").GetBoolean());
        Assert.False(root.GetProperty("target_specific").GetBoolean());
    }

    private static void AssertExactProperties(
        JsonElement element,
        params string[] expected) =>
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal));

    private sealed class FixedTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PoisonInputStream : Stream
    {
        public override bool CanRead =>
            throw new InvalidOperationException(
                "Provider discovery must not inspect standard input.");
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(
            byte[] buffer,
            int offset,
            int count) =>
            throw new InvalidOperationException(
                "Provider discovery must not read standard input.");
        public override long Seek(
            long offset,
            SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }
}
