using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.FleetInstallerLifecycle;

internal static class Program
{
    private const string InputSchema =
        "questionable.file_manager.fleet_installer_lifecycle_input.v1";
    private const string ReceiptSchema =
        "questionable.file_manager.fleet_installer_lifecycle_receipt.v1";
    private const string BuildReceiptSchema =
        "rusty.fleet.windows_setup_build_receipt.v3";
    private const string DescriptorReceiptSchema =
        "rusty.fleet.windows_release_descriptor_receipt.v5";
    private const string FleetStateSchema =
        "rusty.fleet.windows_setup_state.v2";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is not ["--input", var inputPath])
            {
                return 2;
            }
            var input = ReadJson<LifecycleInput>(
                inputPath,
                64 * 1024,
                "lifecycle input");
            var receipt = await RunAsync(input).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(receipt, Json));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(
                new LifecycleFailure(
                    ReceiptSchema,
                    "failed",
                    FailureCode(exception)),
                Json));
            return 1;
        }
    }

    private static async Task<LifecycleReceipt> RunAsync(LifecycleInput input)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Fleet installer lifecycle gate requires Windows.");
        }
        ValidateInput(input);

        var installRoot = ValidateInstallRoot(input);
        var releaseA = await ExternalRelease.LoadAsync(
            input.ReleaseARoot,
            input.Channel,
            input.TrustedDescriptorSignerSpkiSha256,
            input.TrustedInstallerSignerCertificateSha256)
            .ConfigureAwait(false);
        var releaseB = await ExternalRelease.LoadAsync(
            input.ReleaseBRoot,
            input.Channel,
            input.TrustedDescriptorSignerSpkiSha256,
            input.TrustedInstallerSignerCertificateSha256)
            .ConfigureAwait(false);
        ValidateReleasePair(releaseA, releaseB);
        RequireNoPathOverlap(
            installRoot,
            releaseA.Root,
            releaseB.Root);

        var qfmStateRoot = Path.Combine(
            Path.GetTempPath(),
            "qfm-fleet-lifecycle-state-" + Guid.NewGuid().ToString("N"));
        var initializationStore = new MemoryInitializationStore();
        var stateRootDigest =
            FleetInstallerWorkspace.StateRootDigest(qfmStateRoot);
        initializationStore.SetupRepair(stateRootDigest);
        InitializeReplayFiles(qfmStateRoot);

        var qfmCleanup = false;
        var fixtureCleanup = false;
        var interruptedCandidateCount = 0;
        try
        {
            var serviceA = CreateService(
                releaseA,
                qfmStateRoot,
                initializationStore,
                new ControlledSetupRunner(
                    GuidedMode.Install,
                    installRoot));
            var statusA = await serviceA.GetStatusAsync().ConfigureAwait(false);
            Require(statusA.Status == "ready", "Release A was not ready.");
            var handoffA = await serviceA.InstallAsync().ConfigureAwait(false);
            RequireSuccessfulHandoff(handoffA, releaseA);
            var fleetStateA = ReadFleetState(installRoot);
            var candidateCountBeforeInterrupt =
                CountInterruptedCandidates(installRoot);
            Require(
                fleetStateA.Current.Version == releaseA.Descriptor.Version &&
                fleetStateA.History.Count == 0,
                "Fleet release A was not installed as the sole current release.");

            using (var cancellation = new CancellationTokenSource(
                       TimeSpan.FromSeconds(45)))
            {
                var interruptedRunner = new ControlledSetupRunner(
                    GuidedMode.InterruptAfterCandidate,
                    installRoot,
                    cancellation);
                var interruptedService = CreateService(
                    releaseB,
                    qfmStateRoot,
                    initializationStore,
                    interruptedRunner);
                await RequireExceptionAsync<OperationCanceledException>(
                    () => interruptedService.InstallAsync(
                        cancellation.Token),
                    expectedCode: null).ConfigureAwait(false);
            }
            RequireNoQfmStages(qfmStateRoot);
            var stateAfterInterrupt = ReadFleetState(installRoot);
            interruptedCandidateCount = CountInterruptedCandidates(
                installRoot);
            Require(
                stateAfterInterrupt.Current.Version ==
                    releaseA.Descriptor.Version &&
                initializationStore.HighestHandoffVersion ==
                    releaseA.Descriptor.Version &&
                interruptedCandidateCount >
                    candidateCountBeforeInterrupt,
                "The interrupted update was activated or left no inert candidate.");
            interruptedCandidateCount -=
                candidateCountBeforeInterrupt;

            var serviceB = CreateService(
                releaseB,
                qfmStateRoot,
                initializationStore,
                new ControlledSetupRunner(
                    GuidedMode.Install,
                    installRoot));
            var statusB = await serviceB.GetStatusAsync().ConfigureAwait(false);
            Require(statusB.Status == "ready", "Release B was not ready.");
            var handoffB = await serviceB.InstallAsync().ConfigureAwait(false);
            RequireSuccessfulHandoff(handoffB, releaseB);
            var fleetStateB = ReadFleetState(installRoot);
            Require(
                fleetStateB.Current.Version == releaseB.Descriptor.Version &&
                fleetStateB.History.Any(item =>
                    item.Version == releaseA.Descriptor.Version),
                "Fleet did not retain release A beside current release B.");
            RequireReleaseReadback(
                installRoot,
                fleetStateB.Current,
                releaseB);
            RequireNoQfmStages(qfmStateRoot);

            var replayStatus =
                await serviceB.GetStatusAsync().ConfigureAwait(false);
            Require(
                replayStatus.Status == "already_handed_off" &&
                replayStatus.HighestHandoffVersion ==
                    releaseB.Descriptor.Version,
                "Release B replay status did not preserve the high-water mark.");
            await RequireFleetCodeAsync(
                () => serviceB.InstallAsync(),
                "fleet_descriptor_replay").ConfigureAwait(false);

            var downgradeService = CreateService(
                releaseA,
                qfmStateRoot,
                initializationStore,
                new ControlledSetupRunner(
                    GuidedMode.Install,
                    installRoot));
            var downgradeStatus =
                await downgradeService.GetStatusAsync().ConfigureAwait(false);
            Require(
                downgradeStatus.Status == "not_newer_than_last_handoff" &&
                downgradeStatus.HighestHandoffVersion ==
                    releaseB.Descriptor.Version,
                "Release A downgrade status did not preserve the high-water mark.");
            await RequireFleetCodeAsync(
                () => downgradeService.InstallAsync(),
                "fleet_release_downgrade_rejected").ConfigureAwait(false);

            var missingAuthority = new MemoryInitializationStore();
            var missingAuthorityService = CreateService(
                releaseA,
                qfmStateRoot,
                missingAuthority,
                new ControlledSetupRunner(
                    GuidedMode.Install,
                    installRoot));
            await RequireFleetCodeAsync(
                () => missingAuthorityService.GetStatusAsync(),
                "fleet_installer_recovery_required").ConfigureAwait(false);

            var staleService = CreateService(
                releaseB,
                qfmStateRoot,
                initializationStore,
                new ControlledSetupRunner(
                    GuidedMode.Install,
                    installRoot),
                new FixedTimeProvider(
                    DateTimeOffset.FromUnixTimeMilliseconds(
                        releaseB.Descriptor.ExpiresAtMs)));
            await RequireFleetCodeAsync(
                () => staleService.GetStatusAsync(),
                "fleet_descriptor_stale").ConfigureAwait(false);

            await RunExternalAdversarialChecksAsync(
                releaseA,
                qfmStateRoot,
                installRoot).ConfigureAwait(false);

            await ControlledSetupRunner.RunDirectAsync(
                releaseB.SetupPath,
                GuidedMode.Rollback,
                installRoot,
                CancellationToken.None).ConfigureAwait(false);
            var rolledBack = ReadFleetState(installRoot);
            Require(
                rolledBack.Current.Version ==
                    releaseA.Descriptor.Version &&
                rolledBack.History.Any(item =>
                    item.Version == releaseB.Descriptor.Version),
                "Fleet rollback did not restore release A with release B retained.");
            RequireReleaseReadback(
                installRoot,
                rolledBack.Current,
                releaseA);
            Require(
                initializationStore.HighestHandoffVersion ==
                    releaseB.Descriptor.Version,
                "Fleet rollback incorrectly lowered QFM replay authority.");

            return new LifecycleReceipt(
                ReceiptSchema,
                "passed",
                input.ArtifactKind,
                "controlled_external_setup_lifecycle",
                "isolated_non_authorizing_test_store",
                new LifecycleReleaseReceipt(
                    releaseA.Descriptor.Version,
                    releaseA.Descriptor.Channel,
                    releaseA.Descriptor.DescriptorId,
                    releaseA.Descriptor.PayloadSha256,
                    releaseA.Descriptor.Asset.Sha256,
                    releaseA.Descriptor.Asset.SizeBytes,
                    releaseA.Descriptor.Asset.SignerCertificateSha256,
                    releaseA.BuildReceipt.SourceRevision,
                    releaseA.BuildReceipt.SourceTree,
                    true),
                new LifecycleReleaseReceipt(
                    releaseB.Descriptor.Version,
                    releaseB.Descriptor.Channel,
                    releaseB.Descriptor.DescriptorId,
                    releaseB.Descriptor.PayloadSha256,
                    releaseB.Descriptor.Asset.Sha256,
                    releaseB.Descriptor.Asset.SizeBytes,
                    releaseB.Descriptor.Asset.SignerCertificateSha256,
                    releaseB.BuildReceipt.SourceRevision,
                    releaseB.BuildReceipt.SourceTree,
                    true),
                StatusAndPlanVerified: true,
                SameSignerUpdateVerified: true,
                SideBySideReleaseRetentionVerified: true,
                ExactRollbackReadbackVerified: true,
                ReplayRejected: true,
                DowngradeRejected: true,
                ReplayHighWaterPreservedAfterRollback: true,
                MissingMachineAuthorityRejected: true,
                WrongSignerRejected: true,
                WrongHashRejected: true,
                WrongSpkiRejected: true,
                CanonicalAssetUrlVerified: true,
                StaleMetadataRejected: true,
                PartialStagingRejectedAndCleaned: true,
                CancellationVerified: true,
                InterruptedCandidateRetainedInert: true,
                InterruptedRecoveryVerified: true,
                InterruptedCandidateCount: interruptedCandidateCount,
                QfmStageCleanupVerified: true,
                FixtureInstallCleanupVerified:
                    input.CleanupFixtureInstallRoot,
                ObservedAtMs:
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        finally
        {
            qfmCleanup = DeleteOwnedQfmState(qfmStateRoot);
            if (input.CleanupFixtureInstallRoot)
            {
                fixtureCleanup = DeleteOwnedFixtureInstallRoot(installRoot);
            }
            if (!qfmCleanup ||
                input.CleanupFixtureInstallRoot && !fixtureCleanup)
            {
                throw new InvalidOperationException(
                    "Lifecycle-owned state cleanup did not complete.");
            }
        }
    }

    private static async Task RunExternalAdversarialChecksAsync(
        ExternalRelease release,
        string sharedQfmStateRoot,
        string installRoot)
    {
        using var otherKey = RSA.Create(2048);
        var otherSpki = otherKey.ExportSubjectPublicKeyInfo();
        var wrongSpkiPolicy = new FleetInstallerTrustPolicy(
            otherSpki,
            Sha256(otherSpki),
            release.Policy.InstallerSignerCertificateSha256,
            release.Policy.Channel);
        var wrongSpkiSettings = new FleetInstallerSettings(
            new LocalFleetReleaseSource(release.DescriptorPath),
            wrongSpkiPolicy,
            sharedQfmStateRoot,
            "external_signed_fixture");
        await RequireFleetCodeAsync(
            () => new FleetInstallerHandoff(
                    wrongSpkiSettings,
                    timeProvider: release.TimeProvider)
                .GetStatusAsync(),
            "fleet_descriptor_signer_mismatch",
            "fleet_descriptor_signature_invalid").ConfigureAwait(false);

        var wrongSignerPolicy = release.Policy with
        {
            InstallerSignerCertificateSha256 = new string('0', 64)
        };
        var wrongSignerSettings = new FleetInstallerSettings(
            new LocalFleetReleaseSource(release.DescriptorPath),
            wrongSignerPolicy,
            sharedQfmStateRoot,
            "external_signed_fixture");
        await RequireFleetCodeAsync(
            () => new FleetInstallerHandoff(
                    wrongSignerSettings,
                    timeProvider: release.TimeProvider)
                .GetStatusAsync(),
            "fleet_descriptor_binding_invalid").ConfigureAwait(false);

        foreach (var source in new IFleetReleaseSource[]
                 {
                     new CorruptingReleaseSource(
                         new LocalFleetReleaseSource(
                             release.DescriptorPath)),
                     new PartialReleaseSource(
                         new LocalFleetReleaseSource(
                             release.DescriptorPath))
                 })
        {
            var stateRoot = Path.Combine(
                Path.GetTempPath(),
                "qfm-fleet-lifecycle-adversarial-" +
                Guid.NewGuid().ToString("N"));
            var store = new MemoryInitializationStore();
            store.SetupRepair(
                FleetInstallerWorkspace.StateRootDigest(stateRoot));
            InitializeReplayFiles(stateRoot);
            try
            {
                var settings = new FleetInstallerSettings(
                    source,
                    release.Policy,
                    stateRoot,
                    "external_signed_fixture");
                var service = new FleetInstallerHandoff(
                    settings,
                    new WindowsFleetInstallerArtifactTrustVerifier(),
                    new ControlledSetupRunner(
                        GuidedMode.Install,
                        installRoot),
                    release.TimeProvider,
                    store);
                await RequireFleetCodeAsync(
                    () => service.InstallAsync(),
                    source is PartialReleaseSource
                        ? "fleet_asset_size_mismatch"
                        : "fleet_asset_digest_mismatch")
                    .ConfigureAwait(false);
                RequireNoQfmStages(stateRoot);
            }
            finally
            {
                if (!DeleteOwnedQfmState(stateRoot))
                {
                    throw new InvalidOperationException(
                        "Adversarial QFM staging cleanup did not complete.");
                }
            }
        }
    }

    private static FleetInstallerHandoff CreateService(
        ExternalRelease release,
        string stateRoot,
        MemoryInitializationStore initializationStore,
        IFleetInstallerProcessRunner runner,
        TimeProvider? timeProvider = null)
    {
        var settings = new FleetInstallerSettings(
            new LocalFleetReleaseSource(release.DescriptorPath),
            release.Policy,
            stateRoot,
            "external_signed_fixture");
        return new FleetInstallerHandoff(
            settings,
            new WindowsFleetInstallerArtifactTrustVerifier(),
            runner,
            timeProvider ?? release.TimeProvider,
            initializationStore);
    }

    private static void ValidateInput(LifecycleInput input)
    {
        if (input.Schema != InputSchema ||
            input.ArtifactKind is not ("signed_synthetic" or "signed_release") ||
            !FleetInstallerValidation.IsReleaseChannel(input.Channel) ||
            !FleetInstallerValidation.IsLowerSha256(
                input.TrustedDescriptorSignerSpkiSha256) ||
            !FleetInstallerValidation.IsLowerSha256(
                input.TrustedInstallerSignerCertificateSha256))
        {
            throw new InvalidOperationException(
                "The lifecycle input contract is invalid.");
        }
        if (input.ArtifactKind == "signed_release" &&
            input.CleanupFixtureInstallRoot)
        {
            throw new InvalidOperationException(
                "A production release install root cannot be selected for fixture cleanup.");
        }
        if (input.ArtifactKind == "signed_synthetic" &&
            !input.CleanupFixtureInstallRoot)
        {
            throw new InvalidOperationException(
                "A synthetic lifecycle run must clean its dedicated fixture install root.");
        }
    }

    private static string ValidateInstallRoot(LifecycleInput input)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(input.InstallRoot));
        if (!Path.IsPathFullyQualified(root) ||
            root.StartsWith(@"\\", StringComparison.Ordinal) ||
            Directory.Exists(root) ||
            File.Exists(root))
        {
            throw new InvalidOperationException(
                "The lifecycle install root must be a new local absolute path.");
        }
        if (input.CleanupFixtureInstallRoot)
        {
            var temporaryRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            if (!root.StartsWith(
                    temporaryRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith(
                    "qfm-fleet-lifecycle-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Fixture cleanup is restricted to a dedicated lifecycle root under the system temporary directory.");
            }
        }
        return root;
    }

    private static void ValidateReleasePair(
        ExternalRelease releaseA,
        ExternalRelease releaseB)
    {
        if (!releaseA.SpkiBytes.AsSpan().SequenceEqual(
                releaseB.SpkiBytes) ||
            releaseA.Descriptor.DescriptorSignerSpkiSha256 !=
                releaseB.Descriptor.DescriptorSignerSpkiSha256 ||
            releaseA.Descriptor.Asset.SignerCertificateSha256 !=
                releaseB.Descriptor.Asset.SignerCertificateSha256 ||
            releaseA.Descriptor.Channel != releaseB.Descriptor.Channel ||
            Version.Parse(releaseA.Descriptor.Version) >=
                Version.Parse(releaseB.Descriptor.Version))
        {
            throw new InvalidOperationException(
                "The external Fleet releases are not an ordered same-signer A-to-B pair.");
        }
    }

    private static void RequireNoPathOverlap(
        string installRoot,
        params string[] releaseRoots)
    {
        var installPrefix =
            Path.TrimEndingDirectorySeparator(installRoot) +
            Path.DirectorySeparatorChar;
        for (var index = 0;
             index < releaseRoots.Length;
             index++)
        {
            var releaseRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(releaseRoots[index]));
            var releasePrefix =
                releaseRoot + Path.DirectorySeparatorChar;
            if (installRoot.Equals(
                    releaseRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                installRoot.StartsWith(
                    releasePrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                releaseRoot.StartsWith(
                    installPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The Fleet install root overlaps retained release input.");
            }
            for (var other = index + 1;
                 other < releaseRoots.Length;
                 other++)
            {
                var otherRoot = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(releaseRoots[other]));
                var otherPrefix =
                    otherRoot + Path.DirectorySeparatorChar;
                if (releaseRoot.Equals(
                        otherRoot,
                        StringComparison.OrdinalIgnoreCase) ||
                    releaseRoot.StartsWith(
                        otherPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                    otherRoot.StartsWith(
                        releasePrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Fleet release A and B roots must be distinct and non-overlapping.");
                }
            }
        }
    }

    private static void RequireSuccessfulHandoff(
        FleetInstallerHandoffReceipt receipt,
        ExternalRelease release)
    {
        Require(
            receipt.Status == "guided_installer_completed" &&
            receipt.Version == release.Descriptor.Version &&
            receipt.DescriptorId == release.Descriptor.DescriptorId &&
            receipt.AssetSha256 == release.Descriptor.Asset.Sha256 &&
            receipt.PlanVerified &&
            receipt.GuidedInstallerStarted &&
            receipt.GuidedInstallerExitCode == 0 &&
            receipt.CleanupCompleted,
            "The QFM handoff receipt did not bind the external Fleet release.");
    }

    private static void InitializeReplayFiles(string stateRoot)
    {
        Directory.CreateDirectory(stateRoot);
        File.WriteAllText(
            Path.Combine(stateRoot, "fleet-installer.state.json"),
            JsonSerializer.Serialize(
                FleetInstallerState.Empty,
                new JsonSerializerOptions(FleetInstallerValidation.Json)
                {
                    DefaultIgnoreCondition =
                        JsonIgnoreCondition.Never
                }),
            new UTF8Encoding(false));
        File.WriteAllText(
            FleetInstallerWorkspace.GetDurableAnchorPath(stateRoot),
            JsonSerializer.Serialize(
                new FleetInstallerStateAnchor(
                    FleetInstallerContract.StateAnchorSchema,
                    FleetInstallerWorkspace.StateRootDigest(stateRoot)),
                FleetInstallerValidation.Json),
            new UTF8Encoding(false));
    }

    private static void RequireNoQfmStages(string stateRoot)
    {
        Require(
            !Directory.EnumerateDirectories(
                    stateRoot,
                    "fleet-*",
                    SearchOption.TopDirectoryOnly)
                .Any(),
            "QFM left an operation-owned Fleet installer stage.");
    }

    private static int CountInterruptedCandidates(string installRoot)
    {
        var releases = Path.Combine(installRoot, "releases");
        return Directory.Exists(releases)
            ? Directory.EnumerateDirectories(
                    releases,
                    ".candidate-*",
                    SearchOption.TopDirectoryOnly)
                .Count()
            : 0;
    }

    private static FleetSetupState ReadFleetState(string installRoot)
    {
        var statePath = Path.Combine(
            installRoot,
            "state",
            "current.json");
        return ReadJson<FleetSetupState>(
            statePath,
            64 * 1024,
            "Fleet Setup state").Validate();
    }

    private static void RequireReleaseReadback(
        string installRoot,
        FleetSetupReleasePointer pointer,
        ExternalRelease release)
    {
        var relative = pointer.RelativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(relative) ||
            relative.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidOperationException(
                "Fleet state contains an unsafe release pointer.");
        }
        var releaseRoot = Path.GetFullPath(
            Path.Combine(installRoot, relative));
        var expectedPrefix =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(installRoot)) +
            Path.DirectorySeparatorChar;
        Require(
            releaseRoot.StartsWith(
                expectedPrefix,
                StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(releaseRoot) &&
            pointer.Version == release.Descriptor.Version &&
            pointer.ManifestSha256 ==
                release.BuildReceipt.ManifestSha256 &&
            pointer.BundleSha256 ==
                release.BuildReceipt.BundleSha256,
            "Fleet rollback readback did not resolve the expected retained release.");
    }

    private static async Task RequireFleetCodeAsync(
        Func<Task> action,
        params string[] expectedCodes)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (FleetInstallerException exception) when (
            expectedCodes.Contains(exception.Code, StringComparer.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException(
            "The expected Fleet installer rejection did not occur.");
    }

    private static async Task RequireExceptionAsync<TException>(
        Func<Task> action,
        string? expectedCode)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception) when (
            expectedCode is null ||
            exception is FleetInstallerException fleet &&
            fleet.Code == expectedCode)
        {
            return;
        }
        throw new InvalidOperationException(
            $"The expected {typeof(TException).Name} did not occur.");
    }

    private static T ReadJson<T>(
        string path,
        int maximumBytes,
        string context)
    {
        var fullPath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        if (bytes.Length is < 2 || bytes.Length > maximumBytes)
        {
            throw new InvalidOperationException(
                $"The {context} exceeds its byte bound.");
        }
        FleetInstallerValidation.RejectDuplicateProperties(bytes);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Json) ??
                throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The {context} is not strict JSON.",
                exception);
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();

    private static string FileSha256(string path)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input))
            .ToLowerInvariant();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool DeleteOwnedQfmState(string stateRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stateRoot));
        var temporaryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        if (!root.StartsWith(
                temporaryRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(root).StartsWith(
                "qfm-fleet-lifecycle-",
                StringComparison.Ordinal) ||
            ContainsReparsePoint(root))
        {
            return false;
        }
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        var anchor =
            FleetInstallerWorkspace.GetDurableAnchorPath(root);
        if (File.Exists(anchor))
        {
            File.Delete(anchor);
        }
        return !Directory.Exists(root) && !File.Exists(anchor);
    }

    private static bool DeleteOwnedFixtureInstallRoot(string installRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installRoot));
        var temporaryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        if (!root.StartsWith(
                temporaryRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(root).StartsWith(
                "qfm-fleet-lifecycle-",
                StringComparison.Ordinal) ||
            ContainsReparsePoint(root))
        {
            return false;
        }
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        return !Directory.Exists(root);
    }

    private static bool ContainsReparsePoint(string root)
    {
        if (!Directory.Exists(root))
        {
            return false;
        }
        if ((File.GetAttributes(root) &
             FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in
                     Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes &
                     FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                if ((attributes &
                     FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                }
            }
        }
        return false;
    }

    private static string FailureCode(Exception exception) =>
        exception switch
        {
            FleetInstallerException fleet => fleet.Code,
            OperationCanceledException => "lifecycle_cancelled",
            TimeoutException => "lifecycle_timeout",
            _ => "lifecycle_validation_failed"
        };

    private sealed record LifecycleInput(
        string Schema,
        string ArtifactKind,
        string ReleaseARoot,
        string ReleaseBRoot,
        string InstallRoot,
        string Channel,
        string TrustedDescriptorSignerSpkiSha256,
        string TrustedInstallerSignerCertificateSha256,
        bool CleanupFixtureInstallRoot);

    private sealed record LifecycleReleaseReceipt(
        string Version,
        string Channel,
        string DescriptorId,
        string PayloadSha256,
        string AssetSha256,
        long AssetSizeBytes,
        string InstallerSignerCertificateSha256,
        string SourceRevision,
        string SourceTree,
        bool PlanVerified);

    private sealed record LifecycleReceipt(
        string Schema,
        string Status,
        string ArtifactKind,
        string ExecutionMode,
        string ReplayAuthorityMode,
        LifecycleReleaseReceipt ReleaseA,
        LifecycleReleaseReceipt ReleaseB,
        bool StatusAndPlanVerified,
        bool SameSignerUpdateVerified,
        bool SideBySideReleaseRetentionVerified,
        bool ExactRollbackReadbackVerified,
        bool ReplayRejected,
        bool DowngradeRejected,
        bool ReplayHighWaterPreservedAfterRollback,
        bool MissingMachineAuthorityRejected,
        bool WrongSignerRejected,
        bool WrongHashRejected,
        bool WrongSpkiRejected,
        bool CanonicalAssetUrlVerified,
        bool StaleMetadataRejected,
        bool PartialStagingRejectedAndCleaned,
        bool CancellationVerified,
        bool InterruptedCandidateRetainedInert,
        bool InterruptedRecoveryVerified,
        int InterruptedCandidateCount,
        bool QfmStageCleanupVerified,
        bool FixtureInstallCleanupVerified,
        long ObservedAtMs);

    private sealed record LifecycleFailure(
        string Schema,
        string Status,
        string ErrorCode);

    private sealed record SetupBuildReceipt(
        string Schema,
        string Result,
        string Version,
        string Channel,
        string ProductChannel,
        string Maturity,
        string DistributionTrack,
        string BuildKind,
        string SetupSha256,
        string BundleSha256,
        string ManifestSha256,
        string SourceRevision,
        string SourceTree,
        bool SourceTreeClean,
        string CanonicalPePayloadSha256,
        long CanonicalPePayloadSizeBytes,
        string AuthenticodeTrustMode,
        string? SignerCertificateSha256,
        bool SignerSelfIssued,
        bool PublicTrustClaim,
        bool TimestampRequired,
        string DistributionEligibility);

    private sealed record ReleasePrimaryArtifact(
        string Role,
        string Name,
        string Sha256,
        long Bytes,
        string Url);

    private sealed record ReleaseDescriptorReceipt(
        string Schema,
        string Result,
        string DescriptorId,
        string Version,
        string ProductChannel,
        string Maturity,
        string Channel,
        string DistributionTrack,
        string ReleaseTag,
        string InstallationIdentity,
        ReleasePrimaryArtifact PrimaryArtifact,
        long IssuedAtMs,
        long ExpiresAtMs,
        long ValidityDurationMs,
        string SetupSha256,
        long SetupSizeBytes,
        string SetupSignerCertificateSha256,
        string SetupSignerSubject,
        string SetupSignerThumbprint,
        bool SetupSignerSelfIssued,
        string AuthenticodeTrustMode,
        bool PublicTrustClaim,
        bool TimestampRequired,
        string SetupBuildReceiptSha256,
        string SourceRevision,
        string SourceTree,
        string CanonicalPePayloadSha256,
        long CanonicalPePayloadSizeBytes,
        string DescriptorSignerSpkiSha256,
        string DescriptorSignerSpkiAsset,
        string PayloadSha256,
        string DescriptorSha256,
        string CanonicalPayload,
        string Signature,
        string PagesPath,
        string AssetUrl);

    private sealed record FleetSetupReleasePointer(
        string Version,
        string ReleaseId,
        string ManifestSha256,
        string BundleSha256,
        string RelativePath);

    private sealed record FleetSetupPolicy(
        string Update,
        string Rollback,
        bool AutomaticDelete);

    private sealed record FleetSetupState(
        string Schema,
        FleetSetupReleasePointer Current,
        IReadOnlyList<FleetSetupReleasePointer> History,
        FleetSetupPolicy Policy)
    {
        public FleetSetupState Validate()
        {
            var releases = new[] { Current }
                .Concat(History)
                .ToArray();
            Require(
                Schema == FleetStateSchema &&
                Policy.Update ==
                    "side_by_side_exact_manifest" &&
                Policy.Rollback ==
                    "previous_fully_verified_release" &&
                !Policy.AutomaticDelete &&
                History.Count <= 32 &&
                releases.All(item =>
                    FleetInstallerValidation.IsThreePartVersion(
                        item.Version) &&
                    !string.IsNullOrWhiteSpace(item.ReleaseId) &&
                    FleetInstallerValidation.IsLowerSha256(
                        item.ManifestSha256) &&
                    FleetInstallerValidation.IsLowerSha256(
                        item.BundleSha256)) &&
                releases.Select(item => item.ReleaseId)
                    .Distinct(StringComparer.Ordinal)
                    .Count() == releases.Length,
                "Fleet Setup state has an invalid lifecycle contract.");
            return this;
        }
    }

    private sealed class ExternalRelease
    {
        private ExternalRelease(
            string root,
            string setupPath,
            string descriptorPath,
            byte[] spkiBytes,
            FleetInstallerTrustPolicy policy,
            FleetReleaseDescriptor descriptor,
            FleetInstallerPlanReceipt plan,
            SetupBuildReceipt buildReceipt,
            ReleaseDescriptorReceipt descriptorReceipt,
            TimeProvider timeProvider)
        {
            Root = root;
            SetupPath = setupPath;
            DescriptorPath = descriptorPath;
            SpkiBytes = spkiBytes;
            Policy = policy;
            Descriptor = descriptor;
            Plan = plan;
            BuildReceipt = buildReceipt;
            DescriptorReceipt = descriptorReceipt;
            TimeProvider = timeProvider;
        }

        public string Root { get; }
        public string SetupPath { get; }
        public string DescriptorPath { get; }
        public byte[] SpkiBytes { get; }
        public FleetInstallerTrustPolicy Policy { get; }
        public FleetReleaseDescriptor Descriptor { get; }
        public FleetInstallerPlanReceipt Plan { get; }
        public SetupBuildReceipt BuildReceipt { get; }
        public ReleaseDescriptorReceipt DescriptorReceipt { get; }
        public TimeProvider TimeProvider { get; }

        public static async Task<ExternalRelease> LoadAsync(
            string root,
            string channel,
            string trustedSpkiPin,
            string trustedInstallerSignerPin)
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(root));
            if (!Directory.Exists(fullRoot) ||
                (File.GetAttributes(fullRoot) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "The external Fleet release root is missing or reparsed.");
            }
            var setupName = FleetInstallerContract.AssetNameForChannel(channel);
            var setupPath = ExactLeaf(fullRoot, setupName);
            var descriptorPath = ExactLeaf(fullRoot, "release.json");
            var buildReceiptPath = ExactLeaf(
                fullRoot,
                Path.GetFileNameWithoutExtension(setupName) + ".build-receipt.json");
            var descriptorReceiptPath = ExactLeaf(
                fullRoot,
                "release-descriptor.receipt.json");
            var spkiPath = ExactLeaf(
                fullRoot,
                "release-descriptor.spki.der");

            var spki = File.ReadAllBytes(spkiPath);
            Require(
                spki.Length is >= 64 and <= 4096 &&
                Sha256(spki) == trustedSpkiPin,
                "The staged descriptor SPKI does not match independent trust.");
            var policy = new FleetInstallerTrustPolicy(
                spki,
                trustedSpkiPin,
                trustedInstallerSignerPin,
                channel);
            policy.Validate();

            var descriptorBytes = File.ReadAllBytes(descriptorPath);
            var now = DateTimeOffset.UtcNow;
            var descriptor = FleetInstallerValidation.VerifyDescriptor(
                descriptorBytes,
                policy,
                now);
            var timeProvider = new FixedTimeProvider(now);
            var signer =
                new WindowsFleetInstallerArtifactTrustVerifier()
                    .Verify(setupPath, descriptor.Asset);
            Require(
                signer == trustedInstallerSignerPin &&
                signer ==
                    descriptor.Asset.SignerCertificateSha256,
                "The staged Fleet Setup signer does not match independent trust.");
            Require(
                FileSha256(setupPath) == descriptor.Asset.Sha256 &&
                new FileInfo(setupPath).Length ==
                    descriptor.Asset.SizeBytes,
                "The staged Fleet Setup bytes do not match the signed descriptor.");

            var plan =
                await new FleetInstallerProcessRunner()
                    .RunPlanAsync(
                        setupPath,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            Require(
                plan.Schema == FleetInstallerContract.PlanSchema &&
                plan.Product == descriptor.Product &&
                plan.Version == descriptor.Version &&
                plan.Channel == descriptor.Channel &&
                plan.AssetSha256 == descriptor.Asset.Sha256 &&
                plan.AuthenticodeTrustMode == descriptor.Asset.AuthenticodeTrustMode &&
                plan.SignerCertificateSha256 ==
                    descriptor.Asset.SignerCertificateSha256 &&
                plan.SignerSelfIssued == descriptor.Asset.SignerSelfIssued &&
                plan.PublicTrustClaim == descriptor.Asset.PublicTrustClaim &&
                plan.TimestampRequired == descriptor.Asset.TimestampRequired &&
                plan.Ready,
                "The exact Fleet Setup plan does not bind the signed descriptor.");

            var buildReceipt = ReadJson<SetupBuildReceipt>(
                buildReceiptPath,
                64 * 1024,
                "Fleet Setup build receipt");
            var descriptorReceipt =
                ReadJson<ReleaseDescriptorReceipt>(
                    descriptorReceiptPath,
                    64 * 1024,
                    "Fleet release descriptor receipt");
            ValidateReceipts(
                setupPath,
                descriptorPath,
                buildReceiptPath,
                spkiPath,
                descriptor,
                buildReceipt,
                descriptorReceipt);
            return new ExternalRelease(
                fullRoot,
                setupPath,
                descriptorPath,
                spki,
                policy,
                descriptor,
                plan,
                buildReceipt,
                descriptorReceipt,
                timeProvider);
        }

        private static void ValidateReceipts(
            string setupPath,
            string descriptorPath,
            string buildReceiptPath,
            string spkiPath,
            FleetReleaseDescriptor descriptor,
            SetupBuildReceipt build,
            ReleaseDescriptorReceipt receipt)
        {
            var actualSetupSha = FileSha256(setupPath);
            Require(
                build.Schema == BuildReceiptSchema &&
                build.Result == "pass" &&
                build.Version == descriptor.Version &&
                build.Channel == descriptor.Channel &&
                build.ProductChannel == descriptor.ProductChannel &&
                build.Maturity == descriptor.Maturity &&
                build.DistributionTrack == descriptor.DistributionTrack &&
                build.BuildKind == "signed-release" &&
                build.SourceTreeClean &&
                build.DistributionEligibility ==
                    "requires_setup_authenticode_signing" &&
                FleetInstallerValidation.IsLowerSha256(
                    build.SetupSha256) &&
                build.SetupSha256 ==
                    build.CanonicalPePayloadSha256 &&
                FleetInstallerValidation.IsLowerSha256(
                    build.BundleSha256) &&
                FleetInstallerValidation.IsLowerSha256(
                    build.ManifestSha256) &&
                IsLowerHex40(build.SourceRevision) &&
                IsLowerHex40(build.SourceTree) &&
                FleetInstallerValidation.IsLowerSha256(
                    build.CanonicalPePayloadSha256) &&
                build.CanonicalPePayloadSizeBytes > 0 &&
                build.AuthenticodeTrustMode ==
                    descriptor.Asset.AuthenticodeTrustMode &&
                build.SignerCertificateSha256 ==
                    descriptor.Asset.SignerCertificateSha256 &&
                build.SignerSelfIssued == descriptor.Asset.SignerSelfIssued &&
                build.PublicTrustClaim == descriptor.Asset.PublicTrustClaim &&
                build.TimestampRequired == descriptor.Asset.TimestampRequired,
                "The Fleet Setup build receipt is invalid.");
            var releaseTag = new Uri(descriptor.Asset.Url).Segments[^2].TrimEnd('/');
            Require(
                receipt.Schema == DescriptorReceiptSchema &&
                receipt.Result == "pass" &&
                receipt.DescriptorId == descriptor.DescriptorId &&
                receipt.Version == descriptor.Version &&
                receipt.ProductChannel == descriptor.ProductChannel &&
                receipt.Maturity == descriptor.Maturity &&
                receipt.Channel == descriptor.Channel &&
                receipt.DistributionTrack == descriptor.DistributionTrack &&
                receipt.ReleaseTag == releaseTag &&
                receipt.InstallationIdentity == descriptor.Product &&
                receipt.PrimaryArtifact.Role == "complete-product" &&
                receipt.PrimaryArtifact.Name == descriptor.Asset.Name &&
                receipt.PrimaryArtifact.Sha256 == descriptor.Asset.Sha256 &&
                receipt.PrimaryArtifact.Bytes == descriptor.Asset.SizeBytes &&
                receipt.PrimaryArtifact.Url == descriptor.Asset.Url &&
                receipt.IssuedAtMs == descriptor.IssuedAtMs &&
                receipt.ExpiresAtMs == descriptor.ExpiresAtMs &&
                receipt.ValidityDurationMs ==
                    descriptor.ValidityDurationMs &&
                receipt.SetupSha256 == actualSetupSha &&
                receipt.SetupSizeBytes ==
                    new FileInfo(setupPath).Length &&
                receipt.SetupSignerCertificateSha256 ==
                    descriptor.Asset.SignerCertificateSha256 &&
                receipt.SetupSignerSubject == descriptor.Asset.SignerSubject &&
                receipt.SetupSignerThumbprint == descriptor.Asset.SignerThumbprint &&
                receipt.SetupSignerSelfIssued == descriptor.Asset.SignerSelfIssued &&
                receipt.AuthenticodeTrustMode == descriptor.Asset.AuthenticodeTrustMode &&
                receipt.PublicTrustClaim == descriptor.Asset.PublicTrustClaim &&
                receipt.TimestampRequired == descriptor.Asset.TimestampRequired &&
                receipt.SetupBuildReceiptSha256 ==
                    FileSha256(buildReceiptPath) &&
                receipt.SourceRevision == build.SourceRevision &&
                receipt.SourceTree == build.SourceTree &&
                receipt.CanonicalPePayloadSha256 ==
                    build.CanonicalPePayloadSha256 &&
                receipt.CanonicalPePayloadSizeBytes ==
                    build.CanonicalPePayloadSizeBytes &&
                receipt.DescriptorSignerSpkiSha256 ==
                    descriptor.DescriptorSignerSpkiSha256 &&
                receipt.DescriptorSignerSpkiAsset ==
                    Path.GetFileName(spkiPath) &&
                receipt.PayloadSha256 ==
                    descriptor.PayloadSha256 &&
                receipt.DescriptorSha256 ==
                    FileSha256(descriptorPath) &&
                receipt.CanonicalPayload ==
                    "rfc8785_jcs_closed_shape" &&
                receipt.Signature == "rsa_pss_sha256" &&
                receipt.PagesPath ==
                    $"Rusty-Fleet/metadata/{descriptor.Channel}/release.json" &&
                receipt.AssetUrl == descriptor.Asset.Url,
                "The Fleet descriptor receipt is not exact hash-bound evidence.");
        }

        private static bool IsLowerHex40(string value) =>
            value.Length == 40 &&
            value.All(character =>
                char.IsAsciiHexDigit(character) &&
                !char.IsAsciiLetterUpper(character));

        private static string ExactLeaf(
            string root,
            string name)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path) ||
                (File.GetAttributes(path) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"The external Fleet release is missing {name}.");
            }
            return path;
        }
    }

    private enum GuidedMode
    {
        Install,
        Rollback,
        InterruptAfterCandidate
    }

    private sealed class ControlledSetupRunner(
        GuidedMode mode,
        string installRoot,
        CancellationTokenSource? interruption = null) :
        IFleetInstallerProcessRunner
    {
        private readonly FleetInstallerProcessRunner _planRunner = new();

        public Task<FleetInstallerPlanReceipt> RunPlanAsync(
            string executablePath,
            CancellationToken cancellationToken) =>
            _planRunner.RunPlanAsync(
                executablePath,
                cancellationToken);

        public Task<int> RunGuidedAsync(
            string executablePath,
            CancellationToken cancellationToken) =>
            RunDirectAsync(
                executablePath,
                mode,
                installRoot,
                cancellationToken,
                interruption);

        public static async Task<int> RunDirectAsync(
            string executablePath,
            GuidedMode mode,
            string installRoot,
            CancellationToken cancellationToken,
            CancellationTokenSource? interruption = null)
        {
            var initialCandidateCount =
                CountInterruptedCandidates(installRoot);
            var start = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(
                    executablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = new Process { StartInfo = start };
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "The external Fleet Setup did not start.");
            }
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(45));
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
            var stdout = ReadBoundedAsync(
                process.StandardOutput,
                64 * 1024,
                linked.Token);
            var stderr = ReadBoundedAsync(
                process.StandardError,
                64 * 1024,
                linked.Token);
            process.StandardInput.WriteLine(
                mode == GuidedMode.Rollback ? "r" : "i");
            process.StandardInput.Close();
            try
            {
                if (mode ==
                    GuidedMode.InterruptAfterCandidate)
                {
                    await WaitForCandidateAsync(
                        installRoot,
                        initialCandidateCount,
                        process,
                        linked.Token).ConfigureAwait(false);
                    interruption?.Cancel();
                }
                await Task.WhenAll(
                        process.WaitForExitAsync(linked.Token),
                        stdout,
                        stderr)
                    .ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0 ||
                    error.Length != 0)
                {
                    throw new InvalidOperationException(
                        "The external Fleet Setup lifecycle action failed.");
                }
                return process.ExitCode;
            }
            catch
            {
                TryKill(process);
                throw;
            }
        }

        private static async Task<string> ReadBoundedAsync(
            TextReader reader,
            int maximumCharacters,
            CancellationToken cancellationToken)
        {
            var output = new StringBuilder();
            var buffer = new char[4096];
            while (true)
            {
                var read = await reader.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return output.ToString();
                }
                if (output.Length + read >
                    maximumCharacters)
                {
                    throw new InvalidOperationException(
                        "The external Fleet Setup exceeded its output bound.");
                }
                output.Append(buffer, 0, read);
            }
        }

        private static async Task WaitForCandidateAsync(
            string installRoot,
            int initialCandidateCount,
            Process process,
            CancellationToken cancellationToken)
        {
            while (!process.HasExited)
            {
                if (CountInterruptedCandidates(installRoot) >
                    initialCandidateCount)
                {
                    return;
                }
                await Task.Delay(
                    TimeSpan.FromMilliseconds(20),
                    cancellationToken).ConfigureAwait(false);
            }
            throw new InvalidOperationException(
                "Fleet Setup exited before an interruptible candidate was retained.");
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // The original bounded lifecycle failure is authoritative.
            }
        }
    }

    private sealed class MemoryInitializationStore :
        IFleetInstallerInitializationStore
    {
        private string? _stateRootSha256;
        private FleetInstallerProtectedState? _state;

        public string? HighestHandoffVersion =>
            _state?.HighestHandoffVersion;

        public FleetInstallerProtectedState? Read(
            string stateRootSha256) =>
            _stateRootSha256 == stateRootSha256
                ? _state
                : null;

        public FleetInstallerProtectedState Accept(
            string stateRootSha256,
            FleetReleaseDescriptor descriptor)
        {
            var current = Read(stateRootSha256) ??
                throw new FleetInstallerException(
                    "fleet_installer_recovery_required",
                    "The fixture replay authority is missing.");
            if (current.AcceptedDescriptorIds.Contains(
                    descriptor.DescriptorId,
                    StringComparer.Ordinal) ||
                current.HighestHandoffVersion is not null &&
                Version.Parse(descriptor.Version) <=
                Version.Parse(current.HighestHandoffVersion))
            {
                throw new FleetInstallerException(
                    "fleet_descriptor_replay",
                    "The fixture replay authority rejected the transition.");
            }
            _state = current with
            {
                HighestHandoffVersion = descriptor.Version,
                AcceptedDescriptorIds =
                    current.AcceptedDescriptorIds
                        .Append(descriptor.DescriptorId)
                        .TakeLast(256)
                        .ToArray()
            };
            return _state;
        }

        public void SetupRepair(string stateRootSha256)
        {
            _stateRootSha256 = stateRootSha256;
            _state =
                FleetInstallerProtectedState.Empty(
                    stateRootSha256);
        }
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CorruptingReleaseSource(
        IFleetReleaseSource inner) : IFleetReleaseSource
    {
        public string Kind => "external_corrupting_fixture";

        public Task<byte[]> ReadDescriptorAsync(
            CancellationToken cancellationToken) =>
            inner.ReadDescriptorAsync(cancellationToken);

        public async Task CopyAssetAsync(
            FleetReleaseAsset asset,
            Stream destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await inner.CopyAssetAsync(
                asset,
                buffer,
                maximumBytes,
                cancellationToken).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            bytes[^1] ^= 0x01;
            await destination.WriteAsync(
                bytes,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class PartialReleaseSource(
        IFleetReleaseSource inner) : IFleetReleaseSource
    {
        public string Kind => "external_partial_fixture";

        public Task<byte[]> ReadDescriptorAsync(
            CancellationToken cancellationToken) =>
            inner.ReadDescriptorAsync(cancellationToken);

        public async Task CopyAssetAsync(
            FleetReleaseAsset asset,
            Stream destination,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await inner.CopyAssetAsync(
                asset,
                buffer,
                maximumBytes,
                cancellationToken).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            await destination.WriteAsync(
                bytes.AsMemory(0, bytes.Length / 2),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
