<#
.SYNOPSIS
    One entry point for the measurement and vendoring scripts.

.DESCRIPTION
    Four scripts with four names and four flag sets is three names too many to remember when you
    are switching between machines. This dispatches to them and nothing else: every task is still
    a script you can run directly, and this changes none of their behaviour.

    It is a dispatcher rather than a merge because the scripts it calls produced every number in
    `docs/UNPROVEN.md`. Rewriting them into one file to save a filename would put that at risk for
    no measurement gained.

    Two things here are load-bearing rather than convenience.

    **Parameters are declared, not forwarded blind.** The obvious implementation —
    `[Parameter(ValueFromRemainingArguments)] $Rest` and `& $script @Rest` — does not work, and
    fails in a way that looks like it should. An unquoted `-Formats srt,txt,json` is an array by
    the time the dispatcher sees it, and lands in `$Rest` stringified as `System.Object[]`, so the
    formats are destroyed before anything is called. Splatting an array then passes every element
    positionally, so even `-Path` is not recognised as a parameter name. Declaring the parameters
    and splatting `$PSBoundParameters` — a hashtable — preserves both the array and the names.

    **Drift is caught mechanically.** The parameters each task accepts are read from the target
    script at runtime with `Get-Command`, never listed here. Pass something a task does not take
    and it says so by name; add a parameter to one of those scripts and this file cannot silently
    fail to pass it, because the listing below reads the real thing and marks anything it cannot
    forward.

.EXAMPLE
    .\scripts\lab.ps1
    Lists the tasks, each with the parameters its own script actually declares.

.EXAMPLE
    .\scripts\lab.ps1 measure -Path chunk.m4a -Backend cuda -Formats srt,txt,json,vtt,vtt-words

.EXAMPLE
    .\scripts\lab.ps1 machine -Path chunk.m4a

.EXAMPLE
    .\scripts\lab.ps1 compare -Reference runs\A\chunk-cpu.json -Candidate runs\B\chunk-cpu.json
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('measure', 'machine', 'compare', 'vendor-cuda')]
    [string] $Task,

    # --- measure / machine ---
    [string] $Path,
    [string] $Model,
    [ValidateSet('cpu', 'vulkan', 'cuda')]
    [string] $Backend,
    [string[]] $Backends,
    [string[]] $Formats,
    [string] $OutputDirectory,
    [string] $Configuration,
    [string] $MemoryCsv,
    [switch] $ColdVulkanAlreadySpent,
    [switch] $SkipBuild,

    # --- compare ---
    [string] $Reference,
    [string] $Candidate,
    [double] $TimeEpsilon,
    [double] $ConfidenceEpsilon,
    [switch] $ShowWords,
    [switch] $ShowTimestamps,

    # --- vendor-cuda ---
    [string] $LibArchive,
    [string] $CudartArchive,
    [string] $Destination,
    [switch] $InspectOnly,
    [switch] $SkipArchScan,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tasks = [ordered]@{
    'measure'     = 'measure-transcribe.ps1'
    'machine'     = 'measure-second-machine.ps1'
    'compare'     = 'compare-transcripts.ps1'
    'vendor-cuda' = 'vendor-cuda.ps1'
}

# What this file declares, so the listing can mark anything a task takes and this cannot pass on.
$declared = @($MyInvocation.MyCommand.Parameters.Keys)

function Resolve-Task([string] $name) {
    $target = Join-Path $PSScriptRoot $tasks[$name]
    if (-not (Test-Path -LiteralPath $target)) { throw "Task '$name' points at $target, which is not there." }
    return $target
}

function Get-TaskParameters([string] $target) {
    $common = [System.Management.Automation.PSCmdlet]::CommonParameters +
              [System.Management.Automation.PSCmdlet]::OptionalCommonParameters
    return @((Get-Command $target).Parameters.Keys | Where-Object { $_ -notin $common })
}

if (-not $Task) {
    Write-Host ''
    Write-Host 'uindosill lab — one entry point for the measurement and vendoring scripts' -ForegroundColor Cyan
    Write-Host ''

    foreach ($name in $tasks.Keys) {
        $target = Resolve-Task $name

        # Read from the script rather than repeated here, so this listing cannot go stale.
        $synopsis = (Get-Help $target -ErrorAction SilentlyContinue).Synopsis
        $synopsis = ($synopsis -replace '\s+', ' ').Trim()
        if ($synopsis.Length -gt 92) { $synopsis = $synopsis.Substring(0, 89) + '...' }

        Write-Host ("  {0,-12} {1}" -f $name, $synopsis) -ForegroundColor Green
        Write-Host ("  {0,-12} {1}" -f '', $tasks[$name]) -ForegroundColor DarkGray

        $rendered = Get-TaskParameters $target | Sort-Object | ForEach-Object {
            # A '!' means the task accepts it and this dispatcher cannot pass it on — call the
            # script directly for that one. It is here so drift is visible rather than silent.
            if ($_ -in $declared) { "-$_" } else { "!-$_" }
        }
        Write-Host ("  {0,-12} {1}" -f '', ($rendered -join ' '))
        Write-Host ''
    }

    Write-Host '  A leading ! marks a parameter the task accepts that this dispatcher does not declare;' -ForegroundColor DarkGray
    Write-Host '  run that script directly to use it. Every task is runnable on its own.' -ForegroundColor DarkGray
    Write-Host ''
    return
}

$target = Resolve-Task $Task
$accepted = @((Get-Command $target).Parameters.Keys)

$forward = @{}
$rejected = @()

foreach ($name in $PSBoundParameters.Keys) {
    if ($name -eq 'Task') { continue }
    if ($name -in $accepted) { $forward[$name] = $PSBoundParameters[$name] }
    else { $rejected += $name }
}

if ($rejected.Count -gt 0) {
    $usable = (Get-TaskParameters $target | Sort-Object | ForEach-Object { "-$_" }) -join ' '
    throw "Task '$Task' ($($tasks[$Task])) does not take: $(($rejected | ForEach-Object { "-$_" }) -join ', ').`n" +
          "It takes: $usable"
}

& $target @forward
