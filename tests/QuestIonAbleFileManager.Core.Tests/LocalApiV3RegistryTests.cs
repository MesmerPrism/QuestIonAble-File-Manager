using QuestIonAbleFileManager.Core;
using System.Text.Json;
using System.Security.Cryptography;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LocalApiV3RegistryTests
{
    [Fact]
    public void NativeV3HasExplicitVersionedEntryPointsAndReadOnlyLease()
    {
        var type = typeof(LocalApiCommandRegistry);
        foreach (var name in new[] { "GetV3Capabilities", "PreflightV3Async", "ExecuteV3Async", "GetV3Status", "CancelV3", "AcquireDiagnosticOutputAsync" })
            Assert.NotNull(type.GetMethod(name));
        Assert.Equal(9, (int)Enum.Parse<LocalApiOperationStage>("Preparing"));
    }

    [Fact]
    public async Task SourceFirstPreparingIsNonExecutableAndUsesFirstCommitExpiry()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Fault = (phase, _) => { if (phase == "input-created") { entered.TrySetResult(); release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); } };
        var preparing = fixture.Preflight();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var row = fixture.ReadRow();
        Assert.Equal(LocalApiOperationStage.Preparing, row.Stage);
        Assert.False(row.Consumed); Assert.False(row.Diagnostic!.OutputStarted);
        Assert.Equal(fixture.Apk, row.Artifact.Path);
        Assert.Equal(row.Diagnostic.PreparationStartedAt.AddSeconds(120), row.ExpiresAt);
        var error = await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV3Async(Binding(row)));
        Assert.Equal("operation_preparing", error.Code);
        release.SetResult();
        var ready = await preparing;
        Assert.Equal(row.CommandDigest, ready.CommandDigest); Assert.Equal(row.ExpiresAt, ready.ExpiresAt);
        Assert.Equal(LocalApiOperationStage.Preflighted, fixture.Registry.GetV3Status(Binding(row)).Stage);
        Assert.Single(Directory.GetFiles(fixture.Stage));
        Assert.All(fixture.Source.Calls.Where(call => call.FileName == "aapt2"),
            call => Assert.True(call.Arguments.Last() == fixture.Apk || call.Arguments.Last() == row.StagedPath));
    }

    [Fact]
    public async Task ExecutePublishesOnlySafeDescriptorAndExactReadLeaseThenSurvivesRestartWithoutAdb()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        var ready = await fixture.Preflight();
        var result = await fixture.Registry.ExecuteV3Async(Binding(ready));
        Assert.Equal(LocalApiOperationStage.Completed, result.Stage);
        Assert.True(result.Output.Available);
        var archive = Assert.IsType<LocalApiV3ArchiveDescriptor>(result.Output.Archive);
        Assert.Equal("apk-diagnostics.zip", archive.FileName);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Apk, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uid logcat witness", json);
        Assert.DoesNotContain("result", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mutation", json, StringComparison.OrdinalIgnoreCase);
        using (var lease = await fixture.Registry.AcquireDiagnosticOutputAsync(ready.OperationId, ready.CommandDigest, archive.OutputId, archive.SizeBytes, archive.Sha256))
        {
            Assert.False(lease.Stream.CanWrite);
            Assert.Equal(archive.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(lease.Stream)).ToLowerInvariant());
            Assert.Equal("diagnostic_reads_active", Assert.Throws<LocalApiException>(fixture.Registry.Dispose).Code);
        }
        var calls = fixture.Source.Calls.Count;
        fixture.Reopen();
        Assert.Equal(calls, fixture.Source.Calls.Count);
        Assert.True(fixture.Registry.GetV3Status(Binding(ready)).Output.Available);
        using var reopened = await fixture.Registry.AcquireDiagnosticOutputAsync(ready.OperationId, ready.CommandDigest, archive.OutputId, archive.SizeBytes, archive.Sha256);
        Assert.Equal(archive.SizeBytes, reopened.Stream.Length);
        Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV3Async(Binding(ready)))).Code);
    }

    [Theory]
    [InlineData("before_save")]
    [InlineData("after_journal")]
    public async Task FailedPublicationNeverBecomesAvailableThroughStatusOrLaterSave(string phase)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        var ready = await fixture.Preflight();
        var failed = false;
        fixture.JournalFault = value =>
        {
            if (failed || value != phase) return false;
            var row = phase == "after_journal" ? fixture.PeekUnanchoredRowForFaultSelection() : fixture.ReadRow();
            var terminal = phase == "after_journal" ? row.Diagnostic!.Archive is not null : row.Diagnostic!.ResourcePhase == "sealed";
            return failed = terminal;
        };
        var result = await fixture.Registry.ExecuteV3Async(Binding(ready));
        Assert.True(failed); Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, result.Stage);
        Assert.False(result.Output.Available); Assert.Null(result.Output.Archive);
        fixture.JournalFault = null;
        Assert.False(fixture.Registry.GetV3Status(Binding(ready)).Output.Available);
        await fixture.Registry.PreflightAsync(JsonSerializer.SerializeToUtf8Bytes(new { contractVersion = LocalApiContract.Version, command = "apk.inspect", apkPath = fixture.Apk }));
        Assert.False(fixture.Registry.GetV3Status(Binding(ready)).Output.Available);
        var rowAfter = fixture.ReadRow(ready.OperationId);
        Assert.Null(rowAfter.Diagnostic!.Archive);
        fixture.Reopen();
        Assert.False(fixture.Registry.GetV3Status(Binding(ready)).Output.Available);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("digest")]
    [InlineData("output")]
    [InlineData("size")]
    [InlineData("hash")]
    public async Task ArchiveLeaseRejectsEveryCrossedOriginalBinding(string field)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight();
        var status = await fixture.Registry.ExecuteV3Async(Binding(ready)); var archive = status.Output.Archive!;
        await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.AcquireDiagnosticOutputAsync(
            field == "operation" ? "api-" + new string('a', 32) : ready.OperationId,
            field == "digest" ? new string('a', 64) : ready.CommandDigest,
            field == "output" ? "diag-" + new string('a', 32) : archive.OutputId,
            field == "size" ? archive.SizeBytes + 1 : archive.SizeBytes,
            field == "hash" ? new string('b', 64) : archive.Sha256));
        Assert.True(fixture.Registry.GetV3Status(Binding(ready)).Output.Available);
    }

    [Fact]
    public async Task RequiredInspectionOverflowNeverAllocatesInputOrJournalRow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create("aapt2");
        Assert.Equal("diagnostic_required_output_limit", (await Assert.ThrowsAsync<LocalApiException>(fixture.Preflight)).Code);
        Assert.Empty(Directory.GetFiles(fixture.Stage));
        Assert.Empty(fixture.ReadRows());
    }

    [Fact]
    public async Task RestartedUnconsumedApprovalPerformsNoReadsUntilItsFirstExplicitExecute()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight();
        var calls = fixture.Source.Calls.Count; fixture.Reopen();
        Assert.Equal(calls, fixture.Source.Calls.Count);
        Assert.Equal(LocalApiOperationStage.Preflighted, fixture.Registry.GetV3Status(Binding(ready)).Stage);
        Assert.Equal(LocalApiOperationStage.Completed, (await fixture.Registry.ExecuteV3Async(Binding(ready))).Stage);
        Assert.Equal("operation_consumed", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.ExecuteV3Async(Binding(ready)))).Code);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("digest")]
    [InlineData("leaf")]
    [InlineData("state")]
    [InlineData("input")]
    [InlineData("summary")]
    [InlineData("readyInput")]
    [InlineData("notStartedPayload")]
    [InlineData("completedInput")]
    public async Task EveryRowIsValidatedBeforeAnyOrphanOrRecoveryTempCleanup(string corruption)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var prepared = await fixture.Preflight();
        if (corruption == "completedInput") await fixture.Registry.ExecuteV3Async(Binding(prepared));
        fixture.Registry.Dispose();
        using (var journal = new LocalApiJournal(fixture.Settings))
        {
            var row = Assert.Single(journal.Load()); var metadata = row.Diagnostic!;
            var changed = corruption switch
            {
                "version" => row with { OperationVersion = "unknown.v99" },
                "digest" => row with { CommandDigest = new string('0', 64) },
                "leaf" => row with { Diagnostic = metadata with { Resources = metadata.Resources.Select(resource => resource.Role == "archive" ? resource with { Leaf = "foreign.zip" } : resource).ToArray() } },
                "state" => row with { Diagnostic = metadata with { Resources = metadata.Resources.Select(resource => resource.Role == "archive" ? resource with { State = "owned" } : resource).ToArray() } },
                "input" => row with { StagedPath = fixture.Apk },
                "readyInput" => row with { Diagnostic = metadata with { Resources = metadata.Resources.Select(resource => resource.Role == "input" ? resource with { State = "createPending", Identity = null } : resource).ToArray() } },
                "notStartedPayload" => row with { Stage = LocalApiOperationStage.Running, Consumed = true,
                    Diagnostic = metadata with { Resources = metadata.Resources.Select(resource => resource.Slot == 0 ? resource with { State = "owned", Identity = new(42, 999) } : resource).ToArray() } },
                "completedInput" => row with { Diagnostic = metadata with { Resources = metadata.Resources.Select(resource => resource.Role == "input" ? resource with { State = "planned", Identity = null, SizeBytes = null, Sha256 = null } : resource).ToArray() } },
                _ => row with { Diagnostic = metadata with { Summary = new("questionable.file_manager.apk_diagnostic_bundle.v3", DateTimeOffset.UtcNow, "complete", 1, 0, 0) } }
            };
            journal.Save([changed]);
        }
        var orphan = Path.Combine(fixture.Stage, "orphan.apk"); var temp = Path.Combine(fixture.Root, "operations.v1.synthetic.tmp");
        await File.WriteAllTextAsync(orphan, "preserve orphan"); await File.WriteAllTextAsync(temp, "preserve recovery temp");
        LocalApiCommandRegistry? incorrectlyAccepted = null;
        var rejected = Record.Exception(() => incorrectlyAccepted = new LocalApiCommandRegistry(fixture.Client, fixture.Settings));
        incorrectlyAccepted?.Dispose();
        Assert.IsType<LocalApiException>(rejected);
        Assert.Equal("preserve orphan", await File.ReadAllTextAsync(orphan));
        Assert.Equal("preserve recovery temp", await File.ReadAllTextAsync(temp));
    }

    [Fact]
    public async Task UnknownCaptureAndItsResourceEvidenceSurviveBothSweepsAndRestartBeyondRetention()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight();
        fixture.Fault = (phase, _) => { if (phase == "output-created") throw new IOException("private failure sentinel"); };
        var unknown = await fixture.Registry.ExecuteV3Async(Binding(ready));
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, unknown.Stage);
        fixture.Fault = null; var oldFiles = Directory.GetFiles(fixture.Stage).ToArray();
        fixture.Clock.Now = fixture.Clock.Now.AddDays(2);
        await fixture.Registry.PreflightAsync(JsonSerializer.SerializeToUtf8Bytes(new { contractVersion = LocalApiContract.Version, command = "apk.inspect", apkPath = fixture.Apk }));
        await fixture.Preflight();
        Assert.All(oldFiles, path => Assert.True(File.Exists(path)));
        fixture.Reopen();
        var status = fixture.Registry.GetV3Status(Binding(ready));
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, status.Stage);
        Assert.False(status.Output.Available);
        Assert.Contains(fixture.ReadRows(), row => row.OperationId == ready.OperationId);
    }

    [Fact]
    public async Task ExpiredKnownTerminalCleanupHonorsActiveOutputLeaseAndDeletesInputLast()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight();
        var complete = await fixture.Registry.ExecuteV3Async(Binding(ready)); var archive = complete.Output.Archive!;
        using var lease = await fixture.Registry.AcquireDiagnosticOutputAsync(ready.OperationId, ready.CommandDigest, archive.OutputId, archive.SizeBytes, archive.Sha256);
        fixture.Clock.Now = archive.PublishedAt + fixture.Settings.Limits.EffectiveTerminalRetention;
        var expired = fixture.Registry.GetV3Status(Binding(ready));
        Assert.Equal(LocalApiOperationStage.Completed, expired.Stage);
        Assert.False(expired.Output.Available); Assert.Null(expired.Output.Archive);
        Assert.Equal("operation_expired", expired.ErrorCode);
        Assert.Equal(complete.DiagnosticSummary, expired.DiagnosticSummary);
        Assert.Equal("diagnostic_output_unavailable", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.AcquireDiagnosticOutputAsync(
            ready.OperationId, ready.CommandDigest, archive.OutputId, archive.SizeBytes, archive.Sha256))).Code);
        Assert.Equal(archive.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(lease.Stream)).ToLowerInvariant());
        await fixture.Preflight();
        Assert.False(fixture.Registry.GetV3Status(Binding(ready)).Output.Available);
        lease.Dispose();
        await fixture.Preflight();
        Assert.DoesNotContain(fixture.ReadRows(), row => row.OperationId == ready.OperationId);
        Assert.Equal("operation_unknown", Assert.Throws<LocalApiException>(() => fixture.Registry.GetV3Status(Binding(ready))).Code);
    }

    [Fact]
    public async Task SmallEncodedJournalBudgetRejectsBeforeAnyInspectionOrInputAllocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(limits: new(MaximumJournalBytes: 64 * 1024));
        Assert.Equal("journal_capacity", (await Assert.ThrowsAsync<LocalApiException>(fixture.Preflight)).Code);
        Assert.Empty(fixture.Source.Calls); Assert.Empty(Directory.GetFiles(fixture.Stage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreparingCountsInputAndFullOutputPromiseOnceAcrossLiveAdmissionAndRestart(bool unknownIdentity)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(limits: new(MaximumStagedBytes: 8388612, MaximumStagedFiles: 16));
        fixture.Fault = (phase, _) =>
        {
            if (phase is "input-created" or "input-rollback" || unknownIdentity && phase == "input-identity")
                throw new IOException("injected private allocation fault");
        };
        await Assert.ThrowsAsync<LocalApiException>(fixture.Preflight);
        var row = fixture.ReadRow();
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, row.Stage);
        var inventory = Inventory(fixture.Registry);
        Assert.Equal(new LocalApiStageInventory(16, 8388612), inventory);
        Assert.False(fixture.Registry.GetV3Status(Binding(row)).Output.Available);
        Assert.Equal("staged_file_capacity", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.PreflightAsync(
            JsonSerializer.SerializeToUtf8Bytes(new { contractVersion = LocalApiContract.Version, command = "apk.inspect", apkPath = fixture.Apk })))).Code);
        fixture.Fault = null; fixture.Reopen();
        Assert.Equal(new LocalApiStageInventory(16, 8388612), Inventory(fixture.Registry));
        Assert.Equal(LocalApiOperationStage.OutcomeUnknownRecoveryRequired, fixture.Registry.GetV3Status(Binding(row)).Stage);
    }

    [Fact]
    public async Task PromisedOutputCapacityBlocksLegacyAdmissionBeforeInputAllocationAndTransfersWithoutDoubleCount()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(limits: new(MaximumStagedBytes: 8388612, MaximumStagedFiles: 16));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Fault = (phase, _) => { if (phase == "input-created") { entered.TrySetResult(); release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); } };
        var preparing = fixture.Preflight();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("staged_file_capacity", (await Assert.ThrowsAsync<LocalApiException>(() => fixture.Registry.PreflightAsync(
                JsonSerializer.SerializeToUtf8Bytes(new { contractVersion = LocalApiContract.Version, command = "apk.inspect", apkPath = fixture.Apk })))).Code);
        }
        finally { release.TrySetResult(); }
        await preparing;
        Assert.Equal(new LocalApiStageInventory(16, 8388612), Inventory(fixture.Registry));
        Assert.Single(Directory.GetFiles(fixture.Stage));
    }

    [Fact]
    public async Task CompletedInputRemovalRetainsPublishedArchiveAndDoesNotRecreateInputOnRestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); var ready = await fixture.Preflight();
        var status = await fixture.Registry.ExecuteV3Async(Binding(ready)); var descriptor = status.Output.Archive!;
        // Exercise the owner-only partial retention seam with its actual journal
        // callback: available archive retains custody while the input is removed.
        var operations = (System.Collections.IDictionary)typeof(LocalApiCommandRegistry).GetField("_operations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(fixture.Registry)!;
        var operation = operations[ready.OperationId]!;
        var diagnostic = operation.GetType().GetProperty("Diagnostic")!.GetValue(operation)!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var reservation = (LocalApiDiagnosticOutputReservation)diagnostic.GetType().GetField("Reservation", flags)!.GetValue(diagnostic)!;
        var staged = (LocalApiStagedArtifact)diagnostic.GetType().GetField("Staged", flags)!.GetValue(diagnostic)!;
        var row = fixture.ReadRow();
        Assert.True(await reservation.CleanupInputAsync(staged, (input, _) =>
        {
            var field = diagnostic.GetType().GetField("Row", flags)!;
            var current = (LocalApiJournalEntry)field.GetValue(diagnostic)!;
            var updated = current with { Diagnostic = current.Diagnostic! with { Resources = current.Diagnostic.Resources.Select(resource => resource.Role == "input" ? input : resource).ToArray() } };
            field.SetValue(diagnostic, updated);
            typeof(LocalApiCommandRegistry).GetMethod("PersistLocked", flags)!.Invoke(fixture.Registry, null);
            return Task.CompletedTask;
        }));
        Assert.False(File.Exists(row.StagedPath)); fixture.Reopen();
        Assert.False(File.Exists(row.StagedPath)); Assert.True(fixture.Registry.GetV3Status(Binding(ready)).Output.Available);
        using var read = await fixture.Registry.AcquireDiagnosticOutputAsync(ready.OperationId, ready.CommandDigest, descriptor.OutputId, descriptor.SizeBytes, descriptor.Sha256);
        Assert.Equal(descriptor.SizeBytes, read.Stream.Length); Assert.False(File.Exists(row.StagedPath));
    }

    private static LocalApiStageInventory Inventory(LocalApiCommandRegistry registry) =>
        ((LocalApiArtifactStager)typeof(LocalApiCommandRegistry).GetField("_stager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(registry)!).GetInventory();

    [Fact]
    public async Task PendingLegacyFilePromiseIsCountedBeforeDiagnosticSixteenSlotAdmission()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(limits: new(MaximumStagedFiles: 16));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var stager = (LocalApiArtifactStager)typeof(LocalApiCommandRegistry).GetField("_stager", flags)!.GetValue(fixture.Registry)!;
        var stageGate = (SemaphoreSlim)typeof(LocalApiArtifactStager).GetField("_stageGate", flags)!.GetValue(stager)!;
        int Counter(string name) => (int)typeof(LocalApiCommandRegistry).GetField(name, flags)!.GetValue(fixture.Registry)!;
        Task<LocalApiPreflightResult>? legacy = null; Task<LocalApiV3PreflightResult>? diagnostic = null;
        await stageGate.WaitAsync();
        try
        {
            legacy = fixture.Registry.PreflightAsync(JsonSerializer.SerializeToUtf8Bytes(new
            { contractVersion = LocalApiContract.Version, command = "apk.inspect", apkPath = fixture.Apk }));
            Assert.Equal(1, Counter("_reservedPreflights"));
            diagnostic = fixture.Preflight();
            var until = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!diagnostic.IsCompleted && Counter("_reservedDiagnosticFiles") == 0)
            {
                if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("The bounded diagnostic admission did not reach its decision.");
                await Task.Delay(10);
            }
        }
        finally { stageGate.Release(); }
        await legacy!;
        Assert.Equal("staged_file_capacity", (await Assert.ThrowsAsync<LocalApiException>(() => diagnostic!)).Code);
        Assert.Single(fixture.ReadRows());
        Assert.Single(Directory.GetFiles(fixture.Stage));
        Assert.Equal(1, Inventory(fixture.Registry).FileCount);
    }

    [Fact]
    public async Task AuthenticatedNullRootCannotBecomeAnEmptyJournalAndAuthorizeCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create(); fixture.Registry.Dispose();
        var payload = Convert.ToBase64String("null"u8);
        var previousEnvelopeHash = new string('0', 64); const long sequence = 2;
        var macInput = JsonSerializer.SerializeToUtf8Bytes(new { sequence, previousEnvelopeHash, payload }, Json);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new { sequence, previousEnvelopeHash, payload,
            hmac = Convert.ToHexString(HMACSHA256.HashData(fixture.Settings.JournalIntegrityKey, macInput)).ToLowerInvariant() }, Json);
        var envelopeHash = Convert.ToHexString(SHA256.HashData(envelope)).ToLowerInvariant();
        var anchorInput = JsonSerializer.SerializeToUtf8Bytes(new { sequence, envelopeHash }, Json);
        var anchor = JsonSerializer.SerializeToUtf8Bytes(new { sequence, envelopeHash,
            hmac = Convert.ToHexString(HMACSHA256.HashData(fixture.Settings.JournalIntegrityKey, anchorInput)).ToLowerInvariant() }, Json);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "operations.v1.json"), envelope);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "operations.v1.anchor"), anchor);
        var orphan = Path.Combine(fixture.Stage, "orphan.apk"); var temp = Path.Combine(fixture.Root, "operations.v1.synthetic.tmp");
        await File.WriteAllTextAsync(orphan, "preserve orphan"); await File.WriteAllTextAsync(temp, "preserve recovery temp");
        LocalApiCommandRegistry? incorrectlyAccepted = null;
        var rejected = Record.Exception(() => incorrectlyAccepted = new LocalApiCommandRegistry(fixture.Client, fixture.Settings));
        incorrectlyAccepted?.Dispose();
        Assert.IsType<LocalApiException>(rejected);
        Assert.Equal("preserve orphan", await File.ReadAllTextAsync(orphan)); Assert.Equal("preserve recovery temp", await File.ReadAllTextAsync(temp));
    }

    private static byte[] Binding(LocalApiV3PreflightResult result) => JsonSerializer.SerializeToUtf8Bytes(new
    { contractVersion = LocalApiV3Contract.Version, result.OperationId, result.CommandDigest }, Json);
    private static byte[] Binding(LocalApiJournalEntry result) => JsonSerializer.SerializeToUtf8Bytes(new
    { contractVersion = LocalApiV3Contract.Version, result.OperationId, result.CommandDigest }, Json);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Fixture : IDisposable
    {
        internal string Root = "", Apk = "";
        internal string Stage => Path.Combine(Root, "staged");
        internal Action<string, string>? Fault;
        internal Func<string, bool>? JournalFault;
        internal LocalApiStateSettings Settings = null!;
        internal InspectedDeploymentTests.FakeRunner Source = null!;
        internal AdbClient Client = null!;
        internal LocalApiCommandRegistry Registry = null!;
        internal Clock Clock = new();
        internal static async Task<Fixture> Create(string truncated = "none", LocalApiStateLimits? limits = null)
        {
            var fixture = new Fixture { Root = Path.Combine(Path.GetTempPath(), "qfm-v3-registry-" + Guid.NewGuid().ToString("N")), Apk = await InspectedDeploymentTests.CreateApkAsync() };
            fixture.Source = InspectedDeploymentTests.CreateDeploymentRunner(fixture.Apk, pidofOutput: "", corroboration: args =>
                args.SequenceEqual(new[] { "devices", "-l" }) ? new CommandResult("adb", args, 0, "List of devices attached\nQUEST123 device model:Quest\n", "", TimeSpan.Zero) : null);
            fixture.Client = new AdbClient("adb", new InspectedDeploymentTests.TruncatingDiagnosticRunner(fixture.Source, truncated), new("aapt2", "apksigner"));
            fixture.Settings = LocalApiStateSettings.CreateForTests(fixture.Root, limits) with
            { DiagnosticFault = (phase, path) => fixture.Fault?.Invoke(phase, path), JournalFault = phase => fixture.JournalFault?.Invoke(phase) == true };
            fixture.Registry = new(fixture.Client, fixture.Settings, timeProvider: fixture.Clock);
            return fixture;
        }
        internal Task<LocalApiV3PreflightResult> Preflight() => Registry.PreflightV3Async(JsonSerializer.SerializeToUtf8Bytes(new
        { contractVersion = LocalApiV3Contract.Version, command = "apk.diagnose", apkPath = Apk, serial = "QUEST123" }));
        internal IReadOnlyList<LocalApiJournalEntry> ReadRows()
        {
            // Test-only observation of the writer's current bytes. A second journal
            // loader intentionally cannot open the pinned DELETE-access anchor while
            // this owner is alive; actual authentication is tested by Registry reopen.
            using var envelope = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "operations.v1.json")));
            return JsonSerializer.Deserialize<LocalApiJournalEntry[]>(Convert.FromBase64String(envelope.RootElement.GetProperty("payload").GetString()!), Json)!;
        }
        internal LocalApiJournalEntry ReadRow(string? id = null) => id is null ? Assert.Single(ReadRows()) : ReadRows().Single(row => row.OperationId == id);
        internal LocalApiJournalEntry PeekUnanchoredRowForFaultSelection()
        {
            using var envelope = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "operations.v1.json")));
            return Assert.Single(JsonSerializer.Deserialize<LocalApiJournalEntry[]>(Convert.FromBase64String(envelope.RootElement.GetProperty("payload").GetString()!), Json)!);
        }
        internal void Reopen() { Registry.Dispose(); Registry = new(Client, Settings, timeProvider: Clock); }
        public void Dispose() { Registry.Dispose(); File.Delete(Apk); Directory.Delete(Root, true); }
    }
    private sealed class Clock : TimeProvider
    { internal DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
