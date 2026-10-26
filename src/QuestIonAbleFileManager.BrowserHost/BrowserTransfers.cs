using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

internal sealed record BrowserTransferCalls(
    Func<CancellationToken, Task<IReadOnlyList<QuestDevice>>> Discover,
    Func<string, string, Stream, long, CancellationToken, Task<StreamingCommandResult>> Pull,
    Func<string, string, Stream, long, CancellationToken, Task<BoundedApkExportResult>> Export);

/// <summary>Read-only bounded Core delivery into process-owned opaque artifacts.</summary>
public sealed class BrowserTransfers
{
    private readonly Func<BrowserArtifactStore> _artifacts;
    private readonly Func<BrowserTransferCalls> _calls;
    public BrowserTransfers(Func<BrowserArtifactStore> artifacts) : this(artifacts, () =>
    {
        var client = AdbClient.CreateDefault();
        return new(client.GetDevicesAsync, client.PullFileBoundedAsync, client.ExportSingleApkBoundedAsync);
    }) { }
    internal BrowserTransfers(Func<BrowserArtifactStore> artifacts, Func<BrowserTransferCalls> calls)
    { _artifacts = artifacts; _calls = calls; }

    public async Task<BrowserArtifactTransferOutcome> ExecuteAsync(BrowserTransferRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // Revalidate the closed request even when invoked through the typed seam.
        AndroidInput.RequireSerial(request.Serial); BrowserArtifactStore.ValidateFileName(request.FileName);
        if (request.Action == "files.pull")
        {
            AndroidInput.RequireRemotePath(request.RemotePath!);
            if (request.PackageName is not null) throw new BrowserRequestException("capture_request_invalid");
        }
        else if (request.Action == "apk.export")
        {
            AndroidInput.RequirePackageName(request.PackageName!);
            if (request.RemotePath is not null || !request.FileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                throw new BrowserRequestException("capture_request_invalid");
        }
        else throw new BrowserRequestException("action_unsupported");
        var calls = _calls();
        var devices = await calls.Discover(cancellation).ConfigureAwait(false);
        if (devices.Count(device => device.IsReady && device.Serial == request.Serial) != 1)
            throw new BrowserRequestException("target_not_ready");
        var store = _artifacts();
        StreamingCommandResult? transfer = null; BoundedApkExportResult? export = null;
        var receipt = await store.CaptureAsync(request.FileName, async (destination, token) =>
        {
            if (request.Action == "files.pull")
                transfer = await calls.Pull(request.Serial, request.RemotePath!, destination,
                    BrowserArtifactCommands.MaximumArtifactBytes, token).ConfigureAwait(false);
            else
            {
                export = await calls.Export(request.Serial, request.PackageName!, destination,
                    BrowserArtifactCommands.MaximumArtifactBytes, token).ConfigureAwait(false);
                if (export.PackageName != request.PackageName) throw new BrowserArtifactException("capture_binding_invalid");
                AndroidInput.RequireRemotePath(export.RemoteApkPath);
                transfer = export.Transfer;
            }
            transfer.CommandResult.EnsureSuccess("Capture bounded bytes");
        }, cancellation).ConfigureAwait(false);
        // The Core runner and independent artifact store must agree. Never publish
        // a successful partial capture or expose a Core/native output path.
        if (transfer is null || transfer.BytesWritten != receipt.SizeBytes || transfer.Sha256 != receipt.Sha256)
        {
            try { await store.RemoveAsync(receipt.Id, CancellationToken.None).ConfigureAwait(false); }
            catch { throw new BrowserArtifactException("capture_cleanup_debt", receipt.Id); }
            throw new BrowserArtifactException("capture_binding_invalid");
        }
        return new(receipt, request.Serial, export?.RemoteApkPath ?? request.RemotePath, export?.PackageName,
            export is null ? null : $"{receipt.Sha256}  {receipt.FileName}\n");
    }
}
