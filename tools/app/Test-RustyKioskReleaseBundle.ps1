[CmdletBinding(DefaultParameterSetName = 'Verify')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Verify')]
    [string]$BundleDirectory,

    [Parameter(Mandatory = $true, ParameterSetName = 'Verify')]
    [ValidatePattern('^(0|[1-9]\d{0,3})\.(0|[1-9]\d?)\.(0|[1-9]\d?)(?:-(?:alpha|beta|rc)\.([1-9]|[1-8]\d|9[0-8]))?$')]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true, ParameterSetName = 'Verify')]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedSourceRevision,

    [Parameter(ParameterSetName = 'Verify')]
    [ValidateSet('stable', 'labs')]
    [string]$ExpectedProductChannel = 'stable',

    [Parameter(ParameterSetName = 'Verify')]
    [ValidateSet('alpha', 'beta', 'rc', 'released')]
    [string]$ExpectedMaturity = 'released',

    [Parameter(ParameterSetName = 'Verify')]
    [ValidateSet('github-release', 'github-prerelease')]
    [string]$ExpectedDistributionTrack = 'github-release',

    [Parameter(ParameterSetName = 'Verify')]
    [string]$ExpectedTag,

    [Parameter(ParameterSetName = 'Verify')]
    [string]$ExpectedReleaseUrl,

    [Parameter(ParameterSetName = 'Verify')]
    [string]$ExpectedSourceTree,

    [Parameter(ParameterSetName = 'Verify')]
    [long]$ExpectedVersionCode,

    [Parameter(ParameterSetName = 'Verify')]
    [string]$ExpectedMainPackageName,

    [Parameter(ParameterSetName = 'Verify')]
    [string]$ExpectedHelperPackageName,

    [Parameter(ParameterSetName = 'Verify')]
    [string]$ExpectedOwnerMetadataPath,

    [Parameter(ParameterSetName = 'Verify')]
    [string]$ExpectedSignerSha256,

    [Parameter(ParameterSetName = 'Verify')]
    [string]$ExpectedManifestSha256,

    [Parameter(ParameterSetName = 'Verify')]
    [string]$ApkSignerPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'SelfTest')]
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

if ($SelfTest) {
    $artifactsRoot = Join-Path $repoRoot 'artifacts'
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    $testRoot = Join-Path $artifactsRoot ("kiosk-bundle-verifier-{0}" -f [guid]::NewGuid().ToString('N'))
    $bundle = Join-Path $testRoot 'bundle'
    $fakeSigner = Join-Path $testRoot 'apksigner.cmd'
    $version = '9.8.7'
    $revision = '0123456789abcdef0123456789abcdef01234567'
    $signer = 'a' * 64

    try {
        New-Item -ItemType Directory -Path $bundle -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $bundle 'rusty-kiosk.apk'), [byte[]](1, 2, 3, 4))
        [IO.File]::WriteAllBytes((Join-Path $bundle 'rusty-kiosk-setup-helper.apk'), [byte[]](5, 6, 7))
        Set-Content -LiteralPath (Join-Path $bundle 'RUSTY-KIOSK-LICENSE.txt') -Encoding utf8 -Value 'test license'
        Set-Content -LiteralPath (Join-Path $bundle 'RUSTY-KIOSK-SOURCE.txt') -Encoding utf8 -Value @"
Rusty Kiosk source: https://github.com/MesmerPrism/Rusty-Kiosk
Source revision: $revision
Version: $version
License: GNU Affero General Public License v3.0 or later (see RUSTY-KIOSK-LICENSE.txt)
"@
        Set-Content -LiteralPath $fakeSigner -Encoding ascii -Value @"
@echo V2 Signer: certificate SHA-256 digest: $signer 1>&2
"@

        $fileNames = @(
            'rusty-kiosk.apk',
            'rusty-kiosk-setup-helper.apk',
            'RUSTY-KIOSK-LICENSE.txt',
            'RUSTY-KIOSK-SOURCE.txt'
        )
        $manifest = [ordered]@{
            schema = 'meta.quest.file_manager.rusty_kiosk_bundle.v2'
            build_type = 'release'
            version = $version
            product_channel = 'stable'
            maturity = 'released'
            distribution_track = 'github-release'
            source_url = 'https://github.com/MesmerPrism/Rusty-Kiosk'
            source_revision = $revision
            signer_sha256 = $signer
            staged_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
            files = @($fileNames | ForEach-Object {
                $path = Join-Path $bundle $_
                [ordered]@{
                    name = $_
                    sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                    bytes = (Get-Item -LiteralPath $path).Length
                }
            })
        }
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bundle 'bundle-manifest.json') -Encoding utf8

        $null = & $PSCommandPath `
            -BundleDirectory $bundle `
            -ExpectedVersion $version `
            -ExpectedSourceRevision $revision `
            -ApkSignerPath $fakeSigner

        Add-Content -LiteralPath (Join-Path $bundle 'rusty-kiosk.apk') -Value 'tamper'
        $tamperRejected = $false
        try {
            $null = & $PSCommandPath `
                -BundleDirectory $bundle `
                -ExpectedVersion $version `
                -ExpectedSourceRevision $revision `
                -ApkSignerPath $fakeSigner
        }
        catch {
            $tamperRejected = $true
        }
        if (-not $tamperRejected) {
            throw 'The Rusty Kiosk bundle verifier accepted a tampered APK.'
        }

        Write-Output 'Rusty Kiosk release-bundle verifier self-test passed.'
        return
    }
    finally {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$BundleDirectory = [IO.Path]::GetFullPath($BundleDirectory)
$manifestPath = Join-Path $BundleDirectory 'bundle-manifest.json'
$expectedSourceUrl = 'https://github.com/MesmerPrism/Rusty-Kiosk'
$expectedFiles = @(
    'rusty-kiosk.apk',
    'rusty-kiosk-setup-helper.apk',
    'RUSTY-KIOSK-LICENSE.txt',
    'RUSTY-KIOSK-SOURCE.txt'
)
foreach ($name in @($expectedFiles + 'bundle-manifest.json')) {
    $path = Join-Path $BundleDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The Rusty Kiosk release bundle is incomplete; missing $name."
    }
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$manifestProperties = @($manifest.PSObject.Properties.Name | Sort-Object)
$expectedManifestProperties = @('schema','build_type','product_channel','maturity','distribution_track','prerelease','tag','version','version_code','identity_mode','exit_policy','source_url','source_revision','source_tree','signer_sha256','files' | Sort-Object)
if ($ExpectedProductChannel -eq 'labs' -and
    ($manifestProperties -join "`n") -cne ($expectedManifestProperties -join "`n")) {
    throw 'The Rusty Kiosk Labs manifest shape is incomplete or expanded.'
}
if ($manifest.schema -cne 'meta.quest.file_manager.rusty_kiosk_bundle.v2' -or $manifest.build_type -cne 'release') {
    throw 'The Rusty Kiosk bundle does not use the supported release manifest.'
}
if ($manifest.version -ne $ExpectedVersion) {
    throw "Rusty Kiosk bundle version '$($manifest.version)' does not match requested release '$ExpectedVersion'."
}
if ($manifest.product_channel -cne $ExpectedProductChannel -or
    $manifest.maturity -cne $ExpectedMaturity -or
    $manifest.distribution_track -cne $ExpectedDistributionTrack) {
    throw 'The Rusty Kiosk bundle distribution axes do not match the requested product.'
}
if ($ExpectedProductChannel -eq 'labs') {
    $versionMatch = [regex]::Match(
        $ExpectedVersion,
        '^(?<major>0|[1-9]\d{0,3})\.(?<minor>0|[1-9]\d?)\.(?<patch>0|[1-9]\d?)-alpha\.(?<alpha>[1-9]|[1-8]\d|9[0-8])$')
    if (-not $versionMatch.Success -or
        [int64]$versionMatch.Groups['major'].Value -gt 2099 -or
        $ExpectedTag -cne "v$ExpectedVersion") {
        throw 'Alpha Kiosk input requires an exact canonical vX.Y.Z-alpha.N tag/version pair.'
    }
    $mappedVersionCode =
        [int64]$versionMatch.Groups['major'].Value * 1000000L +
        [int64]$versionMatch.Groups['minor'].Value * 10000L +
        [int64]$versionMatch.Groups['patch'].Value * 100L +
        [int64]$versionMatch.Groups['alpha'].Value
    if ($ExpectedMaturity -cne 'alpha' -or
        $ExpectedDistributionTrack -cne 'github-prerelease' -or
        $manifest.prerelease -ne $true -or
        $manifest.tag -cne $ExpectedTag) {
        throw 'The Rusty Kiosk Labs bundle maturity/tag does not match the requested alpha prerelease.'
    }
    $canonicalReleaseUrl =
        "https://github.com/MesmerPrism/Rusty-Kiosk/releases/tag/$ExpectedTag"
    if ($ExpectedReleaseUrl -cne $canonicalReleaseUrl -or
        $ExpectedReleaseUrl -match '/latest(?:/|$)') {
        throw 'The reviewed Rusty Kiosk alpha release URL must identify the exact immutable tag.'
    }
    if ($ExpectedVersionCode -ne $mappedVersionCode -or
        [long]$manifest.version_code -ne $ExpectedVersionCode) {
        throw 'The Rusty Kiosk Labs Android versionCode does not match the reviewed deterministic mapping.'
    }
    if ($manifest.identity_mode -cne 'separate-coinstallable' -or
        $manifest.exit_policy -cne 'uninstall-labs-without-changing-stable' -or
        @($ExpectedMainPackageName, $ExpectedHelperPackageName |
            Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0 -or
        @($ExpectedMainPackageName, $ExpectedHelperPackageName |
            Select-Object -Unique).Count -ne 2) {
        throw 'The Rusty Kiosk Labs bundle does not use the two exact separate-coinstallable package identities.'
    }
    if ($ExpectedSourceTree -notmatch '^[0-9a-f]{40}$' -or
        $manifest.source_tree -cne $ExpectedSourceTree) {
        throw 'The Rusty Kiosk Labs source tree does not match the reviewed owner tree.'
    }
    if ($ExpectedSignerSha256 -notmatch '^[0-9a-f]{64}$' -or
        $manifest.signer_sha256 -cne $ExpectedSignerSha256) {
        throw 'The Rusty Kiosk Labs signer does not match the pinned consumer policy.'
    }
    $actualManifestSha256 =
        (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ExpectedManifestSha256 -notmatch '^[0-9a-f]{64}$' -or
        $actualManifestSha256 -cne $ExpectedManifestSha256) {
        throw 'The Rusty Kiosk Labs bundle manifest does not match its pinned release hash.'
    }
}
elseif ($ExpectedMaturity -cne 'released' -or
    $ExpectedDistributionTrack -cne 'github-release' -or
    ($ExpectedTag -and $ExpectedTag -cne "v$ExpectedVersion")) {
    throw 'Stable Kiosk input tag does not match its exact stable version.'
}
if ($manifest.source_url -ne $expectedSourceUrl) {
    throw "Unexpected Rusty Kiosk source URL: $($manifest.source_url)"
}
if ($manifest.source_revision -ine $ExpectedSourceRevision) {
    throw "Rusty Kiosk source revision '$($manifest.source_revision)' does not match tag commit '$ExpectedSourceRevision'."
}
if ([string]$manifest.signer_sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'The Rusty Kiosk manifest signer digest is missing or malformed.'
}

$manifestFiles = @($manifest.files)
if ($manifestFiles.Count -ne $expectedFiles.Count) {
    throw "The Rusty Kiosk manifest must describe exactly $($expectedFiles.Count) payload files."
}
$manifestNames = @($manifestFiles | ForEach-Object { [string]$_.name })
if (@($manifestNames | Select-Object -Unique).Count -ne $manifestNames.Count -or
    @(Compare-Object -ReferenceObject $expectedFiles -DifferenceObject $manifestNames).Count -ne 0) {
    throw 'The Rusty Kiosk manifest payload names are incomplete, duplicated, or unexpected.'
}
foreach ($file in $manifestFiles) {
    $path = Join-Path $BundleDirectory $file.name
    if ([string]$file.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$file.bytes -lt 1) {
        throw "Rusty Kiosk manifest metadata is malformed for $($file.name)."
    }
    $actualBytes = (Get-Item -LiteralPath $path).Length
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualBytes -ne [long]$file.bytes -or $actualHash -ine [string]$file.sha256) {
        throw "Rusty Kiosk payload does not match its manifest: $($file.name)."
    }
    if ($ExpectedProductChannel -eq 'labs' -and
        $file.name -in @('rusty-kiosk.apk', 'rusty-kiosk-setup-helper.apk')) {
        $expectedPackage = switch ($file.name) {
            'rusty-kiosk.apk' { $ExpectedMainPackageName }
            'rusty-kiosk-setup-helper.apk' { $ExpectedHelperPackageName }
        }
        if ([string]::IsNullOrWhiteSpace($expectedPackage) -or
            $file.package_name -cne $expectedPackage -or
            $file.version_name -cne $ExpectedVersion -or
            [long]$file.version_code -ne $ExpectedVersionCode) {
            throw "Rusty Kiosk APK identity does not match the pinned owner manifest: $($file.name)."
        }
    }
}

$sourceLines = @(Get-Content -LiteralPath (Join-Path $BundleDirectory 'RUSTY-KIOSK-SOURCE.txt'))
$requiredSourceLines = @(
    "Rusty Kiosk source: $expectedSourceUrl",
    "Source revision: $ExpectedSourceRevision",
    "Version: $ExpectedVersion"
)
if ($ExpectedProductChannel -eq 'labs') {
    $requiredSourceLines += @(
        "Source tree: $ExpectedSourceTree",
        "Product channel: $ExpectedProductChannel",
        "Maturity: $ExpectedMaturity",
        "Distribution track: $ExpectedDistributionTrack",
        "Tag: $ExpectedTag"
    )
}
foreach ($requiredLine in $requiredSourceLines) {
    if ($sourceLines -notcontains $requiredLine) {
        throw "Rusty Kiosk source pointer is inconsistent; missing '$requiredLine'."
    }
}

if (-not $ApkSignerPath) {
    $sdkRoots = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $ApkSignerPath = @($sdkRoots | ForEach-Object {
        Get-ChildItem -Path (Join-Path $_ 'build-tools\*\apksigner.bat') -ErrorAction SilentlyContinue
    } | Sort-Object FullName -Descending | Select-Object -First 1).FullName
}
if (-not $ApkSignerPath -or -not (Test-Path -LiteralPath $ApkSignerPath -PathType Leaf)) {
    throw 'apksigner is required to verify the Rusty Kiosk release bundle.'
}

function Get-ApkSignerDigest {
    param([Parameter(Mandatory = $true)][string]$Path)
    $output = & $ApkSignerPath verify --print-certs $Path 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "APK signature verification failed for $([IO.Path]::GetFileName($Path))."
    }
    $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    $matches = [regex]::Matches(
        $text,
        '(?im)certificate\s+SHA-?256\s+digest\s*:\s*([0-9a-fA-F:\- ]{64,128})'
    )
    if ($matches.Count -eq 0) {
        throw "No APK signer digest was reported for $([IO.Path]::GetFileName($Path)).`nVerifier output:`n$text"
    }
    $digests = @($matches | ForEach-Object {
        ($_.Groups[1].Value -replace '[^0-9a-fA-F]', '').ToLowerInvariant()
    } | Sort-Object -Unique)
    if ($digests.Count -ne 1 -or $digests[0].Length -ne 64) {
        throw "Expected exactly one 32-byte APK signer digest for $([IO.Path]::GetFileName($Path)), got: $($digests -join ', ')"
    }
    return $digests[0]
}

$mainSigner = Get-ApkSignerDigest -Path (Join-Path $BundleDirectory 'rusty-kiosk.apk')
$helperSigner = Get-ApkSignerDigest -Path (Join-Path $BundleDirectory 'rusty-kiosk-setup-helper.apk')
$expectedSigner = ([string]$manifest.signer_sha256).ToLowerInvariant()
if ($mainSigner -ne $helperSigner -or $mainSigner -ne $expectedSigner) {
    throw 'The Kiosk core/helper APK set does not match the same signer recorded in its release manifest.'
}

if ($ExpectedProductChannel -eq 'labs') {
    if ([string]::IsNullOrWhiteSpace($ExpectedOwnerMetadataPath) -or
        -not (Test-Path -LiteralPath $ExpectedOwnerMetadataPath -PathType Leaf)) {
        throw 'The exact Kiosk Labs owner metadata asset is required.'
    }
    $owner = Get-Content -Raw -LiteralPath $ExpectedOwnerMetadataPath | ConvertFrom-Json
    $ownerNames = @($owner.PSObject.Properties.Name | Sort-Object)
    $expectedOwnerNames = @('schema','repository','product','product_channel','maturity','distribution_track','prerelease','tag','version','source_revision','source_tree','installation_identity','coinstallable_lineage','bundle_manifest','primary_artifact' | Sort-Object)
    $lineageNames = @($owner.coinstallable_lineage.PSObject.Properties.Name | Sort-Object)
    $bundleNames = @($owner.bundle_manifest.PSObject.Properties.Name | Sort-Object)
    $artifactNames = @($owner.primary_artifact.PSObject.Properties.Name | Sort-Object)
    if (($ownerNames -join "`n") -cne ($expectedOwnerNames -join "`n") -or
        ($lineageNames -join "`n") -cne (@('identity_mode','package_name','signer_sha256','version_name','version_code','exit_policy' | Sort-Object) -join "`n") -or
        ($bundleNames -join "`n") -cne (@('schema','name','sha256','bytes' | Sort-Object) -join "`n") -or
        ($artifactNames -join "`n") -cne (@('role','name','sha256','bytes' | Sort-Object) -join "`n") -or
        $owner.schema -cne 'rusty.kiosk.labs_release_owner_metadata.v2' -or
        $owner.repository -cne 'MesmerPrism/Rusty-Kiosk' -or
        $owner.product -cne 'rusty-kiosk-labs' -or
        $owner.product_channel -cne 'labs' -or $owner.maturity -cne 'alpha' -or
        $owner.distribution_track -cne 'github-prerelease' -or $owner.prerelease -ne $true -or
        $owner.tag -cne $ExpectedTag -or $owner.version -cne $ExpectedVersion -or
        $owner.source_revision -cne $ExpectedSourceRevision -or $owner.source_tree -cne $ExpectedSourceTree -or
        $owner.installation_identity -cne $ExpectedMainPackageName -or
        $owner.coinstallable_lineage.identity_mode -cne 'separate-coinstallable' -or
        $owner.coinstallable_lineage.package_name -cne $ExpectedMainPackageName -or
        $owner.coinstallable_lineage.signer_sha256 -cne $ExpectedSignerSha256 -or
        $owner.coinstallable_lineage.version_name -cne $ExpectedVersion -or
        [long]$owner.coinstallable_lineage.version_code -ne $ExpectedVersionCode -or
        $owner.coinstallable_lineage.exit_policy -cne 'uninstall-labs-without-changing-stable' -or
        $owner.bundle_manifest.schema -cne 'meta.quest.file_manager.rusty_kiosk_bundle.v2' -or
        $owner.bundle_manifest.name -cne 'bundle-manifest.json' -or
        $owner.bundle_manifest.sha256 -cne $ExpectedManifestSha256 -or
        [long]$owner.bundle_manifest.bytes -ne (Get-Item -LiteralPath $manifestPath).Length -or
        $owner.primary_artifact.role -cne 'complete-product' -or
        $owner.primary_artifact.name -cne 'rusty-kiosk.apk' -or
        $owner.primary_artifact.sha256 -cne (Get-FileHash -LiteralPath (Join-Path $BundleDirectory 'rusty-kiosk.apk') -Algorithm SHA256).Hash.ToLowerInvariant() -or
        [long]$owner.primary_artifact.bytes -ne (Get-Item -LiteralPath (Join-Path $BundleDirectory 'rusty-kiosk.apk')).Length) {
        throw 'The Kiosk Labs owner metadata does not bind the exact owner-authorized release evidence.'
    }
}

[pscustomobject]@{
    version = $manifest.version
    product_channel = $manifest.product_channel
    maturity = $manifest.maturity
    distribution_track = $manifest.distribution_track
    source_url = $manifest.source_url
    source_revision = ([string]$manifest.source_revision).ToLowerInvariant()
    signer_sha256 = $expectedSigner
    manifest_sha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    files = $manifestFiles
}
