using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserArtifactReadBrokerTests
{
    private const string Serial = "fixture-target";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    [Theory]
    [InlineData("apk.preflight", null)]
    [InlineData("runtime.observe", "exact")]
    [InlineData("runtime.observe", "development-metadata")]
    public async Task ReadFactsStayBoundLeasedSanitizedAndOneUse(string action, string? verification)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("fixture.apk", new MemoryStream([1, 2, 3]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? privatePath = null;
        var calls = 0;
        var commands = new BrowserCommands(async (command, token) =>
        {
            calls++; privatePath = command.LocalPath;
            entered.SetResult(); await release.Task.WaitAsync(token);
            return Result(command, receipt);
        }, () => store);
        Assert.Contains(action, commands.AvailableActions);
        var request = Body(action, verification, receipt);
        var pending = commands.ExecuteAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => store.RemoveAsync(receipt.Id))).Code);
            Assert.Throws<IOException>(() => File.Open(privatePath!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        }
        finally { release.SetResult(); }
        var outcome = await pending;
        Assert.Equal("completed", outcome.State); Assert.Null(outcome.Result); Assert.Null(outcome.ArtifactInspection); Assert.Null(outcome.Mutation);
        var serialized = JsonSerializer.Serialize(outcome, Json);
        foreach (var text in AllStrings(JsonSerializer.SerializeToElement(outcome, Json)))
        {
            Assert.DoesNotContain(privatePath!, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetDirectoryName(privatePath!)!, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.Combine(Path.GetTempPath(), "QuestIonAbleFileManager.ApkAdmission"), text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("native-secret-vector", serialized, StringComparison.Ordinal);
        var read = JsonSerializer.SerializeToElement(outcome, Json).GetProperty("artifactRead");
        Assert.Equal(Serial, read.GetProperty("serial").GetString());
        Assert.Equal(receipt.Id, read.GetProperty("artifact").GetProperty("id").GetString());
        if (action == "apk.preflight")
        {
            Assert.Equal("preflight", read.GetProperty("kind").GetString());
            var facts = read.GetProperty("preflight");
            Assert.Equal("exact", facts.GetProperty("installedMatch").GetString());
            Assert.True(facts.GetProperty("readyForLaunch").GetBoolean());
            Assert.Equal(29, facts.GetProperty("manifest").GetProperty("minimumSdkVersion").GetInt32());
            var suggestion = facts.GetProperty("nextCommands")[0];
            Assert.Equal("launch_exact_installed_artifact", suggestion.GetProperty("purpose").GetString());
            Assert.False(suggestion.TryGetProperty("arguments", out _));
        }
        else
        {
            Assert.Equal("runtime", read.GetProperty("kind").GetString()); Assert.Equal(verification, read.GetProperty("verification").GetString());
            var runtime = verification == "exact" ? read.GetProperty("runtime") : read.GetProperty("development").GetProperty("runtime");
            Assert.Equal(JsonValueKind.Null, runtime.GetProperty("processAlive").ValueKind);
            Assert.Equal("unknown", runtime.GetProperty("applicationReadiness").GetString());
            Assert.False(runtime.GetProperty("applicationReadinessAuthority").GetBoolean());
            Assert.Equal("unknown", runtime.GetProperty("openXrReadiness").GetString());
            Assert.False(runtime.GetProperty("openXrReadinessAuthority").GetBoolean());
            if (verification == "development-metadata")
            {
                var development = read.GetProperty("development");
                Assert.False(development.GetProperty("installedBytesVerified").GetBoolean());
                Assert.False(development.GetProperty("installedSignerVerified").GetBoolean());
                Assert.False(development.GetProperty("reportedInstallProvenanceVerified").GetBoolean());
                Assert.Equal(JsonValueKind.Null, development.GetProperty("reportedInstallReference").ValueKind);
                Assert.Equal(receipt.FileName, development.GetProperty("expectedArtifact").GetProperty("path").GetString());
            }
            Assert.Equal(receipt.FileName, runtime.GetProperty("artifact").GetProperty("path").GetString());
        }
        Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(request))).Code);
        Assert.Equal(1, calls);
        await store.RemoveAsync(receipt.Id);
        Assert.Same(outcome, commands.Get(outcome.RequestId));
    }

    [Theory]
    [InlineData("apk.preflight", null, "artifact")]
    [InlineData("apk.preflight", null, "serial")]
    [InlineData("runtime.observe", "exact", "artifact")]
    [InlineData("runtime.observe", "exact", "installed")]
    [InlineData("runtime.observe", "development-metadata", "nested-artifact")]
    [InlineData("runtime.observe", "development-metadata", "metadata")]
    [InlineData("runtime.observe", "exact", "command")]
    public async Task MismatchedFactsFailWithoutRawResultsOrPolicyFallback(string action, string? verification, string mismatch)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.apk", new MemoryStream([1, 2, 3]));
        var calls = 0;
        var commands = new BrowserCommands((command, _) =>
        {
            calls++;
            var result = Result(command, receipt);
            result = mismatch switch
            {
                "artifact" when action == "apk.preflight" => result with { ApkPreflightResult = result.ApkPreflightResult! with { Artifact = result.ApkPreflightResult.Artifact with { SizeBytes = 4 } } },
                "artifact" => result with { AppRuntimeObservation = result.AppRuntimeObservation! with { Artifact = result.AppRuntimeObservation.Artifact with { Sha256 = new string('0', 64) } } },
                "serial" => result with { ApkPreflightResult = result.ApkPreflightResult! with { Serial = "other-target" } },
                "installed" => result with { AppRuntimeObservation = result.AppRuntimeObservation! with { Installed = Installed(Artifact(command, receipt)) with { Serial = "other-target" } } },
                "nested-artifact" => result with { DevelopmentAppRuntimeObservation = result.DevelopmentAppRuntimeObservation! with { Runtime = result.DevelopmentAppRuntimeObservation.Runtime with { Artifact = Artifact(command, receipt) with { SizeBytes = 4 } } } },
                "metadata" => result with { DevelopmentAppRuntimeObservation = result.DevelopmentAppRuntimeObservation! with { InstalledMetadata = result.DevelopmentAppRuntimeObservation.InstalledMetadata with { VersionCode = 2 } } },
                "command" => result with { Command = OperatorCommands.ObserveInspectedApp("other-target", command.LocalPath!) },
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(result);
        }, () => store);
        var outcome = await commands.ExecuteAsync(Body(action, verification, receipt));
        Assert.Equal("failed", outcome.State); Assert.Null(outcome.Result); Assert.Null(outcome.ArtifactInspection); Assert.Null(outcome.Mutation);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(outcome, Json).GetProperty("artifactRead").ValueKind);
        Assert.Equal(1, calls); await store.RemoveAsync(receipt.Id);
    }

    [Fact]
    public async Task AbsentInstalledRuntimeRemainsStructuredAndPriorUnknownIsUntouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.apk", new MemoryStream([1, 2, 3]));
        var commands = new BrowserCommands((command, _) => command.Kind == OperatorCommandKind.ObserveInspectedApp
            ? Task.FromResult(Result(command, receipt)) : throw new IOException("lost mutation reply"), () => store);
        var original = await commands.ExecuteAsync(JsonSerializer.SerializeToUtf8Bytes(new { requestId = Guid.NewGuid().ToString("D"), action = "device.normal", serial = Serial, confirmed = true }));
        Assert.Equal("outcomeUnknown", original.State);
        var read = await commands.ExecuteAsync(Body("runtime.observe", "exact", receipt));
        Assert.Equal("completed", read.State);
        var runtime = JsonSerializer.SerializeToElement(read, Json).GetProperty("artifactRead").GetProperty("runtime");
        Assert.Equal(JsonValueKind.Null, runtime.GetProperty("installed").ValueKind);
        Assert.Same(original, commands.Get(original.RequestId)); Assert.Equal("outcomeUnknown", commands.Get(original.RequestId).State);
    }

    [Theory]
    [InlineData(InstalledApkMatch.Absent)]
    [InlineData(InstalledApkMatch.Different)]
    [InlineData(InstalledApkMatch.Unverified)]
    public async Task PreflightPreservesNegativeAndUnverifiedChecksWithoutTurningThemIntoExecutionFailures(InstalledApkMatch match)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.apk", new MemoryStream([1, 2, 3]));
        var commands = new BrowserCommands((command, _) =>
        {
            var result = Result(command, receipt);
            return Task.FromResult(result with { ApkPreflightResult = result.ApkPreflightResult! with
            {
                InstalledMatch = match, Installed = match == InstalledApkMatch.Different ? Installed(Artifact(command, receipt)) with { Identity = null } : null,
                ReadyForLaunch = false, ReadyForDiagnose = false, Checks = [new("installed.exact_bytes", false, "Not exact")]
            } });
        }, () => store);
        var outcome = await commands.ExecuteAsync(Body("apk.preflight", null, receipt));
        Assert.Equal("completed", outcome.State); Assert.Null(outcome.Result);
        var facts = outcome.ArtifactRead!.Preflight!;
        Assert.Equal(match, facts.InstalledMatch); Assert.False(facts.ReadyForLaunch); Assert.False(facts.ReadyForDiagnose);
        Assert.False(facts.Checks[0].Passed);
    }

    [Fact]
    public async Task ExactRuntimePreservesInstalledProofAndCancellationReleasesOnlyReadCustody()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.apk", new MemoryStream([1, 2, 3]));
        var calls = 0;
        var commands = new BrowserCommands((command, token) =>
        {
            if (++calls == 2) throw new OperationCanceledException("private cancellation detail", token);
            var result = Result(command, receipt);
            return Task.FromResult(result with { AppRuntimeObservation = result.AppRuntimeObservation! with { Installed = Installed(Artifact(command, receipt)) } });
        }, () => store);
        var first = await commands.ExecuteAsync(Body("runtime.observe", "exact", receipt));
        Assert.Equal("completed", first.State); Assert.Equal(receipt.Sha256, first.ArtifactRead!.Runtime!.Installed!.BaseApkSha256);
        Assert.Equal(receipt.SizeBytes, first.ArtifactRead.Runtime.Installed.BaseApkSizeBytes);
        var secondBody = Body("runtime.observe", "exact", receipt);
        var cancelled = await commands.ExecuteAsync(secondBody);
        Assert.Equal("failed", cancelled.State); Assert.Null(cancelled.Result); Assert.Null(cancelled.ArtifactRead);
        Assert.DoesNotContain("private cancellation detail", JsonSerializer.Serialize(cancelled));
        Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(secondBody))).Code);
        await store.RemoveAsync(receipt.Id);
    }

    [Fact]
    public async Task SharedGateRejectsReadsWithoutQueuingOrReplayingThem()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(); var receipt = await store.StageAsync("fixture.apk", new MemoryStream([1, 2, 3]));
        var gate = new BrowserOperationGate(); var calls = 0;
        var commands = new BrowserCommands((command, _) => { calls++; return Task.FromResult(Result(command, receipt)); }, () => store, gate);
        using (gate.TryAcquire())
            Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => commands.ExecuteAsync(Body("runtime.observe", "exact", receipt)))).Code);
        Assert.Equal(0, calls);
        var explicitRead = await commands.ExecuteAsync(Body("runtime.observe", "exact", receipt));
        Assert.Equal("completed", explicitRead.State); Assert.Equal(1, calls);
    }

    private static IEnumerable<string> AllStrings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Array => value.EnumerateArray().SelectMany(AllStrings),
        JsonValueKind.Object => value.EnumerateObject().SelectMany(property => AllStrings(property.Value)),
        _ => []
    };

    private static byte[] Body(string action, string? verification, BrowserArtifactReceipt artifact)
    {
        var body = new Dictionary<string, object> { ["requestId"] = Guid.NewGuid().ToString("D"), ["action"] = action,
            ["serial"] = Serial, ["artifactId"] = artifact.Id, ["sha256"] = artifact.Sha256, ["sizeBytes"] = artifact.SizeBytes };
        if (verification is not null) body["verification"] = verification;
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private static ApkArtifactInspection Artifact(OperatorCommand command, BrowserArtifactReceipt artifact) =>
        new(command.LocalPath!, artifact.SizeBytes, artifact.Sha256, new("example.fixture", 1, "version " + command.LocalPath, new string('c', 64), null));
    private static InstalledApkIdentity Installed(ApkArtifactInspection artifact) => new(Serial, artifact.Identity, ["/data/app/fixture/base.apk"], artifact.Sha256, artifact.SizeBytes);
    private static OperatorExecutionResult Result(OperatorCommand command, BrowserArtifactReceipt receipt)
    {
        var artifact = Artifact(command, receipt);
        var runtime = new AppRuntimeObservation(artifact, null, false, false, [])
        {
            ForegroundComponents = ["prefix " + command.LocalPath + " suffix"],
            ActivityObservationSource = "source " + command.LocalPath,
            CurrentFocus = new(AndroidGlobalFocusObservationState.Unknown, command.LocalPath, "source " + command.LocalPath),
            GlobalFocus = new(new(AndroidGlobalFocusRecordState.Unknown, 1, [command.LocalPath!]) { ObservationSource = command.LocalPath! },
                new(AndroidGlobalFocusRecordState.Unknown, 0, [])),
            ProcessObservationSource = command.LocalPath!
        };
        return command.Kind switch
        {
            OperatorCommandKind.PreflightInspectedApp => new(command, ApkPreflightResult: new(artifact, new(29, 34, ["example.fixture.MainActivity"]), Serial,
                new(Serial, "device", "model " + command.LocalPath, null), 34, InstalledApkMatch.Exact, Installed(artifact), "example.fixture/.MainActivity", true, true, true,
                [new("installed.exact_bytes", true, "detail " + command.LocalPath + " " + Path.GetDirectoryName(command.LocalPath!) + " " + Path.Combine(Path.GetTempPath(), "QuestIonAbleFileManager.ApkAdmission", "private", "base.apk"))],
                [new("launch_exact_installed_artifact", ["native-secret-vector", command.LocalPath!], true)])),
            OperatorCommandKind.ObserveInspectedApp => new(command, AppRuntimeObservation: runtime),
            OperatorCommandKind.ObserveDevelopmentApp => new(command, DevelopmentAppRuntimeObservation: new(artifact,
                new(artifact.Identity.PackageName, artifact.Identity.VersionCode, artifact.Identity.VersionName, ["/data/app/fixture/base.apk"], "2026-10-26 12:00:00"),
                runtime with { ObservationContract = "questionable.file_manager.development_runtime_facts.v1" }, null)),
            _ => throw new InvalidOperationException("Unexpected read factory")
        };
    }
}
