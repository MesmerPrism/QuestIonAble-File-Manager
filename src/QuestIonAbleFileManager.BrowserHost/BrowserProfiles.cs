using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserProfileCapability(bool Supported, string Code, string Reason, string Alternative,
    int MaximumPrivateInputBytes = 4096, int MaximumListEntries = 128);
public sealed record BrowserProfileFailure(string Code, string? Status, string? RollbackState);
public sealed record BrowserProfileOutcome(string Operation, string? DeviceId, bool? ReplaceExisting, string Effect,
    QuestConnectivityProfileListReceipt? List = null, QuestConnectivityProfileStatusReceipt? Status = null,
    QuestConnectivityProfileMutationReceipt? Receipt = null, BrowserProfileFailure? Failure = null,
    int? TotalProfiles = null, bool Truncated = false);

/// <summary>Profile-only typed adapter. Construction/capability never opens the native store.</summary>
public sealed class BrowserProfiles
{
    private readonly Func<OperatorCommand, Stream?, CancellationToken, Task<OperatorExecutionResult>> _execute;
    private readonly IQuestConnectivityPrivateInputReader _reader;
    public BrowserProfiles(Func<OperatorCommand, Stream?, CancellationToken, Task<OperatorExecutionResult>> execute,
        IQuestConnectivityPrivateInputReader privateInputReader, bool platformSupported)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _reader = privateInputReader ?? throw new ArgumentNullException(nameof(privateInputReader));
        Capability = new(platformSupported, platformSupported ? "platform_supported" : "windows_credential_manager_required",
            platformSupported ? "Windows current-user profile management is implemented; availability is checked only by an explicit operation." : "Connectivity profiles require the Windows current-user Credential Manager.",
            "Use the Windows companion or its typed connectivity-profile CLI owner route.");
    }
    public BrowserProfileCapability Capability { get; }
    internal void EnsureSupported() { if (!Capability.Supported) throw new BrowserRequestException("windows_credential_manager_required", 503); }
    internal Task<byte[]> ReadPrivateAsync(Stream input, CancellationToken cancellation) => _reader.ReadStreamAsync(input, cancellation);
    internal async Task<BrowserCommandOutcome> ExecuteAsync(BrowserProfileRequest request, Stream? input, CancellationToken cancellation)
    {
        try
        {
            var command = request.ToOperatorCommand();
            var result = await _execute(command, input, cancellation).ConfigureAwait(false);
            return BrowserProfileProjection.Project(request, command, result);
        }
        catch (QuestConnectivityProfileManagementException exception) { return BrowserProfileProjection.Failed(request, exception); }
        catch (Exception) { return BrowserProfileProjection.Unknown(request, "profile_operation_failed"); }
    }
}
