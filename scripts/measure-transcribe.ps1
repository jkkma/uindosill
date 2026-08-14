<#
.SYNOPSIS
    Transcribes one file and reports the numbers that matter: elapsed time, memory over the whole
    run, real-time factor, and the segment and timeline invariants.

.DESCRIPTION
    Memory is sampled rather than taken at the end, because the question on a long recording is not
    only how high it got but whether it climbed. The segmenter holds one batch of audio at a time
    regardless of file length, so working set should rise while the model loads and then hold. A
    figure that grows with duration means something accumulates that should not — and a single peak
    cannot tell the two apart, since a peak reached in the first minute and one reached in the last
    read exactly the same.

    Sampling requires the process to stay addressable, so the executable is launched directly and
    polled. Running it through `dotnet run` would measure the launcher instead of the application.

.EXAMPLE
    .\scripts\measure-transcribe.ps1 -Path CSB384.mp3

.EXAMPLE
    .\scripts\measure-transcribe.ps1 -Path long.wav -Model tdt-0.6b-v3-q8_0 -Backend vulkan

.EXAMPLE
    .\scripts\measure-transcribe.ps1 -Path CSB384.mp3 -MemoryCsv memory.csv
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

    # Write every working-set sample here as CSV, for plotting the shape rather than reading the
    # summary. Columns: elapsedSeconds,workingSetMb.
    [string] $MemoryCsv,

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

    # Anything older than this is a leftover from an earlier run, not an output of this one. The
    # writer renames rather than clobbers, so an existing chunk.srt makes this run write
    # "chunk (2).srt" — reconstructing the name instead of checking the timestamp reports the size
    # of the stale file and calls it a result.
    $runStart = (Get-Date).AddSeconds(-2)

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -PassThru

    # PeakWorkingSet64 has to be read while the process is still alive. Windows discards the
    # counter when the process exits, and the property then reads back as zero rather than
    # failing, so `Start-Process -Wait` reports a peak of 0 MB for a run that used gigabytes.
    # The peak never falls, so keeping the highest value seen is exact. WorkingSet64 is the
    # instantaneous figure and is collected alongside it to show the shape.
    $peakBytes = 0L
    $samples = [Collections.Generic.List[object]]::new()

    while (-not $process.HasExited) {
        try {
            $process.Refresh()
            if ($process.PeakWorkingSet64 -gt $peakBytes) { $peakBytes = $process.PeakWorkingSet64 }
            $samples.Add([PSCustomObject]@{
                elapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
                workingSetMb   = [Math]::Round($process.WorkingSet64 / 1MB, 1)
            })
        }
        catch {
            # The process exited between HasExited and Refresh. The samples so far stand.
        }

        Start-Sleep -Milliseconds 250
    }

    $process.WaitForExit()
    $stopwatch.Stop()

    Write-Host ''
    Write-Host '── run ─────────────────────────────────────────' -ForegroundColor Green
    Write-Host ("exit code      : {0}" -f $process.ExitCode)
    Write-Host ("elapsed        : {0:hh\:mm\:ss}" -f $stopwatch.Elapsed)

    if ($peakBytes -gt 0) {
        Write-Host ("peak memory    : {0:N0} MB" -f ($peakBytes / 1MB))
    }
    else {
        Write-Host 'peak memory    : not sampled (the run finished inside one poll interval)' -ForegroundColor Yellow
    }

    # A peak is a high-water mark with no time axis, so it bounds memory without showing whether it
    # climbed. The profile below is the measurement that tells them apart.
    if ($samples.Count -ge 10) {
        Write-Host ''
        Write-Host '── working set over the run ────────────────────' -ForegroundColor Green

        $bucketCount = 10
        $perBucket = [Math]::Floor($samples.Count / $bucketCount)
        $means = @()
        for ($b = 0; $b -lt $bucketCount; $b++) {
            $slice = $samples[($b * $perBucket)..(($b + 1) * $perBucket - 1)]
            $means += ($slice | Measure-Object -Property workingSetMb -Average).Average
        }

        $widest = ($means | Measure-Object -Maximum).Maximum
        for ($b = 0; $b -lt $bucketCount; $b++) {
            $bars = [Math]::Max(1, [int](40 * $means[$b] / $widest))
            Write-Host ("{0,3}% {1,8:N0} MB  {2}" -f (10 * ($b + 1)), $means[$b], ('█' * $bars))
        }

        # Compare the first tenth against the last. The model load dominates the very start, so a
        # decode that holds steady shows a small positive delta and one that accumulates does not.
        $growth = $means[$bucketCount - 1] - $means[0]
        $growthPercent = 100 * $growth / $means[0]
        $verdict = if ([Math]::Abs($growthPercent) -lt 10) { 'Green' } else { 'Yellow' }
        Write-Host ''
        Write-Host ("last tenth vs first: {0:+#,##0;-#,##0;0} MB ({1:+0.0;-0.0;0}%)" -f $growth, $growthPercent) -ForegroundColor $verdict

        if ($MemoryCsv) {
            $samples | Export-Csv -LiteralPath $MemoryCsv -NoTypeInformation
            Write-Host ("samples written to $MemoryCsv ({0} rows)" -f $samples.Count)
        }
    }

    $stem = [IO.Path]::GetFileNameWithoutExtension($audio.Path)
    $directory = Split-Path -Parent $audio.Path

    # Report the files this run actually wrote, found by timestamp, rather than the names it was
    # assumed to have used.
    Write-Host ''
    Write-Host '── outputs ─────────────────────────────────────' -ForegroundColor Green
    $freshByExtension = @{}
    foreach ($extension in $Formats.Split(',')) {
        $ext = $extension.Trim()
        $fresh = @(Get-ChildItem -LiteralPath $directory -Filter "$stem*.$ext" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $runStart } | Sort-Object LastWriteTime)

        if ($fresh.Count -eq 0) {
            Write-Host ("{0,-28} NOT WRITTEN BY THIS RUN" -f "$stem.$ext") -ForegroundColor Yellow
            continue
        }

        $freshByExtension[$ext] = $fresh[-1].FullName
        foreach ($file in $fresh) {
            Write-Host ("{0,-28} {1,12:N0} bytes" -f $file.Name, $file.Length)
        }
    }

    # The invariant checks. These are the same properties asserted in the unit tests, applied to
    # a real transcript at whatever scale this file happens to be, because timeline drift and
    # accumulation bugs only show up after a few hundred segments.
    if (-not $freshByExtension.ContainsKey('json')) {
        Write-Host ''
        Write-Host 'No JSON output from this run, so the invariant checks were skipped. Add json to -Formats.' -ForegroundColor Yellow
        return
    }

    $document = Get-Content -LiteralPath $freshByExtension['json'] -Raw | ConvertFrom-Json
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
