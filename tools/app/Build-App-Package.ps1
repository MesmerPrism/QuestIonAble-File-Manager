[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\release'),
    # Update compatibility contract: do not rename this signed package identity.
    [string]$PackageName = 'MesmerPrism.MetaQuestFileManager',
    [string]$Publisher = 'CN=MesmerPrism',
    [string]$DisplayName = 'QuestIonAble File Manager',
    [string]$PackageFileName = 'QuestIonAbleFileManager-win-x64.msix',
    [string]$AppInstallerFileName = 'QuestIonAbleFileManager.appinstaller',
    [string]$CertificateFileName = 'QuestIonAbleFileManager.cer',
    [string]$PackageUri = 'https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/latest/download/QuestIonAbleFileManager-win-x64.msix',
    [string]$AppInstallerUri = 'https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/latest/download/QuestIonAbleFileManager.appinstaller',
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$PackageVersion,
    [string]$ReleaseTag,
    [switch]$Unsigned
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$packageProject = Join-Path $repoRoot 'src\QuestIonAbleFileManager.App.Package\QuestIonAbleFileManager.App.Package.wapproj'
$manifestPath = Join-Path $repoRoot 'src\QuestIonAbleFileManager.App.Package\Package.appxmanifest'
$appInstallerTemplatePath = Join-Path $repoRoot 'src\QuestIonAbleFileManager.App.Package\QuestIonAbleFileManager.appinstaller.template'
$entryProject = Join-Path $repoRoot 'src\QuestIonAbleFileManager.App\QuestIonAbleFileManager.App.csproj'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$packageVersion = if ($PackageVersion) { $PackageVersion } else { "$Version.0" }
if ($packageVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "PackageVersion must be a four-part numeric version; got $packageVersion."
}

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $matches = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe'
        if ($LASTEXITCODE -eq 0 -and $matches) {
            return $matches | Select-Object -First 1
        }
    }

    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2026\Community\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2026\BuildTools\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe')
    )) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw 'MSBuild.exe with the MSIX/Desktop Bridge workload was not found.'
}

function Find-WindowsSdkTool {
    param([Parameter(Mandatory = $true)][string]$ToolName)

    $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem -LiteralPath $sdkBin -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [Version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$ToolName" } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
    if (-not $tool) {
        throw "$ToolName was not found in the Windows SDK."
    }

    return $tool
}

function Resolve-UapSdkVersion {
    $platformRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Platforms\UAP'
    $versions = Get-ChildItem -LiteralPath $platformRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'Platform.xml')) } |
        Sort-Object { [Version]$_.Name } -Descending
    if (-not $versions) {
        throw 'No installed UAP platform SDK was found.'
    }

    $preferred = $versions | Where-Object Name -eq '10.0.19041.0' | Select-Object -First 1
    return ($preferred ?? $versions[0]).Name
}

function Initialize-DotNetSdkResolver {
    $dotnet = Get-Command dotnet -ErrorAction Stop
    $dotnetRoot = Split-Path -Parent $dotnet.Source
    $sdkVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $sdkDirectory = Join-Path $dotnetRoot "sdk\$sdkVersion"
    if (-not (Test-Path -LiteralPath $sdkDirectory)) {
        throw ".NET SDK $sdkVersion is required by global.json but was not found under $dotnetRoot."
    }

    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR = $dotnetRoot
    $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR = Join-Path $sdkDirectory 'Sdks'
    $env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER = $sdkVersion
}

if (-not $Unsigned) {
    if ([string]::IsNullOrWhiteSpace($CertificatePath) -or -not (Test-Path -LiteralPath $CertificatePath)) {
        throw 'A valid -CertificatePath is required for a signed package.'
    }
    if ([string]::IsNullOrWhiteSpace($CertificatePassword)) {
        throw '-CertificatePassword is required for a signed package.'
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$buildRoot = Join-Path $repoRoot 'artifacts\package-build'
if (Test-Path -LiteralPath $buildRoot) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

$originalManifestBytes = [IO.File]::ReadAllBytes($manifestPath)
$originalPackageProjectBytes = [IO.File]::ReadAllBytes($packageProject)
try {
    [xml]$manifest = [Text.Encoding]::UTF8.GetString($originalManifestBytes)
    $namespace = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespace.AddNamespace('foundation', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespace.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $identity = $manifest.SelectSingleNode('/foundation:Package/foundation:Identity', $namespace)
    $display = $manifest.SelectSingleNode('/foundation:Package/foundation:Properties/foundation:DisplayName', $namespace)
    $visual = $manifest.SelectSingleNode('/foundation:Package/foundation:Applications/foundation:Application/uap:VisualElements', $namespace)
    $identity.SetAttribute('Name', $PackageName)
    $identity.SetAttribute('Publisher', $Publisher)
    $identity.SetAttribute('Version', $packageVersion)
    $display.InnerText = $DisplayName
    $visual.SetAttribute('DisplayName', $DisplayName)
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($manifestPath, $settings)
    try { $manifest.Save($writer) } finally { $writer.Dispose() }

    Initialize-DotNetSdkResolver
    $uapSdkVersion = Resolve-UapSdkVersion
    [xml]$packageProjectXml = [Text.Encoding]::UTF8.GetString($originalPackageProjectBytes)
    $projectNamespace = [Xml.XmlNamespaceManager]::new($packageProjectXml.NameTable)
    $projectNamespace.AddNamespace('msbuild', 'http://schemas.microsoft.com/developer/msbuild/2003')
    $targetPlatformNode = $packageProjectXml.SelectSingleNode('//msbuild:TargetPlatformVersion', $projectNamespace)
    if ($null -eq $targetPlatformNode) { throw 'The package project has no TargetPlatformVersion node.' }
    $targetPlatformNode.InnerText = $uapSdkVersion
    $projectSettings = [Xml.XmlWriterSettings]::new()
    $projectSettings.Encoding = [Text.UTF8Encoding]::new($false)
    $projectSettings.Indent = $true
    $projectWriter = [Xml.XmlWriter]::Create($packageProject, $projectSettings)
    try { $packageProjectXml.Save($projectWriter) } finally { $projectWriter.Dispose() }

    & dotnet restore $entryProject --runtime win-x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    $msbuild = Find-MSBuild
    $appPackagesRoot = Join-Path $buildRoot 'AppPackages'
    $msbuildArguments = @(
        $packageProject,
        '/restore',
        '/p:Configuration=Release',
        '/p:Platform=x64',
        '/p:UapAppxPackageBuildMode=SideLoadOnly',
        '/p:AppxBundle=Never',
        "/p:AppxPackageDir=$appPackagesRoot\",
        '/p:RuntimeIdentifier=win-x64',
        "/p:Version=$Version",
        "/p:AssemblyVersion=$packageVersion",
        "/p:FileVersion=$packageVersion",
        "/p:InformationalVersion=$Version",
        '/p:GenerateAppInstallerFile=False',
        '/p:AppxPackageSigningEnabled=False'
    )
    & $msbuild @msbuildArguments
    if ($LASTEXITCODE -ne 0) { throw "MSIX package build failed with exit code $LASTEXITCODE" }

    $builtPackage = Get-ChildItem -LiteralPath $appPackagesRoot -Recurse -Filter '*.msix' |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $builtPackage) { throw 'The WAP build did not produce an MSIX package.' }

    $packageOutputPath = Join-Path $OutputDirectory $PackageFileName
    Copy-Item -LiteralPath $builtPackage.FullName -Destination $packageOutputPath -Force

    if (-not $Unsigned) {
        # Restore the exact checked-in project inputs before the final release
        # trust gate. The package is still unsigned at this point.
        [IO.File]::WriteAllBytes($manifestPath, $originalManifestBytes)
        [IO.File]::WriteAllBytes(
            $packageProject,
            $originalPackageProjectBytes)
        $resolverEnvironment = [ordered]@{}
        foreach ($name in @(
            'DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR'
            'DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR'
            'DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER'
        )) {
            $resolverEnvironment[$name] =
                [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, $null)
        }
        try {
            & (Join-Path $repoRoot `
                'tools\Test-FleetInstallerReleaseConfiguration.ps1') `
                -RequireOfficialRelease `
                -ExpectedVersion $Version `
                -ExpectedTag $ReleaseTag `
                -PackagePath $packageOutputPath
            if ($LASTEXITCODE -ne 0) {
                throw 'Unsigned MSIX Fleet release trust validation failed.'
            }
        }
        finally {
            foreach ($entry in $resolverEnvironment.GetEnumerator()) {
                [Environment]::SetEnvironmentVariable(
                    $entry.Key,
                    $entry.Value)
            }
        }

        $signTool = Find-WindowsSdkTool -ToolName 'signtool.exe'
        & $signTool sign /fd SHA256 /f ([IO.Path]::GetFullPath($CertificatePath)) /p $CertificatePassword /tr $TimestampUrl /td SHA256 $packageOutputPath
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $PackageFileName with exit code $LASTEXITCODE" }

        $certificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile(
            [IO.Path]::GetFullPath($CertificatePath),
            $CertificatePassword,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        try {
            [IO.File]::WriteAllBytes(
                (Join-Path $OutputDirectory $CertificateFileName),
                $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        }
        finally {
            $certificate.Dispose()
        }
    }

    $appInstaller = Get-Content -LiteralPath $appInstallerTemplatePath -Raw
    $appInstaller = $appInstaller.Replace('{{APPINSTALLER_URI}}', [Security.SecurityElement]::Escape($AppInstallerUri))
    $appInstaller = $appInstaller.Replace('{{PACKAGE_VERSION}}', $packageVersion)
    $appInstaller = $appInstaller.Replace('{{PACKAGE_NAME}}', [Security.SecurityElement]::Escape($PackageName))
    $appInstaller = $appInstaller.Replace('{{PUBLISHER}}', [Security.SecurityElement]::Escape($Publisher))
    $appInstaller = $appInstaller.Replace('{{MSIX_URI}}', [Security.SecurityElement]::Escape($PackageUri))
    [IO.File]::WriteAllText(
        (Join-Path $OutputDirectory $AppInstallerFileName),
        $appInstaller,
        [Text.UTF8Encoding]::new($false))

    [pscustomobject]@{
        Package = $packageOutputPath
        AppInstaller = (Join-Path $OutputDirectory $AppInstallerFileName)
        Certificate = if ($Unsigned) { $null } else { Join-Path $OutputDirectory $CertificateFileName }
        Identity = $PackageName
        Publisher = $Publisher
        Version = $packageVersion
        TargetPlatformVersion = $uapSdkVersion
    }
}
finally {
    [IO.File]::WriteAllBytes($manifestPath, $originalManifestBytes)
    [IO.File]::WriteAllBytes($packageProject, $originalPackageProjectBytes)
}
