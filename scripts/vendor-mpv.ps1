<#
.SYNOPSIS
    Fetches the pinned libmpv Windows build, verifies it against its recorded SHA-256, and unpacks
    it into native/win-x64/mpv/ — the layout MpvNativeLibrary searches and the build copies.

.DESCRIPTION
    The same procedure, and the same reasons, as scripts/vendor-natives.ps1: the binary is pinned to
    a release rather than fetched at build time, every download is checked by digest before anything
    is unpacked, and the digests this script trusts must also appear in docs/NATIVE-BINARIES.md or
    the run fails. See that document for why pinning rather than tracking.

    What is different here is the licence, and it is the reason this is a separate script rather
    than a fourth backend in the other one.

    **libmpv is GPLv2+ and this build links GPL components.** Vendoring it makes the application's
    distribution GPLv2+ — see docs/LICENSING.md, which records what that obliges. Two consequences
    are enforced here rather than left to a reader:

      · The notice files are not optional and not "documentation". GPLv2 §1 requires the licence
        text to travel with the binary, and §3 requires the corresponding source to be available.
        This script writes licences/GPL-2.0.txt, licences/mpv-Copyright.txt and
        licences/mpv-WRITTEN-OFFER.txt into native/win-x64/mpv/ beside the DLL, and refuses to
        finish if any is missing. Unpacking only the DLL is a licence breach that fails silently,
        which is exactly the failure the parakeet.cpp LICENSE check exists to prevent.

      · The upstream archive contains no licence text at all — only libmpv-2.dll and the headers.
        So the notices come from this repository's licences/ directory, which is where they are
        version-controlled, rather than from the archive. If they are not there, this says so.

    The archive is a .7z, which needs 7z on PATH; the parakeet archives are .zip and needed
    nothing. That is upstream's choice of format, not ours.

.EXAMPLE
    .\scripts\vendor-mpv.ps1
    Downloads about 31 MB and unpacks a 114 MB DLL.

.EXAMPLE
    .\scripts\vendor-mpv.ps1 -Force
    Overwrites a libmpv-2.dll already there. Without this, one whose size differs from the archive's
    is refused rather than silently kept.
#>

[CmdletBinding()]
param(
    # Where the downloaded .7z is kept, so a re-run verifies instead of re-downloading.
    [string] $ArchiveDirectory,

    # The native/ root to unpack into. Defaults to the repository's.
    [string] $NativeRoot,

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $NativeRoot) { $NativeRoot = Join-Path $repoRoot 'native' }
if (-not $ArchiveDirectory) { $ArchiveDirectory = Join-Path $NativeRoot 'archives' }

# ── The pin ────────────────────────────────────────────────────────────────────────────────────
# shinchiro/mpv-winbuild-cmake publishes a dated release; the mpv commit is in the file name. The
# digests are recorded in docs/NATIVE-BINARIES.md and step 5 holds the two together.
$release = '20260814'
$mpvCommit = '7b8915bc1d'
$archiveName = "mpv-dev-x86_64-$release-git-$mpvCommit.7z"
$archiveUrl = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$release/$archiveName"
$archiveLength = 31181976
$archiveSha256 = '0AF22B28E920620036D3AE08FD9283156DC9AF0420BF4DF84B0E02282094599C'

$libraryName = 'libmpv-2.dll'
$libraryLength = 119757824
$librarySha256 = 'F709C7CA8B183BEC76B8158BF0C45C53018C63366750729352612F228FF7BDEA'

# The notices that must land beside the binary. Sources, not decoration — see the header.
$notices = @('GPL-2.0.txt', 'mpv-Copyright.txt', 'mpv-WRITTEN-OFFER.txt')

function Write-Heading([string] $Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('─' * $Text.Length) -ForegroundColor DarkGray
}

function Get-Sha256([string] $Path) {
    (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

Write-Heading "libmpv $release (mpv $mpvCommit)"

# ── 1. Find or fetch the archive ───────────────────────────────────────────────────────────────
$null = New-Item -ItemType Directory -Force -Path $ArchiveDirectory
$archivePath = Join-Path $ArchiveDirectory $archiveName

if (-not (Test-Path $archivePath)) {
    Write-Host "  downloading $archiveName …"

    # Lands under a temporary name and is renamed only after it verifies, so a file carrying the
    # real name has passed step 2 at least once.
    $staging = "$archivePath.partial"
    Invoke-WebRequest -Uri $archiveUrl -OutFile $staging -UseBasicParsing

    $stagedDigest = Get-Sha256 $staging
    if ($stagedDigest -ne $archiveSha256) {
        Remove-Item $staging -Force
        throw "The download's SHA-256 is $stagedDigest, not the pinned $archiveSha256. Nothing was unpacked."
    }

    Move-Item $staging $archivePath -Force
}

# ── 2. Verify ──────────────────────────────────────────────────────────────────────────────────
$actualLength = (Get-Item $archivePath).Length
if ($actualLength -ne $archiveLength) {
    throw "$archiveName is $actualLength bytes, not the pinned $archiveLength. A wrong archive on disk is a question, not a drop."
}

$actualDigest = Get-Sha256 $archivePath
if ($actualDigest -ne $archiveSha256) {
    throw "$archiveName hashes to $actualDigest, not the pinned $archiveSha256."
}

Write-Host "  archive ok — $actualLength bytes, $actualDigest"

# ── 3. Unpack the one file that matters ────────────────────────────────────────────────────────
$target = Join-Path (Join-Path $NativeRoot 'win-x64') 'mpv'
$null = New-Item -ItemType Directory -Force -Path $target
$libraryPath = Join-Path $target $libraryName

if ((Test-Path $libraryPath) -and -not $Force) {
    $existing = (Get-Item $libraryPath).Length
    if ($existing -ne $libraryLength) {
        throw "$libraryPath is $existing bytes, not the archive's $libraryLength. Pass -Force to overwrite; silently keeping a stale libmpv is the failure this refuses."
    }
}

if (-not (Get-Command 7z -ErrorAction SilentlyContinue)) {
    throw "7z is not on PATH, and upstream publishes this as a .7z. Install it (scoop install 7zip) and run again."
}

# The archive is flat — libmpv-2.dll and include/ at its root — so one file is extracted rather
# than the lot: the headers are documentation and the build has no use for them.
& 7z e -y -o"$target" $archivePath $libraryName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "7z exited $LASTEXITCODE extracting $libraryName."
}

# ── 4. Read the result back ────────────────────────────────────────────────────────────────────
if (-not (Test-Path $libraryPath)) {
    throw "$libraryName is not in $target after unpacking."
}

$unpackedLength = (Get-Item $libraryPath).Length
$unpackedDigest = Get-Sha256 $libraryPath

if ($unpackedLength -ne $libraryLength) {
    throw "$libraryName unpacked to $unpackedLength bytes, not the pinned $libraryLength."
}
if ($unpackedDigest -ne $librarySha256) {
    throw "$libraryName hashes to $unpackedDigest, not the pinned $librarySha256."
}

Write-Host "  $libraryName ok — $unpackedLength bytes, $unpackedDigest"

# ── 5. The notices, which are not optional ─────────────────────────────────────────────────────
# GPLv2 §1 wants the licence text with the binary and §3 wants the source reachable. The upstream
# archive carries neither, so both come from licences/ in this repository.
$licenceSource = Join-Path $repoRoot 'licences'

foreach ($notice in $notices) {
    $from = Join-Path $licenceSource $notice
    if (-not (Test-Path $from)) {
        throw "licences/$notice is missing. libmpv is GPLv2+ and may not be redistributed without it — see docs/LICENSING.md."
    }

    Copy-Item $from (Join-Path $target $notice) -Force
}

Write-Host "  notices ok — $($notices -join ', ')"

# ── 6. The pins must be recorded where a reader looks ──────────────────────────────────────────
$document = Join-Path $repoRoot 'docs/NATIVE-BINARIES.md'
$documentText = Get-Content $document -Raw

foreach ($digest in @($archiveSha256, $librarySha256)) {
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
