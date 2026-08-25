using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class ApkPermissionObservationTests
{
    private const string Serial = "QUEST123";
    private const string Package = "com.example.app";

    [Fact]
    public async Task FixtureCorpusPreservesBoundedRawPermissionStates()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "QuestIonAbleFileManager.Core.Tests",
            "Fixtures",
            "apk-permission-observation.v1.json")));
        Assert.Equal(
            "questionable.file_manager.apk_permission_observation.v1",
            fixture.RootElement.GetProperty("schema").GetString());

        foreach (var scenario in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var runner = new FixtureRunner(scenario);
            var observation = await new AdbClient("adb", runner)
                .ObservePackagePermissionsAsync(Serial, Package);
            var expected = scenario.GetProperty("expected");

            Assert.Equal(
                ParseState(expected, "package_state"),
                observation.PackageState);
            Assert.Equal(
                ParseState(expected, "manifest_state"),
                observation.ManifestDeclaredPermissionsState);
            Assert.Equal(
                ParseState(expected, "grant_state"),
                observation.EffectiveGrantState);
            Assert.Equal(
                ParseState(expected, "app_op_state"),
                observation.AppOpState);
            Assert.Equal(
                ReadStrings(expected, "manifest_permissions"),
                observation.ManifestDeclaredPermissions.Select(permission => permission.Name));
            Assert.Equal(
                ReadStrings(expected, "grants"),
                observation.EffectiveGrants.Select(grant =>
                    $"{grant.Name}|{grant.Granted}|{grant.Source}"));
            Assert.Equal(
                ReadStrings(expected, "app_ops"),
                observation.AppOps.Select(appOp => $"{appOp.Operation}|{appOp.Mode}"));
            Assert.Equal(
                "questionable.file_manager.apk_permission_observation.v1",
                observation.ObservationContract);
            Assert.Equal("questionable-file-manager", observation.Provider.Id);
            Assert.Equal(
                ProviderCapabilityDiscoveryContract.ProviderVersion,
                observation.Provider.Version);
            Assert.Equal(
                "https://github.com/MesmerPrism/QuestIonAble-File-Manager",
                observation.Provider.SourceRepository);
            Assert.Equal("windows-portable-cli", observation.Provider.Distribution);
            Assert.DoesNotContain(runner.Calls, call =>
                call.Arguments.Contains("grant", StringComparer.OrdinalIgnoreCase) ||
                call.Arguments.Contains("revoke", StringComparer.OrdinalIgnoreCase) ||
                call.Arguments.Contains("setprop", StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void AgentRouteIsStrictAndReadOnly()
    {
        var command = OperatorCommands.ParsePackagePermissionObservationCliArguments(
            ["apk", "permissions", "--serial", Serial, "--package", Package, "--json"]);

        Assert.Equal(OperatorCommandKind.ObservePackagePermissions, command.Kind);
        Assert.Equal(
            ["apk", "permissions", "--serial", Serial, "--package", Package],
            command.CliArguments);
        Assert.Throws<ArgumentException>(() =>
            OperatorCommands.ParsePackagePermissionObservationCliArguments(
                ["apk", "permissions", "--serial", Serial, "--package", Package, "--json", "--extra"]));

        var route = Assert.Single(
            OperatorActionRegistry.AgentRoutes,
            route => route.Id == "apk_permission_observation");
        Assert.False(route.RequiresConfirmation);
        Assert.Contains("does not mutate", route.ReadbackContract, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OperatorActionRegistry.Actions, action => action.Id == "apk.permissions");
    }

    [Fact]
    public async Task OperatorActionsExporterAdvertisesTheExactContract()
    {
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, await CliApplication.RunAsync(["operator-actions", "--json"]));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var exported = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "questionable.file_manager.apk_permission_observation.v1",
            exported.RootElement.GetProperty("contracts")
                .GetProperty("apkPermissionObservation").GetString());
        var route = Assert.Single(
            exported.RootElement.GetProperty("agentRoutes").EnumerateArray(),
            route => string.Equals(
                route.GetProperty("Id").GetString(),
                "apk_permission_observation",
                StringComparison.Ordinal));
        Assert.Equal(
            "apk permissions --serial --package --json",
            route.GetProperty("CliRoute").GetString());
    }

    private static ApkPermissionObservationState ParseState(
        JsonElement expected,
        string property) =>
        Enum.Parse<ApkPermissionObservationState>(
            expected.GetProperty(property).GetString()!,
            ignoreCase: false);

    private static string[] ReadStrings(JsonElement expected, string property) =>
        expected.GetProperty(property).EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "QuestIonAbleFileManager.slnx")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed class FixtureRunner(JsonElement scenario) : IStreamingCommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return Task.FromResult(ResultFor(arguments, streamed: false) with
            {
                FileName = fileName,
                Arguments = arguments.ToArray()
            });
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
            var result = ResultFor(arguments, streamed: true) with
            {
                FileName = fileName,
                Arguments = arguments.ToArray()
            };
            var bytes = Encoding.UTF8.GetBytes(result.StandardOutput);
            if (result.Succeeded)
            {
                Assert.True(bytes.LongLength <= maximumBytes);
                await destination.WriteAsync(bytes, cancellationToken);
            }
            return new StreamingCommandResult(
                result,
                result.Succeeded ? bytes.LongLength : 0,
                Convert.ToHexString(SHA256.HashData(result.Succeeded ? bytes : [])).ToLowerInvariant());
        }

        private CommandResult ResultFor(IReadOnlyList<string> arguments, bool streamed)
        {
            var field = arguments.SequenceEqual(
                ["-s", Serial, "shell", $"pm path '{Package}'"])
                ? "package_path"
                : arguments.SequenceEqual(
                    ["-s", Serial, "shell", $"pm list packages '{Package}'"])
                    ? "package_list"
                    : arguments.SequenceEqual(
                        ["-s", Serial, "shell", "dumpsys", "package", Package])
                        ? "package_dump"
                        : arguments.SequenceEqual(
                            ["-s", Serial, "shell", "cmd", "appops", "get", "--uid", Package])
                            ? "app_ops"
                            : throw new Xunit.Sdk.XunitException(
                                $"Unexpected permission-observation command: {string.Join(" ", arguments)}");
            if (streamed && field is not ("package_dump" or "app_ops"))
                throw new Xunit.Sdk.XunitException("Only bounded permission sources may stream.");
            var source = scenario.TryGetProperty(field, out var value)
                ? value
                : default;
            return new CommandResult(
                "",
                [],
                source.ValueKind == JsonValueKind.Undefined
                    ? 0
                    : source.TryGetProperty("exit_code", out var exitCode)
                        ? exitCode.GetInt32()
                        : 0,
                source.ValueKind == JsonValueKind.Undefined
                    ? string.Empty
                    : source.TryGetProperty("stdout", out var stdout)
                        ? stdout.GetString() ?? string.Empty
                        : string.Empty,
                source.ValueKind == JsonValueKind.Undefined
                    ? string.Empty
                    : source.TryGetProperty("stderr", out var stderr)
                        ? stderr.GetString() ?? string.Empty
                        : string.Empty,
                TimeSpan.Zero);
        }
    }
}
