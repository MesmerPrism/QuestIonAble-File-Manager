using QuestIonAbleFileManager.Core;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectCoreReceipt(string ActionId,KioskDirectOperatorAction Action,string RequestId,OperatorMutationStage Stage);
public sealed record BrowserDirectLifecycle(string RequestId,string OperationState,bool Accepted,bool Completed,long? EnqueuedAtMs,long? ExpiresAtMs);
public sealed record BrowserDirectSelectedEntry(string Key,string LaunchRequirement)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PackageName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Installed { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Launchable { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Selectable { get; init; }
}
public sealed record BrowserDirectSelectionReview(string CatalogRequestId,string Serial,string ProductChannel,string Key,string PackageName);
public sealed record BrowserDirectTypedResult(string Schema,string RequestId,string Command,bool Accepted,bool Completed,
    bool? ControlsOpen,string? SelectedKey,string? SelectedPackage,bool SelectedInstalled,bool SelectedLaunchable,
    string? SelectedLaunchRequirement,bool? PendingRequirementLaunch,bool GuardArmed,bool SetupHelperInstalled,bool SetupHelperReady,
    BrowserDirectSelectedEntry? SelectedEntry)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequestWifiAdbAfterBoot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AccessibilityEnabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SystemPassthroughEnabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PassthroughStyle { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PassthroughLutApplied { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WifiAdbEnabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Search { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TagFilter { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ValueMatches { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequestedTagPresent { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BrowserDirectLaunchOptions? LaunchOptions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LastDispatchedOptionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LastDispatchedOptionPackage { get; init; }
}
public sealed record BrowserDirectCommandOutcome(string ConnectionId,string ConnectRequestId,string UsbOperationId,string Serial,string ProductChannel,
    string Operation,string OriginalRequestId,string LogicalRequestId,string Command,string? Value,string OriginalEffect="unknown",bool ReconcilesOriginal=false,
    BrowserDirectCoreReceipt? CoreReceipt=null,BrowserDirectLifecycle? Lifecycle=null,BrowserDirectTypedResult? TypedResult=null,bool CancellationAcknowledged=false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public BrowserDirectSelectionReview? SelectionReview { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public BrowserDirectLaunchReview? LaunchReview { get; init; }
}

internal static class BrowserDirectCommandProjection
{
    internal static BrowserDirectCommandOutcome Metadata(BrowserDirectRequest request,BrowserDirectConnections.Entry entry,BrowserDirectCommandOutcome? original,BrowserDirectSelectionReview? selection=null,BrowserDirectLaunchSource? launch=null)
    {
        var operation=request.Action switch
        { "kiosk.direct.invoke"=>"invoke","kiosk.direct.request-status"=>"requestStatus","kiosk.direct.request-result"=>"requestResult","kiosk.direct.request-cancel"=>"requestCancel",_=>throw new InvalidDataException() };
        if(!request.Recovery)
        {
            var kind=request.Command ?? throw new BrowserRequestException("direct_command_unsupported");
            var name=kind.ToWireName();_ = BrowserDirectCommands.CommandName(name);BrowserDirectCommands.ValidateValue(kind,request.Value);
            ValidateSelection(kind,request.Value,selection,entry);
            if (kind==RustyKioskCommand.Select && selection!.CatalogRequestId!=request.CatalogRequestId)
                throw new BrowserRequestException("direct_selection_mismatch",409);
            ValidateLaunch(kind,launch?.Review);
            if(kind==RustyKioskCommand.LaunchOption && (launch!.Review.OptionsRequestId!=request.OptionsRequestId || launch.Value!=request.Value ||
                launch.Source.ConnectionId!=entry.Id || launch.Source.ConnectRequestId!=entry.Context.RequestId || launch.Source.UsbOperationId!=entry.Context.UsbOperationId ||
                launch.Source.Serial!=entry.Context.Serial || launch.Source.ProductChannel!=entry.Context.ProductChannel))
                throw new BrowserRequestException("direct_launch_mismatch",409);
            return new(entry.Id,entry.Context.RequestId,entry.Context.UsbOperationId,entry.Context.Serial,entry.Context.ProductChannel,
                operation,request.RequestId,request.LogicalRequestId,name,request.Value) { SelectionReview=selection,LaunchReview=launch?.Review };
        }
        if(original is null || original.Operation!="invoke" || original.OriginalRequestId!=request.OriginalRequestId || original.ConnectionId!=entry.Id ||
            original.ConnectRequestId!=entry.Context.RequestId || original.UsbOperationId!=entry.Context.UsbOperationId ||
            original.Serial!=entry.Context.Serial || original.ProductChannel!=entry.Context.ProductChannel ||
            original.LogicalRequestId!="kiosk_"+Guid.Parse(request.OriginalRequestId!).ToString("N"))
            throw new BrowserRequestException("direct_original_mismatch",409);
        BrowserDirectCommands.ValidateValue(BrowserDirectCommands.CommandName(original.Command),original.Value);
        ValidateSelection(BrowserDirectCommands.CommandName(original.Command),original.Value,original.SelectionReview,entry);
        ValidateLaunch(BrowserDirectCommands.CommandName(original.Command),original.LaunchReview);
        return original with { Operation=operation,OriginalEffect="unknown",ReconcilesOriginal=false,CoreReceipt=null,Lifecycle=null,TypedResult=null,CancellationAcknowledged=false };
    }
    private static void ValidateLaunch(RustyKioskCommand kind,BrowserDirectLaunchReview? review)
    {
        if(kind==RustyKioskCommand.LaunchOption ? !BrowserDirectLaunchReviewValidation.Review(review) : review is not null)
            throw new BrowserRequestException("direct_launch_mismatch",409);
    }
    internal static bool SelectionProven(BrowserDirectTypedResult typed,BrowserDirectSelectionReview selection,string? value) =>
        typed.SelectedKey==value && typed.SelectedKey==selection.Key && typed.SelectedPackage==selection.PackageName && typed.SelectedInstalled && typed.SelectedLaunchable &&
        typed.SelectedEntry is { Installed:true,Launchable:true,Selectable:true } row && row.Key==selection.Key && row.PackageName==selection.PackageName;
    private static void ValidateSelection(RustyKioskCommand kind,string? value,BrowserDirectSelectionReview? review,BrowserDirectConnections.Entry entry)
    {
        if(kind!=RustyKioskCommand.Select)
        {
            if(review is not null)throw new BrowserRequestException("direct_selection_mismatch",409);
            return;
        }
        if(review is null || !Guid.TryParseExact(review.CatalogRequestId,"D",out var id) || id==Guid.Empty || id.ToString("D")!=review.CatalogRequestId ||
            review.Serial!=entry.Context.Serial || review.ProductChannel!=entry.Context.ProductChannel || review.Key!=value ||
            !BrowserKioskCommands.IsSelectableKey(review.Key) || !BrowserDirectCommands.IsWellFormedUtf16(review.Key) ||
            string.IsNullOrEmpty(review.PackageName) || review.PackageName.Length>256)
            throw new BrowserRequestException("direct_selection_mismatch",409);
        try { if(AndroidInput.RequirePackageName(review.PackageName)!=review.PackageName)throw new ArgumentException(); }
        catch(ArgumentException) { throw new BrowserRequestException("direct_selection_mismatch",409); }
    }
    internal static BrowserCommandOutcome Project(BrowserDirectRequest request,BrowserDirectCommandOutcome source,KioskDirectOperatorCommand command,KioskDirectOperatorResult result)
    {
        if(result.Command!=command || result.Mutation.ActionId!=command.ActionId || result.Mutation.Action!=command.Action ||
            result.Mutation.RequestId!=source.LogicalRequestId || command.RequestId!=source.LogicalRequestId || !Enum.IsDefined(result.Mutation.Stage))
            throw new InvalidDataException("direct_command_binding_invalid");
        var receipt=new BrowserDirectCoreReceipt(result.Mutation.ActionId,result.Mutation.Action,source.LogicalRequestId,result.Mutation.Stage);
        if(result.KioskResult is { } typed && result.RequestReceipt is null && request.Action is "kiosk.direct.invoke" or "kiosk.direct.request-result")
        {
            var expected=BrowserDirectCommands.CommandName(source.Command);
            if(command.KioskCommand!=expected || command.Value!=source.Value || typed.Schema!=RustyKioskContract.ResultSchema ||
                typed.RequestId!=source.LogicalRequestId || typed.Command!=expected || !typed.Accepted || !typed.Completed)
                throw new InvalidDataException("direct_typed_binding_invalid");
            var shared=RustyKioskReadback.Confirms(expected,source.Value,typed);
            if(result.Mutation.Stage!=(shared?OperatorMutationStage.Confirmed:OperatorMutationStage.PendingWearerAction))throw new InvalidDataException("direct_typed_receipt_invalid");
            var snapshot=Typed(typed,source.Value,shared);
            var launch=expected is RustyKioskCommand.LaunchNormal or RustyKioskCommand.LaunchKiosk;
            var tags=expected is RustyKioskCommand.AddTag or RustyKioskCommand.RemoveTag;
            var selection=source.SelectionReview;
            var selectionProof=expected!=RustyKioskCommand.Select || selection is not null && SelectionProven(snapshot,selection,source.Value);
            if(expected==RustyKioskCommand.Select && selectionProof)
                snapshot=snapshot with { LaunchOptions=BrowserDirectLaunchOptionsProjection.Capture(typed.State,selection!) };
            var optionProof=expected!=RustyKioskCommand.LaunchOption || source.LaunchReview is { } review && snapshot.LastDispatchedOptionId==source.Value &&
                snapshot.LastDispatchedOptionPackage==review.PackageName && snapshot.SelectedPackage==review.PackageName;
            var missing=!selectionProof || !optionProof || launch && snapshot.PendingRequirementLaunch is null || expected==RustyKioskCommand.SetLaunchRequirement && shared && snapshot.SelectedEntry is null ||
                tags && (snapshot.SelectedEntry is null || snapshot.RequestedTagPresent is null);
            var confirmed=shared && !missing && (!launch || snapshot.PendingRequirementLaunch==false);
            var effect=confirmed?"confirmed":missing?"unknown":"pendingWearerAction";
            var state=request.Action=="kiosk.direct.request-result"?"completed":confirmed?"completed":missing?"outcomeUnknown":"pending";
            return new(request.RequestId,request.Action,state,KioskDirectCommand:source with
            { OriginalEffect=effect,ReconcilesOriginal=confirmed,CoreReceipt=receipt,TypedResult=snapshot });
        }
        if(result.RequestReceipt is not { } lifecycle || result.KioskResult is not null || lifecycle.RequestId!=source.LogicalRequestId ||
            lifecycle.EnqueuedAtMs is < 0 || lifecycle.ExpiresAtMs is < 0 || lifecycle.MutationStage!=result.Mutation.Stage)
            throw new InvalidDataException("direct_lifecycle_binding_invalid");
        var raw=new BrowserDirectLifecycle(lifecycle.RequestId,lifecycle.OperationState,lifecycle.Accepted,lifecycle.Completed,lifecycle.EnqueuedAtMs,lifecycle.ExpiresAtMs);
        var originalEffect=lifecycle.OperationState switch { "pending"=>"pending","pending_wearer_action"=>"pendingWearerAction",_=>"unknown" };
        var cancelled=request.Action=="kiosk.direct.request-cancel" && lifecycle.OperationState=="cancelled" && lifecycle.Completed;
        var read=request.Action is "kiosk.direct.request-status" or "kiosk.direct.request-result";
        var top=cancelled || read && lifecycle.OperationState!="unknown"?"completed":request.Action=="kiosk.direct.invoke" && originalEffect!="unknown"?"pending":"outcomeUnknown";
        return new(request.RequestId,request.Action,top,KioskDirectCommand:source with
        { CoreReceipt=receipt,Lifecycle=raw,OriginalEffect=originalEffect,CancellationAcknowledged=cancelled });
    }
    private static BrowserDirectTypedResult Typed(RustyKioskOperatorResult typed,string? value,bool matches)
    {
        var state=typed.State;var key=state.SelectedKey;
        if(key is not null && (key.Length is < 1 or > 160 || key.Trim()!=key || key.Any(char.IsControl) ||
            (typed.Command is RustyKioskCommand.Select or RustyKioskCommand.LaunchOption) && !BrowserDirectCommands.IsWellFormedUtf16(key)))
        {
            if(typed.Command is not (RustyKioskCommand.Select or RustyKioskCommand.LaunchOption))throw new InvalidDataException("direct_selected_key_invalid");
            key=null;
        }
        var package=state.SelectedPackage;
        if(typed.Command is RustyKioskCommand.Select or RustyKioskCommand.LaunchOption)package=SelectionPackage(package);
        else if(package is not null && (package.Length>256 || AndroidInput.RequirePackageName(package)!=package))throw new InvalidDataException("direct_selected_package_invalid");
        var requirement=state.SelectedLaunchRequirement?.ToWireName();
        var selected=key is null?[]:state.Entries.Where(entry=>entry.Key==key).Take(2).ToArray();
        var entry=selected.Length==1?new BrowserDirectSelectedEntry(key!,selected[0].LaunchRequirement.ToWireName()):null;
        if(typed.Command==RustyKioskCommand.Select && entry is not null)
        {
            var row=selected[0];
            var rowPackage=SelectionPackage(row.PackageName);
            entry=entry with { PackageName=rowPackage,Installed=row.Installed,Launchable=row.Launchable,
                Selectable=row.Installed && row.Launchable && rowPackage is not null };
        }
        var tags=typed.Command is RustyKioskCommand.AddTag or RustyKioskCommand.RemoveTag;
        if(tags && key is not null && !BrowserDirectCommands.IsWellFormedUtf16(key))throw new InvalidDataException("direct_selected_key_invalid");
        var search=typed.Command==RustyKioskCommand.SetSearch?BoundedText(state.Search):null;
        var filter=typed.Command==RustyKioskCommand.FilterTag?BoundedText(state.TagFilter ?? string.Empty):null;
        return new(typed.Schema,typed.RequestId,typed.Command.ToWireName(),typed.Accepted,typed.Completed,state.ControlsOpen,key,package,
            state.SelectedInstalled,state.SelectedLaunchable,requirement,state.PendingRequirementLaunch,state.GuardArmed,state.SetupHelperInstalled,state.SetupHelperReady,entry)
        {
            RequestWifiAdbAfterBoot=typed.Command is RustyKioskCommand.EnableWifiAfterBoot or RustyKioskCommand.DisableWifiAfterBoot ? state.RequestWifiAdbAfterBoot : null,
            AccessibilityEnabled=typed.Command is RustyKioskCommand.EnableAccessibility or RustyKioskCommand.DisableAccessibility ? state.AccessibilityEnabled : null,
            SystemPassthroughEnabled=typed.Command is RustyKioskCommand.PassthroughNatural or RustyKioskCommand.PassthroughContour ? state.SystemPassthroughEnabled : null,
            PassthroughStyle=typed.Command is RustyKioskCommand.PassthroughNatural or RustyKioskCommand.PassthroughContour ? state.PassthroughStyle?.ToWireName() : null,
            PassthroughLutApplied=typed.Command==RustyKioskCommand.PassthroughContour ? state.PassthroughLutApplied : null,
            WifiAdbEnabled=typed.Command is RustyKioskCommand.RequestWifiAdb or RustyKioskCommand.DisableWifiAdb ? state.WifiAdbEnabled : null,
            Search=search,
            TagFilter=filter,
            ValueMatches=typed.Command is RustyKioskCommand.SetSearch or RustyKioskCommand.FilterTag ? matches : null,
            RequestedTagPresent=tags && entry is not null ? selected[0].Tags.Contains(value!,StringComparer.OrdinalIgnoreCase) : null,
            LastDispatchedOptionId=typed.Command==RustyKioskCommand.LaunchOption && state.LastDispatchedOptionId is { Length:<=RustyKioskContract.MaxCommandValueLength } id && BrowserDirectCommands.IsWellFormedUtf16(id) ? id : null,
            LastDispatchedOptionPackage=typed.Command==RustyKioskCommand.LaunchOption ? SelectionPackage(state.LastDispatchedOptionPackage) : null
        };
    }
    private static string? SelectionPackage(string? value)
    {
        if(value is null || value.Length>256)return null;
        try { return AndroidInput.RequirePackageName(value)==value ? value : null; }
        catch(ArgumentException) { return null; }
    }
    private static string BoundedText(string? value) => value is not null && value.Length<=RustyKioskContract.MaxCommandValueLength && BrowserDirectCommands.IsWellFormedUtf16(value)
        ? value : throw new InvalidDataException("direct_text_proof_invalid");
}
