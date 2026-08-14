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

    Word streams are aligned by index, which is exact while the two runs agree on how many words
    there are. If the counts differ the alignment is reported as broken rather than papered over:
    a single inserted word would otherwise shift every later comparison and report hundreds of
    differences that are one event.

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

    # Print every differing word token, not just the count.
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

if ($left.Words.Count -ne $right.Words.Count) {
    Write-Host ''
    Write-Host 'The word counts differ, so index alignment is not valid and the per-word figures below' -ForegroundColor Red
    Write-Host 'would be an artefact of the offset rather than a measurement. Compare the text output' -ForegroundColor Red
    Write-Host 'instead and find where the streams diverge.' -ForegroundColor Red

    $limit = [Math]::Min($left.Words.Count, $right.Words.Count)
    for ($i = 0; $i -lt $limit; $i++) {
        if ($left.Words[$i].Text -cne $right.Words[$i].Text) {
            Write-Host ''
            Write-Host ("first divergence at word {0}: '{1}' vs '{2}' (around {3:N2} s)" -f `
                $i, $left.Words[$i].Text, $right.Words[$i].Text, $left.Words[$i].Start) -ForegroundColor Red
            break
        }
    }
    return
}

$tokenDiffs = [Collections.Generic.List[object]]::new()
$timeDiffs = [Collections.Generic.List[object]]::new()
$confDiffs = [Collections.Generic.List[object]]::new()
$largestTimeMove = 0.0
$largestConfMove = 0.0
$confDeltaSum = 0.0
$missingConfidence = 0

for ($i = 0; $i -lt $left.Words.Count; $i++) {
    $a = $left.Words[$i]
    $b = $right.Words[$i]

    if ($a.Text -cne $b.Text) {
        $tokenDiffs.Add([PSCustomObject]@{
            Index = $i
            At    = $a.Start
            Reference = $a.Text
            Candidate = $b.Text
        })
    }

    $startDelta = [Math]::Abs($a.Start - $b.Start)
    $endDelta = [Math]::Abs($a.End - $b.End)
    $worstTime = [Math]::Max($startDelta, $endDelta)
    if ($worstTime -gt $TimeEpsilon) {
        $timeDiffs.Add([PSCustomObject]@{
            Index = $i
            Word  = $a.Text
            Reference = $a.Start
            Candidate = $b.Start
            Delta = $worstTime
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
            Index = $i
            Word  = $a.Text
            Reference = $a.Confidence
            Candidate = $b.Confidence
            Delta = $confDelta
        })
        $confDeltaSum += $confDelta
        if ($confDelta -gt $largestConfMove) { $largestConfMove = $confDelta }
    }
}

$tokenColour = 'Green'
if ($tokenDiffs.Count -gt 0) { $tokenColour = 'Yellow' }
Write-Host ''
Write-Host ("word tokens differing : {0} of {1:N0}" -f $tokenDiffs.Count, $left.Words.Count) -ForegroundColor $tokenColour
Write-Host ("timestamps differing  : {0} of {1:N0}, largest {2:N3} s" -f $timeDiffs.Count, $left.Words.Count, $largestTimeMove)

if ($confDiffs.Count -gt 0) {
    # The mean is over the words that actually differ, which is what makes it a description of the
    # disagreement rather than a number diluted by every word the two runs agreed on exactly.
    Write-Host ("confidences differing : {0} of {1:N0}, mean delta {2:N4}, maximum {3:N4}" -f `
        $confDiffs.Count, $left.Words.Count, ($confDeltaSum / $confDiffs.Count), $largestConfMove)
}
else {
    Write-Host ("confidences differing : 0 of {0:N0}" -f $left.Words.Count)
}

if ($missingConfidence -gt 0) {
    Write-Host ("{0} words carry a confidence on one side only" -f $missingConfidence) -ForegroundColor Yellow
}

if ($tokenDiffs.Count -gt 0) {
    Write-Host ''
    Write-Host 'differing tokens:'
    $show = $tokenDiffs
    if (-not $ShowWords -and $tokenDiffs.Count -gt 40) {
        $show = $tokenDiffs[0..39]
    }
    foreach ($diff in $show) {
        Write-Host ("  {0,6}  {1,8:N2} s  {2,-24} -> {3}" -f $diff.Index, $diff.At, "'$($diff.Reference)'", "'$($diff.Candidate)'")
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

if ($segmentCountMatches -and $tokenDiffs.Count -eq 0 -and $timeDiffs.Count -eq 0 -and $confDiffs.Count -eq 0) {
    Write-Host 'The two transcripts are identical.' -ForegroundColor Green
}
elseif ($tokenDiffs.Count -eq 0) {
    Write-Host 'Same transcript, different numbers: no word changed, only timings and confidences.' -ForegroundColor Green
    Write-Host 'That is what different floating-point kernels look like.'
}
else {
    Write-Host ("{0} word token(s) changed. Read them above and judge whether the transcript means" -f $tokenDiffs.Count)
    Write-Host 'something different, or whether a near-tie in the argmax landed the other way.'
}
