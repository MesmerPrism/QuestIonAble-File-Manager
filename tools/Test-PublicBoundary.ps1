[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$trackedFiles = @(& git -C $repoRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not enumerate tracked files.'
}
$untrackedFiles = @(& git -C $repoRoot ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not enumerate untracked public files.'
}
$repositoryFiles = @($trackedFiles + $untrackedFiles | Sort-Object -Unique | Where-Object {
    Test-Path -LiteralPath (Join-Path $repoRoot $_) -PathType Leaf
})

$forbiddenExtensions = @(
    '.apk', '.apks', '.aab', '.idsig', '.keystore', '.jks', '.pfx', '.p12', '.cer'
)
$forbiddenRoots = @('artifacts/', 'local/', 'bin/', 'obj/')
$textExtensions = @(
    '.cs', '.csproj', '.props', '.slnx', '.xaml', '.md', '.json', '.yml', '.yaml', '.html', '.css', '.ps1'
)
$violations = [System.Collections.Generic.List[string]]::new()

foreach ($relativePath in $repositoryFiles) {
    $normalizedPath = $relativePath.Replace('\', '/')
    $extension = [IO.Path]::GetExtension($relativePath).ToLowerInvariant()

    if ($forbiddenExtensions -contains $extension) {
        $violations.Add("Forbidden release/device artifact is tracked: $relativePath")
    }

    foreach ($forbiddenRoot in $forbiddenRoots) {
        if ($normalizedPath.StartsWith($forbiddenRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $violations.Add("Generated or machine-local path is tracked: $relativePath")
        }
    }

    if ($textExtensions -notcontains $extension -or
        $normalizedPath -eq 'tools/Test-PublicBoundary.ps1') {
        continue
    }

    $fullPath = Join-Path $repoRoot $relativePath
    $content = Get-Content -Raw -LiteralPath $fullPath
    if ($content -match '(?i)[A-Z]:\\(?:Users|Work)\\') {
        $violations.Add("Local absolute Windows path found in $relativePath")
    }

    if ($content -match '(?i)device[-_ ]serial\s*[:=]\s*[A-Z0-9]{10,}') {
        $violations.Add("Possible concrete device serial found in $relativePath")
    }
}

& git -C $repoRoot diff --check
if ($LASTEXITCODE -ne 0) {
    $violations.Add('git diff --check failed.')
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    throw "Public-boundary validation failed with $($violations.Count) issue(s)."
}

Write-Output "Public-boundary validation passed for $($repositoryFiles.Count) tracked and untracked public files."
