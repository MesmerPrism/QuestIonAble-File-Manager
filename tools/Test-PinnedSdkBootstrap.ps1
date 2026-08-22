[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$module = Import-Module (Join-Path $PSScriptRoot 'PinnedSdkBootstrap.psm1') -Force -PassThru
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("qfm-pinned-sdk-bootstrap-tests-" + [Guid]::NewGuid().ToString('N'))

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Message)
    try {
        & $Action
    }
    catch {
        return
    }
    throw $Message
}

function Set-TestHooks {
    param([hashtable]$Hooks, [string]$InstallerSha256)
    & $module {
        param($hooks, $hash)
        $script:QfmPinnedSdkBootstrapTestHooks = $hooks
        if ($null -ne $hash) {
            $script:QfmBootstrapContract.InstallerSha256 = $hash
        }
    } $Hooks $InstallerSha256
}

function Clear-TestHooks {
    & $module {
        $script:QfmPinnedSdkBootstrapTestHooks = $null
        $script:QfmBootstrapContract.InstallerSha256 = 'e8b873e18a81e5c4cd8ab69d84dac8fead291d50b3c44633cd7fddad709a13d6'
    }
}

function New-TestRepository {
    param([string]$Name, [string]$RollForward = 'disable')
    $root = Join-Path $testRoot $Name
    [void][System.IO.Directory]::CreateDirectory($root)
    [System.IO.File]::WriteAllText(
        (Join-Path $root 'global.json'),
        "{`n  `"sdk`": {`n    `"version`": `"10.0.302`",`n    `"rollForward`": `"$RollForward`"`n  }`n}`n",
        [Text.UTF8Encoding]::new($false))
    return $root
}

function New-TestHooks {
    param(
        [hashtable]$State,
        [string]$Version = '10.0.302',
        [string]$Architecture = 'x64',
        [switch]$DamageInstaller
    )

    $process = {
            param($filePath, $arguments, $workingDirectory)
            $State.ProcessCalls += [pscustomobject]@{ FilePath = $filePath; Arguments = @($arguments); WorkingDirectory = $workingDirectory }
            if ($filePath -match 'git(\.exe)?$') {
                if ($arguments[-1] -eq 'HEAD') { return [pscustomobject]@{ ExitCode = 0; StandardOutput = "1111111111111111111111111111111111111111`n"; StandardError = '' } }
                if ($arguments[-1] -eq 'HEAD^{tree}') { return [pscustomobject]@{ ExitCode = 0; StandardOutput = "2222222222222222222222222222222222222222`n"; StandardError = '' } }
                return [pscustomobject]@{ ExitCode = 0; StandardOutput = ''; StandardError = '' }
            }
            if ($arguments.Count -eq 1 -and $arguments[0] -eq '--version') {
                $reportedVersion = if ($State.CacheRoot -and $filePath.StartsWith($State.CacheRoot, [StringComparison]::OrdinalIgnoreCase)) { $Version } else { '10.0.201' }
                return [pscustomobject]@{ ExitCode = 0; StandardOutput = "$reportedVersion`n"; StandardError = '' }
            }
            if ($arguments.Count -eq 1 -and $arguments[0] -eq '--list-runtimes') {
                $runtimeRoot = Join-Path (Split-Path -Parent $filePath) 'shared\Microsoft.NETCore.App'
                return [pscustomobject]@{ ExitCode = 0; StandardOutput = "Microsoft.NETCore.App 10.0.10 [$runtimeRoot]`r`n"; StandardError = '' }
            }
            if ($arguments.Count -eq 1 -and $arguments[0] -eq '--info') {
                return [pscustomobject]@{ ExitCode = 0; StandardOutput = "SDK $Version`r`n  Architecture: $Architecture`r`n"; StandardError = '' }
            }
            if ($arguments[0] -eq 'restore') {
                return [pscustomobject]@{ ExitCode = 0; StandardOutput = ''; StandardError = '' }
            }
            if ($arguments[0] -eq 'publish') {
                $outputIndex = [Array]::IndexOf($arguments, '--output')
                $output = $arguments[$outputIndex + 1]
                [void][System.IO.Directory]::CreateDirectory($output)
                [System.IO.File]::WriteAllText((Join-Path $output 'questionable-file-manager.exe'), 'synthetic cli', [Text.UTF8Encoding]::new($false))
                [System.IO.File]::WriteAllText((Join-Path $output 'QuestIonAbleFileManager.Core.dll'), 'synthetic core', [Text.UTF8Encoding]::new($false))
                return [pscustomobject]@{ ExitCode = 0; StandardOutput = ''; StandardError = '' }
            }
            if ($arguments.Count -eq 1 -and $arguments[0] -eq '--help') {
                return [pscustomobject]@{ ExitCode = 0; StandardOutput = "QFM help`n"; StandardError = '' }
            }
            throw "Unexpected synthetic process: $filePath $($arguments -join '|')"
    }.GetNewClosure()
    $download = {
            param($uri, $outputPath)
            $State.DownloadCalls++
            $content = if ($DamageInstaller) { 'damaged installer' } else { 'reviewed installer' }
            [System.IO.File]::WriteAllText($outputPath, $content, [Text.UTF8Encoding]::new($false))
    }.GetNewClosure()
    $install = {
            param($installerPath, $installDirectory, $requestedVersion, $workingDirectory)
            $State.InstallCalls++
            [void][System.IO.Directory]::CreateDirectory($installDirectory)
            [System.IO.File]::WriteAllText((Join-Path $installDirectory 'dotnet.exe'), 'synthetic dotnet', [Text.UTF8Encoding]::new($false))
            return [pscustomobject]@{ ExitCode = 0; StandardOutput = ''; StandardError = '' }
    }.GetNewClosure()
    return @{
        Process = $process
        Download = $download
        Install = $install
    }
}

try {
    [void][System.IO.Directory]::CreateDirectory($testRoot)

    $wrongRollForward = New-TestRepository -Name 'wrong-roll-forward' -RollForward 'latestPatch'
    Assert-Throws { Invoke-QfmPinnedSdkBootstrap -RepositoryRoot $wrongRollForward -CacheRoot (Join-Path $wrongRollForward 'cache') } 'A non-disable rollForward pin must fail closed.'

    $state = @{ DownloadCalls = 0; InstallCalls = 0; ProcessCalls = @() }
    $repoWithSpaces = New-TestRepository -Name 'repo with spaces'
    $cacheWithSpaces = Join-Path $repoWithSpaces 'cache with spaces'
    $state.CacheRoot = $cacheWithSpaces
    $reviewedInstallerHash = (& $module { param($value) Get-QfmSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes($value)) } 'reviewed installer')
    Set-TestHooks -Hooks (New-TestHooks -State $state) -InstallerSha256 $reviewedInstallerHash
    $first = Invoke-QfmPinnedSdkBootstrap -RepositoryRoot $repoWithSpaces -CacheRoot $cacheWithSpaces
    Assert-True ($first.result -ceq 'built') 'An absent SDK must install and build once.'
    Assert-True ($state.DownloadCalls -eq 1 -and $state.InstallCalls -eq 1) 'The missing SDK path must use one reviewed installer invocation.'
    Assert-True (($state.ProcessCalls | Where-Object { $_.Arguments[0] -eq 'publish' }).Count -eq 1) 'The first result must publish once.'
    $publishCall = $state.ProcessCalls | Where-Object { $_.Arguments[0] -eq 'publish' } | Select-Object -First 1
    $projectPath = Join-Path $repoWithSpaces 'src\QuestIonAbleFileManager.Cli\QuestIonAbleFileManager.Cli.csproj'
    $outputIndex = [Array]::IndexOf($publishCall.Arguments, '--output')
    Assert-True ($publishCall.Arguments -contains $projectPath) 'Process arguments must retain a repository path with spaces as one value.'
    Assert-True ($publishCall.Arguments[$outputIndex + 1].StartsWith($cacheWithSpaces, [StringComparison]::Ordinal)) 'Process arguments must retain a cache path with spaces as one value.'

    $second = Invoke-QfmPinnedSdkBootstrap -RepositoryRoot $repoWithSpaces -CacheRoot $cacheWithSpaces
    Assert-True ($second.result -ceq 'reused') 'A valid content-addressed result must be reused.'
    Assert-True ($state.DownloadCalls -eq 1 -and $state.InstallCalls -eq 1) 'Cache reuse must not download or install again.'

    $builtCli = Join-Path $cacheWithSpaces 'builds\2222222222222222222222222222222222222222\10.0.302\x64\questionable-file-manager.exe'
    [System.IO.File]::WriteAllText($builtCli, 'tampered cli', [Text.UTF8Encoding]::new($false))
    Assert-Throws { Invoke-QfmPinnedSdkBootstrap -RepositoryRoot $repoWithSpaces -CacheRoot $cacheWithSpaces } 'A damaged content-addressed CLI result must fail closed.'

    $cachedDotnet = Join-Path $cacheWithSpaces 'sdks\10.0.302\x64\dotnet.exe'
    [System.IO.File]::WriteAllText($cachedDotnet, 'tampered dotnet', [Text.UTF8Encoding]::new($false))
    Assert-Throws { Invoke-QfmPinnedSdkBootstrap -RepositoryRoot $repoWithSpaces -CacheRoot $cacheWithSpaces } 'A damaged cached SDK result must fail closed.'

    $wrongVersionState = @{ DownloadCalls = 0; InstallCalls = 0; ProcessCalls = @() }
    $wrongVersionRepo = New-TestRepository -Name 'wrong-version'
    $wrongVersionCache = Join-Path $wrongVersionRepo 'cache'
    $wrongVersionState.CacheRoot = $wrongVersionCache
    Set-TestHooks -Hooks (New-TestHooks -State $wrongVersionState -Version '10.0.301') -InstallerSha256 $reviewedInstallerHash
    [void][System.IO.Directory]::CreateDirectory((Join-Path $wrongVersionCache 'sdks\10.0.302\x64'))
    [System.IO.File]::WriteAllText((Join-Path $wrongVersionCache 'sdks\10.0.302\x64\dotnet.exe'), 'wrong SDK', [Text.UTF8Encoding]::new($false))
    Assert-Throws { Invoke-QfmPinnedSdkBootstrap -RepositoryRoot $wrongVersionRepo -CacheRoot $wrongVersionCache } 'A wrong SDK version in the stable cache must fail closed.'

    $wrongArchitectureState = @{ DownloadCalls = 0; InstallCalls = 0; ProcessCalls = @() }
    $wrongArchitectureRepo = New-TestRepository -Name 'wrong-architecture'
    $wrongArchitectureCache = Join-Path $wrongArchitectureRepo 'cache'
    $wrongArchitectureState.CacheRoot = $wrongArchitectureCache
    Set-TestHooks -Hooks (New-TestHooks -State $wrongArchitectureState -Architecture 'arm64') -InstallerSha256 $reviewedInstallerHash
    [void][System.IO.Directory]::CreateDirectory((Join-Path $wrongArchitectureCache 'sdks\10.0.302\x64'))
    [System.IO.File]::WriteAllText((Join-Path $wrongArchitectureCache 'sdks\10.0.302\x64\dotnet.exe'), 'wrong architecture SDK', [Text.UTF8Encoding]::new($false))
    Assert-Throws { Invoke-QfmPinnedSdkBootstrap -RepositoryRoot $wrongArchitectureRepo -CacheRoot $wrongArchitectureCache } 'A non-x64 SDK in the stable cache must fail closed.'

    $damageState = @{ DownloadCalls = 0; InstallCalls = 0; ProcessCalls = @() }
    $damageRepo = New-TestRepository -Name 'damaged-installer'
    $damageCache = Join-Path $damageRepo 'cache'
    $damageState.CacheRoot = $damageCache
    Set-TestHooks -Hooks (New-TestHooks -State $damageState -DamageInstaller) -InstallerSha256 $reviewedInstallerHash
    Assert-Throws { Invoke-QfmPinnedSdkBootstrap -RepositoryRoot $damageRepo -CacheRoot $damageCache } 'A hash-mismatched installer must fail before installation.'
    Assert-True ($damageState.InstallCalls -eq 0) 'A damaged installer must never run.'

    Clear-TestHooks
    $quotedPathRoot = Join-Path $testRoot 'paths with spaces'
    [void][System.IO.Directory]::CreateDirectory($quotedPathRoot)
    $quotedChild = Join-Path $quotedPathRoot 'quoted child.ps1'
    $quotedOutput = Join-Path $quotedPathRoot 'quoted output.txt'
    [System.IO.File]::WriteAllText($quotedChild, @'
param([string]$Value, [string]$Output)
[System.IO.File]::WriteAllText($Output, $Value, [Text.UTF8Encoding]::new($false))
'@, [Text.UTF8Encoding]::new($false))
    $quotedValue = 'one value with spaces and an embedded "quote"'
    $quotedResult = & $module {
        param($child, $output, $value, $workingDirectory)
        Invoke-QfmProcess -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @(
            '-NoProfile', '-File', $child, '-Value', $value, '-Output', $output) -WorkingDirectory $workingDirectory
    } $quotedChild $quotedOutput $quotedValue $quotedPathRoot
    Assert-True ($quotedResult.ExitCode -eq 0) 'The unhooked path-and-quote child process must succeed.'
    Assert-True ((Get-Content -Raw -LiteralPath $quotedOutput) -ceq $quotedValue) 'ProcessStartInfo.ArgumentList must retain paths with spaces and embedded quotes exactly.'

    $mutexCache = Join-Path $testRoot 'mutex cache'
    $mutexName = & $module { param($cache) Get-QfmBootstrapMutexName -CacheRoot $cache } $mutexCache
    $readyPath = Join-Path $testRoot 'mutex-ready'
    $job = Start-ThreadJob -ScriptBlock {
        param($name, $ready)
        $held = [System.Threading.Mutex]::new($false, $name)
        try {
            [void]$held.WaitOne()
            [System.IO.File]::WriteAllText($ready, 'ready')
            Start-Sleep -Milliseconds 500
        }
        finally {
            $held.ReleaseMutex()
            $held.Dispose()
        }
    } -ArgumentList $mutexName, $readyPath
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(3)
    while (-not (Test-Path -LiteralPath $readyPath) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 20
    }
    Assert-True (Test-Path -LiteralPath $readyPath) 'The mutex holder did not become ready.'
    Assert-Throws {
        & $module { param($cache) Enter-QfmBootstrapMutex -CacheRoot $cache -Timeout ([TimeSpan]::FromMilliseconds(25)) } $mutexCache
    } 'Concurrent callers must contend on the same named bootstrap mutex.'
    Wait-Job -Job $job | Out-Null
    Receive-Job -Job $job | Out-Null
    Remove-Job -Job $job -Force

    'Pinned SDK bootstrap synthetic tests passed.'
}
finally {
    Clear-TestHooks
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = [System.IO.Path]::GetFullPath($testRoot)
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolved).StartsWith('qfm-pinned-sdk-bootstrap-tests-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
