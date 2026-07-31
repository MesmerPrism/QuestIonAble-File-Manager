[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MainApkPath,

    [Parameter(Mandatory = $true)]
    [string]$SetupHelperApkPath,

    [Parameter(Mandatory = $true)]
    [string]$RustyKioskLicensePath,

    [ValidateSet('debug', 'release')]
    [string]$BuildType = 'debug',

    [string]$SourceUrl = 'https://github.com/MesmerPrism/Rusty-Kiosk',
    [string]$SourceRevision = 'working-tree',
    [string]$Version = '0.0.0',
    [ValidateSet('stable', 'labs')]
    [string]$ProductChannel = 'stable',
    [ValidateSet('alpha', 'beta', 'rc', 'released')]
    [string]$Maturity = 'released',
    [ValidateSet('github-release', 'github-prerelease')]
    [string]$DistributionTrack = 'github-release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\kiosk-bundle')
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Rusty Kiosk staging output must stay under $artifactsRoot."
}

$inputs = @($MainApkPath, $SetupHelperApkPath, $RustyKioskLicensePath) |
    ForEach-Object { [IO.Path]::GetFullPath($_) }
foreach ($path in $inputs) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Rusty Kiosk bundle input was not found: $path"
    }
}
if (@($inputs[0..1] | Where-Object { [IO.Path]::GetExtension($_) -ne '.apk' }).Count -ne 0) {
    throw 'Both Rusty Kiosk application inputs must be APK files.'
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$mainOutput = Join-Path $OutputDirectory 'rusty-kiosk.apk'
$helperOutput = Join-Path $OutputDirectory 'rusty-kiosk-setup-helper.apk'
$licenseOutput = Join-Path $OutputDirectory 'RUSTY-KIOSK-LICENSE.txt'
Copy-Item -LiteralPath $inputs[0] -Destination $mainOutput
Copy-Item -LiteralPath $inputs[1] -Destination $helperOutput
Copy-Item -LiteralPath $inputs[2] -Destination $licenseOutput
$sourceOutput = Join-Path $OutputDirectory 'RUSTY-KIOSK-SOURCE.txt'
Set-Content -LiteralPath $sourceOutput -Encoding utf8 -Value @"
Rusty Kiosk source: $SourceUrl
Source revision: $SourceRevision
Version: $Version
Product channel: $ProductChannel
Maturity: $Maturity
Distribution track: $DistributionTrack
License: GNU Affero General Public License v3.0 or later (see RUSTY-KIOSK-LICENSE.txt)
"@

$manifest = [ordered]@{
    schema = 'meta.quest.file_manager.rusty_kiosk_bundle.v2'
    build_type = $BuildType
    version = $Version
    product_channel = $ProductChannel
    maturity = $Maturity
    distribution_track = $DistributionTrack
    source_url = $SourceUrl
    source_revision = $SourceRevision
    staged_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
    files = @(
        [ordered]@{
            name = 'rusty-kiosk.apk'
            sha256 = (Get-FileHash -LiteralPath $mainOutput -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = (Get-Item -LiteralPath $mainOutput).Length
        },
        [ordered]@{
            name = 'rusty-kiosk-setup-helper.apk'
            sha256 = (Get-FileHash -LiteralPath $helperOutput -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = (Get-Item -LiteralPath $helperOutput).Length
        },
        [ordered]@{
            name = 'RUSTY-KIOSK-LICENSE.txt'
            sha256 = (Get-FileHash -LiteralPath $licenseOutput -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = (Get-Item -LiteralPath $licenseOutput).Length
        },
        [ordered]@{
            name = 'RUSTY-KIOSK-SOURCE.txt'
            sha256 = (Get-FileHash -LiteralPath $sourceOutput -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = (Get-Item -LiteralPath $sourceOutput).Length
        }
    )
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'bundle-manifest.json') -Encoding utf8
Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | Select-Object Name, Length, FullName
