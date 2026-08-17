<#
.SYNOPSIS
    Diarisation error rate of speaker-turn hypotheses against the hand-labelled development
    stretches, and the cutting of those stretches from the episodes — the harness the speaker
    ship gate is scored with.

.DESCRIPTION
    Two jobs, one script, because both revolve around the same manifest:
    tests/fixtures/diarisation/dev/stretches.json, which pins each ten-minute development stretch to
    an episode, an onset, the exact ffmpeg line that cuts it, the ffmpeg version that ran, and the
    SHA-256 of what came out. The audio itself never enters the repository; the pins do.

    -Cut re-creates the stretch WAVs from the episodes at the repository root (`lab.ps1 drive
    -Episodes` fetches them, sizes checked against the Drive) into runs/der/stretches/, with the
    manifest's own ffmpeg line — a re-encode, never `-c copy`, because `-ss` before `-i` is
    sample-accurate only when transcoding — and refuses to trust a WAV whose bytes do not match the
    pin. Two hashes are checked: the whole file, which pins the ffmpeg build as well as the audio
    (ffmpeg copies the episode's tags into the WAV header, encoder string included), and the PCM
    data chunk alone, which pins the samples. A different ffmpeg that produced identical samples
    passes with a note; different samples fail, and nothing should be measured against them.

    The default mode scores. Given a directory of hypothesis RTTMs — one <id>.rttm per stretch, as
    `uindosill transcribe --speakers -f rttm` writes or as a spike emits — it runs `uindosill der`
    against the reference RTTM of the same id in tests/fixtures/diarisation/dev/ and writes
    runs/der/<timestamp>-<system>/summary.{json,md}: per stretch and summed, the headline DER
    (collar 0.25 s, pyannote.metrics semantics, overlap included), the strict collar-0 number and
    the reference-overlap-region breakdown, every one named with its convention. Stretches with no
    reference yet are listed as unlabelled rather than silently skipped.

    Read `uindosill der --help` before quoting a figure from here: the collar is a total width
    centred on each reference boundary, so md-eval's and NeMo's "collar 0.25" is this scorer's 0.5.

.EXAMPLE
    # After `lab.ps1 drive -Episodes`: the development stretches, cut and verified against the pins.
    .\scripts\measure-der.ps1 -Cut

.EXAMPLE
    # Score a spike's output. runs/spike-x/*.rttm are named by stretch id.
    .\scripts\measure-der.ps1 -Hypotheses runs\spike-x -System "sherpa-onnx 1.13.5 cpu"

.EXAMPLE
    # The product's own opt-in over the stretches, then scored. Only the canned labeller exists in
    # this build, so --fake is what runs today, and the CLI takes files, not wildcards.
    Get-ChildItem runs\der\stretches\*.wav | ForEach-Object { uindosill transcribe --fake --speakers -f rttm -o runs\product $_.FullName }
    .\scripts\measure-der.ps1 -Hypotheses runs\product -System "uindosill --speakers (canned labeller)"

.EXAMPLE
    .\scripts\lab.ps1 der -Cut
    .\scripts\lab.ps1 der -Hypotheses runs\spike-x -System "sherpa-onnx 1.13.5 cpu"
#>

[CmdletBinding(DefaultParameterSetName = 'Score')]
param(
    # ── score ──────────────────────────────────────────────────────────────────────────────────

    # Directory of hypothesis RTTMs, one <stretch id>.rttm each.
    [Parameter(ParameterSetName = 'Score', Mandatory)]
    [string] $Hypotheses,

    # What produced the hypotheses — a candidate name, a version, a backend. Named in the summary
    # and in the output directory. Default: the hypotheses directory's own name.
    [Parameter(ParameterSetName = 'Score')]
    [string] $System,

    # Where the reference RTTMs live. Default tests/fixtures/diarisation/dev/.
    [Parameter(ParameterSetName = 'Score')]
    [string] $ReferenceDirectory,

    # The headline collar in seconds — total width centred on each reference boundary, pyannote.metrics
    # semantics. 0.25 is the convention of arXiv 2509.26177 and of the proposed gate.
    [Parameter(ParameterSetName = 'Score')]
    [double] $Collar = 0.25,

    # Leave reference-overlap regions out of the score. Off by default: crosstalk is what is measured.
    [Parameter(ParameterSetName = 'Score')]
    [switch] $SkipOverlap,

    # Where the summary lands. Default runs/der/<timestamp>-<system>/.
    [Parameter(ParameterSetName = 'Score')]
    [string] $OutputDirectory,

    [Parameter(ParameterSetName = 'Score')]
    [string] $Configuration = 'Release',

    [Parameter(ParameterSetName = 'Score')]
    [switch] $SkipBuild,

    # ── cut ────────────────────────────────────────────────────────────────────────────────────

    [Parameter(ParameterSetName = 'Cut', Mandatory)]
    [switch] $Cut,

    # Stretch ids to cut. Default: every stretch in the manifest.
    [Parameter(ParameterSetName = 'Cut')]
    [string[]] $Stretches,

    # Where the episode mp3s are. Default: the repository root, where `lab.ps1 drive -Episodes` puts them.
    [Parameter(ParameterSetName = 'Cut')]
    [string] $EpisodeDirectory,

    # Where the WAVs land. Default runs/der/stretches/, gitignored like everything under runs/.
    [Parameter(ParameterSetName = 'Cut')]
    [string] $Destination,

    # Re-cut even when a WAV is already there and matches its pin.
    [Parameter(ParameterSetName = 'Cut')]
    [switch] $Force,

    # The stretch manifest. Default tests/fixtures/diarisation/dev/stretches.json.
    [Parameter(ParameterSetName = 'Cut')]
    [Parameter(ParameterSetName = 'Score')]
    [string] $ManifestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The summaries are pasted into public documents, so numbers are formatted the same way on every
# machine: 0.25, not 0,25 on a machine whose Windows speaks a comma-decimal language.
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
[Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::InvariantCulture

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo

try {
    if (-not $ManifestPath) { $ManifestPath = Join-Path $repo 'tests' 'fixtures' 'diarisation' 'dev' 'stretches.json' }
    if (-not (Test-Path -LiteralPath $ManifestPath)) { throw "Stretch manifest not found: $ManifestPath" }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $manifestDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $ManifestPath).Path

    # ── the SHA-256 of a WAV's data chunk alone ─────────────────────────────────────────────────

    function Get-PcmSha256 {
        param([string] $Path)

        $bytes = [IO.File]::ReadAllBytes($Path)
        if ($bytes.Length -lt 12 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'RIFF' -or [Text.Encoding]::ASCII.GetString($bytes, 8, 4) -ne 'WAVE') {
            throw "$Path is not a RIFF/WAVE file."
        }

        $offset = 12
        while ($offset + 8 -le $bytes.Length) {
            $id = [Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
            $size = [BitConverter]::ToUInt32($bytes, $offset + 4)
            if ($id -eq 'data') {
                $length = [Math]::Min([long] $size, $bytes.Length - $offset - 8)
                $sha = [Security.Cryptography.SHA256]::Create()
                try {
                    return ([BitConverter]::ToString($sha.ComputeHash($bytes, $offset + 8, [int] $length)) -replace '-', '').ToLowerInvariant()
                }
                finally { $sha.Dispose() }
            }
            # Chunks are word-aligned: an odd-sized chunk carries one byte of padding.
            $offset += 8 + $size + ($size % 2)
        }

        throw "$Path has no data chunk."
    }

    function Get-FileSha256([string] $Path) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    switch ($PSCmdlet.ParameterSetName) {

        # ── cut ────────────────────────────────────────────────────────────────────────────────

        'Cut' {
            $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
            if (-not $ffmpeg) { throw 'ffmpeg is not on PATH.' }
            $versionLine = (& $ffmpeg.Source -version 2>&1 | Select-Object -First 1).ToString()
            $version = if ($versionLine -match '^ffmpeg version (\S+)') { $Matches[1] } else { $versionLine }

            if (-not $EpisodeDirectory) { $EpisodeDirectory = $repo }
            if (-not $Destination) { $Destination = Join-Path $repo 'runs' 'der' 'stretches' }
            New-Item -ItemType Directory -Force -Path $Destination | Out-Null
            $Destination = (Resolve-Path -LiteralPath $Destination).Path

            $wanted = @($manifest.stretches)
            if ($Stretches) {
                $ids = @(($Stretches -join ',') -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
                $wanted = @($wanted | Where-Object { $_.id -in $ids })
                $missing = @($ids | Where-Object { $_ -notin @($wanted | ForEach-Object { $_.id }) })
                if ($missing.Count -gt 0) { throw "Not in the manifest: $($missing -join ', ')" }
            }

            Write-Host ''
            Write-Host '── cutting stretches ───────────────────────────' -ForegroundColor Green
            Write-Host ("  ffmpeg  {0}" -f $version)
            if ($version -ne $manifest.ffmpeg.version) {
                Write-Host ("  pinned  {0} — a different build; the file hash may differ while the samples agree, and this says which" -f $manifest.ffmpeg.version) -ForegroundColor Yellow
            }
            Write-Host ("  line    {0}" -f $manifest.ffmpeg.line)
            Write-Host ("  into    {0}" -f $Destination)
            Write-Host ''

            $bad = 0
            $unpinned = 0
            foreach ($stretch in $wanted) {
                $source = Join-Path $EpisodeDirectory $stretch.episode
                $target = Join-Path $Destination ("{0}.wav" -f $stretch.id)

                if (-not (Test-Path -LiteralPath $source)) {
                    Write-Host ("  {0,-28} episode missing: {1} (run lab.ps1 drive -Episodes)" -f $stretch.id, $source) -ForegroundColor Red
                    $bad++
                    continue
                }

                # The episode itself, against what the manifest recorded, so a re-encoded or truncated
                # copy is caught before it is cut. Guarded lookups: under StrictMode a missing property
                # throws, and a stretch on an episode the manifest does not know yet is exactly the
                # bootstrap case below.
                $episodePin = if ($manifest.episodes.PSObject.Properties[$stretch.episode]) { $manifest.episodes.($stretch.episode) } else { $null }
                $pin = if ($stretch.PSObject.Properties['wav'] -and $null -ne $stretch.wav) { $stretch.wav } else { $null }
                $episodeLength = (Get-Item -LiteralPath $source).Length
                if ($episodePin -and $episodeLength -ne [long] $episodePin.bytes) {
                    Write-Host ("  {0,-28} episode {1} is {2:N0} B, manifest says {3:N0} B — not the same file; not cut" -f $stretch.id, $stretch.episode, $episodeLength, [long] $episodePin.bytes) -ForegroundColor Red
                    $bad++
                    continue
                }

                $present = Test-Path -LiteralPath $target
                if ($present -and -not $Force -and $pin -and (Get-FileSha256 $target) -eq $pin.sha256) {
                    Write-Host ("  {0,-28} already there and matches the pin" -f $stretch.id) -ForegroundColor Gray
                    continue
                }

                # The manifest's line, with its placeholders filled. `-ss` before `-i`, re-encoding to
                # 16 kHz mono PCM: sample-accurate seeking, and the pin makes the decoder question moot.
                $arguments = @('-hide_banner', '-loglevel', 'error', '-y',
                    '-ss', ([string] $stretch.onsetSeconds), '-t', ([string] $stretch.durationSeconds),
                    '-i', $source, '-ac', '1', '-ar', '16000', '-c:a', 'pcm_s16le', $target)
                & $ffmpeg.Source @arguments
                if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $($stretch.id) (exit $LASTEXITCODE)." }

                $length = (Get-Item -LiteralPath $target).Length
                $fileHash = Get-FileSha256 $target
                $pcmHash = Get-PcmSha256 $target

                if (-not $pin) {
                    # A stretch without a `wav` block is one being added: cut it, and print what the
                    # manifest needs so the pin can be pasted in. Not a failure — it is how a pin is born.
                    Write-Host ("  {0,-28} {1,12:N0} B  UNPINNED — add to stretches.json:" -f $stretch.id, $length) -ForegroundColor Yellow
                    Write-Host ('      "wav": {{ "bytes": {0}, "sha256": "{1}", "pcmBytes": {2}, "pcmSha256": "{3}" }}   (ffmpeg {4})' -f $length, $fileHash, ([long] $length - 44), $pcmHash, $version) -ForegroundColor Yellow
                    $unpinned++
                }
                elseif ($fileHash -eq $pin.sha256 -and $length -eq [long] $pin.bytes) {
                    Write-Host ("  {0,-28} {1,12:N0} B  file and PCM match the pin" -f $stretch.id, $length) -ForegroundColor Gray
                }
                elseif ($pcmHash -eq $pin.pcmSha256) {
                    Write-Host ("  {0,-28} {1,12:N0} B  PCM matches the pin; the file does not (header differs — ffmpeg {2} against pinned {3})" -f $stretch.id, $length, $version, $manifest.ffmpeg.version) -ForegroundColor Yellow
                }
                else {
                    Write-Host ("  {0,-28} {1,12:N0} B  DIFFERS: PCM {2} against pinned {3}. Do not measure against this file." -f $stretch.id, $length, $pcmHash, $pin.pcmSha256) -ForegroundColor Red
                    $bad++
                }
            }

            Write-Host ''
            if ($bad -gt 0) { throw "$bad stretch(es) could not be cut to their pins." }
            if ($unpinned -gt 0) { Write-Host ("  {0} stretch(es) cut without a pin — record the digests above before measuring against them" -f $unpinned) -ForegroundColor Yellow }
            else { Write-Host '  every stretch cut matches its pinned samples' -ForegroundColor Green }
        }

        # ── score ──────────────────────────────────────────────────────────────────────────────

        'Score' {
            $Hypotheses = (Resolve-Path -LiteralPath $Hypotheses).Path
            if (-not $System) { $System = Split-Path -Leaf $Hypotheses }
            if (-not $ReferenceDirectory) { $ReferenceDirectory = $manifestDirectory }
            $ReferenceDirectory = (Resolve-Path -LiteralPath $ReferenceDirectory).Path

            if (-not $SkipBuild) {
                Write-Host ''
                Write-Host 'Building...' -ForegroundColor Cyan
                dotnet build src/Parakeet.Cli -c $Configuration --nologo | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
            }

            $exe = Join-Path $repo "src/Parakeet.Cli/bin/$Configuration/net10.0/uindosill.exe"
            if (-not (Test-Path -LiteralPath $exe)) { $exe = Join-Path $repo "src/Parakeet.Cli/bin/$Configuration/net10.0/uindosill" }
            if (-not (Test-Path -LiteralPath $exe)) { throw "Built executable not found. Run without -SkipBuild, or check bin/$Configuration/net10.0." }

            $slug = ($System -replace '[^A-Za-z0-9._-]+', '-').Trim('-')
            if (-not $OutputDirectory) {
                $OutputDirectory = Join-Path $repo 'runs' 'der' ("{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $slug)
            }
            New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
            $OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

            # Named hardware, never a machine name: these summaries get pasted into a public document.
            $machine = [ordered]@{ os = [Environment]::OSVersion.VersionString; cpu = $null; gpu = @() }
            try {
                $machine.cpu = (Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1).Name.Trim()
                $machine.gpu = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object { $_.Name })
            }
            catch { }

            Write-Host ''
            Write-Host '── scoring ─────────────────────────────────────' -ForegroundColor Green
            Write-Host ("  system      {0}" -f $System)
            Write-Host ("  hypotheses  {0}" -f $Hypotheses)
            Write-Host ("  references  {0}" -f $ReferenceDirectory)
            Write-Host ("  convention  collar {0} s (pyannote.metrics semantics: {1} s either side of each reference boundary), overlap {2}" -f $Collar, ($Collar / 2), $(if ($SkipOverlap) { 'skipped' } else { 'included' }))
            Write-Host ("  output      {0}" -f $OutputDirectory)

            $rows = [Collections.Generic.List[object]]::new()
            $unlabelled = [Collections.Generic.List[string]]::new()
            $unscored = [Collections.Generic.List[string]]::new()

            foreach ($stretch in @($manifest.stretches)) {
                $reference = Join-Path $ReferenceDirectory ("{0}.rttm" -f $stretch.id)
                $hypothesis = Join-Path $Hypotheses ("{0}.rttm" -f $stretch.id)
                if (-not (Test-Path -LiteralPath $reference)) { $unlabelled.Add($stretch.id); continue }
                if (-not (Test-Path -LiteralPath $hypothesis)) { $unscored.Add($stretch.id); continue }

                $arguments = @('der', '--reference', $reference, '--collar', ([string] $Collar), '--json') + $(if ($SkipOverlap) { @('--skip-overlap') } else { @() }) + @($hypothesis)
                $raw = & $exe @arguments
                if ($LASTEXITCODE -ne 0) { throw "der failed for $($stretch.id) (exit $LASTEXITCODE): $($raw -join ' ')" }
                $scored = ($raw -join "`n") | ConvertFrom-Json
                $h = $scored.hypotheses[0]

                $rows.Add([PSCustomObject]@{
                    id               = $stretch.id
                    episode          = $stretch.episode
                    nominalVoices    = $stretch.nominalVoices
                    referenceSpeakers = @($h.referenceSpeakers).Count
                    hypothesisSpeakers = @($h.hypothesisSpeakers).Count
                    headline         = $h.headline
                    strict           = $h.strict
                    overlapRegions   = $h.overlapRegions
                    mapping          = $h.mapping
                    warnings         = @($h.warnings)
                })

                Write-Host ("  {0,-28} DER {1,7:F2}%  miss {2,6:F2}%  FA {3,6:F2}%  conf {4,6:F2}%  | collar 0: {5,7:F2}%  | overlap regions: {6,7} DER on {7:F1} s" -f
                    $stretch.id, (100 * $h.headline.rate), (100 * $h.headline.missedRate), (100 * $h.headline.falseAlarmRate), (100 * $h.headline.confusionRate),
                    (100 * $h.strict.rate), $(if ($null -eq $h.overlapRegions.rate) { 'n/a' } else { ('{0:F2}%' -f (100 * $h.overlapRegions.rate)) }), $h.overlapRegions.referenceSpeechSeconds)
                foreach ($w in @($h.warnings)) { Write-Host ("      note: {0}" -f $w) -ForegroundColor Yellow }
            }

            foreach ($id in $unlabelled) { Write-Host ("  {0,-28} no reference yet — not labelled" -f $id) -ForegroundColor DarkGray }
            foreach ($id in $unscored) { Write-Host ("  {0,-28} labelled, but no hypothesis in {1}" -f $id, $Hypotheses) -ForegroundColor Yellow }
            if ($rows.Count -eq 0) { throw 'Nothing was scored: no stretch has both a reference and a hypothesis.' }

            function Sum-Components($block) {
                $t = 0.0; $m = 0.0; $f = 0.0; $c = 0.0
                foreach ($r in $rows) { $b = $r.$block; $t += $b.referenceSpeechSeconds; $m += $b.missedSeconds; $f += $b.falseAlarmSeconds; $c += $b.confusionSeconds }
                return [ordered]@{
                    referenceSpeechSeconds = [Math]::Round($t, 6); missedSeconds = [Math]::Round($m, 6); falseAlarmSeconds = [Math]::Round($f, 6); confusionSeconds = [Math]::Round($c, 6)
                    rate = $(if ($t -gt 0) { [Math]::Round(($m + $f + $c) / $t, 6) } else { $null })
                    missedRate = $(if ($t -gt 0) { [Math]::Round($m / $t, 6) } else { $null })
                    falseAlarmRate = $(if ($t -gt 0) { [Math]::Round($f / $t, 6) } else { $null })
                    confusionRate = $(if ($t -gt 0) { [Math]::Round($c / $t, 6) } else { $null })
                }
            }

            $summed = [ordered]@{ headline = Sum-Components 'headline'; strict = Sum-Components 'strict'; overlapRegions = Sum-Components 'overlapRegions' }

            $summary = [ordered]@{
                measuredAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
                system     = $System
                machine    = $machine
                convention = [ordered]@{
                    collarSeconds   = $Collar
                    collarSemantics = 'pyannote.metrics: total width centred on each reference boundary (md-eval and NeMo quote the half-width)'
                    skipOverlap     = [bool] $SkipOverlap
                    strict          = 'the same components at collar 0'
                    overlapRegions  = 'the same components over regions where two or more reference speakers talk at once, under the whole-file mapping'
                }
                hypotheses = $Hypotheses
                references = $ReferenceDirectory
                stretches  = @($rows)
                unlabelled = @($unlabelled)
                unscored   = @($unscored)
                summed     = $summed
            }
            $summaryJson = Join-Path $OutputDirectory 'summary.json'
            $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryJson -Encoding UTF8

            function Pct($rate) { if ($null -eq $rate) { 'n/a' } else { '{0:F2}%' -f (100 * [double] $rate) } }

            $lines = [Collections.Generic.List[string]]::new()
            $lines.Add(("# DER of {0} on the development stretches, {1}" -f $System, $summary.measuredAt))
            $lines.Add('')
            $lines.Add(("Machine: {0}; {1}. {2} stretch(es) scored, {3} unlabelled, {4} without a hypothesis." -f $machine.cpu, ($machine.gpu -join ' | '), $rows.Count, $unlabelled.Count, $unscored.Count))
            $lines.Add(("Convention: collar {0} s — pyannote.metrics semantics, {1} s either side of each reference boundary — overlap {2}. md-eval's and NeMo's ""collar 0.25"" is this scorer's 0.5." -f $Collar, ($Collar / 2), $(if ($SkipOverlap) { 'skipped' } else { 'included' })))
            $lines.Add('')
            $lines.Add('| Stretch | Voices (nominal / ref / hyp) | DER | miss | FA | conf | DER at collar 0 | Overlap-region DER (miss) | Overlap s |')
            $lines.Add('|---|---|---|---|---|---|---|---|---|')
            foreach ($r in $rows) {
                $lines.Add(("| {0} | {1} / {2} / {3} | {4} | {5} | {6} | {7} | {8} | {9} ({10}) | {11:F1} |" -f $r.id, $r.nominalVoices, $r.referenceSpeakers, $r.hypothesisSpeakers,
                    (Pct $r.headline.rate), (Pct $r.headline.missedRate), (Pct $r.headline.falseAlarmRate), (Pct $r.headline.confusionRate),
                    (Pct $r.strict.rate), (Pct $r.overlapRegions.rate), (Pct $r.overlapRegions.missedRate), $r.overlapRegions.referenceSpeechSeconds))
            }
            $lines.Add(("| **all, summed** | | **{0}** | {1} | {2} | {3} | {4} | {5} ({6}) | {7:F1} |" -f (Pct $summed.headline.rate), (Pct $summed.headline.missedRate), (Pct $summed.headline.falseAlarmRate), (Pct $summed.headline.confusionRate),
                (Pct $summed.strict.rate), (Pct $summed.overlapRegions.rate), (Pct $summed.overlapRegions.missedRate), $summed.overlapRegions.referenceSpeechSeconds))
            $lines.Add('')
            foreach ($id in $unlabelled) { $lines.Add(("- {0}: no reference labels yet." -f $id)) }
            foreach ($id in $unscored) { $lines.Add(("- {0}: labelled, no hypothesis in the scored directory." -f $id)) }
            if ($unlabelled.Count + $unscored.Count -gt 0) { $lines.Add('') }
            $lines.Add('DER is (missed + false alarm + confusion) / reference speech under the optimal one-to-one speaker mapping; the summed row')
            $lines.Add('sums components over stretches, weighting a long stretch more. Computed by `uindosill der`, validated against pyannote.metrics')
            $lines.Add('on the fixture pairs in tests/fixtures/diarisation/scorer/. Real-time factors and memory are not measured here: they come')
            $lines.Add('from the run that produced the hypotheses, and belong beside them, named with their backend.')
            $summaryMd = Join-Path $OutputDirectory 'summary.md'
            $lines | Set-Content -LiteralPath $summaryMd -Encoding UTF8

            Write-Host ''
            Write-Host '── summary ─────────────────────────────────────' -ForegroundColor Green
            foreach ($line in $lines) { Write-Host $line }
            Write-Host ''
            Write-Host ("written: {0}" -f $summaryJson)
            Write-Host ("         {0}" -f $summaryMd)
        }
    }
}
finally {
    Pop-Location
}
