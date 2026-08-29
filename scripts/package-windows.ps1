<#
.SYNOPSIS
    Builds the Windows installer for the desktop application with Velopack, one channel at a time,
    and reads back what it produced.

.DESCRIPTION
    Phase 5's shape was decided on 2026-08-16 (docs/PHASES.md, *Decisions taken*) and this script is
    that decision, executed:

      * **Two channels from one publish.** The default channel `win` carries the cpu and vulkan
        natives; `win-cuda` carries those plus the opt-in CUDA drop. The choice is made at download
        time, which is what keeps ~730 MB of NVIDIA runtime out of the download almost everybody
        wants. Velopack records the channel a release was packed with, and an installed copy asks
        for its own channel without being told — so a CUDA user is never moved onto the default
        flavour by an update. Since 2026-08-24 both channels also carry the second native stack
        under native/<rid>/llm/ — the Ask panel's engine: the vulkan `llama-server` drop in the
        default channel, and (decided later the same day, after the gap was priced on the 5080)
        the CUDA drop with its cudart-13.3 in win-cuda — per the $llmBackendsFor table below and
        the decisions recorded beside it.
      * **The desktop application only.** The CLI ships as the zip beside it on the release, which
        is the CI artefact as it already exists. Velopack has no PATH feature, so putting the CLI in
        the installer would be custom code on install and on uninstall.
      * **A third artefact since 2026-08-21: the bundled Python, zipped on its own.** The installer
        carries a copy inside its publish and the CLI zip carries none, so a command-line user gets
        the interpreter here and unpacks it into `%LOCALAPPDATA%\Uindosill`, where `PythonRuntime`
        looks. It is packed once from the first channel's publish — both channels assemble the same
        bundle — and read back like everything else.
      * **Unsigned.** v1.0 ships without a signing identity; `--signParams` and `--signTemplate` are
        deliberately not passed. Every user will see SmartScreen's unknown-publisher prompt, and
        docs/PHASES.md records that as an accepted cost rather than an oversight.

    **The one thing that can destroy a user's data, and what stops it.** Velopack installs under
    `%LOCALAPPDATA%\<package id>` and its uninstall deletes that directory. The downloaded weights
    live in `%LOCALAPPDATA%\Uindosill\models` — 675 MB for one catalogue entry and several GiB for
    a full set, with a diariser to come. If the
    package id were `Uindosill`, uninstalling would delete every one of them. The id therefore comes
    from the `VelopackPackageId` property in src/Parakeet.App/Parakeet.App.csproj, and this script
    refuses to run if that id would collide, so the guard is in the thing that actually builds the
    installer and not only in a test.

    Every channel is read back after it is packed: the release files must exist, and the natives
    inside the nupkg must be exactly the ones that channel promises — with the LICENSE beside each,
    because parakeet.cpp is MIT and shipping the binary without its notice is a licence breach that
    nothing else in the pipeline would report.

.EXAMPLE
    .\scripts\package-windows.ps1 -Version 1.0.0
    Both channels. Needs Windows: vendoring the CUDA drop reads a PE import table.

.EXAMPLE
    .\scripts\package-windows.ps1 -Version 1.0.0 -Channels win
    The default channel alone — cpu and vulkan for transcription, the vulkan ask engine beside
    them. Runs anywhere pwsh does.

.EXAMPLE
    # Natives already vendored and the app already published; re-pack only.
    .\scripts\package-windows.ps1 -Version 1.0.0 -SkipVendor -SkipPublish
#>

[CmdletBinding()]
param(
    # The release version. Anything Velopack accepts as a SemVer: 1.0.0, or 1.0.0-rc.1.
    [Parameter(Mandatory = $true)]
    [string] $Version,

    # Which flavours to build. 'win' is the default channel and carries cpu and vulkan; 'win-cuda'
    # adds the opt-in CUDA drop and is Windows-only to vendor.
    [ValidateSet('win', 'win-cuda')]
    [string[]] $Channels = @('win', 'win-cuda'),

    # Skip assembling the bundled Python. For iterating on packaging alone: the resulting installer
    # carries no interpreter, so speaker labelling and translation are both dead in it, and the
    # read-back below says so rather than letting it pass as a release.
    [switch] $SkipPython,

    # Also build and split the CUDA pack into release assets. Off by default and
    # deliberately so: it needs a CUDA venv or about 3 GB of pip downloads, produces
    # 2.8 GB on disk, and is an accelerator for one opt-in rather than part of the
    # product. `-CudaPackVenv` names an existing venv to copy out of, which is what
    # makes the step take a minute instead of twenty.
    [switch] $CudaPack,

    [string] $CudaPackVenv,

    # Where the publish, the packages and the release feed land. Gitignored; nothing a build
    # produces belongs in the working tree.
    [string] $OutputDirectory,

    # Only win-x64 ships an installer. Upstream publishes no win-arm64 native, so an arm64 install
    # could not transcribe, and an installer that cannot do the product's one job is worse than no
    # installer. The arm64 publish stays a CI artefact.
    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',

    # PATH to a file of markdown release notes — vpk's --releaseNotes takes a filename, not the text,
    # and rejects text with "--releaseNotes file is not found, but must exist". Checked below,
    # before anything is downloaded or published.
    [string] $ReleaseNotes,

    # Reuse whatever is in native/ instead of downloading and verifying it.
    [switch] $SkipVendor,

    # Reuse the publish already in the output directory.
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repo 'src/Parakeet.App/Parakeet.App.csproj'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'packaging' }

# Made absolute once, here. Every cmdlet below honours PowerShell's own location, but
# [System.IO.Compression.ZipFile]::OpenRead in the read-back is an in-process .NET call and resolves
# a relative path against [Environment]::CurrentDirectory — which PowerShell does not keep in step
# with Set-Location. A relative -OutputDirectory therefore packed both channels and then threw on
# the very last step. New-Item first, because Resolve-Path needs the directory to exist.
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

if ($ReleaseNotes) {
    if (-not (Test-Path -LiteralPath $ReleaseNotes -PathType Leaf)) {
        throw "-ReleaseNotes is the path to a markdown file, and '$ReleaseNotes' is not one. " +
              "vpk takes a filename here, not the notes themselves."
    }
    $ReleaseNotes = (Resolve-Path -LiteralPath $ReleaseNotes).Path
}

# Which backend directories each channel is allowed to carry. This table is the whole difference
# between the two downloads, and the read-back at the end checks the package against it.
$backendsFor = @{
    'win'      = @('cpu', 'vulkan')
    'win-cuda' = @('cpu', 'vulkan', 'cuda')
}

# The second native stack: which llama-server drops each channel carries, under
# native/<rid>/llm/<backend>/. Three decisions, recorded in docs/V2-ASK-THE-TRANSCRIPT.md,
# docs/NATIVE-BINARIES.md and docs/PHASES.md rather than made here:
#
#   * No separate llm/cpu drop. Upstream builds these zips with GGML_BACKEND_DL, so every GPU
#     drop carries every per-ISA CPU variant beside its own backend DLL — the cpu drop is a
#     strict subset of either — and shipping both would be 40 MB of duplicate bytes. Whether the
#     server actually falls back to those CPU variants on a machine whose GPU driver is broken
#     is recorded as unmeasured in docs/UNPROVEN.md, not assumed here.
#   * win-cuda carries llm/cuda — decided by the maintainer on 2026-08-24, with the cost priced
#     first: on the 5080, CUDA buys 2.40x on the whole-transcript prefill (7.9 s against 19.1 s)
#     and ~9% on decode over the vulkan drop, for the CUDA pair's ~537 MB of archives against
#     vulkan's 34 MB. That puts a second CUDA runtime major (cudart-13.3) beside the ASR tier's
#     cudart-12.8, inside the llm/cuda directory, exactly the cost the decision accepted.
#   * win-cuda carries llm/cuda ALONE, not vulkan beside it. LlamaServerLocator takes the best
#     backend PRESENT — cuda before vulkan, no driver probe — and no product surface lets a user
#     pick the ask tier's backend, so a vulkan drop beside the cuda one would be 34 MB nothing
#     could ever run. The broken-driver fallback is the cuda drop's own CPU variants, the same
#     unproven-marker status as the default channel's.
$llmBackendsFor = @{
    'win'      = @('vulkan')
    'win-cuda' = @('cuda')
}

# Every backend name any channel can carry, which is what the prune below is allowed to delete.
#
# It used to delete any directory under native/<rid>/ that was not in the channel's list, on the
# assumption that backends were the only thing there. Three features broke that assumption without
# anyone noticing — tools/ (yt-dlp and Deno), ffmpeg/ and mpv/ are siblings of cpu/ and vulkan/ —
# and v1.0.0-rc.3 therefore shipped with links, video and transcript muxing silently absent, on a
# machine where all three had been vendored. Naming the backends means an unrecognised directory
# now survives instead of being deleted, which is the direction that fails safely.
$everyBackend = @('cpu', 'vulkan', 'cuda')

# The other three drops, what proves each is really there, and what has to travel beside it. The
# notices are not decoration: libmpv is GPLv2+, so its licence, mpv's copyright summary and the
# written offer are conditions of shipping the DLL at all.
$companionDrops = @(
    @{ Directory = 'tools';  Files = @('yt-dlp.exe', 'yt-dlp-LICENSE.txt', 'deno.exe', 'deno-LICENSE.txt')
       Feature = 'opening a link' }
    @{ Directory = 'ffmpeg'; Files = @('ffmpeg.exe', 'ffmpeg-LICENSE.txt')
       Feature = 'adding a transcript to a recording' }
    @{ Directory = 'mpv';    Files = @('libmpv-2.dll', 'GPL-2.0.txt', 'mpv-Copyright.txt', 'mpv-WRITTEN-OFFER.txt')
       Feature = 'playing the picture rather than the sound alone' }
)

function Write-Step([string] $Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Note([string] $Text) { Write-Host "   $Text" -ForegroundColor DarkGray }

function Get-MsBuildProperty([string] $Project, [string] $Name) {
    $output = & dotnet msbuild $Project "-getProperty:$Name" -nologo 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not read $Name from ${Project}:`n$output" }

    # -getProperty prints the bare value for one property and a JSON object for several. Take both,
    # so this keeps working if a caller ever asks for two at once.
    $text = ($output -join "`n").Trim()
    if ($text.StartsWith('{')) { return ($text | ConvertFrom-Json).Properties.$Name }

    # The bare form is one line, but a restore notice or a NuGet warning can land in front of it and
    # would otherwise be spliced onto the value — and this value becomes a directory name. Take the
    # last non-empty line, which is the value in every case, and refuse anything with whitespace in
    # it rather than passing it to vpk.
    $value = @($text -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1)[0].Trim()
    if ($value -match '\s') {
        throw "Reading $Name from $Project produced '$value', which is not a single token. Full output:`n$text"
    }
    return $value
}

Write-Step 'Identity'

$packId = Get-MsBuildProperty $appProject 'VelopackPackageId'
$packTitle = Get-MsBuildProperty $appProject 'VelopackPackageTitle'
$assemblyName = Get-MsBuildProperty $appProject 'AssemblyName'
$mainExe = "$assemblyName.exe"

if (-not $packId) {
    throw "Parakeet.App.csproj sets no VelopackPackageId. That property is the install directory name."
}

Write-Note "package id  $packId    (installs to %LOCALAPPDATA%\$packId)"
Write-Note "title       $packTitle"
Write-Note "main exe    $mainExe"

# The refusal that protects the weights. LocalModelStore puts them under
# %LOCALAPPDATA%\<data directory>\models and Velopack's uninstall deletes %LOCALAPPDATA%\<package id>
# — so these two names must differ, and must still differ after the case folding or punctuation
# stripping an installer might apply. A unit test asserts the same thing against the same property
# (tests/Parakeet.App.Tests/PackagingTests.cs); it is repeated here because this script is what
# actually builds the installer.
#
# The data directory's name is READ rather than repeated. Hardcoding it here would put a second copy
# in the one place whose entire job is to notice when the two names collide — and a guard carrying a
# stale copy of what it guards fails open, silently, on the day the name changes.
$userDataSource = Join-Path $repo 'src/Parakeet.Core/Models/UserDataPaths.cs'
$dataNameMatch = [regex]::Matches(
    (Get-Content -LiteralPath $userDataSource -Raw),
    'const\s+string\s+DirectoryName\s*=\s*"([^"]+)"')

if ($dataNameMatch.Count -ne 1) {
    throw "Expected exactly one 'const string DirectoryName' in $userDataSource, found " +
          "$($dataNameMatch.Count). That constant is what the package id is checked against; this " +
          "cannot run without it."
}
$dataDirectoryName = $dataNameMatch[0].Groups[1].Value
foreach ($candidate in @($packId, $packId.ToLowerInvariant(), ($packId -replace '[^A-Za-z0-9]', ''))) {
    if ($candidate -ieq $dataDirectoryName) {
        throw "REFUSING TO BUILD. The package id '$packId' normalises to '$candidate', which is the " +
              "directory holding the user's downloaded models (%LOCALAPPDATA%\$dataDirectoryName). " +
              "Velopack's uninstall deletes the install directory, so this installer would delete " +
              "every model that user has downloaded. Change VelopackPackageId in $appProject."
    }
}
Write-Note "checked: uninstalling %LOCALAPPDATA%\$packId cannot reach %LOCALAPPDATA%\$dataDirectoryName"

# Which catalogue entries travel inside the installer, read out of the file that decides it rather
# than repeated here — the same rule as the data directory name above, for the same reason. The pins
# themselves stay in models.json: this list only says which of them ship, so there is exactly one
# copy of every digest and the installer verifies what the Models tab would have verified.
$bundledSource = Join-Path $repo 'src/Parakeet.App/Services/BundledModels.cs'
$bundledBlock = [regex]::Match(
    (Get-Content -LiteralPath $bundledSource -Raw),
    'BundledIds\s*=\s*\[(?<ids>[^\]]*)\]')
if (-not $bundledBlock.Success) {
    throw "Could not find the BundledIds array in $bundledSource. That array is what decides which " +
          "weights the installer carries; this cannot run without it."
}
$bundledModelIds = @([regex]::Matches($bundledBlock.Groups['ids'].Value, '"([^"]+)"') |
    ForEach-Object { $_.Groups[1].Value })

# The win-cuda channel bundles less — the maintainer's decision, 2026-08-24: with llm/cuda
# inside, the measured python-less package (1,976,256,205-byte Setup.exe) plus rc.3's observed
# Python delta (+369.3 MB) projected past GitHub's 2 GiB asset limit, and the diariser's weight
# is what gives. The list of what stays out lives beside BundledIds in BundledModels.cs, read
# the same way, so there is one copy of the decision and the suite can hold its arithmetic.
$excludedBlock = [regex]::Match(
    (Get-Content -LiteralPath $bundledSource -Raw),
    'NotInCudaChannelIds\s*=\s*\[(?<ids>[^\]]*)\]')
if (-not $excludedBlock.Success) {
    throw "Could not find the NotInCudaChannelIds array in $bundledSource. That array is what keeps " +
          "the win-cuda channel under the release asset limit; this cannot run without it."
}
$notInCudaIds = @([regex]::Matches($excludedBlock.Groups['ids'].Value, '"([^"]+)"') |
    ForEach-Object { $_.Groups[1].Value })
foreach ($id in $notInCudaIds) {
    if ($id -notin $bundledModelIds) {
        throw "NotInCudaChannelIds names '$id', which BundledIds does not carry — an exclusion of " +
              "nothing is a decision that quietly stopped applying. Fix BundledModels.cs."
    }
}
$bundledIdsFor = @{
    'win'      = $bundledModelIds
    'win-cuda' = @($bundledModelIds | Where-Object { $_ -notin $notInCudaIds })
}

$catalogue = Get-Content -LiteralPath (Join-Path $repo 'src/Parakeet.Core/Models/models.json') -Raw |
    ConvertFrom-Json

# The mark, checked before anything is built rather than discovered by vpk halfway through: both
# files are committed, and a missing one means somebody moved them without running make-icon.ps1.
$brandIcon = Join-Path $repo 'brand/uindosill.ico'
$brandSplash = Join-Path $repo 'brand/uindosill-splash.png'
foreach ($art in @($brandIcon, $brandSplash)) {
    if (-not (Test-Path -LiteralPath $art -PathType Leaf)) {
        throw "$art is missing. Run scripts/make-icon.ps1 to regenerate the mark, and commit what it writes."
    }
}

Write-Step 'Tooling'

# The vpk CLI and the Velopack package the app links against build two halves of one artefact — the
# Setup stub, and the runtime that talks to it — so a mismatch is a mismatch between the installer
# and the thing it installs. The package version is read from Directory.Packages.props rather than
# repeated here.
$packagesProps = Join-Path $repo 'Directory.Packages.props'
$velopackPin = ([xml](Get-Content -LiteralPath $packagesProps -Raw)).
    SelectSingleNode("//PackageVersion[@Include='Velopack']")
if (-not $velopackPin) {
    # Checked before the property is read: under Set-StrictMode -Version Latest, reaching for
    # .Version on the null node throws first and this message never appears.
    throw "Directory.Packages.props pins no Velopack PackageVersion."
}
$packageVersion = $velopackPin.Version

# --skip-updates because vpk checks nuget.org for a newer vpk on every invocation and prints about
# it; and the whole output is searched rather than a fixed window for the same reason — one
# prepended line would otherwise turn "your tool is out of date" into "could not read the version".
$vpkBanner = (& dotnet vpk --help --skip-updates 2>&1 | Out-String)
$vpkMatch = [regex]::Match($vpkBanner, 'Velopack CLI\s+([0-9][^\s,]*)')
if (-not $vpkMatch.Success) {
    throw "Could not find a version in vpk's output. Is the tool restored? Try: dotnet tool restore`n$vpkBanner"
}
$vpkVersion = $vpkMatch.Groups[1].Value

if ($vpkVersion -ne $packageVersion) {
    throw "vpk is $vpkVersion but the app links Velopack $packageVersion. They build two halves of " +
          "one artefact and must match. Fix .config/dotnet-tools.json or Directory.Packages.props."
}
Write-Note "vpk $vpkVersion, Velopack package $packageVersion — matched"

if (($Channels -contains 'win-cuda') -and (-not $SkipVendor) -and (-not $IsWindows)) {
    throw "The win-cuda channel cannot be vendored here: scripts/vendor-cuda.ps1 reads a PE import " +
          "table against System32 and is Windows-only. Build it on Windows, or pass -SkipVendor if " +
          "native/$Runtime/cuda is already in place."
}

# Delta packages are compressed with zstd. On Windows vpk uses the copy bundled in its own package;
# everywhere else it wants `zstd` on PATH — and if it is missing it does not fail, it warns and falls
# back to bsdiff, which in the 1.2.0 line produces patches Update.exe cannot apply
# (velopack/velopack#1008, "vpk pack generates .bsdiff delta patches incompatible with Update.exe
# (v1.2.0 through v1.2.110)"). A silent degradation that only shows up as a failed update on someone
# else's machine is worth a loud check here. It is a warning rather than a throw because a release
# whose deltas fall back to a full download still installs correctly.
if (-not $IsWindows) {
    $zstd = Get-Command zstd -ErrorAction SilentlyContinue
    if (-not $zstd) {
        Write-Warning ("zstd is not on PATH. vpk will fall back to bsdiff deltas, which Update.exe " +
                       "cannot apply in the 1.2.0 line (velopack/velopack#1008): every user would " +
                       "silently re-download the whole application instead of a patch. " +
                       "Install it (apt install zstd) or pack on Windows, which bundles its own.")
    }
    else {
        Write-Note "zstd $((& zstd --version) -replace '.*v([0-9.]+).*', '$1') on PATH — deltas will use it"
    }
}

$built = @()

foreach ($channel in $Channels) {
    $backends = $backendsFor[$channel]
    $channelRoot = Join-Path $OutputDirectory $channel
    $publishDir = Join-Path $channelRoot 'publish'
    $releaseDir = Join-Path $OutputDirectory 'releases'

    Write-Step "Channel '$channel' — backends: $($backends -join ', ')"

    if ($SkipPublish -and -not $SkipVendor) {
        # Vendoring writes into native/, and it is the BUILD that copies native/ into a publish. With
        # the publish skipped, a freshly vendored drop — a bumped pin, say — would be downloaded,
        # verified, and then not packaged, while every step reported success and the package carried
        # the old binaries.
        Write-Note 'not vendoring: -SkipPublish reuses an existing publish, which a new drop would not reach'
    }
    elseif (-not $SkipVendor) {
        # The same scripts a developer runs and CI runs: pinned archives, byte count and SHA-256
        # checked against docs/NATIVE-BINARIES.md before anything is unpacked.
        & (Join-Path $PSScriptRoot 'vendor-natives.ps1') -Backends $backends

        # The other two, which nothing used to call from here. That was half of why rc.3 shipped
        # without links, video or muxing: CI ran this script and this script vendored the decoder
        # alone, so the release path had no way to produce a drop it then went on to prune anyway.
        # They are cheap when the binaries are already on disk — each verifies a digest and returns.
        & (Join-Path $PSScriptRoot 'vendor-tools.ps1')
        & (Join-Path $PSScriptRoot 'vendor-mpv.ps1')

        # And the second stack, under the same rules: pinned archives, digests checked against
        # docs/NATIVE-BINARIES.md, the MIT text written beside the binaries.
        & (Join-Path $PSScriptRoot 'vendor-llm-natives.ps1') -Backends $llmBackendsFor[$channel]
    }

    if (-not $SkipPublish) {
        if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }

        & dotnet publish (Join-Path $repo 'src/Parakeet.App') `
            --configuration Release -r $Runtime -o $publishDir --nologo
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for channel '$channel'." }
    }

    if (-not (Test-Path -LiteralPath $publishDir)) {
        throw "$publishDir is not there. Drop -SkipPublish."
    }

    # Self-contained, not framework-dependent. Eleven files means a lost --self-contained; see
    # Directory.Build.targets and docs/GOTCHAS.md gotcha 9. Checked here as well as in CI, because
    # this is the output a user actually receives.
    # Both of the old signals went away when single-file was turned on: hostfxr.dll is inside the
    # executable now, and the publish is around 34 files rather than 200. What still separates a
    # self-contained build from a framework-dependent one is the size of the executable itself — the
    # runtime is either in there or it is not, and that is about 98 MB of difference.
    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
    $mainExePath = Join-Path $publishDir $mainExe
    if (-not (Test-Path -LiteralPath $mainExePath)) {
        throw "$publishDir has no $mainExe. Publishing produced something this cannot package."
    }

    $mainExeBytes = (Get-Item -LiteralPath $mainExePath).Length
    if ($mainExeBytes -lt 50MB) {
        throw "$mainExe is $('{0:N0}' -f $mainExeBytes) bytes, which is a framework-dependent " +
              "single-file build: it would need the exact .NET runtime present on every machine. " +
              "A RuntimeIdentifier does not imply SelfContained on .NET 8+; see Directory.Build.targets."
    }
    Write-Note ("publish: {0} files, {1:N0} MB executable, self-contained and single-file" -f `
        $publishedFiles.Count, ($mainExeBytes / 1MB))

    # The bundled Python, which is where two of this product's three models actually run. It goes
    # into the publish rather than being packed separately because Velopack ships a directory: what
    # is here is what a user receives, and `PythonRuntime.Resolve` looks for `<app>/python`.
    #
    # Assembled after the publish on purpose. `dotnet publish` clears its output directory, so a
    # bundle written first would be deleted by the very next run with -SkipPython.
    $bundleDir = Join-Path $publishDir 'python'
    if ($SkipPython) {
        Write-Note 'not bundling Python: -SkipPython — speaker labelling and translation will be dead in this build'
    }
    else {
        & (Join-Path $PSScriptRoot 'bundle-python.ps1') -Destination $bundleDir
        if ($LASTEXITCODE -ne 0) { throw "Assembling the bundled Python failed for channel '$channel'." }
    }

    # The build copies whatever is in native/ into the output, so a machine that has vendored CUDA
    # produces a CUDA-carrying publish for every channel. The channel is what decides which survives
    # into the package, and dropping the rest here — before packing, on disk, where it can be listed
    # — is checkable in a way an --exclude regex is not.
    $nativeRoot = Join-Path $publishDir "native/$Runtime"
    if (Test-Path -LiteralPath $nativeRoot) {
        foreach ($present in Get-ChildItem -LiteralPath $nativeRoot -Directory) {
            # Only a backend this channel does not promise. Anything else under native/<rid>/ is a
            # drop every channel carries — or something added later that this script has never
            # heard of, which is not a reason to delete it. See $everyBackend.
            if ($present.Name -in $everyBackend -and $present.Name -notin $backends) {
                Write-Note "dropping native/$Runtime/$($present.Name) — not in channel '$channel'"
                Remove-Item -LiteralPath $present.FullName -Recurse -Force
            }
        }
    }

    foreach ($backend in $backends) {
        foreach ($file in @('parakeet.dll', 'LICENSE')) {
            $path = Join-Path $nativeRoot "$backend/$file"
            if ((-not (Test-Path -LiteralPath $path)) -or ((Get-Item -LiteralPath $path).Length -eq 0)) {
                throw "native/$Runtime/$backend/$file is missing or empty in the publish. Vendor the " +
                      "'$backend' backend first, and remember the build is what copies native/ into the output."
            }
        }
    }

    # The second stack's prune and presence check, on the same terms as the first's. A developer
    # machine that has vendored llm/cpu for its own tests produces a publish carrying it, and the
    # channel is what decides which drops survive — the prune only ever touches names it knows,
    # the direction that fails safely.
    $llmBackends = $llmBackendsFor[$channel]
    $llmRoot = Join-Path $nativeRoot 'llm'
    if (Test-Path -LiteralPath $llmRoot) {
        foreach ($present in Get-ChildItem -LiteralPath $llmRoot -Directory) {
            if ($present.Name -in $everyBackend -and $present.Name -notin $llmBackends) {
                Write-Note "dropping native/$Runtime/llm/$($present.Name) — not in channel '$channel'"
                Remove-Item -LiteralPath $present.FullName -Recurse -Force
            }
        }
    }

    foreach ($backend in $llmBackends) {
        foreach ($file in @('llama-server.exe', 'LICENSE')) {
            $path = Join-Path $llmRoot "$backend/$file"
            if ((-not (Test-Path -LiteralPath $path)) -or ((Get-Item -LiteralPath $path).Length -eq 0)) {
                throw "native/$Runtime/llm/$backend/$file is missing or empty in the publish, so this " +
                      "package would ship an Ask panel whose engine is never available. Run " +
                      "scripts/vendor-llm-natives.ps1 — and remember the build is what copies native/ " +
                      "into the output, so vendoring after a publish needs another one."
            }
        }
    }
    Write-Note "ask engine: llm/$($llmBackends -join ', llm/') present with the MIT text beside it"

    # The three drops that are not backends, checked the same way and for a better reason: every one
    # of them is a whole feature of the application, and the application degrades politely when one
    # is absent — a link box that refuses, sound without picture, a transcript that will not go into
    # the file. Politeness is why nothing shouted when v1.0.0-rc.3 shipped without all three. A
    # release that quietly drops a feature is worse than one that fails to build, so this throws.
    foreach ($drop in $companionDrops) {
        foreach ($file in $drop.Files) {
            $path = Join-Path $nativeRoot "$($drop.Directory)/$file"
            if ((-not (Test-Path -LiteralPath $path)) -or ((Get-Item -LiteralPath $path).Length -eq 0)) {
                throw "native/$Runtime/$($drop.Directory)/$file is missing or empty in the publish, so " +
                      "this package would ship without $($drop.Feature). Run scripts/vendor-tools.ps1 and " +
                      "scripts/vendor-mpv.ps1 — and remember the build is what copies native/ into the " +
                      "output, so vendoring after a publish needs another one."
            }
        }
    }
    Write-Note "companions: $(($companionDrops.Directory) -join ', ') present with their notices"

    # The weights the installer carries, into <app>/models where BundledModels looks.
    #
    # The line between bundled and downloaded is a GitHub release asset limit of 2 GiB rather than
    # a principle: the recogniser is 1.34 GiB and the translator 1.34 GiB, so either one puts the
    # CUDA channel over it, and since llm/cuda the speaker labeller does too — $bundledIdsFor above
    # holds that decision. Bundling what fits is what makes the opt-ins live on a fresh install
    # instead of dead until somebody visits a tab.
    #
    # Every file is verified against the digest models.json already pins, before it is copied — the
    # same check ModelInstaller performs on a download, because a weight nobody hashed is a weight
    # that ships wrong once and is blamed on the model.
    $bundledDir = Join-Path $publishDir 'models'
    New-Item -ItemType Directory -Force -Path $bundledDir | Out-Null

    # Cached beside the publish so packing the second channel does not download the same file again,
    # and so an interrupted run resumes cheaply. packaging/ is gitignored in its entirety.
    $cacheDir = Join-Path $OutputDirectory 'model-cache'
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

    foreach ($id in $bundledIdsFor[$channel]) {
        # Not $matches: that is PowerShell's own automatic variable, written by every -match in this
        # script, and assigning to it is how a regex two hundred lines away starts returning surprises.
        $found = @($catalogue.models | Where-Object { $_.id -eq $id })
        if ($found.Count -ne 1) {
            throw "models.json holds $($found.Count) entries with id '$id', and BundledModels.cs says " +
                  "the installer carries it. One of the two files is wrong."
        }
        $entry = $found[0]

        if ($entry.PSObject.Properties.Name -notcontains 'fileName') {
            throw "'$id' is a multi-file catalogue entry. The installer carries single-file entries only; " +
                  "BundledModels.PathFor answers null for the others, so bundling this one would ship a " +
                  "file nothing reads."
        }

        $cached = Join-Path $cacheDir $entry.fileName
        if ((Test-Path -LiteralPath $cached) -and ((Get-Item -LiteralPath $cached).Length -eq $entry.sizeBytes)) {
            Write-Note "$($entry.fileName): cached"
        }
        else {
            Write-Note ("$($entry.fileName): downloading {0:N1} MB" -f ($entry.sizeBytes / 1MB))
            $previous = $ProgressPreference
            $ProgressPreference = 'SilentlyContinue'   # Write-Progress costs more than the download
            try { Invoke-WebRequest -Uri $entry.url -OutFile $cached }
            finally { $ProgressPreference = $previous }
        }

        $size = (Get-Item -LiteralPath $cached).Length
        if ($size -ne $entry.sizeBytes) {
            throw "$($entry.fileName) is $size bytes and models.json pins $($entry.sizeBytes). Refusing to " +
                  "bundle a file that is not the pinned one."
        }

        $hash = (Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash
        if ($hash -ne $entry.sha256.ToUpperInvariant()) {
            throw "$($entry.fileName) hashes to $hash and models.json pins $($entry.sha256.ToUpperInvariant()). " +
                  "Refusing to bundle it."
        }

        Copy-Item -LiteralPath $cached -Destination (Join-Path $bundledDir $entry.fileName) -Force
        Write-Note ("$($entry.fileName): {0:N1} MB, sha256 matches the catalogue" -f ($size / 1MB))
    }

    # The notices that must be on disk before anything is packed. ONNX Runtime is MIT and has to
    # travel with its notice; Silero VAD is MIT with a notice of its own; and the LGPL written offer
    # discharges what libsndfile leaves. They arrive through the build (Licences.targets and the
    # NuGet package's own RID assets), which is exactly why they are checked here: a build that
    # silently stopped copying them would produce a package that looks complete.
    #
    # The NVIDIA Open Model License copy was a fourth entry until 2026-08-27 and left with the
    # speaker weights it covered; nothing in this product is under that Agreement now.
    foreach ($required in @(
        # The NVIDIA Open Model License copy left this list on 2026-08-27 with the Sortformer
        # weights it covered — §3.1 wanted a copy rather than a link, and there is no longer a model
        # under that Agreement to want one. `attic/sortformer/` holds the text.
        'licences/onnxruntime-LICENSE.txt',
        'licences/onnxruntime-ThirdPartyNotices.txt',
        'licences/silero-vad-LICENSE.txt',
        # The LGPL written offer, added 2026-08-26. Two libraries inside the bundled Python are
        # LGPL-2.1 and one of them -- libsoxr, inside `soxr/soxr_ext.pyd` -- is statically linked,
        # which closes section 6(b) and leaves the 6(c) offer as what discharges it. A publish
        # without this file is a distribution of LGPL binaries with none of 6(a)-(e) done.
        'licences/LGPL-WRITTEN-OFFER.txt')) {
        $path = Join-Path $publishDir $required
        if ((-not (Test-Path -LiteralPath $path)) -or ((Get-Item -LiteralPath $path).Length -eq 0)) {
            throw "$required is missing or empty in the publish. 'uindosill notice' prints the path to each " +
                  "of these, so a publish without one prints a promise it does not keep. " +
                  "ONNX Runtime is MIT and redistributed twice — inside the Python bundle since " +
                  "2026-08-21 and beside the .NET assemblies again since 2026-08-23 for speech detection — and the " +
                  "Silero VAD graph is MIT with a notice of its own, so all of these are owed. See docs/LICENSING.md."
        }
    }

    # ONNX Runtime ships twice since 2026-08-23: the bundled Python carries the onnxruntime-webgpu
    # wheel for the translator — the diariser was its other consumer until 2026-08-27 and is torch
    # now — and `onnxruntime.dll` is beside the managed
    # assemblies again for the speech-detection graph, which runs in process. Both copies' notices
    # come from `licences/` and are asserted above — one obligation, two binaries.
    #
    # **The committed notices are 1.29.0's and the bundle ships onnxruntime-webgpu 1.27.0.** Two
    # versions, one ThirdPartyNotices.txt; docs/LICENSING.md is where that is reconciled or recorded
    # as a gap, and this comment exists so the next person to read this file knows it is one.
    if (-not $SkipPython) {
        foreach ($required in @(
            'python.exe',
            'uindosill_engines/serve.py',
            # No diariser reference: the engine that had one is in `attic/sortformer/`, and the
            # pipeline that replaced it is torch on both stages with no ONNX path to check against.
            # See bundle-python.ps1's list, where the same three names left together.
            'uindosill_engines/translator/parity-reference.json',
            'Lib/site-packages/onnxruntime')) {
            $path = Join-Path $bundleDir $required
            if (-not (Test-Path -LiteralPath $path)) {
                throw "python/$required is missing from the publish. Two of this product's models run in " +
                      "that bundle, and the translator's parity reference is what stands between a user and a " +
                      "silently wrong execution provider."
            }
        }

        $bundleSize = (Get-ChildItem -LiteralPath $bundleDir -Recurse -File |
            Measure-Object -Property Length -Sum).Sum
        Write-Note ("python bundle: {0:N2} GB" -f ($bundleSize / 1GB))
    }

    Write-Step "Packing '$channel' $Version"

    # `[win]` is a System.CommandLine directive, not a flag, and it is what enables cross-building a
    # Windows package from a non-Windows host — `--runtime win-x64` alone names the RID and on Linux
    # is refused with "Not Supported". It is accepted on Windows too (verified against vpk 1.2.0),
    # so it is passed unconditionally rather than behind an $IsWindows test.
    $packArgs = @(
        'vpk', '[win]', 'pack'
        '--packId', $packId
        '--packVersion', $Version
        '--packDir', $publishDir
        '--packTitle', $packTitle
        '--packAuthors', 'Uindosill contributors'
        '--mainExe', $mainExe
        '--outputDir', $releaseDir
        '--runtime', $Runtime
        '--channel', $channel
        # The mark, for Setup.exe and for the Add/Remove Programs row — the same file the
        # application compiles into its own executable, so the installer and the thing it installs
        # cannot drift apart. Generated by scripts/make-icon.ps1 and committed.
        '--icon', $brandIcon
        # Shown while Setup.exe unpacks. Velopack's installer asks no questions by design — there
        # is no directory to choose and nothing to configure — so the splash is the whole of what a
        # user sees, and an unbranded grey box is the difference between "installing" and "what is
        # this". The mark alone: the window says the name a second later.
        '--splashImage', $brandSplash
        # vpk checks nuget.org for a newer vpk on every run. A packaging step that reaches the
        # network to talk about itself is a packaging step that fails when nuget.org does.
        '--skip-updates'
    )
    if ($ReleaseNotes) { $packArgs += @('--releaseNotes', $ReleaseNotes) }

    # No --framework: this publish is self-contained, and passing one would have Setup.exe download
    # a runtime the application does not need — it is also the only thing that would make Setup.exe
    # touch the network at all. No --signParams and no --signTemplate: v1.0 is unsigned by decision.
    & dotnet @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed for channel '$channel'." }

    $built += [PSCustomObject]@{
        Channel = $channel; Backends = $backends; ReleaseDir = $releaseDir; PublishDir = $publishDir
    }
}

Write-Step 'Reading the packages back'

# Nothing above proves what is inside the nupkg, and the difference between the two channels is
# exactly that. Both are opened and their native payload listed against the table at the top.
Add-Type -AssemblyName System.IO.Compression.FileSystem

$failures = @()

foreach ($entry in $built) {
    $channel = $entry.Channel

    # Velopack suffixes every file of a non-default channel and leaves the default channel's names
    # bare, which is what lets both live on one GitHub release without colliding. Observed, not
    # assumed — see docs/UNPROVEN.md.
    $suffix = if ($channel -eq 'win') { '' } else { "-$channel" }

    $expected = @(
        "$packId-$Version$suffix-full.nupkg"
        "$packId-$channel-Setup.exe"
        "releases.$channel.json"
    )

    Write-Host "`n   channel '$channel'" -ForegroundColor Green

    foreach ($name in $expected) {
        $path = Join-Path $entry.ReleaseDir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $failures += "channel '$channel': $name was not produced."
            continue
        }

        # Length as well as existence: packaging/releases is never cleaned between runs, so an
        # interrupted earlier run can leave a zero-byte file that Test-Path is perfectly happy with.
        $size = (Get-Item -LiteralPath $path).Length
        if ($size -eq 0) {
            $failures += "channel '$channel': $name is zero bytes — left by an interrupted run, not built by this one."
            continue
        }
        Write-Host ("     {0,-54} {1,13:N0} bytes" -f $name, $size)
    }

    $nupkg = Join-Path $entry.ReleaseDir "$packId-$Version$suffix-full.nupkg"
    if (-not (Test-Path -LiteralPath $nupkg)) { continue }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })

        $directories = @($names |
            ForEach-Object { if ($_ -match "native/$Runtime/([^/]+)/") { $Matches[1] } } |
            Sort-Object -Unique)

        # Backend directories only. The companions — tools, ffmpeg, mpv — live beside them and are
        # checked below on their own terms; counting them here would report every one of them as a
        # backend this channel does not promise, which is the same conflation that made the prune
        # delete them.
        $inside = @($directories | Where-Object { $_ -in $everyBackend })

        $missing = @($entry.Backends | Where-Object { $_ -notin $inside })
        $extra = @($inside | Where-Object { $_ -notin $entry.Backends })

        if ($missing) {
            $failures += "channel '$channel': the package is missing backend(s) $($missing -join ', ')."
        }
        if ($extra) {
            $failures += "channel '$channel': the package carries backend(s) $($extra -join ', ') that " +
                         "this channel does not promise — the point of two channels is that the default " +
                         "download does not contain CUDA."
        }

        foreach ($backend in $entry.Backends) {
            foreach ($file in @('parakeet.dll', 'LICENSE')) {
                $wanted = "native/$Runtime/$backend/$file"
                if (-not ($names | Where-Object { $_.EndsWith($wanted) })) {
                    $failures += "channel '$channel': $wanted is not inside the package. parakeet.cpp is " +
                                 "MIT and its LICENSE has to travel with the binary."
                }
            }
        }

        # And the three companions, inside the package rather than merely on disk before it. This is
        # the check that would have stopped v1.0.0-rc.3: everything upstream of it passed while the
        # package carried no yt-dlp, no ffmpeg and no libmpv.
        foreach ($drop in $companionDrops) {
            foreach ($file in $drop.Files) {
                $wanted = "native/$Runtime/$($drop.Directory)/$file"
                if (-not ($names | Where-Object { $_.EndsWith($wanted) })) {
                    $failures += "channel '$channel': $wanted is not inside the package, so it would " +
                                 "ship without $($drop.Feature)."
                }
            }
        }

        # The second stack, against its own half of the channel table: exactly the promised llm
        # drops, no others, each with llama-server.exe and the MIT text inside the package.
        # Deliberately NOT filtered to $everyBackend: the prune deletes only names it knows,
        # which is the direction that fails safely — so this check is what keeps a drop under a
        # name nothing here recognises from shipping in every channel unflagged.
        $llmInside = @($names |
            ForEach-Object { if ($_ -match "native/$Runtime/llm/([^/]+)/") { $Matches[1] } } |
            Sort-Object -Unique)
        $llmPromised = $llmBackendsFor[$channel]

        foreach ($backend in @($llmPromised | Where-Object { $_ -notin $llmInside })) {
            $failures += "channel '$channel': the package is missing llm backend '$backend', so its " +
                         "Ask panel's engine would never be available."
        }
        foreach ($backend in @($llmInside | Where-Object { $_ -notin $llmPromised })) {
            $failures += "channel '$channel': the package carries llm backend '$backend' that this " +
                         "channel does not promise."
        }

        foreach ($backend in $llmPromised) {
            foreach ($file in @('llama-server.exe', 'LICENSE')) {
                $wanted = "native/$Runtime/llm/$backend/$file"
                if (-not ($names | Where-Object { $_.EndsWith($wanted) })) {
                    $failures += "channel '$channel': $wanted is not inside the package. llama.cpp is " +
                                 "MIT and its notice has to travel with the binaries."
                }
            }
        }

        # And the bundled weights, for the same reason: the opt-ins they serve degrade politely to
        # "not installed yet", so a package that lost them would look exactly like a working one.
        foreach ($id in $bundledIdsFor[$channel]) {
            $entry = @($catalogue.models | Where-Object { $_.id -eq $id })[0]
            $wanted = "models/$($entry.fileName)"
            if (-not ($names | Where-Object { $_.EndsWith($wanted) })) {
                $failures += "channel '$channel': $wanted is not inside the package, so '$($entry.displayName)' " +
                             "would be a download again rather than something the installer carries."
            }
        }

        # The excluded weight's absence is checked as positively as the others' presence: a weight
        # that slipped back into win-cuda is the 2 GiB upload failure coming back silently.
        foreach ($id in @($bundledModelIds | Where-Object { $_ -notin $bundledIdsFor[$channel] })) {
            $entry = @($catalogue.models | Where-Object { $_.id -eq $id })[0]
            $wanted = "models/$($entry.fileName)"
            if ($names | Where-Object { $_.EndsWith($wanted) }) {
                $failures += "channel '$channel': $wanted is inside the package, and the channel table " +
                             "excludes it — that is the release asset limit decision not being applied."
            }
        }

        Write-Host "     natives inside the package: $($inside -join ', ')"
        Write-Host "     ask engine inside the package: llm/$($llmInside -join ', llm/')"
        Write-Host "     companions inside the package: $((@($directories | Where-Object { $_ -notin $everyBackend -and $_ -ne 'llm' })) -join ', ')"
        Write-Host "     weights inside the package: $($bundledIdsFor[$channel] -join ', ')"
    }
    finally { $zip.Dispose() }
}

Write-Step 'Packing the bundled Python as its own download'

# Decision of 2026-08-21: the CLI zip carries no interpreter, so the bundle is a third download
# rather than 1.2 GB charged to every command-line user for two opt-ins most will never run.
#
# **The zip carries `python/` at its root, and that is the whole of the install instruction.**
# `PythonRuntime` looks under `%LOCALAPPDATA%\Uindosill` — the directory the weights already live
# in — so unpacking this there is all a CLI user does, with nothing to configure and no variable to
# set. `includeBaseDirectory` is what puts that root in, and it is the difference between an unpack
# that works and one that scatters an interpreter across a user's data directory.
#
# The installer is untouched by this: its copy is inside the publish where it always was, and an
# installed desktop application prefers its own over a download that may be a different version.
$bundleZip = Join-Path (Join-Path $OutputDirectory 'releases') 'uindosill-python-win-x64.zip'
$bundleSource = @($built |
    ForEach-Object { Join-Path $_.PublishDir 'python' } |
    Where-Object { Test-Path -LiteralPath $_ }) | Select-Object -First 1

if (-not $bundleSource) {
    Write-Note 'no bundled Python in any publish, so no bundle download was packed (-SkipPython)'
}
else {
    # Never appended to: packaging/releases is not cleaned between runs, and CreateFromDirectory
    # refuses an existing file rather than replacing it.
    if (Test-Path -LiteralPath $bundleZip) { Remove-Item -LiteralPath $bundleZip -Force }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $bundleSource, $bundleZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

    # Read back for the same reason every other artefact here is: a zip that assembles is not a zip
    # that carries what it promises, and the failure mode this catches — an interpreter with no
    # engines beside it — is exactly what `PythonRuntime` calls half a bundle.
    $zip = [System.IO.Compression.ZipFile]::OpenRead($bundleZip)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })

        if ('python/python.exe' -notin $names) {
            $failures += 'the bundle download has no python/python.exe at its root, so unpacking it ' +
                         'into the user data directory would not produce a bundle anything can find.'
        }

        if (-not ($names | Where-Object { $_.StartsWith('python/uindosill_engines/', [StringComparison]::Ordinal) })) {
            $failures += 'the bundle download carries no uindosill_engines package — half a bundle, ' +
                         'which fails on a user machine rather than here.'
        }

        $size = (Get-Item -LiteralPath $bundleZip).Length
        Write-Host ("     {0,-54} {1,13:N0} bytes" -f 'uindosill-python-win-x64.zip', $size)
        Write-Host "     $($names.Count) entries, from $bundleSource"
    }
    finally { $zip.Dispose() }
}

# ---- The CUDA pack, when asked for. -------------------------------------------------------------
#
# **Not part of a normal release, and the switch is the decision.** The pack is 2.8 GB unpacked and
# 1.83 GB compressed; building it needs either a CUDA venv on this machine or about 3 GB of wheel
# downloads. It accelerates one opt-in on one vendor's hardware, so it is built when somebody asks
# for it rather than on every release.
#
# **The parts go beside the other release files and the whole zip does not.** 1,961,716,087 bytes
# clears GitHub's 2 GiB asset limit by 177 MB, which is the kind of margin that stops being one
# after a torch point release -- and parts give a 1.8 GB download somewhere to resume from. The
# manifest is copied out beside them because `src/Parakeet.Engine.Python/cuda-pack.json` is filled
# in from it by hand, and the digests have to be read off the thing that was actually uploaded.
if ($CudaPack) {
    Write-Host ''
    Write-Note 'building the CUDA pack (this is the slow one)'

    $packStaging = Join-Path $OutputDirectory 'python-cuda'
    $packArgs = @{ Destination = $packStaging; Package = $true
                   PackageDirectory = (Join-Path $OutputDirectory 'releases') }
    if ($CudaPackVenv) { $packArgs['FromVenv'] = $CudaPackVenv }

    & (Join-Path $PSScriptRoot 'bundle-python-cuda.ps1') @packArgs
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
        throw 'bundle-python-cuda.ps1 failed; no CUDA pack assets were produced.'
    }

    # Read back, on the same terms as every other artefact here: the parts must exist, add up to
    # the manifest's archive size, and each be under the asset limit that made them parts at all.
    $packManifestPath = Join-Path (Join-Path $OutputDirectory 'releases') 'manifest.json'
    if (-not (Test-Path -LiteralPath $packManifestPath)) {
        $failures += 'the CUDA pack produced no manifest.json, so its digests cannot be pinned.'
    }
    else {
        $packManifest = Get-Content -LiteralPath $packManifestPath -Raw | ConvertFrom-Json
        $total = 0L
        foreach ($part in $packManifest.parts) {
            $partPath = Join-Path (Join-Path $OutputDirectory 'releases') $part.fileName
            if (-not (Test-Path -LiteralPath $partPath)) {
                $failures += "the CUDA pack manifest names $($part.fileName), which was not produced."
                continue
            }
            $actual = (Get-Item -LiteralPath $partPath).Length
            if ($actual -ne $part.sizeBytes) {
                $failures += "CUDA pack part $($part.fileName) is $actual bytes and its manifest says $($part.sizeBytes)."
            }
            if ($actual -ge 2GB) {
                $failures += "CUDA pack part $($part.fileName) is $actual bytes, at or over the 2 GiB asset limit."
            }
            $total += $actual
            Write-Host ("     {0,-54} {1,13:N0} bytes" -f $part.fileName, $actual)
        }
        if ($total -ne $packManifest.archiveBytes) {
            $failures += "the CUDA pack parts total $total bytes and the manifest claims an archive of $($packManifest.archiveBytes)."
        }
        Write-Host ("     {0,-54} {1,13:N0} bytes" -f 'manifest.json', (Get-Item -LiteralPath $packManifestPath).Length)
        Write-Host "     torch $($packManifest.torchVersion); pin these digests into src/Parakeet.Engine.Python/cuda-pack.json"
    }
}

if ($failures) {
    Write-Host ''
    foreach ($failure in $failures) { Write-Host "   $failure" -ForegroundColor Red }
    throw "The packages were built, but do not contain what they promise ($($failures.Count) problem(s))."
}

Write-Host ''
Write-Host "   Release files are in $(Join-Path $OutputDirectory 'releases')." -ForegroundColor Green
Write-Host '   Nothing here is signed: every user will see SmartScreen name an unknown publisher.' -ForegroundColor DarkGray
Write-Host ''
