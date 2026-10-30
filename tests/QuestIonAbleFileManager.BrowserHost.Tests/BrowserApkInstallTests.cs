using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserApkInstallTests
{
    private const string Serial = "QUEST123";
    private static readonly byte[] ApkBytes = [0x50, 0x4b, 0x03, 0x04];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task SingleApkTracerUsesOneRealCoreInstallAndExactInstalledBaseProof()
    {
        // This tests the Windows protected store; there is no advisory-lock substitute.
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await store.StageAsync("base.apk", new MemoryStream(ApkBytes));
        var runner = new InstallRunner();
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner,
            new AndroidBuildToolPaths("aapt2", "apksigner")));
        OperatorExecutionResult? actual = null;
        var commands = new BrowserCommands(async (command, cancellation) =>
        {
            actual = await BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation);
            return actual;
        }, () => store);
        var requestId = Guid.NewGuid().ToString("D");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            requestId, action = "apk.install", serial = Serial, source,
            options = new { replaceExisting = true, allowDowngrade = true,
                grantRuntimePermissions = true, allowTestPackages = true }, confirmed = true
        }, Json);

        // No Core warm-up: the sole browser request must initiate the actual pipeline.
        Assert.Empty(runner.Calls);
        var outcome = await commands.ExecuteAsync(body);

        Assert.Equal("completed", outcome.State);
        Assert.Null(outcome.Result);
        Assert.Null(outcome.Error);
        Assert.Equal(outcome, commands.Get(requestId));
        var install = Assert.Single(runner.Calls, call => call.Arguments.Contains("install"));
        Assert.Equal(new[] { "-s", Serial, "install", "-r", "-d", "-g", "-t" }, install.Arguments.Take(7));
        Assert.Equal(8, install.Arguments.Length);
        Assert.Equal(Timeout.InfiniteTimeSpan, install.Timeout);
        Assert.NotNull(actual);
        Assert.NotEqual(actual!.Command.LocalPath, install.Arguments[^1]);
        Assert.False(File.Exists(install.Arguments[^1]));
        Assert.Equal(OperatorCommandKind.InstallApk, actual.Command.Kind);
        Assert.Equal(Serial, actual.Command.Serial);
        Assert.Equal(2, runner.Calls.Count(call => call.FileName == "aapt2"));
        Assert.Equal(2, runner.Calls.Count(call => call.FileName == "apksigner"));
        Assert.Equal(2, runner.Calls.Count(call => call.Arguments.SequenceEqual(
            ["-s", Serial, "shell", "pm path 'com.example.app'"])));
        Assert.Single(runner.Calls, call => call.Arguments.Length == 6 &&
            call.Arguments[5].Contains("qfm-installed-digest:v1", StringComparison.Ordinal));
        Assert.Equal(source.Sha256, actual.InspectedApkInstallResult!.Installed.BaseApkSha256);
        Assert.Equal(source.SizeBytes, actual.InspectedApkInstallResult.Installed.BaseApkSizeBytes);
        Assert.Equal(actual.ApkArtifactInspection!.Identity, actual.InspectedApkInstallResult.Installed.Identity);
        Assert.Same(actual.CommandResult, actual.InspectedApkInstallResult.CommandResult);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(outcome, Json));
        var proof = document.RootElement.GetProperty("apkInstall");
        Assert.Equal(new[] { "contractVersion", "serial", "source", "options", "executionStarted",
            "installerCommandSucceeded", "exactInstalledBaseBytes", "artifactIdentity", "installed" }.Order(),
            proof.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("questionable.file_manager.browser_apk_install.v1", proof.GetProperty("contractVersion").GetString());
        Assert.Equal(Serial, proof.GetProperty("serial").GetString());
        Assert.True(proof.GetProperty("executionStarted").GetBoolean());
        Assert.True(proof.GetProperty("installerCommandSucceeded").GetBoolean());
        Assert.True(proof.GetProperty("exactInstalledBaseBytes").GetBoolean());
        Assert.Equal(source.Id, proof.GetProperty("source").GetProperty("id").GetString());
        Assert.Equal("base.apk", proof.GetProperty("source").GetProperty("fileName").GetString());
        Assert.Equal(source.SizeBytes, proof.GetProperty("source").GetProperty("sizeBytes").GetInt64());
        Assert.Equal(source.Sha256, proof.GetProperty("source").GetProperty("sha256").GetString());
        Assert.All(proof.GetProperty("options").EnumerateObject(), option => Assert.True(option.Value.GetBoolean()));
        var identity = proof.GetProperty("artifactIdentity");
        Assert.Equal(new[] { "packageName", "versionCode", "versionName", "signerSha256", "splitName" }.Order(),
            identity.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("com.example.app", identity.GetProperty("packageName").GetString());
        Assert.Equal("42", identity.GetProperty("versionCode").GetString());
        Assert.Equal("1.2.3", identity.GetProperty("versionName").GetString());
        Assert.Equal(new string('a', 64), identity.GetProperty("signerSha256").GetString());
        Assert.Equal(JsonValueKind.Null, identity.GetProperty("splitName").ValueKind);
        var installed = proof.GetProperty("installed");
        Assert.Equal(new[] { "serial", "identity", "baseApkSha256", "baseApkSizeBytes",
            "verificationMethod", "verificationFallbackReason" }.Order(),
            installed.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(Serial, installed.GetProperty("serial").GetString());
        Assert.Equal(identity.GetRawText(), installed.GetProperty("identity").GetRawText());
        Assert.Equal(source.Sha256, installed.GetProperty("baseApkSha256").GetString());
        Assert.Equal(source.SizeBytes, installed.GetProperty("baseApkSizeBytes").GetInt64());
        Assert.Equal("same-opened-handle-device-sha256", installed.GetProperty("verificationMethod").GetString());
        Assert.Equal(JsonValueKind.Null, installed.GetProperty("verificationFallbackReason").ValueKind);
        Assert.NotNull(outcome.Mutation);
        Assert.Equal(OperatorMutationStage.Confirmed, outcome.Mutation!.Stage);
        Assert.True(outcome.Mutation.HeadsetReadback);
        Assert.True(outcome.Mutation.IsTerminal);
        Assert.Equal(actual.MutationReceipt!.OperationId, outcome.Mutation.OperationId);
        Assert.Equal(OperatorCommandKind.InstallApk, outcome.Mutation.CommandKind);
        Assert.Equal(Serial, outcome.Mutation.Target);
        Assert.Equal("apk-install-exact-base", outcome.Mutation.DesiredState);
        Assert.Equal("exact-installed-base-bytes-and-identity", outcome.Mutation.ObservedState);
        Assert.Equal(new[] { OperatorMutationStage.Sent, OperatorMutationStage.Pending, OperatorMutationStage.Confirmed },
            outcome.Mutation.Transitions.Select(transition => transition.Stage));
        Assert.Equal(actual.MutationReceipt.Transitions.Select(transition => (transition.Stage, transition.At)),
            outcome.Mutation.Transitions.Select(transition => (transition.Stage, transition.At)));
        Assert.Equal(new[] { "apk-install-sent", "apk-install-pending", "apk-install-confirmed" },
            outcome.Mutation.Transitions.Select(transition => transition.Message));
        Assert.All(document.RootElement.EnumerateObject().Where(property => property.Name is not
            ("requestId" or "action" or "state" or "mutation" or "apkInstall")),
            property => Assert.Equal(JsonValueKind.Null, property.Value.ValueKind));
        Assert.DoesNotContain("private-output-marker", document.RootElement.GetRawText());
        Assert.DoesNotContain(install.Arguments[^1], document.RootElement.GetRawText());
        Assert.DoesNotContain("apkPaths", installed.GetRawText());
        Assert.Equal(1, await store.CleanupAsync());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    public async Task AllSixteenOptionsReachOneRealCoreInstall(int flags)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        var runner = new InstallRunner();
        var commands = Commands(store, runner);
        var options = new ApkInstallOptions((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0);
        var outcome = await commands.ExecuteAsync(Request(source, options));
        Assert.Equal("completed", outcome.State);
        Assert.Equal(options, outcome.ApkInstall!.Options);
        var call = Assert.Single(runner.Calls, call => call.Arguments.Contains("install"));
        List<string> expected = ["-s", Serial, "install"];
        if ((flags & 1) != 0) expected.Add("-r");
        if ((flags & 2) != 0) expected.Add("-d");
        if ((flags & 4) != 0) expected.Add("-g");
        if ((flags & 8) != 0) expected.Add("-t");
        Assert.Equal(expected, call.Arguments.Take(expected.Count));
        Assert.Equal(expected.Count + 1, call.Arguments.Length);
        Assert.Equal(Timeout.InfiniteTimeSpan, call.Timeout);
        Assert.Equal(1, await store.CleanupAsync());
    }

    public static IEnumerable<object[]> MalformedInputs() => new[]
    {
        "top-duplicate", "source-duplicate", "option-duplicate", "top-extra", "source-extra", "option-extra",
        "missing-source", "missing-confirmed", "missing-option", "missing-source-size", "source-array", "options-null",
        "option-string", "option-number", "option-null", "confirmation-false", "confirmation-string", "confirmation-number",
        "request-zero", "request-case", "request-format", "source-zero", "source-case", "source-format",
        "bad-serial", "serial-null", "serial-number", "reserved-name", "unicode-name", "traversal", "non-apk", "long-name",
        "hash-case", "hash-short", "hash-nonhex", "size-zero", "size-negative", "size-over", "size-fraction", "size-string"
    }.Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(MalformedInputs))]
    public async Task ClosedMalformedInputRejectsBeforeExecutorOrIdConsumption(string fault)
    {
        var source = new BrowserArtifactReceipt("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "base.apk", 4, new string('a', 64));
        const string id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        var text = Encoding.UTF8.GetString(Request(source, requestId: id));
        text = fault switch
        {
            "top-duplicate" => text.Insert(1, $"\"requestId\":\"{id}\","),
            "source-duplicate" => text.Replace("\"fileName\":", $"\"id\":\"{source.Id}\",\"fileName\":"),
            "option-duplicate" => text.Replace("\"allowDowngrade\":", "\"replaceExisting\":true,\"allowDowngrade\":"),
            "top-extra" => text.Insert(1, "\"hostPath\":\"private-output-marker\","),
            "source-extra" => text.Replace("\"fileName\":", "\"path\":\"private-output-marker\",\"fileName\":"),
            "option-extra" => text.Replace("\"allowDowngrade\":", "\"nativeArgs\":[],\"allowDowngrade\":"),
            "missing-source" => text.Replace("\"source\":" + JsonSerializer.Serialize(source, Json) + ",", ""),
            "missing-confirmed" => text.Replace(",\"confirmed\":true", ""),
            "missing-option" => text.Replace("\"allowDowngrade\":false,", ""),
            "missing-source-size" => text.Replace("\"sizeBytes\":4,", ""),
            "source-array" => text.Replace(JsonSerializer.Serialize(source, Json), "[]"),
            "options-null" => text.Replace(JsonSerializer.Serialize(new ApkInstallOptions(), Json), "null"),
            "option-string" => text.Replace("\"replaceExisting\":true", "\"replaceExisting\":\"true\""),
            "option-number" => text.Replace("\"replaceExisting\":true", "\"replaceExisting\":1"),
            "option-null" => text.Replace("\"replaceExisting\":true", "\"replaceExisting\":null"),
            "confirmation-false" => text.Replace("\"confirmed\":true", "\"confirmed\":false"),
            "confirmation-string" => text.Replace("\"confirmed\":true", "\"confirmed\":\"true\""),
            "confirmation-number" => text.Replace("\"confirmed\":true", "\"confirmed\":1"),
            "request-zero" => text.Replace(id, Guid.Empty.ToString("D")),
            "request-case" => text.Replace(id, id.ToUpperInvariant()),
            "request-format" => text.Replace(id, id.Replace("-", "")),
            "source-zero" => text.Replace(source.Id, Guid.Empty.ToString("N")),
            "source-case" => text.Replace(source.Id, source.Id.ToUpperInvariant()),
            "source-format" => text.Replace(source.Id, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "bad-serial" => text.Replace(Serial, "bad serial"),
            "serial-null" => text.Replace("\"serial\":\"QUEST123\"", "\"serial\":null"),
            "serial-number" => text.Replace("\"serial\":\"QUEST123\"", "\"serial\":123"),
            "reserved-name" => text.Replace("base.apk", "NUL.apk"),
            "unicode-name" => text.Replace("base.apk", "bаse.apk"),
            "traversal" => text.Replace("base.apk", "../base.apk"),
            "non-apk" => text.Replace("base.apk", "base.zip"),
            "long-name" => text.Replace("base.apk", new string('a', 125) + ".apk"),
            "hash-case" => text.Replace(source.Sha256, source.Sha256.ToUpperInvariant()),
            "hash-short" => text.Replace(source.Sha256, new string('a', 63)),
            "hash-nonhex" => text.Replace(source.Sha256, new string('g', 64)),
            "size-zero" => text.Replace("\"sizeBytes\":4", "\"sizeBytes\":0"),
            "size-negative" => text.Replace("\"sizeBytes\":4", "\"sizeBytes\":-1"),
            "size-over" => text.Replace("\"sizeBytes\":4", "\"sizeBytes\":1073741825"),
            "size-fraction" => text.Replace("\"sizeBytes\":4", "\"sizeBytes\":4.5"),
            "size-string" => text.Replace("\"sizeBytes\":4", "\"sizeBytes\":\"4\""),
            _ => throw new InvalidOperationException()
        };
        var calls = 0;
        var commands = new BrowserCommands((command, _) =>
        { calls++; return Task.FromResult(new OperatorExecutionResult(command)); });
        await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(0, calls);
        Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => commands.Get(id)).Code);
        Assert.Equal(id, BrowserApkInstallCommands.Parse(Request(source, requestId: id)).RequestId);
    }

    [Fact]
    public void MaximumShapeAndUtf8BodyBoundaryRemainClosed()
    {
        const string id = "11111111-1111-4111-8111-111111111111";
        var source = new BrowserArtifactReceipt(new string('a', 32), new string('a', 124) + ".apk", 1073741824, new string('a', 64));
        var serial = new string('A', 256);
        var body = Request(source, new(false, false, false, false), id, serial);
        var original = JsonSerializer.SerializeToUtf8Bytes(new
        { requestId = id, action = "apk.install", sessionId = id, serial, source, options = new ApkInstallOptions(false, false, false, false) }, Json);
        Assert.True(body.Length < 4096);
        Assert.True(original.Length < 4096);
        Assert.Equal(source, BrowserApkInstallCommands.Parse(body).Source);
        var boundary = body.Concat(Enumerable.Repeat((byte)' ', 4096 - body.Length)).ToArray();
        Assert.Equal(id, BrowserApkInstallCommands.Parse(boundary).RequestId);
        Assert.Equal("body_size_invalid", Assert.Throws<BrowserRequestException>(() =>
            BrowserApkInstallCommands.Parse(boundary.Concat(new byte[] { (byte)' ' }).ToArray())).Code);
        var utf8 = Encoding.UTF8.GetBytes("{\"extra\":\"" + new string('é', 2048) + "\"}");
        Assert.Equal("body_size_invalid", Assert.Throws<BrowserRequestException>(() => BrowserApkInstallCommands.Parse(utf8)).Code);
    }

    [Theory]
    [InlineData("missing")] [InlineData("name")] [InlineData("size")] [InlineData("hash")]
    public async Task SourceMismatchUnwindsAndCorrectedRequestCanReuseTheId(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        var invalid = fault switch
        {
            "missing" => source with { Id = Guid.NewGuid().ToString("N") },
            "name" => source with { FileName = "other.apk" },
            "size" => source with { SizeBytes = 1 },
            "hash" => source with { Sha256 = new string('0', 64) },
            _ => throw new InvalidOperationException()
        };
        var runner = new InstallRunner();
        var gate = new BrowserOperationGate();
        var commands = Commands(store, runner, gate: gate);
        var id = Guid.NewGuid().ToString("D");
        var rejected = await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(Request(invalid, requestId: id)));
        Assert.False(rejected.Dispatched);
        Assert.Empty(runner.Calls);
        Assert.False(gate.IsBusy);
        Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => commands.Get(id)).Code);
        Assert.Equal("completed", (await commands.ExecuteAsync(Request(source, requestId: id))).State);
        Assert.Single(runner.Calls, call => call.Arguments.Contains("install"));
        await store.RemoveAsync(source.Id);
    }

    [Fact]
    public void CapabilityAccessIsPureAndRequiresProtectedStagingFactory()
    {
        var calls = 0;
        var withFactory = new BrowserCommands((_, _) => throw new InvalidOperationException(), () =>
        { calls++; throw new InvalidOperationException("must stay lazy"); });
        var withoutFactory = new BrowserCommands((_, _) => throw new InvalidOperationException());
        Assert.Equal(OperatingSystem.IsWindows(), withFactory.AvailableActions.Contains("apk.install"));
        Assert.DoesNotContain("apk.install", withoutFactory.AvailableActions);
        Assert.Equal(0, calls);
        Assert.NotEqual(Guid.Empty.ToString("D"), withFactory.SessionId);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task BothCoreReadbackMethodsAcceptExactBaseAmongMultipleInstalledPaths(bool fallback, bool splits)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        var runner = new InstallRunner { FallbackDigest = fallback, MultipleInstalledPaths = splits };
        var outcome = await Commands(store, runner).ExecuteAsync(Request(source));
        Assert.Equal("completed", outcome.State);
        Assert.Equal(fallback ? "host-streamed-sha256" : "same-opened-handle-device-sha256", outcome.ApkInstall!.Installed!.VerificationMethod);
        Assert.Equal(fallback ? "remote-handle-digest-capability-unsupported" : null, outcome.ApkInstall.Installed.VerificationFallbackReason);
        Assert.Equal(fallback ? 2 : 1, runner.Calls.Count(call => call.Arguments.Contains("exec-out")));
        Assert.Single(runner.Calls, call => call.Arguments.Contains("install"));
        Assert.DoesNotContain("apkPaths", JsonSerializer.Serialize(outcome, Json));
    }

    [Theory]
    [InlineData("digest-failure", true)] [InlineData("digest-malformed", true)]
    [InlineData("body-mismatch", true)] [InlineData("paths-change", true)]
    [InlineData("pm-failure", true)] [InlineData("split", false)]
    [InlineData("tool", false)] [InlineData("not-ready", false)]
    [InlineData("zero-version", false)] [InlineData("negative-version", false)]
    [InlineData("overflow-version", false)] [InlineData("malformed-version", false)]
    public async Task RealCoreFailureAfterEntryRemainsUnknownWithoutCompletionFacts(string fault, bool nativeInstall)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        var runner = new InstallRunner { Failure = fault };
        var outcome = await Commands(store, runner).ExecuteAsync(Request(source));
        AssertUnknown(outcome);
        Assert.Equal(fault == "body-mismatch" ? "apk_install_binding_mismatch" : "apk_install_outcome_unknown", outcome.Error);
        Assert.Equal(nativeInstall ? 1 : 0, runner.Calls.Count(call => call.Arguments.Contains("install")));
        Assert.Equal(1, await store.CleanupAsync());
    }

    [Theory]
    [InlineData("9007199254740993", null)]
    [InlineData("9223372036854775807", "")]
    [InlineData("1", " version text ")]
    public async Task RealCoreMetadataPreservesInt64PrecisionAndLiteralNullableVersionName(string versionCode, string? versionName)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        var runner = new InstallRunner { VersionCode = versionCode, VersionName = versionName };
        var outcome = await Commands(store, runner).ExecuteAsync(Request(source));
        Assert.Equal("completed", outcome.State);
        Assert.Equal(versionCode, outcome.ApkInstall!.ArtifactIdentity!.VersionCode);
        Assert.Equal(versionName, outcome.ApkInstall.ArtifactIdentity.VersionName);
        Assert.Equal(outcome.ApkInstall.ArtifactIdentity, outcome.ApkInstall.Installed!.Identity);
    }

    [Theory]
    [InlineData(512, true)] [InlineData(513, false)]
    public async Task VersionNameBoundPreservesFullTextOrRefusesCompletion(int units, bool accepted)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        var runner = new InstallRunner { VersionName = new string('v', units) };
        var outcome = await Commands(store, runner).ExecuteAsync(Request(source));
        Assert.Equal(accepted ? "completed" : "outcomeUnknown", outcome.State);
        if (accepted) Assert.Equal(new string('v', units), outcome.ApkInstall!.ArtifactIdentity!.VersionName);
        else { AssertUnknown(outcome); Assert.Equal("apk_install_binding_mismatch", outcome.Error); }
    }

    public static IEnumerable<object[]> CrossedProofs() => new[]
    {
        "command-kind", "command-serial", "command-path", "command-options", "command-cli",
        "missing-install", "missing-inspection", "inspection-equality", "artifact-path", "artifact-hash", "artifact-size",
        "artifact-split", "artifact-package", "artifact-signer", "artifact-version-zero", "artifact-version-negative",
        "artifact-versionname-too-long", "common-result-reference", "native-exit", "native-serial", "native-flag",
        "native-extra", "native-source-path", "native-relative", "native-control", "native-unc", "native-normalization",
        "native-extension", "native-ads", "native-invalid-component", "native-reserved-component",
        "installed-serial", "installed-package", "installed-version", "installed-versionname", "installed-signer", "installed-split",
        "installed-hash", "installed-size", "method-unknown", "method-digest-fallback", "method-stream-no-fallback",
        "receipt-missing", "receipt-id-zero", "receipt-id-case", "receipt-id-format", "receipt-kind", "receipt-target",
        "receipt-desired", "receipt-stage", "receipt-readback", "receipt-history", "receipt-stage-order", "receipt-time-order",
        "receipt-time-default", "receipt-message-null", "receipt-observed-null", "receipt-final-stage"
    }.Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(CrossedProofs))]
    public async Task CrossedActualCoreProofCannotCompleteOrExposePrivateEvidence(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        var runner = new InstallRunner();
        var commands = Commands(store, runner, (command, result) => CrossProof(fault, command, result));
        var body = Request(source);
        var outcome = await commands.ExecuteAsync(body);
        AssertUnknown(outcome);
        Assert.Equal("apk_install_binding_mismatch", outcome.Error);
        Assert.Equal(outcome, commands.Get(BrowserApkInstallCommands.Parse(body).RequestId));
        Assert.Single(runner.Calls, call => call.Arguments.Contains("install"));
        var wire = JsonSerializer.Serialize(outcome, Json);
        Assert.DoesNotContain("/data/app/", wire);
        Assert.DoesNotContain(runner.Calls.Single(call => call.Arguments.Contains("install")).Arguments[^1], wire);
    }

    private static OperatorExecutionResult CrossProof(string fault, OperatorCommand command, OperatorExecutionResult result)
    {
        var install = result.InspectedApkInstallResult!;
        var artifact = install.Artifact;
        var installed = install.Installed;
        var identity = artifact.Identity;
        var receipt = result.MutationReceipt!;
        OperatorExecutionResult Artifact(ApkArtifactInspection changed) => result with
        { ApkArtifactInspection = changed, InspectedApkInstallResult = install with { Artifact = changed } };
        OperatorExecutionResult Identity(ApkArtifactIdentity changed) => result with
        { ApkArtifactInspection = artifact with { Identity = changed }, InspectedApkInstallResult = install with
            { Artifact = artifact with { Identity = changed }, Installed = installed with { Identity = changed } } };
        OperatorExecutionResult Installed(InstalledApkIdentity changed) => result with
        { InspectedApkInstallResult = install with { Installed = changed } };
        OperatorExecutionResult Dispatch(CommandResult changed) => result with
        { CommandResult = changed, InspectedApkInstallResult = install with { CommandResult = changed } };
        OperatorExecutionResult Receipt(OperatorMutationReceipt changed) => result with { MutationReceipt = changed };
        OperatorExecutionResult NativePath(string path) => Dispatch(install.CommandResult with
        { Arguments = [.. install.CommandResult.Arguments.SkipLast(1), path] });
        return fault switch
        {
            "command-kind" => result with { Command = OperatorCommands.DiscoverDevices() },
            "command-serial" => result with { Command = OperatorCommands.InstallApk("QUEST456", command.LocalPath!, command.InstallOptions) },
            "command-path" => result with { Command = OperatorCommands.InstallApk(Serial, command.LocalPath! + ".other.apk", command.InstallOptions) },
            "command-options" => result with { Command = OperatorCommands.InstallApk(Serial, command.LocalPath!, new(false, true, true, true)) },
            "command-cli" => result with { Command = CrossedCli(command) },
            "missing-install" => result with { InspectedApkInstallResult = null },
            "missing-inspection" => result with { ApkArtifactInspection = null },
            "inspection-equality" => result with { ApkArtifactInspection = artifact with { Sha256 = new string('0', 64) } },
            "artifact-path" => Artifact(artifact with { Path = "private-output-marker" }),
            "artifact-hash" => Artifact(artifact with { Sha256 = new string('0', 64) }),
            "artifact-size" => Artifact(artifact with { SizeBytes = 5 }),
            "artifact-split" => Identity(identity with { SplitName = "config.en" }),
            "artifact-package" => Identity(identity with { PackageName = " com.example.app" }),
            "artifact-signer" => Identity(identity with { SignerSha256 = new string('A', 64) }),
            "artifact-version-zero" => Identity(identity with { VersionCode = 0 }),
            "artifact-version-negative" => Identity(identity with { VersionCode = -1 }),
            "artifact-versionname-too-long" => Identity(identity with { VersionName = new string('v', 513) }),
            "common-result-reference" => result with { CommandResult = install.CommandResult with { } },
            "native-exit" => Dispatch(install.CommandResult with { ExitCode = 1 }),
            "native-serial" => Dispatch(install.CommandResult with { Arguments = ["-s", "QUEST456", "install", "-r", install.CommandResult.Arguments[^1]] }),
            "native-flag" => Dispatch(install.CommandResult with { Arguments = ["-s", Serial, "install", "-d", install.CommandResult.Arguments[^1]] }),
            "native-extra" => Dispatch(install.CommandResult with { Arguments = [.. install.CommandResult.Arguments, "private-output-marker"] }),
            "native-source-path" => NativePath(command.LocalPath!),
            "native-relative" => NativePath("admitted.apk"),
            "native-control" => NativePath(Path.Combine(Path.GetPathRoot(command.LocalPath!)!, "private\nmarker.apk")),
            "native-unc" => NativePath(@"\\server\share\admitted.apk"),
            "native-normalization" => NativePath(Path.GetDirectoryName(command.LocalPath!) + @"\..\admitted.apk"),
            "native-extension" => NativePath(command.LocalPath! + ".zip"),
            "native-ads" => NativePath(Path.Combine(Path.GetPathRoot(command.LocalPath!)!, "admitted:stream.apk")),
            "native-invalid-component" => NativePath(Path.Combine(Path.GetPathRoot(command.LocalPath!)!, "private|marker.apk")),
            "native-reserved-component" => NativePath(Path.Combine(Path.GetPathRoot(command.LocalPath!)!, "NUL.apk")),
            "installed-serial" => Installed(installed with { Serial = "QUEST456" }),
            "installed-package" => Installed(installed with { Identity = identity with { PackageName = "com.example.other" } }),
            "installed-version" => Installed(installed with { Identity = identity with { VersionCode = 43 } }),
            "installed-versionname" => Installed(installed with { Identity = identity with { VersionName = "1.2.3 " } }),
            "installed-signer" => Installed(installed with { Identity = identity with { SignerSha256 = new string('b', 64) } }),
            "installed-split" => Installed(installed with { Identity = identity with { SplitName = "config.en" } }),
            "installed-hash" => Installed(installed with { BaseApkSha256 = new string('0', 64) }),
            "installed-size" => Installed(installed with { BaseApkSizeBytes = 5 }),
            "method-unknown" => Installed(installed with { VerificationMethod = "private-output-marker" }),
            "method-digest-fallback" => Installed(installed with { VerificationFallbackReason = "remote-handle-digest-capability-unsupported" }),
            "method-stream-no-fallback" => Installed(installed with { VerificationMethod = "host-streamed-sha256", VerificationFallbackReason = null }),
            "receipt-missing" => result with { MutationReceipt = null },
            "receipt-id-zero" => Receipt(receipt with { OperationId = "pc-" + Guid.Empty.ToString("N") }),
            "receipt-id-case" => Receipt(receipt with { OperationId = "pc-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" }),
            "receipt-id-format" => Receipt(receipt with { OperationId = "pc-11111111-1111-4111-8111-111111111111" }),
            "receipt-kind" => Receipt(receipt with { CommandKind = OperatorCommandKind.InstallApkBundle }),
            "receipt-target" => Receipt(receipt with { Target = "QUEST456" }),
            "receipt-desired" => Receipt(receipt with { DesiredState = "inspected APK installed on QUEST123: base.apk" }),
            "receipt-stage" => Receipt(receipt with { Stage = OperatorMutationStage.Pending }),
            "receipt-readback" => Receipt(receipt with { HeadsetReadback = false }),
            "receipt-history" => Receipt(receipt with { Transitions = receipt.Transitions.Take(2).ToArray() }),
            "receipt-stage-order" => Receipt(receipt with { Transitions = receipt.Transitions.Reverse().ToArray() }),
            "receipt-time-order" => Receipt(receipt with { Transitions = receipt.Transitions.Select((transition, index) =>
                transition with { At = receipt.Transitions[0].At.AddTicks(-index) }).ToArray() }),
            "receipt-time-default" => Receipt(receipt with { Transitions = receipt.Transitions.Select(transition => transition with { At = default }).ToArray() }),
            "receipt-message-null" => Receipt(receipt with { Transitions = receipt.Transitions.Select(transition => transition with { Message = null! }).ToArray() }),
            "receipt-observed-null" => Receipt(receipt with { ObservedState = null! }),
            "receipt-final-stage" => Receipt(receipt with { Transitions = receipt.Transitions.Select((transition, index) =>
                index == 2 ? transition with { Stage = OperatorMutationStage.Pending } : transition).ToArray() }),
            _ => throw new InvalidOperationException()
        };
    }

    private static OperatorCommand CrossedCli(OperatorCommand original)
    {
        // Adversarial returned snapshot only: the immutable prepared command stays intact.
        var returned = OperatorCommands.InstallApk(original.Serial!, original.LocalPath!, original.InstallOptions);
        typeof(OperatorCommand).GetField("<CliArguments>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(returned, Array.AsReadOnly(new[] { "private-output-marker" }));
        return returned;
    }

    [Fact]
    public async Task NativePathShapeAllowsUnicodeAndSpaceAncestorsWithoutReopeningDisposedCoreFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        var commands = Commands(store, new InstallRunner(), (command, result) =>
        {
            var install = result.InspectedApkInstallResult!;
            var dispatch = install.CommandResult with { Arguments = [.. install.CommandResult.Arguments.SkipLast(1),
                Path.Combine(Path.GetPathRoot(command.LocalPath!)!, "Unicode Ω cache", "admitted.apk")] };
            return result with { CommandResult = dispatch, InspectedApkInstallResult = install with { CommandResult = dispatch } };
        });
        Assert.Equal("completed", (await commands.ExecuteAsync(Request(source))).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DrainJoinsFirstAcquisitionAndItsFailureUnwindBeforeSourceTeardown(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        using var stopping = new CancellationTokenSource();
        var source = await Stage(store);
        var entered = Signal(); var release = Signal();
        var capture = store.CaptureAsync("unrelated.apk", async (destination, _) =>
        { entered.SetResult(); await release.Task; await destination.WriteAsync(new byte[] { 9 }); });
        await entered.Task;
        var gate = new BrowserOperationGate();
        var calls = 0;
        var commands = new BrowserCommands((_, _) =>
        { calls++; throw new InvalidOperationException("must not enter Core"); }, () => store, gate);
        var body = Request(source);
        var request = commands.ExecuteAsync(body, stopping.Token);
        Assert.False(request.IsCompleted);
        Assert.False(gate.IsBusy);
        var competing = Request(source);
        Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(competing))).Code);
        var drain = commands.DrainSingleApkInstallsAsync();
        var repeated = commands.DrainSingleApkInstallsAsync();
        Assert.False(drain.IsCompleted);
        Assert.False(repeated.IsCompleted);
        try
        {
            if (cancel) stopping.Cancel(); else release.TrySetResult();
            var rejected = await Assert.ThrowsAsync<BrowserRequestException>(() => request);
            Assert.Equal(cancel ? "apk_install_preflight_cancelled" : "busy", rejected.Code);
            Assert.False(rejected.Dispatched);
            await drain; await repeated;
            Assert.Equal(0, calls);
            Assert.False(gate.IsBusy);
            Assert.Equal("request_not_found", Assert.Throws<BrowserRequestException>(() => commands.Get(BrowserApkInstallCommands.Parse(body).RequestId)).Code);
        }
        finally { release.TrySetResult(); await capture; }
        Assert.Equal(2, await store.CleanupAsync());
    }

    [Fact]
    public async Task HostStoppingAndAbandonedWaitRetainRealCoreSourceAndGateThroughNativeExitAndReaders()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        using var stopping = new CancellationTokenSource();
        var source = await Stage(store);
        var entered = Signal(); var cancelled = Signal(); var exit = Signal(); var readers = Signal();
        var runner = new InstallRunner { InstallEntered = entered, ProcessExit = exit,
            ReadersDrained = readers, CancellationObserved = cancelled };
        var gate = new BrowserOperationGate();
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner, new("aapt2", "apksigner")));
        var commands = new BrowserCommands((command, cancellation) =>
        {
            Assert.Equal(stopping.Token, cancellation);
            return BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation);
        }, () => store, gate);
        var body = Request(source);
        var id = BrowserApkInstallCommands.Parse(body).RequestId;
        var task = commands.ExecuteAsync(body, stopping.Token);
        await entered.Task;
        try
        {
            var running = commands.Get(id);
            Assert.Equal("running", running.State);
            Assert.True(running.ApkInstall!.ExecutionStarted);
            Assert.Null(running.ApkInstall.InstallerCommandSucceeded);
            Assert.Null(running.ApkInstall.ExactInstalledBaseBytes);
            Assert.Null(running.ApkInstall.ArtifactIdentity);
            Assert.Null(running.ApkInstall.Installed);
            Assert.Null(running.Error); Assert.Null(running.Mutation); Assert.Null(running.Result);
            using var abandoned = new CancellationTokenSource(); abandoned.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(abandoned.Token));
            var drain = commands.DrainSingleApkInstallsAsync();
            stopping.Cancel(); await cancelled.Task;
            Assert.False(task.IsCompleted); Assert.False(drain.IsCompleted); Assert.True(gate.IsBusy);
            Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => store.RemoveAsync(source.Id))).Code);
            var admitted = runner.Calls.Single(call => call.Arguments.Contains("install")).Arguments[^1];
            Assert.Throws<IOException>(() => File.Delete(admitted));
            Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(body))).Code);
            var sibling = JsonSerializer.SerializeToUtf8Bytes(new { requestId = Guid.NewGuid().ToString("D"), action = "devices" });
            Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(sibling))).Code);
            exit.TrySetResult();
            Assert.False(task.IsCompleted); Assert.False(drain.IsCompleted); Assert.True(gate.IsBusy);
            readers.TrySetResult();
            var final = await task;
            AssertUnknown(final);
            await drain;
            Assert.Equal(final, commands.Get(id));
            Assert.False(gate.IsBusy);
            Assert.False(File.Exists(admitted));
            Assert.Single(runner.Calls, call => call.Arguments.Contains("install"));
            Assert.Equal(1, await store.CleanupAsync());
        }
        finally { exit.TrySetResult(); readers.TrySetResult(); await task; }
    }

    [Fact]
    public async Task ExternalSharedGateRejectsSingleInstallWithoutConsumingItsId()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        var gate = new BrowserOperationGate();
        var runner = new InstallRunner();
        var commands = Commands(store, runner, gate: gate);
        var body = Request(source);
        using (gate.TryAcquire())
        {
            Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(body))).Code);
            Assert.Empty(runner.Calls);
        }
        Assert.Equal("completed", (await commands.ExecuteAsync(body)).State);
        Assert.Single(runner.Calls, call => call.Arguments.Contains("install"));
    }

    [Fact]
    public async Task CapacityAndConsumedIdGuardsRunBeforeFactoryAndDuplicateWinsOverBusy()
    {
        var factoryCalls = 0;
        var commands = new BrowserCommands((command, _) => Task.FromResult(new OperatorExecutionResult(command)), () =>
        { factoryCalls++; throw new InvalidOperationException("no source work allowed"); });
        string? consumed = null;
        for (var index = 0; index < 256; index++)
        {
            var id = Guid.NewGuid().ToString("D"); consumed ??= id;
            var generic = JsonSerializer.SerializeToUtf8Bytes(new { requestId = id, action = "devices" });
            Assert.Equal("completed", (await commands.ExecuteAsync(generic)).State);
        }
        var source = new BrowserArtifactReceipt(new string('a', 32), "base.apk", 4, new string('a', 64));
        Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(() =>
            commands.ExecuteAsync(Request(source, requestId: consumed)))).Code);
        Assert.Equal("session_capacity_reached", (await Assert.ThrowsAsync<BrowserRequestException>(() =>
            commands.ExecuteAsync(Request(source)))).Code);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task SettledApkSetCleanupDebtDoesNotGateIndependentSingleApkInstall()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        var allowCleanup = false;
        var workStore = new BrowserApkSetWorkStore(beforeSnapshot: _ => throw new IOException("private-output-marker"),
            beforeCleanup: _ => { if (!allowCleanup) throw new IOException("private-output-marker"); });
        var work = workStore.Reserve(source.SizeBytes);
        var runner = new InstallRunner();
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner, new("aapt2", "apksigner")));
        var commands = new BrowserCommands((command, cancellation) => BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation),
            store, workStore);
        try
        {
            await using (var lease = await store.AcquireAsync(source.Id, source.Sha256, source.SizeBytes))
                await Assert.ThrowsAsync<IOException>(() => work.PrepareAsync([lease], CancellationToken.None));
            Assert.False(work.Cleanup());
            Assert.True(commands.ApkSetCapability.WorkingCopyCleanupPending);
            Assert.Equal(source.SizeBytes, workStore.ReservedBytes);
            var single = await commands.ExecuteAsync(Request(source));
            Assert.Equal("completed", single.State);
            Assert.True(commands.ApkSetCapability.WorkingCopyCleanupPending);
            Assert.Equal(source.SizeBytes, workStore.ReservedBytes);
            Assert.DoesNotContain("sourceCleanup", JsonSerializer.Serialize(single, Json));
            Assert.Single(runner.Calls, call => call.Arguments.Contains("install"));
        }
        finally { allowCleanup = true; Assert.True(await commands.CleanupApkSetWorkAsync()); }
    }

    [Fact]
    public async Task UnrepresentableReceiptFreeTextIsSanitizedWithoutSynthesizingOperationIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var source = await Stage(store);
        OperatorMutationReceipt? actual = null;
        var commands = Commands(store, new InstallRunner(), (_, result) =>
        {
            actual = result.MutationReceipt;
            return result with { MutationReceipt = actual! with { ObservedState = "private-output-marker /private/path",
                Transitions = actual!.Transitions.Select(transition => transition with { Message = "private-output-marker /private/path" }).ToArray() } };
        });
        var outcome = await commands.ExecuteAsync(Request(source));
        Assert.Equal("completed", outcome.State);
        Assert.Equal(actual!.OperationId, outcome.Mutation!.OperationId);
        Assert.Equal(actual.Transitions.Select(transition => (transition.Stage, transition.At)),
            outcome.Mutation.Transitions.Select(transition => (transition.Stage, transition.At)));
        var wire = JsonSerializer.Serialize(outcome, Json);
        Assert.DoesNotContain("private-output-marker", wire);
        Assert.DoesNotContain("/private/path", wire);
        Assert.Throws<NotSupportedException>(() => ((IList<OperatorMutationTransition>)outcome.Mutation.Transitions)[0] = actual.Transitions[0]);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static byte[] Request(BrowserArtifactReceipt source, ApkInstallOptions? options = null,
        string? requestId = null, string serial = Serial) => JsonSerializer.SerializeToUtf8Bytes(new
        { requestId = requestId ?? Guid.NewGuid().ToString("D"), action = "apk.install", serial, source,
            options = options ?? new ApkInstallOptions(), confirmed = true }, Json);

    private static Task<BrowserArtifactReceipt> Stage(BrowserArtifactStore store) => store.StageAsync("base.apk", new MemoryStream(ApkBytes));

    private static BrowserCommands Commands(BrowserArtifactStore store, InstallRunner runner,
        Func<OperatorCommand, OperatorExecutionResult, OperatorExecutionResult>? alter = null, BrowserOperationGate? gate = null)
    {
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner, new("aapt2", "apksigner")));
        return new(async (command, cancellation) =>
        {
            var result = await BrowserCommands.ExecuteOwnedAsync(executor, command, cancellation);
            return alter is null ? result : alter(command, result);
        }, () => store, gate);
    }

    private static void AssertUnknown(BrowserCommandOutcome outcome)
    {
        Assert.Equal("outcomeUnknown", outcome.State);
        Assert.True(outcome.ApkInstall!.ExecutionStarted);
        Assert.Null(outcome.ApkInstall.InstallerCommandSucceeded);
        Assert.Null(outcome.ApkInstall.ExactInstalledBaseBytes);
        Assert.Null(outcome.ApkInstall.ArtifactIdentity);
        Assert.Null(outcome.ApkInstall.Installed);
        Assert.Null(outcome.Result);
        Assert.Null(outcome.Mutation);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(outcome, Json));
        Assert.DoesNotContain("private-output-marker", document.RootElement.GetRawText());
        Assert.DoesNotContain("dispatched", document.RootElement.GetRawText());
        Assert.All(document.RootElement.EnumerateObject().Where(property => property.Name is not
            ("requestId" or "action" or "state" or "error" or "apkInstall")),
            property => Assert.Equal(JsonValueKind.Null, property.Value.ValueKind));
    }

    private sealed record NativeCall(string FileName, string[] Arguments, TimeSpan Timeout);

    private sealed class InstallRunner : IStreamingCommandRunner
    {
        public List<NativeCall> Calls { get; } = [];
        public bool FallbackDigest { get; init; }
        public bool MultipleInstalledPaths { get; init; }
        public string? Failure { get; init; }
        public string VersionCode { get; init; } = "42";
        public string? VersionName { get; init; } = "1.2.3";
        public TaskCompletionSource? InstallEntered { get; init; }
        public TaskCompletionSource? ProcessExit { get; init; }
        public TaskCompletionSource? ReadersDrained { get; init; }
        public TaskCompletionSource? CancellationObserved { get; init; }
        private int _pathReads;

        public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(new(fileName, arguments.ToArray(), timeout));
            string output;
            if (fileName == "aapt2")
            {
                if (Failure == "tool") throw new IOException("private-output-marker tool");
                var code = Failure switch { "zero-version" => "0", "negative-version" => "-1",
                    "overflow-version" => "9223372036854775808", "malformed-version" => "1x", _ => VersionCode };
                output = $"package: name='com.example.app' versionCode='{code}'" +
                    (VersionName is null ? "" : $" versionName='{VersionName}'") +
                    (Failure == "split" ? " split='config.en'" : "") + "\n";
            }
            else if (fileName == "apksigner")
                output = "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n";
            else if (arguments.SequenceEqual(["devices", "-l"]))
                output = Failure == "not-ready" ? "List of devices attached\n" :
                    "List of devices attached\nQUEST123 device product:hollywood model:Quest_2 device:hollywood\n";
            else if (arguments.Count >= 4 && arguments[0] == "-s" && arguments[1] == Serial && arguments[2] == "install")
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
                Assert.Equal(ApkBytes, File.ReadAllBytes(arguments[^1]));
                Assert.Throws<IOException>(() => File.WriteAllBytes(arguments[^1], [9]));
                Assert.Throws<IOException>(() => File.Delete(arguments[^1]));
                Assert.Throws<IOException>(() => File.Move(arguments[^1], arguments[^1] + ".moved"));
                using var registration = cancellationToken.Register(() => CancellationObserved?.TrySetResult());
                InstallEntered?.TrySetResult();
                if (ProcessExit is not null) await ProcessExit.Task;
                if (ReadersDrained is not null) await ReadersDrained.Task;
                output = "Success\nprivate-output-marker";
                if (Failure == "pm-failure") return new CommandResult(fileName,
                    arguments.ToArray(), 1, output, "private-output-marker", TimeSpan.Zero);
            }
            else if (arguments.SequenceEqual(["-s", Serial, "shell", "pm path 'com.example.app'"]))
            {
                _pathReads++;
                output = Failure == "paths-change" && _pathReads > 1 ? "package:/data/app/replacement/base.apk\n" :
                    "package:/data/app/example/base.apk\n" + (MultipleInstalledPaths ? "package:/data/app/example/split_config.en.apk\n" : "");
            }
            else throw new InvalidOperationException("Unexpected fixture command");
            return new CommandResult(fileName, arguments.ToArray(), 0, output, "", TimeSpan.Zero);
        }

        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName,
            IReadOnlyList<string> arguments, Stream destination, long maximumBytes,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(new(fileName, arguments.ToArray(), timeout));
            if (arguments.Count != 6 || arguments[0] != "-s" || arguments[1] != Serial ||
                arguments[2] != "exec-out" || arguments[3] != "sh" || arguments[4] != "-c" ||
                (!arguments[5].Contains("qfm-installed-digest:v1", StringComparison.Ordinal) &&
                    !arguments[5].Contains("exec cat <&3", StringComparison.Ordinal)))
                throw new InvalidOperationException("Unexpected fixture stream");
            if (Failure == "digest-failure") throw new IOException("private-output-marker digest failed");
            var hash = Failure == "body-mismatch" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(ApkBytes)).ToLowerInvariant();
            var digest = arguments[5].Contains("qfm-installed-digest:v1", StringComparison.Ordinal);
            var unsupported = digest && FallbackDigest;
            var bytes = unsupported ? Encoding.UTF8.GetBytes("qfm-installed-digest:unsupported\n") :
                digest ? Encoding.UTF8.GetBytes(Failure == "digest-malformed" ? "private-output-marker malformed" : $"qfm-installed-digest:v1\n4\n{hash}\n") : ApkBytes;
            Assert.True(bytes.Length <= maximumBytes);
            await destination.WriteAsync(bytes, cancellationToken);
            return new(new CommandResult(fileName, arguments.ToArray(), unsupported ? 90 : 0, "", "", TimeSpan.Zero),
                bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}
