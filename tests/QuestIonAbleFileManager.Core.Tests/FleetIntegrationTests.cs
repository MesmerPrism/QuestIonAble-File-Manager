using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class FleetIntegrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Capabilities_DisabledIsSideEffectFreeAndAdvertisesNoOperations()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), $"qfm-missing-{Guid.NewGuid():N}");
        var adapter = new FleetIntegrationAdapter(
            new FleetIntegrationSettings(
                FleetIntegrationStatus.Disabled,
                missingRoot,
                null,
                "disabled for test"),
            utcNow: () => Now);

        var capability = adapter.GetCapabilities();

        Assert.Equal(FleetIntegrationStatus.Disabled, capability.State);
        Assert.Empty(capability.Operations);
        Assert.Equal(0, capability.MaximumConcurrentOperations);
        Assert.False(Directory.Exists(missingRoot));
    }

    [Theory]
    [InlineData(FleetIntegrationStatus.Disabled)]
    [InlineData(FleetIntegrationStatus.Absent)]
    [InlineData(FleetIntegrationStatus.Unsupported)]
    [InlineData(FleetIntegrationStatus.Unavailable)]
    public void Capabilities_PreserveExplicitNonReadyState(FleetIntegrationStatus state)
    {
        var adapter = new FleetIntegrationAdapter(
            new FleetIntegrationSettings(state, null, null, state.ToString()),
            utcNow: () => Now);

        var capability = adapter.GetCapabilities();

        Assert.Equal(state, capability.State);
        Assert.Empty(capability.Operations);
        Assert.NotNull(capability.Reason);
    }

    [Fact]
    public async Task Observe_BindsOnlyTheExactReadySerial()
    {
        using var root = new TemporaryDirectory();
        var runner = new ScriptedRunner((_, arguments, _) =>
            arguments.SequenceEqual(["devices", "-l"])
                ? Success(
                    "List of devices attached\n" +
                    "QUEST123 device product:eureka model:Quest_3\n" +
                    "OTHER456 unauthorized product:eureka model:Quest_2\n")
                : Failure("unexpected"));
        var adapter = CreateReadyAdapter(root.Path, runner);

        var observation = await adapter.ObserveAsync("QUEST123");

        Assert.Equal("QUEST123", observation.Serial);
        Assert.Equal("usb", observation.Transport);
        Assert.Equal("device", observation.State);
        Assert.Equal(
            FleetIntegrationAdapter.ComputeObservationId(
                observation.AdapterEpoch,
                observation.Serial,
                observation.Transport,
                observation.State,
                observation.ObservedAtUtc),
            observation.ObservationId);
        var unauthorized = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.ObserveAsync("OTHER456"));
        Assert.Equal("device_unauthorized", unauthorized.Code);
    }

    [Fact]
    public async Task InvokeList_RediscoversExactSerialBeforeAndAfterAndBoundsEntries()
    {
        using var root = new TemporaryDirectory();
        var runner = new ScriptedRunner((_, arguments, _) =>
        {
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n");
            }
            if (arguments.Count == 4 &&
                arguments[0] == "-s" &&
                arguments[1] == "QUEST123" &&
                arguments[2] == "shell" &&
                arguments[3].Contains("root=$(realpath '/sdcard')", StringComparison.Ordinal) &&
                arguments[3].Contains(
                    "candidate=$(realpath '/sdcard/Download')",
                    StringComparison.Ordinal) &&
                arguments[3].Contains(
                    "for entry in /proc/self/fd/3/*",
                    StringComparison.Ordinal) &&
                arguments[3].Contains(
                    "qfm-integration:unsupported-entry-type",
                    StringComparison.Ordinal) &&
                arguments[3].Contains(
                    "[ \"$count\" -gt 10 ]",
                    StringComparison.Ordinal))
            {
                return Success("Folder/\nreport.txt\n");
            }
            return Failure("unexpected");
        });
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "list",
            relativePath: "Download",
            maximumEntries: 10);

        var result = await adapter.InvokeAsync(request);

        Assert.Equal(2, result.EntryCount);
        Assert.Collection(
            result.Entries!,
            entry =>
            {
                Assert.Equal("Folder", entry.Name);
                Assert.Equal("Download/Folder", entry.RelativePath);
                Assert.Equal("directory", entry.EntryType);
            },
            entry =>
            {
                Assert.Equal("report.txt", entry.Name);
                Assert.Equal("Download/report.txt", entry.RelativePath);
                Assert.Equal("file", entry.EntryType);
            });
        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(["devices", "-l"], runner.Calls[0].Arguments);
        Assert.Equal("-s", runner.Calls[1].Arguments[0]);
        Assert.Equal("QUEST123", runner.Calls[1].Arguments[1]);
        Assert.Equal(["devices", "-l"], runner.Calls[2].Arguments);
    }

    [Fact]
    public async Task InvokeList_RejectsTheAdvertisedExtraRowInsteadOfTruncating()
    {
        using var root = new TemporaryDirectory();
        var runner = new ScriptedRunner((_, arguments, _) =>
        {
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n");
            }
            if (arguments[2] == "shell")
            {
                Assert.Contains("[ \"$count\" -gt 1 ]", arguments[3], StringComparison.Ordinal);
                return new CommandResult(
                    "adb-test",
                    arguments.ToArray(),
                    50,
                    "first.txt\n",
                    "qfm-integration:maximum-entries\n",
                    TimeSpan.Zero);
            }
            return Failure("unexpected");
        });
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "list",
            relativePath: "",
            maximumEntries: 1);

        var exception = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal("entry_limit_exceeded", exception.Code);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Theory]
    [InlineData("qfm-integration:unsupported-entry-type", "remote_entry_type_unsupported")]
    [InlineData("qfm-integration:entry-name-unrepresentable", "remote_entry_name_rejected")]
    public async Task InvokeList_RejectsEntriesWithoutSafeV1Semantics(
        string marker,
        string expectedCode)
    {
        using var root = new TemporaryDirectory();
        var runner = new ScriptedRunner((_, arguments, _) =>
        {
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n");
            }
            if (arguments[2] == "shell")
            {
                return new CommandResult(
                    "adb-test",
                    arguments.ToArray(),
                    51,
                    string.Empty,
                    marker + "\n",
                    TimeSpan.Zero);
            }
            return Failure("unexpected");
        });
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "list",
            relativePath: "Download",
            maximumEntries: 10);

        var exception = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(FleetIntegrationStatus.Rejected, exception.Status);
    }

    [Fact]
    public async Task InvokePull_StagesWithoutOverwriteAndReturnsSizeAndSha256()
    {
        using var root = new TemporaryDirectory();
        var expectedBytes = new byte[] { 1, 3, 3, 7, 9 };
        var runner = new ScriptedRunner((_, arguments, _) =>
        {
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n");
            }
            return Failure("unexpected");
        }, StreamBytes(expectedBytes));
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "pull",
            relativePath: "Download/report.bin",
            maximumBytes: 1_024);

        var result = await adapter.InvokeAsync(request);

        var expectedPath = System.IO.Path.Combine(
            root.Path,
            "operations",
            request.OperationId,
            "payload.bin");
        Assert.Equal(expectedPath, result.LocalArtifactPath);
        Assert.Equal(expectedBytes.Length, result.SizeBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant(),
            result.Sha256);
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(expectedPath));
        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(["devices", "-l"], runner.Calls[^1].Arguments);

        var collision = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));
        Assert.Equal("destination_collision", collision.Code);
    }

    [Fact]
    public async Task InvokePull_SizeViolationRemovesOperationOwnedStaging()
    {
        using var root = new TemporaryDirectory();
        var runner = CreatePullRunner(new byte[] { 1, 2, 3, 4, 5 });
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "pull",
            relativePath: "Download/report.bin",
            maximumBytes: 4);

        var exception = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal("maximum_bytes_exceeded", exception.Code);
        Assert.False(Directory.Exists(
            System.IO.Path.Combine(root.Path, "operations", request.OperationId)));
        Assert.True(File.Exists(
            System.IO.Path.Combine(root.Path, "operations", request.OperationId + ".lock")));

        var replay = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));
        Assert.Equal("destination_collision", replay.Code);
    }

    [Fact]
    public async Task InvokePull_LostSerialAfterTransferRejectsAndCleansStaging()
    {
        using var root = new TemporaryDirectory();
        var discoveryCount = 0;
        var runner = new ScriptedRunner((_, arguments, _) =>
        {
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                discoveryCount++;
                return discoveryCount == 1
                    ? Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n")
                    : Success("List of devices attached\n");
            }
            return Failure("unexpected");
        }, StreamBytes([1, 2, 3]));
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "pull",
            relativePath: "Download/report.bin",
            maximumBytes: 100);

        var exception = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal("device_absent", exception.Code);
        Assert.False(Directory.Exists(
            System.IO.Path.Combine(root.Path, "operations", request.OperationId)));
    }

    [Fact]
    public async Task CommandRunner_HardStopsOversizedBinaryOutputBeforeWritingPastLimit()
    {
        var command = Environment.GetEnvironmentVariable("COMSPEC")
            ?? System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var runner = new CommandRunner();
        using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<FleetTransferLimitException>(
            () => runner.RunToStreamAsync(
                command,
                [
                    "/d",
                    "/s",
                    "/c",
                    "for /l %i in (1,1,4096) do @<nul set /p =A"
                ],
                destination,
                maximumBytes: 128,
                timeout: TimeSpan.FromSeconds(10)));

        Assert.Equal(128, exception.MaximumBytes);
        Assert.True(destination.Length <= 128);
    }

    [Fact]
    public async Task InvokePull_RejectsRemoteCanonicalSymlinkEscapeAndCleansStaging()
    {
        using var root = new TemporaryDirectory();
        var runner = new ScriptedRunner(
            (_, arguments, _) =>
                arguments.SequenceEqual(["devices", "-l"])
                    ? Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n")
                    : Failure("unexpected"),
            (_, arguments, _, _, _, _) =>
            {
                var commandResult = new CommandResult(
                    "adb-test",
                    arguments.ToArray(),
                    42,
                    string.Empty,
                    "qfm-integration:path-indirection\n",
                    TimeSpan.Zero);
                return Task.FromResult(new StreamingCommandResult(
                    commandResult,
                    0,
                    Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant()));
            });
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "pull",
            relativePath: "Download/link/report.bin",
            maximumBytes: 100);

        var exception = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal("remote_path_indirection", exception.Code);
        Assert.False(Directory.Exists(
            System.IO.Path.Combine(root.Path, "operations", request.OperationId)));
    }

    [Fact]
    public async Task InvokeList_RejectsRemoteCanonicalSymlinkEscape()
    {
        using var root = new TemporaryDirectory();
        var runner = new ScriptedRunner((_, arguments, _) =>
        {
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n");
            }
            if (arguments[2] == "shell")
            {
                return new CommandResult(
                    "adb-test",
                    arguments.ToArray(),
                    42,
                    string.Empty,
                    "qfm-integration:path-indirection\n",
                    TimeSpan.Zero);
            }
            return Failure("unexpected");
        });
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "list",
            relativePath: "Download/link",
            maximumEntries: 10);

        var exception = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal("remote_path_indirection", exception.Code);
    }

    [Fact]
    public async Task InvokePull_RejectsFinalFileHardlinkRace()
    {
        using var root = new TemporaryDirectory();
        var bytes = new byte[] { 7, 8, 9 };
        var hardlinkPath = System.IO.Path.Combine(root.Path, "attacker-hardlink.bin");
        var outputPath = System.IO.Path.Combine(
            root.Path,
            "operations",
            "operation01",
            "payload.bin");
        var runner = new ScriptedRunner(
            (_, arguments, _) =>
                arguments.SequenceEqual(["devices", "-l"])
                    ? Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n")
                    : Failure("unexpected"),
            async (fileName, arguments, _, destination, _, cancellationToken) =>
            {
                await destination.WriteAsync(bytes, cancellationToken);
                if (!CreateHardLink(hardlinkPath, outputPath, IntPtr.Zero))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The adversarial hardlink could not be created.");
                }
                return StreamingSuccess(fileName, arguments, bytes);
            });
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "pull",
            relativePath: "Download/report.bin",
            maximumBytes: 100);

        var exception = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal("final_output_hardlink_rejected", exception.Code);
        Assert.False(Directory.Exists(
            System.IO.Path.Combine(root.Path, "operations", request.OperationId)));
    }

    [Fact]
    public async Task InvokePull_BlocksFinalFileDeleteSubstitutionRace()
    {
        using var root = new TemporaryDirectory();
        var bytes = new byte[] { 4, 5, 6 };
        var fileAttackBlocked = false;
        var outputPath = System.IO.Path.Combine(
            root.Path,
            "operations",
            "operation01",
            "payload.bin");
        var runner = new ScriptedRunner(
            (_, arguments, _) =>
                arguments.SequenceEqual(["devices", "-l"])
                    ? Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n")
                    : Failure("unexpected"),
            async (fileName, arguments, _, destination, _, cancellationToken) =>
            {
                await destination.WriteAsync(bytes, cancellationToken);
                try
                {
                    File.Delete(outputPath);
                }
                catch (IOException)
                {
                    fileAttackBlocked = true;
                }

                return StreamingSuccess(fileName, arguments, bytes);
            });
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "pull",
            relativePath: "Download/report.bin",
            maximumBytes: 100);

        var result = await adapter.InvokeAsync(request);

        Assert.True(fileAttackBlocked);
        Assert.Equal(bytes.Length, result.SizeBytes);
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task InvokePull_BlocksOrRejectsParentJunctionSubstitutionRace()
    {
        using var root = new TemporaryDirectory();
        var bytes = new byte[] { 4, 5, 6 };
        var parentAttackBlocked = false;
        var outputPath = System.IO.Path.Combine(
            root.Path,
            "operations",
            "operation01",
            "payload.bin");
        var runner = new ScriptedRunner(
            (_, arguments, _) =>
                arguments.SequenceEqual(["devices", "-l"])
                    ? Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n")
                    : Failure("unexpected"),
            async (fileName, arguments, _, destination, _, cancellationToken) =>
            {
                await destination.WriteAsync(bytes, cancellationToken);
                var operationRoot = Directory.GetParent(outputPath)!.FullName;
                try
                {
                    Directory.Move(operationRoot, operationRoot + "-moved");
                }
                catch (IOException)
                {
                    parentAttackBlocked = true;
                }
                return StreamingSuccess(fileName, arguments, bytes);
            });
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "pull",
            relativePath: "Download/report.bin",
            maximumBytes: 100);

        FleetIntegrationException? rejection = null;
        try
        {
            await adapter.InvokeAsync(request);
        }
        catch (FleetIntegrationException exception)
        {
            rejection = exception;
        }

        Assert.True(
            parentAttackBlocked || rejection?.Code == "local_path_identity_changed",
            $"Race was neither blocked nor rejected: {rejection?.Code}");
    }

    [Fact]
    public async Task InvokePull_CancellationAndTimeoutCleanOwnedStaging()
    {
        foreach (var timeout in new[] { false, true })
        {
            using var root = new TemporaryDirectory();
            var runner = new ScriptedRunner(
                (_, arguments, _) =>
                    arguments.SequenceEqual(["devices", "-l"])
                        ? Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n")
                        : Failure("unexpected"),
                async (_, _, _, destination, _, cancellationToken) =>
                {
                    await destination.WriteAsync(new byte[] { 1, 2 }, cancellationToken);
                    if (timeout)
                    {
                        throw new TimeoutException("stream timed out");
                    }
                    throw new OperationCanceledException(cancellationToken);
                });
            var adapter = CreateReadyAdapter(root.Path, runner);
            var request = CreateRequest(
                adapter,
                kind: "pull",
                relativePath: "Download/report.bin",
                maximumBytes: 100);

            var exception = await Assert.ThrowsAsync<FleetIntegrationException>(
                () => adapter.InvokeAsync(request));

            Assert.Equal(timeout ? "operation_timeout" : "operation_cancelled", exception.Code);
            Assert.True(exception.Retryable);
            Assert.False(Directory.Exists(
                System.IO.Path.Combine(root.Path, "operations", request.OperationId)));
        }
    }

    [Fact]
    public void Capabilities_PushIsUnadvertisedWithoutInjectedAuthority()
    {
        using var root = new TemporaryDirectory();
        var adapter = CreateReadyAdapter(
            root.Path,
            new ScriptedRunner((_, _, _) => Failure("ADB must not run")));

        var capability = adapter.GetCapabilities();

        Assert.Equal(["list", "pull"], capability.Operations);
        Assert.True(capability.RootProfiles.Single().ReadOnly);
    }

    [Fact]
    public async Task InvokePush_LocksStagedInputAndReturnsSameStreamRemoteEvidence()
    {
        using var root = new TemporaryDirectory();
        var payload = new byte[] { 7, 3, 1, 9, 5 };
        StagePushInput(root.Path, "input01", payload);
        var verifier = new ScriptedAuthorityVerifier(_ => AcceptedAuthority());
        var runner = new ScriptedRunner(
            (_, arguments, _) =>
                arguments.SequenceEqual(["devices", "-l"])
                    ? Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n")
                    : Failure("unexpected"),
            inputStreamingHandler: async (
                fileName,
                arguments,
                _,
                source,
                maximumBytes,
                cancellationToken) =>
            {
                Assert.Equal("exec-in", arguments[2]);
                Assert.Contains("set -C", arguments[5], StringComparison.Ordinal);
                Assert.Contains("qfm-integration:destination-exists", arguments[5], StringComparison.Ordinal);
                Assert.Contains(".qfm-operation01.partial", arguments[5], StringComparison.Ordinal);
                AssertAtomicPushPublicationContract(arguments[5]);
                using var buffer = new MemoryStream();
                await source.CopyToAsync(buffer, cancellationToken);
                var bytes = buffer.ToArray();
                Assert.Equal(payload, bytes);
                Assert.Equal(payload.LongLength, maximumBytes);
                var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                return new StreamingCommandResult(
                    new CommandResult(
                        fileName,
                        arguments.ToArray(),
                        0,
                        $"qfm-integration:push-complete:{bytes.LongLength}:{digest}\n",
                        string.Empty,
                        TimeSpan.Zero),
                    bytes.LongLength,
                    digest);
            });
        var adapter = CreateReadyAdapter(root.Path, runner, verifier);
        var capability = adapter.GetCapabilities();
        Assert.Equal(["list", "pull", "push"], capability.Operations);
        Assert.False(capability.RootProfiles.Single().ReadOnly);
        var request = CreatePushRequest(adapter, payload);

        var result = await adapter.InvokeAsync(request);

        Assert.Equal("push", result.Operation);
        Assert.Equal(payload.LongLength, result.SizeBytes);
        Assert.Equal(Sha256(payload), result.Sha256);
        Assert.Equal(3, verifier.Calls);
        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(["devices", "-l"], runner.Calls[0].Arguments);
        Assert.Equal(["devices", "-l"], runner.Calls[2].Arguments);
        var status = adapter.GetOperationStatus(request.OperationId);
        Assert.Equal(FleetIntegrationOperationPhase.Completed, status.Phase);
        Assert.Equal(FleetIntegrationCleanupState.Completed, status.CleanupState);
        Assert.True(status.DestinationMayExist);
        Assert.False(status.PartialMayExist);

        var replay = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));
        Assert.Equal("destination_collision", replay.Code);
    }

    [Fact]
    public async Task InvokePush_RejectsChangedAuthorityBeforeStreamAndAfterRemoteCommit()
    {
        using var root = new TemporaryDirectory();
        var payload = new byte[] { 1, 2, 3 };
        StagePushInput(root.Path, "input01", payload);
        var streamCalls = 0;
        var runner = CreatePushRunner(payload, () => streamCalls++);
        var beforeStreamVerifier = new ScriptedAuthorityVerifier(call =>
            AcceptedAuthority(call == 0 ? 'd' : 'e'));
        var beforeStreamAdapter = CreateReadyAdapter(root.Path, runner, beforeStreamVerifier);
        var beforeStreamRequest = CreatePushRequest(beforeStreamAdapter, payload);

        var beforeStream = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => beforeStreamAdapter.InvokeAsync(beforeStreamRequest));

        Assert.Equal("mutation_authority_changed", beforeStream.Code);
        Assert.Equal(0, streamCalls);
        var beforeStatus = beforeStreamAdapter.GetOperationStatus(beforeStreamRequest.OperationId);
        Assert.Equal(FleetIntegrationOperationPhase.Failed, beforeStatus.Phase);
        Assert.Equal(FleetIntegrationCleanupState.NotRequired, beforeStatus.CleanupState);
        Assert.False(beforeStatus.DestinationMayExist);

        using var secondRoot = new TemporaryDirectory();
        StagePushInput(secondRoot.Path, "input01", payload);
        var afterStreamVerifier = new ScriptedAuthorityVerifier(call =>
            AcceptedAuthority(call < 2 ? 'd' : 'e'));
        var afterStreamAdapter = CreateReadyAdapter(
            secondRoot.Path,
            CreatePushRunner(payload),
            afterStreamVerifier);
        var afterStreamRequest = CreatePushRequest(afterStreamAdapter, payload);

        var afterStream = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => afterStreamAdapter.InvokeAsync(afterStreamRequest));

        Assert.Equal("mutation_authority_changed", afterStream.Code);
        var afterStatus = afterStreamAdapter.GetOperationStatus(afterStreamRequest.OperationId);
        Assert.Equal(FleetIntegrationOperationPhase.Failed, afterStatus.Phase);
        Assert.Equal(FleetIntegrationCleanupState.NotRequired, afterStatus.CleanupState);
        Assert.True(afterStatus.DestinationMayExist);
        Assert.False(afterStatus.PartialMayExist);
    }

    [Fact]
    public async Task InvokePush_DurableCancellationUsesSameAuthorityAndReportsUncertainCleanup()
    {
        using var root = new TemporaryDirectory();
        var payload = new byte[] { 4, 4, 2, 1 };
        StagePushInput(root.Path, "input01", payload);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifier = new ScriptedAuthorityVerifier(_ => AcceptedAuthority());
        var runner = new ScriptedRunner(
            (_, arguments, _) =>
                arguments.SequenceEqual(["devices", "-l"])
                    ? Success("List of devices attached\nQUEST123 device\n")
                    : Failure("unexpected"),
            inputStreamingHandler: async (_, _, _, _, _, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
        var adapter = CreateReadyAdapter(root.Path, runner, verifier);
        var request = CreatePushRequest(adapter, payload);
        var invoke = adapter.InvokeAsync(request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var live = adapter.GetOperationStatus(request.OperationId);
        Assert.Equal(FleetIntegrationOperationPhase.Running, live.Phase);
        Assert.Equal(FleetIntegrationCleanupState.Pending, live.CleanupState);
        var untrusted = CreateReadyAdapter(
            root.Path,
            new ScriptedRunner((_, _, _) => Failure("ADB must not run")));
        var untrustedCancel = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => untrusted.RequestOperationCancellationAsync(request.OperationId));
        Assert.Equal("mutation_authority_unavailable", untrustedCancel.Code);
        Assert.False(File.Exists(Path.Combine(
            root.Path,
            "operations",
            request.OperationId,
            "cancel.request")));

        var cancel = await adapter.RequestOperationCancellationAsync(request.OperationId);
        var exception = await Assert.ThrowsAsync<FleetIntegrationException>(() => invoke);

        Assert.Equal(FleetIntegrationOperationPhase.CancelRequested, cancel.Phase);
        Assert.Equal("operation_cancelled", exception.Code);
        var status = adapter.GetOperationStatus(request.OperationId);
        Assert.Equal(FleetIntegrationOperationPhase.Cancelled, status.Phase);
        Assert.Equal(FleetIntegrationCleanupState.Unknown, status.CleanupState);
        Assert.True(status.DestinationMayExist);
        Assert.True(status.PartialMayExist);
    }

    [Fact]
    public async Task DurableStatusWaitsForConcurrentJournalWriterToCommit()
    {
        using var root = new TemporaryDirectory();
        var payload = new byte[] { 4, 4, 2, 1 };
        StagePushInput(root.Path, "input01", payload);
        var adapter = CreateReadyAdapter(root.Path, CreatePushRunner(payload));
        var request = CreatePushRequest(adapter, payload);
        var store = new FleetPushOperationStore(root.Path, () => Now);
        using var operation = store.Begin(request, new string('d', 64));
        operation.Append(
            FleetIntegrationOperationPhase.Running,
            FleetIntegrationCleanupState.Pending,
            null,
            null,
            "Running",
            destinationMayExist: true,
            partialMayExist: true);
        var status = FleetPushOperationStore.CreateStatus(
            request,
            FleetIntegrationOperationPhase.CancelRequested,
            FleetIntegrationCleanupState.Pending,
            null,
            null,
            destinationMayExist: true,
            partialMayExist: true,
            Now,
            "Cancellation requested");
        var entry = new FleetPushJournalEntry(
            "questionable.file_manager.integration.push_journal.v1",
            3,
            status,
            operation.RequestDigest,
            request.Operation.LocalArtifactPath!,
            operation.RemotePartialName,
            operation.VerifiedAuthorityDigest);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, options);
        var path = Path.Combine(operation.OperationRoot, "state-0003.json");
        Task<FleetIntegrationOperationStatusSnapshot> read;
        using (var writer = FleetWindowsFileSafety.CreateNewOwnedFile(path))
        {
            read = Task.Run(() => store.ReadStatus(request.OperationId));
            await Task.Delay(50);
            writer.Write(bytes);
            writer.Flush(flushToDisk: true);
            FleetWindowsFileSafety.ValidateFile(writer.SafeFileHandle, path, requireSingleLink: true);
        }

        var observed = await read.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(FleetIntegrationOperationPhase.CancelRequested, observed.Phase);
        Assert.Equal(FleetIntegrationCleanupState.Pending, observed.CleanupState);
    }

    [Fact]
    public async Task InvokePush_NoOverwriteRaceAndJournalSubstitutionFailClosed()
    {
        using var root = new TemporaryDirectory();
        var payload = new byte[] { 8, 8 };
        StagePushInput(root.Path, "input01", payload);
        var verifier = new ScriptedAuthorityVerifier(_ => AcceptedAuthority());
        var runner = new ScriptedRunner(
            (_, arguments, _) =>
                arguments.SequenceEqual(["devices", "-l"])
                    ? Success("List of devices attached\nQUEST123 device\n")
                    : Failure("unexpected"),
            inputStreamingHandler: (fileName, arguments, _, _, _, _) =>
            {
                AssertAtomicPushPublicationContract(arguments[5]);
                Assert.Contains(
                    "elif [ -e \"$candidate\" ] || [ -L \"$candidate\" ]; then",
                    arguments[5],
                    StringComparison.Ordinal);
                var publication = arguments[5].IndexOf(
                    "ln -T -- \"$partial\" \"$candidate\"",
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    ">\"$candidate\"",
                    arguments[5][..publication],
                    StringComparison.Ordinal);
                return Task.FromResult(new StreamingCommandResult(
                    new CommandResult(
                        fileName,
                        arguments.ToArray(),
                        61,
                        string.Empty,
                        "qfm-integration:destination-exists\n",
                        TimeSpan.Zero),
                    0,
                    Sha256(Array.Empty<byte>())));
            });
        var adapter = CreateReadyAdapter(root.Path, runner, verifier);
        var request = CreatePushRequest(adapter, payload);

        var collision = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal("remote_destination_collision", collision.Code);
        var status = adapter.GetOperationStatus(request.OperationId);
        Assert.Equal(FleetIntegrationOperationPhase.CleanupRequired, status.Phase);
        Assert.Equal(FleetIntegrationCleanupState.Unknown, status.CleanupState);
        Assert.True(status.DestinationMayExist);
        Assert.True(status.PartialMayExist);

        var lastJournal = Directory.EnumerateFiles(
                Path.Combine(root.Path, "operations", request.OperationId),
                "state-*.json")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Last();
        var originalJournal = File.ReadAllText(lastJournal);
        var damaged = originalJournal.Replace(
            "\"operationId\":\"operation01\"",
            "\"operationId\":\"foreign01\"",
            StringComparison.Ordinal);
        File.WriteAllText(lastJournal, damaged);
        var journal = Assert.Throws<FleetIntegrationException>(
            () => adapter.GetOperationStatus(request.OperationId));
        Assert.Equal("operation_journal_invalid", journal.Code);
        File.WriteAllText(lastJournal, originalJournal);
        var reservationPath = Path.Combine(
            root.Path,
            "operations",
            request.OperationId + ".lock");
        var damagedReservation = File.ReadAllText(reservationPath).Replace(
            "\"requestId\":\"request01\"",
            "\"requestId\":\"foreign01\"",
            StringComparison.Ordinal);
        File.WriteAllText(reservationPath, damagedReservation);
        var reservation = Assert.Throws<FleetIntegrationException>(
            () => adapter.GetOperationStatus(request.OperationId));
        Assert.Equal("operation_reservation_invalid", reservation.Code);
    }

    [Fact]
    public async Task InvokePush_RejectsChangedSourceBeforeRemoteStream()
    {
        using var root = new TemporaryDirectory();
        var payload = new byte[] { 2, 4, 6 };
        StagePushInput(root.Path, "input01", payload);
        var verifier = new ScriptedAuthorityVerifier(_ => AcceptedAuthority());
        var runner = CreatePushRunner(payload);
        var adapter = CreateReadyAdapter(root.Path, runner, verifier);
        var request = CreatePushRequest(adapter, payload) with
        {
            Operation = CreatePushRequest(adapter, payload).Operation with
            {
                ExpectedSha256 = new string('a', 64)
            }
        };

        var mismatch = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal("push_source_digest_mismatch", mismatch.Code);
        Assert.Single(runner.Calls);
        Assert.Equal(["devices", "-l"], runner.Calls[0].Arguments);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "operations")));
    }

    [Fact]
    public async Task InvokePush_LossOfExactSerialAfterRemoteEvidenceDoesNotRetry()
    {
        using var root = new TemporaryDirectory();
        var payload = new byte[] { 6, 5, 4 };
        StagePushInput(root.Path, "input01", payload);
        var runner = new ScriptedRunner(
            (_, arguments, callIndex) =>
            {
                if (arguments.SequenceEqual(["devices", "-l"]))
                {
                    return callIndex == 0
                        ? Success("List of devices attached\nQUEST123 device\n")
                        : Success("List of devices attached\n");
                }
                return Failure("unexpected");
            },
            inputStreamingHandler: CreatePushInputHandler(payload));
        var verifier = new ScriptedAuthorityVerifier(_ => AcceptedAuthority());
        var adapter = CreateReadyAdapter(root.Path, runner, verifier);
        var request = CreatePushRequest(adapter, payload);

        var missing = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal("device_absent", missing.Code);
        Assert.Equal(3, runner.Calls.Count);
        var status = adapter.GetOperationStatus(request.OperationId);
        Assert.Equal(FleetIntegrationOperationPhase.Failed, status.Phase);
        Assert.Equal(FleetIntegrationCleanupState.NotRequired, status.CleanupState);
        Assert.True(status.DestinationMayExist);
        Assert.False(status.PartialMayExist);
    }

    [Fact]
    public async Task InvokePush_RevocationAndExpiryDuringStreamCancelWithoutAutomaticRetry()
    {
        using var root = new TemporaryDirectory();
        var payload = new byte[] { 9, 1, 1 };
        StagePushInput(root.Path, "input01", payload);
        using var revoked = new CancellationTokenSource();
        var revocationVerifier = new ScriptedAuthorityVerifier(call =>
            AcceptedAuthority() with
            {
                RevocationToken = call == 1 ? revoked.Token : default
            });
        var revocationRunner = new ScriptedRunner(
            (_, arguments, _) =>
                arguments.SequenceEqual(["devices", "-l"])
                    ? Success("List of devices attached\nQUEST123 device\n")
                    : Failure("unexpected"),
            inputStreamingHandler: async (_, _, _, _, _, cancellationToken) =>
            {
                revoked.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
        var revocationAdapter = CreateReadyAdapter(
            root.Path,
            revocationRunner,
            revocationVerifier);
        var revocationRequest = CreatePushRequest(revocationAdapter, payload);

        var revokedResult = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => revocationAdapter.InvokeAsync(revocationRequest));

        Assert.Equal("operation_cancelled", revokedResult.Code);
        var revokedStatus = revocationAdapter.GetOperationStatus(revocationRequest.OperationId);
        Assert.Equal(FleetIntegrationCleanupState.Unknown, revokedStatus.CleanupState);
        Assert.True(revokedStatus.DestinationMayExist);
        Assert.True(revokedStatus.PartialMayExist);

        using var expiryRoot = new TemporaryDirectory();
        StagePushInput(expiryRoot.Path, "input01", payload);
        var expiryVerifier = new ScriptedAuthorityVerifier(_ => AcceptedAuthority());
        var expiryRunner = new ScriptedRunner(
            (_, arguments, _) =>
                arguments.SequenceEqual(["devices", "-l"])
                    ? Success("List of devices attached\nQUEST123 device\n")
                    : Failure("unexpected"),
            inputStreamingHandler: async (_, _, _, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
        var expiryAdapter = CreateReadyAdapter(expiryRoot.Path, expiryRunner, expiryVerifier);
        var expiryRequest = CreatePushRequest(expiryAdapter, payload);
        expiryRequest = expiryRequest with
        {
            ExpiresAtUtc = Now + TimeSpan.FromMilliseconds(600),
            MutationAuthority = expiryRequest.MutationAuthority! with
            {
                ExpiresAtUtc = Now + TimeSpan.FromMilliseconds(600)
            }
        };

        var expired = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => expiryAdapter.InvokeAsync(expiryRequest));

        Assert.Equal("mutation_authority_expired_during_operation", expired.Code);
        var expiredStatus = expiryAdapter.GetOperationStatus(expiryRequest.OperationId);
        Assert.Equal(FleetIntegrationOperationPhase.Cancelled, expiredStatus.Phase);
        Assert.Equal(FleetIntegrationCleanupState.Unknown, expiredStatus.CleanupState);
        Assert.True(expiredStatus.DestinationMayExist);
        Assert.True(expiredStatus.PartialMayExist);
    }

    [Fact]
    public async Task OperationStatus_DeadExclusiveOwnerBecomesRecoveryWithoutRemoteAction()
    {
        using var root = new TemporaryDirectory();
        var payload = new byte[] { 3, 3, 7 };
        StagePushInput(root.Path, "input01", payload);
        var runner = CreatePushRunner(payload);
        var adapter = CreateReadyAdapter(
            root.Path,
            runner,
            new ScriptedAuthorityVerifier(_ => AcceptedAuthority()));
        var request = CreatePushRequest(adapter, payload);
        await adapter.InvokeAsync(request);
        var operationRoot = Path.Combine(root.Path, "operations", request.OperationId);
        var terminal = Directory.EnumerateFiles(operationRoot, "state-*.json")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Last();
        File.Delete(terminal);
        var priorCalls = runner.Calls.Count;

        var recovered = adapter.GetOperationStatus(request.OperationId);

        Assert.Equal(FleetIntegrationOperationPhase.RecoveryRequired, recovered.Phase);
        Assert.Equal(FleetIntegrationCleanupState.Unknown, recovered.CleanupState);
        Assert.True(recovered.DestinationMayExist);
        Assert.True(recovered.PartialMayExist);
        Assert.Equal(priorCalls, runner.Calls.Count);
        Assert.False(File.Exists(Path.Combine(operationRoot, "cancel.request")));
    }

    [Fact]
    public async Task OperationStatus_MissingFirstJournalUsesReservationAndOwnerTruth()
    {
        using var liveRoot = new TemporaryDirectory();
        var payload = new byte[] { 2, 4, 6 };
        StagePushInput(liveRoot.Path, "input01", payload);
        var runner = CreatePushRunner(payload);
        var verifier = new BlockingSecondAuthorityVerifier();
        var adapter = CreateReadyAdapter(
            liveRoot.Path,
            runner,
            verifier);
        var request = CreatePushRequest(adapter, payload);
        using var cancellation = new CancellationTokenSource();
        var invoke = adapter.InvokeAsync(request, cancellation.Token);
        await verifier.SecondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var operationRoot = Path.Combine(liveRoot.Path, "operations", request.OperationId);
        foreach (var journal in Directory.EnumerateFiles(operationRoot, "state-*.json"))
        {
            File.Delete(journal);
        }
        var priorCalls = runner.Calls.Count;

        var live = adapter.GetOperationStatus(request.OperationId);

        Assert.Equal(FleetIntegrationOperationPhase.Accepted, live.Phase);
        Assert.Equal(FleetIntegrationCleanupState.NotRequired, live.CleanupState);
        Assert.False(live.DestinationMayExist);
        Assert.False(live.PartialMayExist);
        Assert.Equal(priorCalls, runner.Calls.Count);

        cancellation.Cancel();
        var cancelled = await Assert.ThrowsAsync<FleetIntegrationException>(() => invoke);
        Assert.Equal("operation_cancelled", cancelled.Code);

        using var deadRoot = new TemporaryDirectory();
        StagePushInput(deadRoot.Path, "input01", payload);
        var deadRunner = CreatePushRunner(payload);
        var deadAdapter = CreateReadyAdapter(
            deadRoot.Path,
            deadRunner,
            new ScriptedAuthorityVerifier(_ => AcceptedAuthority()));
        var deadRequest = CreatePushRequest(deadAdapter, payload);
        await deadAdapter.InvokeAsync(deadRequest);
        var deadOperationRoot = Path.Combine(
            deadRoot.Path,
            "operations",
            deadRequest.OperationId);
        foreach (var journal in Directory.EnumerateFiles(deadOperationRoot, "state-*.json"))
        {
            File.Delete(journal);
        }
        var deadPriorCalls = deadRunner.Calls.Count;

        var recovered = deadAdapter.GetOperationStatus(deadRequest.OperationId);

        Assert.Equal(FleetIntegrationOperationPhase.RecoveryRequired, recovered.Phase);
        Assert.Equal(FleetIntegrationCleanupState.NotRequired, recovered.CleanupState);
        Assert.False(recovered.DestinationMayExist);
        Assert.False(recovered.PartialMayExist);
        Assert.Equal(deadPriorCalls, deadRunner.Calls.Count);
    }

    [Theory]
    [InlineData("../escape.txt", "path_traversal")]
    [InlineData("folder/../../escape.txt", "path_traversal")]
    [InlineData("CON.txt", "path_name_reserved")]
    [InlineData("folder/bad:name.txt", "path_character_invalid")]
    [InlineData("folder/trailing.", "path_name_invalid")]
    [InlineData("folder//file.txt", "path_traversal")]
    public void RelativePathPolicy_RejectsTraversalReservedAndDamagingNames(
        string relativePath,
        string expectedCode)
    {
        var exception = Assert.Throws<FleetIntegrationException>(
            () => FleetPathPolicy.ValidateRelativePath(relativePath, allowEmpty: false));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void RequestParser_AcceptsStrictV1AndRejectsUnknownDuplicateAndIncompletePush()
    {
        var valid = CreateRequestJson("""
            "kind": "list",
            "rootProfile": "adb-shared",
            "relativePath": "Download",
            "maximumEntries": 50
            """);

        var request = FleetIntegrationOperationRequest.Parse(Encoding.UTF8.GetBytes(valid));

        Assert.Equal("list", request.Operation.Kind);
        Assert.Equal(50, request.Operation.MaximumEntries);

        var unknown = valid.Replace(
            "\"operationId\": \"operation01\",",
            "\"operationId\": \"operation01\", \"overwrite\": true,",
            StringComparison.Ordinal);
        Assert.Equal(
            "request_unknown_field",
            Assert.Throws<FleetIntegrationException>(
                () => FleetIntegrationOperationRequest.Parse(Encoding.UTF8.GetBytes(unknown))).Code);

        var duplicate = valid.Replace(
            "\"requestId\": \"request01\",",
            "\"requestId\": \"request01\", \"requestId\": \"request02\",",
            StringComparison.Ordinal);
        Assert.Equal(
            "request_duplicate_field",
            Assert.Throws<FleetIntegrationException>(
                () => FleetIntegrationOperationRequest.Parse(Encoding.UTF8.GetBytes(duplicate))).Code);

        var push = CreateRequestJson("""
            "kind": "push",
            "rootProfile": "adb-shared",
            "relativePath": "Download/file.txt",
            "maximumBytes": 10
            """);
        Assert.Equal(
            "push_source_invalid",
            Assert.Throws<FleetIntegrationException>(
                () => FleetIntegrationOperationRequest.Parse(Encoding.UTF8.GetBytes(push))).Code);

        var completePush = FleetIntegrationOperationRequest.Parse(
            Encoding.UTF8.GetBytes(CreatePushRequestJson()));
        Assert.Equal("push", completePush.Operation.Kind);
        Assert.Equal("artifacts/input01/payload.bin", completePush.Operation.LocalArtifactPath);
        Assert.Equal(10, completePush.Operation.ExpectedSizeBytes);
        Assert.NotNull(completePush.MutationAuthority);
    }

    [Fact]
    public async Task Invoke_RejectsChangedObservationAndStaleBindingBeforeAdb()
    {
        using var root = new TemporaryDirectory();
        var runner = new ScriptedRunner((_, _, _) => Failure("ADB must not run"));
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "list",
            relativePath: "",
            maximumEntries: 10);
        var damaged = request with
        {
            DeviceBinding = request.DeviceBinding with
            {
                ObservationId = new string('a', 64)
            }
        };

        var invalid = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(damaged));
        Assert.Equal("device_binding_invalid", invalid.Code);

        var staleObservedAt = Now - FleetIntegrationContract.MaximumObservationAge - TimeSpan.FromSeconds(1);
        var stale = request with
        {
            DeviceBinding = request.DeviceBinding with
            {
                ObservedAtUtc = staleObservedAt,
                ObservationId = FleetIntegrationAdapter.ComputeObservationId(
                    request.AdapterEpoch,
                    "QUEST123",
                    "usb",
                    "device",
                    staleObservedAt)
            }
        };
        var staleException = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(stale));
        Assert.Equal("device_binding_stale", staleException.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Invoke_RejectsDamagingInMemoryOperationIdBeforeFilesystemOrAdb()
    {
        using var root = new TemporaryDirectory();
        var runner = new ScriptedRunner((_, _, _) => Failure("ADB must not run"));
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "pull",
            relativePath: "Download/report.bin",
            maximumBytes: 100) with
        {
            OperationId = "../escape"
        };

        var exception = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal("identifier_invalid", exception.Code);
        Assert.Empty(runner.Calls);
        Assert.False(Directory.Exists(System.IO.Path.Combine(root.Path, "operations")));
    }

    [Fact]
    public async Task Invoke_ConvertsCancellationToStableContractFailure()
    {
        using var root = new TemporaryDirectory();
        var runner = new ScriptedRunner((_, _, _) => throw new OperationCanceledException());
        var adapter = CreateReadyAdapter(root.Path, runner);
        var request = CreateRequest(
            adapter,
            kind: "list",
            relativePath: "",
            maximumEntries: 10);

        var exception = await Assert.ThrowsAsync<FleetIntegrationException>(
            () => adapter.InvokeAsync(request));

        Assert.Equal(FleetIntegrationStatus.Cancelled, exception.Status);
        Assert.Equal("operation_cancelled", exception.Code);
        Assert.True(exception.Retryable);
    }

    private static FleetIntegrationAdapter CreateReadyAdapter(
        string root,
        ICommandRunner runner,
        IFleetMutationAuthorityVerifier? mutationAuthorityVerifier = null)
    {
        var adbPath = System.IO.Path.Combine(root, "adb-test.exe");
        File.WriteAllBytes(adbPath, []);
        return new(
            new FleetIntegrationSettings(
                FleetIntegrationStatus.Ready,
                root,
                adbPath,
                null),
            new AdbClient("adb-test", runner),
            () => Now,
            mutationAuthorityVerifier);
    }

    private static FleetIntegrationOperationRequest CreateRequest(
        FleetIntegrationAdapter adapter,
        string kind,
        string relativePath,
        int? maximumEntries = null,
        long? maximumBytes = null)
    {
        var capability = adapter.GetCapabilities();
        var observedAt = Now - TimeSpan.FromSeconds(5);
        var observationId = FleetIntegrationAdapter.ComputeObservationId(
            capability.AdapterEpoch,
            "QUEST123",
            "usb",
            "device",
            observedAt);
        return new FleetIntegrationOperationRequest(
            FleetIntegrationContract.RequestSchema,
            FleetIntegrationContract.Version,
            "request01",
            "operation01",
            capability.AdapterEpoch,
            Now + TimeSpan.FromMinutes(1),
            new FleetIntegrationDeviceBinding(
                FleetIntegrationContract.BindingSchema,
                observationId,
                "QUEST123",
                "usb",
                observedAt),
            new FleetIntegrationOperation(
                kind,
                FleetIntegrationContract.RootProfile,
                relativePath,
                maximumEntries,
                maximumBytes));
    }

    private static ScriptedRunner CreatePullRunner(byte[] bytes) =>
        new((_, arguments, _) =>
        {
            if (arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n");
            }
            return Failure("unexpected");
        }, StreamBytes(bytes));

    private static ScriptedRunner CreatePushRunner(
        byte[] expectedBytes,
        Action? onStream = null) =>
        new(
            (_, arguments, _) =>
                arguments.SequenceEqual(["devices", "-l"])
                    ? Success("List of devices attached\nQUEST123 device product:eureka model:Quest_3\n")
                    : Failure("unexpected"),
            inputStreamingHandler: CreatePushInputHandler(expectedBytes, onStream));

    private static void AssertAtomicPushPublicationContract(string command)
    {
        var partialSizeRead = command.IndexOf(
            "size=$(stat -c %s -- \"$partial\")",
            StringComparison.Ordinal);
        var partialDigestRead = command.IndexOf(
            "digest=$(sha256sum \"$partial\")",
            StringComparison.Ordinal);
        var sizeVerification = command.IndexOf(
            "if [ \"$size\" !=",
            StringComparison.Ordinal);
        var digestVerification = command.IndexOf(
            "if [ \"$digest\" !=",
            StringComparison.Ordinal);
        var publication = command.IndexOf(
            "ln -T -- \"$partial\" \"$candidate\"",
            StringComparison.Ordinal);
        var finalOpen = command.IndexOf(
            "exec 4<\"$candidate\"",
            StringComparison.Ordinal);

        Assert.True(partialSizeRead >= 0);
        Assert.True(partialDigestRead > partialSizeRead);
        Assert.True(sizeVerification > partialDigestRead);
        Assert.True(digestVerification > sizeVerification);
        Assert.True(publication > digestVerification);
        Assert.True(finalOpen > publication);
        Assert.DoesNotContain("exec 4>\"$candidate\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("cat <&5 >&4", command, StringComparison.Ordinal);
        Assert.Contains(
            "if [ \"$final_id\" != \"$partial_id\" ]",
            command,
            StringComparison.Ordinal);
        Assert.Contains(
            "final_digest=$(sha256sum <&4)",
            command,
            StringComparison.Ordinal);
    }

    private static Func<
        string,
        IReadOnlyList<string>,
        int,
        Stream,
        long,
        CancellationToken,
        Task<StreamingCommandResult>> CreatePushInputHandler(
            byte[] expectedBytes,
            Action? onStream = null) =>
        async (
            fileName,
            arguments,
            _,
            source,
            maximumBytes,
            cancellationToken) =>
        {
            onStream?.Invoke();
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            Assert.Equal(expectedBytes, bytes);
            Assert.Equal(expectedBytes.LongLength, maximumBytes);
            var digest = Sha256(bytes);
            return new StreamingCommandResult(
                new CommandResult(
                    fileName,
                    arguments.ToArray(),
                    0,
                    $"qfm-integration:push-complete:{bytes.LongLength}:{digest}\n",
                    string.Empty,
                    TimeSpan.Zero),
                bytes.LongLength,
                digest);
        };

    private static FleetIntegrationOperationRequest CreatePushRequest(
        FleetIntegrationAdapter adapter,
        byte[] payload)
    {
        var request = CreateRequest(
            adapter,
            kind: "push",
            relativePath: "Download/file.bin",
            maximumBytes: payload.LongLength);
        return request with
        {
            Operation = request.Operation with
            {
                LocalArtifactPath = "artifacts/input01/payload.bin",
                ExpectedSizeBytes = payload.LongLength,
                ExpectedSha256 = Sha256(payload)
            },
            MutationAuthority = new FleetIntegrationMutationAuthority(
                FleetIntegrationContract.MutationAuthoritySchema,
                "fleet-device-1",
                7,
                "quest-proof-1",
                "command-1",
                "lease-1",
                "provider-1",
                9,
                Now + TimeSpan.FromSeconds(30))
        };
    }

    private static FleetMutationAuthorityDecision AcceptedAuthority(char digest = 'd') =>
        new(
            Accepted: true,
            Code: null,
            Reason: null,
            VerifiedAuthorityDigest: new string(digest, 64));

    private static void StagePushInput(string root, string artifactId, byte[] bytes)
    {
        var directory = Path.Combine(root, "artifacts", artifactId);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), bytes);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Func<
        string,
        IReadOnlyList<string>,
        int,
        Stream,
        long,
        CancellationToken,
        Task<StreamingCommandResult>> StreamBytes(byte[] bytes) =>
        async (fileName, arguments, _, destination, maximumBytes, cancellationToken) =>
        {
            Assert.Equal("-s", arguments[0]);
            Assert.Equal("QUEST123", arguments[1]);
            Assert.Equal("exec-out", arguments[2]);
            Assert.Equal("sh", arguments[3]);
            Assert.Equal("-c", arguments[4]);
            Assert.Contains("root=$(realpath '/sdcard')", arguments[5], StringComparison.Ordinal);
            Assert.Contains(
                "candidate=$(realpath '/sdcard/Download/report.bin')",
                arguments[5],
                StringComparison.Ordinal);
            Assert.Contains(
                "opened=$(readlink /proc/$$/fd/3)",
                arguments[5],
                StringComparison.Ordinal);
            Assert.Contains(
                $"[ \"$size\" -gt {maximumBytes} ]",
                arguments[5],
                StringComparison.Ordinal);
            Assert.EndsWith("exec cat <&3", arguments[5], StringComparison.Ordinal);

            if (bytes.LongLength > maximumBytes)
            {
                await destination.WriteAsync(
                    bytes.AsMemory(0, checked((int)maximumBytes)),
                    cancellationToken);
                throw new FleetTransferLimitException(maximumBytes);
            }

            await destination.WriteAsync(bytes, cancellationToken);
            var commandResult = new CommandResult(
                fileName,
                arguments.ToArray(),
                0,
                string.Empty,
                string.Empty,
                TimeSpan.Zero);
            return new StreamingCommandResult(
                commandResult,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        };

    private static string CreateRequestJson(string operationFields) => $$"""
        {
          "schema": "{{FleetIntegrationContract.RequestSchema}}",
          "contractVersion": "{{FleetIntegrationContract.Version}}",
          "requestId": "request01",
          "operationId": "operation01",
          "adapterEpoch": "{{new string('b', 64)}}",
          "expiresAtUtc": "{{(Now + TimeSpan.FromMinutes(1)).ToString("O")}}",
          "deviceBinding": {
            "schema": "{{FleetIntegrationContract.BindingSchema}}",
            "observationId": "{{new string('a', 64)}}",
            "serial": "QUEST123",
            "transport": "usb",
            "observedAtUtc": "{{(Now - TimeSpan.FromSeconds(5)).ToString("O")}}"
          },
          "operation": {
            {{operationFields}}
          }
        }
        """;

    private static string CreatePushRequestJson() => $$"""
        {
          "schema": "{{FleetIntegrationContract.RequestSchema}}",
          "contractVersion": "{{FleetIntegrationContract.Version}}",
          "requestId": "request01",
          "operationId": "operation01",
          "adapterEpoch": "{{new string('b', 64)}}",
          "expiresAtUtc": "{{(Now + TimeSpan.FromMinutes(1)).ToString("O")}}",
          "deviceBinding": {
            "schema": "{{FleetIntegrationContract.BindingSchema}}",
            "observationId": "{{new string('a', 64)}}",
            "serial": "QUEST123",
            "transport": "usb",
            "observedAtUtc": "{{(Now - TimeSpan.FromSeconds(5)).ToString("O")}}"
          },
          "operation": {
            "kind": "push",
            "rootProfile": "adb-shared",
            "relativePath": "Download/file.txt",
            "maximumBytes": 10,
            "localArtifactPath": "artifacts/input01/payload.bin",
            "expectedSizeBytes": 10,
            "expectedSha256": "{{new string('c', 64)}}"
          },
          "mutationAuthority": {
            "schema": "{{FleetIntegrationContract.MutationAuthoritySchema}}",
            "fleetDeviceId": "fleet-device-1",
            "fleetIdentityRevision": 7,
            "questIdentityProofId": "quest-proof-1",
            "manifoldCommandId": "command-1",
            "manifoldLeaseId": "lease-1",
            "manifoldProviderEpoch": "provider-1",
            "revocationBarrierRevision": 9,
            "expiresAtUtc": "{{(Now + TimeSpan.FromSeconds(30)).ToString("O")}}"
          }
        }
        """;

    private static CommandResult Success(string output) =>
        new("adb-test", Array.Empty<string>(), 0, output, string.Empty, TimeSpan.Zero);

    private static CommandResult Failure(string error) =>
        new("adb-test", Array.Empty<string>(), 1, string.Empty, error, TimeSpan.Zero);

    private static StreamingCommandResult StreamingSuccess(
        string fileName,
        IReadOnlyList<string> arguments,
        byte[] bytes) =>
        new(
            new CommandResult(
                fileName,
                arguments.ToArray(),
                0,
                string.Empty,
                string.Empty,
                TimeSpan.Zero),
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private sealed class ScriptedRunner(
        Func<string, IReadOnlyList<string>, int, CommandResult> handler,
        Func<
            string,
            IReadOnlyList<string>,
            int,
            Stream,
            long,
            CancellationToken,
            Task<StreamingCommandResult>>? streamingHandler = null,
        Func<
            string,
            IReadOnlyList<string>,
            int,
            Stream,
            long,
            CancellationToken,
            Task<StreamingCommandResult>>? inputStreamingHandler = null) : IStreamingCommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callIndex = Calls.Count;
            Calls.Add((fileName, arguments.ToArray()));
            var result = handler(fileName, arguments, callIndex);
            return Task.FromResult(result with { FileName = fileName, Arguments = arguments.ToArray() });
        }

        public Task<StreamingCommandResult> RunToStreamAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            Stream destination,
            long maximumBytes,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callIndex = Calls.Count;
            Calls.Add((fileName, arguments.ToArray()));
            return streamingHandler is null
                ? throw new InvalidOperationException("Unexpected streaming command.")
                : streamingHandler(
                    fileName,
                    arguments,
                    callIndex,
                    destination,
                    maximumBytes,
                    cancellationToken);
        }

        public Task<StreamingCommandResult> RunFromStreamAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            Stream source,
            long maximumBytes,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callIndex = Calls.Count;
            Calls.Add((fileName, arguments.ToArray()));
            return inputStreamingHandler is null
                ? throw new InvalidOperationException("Unexpected input streaming command.")
                : inputStreamingHandler(
                    fileName,
                    arguments,
                    callIndex,
                    source,
                    maximumBytes,
                    cancellationToken);
        }
    }

    private sealed class ScriptedAuthorityVerifier(
        Func<int, FleetMutationAuthorityDecision> handler) : IFleetMutationAuthorityVerifier
    {
        public int Calls { get; private set; }

        public ValueTask<FleetMutationAuthorityDecision> VerifyCurrentAsync(
            FleetIntegrationOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(request.MutationAuthority);
            return ValueTask.FromResult(handler(Calls++));
        }
    }

    private sealed class BlockingSecondAuthorityVerifier : IFleetMutationAuthorityVerifier
    {
        private int _calls;

        public TaskCompletionSource SecondCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<FleetMutationAuthorityDecision> VerifyCurrentAsync(
            FleetIntegrationOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.NotNull(request.MutationAuthority);
            var call = Interlocked.Increment(ref _calls);
            if (call == 2)
            {
                SecondCallStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return AcceptedAuthority();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"qfm-fleet-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
