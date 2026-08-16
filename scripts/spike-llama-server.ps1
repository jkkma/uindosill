<#
.SYNOPSIS
    Runs the llama-server spike that decision 1 of docs/V2-ASK-THE-TRANSCRIPT.md asks for, and
    prints the block each document should say afterwards.

.DESCRIPTION
    Nothing under docs/V2-ASK-THE-TRANSCRIPT.md has been run against a language model. This is the
    one sitting that changes that, made mechanical so that the machine it runs on decides nothing:
    it fetches the pinned upstream llama.cpp release for the backend, verifies what it can against
    the digests already recorded, unpacks it flat, reads the compiled GPU architectures out of the
    CUDA backend with vendor-cuda.ps1, starts llama-server as a child on 127.0.0.1 with a random
    port and api-key, waits for /health, prefills a transcript, asks one question, stops the child,
    starts it a second time, and samples GPU memory at every phase.

    Four things it measures that no document here has yet:

      1. Whether the CUDA 13.3 build's sm_120 cubins are what an RTX 5080 actually runs. The scan
         is a header walk; corroboration is a run. With CUDA_CACHE_DISABLE=1 in the child's
         environment the driver's JIT cache is off, so a PTX-only backend would JIT on every
         start and a cubin backend would not care: two starts that both reach /health in seconds
         are the evidence. That is the check docs/NATIVE-BINARIES.md made for parakeet.dll, made
         sharper.
      2. GPU memory, per process and per adapter, at each phase — idle, loaded, after the prefill,
         after an answer, after exit, loaded again — through the WDDM performance counters
         (\GPU Adapter Memory, \GPU Process Memory(pid_*)), which are vendor-neutral, and through
         nvidia-smi where it exists. Decision 4 has never had a VRAM figure.
      3. What the desktop holds before any model loads, which is the term every "fits" in
         decision 4 depends on.
      4. Prefill and decode timings for the file under test, at the context length the feature
         needs, on the backend the desktop tier will ship.

    It does not build anything, does not touch native/, writes only under runs/ (gitignored), and
    prints its results as a Markdown block plus a JSON file so the numbers travel into the docs by
    copying rather than by memory. The block withholds the hostname, as the docs do.

    What it cannot do from the machine it was written on: the CUDA branch. Written and run on the
    second machine on 2026-08-16 (Radeon 880M; cpu and vulkan, with Qwen3-0.6B-Q8_0 and a 7,779-token
    stand-in prompt), so the cuda-specific paths — the cudart zip, the JIT-cache environment,
    nvidia-smi — parse and are reasoned about but are first exercised on the desktop, not here. If
    something in them is wrong, the run says so before any number is printed. What the two runs
    here did establish: the whole sequence works end to end; the per-process counter sees the
    server's memory (2,456.8 MiB dedicated for that model on Vulkan) and the adapter figure returns
    to idle after the kill; and upstream Vulkan on the second machine's 2025-01-22 driver does not
    load a model without GGML_VK_DISABLE_BFLOAT16=1 — `vkDestroyFence: Invalid device`, then a hang
    that the load timeout catches — and loads in 2.6 s with it. That is gotcha 21's knob and
    docs/UNPROVEN.md's bf16 mechanism, seen on llama.cpp rather than parakeet.cpp.

    Digests: the cuda-13.3 library zip's SHA-256 is recorded (read on 2026-08-16); a mismatch fails
    the run. Archives whose digest is not yet recorded — the cudart zip, the vulkan and cpu zips —
    are printed as a first reading and the run continues; that is the point of a spike. Byte
    counts from the releases API are checked for all four.

.PARAMETER ModelPath
    The GGUF to load. Not downloaded here — the first file is 9.5 GB and the pinning procedure in
    docs/MODELS.md is the right way to fetch it. Its size and SHA-256 are printed for the record;
    pass -ExpectedModelSha256 to have them checked.

.PARAMETER PromptFile
    Plain text to prefill — CSB384's transcript is the one the doc is sized for. Without it a
    short built-in paragraph is used and the summary says so; the mechanics run, the numbers do
    not mean much.

.PARAMETER Backend
    cuda (the desktop tier), vulkan (the portable default and the laptop), or cpu.

.PARAMETER FlashAttention
    Passed as -fa. Decision 2's third file runs with it off while llama.cpp #26609 stands.

.PARAMETER CacheType
    Passed as -ctk and -ctv. f16 by default; q8_0 needs flash attention on.

.PARAMETER ExtraServerArgs
    Anything else for llama-server, verbatim — for example -ot, exps=CPU for the third file.

.PARAMETER ServerEnvironment
    Environment variables set for the child only — for example @{ GGML_VK_DISABLE_BFLOAT16 = '1' }
    on the laptop. CUDA_CACHE_DISABLE=1 is added on the cuda backend unless -KeepJitCache.

.EXAMPLE
    .\scripts\spike-llama-server.ps1 -ModelPath D:\models\Qwen3.5-9B-Q8_0.gguf -PromptFile CSB384.txt

.EXAMPLE
    # The laptop: Vulkan, a small model, and the bf16 knob the second machine's driver needs.
    .\scripts\spike-llama-server.ps1 -Backend vulkan -ModelPath .\Qwen3-0.6B-Q8_0.gguf `
        -PromptFile .\prompt.txt -ServerEnvironment @{ GGML_VK_DISABLE_BFLOAT16 = '1' }

.EXAMPLE
    # The third file: experts in RAM, flash attention off while #26609 stands.
    .\scripts\spike-llama-server.ps1 -ModelPath D:\models\Qwen3.6-35B-A3B-UD-IQ4_XS.gguf `
        -PromptFile CSB384.txt -FlashAttention off -ExtraServerArgs '-ot','exps=CPU'
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ModelPath,

    [string] $PromptFile,

    [string] $Question = 'In two sentences, what is this transcript about? Cite segment ids in square brackets, like [S12].',

    [ValidateSet('cuda', 'vulkan', 'cpu')]
    [string] $Backend = 'cuda',

    # The upstream release tag. b10448 is the one every reading in the doc was taken from.
    [string] $Release = 'b10448',

    # Which CUDA toolkit build of that release. 13.3 is the one with sm_120 cubins; 12.4 has none.
    [string] $CudaVersion = '13.3',

    # Where the release zips are kept between runs. Defaults to runs/llama-<release>/archives.
    [string] $ArchiveDirectory,

    # Where the release is unpacked, flat. Defaults to runs/llama-<release>/<backend>.
    [string] $Destination,

    # Where this run's logs, samples and summary go. Defaults to runs/<timestamp>-spike-<backend>.
    [string] $OutputDirectory,

    [int] $ContextSize = 40960,

    [ValidateSet('on', 'off', 'auto')]
    [string] $FlashAttention = 'on',

    [string] $CacheType = 'f16',

    [int] $GpuLayers = 99,

    # Passed as --reasoning-budget. 0 ends thinking immediately, so timings measure the answer.
    [int] $ReasoningBudget = 0,

    [string[]] $ExtraServerArgs = @(),

    [hashtable] $ServerEnvironment = @{},

    # 0 picks a free loopback port.
    [int] $Port = 0,

    [int] $LoadTimeoutSeconds = 900,

    # Prefill of a three-hour transcript with experts in RAM can take minutes; this is the ceiling.
    [int] $RequestTimeoutSeconds = 3600,

    [string] $ExpectedModelSha256,

    [switch] $SkipDownload,
    [switch] $SkipScan,
    [switch] $SkipSecondStart,
    [switch] $SkipModelHash,
    [switch] $KeepJitCache
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$inv = [System.Globalization.CultureInfo]::InvariantCulture
$repo = Split-Path -Parent $PSScriptRoot
$startedAt = Get-Date

if (-not $ArchiveDirectory) { $ArchiveDirectory = Join-Path $repo "runs/llama-$Release/archives" }
if (-not $Destination)      { $Destination      = Join-Path $repo "runs/llama-$Release/$Backend" }
if (-not $OutputDirectory)  { $OutputDirectory  = Join-Path $repo ("runs/{0}-spike-{1}" -f $startedAt.ToString('yyyyMMdd-HHmmss'), $Backend) }

foreach ($dir in @($ArchiveDirectory, $Destination, $OutputDirectory)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$ModelPath = (Resolve-Path -LiteralPath $ModelPath).Path

function Write-Heading {
    param([string] $Text)
    Write-Host ''
    Write-Host ("── $Text " + ('─' * [Math]::Max(1, 60 - $Text.Length))) -ForegroundColor Green
}

function Fmt {
    # Every number that might be pasted into a document goes through the invariant culture: this
    # machine's locale writes 2.548,4 and the docs write 2,548.4.
    param($Value, [string] $Format = 'N0')
    if ($null -eq $Value) { return '—' }
    return ([double]$Value).ToString($Format, $inv)
}

# ── What upstream ships for this release, as read from the releases API on 2026-08-16 ────────────
#
# Byte counts are checked for every archive. The digest is recorded for the one archive that has
# been scanned; the others are first readings and are printed as such. Add rows here when a run
# on any machine has produced them.

$known = @{
    'llama-b10448-bin-win-cuda-13.3-x64.zip'   = @{ Bytes = 146699660; Sha256 = '56bef9038109ccae82e1c3843d400d6ca51aee406649a69c206769c8cbc7c89c' }
    'cudart-llama-bin-win-cuda-13.3-x64.zip'   = @{ Bytes = 390970417; Sha256 = $null }
    'llama-b10448-bin-win-cuda-12.4-x64.zip'   = @{ Bytes = 250791166; Sha256 = $null }
    'cudart-llama-bin-win-cuda-12.4-x64.zip'   = @{ Bytes = 391443627; Sha256 = $null }
    'llama-b10448-bin-win-vulkan-x64.zip'      = @{ Bytes = 34807759;  Sha256 = $null }
    'llama-b10448-bin-win-cpu-x64.zip'         = @{ Bytes = 18464245;  Sha256 = $null }
}

$archives = switch ($Backend) {
    'cuda'   { @("llama-$Release-bin-win-cuda-$CudaVersion-x64.zip", "cudart-llama-bin-win-cuda-$CudaVersion-x64.zip") }
    'vulkan' { @("llama-$Release-bin-win-vulkan-x64.zip") }
    'cpu'    { @("llama-$Release-bin-win-cpu-x64.zip") }
}

$summary = [ordered]@{
    date            = $startedAt.ToString('yyyy-MM-dd HH:mm zzz')
    backend         = $Backend
    release         = $Release
    archives        = @()
    model           = [ordered]@{ path = $ModelPath; bytes = (Get-Item -LiteralPath $ModelPath).Length; sha256 = $null }
    gpu             = [ordered]@{ name = $null; driver = $null; cudaVersion = $null; totalMiB = $null; nvidiaSmi = $false }
    cudart          = @()
    server          = [ordered]@{ exe = $null; args = @(); environment = @{}; port = $null }
    samples         = @()
    starts          = @()
    prefill         = $null
    answer          = $null
    logLines        = @()
    promptSource    = $null
    notes           = @()
}

# ── Archives ─────────────────────────────────────────────────────────────────────────────────────

Write-Heading "release $Release, backend $Backend"

foreach ($name in $archives) {
    $path = Join-Path $ArchiveDirectory $name
    $url = "https://github.com/ggml-org/llama.cpp/releases/download/$Release/$name"
    $expected = if ($known.ContainsKey($name)) { $known[$name] } else { $null }

    if (-not $SkipDownload) {
        $have = (Test-Path -LiteralPath $path) -and $expected -and ((Get-Item -LiteralPath $path).Length -eq $expected.Bytes)
        if (-not $have) {
            Write-Host "  downloading $name"
            & curl.exe -sSL --fail -o $path $url
            if ($LASTEXITCODE -ne 0) { throw "curl exit $LASTEXITCODE fetching $url" }
        }
        else {
            Write-Host "  have        $name"
        }
    }

    if (-not (Test-Path -LiteralPath $path)) { throw "Missing $path (pass without -SkipDownload to fetch it)." }

    $bytes = (Get-Item -LiteralPath $path).Length
    $sha = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $status = 'first reading — no digest recorded for this archive yet'
    if ($expected) {
        if ($bytes -ne $expected.Bytes) {
            throw "$name is $bytes bytes; the releases API said $($expected.Bytes). Not the archive upstream serves — stopping."
        }
        if ($expected.Sha256) {
            if ($sha -ne $expected.Sha256) { throw "$name hashed to $sha; recorded $($expected.Sha256). Stopping." }
            $status = 'matches the recorded digest'
        }
    }
    else {
        $status = 'first reading — neither size nor digest recorded for this archive'
    }
    Write-Host ("  {0}  {1} bytes  sha256 {2}" -f $name, (Fmt $bytes), $sha)
    Write-Host ("  {0,-40}{1}" -f '', $status) -ForegroundColor DarkGray
    $summary.archives += [ordered]@{ name = $name; bytes = $bytes; sha256 = $sha; status = $status }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($path, $Destination, $true)
}

# Flatten: the loader and the scan both want every DLL beside llama-server.exe. The library zips
# are flat already; the cudart zip's layout was not read before this ran, so a nested file is
# moved up rather than assumed absent.
$nested = @(Get-ChildItem -LiteralPath $Destination -Recurse -File | Where-Object { $_.DirectoryName -ne (Resolve-Path -LiteralPath $Destination).Path })
foreach ($file in $nested) {
    $target = Join-Path $Destination $file.Name
    if (-not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $file.FullName -Destination $target }
}

$exe = Join-Path $Destination 'llama-server.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "No llama-server.exe under $Destination after unpacking." }
$summary.server.exe = $exe

$files = @(Get-ChildItem -LiteralPath $Destination -File)
Write-Host ("  {0} files unpacked under {1}" -f $files.Count, $Destination)

foreach ($dll in ($files | Where-Object { $_.Name -like 'cudart64_*.dll' })) {
    $version = if ($dll.VersionInfo -and $dll.VersionInfo.FileVersion) { $dll.VersionInfo.FileVersion.Trim() } else { 'no version resource' }
    Write-Host ("  {0,-24} file version {1}" -f $dll.Name, $version)
    $summary.cudart += [ordered]@{ name = $dll.Name; bytes = $dll.Length; fileVersion = $version }
}

# ── Compiled GPU architectures ───────────────────────────────────────────────────────────────────

if ($Backend -eq 'cuda' -and -not $SkipScan) {
    Write-Heading 'compiled GPU architectures, read out of ggml-cuda.dll'
    Write-Host '  (vendor-cuda.ps1 -InspectOnly; its headings say native/win-x64/cuda but it is reading this directory)' -ForegroundColor DarkGray
    & (Join-Path $PSScriptRoot 'vendor-cuda.ps1') -InspectOnly -Destination $Destination
}

# ── The model ────────────────────────────────────────────────────────────────────────────────────

Write-Heading 'model'
Write-Host ("  {0}" -f $ModelPath)
Write-Host ("  {0} bytes" -f (Fmt $summary.model.bytes))
if (-not $SkipModelHash) {
    $summary.model.sha256 = (Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("  sha256 {0}" -f $summary.model.sha256)
    if ($ExpectedModelSha256 -and $summary.model.sha256 -ne $ExpectedModelSha256.ToLowerInvariant()) {
        throw "Model hashed to $($summary.model.sha256); expected $ExpectedModelSha256. Stopping."
    }
}

# ── GPU: what is there, and how it is sampled ────────────────────────────────────────────────────

$nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($nvidiaSmi) {
    $summary.gpu.nvidiaSmi = $true
    try {
        $row = (& nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits | Select-Object -First 1)
        $parts = $row -split ',\s*'
        $summary.gpu.name = $parts[0]; $summary.gpu.driver = $parts[1]; $summary.gpu.totalMiB = [int]$parts[2]
        $header = (& nvidia-smi | Out-String)
        if ($header -match 'CUDA Version:\s*([\d.]+)') { $summary.gpu.cudaVersion = $Matches[1] }
    }
    catch {
        $summary.notes += "nvidia-smi is present but its query failed: $($_.Exception.Message)"
    }
}
else {
    $summary.notes += 'nvidia-smi not on PATH; per-adapter and per-process figures come from the WDDM counters only'
}

function Get-GpuSample {
    param([string] $Phase, [int] $ServerPid = 0)
    $s = [ordered]@{
        phase = $Phase; at = (Get-Date).ToString('HH:mm:ss')
        adapterDedicatedMiB = $null; adapterSharedMiB = $null
        serverDedicatedMiB = $null; serverSharedMiB = $null
        nvidiaSmiUsedMiB = $null
    }
    try {
        $c = Get-Counter '\GPU Adapter Memory(*)\Dedicated Usage', '\GPU Adapter Memory(*)\Shared Usage' -ErrorAction Stop
        $s.adapterDedicatedMiB = [math]::Round((($c.CounterSamples | Where-Object { $_.Path -like '*dedicated usage' } | Measure-Object CookedValue -Sum).Sum) / 1MB, 1)
        $s.adapterSharedMiB    = [math]::Round((($c.CounterSamples | Where-Object { $_.Path -like '*shared usage' }    | Measure-Object CookedValue -Sum).Sum) / 1MB, 1)
    }
    catch { }
    if ($ServerPid -gt 0) {
        try {
            $p = Get-Counter "\GPU Process Memory(pid_${ServerPid}_*)\Dedicated Usage", "\GPU Process Memory(pid_${ServerPid}_*)\Shared Usage" -ErrorAction Stop
            $s.serverDedicatedMiB = [math]::Round((($p.CounterSamples | Where-Object { $_.Path -like '*dedicated usage' } | Measure-Object CookedValue -Sum).Sum) / 1MB, 1)
            $s.serverSharedMiB    = [math]::Round((($p.CounterSamples | Where-Object { $_.Path -like '*shared usage' }    | Measure-Object CookedValue -Sum).Sum) / 1MB, 1)
        }
        catch { }
    }
    if ($nvidiaSmi) {
        try { $s.nvidiaSmiUsedMiB = [int](& nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits | Select-Object -First 1) } catch { }
    }
    $obj = [pscustomobject]$s
    $script:summary.samples += $obj
    Write-Host ("  {0,-22} adapter dedicated {1,9} MiB  shared {2,8} MiB   server dedicated {3,9} MiB  shared {4,8} MiB   nvidia-smi used {5,8} MiB" -f `
        $Phase, (Fmt $obj.adapterDedicatedMiB 'N1'), (Fmt $obj.adapterSharedMiB 'N1'), (Fmt $obj.serverDedicatedMiB 'N1'), (Fmt $obj.serverSharedMiB 'N1'), (Fmt $obj.nvidiaSmiUsedMiB))
    return $obj
}

Write-Heading 'GPU memory, before anything loads'
if ($summary.gpu.nvidiaSmi) {
    Write-Host ("  {0}, driver {1}, CUDA version {2}, {3} MiB" -f $summary.gpu.name, $summary.gpu.driver, $summary.gpu.cudaVersion, (Fmt $summary.gpu.totalMiB))
}
$null = Get-GpuSample 'idle'

# ── The server ───────────────────────────────────────────────────────────────────────────────────

if ($Port -le 0) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $Port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
}
$apiKey = [guid]::NewGuid().ToString('N')
$base = "http://127.0.0.1:$Port"
$summary.server.port = $Port

$serverArgs = @(
    '-m', $ModelPath,
    '-c', $ContextSize,
    '-ngl', $GpuLayers,
    '-fa', $FlashAttention,
    '-ctk', $CacheType, '-ctv', $CacheType,
    '--fit', 'off',
    '--jinja',
    '--reasoning-budget', $ReasoningBudget,
    '--host', '127.0.0.1', '--port', $Port,
    '--api-key', $apiKey
) + $ExtraServerArgs
$summary.server.args = @($serverArgs | ForEach-Object { if ($_ -eq $apiKey) { '<api-key>' } else { "$_" } })

# Start-Process joins -ArgumentList with spaces and quotes nothing, so a path with a space in it
# arrives as two arguments. Quote anything that needs it here, once.
$serverArgsForStart = @($serverArgs | ForEach-Object { $s = "$_"; if ($s -match '\s') { '"' + $s + '"' } else { $s } })

$childEnv = @{}
foreach ($k in $ServerEnvironment.Keys) { $childEnv[$k] = [string]$ServerEnvironment[$k] }
if ($Backend -eq 'cuda' -and -not $KeepJitCache) { $childEnv['CUDA_CACHE_DISABLE'] = '1' }
$summary.server.environment = $childEnv

function Start-Server {
    param([int] $Attempt)
    $out = Join-Path $OutputDirectory ("server-{0}.stdout.log" -f $Attempt)
    $err = Join-Path $OutputDirectory ("server-{0}.stderr.log" -f $Attempt)

    # Environment for the child only: set, start, restore. Start-Process inherits the current
    # process environment and offers no per-child override, so this is the mechanism.
    $saved = @{}
    foreach ($k in $childEnv.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $childEnv[$k]) }
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $proc = Start-Process -FilePath $exe -ArgumentList $serverArgsForStart -WorkingDirectory $Destination `
            -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -NoNewWindow
    }
    finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }

    $healthy = $false
    while ($sw.Elapsed.TotalSeconds -lt $LoadTimeoutSeconds) {
        if ($proc.HasExited) { break }
        try {
            $r = Invoke-WebRequest -Uri "$base/health" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
            if ($r.StatusCode -eq 200) { $healthy = $true; break }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }
    $sw.Stop()

    if (-not $healthy) {
        $tail = if (Test-Path -LiteralPath $err) { (Get-Content -LiteralPath $err -Tail 40) -join "`n" } else { '(no stderr)' }
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
        throw ("llama-server did not reach /health within {0} s (attempt {1}). Last lines of stderr:`n{2}" -f $LoadTimeoutSeconds, $Attempt, $tail)
    }

    $record = [ordered]@{ attempt = $Attempt; pid = $proc.Id; secondsToHealth = [math]::Round($sw.Elapsed.TotalSeconds, 2); stderr = $err }
    $script:summary.starts += $record
    Write-Host ("  start {0}: /health ok after {1} s (pid {2})" -f $Attempt, (Fmt $record.secondsToHealth 'N2'), $proc.Id)
    return $proc
}

function Stop-Server {
    param($Proc)
    if ($Proc -and -not $Proc.HasExited) {
        Stop-Process -Id $Proc.Id -Force
        $Proc.WaitForExit()
    }
}

function Invoke-Chat {
    param([object[]] $Messages, [int] $MaxTokens)
    $body = @{ model = 'spike'; messages = $Messages; max_tokens = $MaxTokens; temperature = 0; stream = $false } | ConvertTo-Json -Depth 6 -Compress
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-RestMethod -Method Post -Uri "$base/v1/chat/completions" -Headers @{ Authorization = "Bearer $apiKey" } `
        -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec $RequestTimeoutSeconds
    $sw.Stop()

    $result = [ordered]@{
        wallMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 0)
        promptTokens = $null; completionTokens = $null
        promptMs = $null; promptPerSecond = $null; predictedMs = $null; predictedPerSecond = $null
        text = $null
    }
    if ($r.PSObject.Properties['usage']) {
        $result.promptTokens = $r.usage.prompt_tokens; $result.completionTokens = $r.usage.completion_tokens
    }
    if ($r.PSObject.Properties['timings']) {
        $t = $r.timings
        foreach ($pair in @(@('prompt_ms', 'promptMs'), @('prompt_per_second', 'promptPerSecond'), @('predicted_ms', 'predictedMs'), @('predicted_per_second', 'predictedPerSecond'))) {
            if ($t.PSObject.Properties[$pair[0]]) { $result[$pair[1]] = [math]::Round([double]$t.($pair[0]), 1) }
        }
        if ($null -eq $result.promptTokens -and $t.PSObject.Properties['prompt_n']) { $result.promptTokens = $t.prompt_n }
        if ($null -eq $result.completionTokens -and $t.PSObject.Properties['predicted_n']) { $result.completionTokens = $t.predicted_n }
    }
    if ($r.PSObject.Properties['choices'] -and $r.choices.Count -gt 0) {
        $m = $r.choices[0].message
        if ($m.PSObject.Properties['content']) { $result.text = [string]$m.content }
    }
    return $result
}

# The prompt: the transcript, or a stand-in that keeps the mechanics honest about being a stand-in.
if ($PromptFile) {
    $transcript = Get-Content -LiteralPath $PromptFile -Raw
    # The leaf and the size, not the path: the block is meant to be pasted into a public document.
    $summary.promptSource = ("``{0}``, {1} bytes" -f (Split-Path -Leaf $PromptFile), (Fmt (Get-Item -LiteralPath $PromptFile).Length))
}
else {
    $transcript = "[S1] This is a stand-in transcript used to exercise the spike script. [S2] It has three segments and says nothing about the feature. [S3] Pass -PromptFile to prefill a real one."
    $summary.promptSource = '(built-in stand-in; pass -PromptFile)'
    $summary.notes += 'no -PromptFile: the prefill below is a three-segment stand-in and its timings mean nothing'
}
$messages = @(
    @{ role = 'system'; content = 'You answer questions about a transcript. Every claim cites the segment ids it rests on, in square brackets. If the transcript does not say, say so.' },
    @{ role = 'user';   content = "Transcript:`n$transcript`n`nQuestion: $Question" }
)

Write-Heading 'first start'
$proc = Start-Server -Attempt 1
try {
    $null = Get-GpuSample 'loaded' $proc.Id

    Write-Heading 'prefill'
    $summary.prefill = Invoke-Chat -Messages $messages -MaxTokens 8
    Write-Host ("  {0} prompt tokens in {1} ms wall; server timings: prompt {2} ms ({3} tok/s)" -f `
        (Fmt $summary.prefill.promptTokens), (Fmt $summary.prefill.wallMs), (Fmt $summary.prefill.promptMs 'N1'), (Fmt $summary.prefill.promptPerSecond 'N1'))
    $null = Get-GpuSample 'after prefill' $proc.Id

    Write-Heading 'answer'
    $summary.answer = Invoke-Chat -Messages $messages -MaxTokens 160
    Write-Host ("  {0} prompt tokens (cache reuse shows here), {1} generated; server timings: prompt {2} ms, predicted {3} ms ({4} tok/s)" -f `
        (Fmt $summary.answer.promptTokens), (Fmt $summary.answer.completionTokens), (Fmt $summary.answer.promptMs 'N1'), (Fmt $summary.answer.predictedMs 'N1'), (Fmt $summary.answer.predictedPerSecond 'N1'))
    if ($summary.answer.text) {
        $preview = $summary.answer.text -replace '\s+', ' '
        if ($preview.Length -gt 400) { $preview = $preview.Substring(0, 400) + '…' }
        Write-Host ("  > {0}" -f $preview) -ForegroundColor DarkGray
    }
    $null = Get-GpuSample 'after answer' $proc.Id
}
finally {
    Stop-Server $proc
}
Start-Sleep -Milliseconds 1500
$null = Get-GpuSample 'after exit'

if (-not $SkipSecondStart) {
    Write-Heading 'second start (the JIT check: same environment, cache still disabled on cuda)'
    # A failure here must not lose the first start's numbers: note it and go on to the outputs.
    try {
        $proc2 = Start-Server -Attempt 2
        try { $null = Get-GpuSample 'loaded again' $proc2.Id } finally { Stop-Server $proc2 }
    }
    catch {
        $summary.notes += "second start failed: $($_.Exception.Message)"
        Write-Host "  second start failed: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# ── What the server said about itself ────────────────────────────────────────────────────────────

$firstErr = $summary.starts[0].stderr
if (Test-Path -LiteralPath $firstErr) {
    $pattern = 'ggml_cuda_init|ggml_vulkan|compute capability|Device 0|offloaded|model buffer size|KV buffer size|compute buffer size|flash_attn|n_ctx_seq|n_ctx =|load_tensors: +\w|llama_model_load|system_info|graph splits|fit:|--fit|warning|error|failed'
    $summary.logLines = @(Get-Content -LiteralPath $firstErr | Where-Object { $_ -match $pattern } | Select-Object -First 60)
}

# ── Outputs ──────────────────────────────────────────────────────────────────────────────────────

$summary.samples | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'samples.csv') -NoTypeInformation -UseCulture:$false
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'spike.json') -Encoding utf8

$md = [System.Text.StringBuilder]::new()
$null = $md.AppendLine("### llama-server spike — $($summary.date), backend $Backend, release $Release")
$null = $md.AppendLine('')
$null = $md.AppendLine('| | |')
$null = $md.AppendLine('|---|---|')
if ($summary.gpu.nvidiaSmi) {
    $null = $md.AppendLine(("| GPU | {0}, driver {1}, CUDA version {2} (nvidia-smi), {3} MiB |" -f $summary.gpu.name, $summary.gpu.driver, $summary.gpu.cudaVersion, (Fmt $summary.gpu.totalMiB)))
}
else {
    $null = $md.AppendLine('| GPU | nvidia-smi not present; adapter figures from the WDDM counters only |')
}
foreach ($a in $summary.archives) { $null = $md.AppendLine(("| ``{0}`` | {1} bytes, sha256 ``{2}`` — {3} |" -f $a.name, (Fmt $a.bytes), $a.sha256, $a.status)) }
foreach ($c in $summary.cudart)   { $null = $md.AppendLine(("| ``{0}`` | file version {1} |" -f $c.name, $c.fileVersion)) }
$null = $md.AppendLine(("| Model | ``{0}`` — {1} bytes{2} |" -f (Split-Path -Leaf $ModelPath), (Fmt $summary.model.bytes), $(if ($summary.model.sha256) { ", sha256 ``$($summary.model.sha256)``" } else { '' })))
$null = $md.AppendLine(("| Server | ``{0}`` |" -f (($summary.server.args | ForEach-Object { if ($_ -eq $ModelPath) { '<model>' } else { $_ } }) -join ' ')))
if ($childEnv.Count -gt 0) { $null = $md.AppendLine(("| Child environment | {0} |" -f (($childEnv.GetEnumerator() | ForEach-Object { "``$($_.Key)=$($_.Value)``" }) -join ', '))) }
$null = $md.AppendLine(("| Prompt | {0} |" -f $summary.promptSource))
$null = $md.AppendLine('')
$null = $md.AppendLine('| Start | Seconds to `/health` |')
$null = $md.AppendLine('|---|---|')
foreach ($s in $summary.starts) { $null = $md.AppendLine(("| {0} | {1} |" -f $s.attempt, (Fmt $s.secondsToHealth 'N2'))) }
$null = $md.AppendLine('')
$null = $md.AppendLine('| Phase | Adapter dedicated (MiB) | Adapter shared | Server dedicated | Server shared | nvidia-smi used |')
$null = $md.AppendLine('|---|---|---|---|---|---|')
foreach ($s in $summary.samples) {
    $null = $md.AppendLine(("| {0} | {1} | {2} | {3} | {4} | {5} |" -f $s.phase, (Fmt $s.adapterDedicatedMiB 'N1'), (Fmt $s.adapterSharedMiB 'N1'), (Fmt $s.serverDedicatedMiB 'N1'), (Fmt $s.serverSharedMiB 'N1'), (Fmt $s.nvidiaSmiUsedMiB)))
}
$null = $md.AppendLine('')
$null = $md.AppendLine('| Request | Prompt tokens | Prompt ms | Prompt tok/s | Generated | Predicted ms | Predicted tok/s | Wall ms |')
$null = $md.AppendLine('|---|---|---|---|---|---|---|---|')
foreach ($pair in @(@('prefill', $summary.prefill), @('answer', $summary.answer))) {
    $r = $pair[1]
    if ($null -ne $r) {
        $null = $md.AppendLine(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |" -f $pair[0], (Fmt $r.promptTokens), (Fmt $r.promptMs 'N1'), (Fmt $r.promptPerSecond 'N1'), (Fmt $r.completionTokens), (Fmt $r.predictedMs 'N1'), (Fmt $r.predictedPerSecond 'N1'), (Fmt $r.wallMs)))
    }
}
if ($summary.logLines.Count -gt 0) {
    $null = $md.AppendLine('')
    $null = $md.AppendLine('What the server said about itself (first start, matched lines):')
    $null = $md.AppendLine('')
    $null = $md.AppendLine('```')
    foreach ($line in $summary.logLines) { $null = $md.AppendLine($line) }
    $null = $md.AppendLine('```')
}
if ($summary.notes.Count -gt 0) {
    $null = $md.AppendLine('')
    foreach ($n in $summary.notes) { $null = $md.AppendLine("- $n") }
}
$null = $md.AppendLine('')
$null = $md.AppendLine('Per-process "dedicated" is committed memory and can sum past the adapter total; the adapter figure is what the card is holding. Two starts that both reach `/health` in seconds with `CUDA_CACHE_DISABLE=1` mean native cubins, not a JIT. Nothing here measures answer quality.')

$mdPath = Join-Path $OutputDirectory 'summary.md'
$md.ToString() | Set-Content -LiteralPath $mdPath -Encoding utf8

Write-Heading 'the block for the docs'
Write-Host $md.ToString()
Write-Host ("written: {0}" -f $OutputDirectory) -ForegroundColor Cyan
