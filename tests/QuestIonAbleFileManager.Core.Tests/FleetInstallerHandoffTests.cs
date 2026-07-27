using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class FleetInstallerHandoffTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
    private static readonly string InstallerSigner = new('a', 64);

    [Fact]
    public async Task ValidOfflineReleaseHasTypedStatusAndGuidedHandoffWithoutAdb()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SignedFixture();
        var runner = new RecordingInstallerRunner(fixture.Plan);
        var service = fixture.CreateService(runner: runner);
        var executor = new OperatorCommandExecutor(client: null, service);

        var statusExecution = await executor.ExecuteAsync(
            OperatorCommands.FleetInstallStatus());
        var handoffExecution = await executor.ExecuteAsync(
            OperatorCommands.FleetInstall(operatorConfirmed: true));

        var status = Assert.IsType<FleetInstallerStatusReceipt>(
            statusExecution.FleetInstallerStatus);
        var handoff = Assert.IsType<FleetInstallerHandoffReceipt>(
            handoffExecution.FleetInstallerHandoff);
        Assert.Equal("ready", status.Status);
        Assert.Equal(FleetInstallerContract.Product, status.Product);
        Assert.Equal("guided_installer_completed", handoff.Status);
        Assert.True(handoff.PlanVerified);
        Assert.True(handoff.GuidedInstallerStarted);
        Assert.True(handoff.CleanupCompleted);
        Assert.Equal(1, runner.PlanCalls);
        Assert.Equal(1, runner.GuidedCalls);
        Assert.Empty(Directory.EnumerateDirectories(fixture.StateRoot, "fleet-*"));
        var receipts = JsonSerializer.Serialize(new { status, handoff });
        Assert.DoesNotContain(
            fixture.StateRoot,
            receipts,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            ReleaseAssetUrl("1.2.3"),
            receipts,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "https://mesmerprism.com/",
            receipts,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TypedFleetFactoriesHaveExactClosedCliRoutesAndRequireConfirmation()
    {
        Assert.Equal(
            ["fleet", "status", "--json"],
            OperatorCommands.FleetInstallStatus().CliArguments);
        Assert.Equal(
            ["fleet", "install", "--confirm-fleet-install", "--json"],
            OperatorCommands.FleetInstall(operatorConfirmed: true).CliArguments);
        Assert.Throws<InvalidOperationException>(
            () => OperatorCommands.FleetInstall());

        foreach (var invalid in new string[][]
                 {
                     ["fleet", "status"],
                     ["Fleet", "status", "--json"],
                     ["fleet", "status", "--json", "--url", "https://example.invalid"],
                     ["fleet", "install", "--json", "--confirm-fleet-install"],
                     ["fleet", "install", "--confirm-fleet-install", "--json", "--quiet"],
                     ["fleet", "install", "--confirm-fleet-install", "--confirm-fleet-install", "--json"]
                 })
        {
            Assert.Throws<ArgumentException>(
                () => OperatorCommands.ParseFleetCliArguments(invalid));
        }
    }

    [Fact]
    public async Task StrictEnvelopeRejectsUnknownFieldsAndDuplicateFields()
    {
        using var fixture = new SignedFixture();
        var unknown = JsonNode.Parse(fixture.DescriptorBytes)!.AsObject();
        unknown["unexpected"] = true;
        fixture.DescriptorBytes = Encoding.UTF8.GetBytes(
            unknown.ToJsonString());

        var exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().GetStatusAsync());
        Assert.Equal("fleet_descriptor_invalid", exception.Code);

        fixture.ResignPayload(payload => payload["unexpected"] = true);
        exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().GetStatusAsync());
        Assert.Equal("fleet_descriptor_payload_invalid", exception.Code);

        fixture.DescriptorBytes = Encoding.UTF8.GetBytes(
            "{\"schema\":\"x\",\"schema\":\"y\"}");
        exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().GetStatusAsync());
        Assert.Equal("fleet_descriptor_invalid", exception.Code);
    }

    [Fact]
    public async Task VersionOneDescriptorContractsAreRejected()
    {
        using var fixture = new SignedFixture();
        var envelope = JsonNode.Parse(fixture.DescriptorBytes)!.AsObject();
        envelope["schema"] = "rusty.fleet.release_descriptor_envelope.v1";
        fixture.DescriptorBytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());

        var exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().GetStatusAsync());
        Assert.Equal("fleet_descriptor_signer_mismatch", exception.Code);

        fixture.ResignPayload(
            payload => payload["schema"] = "rusty.fleet.windows_release.v1");
        exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().GetStatusAsync());
        Assert.Equal("fleet_descriptor_binding_invalid", exception.Code);
    }

    [Fact]
    public async Task DescriptorSignatureAndSignerPinAreBothRequired()
    {
        using var fixture = new SignedFixture();
        var envelope = JsonNode.Parse(fixture.DescriptorBytes)!.AsObject();
        var signature = envelope["signature_base64url"]!.GetValue<string>();
        envelope["signature_base64url"] =
            (signature[0] == 'A' ? "B" : "A") + signature[1..];
        fixture.DescriptorBytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());

        var exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().GetStatusAsync());
        Assert.Equal("fleet_descriptor_signature_invalid", exception.Code);

        using var otherKey = RSA.Create(2048);
        var wrongPolicy = fixture.Policy with
        {
            DescriptorSignerSubjectPublicKeyInfo =
                otherKey.ExportSubjectPublicKeyInfo()
        };
        exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService(policy: wrongPolicy).GetStatusAsync());
        Assert.Equal("fleet_descriptor_signer_pin_mismatch", exception.Code);
    }

    [Theory]
    [InlineData("product", "not-rusty-fleet")]
    [InlineData("channel", "nightly")]
    public async Task WrongProductOrChannelBindingIsRejected(
        string property,
        string value)
    {
        using var fixture = new SignedFixture();
        fixture.ResignPayload(payload => payload[property] = value);

        var exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().GetStatusAsync());
        Assert.Equal("fleet_descriptor_binding_invalid", exception.Code);
    }

    [Theory]
    [InlineData("name", "Other.exe")]
    [InlineData("media_type", "application/octet-stream")]
    [InlineData("installer_protocol", "arbitrary.v1")]
    public async Task WrongAssetIdentityIsRejected(
        string property,
        string value)
    {
        using var fixture = new SignedFixture();
        fixture.ResignPayload(
            payload => payload["asset"]!.AsObject()[property] = value);

        var exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().GetStatusAsync());
        Assert.Equal("fleet_asset_binding_invalid", exception.Code);
    }

    [Fact]
    public async Task ExpiredFutureAndOverlongDescriptorsAreRejectedAsStale()
    {
        using var fixture = new SignedFixture();
        foreach (var mutate in new Action<JsonObject>[]
                 {
                     payload =>
                     {
                         payload["issued_at_ms"] = Now.AddDays(-2).ToUnixTimeMilliseconds();
                         payload["expires_at_ms"] = Now.AddDays(-1).ToUnixTimeMilliseconds();
                     },
                     payload =>
                     {
                         payload["issued_at_ms"] = Now.AddMinutes(1).ToUnixTimeMilliseconds();
                         payload["expires_at_ms"] = Now.AddDays(1).ToUnixTimeMilliseconds();
                     },
                     payload =>
                     {
                         payload["issued_at_ms"] = Now.ToUnixTimeMilliseconds();
                         payload["expires_at_ms"] = Now.AddDays(15).ToUnixTimeMilliseconds();
                     }
                 })
        {
            fixture.ResignPayload(mutate);
            var exception = await Assert.ThrowsAsync<FleetInstallerException>(
                () => fixture.CreateService().GetStatusAsync());
            Assert.Equal("fleet_descriptor_stale", exception.Code);
        }
    }

    [Fact]
    public async Task SignedSizeAndHashBindingsAreEnforcedBeforePlan()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sizeFixture = new SignedFixture();
        sizeFixture.ResignPayload(
            payload => payload["asset"]!.AsObject()["size_bytes"] =
                sizeFixture.AssetBytes.LongLength + 1);
        var sizeException = await Assert.ThrowsAsync<FleetInstallerException>(
            () => sizeFixture.CreateService().InstallAsync());
        Assert.Equal("fleet_asset_size_mismatch", sizeException.Code);

        using var hashFixture = new SignedFixture();
        hashFixture.ResignPayload(
            payload => payload["asset"]!.AsObject()["sha256"] = new string('0', 64));
        var hashException = await Assert.ThrowsAsync<FleetInstallerException>(
            () => hashFixture.CreateService().InstallAsync());
        Assert.Equal("fleet_asset_digest_mismatch", hashException.Code);
    }

    [Fact]
    public async Task AuthenticodeSignerAndFleetPlanMustBindTheRelease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var signerFixture = new SignedFixture();
        var signerException = await Assert.ThrowsAsync<FleetInstallerException>(
            () => signerFixture.CreateService(
                verifier: new FixedVerifier(new string('b', 64))).InstallAsync());
        Assert.Equal("fleet_installer_signer_mismatch", signerException.Code);

        using var planFixture = new SignedFixture();
        var wrongPlan = planFixture.Plan with { AssetSha256 = new string('0', 64) };
        var planException = await Assert.ThrowsAsync<FleetInstallerException>(
            () => planFixture.CreateService(
                runner: new RecordingInstallerRunner(wrongPlan)).InstallAsync());
        Assert.Equal("fleet_installer_plan_mismatch", planException.Code);
    }

    [Fact]
    public async Task ReplayAndDowngradeRemainRejectedAcrossServiceInstances()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SignedFixture(version: "2.0.0", descriptorId: "release-2");
        await fixture.CreateService().InstallAsync();

        var replay = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().InstallAsync());
        Assert.Equal("fleet_descriptor_replay", replay.Code);

        fixture.ResignPayload(
            payload => payload["descriptor_id"] = "release-2-republished");
        var sameVersion = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().InstallAsync());
        Assert.Equal("fleet_release_not_newer_rejected", sameVersion.Code);

        fixture.ResignPayload(payload =>
        {
            payload["descriptor_id"] = "release-1";
            payload["version"] = "1.9.9";
            payload["asset"]!.AsObject()["url"] = ReleaseAssetUrl("1.9.9");
        });
        fixture.Plan = fixture.Plan with { Version = "1.9.9" };
        var status = await fixture.CreateService().GetStatusAsync();
        Assert.Equal("not_newer_than_last_handoff", status.Status);
        var downgrade = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().InstallAsync());
        Assert.Equal("fleet_release_downgrade_rejected", downgrade.Code);
    }

    [Fact]
    public async Task GuidedTimeoutConsumesTheVerifiedDescriptorAndCleansPrivateStage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SignedFixture();
        var timeout = new TimeoutInstallerRunner(fixture.Plan);
        await Assert.ThrowsAsync<TimeoutException>(
            () => fixture.CreateService(runner: timeout).InstallAsync());

        Assert.Empty(Directory.EnumerateDirectories(fixture.StateRoot, "fleet-*"));
        var replay = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().InstallAsync());
        Assert.Equal("fleet_descriptor_replay", replay.Code);
        var status = await fixture.CreateService().GetStatusAsync();
        Assert.Equal("already_handed_off", status.Status);
        Assert.Equal("guided_installer_failed", status.LastOutcome);
    }

    [Fact]
    public async Task ProcessTimeoutKillsTheBoundedProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => FleetInstallerProcessRunner.RunProcessAsync(
                shell,
                ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
                TimeSpan.FromMilliseconds(250),
                visible: false,
                CancellationToken.None));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task ProcessContainerKillsPipeHoldingDescendantAfterParentExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"qfm-fleet-job-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var childScript = Path.Combine(root, "child.ps1");
        var parentScript = Path.Combine(root, "parent.ps1");
        var sentinel = Path.Combine(root, "escaped.txt");
        var shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        static string QuotePs(string value) =>
            "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        await File.WriteAllTextAsync(
            childScript,
            "Start-Sleep -Seconds 2\n" +
            $"[IO.File]::WriteAllText({QuotePs(sentinel)}, 'escaped')\n");
        await File.WriteAllTextAsync(
            parentScript,
            $"Start-Process -FilePath {QuotePs(shell)} " +
            $"-ArgumentList @('-NoProfile','-File',{QuotePs(childScript)}) " +
            "-NoNewWindow\n");

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => FleetInstallerProcessRunner.RunProcessAsync(
                    shell,
                    ["-NoProfile", "-NonInteractive", "-File", parentScript],
                    TimeSpan.FromMilliseconds(500),
                    visible: false,
                    CancellationToken.None));
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(File.Exists(sentinel));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessContainerDoesNotReportSuccessWithDetachedChildActive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"qfm-fleet-detached-job-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var childScript = Path.Combine(root, "child.ps1");
        var parentScript = Path.Combine(root, "parent.ps1");
        var sentinel = Path.Combine(root, "escaped.txt");
        var shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        static string QuotePs(string value) =>
            "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        await File.WriteAllTextAsync(
            childScript,
            "Start-Sleep -Seconds 2\n" +
            $"[IO.File]::WriteAllText({QuotePs(sentinel)}, 'escaped')\n");
        await File.WriteAllTextAsync(
            parentScript,
            $"Start-Process -FilePath {QuotePs(shell)} " +
            $"-ArgumentList @('-NoProfile','-File',{QuotePs(childScript)}) " +
            "-WindowStyle Hidden\n");

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => FleetInstallerProcessRunner.RunProcessAsync(
                    shell,
                    ["-NoProfile", "-NonInteractive", "-File", parentScript],
                    TimeSpan.FromMilliseconds(500),
                    visible: false,
                    CancellationToken.None));
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(File.Exists(sentinel));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedProcessOutputKillsPromptlyAndWorkingDirectoryIsFixed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => FleetInstallerProcessRunner.RunProcessAsync(
                shell,
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "[Console]::Out.Write(('x' * 70000)); Start-Sleep -Seconds 30"
                ],
                TimeSpan.FromSeconds(20),
                visible: false,
                CancellationToken.None));
        Assert.Equal("fleet_installer_output_oversized", exception.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8));

        var cmd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var result = await FleetInstallerProcessRunner.RunProcessAsync(
            cmd,
            ["/d", "/c", "cd"],
            TimeSpan.FromSeconds(5),
            visible: false,
            CancellationToken.None);
        Assert.Equal(
            Path.GetDirectoryName(cmd),
            result.StandardOutput.Trim(),
            ignoreCase: true);
    }

    [Fact]
    public async Task HttpsSourceRejectsUnreviewedAndChainedRedirects()
    {
        var descriptor = new Uri(
            "https://mesmerprism.com/Rusty-Fleet/metadata/stable/release.json");
        using var evilClient = new HttpClient(new SequenceHandler(
            Redirect("https://example.invalid/asset")));
        using var evilSource = new HttpsFleetReleaseSource(
            descriptor,
            "stable",
            evilClient);
        var exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => evilSource.ReadDescriptorAsync(CancellationToken.None));
        Assert.Equal("fleet_source_redirect_rejected", exception.Code);

        using var escapedAssetClient = new HttpClient(new SequenceHandler(
            Redirect("https://example.invalid/RustyFleet-Setup.exe")));
        using var escapedAssetSource = new HttpsFleetReleaseSource(
            descriptor,
            "stable",
            escapedAssetClient);
        exception = await Assert.ThrowsAsync<FleetInstallerException>(async () =>
        {
            using var output = new MemoryStream();
            await escapedAssetSource.CopyAssetAsync(
                ReleaseAsset(),
                output,
                1024,
                CancellationToken.None);
        });
        Assert.Equal("fleet_source_redirect_rejected", exception.Code);

        using var alternatePortClient = new HttpClient(new SequenceHandler(
            Redirect("https://release-assets.githubusercontent.com:444/asset")));
        using var alternatePortSource = new HttpsFleetReleaseSource(
            descriptor,
            "stable",
            alternatePortClient);
        exception = await Assert.ThrowsAsync<FleetInstallerException>(async () =>
        {
            using var output = new MemoryStream();
            await alternatePortSource.CopyAssetAsync(
                ReleaseAsset(),
                output,
                1024,
                CancellationToken.None);
        });
        Assert.Equal("fleet_source_redirect_rejected", exception.Code);

        using var chainedClient = new HttpClient(new SequenceHandler(
            Redirect("https://release-assets.githubusercontent.com/one"),
            Redirect("https://release-assets.githubusercontent.com/two")));
        using var chainedSource = new HttpsFleetReleaseSource(
            descriptor,
            "stable",
            chainedClient);
        exception = await Assert.ThrowsAsync<FleetInstallerException>(async () =>
        {
            using var output = new MemoryStream();
            await chainedSource.CopyAssetAsync(
                ReleaseAsset(),
                output,
                1024,
                CancellationToken.None);
        });
        Assert.Equal("fleet_source_redirect_rejected", exception.Code);
    }

    [Fact]
    public async Task CanonicalPagesMetadataAndImmutableReleaseAssetAreSeparated()
    {
        var descriptorUri = new Uri(
            "https://mesmerprism.com/Rusty-Fleet/metadata/stable/release.json");
        var descriptorBytes = Encoding.UTF8.GetBytes("{\"fixture\":true}");
        var assetBytes = Encoding.UTF8.GetBytes("installer");
        using (var client = new HttpClient(new SequenceHandler(
                   Ok(descriptorBytes),
                   Ok(assetBytes))))
        using (var source = new HttpsFleetReleaseSource(
                   descriptorUri,
                   "stable",
                   client))
        {
            Assert.Equal(
                descriptorBytes,
                await source.ReadDescriptorAsync(CancellationToken.None));
            using var output = new MemoryStream();
            await source.CopyAssetAsync(
                ReleaseAsset(sizeBytes: assetBytes.Length),
                output,
                maximumBytes: assetBytes.Length,
                CancellationToken.None);
            Assert.Equal(assetBytes, output.ToArray());
        }
    }

    [Fact]
    public async Task ExplicitLocalFixtureStillWorksWithoutNetwork()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SignedFixture();
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"qfm-fleet-local-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var descriptorPath = Path.Combine(fixtureRoot, "release.json");
            await File.WriteAllBytesAsync(descriptorPath, fixture.DescriptorBytes);
            await File.WriteAllBytesAsync(
                Path.Combine(fixtureRoot, FleetInstallerContract.AssetName),
                fixture.AssetBytes);
            var service = new FleetInstallerHandoff(
                new FleetInstallerSettings(
                    new LocalFleetReleaseSource(descriptorPath),
                    fixture.Policy,
                    fixture.StateRoot),
                new FixedVerifier(InstallerSigner),
                new RecordingInstallerRunner(fixture.Plan),
                new FixedTimeProvider(Now));

            var receipt = await service.InstallAsync();
            Assert.Equal("guided_installer_completed", receipt.Status);
            Assert.True(receipt.CleanupCompleted);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://mesmerprism.github.io/rusty-fleet/metadata/stable/release.json")]
    [InlineData("https://mesmerprism.com/Rusty-Fleet/metadata/stable/RustyFleet-Setup.exe")]
    [InlineData("https://mesmerprism.com/Rusty-Fleet/metadata/preview/release.json")]
    [InlineData("https://github.com/MesmerPrism/rusty-fleet/releases/download/v1.2.3/release.json")]
    [InlineData("https://mesmerprism.com/Rusty-Fleet/metadata/stable/release.json?latest=1")]
    public void DescriptorSourceRejectsEveryNoncanonicalMetadataPath(string value)
    {
        var exception = Assert.Throws<FleetInstallerException>(() =>
            new HttpsFleetReleaseSource(
                new Uri(value),
                "stable",
                new HttpClient(new SequenceHandler())));
        Assert.Equal("fleet_descriptor_source_invalid", exception.Code);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("preview")]
    [InlineData("dev")]
    public void DescriptorSourceAcceptsOnlyCanonicalAllowlistedChannel(string channel)
    {
        using var source = new HttpsFleetReleaseSource(
            new Uri(
                $"https://mesmerprism.com/Rusty-Fleet/metadata/{channel}/release.json"),
            channel,
            new HttpClient(new SequenceHandler()));
        Assert.Equal("pages_metadata", source.Kind);
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("Stable")]
    [InlineData("nightly")]
    public void DescriptorSourceRejectsUnallowlistedChannel(string channel)
    {
        var exception = Assert.Throws<FleetInstallerException>(() =>
            new HttpsFleetReleaseSource(
                new Uri(
                    $"https://mesmerprism.com/Rusty-Fleet/metadata/{channel}/release.json"),
                channel,
                new HttpClient(new SequenceHandler())));
        Assert.Equal("fleet_descriptor_source_invalid", exception.Code);
    }

    [Theory]
    [InlineData("https://mesmerprism.com/Rusty-Fleet/metadata/stable/RustyFleet-Setup.exe")]
    [InlineData("https://github.com/MesmerPrism/rusty-fleet/releases/latest/download/RustyFleet-Setup.exe")]
    [InlineData("https://github.com/MesmerPrism/rusty-fleet/releases/download/v1.2.4/RustyFleet-Setup.exe")]
    [InlineData("https://github.com/MesmerPrism/rusty-fleet/releases/download/v1.2.3/Other.exe")]
    [InlineData("https://github.com/MesmerPrism/rusty-fleet/releases/download/v1.2.3/RustyFleet-Setup.exe?x=1")]
    public async Task PayloadRejectsNonimmutableOrWrongVersionAssetUrl(string value)
    {
        using var fixture = new SignedFixture();
        fixture.ResignPayload(
            payload => payload["asset"]!.AsObject()["url"] = value);

        var exception = await Assert.ThrowsAsync<FleetInstallerException>(
            () => fixture.CreateService().GetStatusAsync());
        Assert.Equal("fleet_asset_source_invalid", exception.Code);
    }

    [Fact]
    public void EmbeddedReleaseConfigurationIsCompleteAndIgnoresEnvironment()
    {
        var metadata = EmbeddedMetadata();
        var environmentRead = false;

        var settings = FleetInstallerSettings.FromConfiguration(
            _ =>
            {
                environmentRead = true;
                return "untrusted-override";
            },
            metadata);

        Assert.NotNull(settings);
        Assert.False(environmentRead);
        Assert.Equal("embedded_pages_metadata", settings.ConfigurationSourceKind);
        Assert.Equal("pages_metadata", settings.Source.Kind);
        Assert.EndsWith(
            Path.Combine("QuestIonAbleFileManager", "FleetInstaller"),
            settings.PrivateStageRoot,
            StringComparison.Ordinal);

        var withUnknown = new Dictionary<string, string>(metadata)
        {
            ["Unexpected"] = "value"
        };
        var exception = Assert.Throws<FleetInstallerException>(() =>
            FleetInstallerSettings.FromConfiguration(_ => null, withUnknown));
        Assert.Equal("fleet_embedded_configuration_invalid", exception.Code);
    }

    [Fact]
    public void IncompleteOrInvalidEmbeddedConfigurationFailsClosed()
    {
        var metadata = EmbeddedMetadata();
        metadata.Remove("InstallerSignerCertificateSha256");
        var exception = Assert.Throws<FleetInstallerException>(() =>
            FleetInstallerSettings.FromConfiguration(_ => null, metadata));
        Assert.Equal("fleet_embedded_configuration_invalid", exception.Code);

        metadata = EmbeddedMetadata();
        var invalidSpki = Enumerable.Repeat((byte)0x5a, 128).ToArray();
        metadata["DescriptorPublicKeySpkiBase64"] =
            Convert.ToBase64String(invalidSpki);
        metadata["DescriptorSignerSpkiSha256"] = Sha256(invalidSpki);
        exception = Assert.Throws<FleetInstallerException>(() =>
            FleetInstallerSettings.FromConfiguration(_ => null, metadata));
        Assert.Equal("fleet_trust_policy_invalid", exception.Code);
    }

    [Theory]
    [InlineData("../FleetInstaller")]
    [InlineData("QuestIonAbleFileManager/../FleetInstaller")]
    [InlineData("C:\\private\\FleetInstaller")]
    [InlineData("/private/FleetInstaller")]
    [InlineData("QuestIonAbleFileManager//FleetInstaller")]
    [InlineData("QuestIonAbleFileManager/Fleet Installer")]
    [InlineData("CON/FleetInstaller")]
    [InlineData("QuestIonAbleFileManager./FleetInstaller")]
    public void EmbeddedStateRootMustBeSafeCanonicalAndPerUser(string value)
    {
        var metadata = EmbeddedMetadata();
        metadata["StateRootRelativePath"] = value;

        var exception = Assert.Throws<FleetInstallerException>(() =>
            FleetInstallerSettings.FromConfiguration(_ => null, metadata));
        Assert.Equal("fleet_embedded_configuration_invalid", exception.Code);
    }

    [Fact]
    public void SecureWorkspaceRejectsReparseRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = Path.Combine(
            Path.GetTempPath(),
            $"qfm-fleet-reparse-{Guid.NewGuid():N}");
        var target = Path.Combine(parent, "target");
        var link = Path.Combine(parent, "link");
        Directory.CreateDirectory(target);
        try
        {
            var shell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "/d",
                    "/c",
                    "mklink",
                    "/J",
                    link,
                    target
                }
            });
            Assert.NotNull(process);
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Assert.ThrowsAny<Exception>(() => FleetInstallerWorkspace.Open(link));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Ok(byte[] content) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };

    private sealed class SignedFixture : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private JsonObject _payload;

        public SignedFixture(
            string version = "1.2.3",
            string descriptorId = "release-123")
        {
            AssetBytes = Encoding.UTF8.GetBytes(
                "offline signed Fleet installer fixture");
            StateRoot = Path.Combine(
                Path.GetTempPath(),
                $"qfm-fleet-state-{Guid.NewGuid():N}");
            var spki = _rsa.ExportSubjectPublicKeyInfo();
            var spkiHash = Sha256(spki);
            Policy = new FleetInstallerTrustPolicy(
                spki,
                spkiHash,
                InstallerSigner,
                "stable");
            _payload = new JsonObject
            {
                ["schema"] = FleetInstallerContract.PayloadSchema,
                ["descriptor_id"] = descriptorId,
                ["product"] = FleetInstallerContract.Product,
                ["version"] = version,
                ["channel"] = "stable",
                ["issued_at_ms"] = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
                ["expires_at_ms"] = Now.AddDays(1).ToUnixTimeMilliseconds(),
                ["asset"] = new JsonObject
                {
                    ["name"] = FleetInstallerContract.AssetName,
                    ["url"] = ReleaseAssetUrl(version),
                    ["size_bytes"] = AssetBytes.LongLength,
                    ["sha256"] = Sha256(AssetBytes),
                    ["signer_certificate_sha256"] = InstallerSigner,
                    ["media_type"] =
                        "application/vnd.microsoft.portable-executable",
                    ["installer_protocol"] =
                        FleetInstallerContract.InstallerProtocol
                }
            };
            Plan = new FleetInstallerPlanReceipt(
                FleetInstallerContract.PlanSchema,
                FleetInstallerContract.Product,
                version,
                "stable",
                Sha256(AssetBytes),
                Ready: true);
            SignPayload();
        }

        public byte[] AssetBytes { get; }

        public byte[] DescriptorBytes { get; set; } = [];

        public string StateRoot { get; }

        public FleetInstallerTrustPolicy Policy { get; }

        public FleetInstallerPlanReceipt Plan { get; set; }

        public void ResignPayload(Action<JsonObject> mutate)
        {
            mutate(_payload);
            SignPayload();
        }

        public FleetInstallerHandoff CreateService(
            FleetInstallerTrustPolicy? policy = null,
            IFleetInstallerArtifactTrustVerifier? verifier = null,
            IFleetInstallerProcessRunner? runner = null)
        {
            var source = new MemorySource(this);
            var settings = new FleetInstallerSettings(
                source,
                policy ?? Policy,
                StateRoot);
            return new FleetInstallerHandoff(
                settings,
                verifier ?? new FixedVerifier(InstallerSigner),
                runner ?? new RecordingInstallerRunner(Plan),
                new FixedTimeProvider(Now));
        }

        public void Dispose()
        {
            _rsa.Dispose();
            if (Directory.Exists(StateRoot))
            {
                Directory.Delete(StateRoot, recursive: true);
            }
        }

        private void SignPayload()
        {
            var payloadBytes = Encoding.UTF8.GetBytes(
                _payload.ToJsonString());
            var signature = _rsa.SignData(
                payloadBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            var envelope = new JsonObject
            {
                ["schema"] = FleetInstallerContract.EnvelopeSchema,
                ["payload_base64url"] = Base64Url(payloadBytes),
                ["signature_base64url"] = Base64Url(signature),
                ["signer_spki_sha256"] = Policy.DescriptorSignerSpkiSha256
            };
            DescriptorBytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        }

        private static string Base64Url(byte[] value) =>
            Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        private static string Sha256(byte[] value) =>
            Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

        private sealed class MemorySource(SignedFixture fixture) : IFleetReleaseSource
        {
            public string Kind => "offline_fixture";

            public Task<byte[]> ReadDescriptorAsync(
                CancellationToken cancellationToken) =>
                Task.FromResult(fixture.DescriptorBytes.ToArray());

            public async Task CopyAssetAsync(
                FleetReleaseAsset asset,
                Stream destination,
                long maximumBytes,
                CancellationToken cancellationToken)
            {
                Assert.Equal(FleetInstallerContract.AssetName, asset.Name);
                Assert.Equal(ReleaseAssetUrl(fixture.Plan.Version), asset.Url);
                await destination.WriteAsync(
                    fixture.AssetBytes,
                    cancellationToken);
            }
        }
    }

    private sealed class FixedVerifier(string signer) :
        IFleetInstallerArtifactTrustVerifier
    {
        public string Verify(string executablePath)
        {
            Assert.Equal(FleetInstallerContract.AssetName, Path.GetFileName(executablePath));
            return signer;
        }
    }

    private sealed class RecordingInstallerRunner(FleetInstallerPlanReceipt plan) :
        IFleetInstallerProcessRunner
    {
        public int PlanCalls { get; private set; }

        public int GuidedCalls { get; private set; }

        public Task<FleetInstallerPlanReceipt> RunPlanAsync(
            string executablePath,
            CancellationToken cancellationToken)
        {
            PlanCalls++;
            Assert.Equal(FleetInstallerContract.AssetName, Path.GetFileName(executablePath));
            return Task.FromResult(plan);
        }

        public Task<int> RunGuidedAsync(
            string executablePath,
            CancellationToken cancellationToken)
        {
            GuidedCalls++;
            Assert.Equal(FleetInstallerContract.AssetName, Path.GetFileName(executablePath));
            return Task.FromResult(0);
        }
    }

    private sealed class TimeoutInstallerRunner(FleetInstallerPlanReceipt plan) :
        IFleetInstallerProcessRunner
    {
        public Task<FleetInstallerPlanReceipt> RunPlanAsync(
            string executablePath,
            CancellationToken cancellationToken) =>
            Task.FromResult(plan);

        public Task<int> RunGuidedAsync(
            string executablePath,
            CancellationToken cancellationToken) =>
            throw new TimeoutException("fixture timeout");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequenceHandler(
        params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_index >= responses.Length)
            {
                throw new InvalidOperationException("Unexpected HTTP request.");
            }
            var response = responses[_index++];
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static FleetReleaseAsset ReleaseAsset(
        string version = "1.2.3",
        long sizeBytes = 9) =>
        new(
            FleetInstallerContract.AssetName,
            ReleaseAssetUrl(version),
            sizeBytes,
            new string('1', 64),
            InstallerSigner,
            "application/vnd.microsoft.portable-executable",
            FleetInstallerContract.InstallerProtocol);

    private static string ReleaseAssetUrl(string version) =>
        "https://github.com/MesmerPrism/rusty-fleet/releases/download/" +
        $"v{version}/{FleetInstallerContract.AssetName}";

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static Dictionary<string, string> EmbeddedMetadata()
    {
        using var rsa = RSA.Create(2048);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConfigurationVersion"] = "2",
            ["DescriptorUri"] =
                "https://mesmerprism.com/Rusty-Fleet/metadata/stable/release.json",
            ["DescriptorPublicKeySpkiBase64"] = Convert.ToBase64String(spki),
            ["DescriptorSignerSpkiSha256"] = Sha256(spki),
            ["InstallerSignerCertificateSha256"] = InstallerSigner,
            ["Channel"] = "stable",
            ["StateRootRelativePath"] =
                "QuestIonAbleFileManager/FleetInstaller"
        };
    }
}
