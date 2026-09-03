<#
.SYNOPSIS
    Word-level edit distance between transcripts, for comparing two variants of the same model.

.DESCRIPTION
    `compare-transcripts.ps1` used to align two transcripts by word index, so a single insertion
    desynchronised every pair after it and each one was counted as a difference; its guard —
    refusing the per-word figures when the two word totals differed — could not catch insertions
    and deletions that cancel in the total, which is the likely shape whenever two quantisations of
    one model are compared, and `docs/UNPROVEN.md` records it producing 727 differences where an
    alignment-free measure found 50. This script was written as that alignment-free measure: an
    exact word-level Levenshtein distance.

    `compare-transcripts.ps1` now aligns properly, with the same code this uses, so the two no
    longer disagree. What this one still does that it does not is the table: several candidates
    against one reference on one screen — the quantisation ladder — and it reads the `.txt`
    output as well as the JSON. What it does not do is segment boundaries, timestamps and
    confidences; that is the other script.

    Two figures are reported, because they answer different questions:

      raw          tokens compared as they appear, so casing and punctuation count as differences
      normalised   lower-cased with non-alphanumeric characters removed, so only the words count

    Both matter. A large gap between them means the divergence is mostly presentation; a small gap
    means words are genuinely changing. Neither is a word error rate — that needs a ground truth
    transcript, which this project does not have for any audio it has run. What these measure is
    divergence from whichever transcript you pass as -Reference, and two transcripts can be wrong in
    the same place and agree perfectly.

    **Read the noise floor before reading anything else.** Two runs of the *same* weights on
    different backends already differ. Measure that pairing first and treat it as the floor: a
    candidate that scores near it has not been shown to differ from the reference at all.

    Input may be transcript JSON or the .txt output. JSON is preferred and is what the figures in
    `docs/UNPROVEN.md` were computed from: it carries the model's own token stream at
    `segments[].words[].w`, whereas the .txt is the rendered form and counts differently. The mode
    used is printed, so the two are never confused.

    The distance is computed in C# because the matrix is large — three hours of audio is roughly
    30,000 tokens a side, so a full DP is about 9e8 cell updates. That is seconds in C# and hours
    in PowerShell. The code is src/Parakeet.Core/Text/WordAlignment.cs, the same implementation
    the product's `wer` command and compare-transcripts.ps1 use and the test suite covers, loaded
    here with Add-Type straight from the source tree so that this script still needs no build.
    The common prefix and suffix are trimmed first, which helps when the transcripts are close and
    does almost nothing when they are not.

.EXAMPLE
    # The noise floor: same weights, different backend.
    .\scripts\word-distance.ps1 -Reference runs\csb-f16-cpu\CSB384.json `
                                -Candidates runs\csb-f16-cuda\CSB384.json

.EXAMPLE
    # The quantisation ladder, against one f16 reference.
    .\scripts\word-distance.ps1 -Reference runs\csb-f16-cuda\CSB384.json `
                                -Candidates runs\csb-q8_0-cuda\CSB384.json,
                                            runs\csb-q6_k-cuda\CSB384.json,
                                            runs\csb-q5_k-cuda\CSB384.json,
                                            runs\csb-q4_k-cuda\CSB384.json

.EXAMPLE
    # Through the dispatcher, which forwards -Candidates but not -Candidate to this task.
    .\scripts\lab.ps1 word-distance -Reference runs\csb-f16-cuda\CSB384.json `
                                    -Candidates runs\csb-q4_k-cuda\CSB384.json
#>

[CmdletBinding()]
param(
    # The transcript everything else is measured against. Divergence is reported as a share of this
    # one's token count, so which file is the reference changes the denominator.
    [Parameter(Mandatory = $true)]
    [string] $Reference,

    # One or more transcripts to compare against the reference. Plural, and deliberately not the
    # -Candidate that compare-transcripts.ps1 takes: scripts/lab.ps1 has to declare every parameter
    # it forwards, and one name cannot be [string] for that script and [string[]] for this one.
    [Parameter(Mandatory = $true)]
    [string[]] $Candidates
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The reports are pasted into public documents, so numbers are formatted the same way on every
# machine: 0.25, not 0,25 on a machine whose Windows speaks a comma-decimal language (gotcha 42).
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
[Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::InvariantCulture

# From a PowerShell prompt, `-Candidates a.json,b.json` binds as an array. Through `pwsh -File` it
# arrives as one string with commas in it, because the native command line has no array syntax and
# the binder has nothing to split on. Both are reasonable ways to run this, and the second failing
# with "Transcript not found: a.json,b.json" is a poor way to find that out. No transcript this
# writes has a comma in its name — outputs are named after the audio file's stem.
$Candidates = @($Candidates | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# ── the distance itself ─────────────────────────────────────────────────────────────────────────
#
# WordAlignment.Distance: two rows rather than the full matrix, because only the distance is wanted
# here, not the edit script. The source files are written to compile standalone — BCL only, nothing
# else from Parakeet.Core — precisely so this works. Add-Type refuses to load a type name twice
# into one session, which is what the catch is for; the cost is that a session that has already
# loaded the types keeps the version it loaded first.

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

# ── reading transcripts ─────────────────────────────────────────────────────────────────────────

function Get-Tokens {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Transcript not found: $Path"
    }

    $extension = [IO.Path]::GetExtension($Path).ToLowerInvariant()

    if ($extension -eq '.json') {
        $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        $tokens = [Collections.Generic.List[string]]::new()

        foreach ($segment in @($document.segments)) {
            if ($segment.PSObject.Properties.Name -contains 'words') {
                foreach ($word in @($segment.words)) {
                    $tokens.Add([string] $word.w)
                }
            }
        }

        if ($tokens.Count -eq 0) {
            throw "No words in $Path. A transcript written without word timings cannot be compared here."
        }

        return [PSCustomObject]@{ Mode = 'json'; Tokens = $tokens.ToArray() }
    }

    # The .txt output is "[hh:mm:ss] words...". The timestamp is not part of the transcript and
    # would otherwise be counted as one token per segment line.
    $text = Get-Content -LiteralPath $Path -Raw
    $text = [Text.RegularExpressions.Regex]::Replace($text, '(?m)^\[\d{2}:\d{2}:\d{2}\]\s*', '')
    $tokens = @($text -split '\s+' | Where-Object { $_ })

    if ($tokens.Count -eq 0) { throw "No words in $Path." }

    return [PSCustomObject]@{ Mode = 'text'; Tokens = $tokens }
}

# Lower-cased, letters and digits only, empties dropped — TranscriptNormalizer.AlphanumericTokens,
# which is this script's original rule moved into the shared source unchanged, so every figure
# docs/UNPROVEN.md quotes from here still reproduces.
function ConvertTo-Normalised {
    param([string[]] $Tokens)
    return [string[]] [Parakeet.Core.Text.TranscriptNormalizer]::AlphanumericTokens($Tokens)
}

# ── report ──────────────────────────────────────────────────────────────────────────────────────

$referenceRead = Get-Tokens -Path $Reference
$referenceRaw = $referenceRead.Tokens
$referenceNormalised = ConvertTo-Normalised -Tokens $referenceRaw

Write-Host ''
Write-Host ("reference  {0}" -f (Resolve-Path -LiteralPath $Reference).Path) -ForegroundColor Cyan
Write-Host ("           read as {0}, {1:N0} tokens ({2:N0} normalised)" -f
    $referenceRead.Mode, $referenceRaw.Count, $referenceNormalised.Count)
Write-Host ''
Write-Host ("{0,-34} {1,10} {2,10} {3,9} {4,10} {5,9}" -f
    'candidate', 'tokens', 'raw', 'raw %', 'normalised', 'norm %')

foreach ($path in $Candidates) {
    $read = Get-Tokens -Path $path
    $raw = $read.Tokens
    $normalised = ConvertTo-Normalised -Tokens $raw

    if ($read.Mode -ne $referenceRead.Mode) {
        Write-Host ''
        Write-Host ("  $path was read as $($read.Mode) and the reference as $($referenceRead.Mode). " +
                    "The two forms tokenise differently, so this comparison would not mean anything.") -ForegroundColor Red
        continue
    }

    $rawEdits = [Parakeet.Core.Text.WordAlignment]::Distance([string[]] $referenceRaw, [string[]] $raw)
    $normalisedEdits = [Parakeet.Core.Text.WordAlignment]::Distance([string[]] $referenceNormalised, [string[]] $normalised)

    $rawShare = 100.0 * $rawEdits / $referenceRaw.Count
    $normalisedShare = 100.0 * $normalisedEdits / $referenceNormalised.Count

    # Long paths are the norm here (runs/<something>/<stem>.json); the leaf alone is usually the
    # same name for every candidate, so show the directory that distinguishes them.
    $label = Join-Path (Split-Path -Leaf (Split-Path -Parent $path)) (Split-Path -Leaf $path)
    if ($label.Length -gt 34) { $label = $label.Substring($label.Length - 34) }

    Write-Host ("{0,-34} {1,10:N0} {2,10:N0} {3,8:F3}% {4,10:N0} {5,8:F3}%" -f
        $label, $raw.Count, $rawEdits, $rawShare, $normalisedEdits, $normalisedShare)
}

Write-Host ''
Write-Host '  Divergence from the reference, not a word error rate: there is no ground truth here and' -ForegroundColor DarkGray
Write-Host '  both transcripts can be wrong in the same place. Compare against the same-weights,' -ForegroundColor DarkGray
Write-Host '  different-backend pairing first — that is the floor any other figure has to clear.' -ForegroundColor DarkGray
Write-Host ''
