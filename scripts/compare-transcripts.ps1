<#
.SYNOPSIS
    Compares two transcript JSON documents produced from the same audio and reports exactly where
    they disagree.

.DESCRIPTION
    Two backends decoding the same file do not produce identical output, and the useful question is
    not whether they differ but by how much and in what. Different floating-point kernels produce
    marginally different logits, which occasionally flips a near-tie in the token argmax and moves
    a timestamp by a frame. That is expected. A changed segment count, a word that vanishes, or a
    timestamp that moves by seconds is not.

    So the comparison is split along that line:

      segments      Segmentation runs in managed code on the CPU whatever the backend, so segment
                    boundaries should be bit-identical across backends. If they are not, the
                    difference is not a kernel difference and something else is wrong.
      word tokens   The decoded text. Differences here are real transcript differences.
      timestamps    Word times. Expected to move by a frame or so; a large move is not.
      confidences   The most sensitive surface, and the one that moves most.

    Word streams are ALIGNED, not compared by index. An earlier version of this script paired word
    i with word i and refused to report when the totals differed. That guard could not see
    insertions and deletions that cancel out — f16 against q4_k on one ten-minute file produced
    exactly 1,606 words each, so the guard passed and the script reported 727 differing tokens
    where a word-level edit distance found 50 — and docs/UNPROVEN.md records that figure as an
    artefact. Now the two word streams are aligned by word-level Levenshtein distance, so a
    dropped or added word is one edit rather than a desynchronisation of everything after it, and
    the timestamp and confidence figures are computed over the pairs the alignment actually made.

    Two token figures are reported: RAW, where the model's own tokens are compared exactly, so
    casing and punctuation count; and NORMALISED, lower-cased with everything but letters and
    digits removed — the same rule scripts/word-distance.ps1 applies. A large gap between them
    means the divergence is mostly presentation.

    The alignment is the same code the product and its tests use: src/Parakeet.Core/Text/*.cs,
    compiled here with Add-Type straight from the source tree, so this script still needs no
    build and runs anywhere pwsh does — including a container with no natives, against JSONs the
    --fake engine produced.

.EXAMPLE
    .\scripts\compare-transcripts.ps1 -Reference chunk-cpu.json -Candidate chunk-cuda.json

.EXAMPLE
    .\scripts\compare-transcripts.ps1 -Reference chunk-vulkan.json -Candidate chunk-cuda.json -ShowWords
#>

[CmdletBinding()]
param(
    # The transcript to compare against.
    [Parameter(Mandatory = $true)]
    [string] $Reference,

    # The transcript under test.
    [Parameter(Mandatory = $true)]
    [string] $Candidate,

    # Print every differing word token, not just the first forty.
    [switch] $ShowWords,

    # Print every word whose timestamp moved.
    [switch] $ShowTimestamps,

    # Timestamps closer together than this count as equal. The JSON carries milliseconds.
    [double] $TimeEpsilon = 0.0005,

    # Confidences closer together than this count as equal. The JSON carries four decimals.
    [double] $ConfidenceEpsilon = 0.00005
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The reports are pasted into public documents, so numbers are formatted the same way on every
# machine: 0.25, not 0,25 on a machine whose Windows speaks a comma-decimal language (gotcha 42).
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
[Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::InvariantCulture

# ── the alignment, shared with the product ──────────────────────────────────────────────────────
#
# One implementation, tested in tests/Parakeet.Core.Tests, used by the CLI's `wer` command and by
# this script and word-distance.ps1. Those two source files are written to compile standalone —
# BCL only, nothing else from Parakeet.Core — precisely so this works. Add-Type refuses to load a
# type name twice into one session, which is what the catch is for; the cost is that a session
# that has already loaded the types keeps the version it loaded first.

$textSources = @('WordAlignment.cs', 'TranscriptNormalizer.cs') |
    ForEach-Object { Join-Path $PSScriptRoot '..' 'src' 'Parakeet.Core' 'Text' $_ }
foreach ($source in $textSources) {
    if (-not (Test-Path -LiteralPath $source)) { throw "Alignment source not found: $source" }
}
try {
    Add-Type -Path $textSources -ErrorAction Stop | Out-Null
}
catch {
    if (-not ('Parakeet.Core.Text.WordAlignment' -as [type])) { throw }
}

function Read-Transcript {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Transcript not found: $Path"
    }

    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json

    $words = [Collections.Generic.List[object]]::new()
    $segmentIndex = 0
    foreach ($segment in @($document.segments)) {
        if ($segment.PSObject.Properties.Name -contains 'words') {
            foreach ($word in @($segment.words)) {
                $confidence = $null
                if ($word.PSObject.Properties.Name -contains 'conf') { $confidence = [double] $word.conf }
                $words.Add([PSCustomObject]@{
                    Segment    = $segmentIndex
                    Text       = [string] $word.w
                    Start      = [double] $word.start
                    End        = [double] $word.end
                    Confidence = $confidence
                })
            }
        }
        $segmentIndex++
    }

    $backend = '(not recorded)'
    if ($document.PSObject.Properties.Name -contains 'backend' -and $document.backend) {
        $backend = [string] $document.backend
    }

    return [PSCustomObject]@{
        Path     = (Resolve-Path -LiteralPath $Path).Path
        Document = $document
        Segments = @($document.segments)
        Words    = $words
        Backend  = $backend
    }
}

function Write-Heading {
    param([string] $Text)
    Write-Host ''
    Write-Host ("── $Text " + ('─' * [Math]::Max(1, 46 - $Text.Length))) -ForegroundColor Green
}

$left = Read-Transcript -Path $Reference
$right = Read-Transcript -Path $Candidate

Write-Heading 'what is being compared'
Write-Host ("reference  {0,-10} {1}" -f $left.Backend, (Split-Path -Leaf $left.Path))
Write-Host ("candidate  {0,-10} {1}" -f $right.Backend, (Split-Path -Leaf $right.Path))

if ($left.Backend -eq $right.Backend) {
    Write-Host ''
    Write-Host ("Both documents report backend '{0}'. This is a same-backend comparison." -f $left.Backend) -ForegroundColor Yellow
}

# Real-time factor is per-backend and is never comparable without naming the backend it came from.
foreach ($side in @($left, $right)) {
    $rtf = '(none)'
    if ($side.Document.PSObject.Properties.Name -contains 'realTimeFactor' -and $side.Document.realTimeFactor) {
        $rtf = "{0:N4}" -f $side.Document.realTimeFactor
    }
    Write-Host ("  {0,-10} rtf {1}" -f $side.Backend, $rtf)
}

# ── segments ────────────────────────────────────────────────────────────────────────────────────
Write-Heading 'segment boundaries'

$segmentCountMatches = $left.Segments.Count -eq $right.Segments.Count
if (-not $segmentCountMatches) {
    Write-Host ("segment count differs: {0} vs {1}" -f $left.Segments.Count, $right.Segments.Count) -ForegroundColor Red
    Write-Host 'Segmentation runs in managed code on the CPU regardless of backend, so this is not a' -ForegroundColor Red
    Write-Host 'floating-point difference. Something upstream of the decoder changed.' -ForegroundColor Red
}
else {
    $boundaryDiffs = 0
    $largestBoundaryMove = 0.0
    for ($i = 0; $i -lt $left.Segments.Count; $i++) {
        $startDelta = [Math]::Abs([double] $left.Segments[$i].start - [double] $right.Segments[$i].start)
        $endDelta = [Math]::Abs([double] $left.Segments[$i].end - [double] $right.Segments[$i].end)
        $worst = [Math]::Max($startDelta, $endDelta)
        if ($worst -gt $TimeEpsilon) {
            $boundaryDiffs++
            if ($worst -gt $largestBoundaryMove) { $largestBoundaryMove = $worst }
        }
    }

    if ($boundaryDiffs -eq 0) {
        Write-Host ("all {0} segment boundaries identical" -f $left.Segments.Count) -ForegroundColor Green
    }
    else {
        Write-Host ("{0} of {1} segment boundaries differ, largest by {2:N3} s" -f $boundaryDiffs, $left.Segments.Count, $largestBoundaryMove) -ForegroundColor Red
    }
}

# ── words ───────────────────────────────────────────────────────────────────────────────────────
Write-Heading 'words'

Write-Host ("reference  {0:N0} words" -f $left.Words.Count)
Write-Host ("candidate  {0:N0} words" -f $right.Words.Count)

[string[]] $leftTokens = @($left.Words | ForEach-Object { $_.Text })
[string[]] $rightTokens = @($right.Words | ForEach-Object { $_.Text })

$ops = [Parakeet.Core.Text.WordAlignment]::Align($leftTokens, $rightTokens)
$summary = [Parakeet.Core.Text.WordAlignment]::Summarize($ops)

[string[]] $leftNormalised = [Parakeet.Core.Text.TranscriptNormalizer]::AlphanumericTokens($leftTokens)
[string[]] $rightNormalised = [Parakeet.Core.Text.TranscriptNormalizer]::AlphanumericTokens($rightTokens)
$normalisedEdits = [Parakeet.Core.Text.WordAlignment]::Distance($leftNormalised, $rightNormalised)

$tokenDiffs = [Collections.Generic.List[object]]::new()
$timeDiffs = [Collections.Generic.List[object]]::new()
$confDiffs = [Collections.Generic.List[object]]::new()
$largestTimeMove = 0.0
$largestConfMove = 0.0
$confDeltaSum = 0.0
$missingConfidence = 0
$alignedPairs = 0
$firstDivergence = $null

foreach ($op in $ops) {
    $kind = [string] $op.Kind

    if ($kind -ne 'Match') {
        $a = if ($op.ReferenceIndex -ge 0) { $left.Words[$op.ReferenceIndex] } else { $null }
        $b = if ($op.HypothesisIndex -ge 0) { $right.Words[$op.HypothesisIndex] } else { $null }
        $at = if ($null -ne $a) { $a.Start } else { $b.Start }
        $diff = [PSCustomObject]@{
            Kind      = $kind
            Index     = $op.ReferenceIndex
            At        = $at
            Reference = if ($null -ne $a) { $a.Text } else { $null }
            Candidate = if ($null -ne $b) { $b.Text } else { $null }
        }
        $tokenDiffs.Add($diff)
        if ($null -eq $firstDivergence) { $firstDivergence = $diff }
    }

    # Timestamps and confidences are compared over the pairs the alignment made — matches and
    # substitutions. A deleted or inserted word has nothing opposite it to compare with.
    if ($kind -ne 'Match' -and $kind -ne 'Substitute') { continue }

    $alignedPairs++
    $a = $left.Words[$op.ReferenceIndex]
    $b = $right.Words[$op.HypothesisIndex]

    $startDelta = [Math]::Abs($a.Start - $b.Start)
    $endDelta = [Math]::Abs($a.End - $b.End)
    $worstTime = [Math]::Max($startDelta, $endDelta)
    if ($worstTime -gt $TimeEpsilon) {
        $timeDiffs.Add([PSCustomObject]@{
            Index     = $op.ReferenceIndex
            Word      = $a.Text
            Reference = $a.Start
            Candidate = $b.Start
            Delta     = $worstTime
        })
        if ($worstTime -gt $largestTimeMove) { $largestTimeMove = $worstTime }
    }

    if ($null -eq $a.Confidence -or $null -eq $b.Confidence) {
        if ($null -ne $a.Confidence -or $null -ne $b.Confidence) { $missingConfidence++ }
        continue
    }

    $confDelta = [Math]::Abs($a.Confidence - $b.Confidence)
    if ($confDelta -gt $ConfidenceEpsilon) {
        $confDiffs.Add([PSCustomObject]@{
            Index     = $op.ReferenceIndex
            Word      = $a.Text
            Reference = $a.Confidence
            Candidate = $b.Confidence
            Delta     = $confDelta
        })
        $confDeltaSum += $confDelta
        if ($confDelta -gt $largestConfMove) { $largestConfMove = $confDelta }
    }
}

$tokenColour = 'Green'
if ($summary.Edits -gt 0) { $tokenColour = 'Yellow' }
$referenceCount = [Math]::Max(1, $left.Words.Count)
$normalisedCount = [Math]::Max(1, $leftNormalised.Count)

Write-Host ''
Write-Host ("word edits, raw        : {0} of {1:N0} ({2:F3}%) — {3} substituted, {4} deleted, {5} inserted, {6:N0} matched" -f `
    $summary.Edits, $left.Words.Count, (100.0 * $summary.Edits / $referenceCount), `
    $summary.Substitutions, $summary.Deletions, $summary.Insertions, $summary.Matches) -ForegroundColor $tokenColour
Write-Host ("word edits, normalised : {0} of {1:N0} ({2:F3}%) — case and punctuation set aside" -f `
    $normalisedEdits, $leftNormalised.Count, (100.0 * $normalisedEdits / $normalisedCount))
Write-Host ("timestamps differing   : {0} of {1:N0} aligned pairs, largest {2:N3} s" -f $timeDiffs.Count, $alignedPairs, $largestTimeMove)

if ($confDiffs.Count -gt 0) {
    # The mean is over the words that actually differ, which is what makes it a description of the
    # disagreement rather than a number diluted by every word the two runs agreed on exactly.
    Write-Host ("confidences differing  : {0} of {1:N0} aligned pairs, mean delta {2:N4}, maximum {3:N4}" -f `
        $confDiffs.Count, $alignedPairs, ($confDeltaSum / $confDiffs.Count), $largestConfMove)
}
else {
    Write-Host ("confidences differing  : 0 of {0:N0} aligned pairs" -f $alignedPairs)
}

if ($missingConfidence -gt 0) {
    Write-Host ("{0} words carry a confidence on one side only" -f $missingConfidence) -ForegroundColor Yellow
}

if ($null -ne $firstDivergence) {
    Write-Host ''
    $what = switch ($firstDivergence.Kind) {
        'Substitute' { "'{0}' vs '{1}'" -f $firstDivergence.Reference, $firstDivergence.Candidate }
        'Delete'     { "'{0}' has nothing opposite it in the candidate" -f $firstDivergence.Reference }
        'Insert'     { "candidate adds '{0}'" -f $firstDivergence.Candidate }
    }
    $where = if ($firstDivergence.Index -ge 0) { "reference word {0}" -f $firstDivergence.Index } else { 'before the next reference word' }
    Write-Host ("first divergence at {0}: {1} (around {2:N2} s)" -f $where, $what, $firstDivergence.At) -ForegroundColor Yellow
}

if ($tokenDiffs.Count -gt 0) {
    Write-Host ''
    Write-Host 'differing tokens:'
    $show = $tokenDiffs
    if (-not $ShowWords -and $tokenDiffs.Count -gt 40) {
        $show = $tokenDiffs[0..39]
    }
    foreach ($diff in $show) {
        $index = if ($diff.Index -ge 0) { '{0,6}' -f $diff.Index } else { '     -' }
        $line = switch ($diff.Kind) {
            'Substitute' { "{0,-24} -> {1}" -f "'$($diff.Reference)'", "'$($diff.Candidate)'" }
            'Delete'     { "{0,-24} -> (deleted)" -f "'$($diff.Reference)'" }
            'Insert'     { "{0,-24} -> {1}" -f '(inserted)', "'$($diff.Candidate)'" }
        }
        Write-Host ("  {0}  {1,8:N2} s  {2}" -f $index, $diff.At, $line)
    }
    if ($show.Count -lt $tokenDiffs.Count) {
        Write-Host ("  ... {0} more (pass -ShowWords)" -f ($tokenDiffs.Count - $show.Count))
    }
}

if ($ShowTimestamps -and $timeDiffs.Count -gt 0) {
    Write-Host ''
    Write-Host 'moved timestamps:'
    foreach ($diff in $timeDiffs) {
        Write-Host ("  {0,6}  {1,-24} {2,8:N3} -> {3,8:N3}  ({4:N3} s)" -f `
            $diff.Index, "'$($diff.Word)'", $diff.Reference, $diff.Candidate, $diff.Delta)
    }
}

# ── text ────────────────────────────────────────────────────────────────────────────────────────
Write-Heading 'joined text'

$leftText = [string] $left.Document.text
$rightText = [string] $right.Document.text

if ($leftText -ceq $rightText) {
    Write-Host 'byte-identical' -ForegroundColor Green
}
else {
    Write-Host ("differs — {0:N0} vs {1:N0} characters" -f $leftText.Length, $rightText.Length) -ForegroundColor Yellow
}

Write-Heading 'verdict'

if ($segmentCountMatches -and $summary.Edits -eq 0 -and $timeDiffs.Count -eq 0 -and $confDiffs.Count -eq 0) {
    Write-Host 'The two transcripts are identical.' -ForegroundColor Green
}
elseif ($summary.Edits -eq 0) {
    Write-Host 'Same transcript, different numbers: no word changed, only timings and confidences.' -ForegroundColor Green
    Write-Host 'That is what different floating-point kernels look like.'
}
else {
    Write-Host ("{0} word edit(s) — {1} once case and punctuation are set aside. Read them above and judge whether" -f $summary.Edits, $normalisedEdits)
    Write-Host 'the transcript means something different, or whether a near-tie in the argmax landed the other way.'
    Write-Host 'This is divergence between two transcripts, not a word error rate: neither side is ground truth.' -ForegroundColor DarkGray
}
