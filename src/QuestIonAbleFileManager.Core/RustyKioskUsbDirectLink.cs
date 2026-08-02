using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed record RustyKioskUsbDirectLinkReceipt(
    string Schema,
    string OperationId,
    string ProductChannel,
    string PackageName,
    int PackageUid,
    string TargetSerialSha256,
    string BridgeGenerationSha256,
    bool EnabledByRequest,
    DateTimeOffset ExpiresAtUtc,
    OperatorMutationStage Stage,
    string Message);

public sealed record RustyKioskUsbDirectCleanupReceipt(
    string Schema,
    string OperationId,
    OperatorMutationStage Stage,
    string Message);

public sealed class RustyKioskUsbDirectBootstrapException : InvalidOperationException
{
    internal RustyKioskUsbDirectBootstrapException(
        RustyKioskUsbDirectCleanupReceipt cleanupReceipt,
        Exception innerException)
        : base(
            $"Authorized-USB Direct Link bootstrap failed. Cleanup {cleanupReceipt.Stage.ToString().ToLowerInvariant()}: {cleanupReceipt.Message}",
            innerException)
    {
        CleanupReceipt = cleanupReceipt;
    }

    public RustyKioskUsbDirectCleanupReceipt CleanupReceipt { get; }
}

public sealed class RustyKioskUsbDirectAdoptionException : InvalidOperationException
{
    internal RustyKioskUsbDirectAdoptionException(
        RustyKioskUsbDirectCleanupReceipt cleanupReceipt,
        Exception adoptionFailure)
        : base(
            $"Authorized-USB Direct Link adoption failed ({ReasonCode(adoptionFailure)}). " +
            $"Cleanup {StageName(cleanupReceipt.Stage)}: {cleanupReceipt.Message}")
    {
        CleanupReceipt = cleanupReceipt;
        AdoptionFailureReasonCode = ReasonCode(adoptionFailure);
        AdoptionFailureType = adoptionFailure.GetType().FullName ?? adoptionFailure.GetType().Name;
    }

    public RustyKioskUsbDirectCleanupReceipt CleanupReceipt { get; }

    public string AdoptionFailureReasonCode { get; }

    public string AdoptionFailureType { get; }

    private static string ReasonCode(Exception exception) => exception switch
    {
        TimeoutException => "bounded_timeout",
        HttpRequestException or TaskCanceledException => "direct_transport_unavailable",
        InvalidDataException => "contract_rejected",
        _ => "required_readback_failed"
    };

    private static string StageName(OperatorMutationStage stage) =>
        stage == OperatorMutationStage.CleanupUnknown
            ? "cleanup_unknown"
            : stage.ToString().ToLowerInvariant();
}

/// <summary>
/// One memory-only Direct Link session minted by the exact serial's DUMP-protected
/// Kiosk provider. No endpoint or credential is exposed by this object.
/// </summary>
public sealed class RustyKioskUsbDirectLinkSession : IAsyncDisposable
{
    private readonly RustyKioskUsbDirectLinkBootstrapper _owner;
    private readonly string _serial;
    private readonly RustyKioskProductContract _product;
    private readonly long _bridgeGeneration;
    private readonly string _sessionId;
    private readonly string _originOperationId;
    private readonly object _cleanupGate = new();
    private Task<RustyKioskUsbDirectCleanupReceipt>? _cleanupTask;

    internal RustyKioskUsbDirectLinkSession(
        RustyKioskUsbDirectLinkBootstrapper owner,
        string serial,
        RustyKioskProductContract product,
        long bridgeGeneration,
        string sessionId,
        string originOperationId,
        bool enabledByRequest,
        RustyKioskDirectClient client,
        RustyKioskUsbDirectLinkReceipt receipt)
    {
        _owner = owner;
        _serial = serial;
        _product = product;
        _bridgeGeneration = bridgeGeneration;
        _sessionId = sessionId;
        _originOperationId = originOperationId;
        EnabledByRequest = enabledByRequest;
        Client = client;
        Receipt = receipt;
    }

    public bool EnabledByRequest { get; }

    public RustyKioskDirectClient Client { get; }

    public RustyKioskUsbDirectLinkReceipt Receipt { get; }

    public RustyKioskUsbDirectCleanupReceipt? CleanupReceipt =>
        _cleanupTask is { IsCompletedSuccessfully: true } task ? task.Result : null;

    public Task<RustyKioskUsbDirectCleanupReceipt> CloseAsync() =>
        GetOrStartCleanup();

    public async Task<RustyKioskUsbDirectAdoptionException> CloseAfterAdoptionFailureAsync(
        Exception adoptionFailure)
    {
        ArgumentNullException.ThrowIfNull(adoptionFailure);
        var cleanup = await GetOrStartCleanup().ConfigureAwait(false);
        return new RustyKioskUsbDirectAdoptionException(cleanup, adoptionFailure);
    }

    public async ValueTask DisposeAsync()
    {
        await GetOrStartCleanup().ConfigureAwait(false);
    }

    private Task<RustyKioskUsbDirectCleanupReceipt> GetOrStartCleanup()
    {
        lock (_cleanupGate)
        {
            return _cleanupTask ??= CloseCoreAsync();
        }
    }

    private async Task<RustyKioskUsbDirectCleanupReceipt> CloseCoreAsync()
    {
        Client.Dispose();
        if (!EnabledByRequest)
        {
            return new RustyKioskUsbDirectCleanupReceipt(
                RustyKioskUsbDirectLinkBootstrapper.CleanupReceiptSchema,
                _originOperationId,
                OperatorMutationStage.Confirmed,
                "The pre-existing wearer-owned Direct Link listener was preserved.");
        }

        try
        {
            return await _owner.DisableOwnedAsync(
                    _serial,
                    _product,
                    _bridgeGeneration,
                    _originOperationId,
                    _sessionId,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            return new RustyKioskUsbDirectCleanupReceipt(
                RustyKioskUsbDirectLinkBootstrapper.CleanupReceiptSchema,
                _originOperationId,
                OperatorMutationStage.CleanupUnknown,
                "Owned Direct Link cleanup could not be reconciled on the exact USB target.");
        }
    }
}

public sealed class RustyKioskUsbDirectLinkBootstrapper
{
    public const string ReceiptSchema = "questionable.file_manager.kiosk_direct_usb.v1";
    public const string CleanupReceiptSchema = "questionable.file_manager.kiosk_direct_usb_cleanup.v1";
    private const int MaximumSensitiveOutputBytes = 16 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaximumSessionLifetime = TimeSpan.FromMinutes(10);
    private readonly AdbClient _client;
    private readonly ISensitiveCommandRunner _sensitiveRunner;
    private readonly Func<DateTimeOffset> _utcNow;

    public RustyKioskUsbDirectLinkBootstrapper(
        AdbClient client,
        Func<DateTimeOffset>? utcNow = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sensitiveRunner = client.CommandRunner as ISensitiveCommandRunner ??
            throw new InvalidOperationException(
                "The configured ADB process runner cannot contain a memory-only Direct Link credential.");
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    public async Task<RustyKioskUsbDirectLinkSession> ConnectAsync(
        string serial,
        RustyKioskProductChannel channel,
        bool operatorConfirmed,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        if (!operatorConfirmed)
        {
            throw new InvalidOperationException(
                "Authorized-USB Direct Link bootstrap requires explicit operator confirmation.");
        }

        serial = AndroidInput.RequireSerial(serial);
        var product = RustyKioskProductContract.For(channel);
        await RequireExactReadyUsbTargetAsync(serial, cancellationToken).ConfigureAwait(false);
        var uid = await RequireInstalledProviderAsync(serial, product, cancellationToken)
            .ConfigureAwait(false);

        var operationId = "usb_" + Guid.NewGuid().ToString("N");
        var arguments = DeviceArguments(
            serial,
            product,
            "direct-enable",
            operationId,
            []);
        SensitiveCommandResult<UsbBootstrapPayload> sensitive;
        try
        {
            sensitive = await _sensitiveRunner.RunSensitiveAsync(
                    _client.AdbPath,
                    arguments,
                    MaximumSensitiveOutputBytes,
                    MaximumSensitiveOutputBytes,
                    CommandTimeout,
                    bytes => ParseBootstrap(bytes, operationId, product, _utcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var cleanup = await RecoverLostEnableResponseAsync(
                    serial,
                    product,
                    operationId)
                .ConfigureAwait(false);
            throw new RustyKioskUsbDirectBootstrapException(cleanup, exception);
        }
        var bootstrap = sensitive.Value;
        RustyKioskDirectClient? directClient = null;
        try
        {
            var sessionSecret = bootstrap.TakeSecret();
            try
            {
                directClient = new RustyKioskDirectClient(
                    bootstrap.Endpoint,
                    sessionSecret,
                    bootstrap.SessionId,
                    bootstrap.BridgeGeneration,
                    httpClient);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(sessionSecret);
                throw;
            }
            var directStatus = await WaitForAuthenticatedStatusAsync(
                    directClient,
                    cancellationToken)
                .ConfigureAwait(false);
            if (directStatus.BridgeGeneration != bootstrap.BridgeGeneration ||
                !string.Equals(directStatus.SessionId, bootstrap.SessionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The Direct Link listener did not redeem the exact authorized USB session.");
            }

            var receipt = new RustyKioskUsbDirectLinkReceipt(
                ReceiptSchema,
                operationId,
                product.WireName,
                product.MainPackage,
                uid,
                Sha256(serial),
                Sha256(bootstrap.BridgeGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                bootstrap.EnabledByRequest,
                bootstrap.ExpiresAtUtc,
                OperatorMutationStage.Confirmed,
                "The exact USB target, provider identity, listener generation, and memory-only session were confirmed.");
            return new RustyKioskUsbDirectLinkSession(
                this,
                serial,
                product,
                bootstrap.BridgeGeneration,
                bootstrap.SessionId,
                operationId,
                bootstrap.EnabledByRequest,
                directClient,
                receipt);
        }
        catch
        {
            directClient?.Dispose();
            bootstrap.Dispose();
            if (bootstrap.EnabledByRequest)
            {
                RustyKioskUsbDirectCleanupReceipt? cleanup = null;
                try
                {
                    cleanup = await DisableOwnedAsync(
                            serial,
                            product,
                            bootstrap.BridgeGeneration,
                            operationId,
                            bootstrap.SessionId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The replacement error below is deliberately sanitized.
                }
                if (cleanup?.Stage != OperatorMutationStage.Confirmed)
                {
                    throw new InvalidOperationException(
                        "Authorized Direct Link authentication failed and owned listener cleanup could not be confirmed.");
                }
            }
            throw;
        }
        finally
        {
            bootstrap.Dispose();
        }
    }

    internal async Task<RustyKioskUsbDirectCleanupReceipt> DisableOwnedAsync(
        string serial,
        RustyKioskProductContract product,
        long expectedBridgeGeneration,
        string originOperationId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        serial = AndroidInput.RequireSerial(serial);
        if (expectedBridgeGeneration <= 0 ||
            !Regex.IsMatch(originOperationId, "^usb_[a-f0-9]{32}$") ||
            !Regex.IsMatch(sessionId, "^[A-Za-z0-9_-]{8,64}$"))
        {
            throw new ArgumentException("The owned Direct Link generation binding is invalid.");
        }

        await RequireExactReadyUsbTargetAsync(serial, cancellationToken).ConfigureAwait(false);
        var arguments = DeviceArguments(
            serial,
            product,
            "direct-disable",
            originOperationId,
            [
                "l", "expected_bridge_generation", expectedBridgeGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "s", "session_id", sessionId
            ]);
        var admission = await _sensitiveRunner.RunSensitiveAsync(
                _client.AdbPath,
                arguments,
                MaximumSensitiveOutputBytes,
                MaximumSensitiveOutputBytes,
                CommandTimeout,
                bytes => ParseDirectProviderStatus(
                    bytes,
                    product,
                    originOperationId,
                    requireOperationId: true),
                cancellationToken)
            .ConfigureAwait(false);
        var postDisableGeneration = admission.Value.BridgeGeneration;
        using var cleanupWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cleanupWindow.CancelAfter(TimeSpan.FromSeconds(12));
        var cleanupToken = cleanupWindow.Token;
        try
        {
            for (var attempt = 0; attempt < 48; attempt++)
            {
                cleanupToken.ThrowIfCancellationRequested();
                var status = (await _sensitiveRunner.RunSensitiveAsync(
                        _client.AdbPath,
                        DeviceArguments(serial, product, "direct-status", arg: null, []),
                        MaximumSensitiveOutputBytes,
                        MaximumSensitiveOutputBytes,
                        CommandTimeout,
                        bytes => ParseDirectProviderStatus(
                            bytes,
                            product,
                            operationId: null,
                            requireOperationId: false),
                        cleanupToken)
                    .ConfigureAwait(false)).Value;
                if (status.BridgeGeneration != postDisableGeneration)
                {
                    throw new InvalidDataException(
                        "The Direct Link generation changed while owned cleanup was being reconciled.");
                }
                if (!status.Enabled && !status.Running)
                {
                    return new RustyKioskUsbDirectCleanupReceipt(
                        CleanupReceiptSchema,
                        originOperationId,
                        OperatorMutationStage.Confirmed,
                        "The owned Direct Link listener was confirmed disabled and stopped on its post-disable generation.");
                }
                if (attempt < 47)
                {
                    await Task.Delay(250, cleanupToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (
            cleanupWindow.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The typed cleanup_unknown receipt below owns bounded non-convergence.
        }

        return new RustyKioskUsbDirectCleanupReceipt(
            CleanupReceiptSchema,
            originOperationId,
            OperatorMutationStage.CleanupUnknown,
            "The owned Direct Link disable was admitted, but stopped-state readback did not converge within the bounded window.");
    }

    private async Task<RustyKioskUsbDirectCleanupReceipt> RecoverLostEnableResponseAsync(
        string serial,
        RustyKioskProductContract product,
        string operationId)
    {
        using var cleanupWindow = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var cleanupToken = cleanupWindow.Token;
        try
        {
            await RequireExactReadyUsbTargetAsync(serial, cleanupToken).ConfigureAwait(false);
            for (var attempt = 0; attempt < 48; attempt++)
            {
                cleanupToken.ThrowIfCancellationRequested();
                DirectProviderStatus? recovery = null;
                try
                {
                    recovery = (await _sensitiveRunner.RunSensitiveAsync(
                            _client.AdbPath,
                            DeviceArguments(serial, product, "direct-recover-disable", operationId, []),
                            MaximumSensitiveOutputBytes,
                            MaximumSensitiveOutputBytes,
                            CommandTimeout,
                            bytes => ParseDirectProviderStatus(
                                bytes,
                                product,
                                operationId,
                                requireOperationId: true),
                            cleanupToken)
                        .ConfigureAwait(false)).Value;
                }
                catch (Exception) when (!cleanupToken.IsCancellationRequested)
                {
                    // A response can be lost after the idempotent STOP dispatch. The no-arg
                    // status read below is the only authority for confirming the safe result.
                }

                if (recovery is { Enabled: false })
                {
                    return await ReconcileStoppedGenerationAsync(
                            serial,
                            product,
                            recovery.BridgeGeneration,
                            operationId,
                            cleanupToken,
                            "The lost-response bootstrap-owned listener was confirmed disabled and stopped.")
                        .ConfigureAwait(false);
                }

                DirectProviderStatus? status = null;
                try
                {
                    status = (await _sensitiveRunner.RunSensitiveAsync(
                            _client.AdbPath,
                            DeviceArguments(serial, product, "direct-status", arg: null, []),
                            MaximumSensitiveOutputBytes,
                            MaximumSensitiveOutputBytes,
                            CommandTimeout,
                            bytes => ParseDirectProviderStatus(
                                bytes,
                                product,
                                operationId: null,
                                requireOperationId: false),
                            cleanupToken)
                        .ConfigureAwait(false)).Value;
                }
                catch (Exception) when (!cleanupToken.IsCancellationRequested)
                {
                    // Retry only within this bounded, operation-id-only recovery window.
                }

                if (status is { Enabled: false })
                {
                    return await ReconcileStoppedGenerationAsync(
                            serial,
                            product,
                            status.BridgeGeneration,
                            operationId,
                            cleanupToken,
                            "No Direct Link listener remained after the lost bootstrap response.")
                        .ConfigureAwait(false);
                }
                if (attempt < 47)
                {
                    await Task.Delay(250, cleanupToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cleanupWindow.IsCancellationRequested)
        {
            // The typed cleanup_unknown receipt below owns bounded non-convergence.
        }
        catch
        {
            // The typed cleanup_unknown receipt below is deliberately sanitized.
        }

        return new RustyKioskUsbDirectCleanupReceipt(
            CleanupReceiptSchema,
            operationId,
            OperatorMutationStage.CleanupUnknown,
            "The lost bootstrap response could not be reconciled to a confirmed stopped listener on the exact USB target.");
    }

    private async Task<RustyKioskUsbDirectCleanupReceipt> ReconcileStoppedGenerationAsync(
        string serial,
        RustyKioskProductContract product,
        long expectedGeneration,
        string operationId,
        CancellationToken cancellationToken,
        string confirmedMessage)
    {
        for (var attempt = 0; attempt < 48; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectProviderStatus? status = null;
            try
            {
                status = (await _sensitiveRunner.RunSensitiveAsync(
                        _client.AdbPath,
                        DeviceArguments(serial, product, "direct-status", arg: null, []),
                        MaximumSensitiveOutputBytes,
                        MaximumSensitiveOutputBytes,
                        CommandTimeout,
                        bytes => ParseDirectProviderStatus(
                            bytes,
                            product,
                            operationId: null,
                            requireOperationId: false),
                        cancellationToken)
                    .ConfigureAwait(false)).Value;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A transient status failure is retried only inside the bounded window.
            }
            if (status is null)
            {
                if (attempt < 47)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            if (status.BridgeGeneration != expectedGeneration)
            {
                return new RustyKioskUsbDirectCleanupReceipt(
                    CleanupReceiptSchema,
                    operationId,
                    OperatorMutationStage.CleanupUnknown,
                    "The Direct Link generation changed while lost-response cleanup was being reconciled.");
            }
            if (!status.Enabled && !status.Running)
            {
                return new RustyKioskUsbDirectCleanupReceipt(
                    CleanupReceiptSchema,
                    operationId,
                    OperatorMutationStage.Confirmed,
                    confirmedMessage);
            }
            if (attempt < 47)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        return new RustyKioskUsbDirectCleanupReceipt(
            CleanupReceiptSchema,
            operationId,
            OperatorMutationStage.CleanupUnknown,
            "Lost-response cleanup was admitted, but stopped-state readback did not converge within the bounded window.");
    }

    private async Task RequireExactReadyUsbTargetAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var devices = await _client.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        var exact = devices.Where(device =>
            string.Equals(device.Serial, serial, StringComparison.Ordinal)).ToArray();
        if (exact.Length != 1 || !exact[0].IsReady || exact[0].IsWifiConnection)
        {
            throw new InvalidOperationException(
                "The exact selected serial must be one ready classic-USB ADB transport.");
        }
    }

    private static async Task<RustyKioskDirectStatus> WaitForAuthenticatedStatusAsync(
        RustyKioskDirectClient client,
        CancellationToken cancellationToken)
    {
        using var startupWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupWindow.CancelAfter(TimeSpan.FromSeconds(12));
        var startupToken = startupWindow.Token;
        Exception? lastTransient = null;
        try
        {
            while (true)
            {
                startupToken.ThrowIfCancellationRequested();
                try
                {
                    return await client.GetStatusAsync(startupToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or TimeoutException or TaskCanceledException)
                {
                    lastTransient = exception;
                }
                await Task.Delay(200, startupToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            startupWindow.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The sanitized timeout below owns startup non-convergence.
        }

        throw new TimeoutException(
            lastTransient is null
                ? "The authorized Direct Link session did not become ready within the bounded reconciliation window."
                : "The authorized Direct Link listener remained unavailable within the bounded reconciliation window.");
    }

    private async Task<int> RequireInstalledProviderAsync(
        string serial,
        RustyKioskProductContract product,
        CancellationToken cancellationToken)
    {
        var packageResult = await _client.CommandRunner.RunAsync(
                _client.AdbPath,
                ["-s", serial, "shell", "pm", "list", "packages", "-U", product.MainPackage],
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        packageResult.EnsureSuccess("Read the fixed Rusty Kiosk package identity");
        var packageMatch = Regex.Match(
            packageResult.StandardOutput,
            $"(?m)^package:{Regex.Escape(product.MainPackage)}\\s+uid:(?<uid>[0-9]+)\\s*$",
            RegexOptions.CultureInvariant);
        if (!packageMatch.Success ||
            !int.TryParse(packageMatch.Groups["uid"].Value, out var uid) ||
            uid < 10_000)
        {
            throw new InvalidOperationException(
                "The selected Rusty Kiosk product provider is not installed with a valid application UID.");
        }

        var contractResult = await _client.CommandRunner.RunAsync(
                _client.AdbPath,
                DeviceArguments(serial, product, "contract", arg: null, []),
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        contractResult.EnsureSuccess("Read the fixed Rusty Kiosk host contract");
        if (!string.Equals(
                BundleValue(contractResult.StandardOutput, "schema"),
                RustyKioskContract.HostOperatorSuccessorSchema,
                StringComparison.Ordinal) ||
            !string.Equals(
                BundleValue(contractResult.StandardOutput, "package"),
                product.MainPackage,
                StringComparison.Ordinal) ||
            !string.Equals(
                BundleValue(contractResult.StandardOutput, "product_channel"),
                product.WireName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected package did not expose the exact channel-bound Rusty Kiosk host operator v4 contract.");
        }
        return uid;
    }

    private static IReadOnlyList<string> DeviceArguments(
        string serial,
        RustyKioskProductContract product,
        string method,
        string? arg,
        IReadOnlyList<string> extraParts)
    {
        var arguments = new List<string>
        {
            "-s", serial,
            "shell", "content", "call",
            "--uri", product.OperatorUri,
            "--method", method
        };
        if (arg is not null)
        {
            arguments.Add("--arg");
            arguments.Add(arg);
        }
        for (var index = 0; index < extraParts.Count; index += 3)
        {
            arguments.Add("--extra");
            arguments.Add($"{extraParts[index]}:{extraParts[index + 1]}:{extraParts[index + 2]}");
        }
        return arguments;
    }

    private static UsbBootstrapPayload ParseBootstrap(
        ReadOnlyMemory<byte> output,
        string operationId,
        RustyKioskProductContract product,
        DateTimeOffset now)
    {
        RequireAsciiValue(output.Span, "accepted", "true");
        RequireAsciiValue(output.Span, "schema", RustyKioskContract.DirectUsbBootstrapSchema);
        RequireAsciiValue(output.Span, "operation_id", operationId);
        RequireAsciiValue(output.Span, "product_channel", product.WireName);
        RequireAsciiValue(output.Span, "package", product.MainPackage);
        RequireAsciiValue(output.Span, "session_capability", RustyKioskDirectClient.ContractSchema);

        var endpointText = ReadAsciiValue(output.Span, "endpoint");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            endpoint.Port != 39873 ||
            endpoint.AbsolutePath is not ("" or "/") ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidDataException("The USB bootstrap endpoint was not the fixed Direct Link listener.");
        }

        var generationText = ReadAsciiValue(output.Span, "bridge_generation");
        var sessionId = ReadAsciiValue(output.Span, "session_id");
        if (!long.TryParse(
                generationText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var generation) ||
            generation <= 0 ||
            !Regex.IsMatch(sessionId, "^[A-Za-z0-9_-]{8,64}$"))
        {
            throw new InvalidDataException("The USB bootstrap session binding was malformed.");
        }

        var expiryText = ReadAsciiValue(output.Span, "expires_at_ms");
        if (!long.TryParse(expiryText, out var expiryMilliseconds))
        {
            throw new InvalidDataException("The USB bootstrap expiry was malformed.");
        }
        var expiry = DateTimeOffset.FromUnixTimeMilliseconds(expiryMilliseconds);
        if (expiry <= now || expiry - now > MaximumSessionLifetime)
        {
            throw new InvalidDataException("The USB bootstrap session was expired or too long-lived.");
        }

        var enabledByRequest = ReadRequiredAsciiBoolean(output.Span, "enabled_by_request");
        var secretBase64 = ReadAsciiBytes(output.Span, "session_secret_base64");
        var secret = new byte[32];
        var decode = Base64.DecodeFromUtf8(secretBase64, secret, out var consumed, out var written);
        if (decode != OperationStatus.Done || consumed != secretBase64.Length || written != secret.Length)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new InvalidDataException("The USB bootstrap session credential was malformed.");
        }

        return new UsbBootstrapPayload(
            endpoint,
            generation,
            sessionId,
            secret,
            expiry,
            enabledByRequest);
    }

    private static DirectProviderStatus ParseDirectProviderStatus(
        ReadOnlyMemory<byte> output,
        RustyKioskProductContract product,
        string? operationId,
        bool requireOperationId)
    {
        RequireAsciiValue(output.Span, "accepted", "true");
        RequireAsciiValue(output.Span, "schema", RustyKioskContract.DirectUsbBootstrapSchema);
        RequireAsciiValue(output.Span, "product_channel", product.WireName);
        RequireAsciiValue(output.Span, "package", product.MainPackage);
        if (requireOperationId)
        {
            RequireAsciiValue(output.Span, "operation_id", operationId ?? string.Empty);
        }
        var generationText = ReadAsciiValue(output.Span, "bridge_generation");
        if (!long.TryParse(
                generationText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var generation) ||
            generation <= 0)
        {
            throw new InvalidDataException("The Direct Link cleanup generation was malformed.");
        }
        var enabledText = ReadAsciiValue(output.Span, "direct_enabled");
        var runningText = ReadAsciiValue(output.Span, "direct_running");
        if (enabledText is not ("true" or "false") || runningText is not ("true" or "false"))
        {
            throw new InvalidDataException("The Direct Link cleanup status was malformed.");
        }
        return new DirectProviderStatus(
            generation,
            string.Equals(enabledText, "true", StringComparison.Ordinal),
            string.Equals(runningText, "true", StringComparison.Ordinal));
    }

    private static string? BundleValue(string output, string key)
    {
        var match = Regex.Match(
            output,
            $@"(?:^|[{{,]\s*){Regex.Escape(key)}=([^,}}\]]*)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static void RequireAsciiValue(ReadOnlySpan<byte> output, string key, string expected)
    {
        if (!string.Equals(ReadAsciiValue(output, key), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The sensitive provider response did not match its fixed contract.");
        }
    }

    private static string ReadAsciiValue(ReadOnlySpan<byte> output, string key) =>
        Encoding.ASCII.GetString(ReadAsciiBytes(output, key));

    private static bool ReadRequiredAsciiBoolean(ReadOnlySpan<byte> output, string key) =>
        ReadAsciiValue(output, key) switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidDataException(
                "The sensitive provider response contained a malformed required boolean field.")
        };

    private static ReadOnlySpan<byte> ReadAsciiBytes(ReadOnlySpan<byte> output, string key)
    {
        var marker = Encoding.ASCII.GetBytes(key + "=");
        var index = -1;
        var searchOffset = 0;
        while (searchOffset <= output.Length - marker.Length)
        {
            var relative = output[searchOffset..].IndexOf(marker);
            if (relative < 0)
            {
                break;
            }
            var candidate = searchOffset + relative;
            var hasFieldBoundary = candidate == 0 || output[candidate - 1] is
                (byte)'{' or (byte)'[' or (byte)',' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
            if (hasFieldBoundary)
            {
                if (index >= 0)
                {
                    throw new InvalidDataException(
                        "The sensitive provider response duplicated a required field.");
                }
                index = candidate;
            }
            searchOffset = candidate + marker.Length;
        }
        if (index < 0)
        {
            throw new InvalidDataException("The sensitive provider response omitted a required field.");
        }
        var start = index + marker.Length;
        var end = start;
        while (end < output.Length && output[end] is not (byte)',' and not (byte)'}' and not (byte)']' and not (byte)'\r' and not (byte)'\n')
        {
            end++;
        }
        while (end > start && output[end - 1] == (byte)' ')
        {
            end--;
        }
        if (end == start)
        {
            throw new InvalidDataException("The sensitive provider response contained an empty required field.");
        }
        return output[start..end];
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class UsbBootstrapPayload(
        Uri endpoint,
        long bridgeGeneration,
        string sessionId,
        byte[] secret,
        DateTimeOffset expiresAtUtc,
        bool enabledByRequest) : IDisposable
    {
        private byte[]? _secret = secret;

        public Uri Endpoint { get; } = endpoint;
        public long BridgeGeneration { get; } = bridgeGeneration;
        public string SessionId { get; } = sessionId;
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        public bool EnabledByRequest { get; } = enabledByRequest;

        public byte[] TakeSecret() => Interlocked.Exchange(ref _secret, null) ??
            throw new ObjectDisposedException(nameof(UsbBootstrapPayload));

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _secret, null);
            if (value is not null)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
    }

    private sealed record DirectProviderStatus(
        long BridgeGeneration,
        bool Enabled,
        bool Running);
}
