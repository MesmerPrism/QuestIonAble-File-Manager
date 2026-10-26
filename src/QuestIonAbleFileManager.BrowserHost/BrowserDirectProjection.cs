using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectBootstrap(string Schema, string OperationId, OperatorMutationStage Stage);
public sealed record BrowserDirectAdoption(OperatorMutationStage Stage, bool SignedStatusRead, bool TypedKioskStatusRead, bool StagingInventoryRead);
public sealed record BrowserDirectStatus(bool InstallerAllowed, DateTimeOffset ObservedAtUtc);
public sealed record BrowserDirectCleanup(string Schema, string OperationId, OperatorMutationStage Stage, string ListenerDisposition);
public sealed record BrowserDirectConnectionOutcome(string? ConnectionId, string ConnectRequestId, string UsbOperationId,
    string Serial, string ProductChannel, string State, DateTimeOffset? ExpiresAtUtc = null, bool? EnabledByRequest = null,
    BrowserDirectBootstrap? Bootstrap = null, BrowserDirectAdoption? Adoption = null, BrowserDirectStatus? Status = null, BrowserDirectCleanup? Cleanup = null);

internal static class BrowserDirectProjection
{
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static void Bootstrap(RustyKioskUsbDirectLinkReceipt receipt, BrowserDirectConnectContext context, DateTimeOffset now)
    {
        if (receipt.Schema != RustyKioskUsbDirectLinkBootstrapper.ReceiptSchema || receipt.OperationId != context.UsbOperationId ||
            receipt.ProductChannel != context.ProductChannel || receipt.PackageName != RustyKioskProductContract.Parse(context.ProductChannel).MainPackage ||
            receipt.PackageUid < 10000 || receipt.TargetSerialSha256 != Hash(context.Serial) ||
            !Regex.IsMatch(receipt.BridgeGenerationSha256, "\\A[a-f0-9]{64}\\z") ||
            receipt.Stage != OperatorMutationStage.Confirmed || receipt.ExpiresAtUtc <= now || receipt.ExpiresAtUtc-now > TimeSpan.FromMinutes(10))
            throw new InvalidDataException("direct_bootstrap_binding_invalid");
    }
    internal static RustyKioskDirectStatus Result(KioskDirectOperatorResult value, KioskDirectOperatorCommand command,
        RustyKioskUsbDirectLinkReceipt receipt, string? expectedSession = null, long? expectedGeneration = null)
    {
        if (value.Command != command || value.Mutation.ActionId != command.ActionId || value.Mutation.Action != command.Action ||
            value.Mutation.Stage != OperatorMutationStage.Confirmed || value.Status is not { } status || status.Schema != RustyKioskDirectClient.ContractSchema ||
            status.BridgeGeneration is not > 0 || Hash(status.BridgeGeneration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) != receipt.BridgeGenerationSha256 ||
            status.SessionId is null || !Regex.IsMatch(status.SessionId,"\\A[A-Za-z0-9_-]{8,64}\\z") ||
            (expectedSession is not null && (status.SessionId != expectedSession || status.BridgeGeneration != expectedGeneration)))
            throw new InvalidDataException("direct_readback_binding_invalid");
        if (command.Action == KioskDirectOperatorAction.Adopt &&
            (value.KioskResult is not { Schema: RustyKioskContract.ResultSchema, Command: RustyKioskCommand.Status } kiosk ||
             !RustyKioskReadback.Confirms(RustyKioskCommand.Status,null,kiosk) || value.StagedFiles is null))
            throw new InvalidDataException("direct_adoption_binding_invalid");
        return status;
    }
    internal static BrowserDirectCleanup Cleanup(RustyKioskUsbDirectCleanupReceipt receipt, string operationId, bool? owned)
    {
        if (receipt.Schema != RustyKioskUsbDirectLinkBootstrapper.CleanupReceiptSchema || receipt.OperationId != operationId ||
            receipt.Stage is not (OperatorMutationStage.Confirmed or OperatorMutationStage.CleanupUnknown))
            throw new InvalidDataException("direct_cleanup_binding_invalid");
        return new(receipt.Schema,receipt.OperationId,receipt.Stage,receipt.Stage == OperatorMutationStage.Confirmed && owned is not null
            ? owned.Value ? "owned-stopped" : "preexisting-preserved" : "unknown");
    }
}
