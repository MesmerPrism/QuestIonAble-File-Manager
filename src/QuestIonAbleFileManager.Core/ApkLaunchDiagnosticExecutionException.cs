namespace QuestIonAbleFileManager.Core;

/// <summary>A failed invocation's owner-observed fixed launch boundary.</summary>
public sealed class ApkLaunchDiagnosticExecutionException : Exception
{
    internal ApkLaunchDiagnosticExecutionException(bool dispatchBoundaryCrossed, Exception innerException)
        : base("Launch diagnostics failed; consult the closed dispatch provenance.", innerException)
    {
        DispatchBoundaryCrossed = dispatchBoundaryCrossed;
    }

    public bool DispatchBoundaryCrossed { get; }
}
