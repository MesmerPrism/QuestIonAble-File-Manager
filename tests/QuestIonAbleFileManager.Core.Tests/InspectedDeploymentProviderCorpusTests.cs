using QuestIonAbleFileManager.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class InspectedDeploymentProviderCorpusTests
{
    private const string ProviderCorpusSchema =
        "questionable.file_manager.inspected_deployment_provider_conformance.v1";
    private const string LaunchSchema =
        "questionable.file_manager.apk_launch_result.v1";

    [Fact]
    public async Task PublicCorpusFixesNativeSchemasAndConsumerTerminalBoundary()
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "QuestIonAbleFileManager.Core.Tests",
            "Fixtures",
            "inspected-deployment-provider-conformance.v1.json")));
        var root = document.RootElement;

        Assert.Equal(ProviderCorpusSchema, root.GetProperty("schema").GetString());
        Assert.Equal(
            [
                "questionable.file_manager.apk_launch_result.v1",
                "questionable.file_manager.app_runtime_observation.v2",
                "questionable.file_manager.inspected_deployment.v3",
                "questionable.file_manager.launcher_export_proof.v2"
            ],
            root.GetProperty("native_schema_ids")
                .EnumerateArray()
                .Select(static value => value.GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray());

        var launchInvariant = root.GetProperty("native_launch_envelope_invariant");
        Assert.Equal(LaunchSchema, launchInvariant.GetProperty("schema").GetString());
        Assert.Equal("non-null", launchInvariant.GetProperty("success").GetProperty("mutation").GetString());
        Assert.Equal("non-null", launchInvariant.GetProperty("success").GetProperty("result").GetString());
        Assert.Equal("null", launchInvariant.GetProperty("success").GetProperty("failure").GetString());
        Assert.Equal("null", launchInvariant.GetProperty("failure").GetProperty("mutation").GetString());
        Assert.Equal("null", launchInvariant.GetProperty("failure").GetProperty("result").GetString());
        Assert.Equal("non-null", launchInvariant.GetProperty("failure").GetProperty("failure").GetString());

        var runtime = root.GetProperty("runtime_observation_v2");
        Assert.Equal("questionable.file_manager.app_runtime_observation.v2", runtime.GetProperty("schema").GetString());
        Assert.Equal(
            ["android_foreground", "android_installed_identity", "android_process", "android_top_resumed"],
            runtime.GetProperty("proves").EnumerateArray()
                .Select(static value => value.GetString()!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["app_effect", "openxr_readiness", "wearer_visibility"],
            runtime.GetProperty("does_not_prove").EnumerateArray()
                .Select(static value => value.GetString()!).Order(StringComparer.Ordinal).ToArray());

        var terminal = root.GetProperty("consumer_terminal_contract");
        Assert.Equal("consumer", terminal.GetProperty("owner").GetString());
        Assert.True(terminal.GetProperty("exactly_one_terminal_json_document").GetBoolean());
        Assert.True(terminal.GetProperty("all_outcomes_publish_atomically").GetBoolean());
        Assert.True(terminal.GetProperty("raw_streams_must_be_retained_and_digested").GetBoolean());

        var expectedCases = new[]
        {
            "atomic_final_file",
            "automatic_variable_name_hazards",
            "cancelled",
            "internal_failure",
            "invocation_failure",
            "malformed_stdout",
            "offline",
            "parse_failure",
            "process_tree_cancellation",
            "provider_failure",
            "raw_stream_retention_and_digest",
            "stderr_only",
            "success",
            "timeout",
            "truncated_stdout",
            "windows_quoting_and_locale",
            "zero_stdout"
        };
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(
            expectedCases,
            cases.Select(static item => item.GetProperty("id").GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray());

        foreach (var item in cases)
        {
            AssertRawStreamDigest(item.GetProperty("raw_streams"), "stdout");
            AssertRawStreamDigest(item.GetProperty("raw_streams"), "stderr");

            var expected = item.GetProperty("expected");
            Assert.Equal(
                expected.GetProperty("provider_json_documents").GetInt32(),
                CountStrictJsonDocuments(item.GetProperty("raw_streams").GetProperty("stdout_utf8").GetString()!));
            Assert.Equal(1, expected.GetProperty("consumer_terminal_json_documents").GetInt32());
            Assert.Equal("atomic", expected.GetProperty("final_file_publication").GetString());

            var envelope = item.GetProperty("native_launch_envelope");
            if (envelope.ValueKind != JsonValueKind.Null)
            {
                AssertNativeLaunchEnvelope(envelope);
            }
        }
    }

    [Fact]
    public void AliasFactsAreAdditiveToTheExistingLaunchResultShape()
    {
        var identity = new ApkArtifactIdentity(
            "com.example.app",
            42,
            "1.2.3",
            new string('a', 64),
            null);
        var result = new ResolvedAppLaunchResult(
            new ApkArtifactInspection("artifact.apk", 4, new string('b', 64), identity),
            new InstalledApkIdentity("QUEST123", identity, ["/data/app/base.apk"], new string('b', 64), 4),
            "com.example.app/.Main",
            new CommandResult("adb", [], 0, "Starting: Intent", "", TimeSpan.Zero),
            true);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var properties = document.RootElement.EnumerateObject()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(properties.IsSupersetOf(
            ["Artifact", "Installed", "Component", "CommandResult", "ComponentObservedResumed"]));
        Assert.False(document.RootElement.GetProperty("LauncherIsActivityAlias").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("LauncherTargetActivity").ValueKind);
    }

    private static void AssertNativeLaunchEnvelope(JsonElement envelope)
    {
        Assert.Equal(LaunchSchema, envelope.GetProperty("schema").GetString());
        var succeeded = envelope.GetProperty("succeeded").GetBoolean();
        if (succeeded)
        {
            Assert.NotEqual(JsonValueKind.Null, envelope.GetProperty("mutation").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, envelope.GetProperty("result").ValueKind);
            Assert.Equal(JsonValueKind.Null, envelope.GetProperty("failure").ValueKind);
            return;
        }

        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("mutation").ValueKind);
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("result").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, envelope.GetProperty("failure").ValueKind);
    }

    private static void AssertRawStreamDigest(JsonElement streams, string streamName)
    {
        var raw = streams.GetProperty($"{streamName}_utf8").GetString()!;
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        Assert.Equal(streams.GetProperty($"{streamName}_sha256").GetString(), actual);
    }

    private static int CountStrictJsonDocuments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        try
        {
            using var _ = JsonDocument.Parse(raw);
            return 1;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "QuestIonAbleFileManager.slnx")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
