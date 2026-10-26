using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserKioskInstallation(bool MainInstalled, string? MainVersion, bool SetupHelperInstalled,
    string? SetupHelperVersion, bool SetupHelperReady, bool SameSignerControlGranted, bool HostOperatorAvailable);
public sealed record BrowserKioskCatalogEntry(string Key, string Name, string? PackageName, bool Installed, bool Launchable, bool Selectable);
public sealed record BrowserKioskResult(string ProviderRequestId, string Command, bool Accepted, bool Completed,
    string? SelectedKey, string? SelectedName, string? SelectedPackage, bool SelectedInstalled, bool SelectedLaunchable,
    int InstalledCount, int NotInstalledCount, int VisibleCount, bool VisibleEntriesTruncated, bool ProjectionTruncated,
    IReadOnlyList<BrowserKioskCatalogEntry> Entries);
public sealed record BrowserKioskOutcome(string Serial, string ProductChannel, string Operation, string? ProviderRequestId,
    string ProviderState, bool? ActivityOpenRequested, BrowserKioskInstallation? Installation, BrowserKioskResult? Result,
    string? CatalogRequestId, string? ReviewedKey, string? ReviewedPackage, bool OriginalFactsOnly);

/// <summary>Only selected typed Kiosk facts cross into the browser; native vectors and raw owner state stay private.</summary>
internal sealed class BrowserKioskProjection(BrowserKioskRequest request, OperatorCommand command, BrowserKioskReview? review)
{
    private const int MaximumEntries = 128;
    private const int MaximumLabelCharacters = 256;

    public BrowserCommandOutcome Project(OperatorExecutionResult executed)
    {
        var value = executed.RustyKioskAdbResult;
        if (value is null || executed.Command.Kind != command.Kind || executed.Command.Serial != request.Serial ||
            executed.Command.RustyKioskProduct != command.RustyKioskProduct ||
            !executed.Command.CliArguments.SequenceEqual(command.CliArguments, StringComparer.Ordinal) ||
            value.Serial != request.Serial || value.ProductChannel != request.ProductChannel ||
            value.ProviderRequestId != request.ProviderRequestId || value.ExpectedCommand != request.ExpectedCommand || value.Value != request.Key ||
            !Enum.IsDefined(value.State))
            return Failure("kiosk_result_binding_mismatch");
        if (value.Result is { } ownerResult && (ownerResult.Schema != RustyKioskContract.ResultSchema || ownerResult.RequestId != request.ProviderRequestId ||
                ownerResult.Command != request.ExpectedCommand))
            return Failure("kiosk_result_binding_mismatch");
        if (request.Action is "kiosk.installation" or "kiosk.original-result" &&
            (value.ActivityOpenRequested || executed.MutationReceipt is not null))
            return Failure("kiosk_result_binding_mismatch");
        if (value.State == RustyKioskAdbResultState.Completed && value.Result is not { Completed: true })
            return Failure("kiosk_result_binding_mismatch");
        if (request.Action == "kiosk.original-result" &&
            (value.Installation is not null || value.State == RustyKioskAdbResultState.InstallationOnly))
            return Failure("kiosk_result_binding_mismatch");
        var installation = value.Installation is { } installed ? Installation(installed) : null;
        var result = value.Result is { } facts ? Result(facts) : null;
        var providerState = value.State switch
        {
            RustyKioskAdbResultState.InstallationOnly => "installationOnly",
            RustyKioskAdbResultState.Pending => "pending",
            RustyKioskAdbResultState.Missing => "missing",
            RustyKioskAdbResultState.Completed => "completed",
            RustyKioskAdbResultState.Rejected => "rejected",
            _ => "unknown"
        };
        if (request.Action == "kiosk.installation")
        {
            if (installation is null || value.State != RustyKioskAdbResultState.InstallationOnly || value.Result is not null)
                return Failure("kiosk_result_binding_mismatch");
            return Outcome("completed", providerState, value.ActivityOpenRequested, installation, null);
        }
        if (request.Action == "kiosk.original-result")
        {
            // This is the result of one read, never a transition on the saved original.
            return Outcome("completed", providerState, false, installation, result);
        }
        var marker = BoundMarker(executed.MutationReceipt) ? executed.MutationReceipt : null;
        var confirmed = marker is { Stage: OperatorMutationStage.Confirmed, HeadsetReadback: true };
        var noOpen = request.Mutation && value.State == RustyKioskAdbResultState.InstallationOnly &&
            !value.ActivityOpenRequested && value.Result is null && installation is { HostOperatorAvailable: false } && confirmed;
        if (noOpen)
            return Outcome("completed", providerState, false, installation, null, Sanitize(marker!, noOpen: true));
        var completed = value.State == RustyKioskAdbResultState.Completed && result is { Accepted: true, Completed: true } &&
            value.ActivityOpenRequested && installation is not null && confirmed;
        if (request.Action == "kiosk.select")
            completed &= review is not null && result is { SelectedInstalled: true, SelectedLaunchable: true } &&
                result.SelectedKey == review.Key && result.SelectedPackage == review.PackageName;
        return completed
            ? Outcome("completed", providerState, value.ActivityOpenRequested, installation, result, Sanitize(marker!),
                catalog: request.Action == "kiosk.catalog.open" ? request.RequestId : request.CatalogRequestId)
            : Outcome("outcomeUnknown", providerState, value.ActivityOpenRequested, installation, result,
                marker is { Stage: not OperatorMutationStage.Confirmed } ? Sanitize(marker) : null, "kiosk_readback_unconfirmed");
    }

    public BrowserCommandOutcome Failure(string error, OperatorMutationReceipt? receipt = null) =>
        Outcome(request.Mutation ? "outcomeUnknown" : "failed", "unknown", null, null, null,
            BoundMarker(receipt) && receipt!.Stage != OperatorMutationStage.Confirmed ? Sanitize(receipt) : null, error);

    private BrowserCommandOutcome Outcome(string state, string providerState, bool? activity, BrowserKioskInstallation? installation,
        BrowserKioskResult? result, OperatorMutationReceipt? marker = null, string? error = null, string? catalog = null) =>
        new(request.RequestId, request.Action, state, Error: error, Mutation: marker,
            Kiosk: new(request.Serial, request.ProductChannel, request.Operation, request.ProviderRequestId, providerState, activity,
                installation, result, catalog ?? request.CatalogRequestId, request.Key, review?.PackageName, request.Action == "kiosk.original-result"));

    private bool BoundMarker(OperatorMutationReceipt? marker) => request.Mutation && marker is not null &&
        marker.CommandKind == command.Kind && marker.Target == request.Serial && marker.DesiredState == DesiredState &&
        BrowserKioskCommands.IsProviderId(marker.OperationId) && Enum.IsDefined(marker.Stage) && marker.Transitions is { Count: <= 16 } &&
        marker.Transitions.All(static transition => transition is not null && Enum.IsDefined(transition.Stage));
    private string DesiredState => request.Action == "kiosk.catalog.open"
        ? "Rusty Kiosk installation inspected; catalog requested if available (" + request.ProductChannel + ")"
        : "Rusty Kiosk installation inspected; selection " + request.Key + " requested if available (" + request.ProductChannel + ")";
    private OperatorMutationReceipt Sanitize(OperatorMutationReceipt marker, bool noOpen = false) => marker with
    {
        ObservedState = noOpen ? NoOpMessage : "The bound Kiosk provider outcome was read back.",
        Transitions = marker.Transitions.Select(transition => transition with
        {
            Message = transition.Stage switch
            {
                OperatorMutationStage.Sent => "The typed Kiosk operation entered execution.",
                OperatorMutationStage.Confirmed => noOpen ? NoOpMessage : "Matching typed Kiosk readback was confirmed.",
                _ => "The Kiosk operation retains its reported mutation stage."
            }
        }).ToArray()
    };

    private string NoOpMessage => request.Action == "kiosk.select"
        ? "Installation facts checked; selection was not performed because the Kiosk operator is unavailable."
        : "Installation facts checked; catalog activity was not requested because the Kiosk operator is unavailable.";

    private static BrowserKioskInstallation Installation(RustyKioskInstallationStatus value) => new(value.MainInstalled,
        Version(value.MainVersion), value.SetupHelperInstalled, Version(value.SetupHelperVersion), value.SetupHelperReady,
        value.SameSignerControlGranted, value.HostOperatorAvailable);
    private static string? Version(string? value) => value is null || value.Length > 128 || value.Any(char.IsControl) ? null : value;
    private static string? Package(string? value)
    {
        if (value is null || value.Length > 256) return null;
        try { return AndroidInput.RequirePackageName(value) == value ? value : null; }
        catch (ArgumentException) { return null; }
    }
    private static BrowserKioskResult Result(RustyKioskOperatorResult value)
    {
        var state = value.State;
        if (state.InstalledCount < 0 || state.NotInstalledCount < 0 || state.VisibleCount < 0)
            throw new InvalidDataException("Kiosk catalog counts are invalid.");
        var truncated = state.Entries.Count > MaximumEntries;
        string Label(string? input)
        {
            input ??= "";
            var safe = new string(input.Where(static character => !char.IsControl(character)).Take(MaximumLabelCharacters).ToArray());
            if (safe != input) truncated = true;
            return safe;
        }
        var occurrences = state.Entries.Where(row => BrowserKioskCommands.IsSelectableKey(row.Key))
            .GroupBy(row => row.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var rows = new List<BrowserKioskCatalogEntry>();
        foreach (var entry in state.Entries.Take(MaximumEntries))
        {
            if (!BrowserKioskCommands.IsSelectableKey(entry.Key)) { truncated = true; continue; }
            var package = Package(entry.PackageName);
            if (package != entry.PackageName) truncated = true;
            rows.Add(new(entry.Key, Label(entry.Name), package, entry.Installed, entry.Launchable,
                entry.Installed && entry.Launchable && package is not null && occurrences[entry.Key] == 1));
        }
        var selectedKey = state.SelectedKey;
        if (selectedKey is not null && !BrowserKioskCommands.IsSelectableKey(selectedKey)) { selectedKey = null; truncated = true; }
        var selectedPackage = Package(state.SelectedPackage);
        if (selectedPackage != state.SelectedPackage) truncated = true;
        return new(value.RequestId, value.Command.ToWireName(), value.Accepted, value.Completed, selectedKey,
            state.SelectedName is null ? null : Label(state.SelectedName), selectedPackage, state.SelectedInstalled, state.SelectedLaunchable,
            state.InstalledCount, state.NotInstalledCount, state.VisibleCount, state.VisibleEntriesTruncated, truncated, rows);
    }
}
