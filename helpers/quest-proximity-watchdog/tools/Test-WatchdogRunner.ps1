# Exercise the actual bounded host runner using a local PowerShell child, never ADB.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-Watchdog.ps1'),[ref]$tokens,[ref]$parseErrors)
$function = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-ExactAdb'
}, $false).Extent.Text
# Only the ADB-specific prefix and test deadline differ; retain drain/cap/cleanup code.
$function = $function.Replace("`$info.ArgumentList.Add('-s'); `$info.ArgumentList.Add(`$Serial)", '')
$function = $function.Replace('-ge 30000', '-ge 3000')
. ([scriptblock]::Create($function))
$adbPath = (Get-Command pwsh -ErrorAction Stop).Source
$result = Invoke-ExactAdb -Arguments @('-NoProfile','-Command',"[Console]::Out.Write('fixture')")
if ($result -ne 'fixture') { throw 'Host runner lost output' }
$inheritedPipe = @'
$info = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.ArgumentList.Add('-NoProfile')
$info.ArgumentList.Add('-Command')
$info.ArgumentList.Add('Start-Sleep -Seconds 5')
$child = [Diagnostics.Process]::Start($info)
exit 0
'@
foreach ($case in @(
    @{ command = 'Start-Sleep -Seconds 8'; error = 'timed out'; limit = 6000 },
    # Retain pending inherited-pipe reads until that known participant exits.
    @{ command = $inheritedPipe; error = 'timed out'; limit = 10000 },
    @{ command = "[Console]::Out.Write(('a' * 600000))"; error = 'exceeded bound'; limit = 6000 },
    @{ command = 'exit 7'; error = 'exit 7'; limit = 6000 }
)) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $rejected = $false
    try { Invoke-ExactAdb -Arguments @('-NoProfile','-Command',$case.command) | Out-Null }
    catch { $rejected = $_.Exception.Message.Contains($case.error) }
    if (-not $rejected -or $clock.ElapsedMilliseconds -gt $case.limit) { throw "Host runner case failed: $($case.error), elapsed=$($clock.ElapsedMilliseconds)" }
}
foreach ($reader in 'outRead','errRead') {
    $assignment = if ($reader -eq 'outRead') {
        '$outRead = $process.StandardOutput.ReadAsync($outBuffer,0,$outBuffer.Length)'
    } else { '$errRead = $process.StandardError.ReadAsync($errBuffer,0,$errBuffer.Length)' }
    $offset = $function.IndexOf($assignment, [StringComparison]::Ordinal)
    if ($offset -lt 0) { throw 'Actual runner reader-setup seam was not found' }
    $injected = $function.Insert($offset, '$script:InjectedPid = $process.Id; throw "injected-reader-setup"; ')
    . ([scriptblock]::Create($injected))
    $rejected = $false
    try { Invoke-ExactAdb -Arguments @('-NoProfile','-Command','Start-Sleep -Seconds 8') | Out-Null }
    catch { $rejected = $_.Exception.Message.Contains('injected-reader-setup') }
    if (-not $rejected) { throw "Reader setup failure lost original error: $reader" }
    if (Get-Process -Id $script:InjectedPid -ErrorAction SilentlyContinue) { throw "Reader setup failure abandoned child: $reader" }
}
Write-Output 'PASS 7 bounded host process cases (local PowerShell children; no ADB/device use)'
