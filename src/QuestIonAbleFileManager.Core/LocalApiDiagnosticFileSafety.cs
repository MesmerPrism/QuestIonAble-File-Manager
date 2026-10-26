using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QuestIonAbleFileManager.Core;

// Lazy diagnostic-only strengthening. Do not change the legacy state/CLI
// platform contract. The pinned immutable input keeps this root nonempty while
// usable; deny-write sharing alone cannot stop metadata-only reparse transitions.
internal static class LocalApiDiagnosticFileSafety
{
    internal static LocalApiDiagnosticInputLease BorrowSource(string sourcePath)
    {
        if (!OperatingSystem.IsWindows()) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_output_unsupported");
        var full = Path.GetFullPath(sourcePath);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || !string.Equals(Path.GetExtension(full), ".apk", StringComparison.OrdinalIgnoreCase))
            throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_source_invalid");
        var ancestors = new List<(SafeFileHandle Handle, string Path)>();
        FileStream? source = null;
        try
        {
            var root = Path.GetPathRoot(full)!;
            var current = root;
            foreach (var part in Path.GetRelativePath(root, Path.GetDirectoryName(full)!).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var handle = FleetWindowsFileSafety.OpenDirectory(current, false);
                try { FleetWindowsFileSafety.ValidateDirectory(handle, current); }
                catch { handle.Dispose(); throw; }
                ancestors.Add((handle, current));
            }
            source = FleetWindowsFileSafety.OpenReadOnlyFile(full);
            FleetWindowsFileSafety.ValidateFile(source.SafeFileHandle, full, true);
            if (source.Length is < 1 or > LocalApiStateLimits.DefaultMaximumSingleArtifactBytes)
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_source_capacity");
            var identity = LocalApiDiagnosticIdentity.From(FleetWindowsFileSafety.GetIdentity(source.SafeFileHandle));
            var lease = new LocalApiDiagnosticInputLease(full, source, identity,
                () => { foreach (var parent in ancestors.AsEnumerable().Reverse()) parent.Handle.Dispose(); },
                () => { foreach (var parent in ancestors) FleetWindowsFileSafety.ValidateDirectory(parent.Handle, parent.Path); });
            lease.Verify();
            return lease;
        }
        catch { source?.Dispose(); foreach (var parent in ancestors) parent.Handle.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenGuard(string path)
    {
        if (!OperatingSystem.IsWindows()) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_output_unsupported");
        LocalApiArtifactStager.CreateOrValidatePrivateDirectory(path);
        var handle = CreateFile(@"\\?\" + Path.GetFullPath(path), 0x81, 1, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero); // LIST_DIRECTORY + READ_ATTRIBUTES; share READ only
        if (handle.IsInvalid) { handle.Dispose(); throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_root_unavailable"); }
        try
        {
            ValidateGuard(handle, path);
            var format = new StringBuilder(32);
            if (!GetVolumeInformationByHandle(handle, null, 0, out _, out _, out _, format, format.Capacity) ||
                !string.Equals(format.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
                throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_output_unsupported");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void ValidateGuard(SafeFileHandle handle, string path)
    {
        if (!OperatingSystem.IsWindows()) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_output_unsupported");
        FleetWindowsFileSafety.ValidateDirectory(handle, path);
        LocalApiArtifactStager.CreateOrValidatePrivateDirectory(path);
    }

    internal static long ReadInventoryLength(string path)
    {
        using var handle = CreateFile(@"\\?\" + Path.GetFullPath(path), 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_inventory_unknown");
        FleetWindowsFileSafety.ValidateFile(handle, path, true);
        return FleetWindowsFileSafety.GetIdentity(handle).SizeBytes;
    }

    internal static bool ProveAbsent(string path)
    {
        using var handle = CreateFile(@"\\?\" + Path.GetFullPath(path), 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return false;
        var error = Marshal.GetLastWin32Error();
        if (error is 2 or 3) return true;
        throw LocalApiDiagnosticOutputIntent.Failure("diagnostic_absence_unproven");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(SafeFileHandle handle, StringBuilder? volumeName,
        int volumeNameSize, out uint serial, out uint maximumComponent, out uint flags,
        StringBuilder format, int formatSize);
}
