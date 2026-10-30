using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LocalApiV3LaunchContractTests
{
    private const string Operation = "api-11111111111111111111111111111111";
    private const string Output = "diag-22222222222222222222222222222222";
    private const string PlanDigest = "0cea6ab058514a8569d577782ebccf4cbd546a10c99f3d317cfbfd5b251f71db";
    private const string Digest = "5c8c7d942fad33ca3a5c508fef4bec6b7a307e7251608a69a1bdac56aca76f26";
    // Independently authored and hashed; preserve the exact old diagnostic golden separately.
    private const string Canonical = """
        {"contractVersion":"questionable.file_manager.local_api.v3","action":"apk.launch-diagnose","policyVersion":"exact-installed-launch-diagnostic-output.v1","target":"QUEST-01","command":{"kind":"LaunchDiagnoseInspectedApp","arguments":["apk","launch-diagnose","--serial","QUEST-01","--file","retained://api-11111111111111111111111111111111/base.apk","--output","retained-output://api-11111111111111111111111111111111/diag-22222222222222222222222222222222"]},"resources":[{"role":"apk","name":"base.apk","sizeBytes":4,"sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","identity":{"packageName":"com.example.app","versionCode":42,"versionName":"1.2.3","signerSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","splitName":null}},{"role":"diagnostic-output","outputId":"diag-22222222222222222222222222222222","archiveFormat":"zip-store.v1","plannedLeafDigest":"0cea6ab058514a8569d577782ebccf4cbd546a10c99f3d317cfbfd5b251f71db","limits":{"maximumProcessLogs":8,"recentLogLinesPerCapture":400,"maximumPayloadFiles":14,"maximumPayloadFileBytes":262144,"maximumPayloadBytes":3670016,"maximumArchiveBytes":4194304,"reservedOutputBytes":8388608,"reservedOutputFiles":15,"maximumRequiredStandardOutputBytes":262144,"maximumRequiredStandardErrorBytes":262144,"maximumOptionalStandardOutputBytes":262144,"maximumOptionalStandardErrorBytes":262144,"maximumInputArtifactBytes":1073741824,"maximumBinaryFramingBytes":256,"maximumProcessTimeoutSeconds":300,"maximumArmedCaptureBytes":262144,"postActionWindowMilliseconds":10000,"maximumLaunchOperationSeconds":300}}]}
        """;
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LaunchCanonicalAndPlanMatchIndependentLiteralAndSha256Golden()
    {
        var intent = LocalApiDiagnosticOutputIntent.RestoreForLaunch(Operation, Output, PlanDigest);
        var bytes = LocalApiV3Contract.GetV3CanonicalCommandBytes(Command(intent), Artifact(), intent);
        Assert.Equal(Canonical, Encoding.UTF8.GetString(bytes));
        Assert.Equal(Digest, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical))).ToLowerInvariant());
        Assert.Equal(Digest, LocalApiV3Contract.ComputeV3CommandDigest(Command(intent), Artifact(), intent));
        Assert.Equal(PlanDigest, intent.PlannedLeafDigest);
    }

    [Fact]
    public void NewLimitsCannotCrossIntoOldPolicyOrBeBroadenedOrOmittedFromLaunchPolicy()
    {
        var intent = LocalApiV3OutputIntent.FromIntent(LocalApiDiagnosticOutputIntent.CreateForLaunch(Operation));
        foreach (var property in typeof(LocalApiV3OutputLimits).GetProperties())
        {
            var changed = intent.Limits with { };
            var value = property.GetValue(changed)!;
            property.SetValue(changed, value is int integer ? (object)(integer + 1) : (long)value + 1);
            Assert.Throws<LocalApiException>((intent with { Limits = changed }).Validate);
            if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
            {
                property.SetValue(changed, null);
                Assert.Throws<LocalApiException>((intent with { Limits = changed }).Validate);
            }
        }
        Assert.Throws<LocalApiException>((intent with { PolicyVersion = LocalApiDiagnosticOutputIntent.PolicyVersion }).Validate);
        Assert.Throws<LocalApiException>((intent with { Limits = LocalApiV3OutputLimits.Current }).Validate);
    }

    [Fact]
    public void LaunchDigestNormalizesOnlyPrivatePathsWhileBindingTargetAndImmutableArtifactFacts()
    {
        var intent = LocalApiDiagnosticOutputIntent.RestoreForLaunch(Operation, Output, PlanDigest);
        var path = Path.Combine(Path.GetTempPath(), "different-private-root", "staged", new string('4', 32) + ".apk");
        var moved = OperatorCommands.LaunchDiagnoseInspectedApp("QUEST-01", path, Path.Combine(Path.GetDirectoryName(path)!, "d-" + Output[5..]));
        Assert.Equal(Digest, LocalApiV3Contract.ComputeV3CommandDigest(moved, Artifact() with { Path = path }, intent));
        Assert.NotEqual(Digest, LocalApiV3Contract.ComputeV3CommandDigest(OperatorCommands.LaunchDiagnoseInspectedApp("quest-01", path, moved.OutputPath!), Artifact(), intent));
        foreach (var artifact in new[] { Artifact() with { SizeBytes = 5 }, Artifact() with { Sha256 = new string('c', 64) },
            Artifact() with { Identity = Artifact().Identity with { PackageName = "com.example.other" } },
            Artifact() with { Identity = Artifact().Identity with { VersionCode = 43 } },
            Artifact() with { Identity = Artifact().Identity with { VersionName = null } },
            Artifact() with { Identity = Artifact().Identity with { SignerSha256 = new string('c', 64) } } })
            Assert.NotEqual(Digest, LocalApiV3Contract.ComputeV3CommandDigest(moved, artifact, intent));
    }

    [Fact]
    public void PreflightAdmitsOnlyExactNewActionWithoutAddingCallerControls()
    {
        new LocalApiV3PreflightRequest(LocalApiV3Contract.Version, "apk.launch-diagnose", "base.apk", "QUEST-01").Validate();
        foreach (var action in new[] { "apk.Launch-diagnose", "apk.launch-diagnose ", "apk.launch-diagnose\n", "app.launch-resolved" })
            Assert.Throws<LocalApiException>(() => new LocalApiV3PreflightRequest(LocalApiV3Contract.Version, action, "base.apk", "QUEST-01").Validate());
        Assert.Equal(new[] { "ContractVersion", "Command", "ApkPath", "Serial", "ExpiresInSeconds" },
            typeof(LocalApiV3PreflightRequest).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void LaunchCanonicalBindsActionPolicyKindLogicalPathsAndAllEighteenOrderedLimits()
    {
        var intent = LocalApiDiagnosticOutputIntent.CreateForLaunch(Operation);
        var command = Command(intent);
        var bytes = LocalApiV3Contract.GetV3CanonicalCommandBytes(command, Artifact(), intent);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        Assert.Equal("apk.launch-diagnose", root.GetProperty("action").GetString());
        Assert.Equal(LocalApiDiagnosticOutputIntent.LaunchPolicyVersion, root.GetProperty("policyVersion").GetString());
        Assert.Equal("LaunchDiagnoseInspectedApp", root.GetProperty("command").GetProperty("kind").GetString());
        var args = root.GetProperty("command").GetProperty("arguments").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Equal($"retained://{Operation}/base.apk", args[5]);
        Assert.Equal($"retained-output://{Operation}/{intent.OutputId}", args[7]);
        Assert.DoesNotContain(command.LocalPath!, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        var limits = root.GetProperty("resources")[1].GetProperty("limits");
        Assert.Equal(new[] { "maximumProcessLogs", "recentLogLinesPerCapture", "maximumPayloadFiles", "maximumPayloadFileBytes",
            "maximumPayloadBytes", "maximumArchiveBytes", "reservedOutputBytes", "reservedOutputFiles",
            "maximumRequiredStandardOutputBytes", "maximumRequiredStandardErrorBytes", "maximumOptionalStandardOutputBytes",
            "maximumOptionalStandardErrorBytes", "maximumInputArtifactBytes", "maximumBinaryFramingBytes", "maximumProcessTimeoutSeconds",
            "maximumArmedCaptureBytes", "postActionWindowMilliseconds", "maximumLaunchOperationSeconds" }, limits.EnumerateObject().Select(v => v.Name));
        Assert.Equal(262144, limits.GetProperty("maximumArmedCaptureBytes").GetInt32());
        Assert.Equal(10000, limits.GetProperty("postActionWindowMilliseconds").GetInt32());
        Assert.Equal(300, limits.GetProperty("maximumLaunchOperationSeconds").GetInt32());
        var projection = LocalApiV3OutputIntent.FromIntent(intent);
        projection.Validate();
        new LocalApiV3PreflightResult(LocalApiV3Contract.Version, Operation, "apk.launch-diagnose", new string('b', 64), Now,
            LocalApiV3ApkFacts.FromArtifact(Artifact()), "QUEST-01", projection).Validate();
        Assert.Equal(limits.GetRawText(), JsonSerializer.Serialize(projection.Limits, JsonSerializerOptions.Web));
    }

    [Fact]
    public void IntentActionAndImmutableVectorCrossingsFailBeforeNormalization()
    {
        var launch = LocalApiDiagnosticOutputIntent.CreateForLaunch(Operation);
        var diagnostic = LocalApiDiagnosticOutputIntent.Create(Operation);
        Assert.Throws<LocalApiException>(() => LocalApiV3Contract.GetV3CanonicalCommandBytes(Command(launch), Artifact(), diagnostic));
        var wrongAction = OperatorCommands.DiagnoseInspectedApp("QUEST-01", Command(launch).LocalPath!, Command(launch).OutputPath!);
        Assert.Throws<LocalApiException>(() => LocalApiV3Contract.GetV3CanonicalCommandBytes(wrongAction, Artifact(), launch));
        var valid = Command(launch);
        foreach (var index in new[] { 1, 3, 5, 7 })
        {
            var args = valid.CliArguments.ToArray(); args[index] += "-crossed";
            var crossed = new OperatorCommand(valid.Kind, args, serial: valid.Serial, localPath: valid.LocalPath, outputPath: valid.OutputPath);
            Assert.Throws<LocalApiException>(() => LocalApiV3Contract.GetV3CanonicalCommandBytes(crossed, Artifact(), launch));
        }
    }

    [Fact]
    public void LaunchStatusMembersAreNullableInitAndAbsentFromOldJson()
    {
        foreach (var name in new[] { "LaunchSummary", "Mutation", "LaunchMayHaveDispatched" })
        {
            var member = typeof(LocalApiV3OperationStatus).GetProperty(name);
            Assert.NotNull(member);
            Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit), member.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        }
        var intent = LocalApiV3OutputIntent.FromIntent(LocalApiDiagnosticOutputIntent.Create(Operation));
        var old = new LocalApiV3OperationStatus(LocalApiV3Contract.Version, Operation, "apk.diagnose", new string('b', 64), Now,
            LocalApiOperationStage.Preflighted, false, "QUEST-01", new(intent, "preflighted", false, 16, 0));
        old.Validate();
        var json = JsonSerializer.Serialize(old, JsonSerializerOptions.Web);
        foreach (var name in new[] { "launchSummary", "mutation", "launchMayHaveDispatched" }) Assert.DoesNotContain(name, json);
    }

    [Theory]
    [InlineData(ApkLaunchDiagnosticDisposition.Completed, true, OperatorMutationStage.Confirmed, LocalApiOperationStage.Completed)]
    [InlineData(ApkLaunchDiagnosticDisposition.LaunchPending, true, OperatorMutationStage.Pending, LocalApiOperationStage.OutcomeUnknownRecoveryRequired)]
    [InlineData(ApkLaunchDiagnosticDisposition.OutcomeUnknown, true, OperatorMutationStage.Pending, LocalApiOperationStage.OutcomeUnknownRecoveryRequired)]
    [InlineData(ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch, false, OperatorMutationStage.Rejected, LocalApiOperationStage.Failed)]
    [InlineData(ApkLaunchDiagnosticDisposition.OutcomeUnknown, false, OperatorMutationStage.Rejected, LocalApiOperationStage.OutcomeUnknownRecoveryRequired)]
    public void ProjectionJoinsActualResultAndReceiptWithoutReclassifyingNoDispatchUnknown(
        ApkLaunchDiagnosticDisposition disposition, bool attempted, OperatorMutationStage stage, LocalApiOperationStage ownerStage)
    {
        var raw = Result(disposition, attempted, stage);
        var proofType = typeof(LocalApiV3Contract).Assembly.GetType("QuestIonAbleFileManager.Core.LocalApiV3LaunchProof");
        Assert.NotNull(proofType);
        var project = proofType.GetMethod("Project", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(project);
        var tuple = project.Invoke(null, [raw, "QUEST-01", true])!;
        var summary = tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        var mutation = Assert.IsType<OperatorMutationReceipt>(tuple.GetType().GetField("Item2")!.GetValue(tuple));
        Assert.Equal(ownerStage, tuple.GetType().GetField("Item3")!.GetValue(tuple));
        Assert.Equal(raw.MutationReceipt!.OperationId, mutation.OperationId);
        Assert.Equal(raw.MutationReceipt.Stage, mutation.Stage);
        Assert.Equal(raw.MutationReceipt.HeadsetReadback, mutation.HeadsetReadback);
        Assert.Equal(raw.MutationReceipt.Transitions.Select(v => (v.Stage, v.At)), mutation.Transitions.Select(v => (v.Stage, v.At)));
        Assert.Equal(Now, summary.GetType().GetProperty("CapturedAt")!.GetValue(summary));
        var json = JsonSerializer.Serialize(new { summary, mutation }, JsonSerializerOptions.Web);
        Assert.DoesNotContain("PRIVATE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("11001", json, StringComparison.Ordinal);
        Assert.Contains("\"applicationReadiness\":\"unknown\"", json);
        Assert.Contains("\"wearerVisibilityAuthority\":false", json);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= 4096);
    }

    [Theory]
    [InlineData("commandKind")]
    [InlineData("commandTarget")]
    [InlineData("receiptMissing")]
    [InlineData("receiptTarget")]
    [InlineData("receiptKind")]
    [InlineData("receiptId")]
    [InlineData("receiptStage")]
    [InlineData("receiptReadback")]
    [InlineData("transitionOrder")]
    [InlineData("transitionTime")]
    [InlineData("transitionCount")]
    [InlineData("captureOverflow")]
    [InlineData("captureNegative")]
    [InlineData("captureWindow")]
    [InlineData("captureLimit")]
    [InlineData("captureEarly")]
    [InlineData("captureCleanup")]
    [InlineData("processCount")]
    [InlineData("processOverflow")]
    [InlineData("installedTarget")]
    [InlineData("installedBytes")]
    [InlineData("contract")]
    [InlineData("disposition")]
    public void CrossedCoreProofCannotBecomeSafeAcknowledgement(string crossing)
    {
        var raw = Result(ApkLaunchDiagnosticDisposition.Completed, true, OperatorMutationStage.Confirmed);
        var receipt = raw.MutationReceipt!;
        var launch = raw.ApkLaunchDiagnosticBundleResult!;
        raw = crossing switch
        {
            "commandKind" => raw with { Command = OperatorCommands.DiagnoseInspectedApp("QUEST-01", raw.Command.LocalPath!, raw.Command.OutputPath!) },
            "commandTarget" => raw with { Command = OperatorCommands.LaunchDiagnoseInspectedApp("QUEST-02", raw.Command.LocalPath!, raw.Command.OutputPath!) },
            "receiptMissing" => raw with { MutationReceipt = null },
            "receiptTarget" => raw with { MutationReceipt = receipt with { Target = "QUEST-02" } },
            "receiptKind" => raw with { MutationReceipt = receipt with { CommandKind = OperatorCommandKind.DiagnoseInspectedApp } },
            "receiptId" => raw with { MutationReceipt = receipt with { OperationId = receipt.OperationId + "\n" } },
            "receiptStage" => raw with { MutationReceipt = receipt with { Stage = OperatorMutationStage.Pending, Transitions = [.. receipt.Transitions.Take(2)] } },
            "receiptReadback" => raw with { MutationReceipt = receipt with { HeadsetReadback = false } },
            "transitionOrder" => raw with { MutationReceipt = receipt with { Transitions = [receipt.Transitions[1], receipt.Transitions[0], receipt.Transitions[2]] } },
            "transitionTime" => raw with { MutationReceipt = receipt with { Transitions = [receipt.Transitions[0], receipt.Transitions[1] with { At = Now.AddSeconds(-1) }, receipt.Transitions[2]] } },
            "transitionCount" => raw with { MutationReceipt = receipt with { Transitions = [.. receipt.Transitions, receipt.Transitions[2]] } },
            "captureOverflow" => raw with { ApkLaunchDiagnosticBundleResult = launch with { Capture = launch.Capture with { SizeBytes = 262145 } } },
            "captureNegative" => raw with { ApkLaunchDiagnosticBundleResult = launch with { Capture = launch.Capture with { SizeBytes = -1 } } },
            "captureWindow" => raw with { ApkLaunchDiagnosticBundleResult = launch with { Capture = launch.Capture with { PostActionWindowElapsed = false } } },
            "captureLimit" => raw with { ApkLaunchDiagnosticBundleResult = launch with { Capture = launch.Capture with { OutputLimitReached = true } } },
            "captureEarly" => raw with { ApkLaunchDiagnosticBundleResult = launch with { Capture = launch.Capture with { CaptureExitedEarly = true } } },
            "captureCleanup" => raw with { ApkLaunchDiagnosticBundleResult = launch with { Capture = launch.Capture with { ProcessTreeCleanupSucceeded = false } } },
            "processCount" => raw with { ApkLaunchDiagnosticBundleResult = launch with { Attempt = launch.Attempt with { CurrentPackageProcessIds = [] } } },
            "processOverflow" => raw with { ApkLaunchDiagnosticBundleResult = launch with { Attempt = launch.Attempt with { CurrentPackageProcessIds = Enumerable.Range(1, LocalApiDiagnosticOutputIntent.MaximumRequiredStandardOutputBytes + 1).ToArray() } } },
            "installedTarget" => raw with { ApkLaunchDiagnosticBundleResult = launch with { InstalledBeforeDispatch = launch.InstalledBeforeDispatch with { Serial = "QUEST-02" } } },
            "installedBytes" => raw with { ApkLaunchDiagnosticBundleResult = launch with { InstalledAfterCapture = launch.InstalledAfterCapture! with { BaseApkSizeBytes = 5 } } },
            "contract" => raw with { ApkLaunchDiagnosticBundleResult = launch with { DiagnosticContract = "ready" } },
            _ => raw with { ApkLaunchDiagnosticBundleResult = launch with { Disposition = (ApkLaunchDiagnosticDisposition)99 } }
        };
        Assert.Throws<LocalApiException>(() => LocalApiV3LaunchProof.Project(raw, "QUEST-01", true));
    }

    [Theory]
    [InlineData(ApkLaunchDiagnosticDisposition.Completed, true, OperatorMutationStage.Confirmed)]
    [InlineData(ApkLaunchDiagnosticDisposition.LaunchPending, true, OperatorMutationStage.Pending)]
    [InlineData(ApkLaunchDiagnosticDisposition.OutcomeUnknown, true, OperatorMutationStage.Pending)]
    [InlineData(ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch, false, OperatorMutationStage.Rejected)]
    [InlineData(ApkLaunchDiagnosticDisposition.OutcomeUnknown, false, OperatorMutationStage.Rejected)]
    public void ArchiveAvailabilityNeverUpgradesActualLaunchOutcome(
        ApkLaunchDiagnosticDisposition disposition, bool attempted, OperatorMutationStage stage)
    {
        var proof = LocalApiV3LaunchProof.Project(Result(disposition, attempted, stage), "QUEST-01", true);
        var intent = LocalApiV3OutputIntent.FromIntent(LocalApiDiagnosticOutputIntent.CreateForLaunch(Operation));
        var archive = new LocalApiV3ArchiveDescriptor(intent.OutputId, "apk-diagnostics.zip", 123, new string('a', 64), Now);
        var status = new LocalApiV3OperationStatus(LocalApiV3Contract.Version, Operation, "apk.launch-diagnose", new string('b', 64), Now,
            proof.Stage, true, "QUEST-01", new(intent, "available", true, 16, 0, null, archive))
        { LaunchSummary = proof.Summary, Mutation = proof.Mutation, LaunchMayHaveDispatched = true };
        status.Validate();
        Assert.Throws<LocalApiException>((status with { Consumed = false }).Validate);
        Assert.Throws<LocalApiException>((status with { LaunchMayHaveDispatched = false }).Validate);
        Assert.Throws<LocalApiException>((status with { LaunchSummary = null }).Validate);
        Assert.Throws<LocalApiException>((status with { Mutation = null }).Validate);
        Assert.Throws<LocalApiException>((status with { Stage = LocalApiOperationStage.Cancelled }).Validate);
        Assert.Throws<LocalApiException>((status with { DiagnosticSummary = new("questionable.file_manager.apk_diagnostic_bundle.v3", Now, "complete", 2, 0, 0) }).Validate);
        var unavailable = status with { Output = new(intent, "unavailable", false, 16, 0), Stage = LocalApiOperationStage.OutcomeUnknownRecoveryRequired };
        unavailable.Validate();
        if (disposition is ApkLaunchDiagnosticDisposition.Completed or ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch)
            Assert.Throws<LocalApiException>((status with { Stage = LocalApiOperationStage.OutcomeUnknownRecoveryRequired }).Validate);
        if (proof.Stage != LocalApiOperationStage.Completed)
            Assert.Throws<LocalApiException>((status with { Stage = LocalApiOperationStage.Completed }).Validate);
    }

    [Fact]
    public void ConservativeMarkerWithoutResultRemainsUnknownAndCannotManufactureReceiptOrFailure()
    {
        var intent = LocalApiV3OutputIntent.FromIntent(LocalApiDiagnosticOutputIntent.CreateForLaunch(Operation));
        var status = new LocalApiV3OperationStatus(LocalApiV3Contract.Version, Operation, "apk.launch-diagnose", new string('b', 64), Now,
            LocalApiOperationStage.OutcomeUnknownRecoveryRequired, true, "QUEST-01", new(intent, "unavailable", false, 16, 0))
        { LaunchMayHaveDispatched = true };
        status.Validate();
        foreach (var stage in new[] { LocalApiOperationStage.Completed, LocalApiOperationStage.Cancelled, LocalApiOperationStage.Failed, LocalApiOperationStage.Preflighted })
            Assert.Throws<LocalApiException>((status with { Stage = stage }).Validate);
        Assert.Throws<LocalApiException>(() => LocalApiV3LaunchProof.Project(Result(ApkLaunchDiagnosticDisposition.Completed, true, OperatorMutationStage.Confirmed), "QUEST-01", false));
    }

    [Fact]
    public void RestoredSafeReceiptAcceptsOnlyNormalizedClosedStringsAndBoundedActualHistory()
    {
        var proof = LocalApiV3LaunchProof.Project(Result(ApkLaunchDiagnosticDisposition.Completed, true, OperatorMutationStage.Confirmed), "QUEST-01", true);
        foreach (var receipt in new[] { proof.Mutation with { DesiredState = "PRIVATE desired" },
            proof.Mutation with { ObservedState = "PRIVATE observed" },
            proof.Mutation with { Transitions = [.. proof.Mutation.Transitions.Take(2), proof.Mutation.Transitions[2] with { Message = "PRIVATE transition" }] } })
            Assert.Throws<LocalApiException>(() => LocalApiV3LaunchProof.Validate(proof.Summary, receipt, "QUEST-01", true, proof.Stage));
        var largeSerial = new string('Q', 4096);
        var tooLarge = proof.Mutation with { Target = largeSerial };
        Assert.Throws<LocalApiException>(() => LocalApiV3LaunchProof.Validate(proof.Summary, tooLarge, largeSerial, true, proof.Stage));
        var noDispatchUnknown = LocalApiV3LaunchProof.Project(Result(ApkLaunchDiagnosticDisposition.OutcomeUnknown, false, OperatorMutationStage.Rejected), "QUEST-01", true);
        Assert.Throws<LocalApiException>(() => LocalApiV3LaunchProof.Validate(noDispatchUnknown.Summary, noDispatchUnknown.Mutation,
            "QUEST-01", true, LocalApiOperationStage.Failed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualPostReadbackFailureRetainsPartialOrDriftedAfterFactsAsUnknown(bool partial)
    {
        var raw = Result(ApkLaunchDiagnosticDisposition.OutcomeUnknown, true, OperatorMutationStage.Pending);
        var launch = raw.ApkLaunchDiagnosticBundleResult!;
        var after = launch.InstalledAfterCapture!;
        after = partial ? after with { Identity = null } : after with { BaseApkSizeBytes = 5, BaseApkSha256 = new string('c', 64) };
        raw = raw with { ApkLaunchDiagnosticBundleResult = launch with
        {
            InstalledAfterCapture = after,
            OwnedPostReadbackFailure = "A fixed device readback was malformed, ambiguous, or drifted."
        } };
        var proof = LocalApiV3LaunchProof.Project(raw, "QUEST-01", true);
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, proof.Stage);
        Assert.Equal("outcomeUnknown", proof.Summary.Disposition);
        Assert.Equal(OperatorMutationStage.Pending, proof.Mutation.Stage);
        Assert.Equal(raw.MutationReceipt!.OperationId, proof.Mutation.OperationId);
        Assert.Throws<LocalApiException>(() => LocalApiV3LaunchProof.Project(raw with
        { ApkLaunchDiagnosticBundleResult = raw.ApkLaunchDiagnosticBundleResult with { InstalledAfterCapture = after with { Serial = "QUEST-02" } } }, "QUEST-01", true));
    }

    [Fact]
    public void SafeProcessCountUsesExistingRequiredReadbackByteCap()
    {
        var proof = LocalApiV3LaunchProof.Project(Result(ApkLaunchDiagnosticDisposition.OutcomeUnknown, true, OperatorMutationStage.Pending), "QUEST-01", true);
        (proof.Summary with { CurrentPackageProcessCount = 4097 }).Validate();
        (proof.Summary with { CurrentPackageProcessCount = LocalApiDiagnosticOutputIntent.MaximumRequiredStandardOutputBytes }).Validate();
        Assert.Throws<LocalApiException>((proof.Summary with
        { CurrentPackageProcessCount = LocalApiDiagnosticOutputIntent.MaximumRequiredStandardOutputBytes + 1 }).Validate);
    }

    private static OperatorExecutionResult Result(ApkLaunchDiagnosticDisposition disposition, bool attempted, OperatorMutationStage stage)
    {
        var artifact = Artifact();
        var installed = new InstalledApkIdentity("QUEST-01", artifact.Identity, ["/PRIVATE/base.apk"], artifact.Sha256, artifact.SizeBytes);
        var command = Command(LocalApiDiagnosticOutputIntent.CreateForLaunch(Operation));
        var transitions = attempted
            ? new[] { new OperatorMutationTransition(OperatorMutationStage.Sent, Now, "PRIVATE sent"), new(OperatorMutationStage.Pending, Now, "PRIVATE pending"), new(stage, Now, "PRIVATE final") }
            : [new OperatorMutationTransition(stage, Now, "PRIVATE final")];
        var receipt = new OperatorMutationReceipt("pc-" + new string('1', 32), command.Kind, command.Serial!, "PRIVATE desired", stage,
            "PRIVATE observed", true, transitions);
        var launch = new ApkLaunchDiagnosticBundleResult(artifact, installed, installed, 11001, 11001, "PRIVATE fence", Now, "PRIVATE epoch",
            new(attempted, null, null, "PRIVATE failure", attempted ? [11001] : []),
            new("logcat-uid-post-fence.txt", 4, new string('a', 64), true, false, false, true, 0), disposition, "PRIVATE detail",
            "PRIVATE output", "launch-diagnostic-manifest.json", 4, new string('b', 64), false, "PRIVATE leaf");
        return new(command, MutationReceipt: receipt, ApkLaunchDiagnosticBundleResult: launch);
    }

    private static OperatorCommand Command(LocalApiDiagnosticOutputIntent intent)
    {
        var path = Path.Combine(Path.GetTempPath(), "qfm-v3-launch-canonical", "staged", new string('3', 32) + ".apk");
        return OperatorCommands.LaunchDiagnoseInspectedApp("QUEST-01", path, Path.Combine(Path.GetDirectoryName(path)!, "d-" + intent.OutputId[5..]));
    }
    private static ApkArtifactInspection Artifact() => new("private-source.apk", 4, new string('b', 64),
        new("com.example.app", 42, "1.2.3", new string('a', 64), null));
}
