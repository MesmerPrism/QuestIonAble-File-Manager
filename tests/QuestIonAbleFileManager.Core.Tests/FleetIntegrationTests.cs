using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
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
    public void RequestParser_AcceptsStrictV1AndRejectsUnknownDuplicateAndMutationFields()
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
            "operation_unsupported",
            Assert.Throws<FleetIntegrationException>(
                () => FleetIntegrationOperationRequest.Parse(Encoding.UTF8.GetBytes(push))).Code);
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
        ICommandRunner runner)
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
            () => Now);
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
                "opened=$(realpath /proc/self/fd/3)",
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
            Task<StreamingCommandResult>>? streamingHandler = null) : IStreamingCommandRunner
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
