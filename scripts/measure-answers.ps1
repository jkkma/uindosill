<#
.SYNOPSIS
    Asks a labelled question set against a transcript through llama-server and reports every check
    a machine can make — and prints a blank where only a person can judge.

.DESCRIPTION
    Decision 6 of docs/V2-ASK-THE-TRANSCRIPT.md splits v2's tests between the suite (pure functions,
    CI) and the lab (model in the loop, a script writing to runs/). This is the lab script.

    What it does, per question in the set: builds the prompt from the transcript's segments with
    [S<n>] markers (ids are 1-based positions in segments[] — the transcript JSON carries no id
    field); builds a GBNF grammar that admits exactly the live ids, a range form, [?] and an
    abstain line, so an unresolvable citation is impossible rather than detected; asks the server;
    and checks what is mechanically checkable: every cited id resolves, ranges run forward, cited
    ranges overlap the gold ranges, adversarial questions were abstained from, and a needle —
    a synthetic segment planted into a copy of the transcript at a known position — is cited when
    only it holds the answer. It also measures the grammar's decode cost by running one question
    with and without the grammar, because the April-2024 figure (80 -> 13 tok/s) predates the
    rejection-sampling rewrite and the current cost should be a number, not a memory.

    What it deliberately does not do:

      - **recall@k is not implemented.** Tier 0 retrieval (windowed BM25) belongs in
        Parakeet.Core, and a BM25 reimplemented here in PowerShell would measure this script's
        tokenizer rather than the product's — the exact failure docs/UNPROVEN.md exists to
        prevent. The summary prints the stub honestly.
      - **Citation precision is not scored.** Whether a resolving citation actually supports the
        claim is a person's judgement; the summary carries an empty column for it.
      - **A template is not scored.** The question set's status must be 'labelled'; scoring
        placeholders would produce numbers that measure nothing.

    Before anything is asked, the set's transcript pin is checked against the transcript actually
    supplied: segment count and a SHA-256 over each segment's start, end and text. Ids are only
    meaningful against one transcript, and the wrong-transcript failure mode — same audio,
    different model, every id silently pointing at different words — has to be caught before any
    number exists, not after. This script is the canonical implementation of that hash:
    per segment, the invariant-culture '0.######' rendering of start and of end, then the text,
    each followed by one LF, all UTF-8, in order.

    -PrintPin computes and prints the pin block for a transcript and exits — the labelling
    session's helper, so the pin in questions.json is pasted rather than typed.

    The server is llama-server from an already-unpacked upstream release — run
    scripts/spike-llama-server.ps1 once on the machine first; this script does not download or
    verify archives, one script owns that.

.EXAMPLE
    .\scripts\measure-answers.ps1 -TranscriptPath runs\csb-f16-cuda\CSB384.json -PrintPin

.EXAMPLE
    .\scripts\measure-answers.ps1 -TranscriptPath runs\csb-f16-cuda\CSB384.json `
        -ModelPath D:\models\Qwen3.5-9B-Q8_0.gguf -Backend cuda

.EXAMPLE
    # The laptop: Vulkan needs the bf16 knob in the child's environment on this driver.
    .\scripts\measure-answers.ps1 -TranscriptPath runs\vulkan\sample.json -QuestionsPath my-set.json `
        -ModelPath .\Qwen3-0.6B-Q8_0.gguf -Backend vulkan -ContextSize 16384 `
        -ServerEnvironment @{ GGML_VK_DISABLE_BFLOAT16 = '1' }
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TranscriptPath,

    [string] $QuestionsPath,

    # Compute and print the transcript pin block, then exit. Needs no model and no server.
    [switch] $PrintPin,

    [string] $ModelPath,

    [ValidateSet('cuda', 'vulkan', 'cpu')]
    [string] $Backend = 'cuda',

    [string] $Release = 'b10448',

    # Where the release is already unpacked; the spike script put it there.
    [string] $ServerDirectory,

    [string] $OutputDirectory,

    [int] $ContextSize = 40960,

    [ValidateSet('on', 'off', 'auto')]
    [string] $FlashAttention = 'on',

    [string] $CacheType = 'f16',

    [int] $GpuLayers = 99,

    [int] $ReasoningBudget = 0,

    [int] $MaxAnswerTokens = 300,

    [hashtable] $ServerEnvironment = @{},

    [int] $Port = 0,

    [int] $LoadTimeoutSeconds = 900,

    [int] $RequestTimeoutSeconds = 3600,

    # Skip the with/without-grammar cost comparison (it asks one extra question).
    [switch] $SkipGrammarCost,

    # Diagnostic: build the grammar without the NOT_IN_TRANSCRIPT production, so the model must
    # answer in cited bullets (or [?]). The default grammar is decision 6's design — an abstain
    # production is part of it — but the self-test found the 0.6B model at temperature 0 taking
    # the abstain branch on all four questions, including two answered verbatim in the prompt.
    # Running with and without this switch separates "cannot find the answer" from "prefers the
    # exit", which are different failures with the same output. Adversarial questions cannot pass
    # under this switch except through [?]-only answers; read their rows accordingly.
    [switch] $NoAbstainBranch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$inv = [System.Globalization.CultureInfo]::InvariantCulture
$repo = Split-Path -Parent $PSScriptRoot
$startedAt = Get-Date

if (-not $QuestionsPath) { $QuestionsPath = Join-Path $repo 'tests/fixtures/csb384/questions.json' }

function Get-Prop {
    param($Object, [string] $Name, $Default = $null)
    $p = $Object.PSObject.Properties[$Name]
    if ($null -ne $p -and $null -ne $p.Value) { return $p.Value }
    return $Default
}

function Fmt {
    param($Value, [string] $Format = 'N0')
    if ($null -eq $Value) { return '—' }
    return ([double]$Value).ToString($Format, $inv)
}

# ── The transcript, and the canonical pin ────────────────────────────────────────────────────────

$TranscriptPath = (Resolve-Path -LiteralPath $TranscriptPath).Path
$transcript = Get-Content -LiteralPath $TranscriptPath -Raw | ConvertFrom-Json
$segments = @($transcript.segments)
if ($segments.Count -eq 0) { throw "No segments in $TranscriptPath." }

$sb = [System.Text.StringBuilder]::new()
foreach ($s in $segments) {
    $null = $sb.Append(([double]$s.start).ToString('0.######', $inv)).Append("`n")
    $null = $sb.Append(([double]$s.end).ToString('0.######', $inv)).Append("`n")
    $null = $sb.Append([string]$s.text).Append("`n")
}
$sha256 = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($sb.ToString()))
$transcriptSha = ([System.BitConverter]::ToString($sha256) -replace '-', '').ToLowerInvariant()

if ($PrintPin) {
    $pin = [ordered]@{
        source       = Get-Prop $transcript 'source'
        model        = Get-Prop $transcript 'model'
        quantisation = Get-Prop $transcript 'quantisation'
        backend      = Get-Prop $transcript 'backend'
        segments     = $segments.Count
        sha256       = $transcriptSha
    }
    Write-Host ''
    Write-Host 'The transcript pin for questions.json — paste it over the "transcript" block:' -ForegroundColor Cyan
    Write-Host ''
    Write-Host (($pin | ConvertTo-Json))
    return
}

if (-not $ModelPath) { throw 'Pass -ModelPath (or -PrintPin, which needs neither model nor server).' }
$ModelPath = (Resolve-Path -LiteralPath $ModelPath).Path

# ── The question set, held to its own pin ────────────────────────────────────────────────────────

$questionsDoc = Get-Content -LiteralPath $QuestionsPath -Raw | ConvertFrom-Json
$status = [string](Get-Prop $questionsDoc 'status' '')
if ($status -ne 'labelled') {
    throw "The question set at $QuestionsPath has status '$status'. Scoring a template would produce numbers that measure nothing; label it first (docs/V2-ASK-THE-TRANSCRIPT.md, decision 6)."
}

$pin = $questionsDoc.transcript
$pinnedCount = Get-Prop $pin 'segments'
$pinnedSha = Get-Prop $pin 'sha256'
if ([int]$pinnedCount -ne $segments.Count) {
    throw "The set pins a transcript of $pinnedCount segments; $TranscriptPath has $($segments.Count). Same audio, different segmentation — every id would point somewhere else. Stopping."
}
if ([string]$pinnedSha -ne $transcriptSha) {
    throw "The set pins transcript sha256 $pinnedSha; this transcript hashes to $transcriptSha. Not the transcript the labels were written against. Stopping."
}

$questions = @($questionsDoc.questions)
Write-Host ("question set: {0} questions, pin ok ({1} segments, sha256 {2}…)" -f $questions.Count, $segments.Count, $transcriptSha.Substring(0, 12))

if (-not $ServerDirectory) { $ServerDirectory = Join-Path $repo "runs/llama-$Release/$Backend" }
$exe = Join-Path $ServerDirectory 'llama-server.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "No llama-server.exe under $ServerDirectory. Run scripts/spike-llama-server.ps1 -Backend $Backend once on this machine first; it downloads, verifies and unpacks the release, and one script owns that."
}

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ("runs/{0}-answers-{1}" -f $startedAt.ToString('yyyyMMdd-HHmmss'), $Backend) }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# ── Prompt and grammar builders ──────────────────────────────────────────────────────────────────

function Build-PromptText {
    param($Segments)
    $b = [System.Text.StringBuilder]::new()
    for ($i = 0; $i -lt $Segments.Count; $i++) {
        $null = $b.Append('[S').Append($i + 1).Append('] ').Append(([string]$Segments[$i].text).Trim()).Append("`n")
    }
    return $b.ToString()
}

function Build-Grammar {
    param([int] $SegmentCount)
    # Exactly the live ids; a range form; [?]; and an abstain line unless -NoAbstainBranch. Bullets,
    # because decision 6's own reference answers are short labelled bullets. Free text excludes
    # brackets and newlines so a citation cannot be faked in prose.
    $ids = (1..$SegmentCount | ForEach-Object { '"' + $_ + '"' }) -join ' | '
    $root = if ($NoAbstainBranch) { 'root ::= bullet bullet? bullet? bullet? bullet? bullet?' }
            else { 'root ::= abstain | bullet bullet? bullet? bullet? bullet? bullet?' }
    $rules = @($root)
    if (-not $NoAbstainBranch) { $rules += 'abstain ::= "NOT_IN_TRANSCRIPT\n"' }
    $rules += @(
        'bullet ::= "- " text " " cite "\n"'
        'text ::= [^\[\]\n]{8,300}'
        'cite ::= "[S" num "]" | "[S" num "-S" num "]" | "[?]"'
        ('num ::= ' + $ids)
    )
    return $rules -join "`n"
}

function Normalize-Text {
    param([string] $Text)
    # The quote check's normalisation: case, punctuation and whitespace folded, letters and digits
    # kept. Defined once, here and in the suite's substring test, and it must stay the same shape.
    $t = $Text.ToLowerInvariant()
    $t = [regex]::Replace($t, '[^\p{L}\p{N}\s]', ' ')
    return ([regex]::Replace($t, '\s+', ' ')).Trim()
}

function Parse-Citations {
    param([string] $Answer)
    $cites = [System.Collections.Generic.List[object]]::new()
    foreach ($m in [regex]::Matches($Answer, '\[S(\d+)(?:-S(\d+))?\]')) {
        $from = [int]$m.Groups[1].Value
        $to = if ($m.Groups[2].Success) { [int]$m.Groups[2].Value } else { $from }
        $cites.Add([pscustomobject]@{ From = $from; To = $to })
    }
    # Returned through the pipeline, an empty list arrives as $null, whose .Count strict mode
    # refuses — reachable only by an answer with no [S<n>] citation at all, which no self-test
    # model produced until the 9B abstained with a [?]-only bullet.
    return , $cites.ToArray()
}

# ── The server (start, ask, stop) ────────────────────────────────────────────────────────────────

if ($Port -le 0) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $Port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
}
$apiKey = [guid]::NewGuid().ToString('N')
$base = "http://127.0.0.1:$Port"

# --reasoning-format none keeps every generated token in message.content. On a template that opens
# a real think block (Qwen3.5-9B; not the 0.6B, whose template emits <think></think> closed),
# --reasoning-budget 0 does not stop the thinking, the server's parser files the whole generation
# under reasoning_content — including grammar-shaped tokens, since the grammar constrains sampling
# wherever the stream happens to be — and this script reads four empty answers. With the stream in
# content, the grammar forbids think-prose from the first token, which is the design's intent.
$serverArgs = @('-m', $ModelPath, '-c', $ContextSize, '-ngl', $GpuLayers, '-fa', $FlashAttention,
    '-ctk', $CacheType, '-ctv', $CacheType, '--fit', 'off', '--jinja', '--reasoning-format', 'none',
    '--reasoning-budget', $ReasoningBudget, '--host', '127.0.0.1', '--port', $Port, '--api-key', $apiKey) |
    ForEach-Object { $s = "$_"; if ($s -match '\s') { '"' + $s + '"' } else { $s } }

$childEnv = @{}
foreach ($k in $ServerEnvironment.Keys) { $childEnv[$k] = [string]$ServerEnvironment[$k] }
if ($Backend -eq 'cuda') { $childEnv['CUDA_CACHE_DISABLE'] = '1' }

$saved = @{}
foreach ($k in $childEnv.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $childEnv[$k]) }
try {
    $proc = Start-Process -FilePath $exe -ArgumentList $serverArgs -WorkingDirectory $ServerDirectory `
        -RedirectStandardOutput (Join-Path $OutputDirectory 'server.stdout.log') `
        -RedirectStandardError (Join-Path $OutputDirectory 'server.stderr.log') -PassThru -NoNewWindow
}
finally {
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$healthy = $false
while ($sw.Elapsed.TotalSeconds -lt $LoadTimeoutSeconds) {
    if ($proc.HasExited) { break }
    try {
        if ((Invoke-WebRequest -Uri "$base/health" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop).StatusCode -eq 200) { $healthy = $true; break }
    }
    catch { }
    Start-Sleep -Milliseconds 500
}
if (-not $healthy) {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    throw "llama-server did not reach /health within $LoadTimeoutSeconds s. Its stderr is in $OutputDirectory."
}
Write-Host ("server: /health after {0} s (pid {1})" -f (Fmt $sw.Elapsed.TotalSeconds 'N1'), $proc.Id)

function Ask {
    param([string] $PromptText, [string] $QuestionText, [string] $Grammar)
    # The abstain instruction is deliberately phrased as the exception. With it phrased neutrally
    # ("if the transcript does not contain the answer, reply NOT_IN_TRANSCRIPT") the 0.6B self-test
    # model at temperature 0 abstained on all four questions, including two whose answers were
    # verbatim in the prompt — the grammar's abstain branch is cheap for a small model to prefer.
    # Whether larger models do the same is exactly what this script exists to measure.
    $messages = @(
        @{ role = 'system'; content = 'You answer questions about a transcript. The transcript usually contains the answer: find it and answer as one to six short bullets, each ending with the segment ids it rests on, like [S12] or [S12-S15], or [?] if a claim has no segment. Only when the transcript contains nothing relevant at all, reply with the single line NOT_IN_TRANSCRIPT.' },
        @{ role = 'user'; content = "Transcript:`n$PromptText`nQuestion: $QuestionText" }
    )
    $body = @{ model = 'answers'; messages = $messages; max_tokens = $MaxAnswerTokens; temperature = 0; stream = $false }
    if ($Grammar) { $body.grammar = $Grammar }
    $json = $body | ConvertTo-Json -Depth 6 -Compress
    $swReq = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-RestMethod -Method Post -Uri "$base/v1/chat/completions" -Headers @{ Authorization = "Bearer $apiKey" } `
        -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec $RequestTimeoutSeconds
    $swReq.Stop()
    $timings = Get-Prop $r 'timings'
    return [pscustomobject]@{
        Text = [string]$r.choices[0].message.content
        WallMs = [math]::Round($swReq.Elapsed.TotalMilliseconds)
        PromptTokens = if ($timings) { Get-Prop $timings 'prompt_n' } else { $null }
        PredictedPerSecond = if ($timings) { [math]::Round([double](Get-Prop $timings 'predicted_per_second' 0), 1) } else { $null }
    }
}

# ── The run ──────────────────────────────────────────────────────────────────────────────────────

$promptText = Build-PromptText $segments
$grammar = Build-Grammar $segments.Count
$results = [System.Collections.Generic.List[object]]::new()

try {
    foreach ($q in $questions) {
        $id = [string]$q.id
        $kind = [string]$q.kind
        $gold = $q.gold
        $goldRanges = @(Get-Prop $gold 'segments' @() | ForEach-Object { [pscustomobject]@{ From = [int]$_[0]; To = [int]$_[1] } })
        $goldAbstain = [bool](Get-Prop $gold 'abstain' $false)

        # A needle is asked against a copy of the transcript with the plant inserted; everything
        # else against the transcript as it is.
        $askPrompt = $promptText
        $askGrammar = $grammar
        $plantedId = $null
        if ($kind -eq 'needle') {
            $plant = Get-Prop $gold 'plant'
            if ($null -eq $plant) { throw "$id is a needle with no gold.plant." }
            $after = [int](Get-Prop $plant 'afterSegment' 0)
            $plantedList = [System.Collections.Generic.List[object]]::new()
            for ($i = 0; $i -lt $segments.Count; $i++) {
                $plantedList.Add($segments[$i])
                if (($i + 1) -eq $after) { $plantedList.Add([pscustomobject]@{ start = 0; end = 0; text = [string]$plant.text }) }
            }
            $plantedId = $after + 1
            $askPrompt = Build-PromptText $plantedList
            $askGrammar = Build-Grammar $plantedList.Count
        }

        $a = Ask -PromptText $askPrompt -QuestionText ([string]$q.question) -Grammar $askGrammar
        $cites = Parse-Citations $a.Text
        # An abstention is the abstain line, or an answer whose only citations are [?]. A template
        # that forces the think block open (Qwen3.5-9B) puts a literal <think> at the front of
        # content under --reasoning-format none, so it is stripped before the exact match — the
        # grammar forbids the model closing the tag, so it can only ever appear once, at the front.
        $answerText = ($a.Text -replace '^\s*<think>\s*', '').Trim()
        $abstained = ($answerText -eq 'NOT_IN_TRANSCRIPT') -or ($cites.Count -eq 0 -and $a.Text -match '\[\?\]')

        $limit = if ($null -ne $plantedId) { $segments.Count + 1 } else { $segments.Count }
        $allResolve = @($cites | Where-Object { $_.From -lt 1 -or $_.To -gt $limit -or $_.To -lt $_.From }).Count -eq 0
        $overlap = $false
        foreach ($c in $cites) {
            foreach ($g in $goldRanges) {
                if ($c.From -le $g.To -and $g.From -le $c.To) { $overlap = $true }
            }
        }

        $pass = switch ($kind) {
            'adversarial' { $abstained }
            'needle'      { @($cites | Where-Object { $_.From -le $plantedId -and $plantedId -le $_.To }).Count -gt 0 }
            default       { $allResolve -and $overlap }
        }

        # The label validated against the transcript it points into: the gold quote, normalised,
        # must appear in the text of the gold ranges. A false here is a labelling error, and it has
        # to be reported as one — otherwise it would surface later as a model failing the substring
        # check against a quote the transcript never contained.
        $labelQuoteOk = $null
        $goldQuote = [string](Get-Prop $gold 'quote' '')
        if ($goldQuote -and $goldRanges.Count -gt 0) {
            $spanText = ($goldRanges | ForEach-Object { for ($i = $_.From; $i -le $_.To; $i++) { [string]$segments[$i - 1].text } }) -join ' '
            $labelQuoteOk = (Normalize-Text $spanText).Contains((Normalize-Text $goldQuote))
            if (-not $labelQuoteOk) {
                Write-Host ("  {0,-5} LABEL ERROR: the gold quote is not in the gold span's text — fix the set, not the model" -f $id) -ForegroundColor Red
            }
        }

        $record = [pscustomobject]@{
            id = $id; kind = $kind
            pass = $pass; abstained = $abstained; allResolve = $allResolve; overlapsGold = $overlap
            labelQuoteOk = $labelQuoteOk
            plantedId = $plantedId
            citations = @($cites | ForEach-Object { if ($_.From -eq $_.To) { "S$($_.From)" } else { "S$($_.From)-S$($_.To)" } })
            goldSegments = @($goldRanges | ForEach-Object { "S$($_.From)-S$($_.To)" })
            predictedPerSecond = $a.PredictedPerSecond
            wallMs = $a.WallMs
            answer = $a.Text
        }
        $results.Add($record)
        Write-Host ("  {0,-5} {1,-12} {2}  cites {3}" -f $id, $kind, $(if ($pass) { 'pass' } else { 'FAIL' }), ($(if ($record.citations.Count) { ($record.citations -join ' ') } else { '(none)' })))
    }

    $grammarCost = $null
    if (-not $SkipGrammarCost) {
        $sample = $questions | Where-Object { [string]$_.kind -eq 'pointed' } | Select-Object -First 1
        if ($null -eq $sample) { $sample = $questions[0] }
        $with = Ask -PromptText $promptText -QuestionText ([string]$sample.question) -Grammar $grammar
        $without = Ask -PromptText $promptText -QuestionText ([string]$sample.question) -Grammar $null
        $grammarCost = [pscustomobject]@{
            question = [string]$sample.id
            withGrammarTokPerSec = $with.PredictedPerSecond
            withoutGrammarTokPerSec = $without.PredictedPerSecond
        }
    }
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; $proc.WaitForExit() }
}

# ── Outputs ──────────────────────────────────────────────────────────────────────────────────────

$byKind = $results | Group-Object kind
$summary = [ordered]@{
    date = $startedAt.ToString('yyyy-MM-dd HH:mm zzz')
    backend = $Backend; release = $Release
    model = [ordered]@{ path = (Split-Path -Leaf $ModelPath); bytes = (Get-Item -LiteralPath $ModelPath).Length }
    transcript = [ordered]@{ path = (Split-Path -Leaf $TranscriptPath); segments = $segments.Count; sha256 = $transcriptSha }
    questions = $questions.Count
    server = @($serverArgs | ForEach-Object { if ($_ -eq $apiKey) { '<api-key>' } elseif ($_ -eq $ModelPath) { '<model>' } else { $_ } }) -join ' '
    environment = $childEnv
    results = $results
    grammarCost = $grammarCost
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'answers.json') -Encoding utf8

$md = [System.Text.StringBuilder]::new()
$null = $md.AppendLine("### measure-answers — $($summary.date), backend $Backend, release $Release")
$null = $md.AppendLine('')
$null = $md.AppendLine("Model ``$($summary.model.path)``; transcript ``$($summary.transcript.path)`` ($($segments.Count) segments, sha256 ``$($transcriptSha.Substring(0,12))…``); $($questions.Count) questions.")
$null = $md.AppendLine('')
$null = $md.AppendLine('| Kind | Pass | Of | What a pass means |')
$null = $md.AppendLine('|---|---|---|---|')
foreach ($g in $byKind) {
    $meaning = switch ($g.Name) {
        'adversarial' { 'abstained' }
        'needle'      { 'the planted id was cited' }
        default       { 'every citation resolves and one overlaps gold' }
    }
    $null = $md.AppendLine(("| {0} | {1} | {2} | {3} |" -f $g.Name, @($g.Group | Where-Object pass).Count, $g.Count, $meaning))
}
$null = $md.AppendLine('')
if ($null -ne $grammarCost) {
    $null = $md.AppendLine(("Grammar decode cost, one question ({0}): **{1} tok/s with**, {2} tok/s without." -f $grammarCost.question, (Fmt $grammarCost.withGrammarTokPerSec 'N1'), (Fmt $grammarCost.withoutGrammarTokPerSec 'N1')))
    $null = $md.AppendLine('')
}
$null = $md.AppendLine('| id | kind | pass | citations | gold | tok/s |')
$null = $md.AppendLine('|---|---|---|---|---|---|')
foreach ($r in $results) {
    $null = $md.AppendLine(("| {0} | {1} | {2} | {3} | {4} | {5} |" -f $r.id, $r.kind, $(if ($r.pass) { 'pass' } else { '**FAIL**' }), ($(if ($r.citations.Count) { $r.citations -join ' ' } else { '—' })), ($(if ($r.goldSegments.Count) { $r.goldSegments -join ' ' } else { 'abstain' })), (Fmt $r.predictedPerSecond 'N1')))
}
$null = $md.AppendLine('')
$null = $md.AppendLine('recall@10: **not implemented** — tier 0 (windowed BM25) belongs in Parakeet.Core, and a PowerShell reimplementation here would measure this script rather than the product.')
$null = $md.AppendLine('')
$null = $md.AppendLine('Citation precision — does a resolving citation actually support its claim — is a **person''s column, and it is blank**: spot-check N answers in answers.json and record the count beside this block, labelled as a human judgement.')
$md.ToString() | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.md') -Encoding utf8

Write-Host ''
Write-Host $md.ToString()
Write-Host ("written: {0}" -f $OutputDirectory) -ForegroundColor Cyan
