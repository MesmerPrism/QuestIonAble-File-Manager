using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.Core;

public static class FleetIntegrationContract
{
    public const string Version = "1.0";
    public const string ResponseSchema = "questionable.file_manager.integration.response.v1";
    public const string CapabilitySchema = "questionable.file_manager.integration.capability_snapshot.v1";
    public const string ObservationSchema = "questionable.file_manager.integration.device_observation.v1";
    public const string BindingSchema = "questionable.file_manager.integration.device_binding.v1";
    public const string MutationAuthoritySchema = "questionable.file_manager.integration.mutation_authority.v1";
    public const string RequestSchema = "questionable.file_manager.integration.operation_request.v1";
    public const string ResultSchema = "questionable.file_manager.integration.operation_result.v1";
    public const string OperationStatusSchema = "questionable.file_manager.integration.operation_status.v1";
    public const string RootProfile = "adb-shared";
    public const string RemoteRoot = "/sdcard";
    public const int MaximumRequestBytes = 64 * 1024;
    public const int MaximumListEntries = 1_000;
    public const long MaximumPullBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumPushBytes = 4L * 1024 * 1024 * 1024;
    public static readonly TimeSpan MaximumObservationAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumRequestLifetime = TimeSpan.FromMinutes(5);
}

[JsonConverter(typeof(JsonStringEnumConverter<FleetIntegrationStatus>))]
public enum FleetIntegrationStatus
{
    Ready,
    Completed,
    Disabled,
    Absent,
    Unsupported,
    Unavailable,
    Unauthorized,
    Rejected,
    Failed,
    Cancelled
}

public sealed record FleetIntegrationRootProfile(
    string Id,
    string RemoteRoot,
    string? LocalStagingRoot,
    bool ReadOnly);

public sealed record FleetIntegrationCapabilitySnapshot(
    string Schema,
    string ContractVersion,
    FleetIntegrationStatus State,
    string AdapterEpoch,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<string> SupportedContractVersions,
    IReadOnlyList<string> Operations,
    IReadOnlyList<FleetIntegrationRootProfile> RootProfiles,
    int MaximumConcurrentOperations,
    string? SelectedAdbIdentity,
    string? Reason);

public sealed record FleetIntegrationDeviceObservation(
    string Schema,
    string ContractVersion,
    string AdapterEpoch,
    string ObservationId,
    string Serial,
    string Transport,
    string State,
    string? Model,
    string? Product,
    DateTimeOffset ObservedAtUtc);

public sealed record FleetIntegrationDeviceBinding(
    string Schema,
    string ObservationId,
    string Serial,
    string Transport,
    DateTimeOffset ObservedAtUtc);

public sealed record FleetIntegrationOperation(
    string Kind,
    string RootProfile,
    string RelativePath,
    int? MaximumEntries,
    long? MaximumBytes,
    string? LocalArtifactPath = null,
    long? ExpectedSizeBytes = null,
    string? ExpectedSha256 = null);

public sealed record FleetIntegrationMutationAuthority(
    string Schema,
    string FleetDeviceId,
    long FleetIdentityRevision,
    string QuestIdentityProofId,
    string ManifoldCommandId,
    string ManifoldLeaseId,
    string ManifoldProviderEpoch,
    long RevocationBarrierRevision,
    DateTimeOffset ExpiresAtUtc);

public sealed record FleetIntegrationOperationRequest(
    string Schema,
    string ContractVersion,
    string RequestId,
    string OperationId,
    string AdapterEpoch,
    DateTimeOffset ExpiresAtUtc,
    FleetIntegrationDeviceBinding DeviceBinding,
    FleetIntegrationOperation Operation,
    FleetIntegrationMutationAuthority? MutationAuthority = null)
{
    public static FleetIntegrationOperationRequest Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0)
        {
            throw FleetIntegrationException.Input("request_empty", "The integration request file is empty.");
        }

        if (utf8Json.Length > FleetIntegrationContract.MaximumRequestBytes)
        {
            throw FleetIntegrationException.Input(
                "request_too_large",
                $"The integration request exceeds {FleetIntegrationContract.MaximumRequestBytes} bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                utf8Json.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            var root = document.RootElement;
            RequireObject(root, "request");
            RequireAllowedProperties(
                root,
                "request",
                [
                    "schema",
                    "contractVersion",
                    "requestId",
                    "operationId",
                    "adapterEpoch",
                    "expiresAtUtc",
                    "deviceBinding",
                    "operation",
                    "mutationAuthority"
                ],
                [
                    "schema",
                    "contractVersion",
                    "requestId",
                    "operationId",
                    "adapterEpoch",
                    "expiresAtUtc",
                    "deviceBinding",
                    "operation"
                ]);

            var schema = RequireString(root, "schema", 128);
            if (!string.Equals(schema, FleetIntegrationContract.RequestSchema, StringComparison.Ordinal))
            {
                throw FleetIntegrationException.Unsupported(
                    $"Unsupported request schema '{schema}'. Expected '{FleetIntegrationContract.RequestSchema}'.");
            }

            var contractVersion = RequireString(root, "contractVersion", 16);
            if (!string.Equals(contractVersion, FleetIntegrationContract.Version, StringComparison.Ordinal))
            {
                throw FleetIntegrationException.Unsupported(
                    $"Unsupported integration contract version '{contractVersion}'.");
            }

            var requestId = RequireIdentifier(root, "requestId");
            var operationId = RequireIdentifier(root, "operationId");
            var adapterEpoch = RequireIdentifier(root, "adapterEpoch");
            var expiresAtUtc = RequireTimestamp(root, "expiresAtUtc");
            var binding = ParseBinding(root.GetProperty("deviceBinding"));
            var operation = ParseOperation(root.GetProperty("operation"));
            var mutationAuthority =
                root.TryGetProperty("mutationAuthority", out var mutationAuthorityElement)
                    ? ParseMutationAuthority(mutationAuthorityElement)
                    : null;

            return new FleetIntegrationOperationRequest(
                schema,
                contractVersion,
                requestId,
                operationId,
                adapterEpoch,
                expiresAtUtc,
                binding,
                operation,
                mutationAuthority);
        }
        catch (FleetIntegrationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw FleetIntegrationException.Input(
                "request_invalid_json",
                $"The integration request is not strict JSON: {exception.Message}");
        }
    }

    private static FleetIntegrationDeviceBinding ParseBinding(JsonElement element)
    {
        RequireObject(element, "deviceBinding");
        RequireExactProperties(
            element,
            "deviceBinding",
            ["schema", "observationId", "serial", "transport", "observedAtUtc"]);

        var schema = RequireString(element, "schema", 128);
        if (!string.Equals(schema, FleetIntegrationContract.BindingSchema, StringComparison.Ordinal))
        {
            throw FleetIntegrationException.Unsupported(
                $"Unsupported device-binding schema '{schema}'.");
        }

        var transport = RequireString(element, "transport", 16);
        if (transport is not ("usb" or "wifi"))
        {
            throw FleetIntegrationException.Input(
                "binding_transport_invalid",
                "The device binding transport must be 'usb' or 'wifi'.");
        }

        return new FleetIntegrationDeviceBinding(
            schema,
            RequireHexIdentifier(element, "observationId"),
            AndroidInput.RequireSerial(RequireString(element, "serial", 255)),
            transport,
            RequireTimestamp(element, "observedAtUtc"));
    }

    private static FleetIntegrationOperation ParseOperation(JsonElement element)
    {
        RequireObject(element, "operation");
        RequireAllowedProperties(
            element,
            "operation",
            [
                "kind",
                "rootProfile",
                "relativePath",
                "maximumEntries",
                "maximumBytes",
                "localArtifactPath",
                "expectedSizeBytes",
                "expectedSha256"
            ],
            ["kind", "rootProfile", "relativePath"]);

        var kind = RequireString(element, "kind", 16);
        var rootProfile = RequireString(element, "rootProfile", 32);
        if (!string.Equals(rootProfile, FleetIntegrationContract.RootProfile, StringComparison.Ordinal))
        {
            throw FleetIntegrationException.Input(
                "root_profile_unsupported",
                $"Only the '{FleetIntegrationContract.RootProfile}' root profile is supported.");
        }

        var relativePath = RequireString(element, "relativePath", FleetPathPolicy.MaximumRelativePathLength, allowEmpty: true);
        var maximumEntries = OptionalInt32(element, "maximumEntries");
        var maximumBytes = OptionalInt64(element, "maximumBytes");
        var localArtifactPath = OptionalString(
            element,
            "localArtifactPath",
            FleetPathPolicy.MaximumRelativePathLength);
        var expectedSizeBytes = OptionalInt64(element, "expectedSizeBytes");
        var expectedSha256 = OptionalLowerHexDigest(element, "expectedSha256");

        switch (kind)
        {
            case "list":
                if (maximumEntries is null or < 1 or > FleetIntegrationContract.MaximumListEntries)
                {
                    throw FleetIntegrationException.Input(
                        "maximum_entries_invalid",
                        $"List requests require maximumEntries between 1 and {FleetIntegrationContract.MaximumListEntries}.");
                }
                if (maximumBytes is not null)
                {
                    throw FleetIntegrationException.Input(
                        "operation_field_invalid",
                        "List requests must not include maximumBytes.");
                }
                RequireAbsentPushFields(localArtifactPath, expectedSizeBytes, expectedSha256, "List");
                break;
            case "pull":
                if (string.IsNullOrEmpty(relativePath))
                {
                    throw FleetIntegrationException.Input(
                        "pull_path_empty",
                        "Pull requests require a non-empty relativePath.");
                }
                if (maximumBytes is null or < 1 or > FleetIntegrationContract.MaximumPullBytes)
                {
                    throw FleetIntegrationException.Input(
                        "maximum_bytes_invalid",
                        $"Pull requests require maximumBytes between 1 and {FleetIntegrationContract.MaximumPullBytes}.");
                }
                if (maximumEntries is not null)
                {
                    throw FleetIntegrationException.Input(
                        "operation_field_invalid",
                        "Pull requests must not include maximumEntries.");
                }
                RequireAbsentPushFields(localArtifactPath, expectedSizeBytes, expectedSha256, "Pull");
                break;
            case "push":
                if (string.IsNullOrEmpty(relativePath))
                {
                    throw FleetIntegrationException.Input(
                        "push_path_empty",
                        "Push requests require a non-empty relativePath.");
                }
                if (maximumBytes is null or < 1 or > FleetIntegrationContract.MaximumPushBytes)
                {
                    throw FleetIntegrationException.Input(
                        "maximum_bytes_invalid",
                        $"Push requests require maximumBytes between 1 and {FleetIntegrationContract.MaximumPushBytes}.");
                }
                if (maximumEntries is not null)
                {
                    throw FleetIntegrationException.Input(
                        "operation_field_invalid",
                        "Push requests must not include maximumEntries.");
                }
                if (localArtifactPath is null ||
                    expectedSizeBytes is null or < 1 ||
                    expectedSizeBytes > maximumBytes)
                {
                    throw FleetIntegrationException.Input(
                        "push_source_invalid",
                        "Push requires a staged localArtifactPath and expectedSizeBytes within maximumBytes.");
                }
                FleetPathPolicy.ValidatePushArtifactPath(localArtifactPath);
                if (expectedSha256 is null)
                {
                    throw FleetIntegrationException.Input(
                        "push_source_invalid",
                        "Push requires a lowercase SHA-256 expectedSha256.");
                }
                break;
            default:
                throw FleetIntegrationException.Input(
                    "operation_unsupported",
                    "Only 'list', 'pull', and explicitly authorized 'push' integration operations are supported.");
        }

        return new FleetIntegrationOperation(
            kind,
            rootProfile,
            relativePath,
            maximumEntries,
            maximumBytes,
            localArtifactPath,
            expectedSizeBytes,
            expectedSha256);
    }

    private static FleetIntegrationMutationAuthority ParseMutationAuthority(JsonElement element)
    {
        RequireObject(element, "mutationAuthority");
        RequireExactProperties(
            element,
            "mutationAuthority",
            [
                "schema",
                "fleetDeviceId",
                "fleetIdentityRevision",
                "questIdentityProofId",
                "manifoldCommandId",
                "manifoldLeaseId",
                "manifoldProviderEpoch",
                "revocationBarrierRevision",
                "expiresAtUtc"
            ]);
        var schema = RequireString(element, "schema", 128);
        if (!string.Equals(schema, FleetIntegrationContract.MutationAuthoritySchema, StringComparison.Ordinal))
        {
            throw FleetIntegrationException.Unsupported(
                $"Unsupported mutation-authority schema '{schema}'.");
        }
        return new FleetIntegrationMutationAuthority(
            schema,
            RequireIdentifier(element, "fleetDeviceId"),
            RequireNonNegativeInt64(element, "fleetIdentityRevision"),
            RequireIdentifier(element, "questIdentityProofId"),
            RequireIdentifier(element, "manifoldCommandId"),
            RequireIdentifier(element, "manifoldLeaseId"),
            RequireIdentifier(element, "manifoldProviderEpoch"),
            RequireNonNegativeInt64(element, "revocationBarrierRevision"),
            RequireTimestamp(element, "expiresAtUtc"));
    }

    private static void RequireAbsentPushFields(
        string? localArtifactPath,
        long? expectedSizeBytes,
        string? expectedSha256,
        string operation)
    {
        if (localArtifactPath is not null || expectedSizeBytes is not null || expectedSha256 is not null)
        {
            throw FleetIntegrationException.Input(
                "operation_field_invalid",
                $"{operation} requests must not include push source fields.");
        }
    }

    private static void RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw FleetIntegrationException.Input(
                "request_shape_invalid",
                $"{context} must be a JSON object.");
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        string context,
        IReadOnlyCollection<string> required) =>
        RequireAllowedProperties(element, context, required, required);

    private static void RequireAllowedProperties(
        JsonElement element,
        string context,
        IReadOnlyCollection<string> allowed,
        IReadOnlyCollection<string> required)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!observed.Add(property.Name))
            {
                throw FleetIntegrationException.Input(
                    "request_duplicate_field",
                    $"{context} contains duplicate field '{property.Name}'.");
            }
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw FleetIntegrationException.Input(
                    "request_unknown_field",
                    $"{context} contains unknown field '{property.Name}'.");
            }
        }

        foreach (var name in required)
        {
            if (!observed.Contains(name))
            {
                throw FleetIntegrationException.Input(
                    "request_missing_field",
                    $"{context} is missing required field '{name}'.");
            }
        }
    }

    private static string RequireIdentifier(JsonElement element, string name)
    {
        var value = RequireString(element, name, 64);
        if (value.Length == 0 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw FleetIntegrationException.Input(
                "identifier_invalid",
                $"{name} must contain only ASCII letters, digits, underscores, or hyphens and start with a letter or digit.");
        }
        return value;
    }

    private static string RequireHexIdentifier(JsonElement element, string name)
    {
        var value = RequireString(element, name, 64);
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw FleetIntegrationException.Input(
                "identifier_invalid",
                $"{name} must be a 64-character hexadecimal digest.");
        }
        return value.ToLowerInvariant();
    }

    private static string RequireString(
        JsonElement element,
        string name,
        int maximumLength,
        bool allowEmpty = false)
    {
        var property = element.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw FleetIntegrationException.Input(
                "request_type_invalid",
                $"{name} must be a JSON string.");
        }

        var value = property.GetString() ?? string.Empty;
        if ((!allowEmpty && value.Length == 0) || value.Length > maximumLength)
        {
            throw FleetIntegrationException.Input(
                "request_value_invalid",
                $"{name} must contain {(allowEmpty ? "at most" : "between 1 and")} {maximumLength} characters.");
        }
        return value;
    }

    private static DateTimeOffset RequireTimestamp(JsonElement element, string name)
    {
        var value = RequireString(element, name, 64);
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            throw FleetIntegrationException.Input(
                "timestamp_invalid",
                $"{name} must be an ISO-8601 round-trip timestamp.");
        }
        return parsed.ToUniversalTime();
    }

    private static int? OptionalInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw FleetIntegrationException.Input(
                "request_type_invalid",
                $"{name} must be a JSON integer.");
        }
        return value;
    }

    private static long? OptionalInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
        {
            throw FleetIntegrationException.Input(
                "request_type_invalid",
                $"{name} must be a JSON integer.");
        }
        return value;
    }

    private static long RequireNonNegativeInt64(JsonElement element, string name)
    {
        var property = element.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var value) ||
            value < 0)
        {
            throw FleetIntegrationException.Input(
                "request_type_invalid",
                $"{name} must be a non-negative JSON integer.");
        }
        return value;
    }

    private static string? OptionalString(
        JsonElement element,
        string name,
        int maximumLength)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            throw FleetIntegrationException.Input(
                "request_type_invalid",
                $"{name} must be a JSON string.");
        }
        var value = property.GetString() ?? string.Empty;
        if (value.Length == 0 || value.Length > maximumLength)
        {
            throw FleetIntegrationException.Input(
                "request_value_invalid",
                $"{name} must contain between 1 and {maximumLength} characters.");
        }
        return value;
    }

    private static string? OptionalLowerHexDigest(JsonElement element, string name)
    {
        var value = OptionalString(element, name, 64);
        if (value is null)
        {
            return null;
        }
        if (value.Length != 64 ||
            value.Any(static character =>
                !char.IsAsciiDigit(character) &&
                (character < 'a' || character > 'f')))
        {
            throw FleetIntegrationException.Input(
                "digest_invalid",
                $"{name} must be a lowercase 64-character SHA-256 digest.");
        }
        return value;
    }
}

public sealed record FleetIntegrationListEntry(
    string Name,
    string RelativePath,
    string EntryType);

public sealed record FleetIntegrationOperationResult(
    string Schema,
    string ContractVersion,
    string RequestId,
    string OperationId,
    string AdapterEpoch,
    string ObservationId,
    string Serial,
    string Transport,
    string Operation,
    string RootProfile,
    string RelativePath,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<FleetIntegrationListEntry>? Entries,
    int? EntryCount,
    string? LocalArtifactPath,
    long? SizeBytes,
    string? Sha256);

[JsonConverter(typeof(JsonStringEnumConverter<FleetIntegrationOperationPhase>))]
public enum FleetIntegrationOperationPhase
{
    Accepted,
    Running,
    Completed,
    CancelRequested,
    Cancelled,
    Failed,
    RecoveryRequired,
    CleanupRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<FleetIntegrationCleanupState>))]
public enum FleetIntegrationCleanupState
{
    NotRequired,
    Pending,
    Completed,
    Required,
    Unknown
}

public sealed record FleetIntegrationOperationStatusSnapshot(
    string Schema,
    string ContractVersion,
    string OperationId,
    string RequestId,
    string AdapterEpoch,
    string Serial,
    string Transport,
    string RelativePath,
    FleetIntegrationOperationPhase Phase,
    FleetIntegrationCleanupState CleanupState,
    long ExpectedSizeBytes,
    string ExpectedSha256,
    long? ObservedSizeBytes,
    string? ObservedSha256,
    bool DestinationMayExist,
    bool PartialMayExist,
    DateTimeOffset UpdatedAtUtc,
    string? Reason);

public sealed record FleetIntegrationError(
    string Code,
    string Message,
    bool Retryable);

public sealed record FleetIntegrationResponse(
    string Schema,
    string ContractVersion,
    bool Success,
    FleetIntegrationStatus Status,
    FleetIntegrationCapabilitySnapshot? Capability,
    FleetIntegrationDeviceObservation? Observation,
    FleetIntegrationOperationResult? Result,
    FleetIntegrationError? Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    FleetIntegrationOperationStatusSnapshot? OperationStatus = null)
{
    public static FleetIntegrationResponse ForCapability(FleetIntegrationCapabilitySnapshot capability) =>
        new(
            FleetIntegrationContract.ResponseSchema,
            FleetIntegrationContract.Version,
            capability.State == FleetIntegrationStatus.Ready,
            capability.State,
            capability,
            null,
            null,
            null);

    public static FleetIntegrationResponse ForObservation(
        FleetIntegrationCapabilitySnapshot capability,
        FleetIntegrationDeviceObservation observation) =>
        new(
            FleetIntegrationContract.ResponseSchema,
            FleetIntegrationContract.Version,
            true,
            FleetIntegrationStatus.Ready,
            capability,
            observation,
            null,
            null);

    public static FleetIntegrationResponse ForResult(
        FleetIntegrationCapabilitySnapshot capability,
        FleetIntegrationOperationResult result) =>
        new(
            FleetIntegrationContract.ResponseSchema,
            FleetIntegrationContract.Version,
            true,
            FleetIntegrationStatus.Completed,
            capability,
            null,
            result,
            null);

    public static FleetIntegrationResponse ForOperationStatus(
        FleetIntegrationCapabilitySnapshot capability,
        FleetIntegrationOperationStatusSnapshot operationStatus) =>
        new(
            FleetIntegrationContract.ResponseSchema,
            FleetIntegrationContract.Version,
            true,
            operationStatus.Phase == FleetIntegrationOperationPhase.Completed
                ? FleetIntegrationStatus.Completed
                : FleetIntegrationStatus.Ready,
            capability,
            null,
            null,
            null,
            operationStatus);

    public static FleetIntegrationResponse Failure(
        FleetIntegrationStatus status,
        string code,
        string message,
        bool retryable,
        FleetIntegrationCapabilitySnapshot? capability = null) =>
        new(
            FleetIntegrationContract.ResponseSchema,
            FleetIntegrationContract.Version,
            false,
            status,
            capability,
            null,
            null,
            new FleetIntegrationError(code, message, retryable));
}

public sealed class FleetIntegrationException : InvalidOperationException
{
    public FleetIntegrationException(
        FleetIntegrationStatus status,
        string code,
        string message,
        bool retryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        Code = code;
        Retryable = retryable;
    }

    public FleetIntegrationStatus Status { get; }

    public string Code { get; }

    public bool Retryable { get; }

    public static FleetIntegrationException Input(string code, string message) =>
        new(FleetIntegrationStatus.Rejected, code, message);

    public static FleetIntegrationException Unsupported(string message) =>
        new(FleetIntegrationStatus.Unsupported, "contract_unsupported", message);
}
