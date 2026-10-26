using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed record BrowserArtifactReadOutcome(BrowserArtifactReceipt Artifact, string Serial, string Kind,
    string? Verification = null, BrowserApkPreflightFacts? Preflight = null,
    AppRuntimeObservation? Runtime = null, DevelopmentAppRuntimeObservation? Development = null);

public sealed record BrowserApkReadSuggestion(string Purpose, bool Ready);

// CLI vectors are intentionally absent: they contain a private host source path
// and describe operations that are not admitted by a read-only browser result.
public sealed record BrowserApkPreflightFacts(ApkArtifactInspection Artifact, ApkArtifactManifestFacts Manifest,
    string Serial, QuestDevice? Device, int? DeviceApiLevel, InstalledApkMatch InstalledMatch,
    InstalledApkIdentity? Installed, string? LauncherComponent, bool ReadyForDeploy, bool ReadyForLaunch,
    bool ReadyForDiagnose, IReadOnlyList<ApkPreflightCheck> Checks, IReadOnlyList<BrowserApkReadSuggestion> NextCommands,
    string PreflightContract);

/// <summary>Closed typed facts from a live immutable source lease. Never publishes a native command/result vector.</summary>
internal sealed class BrowserArtifactReadProjection
{
    private readonly BrowserArtifactLease _lease;
    private readonly (string Private, string Public)[] _replacements;

    public BrowserArtifactReadProjection(BrowserArtifactLease lease)
    {
        _lease = lease;
        _replacements = new[]
        {
            (lease.Path, lease.Receipt.FileName),
            (Path.GetDirectoryName(lease.Path)!, "[private-staging]"),
            (Path.Combine(Path.GetTempPath(), "QuestIonAbleFileManager.ApkAdmission"), "[private-admission]")
        }.SelectMany(pair => new[] { pair, (pair.Item1.Replace('\\', '/'), pair.Item2), (pair.Item1.Replace('/', '\\'), pair.Item2) })
         .Distinct().ToArray();
    }

    public BrowserArtifactReadOutcome? Project(BrowserArtifactCommandRequest request, OperatorExecutionResult result)
    {
        var expected = request.ToOperatorCommand(_lease);
        if (result.Command.Kind != expected.Kind || result.Command.Serial != request.Serial ||
            !string.Equals(result.Command.LocalPath, _lease.Path, StringComparison.Ordinal) || request.Serial is null)
            return null;
        if (request.Action == "apk.preflight" && result.ApkPreflightResult is { } preflight)
        {
            if (!Bound(preflight.Artifact) || preflight.Serial != request.Serial ||
                (preflight.Device is not null && preflight.Device.Serial != request.Serial) ||
                (preflight.Installed is not null && preflight.Installed.Serial != request.Serial) ||
                (preflight.InstalledMatch == InstalledApkMatch.Exact && !ExactInstalled(preflight.Installed, preflight.Artifact, request.Serial)))
                return null;
            var facts = new BrowserApkPreflightFacts(Inspection(preflight.Artifact),
                preflight.Manifest with { LauncherActivities = Strings(preflight.Manifest.LauncherActivities) },
                request.Serial, preflight.Device is null ? null : preflight.Device with
                { Model = Optional(preflight.Device.Model), Product = Optional(preflight.Device.Product), State = Text(preflight.Device.State) },
                preflight.DeviceApiLevel, preflight.InstalledMatch, Installed(preflight.Installed), Optional(preflight.LauncherComponent),
                preflight.ReadyForDeploy, preflight.ReadyForLaunch, preflight.ReadyForDiagnose,
                preflight.Checks.Select(check => check with { Id = Text(check.Id), Detail = Text(check.Detail) }).ToArray(),
                preflight.NextCommands.Select(next => new BrowserApkReadSuggestion(Text(next.Purpose), next.Ready)).ToArray(),
                Text(preflight.PreflightContract));
            return new(_lease.Receipt, request.Serial, "preflight", Preflight: facts);
        }
        if (request.Action == "runtime.observe" && request.Verification == "exact" && result.AppRuntimeObservation is { } runtime)
        {
            if (!Bound(runtime.Artifact) || (runtime.Installed is not null && !ExactInstalled(runtime.Installed, runtime.Artifact, request.Serial)))
                return null;
            return new(_lease.Receipt, request.Serial, "runtime", "exact", Runtime: Runtime(runtime));
        }
        if (request.Action == "runtime.observe" && request.Verification == "development-metadata" && result.DevelopmentAppRuntimeObservation is { } development)
        {
            var identity = development.ExpectedArtifact.Identity;
            var metadata = development.InstalledMetadata;
            if (!Bound(development.ExpectedArtifact) || !Bound(development.Runtime.Artifact) ||
                development.Runtime.Artifact.Identity != identity || development.Runtime.Installed is not null ||
                metadata.PackageName != identity.PackageName || metadata.VersionCode != identity.VersionCode || metadata.VersionName != identity.VersionName ||
                development.ReportedInstallReference is not null)
                return null;
            var facts = development with
            {
                ExpectedArtifact = Inspection(development.ExpectedArtifact), Runtime = Runtime(development.Runtime),
                InstalledMetadata = metadata with
                { PackageName = Text(metadata.PackageName), VersionName = Optional(metadata.VersionName), ApkPaths = Strings(metadata.ApkPaths), LastUpdateTime = Text(metadata.LastUpdateTime) }
            };
            return new(_lease.Receipt, request.Serial, "runtime", "development-metadata", Development: facts);
        }
        return null;
    }

    private bool Bound(ApkArtifactInspection artifact) => artifact.Sha256 == _lease.Receipt.Sha256 &&
        artifact.SizeBytes == _lease.Receipt.SizeBytes && string.Equals(artifact.Path, _lease.Path, StringComparison.Ordinal);

    private static bool ExactInstalled(InstalledApkIdentity? installed, ApkArtifactInspection artifact, string serial) =>
        installed is not null && installed.Serial == serial && installed.BaseApkSha256 == artifact.Sha256 &&
        installed.BaseApkSizeBytes == artifact.SizeBytes && installed.Identity == artifact.Identity;

    public ApkArtifactInspection Inspection(ApkArtifactInspection artifact) => artifact with
    { Path = _lease.Receipt.FileName, Identity = Identity(artifact.Identity) };

    private ApkArtifactIdentity Identity(ApkArtifactIdentity identity) => identity with
    { PackageName = Text(identity.PackageName), VersionName = Optional(identity.VersionName), SignerSha256 = Text(identity.SignerSha256), SplitName = Optional(identity.SplitName) };

    private InstalledApkIdentity? Installed(InstalledApkIdentity? installed) => installed is null ? null : installed with
    {
        Serial = Text(installed.Serial), Identity = installed.Identity is null ? null : Identity(installed.Identity),
        ApkPaths = Strings(installed.ApkPaths), BaseApkSha256 = Text(installed.BaseApkSha256),
        VerificationMethod = Text(installed.VerificationMethod), VerificationFallbackReason = Optional(installed.VerificationFallbackReason)
    };

    private AppRuntimeObservation Runtime(AppRuntimeObservation runtime) => runtime with
    {
        Artifact = Inspection(runtime.Artifact), Installed = Installed(runtime.Installed),
        ObservationContract = Text(runtime.ObservationContract), ForegroundComponents = Strings(runtime.ForegroundComponents),
        TopResumedComponents = Strings(runtime.TopResumedComponents), BlockingSystemComponents = Strings(runtime.BlockingSystemComponents),
        ActivityObservationSource = Text(runtime.ActivityObservationSource), ProcessObservationSource = Text(runtime.ProcessObservationSource),
        CurrentFocus = Focus(runtime.CurrentFocus), FocusedApp = Focus(runtime.FocusedApp),
        GlobalFocus = runtime.GlobalFocus with
        { CurrentFocus = FocusRecord(runtime.GlobalFocus.CurrentFocus), FocusedApp = FocusRecord(runtime.GlobalFocus.FocusedApp), ObservationContract = Text(runtime.GlobalFocus.ObservationContract) },
        ApplicationReadiness = Text(runtime.ApplicationReadiness), OpenXrReadiness = Text(runtime.OpenXrReadiness)
    };

    private AndroidGlobalFocusFact Focus(AndroidGlobalFocusFact focus) => focus with
    { Component = Optional(focus.Component), ObservationSource = Text(focus.ObservationSource) };
    private AndroidGlobalFocusRecord FocusRecord(AndroidGlobalFocusRecord focus) => focus with
    { Components = Strings(focus.Components), ObservationSource = Text(focus.ObservationSource) };
    private string[] Strings(IEnumerable<string> values) => values.Select(Text).ToArray();
    private string? Optional(string? value) => value is null ? null : Text(value);
    private string Text(string value)
    {
        foreach (var pair in _replacements) value = value.Replace(pair.Private, pair.Public, StringComparison.OrdinalIgnoreCase);
        return value;
    }
}
