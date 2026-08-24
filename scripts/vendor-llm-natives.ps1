<#
.SYNOPSIS
    Fetches the pinned llama.cpp release archives, verifies each against its recorded SHA-256, and
    unpacks the server set into native/win-x64/llm/<backend>/ — the second native stack, under the
    same rules as the first.

.DESCRIPTION
    The v2 language-model tier runs `llama-server` as a bundled child process
    (docs/V2-ASK-THE-TRANSCRIPT.md, decision 1), and its natives follow the parakeet pattern
    exactly: a pinned release, byte counts and SHA-256 checked before anything is unpacked, the
    licence text beside the binaries, and every trusted digest recorded in
    docs/NATIVE-BINARIES.md. This script is scripts/vendor-natives.ps1's shape applied to the
    second stack; where the two differ, the difference is stated below rather than left to be
    noticed.

    Three differences from the parakeet script:

    1. The archives are pruned, not flattened whole. A llama.cpp Windows zip carries a dozen
       tools — llama-cli, llama-bench, llama-quantize and friends — and build/NativeAssets.targets
       globs native/**/*.exe into every build output, so vendoring the whole zip would ship ten
       executables the product never spawns. What lands is the server set: llama-server.exe and
       every DLL. The lab scripts that want llama-bench fetch their own zips into a scratch
       directory (scripts/spike-llama-server.ps1 already does) and are not this script's problem.

    2. The LICENSE does not come from the archive, because no llama.cpp release zip ships one —
       measured at b10448 and again at the pin below. The MIT text is fetched from the source tree
       at the pinned tag, verified against its own recorded digest, and written beside the
       binaries in each backend directory, where build/NativeAssets.targets carries it into the
       output. MIT requires the notice to travel with every copy; a backend directory without it
       ships an unlicensed binary.

    3. No inner byte-count pin. vendor-natives.ps1 checks parakeet.dll's documented length after
       unpacking; here the archive digest transitively pins every inner byte, and the after-check
       is presence — llama-server.exe, llama.dll, the backend's own ggml DLL, LICENSE — rather
       than a second copy of a number the digest already guarantees.

    The pin is a release *tag*, never "latest": upstream marks its build releases as prereleases,
    so the GitHub releases/latest endpoint answers with something that is not a build at all
    (observed 2026-08-23). The archive digests below are the `digest` field the releases API
    serves for each asset, read 2026-08-23, and every download is re-hashed against them here.

    CUDA is included but not default, exactly as for the ASR tier: it is the desktop's backend,
    ~537 MB across two archives, and the cuda-13.3 zip's compiled GPU architectures are read with
    scripts/vendor-cuda.ps1 -InspectOnly, which this script reminds rather than repeats.

.EXAMPLE
    .\scripts\vendor-llm-natives.ps1
    cpu and vulkan, the backends the laptop and the default channel use. About 52 MB the first time.

.EXAMPLE
    .\scripts\vendor-llm-natives.ps1 -Backends cuda
    The desktop pair: the cuda-13.3 zip and its cudart beside it. About 537 MB.
#>

[CmdletBinding()]
param(
    # Which backends to vendor. cpu and vulkan are the laptop's and the default channel's; cuda is
    # the desktop tier.
    [ValidateSet('cpu', 'vulkan', 'cuda')]
    [string[]] $Backends = @('cpu', 'vulkan'),

    # Where downloaded archives are kept and looked for first. Defaults to native/archives/,
    # which .gitignore covers and the build's glob does not touch.
    [string] $ArchiveDirectory,

    # The native/ root to unpack into. The drop lands under <root>/win-x64/llm/<backend>/.
    [string] $NativeRoot,

    # Overwrite files already present in the backend directory instead of refusing on a mismatch.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
if (-not $ArchiveDirectory) { $ArchiveDirectory = Join-Path $repo 'native/archives' }
if (-not $NativeRoot)       { $NativeRoot = Join-Path $repo 'native' }
$rid = 'win-x64'

if ($env:GITHUB_ACTIONS -or $env:CI) { $ProgressPreference = 'SilentlyContinue' }

# ── The pins ────────────────────────────────────────────────────────────────────────────────────
#
# Release tag, archive names, byte counts and SHA-256 as the GitHub releases API serves them
# (each asset's `digest` field, read 2026-08-23, re-hashed here on every run). These are the
# values in the llama.cpp table at the end of docs/NATIVE-BINARIES.md, and the run fails if the
# two disagree.
$release = 'b10603'
$releaseUrl = "https://github.com/ggml-org/llama.cpp/releases/download/$release"

# The MIT text at the pinned tag, since no release zip carries one. 1,078 bytes,
# "Copyright (c) 2023-2026 The ggml authors".
$licenceUrl = "https://raw.githubusercontent.com/ggml-org/llama.cpp/$release/LICENSE"
$licenceSha256 = '94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d'
$licenceLength = 1078

function New-Archive {
    param([string] $Name, [long] $Length, [string] $Sha256)
    [PSCustomObject]@{ Name = $Name; Length = $Length; Sha256 = $Sha256; Path = $null }
}

$pins = [ordered]@{
    cpu = [PSCustomObject]@{
        Archives = @(
            New-Archive "llama-$release-bin-win-cpu-x64.zip" 18063576 `
                '878efa5bc0cdeb9c3fcb96335521556e06ca9252f83de3a1d924981918607702'
        )
        # A pattern rather than a name: with GGML_BACKEND_DL the CPU backend is a family of
        # per-ISA variants (haswell, icelake, …) picked at load, not one file.
        MarkerDll = 'ggml-cpu-*.dll'
    }
    vulkan = [PSCustomObject]@{
        Archives = @(
            New-Archive "llama-$release-bin-win-vulkan-x64.zip" 34400125 `
                '8e2fa4ef100af6e4a08f7d9cf9686ee40b1349e6c11933efd63f4e68f9261d2e'
        )
        MarkerDll = 'ggml-vulkan.dll'
    }
    cuda = [PSCustomObject]@{
        # The cuda zip is the whole CPU drop with ggml-cuda.dll on top; the cudart archive is the
        # runtime it needs beside it, and it does not churn with the builds — its bytes at this
        # tag are identical to the ones read beside b10448.
        Archives = @(
            New-Archive "llama-$release-bin-win-cuda-13.3-x64.zip" 146422151 `
                '687a4e750e89790491802fa369f4541763f7e8d43cb27f0d3cf2e4fc4063258d'
            New-Archive 'cudart-llama-bin-win-cuda-13.3-x64.zip' 390970417 `
                '1462a050eb4c684921ba51dcc4cc488a036674c3e73e9945ee705b854808d03e'
        )
        MarkerDll = 'ggml-cuda.dll'
    }
}

function Write-Heading {
    param([string] $Text)
    Write-Host ''
    Write-Host ("── $Text " + ('─' * [Math]::Max(1, 46 - $Text.Length))) -ForegroundColor Green
}

function Get-Sha256 {
    param([string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-FileMatches {
    param([string] $Name, [long] $Length, [string] $Sha256, [string] $Path, [string] $Origin)

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $Length) {
        throw ("{0} ({1}) is {2:N0} bytes; the pin is {3:N0}. Not using it. " +
               "A few KB is usually an error page saved under the file's name.") -f `
               $Name, $Origin, $item.Length, $Length
    }

    $actual = Get-Sha256 -Path $Path
    if ($actual -ne $Sha256) {
        throw ("{0} ({1}) hashes to`n  {2}`nbut the pin is`n  {3}`nNot using it. If the file was " +
               "placed by hand, delete it and let this script download it; if this script " +
               "downloaded it, the release asset has changed under the tag and " +
               "docs/NATIVE-BINARIES.md needs a new row before anything else happens.") -f `
               $Name, $Origin, $actual, $Sha256
    }
}

# Finds the file on disk or downloads it, verifies either way, and returns the path. Downloads
# land under a temporary name and are renamed only after they verify, so a file carrying the real
# name has passed the check at least once.
function Resolve-Pinned {
    param([string] $Name, [long] $Length, [string] $Sha256, [string] $Url)

    $target = Join-Path $ArchiveDirectory $Name
    Write-Host ("{0,-14} {1}" -f 'archive', $Name)

    if (Test-Path -LiteralPath $target) {
        Assert-FileMatches -Name $Name -Length $Length -Sha256 $Sha256 -Path $target -Origin 'already on disk'
        Write-Host ("{0,-14} already in {1}" -f 'source', $ArchiveDirectory)
    }
    else {
        New-Item -ItemType Directory -Path $ArchiveDirectory -Force | Out-Null
        $partial = "$target.partial"

        Write-Host ("{0,-14} {1}" -f 'source', $Url)
        Write-Host ("{0,-14} {1:N1} MB" -f 'downloading', ($Length / 1MB))

        $attempt = 0
        $started = Get-Date
        while ($true) {
            $attempt++
            try {
                if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
                Invoke-WebRequest -Uri $Url -OutFile $partial -MaximumRedirection 5
                break
            }
            catch {
                if ($attempt -ge 3) { throw "Download of $Url failed on attempt ${attempt}: $_" }
                Write-Host ("{0,-14} attempt {1} failed ({2}); retrying" -f 'download', $attempt, $_.Exception.Message) -ForegroundColor Yellow
                Start-Sleep -Seconds (5 * $attempt)
            }
        }
        $elapsed = (Get-Date) - $started
        Write-Host ("{0,-14} {1:N1} s" -f 'took', $elapsed.TotalSeconds)

        Assert-FileMatches -Name $Name -Length $Length -Sha256 $Sha256 -Path $partial -Origin 'just downloaded'
        Move-Item -LiteralPath $partial -Destination $target -Force
    }

    Write-Host ("{0,-14} {1:N0} bytes" -f 'size', $Length)
    Write-Host ("{0,-14} {1}  matches the pin" -f 'sha-256', $Sha256) -ForegroundColor Green
    return (Get-Item -LiteralPath $target).FullName
}

# Extracts the server set — llama-server.exe and every DLL — flat into $Target. Existing files:
# kept when their size matches the entry, replaced with -Force, otherwise collected and thrown at
# the end, never skipped in silence.
function Expand-ServerSet {
    param([string] $Path, [string] $Target, [switch] $Overwrite)

    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    $written = 0
    $kept = 0
    $pruned = 0
    $collisions = @()

    try {
        foreach ($entry in $zip.Entries) {
            if (-not $entry.Name) { continue }

            $wanted = $entry.Name -like '*.dll' -or $entry.Name -eq 'llama-server.exe'
            if (-not $wanted) {
                $pruned++
                continue
            }

            $out = Join-Path $Target $entry.Name
            if ((Test-Path -LiteralPath $out) -and -not $Overwrite) {
                $existing = Get-Item -LiteralPath $out
                if ($existing.Length -eq $entry.Length) {
                    $kept++
                    continue
                }
                $collisions += ("  {0}: {1:N0} bytes on disk, {2:N0} in the archive" -f $entry.Name, $existing.Length, $entry.Length)
                continue
            }

            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $out, $true)
            $written++
        }
    }
    finally {
        $zip.Dispose()
    }

    Write-Host ("{0,-14} {1} written, {2} already present and the same size, {3} pruned (lab tools)" -f `
        'extracted', $written, $kept, $pruned)

    if ($collisions.Count -gt 0) {
        throw ("Files already in {0} differ in size from the archive, and were not touched:`n{1}`n" +
               "Re-run with -Force to replace them with the pinned release's.") -f $Target, ($collisions -join "`n")
    }
}

# Presence, not lengths: the archive digest already pins every inner byte. What this proves is
# that the drop is the *server* drop for the backend it claims — and that the licence travels.
function Assert-Drop {
    param([string] $Backend, [string] $Directory, [string] $MarkerDll)

    $files = @(Get-ChildItem -LiteralPath $Directory -File | Sort-Object Name)
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Host ("  {0} files, {1:N1} MB" -f $files.Count, ($bytes / 1MB))

    foreach ($required in @('llama-server.exe', 'llama.dll', $MarkerDll, 'LICENSE')) {
        $hit = @($files | Where-Object { $_.Name -like $required -and $_.Length -gt 0 })
        if ($hit.Count -lt 1) {
            throw ("No {0} in {1} after unpacking. The drop is not the {2} server set this " +
                   "script promises; do not ship this directory as it stands.") -f $required, $Directory, $Backend
        }
    }

    Write-Host ("  llama-server.exe, llama.dll and {0} are in place, and LICENSE is beside them" -f $MarkerDll) -ForegroundColor Green
}

Push-Location $repo
try {
    # The licence text once, verified, then copied into each backend directory below.
    Write-Heading 'the MIT text at the pinned tag'
    $licencePath = Resolve-Pinned -Name "llama-$release-LICENSE" -Length $licenceLength `
        -Sha256 $licenceSha256 -Url $licenceUrl

    $trusted = @()

    foreach ($backend in $Backends) {
        $pin = $pins[$backend]
        $destination = Join-Path (Join-Path (Join-Path $NativeRoot $rid) 'llm') $backend

        Write-Heading "$backend — archives"
        foreach ($archive in $pin.Archives) {
            $archive.Path = Resolve-Pinned -Name $archive.Name -Length $archive.Length `
                -Sha256 $archive.Sha256 -Url "$releaseUrl/$($archive.Name)"
            $trusted += $archive
            Write-Host ''
        }

        Write-Heading "$backend — unpacking into native/$rid/llm/$backend"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($archive in $pin.Archives) {
            Expand-ServerSet -Path $archive.Path -Target $destination -Overwrite:$Force
        }
        Copy-Item -LiteralPath $licencePath -Destination (Join-Path $destination 'LICENSE') -Force

        Write-Heading "$backend — what is in native/$rid/llm/$backend"
        Assert-Drop -Backend $backend -Directory $destination -MarkerDll $pin.MarkerDll
    }

    # The pins above and the table in docs/NATIVE-BINARIES.md are two copies of one fact, and
    # this is what keeps them one — the same guard as vendor-natives.ps1's, over the same file.
    Write-Heading 'the record'
    $doc = Join-Path $repo 'docs/NATIVE-BINARIES.md'
    $text = Get-Content -LiteralPath $doc -Raw
    $unrecorded = @()
    foreach ($archive in $trusted) {
        if ($text.Contains($archive.Sha256)) {
            Write-Host ("  {0,-44} recorded in docs/NATIVE-BINARIES.md" -f $archive.Name) -ForegroundColor Green
        }
        else {
            Write-Host ("  {0,-44} NOT in docs/NATIVE-BINARIES.md" -f $archive.Name) -ForegroundColor Red
            $unrecorded += $archive
        }
    }
    if ($unrecorded.Count -gt 0) {
        $rows = ($unrecorded | ForEach-Object { "| $release | ``$($_.Name)`` | ``$($_.Sha256)`` | $(Get-Date -Format yyyy-MM-dd) |" }) -join "`n"
        throw ("The drop is in place, but {0} digest(s) this script trusts are not in the llama.cpp " +
               "table at the end of docs/NATIVE-BINARIES.md. Record them — the pin and the record " +
               "must agree:`n{1}") -f $unrecorded.Count, $rows
    }

    Write-Heading 'next'
    Write-Host '  dotnet build Uindosill.slnx -c Release          (the glob is evaluated at build time; the drop'
    Write-Host '                                                   is not in any output until the next build)'
    if ($Backends -contains 'cuda') {
        Write-Host '  scripts/vendor-cuda.ps1 -InspectOnly ...        (read the compiled GPU architectures out of'
        Write-Host '                                                   ggml-cuda.dll: sm_120 expected, never yet run)'
    }
    Write-Host ''
    Write-Host '  Nothing loads these yet by being on disk: the engine starts llama-server.exe as a child'
    Write-Host '  process, and only running an ask shows the backend works on this machine.'
}
finally {
    Pop-Location
}
