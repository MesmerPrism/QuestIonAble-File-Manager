namespace QuestIonAbleFileManager.Core;

public sealed partial class LocalApiCommandRegistry
{
    private void ValidateResourceNamespaces(IReadOnlyList<LocalApiJournalEntry> entries)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<LocalApiDiagnosticIdentity>();
        var root = Path.GetFullPath(Path.Combine(_settings.StateDirectory, "staged"));
        foreach (var entry in entries)
        {
            if (entry.OperationId is null || entry.OperationId.Length != 36 ||
                !entry.OperationId.StartsWith("api-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(entry.OperationId[4..], "N", out var id) || id == Guid.Empty || id.ToString("N") != entry.OperationId[4..] ||
                !string.Equals(Path.GetDirectoryName(Path.GetFullPath(entry.StagedPath)), root, StringComparison.OrdinalIgnoreCase) ||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(entry.StagedPath), "\\A[0-9a-f]{32}\\.apk\\z"))
                throw Input("journal_damaged", "A retained resource namespace is invalid.");
            var leaves = entry.Diagnostic is { } metadata ? metadata.Resources.Select(resource => resource.Leaf) : [Path.GetFileName(entry.StagedPath)];
            foreach (var leaf in leaves)
                if (!paths.Add(Path.Combine(root, leaf))) throw Input("journal_damaged", "Retained resource names overlap.");
            if (entry.Diagnostic is { } diagnostic)
                foreach (var resource in diagnostic.Resources)
                    if (resource.Identity is { } identity && !identities.Add(identity))
                        throw Input("journal_damaged", "Retained resource identities overlap.");
        }
    }

    private OperatorCommand ValidateV3JournalEntry(LocalApiJournalEntry entry)
    {
        var metadata = entry.Diagnostic ?? throw Input("journal_damaged", "Diagnostic resource metadata is missing.");
        LocalApiV3OperationRequest.RequireBinding(LocalApiV3Contract.Version, entry.OperationId, entry.CommandDigest);
        new LocalApiV3PreflightRequest(LocalApiV3Contract.Version, entry.CommandName, entry.StagedPath, entry.Serial!).Validate();
        var launch = entry.CommandName == "apk.launch-diagnose";
        if (launch)
        {
            if (metadata.OutputPolicy != LocalApiDiagnosticOutputIntent.LaunchPolicyVersion || metadata.LaunchMayHaveDispatched is null ||
                metadata.Summary is not null || (metadata.LaunchSummary is null) != (metadata.LaunchMutation is null))
                throw Input("journal_damaged", "The launch diagnostic policy binding is invalid.");
        }
        else if (metadata.OutputPolicy is not null || metadata.LaunchMayHaveDispatched is not null ||
            metadata.LaunchSummary is not null || metadata.LaunchMutation is not null)
            throw Input("journal_damaged", "Read-only diagnostic metadata cannot contain launch authority.");
        var snapshot = metadata.Snapshot(entry.OperationId);
        LocalApiDiagnosticOutputReservation.ValidateRecovery(snapshot);
        LocalApiJournal.EnsureV3FutureRowFits(entry);
        if (snapshot.Resources.Any(resource => resource.DebtCode is not (null or
            "diagnostic_create_cleanup_debt" or "diagnostic_owned_handle_delete_pending" or "diagnostic_witness_retained" or
            "diagnostic_cleanup_debt" or "diagnostic_identity_unknown" or "diagnostic_cleanup_persist_failed" or
            "diagnostic_missing_unexpected" or "diagnostic_witness_unavailable")))
            throw Input("journal_damaged", "A diagnostic resource reason is invalid.");
        var stagedRoot = Path.GetFullPath(Path.Combine(_settings.StateDirectory, "staged"));
        var input = snapshot.Resources.Single(resource => resource.Role == "input");
        var expectedInput = Path.Combine(stagedRoot, input.Leaf);
        if (!string.Equals(Path.GetFullPath(entry.StagedPath), expectedInput, StringComparison.OrdinalIgnoreCase) ||
            entry.InstallOptions is not null || entry.ResultEvidence is not null || entry.MutationEvidence is not null || entry.Error is not null ||
            !Enum.IsDefined(entry.Stage) || entry.Stage == LocalApiOperationStage.CleanupDebt ||
            entry.Artifact is null || entry.Artifact.SizeBytes is <= 0 or > LocalApiDiagnosticOutputIntent.MaximumInputArtifactBytes ||
            entry.Artifact.SizeBytes != input.MaximumBytes ||
            entry.Artifact.Sha256 is not { Length: 64 } || !entry.Artifact.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            entry.Artifact.Identity is null || !string.IsNullOrWhiteSpace(entry.Artifact.Identity.SplitName) ||
            metadata.PreparationDeadline != metadata.PreparationStartedAt.AddSeconds(V3PreparationSeconds) ||
            entry.ExpiresAt <= metadata.PreparationStartedAt || entry.ExpiresAt > metadata.PreparationDeadline ||
            (metadata.Ready && entry.Artifact.Path != entry.StagedPath) ||
            (!metadata.Ready && entry.Stage is not (LocalApiOperationStage.Preparing or LocalApiOperationStage.OutcomeUnknownRecoveryRequired)) ||
            (entry.Stage == LocalApiOperationStage.Preparing && (entry.Consumed || entry.Dispatched || metadata.OutputStarted)) ||
            (entry.Stage == LocalApiOperationStage.Preflighted && (entry.Consumed || entry.Dispatched || metadata.OutputStarted)) ||
            (metadata.LaunchMayHaveDispatched == true && (!entry.Consumed || !metadata.Ready || !metadata.OutputStarted ||
                !entry.Dispatched || entry.Stage is not (LocalApiOperationStage.Running or LocalApiOperationStage.CancellationRequested or
                    LocalApiOperationStage.Completed or LocalApiOperationStage.Failed or LocalApiOperationStage.OutcomeUnknownRecoveryRequired))) ||
            (metadata.OutputStarted && !entry.Consumed) ||
            (entry.Dispatched && !entry.Consumed) ||
            (!metadata.Ready && (entry.Consumed || metadata.OutputStarted)) ||
            (metadata.Ready && (input.Identity is null || input.State is not ("owned" or "sealed" or "deletePending" or "removed") ||
                input.SizeBytes != entry.Artifact.SizeBytes || input.Sha256 is not null && input.Sha256 != entry.Artifact.Sha256)) ||
            (!metadata.OutputStarted && snapshot.Resources.Any(resource => resource.Role != "input" && resource.State != "planned")) ||
            (!metadata.OutputStarted && snapshot.Phase != "reserved" && !(metadata.CleanupStarted && snapshot.Phase == "removed")) ||
            (entry.Stage is LocalApiOperationStage.Running or LocalApiOperationStage.CancellationRequested && !entry.Consumed) ||
            (entry.Stage == LocalApiOperationStage.Completed && (!entry.Consumed || !metadata.OutputStarted || metadata.Archive is null ||
                (launch ? metadata.LaunchSummary is null : metadata.Summary is null))) ||
            (launch && metadata.LaunchMayHaveDispatched == true && entry.Stage == LocalApiOperationStage.Failed && metadata.LaunchSummary is null) ||
            (metadata.Archive is not null && (launch
                ? entry.Stage is not (LocalApiOperationStage.Completed or LocalApiOperationStage.Failed or LocalApiOperationStage.OutcomeUnknownRecoveryRequired) ||
                    metadata.LaunchSummary is null || metadata.LaunchMutation is null
                : entry.Stage != LocalApiOperationStage.Completed)))
            throw Input("journal_damaged", "The diagnostic lifecycle binding is invalid.");
        _ = LocalApiV3ApkFacts.FromArtifact(entry.Artifact);
        if (entry.Artifact.Identity.VersionCode <= 0 || entry.Artifact.Identity.SignerSha256 is not { Length: 64 } signer ||
            !signer.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw Input("journal_damaged", "The diagnostic APK facts are invalid.");
        if (metadata.Archive is { } descriptor)
        {
            descriptor.Validate();
            if (launch)
            {
                LocalApiV3LaunchProof.Validate(metadata.LaunchSummary!, metadata.LaunchMutation!, entry.Serial!,
                    metadata.LaunchMayHaveDispatched == true, entry.Stage);
                // An archive cannot promote custody-loss downgrades of a known
                // terminal launch proof into a newly available publication.
                if (entry.Stage == LocalApiOperationStage.OutcomeUnknownRecoveryRequired &&
                    metadata.LaunchSummary!.Disposition is not ("launchPending" or "outcomeUnknown"))
                    throw Input("journal_damaged", "The launch archive outcome binding is invalid.");
            }
            else metadata.Summary!.Validate();
            var archive = snapshot.Resources.Single(resource => resource.Role == "archive");
            if (descriptor.OutputId != metadata.OutputId || descriptor.SizeBytes != archive.SizeBytes || descriptor.Sha256 != archive.Sha256 ||
                (!metadata.CleanupStarted && (snapshot.Phase != "sealed" || archive.State != "sealed")) || descriptor.PublishedAt != entry.TerminalAt)
                throw Input("journal_damaged", "The diagnostic archive publication binding is invalid.");
        }
        if (metadata.Summary is not null && metadata.Archive is null)
            throw Input("journal_damaged", "An unpublished diagnostic summary cannot grant availability.");
        if (metadata.LaunchSummary is not null)
            LocalApiV3LaunchProof.Validate(metadata.LaunchSummary, metadata.LaunchMutation!, entry.Serial!,
                metadata.LaunchMayHaveDispatched == true, entry.Stage);
        var command = BuildV3Command(launch, entry.Serial!, entry.StagedPath, Path.Combine(stagedRoot, "d-" + metadata.OutputId[5..]));
        if (!FixedDigestEquals(entry.CommandDigest, LocalApiV3Contract.ComputeV3CommandDigest(command, entry.Artifact, snapshot.Intent)))
            throw Input("journal_damaged", "The diagnostic command digest does not match.");
        return command;
    }

    private void RestoreV3Entries(IReadOnlyList<LocalApiJournalEntry> entries, IReadOnlyDictionary<string, OperatorCommand> commands)
    {
        var rows = entries.Where(entry => entry.OperationVersion == LocalApiV3Contract.Version).ToArray();
        _journal.EnsureV3Capacity(entries);
        foreach (var row in rows)
        {
            var operation = new DiagnosticOperation(row);
            if (row.Stage is LocalApiOperationStage.Preparing or LocalApiOperationStage.Running or LocalApiOperationStage.CancellationRequested)
                MarkV3UnknownLocked(operation, "diagnostic_restart_recovery_required");
            else if (row.Stage == LocalApiOperationStage.Preflighted &&
                (!row.Diagnostic!.Ready || row.Consumed || row.Diagnostic.OutputStarted || row.Diagnostic.LaunchMayHaveDispatched == true || row.Diagnostic.CleanupStarted ||
                    row.Diagnostic.ResourcePhase != "reserved" || row.Diagnostic.Resources.Any(resource => resource.DebtCode is not null ||
                        (resource.Role != "input" && resource.State != "planned"))))
                throw Input("journal_damaged", "A never-started diagnostic approval contains output allocation.");
            _operations.Add(row.OperationId, new RetainedOperation(row.OperationId, row.CommandName, row.CommandDigest,
                row.ExpiresAt, commands[row.OperationId], row.Artifact, null,
                contractVersion: LocalApiV3Contract.Version, diagnostic: operation));
        }
        if (rows.Length == 0) return; // preserve existing v1/v2 platform behavior
        var reservations = _stager.RegisterDiagnosticRecoveryAsync(rows.Select(row => row.Diagnostic!.Snapshot(row.OperationId)).ToArray(),
            (snapshot, _) => PersistSnapshot(_operations[snapshot.Intent.OperationId].Diagnostic!, snapshot)).GetAwaiter().GetResult();
        foreach (var reservation in reservations) _operations[reservation.Intent.OperationId].Diagnostic!.Reservation = reservation;
    }

    // Only known terminal records are eligible. UNKNOWN/preparing debt is never
    // selected by age or by a cleanup-debt flag; explicit destruction is deferred.
    private async Task SweepV3Async()
    {
        DiagnosticOperation[] candidates;
        lock (_operationsGate)
        {
            candidates = _operations.Values.Select(item => item.Diagnostic).OfType<DiagnosticOperation>()
                .Where(item => !item.Active && !item.Cleaning && item.ReadLeases == 0 && !item.PersistenceUncertain && item.Reservation is not null &&
                    V3RetentionElapsed(item.Row) && item.Row.Stage is LocalApiOperationStage.Completed or LocalApiOperationStage.Failed or LocalApiOperationStage.Cancelled or LocalApiOperationStage.Expired)
                .ToArray();
            foreach (var item in candidates) item.Cleaning = true;
        }
        foreach (var item in candidates)
        {
            try
            {
                lock (_operationsGate)
                    PersistDiagnosticLocked(item, item.Row with { Diagnostic = item.Metadata with { CleanupStarted = true } });
                if (!await item.Reservation!.CleanupAsync(CancellationToken.None).ConfigureAwait(false)) continue;
                if (!await item.Reservation.CleanupInputAsync(item.Staged, (input, _) =>
                {
                    lock (_operationsGate)
                        PersistDiagnosticLocked(item, item.Row with { Diagnostic = item.Metadata with { Resources = Array.AsReadOnly(
                            item.Metadata.Resources.Select(resource => resource.Role == "input" ? input : resource).ToArray()) } });
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false)) continue;
                lock (_operationsGate)
                {
                    var retained = _operations[item.Row.OperationId];
                    _operations.Remove(item.Row.OperationId);
                    try { PersistLocked(); }
                    catch { _operations.Add(item.Row.OperationId, retained); item.PersistenceUncertain = true; throw; }
                    retained.ReleaseHandles();
                }
            }
            catch
            {
                // Exact tombstones and all remaining resources stay in the same
                // journal; a failed sweep never turns UNKNOWN into deletion authority.
            }
            finally { lock (_operationsGate) item.Cleaning = false; }
        }
    }
}
