using System.Security.Cryptography;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core;

/// <summary>The closed, single-target exact inspected-deployment contract.</summary>
public static class LocalApiV2Contract
{
    public const string Version = "questionable.file_manager.local_api.v2";
    public const string DeployPolicyVersion = "exact-installed-install-launch.v1";
    public static readonly IReadOnlyList<string> Commands = ["apk.deploy"];
}

public sealed record LocalApiV2PreflightRequest(
    string ContractVersion,
    string Command,
    string ApkPath,
    string Serial,
    LocalApiInstallOptions? InstallOptions = null,
    int? ExpiresInSeconds = null)
{
    public void Validate()
    {
        LocalApiV2OperationRequest.RequireVersion(ContractVersion);
        if (Command != "apk.deploy")
            throw new LocalApiException("command_not_allowed", "The requested v2 command is not implemented.");
        if (string.IsNullOrWhiteSpace(ApkPath))
            throw new LocalApiException("apk_path_required", "The local APK file is required.");
        if (string.IsNullOrWhiteSpace(Serial))
            throw new LocalApiException("serial_required", "This command requires an exact serial.");
        if (InstallOptions?.AllowDowngrade == true)
            throw new LocalApiException("downgrade_not_allowed", "Downgrade is not allowed through the local API.");
        if (InstallOptions?.AllowTestPackages == true)
            throw new LocalApiException("test_only_not_allowed", "Android test-only packages are not allowed through the local API.");
    }
}

public sealed record LocalApiV2ExecuteRequest(string ContractVersion, string OperationId, string CommandDigest)
{
    public void Validate()
    {
        LocalApiV2OperationRequest.RequireVersion(ContractVersion);
        if (string.IsNullOrWhiteSpace(OperationId) || string.IsNullOrWhiteSpace(CommandDigest))
            throw new LocalApiException("request_invalid", "Operation id and command digest are required.");
    }
}

public sealed record LocalApiV2OperationRequest(string ContractVersion, string OperationId)
{
    public void Validate()
    {
        RequireVersion(ContractVersion);
        if (string.IsNullOrWhiteSpace(OperationId))
            throw new LocalApiException("request_invalid", "Operation id is required.");
    }

    internal static void RequireVersion(string version)
    {
        if (version != LocalApiV2Contract.Version)
            throw new LocalApiException("contract_unsupported", "The contract version is unsupported.");
    }
}

public sealed partial class LocalApiCommandRegistry
{
    public LocalApiCapabilities GetV2Capabilities() => new(
        LocalApiV2Contract.Version, LocalApiV2Contract.Commands,
        LocalApiContract.MaximumRequestBytes, (int)LocalApiContract.DefaultPreflightLifetime.TotalSeconds);

    public Task<LocalApiPreflightResult> PreflightV2Async(
        ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        var request = ParseBound<LocalApiV2PreflightRequest>(body);
        request.Validate();
        return PreflightRetainedAsync(new LocalApiPreflightRequest(
            request.ContractVersion, request.Command, request.ApkPath, request.Serial,
            request.InstallOptions, request.ExpiresInSeconds), LocalApiV2Contract.Version, cancellationToken);
    }

    public Task<LocalApiOperationStatus> ExecuteV2Async(
        ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        var request = ParseBound<LocalApiV2ExecuteRequest>(body);
        request.Validate();
        // The retained owner outlives HTTP/page loss after admission. Only explicit
        // operation cancellation can request executor cancellation, with acknowledgement.
        return ExecuteRetainedAsync(request.OperationId, request.CommandDigest,
            LocalApiV2Contract.Version, CancellationToken.None);
    }

    public LocalApiOperationStatus GetV2Status(ReadOnlyMemory<byte> body)
    {
        var request = ParseBound<LocalApiV2OperationRequest>(body);
        request.Validate();
        return GetRetainedStatus(request.OperationId, LocalApiV2Contract.Version);
    }

    public LocalApiOperationStatus CancelV2(ReadOnlyMemory<byte> body)
    {
        var request = ParseBound<LocalApiV2OperationRequest>(body);
        request.Validate();
        return CancelRetained(request.OperationId, LocalApiV2Contract.Version);
    }

    private static void RequireOperationVersion(RetainedOperation operation, string version)
    {
        if (operation.ContractVersion != version)
            throw Input("operation_version_mismatch", "The retained operation belongs to a different contract version.");
    }

    private static OperatorCommand BuildCommandForVersion(LocalApiPreflightRequest request, string version)
    {
        if (version == LocalApiContract.Version)
            return BuildCommand(request);
        var v2 = new LocalApiV2PreflightRequest(version, request.Command, request.ApkPath,
            request.Serial!, request.InstallOptions, request.ExpiresInSeconds);
        v2.Validate();
        return OperatorCommands.DeployInspectedApp(v2.Serial, v2.ApkPath, v2.InstallOptions?.ToCore());
    }

    private static string ComputeDigestForVersion(
        OperatorCommand command, ApkArtifactInspection artifact, string version) =>
        version == LocalApiContract.Version
            ? ComputeCommandDigest(command, artifact)
            : ComputeV2CommandDigest(command, artifact);

    private static string ComputeV2CommandDigest(OperatorCommand command, ApkArtifactInspection artifact)
    {
        // A separate canonical domain preserves all v1 digest bytes. One ordered
        // APK resource and the normalized immutable options bind this closed policy.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            contractVersion = LocalApiV2Contract.Version,
            action = "apk.deploy",
            policyVersion = LocalApiV2Contract.DeployPolicyVersion,
            target = command.Serial,
            options = command.InstallOptions,
            arguments = command.CliArguments,
            resources = new[]
            {
                new { role = "apk", name = Path.GetFileName(artifact.Path),
                    artifact.SizeBytes, artifact.Sha256, artifact.Identity }
            }
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }
}
