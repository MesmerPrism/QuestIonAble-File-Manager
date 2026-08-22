[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$CacheRoot
)

Set-StrictMode -Version Latest

$modulePath = Join-Path $PSScriptRoot 'PinnedSdkBootstrap.psm1'
Import-Module $modulePath -Force

$parameters = @{ RepositoryRoot = $RepositoryRoot }
if (-not [string]::IsNullOrWhiteSpace($CacheRoot)) {
    $parameters.CacheRoot = $CacheRoot
}

Invoke-QfmPinnedSdkBootstrap @parameters | ConvertTo-Json -Depth 6
