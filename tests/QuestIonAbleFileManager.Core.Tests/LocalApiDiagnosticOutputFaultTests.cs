using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class LocalApiDiagnosticOutputFaultTests
{
    [Fact]
    public async Task KnownIdentityInputCleanupAcknowledgesBoundDeleteIntentBeforeRemovalAndRetainsFailedFinalAck()
    {
        if (!OperatingSystem.IsWindows()) return;
        var failing = true;
        using var fixture = new Fixture((step, _) =>
        {
            if (failing && step is "input-created" or "input-rollback")
                throw new IOException("synthetic input failure with successful identity lookup");
        });
        await File.WriteAllBytesAsync(fixture.Source, [1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() => fixture.Stager.StageDiagnosticInputAsync(
            fixture.Source, (_, _) => Task.CompletedTask));
        var debt = Assert.Single(fixture.Stager.DiagnosticCreationDebts);
        Assert.NotNull(debt.Identity);
        Assert.Equal("owned", debt.State);
        var path = Path.Combine(fixture.StageDirectory, debt.Leaf);
        var acknowledgements = new List<LocalApiDiagnosticResourceSnapshot>();
        failing = false;
        await Assert.ThrowsAsync<IOException>(() => fixture.Stager.CleanupDiagnosticCreationDebtsAsync((resource, _) =>
        {
            if (resource.State == "removed") throw new IOException("synthetic final removal acknowledgement loss");
            Assert.True(File.Exists(path));
            acknowledgements.Add(resource);
            return Task.CompletedTask;
        }));
        var acknowledged = Assert.Single(acknowledgements);
        Assert.Equal("deletePending", acknowledged.State);
        Assert.Equal(debt.Identity, acknowledged.Identity);
        Assert.False(File.Exists(path));
        Assert.Equal("removed", Assert.Single(fixture.Stager.DiagnosticCreationDebts).State);
        Assert.Equal(1, fixture.Stager.GetInventory().FileCount);
        Assert.True(await fixture.Stager.CleanupDiagnosticCreationDebtsAsync((resource, _) =>
        {
            Assert.Equal("removed", resource.State);
            Assert.Null(resource.DebtCode);
            return Task.CompletedTask;
        }));
        Assert.Empty(fixture.Stager.DiagnosticCreationDebts);
        Assert.Equal(new LocalApiStageInventory(0, 0), fixture.Stager.GetInventory());
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownIdentityAfterCreateAndRollbackFailureKeepsRecoverableDebtAndBoundDeleteIntent(bool failAfterIdentityAcknowledgement)
    {
        if (!OperatingSystem.IsWindows()) return;
        var failing = true;
        using var fixture = new Fixture((step, _) =>
        {
            if (failing && (step == "output-rollback" ||
                (!failAfterIdentityAcknowledgement && step == "output-created") ||
                (failAfterIdentityAcknowledgement && step == "output-identity")))
                throw new IOException("synthetic native failure with successful identity lookup");
        });
        using var input = await fixture.Stage();
        var acknowledgements = new List<LocalApiDiagnosticOutputSnapshot>();
        using var output = await fixture.Reserve(input, (snapshot, _) =>
        {
            LocalApiDiagnosticOutputReservation.ValidateRecovery(snapshot);
            if (failing && failAfterIdentityAcknowledgement &&
                snapshot.Resources.Any(resource => resource.Role == "payload" && resource.State == "owned"))
                throw new IOException("synthetic owned-identity acknowledgement loss");
            acknowledgements.Add(snapshot);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<IOException>(() => output.CreateSink().WriteAsync(
            "package", "package.txt", [1, 2, 3], 0, null, null, false, default));
        var debt = Assert.Single(output.Snapshot().Resources, resource => resource.DebtCode is not null);
        Assert.NotNull(debt.Identity);
        Assert.Equal("owned", debt.State);
        LocalApiDiagnosticOutputReservation.ValidateRecovery(output.Snapshot());
        var path = Path.Combine(fixture.StageDirectory, debt.Leaf);
        Assert.Equal(0, new FileInfo(path).Length);
        Assert.Equal(32, Assert.Throws<IOException>(() => File.Delete(path)).HResult & 0xffff);

        failing = false;
        Assert.True(await output.CleanupAsync());
        Assert.Contains(acknowledgements, snapshot => snapshot.Resources.Any(resource =>
            resource.Leaf == debt.Leaf && resource.State == "deletePending" && resource.Identity == debt.Identity));
        Assert.False(File.Exists(path));
        Assert.Equal(new LocalApiStageInventory(1, input.SizeBytes), fixture.Stager.GetInventory());
    }

    [Fact]
    public async Task UnknownIdentityRetainsTheRawCreatedHandleAndRetriesOnlyItsZeroByteObject()
    {
        if (!OperatingSystem.IsWindows()) return;
        var failing = true;
        var faults = new List<string>();
        using var fixture = new Fixture((step, _) =>
        {
            if (failing && step is "output-created" or "output-rollback" or "output-identity")
            { faults.Add(step); throw new IOException("synthetic native failure"); }
        });
        using var input = await fixture.Stage();
        using var output = await fixture.Reserve(input);
        var foreign = Path.Combine(fixture.Root, "foreign.keep");
        await File.WriteAllBytesAsync(foreign, [7, 8, 9]);
        await Assert.ThrowsAsync<IOException>(() => output.CreateSink().WriteAsync(
            "package", "package.txt", [1, 2, 3, 4], 0, null, null, false, default));
        Assert.Equal(new[] { "output-created", "output-rollback", "output-identity" }, faults);
        var debt = Assert.Single(output.Snapshot().Resources, resource => resource.State == "identityUnknown");
        Assert.Null(debt.Identity);
        Assert.Equal("diagnostic_create_cleanup_debt", debt.DebtCode);
        var debtPath = Path.Combine(fixture.StageDirectory, debt.Leaf);
        Assert.Equal(0, new FileInfo(debtPath).Length);
        // The actual native ownership must survive the failed identity query.
        // A closed handle followed by pathname adoption at cleanup is unsafe.
        Assert.Equal(32, Assert.Throws<IOException>(() => File.Delete(debtPath)).HResult & 0xffff);
        var renamed = Path.Combine(fixture.StageDirectory, "independent-rename.bin");
        Assert.Equal(32, Assert.Throws<IOException>(() => File.Move(debtPath, renamed)).HResult & 0xffff);
        Assert.Equal(32, Assert.Throws<IOException>(() => File.WriteAllBytes(debtPath, [99])).HResult & 0xffff);
        Assert.False(File.Exists(renamed));
        Assert.Equal(0, new FileInfo(debtPath).Length);
        Assert.Equal(new LocalApiStageInventory(16, input.SizeBytes + 8 * 1024 * 1024), fixture.Stager.GetInventory());
        failing = false;
        Assert.True(await output.CleanupAsync());
        Assert.False(File.Exists(Path.Combine(fixture.StageDirectory, debt.Leaf)));
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(foreign));
        Assert.Equal(new LocalApiStageInventory(1, input.SizeBytes), fixture.Stager.GetInventory());
    }

    [Theory]
    [InlineData("owned")]
    [InlineData("sealed")]
    public async Task UnexpectedMissingKnownResourceStaysDebtAcrossRepeatedCleanupAndRestart(string originalState)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        using var input = await fixture.Stage();
        var acknowledged = new List<LocalApiDiagnosticOutputSnapshot>();
        using var output = await fixture.Reserve(input, (snapshot, _) =>
        { acknowledged.Add(snapshot); return Task.CompletedTask; });
        await output.CreateSink().WriteAsync("package", "package.txt", [1, 2, 3], 0, null, null, false, default);
        var recorded = acknowledged.Single(snapshot => snapshot.Resources.Any(resource => resource.Role == "payload" && resource.State == originalState));
        var original = recorded.Resources.Single(resource => resource.Role == "payload" && resource.State == originalState);
        Assert.NotNull(original.Identity);
        output.Dispose(); input.Dispose();
        File.Delete(Path.Combine(fixture.StageDirectory, original.Leaf));
        fixture.Restart();
        using var recovered = Assert.Single(await fixture.Stager.RegisterDiagnosticRecoveryAsync([recorded], Ack));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.False(await recovered.CleanupAsync());
            var debt = recovered.LastAcknowledgedSnapshot!.Resources.Single(resource => resource.Leaf == original.Leaf);
            Assert.Equal(originalState, debt.State);
            Assert.Equal(original.Identity, debt.Identity);
            Assert.Equal("diagnostic_missing_unexpected", debt.DebtCode);
            Assert.Equal(new LocalApiStageInventory(16, input.SizeBytes + 8 * 1024 * 1024), fixture.Stager.GetInventory());
        }
        var durableDebt = recovered.LastAcknowledgedSnapshot!;
        recovered.Dispose(); fixture.Restart();
        using var secondRecovery = Assert.Single(await fixture.Stager.RegisterDiagnosticRecoveryAsync([durableDebt], Ack));
        Assert.False(await secondRecovery.CleanupAsync());
        Assert.Equal(originalState, secondRecovery.LastAcknowledgedSnapshot!.Resources.Single(resource => resource.Leaf == original.Leaf).State);
    }

    [Fact]
    public async Task MissingResourceCompletesOnlyAfterAnActuallyBoundDurableDeleteIntent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var rejectDelete = true;
        var deleteCalls = 0;
        using var fixture = new Fixture((step, _) =>
        {
            if (step == "output-delete" && rejectDelete)
            { deleteCalls++; throw new IOException("synthetic interruption after acknowledged delete intent"); }
        });
        using var input = await fixture.Stage();
        var acknowledgements = new List<LocalApiDiagnosticOutputSnapshot>();
        using var output = await fixture.Reserve(input, (snapshot, _) =>
        { acknowledgements.Add(snapshot); return Task.CompletedTask; });
        await output.CreateSink().WriteAsync("package", "package.txt", [4, 5], 0, null, null, false, default);
        var sealedResource = output.LastAcknowledgedSnapshot!.Resources.Single(resource => resource.State == "sealed");
        Assert.False(await output.CleanupAsync());
        Assert.Equal(1, deleteCalls);
        Assert.Contains(acknowledgements, snapshot => snapshot.Resources.Any(resource => resource.State == "deletePending" && resource.Identity == sealedResource.Identity));
        var recorded = output.LastAcknowledgedSnapshot!;
        Assert.Equal("deletePending", recorded.Resources.Single(resource => resource.Leaf == sealedResource.Leaf).State);
        output.Dispose(); input.Dispose();
        // The test owns this exact object. Model deletion after a bound,
        // acknowledged tombstone but before a final removal acknowledgement.
        File.Delete(Path.Combine(fixture.StageDirectory, sealedResource.Leaf));
        fixture.Restart(); rejectDelete = false;
        using var recovered = Assert.Single(await fixture.Stager.RegisterDiagnosticRecoveryAsync([recorded], Ack));
        Assert.True(await recovered.CleanupAsync());
        var removed = recovered.LastAcknowledgedSnapshot!.Resources.Single(resource => resource.Leaf == sealedResource.Leaf);
        Assert.Equal("removed", removed.State); Assert.Null(removed.DebtCode);
        Assert.Equal(new LocalApiStageInventory(1, input.SizeBytes), fixture.Stager.GetInventory());
    }

    [Fact]
    public async Task InventoryUsesOneMeasuredLengthWhenPayloadGrowthCompletesDuringTheSameRead()
    {
        if (!OperatingSystem.IsWindows()) return;
        var owned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ApkDiagnosticBundleFile>? write = null;
        var measure = false;
        var hookCount = 0;
        using var fixture = new Fixture((step, path) =>
        {
            if (!measure || step != "inventory-measured" || !path.EndsWith(".bin", StringComparison.Ordinal)) return;
            measure = false; hookCount++;
            Assert.Equal(0, new FileInfo(path).Length);
            release.TrySetResult();
            write!.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.Equal(4096, new FileInfo(path).Length);
        });
        using var input = await fixture.Stage();
        using var output = await fixture.Reserve(input, async (snapshot, token) =>
        {
            if (snapshot.Resources.Any(resource => resource.Role == "payload" && resource.State == "owned"))
            { owned.TrySetResult(); await release.Task.WaitAsync(token).ConfigureAwait(false); }
        });
        write = output.CreateSink().WriteAsync("package", "package.txt", new byte[4096], 0, null, null, false, default);
        try
        {
            await owned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            measure = true;
            Assert.Equal(new LocalApiStageInventory(16, input.SizeBytes + 8 * 1024 * 1024), fixture.Stager.GetInventory());
            Assert.Equal(1, hookCount);
            Assert.Equal(new LocalApiStageInventory(16, input.SizeBytes + 8 * 1024 * 1024), fixture.Stager.GetInventory());
        }
        finally { release.TrySetResult(); await write.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task DuplicateResultEntryCannotHideAnOmittedPayloadWithTheSameEntryCount()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        using var input = await fixture.Stage();
        using var output = await fixture.Reserve(input);
        var result = await Capture(fixture, input, output);
        var repeated = result.Files.ToArray();
        Assert.NotEqual(repeated[0].RelativePath, repeated[1].RelativePath);
        repeated[1] = repeated[0];
        Assert.Equal(result.Files.Count, repeated.Length);
        var failure = await Assert.ThrowsAsync<LocalApiException>(() => output.SealAsync(result with { Files = repeated }, default));
        Assert.Equal("diagnostic_manifest_invalid", failure.Code);
        Assert.False(File.Exists(Path.Combine(fixture.StageDirectory, output.Intent.ArchiveLeaf)));
        Assert.Equal("planned", output.LastAcknowledgedSnapshot!.Resources.Single(resource => resource.Role == "archive").State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestEntrySetMustBeBijectiveEvenWhenEveryListedHashAndCountMatches(bool duplicateManifestEntry)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        using var input = await fixture.Stage();
        using var captured = await fixture.Reserve(input);
        var result = await Capture(fixture, input, captured);
        using var candidate = await fixture.Reserve(input);
        var reemitted = await ReemitCapture(fixture, captured, candidate, result, duplicateManifestEntry);
        if (duplicateManifestEntry)
        {
            var failure = await Assert.ThrowsAsync<LocalApiException>(() => candidate.SealAsync(reemitted, default));
            Assert.Equal("diagnostic_manifest_invalid", failure.Code);
            Assert.False(File.Exists(Path.Combine(fixture.StageDirectory, candidate.Intent.ArchiveLeaf)));
        }
        else
        {
            var sealedOutput = await candidate.SealAsync(reemitted, default);
            using var lease = await candidate.OpenSealedCandidateAsync(sealedOutput.SizeBytes, sealedOutput.Sha256);
            Assert.Equal(sealedOutput.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(lease.Stream)).ToLowerInvariant());
        }
    }

    private static async Task<ApkDiagnosticBundleResult> Capture(Fixture fixture, LocalApiStagedArtifact input,
        LocalApiDiagnosticOutputReservation output)
    {
        var source = InspectedDeploymentTests.CreateDeploymentRunner(fixture.Source, pidofOutput: "");
        var runner = new InspectedDeploymentTests.TruncatingDiagnosticRunner(source, "none");
        using var lease = input.BorrowDiagnosticInput();
        var result = await new AdbClient("adb", runner, new("aapt2", "apksigner"))
            .CaptureAdmittedApkDiagnosticsAsync("QUEST123", lease, output.CreateSink());
        Assert.DoesNotContain(source.Calls, call => call.Arguments.Contains("install") || call.Arguments.Contains("am"));
        return result;
    }

    private static async Task<ApkDiagnosticBundleResult> ReemitCapture(Fixture fixture,
        LocalApiDiagnosticOutputReservation original, LocalApiDiagnosticOutputReservation output,
        ApkDiagnosticBundleResult result, bool duplicateManifestEntry)
    {
        var resources = original.LastAcknowledgedSnapshot!.Resources.Where(resource => resource.Role == "payload" && resource.State == "sealed")
            .ToDictionary(resource => resource.EntryName!, StringComparer.Ordinal);
        var artifact = result.Artifact with { Path = output.ReportedInputPath };
        var runtime = result.Runtime with { Artifact = artifact };
        var files = new List<ApkDiagnosticBundleFile>();
        foreach (var file in result.Files.Where(file => file.RelativePath != "diagnostic-manifest.json"))
        {
            var bytes = file.RelativePath == "runtime.json"
                ? JsonSerializer.SerializeToUtf8Bytes(runtime, JsonSerializerOptions.Web)
                : await File.ReadAllBytesAsync(Path.Combine(fixture.StageDirectory, resources[file.RelativePath].Leaf));
            files.Add(await output.CreateSink().WriteAsync(file.CaptureKind, file.RelativePath, bytes,
                file.CommandExitCode, file.ObservationSource, file.CommandSemantic, file.Truncated, default));
        }
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture.StageDirectory, resources["diagnostic-manifest.json"].Leaf)))!;
        manifest["artifact"] = JsonSerializer.SerializeToNode(artifact, JsonSerializerOptions.Web);
        manifest["files"] = JsonSerializer.SerializeToNode(files, JsonSerializerOptions.Web);
        var entries = manifest["files"]!.AsArray();
        Assert.True(entries.Count >= 2);
        Assert.NotEqual(entries[0]!["relativePath"]!.GetValue<string>(), entries[1]!["relativePath"]!.GetValue<string>());
        if (duplicateManifestEntry) entries[1] = entries[0]!.DeepClone();
        Assert.Equal(files.Count, entries.Count);
        var manifestFile = result.Files.Single(file => file.RelativePath == "diagnostic-manifest.json");
        files.Add(await output.CreateSink().WriteAsync("manifest", "diagnostic-manifest.json",
            JsonSerializer.SerializeToUtf8Bytes(manifest, JsonSerializerOptions.Web), manifestFile.CommandExitCode,
            manifestFile.ObservationSource, manifestFile.CommandSemantic, manifestFile.Truncated, default));
        return result with { Artifact = artifact, Runtime = runtime, OutputDirectory = output.ReportedOutputPath, Files = files };
    }

    private static Task Ack(LocalApiDiagnosticOutputSnapshot snapshot, CancellationToken token) => Task.CompletedTask;

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "qfm-diagnostic-fault-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.apk");
        internal string StageDirectory => Path.Combine(Root, "state", "staged");
        internal LocalApiArtifactStager Stager { get; private set; }
        private readonly LocalApiStateSettings _settings;
        internal Fixture(Action<string, string>? fault = null)
        {
            Directory.CreateDirectory(Root);
            _settings = LocalApiStateSettings.CreateForTests(Path.Combine(Root, "state")) with { DiagnosticFault = fault };
            Stager = new(_settings);
        }
        internal async Task<LocalApiStagedArtifact> Stage()
        {
            await File.WriteAllBytesAsync(Source, [0x50, 0x4b, 0x03, 0x04]);
            return await Stager.StageDiagnosticInputAsync(Source, (_, _) => Task.CompletedTask);
        }
        internal Task<LocalApiDiagnosticOutputReservation> Reserve(LocalApiStagedArtifact input,
            Func<LocalApiDiagnosticOutputSnapshot, CancellationToken, Task>? persist = null) =>
            Stager.ReserveDiagnosticOutputAsync(LocalApiDiagnosticOutputIntent.Create("api-" + Guid.NewGuid().ToString("N")), input, persist ?? Ack);
        internal void Restart() { Stager.Dispose(); Stager = new(_settings); }
        public void Dispose() { Stager.Dispose(); Directory.Delete(Root, true); }
    }
}
