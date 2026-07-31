[CmdletBinding()]
param(
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ExpectedStableWorkflowSha256 =
        '5382f772fc5adc57886fa29f84ea7d75e0a722a69efdc9dd2fe6a7ac5d7567b8'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$testRoot = Join-Path $artifactsRoot ("alpha-contract-{0}" -f [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $testRoot 'bundle'
$fakeSigner = Join-Path $testRoot 'apksigner.cmd'
$tag = 'v2.3.4-alpha.5'
$version = $tag.Substring(1)
$revision = '0123456789abcdef0123456789abcdef01234567'
$sourceTree = '89abcdef0123456789abcdef0123456789abcdef'
$versionCode = 2030405
$signer = 'a' * 64
$mainPackage = 'io.github.mesmerprism.rustykiosk'
$helperPackage = 'io.github.mesmerprism.rustykiosk.setuphelper'
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
        [string]$ManifestMainPackage = $mainPackage,
        [string]$ManifestHelperPackage = $helperPackage,
        [string]$ManifestSigner = $signer,
        [long]$ManifestVersionCode = $versionCode,
        [string]$ManifestSourceTree = $sourceTree,
        [bool]$Prerelease = $true,
        [string]$IdentityMode = 'same-package-in-place'
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
        prerelease = $Prerelease
        tag = $ManifestTag
        version = $ManifestVersion
        version_code = $ManifestVersionCode
        identity_mode = $IdentityMode
        exit_policy = 'in-place; install a later same-signer stable build with a higher versionCode'
        source_url = 'https://github.com/MesmerPrism/Rusty-Kiosk'
        source_revision = $revision
        source_tree = $ManifestSourceTree
        signer_sha256 = $ManifestSigner
        staged_at_utc = '2026-07-31T00:00:00.0000000+00:00'
        files = @($files | ForEach-Object {
            $path = Join-Path $bundle $_
            $entry = [ordered]@{
                name = $_
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                bytes = (Get-Item -LiteralPath $path).Length
            }
            if ($_ -in @('rusty-kiosk.apk', 'rusty-kiosk-setup-helper.apk')) {
                $entry.package_name = if ($_ -ceq 'rusty-kiosk.apk') {
                    $ManifestMainPackage
                } else {
                    $ManifestHelperPackage
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
        -ExpectedSourceTree $sourceTree `
        -ExpectedVersionCode $versionCode `
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
Source tree: $sourceTree
Version: $version
Channel: alpha
Tag: $tag
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
    Assert-Rejected {
        & (Join-Path $PSScriptRoot 'Test-RustyKioskReleaseBundle.ps1') `
            -BundleDirectory $bundle -ExpectedVersion $version `
            -ExpectedSourceRevision $revision -ExpectedChannel alpha `
            -ExpectedTag $tag -ExpectedReleaseUrl 'https://github.com/MesmerPrism/Rusty-Kiosk/releases/latest/download/bundle-manifest.json' `
            -ExpectedSourceTree $sourceTree -ExpectedVersionCode $versionCode `
            -ExpectedMainPackageName $mainPackage -ExpectedHelperPackageName $helperPackage `
            -ExpectedSignerSha256 $signer -ExpectedManifestSha256 ('0' * 64) `
            -ApkSignerPath $fakeSigner | Out-Null
    } 'latest reviewed release URL'
    Write-Manifest -ManifestMainPackage 'io.github.mesmerprism.rustykiosk.alpha'
    Assert-Rejected { Invoke-Verification } 'unreviewed alpha Android package substituted'
    Write-Manifest -ManifestVersionCode 2030406
    Assert-Rejected { Invoke-Verification } 'mismatched versionCode'
    Write-Manifest -Prerelease $false
    Assert-Rejected { Invoke-Verification } 'prerelease false'
    Write-Manifest -IdentityMode 'parallel-package'
    Assert-Rejected { Invoke-Verification } 'non-in-place identity mode'
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
            -ExpectedSourceRevision $revision -ExpectedChannel alpha `
            -ExpectedTag $tag -ExpectedReleaseUrl $releaseUrl `
            -ExpectedSourceTree $sourceTree -ExpectedVersionCode $versionCode `
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
        'absence was not proven by a GitHub API 404'
        'Published alpha tag ref does not peel to GITHUB_SHA'
        'Published alpha asset readback differs'
        'Alpha publication changed or could not distinguish the stable latest release.'
    )) {
        if (-not $workflow.Contains($required, [StringComparison]::Ordinal)) {
            throw "Alpha workflow is missing contract text: $required"
        }
    }
    if ($workflow -match 'releases/latest/download') {
        throw 'Alpha workflow contains a mutable latest/download URL.'
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

    $alphaSetup = Join-Path $testRoot 'alpha-setup'
    & dotnet publish (Join-Path $repoRoot 'src\QuestIonAbleFileManager.Setup\QuestIonAbleFileManager.Setup.csproj') `
        --configuration Release --runtime win-x64 --self-contained false `
        -p:QfmDistributionChannel=alpha `
        -p:QfmReleaseTag=v2.3.4-alpha.5 `
        -p:QfmPackageIdentity=MesmerPrism.QuestIonAbleFileManager.Alpha `
        '-p:QfmDistributionDisplayName=QuestIonAble File Manager Alpha' `
        -p:QfmSetupAssetStem=QuestIonAbleFileManager-Alpha `
        --output $alphaSetup *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Synthetic alpha Setup publish failed.'
    }
    $alphaSetupDll = Join-Path $alphaSetup 'QuestIonAbleFileManager.Setup.dll'
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
        & dotnet $alphaSetupDll @arguments *> $null
        if ($LASTEXITCODE -eq 0) {
            throw "Alpha Setup reached a stable Fleet replay route: $($arguments[0])"
        }
    }

    Write-Output 'Alpha distribution contract tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
