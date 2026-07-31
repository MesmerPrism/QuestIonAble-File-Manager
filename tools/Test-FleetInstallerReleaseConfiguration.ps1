[CmdletBinding()]
param(
    [switch]$RequireOfficialRelease,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ExpectedVersion,
    [string]$ExpectedTag,

    [string[]]$AssemblyPath = @(),

    [string]$PackagePath,

    [string]$SetupExecutablePath,

    [string]$ExpectedSetupSignerCertificateSha256
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot `
    'src\QuestIonAbleFileManager.Core\QuestIonAbleFileManager.Core.csproj'
$configurationSource = Join-Path $repoRoot `
    'src\QuestIonAbleFileManager.Core\FleetInstallerReleaseConfiguration.cs'
$configurationRelative =
    'src/QuestIonAbleFileManager.Core/FleetInstallerReleaseConfiguration.cs'
$outputRoot = Join-Path ([IO.Path]::GetTempPath()) `
    "questionable-file-manager-fleet-release-config-$(
        [Guid]::NewGuid().ToString('N'))"
$metadataPrefix = 'QuestIonAbleFileManager.FleetInstaller.'
$expectedNames = @(
    'ConfigurationVersion'
    'DescriptorUri'
    'DescriptorPublicKeySpkiBase64'
    'DescriptorSignerSpkiSha256'
    'InstallerSignerCertificateSha256'
    'ProvisioningSetupSignerCertificateSha256'
    'Channel'
    'StateRootRelativePath'
)
$ambientConfiguration = [ordered]@{
    FleetInstallerReleaseDescriptorUri =
        'https://attacker.invalid/release.json'
    FleetInstallerDescriptorPublicKeySpkiBase64 = 'ambient-value'
    FleetInstallerDescriptorSignerSpkiSha256 = 'a' * 64
    FleetInstallerSetupSignerCertificateSha256 = 'b' * 64
    FleetInstallerChannel = 'stable'
    FleetInstallerStateRootRelativePath = 'Attacker/State'
}
$forbiddenEnvironmentHooks = @(
    'CustomBeforeMicrosoftCommonTargets'
    'CustomAfterMicrosoftCommonTargets'
    'DirectoryBuildPropsPath'
    'DirectoryBuildTargetsPath'
    'MSBuildProjectExtensionsPath'
    'MSBuildSDKsPath'
    'MSBuildExtensionsPath'
    'MSBuildExtensionsPath32'
    'MSBuildExtensionsPath64'
    'MSBuildUserExtensionsPath'
    'DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR'
    'DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR'
    'DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER'
    'MSBUILD_EXE_PATH'
)

function Read-FleetMetadataBytes {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $assembly = [Reflection.Assembly]::Load($Bytes)
    $values = [ordered]@{}
    foreach ($attribute in $assembly.GetCustomAttributesData() |
        Where-Object AttributeType -eq (
            [Reflection.AssemblyMetadataAttribute]
        )) {
        $key = [string]$attribute.ConstructorArguments[0].Value
        if (-not $key.StartsWith(
                $metadataPrefix,
                [StringComparison]::Ordinal)) {
            continue
        }
        $name = $key.Substring($metadataPrefix.Length)
        if ($values.Contains($name)) {
            throw "Duplicate compiled Fleet installer release field: $name"
        }
        $values[$name] =
            [string]$attribute.ConstructorArguments[1].Value
    }
    return $values
}

function Read-FleetMetadata {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Fleet release validation assembly does not exist: $Path"
    }
    return Read-FleetMetadataBytes ([IO.File]::ReadAllBytes(
        [IO.Path]::GetFullPath($Path)))
}

function Read-FleetMetadataFromPackage {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Fleet release validation package does not exist: $Path"
    }
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead(
        [IO.Path]::GetFullPath($Path))
    try {
        $entries = @($archive.Entries | Where-Object {
            $_.FullName.EndsWith(
                '/QuestIonAbleFileManager.Core.dll',
                [StringComparison]::OrdinalIgnoreCase) -or
            $_.FullName.Equals(
                'QuestIonAbleFileManager.Core.dll',
                [StringComparison]::OrdinalIgnoreCase)
        })
        if ($entries.Count -ne 1) {
            throw 'The unsigned release package must contain exactly one Core assembly.'
        }
        $stream = $entries[0].Open()
        try {
            $memory = [IO.MemoryStream]::new()
            try {
                $stream.CopyTo($memory)
                return Read-FleetMetadataBytes $memory.ToArray()
            }
            finally {
                $memory.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ConfigurationDigest {
    param([Parameter(Mandatory)][Collections.IDictionary]$Values)

    $memory = [IO.MemoryStream]::new()
    try {
        $names = [string[]]@($Values.Keys)
        [Array]::Sort($names, [StringComparer]::Ordinal)
        foreach ($name in $names) {
            foreach ($value in @([string]$name, [string]$Values[$name])) {
                $bytes = [Text.Encoding]::UTF8.GetBytes($value)
                $memory.Write($bytes, 0, $bytes.Length)
                $memory.WriteByte(0)
            }
        }
        return [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($memory.ToArray())
        ).ToLowerInvariant()
    }
    finally {
        $memory.Dispose()
    }
}

function Assert-SetupExecutableProof {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][Collections.IDictionary]$Expected
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Fleet release Setup executable does not exist: $Path"
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = [IO.Path]::GetFullPath($Path)
    $startInfo.ArgumentList.Add(
        '--fleet-release-configuration-proof')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'The unsigned Setup release proof did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'The unsigned Setup release proof timed out.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or
            -not [string]::IsNullOrEmpty($stderr)) {
            throw 'The unsigned Setup release proof failed.'
        }
        $proof = $stdout | ConvertFrom-Json -NoEnumerate
        if ($proof.schema -cne
                'questionable.file_manager.fleet_installer_release_proof.v1' -or
            $proof.field_count -ne $Expected.Count -or
            $proof.configuration_sha256 -cne
                (Get-ConfigurationDigest $Expected)) {
            throw 'The unsigned Setup bundle differs from checked-in Fleet release trust.'
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-SetupSecuritySelfTest {
    param([Parameter(Mandatory)][string]$Path)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = [IO.Path]::GetFullPath($Path)
    $startInfo.ArgumentList.Add('--fleet-replay-security-self-test')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'The Setup replay-security self-test did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'The Setup replay-security self-test timed out.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or
            -not [string]::IsNullOrEmpty($stderr)) {
            throw 'The Setup replay-security self-test failed.'
        }
        $proof = $stdout | ConvertFrom-Json -NoEnumerate
        if ($proof.schema -cne
                'questionable.file_manager.fleet_replay_security_self_test.v1' -or
            $proof.status -cne 'ok' -or
            $proof.acl -cne 'system_admin_write_users_read' -or
            $proof.transaction_lock_acl -cne 'system_admin_only' -or
            $proof.abandoned_lock -cne 'recoverable' -or
            $proof.provisioning_acceptance -cne 'serialized' -or
            $proof.staging -cne
                'unpredictable_and_protected_when_elevated' -or
            $proof.unelevated_writer -cne 'rejected' -or
            $proof.signer_mismatch -cne 'rejected' -or
            $proof.explicit_repair -cne 'required' -or
            $proof.release_artifact_upgrade -cne
                'synthetic_same_signer_state_preserving' -or
            $proof.missing_machine_repair -cne 'fail_closed' -or
            $proof.destructive_reset -cne 'explicit_only' -or
            $proof.forged_partial_evidence -cne 'rejected' -or
            $proof.rollback_readback -cne
                'verified_or_validated_backup_retained' -or
            $proof.rollback_partial_replace -cne
                'reconciled_and_evidence_preserved' -or
            $proof.partial_replace_failure -cne
                'reconciled_and_prior_backup_retained' -or
            $proof.result_local_paths -cne 'absent') {
            throw 'The Setup replay-security self-test proof is invalid.'
        }
    }
    finally {
        $process.Dispose()
    }
}

function Read-CheckedInConfiguration {
    param([Parameter(Mandatory)][string]$Text)

    $allPattern =
        '(?m)^\s*\[assembly:\s*AssemblyMetadata\("QuestIonAbleFileManager\.FleetInstaller\.[^\r\n]*$'
    $exactPattern =
        '(?m)^\s*\[assembly:\s*AssemblyMetadata\("QuestIonAbleFileManager\.FleetInstaller\.(?<name>[A-Za-z]+)",\s*"(?<value>[^"\r\n]*)"\)\]\s*$'
    $all = [regex]::Matches($Text, $allPattern)
    $exact = [regex]::Matches($Text, $exactPattern)
    if ($all.Count -ne $exact.Count) {
        throw 'Fleet release trust must use exact one-line checked-in string literals.'
    }

    $values = [ordered]@{}
    foreach ($match in $exact) {
        $name = $match.Groups['name'].Value
        if ($expectedNames -cnotcontains $name) {
            throw "Unknown checked-in Fleet installer release field: $name"
        }
        if ($values.Contains($name)) {
            throw "Duplicate checked-in Fleet installer release field: $name"
        }
        $values[$name] = $match.Groups['value'].Value
    }
    if ($values.Count -notin @(0, $expectedNames.Count)) {
        throw 'Checked-in Fleet release trust is incomplete.'
    }
    return $values
}

function Assert-ExactMetadata {
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Expected,
        [Parameter(Mandatory)][Collections.IDictionary]$Actual,
        [Parameter(Mandatory)][string]$Evidence
    )

    if ($Expected.Count -ne $Actual.Count) {
        throw "$Evidence does not contain the exact checked-in Fleet metadata field count."
    }
    foreach ($name in $expectedNames) {
        $expectedHas = $Expected.Contains($name)
        $actualHas = $Actual.Contains($name)
        if ($expectedHas -ne $actualHas -or
            ($expectedHas -and $Expected[$name] -cne $Actual[$name])) {
            throw "$Evidence differs from checked-in Fleet release field $name."
        }
    }
}

function Assert-ConfiguredMetadata {
    param([Parameter(Mandatory)][Collections.IDictionary]$Values)

    if ($Values.Count -ne $expectedNames.Count -or
        @($expectedNames | Where-Object {
            -not $Values.Contains($_)
        }).Count -ne 0) {
        throw 'Checked-in Fleet installer release trust must contain exactly eight fields.'
    }
    if ($Values.ConfigurationVersion -cne '2' -or
        $Values.Channel -cnotin @('stable', 'preview', 'dev')) {
        throw 'Checked-in Fleet installer release trust has an invalid version or channel.'
    }
    $expectedUri =
        "https://mesmerprism.com/Rusty-Fleet/metadata/$(
            $Values.Channel)/release.json"
    if ($Values.DescriptorUri -cne $expectedUri) {
        throw 'Checked-in Fleet installer release metadata URI is not canonical.'
    }
    if ($Values.DescriptorSignerSpkiSha256 -notmatch '^[0-9a-f]{64}$' -or
        $Values.InstallerSignerCertificateSha256 -notmatch
            '^[0-9a-f]{64}$' -or
        $Values.ProvisioningSetupSignerCertificateSha256 -notmatch
            '^[0-9a-f]{64}$') {
        throw 'Checked-in Fleet installer release signer pins are invalid.'
    }

    try {
        $spki = [Convert]::FromBase64String(
            $Values.DescriptorPublicKeySpkiBase64)
        if ([Convert]::ToBase64String($spki) -cne
            $Values.DescriptorPublicKeySpkiBase64) {
            throw 'The descriptor SPKI is not canonical base64.'
        }
        $actualPin = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($spki)
        ).ToLowerInvariant()
        if ($actualPin -cne $Values.DescriptorSignerSpkiSha256) {
            throw 'The descriptor SPKI does not match its checked-in pin.'
        }
        $rsa = [Security.Cryptography.RSA]::Create()
        try {
            $read = 0
            $rsa.ImportSubjectPublicKeyInfo($spki, [ref]$read)
            if ($read -ne $spki.Length) {
                throw 'The descriptor SPKI contains trailing data.'
            }
        }
        finally {
            $rsa.Dispose()
        }
    }
    catch {
        throw "Checked-in Fleet descriptor SPKI is invalid: $(
            $_.Exception.Message)"
    }

    $relative = $Values.StateRootRelativePath
    if ([IO.Path]::IsPathFullyQualified($relative)) {
        throw 'Checked-in Fleet state root must be relative.'
    }
    $segments = @($relative -split '[/\\]' |
        Where-Object { $_ -ne '' })
    if ($segments.Count -lt 1 -or
        $segments.Count -gt 4 -or
        ($segments | Where-Object {
            $stem = ($_ -split '\.')[0]
            $_ -notmatch '^[A-Za-z0-9._-]{1,64}$' -or
            $_ -in @('.', '..') -or
            $_.EndsWith('.') -or
            $stem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$'
        }).Count -gt 0 -or
        ($segments -join '/') -cne $relative.Replace('\', '/')) {
        throw 'Checked-in Fleet state root is not a safe canonical relative path.'
    }
}

function Invoke-IsolatedCoreBuild {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Collections.IDictionary]$Properties = [ordered]@{}
    )

    $output = Join-Path $outputRoot "$Name-output"
    $intermediate = Join-Path $outputRoot "$Name-obj"
    $arguments = @(
        'build'
        $project
        '--configuration'
        'Release'
        "-p:OutputPath=$output"
        "-p:MSBuildProjectExtensionsPath=$intermediate\"
        "-p:IntermediateOutputPath=$intermediate\compile\"
        '-p:CustomBeforeMicrosoftCommonTargets='
        '-p:CustomAfterMicrosoftCommonTargets='
    )
    foreach ($entry in $Properties.GetEnumerator()) {
        $arguments += "-p:$($entry.Key)=$($entry.Value)"
    }
    & dotnet @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$Name Fleet release configuration build failed."
    }
    return Join-Path $output 'QuestIonAbleFileManager.Core.dll'
}

$validatorRsa = [Security.Cryptography.RSA]::Create(2048)
try {
    $validatorSpki = $validatorRsa.ExportSubjectPublicKeyInfo()
    $validatorSpkiPin = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($validatorSpki)
    ).ToLowerInvariant()
    Assert-ConfiguredMetadata ([ordered]@{
        ConfigurationVersion = '2'
        DescriptorUri =
            'https://mesmerprism.com/Rusty-Fleet/metadata/stable/release.json'
        DescriptorPublicKeySpkiBase64 =
            [Convert]::ToBase64String($validatorSpki)
        DescriptorSignerSpkiSha256 = $validatorSpkiPin
        InstallerSignerCertificateSha256 = 'c' * 64
        ProvisioningSetupSignerCertificateSha256 = 'd' * 64
        Channel = 'stable'
        StateRootRelativePath =
            'QuestIonAbleFileManager/FleetInstaller'
    })
}
finally {
    $validatorRsa.Dispose()
}

if (-not (Test-Path -LiteralPath $configurationSource -PathType Leaf)) {
    throw 'The checked-in Fleet installer release configuration source is missing.'
}
foreach ($name in $forbiddenEnvironmentHooks) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not [string]::IsNullOrEmpty($value)) {
        throw "Release validation rejects the ambient MSBuild hook $name."
    }
}

$projectText = [IO.File]::ReadAllText($project)
[xml]$projectXml = $projectText
if ($projectXml.Project.Sdk -cne 'Microsoft.NET.Sdk' -or
    @($projectXml.SelectNodes(
        '//*[local-name()="Import" or local-name()="Target" or local-name()="UsingTask" or local-name()="Compile" or local-name()="Sdk" or local-name()="PackageReference" or local-name()="ProjectReference"]'
    )).Count -ne 0) {
    throw 'Core must use only the reviewed Microsoft.NET.Sdk and no custom imports.'
}
$projectDirectory = Split-Path $project
$ancestorDirectories = @()
$cursor = [IO.Path]::GetFullPath($projectDirectory)
while ($true) {
    $ancestorDirectories += $cursor
    $parent = [IO.Directory]::GetParent($cursor)
    if ($null -eq $parent) {
        break
    }
    $cursor = $parent.FullName
}
$discoveredProps = @(
    $ancestorDirectories |
        ForEach-Object { Join-Path $_ 'Directory.Build.props' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
)
$directoryPropsPath = Join-Path $repoRoot 'Directory.Build.props'
if ($discoveredProps.Count -ne 1 -or
    [IO.Path]::GetFullPath($discoveredProps[0]) -cne
        [IO.Path]::GetFullPath($directoryPropsPath)) {
    throw 'Core must resolve the reviewed repository Directory.Build.props.'
}
$directoryPropsText = [IO.File]::ReadAllText($directoryPropsPath)
[xml]$directoryPropsXml = $directoryPropsText
if (@($directoryPropsXml.SelectNodes(
        '//*[local-name()="Import" or local-name()="Target" or local-name()="UsingTask" or local-name()="Compile" or local-name()="Sdk" or local-name()="PackageReference" or local-name()="ProjectReference"]'
    )).Count -ne 0) {
    throw 'The reviewed Directory.Build.props must contain properties only.'
}
foreach ($forbidden in @(
    'QuestIonAbleFileManager.FleetInstaller.'
    'FleetInstallerReleaseDescriptorUri'
    'FleetInstallerReleaseCapability'
    'FleetInstallerReleaseConfiguration.g.'
    '_FleetInstallerReleaseProvenance'
    'CustomBeforeMicrosoftCommonTargets'
    'CustomAfterMicrosoftCommonTargets'
    'DirectoryBuildPropsPath'
    'DirectoryBuildTargetsPath'
)) {
    if ($projectText.Contains($forbidden, [StringComparison]::Ordinal) -or
        $directoryPropsText.Contains(
            $forbidden,
            [StringComparison]::Ordinal)) {
        throw "Core still admits dynamic Fleet release trust through $forbidden."
    }
}
foreach ($path in @(
    $ancestorDirectories | ForEach-Object {
        Join-Path $_ 'Directory.Build.targets'
        Join-Path $_ 'MSBuild.rsp'
        Join-Path $_ 'Directory.Build.rsp'
    }
)) {
    if (Test-Path -LiteralPath $path) {
        throw "Release validation rejects the custom build hook $path."
    }
}

$configurationSourceText = [IO.File]::ReadAllText($configurationSource)
$checkedIn = Read-CheckedInConfiguration $configurationSourceText
$sourceHashBefore = (
    Get-FileHash -LiteralPath $configurationSource -Algorithm SHA256
).Hash.ToLowerInvariant()
$assemblyMetadataPattern =
    '\[assembly:\s*AssemblyMetadata\("QuestIonAbleFileManager\.FleetInstaller\.'
$otherConfigurationSources = @(
    Get-ChildItem -LiteralPath (Split-Path $configurationSource) `
        -Filter '*.cs' -File |
        Where-Object FullName -ne $configurationSource |
        Where-Object {
            [regex]::IsMatch(
                [IO.File]::ReadAllText($_.FullName),
                $assemblyMetadataPattern)
        }
)
if ($otherConfigurationSources.Count -ne 0) {
    throw 'Fleet installer release metadata exists outside its dedicated checked-in source.'
}

$sourceTracked = $false
& git -C $repoRoot ls-files --error-unmatch -- $configurationRelative `
    *> $null
if ($LASTEXITCODE -eq 0) {
    $sourceTracked = $true
}
if ($RequireOfficialRelease) {
    if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
        throw 'Official Fleet release validation requires ExpectedVersion.'
    }
    if (-not $sourceTracked) {
        throw 'Official Fleet release configuration source is not tracked.'
    }
    $dirty = @(& git -C $repoRoot status --porcelain=v1 `
        --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) {
        throw 'Official release builds require an exact clean source commit.'
    }
    $expectedTag = if ($ExpectedTag) { $ExpectedTag } else { "v$ExpectedVersion" }
    if ($expectedTag -notmatch '^v\d+\.\d+\.\d+(?:-alpha\.[1-9]\d*)?$') {
        throw 'Official Fleet release validation received a non-canonical tag.'
    }
    $tags = @(& git -C $repoRoot tag --points-at HEAD)
    if ($LASTEXITCODE -ne 0 -or $tags -cnotcontains $expectedTag) {
        throw "Official release commit must carry exact tag $expectedTag."
    }
}

try {
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    $baselineAssembly = Invoke-IsolatedCoreBuild 'baseline'
    $baseline = Read-FleetMetadata $baselineAssembly
    Assert-ExactMetadata $checkedIn $baseline 'Baseline Core assembly'

    $ambientAssembly = Invoke-IsolatedCoreBuild `
        'ambient' $ambientConfiguration
    $ambient = Read-FleetMetadata $ambientAssembly
    Assert-ExactMetadata $checkedIn $ambient 'Ambient-property Core assembly'

    if ($checkedIn.Count -eq $expectedNames.Count) {
        Assert-ConfiguredMetadata $checkedIn
    }

    $validatedBinaries = 0
    foreach ($path in $AssemblyPath) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }
        Assert-ExactMetadata `
            $checkedIn `
            (Read-FleetMetadata $path) `
            "Release assembly $path"
        $validatedBinaries++
    }
    if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
        Assert-ExactMetadata `
            $checkedIn `
            (Read-FleetMetadataFromPackage $PackagePath) `
            "Unsigned release package $PackagePath"
        $validatedBinaries++
    }
    if (-not [string]::IsNullOrWhiteSpace($SetupExecutablePath)) {
        Assert-SetupExecutableProof `
            $SetupExecutablePath `
            $checkedIn
        Assert-SetupSecuritySelfTest $SetupExecutablePath
        $validatedBinaries++
    }
    if (-not [string]::IsNullOrWhiteSpace(
            $ExpectedSetupSignerCertificateSha256) -and
        $ExpectedSetupSignerCertificateSha256 -cne
            $checkedIn.ProvisioningSetupSignerCertificateSha256) {
        throw 'The signing certificate does not match the reviewed QFM Setup signer pin.'
    }

    $sourceHashAfter = (
        Get-FileHash -LiteralPath $configurationSource -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($sourceHashBefore -cne $sourceHashAfter) {
        throw 'The checked-in Fleet release configuration changed during validation.'
    }

    [pscustomobject]@{
        schema =
            'questionable.file_manager.fleet_installer_release_configuration_test.v3'
        authority = 'checked-in-reviewed-release-source'
        source_sha256 = $sourceHashAfter
        source_tracked = $sourceTracked
        official_release_checks = [bool]$RequireOfficialRelease
        compiled_field_count = $baseline.Count
        ambient_property_count = $ambientConfiguration.Count
        ambient_properties_inert = $true
        exact_compiled_values_match_source = $true
        validated_release_binary_count = $validatedBinaries
        configured_validator_self_test = $true
        custom_build_hooks_absent = $true
        status = 'passed'
    } | ConvertTo-Json -Compress
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedOutputRoot = [IO.Path]::GetFullPath($outputRoot)
    if ($resolvedOutputRoot.StartsWith(
            $tempRoot,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedOutputRoot)) {
        Remove-Item -LiteralPath $resolvedOutputRoot -Recurse -Force
    }
}
