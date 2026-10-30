using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LocalApiV3ContractTests
{
    private const string Operation = "api-11111111111111111111111111111111";
    private const string Output = "diag-22222222222222222222222222222222";
    private const string PlanDigest = "4f109825aca0feb4a2600fa9f26a60d0f5e0227193d2025e0f3d134489c2e6f0";
    private const string Digest = "2e5565d7d045ea15c1b28b1b1f17e208aa85f057b61ec4ebac6790c95767ed0e";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    // Authored independently of the helper; freeze names, order, nulls, integers and all 15 limits.
    private const string Canonical = """
        {"contractVersion":"questionable.file_manager.local_api.v3","action":"apk.diagnose","policyVersion":"exact-installed-diagnostic-output.v1","target":"QUEST-01","command":{"kind":"DiagnoseInspectedApp","arguments":["apk","diagnose","--serial","QUEST-01","--file","retained://api-11111111111111111111111111111111/base.apk","--output","retained-output://api-11111111111111111111111111111111/diag-22222222222222222222222222222222"]},"resources":[{"role":"apk","name":"base.apk","sizeBytes":4,"sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","identity":{"packageName":"com.example.app","versionCode":42,"versionName":"1.2.3","signerSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","splitName":null}},{"role":"diagnostic-output","outputId":"diag-22222222222222222222222222222222","archiveFormat":"zip-store.v1","plannedLeafDigest":"4f109825aca0feb4a2600fa9f26a60d0f5e0227193d2025e0f3d134489c2e6f0","limits":{"maximumProcessLogs":8,"recentLogLinesPerCapture":400,"maximumPayloadFiles":14,"maximumPayloadFileBytes":262144,"maximumPayloadBytes":3670016,"maximumArchiveBytes":4194304,"reservedOutputBytes":8388608,"reservedOutputFiles":15,"maximumRequiredStandardOutputBytes":262144,"maximumRequiredStandardErrorBytes":262144,"maximumOptionalStandardOutputBytes":262144,"maximumOptionalStandardErrorBytes":262144,"maximumInputArtifactBytes":1073741824,"maximumBinaryFramingBytes":256,"maximumProcessTimeoutSeconds":300}}]}
        """;

    [Fact]
    public void DigestMatchesIndependentLiteralAndSha256Golden()
    {
        var bytes = LocalApiV3Contract.GetV3CanonicalCommandBytes(Command(), Artifact(), Intent());
        Assert.Equal(Canonical, Encoding.UTF8.GetString(bytes));
        Assert.Equal(Digest, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical))).ToLowerInvariant());
        Assert.Equal(Digest, LocalApiV3Contract.ComputeV3CommandDigest(Command(), Artifact(), Intent()));
    }

    [Fact]
    public void SourceToStagedArtifactPathAdvanceAndPrivateRootRelocationDoNotChangeDigest()
    {
        var artifact = Artifact();
        Assert.Equal(Digest, Hash(Command(), artifact with { Path = "source-before-copy.apk" }));
        var movedInput = Path.Combine(Path.GetTempPath(), "other-private-root", "staged", new string('3', 32) + ".apk");
        var moved = OperatorCommands.DiagnoseInspectedApp("QUEST-01", movedInput,
            Path.Combine(Path.GetDirectoryName(movedInput)!, "d-" + Output[5..]));
        Assert.Equal(Digest, Hash(moved, artifact with { Path = movedInput }));
        Assert.DoesNotContain(Path.GetDirectoryName(movedInput)!, Encoding.UTF8.GetString(
            LocalApiV3Contract.GetV3CanonicalCommandBytes(moved, artifact, Intent())), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("sha")]
    [InlineData("package")]
    [InlineData("versionCode")]
    [InlineData("versionName")]
    [InlineData("nullVersionName")]
    [InlineData("signer")]
    [InlineData("versionControl")]
    public void EveryArtifactFactChangesTheDigest(string fact)
    {
        var artifact = Artifact();
        artifact = fact switch
        {
            "size" => artifact with { SizeBytes = 5 },
            "sha" => artifact with { Sha256 = new string('c', 64) },
            "package" => artifact with { Identity = artifact.Identity with { PackageName = "com.example.other" } },
            "versionCode" => artifact with { Identity = artifact.Identity with { VersionCode = 43 } },
            "versionName" => artifact with { Identity = artifact.Identity with { VersionName = "1.2.4" } },
            "nullVersionName" => artifact with { Identity = artifact.Identity with { VersionName = null } },
            "signer" => artifact with { Identity = artifact.Identity with { SignerSha256 = new string('c', 64) } },
            _ => artifact with { Identity = artifact.Identity with { VersionName = "1.2.3\n" } }
        };
        Assert.NotEqual(Digest, Hash(Command(), artifact));
    }

    [Fact]
    public void TargetCaseAndNewIntentIdsAreBoundWithoutGenericTextReplacement()
    {
        var command = OperatorCommands.DiagnoseInspectedApp("quest-01", Command().LocalPath!, Command().OutputPath!);
        Assert.NotEqual(Digest, Hash(command, Artifact()));
        var intent = LocalApiDiagnosticOutputIntent.Create(Operation);
        command = OperatorCommands.DiagnoseInspectedApp("QUEST-01", Command().LocalPath!,
            Path.Combine(Path.GetDirectoryName(Command().LocalPath)!, "d-" + intent.OutputId[5..]));
        Assert.NotEqual(Digest, LocalApiV3Contract.ComputeV3CommandDigest(command, Artifact(), intent));
        var otherOperation = LocalApiDiagnosticOutputIntent.Create("api-" + new string('4', 32));
        command = OperatorCommands.DiagnoseInspectedApp("QUEST-01", command.LocalPath!,
            Path.Combine(Path.GetDirectoryName(command.LocalPath)!, "d-" + otherOperation.OutputId[5..]));
        Assert.NotEqual(Digest, LocalApiV3Contract.ComputeV3CommandDigest(command, Artifact(), otherOperation));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("extraArgument")]
    [InlineData("sourceArgument")]
    [InlineData("outputArgument")]
    [InlineData("foreignStem")]
    [InlineData("otherDirectory")]
    [InlineData("options")]
    [InlineData("serialControl")]
    [InlineData("pathControl")]
    [InlineData("inputCase")]
    public void OnlyTheExactImmutableFactoryVectorAndGeneratedSiblingPathsAreAccepted(string crossing)
    {
        var valid = Command();
        var arguments = valid.CliArguments.ToArray();
        if (crossing == "sourceArgument") arguments[5] = "other.apk";
        if (crossing == "outputArgument") arguments[7] += "-other";
        var local = valid.LocalPath!;
        var output = valid.OutputPath!;
        if (crossing == "foreignStem") output += "-other";
        if (crossing == "otherDirectory") output = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(local))!, "d-" + Output[5..]);
        if (crossing == "pathControl") local += "\n";
        if (crossing == "inputCase") local = Path.Combine(Path.GetDirectoryName(local)!, new string('A', 32) + ".apk");
        // Keep the crossed path/property pair self-consistent: rejection must
        // prove the owner namespace, rather than an incidental vector mismatch.
        if (crossing is "pathControl" or "inputCase") arguments[5] = local;
        if (crossing is "foreignStem" or "otherDirectory") arguments[7] = output;
        if (crossing == "serialControl") arguments[3] = "QUEST-01\n";
        var command = new OperatorCommand(crossing == "kind" ? OperatorCommandKind.ObserveInspectedApp : valid.Kind,
            crossing == "extraArgument" ? [.. arguments, "--filter", "all"] : arguments,
            serial: crossing == "serialControl" ? "QUEST-01\n" : valid.Serial,
            localPath: local, outputPath: output,
            installOptions: crossing == "options" ? new ApkInstallOptions() : null);
        Assert.Throws<LocalApiException>(() => Hash(command, Artifact()));
    }

    [Theory]
    [InlineData("sizeZero")]
    [InlineData("sizeOverflow")]
    [InlineData("shaCase")]
    [InlineData("shaControl")]
    [InlineData("packageControl")]
    [InlineData("signer")]
    [InlineData("versionCode")]
    [InlineData("split")]
    public void InvalidArtifactFactsDoNotEnterCanonicalEvidence(string scenario)
    {
        var artifact = Artifact();
        artifact = scenario switch
        {
            "sizeZero" => artifact with { SizeBytes = 0 },
            "sizeOverflow" => artifact with { SizeBytes = 1073741825 },
            "shaCase" => artifact with { Sha256 = new string('B', 64) },
            "shaControl" => artifact with { Sha256 = artifact.Sha256 + "\n" },
            "packageControl" => artifact with { Identity = artifact.Identity with { PackageName = "com.example.app\n" } },
            "signer" => artifact with { Identity = artifact.Identity with { SignerSha256 = "missing" } },
            "versionCode" => artifact with { Identity = artifact.Identity with { VersionCode = 0 } },
            _ => artifact with { Identity = artifact.Identity with { SplitName = "config.en" } }
        };
        Assert.Throws<LocalApiException>(() => Hash(Command(), artifact));
    }

    [Fact]
    public void PublicProjectionIsPathlessAndOmitsAllPrivateOutputAuthority()
    {
        var intent = LocalApiV3OutputIntent.FromIntent(Intent());
        var artifact = LocalApiV3ApkFacts.FromArtifact(Artifact());
        var preflight = new LocalApiV3PreflightResult(LocalApiV3Contract.Version, Operation, "apk.diagnose", Digest,
            Now.AddMinutes(2), artifact, "QUEST-01", intent);
        preflight.Validate();
        var output = new LocalApiV3OutputStatus(intent, "preflighted", false, 16, 0, null, null);
        output.Validate();
        var status = new LocalApiV3OperationStatus(LocalApiV3Contract.Version, Operation, "apk.diagnose", Digest,
            Now.AddMinutes(2), LocalApiOperationStage.Preflighted, false, "QUEST-01", output);
        status.Validate();
        var json = JsonSerializer.Serialize(new { preflight, status }, JsonSerializerOptions.Web);
        foreach (var secret in new[] { "payloadLeaves", "archiveLeaf", "nativeIdentity", "fileIndex", "volumeSerialNumber", "cliArguments", "localPath", "outputPath", "mutation", "rawResult", "uid", "pid", Artifact().Path })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "outputId", "policyVersion", "archiveFormat", "plannedLeafDigest", "limits" },
            JsonSerializer.SerializeToElement(intent, JsonSerializerOptions.Web).EnumerateObject().Select(property => property.Name));
        Assert.Equal(15, JsonSerializer.SerializeToElement(intent.Limits, JsonSerializerOptions.Web).EnumerateObject().Count());
    }

    [Fact]
    public void OriginalAndCommittedAvailabilityFieldsCannotBeCrossedInPublicStatus()
    {
        var intent = LocalApiV3OutputIntent.FromIntent(Intent());
        var archive = new LocalApiV3ArchiveDescriptor(Output, "apk-diagnostics.zip", 123, new string('c', 64), Now);
        var summary = new LocalApiV3DiagnosticSummary("questionable.file_manager.apk_diagnostic_bundle.v3", Now, "complete", 6, 0, 0);
        var status = new LocalApiV3OperationStatus(LocalApiV3Contract.Version, Operation, "apk.diagnose", Digest,
            Now, LocalApiOperationStage.Completed, true, "QUEST-01", new(intent, "available", true, 16, 0, null, archive), summary);
        status.Validate();
        Assert.Throws<LocalApiException>((status with { Consumed = false }).Validate);
        Assert.Throws<LocalApiException>((status with { Stage = LocalApiOperationStage.Running }).Validate);
        Assert.Throws<LocalApiException>((status with { DiagnosticSummary = null }).Validate);
        Assert.Throws<LocalApiException>((status with { Stage = (LocalApiOperationStage)999 }).Validate);
        Assert.Throws<LocalApiException>((status with { OperationId = "api-" + new string('4', 32) }).Validate);
        var preflight = new LocalApiV3PreflightResult(LocalApiV3Contract.Version, "api-" + new string('4', 32),
            "apk.diagnose", Digest, Now, LocalApiV3ApkFacts.FromArtifact(Artifact()), "QUEST-01", intent);
        Assert.Throws<LocalApiException>(preflight.Validate);
        var capabilities = new LocalApiV3Capabilities(LocalApiV3Contract.Version, ["apk.diagnose"],
            LocalApiContract.MaximumRequestBytes, 120, LocalApiV3OutputLimits.Current);
        Assert.Equal(15, JsonSerializer.SerializeToElement(capabilities, JsonSerializerOptions.Web)
            .GetProperty("outputLimits").EnumerateObject().Count());
    }

    [Fact]
    public void EveryPublicLimitIsFrozenAndCannotBeBroadenedOrSilentlyOmitted()
    {
        var limits = LocalApiV3OutputIntent.FromIntent(Intent()).Limits;
        limits.Validate();
        var properties = typeof(LocalApiV3OutputLimits).GetProperties();
        Assert.Equal(15, properties.Count(property => Nullable.GetUnderlyingType(property.PropertyType) is null));
        Assert.Equal(new[] { "MaximumArmedCaptureBytes", "PostActionWindowMilliseconds", "MaximumLaunchOperationSeconds" },
            properties.Where(property => Nullable.GetUnderlyingType(property.PropertyType) is not null).Select(property => property.Name));
        foreach (var property in properties)
        {
            var changed = limits with { };
            var value = property.GetValue(changed);
            if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
            {
                Assert.Null(value);
                property.SetValue(changed, 1);
            }
            else property.SetValue(changed, value is int integer ? (object)(integer + 1) : (long)value! + 1);
            Assert.Throws<LocalApiException>(changed.Validate);
        }
        Assert.Equal(15, JsonSerializer.SerializeToElement(limits, JsonSerializerOptions.Web).EnumerateObject().Count());
    }

    [Theory]
    [InlineData("idCase")]
    [InlineData("idControl")]
    [InlineData("idZero")]
    [InlineData("planCase")]
    [InlineData("policy")]
    [InlineData("format")]
    public void SafeIntentRejectsAliasesAndOtherPolicies(string scenario)
    {
        var intent = LocalApiV3OutputIntent.FromIntent(Intent());
        intent = scenario switch
        {
            "idCase" => intent with { OutputId = Output.ToUpperInvariant() },
            "idControl" => intent with { OutputId = Output + "\n" },
            "idZero" => intent with { OutputId = "diag-" + new string('0', 32) },
            "planCase" => intent with { PlannedLeafDigest = PlanDigest.ToUpperInvariant() },
            "policy" => intent with { PolicyVersion = "other" },
            _ => intent with { ArchiveFormat = "other" }
        };
        Assert.Throws<LocalApiException>(intent.Validate);
    }

    [Theory]
    [InlineData("phase")]
    [InlineData("availability")]
    [InlineData("descriptorBeforeAvailable")]
    [InlineData("descriptorMissing")]
    [InlineData("descriptorCrossed")]
    [InlineData("counts")]
    [InlineData("errorProse")]
    public void OutputProjectionCannotClaimUncommittedOrCrossedAvailability(string scenario)
    {
        var intent = LocalApiV3OutputIntent.FromIntent(Intent());
        var descriptor = new LocalApiV3ArchiveDescriptor(Output, "apk-diagnostics.zip", 123, new string('c', 64), Now);
        var output = new LocalApiV3OutputStatus(intent, "available", true, 16, 0, null, descriptor);
        output.Validate();
        output = scenario switch
        {
            "phase" => output with { Phase = "Available" },
            "availability" => output with { Phase = "sealed" },
            "descriptorBeforeAvailable" => output with { Phase = "sealed", Available = false },
            "descriptorMissing" => output with { Archive = null },
            "descriptorCrossed" => output with { Archive = descriptor with { OutputId = "diag-" + new string('4', 32) } },
            "counts" => output with { DebtCount = 17 },
            _ => output with { ErrorCode = "native path: /private/state" }
        };
        Assert.Throws<LocalApiException>(output.Validate);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("zero")]
    [InlineData("overflow")]
    [InlineData("sha")]
    public void DescriptorIsOnlyTheFixedBoundedDownloadReference(string scenario)
    {
        var descriptor = new LocalApiV3ArchiveDescriptor(Output, "apk-diagnostics.zip", 123, new string('c', 64), Now);
        descriptor = scenario switch
        {
            "name" => descriptor with { FileName = "../private.zip" },
            "zero" => descriptor with { SizeBytes = 0 },
            "overflow" => descriptor with { SizeBytes = 4194305 },
            _ => descriptor with { Sha256 = new string('C', 64) }
        };
        Assert.Throws<LocalApiException>(descriptor.Validate);
    }

    [Fact]
    public void SummaryOnlyDescribesCaptureCompletenessAndAlwaysKeepsReadinessUnknown()
    {
        var result = new ApkDiagnosticBundleResult(null!, null!, null!, null!, "private-state-must-not-escape", Now,
            [new("package", "package.txt", 10, "private-hash", 0),
             new("memory", "meminfo.txt", 10, "private-hash", 1) { Truncated = true }]);
        var summary = LocalApiV3DiagnosticSummary.FromResult(result);
        summary.Validate();
        Assert.Equal("partial", summary.CaptureState);
        Assert.Equal(2, summary.PayloadCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(1, summary.TruncatedCount);
        Assert.Equal("unknown", summary.ApplicationReadiness);
        Assert.False(summary.ApplicationReadinessAuthority);
        Assert.Equal("unknown", summary.OpenXrReadiness);
        Assert.False(summary.OpenXrReadinessAuthority);
        Assert.Equal("unknown", summary.WearerVisibility);
        Assert.False(summary.WearerVisibilityAuthority);
        var json = JsonSerializer.SerializeToUtf8Bytes(summary, JsonSerializerOptions.Web);
        Assert.InRange(json.Length, 1, 4096);
        Assert.DoesNotContain("private", Encoding.UTF8.GetString(json));
        Assert.Equal("complete", LocalApiV3DiagnosticSummary.FromResult(result with { Files = [result.Files[0]] }).CaptureState);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("failed")]
    [InlineData("truncated")]
    [InlineData("completeClaim")]
    [InlineData("contract")]
    [InlineData("oversized")]
    public void SummaryRejectsUnboundedOrContradictoryClaims(string scenario)
    {
        var summary = new LocalApiV3DiagnosticSummary("questionable.file_manager.apk_diagnostic_bundle.v3", Now, "complete", 6, 0, 0);
        summary = scenario switch
        {
            "count" => summary with { PayloadCount = 15 },
            "failed" => summary with { FailedCount = 7, CaptureState = "partial" },
            "truncated" => summary with { TruncatedCount = -1 },
            "completeClaim" => summary with { FailedCount = 1 },
            "contract" => summary with { DiagnosticContract = "ready" },
            _ => summary with { DiagnosticContract = new string('x', 4097) }
        };
        Assert.Throws<LocalApiException>(summary.Validate);
    }

    private static string Hash(OperatorCommand command, ApkArtifactInspection artifact) => LocalApiV3Contract.ComputeV3CommandDigest(command, artifact, Intent());
    private static LocalApiDiagnosticOutputIntent Intent() => LocalApiDiagnosticOutputIntent.Restore(Operation, Output, PlanDigest);
    private static OperatorCommand Command()
    {
        var source = Path.Combine(Path.GetTempPath(), "qfm-v3-canonical", "staged", new string('3', 32) + ".apk");
        return OperatorCommands.DiagnoseInspectedApp("QUEST-01", source, Path.Combine(Path.GetDirectoryName(source)!, "d-" + Output[5..]));
    }
    private static ApkArtifactInspection Artifact() => new("private-source-before-copy.apk", 4, new string('b', 64),
        new("com.example.app", 42, "1.2.3", new string('a', 64), null));
}
