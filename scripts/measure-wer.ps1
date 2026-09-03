<#
.SYNOPSIS
    Word error rate of each catalogue model against human transcripts, on one named backend, over
    the pinned Earnings-22 subset — the Phase 0 measurement that turns "divergence from f16" into
    an error rate.

.DESCRIPTION
    Every quantisation figure this project had before this script was divergence: how far q4_k's
    transcript is from f16's, which says nothing about whether either is right. This measures each
    model against what a human wrote down, so a quantisation can be judged rather than merely
    compared, and f16 itself gets a number for the first time.

    The corpus is scripts/wer-corpus.json: ten English earnings calls from five countries, about
    eleven hours, each with two human transcripts — verbatim (fillers, stutters and repetitions
    written down) and non-verbatim (lightly edited for readability). It is fetched here from the
    pinned upstream commit, byte counts and SHA-256 checked against the manifest before anything is
    scored, into corpus/ which is gitignored. Both reference styles are scored, because the model
    sits between them and the gap between the two numbers is real information about what "WER"
    means for this material.

    What runs, per model: one `uindosill transcribe` process over all ten files (the model loads
    once), then `uindosill wer --reference-dir` twice, once per style. Per model and per file the
    script keeps the counts, the rate, the audio duration and the decode time the transcript JSON
    reports, and writes runs/wer/<timestamp>-<backend>/summary.{json,md} beside the transcripts.

    With -Tidy that same transcribe also runs the tidy stage beside the recogniser, so every plain
    transcript gets a tidied one beside it under the .tidy infix, and both are scored against both
    styles. The recogniser ran once for the pair: the two rows differ only in whether a language
    model rewrote the lines, so the difference between them is the tidy's and nothing else's.
    Refusals and the words admitted through the low-confidence door are counted from the tidied
    transcripts themselves and reported per file beside the two rates.

    Read `uindosill wer --help` for exactly what the normalisation does and does not do before
    quoting a figure from here: it is not the leaderboard normaliser, so a number from this script
    is comparable to another number from this script and not to a published one. Real-time factors
    are per backend and this script names its backend on every line that carries one.

.EXAMPLE
    # The ladder on CUDA — all five catalogue models, both reference styles.
    .\scripts\measure-wer.ps1 -Backend cuda

.EXAMPLE
    # The backend control: f16 alone on CPU, to put beside the CUDA f16 row.
    .\scripts\measure-wer.ps1 -Backend cpu -Models tdt-0.6b-v3-f16

.EXAMPLE
    # A quick check on two calls, keeping filler words as words.
    .\scripts\measure-wer.ps1 -Backend cuda -Models tdt-0.6b-v3-f16 -Files 4474506,4485192 -KeepFillers

.EXAMPLE
    # What the transcript tidy does to the error rate: both styles, one recogniser pass.
    .\scripts\measure-wer.ps1 -Backend vulkan -Models tdt-0.6b-v3-f16 -Tidy

.EXAMPLE
    .\scripts\lab.ps1 wer -Backend cuda
#>

[CmdletBinding()]
param(
    [ValidateSet('cpu', 'vulkan', 'cuda')]
    [string] $Backend = 'cuda',

    # Which detector cuts the audio (energy | neural). Unset leaves it to the CLI, which since
    # 2026-08-23 is neural whenever its model is installed. Every WER recorded before that day is the
    # gate's — pass -Vad energy to reproduce one. Named per file in the summary as speechDetector.
    [ValidateSet('energy', 'neural')]
    [string] $Vad,

    # Catalogue ids, in the order they run. The default is the whole ladder, f16 first, so the
    # reference model's own row is there before anything is judged against it.
    [string[]] $Models = @('tdt-0.6b-v3-f16', 'tdt-0.6b-v3-q8_0', 'tdt-0.6b-v3-q6_k', 'tdt-0.6b-v3-q5_k', 'tdt-0.6b-v3-q4_k'),

    # Corpus file ids to run, for a quick look; the default is every file in the manifest.
    [string[]] $Files,

    # Which reference styles to score against.
    [ValidateSet('verbatim', 'nonverbatim')]
    [string[]] $Styles = @('verbatim', 'nonverbatim'),

    # Tidy each line beside the recogniser and score the tidied transcript against the same
    # references, in the same run and off the same recogniser output. Needs the tidying model
    # installed ('uindosill models list') and a llama-server drop vendored.
    [switch] $Tidy,

    # Which llama-server drop runs the tidying model: cpu, vulkan or cuda. Unset leaves it to the
    # CLI, which takes the best vendored. On the second machine the cpu arrangement starved the
    # recogniser and doubled its time, so this is not a choice to leave to chance on a long run.
    [ValidateSet('cpu', 'vulkan', 'cuda')]
    [string] $TidyBackend,

    # Score uh, um, hmm, mm, mhm and mmm as words on both sides instead of dropping them.
    [switch] $KeepFillers,

    [string] $ManifestPath,

    # Where the corpus lives. Default corpus/<manifest name>/ under the repository, gitignored.
    [string] $CorpusRoot,

    # Where the transcripts and summaries land. Default runs/wer/<timestamp>-<backend>/.
    [string] $OutputDirectory,

    [string] $Configuration = 'Release',

    [switch] $SkipBuild,

    # Trust what is in the corpus directory without re-hashing it. The check is ~200 MB of SHA-256
    # and takes a few seconds; skip it only on a machine that has just passed it.
    [switch] $SkipVerify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The format operator follows the machine's culture, and these summaries are read on other
# machines: on the second machine, which runs es-PY, `{0:F2}` wrote 10,21% and `{0:N0}` wrote
# 5.599 into summary.md on 2026-09-02 (docs/GOTCHAS.md, 42). Invariant from here on, so a summary
# reads the same wherever it was made; ConvertTo-Json was invariant already.
[System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo

try {
    # ── manifest and corpus ─────────────────────────────────────────────────────────────────────

    if (-not $ManifestPath) { $ManifestPath = Join-Path $PSScriptRoot 'wer-corpus.json' }
    if (-not (Test-Path -LiteralPath $ManifestPath)) { throw "Manifest not found: $ManifestPath" }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json

    if (-not $CorpusRoot) { $CorpusRoot = Join-Path $repo 'corpus' $manifest.name }
    foreach ($sub in @('media') + $Styles) {
        New-Item -ItemType Directory -Force -Path (Join-Path $CorpusRoot $sub) | Out-Null
    }
    $CorpusRoot = (Resolve-Path -LiteralPath $CorpusRoot).Path

    $entries = @($manifest.files)
    if ($Files) {
        $wanted = @(($Files -join ',') -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $entries = @($entries | Where-Object { $_.id -in $wanted })
        $missing = @($wanted | Where-Object { $_ -notin @($entries | ForEach-Object { $_.id }) })
        if ($missing.Count -gt 0) { throw "Not in the manifest: $($missing -join ', ')" }
    }
    if ($entries.Count -eq 0) { throw 'No corpus files selected.' }

    Write-Host ''
    Write-Host '── corpus ──────────────────────────────────────' -ForegroundColor Green
    Write-Host ("{0}: {1} of {2} files, pinned to {3} @ {4}" -f $manifest.name, $entries.Count, @($manifest.files).Count,
        $manifest.source.repository, $manifest.source.commit.Substring(0, 12))

    # Fetch what is missing and verify everything against the manifest. A file that fails the check
    # is moved aside rather than deleted, so a bad download can be looked at, and the run stops:
    # scoring against a reference that is not the one the manifest describes would be a
    # measurement of nothing.
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

    $verifyWatch = [Diagnostics.Stopwatch]::StartNew()
    foreach ($entry in $entries) {
        $id = [string] $entry.id
        Get-Pinned -Url ($manifest.source.media -replace '\{id\}', $id) -Destination (Join-Path $CorpusRoot 'media' "$id.mp3") -Pin $entry.media -What "media/$id.mp3"
        foreach ($style in $Styles) {
            Get-Pinned -Url ($manifest.source.$style -replace '\{id\}', $id) -Destination (Join-Path $CorpusRoot $style "$id.nlp") -Pin $entry.$style -What "$style/$id.nlp"
        }
    }
    $verifyWatch.Stop()
    if ($SkipVerify) {
        Write-Host '  corpus present; digests NOT re-checked (-SkipVerify)' -ForegroundColor Yellow
    }
    else {
        Write-Host ("  every file matches its pinned byte count and SHA-256 ({0:N1} s)" -f $verifyWatch.Elapsed.TotalSeconds)
    }

    $totalAudioSeconds = ($entries | ForEach-Object { [double] $_.durationSeconds } | Measure-Object -Sum).Sum
    Write-Host ("  {0:N0} s of audio = {1:N2} h" -f $totalAudioSeconds, ($totalAudioSeconds / 3600))

    # ── build and machine ───────────────────────────────────────────────────────────────────────

    if (-not $SkipBuild) {
        Write-Host ''
        Write-Host 'Building...' -ForegroundColor Cyan
        dotnet build src/Parakeet.Cli -c $Configuration --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }

    $exe = Join-Path $repo "src/Parakeet.Cli/bin/$Configuration/net10.0/uindosill.exe"
    if (-not (Test-Path -LiteralPath $exe)) { $exe = Join-Path $repo "src/Parakeet.Cli/bin/$Configuration/net10.0/uindosill" }
    if (-not (Test-Path -LiteralPath $exe)) { throw "Built executable not found. Run without -SkipBuild, or check bin/$Configuration/net10.0." }

    # When the binary that ran was actually written. With -SkipBuild it can be any age, and the
    # revision below is only the tree's — the two together are what say whether this run measured
    # this revision or yesterday's binary wearing today's commit.
    $cliBuiltAt = (Get-Item -LiteralPath $exe).LastWriteTime.ToString('o')

    if (-not $OutputDirectory) {
        $OutputDirectory = Join-Path $repo 'runs' 'wer' ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Backend)
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

    # Named hardware, never a machine name: these summaries get pasted into a public document.
    $machine = [ordered]@{
        os        = [Environment]::OSVersion.VersionString
        cpu       = $null
        gpu       = @()
        driver    = $null
    }
    try {
        $machine.cpu = (Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1).Name.Trim()
        $machine.gpu = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object { $_.Name })
        if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
            $machine.driver = (& nvidia-smi --query-gpu=driver_version --format=csv,noheader 2>&1 | Select-Object -First 1).ToString().Trim()
        }
        if (-not $machine.driver) {
            # No NVIDIA tools — the second machine's Radeon, or any card without them — so the
            # adapter's own driver version, which is the figure docs/UNPROVEN.md names for it.
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
    Write-Host ("  os      {0}" -f $machine.os)
    Write-Host ("  cpu     {0}" -f $machine.cpu)
    Write-Host ("  gpu     {0}" -f ($machine.gpu -join ' | '))
    if ($machine.driver) { Write-Host ("  driver  {0}" -f $machine.driver) }
    Write-Host ("  backend {0} — requested, not what loaded" -f $Backend)
    if ($revision.commit) {
        Write-Host ("  build   {0}{1}" -f $revision.commit.Substring(0, 12), $(if ($revision.dirty) { ' (tree dirty)' } else { '' }))
    }
    Write-Host ("  output  {0}" -f $OutputDirectory)

    # ── transcribe and score, per model ─────────────────────────────────────────────────────────

    $mediaPaths = @($entries | ForEach-Object { Join-Path $CorpusRoot 'media' "$($_.id).mp3" })
    $modelRows = [Collections.Generic.List[object]]::new()

    # A rate is null in the JSON when the reference was empty; it never is here, but a null
    # multiplied would stop the run over a bookkeeping detail rather than a measurement.
    function ConvertTo-Percent($rate) { if ($null -eq $rate) { $null } else { [Math]::Round(100.0 * [double] $rate, 2) } }

    # One set of transcripts against every reference style, kept per file and summed by the tool
    # that publishes the figures. Called once for the spoken transcripts and, with -Tidy, again
    # for the tidied ones; the second call writes its per-file rates under a prefixed key, so a
    # file's two rates sit beside each other and the delta is subtraction, not a second run.
    # -Into is filled rather than returned: an ordered dictionary is a reference, and a PowerShell
    # function's return value is one pipeline surprise away from being something else.
    function Measure-Styles {
        param(
            [string[]] $Hypotheses,
            $PerFile,
            $Into,
            [string] $KeyPrefix = '',
            [string] $What
        )

        foreach ($style in $Styles) {
            $werArguments = @('wer', '--reference-dir', (Join-Path $CorpusRoot $style), '--json') + $(if ($KeepFillers) { @('--keep-fillers') } else { @() }) + $Hypotheses
            $raw = & $exe @werArguments
            if ($LASTEXITCODE -ne 0) { throw "wer failed for $What against $style (exit $LASTEXITCODE): $($raw -join ' ')" }
            $scored = ($raw -join "`n") | ConvertFrom-Json

            foreach ($h in @($scored.hypotheses)) {
                $id = [IO.Path]::GetFileNameWithoutExtension([string] $h.path)
                $PerFile[$id]["${KeyPrefix}wer_$style"] = ConvertTo-Percent $h.normalised.rate
                $PerFile[$id]["${KeyPrefix}errors_$style"] = [ordered]@{
                    referenceWords = $h.normalised.referenceWords
                    substitutions  = $h.normalised.substitutions
                    deletions      = $h.normalised.deletions
                    insertions     = $h.normalised.insertions
                    rawRate        = ConvertTo-Percent $h.raw.rate
                }
            }

            $Into[$style] = [ordered]@{
                referenceWords  = $scored.summed.normalised.referenceWords
                hypothesisWords = $scored.summed.normalised.hypothesisWords
                substitutions   = $scored.summed.normalised.substitutions
                deletions       = $scored.summed.normalised.deletions
                insertions      = $scored.summed.normalised.insertions
                errors          = $scored.summed.normalised.errors
                wer             = ConvertTo-Percent $scored.summed.normalised.rate
                rawWer          = ConvertTo-Percent $scored.summed.raw.rate
                normaliser      = $scored.normaliser
            }
        }
    }

    foreach ($model in $Models) {
        $modelDirectory = Join-Path $OutputDirectory $model
        New-Item -ItemType Directory -Force -Path $modelDirectory | Out-Null

        Write-Host ''
        Write-Host ("── {0} on {1} ─────────────────────────────" -f $model, $Backend) -ForegroundColor Green

        # --tidy writes the tidied version beside the plain one; the plain files are what they
        # always were, so the spoken row of this run is a spoken row whether or not -Tidy was given.
        $tidyArguments = if ($Tidy) { @('--tidy') + $(if ($TidyBackend) { @('--tidy-backend', $TidyBackend) } else { @() }) } else { @() }
        $arguments = @('transcribe', '--backend', $Backend, '--model', $model, '-f', 'json,txt', '-o', $modelDirectory, '--overwrite', '--quiet') + $(if ($Vad) { @('--vad', $Vad) } else { @() }) + $tidyArguments + $mediaPaths
        $watch = [Diagnostics.Stopwatch]::StartNew()
        # Not redirected: a CUDA process whose streams are captured has hung on abort here before
        # (gotcha 19). Its own progress is suppressed with --quiet; the summary lines still print.
        & $exe @arguments
        $exitCode = $LASTEXITCODE
        $watch.Stop()
        Write-Host ("  transcribe exit {0}, {1:hh\:mm\:ss} wall for {2:N2} h of audio" -f $exitCode, $watch.Elapsed, ($totalAudioSeconds / 3600))

        if ($exitCode -ne 0) {
            Write-Host '  THE TRANSCRIPTION FAILED for this model. Nothing is scored for it.' -ForegroundColor Red
            $modelRows.Add([PSCustomObject]@{ model = $model; backend = $Backend; exitCode = $exitCode; failed = $true })
            continue
        }

        # Per file: what the transcript JSON says about itself. RTF is per backend, and it is
        # recorded here beside the backend's name for that reason.
        $perFile = [ordered]@{}
        foreach ($entry in $entries) {
            $id = [string] $entry.id
            $jsonPath = Join-Path $modelDirectory "$id.json"
            if (-not (Test-Path -LiteralPath $jsonPath)) {
                Write-Host ("  {0}: no transcript written" -f $id) -ForegroundColor Red
                continue
            }
            $document = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
            $perFile[$id] = [ordered]@{
                id               = $id
                country          = $entry.country
                audioSeconds     = if ($document.PSObject.Properties.Name -contains 'audioDurationSec') { [double] $document.audioDurationSec } else { $null }
                processingSeconds = if ($document.PSObject.Properties.Name -contains 'processingSec') { [double] $document.processingSec } else { $null }
                realTimeFactor   = if ($document.PSObject.Properties.Name -contains 'realTimeFactor') { [double] $document.realTimeFactor } else { $null }
                # What cut the file, from the transcript itself; null on one written before the
                # field existed, which means the gate (every WER before 2026-08-23 was the gate's).
                speechDetector   = if ($document.PSObject.Properties.Name -contains 'speechDetector') { [string] $document.speechDetector } else { $null }
                segments         = @($document.segments).Count
                words            = (@($document.segments) | ForEach-Object { if ($_.PSObject.Properties.Name -contains 'words') { @($_.words).Count } else { 0 } } | Measure-Object -Sum).Sum
            }
        }

        $hypotheses = @($perFile.Keys | ForEach-Object { Join-Path $modelDirectory "$_.json" })
        $styleResults = [ordered]@{}
        Measure-Styles -Hypotheses $hypotheses -PerFile $perFile -Into $styleResults -What $model

        # The tidied transcripts, against the same references through the same command. `wer
        # --reference-dir` finds a reference by exact file stem, and a tidied file's stem carries
        # the .tidy infix, so the files are copied under their plain stems into a directory of
        # their own rather than the matching being loosened in the tool for a harness's sake.
        $tidyStyleResults = $null
        $tidyPerFile = [ordered]@{}
        if ($Tidy) {
            $tidyScoringDirectory = Join-Path $modelDirectory 'tidied'
            New-Item -ItemType Directory -Force -Path $tidyScoringDirectory | Out-Null
            foreach ($id in @($perFile.Keys)) {
                $written = Join-Path $modelDirectory "$id.tidy.json"
                if (-not (Test-Path -LiteralPath $written)) {
                    throw "No tidied transcript for $id, though -Tidy was asked for. Nothing is scored: a missing " +
                          "tidied file is a pass that did not run, not a pass that changed nothing."
                }
                Copy-Item -LiteralPath $written -Destination (Join-Path $tidyScoringDirectory "$id.json") -Force

                # What the tidy came to on this file, from the tidied transcript itself: the
                # refusals it counts, and the words that came through the low-confidence door,
                # each of which carries the spoken word it replaced.
                $tidied = Get-Content -LiteralPath $written -Raw | ConvertFrom-Json

                # A segment the tidy emptied — a line that was nothing but fillers — has no words
                # left, and the writer omits an empty list rather than writing one, so the property
                # is absent on it. Counting through a helper makes that a zero rather than a
                # StrictMode failure after the transcription has already been paid for.
                $tidySegments = @($tidied.segments)
                function Get-Words($segment) {
                    if ($segment.PSObject.Properties.Name -contains 'words') { @($segment.words) } else { @() }
                }
                $tidyPerFile[$id] = [ordered]@{
                    id              = $id
                    country         = $perFile[$id].country
                    tidyModel       = if ($tidied.PSObject.Properties.Name -contains 'tidyModel') { [string] $tidied.tidyModel } else { $null }
                    tidyBackend     = if ($tidied.PSObject.Properties.Name -contains 'tidyBackend') { [string] $tidied.tidyBackend } else { $null }
                    segments        = $tidySegments.Count
                    refusedSegments = if ($tidied.PSObject.Properties.Name -contains 'tidyRefusedSegments') { [int] $tidied.tidyRefusedSegments } else { $null }
                    emptiedSegments = @($tidySegments | Where-Object { @(Get-Words $_).Count -eq 0 }).Count
                    words           = ($tidySegments | ForEach-Object { @(Get-Words $_).Count } | Measure-Object -Sum).Sum
                    replacedWords   = @($tidySegments | ForEach-Object { Get-Words $_ } |
                        Where-Object { $_.PSObject.Properties.Name -contains 'replacedFrom' -and $null -ne $_.replacedFrom }).Count
                }
                $perFile[$id]['tidy'] = $tidyPerFile[$id]
            }

            $tidyStyleResults = [ordered]@{}
            $tidyHypotheses = @($perFile.Keys | ForEach-Object { Join-Path $tidyScoringDirectory "$_.json" })
            Measure-Styles -Hypotheses $tidyHypotheses -PerFile $perFile -Into $tidyStyleResults -KeyPrefix 'tidy_' -What "$model tidied"
        }

        $processingTotal = ($perFile.Values | ForEach-Object { $_.processingSeconds } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
        $audioTotal = ($perFile.Values | ForEach-Object { $_.audioSeconds } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
        $row = [ordered]@{
            model             = $model
            backend           = $Backend
            exitCode          = $exitCode
            failed            = $false
            files             = $perFile.Count
            audioSeconds      = [Math]::Round($audioTotal, 1)
            processingSeconds = [Math]::Round($processingTotal, 1)
            realTimeFactor    = if ($audioTotal -gt 0) { [Math]::Round($processingTotal / $audioTotal, 4) } else { $null }
            wallSeconds       = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
            styles            = $styleResults
            tidy              = if ($Tidy) {
                [ordered]@{
                    styles          = $tidyStyleResults
                    backend         = $TidyBackend
                    model           = @($tidyPerFile.Values | ForEach-Object { $_.tidyModel } | Where-Object { $_ } | Select-Object -Unique)
                    segments        = ($tidyPerFile.Values | ForEach-Object { $_.segments } | Measure-Object -Sum).Sum
                    refusedSegments = ($tidyPerFile.Values | ForEach-Object { $_.refusedSegments } | Where-Object { $null -ne $_ } | Measure-Object -Sum).Sum
                    emptiedSegments = ($tidyPerFile.Values | ForEach-Object { $_.emptiedSegments } | Measure-Object -Sum).Sum
                    replacedWords   = ($tidyPerFile.Values | ForEach-Object { $_.replacedWords } | Measure-Object -Sum).Sum
                    words           = ($tidyPerFile.Values | ForEach-Object { $_.words } | Measure-Object -Sum).Sum
                }
            } else { $null }
            perFile           = @($perFile.Values)
        }
        $modelRows.Add([PSCustomObject] $row)

        foreach ($style in $Styles) {
            $s = $styleResults[$style]
            Write-Host ("  {0,-12} WER {1,6:F2}%  (S {2:N0} / D {3:N0} / I {4:N0} over {5:N0} reference words; raw {6:F2}%)" -f
                $style, $s.wer, $s.substitutions, $s.deletions, $s.insertions, $s.referenceWords, $s.rawWer)
        }
        if ($null -ne $tidyStyleResults) {
            foreach ($style in $Styles) {
                $t = $tidyStyleResults[$style]
                $delta = $t.wer - $styleResults[$style].wer
                Write-Host ("  {0,-12} WER {1,6:F2}%  (S {2:N0} / D {3:N0} / I {4:N0}; {5}{6:F2} points against the spoken transcript)" -f
                    "$style tidy", $t.wer, $t.substitutions, $t.deletions, $t.insertions, $(if ($delta -ge 0) { '+' } else { '' }), $delta) -ForegroundColor Cyan
            }
            $t = $row.tidy
            Write-Host ("  {0,-12} {1:N0} of {2:N0} lines refused and kept as spoken, {3:N0} emptied; {4:N0} words through the low-confidence door" -f
                'contract', $t.refusedSegments, $t.segments, $t.emptiedSegments, $t.replacedWords)
        }
        if ($audioTotal -gt 0) {
            Write-Host ("  {0,-12} RTF {1:F4} on {2} (decode {3:N0} s for {4:N0} s of audio, from the transcripts' own processingSec)" -f
                'speed', ($processingTotal / $audioTotal), $Backend, $processingTotal, $audioTotal)
        }
    }

    # ── summary ─────────────────────────────────────────────────────────────────────────────────

    $summary = [ordered]@{
        measuredAt   = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
        backend      = $Backend
        machine      = $machine
        corpus       = [ordered]@{
            name     = $manifest.name
            commit   = $manifest.source.commit
            files    = @($entries | ForEach-Object { $_.id })
            audioSeconds = [Math]::Round($totalAudioSeconds, 1)
        }
        keepFillers  = [bool] $KeepFillers
        tidy         = [bool] $Tidy
        tidyBackend  = $TidyBackend
        styles       = $Styles
        # Which build produced this, and which of the two checks the run skipped. A record that
        # names neither cannot say what it measured: -SkipBuild reuses whatever binary was already
        # in bin/, which need not be this revision's, and -SkipVerify trusts the corpus on disk
        # without re-hashing it against the manifest.
        skipBuild    = [bool] $SkipBuild
        skipVerify   = [bool] $SkipVerify
        revision     = $revision
        cliBuiltAt   = $cliBuiltAt
        models       = @($modelRows)
    }
    $summaryJson = Join-Path $OutputDirectory 'summary.json'
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryJson -Encoding UTF8

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add(("# WER on {0}, {1} backend, {2}" -f $manifest.name, $Backend, $summary.measuredAt))
    $lines.Add('')
    $lines.Add(("Machine: {0}; {1}; driver {2}. {3} files, {4:N2} h of audio. Normaliser: {5}." -f
        $machine.cpu, ($machine.gpu -join ' | '), $machine.driver, $entries.Count, ($totalAudioSeconds / 3600),
        $(if ($KeepFillers) { 'basic, fillers kept' } else { 'basic, fillers dropped' })))
    $lines.Add('')
    $werSwitches = [Collections.Generic.List[string]]::new()
    if ($SkipBuild) { $werSwitches.Add('-SkipBuild') }
    if ($SkipVerify) { $werSwitches.Add('-SkipVerify') }
    $lines.Add(("{0} {1}{2}, from a revision this run reads as {3}. Switches: {4}." -f
        $(if ($SkipBuild) { 'Ran the binary already in `bin/` — **this run built nothing** — last written' } else { 'Built' }),
        $cliBuiltAt,
        $(if ($SkipBuild) { '' } else { ' by this run' }),
        $(if ($revision.commit) {
            "``$($revision.commit)``" + $(if ($revision.dirty) { ' **with the working tree dirty**' } else { ', tree clean' })
        }
        else { 'unreadable' }),
        $(if ($werSwitches.Count -gt 0) { $werSwitches -join ', ' } else { 'none' })))
    $lines.Add('')
    $header = '| Model | ' + (($Styles | ForEach-Object { "WER vs $_" }) -join ' | ') + ' | S / D / I (verbatim) | RTF (' + $Backend + ') |'
    $lines.Add($header)
    $lines.Add('|---|' + (($Styles | ForEach-Object { '---|' }) -join '') + '---|---|')
    foreach ($row in $modelRows) {
        if ($row.failed) { $lines.Add(("| {0} | FAILED (exit {1}) |" -f $row.model, $row.exitCode)); continue }
        $cells = @($Styles | ForEach-Object { ("{0:F2}%" -f $row.styles[$_].wer) })
        $firstStyle = $row.styles[$Styles[0]]
        $lines.Add(("| {0} | {1} | {2:N0} / {3:N0} / {4:N0} | {5:F4} |" -f $row.model, ($cells -join ' | '),
            $firstStyle.substitutions, $firstStyle.deletions, $firstStyle.insertions, $row.realTimeFactor))

        if ($null -ne $row.tidy -and $null -ne $row.tidy.styles) {
            $tidyCells = @($Styles | ForEach-Object { ("{0:F2}%" -f $row.tidy.styles[$_].wer) })
            $tidyFirst = $row.tidy.styles[$Styles[0]]
            $lines.Add(("| {0}, tidied | {1} | {2:N0} / {3:N0} / {4:N0} | — |" -f $row.model, ($tidyCells -join ' | '),
                $tidyFirst.substitutions, $tidyFirst.deletions, $tidyFirst.insertions))
            $deltaCells = @($Styles | ForEach-Object {
                $d = $row.tidy.styles[$_].wer - $row.styles[$_].wer
                ("**{0}{1:F2}**" -f $(if ($d -ge 0) { '+' } else { '' }), $d)
            })
            $lines.Add(("| the tidy's delta | {0} | | |" -f ($deltaCells -join ' | ')))
        }
    }
    $lines.Add('')
    $lines.Add('Per file, WER vs ' + $Styles[0] + ':')
    $lines.Add('')
    $lines.Add('| File | Country | ' + (($modelRows | Where-Object { -not $_.failed } | ForEach-Object { $_.model }) -join ' | ') + ' |')
    $lines.Add('|---|---|' + (($modelRows | Where-Object { -not $_.failed } | ForEach-Object { '---|' }) -join ''))
    foreach ($entry in $entries) {
        $id = [string] $entry.id
        $cells = @($modelRows | Where-Object { -not $_.failed } | ForEach-Object {
            $file = @($_.perFile | Where-Object { $_.id -eq $id }) | Select-Object -First 1
            if ($file) { ("{0:F2}%" -f $file["wer_$($Styles[0])"]) } else { '—' }
        })
        $lines.Add(("| {0} | {1} | {2} |" -f $id, $entry.country, ($cells -join ' | ')))
    }
    foreach ($row in $modelRows) {
        if ($row.failed -or $null -eq $row.tidy -or $null -eq $row.tidy.styles) { continue }
        $lines.Add('')
        $lines.Add(("The tidy on {0}: {1} lines, {2} refused and kept as spoken, {3} emptied outright, {4} words through the low-confidence door." -f
            $row.model, $row.tidy.segments, $row.tidy.refusedSegments, $row.tidy.emptiedSegments, $row.tidy.replacedWords))
        $lines.Add(("Tidying model: {0}, on {1}." -f (($row.tidy.model -join ', ')), $(if ($row.tidy.backend) { $row.tidy.backend } else { 'the best drop vendored' })))
        foreach ($style in $Styles) {
            $lines.Add('')
            $lines.Add(("Per file against the $style reference, spoken and tidied:"))
            $lines.Add('')
            $lines.Add('| File | Country | spoken | tidied | Δ | lines | refused | emptied | door |')
            $lines.Add('|---|---|---|---|---|---|---|---|---|')
            foreach ($file in $row.perFile) {
                $d = $file["tidy_wer_$style"] - $file["wer_$style"]
                $lines.Add(("| {0} | {1} | {2:F2}% | {3:F2}% | {4}{5:F2} | {6:N0} | {7:N0} | {8:N0} | {9:N0} |" -f
                    $file.id, $file.country, $file["wer_$style"], $file["tidy_wer_$style"],
                    $(if ($d -ge 0) { '+' } else { '' }), $d, $file.tidy.segments, $file.tidy.refusedSegments,
                    $file.tidy.emptiedSegments, $file.tidy.replacedWords))
            }
        }
    }

    $lines.Add('')
    $lines.Add('WER is (S + D + I) / reference words over tokens normalised the same way on both sides; see `uindosill wer --help`.')
    $lines.Add('Not comparable to leaderboard figures for the same model, which use a richer normaliser. RTF is from the')
    $lines.Add('transcripts'' own processingSec over their audioDurationSec, and is per backend.')
    $summaryMd = Join-Path $OutputDirectory 'summary.md'
    $lines | Set-Content -LiteralPath $summaryMd -Encoding UTF8

    Write-Host ''
    Write-Host '── summary ─────────────────────────────────────' -ForegroundColor Green
    foreach ($line in $lines) { Write-Host $line }
    Write-Host ''
    Write-Host ("written: {0}" -f $summaryJson)
    Write-Host ("         {0}" -f $summaryMd)
}
finally {
    Pop-Location
}
