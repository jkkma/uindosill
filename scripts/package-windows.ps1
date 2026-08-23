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
        flavour by an update.
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
    The default channel alone — cpu and vulkan, ~82 MB of Setup.exe. Runs anywhere pwsh does.

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
        # The same script a developer runs and CI runs: pinned archives, byte count and SHA-256
        # checked against docs/NATIVE-BINARIES.md before anything is unpacked.
        & (Join-Path $PSScriptRoot 'vendor-natives.ps1') -Backends $backends
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
    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
    if ((-not (Test-Path -LiteralPath (Join-Path $publishDir 'hostfxr.dll'))) -or ($publishedFiles.Count -lt 100)) {
        throw "$publishDir has $($publishedFiles.Count) files and no hostfxr.dll — this publish is not " +
              "self-contained, and would need the exact .NET runtime present on every machine."
    }
    Write-Note "publish: $($publishedFiles.Count) files, self-contained"

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
            if ($present.Name -notin $backends) {
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

    # The speaker opt-in's two obligations, checked on disk before anything is packed. The graph is
    # run by onnxruntime.dll, which is MIT and has to travel with its notice; the weights are under
    # the NVIDIA Open Model License, whose section 3.1 wants a copy of the Agreement rather than a
    # link — and `uindosill notice` prints the path to that copy, so a publish without it prints a
    # promise it does not keep. Both arrive through the build (Licences.targets and the NuGet
    # package's own RID assets), which is exactly why they are checked here: a build that silently
    # stopped copying them would produce a package that looks complete.
    foreach ($required in @(
        'licences/NVIDIA-Open-Model-License-2025-10-24.txt',
        'licences/onnxruntime-LICENSE.txt',
        'licences/onnxruntime-ThirdPartyNotices.txt',
        'licences/silero-vad-LICENSE.txt')) {
        $path = Join-Path $publishDir $required
        if ((-not (Test-Path -LiteralPath $path)) -or ((Get-Item -LiteralPath $path).Length -eq 0)) {
            throw "$required is missing or empty in the publish. The diarisation weights are under the NVIDIA " +
                  "Open Model License, whose section 3.1 wants a copy of the Agreement rather than a link, and " +
                  "'uindosill notice' prints the path to that copy — so a publish without it prints a promise it " +
                  "does not keep. ONNX Runtime is MIT and redistributed twice — inside the Python bundle since " +
                  "2026-08-21 and beside the .NET assemblies again since 2026-08-23 for speech detection — and the " +
                  "Silero VAD graph is MIT with a notice of its own, so all of these are owed. See docs/LICENSING.md."
        }
    }

    # ONNX Runtime ships twice since 2026-08-23: the bundled Python carries the onnxruntime-webgpu
    # wheel for the diariser and the translator, and `onnxruntime.dll` is beside the managed
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
            'uindosill_engines/diariser/parity-reference.npy',
            'uindosill_engines/translator/parity-reference.json',
            'Lib/site-packages/onnxruntime')) {
            $path = Join-Path $bundleDir $required
            if (-not (Test-Path -LiteralPath $path)) {
                throw "python/$required is missing from the publish. Two of this product's three models run in " +
                      "that bundle, and both parity references are what stand between a user and a silently " +
                      "wrong execution provider."
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

        $inside = @($names |
            ForEach-Object { if ($_ -match "native/$Runtime/([^/]+)/") { $Matches[1] } } |
            Sort-Object -Unique)

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

        Write-Host "     natives inside the package: $($inside -join ', ')"
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

if ($failures) {
    Write-Host ''
    foreach ($failure in $failures) { Write-Host "   $failure" -ForegroundColor Red }
    throw "The packages were built, but do not contain what they promise ($($failures.Count) problem(s))."
}

Write-Host ''
Write-Host "   Release files are in $(Join-Path $OutputDirectory 'releases')." -ForegroundColor Green
Write-Host '   Nothing here is signed: every user will see SmartScreen name an unknown publisher.' -ForegroundColor DarkGray
Write-Host ''
