using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using QuestIonAbleFileManager.Core;

return await CliApplication.RunAsync(args);

internal static class CliApplication
{
    private const string ApkLaunchResultSchema = "questionable.file_manager.apk_launch_result.v1";
    private const string ApkPreflightResultSchema = "questionable.file_manager.apk_preflight_result.v1";
    private const string ApkDeployResultSchema = "questionable.file_manager.apk_deploy_result.v1";
    private const string ApkDiagnosticResultSchema = "questionable.file_manager.apk_diagnostic_result.v3";
    private const string ApkLaunchDiagnosticResultSchema =
        "questionable.file_manager.apk_launch_diagnostic_result.v1";
    private const string ApkStopResultSchema = "questionable.file_manager.apk_stop_result.v1";
    private const string ExactApkUninstallResultSchema =
        "questionable.file_manager.apk_uninstall_result.v1";
    private const string ApkPermissionObservationSchema =
        "questionable.file_manager.apk_permission_observation.v1";
    private const string ApkPropertyObservationSchema =
        "questionable.file_manager.apk_property_observation_result.v1";
    private const string ApkPropertyMutationSchema =
        "questionable.file_manager.apk_property_mutation_result.v1";
    private const string AdbForwardInventoryResultSchema = "questionable.file_manager.adb_forward_inventory_result.v1";
    private const string InspectedDeploymentContract =
        "questionable.file_manager.inspected_deployment.v5";
    private const string LauncherExportProofContract =
        "questionable.file_manager.launcher_export_proof.v2";
    private const string RuntimeObservationContract =
        "questionable.file_manager.app_runtime_observation.v5";

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
    private static readonly IReadOnlyList<CliAgentRouteAdmission> AgentRouteAdmissions =
    [
        new("apk_preflight", "apk preflight --serial <serial> --file <file.apk> --json",
            ["apk", "preflight", "--serial", "QUEST123", "--file", "example.apk", "--json"], true),
        new("apk_deploy", "apk deploy --serial <serial> --file <file.apk> [options] --json",
            ["apk", "deploy", "--serial", "QUEST123", "--file", "example.apk", "--json"], true),
        new("apk_diagnose", "apk diagnose --serial <serial> --file <file.apk> --output <new-folder> --json",
            ["apk", "diagnose", "--serial", "QUEST123", "--file", "example.apk", "--output", "capture", "--json"], true),
        new("apk_launch_diagnose", "apk launch-diagnose --serial <serial> --file <file.apk> --output <new-folder> --json",
            ["apk", "launch-diagnose", "--serial", "QUEST123", "--file", "example.apk", "--output", "capture", "--json"], true),
        new("apk_stop", "apk stop --serial <serial> --package <package> --confirm-package-stop --json",
            ["apk", "stop", "--serial", "QUEST123", "--package", "com.example.app", "--confirm-package-stop", "--json"], true),
        new("apk_exact_uninstall", "apk uninstall --serial <serial> --file <file.apk> --confirm-exact-apk-uninstall --json",
            ["apk", "uninstall", "--serial", "QUEST123", "--file", "example.apk", "--confirm-exact-apk-uninstall", "--json"], true),
        new("apk_property_observe", "apk properties observe --serial <serial> --file <apk> --manifest <manifest> --output <new-snapshot> --json",
            ["apk", "properties", "observe", "--serial", "QUEST123", "--file", "example.apk", "--manifest", "properties.json", "--output", "snapshot.json", "--json"], true),
        new("apk_property_clear", "apk properties clear --serial <serial> --file <apk> --manifest <manifest> --snapshot <snapshot> --confirm-exact-apk-property-mutation --json",
            ["apk", "properties", "clear", "--serial", "QUEST123", "--file", "example.apk", "--manifest", "properties.json", "--snapshot", "snapshot.json", "--confirm-exact-apk-property-mutation", "--json"], true),
        new("apk_property_restore", "apk properties restore --serial <serial> --file <apk> --manifest <manifest> --snapshot <snapshot> --confirm-exact-apk-property-mutation --json",
            ["apk", "properties", "restore", "--serial", "QUEST123", "--file", "example.apk", "--manifest", "properties.json", "--snapshot", "snapshot.json", "--confirm-exact-apk-property-mutation", "--json"], true),
        new("apk_permission_observation", "apk permissions --serial <serial> --package <package> --json",
            ["apk", "permissions", "--serial", "QUEST123", "--package", "com.example.app", "--json"], true),
        new("adb_forward_inventory", "adb forwards --serial <serial> --json",
            ["adb", "forwards", "--serial", "QUEST123", "--json"], true)
    ];

    internal static IReadOnlyList<CliAgentRouteAdmission> DescribeAgentRouteAdmissions() =>
        AgentRouteAdmissions;

    internal static bool TryClassifyAgentRoute(IReadOnlyList<string> arguments, out string routeId)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        routeId = string.Empty;
        if (arguments.Count < 2)
        {
            return false;
        }

        if (string.Equals(arguments[0], "apk", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(arguments[1], "properties", StringComparison.OrdinalIgnoreCase) &&
                arguments.Count > 2)
            {
                routeId = arguments[2].ToLowerInvariant() switch
                {
                    "observe" => "apk_property_observe",
                    "clear" => "apk_property_clear",
                    "restore" => "apk_property_restore",
                    _ => string.Empty
                };
                return routeId.Length > 0;
            }
            if (string.Equals(arguments[1], "stop", StringComparison.OrdinalIgnoreCase))
            {
                routeId = "apk_stop";
                return true;
            }
            if (string.Equals(arguments[1], "uninstall", StringComparison.OrdinalIgnoreCase))
            {
                routeId = "apk_exact_uninstall";
                return true;
            }
            if (HasFlag(arguments.ToArray(), "--json"))
            {
                routeId = arguments[1].ToLowerInvariant() switch
                {
                    "preflight" => "apk_preflight",
                    "deploy" => "apk_deploy",
                    "diagnose" => "apk_diagnose",
                    "launch-diagnose" => "apk_launch_diagnose",
                    "permissions" => "apk_permission_observation",
                    _ => string.Empty
                };
                return routeId.Length > 0;
            }
        }
        if (string.Equals(arguments[0], "adb", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(arguments[1], "forwards", StringComparison.OrdinalIgnoreCase))
        {
            routeId = "adb_forward_inventory";
            return true;
        }
        return false;
    }

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
            if (command == "operator-actions")
            {
                if (!arguments.SequenceEqual(["operator-actions", "--json"], StringComparer.Ordinal))
                {
                    throw new ArgumentException("Use exactly operator-actions --json.");
                }
                WriteJson(new
                {
                    schema = "questionable.file_manager.operator_actions.v1",
                    contracts = new
                    {
                        inspectedDeployment = InspectedDeploymentContract,
                        apkPreflightResult = ApkPreflightResultSchema,
                        apkDeployResult = ApkDeployResultSchema,
                        apkDiagnosticResult = ApkDiagnosticResultSchema,
                        apkLaunchDiagnosticResult = ApkLaunchDiagnosticResultSchema,
                        apkStopResult = ApkStopResultSchema,
                        exactApkUninstallResult = ExactApkUninstallResultSchema,
                        apkPropertyObservationResult = ApkPropertyObservationSchema,
                        apkPropertyMutationResult = ApkPropertyMutationSchema,
                        apkPermissionObservation = ApkPermissionObservationSchema,
                        adbForwardInventoryResult = AdbForwardInventoryResultSchema,
                        apkLaunchResult = ApkLaunchResultSchema,
                        launcherExportProof = LauncherExportProofContract,
                        runtimeObservation = RuntimeObservationContract
                    },
                    agentRoutes = OperatorActionRegistry.AgentRoutes,
                    actions = OperatorActionRegistry.Actions
                });
                return 0;
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
            if (TryClassifyAgentRoute(arguments, out var agentRouteId))
            {
                return await RunAgentRouteAsync(agentRouteId, arguments);
            }
            if (command == "apk" &&
                arguments.Length > 1 &&
                string.Equals(arguments[1], "launch", StringComparison.OrdinalIgnoreCase) &&
                HasFlag(arguments, "--json"))
            {
                return await RunApkLaunchJsonAsync(arguments);
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

    private static Task<int> RunAgentRouteAsync(string routeId, string[] arguments) => routeId switch
    {
        "apk_preflight" => RunApkPreflightJsonAsync(arguments),
        "apk_deploy" => RunApkDeployJsonAsync(arguments),
        "apk_diagnose" => RunApkDiagnoseJsonAsync(arguments),
        "apk_launch_diagnose" => RunApkLaunchDiagnoseJsonAsync(arguments),
        "apk_stop" => RunApkStopJsonAsync(arguments),
        "apk_exact_uninstall" => RunExactApkUninstallJsonAsync(arguments),
        "apk_property_observe" or "apk_property_clear" or "apk_property_restore" =>
            RunExactApkPropertiesJsonAsync(arguments),
        "apk_permission_observation" => RunApkPermissionObservationJsonAsync(arguments),
        "adb_forward_inventory" => RunAdbForwardInventoryJsonAsync(arguments),
        _ => throw new ArgumentException("The advertised agent route has no CLI dispatcher.", nameof(routeId))
    };

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
        var json = HasFlag(arguments, "--json");
        KioskDirectClientLease? lease = null;
        try
        {
            RejectDirectCredentialArguments(arguments);
            var action = RequireAction(arguments, "kiosk-direct");
            lease = await CreateKioskDirectClientAsync(arguments).ConfigureAwait(false);
            var executor = new KioskDirectOperatorExecutor(lease.Client);
            KioskDirectOperatorResult result;
            switch (action)
            {
                case "status":
                    result = await executor.ExecuteAsync(KioskDirectOperatorCommand.Adopt()).ConfigureAwait(false);
                    return await CompleteDirectAsync(result, lease, json, 0).ConfigureAwait(false);

                case "command":
                    {
                        var command = RustyKioskCommands.Parse(RequireOption(arguments, "--command"));
                        var readOnly = command is RustyKioskCommand.Status or RustyKioskCommand.CheckSetupHelper;
                        if (!readOnly)
                        {
                            RequireConfirmation(arguments, "--confirm-kiosk-control", "Direct Rusty Kiosk state change");
                        }
                        result = await executor.ExecuteAsync(KioskDirectOperatorCommand.Invoke(
                                command,
                                GetOption(arguments, "--value"),
                                operatorConfirmed: readOnly || HasFlag(arguments, "--confirm-kiosk-control")))
                            .ConfigureAwait(false);
                        return await CompleteDirectAsync(
                                result,
                                lease,
                                json,
                                DirectExitCode(result.Mutation.Stage))
                            .ConfigureAwait(false);
                    }

                case "request-status":
                    result = await executor.ExecuteAsync(
                            KioskDirectOperatorCommand.RequestStatus(
                                RequireOption(arguments, "--request-id")))
                        .ConfigureAwait(false);
                    return await CompleteDirectAsync(
                            result,
                            lease,
                            json,
                            DirectExitCode(result.Mutation.Stage))
                        .ConfigureAwait(false);

                case "request-cancel":
                    RequireConfirmation(arguments, "--confirm-kiosk-control", "Exact Direct Link request cancellation");
                    result = await executor.ExecuteAsync(
                            KioskDirectOperatorCommand.Cancel(
                                RequireOption(arguments, "--request-id"),
                                operatorConfirmed: true))
                        .ConfigureAwait(false);
                    return await CompleteDirectAsync(
                            result,
                            lease,
                            json,
                            DirectExitCode(result.Mutation.Stage))
                        .ConfigureAwait(false);

                case "tags":
                    {
                        if (arguments.Length < 3 || arguments[2].StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new ArgumentException("The kiosk-direct tags command requires export or import.");
                        }
                        var tagsAction = arguments[2].ToLowerInvariant();
                        if (tagsAction == "export")
                        {
                            result = await executor.ExecuteAsync(
                                    KioskDirectOperatorCommand.ExportTags(
                                        RequireOption(arguments, "--output")))
                                .ConfigureAwait(false);
                        }
                        else if (tagsAction == "import")
                        {
                            RequireConfirmation(arguments, "--confirm-kiosk-control", "Direct tag-file replacement");
                            result = await executor.ExecuteAsync(
                                    KioskDirectOperatorCommand.ImportTags(
                                        RequireOption(arguments, "--file"),
                                        operatorConfirmed: true))
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            throw new ArgumentException($"Unknown kiosk-direct tags action: {tagsAction}");
                        }
                        return await CompleteDirectAsync(
                                result,
                                lease,
                                json,
                                DirectExitCode(result.Mutation.Stage))
                            .ConfigureAwait(false);
                    }

                case "files":
                    {
                        if (arguments.Length < 3 || arguments[2].StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new ArgumentException("The kiosk-direct files command requires list, upload, download, or delete.");
                        }
                        var fileAction = arguments[2].ToLowerInvariant();
                        result = fileAction switch
                        {
                            "list" => await executor.ExecuteAsync(KioskDirectOperatorCommand.ListStaging()).ConfigureAwait(false),
                            "upload" => await ExecuteDirectUploadAsync(executor, arguments).ConfigureAwait(false),
                            "download" => await executor.ExecuteAsync(KioskDirectOperatorCommand.Download(
                                    RequireOption(arguments, "--name"),
                                    RequireOption(arguments, "--output"),
                                    HasFlag(arguments, "--overwrite")))
                                .ConfigureAwait(false),
                            "delete" => await ExecuteDirectDeleteAsync(executor, arguments).ConfigureAwait(false),
                            _ => throw new ArgumentException($"Unknown kiosk-direct files action: {fileAction}")
                        };
                        return await CompleteDirectAsync(
                                result,
                                lease,
                                json,
                                DirectExitCode(result.Mutation.Stage))
                            .ConfigureAwait(false);
                    }

                case "install":
                    {
                        RequireConfirmation(arguments, "--confirm-local-install", "Wearer-confirmed local APK installation");
                        var paths = GetOptions(arguments, "--file");
                        if (paths.Count == 0)
                        {
                            throw new ArgumentException("Pass one --file per APK part.");
                        }
                        result = await executor.ExecuteAsync(
                                KioskDirectOperatorCommand.Install(paths, operatorConfirmed: true))
                            .ConfigureAwait(false);
                        return await CompleteDirectAsync(
                                result,
                                lease,
                                json,
                                DirectExitCode(result.Mutation.Stage))
                            .ConfigureAwait(false);
                    }

                case "install-status":
                    result = await executor.ExecuteAsync(
                            KioskDirectOperatorCommand.InstallStatus(
                                RequireOption(arguments, "--request-id")))
                        .ConfigureAwait(false);
                    return await CompleteDirectAsync(
                            result,
                            lease,
                            json,
                            DirectExitCode(result.Mutation.Stage))
                        .ConfigureAwait(false);

                default:
                    throw new ArgumentException($"Unknown kiosk-direct action: {action}");
            }
        }
        catch (Exception exception)
        {
            if (lease is not null)
            {
                await lease.CloseAsync().ConfigureAwait(false);
            }
            if (json)
            {
                WriteDirectFailure(lease, exception);
                var cleanup = lease?.CleanupReceipt ??
                    (exception as RustyKioskUsbDirectBootstrapException)?.CleanupReceipt;
                if (cleanup?.Stage == OperatorMutationStage.CleanupUnknown)
                {
                    return 1;
                }
                return exception is ArgumentException or IOException ? 2 : 1;
            }
            throw;
        }
    }

    private static void RejectDirectCredentialArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Any(argument =>
                argument.StartsWith("--", StringComparison.Ordinal) &&
                !string.Equals(argument, "--credential-stdin", StringComparison.Ordinal) &&
                (argument.Contains("pairing", StringComparison.OrdinalIgnoreCase) ||
                 argument.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                 argument.Contains("secret", StringComparison.OrdinalIgnoreCase))))
        {
            throw new ArgumentException(
                "Direct Link credentials are accepted only through --credential-stdin or authorized USB bootstrap.");
        }
    }

    private static async Task<KioskDirectClientLease> CreateKioskDirectClientAsync(string[] arguments)
    {
        var serial = GetOption(arguments, "--serial");
        if (serial is not null)
        {
            RequireConfirmation(
                arguments,
                "--confirm-kiosk-direct-bootstrap",
                "Authorized-USB Direct Link bootstrap");
            if (GetOption(arguments, "--endpoint") is not null ||
                HasFlag(arguments, "--credential-stdin"))
            {
                throw new ArgumentException(
                    "Authorized-USB Direct Link bootstrap cannot be mixed with a manual endpoint or credential.");
            }
            var product = RustyKioskProductContract.Parse(
                RequireOption(arguments, "--product-channel"));
            var adbClient = AdbClient.CreateDefault(GetOption(arguments, "--adb"));
            var session = await new RustyKioskUsbDirectLinkBootstrapper(adbClient)
                .ConnectAsync(serial, product.Channel, operatorConfirmed: true)
                .ConfigureAwait(false);
            return KioskDirectClientLease.FromUsb(session);
        }

        var endpoint = RequireOption(arguments, "--endpoint");
        if (!HasFlag(arguments, "--credential-stdin"))
        {
            throw new ArgumentException(
                "Manual Direct Link requires --credential-stdin; pairing codes are never read from arguments or environment variables.");
        }
        var pairingCode = await ReadBoundedSecretLineAsync().ConfigureAwait(false);
        try
        {
            return KioskDirectClientLease.FromManual(
                new RustyKioskDirectClient(RustyKioskDirectEndpoint.Parse(endpoint, pairingCode)));
        }
        finally
        {
            pairingCode = string.Empty;
        }
    }

    private static async Task<KioskDirectOperatorResult> ExecuteDirectUploadAsync(
        KioskDirectOperatorExecutor executor,
        string[] arguments)
    {
        RequireConfirmation(arguments, "--confirm-staging-upload", "Direct staged-file upload");
        return await executor.ExecuteAsync(KioskDirectOperatorCommand.Upload(
                RequireOption(arguments, "--file"),
                GetOption(arguments, "--name"),
                operatorConfirmed: true))
            .ConfigureAwait(false);
    }

    private static async Task<KioskDirectOperatorResult> ExecuteDirectDeleteAsync(
        KioskDirectOperatorExecutor executor,
        string[] arguments)
    {
        RequireConfirmation(arguments, "--confirm-file-delete", "Direct staged-file deletion");
        return await executor.ExecuteAsync(KioskDirectOperatorCommand.Delete(
                RequireOption(arguments, "--name"),
                operatorConfirmed: true))
            .ConfigureAwait(false);
    }

    private static void WriteDirectResult(
        KioskDirectOperatorResult result,
        KioskDirectClientLease lease,
        bool json)
    {
        if (json)
        {
            WriteJson(new
            {
                schema = "questionable.file_manager.kiosk_direct_cli_result.v1",
                succeeded = true,
                transport = lease.IsUsb ? "authorized_usb_session" : "manual_direct",
                bootstrap = lease.UsbReceipt,
                cleanup = lease.CleanupReceipt,
                mutation = result.Mutation,
                status = result.Status is null ? null : new
                {
                    result.Status.Schema,
                    result.Status.InstallerAllowed,
                    result.Status.StagingDirectoryKind,
                    result.Status.Message
                },
                kiosk = result.KioskResult,
                request = result.RequestReceipt,
                files = result.StagedFiles,
                file = result.StagedFile,
                local_file_name = result.LocalFileName,
                install = result.InstallReceipt
            });
            return;
        }

        Console.WriteLine($"Sync: {result.Mutation.Stage.ToString().ToLowerInvariant()} · {result.Mutation.Message}");
        if (lease.CleanupReceipt is { } cleanup)
        {
            Console.WriteLine($"Cleanup: {cleanup.Stage.ToString().ToLowerInvariant()} · {cleanup.Message}");
        }
        if (result.Mutation.RequestId is not null)
        {
            Console.WriteLine($"Request: {result.Mutation.RequestId}");
        }
        if (result.StagedFiles is not null)
        {
            foreach (var file in result.StagedFiles)
            {
                Console.WriteLine($"{file.Bytes}\t{file.Name}");
            }
        }
    }

    private static void WriteDirectFailure(
        KioskDirectClientLease? lease,
        Exception exception)
    {
        var (reasonCode, message) = exception switch
        {
            RustyKioskUsbDirectBootstrapException => (
                "usb_bootstrap_failed",
                "Authorized-USB Direct Link bootstrap failed; inspect the typed cleanup receipt before retry."),
            SensitiveCommandException => (
                "usb_provider_rejected",
                "The fixed on-device bootstrap provider rejected or malformed the sensitive request."),
            TimeoutException => (
                "bounded_timeout",
                "The Direct Link operation did not converge within its bounded window."),
            HttpRequestException => (
                "direct_transport_unavailable",
                "The authenticated Direct Link transport was unavailable."),
            InvalidDataException => (
                "contract_rejected",
                "The Direct Link response did not match the fixed authenticated contract."),
            ArgumentException or IOException => (
                "input_rejected",
                "The Direct Link request input was rejected before a confirmed operation result."),
            _ => (
                "operation_failed",
                "The Direct Link operation failed before a confirmed result was available.")
        };
        WriteJson(new
        {
            schema = "questionable.file_manager.kiosk_direct_cli_result.v1",
            succeeded = false,
            transport = lease is null
                ? exception is RustyKioskUsbDirectBootstrapException
                    ? "authorized_usb_session"
                    : "not_established"
                : lease.IsUsb ? "authorized_usb_session" : "manual_direct",
            bootstrap = lease?.UsbReceipt,
            cleanup = lease?.CleanupReceipt ??
                (exception as RustyKioskUsbDirectBootstrapException)?.CleanupReceipt,
            failure = new
            {
                reason_code = reasonCode,
                message
            }
        });
    }

    private static async Task<int> CompleteDirectAsync(
        KioskDirectOperatorResult result,
        KioskDirectClientLease lease,
        bool json,
        int operationExitCode)
    {
        await lease.CloseAsync().ConfigureAwait(false);
        WriteDirectResult(result, lease, json);
        return lease.CleanupReceipt?.Stage == OperatorMutationStage.CleanupUnknown
            ? 1
            : operationExitCode;
    }

    private static int DirectExitCode(OperatorMutationStage stage) => stage switch
    {
        OperatorMutationStage.Confirmed or OperatorMutationStage.Cancelled => 0,
        OperatorMutationStage.Pending or
        OperatorMutationStage.PendingWearerAction or
        OperatorMutationStage.TimedOut => 3,
        OperatorMutationStage.Rejected or OperatorMutationStage.Expired => 2,
        _ => 1
    };

    private static async Task<string> ReadBoundedSecretLineAsync()
    {
        await using var input = Console.OpenStandardInput();
        using var memory = new MemoryStream();
        var buffer = new byte[128];
        try
        {
            while (memory.Length <= 512)
            {
                var read = await input.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                var accepted = newline >= 0 ? newline : read;
                memory.Write(buffer, 0, accepted);
                if (newline >= 0)
                {
                    break;
                }
            }
            if (memory.Length is < 1 or > 512)
            {
                throw new ArgumentException("The standard-input credential is empty or oversized.");
            }
            var credentialBytes = memory.ToArray();
            try
            {
                return System.Text.Encoding.UTF8.GetString(credentialBytes).Trim();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(credentialBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (memory.TryGetBuffer(out var segment) && segment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(segment.Array);
            }
        }
    }

    private sealed class KioskDirectClientLease : IAsyncDisposable
    {
        private readonly RustyKioskUsbDirectLinkSession? _usb;

        private KioskDirectClientLease(
            RustyKioskDirectClient client,
            RustyKioskUsbDirectLinkSession? usb)
        {
            Client = client;
            _usb = usb;
        }

        public RustyKioskDirectClient Client { get; }
        public bool IsUsb => _usb is not null;
        public RustyKioskUsbDirectLinkReceipt? UsbReceipt => _usb?.Receipt;
        public RustyKioskUsbDirectCleanupReceipt? CleanupReceipt => _usb?.CleanupReceipt;

        public static KioskDirectClientLease FromManual(RustyKioskDirectClient client) =>
            new(client, null);

        public static KioskDirectClientLease FromUsb(RustyKioskUsbDirectLinkSession session) =>
            new(session.Client, session);

        public async Task CloseAsync()
        {
            if (_usb is not null)
            {
                await _usb.CloseAsync().ConfigureAwait(false);
            }
            else
            {
                Client.Dispose();
            }
        }

        public async ValueTask DisposeAsync() =>
            await CloseAsync().ConfigureAwait(false);
    }

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

            case "deploy":
                return await RunApkDeployAsync(executor, arguments);

            case "preflight":
                return await RunApkPreflightAsync(executor, arguments);

            case "diagnose":
                return await RunApkDiagnoseAsync(executor, arguments);

            case "launch-diagnose":
                return await RunApkLaunchDiagnoseAsync(executor, arguments);

            case "launch":
                return await RunApkLaunchAsync(executor, arguments);

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
                        Console.WriteLine($"Foreground components: {string.Join(", ", observation.ForegroundComponents)}");
                        Console.WriteLine($"Top-resumed components: {string.Join(", ", observation.TopResumedComponents)}");
                        Console.WriteLine($"Global mCurrentFocus: {observation.GlobalFocus.CurrentFocus.State} ({observation.GlobalFocus.CurrentFocus.RecordCount} records)");
                        Console.WriteLine($"Global mFocusedApp: {observation.GlobalFocus.FocusedApp.State} ({observation.GlobalFocus.FocusedApp.RecordCount} records)");
                        Console.WriteLine($"Blocking system components: {string.Join(", ", observation.BlockingSystemComponents)}");
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

    private static async Task<int> RunApkPreflightAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        var json = HasFlag(arguments, "--json");
        try
        {
            var execution = await executor.ExecuteAsync(
                OperatorCommands.PreflightInspectedApp(
                    RequireOption(arguments, "--serial"),
                    RequireOption(arguments, "--file")));
            var result = execution.ApkPreflightResult ??
                throw new InvalidOperationException("Inspected APK preflight returned no result.");
            if (json)
            {
                WriteJson(new
                {
                    schema = ApkPreflightResultSchema,
                    succeeded = true,
                    complete = true,
                    ready = result.ReadyForDeploy,
                    result,
                    failure = (object?)null
                });
            }
            else
            {
                Console.WriteLine(
                    $"Artifact: {result.Artifact.Identity.PackageName} " +
                    $"versionCode={result.Artifact.Identity.VersionCode} sha256={result.Artifact.Sha256}");
                Console.WriteLine(
                    $"Device: {result.Serial} state={result.Device?.State ?? "absent"} " +
                    $"api={result.DeviceApiLevel?.ToString() ?? "unknown"}");
                Console.WriteLine($"Installed match: {result.InstalledMatch}");
                Console.WriteLine(
                    $"Ready: deploy={result.ReadyForDeploy.ToString().ToLowerInvariant()}, " +
                    $"launch={result.ReadyForLaunch.ToString().ToLowerInvariant()}, " +
                    $"diagnose={result.ReadyForDiagnose.ToString().ToLowerInvariant()}");
            }
            return result.ReadyForDeploy ? 0 : 3;
        }
        catch (Exception exception) when (json)
        {
            return WriteApkPreflightFailure(exception);
        }
    }

    private static async Task<int> RunApkPreflightJsonAsync(string[] arguments)
    {
        try
        {
            var client = AdbClient.CreateDefault(GetOption(arguments, "--adb"));
            return await RunApkPreflightAsync(new OperatorCommandExecutor(client), arguments);
        }
        catch (Exception exception)
        {
            return WriteApkPreflightFailure(exception);
        }
    }

    private static int WriteApkPreflightFailure(Exception exception)
    {
        var failure = ClassifyApkPreflightFailure(exception);
        WriteJson(new
        {
            schema = ApkPreflightResultSchema,
            succeeded = false,
            complete = false,
            ready = false,
            result = (object?)null,
            failure = new
            {
                code = failure.Code,
                message = failure.Message,
                state_change_possible = false
            }
        });
        return failure.ExitCode;
    }

    private static (string Code, string Message, int ExitCode)
        ClassifyApkPreflightFailure(Exception exception)
    {
        if (exception is FileNotFoundException missingFile &&
            !string.IsNullOrWhiteSpace(missingFile.FileName) &&
            string.Equals(Path.GetExtension(missingFile.FileName), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "input_rejected",
                "The inspected APK preflight input could not be admitted.",
                2);
        }
        if (exception is ArgumentException or SplitPackageException)
        {
            return (
                "input_rejected",
                "The inspected APK preflight input could not be admitted.",
                2);
        }
        if (exception is AdbCommandException or TimeoutException)
        {
            return (
                "device_read_failed",
                "A fixed read-only ADB discovery or serial-scoped preflight command failed.",
                1);
        }
        if (exception is InvalidDataException)
        {
            return (
                "proof_rejected",
                "Artifact, device API, installed-byte, or launcher evidence was malformed.",
                1);
        }
        if (exception is FileNotFoundException or InvalidOperationException)
        {
            return (
                "tool_unavailable",
                "Required Android Platform Tools, SDK Build Tools, Java, or exact-byte streaming support is unavailable.",
                1);
        }
        if (exception is IOException or UnauthorizedAccessException)
        {
            return (
                "artifact_io_failed",
                "The immutable APK could not be admitted for read-only preflight.",
                1);
        }
        return (
            "preflight_failed",
            "The inspected APK preflight did not complete.",
            1);
    }

    private static async Task<int> RunApkDiagnoseAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        var json = HasFlag(arguments, "--json");
        try
        {
            var execution = await executor.ExecuteAsync(
                OperatorCommands.DiagnoseInspectedApp(
                    RequireOption(arguments, "--serial"),
                    RequireOption(arguments, "--file"),
                    RequireOption(arguments, "--output")));
            var result = execution.ApkDiagnosticBundleResult ??
                throw new InvalidOperationException("Inspected APK diagnostics returned no result.");
            var complete = result.FailedCaptureCount == 0;
            if (json)
            {
                WriteJson(new
                {
                    schema = ApkDiagnosticResultSchema,
                    succeeded = true,
                    complete,
                    result = CreateSanitizedApkDiagnosticResult(result),
                    failure = (object?)null
                });
            }
            else
            {
                Console.WriteLine($"Diagnostic bundle: {result.OutputDirectory}");
                Console.WriteLine(
                    $"Captured {result.Files.Count} files; failed fixed captures: {result.FailedCaptureCount}.");
            }
            return complete ? 0 : 3;
        }
        catch (Exception exception) when (json)
        {
            return WriteApkDiagnosticFailure(exception);
        }
    }

    private static async Task<int> RunApkDiagnoseJsonAsync(string[] arguments)
    {
        try
        {
            var client = AdbClient.CreateDefault(GetOption(arguments, "--adb"));
            return await RunApkDiagnoseAsync(new OperatorCommandExecutor(client), arguments);
        }
        catch (Exception exception)
        {
            return WriteApkDiagnosticFailure(exception);
        }
    }

    private static async Task<int> RunApkLaunchDiagnoseAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        try
        {
            var command = OperatorCommands.ParseLaunchDiagnosticCliArguments(arguments);
            var execution = await executor.ExecuteAsync(command).ConfigureAwait(false);
            var result = execution.ApkLaunchDiagnosticBundleResult ??
                throw new InvalidOperationException("APK launch diagnostics returned no result.");
            var completed = result.Disposition == ApkLaunchDiagnosticDisposition.Completed;
            WriteJson(new
            {
                schema = ApkLaunchDiagnosticResultSchema,
                succeeded = completed,
                complete = completed,
                mutation = execution.MutationReceipt,
                result = CreateSanitizedApkLaunchDiagnosticResult(result),
                failure = completed ? null : new
                {
                    code = result.Disposition.ToString().ToLowerInvariant(),
                    message = result.DispositionDetail,
                    state_change_possible = result.Attempt.DispatchAttempted
                }
            });
            return result.Disposition switch
            {
                ApkLaunchDiagnosticDisposition.Completed => 0,
                ApkLaunchDiagnosticDisposition.RejectedBeforeDispatch => 2,
                ApkLaunchDiagnosticDisposition.LaunchPending => 3,
                _ => 4
            };
        }
        catch (Exception exception)
        {
            return WriteApkLaunchDiagnosticFailure(exception);
        }
    }

    private static async Task<int> RunApkLaunchDiagnoseJsonAsync(string[] arguments)
    {
        try
        {
            var client = AdbClient.CreateDefault(GetOption(arguments, "--adb"));
            return await RunApkLaunchDiagnoseAsync(
                new OperatorCommandExecutor(client),
                arguments).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return WriteApkLaunchDiagnosticFailure(exception);
        }
    }

    private static object CreateSanitizedApkLaunchDiagnosticResult(
        ApkLaunchDiagnosticBundleResult result) => new
    {
        diagnosticContract = result.DiagnosticContract,
        disposition = result.Disposition,
        dispositionDetail = result.DispositionDetail,
        dispatchAttempted = result.Attempt.DispatchAttempted,
        componentObservedResumed = result.Attempt.Launch?.ComponentObservedResumed,
        currentPackageProcessCount = result.Attempt.CurrentPackageProcessIds.Count,
        capture = new
        {
            result.Capture.SizeBytes,
            result.Capture.Sha256,
            result.Capture.PostActionWindowElapsed,
            result.Capture.OutputLimitReached,
            result.Capture.CaptureExitedEarly,
            result.Capture.ProcessTreeCleanupSucceeded,
            result.Capture.CaptureExitCode
        },
        manifest = new
        {
            result.ManifestSizeBytes,
            result.ManifestSha256,
            result.PublishedAtRequestedPath,
            result.BundleLeafName
        },
        limitations = new
        {
            applicationReadiness = "unknown",
            openXrReadiness = "unknown",
            wearerVisibility = "unknown",
            appSemanticAcceptance = false,
            screenshotOrRecording = false,
            retryPerformed = false
        }
    };

    internal static int WriteApkLaunchDiagnosticFailure(Exception exception)
    {
        var dispatched = exception as OperatorMutationExecutionException;
        var inputRejected = exception is ArgumentException or FileNotFoundException or
            DirectoryNotFoundException or SplitPackageException;
        WriteJson(new
        {
            schema = ApkLaunchDiagnosticResultSchema,
            succeeded = false,
            complete = false,
            mutation = dispatched?.MutationReceipt,
            result = (object?)null,
            failure = new
            {
                code = dispatched is not null
                    ? "launch_pending"
                    : inputRejected ? "input_rejected" : "launch_diagnostic_failed",
                message = dispatched is not null
                    ? "Launch was dispatched, but exact terminal evidence is unavailable."
                    : inputRejected
                        ? "The exact launch-diagnostic input or new output directory was rejected."
                        : "Launch diagnostics failed without exposing private artifact, target, or log details.",
                state_change_possible = dispatched is not null || !inputRejected
            }
        });
        return dispatched is not null ? 3 : inputRejected ? 2 : 1;
    }

    private static int WriteApkDiagnosticFailure(Exception exception)
    {
        var failure = ClassifyApkDiagnosticFailure(exception);
        WriteJson(new
        {
            schema = ApkDiagnosticResultSchema,
            succeeded = false,
            complete = false,
            result = (object?)null,
            failure = new
            {
                code = failure.Code,
                message = failure.Message,
                state_change_possible = false
            }
        });
        return failure.ExitCode;
    }

    private static object CreateSanitizedApkDiagnosticResult(ApkDiagnosticBundleResult result) => new
    {
        diagnosticContract = result.DiagnosticContract,
        capturedAt = result.CapturedAt,
        fileCount = result.Files.Count,
        failedCaptureCount = result.FailedCaptureCount,
        runtime = new
        {
            observationContract = result.Runtime.ObservationContract,
            globalFocus = CreateSanitizedGlobalFocus(result.Runtime.GlobalFocus)
        },
        captures = result.Files.Select(file => new
        {
            captureKind = file.CaptureKind,
            observationSource = file.ObservationSource,
            commandSemantic = file.CommandSemantic,
            exitCode = file.CommandExitCode,
            sizeBytes = file.SizeBytes,
            truncated = file.Truncated,
            sha256 = file.Sha256
        }),
        limitations = new
        {
            applicationReadiness = "unknown",
            applicationReadinessAuthority = false,
            openXrReadiness = "unknown",
            openXrReadinessAuthority = false,
            wearerVisibility = "unknown",
            panelPausedState = "unknown",
            focusedOrSubmittedFrameStability = "unknown",
            appOwnedHandoffMarkers = "unknown"
        }
    };

    private static object CreateSanitizedGlobalFocus(AndroidGlobalFocusObservation focus) => new
    {
        observationContract = focus.ObservationContract,
        currentFocus = CreateSanitizedGlobalFocusFact(focus.CurrentFocus),
        focusedApp = CreateSanitizedGlobalFocusFact(focus.FocusedApp)
    };

    private static object CreateSanitizedGlobalFocusFact(AndroidGlobalFocusRecord fact) => new
    {
        state = fact.State,
        recordCount = fact.RecordCount,
        components = fact.Components,
        emptyRecordCount = fact.EmptyRecordCount,
        malformedRecordCount = fact.MalformedRecordCount,
        recordsTruncated = fact.RecordsTruncated,
        observationSource = fact.ObservationSource,
        sourceExitCode = fact.SourceExitCode
    };

    private static (string Code, string Message, int ExitCode)
        ClassifyApkDiagnosticFailure(Exception exception)
    {
        if (exception is ArgumentException or FileNotFoundException or DirectoryNotFoundException or SplitPackageException)
        {
            return (
                "input_rejected",
                "The inspected APK diagnostic input or new output directory could not be admitted.",
                2);
        }
        if (exception is PackageNotInstalledException)
        {
            return (
                "package_not_installed",
                "The inspected APK is not installed on the selected headset.",
                1);
        }
        if (exception is InvalidDataException)
        {
            return (
                "installed_proof_rejected",
                "Installed identity or exact base-APK byte proof was rejected.",
                1);
        }
        if (exception is AdbCommandException or TimeoutException)
        {
            return (
                "device_read_failed",
                "A fixed serial-scoped diagnostic readback did not complete.",
                1);
        }
        if (exception is IOException or UnauthorizedAccessException)
        {
            return (
                "artifact_or_output_io_failed",
                "The immutable APK admission or private diagnostic bundle write could not be completed.",
                1);
        }
        return (
            "diagnostic_failed",
            "The inspected APK diagnostic bundle did not complete.",
            1);
    }

    private static async Task<int> RunApkDeployAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        var json = HasFlag(arguments, "--json");
        try
        {
            var execution = await executor.ExecuteAsync(
                OperatorCommands.DeployInspectedApp(
                    RequireOption(arguments, "--serial"),
                    RequireOption(arguments, "--file"),
                    ReadInstallOptions(arguments)));
            var result = execution.InspectedApkDeploymentResult ??
                throw new InvalidOperationException("Inspected APK deployment returned no result.");
            if (json)
            {
                WriteJson(new
                {
                    schema = ApkDeployResultSchema,
                    succeeded = true,
                    mutation = execution.MutationReceipt,
                    result,
                    failure = (object?)null
                });
            }
            else
            {
                Console.WriteLine(
                    $"Installed {result.Install.Artifact.Identity.PackageName} " +
                    $"sha256={result.Install.Artifact.Sha256}.");
                Console.WriteLine(
                    $"Launched {result.Launch.Component}; " +
                    $"resumed={result.Launch.ComponentObservedResumed.ToString().ToLowerInvariant()}.");
                Console.WriteLine(
                    $"Runtime: process-alive={result.Runtime.ProcessAlive.ToString().ToLowerInvariant()}, " +
                    $"foreground={result.Runtime.IsForeground.ToString().ToLowerInvariant()}, " +
                    $"top-resumed={result.Runtime.IsTopResumed.ToString().ToLowerInvariant()}, " +
                    $"global-current-focus={result.Runtime.GlobalFocus.CurrentFocus.State.ToString().ToLowerInvariant()}, " +
                    $"global-focused-app={result.Runtime.GlobalFocus.FocusedApp.State.ToString().ToLowerInvariant()}, " +
                    $"blocking-system-components={result.Runtime.BlockingSystemComponents.Count}.");
                WriteMutationReceipt(execution.MutationReceipt);
            }
            return 0;
        }
        catch (Exception exception) when (json)
        {
            return WriteApkDeployFailure(exception);
        }
    }

    private static async Task<int> RunApkDeployJsonAsync(string[] arguments)
    {
        try
        {
            var client = AdbClient.CreateDefault(GetOption(arguments, "--adb"));
            return await RunApkDeployAsync(new OperatorCommandExecutor(client), arguments);
        }
        catch (Exception exception)
        {
            return WriteApkDeployFailure(exception);
        }
    }

    private static int WriteApkDeployFailure(Exception exception)
    {
        var failure = ClassifyApkDeployFailure(exception);
        WriteJson(new
        {
            schema = ApkDeployResultSchema,
            succeeded = false,
            mutation = (object?)null,
            result = (object?)null,
            failure = new
            {
                code = failure.Code,
                message = failure.Message,
                state_change_possible = failure.StateChangePossible
            }
        });
        return failure.ExitCode;
    }

    private static (string Code, string Message, bool StateChangePossible, int ExitCode)
        ClassifyApkDeployFailure(Exception exception)
    {
        if (exception is ArgumentException or FileNotFoundException or SplitPackageException)
        {
            return (
                "input_rejected",
                "The inspected APK deployment input could not be admitted.",
                false,
                2);
        }
        if (exception is AdbCommandException adbFailure && IsInstallCommand(adbFailure.Result))
        {
            return (
                "install_failed",
                "Android Package Manager did not complete the fixed inspected APK install.",
                true,
                1);
        }
        if (exception is AdbCommandException launchFailure && IsLauncherStartCommand(launchFailure.Result))
        {
            return (
                "launch_dispatch_failed",
                "The proven launcher component could not be started after installation.",
                true,
                1);
        }
        if (exception is InvalidDataException)
        {
            return (
                "proof_rejected",
                "Artifact, installed-byte, or launcher safety proof was rejected.",
                true,
                1);
        }
        if (exception is IOException)
        {
            return (
                "artifact_io_failed",
                "The immutable APK or exact installed-byte readback could not be completed.",
                true,
                1);
        }
        if (exception is AdbCommandException)
        {
            return (
                "device_command_failed",
                "A fixed serial-scoped deployment or observation command failed.",
                true,
                1);
        }
        return (
            "deploy_failed",
            "The inspected APK deployment did not complete.",
            true,
            1);
    }

    private static bool IsInstallCommand(CommandResult result) =>
        result.Arguments.Count >= 3 &&
        string.Equals(result.Arguments[2], "install", StringComparison.Ordinal);

    private static async Task<int> RunApkLaunchAsync(
        OperatorCommandExecutor executor,
        string[] arguments)
    {
        var json = HasFlag(arguments, "--json");
        try
        {
            var execution = await executor.ExecuteAsync(
                OperatorCommands.LaunchInspectedApp(
                    RequireOption(arguments, "--serial"),
                    RequireOption(arguments, "--file")));
            if (json)
            {
                WriteJson(new
                {
                    schema = ApkLaunchResultSchema,
                    succeeded = true,
                    mutation = execution.MutationReceipt,
                    result = execution.ResolvedAppLaunchResult,
                    failure = (object?)null
                });
            }
            else
            {
                Console.WriteLine(execution.ResolvedAppLaunchResult?.Component);
                WriteMutationReceipt(execution.MutationReceipt);
            }
            return 0;
        }
        catch (Exception exception) when (json)
        {
            return WriteApkLaunchFailure(exception);
        }
    }

    private static async Task<int> RunApkLaunchJsonAsync(string[] arguments)
    {
        try
        {
            var client = AdbClient.CreateDefault(GetOption(arguments, "--adb"));
            return await RunApkLaunchAsync(new OperatorCommandExecutor(client), arguments);
        }
        catch (Exception exception)
        {
            return WriteApkLaunchFailure(exception);
        }
    }

    private static int WriteApkLaunchFailure(Exception exception)
    {
        var failure = ClassifyApkLaunchFailure(exception);
        WriteJson(new
        {
            schema = ApkLaunchResultSchema,
            succeeded = false,
            mutation = (object?)null,
            result = (object?)null,
            failure = new
            {
                code = failure.Code,
                message = failure.Message,
                dispatch_attempted = failure.DispatchAttempted
            }
        });
        return failure.ExitCode;
    }

    private static (string Code, string Message, bool DispatchAttempted, int ExitCode)
        ClassifyApkLaunchFailure(Exception exception)
    {
        if (exception is ArgumentException or FileNotFoundException or IOException or SplitPackageException)
        {
            return (
                "input_rejected",
                "The inspected-app launch input could not be admitted.",
                false,
                2);
        }
        if (exception is InvalidDataException)
        {
            return (
                "pre_dispatch_proof_rejected",
                "Installed identity or launcher safety proof was rejected before dispatch.",
                false,
                1);
        }
        if (exception is AdbCommandException adbFailure && IsLauncherStartCommand(adbFailure.Result))
        {
            return (
                "launch_dispatch_failed",
                "The proven launcher component could not be started.",
                true,
                1);
        }
        if (exception is AdbCommandException)
        {
            return (
                "pre_dispatch_command_failed",
                "A fixed pre-dispatch device readback command failed.",
                false,
                1);
        }
        return (
            "launch_failed",
            "The inspected-app launch did not complete.",
            false,
            1);
    }

    private static bool IsLauncherStartCommand(CommandResult result) =>
        result.Arguments.Count >= 5 &&
        string.Equals(result.Arguments[2], "shell", StringComparison.Ordinal) &&
        string.Equals(result.Arguments[3], "am", StringComparison.Ordinal) &&
        string.Equals(result.Arguments[4], "start", StringComparison.Ordinal);

    private static async Task<int> RunApkStopJsonAsync(string[] arguments)
    {
        try
        {
            var command = OperatorCommands.ParsePackageStopCliArguments(arguments);
            var client = AdbClient.CreateDefault();
            var execution = await new OperatorCommandExecutor(client).ExecuteAsync(command);
            var result = execution.PackageStopResult ??
                throw new InvalidOperationException("Exact-package stop returned no result.");
            WriteJson(new
            {
                schema = ApkStopResultSchema,
                succeeded = true,
                mutation = execution.MutationReceipt,
                result,
                failure = (object?)null
            });
            return 0;
        }
        catch (Exception exception)
        {
            return WriteApkStopFailure(exception);
        }
    }

    private static int WriteApkStopFailure(Exception exception)
    {
        var failure = ClassifyApkStopFailure(exception);
        WriteJson(new
        {
            schema = ApkStopResultSchema,
            succeeded = false,
            mutation = (object?)null,
            result = (object?)null,
            failure = new
            {
                code = failure.Code,
                message = failure.Message,
                state_change_possible = failure.StateChangePossible
            }
        });
        return failure.ExitCode;
    }

    private static (string Code, string Message, bool StateChangePossible, int ExitCode)
        ClassifyApkStopFailure(Exception exception)
    {
        if (exception is ArgumentException)
        {
            return (
                "input_rejected",
                "The exact-package stop input was rejected before device dispatch.",
                false,
                2);
        }
        if (exception is PackageNotInstalledException)
        {
            return (
                "pre_stop_package_absent",
                "The exact package was not installed on the selected serial before dispatch.",
                false,
                1);
        }
        if (exception is PackageStopDispatchException)
        {
            return (
                "stop_dispatch_failed",
                "The fixed current-user package-stop command may have changed Android state but did not complete.",
                true,
                1);
        }
        if (exception is PackageStopReadbackException)
        {
            return (
                "post_stop_readback_failed",
                "The fixed package-stop command was sent, but exact-package readback did not complete.",
                true,
                1);
        }
        if (exception is InvalidDataException)
        {
            return (
                "pre_stop_proof_rejected",
                "Exact-package pre-stop readback was malformed or incomplete.",
                false,
                1);
        }
        if (exception is AdbCommandException)
        {
            return (
                "pre_stop_read_failed",
                "A fixed serial-scoped pre-stop package readback command failed.",
                false,
                1);
        }
        if (exception is OperationCanceledException)
        {
            return (
                "pre_stop_cancelled",
                "The exact-package stop was cancelled before dispatch began.",
                false,
                1);
        }
        return (
            "stop_failed",
            "The exact-package stop did not complete before dispatch was proven.",
            false,
            1);
    }

    private static async Task<int> RunExactApkUninstallJsonAsync(string[] arguments)
    {
        try
        {
            var command = OperatorCommands.ParseExactApkUninstallCliArguments(arguments);
            var client = AdbClient.CreateDefault();
            var execution = await new OperatorCommandExecutor(client).ExecuteAsync(command);
            var result = execution.ExactApkUninstallResult ??
                throw new InvalidOperationException("Exact inspected-APK uninstall returned no result.");
            var confirmed = result.Confirmed;
            WriteJson(new
            {
                schema = ExactApkUninstallResultSchema,
                succeeded = confirmed,
                mutation = execution.MutationReceipt,
                result,
                failure = confirmed
                    ? (object?)null
                    : new
                    {
                        code = result.Disposition == ExactApkUninstallDisposition.StillPresent
                            ? "still_present"
                            : "cleanup_unknown",
                        message = result.Detail,
                        state_change_possible = true
                    }
            });
            return confirmed ? 0 : 1;
        }
        catch (Exception exception)
        {
            var failure = ClassifyExactApkUninstallFailure(exception);
            WriteJson(new
            {
                schema = ExactApkUninstallResultSchema,
                succeeded = false,
                mutation = (object?)null,
                result = (object?)null,
                failure = new
                {
                    code = failure.Code,
                    message = failure.Message,
                    state_change_possible = false
                }
            });
            return failure.ExitCode;
        }
    }

    private static (string Code, string Message, int ExitCode)
        ClassifyExactApkUninstallFailure(Exception exception)
    {
        if (exception is ArgumentException or FileNotFoundException or IOException or SplitPackageException)
        {
            return (
                "input_rejected",
                "The exact inspected-APK uninstall input was rejected before device dispatch.",
                2);
        }
        if (exception is PackageNotInstalledException)
        {
            return (
                "preimage_absent",
                "The inspected package was absent before dispatch; uninstall is not an idempotent success.",
                1);
        }
        if (exception is InvalidDataException)
        {
            return (
                "pre_dispatch_proof_rejected",
                "Exact serial, immutable artifact, or installed-identity proof was rejected before dispatch.",
                1);
        }
        if (exception is AdbCommandException or OperationCanceledException or TimeoutException)
        {
            return (
                "pre_dispatch_read_failed",
                "A fixed serial-scoped uninstall precondition readback did not complete before dispatch.",
                1);
        }
        return (
            "pre_dispatch_failed",
            "Exact inspected-APK uninstall did not reach device dispatch.",
            1);
    }

    private static async Task<int> RunExactApkPropertiesJsonAsync(string[] arguments)
    {
        var mutationRequested = arguments.Length > 2 &&
            (string.Equals(arguments[2], "clear", StringComparison.Ordinal) ||
             string.Equals(arguments[2], "restore", StringComparison.Ordinal));
        try
        {
            var command = OperatorCommands.ParseExactApkPropertyCliArguments(arguments);
            var execution = await new OperatorCommandExecutor(AdbClient.CreateDefault())
                .ExecuteAsync(command);
            if (command.Kind == OperatorCommandKind.ObserveExactApkProperties)
            {
                var observation = execution.ApkPropertyObservationResult ??
                    throw new InvalidOperationException("Exact APK property observation returned no result.");
                WriteJson(new
                {
                    schema = ApkPropertyObservationSchema,
                    succeeded = true,
                    result = observation,
                    failure = (object?)null
                });
                return 0;
            }

            var mutation = execution.ApkPropertyMutationResult ??
                throw new InvalidOperationException("Exact APK property mutation returned no result.");
            WriteJson(new
            {
                schema = ApkPropertyMutationSchema,
                succeeded = mutation.Confirmed,
                mutation = execution.MutationReceipt,
                result = mutation,
                failure = mutation.Confirmed
                    ? (object?)null
                    : new
                    {
                        code = mutation.Disposition == ApkPropertyMutationDisposition.StillDivergent
                            ? "property_readback_divergent"
                            : "cleanup_unknown",
                        message = mutation.Detail,
                        state_change_possible = true
                    }
            });
            return mutation.Confirmed ? 0 : 1;
        }
        catch (Exception exception)
        {
            return WriteExactApkPropertyFailureJson(exception, mutationRequested);
        }
    }

    internal static int WriteExactApkPropertyFailureJson(
        Exception exception,
        bool mutationRequested)
    {
        var dispatched = exception as OperatorMutationExecutionException;
        var failure = dispatched is null
            ? ClassifyExactApkPropertyFailure(exception, mutationRequested)
            : (
                Code: "cleanup_unknown",
                Message: "Property mutation was dispatched, but exact terminal readback is unavailable.",
                StateChangePossible: true,
                ExitCode: 1);
        WriteJson(new
        {
            schema = mutationRequested ? ApkPropertyMutationSchema : ApkPropertyObservationSchema,
            succeeded = false,
            mutation = dispatched?.MutationReceipt,
            result = (object?)null,
            failure = new
            {
                code = failure.Code,
                message = failure.Message,
                state_change_possible = failure.StateChangePossible
            }
        });
        return failure.ExitCode;
    }

    private static (string Code, string Message, bool StateChangePossible, int ExitCode)
        ClassifyExactApkPropertyFailure(Exception exception, bool mutationRequested)
    {
        if (exception is ArgumentException or FileNotFoundException or DirectoryNotFoundException or
            IOException or SplitPackageException)
        {
            return (
                "input_rejected",
                "The exact APK property input was rejected before device mutation.",
                false,
                2);
        }
        if (exception is PackageNotInstalledException)
        {
            return (
                "exact_apk_absent",
                "The exact inspected APK is not installed on the selected serial.",
                false,
                1);
        }
        if (exception is InvalidDataException)
        {
            return (
                "pre_dispatch_proof_rejected",
                "Exact serial, APK, closed manifest, snapshot, or installed-byte proof was rejected.",
                false,
                1);
        }
        if (exception is AdbCommandException or OperationCanceledException or TimeoutException)
        {
            return (
                "pre_dispatch_read_failed",
                "A fixed serial-scoped APK property precondition readback did not complete.",
                false,
                1);
        }
        return (
            "property_operation_failed",
            "The exact APK property operation did not complete.",
            mutationRequested,
            1);
    }

    private static async Task<int> RunApkPermissionObservationJsonAsync(string[] arguments)
    {
        try
        {
            var command = OperatorCommands.ParsePackagePermissionObservationCliArguments(arguments);
            var client = AdbClient.CreateDefault();
            var execution = await new OperatorCommandExecutor(client).ExecuteAsync(command);
            var observation = execution.ApkPermissionObservation ??
                throw new InvalidOperationException("Permission observation returned no result.");
            WriteJson(new
            {
                schema = ApkPermissionObservationSchema,
                succeeded = true,
                result = observation,
                failure = (object?)null
            });
            return 0;
        }
        catch (Exception exception)
        {
            return WriteApkPermissionObservationFailure(exception);
        }
    }

    private static int WriteApkPermissionObservationFailure(Exception exception)
    {
        var failure = exception switch
        {
            ArgumentException => (
                "input_rejected",
                "The exact serial/package permission-observation input was rejected before device observation.",
                2),
            OperationCanceledException => (
                "permission_observation_cancelled",
                "The bounded exact-package permission observation did not complete.",
                1),
            InvalidDataException => (
                "permission_observation_output_rejected",
                "The exact-package permission observation source was malformed or incomplete.",
                1),
            _ => (
                "permission_observation_failed",
                "The bounded exact-package permission observation did not complete.",
                1)
        };
        WriteJson(new
        {
            schema = ApkPermissionObservationSchema,
            succeeded = false,
            result = (object?)null,
            failure = new
            {
                code = failure.Item1,
                message = failure.Item2,
                state_change_possible = false
            }
        });
        return failure.Item3;
    }

    private static async Task<int> RunAdbForwardInventoryJsonAsync(string[] arguments)
    {
        try
        {
            var command = OperatorCommands.ParseAdbForwardInventoryCliArguments(arguments);
            var client = AdbClient.CreateDefault();
            var execution = await new OperatorCommandExecutor(client).ExecuteAsync(command);
            var result = execution.AdbForwardInventoryResult ??
                throw new InvalidOperationException("ADB forward inventory returned no result.");
            WriteJson(new
            {
                schema = AdbForwardInventoryResultSchema,
                succeeded = true,
                result,
                failure = (object?)null
            });
            return 0;
        }
        catch (Exception exception)
        {
            return WriteAdbForwardInventoryFailure(exception);
        }
    }

    private static int WriteAdbForwardInventoryFailure(Exception exception)
    {
        var failure = ClassifyAdbForwardInventoryFailure(exception);
        WriteJson(new
        {
            schema = AdbForwardInventoryResultSchema,
            succeeded = false,
            result = (object?)null,
            failure = new
            {
                code = failure.Code,
                message = failure.Message,
                state_change_possible = false
            }
        });
        return failure.ExitCode;
    }

    private static (string Code, string Message, int ExitCode)
        ClassifyAdbForwardInventoryFailure(Exception exception)
    {
        if (exception is ArgumentException)
        {
            return (
                "input_rejected",
                "The typed ADB forward inventory input was rejected before observation.",
                2);
        }
        if (exception is OperationCanceledException)
        {
            return (
                "forward_inventory_cancelled",
                "The shared ADB forward inventory did not complete.",
                1);
        }
        if (exception is InvalidDataException)
        {
            return (
                "forward_inventory_output_rejected",
                "The shared ADB forward inventory output was malformed or incomplete.",
                1);
        }
        return (
            "forward_inventory_failed",
            "The shared ADB forward inventory did not complete.",
            1);
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
        var product = action is "install" or "provision"
            ? OperatorCommands.ParseRequiredKioskSetupProductChannel(arguments)
            : RustyKioskProductContract.Parse(
                GetOption(arguments, "--product-channel") ?? "stable");
        switch (action)
        {
            case "status":
                {
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.InspectRustyKiosk(serial, product));
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
                    var bundle = RustyKioskBundleLocator.ResolveRequiredForSetup(bundleDirectory);
                    var execution = await executor.ExecuteAsync(
                        OperatorCommands.InstallRustyKiosk(
                            serial,
                            bundle,
                            operatorConfirmed: true,
                            product: product));
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
                        OperatorCommands.ProvisionRustyKiosk(
                            serial,
                            operatorConfirmed: true,
                            product: product));
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
                            operatorConfirmed: confirmation,
                            product: product));
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

                    return RustyKioskCliExitCodes.For(execution.MutationReceipt, result.Accepted);
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
                        await executor.ExecuteAsync(OperatorCommands.PullRustyKioskTags(serial, output, product));
                        Console.WriteLine(Path.GetFullPath(output));
                        return 0;
                    }

                    if (tagsAction == "import")
                    {
                        RequireConfirmation(arguments, "--confirm-kiosk-control", "Rusty Kiosk tag-file replacement");
                        var input = RequireOption(arguments, "--file");
                        var execution = await executor.ExecuteAsync(
                            OperatorCommands.PushRustyKioskTags(
                                serial,
                                input,
                                operatorConfirmed: true,
                                product: product));
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
              questionable-file-manager apk launch-diagnose --serial <serial> --file <file.apk> --output <new-folder> --json
              questionable-file-manager apk observe --serial <serial> --file <file.apk> [--json]
              questionable-file-manager apk properties observe --serial <serial> --file <file.apk> --manifest <manifest.json> --output <new-snapshot.json> --json
              questionable-file-manager apk properties clear --serial <serial> --file <file.apk> --manifest <manifest.json> --snapshot <snapshot.json> --confirm-exact-apk-property-mutation --json
              questionable-file-manager apk properties restore --serial <serial> --file <file.apk> --manifest <manifest.json> --snapshot <snapshot.json> --confirm-exact-apk-property-mutation --json
              questionable-file-manager apk permissions --serial <serial> --package <package> --json
              questionable-file-manager apk preflight --serial <serial> --file <file.apk> --json
              questionable-file-manager apk deploy --serial <serial> --file <file.apk> --json
              questionable-file-manager apk diagnose --serial <serial> --file <file.apk> --output <new-folder> --json
              questionable-file-manager apk stop --serial <serial> --package <package> --confirm-package-stop --json
              questionable-file-manager apk uninstall --serial <serial> --file <file.apk> --confirm-exact-apk-uninstall --json
              questionable-file-manager apk install-bundle --serial <serial> --folder <apk-folder> [options]
              questionable-file-manager apk install-many --serial <host:port> --serial <host:port> --file <file.apk> [options]
              questionable-file-manager apk install-bundle-many --serial <host:port> --serial <host:port> --folder <apk-folder> [options]
              questionable-file-manager wifi enable --serial <usb-serial> [--port 5555] --confirm-wifi-adb
              questionable-file-manager wifi connect --host <quest-ip> [--port 5555] --confirm-wifi-adb
              questionable-file-manager wifi disconnect --host <quest-ip> [--port 5555] --confirm-wifi-adb
              questionable-file-manager kiosk status --serial <serial> [--product-channel <stable|labs>] [--json]
              questionable-file-manager kiosk install --serial <usb-serial> --product-channel <stable|labs> [--bundle <folder>] --confirm-kiosk-setup
              questionable-file-manager kiosk provision --serial <usb-serial> --product-channel <stable|labs> --confirm-kiosk-setup
              questionable-file-manager kiosk command --serial <serial> [--product-channel <stable|labs>] --command <typed-command> [--value <text>] [--confirm-kiosk-control] [--json]
              questionable-file-manager kiosk tags export --serial <serial> [--product-channel <stable|labs>] --output <app-tags.json>
              questionable-file-manager kiosk tags import --serial <serial> [--product-channel <stable|labs>] --file <app-tags.json> --confirm-kiosk-control
              questionable-file-manager kiosk-direct status <direct-auth> [--json]
              questionable-file-manager kiosk-direct command <direct-auth> --command <typed-command> [--value <text>] [--confirm-kiosk-control] [--json]
              questionable-file-manager kiosk-direct request-status <direct-auth> --request-id <id> [--json]
              questionable-file-manager kiosk-direct request-cancel <direct-auth> --request-id <id> --confirm-kiosk-control [--json]
              questionable-file-manager kiosk-direct tags export <direct-auth> --output <app-tags.json>
              questionable-file-manager kiosk-direct tags import <direct-auth> --file <app-tags.json> --confirm-kiosk-control
              questionable-file-manager kiosk-direct files list <direct-auth> [--json]
              questionable-file-manager kiosk-direct files upload <direct-auth> --file <path> [--name <staged-name>] --confirm-staging-upload
              questionable-file-manager kiosk-direct files download <direct-auth> --name <staged-name> --output <path> [--overwrite]
              questionable-file-manager kiosk-direct files delete <direct-auth> --name <staged-name> --confirm-file-delete
              questionable-file-manager kiosk-direct install <direct-auth> --file <base.apk> [--file <split.apk> ...] --confirm-local-install [--json]
              questionable-file-manager kiosk-direct install-status <direct-auth> --request-id <id> [--json]
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

            Rusty Kiosk typed commands:
              status, show-controls, show-apps, reload, focus-search,
              focus-tag-editor, set-search, select, filter-tag, add-tag,
              remove-tag, set-launch-requirement, cancel-pending-launch,
              launch-normal, launch-kiosk, launch-option, check-setup-helper, request-wifi-adb,
              enable-wifi-adb-after-boot, disable-wifi-adb-after-boot,
              disable-wifi-adb, enable-accessibility, disable-accessibility,
              passthrough-natural, passthrough-contour, exit-meta-home.
              set-launch-requirement accepts exactly any, wifi-on, or wifi-off.
              launch-option accepts only one discovered opaque option id (maximum 160 characters).
              ADB Kiosk routes default to stable; --product-channel labs binds
              status, command, and tag traffic to the separate Labs identity.
              Accepted focus commands exit 3 until wearer-visible focus is confirmed.

            Direct authentication (choose exactly one):
              --endpoint <http://quest-ip:39873> --credential-stdin
              --serial <usb-serial> --product-channel <stable|labs> --confirm-kiosk-direct-bootstrap [--adb <path>]

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
            local link. Manual credentials are read only from bounded standard input.
            Authorized-USB bootstrap is exact-serial/channel scoped, uses the existing ADB
            daemon, and owns one memory-only session for the lifetime of that CLI command.
            The status action confirms Direct Link status, typed Kiosk status, and staging
            inventory through the same composite Core adoption readback used by WPF.
            With --json, cleanup precedes one sanitized kiosk_direct_cli_result.v1 document
            on success or failure; Direct Link JSON failures do not write plaintext stderr.
            Direct files are confined to app-owned staging, and
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
            Exact inspected-APK uninstall is a destructive agent-only cleanup
            primitive: it removes the app and may delete its app-private data.
            It is valid only when a separate pre-run snapshot proves absence and
            the current run owns the exact install. Exact-byte equality alone is
            not cleanup authority, and the route proves only fixed package absence.
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
            installer signer. Its strict v4 payload binds the exact channel/maturity GitHub
            Release URL for RustyFleet[-Labs]-Setup.exe; Pages never carries the binary. Payload
            bytes must use RFC 8785 JCS, bind issue/expiry with a required duration, and
            expire within 24 hours. Embedded release trust
            comes only from reviewed checked-in source on the clean tagged release commit;
            MSBuild, environment, script arguments, and generated files cannot add trust.
            Replay state, its sibling file anchor, and its elevated signed-Setup-provisioned
            protected machine record fail closed after deletion or record loss; Core is
            read-only and has no provisioning/reset/transition API. A declined, failed, or prompt-expired
            visible guided run remains unconsumed, with freshness checked again after
            success. File Manager invokes only Fleet's
            fixed plan and guided setup entrypoints. It accepts no URL, program, argument,
            credential, device, ADB, hotspot, or elevation option and reports only sanitized
            handoff metadata.
            """);
        Console.WriteLine();
        Console.WriteLine("            Agent-only typed routes:");
        foreach (var route in AgentRouteAdmissions.Where(static route => route.Executable))
        {
            Console.WriteLine($"              questionable-file-manager {route.HelpUsage}");
        }
    }
}

internal sealed record CliAgentRouteAdmission(
    string Id,
    string HelpUsage,
    IReadOnlyList<string> ProbeArguments,
    bool Executable);
