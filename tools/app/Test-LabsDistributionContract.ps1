[CmdletBinding()]
param(
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ExpectedStableWorkflowSha256 =
        '5382f772fc5adc57886fa29f84ea7d75e0a722a69efdc9dd2fe6a7ac5d7567b8'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$testRoot = Join-Path $artifactsRoot ("labs-contract-{0}" -f [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $testRoot 'bundle'
$fakeSigner = Join-Path $testRoot 'apksigner.cmd'
$tag = 'v2.3.4-alpha.5'
$version = $tag.Substring(1)
$revision = '0123456789abcdef0123456789abcdef01234567'
$sourceTree = '89abcdef0123456789abcdef0123456789abcdef'
$versionCode = 2030405
$signer = 'a' * 64
$mainPackage = 'io.github.mesmerprism.rustykiosk.labs'
$helperPackage = 'io.github.mesmerprism.rustykiosk.setuphelper.labs'
$kioskOwnerMetadata = Join-Path $bundle 'rusty-kiosk-labs-owner-release.json'
$releaseUrl = "https://github.com/MesmerPrism/Rusty-Kiosk/releases/tag/$tag"

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Name
    )
    try {
        & $Action
    }
    catch {
        return
    }
    throw "Labs contract negative case was accepted: $Name"
}

function Write-Manifest {
    param(
        [string]$ProductChannel = 'labs',
        [string]$ManifestTag = $tag,
        [string]$ManifestVersion = $version,
        [string]$ManifestMainPackage = $mainPackage,
        [string]$ManifestHelperPackage = $helperPackage,
        [string]$ManifestSigner = $signer,
        [long]$ManifestVersionCode = $versionCode,
        [string]$ManifestSourceTree = $sourceTree,
        [bool]$Prerelease = $true,
        [string]$IdentityMode = 'separate-coinstallable'
    )
    $files = @(
        'rusty-kiosk.apk',
        'rusty-kiosk-setup-helper.apk',
        'RUSTY-KIOSK-LICENSE.txt',
        'RUSTY-KIOSK-SOURCE.txt'
    )
    $manifest = [ordered]@{
        schema = 'meta.quest.file_manager.rusty_kiosk_bundle.v2'
        build_type = 'release'
        product_channel = $ProductChannel
        maturity = 'alpha'
        distribution_track = 'github-prerelease'
        prerelease = $Prerelease
        tag = $ManifestTag
        version = $ManifestVersion
        version_code = $ManifestVersionCode
        identity_mode = $IdentityMode
        exit_policy = 'uninstall-labs-without-changing-stable'
        source_url = 'https://github.com/MesmerPrism/Rusty-Kiosk'
        source_revision = $revision
        source_tree = $ManifestSourceTree
        signer_sha256 = $ManifestSigner
        files = @($files | ForEach-Object {
            $path = Join-Path $bundle $_
            $entry = [ordered]@{
                name = $_
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                bytes = (Get-Item -LiteralPath $path).Length
            }
            if ($_ -in @('rusty-kiosk.apk', 'rusty-kiosk-setup-helper.apk')) {
                $entry.package_name = switch ($_) {
                    'rusty-kiosk.apk' { $ManifestMainPackage }
                    'rusty-kiosk-setup-helper.apk' { $ManifestHelperPackage }
                }
                $entry.version_name = $ManifestVersion
                $entry.version_code = $ManifestVersionCode
            }
            $entry
        })
    }
    $manifest | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $bundle 'bundle-manifest.json') -Encoding utf8
}

function Write-KioskOwnerMetadata {
    $manifestPath = Join-Path $bundle 'bundle-manifest.json'
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $main = @($manifest.files | Where-Object name -CEQ 'rusty-kiosk.apk')[0]
    [ordered]@{
        schema = 'rusty.kiosk.labs_release_owner_metadata.v2'; repository = 'MesmerPrism/Rusty-Kiosk'; product = 'rusty-kiosk-labs'
        product_channel = 'labs'; maturity = 'alpha'; distribution_track = 'github-prerelease'; prerelease = $true
        tag = $tag; version = $version; source_revision = $revision; source_tree = $sourceTree
        installation_identity = $mainPackage
        coinstallable_lineage = [ordered]@{ identity_mode = 'separate-coinstallable'; package_name = $mainPackage; signer_sha256 = $signer; version_name = $version; version_code = $versionCode; exit_policy = 'uninstall-labs-without-changing-stable' }
        bundle_manifest = [ordered]@{ schema = 'meta.quest.file_manager.rusty_kiosk_bundle.v2'; name = 'bundle-manifest.json'; sha256 = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item $manifestPath).Length }
        primary_artifact = [ordered]@{ role = 'complete-product'; name = 'rusty-kiosk.apk'; sha256 = $main.sha256; bytes = [long]$main.bytes }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $kioskOwnerMetadata -Encoding utf8
}

function Invoke-Verification {
    Write-KioskOwnerMetadata
    $manifestHash =
        (Get-FileHash -LiteralPath (Join-Path $bundle 'bundle-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    & (Join-Path $PSScriptRoot 'Test-RustyKioskReleaseBundle.ps1') `
        -BundleDirectory $bundle `
        -ExpectedVersion $version `
        -ExpectedSourceRevision $revision `
        -ExpectedProductChannel labs `
        -ExpectedMaturity alpha `
        -ExpectedDistributionTrack github-prerelease `
        -ExpectedTag $tag `
        -ExpectedReleaseUrl $releaseUrl `
        -ExpectedSourceTree $sourceTree `
        -ExpectedVersionCode $versionCode `
        -ExpectedMainPackageName $mainPackage `
        -ExpectedHelperPackageName $helperPackage `
        -ExpectedOwnerMetadataPath $kioskOwnerMetadata `
        -ExpectedSignerSha256 $signer `
        -ExpectedManifestSha256 $manifestHash `
        -ApkSignerPath $fakeSigner | Out-Null
}

try {
    New-Item -ItemType Directory -Path $bundle -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $bundle 'rusty-kiosk.apk'), [byte[]](1, 2, 3, 4))
    [IO.File]::WriteAllBytes((Join-Path $bundle 'rusty-kiosk-setup-helper.apk'), [byte[]](5, 6, 7))
    Set-Content -LiteralPath (Join-Path $bundle 'RUSTY-KIOSK-LICENSE.txt') -Encoding utf8 -Value 'synthetic license'
    Set-Content -LiteralPath (Join-Path $bundle 'RUSTY-KIOSK-SOURCE.txt') -Encoding utf8 -Value @"
Rusty Kiosk source: https://github.com/MesmerPrism/Rusty-Kiosk
Source revision: $revision
Source tree: $sourceTree
Version: $version
Product channel: labs
Maturity: alpha
Distribution track: github-prerelease
Tag: $tag
"@
    Set-Content -LiteralPath $fakeSigner -Encoding ascii -Value "@echo V2 Signer: certificate SHA-256 digest: $signer 1>&2"

    Write-Manifest
    Invoke-Verification

    [IO.File]::WriteAllBytes((Join-Path $bundle 'rusty-kiosk-launcher.apk'), [byte[]](8, 9))
    $phantom = Get-Content -Raw (Join-Path $bundle 'bundle-manifest.json') | ConvertFrom-Json
    $phantom.files += [pscustomobject]@{ name = 'rusty-kiosk-launcher.apk'; package_name = 'io.github.mesmerprism.rustykiosk.launcher.labs'; version_name = $version; version_code = $versionCode; sha256 = (Get-FileHash (Join-Path $bundle 'rusty-kiosk-launcher.apk') -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = 2 }
    $phantom | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $bundle 'bundle-manifest.json') -Encoding utf8
    Assert-Rejected { Invoke-Verification } 'phantom launcher five-payload bundle'
    Remove-Item -LiteralPath (Join-Path $bundle 'rusty-kiosk-launcher.apk')
    Write-Manifest
    Write-KioskOwnerMetadata
    $damagedOwner = Get-Content -Raw $kioskOwnerMetadata | ConvertFrom-Json
    $damagedOwner.primary_artifact.sha256 = 'b' * 64
    $damagedOwner | ConvertTo-Json -Depth 8 | Set-Content $kioskOwnerMetadata -Encoding utf8
    Assert-Rejected {
        $manifestHash = (Get-FileHash (Join-Path $bundle 'bundle-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        & (Join-Path $PSScriptRoot 'Test-RustyKioskReleaseBundle.ps1') -BundleDirectory $bundle -ExpectedVersion $version -ExpectedSourceRevision $revision -ExpectedProductChannel labs -ExpectedMaturity alpha -ExpectedDistributionTrack github-prerelease -ExpectedTag $tag -ExpectedReleaseUrl $releaseUrl -ExpectedSourceTree $sourceTree -ExpectedVersionCode $versionCode -ExpectedMainPackageName $mainPackage -ExpectedHelperPackageName $helperPackage -ExpectedOwnerMetadataPath $kioskOwnerMetadata -ExpectedSignerSha256 $signer -ExpectedManifestSha256 $manifestHash -ApkSignerPath $fakeSigner | Out-Null
    } 'damaged Kiosk owner metadata'

    Write-Manifest -ProductChannel stable
    Assert-Rejected { Invoke-Verification } 'stable bundle substituted into Labs'
    Write-Manifest -ManifestTag 'v2.3.4-alpha.6'
    Assert-Rejected { Invoke-Verification } 'mismatched exact tag'
    Write-Manifest -ManifestVersion '2.3.4'
    Assert-Rejected { Invoke-Verification } 'stable version substituted into alpha'
    Assert-Rejected {
        & (Join-Path $PSScriptRoot 'Test-RustyKioskReleaseBundle.ps1') `
            -BundleDirectory $bundle -ExpectedVersion $version `
            -ExpectedSourceRevision $revision -ExpectedProductChannel labs `
            -ExpectedMaturity alpha -ExpectedDistributionTrack github-prerelease `
            -ExpectedTag $tag -ExpectedReleaseUrl 'https://github.com/MesmerPrism/Rusty-Kiosk/releases/latest/download/bundle-manifest.json' `
            -ExpectedSourceTree $sourceTree -ExpectedVersionCode $versionCode `
            -ExpectedMainPackageName $mainPackage -ExpectedHelperPackageName $helperPackage `
            -ExpectedOwnerMetadataPath $kioskOwnerMetadata `
            -ExpectedSignerSha256 $signer -ExpectedManifestSha256 ('0' * 64) `
            -ApkSignerPath $fakeSigner | Out-Null
    } 'latest reviewed release URL'
    Write-Manifest -ManifestMainPackage 'io.github.mesmerprism.rustykiosk.unreviewed'
    Assert-Rejected { Invoke-Verification } 'unreviewed Labs Android package substituted'
    Write-Manifest -ManifestVersionCode 2030406
    Assert-Rejected { Invoke-Verification } 'mismatched versionCode'
    Write-Manifest -Prerelease $false
    Assert-Rejected { Invoke-Verification } 'prerelease false'
    Write-Manifest -IdentityMode 'same-package-in-place'
    Assert-Rejected { Invoke-Verification } 'non-coinstallable identity mode'
    Write-Manifest -ManifestSourceTree ('d' * 40)
    Assert-Rejected { Invoke-Verification } 'mismatched source tree'
    Write-Manifest
    $apkIdentityManifest =
        Get-Content -Raw -LiteralPath (Join-Path $bundle 'bundle-manifest.json') |
        ConvertFrom-Json
    $apkIdentityManifest.files[0].version_name = '2.3.4-alpha.6'
    $apkIdentityManifest | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $bundle 'bundle-manifest.json') -Encoding utf8
    Assert-Rejected { Invoke-Verification } 'per-APK versionName substitution'
    Write-Manifest -ManifestSigner ('b' * 64)
    Assert-Rejected { Invoke-Verification } 'wrong signer'
    Write-Manifest
    $wrongHash = 'c' * 64
    Assert-Rejected {
        & (Join-Path $PSScriptRoot 'Test-RustyKioskReleaseBundle.ps1') `
            -BundleDirectory $bundle -ExpectedVersion $version `
            -ExpectedSourceRevision $revision -ExpectedProductChannel labs `
            -ExpectedMaturity alpha -ExpectedDistributionTrack github-prerelease `
            -ExpectedTag $tag -ExpectedReleaseUrl $releaseUrl `
            -ExpectedSourceTree $sourceTree -ExpectedVersionCode $versionCode `
            -ExpectedMainPackageName $mainPackage -ExpectedHelperPackageName $helperPackage `
            -ExpectedOwnerMetadataPath $kioskOwnerMetadata `
            -ExpectedSignerSha256 $signer -ExpectedManifestSha256 $wrongHash `
            -ApkSignerPath $fakeSigner | Out-Null
    } 'wrong manifest hash'

    foreach ($invalidTag in @(
        'v2.3.4-alpha.0',
        'v2.3.4-alpha',
        'v2.3.4-alpha.1.2',
        'v2.3.4-beta.1',
        'v2.3.4'
    )) {
        Write-Manifest -ManifestTag $invalidTag
        Assert-Rejected { Invoke-Verification } "invalid prerelease tag $invalidTag"
    }

    $ownerRelease = Join-Path $testRoot 'owner-release'
    New-Item -ItemType Directory -Path $ownerRelease -Force | Out-Null
    $ownerSetup = Join-Path $ownerRelease 'QuestIonAbleFileManager-Labs-Setup.exe'
    [IO.File]::WriteAllBytes($ownerSetup, [byte[]](11, 22, 33, 44, 55))
    $ownerValidation =
        Join-Path $ownerRelease 'release-validation.json'
    $ownerTag = 'v2.3.4-alpha.5'
    $ownerVersion = '2.3.4-alpha.5'
    $ownerWindowsVersion = '2.3.4.5'
    $ownerRevision = '1' * 40
    $ownerTree = '2' * 40
    $ownerIdentity = 'MesmerPrism.QuestIonAbleFileManager.Labs'
    function Write-OwnerValidationEvidence([string]$Content) {
        Set-Content -LiteralPath $ownerValidation -Encoding utf8 -Value $Content
    }
    function New-OwnerMetadataFixture(
        [string]$WindowsPackageVersion = $ownerWindowsVersion
    ) {
        & (Join-Path $PSScriptRoot 'New-LabsOwnerReleaseMetadata.ps1') `
            -ReleaseDirectory $ownerRelease -ReleaseTag $ownerTag `
            -ReleaseVersion $ownerVersion `
            -WindowsPackageVersion $WindowsPackageVersion `
            -SourceRevision $ownerRevision -SourceTree $ownerTree `
            -PackageIdentity $ownerIdentity
    }
    Write-OwnerValidationEvidence '{"schema":"wrong.release-validation.v1"}'
    Assert-Rejected {
        New-OwnerMetadataFixture
    } 'wrong release-validation evidence schema'
    Write-OwnerValidationEvidence '{"schema":'
    Assert-Rejected {
        New-OwnerMetadataFixture
    } 'invalid release-validation evidence JSON'
    Write-OwnerValidationEvidence (
        '{"schema":"questionable-file-manager.release-validation.v2"}'
    )
    Assert-Rejected {
        New-OwnerMetadataFixture -WindowsPackageVersion '2.3.4.6'
    } 'semantic and Windows package version mismatch'
    $ownerMetadata = New-OwnerMetadataFixture

    function Invoke-OwnerMetadataVerification {
        & (Join-Path $PSScriptRoot 'Test-LabsOwnerReleaseMetadata.ps1') `
            -MetadataPath $ownerMetadata -SetupPath $ownerSetup `
            -ExpectedTag $ownerTag -ExpectedVersion $ownerVersion `
            -ExpectedWindowsPackageVersion $ownerWindowsVersion `
            -ExpectedSourceRevision $ownerRevision -ExpectedSourceTree $ownerTree `
            -ExpectedPackageIdentity $ownerIdentity | Out-Null
    }
    function Set-OwnerMetadataMutation {
        param([scriptblock]$Mutation)
        $document = Get-Content -Raw -LiteralPath $ownerMetadata | ConvertFrom-Json
        & $Mutation $document
        $document | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath $ownerMetadata -Encoding utf8
    }
    function Reset-OwnerMetadata {
        $script:ownerMetadata = New-OwnerMetadataFixture
    }

    Invoke-OwnerMetadataVerification
    $metadataNegativeCases = @(
        @('wrong tag', { param($m) $m.release.tag = 'v2.3.4-alpha.6' }),
        @('wrong version', { param($m) $m.release.version = '2.3.4-alpha.6' }),
        @('wrong Windows package version', {
            param($m) $m.release.windows_package_version = '2.3.4.6'
        }),
        @('wrong source revision', { param($m) $m.source.revision = '3' * 40 }),
        @('wrong source tree', { param($m) $m.source.tree = '4' * 40 }),
        @('wrong product channel', { param($m) $m.product_channel = 'stable' }),
        @('wrong maturity', { param($m) $m.maturity = 'beta' }),
        @('channel-valued stable distribution track', { param($m) $m.distribution_track = 'stable' }),
        @('channel-valued labs distribution track', { param($m) $m.distribution_track = 'labs' }),
        @('wrong installation identity', {
            param($m) $m.installation.package_identity = 'MesmerPrism.MetaQuestFileManager'
        }),
        @('wrong Setup artifact name', {
            param($m) $m.primary_windows_setup.name = 'QuestIonAbleFileManager-Setup.exe'
        }),
        @('wrong Setup SHA-256', {
            param($m) $m.primary_windows_setup.sha256 = '5' * 64
        }),
        @('wrong Setup byte count', {
            param($m) $m.primary_windows_setup.bytes = 999
        }),
        @('expanded validation evidence', {
            param($m)
            $m.validation_evidence |
                Add-Member -NotePropertyName authority -NotePropertyValue 'synthetic'
        }),
        @('omitted validation evidence schema', {
            param($m) $m.validation_evidence.PSObject.Properties.Remove('schema')
        }),
        @('omitted metadata field', {
            param($m) $m.source.PSObject.Properties.Remove('tree')
        })
    )
    foreach ($case in $metadataNegativeCases) {
        Reset-OwnerMetadata
        Set-OwnerMetadataMutation $case[1]
        Assert-Rejected { Invoke-OwnerMetadataVerification } $case[0]
    }
    Remove-Item -LiteralPath $ownerMetadata -Force
    Assert-Rejected { Invoke-OwnerMetadataVerification } 'omitted metadata asset'

    $workflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot '.github\workflows\release-labs.yml')
    foreach ($required in @(
        '--prerelease',
        '--latest=false',
        "environment: labs",
        'Compare-Object $expectedAssets $actualAssets',
        'RUSTY_KIOSK_LABS_MANIFEST_SHA256',
        'RUSTY_KIOSK_LABS_SIGNER_SHA256'
        'secrets.WINDOWS_PACKAGE_CERTIFICATE_BASE64'
        'secrets.WINDOWS_PACKAGE_CERTIFICATE_PASSWORD'
        'secrets.WINDOWS_PACKAGE_PUBLISHER'
        'secrets.WINDOWS_PREVIEW_SETUP_CERTIFICATE_BASE64'
        'secrets.WINDOWS_PREVIEW_SETUP_CERTIFICATE_PASSWORD'
        'questionable-file-manager-labs-owner-release.json'
        'absence was not proven by a GitHub API 404'
        'Published Labs tag ref does not peel to GITHUB_SHA'
        'Published Labs asset readback differs'
        'Labs publication changed or could not distinguish the stable latest release.'
    )) {
        if (-not $workflow.Contains($required, [StringComparison]::Ordinal)) {
            throw "Labs workflow is missing contract text: $required"
        }
    }
    if ($workflow -match 'releases/latest/download') {
        throw 'Labs workflow contains a mutable latest/download URL.'
    }
    if ($workflow -match 'WINDOWS_LABS_') {
        throw 'Labs workflow requires obsolete channel-specific Windows signing secrets.'
    }

    $packageBuilder = Get-Content -Raw -LiteralPath (
        Join-Path $repoRoot 'tools\app\Build-App-Package.ps1')
    $environmentCapture = $packageBuilder.IndexOf(
        '$originalBuildEnvironment = [ordered]@{}',
        [StringComparison]::Ordinal)
    $resolverInitialization = $packageBuilder.LastIndexOf(
        '    Initialize-DotNetSdkResolver',
        [StringComparison]::Ordinal)
    $environmentRestore = $packageBuilder.LastIndexOf(
        'foreach ($entry in $originalBuildEnvironment.GetEnumerator())',
        [StringComparison]::Ordinal)
    if ($environmentCapture -lt 0 -or
        $resolverInitialization -le $environmentCapture -or
        $environmentRestore -le $resolverInitialization -or
        -not $packageBuilder.Contains(
            "'DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR'",
            [StringComparison]::Ordinal) -or
        -not $packageBuilder.Contains(
            "'DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR'",
            [StringComparison]::Ordinal) -or
        -not $packageBuilder.Contains(
            "'DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER'",
            [StringComparison]::Ordinal)) {
        throw 'Package build does not restore its temporary MSBuild resolver environment.'
    }
    foreach ($channelAwareReleaseScript in @(
        'tools\app\Invoke-ReleaseBuild.ps1',
        'tools\app\Build-App-Package.ps1'
    )) {
        $channelAwareReleaseText = Get-Content -Raw -LiteralPath (
            Join-Path $repoRoot $channelAwareReleaseScript)
        if (-not $channelAwareReleaseText.Contains(
                '-ExpectedProductChannel $ProductChannel',
                [StringComparison]::Ordinal)) {
            throw "$channelAwareReleaseScript does not preserve the Labs Fleet trust boundary."
        }
    }

    $releaseAssetGate = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'tools\app\Test-ReleaseAssets.ps1')
    foreach ($requiredSignerPolicy in @(
        "[string]`$ExpectedPublisher = 'CN=MesmerPrism'",
        "'08A5878AD6E652A94517D2C79144EB2655B0088C'",
        'setup helper signer does not match the reviewed organizational signer thumbprint',
        'MSIX signer does not match the reviewed organizational signer thumbprint'
    )) {
        if (-not $releaseAssetGate.Contains($requiredSignerPolicy, [StringComparison]::Ordinal)) {
            throw "Release asset validation is missing reviewed signer policy: $requiredSignerPolicy"
        }
    }
    foreach ($requiredAppInstallerPolicy in @(
        "if (`$ProductChannel -eq 'labs') {",
        '"https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/download/$ReleaseTag/"',
        "`$appInstallerRoot.GetAttribute('Uri')",
        "`$mainPackage.GetAttribute('Uri')",
        "[string]`$uri -match '/latest(?:/|`$)'",
        "if (`$ProductChannel -eq 'stable' -and"
    )) {
        if (-not $releaseAssetGate.Contains(
                $requiredAppInstallerPolicy,
                [StringComparison]::Ordinal)) {
            throw "Release asset validation is missing App Installer channel policy: $requiredAppInstallerPolicy"
        }
    }

    $setupPublisher = Get-Content -Raw -LiteralPath (
        Join-Path $repoRoot 'tools\app\Publish-GuidedSetup.ps1')
    foreach ($requiredAuthorityBoundary in @(
        "if (`$ProductChannel -ceq 'stable')",
        '$releaseTrustParameters.ExpectedSetupSignerCertificateSha256'
    )) {
        if (-not $setupPublisher.Contains(
                $requiredAuthorityBoundary,
                [StringComparison]::Ordinal)) {
            throw "Labs Setup publishing is missing the Fleet signer authority boundary: $requiredAuthorityBoundary"
        }
    }
    $fleetReleaseValidator = Get-Content -Raw -LiteralPath (
        Join-Path $repoRoot `
            'tools\Test-FleetInstallerReleaseConfiguration.ps1')
    if (-not $fleetReleaseValidator.Contains(
            'Labs releases require the checked-in Fleet installer trust block to remain absent.',
            [StringComparison]::Ordinal)) {
        throw 'Labs validation does not require absent checked-in Fleet trust.'
    }

    $stableWorkflowPath = Join-Path $repoRoot '.github\workflows\release.yml'
    $stableWorkflowSha256 = (
        Get-FileHash -LiteralPath $stableWorkflowPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($stableWorkflowSha256 -cne $ExpectedStableWorkflowSha256) {
        throw (
            'The stable release workflow differs from the reviewed ' +
            "SHA-256 $ExpectedStableWorkflowSha256."
        )
    }

    $labsSetup = Join-Path $testRoot 'labs-setup'
    & dotnet publish (Join-Path $repoRoot 'src\QuestIonAbleFileManager.Setup\QuestIonAbleFileManager.Setup.csproj') `
        --configuration Release --runtime win-x64 --self-contained false `
        -p:QfmProductChannel=labs `
        -p:QfmMaturity=alpha `
        -p:QfmDistributionTrack=github-prerelease `
        -p:QfmReleaseTag=v2.3.4-alpha.5 `
        -p:QfmPackageIdentity=MesmerPrism.QuestIonAbleFileManager.Labs `
        '-p:QfmDistributionDisplayName=QuestIonAble File Manager Labs' `
        -p:QfmSetupAssetStem=QuestIonAbleFileManager-Labs `
        --output $labsSetup *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Synthetic Labs Setup publish failed.'
    }
    $labsFleetValidation = & (Join-Path $repoRoot `
        'tools\Test-FleetInstallerReleaseConfiguration.ps1') `
        -ExpectedVersion 2.3.4 `
        -ExpectedTag v2.3.4-alpha.5 `
        -ExpectedProductChannel labs `
        -SetupExecutablePath (Join-Path $labsSetup `
            'QuestIonAbleFileManager.Setup.exe') | ConvertFrom-Json
    if ($labsFleetValidation.compiled_field_count -ne 0 -or
        $labsFleetValidation.setup_replay_security -cne
            'stable_routes_disabled_and_rejected' -or
        $labsFleetValidation.status -cne 'passed') {
        throw 'Labs Fleet validation did not prove absent trust and disabled stable replay routes.'
    }
    Assert-Rejected {
        & (Join-Path $repoRoot `
            'tools\Test-FleetInstallerReleaseConfiguration.ps1') `
            -ExpectedProductChannel labs `
            -ExpectedSetupSignerCertificateSha256 ('f' * 64) | Out-Null
    } 'stable Fleet provisioning signer authority supplied to Labs'
    foreach ($oldTrack in @('stable', 'labs')) {
        $invalidSetup = Join-Path $testRoot "invalid-track-$oldTrack"
        & dotnet publish (Join-Path $repoRoot 'src\QuestIonAbleFileManager.Setup\QuestIonAbleFileManager.Setup.csproj') --configuration Release --runtime win-x64 --self-contained false -p:QfmProductChannel=labs -p:QfmMaturity=alpha -p:QfmDistributionTrack=$oldTrack -p:QfmReleaseTag=v2.3.4-alpha.5 -p:QfmPackageIdentity=MesmerPrism.QuestIonAbleFileManager.Labs '-p:QfmDistributionDisplayName=QuestIonAble File Manager Labs' -p:QfmSetupAssetStem=QuestIonAbleFileManager-Labs --output $invalidSetup *> $null
        if ($LASTEXITCODE -ne 0) { throw "Synthetic old-track Setup publish failed before runtime rejection: $oldTrack" }
        & dotnet (Join-Path $invalidSetup 'QuestIonAbleFileManager.Setup.dll') --plan --json *> $null
        if ($LASTEXITCODE -eq 0) { throw "Setup accepted obsolete channel-valued distribution track: $oldTrack" }
    }
    $labsSetupDll = Join-Path $labsSetup 'QuestIonAbleFileManager.Setup.dll'
    foreach ($arguments in @(
        @('--repair-fleet-replay-protection', '--quiet'),
        @('--destructive-reset-fleet-replay-protection', '--quiet'),
        @(
            '--fleet-replay-accept',
            ('0' * 64),
            ('1' * 64),
            '1.2.3',
            ('2' * 64)
        )
    )) {
        & dotnet $labsSetupDll @arguments *> $null
        if ($LASTEXITCODE -eq 0) {
            throw "Labs Setup reached a stable Fleet replay route: $($arguments[0])"
        }
    }

    Write-Output 'Labs distribution contract tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
