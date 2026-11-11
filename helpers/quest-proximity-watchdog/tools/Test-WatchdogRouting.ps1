# Host-only adapter test: replace only the process boundary with a fake ADB.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$wrapper = Join-Path $PSScriptRoot 'Invoke-Watchdog.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($wrapper,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw 'Wrapper parse failed' }
$boundary = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-ExactAdb'
}, $false)
$code = Get-Content -LiteralPath $wrapper -Raw
$fake = @'
function Invoke-ExactAdb([string[]]$Arguments) {
    $script:RoutingCalls.Add(($Arguments -join '|'))
    if ($Arguments[0] -eq 'get-state') { return 'device' }
    if ($Arguments[0] -eq 'push') { return '' }
    if ($Arguments[0] -ne 'shell') { throw 'Unexpected fake transport request' }
    $command = $Arguments[1]
    if ($command -eq 'ps -A -o ARGS') { return $script:RoutingProcesses }
    if ($command -eq 'cat /proc/sys/kernel/random/boot_id') { return 'fixture-boot-1234' }
    if ($command.StartsWith('sha256sum ')) { return $script:RoutingHash + '  fixture.jar' }
    if ($command.StartsWith('if [ -f ')) { return 'missing' }
    if ($command.StartsWith('touch ')) { $script:RoutingProcesses = ''; return '' }
    if ($command.Contains(' start ')) { $script:RoutingProcesses = 'com.example.rustyxr.shell.StandaloneWatchdog'; return '' }
    if ($command.EndsWith(' status')) {
        return "owner_active=$script:RoutingOwnerActive`nlease_held=true`nactive=true`nfresh=$script:RoutingFresh`nboot_id=fixture-boot-1234`nlast_success_elapsed_ms=5000`nhold_ms=28800000`ninterval_ms=5000`nuntil_stopped=true`nsession_ms=28800000`nstartup_jar_sha256=$script:RoutingStartupHash"
    }
    if ($command -eq 'dumpsys power') { return 'mStayOn=true mWakefulness=Awake Display Power: com.android.server.power.PowerManagerService$1@opaque' }
    if ($command -eq 'dumpsys display') { return $script:RoutingDisplay }
    if ($command -eq 'dumpsys vrpowermanager') { return 'Virtual proximity state: CLOSE' }
    throw "Unexpected fake shell request: $command"
}
'@
$code = $code.Substring(0,$boundary.Extent.StartOffset) + $fake + $code.Substring($boundary.Extent.EndOffset)
$fixtureRoot = Join-Path $root ('build/routing-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
$script:RoutingJar = Join-Path $fixtureRoot 'fixture.jar'
[IO.File]::WriteAllText($script:RoutingJar, 'host fixture, never a device artifact')
$script:RoutingHash = (Get-FileHash -LiteralPath $script:RoutingJar -Algorithm SHA256).Hash.ToLowerInvariant()
$script:RoutingDisplayFixture = (Get-Content -LiteralPath (Join-Path $root 'tests/fixtures/physical-display.txt') -Raw).Replace("`r",'')
$script:RoutingDisplay = $script:RoutingDisplayFixture
$script:RoutingRoot = $root
$code = $code.Replace('$root = Split-Path $PSScriptRoot -Parent', '$root = $script:RoutingRoot')
$code = $code.Replace("`$localJar = Join-Path `$root 'build/outputs/questionable-file-manager-proximity-watchdog.jar'", '`$localJar = `$script:RoutingJar'.Replace('`',''))
$script:RoutingCalls = [Collections.Generic.List[string]]::new()
$script:RoutingProcesses = ''
$invoke = [scriptblock]::Create($code)
function Check([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
$parseDisplay = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-QfmPhysicalDisplayState'
}, $false).Extent.Text
. ([scriptblock]::Create($parseDisplay))
$marker = "mIsDisplayInternal=true`n    mScreenState=ON"
Check ($script:RoutingDisplayFixture.Contains($marker)) 'Sanitized physical display fixture shape differs'
$fixture = $script:RoutingDisplayFixture
foreach ($case in @(
    @{ text=$fixture; expected='ON' },
    @{ text=$fixture.Replace($marker,$marker.Replace('mScreenState=ON','mScreenState=OFF')); expected='OFF' },
    @{ text=$fixture.Replace($marker,$marker.Replace('mScreenState=ON','mScreenState=ON_SUSPEND')); expected='ON_SUSPEND' },
    @{ text=$fixture.Replace('mIsDisplayInternal=true','mIsDisplayInternal=false'); expected='' },
    @{ text=$fixture.Replace('mIsDisplayInternal=true','mIsDisplayInternal=TRUE'); expected='' },
    @{ text=$fixture.Replace($marker,'mIsDisplayInternal=true'); expected='' },
    @{ text=$fixture.Replace($marker,$marker.Replace('mScreenState=ON','mScreenState=UNKNOWN')); expected='' },
    @{ text=$fixture.Replace($marker,$marker + "`n    mScreenState=OFF"); expected='' },
    @{ text=$fixture + "`n  Display Power Controller:`n    mDisplayId=0`n    " + $marker; expected='' },
    @{ text=$fixture.Replace('mDisplayId=0','mDisplayId=1'); expected='' },
    @{ text='Display Power: state=ON'; expected='' },
    @{ text=$fixture.Replace($marker,$marker + "`n    mDisplayId=20"); expected='' },
    @{ text=$fixture.Replace($marker,$marker + "`n    mDisplayId=: 0"); expected='ON' }
)) { Check ((Get-QfmPhysicalDisplayState $case.text) -ceq $case.expected) 'Strict physical display parser mismatch' }
Write-Output 'PASS 13 PowerShell physical display parser cases'
function Reset-Fixture {
    $script:RoutingCalls.Clear(); $script:RoutingProcesses = ''; $script:RoutingFresh = 'true'
    $script:RoutingOwnerActive = 'true'; $script:RoutingStartupHash = $script:RoutingHash
    $script:RoutingDisplay = $script:RoutingDisplayFixture
}
$common = @{ Adb = $script:RoutingJar; Serial = 'fixture-selector' }

Reset-Fixture
$status = & $invoke @common -Action status | ConvertFrom-Json
Check ($status.physical_sensor_lock_readiness -eq 'not_established') 'Status must preserve wearer-readiness uncertainty'
Check ($status.readback_display_state -eq 'ON') 'Status must identify physical INTERNAL display when power exposes only an opaque object'
Check ($status.readback_display_source -eq 'dumpsys-display-internal-controller-0') 'Status must name its physical source'
Check (-not @($script:RoutingCalls | Where-Object { $_ -match '^push\||touch | start ' }).Count) 'Status must never push or mutate policy'
Check (@($script:RoutingCalls | Where-Object { $_ -eq 'shell|dumpsys power' }).Count -eq 1) 'Status requires independent power readback'

Reset-Fixture
$script:RoutingProcesses = 'com.example.rustyxr.shell.StandaloneWatchdog'
$stop = & $invoke @common -Action stop | ConvertFrom-Json
Check ($stop.owned_process_absent -and -not $stop.settings_restored) 'Stop proves absence and never reports restore'
Check (-not @($script:RoutingCalls | Where-Object { $_ -match '^push\|| start |stayon|automation_disable' }).Count) 'Stop must not push, start another owner or restore policy'
Check (@($script:RoutingCalls | Where-Object { $_ -match ' status$' }).Count -eq 1) 'Stop must verify hash-matched owner status before marker'

Reset-Fixture
$script:RoutingStartupHash = 'different-startup-artifact'
$rejected = $false
try { & $invoke @common -Action stop | Out-Null }
catch { $rejected = $_.Exception.Message.Contains('startup artifact differs') }
Check $rejected 'Current path hash must not relabel a live older artifact'
Check (-not @($script:RoutingCalls | Where-Object { $_ -match 'touch ' }).Count) 'Artifact mismatch refuses before stop marker'

Reset-Fixture
$script:RoutingOwnerActive = 'false'
$script:RoutingProcesses = 'com.example.rustyxr.shell.StandaloneWatchdog'
$rejected = $false
try { & $invoke @common -Action stop | Out-Null }
catch { $rejected = $_.Exception.Message.Contains('identity or lease owner is unresolved') }
Check $rejected 'Unknown same-class actor must not be treated as owned'
Check (-not @($script:RoutingCalls | Where-Object { $_ -match 'touch ' }).Count) 'Unknown owner refuses before stop marker'

Reset-Fixture
$start = & $invoke @common -Action start -ConfirmDeviceSettings -UntilStopped | ConvertFrom-Json
Check ($start.mutation_confirmation -eq 'confirmed') 'Start requires live and independent effect observations'
Check (@($script:RoutingCalls | Where-Object { $_ -match '^push\|' }).Count -eq 1) 'Explicit start pushes one selected JAR'
Check (@($script:RoutingCalls | Where-Object { $_ -match ' start 28800000 5000 until-stopped ' }).Count -eq 1) 'Start passes bounded hold and until-stopped separately'

Reset-Fixture
$script:RoutingFresh = 'false'
$pending = & $invoke @common -Action start -ConfirmDeviceSettings -UntilStopped | ConvertFrom-Json
Check ($pending.mutation_confirmation -eq 'pending') 'A stale owner must not confirm start'
Check (@($script:RoutingCalls | Where-Object { $_ -match ' start ' }).Count -eq 1) 'Pending effects must never trigger another start'

Reset-Fixture
$script:RoutingDisplay = $fixture.Replace('mIsDisplayInternal=true','mIsDisplayInternal=false')
$virtualOnly = & $invoke @common -Action start -ConfirmDeviceSettings -UntilStopped | ConvertFrom-Json
Check ($virtualOnly.mutation_confirmation -eq 'pending' -and $virtualOnly.readback_display_state -eq '') 'Virtual ON cannot confirm physical display readiness'
Check (@($script:RoutingCalls | Where-Object { $_ -match ' start ' }).Count -eq 1) 'Unknown physical display must not trigger repeated start'

foreach ($existing in 'com.example.rustyxr.shell.StandaloneWatchdog','com.example.rustyxr.shell.Helper','/data/local/tmp/questionable-file-manager-awake-watchdog.sh') {
    Reset-Fixture
    $script:RoutingProcesses = $existing
    $rejected = $false
    try { & $invoke @common -Action start -ConfirmDeviceSettings | Out-Null }
    catch { $rejected = $_.Exception.Message.Contains('existing helper') }
    Check $rejected 'Existing owner must block replacement'
    Check (-not @($script:RoutingCalls | Where-Object { $_ -match '^push\|| start ' }).Count) 'Existing owner blocks before push/effects'
}
Reset-Fixture
$rejected = $false
try { & $invoke @common -Action start -ConfirmDeviceSettings -HoldDurationMs 60000 -IntervalMs 60000 | Out-Null }
catch { $rejected = $_.Exception.Message.Contains('Hold must exceed') }
Check ($rejected -and $script:RoutingCalls.Count -eq 0) 'Impossible timing rejected before transport'
Write-Output 'PASS 11 host-only wrapper routing cases (fake transport; no ADB/device use)'
