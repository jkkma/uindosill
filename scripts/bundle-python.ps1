<#
.SYNOPSIS
    Assembles the Python the installer ships: a pinned embeddable CPython, the pinned packages, and
    the uindosill_engines source, laid out where PythonRuntime looks for them.

.DESCRIPTION
    The diariser and the translator run out of process in this interpreter (docs/PHASES.md,
    *Decided 2026-08-21*). `PythonRuntime.Resolve` looks for `<app>/python/python.exe` and a
    `uindosill_engines` package under `<app>/python`, and this script is what puts them there —
    deliberately not PATH, and deliberately not an installer step that asks the user for a Python.

    **The embeddable distribution, not the installer.** python.org publishes a zip with no
    registry entry, no launcher, no uninstaller and no pip — which is exactly what a bundled
    interpreter should be. Its byte count and SHA-256 are checked before anything is unpacked, on
    `vendor-natives.ps1`'s terms: a pinned artefact whose hash is not checked is a pin in name only.

    **Two things the embeddable distribution needs before it can import anything.**

      * Its `pythonXY._pth` file replaces the normal path machinery. `Lib\site-packages` is added
        to it as a line, which is enough — a `._pth` entry goes on `sys.path` directly.
        **`import site` is deliberately NOT added**, though it is the usual advice: it would also
        turn the *user* site directory back on, so a bundled interpreter would execute whatever
        `.pth` files and `usercustomize.py` the person running it happens to have under
        `%APPDATA%\Python`. Anything those print goes to stdout, and stdout here is the protocol.
        Verified 2026-08-21 on an assembled bundle: without it `site.ENABLE_USER_SITE` is `None`
        and every engine import, the real translator load and its parity check all still pass.
      * It has no pip. Rather than bootstrap one inside the bundle — which leaves pip, setuptools
        and a wheel cache in a directory a user receives — the packages are installed from the
        *host* interpreter with `pip install --target`. Nothing pip-shaped ends up in the bundle.

    **A `._pth` interpreter ignores `PYTHONPATH` entirely**, which the host sets to the package root.
    That is not a bug here and is worth knowing before it looks like one: a shipped bundle should run
    the code it shipped with, and `.` in the `._pth` is what finds `uindosill_engines` beside
    `python.exe`. What it means is that `UINDOSILL_PYTHON_PACKAGES` only bites on a *venv*
    interpreter, which is the development case it exists for — see `PythonRuntime`.

    That second choice is why `-HostPython` must be the same feature version as the embeddable one.
    `--target` resolves wheels for the interpreter running pip, so a 3.13 host would fetch `cp313`
    wheels into a `cp312` bundle and the failure would be an ImportError on a user's machine.
    The check below refuses rather than warns — and `.github/workflows/release.yml` pins the runner's
    Python to match, because a release job that fails on the version of an interpreter nobody chose
    is a release job that fails for a reason nobody can read.

    **It is verified by being run.** The last step starts the bundle exactly as the .NET host does —
    `python.exe -u -m uindosill_engines` with `PYTHONPATH` set — and completes the handshake over
    the real protocol. A bundle that assembles but cannot answer `hello` is a bundle that fails on
    a user's machine instead of here.

    **What it does not do is choose a size.** Measured 2026-08-21 the package set is about 1.3 GB
    on disk, which is not the ~0.55 GB the migration budgeted: the estimate counted
    onnxruntime-webgpu and CPU torch and missed the transitive set. `python/requirements-bundle.txt`
    says where it goes, and docs/UNPROVEN.md carries the gap.

.EXAMPLE
    .\scripts\bundle-python.ps1 -Destination packaging\win\publish\python
    Assemble into a publish directory, ready for vpk.

.EXAMPLE
    .\scripts\bundle-python.ps1 -Destination out\python -SkipPackages
    The interpreter and the source alone — seconds rather than minutes, for checking the layout.
#>

[CmdletBinding()]
param(
    # Where the bundle goes. Becomes `<app>/python`, so it holds python.exe at its root.
    [Parameter(Mandatory = $true)]
    [string] $Destination,

    # The interpreter pip is run from. Must be the same feature version as the embeddable one below.
    [string] $HostPython = 'python',

    # Skip the package install. The layout and the handshake still run, and the handshake still
    # passes: importing uindosill_engines costs nothing until a model is asked for, which is a
    # property of the sidecar worth checking on its own.
    [switch] $SkipPackages,

    # Reuse an already-downloaded embeddable zip instead of fetching it.
    [string] $ArchivePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The pin. 3.12.10 because that is what every measurement in docs/UNPROVEN.md was taken on, and
# because the wheels named in requirements-bundle.txt are published for cp312.
#
# The hash was taken from the file this script downloads, on 2026-08-21, and is recorded here rather
# than in a document because this is the only thing that reads it. Bumping the version means
# bumping both lines and re-running every figure that names an interpreter.
$pythonVersion = '3.12.10'
$archiveName   = "python-$pythonVersion-embed-amd64.zip"
$archiveUrl    = "https://www.python.org/ftp/python/$pythonVersion/$archiveName"
$archiveBytes  = 11133606
$archiveSha256 = '4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3'

$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'python/uindosill_engines'
$requirements = Join-Path $repo 'python/requirements-bundle.txt'

function Write-Step([string] $Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Note([string] $Text) { Write-Host "   $Text" -ForegroundColor DarkGray }

if (-not (Test-Path -LiteralPath $source)) {
    throw "$source is not there. This script bundles that package; without it there is nothing to ship."
}

# -- the host interpreter, and the one check that stops a wheel-tag mismatch reaching a user -------

Write-Step 'Host interpreter'

$hostVersion = & $HostPython -c 'import sys; print("%d.%d" % sys.version_info[:2])'
if ($LASTEXITCODE -ne 0) {
    throw "Could not run '$HostPython'. Pass -HostPython with a path to a CPython $pythonVersion interpreter."
}

$wanted = ($pythonVersion -split '\.')[0..1] -join '.'
if ($hostVersion.Trim() -ne $wanted) {
    throw "The host interpreter is $($hostVersion.Trim()) and the bundle is $wanted. `pip install --target` " +
          "resolves wheels for the interpreter running it, so this would put wheels built for one ABI into a " +
          "bundle with another — an ImportError on a user's machine rather than an error here. Pass " +
          "-HostPython with a CPython $wanted."
}
Write-Note "$HostPython is CPython $($hostVersion.Trim())"

# -- the interpreter ------------------------------------------------------------------------------

Write-Step "Embeddable CPython $pythonVersion"

if (-not $ArchivePath) {
    $ArchivePath = Join-Path ([System.IO.Path]::GetTempPath()) $archiveName
    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        Write-Note "downloading $archiveUrl"
        Invoke-WebRequest -Uri $archiveUrl -OutFile $ArchivePath -UseBasicParsing
    }
    else {
        Write-Note "already downloaded: $ArchivePath"
    }
}

# Both, and in this order. The length is what catches a truncated download or an error page saved as
# a zip; the hash is what catches everything else. Checking only the hash would still be correct and
# would spend a second hashing 11 MB of HTML to say so.
$actualBytes = (Get-Item -LiteralPath $ArchivePath).Length
if ($actualBytes -ne $archiveBytes) {
    throw "$ArchivePath is $actualBytes bytes and the pin is $archiveBytes. Not the archive this script pins."
}

$actualSha = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha -ne $archiveSha256) {
    throw "$ArchivePath hashes to $actualSha and the pin is $archiveSha256. Refusing to unpack it."
}
Write-Note "verified: $archiveBytes bytes, sha256 $archiveSha256"

if (Test-Path -LiteralPath $Destination) {
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

Expand-Archive -LiteralPath $ArchivePath -DestinationPath $Destination -Force

$interpreter = Join-Path $Destination 'python.exe'
if (-not (Test-Path -LiteralPath $interpreter)) {
    throw "No python.exe under $Destination after unpacking. The archive's layout is not what this script expects."
}

# -- the path file, which is the whole difference between an embeddable Python and a usable one ----

Write-Step 'Path configuration'

$pth = Get-ChildItem -LiteralPath $Destination -Filter 'python*._pth' | Select-Object -First 1
if (-not $pth) {
    throw "No ._pth file in $Destination. Without one this is a normal interpreter and would read the user's " +
          "own site-packages, which is exactly what bundling is for avoiding."
}

# A `._pth` line is added to sys.path directly, so naming the directory is all that is needed — and
# `import site`, which is the usual advice, is left commented out on purpose. See this file's header:
# it would also enable the user site directory, and a bundled interpreter running somebody's
# `usercustomize.py` is a bundled interpreter that can print to the protocol's channel.
$lines = @(Get-Content -LiteralPath $pth.FullName | Where-Object { $_.Trim() -ne '' -and $_ -notmatch '^\s*#' })
if ($lines -contains 'import site') {
    throw "$($pth.Name) already enables site. That would put the user's own site-packages on this " +
          "bundle's path; refusing rather than quietly leaving it."
}
if ($lines -notcontains 'Lib\site-packages') { $lines += 'Lib\site-packages' }
Set-Content -LiteralPath $pth.FullName -Value $lines -Encoding ascii
Write-Note "$($pth.Name): $($lines -join ', ')"

$sitePackages = Join-Path $Destination 'Lib/site-packages'
New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null

# -- the packages ----------------------------------------------------------------------------------

if ($SkipPackages) {
    Write-Step 'Packages: skipped'
}
else {
    Write-Step 'Packages'
    Write-Note "installing $requirements into $sitePackages"

    # --only-binary rather than allowing source distributions generally. A wheel that had to be
    # built would be built by and for the HOST, and a bundle is not the machine that built it.
    #
    # **Two packages are exempt, by name, and the list is the point.** Both arrive with the second
    # diariser and neither has ever published a wheel:
    #
    #   * `docopt` -- one module, MIT, untouched since 2014, declared by `pyannote.metrics`.
    #   * `antlr4-python3-runtime` -- `omegaconf` pins `==4.9.*`, and antlr4's wheels start at 4.11.
    #     Every omegaconf release from 2.2.2 to 2.3.1 pins the same range, so there is no version of
    #     it that avoids this.
    #
    # **Both are pure Python, which is what makes the exemption safe rather than merely necessary.**
    # The rule above exists because a wheel built here would be built *for this host* -- an ABI, a
    # Python version, a compiler. Neither of these compiles anything, so the artefact a source build
    # produces is the same on every machine. A package with an extension module must not be added to
    # this list; it would need a wheel, or it does not ship.
    #
    # Found by resolving rather than by asking PyPI which packages look wheel-less: pip reports one
    # missing wheel at a time, and the first attempt at this checked each package's *latest* release
    # and missed antlr4 entirely, because what matters is the version the resolver settles on.
    $sdistAllowed = @('docopt', 'antlr4-python3-runtime')

    # `--only-binary :all:` demands wheels for everything; `--no-binary <name>` then names the ones
    # to build from source instead. pip applies the narrower flag to those packages, which is how
    # "wheels for all but these" is expressed -- the two cannot be combined the other way round.
    $pipArgs = @('-m', 'pip', 'install', '--disable-pip-version-check', '--only-binary', ':all:')
    foreach ($name in $sdistAllowed) { $pipArgs += @('--no-binary', $name) }
    $pipArgs += @('--target', $sitePackages, '-r', $requirements)

    & $HostPython @pipArgs
    if ($LASTEXITCODE -ne 0) { throw 'pip install failed; the bundle is incomplete.' }
}

# -- the source ------------------------------------------------------------------------------------

Write-Step 'uindosill_engines'

$engines = Join-Path $Destination 'uindosill_engines'
Copy-Item -LiteralPath $source -Destination $engines -Recurse -Force

# Compiled caches from a developer's tree are not the bundle's to carry: they are keyed to an
# absolute path that will not exist on a user's machine, and CPython rewrites them on first import
# anyway.
Get-ChildItem -LiteralPath $engines -Recurse -Directory -Filter '__pycache__' |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }

foreach ($required in @(
    'uindosill_engines/serve.py',
    'uindosill_engines/protocol.py',
    'uindosill_engines/diariser/parity-reference.npy',
    # The second diariser's speaker embedder has its own reference and its own gate, on the same
    # terms as the three beside it: without the file the check reports "no reference committed" and
    # a run continues on an unverified embedder. Added 2026-08-27 — it shipped for a day on neither
    # this list nor package-windows.ps1's, which an adversarial review caught.
    'uindosill_engines/diariser/embedding-parity-reference.npy',
    'uindosill_engines/translator/parity-reference.json',
    'uindosill_engines/translator/parity-sources.json',
    'uindosill_engines/_vendor/nemo/collections/asr/modules/sortformer_modules.py')) {
    $path = Join-Path $Destination $required
    if ((-not (Test-Path -LiteralPath $path)) -or ((Get-Item -LiteralPath $path).Length -eq 0)) {
        # The parity references are on this list on purpose. Without one, the check that stands
        # between a user and a silently wrong execution provider reports "not available" and the run
        # proceeds — which is the failure looking exactly like success again.
        throw "$required is missing or empty in the bundle."
    }
}

# -- the check that makes this a verification rather than a copy -------------------------------------

Write-Step 'Handshake'

$handshake = @'
import json, os, subprocess, sys
root = sys.argv[1]
child = subprocess.Popen(
    [os.path.join(root, "python.exe"), "-u", "-m", "uindosill_engines"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    env={**os.environ, "PYTHONPATH": root, "PYTHONIOENCODING": "utf-8"},
    text=True, encoding="utf-8")
out, err = child.communicate('{"id":1,"op":"hello"}\n{"id":2,"op":"shutdown"}\n', timeout=120)
lines = [l for l in out.splitlines() if l.strip()]
if not lines:
    sys.exit("the bundle produced no protocol output.\n" + err[-4000:])
hello = json.loads(lines[0])
expected = int(sys.argv[2])
if hello.get("protocol") != expected:
    sys.exit(f"the bundle speaks protocol {hello.get('protocol')}, not {expected}.\n" + err[-4000:])
print(json.dumps(hello))
'@

$handshakeScript = Join-Path ([System.IO.Path]::GetTempPath()) 'uindosill-bundle-handshake.py'
Set-Content -LiteralPath $handshakeScript -Value $handshake -Encoding utf8

# **The expected protocol number is read from the source, never written here.** It was a literal
# 1 in the handshake until 2026-08-26, when the diariser gained a second engine and
# PROTOCOL_VERSION became 2: the bundle assembled correctly, started correctly, answered "2", and
# this script rejected it for not being the number a second copy of the fact still said. That copy
# is the one nobody would remember to bump, which is why there is no longer one.
#
# Read from python/uindosill_engines/protocol.py rather than from the assembled bundle: the bundle
# is built from this repository moments earlier, so asking it would only ever agree with itself.
# What this check is worth catching is a stale bundle at $Destination that the copy did not
# overwrite, and only the repository can say what the number should have been.
$protocolSource = Get-Content -LiteralPath (Join-Path $repo 'python/uindosill_engines/protocol.py') -Raw
if ($protocolSource -notmatch '(?m)^PROTOCOL_VERSION\s*=\s*(\d+)\s*$') {
    throw "PROTOCOL_VERSION was not found in protocol.py; the handshake has nothing to check against."
}
$expectedProtocol = [int] $Matches[1]
Write-Note "expecting protocol $expectedProtocol"

$reply = & $HostPython $handshakeScript (Resolve-Path -LiteralPath $Destination).Path $expectedProtocol
if ($LASTEXITCODE -ne 0) { throw "The assembled bundle did not answer the handshake." }
Write-Note $reply

$size = (Get-ChildItem -LiteralPath $Destination -Recurse -File | Measure-Object -Property Length -Sum).Sum
$files = @(Get-ChildItem -LiteralPath $Destination -Recurse -File).Count

Write-Step 'Done'
Write-Host ("   {0}: {1:N0} files, {2:N2} GB" -f $Destination, $files, ($size / 1GB))
