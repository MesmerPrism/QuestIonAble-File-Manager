using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class LocalApiV3RequestTests
{
    private const string Version = "questionable.file_manager.local_api.v3";
    private const string OperationId = "api-0123456789abcdef0123456789abcdef";
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Preflight_DefaultsToExistingLifetimeWithoutAddingOutputOrInstallFields()
    {
        var request = new LocalApiV3PreflightRequest(Version, "apk.diagnose", "base.apk", "QUEST-01");
        request.Validate();
        Assert.Equal(Version, LocalApiV3Contract.Version);
        Assert.Null(request.ExpiresInSeconds);
        Assert.Equal(120, LocalApiContract.DefaultPreflightLifetime.TotalSeconds);
        Assert.Equal(300, LocalApiContract.MaximumPreflightLifetime.TotalSeconds);
        Assert.Equal(new[] { "ContractVersion", "Command", "ApkPath", "Serial", "ExpiresInSeconds" },
            typeof(LocalApiV3PreflightRequest).GetProperties().Select(property => property.Name));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(120)]
    [InlineData(300)]
    public void Preflight_AcceptsOnlyPositiveLifetimeUpToFiveMinutes(int seconds) =>
        (Preflight() with { ExpiresInSeconds = seconds }).Validate();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(301)]
    [InlineData(int.MaxValue)]
    public void Preflight_RejectsLifetimeOutsideBound(int seconds) =>
        AssertCode("expiry_invalid", () => (Preflight() with { ExpiresInSeconds = seconds }).Validate());

    [Theory]
    [InlineData("questionable.file_manager.local_api.v1")]
    [InlineData("questionable.file_manager.local_api.v2")]
    [InlineData("questionable.file_manager.local_api.V3")]
    [InlineData("questionable.file_manager.local_api.v3\n")]
    [InlineData("")]
    [InlineData(null)]
    public void EveryRequest_RejectsOtherVersionWithoutFallback(string? version)
    {
        AssertCode("contract_unsupported", () => (Preflight() with { ContractVersion = version! }).Validate());
        AssertCode("contract_unsupported", () => new LocalApiV3ExecuteRequest(version!, OperationId, Digest).Validate());
        AssertCode("contract_unsupported", () => new LocalApiV3OperationRequest(version!, OperationId, Digest).Validate());
    }

    [Theory]
    [InlineData("apk.inspect")]
    [InlineData("apk.deploy")]
    [InlineData("apk.install-inspected")]
    [InlineData("app.launch-resolved")]
    [InlineData("runtime.observe")]
    [InlineData("apk.Diagnose")]
    [InlineData("apk.diagnose ")]
    [InlineData("adb.raw")]
    [InlineData(null)]
    public void Preflight_RejectsEveryOtherCommand(string? command) =>
        AssertCode("command_not_allowed", () => (Preflight() with { Command = command! }).Validate());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Preflight_RequiresSourceAndExactSerial(string? value)
    {
        AssertCode("apk_path_required", () => (Preflight() with { ApkPath = value! }).Validate());
        AssertCode("serial_required", () => (Preflight() with { Serial = value! }).Validate());
    }

    [Theory]
    [InlineData("QUEST-01")]
    [InlineData("192.0.2.1:5555")]
    [InlineData("quest._:01")]
    public void Preflight_PreservesExactSerialWithoutSelectingOrNormalizing(string serial)
    {
        var request = Preflight() with { Serial = serial, ApkPath = "relative source.apk" };
        request.Validate();
        Assert.Equal(serial, request.Serial);
        Assert.Equal("relative source.apk", request.ApkPath);
    }

    [Theory]
    [InlineData(" QUEST-01")]
    [InlineData("QUEST-01 ")]
    [InlineData("QUEST-01\n")]
    [InlineData("QUEST-01\r")]
    [InlineData("QUEST\t01")]
    [InlineData("QUEST;other")]
    [InlineData("QUEST/01")]
    public void Preflight_RejectsInvalidSerialRatherThanChangingIt(string serial) =>
        AssertCode("serial_invalid", () => (Preflight() with { Serial = serial }).Validate());

    [Fact]
    public void ExecuteStatusAndCancel_RequireOriginalDigestAndHaveSameClosedFields()
    {
        new LocalApiV3ExecuteRequest(Version, OperationId, Digest).Validate();
        new LocalApiV3OperationRequest(Version, OperationId, Digest).Validate();
        var fields = new[] { "ContractVersion", "OperationId", "CommandDigest" };
        Assert.Equal(fields, typeof(LocalApiV3ExecuteRequest).GetProperties().Select(property => property.Name));
        Assert.Equal(fields, typeof(LocalApiV3OperationRequest).GetProperties().Select(property => property.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void ExecuteStatusAndCancel_RejectMissingIdentityOrDigest(string? value)
    {
        AssertCode("request_invalid", () => new LocalApiV3ExecuteRequest(Version, value!, Digest).Validate());
        AssertCode("request_invalid", () => new LocalApiV3ExecuteRequest(Version, OperationId, value!).Validate());
        AssertCode("request_invalid", () => new LocalApiV3OperationRequest(Version, value!, Digest).Validate());
        AssertCode("request_invalid", () => new LocalApiV3OperationRequest(Version, OperationId, value!).Validate());
    }

    [Theory]
    [InlineData("api-00000000000000000000000000000000")]
    [InlineData("api-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("API-0123456789abcdef0123456789abcdef")]
    [InlineData("api-0123456789abcdef0123456789abcdef\n")]
    [InlineData(" api-0123456789abcdef0123456789abcdef")]
    [InlineData("api-01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("api-0123456789abcdef0123456789abcde")]
    [InlineData("api-0123456789abcdef0123456789abcdeg")]
    public void ExecuteStatusAndCancel_RejectNoncanonicalOperationIds(string id)
    {
        AssertCode("request_invalid", () => new LocalApiV3ExecuteRequest(Version, id, Digest).Validate());
        AssertCode("request_invalid", () => new LocalApiV3OperationRequest(Version, id, Digest).Validate());
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n")]
    [InlineData(" 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    public void ExecuteStatusAndCancel_RejectNoncanonicalDigests(string digest)
    {
        AssertCode("request_invalid", () => new LocalApiV3ExecuteRequest(Version, OperationId, digest).Validate());
        AssertCode("request_invalid", () => new LocalApiV3OperationRequest(Version, OperationId, digest).Validate());
    }

    [Fact]
    public void ExecuteStatusAndCancel_AcceptGeneratedIdsAndSyntacticHashesWithoutGrantingAuthority()
    {
        foreach (var id in new[] { "api-" + Guid.NewGuid().ToString("N"), "api-00000000000000000000000000000001", "api-ffffffffffffffffffffffffffffffff" })
        foreach (var digest in new[] { Digest, new string('0', 64), new string('f', 64) })
        {
            new LocalApiV3ExecuteRequest(Version, id, digest).Validate();
            new LocalApiV3OperationRequest(Version, id, digest).Validate();
        }
    }

    [Theory]
    [InlineData("installOptions", "{}")]
    [InlineData("outputPath", "\"bundle\"")]
    [InlineData("outputIntent", "{}")]
    [InlineData("options", "{}")]
    [InlineData("filter", "\"all\"")]
    [InlineData("uid", "123")]
    [InlineData("pid", "123")]
    [InlineData("Command", "\"apk.diagnose\"")]
    public void ExistingStrictParser_RejectsUnownedPreflightFields(string field, string jsonValue)
    {
        var json = PreflightJson()[..^1] + ",\"" + field + "\":" + jsonValue + "}";
        AssertCode("request_invalid", () => Parse<LocalApiV3PreflightRequest>(json));
    }

    [Theory]
    [InlineData("serial", "\"QUEST-02\"")]
    [InlineData("command", "\"apk.diagnose\"")]
    [InlineData("apkPath", "\"other.apk\"")]
    [InlineData("outputId", "\"diag-other\"")]
    public void ExistingStrictParser_RejectsExecuteTimeCommandReconstruction(string field, string jsonValue)
    {
        var json = OperationJson()[..^1] + ",\"" + field + "\":" + jsonValue + "}";
        AssertCode("request_invalid", () => Parse<LocalApiV3ExecuteRequest>(json));
        AssertCode("request_invalid", () => Parse<LocalApiV3OperationRequest>(json));
    }

    [Fact]
    public void ExistingStrictParser_PreservesDuplicateAndBodyBounds()
    {
        AssertCode("duplicate_property", () => Parse<LocalApiV3PreflightRequest>(
            PreflightJson()[..^1] + ",\"serial\":\"QUEST-02\"}"));
        AssertCode("duplicate_property", () => Parse<LocalApiV3OperationRequest>(
            OperationJson()[..^1] + ",\"commandDigest\":\"other\"}"));
        AssertCode("request_size_invalid", () => Parse<LocalApiV3PreflightRequest>(
            new string(' ', LocalApiContract.MaximumRequestBytes + 1)));
        AssertCode("request_invalid", () => Parse<LocalApiV3ExecuteRequest>("null"));
    }

    [Fact]
    public void ExistingStrictParser_DeserializesExactWireContract()
    {
        var preflight = Parse<LocalApiV3PreflightRequest>(PreflightJson());
        preflight.Validate();
        Assert.Equal(Preflight(), preflight);
        var execute = Parse<LocalApiV3ExecuteRequest>(OperationJson());
        execute.Validate();
        var operation = Parse<LocalApiV3OperationRequest>(OperationJson());
        operation.Validate();
        Assert.Equal(execute.CommandDigest, operation.CommandDigest);
    }

    private static LocalApiV3PreflightRequest Preflight() => new(Version, "apk.diagnose", "base.apk", "QUEST-01");
    private static string PreflightJson() => JsonSerializer.Serialize(Preflight(), JsonSerializerOptions.Web);
    private static string OperationJson() => JsonSerializer.Serialize(new LocalApiV3ExecuteRequest(Version, OperationId, Digest), JsonSerializerOptions.Web);
    private static void AssertCode(string code, Action action) => Assert.Equal(code, Assert.Throws<LocalApiException>(action).Code);

    // Exercise the owner's existing parser without adding another production admission path.
    private static T Parse<T>(string json) where T : class
    {
        var method = typeof(LocalApiCommandRegistry).GetMethod("ParseBound", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(T));
        try
        {
            return (T)method.Invoke(null, [new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json))])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
