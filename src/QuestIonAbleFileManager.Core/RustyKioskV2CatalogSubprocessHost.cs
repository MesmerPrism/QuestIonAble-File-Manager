using System.Security.Cryptography;

namespace QuestIonAbleFileManager.Core;

public sealed class RustyKioskV2CatalogSubprocessHost
{
    private static readonly byte[] Newline = [(byte)'\n'];
    private readonly Func<RustyKioskV2CatalogProvider> _providerFactory;
    private readonly TimeProvider _timeProvider;

    public RustyKioskV2CatalogSubprocessHost(
        Func<RustyKioskV2CatalogProvider> providerFactory,
        TimeProvider? timeProvider = null)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static RustyKioskV2CatalogSubprocessHost CreateWindows() =>
        new(() => new RustyKioskV2CatalogProvider(
            new WindowsCredentialRustyKioskV2ProviderProfileStore()));

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (ProviderCapabilityDiscoveryContract.HasExactDescribeArguments(
                arguments))
        {
            await ProviderCapabilityDiscoveryProjection.WriteAsync(
                    output,
                    ProviderCapabilityDiscoveryProjection.CreateKioskCatalog(
                        _timeProvider.GetUtcNow()),
                    cancellationToken);
            return 0;
        }

        const string unavailableIdentity = "unavailable";
        var profileId = unavailableIdentity;
        var requestId = unavailableIdentity;
        byte[]? requestBytes = null;
        try
        {
            if (!HasExactArguments(arguments))
            {
                throw RustyKioskV2ProviderException.Rejected("provider_arguments_invalid");
            }

            requestBytes = await ReadBoundedInputAsync(input, cancellationToken);
            var request = RustyKioskV2CatalogProviderRequest.Parse(requestBytes);
            profileId = request.ProfileId;
            requestId = request.RequestId;
            using var provider = _providerFactory();
            using var exchange = await provider.FetchAsync(profileId, request, cancellationToken);
            await WriteResponseAsync(
                output,
                RustyKioskV2CatalogProviderResponse.Verified(profileId, requestId, exchange),
                cancellationToken);
            return RustyKioskV2ProviderContract.ExitCodeForStatus(
                RustyKioskV2ProviderContract.VerifiedStatus);
        }
        catch (RustyKioskV2ProviderException exception)
        {
            await WriteResponseAsync(
                output,
                RustyKioskV2CatalogProviderResponse.Failure(
                    exception.Status,
                    profileId,
                    requestId,
                    exception.Code),
                CancellationToken.None);
            return RustyKioskV2ProviderContract.ExitCodeForStatus(exception.Status);
        }
        catch (OperationCanceledException)
        {
            await WriteResponseAsync(
                output,
                RustyKioskV2CatalogProviderResponse.Failure(
                    RustyKioskV2ProviderContract.FailedStatus,
                    profileId,
                    requestId,
                    "provider_cancelled"),
                CancellationToken.None);
            return RustyKioskV2ProviderContract.ExitCodeForStatus(
                RustyKioskV2ProviderContract.FailedStatus);
        }
        catch
        {
            await WriteResponseAsync(
                output,
                RustyKioskV2CatalogProviderResponse.Failure(
                    RustyKioskV2ProviderContract.FailedStatus,
                    profileId,
                    requestId,
                    "provider_internal_error"),
                CancellationToken.None);
            return RustyKioskV2ProviderContract.ExitCodeForStatus(
                RustyKioskV2ProviderContract.FailedStatus);
        }
        finally
        {
            if (requestBytes is not null)
            {
                CryptographicOperations.ZeroMemory(requestBytes);
            }
        }
    }

    private static bool HasExactArguments(IReadOnlyList<string> arguments) =>
        arguments.Count == 3 &&
        string.Equals(arguments[0], "integration", StringComparison.Ordinal) &&
        string.Equals(arguments[1], "kiosk-v2-catalog", StringComparison.Ordinal) &&
        string.Equals(arguments[2], "--json", StringComparison.Ordinal);

    private static async Task<byte[]> ReadBoundedInputAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        if (!input.CanRead)
        {
            throw RustyKioskV2ProviderException.Rejected("request_input_unreadable");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(chunk, cancellationToken);
                if (count == 0)
                {
                    break;
                }
                if (buffer.Length + count > RustyKioskV2ProviderContract.MaximumRequestBytes)
                {
                    throw RustyKioskV2ProviderException.Rejected("request_size_invalid");
                }
                buffer.Write(chunk, 0, count);
            }
            return buffer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
        }
    }

    private static async Task WriteResponseAsync(
        Stream output,
        RustyKioskV2CatalogProviderResponse response,
        CancellationToken cancellationToken)
    {
        if (!output.CanWrite)
        {
            throw new InvalidOperationException("Provider output is not writable.");
        }

        var bytes = response.ToUtf8Json();
        try
        {
            await output.WriteAsync(bytes, cancellationToken);
            await output.WriteAsync(Newline, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
