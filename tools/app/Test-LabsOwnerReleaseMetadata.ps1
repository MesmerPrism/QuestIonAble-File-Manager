[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MetadataPath,
    [Parameter(Mandatory = $true)]
    [string]$SetupPath,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedTag,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedWindowsPackageVersion,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedSourceRevision,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedSourceTree,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedPackageIdentity
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $MetadataPath -PathType Leaf)) {
    throw 'The Labs owner-release metadata asset is missing.'
}
if (-not (Test-Path -LiteralPath $SetupPath -PathType Leaf)) {
    throw 'The primary Labs Windows Setup artifact is missing.'
}

$metadata = Get-Content -Raw -LiteralPath $MetadataPath | ConvertFrom-Json
$versionMatch = [regex]::Match(
    $ExpectedVersion,
    '^(?<base>\d+\.\d+\.\d+)-alpha\.(?<alpha>[1-9]\d*)$'
)
$expectedDerivedWindowsVersion = (
    "$($versionMatch.Groups['base'].Value)." +
    $versionMatch.Groups['alpha'].Value
)
if (-not $versionMatch.Success -or
    $ExpectedVersion -cne $ExpectedTag.Substring(1) -or
    $ExpectedWindowsPackageVersion -cne $expectedDerivedWindowsVersion) {
    throw 'The expected alpha maturity tag and package versions are inconsistent.'
}
$requiredTopLevel = @(
    'schema', 'product_channel', 'maturity', 'distribution_track', 'release', 'source', 'installation',
    'primary_windows_setup', 'validation_evidence'
)
if (@($metadata.PSObject.Properties.Name).Count -ne $requiredTopLevel.Count -or
    @(Compare-Object $requiredTopLevel @($metadata.PSObject.Properties.Name)).Count -ne 0) {
    throw 'The Labs owner-release metadata has an incomplete or expanded top-level shape.'
}
$setup = Get-Item -LiteralPath $SetupPath
$setupHash = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($metadata.schema -cne 'questionable-file-manager.owner-release.v2' -or
    $metadata.product_channel -cne 'labs' -or
    $metadata.maturity -cne 'alpha' -or
    $metadata.distribution_track -cne 'github-prerelease' -or
    $metadata.release.tag -cne $ExpectedTag -or
    $metadata.release.version -cne $ExpectedVersion -or
    $metadata.release.windows_package_version -cne $ExpectedWindowsPackageVersion -or
    $metadata.source.revision -cne $ExpectedSourceRevision -or
    $metadata.source.tree -cne $ExpectedSourceTree -or
    $metadata.installation.package_identity -cne $ExpectedPackageIdentity -or
    $metadata.primary_windows_setup.name -cne 'QuestIonAbleFileManager-Labs-Setup.exe' -or
    $metadata.primary_windows_setup.sha256 -cne $setupHash -or
    [long]$metadata.primary_windows_setup.bytes -ne $setup.Length -or
    $metadata.validation_evidence.name -cne 'release-validation.json' -or
    $metadata.validation_evidence.schema -cne 'questionable-file-manager.release-validation.v2') {
    throw 'The Labs owner-release metadata does not bind the exact owner release.'
}

$expectedNestedShapes = @{
    release = @('tag', 'version', 'windows_package_version')
    source = @('revision', 'tree')
    installation = @('package_identity')
    primary_windows_setup = @('name', 'sha256', 'bytes')
    validation_evidence = @('name', 'schema')
}
foreach ($entry in $expectedNestedShapes.GetEnumerator()) {
    $actual = @($metadata.($entry.Key).PSObject.Properties.Name)
    if ($actual.Count -ne $entry.Value.Count -or
        @(Compare-Object $entry.Value $actual).Count -ne 0) {
        throw "The Labs owner-release metadata has an incomplete or expanded $($entry.Key) shape."
    }
}
$metadata
