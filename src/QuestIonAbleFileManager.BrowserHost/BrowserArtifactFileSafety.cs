using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace QuestIonAbleFileManager.BrowserHost;

// Deliberately Windows-only. Mode bits and advisory Unix locks cannot provide
// the deny-write/deny-delete path lease used by the existing Core APK boundary.
internal static class BrowserArtifactFileSafety
{
    internal const string RootMarkerName = ".qfm-owner";
    private static readonly object CreationDebtGate = new();
    private static readonly Dictionary<string, FileStream> CreationDebts = new(StringComparer.Ordinal);
    internal readonly record struct Identity(uint Volume, ulong Index, long Length, uint Links,
        bool Directory, bool Reparse, bool DeletePending);

    [SupportedOSPlatform("windows")]
    internal static void CreatePrivateDirectory(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new BrowserArtifactException("artifact_owner_unknown");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        var trusted = new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        foreach (var sid in trusted)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            // Native create rejects a pre-existing object; never adopt a guessed root.
            if (!CreateDirectory(Extended(path), ref attributes)) Fail("artifact_root_create_failed");
        }
        finally { pinned.Free(); }
        ValidatePrivateDirectory(path);
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidatePrivateDirectory(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new BrowserArtifactException("artifact_owner_unknown");
        var trusted = new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!acl.AreAccessRulesProtected || !user.Equals(acl.GetOwner(typeof(SecurityIdentifier))))
            Fail("artifact_acl_invalid");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !trusted.Contains(rule.IdentityReference))
                Fail("artifact_acl_untrusted_principal");
    }

    internal static SafeFileHandle OpenDirectory(string path, bool allowWriteSharing = true)
    {
        // Ancestors can serve unrelated writers. The private root blocks data-
        // writable handles; its retained marker also prevents metadata-only
        // reparse transitions by keeping that directory nonempty.
        // FILE_READ_ATTRIBUTES alone does not enroll the handle in data-access
        // sharing. FILE_LIST_DIRECTORY makes deny-write/delete effective.
        var handle = Open(path, allowWriteSharing ? 0x80u : 0x81u,
            allowWriteSharing ? 3u : 1u, 3, 0x02000000 | 0x00200000);
        try { Validate(handle, path, directory: true); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static FileStream CreateFile(string path) =>
        new(Open(path, 0x80000000 | 0x40000000, 1, 1, 0x00200000), FileAccess.ReadWrite, 64 * 1024, false);

    internal static FileStream OpenRead(string path) =>
        new(Open(path, 0x80000000, 1, 3, 0x00200000), FileAccess.Read, 64 * 1024, false);

    internal static FileStream CreateRootMarker(string root)
    {
        var path = Path.Combine(root, RootMarkerName);
        var marker = new FileStream(Open(path, 0x80000000 | 0x40000000 | 0x10000, 1, 1, 0x00200000),
            FileAccess.ReadWrite, 4096, false);
        try
        {
            Validate(marker.SafeFileHandle, path);
            marker.WriteByte(0);
            marker.Flush(true);
            Validate(marker.SafeFileHandle, path);
            return marker;
        }
        catch
        {
            // If the ancestor became a junction before the root was admitted,
            // discard only this exact create-new handle, even outside the root.
            DiscardCreatedOrRetainDebt(marker);
            throw;
        }
    }

    internal static void DeleteCreatedFile(FileStream file)
    {
        MarkDelete(file.SafeFileHandle);
        file.Dispose();
    }

    internal static void DiscardCreatedOrRetainDebt(FileStream file)
    {
        try { DeleteCreatedFile(file); }
        catch (BrowserArtifactException)
        {
            var id = Guid.NewGuid().ToString("N");
            lock (CreationDebtGate) CreationDebts.Add(id, file);
            throw new BrowserArtifactException("artifact_create_cleanup_pending", id);
        }
    }

    internal static bool TryCleanupCreationDebt(string id)
    {
        lock (CreationDebtGate)
        {
            if (!CreationDebts.TryGetValue(id, out var file)) return false;
            try { DeleteCreatedFile(file); }
            catch (BrowserArtifactException) { return false; }
            CreationDebts.Remove(id);
            return true;
        }
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags)
    {
        var handle = CreateFileNative(Extended(path), access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw new BrowserArtifactException("artifact_native_open_failed");
    }

    internal static Identity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) ||
            !GetFileInformationByHandleEx(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInformation>()))
            throw new BrowserArtifactException("artifact_identity_unavailable");
        return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            ((long)info.FileSizeHigh << 32) | info.FileSizeLow, info.NumberOfLinks,
            (info.FileAttributes & 0x10) != 0, (info.FileAttributes & 0x400) != 0, standard.DeletePending);
    }

    internal static Identity Validate(SafeFileHandle handle, string expectedPath, bool directory = false)
    {
        var identity = ReadIdentity(handle);
        if (identity.Reparse || identity.DeletePending || identity.Directory != directory || (!directory && identity.Links != 1))
            throw new BrowserArtifactException("artifact_object_invalid");
        var buffer = new char[32768];
        var count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (count == 0 || count >= buffer.Length) Fail("artifact_path_unavailable");
        var actual = new string(buffer, 0, (int)count);
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(expectedPath), StringComparison.OrdinalIgnoreCase))
            Fail("artifact_path_changed");
        return identity;
    }

    internal static bool SameObject(Identity left, Identity right) =>
        left.Volume == right.Volume && left.Index == right.Index && left.Directory == right.Directory;

    internal static void DeleteExpected(string path, Identity expected)
    {
        using var handle = Open(path, 0x80000000 | 0x10000, 0, 3, 0x00200000 | (expected.Directory ? 0x02000000u : 0));
        var current = Validate(handle, path, expected.Directory);
        if (!SameObject(current, expected)) Fail("artifact_identity_changed");
        MarkDelete(handle);
    }

    private static void MarkDelete(SafeFileHandle handle)
    {
        var disposition = new DispositionInformation { Delete = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<DispositionInformation>()))
            Fail("artifact_cleanup_incomplete");
    }

    private static string Extended(string path) => @"\\?\" + Path.GetFullPath(path);
    private static void Fail(string code) => throw new BrowserArtifactException(code);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public uint High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public uint FileAttributes; public NativeFileTime Creation; public NativeFileTime Access; public NativeFileTime Write;
        public uint VolumeSerialNumber; public uint FileSizeHigh; public uint FileSizeLow; public uint NumberOfLinks;
        public uint FileIndexHigh; public uint FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInformation
    {
        public long AllocationSize; public long EndOfFile; public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DispositionInformation { [MarshalAs(UnmanagedType.Bool)] public bool Delete; }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out StandardInformation info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref DispositionInformation info, uint size);
}
