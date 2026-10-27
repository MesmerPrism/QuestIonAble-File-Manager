using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core;

/// <summary>
/// An owned, validated snapshot of tag-file bytes. Create validates local bytes;
/// signed provider authority belongs to the Direct client and executor.
/// Dispose closes the stream and zeroes this lease's buffer, but cannot erase
/// runtime/decoder copies made while validating or copies made by consumers.
/// </summary>
public sealed class KioskDirectTagExportLease : IDisposable
{
    private readonly byte[] _bytes;
    private readonly MemoryStream _stream;
    private int _disposed;

    private KioskDirectTagExportLease(byte[] bytes, string schema)
    {
        _bytes = bytes;
        Schema = schema;
        SizeBytes = bytes.LongLength;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        _stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false);
    }

    public string Schema { get; }
    public long SizeBytes { get; }
    public string Sha256 { get; }
    public Stream Stream => _stream;

    public static KioskDirectTagExportLease Create(ReadOnlyMemory<byte> source)
    {
        if (source.Length is < 1 or > RustyKioskContract.MaxTagFileBytes)
        {
            throw new InvalidDataException("The tag export is empty or exceeds the bounded size.");
        }
        // Validate the owned snapshot, never caller-owned mutable memory.
        var bytes = source.ToArray();
        try
        {
            string json;
            try { json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes); }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The tag export is not valid UTF-8.", exception);
            }
            _ = RustyKioskTagFile.Validate(json);
            using var document = JsonDocument.Parse(json);
            var schema = document.RootElement.GetProperty("schema").GetString()!;
            return new KioskDirectTagExportLease(bytes, schema);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stream.Dispose();
        CryptographicOperations.ZeroMemory(_bytes);
    }
}
