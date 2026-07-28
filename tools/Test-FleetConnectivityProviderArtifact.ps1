[CmdletBinding()]
param(
    [string]$OutputDirectory = (
        Join-Path $PSScriptRoot '..\artifacts\fleet-connectivity-provider-validation'),
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactName = 'questionable-file-manager-connectivity-provider.exe'
$receiptName = 'questionable-file-manager-connectivity-provider.receipt.json'
$expectedPrefix = $artifactsRoot + [IO.Path]::DirectorySeparatorChar

if (-not $OutputDirectory.StartsWith(
        $expectedPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Provider validation output must stay under $artifactsRoot."
}

$suffix = [Guid]::NewGuid().ToString('N')
$publishStaging = Join-Path ([IO.Path]::GetTempPath()) (
    "questionable-file-manager-connectivity-provider-publish-$suffix")
$isolationDirectory = Join-Path ([IO.Path]::GetTempPath()) (
    "questionable-file-manager-connectivity-provider-isolation-$suffix")
$apphostBuildStaging = Join-Path ([IO.Path]::GetTempPath()) (
    "questionable-file-manager-connectivity-provider-apphost-build-$suffix")
$apphostIsolationDirectory = Join-Path ([IO.Path]::GetTempPath()) (
    "questionable-file-manager-connectivity-provider-apphost-isolation-$suffix")
$bundleExtractionDirectory = Join-Path $isolationDirectory 'bundle-extract'

function Invoke-IsolatedProvider {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$BundleExtractionRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Arguments,
        [string]$InputJson = '',
        [switch]$KeepStandardInputOpen,
        [int]$TimeoutMilliseconds = 5000
    )

    New-Item -ItemType Directory -Path $BundleExtractionRoot -Force |
        Out-Null
    $launchExtractionDirectory = Join-Path $BundleExtractionRoot (
        'launch-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $launchExtractionDirectory |
        Out-Null

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] =
        $launchExtractionDirectory
    $startInfo.Environment['QUESTIONABLE_FILE_MANAGER_ADB'] =
        (Join-Path $WorkingDirectory 'must-not-run-adb.exe')
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Could not start isolated Fleet connectivity provider.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($InputJson.Length -ne 0) {
            $process.StandardInput.Write($InputJson)
        }
        if (-not $KeepStandardInputOpen) {
            $process.StandardInput.Close()
        }
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Isolated Fleet connectivity provider exceeded its bounded gate.'
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdoutTask.GetAwaiter().GetResult()
            Stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

try {
    $providerProject = Join-Path $repoRoot (
        'src\QuestIonAbleFileManager.FleetConnectivityProvider\' +
        'QuestIonAbleFileManager.FleetConnectivityProvider.csproj')
    New-Item -ItemType Directory -Path $apphostBuildStaging | Out-Null
    & dotnet build $providerProject `
        --configuration Release `
        --self-contained false `
        -p:UseAppHost=true `
        -p:Version=$Version `
        --output $apphostBuildStaging
    if ($LASTEXITCODE -ne 0) {
        throw 'Framework-dependent connectivity-provider apphost control build failed.'
    }

    New-Item -ItemType Directory -Path $publishStaging | Out-Null
    & dotnet publish `
        $providerProject `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:PublishTrimmed=false `
        -p:Version=$Version `
        --output $publishStaging
    if ($LASTEXITCODE -ne 0) {
        throw 'Fleet connectivity provider publish failed.'
    }

    $publishedProviderPath = Join-Path $publishStaging $artifactName
    if (-not (Test-Path -LiteralPath $publishedProviderPath -PathType Leaf)) {
        throw 'Dedicated connectivity provider did not emit its exact executable name.'
    }
    $unexpectedCodeFiles = @(
        Get-ChildItem -LiteralPath $publishStaging -File |
            Where-Object {
                $_.Name -match '\.(dll|pdb)$' -or
                $_.Name -match '\.(deps|runtimeconfig)\.json$'
            }
    )
    if ($unexpectedCodeFiles.Count -ne 0) {
        throw "Single-file publish left sibling code/runtime files: $($unexpectedCodeFiles.Name -join ', ')"
    }

    if (Test-Path -LiteralPath $OutputDirectory) {
        $resolvedOutput = [IO.Path]::GetFullPath(
            (Resolve-Path -LiteralPath $OutputDirectory).Path)
        if (-not $resolvedOutput.StartsWith(
                $expectedPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to replace provider output outside $artifactsRoot."
        }
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    $artifactPath = Join-Path $OutputDirectory $artifactName
    Copy-Item -LiteralPath $publishedProviderPath -Destination $artifactPath

    New-Item -ItemType Directory -Path $isolationDirectory | Out-Null
    $isolatedArtifactPath = Join-Path $isolationDirectory $artifactName
    Copy-Item -LiteralPath $artifactPath -Destination $isolatedArtifactPath
    New-Item -ItemType Directory -Path $bundleExtractionDirectory | Out-Null

    $description = Invoke-IsolatedProvider `
        -Executable $isolatedArtifactPath `
        -WorkingDirectory $isolationDirectory `
        -BundleExtractionRoot $bundleExtractionDirectory `
        -Arguments @('--describe-json') `
        -KeepStandardInputOpen
    if ($description.ExitCode -ne 0 -or
        $description.Stderr.Length -ne 0) {
        throw 'The isolated connectivity descriptor failed or blocked on standard input.'
    }
    $descriptionDocument = $description.Stdout | ConvertFrom-Json
    $descriptionActions = @(
        $descriptionDocument.capabilities.actions.id |
            Sort-Object
    )
    $expectedDescriptionActions = @(
        'disable_request_after_boot',
        'disable_wireless_adb',
        'enable_classic_tcpip_from_usb',
        'enable_request_after_boot',
        'request_wireless_adb',
        'status'
    )
    if ($descriptionDocument.schema -cne
            'rusty.quest.workflow.provider_capability_discovery.v1' -or
        $descriptionDocument.provider.id -cne
            'questionable-file-manager.quest-connectivity-provider' -or
        $descriptionDocument.provider.version -cne $Version -or
        $descriptionDocument.authorizes_execution -ne $false -or
        $descriptionDocument.target_specific -ne $false -or
        ($descriptionActions -join "`n") -cne
            ($expectedDescriptionActions -join "`n") -or
        $description.Stdout.Contains(
            'must-not-run-adb.exe',
            [StringComparison]::Ordinal)) {
        throw 'The isolated connectivity provider returned an invalid inert descriptor.'
    }

    $rejectedShapes = @(
        [string[]]@(),
        [string[]]@('--help'),
        [string[]]@('devices', '--json'),
        [string[]]@('wifi', 'enable'),
        [string[]]@('kiosk-direct', 'status'),
        [string[]]@('integration', 'quest-connectivity'),
        [string[]]@('integration', 'quest-connectivity', '--json', 'extra'),
        [string[]]@('Integration', 'quest-connectivity', '--json'),
        [string[]]@('integration', 'QUEST-CONNECTIVITY', '--json'),
        [string[]]@('integration', 'quest-connectivity', '--JSON'),
        [string[]]@('--Describe-json'),
        [string[]]@('--describe-json', 'extra'),
        [string[]]@(
            'integration',
            'quest-connectivity',
            '--json',
            '--describe-json')
    )
    foreach ($arguments in $rejectedShapes) {
        $rejected = Invoke-IsolatedProvider `
            -Executable $isolatedArtifactPath `
            -WorkingDirectory $isolationDirectory `
            -BundleExtractionRoot $bundleExtractionDirectory `
            -Arguments $arguments
        if ($rejected.ExitCode -ne 2 -or $rejected.Stderr.Length -ne 0) {
            throw "Dedicated provider admitted or misreported arguments: $($arguments -join ' ')"
        }
        $response = $rejected.Stdout | ConvertFrom-Json
        if ($response.schema -cne
                'questionable.file_manager.quest_wifi_adb_provider_response.v1' -or
            $response.status -cne 'rejected' -or
            $response.error -cne 'providerArgumentsInvalid') {
            throw "Dedicated provider returned the wrong rejection: $($arguments -join ' ')"
        }
    }

    $issuedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $deviceId = 'artifact-gate-' + [Guid]::NewGuid().ToString('N')
    $validRequest = [ordered]@{
        schema = 'rusty.fleet.quest_wifi_adb_owner_invocation.v1'
        request_id = 'artifact-gate-request'
        operation_id = 'artifact-gate-operation'
        preview_id = 'artifact-gate-preview'
        device_id = $deviceId
        identity_revision = 1
        action = 'status'
        issued_at_ms = $issuedAt
        expires_at_ms = $issuedAt + 25000
    } | ConvertTo-Json -Compress
    $missingProfile = Invoke-IsolatedProvider `
        -Executable $isolatedArtifactPath `
        -WorkingDirectory $isolationDirectory `
        -BundleExtractionRoot $bundleExtractionDirectory `
        -Arguments @('integration', 'quest-connectivity', '--json') `
        -InputJson $validRequest
    if ($missingProfile.ExitCode -ne 1 -or
        $missingProfile.Stderr.Length -ne 0) {
        throw 'Isolated connectivity provider did not fail closed without its private profile.'
    }
    $missingProfileResponse = $missingProfile.Stdout | ConvertFrom-Json
    if ($missingProfileResponse.status -cne 'failed' -or
        $missingProfileResponse.error -cne 'providerProfileUnavailable' -or
        $missingProfile.Stdout.Contains($deviceId, [StringComparison]::Ordinal)) {
        throw 'Missing-profile response was not sanitized or stable.'
    }

    $entriesAfterRun = @(Get-ChildItem -LiteralPath $isolationDirectory -Force)
    $filesAfterRun = @($entriesAfterRun | Where-Object { -not $_.PSIsContainer })
    $directoriesAfterRun = @($entriesAfterRun | Where-Object PSIsContainer)
    if ($entriesAfterRun.Count -ne 2 -or
        $filesAfterRun.Count -ne 1 -or
        $filesAfterRun[0].Name -cne $artifactName -or
        $directoriesAfterRun.Count -ne 1 -or
        $directoriesAfterRun[0].Name -cne 'bundle-extract') {
        throw 'Provider isolation stage grew unexpected top-level entries.'
    }
    $extractedEntries = @(
        Get-ChildItem -LiteralPath $bundleExtractionDirectory -Force -Recurse)
    $launchExtractionDirectories = @(
        Get-ChildItem -LiteralPath $bundleExtractionDirectory -Directory)
    if ($launchExtractionDirectories.Count -ne
            ($rejectedShapes.Count + 2) -or
        @($launchExtractionDirectories | Where-Object {
            $_.Name -cnotmatch '^launch-[0-9a-f]{32}$'
        }).Count -ne 0) {
        throw 'Every isolated provider launch must receive one fresh extraction directory.'
    }
    [long]$extractedBytes = 0
    foreach ($entry in $extractedEntries) {
        if (-not $entry.PSIsContainer) {
            $extractedBytes += $entry.Length
        }
    }
    if (@($extractedEntries | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        }).Count -ne 0 -or
        @($extractedEntries | Where-Object { -not $_.PSIsContainer }).Count -gt 128 -or
        @($extractedEntries | Where-Object PSIsContainer).Count -gt 16 -or
        $extractedBytes -gt 134217728) {
        throw 'Private native extraction exceeded its bounded non-reparse shape.'
    }

    $ordinaryApphostPath = Join-Path $apphostBuildStaging $artifactName
    New-Item -ItemType Directory -Path $apphostIsolationDirectory | Out-Null
    $isolatedApphostPath = Join-Path $apphostIsolationDirectory $artifactName
    Copy-Item -LiteralPath $ordinaryApphostPath -Destination $isolatedApphostPath
    $apphostResult = Invoke-IsolatedProvider `
        -Executable $isolatedApphostPath `
        -WorkingDirectory $apphostIsolationDirectory `
        -BundleExtractionRoot (
            Join-Path $apphostIsolationDirectory 'bundle-extract') `
        -Arguments @('integration', 'quest-connectivity', '--json') `
        -InputJson $validRequest
    if ($apphostResult.ExitCode -eq 1 -and
        $apphostResult.Stderr.Length -eq 0 -and
        $apphostResult.Stdout -ceq $missingProfile.Stdout) {
        throw 'Framework-dependent apphost unexpectedly ran without sibling assemblies.'
    }

    $artifact = Get-Item -LiteralPath $artifactPath
    $receipt = [ordered]@{
        schema = 'questionable.file_manager.fleet_connectivity_provider_artifact_receipt.v1'
        artifact_name = $artifactName
        sha256 = (
            Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        size_bytes = $artifact.Length
        runtime = 'win-x64'
        source_project = 'QuestIonAbleFileManager.FleetConnectivityProvider'
        self_contained = $true
        single_file = $true
        sibling_code_files = 0
        isolated_file_count = 1
        bundle_extract_base = 'caller-private-per-launch'
        bundle_extract_file_count = @(
            $extractedEntries | Where-Object { -not $_.PSIsContainer }
        ).Count
        bundle_extract_directory_count = @(
            $extractedEntries | Where-Object PSIsContainer
        ).Count
        bundle_extract_launch_directories =
            $launchExtractionDirectories.Count
        bundle_extract_bytes = $extractedBytes
        isolated_top_level_entries_after_run = 2
        ordinary_apphost_isolation_rejected = $true
        general_cli_dispatch_unreachable = $true
        rejected_argument_shapes = $rejectedShapes.Count
        description_route = '--describe-json'
        description_schema =
            'rusty.quest.workflow.provider_capability_discovery.v1'
        description_provider_version = $descriptionDocument.provider.version
        description_action_count = $descriptionActions.Count
        description_stdin_unread = $true
        description_authorizes_execution = $false
        description_target_specific = $false
        private_profile_required = $true
        request_schema = 'rusty.fleet.quest_wifi_adb_owner_invocation.v1'
        receipt_schema = 'questionable.file_manager.quest_wifi_adb_receipt.v1'
        smoke_route = 'integration quest-connectivity --json'
        smoke_status = 'failed'
        smoke_error = 'providerProfileUnavailable'
        smoke_exit_code = 1
        exit_codes = [ordered]@{
            verified = 0
            failed = 1
            rejected = 2
            pending = 3
            cancelled = 4
        }
        stderr_bytes = 0
    }
    $receiptJson = $receipt | ConvertTo-Json -Compress
    Set-Content `
        -LiteralPath (Join-Path $OutputDirectory $receiptName) `
        -Value $receiptJson `
        -Encoding utf8
    Write-Output $receiptJson
}
finally {
    foreach ($temporaryDirectory in @(
        $publishStaging,
        $isolationDirectory,
        $apphostBuildStaging,
        $apphostIsolationDirectory
    )) {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            $resolved = [IO.Path]::GetFullPath(
                (Resolve-Path -LiteralPath $temporaryDirectory).Path)
            $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
            if (-not $resolved.StartsWith(
                    $temporaryRoot,
                    [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($resolved) -notmatch
                    '^questionable-file-manager-connectivity-provider-(publish|isolation|apphost-build|apphost-isolation)-[0-9a-f]{32}$') {
                throw "Refusing to remove unexpected temporary directory $resolved."
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
