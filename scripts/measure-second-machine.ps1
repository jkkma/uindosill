<#
.SYNOPSIS
    Records a machine, then measures every backend on it in the one order that does not throw away
    a measurement you cannot take twice.

.DESCRIPTION
    `measure-transcribe.ps1` measures a run. This measures a *machine*, and it exists because two
    of the things worth learning from a second machine are destroyed by running things casually
    first.

    The first is the cold Vulkan number. Gotcha 20: the first Vulkan run on a machine that has
    never compiled these shaders is about twice as slow as every run after it, and the cost lands
    inside `processingSec`, which is the field the real-time factor is computed from. There is
    exactly one first run per machine. On NVIDIA it was recoverable by emptying
    `%LOCALAPPDATA%\NVIDIA\GLCache`; on other vendors the cache lives elsewhere and whether
    clearing it reproduces the effect is unknown. So this script refuses to run a warm Vulkan
    measurement before a cold one, and records that it has spent the cold run so a later
    invocation cannot quietly report a warm figure as a first-run one.

    The second is the machine itself. `docs/UNPROVEN.md` had to be corrected once already because
    it described a 16-core CPU as 32-core, and it carried no memory speed at all until the
    hardware turned out to matter. A figure without its machine cannot be reproduced or argued
    with, so the machine block is captured here rather than remembered.

    Everything measured is delegated to `measure-transcribe.ps1`. This script sequences, records
    and collects; it does not time anything itself.

.PARAMETER ColdVulkanAlreadySpent
    Declare that Vulkan has already run on this machine outside this script. The cold measurement
    is then gone and the script says so in its output rather than presenting a warm number as
    a first-run one.

.EXAMPLE
    .\scripts\measure-second-machine.ps1 -Path chunk.m4a

.EXAMPLE
    .\scripts\measure-second-machine.ps1 -Path chunk.m4a -Backends cpu,vulkan -ColdVulkanAlreadySpent
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [string] $Model = 'tdt-0.6b-v3-f16',

    # Order matters and the default is deliberate: vulkan first, while its shader cache is cold.
    [string[]] $Backends = @('vulkan', 'cuda', 'cpu'),

    # Where the per-run JSONs and the machine block are collected. Defaults to runs/<computername>.
    [string] $OutputDirectory,

    [switch] $ColdVulkanAlreadySpent,

    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo

# Every probe is wrapped. A machine block is a diagnostic, and a diagnostic that can abort the run
# it is describing is worse than a missing line — see the nvidia-smi wrapper in
# measure-transcribe.ps1 for the same reasoning arrived at the hard way.
function Probe([string] $label, [scriptblock] $block) {
    try {
        $value = & $block
        if ($null -eq $value -or "$value".Trim().Length -eq 0) { return "$label`: not reported" }
        return "$label`: $value"
    }
    catch {
        # Collapsed to one line: the machine block is rendered as a markdown table further down,
        # and a probe that fails with a multi-line message would break the row it lands in.
        $reason = ($_.Exception.Message -replace '\s+', ' ').Trim()
        return "$label`: probe failed — $reason"
    }
}

try {
    $audio = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
    if (-not $audio) { throw "Audio file not found: $Path" }

    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "runs/$([Environment]::MachineName)" }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

    $coldMarker = Join-Path $OutputDirectory 'cold-vulkan-spent.txt'

    # ── the machine ────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '── machine ─────────────────────────────────────' -ForegroundColor Green

    $machine = @()
    $machine += Probe 'host        ' { [Environment]::MachineName }
    $machine += Probe 'os          ' {
        $os = Get-CimInstance Win32_OperatingSystem
        "$($os.Caption) ($($os.Version)), $($os.OSArchitecture)"
    }
    $machine += Probe 'cpu         ' {
        $cpu = @(Get-CimInstance Win32_Processor)[0]
        "$($cpu.Name.Trim()) — $($cpu.NumberOfCores) cores, $($cpu.NumberOfLogicalProcessors) threads"
    }

    # Cores and threads separately, because this is the line that was wrong in the docs: a
    # 16-core/32-thread part was recorded as "32-core", which doubled the machine in every
    # extrapolation that referred to it.
    $machine += Probe 'memory      ' {
        $sticks = @(Get-CimInstance Win32_PhysicalMemory)
        $total = ($sticks | Measure-Object -Property Capacity -Sum).Sum / 1GB
        # ConfiguredClockSpeed is what the memory is actually running at; Speed is what the part is
        # rated for. They differ under XMP/EXPO and on soldered LPDDR, and the configured one is
        # the number that bounds anything bandwidth-limited.
        $configured = ($sticks | Where-Object { $_.ConfiguredClockSpeed } | Select-Object -First 1).ConfiguredClockSpeed
        $rated = ($sticks | Where-Object { $_.Speed } | Select-Object -First 1).Speed
        "{0:N0} GB across {1} module(s), configured {2} MT/s (rated {3})" -f $total, $sticks.Count, ($configured ?? '?'), ($rated ?? '?')
    }
    $machine += Probe 'gpu         ' {
        (@(Get-CimInstance Win32_VideoController) | ForEach-Object {
            "$($_.Name.Trim()) driver $($_.DriverVersion)"
        }) -join '; '
    }
    $machine += Probe 'storage     ' {
        (@(Get-PhysicalDisk | Where-Object { $_.BusType -ne 'USB' }) | ForEach-Object {
            "$($_.FriendlyName) $([Math]::Round($_.Size / 1GB)) GB $($_.MediaType)"
        }) -join '; '
    }

    # A laptop timing without the power state is the same species of unreproducible number as a GPU
    # timing without a driver version. Both machines this project measures are now capable of
    # throttling for reasons the transcript cannot see.
    $machine += Probe 'power       ' {
        $battery = @(Get-CimInstance Win32_Battery)
        $onAc = if ($battery.Count -eq 0) { 'desktop, no battery' }
                elseif ($battery[0].BatteryStatus -eq 2) { 'plugged in' }
                else { "ON BATTERY (status $($battery[0].BatteryStatus))" }
        $scheme = (powercfg /getactivescheme) -replace '.*\(', '' -replace '\).*', ''
        "$onAc, power scheme '$scheme'"
    }
    $machine += Probe 'runtime     ' { "$(dotnet --version) SDK" }

    $machine | ForEach-Object { Write-Host "  $_" }
    $machine | Set-Content -LiteralPath (Join-Path $OutputDirectory 'machine.txt')

    # ── the cold-Vulkan guard ──────────────────────────────────────────────────
    $wantsVulkan = $Backends -contains 'vulkan'
    $coldSpent = $ColdVulkanAlreadySpent -or (Test-Path -LiteralPath $coldMarker)

    if ($wantsVulkan) {
        Write-Host ''
        if ($coldSpent) {
            $why = if ($ColdVulkanAlreadySpent) { 'declared on the command line' } else { "recorded in $coldMarker" }
            Write-Host "  Vulkan has already run on this machine ($why)." -ForegroundColor Yellow
            Write-Host '  The first-run figure is gone and cannot be retaken. Anything measured now is' -ForegroundColor Yellow
            Write-Host '  a STEADY-STATE number and must not be quoted as a first run. See gotcha 20.' -ForegroundColor Yellow
        }
        else {
            Write-Host '  Vulkan has not run on this machine through this script, so the next Vulkan' -ForegroundColor Cyan
            Write-Host '  measurement is the COLD one — the only one that can ever be taken here.' -ForegroundColor Cyan
            Write-Host '  If Vulkan has run outside this script, stop and re-run with' -ForegroundColor Cyan
            Write-Host '  -ColdVulkanAlreadySpent so the figure is not mislabelled.' -ForegroundColor Cyan
        }
    }

    if ($Backends[0] -ne 'vulkan' -and $wantsVulkan -and -not $coldSpent) {
        throw "Vulkan is in -Backends but is not first, and its cold run has not been spent. " +
              "Running another backend first is harmless, but the ordering suggests the cold run " +
              "was not the intent. Put vulkan first, or pass -ColdVulkanAlreadySpent."
    }

    # ── the runs ───────────────────────────────────────────────────────────────
    $stem = [IO.Path]::GetFileNameWithoutExtension($audio.Path)
    $summary = @()

    foreach ($backend in $Backends) {
        $isColdVulkan = ($backend -eq 'vulkan') -and -not $coldSpent
        $label = if ($isColdVulkan) { 'vulkan (COLD — first ever run)' } else { $backend }

        Write-Host ''
        Write-Host "══ $label ═══════════════════════════════════" -ForegroundColor Magenta

        $before = (Get-Date).AddSeconds(-2)
        $suffix = if ($isColdVulkan) { 'vulkan-cold' } else { $backend }
        $runDirectory = Join-Path $OutputDirectory $suffix
        $arguments = @{
            Path            = $audio.Path
            Model           = $Model
            Backend         = $backend
            Formats         = @('json')
            OutputDirectory = $runDirectory
        }
        # Only the first run builds; the rest reuse it. Building between runs would put a compile
        # between two timings that are meant to be comparable.
        if ($SkipBuild -or $summary.Count -gt 0) { $arguments['SkipBuild'] = $true }

        & (Join-Path $PSScriptRoot 'measure-transcribe.ps1') @arguments

        # Each backend writes into its own directory, so there is nothing to disambiguate. The
        # freshness filter stays anyway: it costs nothing and it is the only thing standing between
        # a re-run into an existing folder and a stale transcript reported as this run's.
        $fresh = @(Get-ChildItem -LiteralPath $runDirectory -Filter '*.json' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $before } |
            Sort-Object LastWriteTime)

        if ($fresh.Count -eq 0) {
            Write-Host "  no JSON from this run — nothing collected for $backend" -ForegroundColor Red
            # Null rather than a placeholder string. A placeholder compares unequal to the
            # requested backend further down and gets reported as a fallback, which is a claim
            # about a run that measured nothing at all.
            $summary += [PSCustomObject]@{ Requested = $backend; Loaded = $null; Rtf = $null; Cold = $isColdVulkan; File = $null }
            continue
        }

        $destination = Join-Path $OutputDirectory "$stem-$suffix.json"
        Copy-Item -LiteralPath $fresh[-1].FullName -Destination $destination -Force

        $document = Get-Content -LiteralPath $destination -Raw | ConvertFrom-Json
        $summary += [PSCustomObject]@{
            Requested = $backend
            Loaded    = $document.backend
            Rtf       = $document.realTimeFactor
            Cold      = $isColdVulkan
            File      = $destination
        }

        if ($isColdVulkan) {
            "Cold Vulkan run spent $(Get-Date -Format o). RTF $($document.realTimeFactor), processingSec $($document.processingSec)." |
                Set-Content -LiteralPath $coldMarker
            $coldSpent = $true
        }
    }

    # ── what the runs say ──────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '── backends ────────────────────────────────────' -ForegroundColor Green
    foreach ($run in $summary) {
        $note = if (-not $run.Loaded) { '  <- the run failed; nothing was measured' }
                elseif ($run.Cold) { '  <- cold, first ever run on this machine' }
                elseif ($run.Requested -ne $run.Loaded) { "  <- FELL BACK to $($run.Loaded)" }
                else { '' }
        $colour = if (-not $run.Loaded) { 'Red' } else { 'Gray' }
        Write-Host ("  {0,-8} loaded {1,-8} rtf {2,-8} {3}" -f
            $run.Requested, ($run.Loaded ?? '—'), ($run.Rtf ?? '—'), $note) -ForegroundColor $colour
    }

    # Gotcha 17 is recorded as read out of the loader's code path rather than reproduced: a CUDA
    # request on a machine with no usable CUDA should land on CPU, and the transcript's `backend`
    # field should say so rather than echoing what was asked for. A machine without an NVIDIA GPU
    # is the only place that can actually be observed.
    $cuda = $summary | Where-Object { $_.Requested -eq 'cuda' } | Select-Object -First 1
    if ($cuda -and $cuda.Loaded) {
        Write-Host ''
        Write-Host '── the CUDA fallback, reproduced or not ────────' -ForegroundColor Green
        if ($cuda.Loaded -eq 'cuda') {
            Write-Host '  CUDA loaded. This machine cannot test the fallback path.'
        }
        elseif ($cuda.Loaded -eq 'cpu') {
            Write-Host '  CUDA was requested and CPU loaded, and the transcript says "cpu".' -ForegroundColor Green
            Write-Host '  That is gotcha 17 reproduced rather than reasoned about: the fallback is'
            Write-Host '  silent in the run and honest in the output.'
        }
        else {
            Write-Host "  CUDA was requested and '$($cuda.Loaded)' loaded, which is neither cuda nor cpu" -ForegroundColor Red
            Write-Host '  and contradicts the documented order in docs/NATIVE-BINARIES.md.' -ForegroundColor Red
        }
    }

    # ── a block that can be pasted into docs/UNPROVEN.md ───────────────────────
    $block = @()
    $block += '| | Machine |'
    $block += '|---|---|'
    foreach ($line in $machine) {
        $name, $value = $line -split ':', 2
        $block += "| $($name.Trim()) | $($value.Trim()) |"
    }
    $block += ''
    $block += '| Requested | Loaded | Real-time factor |'
    $block += '|---|---|---|'
    foreach ($run in $summary) {
        if (-not $run.Loaded) {
            $block += "| $($run.Requested) | *the run failed — nothing measured* | — |"
            continue
        }
        $tag = if ($run.Cold) { ' (cold, first ever run)' } else { '' }
        $block += "| $($run.Requested)$tag | $($run.Loaded) | $($run.Rtf) |"
    }

    $blockPath = Join-Path $OutputDirectory 'unproven-block.md'
    $block | Set-Content -LiteralPath $blockPath

    Write-Host ''
    Write-Host '── collected ───────────────────────────────────' -ForegroundColor Green
    Write-Host "  transcripts and machine block in $OutputDirectory"
    Write-Host "  paste-ready table          $blockPath"
    Write-Host ''
    Write-Host '  These are one run each on one machine. Nothing here is a benchmark, and a GPU'
    Write-Host '  figure from an unfamiliar machine is worth distrusting until it has been run twice.'
    Write-Host ''
    Write-Host '  Cross-machine determinism, once another machine has a CPU transcript:'
    Write-Host "    .\scripts\compare-transcripts.ps1 -Reference <other>\$stem-cpu.json -Candidate $OutputDirectory\$stem-cpu.json"
}
finally {
    Pop-Location
}
