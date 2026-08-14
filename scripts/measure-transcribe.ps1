<#
.SYNOPSIS
    Transcribes one file and reports the numbers that matter: elapsed time, peak memory,
    real-time factor, and the segment and timeline invariants.

.DESCRIPTION
    Peak working set is read from the process itself rather than sampled, so it cannot be
    missed between polls. That means the executable has to be launched directly: running it
    through `dotnet run` would measure the launcher instead of the application.

    On a long recording the memory figure is the interesting one. The segmenter holds one batch
    of audio at a time regardless of file length, so peak should sit a little above the model
    size (1.34 GiB for f16) and stay there. Memory that climbs with duration means something
    accumulates that should not.

.EXAMPLE
    .\scripts\measure-transcribe.ps1 -Path CSB384.mp3

.EXAMPLE
    .\scripts\measure-transcribe.ps1 -Path long.wav -Model tdt-0.6b-v3-q8_0 -Backend vulkan
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [string] $Model = 'tdt-0.6b-v3-f16',

    [ValidateSet('cpu', 'vulkan', 'cuda')]
    [string] $Backend = 'cpu',

    [string] $Formats = 'srt,txt,json',

    [string] $Configuration = 'Release',

    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo

try {
    $audio = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
    if (-not $audio) {
        throw "Audio file not found: $Path"
    }

    if (-not $SkipBuild) {
        Write-Host 'Building...' -ForegroundColor Cyan
        dotnet build src/Parakeet.Cli -c $Configuration --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }

    $exe = Join-Path $repo "src/Parakeet.Cli/bin/$Configuration/net10.0/uindosill.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        $exe = Join-Path $repo "src/Parakeet.Cli/bin/$Configuration/net10.0/uindosill"
    }
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Built executable not found. Run without -SkipBuild, or check bin/$Configuration/net10.0."
    }

    $arguments = @(
        'transcribe'
        '--backend'; $Backend
        '--model'; $Model
        '-f'; $Formats
        $audio.Path
    )

    Write-Host "Transcribing $($audio.Path)" -ForegroundColor Cyan
    Write-Host "  model $Model, backend $Backend, formats $Formats"
    Write-Host ''

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -PassThru -Wait
    $stopwatch.Stop()

    $peakMb = $process.PeakWorkingSet64 / 1MB

    Write-Host ''
    Write-Host '── run ─────────────────────────────────────────' -ForegroundColor Green
    Write-Host ("exit code      : {0}" -f $process.ExitCode)
    Write-Host ("elapsed        : {0:hh\:mm\:ss}" -f $stopwatch.Elapsed)
    Write-Host ("peak memory    : {0:N0} MB" -f $peakMb)

    $stem = [IO.Path]::GetFileNameWithoutExtension($audio.Path)
    $directory = Split-Path -Parent $audio.Path

    Write-Host ''
    Write-Host '── outputs ─────────────────────────────────────' -ForegroundColor Green
    foreach ($extension in $Formats.Split(',')) {
        $outputPath = Join-Path $directory "$stem.$($extension.Trim())"
        if (Test-Path -LiteralPath $outputPath) {
            $size = (Get-Item -LiteralPath $outputPath).Length
            Write-Host ("{0,-28} {1,12:N0} bytes" -f (Split-Path -Leaf $outputPath), $size)
        }
        else {
            Write-Host ("{0,-28} MISSING" -f (Split-Path -Leaf $outputPath)) -ForegroundColor Yellow
        }
    }

    # The invariant checks. These are the same properties asserted in the unit tests, applied to
    # a real transcript at whatever scale this file happens to be, because timeline drift and
    # accumulation bugs only show up after a few hundred segments.
    $jsonPath = Join-Path $directory "$stem.json"
    if (-not (Test-Path -LiteralPath $jsonPath)) {
        Write-Host ''
        Write-Host 'No JSON output, so the invariant checks were skipped. Add json to -Formats.' -ForegroundColor Yellow
        return
    }

    $document = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    $segments = @($document.segments)

    if ($segments.Count -eq 0) {
        Write-Host ''
        Write-Host 'The transcript is empty — nothing to check.' -ForegroundColor Yellow
        return
    }

    $durations = $segments | ForEach-Object { $_.end - $_.start }
    $longest = ($durations | Measure-Object -Maximum).Maximum
    $covered = ($durations | Measure-Object -Sum).Sum
    $atCap = @($durations | Where-Object { $_ -ge 29.5 }).Count

    $largestGap = 0.0
    for ($i = 1; $i -lt $segments.Count; $i++) {
        $gap = $segments[$i].start - $segments[$i - 1].end
        if ($gap -gt $largestGap) { $largestGap = $gap }
    }

    $words = @($segments | ForEach-Object { $_.words } | Where-Object { $_ })
    $nonMonotonic = 0
    $pastEnd = 0
    $previousStart = -1.0
    foreach ($word in $words) {
        if ($word.start -lt $previousStart) { $nonMonotonic++ }
        $previousStart = $word.start
        if ($word.end -gt $document.audioDurationSec) { $pastEnd++ }
    }

    Write-Host ''
    Write-Host '── transcript ──────────────────────────────────' -ForegroundColor Green
    Write-Host ("audio duration : {0:N1} s" -f $document.audioDurationSec)
    Write-Host ("decode time    : {0:N1} s" -f $document.processingSec)
    Write-Host ("real-time factor: {0:N4}" -f $document.realTimeFactor)
    Write-Host ("model          : {0} ({1}) on {2}" -f $document.model, $document.quantisation, $document.backend)
    Write-Host ''
    Write-Host ("segments       : {0}" -f $segments.Count)
    Write-Host ("longest segment: {0:N2} s   (cap is 30)" -f $longest)
    Write-Host ("hit the cap    : {0}" -f $atCap)
    Write-Host ("largest gap    : {0:N2} s" -f $largestGap)
    Write-Host ("coverage       : {0:N1} s of {1:N1} s  ({2:N1}%)" -f $covered, $document.audioDurationSec, (100 * $covered / $document.audioDurationSec))
    Write-Host ''
    Write-Host ("words          : {0:N0}" -f $words.Count)

    $monotonicColour = if ($nonMonotonic -eq 0) { 'Green' } else { 'Red' }
    $pastEndColour = if ($pastEnd -eq 0) { 'Green' } else { 'Red' }
    Write-Host ("non-monotonic  : {0}" -f $nonMonotonic) -ForegroundColor $monotonicColour
    Write-Host ("past end of audio: {0}" -f $pastEnd) -ForegroundColor $pastEndColour

    if ($atCap -eq 0) {
        Write-Host ''
        Write-Host 'No segment reached the 30 s cap, so the forced-cut path did not run on this file.' -ForegroundColor Yellow
    }
}
finally {
    Pop-Location
}
