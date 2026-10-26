using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using QuestIonAbleFileManager.BrowserHost;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserArtifactStoreTests
{
    [Fact]
    public void UnsupportedPlatformsReportProtectedPathBoundaryAndAlternative()
    {
        var capability = BrowserArtifactStore.Capability;
        Assert.Equal(OperatingSystem.IsWindows(), capability.Supported);
        Assert.Contains("Windows", capability.Alternative);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("native_staging_unavailable", capability.Code);
            Assert.Throws<PlatformNotSupportedException>(() => new BrowserArtifactStore());
        }
    }

    [Theory]
    [InlineData("../app.apk")]
    [InlineData("folder/app.apk")]
    [InlineData("folder\\app.apk")]
    [InlineData("C:app.apk")]
    [InlineData("file.apk:secret")]
    [InlineData("CON.apk")]
    [InlineData("CON .apk")]
    [InlineData("Lpt1.txt")]
    [InlineData("file.apk.")]
    [InlineData("file.apk ")]
    [InlineData(" file.apk")]
    [InlineData("a..apk")]
    [InlineData(".hidden")]
    [InlineData("file\n.apk")]
    [InlineData("аpp.apk")]
    [InlineData("")]
    public void FilenameValidationRejectsTraversalAndCrossPlatformAmbiguity(string name) =>
        Assert.Throws<BrowserArtifactException>(() => BrowserArtifactStore.ValidateFileName(name));

    [Fact]
    public async Task ReceiptsHaveOpaqueSessionOwnedIdsAndNoLocalPaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        using var other = new BrowserArtifactStore();
        var receipt = await store.StageAsync("Example app.apk", Bytes("abc"), 3,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Assert.Equal(3, receipt.SizeBytes);
        Assert.Equal("Example app.apk", receipt.FileName);
        Assert.Matches("^[0-9a-f]{32}$", receipt.Id);
        await using var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes);
        Assert.Equal("abc", await new StreamReader(lease.Stream, leaveOpen: true).ReadToEndAsync());
        Assert.EndsWith(".apk", lease.Path);
        Assert.DoesNotContain(lease.Path, JsonSerializer.Serialize(receipt));
        Assert.DoesNotContain(lease.Path, JsonSerializer.Serialize(lease));
        var error = await Assert.ThrowsAsync<BrowserArtifactException>(() => other.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes));
        Assert.Equal("artifact_not_found", error.Code);
    }

    [Fact]
    public async Task StreamingSizeAndCountLimitsRollbackFailedUploads()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(Path.GetTempPath(), new(4, 2, 6));
        var first = await store.StageAsync("a.bin", Bytes("abcd"));
        var large = await Assert.ThrowsAsync<BrowserArtifactException>(() => store.StageAsync("large.bin", Bytes("12345")));
        Assert.Equal("artifact_size_limit", large.Code);
        var aggregate = await Assert.ThrowsAsync<BrowserArtifactException>(() => store.StageAsync("extra.bin", Bytes("123")));
        Assert.Equal("artifact_byte_capacity", aggregate.Code);
        var second = await store.StageAsync("b.bin", Bytes("ef"));
        var count = await Assert.ThrowsAsync<BrowserArtifactException>(() => store.StageAsync("c.bin", Bytes("")));
        Assert.Equal("artifact_file_capacity", count.Code);
        await store.RemoveAsync(first.Id);
        await store.RemoveAsync(second.Id);
        Assert.Equal(0, await store.CleanupAsync());
    }

    [Fact]
    public async Task ExpectedHashesAndSizesMustMatchBeforeLeaseOrReceipt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("app.apk", Bytes("abc"));
        Assert.Equal("artifact_integrity_mismatch", (await Assert.ThrowsAsync<BrowserArtifactException>(() =>
            store.AcquireAsync(receipt.Id, new string('0', 64), receipt.SizeBytes))).Code);
        await Assert.ThrowsAsync<BrowserArtifactException>(() => store.AcquireAsync(receipt.Id, receipt.Sha256, 4));
        await Assert.ThrowsAsync<BrowserArtifactException>(() => store.StageAsync("wrong.apk", Bytes("abc"), 3, new string('0', 64)));
        await Assert.ThrowsAsync<BrowserArtifactException>(() => store.StageAsync("short.apk", Bytes("abc"), 4));
        Assert.Equal(1, await store.CleanupAsync());
    }

    [Fact]
    public async Task LeasedBytesAndAncestorsCannotBeChangedOrDeletedAndCleanupRetainsUse()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("app.apk", Bytes("abc"));
        var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes);
        var path = lease.Path;
        Assert.Throws<IOException>(() => File.WriteAllText(path, "bad"));
        Assert.Throws<IOException>(() => File.Delete(path));
        Assert.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(path)!, Path.GetDirectoryName(path)! + "-moved"));
        Assert.Equal("artifact_in_use", (await Assert.ThrowsAsync<BrowserArtifactException>(() => store.RemoveAsync(receipt.Id))).Code);
        Assert.Equal(0, await store.CleanupAsync());
        Assert.Throws<BrowserArtifactException>(() => store.Dispose());
        Assert.True(File.Exists(path));
        lease.Dispose();
        await store.RemoveAsync(receipt.Id);
        Assert.False(File.Exists(path));
        await Assert.ThrowsAsync<BrowserArtifactException>(() => store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes));
    }

    [Fact]
    public async Task BoundedProducerCannotExceedLimitsEvenIfItCatchesWriteFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(Path.GetTempPath(), new(4, 2, 6));
        var error = await Assert.ThrowsAsync<BrowserArtifactException>(() => store.CaptureAsync("out.bin", async (stream, cancellation) =>
        {
            try { await stream.WriteAsync("12345"u8.ToArray(), cancellation); }
            catch (BrowserArtifactException) { }
        }));
        Assert.Equal("artifact_size_limit", error.Code);
        Assert.Equal(0, await store.CleanupAsync());
        var receipt = await store.CaptureAsync("out.bin", async (stream, cancellation) =>
            await stream.WriteAsync("1234"u8.ToArray(), cancellation));
        Assert.Equal(4, receipt.SizeBytes);
    }

    [Fact]
    public async Task CompletedCaptureCannotBeChangedThroughRetainedProducerStream()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        Stream? retained = null;
        var receipt = await store.CaptureAsync("out.bin", async (stream, cancellation) =>
        {
            retained = stream;
            await stream.WriteAsync("abc"u8.ToArray(), cancellation);
        });
        Assert.Throws<ObjectDisposedException>(() => retained!.WriteByte(0));
        await using var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes);
        Assert.Equal(3, lease.Stream.Length);
    }

    [Fact]
    public async Task CleanupDoesNotRecursivelyDeleteUnexpectedFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("owned.bin", Bytes("abc"));
        string root;
        await using (var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes)) root = Path.GetDirectoryName(lease.Path)!;
        var foreign = Path.Combine(root, "unregistered.bin");
        File.WriteAllText(foreign, "preserve");
        Assert.Equal(1, await store.CleanupAsync());
        Assert.Throws<BrowserArtifactException>(() => store.Dispose());
        Assert.Equal("preserve", File.ReadAllText(foreign));
        File.Delete(foreign);
        store.Dispose();
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ReparseParentIsRejectedBeforeAnySessionRootCanBeCreated()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = Path.Combine(Path.GetTempPath(), "QfmArtifactReparseTest-" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(fixture, "real");
        var link = Path.Combine(fixture, "link");
        Directory.CreateDirectory(real);
        try
        {
            CreateJunction(link, real);
            Assert.Throws<BrowserArtifactException>(() => new BrowserArtifactStore(link, new()));
            Assert.Empty(Directory.GetFileSystemEntries(real));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(real);
            Directory.Delete(fixture);
        }
    }

    [Fact]
    public void NativeFileAdmissionRejectsHardLinkedBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "QfmArtifactHardlinkTest-" + Guid.NewGuid().ToString("N"));
        var alias = path + "-alias";
        File.WriteAllText(path, "abc");
        try
        {
            Assert.True(CreateHardLink(alias, path, IntPtr.Zero));
            using var file = BrowserArtifactFileSafety.OpenRead(path);
            Assert.Equal("artifact_object_invalid", Assert.Throws<BrowserArtifactException>(() =>
                BrowserArtifactFileSafety.Validate(file.SafeFileHandle, path)).Code);
        }
        finally { File.Delete(alias); File.Delete(path); }
    }

    [Fact]
    public async Task CancellationAndProducerFailureLeaveNoCommittedArtifact()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore(Path.GetTempPath(), new(4, 1, 4));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.CaptureAsync("out.bin", async (stream, cancellation) =>
        {
            await stream.WriteAsync("a"u8.ToArray(), cancellation);
            throw new OperationCanceledException();
        }));
        var receipt = await store.StageAsync("out.bin", Bytes("abcd"));
        Assert.Equal(4, receipt.SizeBytes);
    }

    [Fact]
    public async Task SessionRootHasProtectedPrivateAclAndDisposeDeletesOnlyOwnedBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("app.apk", Bytes("abc"));
        string root;
        await using (var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes))
        {
            root = Path.GetDirectoryName(lease.Path)!;
            var acl = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Assert.True(acl.AreAccessRulesProtected);
            Assert.Equal(WindowsIdentity.GetCurrent().User, acl.GetOwner(typeof(SecurityIdentifier)));
            var trusted = new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow) Assert.Contains(rule.IdentityReference, trusted);
        }
        store.Dispose();
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task WidenedRootReadPermissionsRejectFurtherStagingAndUse()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new BrowserArtifactStore();
        var receipt = await store.StageAsync("app.apk", Bytes("abc"));
        string root;
        await using (var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes)) root = Path.GetDirectoryName(lease.Path)!;
        var directory = new DirectoryInfo(root);
        var original = directory.GetAccessControl();
        var modified = directory.GetAccessControl();
        modified.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        directory.SetAccessControl(modified);
        try
        {
            var error = await Assert.ThrowsAsync<BrowserArtifactException>(async () =>
            {
                await using var lease = await store.AcquireAsync(receipt.Id, receipt.Sha256, receipt.SizeBytes);
            });
            Assert.Equal("artifact_acl_untrusted_principal", error.Code);
            await Assert.ThrowsAsync<BrowserArtifactException>(() => store.StageAsync("other.bin", Bytes("abc")));
        }
        finally { directory.SetAccessControl(original); }
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    // Mount-point reparse fixtures need no symbolic-link privilege or external
    // shell. Both paths are new directories owned exclusively by this test.
    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        var substitute = @"\??\" + target;
        var names = Encoding.Unicode.GetBytes(substitute + '\0' + target + '\0');
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(0xA0000003u);
            writer.Write(checked((ushort)(8 + names.Length)));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(checked((ushort)(substitute.Length * 2)));
            writer.Write(checked((ushort)((substitute.Length + 1) * 2)));
            writer.Write(checked((ushort)(target.Length * 2)));
            writer.Write(names);
        }
        using var handle = CreateFile(link, 0x40000000, 3, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        var buffer = bytes.ToArray();
        Assert.True(DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            "Could not create the test-owned mount-point fixture: " + Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newPath, string existingPath, IntPtr security);
}
