using System.Text.Json;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class PackageStopTests
{
    private const string Serial = "QUEST123";
    private const string Package = "com.example.app";

    [Fact]
    public void FactoryAndExactCliParserRequireTheClosedConfirmedRoute()
    {
        var command = OperatorCommands.StopPackage(Serial, Package, operatorConfirmed: true);

        Assert.Equal(
            ["apk", "stop", "--serial", Serial, "--package", Package, "--confirm-package-stop"],
            command.CliArguments);
        Assert.Equal(OperatorCommandKind.StopPackage, command.Kind);
        Assert.True(command.OperatorConfirmed);
        Assert.Throws<InvalidOperationException>(() => OperatorCommands.StopPackage(Serial, Package));
        Assert.Throws<ArgumentException>(() => OperatorCommands.StopPackage(Serial, "com.example.$(bad)", true));

        var parsed = OperatorCommands.ParsePackageStopCliArguments(
            [
                "apk", "stop", "--serial", Serial, "--package", Package,
                "--confirm-package-stop", "--json"
            ]);
        Assert.Equal(command.Kind, parsed.Kind);
        Assert.Equal(command.CliArguments, parsed.CliArguments);

        Assert.Throws<ArgumentException>(() => OperatorCommands.ParsePackageStopCliArguments(
            [
                "apk", "stop", "--serial", Serial, "--package", Package,
                "--user", "current", "--confirm-package-stop", "--json"
            ]));
        Assert.Throws<ArgumentException>(() => OperatorCommands.ParsePackageStopCliArguments(
            ["apk", "stop", "--serial", Serial, "--package", Package, "--json"]));
        Assert.Throws<ArgumentException>(() => OperatorCommands.ParsePackageStopCliArguments(
            [
                "apk", "STOP", "--serial", Serial, "--package", Package,
                "--confirm-package-stop", "--json"
            ]));
    }

    [Fact]
    public async Task StopPackage_UsesFixedCurrentUserDispatchAndBothPackageChecks()
    {
        var runner = StopRunner(
            activities: "mResumedActivity: ActivityRecord{1 u0 com.example.other/.Main t1}\n",
            pidof: Result(1));
        var client = new AdbClient("adb-test", runner);

        var result = await client.StopPackageAsync(Serial, Package);

        Assert.True(result.PackagePresentBeforeDispatch);
        Assert.True(result.PackagePresentAfterDispatch);
        Assert.True(result.Quiescence.IsQuiescent);
        Assert.Equal(
            [
                ["-s", Serial, "shell", "pm path --user current 'com.example.app'"],
                ["-s", Serial, "shell", "am", "force-stop", "--user", "current", Package],
                ["-s", Serial, "shell", "pm path --user current 'com.example.app'"],
                ["-s", Serial, "shell", "dumpsys", "activity", "activities"],
                ["-s", Serial, "shell", "pidof", Package]
            ],
            runner.Calls.Select(static call => call.Arguments));
        Assert.DoesNotContain(runner.Calls.SelectMany(static call => call.Arguments),
            static argument => argument.Contains("shell -c", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StopPackage_UsesFixtureMatrixForQuiescenceAndDamagedReadback()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "QuestIonAbleFileManager.Core.Tests",
            "Fixtures",
            "package-stop-quiescence.v1.json")));
        var root = fixture.RootElement;
        Assert.Equal("questionable.file_manager.package_stop_quiescence_fixture.v1", root.GetProperty("schema").GetString());
        Assert.Equal(Package, root.GetProperty("package").GetString());

        foreach (var testCase in root.GetProperty("cases").EnumerateArray())
        {
            var id = testCase.GetProperty("id").GetString()!;
            var expected = testCase.GetProperty("expected").GetString();
            var pidof = testCase.GetProperty("pidof");
            var runner = StopRunner(
                testCase.GetProperty("activities").GetString()!,
                Result(
                    pidof.GetProperty("exitCode").GetInt32(),
                    pidof.GetProperty("stdout").GetString()!,
                    pidof.GetProperty("stderr").GetString()!));
            var client = new AdbClient("adb-test", runner);

            if (expected == "readback-rejected")
            {
                var exception = await Assert.ThrowsAsync<PackageStopReadbackException>(
                    () => client.StopPackageAsync(Serial, Package));
                Assert.NotNull(exception.InnerException);
                continue;
            }

            var result = await client.StopPackageAsync(Serial, Package);
            Assert.Equal(expected == "quiescent", result.Quiescence.IsQuiescent);
            if (id == "process-present") Assert.Equal([345], result.Quiescence.ProcessIds);
            if (id == "foreground-target") Assert.Equal(["com.example.app/com.example.app.Main"], result.Quiescence.ForegroundComponents);
            if (id == "top-resumed-target") Assert.Equal(["com.example.app/com.example.app.Main"], result.Quiescence.TopResumedComponents);
            if (id == "target-after-unrelated-component") Assert.Equal(["com.example.app/com.example.app.Second"], result.Quiescence.ForegroundComponents);
        }
    }

    [Fact]
    public async Task StopPackage_RejectsAbsentPackageBeforeDispatch()
    {
        var runner = new RecordingCommandRunner((_, arguments) =>
            arguments.SequenceEqual(["-s", Serial, "shell", "pm path --user current 'com.example.app'"])
                ? Result(1)
                : arguments.SequenceEqual(["-s", Serial, "shell", "pm list packages --user current 'com.example.app'"])
                    ? Success()
                    : Failure(arguments));
        var client = new AdbClient("adb-test", runner);

        await Assert.ThrowsAsync<PackageNotInstalledException>(() => client.StopPackageAsync(Serial, Package));
        Assert.DoesNotContain(runner.Calls, static call => call.Arguments.Contains("force-stop"));
    }

    [Fact]
    public async Task StopPackage_RejectsDamagedPreStopPackageOutputBeforeDispatch()
    {
        var runner = new RecordingCommandRunner((_, arguments) =>
            arguments.SequenceEqual(["-s", Serial, "shell", "pm path --user current 'com.example.app'"])
                ? Result(0, "package:/data/app/example/base.apk\n", "unexpected warning")
                : Failure(arguments));
        var client = new AdbClient("adb-test", runner);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.StopPackageAsync(Serial, Package));
        Assert.DoesNotContain(runner.Calls, static call => call.Arguments.Contains("force-stop"));
    }

    [Fact]
    public async Task StopPackage_RejectsOtherUserPackageEvidenceForBothProofs()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "QuestIonAbleFileManager.Core.Tests",
            "Fixtures",
            "package-stop-quiescence.v1.json")));

        foreach (var testCase in fixture.RootElement
                     .GetProperty("currentUserPackageProofCases")
                     .EnumerateArray())
        {
            var id = testCase.GetProperty("id").GetString()!;
            var currentUserPaths = testCase.GetProperty("currentUserPackagePaths")
                .EnumerateArray().Select(Result).ToArray();
            var currentUserLists = testCase.GetProperty("currentUserPackageLists")
                .EnumerateArray().Select(Result).ToArray();
            var otherUserPath = Result(testCase.GetProperty("unscopedOtherUserPackagePath"));
            var otherUserList = Result(testCase.GetProperty("unscopedOtherUserPackageList"));
            var pathCalls = 0;
            var listCalls = 0;
            var runner = new RecordingCommandRunner((_, arguments) =>
            {
                if (arguments.SequenceEqual(
                        ["-s", Serial, "shell", "pm path --user current 'com.example.app'"]))
                {
                    return currentUserPaths[pathCalls++];
                }
                if (arguments.SequenceEqual(
                        ["-s", Serial, "shell", "pm list packages --user current 'com.example.app'"]))
                {
                    return currentUserLists[listCalls++];
                }
                // These emulate an install in a different Android user. The
                // route must never consult them as proof for --user current.
                if (arguments.SequenceEqual(["-s", Serial, "shell", "pm path 'com.example.app'"]))
                    return otherUserPath;
                if (arguments.SequenceEqual(["-s", Serial, "shell", "pm list packages 'com.example.app'"]))
                    return otherUserList;
                if (arguments.Contains("force-stop")) return Success();
                if (arguments.SequenceEqual(["-s", Serial, "shell", "dumpsys", "activity", "activities"])) return Success();
                if (arguments.SequenceEqual(["-s", Serial, "shell", "pidof", Package])) return Result(1);
                return Failure(arguments);
            });
            var client = new AdbClient("adb-test", runner);

            if (testCase.GetProperty("expected").GetString() == "pre-dispatch-absent")
            {
                await Assert.ThrowsAsync<PackageNotInstalledException>(
                    () => client.StopPackageAsync(Serial, Package));
                Assert.DoesNotContain(runner.Calls, static call => call.Arguments.Contains("force-stop"));
            }
            else
            {
                await Assert.ThrowsAsync<PackageStopReadbackException>(
                    () => client.StopPackageAsync(Serial, Package));
                Assert.Contains(runner.Calls, static call => call.Arguments.Contains("force-stop"));
            }

            Assert.False(
                runner.Calls.Any(static call =>
                    call.Arguments.SequenceEqual(
                        ["-s", Serial, "shell", "pm path 'com.example.app'"]) ||
                    call.Arguments.SequenceEqual(
                        ["-s", Serial, "shell", "pm list packages 'com.example.app'"])),
                $"{id} consulted unscoped package evidence.");
        }
    }

    [Fact]
    public async Task StopPackage_PreservesDispatchAndPostDispatchUncertainty()
    {
        var dispatchFailure = new AdbClient("adb-test", new RecordingCommandRunner((_, arguments) =>
            arguments.Contains("force-stop") ? Result(1, stderr: "transport lost") : Success("package:/data/app/example/base.apk\n")));
        await Assert.ThrowsAsync<PackageStopDispatchException>(() => dispatchFailure.StopPackageAsync(Serial, Package));

        var postPackagePathCalls = 0;
        var postAbsent = new AdbClient("adb-test", new RecordingCommandRunner((_, arguments) =>
        {
            if (arguments.Contains("force-stop")) return Success();
            if (arguments.SequenceEqual(["-s", Serial, "shell", "pm path --user current 'com.example.app'"]))
            {
                return ++postPackagePathCalls == 1
                    ? Success("package:/data/app/example/base.apk\n")
                    : Result(1);
            }
            if (arguments.SequenceEqual(["-s", Serial, "shell", "pm list packages --user current 'com.example.app'"])) return Success();
            return Failure(arguments);
        }));
        await Assert.ThrowsAsync<PackageStopReadbackException>(() => postAbsent.StopPackageAsync(Serial, Package));

        var cancelledAfterDispatch = new AdbClient("adb-test", new RecordingCommandRunner((_, arguments) =>
        {
            if (arguments.Contains("force-stop")) throw new OperationCanceledException();
            return Success("package:/data/app/example/base.apk\n");
        }));
        await Assert.ThrowsAsync<PackageStopDispatchException>(() => cancelledAfterDispatch.StopPackageAsync(Serial, Package));

        var timedOutAfterDispatch = new AdbClient("adb-test", new RecordingCommandRunner((_, arguments) =>
        {
            if (arguments.Contains("force-stop")) throw new TimeoutException();
            return Success("package:/data/app/example/base.apk\n");
        }));
        await Assert.ThrowsAsync<PackageStopDispatchException>(() => timedOutAfterDispatch.StopPackageAsync(Serial, Package));
    }

    [Fact]
    public async Task ExecutorEmitsPendingAndConfirmedQuiescenceReceiptsWithoutReadinessClaims()
    {
        var confirmed = new OperatorCommandExecutor(new AdbClient(
            "adb-test",
            StopRunner("mResumedActivity: ActivityRecord{1 u0 com.example.other/.Main t1}\n", Result(1))));
        var complete = await confirmed.ExecuteAsync(OperatorCommands.StopPackage(Serial, Package, true));

        Assert.Equal(OperatorMutationStage.Confirmed, complete.MutationReceipt!.Stage);
        Assert.Equal(
            [OperatorMutationStage.Sent, OperatorMutationStage.Pending, OperatorMutationStage.Confirmed],
            complete.MutationReceipt.Transitions.Select(static transition => transition.Stage));
        Assert.DoesNotContain("readiness", complete.MutationReceipt.ObservedState, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("openxr", complete.MutationReceipt.ObservedState, StringComparison.OrdinalIgnoreCase);

        var pending = new OperatorCommandExecutor(new AdbClient(
            "adb-test",
            StopRunner("mResumedActivity: ActivityRecord{1 u0 com.example.other/.Main t1}\n", Success("42\n"))));
        var incomplete = await pending.ExecuteAsync(OperatorCommands.StopPackage(Serial, Package, true));
        Assert.Equal(OperatorMutationStage.Pending, incomplete.MutationReceipt!.Stage);
    }

    private static RecordingCommandRunner StopRunner(string activities, CommandResult pidof) =>
        new((_, arguments) =>
        {
            if (arguments.SequenceEqual(["-s", Serial, "shell", "pm path --user current 'com.example.app'"]))
                return Success("package:/data/app/example/base.apk\n");
            if (arguments.SequenceEqual(
                    ["-s", Serial, "shell", "am", "force-stop", "--user", "current", Package]))
                return Success();
            if (arguments.SequenceEqual(["-s", Serial, "shell", "dumpsys", "activity", "activities"]))
                return Success(activities);
            if (arguments.SequenceEqual(["-s", Serial, "shell", "pidof", Package]))
                return pidof;
            return Failure(arguments);
        });

    private static CommandResult Success(string output = "") => Result(0, output);

    private static CommandResult Result(int exitCode, string output = "", string stderr = "") =>
        new("adb-test", [], exitCode, output, stderr, TimeSpan.Zero);

    private static CommandResult Result(JsonElement result) =>
        Result(
            result.GetProperty("exitCode").GetInt32(),
            result.GetProperty("stdout").GetString()!,
            result.GetProperty("stderr").GetString()!);

    private static CommandResult Failure(IReadOnlyList<string> arguments) =>
        Result(1, stderr: "unexpected command: " + string.Join(" ", arguments));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "QuestIonAbleFileManager.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the File Manager repository root.");
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
}
