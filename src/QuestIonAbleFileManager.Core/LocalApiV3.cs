using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

/// <summary>Request contract identity; this does not advertise an implemented capability.</summary>
public static class LocalApiV3Contract
{
    public const string Version = "questionable.file_manager.local_api.v3";

    internal static string ComputeV3CommandDigest(OperatorCommand command, ApkArtifactInspection artifact,
        LocalApiDiagnosticOutputIntent intent) => Convert.ToHexString(SHA256.HashData(
            GetV3CanonicalCommandBytes(command, artifact, intent))).ToLowerInvariant();

    internal static byte[] GetV3CanonicalCommandBytes(OperatorCommand command, ApkArtifactInspection artifact,
        LocalApiDiagnosticOutputIntent intent)
    {
        _ = LocalApiDiagnosticOutputIntent.Restore(intent.OperationId, intent.OutputId, intent.PlannedLeafDigest);
        LocalApiV3ApkFacts.FromArtifact(artifact).Validate();
        new LocalApiV3PreflightRequest(Version, "apk.diagnose", command.LocalPath!, command.Serial!).Validate();
        if (command.Kind != OperatorCommandKind.DiagnoseInspectedApp || command.InstallOptions is not null ||
            command.LocalPath is null || command.OutputPath is null ||
            command.LocalPath.Any(char.IsControl) || command.OutputPath.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(command.LocalPath) || !Path.IsPathFullyQualified(command.OutputPath) ||
            !Regex.IsMatch(Path.GetFileName(command.LocalPath), "\\A[0-9a-f]{32}\\.apk\\z", RegexOptions.CultureInvariant))
            throw Failure("diagnostic_command_mismatch");
        var expectedOutput = Path.Combine(Path.GetDirectoryName(command.LocalPath)!, "d-" + intent.OutputId[5..]);
        var expected = OperatorCommands.DiagnoseInspectedApp(command.Serial!, command.LocalPath, expectedOutput);
        if (command.LocalPath != expected.LocalPath || command.OutputPath != expected.OutputPath ||
            !command.CliArguments.SequenceEqual(expected.CliArguments, StringComparer.Ordinal))
            throw Failure("diagnostic_command_mismatch");
        var arguments = expected.CliArguments.ToArray();
        arguments[5] = $"retained://{intent.OperationId}/base.apk";
        arguments[7] = $"retained-output://{intent.OperationId}/{intent.OutputId}";
        // The source inspection may precede staging. Only real immutable APK
        // facts enter this domain; Artifact.Path is never source/destination authority.
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            contractVersion = Version, action = "apk.diagnose",
            policyVersion = LocalApiDiagnosticOutputIntent.PolicyVersion, target = command.Serial,
            command = new { kind = command.Kind.ToString(), arguments },
            resources = new object[]
            {
                new { role = "apk", name = "base.apk", sizeBytes = artifact.SizeBytes, sha256 = artifact.Sha256,
                    identity = new { packageName = artifact.Identity.PackageName, versionCode = artifact.Identity.VersionCode,
                        versionName = artifact.Identity.VersionName, signerSha256 = artifact.Identity.SignerSha256,
                        splitName = artifact.Identity.SplitName } },
                new { role = "diagnostic-output", outputId = intent.OutputId,
                    archiveFormat = LocalApiDiagnosticOutputIntent.ArchiveFormat,
                    plannedLeafDigest = intent.PlannedLeafDigest, limits = intent.Limits }
            }
        });
    }

    internal static bool IsHash(string? value) => value is not null && Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    internal static bool IsOutputId(string? value) => value is not null &&
        Regex.IsMatch(value, "\\Adiag-[0-9a-f]{32}\\z", RegexOptions.CultureInvariant) && value[5..].Any(character => character != '0');
    internal static void RequireErrorCode(string? value)
    {
        if (value is not (null or "diagnostic_preparation_failed" or "diagnostic_execution_failed" or
            "diagnostic_output_unavailable" or "diagnostic_cleanup_pending" or "diagnostic_cleanup_debt" or
            "diagnostic_recovery_required" or "journal_persist_failed" or "operation_expired" or "operation_cancelled"))
            throw Failure("diagnostic_projection_invalid");
    }
    internal static LocalApiException Failure(string code) => new(code, "The v3 diagnostic contract is invalid.");
}

public sealed record LocalApiV3PreflightRequest(
    string ContractVersion,
    string Command,
    string ApkPath,
    string Serial,
    int? ExpiresInSeconds = null)
{
    public void Validate()
    {
        LocalApiV3OperationRequest.RequireVersion(ContractVersion);
        if (Command != "apk.diagnose")
            throw new LocalApiException("command_not_allowed", "The requested v3 command is not allowed.");
        if (string.IsNullOrWhiteSpace(ApkPath))
            throw new LocalApiException("apk_path_required", "The local APK file is required.");
        if (string.IsNullOrWhiteSpace(Serial))
            throw new LocalApiException("serial_required", "This command requires an exact serial.");
        try
        {
            // The existing serial validator is shared with the immutable command factory.
            // Reject controls explicitly because its regex permits a final LF in .NET.
            if (Serial.Any(char.IsControl))
                throw new ArgumentException("The serial contains a control character.");
            _ = AndroidInput.RequireSerial(Serial);
        }
        catch (ArgumentException)
        {
            throw new LocalApiException("serial_invalid", "The exact serial contains unsupported characters.");
        }
        var lifetime = TimeSpan.FromSeconds(ExpiresInSeconds ??
            (int)LocalApiContract.DefaultPreflightLifetime.TotalSeconds);
        if (lifetime <= TimeSpan.Zero || lifetime > LocalApiContract.MaximumPreflightLifetime)
            throw new LocalApiException("expiry_invalid", "Preflight expiry is outside the allowed bound.");
    }
}

public sealed record LocalApiV3ExecuteRequest(string ContractVersion, string OperationId, string CommandDigest)
{
    public void Validate() => LocalApiV3OperationRequest.RequireBinding(ContractVersion, OperationId, CommandDigest);
}

/// <summary>Status and cancellation both bind the original operation and digest.</summary>
public sealed record LocalApiV3OperationRequest(string ContractVersion, string OperationId, string CommandDigest)
{
    public void Validate() => RequireBinding(ContractVersion, OperationId, CommandDigest);

    internal static void RequireVersion(string version)
    {
        if (version != LocalApiV3Contract.Version)
            throw new LocalApiException("contract_unsupported", "The contract version is unsupported.");
    }

    internal static void RequireBinding(string version, string operationId, string commandDigest)
    {
        RequireVersion(version);
        if (operationId is null || operationId.Length != 36 ||
            !operationId.StartsWith("api-", StringComparison.Ordinal) ||
            !IsLowerHex(operationId.AsSpan(4)) ||
            operationId.AsSpan(4).IndexOfAnyExcept('0') < 0 ||
            commandDigest is null || commandDigest.Length != 64 || !IsLowerHex(commandDigest))
        {
            throw new LocalApiException("request_invalid", "A canonical operation id and command digest are required.");
        }
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        return true;
    }
}

/// <summary>Fixed policy limits, not an output reservation or execution capability.</summary>
public sealed record LocalApiV3OutputLimits(
    int MaximumProcessLogs, int RecentLogLinesPerCapture, int MaximumPayloadFiles, int MaximumPayloadFileBytes,
    long MaximumPayloadBytes, long MaximumArchiveBytes, long ReservedOutputBytes, int ReservedOutputFiles,
    int MaximumRequiredStandardOutputBytes, int MaximumRequiredStandardErrorBytes,
    int MaximumOptionalStandardOutputBytes, int MaximumOptionalStandardErrorBytes,
    long MaximumInputArtifactBytes, int MaximumBinaryFramingBytes, int MaximumProcessTimeoutSeconds)
{
    internal static LocalApiV3OutputLimits Current => new(
        LocalApiDiagnosticOutputIntent.MaximumProcessLogs, LocalApiDiagnosticOutputIntent.RecentLogLinesPerCapture,
        LocalApiDiagnosticOutputIntent.MaximumPayloadFiles, LocalApiDiagnosticOutputIntent.MaximumPayloadFileBytes,
        LocalApiDiagnosticOutputIntent.MaximumPayloadBytes, LocalApiDiagnosticOutputIntent.MaximumArchiveBytes,
        LocalApiDiagnosticOutputIntent.ReservedOutputBytes, LocalApiDiagnosticOutputIntent.ReservedOutputFiles,
        LocalApiDiagnosticOutputIntent.MaximumRequiredStandardOutputBytes, LocalApiDiagnosticOutputIntent.MaximumRequiredStandardErrorBytes,
        LocalApiDiagnosticOutputIntent.MaximumOptionalStandardOutputBytes, LocalApiDiagnosticOutputIntent.MaximumOptionalStandardErrorBytes,
        LocalApiDiagnosticOutputIntent.MaximumInputArtifactBytes, LocalApiDiagnosticOutputIntent.MaximumBinaryFramingBytes,
        LocalApiDiagnosticOutputIntent.MaximumProcessTimeoutSeconds);

    public void Validate()
    {
        if (this != Current) throw LocalApiV3Contract.Failure("diagnostic_limits_invalid");
    }
}

/// <summary>Pathless projection; physical leaves and native identities remain private.</summary>
public sealed record LocalApiV3OutputIntent(string OutputId, string PolicyVersion, string ArchiveFormat,
    string PlannedLeafDigest, LocalApiV3OutputLimits Limits)
{
    internal static LocalApiV3OutputIntent FromIntent(LocalApiDiagnosticOutputIntent intent)
    {
        _ = LocalApiDiagnosticOutputIntent.Restore(intent.OperationId, intent.OutputId, intent.PlannedLeafDigest);
        return new(intent.OutputId, LocalApiDiagnosticOutputIntent.PolicyVersion,
            LocalApiDiagnosticOutputIntent.ArchiveFormat, intent.PlannedLeafDigest, LocalApiV3OutputLimits.Current);
    }
    public void Validate()
    {
        if (!LocalApiV3Contract.IsOutputId(OutputId) || !LocalApiV3Contract.IsHash(PlannedLeafDigest) ||
            PolicyVersion != LocalApiDiagnosticOutputIntent.PolicyVersion || ArchiveFormat != LocalApiDiagnosticOutputIntent.ArchiveFormat || Limits is null)
            throw LocalApiV3Contract.Failure("diagnostic_intent_invalid");
        Limits.Validate();
    }
}

public sealed record LocalApiV3ApkFacts(long SizeBytes, string Sha256, ApkArtifactIdentity Identity)
{
    internal static LocalApiV3ApkFacts FromArtifact(ApkArtifactInspection artifact)
    {
        var facts = new LocalApiV3ApkFacts(artifact.SizeBytes, artifact.Sha256, artifact.Identity);
        facts.Validate();
        return facts;
    }
    public void Validate()
    {
        if (SizeBytes is < 1 or > LocalApiDiagnosticOutputIntent.MaximumInputArtifactBytes ||
            !LocalApiV3Contract.IsHash(Sha256) || Identity is null || Identity.VersionCode <= 0 ||
            !LocalApiV3Contract.IsHash(Identity.SignerSha256) || Identity.SplitName is not null ||
            string.IsNullOrWhiteSpace(Identity.PackageName) || Identity.PackageName.Any(char.IsControl))
            throw LocalApiV3Contract.Failure("diagnostic_artifact_invalid");
        try { _ = AndroidInput.RequirePackageName(Identity.PackageName); }
        catch (ArgumentException) { throw LocalApiV3Contract.Failure("diagnostic_artifact_invalid"); }
    }
}

/// <summary>Data only; construction does not advertise or activate a registry capability.</summary>
public sealed record LocalApiV3Capabilities(string ContractVersion, IReadOnlyList<string> Commands,
    int MaximumRequestBytes, int PreflightLifetimeSeconds, LocalApiV3OutputLimits OutputLimits);

public sealed record LocalApiV3PreflightResult(string ContractVersion, string OperationId, string Command,
    string CommandDigest, DateTimeOffset ExpiresAt, LocalApiV3ApkFacts Artifact, string Serial,
    LocalApiV3OutputIntent OutputIntent)
{
    public void Validate()
    {
        LocalApiV3OperationRequest.RequireBinding(ContractVersion, OperationId, CommandDigest);
        new LocalApiV3PreflightRequest(ContractVersion, Command, "base.apk", Serial).Validate();
        if (Artifact is null || OutputIntent is null) throw LocalApiV3Contract.Failure("diagnostic_projection_invalid");
        Artifact.Validate(); OutputIntent.Validate();
        _ = LocalApiDiagnosticOutputIntent.Restore(OperationId, OutputIntent.OutputId, OutputIntent.PlannedLeafDigest);
    }
}

/// <summary>Descriptor data alone grants no file access; the retained owner must acknowledge publication.</summary>
public sealed record LocalApiV3ArchiveDescriptor(string OutputId, string FileName, long SizeBytes,
    string Sha256, DateTimeOffset PublishedAt)
{
    public void Validate()
    {
        if (!LocalApiV3Contract.IsOutputId(OutputId) || FileName != "apk-diagnostics.zip" ||
            SizeBytes is < 1 or > LocalApiDiagnosticOutputIntent.MaximumArchiveBytes || !LocalApiV3Contract.IsHash(Sha256))
            throw LocalApiV3Contract.Failure("diagnostic_descriptor_invalid");
    }
}

public sealed record LocalApiV3OutputStatus(LocalApiV3OutputIntent Intent, string Phase, bool Available,
    int ResourceCount, int DebtCount, string? ErrorCode = null, LocalApiV3ArchiveDescriptor? Archive = null)
{
    public void Validate()
    {
        if (Intent is null || Phase is not ("preparing" or "preflighted" or "capturing" or "sealing" or "sealed" or
                "available" or "unavailable" or "cleanupPending" or "removed") ||
            ResourceCount is < 0 or > 16 || DebtCount < 0 || DebtCount > ResourceCount ||
            Available != (Phase == "available") || Available != (Archive is not null))
            throw LocalApiV3Contract.Failure("diagnostic_projection_invalid");
        Intent.Validate(); LocalApiV3Contract.RequireErrorCode(ErrorCode);
        if (Archive is not null)
        {
            Archive.Validate();
            if (Archive.OutputId != Intent.OutputId) throw LocalApiV3Contract.Failure("diagnostic_projection_invalid");
        }
    }
}

/// <summary>Capture completeness only; none of these fields proves application readiness or wearer visibility.</summary>
public sealed record LocalApiV3DiagnosticSummary(string DiagnosticContract, DateTimeOffset CapturedAt,
    string CaptureState, int PayloadCount, int FailedCount, int TruncatedCount)
{
    public string ApplicationReadiness => "unknown";
    public bool ApplicationReadinessAuthority => false;
    public string OpenXrReadiness => "unknown";
    public bool OpenXrReadinessAuthority => false;
    public string WearerVisibility => "unknown";
    public bool WearerVisibilityAuthority => false;

    internal static LocalApiV3DiagnosticSummary FromResult(ApkDiagnosticBundleResult result)
    {
        var failed = result.Files.Count(file => !file.Succeeded);
        var truncated = result.Files.Count(file => file.Truncated);
        var summary = new LocalApiV3DiagnosticSummary(result.DiagnosticContract, result.CapturedAt,
            failed != 0 || truncated != 0 ? "partial" : "complete", result.Files.Count, failed, truncated);
        summary.Validate();
        return summary;
    }
    public void Validate()
    {
        if (DiagnosticContract != "questionable.file_manager.apk_diagnostic_bundle.v3" ||
            CaptureState is not ("complete" or "partial") || PayloadCount is < 1 or > 14 ||
            FailedCount < 0 || FailedCount > PayloadCount || TruncatedCount < 0 || TruncatedCount > PayloadCount ||
            (CaptureState == "complete" && (FailedCount != 0 || TruncatedCount != 0)) ||
            JsonSerializer.SerializeToUtf8Bytes(this, JsonSerializerOptions.Web).Length > 4096)
            throw LocalApiV3Contract.Failure("diagnostic_summary_invalid");
    }
}

public sealed record LocalApiV3OperationStatus(string ContractVersion, string OperationId, string Command,
    string CommandDigest, DateTimeOffset ExpiresAt, LocalApiOperationStage Stage, bool Consumed, string Serial,
    LocalApiV3OutputStatus Output, LocalApiV3DiagnosticSummary? DiagnosticSummary = null, string? ErrorCode = null)
{
    public void Validate()
    {
        LocalApiV3OperationRequest.RequireBinding(ContractVersion, OperationId, CommandDigest);
        new LocalApiV3PreflightRequest(ContractVersion, Command, "base.apk", Serial).Validate();
        if (!Enum.IsDefined(Stage) || Output is null ||
            (Output.Available && (!Consumed || Stage != LocalApiOperationStage.Completed || DiagnosticSummary is null)))
            throw LocalApiV3Contract.Failure("diagnostic_projection_invalid");
        Output.Validate(); DiagnosticSummary?.Validate(); LocalApiV3Contract.RequireErrorCode(ErrorCode);
        _ = LocalApiDiagnosticOutputIntent.Restore(OperationId, Output.Intent.OutputId, Output.Intent.PlannedLeafDigest);
    }
}
