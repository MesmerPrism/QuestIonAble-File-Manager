[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputPath,

    [string]$QfmSetupExecutablePath,

    [string]$OutputDirectory = (
        Join-Path $PSScriptRoot `
            '..\artifacts\fleet-installer-handoff-lifecycle')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot 'artifacts'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$expectedOutputPrefix =
    $artifactsRoot + [IO.Path]::DirectorySeparatorChar
$input = [IO.Path]::GetFullPath($InputPath)
$project = Join-Path $repoRoot (
    'tools\FleetInstallerLifecycle\' +
    'QuestIonAbleFileManager.FleetInstallerLifecycle.csproj')
$receiptName =
    'fleet-installer-handoff-lifecycle.receipt.json'

if (-not $output.StartsWith(
        $expectedOutputPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Lifecycle output must stay under $artifactsRoot."
}
if (-not (Test-Path -LiteralPath $input -PathType Leaf) -or
    ((Get-Item -LiteralPath $input -Force).Attributes -band
        [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The exact lifecycle input must be a regular non-reparse file.'
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw 'The Fleet installer lifecycle runner project is missing.'
}
$inputDocument =
    Get-Content -LiteralPath $input -Raw |
        ConvertFrom-Json -NoEnumerate
$protectedInputs = @(
    $input,
    [IO.Path]::GetFullPath(
        [string]$inputDocument.release_a_root),
    [IO.Path]::GetFullPath(
        [string]$inputDocument.release_b_root),
    [IO.Path]::GetFullPath(
        [string]$inputDocument.install_root)
)
$qfmSetup = $null
if (-not [string]::IsNullOrWhiteSpace(
        $QfmSetupExecutablePath)) {
    $qfmSetup = [IO.Path]::GetFullPath(
        $QfmSetupExecutablePath)
    if (-not (Test-Path -LiteralPath $qfmSetup -PathType Leaf)) {
        throw 'The exact QFM Setup executable is missing.'
    }
    $protectedInputs += $qfmSetup
}
foreach ($protectedInput in $protectedInputs) {
    $inputPrefix =
        [IO.Path]::TrimEndingDirectorySeparator($protectedInput) +
        [IO.Path]::DirectorySeparatorChar
    $outputPrefix =
        [IO.Path]::TrimEndingDirectorySeparator($output) +
        [IO.Path]::DirectorySeparatorChar
    if ($output.Equals(
            $protectedInput,
            [StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith(
            $inputPrefix,
            [StringComparison]::OrdinalIgnoreCase) -or
        $protectedInput.StartsWith(
            $outputPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Lifecycle output must not overlap input, release, or install state.'
    }
}

function Invoke-BoundedProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Arguments,

        [int]$TimeoutMilliseconds = 180000
    )

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw 'The lifecycle validation process did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'The lifecycle validation process exceeded its deadline.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($stdout.Length -gt 1048576 -or
            $stderr.Length -gt 1048576) {
            throw 'The lifecycle validation process exceeded its output bound.'
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdout
            Stderr = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
}

if (Test-Path -LiteralPath $output) {
    $resolvedOutput = [IO.Path]::GetFullPath(
        (Resolve-Path -LiteralPath $output).Path)
    if (-not $resolvedOutput.StartsWith(
            $expectedOutputPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace lifecycle output outside $artifactsRoot."
    }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
[IO.Directory]::CreateDirectory($output) | Out-Null

try {
    $runner = Invoke-BoundedProcess `
        -Executable 'dotnet' `
        -Arguments @(
            'run',
            '--project',
            $project,
            '--configuration',
            'Release',
            '--no-launch-profile',
            '--',
            '--input',
            $input
        )
    if ($runner.ExitCode -ne 0 -or
        -not [string]::IsNullOrEmpty($runner.Stderr)) {
        throw 'The externally staged Fleet lifecycle validation failed.'
    }
    $receipt = $runner.Stdout | ConvertFrom-Json -NoEnumerate
    if ($receipt.schema -cne
            'questionable.file_manager.fleet_installer_lifecycle_receipt.v1' -or
        $receipt.status -cne 'passed' -or
        $receipt.execution_mode -cne
            'controlled_external_setup_lifecycle' -or
        $receipt.replay_authority_mode -cne
            'isolated_non_authorizing_test_store' -or
        $receipt.status_and_plan_verified -ne $true -or
        $receipt.same_signer_update_verified -ne $true -or
        $receipt.side_by_side_release_retention_verified -ne $true -or
        $receipt.exact_rollback_readback_verified -ne $true -or
        $receipt.replay_rejected -ne $true -or
        $receipt.downgrade_rejected -ne $true -or
        $receipt.replay_high_water_preserved_after_rollback -ne $true -or
        $receipt.missing_machine_authority_rejected -ne $true -or
        $receipt.wrong_signer_rejected -ne $true -or
        $receipt.wrong_hash_rejected -ne $true -or
        $receipt.wrong_spki_rejected -ne $true -or
        $receipt.canonical_asset_url_verified -ne $true -or
        $receipt.stale_metadata_rejected -ne $true -or
        $receipt.partial_staging_rejected_and_cleaned -ne $true -or
        $receipt.cancellation_verified -ne $true -or
        $receipt.interrupted_candidate_retained_inert -ne $true -or
        $receipt.interrupted_recovery_verified -ne $true -or
        $receipt.qfm_stage_cleanup_verified -ne $true) {
        throw 'The Fleet lifecycle receipt is incomplete.'
    }
    $receiptJson = $receipt | ConvertTo-Json -Compress -Depth 20
    foreach ($localPath in $protectedInputs) {
        if ($receiptJson.Contains(
                $localPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The Fleet lifecycle receipt leaked a local path.'
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $output $receiptName),
        $receiptJson + "`n",
        [Text.UTF8Encoding]::new($false))

    if ($null -ne $qfmSetup) {
        $configuration = Invoke-BoundedProcess `
            -Executable 'pwsh' `
            -Arguments @(
                '-NoProfile',
                '-File',
                (Join-Path $repoRoot `
                    'tools\Test-FleetInstallerReleaseConfiguration.ps1'),
                '-SetupExecutablePath',
                $qfmSetup
            )
        if ($configuration.ExitCode -ne 0 -or
            -not [string]::IsNullOrEmpty($configuration.Stderr)) {
            throw 'The exact QFM Setup replay/configuration proof failed.'
        }
        $configurationReceipt =
            $configuration.Stdout | ConvertFrom-Json -NoEnumerate
        if ($configurationReceipt.schema -cne
                'questionable.file_manager.fleet_installer_release_configuration_test.v3' -or
            $configurationReceipt.status -cne 'passed' -or
            $configurationReceipt.validated_release_binary_count -lt 1) {
            throw 'The QFM Setup configuration receipt is invalid.'
        }
        $configurationJson =
            $configurationReceipt |
                ConvertTo-Json -Compress -Depth 10
        if ($configurationJson.Contains(
                $qfmSetup,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The QFM Setup configuration receipt leaked a local path.'
        }
        [IO.File]::WriteAllText(
            (Join-Path $output (
                'qfm-setup-fleet-release-configuration.receipt.json')),
            $configurationJson + "`n",
            [Text.UTF8Encoding]::new($false))
    }

    Write-Output $receiptJson
}
catch {
    if (Test-Path -LiteralPath $output) {
        $resolvedOutput = [IO.Path]::GetFullPath(
            (Resolve-Path -LiteralPath $output).Path)
        if ($resolvedOutput.StartsWith(
                $expectedOutputPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
        }
    }
    throw
}
