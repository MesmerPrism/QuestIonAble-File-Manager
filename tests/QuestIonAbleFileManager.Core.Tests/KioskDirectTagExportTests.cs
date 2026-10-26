using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class KioskDirectTagExportTests
{
    private const string Code = "0123-4567-89AB-CDEF-0123-4567-89";
    private const string Document = "{\n  \"schema\":\"rusty.kiosk.app_tags.v1\", \"apps\":[{\"name\":\"Example café\",\"tags\":[\"private-tag\"]}]\n}\n";

    [Fact]
    public async Task MemoryExportReturnsTheExactSignedBytesAndBoundCoreReceiptWithoutAPath()
    {
        var bytes = Encoding.UTF8.GetBytes(Document);
        var handler = new TagsHandler(bytes);
        using var client = Client(handler);
        var command = KioskDirectOperatorCommand.ExportTags();
        var result = await new KioskDirectOperatorExecutor(client).ExecuteAsync(command);
        using var lease = Assert.IsType<KioskDirectTagExportLease>(result.TagExport);
        Assert.Equal(command, result.Command); Assert.Null(command.LocalPath); Assert.False(command.OperatorConfirmed);
        Assert.Equal(KioskDirectOperatorAction.ExportTags, command.Action);
        Assert.Equal(command.ActionId, result.Mutation.ActionId); Assert.Equal(command.Action, result.Mutation.Action);
        Assert.Equal(OperatorMutationStage.Confirmed, result.Mutation.Stage); Assert.Null(result.Mutation.RequestId);
        Assert.Null(result.LocalFileName); Assert.Null(result.KioskResult); Assert.Null(result.Status);
        Assert.Equal("rusty.kiosk.app_tags.v1", lease.Schema);
        Assert.Equal(bytes.Length, lease.SizeBytes); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), lease.Sha256);
        using var copy = new MemoryStream(); await lease.Stream.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray()); Assert.Equal(new[] { "GET /v1/tags" }, handler.Requests);
    }

    [Theory]
    [InlineData("rusty.kiosk.app_tags.v1")]
    [InlineData("rusty.kiosk.app_tags.v2")]
    public void LeaseCopiesExactValidatedBytesAndDoesNotExposeWritableMemory(string schema)
    {
        var bytes = Encoding.UTF8.GetBytes("{ \"schema\":\"" + schema + "\",\"apps\":[] }\n");
        var original = bytes.ToArray();
        using var lease = KioskDirectTagExportLease.Create(bytes);
        Array.Fill(bytes, (byte)'x');
        Assert.Equal(schema, lease.Schema); Assert.Equal(original.Length, lease.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), lease.Sha256);
        Assert.True(lease.Stream.CanRead); Assert.False(lease.Stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => lease.Stream.WriteByte(1));
        Assert.Throws<NotSupportedException>(() => lease.Stream.SetLength(1));
        if (lease.Stream is MemoryStream memory)
        {
            Assert.False(memory.TryGetBuffer(out _)); Assert.Throws<UnauthorizedAccessException>(() => memory.GetBuffer());
        }
        using var copy = new MemoryStream(); lease.Stream.CopyTo(copy);
        Assert.Equal(original, copy.ToArray());
    }

    [Fact]
    public void MaximumBoundedDocumentIsAcceptedAndNextByteIsRejected()
    {
        var prefix = Encoding.UTF8.GetBytes("{\"schema\":\"rusty.kiosk.app_tags.v1\",\"apps\":[]}");
        var bytes = Enumerable.Repeat((byte)' ', 256 * 1024).ToArray(); prefix.CopyTo(bytes, 0);
        using var lease = KioskDirectTagExportLease.Create(bytes);
        Assert.Equal(262144, lease.SizeBytes);
        Assert.Throws<InvalidDataException>(() => KioskDirectTagExportLease.Create(new byte[262145]));
        Assert.Throws<InvalidDataException>(() => KioskDirectTagExportLease.Create(ReadOnlyMemory<byte>.Empty));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"unknown\",\"apps\":[]}")]
    [InlineData("{\"schema\":\"rusty.kiosk.app_tags.v1\",\"apps\":{}}")]
    [InlineData("{\"schema\":\"rusty.kiosk.app_tags.v1\",\"apps\":[{\"name\":\"Example\",\"tags\":[1]}]}")]
    public void InvalidTagSchemaAndEntriesNeverCreateALease(string json) =>
        Assert.Throws<InvalidDataException>(() => KioskDirectTagExportLease.Create(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void LeaseOwnsOnlyTheSuppliedMemorySlice()
    {
        var content = Encoding.UTF8.GetBytes(Document);
        var input = new byte[content.Length + 2]; input[0] = 0xff; input[^1] = 0xff; content.CopyTo(input, 1);
        using var lease = KioskDirectTagExportLease.Create(input.AsMemory(1, content.Length));
        using var copy = new MemoryStream(); lease.Stream.CopyTo(copy);
        Assert.Equal(content, copy.ToArray()); Assert.Equal(content.Length, lease.SizeBytes);
    }

    [Fact]
    public void InvalidUtf8InsideOtherwiseValidJsonIsNotReplacedOrAccepted()
    {
        var bytes = Encoding.UTF8.GetBytes(Document); bytes[Array.LastIndexOf(bytes, (byte)'p')] = 0xff;
        Assert.Throws<InvalidDataException>(() => KioskDirectTagExportLease.Create(bytes));
    }

    [Fact]
    public void DisposeClosesTheBorrowedStreamAndZeroesTheOwnedSnapshotOnly()
    {
        var source = Encoding.UTF8.GetBytes(Document); var original = source.ToArray();
        var lease = KioskDirectTagExportLease.Create(source); var stream = lease.Stream;
        // A private memory witness is necessary to verify erasure without exposing
        // the backing array through the production API.
        var field = Assert.Single(typeof(KioskDirectTagExportLease).GetFields(BindingFlags.Instance | BindingFlags.NonPublic), item => item.FieldType == typeof(byte[]));
        var owned = Assert.IsType<byte[]>(field.GetValue(lease));
        Assert.NotSame(source, owned); Assert.Equal(source, owned);
        lease.Dispose(); lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte()); Assert.False(stream.CanRead);
        Assert.All(owned, item => Assert.Equal(0, item)); Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("invalid-utf8")]
    [InlineData("schema")]
    [InlineData("oversized")]
    public async Task SignedReadFailureCannotReturnAnExportLeaseOrWriteAFile(string failure)
    {
        var bytes = failure == "schema" ? Encoding.UTF8.GetBytes("{\"schema\":\"unknown\",\"apps\":[]}") :
            failure == "oversized" ? new byte[262145] : Encoding.UTF8.GetBytes(Document);
        if (failure == "invalid-utf8") bytes[Array.LastIndexOf(bytes, (byte)'p')] = 0xff;
        var handler = new TagsHandler(bytes, failure == "signature");
        using var client = Client(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new KioskDirectOperatorExecutor(client).ExecuteAsync(KioskDirectOperatorCommand.ExportTags()));
        Assert.Equal(new[] { "GET /v1/tags" }, handler.Requests);
    }

    [Fact]
    public async Task ExistingFileExportAndPrimaryConstructorDeconstructionAndJsonRemainCompatible()
    {
        var root = Path.Combine(Path.GetTempPath(), "qfm-tag-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "tags.json");
        var bytes = Encoding.UTF8.GetBytes(Document); var handler = new TagsHandler(bytes);
        try
        {
            using var client = Client(handler);
            var command = KioskDirectOperatorCommand.ExportTags(path);
            var file = await new KioskDirectOperatorExecutor(client).ExecuteAsync(command);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); Assert.Null(file.TagExport); Assert.Equal("tags.json", file.LocalFileName);
            // Legacy overwrite behavior is retained too.
            await File.WriteAllTextAsync(path, "replace me");
            await new KioskDirectOperatorExecutor(client).ExecuteAsync(command);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            var (action, id, kind, value, requestId, localPath, stagedName, paths, overwrite, confirmed) = command;
            var recreated = new KioskDirectOperatorCommand(action, id, kind, value, requestId, localPath, stagedName, paths, overwrite, confirmed);
            Assert.Equal(command, recreated); Assert.Equal(4, (int)action);
            var (source, mutation, status, kiosk, request, files, staged, localFileName, install) = file;
            var legacy = new KioskDirectOperatorResult(source, mutation, status, kiosk, request, files, staged, localFileName, install);
            using var lease = KioskDirectTagExportLease.Create(bytes);
            Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(legacy with { TagExport = lease }));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(legacy with { TagExport = lease }));
            Assert.Equal(new[] { "Command", "Mutation", "Status", "KioskResult", "RequestReceipt", "StagedFiles", "StagedFile", "LocalFileName", "InstallReceipt" }, json.RootElement.EnumerateObject().Select(item => item.Name));
            Assert.DoesNotContain("private-tag", json.RootElement.GetRawText());
            Assert.Single(typeof(KioskDirectOperatorResult).GetConstructors(), item => item.GetParameters().Length == 9);
            Assert.Single(typeof(KioskDirectOperatorCommand).GetConstructors(), item => item.GetParameters().Length == 10);
        }
        finally { File.Delete(path); Directory.Delete(root); }
    }

    private static RustyKioskDirectClient Client(HttpMessageHandler handler) =>
        new(RustyKioskDirectEndpoint.Parse("http://192.0.2.1:39873", Code), new HttpClient(handler));

    private sealed class TagsHandler(byte[] bytes, bool tamper = false) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            Assert.Equal("GET /v1/tags", Requests[^1]); Assert.Null(request.Content);
            var id = Assert.Single(request.Headers.GetValues("X-Rusty-Request-Id"));
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var content = bytes.ToArray(); if (tamper) content[^1] ^= 1;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            response.Headers.TryAddWithoutValidation("X-Rusty-Request-Id", id);
            response.Headers.TryAddWithoutValidation("X-Rusty-Content-Sha256", sha);
            response.Headers.TryAddWithoutValidation("X-Rusty-Signature", RustyKioskDirectAuth.SignResponse(Code, id, 200, sha));
            return Task.FromResult(response);
        }
    }
}
