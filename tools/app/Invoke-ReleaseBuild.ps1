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
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ExpectedKioskVersion,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedKioskSourceRevision,

    [string]$ApkSignerPath,
    [string]$FleetInstallerReleaseDescriptorUri,
    [string]$FleetInstallerDescriptorPublicKeySpkiBase64,
    [string]$FleetInstallerDescriptorSignerSpkiSha256,
    [string]$FleetInstallerSetupSignerCertificateSha256,
    [string]$FleetInstallerChannel,
    [string]$FleetInstallerStateRootRelativePath,
    [switch]$SkipBuildAndTest
)

$ErrorActionPreference = 'Stop'
$fleetInstallerBuildConfiguration = [ordered]@{
    FleetInstallerReleaseDescriptorUri = $FleetInstallerReleaseDescriptorUri
    FleetInstallerDescriptorPublicKeySpkiBase64 = $FleetInstallerDescriptorPublicKeySpkiBase64
    FleetInstallerDescriptorSignerSpkiSha256 = $FleetInstallerDescriptorSignerSpkiSha256
    FleetInstallerSetupSignerCertificateSha256 = $FleetInstallerSetupSignerCertificateSha256
    FleetInstallerChannel = $FleetInstallerChannel
    FleetInstallerStateRootRelativePath = $FleetInstallerStateRootRelativePath
}
$configuredFleetInstallerValues = @(
    $fleetInstallerBuildConfiguration.Values |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
).Count
if ($configuredFleetInstallerValues -notin @(0, $fleetInstallerBuildConfiguration.Count)) {
    throw 'Fleet installer release trust configuration is all-or-none.'
}
if ($configuredFleetInstallerValues -gt 0) {
    if ($FleetInstallerChannel -cnotin @('stable', 'preview', 'dev')) {
        throw 'The Fleet installer release channel must be stable, preview, or dev.'
    }
    $expectedDescriptorUri =
        "https://mesmerprism.com/Rusty-Fleet/metadata/$FleetInstallerChannel/release.json"
    if ($FleetInstallerReleaseDescriptorUri -cne $expectedDescriptorUri) {
        throw 'The Fleet installer descriptor must use the canonical MesmerPrism Pages metadata path.'
    }
    if ($FleetInstallerDescriptorSignerSpkiSha256 -notmatch '^[0-9a-f]{64}$' -or
        $FleetInstallerSetupSignerCertificateSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Fleet installer signer pins must be lowercase SHA-256 values.'
    }
    if ([IO.Path]::IsPathFullyQualified($FleetInstallerStateRootRelativePath)) {
        throw 'The Fleet installer state root must be a safe relative per-user path.'
    }
    $fleetStateSegments = @(
        $FleetInstallerStateRootRelativePath -split '[/\\]' |
            Where-Object { $_ -ne '' }
    )
    if ($fleetStateSegments.Count -lt 1 -or
        $fleetStateSegments.Count -gt 4 -or
        ($fleetStateSegments | Where-Object {
            $stem = ($_ -split '\.')[0]
            $_ -notmatch '^[A-Za-z0-9._-]{1,64}$' -or
            $_ -in @('.', '..') -or
            $_.EndsWith('.') -or
            $stem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$'
        }).Count -gt 0 -or
        ($fleetStateSegments -join '/') -cne
            $FleetInstallerStateRootRelativePath.Replace('\', '/')) {
        throw 'The Fleet installer state root must be a safe canonical relative path.'
    }
    try {
        $fleetDescriptorSpki =
            [Convert]::FromBase64String($FleetInstallerDescriptorPublicKeySpkiBase64)
        if ([Convert]::ToBase64String($fleetDescriptorSpki) -cne
            $FleetInstallerDescriptorPublicKeySpkiBase64) {
            throw 'The Fleet installer descriptor SPKI must use canonical base64.'
        }
        $fleetDescriptorSpkiPin = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($fleetDescriptorSpki)
        ).ToLowerInvariant()
        if ($fleetDescriptorSpkiPin -cne
            $FleetInstallerDescriptorSignerSpkiSha256) {
            throw 'The Fleet installer descriptor SPKI does not match its pin.'
        }
        $fleetDescriptorRsa = [Security.Cryptography.RSA]::Create()
        try {
            $fleetDescriptorBytesRead = 0
            $fleetDescriptorRsa.ImportSubjectPublicKeyInfo(
                $fleetDescriptorSpki,
                [ref]$fleetDescriptorBytesRead)
            if ($fleetDescriptorBytesRead -ne $fleetDescriptorSpki.Length) {
                throw 'The Fleet installer descriptor SPKI contains trailing data.'
            }
        }
        finally {
            $fleetDescriptorRsa.Dispose()
        }
    }
    catch {
        throw "The Fleet installer descriptor SPKI is invalid: $($_.Exception.Message)"
    }
}

# MSBuild imports process environment variables as properties. Clear ambient
# values when this invocation is inert, and restore the caller environment on
# every exit so only explicit release arguments can affect published binaries.
$priorFleetInstallerBuildEnvironment = @{}
foreach ($entry in $fleetInstallerBuildConfiguration.GetEnumerator()) {
    $priorFleetInstallerBuildEnvironment[$entry.Key] =
        [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
    $value = if ($configuredFleetInstallerValues -gt 0) { $entry.Value } else { $null }
    [Environment]::SetEnvironmentVariable($entry.Key, $value, 'Process')
}

try {
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
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
Compress-Archive -Path (Join-Path $combined '*') -DestinationPath (Join-Path $OutputDirectory 'QuestIonAbleFileManager-win-x64.zip')
Compress-Archive -Path (Join-Path $cliPublish '*') -DestinationPath (Join-Path $OutputDirectory 'questionable-file-manager-cli-win-x64.zip')

& (Join-Path $PSScriptRoot 'Build-App-Package.ps1') `
    -Version $Version `
    -OutputDirectory $OutputDirectory `
    -Publisher $Publisher `
    -CertificatePath $PackageCertificatePath `
    -CertificatePassword $PackageCertificatePassword `
    -TimestampUrl $PackageTimestampUrl
if ($LASTEXITCODE -ne 0) { throw 'MSIX package build failed.' }

& (Join-Path $PSScriptRoot 'Publish-GuidedSetup.ps1') `
    -Version $Version `
    -OutputDirectory $OutputDirectory `
    -CertificatePath $SetupCertificatePath `
    -CertificatePassword $SetupCertificatePassword `
    -TimestampUrl $SetupTimestampUrl
if ($LASTEXITCODE -ne 0) { throw 'Guided setup publish failed.' }

# Releases keep byte-identical former-name aliases so 0.3.x App Installer
# subscriptions and pinned automation download URLs migrate without breaking.
$compatibilityAliases = [ordered]@{
    'MetaQuestFileManager-Setup.exe' = 'QuestIonAbleFileManager-Setup.exe'
    'MetaQuestFileManager-win-x64.msix' = 'QuestIonAbleFileManager-win-x64.msix'
    'MetaQuestFileManager.appinstaller' = 'QuestIonAbleFileManager.appinstaller'
    'MetaQuestFileManager.cer' = 'QuestIonAbleFileManager.cer'
    'MetaQuestFileManager-win-x64.zip' = 'QuestIonAbleFileManager-win-x64.zip'
    'meta-quest-file-manager-cli-win-x64.zip' = 'questionable-file-manager-cli-win-x64.zip'
}
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
    (Join-Path $OutputDirectory 'QuestIonAbleFileManager-Setup.exe')
)
if ($LASTEXITCODE -ne 0) { throw 'Brand asset validation failed.' }

& (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') `
    -ReleaseDirectory $OutputDirectory `
    -ExpectedPublisher $Publisher `
    -KioskBundleManifestPath (Join-Path $KioskBundleDirectory 'bundle-manifest.json') `
    -AllowSelfIssuedTrustFailure
if ($LASTEXITCODE -ne 0) { throw 'Release asset validation failed.' }

Get-ChildItem -LiteralPath $OutputDirectory -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Sort-Object Name |
    ForEach-Object { '{0} *{1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8

Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | Select-Object Name, Length, FullName
}
finally {
    foreach ($entry in $priorFleetInstallerBuildEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable(
            $entry.Key,
            $entry.Value,
            'Process')
    }
}
