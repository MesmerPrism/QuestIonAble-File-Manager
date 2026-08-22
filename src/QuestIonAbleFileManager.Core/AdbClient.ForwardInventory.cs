namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    /// <summary>
    /// Observes the shared ADB daemon's complete forward inventory and projects
    /// only one exact serial. This deliberately uses no <c>-s</c> transport
    /// selector; an inventory row is not a health, ownership, or reachability
    /// claim and this route has no forwarding mutation operation.
    /// </summary>
    public async Task<AdbForwardInventoryResult> GetForwardInventoryAsync(
        string requestedSerial,
        CancellationToken cancellationToken = default)
    {
        requestedSerial = AndroidInput.RequireSerial(requestedSerial);
        var result = await RunAsync(
            ["forward", "--list"],
            InspectionTimeout,
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Read shared ADB forward inventory");
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new InvalidDataException(
                "Shared ADB forward inventory returned unexpected standard-error output.");
        }

        return new AdbForwardInventoryResult(
            requestedSerial,
            AdbOutputParser.ParseForwardInventory(result.StandardOutput, requestedSerial));
    }
}
