using QuestIonAbleFileManager.Core;
using System.Text.Json.Serialization;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectCoreReceipt(string ActionId,KioskDirectOperatorAction Action,string RequestId,OperatorMutationStage Stage);
public sealed record BrowserDirectLifecycle(string RequestId,string OperationState,bool Accepted,bool Completed,long? EnqueuedAtMs,long? ExpiresAtMs);
public sealed record BrowserDirectSelectedEntry(string Key,string LaunchRequirement);
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
}
public sealed record BrowserDirectCommandOutcome(string ConnectionId,string ConnectRequestId,string UsbOperationId,string Serial,string ProductChannel,
    string Operation,string OriginalRequestId,string LogicalRequestId,string Command,string? Value,string OriginalEffect="unknown",bool ReconcilesOriginal=false,
    BrowserDirectCoreReceipt? CoreReceipt=null,BrowserDirectLifecycle? Lifecycle=null,BrowserDirectTypedResult? TypedResult=null,bool CancellationAcknowledged=false);

internal static class BrowserDirectCommandProjection
{
    internal static BrowserDirectCommandOutcome Metadata(BrowserDirectRequest request,BrowserDirectConnections.Entry entry,BrowserDirectCommandOutcome? original)
    {
        var operation=request.Action switch
        { "kiosk.direct.invoke"=>"invoke","kiosk.direct.request-status"=>"requestStatus","kiosk.direct.request-result"=>"requestResult","kiosk.direct.request-cancel"=>"requestCancel",_=>throw new InvalidDataException() };
        if(!request.Recovery)
        {
            var kind=request.Command ?? throw new BrowserRequestException("direct_command_unsupported");
            var name=kind.ToWireName();_ = BrowserDirectCommands.CommandName(name);BrowserDirectCommands.ValidateValue(kind,request.Value);
            return new(entry.Id,entry.Context.RequestId,entry.Context.UsbOperationId,entry.Context.Serial,entry.Context.ProductChannel,
                operation,request.RequestId,request.LogicalRequestId,name,request.Value);
        }
        if(original is null || original.Operation!="invoke" || original.OriginalRequestId!=request.OriginalRequestId || original.ConnectionId!=entry.Id ||
            original.ConnectRequestId!=entry.Context.RequestId || original.UsbOperationId!=entry.Context.UsbOperationId ||
            original.Serial!=entry.Context.Serial || original.ProductChannel!=entry.Context.ProductChannel ||
            original.LogicalRequestId!="kiosk_"+Guid.Parse(request.OriginalRequestId!).ToString("N"))
            throw new BrowserRequestException("direct_original_mismatch",409);
        BrowserDirectCommands.ValidateValue(BrowserDirectCommands.CommandName(original.Command),original.Value);
        return original with { Operation=operation,OriginalEffect="unknown",ReconcilesOriginal=false,CoreReceipt=null,Lifecycle=null,TypedResult=null,CancellationAcknowledged=false };
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
            var snapshot=Typed(typed);
            var launch=expected is RustyKioskCommand.LaunchNormal or RustyKioskCommand.LaunchKiosk;
            var missing=launch && snapshot.PendingRequirementLaunch is null || expected==RustyKioskCommand.SetLaunchRequirement && shared && snapshot.SelectedEntry is null;
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
    private static BrowserDirectTypedResult Typed(RustyKioskOperatorResult typed)
    {
        var state=typed.State;var key=state.SelectedKey;
        if(key is not null && (key.Length is < 1 or > 160 || key.Trim()!=key || key.Any(char.IsControl)))throw new InvalidDataException("direct_selected_key_invalid");
        var package=state.SelectedPackage;
        if(package is not null && (package.Length>256 || AndroidInput.RequirePackageName(package)!=package))throw new InvalidDataException("direct_selected_package_invalid");
        var requirement=state.SelectedLaunchRequirement?.ToWireName();
        var selected=key is null?[]:state.Entries.Where(entry=>entry.Key==key).Take(2).ToArray();
        var entry=selected.Length==1?new BrowserDirectSelectedEntry(key!,selected[0].LaunchRequirement.ToWireName()):null;
        return new(typed.Schema,typed.RequestId,typed.Command.ToWireName(),typed.Accepted,typed.Completed,state.ControlsOpen,key,package,
            state.SelectedInstalled,state.SelectedLaunchable,requirement,state.PendingRequirementLaunch,state.GuardArmed,state.SetupHelperInstalled,state.SetupHelperReady,entry)
        {
            RequestWifiAdbAfterBoot=typed.Command is RustyKioskCommand.EnableWifiAfterBoot or RustyKioskCommand.DisableWifiAfterBoot ? state.RequestWifiAdbAfterBoot : null,
            AccessibilityEnabled=typed.Command is RustyKioskCommand.EnableAccessibility or RustyKioskCommand.DisableAccessibility ? state.AccessibilityEnabled : null,
            SystemPassthroughEnabled=typed.Command is RustyKioskCommand.PassthroughNatural or RustyKioskCommand.PassthroughContour ? state.SystemPassthroughEnabled : null,
            PassthroughStyle=typed.Command is RustyKioskCommand.PassthroughNatural or RustyKioskCommand.PassthroughContour ? state.PassthroughStyle?.ToWireName() : null,
            PassthroughLutApplied=typed.Command==RustyKioskCommand.PassthroughContour ? state.PassthroughLutApplied : null
        };
    }
}
