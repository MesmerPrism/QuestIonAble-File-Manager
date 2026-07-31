[CmdletBinding(DefaultParameterSetName = 'Verify')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Verify')]
    [string]$BundleDirectory,

    [Parameter(Mandatory = $true, ParameterSetName = 'Verify')]
    [ValidatePattern('^(0|[1-9]\d{0,3})\.(0|[1-9]\d?)\.(0|[1-9]\d?)(?:-alpha\.([1-9]|[1-8]\d|9[0-8]))?$')]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true, ParameterSetName = 'Verify')]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedSourceRevision,

    [Parameter(ParameterSetName = 'Verify')]
    [ValidateSet('stable', 'alpha')]
    [string]$ExpectedChannel = 'stable',

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
            schema = 'meta.quest.file_manager.rusty_kiosk_bundle.v1'
            build_type = 'release'
            version = $version
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
if ($manifest.schema -ne 'meta.quest.file_manager.rusty_kiosk_bundle.v1' -or $manifest.build_type -ne 'release') {
    throw 'The Rusty Kiosk bundle does not use the supported release manifest.'
}
if ($manifest.version -ne $ExpectedVersion) {
    throw "Rusty Kiosk bundle version '$($manifest.version)' does not match requested release '$ExpectedVersion'."
}
if ($ExpectedChannel -eq 'alpha') {
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
    if ($manifest.channel -cne 'alpha' -or
        $manifest.prerelease -ne $true -or
        $manifest.tag -cne $ExpectedTag) {
        throw 'The Rusty Kiosk bundle channel/tag does not match the requested alpha prerelease.'
    }
    $canonicalReleaseUrl =
        "https://github.com/MesmerPrism/Rusty-Kiosk/releases/tag/$ExpectedTag"
    if ($ExpectedReleaseUrl -cne $canonicalReleaseUrl -or
        $ExpectedReleaseUrl -match '/latest(?:/|$)') {
        throw 'The reviewed Rusty Kiosk alpha release URL must identify the exact immutable tag.'
    }
    if ($ExpectedVersionCode -ne $mappedVersionCode -or
        [long]$manifest.version_code -ne $ExpectedVersionCode) {
        throw 'The Rusty Kiosk alpha Android versionCode does not match the reviewed deterministic mapping.'
    }
    if ($manifest.identity_mode -cne 'same-package-in-place' -or
        $manifest.exit_policy -cne
            'in-place; install a later same-signer stable build with a higher versionCode') {
        throw 'The Rusty Kiosk alpha bundle is not the reviewed same-package in-place update policy.'
    }
    if ($ExpectedSourceTree -notmatch '^[0-9a-f]{40}$' -or
        $manifest.source_tree -cne $ExpectedSourceTree) {
        throw 'The Rusty Kiosk alpha source tree does not match the reviewed owner tree.'
    }
    if ($ExpectedSignerSha256 -notmatch '^[0-9a-f]{64}$' -or
        $manifest.signer_sha256 -cne $ExpectedSignerSha256) {
        throw 'The Rusty Kiosk alpha signer does not match the pinned consumer policy.'
    }
    $actualManifestSha256 =
        (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ExpectedManifestSha256 -notmatch '^[0-9a-f]{64}$' -or
        $actualManifestSha256 -cne $ExpectedManifestSha256) {
        throw 'The Rusty Kiosk alpha bundle manifest does not match its pinned release hash.'
    }
}
elseif ($ExpectedTag -and $ExpectedTag -cne "v$ExpectedVersion") {
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
    if ($ExpectedChannel -eq 'alpha' -and
        $file.name -in @('rusty-kiosk.apk', 'rusty-kiosk-setup-helper.apk')) {
        $expectedPackage = if ($file.name -ceq 'rusty-kiosk.apk') {
            $ExpectedMainPackageName
        } else {
            $ExpectedHelperPackageName
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
if ($ExpectedChannel -eq 'alpha') {
    $requiredSourceLines += @(
        "Source tree: $ExpectedSourceTree",
        "Channel: $ExpectedChannel",
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
    throw 'The Kiosk APK pair does not match the same signer recorded in its release manifest.'
}

[pscustomobject]@{
    version = $manifest.version
    source_url = $manifest.source_url
    source_revision = ([string]$manifest.source_revision).ToLowerInvariant()
    signer_sha256 = $expectedSigner
    manifest_sha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    files = $manifestFiles
}
