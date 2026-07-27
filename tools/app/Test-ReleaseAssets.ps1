[CmdletBinding()]
param(
    [string]$ReleaseDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\release'),
    [string]$ExpectedPackageName = 'MesmerPrism.MetaQuestFileManager',
    [string]$ExpectedPublisher = 'CN=MesmerPrism',
    [string]$KioskBundleManifestPath,
    [switch]$AllowSelfIssuedTrustFailure
)

$ErrorActionPreference = 'Stop'
$ReleaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
$setupPath = Join-Path $ReleaseDirectory 'QuestIonAbleFileManager-Setup.exe'
$packagePath = Join-Path $ReleaseDirectory 'QuestIonAbleFileManager-win-x64.msix'
$appInstallerPath = Join-Path $ReleaseDirectory 'QuestIonAbleFileManager.appinstaller'
$certificatePath = Join-Path $ReleaseDirectory 'QuestIonAbleFileManager.cer'
$providerPath = Join-Path $ReleaseDirectory 'questionable-file-manager-kiosk-v2-provider.exe'
$providerReceiptPath =
    Join-Path $ReleaseDirectory 'questionable-file-manager-kiosk-v2-provider.receipt.json'
$awakeProviderPath =
    Join-Path $ReleaseDirectory 'questionable-file-manager-awake-provider.exe'
$awakeProviderReceiptPath =
    Join-Path $ReleaseDirectory 'questionable-file-manager-awake-provider.receipt.json'
$connectivityProviderPath =
    Join-Path $ReleaseDirectory 'questionable-file-manager-connectivity-provider.exe'
$connectivityProviderReceiptPath =
    Join-Path $ReleaseDirectory 'questionable-file-manager-connectivity-provider.receipt.json'
$receiptPath = Join-Path $ReleaseDirectory 'release-validation.json'
$legacyAliases = [ordered]@{
    'MetaQuestFileManager-Setup.exe' = 'QuestIonAbleFileManager-Setup.exe'
    'MetaQuestFileManager-win-x64.msix' = 'QuestIonAbleFileManager-win-x64.msix'
    'MetaQuestFileManager.appinstaller' = 'QuestIonAbleFileManager.appinstaller'
    'MetaQuestFileManager.cer' = 'QuestIonAbleFileManager.cer'
    'MetaQuestFileManager-win-x64.zip' = 'QuestIonAbleFileManager-win-x64.zip'
    'meta-quest-file-manager-cli-win-x64.zip' = 'questionable-file-manager-cli-win-x64.zip'
}

foreach ($path in @(
    $setupPath,
    $packagePath,
    $appInstallerPath,
    $certificatePath,
    $providerPath,
    $providerReceiptPath,
    $awakeProviderPath,
    $awakeProviderReceiptPath,
    $connectivityProviderPath,
    $connectivityProviderReceiptPath
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release asset was not found: $path"
    }
}

$providerReceipt = Get-Content -Raw -LiteralPath $providerReceiptPath | ConvertFrom-Json
$providerHash =
    (Get-FileHash -LiteralPath $providerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$providerLength = (Get-Item -LiteralPath $providerPath).Length
if ($providerReceipt.schema -cne
        'questionable.file_manager.fleet_kiosk_v2_provider_artifact_receipt.v1' -or
    $providerReceipt.artifact_name -cne
        'questionable-file-manager-kiosk-v2-provider.exe' -or
    $providerReceipt.source_project -cne
        'QuestIonAbleFileManager.FleetKioskV2Provider' -or
    $providerReceipt.sha256 -cne $providerHash -or
    [long]$providerReceipt.size_bytes -ne $providerLength -or
    $providerReceipt.self_contained -ne $true -or
    $providerReceipt.single_file -ne $true -or
    [int]$providerReceipt.sibling_code_files -ne 0 -or
    [int]$providerReceipt.isolated_file_count -ne 1 -or
    $providerReceipt.bundle_extract_base -cne 'caller-private-per-launch' -or
    [int]$providerReceipt.bundle_extract_file_count -lt 0 -or
    [int]$providerReceipt.bundle_extract_file_count -gt 128 -or
    [int]$providerReceipt.bundle_extract_directory_count -lt 0 -or
    [int]$providerReceipt.bundle_extract_directory_count -gt 16 -or
    [long]$providerReceipt.bundle_extract_bytes -lt 0 -or
    [long]$providerReceipt.bundle_extract_bytes -gt 134217728 -or
    [int]$providerReceipt.isolated_top_level_entries_after_run -ne 2 -or
    $providerReceipt.ordinary_apphost_isolation_rejected -ne $true -or
    $providerReceipt.general_cli_dispatch_unreachable -ne $true -or
    [int]$providerReceipt.rejected_argument_shapes -lt 15 -or
    [int]$providerReceipt.exit_codes.verified -ne 0 -or
    [int]$providerReceipt.exit_codes.failed -ne 1 -or
    [int]$providerReceipt.exit_codes.rejected -ne 2 -or
    [int]$providerReceipt.exit_codes.unavailable -ne 3 -or
    [int]$providerReceipt.smoke_exit_code -ne 3 -or
    $providerReceipt.smoke_status -cne 'unavailable' -or
    [int]$providerReceipt.stderr_bytes -ne 0) {
    throw 'Fleet Kiosk v2 provider artifact receipt does not match the validated executable.'
}
$awakeProviderReceipt =
    Get-Content -Raw -LiteralPath $awakeProviderReceiptPath | ConvertFrom-Json
$awakeProviderHash =
    (Get-FileHash -LiteralPath $awakeProviderPath -Algorithm SHA256).Hash.ToLowerInvariant()
$awakeProviderLength = (Get-Item -LiteralPath $awakeProviderPath).Length
if ($awakeProviderReceipt.schema -cne
        'questionable.file_manager.fleet_awake_provider_artifact_receipt.v1' -or
    $awakeProviderReceipt.artifact_name -cne
        'questionable-file-manager-awake-provider.exe' -or
    $awakeProviderReceipt.source_project -cne
        'QuestIonAbleFileManager.FleetAwakeProvider' -or
    $awakeProviderReceipt.sha256 -cne $awakeProviderHash -or
    [long]$awakeProviderReceipt.size_bytes -ne $awakeProviderLength -or
    $awakeProviderReceipt.self_contained -ne $true -or
    $awakeProviderReceipt.single_file -ne $true -or
    [int]$awakeProviderReceipt.sibling_code_files -ne 0 -or
    [int]$awakeProviderReceipt.isolated_file_count -ne 1 -or
    $awakeProviderReceipt.bundle_extract_base -cne 'caller-private-per-launch' -or
    [int]$awakeProviderReceipt.bundle_extract_file_count -lt 0 -or
    [int]$awakeProviderReceipt.bundle_extract_file_count -gt 128 -or
    [int]$awakeProviderReceipt.bundle_extract_directory_count -lt 0 -or
    [int]$awakeProviderReceipt.bundle_extract_directory_count -gt 16 -or
    [long]$awakeProviderReceipt.bundle_extract_bytes -lt 0 -or
    [long]$awakeProviderReceipt.bundle_extract_bytes -gt 134217728 -or
    [int]$awakeProviderReceipt.isolated_top_level_entries_after_run -ne 2 -or
    $awakeProviderReceipt.ordinary_apphost_isolation_rejected -ne $true -or
    $awakeProviderReceipt.general_cli_dispatch_unreachable -ne $true -or
    [int]$awakeProviderReceipt.rejected_argument_shapes -lt 9 -or
    $awakeProviderReceipt.strict_request_rejection -cne 'durationInvalid' -or
    [int]$awakeProviderReceipt.former_duration_maximum_rejected_ms -ne 86400000 -or
    [int]$awakeProviderReceipt.supported_duration_maximum_ms -ne 28800000 -or
    [int]$awakeProviderReceipt.exit_codes.verified -ne 0 -or
    [int]$awakeProviderReceipt.exit_codes.failed -ne 1 -or
    [int]$awakeProviderReceipt.exit_codes.rejected -ne 2 -or
    [int]$awakeProviderReceipt.exit_codes.pending -ne 3 -or
    [int]$awakeProviderReceipt.exit_codes.cancelled -ne 4 -or
    [int]$awakeProviderReceipt.smoke_exit_code -ne 2 -or
    $awakeProviderReceipt.smoke_status -cne 'rejected' -or
    [int]$awakeProviderReceipt.stderr_bytes -ne 0) {
    throw 'Fleet awake provider artifact receipt does not match the validated executable.'
}
$connectivityProviderReceipt =
    Get-Content -Raw -LiteralPath $connectivityProviderReceiptPath | ConvertFrom-Json
$connectivityProviderHash =
    (Get-FileHash -LiteralPath $connectivityProviderPath -Algorithm SHA256).Hash.ToLowerInvariant()
$connectivityProviderLength =
    (Get-Item -LiteralPath $connectivityProviderPath).Length
if ($connectivityProviderReceipt.schema -cne
        'questionable.file_manager.fleet_connectivity_provider_artifact_receipt.v1' -or
    $connectivityProviderReceipt.artifact_name -cne
        'questionable-file-manager-connectivity-provider.exe' -or
    $connectivityProviderReceipt.source_project -cne
        'QuestIonAbleFileManager.FleetConnectivityProvider' -or
    $connectivityProviderReceipt.sha256 -cne $connectivityProviderHash -or
    [long]$connectivityProviderReceipt.size_bytes -ne $connectivityProviderLength -or
    $connectivityProviderReceipt.self_contained -ne $true -or
    $connectivityProviderReceipt.single_file -ne $true -or
    [int]$connectivityProviderReceipt.sibling_code_files -ne 0 -or
    [int]$connectivityProviderReceipt.isolated_file_count -ne 1 -or
    $connectivityProviderReceipt.bundle_extract_base -cne 'caller-private-per-launch' -or
    [int]$connectivityProviderReceipt.bundle_extract_file_count -lt 0 -or
    [int]$connectivityProviderReceipt.bundle_extract_file_count -gt 128 -or
    [int]$connectivityProviderReceipt.bundle_extract_directory_count -lt 0 -or
    [int]$connectivityProviderReceipt.bundle_extract_directory_count -gt 16 -or
    [int]$connectivityProviderReceipt.bundle_extract_launch_directories -ne 11 -or
    [long]$connectivityProviderReceipt.bundle_extract_bytes -lt 0 -or
    [long]$connectivityProviderReceipt.bundle_extract_bytes -gt 134217728 -or
    [int]$connectivityProviderReceipt.isolated_top_level_entries_after_run -ne 2 -or
    $connectivityProviderReceipt.ordinary_apphost_isolation_rejected -ne $true -or
    $connectivityProviderReceipt.general_cli_dispatch_unreachable -ne $true -or
    [int]$connectivityProviderReceipt.rejected_argument_shapes -lt 10 -or
    $connectivityProviderReceipt.private_profile_required -ne $true -or
    $connectivityProviderReceipt.request_schema -cne
        'rusty.fleet.quest_wifi_adb_owner_invocation.v1' -or
    $connectivityProviderReceipt.receipt_schema -cne
        'questionable.file_manager.quest_wifi_adb_receipt.v1' -or
    [int]$connectivityProviderReceipt.exit_codes.verified -ne 0 -or
    [int]$connectivityProviderReceipt.exit_codes.failed -ne 1 -or
    [int]$connectivityProviderReceipt.exit_codes.rejected -ne 2 -or
    [int]$connectivityProviderReceipt.exit_codes.pending -ne 3 -or
    [int]$connectivityProviderReceipt.exit_codes.cancelled -ne 4 -or
    [int]$connectivityProviderReceipt.smoke_exit_code -ne 1 -or
    $connectivityProviderReceipt.smoke_status -cne 'failed' -or
    $connectivityProviderReceipt.smoke_error -cne 'providerProfileUnavailable' -or
    [int]$connectivityProviderReceipt.stderr_bytes -ne 0) {
    throw 'Fleet connectivity provider artifact receipt does not match the validated executable.'
}
foreach ($entry in $legacyAliases.GetEnumerator()) {
    $legacyPath = Join-Path $ReleaseDirectory $entry.Key
    $canonicalPath = Join-Path $ReleaseDirectory $entry.Value
    if (-not (Test-Path -LiteralPath $legacyPath -PathType Leaf)) {
        throw "Required compatibility alias was not found: $legacyPath"
    }
    if ((Get-FileHash -LiteralPath $legacyPath -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $canonicalPath -Algorithm SHA256).Hash) {
        throw "Compatibility alias differs from its canonical asset: $($entry.Key)"
    }
}

function Test-Signature {
    param([Parameter(Mandatory = $true)][string]$Path)

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($null -eq $signature.SignerCertificate) {
        throw "No Authenticode signer was found on $Path."
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "No RFC 3161 timestamp was found on $Path."
    }
    if ($signature.Status -ne 'Valid') {
        $allowed = $AllowSelfIssuedTrustFailure -and $signature.Status -eq 'UnknownError'
        if (-not $allowed) {
            throw "Signature validation failed for $Path with status $($signature.Status): $($signature.StatusMessage)"
        }
    }

    return [pscustomobject]@{
        Path = [IO.Path]::GetFileName($Path)
        Status = [string]$signature.Status
        SignerSubject = $signature.SignerCertificate.Subject
        SignerThumbprint = $signature.SignerCertificate.Thumbprint
        TimestampSubject = $signature.TimeStamperCertificate.Subject
        TimestampNotBefore = $signature.TimeStamperCertificate.NotBefore.ToUniversalTime().ToString('o')
        TimestampNotAfter = $signature.TimeStamperCertificate.NotAfter.ToUniversalTime().ToString('o')
    }
}

$setupSignature = Test-Signature -Path $setupPath
$packageSignature = Test-Signature -Path $packagePath
if ($setupSignature.SignerSubject -ne $ExpectedPublisher) {
    throw "The setup helper signer was '$($setupSignature.SignerSubject)', expected '$ExpectedPublisher'."
}
if ($packageSignature.SignerSubject -ne $ExpectedPublisher) {
    throw "The MSIX signer was '$($packageSignature.SignerSubject)', expected '$ExpectedPublisher'."
}

$certificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadCertificateFromFile($certificatePath)
try {
    if ($certificate.Thumbprint -ne $packageSignature.SignerThumbprint) {
        throw 'The public CER does not match the package signer.'
    }
    if ($certificate.Subject -ne $ExpectedPublisher) {
        throw "The public CER publisher was '$($certificate.Subject)', expected '$ExpectedPublisher'."
    }
}
finally {
    $certificate.Dispose()
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($portableArchiveName in @(
    'QuestIonAbleFileManager-win-x64.zip',
    'questionable-file-manager-cli-win-x64.zip'
)) {
    $portableArchivePath = Join-Path $ReleaseDirectory $portableArchiveName
    $portableArchive = [IO.Compression.ZipFile]::OpenRead($portableArchivePath)
    try {
        $portableEntries = @($portableArchive.Entries | ForEach-Object FullName)
        foreach ($providerEntry in @(
            'questionable-file-manager-kiosk-v2-provider.exe',
            'questionable-file-manager-kiosk-v2-provider.receipt.json',
            'questionable-file-manager-awake-provider.exe',
            'questionable-file-manager-awake-provider.receipt.json',
            'questionable-file-manager-connectivity-provider.exe',
            'questionable-file-manager-connectivity-provider.receipt.json'
        )) {
            if ($portableEntries -notcontains $providerEntry) {
                throw "$portableArchiveName is missing required provider asset $providerEntry."
            }
        }
    }
    finally {
        $portableArchive.Dispose()
    }
}
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entries = @($archive.Entries | ForEach-Object FullName)
    foreach ($required in @('AppxManifest.xml', 'AppxBlockMap.xml', 'AppxSignature.p7x')) {
        if ($entries -notcontains $required) {
            throw "The MSIX package is missing $required."
        }
    }
    if (-not ($entries | Where-Object { $_ -match '(^|/)QuestIonAbleFileManager\.exe$' })) {
        throw 'The MSIX package does not contain QuestIonAbleFileManager.exe.'
    }
}
finally {
    $archive.Dispose()
}

[xml]$appInstaller = Get-Content -LiteralPath $appInstallerPath -Raw
$namespace = [Xml.XmlNamespaceManager]::new($appInstaller.NameTable)
$namespace.AddNamespace('ai', $appInstaller.DocumentElement.NamespaceURI)
$mainPackage = $appInstaller.SelectSingleNode('/ai:AppInstaller/ai:MainPackage', $namespace)
if ($null -eq $mainPackage) { throw 'The App Installer feed is missing MainPackage.' }
if ($mainPackage.Name -ne $ExpectedPackageName) { throw "Unexpected App Installer package name: $($mainPackage.Name)" }
if ($mainPackage.Publisher -ne $ExpectedPublisher) { throw "Unexpected App Installer publisher: $($mainPackage.Publisher)" }
if ($mainPackage.Uri -notmatch '^https://github\.com/MesmerPrism/QuestIonAble-File-Manager/releases/latest/download/') {
    throw "The published App Installer MSIX URI is not release-stable: $($mainPackage.Uri)"
}

$kioskReceipt = $null
if ($KioskBundleManifestPath) {
    $KioskBundleManifestPath = [IO.Path]::GetFullPath($KioskBundleManifestPath)
    if (-not (Test-Path -LiteralPath $KioskBundleManifestPath -PathType Leaf)) {
        throw "The verified Rusty Kiosk manifest was not found: $KioskBundleManifestPath"
    }
    $kioskManifest = Get-Content -Raw -LiteralPath $KioskBundleManifestPath | ConvertFrom-Json
    $kioskReceipt = [ordered]@{
        version = $kioskManifest.version
        source_url = $kioskManifest.source_url
        source_revision = $kioskManifest.source_revision
        signer_sha256 = $kioskManifest.signer_sha256
        bundle_manifest_sha256 = (Get-FileHash -LiteralPath $KioskBundleManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        files = $kioskManifest.files
    }
}

$receipt = [ordered]@{
    schema = 'questionable-file-manager.release-validation.v1'
    validated_at_utc = [DateTime]::UtcNow.ToString('o')
    package_name = $ExpectedPackageName
    package_version = $mainPackage.Version
    publisher = $ExpectedPublisher
    setup_signature = $setupSignature
    package_signature = $packageSignature
    appinstaller_uri = $appInstaller.AppInstaller.Uri
    msix_uri = $mainPackage.Uri
    rusty_kiosk = $kioskReceipt
    fleet_kiosk_v2_provider = [ordered]@{
        artifact_name = 'questionable-file-manager-kiosk-v2-provider.exe'
        source_project = 'QuestIonAbleFileManager.FleetKioskV2Provider'
        sha256 = $providerHash
        size_bytes = $providerLength
        self_contained = $true
        single_file = $true
        bundle_extract_base = 'caller-private-per-launch'
        bundle_extract_file_count = [int]$providerReceipt.bundle_extract_file_count
        bundle_extract_directory_count = [int]$providerReceipt.bundle_extract_directory_count
        bundle_extract_bytes = [long]$providerReceipt.bundle_extract_bytes
        ordinary_apphost_isolation_rejected = $true
        general_cli_dispatch_unreachable = $true
        rejected_argument_shapes = [int]$providerReceipt.rejected_argument_shapes
        exit_codes = [ordered]@{
            verified = 0
            failed = 1
            rejected = 2
            unavailable = 3
        }
        isolated_smoke_status = 'unavailable'
        isolated_smoke_exit_code = 3
        stderr_bytes = 0
    }
    fleet_awake_provider = [ordered]@{
        artifact_name = 'questionable-file-manager-awake-provider.exe'
        source_project = 'QuestIonAbleFileManager.FleetAwakeProvider'
        sha256 = $awakeProviderHash
        size_bytes = $awakeProviderLength
        self_contained = $true
        single_file = $true
        bundle_extract_base = 'caller-private-per-launch'
        bundle_extract_file_count = [int]$awakeProviderReceipt.bundle_extract_file_count
        bundle_extract_directory_count = [int]$awakeProviderReceipt.bundle_extract_directory_count
        bundle_extract_bytes = [long]$awakeProviderReceipt.bundle_extract_bytes
        ordinary_apphost_isolation_rejected = $true
        general_cli_dispatch_unreachable = $true
        rejected_argument_shapes = [int]$awakeProviderReceipt.rejected_argument_shapes
        supported_duration_maximum_ms = 28800000
        exit_codes = [ordered]@{
            verified = 0
            failed = 1
            rejected = 2
            pending = 3
            cancelled = 4
        }
        isolated_smoke_status = 'rejected'
        isolated_smoke_exit_code = 2
        stderr_bytes = 0
    }
    fleet_connectivity_provider = [ordered]@{
        artifact_name = 'questionable-file-manager-connectivity-provider.exe'
        source_project = 'QuestIonAbleFileManager.FleetConnectivityProvider'
        sha256 = $connectivityProviderHash
        size_bytes = $connectivityProviderLength
        self_contained = $true
        single_file = $true
        bundle_extract_base = 'caller-private-per-launch'
        bundle_extract_file_count =
            [int]$connectivityProviderReceipt.bundle_extract_file_count
        bundle_extract_directory_count =
            [int]$connectivityProviderReceipt.bundle_extract_directory_count
        bundle_extract_launch_directories =
            [int]$connectivityProviderReceipt.bundle_extract_launch_directories
        bundle_extract_bytes =
            [long]$connectivityProviderReceipt.bundle_extract_bytes
        ordinary_apphost_isolation_rejected = $true
        general_cli_dispatch_unreachable = $true
        rejected_argument_shapes =
            [int]$connectivityProviderReceipt.rejected_argument_shapes
        private_profile_required = $true
        request_schema = 'rusty.fleet.quest_wifi_adb_owner_invocation.v1'
        receipt_schema = 'questionable.file_manager.quest_wifi_adb_receipt.v1'
        exit_codes = [ordered]@{
            verified = 0
            failed = 1
            rejected = 2
            pending = 3
            cancelled = 4
        }
        isolated_smoke_status = 'failed'
        isolated_smoke_error = 'providerProfileUnavailable'
        isolated_smoke_exit_code = 1
        stderr_bytes = 0
    }
    required_assets = @(
        'QuestIonAbleFileManager-Setup.exe',
        'QuestIonAbleFileManager-win-x64.msix',
        'QuestIonAbleFileManager.appinstaller',
        'QuestIonAbleFileManager.cer',
        'questionable-file-manager-kiosk-v2-provider.exe',
        'questionable-file-manager-kiosk-v2-provider.receipt.json',
        'questionable-file-manager-awake-provider.exe',
        'questionable-file-manager-awake-provider.receipt.json',
        'questionable-file-manager-connectivity-provider.exe',
        'questionable-file-manager-connectivity-provider.receipt.json'
    )
    compatibility_aliases = $legacyAliases
}
$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receiptPath -Encoding utf8
$receipt
