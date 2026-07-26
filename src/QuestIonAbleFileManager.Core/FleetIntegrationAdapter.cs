using System.Security.Cryptography;
using System.Text;

namespace QuestIonAbleFileManager.Core;

public sealed record FleetIntegrationSettings(
    FleetIntegrationStatus ConfiguredState,
    string? LocalStagingRoot,
    string? AdbPath,
    string? Reason)
{
    public const string EnableEnvironmentVariable = "QUESTIONABLE_FILE_MANAGER_FLEET_INTEGRATION";
    public const string RootEnvironmentVariable = "QUESTIONABLE_FILE_MANAGER_FLEET_ADB_SHARED_ROOT";

    public static FleetIntegrationSettings FromEnvironment(string? explicitAdbPath = null)
    {
        var mode = Environment.GetEnvironmentVariable(EnableEnvironmentVariable)?.Trim();
        if (string.IsNullOrEmpty(mode) ||
            mode.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            mode == "0")
        {
            return new FleetIntegrationSettings(
                FleetIntegrationStatus.Disabled,
                Environment.GetEnvironmentVariable(RootEnvironmentVariable),
                AdbLocator.Find(explicitAdbPath),
                $"Fleet integration is disabled. Set {EnableEnvironmentVariable}=enabled only after operator approval.");
        }

        if (!mode.Equals("enabled", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("true", StringComparison.OrdinalIgnoreCase) &&
            mode != "1")
        {
            return new FleetIntegrationSettings(
                FleetIntegrationStatus.Unsupported,
                Environment.GetEnvironmentVariable(RootEnvironmentVariable),
                AdbLocator.Find(explicitAdbPath),
                $"{EnableEnvironmentVariable} contains an unsupported value.");
        }

        var root = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            return new FleetIntegrationSettings(
                FleetIntegrationStatus.Absent,
                null,
                AdbLocator.Find(explicitAdbPath),
                $"Fleet integration is enabled but {RootEnvironmentVariable} is absent.");
        }

        var adbPath = AdbLocator.Find(explicitAdbPath);
        if (adbPath is null)
        {
            return new FleetIntegrationSettings(
                FleetIntegrationStatus.Unavailable,
                root,
                null,
                "Fleet integration is enabled, but an ADB executable is unavailable.");
        }

        return new FleetIntegrationSettings(FleetIntegrationStatus.Ready, root, adbPath, null);
    }
}

public sealed class FleetIntegrationAdapter
{
    private readonly FleetIntegrationSettings _settings;
    private readonly AdbClient? _client;
    private readonly Func<DateTimeOffset> _utcNow;

    public FleetIntegrationAdapter(
        FleetIntegrationSettings settings,
        AdbClient? client = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = client;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    public FleetIntegrationCapabilitySnapshot GetCapabilities()
    {
        var state = _settings.ConfiguredState;
        var reason = _settings.Reason;
        string? safeRoot = null;

        if (state == FleetIntegrationStatus.Ready)
        {
            if (string.IsNullOrWhiteSpace(_settings.AdbPath) || !File.Exists(_settings.AdbPath))
            {
                state = FleetIntegrationStatus.Unavailable;
                reason = "The configured ADB executable is unavailable.";
            }
            else
            {
                try
                {
                    safeRoot = FleetPathPolicy.RequireSafeExistingRoot(
                        _settings.LocalStagingRoot
                        ?? throw new InvalidOperationException("A ready integration must have a staging root."));
                }
                catch (FleetIntegrationException exception)
                {
                    state = exception.Status switch
                    {
                        FleetIntegrationStatus.Absent => FleetIntegrationStatus.Absent,
                        FleetIntegrationStatus.Unsupported => FleetIntegrationStatus.Unsupported,
                        _ => FleetIntegrationStatus.Unavailable
                    };
                    reason = exception.Message;
                }
                catch (Exception exception) when (
                    exception is IOException or
                    UnauthorizedAccessException or
                    ArgumentException)
                {
                    state = FleetIntegrationStatus.Unavailable;
                    reason = $"The configured integration staging root is unavailable: {exception.Message}";
                }
            }
        }

        var epoch = ComputeAdapterEpoch(
            _settings.ConfiguredState,
            safeRoot ?? _settings.LocalStagingRoot,
            _settings.AdbPath);
        var ready = state == FleetIntegrationStatus.Ready;
        return new FleetIntegrationCapabilitySnapshot(
            FleetIntegrationContract.CapabilitySchema,
            FleetIntegrationContract.Version,
            state,
            epoch,
            _utcNow().ToUniversalTime(),
            [FleetIntegrationContract.Version],
            ready ? ["list", "pull"] : Array.Empty<string>(),
            [
                new FleetIntegrationRootProfile(
                    FleetIntegrationContract.RootProfile,
                    FleetIntegrationContract.RemoteRoot,
                    safeRoot,
                    ReadOnly: true)
            ],
            ready ? 1 : 0,
            NormalizePathForIdentity(_settings.AdbPath),
            reason);
    }

    public async Task<FleetIntegrationDeviceObservation> ObserveAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var capability = RequireReadyCapability();
        serial = AndroidInput.RequireSerial(serial);
        var device = await RediscoverExactReadyDeviceAsync(
            serial,
            expectedTransport: null,
            cancellationToken).ConfigureAwait(false);
        var observedAt = _utcNow().ToUniversalTime();
        var transport = device.IsWifiConnection ? "wifi" : "usb";
        return new FleetIntegrationDeviceObservation(
            FleetIntegrationContract.ObservationSchema,
            FleetIntegrationContract.Version,
            capability.AdapterEpoch,
            ComputeObservationId(
                capability.AdapterEpoch,
                device.Serial,
                transport,
                device.State,
                observedAt),
            device.Serial,
            transport,
            device.State,
            device.Model,
            device.Product,
            observedAt);
    }

    public async Task<FleetIntegrationOperationResult> InvokeAsync(
        FleetIntegrationOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var capability = RequireReadyCapability();
        var now = _utcNow().ToUniversalTime();
        ValidateRequestAuthority(request, capability, now);

        var startedAt = now;
        try
        {
            await RediscoverExactReadyDeviceAsync(
                request.DeviceBinding.Serial,
                request.DeviceBinding.Transport,
                cancellationToken).ConfigureAwait(false);

            return request.Operation.Kind switch
            {
                "list" => await InvokeListAsync(
                    capability,
                    request,
                    startedAt,
                    cancellationToken).ConfigureAwait(false),
                "pull" => await InvokePullAsync(
                    capability,
                    request,
                    startedAt,
                    cancellationToken).ConfigureAwait(false),
                _ => throw FleetIntegrationException.Input(
                    "operation_unsupported",
                    "Only read-only 'list' and 'pull' integration operations are supported.")
            };
        }
        catch (FleetIntegrationException)
        {
            throw;
        }
        catch (FleetTransferLimitException exception)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "maximum_bytes_exceeded",
                exception.Message,
                retryable: false,
                exception);
        }
        catch (FleetRemotePathException exception)
        {
            var status = exception.Code switch
            {
                "remote_path_absent" => FleetIntegrationStatus.Absent,
                "remote_root_unavailable" => FleetIntegrationStatus.Unavailable,
                "remote_path_open_failed" or
                "remote_size_unavailable" => FleetIntegrationStatus.Failed,
                _ => FleetIntegrationStatus.Rejected
            };
            throw new FleetIntegrationException(
                status,
                exception.Code,
                exception.Message,
                retryable: status is
                    FleetIntegrationStatus.Absent or
                    FleetIntegrationStatus.Unavailable or
                    FleetIntegrationStatus.Failed,
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "operation_timeout",
                exception.Message,
                retryable: true,
                exception);
        }
        catch (OperationCanceledException exception)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Cancelled,
                "operation_cancelled",
                "The integration operation was cancelled.",
                retryable: true,
                exception);
        }
        catch (AdbCommandException exception)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "adb_operation_failed",
                exception.Message,
                retryable: true,
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "file_operation_failed",
                exception.Message,
                retryable: true,
                exception);
        }
    }

    public static string ComputeObservationId(
        string adapterEpoch,
        string serial,
        string transport,
        string state,
        DateTimeOffset observedAtUtc)
    {
        var canonical = string.Join(
            "\n",
            FleetIntegrationContract.ObservationSchema,
            FleetIntegrationContract.Version,
            adapterEpoch,
            serial,
            transport,
            state,
            observedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private async Task<FleetIntegrationOperationResult> InvokeListAsync(
        FleetIntegrationCapabilitySnapshot capability,
        FleetIntegrationOperationRequest request,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var remotePath = FleetPathPolicy.ToRemotePath(request.Operation.RelativePath, allowEmpty: true);
        var entries = await RequireClient().ListRemoteDirectoryBoundedAsync(
            request.DeviceBinding.Serial,
            remotePath,
            request.Operation.MaximumEntries!.Value,
            cancellationToken).ConfigureAwait(false);
        if (entries.Count > request.Operation.MaximumEntries)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "entry_limit_exceeded",
                $"The directory contains {entries.Count} entries, exceeding the request limit of {request.Operation.MaximumEntries}.");
        }

        var projected = entries.Select(entry =>
        {
            var relative = string.IsNullOrEmpty(request.Operation.RelativePath)
                ? entry.Name
                : request.Operation.RelativePath + "/" + entry.Name;
            try
            {
                FleetPathPolicy.ValidateRelativePath(relative, allowEmpty: false);
            }
            catch (FleetIntegrationException exception)
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Rejected,
                    "remote_entry_name_rejected",
                    $"The directory contains an entry that cannot be represented safely: {exception.Message}",
                    innerException: exception);
            }

            return new FleetIntegrationListEntry(
                entry.Name,
                relative,
                entry.IsDirectory ? "directory" : "file");
        }).ToArray();

        await RediscoverExactReadyDeviceAsync(
            request.DeviceBinding.Serial,
            request.DeviceBinding.Transport,
            cancellationToken).ConfigureAwait(false);
        return CreateResult(
            capability,
            request,
            startedAt,
            entries: projected,
            entryCount: projected.Length);
    }

    private async Task<FleetIntegrationOperationResult> InvokePullAsync(
        FleetIntegrationCapabilitySnapshot capability,
        FleetIntegrationOperationRequest request,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var remotePath = FleetPathPolicy.ToRemotePath(request.Operation.RelativePath, allowEmpty: false);
        var destination = FleetPathPolicy.PreparePullDestination(
            capability.RootProfiles[0].LocalStagingRoot
            ?? throw new InvalidOperationException("A ready integration has no local staging root."),
            request.OperationId,
            request.Operation.RelativePath);
        var completed = false;
        try
        {
            destination.ValidateForWrite();
            var streamed = await RequireClient().StreamRemoteFileBoundedAsync(
                request.DeviceBinding.Serial,
                remotePath,
                destination.OutputStream,
                request.Operation.MaximumBytes!.Value,
                cancellationToken).ConfigureAwait(false);
            destination.FlushAndValidate(streamed.BytesWritten);
            await RediscoverExactReadyDeviceAsync(
                request.DeviceBinding.Serial,
                request.DeviceBinding.Transport,
                cancellationToken).ConfigureAwait(false);
            destination.ValidateForWrite();
            var result = CreateResult(
                capability,
                request,
                startedAt,
                localArtifactPath: destination.OutputPath,
                sizeBytes: streamed.BytesWritten,
                sha256: streamed.Sha256);
            destination.Commit();
            completed = true;
            return result;
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                if (!completed)
                {
                    destination.Abort();
                }
            }
            catch (Exception cleanupException)
            {
                cleanupFailure = cleanupException;
            }
            finally
            {
                destination.Dispose();
            }

            if (cleanupFailure is not null)
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Failed,
                    "cleanup_failed",
                    $"The pull failed and its operation-owned staging could not be cleaned up: {cleanupFailure.Message}",
                    retryable: true,
                    cleanupFailure);
            }
        }
    }

    private FleetIntegrationOperationResult CreateResult(
        FleetIntegrationCapabilitySnapshot capability,
        FleetIntegrationOperationRequest request,
        DateTimeOffset startedAt,
        IReadOnlyList<FleetIntegrationListEntry>? entries = null,
        int? entryCount = null,
        string? localArtifactPath = null,
        long? sizeBytes = null,
        string? sha256 = null) =>
        new(
            FleetIntegrationContract.ResultSchema,
            FleetIntegrationContract.Version,
            request.RequestId,
            request.OperationId,
            capability.AdapterEpoch,
            request.DeviceBinding.ObservationId,
            request.DeviceBinding.Serial,
            request.DeviceBinding.Transport,
            request.Operation.Kind,
            request.Operation.RootProfile,
            request.Operation.RelativePath,
            startedAt,
            _utcNow().ToUniversalTime(),
            entries,
            entryCount,
            localArtifactPath,
            sizeBytes,
            sha256);

    private FleetIntegrationCapabilitySnapshot RequireReadyCapability()
    {
        var capability = GetCapabilities();
        if (capability.State == FleetIntegrationStatus.Ready)
        {
            return capability;
        }

        var code = capability.State switch
        {
            FleetIntegrationStatus.Disabled => "integration_disabled",
            FleetIntegrationStatus.Absent => "integration_absent",
            FleetIntegrationStatus.Unsupported => "integration_unsupported",
            _ => "integration_unavailable"
        };
        throw new FleetIntegrationException(
            capability.State,
            code,
            capability.Reason ?? "Fleet integration is not ready.",
            retryable: capability.State is FleetIntegrationStatus.Absent or FleetIntegrationStatus.Unavailable);
    }

    private AdbClient RequireClient() =>
        _client ?? throw new FleetIntegrationException(
            FleetIntegrationStatus.Unavailable,
            "adb_client_unavailable",
            "The integration adapter has no ADB client.",
            retryable: true);

    private void ValidateRequestAuthority(
        FleetIntegrationOperationRequest request,
        FleetIntegrationCapabilitySnapshot capability,
        DateTimeOffset now)
    {
        if (!string.Equals(request.Schema, FleetIntegrationContract.RequestSchema, StringComparison.Ordinal) ||
            !string.Equals(request.ContractVersion, FleetIntegrationContract.Version, StringComparison.Ordinal) ||
            !string.Equals(request.DeviceBinding.Schema, FleetIntegrationContract.BindingSchema, StringComparison.Ordinal))
        {
            throw FleetIntegrationException.Unsupported(
                "The in-memory request uses an unsupported integration schema or contract version.");
        }
        ValidateIdentifier(request.RequestId, nameof(request.RequestId));
        ValidateIdentifier(request.OperationId, nameof(request.OperationId));
        AndroidInput.RequireSerial(request.DeviceBinding.Serial);
        if (request.DeviceBinding.Transport is not ("usb" or "wifi"))
        {
            throw FleetIntegrationException.Input(
                "binding_transport_invalid",
                "The device binding transport must be 'usb' or 'wifi'.");
        }
        if (!string.Equals(
                request.Operation.RootProfile,
                FleetIntegrationContract.RootProfile,
                StringComparison.Ordinal))
        {
            throw FleetIntegrationException.Input(
                "root_profile_unsupported",
                $"Only the '{FleetIntegrationContract.RootProfile}' root profile is supported.");
        }
        FleetPathPolicy.ValidateRelativePath(
            request.Operation.RelativePath,
            allowEmpty: request.Operation.Kind == "list");
        if (request.Operation.Kind == "list")
        {
            if (request.Operation.MaximumEntries is null or < 1 or > FleetIntegrationContract.MaximumListEntries ||
                request.Operation.MaximumBytes is not null)
            {
                throw FleetIntegrationException.Input(
                    "operation_bounds_invalid",
                    "List requires only maximumEntries within the advertised limit.");
            }
        }
        else if (request.Operation.Kind == "pull")
        {
            if (request.Operation.MaximumBytes is null or < 1 or > FleetIntegrationContract.MaximumPullBytes ||
                request.Operation.MaximumEntries is not null)
            {
                throw FleetIntegrationException.Input(
                    "operation_bounds_invalid",
                    "Pull requires only maximumBytes within the advertised limit.");
            }
        }
        else
        {
            throw FleetIntegrationException.Input(
                "operation_unsupported",
                "Only read-only 'list' and 'pull' integration operations are supported.");
        }

        if (!string.Equals(request.AdapterEpoch, capability.AdapterEpoch, StringComparison.Ordinal))
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "adapter_epoch_mismatch",
                "The request targets a different File Manager adapter epoch. Refresh capabilities and observe again.",
                retryable: true);
        }

        if (request.ExpiresAtUtc <= now ||
            request.ExpiresAtUtc - now > FleetIntegrationContract.MaximumRequestLifetime)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "request_expired_or_too_long",
                "The request is expired or exceeds the five-minute maximum lifetime.");
        }

        var age = now - request.DeviceBinding.ObservedAtUtc;
        if (age < TimeSpan.FromSeconds(-5) || age > FleetIntegrationContract.MaximumObservationAge)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "device_binding_stale",
                "The device observation is stale or dated in the future. Observe the exact serial again.",
                retryable: true);
        }

        var expectedObservationId = ComputeObservationId(
            capability.AdapterEpoch,
            request.DeviceBinding.Serial,
            request.DeviceBinding.Transport,
            "device",
            request.DeviceBinding.ObservedAtUtc);
        if (!string.Equals(
                request.DeviceBinding.ObservationId,
                expectedObservationId,
                StringComparison.Ordinal))
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "device_binding_invalid",
                "The device binding does not match its File Manager observation.");
        }
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > 64 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw FleetIntegrationException.Input(
                "identifier_invalid",
                $"{name} must contain only ASCII letters, digits, underscores, or hyphens and start with a letter or digit.");
        }
    }

    private async Task<QuestDevice> RediscoverExactReadyDeviceAsync(
        string serial,
        string? expectedTransport,
        CancellationToken cancellationToken)
    {
        var devices = await RequireClient().GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        var matches = devices
            .Where(device => string.Equals(device.Serial, serial, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Absent,
                "device_absent",
                "The exact ADB serial is no longer present.",
                retryable: true);
        }
        if (matches.Length != 1)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "device_serial_ambiguous",
                "ADB reported the exact serial more than once.",
                retryable: true);
        }

        var device = matches[0];
        if (!device.IsReady)
        {
            var unauthorized = string.Equals(device.State, "unauthorized", StringComparison.OrdinalIgnoreCase);
            throw new FleetIntegrationException(
                unauthorized ? FleetIntegrationStatus.Unauthorized : FleetIntegrationStatus.Unavailable,
                unauthorized ? "device_unauthorized" : "device_not_ready",
                $"The exact ADB serial is '{device.State}', not ready.",
                retryable: true);
        }

        var transport = device.IsWifiConnection ? "wifi" : "usb";
        if (expectedTransport is not null &&
            !string.Equals(expectedTransport, transport, StringComparison.Ordinal))
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "device_transport_changed",
                "The exact serial's transport no longer matches the observation. Observe again.",
                retryable: true);
        }
        return device;
    }

    private static string ComputeAdapterEpoch(
        FleetIntegrationStatus state,
        string? localRoot,
        string? adbPath)
    {
        var assemblyVersion = typeof(FleetIntegrationAdapter).Assembly.GetName().Version?.ToString() ?? "unknown";
        var canonical = string.Join(
            "\n",
            FleetIntegrationContract.Version,
            assemblyVersion,
            state.ToString(),
            "root:" + (NormalizePathForIdentity(localRoot) ?? "absent-or-invalid"),
            "adb:" + (NormalizePathForIdentity(adbPath) ?? "absent-or-invalid"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? NormalizePathForIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }
    }

}
