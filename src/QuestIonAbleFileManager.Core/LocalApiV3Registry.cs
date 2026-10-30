namespace QuestIonAbleFileManager.Core;

/// <summary>A read-only lease over one durably published diagnostic archive.</summary>
public sealed class LocalApiDiagnosticOutputLease : IDisposable
{
    private LocalApiDiagnosticCandidateReadLease? _lease;
    private Action? _release;
    internal LocalApiDiagnosticOutputLease(LocalApiDiagnosticCandidateReadLease lease, LocalApiV3ArchiveDescriptor descriptor, Action release)
    { _lease = lease; Descriptor = descriptor; _release = release; }
    public LocalApiV3ArchiveDescriptor Descriptor { get; }
    public Stream Stream => _lease?.Stream ?? throw new ObjectDisposedException(nameof(LocalApiDiagnosticOutputLease));
    public void Dispose()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease is null) return;
        try { lease.Dispose(); }
        finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
    }
}

public sealed partial class LocalApiCommandRegistry
{
    private int _reservedV3Rows, _reservedDiagnosticFiles;
    private bool _registryClosing;
    private readonly HashSet<Task> _v3Tasks = [];
    private const int V3PreparationSeconds = 300;

    private sealed class DiagnosticOperation(LocalApiJournalEntry row)
    {
        internal LocalApiJournalEntry Row = row;
        internal LocalApiDiagnosticOutputReservation? Reservation;
        internal LocalApiStagedArtifact? Staged;
        internal readonly CancellationTokenSource Cancellation = new();
        internal bool Active, PersistenceUncertain, Cleaning;
        internal int ReadLeases;
        internal LocalApiV3JournalState Metadata => Row.Diagnostic!;
    }

    private void EnsureRegistryOpenLocked()
    { if (_registryClosing) throw Input("owner_stopping", "The retained owner is stopping."); }

    public LocalApiV3Capabilities GetV3Capabilities() => new(
        LocalApiV3Contract.Version, ["apk.diagnose", "apk.launch-diagnose"], LocalApiContract.MaximumRequestBytes,
        (int)LocalApiContract.DefaultPreflightLifetime.TotalSeconds, LocalApiV3OutputLimits.Current);

    public Task<LocalApiV3PreflightResult> PreflightV3Async(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        var request = ParseBound<LocalApiV3PreflightRequest>(body); request.Validate();
        if (request.Command == "apk.launch-diagnose" && !_client.SupportsOwnedLaunchDiagnostics)
            throw Input("diagnostic_runner_unsupported", "The retained launch diagnostic requires an armed bounded runner.");
        lock (_operationsGate)
        {
            EnsureRegistryOpenLocked();
            if (CapacityExhausted(_operations.Count, _reservedPreflights, _settings.Limits.MaximumRetainedOperations))
                throw Input("operation_capacity", "The retained-operation capacity is exhausted.");
            _journal.EnsureV3Capacity(_operations.Values.Select(item => item.ToJournal()).ToArray(), _reservedV3Rows + 1);
            _reservedV3Rows++; _reservedPreflights++;
            var completion = new TaskCompletionSource<LocalApiV3PreflightResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _v3Tasks.Add(completion.Task);
            _ = PrepareV3JoinedAsync(request, cancellationToken, completion);
            return completion.Task;
        }
    }

    private async Task PrepareV3JoinedAsync(LocalApiV3PreflightRequest request, CancellationToken cancellation,
        TaskCompletionSource<LocalApiV3PreflightResult> completion)
    {
        // Dispatch only after releasing the registry monitor; all work remains joined by completion.
        await Task.Yield();
        DiagnosticOperation? operation = null;
        var reservationOutstanding = true;
        long budget = 0;
        var fileBudget = 0;
        LocalApiV3PreflightResult? prepared = null;
        Exception? failure = null;
        try
        {
            await SweepV3Async().ConfigureAwait(false);
            using var lifetime = _stager.AcquireDiagnosticOperation();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(V3PreparationSeconds));
            var token = deadline.Token;
            using var source = LocalApiArtifactStager.BorrowDiagnosticSource(request.ApkPath);
            var ownedClient = _client.CreateOwnedDiagnosticClient();
            var artifact = await ownedClient.CreateApkInspector().InspectAsync(source.Path, token).ConfigureAwait(false);
            source.Verify();
            if (artifact.SizeBytes != source.SizeBytes || artifact.Sha256 != await source.HashAsync(token).ConfigureAwait(false) ||
                !string.IsNullOrWhiteSpace(artifact.Identity.SplitName))
                throw Input("artifact_changed", "The inspected base APK could not be bound to the retained source.");
            var id = "api-" + Guid.NewGuid().ToString("N");
            var launch = request.Command == "apk.launch-diagnose";
            var intent = launch ? LocalApiDiagnosticOutputIntent.CreateForLaunch(id) : LocalApiDiagnosticOutputIntent.Create(id);
            lock (_operationsGate)
            {
                var inventory = _stager.GetInventory();
                if (source.SizeBytes > _settings.Limits.EffectiveMaximumSingleArtifactBytes ||
                    inventory.SizeBytes + _reservedStageBytes + source.SizeBytes + LocalApiDiagnosticOutputIntent.ReservedOutputBytes > _settings.Limits.MaximumStagedBytes)
                    throw Input("staged_byte_capacity", "The staged-byte capacity is exhausted.");
                var pendingLegacyFiles = _reservedPreflights - _reservedV3Rows;
                if (inventory.FileCount + _reservedDiagnosticFiles + pendingLegacyFiles + 16 > _settings.Limits.MaximumStagedFiles)
                    throw Input("staged_file_capacity", "The staged-file capacity is exhausted.");
                budget = source.SizeBytes + LocalApiDiagnosticOutputIntent.ReservedOutputBytes;
                _reservedStageBytes += budget; fileBudget = 16; _reservedDiagnosticFiles += fileBudget;
            }
            async Task PersistInput(LocalApiDiagnosticResourceSnapshot input, CancellationToken ignored)
            {
                lock (_operationsGate)
                {
                    if (operation is null)
                    {
                        var now = _timeProvider.GetUtcNow();
                        var stagedPath = Path.Combine(_settings.StateDirectory, "staged", input.Leaf);
                        var outputPath = Path.Combine(_settings.StateDirectory, "staged", "d-" + intent.OutputId[5..]);
                        var command = BuildV3Command(launch, request.Serial, stagedPath, outputPath);
                        var digest = LocalApiV3Contract.ComputeV3CommandDigest(command, artifact, intent);
                        var metadata = new LocalApiV3JournalState(intent.OutputId, intent.PlannedLeafDigest, "reserved",
                            PlanResources(intent, input), now, now.AddSeconds(V3PreparationSeconds))
                        { OutputPolicy = launch ? LocalApiDiagnosticOutputIntent.LaunchPolicyVersion : null,
                            LaunchMayHaveDispatched = launch ? false : null };
                        var row = new LocalApiJournalEntry(id, request.Command, digest,
                            now.AddSeconds(request.ExpiresInSeconds ?? 120), stagedPath, artifact, request.Serial, null,
                            LocalApiOperationStage.Preparing, false, false, null, null, null, null, null,
                            LocalApiV3Contract.Version, metadata);
                        LocalApiJournal.EnsureV3FutureRowFits(row);
                        _journal.EnsureV3Capacity(_operations.Values.Select(item => item.ToJournal()).Append(row).ToArray(), _reservedV3Rows - 1);
                        operation = new(row) { Active = true };
                        _operations.Add(id, new RetainedOperation(id, row.CommandName, digest, row.ExpiresAt, command, artifact, null,
                            contractVersion: LocalApiV3Contract.Version, diagnostic: operation));
                        _reservedV3Rows--; _reservedPreflights--; reservationOutstanding = false;
                        try { PersistLocked(); }
                        catch
                        {
                            operation.PersistenceUncertain = true;
                            throw;
                        }
                        // StageCore already accounts this pending input before calling
                        // us. Transfer its byte/slot promise without counting it twice.
                        _reservedStageBytes -= source.SizeBytes; budget -= source.SizeBytes;
                        _reservedDiagnosticFiles--; fileBudget--;
                    }
                    else PersistDiagnosticLocked(operation, operation.Row with { Diagnostic = operation.Metadata with
                    { Resources = Array.AsReadOnly(operation.Metadata.Resources.Select(resource => resource.Role == "input" ? input : resource).ToArray()) } });
                }
                await Task.CompletedTask;
            }
            var staged = await _stager.StageDiagnosticInputAsync(source, PersistInput, token).ConfigureAwait(false);
            operation!.Staged = staged;
            var inspected = await ownedClient.CreateApkInspector().InspectAsync(staged.Path, token).ConfigureAwait(false);
            if (!SameApkFacts(artifact, inspected)) throw Input("artifact_changed", "The retained APK inspection changed.");
            var transferBytes = budget; var transferFiles = fileBudget;
            try
            {
                operation.Reservation = await _stager.ReserveDiagnosticOutputAsync(intent, staged, (snapshot, _) =>
                {
                    lock (_operationsGate)
                    {
                        _reservedStageBytes -= budget; budget = 0;
                        _reservedDiagnosticFiles -= fileBudget; fileBudget = 0;
                        PersistDiagnosticLocked(operation, operation.Row with { Diagnostic = operation.Metadata.WithSnapshot(snapshot) });
                    }
                    return Task.CompletedTask;
                }, token).ConfigureAwait(false);
            }
            catch
            {
                lock (_operationsGate)
                    if (budget == 0) { budget = transferBytes; fileBudget = transferFiles; _reservedStageBytes += budget; _reservedDiagnosticFiles += fileBudget; }
                throw;
            }
            var target = await RequireV3TargetAsync(ownedClient, request.Serial, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_operationsGate)
            {
                if (_timeProvider.GetUtcNow() >= operation.Row.ExpiresAt)
                    throw Input("operation_expired", "The preparing operation expired.");
                PersistDiagnosticLocked(operation, operation.Row with { Artifact = inspected,
                    Stage = LocalApiOperationStage.Preflighted, Diagnostic = operation.Metadata with { Ready = true } });
                prepared = new(LocalApiV3Contract.Version, id, request.Command, operation.Row.CommandDigest,
                    operation.Row.ExpiresAt, LocalApiV3ApkFacts.FromArtifact(inspected), target.Serial, LocalApiV3OutputIntent.FromIntent(intent));
            }
        }
        catch (Exception exception)
        {
            var acknowledged = false;
            lock (_operationsGate)
            {
                if (operation is not null)
                {
                    // Preparation may have created owned bytes even though no headset capture began.
                    // Preserve its durable reference/capacity; expiry is never deletion authority.
                    var inputLeaf = operation.Metadata.Resources.Single(resource => resource.Role == "input").Leaf;
                    var creationDebt = _stager.DiagnosticCreationDebts.SingleOrDefault(resource => resource.Leaf == inputLeaf);
                    if (creationDebt is not null) operation.Row = operation.Row with { Diagnostic = operation.Metadata with { Resources = Array.AsReadOnly(
                        operation.Metadata.Resources.Select(resource => resource.Role == "input" ? creationDebt : resource).ToArray()) } };
                    MarkV3UnknownLocked(operation, "diagnostic_preparation_incomplete");
                    try { PersistLocked(); acknowledged = true; } catch { operation.PersistenceUncertain = true; }
                }
            }
            if (operation is not null && operation.Reservation is null && acknowledged)
            {
                try
                {
                    operation.Reservation = await _stager.RetainDiagnosticRecoveryAsync(operation.Metadata.Snapshot(operation.Row.OperationId),
                        (snapshot, _) => PersistSnapshot(operation, snapshot)).ConfigureAwait(false);
                    lock (_operationsGate)
                    { _reservedStageBytes -= budget; budget = 0; _reservedDiagnosticFiles -= fileBudget; fileBudget = 0; }
                }
                catch { /* exact quota promises remain charged in the registry until restart */ }
            }
            failure = SafeV3Failure(exception, "diagnostic_preparation_failed");
        }
        finally
        {
            lock (_operationsGate)
            {
                if (operation is not null) operation.Active = false;
                if (reservationOutstanding) { _reservedV3Rows--; _reservedPreflights--; }
                // If allocation started without an output reservation, keep missing-resource
                // quota charged until authenticated restart can reconstruct the exact plan.
                if (operation is null || operation.Reservation is not null)
                { _reservedStageBytes -= budget; _reservedDiagnosticFiles -= fileBudget; }
                if (failure is not null) completion.TrySetException(failure);
                else completion.TrySetResult(prepared!);
                _v3Tasks.Remove(completion.Task);
            }
        }
    }

    private static IReadOnlyList<LocalApiDiagnosticResourceSnapshot> PlanResources(LocalApiDiagnosticOutputIntent intent, LocalApiDiagnosticResourceSnapshot input) =>
        Array.AsReadOnly(new[] { input }.Concat(intent.PayloadLeaves.Select((leaf, slot) =>
            new LocalApiDiagnosticResourceSnapshot("payload", slot, leaf, LocalApiDiagnosticOutputIntent.MaximumPayloadFileBytes, "planned")))
            .Append(new("archive", 14, intent.ArchiveLeaf, LocalApiDiagnosticOutputIntent.MaximumArchiveBytes, "planned")).ToArray());

    private Task PersistSnapshot(DiagnosticOperation operation, LocalApiDiagnosticOutputSnapshot snapshot)
    {
        lock (_operationsGate)
            PersistDiagnosticLocked(operation, operation.Row with { Diagnostic = operation.Metadata.WithSnapshot(snapshot) });
        return Task.CompletedTask;
    }

    private void PersistDiagnosticLocked(DiagnosticOperation operation, LocalApiJournalEntry row)
    {
        var previous = operation.Row;
        operation.Row = row;
        try { PersistLocked(); }
        catch { operation.Row = previous; operation.PersistenceUncertain = true; throw; }
    }

    private static bool SameApkFacts(ApkArtifactInspection left, ApkArtifactInspection right) =>
        left.SizeBytes == right.SizeBytes && left.Sha256 == right.Sha256 && left.Identity == right.Identity;

    private static OperatorCommand BuildV3Command(bool launch, string serial, string input, string output) => launch
        ? OperatorCommands.LaunchDiagnoseInspectedApp(serial, input, output)
        : OperatorCommands.DiagnoseInspectedApp(serial, input, output);

    private static async Task<QuestDevice> RequireV3TargetAsync(AdbClient client, string serial, CancellationToken token)
    {
        var matches = (await client.GetDevicesAsync(token).ConfigureAwait(false)).Where(device => device.Serial == serial).ToArray();
        if (matches.Length != 1 || !matches[0].IsReady) throw Input("target_not_ready", "The exact selected serial is not uniquely ready.");
        return matches[0];
    }

    public Task<LocalApiV3OperationStatus> ExecuteV3Async(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        var request = ParseBound<LocalApiV3ExecuteRequest>(body); request.Validate();
        lock (_operationsGate)
        {
            EnsureRegistryOpenLocked();
            var operation = RequireV3Locked(request.OperationId, request.CommandDigest);
            ExpireV3Locked(operation, _timeProvider.GetUtcNow());
            if (operation.Row.Stage == LocalApiOperationStage.Preparing)
                throw Input("operation_preparing", "Preparation is not yet durably complete.");
            if (operation.Row.Stage == LocalApiOperationStage.Expired) throw Input("operation_expired", "The retained command has expired.");
            if (operation.Active || operation.Row.Consumed || operation.Row.Stage != LocalApiOperationStage.Preflighted || !operation.Metadata.Ready || operation.PersistenceUncertain)
                throw Input("operation_consumed", "The retained command is unavailable or already consumed.");
            if (_operations.Values.Count(item => item.IsRunning) >= _settings.Limits.MaximumRunningOperations)
                throw Input("running_capacity", "The running-operation capacity is exhausted.");
            operation.Active = true;
            var completion = new TaskCompletionSource<LocalApiV3OperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            _v3Tasks.Add(completion.Task);
            _ = RunV3JoinedAsync(operation, completion);
            return completion.Task;
        }
    }

    private async Task RunV3JoinedAsync(DiagnosticOperation operation, TaskCompletionSource<LocalApiV3OperationStatus> completion)
    {
        await Task.Yield();
        try
        {
            using var lifetime = _stager.AcquireDiagnosticOperation();
            var token = operation.Cancellation.Token;
            var ownedClient = _client.CreateOwnedDiagnosticClient();
            if (operation.Staged is null)
            {
                var metadata = operation.Metadata;
                var recorded = metadata.Resources.Single(resource => resource.Role == "input");
                var staged = _stager.Reopen(operation.Row.StagedPath);
                try
                {
                    if (staged.DiagnosticIdentity != recorded.Identity || staged.SizeBytes != operation.Row.Artifact.SizeBytes)
                        throw Input("diagnostic_input_changed", "The recorded input identity changed.");
                    var activation = staged.BorrowDiagnosticInput();
                    try
                    {
                        await operation.Reservation!.ActivateNeverStartedAsync(activation, operation.Row.Artifact.Sha256,
                            operation.Row.ExpiresAt, _timeProvider.GetUtcNow(), token).ConfigureAwait(false);
                    }
                    catch { activation.Dispose(); throw; }
                    operation.Staged = staged;
                }
                catch { staged.Dispose(); throw; }
            }
            lock (_operationsGate)
            {
                if (_timeProvider.GetUtcNow() >= operation.Row.ExpiresAt)
                    throw Input("operation_expired", "The retained command has expired.");
                PersistDiagnosticLocked(operation, operation.Row with { Stage = LocalApiOperationStage.Running, Consumed = true });
            }
            using var input = operation.Staged!.BorrowDiagnosticInput();
            var current = await ownedClient.CreateApkInspector().InspectAsync(input.Path, token).ConfigureAwait(false);
            if (current != operation.Row.Artifact) throw Input("artifact_changed", "The retained artifact changed.");
            await RequireV3TargetAsync(ownedClient, operation.Row.Serial!, token).ConfigureAwait(false);
            lock (_operationsGate)
                PersistDiagnosticLocked(operation, operation.Row with { Dispatched = true,
                    Diagnostic = operation.Metadata with { OutputStarted = true } });
            OperatorCommand command;
            lock (_operationsGate) command = _operations[operation.Row.OperationId].Command;
            var launch = operation.Row.CommandName == "apk.launch-diagnose";
            if (launch)
                lock (_operationsGate)
                    // Both journal and anchor must acknowledge this conservative
                    // intent before Core can arm capture or observe actual dispatch.
                    PersistDiagnosticLocked(operation, operation.Row with
                    { Diagnostic = operation.Metadata with { LaunchMayHaveDispatched = true } });
            var result = await ownedClient.ExecuteOwnedDiagnosticAsync(command, input, operation.Reservation!.CreateSink(), token).ConfigureAwait(false);
            if (result.Command.Kind != command.Kind || result.Command.Serial != command.Serial ||
                result.Command.LocalPath != command.LocalPath || result.Command.OutputPath != command.OutputPath ||
                result.Command.InstallOptions != command.InstallOptions || !result.Command.CliArguments.SequenceEqual(command.CliArguments, StringComparer.Ordinal))
                throw Input("diagnostic_result_invalid", "The owned diagnostic command binding changed.");
            LocalApiDiagnosticSealedOutput candidate;
            LocalApiV3DiagnosticSummary? summary = null;
            LocalApiV3LaunchSummary? launchSummary = null;
            OperatorMutationReceipt? launchMutation = null;
            var stage = LocalApiOperationStage.Completed;
            if (launch)
            {
                var diagnostic = result.ApkLaunchDiagnosticBundleResult ?? throw Input("diagnostic_result_invalid", "No launch diagnostic result was returned.");
                if (!SameApkFacts(operation.Row.Artifact, diagnostic.Artifact))
                    throw Input("diagnostic_result_invalid", "The launch diagnostic facts changed.");
                (launchSummary, launchMutation, stage) = LocalApiV3LaunchProof.Project(result, operation.Row.Serial!,
                    operation.Metadata.LaunchMayHaveDispatched == true);
                candidate = await operation.Reservation.SealAsync(diagnostic, token).ConfigureAwait(false);
            }
            else
            {
                var diagnostic = result.ApkDiagnosticBundleResult ?? throw Input("diagnostic_result_invalid", "No diagnostic result was returned.");
                candidate = await operation.Reservation.SealAsync(diagnostic, token).ConfigureAwait(false);
                summary = LocalApiV3DiagnosticSummary.FromResult(diagnostic);
            }
            var descriptor = new LocalApiV3ArchiveDescriptor(candidate.OutputId, "apk-diagnostics.zip", candidate.SizeBytes, candidate.Sha256, _timeProvider.GetUtcNow());
            lock (_operationsGate)
            {
                // Save+anchor acknowledgement is the sole publication point. Status cannot
                // see this staged row because it shares the registry monitor.
                var terminal = operation.Row with { Stage = stage, TerminalAt = descriptor.PublishedAt,
                    Diagnostic = operation.Metadata with { Archive = descriptor, Summary = summary,
                        LaunchSummary = launchSummary, LaunchMutation = launchMutation },
                    ErrorCode = stage == LocalApiOperationStage.Completed ? null : stage == LocalApiOperationStage.Failed
                        ? "diagnostic_execution_failed" : "diagnostic_execution_unconfirmed", Error = null };
                PersistDiagnosticLocked(operation, terminal);
            }
        }
        catch (Exception exception)
        {
            var cancellationProved = exception is OperationCanceledException && operation.Cancellation.IsCancellationRequested &&
                await TryCleanCancelledV3Async(operation).ConfigureAwait(false);
            lock (_operationsGate)
            {
                var started = operation.Metadata.OutputStarted;
                if (cancellationProved && operation.Metadata.LaunchMayHaveDispatched != true) operation.Row = operation.Row with { Stage = LocalApiOperationStage.Cancelled,
                    TerminalAt = _timeProvider.GetUtcNow(), ErrorCode = "diagnostic_cancelled", Error = null };
                else if (started || operation.PersistenceUncertain) MarkV3UnknownLocked(operation, exception is OperationCanceledException ? "diagnostic_cancellation_unconfirmed" : "diagnostic_execution_unconfirmed");
                else operation.Row = operation.Row with { Stage = exception is OperationCanceledException ? LocalApiOperationStage.Cancelled : LocalApiOperationStage.Failed,
                    TerminalAt = _timeProvider.GetUtcNow(), ErrorCode = exception is OperationCanceledException ? "diagnostic_cancelled" : "diagnostic_execution_failed", Error = null };
                try { PersistLocked(); } catch { MarkV3UnknownLocked(operation, "journal_persist_failed"); operation.PersistenceUncertain = true; }
            }
        }
        finally
        {
            lock (_operationsGate)
            {
                operation.Active = false;
                completion.TrySetResult(V3StatusLocked(operation));
                _v3Tasks.Remove(completion.Task);
            }
        }
    }

    private async Task<bool> TryCleanCancelledV3Async(DiagnosticOperation operation)
    {
        if (operation.Reservation is null || operation.PersistenceUncertain) return false;
        try
        {
            lock (_operationsGate)
                PersistDiagnosticLocked(operation, operation.Row with { Diagnostic = operation.Metadata with { CleanupStarted = true } });
            if (!await operation.Reservation.CleanupAsync(CancellationToken.None).ConfigureAwait(false)) return false;
            return await operation.Reservation.CleanupInputAsync(operation.Staged, (input, _) =>
            {
                lock (_operationsGate)
                    PersistDiagnosticLocked(operation, operation.Row with { Diagnostic = operation.Metadata with { Resources = Array.AsReadOnly(
                        operation.Metadata.Resources.Select(resource => resource.Role == "input" ? input : resource).ToArray()) } });
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch { return false; }
    }

    private DiagnosticOperation RequireV3Locked(string id, string digest)
    {
        if (!_operations.TryGetValue(id, out var retained)) throw Input("operation_unknown", "The operation identifier is unknown.");
        RequireOperationVersion(retained, LocalApiV3Contract.Version);
        if (!FixedDigestEquals(retained.CommandDigest, digest)) throw Input("digest_mismatch", "The retained command digest does not match.");
        return retained.Diagnostic!;
    }

    public LocalApiV3OperationStatus GetV3Status(ReadOnlyMemory<byte> body)
    {
        var request = ParseBound<LocalApiV3OperationRequest>(body); request.Validate();
        lock (_operationsGate)
        {
            var operation = RequireV3Locked(request.OperationId, request.CommandDigest);
            var previous = operation.Row;
            ExpireV3Locked(operation, _timeProvider.GetUtcNow());
            if (previous != operation.Row) PersistDiagnosticLocked(operation, operation.Row);
            return V3StatusLocked(operation);
        }
    }

    public LocalApiV3OperationStatus CancelV3(ReadOnlyMemory<byte> body)
    {
        var request = ParseBound<LocalApiV3OperationRequest>(body); request.Validate();
        DiagnosticOperation operation;
        lock (_operationsGate)
        {
            operation = RequireV3Locked(request.OperationId, request.CommandDigest);
            if (operation.Row.Stage is LocalApiOperationStage.Preparing or LocalApiOperationStage.Preflighted)
                throw Input("operation_not_running", "An unexecuted operation cannot be cancelled.");
            if (operation.Row.Stage == LocalApiOperationStage.Running)
                PersistDiagnosticLocked(operation, operation.Row with { Stage = LocalApiOperationStage.CancellationRequested });
        }
        operation.Cancellation.Cancel();
        lock (_operationsGate) return V3StatusLocked(operation);
    }

    private void MarkV3UnknownLocked(DiagnosticOperation operation, string code) => operation.Row = operation.Row with
    { Stage = LocalApiOperationStage.OutcomeUnknownRecoveryRequired, TerminalAt = _timeProvider.GetUtcNow(), ErrorCode = code, Error = null,
        Diagnostic = operation.Metadata with { Archive = null, Summary = null } };

    private static void ExpireV3Locked(DiagnosticOperation operation, DateTimeOffset now)
    {
        if (operation.Row.Stage == LocalApiOperationStage.Preflighted && now >= operation.Row.ExpiresAt)
            operation.Row = operation.Row with { Stage = LocalApiOperationStage.Expired, Consumed = true, TerminalAt = now };
    }

    private bool V3RetentionElapsed(LocalApiJournalEntry row) => row.TerminalAt is { } terminal &&
        _timeProvider.GetUtcNow() - terminal >= _settings.Limits.EffectiveTerminalRetention;

    private LocalApiV3OperationStatus V3StatusLocked(DiagnosticOperation operation)
    {
        var row = operation.Row; var metadata = operation.Metadata;
        var intent = metadata.Snapshot(row.OperationId).Intent;
        var launch = row.CommandName == "apk.launch-diagnose";
        var expired = (row.Stage == LocalApiOperationStage.Completed || launch && row.Stage == LocalApiOperationStage.Failed) && V3RetentionElapsed(row);
        var code = expired ? "operation_expired" : V3PublicCode(row.ErrorCode);
        var publishableStage = launch ?
            row.Stage is LocalApiOperationStage.Completed or LocalApiOperationStage.Failed or LocalApiOperationStage.OutcomeUnknownRecoveryRequired &&
                metadata.LaunchSummary is not null && metadata.LaunchMutation is not null &&
                (row.Stage != LocalApiOperationStage.OutcomeUnknownRecoveryRequired || metadata.LaunchSummary.Disposition is "launchPending" or "outcomeUnknown")
            : row.Stage == LocalApiOperationStage.Completed;
        var available = publishableStage && metadata.Archive is not null &&
            !expired && !metadata.CleanupStarted && !operation.PersistenceUncertain;
        var phase = available ? "available" : expired ? "unavailable" : metadata.CleanupStarted ? "cleanupPending" : row.Stage switch
        {
            LocalApiOperationStage.Preparing => "preparing", LocalApiOperationStage.Preflighted => "preflighted",
            LocalApiOperationStage.Running or LocalApiOperationStage.CancellationRequested => "capturing", _ => "unavailable"
        };
        return new(LocalApiV3Contract.Version, row.OperationId, row.CommandName, row.CommandDigest, row.ExpiresAt, row.Stage, row.Consumed, row.Serial!,
            new(LocalApiV3OutputIntent.FromIntent(intent), phase, available, metadata.Resources.Count(resource => resource.State is not ("planned" or "removed")),
                metadata.Resources.Count(resource => resource.DebtCode is not null || resource.State == "identityUnknown"), code, available ? metadata.Archive : null),
            row.Stage == LocalApiOperationStage.Completed ? metadata.Summary : null, code)
        { LaunchMayHaveDispatched = metadata.LaunchMayHaveDispatched, LaunchSummary = metadata.LaunchSummary, Mutation = metadata.LaunchMutation };
    }

    public async Task<LocalApiDiagnosticOutputLease> AcquireDiagnosticOutputAsync(string operationId, string commandDigest,
        string outputId, long expectedSizeBytes, string expectedSha256, CancellationToken cancellationToken = default)
    {
        LocalApiV3OperationRequest.RequireBinding(LocalApiV3Contract.Version, operationId, commandDigest);
        DiagnosticOperation operation; LocalApiV3ArchiveDescriptor descriptor;
        lock (_operationsGate)
        {
            EnsureRegistryOpenLocked(); operation = RequireV3Locked(operationId, commandDigest);
            descriptor = V3StatusLocked(operation).Output.Archive ?? throw Input("diagnostic_output_unavailable", "No published diagnostic archive is available.");
            if (descriptor.OutputId != outputId || descriptor.SizeBytes != expectedSizeBytes || descriptor.Sha256 != expectedSha256 || operation.Cleaning)
                throw Input("diagnostic_output_binding_invalid", "The diagnostic output reference does not match.");
            Interlocked.Increment(ref operation.ReadLeases);
        }
        try
        {
            var snapshot = operation.Metadata.Snapshot(operationId);
            var lease = await _stager.ReopenDiagnosticArchiveAsync(snapshot.Intent,
                snapshot.Resources.Single(resource => resource.Role == "archive"), cancellationToken).ConfigureAwait(false);
            return new(lease, descriptor, () => Interlocked.Decrement(ref operation.ReadLeases));
        }
        catch (OperationCanceledException)
        { Interlocked.Decrement(ref operation.ReadLeases); throw; }
        catch
        {
            lock (_operationsGate)
            {
                Interlocked.Decrement(ref operation.ReadLeases);
                MarkV3UnknownLocked(operation, "diagnostic_output_unavailable");
                try { PersistLocked(); } catch { operation.PersistenceUncertain = true; }
            }
            throw Input("diagnostic_output_unavailable", "The published archive could not be verified.");
        }
    }

    private void DrainV3Operations()
    {
        Task[] pending;
        lock (_operationsGate) { _registryClosing = true; pending = _v3Tasks.ToArray(); }
        try { Task.WhenAll(pending).GetAwaiter().GetResult(); } catch { /* joined failed preparation already retained its resource state */ }
    }

    private static string? V3PublicCode(string? code) => code switch
    {
        null => null,
        "diagnostic_preparation_failed" => code,
        "diagnostic_execution_failed" => code,
        "diagnostic_output_unavailable" => code,
        "diagnostic_cleanup_pending" => code,
        "diagnostic_cleanup_debt" => code,
        "journal_persist_failed" => code,
        "operation_expired" or "diagnostic_preflight_restart_expired" => "operation_expired",
        "diagnostic_cancelled" => "operation_cancelled",
        _ => "diagnostic_recovery_required"
    };

    private static LocalApiException SafeV3Failure(Exception exception, string fallback) => new(
        exception is OperationCanceledException ? "diagnostic_preparation_cancelled" :
        exception is LocalApiException { Code: "target_not_ready" or "artifact_changed" or "split_not_supported" or "operation_expired" or
            "staged_byte_capacity" or "staged_file_capacity" or "diagnostic_required_output_limit" or "diagnostic_required_stream_unusable" } local ? local.Code : fallback,
        "The bounded retained diagnostic operation could not complete.");
}
