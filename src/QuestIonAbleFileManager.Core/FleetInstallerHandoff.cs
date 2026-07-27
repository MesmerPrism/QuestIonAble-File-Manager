using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace QuestIonAbleFileManager.Core;

public static class FleetInstallerContract
{
    public const string EnvelopeSchema = "rusty.fleet.release_descriptor_envelope.v1";
    public const string PayloadSchema = "rusty.fleet.windows_release.v1";
    public const string PlanSchema = "rusty.fleet.guided_installer_plan.v1";
    public const string StatusSchema = "questionable.file_manager.fleet_installer_status.v1";
    public const string HandoffSchema = "questionable.file_manager.fleet_installer_handoff.v1";
    public const string StateSchema = "questionable.file_manager.fleet_installer_state.v1";
    public const string Product = "rusty-fleet";
    public const string AssetName = "RustyFleet-Setup.exe";
    public const string InstallerProtocol = "rusty.fleet.guided_setup.v1";
    public const int MaximumDescriptorBytes = 64 * 1024;
    public const long MaximumAssetBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan MaximumDescriptorLifetime = TimeSpan.FromDays(14);
}

public sealed class FleetInstallerException : InvalidOperationException
{
    public FleetInstallerException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record FleetInstallerTrustPolicy(
    byte[] DescriptorSignerSubjectPublicKeyInfo,
    string DescriptorSignerSpkiSha256,
    string InstallerSignerCertificateSha256,
    string Channel)
{
    public void Validate()
    {
        if (DescriptorSignerSubjectPublicKeyInfo.Length is < 64 or > 4096 ||
            !FleetInstallerValidation.IsLowerSha256(DescriptorSignerSpkiSha256) ||
            !FleetInstallerValidation.IsLowerSha256(InstallerSignerCertificateSha256) ||
            !FleetInstallerValidation.IsIdentifier(Channel, 64))
        {
            throw new FleetInstallerException(
                "fleet_trust_policy_invalid",
                "The Fleet release trust policy is incomplete or invalid.");
        }

        var actual = Convert.ToHexString(
            SHA256.HashData(DescriptorSignerSubjectPublicKeyInfo)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(DescriptorSignerSpkiSha256)))
        {
            throw new FleetInstallerException(
                "fleet_descriptor_signer_pin_mismatch",
                "The configured Fleet descriptor public key does not match its SHA-256 pin.");
        }
    }
}

public sealed record FleetInstallerSettings(
    IFleetReleaseSource Source,
    FleetInstallerTrustPolicy TrustPolicy,
    string PrivateStageRoot)
{
    public static FleetInstallerSettings? FromEnvironment()
    {
        var descriptorSource = Environment.GetEnvironmentVariable(
            "QUESTIONABLE_FILE_MANAGER_FLEET_RELEASE_DESCRIPTOR");
        if (string.IsNullOrWhiteSpace(descriptorSource))
        {
            return null;
        }

        var stageRoot = Environment.GetEnvironmentVariable(
            "QUESTIONABLE_FILE_MANAGER_FLEET_INSTALLER_STATE");
        var publicKeyPath = Environment.GetEnvironmentVariable(
            "QUESTIONABLE_FILE_MANAGER_FLEET_DESCRIPTOR_PUBLIC_KEY");
        var publicKeyDigest = Environment.GetEnvironmentVariable(
            "QUESTIONABLE_FILE_MANAGER_FLEET_DESCRIPTOR_SIGNER_SHA256");
        var installerSigner = Environment.GetEnvironmentVariable(
            "QUESTIONABLE_FILE_MANAGER_FLEET_INSTALLER_SIGNER_SHA256");
        var channel = Environment.GetEnvironmentVariable(
            "QUESTIONABLE_FILE_MANAGER_FLEET_CHANNEL") ?? "stable";
        if (string.IsNullOrWhiteSpace(stageRoot) ||
            string.IsNullOrWhiteSpace(publicKeyPath) ||
            string.IsNullOrWhiteSpace(publicKeyDigest) ||
            string.IsNullOrWhiteSpace(installerSigner))
        {
            throw new FleetInstallerException(
                "fleet_installer_configuration_incomplete",
                "Fleet installer configuration requires the private state root and both descriptor and installer signer pins.");
        }

        var fullPublicKeyPath = Path.GetFullPath(publicKeyPath);
        var pem = File.ReadAllText(fullPublicKeyPath, Encoding.ASCII);
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (Exception exception) when (
            exception is ArgumentException or CryptographicException)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_public_key_invalid",
                "The configured Fleet descriptor public key is invalid.",
                exception);
        }

        IFleetReleaseSource source;
        if (Uri.TryCreate(descriptorSource, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            source = new HttpsFleetReleaseSource(
                uri,
                new Uri(uri, "."),
                new HttpClient(handler, disposeHandler: true));
        }
        else
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(
                        "QUESTIONABLE_FILE_MANAGER_FLEET_ALLOW_LOCAL_FIXTURE"),
                    "1",
                    StringComparison.Ordinal))
            {
                throw new FleetInstallerException(
                    "fleet_descriptor_source_invalid",
                    "Fleet release descriptors must use the configured HTTPS source.");
            }
            source = new LocalFleetReleaseSource(Path.GetFullPath(descriptorSource));
        }

        var policy = new FleetInstallerTrustPolicy(
            rsa.ExportSubjectPublicKeyInfo(),
            publicKeyDigest,
            installerSigner,
            channel);
        policy.Validate();
        return new FleetInstallerSettings(source, policy, Path.GetFullPath(stageRoot));
    }
}

public interface IFleetReleaseSource
{
    string Kind { get; }

    Task<byte[]> ReadDescriptorAsync(CancellationToken cancellationToken);

    Task CopyAssetAsync(
        string assetName,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken);
}

public sealed class LocalFleetReleaseSource : IFleetReleaseSource
{
    private readonly string _descriptorPath;
    private readonly string _fixtureDirectory;

    public LocalFleetReleaseSource(string descriptorPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);
        if (!Path.IsPathFullyQualified(descriptorPath))
        {
            throw new FleetInstallerException(
                "fleet_fixture_path_invalid",
                "The explicit Fleet fixture descriptor path must be absolute.");
        }
        _descriptorPath = Path.GetFullPath(descriptorPath);
        _fixtureDirectory = Path.GetDirectoryName(_descriptorPath)
            ?? throw new FleetInstallerException(
                "fleet_fixture_path_invalid",
                "The explicit Fleet fixture descriptor has no parent directory.");
    }

    public string Kind => "local_fixture";

    public async Task<byte[]> ReadDescriptorAsync(CancellationToken cancellationToken) =>
        await ReadBoundedLocalFileAsync(
            _descriptorPath,
            FleetInstallerContract.MaximumDescriptorBytes,
            cancellationToken).ConfigureAwait(false);

    public async Task CopyAssetAsync(
        string assetName,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                assetName,
                FleetInstallerContract.AssetName,
                StringComparison.Ordinal))
        {
            throw new FleetInstallerException(
                "fleet_asset_name_invalid",
                "The Fleet descriptor selected an unsupported installer asset.");
        }

        var assetPath = Path.Combine(_fixtureDirectory, FleetInstallerContract.AssetName);
        await using var input = OpenSecureRead(assetPath);
        await CopyBoundedAsync(input, destination, maximumBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedLocalFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = OpenSecureRead(path);
        if (input.Length is < 2 || input.Length > maximumBytes)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_size_invalid",
                "The Fleet release descriptor is empty or exceeds its byte limit.");
        }
        using var output = new MemoryStream(checked((int)input.Length));
        await CopyBoundedAsync(input, output, maximumBytes, cancellationToken)
            .ConfigureAwait(false);
        return output.ToArray();
    }

    private static FileStream OpenSecureRead(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
        }

        var stream = FleetWindowsFileSafety.OpenReadOnlyFile(path);
        FleetWindowsFileSafety.ValidateFile(
            stream.SafeFileHandle,
            path,
            requireSingleLink: true);
        return stream;
    }

    internal static async Task CopyBoundedAsync(
        Stream input,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            total += count;
            if (total > maximumBytes)
            {
                throw new FleetInstallerException(
                    "fleet_asset_oversized",
                    "The Fleet installer exceeded its descriptor-bound byte limit.");
            }
            await destination.WriteAsync(
                buffer.AsMemory(0, count),
                cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class HttpsFleetReleaseSource : IFleetReleaseSource, IDisposable
{
    private readonly Uri _descriptorUri;
    private readonly Uri _assetBaseUri;
    private readonly HttpClient _httpClient;

    public HttpsFleetReleaseSource(
        Uri descriptorUri,
        Uri assetBaseUri,
        HttpClient httpClient)
    {
        ValidateInitialUri(descriptorUri);
        ValidateInitialUri(assetBaseUri);
        if (!descriptorUri.AbsolutePath.StartsWith(
                assetBaseUri.AbsolutePath,
                StringComparison.Ordinal))
        {
            throw new FleetInstallerException(
                "fleet_asset_source_invalid",
                "The Fleet asset base must contain the configured descriptor.");
        }
        _descriptorUri = descriptorUri;
        _assetBaseUri = assetBaseUri;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string Kind => "https";

    public async Task<byte[]> ReadDescriptorAsync(CancellationToken cancellationToken)
    {
        using var response = await SendBoundedAsync(
            _descriptorUri,
            cancellationToken).ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        await LocalFleetReleaseSource.CopyBoundedAsync(
            input,
            output,
            FleetInstallerContract.MaximumDescriptorBytes,
            cancellationToken).ConfigureAwait(false);
        if (output.Length < 2)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_size_invalid",
                "The Fleet release descriptor is empty.");
        }
        return output.ToArray();
    }

    public async Task CopyAssetAsync(
        string assetName,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                assetName,
                FleetInstallerContract.AssetName,
                StringComparison.Ordinal))
        {
            throw new FleetInstallerException(
                "fleet_asset_name_invalid",
                "The Fleet descriptor selected an unsupported installer asset.");
        }
        var assetUri = new Uri(_assetBaseUri, assetName);
        ValidateInitialUri(assetUri);
        if (!assetUri.AbsolutePath.StartsWith(
                _assetBaseUri.AbsolutePath,
                StringComparison.Ordinal))
        {
            throw new FleetInstallerException(
                "fleet_asset_source_invalid",
                "The Fleet installer asset escaped the configured release source.");
        }
        using var response = await SendBoundedAsync(assetUri, cancellationToken)
            .ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await LocalFleetReleaseSource.CopyBoundedAsync(
            input,
            destination,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<HttpResponseMessage> SendBoundedAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, uri),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (IsRedirect(response.StatusCode))
        {
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new FleetInstallerException(
                    "fleet_source_redirect_rejected",
                    "The Fleet release source returned a redirect without a destination.");
            }
            var target = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!IsAllowedReleaseRedirect(uri, target))
            {
                throw new FleetInstallerException(
                    "fleet_source_redirect_rejected",
                    "The Fleet release source redirected outside the reviewed GitHub release boundary.");
            }
            response = await _httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, target),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                response.Dispose();
                throw new FleetInstallerException(
                    "fleet_source_redirect_rejected",
                    "The Fleet release source exceeded its one reviewed redirect.");
            }
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new FleetInstallerException(
                "fleet_source_failed",
                $"The Fleet release source returned HTTP {status}.");
        }
        return response;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static bool IsAllowedReleaseRedirect(Uri source, Uri target) =>
        string.Equals(source.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        string.Equals(
            target.Host,
            "release-assets.githubusercontent.com",
            StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(target.UserInfo) &&
        string.IsNullOrEmpty(target.Fragment);

    private static void ValidateInitialUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port != 443)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_source_invalid",
                "The Fleet release source must be a clean HTTPS URI.");
        }

        var githubRelease =
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith(
                "/MesmerPrism/rusty-fleet/releases/download/",
                StringComparison.Ordinal);
        var githubPages =
            string.Equals(
                uri.Host,
                "mesmerprism.github.io",
                StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/rusty-fleet/", StringComparison.Ordinal);
        if (!githubRelease && !githubPages)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_source_invalid",
                "The Fleet release source must be the reviewed Rusty Fleet GitHub Release or Pages path.");
        }
    }
}

public sealed record FleetReleaseAsset(
    string Name,
    long SizeBytes,
    string Sha256,
    string SignerCertificateSha256,
    string MediaType,
    string InstallerProtocol);

public sealed record FleetReleaseDescriptor(
    string DescriptorId,
    string Product,
    string Version,
    string Channel,
    long IssuedAtMs,
    long ExpiresAtMs,
    FleetReleaseAsset Asset,
    string PayloadSha256,
    string DescriptorSignerSpkiSha256);

public sealed record FleetInstallerStatusReceipt(
    string Schema,
    string Status,
    bool Configured,
    string SourceKind,
    string? Product,
    string? Version,
    string? Channel,
    string? DescriptorId,
    string? DescriptorSha256,
    string? HighestHandoffVersion,
    string? LastOutcome,
    long ObservedAtMs);

public sealed record FleetInstallerHandoffReceipt(
    string Schema,
    string Status,
    string Product,
    string Version,
    string Channel,
    string DescriptorId,
    string DescriptorSha256,
    string DescriptorSignerSpkiSha256,
    string AssetSha256,
    long AssetSizeBytes,
    string InstallerSignerCertificateSha256,
    bool PlanVerified,
    bool GuidedInstallerStarted,
    int GuidedInstallerExitCode,
    bool CleanupCompleted,
    long ObservedAtMs);

public interface IFleetInstallerArtifactTrustVerifier
{
    string Verify(string executablePath);
}

public interface IFleetInstallerProcessRunner
{
    Task<FleetInstallerPlanReceipt> RunPlanAsync(
        string executablePath,
        CancellationToken cancellationToken);

    Task<int> RunGuidedAsync(
        string executablePath,
        CancellationToken cancellationToken);
}

public sealed record FleetInstallerPlanReceipt(
    string Schema,
    string Product,
    string Version,
    string Channel,
    string AssetSha256,
    bool Ready);

public sealed class WindowsFleetInstallerArtifactTrustVerifier :
    IFleetInstallerArtifactTrustVerifier
{
    public string Verify(string executablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Fleet installer Authenticode verification requires Windows.");
        }
        WindowsAuthenticode.Verify(executablePath);
#pragma warning disable SYSLIB0057 // No X509CertificateLoader API reads the signer embedded in a PE Authenticode signature.
        using var certificate = X509Certificate.CreateFromSignedFile(executablePath);
#pragma warning restore SYSLIB0057
        var rawCertificate = certificate.Export(X509ContentType.Cert);
        return Convert.ToHexString(SHA256.HashData(rawCertificate)).ToLowerInvariant();
    }
}

public sealed class FleetInstallerProcessRunner : IFleetInstallerProcessRunner
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private static readonly TimeSpan PlanTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GuidedTimeout = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = FleetInstallerValidation.Json;

    public async Task<FleetInstallerPlanReceipt> RunPlanAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            executablePath,
            ["--plan", "--json"],
            PlanTimeout,
            visible: false,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 ||
            !string.IsNullOrEmpty(result.StandardError))
        {
            throw new FleetInstallerException(
                "fleet_installer_plan_failed",
                "The Fleet guided installer did not return a clean successful plan.");
        }
        FleetInstallerValidation.RejectDuplicateProperties(
            Encoding.UTF8.GetBytes(result.StandardOutput));
        try
        {
            return JsonSerializer.Deserialize<FleetInstallerPlanReceipt>(
                    result.StandardOutput,
                    Json) ??
                throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new FleetInstallerException(
                "fleet_installer_plan_invalid",
                "The Fleet guided installer returned an invalid strict plan receipt.",
                exception);
        }
    }

    public async Task<int> RunGuidedAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            executablePath,
            [],
            GuidedTimeout,
            visible: true,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(result.StandardError))
        {
            throw new FleetInstallerException(
                "fleet_guided_installer_stderr",
                "The Fleet guided installer wrote unexpected standard-error output.");
        }
        if (result.ExitCode != 0)
        {
            throw new FleetInstallerException(
                "fleet_guided_installer_failed",
                $"The Fleet guided installer exited with code {result.ExitCode}.");
        }
        return result.ExitCode;
    }

    internal static async Task<CommandResult> RunProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        bool visible,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ??
                throw new FleetInstallerException(
                    "fleet_installer_path_invalid",
                    "The Fleet guided installer path is invalid."),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = !visible,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment.Clear();
        foreach (var name in new[]
                 {
                     "SystemRoot",
                     "WINDIR",
                     "USERPROFILE",
                     "HOMEDRIVE",
                     "HOMEPATH",
                     "LOCALAPPDATA",
                     "APPDATA",
                     "TEMP",
                     "TMP"
                 })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var job = WindowsProcessJob.Create();
        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                throw new FleetInstallerException(
                    "fleet_installer_start_failed",
                    "The Fleet guided installer could not be started.");
            }
            job.Assign(process);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
                FleetInstallerException)
        {
            TryKillTree(process);
            throw new FleetInstallerException(
                "fleet_installer_start_failed",
                "The Fleet guided installer could not be started.",
                exception);
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, linked.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, linked.Token);
        var exitTask = process.WaitForExitAsync(linked.Token);
        try
        {
            var pending = new List<Task> { exitTask, stdoutTask, stderrTask };
            while (pending.Count > 1)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            if (pending.Count == 1)
            {
                await pending[0].ConfigureAwait(false);
            }
            await job.WaitForEmptyAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            job.Terminate();
            TryKillTree(process);
            throw new TimeoutException(
                $"The Fleet guided installer exceeded its {timeout} deadline.");
        }
        catch
        {
            job.Terminate();
            TryKillTree(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        job.ReleaseSuccessfulChildren();
        stopwatch.Stop();
        return new CommandResult(
            executablePath,
            arguments.ToArray(),
            process.ExitCode,
            stdout,
            stderr,
            stopwatch.Elapsed);
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            if (builder.Length + count > MaximumOutputCharacters)
            {
                throw new FleetInstallerException(
                    "fleet_installer_output_oversized",
                    "The Fleet guided installer exceeded its output limit.");
            }
            builder.Append(buffer, 0, count);
        }
        return builder.ToString();
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // The original timeout, cancellation, or output failure remains authoritative.
        }
    }
}

internal sealed class WindowsProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle _handle;
    private bool _released;

    private WindowsProcessJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static WindowsProcessJob Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Fleet installer process containment requires Windows.");
        }

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new FleetInstallerException(
                "fleet_installer_job_unavailable",
                "The Fleet guided installer process container could not be created.");
        }
        var job = new WindowsProcessJob(handle);
        try
        {
            job.SetLimit(JobObjectLimitKillOnJobClose);
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new FleetInstallerException(
                "fleet_installer_job_unavailable",
                "The Fleet guided installer could not enter its process container.");
        }
    }

    public void Terminate()
    {
        if (_released || _handle.IsInvalid || _handle.IsClosed)
        {
            return;
        }
        TerminateJobObject(_handle, 1);
    }

    public void ReleaseSuccessfulChildren()
    {
        if (_released)
        {
            return;
        }
        SetLimit(0);
        _released = true;
    }

    public async Task WaitForEmptyAsync(CancellationToken cancellationToken)
    {
        while (GetActiveProcessCount() != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private void SetLimit(uint flags)
    {
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = flags
            }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, fDeleteOld: false);
            if (!SetInformationJobObject(
                    _handle,
                    JobObjectExtendedLimitInformationClass,
                    pointer,
                    (uint)size))
            {
                throw new FleetInstallerException(
                    "fleet_installer_job_unavailable",
                    "The Fleet guided installer process container could not be configured.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private uint GetActiveProcessCount()
    {
        var size = Marshal.SizeOf<JobObjectBasicAccountingInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                    _handle,
                    JobObjectBasicAccountingInformationClass,
                    pointer,
                    (uint)size,
                    out _))
            {
                throw new FleetInstallerException(
                    "fleet_installer_job_unavailable",
                    "The Fleet guided installer process container could not be observed.");
            }
            return Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(
                pointer).ActiveProcesses;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(
        IntPtr jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(
        SafeFileHandle job,
        uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    private const int JobObjectBasicAccountingInformationClass = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

public sealed class FleetInstallerHandoff
{
    private readonly FleetInstallerSettings? _settings;
    private readonly IFleetInstallerArtifactTrustVerifier _artifactTrustVerifier;
    private readonly IFleetInstallerProcessRunner _processRunner;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FleetInstallerHandoff(
        FleetInstallerSettings? settings,
        IFleetInstallerArtifactTrustVerifier? artifactTrustVerifier = null,
        IFleetInstallerProcessRunner? processRunner = null,
        TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _artifactTrustVerifier =
            artifactTrustVerifier ?? new WindowsFleetInstallerArtifactTrustVerifier();
        _processRunner = processRunner ?? new FleetInstallerProcessRunner();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static FleetInstallerHandoff FromEnvironment() =>
        new(FleetInstallerSettings.FromEnvironment());

    public async Task<FleetInstallerStatusReceipt> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (_settings is null)
        {
            return new FleetInstallerStatusReceipt(
                FleetInstallerContract.StatusSchema,
                "not_configured",
                Configured: false,
                SourceKind: "none",
                Product: null,
                Version: null,
                Channel: null,
                DescriptorId: null,
                DescriptorSha256: null,
                HighestHandoffVersion: null,
                LastOutcome: null,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings.TrustPolicy.Validate();
            var descriptor = await ReadAndVerifyDescriptorAsync(
                _settings,
                cancellationToken).ConfigureAwait(false);
            using var workspace = FleetInstallerWorkspace.Open(_settings.PrivateStageRoot);
            var state = workspace.ReadState();
            var status = state.AcceptedDescriptorIds.Contains(
                descriptor.DescriptorId,
                StringComparer.Ordinal)
                ? "already_handed_off"
                : state.HighestHandoffVersion is not null &&
                  Version.Parse(descriptor.Version) <=
                  Version.Parse(state.HighestHandoffVersion)
                    ? "not_newer_than_last_handoff"
                    : "ready";
            return new FleetInstallerStatusReceipt(
                FleetInstallerContract.StatusSchema,
                status,
                Configured: true,
                _settings.Source.Kind,
                descriptor.Product,
                descriptor.Version,
                descriptor.Channel,
                descriptor.DescriptorId,
                descriptor.PayloadSha256,
                state.HighestHandoffVersion,
                state.LastOutcome,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FleetInstallerHandoffReceipt> InstallAsync(
        CancellationToken cancellationToken = default,
        IProgress<OperatorProgress>? progress = null)
    {
        if (_settings is null)
        {
            throw new FleetInstallerException(
                "fleet_installer_not_configured",
                "The optional Fleet installer handoff is not configured.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report(new OperatorProgress(
                "fleet-descriptor",
                "Verifying the signed Rusty Fleet release descriptor…",
                0,
                5));
            _settings.TrustPolicy.Validate();
            var descriptor = await ReadAndVerifyDescriptorAsync(
                _settings,
                cancellationToken).ConfigureAwait(false);

            using var workspace = FleetInstallerWorkspace.Open(_settings.PrivateStageRoot);
            var state = workspace.ReadState();
            EnsureNotReplayOrDowngrade(state, descriptor);

            progress?.Report(new OperatorProgress(
                "fleet-stage",
                "Staging and hashing the exact Fleet guided installer…",
                1,
                5));
            using var stage = workspace.CreateStage(descriptor.DescriptorId);
            await stage.WriteAssetAsync(
                _settings.Source,
                descriptor.Asset,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new OperatorProgress(
                "fleet-trust",
                "Verifying the Fleet installer signature and trust policy…",
                2,
                5));
            var signer = _artifactTrustVerifier.Verify(stage.ExecutablePath);
            if (!FleetInstallerValidation.FixedLowerHexEquals(
                    signer,
                    descriptor.Asset.SignerCertificateSha256) ||
                !FleetInstallerValidation.FixedLowerHexEquals(
                    signer,
                    _settings.TrustPolicy.InstallerSignerCertificateSha256))
            {
                throw new FleetInstallerException(
                    "fleet_installer_signer_mismatch",
                    "The Fleet installer signer does not match the signed descriptor and configured trust policy.");
            }

            progress?.Report(new OperatorProgress(
                "fleet-plan",
                "Running the Fleet-owned non-mutating installer plan…",
                3,
                5));
            var plan = await _processRunner.RunPlanAsync(
                stage.ExecutablePath,
                cancellationToken).ConfigureAwait(false);
            ValidatePlan(descriptor, plan);

            state = state.Accept(descriptor, "plan_verified");
            workspace.WriteState(state);

            progress?.Report(new OperatorProgress(
                "fleet-guided-installer",
                "Opening the visible Fleet-owned guided installer…",
                4,
                5));
            int exitCode;
            try
            {
                exitCode = await _processRunner.RunGuidedAsync(
                    stage.ExecutablePath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                workspace.WriteState(state with { LastOutcome = "guided_installer_failed" });
                throw;
            }

            workspace.WriteState(state with { LastOutcome = "guided_installer_completed" });
            stage.Cleanup();
            progress?.Report(new OperatorProgress(
                "fleet-handoff-complete",
                "The Fleet-owned guided installer completed its handoff.",
                5,
                5));
            return new FleetInstallerHandoffReceipt(
                FleetInstallerContract.HandoffSchema,
                "guided_installer_completed",
                descriptor.Product,
                descriptor.Version,
                descriptor.Channel,
                descriptor.DescriptorId,
                descriptor.PayloadSha256,
                descriptor.DescriptorSignerSpkiSha256,
                descriptor.Asset.Sha256,
                descriptor.Asset.SizeBytes,
                signer,
                PlanVerified: true,
                GuidedInstallerStarted: true,
                GuidedInstallerExitCode: exitCode,
                CleanupCompleted: true,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FleetReleaseDescriptor> ReadAndVerifyDescriptorAsync(
        FleetInstallerSettings settings,
        CancellationToken cancellationToken)
    {
        var bytes = await settings.Source.ReadDescriptorAsync(cancellationToken)
            .ConfigureAwait(false);
        return FleetInstallerValidation.VerifyDescriptor(
            bytes,
            settings.TrustPolicy,
            _timeProvider.GetUtcNow());
    }

    private static void EnsureNotReplayOrDowngrade(
        FleetInstallerState state,
        FleetReleaseDescriptor descriptor)
    {
        if (state.AcceptedDescriptorIds.Contains(
                descriptor.DescriptorId,
                StringComparer.Ordinal))
        {
            throw new FleetInstallerException(
                "fleet_descriptor_replay",
                "This Fleet release descriptor has already been handed off.");
        }
        if (state.HighestHandoffVersion is null)
        {
            return;
        }

        var versionComparison = Version.Parse(descriptor.Version).CompareTo(
            Version.Parse(state.HighestHandoffVersion));
        if (versionComparison < 0)
        {
            throw new FleetInstallerException(
                "fleet_release_downgrade_rejected",
                "The Fleet release is older than the highest previously verified handoff.");
        }
        if (versionComparison == 0)
        {
            throw new FleetInstallerException(
                "fleet_release_not_newer_rejected",
                "The Fleet release is not newer than the highest previously verified handoff.");
        }
    }

    private static void ValidatePlan(
        FleetReleaseDescriptor descriptor,
        FleetInstallerPlanReceipt plan)
    {
        if (plan.Schema != FleetInstallerContract.PlanSchema ||
            plan.Product != descriptor.Product ||
            plan.Version != descriptor.Version ||
            plan.Channel != descriptor.Channel ||
            !FleetInstallerValidation.FixedLowerHexEquals(
                plan.AssetSha256,
                descriptor.Asset.Sha256) ||
            !plan.Ready)
        {
            throw new FleetInstallerException(
                "fleet_installer_plan_mismatch",
                "The Fleet guided installer plan does not bind the verified release.");
        }
    }
}

internal sealed record FleetInstallerState(
    string Schema,
    string? HighestHandoffVersion,
    IReadOnlyList<string> AcceptedDescriptorIds,
    string? LastOutcome)
{
    public static FleetInstallerState Empty { get; } =
        new(FleetInstallerContract.StateSchema, null, [], null);

    public FleetInstallerState Accept(
        FleetReleaseDescriptor descriptor,
        string outcome)
    {
        var ids = AcceptedDescriptorIds
            .Append(descriptor.DescriptorId)
            .TakeLast(256)
            .ToArray();
        var highest = HighestHandoffVersion is null ||
                      Version.Parse(descriptor.Version) >
                      Version.Parse(HighestHandoffVersion)
            ? descriptor.Version
            : HighestHandoffVersion;
        return new FleetInstallerState(
            FleetInstallerContract.StateSchema,
            highest,
            ids,
            outcome);
    }
}

internal sealed class FleetInstallerWorkspace : IDisposable
{
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;
    private static readonly JsonSerializerOptions Json = FleetInstallerValidation.Json;
    private readonly string _root;
    private readonly SafeFileHandle _rootHandle;
    private readonly FileStream _ownerLock;

    private FleetInstallerWorkspace(
        string root,
        SafeFileHandle rootHandle,
        FileStream ownerLock)
    {
        _root = root;
        _rootHandle = rootHandle;
        _ownerLock = ownerLock;
    }

    public static FleetInstallerWorkspace Open(string configuredRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Fleet installer secure staging requires Windows.");
        }
        if (!Path.IsPathFullyQualified(configuredRoot) ||
            configuredRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new FleetInstallerException(
                "fleet_stage_root_invalid",
                "The Fleet installer state root must be a local absolute Windows path.");
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        var driveRoot = Path.GetPathRoot(root)
            ?? throw new FleetInstallerException(
                "fleet_stage_root_invalid",
                "The Fleet installer state root is invalid.");
        if (new DriveInfo(driveRoot).DriveType is
            DriveType.Network or DriveType.NoRootDirectory)
        {
            throw new FleetInstallerException(
                "fleet_stage_root_nonlocal",
                "The Fleet installer state root must be on a local drive.");
        }
        Directory.CreateDirectory(root);
        var rootHandle = FleetWindowsFileSafety.OpenDirectory(root, allowDelete: false);
        try
        {
            FleetWindowsFileSafety.ValidateDirectory(rootHandle, root);
            var lockPath = Path.Combine(root, "fleet-installer.owner.lock");
            var ownerLock = FleetWindowsFileSafety.OpenOrCreateExclusiveFile(lockPath);
            FleetWindowsFileSafety.ValidateFile(
                ownerLock.SafeFileHandle,
                lockPath,
                requireSingleLink: true);
            return new FleetInstallerWorkspace(root, rootHandle, ownerLock);
        }
        catch
        {
            rootHandle.Dispose();
            throw;
        }
    }

    public FleetInstallerState ReadState()
    {
        var path = Path.Combine(_root, "fleet-installer.state.json");
        if (!File.Exists(path))
        {
            return FleetInstallerState.Empty;
        }

        byte[] bytes;
        using (var input = FleetWindowsFileSafety.OpenReadOnlyFile(path))
        {
            FleetWindowsFileSafety.ValidateFile(
                input.SafeFileHandle,
                path,
                requireSingleLink: true);
            if (input.Length is < 2 or > FleetInstallerContract.MaximumDescriptorBytes)
            {
                throw new FleetInstallerException(
                    "fleet_installer_state_invalid",
                    "The Fleet installer state is invalid.");
            }
            bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
        }

        FleetInstallerValidation.RejectDuplicateProperties(bytes);
        FleetInstallerState state;
        try
        {
            state = JsonSerializer.Deserialize<FleetInstallerState>(bytes, Json)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new FleetInstallerException(
                "fleet_installer_state_invalid",
                "The Fleet installer state is invalid.",
                exception);
        }
        if (state.Schema != FleetInstallerContract.StateSchema ||
            state.AcceptedDescriptorIds.Count > 256 ||
            state.AcceptedDescriptorIds.Distinct(StringComparer.Ordinal).Count() !=
            state.AcceptedDescriptorIds.Count ||
            state.AcceptedDescriptorIds.Any(
                static value => !FleetInstallerValidation.IsIdentifier(value, 128)) ||
            state.HighestHandoffVersion is not null &&
            !FleetInstallerValidation.IsThreePartVersion(state.HighestHandoffVersion) ||
            state.LastOutcome is { Length: > 128 })
        {
            throw new FleetInstallerException(
                "fleet_installer_state_invalid",
                "The Fleet installer state is invalid.");
        }
        return state;
    }

    public void WriteState(FleetInstallerState state)
    {
        FleetWindowsFileSafety.ValidateDirectory(_rootHandle, _root);
        var path = Path.Combine(_root, "fleet-installer.state.json");
        var temporary = Path.Combine(_root, "fleet-installer.state." +
            Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        using (var output = FleetWindowsFileSafety.CreateNewOwnedFile(temporary))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
            FleetWindowsFileSafety.ValidateFile(
                output.SafeFileHandle,
                temporary,
                requireSingleLink: true);
        }

        try
        {
            if (!MoveFileEx(
                    temporary,
                    path,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The Fleet installer state rename could not be committed.");
            }
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    public FleetInstallerStage CreateStage(string descriptorId)
    {
        FleetWindowsFileSafety.ValidateDirectory(_rootHandle, _root);
        var token = "fleet-" +
            Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(descriptorId)))
                .ToLowerInvariant()[..24] +
            "-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(_root, token);
        Directory.CreateDirectory(path);
        SafeFileHandle? handle = null;
        try
        {
            handle = FleetWindowsFileSafety.OpenDirectory(path, allowDelete: true);
            FleetWindowsFileSafety.ValidateDirectory(handle, path);
            return new FleetInstallerStage(path, handle);
        }
        catch
        {
            handle?.Dispose();
            TryDeleteDirectory(path);
            throw;
        }
    }

    public void Dispose()
    {
        _ownerLock.Dispose();
        _rootHandle.Dispose();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The state-write failure remains authoritative.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path);
        }
        catch
        {
            // The stage-creation failure remains authoritative.
        }
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}

internal sealed class FleetInstallerStage : IDisposable
{
    private readonly string _path;
    private readonly SafeFileHandle _directoryHandle;
    private FileStream? _retainedExecutable;
    private FleetWindowsFileIdentity? _fileIdentity;
    private bool _cleaned;

    public FleetInstallerStage(string path, SafeFileHandle directoryHandle)
    {
        _path = path;
        _directoryHandle = directoryHandle;
        ExecutablePath = Path.Combine(path, FleetInstallerContract.AssetName);
    }

    public string ExecutablePath { get; }

    public async Task WriteAssetAsync(
        IFleetReleaseSource source,
        FleetReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        FleetWindowsFileSafety.ValidateDirectory(_directoryHandle, _path);
        await using (var output =
                     FleetWindowsFileSafety.CreateNewRetainedReadableFile(ExecutablePath))
        {
            FleetWindowsFileSafety.ValidateFile(
                output.SafeFileHandle,
                ExecutablePath,
                requireSingleLink: true);
            await source.CopyAssetAsync(
                asset.Name,
                output,
                asset.SizeBytes,
                cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (output.Length != asset.SizeBytes)
            {
                throw new FleetInstallerException(
                    "fleet_asset_size_mismatch",
                    "The Fleet installer size does not match the signed descriptor.");
            }
            output.Position = 0;
            var digest = Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant();
            if (!FleetInstallerValidation.FixedLowerHexEquals(digest, asset.Sha256))
            {
                throw new FleetInstallerException(
                    "fleet_asset_digest_mismatch",
                    "The Fleet installer SHA-256 does not match the signed descriptor.");
            }
            FleetWindowsFileSafety.ValidateFile(
                output.SafeFileHandle,
                ExecutablePath,
                requireSingleLink: true);
            _fileIdentity = FleetWindowsFileSafety.GetIdentity(output.SafeFileHandle);
        }

        _retainedExecutable =
            FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(ExecutablePath);
        FleetWindowsFileSafety.ValidateFile(
            _retainedExecutable.SafeFileHandle,
            ExecutablePath,
            requireSingleLink: true);
        if (FleetWindowsFileSafety.GetIdentity(_retainedExecutable.SafeFileHandle) !=
            _fileIdentity)
        {
            throw new FleetInstallerException(
                "fleet_stage_identity_changed",
                "The staged Fleet installer identity changed after verification.");
        }
    }

    public void Cleanup()
    {
        if (_cleaned)
        {
            return;
        }
        _cleaned = true;
        _retainedExecutable?.Dispose();
        _retainedExecutable = null;
        if (File.Exists(ExecutablePath))
        {
            using var deletion = FleetWindowsFileSafety.OpenFileForDeletion(
                ExecutablePath);
            FleetWindowsFileSafety.ValidateFile(
                deletion.SafeFileHandle,
                ExecutablePath,
                requireSingleLink: true);
            if (_fileIdentity is not null &&
                FleetWindowsFileSafety.GetIdentity(deletion.SafeFileHandle) !=
                _fileIdentity.Value)
            {
                throw new FleetInstallerException(
                    "fleet_stage_identity_changed",
                    "The staged Fleet installer identity changed before cleanup.");
            }
            FleetWindowsFileSafety.MarkDelete(deletion.SafeFileHandle);
        }
        FleetWindowsFileSafety.ValidateDirectory(_directoryHandle, _path);
        FleetWindowsFileSafety.MarkDelete(_directoryHandle);
    }

    public void Dispose()
    {
        if (!_cleaned)
        {
            try
            {
                Cleanup();
            }
            catch
            {
                _directoryHandle.Dispose();
                throw;
            }
        }
        _directoryHandle.Dispose();
    }
}

internal static class FleetInstallerValidation
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static FleetReleaseDescriptor VerifyDescriptor(
        ReadOnlySpan<byte> envelopeBytes,
        FleetInstallerTrustPolicy policy,
        DateTimeOffset now)
    {
        if (envelopeBytes.Length is < 2 or > FleetInstallerContract.MaximumDescriptorBytes)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_size_invalid",
                "The Fleet release descriptor is empty or exceeds its byte limit.");
        }
        RejectDuplicateProperties(envelopeBytes);
        FleetReleaseEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<FleetReleaseEnvelope>(
                    envelopeBytes,
                    Json) ??
                throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_invalid",
                "The Fleet release descriptor envelope is invalid strict JSON.",
                exception);
        }
        if (envelope.Schema != FleetInstallerContract.EnvelopeSchema ||
            !FixedLowerHexEquals(
                envelope.SignerSpkiSha256,
                policy.DescriptorSignerSpkiSha256))
        {
            throw new FleetInstallerException(
                "fleet_descriptor_signer_mismatch",
                "The Fleet release descriptor signer is not trusted.");
        }

        var payload = DecodeBase64Url(
            envelope.PayloadBase64Url,
            FleetInstallerContract.MaximumDescriptorBytes,
            "fleet_descriptor_payload_invalid");
        var signature = DecodeBase64Url(
            envelope.SignatureBase64Url,
            1024,
            "fleet_descriptor_signature_invalid");
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(
                policy.DescriptorSignerSubjectPublicKeyInfo,
                out var read);
            if (read != policy.DescriptorSignerSubjectPublicKeyInfo.Length ||
                !rsa.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
            {
                throw new FleetInstallerException(
                    "fleet_descriptor_signature_invalid",
                    "The Fleet release descriptor signature was not accepted.");
            }
        }
        catch (CryptographicException exception)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_signature_invalid",
                "The Fleet release descriptor signature was not accepted.",
                exception);
        }

        RejectDuplicateProperties(payload);
        FleetReleasePayload parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FleetReleasePayload>(payload, Json)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_payload_invalid",
                "The signed Fleet release payload is invalid strict JSON.",
                exception);
        }

        ValidatePayload(parsed, policy, now);
        return new FleetReleaseDescriptor(
            parsed.DescriptorId,
            parsed.Product,
            parsed.Version,
            parsed.Channel,
            parsed.IssuedAtMs,
            parsed.ExpiresAtMs,
            parsed.Asset,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            envelope.SignerSpkiSha256);
    }

    public static void RejectDuplicateProperties(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            var scopes = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                }
                else if (reader.TokenType == JsonTokenType.EndObject)
                {
                    scopes.Pop();
                }
                else if (reader.TokenType == JsonTokenType.PropertyName &&
                         scopes.TryPeek(out var scope) &&
                         !scope.Add(reader.GetString() ?? string.Empty))
                {
                    throw new FleetInstallerException(
                        "fleet_descriptor_invalid",
                        "The Fleet release descriptor contains a duplicate property.");
                }
            }
        }
        catch (JsonException exception)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_invalid",
                "The Fleet release descriptor is invalid JSON.",
                exception);
        }
    }

    public static bool IsIdentifier(string? value, int maximum) =>
        value is not null &&
        value.Length is >= 1 &&
        value.Length <= maximum &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-');

    public static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            char.IsAsciiHexDigit(character) &&
            !char.IsAsciiLetterUpper(character));

    public static bool FixedLowerHexEquals(string left, string right) =>
        IsLowerSha256(left) &&
        IsLowerSha256(right) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    public static bool IsThreePartVersion(string value) =>
        value.Split('.').Length == 3 &&
        Version.TryParse(value, out var version) &&
        version.Major >= 0 &&
        version.Minor >= 0 &&
        version.Build >= 0 &&
        string.Equals(version.ToString(3), value, StringComparison.Ordinal);

    private static void ValidatePayload(
        FleetReleasePayload payload,
        FleetInstallerTrustPolicy policy,
        DateTimeOffset now)
    {
        if (payload.Schema != FleetInstallerContract.PayloadSchema ||
            payload.Product != FleetInstallerContract.Product ||
            !IsIdentifier(payload.DescriptorId, 128) ||
            !IsThreePartVersion(payload.Version) ||
            payload.Channel != policy.Channel)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_binding_invalid",
                "The Fleet release descriptor product, version, channel, or identity is invalid.");
        }
        var nowMs = now.ToUnixTimeMilliseconds();
        if (payload.IssuedAtMs <= 0 ||
            payload.ExpiresAtMs <= payload.IssuedAtMs ||
            payload.ExpiresAtMs - payload.IssuedAtMs >
                FleetInstallerContract.MaximumDescriptorLifetime.TotalMilliseconds ||
            nowMs < payload.IssuedAtMs - 30_000 ||
            nowMs >= payload.ExpiresAtMs)
        {
            throw new FleetInstallerException(
                "fleet_descriptor_stale",
                "The Fleet release descriptor is not fresh.");
        }
        if (payload.Asset.Name != FleetInstallerContract.AssetName ||
            payload.Asset.SizeBytes is < 1 or > FleetInstallerContract.MaximumAssetBytes ||
            !IsLowerSha256(payload.Asset.Sha256) ||
            !IsLowerSha256(payload.Asset.SignerCertificateSha256) ||
            !FixedLowerHexEquals(
                payload.Asset.SignerCertificateSha256,
                policy.InstallerSignerCertificateSha256) ||
            payload.Asset.MediaType != "application/vnd.microsoft.portable-executable" ||
            payload.Asset.InstallerProtocol != FleetInstallerContract.InstallerProtocol)
        {
            throw new FleetInstallerException(
                "fleet_asset_binding_invalid",
                "The Fleet installer asset identity or trust binding is invalid.");
        }
    }

    private static byte[] DecodeBase64Url(
        string value,
        int maximumBytes,
        string code)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > maximumBytes * 2 ||
            value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '_')))
        {
            throw new FleetInstallerException(
                code,
                "The Fleet descriptor contains invalid base64url evidence.");
        }
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length > maximumBytes ||
                !string.Equals(
                    Convert.ToBase64String(bytes)
                        .TrimEnd('=')
                        .Replace('+', '-')
                        .Replace('/', '_'),
                    value,
                    StringComparison.Ordinal))
            {
                throw new FleetInstallerException(
                    code,
                    "The Fleet descriptor contains non-canonical base64url evidence.");
            }
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new FleetInstallerException(
                code,
                "The Fleet descriptor contains invalid base64url evidence.",
                exception);
        }
    }
}

internal sealed record FleetReleaseEnvelope(
    string Schema,
    [property: JsonPropertyName("payload_base64url")]
    string PayloadBase64Url,
    [property: JsonPropertyName("signature_base64url")]
    string SignatureBase64Url,
    string SignerSpkiSha256);

internal sealed record FleetReleasePayload(
    string Schema,
    string DescriptorId,
    string Product,
    string Version,
    string Channel,
    long IssuedAtMs,
    long ExpiresAtMs,
    FleetReleaseAsset Asset);

internal static class WindowsAuthenticode
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static void Verify(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var dataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var data = new WinTrustData(fileInfoPointer);
            dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPointer, fDeleteOld: false);
            var result = WinVerifyTrust(
                IntPtr.Zero,
                GenericVerifyV2,
                dataPointer);
            if (result != 0)
            {
                throw new FleetInstallerException(
                    "fleet_installer_authenticode_invalid",
                    $"Windows rejected the Fleet installer Authenticode signature (0x{result:x8}).");
            }
        }
        finally
        {
            if (dataPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustData>(dataPointer);
                Marshal.FreeHGlobal(dataPointer);
            }
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(
        IntPtr window,
        [MarshalAs(UnmanagedType.LPStruct)] Guid action,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string path)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00001000;
            UiContext = 0;
        }
    }
}
