using System.Collections.ObjectModel;

namespace QuestIonAbleFileManager.Core;

public enum KioskDirectOperatorAction
{
    Status,
    Invoke,
    RequestStatus,
    CancelRequest,
    ExportTags,
    ImportTags,
    ListStaging,
    UploadStaging,
    DownloadStaging,
    DeleteStaging,
    Install,
    InstallStatus
}

public sealed record KioskDirectOperatorCommand(
    KioskDirectOperatorAction Action,
    string ActionId,
    RustyKioskCommand? KioskCommand = null,
    string? Value = null,
    string? RequestId = null,
    string? LocalPath = null,
    string? StagedName = null,
    IReadOnlyList<string>? LocalApkPaths = null,
    bool Overwrite = false,
    bool OperatorConfirmed = false)
{
    public static KioskDirectOperatorCommand Status() => New(KioskDirectOperatorAction.Status);

    public static KioskDirectOperatorCommand Invoke(
        RustyKioskCommand command,
        string? value,
        bool operatorConfirmed) =>
        New(
            KioskDirectOperatorAction.Invoke,
            KioskCommand: command,
            Value: value,
            OperatorConfirmed: operatorConfirmed);

    public static KioskDirectOperatorCommand RequestStatus(string requestId) =>
        New(KioskDirectOperatorAction.RequestStatus, RequestId: RequireRequestId(requestId));

    public static KioskDirectOperatorCommand Cancel(string requestId, bool operatorConfirmed) =>
        New(
            KioskDirectOperatorAction.CancelRequest,
            RequestId: RequireRequestId(requestId),
            OperatorConfirmed: operatorConfirmed);

    public static KioskDirectOperatorCommand ExportTags(string outputPath) =>
        New(KioskDirectOperatorAction.ExportTags, LocalPath: Path.GetFullPath(outputPath));

    public static KioskDirectOperatorCommand ImportTags(string inputPath, bool operatorConfirmed) =>
        New(
            KioskDirectOperatorAction.ImportTags,
            LocalPath: Path.GetFullPath(inputPath),
            OperatorConfirmed: operatorConfirmed);

    public static KioskDirectOperatorCommand ListStaging() =>
        New(KioskDirectOperatorAction.ListStaging);

    public static KioskDirectOperatorCommand Upload(
        string localPath,
        string? stagedName,
        bool operatorConfirmed) =>
        New(
            KioskDirectOperatorAction.UploadStaging,
            LocalPath: Path.GetFullPath(localPath),
            StagedName: stagedName,
            OperatorConfirmed: operatorConfirmed);

    public static KioskDirectOperatorCommand Download(
        string stagedName,
        string outputPath,
        bool overwrite) =>
        New(
            KioskDirectOperatorAction.DownloadStaging,
            LocalPath: Path.GetFullPath(outputPath),
            StagedName: stagedName,
            Overwrite: overwrite);

    public static KioskDirectOperatorCommand Delete(string stagedName, bool operatorConfirmed) =>
        New(
            KioskDirectOperatorAction.DeleteStaging,
            StagedName: stagedName,
            OperatorConfirmed: operatorConfirmed);

    public static KioskDirectOperatorCommand Install(
        IReadOnlyList<string> localApkPaths,
        bool operatorConfirmed,
        string? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(localApkPaths);
        if (localApkPaths.Count is < 1 or > 32)
        {
            throw new ArgumentException("Choose one to 32 APK parts.", nameof(localApkPaths));
        }
        return New(
            KioskDirectOperatorAction.Install,
            RequestId: requestId is null ? null : RequireRequestId(requestId),
            LocalApkPaths: new ReadOnlyCollection<string>(
                localApkPaths.Select(Path.GetFullPath).ToArray()),
            OperatorConfirmed: operatorConfirmed);
    }

    public static KioskDirectOperatorCommand InstallStatus(string requestId) =>
        New(KioskDirectOperatorAction.InstallStatus, RequestId: RequireRequestId(requestId));

    private static KioskDirectOperatorCommand New(
        KioskDirectOperatorAction action,
        RustyKioskCommand? KioskCommand = null,
        string? Value = null,
        string? RequestId = null,
        string? LocalPath = null,
        string? StagedName = null,
        IReadOnlyList<string>? LocalApkPaths = null,
        bool Overwrite = false,
        bool OperatorConfirmed = false) =>
        new(
            action,
            "action_" + Guid.NewGuid().ToString("N"),
            KioskCommand,
            Value,
            RequestId,
            LocalPath,
            StagedName,
            LocalApkPaths,
            Overwrite,
            OperatorConfirmed);

    private static string RequireRequestId(string requestId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                requestId ?? string.Empty,
                "^[A-Za-z0-9_-]{8,64}$"))
        {
            throw new ArgumentException("The Direct Link request id is invalid.", nameof(requestId));
        }
        return requestId!;
    }
}

public sealed record KioskDirectOperatorMutationReceipt(
    string ActionId,
    KioskDirectOperatorAction Action,
    OperatorMutationStage Stage,
    string Message,
    string? RequestId = null);

public sealed record KioskDirectOperatorResult(
    KioskDirectOperatorCommand Command,
    KioskDirectOperatorMutationReceipt Mutation,
    RustyKioskDirectStatus? Status = null,
    RustyKioskOperatorResult? KioskResult = null,
    RustyKioskDirectRequestReceipt? RequestReceipt = null,
    IReadOnlyList<RustyKioskStagedFile>? StagedFiles = null,
    RustyKioskStagedFile? StagedFile = null,
    string? LocalFileName = null,
    RustyKioskDirectInstallReceipt? InstallReceipt = null);

/// <summary>
/// The single typed Core projection used by both the CLI and WPF Direct Link surfaces.
/// Credentials and endpoints belong to the injected client and never enter commands/results.
/// </summary>
public sealed class KioskDirectOperatorExecutor(RustyKioskDirectClient client)
{
    private readonly RustyKioskDirectClient _client = client ??
        throw new ArgumentNullException(nameof(client));

    public async Task<KioskDirectOperatorResult> ExecuteAsync(
        KioskDirectOperatorCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureConfirmation(command);
        switch (command.Action)
        {
            case KioskDirectOperatorAction.Status:
                return Result(command, OperatorMutationStage.Confirmed, "Signed Direct Link status was read back.",
                    Status: await _client.GetStatusAsync(cancellationToken).ConfigureAwait(false));

            case KioskDirectOperatorAction.Invoke:
                {
                    var kioskCommand = command.KioskCommand ??
                        throw new InvalidOperationException("The typed Kiosk action is missing.");
                    var admitted = await _client.AdmitKioskRequestAsync(
                            kioskCommand,
                            command.Value,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        var kiosk = await _client.WaitForKioskResultAsync(
                                admitted.RequestId,
                                kioskCommand,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        var stage = RustyKioskReadback.Confirms(kioskCommand, command.Value, kiosk)
                            ? OperatorMutationStage.Confirmed
                            : OperatorMutationStage.PendingWearerAction;
                        return Result(command, stage, kiosk.Message, admitted.RequestId, KioskResult: kiosk);
                    }
                    catch (TimeoutException)
                    {
                        var lifecycle = await _client.ReadKioskRequestStatusAsync(
                                admitted.RequestId,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return Result(
                            command,
                            lifecycle.MutationStage,
                            lifecycle.Message,
                            admitted.RequestId,
                            RequestReceipt: lifecycle);
                    }
                }

            case KioskDirectOperatorAction.RequestStatus:
                {
                    var request = await _client.ReadKioskRequestStatusAsync(
                            Require(command.RequestId),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return Result(command, request.MutationStage, request.Message, request.RequestId, RequestReceipt: request);
                }

            case KioskDirectOperatorAction.CancelRequest:
                {
                    var request = await _client.CancelKioskRequestAsync(
                            Require(command.RequestId),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return Result(command, request.MutationStage, request.Message, request.RequestId, RequestReceipt: request);
                }

            case KioskDirectOperatorAction.ExportTags:
                {
                    var output = Require(command.LocalPath);
                    await File.WriteAllBytesAsync(
                            output,
                            await _client.ReadTagsAsync(cancellationToken).ConfigureAwait(false),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return Result(command, OperatorMutationStage.Confirmed, "The signed tag document was exported.",
                        LocalFileName: Path.GetFileName(output));
                }

            case KioskDirectOperatorAction.ImportTags:
                {
                    var input = Require(command.LocalPath);
                    var validatedJson = RustyKioskTagFile.ValidateAndRead(input);
                    await _client.WriteTagsAsync(
                            System.Text.Encoding.UTF8.GetBytes(validatedJson),
                            cancellationToken)
                        .ConfigureAwait(false);
                    var kiosk = await _client.InvokeKioskAsync(
                            RustyKioskCommand.Reload,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return Result(command, OperatorMutationStage.Confirmed, kiosk.Message, KioskResult: kiosk);
                }

            case KioskDirectOperatorAction.ListStaging:
                return Result(command, OperatorMutationStage.Confirmed, "Signed staging inventory was read back.",
                    StagedFiles: await _client.ListStagingAsync(cancellationToken).ConfigureAwait(false));

            case KioskDirectOperatorAction.UploadStaging:
                {
                    var staged = await _client.UploadToStagingAsync(
                            Require(command.LocalPath),
                            command.StagedName,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return Result(command, OperatorMutationStage.Confirmed, "Remote size and digest were confirmed.",
                        StagedFile: staged);
                }

            case KioskDirectOperatorAction.DownloadStaging:
                {
                    var output = await _client.DownloadFromStagingAsync(
                            Require(command.StagedName),
                            Require(command.LocalPath),
                            command.Overwrite,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return Result(command, OperatorMutationStage.Confirmed, "The bounded signed download was atomically committed.",
                        LocalFileName: Path.GetFileName(output));
                }

            case KioskDirectOperatorAction.DeleteStaging:
                {
                    var name = Require(command.StagedName);
                    await _client.DeleteStagedAsync(name, cancellationToken).ConfigureAwait(false);
                    var refreshed = await _client.ListStagingAsync(cancellationToken).ConfigureAwait(false);
                    if (refreshed.Any(file => string.Equals(file.Name, name, StringComparison.Ordinal)))
                    {
                        return Result(command, OperatorMutationStage.Pending, "The exact staged filename remains present after refresh.");
                    }
                    return Result(command, OperatorMutationStage.Confirmed, "The exact staged filename is absent after refresh.");
                }

            case KioskDirectOperatorAction.Install:
                {
                    var paths = command.LocalApkPaths ??
                        throw new InvalidOperationException("The APK part set is missing.");
                    var names = paths.Select(Path.GetFileName).Select(Require).ToArray();
                    if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                    {
                        throw new InvalidOperationException(
                            "APK parts for one Direct Link install must have distinct staging filenames.");
                    }
                    var commitments = new List<RustyKioskStagedFile>(paths.Count);
                    foreach (var path in paths)
                    {
                        commitments.Add(
                            await _client.UploadToStagingAsync(path, cancellationToken: cancellationToken)
                                .ConfigureAwait(false));
                    }
                    var receipt = await _client.RequestInstallAsync(
                            commitments,
                            command.RequestId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return Result(command, InstallStage(receipt), receipt.Message, receipt.RequestId, InstallReceipt: receipt);
                }

            case KioskDirectOperatorAction.InstallStatus:
                {
                    var receipt = await _client.ReadInstallReceiptAsync(
                            Require(command.RequestId),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return Result(command, InstallStage(receipt), receipt.Message, receipt.RequestId, InstallReceipt: receipt);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Action, "Unknown Direct Link action.");
        }
    }

    private static KioskDirectOperatorResult Result(
        KioskDirectOperatorCommand command,
        OperatorMutationStage stage,
        string message,
        string? requestId = null,
        RustyKioskDirectStatus? Status = null,
        RustyKioskOperatorResult? KioskResult = null,
        RustyKioskDirectRequestReceipt? RequestReceipt = null,
        IReadOnlyList<RustyKioskStagedFile>? StagedFiles = null,
        RustyKioskStagedFile? StagedFile = null,
        string? LocalFileName = null,
        RustyKioskDirectInstallReceipt? InstallReceipt = null) =>
        new(
            command,
            new KioskDirectOperatorMutationReceipt(command.ActionId, command.Action, stage, message, requestId),
            Status,
            KioskResult,
            RequestReceipt,
            StagedFiles,
            StagedFile,
            LocalFileName,
            InstallReceipt);

    private static void EnsureConfirmation(KioskDirectOperatorCommand command)
    {
        var mutating = command.Action is
            KioskDirectOperatorAction.Invoke or
            KioskDirectOperatorAction.CancelRequest or
            KioskDirectOperatorAction.ImportTags or
            KioskDirectOperatorAction.UploadStaging or
            KioskDirectOperatorAction.DeleteStaging or
            KioskDirectOperatorAction.Install;
        if (mutating && !command.OperatorConfirmed)
        {
            throw new InvalidOperationException(
                "This Direct Link mutation requires explicit operator confirmation.");
        }
    }

    private static OperatorMutationStage InstallStage(RustyKioskDirectInstallReceipt receipt) =>
        receipt.Installed
            ? OperatorMutationStage.Confirmed
            : receipt.Failed
                ? OperatorMutationStage.Failed
                : receipt.NeedsWearerAction
                    ? OperatorMutationStage.PendingWearerAction
                    : OperatorMutationStage.Pending;

    private static string Require(string? value) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException("The Direct Link action is missing a required value.");
}
