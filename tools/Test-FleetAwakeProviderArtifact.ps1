[CmdletBinding()]
param(
    [string]$OutputDirectory = (
        Join-Path $PSScriptRoot '..\artifacts\fleet-awake-provider-validation'),
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactName = 'questionable-file-manager-awake-provider.exe'
$receiptName = 'questionable-file-manager-awake-provider.receipt.json'
$expectedPrefix = $artifactsRoot + [IO.Path]::DirectorySeparatorChar

if (-not $OutputDirectory.StartsWith(
        $expectedPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Provider validation output must stay under $artifactsRoot."
}

$suffix = [Guid]::NewGuid().ToString('N')
$publishStaging = Join-Path ([IO.Path]::GetTempPath()) (
    "questionable-file-manager-awake-provider-publish-$suffix")
$isolationDirectory = Join-Path ([IO.Path]::GetTempPath()) (
    "questionable-file-manager-awake-provider-isolation-$suffix")
$apphostBuildStaging = Join-Path ([IO.Path]::GetTempPath()) (
    "questionable-file-manager-awake-provider-apphost-build-$suffix")
$apphostIsolationDirectory = Join-Path ([IO.Path]::GetTempPath()) (
    "questionable-file-manager-awake-provider-apphost-isolation-$suffix")
$bundleExtractionDirectory = Join-Path $isolationDirectory 'bundle-extract'

function Invoke-IsolatedProvider {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,
        [Parameter(Mandatory)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory)]
        [string]$BundleExtractionDirectory,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Arguments,
        [string]$InputJson = '',
        [int]$TimeoutMilliseconds = 5000
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] =
        $BundleExtractionDirectory
    $startInfo.Environment['QUESTIONABLE_FILE_MANAGER_ADB'] =
        (Join-Path $WorkingDirectory 'must-not-run-adb.exe')
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Could not start isolated Fleet awake provider.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($InputJson.Length -ne 0) {
            $process.StandardInput.Write($InputJson)
        }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Isolated Fleet awake provider exceeded its bounded gate.'
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
        'src\QuestIonAbleFileManager.FleetAwakeProvider\' +
        'QuestIonAbleFileManager.FleetAwakeProvider.csproj')
    New-Item -ItemType Directory -Path $apphostBuildStaging | Out-Null
    & dotnet build $providerProject `
        --configuration Release `
        --self-contained false `
        -p:UseAppHost=true `
        -p:Version=$Version `
        --output $apphostBuildStaging
    if ($LASTEXITCODE -ne 0) {
        throw 'Framework-dependent awake-provider apphost control build failed.'
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
        throw 'Fleet awake provider publish failed.'
    }

    $publishedProviderPath = Join-Path $publishStaging $artifactName
    if (-not (Test-Path -LiteralPath $publishedProviderPath -PathType Leaf)) {
        throw 'Dedicated awake provider did not emit its exact executable name.'
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
    $initialEntries = @(Get-ChildItem -LiteralPath $isolationDirectory -Force)
    if ($initialEntries.Count -ne 2 -or
        @($initialEntries | Where-Object { -not $_.PSIsContainer }).Count -ne 1 -or
        @($initialEntries | Where-Object PSIsContainer).Count -ne 1) {
        throw 'Provider stage must contain only the executable and private bundle-extract directory.'
    }

    $issuedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $formerTwentyFourHourRequest = [ordered]@{
        contractVersion = 'questionable.file_manager.fleet_awake_provider.v1'
        requestId = 'artifact-gate-request'
        operationId = 'artifact-gate-operation'
        previewId = 'artifact-gate-preview'
        deviceId = 'artifact-gate-device'
        identityRevision = 1
        action = 'applyBounded'
        durationMilliseconds = 86400000
        watchdogIntervalMilliseconds = 5000
        watchdogGeneration = 'artifact-gate-generation'
        issuedAtUnixMilliseconds = $issuedAt
        expiresAtUnixMilliseconds = $issuedAt + 25000
        serial = 'must-not-run'
    } | ConvertTo-Json -Compress
    $rejectedDuration = Invoke-IsolatedProvider `
        -Executable $isolatedArtifactPath `
        -WorkingDirectory $isolationDirectory `
        -BundleExtractionDirectory $bundleExtractionDirectory `
        -Arguments @('integration', 'quest-awake', '--json') `
        -InputJson $formerTwentyFourHourRequest
    if ($rejectedDuration.ExitCode -ne 2 -or
        $rejectedDuration.Stderr.Length -ne 0) {
        throw 'The isolated provider did not strictly reject the former 24-hour bound.'
    }
    $durationResponse = $rejectedDuration.Stdout | ConvertFrom-Json
    if ($durationResponse.contractVersion -cne
            'questionable.file_manager.fleet_awake_provider.v1' -or
        $durationResponse.status -cne 'rejected' -or
        $durationResponse.error -cne 'durationInvalid' -or
        $rejectedDuration.Stdout.Contains('must-not-run', [StringComparison]::Ordinal)) {
        throw 'The isolated provider returned an invalid bounded-duration rejection.'
    }

    $rejectedShapes = @(
        [string[]]@(),
        [string[]]@('--help'),
        [string[]]@('devices', '--json'),
        [string[]]@('device', 'status', '--serial', 'must-not-run', '--json'),
        [string[]]@('integration', 'quest-awake'),
        [string[]]@('integration', 'quest-awake', '--json', 'extra'),
        [string[]]@('Integration', 'quest-awake', '--json'),
        [string[]]@('integration', 'QUEST-AWAKE', '--json'),
        [string[]]@('integration', 'quest-awake', '--JSON')
    )
    foreach ($arguments in $rejectedShapes) {
        $rejected = Invoke-IsolatedProvider `
            -Executable $isolatedArtifactPath `
            -WorkingDirectory $isolationDirectory `
            -BundleExtractionDirectory $bundleExtractionDirectory `
            -Arguments $arguments
        if ($rejected.ExitCode -ne 2 -or $rejected.Stderr.Length -ne 0) {
            throw "Dedicated provider admitted or misreported arguments: $($arguments -join ' ')"
        }
        $response = $rejected.Stdout | ConvertFrom-Json
        if ($response.status -cne 'rejected' -or
            $response.error -cne 'providerArgumentsInvalid') {
            throw "Dedicated provider returned the wrong rejection: $($arguments -join ' ')"
        }
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

    $ordinaryApphostPath =
        Join-Path $apphostBuildStaging $artifactName
    if (-not (Test-Path -LiteralPath $ordinaryApphostPath -PathType Leaf)) {
        throw 'Framework-dependent awake-provider apphost was not produced.'
    }
    New-Item -ItemType Directory -Path $apphostIsolationDirectory | Out-Null
    $isolatedApphostPath = Join-Path $apphostIsolationDirectory $artifactName
    Copy-Item -LiteralPath $ordinaryApphostPath -Destination $isolatedApphostPath
    $apphostResult = Invoke-IsolatedProvider `
        -Executable $isolatedApphostPath `
        -WorkingDirectory $apphostIsolationDirectory `
        -BundleExtractionDirectory (
            Join-Path $apphostIsolationDirectory 'bundle-extract') `
        -Arguments @('integration', 'quest-awake', '--json') `
        -InputJson $formerTwentyFourHourRequest
    if ($apphostResult.ExitCode -eq 2 -and
        $apphostResult.Stderr.Length -eq 0 -and
        $apphostResult.Stdout -ceq $rejectedDuration.Stdout) {
        throw 'Framework-dependent apphost unexpectedly ran without sibling assemblies.'
    }
    $apphostEntries = @(
        Get-ChildItem -LiteralPath $apphostIsolationDirectory -Force)
    if ($apphostEntries.Count -ne 1 -or
        $apphostEntries[0].PSIsContainer -or
        $apphostEntries[0].Name -cne $artifactName) {
        throw 'Framework-dependent apphost created an unexpected sibling entry.'
    }

    $artifact = Get-Item -LiteralPath $artifactPath
    $receipt = [ordered]@{
        schema = 'questionable.file_manager.fleet_awake_provider_artifact_receipt.v1'
        artifact_name = $artifactName
        sha256 = (
            Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        size_bytes = $artifact.Length
        runtime = 'win-x64'
        source_project = 'QuestIonAbleFileManager.FleetAwakeProvider'
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
        bundle_extract_bytes = $extractedBytes
        isolated_top_level_entries_after_run = 2
        ordinary_apphost_isolation_rejected = $true
        general_cli_dispatch_unreachable = $true
        rejected_argument_shapes = $rejectedShapes.Count
        strict_request_rejection = 'durationInvalid'
        former_duration_maximum_rejected_ms = 86400000
        supported_duration_maximum_ms = 28800000
        exit_codes = [ordered]@{
            verified = 0
            failed = 1
            rejected = 2
            pending = 3
            cancelled = 4
        }
        smoke_route = 'integration quest-awake --json'
        smoke_exit_code = 2
        smoke_status = 'rejected'
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
                    '^questionable-file-manager-awake-provider-(publish|isolation|apphost-build|apphost-isolation)-[0-9a-f]{32}$') {
                throw "Refusing to remove unexpected temporary directory $resolved."
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
