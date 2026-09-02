using System.Security.Cryptography;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class ApkPropertyContractTests
{
    private const string Serial = "QUEST123";
    private const string Package = "com.example.app";
    private static readonly string[] PropertyNames =
    [
        "debug.rustyquest.example.enabled",
        "debug.rustyquest.example.mode"
    ];

    [Fact]
    public void CommandsAreClosedAgentOnlyRoutesWithoutPropertyOrValueArguments()
    {
        using var fixtureDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "QuestIonAbleFileManager.Core.Tests",
            "Fixtures",
            "apk-property-contract.v1.json")));
        var fixtureRoot = fixtureDocument.RootElement;
        Assert.Equal(
            "questionable.file_manager.apk_property_contract_fixture.v1",
            fixtureRoot.GetProperty("schema").GetString());
        Assert.Equal(
            "questionable.file_manager.apk_property_snapshot.v1",
            fixtureRoot.GetProperty("snapshot_schema").GetString());
        Assert.False(fixtureRoot.GetProperty("caller_supplied_property_names").GetBoolean());
        Assert.False(fixtureRoot.GetProperty("automatic_retry").GetBoolean());

        var apk = Path.GetFullPath("example.apk");
        var manifest = Path.GetFullPath("properties.json");
        var snapshot = Path.GetFullPath("snapshot.json");
        var observe = OperatorCommands.ParseExactApkPropertyCliArguments(
            [
                "apk", "properties", "observe", "--serial", Serial,
                "--file", apk, "--manifest", manifest, "--output", snapshot, "--json"
            ]);
        Assert.Equal(OperatorCommandKind.ObserveExactApkProperties, observe.Kind);
        Assert.False(observe.OperatorConfirmed);

        var clear = OperatorCommands.ParseExactApkPropertyCliArguments(
            [
                "apk", "properties", "clear", "--serial", Serial,
                "--file", apk, "--manifest", manifest, "--snapshot", snapshot,
                "--confirm-exact-apk-property-mutation", "--json"
            ]);
        Assert.Equal(OperatorCommandKind.ClearExactApkProperties, clear.Kind);
        Assert.True(clear.OperatorConfirmed);
        Assert.Throws<InvalidOperationException>(() => OperatorCommands.ClearExactApkProperties(
            Serial, apk, manifest, snapshot));

        var rejected = new[]
        {
            new[] { "apk", "properties", "clear", "--serial", Serial, "--file", apk,
                "--manifest", manifest, "--snapshot", snapshot, "--json" },
            new[] { "apk", "properties", "clear", "--serial", Serial, "--file", apk,
                "--manifest", manifest, "--snapshot", snapshot,
                "--property", PropertyNames[0], "--confirm-exact-apk-property-mutation", "--json" },
            new[] { "apk", "properties", "restore", "--serial", Serial, "--file", apk,
                "--manifest", manifest, "--snapshot", snapshot, "--value", "true",
                "--confirm-exact-apk-property-mutation", "--json" },
            new[] { "apk", "properties", "observe", "--serial", Serial, "--file", apk,
                "--manifest", manifest, "--output", snapshot, "--json", "--adb", "adb.exe" }
        };
        foreach (var arguments in rejected)
            Assert.Throws<ArgumentException>(() =>
                OperatorCommands.ParseExactApkPropertyCliArguments(arguments));

        Assert.Contains(OperatorActionRegistry.AgentRoutes, route =>
            route.Id == "apk_property_observe" && !route.RequiresConfirmation);
        Assert.Contains(OperatorActionRegistry.AgentRoutes, route =>
            route.Id == "apk_property_clear" && route.RequiresConfirmation);
        Assert.Contains(OperatorActionRegistry.AgentRoutes, route =>
            route.Id == "apk_property_restore" && route.RequiresConfirmation);
        Assert.DoesNotContain(OperatorActionRegistry.Actions, action =>
            action.Id.Contains("properties", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObservationCreatesOneExactSnapshotAndNeverOverwrites()
    {
        using var fixture = await PropertyFixture.CreateAsync();
        var runner = fixture.Runner;
        var observation = await Client(runner).ObserveExactApkPropertiesAsync(
            Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot);

        Assert.Equal(Package, observation.Artifact.Identity.PackageName);
        Assert.Equal(2, observation.Manifest.PropertyCount);
        Assert.Equal(1, observation.SetPropertyCount);
        Assert.Equal(1, observation.UnsetPropertyCount);
        Assert.True(File.Exists(fixture.Snapshot));
        Assert.Equal(observation.Snapshot.SizeBytes, new FileInfo(fixture.Snapshot).Length);
        Assert.Equal(
            observation.Snapshot.Sha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Snapshot))).ToLowerInvariant());
        using var document = JsonDocument.Parse(File.ReadAllBytes(fixture.Snapshot));
        Assert.Equal(
            "questionable.file_manager.apk_property_snapshot.v1",
            document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(Serial, document.RootElement.GetProperty("serial").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("properties").GetArrayLength());
        var prior = File.ReadAllBytes(fixture.Snapshot);

        await Assert.ThrowsAsync<IOException>(() =>
            Client(runner).ObserveExactApkPropertiesAsync(
                Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot));
        Assert.Equal(prior, File.ReadAllBytes(fixture.Snapshot));
        Assert.Equal(0, runner.SetPropertyCalls);
    }

    [Fact]
    public async Task ClearRejectsStaleSnapshotBeforeReadyRecheckOrDispatch()
    {
        using var fixture = await PropertyFixture.CreateAsync();
        await Client(fixture.Runner).ObserveExactApkPropertiesAsync(
            Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot);
        var discoveryBefore = fixture.Runner.DiscoveryCalls;
        fixture.Runner.Values[PropertyNames[0]] = "changed";
        var progress = new List<OperatorProgress>();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OperatorCommandExecutor(Client(fixture.Runner)).ExecuteAsync(
                OperatorCommands.ClearExactApkProperties(
                    Serial,
                    fixture.Apk,
                    fixture.Manifest,
                    fixture.Snapshot,
                    operatorConfirmed: true),
                progress: new InlineProgress<OperatorProgress>(progress.Add)));

        Assert.Equal(0, fixture.Runner.SetPropertyCalls);
        Assert.Equal(discoveryBefore, fixture.Runner.DiscoveryCalls);
        Assert.DoesNotContain(progress, item =>
            item.Stage.StartsWith("mutation-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MutationRejectsUnreadyFinalDiscoveryAndOpenEndedSnapshotBeforeDispatch()
    {
        using var fixture = await PropertyFixture.CreateAsync();
        await Client(fixture.Runner).ObserveExactApkPropertiesAsync(
            Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot);
        fixture.Runner.Ready = false;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Client(fixture.Runner).ClearExactApkPropertiesAsync(
                Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot));
        Assert.Equal(0, fixture.Runner.SetPropertyCalls);

        fixture.Runner.Ready = true;
        var snapshotText = await File.ReadAllTextAsync(fixture.Snapshot);
        snapshotText = snapshotText.Replace(
            "\"schema\": \"questionable.file_manager.apk_property_snapshot.v1\",",
            "\"schema\": \"questionable.file_manager.apk_property_snapshot.v1\",\n  \"extra\": true,",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.Snapshot, snapshotText, new System.Text.UTF8Encoding(false));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Client(fixture.Runner).RestoreExactApkPropertiesAsync(
                Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot));
        Assert.Equal(0, fixture.Runner.SetPropertyCalls);
    }

    [Fact]
    public async Task ClearAndRestoreUseOnlyManifestSnapshotValuesAndConfirmExactReadback()
    {
        using var fixture = await PropertyFixture.CreateAsync();
        var executor = new OperatorCommandExecutor(Client(fixture.Runner));
        await executor.ExecuteAsync(OperatorCommands.ObserveExactApkProperties(
            Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot));

        var clear = await executor.ExecuteAsync(OperatorCommands.ClearExactApkProperties(
            Serial,
            fixture.Apk,
            fixture.Manifest,
            fixture.Snapshot,
            operatorConfirmed: true),
            progress: new InlineProgress<OperatorProgress>(item =>
                fixture.Runner.ProgressObservations.Add(
                    (item.Stage, fixture.Runner.SetPropertyCalls, fixture.Runner.LastCallWasReadyDiscovery))));
        var clearResult = Assert.IsType<ApkPropertyMutationResult>(clear.ApkPropertyMutationResult);
        Assert.True(clearResult.Confirmed);
        Assert.Equal(ApkPropertyMutationDisposition.Confirmed, clearResult.Disposition);
        Assert.All(PropertyNames, name => Assert.Equal(string.Empty, fixture.Runner.Values[name]));
        Assert.Equal(
            [OperatorMutationStage.Sent, OperatorMutationStage.Pending, OperatorMutationStage.Confirmed],
            clear.MutationReceipt!.Transitions.Select(static transition => transition.Stage));
        Assert.Contains(
            ("mutation-sent", 0, true),
            fixture.Runner.ProgressObservations);
        Assert.Contains(
            ("mutation-pending", 2, false),
            fixture.Runner.ProgressObservations);

        var restore = await executor.ExecuteAsync(OperatorCommands.RestoreExactApkProperties(
            Serial,
            fixture.Apk,
            fixture.Manifest,
            fixture.Snapshot,
            operatorConfirmed: true));
        var restoreResult = Assert.IsType<ApkPropertyMutationResult>(restore.ApkPropertyMutationResult);
        Assert.True(restoreResult.Confirmed);
        Assert.Equal("true", fixture.Runner.Values[PropertyNames[0]]);
        Assert.Equal(string.Empty, fixture.Runner.Values[PropertyNames[1]]);
        Assert.Equal(4, fixture.Runner.SetPropertyCalls);
        Assert.Equal(2, fixture.Runner.ReadyDiscoveryImmediatelyBeforeMutationCount);
        Assert.DoesNotContain(fixture.Runner.Calls, call =>
            call.Arguments.Contains("--property") || call.Arguments.Contains("--value"));
    }

    [Fact]
    public async Task DispatchAndReadbackFailuresStayNonConfirmedWithoutReplay()
    {
        using var fixture = await PropertyFixture.CreateAsync();
        await Client(fixture.Runner).ObserveExactApkPropertiesAsync(
            Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot);
        fixture.Runner.FailSetPropertyAtCall = 1;

        var failed = await new OperatorCommandExecutor(Client(fixture.Runner)).ExecuteAsync(
            OperatorCommands.ClearExactApkProperties(
                Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot, operatorConfirmed: true));
        Assert.Equal(
            ApkPropertyMutationDisposition.CleanupUnknown,
            failed.ApkPropertyMutationResult!.Disposition);
        Assert.Equal(1, fixture.Runner.SetPropertyCalls);
        Assert.Equal(OperatorMutationStage.Pending, failed.MutationReceipt!.Stage);

        fixture.Runner.FailSetPropertyAtCall = null;
        fixture.Runner.DivergeReadback = true;
        var divergent = await new OperatorCommandExecutor(Client(fixture.Runner)).ExecuteAsync(
            OperatorCommands.RestoreExactApkProperties(
                Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot, operatorConfirmed: true));
        Assert.Equal(
            ApkPropertyMutationDisposition.StillDivergent,
            divergent.ApkPropertyMutationResult!.Disposition);
        Assert.NotEmpty(divergent.ApkPropertyMutationResult.DivergentProperties);
        Assert.Equal(OperatorMutationStage.Pending, divergent.MutationReceipt!.Stage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostDispatchAdbReadbackFailureReturnsCleanupUnknownWithPendingReceipt(
        bool failPropertyReadback)
    {
        using var fixture = await PropertyFixture.CreateAsync();
        await Client(fixture.Runner).ObserveExactApkPropertiesAsync(
            Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot);
        fixture.Runner.FailPostDispatchGetProperty = failPropertyReadback;
        fixture.Runner.FailPostDispatchInstalledApkReadback = !failPropertyReadback;

        var execution = await new OperatorCommandExecutor(Client(fixture.Runner)).ExecuteAsync(
            OperatorCommands.ClearExactApkProperties(
                Serial,
                fixture.Apk,
                fixture.Manifest,
                fixture.Snapshot,
                operatorConfirmed: true));

        var result = Assert.IsType<ApkPropertyMutationResult>(execution.ApkPropertyMutationResult);
        Assert.Equal(ApkPropertyMutationDisposition.CleanupUnknown, result.Disposition);
        Assert.True(result.CommandsSent >= 1);
        Assert.Equal(OperatorMutationStage.Pending, execution.MutationReceipt!.Stage);
        Assert.Equal(
            [OperatorMutationStage.Sent, OperatorMutationStage.Pending],
            execution.MutationReceipt.Transitions.Select(static transition => transition.Stage));
        Assert.DoesNotContain(execution.MutationReceipt.Transitions, transition =>
            transition.Stage == OperatorMutationStage.Failed);
    }

    [Fact]
    public async Task UnexpectedPostDispatchFailureCarriesReconcilablePendingReceipt()
    {
        using var fixture = await PropertyFixture.CreateAsync();
        await Client(fixture.Runner).ObserveExactApkPropertiesAsync(
            Serial, fixture.Apk, fixture.Manifest, fixture.Snapshot);
        fixture.Runner.ThrowUnexpectedPostDispatchReadback = true;

        var exception = await Assert.ThrowsAsync<OperatorMutationExecutionException>(() =>
            new OperatorCommandExecutor(Client(fixture.Runner)).ExecuteAsync(
                OperatorCommands.ClearExactApkProperties(
                    Serial,
                    fixture.Apk,
                    fixture.Manifest,
                    fixture.Snapshot,
                    operatorConfirmed: true)));

        Assert.Equal(OperatorMutationStage.Pending, exception.MutationReceipt.Stage);
        Assert.False(exception.MutationReceipt.IsTerminal);
        Assert.Equal(
            [OperatorMutationStage.Sent, OperatorMutationStage.Pending],
            exception.MutationReceipt.Transitions.Select(static transition => transition.Stage));
        Assert.True(fixture.Runner.SetPropertyCalls >= 1);
    }

    [Fact]
    public async Task OpenEndedWrongOwnerAndUnsortedManifestsRejectWithoutPropertyEffects()
    {
        using var fixture = await PropertyFixture.CreateAsync();
        var damages = new[]
        {
            ManifestJson(owner: "com.example.other"),
            ManifestJson(extraRoot: true),
            ManifestJson(reverseProperties: true),
            ManifestJson(propertyNames: ["debug.other.example.enabled"])
        };
        foreach (var damage in damages)
        {
            File.WriteAllText(fixture.Manifest, damage, new System.Text.UTF8Encoding(false));
            var output = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N") + ".json");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                Client(fixture.Runner).ObserveExactApkPropertiesAsync(
                    Serial, fixture.Apk, fixture.Manifest, output));
            Assert.False(File.Exists(output));
        }
        Assert.Equal(0, fixture.Runner.SetPropertyCalls);
    }

    private static AdbClient Client(PropertyRunner runner) =>
        new("adb-test", runner, new("aapt2", "apksigner"));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "QuestIonAbleFileManager.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static string ManifestJson(
        string owner = Package,
        bool extraRoot = false,
        bool reverseProperties = false,
        IReadOnlyList<string>? propertyNames = null)
    {
        var names = (propertyNames ?? PropertyNames).ToArray();
        if (reverseProperties)
            Array.Reverse(names);
        var properties = string.Join(",\n", names.Select(name => $"    {{ \"name\": \"{name}\" }}"));
        var extra = extraRoot ? ",\n  \"extra\": true" : string.Empty;
        return $$"""
            {
              "schema": "rusty.quest.android_property_manifest.v1",
              "owner_package": "{{owner}}",
              "scope": "complete-source-consumer-surface",
              "prefixes": ["debug.rustyquest.example."],
              "properties": [
            {{properties}}
              ]{{extra}}
            }
            """;
    }

    private sealed class PropertyFixture : IDisposable
    {
        private PropertyFixture(string root, string apk, string manifest, string snapshot, PropertyRunner runner)
        {
            Root = root;
            Apk = apk;
            Manifest = manifest;
            Snapshot = snapshot;
            Runner = runner;
        }

        public string Root { get; }
        public string Apk { get; }
        public string Manifest { get; }
        public string Snapshot { get; }
        public PropertyRunner Runner { get; }

        public static async Task<PropertyFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "qfm-properties-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var apk = Path.Combine(root, "app.apk");
            var manifest = Path.Combine(root, "properties.json");
            var snapshot = Path.Combine(root, "snapshot.json");
            var bytes = new byte[] { 0x50, 0x4b, 0x03, 0x04 };
            await File.WriteAllBytesAsync(apk, bytes);
            await File.WriteAllTextAsync(manifest, ManifestJson(), new System.Text.UTF8Encoding(false));
            return new PropertyFixture(root, apk, manifest, snapshot, new PropertyRunner(bytes));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private static CommandResult Result(int exitCode, string stdout = "", string stderr = "") =>
        new("", [], exitCode, stdout, stderr, TimeSpan.Zero);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class PropertyRunner(byte[] apkBytes) : IStreamingCommandRunner
    {
        private bool _lastCallWasReadyDiscovery;
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal)
        {
            [PropertyNames[0]] = "true",
            [PropertyNames[1]] = string.Empty
        };
        public int DiscoveryCalls { get; private set; }
        public int SetPropertyCalls { get; private set; }
        public int ReadyDiscoveryImmediatelyBeforeMutationCount { get; private set; }
        public int? FailSetPropertyAtCall { get; set; }
        public bool FailPostDispatchGetProperty { get; set; }
        public bool FailPostDispatchInstalledApkReadback { get; set; }
        public bool ThrowUnexpectedPostDispatchReadback { get; set; }
        public bool DivergeReadback { get; set; }
        public bool Ready { get; set; } = true;
        public bool LastCallWasReadyDiscovery => _lastCallWasReadyDiscovery;
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];
        public List<(string Stage, int SetPropertyCalls, bool LastCallWasReadyDiscovery)>
            ProgressObservations { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            if (fileName == "aapt2")
                return Task.FromResult(Result(0,
                    $"package: name='{Package}' versionCode='42' versionName='1.2.3'\n"));
            if (fileName == "apksigner")
                return Task.FromResult(Result(0,
                    "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n"));
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                DiscoveryCalls++;
                _lastCallWasReadyDiscovery = true;
                var state = Ready ? "device" : "offline";
                return Task.FromResult(Result(0,
                    $"List of devices attached\n{Serial} {state} product:eureka model:Quest_3 transport_id:1\n"));
            }
            if (arguments.SequenceEqual(["-s", Serial, "shell", $"pm path '{Package}'"]))
            {
                _lastCallWasReadyDiscovery = false;
                return Task.FromResult(Result(0, "package:/data/app/example/base.apk\n"));
            }
            if (arguments.SequenceEqual(["-s", Serial, "shell", $"pm list packages '{Package}'"]))
            {
                _lastCallWasReadyDiscovery = false;
                return Task.FromResult(Result(0, $"package:{Package}\n"));
            }
            if (arguments.Count == 5 && arguments[0] == "-s" && arguments[1] == Serial &&
                arguments[2] == "shell" && arguments[3] == "getprop")
            {
                _lastCallWasReadyDiscovery = false;
                if (SetPropertyCalls > 0 && ThrowUnexpectedPostDispatchReadback)
                    throw new InvalidOperationException("synthetic unexpected post-dispatch failure");
                if (SetPropertyCalls > 0 && FailPostDispatchGetProperty)
                    return Task.FromResult(Result(1, stderr: "synthetic getprop failure"));
                var name = arguments[4];
                var value = Values.TryGetValue(name, out var current) ? current : string.Empty;
                if (DivergeReadback && name == PropertyNames[0])
                    value = "synthetic-divergence";
                return Task.FromResult(Result(0, value + "\n"));
            }
            if (arguments.Count == 6 && arguments[0] == "-s" && arguments[1] == Serial &&
                arguments[2] == "shell" && arguments[3] == "setprop")
            {
                SetPropertyCalls++;
                if (_lastCallWasReadyDiscovery)
                    ReadyDiscoveryImmediatelyBeforeMutationCount++;
                _lastCallWasReadyDiscovery = false;
                if (FailSetPropertyAtCall == SetPropertyCalls)
                    return Task.FromResult(Result(1, stderr: "synthetic failure"));
                Values[arguments[4]] = arguments[5];
                return Task.FromResult(Result(0));
            }
            _lastCallWasReadyDiscovery = false;
            return Task.FromResult(Result(1, stderr: "unexpected command: " + string.Join(" ", arguments)));
        }

        public async Task<StreamingCommandResult> RunToStreamAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            Stream destination,
            long maximumBytes,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            if (SetPropertyCalls > 0 && FailPostDispatchInstalledApkReadback)
            {
                throw new AdbCommandException(
                    "Synthetic installed APK readback",
                    Result(1, stderr: "synthetic installed APK failure"));
            }
            await destination.WriteAsync(apkBytes, cancellationToken);
            var command = Result(0) with { FileName = fileName, Arguments = arguments.ToArray() };
            return new StreamingCommandResult(
                command,
                apkBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(apkBytes)).ToLowerInvariant());
        }
    }
}
