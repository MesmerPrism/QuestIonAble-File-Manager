[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('start','status','stop')][string]$Action,
    [Parameter(Mandatory)][string]$Adb,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._:\[\]%-]+$')][string]$Serial,
    [switch]$ConfirmDeviceSettings,
    [ValidateRange(60000,28800000)][int]$HoldDurationMs = 28800000,
    [ValidateRange(1000,60000)][int]$IntervalMs = 5000,
    [ValidateRange(60000,28800000)][int]$SessionDurationMs = 28800000,
    [switch]$UntilStopped
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Action -ne 'start' -and ($ConfirmDeviceSettings -or $UntilStopped -or
    $PSBoundParameters.ContainsKey('HoldDurationMs') -or $PSBoundParameters.ContainsKey('IntervalMs') -or
    $PSBoundParameters.ContainsKey('SessionDurationMs'))) { throw 'Timing/confirmation options apply only to start' }
if ($Action -eq 'start' -and -not $ConfirmDeviceSettings) { throw 'Start requires -ConfirmDeviceSettings for its finite hold and power effects' }
if ($Action -eq 'start' -and [Math]::Max([long]$HoldDurationMs / 4, 2L * ($IntervalMs + 25000L)) -ge $HoldDurationMs) {
    throw 'Hold must exceed twice the poll interval plus 25-second command budget'
}
$adbPath = (Resolve-Path -LiteralPath $Adb).Path
$root = Split-Path $PSScriptRoot -Parent
$localJar = Join-Path $root 'build/outputs/questionable-file-manager-proximity-watchdog.jar'
$prefix = '/data/local/tmp/questionable-file-manager-proximity-watchdog'
if (-not (Test-Path -LiteralPath $localJar -PathType Leaf)) { throw 'Build the local helper first; no action implicitly builds or pushes it' }
$selectedJarHash = (Get-FileHash -LiteralPath $localJar -Algorithm SHA256).Hash.ToLowerInvariant()
$deviceJar = "$prefix-$selectedJarHash.jar"
$main = 'com.example.rustyxr.shell.StandaloneWatchdog'

function Invoke-ExactAdb([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($adbPath)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.ArgumentList.Add('-s'); $info.ArgumentList.Add($Serial)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $outBuffer = [char[]]::new(8192); $errBuffer = [char[]]::new(8192)
    $stdout = [Text.StringBuilder]::new(); $stderr = [Text.StringBuilder]::new()
    $outRead = $null; $errRead = $null; $outDone = $false; $errDone = $false
    $process = [Diagnostics.Process]::Start($info)
    try {
        $outRead = $process.StandardOutput.ReadAsync($outBuffer,0,$outBuffer.Length)
        $errRead = $process.StandardError.ReadAsync($errBuffer,0,$errBuffer.Length)
        while (-not ($process.HasExited -and $outDone -and $errDone)) {
            if ($clock.ElapsedMilliseconds -ge 30000) { throw 'Exact-serial ADB command timed out; outcome requires readback' }
            if (-not $outDone -and $outRead.IsCompleted) {
                $count = $outRead.GetAwaiter().GetResult()
                $outDone = $count -eq 0
                if (-not $outDone) {
                    $null = $stdout.Append($outBuffer,0,$count)
                    $outRead = $process.StandardOutput.ReadAsync($outBuffer,0,$outBuffer.Length)
                }
            }
            if (-not $errDone -and $errRead.IsCompleted) {
                $count = $errRead.GetAwaiter().GetResult()
                $errDone = $count -eq 0
                if (-not $errDone) {
                    $null = $stderr.Append($errBuffer,0,$count)
                    $errRead = $process.StandardError.ReadAsync($errBuffer,0,$errBuffer.Length)
                }
            }
            if ($stdout.Length + $stderr.Length -gt 524288) { throw 'Exact-serial ADB output exceeded bound; outcome requires readback' }
            if (-not ($process.HasExited -and $outDone -and $errDone)) { Start-Sleep -Milliseconds 5 }
        }
        if ($process.ExitCode -ne 0) { throw "Exact-serial ADB failed (exit $($process.ExitCode)); no automatic retry" }
        return $stdout.ToString().Trim()
    } finally {
        if (-not $process.HasExited) {
            try { $process.Kill() } catch { Write-Warning 'Host child retirement unresolved; retaining exact process custody without further effects' }
        }
        # A kill request is not retirement. Keep this exact process until exit is observed.
        while (-not $process.HasExited) { $null = $process.WaitForExit(250) }
        # Drain outstanding handles to EOF without storing more output. An inherited
        # pipe can keep this quiescent owner resident; never kill an unknown descendant.
        if ($null -eq $outRead) {
            try { $outRead = $process.StandardOutput.ReadAsync($outBuffer,0,$outBuffer.Length) }
            catch { $outDone = $true }
        }
        if ($null -eq $errRead) {
            try { $errRead = $process.StandardError.ReadAsync($errBuffer,0,$errBuffer.Length) }
            catch { $errDone = $true }
        }
        while (-not ($outDone -and $errDone)) {
            if (-not $outDone -and $outRead.IsCompleted) {
                try {
                    $outDone = $outRead.GetAwaiter().GetResult() -eq 0
                    if (-not $outDone) { $outRead = $process.StandardOutput.ReadAsync($outBuffer,0,$outBuffer.Length) }
                } catch { $outDone = $true }
            }
            if (-not $errDone -and $errRead.IsCompleted) {
                try {
                    $errDone = $errRead.GetAwaiter().GetResult() -eq 0
                    if (-not $errDone) { $errRead = $process.StandardError.ReadAsync($errBuffer,0,$errBuffer.Length) }
                } catch { $errDone = $true }
            }
            if (-not ($outDone -and $errDone)) { Start-Sleep -Milliseconds 5 }
        }
        try { $process.StandardOutput.BaseStream.Dispose() } catch { }
        try { $process.StandardError.BaseStream.Dispose() } catch { }
        $process.Dispose()
    }
}
function Get-Processes { return Invoke-ExactAdb @('shell','ps -A -o ARGS') }
function Assert-Jar {
    if (-not (Test-Path -LiteralPath $localJar -PathType Leaf)) { throw 'Build the local helper first; status never builds or pushes a JAR' }
    $expected = (Get-FileHash -LiteralPath $localJar -Algorithm SHA256).Hash.ToLowerInvariant()
    $actual = Invoke-ExactAdb @('shell',"sha256sum $deviceJar")
    if (($actual -split '\s+')[0] -ne $expected) { throw 'Installed helper JAR differs from local build; preserve owner and inspect its provenance' }
    return $expected
}
function Get-Status {
    $hash = Assert-Jar
    $text = Invoke-ExactAdb @('shell',"CLASSPATH=$deviceJar app_process / $main status")
    $result = [ordered]@{ jar_sha256 = $hash }
    foreach ($line in ($text -split '\r?\n')) {
        $fields = $line.Split('=',2)
        if ($fields.Count -eq 2) { $result[$fields[0]] = $fields[1] }
    }
    if ($result['owner_active'] -eq 'true' -and $result['startup_jar_sha256'] -ne $hash) {
        throw 'Live helper startup artifact differs from selected build; preserve its owner and reconcile provenance'
    }
    # These are fresh independent observations, separate from recorded scheduler state.
    $power = Invoke-ExactAdb @('shell','dumpsys power')
    $proximity = Invoke-ExactAdb @('shell','dumpsys vrpowermanager')
    $result['readback_stay_on'] = [regex]::Match($power,'mStayOn=(true|false)').Groups[1].Value
    $result['readback_wakefulness'] = [regex]::Match($power,'mWakefulness=(\S+)').Groups[1].Value
    $result['readback_display_state'] = [regex]::Match($power,'Display Power: state=(\S+)').Groups[1].Value
    $result['readback_virtual_proximity'] = [regex]::Match($proximity,'Virtual proximity state:\s*(\S+)').Groups[1].Value
    $result['physical_sensor_lock_readiness'] = 'not_established'
    return $result
}

if ((Invoke-ExactAdb @('get-state')) -ne 'device') { throw 'Exact selected transport is not ready' }
if ($Action -eq 'status') {
    Get-Status | ConvertTo-Json -Depth 4
    return
}
if ($Action -eq 'stop') {
    # Verify the owner before touching its fixed marker; no build/push or policy restoration.
    $before = Get-Status
    $processes = Get-Processes
    if ($before['owner_active'] -ne 'true') {
        if ($processes.Contains($main) -or $before['lease_held'] -eq 'true') {
            throw 'Stop refused: helper identity or lease owner is unresolved; preserve the existing owner'
        }
        [ordered]@{ stop_requested = $false; owned_process_absent = $true; settings_restored = $false } | ConvertTo-Json
        return
    }
    $null = Invoke-ExactAdb @('shell',"touch $prefix.stop")
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        $absent = -not (Get-Processes).Contains($main)
        if ($absent) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    [ordered]@{ stop_requested = $true; owned_process_absent = $absent; settings_restored = $false } | ConvertTo-Json
    if (-not $absent) { throw 'Stop pending: owned process remains; preserve state and reconcile without restart or restore' }
    return
}
if (-not (Test-Path -LiteralPath $localJar -PathType Leaf)) { throw 'Build the helper explicitly before start' }
$processes = Get-Processes
foreach ($owner in $main,'com.example.rustyxr.shell.Helper','/data/local/tmp/questionable-file-manager-awake-watchdog.sh') {
    if ($processes.Contains($owner)) { throw 'An existing helper is present; preserve its owner and stop/read back through that owner before replacement' }
}
$boot = Invoke-ExactAdb @('shell','cat /proc/sys/kernel/random/boot_id')
if ($boot -notmatch '^[a-zA-Z0-9-]{8,128}$') { throw 'Boot identity unavailable before start' }
$existingJar = Invoke-ExactAdb @('shell',"if [ -f $deviceJar ]; then sha256sum $deviceJar; else printf missing; fi")
if ($existingJar -eq 'missing') {
    $null = Invoke-ExactAdb @('push',$localJar,$deviceJar)
} elseif (($existingJar -split '\s+')[0] -ne $selectedJarHash) {
    throw 'Existing content-addressed JAR is damaged; preserve it and resolve through its owner'
}
$null = Assert-Jar
$session = if ($UntilStopped) { 'until-stopped' } else { [string]$SessionDurationMs }
$null = Invoke-ExactAdb @('shell',"CLASSPATH=$deviceJar nohup app_process / $main start $HoldDurationMs $IntervalMs $session >> $prefix.log 2>&1 < /dev/null &")
$deadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    $status = Get-Status
    if ($status['owner_active'] -eq 'true') { break }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $deadline)
$status['start_requested'] = $true
$status['same_boot_as_preflight'] = $status['boot_id'] -eq $boot
$status['requested_timing_matches'] = $status['hold_ms'] -eq [string]$HoldDurationMs -and
    $status['interval_ms'] -eq [string]$IntervalMs -and
    $status['until_stopped'] -eq ([string][bool]$UntilStopped).ToLowerInvariant() -and
    $status['session_ms'] -eq [string]$(if ($UntilStopped) { 28800000 } else { $SessionDurationMs })
$status['mutation_confirmation'] = if ($status['active'] -eq 'true' -and $status['fresh'] -eq 'true' -and
    $status['same_boot_as_preflight'] -and $status['requested_timing_matches'] -and
    $status['last_success_elapsed_ms'] -match '^\d+$' -and
    $status['readback_stay_on'] -eq 'true' -and $status['readback_wakefulness'] -eq 'Awake' -and
    $status['readback_display_state'] -in @('ON','ON_SUSPEND') -and $status['readback_virtual_proximity'] -eq 'CLOSE') { 'confirmed' } else { 'pending' }
$status | ConvertTo-Json -Depth 4
# A pending start may already own effects. Never automatically repeat it.
