using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core;

public sealed class RustyKioskV2ProviderProfile : IDisposable
{
    public RustyKioskV2ProviderProfile(
        string profileId,
        Uri endpoint,
        char[] pairingCode,
        string deviceId)
    {
        ProfileId = profileId;
        Endpoint = endpoint;
        PairingCode = pairingCode;
        DeviceId = deviceId;
        try
        {
            Validate();
        }
        catch
        {
            Array.Clear(PairingCode);
            throw;
        }
    }

    public string ProfileId { get; }
    public Uri Endpoint { get; }
    internal char[] PairingCode { get; }
    public string DeviceId { get; }

    public void Dispose() => Array.Clear(PairingCode);

    private void Validate()
    {
        if (!ProfileToken(ProfileId, 8, 128) ||
            !ProfileToken(DeviceId, 1, 256) ||
            PairingCode.Length is < 26 or > 64 ||
            PairingCode[0] == '-' ||
            PairingCode[^1] == '-' ||
            PairingCode.Zip(PairingCode.Skip(1)).Any(static pair =>
                pair.First == '-' && pair.Second == '-') ||
            PairingCode.Any(static character =>
                !(character is >= '0' and <= '9' or
                    >= 'A' and <= 'H' or
                    'J' or 'K' or 'M' or 'N' or
                    >= 'P' and <= 'T' or
                    >= 'V' and <= 'Z' or '-')) ||
            !Endpoint.IsAbsoluteUri ||
            !string.Equals(Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(Endpoint.Query) ||
            !string.IsNullOrEmpty(Endpoint.Fragment) ||
            Endpoint.Port != 39873 ||
            Endpoint.AbsolutePath != "/")
        {
            throw RustyKioskV2ProviderException.Unavailable("provider_profile_invalid");
        }
    }

    private static bool ProfileToken(string value, int minimum, int maximum) =>
        value.Length >= minimum &&
        value.Length <= maximum &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}

public interface IRustyKioskV2ProviderProfileStore
{
    RustyKioskV2ProviderProfile Open(string profileId);
}

/// <summary>
/// Reads a File Manager-owned profile from the current Windows user's Credential Manager.
/// This type deliberately has no write route; enrollment remains an explicit owner workflow.
/// </summary>
public sealed class WindowsCredentialRustyKioskV2ProviderProfileStore :
    IRustyKioskV2ProviderProfileStore
{
    private const string TargetPrefix = "QuestIonAbleFileManager/RustyKioskV2/";
    private const uint GenericCredential = 1;

    public RustyKioskV2ProviderProfile Open(string profileId)
    {
        if (!OperatingSystem.IsWindows() ||
            profileId.Length is < 8 or > 128 ||
            !profileId.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':'))
        {
            throw RustyKioskV2ProviderException.Unavailable("provider_profile_unavailable");
        }

        if (!CredRead(TargetPrefix + profileId, GenericCredential, 0, out var credentialPointer))
        {
            throw RustyKioskV2ProviderException.Unavailable("provider_profile_unavailable");
        }

        byte[]? credentialBytes = null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero ||
                credential.CredentialBlobSize is 0 or > 4096)
            {
                throw RustyKioskV2ProviderException.Unavailable("provider_profile_invalid");
            }
            credentialBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(
                credential.CredentialBlob,
                credentialBytes,
                0,
                checked((int)credential.CredentialBlobSize));
            return ParseProfile(profileId, credentialBytes);
        }
        catch (RustyKioskV2ProviderException)
        {
            throw;
        }
        catch
        {
            throw RustyKioskV2ProviderException.Unavailable("provider_profile_invalid");
        }
        finally
        {
            if (credentialBytes is not null)
            {
                CryptographicOperations.ZeroMemory(credentialBytes);
            }
            CredFree(credentialPointer);
        }
    }

    internal static RustyKioskV2ProviderProfile ParseProfile(
        string selectedProfileId,
        ReadOnlySpan<byte> utf8Json)
    {
        char[]? pairingCode = null;
        byte[]? profileBytes = null;
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
                    "profile_id",
                    "endpoint",
                    "pairing_code",
                    "device_id"
                ]);
            if (StrictJson.RequiredString(root, "schema", 128) !=
                    "questionable.file_manager.rusty_kiosk_v2_profile.v1" ||
                StrictJson.RequiredToken(root, "profile_id", 8, 128) != selectedProfileId)
            {
                throw RustyKioskV2ProviderException.Unavailable("provider_profile_binding_invalid");
            }
            pairingCode = ReadPairingCode(profileBytes);
            return new RustyKioskV2ProviderProfile(
                selectedProfileId,
                new Uri(StrictJson.RequiredString(root, "endpoint", 256), UriKind.Absolute),
                pairingCode,
                StrictJson.RequiredToken(root, "device_id", 1, 256));
        }
        catch (RustyKioskV2ProviderException)
        {
            if (pairingCode is not null)
            {
                Array.Clear(pairingCode);
            }
            throw;
        }
        catch
        {
            if (pairingCode is not null)
            {
                Array.Clear(pairingCode);
            }
            throw RustyKioskV2ProviderException.Unavailable("provider_profile_invalid");
        }
        finally
        {
            if (profileBytes is not null)
            {
                CryptographicOperations.ZeroMemory(profileBytes);
            }
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
                reader.ValueSpan.Length is < 26 or > 64)
            {
                throw RustyKioskV2ProviderException.Unavailable("provider_profile_invalid");
            }
            var characters = new char[reader.ValueSpan.Length];
            for (var index = 0; index < reader.ValueSpan.Length; index++)
            {
                var value = reader.ValueSpan[index];
                if (value > 0x7f || value < 0x20)
                {
                    Array.Clear(characters);
                    throw RustyKioskV2ProviderException.Unavailable("provider_profile_invalid");
                }
                characters[index] = (char)value;
            }
            return characters;
        }
        throw RustyKioskV2ProviderException.Unavailable("provider_profile_invalid");
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

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);
}
