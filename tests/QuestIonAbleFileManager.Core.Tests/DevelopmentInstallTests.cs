using System.Security.Cryptography;
using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class DevelopmentInstallTests
{
    private const string Metadata = "  Package [com.example.app] (123abc):\n    versionCode=42 minSdk=34\n    versionName=1.0+src.example\n    lastUpdateTime=2026-10-05 12:00:00\n";

    [Fact]
    public async Task ExplicitInstallConfirmsOnlyMetadataAndKeepsInputImmutable()
    {
        using var fixture = new Fixture();
        var command = OperatorCommands.InstallDevelopmentApk("QUEST123", fixture.Apk,
            new(false, true, true, true));
        var result = await new OperatorCommandExecutor(fixture.Client).ExecuteAsync(command);
        var install = Assert.IsType<DevelopmentApkInstallResult>(result.DevelopmentApkInstallResult);
        Assert.Equal("questionable.file_manager.development_apk_install.v1", install.VerificationContract);
        Assert.Equal("development-metadata", install.VerificationPolicy);
        Assert.True(install.InstallerCommandSucceeded);
        Assert.False(install.InstalledBytesVerified);
        Assert.False(install.InstalledSignerVerified);
        Assert.False(install.UniqueBuildIdentityVerified);
        Assert.False(install.TransactionProvenanceVerified);
        Assert.False(install.ApplicationReadinessVerified);
        Assert.Null(result.InspectedApkInstallResult);
        Assert.Null(result.AppRuntimeObservation);
        Assert.Equal(fixture.Apk, install.LocalArtifact.Path);
        Assert.Equal(42, install.InstalledMetadata.VersionCode);
        Assert.Equal("1.0+src.example", install.InstalledMetadata.VersionName);
        Assert.Equal(2, fixture.Runner.MetadataReads);
        Assert.True(fixture.Runner.StagedWriteRejected);
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("exec-out") || args.Contains("pull") || args.Contains("pidof"));
        var receipt = Assert.IsType<OperatorMutationReceipt>(result.MutationReceipt);
        Assert.Equal(OperatorMutationStage.Confirmed, receipt.Stage);
        Assert.Equal(new[] { OperatorMutationStage.Sent, OperatorMutationStage.Pending, OperatorMutationStage.Confirmed }, receipt.Transitions.Select(x => x.Stage));
        Assert.Contains("remain unverified", receipt.ObservedState);
        Assert.Equal(new[] { "-s", "QUEST123", "install", "-d", "-g", "-t" }, fixture.Runner.InstallArguments!.Take(6));
        var exactCommand = OperatorCommands.InstallApk("QUEST123", fixture.Apk);
        var pending = receipt with { CommandKind = OperatorCommandKind.InstallApk, Stage = OperatorMutationStage.Pending };
        Assert.Throws<InvalidOperationException>(() => OperatorMutationReconciler.Reconcile(pending, exactCommand, result));
    }

    [Theory]
    [InlineData("versionCode=43")]
    [InlineData("versionCode=wrong")]
    [InlineData("versionName=other")]
    [InlineData("lastUpdateTime=wrong")]
    [InlineData("versionCode=42\n    versionCode=wrong")]
    public async Task PostDispatchMismatchOrMalformedMetadataRemainsPending(string replacement)
    {
        using var fixture = new Fixture();
        var key = replacement[..replacement.IndexOf('=')];
        fixture.Runner.FirstMetadata = Metadata.Replace(key switch
        {
            "versionCode" => "versionCode=42 minSdk=34", "versionName" => "versionName=1.0+src.example",
            _ => "lastUpdateTime=2026-10-05 12:00:00"
        }, replacement);
        var error = await Assert.ThrowsAsync<OperatorMutationExecutionException>(() =>
            new OperatorCommandExecutor(fixture.Client).ExecuteAsync(OperatorCommands.InstallDevelopmentApk("QUEST123", fixture.Apk)));
        Assert.Equal(OperatorMutationStage.Pending, error.MutationReceipt.Stage);
        Assert.Single(fixture.Runner.Calls, args => args.Contains("install"));
        Assert.DoesNotContain(error.MutationReceipt.Transitions, t => t.Stage == OperatorMutationStage.Confirmed);
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("artifact-path")]
    [InlineData("package")]
    [InlineData("version")]
    [InlineData("native-serial")]
    [InlineData("native-failure")]
    [InlineData("result-command")]
    [InlineData("result-flags")]
    [InlineData("native-flags")]
    public async Task ReconcilerRejectsEvidenceFromAnotherInstall(string mismatch)
    {
        using var fixture = new Fixture();
        var command = OperatorCommands.InstallDevelopmentApk("QUEST123", fixture.Apk);
        var result = await new OperatorCommandExecutor(fixture.Client).ExecuteAsync(command);
        var install = result.DevelopmentApkInstallResult!;
        install = mismatch switch
        {
            "serial" => install with { Serial = "OTHER" },
            "artifact-path" => install with { LocalArtifact = install.LocalArtifact with { Path = fixture.Apk + ".other" } },
            "package" => install with { InstalledMetadata = install.InstalledMetadata with { PackageName = "com.other.app" } },
            "version" => install with { InstalledMetadata = install.InstalledMetadata with { VersionCode = 43 } },
            "native-serial" => install with { CommandResult = install.CommandResult with { Arguments = ["-s", "OTHER", "install"] } },
            "native-failure" => install with { CommandResult = install.CommandResult with { ExitCode = 1 } },
            "native-flags" => install with { CommandResult = install.CommandResult with { Arguments = ["-s", "QUEST123", "install", "-r", "-g", "admitted.apk"] } },
            _ => install
        };
        result = result with { DevelopmentApkInstallResult = install };
        if (mismatch == "result-command") result = result with { Command = OperatorCommands.InstallDevelopmentApk("OTHER", fixture.Apk) };
        if (mismatch == "result-flags") result = result with { Command = OperatorCommands.InstallDevelopmentApk("QUEST123", fixture.Apk, new(GrantRuntimePermissions: true)) };
        var pending = result.MutationReceipt! with { Stage = OperatorMutationStage.Pending };
        Assert.Equal(OperatorMutationStage.Pending, OperatorMutationReconciler.Reconcile(pending, command, result).Stage);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("install-failure")]
    [InlineData("readback-failure")]
    [InlineData("cancel-after-install")]
    public async Task UncertainNativeOutcomeNeverConfirmsOrReplays(string failure)
    {
        using var fixture = new Fixture();
        fixture.Runner.Failure = failure;
        var error = await Assert.ThrowsAsync<OperatorMutationExecutionException>(() =>
            new OperatorCommandExecutor(fixture.Client).ExecuteAsync(OperatorCommands.InstallDevelopmentApk("QUEST123", fixture.Apk)));
        Assert.Equal(OperatorMutationStage.Pending, error.MutationReceipt.Stage);
        Assert.Single(fixture.Runner.Calls, args => args.Contains("install"));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("duplicate-device")]
    [InlineData("split")]
    public async Task AdmissionFailureHasNoSentOrInstall(string failure)
    {
        using var fixture = new Fixture();
        fixture.Runner.Failure = failure;
        var error = await Record.ExceptionAsync(() => new OperatorCommandExecutor(fixture.Client).ExecuteAsync(
            OperatorCommands.InstallDevelopmentApk("QUEST123", fixture.Apk)));
        Assert.NotNull(error);
        Assert.IsNotType<OperatorMutationExecutionException>(error);
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("install"));
    }

    [Fact]
    public void ParserKeepsExactDefaultAndDevelopmentPolicySeparate()
    {
        var exact = OperatorCommands.InstallApk("QUEST123", "example.apk");
        Assert.Equal(OperatorCommandKind.InstallApk, exact.Kind);
        Assert.DoesNotContain("--verification", exact.CliArguments);
        var dev = OperatorCommands.InstallDevelopmentApk("QUEST123", "example.apk", new(false, true, true, true));
        Assert.Equal(dev.CliArguments, OperatorCommands.ParseDevelopmentInstallCliArguments(dev.CliArguments).CliArguments);
        foreach (var suffix in new[] { new[] { "--verification", "exact" }, new[] { "--json" }, new[] { "--unknown" },
            new[] { "--adb" }, new[] { "--adb", "one", "--adb", "two" }, new[] { "--downgrade", "--downgrade" } })
            Assert.Throws<ArgumentException>(() => OperatorCommands.ParseDevelopmentInstallCliArguments(dev.CliArguments.Concat(suffix).ToArray()));
    }

    [Fact]
    public async Task InvalidCliPolicyRejectsBeforeAdbResolution()
    {
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(2, await CliApplication.RunAsync(["apk", "install", "--serial", "QUEST123", "--file", "example.apk",
                "--verification", "unknown", "--json", "--adb", "missing-adb.exe"]));
        }
        finally { Console.SetOut(originalOut); }
        Assert.Contains("input_rejected", output.ToString());
        Assert.DoesNotContain("missing-adb.exe", output.ToString());
        Assert.Contains("\"state_change_possible\": false", output.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        public string Apk { get; } = Path.Combine(Path.GetTempPath(), "qfm-dev-install-" + Guid.NewGuid().ToString("N") + ".apk");
        public Runner Runner { get; } = new();
        public AdbClient Client { get; }
        public Fixture()
        {
            File.WriteAllBytes(Apk, [0x50, 0x4b, 0x03, 0x04]);
            Client = new("adb", Runner, new("aapt2", "apksigner"));
        }
        public void Dispose() => File.Delete(Apk);
    }

    private sealed class Runner : IStreamingCommandRunner
    {
        public string Failure { get; set; } = "";
        public string FirstMetadata { get; set; } = Metadata;
        public int MetadataReads { get; private set; }
        public bool StagedWriteRejected { get; private set; }
        public IReadOnlyList<string>? InstallArguments { get; private set; }
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(args.ToArray());
            string text;
            var exit = 0;
            if (fileName == "aapt2") text = "package: name='com.example.app' versionCode='42' versionName='1.0+src.example'" + (Failure == "split" ? " split='config.en'" : "") + "\n";
            else if (fileName == "apksigner") text = "Signer #1 certificate SHA-256 digest: " + new string('a', 64) + "\n";
            else if (args.SequenceEqual(["devices", "-l"])) text = "List of devices attached\nQUEST123\t" + (Failure == "offline" ? "offline" : "device") + "\n" + (Failure == "duplicate-device" ? "QUEST123\tdevice\n" : "");
            else
            {
                Assert.Equal(new[] { "-s", "QUEST123" }, args.Take(2));
                if (args.Contains("install"))
                {
                    InstallArguments = args.ToArray();
                    try { File.WriteAllBytes(args[^1], [1]); } catch (IOException) { StagedWriteRejected = true; }
                    Assert.True(StagedWriteRejected);
                    if (Failure == "cancel-after-install") throw new OperationCanceledException();
                    exit = Failure == "install-failure" ? 1 : 0;
                    text = exit == 0 ? "Success\n" : "Failure\n";
                }
                else if (args.SequenceEqual(["-s", "QUEST123", "shell", "pm path 'com.example.app'"])) text = "package:/data/app/example/base.apk\n";
                else throw new InvalidOperationException("Unexpected modeled command.");
            }
            return Task.FromResult(new CommandResult(fileName, args, exit, text, "", TimeSpan.Zero));
        }
        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName, IReadOnlyList<string> args, Stream destination, long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(args.ToArray());
            Assert.Equal(new[] { "-s", "QUEST123", "shell", "dumpsys", "package", "com.example.app" }, args);
            Assert.Equal(256 * 1024, maximumBytes);
            if (Failure == "readback-failure") throw new IOException("Modeled readback failure.");
            var text = ++MetadataReads == 1 ? FirstMetadata : Failure == "changed" ? Metadata.Replace("12:00:00", "12:00:01") : Metadata;
            var bytes = Encoding.UTF8.GetBytes(text);
            await destination.WriteAsync(bytes, cancellationToken);
            return new(new(fileName, args, 0, "", "", TimeSpan.Zero), bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}
