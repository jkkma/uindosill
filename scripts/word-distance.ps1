<#
.SYNOPSIS
    Word-level edit distance between transcripts, for comparing two variants of the same model.

.DESCRIPTION
    `compare-transcripts.ps1` is the right tool when two transcripts are nearly identical, and the
    wrong one when they are not. It aligns by word index, so a single insertion desynchronises every
    pair after it and each one is then counted as a difference. Its guard — refusing the per-word
    figures when the two word totals differ — catches the common case and cannot catch the case that
    matters here: insertions and deletions that cancel in the total while leaving the sequence
    misaligned. That is the likely shape whenever two quantisations of one model are compared, and
    `docs/UNPROVEN.md` records it producing 727 differences where an alignment-free measure found 50.

    This script answers the same question without assuming alignment: an exact word-level
    Levenshtein distance. It is a complement to `compare-transcripts.ps1`, not a replacement — that
    script reports segment boundaries, timestamps and confidences, and none of that is here.

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

    The distance is computed in C# via Add-Type because the matrix is large — three hours of audio
    is roughly 30,000 tokens a side, so a full DP is about 9e8 cell updates. That is seconds in C#
    and hours in PowerShell. The common prefix and suffix are trimmed first, which helps when the
    transcripts are close and does almost nothing when they are not.

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

# From a PowerShell prompt, `-Candidates a.json,b.json` binds as an array. Through `pwsh -File` it
# arrives as one string with commas in it, because the native command line has no array syntax and
# the binder has nothing to split on. Both are reasonable ways to run this, and the second failing
# with "Transcript not found: a.json,b.json" is a poor way to find that out. No transcript this
# writes has a comma in its name — outputs are named after the audio file's stem.
$Candidates = @($Candidates | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# ── the distance itself ─────────────────────────────────────────────────────────────────────────
#
# Two rows rather than the full matrix: only the distance is wanted, not the edit script, so there
# is nothing to backtrack through and O(min(n,m)) memory is enough.

$levenshteinSource = @'
using System;

public static class WordDistance
{
    public static int Distance(string[] a, string[] b)
    {
        // Near-identical transcripts share long runs at both ends. Trimming them costs one pass
        // and can turn a very large DP into a small one; on genuinely divergent input it simply
        // finds nothing and the full matrix is walked.
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;

        int endA = a.Length - 1, endB = b.Length - 1;
        while (endA >= start && endB >= start && a[endA] == b[endB]) { endA--; endB--; }

        int n = endA - start + 1, m = endB - start + 1;
        if (n <= 0) return m > 0 ? m : 0;
        if (m <= 0) return n > 0 ? n : 0;

        int[] prev = new int[m + 1];
        int[] cur = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            string ai = a[start + i - 1];
            for (int j = 1; j <= m; j++)
            {
                int cost = string.Equals(ai, b[start + j - 1], StringComparison.Ordinal) ? 0 : 1;
                int del = prev[j] + 1;
                int ins = cur[j - 1] + 1;
                int sub = prev[j - 1] + cost;
                int best = del < ins ? del : ins;
                cur[j] = best < sub ? best : sub;
            }
            int[] swap = prev; prev = cur; cur = swap;
        }
        return prev[m];
    }
}
'@

try {
    Add-Type -TypeDefinition $levenshteinSource -Language CSharp -ErrorAction Stop | Out-Null
}
catch {
    # Already added by an earlier run in this session.
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

function ConvertTo-Normalised {
    param([string[]] $Tokens)

    $out = [Collections.Generic.List[string]]::new($Tokens.Count)
    foreach ($token in $Tokens) {
        $builder = [Text.StringBuilder]::new($token.Length)
        foreach ($ch in $token.ToCharArray()) {
            if ([char]::IsLetterOrDigit($ch)) { [void] $builder.Append([char]::ToLowerInvariant($ch)) }
        }
        if ($builder.Length -gt 0) { $out.Add($builder.ToString()) }
    }
    return $out.ToArray()
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

    $rawEdits = [WordDistance]::Distance($raw, $referenceRaw)
    $normalisedEdits = [WordDistance]::Distance($normalised, $referenceNormalised)

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
