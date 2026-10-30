using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserDirectLaunchOption(int SchemaVersion,string OptionId,string DisplayLabel,string Description);
public sealed record BrowserDirectLaunchOptions(string State,string? PackageName,string? BindingSha256,IReadOnlyList<BrowserDirectLaunchOption> Options);

/// <summary>A bounded observation of options already returned by this Select, never launch authority.</summary>
internal static class BrowserDirectLaunchOptionsProjection
{
    private static BrowserDirectLaunchOptions Empty(string state) => new(state,null,null,Array.Empty<BrowserDirectLaunchOption>());

    internal static BrowserDirectLaunchOptions Capture(RustyKioskState state,BrowserDirectSelectionReview review)
    {
        var rows=state.SelectedLaunchOptions;
        if(state.SelectedLaunchOptionsStatus is null)
            return Empty(state.SelectedLaunchOptionsBinding is null && (rows is null || rows.Count==0) ? "missing" : "unavailable");
        if(state.SelectedLaunchOptionsStatus is RustyKioskLaunchOptionsStatus.None or RustyKioskLaunchOptionsStatus.Rejected)
            return Empty(state.SelectedLaunchOptionsBinding is null && (rows is null || rows.Count==0)
                ? state.SelectedLaunchOptionsStatus.Value.ToWireName() : "unavailable");
        if(state.SelectedLaunchOptionsStatus!=RustyKioskLaunchOptionsStatus.Ready || rows is { Count: > RustyKioskContract.MaxLaunchOptions } ||
            !BindingValid(state.SelectedLaunchOptionsBinding,state.SelectedPackage,review.PackageName))return Empty("unavailable");
        var captured=new List<BrowserDirectLaunchOption>(); var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in rows ?? [])
        {
            if(row is null || row.SchemaVersion!=RustyKioskContract.LaunchOptionSchemaVersion ||
                !Text(row.OptionId,RustyKioskContract.MaxCommandValueLength,true) || !ids.Add(row.OptionId) ||
                !Text(row.DisplayLabel,RustyKioskContract.MaxLaunchOptionLabelLength,true) ||
                !Text(row.Description,RustyKioskContract.MaxLaunchOptionDescriptionLength,false))return Empty("unavailable");
            captured.Add(new(row.SchemaVersion,row.OptionId,row.DisplayLabel,row.Description));
        }
        return new("ready",review.PackageName,state.SelectedLaunchOptionsBinding!.BindingSha256,captured.AsReadOnly());
    }

    private static bool Text(string? value,int maximum,bool nonblank) => value is not null && value.Length<=maximum &&
        (!nonblank || !string.IsNullOrWhiteSpace(value)) && BrowserDirectCommands.IsWellFormedUtf16(value);

    private static bool BindingValid(RustyKioskLaunchOptionsBinding? value,string? selectedPackage,string expectedPackage)
    {
        if(value is null || value.PackageName!=selectedPackage || value.PackageName!=expectedPackage || value.Uid<=0 || value.VersionCode<0 ||
            value.LastUpdateTimeMilliseconds<=0 || !Text(value.PackageName,255,true) || !Text(value.ProviderAuthority,255,true) ||
            !Text(value.ProviderClass,512,true) || !Text(value.OwnerActivity,512,true) || !Text(value.SigningIdentity,2048,true) ||
            !Text(value.BindingSha256,64,true) || !Regex.IsMatch(value.SigningIdentity,"^[0-9a-f]{64}(,[0-9a-f]{64})*$",RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(value.BindingSha256,"^[0-9a-f]{64}$",RegexOptions.CultureInvariant))return false;
        // Owner order differs from the record constructor: update time precedes version.
        var canonical=string.Join('\0',RustyKioskContract.AppLaunchOptionsSchema,value.PackageName,value.Uid.ToString(CultureInfo.InvariantCulture),
            value.SigningIdentity,value.LastUpdateTimeMilliseconds.ToString(CultureInfo.InvariantCulture),value.VersionCode.ToString(CultureInfo.InvariantCulture),
            value.ProviderAuthority,value.ProviderClass,value.OwnerActivity);
        var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return string.Equals(digest,value.BindingSha256,StringComparison.Ordinal);
    }
}
