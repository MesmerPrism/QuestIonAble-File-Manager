[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ContractRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedContractCommit =
    'fc476166f9c05f941dff7e9183f5c893426c05ca'
$expectedContractTree =
    'dbb7d894e60626f48ba51f88bdecff7429c9997e'
$repoRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$ContractRoot = [IO.Path]::GetFullPath($ContractRoot)
$validatorPath = Join-Path $ContractRoot (
    'scripts\Test-AgentExecutionContracts.ps1')
$schemaPath = Join-Path $ContractRoot (
    'schemas\rusty.quest.workflow.' +
    'provider_capability_discovery.v1.schema.json')

if (-not (Test-Path -LiteralPath $validatorPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) {
    throw 'The pinned provider-discovery contract files are unavailable.'
}

$contractCommit = (
    & git -C $ContractRoot rev-parse HEAD
).Trim()
if ($LASTEXITCODE -ne 0 -or
    $contractCommit -cne $expectedContractCommit) {
    throw "Provider-discovery contract commit drift: $contractCommit"
}
$contractTree = (
    & git -C $ContractRoot show -s --format='%T' HEAD
).Trim()
if ($LASTEXITCODE -ne 0 -or
    $contractTree -cne $expectedContractTree) {
    throw "Provider-discovery contract tree drift: $contractTree"
}

$providers = @(
    [pscustomobject]@{
        name = 'awake'
        provider_id =
            'questionable-file-manager.quest-awake-provider'
        project = (
            'src\QuestIonAbleFileManager.FleetAwakeProvider\' +
            'QuestIonAbleFileManager.FleetAwakeProvider.csproj')
    },
    [pscustomobject]@{
        name = 'connectivity'
        provider_id =
            'questionable-file-manager.quest-connectivity-provider'
        project = (
            'src\QuestIonAbleFileManager.FleetConnectivityProvider\' +
            'QuestIonAbleFileManager.FleetConnectivityProvider.csproj')
    },
    [pscustomobject]@{
        name = 'kiosk-catalog'
        provider_id =
            'questionable-file-manager.kiosk-v2-catalog-provider'
        project = (
            'src\QuestIonAbleFileManager.FleetKioskV2Provider\' +
            'QuestIonAbleFileManager.FleetKioskV2Provider.csproj')
    }
)

$temporaryRoot = [IO.Path]::GetFullPath(
    (Join-Path ([IO.Path]::GetTempPath()) (
        'questionable-file-manager-provider-discovery-' +
        [Guid]::NewGuid().ToString('N'))))
$descriptors = @()

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    foreach ($provider in $providers) {
        $projectPath = Join-Path $repoRoot $provider.project
        $output = @(
            & dotnet run `
                --project $projectPath `
                --configuration Release `
                -- `
                --describe-json 2>&1
        )
        if ($LASTEXITCODE -ne 0) {
            throw (
                "Provider description failed for $($provider.name): " +
                ($output -join [Environment]::NewLine))
        }
        $json = ($output -join [Environment]::NewLine).Trim()
        try {
            $descriptor = $json |
                ConvertFrom-Json -Depth 100 -DateKind String
        }
        catch {
            throw (
                "Provider description was not one JSON document for " +
                "$($provider.name).")
        }
        if ($descriptor.schema -cne
                'rusty.quest.workflow.provider_capability_discovery.v1' -or
            $descriptor.provider.id -cne $provider.provider_id -or
            $descriptor.authorizes_execution -ne $false -or
            $descriptor.target_specific -ne $false) {
            throw "Provider description binding failed for $($provider.name)."
        }

        $descriptorPath = Join-Path $temporaryRoot (
            "$($provider.name).json")
        [IO.File]::WriteAllText(
            $descriptorPath,
            $json + "`n",
            [Text.UTF8Encoding]::new($false))
        $validationOutput = @(
            & pwsh `
                -NoProfile `
                -ExecutionPolicy Bypass `
                -File $validatorPath `
                -Root $ContractRoot `
                -ProviderDiscoveryPath $descriptorPath 2>&1
        )
        if ($LASTEXITCODE -ne 0 -or
            ($validationOutput -join "`n") -notmatch
                'Provider discovery semantic validation passed') {
            throw (
                "Shared provider-discovery validation failed for " +
                "$($provider.name): " +
                ($validationOutput -join [Environment]::NewLine))
        }
        $descriptors += $descriptor
    }

    if (@($descriptors.provider.id | Sort-Object -Unique).Count -ne 3) {
        throw 'Provider descriptions must carry three distinct provider IDs.'
    }

    [ordered]@{
        schema =
            'questionable.file_manager.provider_capability_discovery_gate.v1'
        contract_commit = $contractCommit
        contract_tree = $contractTree
        descriptors = $descriptors
        semantic_validation = 'passed'
    } | ConvertTo-Json -Depth 100 -Compress
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath(
            (Resolve-Path -LiteralPath $temporaryRoot).Path)
        $systemTemporaryRoot =
            [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTemporaryRoot.StartsWith(
                $systemTemporaryRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedTemporaryRoot) -notmatch
                '^questionable-file-manager-provider-discovery-[0-9a-f]{32}$') {
            throw (
                'Refusing to remove unexpected provider-discovery ' +
                "temporary directory $resolvedTemporaryRoot.")
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
