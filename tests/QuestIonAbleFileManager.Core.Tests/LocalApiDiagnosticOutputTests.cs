using QuestIonAbleFileManager.Core;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LocalApiDiagnosticOutputTests
{
    [Fact]
    public void DiagnosticOutputIsAnInternalOwnerCreatedCapability()
    {
        var assembly = typeof(AdbClient).Assembly;
        var reservation = assembly.GetType("QuestIonAbleFileManager.Core.LocalApiDiagnosticOutputReservation");
        Assert.NotNull(reservation);
        Assert.False(reservation.IsPublic);
        Assert.NotNull(assembly.GetType("QuestIonAbleFileManager.Core.LocalApiDiagnosticOutputIntent"));
        Assert.NotNull(assembly.GetType("QuestIonAbleFileManager.Core.LocalApiDiagnosticInputLease"));
    }

    [Theory]
    [InlineData("newline")]
    [InlineData("zero")]
    [InlineData("uppercase")]
    public void InternalIntentGrammarRejectsAliasesAndZeroIdentifiers(string scenario)
    {
        var operation = "api-" + new string('a', 32);
        var output = "diag-" + new string('b', 32);
        var badOperation = scenario == "newline" ? operation + "\n" : scenario == "zero" ? "api-" + new string('0', 32) : operation.ToUpperInvariant();
        var badOutput = scenario == "newline" ? output + "\n" : scenario == "zero" ? "diag-" + new string('0', 32) : output.ToUpperInvariant();
        Assert.Equal("diagnostic_operation_invalid", Assert.Throws<LocalApiException>(() => LocalApiDiagnosticOutputIntent.Create(badOperation)).Code);
        Assert.Equal("diagnostic_operation_invalid", Assert.Throws<LocalApiException>(() => LocalApiDiagnosticOutputIntent.Restore(badOperation, output, "")).Code);
        Assert.Equal("diagnostic_operation_invalid", Assert.Throws<LocalApiException>(() => LocalApiDiagnosticOutputIntent.Restore(operation, badOutput, "")).Code);
    }

    [Fact]
    public async Task InputAndFullOutputReservationsShareTheExistingInventoryWithoutDoubleCountingWrites()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        using var input = await root.Stage();
        using var output = await root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, Ack);
        Assert.Equal(new LocalApiStageInventory(16, input.SizeBytes + 8 * 1024 * 1024), root.Stager.GetInventory());
        await output.CreateSink().WriteAsync("package", "package.txt", new byte[2000], 0, null, null, false, default);
        Assert.Equal(new LocalApiStageInventory(16, input.SizeBytes + 8 * 1024 * 1024), root.Stager.GetInventory());
        Assert.False(input.TryDelete(out _));
        Assert.True(await output.CleanupAsync());
        Assert.Equal(new LocalApiStageInventory(1, input.SizeBytes), root.Stager.GetInventory());
        Assert.Single(Directory.GetFiles(root.StageDirectory));
    }

    [Theory]
    [InlineData(15, 16777216, "staged_file_capacity")]
    [InlineData(32, 8388608, "staged_byte_capacity")]
    public async Task AdmissionCountsTheExistingInputAndAllFutureOutputCopies(int files, long bytes, string error)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot(new(MaximumStagedFiles: files, MaximumStagedBytes: bytes));
        using var input = await root.Stage();
        var failure = await Assert.ThrowsAsync<LocalApiException>(() => root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, Ack));
        Assert.Equal(error, failure.Code);
        Assert.Single(Directory.GetFiles(root.StageDirectory));
    }

    [Fact]
    public async Task WritePermissionFollowsBothDurableCreationAndIdentityAcknowledgements()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        using var input = await root.Stage();
        var events = new List<string>();
        using var output = await root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, (snapshot, _) =>
        {
            var resource = snapshot.Resources.First(r => r.Role == "payload");
            events.Add(resource.State);
            var path = Path.Combine(root.StageDirectory, resource.Leaf);
            if (resource.State == "createPending") Assert.False(File.Exists(path));
            if (resource.State == "owned") { Assert.NotNull(resource.Identity); Assert.Equal(0, new FileInfo(path).Length); }
            return Task.CompletedTask;
        });
        var file = await output.CreateSink().WriteAsync("package", "package.txt", [1, 2, 3], 0, null, null, false, default);
        Assert.Equal(new[] { "planned", "createPending", "owned", "sealed" }, events);
        Assert.Equal(3, file.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant(), file.Sha256);
    }

    [Theory]
    [InlineData("createPending")]
    [InlineData("owned")]
    public async Task FailedDurablePermissionNeverWritesBytesAndDoesNotDeleteForeignContent(string failAt)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        using var input = await root.Stage();
        using var output = await root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, (snapshot, _) =>
        {
            if (snapshot.Resources.Any(r => r.Role == "payload" && r.State == failAt)) throw new IOException("synthetic journal rejection");
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<IOException>(() => output.CreateSink().WriteAsync("package", "package.txt", [1, 2, 3], 0, null, null, false, default));
        Assert.Single(Directory.GetFiles(root.StageDirectory));
        await Assert.ThrowsAsync<LocalApiException>(() => output.CreateSink().WriteAsync("device", "device.json", [1], null, null, null, false, default));
    }

    [Theory]
    [InlineData("../package.txt")]
    [InlineData("PACKAGE.txt")]
    [InlineData("package.txt/other")]
    public async Task OnlyClosedCaptureEntriesMayBeCreated(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot(); using var input = await root.Stage();
        using var output = await root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, Ack);
        var failure = await Assert.ThrowsAsync<LocalApiException>(() => output.CreateSink().WriteAsync("package", name, [1], 0, null, null, false, default));
        Assert.Equal("diagnostic_entry_invalid", failure.Code);
        Assert.Single(Directory.GetFiles(root.StageDirectory));
    }

    [Fact]
    public async Task ForeignCollisionIsNotAdoptedOrDeletedDuringCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot(); using var input = await root.Stage();
        using var output = await root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, Ack);
        var collision = Path.Combine(root.StageDirectory, output.Intent.PayloadLeaves[0]);
        await File.WriteAllBytesAsync(collision, [4, 5]);
        await Assert.ThrowsAnyAsync<Exception>(() => output.CreateSink().WriteAsync("package", "package.txt", [1], 0, null, null, false, default));
        Assert.False(await output.CleanupAsync());
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(collision));
        Assert.Equal(16, root.Stager.GetInventory().FileCount);
    }

    [Fact]
    public async Task InputCreationPersistsNativeIdentityBeforeCopyingAnyBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        var source = Path.Combine(root.Root, "source.apk"); await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var states = new List<string>();
        using var input = await root.Stager.StageDiagnosticInputAsync(source, (resource, _) =>
        {
            states.Add(resource.State);
            var path = Path.Combine(root.StageDirectory, resource.Leaf);
            if (resource.State == "createPending") Assert.False(File.Exists(path));
            if (resource.State == "owned") { Assert.NotNull(resource.Identity); Assert.Equal(0, new FileInfo(path).Length); }
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { "createPending", "owned" }, states);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(input.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyRootJunctionRaceRollsBackOnlyTheExactZeroByteCreate(bool afterEarlierCleanup)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        var source = Path.Combine(root.Root, "source.apk"); await File.WriteAllBytesAsync(source, [1, 2, 3]);
        if (afterEarlierCleanup)
        {
            using var earlier = await root.Stager.StageDiagnosticInputAsync(source, (_, _) => Task.CompletedTask);
            Assert.True(earlier.TryDelete(out _));
        }
        var foreign = Path.Combine(root.Root, "foreign"); Directory.CreateDirectory(foreign);
        var changed = false;
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => root.Stager.StageDiagnosticInputAsync(source, (resource, _) =>
            {
                if (resource.State == "createPending")
                {
                    changed = TrySetJunction(root.StageDirectory, foreign, 0x100);
                    Assert.True(changed, "The fixture must exercise the metadata-only empty-root race.");
                }
                return Task.CompletedTask;
            }));
            Assert.True(changed);
            Assert.Empty(Directory.GetFileSystemEntries(foreign));
        }
        finally
        {
            // Repair only this test-owned fixture after releasing the private
            // guard; the guard intentionally denies a generic-write reopen.
            root.Stager.Dispose();
            if (changed) ClearJunction(root.StageDirectory);
        }
    }

    [Theory]
    [InlineData(0x40000000u)]
    [InlineData(0x2u)]
    [InlineData(0x100u)]
    [InlineData(0x80u)]
    [InlineData(0u)]
    public async Task PinnedInputWitnessBlocksEveryReviewedRootReparseMask(uint access)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot(); using var input = await root.Stage();
        using var output = await root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, Ack);
        var foreign = Path.Combine(root.Root, "foreign"); Directory.CreateDirectory(foreign);
        var changed = false;
        try { changed = TrySetJunction(root.StageDirectory, foreign, access); Assert.False(changed); }
        finally { if (changed) ClearJunction(root.StageDirectory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawCreatedHandleSurvivesRollbackAndIdentityFailureUntilExactHandleCleanup(bool outputFault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var failing = true;
        var prefix = outputFault ? "output" : "input";
        using var root = new TestRoot(fault: (step, _) =>
        { if (failing && (step == prefix + "-created" || step == prefix + "-rollback" || step == prefix + "-identity")) throw new IOException("synthetic native fault"); });
        if (outputFault)
        {
            using var input = await root.Stage();
            using var output = await root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, Ack);
            await Assert.ThrowsAsync<IOException>(() => output.CreateSink().WriteAsync("package", "package.txt", [9], 0, null, null, false, default));
            var debt = Assert.Single(output.Snapshot().Resources, r => r.Role == "payload" && r.State == "identityUnknown");
            Assert.Null(debt.Identity);
            Assert.Equal(0, new FileInfo(Path.Combine(root.StageDirectory, debt.Leaf)).Length);
            Assert.Equal(16, root.Stager.GetInventory().FileCount);
            failing = false;
            Assert.True(await output.CleanupAsync());
            Assert.Single(Directory.GetFiles(root.StageDirectory));
        }
        else
        {
            var source = Path.Combine(root.Root, "source.apk"); await File.WriteAllBytesAsync(source, [1, 2, 3]);
            await Assert.ThrowsAsync<IOException>(() => root.Stager.StageDiagnosticInputAsync(source, (_, _) => Task.CompletedTask));
            var debt = Assert.Single(root.Stager.DiagnosticCreationDebts);
            Assert.Null(debt.Identity);
            Assert.Equal(0, new FileInfo(Path.Combine(root.StageDirectory, debt.Leaf)).Length);
            Assert.Equal(new LocalApiStageInventory(1, 3), root.Stager.GetInventory());
            failing = false;
            Assert.True(await root.Stager.CleanupDiagnosticCreationDebtsAsync((_, _) => Task.CompletedTask));
            Assert.Empty(Directory.GetFiles(root.StageDirectory));
        }
    }

    [Fact]
    public async Task HeldInputJournalCallbackDoesNotHoldTheGlobalStageGateOrPermitPrematureDispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        var firstSource = Path.Combine(root.Root, "first.apk"); await File.WriteAllBytesAsync(firstSource, [1, 2, 3]);
        var secondSource = Path.Combine(root.Root, "second.apk"); await File.WriteAllBytesAsync(secondSource, [4, 5]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = root.Stager.StageDiagnosticInputAsync(firstSource, async (resource, token) =>
        {
            if (resource.State == "createPending") { entered.SetResult(); await release.Task.WaitAsync(token); }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(new LocalApiStageInventory(1, 3), root.Stager.GetInventory());
            var busy = Assert.Throws<LocalApiException>(() => root.Stager.Dispose());
            Assert.Equal("diagnostic_operations_active", busy.Code);
            using var second = await root.Stager.StageAsync(secondSource, default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(new LocalApiStageInventory(2, 5), root.Stager.GetInventory());
        }
        finally { release.TrySetResult(); using var first = await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task StatusReadsLastAcknowledgedImmutableSnapshotWithoutWaitingForWriterJournalCallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot(); using var input = await root.Stage();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = await root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, async (snapshot, token) =>
        {
            if (snapshot.Resources.Any(r => r.Role == "payload" && r.State == "owned"))
            { entered.TrySetResult(); await release.Task.WaitAsync(token); }
        });
        var write = output.CreateSink().WriteAsync("package", "package.txt", [1, 2], 0, null, null, false, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var durable = output.LastAcknowledgedSnapshot!;
            Assert.Equal("createPending", durable.Resources.First(r => r.Role == "payload").State);
            Assert.Equal("diagnostic_operations_active", Assert.Throws<LocalApiException>(() => root.Stager.Dispose()).Code);
            Assert.Equal(16, root.Stager.GetInventory().FileCount);
            release.TrySetResult(); await write.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("createPending", durable.Resources.First(r => r.Role == "payload").State);
            Assert.Equal("sealed", output.LastAcknowledgedSnapshot!.Resources.First(r => r.Role == "payload").State);
        }
        finally { release.TrySetResult(); await write.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Theory]
    [InlineData("duplicate-slot")]
    [InlineData("escape-leaf")]
    [InlineData("raise-limit")]
    [InlineData("planned-identity")]
    [InlineData("unknown-state")]
    [InlineData("sha-newline")]
    [InlineData("unknown-entry")]
    [InlineData("reused-identity")]
    public async Task HostileRecoveryMetadataRejectsBeforeOpeningOrDeletingAnyResource(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot(); using var input = await root.Stage();
        using var output = await root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, Ack);
        var snapshot = output.LastAcknowledgedSnapshot!;
        var resources = snapshot.Resources.ToArray();
        var index = Array.FindIndex(resources, r => r.Role == "payload" && r.Slot == 0);
        resources[index] = scenario switch
        {
            "duplicate-slot" => resources[index] with { Slot = 1 },
            "escape-leaf" => resources[index] with { Leaf = "../foreign.bin" },
            "raise-limit" => resources[index] with { MaximumBytes = 262145 },
            "planned-identity" => resources[index] with { Identity = new(1, 2) },
            "unknown-state" => resources[index] with { State = "guessed" },
            "sha-newline" => resources[index] with { State = "sealed", Identity = new(1, 2), SizeBytes = 0, Sha256 = new string('a', 64) + "\n", EntryName = "package.txt" },
            "unknown-entry" => resources[index] with { State = "createPending", EntryName = "../foreign" },
            _ => resources[index] with { State = "owned", Identity = snapshot.Resources.Single(r => r.Role == "input").Identity }
        };
        await Assert.ThrowsAsync<LocalApiException>(() => root.Stager.RegisterDiagnosticRecoveryAsync([snapshot with { Resources = resources }], Ack));
        Assert.Single(Directory.GetFiles(root.StageDirectory));
    }

    [Fact]
    public async Task BroadenedReadAclCannotReceivePrivateDiagnosticPayloads()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot(); using var input = await root.Stage();
        var directory = new DirectoryInfo(root.StageDirectory);
        var original = directory.GetAccessControl();
        var broadened = directory.GetAccessControl();
        broadened.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(broadened);
        try
        {
            await Assert.ThrowsAsync<LocalApiException>(() => root.Stager.ReserveDiagnosticOutputAsync(Intent(), input, Ack));
            Assert.Single(Directory.GetFiles(root.StageDirectory));
        }
        finally { directory.SetAccessControl(original); }
    }

    [Fact]
    public async Task SourceHardlinkCannotEnterDiagnosticPreparation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        var source = Path.Combine(root.Root, "source.apk"); await File.WriteAllBytesAsync(source, [1, 2]);
        Assert.True(CreateHardLink(Path.Combine(root.Root, "alias.apk"), source, IntPtr.Zero));
        await Assert.ThrowsAnyAsync<Exception>(() => root.Stager.StageDiagnosticInputAsync(source, (_, _) => Task.CompletedTask));
        Assert.Empty(Directory.GetFiles(root.StageDirectory));
    }

    [Fact]
    public async Task JunctionAncestorSourceCannotEnterDiagnosticPreparation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        var target = Path.Combine(root.Root, "target"); Directory.CreateDirectory(target);
        await File.WriteAllBytesAsync(Path.Combine(target, "source.apk"), [1, 2]);
        var link = Path.Combine(root.Root, "link"); Directory.CreateDirectory(link);
        Assert.True(TrySetJunction(link, target, 0x40000000));
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => root.Stager.StageDiagnosticInputAsync(Path.Combine(link, "source.apk"), (_, _) => Task.CompletedTask));
            Assert.Empty(Directory.GetFiles(root.StageDirectory));
            Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(target, "source.apk")));
        }
        finally { ClearJunction(link); }
    }

    [Fact]
    public async Task BorrowedInputOwnsItsPathCustodyEvenAfterTheStagerReleasesItsHandles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot(); using var input = await root.Stage();
        using var loan = input.BorrowDiagnosticInput();
        input.Dispose(); root.Stager.Dispose();
        loan.Verify();
        Assert.Throws<IOException>(() => Directory.Move(root.Root, root.Root + "-moved"));
        Assert.Throws<IOException>(() => File.Delete(input.Path));
        Assert.Equal(4, loan.SizeBytes);
    }

    [Fact]
    public async Task InputCallbackCancellationRejoinsTheStageGateAndReleasesEveryPendingPromise()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestRoot();
        var source = Path.Combine(root.Root, "source.apk"); await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var stage = root.Stager.StageDiagnosticInputAsync(source, async (resource, token) =>
        {
            if (resource.State == "owned") { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        }, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stage);
        Assert.Equal(new LocalApiStageInventory(0, 0), root.Stager.GetInventory());
        using var later = await root.Stager.StageAsync(source, default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, root.Stager.GetInventory().FileCount);
    }

    private static LocalApiDiagnosticOutputIntent Intent() => LocalApiDiagnosticOutputIntent.Create("api-" + Guid.NewGuid().ToString("N"));
    private static Task Ack(LocalApiDiagnosticOutputSnapshot snapshot, CancellationToken token) => Task.CompletedTask;
    private sealed class TestRoot : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "qfm-diagnostic-output-test-" + Guid.NewGuid().ToString("N"));
        internal LocalApiArtifactStager Stager { get; }
        internal string StageDirectory => Path.Combine(Root, "state", "staged");
        internal TestRoot(LocalApiStateLimits? limits = null, Action<string, string>? fault = null)
        {
            Directory.CreateDirectory(Root);
            Stager = new(LocalApiStateSettings.CreateForTests(Path.Combine(Root, "state"), limits) with { DiagnosticFault = fault });
        }
        internal async Task<LocalApiStagedArtifact> Stage()
        {
            var source = Path.Combine(Root, "source.apk"); await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            return await Stager.StageAsync(source, default);
        }
        public void Dispose() { Stager.Dispose(); Directory.Delete(Root, true); }
    }

    private static bool TrySetJunction(string link, string target, uint access)
    {
        var substitute = @"\??\" + target;
        var names = Encoding.Unicode.GetBytes(substitute + '\0' + target + '\0');
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.Unicode, true))
        {
            writer.Write(0xA0000003u); writer.Write(checked((ushort)(8 + names.Length))); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write(checked((ushort)(substitute.Length * 2)));
            writer.Write(checked((ushort)((substitute.Length + 1) * 2))); writer.Write(checked((ushort)(target.Length * 2))); writer.Write(names);
        }
        using var handle = CreateFile(link, access, 3, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        var buffer = data.ToArray();
        return DeviceIoControl(handle, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }
    private static void ClearJunction(string path)
    {
        using var handle = CreateFile(path, 0x40000000, 3, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        byte[] header = [3, 0, 0, 0xA0, 0, 0, 0, 0];
        Assert.True(DeviceIoControl(handle, 0x900AC, header, header.Length, IntPtr.Zero, 0, out _, IntPtr.Zero));
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int length,
        IntPtr output, int maximum, out int written, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string link, string existing, IntPtr security);
}
