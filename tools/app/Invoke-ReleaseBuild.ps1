[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$PackageCertificatePath,

    [Parameter(Mandatory = $true)]
    [string]$PackageCertificatePassword,

    [string]$SetupCertificatePath,
    [string]$SetupCertificatePassword,
    [string]$Publisher = 'CN=MesmerPrism',
    [string]$PackageTimestampUrl = 'http://timestamp.digicert.com',
    [string]$SetupTimestampUrl = 'http://timestamp.digicert.com',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\release'),
    [string]$KioskBundleDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\kiosk-bundle'),

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-alpha\.[1-9]\d*)?$')]
    [string]$ExpectedKioskVersion,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedKioskSourceRevision,

    [string]$ApkSignerPath,
    [string]$FleetInstallerLifecycleInputPath,
    [ValidateSet('stable', 'labs')]
    [string]$ProductChannel = 'stable',
    [ValidateSet('alpha', 'beta', 'rc', 'released')]
    [string]$Maturity = 'released',
    [ValidateSet('github-release', 'github-prerelease')]
    [string]$DistributionTrack = 'github-release',
    [string]$ReleaseTag,
    [int]$AlphaNumber,
    [string]$ExpectedKioskTag,
    [string]$ExpectedKioskReleaseUrl,
    [string]$ExpectedKioskSourceTree,
    [long]$ExpectedKioskVersionCode,
    [string]$ExpectedKioskMainPackageName,
    [string]$ExpectedKioskHelperPackageName,
    [string]$ExpectedKioskOwnerMetadataPath,
    [string]$ExpectedKioskSignerSha256,
    [string]$ExpectedKioskManifestSha256,
    [switch]$SkipBuildAndTest
)

$ErrorActionPreference = 'Stop'
$isLabs = $ProductChannel -eq 'labs'
if ($isLabs) {
    if ($AlphaNumber -lt 1 -or
        $Maturity -cne 'alpha' -or
        $DistributionTrack -cne 'github-prerelease' -or
        $ReleaseTag -cne "v$Version-alpha.$AlphaNumber") {
        throw 'Labs alpha-maturity releases require the exact canonical vX.Y.Z-alpha.N tag and github-prerelease distribution track.'
    }
}
elseif ($Maturity -cne 'released' -or $DistributionTrack -cne 'github-release' -or
    ($ReleaseTag -and $ReleaseTag -cne "v$Version")) {
    throw 'Stable release tag does not match the numeric version.'
}
$packageVersion = if ($isLabs) { "$Version.$AlphaNumber" } else { "$Version.0" }
$assetStem = if ($isLabs) { 'QuestIonAbleFileManager-Labs' } else { 'QuestIonAbleFileManager' }
$packageIdentity = if ($isLabs) {
    'MesmerPrism.QuestIonAbleFileManager.Labs'
} else {
    'MesmerPrism.MetaQuestFileManager'
}
$displayName = if ($isLabs) {
    'QuestIonAble File Manager Labs'
} else {
    'QuestIonAble File Manager'
}
$packageUri = if ($isLabs) {
    "https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/download/$ReleaseTag/$assetStem-win-x64.msix"
} else {
    'https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/latest/download/QuestIonAbleFileManager-win-x64.msix'
}
$appInstallerUri = if ($isLabs) {
    "https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/download/$ReleaseTag/$assetStem.appinstaller"
} else {
    'https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/latest/download/QuestIonAbleFileManager.appinstaller'
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
& (Join-Path $repoRoot 'tools\Test-FleetInstallerReleaseConfiguration.ps1') `
    -RequireOfficialRelease `
    -ExpectedVersion $Version `
    -ExpectedTag $ReleaseTag `
    -ExpectedProductChannel $ProductChannel
if ($LASTEXITCODE -ne 0) {
    throw 'Fleet installer checked-in release configuration validation failed.'
}

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release output must stay under $artifactsRoot."
}
if (-not $SetupCertificatePath) { $SetupCertificatePath = $PackageCertificatePath }
if (-not $SetupCertificatePassword) { $SetupCertificatePassword = $PackageCertificatePassword }

$KioskBundleDirectory = [IO.Path]::GetFullPath($KioskBundleDirectory)
$requiredKioskFiles = @(
    'rusty-kiosk.apk',
    'rusty-kiosk-setup-helper.apk',
    'bundle-manifest.json',
    'RUSTY-KIOSK-LICENSE.txt',
    'RUSTY-KIOSK-SOURCE.txt'
)
foreach ($name in $requiredKioskFiles) {
    $path = Join-Path $KioskBundleDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The public Windows release requires the complete Rusty Kiosk bundle; missing $path"
    }
}
$kioskVerification = & (Join-Path $PSScriptRoot 'Test-RustyKioskReleaseBundle.ps1') `
    -BundleDirectory $KioskBundleDirectory `
    -ExpectedVersion $ExpectedKioskVersion `
    -ExpectedSourceRevision $ExpectedKioskSourceRevision `
    -ExpectedProductChannel $ProductChannel `
    -ExpectedMaturity $Maturity `
    -ExpectedDistributionTrack $DistributionTrack `
    -ExpectedTag $ExpectedKioskTag `
    -ExpectedReleaseUrl $ExpectedKioskReleaseUrl `
    -ExpectedSourceTree $ExpectedKioskSourceTree `
    -ExpectedVersionCode $ExpectedKioskVersionCode `
    -ExpectedMainPackageName $ExpectedKioskMainPackageName `
    -ExpectedHelperPackageName $ExpectedKioskHelperPackageName `
    -ExpectedOwnerMetadataPath $ExpectedKioskOwnerMetadataPath `
    -ExpectedSignerSha256 $ExpectedKioskSignerSha256 `
    -ExpectedManifestSha256 $ExpectedKioskManifestSha256 `
    -ApkSignerPath $ApkSignerPath
$defaultKioskBundle = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\kiosk-bundle'))
if (-not $KioskBundleDirectory.Equals($defaultKioskBundle, [StringComparison]::OrdinalIgnoreCase)) {
    if (Test-Path -LiteralPath $defaultKioskBundle) {
        Remove-Item -LiteralPath $defaultKioskBundle -Recurse -Force
    }
    New-Item -ItemType Directory -Path $defaultKioskBundle -Force | Out-Null
    Copy-Item -Path (Join-Path $KioskBundleDirectory '*') -Destination $defaultKioskBundle -Recurse -Force
    $KioskBundleDirectory = $defaultKioskBundle
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if (-not $SkipBuildAndTest) {
    & dotnet restore (Join-Path $repoRoot 'QuestIonAbleFileManager.slnx')
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    & dotnet build (Join-Path $repoRoot 'QuestIonAbleFileManager.slnx') --configuration Release --no-restore -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
    & dotnet test (Join-Path $repoRoot 'QuestIonAbleFileManager.slnx') --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
    & pwsh -NoProfile -File (Join-Path $repoRoot 'tools\Test-PublicBoundary.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Public-boundary validation failed.' }
}

$appPublish = Join-Path $repoRoot 'artifacts\portable-app'
$cliPublish = Join-Path $repoRoot 'artifacts\portable-cli'
$combined = Join-Path $repoRoot 'artifacts\portable-combined'
foreach ($directory in @($appPublish, $cliPublish, $combined)) {
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

& dotnet publish (Join-Path $repoRoot 'src\QuestIonAbleFileManager.App\QuestIonAbleFileManager.App.csproj') `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version --output $appPublish
if ($LASTEXITCODE -ne 0) { throw 'Portable app publish failed.' }
& dotnet publish (Join-Path $repoRoot 'src\QuestIonAbleFileManager.Cli\QuestIonAbleFileManager.Cli.csproj') `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -p:PublishTrimmed=false `
    -p:Version=$Version --output $cliPublish
if ($LASTEXITCODE -ne 0) { throw 'Portable CLI publish failed.' }

# Keep the former executable name as a migration alias for existing scripts.
Copy-Item -LiteralPath (Join-Path $cliPublish 'questionable-file-manager.exe') `
    -Destination (Join-Path $cliPublish 'meta-quest-file-manager.exe') -Force
$providerValidationDirectory = Join-Path $repoRoot 'artifacts\fleet-kiosk-v2-provider-release-validation'
& (Join-Path $repoRoot 'tools\Test-FleetKioskV2ProviderArtifact.ps1') `
    -OutputDirectory $providerValidationDirectory `
    -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'Fleet Kiosk v2 provider artifact validation failed.' }
Copy-Item -LiteralPath (
    Join-Path $providerValidationDirectory 'questionable-file-manager-kiosk-v2-provider.exe') `
    -Destination $cliPublish -Force
Copy-Item -LiteralPath (
    Join-Path $providerValidationDirectory 'questionable-file-manager-kiosk-v2-provider.receipt.json') `
    -Destination $cliPublish -Force
Copy-Item -LiteralPath (
    Join-Path $providerValidationDirectory 'questionable-file-manager-kiosk-v2-provider.exe') `
    -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (
    Join-Path $providerValidationDirectory 'questionable-file-manager-kiosk-v2-provider.receipt.json') `
    -Destination $OutputDirectory -Force
$awakeProviderValidationDirectory =
    Join-Path $repoRoot 'artifacts\fleet-awake-provider-release-validation'
& (Join-Path $repoRoot 'tools\Test-FleetAwakeProviderArtifact.ps1') `
    -OutputDirectory $awakeProviderValidationDirectory `
    -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'Fleet awake provider artifact validation failed.' }
Copy-Item -LiteralPath (
    Join-Path $awakeProviderValidationDirectory 'questionable-file-manager-awake-provider.exe') `
    -Destination $cliPublish -Force
Copy-Item -LiteralPath (
    Join-Path $awakeProviderValidationDirectory 'questionable-file-manager-awake-provider.receipt.json') `
    -Destination $cliPublish -Force
Copy-Item -LiteralPath (
    Join-Path $awakeProviderValidationDirectory 'questionable-file-manager-awake-provider.exe') `
    -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (
    Join-Path $awakeProviderValidationDirectory 'questionable-file-manager-awake-provider.receipt.json') `
    -Destination $OutputDirectory -Force
$connectivityProviderValidationDirectory =
    Join-Path $repoRoot 'artifacts\fleet-connectivity-provider-release-validation'
& (Join-Path $repoRoot 'tools\Test-FleetConnectivityProviderArtifact.ps1') `
    -OutputDirectory $connectivityProviderValidationDirectory `
    -Version $Version
if ($LASTEXITCODE -ne 0) { throw 'Fleet connectivity provider artifact validation failed.' }
Copy-Item -LiteralPath (
    Join-Path $connectivityProviderValidationDirectory 'questionable-file-manager-connectivity-provider.exe') `
    -Destination $cliPublish -Force
Copy-Item -LiteralPath (
    Join-Path $connectivityProviderValidationDirectory 'questionable-file-manager-connectivity-provider.receipt.json') `
    -Destination $cliPublish -Force
Copy-Item -LiteralPath (
    Join-Path $connectivityProviderValidationDirectory 'questionable-file-manager-connectivity-provider.exe') `
    -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (
    Join-Path $connectivityProviderValidationDirectory 'questionable-file-manager-connectivity-provider.receipt.json') `
    -Destination $OutputDirectory -Force
Copy-Item -Path (Join-Path $appPublish '*') -Destination $combined -Recurse -Force
Copy-Item -LiteralPath (Join-Path $cliPublish 'questionable-file-manager.exe') -Destination $combined -Force
Copy-Item -LiteralPath (Join-Path $cliPublish 'meta-quest-file-manager.exe') -Destination $combined -Force
Copy-Item -LiteralPath (
    Join-Path $cliPublish 'questionable-file-manager-kiosk-v2-provider.exe') `
    -Destination $combined -Force
Copy-Item -LiteralPath (
    Join-Path $cliPublish 'questionable-file-manager-kiosk-v2-provider.receipt.json') `
    -Destination $combined -Force
Copy-Item -LiteralPath (
    Join-Path $cliPublish 'questionable-file-manager-awake-provider.exe') `
    -Destination $combined -Force
Copy-Item -LiteralPath (
    Join-Path $cliPublish 'questionable-file-manager-awake-provider.receipt.json') `
    -Destination $combined -Force
Copy-Item -LiteralPath (
    Join-Path $cliPublish 'questionable-file-manager-connectivity-provider.exe') `
    -Destination $combined -Force
Copy-Item -LiteralPath (
    Join-Path $cliPublish 'questionable-file-manager-connectivity-provider.receipt.json') `
    -Destination $combined -Force
$portableName = if ($isLabs) { "$assetStem-win-x64.zip" } else { 'QuestIonAbleFileManager-win-x64.zip' }
$cliName = if ($isLabs) { 'questionable-file-manager-labs-cli-win-x64.zip' } else { 'questionable-file-manager-cli-win-x64.zip' }
Compress-Archive -Path (Join-Path $combined '*') -DestinationPath (Join-Path $OutputDirectory $portableName)
Compress-Archive -Path (Join-Path $cliPublish '*') -DestinationPath (Join-Path $OutputDirectory $cliName)

& (Join-Path $PSScriptRoot 'Build-App-Package.ps1') `
    -Version $Version `
    -ProductChannel $ProductChannel `
    -PackageVersion $packageVersion `
    -ReleaseTag $ReleaseTag `
    -OutputDirectory $OutputDirectory `
    -PackageName $packageIdentity `
    -DisplayName $displayName `
    -PackageFileName "$assetStem-win-x64.msix" `
    -AppInstallerFileName "$assetStem.appinstaller" `
    -CertificateFileName "$assetStem.cer" `
    -PackageUri $packageUri `
    -AppInstallerUri $appInstallerUri `
    -Publisher $Publisher `
    -CertificatePath $PackageCertificatePath `
    -CertificatePassword $PackageCertificatePassword `
    -TimestampUrl $PackageTimestampUrl
if ($LASTEXITCODE -ne 0) { throw 'MSIX package build failed.' }

& (Join-Path $PSScriptRoot 'Publish-GuidedSetup.ps1') `
    -Version $Version `
    -OutputDirectory $OutputDirectory `
    -FileName "$assetStem-Setup.exe" `
    -ProductChannel $ProductChannel `
    -Maturity $Maturity `
    -DistributionTrack $DistributionTrack `
    -ReleaseTag $ReleaseTag `
    -PackageIdentity $packageIdentity `
    -DisplayName $displayName `
    -AssetStem $assetStem `
    -CertificatePath $SetupCertificatePath `
    -CertificatePassword $SetupCertificatePassword `
    -TimestampUrl $SetupTimestampUrl
if ($LASTEXITCODE -ne 0) { throw 'Guided setup publish failed.' }

if (-not [string]::IsNullOrWhiteSpace(
        $FleetInstallerLifecycleInputPath)) {
    & (Join-Path $repoRoot `
        'tools\Test-FleetInstallerHandoffLifecycle.ps1') `
        -InputPath $FleetInstallerLifecycleInputPath `
        -QfmSetupExecutablePath (
            Join-Path $OutputDirectory `
                "$assetStem-Setup.exe")
    if ($LASTEXITCODE -ne 0) {
        throw 'Fleet installer handoff lifecycle validation failed.'
    }
}

# Releases keep byte-identical former-name aliases so 0.3.x App Installer
# subscriptions and pinned automation download URLs migrate without breaking.
$compatibilityAliases = if ($isLabs) { [ordered]@{} } else { [ordered]@{
    'MetaQuestFileManager-Setup.exe' = 'QuestIonAbleFileManager-Setup.exe'
    'MetaQuestFileManager-win-x64.msix' = 'QuestIonAbleFileManager-win-x64.msix'
    'MetaQuestFileManager.appinstaller' = 'QuestIonAbleFileManager.appinstaller'
    'MetaQuestFileManager.cer' = 'QuestIonAbleFileManager.cer'
    'MetaQuestFileManager-win-x64.zip' = 'QuestIonAbleFileManager-win-x64.zip'
    'meta-quest-file-manager-cli-win-x64.zip' = 'questionable-file-manager-cli-win-x64.zip'
} }
foreach ($entry in $compatibilityAliases.GetEnumerator()) {
    Copy-Item -LiteralPath (Join-Path $OutputDirectory $entry.Value) `
        -Destination (Join-Path $OutputDirectory $entry.Key) -Force
}

& (Join-Path $PSScriptRoot 'Test-BrandAssets.ps1') -Executable @(
    (Join-Path $appPublish 'QuestIonAbleFileManager.exe'),
    (Join-Path $cliPublish 'questionable-file-manager.exe'),
    (Join-Path $providerValidationDirectory 'questionable-file-manager-kiosk-v2-provider.exe'),
    (Join-Path $awakeProviderValidationDirectory 'questionable-file-manager-awake-provider.exe'),
    (Join-Path $connectivityProviderValidationDirectory 'questionable-file-manager-connectivity-provider.exe'),
    (Join-Path $OutputDirectory "$assetStem-Setup.exe")
)
if ($LASTEXITCODE -ne 0) { throw 'Brand asset validation failed.' }

& (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') `
    -ReleaseDirectory $OutputDirectory `
    -ExpectedPublisher $Publisher `
    -ExpectedPackageName $packageIdentity `
    -ProductChannel $ProductChannel `
    -Maturity $Maturity `
    -DistributionTrack $DistributionTrack `
    -ReleaseTag $ReleaseTag `
    -KioskBundleManifestPath (Join-Path $KioskBundleDirectory 'bundle-manifest.json') `
    -AllowSelfIssuedTrustFailure
if ($LASTEXITCODE -ne 0) { throw 'Release asset validation failed.' }

if ($isLabs) {
    $sourceRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
    $sourceTree = (& git -C $repoRoot rev-parse 'HEAD^{tree}').Trim()
    if ($LASTEXITCODE -ne 0 -or
        $sourceRevision -notmatch '^[0-9a-f]{40}$' -or
        $sourceTree -notmatch '^[0-9a-f]{40}$') {
        throw 'Could not resolve the exact QFM Labs source revision and tree.'
    }
    $metadataPath = & (Join-Path $PSScriptRoot 'New-LabsOwnerReleaseMetadata.ps1') `
        -ReleaseDirectory $OutputDirectory `
        -ReleaseTag $ReleaseTag `
        -ReleaseVersion "$Version-alpha.$AlphaNumber" `
        -WindowsPackageVersion $packageVersion `
        -SourceRevision $sourceRevision `
        -SourceTree $sourceTree `
        -PackageIdentity $packageIdentity
    & (Join-Path $PSScriptRoot 'Test-LabsOwnerReleaseMetadata.ps1') `
        -MetadataPath $metadataPath `
        -SetupPath (Join-Path $OutputDirectory "$assetStem-Setup.exe") `
        -ExpectedTag $ReleaseTag `
        -ExpectedVersion "$Version-alpha.$AlphaNumber" `
        -ExpectedWindowsPackageVersion $packageVersion `
        -ExpectedSourceRevision $sourceRevision `
        -ExpectedSourceTree $sourceTree `
        -ExpectedPackageIdentity $packageIdentity | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Labs owner-release metadata validation failed.'
    }
}

Get-ChildItem -LiteralPath $OutputDirectory -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Sort-Object Name |
    ForEach-Object { '{0} *{1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8

Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | Select-Object Name, Length, FullName
