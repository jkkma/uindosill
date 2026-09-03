<#
.SYNOPSIS
    The tidy's request unit, measured: one call through the three units the stage can send — a
    segment, segments joined to fifteen seconds, sentence-runs — each in the pass shape and in
    tandem, scored for the lag between the plain transcript landing and the tidied one, the
    contract's refusals, and the WER delta under both reference styles.

.DESCRIPTION
    The unit one request carries is the lever on the tidy's pace — a request costs its own prefill
    plus a decode that mostly copies its input — and the decision of 2026-09-02 (docs/PHASES.md,
    *Decided 2026-09-02, late evening*) is that no unit is chosen until three have been measured on
    the same file, each with the pass shape beside its tandem. This is that measurement.

    Seven arms on one corpus call, through the real CLI: for each unit, `--tidy-shape pass` and
    `--tidy-shape tandem`, alternating; then the segment's tandem arm once more, because the rule
    that picks a winner compares lags and needs a run-to-run floor for them that one run cannot
    give. Every arm transcribes the call again with the recogniser — its transcripts are checked
    byte-identical across arms — and the tidy's own timings come from `--tidy-trace`, on the
    stage's clock: when the plain transcript was complete, when the last tidied line landed, and
    what every request carried and cost.

    The rule, as decided: a unit replaces the segment when its tandem lag is shorter than the
    segment's by more than the segment's own run-to-run lag spread; its delta against the
    non-verbatim reference is no worse than the segment's by more than that unit's pass-versus-
    tandem WER spread; and its refused segments are at most twice the segment's. If two qualify,
    the shorter lag. The summary applies the rule and says so; it does not decide anything.

    Writes runs/tidy-units/<timestamp>-<backend>/summary.{json,md}, with each arm's transcripts,
    tidied transcripts and trace under <unit>-<shape>/ — <unit>-tandem-2 for the segment's repeat —
    and a tidied/ subdirectory inside each holding the tidied transcript again under the plain
    stem, which is the shape `uindosill wer` scores. A -Fake run writes
    runs/tidy-units/<timestamp>-<backend>-fake/ instead, because sync-drive.ps1 carries every
    summary.{json,md} to the Drive and a dry run's is shaped exactly like a real one.

    Both files say what was asked for beside what the transcripts say actually loaded — the
    recogniser's backend and model, and the tidy's drop and GGUF, which is the pair every lag in
    here belongs to — how many requests were in flight, which arm ran first (gotcha 20), when the
    binary was written and from what revision. Numbers are formatted invariantly.

.EXAMPLE
    .\scripts\measure-tidy-units.ps1 -Backend vulkan

.EXAMPLE
    # One unit, both shapes, on a different call.
    .\scripts\measure-tidy-units.ps1 -Backend vulkan -Units sentence -File 4469088

.EXAMPLE
    .\scripts\lab.ps1 tidy-units -Backend cuda
#>

[CmdletBinding()]
param(
    [ValidateSet('cpu', 'vulkan', 'cuda')]
    [string] $Backend = 'vulkan',

    # Which llama-server drop runs the tidying model. Default: the recogniser's backend.
    [ValidateSet('cpu', 'vulkan', 'cuda')]
    [string] $TidyBackend,

    # The one corpus call the arms run on. 4482383 is the shortest, with spoken rows under every
    # condition already; 4469088 is the tiebreak named in the plan.
    [string] $File = '4482383',

    [string] $Model = 'tdt-0.6b-v3-f16',

    # The units to measure, in the order they run. The segment is always run: it is the baseline.
    [ValidateSet('segment', 'run', 'sentence')]
    [string[]] $Units = @('segment', 'run', 'sentence'),

    [ValidateSet('pass', 'tandem')]
    [string[]] $Shapes = @('pass', 'tandem'),

    # Which detector cuts the audio; unset leaves it to the CLI.
    [ValidateSet('energy', 'neural')]
    [string] $Vad,

    [string] $ManifestPath,
    [string] $CorpusRoot,
    [string] $OutputDirectory,
    [string] $Configuration = 'Release',
    [switch] $SkipBuild,
    [switch] $SkipVerify,

    # The canned engine and the canned tidier instead of the models: every arm, every file and the
    # rule run in a couple of minutes on any machine, and the summary means nothing. For checking
    # the harness, never for a figure. Neither -Backend nor -Model nor -TidyBackend reaches the CLI
    # then, and the canned engine answers cpu whatever was asked for — so the record says fake, sets
    # requested beside loaded, and its directory name ends -fake.
    #
    # lab.ps1 declares a fixed parameter set and this is not in it, so the dispatcher cannot pass it
    # on — a leading ! marks it in `lab.ps1`'s listing. Run this script directly to use it.
    [switch] $Fake
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The summaries are pasted into public documents, so numbers are formatted the same way on every
# machine: 0.25, not 0,25 on a machine whose Windows speaks a comma-decimal language (gotcha 42).
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
[Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::InvariantCulture

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo

try {
    if (-not $TidyBackend) { $TidyBackend = $Backend }
    if ('segment' -notin $Units) { $Units = @('segment') + $Units }
    $styles = @('verbatim', 'nonverbatim')

    # ── manifest and corpus ─────────────────────────────────────────────────────────────────────

    if (-not $ManifestPath) { $ManifestPath = Join-Path $PSScriptRoot 'wer-corpus.json' }
    if (-not (Test-Path -LiteralPath $ManifestPath)) { throw "Manifest not found: $ManifestPath" }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json

    if (-not $CorpusRoot) { $CorpusRoot = Join-Path $repo 'corpus' $manifest.name }
    foreach ($sub in @('media') + $styles) {
        New-Item -ItemType Directory -Force -Path (Join-Path $CorpusRoot $sub) | Out-Null
    }
    $CorpusRoot = (Resolve-Path -LiteralPath $CorpusRoot).Path

    $entry = @($manifest.files | Where-Object { [string] $_.id -eq $File })
    if ($entry.Count -ne 1) { throw "Not in the manifest: $File" }
    $entry = $entry[0]

    Write-Host ''
    Write-Host '── corpus ──────────────────────────────────────' -ForegroundColor Green
    Write-Host ("{0}: call {1} ({2}), pinned to {3} @ {4}" -f $manifest.name, $File, $entry.country,
        $manifest.source.repository, $manifest.source.commit.Substring(0, 12))

    function Get-Pinned {
        param([string] $Url, [string] $Destination, $Pin, [string] $What)

        if (Test-Path -LiteralPath $Destination) {
            if ($SkipVerify) { return }
        }
        else {
            Write-Host ("  fetching {0}" -f $What)
            $temporary = "$Destination.partial"
            Invoke-WebRequest -Uri $Url -OutFile $temporary -UseBasicParsing | Out-Null
            Move-Item -LiteralPath $temporary -Destination $Destination -Force
        }

        $item = Get-Item -LiteralPath $Destination
        $digest = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($item.Length -ne [long] $Pin.bytes -or $digest -ne $Pin.sha256) {
            $aside = "$Destination.bad"
            Move-Item -LiteralPath $Destination -Destination $aside -Force
            throw ("{0} does not match the manifest: {1:N0} bytes / {2} against pinned {3:N0} / {4}. Moved to {5}." -f
                $What, $item.Length, $digest, [long] $Pin.bytes, $Pin.sha256, $aside)
        }
    }

    $media = Join-Path $CorpusRoot 'media' "$File.mp3"
    Get-Pinned -Url ($manifest.source.media -replace '\{id\}', $File) -Destination $media -Pin $entry.media -What "media/$File.mp3"
    foreach ($style in $styles) {
        Get-Pinned -Url ($manifest.source.$style -replace '\{id\}', $File) -Destination (Join-Path $CorpusRoot $style "$File.nlp") -Pin $entry.$style -What "$style/$File.nlp"
    }
    $audioSeconds = [double] $entry.durationSeconds
    Write-Host ("  {0:N0} s of audio{1}" -f $audioSeconds, $(if ($SkipVerify) { '; digests NOT re-checked (-SkipVerify)' } else { '; media and references match the manifest' }))

    # ── build and machine ───────────────────────────────────────────────────────────────────────

    if (-not $SkipBuild) {
        Write-Host ''
        Write-Host 'Building...' -ForegroundColor Cyan
        dotnet build src/Parakeet.Cli -c $Configuration --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
    }

    $exe = Join-Path $repo "src/Parakeet.Cli/bin/$Configuration/net10.0/uindosill.exe"
    if (-not (Test-Path -LiteralPath $exe)) { $exe = Join-Path $repo "src/Parakeet.Cli/bin/$Configuration/net10.0/uindosill" }
    if (-not (Test-Path -LiteralPath $exe)) { throw "CLI not found at $exe" }

    # When the binary that ran was actually written. With -SkipBuild it can be any age, and the
    # revision below is only the tree's — the two together are what say whether this run measured
    # this revision or yesterday's binary wearing today's commit.
    $cliBuiltAt = (Get-Item -LiteralPath $exe).LastWriteTime.ToString('o')

    if (-not $OutputDirectory) {
        # The -fake suffix is not decoration. sync-drive.ps1 carries every summary.json and
        # summary.md to the Drive, a dry run's is shaped exactly like a real one, and the folder
        # listing is the first thing anybody reads: the name says which it is before the file is
        # opened. -OutputDirectory overrides this, so the two files say so as well.
        $OutputDirectory = Join-Path $repo 'runs' 'tidy-units' ("{0}-{1}{2}" -f
            (Get-Date -Format 'yyyyMMdd-HHmmss'), $Backend, $(if ($Fake) { '-fake' } else { '' }))
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

    # Named hardware, never a machine name: these summaries get pasted into a public document.
    $machine = [ordered]@{
        os     = [Environment]::OSVersion.VersionString
        cpu    = $null
        gpu    = @()
        driver = $null
    }
    try {
        $machine.cpu = (Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1).Name.Trim()
        $machine.gpu = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object { $_.Name })
        if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
            $machine.driver = (& nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>&1 | Select-Object -First 1).ToString().Trim()
        }
        if (-not $machine.driver) {
            $machine.driver = (Get-CimInstance Win32_VideoController -ErrorAction Stop | Select-Object -First 1).DriverVersion
        }
    }
    catch {
        # Not Windows, or CIM unavailable: the block stays partly empty rather than the run failing.
    }

    # Which build produced this. A record that cannot name its revision cannot be put back beside
    # the tree it came from, and a dirty tree means the CLI is not necessarily what HEAD says.
    $revision = [ordered]@{ commit = $null; dirty = $null }
    try {
        if (Get-Command git -ErrorAction SilentlyContinue) {
            $head = @(& git -C $repo rev-parse HEAD 2>$null) | Select-Object -First 1
            if ($head) {
                $revision.commit = ([string] $head).Trim()
                $revision.dirty = (@(& git -C $repo status --porcelain 2>$null).Count -gt 0)
            }
        }
    }
    catch {
        # No git on PATH, or not a checkout: the block stays empty rather than the run failing.
    }

    Write-Host ''
    Write-Host '── machine ─────────────────────────────────────' -ForegroundColor Green
    Write-Host ("  cpu     {0}" -f $machine.cpu)
    Write-Host ("  gpu     {0}" -f ($machine.gpu -join ' | '))
    if ($machine.driver) { Write-Host ("  driver  {0}" -f $machine.driver) }
    Write-Host ("  backend {0} (tidy on {1}) — requested{2}" -f $Backend, $TidyBackend,
        $(if ($Fake) { '; -Fake sends none of it to the CLI and the canned engine answers cpu' } else { ', not what loaded' }))
    if ($revision.commit) {
        Write-Host ("  build   {0}{1}" -f $revision.commit.Substring(0, 12), $(if ($revision.dirty) { ' (tree dirty)' } else { '' }))
    }
    Write-Host ("  output  {0}" -f $OutputDirectory)
    if ($Fake) {
        Write-Host '  DRY RUN (-Fake): the canned engine and the canned tidier. Nothing here is a measurement.' -ForegroundColor Yellow
    }

    # ── helpers ─────────────────────────────────────────────────────────────────────────────────

    function Get-Property($object, [string] $name) {
        if ($null -ne $object -and $object.PSObject.Properties.Name -contains $name) { return $object.$name }
        return $null
    }

    function Get-Words($segment) {
        if ($segment.PSObject.Properties.Name -contains 'words') { return @($segment.words) }
        return @()
    }

    function Get-Percentile([double[]] $values, [double] $p) {
        if ($values.Count -eq 0) { return $null }
        $sorted = @($values | Sort-Object)
        $rank = [Math]::Ceiling($p * $sorted.Count) - 1
        if ($rank -lt 0) { $rank = 0 }
        return [Math]::Round($sorted[$rank], 3)
    }

    function ConvertTo-Percent($rate) { if ($null -eq $rate) { $null } else { [Math]::Round(100.0 * [double] $rate, 2) } }

    # One transcript scored against one reference style, through the same command the WER harness uses.
    function Measure-Wer([string] $hypothesis, [string] $style) {
        $raw = & $exe wer --reference-dir (Join-Path $CorpusRoot $style) --json $hypothesis
        if ($LASTEXITCODE -ne 0) { throw "wer failed for $hypothesis against $style (exit $LASTEXITCODE): $($raw -join ' ')" }
        $scored = ($raw -join "`n") | ConvertFrom-Json
        $n = $scored.summed.normalised
        return [ordered]@{
            wer           = ConvertTo-Percent $n.rate
            substitutions = $n.substitutions
            deletions     = $n.deletions
            insertions    = $n.insertions
            referenceWords = $n.referenceWords
        }
    }

    # Words that differ between two transcripts, normalised, through compare-transcripts.ps1 —
    # the pass-versus-tandem spread of one unit, in words and in points of that unit's own text.
    function Measure-Spread([string] $reference, [string] $candidate) {
        $lines = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'compare-transcripts.ps1') -Reference $reference -Candidate $candidate 2>&1
        $line = @($lines | Where-Object { $_ -match 'word edits, normalised\s*:\s*(\d[\d,]*)\s+of\s+(\d[\d,]*)' }) | Select-Object -First 1
        if (-not $line) { throw "compare-transcripts.ps1 printed no normalised edit count for $candidate" }
        $null = $line -match 'word edits, normalised\s*:\s*(\d[\d,]*)\s+of\s+(\d[\d,]*)'
        $edits = [int] ($Matches[1] -replace ',', '')
        $of = [int] ($Matches[2] -replace ',', '')
        return [ordered]@{ edits = $edits; of = $of; percent = if ($of -gt 0) { [Math]::Round(100.0 * $edits / $of, 2) } else { $null } }
    }

    # ── the arms ────────────────────────────────────────────────────────────────────────────────

    $arms = [Collections.Generic.List[object]]::new()
    foreach ($unit in $Units) {
        foreach ($shape in $Shapes) {
            $arms.Add([pscustomobject]@{ Name = "$unit-$shape"; Unit = $unit; Shape = $shape })
        }
    }
    if ('tandem' -in $Shapes) {
        $arms.Add([pscustomobject]@{ Name = 'segment-tandem-2'; Unit = 'segment'; Shape = 'tandem' })
    }

    $results = [Collections.Generic.List[object]]::new()
    $spokenTexts = [ordered]@{}

    foreach ($arm in $arms) {
        $armDirectory = Join-Path $OutputDirectory $arm.Name
        New-Item -ItemType Directory -Force -Path $armDirectory | Out-Null
        $trace = Join-Path $armDirectory 'trace.json'

        Write-Host ''
        Write-Host ("── {0}: {1} unit, {2} shape ─────────────────────" -f $arm.Name, $arm.Unit, $arm.Shape) -ForegroundColor Green

        $arguments = @('transcribe') +
                     $(if ($Fake) { @('--fake') } else { @('--backend', $Backend, '--model', $Model, '--tidy-backend', $TidyBackend) }) +
                     @('-f', 'json,txt', '-o', $armDirectory, '--overwrite', '--quiet',
                       '--tidy', '--tidy-unit', $arm.Unit, '--tidy-shape', $arm.Shape, '--tidy-trace', $trace) +
                     $(if ($Vad) { @('--vad', $Vad) } else { @() }) + @($media)
        $watch = [Diagnostics.Stopwatch]::StartNew()
        # Not redirected: a process whose streams are captured has hung on abort here before (gotcha 19).
        & $exe @arguments
        $exit = $LASTEXITCODE
        $watch.Stop()

        $spokenPath = Join-Path $armDirectory "$File.json"
        $tidiedPath = Join-Path $armDirectory "$File.tidy.json"
        if ($exit -ne 0 -or -not (Test-Path -LiteralPath $spokenPath) -or -not (Test-Path -LiteralPath $tidiedPath) -or -not (Test-Path -LiteralPath $trace)) {
            throw ("Arm {0} did not produce a transcript, a tidied version and a trace (exit {1}). Nothing is summarised: " +
                   "an arm that did not run is not an arm that was slow.") -f $arm.Name, $exit
        }

        $spoken = Get-Content -LiteralPath $spokenPath -Raw | ConvertFrom-Json
        $tidied = Get-Content -LiteralPath $tidiedPath -Raw | ConvertFrom-Json
        $traced = Get-Content -LiteralPath $trace -Raw | ConvertFrom-Json
        $spokenTexts[$arm.Name] = Get-Content -LiteralPath (Join-Path $armDirectory "$File.txt") -Raw

        $requests = @($traced.requests)
        $latencies = [double[]] @($requests | ForEach-Object { [double] $_.landedSec - [double] $_.startedSec })
        $waits = [double[]] @($requests | ForEach-Object { [double] $_.startedSec - [double] $_.enqueuedSec })
        $firstEnqueued = if ($requests.Count -gt 0) { ($requests | ForEach-Object { [double] $_.enqueuedSec } | Measure-Object -Minimum).Minimum } else { $null }

        # Peak backlog: units queued and not yet landed, over the trace's events.
        $events = @($requests | ForEach-Object { [pscustomobject]@{ t = [double] $_.enqueuedSec; d = 1 } }) +
                  @($requests | ForEach-Object { [pscustomobject]@{ t = [double] $_.landedSec; d = -1 } })
        $backlog = 0; $peak = 0
        foreach ($event in ($events | Sort-Object t, d)) { $backlog += $event.d; if ($backlog -gt $peak) { $peak = $backlog } }

        # The two things every figure below is indexed by, taken from the trace the CLI wrote rather
        # than from the parameters this script sent. They should agree; a binary that predates
        # --tidy-unit would take the flag and tidy by segment regardless, and every arm would be
        # labelled with what was asked for while the summary compared three copies of one unit.
        # Cheaper to stop here than to explain that record afterwards, and -SkipBuild makes an old
        # binary reachable.
        $tracedUnit = [string] (Get-Property $traced 'unit')
        $tracedShape = [string] (Get-Property $traced 'shape')
        if ($tracedUnit -ne $arm.Unit -or $tracedShape -ne $arm.Shape) {
            throw ("Arm {0} asked for the {1} unit in the {2} shape and the trace says it ran the {3} unit in the {4} " +
                   "shape. The binary is not the one this harness is measuring — rebuild, or drop -SkipBuild.") -f
                   $arm.Name, $arm.Unit, $arm.Shape, $tracedUnit, $tracedShape
        }

        $tidySegments = @($tidied.segments)
        $row = [ordered]@{
            name                 = $arm.Name
            unit                 = $tracedUnit
            shape                = $tracedShape
            # The arms run in order in fresh processes and nothing is warmed up first, so the first
            # one can be paying the driver's shader compile inside processingSec on a GPU backend
            # (docs/GOTCHAS.md, 20). Marked rather than reordered: which arm ran first is a fact
            # about the record, and changing the order would change what a recorded run means.
            first                = ($results.Count -eq 0)
            # What the two transcripts say actually loaded, against what was asked for. The spoken
            # one names the recogniser's backend and model; the tidied one names the llama-server
            # drop that served the tidy and the GGUF it served — the pair TranscriptDocument calls
            # the tidy's provenance, and the pair every lag and latency below belongs to. Under
            # -Fake both come back cpu and the canned ids, which is exactly the disagreement with a
            # requested vulkan that this exists to show.
            loadedBackend        = Get-Property $spoken 'backend'
            loadedModel          = Get-Property $spoken 'model'
            loadedTidyBackend    = Get-Property $tidied 'tidyBackend'
            loadedTidyModel      = Get-Property $tidied 'tidyModel'
            # How many requests were allowed in flight, from the trace. Gotcha 41: a different
            # batching arrangement is a different answer, not merely a different pace, so a lag
            # without it is a figure nobody can reproduce.
            concurrency          = [int] $traced.concurrency
            exitCode             = $exit
            commandWallSec       = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
            recogniserSec        = [double] $spoken.processingSec
            segments             = @($spoken.segments).Count
            requests             = [int] $traced.units
            transcriptCompleteSec = [double] $traced.transcriptCompleteSec
            tidyCompleteSec      = [double] $traced.tidyCompleteSec
            lagSec               = [Math]::Round([double] $traced.tidyCompleteSec - [double] $traced.transcriptCompleteSec, 1)
            tidyWallSec          = if ($null -ne $firstEnqueued) { [Math]::Round([double] $traced.tidyCompleteSec - $firstEnqueued, 1) } else { $null }
            requestLatencySec    = [ordered]@{ median = Get-Percentile $latencies 0.5; p90 = Get-Percentile $latencies 0.9; max = Get-Percentile $latencies 1.0 }
            queueWaitSec         = [ordered]@{ median = Get-Percentile $waits 0.5; p90 = Get-Percentile $waits 0.9; max = Get-Percentile $waits 1.0 }
            peakBacklog          = $peak
            wordsPerSecond       = if ($null -ne $firstEnqueued -and ([double] $traced.tidyCompleteSec - $firstEnqueued) -gt 0) {
                                       [Math]::Round((($requests | ForEach-Object { [int] $_.words } | Measure-Object -Sum).Sum) / ([double] $traced.tidyCompleteSec - $firstEnqueued), 1) } else { $null }
            refusedUnits         = @($requests | Where-Object { -not $_.accepted }).Count
            refusedSegments      = [int] (Get-Property $tidied 'tidyRefusedSegments')
            emptiedSegments      = @($tidySegments | Where-Object { @(Get-Words $_).Count -eq 0 }).Count
            doorWords            = @($tidySegments | ForEach-Object { Get-Words $_ } | Where-Object { $_.PSObject.Properties.Name -contains 'replacedFrom' -and $null -ne $_.replacedFrom }).Count
            refusals             = @($requests | Where-Object { -not $_.accepted } | ForEach-Object { ([string] $_.refusal) -replace "'[^']*'", "'…'" } | Group-Object | ForEach-Object { [ordered]@{ reason = $_.Name; count = $_.Count } })
            spoken               = [ordered]@{}
            tidied               = [ordered]@{}
            delta                = [ordered]@{}
        }

        # Scored the way measure-wer.ps1 -Tidy scores: the tidied transcript under its plain stem.
        $scoring = Join-Path $armDirectory 'tidied'
        New-Item -ItemType Directory -Force -Path $scoring | Out-Null
        Copy-Item -LiteralPath $tidiedPath -Destination (Join-Path $scoring "$File.json") -Force
        foreach ($style in $styles) {
            $row.spoken[$style] = Measure-Wer $spokenPath $style
            $row.tidied[$style] = Measure-Wer (Join-Path $scoring "$File.json") $style
            $row.delta[$style] = [Math]::Round($row.tidied[$style].wer - $row.spoken[$style].wer, 2)
        }

        $results.Add([pscustomobject] $row)

        Write-Host ("  loaded {0} / {1}; tidy on {2} / {3}; {4} in flight{5}" -f
            ($row.loadedBackend ?? '—'), ($row.loadedModel ?? '—'),
            ($row.loadedTidyBackend ?? '—'), ($row.loadedTidyModel ?? '—'), $row.concurrency,
            $(if ($row.first) { '   <- FIRST arm of this run; on a GPU backend its recogniser time can be a cold one (gotcha 20)' } else { '' }))
        Write-Host ("  {0} requests for {1} segments; transcript at {2:F1} s, tidied at {3:F1} s, lag {4:F1} s; recogniser {5:F1} s" -f
            $row.requests, $row.segments, $row.transcriptCompleteSec, $row.tidyCompleteSec, $row.lagSec, $row.recogniserSec)
        Write-Host ("  refused {0} units / {1} segments, emptied {2}, door {3}; tidied WER {4:F2}% / {5:F2}% (delta {6:+0.00;-0.00} / {7:+0.00;-0.00})" -f
            $row.refusedUnits, $row.refusedSegments, $row.emptiedSegments, $row.doorWords,
            $row.tidied.verbatim.wer, $row.tidied.nonverbatim.wer, $row.delta.verbatim, $row.delta.nonverbatim)
    }

    # ── the recogniser across arms, and each unit's pass-versus-tandem spread ───────────────────

    $firstArm = $results[0].name
    $spokenIdentical = [ordered]@{}
    foreach ($name in $spokenTexts.Keys) { $spokenIdentical[$name] = ($spokenTexts[$name] -ceq $spokenTexts[$firstArm]) }

    $spreads = [ordered]@{}
    foreach ($unit in $Units) {
        $pass = @($results | Where-Object { $_.unit -eq $unit -and $_.shape -eq 'pass' }) | Select-Object -First 1
        $tandem = @($results | Where-Object { $_.name -eq "$unit-tandem" }) | Select-Object -First 1
        if ($pass -and $tandem) {
            $spreads[$unit] = [ordered]@{
                words = Measure-Spread (Join-Path $OutputDirectory $pass.name 'tidied' "$File.json") (Join-Path $OutputDirectory $tandem.name 'tidied' "$File.json")
                werNonverbatimPoints = [Math]::Round([Math]::Abs($pass.tidied.nonverbatim.wer - $tandem.tidied.nonverbatim.wer), 2)
                werVerbatimPoints = [Math]::Round([Math]::Abs($pass.tidied.verbatim.wer - $tandem.tidied.verbatim.wer), 2)
            }
        }
    }

    # ── the rule ────────────────────────────────────────────────────────────────────────────────

    $verdict = [ordered]@{ applied = $false; judged = 0; lagFloorSec = $null; qualifying = @(); winner = $null; notes = @() }
    $a1 = @($results | Where-Object { $_.name -eq 'segment-tandem' }) | Select-Object -First 1
    $a2 = @($results | Where-Object { $_.name -eq 'segment-tandem-2' }) | Select-Object -First 1
    if ($a1 -and $a2) {
        $verdict.lagFloorSec = [Math]::Round([Math]::Abs($a1.lagSec - $a2.lagSec), 1)
        # Whether either of the two is the first arm of the run belongs in the sentence, not only in
        # arms[].first: this note is the one that gets quoted, and a floor taken across a cold arm
        # and a warm one is a cold-versus-warm difference wearing the name of run-to-run noise.
        # With -Shapes tandem, segment-tandem IS arm one.
        $verdict.notes += ("The segment's tandem lag ran twice: {0:F1} s and {1:F1} s, a floor of {2:F1} s.{3}" -f
            $a1.lagSec, $a2.lagSec, $verdict.lagFloorSec,
            $(if ($a1.first -or $a2.first) {
                " WARNING: one of the two was the first arm of this run, so on a GPU backend this floor may be a cold-versus-warm difference rather than run-to-run noise (docs/GOTCHAS.md, 20)."
            }
            else { ' Neither was the first arm of the run.' }))
        $qualifying = [Collections.Generic.List[object]]::new()
        # The floor alone is not the rule. A challenger is judged only when it has both a tandem arm
        # and a pass-versus-tandem spread — the quality clause's own noise floor — so with -Shapes
        # tandem there is no spread for anything and nothing is judged at all. Counted rather than
        # assumed, because a skipped challenger used to leave the record saying "Qualifying: none.
        # By the rule, the unit is segment", which is a verdict nothing had earned.
        $judged = 0
        foreach ($unit in ($Units | Where-Object { $_ -ne 'segment' })) {
            $arm = @($results | Where-Object { $_.name -eq "$unit-tandem" }) | Select-Object -First 1
            if (-not $arm) {
                $verdict.notes += ("{0}: not judged — no tandem arm ran, so there is no lag to compare." -f $unit)
                continue
            }

            if (-not $spreads.Contains($unit)) {
                $verdict.notes += ("{0}: not judged — no pass arm ran beside the tandem one, so the quality clause has no spread to allow." -f $unit)
                continue
            }

            $judged++
            $spread = $spreads[$unit].werNonverbatimPoints
            $lagOk = $arm.lagSec -lt ($a1.lagSec - $verdict.lagFloorSec)
            $qualityOk = $arm.delta.nonverbatim -le ($a1.delta.nonverbatim + $spread)
            $refusalOk = $arm.refusedSegments -le (2 * $a1.refusedSegments)
            $verdict.notes += ("{0}: lag {1:F1} s against the segment's {2:F1} s ({3}); non-verbatim delta {4:+0.00;-0.00} against {5:+0.00;-0.00} with a spread of {6:F2} ({7}); refused {8} against {9} ({10})." -f
                $unit, $arm.lagSec, $a1.lagSec, $(if ($lagOk) { 'shorter by more than the floor' } else { 'not shorter by more than the floor' }),
                $arm.delta.nonverbatim, $a1.delta.nonverbatim, $spread, $(if ($qualityOk) { 'holds' } else { 'does not hold' }),
                $arm.refusedSegments, $a1.refusedSegments, $(if ($refusalOk) { 'within twice' } else { 'more than twice' }))
            if ($lagOk -and $qualityOk -and $refusalOk) { $qualifying.Add([pscustomobject]@{ unit = $unit; lagSec = $arm.lagSec }) }
        }

        if ($judged -gt 0) {
            $verdict.applied = $true
            $verdict.judged = $judged
            $verdict.qualifying = @($qualifying | ForEach-Object { $_.unit })
            $verdict.winner = if ($qualifying.Count -gt 0) { ($qualifying | Sort-Object lagSec | Select-Object -First 1).unit } else { 'segment' }
        }
        else {
            # The lag floor is real and stays in the record; the verdict is not, and qualifying and
            # winner stay empty rather than reading as a rule that ran and found nothing.
            $verdict.notes += ('The rule was NOT applied: no challenger was judged. It needs a unit other than the ' +
                'segment with both a tandem arm and a pass arm to take its spread from. This run picks nothing.')
        }
    }
    else {
        $verdict.notes += 'The rule was NOT applied: it needs the segment unit in tandem twice. This run picks nothing.'
    }

    # ── summary ─────────────────────────────────────────────────────────────────────────────────

    # What the transcripts say loaded, across the arms — one value when they agree, which they
    # should, and every value it took when they do not.
    function Join-Loaded([string] $field) {
        $values = @($results | ForEach-Object { $_.$field } | Where-Object { $_ } | Select-Object -Unique)
        if ($values.Count -gt 0) { return $values -join ', ' }
        return $null
    }

    $loadedBackend = Join-Loaded 'loadedBackend'
    $loadedModel = Join-Loaded 'loadedModel'
    $loadedTidyBackend = Join-Loaded 'loadedTidyBackend'
    $loadedTidyModel = Join-Loaded 'loadedTidyModel'
    $concurrencies = @($results | ForEach-Object { $_.concurrency } | Select-Object -Unique)

    $summary = [ordered]@{
        measuredAt   = (Get-Date).ToString('o')
        # First, and a boolean rather than a note, because everything under it is shaped like a
        # measurement whether or not one happened.
        fake         = [bool] $Fake
        backend      = [ordered]@{ requested = $Backend; loaded = $loadedBackend }
        model        = [ordered]@{ requested = $Model; loaded = $loadedModel }
        tidyBackend  = [ordered]@{ requested = $TidyBackend; loaded = $loadedTidyBackend }
        # The model that produced every rewrite, and so the model this whole record is about. This
        # harness names none, so the CLI's catalogue choice is the only thing that says which it
        # was: requested is null on purpose rather than echoing something nobody asked for.
        tidyModel    = [ordered]@{ requested = $null; loaded = $loadedTidyModel }
        concurrency  = $concurrencies
        firstArm     = $firstArm
        skipBuild    = [bool] $SkipBuild
        skipVerify   = [bool] $SkipVerify
        revision     = $revision
        cliBuiltAt   = $cliBuiltAt
        machine      = $machine
        corpus       = [ordered]@{ name = $manifest.name; file = $File; country = $entry.country; audioSeconds = $audioSeconds }
        units        = $Units
        shapes       = $Shapes
        spokenIdenticalAcrossArms = $spokenIdentical
        spreads      = $spreads
        verdict      = $verdict
        arms         = @($results)
    }
    $summaryJson = Join-Path $OutputDirectory 'summary.json'
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryJson -Encoding utf8

    $switchesUsed = [Collections.Generic.List[string]]::new()
    if ($Fake) { $switchesUsed.Add('-Fake') }
    if ($SkipBuild) { $switchesUsed.Add('-SkipBuild') }
    if ($SkipVerify) { $switchesUsed.Add('-SkipVerify') }
    $switchText = if ($switchesUsed.Count -gt 0) { $switchesUsed -join ', ' } else { 'none' }

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("# The tidy's request unit on $File, $Backend backend, $($summary.measuredAt)")
    $lines.Add('')
    if ($Fake) {
        $lines.Add('> **DRY RUN — `-Fake`.** The canned engine and the canned tidier ran, not the models. Every')
        $lines.Add('> figure below is the harness exercising itself and none of it is a measurement. Do not quote it,')
        $lines.Add('> and do not put it in `docs/UNPROVEN.md`.')
        $lines.Add('')
    }
    $lines.Add(("Machine: {0}; {1}; driver {2}. Call {3} ({4}), {5:N0} s of audio." -f
        $machine.cpu, ($machine.gpu -join ' | '), $machine.driver, $File, $entry.country, $audioSeconds))
    $lines.Add('')
    $lines.Add((("Asked for: recogniser backend **{0}** and model **{1}**, tidy on **{2}**. The transcripts say the " +
        "recogniser loaded **{3}** / **{4}** and the tidy ran on **{5}** with **{6}** — the drop and the GGUF every " +
        "lag, latency and refusal below belongs to. This harness passes no ``--tidy-model-path``, so the tidying model " +
        "is whatever single entry the catalogue holds. Requests in flight: **{7}**.") -f
        $Backend, $Model, $TidyBackend, ($loadedBackend ?? '—'), ($loadedModel ?? '—'),
        ($loadedTidyBackend ?? '—'), ($loadedTidyModel ?? '—'),
        $(if ($concurrencies.Count -gt 0) { $concurrencies -join ', ' } else { '—' })))
    $lines.Add('')
    $lines.Add(("{0} {1}{2}, from a revision this run reads as {3}. Switches: {4}." -f
        $(if ($SkipBuild) { 'Ran the binary already in `bin/` — **this run built nothing** — last written' } else { 'Built' }),
        $cliBuiltAt,
        $(if ($SkipBuild) { '' } else { ' by this run' }),
        $(if ($revision.commit) {
            "``$($revision.commit)``" + $(if ($revision.dirty) { ' **with the working tree dirty**' } else { ', tree clean' })
        }
        else { 'unreadable' }), $switchText))
    $lines.Add('')
    $lines.Add('| arm | requests | recogniser s | transcript at | tidied at | **lag s** | tidy wall s | latency med / p90 / max s | wait med s | peak backlog | refused units / segments | emptied | door | tidied WER v / nv | delta v / nv |')
    $lines.Add('|---|---:|---:|---:|---:|---:|---:|---|---:|---:|---|---:|---:|---|---|')
    foreach ($row in $results) {
        $lines.Add(("| {0} | {1} | {2:F1} | {3:F1} | {4:F1} | **{5:F1}** | {6:F1} | {7:F2} / {8:F2} / {9:F2} | {10:F2} | {11} | {12} / {13} | {14} | {15} | {16:F2}% / {17:F2}% | {18:+0.00;-0.00} / {19:+0.00;-0.00} |" -f
            $(if ($row.first) { "$($row.name) *(first)*" } else { $row.name }),
            $row.requests, $row.recogniserSec, $row.transcriptCompleteSec, $row.tidyCompleteSec, $row.lagSec, $row.tidyWallSec,
            $row.requestLatencySec.median, $row.requestLatencySec.p90, $row.requestLatencySec.max, $row.queueWaitSec.median, $row.peakBacklog,
            $row.refusedUnits, $row.refusedSegments, $row.emptiedSegments, $row.doorWords,
            $row.tidied.verbatim.wer, $row.tidied.nonverbatim.wer, $row.delta.verbatim, $row.delta.nonverbatim))
    }
    $lines.Add('')
    $lines.Add((("The arms ran in the order listed, each a fresh process, with nothing warmed up beforehand: **{0}** is " +
        "the first run of this session. On a GPU backend a first run can pay the driver's shader compile inside " +
        "``processingSec`` (``docs/GOTCHAS.md``, 20), which lands in that arm's recogniser time and, in the tandem shape, " +
        "in its lag. Whether it did here is not recorded — run the arm again to find out.") -f $firstArm))
    $lines.Add('')
    # "every arm" only when every arm's transcript really was the same text. Where they diverged the
    # figure is the first arm's alone and the sentence says so; the rest are in arms[].spoken.
    $identicalCount = @($spokenIdentical.Values | Where-Object { $_ }).Count
    $lines.Add(("Spoken WER, {0}: {1:F2}% / {2:F2}% (verbatim / non-verbatim); the recogniser's transcript byte-identical to the first arm's in {3} of {4} arms{5}" -f
        $(if ($identicalCount -eq $spokenIdentical.Count) { 'every arm' } else { ("the first arm ({0})" -f $firstArm) }),
        $results[0].spoken.verbatim.wer, $results[0].spoken.nonverbatim.wer, $identicalCount, $spokenIdentical.Count,
        $(if ($identicalCount -eq $spokenIdentical.Count) { '.' } else { ' — so the arms that differ have their own rates, in `summary.json` under `arms[].spoken`.' })))
    $lines.Add('')
    $lines.Add('Pass-versus-tandem spread of each unit, in the tidied text:')
    $lines.Add('')
    $lines.Add('| unit | words differing | of | % | non-verbatim WER points | verbatim WER points |')
    $lines.Add('|---|---:|---:|---:|---:|---:|')
    foreach ($unit in $spreads.Keys) {
        $s = $spreads[$unit]
        $lines.Add(("| {0} | {1} | {2} | {3:F2} | {4:F2} | {5:F2} |" -f $unit, $s.words.edits, $s.words.of, $s.words.percent, $s.werNonverbatimPoints, $s.werVerbatimPoints))
    }
    $lines.Add('')
    $lines.Add('The rule (docs/PHASES.md, *Decided 2026-09-02, late evening*), applied:')
    $lines.Add('')
    foreach ($note in $verdict.notes) { $lines.Add("- $note") }
    $lines.Add('')
    if ($verdict.applied) {
        $lines.Add(("**{0} challenger{1} judged. Qualifying: {2}. By the rule, the unit is {3}.**" -f
            $verdict.judged, $(if ($verdict.judged -eq 1) { '' } else { 's' }),
            $(if (@($verdict.qualifying).Count -gt 0) { @($verdict.qualifying) -join ', ' } else { 'none' }), $verdict.winner))
    }
    else {
        $lines.Add('**The rule was not applied and this run picks no unit.** The notes above say what was missing.')
    }
    $lines.Add('')
    $lines.Add('Lag is the tidied version landing after the plain transcript, on the stage''s clock; the pass shape''s lag is its whole tidy. WER is')
    $lines.Add('over tokens normalised the same way on both sides (`uindosill wer --help`), not comparable to leaderboard figures. One call, one machine.')
    $summaryMd = Join-Path $OutputDirectory 'summary.md'
    $lines -join "`n" | Set-Content -LiteralPath $summaryMd -Encoding utf8

    Write-Host ''
    Write-Host ("written: {0}" -f $summaryJson) -ForegroundColor Cyan
    Write-Host ("         {0}" -f $summaryMd) -ForegroundColor Cyan
    foreach ($note in $verdict.notes) { Write-Host ("  {0}" -f $note) }
    if ($Fake) {
        Write-Host ''
        Write-Host '  DRY RUN (-Fake): nothing above is a measurement. Both files say so.' -ForegroundColor Yellow
    }
}
finally {
    Pop-Location
}
