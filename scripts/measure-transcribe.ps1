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

    # Both shapes PowerShell can hand this. Unquoted, `-Formats srt,txt,vtt-words` arrives as an
    # array, because a comma is the array operator in argument mode; quoted, it arrives as one
    # string. It has to be [string[]]: [CmdletBinding()] above makes this script an advanced
    # function, and an advanced function refuses to collapse an array into a [string] parameter
    # rather than joining it, so `[string] $Formats` failed the unquoted form outright with
    # "Cannot process argument transformation on parameter 'Formats'". Latent until now only
    # because every run recorded in UNPROVEN.md in the project notes took the default.
    [string[]] $Formats = @('srt', 'txt', 'json'),

    # Where the transcripts land. The default is a fresh timestamped folder per run, because the
    # alternative is the CLI's own default — beside the input file — and the input files for this
    # project live in the repository root. Fourteen runs of chunk.m4a leave fourteen transcripts
    # there, `git status` fills with them, and the rename policy turns the fifteenth into
    # "chunk (15).json". A folder per run also means nothing stale can be in it, which makes the
    # freshness check below belt-and-braces rather than load-bearing — it stays, because a caller
    # who passes an existing directory brings the staleness back with them.
    [string] $OutputDirectory,

    [string] $Configuration = 'Release',

    # Write every working-set sample here as CSV, for plotting the shape rather than reading the
    # summary. Columns: elapsedSeconds,workingSetMb.
    [string] $MemoryCsv,

    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# [TimeSpan]::Parse rejects "25:00:00.000" — hh is a 0-23 field there and a subtitle timecode's
# hours deliberately do not wrap. Read the four fields directly instead.
function ConvertFrom-VttTimecode([string] $value) {
    $parts = $value.Trim() -split '[:.]'
    return [TimeSpan]::new(0, [int]$parts[0], [int]$parts[1], [int]$parts[2], [int]$parts[3])
}

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo

try {
    $audio = Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue
    if (-not $audio) {
        throw "Audio file not found: $Path"
    }

    # Flatten both accepted shapes to one list. Joining first and splitting again handles the
    # mixed case too — `-Formats srt,'txt,json'` is an array whose second element has a comma in
    # it, and a caller has no reason to know that is unusual.
    $formatIds = @(($Formats -join ',') -split ',' |
        ForEach-Object { $_.Trim().ToLowerInvariant() } |
        Where-Object { $_.Length -gt 0 })

    if ($formatIds.Count -eq 0) {
        throw 'No output formats requested. Pass -Formats with at least one id, e.g. srt,txt,json.'
    }

    # The CLI takes them comma separated on one argument. Passing the array straight into the
    # argument list would splat one element per argument, and the CLI would read the extras as
    # positional input paths rather than as formats.
    $formatArgument = $formatIds -join ','

    if (-not $OutputDirectory) {
        $OutputDirectory = Join-Path $repo ("runs/{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Backend)
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

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
        '-f'; $formatArgument
        '-o'; $OutputDirectory
        $audio.Path
    )

    Write-Host "Transcribing $($audio.Path)" -ForegroundColor Cyan
    Write-Host "  model $Model, backend $Backend, formats $formatArgument"
    Write-Host "  writing to $OutputDirectory"

    # A GPU timing with no driver version attached cannot be reproduced or argued with later. The
    # Vulkan figure first recorded for this file was 0.0230 and re-measured at 0.0110 on the same
    # machine and the same binary. It turned out to be a cold shader cache (gotcha 20), and it took
    # an experiment to establish that only because nothing had written down the driver, the clock
    # state, or whether it was a first run. Cheap to record, impossible to recover afterwards.
    #
    # What is still not recorded, and cannot easily be, is whether the driver's shader cache was
    # warm. Run a GPU backend twice on an unfamiliar machine before believing either number.
    if ($Backend -ne 'cpu') {
        Write-Host ''
        Write-Host '── device ──────────────────────────────────────' -ForegroundColor Green

        # Wrapped, because $ErrorActionPreference is Stop and this block runs before the measurement
        # starts. A native command writing to stderr under Stop can raise a terminating
        # NativeCommandError rather than falling through to the else below — which is exactly the
        # case this exists to survive, a driver present but not answering. A diagnostic that can
        # abort the run it is describing is worse than no diagnostic.
        try {
            if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
                $query = 'name,driver_version,pstate,clocks.sm,clocks.max.sm,temperature.gpu'
                $reported = @(& nvidia-smi --query-gpu=$query --format=csv,noheader 2>&1 |
                    Where-Object { $_ -is [string] })

                if ($LASTEXITCODE -eq 0 -and $reported.Count -gt 0) {
                    foreach ($gpu in $reported) {
                        Write-Host ("  {0}" -f $gpu.Trim())
                    }

                    # Sampled before the run starts, so it describes the idle state rather than the
                    # state under load. It bounds the question rather than answering it.
                    Write-Host '  (sampled at rest — not the clock the run will actually see)'
                }
                else {
                    Write-Host '  nvidia-smi is on PATH but reported nothing'
                }
            }
            else {
                Write-Host '  no nvidia-smi on PATH — GPU and driver version not recorded for this run'
            }
        }
        catch {
            Write-Host ("  device query failed: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        }
    }

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

    # Said once, loudly, where the exit code is. Everything below this point describes a run that
    # did not happen: elapsed is the time taken to fail, peak memory is the process starting up,
    # and every output row will read NOT WRITTEN BY THIS RUN. The rows are individually honest and
    # collectively read as a report, and a report of nothing looks a great deal like a report of
    # something when it is eighty lines long. Not fatal, deliberately — the diagnostics above are
    # worth having when a run fails, and gotcha 19 is the case for reading the exit code rather
    # than the noise around it.
    if ($process.ExitCode -ne 0) {
        Write-Host ''
        Write-Host '  THE RUN FAILED. Nothing below measures anything — see the error above.' -ForegroundColor Red

        # The one that has actually happened, twice: -SkipBuild reusing an executable built from a
        # different commit, which does not know a format this one is asking for.
        if ($SkipBuild) {
            Write-Host '  -SkipBuild is in use, so this is whatever was built last. Rebuild if you' -ForegroundColor Red
            Write-Host '  have changed branch: dotnet build src/Parakeet.Cli -c Release' -ForegroundColor Red
        }
    }

    if ($peakBytes -gt 0) {
        Write-Host ("peak memory    : {0:N0} MB" -f ($peakBytes / 1MB))
    }
    else {
        Write-Host 'peak memory    : not sampled (the run finished inside one poll interval)' -ForegroundColor Yellow
    }

    # Working set is host RAM. On a GPU backend the weights live in device memory, which this
    # counter cannot see, so the figure drops sharply and means something different -- it is not
    # comparable with a CPU run and is not the machine's total memory use.
    if ($Backend -ne 'cpu') {
        Write-Host "                 (host RAM only — VRAM held by the $Backend backend is not counted)" -ForegroundColor Yellow
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

        # What matters is whether the curve is still rising at the end, not how the last tenth
        # compares to the first. The model load and the heap warming up both land in the opening
        # tenth, so a healthy run legitimately ends well above where it started while having
        # peaked and settled in between — comparing the ends alone calls that a leak.
        $peakBucket = 0
        for ($b = 1; $b -lt $bucketCount; $b++) {
            if ($means[$b] -gt $means[$peakBucket]) { $peakBucket = $b }
        }

        $fromStart = $means[$bucketCount - 1] - $means[0]
        $fromPeak = $means[$bucketCount - 1] - $means[$peakBucket]

        Write-Host ''
        Write-Host ("last tenth vs first : {0:+#,##0;-#,##0;0} MB" -f $fromStart)
        Write-Host ("peaked at           : {0}% of the run" -f (10 * ($peakBucket + 1)))

        if ($peakBucket -ge $bucketCount - 2) {
            Write-Host 'still rising at the end — memory may accumulate with length' -ForegroundColor Yellow
        }
        else {
            Write-Host ("settled             : {0:N0} MB given back after the peak" -f [Math]::Abs($fromPeak)) -ForegroundColor Green
        }

        if ($MemoryCsv) {
            $samples | Export-Csv -LiteralPath $MemoryCsv -NoTypeInformation
            Write-Host ("samples written to $MemoryCsv ({0} rows)" -f $samples.Count)
        }
    }

    $stem = [IO.Path]::GetFileNameWithoutExtension($audio.Path)
    $directory = $OutputDirectory

    # Report the files this run actually wrote, found by timestamp, rather than the names it was
    # assumed to have used.
    Write-Host ''
    Write-Host '── outputs ─────────────────────────────────────' -ForegroundColor Green
    # A format id is not a file extension, and assuming it is reproduces gotcha 16 from a new
    # direction. 'vtt-words' writes '<stem>.words.vtt', so a wildcard on '.vtt' matches that file
    # as well as the plain one and the 'vtt' row reports whichever was written last — a size that
    # looks like a measurement of one output and is a measurement of another. Map the id to the
    # extension the writer actually uses, and match the whole name rather than a suffix.
    $extensionByFormat = @{
        'txt'          = '.txt'
        'text'         = '.txt'
        'plain'        = '.txt'
        'srt'          = '.srt'
        'subrip'       = '.srt'
        'vtt'          = '.vtt'
        'webvtt'       = '.vtt'
        'vtt-words'    = '.words.vtt'
        'webvtt-words' = '.words.vtt'
        'words'        = '.words.vtt'
        'json'         = '.json'
        'md'           = '.md'
        'markdown'     = '.md'
    }

    $freshByFormat = @{}
    foreach ($format in $formatIds) {
        $ext = if ($extensionByFormat.ContainsKey($format)) { $extensionByFormat[$format] } else { ".$format" }

        # '<stem><ext>', or '<stem> (2)<ext>' from the rename policy, and nothing else.
        $pattern = '^' + [regex]::Escape($stem) + '( \(\d+\))?' + [regex]::Escape($ext) + '$'
        $fresh = @(Get-ChildItem -LiteralPath $directory -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match $pattern -and $_.LastWriteTime -ge $runStart } |
            Sort-Object LastWriteTime)

        if ($fresh.Count -eq 0) {
            Write-Host ("{0,-28} NOT WRITTEN BY THIS RUN" -f "$stem$ext") -ForegroundColor Yellow
            continue
        }

        $freshByFormat[$format] = $fresh[-1].FullName
        foreach ($file in $fresh) {
            Write-Host ("{0,-28} {1,12:N0} bytes" -f $file.Name, $file.Length)
        }
    }

    # The word-timed WebVTT is the first output whose bytes depend on the per-word timings rather
    # than only on the text, and its constraints are ones a player enforces silently: a timestamp
    # outside its cue, or not strictly after the one before it, is dropped by FFmpeg and honoured
    # by a browser. Same file, two renderings. Checked here at whatever scale the input happens to
    # be, because the unit tests run against a few dozen synthetic words.
    if ($freshByFormat.ContainsKey('vtt-words')) {
        Write-Host ''
        Write-Host '── word-timed WebVTT ───────────────────────────' -ForegroundColor Green

        $wordsVtt = Get-Content -LiteralPath $freshByFormat['vtt-words'] -Raw
        $cues = 0; $tags = 0; $untimed = 0; $tagged = 0; $possible = 0; $violations = @()

        foreach ($block in ($wordsVtt -replace "`r`n", "`n") -split "`n`n") {
            $lines = $block -split "`n"
            $arrow = $lines | Where-Object { $_ -match '-->' } | Select-Object -First 1
            if (-not $arrow) { continue }

            $cues++
            $bounds = $arrow -split ' --> '
            $cueStart = ConvertFrom-VttTimecode $bounds[0]
            $cueEnd = ConvertFrom-VttTimecode $bounds[1]
            $payload = ($lines | Select-Object -Skip ([Array]::IndexOf($lines, $arrow) + 1)) -join "`n"

            # A cue built from word timings wraps every word in <c>; a cue the engine reported no
            # word timestamps for has none at all. That is the discriminator, and counting tags
            # alone cannot make it: a cue holding a single word carries no timestamp either, and
            # reporting the two together says "the degradation path ran" when it did not.
            $inCueWords = ([regex]::Matches($payload, '<c>')).Count
            if ($inCueWords -eq 0) {
                $untimed++
            }
            else {
                $tagged += $inCueWords
                $possible += $inCueWords - 1
            }

            $previous = $cueStart

            # A tag body is a timestamp only if it opens with a digit, which is how FFmpeg tells
            # one from a <c> or a </c>.
            foreach ($match in [regex]::Matches($payload, '<(\d[\d:.]*)>')) {
                $at = ConvertFrom-VttTimecode $match.Groups[1].Value
                $tags++
                if ($at -le $cueStart -or $at -ge $cueEnd) {
                    $violations += "$at outside cue $cueStart..$cueEnd"
                }
                elseif ($at -le $previous) {
                    $violations += "$at does not follow $previous in cue $cueStart"
                }
                $previous = $at
            }

        }

        Write-Host ("cues                : {0:N0}, of which {1:N0} had no word timings to carry" -f $cues, $untimed)
        Write-Host ("words tagged        : {0:N0}" -f $tagged)
        Write-Host ("inline timestamps   : {0:N0} of {1:N0} possible, {2:N0} dropped" -f $tags, $possible, ($possible - $tags))

        # Dropped is not a failure: the first word of every cue takes no timestamp by design, and
        # Tidy can move a cue out from under word times it still carries, in which case the tag is
        # skipped rather than nudged into range. It is worth seeing, because a large number would
        # mean cues and words disagree far more often than this pipeline should allow.
        if ($possible - $tags -gt 0) {
            Write-Host ("                      (dropped tags are ones Tidy left outside their cue)")
        }

        if ($violations.Count -eq 0) {
            Write-Host 'ordering            : every timestamp strictly inside its cue and strictly increasing' -ForegroundColor Green
        }
        else {
            Write-Host ("ordering            : {0:N0} VIOLATIONS" -f $violations.Count) -ForegroundColor Red
            $violations | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        }

        # The alignment check. A word landing against the wrong line produces a file that plays,
        # reads correctly and highlights the wrong word, and this is the only thing that sees it.
        if ($freshByFormat.ContainsKey('vtt')) {
            $plain = Get-Content -LiteralPath $freshByFormat['vtt'] -Raw
            $stripped = [regex]::Replace($wordsVtt, '<[^>]*>', '')
            if ($stripped -ceq $plain) {
                Write-Host 'alignment           : tags stripped, byte-identical to the plain vtt' -ForegroundColor Green
            }
            else {
                Write-Host 'alignment           : STRIPPED OUTPUT DIFFERS FROM THE PLAIN VTT' -ForegroundColor Red
            }
        }
        else {
            Write-Host 'alignment           : not checked — add vtt to -Formats to compare against it'
        }
    }

    # The invariant checks. These are the same properties asserted in the unit tests, applied to
    # a real transcript at whatever scale this file happens to be, because timeline drift and
    # accumulation bugs only show up after a few hundred segments.
    if (-not $freshByFormat.ContainsKey('json')) {
        Write-Host ''
        Write-Host 'No JSON output from this run, so the invariant checks were skipped. Add json to -Formats.' -ForegroundColor Yellow
        return
    }

    $document = Get-Content -LiteralPath $freshByFormat['json'] -Raw | ConvertFrom-Json
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
