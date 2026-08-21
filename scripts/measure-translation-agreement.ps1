<#
.SYNOPSIS
    Does the shipping decode reproduce the English the translation gate was scored on?

.DESCRIPTION
    The two ONNX graphs are pinned by digest. The search over them is not, and it is a real degree
    of freedom: length penalty, when a finished beam displaces another, how equal-scoring candidates
    are ordered, when the loop is allowed to stop. A loop that differs in any of them produces
    different English and quietly stops being the thing that was measured — so this asks whether it
    does, against the only reference there is.

    **The reference already exists and was not built for this.** The 2026-08-20 gate run recorded
    every hypothesis it produced — 8,149 sentences across 24 languages, source and output — in
    `hypotheses/*.jsonl` under its run directory, written by HuggingFace's beam search over the
    same graphs. Not "does the output look reasonable": does it reproduce these strings.

    **What is on the other side of the comparison changed on 2026-08-21**, and this is now the
    single highest-value thing to run. It was written to hold a C# port of that beam search to the
    reference; that port is retired to `attic/` and the decode is `transformers.generate` again —
    the same library that wrote the reference, at the same settings, over the same graphs. So the
    expected result went from "reproduces it to within a recorded handful of disagreements" to
    "reproduces it exactly", and the one recorded disagreement (Hungarian 1818, 171 dots against
    248) should be gone. **It has not been run against the sidecar.** Until it has, the chrF++
    figures describe the shipping decode by construction rather than by measurement.

    So the number this prints is an **agreement rate**, per language, and it is not a quality score.
    It says nothing about whether the translations are good; `docs/UNPROVEN.md` carries the chrF++
    figures for that. What it says is whether the chrF++ figures still describe what the product
    would ship. If agreement is 100%, they do by construction. If it is not, this writes the
    disagreeing pairs out verbatim and the honest report is the disagreement — not a fresh score
    computed with the C# loop, which would only agree with itself.

    Text in, text out: it drives `uindosill translate`, which runs the same translator behind the
    same seam as `transcribe --translate` without the ASR pass. There is no audio for a FLEURS
    transcript and none is needed.

.PARAMETER Run
    The gate run directory holding `hypotheses/*.jsonl`. Default: the newest under `runs/translation`.

.PARAMETER Variant
    The exported checkpoint to decode with. Default `runs/translation-onnx/fp32-merged`, which is
    what ships.

.PARAMETER Languages
    Comma-separated source languages. Default: every one the run recorded. Start with `es` — 348
    sentences is minutes, and a port that is going to disagree disagrees there too.

.PARAMETER Sentences
    Stop after this many sentences per language, for a quick look. 0 (the default) is all of them.

.PARAMETER Threads
    Intra-op threads for the ONNX sessions. Default: whatever ONNX Runtime chooses, which is what
    the Python side used.

.EXAMPLE
    .\scripts\measure-translation-agreement.ps1 -Languages es

.EXAMPLE
    # The whole corpus. Budget from the gate run's own rate: 8,149 sentences at ~0.6 s each.
    .\scripts\measure-translation-agreement.ps1
#>

[CmdletBinding()]
param(
    [string] $Run,
    [string] $Variant,
    [string] $Languages,
    [int]    $Sentences = 0,
    [int]    $Threads = 0,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot

function Resolve-Run {
    if ($Run) {
        if (-not (Test-Path -LiteralPath $Run)) { throw "No such run directory: $Run" }
        return (Resolve-Path -LiteralPath $Run).Path
    }

    $root = Join-Path $repo 'runs/translation'
    if (-not (Test-Path -LiteralPath $root)) {
        throw "There is no runs/translation here. The gate run is machine-local and gitignored; " +
              "point -Run at a directory holding hypotheses/*.jsonl."
    }

    $newest = Get-ChildItem -LiteralPath $root -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'hypotheses') } |
        Sort-Object Name -Descending | Select-Object -First 1

    if (-not $newest) { throw "No run under $root carries a hypotheses/ directory." }
    return $newest.FullName
}

$runDirectory = Resolve-Run
$hypothesesDirectory = Join-Path $runDirectory 'hypotheses'

if (-not $Variant) { $Variant = Join-Path $repo 'runs/translation-onnx/fp32-merged' }
if (-not (Test-Path -LiteralPath $Variant)) {
    throw "No exported checkpoint at $Variant. It is 1.34 GiB and gitignored; see docs/UNPROVEN.md."
}
$Variant = (Resolve-Path -LiteralPath $Variant).Path

# Wrapped in @() around the whole conditional, not just inside each branch: a one-element array
# assigned from an if-expression is unwrapped to a scalar, and .Count on a scalar is an error under
# Set-StrictMode -Version Latest. -Languages es is exactly that case.
$codes = @(if ($Languages) {
    $Languages -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
} else {
    Get-ChildItem -LiteralPath $hypothesesDirectory -Filter '*.jsonl' |
        Sort-Object Name | ForEach-Object { $_.BaseName }
})

if ($codes.Count -eq 0) { throw "No languages to check under $hypothesesDirectory." }

# Built once. `uindosill translate` loads the checkpoint per invocation, so this calls it once per
# language rather than once per sentence.
Write-Host "building $Configuration ..." -ForegroundColor DarkGray
& dotnet build (Join-Path $repo 'src/Parakeet.Cli') -c $Configuration --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The CLI did not build.' }

$exe = Join-Path $repo "src/Parakeet.Cli/bin/$Configuration/net10.0/uindosill.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    $exe = Join-Path $repo "src/Parakeet.Cli/bin/$Configuration/net10.0/uindosill"
}
if (-not (Test-Path -LiteralPath $exe)) { throw "Built, but no uindosill at $exe." }

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$out = Join-Path $repo "runs/translation-agreement/$stamp-$(Split-Path -Leaf $Variant)"
New-Item -ItemType Directory -Force -Path $out | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $out 'disagreements') | Out-Null
$work = Join-Path $out 'sources'
New-Item -ItemType Directory -Force -Path $work | Out-Null

Write-Host ''
Write-Host "reference  $runDirectory"
Write-Host "variant    $Variant"
Write-Host "languages  $($codes.Count)   sentences $(if ($Sentences -gt 0) { $Sentences } else { 'all' })"
Write-Host "out        $out"
Write-Host ''

$results = [ordered]@{}
$totalSentences = 0
$totalMatched = 0
$totalSeconds = 0.0

foreach ($code in $codes) {
    $jsonl = Join-Path $hypothesesDirectory "$code.jsonl"
    if (-not (Test-Path -LiteralPath $jsonl)) {
        Write-Host "$code  SKIPPED: no $jsonl" -ForegroundColor DarkYellow
        continue
    }

    $rows = @(Get-Content -LiteralPath $jsonl -Encoding utf8 |
        Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })

    if ($Sentences -gt 0 -and $rows.Count -gt $Sentences) { $rows = @($rows[0..($Sentences - 1)]) }

    # One source per line is the whole contract of `uindosill translate`, so a source carrying a
    # newline would silently become two sentences and shift every line after it. Refused rather
    # than repaired: a corpus that does this is one to find out about.
    $broken = @($rows | Where-Object { $_.source -match "[`r`n]" })
    if ($broken.Count -gt 0) {
        throw "$code has $($broken.Count) source(s) containing a line break; this harness is line-oriented."
    }

    $sourceFile = Join-Path $work "$code.txt"
    Set-Content -LiteralPath $sourceFile -Value ($rows | ForEach-Object { $_.source }) -Encoding utf8NoBOM

    $started = Get-Date
    & $exe translate --model-path $Variant --id $code -o $work --threads $Threads $sourceFile | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "uindosill translate failed on $code (exit $LASTEXITCODE)." }
    $elapsed = ((Get-Date) - $started).TotalSeconds

    $englishFile = Join-Path $work "$code.en.txt"
    $english = @(Get-Content -LiteralPath $englishFile -Encoding utf8)

    if ($english.Count -ne $rows.Count) {
        throw "${code}: $($rows.Count) sentences in and $($english.Count) out."
    }

    $matched = 0
    $disagreements = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $rows.Count; $i++) {
        # Ordinal, not culture-aware and not trimmed: the question is whether the strings are the
        # same string. A difference in a space is a difference in what would be written to a file.
        if ([string]::Equals($english[$i], $rows[$i].hypothesis, [StringComparison]::Ordinal)) {
            $matched++
        } else {
            $disagreements.Add([ordered]@{
                id         = $rows[$i].id
                source     = $rows[$i].source
                recorded   = $rows[$i].hypothesis
                csharp     = $english[$i]
                reference  = $rows[$i].reference
            })
        }
    }

    $rate = if ($rows.Count -gt 0) { 100.0 * $matched / $rows.Count } else { 0.0 }
    $results[$code] = [ordered]@{
        sentences        = $rows.Count
        matched          = $matched
        agreementPercent = [math]::Round($rate, 2)
        seconds          = [math]::Round($elapsed, 1)
        secondsPerSentence = [math]::Round($elapsed / [math]::Max(1, $rows.Count), 3)
    }

    $totalSentences += $rows.Count
    $totalMatched += $matched
    $totalSeconds += $elapsed

    if ($disagreements.Count -gt 0) {
        $path = Join-Path $out "disagreements/$code.jsonl"
        Set-Content -LiteralPath $path -Value ($disagreements | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 4 }) -Encoding utf8NoBOM
    }

    $colour = if ($matched -eq $rows.Count) { 'Green' } else { 'Yellow' }
    Write-Host ("{0}  {1}/{2}  {3,6:N2}%  {4:N0}s  {5:N3} s/sentence" -f `
        $code, $matched, $rows.Count, $rate, $elapsed, ($elapsed / [math]::Max(1, $rows.Count))) -ForegroundColor $colour
}

$overall = if ($totalSentences -gt 0) { 100.0 * $totalMatched / $totalSentences } else { 0.0 }

$payload = [ordered]@{
    measuredUtc      = (Get-Date).ToUniversalTime().ToString('o')
    reference        = $runDirectory
    variant          = $Variant
    threads          = $Threads
    machine          = $env:COMPUTERNAME
    totalSentences   = $totalSentences
    totalMatched     = $totalMatched
    agreementPercent = [math]::Round($overall, 4)
    secondsPerSentence = [math]::Round($totalSeconds / [math]::Max(1, $totalSentences), 3)
    perLanguage      = $results
}

Set-Content -LiteralPath (Join-Path $out 'agreement.json') `
    -Value ($payload | ConvertTo-Json -Depth 6) -Encoding utf8NoBOM

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# C# decode loop against the recorded gate hypotheses')
$lines.Add('')
$lines.Add("Reference run ``$(Split-Path -Leaf $runDirectory)``, variant ``$(Split-Path -Leaf $Variant)``, " +
           "$(if ($Threads -gt 0) { "$Threads intra-op threads" } else { "ONNX Runtime's own thread count" }).")
$lines.Add('')
$lines.Add("**$totalMatched of $totalSentences sentences reproduce the recorded hypothesis exactly " +
           "— $([math]::Round($overall, 2))%.**")
$lines.Add('')
$lines.Add('Exact string agreement, ordinal, untrimmed. This is not a quality score: it says whether the')
$lines.Add('chrF++ figures in `docs/UNPROVEN.md` still describe what the product would ship.')
$lines.Add('')
$lines.Add('| | sentences | exact | agreement | s/sentence |')
$lines.Add('|---|---:|---:|---:|---:|')
foreach ($code in $results.Keys) {
    $r = $results[$code]
    $lines.Add("| $code | $($r.sentences) | $($r.matched) | $($r.agreementPercent)% | $($r.secondsPerSentence) |")
}
$lines.Add('')
if ($totalMatched -lt $totalSentences) {
    $lines.Add('Disagreeing pairs are in `disagreements/<lang>.jsonl`, source and both outputs verbatim.')
} else {
    $lines.Add('No disagreements: every recorded hypothesis was reproduced character for character.')
}

Set-Content -LiteralPath (Join-Path $out 'summary.md') -Value $lines -Encoding utf8NoBOM

Write-Host ''
Write-Host ("total  {0}/{1}  {2:N2}%  {3:N3} s/sentence" -f `
    $totalMatched, $totalSentences, $overall, ($totalSeconds / [math]::Max(1, $totalSentences))) `
    -ForegroundColor $(if ($totalMatched -eq $totalSentences) { 'Green' } else { 'Yellow' })
Write-Host "wrote  $out" -ForegroundColor DarkGray
