[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+-alpha\.[1-9]\d*$')]
    [string]$ReleaseTag,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+-alpha\.[1-9]\d*$')]
    [string]$ReleaseVersion,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.[1-9]\d*$')]
    [string]$WindowsPackageVersion,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$SourceRevision,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$SourceTree,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^MesmerPrism\.QuestIonAbleFileManager\.Labs$')]
    [string]$PackageIdentity
)

$ErrorActionPreference = 'Stop'
$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
$setupName = 'QuestIonAbleFileManager-Labs-Setup.exe'
$setupPath = Join-Path $releaseRoot $setupName
$validationPath = Join-Path $releaseRoot 'release-validation.json'
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw 'The primary Labs Windows Setup artifact is missing.'
}
if (-not (Test-Path -LiteralPath $validationPath -PathType Leaf)) {
    throw 'The Labs release-validation evidence is missing.'
}
if ($ReleaseVersion -cne $ReleaseTag.Substring(1)) {
    throw 'The Labs release tag and version differ.'
}
$versionMatch = [regex]::Match(
    $ReleaseVersion,
    '^(?<base>\d+\.\d+\.\d+)-alpha\.(?<alpha>[1-9]\d*)$'
)
$expectedWindowsPackageVersion = (
    "$($versionMatch.Groups['base'].Value)." +
    $versionMatch.Groups['alpha'].Value
)
if (-not $versionMatch.Success -or
    $WindowsPackageVersion -cne $expectedWindowsPackageVersion) {
    throw 'The alpha maturity and Windows package versions differ.'
}
try {
    $validationEvidence =
        Get-Content -Raw -LiteralPath $validationPath | ConvertFrom-Json
}
catch {
    throw 'The Labs release-validation evidence is not valid JSON.'
}
if ($validationEvidence.schema -cne
    'questionable-file-manager.release-validation.v2') {
    throw 'The Labs release-validation evidence schema is not recognized.'
}

$metadata = [ordered]@{
    schema = 'questionable-file-manager.owner-release.v2'
    product_channel = 'labs'
    maturity = 'alpha'
    distribution_track = 'github-prerelease'
    release = [ordered]@{
        tag = $ReleaseTag
        version = $ReleaseVersion
        windows_package_version = $WindowsPackageVersion
    }
    source = [ordered]@{
        revision = $SourceRevision
        tree = $SourceTree
    }
    installation = [ordered]@{
        package_identity = $PackageIdentity
    }
    primary_windows_setup = [ordered]@{
        name = $setupName
        sha256 = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = [long](Get-Item -LiteralPath $setupPath).Length
    }
    validation_evidence = [ordered]@{
        name = 'release-validation.json'
        schema = 'questionable-file-manager.release-validation.v2'
    }
}
$outputPath = Join-Path $releaseRoot 'questionable-file-manager-labs-owner-release.json'
$metadata | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $outputPath -Encoding utf8
$outputPath
