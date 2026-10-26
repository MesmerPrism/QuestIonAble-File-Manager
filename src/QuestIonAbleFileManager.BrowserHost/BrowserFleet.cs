using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserFleetCapability(bool Supported, string Code, string Reason, string Alternative);
public sealed record BrowserFleetStatus(string Schema, string Status, bool Configured, string SourceKind,
    string? Product, string? Version, string? Channel, string? DescriptorId, string? DescriptorSha256,
    string? HighestHandoffVersion, string? LastOutcome, long ObservedAtMs);
public sealed record BrowserFleetOutcome(string Operation, BrowserFleetStatus Status);

/// <summary>Explicit release-status read only. Construction never resolves release configuration.</summary>
public sealed class BrowserFleet
{
    private readonly Func<OperatorCommand, CancellationToken, Task<OperatorExecutionResult>> _execute;
    public BrowserFleet(Func<OperatorCommand, CancellationToken, Task<OperatorExecutionResult>> execute, bool platformSupported)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        Capability = new(platformSupported, platformSupported ? "platform_supported" : "windows_fleet_installer_required",
            platformSupported ? "Windows Fleet release-status reading is implemented; configuration and replay evidence are checked only by an explicit read."
                : "Fleet release status requires Windows protected replay authority.",
            "Use the Windows companion or its typed fleet status CLI owner route.");
    }
    public BrowserFleetCapability Capability { get; }
    internal void EnsureSupported()
    { if (!Capability.Supported) throw new BrowserRequestException("windows_fleet_installer_required", 503); }
    internal async Task<BrowserCommandOutcome> ExecuteAsync(BrowserFleetRequest request, CancellationToken cancellation)
    {
        try
        {
            var command = request.ToOperatorCommand();
            var result = await _execute(command, cancellation).ConfigureAwait(false);
            return BrowserFleetProjection.Project(request, command, result);
        }
        catch (Exception exception)
        { return new(request.RequestId, request.Action, "failed", Error: BrowserFleetProjection.Error(exception)); }
    }
}
