namespace QuestIonAbleFileManager.Core;

/// <summary>Request contract identity; this does not advertise an implemented capability.</summary>
public static class LocalApiV3Contract
{
    public const string Version = "questionable.file_manager.local_api.v3";
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
