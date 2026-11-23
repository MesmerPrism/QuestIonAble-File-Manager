[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildToolsRoot, [string]$JdkRoot = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $JdkRoot) { $JdkRoot = Split-Path (Split-Path (Get-Command javac -ErrorAction Stop).Source -Parent) -Parent }
$javac = Join-Path $JdkRoot 'bin/javac.exe'
$jar = Join-Path $JdkRoot 'bin/jar.exe'
$d8 = Join-Path $BuildToolsRoot 'd8.bat'
foreach ($tool in $javac,$jar,$d8) { if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing required tool: $tool" } }
& (Join-Path $PSScriptRoot 'Test-Watchdog.ps1') -JdkRoot $JdkRoot
$work = Join-Path $root ('build/compile/' + [guid]::NewGuid().ToString('N'))
$classes = Join-Path $work 'classes'
$dex = Join-Path $work 'dex'
$outputs = Join-Path $root 'build/outputs'
New-Item -ItemType Directory -Force -Path $classes,$dex,$outputs | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Filter '*.java' | Select-Object -ExpandProperty FullName)
& $javac -encoding UTF-8 --release 8 -d $classes @sources
if ($LASTEXITCODE -ne 0) { throw 'Java compilation failed' }
$oldJavaHome = [Environment]::GetEnvironmentVariable('JAVA_HOME', 'Process')
try {
    $env:JAVA_HOME = $JdkRoot
    $classFiles = @(Get-ChildItem -LiteralPath $classes -Recurse -File -Filter '*.class' | Select-Object -ExpandProperty FullName)
    & $d8 --min-api 29 --output $dex @classFiles
    if ($LASTEXITCODE -ne 0) { throw 'D8 compilation failed' }
} finally { [Environment]::SetEnvironmentVariable('JAVA_HOME', $oldJavaHome, 'Process') }
$output = Join-Path $outputs 'questionable-file-manager-proximity-watchdog.jar'
& $jar cf $output -C $dex classes.dex -C $root LICENSE -C $root UPSTREAM.md
if ($LASTEXITCODE -ne 0) { throw 'DEX JAR packaging failed' }
$receipt = [ordered]@{
    schema = 'qfm.standalone_proximity_watchdog.build.v1'
    jar_sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    jar_bytes = (Get-Item -LiteralPath $output).Length
    runtime_validated = $false
    source_files = @($sources | ForEach-Object { [ordered]@{
        path = [IO.Path]::GetRelativePath($root, $_).Replace('\','/')
        sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    } })
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputs 'build-receipt.json') -Encoding utf8NoBOM
Write-Output "Built source-only standalone helper: $output"
$receipt | ConvertTo-Json -Depth 5
