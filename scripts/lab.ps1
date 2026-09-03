<#
.SYNOPSIS
    One entry point for the measurement and vendoring scripts.

.DESCRIPTION
    Eighteen scripts with eighteen names and eighteen flag sets is seventeen names too many to
    remember when you are switching between machines. This dispatches to them and nothing else: every task is
    still a script you can run directly, and this changes none of their behaviour.

    It is a dispatcher rather than a merge because the scripts it calls produced every number in
    `docs/UNPROVEN.md`. Rewriting them into one file to save a filename would put that at risk for
    no measurement gained.

    Two things here are load-bearing rather than convenience.

    **Parameters are declared, not forwarded blind.** The obvious implementation —
    `[Parameter(ValueFromRemainingArguments)] $Rest` and `& $script @Rest` — does not work, and
    fails in a way that looks like it should. An unquoted `-Formats srt,txt,json` is an array by
    the time the dispatcher sees it, and lands in `$Rest` stringified as `System.Object[]`, so the
    formats are destroyed before anything is called. Splatting an array then passes every element
    positionally, so even `-Path` is not recognised as a parameter name. Declaring the parameters
    and splatting `$PSBoundParameters` — a hashtable — preserves both the array and the names.

    **Drift is caught mechanically.** The parameters each task accepts are read from the target
    script at runtime with `Get-Command`, never listed here. Pass something a task does not take
    and it says so by name; add a parameter to one of those scripts and this file cannot silently
    fail to pass it, because the listing below reads the real thing and marks anything it cannot
    forward.

.EXAMPLE
    .\scripts\lab.ps1
    Lists the tasks, each with the parameters its own script actually declares.

.EXAMPLE
    .\scripts\lab.ps1 vendor
    The pinned cpu and vulkan natives, downloaded, verified against docs/NATIVE-BINARIES.md and
    unpacked into native/win-x64/. Add -Backends cpu,vulkan,cuda for the opt-in CUDA pair.

.EXAMPLE
    .\scripts\lab.ps1 measure -Path chunk.m4a -Backend cuda -Formats srt,txt,json,vtt,vtt-words

.EXAMPLE
    .\scripts\lab.ps1 machine -Path chunk.m4a

.EXAMPLE
    .\scripts\lab.ps1 compare -Reference runs\A\chunk-cpu.json -Candidate runs\B\chunk-cpu.json

.EXAMPLE
    # Note -Candidates, plural and several: `compare` and `word-distance` take different parameters
    # for the same idea, and the reason is in the param block below.
    .\scripts\lab.ps1 word-distance -Reference runs\csb-f16-cuda\CSB384.json `
                                    -Candidates runs\csb-q8_0-cuda\CSB384.json,runs\csb-q4_k-cuda\CSB384.json

.EXAMPLE
    # The v2 spike: upstream llama-server as a child, on the desktop's CUDA tier, prefilling CSB384.
    .\scripts\lab.ps1 spike -ModelPath D:\models\Qwen3.5-9B-Q8_0.gguf -PromptFile CSB384.txt

.EXAMPLE
    # The labelled question set, asked and checked; -PrintPin alone computes the transcript pin.
    .\scripts\lab.ps1 answers -TranscriptPath runs\csb-f16-cuda\CSB384.json -ModelPath D:\models\Qwen3.5-9B-Q8_0.gguf

.EXAMPLE
    # Word error rate of the whole quantisation ladder on CUDA, against both human transcript
    # styles of the pinned Earnings-22 subset; the corpus is fetched and verified on first use.
    .\scripts\lab.ps1 wer -Backend cuda

.EXAMPLE
    # The backend control for that table: f16 alone on CPU.
    .\scripts\lab.ps1 wer -Backend cpu -Models tdt-0.6b-v3-f16

.EXAMPLE
    # What the transcript tidy does to that error rate, both styles off one recogniser pass.
    .\scripts\lab.ps1 wer -Backend vulkan -Models tdt-0.6b-v3-f16 -Tidy

.EXAMPLE
    # The tidy's request unit: segment, joined run and sentence-run, each in the pass shape and in
    # tandem, on one call, with the lag and the deltas the decision rule reads.
    .\scripts\lab.ps1 tidy-units -Backend vulkan

.EXAMPLE
    # Does the shipping decode reproduce the English the translation gate was scored on? Start with
    # Spanish: 348 sentences is minutes, and a decode that is going to disagree disagrees there too.
    .\scripts\lab.ps1 agreement -Languages es

.EXAMPLE
    # Run summaries up to the Drive for the other machine, every transfer checksum-verified.
    .\scripts\lab.ps1 drive -Runs laptop

.EXAMPLE
    # On the desktop, which has no Drive mount by choice: the test episodes, sizes checked.
    .\scripts\lab.ps1 drive -Episodes

.EXAMPLE
    # The diarisation development stretches, cut from those episodes and checked against their pins.
    .\scripts\lab.ps1 der -Cut

.EXAMPLE
    # Speaker-turn hypotheses (one <stretch id>.rttm each) scored against the hand-labelled references.
    .\scripts\lab.ps1 der -Hypotheses runs\spike-x -System "sherpa-onnx 1.13.5 cpu"

.EXAMPLE
    # Both installer flavours for a release, each read back against what its channel promises.
    # Windows only, because vendoring the CUDA drop reads a PE import table.
    .\scripts\lab.ps1 package -Version 1.0.0

.EXAMPLE
    # The default flavour alone: cpu and vulkan, ~82 MB of Setup.exe.
    .\scripts\lab.ps1 package -Version 1.0.0 -Channels win
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('vendor', 'vendor-llm', 'vendor-mpv', 'vendor-tools', 'measure', 'machine', 'compare', 'word-distance', 'vendor-cuda', 'spike', 'answers', 'wer', 'drive', 'der', 'package', 'agreement', 'bundle', 'tidy-units')]
    [string] $Task,

    # --- measure / machine ---
    [string] $Path,
    [string] $Model,
    [ValidateSet('cpu', 'vulkan', 'cuda')]
    [string] $Backend,
    [string[]] $Backends,
    [string[]] $Formats,
    [string] $OutputDirectory,
    [string] $Configuration,
    [string] $MemoryCsv,
    [switch] $ColdVulkanAlreadySpent,
    [switch] $SkipBuild,

    # --- tidy-units (and -Vad for wer) ---
    [string] $File,
    [string[]] $Units,
    [string[]] $Shapes,
    [ValidateSet('energy', 'neural')]
    [string] $Vad,

    # --- compare / word-distance ---
    [string] $Reference,
    [string] $Candidate,

    # word-distance.ps1 takes several candidates against one reference, and this cannot be folded
    # into -Candidate above. Declaring that as [string[]] instead looks like it would serve both and
    # does not: splatting a String[] at compare-transcripts.ps1, which declares [string], throws
    # "Cannot process argument transformation" rather than converting. Two names, because the two
    # scripts genuinely take different things.
    [string[]] $Candidates,
    [double] $TimeEpsilon,
    [double] $ConfidenceEpsilon,
    [switch] $ShowWords,
    [switch] $ShowTimestamps,

    # --- vendor (-Backends and -Force are shared with the tasks above) ---
    [string] $ArchiveDirectory,
    [string] $NativeRoot,

    # --- vendor-cuda ---
    [string] $LibArchive,
    [string] $CudartArchive,
    [string] $Destination,
    [switch] $InspectOnly,
    [switch] $SkipArchScan,
    [switch] $Force,

    # --- spike (-Backend, -ArchiveDirectory, -Destination and -OutputDirectory are shared with the
    #     tasks above; -ModelPath is a file, unlike measure's -Model, which is a catalogue id) ---
    [string] $ModelPath,
    [string] $PromptFile,
    [string] $Question,
    [string] $Release,
    [string] $CudaVersion,
    [int] $ContextSize,
    [ValidateSet('on', 'off', 'auto')]
    [string] $FlashAttention,
    [string] $CacheType,
    [int] $GpuLayers,
    [int] $ReasoningBudget,
    [string[]] $ExtraServerArgs,
    [hashtable] $ServerEnvironment,
    [int] $Port,
    [int] $LoadTimeoutSeconds,
    [int] $RequestTimeoutSeconds,
    [string] $ExpectedModelSha256,
    [switch] $SkipDownload,
    [switch] $SkipScan,
    [switch] $SkipSecondStart,
    [switch] $SkipModelHash,
    [switch] $KeepJitCache,

    # --- answers (most of the server parameters above are shared) ---
    [string] $TranscriptPath,
    [string] $QuestionsPath,
    [switch] $PrintPin,
    [string] $ServerDirectory,
    [int] $MaxAnswerTokens,
    [switch] $SkipGrammarCost,
    [switch] $NoAbstainBranch,

    # --- wer (-Backend, -OutputDirectory, -Configuration and -SkipBuild are shared with measure;
    #     -Models is plural because the ladder is the default and one script's -Model is a
    #     [string], which cannot also be this one's [string[]]) ---
    [string[]] $Models,
    [string[]] $Files,
    [ValidateSet('verbatim', 'nonverbatim')]
    [string[]] $Styles,
    [switch] $Tidy,
    [ValidateSet('cpu', 'vulkan', 'cuda')]
    [string] $TidyBackend,
    [switch] $KeepFillers,
    [string] $ManifestPath,
    [string] $CorpusRoot,
    [switch] $SkipVerify,

    # --- drive (-Destination is shared with vendor-cuda; sync-drive.ps1's parameter sets make
    #     -Runs, -Research, -Memory, -Fetch and -Episodes mutually exclusive, and it says so if two
    #     are passed together) ---
    [ValidateSet('laptop', 'desktop')]
    [string] $Runs,
    [string] $Research,

    # -Memory must be declared here even though this file forwards by name, and the reason is
    # measure's -MemoryCsv above: PowerShell binds an unambiguous PREFIX, so before this line
    # `lab.ps1 drive -Memory desktop` bound to -MemoryCsv and was rejected as a parameter the drive
    # task does not take — an error naming a switch the caller never typed. An exact declaration
    # wins over a prefix match. Observed 2026-08-17.
    [ValidateSet('laptop', 'desktop')]
    [string] $Memory,

    [string] $Fetch,
    [switch] $Episodes,
    [string] $Remote,
    [string] $DriveFolder,
    [switch] $DryRun,

    # --- der (-Destination, -Force, -ManifestPath, -OutputDirectory, -Configuration and -SkipBuild
    #     are shared with the tasks above; measure-der.ps1's parameter sets keep -Cut and
    #     -Hypotheses apart, and it says so if both are passed) ---
    [string] $Hypotheses,
    [string] $System,
    [string] $ReferenceDirectory,
    [double] $Collar,
    [switch] $SkipOverlap,
    [switch] $Cut,
    [string[]] $Stretches,
    [string] $EpisodeDirectory,

    # --- agreement (-Configuration is shared with measure) ---
    [string] $Languages,
    [int] $Sentences,
    [string] $Variant,
    [int] $Threads,

    # -Run is declared exactly, and has to be, for the same reason -Memory above is: PowerShell
    # binds an unambiguous prefix, and without this line `lab.ps1 agreement -Run <dir>` is ambiguous
    # between drive's -Runs and package's -Runtime and is rejected naming neither. An exact
    # declaration wins over a prefix match.
    [string] $Run,

    # --- package (-OutputDirectory and -Version's siblings are not shared; -Channels is plural for
    #     the same reason -Backends is, and package-windows.ps1's own ValidateSet is what limits it
    #     to win and win-cuda) ---
    [string] $Version,
    [string[]] $Channels,
    [string] $Runtime,

    # A path to a markdown file, not the notes: vpk's --releaseNotes takes a filename.
    [string] $ReleaseNotes,
    [switch] $SkipVendor,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$tasks = [ordered]@{
    'vendor'        = 'vendor-natives.ps1'
    'vendor-llm'    = 'vendor-llm-natives.ps1'
    'vendor-mpv'    = 'vendor-mpv.ps1'
    'vendor-tools'  = 'vendor-tools.ps1'
    'measure'       = 'measure-transcribe.ps1'
    'machine'       = 'measure-second-machine.ps1'
    'compare'       = 'compare-transcripts.ps1'
    'word-distance' = 'word-distance.ps1'
    'vendor-cuda'   = 'vendor-cuda.ps1'
    'spike'         = 'spike-llama-server.ps1'
    'answers'       = 'measure-answers.ps1'
    'wer'           = 'measure-wer.ps1'
    'drive'         = 'sync-drive.ps1'
    'der'           = 'measure-der.ps1'
    'agreement'     = 'measure-translation-agreement.ps1'
    'package'       = 'package-windows.ps1'
    'bundle'        = 'bundle-python.ps1'
    'tidy-units'    = 'measure-tidy-units.ps1'
}

# What this file declares, so the listing can mark anything a task takes and this cannot pass on.
$declared = @($MyInvocation.MyCommand.Parameters.Keys)

function Resolve-Task([string] $name) {
    $target = Join-Path $PSScriptRoot $tasks[$name]
    if (-not (Test-Path -LiteralPath $target)) { throw "Task '$name' points at $target, which is not there." }
    return $target
}

function Get-TaskParameters([string] $target) {
    $common = [System.Management.Automation.PSCmdlet]::CommonParameters +
              [System.Management.Automation.PSCmdlet]::OptionalCommonParameters
    return @((Get-Command $target).Parameters.Keys | Where-Object { $_ -notin $common })
}

if (-not $Task) {
    Write-Host ''
    Write-Host 'uindosill lab — one entry point for the measurement and vendoring scripts' -ForegroundColor Cyan
    Write-Host ''

    foreach ($name in $tasks.Keys) {
        $target = Resolve-Task $name

        # Read from the script rather than repeated here, so this listing cannot go stale.
        $synopsis = (Get-Help $target -ErrorAction SilentlyContinue).Synopsis
        $synopsis = ($synopsis -replace '\s+', ' ').Trim()
        if ($synopsis.Length -gt 92) { $synopsis = $synopsis.Substring(0, 89) + '...' }

        Write-Host ("  {0,-14} {1}" -f $name, $synopsis) -ForegroundColor Green
        Write-Host ("  {0,-14} {1}" -f '', $tasks[$name]) -ForegroundColor DarkGray

        $rendered = Get-TaskParameters $target | Sort-Object | ForEach-Object {
            # A '!' means the task accepts it and this dispatcher cannot pass it on — call the
            # script directly for that one. It is here so drift is visible rather than silent.
            if ($_ -in $declared) { "-$_" } else { "!-$_" }
        }
        Write-Host ("  {0,-14} {1}" -f '', ($rendered -join ' '))
        Write-Host ''
    }

    Write-Host '  A leading ! marks a parameter the task accepts that this dispatcher does not declare;' -ForegroundColor DarkGray
    Write-Host '  run that script directly to use it. Every task is runnable on its own.' -ForegroundColor DarkGray
    Write-Host ''
    return
}

$target = Resolve-Task $Task
$accepted = @((Get-Command $target).Parameters.Keys)

$forward = @{}
$rejected = @()

foreach ($name in $PSBoundParameters.Keys) {
    if ($name -eq 'Task') { continue }
    if ($name -in $accepted) { $forward[$name] = $PSBoundParameters[$name] }
    else { $rejected += $name }
}

if ($rejected.Count -gt 0) {
    $usable = (Get-TaskParameters $target | Sort-Object | ForEach-Object { "-$_" }) -join ' '
    throw "Task '$Task' ($($tasks[$Task])) does not take: $(($rejected | ForEach-Object { "-$_" }) -join ', ').`n" +
          "It takes: $usable"
}

& $target @forward
