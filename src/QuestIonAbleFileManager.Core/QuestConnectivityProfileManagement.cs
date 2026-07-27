using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core;

public static class QuestConnectivityProfileManagementContract
{
    public const string TargetPrefix = "QuestIonAbleFileManager/QuestConnectivity/";
    public const string EnrollmentSchema =
        "questionable.file_manager.quest_connectivity_profile_enrollment.v1";
    public const string ProfileSchema =
        "questionable.file_manager.quest_connectivity_profile.v1";
    public const string StatusSchema =
        "questionable.file_manager.quest_connectivity_profile_status.v1";
    public const string ListSchema =
        "questionable.file_manager.quest_connectivity_profile_list.v1";
    public const string MutationSchema =
        "questionable.file_manager.quest_connectivity_profile_mutation.v1";
    public const int MaximumPrivateInputBytes = 4096;

    public static bool IsDeviceId(string? value) =>
        QuestConnectivityContract.IsIdentifier(value) &&
        value!.All(static character => !char.IsAsciiLetterUpper(character));
}

public static class QuestConnectivityProfileEnrollmentDocument
{
    public static byte[] Create(
        string deviceId,
        string usbSerial,
        string endpoint,
        string pairingCode)
    {
        if (!QuestConnectivityProfileManagementContract.IsDeviceId(deviceId))
            throw QuestConnectivityProfileManager.Rejected(
                "profileDeviceIdInvalid",
                "Fleet device ID is invalid.");
        usbSerial = AndroidInput.RequireUsbSerial(usbSerial);
        var direct = RustyKioskDirectEndpoint.Parse(endpoint, pairingCode);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "schema",
                QuestConnectivityProfileManagementContract.EnrollmentSchema);
            writer.WriteString(
                "target",
                QuestConnectivityProfileManagementContract.TargetPrefix + deviceId);
            writer.WriteString("device_id", deviceId);
            writer.WriteString("usb_serial", usbSerial);
            writer.WriteString("endpoint", direct.BaseUri.AbsoluteUri);
            writer.WriteString("pairing_code", direct.PairingCode);
            writer.WriteEndObject();
        }
        byte[]? document = null;
        try
        {
            document = output.ToArray();
            using var validation = QuestConnectivityEnrollment.Parse(document);
            return document;
        }
        catch
        {
            if (document is not null)
                CryptographicOperations.ZeroMemory(document);
            throw;
        }
        finally
        {
            ClearMemoryStream(output);
        }
    }

    private static void ClearMemoryStream(MemoryStream stream)
    {
        if (stream.TryGetBuffer(out var buffer) && buffer.Array is not null)
            CryptographicOperations.ZeroMemory(
                buffer.Array.AsSpan(buffer.Offset, buffer.Count));
    }
}

public sealed record QuestConnectivityProfileStatusReceipt(
    string Schema,
    string Status,
    string DeviceId,
    string State,
    string ReasonCode);

public sealed record QuestConnectivityProfileListEntry(
    string DeviceId,
    string State,
    string ReasonCode);

public sealed record QuestConnectivityProfileListReceipt(
    string Schema,
    string Status,
    IReadOnlyList<QuestConnectivityProfileListEntry> Profiles);

public sealed record QuestConnectivityProfileMutationReceipt(
    string Schema,
    string Status,
    string Action,
    string DeviceId,
    string State,
    string ReasonCode);

public enum QuestConnectivityProfileWriteStage
{
    Create,
    Replace
}

public static class QuestConnectivityProfileWriteWorkflow
{
    public static async Task<QuestConnectivityProfileMutationReceipt?> ExecuteAsync(
        Func<QuestConnectivityProfileWriteStage, bool> confirm,
        Func<bool, Task<QuestConnectivityProfileMutationReceipt>> write)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(write);
        if (!confirm(QuestConnectivityProfileWriteStage.Create))
            return null;
        try
        {
            return await write(false);
        }
        catch (QuestConnectivityProfileManagementException exception)
            when (exception.Code == "profileReplaceConfirmationRequired")
        {
            return confirm(QuestConnectivityProfileWriteStage.Replace)
                ? await write(true)
                : null;
        }
    }
}

public sealed class QuestConnectivityProfileManagementException : Exception
{
    public QuestConnectivityProfileManagementException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IQuestConnectivityCredentialStore
{
    IReadOnlyList<string> ListTargets();
    byte[]? Read(string target);
    void Write(string target, ReadOnlySpan<byte> credential);
    bool Delete(string target);
}

public interface IQuestConnectivityPrivateInputReader
{
    Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken);
    Task<byte[]> ReadStreamAsync(Stream stream, CancellationToken cancellationToken);
}

public sealed class QuestConnectivityProfileManager
{
    private readonly IQuestConnectivityCredentialStore _store;
    private readonly IQuestConnectivityPrivateInputReader _inputReader;

    public QuestConnectivityProfileManager(
        IQuestConnectivityCredentialStore store,
        IQuestConnectivityPrivateInputReader inputReader)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _inputReader = inputReader ?? throw new ArgumentNullException(nameof(inputReader));
    }

    public static QuestConnectivityProfileManager CreateWindows() =>
        new(new WindowsQuestConnectivityCredentialStore(), new WindowsPrivateProfileInputReader());

    public QuestConnectivityProfileStatusReceipt GetStatus(string deviceId)
    {
        deviceId = RequireDeviceId(deviceId);
        var target = QuestConnectivityProfileManagementContract.TargetPrefix + deviceId;
        byte[]? credential = null;
        try
        {
            credential = _store.Read(target);
            if (credential is null)
            {
                return new(
                    QuestConnectivityProfileManagementContract.StatusSchema,
                    "ok",
                    deviceId,
                    "absent",
                    "profileAbsent");
            }

            using var profile =
                WindowsCredentialQuestConnectivityProviderProfileStore.ParseProfile(
                    deviceId,
                    credential);
            return new(
                QuestConnectivityProfileManagementContract.StatusSchema,
                "ok",
                deviceId,
                "enrolled",
                "profileEnrolled");
        }
        catch (QuestConnectivityProviderException)
        {
            return new(
                QuestConnectivityProfileManagementContract.StatusSchema,
                "ok",
                deviceId,
                "invalid",
                "profileInvalid");
        }
        catch (QuestConnectivityProfileManagementException exception)
            when (exception.Code == "profileCredentialInvalid")
        {
            return new(
                QuestConnectivityProfileManagementContract.StatusSchema,
                "ok",
                deviceId,
                "invalid",
                "profileInvalid");
        }
        finally
        {
            if (credential is not null)
                CryptographicOperations.ZeroMemory(credential);
        }
    }

    public QuestConnectivityProfileListReceipt List()
    {
        var entries = _store.ListTargets()
            .Where(static target => target.StartsWith(
                QuestConnectivityProfileManagementContract.TargetPrefix,
                StringComparison.Ordinal))
            .Select(static target => target[
                QuestConnectivityProfileManagementContract.TargetPrefix.Length..])
            .Where(QuestConnectivityProfileManagementContract.IsDeviceId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .Select(deviceId =>
            {
                var status = GetStatus(deviceId);
                return new QuestConnectivityProfileListEntry(
                    status.DeviceId,
                    status.State,
                    status.ReasonCode);
            })
            .ToArray();
        return new(
            QuestConnectivityProfileManagementContract.ListSchema,
            "ok",
            entries);
    }

    public async Task<QuestConnectivityProfileMutationReceipt> ImportAsync(
        OperatorCommand command,
        Stream? privateInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind != OperatorCommandKind.ConnectivityProfileImport ||
            !command.OperatorConfirmed)
        {
            throw Rejected(
                "profileWriteConfirmationRequired",
                "Connectivity profile writes require explicit operator confirmation.");
        }
        byte[]? input = null;
        QuestConnectivityEnrollment? enrollment = null;
        byte[]? credential = null;
        try
        {
            input = command.ConnectivityProfileInputKind switch
            {
                QuestConnectivityProfileInputKind.PrivateFile
                    when privateInput is null && command.LocalPath is not null =>
                    await _inputReader.ReadFileAsync(
                        command.LocalPath,
                        cancellationToken).ConfigureAwait(false),
                QuestConnectivityProfileInputKind.StandardInput
                    when privateInput is not null =>
                    await _inputReader.ReadStreamAsync(
                        privateInput,
                        cancellationToken).ConfigureAwait(false),
                QuestConnectivityProfileInputKind.PrivateFile
                    when privateInput is not null =>
                    throw Rejected(
                        "profileInputAmbiguous",
                        "A private file import cannot also receive standard input."),
                QuestConnectivityProfileInputKind.StandardInput =>
                    throw Rejected(
                        "profileStdinRequired",
                        "The standard-input import route requires one private JSON document."),
                _ => throw Rejected(
                    "profileInputInvalid",
                    "Choose exactly one private file or standard-input source.")
            };
            if (input.Length is <= 0 or >
                QuestConnectivityProfileManagementContract.MaximumPrivateInputBytes)
            {
                throw Rejected(
                    "profileInputSizeInvalid",
                    "Private connectivity profile input size is invalid.");
            }

            enrollment = QuestConnectivityEnrollment.Parse(input);
            var existing = _store.Read(enrollment.Target);
            if (existing is not null)
            {
                CryptographicOperations.ZeroMemory(existing);
                if (!command.ReplaceExisting)
                {
                    throw Rejected(
                        "profileReplaceConfirmationRequired",
                        "The profile already exists; repeat with explicit replacement confirmation.");
                }
            }

            credential = enrollment.SerializeCredential();
            _store.Write(enrollment.Target, credential);
            var readback = _store.Read(enrollment.Target);
            try
            {
                if (readback is null ||
                    !CryptographicOperations.FixedTimeEquals(
                        credential,
                        readback))
                {
                    throw Rejected(
                        "profileWriteReadbackFailed",
                        "Credential Manager did not confirm the exact connectivity profile write.");
                }
                using var parsed =
                    WindowsCredentialQuestConnectivityProviderProfileStore.ParseProfile(
                        enrollment.DeviceId,
                        readback);
            }
            finally
            {
                if (readback is not null)
                    CryptographicOperations.ZeroMemory(readback);
            }
            return new(
                QuestConnectivityProfileManagementContract.MutationSchema,
                "confirmed",
                existing is null ? "created" : "replaced",
                enrollment.DeviceId,
                "enrolled",
                existing is null ? "profileCreated" : "profileReplaced");
        }
        finally
        {
            enrollment?.Dispose();
            if (input is not null)
                CryptographicOperations.ZeroMemory(input);
            if (credential is not null)
                CryptographicOperations.ZeroMemory(credential);
        }
    }

    public QuestConnectivityProfileMutationReceipt Revoke(
        string deviceId,
        bool operatorConfirmed)
    {
        if (!operatorConfirmed)
            throw Rejected(
                "profileRevokeConfirmationRequired",
                "Connectivity profile revocation requires explicit operator confirmation.");
        deviceId = RequireDeviceId(deviceId);
        var removed = _store.Delete(
            QuestConnectivityProfileManagementContract.TargetPrefix + deviceId);
        var readback = _store.Read(
            QuestConnectivityProfileManagementContract.TargetPrefix + deviceId);
        if (readback is not null)
        {
            CryptographicOperations.ZeroMemory(readback);
            throw Rejected(
                "profileRevokeReadbackFailed",
                "Credential Manager did not confirm connectivity profile revocation.");
        }
        return new(
            QuestConnectivityProfileManagementContract.MutationSchema,
            "confirmed",
            "revoked",
            deviceId,
            "absent",
            removed ? "profileRevoked" : "profileAlreadyAbsent");
    }

    private static string RequireDeviceId(string value)
    {
        if (!QuestConnectivityProfileManagementContract.IsDeviceId(value))
            throw Rejected("profileDeviceIdInvalid", "Fleet device ID is invalid.");
        return value;
    }

    internal static QuestConnectivityProfileManagementException Rejected(
        string code,
        string message) => new(code, message);
}

internal sealed class QuestConnectivityEnrollment : IDisposable
{
    private readonly char[] _pairingCode;

    private QuestConnectivityEnrollment(
        string target,
        string deviceId,
        string usbSerial,
        Uri endpoint,
        char[] pairingCode)
    {
        Target = target;
        DeviceId = deviceId;
        UsbSerial = usbSerial;
        Endpoint = endpoint;
        _pairingCode = pairingCode;
    }

    public string Target { get; }
    public string DeviceId { get; }
    public string UsbSerial { get; }
    public Uri Endpoint { get; }

    public static QuestConnectivityEnrollment Parse(byte[] input)
    {
        char[]? pairingCode = null;
        try
        {
            using var document = JsonDocument.Parse(
                input,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3
                });
            var root = document.RootElement;
            StrictJson.RequireExactObject(
                root,
                ["schema", "target", "device_id", "usb_serial", "endpoint", "pairing_code"]);
            if (StrictJson.RequiredString(root, "schema", 128) !=
                QuestConnectivityProfileManagementContract.EnrollmentSchema)
            {
                throw QuestConnectivityProfileManager.Rejected(
                    "profileSchemaInvalid",
                    "Connectivity profile enrollment schema is invalid.");
            }

            var deviceId = StrictJson.RequiredToken(root, "device_id", 1, 256);
            if (!QuestConnectivityProfileManagementContract.IsDeviceId(deviceId))
                throw QuestConnectivityProfileManager.Rejected(
                    "profileDeviceIdInvalid",
                    "Fleet device ID must use the lowercase canonical form.");
            var expectedTarget =
                QuestConnectivityProfileManagementContract.TargetPrefix + deviceId;
            var target = StrictJson.RequiredString(root, "target", 512);
            if (!string.Equals(target, expectedTarget, StringComparison.Ordinal))
            {
                throw QuestConnectivityProfileManager.Rejected(
                    "profileTargetInvalid",
                    "Credential target must use the exact File Manager connectivity prefix and device ID.");
            }

            var usbSerial = AndroidInput.RequireUsbSerial(
                StrictJson.RequiredString(root, "usb_serial", 256));
            var endpoint = new Uri(
                StrictJson.RequiredString(root, "endpoint", 256),
                UriKind.Absolute);
            pairingCode = ReadPairingCode(input);
            using (var validation = new QuestConnectivityProviderProfile(
                       deviceId,
                       usbSerial,
                       endpoint,
                       pairingCode.ToArray()))
            {
            }
            return new(target, deviceId, usbSerial, endpoint, pairingCode);
        }
        catch (QuestConnectivityProfileManagementException)
        {
            if (pairingCode is not null)
                Array.Clear(pairingCode);
            throw;
        }
        catch
        {
            if (pairingCode is not null)
                Array.Clear(pairingCode);
            throw QuestConnectivityProfileManager.Rejected(
                "profileDocumentInvalid",
                "The private connectivity profile document is invalid.");
        }
    }

    public byte[] SerializeCredential()
    {
        using var output = new MemoryStream();
        byte[]? credential = null;
        try
        {
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "schema",
                    QuestConnectivityProfileManagementContract.ProfileSchema);
                writer.WriteString("device_id", DeviceId);
                writer.WriteString("usb_serial", UsbSerial);
                writer.WriteString("endpoint", Endpoint.AbsoluteUri);
                writer.WriteString("pairing_code", _pairingCode);
                writer.WriteEndObject();
            }
            credential = output.ToArray();
            using var validation =
                WindowsCredentialQuestConnectivityProviderProfileStore.ParseProfile(
                    DeviceId,
                    credential);
            return credential;
        }
        catch
        {
            if (credential is not null)
                CryptographicOperations.ZeroMemory(credential);
            throw;
        }
        finally
        {
            if (output.TryGetBuffer(out var buffer) && buffer.Array is not null)
                CryptographicOperations.ZeroMemory(
                    buffer.Array.AsSpan(buffer.Offset, buffer.Count));
        }
    }

    public void Dispose() => Array.Clear(_pairingCode);

    private static char[] ReadPairingCode(ReadOnlySpan<byte> input)
    {
        var reader = new Utf8JsonReader(
            input,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName ||
                !reader.ValueTextEquals("pairing_code"u8))
                continue;
            if (!reader.Read() ||
                reader.TokenType != JsonTokenType.String ||
                reader.HasValueSequence ||
                reader.ValueIsEscaped ||
                reader.ValueSpan.Length is < 16 or > 40)
            {
                throw QuestConnectivityProfileManager.Rejected(
                    "profilePairingCodeInvalid",
                    "Kiosk pairing code is invalid.");
            }

            var result = new char[reader.ValueSpan.Length];
            for (var index = 0; index < reader.ValueSpan.Length; index++)
            {
                var value = reader.ValueSpan[index];
                if (value is < 0x21 or > 0x7e)
                {
                    Array.Clear(result);
                    throw QuestConnectivityProfileManager.Rejected(
                        "profilePairingCodeInvalid",
                        "Kiosk pairing code is invalid.");
                }
                result[index] = (char)value;
            }
            return result;
        }

        throw QuestConnectivityProfileManager.Rejected(
            "profilePairingCodeInvalid",
            "Kiosk pairing code is invalid.");
    }
}

public sealed class WindowsPrivateProfileInputReader :
    IQuestConnectivityPrivateInputReader
{
    public async Task<byte[]> ReadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw QuestConnectivityProfileManager.Rejected(
                "profilePrivateFileUnsupported",
                "Private connectivity profile files require Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.AsSpan(2).Contains(':'))
        {
            throw QuestConnectivityProfileManager.Rejected(
                "profilePrivateFilePathInvalid",
                "Use one fully qualified local file path without alternate data streams.");
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (root is null ||
                new DriveInfo(root).DriveType is not
                    (DriveType.Fixed or DriveType.Ram))
            {
                throw QuestConnectivityProfileManager.Rejected(
                    "profilePrivateFileDriveInvalid",
                    "The private profile must be on one local fixed or RAM-backed drive.");
            }
            RejectReparseComponents(fullPath);
            using var handle = CreateFile(
                fullPath,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!string.Equals(
                    ResolvePath(handle),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase) ||
                !GetFileInformationByHandle(handle, out var information) ||
                (information.FileAttributes &
                 ((uint)FileAttributes.Directory |
                  (uint)FileAttributes.ReparsePoint |
                  (uint)FileAttributes.Offline)) != 0 ||
                information.NumberOfLinks != 1)
            {
                throw QuestConnectivityProfileManager.Rejected(
                    "profilePrivateFileUnsafe",
                    "The private profile must be one local, online, non-reparse, single-link regular file.");
            }

            EnsurePrivateAcl(handle);
            using var stream = new FileStream(handle, FileAccess.Read);
            var length = stream.Length;
            if (length is <= 0 or >
                QuestConnectivityProfileManagementContract.MaximumPrivateInputBytes)
            {
                throw QuestConnectivityProfileManager.Rejected(
                    "profileInputSizeInvalid",
                    "Private connectivity profile input size is invalid.");
            }
            var result = new byte[checked((int)length)];
            var read = 0;
            while (read < result.Length)
            {
                var count = await stream.ReadAsync(
                    result.AsMemory(read),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    break;
                read += count;
            }
            if (read != result.Length || stream.Length != length)
            {
                CryptographicOperations.ZeroMemory(result);
                throw QuestConnectivityProfileManager.Rejected(
                    "profilePrivateFileChanged",
                    "The private profile file changed while it was being read.");
            }
            return result;
        }
        catch (QuestConnectivityProfileManagementException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            throw QuestConnectivityProfileManager.Rejected(
                "profilePrivateFileUnavailable",
                "The protected connectivity profile file could not be opened safely.");
        }
    }

    public async Task<byte[]> ReadStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read >
                    QuestConnectivityProfileManagementContract.MaximumPrivateInputBytes)
                {
                    throw QuestConnectivityProfileManager.Rejected(
                        "profileInputSizeInvalid",
                        "Private connectivity profile input size is invalid.");
                }
                output.Write(buffer, 0, read);
            }
            if (output.Length == 0)
                throw QuestConnectivityProfileManager.Rejected(
                    "profileInputSizeInvalid",
                    "Private connectivity profile input size is invalid.");
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var contents) && contents.Array is not null)
                CryptographicOperations.ZeroMemory(
                    contents.Array.AsSpan(contents.Offset, contents.Count));
        }
    }

    private static string ResolvePath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(
            handle,
            buffer,
            checked((uint)buffer.Capacity),
            0);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var resolved = buffer.ToString();
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
            resolved = resolved[4..];
        return Path.GetFullPath(resolved);
    }

    private static void RejectReparseComponents(string path)
    {
        for (var current = new FileInfo(path).Directory;
             current is not null;
             current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw QuestConnectivityProfileManager.Rejected(
                    "profilePrivateFileReparseRejected",
                    "Private profile paths cannot traverse reparse points.");
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw QuestConnectivityProfileManager.Rejected(
                "profilePrivateFileReparseRejected",
                "Private profile paths cannot traverse reparse points.");
    }

    [SupportedOSPlatform("windows")]
    private static void EnsurePrivateAcl(SafeFileHandle handle)
    {
        var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ??
            throw QuestConnectivityProfileManager.Rejected(
                "profilePrivateFileAclInvalid",
                "Current Windows identity is unavailable.");
        var permitted = new HashSet<string>(StringComparer.Ordinal)
        {
            currentUser.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
        };
        const uint securityInformation = 0x00000001 | 0x00000004;
        _ = GetKernelObjectSecurity(
            handle,
            securityInformation,
            null,
            0,
            out var required);
        if (required == 0)
            throw QuestConnectivityProfileManager.Rejected(
                "profilePrivateFileAclInvalid",
                "Private profile security information is unavailable.");
        var descriptorBytes = new byte[required];
        if (!GetKernelObjectSecurity(
                handle,
                securityInformation,
                descriptorBytes,
                checked((uint)descriptorBytes.Length),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var security = new RawSecurityDescriptor(descriptorBytes, 0);
        if (security.Owner is not SecurityIdentifier owner ||
            !permitted.Contains(owner.Value))
        {
            throw QuestConnectivityProfileManager.Rejected(
                "profilePrivateFileAclInvalid",
                "Private profile ownership is not restricted to the current user or Windows administrators.");
        }
        if (security.DiscretionaryAcl is null)
            throw QuestConnectivityProfileManager.Rejected(
                "profilePrivateFileAclInvalid",
                "Private profile access is not restricted.");
        foreach (var ace in security.DiscretionaryAcl)
        {
            if (ace is QualifiedAce
                {
                    AceQualifier: AceQualifier.AccessAllowed,
                    SecurityIdentifier: { } sid
                } &&
                !permitted.Contains(sid.Value))
            {
                throw QuestConnectivityProfileManager.Rejected(
                    "profilePrivateFileAclInvalid",
                    "Private profile read access is broader than the current user and Windows administrators.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeFileHandle handle,
        uint requestedInformation,
        byte[]? securityDescriptor,
        uint length,
        out uint needed);
}

public sealed class WindowsQuestConnectivityCredentialStore :
    IQuestConnectivityCredentialStore
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;

    public IReadOnlyList<string> ListTargets()
    {
        EnsureWindows();
        if (!CredEnumerate(
                QuestConnectivityProfileManagementContract.TargetPrefix + "*",
                0,
                out var count,
                out var credentials))
        {
            return Marshal.GetLastWin32Error() == 1168
                ? []
                : throw QuestConnectivityProfileManager.Rejected(
                    "profileStoreUnavailable",
                    "Windows Credential Manager is unavailable.");
        }
        try
        {
            var result = new List<string>(checked((int)count));
            for (var index = 0; index < count; index++)
            {
                var pointer = Marshal.ReadIntPtr(credentials, checked((int)index * IntPtr.Size));
                var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
                var target = credential.TargetName;
                if (target is not null)
                    result.Add(target);
            }
            return result;
        }
        finally
        {
            CredFree(credentials);
        }
    }

    public byte[]? Read(string target)
    {
        EnsureTarget(target);
        if (!CredRead(target, GenericCredential, 0, out var pointer))
        {
            return Marshal.GetLastWin32Error() == 1168
                ? null
                : throw QuestConnectivityProfileManager.Rejected(
                    "profileStoreUnavailable",
                    "Windows Credential Manager is unavailable.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero ||
                credential.CredentialBlobSize is 0 or >
                    QuestConnectivityProfileManagementContract.MaximumPrivateInputBytes)
            {
                throw QuestConnectivityProfileManager.Rejected(
                    "profileCredentialInvalid",
                    "Stored connectivity profile encoding is invalid.");
            }
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(
                credential.CredentialBlob,
                bytes,
                0,
                checked((int)credential.CredentialBlobSize));
            return bytes;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string target, ReadOnlySpan<byte> credential)
    {
        EnsureTarget(target);
        if (credential.Length is <= 0 or >
            QuestConnectivityProfileManagementContract.MaximumPrivateInputBytes)
        {
            throw QuestConnectivityProfileManager.Rejected(
                "profileCredentialInvalid",
                "Stored connectivity profile encoding is invalid.");
        }
        var bytes = credential.ToArray();
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        var zero = new byte[bytes.Length];
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var native = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = string.Empty
            };
            if (!CredWrite(ref native, 0))
                throw QuestConnectivityProfileManager.Rejected(
                    "profileStoreWriteFailed",
                    "Windows Credential Manager rejected the connectivity profile write.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(zero, 0, blob, zero.Length);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public bool Delete(string target)
    {
        EnsureTarget(target);
        if (CredDelete(target, GenericCredential, 0))
            return true;
        if (Marshal.GetLastWin32Error() == 1168)
            return false;
        throw QuestConnectivityProfileManager.Rejected(
            "profileStoreDeleteFailed",
            "Windows Credential Manager rejected the connectivity profile revocation.");
    }

    private static void EnsureTarget(string target)
    {
        EnsureWindows();
        if (!target.StartsWith(
                QuestConnectivityProfileManagementContract.TargetPrefix,
                StringComparison.Ordinal) ||
            !QuestConnectivityProfileManagementContract.IsDeviceId(
                target[QuestConnectivityProfileManagementContract.TargetPrefix.Length..]))
        {
            throw QuestConnectivityProfileManager.Rejected(
                "profileTargetInvalid",
                "Credential target is outside the File Manager connectivity namespace.");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw QuestConnectivityProfileManager.Rejected(
                "profileStoreUnavailable",
                "Windows Credential Manager is unavailable.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(
        string filter,
        uint flags,
        out uint count,
        out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);
}
