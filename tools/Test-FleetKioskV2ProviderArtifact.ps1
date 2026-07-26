[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\fleet-kiosk-v2-provider-validation'),
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactName = 'questionable-file-manager-kiosk-v2-provider.exe'
$receiptName = 'questionable-file-manager-kiosk-v2-provider.receipt.json'
$expectedPrefix = $artifactsRoot + [IO.Path]::DirectorySeparatorChar

if (-not $OutputDirectory.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Provider validation output must stay under $artifactsRoot."
}

$publishStaging = Join-Path ([IO.Path]::GetTempPath()) (
    'questionable-file-manager-provider-publish-' + [Guid]::NewGuid().ToString('N'))
$isolationDirectory = Join-Path ([IO.Path]::GetTempPath()) (
    'questionable-file-manager-provider-isolation-' + [Guid]::NewGuid().ToString('N'))
$apphostBuildStaging = Join-Path ([IO.Path]::GetTempPath()) (
    'questionable-file-manager-provider-apphost-build-' + [Guid]::NewGuid().ToString('N'))
$apphostIsolationDirectory = Join-Path ([IO.Path]::GetTempPath()) (
    'questionable-file-manager-provider-apphost-isolation-' + [Guid]::NewGuid().ToString('N'))
$bundleExtractionDirectory = Join-Path $isolationDirectory 'bundle-extract'

try {
    $providerProject = Join-Path $repoRoot (
        'src\QuestIonAbleFileManager.FleetKioskV2Provider\' +
        'QuestIonAbleFileManager.FleetKioskV2Provider.csproj')
    New-Item -ItemType Directory -Path $apphostBuildStaging | Out-Null
    & dotnet build $providerProject `
        --configuration Release `
        --self-contained false `
        -p:UseAppHost=true `
        -p:Version=$Version `
        --output $apphostBuildStaging
    if ($LASTEXITCODE -ne 0) {
        throw 'Framework-dependent apphost control build failed.'
    }
    $ordinaryApphostPath =
        Join-Path $apphostBuildStaging 'questionable-file-manager-kiosk-v2-provider.exe'
    if (-not (Test-Path -LiteralPath $ordinaryApphostPath -PathType Leaf)) {
        throw 'Framework-dependent apphost control executable was not produced.'
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
        throw 'Fleet Kiosk v2 provider publish failed.'
    }

    $publishedProviderPath = Join-Path $publishStaging $artifactName
    if (-not (Test-Path -LiteralPath $publishedProviderPath -PathType Leaf)) {
        throw 'Dedicated provider project did not emit its exact executable name.'
    }
    $unexpectedCodeFiles = @(
        Get-ChildItem -LiteralPath $publishStaging -File |
            Where-Object {
                $_.Name -match '\.(dll|pdb)$' -or
                $_.Name -match '\.(deps|runtimeconfig)\.json$'
            }
    )
    if ($unexpectedCodeFiles.Count -ne 0) {
        throw "Single-file provider publish left sibling code/runtime files: $($unexpectedCodeFiles.Name -join ', ')"
    }

    if (Test-Path -LiteralPath $OutputDirectory) {
        $resolvedOutput = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $OutputDirectory).Path)
        if (-not $resolvedOutput.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
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

    $isolatedEntries = @(Get-ChildItem -LiteralPath $isolationDirectory -Force)
    $isolatedFiles = @($isolatedEntries | Where-Object { -not $_.PSIsContainer })
    $isolatedDirectories = @($isolatedEntries | Where-Object PSIsContainer)
    if ($isolatedEntries.Count -ne 2 -or
        $isolatedFiles.Count -ne 1 -or
        $isolatedFiles[0].Name -cne $artifactName -or
        $isolatedDirectories.Count -ne 1 -or
        $isolatedDirectories[0].Name -cne 'bundle-extract') {
        throw 'Provider stage must contain only the dedicated executable and empty private bundle-extract directory before launch.'
    }

    $profileId = 'gate-' + [Guid]::NewGuid().ToString('N')
    $requestId = 'catalog-gate-' + [Guid]::NewGuid().ToString('N')
    $issuedAtMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $request = [ordered]@{
        schema = 'questionable.file_manager.fleet_kiosk_v2_catalog_request.v1'
        profile_id = $profileId
        request_id = $requestId
        device_id = 'fleet-provider-artifact-gate'
        identity_revision = 1
        capability_id = 'rusty-kiosk.direct-operator'
        capability_evidence_revision = 1
        route_id = 'kiosk.encrypted.v2'
        expected_owner_epoch = $null
        scopes = @('kiosk.catalog-summary')
        issued_at_ms = $issuedAtMs
        expires_at_ms = $issuedAtMs + 25000
    }
    $requestJson = $request | ConvertTo-Json -Compress

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $isolatedArtifactPath
    $startInfo.WorkingDirectory = $isolationDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $bundleExtractionDirectory
    $startInfo.ArgumentList.Add('integration')
    $startInfo.ArgumentList.Add('kiosk-v2-catalog')
    $startInfo.ArgumentList.Add('--json')

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Could not start isolated Fleet Kiosk v2 provider.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($requestJson)
        $process.StandardInput.Close()

        if (-not $process.WaitForExit(15000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Isolated Fleet Kiosk v2 provider exceeded the 15-second gate.'
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $expectedResponse = (
            '{"schema":"questionable.file_manager.fleet_kiosk_v2_catalog_response.v1",' +
            '"status":"unavailable","profile_id":"' + $profileId +
            '","request_id":"' + $requestId +
            '","error_code":"provider_profile_unavailable"}')

        if ($process.ExitCode -ne 3) {
            throw "Isolated provider returned exit code $($process.ExitCode), expected 3."
        }
        if ($stderr.Length -ne 0) {
            throw 'Isolated provider wrote to standard error.'
        }
        if ($stdout -cne ($expectedResponse + "`n")) {
            throw 'Isolated provider did not return the exact strict unavailable response.'
        }
    }
    finally {
        $process.Dispose()
    }

    $entriesAfterRun = @(Get-ChildItem -LiteralPath $isolationDirectory -Force)
    $filesAfterRun = @($entriesAfterRun | Where-Object { -not $_.PSIsContainer })
    $directoriesAfterRun = @($entriesAfterRun | Where-Object PSIsContainer)
    if ($entriesAfterRun.Count -ne 2 -or
        $filesAfterRun.Count -ne 1 -or
        $filesAfterRun[0].Name -cne $artifactName -or
        $directoriesAfterRun.Count -ne 1 -or
        $directoriesAfterRun[0].Name -cne 'bundle-extract') {
        throw 'Provider stage must contain only the dedicated executable and its private bundle-extract directory after launch.'
    }
    $extractedEntries = @(
        Get-ChildItem -LiteralPath $bundleExtractionDirectory -Force -Recurse
    )
    $extractedFiles = @($extractedEntries | Where-Object { -not $_.PSIsContainer })
    $extractedDirectories = @($extractedEntries | Where-Object PSIsContainer)
    [long]$extractedBytes = 0
    foreach ($extractedFile in $extractedFiles) {
        $extractedBytes += $extractedFile.Length
    }
    if ($extractedFiles.Count -gt 128 -or
        $extractedDirectories.Count -gt 16 -or
        $extractedBytes -gt 134217728 -or
        @($extractedEntries | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        }).Count -ne 0) {
        throw 'Private native bundle extraction exceeded its bounded non-reparse shape.'
    }

    $rejectedArgumentShapes = [System.Collections.Generic.List[string[]]]::new()
    $rejectedArgumentShapes.Add([string[]]@())
    $rejectedArgumentShapes.Add([string[]]@('--help'))
    $rejectedArgumentShapes.Add([string[]]@('devices', '--json'))
    $rejectedArgumentShapes.Add(
        [string[]]@('files', 'list', '--serial', 'must-not-run', '--path', '/sdcard', '--json'))
    $rejectedArgumentShapes.Add(
        [string[]]@('apk', 'list', '--serial', 'must-not-run', '--json'))
    $rejectedArgumentShapes.Add(
        [string[]]@('wifi', 'connect', '--host', '127.0.0.1', '--port', '5555', '--confirm-wifi-adb'))
    $rejectedArgumentShapes.Add(
        [string[]]@('kiosk', 'status', '--serial', 'must-not-run', '--json'))
    $rejectedArgumentShapes.Add(
        [string[]]@(
            'kiosk-direct',
            'status',
            '--endpoint',
            'http://127.0.0.1:39873',
            '--pairing-code',
            'must-not-run',
            '--json'))
    $rejectedArgumentShapes.Add(
        [string[]]@('device', 'status', '--serial', 'must-not-run', '--json'))
    $rejectedArgumentShapes.Add([string[]]@('integration', 'capabilities', '--json'))
    $rejectedArgumentShapes.Add([string[]]@('integration', 'kiosk-v2-catalog'))
    $rejectedArgumentShapes.Add(
        [string[]]@('integration', 'kiosk-v2-catalog', '--json', 'extra'))
    $rejectedArgumentShapes.Add([string[]]@('Integration', 'kiosk-v2-catalog', '--json'))
    $rejectedArgumentShapes.Add([string[]]@('integration', 'KIOSK-V2-CATALOG', '--json'))
    $rejectedArgumentShapes.Add([string[]]@('integration', 'kiosk-v2-catalog', '--JSON'))
    $expectedRejectedResponse = (
        '{"schema":"questionable.file_manager.fleet_kiosk_v2_catalog_response.v1",' +
        '"status":"rejected","profile_id":"unavailable","request_id":"unavailable",' +
        '"error_code":"provider_arguments_invalid"}' + "`n")
    foreach ($rejectedArguments in $rejectedArgumentShapes) {
        $rejectedStartInfo = [Diagnostics.ProcessStartInfo]::new()
        $rejectedStartInfo.FileName = $isolatedArtifactPath
        $rejectedStartInfo.WorkingDirectory = $isolationDirectory
        $rejectedStartInfo.UseShellExecute = $false
        $rejectedStartInfo.CreateNoWindow = $true
        $rejectedStartInfo.RedirectStandardInput = $true
        $rejectedStartInfo.RedirectStandardOutput = $true
        $rejectedStartInfo.RedirectStandardError = $true
        $rejectedStartInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] =
            $bundleExtractionDirectory
        $rejectedStartInfo.Environment['QUESTIONABLE_FILE_MANAGER_ADB'] =
            (Join-Path $isolationDirectory 'must-not-run-adb.exe')
        foreach ($argument in $rejectedArguments) {
            $rejectedStartInfo.ArgumentList.Add($argument)
        }

        $rejectedProcess = [Diagnostics.Process]::new()
        $rejectedProcess.StartInfo = $rejectedStartInfo
        try {
            if (-not $rejectedProcess.Start()) {
                throw 'Could not start dedicated provider negative-route control.'
            }
            $rejectedStdoutTask = $rejectedProcess.StandardOutput.ReadToEndAsync()
            $rejectedStderrTask = $rejectedProcess.StandardError.ReadToEndAsync()
            $rejectedProcess.StandardInput.Close()
            if (-not $rejectedProcess.WaitForExit(5000)) {
                $rejectedProcess.Kill($true)
                $rejectedProcess.WaitForExit()
                throw 'Dedicated provider negative-route control exceeded five seconds.'
            }
            $rejectedStdout = $rejectedStdoutTask.GetAwaiter().GetResult()
            $rejectedStderr = $rejectedStderrTask.GetAwaiter().GetResult()
            if ($rejectedProcess.ExitCode -ne 2 -or
                $rejectedStderr.Length -ne 0 -or
                $rejectedStdout -cne $expectedRejectedResponse) {
                throw "Dedicated provider admitted or misreported rejected arguments: $($rejectedArguments -join ' ')"
            }
        }
        finally {
            $rejectedProcess.Dispose()
        }
    }

    New-Item -ItemType Directory -Path $apphostIsolationDirectory | Out-Null
    $isolatedApphostPath = Join-Path $apphostIsolationDirectory $artifactName
    Copy-Item -LiteralPath $ordinaryApphostPath -Destination $isolatedApphostPath
    $isolatedApphostFiles = @(Get-ChildItem -LiteralPath $apphostIsolationDirectory -File)
    if ($isolatedApphostFiles.Count -ne 1 -or
        $isolatedApphostFiles[0].Name -cne $artifactName) {
        throw 'Framework-dependent apphost control directory must contain only the renamed apphost.'
    }

    $apphostStartInfo = [Diagnostics.ProcessStartInfo]::new()
    $apphostStartInfo.FileName = $isolatedApphostPath
    $apphostStartInfo.WorkingDirectory = $apphostIsolationDirectory
    $apphostStartInfo.UseShellExecute = $false
    $apphostStartInfo.CreateNoWindow = $true
    $apphostStartInfo.RedirectStandardInput = $true
    $apphostStartInfo.RedirectStandardOutput = $true
    $apphostStartInfo.RedirectStandardError = $true
    $apphostStartInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] =
        (Join-Path $apphostIsolationDirectory 'bundle-extract')
    $apphostStartInfo.ArgumentList.Add('integration')
    $apphostStartInfo.ArgumentList.Add('kiosk-v2-catalog')
    $apphostStartInfo.ArgumentList.Add('--json')

    $apphostProcess = [Diagnostics.Process]::new()
    $apphostProcess.StartInfo = $apphostStartInfo
    try {
        if (-not $apphostProcess.Start()) {
            throw 'Could not start framework-dependent apphost control.'
        }
        $apphostStdoutTask = $apphostProcess.StandardOutput.ReadToEndAsync()
        $apphostStderrTask = $apphostProcess.StandardError.ReadToEndAsync()
        $apphostProcess.StandardInput.Write($requestJson)
        $apphostProcess.StandardInput.Close()
        if (-not $apphostProcess.WaitForExit(5000)) {
            $apphostProcess.Kill($true)
            $apphostProcess.WaitForExit()
            throw 'Framework-dependent apphost control did not fail within five seconds.'
        }

        $apphostStdout = $apphostStdoutTask.GetAwaiter().GetResult()
        $apphostStderr = $apphostStderrTask.GetAwaiter().GetResult()
        if ($apphostProcess.ExitCode -eq 0 -or
            ($apphostProcess.ExitCode -eq 3 -and
                $apphostStderr.Length -eq 0 -and
                $apphostStdout -ceq ($expectedResponse + "`n"))) {
            throw 'Framework-dependent apphost unexpectedly operated without its sibling assemblies.'
        }
    }
    finally {
        $apphostProcess.Dispose()
    }

    $apphostEntriesAfterRun = @(
        Get-ChildItem -LiteralPath $apphostIsolationDirectory -Force)
    if ($apphostEntriesAfterRun.Count -ne 1 -or
        $apphostEntriesAfterRun[0].PSIsContainer -or
        $apphostEntriesAfterRun[0].Name -cne $artifactName) {
        throw 'Framework-dependent apphost control created an unexpected sibling file.'
    }

    $artifact = Get-Item -LiteralPath $artifactPath
    $receipt = [ordered]@{
        schema = 'questionable.file_manager.fleet_kiosk_v2_provider_artifact_receipt.v1'
        artifact_name = $artifactName
        sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
        size_bytes = $artifact.Length
        runtime = 'win-x64'
        source_project = 'QuestIonAbleFileManager.FleetKioskV2Provider'
        self_contained = $true
        single_file = $true
        sibling_code_files = 0
        isolated_file_count = 1
        bundle_extract_base = 'caller-private-per-launch'
        bundle_extract_file_count = $extractedFiles.Count
        bundle_extract_directory_count = $extractedDirectories.Count
        bundle_extract_bytes = $extractedBytes
        isolated_top_level_entries_after_run = 2
        ordinary_apphost_isolation_rejected = $true
        general_cli_dispatch_unreachable = $true
        rejected_argument_shapes = $rejectedArgumentShapes.Count
        exit_codes = [ordered]@{
            verified = 0
            failed = 1
            rejected = 2
            unavailable = 3
        }
        smoke_route = 'integration kiosk-v2-catalog --json'
        smoke_exit_code = 3
        smoke_status = 'unavailable'
        stderr_bytes = 0
    }
    $receiptJson = $receipt | ConvertTo-Json -Compress
    Set-Content -LiteralPath (Join-Path $OutputDirectory $receiptName) -Value $receiptJson -Encoding utf8
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
            $resolvedTemporaryDirectory =
                [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $temporaryDirectory).Path)
            $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
            if (-not $resolvedTemporaryDirectory.StartsWith(
                    $temporaryRoot,
                    [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($resolvedTemporaryDirectory) -notmatch
                    '^questionable-file-manager-provider-(publish|isolation|apphost-build|apphost-isolation|bundle-extract)-[0-9a-f]{32}$') {
                throw "Refusing to remove unexpected temporary directory $resolvedTemporaryDirectory."
            }
            Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force
        }
    }
}
