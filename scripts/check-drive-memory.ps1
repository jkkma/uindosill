<#
.SYNOPSIS
    Check the curated Codex memory export route with a fake rclone; no network or user memory.
.DESCRIPTION
    Copies the two entry points into runs/check-drive-memory/<unique>/repo/scripts and places
    synthetic export folders beside that fixture repository. Each child process has a PATH that
    contains only the fake rclone and PowerShell. Fixtures and captured arguments are retained.
.EXAMPLE
    pwsh -NoProfile -File scripts/check-drive-memory.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repo ('runs/check-drive-memory/' + [Guid]::NewGuid().ToString('N'))
$fixtureScripts = Join-Path $fixture 'repo/scripts'
$fakeBin = Join-Path $fixture 'bin'
$export = Join-Path $fixture 'curated notes'
$emptyExport = Join-Path $fixture 'empty'
$fakeCodexRoot = Join-Path $fixture 'codex-store'
$fakeMemory = Join-Path $fakeCodexRoot 'memories'
foreach ($directory in @($fixtureScripts, $fakeBin, $export, $emptyExport, $fakeMemory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
foreach ($script in @('sync-drive.ps1', 'lab.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $fixtureScripts
}
Set-Content -LiteralPath (Join-Path $export 'MEMORY.md') -Value '# Synthetic curated notes'
Set-Content -LiteralPath (Join-Path $export 'private.json') -Value '{"excluded":true}'
Set-Content -LiteralPath (Join-Path $emptyExport 'notes.txt') -Value 'No markdown'
Set-Content -LiteralPath (Join-Path $fakeMemory 'MEMORY.md') -Value '# Synthetic global store'
New-Item -ItemType Directory -Path (Join-Path $fakeMemory 'skills') | Out-Null
Set-Content -LiteralPath (Join-Path $fakeMemory 'skills/fixture.md') -Value '# Synthetic nested memory'
Set-Content -LiteralPath (Join-Path $fixtureScripts 'notes.md') -Value '# Repository-local notes'

@'
$record = @{ arguments = @($args) } | ConvertTo-Json -Compress
[IO.File]::AppendAllText($env:CHECK_DRIVE_MEMORY_LOG, $record + [Environment]::NewLine)
switch ($args[0]) {
    'listremotes' { 'fixture:'; exit 0 }
    'copy' { exit 0 }
    default { throw "Unexpected rclone operation: $($args[0])" }
}
'@ | Set-Content -LiteralPath (Join-Path $fakeBin 'rclone.ps1')

# Catch the parameter binder's own ErrorRecord before the CLI localizes its rendering. Only
# these identifiers cross the process boundary; no English error text or console encoding is
# involved, and an accepted invocation still reaches the same fake-rclone trap as every case.
@'
param([string] $EntryPoint, [string] $MemorySource)
try {
    & $EntryPoint -Runs laptop -MemorySource $MemorySource
}
catch {
    @{
        FullyQualifiedErrorId = $_.FullyQualifiedErrorId
        ExceptionType = $_.Exception.GetType().FullName
    } | ConvertTo-Json -Compress
    exit 1
}
'@ | Set-Content -LiteralPath (Join-Path $fixtureScripts 'parameter-set.ps1')

$pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$script:caseNumber = 0
function Invoke-Fixture([string] $EntryPoint, [string[]] $Arguments) {
    $script:caseNumber++
    $log = Join-Path $fixture "$script:caseNumber-rclone.jsonl"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $pwsh
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $fixtureScripts $EntryPoint)) + $Arguments) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['PATH'] = $fakeBin + [IO.Path]::PathSeparator + $PSHOME
    $start.Environment['CODEX_HOME'] = $fakeCodexRoot
    $start.Environment['CHECK_DRIVE_MEMORY_LOG'] = $log
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        Set-Content -LiteralPath (Join-Path $fixture "$script:caseNumber-output.txt") -Value $output
        $calls = @(if (Test-Path -LiteralPath $log) {
            Get-Content -LiteralPath $log | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable }
        })
        return @{ ExitCode = $process.ExitCode; Output = $output; Calls = $calls }
    }
    finally { $process.Dispose() }
}

function Assert-That([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected([string[]] $Arguments, [string] $Expected) {
    $result = Invoke-Fixture 'sync-drive.ps1' $Arguments
    Assert-That ($result.ExitCode -ne 0) "Expected rejection: $($Arguments -join ' ')"
    Assert-That ($result.Output -match $Expected) "Missing error '$Expected': $($result.Output)"
    Assert-That ($result.Calls.Count -eq 0) 'Invalid memory source reached rclone.'
}

Assert-Rejected @('-Memory', 'laptop') 'MemorySource'
Assert-Rejected @('-Memory', 'laptop', '-MemorySource', (Join-Path $fixture 'missing')) 'export folder not found'
Assert-Rejected @('-Memory', 'laptop', '-MemorySource', (Join-Path $export 'MEMORY.md')) 'export folder not found'
Assert-Rejected @('-Memory', 'laptop', '-MemorySource', $emptyExport) 'No markdown'
Assert-Rejected @('-Memory', 'laptop', '-MemorySource', $fixtureScripts) 'outside the repository'
Assert-Rejected @('-Memory', 'laptop', '-MemorySource', $fixture) 'outside the repository'
Assert-Rejected @('-Memory', 'laptop', '-MemorySource', $fakeMemory) 'global Codex memory store'
Assert-Rejected @('-Memory', 'laptop', '-MemorySource', (Join-Path $fakeMemory 'skills')) 'global Codex memory store'
Assert-Rejected @('-Memory', 'laptop', '-MemorySource', $fakeCodexRoot) 'global Codex memory store'
$conflict = Invoke-Fixture 'parameter-set.ps1' @('-EntryPoint', (Join-Path $fixtureScripts 'sync-drive.ps1'), '-MemorySource', $export)
Assert-That ($conflict.ExitCode -ne 0) 'Conflicting parameter sets were accepted.'
$bindingError = $conflict.Output | ConvertFrom-Json
Assert-That ($bindingError.FullyQualifiedErrorId -eq 'AmbiguousParameterSet,sync-drive.ps1') `
    "Expected AmbiguousParameterSet, received: $($conflict.Output)"
Assert-That ($bindingError.ExceptionType -eq 'System.Management.Automation.ParameterBindingException') `
    "Expected ParameterBindingException, received: $($conflict.Output)"
Assert-That ($conflict.Calls.Count -eq 0) 'Conflicting parameter sets reached rclone.'

foreach ($entryPoint in @('sync-drive.ps1', 'lab.ps1')) {
    foreach ($dryRun in @($true, $false)) {
        $arguments = @('-Memory', 'desktop', '-MemorySource', $export, '-Remote', 'fixture', '-DriveFolder', 'project')
        if ($dryRun) { $arguments += '-DryRun' }
        if ($entryPoint -eq 'lab.ps1') { $arguments = @('drive') + $arguments }
        $result = Invoke-Fixture $entryPoint $arguments
        Assert-That ($result.ExitCode -eq 0) "$entryPoint failed: $($result.Output)"
        Assert-That ($result.Calls.Count -eq 2) "$entryPoint should only list remotes and copy."
        Assert-That (($result.Calls[0].arguments -join '|') -eq 'listremotes') 'Unexpected remote discovery.'
        $expected = @('copy', $export, 'fixture:project/session-memory/codex/desktop',
            '--checksum', '-v', '--stats-one-line', '--stats', '30s')
        if ($dryRun) { $expected += '--dry-run' }
        $expected += @('--include', '*.md')
        Assert-That (($result.Calls[1].arguments -join '|') -eq ($expected -join '|')) "Unexpected copy arguments from $entryPoint."
        if ($dryRun) {
            Assert-That ($result.Output -match 'Dry run: nothing was transferred') 'Dry-run confirmation missing.'
        }
        else {
            Assert-That ($result.Output -match 'Push only') 'Push-only guidance missing.'
            Assert-That ($result.Output -match 'session-memory/codex/<machine>') 'Codex fetch guidance missing.'
        }
    }
}

Write-Host "PASS: $script:caseNumber isolated CLI checks; no network or user memory accessed."
Write-Host "Fixtures retained at $fixture"
