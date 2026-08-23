<#
.SYNOPSIS
    Fetches the pinned yt-dlp and Deno binaries, verifies each against its recorded SHA-256, and
    puts them in native/win-x64/tools/ — where BundledTools looks and the build copies from.

.DESCRIPTION
    The same procedure and the same reasons as scripts/vendor-natives.ps1 and vendor-mpv.ps1: pinned
    to a release rather than fetched at build time, verified by digest before anything is unpacked,
    and the digests trusted here must also appear in docs/NATIVE-BINARIES.md or the run fails.

    These two are what let the application take a link instead of a file. yt-dlp downloads the audio
    track so it can be transcribed; the Ask tab streams the picture from the same link through mpv,
    which spawns this same yt-dlp.

    **Deno is not optional and not a preference.** yt-dlp needs a JavaScript runtime to answer
    YouTube's signature challenge, and its own documentation enables exactly one by default:
    "Supported runtimes are (in order of priority, from highest to lowest): deno, node, quickjs,
    bun. Only 'deno' is enabled by default." Without it YouTube extraction degrades or fails
    outright, so a drop with yt-dlp and no Deno is a half-drop, and BundledTools reports it as one.

    **Both are permissively licensed** — yt-dlp is Unlicense (public domain) and Deno is MIT — so
    unlike libmpv neither changes what the application may be distributed as. Their notices still
    travel: this script writes them beside the binaries and refuses to finish without them, for the
    same reason the other two vendoring scripts do.

    yt-dlp publishes a SHA2-256SUMS file beside the release and this script checks against its own
    pin rather than against that file, deliberately: a digest fetched from the same place as the
    binary proves only that the two agree. The pin here was compared against upstream's sums by
    hand when it was taken, on 2026-08-23, and both are recorded in docs/NATIVE-BINARIES.md.

.EXAMPLE
    .\scripts\vendor-tools.ps1
    Downloads about 60 MB and leaves about 115 MB on disk.
#>

[CmdletBinding()]
param(
    [string] $ArchiveDirectory,
    [string] $NativeRoot,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $NativeRoot) { $NativeRoot = Join-Path $repoRoot 'native' }
if (-not $ArchiveDirectory) { $ArchiveDirectory = Join-Path $NativeRoot 'archives' }

# ── The pins ───────────────────────────────────────────────────────────────────────────────────
$ytDlpVersion = '2026.08.19'
$denoVersion  = 'v2.9.5'

# BtbN publishes a dated autobuild per day; the tag is the pin, and the asset inside it names the
# FFmpeg release branch rather than a master snapshot. n9.0.1 is deliberate: it is the version every
# container rule in `Parakeet.Core.Muxing.SubtitleMux` was measured against.
#
# **LGPL and not the GPL build beside it, and that is a licence decision rather than a preference.**
# Adding a transcript to a media file copies streams and encodes nothing, so nothing here needs a
# GPL-only encoder — the three subtitle codecs and the two muxers involved are all core FFmpeg. The
# GPL build ships GPLv3, which this project has no reason to take on; the LGPL one is LGPLv3, 30 MB
# smaller, and was driven over all eight input-and-format routes before it was kept.
$ffmpegBuild   = 'autobuild-2026-08-22-12-58'
$ffmpegVersion = 'n9.0.1-6-g9d4ca21220'

$tools = @(
    [PSCustomObject]@{
        Name       = 'yt-dlp.exe'
        Url        = "https://github.com/yt-dlp/yt-dlp/releases/download/$ytDlpVersion/yt-dlp.exe"
        Download   = "yt-dlp-$ytDlpVersion.exe"
        Archive    = $false
        Length     = 17840399
        Sha256     = '66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A'
        FileLength = 17840399
        FileSha256 = '66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A'
        Notice     = 'yt-dlp-LICENSE.txt'
    },
    [PSCustomObject]@{
        Name       = 'deno.exe'
        Url        = "https://github.com/denoland/deno/releases/download/$denoVersion/deno-x86_64-pc-windows-msvc.zip"
        Download   = "deno-$denoVersion-x86_64-pc-windows-msvc.zip"
        Archive    = $true
        Length     = 42691248
        Sha256     = '171EFAB55AC6B9881FD53EE4C20F8BF3BB1340FFC618483746909014DB12216A'
        FileLength = 97408288
        FileSha256 = '98F8C2A2D470E4CCB04C935C86FF8050817D877762AEC5EAEEB9E409CCB3B9FD'
        Notice     = 'deno-LICENSE.txt'
    },
    [PSCustomObject]@{
        Name       = 'ffmpeg.exe'
        Url        = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$ffmpegBuild/ffmpeg-$ffmpegVersion-win64-lgpl-9.0.zip"
        Download   = "ffmpeg-$ffmpegVersion-win64-lgpl-9.0.zip"
        Archive    = $true
        # Nested, unlike Deno's flat zip: this one holds ffmpeg-<version>/bin/ffmpeg.exe, so the
        # extraction has to recurse. `7z e` flattens whatever it finds, which is what we want here.
        Nested     = $true
        Length     = 147007729
        Sha256     = '20F84639FAE87181BB1C9899C34CE05CD3C0B533C68D3FF34206A2615DA94F30'
        FileLength = 114400768
        FileSha256 = '8A5CE69FBB74B4C9E0E24C214E3DEF0E1847A05051A8E1C6D10B1D4A35BD6A65'
        Notice     = 'ffmpeg-LICENSE.txt'
        # Its own directory, and not for tidiness. yt-dlp looks for ffmpeg beside its own executable
        # before it looks at PATH — measured 2026-08-23 — so putting the muxer in tools/ would
        # silently change what a download produces. Nothing needs yt-dlp to have one.
        Directory  = 'ffmpeg'
    }
)

function Write-Heading([string] $Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('-' * $Text.Length) -ForegroundColor DarkGray
}

function Get-Sha256([string] $Path) {
    (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

Write-Heading "yt-dlp $ytDlpVersion, Deno $denoVersion and ffmpeg $ffmpegVersion"

$null = New-Item -ItemType Directory -Force -Path $ArchiveDirectory

$licenceSource = Join-Path $repoRoot 'licences'

foreach ($tool in $tools) {
    $downloadPath = Join-Path $ArchiveDirectory $tool.Download

    # Per tool, because ffmpeg deliberately does not live beside yt-dlp — see its pin above.
    $leaf = if ($tool.PSObject.Properties.Name -contains 'Directory') { $tool.Directory } else { 'tools' }
    $target = Join-Path (Join-Path $NativeRoot 'win-x64') $leaf
    $null = New-Item -ItemType Directory -Force -Path $target

    # ── 1. Find or fetch ───────────────────────────────────────────────────────────────────────
    if (-not (Test-Path $downloadPath)) {
        Write-Host "  downloading $($tool.Download) ..."

        $staging = "$downloadPath.partial"
        Invoke-WebRequest -Uri $tool.Url -OutFile $staging -UseBasicParsing

        $stagedDigest = Get-Sha256 $staging
        if ($stagedDigest -ne $tool.Sha256) {
            Remove-Item $staging -Force
            throw "The download's SHA-256 is $stagedDigest, not the pinned $($tool.Sha256). Nothing was unpacked."
        }

        Move-Item $staging $downloadPath -Force
    }

    # ── 2. Verify ──────────────────────────────────────────────────────────────────────────────
    $actualLength = (Get-Item $downloadPath).Length
    if ($actualLength -ne $tool.Length) {
        throw "$($tool.Download) is $actualLength bytes, not the pinned $($tool.Length)."
    }

    $actualDigest = Get-Sha256 $downloadPath
    if ($actualDigest -ne $tool.Sha256) {
        throw "$($tool.Download) hashes to $actualDigest, not the pinned $($tool.Sha256)."
    }

    Write-Host "  $($tool.Download) ok - $actualLength bytes"

    # ── 3. Put it where the application looks ──────────────────────────────────────────────────
    $toolPath = Join-Path $target $tool.Name

    if ((Test-Path $toolPath) -and -not $Force) {
        $existing = (Get-Item $toolPath).Length
        if ($existing -ne $tool.FileLength) {
            throw "$toolPath is $existing bytes, not the expected $($tool.FileLength). Pass -Force to overwrite."
        }
    }

    if ($tool.Archive) {
        if (-not (Get-Command 7z -ErrorAction SilentlyContinue)) {
            throw "7z is not on PATH and $($tool.Download) is an archive. Install it (scoop install 7zip) and run again."
        }

        $sevenZipArguments = @('e', '-y', "-o$target", $downloadPath, $tool.Name)
        if (($tool.PSObject.Properties.Name -contains 'Nested') -and $tool.Nested) {
            $sevenZipArguments += '-r'
        }

        & 7z @sevenZipArguments | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "7z exited $LASTEXITCODE extracting $($tool.Name)."
        }
    }
    else {
        Copy-Item $downloadPath $toolPath -Force
    }

    # ── 4. Read the result back ────────────────────────────────────────────────────────────────
    if (-not (Test-Path $toolPath)) {
        throw "$($tool.Name) is not in $target after unpacking."
    }

    $unpackedLength = (Get-Item $toolPath).Length
    $unpackedDigest = Get-Sha256 $toolPath

    if ($unpackedLength -ne $tool.FileLength -or $unpackedDigest -ne $tool.FileSha256) {
        throw "$($tool.Name) is $unpackedLength bytes / $unpackedDigest, not the pinned $($tool.FileLength) / $($tool.FileSha256)."
    }

    Write-Host "  $($tool.Name) ok - $unpackedLength bytes, $unpackedDigest"

    # ── 5. Its notice, which is not optional ───────────────────────────────────────────────────
    $noticeSource = Join-Path $licenceSource $tool.Notice
    if (-not (Test-Path $noticeSource)) {
        throw "licences/$($tool.Notice) is missing, and $($tool.Name) may not be redistributed without it."
    }

    Copy-Item $noticeSource (Join-Path $target $tool.Notice) -Force
}

Write-Host "  notices ok - $(($tools.Notice) -join ', ')"

# ── 6. The pins must be recorded where a reader looks ──────────────────────────────────────────
$document = Join-Path $repoRoot 'docs/NATIVE-BINARIES.md'
$documentText = Get-Content $document -Raw

foreach ($digest in @($tools.Sha256) + @($tools.FileSha256)) {
    if ($documentText -notmatch [regex]::Escape($digest)) {
        throw "The digest $digest is trusted by this script and does not appear in docs/NATIVE-BINARIES.md. Record it there."
    }
}

Write-Host "  digests match docs/NATIVE-BINARIES.md"

Write-Heading 'Done'
Write-Host "  $target"
Write-Host ''
Write-Host '  Rebuild before running: NativeAssets.targets evaluates its glob at project evaluation,' -ForegroundColor Yellow
Write-Host '  so a drop made after the last build is not in the output until the next one.' -ForegroundColor Yellow
