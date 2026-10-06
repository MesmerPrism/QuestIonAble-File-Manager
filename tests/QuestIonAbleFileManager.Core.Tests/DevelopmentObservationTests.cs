using System.Security.Cryptography;
using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class DevelopmentObservationTests
{
    private const string Metadata = "  Package [com.example.app] (123abc):\n    versionCode=42 minSdk=34 targetSdk=34\n    versionName=1.0\n    lastUpdateTime=2026-10-05 12:00:00\n";

    [Fact]
    public async Task ObservationReportsActualMetadataAndExplicitlyUnverifiedIdentity()
    {
        using var fixture = new Fixture();
        var result = await fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk, "local-install-reference");
        Assert.Equal("questionable.file_manager.development_app_runtime_observation.v1", result.ObservationContract);
        Assert.Equal("development-metadata", result.VerificationPolicy);
        Assert.False(result.InstalledBytesVerified);
        Assert.False(result.InstalledSignerVerified);
        Assert.False(result.ReportedInstallProvenanceVerified);
        Assert.Equal("local-install-reference", result.ReportedInstallReference);
        Assert.Equal("com.example.app", result.InstalledMetadata.PackageName);
        Assert.Equal(42, result.InstalledMetadata.VersionCode);
        Assert.Equal("1.0", result.InstalledMetadata.VersionName);
        Assert.Equal("2026-10-05 12:00:00", result.InstalledMetadata.LastUpdateTime);
        Assert.Equal(new[] { "/data/app/example/base.apk" }, result.InstalledMetadata.ApkPaths);
        Assert.Null(result.Runtime.Installed);
        Assert.Equal("questionable.file_manager.development_runtime_facts.v1", result.Runtime.ObservationContract);
        Assert.True(result.Runtime.IsForeground);
        Assert.Equal(new[] { 123 }, result.Runtime.ProcessIds);
        Assert.Equal(2, fixture.Runner.MetadataReads);
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("exec-out"));
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("install") || args.Contains("pull"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown package")]
    [InlineData("  Package [com.example.other] (123abc):\n    versionCode=42\n    versionName=1.0\n    lastUpdateTime=2026-10-05 12:00:00\n")]
    [InlineData("  Package [com.example.app] (123abc):\n    versionCode=wrong\n    versionName=1.0\n    lastUpdateTime=2026-10-05 12:00:00\n")]
    [InlineData("  Package [com.example.app] (123abc):\n    versionCode=43\n    versionName=1.0\n    lastUpdateTime=2026-10-05 12:00:00\n")]
    [InlineData("  Package [com.example.app] (123abc):\n    versionCode=42\n    versionName=2.0\n    lastUpdateTime=2026-10-05 12:00:00\n")]
    [InlineData("  Package [com.example.app] (123abc):\n    versionCode=42\n    versionName=1.0\n    lastUpdateTime=unknown\n")]
    public async Task MissingMalformedOrMismatchedMetadataFailsClosed(string metadata)
    {
        using var fixture = new Fixture();
        fixture.Runner.FirstMetadata = metadata;
        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk));
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("exec-out"));
    }

    [Fact]
    public async Task DuplicateMetadataFieldsFailClosed()
    {
        using var fixture = new Fixture();
        fixture.Runner.FirstMetadata = Metadata + "    versionCode=42\n";
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk));
    }

    [Fact]
    public async Task DuplicatePackageSectionsFailClosed()
    {
        using var fixture = new Fixture();
        fixture.Runner.FirstMetadata = Metadata + Metadata;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk));
    }

    [Theory]
    [InlineData("versionCode=wrong")]
    [InlineData("lastUpdateTime=wrong")]
    [InlineData("versionCode =wrong")]
    [InlineData("lastUpdateTime =wrong")]
    public async Task ValidAndMalformedDuplicateMetadataFieldsFailClosed(string duplicate)
    {
        using var fixture = new Fixture();
        fixture.Runner.FirstMetadata = Metadata + "    " + duplicate + "\n";
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk));
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("exec-out") || args.Contains("activity"));
    }

    [Fact]
    public async Task SplitPackageCannotUseStandaloneDevelopmentObservation()
    {
        using var fixture = new Fixture();
        fixture.Runner.SplitPackage = true;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk));
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("exec-out"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedTimestampOrPathDuringRuntimeFailsClosed(bool changePath)
    {
        using var fixture = new Fixture();
        if (changePath) fixture.Runner.ChangePath = true;
        else fixture.Runner.SecondMetadata = Metadata.Replace("12:00:00", "12:00:01", StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk));
    }

    [Fact]
    public void TypedCommandRequiresExplicitDevelopmentPolicyInEquivalentCli()
    {
        var apk = Path.GetFullPath("example.apk");
        var command = OperatorCommands.ObserveDevelopmentApp("QUEST123", apk);
        Assert.Equal(new[] { "apk", "observe", "--serial", "QUEST123", "--file", apk,
            "--verification", "development-metadata", "--json" }, command.CliArguments);
    }

    [Fact]
    public async Task SharedExecutorProjectsOnlyTheDevelopmentObservationContract()
    {
        using var fixture = new Fixture();
        var command = OperatorCommands.ObserveDevelopmentApp("QUEST123", fixture.Apk, "install-123");
        var result = await new OperatorCommandExecutor(fixture.Client).ExecuteAsync(command);
        Assert.NotNull(result.DevelopmentAppRuntimeObservation);
        Assert.Null(result.AppRuntimeObservation);
        Assert.Null(result.MutationReceipt);
        Assert.Equal("install-123", result.DevelopmentAppRuntimeObservation.ReportedInstallReference);
        Assert.False(result.DevelopmentAppRuntimeObservation.ReportedInstallProvenanceVerified);
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("exec-out"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains spaces")]
    [InlineData("path/to/receipt")]
    [InlineData("reference\nother")]
    public async Task MalformedReportedInstallReferenceIsRejectedBeforeAnyRunnerCall(string reference)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk, reference));
        Assert.Throws<ArgumentException>(() => OperatorCommands.ObserveDevelopmentApp("QUEST123", fixture.Apk, reference));
        Assert.Empty(fixture.Runner.Calls);
    }

    [Theory]
    [InlineData("unknown-option")]
    [InlineData("duplicate-policy")]
    [InlineData("reference-without-policy")]
    [InlineData("mixed-case-policy")]
    [InlineData("mixed-case-reference")]
    public async Task InvalidDevelopmentOptionsAreRejectedBeforeAdbResolution(string scenario)
    {
        var arguments = new List<string> { "apk", "observe", "--serial", "QUEST123", "--file", "example.apk" };
        if (scenario is not ("reference-without-policy" or "mixed-case-reference"))
            arguments.AddRange(["--verification", "development-metadata", "--json"]);
        switch (scenario)
        {
            case "unknown-option": arguments.AddRange(["--unknown-option", "value"]); break;
            case "duplicate-policy": arguments.AddRange(["--verification", "development-metadata"]); break;
            case "reference-without-policy": arguments.AddRange(["--reported-install-reference", "install-123", "--json"]); break;
            case "mixed-case-policy": arguments[arguments.IndexOf("--verification")] = "--Verification"; break;
            case "mixed-case-reference": arguments.AddRange(["--Reported-install-reference", "install-123", "--json"]); break;
        }
        arguments.AddRange(["--adb", "missing-adb.exe"]);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Assert.Equal(2, await CliApplication.RunAsync(arguments.ToArray()));
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
        Assert.DoesNotContain("missing-adb.exe", output.ToString() + error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("development", output.ToString() + error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidVerificationPolicyIsRejectedBeforeAdbResolution()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Assert.Equal(2, await CliApplication.RunAsync(["apk", "observe", "--serial", "QUEST123",
                "--file", "example.apk", "--verification", "unknown-policy", "--adb", "missing-adb.exe", "--json"]));
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
        Assert.Contains("verification", output.ToString() + error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing-adb.exe", output.ToString() + error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, "123 456 123\n", "", RuntimeProcessObservationQuality.PidofReportedProcesses, 2)]
    [InlineData(0, "", "", RuntimeProcessObservationQuality.PidofReportedNoProcesses, 0)]
    [InlineData(1, "", "", RuntimeProcessObservationQuality.PidofReportedNoProcesses, 0)]
    [InlineData(1, " \n", "\t", RuntimeProcessObservationQuality.PidofReportedNoProcesses, 0)]
    [InlineData(1, "123\n", "", RuntimeProcessObservationQuality.PidofUnavailable, 0)]
    [InlineData(1, "", "pidof unavailable", RuntimeProcessObservationQuality.PidofUnavailable, 0)]
    [InlineData(2, "", "", RuntimeProcessObservationQuality.PidofUnavailable, 0)]
    [InlineData(-1, "", "", RuntimeProcessObservationQuality.PidofUnavailable, 0)]
    [InlineData(0, "123 damaged\n", "", RuntimeProcessObservationQuality.PidofOutputUnusable, 0)]
    [InlineData(0, "0\n", "", RuntimeProcessObservationQuality.PidofOutputUnusable, 0)]
    [InlineData(0, "123\n", "warning", RuntimeProcessObservationQuality.PidofUnavailable, 0)]
    public async Task RuntimePidofQualityPreservesNativeExitAndNoMatchBoundary(
        int exitCode, string output, string error, RuntimeProcessObservationQuality quality, int count)
    {
        using var fixture = new Fixture();
        fixture.Runner.PidofResult = new("adb", [], exitCode, output, error, TimeSpan.Zero);
        var result = await fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk);
        Assert.Equal(exitCode, result.Runtime.ProcessObservationExitCode);
        Assert.Equal(quality, result.Runtime.ProcessObservationQuality);
        Assert.Equal(count, result.Runtime.ProcessIds.Count);
        Assert.Equal("unknown", result.Runtime.ApplicationReadiness);
        Assert.False(result.Runtime.ApplicationReadinessAuthority);
        Assert.Equal("unknown", result.Runtime.OpenXrReadiness);
        Assert.False(result.Runtime.OpenXrReadinessAuthority);
        Assert.Null(result.Runtime.Installed);
        Assert.False(result.InstalledBytesVerified);
        Assert.False(result.InstalledSignerVerified);
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("exec-out") || args.Contains("install"));
    }

    [Fact]
    public async Task CanceledPidofDoesNotBecomeNoProcessObservation()
    {
        using var fixture = new Fixture();
        fixture.Runner.CancelPidof = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk));
        Assert.Equal(1, fixture.Runner.MetadataReads);
    }
    [Fact]
    public async Task RuntimeExitFieldIsAdditiveAndExistingQualityLabelsRemainStable()
    {
        using var fixture = new Fixture();
        var result = await fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk);
        var options = new System.Text.Json.JsonSerializerOptions();
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        var json = System.Text.Json.JsonSerializer.SerializeToNode(result.Runtime, options)!;
        Assert.Equal(0, json["ProcessObservationExitCode"]!.GetValue<int>());
        Assert.Equal("pidofReportedProcesses", json["ProcessObservationQuality"]!.GetValue<string>());
        Assert.Equal("questionable.file_manager.development_runtime_facts.v1", json["ObservationContract"]!.GetValue<string>());
        Assert.Equal("unknown", json["ApplicationReadiness"]!.GetValue<string>());
        Assert.False(json["ApplicationReadinessAuthority"]!.GetValue<bool>());
        Assert.True(json.AsObject().Remove("ProcessObservationExitCode"));
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppRuntimeObservation>(json.ToJsonString(), options)!;
        Assert.Null(legacy.ProcessObservationExitCode);
        Assert.Equal(RuntimeProcessObservationQuality.PidofReportedProcesses, legacy.ProcessObservationQuality);
        Assert.Equal(new[] { 123 }, legacy.ProcessIds);
        Assert.Equal(new[] { "PidofReportedProcesses", "PidofReportedNoProcesses", "PidofOutputUnusable", "PidofUnavailable" }, Enum.GetNames<RuntimeProcessObservationQuality>());
    }

    [Fact]
    public async Task TimedOutPidofDoesNotBecomeNoProcessObservation()
    {
        using var fixture = new Fixture();
        fixture.Runner.PidofFailure = new TimeoutException("modeled native observer timeout");
        await Assert.ThrowsAsync<TimeoutException>(() =>
            fixture.Client.ObserveDevelopmentAppAsync("QUEST123", fixture.Apk));
        Assert.Equal(1, fixture.Runner.MetadataReads);
    }
    private sealed class Fixture : IDisposable
    {
        public string Apk { get; } = Path.Combine(Path.GetTempPath(), "qfm-development-" + Guid.NewGuid().ToString("N") + ".apk");
        public Runner Runner { get; } = new();
        public AdbClient Client { get; }
        public Fixture()
        {
            File.WriteAllBytes(Apk, [0x50, 0x4b, 0x03, 0x04]);
            Client = new AdbClient("adb", Runner, new("aapt2", "apksigner"));
        }
        public void Dispose() => File.Delete(Apk);
    }

    private sealed class Runner : IStreamingCommandRunner
    {
        public CommandResult? PidofResult { get; set; }
        public bool CancelPidof { get; set; }
        public Exception? PidofFailure { get; set; }
        public string FirstMetadata { get; set; } = Metadata;
        public string SecondMetadata { get; set; } = Metadata;
        public bool ChangePath { get; set; }
        public bool SplitPackage { get; set; }
        public int MetadataReads { get; private set; }
        private int pathReads;
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            if (arguments.SequenceEqual(new[] { "-s", "QUEST123", "shell", "pidof", "com.example.app" }))
            {
                if (CancelPidof) throw new OperationCanceledException();
                if (PidofFailure is not null) throw PidofFailure;
                if (PidofResult is not null) return Task.FromResult(PidofResult with { FileName = fileName, Arguments = arguments });
            }
            var output = fileName switch
            {
                "aapt2" => "package: name='com.example.app' versionCode='42' versionName='1.0'\n",
                "apksigner" => "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n",
                _ => DeviceOutput(arguments)
            };
            return Task.FromResult(new CommandResult(fileName, arguments, 0, output, "", TimeSpan.Zero));
        }

        private string DeviceOutput(IReadOnlyList<string> args)
        {
            Assert.Equal(new[] { "-s", "QUEST123" }, args.Take(2));
            if (args.SequenceEqual(["-s", "QUEST123", "shell", "pm path 'com.example.app'"]))
            {
                pathReads++;
                if (SplitPackage) return "package:/data/app/example/base.apk\npackage:/data/app/example/split_config.en.apk\n";
                return ChangePath && pathReads > 1 ? "package:/data/app/replacement/base.apk\n" : "package:/data/app/example/base.apk\n";
            }
            if (args.SequenceEqual(["-s", "QUEST123", "shell", "dumpsys", "activity", "activities"]))
                return "mResumedActivity: ActivityRecord{123 u0 com.example.app/.Main t1}\n";
            if (args.SequenceEqual(["-s", "QUEST123", "shell", "pidof", "com.example.app"])) return "123\n";
            throw new InvalidOperationException("Unexpected fixture command.");
        }

        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName,
            IReadOnlyList<string> arguments, Stream destination, long maximumBytes, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            string output;
            if (arguments.SequenceEqual(["-s", "QUEST123", "shell", "dumpsys", "package", "com.example.app"]))
            {
                Assert.Equal(256 * 1024, maximumBytes);
                output = ++MetadataReads == 1 ? FirstMetadata : SecondMetadata;
            }
            else
            {
                Assert.Equal(new[] { "-s", "QUEST123", "shell", "dumpsys", "window", "windows" }, arguments);
                output = "mCurrentFocus=Window{123 u0 com.example.app/.Main}\n";
            }
            var bytes = Encoding.UTF8.GetBytes(output);
            await destination.WriteAsync(bytes, cancellationToken);
            return new(new(fileName, arguments, 0, "", "", TimeSpan.Zero), bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}
