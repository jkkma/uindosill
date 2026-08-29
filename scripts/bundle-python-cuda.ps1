<#
.SYNOPSIS
    Assembles the CUDA pack: the three packages whose CUDA build differs from the bundle's CPU one,
    laid out where `PythonRuntime.ResolveCudaPack` looks for them.

.DESCRIPTION
    **What the pack is, and why it is not a second bundle.** The bundle pins the CPU torch build
    (`python/requirements-bundle.txt`), so the diariser's `auto` elects the CPU on every installed
    copy. This is the artefact that changes that on an NVIDIA machine. It is an *overlay*: a
    directory put ahead of the bundle on `PYTHONPATH`, which shadows the bundle's `torch` without
    replacing a byte of it. Deleting the directory undoes it completely.

    **Three packages, because that is what was measured.** On 2026-08-28, on this project's own
    pins, a CPU and a CUDA install of `requirements-bundle.txt` differ in exactly three places:

      | package    | CPU build | CUDA build |
      |------------|-----------|------------|
      | torch      |  489.8 MB |  2778.4 MB |
      | torchcodec |   23.4 MB |    38.2 MB |
      | torchaudio |    2.3 MB |     9.2 MB |

    Everything else in the two site-packages trees is identical — **and on Windows there are no
    separate `nvidia_*` distributions at all**, the CUDA libraries living inside `torch/lib`
    (`cublasLt64_13.dll` alone is 456 MB). So the pack is 2.8 GB where a second whole bundle would
    be about 4 GB, and it needs no change to the three-place bundle resolution.

    **Why not put it in the installer.** It cannot fit. The `win-cuda` channel's Setup.exe was
    measured at 1,976,256,205 bytes against GitHub's 2 GiB asset limit — about 24 MB of headroom,
    and that is already after dropping the diariser's weights from that channel. The pack is a
    post-install download, which is the same shape the Python bundle itself already ships in.

    **The dist-info directories come too, and that is not cosmetic.** `importlib.metadata` resolves
    along `sys.path` like everything else, so without them a dependency asking torch's version reads
    the bundle's `+cpu` back while running the CUDA build. Verified 2026-08-28: with the pack ahead,
    `importlib.metadata.version('torch')` reports `2.13.0+cu130`.

    **The version must match the bundle's exactly.** The pack is built from the same
    `requirements-bundle.txt` with one line changed — the torch index from `whl/cpu` to a CUDA one —
    so the pinned *version* is unchanged and only the build differs. That is what keeps the
    translator's 8,149-sentence gate intact: `requirements-bundle.txt` names torch 2.13.0 as one of
    the three packages that decide the decode, and 2.13.0+cu130 is that version.

.EXAMPLE
    .\scripts\bundle-python-cuda.ps1 -Destination packaging\python-cuda
    Builds the pack into a directory ready to be zipped.

.EXAMPLE
    .\scripts\bundle-python-cuda.ps1 -Destination packaging\python-cuda -FromVenv C:\Users\me\pyannote-cuda-venv
    Copies out of an existing CUDA venv instead of running pip, which is much faster when one is
    already on the machine.
#>
param(
    # Where the pack goes. Becomes `<user data>/python-cuda`, so it holds `torch/` at its root.
    [Parameter(Mandatory = $true)]
    [string] $Destination,

    # An existing venv built from the CUDA requirements, to copy out of instead of running pip.
    [string] $FromVenv,

    # The interpreter pip is run from when there is no venv to copy. Must be the bundle's feature
    # version, because these wheels are cp312 and a pack built at another version cannot be imported.
    [string] $HostPython = 'python',

    # The PyTorch index to install from. Pinned as an argument rather than in the script because the
    # right one is a property of the driver on the target machine, not of this repository.
    [string] $TorchIndex = 'https://download.pytorch.org/whl/cu130',

    # Also zip the pack, split it into parts, and write the manifest a catalogue entry is made from.
    [switch] $Package,

    # Where -Package writes. Under packaging/ by default, which is gitignored for the same reason
    # runs/ is: one channel of this product is already over 800 MB and none of it is an input.
    [string] $PackageDirectory,

    # Bytes per part. 512 MiB by default, and the default is a decision rather than a round number:
    # **the whole zip was measured at 1,961,716,087 bytes on 2026-08-28, which clears GitHub's 2 GiB
    # asset limit by 177 MB.** That is the same trap the win-cuda channel is in at 24 MB, on an
    # artefact that only compresses to 66.2% because CUDA DLLs are already dense — so one torch point
    # release could put it over, and the failure would arrive at release time. Parts remove the
    # question, and they buy the thing that matters more for a 1.8 GB download over a domestic
    # connection: a dropped transfer resumes at a part boundary instead of at zero.
    [long] $PartSizeBytes = 512MB
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The three that differ. Kept here rather than derived by diffing two trees: a derived list would
# silently grow the day a dependency gained a CUDA build, and the pack's whole argument is that its
# contents are known. `PythonRuntime.CudaPackMarker` names the first of them on the C# side.
$PackPackages = @('torch', 'torchaudio', 'torchcodec')

function Write-Note { param([string] $Message) Write-Host "  $Message" }

$repo = Split-Path -Parent $PSScriptRoot
$requirements = Join-Path $repo 'python/requirements-bundle.txt'
if (-not (Test-Path -LiteralPath $requirements)) {
    throw "No requirements file at $requirements."
}

if (Test-Path -LiteralPath $Destination) {
    # Rebuilt rather than merged. A pack assembled over an older one would keep DLLs the new torch
    # no longer ships, and `torch/lib` is where a stale CUDA library would do the most damage.
    Write-Note "removing the existing $Destination"
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$Destination = (Resolve-Path -LiteralPath $Destination).Path

if ($FromVenv) {
    $site = Join-Path $FromVenv 'Lib/site-packages'
    if (-not (Test-Path -LiteralPath $site)) {
        throw "No site-packages under $FromVenv. Pass a venv built from the CUDA requirements, or " +
              "omit -FromVenv to install into a staging directory with pip."
    }
    $source = $site
} else {
    # A staging install rather than a venv, matching bundle-python.ps1's `pip install --target`:
    # nothing pip-shaped is wanted in the output, and only three of the installed packages are kept.
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("uindosill-cuda-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    $cudaRequirements = Join-Path $staging 'requirements-cuda.txt'
    (Get-Content -LiteralPath $requirements -Raw).Replace(
        'https://download.pytorch.org/whl/cpu', $TorchIndex) |
        Set-Content -LiteralPath $cudaRequirements -NoNewline

    Write-Note "installing $requirements against $TorchIndex (this downloads about 3 GB)"
    & $HostPython -m pip install --target $staging -r $cudaRequirements
    if ($LASTEXITCODE -ne 0) { throw 'pip install failed; the pack is incomplete.' }
    $source = $staging
}

# The three packages and their dist-info. Copied by name so that what lands in the pack is a
# decision rather than whatever the resolver happened to pull in.
foreach ($name in $PackPackages) {
    $from = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $from)) {
        throw "$name is missing from $source, so the pack would shadow the bundle with a hole."
    }
    Write-Note "copying $name"
    Copy-Item -LiteralPath $from -Destination (Join-Path $Destination $name) -Recurse -Force
}

$distInfo = Get-ChildItem -LiteralPath $source -Directory |
    Where-Object { $_.Name -match ('^(' + ($PackPackages -join '|') + ')-.*\.dist-info$') }
foreach ($info in $distInfo) {
    Copy-Item -LiteralPath $info.FullName -Destination (Join-Path $Destination $info.Name) -Recurse -Force
}

# **Read back rather than assumed**, on vendor-natives.ps1's terms: a pack whose torch is the CPU
# build looks exactly like one whose torch is not, until a run is slow for a reason nobody can see.
$versions = @{}
foreach ($info in $distInfo) {
    if ($info.Name -match '^(?<pkg>[^-]+)-(?<ver>[^-]+)\.dist-info$') {
        $versions[$Matches['pkg']] = $Matches['ver']
    }
}
$torchVersion = $versions['torch']
if (-not $torchVersion) {
    throw "No torch dist-info reached $Destination, so importlib.metadata would read the bundle's " +
          "CPU version back while running this build."
}
if ($torchVersion -notmatch '\+cu') {
    throw "The pack's torch is '$torchVersion', which is not a CUDA build. A pack of the CPU build " +
          "is 2.8 GB that changes nothing; check -TorchIndex and -FromVenv."
}

if (-not (Test-Path -LiteralPath (Join-Path $Destination 'torch/__init__.py'))) {
    throw "No torch/__init__.py at the root of $Destination. PythonRuntime.IsCudaPack looks for " +
          "exactly that, and would treat this directory as absent."
}

$bytes = (Get-ChildItem -LiteralPath $Destination -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ''
Write-Host ("  pack: {0:N2} GB at {1}" -f ($bytes / 1GB), $Destination)
foreach ($pkg in $PackPackages) {
    Write-Host ("     {0,-12} {1}" -f $pkg, $versions[$pkg])
}
Write-Host ''
Write-Host '  Point a run at it with UINDOSILL_PYTHON_CUDA, or unpack it as python-cuda beside the'
Write-Host '  bundle under %LOCALAPPDATA%\Uindosill. The diariser reports the device it elected.'

if (-not $Package) { return }

# ---- Packaging: one zip, split into digested parts, and the manifest a catalogue entry is made from.

if (-not $PackageDirectory) {
    $PackageDirectory = Join-Path $repo 'packaging/python-cuda'
}
if (Test-Path -LiteralPath $PackageDirectory) {
    Remove-Item -LiteralPath $PackageDirectory -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $PackageDirectory | Out-Null
$PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path

$zipName = 'uindosill-python-cuda-win-x64.zip'
$zipPath = Join-Path $PackageDirectory $zipName

Write-Host ''
Write-Note "compressing (about 45 s; CUDA DLLs only reach about 66%)"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $Destination, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipBytes = (Get-Item -LiteralPath $zipPath).Length
$wholeDigest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

# **Split by reading, not by any archive format's own multi-volume support.** A `.zip.001` produced
# this way is a byte range and nothing more, so the client reassembles by concatenation and needs no
# archive library that understands spanning — and each part carries its own digest, which is what
# makes a resumed download checkable rather than merely restartable.
$parts = [System.Collections.Generic.List[object]]::new()
$buffer = New-Object byte[] (4MB)
$input = [System.IO.File]::OpenRead($zipPath)
try {
    $index = 0
    while ($input.Position -lt $input.Length) {
        $index++
        $partName = '{0}.{1:d3}' -f $zipName, $index
        $partPath = Join-Path $PackageDirectory $partName
        $written = 0L
        $output = [System.IO.File]::Create($partPath)
        try {
            while ($written -lt $PartSizeBytes -and $input.Position -lt $input.Length) {
                $want = [math]::Min($buffer.Length, $PartSizeBytes - $written)
                $read = $input.Read($buffer, 0, $want)
                if ($read -le 0) { break }
                $output.Write($buffer, 0, $read)
                $written += $read
            }
        } finally { $output.Dispose() }

        $parts.Add([pscustomobject]@{
            fileName  = $partName
            sizeBytes = $written
            sha256    = (Get-FileHash -LiteralPath $partPath -Algorithm SHA256).Hash.ToLowerInvariant()
        })
        Write-Note ("part {0}: {1,12:N0} bytes" -f $index, $written)
    }
} finally { $input.Dispose() }

# The zip itself is not shipped — the parts are — so it is removed rather than left to be uploaded
# by mistake beside them, which would double the release's weight for nothing.
Remove-Item -LiteralPath $zipPath -Force

$reassembled = ($parts | Measure-Object sizeBytes -Sum).Sum
if ($reassembled -ne $zipBytes) {
    throw "The parts total $reassembled bytes and the zip was $zipBytes. The split lost data."
}

# **`[long]` on every byte count, and it is not belt and braces.** `Measure-Object -Sum` returns a
# Double, so `$bytes` serialises as `2965027252.0` — which `System.Text.Json` refuses to read as an
# Int64, and the refusal arrives in `CudaPackManifest.Parse` rather than here. Caught 2026-08-29 by
# driving the installer against a manifest this script had written.
$manifest = [ordered]@{
    archiveName     = $zipName
    archiveBytes    = [long] $zipBytes
    archiveSha256   = $wholeDigest
    unpackedBytes   = [long] $bytes
    torchVersion    = $torchVersion
    packages        = [ordered]@{}
    parts           = $parts
}
foreach ($pkg in $PackPackages) { $manifest.packages[$pkg] = $versions[$pkg] }
$manifestPath = Join-Path $PackageDirectory 'manifest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8

Write-Host ''
Write-Host ("  zip   {0,15:N0} bytes ({1:N2} GB), {2:P1} of unpacked" -f $zipBytes, ($zipBytes/1GB), ($zipBytes/$bytes))
Write-Host ("  parts {0,15:N0} in {1} file(s) of at most {2:N0} bytes" -f $reassembled, $parts.Count, $PartSizeBytes)
Write-Host ("  sha256 {0}" -f $wholeDigest)
Write-Host ("  manifest -> {0}" -f $manifestPath)
