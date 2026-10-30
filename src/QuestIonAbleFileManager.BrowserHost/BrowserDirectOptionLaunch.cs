using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectLaunchReview(string OptionsRequestId,string SelectOriginalRequestId,string CatalogRequestId,string Key,string PackageName,string BindingSha256);
internal sealed record BrowserDirectLaunchSource(BrowserDirectLaunchReview Review,BrowserDirectCommandOutcome Source,string Value);

public sealed partial class BrowserCommands
{
    // Existing cache facts are reviewed once, under _admission. Later recovery
    // uses the retained launch review and never reopens this source admission.
    private BrowserDirectLaunchSource ReadDirectOptionLaunch(BrowserDirectRequest request)
    {
        if(!_requests.TryGetValue(request.OptionsRequestId!,out var cached) || cached.RequestId!=request.OptionsRequestId || cached.State!="completed" ||
            cached.Result is not null || cached.Mutation is not null || cached.KioskDirectCommand is not { } source)
            throw new BrowserRequestException("direct_options_unavailable",409);
        var invoke=cached.Action=="kiosk.direct.invoke";
        if(!invoke && cached.Action!="kiosk.direct.request-result" || source.Operation!=(invoke?"invoke":"requestResult") || source.Command!="select" ||
            source.ConnectionId!=request.ConnectionId || !BrowserDirectLaunchReviewValidation.Id(source.OriginalRequestId) ||
            !BrowserDirectLaunchReviewValidation.Id(source.ConnectRequestId) || source.UsbOperationId!="usb_"+Guid.Parse(source.ConnectRequestId).ToString("N") ||
            source.LogicalRequestId!="kiosk_"+Guid.Parse(source.OriginalRequestId).ToString("N") ||
            invoke && source.OriginalRequestId!=cached.RequestId || !invoke && source.OriginalRequestId==cached.RequestId ||
            !source.ReconcilesOriginal || source.OriginalEffect!="confirmed" || source.CancellationAcknowledged || source.Lifecycle is not null || source.LaunchReview is not null ||
            source.SelectionReview is not { } selection || !BrowserDirectLaunchReviewValidation.Selection(selection,source.Serial,source.ProductChannel,source.Value) ||
            source.CoreReceipt is not { Stage:OperatorMutationStage.Confirmed } receipt || !BrowserDirectLaunchReviewValidation.ActionId(receipt.ActionId) ||
            receipt.Action!=(invoke?KioskDirectOperatorAction.Invoke:KioskDirectOperatorAction.RequestResult) || receipt.RequestId!=source.LogicalRequestId ||
            source.TypedResult is not { Schema:RustyKioskContract.ResultSchema,Command:"select",Accepted:true,Completed:true } typed || typed.RequestId!=source.LogicalRequestId ||
            !BrowserDirectCommandProjection.SelectionProven(typed,selection,source.Value) ||
            !BrowserDirectLaunchOptionsProjection.ReadyObservation(typed.LaunchOptions,selection.PackageName))
            throw new BrowserRequestException("direct_options_unavailable",409);
        if(!_requests.TryGetValue(source.OriginalRequestId,out var original) || original.RequestId!=source.OriginalRequestId || original.Action!="kiosk.direct.invoke" ||
            original.KioskDirectCommand is not { Operation:"invoke",Command:"select" } prior || prior.OriginalRequestId!=source.OriginalRequestId ||
            prior.LogicalRequestId!=source.LogicalRequestId || prior.ConnectionId!=source.ConnectionId || prior.ConnectRequestId!=source.ConnectRequestId ||
            prior.UsbOperationId!=source.UsbOperationId || prior.Serial!=source.Serial || prior.ProductChannel!=source.ProductChannel ||
            prior.Value!=source.Value || prior.SelectionReview!=selection || prior.LaunchReview is not null)
            throw new BrowserRequestException("direct_options_unavailable",409);
        if(typed.LaunchOptions!.Options.Count(row=>row.OptionId==request.Value)!=1)
            throw new BrowserRequestException("direct_option_not_reviewed",409);
        return new(new(request.OptionsRequestId!,source.OriginalRequestId,selection.CatalogRequestId,selection.Key,selection.PackageName,typed.LaunchOptions.BindingSha256!),source,request.Value!);
    }
}

internal static class BrowserDirectLaunchReviewValidation
{
    internal static bool Id(string? value) => Guid.TryParseExact(value,"D",out var id) && id!=Guid.Empty && id.ToString("D")==value;
    internal static bool ActionId(string? value) => value is { Length:39 } && value.StartsWith("action_",StringComparison.Ordinal) &&
        Guid.TryParseExact(value[7..],"N",out var id) && id!=Guid.Empty && id.ToString("N")==value[7..];
    internal static bool Digest(string? value) => value is { Length:64 } && value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool Package(string? value)
    {
        if(value is null || value.Length>256)return false;
        try { return AndroidInput.RequirePackageName(value)==value; } catch(ArgumentException) { return false; }
    }
    internal static bool Selection(BrowserDirectSelectionReview review,string serial,string channel,string? key) => Id(review.CatalogRequestId) &&
        review.Serial==serial && review.ProductChannel==channel && (channel is "stable" or "labs") &&
        review.Key==key && BrowserKioskCommands.IsSelectableKey(key) && BrowserDirectCommands.IsWellFormedUtf16(key!) && Package(review.PackageName);
    internal static bool Review(BrowserDirectLaunchReview? review) => review is not null && Id(review.OptionsRequestId) && Id(review.SelectOriginalRequestId) &&
        Id(review.CatalogRequestId) && BrowserKioskCommands.IsSelectableKey(review.Key) && BrowserDirectCommands.IsWellFormedUtf16(review.Key) &&
        Package(review.PackageName) && Digest(review.BindingSha256);
}
