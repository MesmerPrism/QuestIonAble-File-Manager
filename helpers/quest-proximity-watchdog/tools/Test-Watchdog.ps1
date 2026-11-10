[CmdletBinding()]
param([string]$JdkRoot = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $JdkRoot) { $JdkRoot = Split-Path (Split-Path (Get-Command javac -ErrorAction Stop).Source -Parent) -Parent }
$javac = Join-Path $JdkRoot 'bin/javac.exe'
$java = Join-Path $JdkRoot 'bin/java.exe'
$pins = Get-Content -LiteralPath (Join-Path $root 'upstream-pins.json') -Raw | ConvertFrom-Json
foreach ($pin in $pins.unmodified_files) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $pin.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.sha256) {
        throw "Unmodified upstream source differs: $($pin.path)"
    }
}
$classes = Join-Path $root ('build/host-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $classes | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src'),(Join-Path $root 'tests') -Recurse -File -Filter '*.java' | Select-Object -ExpandProperty FullName)
& $javac -encoding UTF-8 --release 8 -d $classes @sources
if ($LASTEXITCODE -ne 0) { throw 'Host test compilation failed' }
foreach ($test in 'ProximityHoldTest','WatchdogInfrastructureTest','StandaloneWatchdogTest') {
    if ($test -eq 'StandaloneWatchdogTest') {
        & $java -cp $classes "com.example.rustyxr.shell.$test" (Join-Path $root 'tests/fixtures/physical-display.txt')
    } else { & $java -cp $classes "com.example.rustyxr.shell.$test" }
    if ($LASTEXITCODE -ne 0) { throw "Host test failed: $test" }
}
& (Join-Path $PSScriptRoot 'Test-WatchdogRouting.ps1')
& (Join-Path $PSScriptRoot 'Test-WatchdogRunner.ps1')
Write-Output 'PASS standalone watchdog host tests (no ADB/device use)'
