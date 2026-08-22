Set-StrictMode -Version Latest

$script:QfmBootstrapContract = [ordered]@{
    Schema          = 'questionable.file_manager.pinned_sdk_build_receipt.v1'
    InstallerUri    = 'https://dot.net/v1/dotnet-install.ps1'
    InstallerSha256 = 'e8b873e18a81e5c4cd8ab69d84dac8fead291d50b3c44633cd7fddad709a13d6'
    RuntimeVersion  = '10.0.10'
    Architecture    = 'x64'
}

# Test-only hooks remain module-private. Production callers cannot provide them.
$script:QfmPinnedSdkBootstrapTestHooks = $null

function Get-QfmSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-QfmFileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected file is missing: $Path"
    }

    return Get-QfmSha256 -Bytes ([System.IO.File]::ReadAllBytes($Path))
}

function Get-QfmDefaultCacheRoot {
    $localApplicationData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localApplicationData)) {
        throw 'The current user does not have a LocalApplicationData directory for the pinned SDK cache.'
    }

    return Join-Path $localApplicationData 'QuestIonAbleFileManager\pinned-sdk-bootstrap'
}

function Resolve-QfmFullPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label must not be empty."
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Get-QfmPinnedSdk {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $globalJsonPath = Join-Path $RepositoryRoot 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
        throw 'global.json is required for the pinned SDK bootstrap.'
    }

    $bytes = [System.IO.File]::ReadAllBytes($globalJsonPath)
    try {
        $document = [System.Text.Json.JsonDocument]::Parse([Text.Encoding]::UTF8.GetString($bytes))
    }
    catch {
        throw 'global.json is not valid JSON.'
    }

    try {
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw 'global.json must contain one root object.'
        }

        $rootProperties = @($document.RootElement.EnumerateObject())
        if ($rootProperties.Count -ne 1 -or $rootProperties[0].Name -ne 'sdk') {
            throw 'global.json must contain exactly one sdk object.'
        }

        $sdk = $rootProperties[0].Value
        if ($sdk.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw 'global.json sdk must be an object.'
        }

        $sdkProperties = @($sdk.EnumerateObject())
        if ($sdkProperties.Count -ne 2 -or
            @($sdkProperties.Name | Sort-Object) -join ',' -ne 'rollForward,version') {
            throw 'global.json sdk must contain exactly version and rollForward.'
        }

        $versionProperty = @($sdkProperties | Where-Object Name -eq 'version')
        $rollForwardProperty = @($sdkProperties | Where-Object Name -eq 'rollForward')
        if ($versionProperty.Count -ne 1 -or $rollForwardProperty.Count -ne 1 -or
            $versionProperty[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $rollForwardProperty[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            throw 'global.json sdk version and rollForward must be strings.'
        }

        $version = $versionProperty[0].Value.GetString()
        $rollForward = $rollForwardProperty[0].Value.GetString()
        if ($version -notmatch '^\d+\.\d+\.\d+$') {
            throw 'global.json sdk version must be an exact three-part SDK version.'
        }
        if ($rollForward -cne 'disable') {
            throw 'global.json sdk rollForward must remain exactly disable.'
        }

        return [pscustomobject]@{
            Version        = $version
            RollForward    = $rollForward
            GlobalJsonHash = Get-QfmSha256 -Bytes $bytes
        }
    }
    finally {
        $document.Dispose()
    }
}

function Invoke-QfmProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    if ($null -ne $script:QfmPinnedSdkBootstrapTestHooks -and
        $script:QfmPinnedSdkBootstrapTestHooks.ContainsKey('Process')) {
        return & $script:QfmPinnedSdkBootstrapTestHooks.Process $FilePath $ArgumentList $WorkingDirectory
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start $FilePath."
        }
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode      = $process.ExitCode
            StandardOutput = $standardOutput.GetAwaiter().GetResult()
            StandardError  = $standardError.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-QfmProcessSucceeded {
    param(
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][string]$Operation
    )

    if ($Result.ExitCode -ne 0) {
        throw "$Operation failed with exit code $($Result.ExitCode)."
    }
}

function Get-QfmGitExecutable {
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $git -or [string]::IsNullOrWhiteSpace($git.Source)) {
        throw 'Git is required to bind the CLI build to an exact clean source tree.'
    }

    return $git.Source
}

function Get-QfmCleanSourceIdentity {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $git = Get-QfmGitExecutable
    $head = Invoke-QfmProcess -FilePath $git -ArgumentList @('-C', $RepositoryRoot, 'rev-parse', 'HEAD') -WorkingDirectory $RepositoryRoot
    Assert-QfmProcessSucceeded -Result $head -Operation 'Resolving the source commit'
    $commit = $head.StandardOutput.Trim()
    if ($commit -notmatch '^[0-9a-f]{40}$') {
        throw 'Git did not return a full lowercase source commit identity.'
    }

    $treeResult = Invoke-QfmProcess -FilePath $git -ArgumentList @('-C', $RepositoryRoot, 'rev-parse', 'HEAD^{tree}') -WorkingDirectory $RepositoryRoot
    Assert-QfmProcessSucceeded -Result $treeResult -Operation 'Resolving the source tree'
    $tree = $treeResult.StandardOutput.Trim()
    if ($tree -notmatch '^[0-9a-f]{40}$') {
        throw 'Git did not return a full lowercase source tree identity.'
    }

    $status = Invoke-QfmProcess -FilePath $git -ArgumentList @('-C', $RepositoryRoot, 'status', '--porcelain=v1', '--untracked-files=all') -WorkingDirectory $RepositoryRoot
    Assert-QfmProcessSucceeded -Result $status -Operation 'Checking source cleanliness'
    if (-not [string]::IsNullOrEmpty($status.StandardOutput)) {
        throw 'The QFM source checkout must be clean before a content-addressed CLI build.'
    }

    return [pscustomobject]@{
        Commit = $commit
        Tree   = $tree
    }
}

function Invoke-QfmWithSdkEnvironment {
    param(
        [Parameter(Mandatory)][string]$DotnetPath,
        [string]$ExecutablePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$CacheRoot
    )

    $sdkRoot = Split-Path -Parent $DotnetPath
    $original = @{}
    foreach ($name in @('DOTNET_ROOT', 'DOTNET_MULTILEVEL_LOOKUP', 'DOTNET_CLI_HOME', 'DOTNET_CLI_UI_LANGUAGE')) {
        $original[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }

    try {
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $sdkRoot, 'Process')
        [Environment]::SetEnvironmentVariable('DOTNET_MULTILEVEL_LOOKUP', '0', 'Process')
        [Environment]::SetEnvironmentVariable(
            'DOTNET_CLI_HOME',
            (Join-Path $CacheRoot 'dotnet-cli-home'),
            'Process')
        [Environment]::SetEnvironmentVariable('DOTNET_CLI_UI_LANGUAGE', 'en-US', 'Process')
        $filePath = if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { $DotnetPath } else { $ExecutablePath }
        return Invoke-QfmProcess -FilePath $filePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory
    }
    finally {
        foreach ($name in $original.Keys) {
            [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process')
        }
    }
}

function Get-QfmDotnetVersion {
    param(
        [Parameter(Mandatory)][string]$DotnetPath,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$CacheRoot
    )

    $result = Invoke-QfmWithSdkEnvironment -DotnetPath $DotnetPath -ArgumentList @('--version') -WorkingDirectory $RepositoryRoot -CacheRoot $CacheRoot
    if ($result.ExitCode -ne 0) {
        return $null
    }

    return $result.StandardOutput.Trim()
}

function Test-QfmSdk {
    param(
        [Parameter(Mandatory)][string]$DotnetPath,
        [Parameter(Mandatory)]$Pin,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$CacheRoot,
        [Parameter(Mandatory)][string]$Source
    )

    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        throw "The $Source SDK executable is missing."
    }

    $version = Get-QfmDotnetVersion -DotnetPath $DotnetPath -RepositoryRoot $RepositoryRoot -CacheRoot $CacheRoot
    if ($version -cne $Pin.Version) {
        throw "The $Source SDK version is '$version', not the required '$($Pin.Version)'."
    }

    $runtimeResult = Invoke-QfmWithSdkEnvironment -DotnetPath $DotnetPath -ArgumentList @('--list-runtimes') -WorkingDirectory $RepositoryRoot -CacheRoot $CacheRoot
    Assert-QfmProcessSucceeded -Result $runtimeResult -Operation "Checking the $Source SDK runtime"
    $runtimePattern = "(?m)^Microsoft\.NETCore\.App $([Regex]::Escape($script:QfmBootstrapContract.RuntimeVersion)) \[(?<root>[^\]\r\n]+)\]\r?$"
    $runtime = [Regex]::Match($runtimeResult.StandardOutput, $runtimePattern)
    if (-not $runtime.Success) {
        throw "The $Source SDK does not contain Microsoft.NETCore.App $($script:QfmBootstrapContract.RuntimeVersion)."
    }

    $sdkRoot = Resolve-QfmFullPath -Path (Split-Path -Parent $DotnetPath) -Label 'SDK root'
    $runtimeRoot = Resolve-QfmFullPath -Path $runtime.Groups['root'].Value -Label 'SDK runtime root'
    $expectedRuntimeRoot = Resolve-QfmFullPath -Path (Join-Path $sdkRoot 'shared\Microsoft.NETCore.App') -Label 'expected SDK runtime root'
    if ($runtimeRoot -cne $expectedRuntimeRoot) {
        throw "The $Source SDK runtime is outside the selected SDK root."
    }

    $info = Invoke-QfmWithSdkEnvironment -DotnetPath $DotnetPath -ArgumentList @('--info') -WorkingDirectory $RepositoryRoot -CacheRoot $CacheRoot
    Assert-QfmProcessSucceeded -Result $info -Operation "Checking the $Source SDK architecture"
    if (-not [Regex]::IsMatch($info.StandardOutput, '(?m)^\s*Architecture:\s*x64\s*\r?$')) {
        throw "The $Source SDK is not an x64 SDK."
    }

    return [pscustomobject]@{
        Root          = $sdkRoot
        DotnetPath    = $DotnetPath
        Version       = $version
        Runtime       = $script:QfmBootstrapContract.RuntimeVersion
        DotnetSha256  = Get-QfmFileSha256 -Path $DotnetPath
        Source        = $Source
    }
}

function Get-QfmSdkCacheRoot {
    param(
        [Parameter(Mandatory)][string]$CacheRoot,
        [Parameter(Mandatory)][string]$Version
    )

    return Join-Path $CacheRoot (Join-Path 'sdks' (Join-Path $Version $script:QfmBootstrapContract.Architecture))
}

function Find-QfmPinnedSdk {
    param(
        [Parameter(Mandatory)]$Pin,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$CacheRoot
    )

    $cacheSdkRoot = Get-QfmSdkCacheRoot -CacheRoot $CacheRoot -Version $Pin.Version
    if (Test-Path -LiteralPath $cacheSdkRoot) {
        $cached = Test-QfmSdk -DotnetPath (Join-Path $cacheSdkRoot 'dotnet.exe') -Pin $Pin -RepositoryRoot $RepositoryRoot -CacheRoot $CacheRoot -Source 'cached'
        if (-not (Test-QfmSdkCacheReceipt -Sdk $cached -Pin $Pin)) {
            throw 'The cached SDK receipt is missing, malformed, or hash-mismatched.'
        }
        return $cached
    }

    $candidateRoots = @()
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $candidateRoots += [pscustomobject]@{ Root = $env:DOTNET_ROOT; Source = 'explicit DOTNET_ROOT' }
    }
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
        $candidateRoots += [pscustomobject]@{ Root = (Join-Path $programFiles 'dotnet'); Source = 'Program Files' }
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidateRoots) {
        $root = Resolve-QfmFullPath -Path $candidate.Root -Label "$($candidate.Source) SDK root"
        if (-not $seen.Add($root)) {
            continue
        }

        $dotnetPath = Join-Path $root 'dotnet.exe'
        if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
            continue
        }

        $version = Get-QfmDotnetVersion -DotnetPath $dotnetPath -RepositoryRoot $RepositoryRoot -CacheRoot $CacheRoot
        if ($version -cne $Pin.Version) {
            continue
        }

        return Test-QfmSdk -DotnetPath $dotnetPath -Pin $Pin -RepositoryRoot $RepositoryRoot -CacheRoot $CacheRoot -Source $candidate.Source
    }

    return $null
}

function Invoke-QfmDownload {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$OutputPath
    )

    if ($null -ne $script:QfmPinnedSdkBootstrapTestHooks -and
        $script:QfmPinnedSdkBootstrapTestHooks.ContainsKey('Download')) {
        & $script:QfmPinnedSdkBootstrapTestHooks.Download $Uri $OutputPath
        return
    }

    Invoke-WebRequest -Uri $Uri -OutFile $OutputPath -ErrorAction Stop
}

function Get-QfmInstaller {
    param([Parameter(Mandatory)][string]$CacheRoot)

    $installerDirectory = Join-Path $CacheRoot 'installers'
    [void][System.IO.Directory]::CreateDirectory($installerDirectory)
    $installerPath = Join-Path $installerDirectory "dotnet-install-$($script:QfmBootstrapContract.InstallerSha256).ps1"
    if (Test-Path -LiteralPath $installerPath -PathType Leaf) {
        if ((Get-QfmFileSha256 -Path $installerPath) -cne $script:QfmBootstrapContract.InstallerSha256) {
            throw 'The cached Microsoft portable installer hash does not match the reviewed QFM pin.'
        }
        return $installerPath
    }

    $stagingDirectory = Join-Path $CacheRoot (Join-Path 'staging' ("installer-" + [Guid]::NewGuid().ToString('N')))
    [void][System.IO.Directory]::CreateDirectory($stagingDirectory)
    $downloadPath = Join-Path $stagingDirectory 'dotnet-install.ps1'
    Invoke-QfmDownload -Uri $script:QfmBootstrapContract.InstallerUri -OutputPath $downloadPath
    if ((Get-QfmFileSha256 -Path $downloadPath) -cne $script:QfmBootstrapContract.InstallerSha256) {
        throw 'The downloaded Microsoft portable installer hash does not match the reviewed QFM pin.'
    }
    if (Test-Path -LiteralPath $installerPath) {
        throw 'The stable installer cache appeared while the bootstrap mutex was held.'
    }
    [System.IO.File]::Move($downloadPath, $installerPath)
    return $installerPath
}

function Invoke-QfmInstaller {
    param(
        [Parameter(Mandatory)][string]$InstallerPath,
        [Parameter(Mandatory)][string]$InstallDirectory,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    if ($null -ne $script:QfmPinnedSdkBootstrapTestHooks -and
        $script:QfmPinnedSdkBootstrapTestHooks.ContainsKey('Install')) {
        return & $script:QfmPinnedSdkBootstrapTestHooks.Install $InstallerPath $InstallDirectory $Version $WorkingDirectory
    }

    $pwsh = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwsh -PathType Leaf)) {
        throw 'The current PowerShell host cannot locate its own pwsh.exe for the Microsoft portable installer.'
    }
    return Invoke-QfmProcess -FilePath $pwsh -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $InstallerPath,
        '-Version', $Version,
        '-InstallDir', $InstallDirectory,
        '-Architecture', $script:QfmBootstrapContract.Architecture,
        '-NoPath'
    ) -WorkingDirectory $WorkingDirectory
}

function Install-QfmPinnedSdk {
    param(
        [Parameter(Mandatory)]$Pin,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$CacheRoot
    )

    $installer = Get-QfmInstaller -CacheRoot $CacheRoot
    $stagingDirectory = Join-Path $CacheRoot (Join-Path 'staging' ("sdk-" + [Guid]::NewGuid().ToString('N')))
    [void][System.IO.Directory]::CreateDirectory($stagingDirectory)
    $install = Invoke-QfmInstaller -InstallerPath $installer -InstallDirectory $stagingDirectory -Version $Pin.Version -WorkingDirectory $RepositoryRoot
    Assert-QfmProcessSucceeded -Result $install -Operation 'Installing the exact Microsoft .NET SDK'
    $validated = Test-QfmSdk -DotnetPath (Join-Path $stagingDirectory 'dotnet.exe') -Pin $Pin -RepositoryRoot $RepositoryRoot -CacheRoot $CacheRoot -Source 'newly installed'

    $targetDirectory = Get-QfmSdkCacheRoot -CacheRoot $CacheRoot -Version $Pin.Version
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $targetDirectory))
    if (Test-Path -LiteralPath $targetDirectory) {
        throw 'The stable SDK cache appeared while the bootstrap mutex was held.'
    }
    [System.IO.Directory]::Move($stagingDirectory, $targetDirectory)
    $cached = Test-QfmSdk -DotnetPath (Join-Path $targetDirectory 'dotnet.exe') -Pin $Pin -RepositoryRoot $RepositoryRoot -CacheRoot $CacheRoot -Source 'installed cache'
    Write-QfmJsonAtomically -Path (Join-Path $targetDirectory 'qfm-sdk-receipt.json') -Value ([ordered]@{
        schema             = 'questionable.file_manager.pinned_sdk_cache_receipt.v1'
        result             = 'pass'
        created_utc        = [DateTimeOffset]::UtcNow.ToString('O')
        sdk                = [ordered]@{
            version        = $cached.Version
            runtime        = $cached.Runtime
            dotnet_sha256  = $cached.DotnetSha256
        }
        installer_sha256   = $script:QfmBootstrapContract.InstallerSha256
    })
    return $cached
}

function Get-QfmOutputTreeSha256 {
    param([Parameter(Mandatory)][string]$Root)

    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        Where-Object Name -cne 'qfm-cli-build-receipt.json' |
        Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw 'The published CLI output is empty.'
    }

    $manifest = [System.Text.StringBuilder]::new()
    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        [void]$manifest.Append($relative)
        [void]$manifest.Append("`t")
        [void]$manifest.Append($file.Length)
        [void]$manifest.Append("`t")
        [void]$manifest.Append((Get-QfmFileSha256 -Path $file.FullName))
        [void]$manifest.Append("`n")
    }

    return Get-QfmSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes($manifest.ToString()))
}

function Write-QfmJsonAtomically {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value
    )

    if (Test-Path -LiteralPath $Path) {
        throw "Refusing to overwrite an existing receipt: $Path"
    }

    $temporaryPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $json = $Value | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    [System.IO.File]::Move($temporaryPath, $Path)
}

function Test-QfmSdkCacheReceipt {
    param(
        [Parameter(Mandatory)]$Sdk,
        [Parameter(Mandatory)]$Pin
    )

    $receiptPath = Join-Path $Sdk.Root 'qfm-sdk-receipt.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        return $false
    }
    try {
        $receipt = Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json -Depth 6
        return $receipt.schema -ceq 'questionable.file_manager.pinned_sdk_cache_receipt.v1' -and
            $receipt.result -ceq 'pass' -and
            $receipt.sdk.version -ceq $Pin.Version -and
            $receipt.sdk.runtime -ceq $script:QfmBootstrapContract.RuntimeVersion -and
            $receipt.sdk.dotnet_sha256 -ceq $Sdk.DotnetSha256 -and
            $receipt.installer_sha256 -ceq $script:QfmBootstrapContract.InstallerSha256
    }
    catch {
        return $false
    }
}

function Test-QfmBuildReceipt {
    param(
        [Parameter(Mandatory)][string]$BuildRoot,
        [Parameter(Mandatory)]$Pin,
        [Parameter(Mandatory)]$Source,
        [Parameter(Mandatory)]$Sdk
    )

    $receiptPath = Join-Path $BuildRoot 'qfm-cli-build-receipt.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        return $false
    }
    try {
        $receipt = Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json -Depth 10
        $artifactPath = Join-Path $BuildRoot 'questionable-file-manager.exe'
        return $receipt.schema -ceq $script:QfmBootstrapContract.Schema -and
            $receipt.result -ceq 'pass' -and
            $receipt.sdk.version -ceq $Pin.Version -and
            $receipt.sdk.runtime -ceq $script:QfmBootstrapContract.RuntimeVersion -and
            $receipt.sdk.dotnet_sha256 -ceq $Sdk.DotnetSha256 -and
            $receipt.source.commit -ceq $Source.Commit -and
            $receipt.source.tree -ceq $Source.Tree -and
            $receipt.source.global_json_sha256 -ceq $Pin.GlobalJsonHash -and
            $receipt.artifact.name -ceq 'questionable-file-manager.exe' -and
            (Test-Path -LiteralPath $artifactPath -PathType Leaf) -and
            $receipt.artifact.sha256 -ceq (Get-QfmFileSha256 -Path $artifactPath) -and
            $receipt.artifact.bytes -eq (Get-Item -LiteralPath $artifactPath).Length -and
            $receipt.output_tree_sha256 -ceq (Get-QfmOutputTreeSha256 -Root $BuildRoot)
    }
    catch {
        return $false
    }
}

function Invoke-QfmContentAddressedCliBuild {
    param(
        [Parameter(Mandatory)]$Pin,
        [Parameter(Mandatory)]$Source,
        [Parameter(Mandatory)]$Sdk,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$CacheRoot
    )

    $buildRoot = Join-Path $CacheRoot (Join-Path 'builds' (Join-Path $Source.Tree (Join-Path $Pin.Version $script:QfmBootstrapContract.Architecture)))
    if (Test-Path -LiteralPath $buildRoot) {
        if (-not (Test-QfmBuildReceipt -BuildRoot $buildRoot -Pin $Pin -Source $Source -Sdk $Sdk)) {
            throw 'The existing content-addressed CLI build is missing, malformed, or hash-mismatched.'
        }
        $artifact = Join-Path $buildRoot 'questionable-file-manager.exe'
        return [pscustomobject]@{
            Result              = 'reused'
            BuildRoot           = $buildRoot
            ArtifactPath        = $artifact
            ArtifactSha256      = Get-QfmFileSha256 -Path $artifact
            ArtifactBytes       = (Get-Item -LiteralPath $artifact).Length
            OutputTreeSha256    = Get-QfmOutputTreeSha256 -Root $buildRoot
            ReceiptRelativePath = (Join-Path (Join-Path 'builds' (Join-Path $Source.Tree (Join-Path $Pin.Version $script:QfmBootstrapContract.Architecture))) 'qfm-cli-build-receipt.json').Replace('\', '/')
        }
    }

    $stagingDirectory = Join-Path $CacheRoot (Join-Path 'staging' ("build-" + [Guid]::NewGuid().ToString('N')))
    [void][System.IO.Directory]::CreateDirectory($stagingDirectory)
    $solution = Join-Path $RepositoryRoot 'QuestIonAbleFileManager.slnx'
    $project = Join-Path $RepositoryRoot 'src\QuestIonAbleFileManager.Cli\QuestIonAbleFileManager.Cli.csproj'
    $restore = Invoke-QfmWithSdkEnvironment -DotnetPath $Sdk.DotnetPath -ArgumentList @('restore', $solution, '--nologo') -WorkingDirectory $RepositoryRoot -CacheRoot $CacheRoot
    Assert-QfmProcessSucceeded -Result $restore -Operation 'Restoring the exact QFM source'
    $publish = Invoke-QfmWithSdkEnvironment -DotnetPath $Sdk.DotnetPath -ArgumentList @(
        'publish', $project,
        '--configuration', 'Release',
        '--no-restore',
        '--nologo',
        '--output', $stagingDirectory
    ) -WorkingDirectory $RepositoryRoot -CacheRoot $CacheRoot
    Assert-QfmProcessSucceeded -Result $publish -Operation 'Publishing the QFM CLI'

    $artifact = Join-Path $stagingDirectory 'questionable-file-manager.exe'
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw 'The exact QFM CLI publish did not produce questionable-file-manager.exe.'
    }
    $smoke = Invoke-QfmWithSdkEnvironment -DotnetPath $Sdk.DotnetPath -ArgumentList @('--info') -WorkingDirectory $RepositoryRoot -CacheRoot $CacheRoot
    Assert-QfmProcessSucceeded -Result $smoke -Operation 'Reading the selected SDK information'
    $cliSmoke = Invoke-QfmWithSdkEnvironment -DotnetPath $Sdk.DotnetPath -ExecutablePath $artifact -ArgumentList @('--help') -WorkingDirectory $RepositoryRoot -CacheRoot $CacheRoot
    Assert-QfmProcessSucceeded -Result $cliSmoke -Operation 'Smoke-running the published QFM CLI'

    $artifactSha256 = Get-QfmFileSha256 -Path $artifact
    $artifactBytes = (Get-Item -LiteralPath $artifact).Length
    $outputTreeSha256 = Get-QfmOutputTreeSha256 -Root $stagingDirectory
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $buildRoot))
    if (Test-Path -LiteralPath $buildRoot) {
        throw 'The content-addressed CLI output appeared while the bootstrap mutex was held.'
    }
    [System.IO.Directory]::Move($stagingDirectory, $buildRoot)

    $receipt = [ordered]@{
        schema                  = $script:QfmBootstrapContract.Schema
        result                  = 'pass'
        created_utc             = [DateTimeOffset]::UtcNow.ToString('O')
        sdk                     = [ordered]@{
            version        = $Sdk.Version
            runtime        = $Sdk.Runtime
            source         = $Sdk.Source
            dotnet_sha256  = $Sdk.DotnetSha256
        }
        source                  = [ordered]@{
            commit              = $Source.Commit
            tree                = $Source.Tree
            global_json_sha256  = $Pin.GlobalJsonHash
            roll_forward        = $Pin.RollForward
        }
        artifact                = [ordered]@{
            name                = 'questionable-file-manager.exe'
            sha256              = $artifactSha256
            bytes               = $artifactBytes
        }
        output_tree_sha256      = $outputTreeSha256
        cli_smoke               = [ordered]@{
            exit_code           = $cliSmoke.ExitCode
            stdout_sha256       = Get-QfmSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes($cliSmoke.StandardOutput))
        }
        network_or_device_proof = $false
    }
    Write-QfmJsonAtomically -Path (Join-Path $buildRoot 'qfm-cli-build-receipt.json') -Value $receipt

    return [pscustomobject]@{
        Result              = 'built'
        BuildRoot           = $buildRoot
        ArtifactPath        = (Join-Path $buildRoot 'questionable-file-manager.exe')
        ArtifactSha256      = $artifactSha256
        ArtifactBytes       = $artifactBytes
        OutputTreeSha256    = $outputTreeSha256
        ReceiptRelativePath = (Join-Path (Join-Path 'builds' (Join-Path $Source.Tree (Join-Path $Pin.Version $script:QfmBootstrapContract.Architecture))) 'qfm-cli-build-receipt.json').Replace('\', '/')
    }
}

function Get-QfmBootstrapMutexName {
    param([Parameter(Mandatory)][string]$CacheRoot)

    $identity = Resolve-QfmFullPath -Path $CacheRoot -Label 'SDK cache root'
    $digest = Get-QfmSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes($identity.ToUpperInvariant()))
    return "Local\QuestIonAbleFileManager.PinnedSdkBootstrap.$($digest.Substring(0, 32))"
}

function Enter-QfmBootstrapMutex {
    param(
        [Parameter(Mandatory)][string]$CacheRoot,
        [TimeSpan]$Timeout = [TimeSpan]::FromMinutes(10)
    )

    $mutex = [System.Threading.Mutex]::new($false, (Get-QfmBootstrapMutexName -CacheRoot $CacheRoot))
    try {
        $acquired = $mutex.WaitOne($Timeout)
    }
    catch [System.Threading.AbandonedMutexException] {
        $acquired = $true
    }
    if (-not $acquired) {
        $mutex.Dispose()
        throw 'Timed out waiting for the QFM pinned SDK bootstrap mutex.'
    }
    return $mutex
}

function Exit-QfmBootstrapMutex {
    param([Parameter(Mandatory)][System.Threading.Mutex]$Mutex)

    try {
        $Mutex.ReleaseMutex()
    }
    finally {
        $Mutex.Dispose()
    }
}

function Invoke-QfmPinnedSdkBootstrap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [string]$CacheRoot = (Get-QfmDefaultCacheRoot)
    )

    $RepositoryRoot = Resolve-QfmFullPath -Path $RepositoryRoot -Label 'repository root'
    $CacheRoot = Resolve-QfmFullPath -Path $CacheRoot -Label 'SDK cache root'
    if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
        throw 'The QFM repository root does not exist.'
    }

    $mutex = Enter-QfmBootstrapMutex -CacheRoot $CacheRoot
    try {
        $pin = Get-QfmPinnedSdk -RepositoryRoot $RepositoryRoot
        $source = Get-QfmCleanSourceIdentity -RepositoryRoot $RepositoryRoot
        $sdk = Find-QfmPinnedSdk -Pin $pin -RepositoryRoot $RepositoryRoot -CacheRoot $CacheRoot
        if ($null -eq $sdk) {
            $sdk = Install-QfmPinnedSdk -Pin $pin -RepositoryRoot $RepositoryRoot -CacheRoot $CacheRoot
        }
        $build = Invoke-QfmContentAddressedCliBuild -Pin $pin -Source $source -Sdk $sdk -RepositoryRoot $RepositoryRoot -CacheRoot $CacheRoot

        return [pscustomobject]@{
            schema                = $script:QfmBootstrapContract.Schema
            result                = $build.Result
            sdk_version           = $sdk.Version
            runtime_version       = $sdk.Runtime
            source_commit         = $source.Commit
            source_tree           = $source.Tree
            artifact_sha256       = $build.ArtifactSha256
            artifact_bytes        = $build.ArtifactBytes
            output_tree_sha256    = $build.OutputTreeSha256
            receipt_relative_path = $build.ReceiptRelativePath
        }
    }
    finally {
        Exit-QfmBootstrapMutex -Mutex $mutex
    }
}

Export-ModuleMember -Function Invoke-QfmPinnedSdkBootstrap
