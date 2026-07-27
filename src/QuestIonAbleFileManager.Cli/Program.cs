using System.Text.Json;
using System.Text.Json.Serialization;
using QuestIonAbleFileManager.Core;

return await CliApplication.RunAsync(args);

internal static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly JsonSerializerOptions IntegrationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly JsonSerializerOptions FleetJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length == 0 || HasFlag(arguments, "--help") || HasFlag(arguments, "-h"))
        {
            WriteHelp();
            return 0;
        }

        try
        {
            var command = arguments[0].ToLowerInvariant();
            if (command == "kiosk-direct")
            {
                return await RunKioskDirectAsync(arguments);
            }
            if (command == "integration")
            {
                return await RunIntegrationAsync(arguments);
            }
            if (command == "fleet")
            {
                return await RunFleetAsync(arguments);
            }
            if (command == "connectivity-profile")
            {
                return await RunConnectivityProfileAsync(arguments);
            }

            var client = AdbClient.CreateDefault(GetOption(arguments, "--adb"));
            var executor = new OperatorCommandExecutor(client);
            return command switch
            {
                "devices" => await RunDevicesAsync(executor, arguments),
                "files" => await RunFilesAsync(executor, arguments),
                "apk" => await RunApkAsync(executor, arguments),
                "wifi" => await RunWifiAsync(executor, arguments),
                "kiosk" => await RunKioskAsync(executor, arguments),
                "device" => await RunDeviceAsync(executor, arguments),
                _ => throw new ArgumentException($"Unknown command: {arguments[0]}")
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FileNotFoundException or
            IOException or
            SplitPackageException)
        {
            Console.Error.WriteLine($"Input error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunConnectivityProfileAsync(string[] arguments)
    {
        var errorSchema = arguments.Length > 1
            ? arguments[1] switch
            {
                "status" => QuestConnectivityProfileManagementContract.StatusSchema,
                "list" => QuestConnectivityProfileManagementContract.ListSchema,
                _ => QuestConnectivityProfileManagementContract.MutationSchema
            }
            : QuestConnectivityProfileManagementContract.MutationSchema;
        try
        {
            var command = OperatorCommands.ParseConnectivityProfileCliArguments(arguments);
            var executor = new OperatorCommandExecutor(
                client: null,
                new FleetInstallerHandoff(null),
                QuestConnectivityProfileManager.CreateWindows());
            await using var stdin = command.ConnectivityProfileInputKind ==
                                    QuestConnectivityProfileInputKind.StandardInput
                ? Console.OpenStandardInput()
                : null;
            var result = await executor.ExecuteAsync(
                command,
                privateInput: stdin).ConfigureAwait(false);
            WriteFleetJson<object?>(command.Kind switch
            {
                OperatorCommandKind.ConnectivityProfileStatus =>
                    result.ConnectivityProfileStatus,
                OperatorCommandKind.ConnectivityProfileList =>
                    result.ConnectivityProfileList,
                OperatorCommandKind.ConnectivityProfileImport or
                    OperatorCommandKind.ConnectivityProfileRevoke =>
                    result.ConnectivityProfileMutation,
                _ => null
            });
            return 0;
        }
        catch (QuestConnectivityProfileManagementException exception)
        {
            WriteFleetJson(new
            {
                schema = errorSchema,
                status = exception.Status,
                reason_code = exception.Code,
                rollback_state = exception.RollbackState,
                message = exception.Message
            });
            return exception.Status == "rejected" ? 2 : 1;
        }
        catch (ArgumentException)
        {
            WriteFleetJson(new
            {
                schema = errorSchema,
                status = "rejected",
                reason_code = "profileCommandInvalid",
                message =
                    "Use one exact connectivity-profile route; private values belong only in --file or --stdin JSON."
            });
            return 2;
        }
        catch
        {
            WriteFleetJson(new
            {
                schema = errorSchema,
                status = "failed",
                reason_code = "profileInternalError",
                message = "Connectivity profile management failed without exposing private details."
            });
            return 1;
        }
    }

    private static async Task<int> RunFleetAsync(string[] arguments)
    {
        var errorSchema = arguments.Length > 1 &&
                          string.Equals(arguments[1], "status", StringComparison.Ordinal)
            ? FleetInstallerContract.StatusSchema
            : FleetInstallerContract.HandoffSchema;
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var command = OperatorCommands.ParseFleetCliArguments(arguments);

            var executor = new OperatorCommandExecutor(
                client: null,
                FleetInstallerHandoff.FromEnvironment());
            var result = await executor.ExecuteAsync(
                command,
                cancellationSource.Token).ConfigureAwait(false);
            WriteFleetJson<object?>(command.Kind == OperatorCommandKind.FleetInstallStatus
                ? result.FleetInstallerStatus
                : result.FleetInstallerHandoff);
            return 0;
        }
        catch (FleetInstallerException exception)
        {
            WriteFleetJson(new
            {
                schema = errorSchema,
                status = "failed",
                error = exception.Code,
                message = exception.Message
            });
            return 2;
        }
        catch (ArgumentException exception)
        {
            WriteFleetJson(new
            {
                schema = errorSchema,
                status = "rejected",
                error = "fleet_command_invalid",
                message = exception.Message
            });
            return 2;
        }
        catch (OperationCanceledException)
        {
            WriteFleetJson(new
            {
                schema = errorSchema,
                status = "cancelled",
                error = "fleet_installer_cancelled",
                message = "The Fleet installer handoff was cancelled."
            });
            return 1;
        }
        catch
        {
            WriteFleetJson(new
            {
                schema = errorSchema,
                status = "failed",
                error = "fleet_installer_internal_error",
                message = "The Fleet installer handoff failed without exposing local details."
            });
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunIntegrationAsync(string[] arguments)
    {
        FleetIntegrationAdapter? adapter = null;
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (!HasFlag(arguments, "--json"))
            {
                throw FleetIntegrationException.Input(
                    "json_required",
                    "Integration routes require --json and emit exactly one final JSON document.");
            }

            var action = RequireAction(arguments, "integration");
            if (string.Equals(action, "kiosk-v2-catalog", StringComparison.Ordinal))
            {
                await using var input = Console.OpenStandardInput();
                await using var output = Console.OpenStandardOutput();
                return await RustyKioskV2CatalogSubprocessHost
                    .CreateWindows()
                    .RunAsync(arguments, input, output, cancellationSource.Token);
            }

            var settings = FleetIntegrationSettings.FromEnvironment(GetOption(arguments, "--adb"));
            var client = settings.AdbPath is null ? null : new AdbClient(settings.AdbPath);
            adapter = new FleetIntegrationAdapter(settings, client);
            var requestedVersion = GetOption(arguments, "--contract-version");
            if (requestedVersion is not null &&
                !string.Equals(requestedVersion, FleetIntegrationContract.Version, StringComparison.Ordinal))
            {
                throw FleetIntegrationException.Unsupported(
                    $"Unsupported integration contract version '{requestedVersion}'.");
            }

            switch (action)
            {
                case "capabilities":
                    WriteIntegrationJson(FleetIntegrationResponse.ForCapability(adapter.GetCapabilities()));
                    return 0;
                case "observe":
                    {
                        var capability = adapter.GetCapabilities();
                        var observation = await adapter.ObserveAsync(
                            RequireOption(arguments, "--serial"),
                            cancellationSource.Token);
                        WriteIntegrationJson(FleetIntegrationResponse.ForObservation(capability, observation));
                        return 0;
                    }
                case "invoke":
                    {
                        var capability = adapter.GetCapabilities();
                        var requestBytes = await ReadBoundedRequestAsync(
                            RequireOption(arguments, "--request"),
                            cancellationSource.Token);
                        var request = FleetIntegrationOperationRequest.Parse(requestBytes);
                        var result = await adapter.InvokeAsync(request, cancellationSource.Token);
                        WriteIntegrationJson(FleetIntegrationResponse.ForResult(capability, result));
                        return 0;
                    }
                case "status":
                    {
                        var capability = adapter.GetCapabilities();
                        var status = adapter.GetOperationStatus(
                            RequireOption(arguments, "--operation"));
                        WriteIntegrationJson(FleetIntegrationResponse.ForOperationStatus(capability, status));
                        return 0;
                    }
                default:
                    throw FleetIntegrationException.Input(
                        "integration_action_unknown",
                        $"Unknown integration action '{action}'.");
            }
        }
        catch (FleetIntegrationException exception)
        {
            WriteIntegrationJson(FleetIntegrationResponse.Failure(
                exception.Status,
                exception.Code,
                exception.Message,
                exception.Retryable,
                TryGetCapability(adapter)));
            return IntegrationExitCode(exception.Status);
        }
        catch (OperationCanceledException exception)
        {
            WriteIntegrationJson(FleetIntegrationResponse.Failure(
                FleetIntegrationStatus.Cancelled,
                "operation_cancelled",
                exception.Message.Length == 0
                    ? "The integration operation was cancelled."
                    : exception.Message,
                retryable: true,
                TryGetCapability(adapter)));
            return IntegrationExitCode(FleetIntegrationStatus.Cancelled);
        }
        catch (TimeoutException exception)
        {
            WriteIntegrationJson(FleetIntegrationResponse.Failure(
                FleetIntegrationStatus.Failed,
                "operation_timeout",
                exception.Message,
                retryable: true,
                TryGetCapability(adapter)));
            return 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FileNotFoundException or
            DirectoryNotFoundException or
            IOException or
            UnauthorizedAccessException)
        {
            WriteIntegrationJson(FleetIntegrationResponse.Failure(
                FleetIntegrationStatus.Rejected,
                "integration_input_invalid",
                exception.Message,
                retryable: false,
                TryGetCapability(adapter)));
            return 2;
        }
        catch (Exception exception)
        {
            WriteIntegrationJson(FleetIntegrationResponse.Failure(
                FleetIntegrationStatus.Failed,
                "integration_internal_error",
                exception.Message,
                retryable: false,
                TryGetCapability(adapter)));
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<byte[]> ReadBoundedRequestAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(requestPath);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > FleetIntegrationContract.MaximumRequestBytes)
        {
            throw FleetIntegrationException.Input(
                "request_too_large",
                $"The integration request exceeds {FleetIntegrationContract.MaximumRequestBytes} bytes.");
        }

        using var buffer = new MemoryStream((int)stream.Length);
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (buffer.Length + count > FleetIntegrationContract.MaximumRequestBytes)
            {
                throw FleetIntegrationException.Input(
                    "request_too_large",
                    $"The integration request exceeds {FleetIntegrationContract.MaximumRequestBytes} bytes.");
            }
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static FleetIntegrationCapabilitySnapshot? TryGetCapability(
        FleetIntegrationAdapter? adapter)
    {
        if (adapter is null)
        {
            return null;
        }
        try
        {
            return adapter.GetCapabilities();
        }
        catch
        {
            return null;
        }
    }

    private static int IntegrationExitCode(FleetIntegrationStatus status) =>
        status switch
        {
            FleetIntegrationStatus.Rejected or
            FleetIntegrationStatus.Unsupported => 2,
            FleetIntegrationStatus.Disabled or
            FleetIntegrationStatus.Absent or
            FleetIntegrationStatus.Unavailable or
            FleetIntegrationStatus.Unauthorized => 3,
            FleetIntegrationStatus.Cancelled => 4,
            _ => 1
        };

    private static async Task<int> RunKioskDirectAsync(string[] arguments)
    {
        var action = RequireAction(arguments, "kiosk-direct");
        var client = CreateKioskDirectClient(arguments);
        var json = HasFlag(arguments, "--json");
        switch (action)
        {
            case "status":
                {
                    var status = await client.GetStatusAsync();
                    var kiosk = await client.InvokeKioskAsync(RustyKioskCommand.Status);
                    if (json)
                    {
                        WriteJson(new { transport = "direct", status, kiosk });
                    }
                    else
                    {
                        Console.WriteLine($"Direct link: confirmed at {status.Endpoint ?? client.Endpoint.BaseUri.ToString()}");
                        Console.WriteLine($"Local installer: {(status.InstallerAllowed ? "wearer allowed" : "needs wearer permission")}");
                        Console.WriteLine($"Apps: {kiosk.State.InstalledCount} installed, {kiosk.State.NotInstalledCount} not installed");
                        Console.WriteLine($"Guard: {(kiosk.State.GuardArmed ? "armed" : "inactive")}");
                    }
                    return 0;
                }

            case "command":
                {
                    var command = RustyKioskCommands.Parse(RequireOption(arguments, "--command"));
                    if (command is not RustyKioskCommand.Status and not RustyKioskCommand.CheckSetupHelper)
                    {
                        RequireConfirmation(arguments, "--confirm-kiosk-control", "Direct Rusty Kiosk state change");
                    }
                    var value = GetOption(arguments, "--value");
                    var sentAt = DateTimeOffset.UtcNow;
                    var result = await client.InvokeKioskAsync(command, value);
                    var confirmed = RustyKioskReadback.Confirms(command, value, result);
                    var stage = confirmed ? "confirmed" : "pending";
                    if (json)
                    {
                        WriteJson(new
                        {
                            transport = "direct",
                            mutation = new
                            {
                                stage,
                                sentAt,
                                readbackAt = DateTimeOffset.UtcNow,
                                headsetReadback = true,
                                desired = command.ToWireName()
                            },
                            result
                        });
                    }
                    else
                    {
                        Console.WriteLine($"Sent: {command.ToWireName()}");
                        Console.WriteLine($"Sync: {stage} · {result.Message}");
                        if (!confirmed)
                        {
                            Console.WriteLine("Refresh status after the wearer responds or the headset settles.");
                        }
                    }
                    return confirmed ? 0 : 3;
                }

            case "tags":
                {
                    if (arguments.Length < 3 || arguments[2].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("The kiosk-direct tags command requires export or import.");
                    }
                    var tagsAction = arguments[2].ToLowerInvariant();
                    if (tagsAction == "export")
                    {
                        var output = Path.GetFullPath(RequireOption(arguments, "--output"));
                        await File.WriteAllBytesAsync(output, await client.ReadTagsAsync());
                        Console.WriteLine(output);
                        return 0;
                    }
                    if (tagsAction == "import")
                    {
                        RequireConfirmation(arguments, "--confirm-kiosk-control", "Direct tag-file replacement");
                        var input = Path.GetFullPath(RequireOption(arguments, "--file"));
                        await client.WriteTagsAsync(await File.ReadAllBytesAsync(input));
                        var result = await client.InvokeKioskAsync(RustyKioskCommand.Reload);
                        if (json)
                        {
                            WriteJson(new { transport = "direct", stage = "confirmed", result });
                        }
                        else
                        {
                            Console.WriteLine("Sync: confirmed · tag file validated, activated, and catalogue reloaded.");
                        }
                        return 0;
                    }
                    throw new ArgumentException($"Unknown kiosk-direct tags action: {tagsAction}");
                }

            case "files":
                {
                    if (arguments.Length < 3 || arguments[2].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("The kiosk-direct files command requires list, upload, download, or delete.");
                    }
                    var fileAction = arguments[2].ToLowerInvariant();
                    if (fileAction == "list")
                    {
                        var files = await client.ListStagingAsync();
                        if (json) WriteJson(files);
                        else foreach (var file in files) Console.WriteLine($"{file.Bytes}\t{file.Name}");
                        return 0;
                    }
                    if (fileAction == "upload")
                    {
                        var result = await client.UploadToStagingAsync(
                            RequireOption(arguments, "--file"),
                            GetOption(arguments, "--name"));
                        if (json) WriteJson(new { transport = "direct", stage = "confirmed", result });
                        else Console.WriteLine($"Sync: confirmed · {result.Name} · {result.Bytes} bytes");
                        return 0;
                    }
                    if (fileAction == "download")
                    {
                        var output = await client.DownloadFromStagingAsync(
                            RequireOption(arguments, "--name"),
                            RequireOption(arguments, "--output"),
                            overwrite: HasFlag(arguments, "--overwrite"));
                        Console.WriteLine($"Sync: confirmed · {output}");
                        return 0;
                    }
                    if (fileAction == "delete")
                    {
                        RequireConfirmation(arguments, "--confirm-file-delete", "Direct staged-file deletion");
                        var name = RequireOption(arguments, "--name");
                        await client.DeleteStagedAsync(name);
                        Console.WriteLine($"Sync: confirmed · deleted staged file {name}");
                        return 0;
                    }
                    throw new ArgumentException($"Unknown kiosk-direct files action: {fileAction}");
                }

            case "install":
                {
                    RequireConfirmation(arguments, "--confirm-local-install", "Wearer-confirmed local APK installation");
                    var paths = GetOptions(arguments, "--file");
                    if (paths.Count == 0)
                    {
                        throw new ArgumentException("Pass one --file per APK part.");
                    }
                    foreach (var path in paths)
                    {
                        await client.UploadToStagingAsync(path);
                    }
                    var requestId = "install_" + Guid.NewGuid().ToString("N");
                    var receipt = await client.RequestInstallAsync(
                        paths.Select(Path.GetFileName).Select(static name => name!).ToArray(),
                        requestId);
                    if (json)
                    {
                        WriteJson(new { transport = "direct", stage = DirectInstallStage(receipt), receipt });
                    }
                    else
                    {
                        Console.WriteLine($"Sent: Android install request {requestId}");
                        Console.WriteLine($"Sync: {DirectInstallStage(receipt)} · {receipt.State} · {receipt.Message}");
                    }
                    return receipt.Installed ? 0 : receipt.Failed ? 1 : 3;
                }

            case "install-status":
                {
                    var receipt = await client.ReadInstallReceiptAsync(RequireOption(arguments, "--request-id"));
                    if (json) WriteJson(new { transport = "direct", stage = DirectInstallStage(receipt), receipt });
                    else Console.WriteLine($"Sync: {DirectInstallStage(receipt)} · {receipt.State} · {receipt.Message}");
                    return receipt.Installed ? 0 : receipt.Failed ? 1 : 3;
                }

            default:
                throw new ArgumentException($"Unknown kiosk-direct action: {action}");
        }
    }

    private static RustyKioskDirectClient CreateKioskDirectClient(string[] arguments)
    {
        var endpoint = RequireOption(arguments, "--endpoint");
        var pairingCode = GetOption(arguments, "--pairing-code") ??
            Environment.GetEnvironmentVariable("RUSTY_KIOSK_PAIRING_CODE") ??
            throw new ArgumentException(
                "Missing --pairing-code. It may also be supplied through RUSTY_KIOSK_PAIRING_CODE.");
        return new RustyKioskDirectClient(RustyKioskDirectEndpoint.Parse(endpoint, pairingCode));
    }

    private static string DirectInstallStage(RustyKioskDirectInstallReceipt receipt) =>
        receipt.Installed ? "confirmed" : receipt.Failed ? "failed" : "pending";

    private static async Task<int> RunDevicesAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        var result = await executor.ExecuteAsync(OperatorCommands.DiscoverDevices());
        var devices = result.Devices ?? throw new InvalidOperationException("Device discovery returned no device collection.");
        if (HasFlag(arguments, "--json"))
        {
            WriteJson(devices);
            return 0;
        }

        if (devices.Count == 0)
        {
            Console.WriteLine("No ADB devices were found.");
            return 0;
        }

        foreach (var device in devices)
        {
            Console.WriteLine($"{device.Serial}\t{device.State}\t{device.Model ?? "unknown model"}");
        }

        return 0;
    }

    private static async Task<int> RunFilesAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        var action = RequireAction(arguments, "files");
        var serial = RequireOption(arguments, "--serial");

        switch (action)
        {
            case "list":
                {
                    var remotePath = GetOption(arguments, "--path") ?? "/sdcard";
                    var result = await executor.ExecuteAsync(OperatorCommands.ListFiles(serial, remotePath));
                    var entries = result.RemoteEntries ??
                        throw new InvalidOperationException("File listing returned no entry collection.");
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(entries);
                    }
                    else
                    {
                        foreach (var entry in entries)
                        {
                            Console.WriteLine($"{entry.TypeLabel}\t{entry.FullPath}");
                        }
                    }

                    return 0;
                }

            case "pull":
                {
                    var remotePath = RequireOption(arguments, "--remote");
                    var outputPath = RequireOption(arguments, "--output");
                    var command = OperatorCommands.PullFile(serial, remotePath, outputPath);
                    await executor.ExecuteAsync(command);
                    Console.WriteLine(command.LocalPath);
                    return 0;
                }

            case "push":
                {
                    var localPath = RequireOption(arguments, "--file");
                    var remotePath = RequireOption(arguments, "--remote");
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.PushFile(serial, localPath, remotePath));
                    WriteMutationAware(
                        execution,
                        new { remotePath, command = execution.CommandResult },
                        HasFlag(arguments, "--json"),
                        () => Console.WriteLine(remotePath));
                    return 0;
                }

            default:
                throw new ArgumentException($"Unknown files action: {action}");
        }
    }

    private static async Task<int> RunApkAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        var action = RequireAction(arguments, "apk");

        switch (action)
        {
            case "inspect":
                {
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.InspectApk(RequireOption(arguments, "--file")));
                    var inspection = execution.ApkArtifactInspection ??
                        throw new InvalidOperationException("APK inspection returned no result.");
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(inspection);
                    }
                    else
                    {
                        Console.WriteLine($"{inspection.Identity.PackageName} versionCode={inspection.Identity.VersionCode}");
                        Console.WriteLine($"Signer SHA-256: {inspection.Identity.SignerSha256}");
                        Console.WriteLine($"Artifact SHA-256: {inspection.Sha256} ({inspection.SizeBytes} bytes)");
                    }
                    return 0;
                }

            case "list":
                {
                    var serial = RequireOption(arguments, "--serial");
                    var result = await executor.ExecuteAsync(OperatorCommands.ListPackages(serial));
                    var packages = result.Packages ??
                        throw new InvalidOperationException("Package listing returned no package collection.");
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(packages);
                    }
                    else
                    {
                        foreach (var packageName in packages)
                        {
                            Console.WriteLine(packageName);
                        }
                    }

                    return 0;
                }

            case "export":
                {
                    var serial = RequireOption(arguments, "--serial");
                    var packageName = RequireOption(arguments, "--package");
                    var outputPath = RequireOption(arguments, "--output");
                    var execution = await executor.ExecuteAsync(OperatorCommands.ExportApk(
                        serial,
                        packageName,
                        outputPath,
                        overwrite: HasFlag(arguments, "--overwrite")));
                    var result = execution.ApkExportResult ??
                        throw new InvalidOperationException("APK export returned no export result.");
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(result);
                    }
                    else
                    {
                        Console.WriteLine($"APK: {result.OutputPath}");
                        Console.WriteLine($"SHA-256: {result.Sha256}");
                        Console.WriteLine($"Checksum: {result.ChecksumPath}");
                    }

                    return 0;
                }

            case "install":
                {
                    var serial = RequireOption(arguments, "--serial");
                    var apkPath = RequireOption(arguments, "--file");
                    var options = ReadInstallOptions(arguments);
                    var execution = await executor.ExecuteAsync(OperatorCommands.InstallApk(serial, apkPath, options));
                    WriteMutationAware(
                        execution,
                        execution.CommandResult,
                        HasFlag(arguments, "--json"),
                        () => Console.WriteLine(execution.CommandResult?.StandardOutput.Trim()));
                    return 0;
                }

            case "launch":
                {
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.LaunchInspectedApp(
                            RequireOption(arguments, "--serial"),
                            RequireOption(arguments, "--file")));
                    WriteMutationAware(
                        execution,
                        execution.ResolvedAppLaunchResult,
                        HasFlag(arguments, "--json"),
                        () => Console.WriteLine(execution.ResolvedAppLaunchResult?.Component));
                    return 0;
                }

            case "observe":
                {
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.ObserveInspectedApp(
                            RequireOption(arguments, "--serial"),
                            RequireOption(arguments, "--file")));
                    var observation = execution.AppRuntimeObservation ??
                        throw new InvalidOperationException("Runtime observation returned no result.");
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(observation);
                    }
                    else
                    {
                        Console.WriteLine($"Installed: {observation.Installed is not null}");
                        Console.WriteLine($"Foreground: {observation.IsForeground}");
                        Console.WriteLine($"Top resumed: {observation.IsTopResumed}");
                        Console.WriteLine($"Processes: {string.Join(", ", observation.ProcessIds)}");
                    }
                    return 0;
                }

            case "install-bundle":
                {
                    var serial = RequireOption(arguments, "--serial");
                    var folderPath = RequireOption(arguments, "--folder");
                    var options = ReadInstallOptions(arguments);
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.InstallApkBundle(serial, folderPath, options));
                    var result = execution.ApkBundleInstallResult ??
                        throw new InvalidOperationException("APK bundle installation returned no result.");
                    WriteMutationAware(
                        execution,
                        result,
                        HasFlag(arguments, "--json"),
                        () =>
                        {
                            Console.WriteLine(result.CommandResult.StandardOutput.Trim());
                            Console.WriteLine($"Installed {result.ApkPaths.Count} APK parts as one package set.");
                        });
                    return 0;
                }

            case "install-many":
                {
                    var serials = GetOptions(arguments, "--serial");
                    var apkPath = RequireOption(arguments, "--file");
                    var options = ReadInstallOptions(arguments);
                    var parallelism = GetIntegerOption(arguments, "--parallelism", 4);
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.InstallApkMany(serials, apkPath, options, parallelism));
                    return WriteParallelInstallResult(
                        execution.ParallelApkInstallResult ??
                        throw new InvalidOperationException("Parallel APK installation returned no result."),
                        execution.MutationReceipt,
                        HasFlag(arguments, "--json"));
                }

            case "install-bundle-many":
                {
                    var serials = GetOptions(arguments, "--serial");
                    var folderPath = RequireOption(arguments, "--folder");
                    var options = ReadInstallOptions(arguments);
                    var parallelism = GetIntegerOption(arguments, "--parallelism", 4);
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.InstallApkBundleMany(serials, folderPath, options, parallelism));
                    return WriteParallelInstallResult(
                        execution.ParallelApkInstallResult ??
                        throw new InvalidOperationException("Parallel APK bundle installation returned no result."),
                        execution.MutationReceipt,
                        HasFlag(arguments, "--json"));
                }

            default:
                throw new ArgumentException($"Unknown apk action: {action}");
        }
    }

    private static async Task<int> RunWifiAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        var action = RequireAction(arguments, "wifi");
        if (!HasFlag(arguments, "--confirm-wifi-adb"))
        {
            throw new ArgumentException(
                "Wi-Fi ADB changes require --confirm-wifi-adb after operator approval.");
        }

        var port = GetIntegerOption(arguments, "--port", 5555);
        switch (action)
        {
            case "enable":
                {
                    var serial = RequireOption(arguments, "--serial");
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.EnableWifiAdb(serial, port, operatorConfirmed: true));
                    var result = execution.WifiAdbEnableResult ??
                        throw new InvalidOperationException("Wi-Fi ADB enablement returned no result.");
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(new { mutation = execution.MutationReceipt, result });
                    }
                    else
                    {
                        Console.WriteLine($"Wi-Fi ADB is connected at {result.Endpoint}.");
                        WriteMutationReceipt(execution.MutationReceipt);
                    }

                    return 0;
                }

            case "connect":
                {
                    var host = RequireOption(arguments, "--host");
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.ConnectWifiAdb(host, port, operatorConfirmed: true));
                    var result = execution.WifiAdbConnectionResult ??
                        throw new InvalidOperationException("Wi-Fi ADB connection returned no result.");
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(result);
                    }
                    else
                    {
                        Console.WriteLine($"Connected to {result.Endpoint}.");
                    }

                    return 0;
                }

            case "disconnect":
                {
                    var host = RequireOption(arguments, "--host");
                    var command = OperatorCommands.DisconnectWifiAdb(
                        host,
                        port,
                        operatorConfirmed: true);
                    var execution = await executor.ExecuteAsync(command);
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(new { mutation = execution.MutationReceipt, result = execution.CommandResult });
                    }
                    else
                    {
                        Console.WriteLine($"Disconnected {AndroidInput.CreateWifiEndpoint(host, port)}.");
                        WriteMutationReceipt(execution.MutationReceipt);
                    }

                    return 0;
                }

            default:
                throw new ArgumentException($"Unknown wifi action: {action}");
        }
    }

    private static async Task<int> RunKioskAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        var action = RequireAction(arguments, "kiosk");
        var serial = RequireOption(arguments, "--serial");
        switch (action)
        {
            case "status":
                {
                    var execution = await executor.ExecuteAsync(OperatorCommands.InspectRustyKiosk(serial));
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(new
                        {
                            installation = execution.RustyKioskInstallationStatus,
                            kiosk = execution.RustyKioskOperatorResult
                        });
                    }
                    else
                    {
                        var installation = execution.RustyKioskInstallationStatus ??
                            throw new InvalidOperationException("Rusty Kiosk inspection returned no status.");
                        Console.WriteLine($"Main app: {(installation.MainInstalled ? installation.MainVersion ?? "installed" : "not installed")}");
                        Console.WriteLine($"Setup helper: {(installation.SetupHelperInstalled ? installation.SetupHelperVersion ?? "installed" : "not installed")}");
                        Console.WriteLine($"USB setup: {(installation.SetupHelperReady ? "ready" : "not ready")}");
                        Console.WriteLine($"Host operator: {(installation.HostOperatorAvailable ? "available" : "unavailable")}");
                        if (execution.RustyKioskOperatorResult is { } kiosk)
                        {
                            Console.WriteLine($"Apps: {kiosk.State.InstalledCount} installed, {kiosk.State.NotInstalledCount} not installed");
                            Console.WriteLine($"Accessibility: {(kiosk.State.AccessibilityEnabled ? "enabled" : "disabled")}");
                            Console.WriteLine($"Wi-Fi ADB: {(kiosk.State.WifiAdbEnabled ? "enabled" : "disabled")}");
                        }
                    }

                    return 0;
                }

            case "install":
                {
                    RequireConfirmation(arguments, "--confirm-kiosk-setup", "Rusty Kiosk installation and USB setup");
                    var bundleDirectory = GetOption(arguments, "--bundle");
                    var bundle = RustyKioskBundleLocator.TryFind(bundleDirectory) ??
                        throw new FileNotFoundException(
                            "No bundled Rusty Kiosk APK set was found. Pass --bundle <folder> or stage the release kiosk folder.");
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.InstallRustyKiosk(serial, bundle, operatorConfirmed: true));
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(new
                        {
                            mutation = execution.MutationReceipt,
                            result = execution.RustyKioskInstallResult
                        });
                    }
                    else
                    {
                        Console.WriteLine("Rusty Kiosk and its setup helper are installed and provisioned.");
                        Console.WriteLine("No Wi-Fi ADB or Accessibility setting was enabled automatically.");
                        WriteMutationReceipt(execution.MutationReceipt);
                    }

                    return 0;
                }

            case "provision":
                {
                    RequireConfirmation(arguments, "--confirm-kiosk-setup", "Rusty Kiosk USB setup");
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.ProvisionRustyKiosk(serial, operatorConfirmed: true));
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(new
                        {
                            mutation = execution.MutationReceipt,
                            result = execution.RustyKioskProvisionResult
                        });
                    }
                    else
                    {
                        Console.WriteLine("Rusty Kiosk Setup is provisioned.");
                        WriteMutationReceipt(execution.MutationReceipt);
                    }

                    return 0;
                }

            case "command":
                {
                    var command = RustyKioskCommands.Parse(RequireOption(arguments, "--command"));
                    var confirmation = HasFlag(arguments, "--confirm-kiosk-control");
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.InvokeRustyKiosk(
                            serial,
                            command,
                            GetOption(arguments, "--value"),
                            operatorConfirmed: confirmation));
                    var result = execution.RustyKioskOperatorResult ??
                        throw new InvalidOperationException("Rusty Kiosk returned no operator result.");
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(new { mutation = execution.MutationReceipt, result });
                    }
                    else
                    {
                        Console.WriteLine(result.Message);
                        WriteMutationReceipt(execution.MutationReceipt);
                    }

                    return result.Accepted ? 0 : 1;
                }

            case "tags":
                {
                    if (arguments.Length < 3 || arguments[2].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("The kiosk tags command requires export or import.");
                    }

                    var tagsAction = arguments[2].ToLowerInvariant();
                    if (tagsAction == "export")
                    {
                        var output = RequireOption(arguments, "--output");
                        await executor.ExecuteAsync(OperatorCommands.PullRustyKioskTags(serial, output));
                        Console.WriteLine(Path.GetFullPath(output));
                        return 0;
                    }

                    if (tagsAction == "import")
                    {
                        RequireConfirmation(arguments, "--confirm-kiosk-control", "Rusty Kiosk tag-file replacement");
                        var input = RequireOption(arguments, "--file");
                        var execution = await executor.ExecuteAsync(
                            OperatorCommands.PushRustyKioskTags(serial, input, operatorConfirmed: true));
                        WriteMutationAware(
                            execution,
                            execution.RustyKioskOperatorResult,
                            HasFlag(arguments, "--json"),
                            () => Console.WriteLine("Rusty Kiosk tag file imported and hotload confirmed."));
                        return 0;
                    }

                    throw new ArgumentException($"Unknown kiosk tags action: {tagsAction}");
                }

            default:
                throw new ArgumentException($"Unknown kiosk action: {action}");
        }
    }

    private static async Task<int> RunDeviceAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        var action = RequireAction(arguments, "device");
        var serial = RequireOption(arguments, "--serial");
        switch (action)
        {
            case "status":
                {
                    var execution = await executor.ExecuteAsync(OperatorCommands.ReadQuestControls(serial));
                    var result = execution.QuestControlStatus ??
                        throw new InvalidOperationException("Quest status returned no result.");
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(result);
                    }
                    else
                    {
                        Console.WriteLine($"Headset battery: {result.HeadsetBatteryLabel}");
                        Console.WriteLine($"Controllers: {result.ControllerBatteryLabel}");
                        Console.WriteLine($"Stay on: {(result.StayOn ? "active" : "inactive")}");
                        Console.WriteLine($"Wake/display: {result.Wakefulness} / {result.DisplayState}");
                        Console.WriteLine(
                            $"Proximity: {result.ProximityState}; " +
                            $"hold {DisplayHold(result.ProximityHoldDurationMilliseconds, result.ProximityHoldRemainingMilliseconds)}");
                        Console.WriteLine($"CPU/GPU override: {DisplayOverride(result.CpuLevel)} / {DisplayOverride(result.GpuLevel)}");
                    }

                    return 0;
                }

            case "keep-awake":
                {
                    RequireConfirmation(arguments, "--confirm-device-settings", "Quest keep-awake policy change");
                    var on = HasFlag(arguments, "--on");
                    var off = HasFlag(arguments, "--off");
                    if (on == off)
                    {
                        throw new ArgumentException("Choose exactly one of --on or --off.");
                    }

                    var duration = GetIntegerOption(arguments, "--duration-ms", 28_800_000);
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.SetQuestKeepAwake(
                            serial,
                            enabled: on,
                            durationMilliseconds: duration,
                            operatorConfirmed: true));
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(new
                        {
                            mutation = execution.MutationReceipt,
                            result = execution.QuestKeepAwakeResult
                        });
                    }
                    else
                    {
                        Console.WriteLine(on ? "Keep-awake requested." : "Normal power/proximity behavior requested.");
                        Console.WriteLine($"Effective proximity: {execution.QuestControlStatus?.ProximityState ?? "unavailable"}");
                        WriteMutationReceipt(execution.MutationReceipt);
                    }

                    return 0;
                }

            case "performance":
                {
                    RequireConfirmation(arguments, "--confirm-device-settings", "Quest CPU/GPU override change");
                    var clear = HasFlag(arguments, "--clear");
                    var cpu = GetOptionalIntegerOption(arguments, "--cpu");
                    var gpu = GetOptionalIntegerOption(arguments, "--gpu");
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.SetQuestPerformance(
                            serial,
                            cpu,
                            gpu,
                            clear,
                            operatorConfirmed: true));
                    if (HasFlag(arguments, "--json"))
                    {
                        WriteJson(new
                        {
                            mutation = execution.MutationReceipt,
                            result = execution.QuestPerformanceResult
                        });
                    }
                    else
                    {
                        var status = execution.QuestControlStatus ??
                            throw new InvalidOperationException("Quest performance change returned no readback.");
                        Console.WriteLine($"CPU/GPU override: {DisplayOverride(status.CpuLevel)} / {DisplayOverride(status.GpuLevel)}");
                        WriteMutationReceipt(execution.MutationReceipt);
                    }

                    return 0;
                }

            default:
                throw new ArgumentException($"Unknown device action: {action}");
        }
    }

    private static string RequireAction(string[] arguments, string command)
    {
        if (arguments.Length < 2 || arguments[1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"The {command} command requires an action.");
        }

        return arguments[1].ToLowerInvariant();
    }

    private static ApkInstallOptions ReadInstallOptions(string[] arguments) =>
        new(
            ReplaceExisting: !HasFlag(arguments, "--no-replace"),
            AllowDowngrade: HasFlag(arguments, "--downgrade"),
            GrantRuntimePermissions: HasFlag(arguments, "--grant-runtime-permissions"),
            AllowTestPackages: HasFlag(arguments, "--test-only"));

    private static string RequireOption(string[] arguments, string name) =>
        GetOption(arguments, name) ?? throw new ArgumentException($"Missing required option {name}.");

    private static string? GetOption(string[] arguments, string name)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            if (!string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option {name} requires a value.");
            }

            return arguments[index + 1];
        }

        return null;
    }

    private static IReadOnlyList<string> GetOptions(string[] arguments, string name)
    {
        var values = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (!string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option {name} requires a value.");
            }

            values.Add(arguments[index + 1]);
        }

        return values;
    }

    private static int GetIntegerOption(string[] arguments, string name, int defaultValue)
    {
        var value = GetOption(arguments, name);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed))
        {
            throw new ArgumentException($"Option {name} requires an integer value.");
        }

        return parsed;
    }

    private static int? GetOptionalIntegerOption(string[] arguments, string name)
    {
        var value = GetOption(arguments, name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Option {name} requires an integer value.");
    }

    private static void RequireConfirmation(string[] arguments, string flag, string operation)
    {
        if (!HasFlag(arguments, flag))
        {
            throw new ArgumentException($"{operation} requires {flag} after operator approval.");
        }
    }

    private static string DisplayOverride(string value) => string.IsNullOrWhiteSpace(value) ? "app controlled" : value;

    private static string DisplayHold(int? durationMilliseconds, int? remainingMilliseconds) =>
        durationMilliseconds is int duration && remainingMilliseconds is int remaining
            ? $"{duration} ms requested, {remaining} ms remaining"
            : "not observed";

    private static bool HasFlag(string[] arguments, string name) =>
        arguments.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    private static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteFleetJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, FleetJsonOptions));

    private static void WriteIntegrationJson(FleetIntegrationResponse value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, IntegrationJsonOptions));

    private static void WriteMutationAware<T>(
        OperatorExecutionResult execution,
        T result,
        bool json,
        Action writeHumanResult)
    {
        if (json)
        {
            WriteJson(new { mutation = execution.MutationReceipt, result });
            return;
        }

        writeHumanResult();
        WriteMutationReceipt(execution.MutationReceipt);
    }

    private static void WriteMutationReceipt(OperatorMutationReceipt? receipt)
    {
        if (receipt is null)
        {
            return;
        }

        Console.WriteLine(
            $"Sync: {receipt.Stage.ToString().ToLowerInvariant()} " +
            $"({receipt.DesiredState}; observed: {receipt.ObservedState})");
        if (receipt.Stage == OperatorMutationStage.Pending)
        {
            Console.WriteLine("Run the corresponding status command after the wearer responds or the headset settles.");
        }
    }

    private static int WriteParallelInstallResult(
        ParallelApkInstallResult result,
        OperatorMutationReceipt? mutationReceipt,
        bool json)
    {
        if (json)
        {
            WriteJson(new { mutation = mutationReceipt, result });
        }
        else
        {
            foreach (var target in result.Targets)
            {
                Console.WriteLine(
                    $"{target.Serial}\t{(target.Succeeded ? "success" : "failed")}\t{target.Summary}");
            }

            Console.WriteLine(
                $"Installed successfully on {result.SucceededCount} of {result.Targets.Count} headsets " +
                $"with at most {result.MaxParallelism} concurrent installs.");
            WriteMutationReceipt(mutationReceipt);
        }

        return result.Succeeded ? 0 : 1;
    }

    private static void WriteHelp()
    {
        Console.WriteLine("""
            QuestIonAble File Manager CLI

            Usage:
              questionable-file-manager devices [--json] [--adb <path>]
              questionable-file-manager files list --serial <serial> [--path /sdcard] [--json]
              questionable-file-manager files pull --serial <serial> --remote <path> --output <path>
              questionable-file-manager files push --serial <serial> --file <path> --remote <path>
              questionable-file-manager apk list --serial <serial> [--json]
              questionable-file-manager apk inspect --file <file.apk> [--json]
              questionable-file-manager apk export --serial <serial> --package <package> --output <file.apk> [--overwrite] [--json]
              questionable-file-manager apk install --serial <serial> --file <file.apk> [options]
              questionable-file-manager apk launch --serial <serial> --file <file.apk> [--json]
              questionable-file-manager apk observe --serial <serial> --file <file.apk> [--json]
              questionable-file-manager apk install-bundle --serial <serial> --folder <apk-folder> [options]
              questionable-file-manager apk install-many --serial <host:port> --serial <host:port> --file <file.apk> [options]
              questionable-file-manager apk install-bundle-many --serial <host:port> --serial <host:port> --folder <apk-folder> [options]
              questionable-file-manager wifi enable --serial <usb-serial> [--port 5555] --confirm-wifi-adb
              questionable-file-manager wifi connect --host <quest-ip> [--port 5555] --confirm-wifi-adb
              questionable-file-manager wifi disconnect --host <quest-ip> [--port 5555] --confirm-wifi-adb
              questionable-file-manager kiosk status --serial <serial> [--json]
              questionable-file-manager kiosk install --serial <usb-serial> [--bundle <folder>] --confirm-kiosk-setup
              questionable-file-manager kiosk provision --serial <usb-serial> --confirm-kiosk-setup
              questionable-file-manager kiosk command --serial <serial> --command <typed-command> [--value <text>] [--confirm-kiosk-control] [--json]
              questionable-file-manager kiosk tags export --serial <serial> --output <app-tags.v1.json>
              questionable-file-manager kiosk tags import --serial <serial> --file <app-tags.v1.json> --confirm-kiosk-control
              questionable-file-manager kiosk-direct status --endpoint <http://quest-ip:39873> --pairing-code <code> [--json]
              questionable-file-manager kiosk-direct command --endpoint <url> --pairing-code <code> --command <typed-command> [--value <text>] [--confirm-kiosk-control] [--json]
              questionable-file-manager kiosk-direct tags export --endpoint <url> --pairing-code <code> --output <app-tags.v1.json>
              questionable-file-manager kiosk-direct tags import --endpoint <url> --pairing-code <code> --file <app-tags.v1.json> --confirm-kiosk-control
              questionable-file-manager kiosk-direct files list --endpoint <url> --pairing-code <code> [--json]
              questionable-file-manager kiosk-direct files upload --endpoint <url> --pairing-code <code> --file <path> [--name <staged-name>]
              questionable-file-manager kiosk-direct files download --endpoint <url> --pairing-code <code> --name <staged-name> --output <path> [--overwrite]
              questionable-file-manager kiosk-direct files delete --endpoint <url> --pairing-code <code> --name <staged-name> --confirm-file-delete
              questionable-file-manager kiosk-direct install --endpoint <url> --pairing-code <code> --file <base.apk> [--file <split.apk> ...] --confirm-local-install [--json]
              questionable-file-manager kiosk-direct install-status --endpoint <url> --pairing-code <code> --request-id <id> [--json]
              questionable-file-manager device status --serial <serial> [--json]
              questionable-file-manager device keep-awake --serial <serial> <--on|--off> [--duration-ms <60000..28800000>] --confirm-device-settings
              questionable-file-manager device performance --serial <serial> [--cpu <0-5>] [--gpu <0-5>] [--clear] --confirm-device-settings
              questionable-file-manager integration capabilities --json [--contract-version 1.0]
              questionable-file-manager integration observe --serial <serial> --json
              questionable-file-manager integration invoke --request <operation-request.v1.json> --json
              questionable-file-manager integration status --operation <operation-id> --json
              questionable-file-manager fleet status --json
              questionable-file-manager fleet install --confirm-fleet-install --json
              questionable-file-manager connectivity-profile status --device-id <fleet-device-id> --json
              questionable-file-manager connectivity-profile list --json
              questionable-file-manager connectivity-profile import --file <private-profile.json> --confirm-profile-write [--replace-existing] --json
              questionable-file-manager connectivity-profile import --stdin --confirm-profile-write [--replace-existing] --json
              questionable-file-manager connectivity-profile revoke --device-id <fleet-device-id> --confirm-profile-revoke --json
              questionable-file-manager-kiosk-v2-provider integration kiosk-v2-catalog --json < <strict-request.json>

            Install options:
              --no-replace                 Do not reinstall over an existing package.
              --downgrade                  Allow a lower version code.
              --grant-runtime-permissions  Ask Android to grant eligible runtime permissions.
              --test-only                  Allow an APK marked testOnly.
              --parallelism <1-16>          Bound concurrent installs (default: 4).

            Bundle install reads every top-level .apk file in the selected folder and
            sends the complete set through one serial-scoped adb install-multiple call.
            The apk commands are the default unattended installation route: after this
            PC is ADB-authorized, they do not require confirmation inside the headset.
            Parallel routes require at least two distinct connected Wi-Fi ADB serials,
            run one serial-scoped install per headset, and report partial failures.
            Enabling Wi-Fi ADB requires a USB-connected authorized headset and explicit
            operator confirmation. Connect/disconnect never reset the global ADB server.
            Rusty Kiosk is optional. Its typed host commands require the installed
            DUMP-protected operator provider and preserve Meta's attended permission prompts.
            The kiosk-direct commands use Rusty Kiosk's wearer-enabled, HMAC-authenticated
            local link and do not initialize ADB. The pairing code may be supplied through
            RUSTY_KIOSK_PAIRING_CODE. Direct files are confined to app-owned staging, and
            direct PackageInstaller is an attended fallback that stays pending until
            Android records one wearer decision for the app installation session.
            Keep-awake, proximity, and CPU/GPU changes require explicit confirmation and
            report effective readback; --clear restores app-controlled performance levels.
            Connectivity profiles are File Manager-owned current-user Credential Manager
            records. Status/list return only Fleet device IDs and sanitized state. Import
            accepts one strict private JSON document from a protected local file or standard
            input; serials, endpoints, and pairing codes are never command-line arguments
            or output. Replacement and revocation require their explicit confirmation flags.
            Split APK packages are refused by the single-APK export command.
            Fleet integration is optional and disabled by default. The normal executable
            exposes one exact-device read-only list or staged pull under adb-shared.
            Bounded push is advertised only by a host that injects current Quest identity
            and Manifold mutation-authority verification. It never overwrites and has no
            delete, move, multi-target, daemon, or WPF automation route. Durable status
            distinguishes final-path and partial-path uncertainty after interruption.
            The separate kiosk-v2-catalog subprocess route reads one strict request from
            standard input and resolves one opaque File Manager-owned profile from the
            current Windows user's Credential Manager. It is unavailable unless that
            profile was explicitly enrolled. Fleet never supplies an endpoint, pairing
            code, key, decrypted transport material, launch scope, or Manifold barrier.
            Fleet uses only the hash-pinned self-contained release artifact named
            questionable-file-manager-kiosk-v2-provider.exe, never a dotnet-build apphost.
            The optional fleet routes are a distribution bootstrap only. Configuration
            selects the canonical MesmerPrism Pages metadata descriptor (or an explicitly
            enabled development fixture) and pins both its descriptor key and Windows
            installer signer. Its strict v2 payload binds the exact numeric-version GitHub
            Release URL for RustyFleet-Setup.exe; Pages never carries the binary. Embedded
            release configuration ignores environment overrides. File Manager then invokes
            only Fleet's fixed plan and guided setup entrypoints. It accepts no URL, program,
            argument, credential, device, ADB, hotspot, or elevation option and reports
            only sanitized handoff metadata.
            """);
    }
}
