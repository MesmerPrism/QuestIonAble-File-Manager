[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')]
    [string]$RuntimeIdentifier,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ConnectorPlainPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse/link paths are not supported for packaging.' }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
}

function Get-ConnectorFileRecords([string]$Root) {
    Assert-ConnectorPlainPath $Root
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    $records = [Collections.Generic.List[object]]::new()
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse/link entries are not supported for packaging.' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
            $relative = [IO.Path]::GetRelativePath($Root,$item.FullName).Replace('\','/')
            if ($relative -notmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -or $relative.Split('/') -contains '..') { throw 'Unsafe package file name.' }
            $records.Add([ordered]@{ path = $relative; sizeBytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
        }
    }
    $records.Sort([Comparison[object]]{ param($left,$right) [StringComparer]::Ordinal.Compare($left.path,$right.path) })
    $records
}

function Get-ConnectorPublishArguments(
    [string]$RepositoryRoot, [string]$OutputRoot,
    [ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$RuntimeIdentifier
) {
    @('publish',(Join-Path $RepositoryRoot 'src/QuestIonAbleFileManager.BrowserHost/QuestIonAbleFileManager.BrowserHost.csproj'),
      '--configuration','Release','--framework','net10.0','--runtime',$RuntimeIdentifier,'--self-contained','false',
      '--artifacts-path',(Join-Path $OutputRoot '.build'),'--output',(Join-Path $OutputRoot 'payload'),
      '-p:UseAppHost=false','-p:PublishTrimmed=false','-p:PublishSingleFile=false','-p:PublishAot=false','-p:PublishReadyToRun=false',
      '-p:DebugType=None','-p:DebugSymbols=false','-p:CompressionEnabled=false','-p:IsTransformWebConfigDisabled=true',
      '-p:ContinuousIntegrationBuild=true')
}

function New-ConnectorOutput([string]$OutputDirectory,[string]$RepositoryRoot) {
    if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'OutputDirectory must be an explicit absolute path.' }
    if ($OutputDirectory -match '[\x00-\x1f\x7f*?\[\]]' -or ($IsWindows -and $OutputDirectory.StartsWith('\\'))) { throw 'Output directory name must be a plain local filesystem path.' }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must not exist; existing output is never overwritten.' }
    $parent = [IO.Path]::GetDirectoryName($output)
    if (-not $parent -or -not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'The explicit output parent must already exist.' }
    Assert-ConnectorPlainPath $parent
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $repoPrefix = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($output.StartsWith($repoPrefix,$comparison)) {
        $relative = [IO.Path]::GetRelativePath($RepositoryRoot,$output).Replace('\','/')
        if ($relative -notmatch '^(local|artifacts)/[^/].*') { throw 'Repository output must be below ignored local/ or artifacts/.' }
    }
    New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
    # Every generated file is create-new. Failures remain visibly incomplete;
    # this script never recursively deletes or overwrites an output tree.
    $marker = [IO.File]::Open((Join-Path $output '.incomplete'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $marker.Dispose()
    return $output
}

function Assert-ConnectorPayload([string]$PayloadRoot,[string]$SourceRoot,
    [ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$RuntimeIdentifier = 'win-x64') {
    $hostSource = Join-Path $SourceRoot 'src/QuestIonAbleFileManager.BrowserHost'
    $static = @(Get-ConnectorFileRecords (Join-Path $hostSource 'wwwroot') | Where-Object { $_.path -cne 'vendor/.gitattributes' })
    $security = [IO.File]::ReadAllText((Join-Path $hostSource 'BrowserSecurity.cs'))
    $pathsBlock = [regex]::Match($security,'(?s)StaticPaths = new\(StringComparer.Ordinal\)\s*\{(?<paths>.*?)\};')
    $shell = [IO.File]::ReadAllText((Join-Path $hostSource 'BrowserShellAssets.cs'))
    $shellBlock = [regex]::Match($shell,'(?s)Array.AsReadOnly\(new\[\]\s*\{(?<paths>.*?)\}\)')
    if (-not $pathsBlock.Success -or -not $shellBlock.Success) { throw 'Source static/shell manifest cannot be read.' }
    $allowed = @([regex]::Matches($pathsBlock.Groups['paths'].Value,'"(/[^"\r\n]*)"') | ForEach-Object { $_.Groups[1].Value.TrimStart('/') })
    $required = @([regex]::Matches($shellBlock.Groups['paths'].Value,'"(/[^"\r\n]+)"') | ForEach-Object { $_.Groups[1].Value.TrimStart('/') }) +
        @('sw.js','vendor/TANGO_NOTICES.txt','vendor/tango-packages.json','vendor/NOBLE_HASHES_NOTICES.txt','vendor/noble-hashes-packages.json')
    foreach ($file in $static) { if ($allowed -cnotcontains $file.path) { throw 'Unexpected source static file outside the public allowlist.' } }
    foreach ($name in $required) { if ($static.path -cnotcontains $name) { throw "Required source shell/vendor file missing: $name" } }
    $worker = [IO.File]::ReadAllText((Join-Path $hostSource 'wwwroot/sw.js'))
    if ([regex]::Matches($worker,[regex]::Escape('/*QFM_SHELL_MANIFEST*/ null')).Count -ne 1) { throw 'Source worker shell manifest marker is invalid.' }
    $files = @(Get-ConnectorFileRecords $PayloadRoot)
    $roots = @('LICENSE','questionable-file-manager-browser.dll','QuestIonAbleFileManager.Core.dll',
        'questionable-file-manager-browser.deps.json','questionable-file-manager-browser.runtimeconfig.json')
    # The Web SDK can produce endpoint metadata even though this host uses
    # UseStaticFiles. It is not needed and is deliberately not archived.
    $optional = @('questionable-file-manager-browser.staticwebassets.endpoints.json','wwwroot/vendor/.gitattributes')
    $expected = @($roots) + @($static | ForEach-Object { 'wwwroot/' + $_.path })
    foreach ($file in $files) { if ($expected -cnotcontains $file.path -and $optional -cnotcontains $file.path) { throw "Unexpected published file: $($file.path)" } }
    foreach ($name in $expected) { if ($files.path -cnotcontains $name) { throw "Published file set missing: $name" } }
    $license = (Get-FileHash -LiteralPath (Join-Path $SourceRoot 'LICENSE') -Algorithm SHA256).Hash.ToLowerInvariant()
    if (($files | Where-Object path -CEQ 'LICENSE').sha256 -cne $license) { throw 'Published LICENSE bytes differ from repository license.' }
    foreach ($file in $static) {
        $actual = $files | Where-Object path -CEQ ('wwwroot/' + $file.path)
        if ($actual.sha256 -cne $file.sha256 -or $actual.sizeBytes -ne $file.sizeBytes) { throw "Published static bytes differ: $($file.path)" }
    }
    $runtime = [IO.File]::ReadAllText((Join-Path $PayloadRoot 'questionable-file-manager-browser.runtimeconfig.json')) | ConvertFrom-Json -AsHashtable
    $options = $runtime.runtimeOptions
    if ($options.tfm -cne 'net10.0' -or $options.ContainsKey('includedFrameworks') -or -not $options.ContainsKey('frameworks') -or
        $options.frameworks.Count -ne 2 -or @($options.frameworks.name | Sort-Object) -join '|' -cne 'Microsoft.AspNetCore.App|Microsoft.NETCore.App' -or
        @($options.frameworks | Where-Object { $_.version -notmatch '^10\.0\.\d+$' }).Count -ne 0) { throw 'Published runtime is not the required .NET 10 framework-dependent ASP.NET Core deployment.' }
    $dependencies = [IO.File]::ReadAllText((Join-Path $PayloadRoot 'questionable-file-manager-browser.deps.json')) | ConvertFrom-Json -AsHashtable
    if ($dependencies.runtimeTarget.name -cne ".NETCoreApp,Version=v10.0/$RuntimeIdentifier") { throw 'Published runtime target does not match the requested RID.' }
    if ($dependencies.libraries.Count -ne 2 -or @($dependencies.libraries.Keys | Where-Object { $_ -cmatch '^questionable-file-manager-browser/[^/]+$' -or $_ -cmatch '^QuestIonAbleFileManager.Core/[^/]+$' }).Count -ne 2 -or
        @($dependencies.libraries.Values | Where-Object { $_.type -cne 'project' }).Count -ne 0) { throw 'Published dependencies exceed the reviewed two-project connector.' }
    $files | Where-Object { $expected -ccontains $_.path }
}

function Get-ConnectorProvenance([string]$RepositoryRoot) {
    $revision = & git -C $RepositoryRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40}$') { throw 'Source revision unavailable.' }
    $tree = & git -C $RepositoryRoot rev-parse 'HEAD^{tree}'
    if ($LASTEXITCODE -ne 0 -or $tree -notmatch '^[a-f0-9]{40}$') { throw 'Source tree unavailable.' }
    $status = @(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Source worktree state unavailable.' }
    $inputScopes = @('global.json','LICENSE','Directory.Build.*','NuGet.Config','nuget.config',
        'src/QuestIonAbleFileManager.BrowserHost','src/QuestIonAbleFileManager.Core',
        'tools/app/Publish-BrowserConnector.ps1','tools/browser-usb','tools/browser-download')
    $paths = @(& git -C $RepositoryRoot -c core.quotepath=false ls-files --cached --others --exclude-standard -- @inputScopes)
    if ($LASTEXITCODE -ne 0) { throw 'Source input inventory unavailable.' }
    $uniquePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $paths) { $null = $uniquePaths.Add($path) }
    $orderedPaths = [string[]]@($uniquePaths); [Array]::Sort($orderedPaths,[StringComparer]::Ordinal)
    $inputs = @($orderedPaths | ForEach-Object {
        if ($_ -notmatch '^[A-Za-z0-9_./-]+$' -or $_.Split('/') -contains '..') { throw 'Unsafe source inventory path.' }
        $path = Join-Path $RepositoryRoot $_
        Assert-ConnectorPlainPath $path
        $file = Get-Item -LiteralPath $path
        [ordered]@{ path = $_; sizeBytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    return [ordered]@{ revision = $revision; tree = $tree; worktreeDirty = $status.Count -gt 0; inputFiles = $inputs }
}

function New-ConnectorArchive([string]$PayloadRoot,[object[]]$Records,$Source,[string]$SdkVersion,[string]$RuntimeIdentifier,[string]$ArchivePath) {
    if (Test-Path -LiteralPath $ArchivePath) { throw 'Archive already exists; never overwrite.' }
    $manifest = [ordered]@{
        schema = 'questionable.file_manager.browser_connector_package.v1'
        purpose = 'optional-development-source-connector'
        runtimeIdentifier = $RuntimeIdentifier
        deployment = 'framework-dependent-dll'
        targetFramework = 'net10.0'
        requiredFrameworks = @('Microsoft.NETCore.App 10.0','Microsoft.AspNetCore.App 10.0')
        sdkVersion = $SdkVersion
        trimmed = $false
        signedRelease = $false
        source = $Source
        files = @($Records)
    }
    $utf8 = [Text.UTF8Encoding]::new($false)
    $manifestBytes = $utf8.GetBytes(($manifest | ConvertTo-Json -Depth 12).Replace("`r`n","`n") + "`n")
    $manifestHash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($manifestBytes))
    $sums = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($record in $Records) { $sums.Add($record.path,$record.sha256) }
    $sums.Add('connector-package.json',$manifestHash)
    $sumLines = @($sums.GetEnumerator() | ForEach-Object { "$($_.Value)  $($_.Key)" })
    $sumBytes = $utf8.GetBytes(($sumLines -join "`n") + "`n")
    $output = [IO.File]::Open($ArchivePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $zip = [IO.Compression.ZipArchive]::new($output,[IO.Compression.ZipArchiveMode]::Create,$false)
    try {
        foreach ($record in $Records) {
            $entry = $zip.CreateEntry($record.path,[IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $entry.ExternalAttributes = 0
            $input = [IO.File]::Open((Join-Path $PayloadRoot $record.path),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            $destination = $entry.Open()
            $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            try {
                $buffer = [byte[]]::new(65536); $length = 0L
                while (($read = $input.Read($buffer,0,$buffer.Length)) -gt 0) {
                    $length += $read
                    if ($length -gt $record.sizeBytes) { throw 'Payload changed while packaging.' }
                    $hash.AppendData($buffer,0,$read); $destination.Write($buffer,0,$read)
                }
                if ($length -ne $record.sizeBytes -or [Convert]::ToHexStringLower($hash.GetHashAndReset()) -cne $record.sha256) { throw 'Payload changed while packaging.' }
            } finally { $hash.Dispose(); $destination.Dispose(); $input.Dispose() }
        }
        foreach ($metadata in @(@('connector-package.json',$manifestBytes),@('SHA256SUMS.txt',$sumBytes))) {
            $entry = $zip.CreateEntry($metadata[0],[IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero); $entry.ExternalAttributes = 0
            $stream = $entry.Open()
            try { $stream.Write($metadata[1]) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    return [ordered]@{ path = [IO.Path]::GetFileName($ArchivePath); sizeBytes = (Get-Item -LiteralPath $ArchivePath).Length; sha256 = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() }
}

# Dot-sourcing exposes only the pure packaging helpers to the isolated fixture
# suite. The public script has no executable override or custom MSBuild hook.
if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $RuntimeIdentifier -or -not $OutputDirectory) { throw 'RuntimeIdentifier and an explicit new OutputDirectory are required.' }
if ($PSVersionTable.PSVersion -lt [version]'7.6') { throw 'PowerShell 7.6 or newer is required.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$expectedSdk = ([IO.File]::ReadAllText((Join-Path $repository 'global.json')) | ConvertFrom-Json).sdk.version
$dotnet = if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT $(if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }) }
    else { (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source }
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf) -or [IO.Path]::GetFileName($dotnet) -cnotin @('dotnet','dotnet.exe')) { throw 'A standard dotnet host from DOTNET_ROOT/PATH is required.' }
Push-Location $repository
try {
    $sdkVersion = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -cne $expectedSdk -or $sdkVersion -notmatch '^10\.0\.\d+$') { throw 'The exact repository-pinned .NET 10 SDK is required.' }
    $before = Get-ConnectorProvenance $repository
    $output = New-ConnectorOutput $OutputDirectory $repository
    $arguments = @(Get-ConnectorPublishArguments $repository $output $RuntimeIdentifier)
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Connector publish failed; output remains incomplete and must not be distributed.' }
    $records = @(Assert-ConnectorPayload (Join-Path $output 'payload') $repository $RuntimeIdentifier)
    $after = Get-ConnectorProvenance $repository
    if (($before | ConvertTo-Json -Depth 12 -Compress) -cne ($after | ConvertTo-Json -Depth 12 -Compress)) { throw 'Source changed during publish; output remains incomplete.' }
    $name = "questionable-file-manager-browser-$RuntimeIdentifier-framework-dependent-dev.zip"
    $archive = New-ConnectorArchive (Join-Path $output 'payload') $records $before $sdkVersion $RuntimeIdentifier (Join-Path $output $name)
    $sums = [IO.File]::Open((Join-Path $output 'SHA256SUMS.txt'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes("$($archive.sha256)  $name`n")
        $sums.Write($bytes)
    } finally { $sums.Dispose() }
    Remove-Item -LiteralPath (Join-Path $output '.incomplete')
    $archive | ConvertTo-Json
} finally { Pop-Location }
