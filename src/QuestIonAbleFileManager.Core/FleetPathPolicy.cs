using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace QuestIonAbleFileManager.Core;

public static class FleetPathPolicy
{
    public const int MaximumRelativePathLength = 512;
    private const int MaximumSegmentLength = 128;
    private static readonly HashSet<string> ReservedWindowsNames = CreateReservedWindowsNames();
    private static readonly char[] InvalidWindowsCharacters = ['<', '>', ':', '"', '\\', '|', '?', '*'];

    public static IReadOnlyList<string> ValidateRelativePath(string relativePath, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Length == 0)
        {
            if (allowEmpty)
            {
                return Array.Empty<string>();
            }
            throw Reject("path_empty", "A non-empty relative path is required.");
        }

        if (relativePath.Length > MaximumRelativePathLength)
        {
            throw Reject("path_too_long", $"The relative path exceeds {MaximumRelativePathLength} characters.");
        }
        if (relativePath.StartsWith("/", StringComparison.Ordinal) ||
            relativePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw Reject("path_not_relative", "The path must be relative and must not end with a separator.");
        }

        var segments = relativePath.Split('/');
        foreach (var segment in segments)
        {
            ValidateSegment(segment);
        }
        return segments;
    }

    public static string ToRemotePath(string relativePath, bool allowEmpty)
    {
        var segments = ValidateRelativePath(relativePath, allowEmpty);
        return segments.Count == 0
            ? FleetIntegrationContract.RemoteRoot
            : FleetIntegrationContract.RemoteRoot + "/" + string.Join("/", segments);
    }

    public static IReadOnlyList<string> ValidatePushArtifactPath(string relativePath)
    {
        var segments = ValidateRelativePath(relativePath, allowEmpty: false);
        if (segments.Count != 3 ||
            segments[0] is not ("artifacts" or "operations") ||
            !IsIdentifier(segments[1]) ||
            !string.Equals(segments[2], "payload.bin", StringComparison.Ordinal))
        {
            throw Reject(
                "push_source_path_invalid",
                "Push input must be artifacts/<artifact-id>/payload.bin or operations/<operation-id>/payload.bin.");
        }
        return segments;
    }

    public static FleetPullDestination PreparePullDestination(
        string configuredRoot,
        string operationId,
        string relativePath)
    {
        ValidateRelativePath(relativePath, allowEmpty: false);
        var root = RequireSafeExistingRoot(configuredRoot);
        return FleetPullDestination.Create(root, operationId);
    }

    public static string RequireSafeExistingRoot(string configuredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        if (!OperatingSystem.IsWindows())
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Unsupported,
                "secure_staging_platform_unsupported",
                "Fleet integration secure staging v1 requires Windows.");
        }
        if (!Path.IsPathFullyQualified(configuredRoot))
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Unavailable,
                "staging_root_invalid",
                "The configured integration staging root must be an absolute path.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        if (!Directory.Exists(root))
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Absent,
                "staging_root_absent",
                "The configured integration staging root does not exist.");
        }

        using var handle = FleetWindowsFileSafety.OpenDirectory(root, allowDelete: false);
        FleetWindowsFileSafety.ValidateDirectory(handle, root);
        return root;
    }

    private static void ValidateSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or "..")
        {
            throw Reject("path_traversal", "Empty, '.' and '..' path segments are not allowed.");
        }
        if (segment.Length > MaximumSegmentLength)
        {
            throw Reject("path_segment_too_long", $"Path segments may contain at most {MaximumSegmentLength} characters.");
        }
        if (segment.Any(static character => char.IsControl(character)) ||
            segment.IndexOfAny(InvalidWindowsCharacters) >= 0)
        {
            throw Reject("path_character_invalid", "The relative path contains a control or Windows-reserved character.");
        }
        if (segment.EndsWith(' ') || segment.EndsWith('.'))
        {
            throw Reject("path_name_invalid", "Path segments must not end with a space or period.");
        }

        var baseName = segment.Split('.', 2)[0];
        if (ReservedWindowsNames.Contains(baseName))
        {
            throw Reject("path_name_reserved", $"'{segment}' is reserved on Windows.");
        }
    }

    private static bool IsIdentifier(string value) =>
        value.Length is >= 1 and <= 64 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static FleetIntegrationException Reject(string code, string message) =>
        new(FleetIntegrationStatus.Rejected, code, message);

    private static HashSet<string> CreateReservedWindowsNames()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL"
        };
        for (var index = 1; index <= 9; index++)
        {
            values.Add($"COM{index}");
            values.Add($"LPT{index}");
        }
        return values;
    }
}

public sealed class FleetPushSource : IDisposable
{
    private readonly IReadOnlyList<SafeFileHandle> _directoryHandles;
    private readonly FileStream _stream;
    private bool _disposed;

    private FleetPushSource(
        string configuredRoot,
        string relativePath,
        string fullPath,
        long sizeBytes,
        string sha256,
        IReadOnlyList<SafeFileHandle> directoryHandles,
        FileStream stream)
    {
        ConfiguredRoot = configuredRoot;
        RelativePath = relativePath;
        FullPath = fullPath;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        _directoryHandles = directoryHandles;
        _stream = stream;
    }

    public string ConfiguredRoot { get; }

    public string RelativePath { get; }

    public string FullPath { get; }

    public long SizeBytes { get; }

    public string Sha256 { get; }

    public Stream InputStream
    {
        get
        {
            ThrowIfDisposed();
            return _stream;
        }
    }

    public static FleetPushSource Open(
        string configuredRoot,
        string relativePath,
        long expectedSizeBytes,
        string expectedSha256)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secure Fleet push staging requires Windows.");
        }
        var segments = FleetPathPolicy.ValidatePushArtifactPath(relativePath);
        var root = FleetPathPolicy.RequireSafeExistingRoot(configuredRoot);
        var handles = new List<SafeFileHandle>();
        FileStream? stream = null;
        try
        {
            var current = root;
            var rootHandle = FleetWindowsFileSafety.OpenDirectory(current, allowDelete: false);
            FleetWindowsFileSafety.ValidateDirectory(rootHandle, current);
            handles.Add(rootHandle);
            for (var index = 0; index < segments.Count - 1; index++)
            {
                current = Path.Combine(current, segments[index]);
                var handle = FleetWindowsFileSafety.OpenDirectory(current, allowDelete: false);
                FleetWindowsFileSafety.ValidateDirectory(handle, current);
                handles.Add(handle);
            }

            var fullPath = Path.Combine(root, Path.Combine(segments.ToArray()));
            stream = FleetWindowsFileSafety.OpenReadOnlyFile(fullPath);
            FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, fullPath, requireSingleLink: true);
            var size = stream.Length;
            if (size != expectedSizeBytes || size is < 1 or > FleetIntegrationContract.MaximumPushBytes)
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Rejected,
                    "push_source_size_mismatch",
                    $"The staged push input is {size} bytes; the request binds {expectedSizeBytes} bytes.");
            }
            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            stream.Position = 0;
            if (!string.Equals(digest, expectedSha256, StringComparison.Ordinal))
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Rejected,
                    "push_source_digest_mismatch",
                    "The staged push input SHA-256 does not match the request.");
            }

            var source = new FleetPushSource(
                root,
                relativePath,
                fullPath,
                size,
                digest,
                handles.ToArray(),
                stream);
            stream = null;
            source.Validate();
            return source;
        }
        catch
        {
            stream?.Dispose();
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
            throw;
        }
    }

    public void RewindAndValidate()
    {
        Validate();
        _stream.Position = 0;
    }

    public void Validate()
    {
        ThrowIfDisposed();
        var current = ConfiguredRoot;
        FleetWindowsFileSafety.ValidateDirectory(_directoryHandles[0], current);
        var segments = FleetPathPolicy.ValidatePushArtifactPath(RelativePath);
        for (var index = 0; index < segments.Count - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            FleetWindowsFileSafety.ValidateDirectory(_directoryHandles[index + 1], current);
        }
        FleetWindowsFileSafety.ValidateFile(_stream.SafeFileHandle, FullPath, requireSingleLink: true);
        if (_stream.Length != SizeBytes)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "push_source_changed",
                "The retained staged push input changed length.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _stream.Dispose();
        foreach (var handle in _directoryHandles.Reverse())
        {
            handle.Dispose();
        }
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed class FleetPullDestination : IDisposable
{
    private readonly SafeFileHandle _rootHandle;
    private readonly SafeFileHandle _operationsHandle;
    private SafeFileHandle? _operationHandle;
    private SafeFileHandle? _reservationHandle;
    private FileStream? _outputStream;
    private bool _committed;
    private bool _aborted;
    private bool _disposed;

    private FleetPullDestination(
        string configuredRoot,
        string operationsRoot,
        string operationRoot,
        string outputPath,
        string reservationPath,
        SafeFileHandle rootHandle,
        SafeFileHandle operationsHandle,
        SafeFileHandle operationHandle,
        SafeFileHandle reservationHandle,
        FileStream outputStream)
    {
        ConfiguredRoot = configuredRoot;
        OperationsRoot = operationsRoot;
        OperationRoot = operationRoot;
        OutputPath = outputPath;
        ReservationPath = reservationPath;
        _rootHandle = rootHandle;
        _operationsHandle = operationsHandle;
        _operationHandle = operationHandle;
        _reservationHandle = reservationHandle;
        _outputStream = outputStream;
    }

    public string ConfiguredRoot { get; }

    public string OperationsRoot { get; }

    public string OperationRoot { get; }

    public string OutputPath { get; }

    public string ReservationPath { get; }

    public Stream OutputStream =>
        _outputStream ?? throw new ObjectDisposedException(nameof(FleetPullDestination));

    public static FleetPullDestination Create(string configuredRoot, string operationId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secure Fleet pull staging requires Windows.");
        }

        SafeFileHandle? rootHandle = null;
        SafeFileHandle? operationsHandle = null;
        SafeFileHandle? operationHandle = null;
        SafeFileHandle? reservationHandle = null;
        FileStream? outputStream = null;
        var reservationCreated = false;
        var operationsRoot = Path.Combine(configuredRoot, "operations");
        var operationRoot = Path.Combine(operationsRoot, operationId);
        var reservationPath = Path.Combine(operationsRoot, operationId + ".lock");
        var outputPath = Path.Combine(operationRoot, "payload.bin");
        try
        {
            rootHandle = FleetWindowsFileSafety.OpenDirectory(configuredRoot, allowDelete: false);
            FleetWindowsFileSafety.ValidateDirectory(rootHandle, configuredRoot);

            if (!Directory.Exists(operationsRoot))
            {
                Directory.CreateDirectory(operationsRoot);
            }
            operationsHandle = FleetWindowsFileSafety.OpenDirectory(operationsRoot, allowDelete: false);
            FleetWindowsFileSafety.ValidateDirectory(operationsHandle, operationsRoot);

            reservationHandle = FleetWindowsFileSafety.CreateNewOwnedFileHandle(reservationPath);
            reservationCreated = true;
            FleetWindowsFileSafety.ValidateFile(
                reservationHandle,
                reservationPath,
                requireSingleLink: true);

            if (Directory.Exists(operationRoot) || File.Exists(operationRoot))
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Rejected,
                    "destination_collision",
                    "The pull operation ID is already staged. Pull operation IDs are one-use.");
            }

            Directory.CreateDirectory(operationRoot);
            operationHandle = FleetWindowsFileSafety.OpenDirectory(operationRoot, allowDelete: true);
            FleetWindowsFileSafety.ValidateDirectory(operationHandle, operationRoot);

            outputStream = FleetWindowsFileSafety.CreateNewOwnedFile(outputPath);
            var destination = new FleetPullDestination(
                configuredRoot,
                operationsRoot,
                operationRoot,
                outputPath,
                reservationPath,
                rootHandle,
                operationsHandle,
                operationHandle,
                reservationHandle,
                outputStream);
            destination.ValidateForWrite();
            return destination;
        }
        catch
        {
            TryDeleteOwnedFile(outputStream);
            TryDeleteOwnedDirectory(operationHandle);
            if (reservationCreated)
            {
                reservationHandle?.Dispose();
            }
            else
            {
                TryDeleteOwnedHandle(reservationHandle);
            }
            rootHandle?.Dispose();
            operationsHandle?.Dispose();
            throw;
        }
    }

    public void ValidateForWrite()
    {
        ThrowIfDisposed();
        FleetWindowsFileSafety.ValidateDirectory(_rootHandle, ConfiguredRoot);
        FleetWindowsFileSafety.ValidateDirectory(_operationsHandle, OperationsRoot);
        FleetWindowsFileSafety.ValidateDirectory(
            _operationHandle ?? throw new ObjectDisposedException(nameof(FleetPullDestination)),
            OperationRoot);
        FleetWindowsFileSafety.ValidateFile(
            _reservationHandle
            ?? throw new ObjectDisposedException(nameof(FleetPullDestination)),
            ReservationPath,
            requireSingleLink: true);
        FleetWindowsFileSafety.ValidateFile(
            _outputStream?.SafeFileHandle
            ?? throw new ObjectDisposedException(nameof(FleetPullDestination)),
            OutputPath,
            requireSingleLink: true);
    }

    public void FlushAndValidate(long expectedBytes)
    {
        ThrowIfDisposed();
        var output = _outputStream
            ?? throw new ObjectDisposedException(nameof(FleetPullDestination));
        output.Flush(flushToDisk: true);
        if (output.Length != expectedBytes || output.Position != expectedBytes)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "owned_output_length_mismatch",
                "The owned output handle length does not match the streamed byte count.");
        }
        ValidateForWrite();
    }

    public void Commit()
    {
        ValidateForWrite();
        var reservationFailure = DeleteOwnedHandle(_reservationHandle, null);
        _reservationHandle = null;
        if (reservationFailure is not null)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "reservation_release_failed",
                $"The operation reservation could not be released by handle: {reservationFailure.Message}",
                retryable: true,
                reservationFailure);
        }
        _committed = true;
    }

    public void Abort()
    {
        if (_aborted || _committed)
        {
            return;
        }
        _aborted = true;

        Exception? failure = null;
        failure = DeleteOwnedFile(_outputStream, failure);
        _outputStream = null;
        failure = DeleteOwnedDirectory(_operationHandle, failure);
        _operationHandle = null;
        _reservationHandle?.Dispose();
        _reservationHandle = null;
        if (failure is not null)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "cleanup_failed",
                $"Operation-owned handle cleanup failed without path traversal: {failure.Message}",
                retryable: true,
                failure);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_committed && !_aborted)
            {
                Abort();
            }
        }
        finally
        {
            DisposeOwnedFile(_outputStream);
            _reservationHandle?.Dispose();
            _operationHandle?.Dispose();
            _operationsHandle.Dispose();
            _rootHandle.Dispose();
            _disposed = true;
        }
    }

    private static Exception? DeleteOwnedFile(FileStream? stream, Exception? failure)
    {
        if (stream is null)
        {
            return failure;
        }
        try
        {
            var handle = stream.SafeFileHandle;
            FleetWindowsFileSafety.MarkDelete(handle);
            stream.Dispose();
            handle.Dispose();
        }
        catch (Exception exception)
        {
            var handle = stream.SafeFileHandle;
            stream.Dispose();
            handle.Dispose();
            return failure ?? exception;
        }
        return failure;
    }

    private static Exception? DeleteOwnedDirectory(SafeFileHandle? handle, Exception? failure)
    {
        if (handle is null)
        {
            return failure;
        }
        try
        {
            FleetWindowsFileSafety.MarkDelete(handle);
            handle.Dispose();
        }
        catch (Exception exception)
        {
            handle.Dispose();
            return failure ?? exception;
        }
        return failure;
    }

    private static Exception? DeleteOwnedHandle(SafeFileHandle? handle, Exception? failure)
    {
        if (handle is null)
        {
            return failure;
        }
        try
        {
            FleetWindowsFileSafety.MarkDelete(handle);
            handle.Dispose();
        }
        catch (Exception exception)
        {
            handle.Dispose();
            return failure ?? exception;
        }
        return failure;
    }

    private static void TryDeleteOwnedFile(FileStream? stream)
    {
        try
        {
            DeleteOwnedFile(stream, null);
        }
        catch
        {
            // Creation failure remains authoritative; cleanup used only owned handles.
        }
    }

    private static void TryDeleteOwnedDirectory(SafeFileHandle? handle)
    {
        try
        {
            DeleteOwnedDirectory(handle, null);
        }
        catch
        {
            // Creation failure remains authoritative; cleanup used only owned handles.
        }
    }

    private static void TryDeleteOwnedHandle(SafeFileHandle? handle)
    {
        try
        {
            DeleteOwnedHandle(handle, null);
        }
        catch
        {
            // Creation failure remains authoritative; cleanup used only the owned handle.
        }
    }

    private static void DisposeOwnedFile(FileStream? stream)
    {
        if (stream is null)
        {
            return;
        }
        var handle = stream.SafeFileHandle;
        stream.Dispose();
        handle.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal static class FleetWindowsFileSafety
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int FileDispositionInfo = 4;
    private const int FileStandardInfo = 1;

    public static SafeFileHandle OpenDirectory(string path, bool allowDelete)
    {
        var access = FileReadAttributes | (allowDelete ? DeleteAccess : 0);
        var handle = CreateFile(
            path,
            access,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        ThrowIfInvalid(handle, $"open directory '{path}'");
        return handle;
    }

    public static FileStream CreateNewOwnedFile(string path)
    {
        var handle = CreateNewOwnedFileHandle(path);
        return new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
    }

    public static FileStream OpenReadOnlyFile(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        ThrowIfInvalid(handle, $"open staged push input '{path}'");
        return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
    }

    public static FileStream CreateNewRetainedReadableFile(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead,
            IntPtr.Zero,
            CreateNew,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        ThrowIfInvalid(handle, $"create retained file '{path}'");
        return new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
    }

    public static FileStream OpenRetainedStagedReadOnlyFile(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        ThrowIfInvalid(handle, $"open retained staged file '{path}'");
        return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
    }

    // Authenticode opens a second path handle and needs broad sharing.
    // Callers must retain identity and re-hash this handle after verification.
    public static FileStream OpenAuthenticodeCompatibleReadOnlyFile(
        string path)
    {
        var handle = CreateFile(
            path,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        ThrowIfInvalid(
            handle,
            $"open Authenticode-compatible file '{path}'");
        return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
    }

    public static FileStream OpenFileForDeletion(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | DeleteAccess,
            0,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        ThrowIfInvalid(handle, $"open file for deletion '{path}'");
        return new FileStream(handle, FileAccess.Read, 4 * 1024, isAsync: false);
    }

    public static FileStream OpenRetainedReadOnlyFile(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | DeleteAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        ThrowIfInvalid(handle, $"open retained file '{path}'");
        return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
    }

    public static FileStream OpenOrCreateExclusiveFile(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            0,
            IntPtr.Zero,
            OpenAlways,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        ThrowIfInvalid(handle, $"open exclusive file '{path}'");
        return new FileStream(handle, FileAccess.ReadWrite, 4 * 1024, isAsync: false);
    }

    public static FleetWindowsFileIdentity GetIdentity(SafeFileHandle handle)
    {
        var information = GetInformation(handle);
        var standard = GetStandardInformation(handle);
        return new FleetWindowsFileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            ((long)information.FileSizeHigh << 32) | information.FileSizeLow,
            information.NumberOfLinks,
            standard.DeletePending,
            (information.FileAttributes & FileAttributeReparsePoint) != 0);
    }

    public static SafeFileHandle CreateNewOwnedFileHandle(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | GenericWrite | DeleteAccess,
            0,
            IntPtr.Zero,
            CreateNew,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 80 or 183)
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Rejected,
                    "destination_collision",
                    "An operation-owned file already exists; overwrite and replay are rejected.");
            }
            throw new Win32Exception(error, $"Could not create owned file '{path}'.");
        }
        return handle;
    }

    public static void ValidateDirectory(SafeFileHandle handle, string expectedPath)
    {
        var information = GetInformation(handle);
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "reparse_point_rejected",
                "A secure staging directory is a reparse point.");
        }
        ValidateFinalPath(handle, expectedPath);
    }

    public static void ValidateFile(
        SafeFileHandle handle,
        string expectedPath,
        bool requireSingleLink)
    {
        var information = GetInformation(handle);
        var standardInformation = GetStandardInformation(handle);
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "final_output_reparse_rejected",
                "The owned output file became a reparse point.");
        }
        if (requireSingleLink && information.NumberOfLinks != 1)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "final_output_hardlink_rejected",
                "The owned output file has another hard link.");
        }
        if (standardInformation.DeletePending)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "final_output_delete_pending",
                "The owned output file was marked for deletion or path substitution.");
        }
        ValidateFinalPath(handle, expectedPath);
    }

    public static void MarkDelete(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not delete the operation-owned object by handle.");
        }
    }

    private static ByHandleFileInformation GetInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read the secure staging handle identity.");
        }
        return information;
    }

    private static FileStandardInformation GetStandardInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileStandardInfo,
                out var information,
                (uint)Marshal.SizeOf<FileStandardInformation>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read the secure staging handle state.");
        }
        return information;
    }

    private static void ValidateFinalPath(SafeFileHandle handle, string expectedPath)
    {
        var buffer = new char[32 * 1024];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not resolve the secure staging handle path.");
        }

        var observed = NormalizeHandlePath(new string(buffer, 0, (int)length));
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath));
        if (!string.Equals(observed, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Rejected,
                "local_path_identity_changed",
                "A secure staging path was renamed, substituted, or resolved through an ancestor indirection.");
        }
    }

    private static string NormalizeHandlePath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[uncPrefix.Length..];
        }
        else if (path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[localPrefix.Length..];
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void ThrowIfInvalid(SafeFileHandle handle, string operation)
    {
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not {operation}.");
        }
    }

    private static SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile) =>
        CreateFileNative(
            ToExtendedPath(fileName),
            desiredAccess,
            shareMode,
            securityAttributes,
            creationDisposition,
            flagsAndAttributes,
            templateFile);

    private static string ToExtendedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + fullPath[2..];
        }
        return @"\\?\" + fullPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)]
        public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)]
        public bool Directory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileNative(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileStandardInformation information,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathSize,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);
}

internal readonly record struct FleetWindowsFileIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex,
    long SizeBytes,
    uint NumberOfLinks,
    bool DeletePending,
    bool IsReparsePoint);
