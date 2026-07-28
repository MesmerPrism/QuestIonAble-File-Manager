using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Setup;

internal sealed record FleetReplayProtectionSetupResult(
    string Action,
    string? StateRootSha256);

internal sealed record ProtectedHelperCommitment(
    FleetWindowsFileIdentity Identity,
    string Sha256,
    string SignerCertificateSha256);

internal enum ProtectedHelperInstallAction
{
    Installed,
    Preserved,
    UpgradedSameSigner
}

internal enum ProtectedHelperArtifactMatch
{
    Missing,
    Prior,
    Replacement,
    Unknown
}

internal interface ISetupArtifactVerifier
{
    string VerifySignerCertificateSha256(string path);
}

internal sealed class AuthenticodeSetupArtifactVerifier :
    ISetupArtifactVerifier
{
    public static AuthenticodeSetupArtifactVerifier Instance { get; } =
        new();

    public string VerifySignerCertificateSha256(string path)
    {
        SetupAuthenticode.Verify(path);
#pragma warning disable SYSLIB0057
        using var signer = new X509Certificate2(
            X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return Convert.ToHexString(
                SHA256.HashData(signer.RawData))
            .ToLowerInvariant();
    }
}

internal static class FleetInstallerReleaseProof
{
    private const string MetadataPrefix =
        "QuestIonAbleFileManager.FleetInstaller.";

    public static int Write()
    {
        var values = ReadValues();
        using var canonical = new MemoryStream();
        foreach (var entry in values)
        {
            WriteUtf8(canonical, entry.Key);
            canonical.WriteByte(0);
            WriteUtf8(canonical, entry.Value);
            canonical.WriteByte(0);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema =
                "questionable.file_manager.fleet_installer_release_proof.v1",
            field_count = values.Count,
            configuration_sha256 = Convert.ToHexString(
                    SHA256.HashData(canonical.ToArray()))
                .ToLowerInvariant()
        }));
        return 0;
    }

    public static IReadOnlyDictionary<string, string> ReadValues()
    {
        var values = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var attribute in typeof(FleetInstallerSettings).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(static attribute => attribute.Key.StartsWith(
                MetadataPrefix,
                StringComparison.Ordinal)))
        {
            var name = attribute.Key[MetadataPrefix.Length..];
            if (!values.TryAdd(name, attribute.Value ?? string.Empty))
            {
                throw new InvalidOperationException(
                    "The embedded Fleet release configuration contains duplicate fields.");
            }
        }
        return values;
    }

    private static void WriteUtf8(Stream output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        output.Write(bytes);
    }
}

// Machine-record creation, monotonic transition, and lifecycle repair live
// only in signed, elevated Setup. Runtime Core can read protected state and
// request the helper's bounded transition; it never writes HKLM directly.
internal static class FleetInstallerReplayProtectionSetup
{
    private const string KeyPrefix =
        @"SOFTWARE\MesmerPrism\QuestIonAbleFileManager\FleetInstallerReplay\";
    private const string RecordValue = "Record";
    private const string StateFileName = "fleet-installer.state.json";
    private const string AnchorSuffix =
        ".fleet-installer.initialized.v1";
    private const string HelperDirectoryName =
        "QuestIonAbleFileManager";
    private const string HelperFileName =
        "QuestIonAbleFileManager-ReplayAuthority.exe";
    private const int MaximumCredentialBytes = 4096;
    private const int MaximumStateBytes = 1024 * 1024;

    public static FleetReplayProtectionSetupResult
        ProvisionOrRepairEmbeddedRelease(
            bool allowRepair,
            bool destructiveReset)
    {
        var settings = FleetInstallerSettings.FromEmbeddedRelease();
        if (settings is null)
        {
            return new FleetReplayProtectionSetupResult(
                "not_configured",
                null);
        }

        try
        {
            var metadata = FleetInstallerReleaseProof.ReadValues();
            if (!metadata.TryGetValue(
                    "ProvisioningSetupSignerCertificateSha256",
                    out var setupSignerPin))
            {
                throw new InvalidOperationException(
                    "The reviewed QFM Setup signer pin is missing.");
            }
            VerifyOwnAuthenticode(setupSignerPin);
            var root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(settings.PrivateStageRoot));
            var digest = StateRootDigest(root);
            var statePath = Path.Combine(root, StateFileName);
            var anchorPath = root + AnchorSuffix;
            using var machineLock =
                FleetReplayMachineLock.Acquire(digest);
            var helperAction =
                InstallProtectedHelper(setupSignerPin);
            var stateExists = File.Exists(statePath);
            var anchorExists = File.Exists(anchorPath);
            var machineRecord = ReadMachineRecord(digest);
            if (destructiveReset)
            {
                var empty = FleetInstallerProtectedState.Empty(digest);
                WriteReplayFiles(root, digest, empty, lastOutcome: null);
                WriteMachineRecord(empty);
                return new FleetReplayProtectionSetupResult(
                    "destructive_reset",
                    digest);
            }
            ValidateReplayEvidenceShape(stateExists, anchorExists);

            if (stateExists)
            {
                var localState = ValidateExistingReplayFiles(
                    statePath,
                    anchorPath,
                    digest);
                if (machineRecord is not null)
                {
                    if (!LocalReplayMatchesMachine(
                            localState,
                            machineRecord))
                    {
                        RequireExplicitRepair(allowRepair);
                        RepairLocalReplayFilesFromMachine(
                            root,
                            digest,
                            machineRecord,
                            localState);
                        return new FleetReplayProtectionSetupResult(
                            "repaired_local_state_from_machine_authority",
                            digest);
                    }
                    return new FleetReplayProtectionSetupResult(
                        "preserved_initialized",
                        digest);
                }

                _ = RequireProtectedAuthorityForRepair(machineRecord);
            }

            if (machineRecord is not null)
            {
                RequireExplicitRepair(allowRepair);
                RepairLocalReplayFilesFromMachine(
                    root,
                    digest,
                    machineRecord,
                    localState: null);
                return new FleetReplayProtectionSetupResult(
                    "repaired_local_files_from_machine_authority",
                    digest);
            }

            if (helperAction != ProtectedHelperInstallAction.Installed)
            {
                throw new InvalidOperationException(
                    "Fleet replay authority is missing from an existing installation. Use --destructive-reset-fleet-replay-protection only to explicitly discard replay history.");
            }
            WriteInitialReplayFiles(root, digest);
            WriteMachineRecord(FleetInstallerProtectedState.Empty(digest));
            return new FleetReplayProtectionSetupResult(
                "provisioned_machine_initialized",
                digest);
        }
        finally
        {
            (settings.Source as IDisposable)?.Dispose();
        }
    }

    public static int WriteSecuritySelfTest()
    {
        ValidateMachineAcl(CreateMachineAcl());
        FleetReplayMachineLock.ValidateSecurity(
            FleetReplayMachineLock.CreateSecurity());
        SetupStagingDirectory.ValidateProtectedSecurity(
            SetupStagingDirectory.CreateProtectedSecurity());
        using (var staging = SetupStagingDirectory.Create(
                   "SecuritySelfTest",
                   protectedMachineStaging: false))
        {
            if (!Path.GetFileName(staging.Path).StartsWith(
                    "SecuritySelfTest-",
                    StringComparison.Ordinal) ||
                Path.GetFileName(staging.Path).Length !=
                "SecuritySelfTest-".Length + 32)
            {
                throw new InvalidOperationException(
                    "The non-elevated Setup staging directory is not unpredictable.");
            }
        }
        RunConcurrencySelfTest();
        RunReleaseArtifactLifecycleSelfTest();
        try
        {
            RequireAdministrator(administrator: false);
            throw new InvalidOperationException(
                "The unelevated writer check did not fail closed.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "elevated signed Setup",
                StringComparison.Ordinal))
        {
        }
        try
        {
            RequireExplicitRepair(allowRepair: false);
            throw new InvalidOperationException(
                "The explicit repair check did not fail closed.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "--repair-fleet-replay-protection",
                StringComparison.Ordinal))
        {
        }
        RequireExplicitRepair(allowRepair: true);
        try
        {
            _ = InstallerOptions.Parse(
            [
                "--repair-fleet-replay-protection",
                "--destructive-reset-fleet-replay-protection"
            ]);
            throw new InvalidOperationException(
                "Conflicting replay lifecycle options were accepted.");
        }
        catch (ArgumentException exception) when (
            exception.Message.Contains(
                "mutually exclusive",
                StringComparison.Ordinal))
        {
        }
        if (!InstallerOptions.Parse(
            [
                "--destructive-reset-fleet-replay-protection"
            ]).DestructiveResetFleetReplayProtection)
        {
            throw new InvalidOperationException(
                "The explicit destructive reset option was not recognized.");
        }
        if (FixedLowerHexEquals(
                new string('a', 64),
                new string('b', 64)))
        {
            throw new InvalidOperationException(
                "The signer mismatch check did not fail closed.");
        }
        var resultProperties = typeof(InstallerResult)
            .GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (resultProperties.Any(static name =>
                name.EndsWith("Path", StringComparison.Ordinal)) ||
            !resultProperties.Contains("AppInstallerSourceKind") ||
            !resultProperties.Contains("AppInstallerSha256"))
        {
            throw new InvalidOperationException(
                "Setup result redaction is not exact.");
        }
        var failureProperties = typeof(InstallerFailureResult)
            .GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (failureProperties.Any(static name =>
                name.EndsWith("Path", StringComparison.Ordinal) ||
                name.Contains("Message", StringComparison.Ordinal) ||
                name.Contains("Type", StringComparison.Ordinal)) ||
            !failureProperties.SetEquals(
            [
                "Status",
                "ErrorCode",
                "HResult",
                "InnerHResult"
            ]))
        {
            throw new InvalidOperationException(
                "Setup failure result redaction is not exact.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema =
                "questionable.file_manager.fleet_replay_security_self_test.v1",
            status = "ok",
            acl = "system_admin_write_users_read",
            transaction_lock_acl = "system_admin_only",
            abandoned_lock = "recoverable",
            provisioning_acceptance = "serialized",
            staging = "unpredictable_and_protected_when_elevated",
            unelevated_writer = "rejected",
            signer_mismatch = "rejected",
            explicit_repair = "required",
            release_artifact_upgrade =
                "synthetic_same_signer_state_preserving",
            missing_machine_repair = "fail_closed",
            destructive_reset = "explicit_only",
            forged_partial_evidence = "rejected",
            rollback_readback =
                "verified_or_validated_backup_retained",
            partial_replace_failure =
                "reconciled_and_prior_backup_retained",
            result_local_paths = "absent"
        }));
        return 0;
    }

    public static int RunLockTestChild(
        string token,
        string descriptorId,
        string version,
        string holdMillisecondsText,
        string mode,
        string ready)
    {
        if (token.Length != 32 ||
            token.Any(static character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')) ||
            !FleetInstallerValidation.IsIdentifier(descriptorId, 128) ||
            !FleetInstallerValidation.IsThreePartVersion(version) ||
            !int.TryParse(
                holdMillisecondsText,
                out var holdMilliseconds) ||
            holdMilliseconds is < 0 or > 2000 ||
            mode is not ("accept" or "crash" or "provision") ||
            ready is not ("ready" or "none"))
        {
            return 20;
        }
        using var machineLock = FleetReplayMachineLock.AcquireTest(token);
        if (ready == "ready")
        {
            File.WriteAllText(
                LockTestReadyPath(token),
                "ready",
                Encoding.ASCII);
        }
        if (holdMilliseconds > 0)
        {
            Thread.Sleep(holdMilliseconds);
        }
        if (mode == "crash")
        {
            Environment.Exit(99);
        }
        var path = LockTestStatePath(token);
        if (mode == "provision")
        {
            if (!File.Exists(path))
            {
                WriteLockTestState(
                    path,
                    new LockTestState(null, []));
            }
            return 0;
        }
        var state = JsonSerializer.Deserialize<LockTestState>(
                File.ReadAllText(path, Encoding.UTF8)) ??
            throw new InvalidOperationException(
                "The replay-lock test state is invalid.");
        if (state.AcceptedDescriptorIds.Contains(
                descriptorId,
                StringComparer.Ordinal))
        {
            return 10;
        }
        if (state.HighestHandoffVersion is not null &&
            Version.Parse(version) <=
            Version.Parse(state.HighestHandoffVersion))
        {
            return 11;
        }
        var next = state with
        {
            HighestHandoffVersion = version,
            AcceptedDescriptorIds =
                [.. state.AcceptedDescriptorIds, descriptorId]
        };
        WriteLockTestState(path, next);
        return 0;
    }

    private static void WriteLockTestState(
        string path,
        LockTestState state)
    {
        var temporary = path + "." +
            Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(state),
                Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void RunConcurrencySelfTest()
    {
        var tokens = new List<string>();
        try
        {
            var replayToken = NewLockTestState(tokens);
            using var replayA = StartLockTestChild(
                replayToken,
                "release-1",
                "1.0.0",
                400,
                "accept",
                "none");
            using var replayB = StartLockTestChild(
                replayToken,
                "release-1",
                "1.0.0",
                0,
                "accept",
                "none");
            var replayCodes = new[]
            {
                WaitForLockTestChild(replayA),
                WaitForLockTestChild(replayB)
            };
            Array.Sort(replayCodes);
            if (!replayCodes.SequenceEqual([0, 10]))
            {
                throw new InvalidOperationException(
                    "The two-process replay barrier did not admit exactly one transition.");
            }

            var orderingToken = NewLockTestState(tokens);
            using var high = StartLockTestChild(
                orderingToken,
                "release-2",
                "2.0.0",
                400,
                "accept",
                "ready");
            WaitForReady(orderingToken);
            using var low = StartLockTestChild(
                orderingToken,
                "release-1",
                "1.0.0",
                0,
                "accept",
                "none");
            if (WaitForLockTestChild(high) != 0 ||
                WaitForLockTestChild(low) != 11)
            {
                throw new InvalidOperationException(
                    "The two-process version-order barrier allowed a rollback.");
            }

            var abandonedToken = NewLockTestState(tokens);
            using var crashing = StartLockTestChild(
                abandonedToken,
                "release-crash",
                "1.0.0",
                0,
                "crash",
                "ready");
            WaitForReady(abandonedToken);
            if (WaitForLockTestChild(crashing) != 99)
            {
                throw new InvalidOperationException(
                    "The abandoned-lock fixture did not terminate while owning the lock.");
            }
            using var recovery = StartLockTestChild(
                abandonedToken,
                "release-recovery",
                "1.0.0",
                0,
                "accept",
                "none");
            if (WaitForLockTestChild(recovery) != 0)
            {
                throw new InvalidOperationException(
                    "The replay authority did not recover an abandoned transaction lock.");
            }

            var provisionToken = NewLockTestToken(
                tokens,
                createState: false);
            using var firstProvision = StartLockTestChild(
                provisionToken,
                "unused-provision-a",
                "1.0.0",
                400,
                "provision",
                "ready");
            WaitForReady(provisionToken);
            using var secondProvision = StartLockTestChild(
                provisionToken,
                "unused-provision-b",
                "1.0.0",
                0,
                "provision",
                "none");
            using var acceptance = StartLockTestChild(
                provisionToken,
                "release-after-provision",
                "1.0.0",
                0,
                "accept",
                "none");
            if (WaitForLockTestChild(firstProvision) != 0 ||
                WaitForLockTestChild(secondProvision) != 0 ||
                WaitForLockTestChild(acceptance) != 0)
            {
                throw new InvalidOperationException(
                    "Provisioning and acceptance were not serialized.");
            }
            var provisioned = JsonSerializer.Deserialize<LockTestState>(
                    File.ReadAllText(
                        LockTestStatePath(provisionToken),
                        Encoding.UTF8)) ??
                throw new InvalidOperationException(
                    "The provisioning race result is invalid.");
            if (provisioned.HighestHandoffVersion != "1.0.0" ||
                !provisioned.AcceptedDescriptorIds.SequenceEqual(
                    ["release-after-provision"],
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "A stale provisioning writer reset protected acceptance state.");
            }
        }
        finally
        {
            foreach (var token in tokens)
            {
                File.Delete(LockTestStatePath(token));
                File.Delete(LockTestReadyPath(token));
            }
        }
    }

    private static void RunReleaseArtifactLifecycleSelfTest()
    {
        using var staging = SetupStagingDirectory.Create(
            "ReplayArtifactSelfTest",
            protectedMachineStaging: false);
        var sourceA = Path.Combine(staging.Path, "setup-a.exe");
        var sourceB = Path.Combine(staging.Path, "setup-b.exe");
        var sourceOtherSigner = Path.Combine(
            staging.Path,
            "setup-other-signer.exe");
        var destination = Path.Combine(
            staging.Path,
            "replay-authority.exe");
        var bytesA = Encoding.UTF8.GetBytes(
            "synthetic signed setup release A");
        var bytesB = Encoding.UTF8.GetBytes(
            "synthetic signed setup release B");
        var bytesOtherSigner = Encoding.UTF8.GetBytes(
            "synthetic signed setup with another signer");
        File.WriteAllBytes(sourceA, bytesA);
        File.WriteAllBytes(sourceB, bytesB);
        File.WriteAllBytes(sourceOtherSigner, bytesOtherSigner);
        var signerPin = new string('a', 64);
        var otherSignerPin = new string('b', 64);
        var signerByHash = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            [BytesSha256(bytesA)] = signerPin,
            [BytesSha256(bytesB)] = signerPin,
            [BytesSha256(bytesOtherSigner)] = otherSignerPin
        };
        var verifier = new SyntheticSetupArtifactVerifier(
            signerByHash,
            attemptMutation: false);
        if (InstallProtectedHelperCore(
                sourceA,
                destination,
                signerPin,
                verifier) !=
            ProtectedHelperInstallAction.Installed)
        {
            throw new InvalidOperationException(
                "The synthetic release-A helper was not installed.");
        }

        var replayRoot = Path.Combine(staging.Path, "replay");
        var replayDigest = StateRootDigest(replayRoot);
        var protectedState =
            FleetInstallerProtectedState.Empty(replayDigest) with
            {
                HighestHandoffVersion = "2.0.0",
                AcceptedDescriptorIds = ["release-2"]
            };
        WriteReplayFiles(
            replayRoot,
            replayDigest,
            protectedState,
            "guided_installer_completed");
        var statePath = Path.Combine(replayRoot, StateFileName);
        var anchorPath = replayRoot + AnchorSuffix;
        var stateBeforeUpgrade = File.ReadAllBytes(statePath);
        var anchorBeforeUpgrade = File.ReadAllBytes(anchorPath);

        var mutationVerifier = new SyntheticSetupArtifactVerifier(
            signerByHash,
            attemptMutation: true);
        try
        {
            _ = InstallProtectedHelperCore(
                sourceB,
                destination,
                signerPin,
                mutationVerifier);
            throw new InvalidOperationException(
                "A staged helper mutation was accepted.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "changed during signature validation",
                StringComparison.Ordinal))
        {
        }
        if (mutationVerifier.MutationAttempts < 3 ||
            mutationVerifier.PermittedMutationAttempts != 1 ||
            mutationVerifier.RejectedMutationAttempts !=
                mutationVerifier.MutationAttempts - 1 ||
            !File.ReadAllBytes(destination).SequenceEqual(bytesA))
        {
            throw new InvalidOperationException(
                "The synthetic substitution attempt was not contained before replacement.");
        }
        if (InstallProtectedHelperCore(
                sourceB,
                destination,
                signerPin,
                verifier) !=
            ProtectedHelperInstallAction.UpgradedSameSigner)
        {
            throw new InvalidOperationException(
                "The synthetic same-signer helper upgrade was not committed.");
        }
        if (!File.ReadAllBytes(destination).SequenceEqual(bytesB) ||
            !File.ReadAllBytes(statePath).SequenceEqual(
                stateBeforeUpgrade) ||
            !File.ReadAllBytes(anchorPath).SequenceEqual(
                anchorBeforeUpgrade))
        {
            throw new InvalidOperationException(
                "The same-signer helper upgrade changed replay state or committed the wrong artifact.");
        }
        if (InstallProtectedHelperCore(
                sourceB,
                destination,
                signerPin,
                verifier) !=
            ProtectedHelperInstallAction.Preserved)
        {
            throw new InvalidOperationException(
                "The identical synthetic helper was not preserved.");
        }

        WriteReplayFiles(
            replayRoot,
            replayDigest,
            FleetInstallerProtectedState.Empty(replayDigest),
            "guided_installer_completed");
        var mutableLocal = ValidateExistingReplayFiles(
            statePath,
            anchorPath,
            replayDigest);
        RepairLocalReplayFilesFromMachine(
            replayRoot,
            replayDigest,
            protectedState,
            mutableLocal);
        var repaired = ValidateExistingReplayFiles(
            statePath,
            anchorPath,
            replayDigest);
        if (!LocalReplayMatchesMachine(
                repaired,
                protectedState) ||
            repaired.LastOutcome !=
                "guided_installer_completed")
        {
            throw new InvalidOperationException(
                "Protected nonempty replay evidence was not preserved during repair reconstruction.");
        }
        try
        {
            _ = RequireProtectedAuthorityForRepair(
                machineRecord: null);
            throw new InvalidOperationException(
                "Mutable local replay evidence reconstructed missing machine authority.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "--destructive-reset-fleet-replay-protection",
                StringComparison.Ordinal))
        {
        }
        try
        {
            ValidateReplayEvidenceShape(
                stateExists: true,
                anchorExists: false);
            throw new InvalidOperationException(
                "Partial replay evidence was accepted.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "only one replay-state file",
                StringComparison.Ordinal))
        {
        }
        WriteThrough(
            anchorPath,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = FleetInstallerContract.StateAnchorSchema,
                state_root_sha256 = new string('c', 64)
            }));
        try
        {
            _ = ValidateExistingReplayFiles(
                statePath,
                anchorPath,
                replayDigest);
            throw new InvalidOperationException(
                "A forged replay anchor was accepted.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "durable replay anchor is invalid",
                StringComparison.Ordinal))
        {
        }
        WriteReplayFiles(
            replayRoot,
            replayDigest,
            protectedState,
            "guided_installer_completed");

        AssertReplayTransitionRejected(
            protectedState,
            "release-downgrade",
            "1.9.9");
        AssertReplayTransitionRejected(
            protectedState,
            "release-same-version",
            "2.0.0");
        var advanced = AdvanceProtectedState(
            protectedState,
            "release-3",
            "3.0.0");
        if (advanced.HighestHandoffVersion != "3.0.0" ||
            !advanced.AcceptedDescriptorIds.SequenceEqual(
                ["release-2", "release-3"],
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The protected replay transition did not preserve history.");
        }

        var rejectCommittedVerifier =
            new RejectCommittedSetupArtifactVerifier(
                verifier,
                destination);
        try
        {
            _ = InstallProtectedHelperCore(
                sourceA,
                destination,
                signerPin,
                rejectCommittedVerifier);
            throw new InvalidOperationException(
                "A failed committed helper readback was accepted.");
        }
        catch (AggregateException exception) when (
            exception.Message.Contains(
                "previous helper was restored and verified",
                StringComparison.Ordinal))
        {
        }
        if (!File.ReadAllBytes(destination).SequenceEqual(bytesB))
        {
            throw new InvalidOperationException(
                "Failed helper readback did not restore release B.");
        }

        var adversarialVerifier =
            new RejectCommittedSetupArtifactVerifier(
                verifier,
                destination);
        var rollbackAttackCount = 0;
        try
        {
            _ = InstallProtectedHelperCore(
                sourceA,
                destination,
                signerPin,
                adversarialVerifier,
                rollbackDestination =>
                {
                    var injected = rollbackDestination + "." +
                        Guid.NewGuid().ToString("N") + ".attack";
                    File.WriteAllBytes(injected, bytesOtherSigner);
                    File.Move(
                        injected,
                        rollbackDestination,
                        overwrite: true);
                    rollbackAttackCount++;
                });
            throw new InvalidOperationException(
                "A substituted rollback readback was accepted.");
        }
        catch (AggregateException exception) when (
            exception.Message.Contains(
                "validated backup was retained",
                StringComparison.Ordinal) &&
            exception.InnerExceptions.Count == 2 &&
            exception.InnerExceptions.All(static inner =>
                inner.Message.StartsWith(
                    "Fleet replay helper ",
                    StringComparison.Ordinal) &&
                !inner.Message.Contains(
                    Path.DirectorySeparatorChar)))
        {
        }
        var retainedBackups = Directory.GetFiles(
            staging.Path,
            "replay-authority.exe.*.backup",
            SearchOption.TopDirectoryOnly);
        if (rollbackAttackCount != 1 ||
            retainedBackups.Length != 1 ||
            !File.ReadAllBytes(retainedBackups[0])
                .SequenceEqual(bytesB) ||
            verifier.VerifySignerCertificateSha256(
                retainedBackups[0]) != signerPin)
        {
            throw new InvalidOperationException(
                "Failed rollback readback did not retain exact validated repair evidence.");
        }
        ProtectedHelperCommitment retainedBackupCommitment;
        using (var retainedBackup =
               FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(
                   retainedBackups[0]))
        {
            FleetWindowsFileSafety.ValidateFile(
                retainedBackup.SafeFileHandle,
                retainedBackups[0],
                requireSingleLink: true);
            retainedBackupCommitment = new ProtectedHelperCommitment(
                FleetWindowsFileSafety.GetIdentity(
                    retainedBackup.SafeFileHandle),
                FileSha256(retainedBackup),
                signerPin);
        }
        RestoreProtectedHelperFromBackup(
            retainedBackups[0],
            destination,
            destination + ".self-test-repair",
            signerPin,
            retainedBackupCommitment,
            verifier,
            backupValidatedHook: null,
            rollbackReadbackHook: null);
        if (!File.ReadAllBytes(destination).SequenceEqual(bytesB))
        {
            throw new InvalidOperationException(
                "Validated rollback repair evidence could not restore release B.");
        }
        File.Delete(retainedBackups[0]);
        if (File.Exists(retainedBackups[0]))
        {
            throw new InvalidOperationException(
                "The rollback repair self-test backup was not cleaned.");
        }

        try
        {
            _ = InstallProtectedHelperCore(
                sourceOtherSigner,
                destination,
                signerPin,
                verifier);
            throw new InvalidOperationException(
                "A helper signed by another synthetic signer was accepted.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "reviewed release signer pin",
                StringComparison.Ordinal))
        {
        }
        if (!File.ReadAllBytes(destination).SequenceEqual(bytesB))
        {
            throw new InvalidOperationException(
                "A rejected signer change modified the installed helper.");
        }

        try
        {
            _ = InstallProtectedHelperCore(
                sourceA,
                destination,
                signerPin,
                verifier,
                rollbackReadbackTestHook: null,
                primaryReplaceTestHook:
                (replacement, installed, backup) =>
                {
                    _ = replacement;
                    File.Move(
                        installed,
                        backup,
                        overwrite: false);
                    throw new System.ComponentModel.Win32Exception(
                        1177,
                        "Synthetic partial ReplaceFileW failure at " +
                        installed);
                });
            throw new InvalidOperationException(
                "A partial ReplaceFileW failure was accepted.");
        }
        catch (AggregateException exception) when (
            exception.Message.Contains(
                "validated prior backup was retained",
                StringComparison.Ordinal) &&
            exception.InnerExceptions.Count == 2 &&
            exception.InnerExceptions[0].Message.StartsWith(
                "Fleet replay helper replacement failed with HRESULT",
                StringComparison.Ordinal) &&
            exception.InnerExceptions[1].Message.Contains(
                "destination=missing, temporary=replacement, backup=prior",
                StringComparison.Ordinal) &&
            !exception.Message.Contains(
                staging.Path,
                StringComparison.OrdinalIgnoreCase))
        {
        }
        var partialFailureBackups = Directory.GetFiles(
            staging.Path,
            "replay-authority.exe.*.backup",
            SearchOption.TopDirectoryOnly);
        if (File.Exists(destination) ||
            partialFailureBackups.Length != 1 ||
            !File.ReadAllBytes(partialFailureBackups[0])
                .SequenceEqual(bytesB) ||
            verifier.VerifySignerCertificateSha256(
                partialFailureBackups[0]) != signerPin)
        {
            throw new InvalidOperationException(
                "Partial ReplaceFileW failure lost exact prior-helper recovery evidence.");
        }
    }

    private sealed class RejectCommittedSetupArtifactVerifier(
        ISetupArtifactVerifier inner,
        string destination) : ISetupArtifactVerifier
    {
        private int _destinationVerificationCount;

        public string VerifySignerCertificateSha256(string path)
        {
            if (string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase) &&
                ++_destinationVerificationCount == 2)
            {
                throw new InvalidOperationException(
                    "Synthetic committed-helper verification failed.");
            }
            return inner.VerifySignerCertificateSha256(path);
        }
    }

    private static void AssertReplayTransitionRejected(
        FleetInstallerProtectedState state,
        string descriptorId,
        string version)
    {
        try
        {
            _ = AdvanceProtectedState(
                state,
                descriptorId,
                version);
            throw new InvalidOperationException(
                "A non-advancing replay transition was accepted.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "high-water mark",
                StringComparison.Ordinal))
        {
        }
    }

    private static string BytesSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();

    private sealed class SyntheticSetupArtifactVerifier(
        IReadOnlyDictionary<string, string> signerByHash,
        bool attemptMutation) : ISetupArtifactVerifier
    {
        public int MutationAttempts { get; private set; }

        public int RejectedMutationAttempts { get; private set; }

        public int PermittedMutationAttempts { get; private set; }

        public string VerifySignerCertificateSha256(string path)
        {
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            var digest = BytesSha256(bytes);
            if (attemptMutation)
            {
                MutationAttempts++;
                var mutationRejected = false;
                try
                {
                    File.WriteAllBytes(
                        path,
                        Encoding.UTF8.GetBytes(
                            "synthetic substitution"));
                }
                catch (IOException)
                {
                    RejectedMutationAttempts++;
                    mutationRejected = true;
                }
                if (!mutationRejected)
                {
                    PermittedMutationAttempts++;
                }
            }
            return signerByHash.TryGetValue(
                    digest,
                    out var signer)
                ? signer
                : throw new InvalidOperationException(
                    "The synthetic helper signature is unknown.");
        }
    }

    private static string NewLockTestState(ICollection<string> tokens)
    {
        return NewLockTestToken(tokens, createState: true);
    }

    private static string NewLockTestToken(
        ICollection<string> tokens,
        bool createState)
    {
        var token = Guid.NewGuid().ToString("N");
        tokens.Add(token);
        if (createState)
        {
            WriteLockTestState(
                LockTestStatePath(token),
                new LockTestState(null, []));
        }
        return token;
    }

    private static Process StartLockTestChild(
        string token,
        string descriptorId,
        string version,
        int holdMilliseconds,
        string mode,
        string ready)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ??
                throw new InvalidOperationException(
                    "The replay-lock self-test executable is unavailable."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "--fleet-replay-lock-test-child",
                     token,
                     descriptorId,
                     version,
                     holdMilliseconds.ToString(
                         System.Globalization.CultureInfo.InvariantCulture),
                     mode,
                     ready
                 })
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start) ??
            throw new InvalidOperationException(
                "The replay-lock self-test child did not start.");
    }

    private static int WaitForLockTestChild(Process process)
    {
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The replay-lock self-test child timed out.");
        }
        var standardError = process.StandardError.ReadToEnd();
        var standardOutput = process.StandardOutput.ReadToEnd();
        if (!string.IsNullOrEmpty(standardError) ||
            !string.IsNullOrEmpty(standardOutput))
        {
            throw new InvalidOperationException(
                "The replay-lock self-test child emitted unexpected output.");
        }
        return process.ExitCode;
    }

    private static void WaitForReady(string token)
    {
        var path = LockTestReadyPath(token);
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException(
                    "The replay-lock self-test child did not acquire the lock.");
            }
            Thread.Sleep(10);
        }
    }

    private static string LockTestStatePath(string token) =>
        Path.Combine(
            Path.GetTempPath(),
            "qfm-fleet-replay-lock-test-" + token + ".json");

    private static string LockTestReadyPath(string token) =>
        Path.Combine(
            Path.GetTempPath(),
            "qfm-fleet-replay-lock-test-" + token + ".ready");

    private sealed record LockTestState(
        string? HighestHandoffVersion,
        IReadOnlyList<string> AcceptedDescriptorIds);

    public static int AcceptEmbeddedRelease(
        string stateRootSha256,
        string descriptorId,
        string version,
        string payloadSha256)
    {
        var settings = FleetInstallerSettings.FromEmbeddedRelease() ??
            throw new InvalidOperationException(
                "The Fleet replay authority is not configured.");
        try
        {
            var metadata = FleetInstallerReleaseProof.ReadValues();
            var signerPin = metadata[
                "ProvisioningSetupSignerCertificateSha256"];
            VerifyOwnAuthenticode(signerPin);
            var root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(settings.PrivateStageRoot));
            var expectedRoot = StateRootDigest(root);
            if (!FixedLowerHexEquals(expectedRoot, stateRootSha256))
            {
                throw new InvalidOperationException(
                    "The Fleet replay authority request targets another state root.");
            }
            using var machineLock =
                FleetReplayMachineLock.Acquire(stateRootSha256);
            var bytes = settings.Source.ReadDescriptorAsync(
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            var descriptor = FleetInstallerValidation.VerifyDescriptor(
                bytes,
                settings.TrustPolicy,
                DateTimeOffset.UtcNow);
            if (!string.Equals(
                    descriptor.DescriptorId,
                    descriptorId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    descriptor.Version,
                    version,
                    StringComparison.Ordinal) ||
                !FixedLowerHexEquals(
                    descriptor.PayloadSha256,
                    payloadSha256))
            {
                throw new InvalidOperationException(
                    "The Fleet replay authority request does not match the current signed release descriptor.");
            }
            var current = ReadMachineRecord(stateRootSha256) ??
                throw new InvalidOperationException(
                    "The protected Fleet replay machine record is missing.");
            var expected = AdvanceProtectedState(
                current,
                descriptorId,
                version);
            WriteMachineRecord(expected);
            var committed = ReadMachineRecord(stateRootSha256) ??
                throw new InvalidOperationException(
                    "The protected Fleet replay transition was not durable.");
            if (committed != expected &&
                (committed.Schema != expected.Schema ||
                 committed.StateRootSha256 != expected.StateRootSha256 ||
                 committed.Status != expected.Status ||
                 committed.HighestHandoffVersion !=
                    expected.HighestHandoffVersion ||
                 !committed.AcceptedDescriptorIds.SequenceEqual(
                     expected.AcceptedDescriptorIds,
                     StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(
                    "The protected Fleet replay transition readback differs from the committed state.");
            }
            return 0;
        }
        finally
        {
            (settings.Source as IDisposable)?.Dispose();
        }
    }

    private static void RequireExplicitRepair(bool allowRepair)
    {
        if (!allowRepair)
        {
            throw new InvalidOperationException(
                "Fleet replay lifecycle repair requires the signed Setup option --repair-fleet-replay-protection.");
        }
    }

    private static void ValidateReplayEvidenceShape(
        bool stateExists,
        bool anchorExists)
    {
        if (stateExists != anchorExists)
        {
            throw new InvalidOperationException(
                "Fleet replay repair stopped because only one replay-state file remains.");
        }
    }

    private static FleetInstallerProtectedState
        RequireProtectedAuthorityForRepair(
            FleetInstallerProtectedState? machineRecord) =>
        machineRecord ??
        throw new InvalidOperationException(
            "Fleet replay repair cannot reconstruct missing protected machine authority from mutable local files. Use --destructive-reset-fleet-replay-protection only to explicitly discard replay history.");

    private static FleetInstallerProtectedState AdvanceProtectedState(
        FleetInstallerProtectedState current,
        string descriptorId,
        string version)
    {
        if (!FleetInstallerValidation.IsIdentifier(
                descriptorId,
                128) ||
            !FleetInstallerValidation.IsThreePartVersion(version))
        {
            throw new InvalidOperationException(
                "The signed Fleet release transition is invalid.");
        }
        if (current.AcceptedDescriptorIds.Contains(
                descriptorId,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The signed Fleet release descriptor was already accepted.");
        }
        if (current.HighestHandoffVersion is not null &&
            Version.Parse(version) <=
            Version.Parse(current.HighestHandoffVersion))
        {
            throw new InvalidOperationException(
                "The signed Fleet release does not advance the protected replay high-water mark.");
        }
        return current with
        {
            HighestHandoffVersion = version,
            AcceptedDescriptorIds = current.AcceptedDescriptorIds
                .Append(descriptorId)
                .TakeLast(256)
                .ToArray()
        };
    }

    private static FleetInstallerState ValidateExistingReplayFiles(
        string statePath,
        string anchorPath,
        string digest)
    {
        using var state = ReadJsonDocument(statePath, MaximumStateBytes);
        var stateRoot = state.RootElement;
        RequireObjectWithUniqueProperties(stateRoot);
        var allowedStateProperties = new HashSet<string>(
            [
                "schema",
                "highest_handoff_version",
                "accepted_descriptor_ids",
                "last_outcome"
            ],
            StringComparer.Ordinal);
        var acceptedIdValues = stateRoot.TryGetProperty(
                "accepted_descriptor_ids",
                out var acceptedIds) &&
            acceptedIds.ValueKind == JsonValueKind.Array
            ? acceptedIds.EnumerateArray()
                .Where(static item =>
                    item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .ToArray()
            : [];
        if (stateRoot.EnumerateObject().Any(property =>
                !allowedStateProperties.Contains(property.Name)) ||
            !TryReadExactString(
                stateRoot,
                "schema",
                FleetInstallerContract.StateSchema) ||
            !stateRoot.TryGetProperty(
                "accepted_descriptor_ids",
                out acceptedIds) ||
            acceptedIds.ValueKind != JsonValueKind.Array ||
            acceptedIds.EnumerateArray().Any(static item =>
                item.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(item.GetString()) ||
                item.GetString()!.Length > 128) ||
            acceptedIdValues.Distinct(StringComparer.Ordinal).Count() !=
                acceptedIdValues.Length ||
            !IsOptionalString(
                stateRoot,
                "highest_handoff_version",
                static value =>
                    Version.TryParse(value, out var version) &&
                    version.Major >= 0 &&
                    version.Minor >= 0 &&
                    version.Build >= 0 &&
                    string.Equals(
                        version.ToString(3),
                        value,
                        StringComparison.Ordinal)) ||
            !IsOptionalString(
                stateRoot,
                "last_outcome",
                static value => value.Length is >= 1 and <= 128))
        {
            throw new InvalidOperationException(
                "Fleet replay repair stopped because the existing replay state is invalid.");
        }

        using var anchor = ReadJsonDocument(anchorPath, MaximumCredentialBytes);
        var anchorRoot = anchor.RootElement;
        RequireObjectWithUniqueProperties(anchorRoot);
        if (anchorRoot.EnumerateObject().Count() != 2 ||
            !TryReadExactString(
                anchorRoot,
                "schema",
                FleetInstallerContract.StateAnchorSchema) ||
            !TryReadExactString(
                anchorRoot,
                "state_root_sha256",
                digest))
        {
            throw new InvalidOperationException(
                "Fleet replay repair stopped because the durable replay anchor is invalid.");
        }
        return new FleetInstallerState(
            FleetInstallerContract.StateSchema,
            ReadOptionalString(
                stateRoot,
                "highest_handoff_version"),
            acceptedIdValues,
            ReadOptionalString(stateRoot, "last_outcome"));
    }

    private static JsonDocument ReadJsonDocument(
        string path,
        int maximumBytes)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (input.Length is < 2 || input.Length > maximumBytes)
        {
            throw new InvalidOperationException(
                "Fleet replay repair stopped because a replay-state file has an invalid size.");
        }

        try
        {
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1)
            {
                throw new InvalidOperationException(
                    "Fleet replay repair stopped because a replay-state file changed while it was read.");
            }
            return JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Fleet replay repair stopped because a replay-state file is invalid.",
                exception);
        }
    }

    private static void RequireObjectWithUniqueProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Fleet replay repair stopped because replay evidence is not an object.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidOperationException(
                    "Fleet replay repair stopped because replay evidence contains duplicate fields.");
            }
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                RequireObjectWithUniqueProperties(property.Value);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        RequireObjectWithUniqueProperties(item);
                    }
                }
            }
        }
    }

    private static bool TryReadExactString(
        JsonElement element,
        string name,
        string expected) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool IsOptionalString(
        JsonElement element,
        string name,
        Func<string, bool> predicate)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return true;
        }
        return value.ValueKind == JsonValueKind.Null ||
               value.ValueKind == JsonValueKind.String &&
               predicate(value.GetString()!);
    }

    private static string? ReadOptionalString(
        JsonElement element,
        string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool LocalReplayMatchesMachine(
        FleetInstallerState localState,
        FleetInstallerProtectedState machineRecord) =>
        localState.HighestHandoffVersion ==
            machineRecord.HighestHandoffVersion &&
        localState.AcceptedDescriptorIds.SequenceEqual(
            machineRecord.AcceptedDescriptorIds,
            StringComparer.Ordinal);

    private static FleetInstallerProtectedState? ReadMachineRecord(
        string digest)
    {
        EnsureDigest(digest);
        using var machine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = machine.OpenSubKey(KeyPrefix + digest, writable: false);
        if (key is null)
        {
            return null;
        }
        ValidateMachineAcl(key);
        if (key.GetValue(
                RecordValue,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames)
            is not string json)
        {
            throw MachineRecordInvalid();
        }
        var bytes = Encoding.UTF8.GetBytes(json);
        FleetInstallerValidation.RejectDuplicateProperties(bytes);
        FleetInstallerProtectedState record;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var names = document.RootElement
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !names.SetEquals(
                [
                    "schema",
                    "state_root_sha256",
                    "status",
                    "highest_handoff_version",
                    "accepted_descriptor_ids"
                ]))
            {
                throw MachineRecordInvalid();
            }
            record = JsonSerializer.Deserialize<FleetInstallerProtectedState>(
                    bytes,
                    FleetInstallerValidation.Json) ??
                throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The protected Fleet replay machine record is invalid.",
                exception);
        }
        if (record.Schema !=
                WindowsFleetInstallerInitializationStore.Schema ||
            !FixedLowerHexEquals(record.StateRootSha256, digest) ||
            record.Status != "initialized" ||
            record.AcceptedDescriptorIds.Count > 256 ||
            record.AcceptedDescriptorIds
                .Distinct(StringComparer.Ordinal).Count() !=
                record.AcceptedDescriptorIds.Count ||
            record.AcceptedDescriptorIds.Any(
                static value =>
                    !FleetInstallerValidation.IsIdentifier(value, 128)) ||
            record.HighestHandoffVersion is not null &&
            !FleetInstallerValidation.IsThreePartVersion(
                record.HighestHandoffVersion))
        {
            throw MachineRecordInvalid();
        }
        return record;
    }

    private static void WriteMachineRecord(
        FleetInstallerProtectedState record)
    {
        EnsureDigest(record.StateRootSha256);
        RequireAdministrator(IsAdministrator());
        var security = CreateMachineAcl();
        using var machine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = machine.CreateSubKey(
            KeyPrefix + record.StateRootSha256,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryOptions.None,
            security) ??
            throw new InvalidOperationException(
                "The protected Fleet replay machine record could not be created.");
        key.SetAccessControl(security);
        key.SetValue(
            RecordValue,
            JsonSerializer.Serialize(
                record,
                new JsonSerializerOptions(
                    FleetInstallerValidation.Json)
                {
                    DefaultIgnoreCondition =
                        System.Text.Json.Serialization
                            .JsonIgnoreCondition.Never
                }),
            RegistryValueKind.String);
        key.Flush();
        ValidateMachineAcl(key);
    }

    private static ProtectedHelperInstallAction InstallProtectedHelper(
        string signerPin)
    {
        RequireAdministrator(IsAdministrator());
        var source = Environment.ProcessPath ??
            throw new InvalidOperationException(
                "Setup could not resolve its signed executable path.");
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "MesmerPrism",
            HelperDirectoryName);
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The Fleet replay authority directory must not be a reparse point.");
        }
        var destination = Path.Combine(directory, HelperFileName);
        return InstallProtectedHelperCore(
            source,
            destination,
            signerPin,
            AuthenticodeSetupArtifactVerifier.Instance);
    }

    internal static ProtectedHelperInstallAction
        InstallProtectedHelperCore(
            string source,
            string destination,
            string signerPin,
            ISetupArtifactVerifier verifier,
            Action<string>? rollbackReadbackTestHook = null,
            Action<string, string, string>? primaryReplaceTestHook = null)
    {
        EnsureDigest(signerPin);
        ArgumentNullException.ThrowIfNull(verifier);
        var sourcePath = Path.GetFullPath(source);
        var destinationPath = Path.GetFullPath(destination);
        using var retainedSource =
            FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(
                sourcePath);
        FleetWindowsFileSafety.ValidateFile(
            retainedSource.SafeFileHandle,
            sourcePath,
            requireSingleLink: true);
        var sourceIdentity = FleetWindowsFileSafety.GetIdentity(
            retainedSource.SafeFileHandle);
        var sourceHash = FileSha256(retainedSource);
        RequireSignerPin(
            verifier.VerifySignerCertificateSha256(sourcePath),
            signerPin);
        FleetWindowsFileSafety.ValidateFile(
            retainedSource.SafeFileHandle,
            sourcePath,
            requireSingleLink: true);
        if (FleetWindowsFileSafety.GetIdentity(
                retainedSource.SafeFileHandle) != sourceIdentity)
        {
            throw new InvalidOperationException(
                "The signed Setup source identity changed during helper validation.");
        }
        if (string.Equals(
                sourcePath,
                destinationPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return ProtectedHelperInstallAction.Preserved;
        }

        var destinationExisted = File.Exists(destinationPath);
        ProtectedHelperCommitment? priorCommitment = null;
        if (destinationExisted)
        {
            using var retainedDestination =
                FleetWindowsFileSafety.OpenRetainedStagedReadOnlyFile(
                    destinationPath);
            FleetWindowsFileSafety.ValidateFile(
                retainedDestination.SafeFileHandle,
                destinationPath,
                requireSingleLink: true);
            var destinationIdentity = FleetWindowsFileSafety.GetIdentity(
                retainedDestination.SafeFileHandle);
            var destinationHash = FileSha256(retainedDestination);
            RequireSignerPin(
                verifier.VerifySignerCertificateSha256(
                    destinationPath),
                signerPin);
            FleetWindowsFileSafety.ValidateFile(
                retainedDestination.SafeFileHandle,
                destinationPath,
                requireSingleLink: true);
            if (FleetWindowsFileSafety.GetIdentity(
                    retainedDestination.SafeFileHandle) !=
                destinationIdentity)
            {
                throw new InvalidOperationException(
                    "The installed replay helper identity changed during validation.");
            }
            priorCommitment = new ProtectedHelperCommitment(
                destinationIdentity,
                destinationHash,
                signerPin);
            if (FixedLowerHexEquals(sourceHash, destinationHash))
            {
                return ProtectedHelperInstallAction.Preserved;
            }
        }

        var temporary = destinationPath + "." +
            Guid.NewGuid().ToString("N") + ".tmp";
        var backup = destinationPath + "." +
            Guid.NewGuid().ToString("N") + ".backup";
        var rollbackCandidate = destinationPath + "." +
            Guid.NewGuid().ToString("N") + ".rollback";
        FleetWindowsFileIdentity temporaryIdentity;
        ProtectedHelperCommitment? replacementCommitment = null;
        var preserveBackup = false;
        var backupWasValidated = false;
        var replacementAttempted = false;
        var replacementCommitted = false;
        try
        {
            using (var retainedTemporary =
                   FleetWindowsFileSafety
                       .CreateNewRetainedReadableFile(temporary))
            {
                retainedSource.Position = 0;
                retainedSource.CopyTo(retainedTemporary);
                retainedTemporary.Flush(flushToDisk: true);
                FleetWindowsFileSafety.ValidateFile(
                    retainedTemporary.SafeFileHandle,
                    temporary,
                    requireSingleLink: true);
                temporaryIdentity = FleetWindowsFileSafety.GetIdentity(
                    retainedTemporary.SafeFileHandle);
                if (!FixedLowerHexEquals(
                        sourceHash,
                        FileSha256(retainedTemporary)))
                {
                    throw new InvalidOperationException(
                        "The staged replay helper differs from the signed Setup source.");
                }
            }
            using (var retainedValidatedTemporary =
                   FleetWindowsFileSafety
                       .OpenAuthenticodeCompatibleReadOnlyFile(
                           temporary))
            {
                FleetWindowsFileSafety.ValidateFile(
                    retainedValidatedTemporary.SafeFileHandle,
                    temporary,
                    requireSingleLink: true);
                if (FleetWindowsFileSafety.GetIdentity(
                        retainedValidatedTemporary.SafeFileHandle) !=
                    temporaryIdentity ||
                    !FixedLowerHexEquals(
                        sourceHash,
                        FileSha256(retainedValidatedTemporary)))
                {
                    throw new InvalidOperationException(
                        "The staged replay helper changed before signature validation.");
                }
                RequireSignerPin(
                    verifier.VerifySignerCertificateSha256(temporary),
                    signerPin);
                FleetWindowsFileSafety.ValidateFile(
                    retainedValidatedTemporary.SafeFileHandle,
                    temporary,
                    requireSingleLink: true);
                if (FleetWindowsFileSafety.GetIdentity(
                        retainedValidatedTemporary.SafeFileHandle) !=
                    temporaryIdentity ||
                    !FixedLowerHexEquals(
                        sourceHash,
                        FileSha256(retainedValidatedTemporary)))
                {
                    throw new InvalidOperationException(
                        "The staged replay helper changed during signature validation.");
                }
                replacementCommitment = new ProtectedHelperCommitment(
                    temporaryIdentity,
                    sourceHash,
                    signerPin);
            }
            try
            {
                if (destinationExisted)
                {
                    replacementAttempted = true;
                    preserveBackup = true;
                    if (primaryReplaceTestHook is null)
                    {
                        File.Replace(
                            temporary,
                            destinationPath,
                            backup,
                            ignoreMetadataErrors: false);
                    }
                    else
                    {
                        primaryReplaceTestHook(
                            temporary,
                            destinationPath,
                            backup);
                    }
                }
                else
                {
                    File.Move(
                        temporary,
                        destinationPath,
                        overwrite: false);
                }
                replacementCommitted = true;
                using var committed =
                    FleetWindowsFileSafety
                        .OpenRetainedStagedReadOnlyFile(
                            destinationPath);
                FleetWindowsFileSafety.ValidateFile(
                    committed.SafeFileHandle,
                    destinationPath,
                    requireSingleLink: true);
                if (FleetWindowsFileSafety.GetIdentity(
                        committed.SafeFileHandle) !=
                    temporaryIdentity ||
                    !FixedLowerHexEquals(
                        sourceHash,
                        FileSha256(committed)))
                {
                    throw new InvalidOperationException(
                        "The committed replay helper differs from the retained validated artifact.");
                }
                RequireSignerPin(
                    verifier.VerifySignerCertificateSha256(
                        destinationPath),
                    signerPin);
                FleetWindowsFileSafety.ValidateFile(
                    committed.SafeFileHandle,
                    destinationPath,
                    requireSingleLink: true);
                if (FleetWindowsFileSafety.GetIdentity(
                        committed.SafeFileHandle) !=
                    temporaryIdentity)
                {
                    throw new InvalidOperationException(
                        "The committed replay helper identity changed during validation.");
                }
                preserveBackup = false;
            }
            catch (Exception validationException)
            {
                if (!replacementCommitted)
                {
                    if (destinationExisted &&
                        replacementAttempted)
                    {
                        throw ReconcileFailedProtectedHelperReplace(
                            temporary,
                            destinationPath,
                            backup,
                            signerPin,
                            priorCommitment ??
                                throw new InvalidOperationException(
                                    "The prior replay helper commitment is missing."),
                            replacementCommitment ??
                                throw new InvalidOperationException(
                                    "The replacement replay helper commitment is missing."),
                            verifier,
                            validationException,
                            ref preserveBackup);
                    }
                    throw new AggregateException(
                        "The new replay helper replacement failed before a commit could be verified.",
                        BoundedLifecycleFailure(
                            "replacement",
                            validationException));
                }
                try
                {
                    if (destinationExisted)
                    {
                        RestoreProtectedHelperFromBackup(
                            backup,
                            destinationPath,
                            rollbackCandidate,
                            signerPin,
                            priorCommitment ??
                                throw new InvalidOperationException(
                                    "The prior replay helper commitment is missing."),
                            verifier,
                            () => backupWasValidated = true,
                            rollbackReadbackTestHook);
                        preserveBackup = false;
                    }
                    else
                    {
                        File.Delete(destinationPath);
                        if (File.Exists(destinationPath))
                        {
                            throw new InvalidOperationException(
                                "The rejected new replay helper remains installed.");
                        }
                    }
                }
                catch (Exception rollbackException)
                {
                    preserveBackup = destinationExisted &&
                        File.Exists(backup);
                    throw new AggregateException(
                        preserveBackup
                            ? backupWasValidated
                                ? "The committed replay helper failed validation and rollback could not be verified; the validated backup was retained."
                                : "The committed replay helper failed validation and rollback could not be verified; the backup was retained for repair inspection."
                            : "The committed replay helper failed validation and rollback could not be verified.",
                        BoundedLifecycleFailure(
                            "committed_validation",
                            validationException),
                        BoundedLifecycleFailure(
                            "rollback_readback",
                            rollbackException));
                }
                throw new AggregateException(
                    destinationExisted
                        ? "The committed replay helper failed validation and the previous helper was restored and verified."
                        : "The committed replay helper failed validation and the new helper was removed and verified.",
                    BoundedLifecycleFailure(
                        "committed_validation",
                        validationException));
            }
        }
        finally
        {
            BestEffortDeleteLifecycleArtifact(temporary);
            BestEffortDeleteLifecycleArtifact(rollbackCandidate);
            if (!preserveBackup)
            {
                BestEffortDeleteLifecycleArtifact(backup);
            }
        }
        return destinationExisted
            ? ProtectedHelperInstallAction.UpgradedSameSigner
            : ProtectedHelperInstallAction.Installed;
    }

    private static AggregateException
        ReconcileFailedProtectedHelperReplace(
            string temporary,
            string destination,
            string backup,
            string signerPin,
            ProtectedHelperCommitment priorCommitment,
            ProtectedHelperCommitment replacementCommitment,
            ISetupArtifactVerifier verifier,
            Exception replacementException,
            ref bool preserveBackup)
    {
        var destinationMatch = ClassifyProtectedHelperArtifact(
            destination,
            signerPin,
            priorCommitment,
            replacementCommitment,
            verifier);
        var temporaryMatch = ClassifyProtectedHelperArtifact(
            temporary,
            signerPin,
            priorCommitment,
            replacementCommitment,
            verifier);
        var backupMatch = ClassifyProtectedHelperArtifact(
            backup,
            signerPin,
            priorCommitment,
            replacementCommitment,
            verifier);
        preserveBackup =
            backupMatch != ProtectedHelperArtifactMatch.Missing;

        string message;
        if (backupMatch == ProtectedHelperArtifactMatch.Prior)
        {
            message =
                "The replay helper replacement failed after an attempted atomic replace; the validated prior backup was retained.";
        }
        else if (destinationMatch ==
                 ProtectedHelperArtifactMatch.Prior)
        {
            message = preserveBackup
                ? "The replay helper replacement failed; the prior helper remains verified and the backup was retained for repair inspection."
                : "The replay helper replacement failed; the prior helper remains verified.";
        }
        else
        {
            message = preserveBackup
                ? "The replay helper replacement failed and filesystem state could not be verified; the backup was retained for repair inspection."
                : "The replay helper replacement failed and filesystem state could not be verified.";
        }

        return new AggregateException(
            message,
            BoundedLifecycleFailure(
                "replacement",
                replacementException),
            new InvalidOperationException(
                "Fleet replay helper reconciliation: destination=" +
                ArtifactMatchToken(destinationMatch) +
                ", temporary=" +
                ArtifactMatchToken(temporaryMatch) +
                ", backup=" +
                ArtifactMatchToken(backupMatch) +
                "."));
    }

    private static ProtectedHelperArtifactMatch
        ClassifyProtectedHelperArtifact(
            string path,
            string signerPin,
            ProtectedHelperCommitment priorCommitment,
            ProtectedHelperCommitment replacementCommitment,
            ISetupArtifactVerifier verifier)
    {
        if (!File.Exists(path))
        {
            return ProtectedHelperArtifactMatch.Missing;
        }
        try
        {
            using var retained =
                FleetWindowsFileSafety
                    .OpenAuthenticodeCompatibleReadOnlyFile(path);
            FleetWindowsFileSafety.ValidateFile(
                retained.SafeFileHandle,
                path,
                requireSingleLink: true);
            var identity = FleetWindowsFileSafety.GetIdentity(
                retained.SafeFileHandle);
            var hash = FileSha256(retained);
            RequireSignerPin(
                verifier.VerifySignerCertificateSha256(path),
                signerPin);
            FleetWindowsFileSafety.ValidateFile(
                retained.SafeFileHandle,
                path,
                requireSingleLink: true);
            if (FleetWindowsFileSafety.GetIdentity(
                    retained.SafeFileHandle) != identity ||
                !FixedLowerHexEquals(
                    FileSha256(retained),
                    hash))
            {
                return ProtectedHelperArtifactMatch.Unknown;
            }
            if (identity == priorCommitment.Identity &&
                FixedLowerHexEquals(
                    hash,
                    priorCommitment.Sha256) &&
                FixedLowerHexEquals(
                    signerPin,
                    priorCommitment.SignerCertificateSha256))
            {
                return ProtectedHelperArtifactMatch.Prior;
            }
            if (identity == replacementCommitment.Identity &&
                FixedLowerHexEquals(
                    hash,
                    replacementCommitment.Sha256) &&
                FixedLowerHexEquals(
                    signerPin,
                    replacementCommitment
                        .SignerCertificateSha256))
            {
                return ProtectedHelperArtifactMatch.Replacement;
            }
            return ProtectedHelperArtifactMatch.Unknown;
        }
        catch (FileNotFoundException)
        {
            return ProtectedHelperArtifactMatch.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return ProtectedHelperArtifactMatch.Missing;
        }
        catch (Exception)
        {
            return ProtectedHelperArtifactMatch.Unknown;
        }
    }

    private static string ArtifactMatchToken(
        ProtectedHelperArtifactMatch match) =>
        match switch
        {
            ProtectedHelperArtifactMatch.Missing => "missing",
            ProtectedHelperArtifactMatch.Prior => "prior",
            ProtectedHelperArtifactMatch.Replacement => "replacement",
            _ => "unknown"
        };

    private static void RestoreProtectedHelperFromBackup(
        string backup,
        string destination,
        string rollbackCandidate,
        string signerPin,
        ProtectedHelperCommitment priorCommitment,
        ISetupArtifactVerifier verifier,
        Action? backupValidatedHook,
        Action<string>? rollbackReadbackHook)
    {
        RequireSignerPin(
            priorCommitment.SignerCertificateSha256,
            signerPin);
        using (var retainedBackup =
               FleetWindowsFileSafety
                   .OpenRetainedStagedReadOnlyFile(backup))
        {
            FleetWindowsFileSafety.ValidateFile(
                retainedBackup.SafeFileHandle,
                backup,
                requireSingleLink: true);
            if (FleetWindowsFileSafety.GetIdentity(
                    retainedBackup.SafeFileHandle) !=
                priorCommitment.Identity ||
                !FixedLowerHexEquals(
                    FileSha256(retainedBackup),
                    priorCommitment.Sha256))
            {
                throw new InvalidOperationException(
                    "The replay helper backup differs from the prior commitment.");
            }
            RequireSignerPin(
                verifier.VerifySignerCertificateSha256(backup),
                priorCommitment.SignerCertificateSha256);
            FleetWindowsFileSafety.ValidateFile(
                retainedBackup.SafeFileHandle,
                backup,
                requireSingleLink: true);
            if (FleetWindowsFileSafety.GetIdentity(
                    retainedBackup.SafeFileHandle) !=
                priorCommitment.Identity ||
                !FixedLowerHexEquals(
                    FileSha256(retainedBackup),
                    priorCommitment.Sha256))
            {
                throw new InvalidOperationException(
                    "The replay helper backup changed during validation.");
            }
            backupValidatedHook?.Invoke();

            using var candidateWriter =
                FleetWindowsFileSafety.CreateNewRetainedReadableFile(
                    rollbackCandidate);
            retainedBackup.Position = 0;
            retainedBackup.CopyTo(candidateWriter);
            candidateWriter.Flush(flushToDisk: true);
            FleetWindowsFileSafety.ValidateFile(
                candidateWriter.SafeFileHandle,
                rollbackCandidate,
                requireSingleLink: true);
            if (!FixedLowerHexEquals(
                    FileSha256(candidateWriter),
                    priorCommitment.Sha256))
            {
                throw new InvalidOperationException(
                    "The replay helper rollback candidate differs from the validated backup.");
            }
        }

        FleetWindowsFileIdentity candidateIdentity;
        using (var retainedCandidate =
               FleetWindowsFileSafety
                   .OpenAuthenticodeCompatibleReadOnlyFile(
                       rollbackCandidate))
        {
            FleetWindowsFileSafety.ValidateFile(
                retainedCandidate.SafeFileHandle,
                rollbackCandidate,
                requireSingleLink: true);
            candidateIdentity = FleetWindowsFileSafety.GetIdentity(
                retainedCandidate.SafeFileHandle);
            if (!FixedLowerHexEquals(
                    FileSha256(retainedCandidate),
                    priorCommitment.Sha256))
            {
                throw new InvalidOperationException(
                    "The replay helper rollback candidate changed before validation.");
            }
            RequireSignerPin(
                verifier.VerifySignerCertificateSha256(
                    rollbackCandidate),
                priorCommitment.SignerCertificateSha256);
            FleetWindowsFileSafety.ValidateFile(
                retainedCandidate.SafeFileHandle,
                rollbackCandidate,
                requireSingleLink: true);
            if (FleetWindowsFileSafety.GetIdentity(
                    retainedCandidate.SafeFileHandle) !=
                candidateIdentity ||
                !FixedLowerHexEquals(
                    FileSha256(retainedCandidate),
                    priorCommitment.Sha256))
            {
                throw new InvalidOperationException(
                    "The replay helper rollback candidate changed during validation.");
            }
        }

        File.Replace(
            rollbackCandidate,
            destination,
            destinationBackupFileName: null,
            ignoreMetadataErrors: false);
        rollbackReadbackHook?.Invoke(destination);

        using var restored =
            FleetWindowsFileSafety
                .OpenAuthenticodeCompatibleReadOnlyFile(destination);
        FleetWindowsFileSafety.ValidateFile(
            restored.SafeFileHandle,
            destination,
            requireSingleLink: true);
        if (FleetWindowsFileSafety.GetIdentity(
                restored.SafeFileHandle) != candidateIdentity ||
            !FixedLowerHexEquals(
                FileSha256(restored),
                priorCommitment.Sha256))
        {
            throw new InvalidOperationException(
                "The restored replay helper differs from the validated rollback candidate.");
        }
        RequireSignerPin(
            verifier.VerifySignerCertificateSha256(destination),
            priorCommitment.SignerCertificateSha256);
        FleetWindowsFileSafety.ValidateFile(
            restored.SafeFileHandle,
            destination,
            requireSingleLink: true);
        if (FleetWindowsFileSafety.GetIdentity(
                restored.SafeFileHandle) != candidateIdentity ||
            !FixedLowerHexEquals(
                FileSha256(restored),
                priorCommitment.Sha256))
        {
            throw new InvalidOperationException(
                "The restored replay helper changed during committed readback.");
        }
    }

    private static InvalidOperationException BoundedLifecycleFailure(
        string phase,
        Exception exception) =>
        new(
            "Fleet replay helper " + phase +
            " failed with HRESULT 0x" +
            exception.HResult.ToString(
                "x8",
                System.Globalization.CultureInfo.InvariantCulture) +
            ".");

    private static void BestEffortDeleteLifecycleArtifact(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Cleanup failure must not mask the bounded validation/rollback
            // result that determined authority.
        }
    }

    private static RegistrySecurity CreateMachineAcl()
    {
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRule(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            RegistryRights.FullControl);
        AddRule(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            RegistryRights.FullControl);
        AddRule(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            RegistryRights.ReadKey);
        return security;
    }

    private static void AddRule(
        RegistrySecurity security,
        SecurityIdentifier identity,
        RegistryRights rights) =>
        security.AddAccessRule(new RegistryAccessRule(
            identity,
            rights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static void ValidateMachineAcl(RegistryKey key)
    {
        var security = key.GetAccessControl(AccessControlSections.Access);
        ValidateMachineAcl(security);
    }

    private static void ValidateMachineAcl(RegistrySecurity security)
    {
        if (!security.AreAccessRulesProtected)
        {
            throw MachineRecordInvalid();
        }
        var expected = new Dictionary<string, RegistryRights>(
            StringComparer.Ordinal)
        {
            [new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null).Value] = RegistryRights.FullControl,
            [new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null).Value] = RegistryRights.FullControl,
            [new SecurityIdentifier(
                WellKnownSidType.BuiltinUsersSid,
                null).Value] = RegistryRights.ReadKey
        };
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<RegistryAccessRule>()
            .ToArray();
        if (rules.Length != expected.Count ||
            rules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                !expected.TryGetValue(sid.Value, out var rights) ||
                rule.RegistryRights != rights))
        {
            throw MachineRecordInvalid();
        }
    }

    private static void WriteInitialReplayFiles(string root, string digest)
    {
        WriteReplayFiles(
            root,
            digest,
            FleetInstallerProtectedState.Empty(digest),
            lastOutcome: null);
    }

    private static void RepairLocalReplayFilesFromMachine(
        string root,
        string digest,
        FleetInstallerProtectedState protectedState,
        FleetInstallerState? localState)
    {
        WriteReplayFiles(
            root,
            digest,
            protectedState,
            localState?.LastOutcome);
    }

    private static void WriteReplayFiles(
        string root,
        string digest,
        FleetInstallerProtectedState protectedState,
        string? lastOutcome)
    {
        if (!FixedLowerHexEquals(
                protectedState.StateRootSha256,
                digest) ||
            protectedState.Schema !=
                WindowsFleetInstallerInitializationStore.Schema ||
            protectedState.Status != "initialized")
        {
            throw MachineRecordInvalid();
        }
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Fleet replay state root must not be a reparse point.");
        }
        var state = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = FleetInstallerContract.StateSchema,
            highest_handoff_version =
                protectedState.HighestHandoffVersion,
            accepted_descriptor_ids =
                protectedState.AcceptedDescriptorIds,
            last_outcome = lastOutcome
        });
        var anchor = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = FleetInstallerContract.StateAnchorSchema,
            state_root_sha256 = digest
        });
        WriteThrough(
            Path.Combine(root, StateFileName),
            state);
        WriteThrough(root + AnchorSuffix, anchor);
    }

    private static void WriteThrough(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void VerifyOwnAuthenticode(string expectedSignerSha256)
    {
        var processPath = Environment.ProcessPath ??
            throw new InvalidOperationException(
                "Setup could not resolve its signed executable path.");
        VerifyExecutableAuthenticode(processPath, expectedSignerSha256);
    }

    private static void VerifyExecutableAuthenticode(
        string path,
        string expectedSignerSha256)
    {
        EnsureDigest(expectedSignerSha256);
        RequireSignerPin(
            AuthenticodeSetupArtifactVerifier.Instance
                .VerifySignerCertificateSha256(path),
            expectedSignerSha256);
    }

    private static void RequireSignerPin(
        string actual,
        string expected)
    {
        if (!FixedLowerHexEquals(actual, expected))
        {
            throw new InvalidOperationException(
                "Signed Setup does not match its reviewed release signer pin.");
        }
    }

    private static string FileSha256(FileStream input)
    {
        input.Position = 0;
        var digest = Convert.ToHexString(SHA256.HashData(input))
            .ToLowerInvariant();
        input.Position = 0;
        return digest;
    }

    private static bool FixedLowerHexEquals(
        string left,
        string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    private static void RequireAdministrator(bool administrator)
    {
        if (!administrator)
        {
            throw new InvalidOperationException(
                "Fleet replay machine provisioning requires elevated signed Setup.");
        }
    }

    private static string StateRootDigest(string root) =>
        Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(root.ToUpperInvariant())))
            .ToLowerInvariant();

    private static void EnsureDigest(string digest)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Fleet replay protection requires Windows.");
        }
        if (digest.Length != 64 ||
            digest.Any(static character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw MachineRecordInvalid();
        }
    }

    private static InvalidOperationException MachineRecordInvalid() =>
        new("The protected Fleet replay machine record is invalid.");
}

internal sealed class FleetReplayMachineLock : IDisposable
{
    private const string NamePrefix =
        @"Global\MesmerPrism.QFM.FleetReplay.";
    private readonly Mutex _mutex;
    private bool _owned;

    private FleetReplayMachineLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static FleetReplayMachineLock Acquire(string stateRootSha256)
    {
        if (stateRootSha256.Length != 64 ||
            stateRootSha256.Any(static character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "The Fleet replay lock target is invalid.");
        }
        return AcquireCore(
            NamePrefix + stateRootSha256,
            CreateSecurity(),
            validateProductionAcl: true);
    }

    internal static FleetReplayMachineLock AcquireTest(string token)
    {
        var security = CreateSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new MutexAccessRule(
            identity.User ?? throw new InvalidOperationException(
                "The replay-lock test user SID is unavailable."),
            MutexRights.FullControl,
            AccessControlType.Allow));
        return AcquireCore(
            @"Local\MesmerPrism.QFM.FleetReplay.Test." + token,
            security,
            validateProductionAcl: false);
    }

    private static FleetReplayMachineLock AcquireCore(
        string name,
        MutexSecurity security,
        bool validateProductionAcl)
    {
        var mutex = MutexAcl.Create(
            initiallyOwned: false,
            name,
            out _,
            security);
        try
        {
            if (validateProductionAcl)
            {
                ValidateSecurity(mutex.GetAccessControl());
            }
            var result = new FleetReplayMachineLock(mutex);
            try
            {
                try
                {
                    result._owned = mutex.WaitOne(
                        TimeSpan.FromMinutes(2));
                }
                catch (AbandonedMutexException)
                {
                    result._owned = true;
                }
                if (!result._owned)
                {
                    throw new TimeoutException(
                        "The protected Fleet replay transaction lock timed out.");
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    internal static MutexSecurity CreateSecurity()
    {
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    internal static void ValidateSecurity(MutexSecurity security)
    {
        if (!security.AreAccessRulesProtected)
        {
            throw new InvalidOperationException(
                "The protected Fleet replay transaction lock ACL is invalid.");
        }
        var expected = new HashSet<string>(
            [
                new SecurityIdentifier(
                    WellKnownSidType.LocalSystemSid,
                    null).Value,
                new SecurityIdentifier(
                    WellKnownSidType.BuiltinAdministratorsSid,
                    null).Value
            ],
            StringComparer.Ordinal);
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<MutexAccessRule>()
            .ToArray();
        if (rules.Length != expected.Count ||
            rules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow ||
                rule.MutexRights != MutexRights.FullControl ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                !expected.Contains(sid.Value)))
        {
            throw new InvalidOperationException(
                "The protected Fleet replay transaction lock ACL is invalid.");
        }
    }

    public void Dispose()
    {
        if (_owned)
        {
            _owned = false;
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}

internal static class SetupAuthenticode
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static void Verify(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(fileInfo));
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, fDeleteOld: false);
            var data = new WinTrustData(filePointer);
            var result = WinVerifyTrust(
                IntPtr.Zero,
                GenericVerifyV2,
                ref data);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"Windows rejected Setup Authenticode (0x{result:x8}).");
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeCoTaskMem(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string path)
        {
            StructSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>());
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public string? UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = checked((uint)Marshal.SizeOf<WinTrustData>());
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = null;
            ProviderFlags = 0x00001000;
            UiContext = 0;
        }
    }

    [DllImport(
        "wintrust.dll",
        ExactSpelling = true,
        PreserveSig = true,
        SetLastError = false)]
    private static extern int WinVerifyTrust(
        IntPtr window,
        [MarshalAs(UnmanagedType.LPStruct)] Guid action,
        ref WinTrustData data);
}
