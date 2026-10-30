using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LocalApiDiagnosticLaunchOutputTests
{
    private const string Operation = "api-11111111111111111111111111111111";
    private const string Output = "diag-22222222222222222222222222222222";
    private const string OldPlan = "4f109825aca0feb4a2600fa9f26a60d0f5e0227193d2025e0f3d134489c2e6f0";
    private const string LogName = "logcat-uid-post-fence.txt", ManifestName = "launch-diagnostic-manifest.json";
    private const string OldLimits = """
        {"maximumProcessLogs":8,"recentLogLinesPerCapture":400,"maximumPayloadFiles":14,"maximumPayloadFileBytes":262144,"maximumPayloadBytes":3670016,"maximumArchiveBytes":4194304,"reservedOutputBytes":8388608,"reservedOutputFiles":15,"maximumRequiredStandardOutputBytes":262144,"maximumRequiredStandardErrorBytes":262144,"maximumOptionalStandardOutputBytes":262144,"maximumOptionalStandardErrorBytes":262144,"maximumInputArtifactBytes":1073741824,"maximumBinaryFramingBytes":256,"maximumProcessTimeoutSeconds":300}
        """;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    [Fact]
    public void NewPolicyHasIndependentRestoreAndOnlyAppendsItsFixedBounds()
    {
        var old = LocalApiDiagnosticOutputIntent.Restore(Operation, Output, OldPlan);
        Assert.False(IsLaunch(old)); Assert.Equal(OldPlan, old.PlannedLeafDigest);
        Assert.Equal(OldLimits, JsonSerializer.Serialize(old.Limits));
        var launch = CreateForLaunch(Operation);
        Assert.True(IsLaunch(launch)); Assert.Equal("exact-installed-launch-diagnostic-output.v1", launch.Policy);
        Assert.Equal(15, launch.PayloadLeaves.Count + 1);
        Assert.Equal(OldLimits[..^1] + ",\"maximumArmedCaptureBytes\":262144,\"postActionWindowMilliseconds\":10000,\"maximumLaunchOperationSeconds\":300}", JsonSerializer.Serialize(launch.Limits));
        Assert.Equal(launch.PlannedLeafDigest, RestoreForLaunch(Operation, launch.OutputId, launch.PlannedLeafDigest).PlannedLeafDigest);
        Assert.Throws<LocalApiException>(() => LocalApiDiagnosticOutputIntent.Restore(Operation, launch.OutputId, launch.PlannedLeafDigest));
        Assert.Throws<LocalApiException>(() => RestoreForLaunch(Operation, Output, OldPlan));
        Assert.Throws<LocalApiException>(() => RestoreForLaunch(Operation, launch.OutputId, new string('0', 64)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("api-00000000000000000000000000000000")]
    [InlineData("api-11111111111111111111111111111111\n")]
    public void LaunchFactoryNeverNormalizesInvalidOperationIds(string id) =>
        Assert.Throws<LocalApiException>(() => CreateForLaunch(id));

    [Theory]
    [InlineData(0)]
    [InlineData(262144)]
    public async Task TwoOwnedPayloadsBecomeOnlyAPrivateStoreZipWithExactBytesAndUnreleasedQuota(int length)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage();
        var phases = new List<string>();
        using var output = await f.Reserve(input, (snapshot, _) => { phases.Add(snapshot.Phase); return Task.CompletedTask; });
        var log = new byte[length]; for (var i = 0; i < log.Length; i++) log[i] = (byte)(i % 251);
        var result = Result(output, log);
        var manifest = Manifest(result);
        result = result with { ManifestSizeBytes = manifest.Length, ManifestSha256 = Hash(manifest) };
        await Write(output, log, manifest);
        var candidate = await Seal(output, result);
        Assert.Equal(output.Intent.OutputId, candidate.OutputId);
        Assert.Equal("sealed", output.Snapshot().Phase); Assert.DoesNotContain("available", phases);
        Assert.Equal(3, output.Snapshot().Resources.Count(r => r.Role != "input" && r.State == "sealed"));
        Assert.Equal(12, output.Snapshot().Resources.Count(r => r.Role == "payload" && r.State == "planned"));
        Assert.Equal(new LocalApiStageInventory(16, input.SizeBytes + 8388608), f.Stager.GetInventory());
        Assert.Equal(4, Directory.GetFiles(f.StageDirectory).Length); Assert.Empty(Directory.GetDirectories(f.StageDirectory));
        using var lease = await output.OpenSealedCandidateAsync(candidate.SizeBytes, candidate.Sha256);
        using var bytes = new MemoryStream(); await lease.Stream.CopyToAsync(bytes);
        var zipBytes = bytes.ToArray(); Assert.Equal(candidate.SizeBytes, zipBytes.Length); Assert.Equal(candidate.Sha256, Hash(zipBytes));
        AssertZip(zipBytes, new Dictionary<string, byte[]> { [LogName] = log, [ManifestName] = manifest });
        Assert.False(result.PublishedAtRequestedPath);
    }

    [Theory]
    [InlineData(true,"package","package.txt")]
    [InlineData(true,"launch_logcat","../logcat-uid-post-fence.txt")]
    [InlineData(true,"launch_manifest","diagnostic-manifest.json")]
    [InlineData(false,"launch_logcat","logcat-uid-post-fence.txt")]
    [InlineData(false,"launch_manifest","launch-diagnostic-manifest.json")]
    public async Task ClosedKindsAndNamesCannotCrossPolicies(bool launch, string kind, string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage();
        using var output = await f.Stager.ReserveDiagnosticOutputAsync(launch ? CreateForLaunch(Operation) : LocalApiDiagnosticOutputIntent.Create(Operation), input, Ack);
        await Assert.ThrowsAsync<LocalApiException>(() => output.CreateSink().WriteAsync(kind, name, [1], 0, null, null, false, default));
        Assert.Single(Directory.GetFiles(f.StageDirectory));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("overflow")]
    public async Task DuplicateOrOversizedCaptureCannotProduceAdditionalOwnedContent(string variant)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage(); using var output = await f.Reserve(input);
        if (variant == "duplicate") await output.CreateSink().WriteAsync("launch_logcat", LogName, [], 0, null, null, false, default);
        await Assert.ThrowsAsync<LocalApiException>(() => output.CreateSink().WriteAsync("launch_logcat", LogName,
            variant == "overflow" ? new byte[262145] : [9], 0, null, null, false, default));
        Assert.Equal(variant == "overflow" ? 1 : 2, Directory.GetFiles(f.StageDirectory).Length);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("installed")]
    [InlineData("afterUid")]
    [InlineData("hostFence")]
    [InlineData("deviceFence")]
    [InlineData("attempt")]
    [InlineData("captureHash")]
    [InlineData("captureSize")]
    [InlineData("disposition")]
    [InlineData("readiness")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("postFailure")]
    public async Task WholeManifestAndTypedFactCrossingsCannotSealAValidLookingArchive(string variant)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage(); using var output = await f.Reserve(input);
        byte[] log = [1,2,3]; var result = Result(output, log); var node = JsonNode.Parse(Manifest(result))!;
        switch (variant)
        {
            case "artifact": node["artifact"]!["sha256"] = new string('f',64); break;
            case "installed": node["installedBeforeDispatch"]!["baseApkSizeBytes"] = 999; break;
            case "afterUid": node["currentUserUidAfterCapture"] = 99999; break;
            case "hostFence": node["hostFence"]!["id"] = new string('e',32); break;
            case "deviceFence": node["deviceFence"]!["epoch"] = "1.00"; break;
            case "attempt": node["launch"]!["dispatchAttempted"] = false; break;
            case "captureHash": node["capture"]!["sha256"] = new string('f',64); break;
            case "captureSize": node["capture"]!["sizeBytes"] = 1; break;
            case "disposition": node["effectDisposition"] = "rejectedBeforeDispatch"; break;
            case "readiness": node["limitations"]!["applicationReadiness"] = "ready"; break;
            case "missing": node.AsObject().Remove("launch"); break;
            case "postFailure": node["postReadbackFailure"] = "crossed private failure"; break;
        }
        var text = node.ToJsonString(Json);
        if (variant == "duplicate") text = text.Replace("\"dispatchAttempted\":true", "\"dispatchAttempted\":true,\"dispatchAttempted\":true", StringComparison.Ordinal);
        if (variant == "malformed") text = "{";
        var manifest = Encoding.UTF8.GetBytes(text);
        // Rebind the actual manifest size/hash so failure cannot be incidental checksum mismatch.
        result = result with { ManifestSizeBytes = manifest.Length, ManifestSha256 = Hash(manifest) };
        await Write(output, log, manifest);
        await Assert.ThrowsAsync<LocalApiException>(() => Seal(output, result));
        Assert.DoesNotContain(output.Snapshot().Resources, r => r.Role == "archive" && r.State == "sealed");
    }

    [Theory]
    [InlineData("source")]
    [InlineData("output")]
    [InlineData("leaf")]
    [InlineData("publication")]
    [InlineData("manifestHash")]
    [InlineData("logHash")]
    public async Task TypedResultsCannotAdoptUnboundSourceOrClaimPathPublication(string variant)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage(); using var output = await f.Reserve(input);
        byte[] log = [1]; var result = Result(output, log); var manifest = Manifest(result);
        result = result with { ManifestSizeBytes=manifest.Length, ManifestSha256=Hash(manifest) };
        await Write(output, log, manifest);
        result = variant switch
        {
            "source" => result with { Artifact=result.Artifact with { Path="arbitrary.apk" } },
            "output" => result with { OutputDirectory="arbitrary-output" },
            "leaf" => result with { BundleLeafName="other" },
            "publication" => result with { PublishedAtRequestedPath=true },
            "manifestHash" => result with { ManifestSha256=new string('f',64) },
            _ => result with { Capture=result.Capture with { Sha256=new string('f',64) } }
        };
        await Assert.ThrowsAsync<LocalApiException>(() => Seal(output, result));
    }

    [Theory]
    [InlineData(ApkLaunchDiagnosticDisposition.LaunchPending)]
    [InlineData(ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch)]
    [InlineData(ApkLaunchDiagnosticDisposition.OutcomeUnknown)]
    public async Task StablePrivateEvidencePreservesTheActualNoncompletedOutcome(ApkLaunchDiagnosticDisposition disposition)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage(); using var output = await f.Reserve(input);
        byte[] log = [1,2,3]; var result = Result(output, log);
        result = result with { Disposition = disposition };
        if (disposition == ApkLaunchDiagnosticDisposition.LaunchPending)
            result = result with { Attempt = result.Attempt with { Launch = result.Attempt.Launch! with { ComponentObservedResumed = false } } };
        if (disposition == ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch)
            result = result with { Attempt = new(false, null, "device_readback_rejected", "Rejected before dispatch", []) };
        if (disposition == ApkLaunchDiagnosticDisposition.OutcomeUnknown)
            SetOwnedFailure(result, "Actual post-PID readback failed after installed identity and UID were read");
        var manifest = Manifest(result);
        result = result with { ManifestSizeBytes = manifest.Length, ManifestSha256 = Hash(manifest) };
        await Write(output, log, manifest);
        var candidate = await Seal(output, result);
        Assert.Equal(output.Intent.OutputId, candidate.OutputId);
        Assert.Equal("sealed", output.Snapshot().Phase);
        Assert.Equal(disposition, result.Disposition);
        Assert.False(result.PublishedAtRequestedPath);
    }

    [Fact]
    public async Task ActualPostReadbackFailureCannotBeReplacedWithNullInTheManifest()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage(); using var output = await f.Reserve(input);
        byte[] log = [1]; var result = Result(output, log) with { Disposition = ApkLaunchDiagnosticDisposition.OutcomeUnknown };
        var manifest = Manifest(result);
        SetOwnedFailure(result, "Actual post-PID readback failure");
        result = result with { ManifestSizeBytes = manifest.Length, ManifestSha256 = Hash(manifest) };
        await Write(output, log, manifest);
        await Assert.ThrowsAsync<LocalApiException>(() => Seal(output, result));
    }

    [Theory]
    [InlineData("logcat")]
    [InlineData("manifest")]
    public async Task BothOwnedPayloadsAreMandatory(string omitted)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage(); using var output = await f.Reserve(input);
        byte[] log = [1]; var result = Result(output, log); var manifest = Manifest(result);
        result = result with { ManifestSizeBytes = manifest.Length, ManifestSha256 = Hash(manifest) };
        if (omitted != "logcat") await output.CreateSink().WriteAsync("launch_logcat", LogName, log, 0, null, null, false, default);
        if (omitted != "manifest") await output.CreateSink().WriteAsync("launch_manifest", ManifestName, manifest, null, null, null, false, default);
        await Assert.ThrowsAsync<LocalApiException>(() => Seal(output, result));
    }

    [Theory]
    [InlineData("same-opened-handle-device-sha256", null, true)]
    [InlineData("host-streamed-sha256", "remote-handle-digest-capability-unsupported", true)]
    [InlineData("same-opened-handle-device-sha256", "remote-handle-digest-capability-unsupported", false)]
    [InlineData("host-streamed-sha256", null, false)]
    [InlineData("metadata-only", null, false)]
    public async Task SealerAcceptsOnlyExistingExactVerificationMethodAndReasonTuples(string method, string? reason, bool accepted)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage(); using var output = await f.Reserve(input);
        byte[] log = [1]; var result = Result(output, log);
        var installed = result.InstalledBeforeDispatch with { VerificationMethod = method, VerificationFallbackReason = reason };
        result = result with { InstalledBeforeDispatch = installed, InstalledAfterCapture = installed,
            Attempt = result.Attempt with { Launch = result.Attempt.Launch! with { Installed = installed } } };
        var manifest = Manifest(result);
        result = result with { ManifestSizeBytes = manifest.Length, ManifestSha256 = Hash(manifest) };
        await Write(output, log, manifest);
        if (accepted) Assert.Equal(output.Intent.OutputId, (await Seal(output, result)).OutputId);
        else await Assert.ThrowsAsync<LocalApiException>(() => Seal(output, result));
    }

    [Theory]
    [InlineData("com.other/.MainActivity", false, null, false)]
    [InlineData("com.example.fixture/.MainActivity\n", false, null, false)]
    [InlineData("com.example.fixture/.MainActivity", false, "com.example.fixture/com.example.fixture.RealMain", false)]
    [InlineData("com.example.fixture/.Alias", true, null, false)]
    [InlineData("com.example.fixture/.Alias", true, "com.other/.RealMain", false)]
    [InlineData("com.example.fixture/.MainActivity", false, null, true)]
    [InlineData("com.example.fixture/com.example.fixture.MainActivity", false, null, true)]
    [InlineData("com.example.fixture/.Alias", true, "com.example.fixture/com.example.fixture.RealMain", true)]
    public async Task MatchingManifestCannotAdoptAnotherPackageOrContradictLauncherAliasFacts(
        string component, bool isAlias, string? target, bool accepted)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage(); using var output = await f.Reserve(input);
        byte[] log = [1]; var result = Result(output, log);
        var launch = result.Attempt.Launch!;
        launch = launch with { Component = component, LauncherIsActivityAlias = isAlias, LauncherTargetActivity = target,
            CommandResult = launch.CommandResult with { Arguments = ["-s", launch.Installed.Serial, "shell", "am", "start", "-n", component] } };
        result = result with { Attempt = result.Attempt with { Launch = launch } };
        var manifest = Manifest(result);
        result = result with { ManifestSizeBytes = manifest.Length, ManifestSha256 = Hash(manifest) };
        await Write(output, log, manifest);
        if (accepted) Assert.Equal(output.Intent.OutputId, (await Seal(output, result)).OutputId);
        else await Assert.ThrowsAsync<LocalApiException>(() => Seal(output, result));
    }

    [Fact]
    public async Task ExcessiveTypedProcessInventoryIsRejectedBeforeEnumerationOrManifestComparison()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage(); using var output = await f.Reserve(input);
        byte[] log = [1]; var result = Result(output, log); var manifest = Manifest(result);
        var processes = new ExcessiveProcessInventory();
        result = result with { Attempt = result.Attempt with { CurrentPackageProcessIds = processes },
            ManifestSizeBytes = manifest.Length, ManifestSha256 = Hash(manifest) };
        await Write(output, log, manifest);
        await Assert.ThrowsAsync<LocalApiException>(() => Seal(output, result));
        Assert.False(processes.Enumerated);
        Assert.DoesNotContain(output.Snapshot().Resources, r => r.Role == "archive" && r.State == "sealed");
    }

    [Theory]
    [InlineData("disposition")]
    [InlineData("capture")]
    [InlineData("uid")]
    [InlineData("installedAfter")]
    [InlineData("postFailure")]
    public async Task MatchingManifestCannotConfirmInconsistentTypedEvidence(string variant)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var input = await f.Stage(); using var output = await f.Reserve(input);
        byte[] log = [1]; var result = Result(output, log);
        result = variant switch
        {
            "disposition" => result with { Disposition = ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch },
            "capture" => result with { Capture = result.Capture with { ProcessTreeCleanupSucceeded = false } },
            "uid" => result with { CurrentUserUidAfterCapture = 99999 },
            "installedAfter" => result with { InstalledAfterCapture = result.InstalledAfterCapture! with { BaseApkSha256 = new string('f',64) } },
            _ => result
        };
        if (variant == "postFailure") SetOwnedFailure(result, "Actual readback failed");
        var manifest = Manifest(result);
        result = result with { ManifestSizeBytes = manifest.Length, ManifestSha256 = Hash(manifest) };
        await Write(output, log, manifest);
        await Assert.ThrowsAsync<LocalApiException>(() => Seal(output, result));
    }

    [Fact]
    public void InternalPostReadbackFailureRemainsOutOfLegacyPublicSerialization()
    {
        var result = Result(null, []); SetOwnedFailure(result,"private-internal-failure");
        var json = JsonSerializer.Serialize(result, Json);
        Assert.DoesNotContain("ownedPostReadbackFailure", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-internal-failure", json, StringComparison.Ordinal);
    }

    // Reflection keeps the preimplementation baseline compilable. Every seam is
    // exact-signature bound; missing capability is an explicit runtime failure.
    private static LocalApiDiagnosticOutputIntent CreateForLaunch(string operation) =>
        Invoke<LocalApiDiagnosticOutputIntent>(typeof(LocalApiDiagnosticOutputIntent), "CreateForLaunch", null, [typeof(string)], [operation]);
    private static LocalApiDiagnosticOutputIntent RestoreForLaunch(string operation,string output,string digest) =>
        Invoke<LocalApiDiagnosticOutputIntent>(typeof(LocalApiDiagnosticOutputIntent), "RestoreForLaunch", null, [typeof(string),typeof(string),typeof(string)], [operation,output,digest]);
    private static bool IsLaunch(LocalApiDiagnosticOutputIntent intent)
    {
        var property=typeof(LocalApiDiagnosticOutputIntent).GetProperty("IsLaunch",BindingFlags.Instance|BindingFlags.NonPublic);
        Assert.NotNull(property); return (bool)property.GetValue(intent)!;
    }
    private static Task<LocalApiDiagnosticSealedOutput> Seal(LocalApiDiagnosticOutputReservation output,ApkLaunchDiagnosticBundleResult result) =>
        Invoke<Task<LocalApiDiagnosticSealedOutput>>(typeof(LocalApiDiagnosticOutputReservation), "SealAsync", output, [typeof(ApkLaunchDiagnosticBundleResult),typeof(CancellationToken)], [result,CancellationToken.None]);
    private static PropertyInfo FailureProperty()
    {
        var property=typeof(ApkLaunchDiagnosticBundleResult).GetProperty("OwnedPostReadbackFailure",BindingFlags.Instance|BindingFlags.NonPublic);
        Assert.NotNull(property); return property;
    }
    private static string? OwnedFailure(ApkLaunchDiagnosticBundleResult result)=>(string?)FailureProperty().GetValue(result);
    private static void SetOwnedFailure(ApkLaunchDiagnosticBundleResult result,string? value)=>FailureProperty().SetValue(result,value);
    private static T Invoke<T>(Type type,string name,object? target,Type[] parameters,object?[] args)
    {
        var method=type.GetMethod(name,BindingFlags.NonPublic|(target is null?BindingFlags.Static:BindingFlags.Instance),null,parameters,null);
        Assert.NotNull(method);
        try{return (T)method.Invoke(target,args)!;}
        catch(TargetInvocationException exception) when(exception.InnerException is not null)
        {ExceptionDispatchInfo.Capture(exception.InnerException).Throw();throw;}
    }

    private static ApkLaunchDiagnosticBundleResult Result(LocalApiDiagnosticOutputReservation? output, byte[] log)
    {
        var artifact = new ApkArtifactInspection(output?.ReportedInputPath ?? "retained://fixture/base.apk", 4, Hash([1,2,3,4]), new("com.example.fixture",1,"1",new string('a',64),null));
        var installed = new InstalledApkIdentity("SYNTHETIC_TARGET", artifact.Identity, ["/data/app/base.apk"], artifact.Sha256, artifact.SizeBytes)
        { VerificationMethod = "same-opened-handle-device-sha256" };
        var start = new CommandResult("adb", ["-s",installed.Serial,"shell","am","start","-n","com.example.fixture/.MainActivity"],0,"","",TimeSpan.FromMilliseconds(1));
        var launch = new ResolvedAppLaunchResult(artifact,installed,"com.example.fixture/.MainActivity",start,true);
        return new(artifact,installed,installed,10123,10123,new string('b',32),new DateTimeOffset(2026,10,27,12,0,0,TimeSpan.Zero),"1793102400.000",
            new(true,launch,null,null,[1234]),new(LogName,log.Length,Hash(log),true,false,false,true,0),ApkLaunchDiagnosticDisposition.Completed,"exact launch observed",
            output?.ReportedOutputPath ?? "retained-output://fixture/output",ManifestName,0,"",false,output?.Intent.OutputId ?? "diag-"+new string('2',32));
    }
    private static byte[] Manifest(ApkLaunchDiagnosticBundleResult r) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema="questionable.file_manager.apk_launch_diagnostic_manifest.v1",diagnosticContract=r.DiagnosticContract,
        hostFence=new{id=r.HostLaunchFence,createdAt=r.HostFenceCreatedAt},deviceFence=new{epoch=r.DeviceLaunchFenceEpoch,source="fixed serial-scoped device UTC epoch readback",logSelection="fixed epoch output at or after fence, filtered to derived current-user UID"},
        artifact=r.Artifact,installedBeforeDispatch=r.InstalledBeforeDispatch,installedAfterCapture=r.InstalledAfterCapture,currentUserUidBeforeDispatch=r.CurrentUserUidBeforeDispatch,currentUserUidAfterCapture=r.CurrentUserUidAfterCapture,
        launch=r.Attempt,capture=r.Capture,effectDisposition=r.Disposition,effectDispositionDetail=r.DispositionDetail,postReadbackFailure=OwnedFailure(r),
        limitations=new{applicationReadiness="unknown",openXrReadiness="unknown",wearerVisibility="unknown",screenshotOrRecording=false,genericLogFilter=false,retryPerformed=false}
    },Json);
    private static async Task Write(LocalApiDiagnosticOutputReservation output,byte[] log,byte[] manifest)
    {
        await output.CreateSink().WriteAsync("launch_logcat",LogName,log,0,null,null,false,default);
        await output.CreateSink().WriteAsync("launch_manifest",ManifestName,manifest,null,null,null,false,default);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static Task Ack(LocalApiDiagnosticOutputSnapshot _,CancellationToken __)=>Task.CompletedTask;
    private static void AssertZip(byte[] bytes,IReadOnlyDictionary<string,byte[]> expected)
    {
        using var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal),zip.Entries.Select(e=>e.FullName));
        foreach(var entry in zip.Entries)
        {
            Assert.Equal(entry.Length,entry.CompressedLength);Assert.Equal(new DateTime(1980,1,1,0,0,0),entry.LastWriteTime.DateTime);
            using var contents=new MemoryStream();using(var source=entry.Open())source.CopyTo(contents);Assert.Equal(expected[entry.FullName],contents.ToArray());
        }
        var end=bytes.Length-22;Assert.Equal(0x06054b50u,U32(bytes,end));Assert.Equal(2,U16(bytes,end+10));Assert.Equal(0,U16(bytes,end+20));
        var position=checked((int)U32(bytes,end+16));
        foreach(var name in expected.Keys.Order(StringComparer.Ordinal))
        {
            Assert.Equal(0x02014b50u,U32(bytes,position));Assert.Equal(0,U16(bytes,position+10));Assert.Equal(0,U16(bytes,position+12));Assert.Equal(0x21,U16(bytes,position+14));
            Assert.Equal(Crc(expected[name]),U32(bytes,position+16));Assert.Equal((uint)expected[name].Length,U32(bytes,position+24));Assert.Equal(U32(bytes,position+20),U32(bytes,position+24));
            Assert.Equal(0,U16(bytes,position+30));Assert.Equal(0,U16(bytes,position+32));var length=U16(bytes,position+28);Assert.Equal(name,Encoding.UTF8.GetString(bytes,position+46,length));position+=46+length;
        }
        Assert.Equal(end,position);
    }
    private static ushort U16(byte[] b,int n)=>BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(n,2));
    private static uint U32(byte[] b,int n)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(n,4));
    private static uint Crc(byte[] bytes){var crc=uint.MaxValue;foreach(var b in bytes){crc^=b;for(var i=0;i<8;i++)crc=(crc>>1)^((crc&1)==0?0u:0xedb88320u);}return ~crc;}
    private sealed class ExcessiveProcessInventory : IReadOnlyList<int>
    {
        public int Count => int.MaxValue;
        internal bool Enumerated { get; private set; }
        public int this[int index] => throw new InvalidOperationException("An excessive inventory must not be traversed.");
        public IEnumerator<int> GetEnumerator()
        {
            Enumerated = true;
            throw new InvalidOperationException("An excessive inventory must not be traversed.");
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Fixture:IDisposable
    {
        internal string Root {get;}=Path.Combine(Path.GetTempPath(),"qfm-launch-output-test-"+Guid.NewGuid().ToString("N"));
        internal string StageDirectory=>Path.Combine(Root,"state","staged");
        internal LocalApiArtifactStager Stager {get;}
        internal Fixture(){Directory.CreateDirectory(Root);Stager=new(LocalApiStateSettings.CreateForTests(Path.Combine(Root,"state")));}
        internal async Task<LocalApiStagedArtifact> Stage(){var path=Path.Combine(Root,"source.apk");await File.WriteAllBytesAsync(path,[1,2,3,4]);return await Stager.StageDiagnosticInputAsync(path,(_,_)=>Task.CompletedTask);}
        internal Task<LocalApiDiagnosticOutputReservation> Reserve(LocalApiStagedArtifact input,Func<LocalApiDiagnosticOutputSnapshot,CancellationToken,Task>? ack=null)=>Stager.ReserveDiagnosticOutputAsync(CreateForLaunch(Operation),input,ack??Ack);
        public void Dispose(){Stager.Dispose();Directory.Delete(Root,true);}
    }
}
