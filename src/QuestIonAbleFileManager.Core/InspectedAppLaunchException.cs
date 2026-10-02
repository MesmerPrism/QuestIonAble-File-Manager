namespace QuestIonAbleFileManager.Core;

public enum InspectedAppLaunchStage
{
    Dispatch,
    Readback
}

/// <summary>Retains uncertainty after entry into the fixed inspected launcher dispatch.</summary>
public sealed class InspectedAppLaunchException : InvalidOperationException
{
    public InspectedAppLaunchException(InspectedAppLaunchStage stage, Exception innerException)
        : base("The inspected launcher dispatch or its readback did not complete.", innerException)
    {
        Stage = stage;
    }

    public InspectedAppLaunchStage Stage { get; }
    public bool DispatchAttempted => true;
}
