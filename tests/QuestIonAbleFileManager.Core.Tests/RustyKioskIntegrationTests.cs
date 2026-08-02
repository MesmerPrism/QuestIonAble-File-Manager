using QuestIonAbleFileManager.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class RustyKioskIntegrationTests
{
    [Fact]
    public void CommandVocabularyExactlyMatchesPinnedKioskAlpha7Contract()
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
        Assert.Null(RustyKioskCommand.SetSearch.ValidateValue("   "));
    }

    [Fact]
    public void Alpha7RequirementUsesTheTypedAdbAndDirectCommandFactories()
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
    [InlineData(RustyKioskProductChannel.Stable, "stable")]
    [InlineData(RustyKioskProductChannel.Labs, "labs")]
    public void AdbOperatorFactoriesPreserveExactProductChannel(
        RustyKioskProductChannel channel,
        string wireName)
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
    public void OperatorResultParsesAlpha7StateAndConfirmsTypedReadback()
    {
        var result = RustyKioskOperatorResult.Parse(Alpha7ResultJson());

        Assert.Equal(RustyKioskLaunchRequirement.WifiOn, result.State.Entries.Single().LaunchRequirement);
        Assert.Equal(RustyKioskLaunchRequirement.WifiOn, result.State.SelectedLaunchRequirement);
        Assert.Equal(RustyKioskPassthroughStyle.ContourLut, result.State.PassthroughStyle);
        Assert.True(result.State.ControlsOpen);
        Assert.False(result.State.PendingRequirementLaunch);
        Assert.True(result.State.SystemPassthroughEnabled);
        Assert.True(result.State.PassthroughLutApplied);
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
    }

    [Fact]
    public void OperatorResultRejectsUnknownAlpha7LaunchRequirement()
    {
        var json = Alpha7ResultJson().Replace(
            "\"launch_requirement\": \"wifi-on\"",
            "\"launch_requirement\": \"bluetooth-on\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => RustyKioskOperatorResult.Parse(json));
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
          "message": "Complete",
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

    private static string Alpha7ResultJson() =>
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
            "passthrough_lut_applied": true
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
