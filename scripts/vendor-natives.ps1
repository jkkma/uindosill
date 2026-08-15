<#
.SYNOPSIS
    Fetches the pinned parakeet.cpp release archives, verifies each against its recorded SHA-256, and
    unpacks them into native/win-x64/<backend>/ — the layout the loader searches and the build copies.

.DESCRIPTION
    docs/NATIVE-BINARIES.md explains why the natives are pinned to a release rather than tracked or
    fetched at build time, and until now "vendor" meant reading that document and doing it by hand:
    pick the `lib-` family and not `bin-`, download the right archive, hash it, compare by eye,
    unpack flat, keep the LICENSE. Every one of those steps has been got wrong at least once, and one
    of them — the LICENSE — is a licence breach when it goes wrong, and fails silently. This script is
    that procedure, so that a fresh machine and the CI publish job both get the same drop from the
    same pins.

    What one run does, per backend:

    1. Finds the archive in -ArchiveDirectory, or downloads it from the pinned release. A download
       lands under a temporary name and is renamed only after it verifies, so a file carrying the
       real name has passed step 2 at least once.
    2. Checks the byte count and then the SHA-256 against the pins in this file. Either mismatch
       stops the run before anything is unpacked: a wrong archive on disk is a question, not a
       drop. The pins are the same values recorded in docs/NATIVE-BINARIES.md, and step 5 holds the
       two together.
    3. Unpacks it flat into native/win-x64/<backend>/. Upstream wraps the four files in a directory
       named after the archive; the loader wants parakeet.dll directly inside the backend folder.
       Files already there are left alone when they match the archive's size, overwritten with
       -Force, and refused otherwise — silently keeping a stale parakeet.dll is the failure this
       exists to prevent.
    4. Reads the result back: parakeet.dll at the documented byte count, LICENSE beside it. The
       LICENSE check is not tidiness. parakeet.cpp is MIT, and build/NativeAssets.targets copies
       native/**/LICENSE into the output for that reason; a backend directory without one ships an
       MIT binary without its notice.
    5. Confirms every digest it trusted appears in docs/NATIVE-BINARIES.md, and fails if one does
       not. Bump the pin here without recording it there and the run says so — the same guard, for
       the same reason, as scripts/check-test-counts.py.

    CUDA is included but not default. It is opt-in in the product, roughly 700 MB across two
    archives, and has questions of its own — which GPU architectures were compiled in, whether
    VCOMP140 is on the machine — that scripts/vendor-cuda.ps1 answers by reading the binaries. So
    for -Backends cuda this script does steps 1 and 2 for both archives and hands them to
    vendor-cuda.ps1 for the unpacking and inspection, then does step 4 on what it left. That script
    reads a PE import table against System32, so the cuda path is Windows-only; cpu and vulkan run
    wherever pwsh does, which is what lets the Linux CI runner vendor before it publishes.

    Nothing here is arm64. v0.5.0 publishes no win-arm64 asset, and docs/NATIVE-BINARIES.md says so.

    Rebuild afterwards. build/NativeAssets.targets evaluates its glob when the project is
    evaluated, so a drop made after the last build is not in the output until the next one — and
    `uindosill doctor` reports "not vendored" for it, which is the wrong diagnosis. The last thing
    this prints is the reminder.

.EXAMPLE
    .\scripts\vendor-natives.ps1
    cpu and vulkan, the backends every build ships. Downloads about 18 MB the first time.

.EXAMPLE
    .\scripts\vendor-natives.ps1 -Backends cpu,vulkan,cuda
    All three. The CUDA pair is about 700 MB and unpacks to 931 MB.

.EXAMPLE
    .\scripts\vendor-natives.ps1 -ArchiveDirectory ~\Downloads
    Use archives already downloaded by hand; each is still verified before it is unpacked, and
    anything missing is fetched into that directory.
#>

[CmdletBinding()]
param(
    # Which backends to vendor. cpu and vulkan are what every build ships; cuda is opt-in.
    [ValidateSet('cpu', 'vulkan', 'cuda')]
    [string[]] $Backends = @('cpu', 'vulkan'),

    # Where downloaded archives are kept, and where existing ones are looked for first. Defaults
    # to native/archives/, which .gitignore already covers and the build's glob does not touch.
    [string] $ArchiveDirectory,

    # The native/ root to unpack into. Defaults to the repository's, which is what
    # build/NativeAssets.targets copies from.
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

# A progress bar is useful for a 553 MB cudart download at a desk and noise in a CI log.
if ($env:GITHUB_ACTIONS -or $env:CI) { $ProgressPreference = 'SilentlyContinue' }

# ── The pins ────────────────────────────────────────────────────────────────────────────────────
#
# Release tag, archive names, byte counts and SHA-256 as served by GitHub, and the byte count of
# the parakeet.dll each archive contains. These are the values in the digest table at the end of
# docs/NATIVE-BINARIES.md, and the run fails if the two disagree — see the last heading below.
#
# The byte count is checked before the digest because it fails faster and says more: an HTML error
# page saved as a .zip is a few KB, and "expected 17,945,091 bytes, got 9,214" is a diagnosis where
# a digest mismatch is only a symptom.
$release = 'v0.5.0'
$releaseUrl = "https://github.com/mudler/parakeet.cpp/releases/download/$release"

function New-Archive {
    param([string] $Name, [long] $Length, [string] $Sha256)
    [PSCustomObject]@{ Name = $Name; Length = $Length; Sha256 = $Sha256; Path = $null }
}

$pins = [ordered]@{
    cpu = [PSCustomObject]@{
        Archives = @(
            New-Archive "parakeet-$release-lib-win-cpu-x64.zip" 735995 `
                '0e9b8a305bf25a485b27bbcb2496fbd5bc8a0653d39c24a76c87a2053966a453'
        )
        LibraryLength = 2008064
    }
    vulkan = [PSCustomObject]@{
        Archives = @(
            New-Archive "parakeet-$release-lib-win-vulkan-x64.zip" 17945091 `
                '4527898049ee1566c4b3e12c8a40ddcce154d2fc5c1661ac00a95b64cd6e512c'
        )
        LibraryLength = 59453952
    }
    cuda = [PSCustomObject]@{
        # Two archives: the library, then the CUDA runtime it imports. Order matters below, where
        # the first is -LibArchive and the second -CudartArchive for vendor-cuda.ps1.
        Archives = @(
            New-Archive "parakeet-$release-lib-win-cuda-x64.zip" 156486028 `
                'be61348d3e1ea60059c141ae3eda7f04bd69bea80ecc689f96bc47a6a1691016'
            New-Archive 'cudart-parakeet-bin-win-cuda-x64.zip' 580185113 `
                'cc2b5fb99951720130e4a701e0978419d0a878e25c88bebc1416152616bd1d94'
        )
        LibraryLength = 169960960
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

# Byte count first, then digest. Returns nothing; throws with both values on a mismatch, because
# the caller has nothing useful to do with a wrong archive except show the reader what it got.
function Assert-ArchiveMatches {
    param([PSCustomObject] $Archive, [string] $Path, [string] $Origin)

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $Archive.Length) {
        throw ("{0} ({1}) is {2:N0} bytes; the pinned archive is {3:N0}. Not unpacking it. " +
               "A few KB is usually an error page saved under the archive's name.") -f `
               $Archive.Name, $Origin, $item.Length, $Archive.Length
    }

    $actual = Get-Sha256 -Path $Path
    if ($actual -ne $Archive.Sha256) {
        throw ("{0} ({1}) hashes to`n  {2}`nbut the pin is`n  {3}`nNot unpacking it. If the file " +
               "was placed by hand, delete it and let this script download the archive; if it was " +
               "downloaded by this script, the release asset has changed under the tag and " +
               "docs/NATIVE-BINARIES.md needs a new row before anything else happens.") -f `
               $Archive.Name, $Origin, $actual, $Archive.Sha256
    }
}

# Finds the archive on disk or downloads it, verifies either way, and fills in .Path.
function Resolve-Archive {
    param([PSCustomObject] $Archive)

    $target = Join-Path $ArchiveDirectory $Archive.Name
    Write-Host ("{0,-14} {1}" -f 'archive', $Archive.Name)

    if (Test-Path -LiteralPath $target) {
        Assert-ArchiveMatches -Archive $Archive -Path $target -Origin 'already on disk'
        Write-Host ("{0,-14} already in {1}" -f 'source', $ArchiveDirectory)
    }
    else {
        New-Item -ItemType Directory -Path $ArchiveDirectory -Force | Out-Null
        $url = "$releaseUrl/$($Archive.Name)"
        $partial = "$target.partial"

        Write-Host ("{0,-14} {1}" -f 'source', $url)
        Write-Host ("{0,-14} {1:N1} MB" -f 'downloading', ($Archive.Length / 1MB))

        # Three attempts, because a release-asset redirect that times out once is the most common
        # way a CI job fails for a reason that has nothing to do with the code. Anything still
        # failing after three is reported with the last error.
        $attempt = 0
        $started = Get-Date
        while ($true) {
            $attempt++
            try {
                if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
                Invoke-WebRequest -Uri $url -OutFile $partial -MaximumRedirection 5
                break
            }
            catch {
                if ($attempt -ge 3) { throw "Download of $url failed on attempt ${attempt}: $_" }
                Write-Host ("{0,-14} attempt {1} failed ({2}); retrying" -f 'download', $attempt, $_.Exception.Message) -ForegroundColor Yellow
                Start-Sleep -Seconds (5 * $attempt)
            }
        }
        $elapsed = (Get-Date) - $started
        Write-Host ("{0,-14} {1:N1} s" -f 'took', $elapsed.TotalSeconds)

        # Verify under the temporary name, so a file with the real name has always been checked.
        Assert-ArchiveMatches -Archive $Archive -Path $partial -Origin 'just downloaded'
        Move-Item -LiteralPath $partial -Destination $target -Force
    }

    $item = Get-Item -LiteralPath $target
    Write-Host ("{0,-14} {1:N0} bytes" -f 'size', $item.Length)
    Write-Host ("{0,-14} {1}  matches the pin" -f 'sha-256', $Archive.Sha256) -ForegroundColor Green

    $Archive.Path = $item.FullName
}

# Flatten the archive into $Target. Existing files: kept when their size matches the entry, replaced
# with -Force, and otherwise collected and thrown at the end — never skipped in silence, because a
# stale parakeet.dll under the right name is the one drop that looks correct and is not.
function Expand-Flattened {
    param([string] $Path, [string] $Target, [switch] $Overwrite)

    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    $written = 0
    $kept = 0
    $collisions = @()

    try {
        foreach ($entry in $zip.Entries) {
            # Directory entries have no Name; upstream wraps the four files in one of those, and
            # only the leaf name is used here, so nothing in the archive can write outside $Target.
            if (-not $entry.Name) { continue }

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

    Write-Host ("{0,-14} {1} written, {2} already present and the same size" -f 'extracted', $written, $kept)

    if ($collisions.Count -gt 0) {
        throw ("Files already in {0} differ in size from the archive, and were not touched:`n{1}`n" +
               "Re-run with -Force to replace them with the pinned release's.") -f $Target, ($collisions -join "`n")
    }
}

# What is actually in the backend directory afterwards, checked against the pins rather than
# against the archive it came from — that is the difference between "the unpack ran" and "the drop
# is the one the documentation describes".
function Assert-Drop {
    param([string] $Backend, [string] $Directory, [long] $LibraryLength)

    $files = @(Get-ChildItem -LiteralPath $Directory -File | Sort-Object Name)
    foreach ($file in $files) {
        Write-Host ("  {0,-24} {1,14:N0} bytes" -f $file.Name, $file.Length)
    }

    $library = @($files | Where-Object { $_.Name -eq 'parakeet.dll' })
    if ($library.Count -ne 1) {
        throw "No parakeet.dll in $Directory after unpacking. The archive is not the lib- family, or the unpack did not run."
    }
    if ($library[0].Length -ne $LibraryLength) {
        throw ("parakeet.dll in {0} is {1:N0} bytes; the {2} build documented for {3} is {4:N0}. " +
               "That is not the pinned library.") -f $Directory, $library[0].Length, $Backend, $release, $LibraryLength
    }

    $licence = @($files | Where-Object { $_.Name -eq 'LICENSE' -and $_.Length -gt 0 })
    if ($licence.Count -ne 1) {
        throw ("No LICENSE beside parakeet.dll in {0}. parakeet.cpp is MIT and its notice has to travel " +
               "with every copy; build/NativeAssets.targets copies native/**/LICENSE for exactly this. " +
               "Do not ship this directory as it stands.") -f $Directory
    }

    Write-Host ("  parakeet.dll is the {0:N0}-byte {1} build, and LICENSE is beside it" -f $LibraryLength, $Backend) -ForegroundColor Green
}

Push-Location $repo
try {
    $trusted = @()

    foreach ($backend in $Backends) {
        $pin = $pins[$backend]
        $destination = Join-Path (Join-Path $NativeRoot $rid) $backend

        Write-Heading "$backend — archives"
        foreach ($archive in $pin.Archives) {
            Resolve-Archive -Archive $archive
            $trusted += $archive
            Write-Host ''
        }

        Write-Heading "$backend — unpacking into native/$rid/$backend"
        if ($backend -eq 'cuda') {
            # vendor-cuda.ps1 owns the CUDA drop: it unpacks both archives flat and then reads the
            # result back — runtime version, import table, compiled GPU architectures. Everything it
            # prints is worth reading; this script only adds the digest check in front of it. It
            # throws on failure and $ErrorActionPreference is Stop here, so a failure inside it ends
            # this run too.
            $cudaScript = Join-Path $PSScriptRoot 'vendor-cuda.ps1'
            Write-Host "handing both archives to $cudaScript"
            & $cudaScript -LibArchive $pin.Archives[0].Path -CudartArchive $pin.Archives[1].Path `
                          -Destination $destination -Force:$Force
        }
        else {
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Expand-Flattened -Path $pin.Archives[0].Path -Target $destination -Overwrite:$Force
        }

        Write-Heading "$backend — what is in native/$rid/$backend"
        Assert-Drop -Backend $backend -Directory $destination -LibraryLength $pin.LibraryLength
    }

    # The pins above and the table in docs/NATIVE-BINARIES.md are two copies of one fact, and this
    # is what keeps them one. It runs after the drop is in place because the drop is correct either
    # way; what is wrong on a mismatch is the record, and the exit code says so.
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
        $rows = ($unrecorded | ForEach-Object { "| $release | ``$($_.Name)`` | ``$($_.Sha256)`` | **unconfirmed** | $(Get-Date -Format yyyy-MM-dd) |" }) -join "`n"
        throw ("The drop is in place, but {0} digest(s) this script trusts are not in the table at the " +
               "end of docs/NATIVE-BINARIES.md. Record them — the pin and the record must agree:`n{1}") -f `
               $unrecorded.Count, $rows
    }

    Write-Heading 'next'
    Write-Host '  dotnet build Uindosill.slnx -c Release          (the glob is evaluated at build time: no'
    Write-Host '                                                   rebuild, and doctor calls this "not vendored")'
    Write-Host '  uindosill doctor                                (each backend: ok — abi 6 from <its own directory>)'
    Write-Host ''
    Write-Host '  ok from doctor means the DLL and its imports resolved and the ABI is 6. It does not mean the'
    Write-Host '  library can decode on this GPU; only a transcription shows that. Read the "from <path>" and'
    Write-Host '  confirm it names the backend directory you asked for.'
}
finally {
    Pop-Location
}
