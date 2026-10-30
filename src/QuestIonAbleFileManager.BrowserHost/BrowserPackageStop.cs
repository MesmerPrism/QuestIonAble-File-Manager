using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

/// <summary>Closed package/process/activity facts, with no raw command or process identities.</summary>
public sealed record BrowserPackageStopOutcome(
    string Serial,
    string PackageName,
    bool PackagePresentBeforeDispatch,
    bool PackagePresentAfterDispatch,
    bool? ProcessAbsenceVerified,
    string ProcessEvidenceDisposition,
    int ProcessCount,
    int ForegroundComponentCount,
    int TopResumedComponentCount,
    bool IsQuiescent)
{
    public string ContractVersion => "questionable.file_manager.browser_package_stop.v1";
}

public sealed partial class BrowserCommands
{
    private TaskCompletionSource? _packageStopCompletion;
    private bool _packageStopsClosing;

    /// <summary>Stop admission and join the actual executor settlement before host cleanup.</summary>
    public Task DrainPackageStopsAsync()
    {
        lock (_admission)
        {
            _packageStopsClosing = true;
            return _packageStopCompletion?.Task ?? Task.CompletedTask;
        }
    }

    private async Task<BrowserCommandOutcome> ExecutePackageStopAsync(
        BrowserCommandRequest request, CancellationToken stopping)
    {
        IDisposable gate;
        TaskCompletionSource completion;
        lock (_admission)
        {
            if (_requests.ContainsKey(request.RequestId))
                throw new BrowserRequestException("request_already_consumed", 409);
            if (_packageStopsClosing || _running)
                throw new BrowserRequestException("busy", 409);
            if (_requests.Count >= MaximumRequests)
                throw new BrowserRequestException("session_capacity_reached", 409);
            gate = _operationGate.TryAcquire() ?? throw new BrowserRequestException("busy", 409);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _packageStopCompletion = completion;
            _requests[request.RequestId] = new(request.RequestId, request.Action, "running");
            _running = true;
        }
        try
        {
            BrowserCommandOutcome outcome;
            try
            {
                // Only deliberate host stopping is cancellation authority. An elapsed
                // estimate or disconnected HTTP waiter cannot detach a dispatched stop.
                var result = await _execute(request.Command, stopping).ConfigureAwait(false);
                outcome = ProjectPackageStop(request, result);
            }
            catch (Exception)
            {
                // Admission may already have changed Android state. Neither an exception
                // receipt nor arbitrary exception/source text establishes confirmation.
                outcome = PackageStopUnknown(request, "package_stop_outcome_unknown");
            }
            _requests[request.RequestId] = outcome;
            return outcome;
        }
        finally
        {
            lock (_admission)
            {
                _running = false;
                gate.Dispose();
                _packageStopCompletion = null;
                completion.TrySetResult();
            }
        }
    }

    private static BrowserCommandOutcome ProjectPackageStop(BrowserCommandRequest request, OperatorExecutionResult result)
    {
        var command = request.Command;
        if (result.Command is not { Kind: OperatorCommandKind.StopPackage, OperatorConfirmed: true } returned ||
            returned.Serial != command.Serial || returned.PackageName != command.PackageName ||
            !returned.CliArguments.SequenceEqual(command.CliArguments, StringComparer.Ordinal) ||
            result.PackageStopResult is not { PackagePresentBeforeDispatch: true, PackagePresentAfterDispatch: true } stop ||
            stop.Serial != command.Serial || stop.PackageName != command.PackageName ||
            stop.StopCommand is not { Succeeded: true, Arguments: not null } dispatch ||
            !dispatch.Arguments.SequenceEqual(
                ["-s", command.Serial!, "shell", "am", "force-stop", "--user", "current", command.PackageName!],
                StringComparer.Ordinal) ||
            stop.Quiescence is not { ProcessIds: not null, ForegroundComponents: not null, TopResumedComponents: not null } quiescence ||
            !PackageStopReceiptBound(command, result.MutationReceipt))
            return PackageStopUnknown(request, "package_stop_binding_mismatch");

        var receipt = result.MutationReceipt!;
        if (receipt.Stage == OperatorMutationStage.Confirmed && !quiescence.IsQuiescent)
            return PackageStopUnknown(request, "package_stop_binding_mismatch");

        // Preserve the actual identity, stage, target, timestamps and readback bit.
        // Replace every free-text field rather than attempting to redact raw output.
        var safeReceipt = receipt with
        {
            DesiredState = "package-stop",
            ObservedState = receipt.Stage == OperatorMutationStage.Confirmed ? "package-quiescent" : "package-stop-pending",
            Transitions = receipt.Transitions.Select(transition => transition with
            {
                Message = "package-stop-" + JsonNamingPolicy.CamelCase.ConvertName(transition.Stage.ToString())
            }).ToArray()
        };
        return new(request.RequestId, request.Action,
            receipt.Stage == OperatorMutationStage.Confirmed ? "completed" : "pending", Mutation: safeReceipt)
        {
            PackageStop = new(stop.Serial, stop.PackageName,
                stop.PackagePresentBeforeDispatch, stop.PackagePresentAfterDispatch,
                quiescence.ProcessAbsenceVerified, quiescence.ProcessEvidence?.Disposition ?? "unverified",
                quiescence.ProcessIds.Count, quiescence.ForegroundComponents.Count,
                quiescence.TopResumedComponents.Count, quiescence.IsQuiescent)
        };
    }

    private static bool PackageStopReceiptBound(OperatorCommand command, OperatorMutationReceipt? receipt)
    {
        if (receipt is null || receipt.CommandKind != OperatorCommandKind.StopPackage ||
            receipt.Target != command.Serial ||
            receipt.DesiredState != $"exact package {command.PackageName} quiescent for the current Android user on {command.Serial}" ||
            receipt.Stage is not (OperatorMutationStage.Pending or OperatorMutationStage.Confirmed) ||
            !receipt.HeadsetReadback || receipt.ObservedState is null ||
            receipt.OperationId is null || !receipt.OperationId.StartsWith("pc-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(receipt.OperationId[3..], "N", out var id) || id == Guid.Empty ||
            receipt.OperationId[3..] != id.ToString("N") || receipt.Transitions is not { Count: 3 } transitions)
            return false;

        // These are the exact stages produced by the Core stop tracker. A pending
        // readback cannot borrow a confirmed transition or reconstruct a missing send.
        var stages = new[] { OperatorMutationStage.Sent, OperatorMutationStage.Pending, receipt.Stage };
        for (var index = 0; index < stages.Length; index++)
        {
            if (transitions[index] is not { Message: not null } transition ||
                transition.Stage != stages[index] || transition.At == default ||
                index > 0 && transition.At < transitions[index - 1].At)
                return false;
        }
        return true;
    }

    private static BrowserCommandOutcome PackageStopUnknown(BrowserCommandRequest request, string error) =>
        new(request.RequestId, request.Action, "outcomeUnknown", Error: error);
}
