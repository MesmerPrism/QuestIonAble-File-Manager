using QuestIonAbleFileManager.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class RustyKioskIntegrationTests
{
    [Fact]
    public void CommandVocabularyExactlyMatchesPinnedKioskContract()
    {
        var root = FindRepositoryRoot();
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            root,
            "references",
            "rusty-kiosk-cli-command-contract.v1.json")));
        var expected = fixture.RootElement.GetProperty("commands").EnumerateArray()
            .Select(command => (
                WireName: command.GetProperty("wire_name").GetString()!,
                ValueRule: command.GetProperty("value_rule").GetString()!))
            .ToArray();
        var actual = Enum.GetValues<RustyKioskCommand>()
            .Select(command => (
                WireName: command.ToWireName(),
                ValueRule: command.RequiresValue()
                    ? "required"
                    : command.AllowsValue() ? "optional" : "none"))
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(
            RustyKioskContract.MaxCommandValueLength,
            fixture.RootElement.GetProperty("max_value_length").GetInt32());
        Assert.Equal(
            ["any", "wifi-on", "wifi-off"],
            fixture.RootElement.GetProperty("launch_requirement_values")
                .EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray());
    }

    [Theory]
    [InlineData("any", RustyKioskLaunchRequirement.Any)]
    [InlineData("wifi-on", RustyKioskLaunchRequirement.WifiOn)]
    [InlineData("wifi-off", RustyKioskLaunchRequirement.WifiOff)]
    public void ActiveLaunchRequirementValuesAreStrictAndRoundTrip(
        string value,
        RustyKioskLaunchRequirement expected)
    {
        Assert.Equal(value, RustyKioskCommand.SetLaunchRequirement.ValidateValue(value));
        Assert.Equal(expected, RustyKioskCommands.ParseLaunchRequirement(value));
        Assert.Equal(value, expected.ToWireName());
    }

    [Theory]
    [InlineData("Wifi-On")]
    [InlineData("wifi_on")]
    [InlineData("on")]
    public void ActiveLaunchRequirementValuesFailClosed(string value) =>
        Assert.Throws<ArgumentException>(() =>
            RustyKioskCommand.SetLaunchRequirement.ValidateValue(value));

    [Fact]
    public void CommandValuesAreBoundedAndRejectedWhenTheCommandHasNoValue()
    {
        Assert.Throws<ArgumentException>(() =>
            RustyKioskCommand.SetSearch.ValidateValue(new string('x', 161)));
        Assert.Throws<ArgumentException>(() =>
            RustyKioskCommand.ShowApps.ValidateValue("unexpected"));
        Assert.Throws<ArgumentException>(() =>
            RustyKioskCommand.SetLaunchRequirement.ValidateValue(null));
        Assert.Throws<ArgumentException>(() =>
            RustyKioskCommand.LaunchOption.ValidateValue(null));
        Assert.Throws<ArgumentException>(() =>
            RustyKioskCommand.LaunchOption.ValidateValue(new string('x', 161)));
        Assert.Null(RustyKioskCommand.SetSearch.ValidateValue("   "));
    }

    [Fact]
    public void LaunchOptionUsesOnlyTheBoundedOpaqueValueOnAdbAndDirectRoutes()
    {
        const string optionId = " playlist.example-1 ";
        var adb = OperatorCommands.InvokeRustyKiosk(
            "QUEST123",
            RustyKioskCommand.LaunchOption,
            optionId,
            operatorConfirmed: true,
            product: RustyKioskProductContract.For(RustyKioskProductChannel.Labs));
        var direct = KioskDirectOperatorCommand.Invoke(
            RustyKioskCommand.LaunchOption,
            optionId,
            operatorConfirmed: true);

        Assert.Equal(optionId, adb.RustyKioskValue);
        Assert.Equal(optionId, direct.Value);
        Assert.Equal(
            [
                "kiosk", "command", "--serial", "QUEST123",
                "--product-channel", "labs",
                "--command", "launch-option",
                "--value", optionId,
                "--confirm-kiosk-control"
            ],
            adb.CliArguments);
        Assert.DoesNotContain(adb.CliArguments, argument =>
            argument.Contains("component", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("activity", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("uri", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("intent", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidOperationException>(() => OperatorCommands.InvokeRustyKiosk(
            "QUEST123",
            RustyKioskCommand.LaunchOption,
            optionId,
            operatorConfirmed: false));
    }

    [Fact]
    public void ActiveRequirementUsesTheTypedAdbAndDirectCommandFactories()
    {
        var adb = OperatorCommands.InvokeRustyKiosk(
            "QUEST123",
            RustyKioskCommand.SetLaunchRequirement,
            " wifi-off ",
            operatorConfirmed: true);
        var direct = KioskDirectOperatorCommand.Invoke(
            RustyKioskCommand.SetLaunchRequirement,
            " wifi-off ",
            operatorConfirmed: true);

        Assert.Equal("wifi-off", adb.RustyKioskValue);
        Assert.Equal("wifi-off", direct.Value);
        Assert.Contains("--command", adb.CliArguments);
        Assert.Contains("set-launch-requirement", adb.CliArguments);
        Assert.Contains("--value", adb.CliArguments);
        Assert.Contains("wifi-off", adb.CliArguments);
        Assert.Contains("--confirm-kiosk-control", adb.CliArguments);
    }

    [Theory]
    [InlineData(
        RustyKioskProductChannel.Stable,
        "stable",
        "io.github.mesmerprism.rustykiosk/io.github.mesmerprism.rustykiosk.RustyKioskActivity")]
    [InlineData(
        RustyKioskProductChannel.Labs,
        "labs",
        "io.github.mesmerprism.rustykiosk.labs/io.github.mesmerprism.rustykiosk.RustyKioskActivity")]
    public void AdbOperatorFactoriesPreserveExactProductChannel(
        RustyKioskProductChannel channel,
        string wireName,
        string expectedMainActivity)
    {
        var product = RustyKioskProductContract.For(channel);
        var commands = new[]
        {
            OperatorCommands.InspectRustyKiosk("QUEST123", product),
            OperatorCommands.InvokeRustyKiosk(
                "QUEST123",
                RustyKioskCommand.ShowApps,
                product: product),
            OperatorCommands.PullRustyKioskTags("QUEST123", "tags.json", product)
        };

        Assert.All(commands, command =>
        {
            Assert.Equal(product, command.RustyKioskProduct);
            var channelIndex = command.CliArguments.ToList().IndexOf("--product-channel");
            Assert.True(channelIndex >= 0);
            Assert.Equal(wireName, command.CliArguments[channelIndex + 1]);
            Assert.Equal(expectedMainActivity, product.MainActivity);
        });
    }

    [Fact]
    public void AdbOperatorFactoriesRejectForgedCrossChannelProductIdentity()
    {
        var labs = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
        var stable = RustyKioskProductContract.For(RustyKioskProductChannel.Stable);
        var forged = labs with
        {
            MainPackage = stable.MainPackage,
            OperatorAuthority = stable.OperatorAuthority
        };

        Assert.Throws<ArgumentException>(() =>
            OperatorCommands.InspectRustyKiosk("QUEST123", forged));
        Assert.Throws<ArgumentException>(() =>
            OperatorCommands.InvokeRustyKiosk(
                "QUEST123",
                RustyKioskCommand.ShowApps,
                product: forged));
    }

    [Theory]
    [InlineData(RustyKioskProductChannel.Stable)]
    [InlineData(RustyKioskProductChannel.Labs)]
    public async Task InstallAndProvisionUseOnlyTheSelectedProductSetupIdentity(
        RustyKioskProductChannel channel)
    {
        var product = RustyKioskProductContract.For(channel);
        var other = RustyKioskProductContract.For(
            channel == RustyKioskProductChannel.Stable
                ? RustyKioskProductChannel.Labs
                : RustyKioskProductChannel.Stable);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"qfm-kiosk-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var main = Path.Combine(tempRoot, RustyKioskContract.MainApkFileName);
        var helper = Path.Combine(tempRoot, RustyKioskContract.SetupHelperApkFileName);
        await File.WriteAllBytesAsync(main, [0x50, 0x4b, 0x03, 0x04]);
        await File.WriteAllBytesAsync(helper, [0x50, 0x4b, 0x03, 0x05]);
        var bundle = CreateCommittedKioskBundle(main, helper, tempRoot, product);
        var runner = new RecordingCommandRunner((fileName, arguments) =>
        {
            if (fileName == "aapt2-test")
            {
                var package = File.ReadAllBytes(arguments[^1])[^1] == 0x04
                    ? product.MainPackage
                    : product.SetupHelperPackage;
                return Success($"package: name='{package}' versionCode='60609' versionName='0.6.6-alpha.9'\n");
            }
            if (fileName == "apksigner-test")
            {
                return Success(
                    $"Signer #1 certificate SHA-256 digest: {RustyKioskContract.TrustedSignerSha256}\n");
            }
            if (arguments.Count > 2 && arguments[2] == "install")
            {
                return Success("Success\n");
            }
            if (arguments.Contains("shell", StringComparer.Ordinal) &&
                arguments.Any(static argument => argument.Contains("pm list packages -3", StringComparison.Ordinal)))
            {
                return Success($"package:{product.MainPackage}\npackage:{product.SetupHelperPackage}\n");
            }
            if (arguments.Contains("dumpsys", StringComparer.Ordinal) &&
                arguments.Contains("package", StringComparer.Ordinal))
            {
                var package = arguments.Last();
                if (package == product.MainPackage)
                {
                    return Success(
                        $"Package [{package}]\nversionName=0.6.6-alpha.9\n" +
                        $"  {product.SetupControlPermission}: granted=true\n");
                }
                if (package == product.SetupHelperPackage)
                {
                    return Success(
                        $"Package [{package}]\nversionName=0.6.6-alpha.9\n" +
                        $"  {RustyKioskContract.WriteSecureSettingsPermission}: granted=true\n");
                }
            }
            if (arguments.Contains("grant", StringComparer.Ordinal))
            {
                return arguments.Contains(product.SetupHelperPackage, StringComparer.Ordinal) &&
                       arguments.Contains(RustyKioskContract.WriteSecureSettingsPermission, StringComparer.Ordinal)
                    ? Success(string.Empty)
                    : new CommandResult("adb-test", arguments, 1, string.Empty, "wrong grant", TimeSpan.Zero);
            }
            if (ProviderMethod(arguments) == "contract")
            {
                return Bundle(
                    $"accepted=true, completed=true, schema={RustyKioskContract.HostOperatorSuccessorSchema}, " +
                    $"package={product.MainPackage}, product_channel={product.WireName}");
            }
            return new CommandResult("adb-test", arguments, 1, string.Empty, "unexpected call", TimeSpan.Zero);
        });
        var client = new AdbClient(
            "adb-test",
            runner,
            new AndroidBuildToolPaths("aapt2-test", "apksigner-test"));
        var executor = new OperatorCommandExecutor(client);

        try
        {
            var installExecution = await executor.ExecuteAsync(
                OperatorCommands.InstallRustyKiosk(
                    "QUEST123",
                    bundle,
                    operatorConfirmed: true,
                    product: product));
            var provisionExecution = await executor.ExecuteAsync(
                OperatorCommands.ProvisionRustyKiosk(
                    "QUEST123",
                    operatorConfirmed: true,
                    product: product));
            var install = installExecution.RustyKioskInstallResult!;
            var provision = provisionExecution.RustyKioskProvisionResult!;

            Assert.Equal(product, install.Product);
            Assert.Equal(product, provision.Product);
            Assert.Equal(OperatorMutationStage.Confirmed, installExecution.MutationReceipt!.Stage);
            Assert.Equal(OperatorMutationStage.Confirmed, provisionExecution.MutationReceipt!.Stage);
            Assert.Contains(product.WireName, installExecution.MutationReceipt.DesiredState, StringComparison.Ordinal);
            Assert.Contains(product.WireName, installExecution.MutationReceipt.ObservedState, StringComparison.Ordinal);
            Assert.Contains(product.WireName, provisionExecution.MutationReceipt.DesiredState, StringComparison.Ordinal);
            Assert.Contains(product.WireName, provisionExecution.MutationReceipt.ObservedState, StringComparison.Ordinal);
            Assert.True(install.HelperReady);
            Assert.True(install.SameSignerControlGranted);
            Assert.True(provision.HelperReady);
            Assert.True(provision.SameSignerControlGranted);
            var installPaths = runner.Calls
                .Where(static call => call.FileName == "adb-test" &&
                                      call.Arguments.Count > 2 &&
                                      call.Arguments[2] == "install")
                .Select(static call => call.Arguments[^1])
                .ToArray();
            Assert.Equal(2, installPaths.Length);
            Assert.All(
                installPaths,
                path =>
                {
                    Assert.Contains("QuestIonAbleFileManager.ApkAdmission", path, StringComparison.Ordinal);
                    Assert.NotEqual(main, path);
                    Assert.NotEqual(helper, path);
                });
            Assert.Equal(
                2,
                runner.Calls.Count(call =>
                    call.Arguments.Contains("grant", StringComparer.Ordinal) &&
                    call.Arguments.Contains(product.SetupHelperPackage, StringComparer.Ordinal)));
            Assert.Contains(
                runner.Calls,
                call => ProviderMethod(call.Arguments) == "contract" &&
                        call.Arguments.Contains(product.OperatorUri, StringComparer.Ordinal));
            Assert.DoesNotContain(
                runner.Calls.SelectMany(static call => call.Arguments),
                argument => argument == other.MainPackage ||
                            argument == other.SetupHelperPackage ||
                            argument == other.SetupControlPermission ||
                            argument == other.OperatorUri);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task KioskSetupRejectsCrossChannelBundleWithoutFallback()
    {
        var stable = RustyKioskProductContract.For(RustyKioskProductChannel.Stable);
        var labs = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
        var bundle = new RustyKioskBundle("main.apk", "helper.apk", "bundle", stable);
        var runner = new RecordingCommandRunner((_, arguments) =>
            new CommandResult("adb-test", arguments, 1, string.Empty, "must not dispatch", TimeSpan.Zero));
        var client = new AdbClient("adb-test", runner);

        Assert.Throws<InvalidDataException>(() => OperatorCommands.InstallRustyKiosk(
            "QUEST123",
            bundle,
            operatorConfirmed: true,
            product: labs));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.InstallRustyKioskAsync(
            "QUEST123",
            bundle,
            product: labs));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task MixedKioskApkPairFailsPreflightWithoutAnyAdbFallback()
    {
        var stable = RustyKioskProductContract.For(RustyKioskProductChannel.Stable);
        var labs = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"qfm-kiosk-mixed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var main = Path.Combine(tempRoot, RustyKioskContract.MainApkFileName);
        var helper = Path.Combine(tempRoot, RustyKioskContract.SetupHelperApkFileName);
        await File.WriteAllBytesAsync(main, [0x50, 0x4b, 0x03, 0x04]);
        await File.WriteAllBytesAsync(helper, [0x50, 0x4b, 0x03, 0x05]);
        var bundle = new RustyKioskBundle(
            main,
            helper,
            tempRoot,
            labs,
            new RustyKioskBundleApkCommitment(
                labs.MainPackage,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(main))).ToLowerInvariant(),
                new FileInfo(main).Length),
            new RustyKioskBundleApkCommitment(
                labs.SetupHelperPackage,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(helper))).ToLowerInvariant(),
                new FileInfo(helper).Length),
            RustyKioskContract.TrustedSignerSha256);
        var runner = new RecordingCommandRunner((fileName, arguments) =>
        {
            if (fileName == "aapt2-test")
            {
                var package = File.ReadAllBytes(arguments[^1])[^1] == 0x04
                    ? labs.MainPackage
                    : stable.SetupHelperPackage;
                return Success($"package: name='{package}' versionCode='60609'\n");
            }
            if (fileName == "apksigner-test")
            {
                return Success(
                    $"Signer #1 certificate SHA-256 digest: {RustyKioskContract.TrustedSignerSha256}\n");
            }
            return new CommandResult(fileName, arguments, 1, string.Empty, "must not dispatch", TimeSpan.Zero);
        });
        var client = new AdbClient(
            "adb-test",
            runner,
            new AndroidBuildToolPaths("aapt2-test", "apksigner-test"));

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.InstallRustyKioskAsync(
                "QUEST123",
                bundle,
                product: labs));
            Assert.DoesNotContain(runner.Calls, static call => call.FileName == "adb-test");
            Assert.Equal(4, runner.Calls.Count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CrossSignedKioskPairFailsPreflightWithoutAnyAdbCall()
    {
        var labs = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"qfm-kiosk-cross-signed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var main = Path.Combine(tempRoot, RustyKioskContract.MainApkFileName);
        var helper = Path.Combine(tempRoot, RustyKioskContract.SetupHelperApkFileName);
        await File.WriteAllBytesAsync(main, [0x50, 0x4b, 0x03, 0x04]);
        await File.WriteAllBytesAsync(helper, [0x50, 0x4b, 0x03, 0x05]);
        var bundle = CreateCommittedKioskBundle(main, helper, tempRoot, labs);
        var runner = new RecordingCommandRunner((fileName, arguments) =>
        {
            if (fileName == "aapt2-test")
            {
                var package = File.ReadAllBytes(arguments[^1])[^1] == 0x04
                    ? labs.MainPackage
                    : labs.SetupHelperPackage;
                return Success($"package: name='{package}' versionCode='60609'\n");
            }
            if (fileName == "apksigner-test")
            {
                var signer = File.ReadAllBytes(arguments[^1])[^1] == 0x04
                    ? RustyKioskContract.TrustedSignerSha256
                    : new string('b', 64);
                return Success($"Signer #1 certificate SHA-256 digest: {signer}\n");
            }
            return new CommandResult(fileName, arguments, 1, string.Empty, "must not dispatch", TimeSpan.Zero);
        });
        var client = new AdbClient(
            "adb-test",
            runner,
            new AndroidBuildToolPaths("aapt2-test", "apksigner-test"));

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.InstallRustyKioskAsync(
                "QUEST123",
                bundle,
                product: labs));
            Assert.DoesNotContain(runner.Calls, static call => call.FileName == "adb-test");
            Assert.Equal(4, runner.Calls.Count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CryptographicallyInvalidKioskApkFailsBeforeAnyAdbCall()
    {
        var labs = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"qfm-kiosk-invalid-signature-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var main = Path.Combine(tempRoot, RustyKioskContract.MainApkFileName);
        var helper = Path.Combine(tempRoot, RustyKioskContract.SetupHelperApkFileName);
        await File.WriteAllBytesAsync(main, [0x50, 0x4b, 0x03, 0x04]);
        await File.WriteAllBytesAsync(helper, [0x50, 0x4b, 0x03, 0x05]);
        var bundle = CreateCommittedKioskBundle(main, helper, tempRoot, labs);
        var runner = new RecordingCommandRunner((fileName, arguments) =>
        {
            if (fileName == "aapt2-test")
            {
                return Success($"package: name='{labs.MainPackage}' versionCode='60609'\n");
            }
            if (fileName == "apksigner-test")
            {
                return new CommandResult(
                    fileName,
                    arguments,
                    1,
                    string.Empty,
                    "DOES NOT VERIFY",
                    TimeSpan.Zero);
            }
            return new CommandResult(fileName, arguments, 1, string.Empty, "must not dispatch", TimeSpan.Zero);
        });
        var client = new AdbClient(
            "adb-test",
            runner,
            new AndroidBuildToolPaths("aapt2-test", "apksigner-test"));

        try
        {
            await Assert.ThrowsAsync<AdbCommandException>(() => client.InstallRustyKioskAsync(
                "QUEST123",
                bundle,
                product: labs));
            Assert.DoesNotContain(runner.Calls, static call => call.FileName == "adb-test");
            Assert.Equal(2, runner.Calls.Count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "ExternalFixture")]
    public async Task ReleaseKioskBundleCryptographicAdmissionMatchesPinnedIdentityWhenConfigured()
    {
        var bundleDirectory = Environment.GetEnvironmentVariable("QFM_KIOSK_RELEASE_FIXTURE_DIR");
        if (string.IsNullOrWhiteSpace(bundleDirectory))
        {
            return;
        }

        var bundle = RustyKioskBundle.FromDirectory(bundleDirectory);
        var labs = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
        using var immutable = await ImmutableApkAdmission.CreateManyAsync(
            [bundle.MainApkPath, bundle.SetupHelperApkPath],
            CancellationToken.None);
        var adb = Path.Combine(
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ??
            Environment.GetEnvironmentVariable("ANDROID_HOME") ??
                throw new InvalidOperationException(
                    "ANDROID_SDK_ROOT or ANDROID_HOME is required for the external fixture gate."),
            "platform-tools",
            "adb.exe");
        var inspector = new ApkArtifactInspector(
            new CommandRunner(),
            AndroidBuildToolPaths.FindFromAdb(adb));
        var main = await inspector.InspectAsync(immutable.Paths[0]);
        var helper = await inspector.InspectAsync(immutable.Paths[1]);
        var admission = bundle.AcquireAdmission(labs, main, helper);

        Assert.Equal(bundle.MainCommitment!.Sha256, main.Sha256);
        Assert.Equal(bundle.SetupHelperCommitment!.Sha256, helper.Sha256);
        Assert.Equal(labs.MainPackage, main.Identity.PackageName);
        Assert.Equal(labs.SetupHelperPackage, helper.Identity.PackageName);
        Assert.Equal(RustyKioskContract.TrustedSignerSha256, main.Identity.SignerSha256);
        Assert.Equal(RustyKioskContract.TrustedSignerSha256, helper.Identity.SignerSha256);
        Assert.Equal(main.Path, admission.MainApkPath);
        Assert.Equal(helper.Path, admission.SetupHelperApkPath);
        Assert.NotEqual(bundle.MainApkPath, admission.MainApkPath);
        Assert.NotEqual(bundle.SetupHelperApkPath, admission.SetupHelperApkPath);
    }

    [Fact]
    public void KioskSetupCliProductChannelIsRequiredStrictAndHasNoDefaultFallback()
    {
        Assert.Equal(
            RustyKioskProductChannel.Stable,
            OperatorCommands.ParseRequiredKioskSetupProductChannel(
                ["kiosk", "install", "--product-channel", "stable"]).Channel);
        Assert.Equal(
            RustyKioskProductChannel.Labs,
            OperatorCommands.ParseRequiredKioskSetupProductChannel(
                ["kiosk", "provision", "--product-channel", "labs"]).Channel);
        Assert.Throws<ArgumentException>(() =>
            OperatorCommands.ParseRequiredKioskSetupProductChannel(
                ["kiosk", "install"]));
        Assert.Throws<ArgumentException>(() =>
            OperatorCommands.ParseRequiredKioskSetupProductChannel(
                ["kiosk", "install", "--product-channel", "Labs"]));
        Assert.Throws<ArgumentException>(() =>
            OperatorCommands.ParseRequiredKioskSetupProductChannel(
                [
                    "kiosk", "install",
                    "--product-channel", "labs",
                    "--product-channel", "stable"
                ]));
    }

    [Fact]
    public void KioskSetupFactoriesPreserveStableDefaultAndExactLabsCliVectors()
    {
        var labs = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
        var stable = RustyKioskProductContract.For(RustyKioskProductChannel.Stable);
        var stableBundle = new RustyKioskBundle("main.apk", "helper.apk", "bundle");
        var labsBundle = new RustyKioskBundle(
            "main.apk",
            "helper.apk",
            "bundle",
            labs,
            new RustyKioskBundleApkCommitment(labs.MainPackage, new string('a', 64), 1),
            new RustyKioskBundleApkCommitment(labs.SetupHelperPackage, new string('b', 64), 1),
            RustyKioskContract.TrustedSignerSha256);
        var stableInstall = OperatorCommands.InstallRustyKiosk(
            "QUEST123",
            stableBundle,
            operatorConfirmed: true);
        var labsInstall = OperatorCommands.InstallRustyKiosk(
            "QUEST123",
            labsBundle,
            operatorConfirmed: true,
            product: labs);
        var labsProvision = OperatorCommands.ProvisionRustyKiosk(
            "QUEST123",
            operatorConfirmed: true,
            product: labs);

        Assert.Equal(stable, stableInstall.RustyKioskProduct);
        var stableChannelIndex = stableInstall.CliArguments.ToList().IndexOf("--product-channel");
        Assert.True(stableChannelIndex >= 0);
        Assert.Equal("stable", stableInstall.CliArguments[stableChannelIndex + 1]);
        Assert.Equal(labs, labsInstall.RustyKioskProduct);
        Assert.Equal(labs, labsProvision.RustyKioskProduct);
        Assert.Contains("--product-channel", labsInstall.CliArguments);
        Assert.Contains("labs", labsInstall.CliArguments);
        Assert.Contains("--product-channel", labsProvision.CliArguments);
        Assert.Contains("labs", labsProvision.CliArguments);
    }

    [Fact]
    public async Task BundledLabsManifestSelectsExactLabsIdentityAndRejectsStableSelection()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"qfm-kiosk-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(tempRoot, RustyKioskContract.MainApkFileName),
            [0x50, 0x4b, 0x03, 0x04]);
        await File.WriteAllBytesAsync(
            Path.Combine(tempRoot, RustyKioskContract.SetupHelperApkFileName),
            [0x50, 0x4b, 0x03, 0x05]);
        var mainPath = Path.Combine(tempRoot, RustyKioskContract.MainApkFileName);
        var helperPath = Path.Combine(tempRoot, RustyKioskContract.SetupHelperApkFileName);
        var mainSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(mainPath))).ToLowerInvariant();
        var helperSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(helperPath))).ToLowerInvariant();
        await File.WriteAllTextAsync(
            Path.Combine(tempRoot, "bundle-manifest.json"),
            $$"""
            {
              "schema": "meta.quest.file_manager.rusty_kiosk_bundle.v2",
              "product_channel": "labs",
              "signer_sha256": "{{RustyKioskContract.TrustedSignerSha256}}",
              "files": [
                {
                  "name": "rusty-kiosk.apk",
                  "package_name": "io.github.mesmerprism.rustykiosk.labs",
                  "sha256": "{{mainSha}}",
                  "bytes": {{new FileInfo(mainPath).Length}}
                },
                {
                  "name": "rusty-kiosk-setup-helper.apk",
                  "package_name": "io.github.mesmerprism.rustykiosk.setuphelper.labs",
                  "sha256": "{{helperSha}}",
                  "bytes": {{new FileInfo(helperPath).Length}}
                }
              ]
            }
            """);

        try
        {
            var bundle = RustyKioskBundle.FromDirectory(tempRoot);
            var labs = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
            var command = OperatorCommands.InstallRustyKiosk(
                "QUEST123",
                bundle,
                operatorConfirmed: true,
                product: bundle.DeclaredProduct);

            Assert.Equal(labs, bundle.DeclaredProduct);
            Assert.Equal(labs, command.RustyKioskProduct);
            Assert.Throws<InvalidDataException>(() => OperatorCommands.InstallRustyKiosk(
                "QUEST123",
                bundle,
                operatorConfirmed: true));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitKioskBundleErrorsNeverFallThroughToAnotherCandidate()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"qfm-missing-kiosk-{Guid.NewGuid():N}");
        Assert.Throws<DirectoryNotFoundException>(() =>
            RustyKioskBundleLocator.ResolveRequiredForSetup(missing));

        var malformed = Path.Combine(Path.GetTempPath(), $"qfm-malformed-kiosk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(malformed);
        await File.WriteAllBytesAsync(
            Path.Combine(malformed, RustyKioskContract.MainApkFileName),
            [0x50, 0x4b, 0x03, 0x04]);
        await File.WriteAllBytesAsync(
            Path.Combine(malformed, RustyKioskContract.SetupHelperApkFileName),
            [0x50, 0x4b, 0x03, 0x05]);
        await File.WriteAllTextAsync(
            Path.Combine(malformed, "bundle-manifest.json"),
            "{\"product_channel\":\"labs\"}");
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                RustyKioskBundleLocator.ResolveRequiredForSetup(malformed));
        }
        finally
        {
            Directory.Delete(malformed, recursive: true);
        }
    }

    [Fact]
    public async Task KioskBundleManifestBoundsRejectOversizedBytesAndExcessiveFileRows()
    {
        static async Task SeedApksAsync(string root)
        {
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(
                Path.Combine(root, RustyKioskContract.MainApkFileName),
                [0x50, 0x4b, 0x03, 0x04]);
            await File.WriteAllBytesAsync(
                Path.Combine(root, RustyKioskContract.SetupHelperApkFileName),
                [0x50, 0x4b, 0x03, 0x05]);
        }

        var oversized = Path.Combine(Path.GetTempPath(), $"qfm-kiosk-oversized-{Guid.NewGuid():N}");
        var excessive = Path.Combine(Path.GetTempPath(), $"qfm-kiosk-excessive-{Guid.NewGuid():N}");
        await SeedApksAsync(oversized);
        await SeedApksAsync(excessive);
        await File.WriteAllBytesAsync(
            Path.Combine(oversized, "bundle-manifest.json"),
            new byte[512 * 1024 + 1]);
        await File.WriteAllTextAsync(
            Path.Combine(excessive, "bundle-manifest.json"),
            JsonSerializer.Serialize(new
            {
                schema = "meta.quest.file_manager.rusty_kiosk_bundle.v2",
                product_channel = "labs",
                signer_sha256 = RustyKioskContract.TrustedSignerSha256,
                files = Enumerable.Range(0, 65).Select(index => new
                {
                    name = $"extra-{index}.txt"
                })
            }));

        try
        {
            var oversizedError = Assert.Throws<InvalidDataException>(() =>
                RustyKioskBundle.FromDirectory(oversized));
            Assert.Contains("2..524288 bytes", oversizedError.Message, StringComparison.Ordinal);
            var excessiveError = Assert.Throws<InvalidDataException>(() =>
                RustyKioskBundle.FromDirectory(excessive));
            Assert.Contains("at most 64 file rows", excessiveError.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(oversized, recursive: true);
            Directory.Delete(excessive, recursive: true);
        }
    }

    [Theory]
    [InlineData(RustyKioskProductChannel.Stable)]
    [InlineData(RustyKioskProductChannel.Labs)]
    public async Task AdbStatusAndCommandUseOnlyTheSelectedProductIdentity(
        RustyKioskProductChannel channel)
    {
        var product = RustyKioskProductContract.For(channel);
        var other = RustyKioskProductContract.For(
            channel == RustyKioskProductChannel.Stable
                ? RustyKioskProductChannel.Labs
                : RustyKioskProductChannel.Stable);
        string? requestId = null;
        var runner = new RecordingCommandRunner((_, arguments) =>
        {
            if (arguments.Contains("dumpsys", StringComparer.Ordinal) &&
                arguments.Contains("package", StringComparer.Ordinal))
            {
                var package = arguments.Last();
                var permission = package == product.MainPackage
                    ? product.SetupControlPermission
                    : RustyKioskContract.WriteSecureSettingsPermission;
                return Success($"Package [{package}]\nversionName=0.6.7\n  {permission}: granted=true\n");
            }
            if (ProviderMethod(arguments) == "contract")
            {
                return Bundle(
                    $"accepted=true, completed=true, schema={RustyKioskContract.HostOperatorSuccessorSchema}, " +
                    $"package={product.MainPackage}, product_channel={product.WireName}");
            }
            if (ProviderMethod(arguments) == "invoke")
            {
                requestId = Extra(arguments, "request_id:s:");
                return Bundle("accepted=true, completed=true, message=accepted");
            }
            if (arguments.Contains("am", StringComparer.Ordinal) &&
                arguments.Contains("start", StringComparer.Ordinal))
            {
                return Success("Status: ok\n");
            }
            if (ProviderMethod(arguments) == "result")
            {
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    ResultJson(requestId!, "show-apps", wifiAdbEnabled: false)));
                return Bundle($"accepted=true, completed=true, result_base64={encoded}");
            }
            return new CommandResult("adb-test", arguments, 1, string.Empty, "unexpected call", TimeSpan.Zero);
        });
        var client = new AdbClient("adb-test", runner);

        var status = await client.GetRustyKioskInstallationStatusAsync("QUEST123", product);
        var result = await client.InvokeRustyKioskAsync(
            "QUEST123",
            RustyKioskCommand.ShowApps,
            product: product);

        Assert.True(status.MainInstalled);
        Assert.True(status.SetupHelperInstalled);
        Assert.True(status.HostOperatorAvailable);
        Assert.Equal(RustyKioskCommand.ShowApps, result.Command);
        Assert.Contains(runner.Calls, call => call.Arguments.Contains(product.MainActivity));
        Assert.All(
            runner.Calls.Where(call => call.Arguments.Contains("content")),
            call => Assert.Contains(product.OperatorUri, call.Arguments));
        Assert.DoesNotContain(
            runner.Calls.SelectMany(static call => call.Arguments),
            argument => argument == other.OperatorUri ||
                        argument == other.MainPackage ||
                        argument == other.SetupHelperPackage ||
                        argument == other.MainActivity);
    }

    [Fact]
    public async Task AdbCommandRejectsCompletedResultForCrossedTypedCommand()
    {
        var product = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
        string? requestId = null;
        var runner = new RecordingCommandRunner((_, arguments) =>
        {
            if (ProviderMethod(arguments) == "invoke")
            {
                requestId = Extra(arguments, "request_id:s:");
                return Bundle("accepted=true, completed=true, message=accepted");
            }
            if (arguments.Contains("am", StringComparer.Ordinal))
            {
                return Success("Status: ok\n");
            }
            if (ProviderMethod(arguments) == "result")
            {
                var crossed = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    ResultJson(requestId!, "show-controls", wifiAdbEnabled: false)));
                return Bundle($"accepted=true, completed=true, result_base64={crossed}");
            }
            return new CommandResult("adb-test", arguments, 1, string.Empty, "unexpected call", TimeSpan.Zero);
        });
        var client = new AdbClient("adb-test", runner);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.InvokeRustyKioskAsync(
                "QUEST123",
                RustyKioskCommand.ShowApps,
                product: product));

        Assert.Contains("typed command", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RustyKioskCommand.FocusSearch)]
    [InlineData(RustyKioskCommand.FocusTagEditor)]
    public void AcceptedFocusCommandsMapPendingReceiptToCliExitThree(RustyKioskCommand command)
    {
        var result = RustyKioskOperatorResult.Parse(ResultJson(
            "pc-focus",
            command.ToWireName(),
            wifiAdbEnabled: false,
            completed: false));
        var receipt = new OperatorMutationReceipt(
            "pc-focus",
            OperatorCommandKind.InvokeRustyKiosk,
            "QUEST123",
            command.ToWireName(),
            OperatorMutationStage.Pending,
            "Keyboard focus cannot be confirmed remotely.",
            HeadsetReadback: true,
            [new OperatorMutationTransition(
                OperatorMutationStage.Pending,
                DateTimeOffset.UtcNow,
                "Pending wearer confirmation")]);

        Assert.True(result.Accepted);
        Assert.False(result.Completed);
        Assert.False(RustyKioskReadback.Confirms(command, null, result));
        Assert.Equal(3, RustyKioskCliExitCodes.For(receipt, accepted: true));
    }

    [Fact]
    public void OperatorResultParsesLaunchOptionStateAndConfirmsTypedReadback()
    {
        var result = RustyKioskOperatorResult.Parse(LaunchOptionResultJson());

        Assert.Equal(RustyKioskLaunchRequirement.WifiOn, result.State.Entries.Single().LaunchRequirement);
        Assert.Equal(RustyKioskLaunchRequirement.WifiOn, result.State.SelectedLaunchRequirement);
        Assert.Equal(RustyKioskPassthroughStyle.ContourLut, result.State.PassthroughStyle);
        Assert.True(result.State.ControlsOpen);
        Assert.False(result.State.PendingRequirementLaunch);
        Assert.True(result.State.SystemPassthroughEnabled);
        Assert.True(result.State.PassthroughLutApplied);
        var option = Assert.Single(result.State.SelectedLaunchOptions!);
        Assert.Equal(1, option.SchemaVersion);
        Assert.Equal("playlist.example-1", option.OptionId);
        Assert.Equal("Example playlist", option.DisplayLabel);
        Assert.Equal("Loop two profiles", option.Description);
        Assert.Equal(RustyKioskLaunchOptionsStatus.Ready, result.State.SelectedLaunchOptionsStatus);
        Assert.NotNull(result.State.SelectedLaunchOptionsBinding);
        Assert.Equal("com.example.installed", result.State.SelectedLaunchOptionsBinding.PackageName);
        Assert.Equal(10123, result.State.SelectedLaunchOptionsBinding.Uid);
        Assert.Equal(new string('a', 64), result.State.SelectedLaunchOptionsBinding.SigningIdentity);
        Assert.Equal("34150cba691aeaa0865603729e672ed4e7cce2a94656c4eea38e18edbde1cbdf", result.State.SelectedLaunchOptionsBinding.BindingSha256);
        Assert.Equal("playlist.example-1", result.State.LastDispatchedOptionId);
        Assert.Equal("com.example.installed", result.State.LastDispatchedOptionPackage);
        Assert.True(RustyKioskReadback.Confirms(
            RustyKioskCommand.SetLaunchRequirement,
            "wifi-on",
            result));
        Assert.True(RustyKioskReadback.Confirms(
            RustyKioskCommand.ShowControls,
            null,
            result with { Command = RustyKioskCommand.ShowControls }));
        Assert.True(RustyKioskReadback.Confirms(
            RustyKioskCommand.CancelPendingLaunch,
            null,
            result with { Command = RustyKioskCommand.CancelPendingLaunch }));
        Assert.True(RustyKioskReadback.Confirms(
            RustyKioskCommand.PassthroughContour,
            null,
            result with { Command = RustyKioskCommand.PassthroughContour }));
        Assert.False(RustyKioskReadback.Confirms(
            RustyKioskCommand.PassthroughNatural,
            null,
            result with { Command = RustyKioskCommand.PassthroughNatural }));
        Assert.False(RustyKioskReadback.Confirms(
            RustyKioskCommand.ShowApps,
            null,
            result with { Command = RustyKioskCommand.ShowControls }));
        Assert.True(RustyKioskReadback.Confirms(
            RustyKioskCommand.LaunchOption,
            "playlist.example-1",
            result with { Command = RustyKioskCommand.LaunchOption }));
        Assert.False(RustyKioskReadback.Confirms(
            RustyKioskCommand.LaunchOption,
            "playlist.other",
            result with { Command = RustyKioskCommand.LaunchOption }));
        Assert.False(RustyKioskReadback.Confirms(
            RustyKioskCommand.LaunchOption,
            "playlist.example-1",
            result with
            {
                Command = RustyKioskCommand.LaunchOption,
                State = result.State with { LastDispatchedOptionPackage = "com.example.other" }
            }));
    }

    [Fact]
    public void OperatorResultRejectsUnknownLaunchRequirement()
    {
        var json = LaunchOptionResultJson().Replace(
            "\"launch_requirement\": \"wifi-on\"",
            "\"launch_requirement\": \"bluetooth-on\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => RustyKioskOperatorResult.Parse(json));
    }

    [Fact]
    public void OperatorResultRejectsMalformedOrDuplicateLaunchOptions()
    {
        var wrongSchema = LaunchOptionResultJson().Replace(
            "\"schema_version\": 1",
            "\"schema_version\": 2",
            StringComparison.Ordinal);
        var oversizedId = LaunchOptionResultJson().Replace(
            "playlist.example-1",
            new string('x', RustyKioskContract.MaxCommandValueLength + 1),
            StringComparison.Ordinal);
        var unknownStatus = LaunchOptionResultJson().Replace(
            "\"selected_launch_options_status\": \"ready\"",
            "\"selected_launch_options_status\": \"maybe\"",
            StringComparison.Ordinal);
        var mismatchedBinding = LaunchOptionResultJson().Replace(
            "\"selected_launch_options_package\": \"com.example.installed\"",
            "\"selected_launch_options_package\": \"com.example.other\"",
            StringComparison.Ordinal);
        var invalidBindingDigest = LaunchOptionResultJson().Replace(
            "34150cba691aeaa0865603729e672ed4e7cce2a94656c4eea38e18edbde1cbdf",
            new string('B', 64),
            StringComparison.Ordinal);
        var incompleteBinding = LaunchOptionResultJson().Replace(
            "\"selected_launch_options_uid\": 10123",
            "\"selected_launch_options_uid\": null",
            StringComparison.Ordinal);
        var duplicateNode = JsonNode.Parse(LaunchOptionResultJson())!;
        var duplicateOptions = duplicateNode["state"]!["selected_launch_options"]!.AsArray();
        duplicateOptions.Add(JsonNode.Parse(duplicateOptions[0]!.ToJsonString()));
        var duplicate = duplicateNode.ToJsonString();

        Assert.Throws<InvalidDataException>(() => RustyKioskOperatorResult.Parse(wrongSchema));
        Assert.Throws<InvalidDataException>(() => RustyKioskOperatorResult.Parse(oversizedId));
        Assert.Throws<InvalidDataException>(() => RustyKioskOperatorResult.Parse(unknownStatus));
        Assert.Throws<InvalidDataException>(() => RustyKioskOperatorResult.Parse(mismatchedBinding));
        Assert.Throws<InvalidDataException>(() => RustyKioskOperatorResult.Parse(invalidBindingDigest));
        Assert.Throws<InvalidDataException>(() => RustyKioskOperatorResult.Parse(incompleteBinding));
        Assert.Throws<InvalidDataException>(() => RustyKioskOperatorResult.Parse(duplicate));
    }
    [Fact]
    public void OperatorResultPreservesCompleteCatalogIncludingNamedMissingApps()
    {
        var result = RustyKioskOperatorResult.Parse(ResultJson(
            requestId: "pc-test",
            command: "status",
            wifiAdbEnabled: false));

        Assert.Equal(2, result.State.Entries.Count);
        var missing = Assert.Single(result.State.Entries, static entry => !entry.Installed);
        Assert.Equal("Purchased Example", missing.Name);
        Assert.Null(missing.PackageName);
        Assert.Equal(["calm", "paid"], missing.Tags);
        Assert.Equal("Not installed", missing.StatusLabel);
        Assert.Contains("paid", result.State.Tags);
    }

    [Fact]
    public void CatalogFilterMatchesSeparatorTolerantTermsAcrossFields()
    {
        RustyKioskAppEntry[] entries =
        [
            new("motion", "Motion App", "com.example.motion", true, true, ["movement"]),
            new("quiet", "Quiet App", "com.example.quiet", false, false, ["calm"])
        ];

        Assert.Equal("Motion App", Assert.Single(
            RustyKioskCatalogFilter.Apply(entries, "example-motion", null)).Name);
        Assert.Equal("Quiet App", Assert.Single(
            RustyKioskCatalogFilter.Apply(entries, "quiet/calm", null)).Name);
        Assert.Equal("Motion App", Assert.Single(
            RustyKioskCatalogFilter.Apply(entries, "example/movement", "movement")).Name);
        Assert.Empty(RustyKioskCatalogFilter.Apply(entries, "motion/calm", null));
        Assert.Equal(["Motion App", "Quiet App"],
            RustyKioskCatalogFilter.Apply(entries, "---", null).Select(static entry => entry.Name));
    }

    [Fact]
    public void WifiPermissionRequestStaysPendingUntilLaterHeadsetReadbackConfirmsIt()
    {
        var command = OperatorCommands.InvokeRustyKiosk(
            "QUEST123",
            RustyKioskCommand.RequestWifiAdb,
            operatorConfirmed: true);
        var now = DateTimeOffset.UtcNow;
        var pending = new OperatorMutationReceipt(
            "pc-test",
            command.Kind,
            "QUEST123",
            "Rusty Kiosk request-wifi-adb",
            OperatorMutationStage.Pending,
            "Wi-Fi ADB=off",
            HeadsetReadback: true,
            [
                new OperatorMutationTransition(
                    OperatorMutationStage.Sent,
                    now,
                    "Sent"),
                new OperatorMutationTransition(
                    OperatorMutationStage.Pending,
                    now,
                    "Waiting for Meta wearer approval")
            ]);
        var stillOff = new OperatorExecutionResult(
            OperatorCommands.InspectRustyKiosk("QUEST123"),
            RustyKioskOperatorResult: RustyKioskOperatorResult.Parse(ResultJson(
                "status-off",
                "status",
                wifiAdbEnabled: false)));
        var nowOn = new OperatorExecutionResult(
            OperatorCommands.InspectRustyKiosk("QUEST123"),
            RustyKioskOperatorResult: RustyKioskOperatorResult.Parse(ResultJson(
                "status-on",
                "status",
                wifiAdbEnabled: true)));

        var unchanged = OperatorMutationReconciler.Reconcile(pending, command, stillOff);
        var confirmed = OperatorMutationReconciler.Reconcile(unchanged, command, nowOn);

        Assert.Equal(OperatorMutationStage.Pending, unchanged.Stage);
        Assert.Equal(OperatorMutationStage.Confirmed, confirmed.Stage);
        Assert.True(confirmed.HeadsetReadback);
        Assert.Equal(
            [
                OperatorMutationStage.Sent,
                OperatorMutationStage.Pending,
                OperatorMutationStage.Pending,
                OperatorMutationStage.Confirmed
            ],
            confirmed.Transitions.Select(static transition => transition.Stage));
    }

    [Fact]
    public void StatusReconciliationRequiresExactSerialAndCanonicalProduct()
    {
        var labs = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
        var stable = RustyKioskProductContract.For(RustyKioskProductChannel.Stable);
        var original = OperatorCommands.InvokeRustyKiosk(
            "QUEST123",
            RustyKioskCommand.RequestWifiAdb,
            operatorConfirmed: true,
            product: labs);
        var pending = new OperatorMutationReceipt(
            "pc-status-bind",
            original.Kind,
            "QUEST123",
            "Rusty Kiosk request-wifi-adb",
            OperatorMutationStage.Pending,
            "Wi-Fi ADB=off",
            HeadsetReadback: true,
            [new OperatorMutationTransition(
                OperatorMutationStage.Pending,
                DateTimeOffset.UtcNow,
                "Waiting for matching status")]);
        var status = RustyKioskOperatorResult.Parse(ResultJson(
            "status-bound",
            "status",
            wifiAdbEnabled: true));
        var sameTarget = new OperatorExecutionResult(
            OperatorCommands.InspectRustyKiosk("QUEST123", labs),
            RustyKioskOperatorResult: status);
        var crossedSerial = new OperatorExecutionResult(
            OperatorCommands.InspectRustyKiosk("QUEST999", labs),
            RustyKioskOperatorResult: status);
        var crossedChannel = new OperatorExecutionResult(
            OperatorCommands.InspectRustyKiosk("QUEST123", stable),
            RustyKioskOperatorResult: status);
        var untypedStatus = new OperatorExecutionResult(
            new OperatorCommand(
                OperatorCommandKind.InspectRustyKiosk,
                ["kiosk", "status"],
                serial: "QUEST123",
                rustyKioskProduct: labs),
            RustyKioskOperatorResult: status);

        Assert.Equal(
            OperatorMutationStage.Confirmed,
            OperatorMutationReconciler.Reconcile(pending, original, sameTarget).Stage);
        Assert.Equal(
            OperatorMutationStage.Pending,
            OperatorMutationReconciler.Reconcile(pending, original, crossedSerial).Stage);
        Assert.Equal(
            OperatorMutationStage.Pending,
            OperatorMutationReconciler.Reconcile(pending, original, crossedChannel).Stage);
        Assert.Equal(
            OperatorMutationStage.Pending,
            OperatorMutationReconciler.Reconcile(pending, original, untypedStatus).Stage);
        Assert.False(RustyKioskReadback.Confirms(
            RustyKioskCommand.RequestWifiAdb,
            value: null,
            status));
    }

    [Fact]
    public async Task PerformanceMutationRecordsSentPendingConfirmedOnlyAfterGetPropReadback()
    {
        var runner = new RecordingCommandRunner((_, arguments) =>
        {
            if (arguments.Contains("dumpsys", StringComparer.Ordinal) &&
                arguments.Contains("battery", StringComparer.Ordinal))
            {
                return Success("level: 71\nstatus: 3\n");
            }

            if (arguments.Contains("dumpsys", StringComparer.Ordinal) &&
                arguments.Contains("power", StringComparer.Ordinal))
            {
                return Success("mWakefulness=Awake\nmInteractive=true\nDisplay Power: state=ON\n");
            }

            if (arguments.Contains("vrpowermanager", StringComparer.Ordinal))
            {
                return Success("Virtual proximity state: OPEN\n");
            }

            if (arguments.Contains("debug.oculus.cpuLevel", StringComparer.Ordinal))
            {
                return Success("3\n");
            }

            if (arguments.Contains("debug.oculus.gpuLevel", StringComparer.Ordinal))
            {
                return Success("4\n");
            }

            return Success(string.Empty);
        });
        var executor = new OperatorCommandExecutor(new AdbClient("adb-test", runner));
        var progress = new RecordingProgress<OperatorProgress>();

        var execution = await executor.ExecuteAsync(
            OperatorCommands.SetQuestPerformance(
                "QUEST123",
                cpuLevel: 3,
                gpuLevel: 4,
                operatorConfirmed: true),
            progress: progress);

        var receipt = Assert.IsType<OperatorMutationReceipt>(execution.MutationReceipt);
        Assert.Equal(OperatorMutationStage.Confirmed, receipt.Stage);
        Assert.True(receipt.HeadsetReadback);
        Assert.Equal(
            [OperatorMutationStage.Sent, OperatorMutationStage.Pending, OperatorMutationStage.Confirmed],
            receipt.Transitions.Select(static transition => transition.Stage));
        Assert.Contains(
            runner.Calls,
            static call => call.Arguments.Any(argument => argument.Contains("setprop", StringComparison.Ordinal)));
        Assert.Contains(runner.Calls, static call => call.Arguments.Contains("getprop", StringComparer.Ordinal));
        Assert.Contains(progress.Values, static value => value.Stage == "mutation-sent");
        Assert.Contains(progress.Values, static value => value.Stage == "mutation-pending");
        Assert.Contains(progress.Values, static value => value.Stage == "mutation-confirmed");
    }

    [Fact]
    public void MountedProximityDoesNotMasqueradeAsKeepAwake()
    {
        var normal = QuestControlParser.Parse(
            "level: 75\nstatus: 3\n",
            string.Empty,
            "mWakefulness=Awake\nmStayOn=false\n",
            "Virtual proximity state: CLOSE\nisAutosleepDisabled: false\nState: HEADSET_MOUNTED\n",
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow);
        var held = QuestControlParser.Parse(
            "level: 75\nstatus: 3\n",
            string.Empty,
            "mWakefulness=Awake\nmStayOn=true\n",
            "Virtual proximity state: CLOSE\nisAutosleepDisabled: true\nState: HEADSET_MOUNTED\n",
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow);

        Assert.False(normal.KeepAwakeActive);
        Assert.False(normal.StayOn);
        Assert.False(normal.AutoSleepDisabled);
        Assert.True(held.KeepAwakeActive);
        Assert.True(held.StayOn);
        Assert.True(held.AutoSleepDisabled);
    }

    [Fact]
    public async Task TagFileValidationAllowsNamedNotInstalledEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rusty-kiosk-tags-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schema": "rusty.kiosk.app_tags.v1",
              "apps": [
                { "name": "Purchased Example", "tags": ["paid", "calm"] }
              ]
            }
            """);

        try
        {
            var json = RustyKioskTagFile.ValidateAndRead(path);
            Assert.Contains("Purchased Example", json, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TagFileValidationAcceptsStrictV2ActiveRequirementAndRejectsUnknownValues()
    {
        var validPath = Path.Combine(Path.GetTempPath(), $"rusty-kiosk-tags-v2-{Guid.NewGuid():N}.json");
        var invalidPath = Path.Combine(Path.GetTempPath(), $"rusty-kiosk-tags-v2-bad-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            validPath,
            """
            {
              "schema": "rusty.kiosk.app_tags.v2",
              "apps": [
                {
                  "name": "Morphovision",
                  "package": "io.github.mesmerprism.rustyquest.spatial_camera_panel",
                  "tags": ["360"],
                  "requirements": ["wifi-on"]
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(
            invalidPath,
            """
            {
              "schema": "rusty.kiosk.app_tags.v2",
              "apps": [
                { "name": "Morphovision", "requirements": ["bluetooth-on"] }
              ]
            }
            """);

        try
        {
            Assert.Contains("wifi-on", RustyKioskTagFile.ValidateAndRead(validPath), StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => RustyKioskTagFile.ValidateAndRead(invalidPath));
        }
        finally
        {
            File.Delete(validPath);
            File.Delete(invalidPath);
        }
    }

    [Fact]
    public async Task TagTransferUsesBoundedProviderChunksAndShaInsteadOfRawAndroidDataPaths()
    {
        var entries = string.Join(
            ",\n",
            Enumerable.Range(0, 140).Select(index =>
                $$"""{ "name": "External App {{index:D3}}", "tags": ["group-{{index % 7}}"] }"""));
        var json = $$"""
            {
              "schema": "rusty.kiosk.app_tags.v1",
              "apps": [
                {{entries}}
              ]
            }
            """;
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.True(bytes.Length > 6 * 1024);
        var input = Path.Combine(Path.GetTempPath(), $"rusty-kiosk-input-{Guid.NewGuid():N}.json");
        var output = Path.Combine(Path.GetTempPath(), $"rusty-kiosk-output-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(input, bytes);
        using var received = new MemoryStream();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var runner = new RecordingCommandRunner((_, arguments) =>
        {
            var methodIndex = Array.IndexOf(arguments.ToArray(), "--method");
            var method = methodIndex >= 0 ? arguments[methodIndex + 1] : string.Empty;
            if (method == "tag-write-begin")
            {
                received.SetLength(0);
                return Bundle("accepted=true, completed=true, offset=0, message=ready");
            }

            if (method == "tag-write-chunk")
            {
                var offset = int.Parse(Extra(arguments, "offset:i:"), System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(received.Length, offset);
                var chunk = Convert.FromBase64String(Extra(arguments, "data_base64:s:"));
                received.Write(chunk);
                return Bundle($"accepted=true, completed=true, offset={received.Length}, message=accepted");
            }

            if (method == "tag-write-commit")
            {
                Assert.Equal(bytes, received.ToArray());
                return Bundle($"accepted=true, completed=true, offset={bytes.Length}, message=committed");
            }

            if (method == "tag-read")
            {
                var offset = int.Parse(Extra(arguments, "offset:i:"), System.Globalization.CultureInfo.InvariantCulture);
                var length = Math.Min(6 * 1024, bytes.Length - offset);
                var encoded = Convert.ToBase64String(bytes, offset, length);
                return Bundle(
                    $"accepted=true, completed=true, total_bytes={bytes.Length}, offset={offset}, " +
                    $"sha256={sha}, data_base64={encoded}, message=ready");
            }

            return new CommandResult("adb-test", arguments, 1, string.Empty, "unexpected method", TimeSpan.Zero);
        });
        var client = new AdbClient("adb-test", runner);

        try
        {
            var product = RustyKioskProductContract.For(RustyKioskProductChannel.Labs);
            await client.PushRustyKioskTagFileAsync("QUEST123", input, product: product);
            await client.PullRustyKioskTagFileAsync("QUEST123", output, product: product);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
            Assert.True(runner.Calls.Count(call => call.Arguments.Contains("tag-write-chunk")) >= 2);
            Assert.True(runner.Calls.Count(call => call.Arguments.Contains("tag-read")) >= 2);
            Assert.All(
                runner.Calls,
                static call =>
                {
                    Assert.Equal(["-s", "QUEST123"], call.Arguments.Take(2));
                    Assert.Contains(
                        RustyKioskProductContract.For(RustyKioskProductChannel.Labs).OperatorUri,
                        call.Arguments);
                    Assert.DoesNotContain(RustyKioskContract.OperatorUri, call.Arguments);
                    Assert.DoesNotContain("push", call.Arguments);
                    Assert.DoesNotContain(RustyKioskContract.TagFilePath, call.Arguments);
                });
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static string ResultJson(
        string requestId,
        string command,
        bool wifiAdbEnabled,
        bool completed = true) => $$"""
        {
          "schema": "rusty.kiosk.cli_result.v1",
          "request_id": "{{requestId}}",
          "command": "{{command}}",
          "accepted": true,
          "completed": {{completed.ToString().ToLowerInvariant()}},
          "message": "Dispatched",
          "state": {
            "installed_count": 1,
            "not_installed_count": 1,
            "visible_count": 2,
            "entries_truncated": false,
            "entries": [
              {
                "key": "package:com.example.installed",
                "name": "Installed Example",
                "package": "com.example.installed",
                "installed": true,
                "launchable": true,
                "tags": ["calm"]
              },
              {
                "key": "name:purchased example",
                "name": "Purchased Example",
                "package": null,
                "installed": false,
                "launchable": false,
                "tags": ["calm", "paid"]
              }
            ],
            "visible_entries_truncated": false,
            "visible_entries": [],
            "search": "",
            "tag_filter": null,
            "status_line": "Ready",
            "tag_file_path": "/sdcard/Android/data/io.github.mesmerprism.rustykiosk/files/tags/app-tags.v1.json",
            "selected_key": null,
            "selected_name": null,
            "selected_package": null,
            "selected_installed": false,
            "selected_launchable": false,
            "wifi_adb_enabled": {{wifiAdbEnabled.ToString().ToLowerInvariant()}},
            "setup_helper_installed": true,
            "setup_helper_ready": true,
            "request_wifi_adb_after_boot": false,
            "accessibility_enabled": false,
            "guard_armed": false,
            "operation_in_progress": null
          }
        }
        """;

    private static string LaunchOptionResultJson() =>
        """
        {
          "schema": "rusty.kiosk.cli_result.v1",
          "request_id": "pc-alpha7",
          "command": "set-launch-requirement",
          "accepted": true,
          "completed": true,
          "message": "Complete",
          "state": {
            "installed_count": 1,
            "not_installed_count": 0,
            "visible_count": 1,
            "visible_entries_truncated": false,
            "entries": [
              {
                "key": "package:com.example.installed",
                "name": "Installed Example",
                "package": "com.example.installed",
                "installed": true,
                "launchable": true,
                "tags": ["calm"],
                "launch_requirement": "wifi-on"
              }
            ],
            "search": "",
            "tag_filter": null,
            "selected_key": "package:com.example.installed",
            "selected_name": "Installed Example",
            "selected_package": "com.example.installed",
            "selected_installed": true,
            "selected_launchable": true,
            "wifi_adb_enabled": true,
            "setup_helper_installed": true,
            "setup_helper_ready": true,
            "request_wifi_adb_after_boot": false,
            "accessibility_enabled": true,
            "guard_armed": false,
            "operation_in_progress": null,
            "status_line": "Ready",
            "tag_file_path": "/sdcard/Android/data/io.github.mesmerprism.rustykiosk/files/tags/app-tags.v1.json",
            "search_focus_request": 7,
            "tag_focus_request": 9,
            "controls_open": true,
            "selected_launch_requirement": "wifi-on",
            "pending_requirement_launch": false,
            "pending_requirement_launch_id": null,
            "passthrough_style": "contour-lut",
            "system_passthrough_enabled": true,
            "passthrough_lut_applied": true,
            "selected_launch_options_status": "ready",
            "selected_launch_options_message": "One option is available.",
            "selected_launch_options": [
              {
                "schema_version": 1,
                "option_id": "playlist.example-1",
                "display_label": "Example playlist",
                "description": "Loop two profiles"
              }
            ],
            "selected_launch_options_package": "com.example.installed",
            "selected_launch_options_uid": 10123,
            "selected_launch_options_signing_identity": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "selected_launch_options_version_code": 7,
            "selected_launch_options_last_update_time_ms": 1785700000000,
            "selected_launch_options_provider_authority": "com.example.installed.app-launch-options",
            "selected_launch_options_provider_class": "com.example.installed.LaunchOptionsProvider",
            "selected_launch_options_owner_activity": "com.example.installed.MainActivity",
            "selected_launch_options_binding_sha256": "34150cba691aeaa0865603729e672ed4e7cce2a94656c4eea38e18edbde1cbdf",
            "last_dispatched_option_id": "playlist.example-1",
            "last_dispatched_option_package": "com.example.installed"
          }
        }
        """;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuestIonAbleFileManager.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static CommandResult Success(string output) =>
        new("adb-test", [], 0, output, string.Empty, TimeSpan.Zero);

    private static CommandResult Bundle(string values) =>
        Success($"Result: Bundle[{{{values}}}]\n");

    private static string Extra(IReadOnlyList<string> arguments, string prefix) =>
        arguments.First(argument => argument.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    private static string? ProviderMethod(IReadOnlyList<string> arguments)
    {
        var index = Array.IndexOf(arguments.ToArray(), "--method");
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    private static RustyKioskBundle CreateCommittedKioskBundle(
        string mainPath,
        string helperPath,
        string source,
        RustyKioskProductContract product)
    {
        static RustyKioskBundleApkCommitment Commit(string path, string packageName) =>
            new(
                packageName,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                new FileInfo(path).Length);

        return new RustyKioskBundle(
            mainPath,
            helperPath,
            source,
            product,
            Commit(mainPath, product.MainPackage),
            Commit(helperPath, product.SetupHelperPackage),
            RustyKioskContract.TrustedSignerSha256);
    }

    private sealed class RecordingCommandRunner(
        Func<string, IReadOnlyList<string>, CommandResult> handler) : ICommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            var result = handler(fileName, arguments);
            return Task.FromResult(result with { FileName = fileName, Arguments = arguments.ToArray() });
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}
