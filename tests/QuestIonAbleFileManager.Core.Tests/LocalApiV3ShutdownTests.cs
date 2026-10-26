using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LocalApiV3ShutdownTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly byte[] UnknownBinding = Binding("api-" + new string('a', 32), new string('b', 64));

    [Fact]
    public async Task DisposeJoinsAdmittedSourceInspectionAndItsLaterJournalAcknowledgementBeforeReleasingCustody()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        var sdk = fixture.Runner.HoldNextInspection();
        var journal = fixture.HoldNextJournalAcknowledgement();
        Task? disposal = null;
        Task<LocalApiV3PreflightResult>? preparing = null;
        try
        {
            preparing = fixture.Preflight();
            await sdk.Entered.Task.WaitAsync(Bound);
            Assert.Equal(fixture.Apk, sdk.InputPath);
            disposal = BeginDispose(fixture.Registry);
            await WaitForClosedAdmission(fixture.Registry);
            Assert.False(preparing.IsCompleted);
            Assert.False(disposal.IsCompleted);
            fixture.AssertOwnerStillExclusive();
            AssertSourceProtected(fixture.Apk);

            // Dispose must join work which has not even reached its durable callback yet.
            sdk.Release.TrySetResult();
            await journal.Entered.Task.WaitAsync(Bound);
            Assert.False(preparing.IsCompleted);
            Assert.False(disposal.IsCompleted);
            fixture.AssertOwnerStillExclusive();
            AssertSourceProtected(fixture.Apk);
            journal.Release.TrySetResult();

            var ready = await preparing.WaitAsync(Bound);
            await disposal.WaitAsync(Bound);
            await journal.Exited.Task.WaitAsync(Bound);
            Assert.Equal("owner_stopping", (await Assert.ThrowsAsync<LocalApiException>(
                () => fixture.Registry.ExecuteV3Async(Binding(ready.OperationId, ready.CommandDigest)))).Code);
            Assert.Equal(0, fixture.Runner.ActiveInspections);
            var row = Assert.Single(fixture.ReadRows());
            Assert.Equal(ready.OperationId, row.OperationId);
            Assert.Equal(ready.CommandDigest, row.CommandDigest);
            Assert.Equal(LocalApiOperationStage.Preflighted, row.Stage);
            Assert.False(row.Consumed);
            Assert.False(row.Dispatched);
            Assert.True(File.Exists(row.StagedPath));
            using (var reopened = new LocalApiCommandRegistry(fixture.Client, fixture.Settings))
            {
                var restored = reopened.GetV3Status(Binding(ready.OperationId, ready.CommandDigest));
                Assert.Equal(LocalApiOperationStage.Preflighted, restored.Stage);
                Assert.False(restored.Output.Available);
            }
            await fixture.AssertNoDetachedWrites();
            // The borrowed caller source is released only after all preparation completes.
            using var writable = File.Open(fixture.Apk, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            sdk.Release.TrySetResult(); journal.Release.TrySetResult();
            if (preparing is not null) await Observe(preparing);
            if (disposal is not null) await disposal.WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task CancelledSourcePreparationStillJoinsTheActualInspectionBeforeDisposeCanReleaseOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        using var caller = new CancellationTokenSource();
        var sdk = fixture.Runner.HoldNextInspection();
        Task? disposal = null;
        Task<LocalApiV3PreflightResult>? preparing = null;
        try
        {
            preparing = fixture.Preflight(caller.Token);
            await sdk.Entered.Task.WaitAsync(Bound);
            caller.Cancel();
            await sdk.CancellationObserved.Task.WaitAsync(Bound);
            Assert.False(preparing.IsCompleted, "Cancellation signalling is not runner termination.");
            disposal = BeginDispose(fixture.Registry);
            await WaitForClosedAdmission(fixture.Registry);
            Assert.False(disposal.IsCompleted);
            fixture.AssertOwnerStillExclusive();
            AssertSourceProtected(fixture.Apk);
            sdk.Release.TrySetResult();
            var failure = await Assert.ThrowsAsync<LocalApiException>(() => preparing.WaitAsync(Bound));
            Assert.Equal("diagnostic_preparation_cancelled", failure.Code);
            await disposal.WaitAsync(Bound);
            Assert.Equal(0, fixture.Runner.ActiveInspections);
            Assert.Empty(fixture.ReadRows());
            using (var reopened = new LocalApiCommandRegistry(fixture.Client, fixture.Settings))
                Assert.Equal(LocalApiV3Contract.Version, reopened.GetV3Capabilities().ContractVersion);
            await fixture.AssertNoDetachedWrites();
        }
        finally
        {
            sdk.Release.TrySetResult();
            if (preparing is not null) await Observe(preparing);
            if (disposal is not null) await disposal.WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task PageLossAndRequestedCancellationDoNotReleaseAnExecutingOwnerBeforeRunnerAndCleanupAckFinish()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = await Fixture.Create();
        var ready = await fixture.Preflight();
        var binding = Binding(ready.OperationId, ready.CommandDigest);
        var sdk = fixture.Runner.HoldNextInspection();
        using var page = new CancellationTokenSource();
        Task<LocalApiV3OperationStatus>? execution = null;
        Task? disposal = null;
        JournalBarrier? journal = null;
        try
        {
            execution = fixture.Registry.ExecuteV3Async(binding, page.Token);
            await sdk.Entered.Task.WaitAsync(Bound);
            Assert.NotEqual(fixture.Apk, sdk.InputPath);
            page.Cancel();
            Assert.False(sdk.CancellationObserved.Task.IsCompleted, "A disconnected observer cannot cancel the retained executor.");
            Assert.False(execution.IsCompleted);
            Assert.Equal(LocalApiOperationStage.Running, fixture.Registry.GetV3Status(binding).Stage);
            var requested = fixture.Registry.CancelV3(binding);
            Assert.Equal(LocalApiOperationStage.CancellationRequested, requested.Stage);
            Assert.False(requested.Output.Available);
            await sdk.CancellationObserved.Task.WaitAsync(Bound);
            Assert.False(execution.IsCompleted, "Cancel acknowledgement alone cannot finish a held runner.");
            fixture.AssertOwnerStillExclusive();
            AssertSourceProtected(sdk.InputPath!);

            // Hold a genuine later cleanup journal/anchor acknowledgement too.
            journal = fixture.HoldNextJournalAcknowledgement();
            disposal = BeginDispose(fixture.Registry);
            await WaitForClosedAdmission(fixture.Registry);
            Assert.False(disposal.IsCompleted);
            sdk.Release.TrySetResult();
            await journal.Entered.Task.WaitAsync(Bound);
            Assert.False(execution.IsCompleted);
            Assert.False(disposal.IsCompleted);
            fixture.AssertOwnerStillExclusive();
            journal.Release.TrySetResult();
            var terminal = await execution.WaitAsync(Bound);
            await disposal.WaitAsync(Bound);
            await journal.Exited.Task.WaitAsync(Bound);
            Assert.Equal(LocalApiOperationStage.Cancelled, terminal.Stage);
            Assert.True(terminal.Consumed);
            Assert.False(terminal.Output.Available);
            Assert.Null(terminal.Output.Archive);
            var row = Assert.Single(fixture.ReadRows());
            Assert.Equal(ready.OperationId, row.OperationId);
            Assert.Equal(LocalApiOperationStage.Cancelled, row.Stage);
            Assert.True(row.Consumed);
            Assert.False(row.Dispatched);
            Assert.All(row.Diagnostic!.Resources, resource => Assert.Contains(resource.State, new[] { "planned", "removed" }));
            Assert.Equal("owner_stopping", (await Assert.ThrowsAsync<LocalApiException>(
                () => fixture.Registry.ExecuteV3Async(binding))).Code);
            using (var reopened = new LocalApiCommandRegistry(fixture.Client, fixture.Settings))
                Assert.Equal(LocalApiOperationStage.Cancelled, reopened.GetV3Status(binding).Stage);
            await fixture.AssertNoDetachedWrites();
        }
        finally
        {
            sdk.Release.TrySetResult(); journal?.Release.TrySetResult();
            if (execution is not null) await Observe(execution);
            if (disposal is not null) await disposal.WaitAsync(Bound);
        }
    }

    private static Task BeginDispose(LocalApiCommandRegistry registry) => Task.Factory.StartNew(
        registry.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static async Task WaitForClosedAdmission(LocalApiCommandRegistry registry)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < Bound)
        {
            try { await registry.ExecuteV3Async(UnknownBinding); }
            catch (LocalApiException exception)
            {
                if (exception.Code == "owner_stopping") return;
                Assert.Equal("operation_unknown", exception.Code);
            }
            await Task.Delay(1);
        }
        Assert.Fail("Dispose did not seal new admission while the controlled operation remained held.");
    }

    private static void AssertSourceProtected(string path)
    {
        Assert.Equal(32, Assert.Throws<IOException>(() => File.Delete(path)).HResult & 0xffff);
        Assert.Equal(32, Assert.Throws<IOException>(() => File.WriteAllBytes(path, [9])).HResult & 0xffff);
    }
    private static byte[] Binding(string operationId, string commandDigest) => JsonSerializer.SerializeToUtf8Bytes(new
    { contractVersion = LocalApiV3Contract.Version, operationId, commandDigest });
    private static async Task Observe(Task task)
    {
        try { await task.WaitAsync(Bound); }
        catch (LocalApiException) { /* Expected failed preflight still must actually finish. */ }
    }

    private sealed class InspectionBarrier
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? InputPath;
    }
    private sealed class JournalBarrier
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class HoldingRunner(InspectedDeploymentTests.TruncatingDiagnosticRunner inner) : IStreamingCommandRunner, ITruncatingCommandRunner
    {
        private InspectionBarrier? _next;
        private int _active;
        internal int ActiveInspections => Volatile.Read(ref _active);
        internal InspectionBarrier HoldNextInspection()
        {
            var barrier = new InspectionBarrier();
            Assert.Null(Interlocked.CompareExchange(ref _next, barrier, null));
            return barrier;
        }
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token = default) =>
            inner.RunAsync(fileName, arguments, timeout, token);
        public Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> arguments, Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken token = default) =>
            inner.RunToStreamAsync(fileName, arguments, destination, maximumBytes, timeout, token);
        public async Task<TruncatingCommandResult> RunTruncatingAsync(string fileName, IReadOnlyList<string> arguments,
            int maximumStandardOutputBytes, int maximumStandardErrorBytes, TimeSpan timeout, CancellationToken token = default)
        {
            var barrier = fileName == "aapt2" ? Interlocked.Exchange(ref _next, null) : null;
            if (barrier is not null)
            {
                Interlocked.Increment(ref _active);
                try
                {
                    barrier.InputPath = arguments[^1];
                    using var signal = token.Register(() => barrier.CancellationObserved.TrySetResult());
                    barrier.Entered.TrySetResult();
                    // Deliberately acknowledge cancellation ONLY after controlled runner exit.
                    await barrier.Release.Task.WaitAsync(Bound);
                    token.ThrowIfCancellationRequested();
                }
                finally { Interlocked.Decrement(ref _active); }
            }
            return await inner.RunTruncatingAsync(fileName, arguments, maximumStandardOutputBytes, maximumStandardErrorBytes, timeout, token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root = "", Apk = "";
        internal LocalApiStateSettings Settings = null!;
        internal HoldingRunner Runner = null!;
        internal AdbClient Client = null!;
        internal LocalApiCommandRegistry Registry = null!;
        private JournalBarrier? _nextJournal;
        private int _journalCallbacks;
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture { Root = Path.Combine(Path.GetTempPath(), "qfm-v3-drain-" + Guid.NewGuid().ToString("N")),
                Apk = await InspectedDeploymentTests.CreateApkAsync() };
            var source = InspectedDeploymentTests.CreateDeploymentRunner(fixture.Apk, pidofOutput: "", corroboration: args =>
                args.SequenceEqual(new[] { "devices", "-l" }) ? new CommandResult("adb", args, 0,
                    "List of devices attached\nQUEST123 device model:Quest\n", "", TimeSpan.Zero) : null);
            fixture.Runner = new(new(source, "none"));
            fixture.Client = new("adb", fixture.Runner, new("aapt2", "apksigner"));
            fixture.Settings = LocalApiStateSettings.CreateForTests(fixture.Root) with
            { JournalIntegrityKey = RandomNumberGenerator.GetBytes(32), JournalFault = fixture.OnJournal };
            fixture.Registry = new(fixture.Client, fixture.Settings);
            return fixture;
        }
        internal Task<LocalApiV3PreflightResult> Preflight(CancellationToken token = default) => Registry.PreflightV3Async(
            JsonSerializer.SerializeToUtf8Bytes(new { contractVersion = LocalApiV3Contract.Version, command = "apk.diagnose", apkPath = Apk, serial = "QUEST123" }), token);
        internal JournalBarrier HoldNextJournalAcknowledgement()
        {
            var barrier = new JournalBarrier();
            Assert.Null(Interlocked.CompareExchange(ref _nextJournal, barrier, null));
            return barrier;
        }
        private bool OnJournal(string phase)
        {
            Interlocked.Increment(ref _journalCallbacks);
            var barrier = phase == "after_journal" ? Interlocked.Exchange(ref _nextJournal, null) : null;
            if (barrier is not null)
            {
                try { barrier.Entered.TrySetResult(); barrier.Release.Task.WaitAsync(Bound).GetAwaiter().GetResult(); }
                finally { barrier.Exited.TrySetResult(); }
            }
            return false;
        }
        internal void AssertOwnerStillExclusive()
        {
            var exception = Assert.Throws<LocalApiException>(() => new LocalApiCommandRegistry(Client, Settings));
            Assert.Equal("state_in_use", exception.Code);
        }
        internal IReadOnlyList<LocalApiJournalEntry> ReadRows()
        { using var journal = new LocalApiJournal(Settings); return journal.Load(); }
        internal async Task AssertNoDetachedWrites()
        {
            Assert.Equal(0, Runner.ActiveInspections);
            var callbacks = Volatile.Read(ref _journalCallbacks);
            var path = Path.Combine(Root, "operations.v1.json");
            var journal = File.Exists(path) ? File.ReadAllBytes(path) : [];
            for (var i = 0; i < 4; i++) await Task.Yield();
            Assert.Equal(callbacks, Volatile.Read(ref _journalCallbacks));
            Assert.Equal(journal, File.Exists(path) ? File.ReadAllBytes(path) : []);
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _nextJournal, null)?.Release.TrySetResult();
            Registry.Dispose();
            CryptographicOperations.ZeroMemory(Settings.JournalIntegrityKey);
            File.Delete(Apk);
            Directory.Delete(Root, recursive: true);
        }
    }
}
