using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class QuestConnectivityProfileManagementTests
{
    private const string DeviceId = "fleet-device-1";
    private const string Target =
        "QuestIonAbleFileManager/QuestConnectivity/fleet-device-1";
    private const string PairingCode = "ABCD-EFGH-JKMN-PQRS";

    [Fact]
    public async Task TypedExecutorCreatesListsStatusesReplacesAndRevokesWithoutProjectingSecrets()
    {
        var store = new MemoryCredentialStore();
        var input = new MemoryInputReader(Enrollment());
        var manager = new QuestConnectivityProfileManager(store, input);
        var executor = new OperatorCommandExecutor(
            client: null,
            new FleetInstallerHandoff(null),
            manager);

        var imported = await executor.ExecuteAsync(
            OperatorCommands.ImportQuestConnectivityProfileStdin(
                operatorConfirmed: true),
            privateInput: new MemoryStream(Enrollment()));
        var status = await executor.ExecuteAsync(
            OperatorCommands.QuestConnectivityProfileStatus(DeviceId));
        var list = await executor.ExecuteAsync(
            OperatorCommands.ListQuestConnectivityProfiles());

        Assert.Equal("created", imported.ConnectivityProfileMutation!.Action);
        Assert.Equal("profileCreated", imported.ConnectivityProfileMutation.ReasonCode);
        Assert.Equal("enrolled", status.ConnectivityProfileStatus!.State);
        Assert.Equal("profileEnrolled", status.ConnectivityProfileStatus.ReasonCode);
        Assert.Collection(
            list.ConnectivityProfileList!.Profiles,
            profile =>
            {
                Assert.Equal(DeviceId, profile.DeviceId);
                Assert.Equal("enrolled", profile.State);
            });
        Assert.Single(store.Writes);
        Assert.Equal(Target, store.Writes[0].Target);
        using (var parsed =
               WindowsCredentialQuestConnectivityProviderProfileStore.ParseProfile(
                   DeviceId,
                   store.Writes[0].Value))
        {
            Assert.Equal(DeviceId, parsed.DeviceId);
        }

        var serialized = JsonSerializer.Serialize(new
        {
            imported.ConnectivityProfileMutation,
            status.ConnectivityProfileStatus,
            list.ConnectivityProfileList
        });
        Assert.DoesNotContain("QUEST-USB-123", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.137.42", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(PairingCode, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Target, serialized, StringComparison.Ordinal);

        var replaced = await executor.ExecuteAsync(
            OperatorCommands.ImportQuestConnectivityProfileStdin(
                replaceExisting: true,
                operatorConfirmed: true),
            privateInput: new MemoryStream(Enrollment()));
        Assert.Equal("replaced", replaced.ConnectivityProfileMutation!.Action);
        Assert.Equal("profileReplaced", replaced.ConnectivityProfileMutation.ReasonCode);

        var revoked = await executor.ExecuteAsync(
            OperatorCommands.RevokeQuestConnectivityProfile(
                DeviceId,
                operatorConfirmed: true));
        var absent = await executor.ExecuteAsync(
            OperatorCommands.QuestConnectivityProfileStatus(DeviceId));
        Assert.Equal("profileRevoked", revoked.ConnectivityProfileMutation!.ReasonCode);
        Assert.Equal("absent", absent.ConnectivityProfileStatus!.State);
        Assert.Empty(store.Values);
    }

    [Fact]
    public async Task WpfInMemoryDocumentUsesTheExactStdinOperatorRoute()
    {
        var document = QuestConnectivityProfileEnrollmentDocument.Create(
            DeviceId,
            "QUEST-USB-123",
            "http://192.168.137.42:39873/",
            PairingCode);
        var store = new MemoryCredentialStore();
        var manager = new QuestConnectivityProfileManager(
            store,
            new MemoryInputReader(document));
        var executor = new OperatorCommandExecutor(
            client: null,
            new FleetInstallerHandoff(null),
            manager);
        var command = OperatorCommands.ImportQuestConnectivityProfileStdin(
            replaceExisting: true,
            operatorConfirmed: true);
        try
        {
            await using var stream = new MemoryStream(document, writable: false);
            var execution = await executor.ExecuteAsync(
                command,
                privateInput: stream);

            Assert.Equal(
                [
                    "connectivity-profile", "import", "--stdin",
                    "--confirm-profile-write", "--replace-existing", "--json"
                ],
                execution.Command.CliArguments);
            Assert.Equal("created", execution.ConnectivityProfileMutation!.Action);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(document);
        }
    }

    [Fact]
    public async Task ExistingProfileRejectsBeforeWriteWithoutReplaceConfirmation()
    {
        var store = new MemoryCredentialStore();
        var manager = new QuestConnectivityProfileManager(
            store,
            new MemoryInputReader(Enrollment()));
        await manager.ImportAsync(
            OperatorCommands.ImportQuestConnectivityProfileStdin(
                operatorConfirmed: true),
            new MemoryStream(Enrollment()),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
            () => manager.ImportAsync(
                OperatorCommands.ImportQuestConnectivityProfileStdin(
                    operatorConfirmed: true),
                new MemoryStream(Enrollment()),
                CancellationToken.None));

        Assert.Equal("profileReplaceConfirmationRequired", exception.Code);
        Assert.Single(store.Writes);
    }

    [Fact]
    public async Task CreateVerificationFailureDeletesJustWrittenCredential()
    {
        var store = new FaultingCredentialStore();
        var input = new TrackingInputReader(Enrollment());
        var manager = new QuestConnectivityProfileManager(store, input);

        var exception =
            await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
                () => manager.ImportAsync(
                    OperatorCommands.ImportQuestConnectivityProfileStdin(
                        operatorConfirmed: true),
                    new MemoryStream(Enrollment()),
                    CancellationToken.None));

        Assert.Equal(
            "profileWriteVerificationFailedRolledBack",
            exception.Code);
        Assert.Equal("failed", exception.Status);
        Assert.Equal("confirmed", exception.RollbackState);
        Assert.False(store.Values.ContainsKey(Target));
        Assert.Equal(1, store.WriteCount);
        Assert.Equal(1, store.DeleteCount);
        Assert.All(
            store.ReturnedBuffers,
            static bytes => Assert.All(bytes, static value => Assert.Equal(0, value)));
        Assert.All(
            input.LastReturned!,
            static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ReplaceVerificationFailureRestoresPriorExactCredential()
    {
        var prior = ProfileCredential();
        var store = new FaultingCredentialStore();
        store.Values[Target] = prior.ToArray();
        var input = new TrackingInputReader(Enrollment());
        var manager = new QuestConnectivityProfileManager(store, input);

        var exception =
            await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
                () => manager.ImportAsync(
                    OperatorCommands.ImportQuestConnectivityProfileStdin(
                        replaceExisting: true,
                        operatorConfirmed: true),
                    new MemoryStream(Enrollment()),
                    CancellationToken.None));

        Assert.Equal(
            "profileWriteVerificationFailedRolledBack",
            exception.Code);
        Assert.Equal("failed", exception.Status);
        Assert.Equal("confirmed", exception.RollbackState);
        Assert.Equal(prior, store.Values[Target]);
        Assert.Equal(2, store.WriteCount);
        Assert.Equal(0, store.DeleteCount);
        Assert.All(
            store.ReturnedBuffers,
            static bytes => Assert.All(bytes, static value => Assert.Equal(0, value)));
        Assert.All(
            input.LastReturned!,
            static value => Assert.Equal(0, value));
        CryptographicOperations.ZeroMemory(prior);
    }

    [Fact]
    public async Task RollbackFailureReportsSanitizedUncertainStateAndZeroesOwnedReads()
    {
        var store = new FaultingCredentialStore
        {
            FailRollback = true
        };
        var input = new TrackingInputReader(Enrollment());
        var manager = new QuestConnectivityProfileManager(store, input);

        var exception =
            await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
                () => manager.ImportAsync(
                    OperatorCommands.ImportQuestConnectivityProfileStdin(
                        operatorConfirmed: true),
                    new MemoryStream(Enrollment()),
                    CancellationToken.None));

        Assert.Equal("profileWriteRollbackFailed", exception.Code);
        Assert.Equal("failed", exception.Status);
        Assert.Equal("uncertain", exception.RollbackState);
        Assert.DoesNotContain(
            "QUEST-USB-123",
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            PairingCode,
            exception.Message,
            StringComparison.Ordinal);
        Assert.All(
            store.ReturnedBuffers,
            static bytes => Assert.All(bytes, static value => Assert.Equal(0, value)));
        Assert.All(
            input.LastReturned!,
            static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task WpfWorkflowCreatesFirstAndRequiresDistinctReplacementConfirmation()
    {
        var store = new MemoryCredentialStore();
        var manager = new QuestConnectivityProfileManager(
            store,
            new MemoryInputReader(Enrollment()));
        var stages = new List<QuestConnectivityProfileWriteStage>();
        var replaceArguments = new List<bool>();

        async Task<QuestConnectivityProfileMutationReceipt> Write(bool replace)
        {
            replaceArguments.Add(replace);
            return await manager.ImportAsync(
                OperatorCommands.ImportQuestConnectivityProfileStdin(
                    replaceExisting: replace,
                    operatorConfirmed: true),
                new MemoryStream(Enrollment()),
                CancellationToken.None);
        }

        var created = await QuestConnectivityProfileWriteWorkflow.ExecuteAsync(
            stage =>
            {
                stages.Add(stage);
                return true;
            },
            Write);
        Assert.Equal("created", created!.Action);
        Assert.Equal([QuestConnectivityProfileWriteStage.Create], stages);
        Assert.Equal([false], replaceArguments);

        stages.Clear();
        replaceArguments.Clear();
        var writesBeforeCancel = store.Writes.Count;
        var cancelled = await QuestConnectivityProfileWriteWorkflow.ExecuteAsync(
            stage =>
            {
                stages.Add(stage);
                return stage == QuestConnectivityProfileWriteStage.Create;
            },
            Write);
        Assert.Null(cancelled);
        Assert.Equal(
            [
                QuestConnectivityProfileWriteStage.Create,
                QuestConnectivityProfileWriteStage.Replace
            ],
            stages);
        Assert.Equal([false], replaceArguments);
        Assert.Equal(writesBeforeCancel, store.Writes.Count);

        stages.Clear();
        replaceArguments.Clear();
        var replaced = await QuestConnectivityProfileWriteWorkflow.ExecuteAsync(
            stage =>
            {
                stages.Add(stage);
                return true;
            },
            Write);
        Assert.Equal("replaced", replaced!.Action);
        Assert.Equal(
            [
                QuestConnectivityProfileWriteStage.Create,
                QuestConnectivityProfileWriteStage.Replace
            ],
            stages);
        Assert.Equal([false, true], replaceArguments);
    }

    [Theory]
    [InlineData(
        """{"schema":"questionable.file_manager.quest_connectivity_profile_enrollment.v1","target":"Wrong/Target","device_id":"fleet-device-1","usb_serial":"QUEST-USB-123","endpoint":"http://192.168.137.42:39873/","pairing_code":"ABCD-EFGH-JKMN-PQRS"}""",
        "profileTargetInvalid")]
    [InlineData(
        """{"schema":"questionable.file_manager.quest_connectivity_profile_enrollment.v1","target":"QuestIonAbleFileManager/QuestConnectivity/fleet-device-1","device_id":"fleet-device-1","usb_serial":"192.0.2.42:5555","endpoint":"http://192.168.137.42:39873/","pairing_code":"ABCD-EFGH-JKMN-PQRS"}""",
        "profileDocumentInvalid")]
    [InlineData(
        """{"schema":"questionable.file_manager.quest_connectivity_profile_enrollment.v1","target":"QuestIonAbleFileManager/QuestConnectivity/fleet-device-1","device_id":"fleet-device-1","usb_serial":"QUEST-USB-123","endpoint":"https://192.168.137.42:39873/","pairing_code":"ABCD-EFGH-JKMN-PQRS"}""",
        "profileDocumentInvalid")]
    [InlineData(
        """{"schema":"questionable.file_manager.quest_connectivity_profile_enrollment.v1","target":"QuestIonAbleFileManager/QuestConnectivity/fleet-device-1","device_id":"fleet-device-1","usb_serial":"QUEST-USB-123","endpoint":"http://198.51.100.42:39873/","pairing_code":"ABCD-EFGH-JKMN-PQRS"}""",
        "profileDocumentInvalid")]
    [InlineData(
        """{"schema":"questionable.file_manager.quest_connectivity_profile_enrollment.v1","target":"QuestIonAbleFileManager/QuestConnectivity/fleet-device-1","device_id":"fleet-device-1","usb_serial":"QUEST-USB-123","endpoint":"http://192.168.137.42:39873/","pairing_code":"too-short"}""",
        "profilePairingCodeInvalid")]
    [InlineData(
        """{"schema":"questionable.file_manager.quest_connectivity_profile_enrollment.v1","target":"QuestIonAbleFileManager/QuestConnectivity/fleet-device-1","device_id":"fleet-device-1","usb_serial":"QUEST-USB-123","endpoint":"http://192.168.137.42:39873/","pairing_code":"ABCD-EFGH-JKMN-PQRS","extra":"rejected"}""",
        "profileDocumentInvalid")]
    [InlineData(
        """{"schema":"questionable.file_manager.quest_connectivity_profile_enrollment.v1","target":"QuestIonAbleFileManager/QuestConnectivity/fleet-device-1","device_id":"fleet-device-1","device_id":"fleet-device-2","usb_serial":"QUEST-USB-123","endpoint":"http://192.168.137.42:39873/","pairing_code":"ABCD-EFGH-JKMN-PQRS"}""",
        "profileDocumentInvalid")]
    public async Task StrictDocumentRejectsInvalidOrDuplicateFields(
        string json,
        string expectedCode)
    {
        var store = new MemoryCredentialStore();
        var manager = new QuestConnectivityProfileManager(
            store,
            new MemoryInputReader(Encoding.UTF8.GetBytes(json)));

        var exception = await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
            () => manager.ImportAsync(
                OperatorCommands.ImportQuestConnectivityProfileStdin(
                    operatorConfirmed: true),
                new MemoryStream(Encoding.UTF8.GetBytes(json)),
                CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task InputModeAmbiguityAndOversizedStdinRejectBeforeCredentialWrite()
    {
        var store = new MemoryCredentialStore();
        var manager = new QuestConnectivityProfileManager(
            store,
            new MemoryInputReader(Enrollment()));
        var fileCommand = OperatorCommands.ImportQuestConnectivityProfileFile(
            Path.GetFullPath("private.json"),
            operatorConfirmed: true);
        var ambiguity = await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
            () => manager.ImportAsync(
                fileCommand,
                new MemoryStream(Enrollment()),
                CancellationToken.None));
        Assert.Equal("profileInputAmbiguous", ambiguity.Code);

        var reader = new WindowsPrivateProfileInputReader();
        var oversized = new byte[
            QuestConnectivityProfileManagementContract.MaximumPrivateInputBytes + 1];
        var size = await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
            () => reader.ReadStreamAsync(
                new MemoryStream(oversized),
                CancellationToken.None));
        Assert.Equal("profileInputSizeInvalid", size.Code);
        Assert.Empty(store.Writes);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PrivateFileReaderAcceptsRestrictedAclAndRejectsBroaderReadAccess()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"qfm-private-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "profile.json");
        await File.WriteAllBytesAsync(path, Enrollment());
        try
        {
            var current = WindowsIdentity.GetCurrent().User!;
            var security = new FileSecurity();
            security.SetOwner(current);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);

            var reader = new WindowsPrivateProfileInputReader();
            var accepted = await reader.ReadFileAsync(path, CancellationToken.None);
            try
            {
                Assert.Equal(Enrollment(), accepted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(accepted);
            }

            var alternateDataStream =
                await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
                    () => reader.ReadFileAsync(
                        path + ":profile",
                        CancellationToken.None));
            Assert.Equal("profilePrivateFilePathInvalid", alternateDataStream.Code);

            var hardLink = Path.Combine(root, "profile-hardlink.json");
            Assert.True(CreateHardLink(hardLink, path, IntPtr.Zero));
            try
            {
                var hardLinkRejected =
                    await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
                        () => reader.ReadFileAsync(
                            hardLink,
                            CancellationToken.None));
                Assert.Equal("profilePrivateFileUnsafe", hardLinkRejected.Code);
            }
            finally
            {
                File.Delete(hardLink);
            }

            var linkRoot = Path.Combine(
                Path.GetDirectoryName(root)!,
                $"qfm-profile-link-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateSymbolicLink(linkRoot, root);
                var reparseRejected =
                    await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
                        () => reader.ReadFileAsync(
                            Path.Combine(linkRoot, "profile.json"),
                            CancellationToken.None));
                Assert.Contains(
                    reparseRejected.Code,
                    new[]
                    {
                        "profilePrivateFileReparseRejected",
                        "profilePrivateFileUnsafe"
                    });
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or
                IOException or
                PlatformNotSupportedException)
            {
                // Some Windows policies disable unprivileged symlink creation.
            }
            finally
            {
                if (Directory.Exists(linkRoot))
                    Directory.Delete(linkRoot);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
            var rejected =
                await Assert.ThrowsAsync<QuestConnectivityProfileManagementException>(
                    () => reader.ReadFileAsync(path, CancellationToken.None));
            Assert.Equal("profilePrivateFileAclInvalid", rejected.Code);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void InvalidStoredProfileIsReportedOnlyAsSanitizedState()
    {
        var store = new MemoryCredentialStore();
        store.Values[Target] = Encoding.UTF8.GetBytes(
            """{"schema":"wrong","private":"do-not-project"}""");
        var manager = new QuestConnectivityProfileManager(
            store,
            new MemoryInputReader(Enrollment()));

        var status = manager.GetStatus(DeviceId);
        var list = manager.List();

        Assert.Equal("invalid", status.State);
        Assert.Equal("profileInvalid", status.ReasonCode);
        Assert.Equal("invalid", Assert.Single(list.Profiles).State);
        var serialized = JsonSerializer.Serialize(new { status, list });
        Assert.DoesNotContain("do-not-project", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceIdsUseOneLowercaseCredentialTargetForm()
    {
        Assert.Throws<ArgumentException>(
            () => OperatorCommands.QuestConnectivityProfileStatus("Fleet-Device-1"));
        Assert.Throws<QuestConnectivityProfileManagementException>(
            () => QuestConnectivityProfileEnrollmentDocument.Create(
                "Fleet-Device-1",
                "QUEST-USB-123",
                "http://192.168.137.42:39873/",
                PairingCode));
    }

    [Fact]
    public void WpfProjectionUsesTransientPasswordInputAndTypedParityHandlers()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.App",
            "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root,
            "src",
            "QuestIonAbleFileManager.App",
            "MainWindow.xaml.cs"));

        Assert.Contains(
            "<PasswordBox x:Name=\"KioskDirectPairingCodeBox\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<TextBox x:Name=\"KioskDirectPairingCodeBox\"",
            xaml,
            StringComparison.Ordinal);
        foreach (var handler in new[]
                 {
                     "OnImportConnectivityProfile",
                     "OnRefreshConnectivityProfiles",
                     "OnCheckConnectivityProfileStatus",
                     "OnSaveEnteredKioskLinkForFleet",
                     "OnRevokeConnectivityProfile"
                 })
        {
            Assert.Contains($"Click=\"{handler}\"", xaml, StringComparison.Ordinal);
            Assert.Contains($" {handler}(", code, StringComparison.Ordinal);
        }
        Assert.Contains(
            "QuestConnectivityProfileWriteWorkflow.ExecuteAsync(",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "KioskDirectEndpointBox.Clear();",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "KioskDirectPairingCodeBox.Clear();",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImportQuestConnectivityProfileStdin(",
            code,
            StringComparison.Ordinal);
    }

    private static byte[] Enrollment() => Encoding.UTF8.GetBytes(
        $$"""
          {
            "schema": "{{QuestConnectivityProfileManagementContract.EnrollmentSchema}}",
            "target": "{{Target}}",
            "device_id": "{{DeviceId}}",
            "usb_serial": "QUEST-USB-123",
            "endpoint": "http://192.168.137.42:39873/",
            "pairing_code": "{{PairingCode}}"
          }
          """);

    private static byte[] ProfileCredential() => Encoding.UTF8.GetBytes(
        $$"""
          {
            "schema": "{{QuestConnectivityProfileManagementContract.ProfileSchema}}",
            "device_id": "{{DeviceId}}",
            "usb_serial": "PRIOR-QUEST-USB",
            "endpoint": "http://192.168.137.41:39873/",
            "pairing_code": "BCDE-FGHJ-KMNP-QRST"
          }
          """);

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "QuestIonAbleFileManager.slnx")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException(
            "Could not locate the QuestIonAble File Manager source root.");
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed class MemoryInputReader(byte[] value) :
        IQuestConnectivityPrivateInputReader
    {
        public Task<byte[]> ReadFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            Task.FromResult(value.ToArray());

        public async Task<byte[]> ReadStreamAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, cancellationToken);
            return output.ToArray();
        }
    }

    private sealed class TrackingInputReader(byte[] value) :
        IQuestConnectivityPrivateInputReader
    {
        public byte[]? LastReturned { get; private set; }

        public Task<byte[]> ReadFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            Task.FromResult(Track());

        public Task<byte[]> ReadStreamAsync(
            Stream stream,
            CancellationToken cancellationToken) =>
            Task.FromResult(Track());

        private byte[] Track()
        {
            LastReturned = value.ToArray();
            return LastReturned;
        }
    }

    private sealed class MemoryCredentialStore : IQuestConnectivityCredentialStore
    {
        public Dictionary<string, byte[]> Values { get; } =
            new(StringComparer.Ordinal);
        public List<(string Target, byte[] Value)> Writes { get; } = [];

        public IReadOnlyList<string> ListTargets() => Values.Keys.ToArray();

        public byte[]? Read(string target) =>
            Values.TryGetValue(target, out var value) ? value.ToArray() : null;

        public void Write(string target, ReadOnlySpan<byte> credential)
        {
            var value = credential.ToArray();
            Values[target] = value;
            Writes.Add((target, value.ToArray()));
        }

        public bool Delete(string target) => Values.Remove(target);
    }

    private sealed class FaultingCredentialStore :
        IQuestConnectivityCredentialStore
    {
        private bool _verificationFaultPending;

        public Dictionary<string, byte[]> Values { get; } =
            new(StringComparer.Ordinal);
        public List<byte[]> ReturnedBuffers { get; } = [];
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }
        public bool FailRollback { get; init; }

        public IReadOnlyList<string> ListTargets() => Values.Keys.ToArray();

        public byte[]? Read(string target)
        {
            byte[]? result;
            if (_verificationFaultPending)
            {
                _verificationFaultPending = false;
                result = Encoding.UTF8.GetBytes(
                    """{"schema":"corrupt-post-write-readback"}""");
            }
            else
            {
                result = Values.TryGetValue(target, out var value)
                    ? value.ToArray()
                    : null;
            }
            if (result is not null)
                ReturnedBuffers.Add(result);
            return result;
        }

        public void Write(string target, ReadOnlySpan<byte> credential)
        {
            WriteCount++;
            if (WriteCount > 1 && FailRollback)
                throw new InvalidOperationException("Injected rollback write failure.");
            Values[target] = credential.ToArray();
            if (WriteCount == 1)
                _verificationFaultPending = true;
        }

        public bool Delete(string target)
        {
            DeleteCount++;
            if (FailRollback)
                throw new InvalidOperationException("Injected rollback delete failure.");
            return Values.Remove(target);
        }
    }
}
