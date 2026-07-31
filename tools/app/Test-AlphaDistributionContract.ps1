[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$testRoot = Join-Path $artifactsRoot ("alpha-contract-{0}" -f [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $testRoot 'bundle'
$fakeSigner = Join-Path $testRoot 'apksigner.cmd'
$tag = 'v2.3.4-alpha.5'
$version = $tag.Substring(1)
$revision = '0123456789abcdef0123456789abcdef01234567'
$signer = 'a' * 64
$mainPackage = 'com.mesmerprism.rustykiosk.alpha'
$helperPackage = 'com.mesmerprism.rustykiosk.setup.alpha'
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
    throw "Alpha contract negative case was accepted: $Name"
}

function Write-Manifest {
    param(
        [string]$Channel = 'alpha',
        [string]$ManifestTag = $tag,
        [string]$ManifestVersion = $version,
        [string]$ManifestReleaseUrl = $releaseUrl,
        [string]$ManifestMainPackage = $mainPackage,
        [string]$ManifestHelperPackage = $helperPackage,
        [string]$ManifestSigner = $signer
    )
    $files = @(
        'rusty-kiosk.apk',
        'rusty-kiosk-setup-helper.apk',
        'RUSTY-KIOSK-LICENSE.txt',
        'RUSTY-KIOSK-SOURCE.txt'
    )
    $manifest = [ordered]@{
        schema = 'meta.quest.file_manager.rusty_kiosk_bundle.v1'
        build_type = 'release'
        channel = $Channel
        tag = $ManifestTag
        version = $ManifestVersion
        release_url = $ManifestReleaseUrl
        source_url = 'https://github.com/MesmerPrism/Rusty-Kiosk'
        source_revision = $revision
        signer_sha256 = $ManifestSigner
        package_identity = [ordered]@{
            main = $ManifestMainPackage
            setup_helper = $ManifestHelperPackage
        }
        staged_at_utc = '2026-07-31T00:00:00.0000000+00:00'
        files = @($files | ForEach-Object {
            $path = Join-Path $bundle $_
            [ordered]@{
                name = $_
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                bytes = (Get-Item -LiteralPath $path).Length
            }
        })
    }
    $manifest | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $bundle 'bundle-manifest.json') -Encoding utf8
}

function Invoke-Verification {
    $manifestHash =
        (Get-FileHash -LiteralPath (Join-Path $bundle 'bundle-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    & (Join-Path $PSScriptRoot 'Test-RustyKioskReleaseBundle.ps1') `
        -BundleDirectory $bundle `
        -ExpectedVersion $version `
        -ExpectedSourceRevision $revision `
        -ExpectedChannel alpha `
        -ExpectedTag $tag `
        -ExpectedReleaseUrl $releaseUrl `
        -ExpectedMainPackageName $mainPackage `
        -ExpectedHelperPackageName $helperPackage `
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
Version: $version
"@
    Set-Content -LiteralPath $fakeSigner -Encoding ascii -Value "@echo V2 Signer: certificate SHA-256 digest: $signer 1>&2"

    Write-Manifest
    Invoke-Verification

    Write-Manifest -Channel stable
    Assert-Rejected { Invoke-Verification } 'stable bundle substituted into alpha'
    Write-Manifest -ManifestTag 'v2.3.4-alpha.6'
    Assert-Rejected { Invoke-Verification } 'mismatched exact tag'
    Write-Manifest -ManifestVersion '2.3.4'
    Assert-Rejected { Invoke-Verification } 'stable version substituted into alpha'
    Write-Manifest -ManifestReleaseUrl 'https://github.com/MesmerPrism/Rusty-Kiosk/releases/latest/download/bundle-manifest.json'
    Assert-Rejected { Invoke-Verification } 'latest URL'
    Write-Manifest -ManifestMainPackage 'com.mesmerprism.rustykiosk'
    Assert-Rejected { Invoke-Verification } 'stable Android package substituted into alpha'
    Write-Manifest -ManifestSigner ('b' * 64)
    Assert-Rejected { Invoke-Verification } 'wrong signer'
    Write-Manifest
    $wrongHash = 'c' * 64
    Assert-Rejected {
        & (Join-Path $PSScriptRoot 'Test-RustyKioskReleaseBundle.ps1') `
            -BundleDirectory $bundle -ExpectedVersion $version `
            -ExpectedSourceRevision $revision -ExpectedChannel alpha `
            -ExpectedTag $tag -ExpectedReleaseUrl $releaseUrl `
            -ExpectedMainPackageName $mainPackage -ExpectedHelperPackageName $helperPackage `
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

    $workflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot '.github\workflows\release-alpha.yml')
    foreach ($required in @(
        '--prerelease',
        '--latest=false',
        "environment: alpha",
        'Compare-Object $expectedAssets $actualAssets',
        'RUSTY_KIOSK_ALPHA_MANIFEST_SHA256',
        'RUSTY_KIOSK_ALPHA_SIGNER_SHA256'
    )) {
        if (-not $workflow.Contains($required, [StringComparison]::Ordinal)) {
            throw "Alpha workflow is missing contract text: $required"
        }
    }
    if ($workflow -match 'releases/latest/download') {
        throw 'Alpha workflow contains a mutable latest/download URL.'
    }

    $stableWorkflow = git -C $repoRoot show HEAD:.github/workflows/release.yml
    $workingStableWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repoRoot '.github\workflows\release.yml')
    if (($stableWorkflow -join "`n").TrimEnd() -cne $workingStableWorkflow.TrimEnd()) {
        throw 'The stable release workflow changed while adding alpha.'
    }

    Write-Output 'Alpha distribution contract tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
