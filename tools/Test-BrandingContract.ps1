[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $content = Get-Content -LiteralPath (Join-Path $repoRoot $Path) -Raw
    if (-not $content.Contains($Value, [StringComparison]::Ordinal)) {
        throw "$Path does not contain the required branding contract: $Value"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $content = Get-Content -LiteralPath (Join-Path $repoRoot $Path) -Raw
    if ($content.Contains($Value, [StringComparison]::Ordinal)) {
        throw "$Path contains forbidden provider/release wiring: $Value"
    }
}

$trackedFormerPaths = @(
    & git -C $repoRoot ls-files |
        Where-Object { $_ -match 'Meta-Quest-File-Manager|MetaQuestFileManager|meta-quest-file-manager' }
)
if ($trackedFormerPaths.Count -gt 0) {
    throw "Former-name tracked paths remain:`n$($trackedFormerPaths -join "`n")"
}

foreach ($path in @(
    'QuestIonAbleFileManager.slnx',
    'src\QuestIonAbleFileManager.App\QuestIonAbleFileManager.App.csproj',
    'src\QuestIonAbleFileManager.Cli\QuestIonAbleFileManager.Cli.csproj',
    'src\QuestIonAbleFileManager.Core\QuestIonAbleFileManager.Core.csproj',
    'src\QuestIonAbleFileManager.FleetKioskV2Provider\QuestIonAbleFileManager.FleetKioskV2Provider.csproj',
    'src\QuestIonAbleFileManager.FleetAwakeProvider\QuestIonAbleFileManager.FleetAwakeProvider.csproj',
    'src\QuestIonAbleFileManager.FleetConnectivityProvider\QuestIonAbleFileManager.FleetConnectivityProvider.csproj',
    'src\QuestIonAbleFileManager.Setup\QuestIonAbleFileManager.Setup.csproj'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $path) -PathType Leaf)) {
        throw "Canonical project path is missing: $path"
    }
}

Assert-Contains 'README.md' '# QuestIonAble File Manager'
Assert-Contains 'site\index.html' '<title>QuestIonAble File Manager · Mesmer Prism</title>'
Assert-Contains 'site\index.html' 'https://mesmerprism.com/QuestIonAble-File-Manager/'
Assert-Contains 'site\index.html' 'https://mesmerprism.com/Rusty-Fleet/'
Assert-Contains 'site\index.html' 'The Fleet integration is still a development preview'
Assert-Contains 'site\index.html' 'It does not prove that a provider is installed, healthy, authorized, connected to a headset, or permitted to execute anything.'
Assert-Contains 'src\QuestIonAbleFileManager.Cli\QuestIonAbleFileManager.Cli.csproj' '<AssemblyName>questionable-file-manager</AssemblyName>'
Assert-Contains 'src\QuestIonAbleFileManager.FleetKioskV2Provider\QuestIonAbleFileManager.FleetKioskV2Provider.csproj' '<AssemblyName>questionable-file-manager-kiosk-v2-provider</AssemblyName>'
Assert-Contains 'src\QuestIonAbleFileManager.FleetAwakeProvider\QuestIonAbleFileManager.FleetAwakeProvider.csproj' '<AssemblyName>questionable-file-manager-awake-provider</AssemblyName>'
Assert-Contains 'src\QuestIonAbleFileManager.FleetAwakeProvider\QuestIonAbleFileManager.FleetAwakeProvider.csproj' '../QuestIonAbleFileManager.Core/QuestIonAbleFileManager.Core.csproj'
Assert-Contains 'src\QuestIonAbleFileManager.FleetAwakeProvider\Program.cs' 'QuestAwakeProviderSubprocessHost'
Assert-Contains 'tools\Test-FleetAwakeProviderArtifact.ps1' 'QuestIonAbleFileManager.FleetAwakeProvider.csproj'
Assert-NotContains 'src\QuestIonAbleFileManager.FleetAwakeProvider\Program.cs' 'CliApplication'
Assert-NotContains 'src\QuestIonAbleFileManager.FleetAwakeProvider\Program.cs' 'OperatorCommand'
Assert-NotContains 'src\QuestIonAbleFileManager.FleetAwakeProvider\QuestIonAbleFileManager.FleetAwakeProvider.csproj' 'QuestIonAbleFileManager.Cli'
Assert-Contains 'tools\Test-FleetAwakeProviderArtifact.ps1' '-p:PublishSingleFile=true'
Assert-Contains 'tools\Test-FleetAwakeProviderArtifact.ps1' '--self-contained true'
Assert-Contains 'src\QuestIonAbleFileManager.FleetConnectivityProvider\QuestIonAbleFileManager.FleetConnectivityProvider.csproj' '<AssemblyName>questionable-file-manager-connectivity-provider</AssemblyName>'
Assert-Contains 'src\QuestIonAbleFileManager.FleetConnectivityProvider\QuestIonAbleFileManager.FleetConnectivityProvider.csproj' '../QuestIonAbleFileManager.Core/QuestIonAbleFileManager.Core.csproj'
Assert-Contains 'src\QuestIonAbleFileManager.FleetConnectivityProvider\Program.cs' 'QuestConnectivityProviderSubprocessHost'
Assert-Contains 'tools\Test-FleetConnectivityProviderArtifact.ps1' 'QuestIonAbleFileManager.FleetConnectivityProvider.csproj'
Assert-NotContains 'src\QuestIonAbleFileManager.FleetConnectivityProvider\Program.cs' 'CliApplication'
Assert-NotContains 'src\QuestIonAbleFileManager.FleetConnectivityProvider\Program.cs' 'OperatorCommand'
Assert-NotContains 'src\QuestIonAbleFileManager.FleetConnectivityProvider\QuestIonAbleFileManager.FleetConnectivityProvider.csproj' 'QuestIonAbleFileManager.Cli'
Assert-Contains 'tools\Test-FleetConnectivityProviderArtifact.ps1' '-p:PublishSingleFile=true'
Assert-Contains 'tools\Test-FleetConnectivityProviderArtifact.ps1' '--self-contained true'
Assert-Contains 'src\QuestIonAbleFileManager.FleetKioskV2Provider\QuestIonAbleFileManager.FleetKioskV2Provider.csproj' '../QuestIonAbleFileManager.Core/QuestIonAbleFileManager.Core.csproj'
Assert-Contains 'src\QuestIonAbleFileManager.FleetKioskV2Provider\Program.cs' 'RustyKioskV2CatalogSubprocessHost'
Assert-Contains 'tools\Test-FleetKioskV2ProviderArtifact.ps1' 'QuestIonAbleFileManager.FleetKioskV2Provider.csproj'
Assert-NotContains 'src\QuestIonAbleFileManager.FleetKioskV2Provider\Program.cs' 'CliApplication'
Assert-NotContains 'src\QuestIonAbleFileManager.FleetKioskV2Provider\Program.cs' 'AdbClient'
Assert-NotContains 'src\QuestIonAbleFileManager.FleetKioskV2Provider\Program.cs' 'OperatorCommand'
Assert-NotContains 'src\QuestIonAbleFileManager.FleetKioskV2Provider\QuestIonAbleFileManager.FleetKioskV2Provider.csproj' 'QuestIonAbleFileManager.Cli'
Assert-NotContains 'tools\Test-FleetKioskV2ProviderArtifact.ps1' 'PublishedCliPath'
Assert-Contains 'src\QuestIonAbleFileManager.Core\AdbLocator.cs' 'QUESTIONABLE_FILE_MANAGER_ADB'

# This signed identity is intentionally the sole former product identifier
# required for in-place updates from 0.3.x.
Assert-Contains 'src\QuestIonAbleFileManager.App.Package\Package.appxmanifest' 'Name="MesmerPrism.MetaQuestFileManager"'
Assert-Contains 'src\QuestIonAbleFileManager.App.Package\Package.appxmanifest' '<DisplayName>QuestIonAble File Manager</DisplayName>'
Assert-Contains 'tools\app\Invoke-ReleaseBuild.ps1' "'MetaQuestFileManager.appinstaller' = 'QuestIonAbleFileManager.appinstaller'"
Assert-Contains 'tools\app\Invoke-ReleaseBuild.ps1' "'meta-quest-file-manager-cli-win-x64.zip' = 'questionable-file-manager-cli-win-x64.zip'"
Assert-Contains 'tools\app\Invoke-ReleaseBuild.ps1' "'questionable-file-manager-kiosk-v2-provider.exe'"
Assert-Contains 'tools\app\Test-ReleaseAssets.ps1' "'questionable-file-manager-kiosk-v2-provider.exe'"
Assert-Contains 'tools\app\Invoke-ReleaseBuild.ps1' 'Test-FleetAwakeProviderArtifact.ps1'
Assert-Contains 'tools\app\Invoke-ReleaseBuild.ps1' "'questionable-file-manager-awake-provider.exe'"
Assert-Contains 'tools\app\Invoke-ReleaseBuild.ps1' "'questionable-file-manager-awake-provider.receipt.json'"
Assert-Contains 'tools\app\Test-ReleaseAssets.ps1' "'questionable-file-manager-awake-provider.exe'"
Assert-Contains 'tools\app\Test-ReleaseAssets.ps1' "'questionable-file-manager-awake-provider.receipt.json'"
Assert-Contains 'tools\app\Test-ReleaseAssets.ps1' 'fleet_awake_provider = [ordered]@{'
Assert-Contains 'docs\release-workflow.md' '`questionable-file-manager-awake-provider.exe`'
Assert-Contains 'tools\app\Invoke-ReleaseBuild.ps1' 'Test-FleetConnectivityProviderArtifact.ps1'
Assert-Contains 'tools\app\Invoke-ReleaseBuild.ps1' "'questionable-file-manager-connectivity-provider.exe'"
Assert-Contains 'tools\app\Invoke-ReleaseBuild.ps1' "'questionable-file-manager-connectivity-provider.receipt.json'"
Assert-Contains 'tools\app\Test-ReleaseAssets.ps1' "'questionable-file-manager-connectivity-provider.exe'"
Assert-Contains 'tools\app\Test-ReleaseAssets.ps1' "'questionable-file-manager-connectivity-provider.receipt.json'"
Assert-Contains 'tools\app\Test-ReleaseAssets.ps1' 'fleet_connectivity_provider = [ordered]@{'
Assert-Contains 'tools\app\Test-ReleaseAssets.ps1' '$connectivityProviderLaunchDirectories -ne'
Assert-Contains 'tools\app\Test-ReleaseAssets.ps1' '($connectivityProviderRejectedShapes + 2)'
Assert-Contains 'docs\release-workflow.md' '`questionable-file-manager-connectivity-provider.exe`'
Assert-Contains 'docs\fleet-integration.md' '`questionable-file-manager-kiosk-v2-provider.exe`'
Assert-Contains 'tools\Test-FleetKioskV2ProviderArtifact.ps1' '-p:PublishSingleFile=true'
Assert-Contains 'tools\Test-FleetKioskV2ProviderArtifact.ps1' '--self-contained true'
Assert-Contains 'src\QuestIonAbleFileManager.Core\ProviderCapabilityDiscovery.cs' 'rusty.quest.workflow.provider_capability_discovery.v1'
Assert-Contains 'src\QuestIonAbleFileManager.Core\QuestAwakeProvider.cs' 'ProviderCapabilityDiscoveryProjection.CreateAwake'
Assert-Contains 'src\QuestIonAbleFileManager.Core\QuestConnectivityProvider.cs' 'ProviderCapabilityDiscoveryProjection.CreateConnectivity'
Assert-Contains 'src\QuestIonAbleFileManager.Core\RustyKioskV2CatalogSubprocessHost.cs' 'ProviderCapabilityDiscoveryProjection.CreateKioskCatalog'
Assert-Contains 'tools\Test-FleetAwakeProviderArtifact.ps1' 'description_stdin_unread = $true'
Assert-Contains 'tools\Test-FleetConnectivityProviderArtifact.ps1' 'description_stdin_unread = $true'
Assert-Contains 'tools\Test-FleetKioskV2ProviderArtifact.ps1' 'description_stdin_unread = $true'
Assert-Contains 'README.md' '`rusty.quest.workflow.provider_capability_discovery.v1`'
Assert-Contains 'docs\architecture.md' '`ProviderCapabilityDiscoveryProjection`'

$currentSurfaces = @(
    'site\index.html',
    'site\site.webmanifest',
    'src\QuestIonAbleFileManager.App\MainWindow.xaml',
    'src\QuestIonAbleFileManager.App\MainWindow.xaml.cs',
    'src\QuestIonAbleFileManager.Cli\Program.cs',
    'src\QuestIonAbleFileManager.Setup\Program.cs'
)
foreach ($relativePath in $currentSurfaces) {
    $content = Get-Content -LiteralPath (Join-Path $repoRoot $relativePath) -Raw
    if ($content -match 'Meta Quest File Manager|Meta-Quest-File-Manager') {
        throw "Former public branding remains on current surface: $relativePath"
    }
}

Write-Output 'QuestIonAble File Manager branding and compatibility contract passed.'
