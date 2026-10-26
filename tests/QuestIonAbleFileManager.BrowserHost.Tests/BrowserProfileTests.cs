using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserProfileTests
{
    internal const string DeviceId = "fixture-profile";
    internal const string Secret = "ABCD-EFGH-JKMN-PQRS";
    internal static byte[] Document(string id = DeviceId) => Encoding.UTF8.GetBytes(
        $$"""{"schema":"questionable.file_manager.quest_connectivity_profile_enrollment.v1","target":"QuestIonAbleFileManager/QuestConnectivity/{{id}}","device_id":"{{id}}","usb_serial":"FIXTURE-USB","endpoint":"http://192.168.137.42:39873/","pairing_code":"{{Secret}}"}""");
    internal static byte[] Body(string action, string? id = null, string deviceId = DeviceId) =>
        JsonSerializer.SerializeToUtf8Bytes(action is "profile.list" or "devices" ? new Dictionary<string, object> { ["requestId"] = id ?? Guid.NewGuid().ToString("D"), ["action"] = action } :
            new Dictionary<string, object> { ["requestId"] = id ?? Guid.NewGuid().ToString("D"), ["action"] = action, ["deviceId"] = deviceId, }.Concat(
                action == "profile.revoke" ? new Dictionary<string, object> { ["confirmed"] = true } : []).ToDictionary());
    internal static BrowserProfileRequest Import(string? id = null, bool replace = false, string deviceId = DeviceId) =>
        new(id ?? Guid.NewGuid().ToString("D"), "profile.import", deviceId, replace, true);

    [Fact]
    public async Task FakeOwnerCreateListStatusExplicitReplaceAndRevokeUseOnlyExactTypedRoutes()
    {
        using var fixture = new Fixture();
        Assert.Contains("profile.import", fixture.Commands.AvailableActions);
        var document = Document(); var request = Import();
        var created = await fixture.Commands.ExecuteProfileImportAsync(request, document);
        Assert.Equal("completed", created.State); Assert.Equal("confirmed", created.Profile!.Effect);
        Assert.Equal("created", created.Profile.Receipt!.Action); Assert.Null(created.Result); Assert.Null(created.Mutation);
        Assert.All(document, value => Assert.Equal(0, value));
        var list = await fixture.Commands.ExecuteAsync(Body("profile.list"));
        Assert.Equal(DeviceId, Assert.Single(list.Profile!.List!.Profiles).DeviceId);
        Assert.Equal("enrolled", (await fixture.Commands.ExecuteAsync(Body("profile.status"))).Profile!.Status!.State);
        var blocked = await fixture.Commands.ExecuteProfileImportAsync(Import(), Document());
        Assert.Equal("failed", blocked.State); Assert.Equal("notPerformed", blocked.Profile!.Effect);
        Assert.Equal("profileReplaceConfirmationRequired", blocked.Error);
        var replaced = await fixture.Commands.ExecuteProfileImportAsync(Import(replace: true), Document());
        Assert.Equal("replaced", replaced.Profile!.Receipt!.Action);
        var revoked = await fixture.Commands.ExecuteAsync(Body("profile.revoke"));
        Assert.Equal("profileRevoked", revoked.Profile!.Receipt!.ReasonCode);
        Assert.Equal("profileAlreadyAbsent", (await fixture.Commands.ExecuteAsync(Body("profile.revoke"))).Profile!.Receipt!.ReasonCode);
        Assert.Equal(0, fixture.OtherCalls);
        Assert.Equal(OperatorCommands.ImportQuestConnectivityProfileStdin(operatorConfirmed: true).CliArguments, fixture.Calls[0].CliArguments);
        Assert.All(fixture.Reader.Returned, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        Assert.All(fixture.Store.Reads, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        var json = JsonSerializer.Serialize(fixture.Commands.Get(request.RequestId));
        foreach (var forbidden in new[] { Secret, "192.168.137.42", "FIXTURE-USB", "pairing_code", "QuestIonAbleFileManager/QuestConnectivity/" })
            Assert.DoesNotContain(forbidden, json);
    }

    [Fact]
    public async Task ReviewedIdMismatchAndUnsupportedPlatformNeverActivateOwnerAndClearBorrowedBytes()
    {
        using var fixture = new Fixture();
        var bytes = Document("other-id");
        Assert.Equal("profile_device_id_mismatch", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Commands.ExecuteProfileImportAsync(Import(), bytes))).Code);
        Assert.Empty(fixture.Calls); Assert.All(bytes, value => Assert.Equal(0, value));
        using var unsupported = new Fixture(supported: false);
        Assert.DoesNotContain("profile.list", unsupported.Commands.AvailableActions);
        Assert.Equal("windows_credential_manager_required", (await Assert.ThrowsAsync<BrowserRequestException>(() => unsupported.Commands.ExecuteAsync(Body("profile.list")))).Code);
        Assert.Empty(unsupported.Calls);
    }

    [Theory]
    [InlineData("{\"requestId\":\"00000000-0000-4000-8000-000000000001\",\"action\":\"profile.import\"}", "sensitive_endpoint_required")]
    [InlineData("{\"requestId\":\"00000000-0000-4000-8000-000000000001\",\"action\":\"profile.status\",\"deviceId\":\"UPPER\"}", "profileDeviceIdInvalid")]
    [InlineData("{\"requestId\":\"00000000-0000-4000-8000-000000000001\",\"action\":\"profile.revoke\",\"deviceId\":\"fixture\",\"confirmed\":false}", "confirmation_required")]
    [InlineData("{\"requestId\":\"00000000-0000-4000-8000-000000000001\",\"action\":\"profile.list\",\"endpoint\":\"secret\"}", "fields_invalid")]
    [InlineData("{\"requestId\":\"00000000-0000-4000-8000-000000000001\",\"action\":\"profile.list\",\"action\":\"profile.list\"}", "duplicate_field")]
    public void ParserIsClosedAndPrivateImportHasOnlyItsDedicatedRoute(string body, string code) =>
        Assert.Equal(code, Assert.Throws<BrowserRequestException>(() => BrowserProfileCommands.Parse(Encoding.UTF8.GetBytes(body))).Code);

    [Fact]
    public async Task SharedLedgerPreventsAnyCrossAdapterRedispatchAndNewStatusDoesNotRewriteOriginal()
    {
        using var fixture = new Fixture();
        fixture.Store.FailWriteVerification = true; fixture.Store.FailRollback = true;
        var request = Import(); var original = await fixture.Commands.ExecuteProfileImportAsync(request, Document());
        Assert.Equal("outcomeUnknown", original.State); Assert.Equal("uncertain", original.Profile!.Failure!.RollbackState);
        Assert.Equal("request_already_consumed", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Commands.ExecuteAsync(Body("devices", request.RequestId)))).Code);
        await fixture.Commands.ExecuteAsync(Body("profile.status"));
        Assert.Same(original, fixture.Commands.Get(request.RequestId)); Assert.Equal(1, fixture.Store.Writes);
        Assert.Null((await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Commands.ExecuteProfileImportAsync(request, Document()))).Dispatched);
    }

    [Theory]
    [InlineData(false, "failed", "rolledBack", "confirmed")]
    [InlineData(true, "outcomeUnknown", "unknown", "uncertain")]
    public async Task ExactOwnerRollbackEvidenceControlsUncertainty(bool rollbackFails, string state, string effect, string rollback)
    {
        using var fixture = new Fixture(); fixture.Store.FailWriteVerification = true; fixture.Store.FailRollback = rollbackFails;
        var result = await fixture.Commands.ExecuteProfileImportAsync(Import(), Document());
        Assert.Equal(state, result.State); Assert.Equal(effect, result.Profile!.Effect); Assert.Equal(rollback, result.Profile.Failure!.RollbackState);
    }

    [Fact]
    public async Task RejectedRevokeReadbackAfterDeleteIsStillUnknown()
    {
        using var fixture = new Fixture(); await fixture.Commands.ExecuteProfileImportAsync(Import(), Document());
        fixture.Store.IgnoreDelete = true;
        var result = await fixture.Commands.ExecuteAsync(Body("profile.revoke"));
        Assert.Equal("outcomeUnknown", result.State); Assert.Equal("unknown", result.Profile!.Effect);
        Assert.Equal("profileRevokeReadbackFailed", result.Error); Assert.Equal("rejected", result.Profile.Failure!.Status);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("status")]
    [InlineData("device")]
    [InlineData("replace-without-approval")]
    [InlineData("state")]
    [InlineData("reason")]
    [InlineData("command")]
    [InlineData("missing")]
    public async Task CrossedReceiptNeverAcknowledgesOrProjectsUntrustedPrivateFields(string variant)
    {
        using var fixture = new Fixture(cross: (command, result) =>
        {
            var receipt = result.ConnectivityProfileMutation!;
            return variant switch
            {
                "schema" => result with { ConnectivityProfileMutation = receipt with { Schema = Secret } },
                "status" => result with { ConnectivityProfileMutation = receipt with { Status = "rejected" } },
                "device" => result with { ConnectivityProfileMutation = receipt with { DeviceId = "other-id" } },
                "replace-without-approval" => result with { ConnectivityProfileMutation = receipt with { Action = "replaced", ReasonCode = "profileReplaced" } },
                "state" => result with { ConnectivityProfileMutation = receipt with { State = "absent" } },
                "reason" => result with { ConnectivityProfileMutation = receipt with { ReasonCode = Secret } },
                "command" => result with { Command = OperatorCommands.ImportQuestConnectivityProfileStdin(true, true) },
                _ => result with { ConnectivityProfileMutation = null }
            };
        });
        var request = Import(); var outcome = await fixture.Commands.ExecuteProfileImportAsync(request, Document());
        Assert.Equal("outcomeUnknown", outcome.State); Assert.Equal("unknown", outcome.Profile!.Effect);
        Assert.Null(outcome.Profile.Receipt); Assert.DoesNotContain(Secret, JsonSerializer.Serialize(fixture.Commands.Get(request.RequestId)));
    }

    [Fact]
    public async Task ListIsCappedWithFullCountAndNativeFailureIsNotAnEmptyOrAbsentFact()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 133; index++) fixture.Store.Seed(QuestConnectivityProfileManagementContract.TargetPrefix + $"profile-{index:000}", [1]);
        fixture.Store.Seed("OtherNamespace/private-id", [1]);
        fixture.Store.Seed(QuestConnectivityProfileManagementContract.TargetPrefix + "UPPER", [1]);
        var outcome = await fixture.Commands.ExecuteAsync(Body("profile.list"));
        Assert.Equal(133, outcome.Profile!.TotalProfiles); Assert.True(outcome.Profile.Truncated); Assert.Equal(128, outcome.Profile.List!.Profiles.Count);
        Assert.Equal("profile-000", outcome.Profile.List.Profiles[0].DeviceId); Assert.All(outcome.Profile.List.Profiles, row => Assert.Equal("invalid", row.State));
        Assert.DoesNotContain("OtherNamespace", JsonSerializer.Serialize(outcome));
        fixture.Store.FailRead = true;
        var failed = await fixture.Commands.ExecuteAsync(Body("profile.status"));
        Assert.Equal("failed", failed.State); Assert.Equal("readOnly", failed.Profile!.Effect); Assert.Null(failed.Profile.Status);
        Assert.Equal("profileStoreUnavailable", failed.Error);
    }

    [Fact]
    public async Task SharedExternalGateAndBoundedLedgerRejectWithoutOwnerOrBodyRetention()
    {
        using var fixture = new Fixture();
        using (fixture.Gate.TryAcquire())
            Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Commands.ExecuteAsync(Body("profile.list")))).Code);
        Assert.Empty(fixture.Calls);
        for (var index = 0; index < BrowserCommands.MaximumRequests; index++) await fixture.Commands.ExecuteAsync(Body("profile.list"));
        var document = Document();
        Assert.Equal("session_capacity_reached", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Commands.ExecuteProfileImportAsync(Import(), document))).Code);
        Assert.All(document, value => Assert.Equal(0, value)); Assert.Equal(256, fixture.Calls.Count);
    }

    [Fact]
    public async Task EmptyDrainSealsNewProfileAdmissionBeforeAnyOwnerCall()
    {
        using var fixture = new Fixture(); await fixture.Commands.DrainProfilesAsync();
        var document = Document();
        Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Commands.ExecuteProfileImportAsync(Import(), document))).Code);
        Assert.All(document, value => Assert.Equal(0, value));
        Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Commands.ExecuteAsync(Body("profile.revoke")))).Code);
        Assert.Empty(fixture.Calls); Assert.Equal(0, fixture.Store.Writes);
    }

    [Fact]
    public async Task ConsumedCrossAdapterIdWinsOverUnsupportedPlatformNoDispatchClaim()
    {
        using var fixture = new Fixture(supported: false); var id = Guid.NewGuid().ToString("D");
        await fixture.Commands.ExecuteAsync(Body("devices", id));
        var exception = await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Commands.ExecuteProfileImportAsync(Import(id), Document()));
        Assert.Equal("request_already_consumed", exception.Code); Assert.Null(exception.Dispatched); Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task ShutdownCancellationCannotDetachAdmittedWriteOrReleaseSharedGateAndDrain()
    {
        using var fixture = new Fixture(); using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        fixture.Store.BeforeWrite = () => { started.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var document = Document(); var request = Import();
        var operation = Task.Run(() => fixture.Commands.ExecuteProfileImportAsync(request, document));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var drain = fixture.Commands.DrainProfilesAsync(); Assert.False(drain.IsCompleted); Assert.True(fixture.Gate.IsBusy);
        Assert.Contains(document, value => value != 0);
        try { Assert.Equal("busy", (await Assert.ThrowsAsync<BrowserRequestException>(() => fixture.Commands.ExecuteAsync(Body("devices")))).Code); }
        finally { release.Set(); }
        await drain; Assert.All(document, value => Assert.Equal(0, value)); var result = await operation;
        Assert.Equal("completed", result.State); Assert.False(fixture.Gate.IsBusy); Assert.All(document, value => Assert.Equal(0, value));
    }

    internal sealed class Fixture : IDisposable
    {
        internal Store Store { get; } = new(); internal Reader Reader { get; } = new();
        internal BrowserOperationGate Gate { get; } = new(); internal List<OperatorCommand> Calls { get; } = [];
        internal int OtherCalls; internal BrowserProfiles Profiles { get; } internal BrowserCommands Commands { get; }
        internal Fixture(bool supported = true, Func<OperatorCommand, OperatorExecutionResult, OperatorExecutionResult>? cross = null)
        {
            var manager = new QuestConnectivityProfileManager(Store, Reader);
            var executor = new OperatorCommandExecutor(null, () => throw new InvalidOperationException("Fleet must stay lazy"), () => manager);
            Profiles = new(async (command, input, cancellation) => { Calls.Add(command); var result = await executor.ExecuteAsync(command, cancellation, privateInput: input); return cross?.Invoke(command, result) ?? result; }, Reader, supported);
            Commands = new((_, _) => { OtherCalls++; throw new InvalidOperationException("ADB must not activate"); }, operationGate: Gate, profiles: Profiles);
        }
        public void Dispose() => Store.Dispose();
    }
    internal sealed class Reader : IQuestConnectivityPrivateInputReader
    {
        internal List<byte[]> Returned { get; } = [];
        internal Func<Task>? BeforeRead;
        public Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("No native file input");
        public async Task<byte[]> ReadStreamAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (BeforeRead is not null) await BeforeRead();
            using var buffer = new MemoryStream(); var chunk = new byte[512];
            try { int count; while ((count = await stream.ReadAsync(chunk, cancellationToken)) != 0) { buffer.Write(chunk, 0, count); if (buffer.Length > 4096) throw new QuestConnectivityProfileManagementException("profileInputSizeInvalid", "fixed"); } var result = buffer.ToArray(); Returned.Add(result); return result; }
            finally { CryptographicOperations.ZeroMemory(chunk); CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
        }
    }
    internal sealed class Store : IQuestConnectivityCredentialStore, IDisposable
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        internal List<byte[]> Reads { get; } = []; internal int Writes; internal bool FailWriteVerification, FailRollback, IgnoreDelete, FailRead; internal Action? BeforeWrite;
        internal void Seed(string target, byte[] bytes) => _values.Add(target, bytes);
        public IReadOnlyList<string> ListTargets() => _values.Keys.ToArray();
        public byte[]? Read(string target) { if (FailRead) throw new QuestConnectivityProfileManagementException("profileStoreUnavailable", Secret); if (!_values.TryGetValue(target, out var value)) return null; var result = value.ToArray(); if (FailWriteVerification && Writes > 0) result[0] ^= 1; Reads.Add(result); return result; }
        public void Write(string target, ReadOnlySpan<byte> credential) { BeforeWrite?.Invoke(); Writes++; if (Writes > 1 && FailRollback) throw new IOException("private failure"); if (_values.Remove(target, out var prior)) CryptographicOperations.ZeroMemory(prior); _values[target] = credential.ToArray(); }
        public bool Delete(string target) { if (IgnoreDelete) return true; if (FailRollback) throw new IOException("private failure"); if (!_values.Remove(target, out var value)) return false; CryptographicOperations.ZeroMemory(value); return true; }
        public void Dispose() { foreach (var value in _values.Values) CryptographicOperations.ZeroMemory(value); }
    }
}
