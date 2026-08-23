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
    }
    catch {
        # Not Windows, or CIM unavailable: the block stays partly empty rather than the run failing.
    }

    Write-Host ''
    Write-Host '── machine ─────────────────────────────────────' -ForegroundColor Green
    Write-Host ("  os      {0}" -f $machine.os)
    Write-Host ("  cpu     {0}" -f $machine.cpu)
    Write-Host ("  gpu     {0}" -f ($machine.gpu -join ' | '))
    if ($machine.driver) { Write-Host ("  driver  {0}" -f $machine.driver) }
    Write-Host ("  backend {0}" -f $Backend)
    Write-Host ("  output  {0}" -f $OutputDirectory)

    # ── transcribe and score, per model ─────────────────────────────────────────────────────────

    $mediaPaths = @($entries | ForEach-Object { Join-Path $CorpusRoot 'media' "$($_.id).mp3" })
    $modelRows = [Collections.Generic.List[object]]::new()

    foreach ($model in $Models) {
        $modelDirectory = Join-Path $OutputDirectory $model
        New-Item -ItemType Directory -Force -Path $modelDirectory | Out-Null

        Write-Host ''
        Write-Host ("── {0} on {1} ─────────────────────────────" -f $model, $Backend) -ForegroundColor Green

        $arguments = @('transcribe', '--backend', $Backend, '--model', $model, '-f', 'json,txt', '-o', $modelDirectory, '--overwrite', '--quiet') + $(if ($Vad) { @('--vad', $Vad) } else { @() }) + $mediaPaths
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
                words            = (@($document.segments) | ForEach-Object { @($_.words).Count } | Measure-Object -Sum).Sum
            }
        }

        $hypotheses = @($perFile.Keys | ForEach-Object { Join-Path $modelDirectory "$_.json" })
        $styleResults = [ordered]@{}
        foreach ($style in $Styles) {
            $werArguments = @('wer', '--reference-dir', (Join-Path $CorpusRoot $style), '--json') + $(if ($KeepFillers) { @('--keep-fillers') } else { @() }) + $hypotheses
            $raw = & $exe @werArguments
            if ($LASTEXITCODE -ne 0) { throw "wer failed for $model against $style (exit $LASTEXITCODE): $($raw -join ' ')" }
            $scored = ($raw -join "`n") | ConvertFrom-Json

            # A rate is null in the JSON when the reference was empty; it never is here, but a null
            # multiplied would stop the run over a bookkeeping detail rather than a measurement.
            function ConvertTo-Percent($rate) { if ($null -eq $rate) { $null } else { [Math]::Round(100.0 * [double] $rate, 2) } }

            foreach ($h in @($scored.hypotheses)) {
                $id = [IO.Path]::GetFileNameWithoutExtension([string] $h.path)
                $perFile[$id]["wer_$style"] = ConvertTo-Percent $h.normalised.rate
                $perFile[$id]["errors_$style"] = [ordered]@{
                    referenceWords = $h.normalised.referenceWords
                    substitutions  = $h.normalised.substitutions
                    deletions      = $h.normalised.deletions
                    insertions     = $h.normalised.insertions
                    rawRate        = ConvertTo-Percent $h.raw.rate
                }
            }

            $styleResults[$style] = [ordered]@{
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
            perFile           = @($perFile.Values)
        }
        $modelRows.Add([PSCustomObject] $row)

        foreach ($style in $Styles) {
            $s = $styleResults[$style]
            Write-Host ("  {0,-12} WER {1,6:F2}%  (S {2:N0} / D {3:N0} / I {4:N0} over {5:N0} reference words; raw {6:F2}%)" -f
                $style, $s.wer, $s.substitutions, $s.deletions, $s.insertions, $s.referenceWords, $s.rawWer)
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
        styles       = $Styles
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
    $header = '| Model | ' + (($Styles | ForEach-Object { "WER vs $_" }) -join ' | ') + ' | S / D / I (verbatim) | RTF (' + $Backend + ') |'
    $lines.Add($header)
    $lines.Add('|---|' + (($Styles | ForEach-Object { '---|' }) -join '') + '---|---|')
    foreach ($row in $modelRows) {
        if ($row.failed) { $lines.Add(("| {0} | FAILED (exit {1}) |" -f $row.model, $row.exitCode)); continue }
        $cells = @($Styles | ForEach-Object { ("{0:F2}%" -f $row.styles[$_].wer) })
        $firstStyle = $row.styles[$Styles[0]]
        $lines.Add(("| {0} | {1} | {2:N0} / {3:N0} / {4:N0} | {5:F4} |" -f $row.model, ($cells -join ' | '),
            $firstStyle.substitutions, $firstStyle.deletions, $firstStyle.insertions, $row.realTimeFactor))
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
