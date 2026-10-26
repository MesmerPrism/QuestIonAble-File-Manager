namespace QuestIonAbleFileManager.Core;

public enum RustyKioskAdbResultState { InstallationOnly, Pending, Missing, Completed, Rejected }

/// <summary>Bound facts for the closed ADB catalog workflow. Lookup facts never reconcile another operation.</summary>
public sealed record RustyKioskAdbResult(string Serial, string ProductChannel, string? ProviderRequestId,
    RustyKioskCommand? ExpectedCommand, string? Value, RustyKioskInstallationStatus? Installation,
    bool ActivityOpenRequested, RustyKioskAdbResultState State, RustyKioskOperatorResult? Result, string Message);

public static class RustyKioskAdbContract
{
    public const int MaximumResultBytes = 1024 * 1024;
    public const int MaximumCatalogEntries = 512;
    public const int MaximumMessageCharacters = 2048;

    public static string RequireProviderRequestId(string value)
    {
        if (value is null || !value.StartsWith("pc-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(value[3..], "N", out var id) || id == Guid.Empty || value[3..] != id.ToString("N"))
            throw new ArgumentException("The provider request ID must be pc- followed by a nonzero lowercase GUID N value.", nameof(value));
        return value;
    }

    public static string? RequireExpectedValue(RustyKioskCommand command, string? value)
    {
        if (command is not (RustyKioskCommand.Status or RustyKioskCommand.Select))
            throw new ArgumentException("Only status and select belong to the bounded Kiosk ADB workflow.", nameof(command));
        if (command == RustyKioskCommand.Status)
        {
            if (value is not null) throw new ArgumentException("Status does not accept a selection value.", nameof(value));
            return null;
        }
        var validated = command.ValidateValue(value);
        if (validated != value || validated!.Any(char.IsControl))
            throw new ArgumentException("The selection key must be one unchanged bounded catalog key.", nameof(value));
        return validated;
    }
}
