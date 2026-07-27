using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;

namespace QuestIonAbleFileManager.Core;

public sealed class QuestConnectivityProviderProfile : IDisposable
{
    private readonly char[] _pairingCode;

    public QuestConnectivityProviderProfile(
        string deviceId,
        string usbSerial,
        Uri endpoint,
        char[] pairingCode)
    {
        DeviceId = deviceId;
        UsbSerial = usbSerial;
        Endpoint = endpoint;
        _pairingCode = pairingCode;
        try
        {
            Validate();
        }
        catch
        {
            Array.Clear(_pairingCode);
            throw;
        }
    }

    public string DeviceId { get; }
    public string UsbSerial { get; }
    public Uri Endpoint { get; }

    public RustyKioskDirectEndpoint CreateDirectEndpoint() =>
        RustyKioskDirectEndpoint.Parse(Endpoint.AbsoluteUri, new string(_pairingCode));

    public void Dispose() => Array.Clear(_pairingCode);

    private void Validate()
    {
        if (!QuestConnectivityContract.IsIdentifier(DeviceId))
            throw QuestConnectivityProviderException.Unavailable("providerProfileInvalid");

        try
        {
            AndroidInput.RequireUsbSerial(UsbSerial);
            _ = RustyKioskDirectEndpoint.Parse(
                Endpoint.AbsoluteUri,
                new string(_pairingCode));
        }
        catch (ArgumentException)
        {
            throw QuestConnectivityProviderException.Unavailable("providerProfileInvalid");
        }

        if (!Endpoint.IsAbsoluteUri ||
            !string.Equals(Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(Endpoint.Query) ||
            !string.IsNullOrEmpty(Endpoint.Fragment) ||
            Endpoint.Port != 39873 ||
            Endpoint.AbsolutePath != "/" ||
            !IsPrivateIpv4(Endpoint.Host))
        {
            throw QuestConnectivityProviderException.Unavailable("providerProfileInvalid");
        }
    }

    private static bool IsPrivateIpv4(string host)
    {
        if (!IPAddress.TryParse(host, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 169 && bytes[1] == 254) ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}

public interface IQuestConnectivityProviderProfileStore
{
    QuestConnectivityProviderProfile Open(string deviceId);
}

/// <summary>
/// Reads a File Manager-owned connectivity profile from the current Windows
/// user's Credential Manager. Enrollment intentionally remains a separate,
/// explicit owner operation; the Fleet provider has no credential write path.
/// </summary>
public sealed class WindowsCredentialQuestConnectivityProviderProfileStore :
    IQuestConnectivityProviderProfileStore
{
    private const uint GenericCredential = 1;

    public QuestConnectivityProviderProfile Open(string deviceId)
    {
        if (!OperatingSystem.IsWindows() ||
            !QuestConnectivityContract.IsIdentifier(deviceId))
        {
            throw QuestConnectivityProviderException.Unavailable(
                "providerProfileUnavailable");
        }

        if (!CredRead(
                QuestConnectivityProfileManagementContract.TargetPrefix + deviceId,
                GenericCredential,
                0,
                out var credentialPointer))
        {
            throw QuestConnectivityProviderException.Unavailable(
                "providerProfileUnavailable");
        }

        byte[]? credentialBytes = null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(
                credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero ||
                credential.CredentialBlobSize is 0 or > 4096)
            {
                throw QuestConnectivityProviderException.Unavailable(
                    "providerProfileInvalid");
            }

            credentialBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(
                credential.CredentialBlob,
                credentialBytes,
                0,
                checked((int)credential.CredentialBlobSize));
            return ParseProfile(deviceId, credentialBytes);
        }
        catch (QuestConnectivityProviderException)
        {
            throw;
        }
        catch
        {
            throw QuestConnectivityProviderException.Unavailable(
                "providerProfileInvalid");
        }
        finally
        {
            if (credentialBytes is not null)
                CryptographicOperations.ZeroMemory(credentialBytes);
            CredFree(credentialPointer);
        }
    }

    internal static QuestConnectivityProviderProfile ParseProfile(
        string selectedDeviceId,
        ReadOnlySpan<byte> utf8Json)
    {
        byte[]? profileBytes = null;
        char[]? pairingCode = null;
        try
        {
            profileBytes = utf8Json.ToArray();
            using var document = JsonDocument.Parse(
                profileBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3
                });
            var root = document.RootElement;
            StrictJson.RequireExactObject(
                root,
                [
                    "schema",
                    "device_id",
                    "usb_serial",
                    "endpoint",
                    "pairing_code"
                ]);
            var deviceId = StrictJson.RequiredToken(root, "device_id", 1, 256);
            if (StrictJson.RequiredString(root, "schema", 128) !=
                    "questionable.file_manager.quest_connectivity_profile.v1" ||
                !string.Equals(deviceId, selectedDeviceId, StringComparison.Ordinal))
            {
                throw QuestConnectivityProviderException.Unavailable(
                    "providerProfileBindingInvalid");
            }

            pairingCode = ReadPairingCode(profileBytes);
            return new QuestConnectivityProviderProfile(
                deviceId,
                StrictJson.RequiredString(root, "usb_serial", 256),
                new Uri(
                    StrictJson.RequiredString(root, "endpoint", 256),
                    UriKind.Absolute),
                pairingCode);
        }
        catch (QuestConnectivityProviderException)
        {
            if (pairingCode is not null)
                Array.Clear(pairingCode);
            throw;
        }
        catch
        {
            if (pairingCode is not null)
                Array.Clear(pairingCode);
            throw QuestConnectivityProviderException.Unavailable(
                "providerProfileInvalid");
        }
        finally
        {
            if (profileBytes is not null)
                CryptographicOperations.ZeroMemory(profileBytes);
        }
    }

    private static char[] ReadPairingCode(ReadOnlySpan<byte> profileBytes)
    {
        var reader = new Utf8JsonReader(
            profileBytes,
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
            {
                continue;
            }

            if (!reader.Read() ||
                reader.TokenType != JsonTokenType.String ||
                reader.HasValueSequence ||
                reader.ValueIsEscaped ||
                reader.ValueSpan.Length is < 16 or > 40)
            {
                throw QuestConnectivityProviderException.Unavailable(
                    "providerProfileInvalid");
            }

            var characters = new char[reader.ValueSpan.Length];
            for (var index = 0; index < reader.ValueSpan.Length; index++)
            {
                var value = reader.ValueSpan[index];
                if (value > 0x7f || value < 0x20)
                {
                    Array.Clear(characters);
                    throw QuestConnectivityProviderException.Unavailable(
                        "providerProfileInvalid");
                }
                characters[index] = (char)value;
            }
            return characters;
        }

        throw QuestConnectivityProviderException.Unavailable(
            "providerProfileInvalid");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);
}
