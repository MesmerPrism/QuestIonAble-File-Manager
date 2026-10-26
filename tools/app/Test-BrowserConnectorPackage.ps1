[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'Publish-BrowserConnector.ps1')
$testRoot = Join-Path $repository ('local/connector-package-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$passed = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Test-Case([string]$CaseName, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Host "PASS $CaseName"
}
function Assert-Rejected([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        return
    }
    throw "Expected rejection: $Pattern"
}
function New-Fixture {
    $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
    $source = Join-Path $root 'source'
    $payload = Join-Path $root 'payload'
    $hostSource = Join-Path $source 'src/QuestIonAbleFileManager.BrowserHost'
    [IO.Directory]::CreateDirectory($hostSource) | Out-Null
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'src/QuestIonAbleFileManager.BrowserHost/wwwroot') -Destination $hostSource -Recurse
    foreach ($name in @('BrowserSecurity.cs','BrowserShellAssets.cs')) {
        Copy-Item -LiteralPath (Join-Path $repository "src/QuestIonAbleFileManager.BrowserHost/$name") -Destination $hostSource
    }
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $source
    Copy-Item -LiteralPath (Join-Path $hostSource 'wwwroot') -Destination $payload -Recurse
    Remove-Item -LiteralPath (Join-Path $payload 'wwwroot/vendor/.gitattributes')
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination $payload
    foreach ($name in @('questionable-file-manager-browser.dll','QuestIonAbleFileManager.Core.dll')) {
        [IO.File]::WriteAllText((Join-Path $payload $name), 'fixture bytes')
    }
    [IO.File]::WriteAllText((Join-Path $payload 'questionable-file-manager-browser.deps.json'),
        '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"},"libraries":{"questionable-file-manager-browser/0.1.0-dev":{"type":"project"},"QuestIonAbleFileManager.Core/0.1.0-dev":{"type":"project"}}}')
    [IO.File]::WriteAllText((Join-Path $payload 'questionable-file-manager-browser.runtimeconfig.json'),
        '{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}')
    return @{ Root = $root; Source = $source; Payload = $payload }
}
try {
    Test-Case 'fixed framework-dependent untrimmed arguments for every closed RID' {
        foreach ($rid in @('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')) {
            $arguments = @(Get-ConnectorPublishArguments $repository $testRoot $rid)
            $joined = $arguments -join '|'
            Assert-True ($joined.Contains("--runtime|$rid|--self-contained|false")) 'Missing fixed deployment mode'
            foreach ($flag in @('-p:UseAppHost=false','-p:PublishTrimmed=false','-p:PublishSingleFile=false','-p:PublishAot=false','-p:PublishReadyToRun=false','-p:CompressionEnabled=false')) {
                Assert-True ($arguments -ccontains $flag) "Missing fixed $flag"
            }
            Assert-True ($arguments[1] -ceq (Join-Path $repository 'src/QuestIonAbleFileManager.BrowserHost/QuestIonAbleFileManager.BrowserHost.csproj')) 'Wrong project'
        }
        Assert-Rejected { Get-ConnectorPublishArguments $repository $testRoot 'linux-musl-x64' } 'RuntimeIdentifier'
    }
    Test-Case 'new output rejects relative, existing and source directories without changing them' {
        Assert-Rejected { New-ConnectorOutput 'relative-output' $repository } 'absolute'
        Assert-Rejected { New-ConnectorOutput $testRoot $repository } 'exist'
        Assert-Rejected { New-ConnectorOutput (Join-Path $repository 'src/never-create-package') $repository } 'local.*artifacts'
        Assert-Rejected { New-ConnectorOutput (Join-Path $testRoot 'ambiguous[output]') $repository } 'name'
        $new = Join-Path $testRoot 'new-output'
        New-ConnectorOutput $new $repository | Out-Null
        Assert-True (Test-Path -LiteralPath (Join-Path $new '.incomplete')) 'Ownership/failure marker absent'
        Assert-Rejected { New-ConnectorOutput $new $repository } 'exist'
    }
    Test-Case 'complete payload includes exact shell, vendors, notices and repository license' {
        $fixture = New-Fixture
        $records = @(Assert-ConnectorPayload $fixture.Payload $fixture.Source)
        foreach ($name in @('LICENSE','wwwroot/sw.js','wwwroot/vendor/TANGO_NOTICES.txt','wwwroot/vendor/tango-packages.json','wwwroot/vendor/NOBLE_HASHES_NOTICES.txt','wwwroot/vendor/noble-hashes-packages.json')) {
            Assert-True ($records.path -ccontains $name) "Missing $name"
        }
        $sorted = [string[]]$records.path.Clone()
        [Array]::Sort($sorted,[StringComparer]::Ordinal)
        Assert-True (($records.path -join '|') -ceq ($sorted -join '|')) 'Manifest not sorted'
    }
    foreach ($name in @('app.js','vendor/tango-adb.js','vendor/TANGO_NOTICES.txt','vendor/noble-sha256.js','vendor/noble-hashes-packages.json')) {
        Test-Case "missing or changed $name fails closed" {
            $fixture = New-Fixture
            $file = Join-Path $fixture.Payload "wwwroot/$name"
            [IO.File]::WriteAllText($file,'changed')
            Assert-Rejected { Assert-ConnectorPayload $fixture.Payload $fixture.Source } 'bytes|differ'
            Remove-Item -LiteralPath $file
            Assert-Rejected { Assert-ConnectorPayload $fixture.Payload $fixture.Source } 'missing|set'
        }
    }
    Test-Case 'wrong license, private files and bundled ADB are excluded by closed content' {
        foreach ($extra in @('adb.exe','appsettings.Production.json','native-state/key.json','wwwroot/capture.json')) {
            $fixture = New-Fixture
            $path = Join-Path $fixture.Payload $extra
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            [IO.File]::WriteAllText($path,'private fixture')
            Assert-Rejected { Assert-ConnectorPayload $fixture.Payload $fixture.Source } 'unexpected|set'
        }
        $fixture = New-Fixture
        [IO.File]::WriteAllText((Join-Path $fixture.Payload 'LICENSE'),'other terms')
        Assert-Rejected { Assert-ConnectorPayload $fixture.Payload $fixture.Source } 'license|bytes|differ'
    }
    Test-Case 'no self-contained or wrong framework runtime config can masquerade as this archive' {
        foreach ($config in @('{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[]}}','{"runtimeOptions":{"tfm":"net9.0","frameworks":[]}}')) {
            $fixture = New-Fixture
            [IO.File]::WriteAllText((Join-Path $fixture.Payload 'questionable-file-manager-browser.runtimeconfig.json'),$config)
            Assert-Rejected { Assert-ConnectorPayload $fixture.Payload $fixture.Source } 'framework'
        }
        $fixture = New-Fixture
        Assert-Rejected { Assert-ConnectorPayload $fixture.Payload $fixture.Source 'linux-x64' } 'runtime.*target|RID'
        [IO.File]::WriteAllText((Join-Path $fixture.Payload 'questionable-file-manager-browser.deps.json'),
            '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"},"libraries":{"other-package/1.0":{"type":"package"}}}')
        Assert-Rejected { Assert-ConnectorPayload $fixture.Payload $fixture.Source } 'dependencies'
    }
    Test-Case 'unused SDK endpoint and version-control metadata never enter the archive inventory' {
        $fixture = New-Fixture
        [IO.File]::WriteAllText((Join-Path $fixture.Payload 'questionable-file-manager-browser.staticwebassets.endpoints.json'),'unused metadata')
        [IO.File]::WriteAllText((Join-Path $fixture.Payload 'wwwroot/vendor/.gitattributes'),'unused metadata')
        $records = @(Assert-ConnectorPayload $fixture.Payload $fixture.Source)
        Assert-True (($records.path -join '|') -notmatch 'staticwebassets|gitattributes') 'Unused SDK/VCS metadata included'
    }
    Test-Case 'reparse/symlink ancestors and payload entries are rejected' {
        $fixture = New-Fixture
        $link = Join-Path $testRoot 'linked-parent'
        $type = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
        New-Item -ItemType $type -Path $link -Target $fixture.Root | Out-Null
        try { Assert-Rejected { New-ConnectorOutput (Join-Path $link 'output') $repository } 'reparse|link' }
        finally { Remove-Item -LiteralPath $link }
        $link = Join-Path $fixture.Payload 'linked-directory'
        New-Item -ItemType $type -Path $link -Target $fixture.Source | Out-Null
        try { Assert-Rejected { Assert-ConnectorPayload $fixture.Payload $fixture.Source } 'reparse|link' }
        finally { Remove-Item -LiteralPath $link }
    }
    Test-Case 'archive manifest hashes exact bytes and fixed ZIP metadata without overwrites' {
        $fixture = New-Fixture
        $records = @(Assert-ConnectorPayload $fixture.Payload $fixture.Source)
        $source = [ordered]@{ revision = 'a' * 40; tree = 'b' * 40; worktreeDirty = $true; inputFiles = @() }
        $first = Join-Path $fixture.Root 'first.zip'
        $second = Join-Path $fixture.Root 'second.zip'
        New-ConnectorArchive $fixture.Payload $records $source '10.0.302' 'win-x64' $first | Out-Null
        New-ConnectorArchive $fixture.Payload $records $source '10.0.302' 'win-x64' $second | Out-Null
        Assert-True ((Get-FileHash $first).Hash -ceq (Get-FileHash $second).Hash) 'Identical inputs produced differing archive metadata'
        Assert-Rejected { New-ConnectorArchive $fixture.Payload $records $source '10.0.302' 'win-x64' $first } 'exist'
        $archive = [IO.Compression.ZipFile]::OpenRead($first)
        try {
            $manifestEntry = $archive.GetEntry('connector-package.json')
            $reader = [IO.StreamReader]::new($manifestEntry.Open())
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            Assert-True ($manifest.source.worktreeDirty -eq $true) 'Dirty custody was falsely labelled clean'
            Assert-True ($manifest.files.Count -eq $records.Count) 'Payload inventory count differs'
            foreach ($file in $manifest.files) {
                $entry = $archive.GetEntry($file.path)
                $stream = $entry.Open()
                try { $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
                Assert-True ($hash -ceq $file.sha256 -and $entry.Length -eq $file.sizeBytes) "Wrong archived bytes $($file.path)"
            }
            foreach ($entry in $archive.Entries) { Assert-True ($entry.LastWriteTime.Year -eq 2000) 'ZIP timestamp is ambient' }
            $sums = $archive.GetEntry('SHA256SUMS.txt')
            Assert-True ($null -ne $sums) 'Package checksums absent'
            $reader = [IO.StreamReader]::new($sums.Open())
            try { $lines = $reader.ReadToEnd().TrimEnd("`n").Split("`n") } finally { $reader.Dispose() }
            Assert-True ($lines.Count -eq $records.Count + 1) 'Checksum inventory count differs'
            $names = [string[]]@($lines | ForEach-Object { $_.Split('  ',[StringSplitOptions]::None)[1] })
            $sortedNames = [string[]]$names.Clone(); [Array]::Sort($sortedNames,[StringComparer]::Ordinal)
            Assert-True (($names -join '|') -ceq ($sortedNames -join '|')) 'Checksums are not in ordinal file order'
            foreach ($line in $lines) {
                $fields = $line.Split('  ',[StringSplitOptions]::None)
                $entry = $archive.GetEntry($fields[1])
                $stream = $entry.Open()
                try { $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
                Assert-True ($fields[0] -ceq $hash) 'Checksum does not bind archived bytes'
            }
            Assert-True ($archive.Entries.Count -eq $records.Count + 2) 'Unexpected archived content'
        } finally { $archive.Dispose() }
    }
    Test-Case 'changed bytes after validation cannot produce a successful archive' {
        $fixture = New-Fixture
        $records = @(Assert-ConnectorPayload $fixture.Payload $fixture.Source)
        [IO.File]::WriteAllText((Join-Path $fixture.Payload 'questionable-file-manager-browser.dll'),'different bytes')
        Assert-Rejected { New-ConnectorArchive $fixture.Payload $records @{} '10.0.302' 'win-x64' (Join-Path $fixture.Root 'changed.zip') } 'changed'
    }
    Test-Case 'source provenance detects dirty tracked and untracked inputs without embedding machine paths' {
        $fixture = New-Fixture
        foreach ($name in @('global.json','Directory.Build.props')) { Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $fixture.Source }
        $tools = Join-Path $fixture.Source 'tools/app'
        [IO.Directory]::CreateDirectory($tools) | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Publish-BrowserConnector.ps1') -Destination $tools
        & git -C $fixture.Source init --quiet
        & git -C $fixture.Source -c core.autocrlf=false add -- .
        & git -C $fixture.Source -c core.hooksPath=/nonexistent-fixture-hooks -c commit.gpgsign=false -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m fixture
        if ($LASTEXITCODE -ne 0) { throw 'Fixture git commit failed' }
        $clean = Get-ConnectorProvenance $fixture.Source
        Assert-True (-not $clean.worktreeDirty) 'Clean fixture incorrectly dirty'
        [IO.File]::AppendAllText((Join-Path $fixture.Source 'Directory.Build.props'),' ')
        [IO.File]::WriteAllText((Join-Path $fixture.Source 'private-untracked.txt'),'not packaged')
        $dirty = Get-ConnectorProvenance $fixture.Source
        Assert-True $dirty.worktreeDirty 'Dirty source mislabelled clean'
        Assert-True (($dirty | ConvertTo-Json -Depth 10) -notlike "*$($fixture.Source)*") 'Absolute path leaked into provenance'
        Assert-True (($dirty.inputFiles.path -join '|') -notmatch 'private-untracked') 'Unrelated private path entered source inventory'
        Assert-True (($clean.inputFiles | ConvertTo-Json -Depth 5) -cne ($dirty.inputFiles | ConvertTo-Json -Depth 5)) 'Changed source bytes not bound'
    }
    Write-Host "Browser connector package tests: $passed passed. No publish, listener, device or credential effects."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath((Join-Path $repository 'local')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
