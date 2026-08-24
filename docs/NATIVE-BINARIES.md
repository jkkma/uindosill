# Vendoring the parakeet.cpp native library

The native library is **not** in this repository and is **not** fetched at build time. It is fetched
by `scripts/vendor-natives.ps1`, which downloads the pinned release archives, checks each against
the digest table at the end of this document, and unpacks them into the layout below. CI runs the
same script before the `win-x64` publish, so that artefact carries the natives.

```powershell
.\scripts\vendor-natives.ps1                            # cpu and vulkan — what every build ships; ~18 MB
.\scripts\vendor-natives.ps1 -Backends cpu,vulkan,cuda  # plus the opt-in CUDA pair; ~700 MB, 931 MB on disk
.\scripts\lab.ps1 vendor                                # the same, through the dispatcher
```

Then **rebuild** — `build/NativeAssets.targets` evaluates its glob when the project is evaluated, so
a drop made after the last build is not in the output until the next one, and `uindosill doctor`
calls that "not vendored", which is the wrong diagnosis.

What one run does, per backend: finds the archive in `native/archives/` or downloads it there from
the pinned release (a download lands under a temporary name and is renamed only after it verifies);
checks the byte count and then the SHA-256 against the pins in the script and stops before
unpacking on either mismatch; unpacks flat into `native/win-x64/<backend>/`, leaving files that
match the archive's size alone, replacing them with `-Force`, and refusing otherwise; reads the
result back — `parakeet.dll` at the byte count recorded below, `LICENSE` beside it; and finally
checks that every digest it trusted is in the table at the end of this document, failing the run if
one is not. That last step is what keeps the script's pins and this document's table one fact
rather than two, the same way `scripts/check-test-counts.py` keeps the test count honest. Bump the
pin in the script without recording it here and the run says so, with the row to paste.

For `cuda` it downloads and verifies both archives and hands them to `scripts/vendor-cuda.ps1`,
which unpacks and then reads the drop back — the CUDA runtime version, `parakeet.dll`'s import
table, the GPU architectures compiled in — because CUDA is the backend where a wrong drop fails
silently. `vendor-cuda.ps1` reads a PE import table against `System32`, so that path is
Windows-only; `cpu` and `vulkan` run wherever `pwsh` does, which is how the Linux CI runner
vendors before it publishes.

The rest of this document is what the script encodes, and what you need when bumping the pin.

## Why it is pinned rather than tracked

Upstream has no Windows CI: `ci.yml` runs `ubuntu-latest` only, and Windows binaries are produced
only at release-tag time by `release.yml` (`runs-on: windows-2022`, publishing cpu, vulkan and cuda
for x64). A build that follows tags therefore takes whatever an untested-on-Windows release
produced, on a schedule nobody in this project controls. Pin a specific release, vendor its binaries
deliberately, and record which one.

## Layout

```
native/
  archives/   the downloaded release zips, kept so a re-run verifies instead of re-downloading
  win-x64/
    cpu/      parakeet.dll  LICENSE
    vulkan/   parakeet.dll  LICENSE
    cuda/     parakeet.dll  LICENSE  (+ the cudart archive's three DLLs)
  win-arm64/
    cpu/
  linux-x64/
    cpu/      libparakeet.so  LICENSE
```

The whole of `native/` is gitignored, `archives/` included, and `build/NativeAssets.targets` globs
only `*.dll`, `*.so`, `*.dylib` and `LICENSE` beneath it, so the zips never reach a build output.

**Keep the `LICENSE` beside the binary.** It is not documentation left over from the archive:
parakeet.cpp is MIT, and MIT requires its notice to be included in all copies of the software.
`build/NativeAssets.targets` copies `native/**/LICENSE` into the build output for that reason, so
unpacking only `parakeet.dll` out of the archive silently redistributes an MIT binary without its
notice. The loader does not care either way, and nothing fails loudly — which is exactly why it is
worth saying here.

`ParakeetNativeLibrary` builds a list of **roots** and walks them once per backend. The roots, in
order (`ParakeetNativeLibrary.CandidateDirectories`):

1. `--native-dir`, resolved to an absolute path
2. `UINDOSILL_PARAKEET_NATIVE_DIR`, resolved to an absolute path
3. `<app>/native/win-x64` — the portable RID, which is what this layout and upstream's archive
   names both use
4. `<app>/native/<RuntimeInformation.RuntimeIdentifier>` — the runtime's own RID, which can be
   version-specific (`ubuntu.24.04-x64` on the CI image), so both are searched
5. `<app>/native`
6. `<app>/runtimes/win-x64/native`
7. `<app>/runtimes/<RuntimeInformation.RuntimeIdentifier>/native`
8. `<app>`

The search is **backend-major**: every root is tried with `/<backend>` appended for the first
backend, then every root again for the next. After that comes one flat pass over the same roots
with *no* backend subdirectory — the shape you get from unzipping a single upstream release — and
finally the OS loader's own search path, for a developer with the library on `PATH` and never how a
shipped build should find it.

Inside each directory the file names tried are `UINDOSILL_PARAKEET_LIBRARY` if set, then
`parakeet.dll`, `libparakeet.dll`, `parakeet_capi.dll`, `parakeet-capi.dll`.

Both `--native-dir` and the environment variable are rooted before use. A relative path would pass
`File.Exists`, which resolves against the working directory, and then be handed to `LoadLibrary`
relative — and Windows only searches a module's own directory for that module's imports when it was
given an absolute path. CUDA is the only backend that ships siblings, so it is the only one this
breaks, and it breaks it into a bare load failure indistinguishable from an absent file.

**Backend order is not "requested, then Vulkan, then CPU".** It is:

| Requested | Order actually tried |
|---|---|
| `cpu` | cpu, **vulkan** |
| `vulkan` | vulkan, cpu |
| `cuda` | **cuda, cpu** — Vulkan is skipped |

Two exclusions for two reasons, and one consequence that surprises people. Nothing ever falls back
*into* CUDA, because it needs its own runtime files and a supported GPU, and landing there silently
turns a missing-file problem into a driver problem. And nothing falls back *from* CUDA into Vulkan:
asking for CUDA costs a 700 MB download to set up, so quietly substituting the other GPU tier would
hide the fact that the thing you went to that trouble for is not running. Dropping to CPU speed is
loud enough to notice.

The surprise is the first row. The guard is written as "not Vulkan and not CUDA", so a **CPU**
request does fall back to Vulkan when no CPU build is vendored. That is the code as it stands, it is
tested, and it is recorded here rather than tidied, because the surprising order is the one worth
writing down.

**The flat pass can make the reported backend a lie.** It tags whatever it finds with the
*requested* backend, because a flat directory carries no evidence of which build it holds. A Vulkan
`parakeet.dll` sitting in `native/win-x64/` will be loaded by a `--backend cuda` run and reported as
`cuda`. The path in `uindosill doctor`'s output is the only thing that distinguishes them, which is
why it prints one. The same applies to the OS-loader last resort, which reports no backend at all
and falls back to the requested value.

If nothing loads, the error lists every path that was tried. That list is the diagnostic — do not
replace it with "library not found".

## Which asset — `lib-`, not `bin-`

v0.5.0 publishes 27 assets in two families, and picking the wrong family is the easiest mistake
here:

- **`parakeet-v0.5.0-bin-win-*-x64.zip`** is the `parakeet-cli` executable. It contains no shared
  library and is of no use to this project.
- **`parakeet-v0.5.0-lib-win-*-x64.zip`** is the shared library. **This is the one you want.**

| Asset | Size |
|---|---|
| `parakeet-v0.5.0-lib-win-cpu-x64.zip` | 719 KB |
| `parakeet-v0.5.0-lib-win-vulkan-x64.zip` | 17.1 MB |
| `parakeet-v0.5.0-lib-win-cuda-x64.zip` | 149 MB (plus `cudart-parakeet-bin-win-cuda-x64.zip`, 553 MB) |

There is **no `win-arm64` asset in v0.5.0**, for either family. Windows on ARM needs a source build.

## File names — verified for v0.5.0

Every `lib-` archive contains exactly four files, and the library is a **single self-contained
`parakeet.dll`** with ggml linked in — there are no sibling ggml DLLs to copy alongside it. **This
holds for CUDA too**, which is worth saying because it is the one place you would expect otherwise:
a ggml CUDA build is often shipped as a separate `ggml-cuda.dll`, and here it is not.

```
parakeet-v0.5.0-lib-win-<backend>-x64/
  parakeet.dll        cpu:      2,008,064 bytes
                      vulkan:  59,453,952 bytes
                      cuda:   169,960,960 bytes
  parakeet_capi.h
  README.md
  LICENSE
```

`parakeet.dll` is the first name the loader tries, so no `UINDOSILL_PARAKEET_LIBRARY` override is
needed.

The `parakeet_capi.h` shipped in v0.5.0 is **byte-identical** to the master header these bindings
were written against (CRLF line endings aside), at ABI version 6.

### CUDA takes a second archive, and it is three files

```
cudart-parakeet-bin-win-cuda-x64/
  cublas64_12.dll     113,712,640 bytes
  cublasLt64_12.dll   692,441,600 bytes     <- most of the download, and most of the disk
  cudart64_12.dll         573,952 bytes
```

Both archives unpack **flat** into `native/win-x64/cuda/`, giving 931 MB on disk. `cublasLt64_12.dll`
alone is 660 MB of it, 71% of the drop. `build/NativeAssets.targets` globs `native/**/*.dll` with
no name or RID filter, so all of it lands in the build output of both apps the first time each is
built, and is re-copied whenever a source file is newer — budget roughly double.

`scripts/vendor-cuda.ps1` does the unpacking, and reads the drop back afterwards: archive digests,
the file list, the CUDA runtime version, `parakeet.dll`'s import table, and the GPU architectures
compiled into it.

### The CUDA build has a dependency the others do not

`parakeet.dll` (cuda) imports **`VCOMP140.DLL`**, the MSVC OpenMP runtime, on top of the
`MSVCP140` / `VCRUNTIME140` imports the CPU and Vulkan builds share. It ships in the Visual C++
2015–2022 redistributable rather than with Windows. A machine without it fails `LoadLibrary`, which
the loader reads as "this backend is not here" and answers by running on CPU without a word. It was
present in `System32` on the machine measured below.

The rest of `parakeet.dll`'s imports are `cudart64_12.dll` and `cublas64_12.dll`, both satisfied
from the same directory, plus the usual API sets. `cublasLt64_12.dll` is **not** among them — it is
cuBLAS that needs it, not parakeet — so do not read its absence from this list as permission to
delete the largest file in the drop.

## Which backend to ship

**Vulkan is the default GPU tier. CUDA is opt-in. CPU is the fallback and is never omitted.**

"Opt-in" is about which channel a user installs rather than which backend the window then selects.
As of 2026-08-20 **both front ends start on the fastest tier whose directory is actually present**,
so an install that has `cuda/` uses CUDA rather than making the user ask for it every launch or
every command — the directory is what the second channel adds, so its presence *is* the opt-in
having happened. One rule serves both, `ParakeetNativeLibrary.PreferredBackend`, so an install
cannot disagree with itself.

A choice made in the window overrides it and is remembered; `--backend` overrides it per command,
and `--backend vulkan` pins exactly what a bare invocation did before. Because CUDA falls back to
CPU and never to Vulkan, `transcribe` now reports on stderr whenever the backend that loaded is not
the one that was asked for — without that line, a CUDA drop with no working driver behind it would
silently run twelve times slower than intended.

Vulkan runs on NVIDIA, AMD and Intel with only a normal graphics driver, and skips the ~553 MB
cudart download. Both come from the same upstream build matrix. `cjpais/Handy` already ships
ggml-Vulkan on Windows in production.

CUDA's packaging advantage is real: the CUDA runtime ships as a **separate cudart archive** (the
llama.cpp convention), so the end user needs no CUDA Toolkit. That is the single biggest packaging
advantage over ONNX Runtime's CUDA execution provider — but it is still roughly 700 MB to download
(both archives) and 931 MB on disk, which is why it is opt-in.

It is also **1.70× faster than Vulkan in the steady state, and 3.6× faster on the first run after a
fresh install**, on the one machine where both have been measured — see `docs/UNPROVEN.md` for the
numbers and their limits, and gotcha 20 for why those two figures differ. That does not change the
default. 1.70× off Vulkan's own 0.011 of real time buys 2.7 seconds of decode on a ten-minute
file, against 700 MB of download and one vendor's hardware; Vulkan runs on NVIDIA, AMD and
Intel with a driver the user already has. CUDA earns its place as an opt-in for people who
transcribe hours at a time, which is exactly where it sits.

## libmpv — the video player, and the licence that comes with it

The Ask tab plays video through **libmpv**, the client library of the mpv media player, vendored the
same way parakeet.cpp is: pinned to a release, verified by digest, unpacked by a script. What is
different is that this one changes the licence of everything that ships with it, so read the second
half of this section before adding it to a build.

```
native/
  win-x64/
    mpv/   libmpv-2.dll  GPL-2.0.txt  mpv-Copyright.txt  mpv-WRITTEN-OFFER.txt
```

```bash
pwsh scripts/vendor-mpv.ps1        # about 31 MB down, 114 MB on disk
```

**The pin, as of 2026-08-23.** shinchiro/mpv-winbuild-cmake publishes dated releases and names the
mpv commit in the asset:

| | |
|---|---|
| Release | `20260814` |
| Asset | `mpv-dev-x86_64-20260814-git-7b8915bc1d.7z` |
| Bytes | 31,181,976 |
| SHA-256 | `0af22b28e920620036d3ae08fd9283156dc9af0420bf4df84b0e02282094599c` |
| `libmpv-2.dll` bytes | 119,757,824 |
| `libmpv-2.dll` SHA-256 | `f709c7ca8b183bec76b8158bf0c45c53018c63366750729352612f228ff7bdea` |
| mpv client API | 2.5 |

`vendor-mpv.ps1` fails if either digest above stops appearing in this file, the same guard
`vendor-natives.ps1` carries and for the same reason.

**Why this build and not another.** It is the only Windows libmpv published as a single statically
linked DLL with no sibling dependencies — the parakeet CUDA drop's three companions are exactly the
kind of thing that makes a loader path fragile, and this has none. The `-v3` variant in the same
release targets the x86-64-v3 microarchitecture (AVX2 and friends); **this project takes the plain
one**, for the reason the instruction-set section below gives: a v3 baseline can execute AVX2 from a
static initialiser and kill the process at load on an older CPU, uncatchably, presenting as "the app
won't launch".

**The `.7z` needs 7z on PATH**, which the parakeet `.zip` archives did not. That is upstream's
choice of container, not a preference here.

**Only `libmpv-2.dll` is unpacked.** The archive also carries `include/mpv/*.h` and an import
library; the headers were read to write `Services/Mpv/MpvNative.cs` and the build has no use for
either. The interop is hand-written against them rather than taken from a binding package, for the
reason `Parakeet.Engine.ParakeetCpp` gives: a package's idea of the ABI has to be reconciled with
the binary actually pinned.

**How it is found.** `Services/Mpv/MpvNativeLibrary` searches `UINDOSILL_MPV_NATIVE_DIR` if set,
then `<app>/native/win-x64/mpv`, then `<app>/native/mpv`, then `<app>`; the file names tried are
`libmpv-2.dll` and `mpv-2.dll`. Both are ABI major 2, so accepting either costs nothing. Paths are
rooted before use, for the reason the parakeet loader roots its own.

**Video is a property of the build, not a setting.** `MediaPlayers.ForThisBuild()` asks whether the
library is on disk: present, and the Ask tab plays picture and sound through mpv; absent, and it
plays sound through Media Foundation and WASAPI and says on the tab that a video's picture is not
being drawn. There is no switch, nothing downloads it, and a missing library is never an exception
out of a `DllImport`.

### It makes the whole distribution GPLv2+

**This is the part that is not like the other natives.** parakeet.cpp is MIT; ONNX Runtime is MIT;
libmpv is **GPL version 2 or later**, and this build links FFmpeg-GPL and other GPL libraries.
Distributing it inside an application makes the combined work a GPL distribution.

**The decision to accept that was taken on 2026-08-23 and it changed the project's licence.**
Uindosill's own source stays MIT — that is its own file's terms and nothing here revokes them — but
**a build that vendors libmpv is distributed under GPLv2+**, and the repository's `LICENSE`,
`NOTICE.md` and `docs/LICENSING.md` say so. `docs/PHASES.md` § *Decided 2026-08-23* records why that
was preferred to the alternatives.

**What the script enforces, because a licence breach fails silently.** Three notices are copied from
`licences/` into `native/win-x64/mpv/` beside the DLL, and the run fails if any is missing:

- `GPL-2.0.txt` — the licence text itself. GPLv2 §1 requires it to travel with the binary.
- `mpv-Copyright.txt` — mpv's own licensing summary, at the pinned commit.
- `mpv-WRITTEN-OFFER.txt` — where the corresponding source is, with the exact revisions, plus the
  three-year written offer GPLv2 §3(b) describes.

The upstream archive contains **no licence text at all**, which is why these come from this
repository rather than from the download. `build/NativeAssets.targets` was widened to copy
`native/**/*.txt` so they reach the build output the way the parakeet `LICENSE` does.

**An LGPL libmpv would avoid all of this and does not exist as a prebuilt.** mpv can be built with
`-Dgpl=false` against an LGPL FFmpeg, which produces an LGPL libmpv that could be shipped beside MIT
code. No such Windows binary is published — checked 2026-08-23 across shinchiro's releases and the
SourceForge mpv-player-windows builds, neither of which offers one — so taking that route means
building and maintaining a toolchain rather than pinning a file. That was weighed and declined; see
the PHASES entry. If one ever appears, this is the section to change.

## yt-dlp, Deno and ffmpeg — taking a link, and putting a transcript back in a file

Paste a link and the application downloads its audio track, transcribes it like any other file, and
— on the Ask tab — streams the picture back from the same link rather than keeping a copy of it.
Hand it a finished transcript and it can put that back inside the recording as a subtitle track.
Three binaries do those two jobs, vendored the same way the others are.

```
native/
  win-x64/
    tools/   yt-dlp.exe  deno.exe  yt-dlp-LICENSE.txt  deno-LICENSE.txt
    ffmpeg/  ffmpeg.exe  ffmpeg-LICENSE.txt
```

**ffmpeg is in a directory of its own, and that is load-bearing rather than tidy.** yt-dlp looks for
ffmpeg beside its own executable before it looks at `PATH`. Measured 2026-08-23: the same yt-dlp
binary reports `exe versions: none` alone in a directory and `exe versions: ffmpeg n9.0.1` with
ffmpeg next to it, on an identical `PATH`. So dropping the muxer into `tools/` silently changes what
a download produces — and retires the check below that says both of this application's readers open
what yt-dlp writes today. Nothing here needs yt-dlp to have a muxer: the one thing that does is
`FfmpegSubtitleMuxer`, which runs it by absolute path. `BundledTools` therefore searches two
different directory lists, `UINDOSILL_FFMPEG_DIR` overrides the muxer's independently of
`UINDOSILL_TOOLS_DIR`, and `BundledToolsTests.TheMuxerIsNotVendoredBesideYtDlp` fails if the two
ever end up together.

**yt-dlp is given the muxer anyway — by name, on the command line.** Keeping the binaries apart is
not about withholding it; it is about the difference between a wiring decision and an accident of
file placement. `MediaUrlFetcher` passes `--ffmpeg-location <absolute path>`, so a download that has
a muxer says so, and one that does not is a build that vendored no ffmpeg rather than a build whose
files moved. mpv spawns its own yt-dlp for streaming and is not given one, which costs nothing: no
file is written on that path.

```bash
pwsh scripts/vendor-tools.ps1     # about 207 MB down, about 230 MB on disk
```

**The three are independent.** A drop with ffmpeg and no yt-dlp adds transcripts to files and cannot
open links; the reverse does the opposite. `BundledTools` asks about each separately rather than
through one "the tools are present" flag, so a half-drop disables the half it affects and says so.

**The pins, as of 2026-08-23.**

| | yt-dlp | Deno | ffmpeg |
|---|---|---|---|
| Release | `2026.08.19` | `v2.9.5` | `autobuild-2026-08-22-12-58` |
| Asset | `yt-dlp.exe` | `deno-x86_64-pc-windows-msvc.zip` | `ffmpeg-n9.0.1-6-g9d4ca21220-win64-lgpl-9.0.zip` |
| Download bytes | 17,840,399 | 42,691,248 | 147,007,729 |
| Download SHA-256 | `66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a` | `171efab55ac6b9881fd53ee4c20f8bf3bb1340ffc618483746909014db12216a` | `20f84639fae87181bb1c9899c34ce05cd3c0b533c68d3ff34206a2615da94f30` |
| Installed bytes | 17,840,399 | 97,408,288 | 114,400,768 |
| Installed SHA-256 | (the same file) | `98f8c2a2d470e4ccb04c935c86ff8050817d877762aec5eaeeb9e409ccb3b9fd` | `8a5ce69fbb74b4c9e0e24c214e3def0e1847a05051a8e1c6d10b1d4a35bd6a65` |
| Licence | Unlicense (public domain) | MIT | **LGPL-3.0** |

**ffmpeg is the LGPL build and not the GPL one beside it, and that is a licence decision rather than
a preference.** Putting a transcript inside a recording copies every stream and encodes nothing, so
none of it needs a GPL-only encoder: the three subtitle codecs involved — `mov_text`, `subrip` and
`webvtt` — and the two muxers — `mp4` and `matroska` — are all core FFmpeg. BtbN's GPL build ships
**GPLv3**, which this project has no reason to take on; the LGPL build is **LGPLv3**, 30 MB smaller,
and was driven over all eight input-and-format routes before it was kept. It is a separate program
this application spawns, not a library it links, so it travels as an aggregate under its own licence.

**The version is the one the rules were measured against.** Every container decision in
`Parakeet.Core.Muxing.SubtitleMux` — and there are several that a specification would get wrong — was
measured against FFmpeg 9.0.1, and `n9.0.1-6` is that release branch rather than a master snapshot.
Bumping this pin means re-running those measurements, not just the digests.

**Its zip is nested where Deno's is flat**: `ffmpeg-<version>/bin/ffmpeg.exe` rather than a file at
the root, which is why the pin carries `Nested` and the extraction recurses. Without that, `7z e`
matches nothing, reports success, and the run fails at the read-back with the file simply absent.

Both digests were compared against upstream's own published sums when the pins were taken:
yt-dlp's `SHA2-256SUMS` and Deno's `.sha256sum` beside each asset. The script checks against the
pins here rather than against those files, because a digest fetched from the same place as the
binary proves only that the two agree.

**Deno is not an optional extra.** yt-dlp needs a JavaScript runtime to answer YouTube's signature
challenge, and its documentation enables exactly one by default: *"Supported runtimes are (in order
of priority, from highest to lowest): deno, node, quickjs, bun. Only `deno` is enabled by default."*
Without it, YouTube extraction degrades or fails. A drop with yt-dlp and no Deno is a half-drop and
`BundledTools.DescribeUnavailable()` names which half is missing rather than saying "unavailable".

**How they are found, and how they find each other.** `Services/Tools/BundledTools` searches
`UINDOSILL_TOOLS_DIR` if set, then `<app>/native/win-x64/tools`, then `<app>/native/tools`. This
application spawns yt-dlp itself and passes `--js-runtimes deno:<absolute path>`, so that path is
exact. **mpv** also spawns yt-dlp — that is how a link streams — and cannot be told about our
layout, so `BundledTools.PrependToPath()` puts the tools directory at the front of *this process's*
`PATH` (process-local; nothing is written to the machine or the user environment) and mpv is handed
`ytdl_hook-ytdl_path` pointing at the pinned binary. Prepending rather than appending is deliberate:
a different yt-dlp already installed on the machine must not silently take over from the pinned one.

**Audio is downloaded as m4a on purpose, and it is a measured choice.** YouTube's "best audio" is
usually Opus in WebM, which `AudioSources.SupportedExtensions` does not list and which Media
Foundation cannot decode on a stock Windows install — so a best-audio download would produce a file
this application then refuses. The selector is
`bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio`, which gets AAC where the site has it.

**No ffmpeg is vendored, and that was checked rather than assumed.** Without ffmpeg on `PATH`,
yt-dlp writes what it calls a DASH m4a and warns that *"Only some players support this container"*.
Both of this application's readers were tested against one on 2026-08-23 — Media Foundation through
`SystemAudioPlayer` and libmpv through `MpvMediaPlayer` — and **both open it and report the correct
9:56 duration**. So the warning does not apply here, and roughly 100 MB of ffmpeg stays out of the
installer. If a future container needs remuxing this is the decision to revisit.

**That decision was revisited later on 2026-08-23 and half-reversed.** ffmpeg is vendored now — not
for yt-dlp, whose DASH m4a still needs nothing, but because putting a transcript back inside a
recording is a remux and there is no other way to do one. The paragraph above stands as the record
of why it was not there: the reasoning was right and the requirement changed.

**What a download produces did change, and it was measured on the same video the same day.** Big
Buck Bunny (Blender Foundation, CC-BY) — the 9:56 the paragraph above is about — downloaded twice
with this application's own arguments:

| | without ffmpeg | with ffmpeg |
|---|---|---|
| Container | `ftyp iso6`, `major_brand: dash`, `mvex`/`trex` present — fragmented | `ftyp isom mp41`, no `mvex` — plain MP4 |
| Bytes | 9,655,276 | 9,648,639 (−6,637, the fragment index) |
| yt-dlp says | *"writing DASH m4a. Only some players support this container"* | `[FixupM4a] Correcting container` |
| Audio samples | 105,226,240 bytes decoded | **bit-identical** |
| `AudioSources.Open` | 44,100 Hz, 00:09:56.5206292, 26,306,560 samples | **identical** |

So the fixup buys *this application* nothing, exactly as the paragraph above found — and it buys the
person who opens the downloaded file in something else a container that is not the one yt-dlp warns
about. That is why it is on. The cost is one remux pass over the downloaded audio, which is seconds
on a podcast-sized file and has not been measured on a long one.

**The first drop got this wrong by accident**, which is worth recording because nothing announced it:
putting ffmpeg.exe into `tools/` gave yt-dlp a muxer with no code change at all, because it searches
its own directory before `PATH`. The separation above plus `--ffmpeg-location` is what turns that
into a decision.

**mkvmerge was measured against ffmpeg for this job on 2026-08-23 and rejected**, which is worth
recording because the result is backwards. MKVToolNix writes WebVTT under `S_TEXT/WEBVTT`, the
identifier Matroska actually specifies; FFmpeg writes `D_WEBVTT/SUBTITLES`, the older WebM one.
FFmpeg's demuxer reads its own and not the specified one — it reports the track's codec as `none`
and refuses to decode it, while carrying a perfectly good WebVTT decoder. This application plays
through libmpv, which *is* FFmpeg, so a file muxed by the more correct tool is a file whose subtitles
our own Ask tab cannot show. mkvmerge also cannot write MP4 at all. SubRip is unaffected — both
write `S_TEXT/UTF8` — so the split is specific to WebVTT, which is exactly the format the word-level
timing rides on.

**None of the three makes the application copyleft.** Unlicense and MIT are permissive; ffmpeg is
LGPLv3 and is spawned as a separate program rather than linked, so unlike libmpv it does not reach
the application's own terms. Their notices still travel: `vendor-tools.ps1` writes them
beside the binaries and refuses to finish without them, and `build/NativeAssets.targets` carries
`native/**/*.txt` and `native/**/*.exe` into the output.

**What this adds to an installer.** About 230 MB, on top of libmpv's 114 MB — ffmpeg alone is
114 MB of it, which is the single largest thing this product vendors after the models. Nothing has
been packaged with any of them yet, and what they do together to the channel size and to Velopack's
deltas is unmeasured — see `docs/UNPROVEN.md`.

**A note on what users do with it.** yt-dlp downloads what its user asks it to. Whether a particular
download is permitted by a particular site's terms, or by copyright where the user lives, is the
user's responsibility and not something this application checks or could. The feature exists because
transcribing a recording you are entitled to is an ordinary thing to want.

## Check the instruction-set baseline of everything you vendor

This is not optional diligence. A native compiled with an `/arch:AVX2` baseline can execute BMI2 or
AVX instructions from a **static initialiser** and kill the process at load on a pre-Haswell CPU:
uncatchable, no stack trace, presents to the user as "the app won't launch". `cjpais/Handy` hit this
through a prebuilt ONNX Runtime and dropped a feature over it.

For ggml builds, `GGML_NATIVE=OFF` targets a portable baseline and `GGML_CPU_ALL_VARIANTS` gives
runtime ISA dispatch. If you build the library yourself, use them.

`uindosill doctor` probes each backend in a **child process** precisely so that this failure shows up
as a diagnosis instead of taking the tool with it.

## After vendoring

```bash
uindosill doctor
```

Expected: each present backend reports `ok — abi 6 from <path>`. An ABI other than 6 is refused
loudly rather than adapted to; the signatures and ownership rules differ between versions, and
guessing corrupts memory rather than failing cleanly.

**Read the path, and know what `ok` does not cover.** The probe calls exactly one entry point,
`parakeet_capi_abi_version`, which returns an integer and touches no GPU state. So `ok` means the
DLL and every one of its dependencies resolved — a real result, and the one that catches a missing
`VCOMP140.DLL` or cudart. It does **not** mean the library can decode on this GPU: a CUDA build with
no kernels for the installed card would load, answer the ABI question, and report `ok` here before
failing at the first kernel launch. Only a transcription tells you that. The path matters for the
separate reason above: the loader's flat pass will report a Vulkan build found in an unqualified
directory as whatever backend you asked for.

**One thing this codebase uses that is not in the C ABI.** The exit-time abort in gotcha 19 is
prevented by calling upstream's `pk::shutdown_backend()`, which `parakeet_capi.h` does not declare;
it is reached through its exported C++ name, `?shutdown_backend@pk@@YAXXZ`, which every v0.5.0
`lib-` build carries only because upstream exports every symbol (2,090 of them, checked with
`cdb -z parakeet.dll -c ".symopt- 2; x parakeet!*shutdown_backend*"` on all three). A future build
that exports only the C ABI would still load, still report `ok`, and still decode — and a CUDA
process would go back to exiting `0xC0000409` after a good run. The probe checks for the export
after the ABI question and prints a warning line under that backend when it is absent, so a
re-vendored release that has lost it says so here. If a release drops the export, ask upstream to
add the call to the C API rather than patching around it: `docs/GOTCHAS.md`, gotcha 19, has the
measurement that makes the case.

Record here, per release: the tag, the archive names, the SHA-256 of each archive, and the ISA
baseline you confirmed. `scripts/vendor-natives.ps1` carries the same digests and byte counts as
its pins, and fails any run whose trusted digest is not in this table — so a new release is a new
row here *and* a new pin there, and the script is what notices when only one of them happened.

| Release tag | Archive | SHA-256 | ISA baseline | Vendored on |
|---|---|---|---|---|
| v0.5.0 | `parakeet-v0.5.0-lib-win-cpu-x64.zip` | `0e9b8a305bf25a485b27bbcb2496fbd5bc8a0653d39c24a76c87a2053966a453` | **unconfirmed** | 2026-08-14 |
| v0.5.0 | `parakeet-v0.5.0-lib-win-vulkan-x64.zip` | `4527898049ee1566c4b3e12c8a40ddcce154d2fc5c1661ac00a95b64cd6e512c` | **unconfirmed** | 2026-08-14 |
| v0.5.0 | `parakeet-v0.5.0-lib-win-cuda-x64.zip` | `be61348d3e1ea60059c141ae3eda7f04bd69bea80ecc689f96bc47a6a1691016` | **unconfirmed** | 2026-08-14 |
| v0.5.0 | `cudart-parakeet-bin-win-cuda-x64.zip` | `cc2b5fb99951720130e4a701e0978419d0a878e25c88bebc1416152616bd1d94` | n/a — CUDA runtime | 2026-08-14 |

The digests are of the archives as served by GitHub and were computed by downloading them —
735,995 bytes for cpu, 17,945,091 for vulkan, and 156,486,028 and 580,185,113 for the two CUDA
archives. All three backends were vendored and loaded on one Windows 11 x64 machine on 2026-08-14;
`uindosill doctor` reported `ok — abi 6` for each, from its own backend directory. On 2026-08-15
`scripts/vendor-natives.ps1` downloaded the cpu and vulkan archives afresh and they hashed to the
same two digests; the publish output it produced loaded both from their own directories.

The x86 ISA baseline is **still not confirmed** for any of them. Nobody has inspected these binaries
for an AVX2 requirement or run them on a pre-Haswell CPU, and loading successfully on one Zen 5
desktop says nothing about that — that CPU has AVX-512, so it could not have exposed an AVX2
baseline even if there is one. `uindosill doctor` is what tells you, and it is why it
probes in a child process.

## GPU architectures compiled into the CUDA build — confirmed

This is the question that decides whether the prebuilt CUDA library is usable on current consumer
hardware at all, and upstream records no answer: Windows binaries are produced by a release job with
no CI, and the CUDA toolkit version is written down nowhere.

Read out of the binaries themselves by `scripts/vendor-cuda.ps1`:

| File | Cubins | PTX |
|---|---|---|
| `parakeet.dll` | `sm_86`, `sm_89`, **`sm_120`** | `compute_50`, `61`, `70`, `75`, `80`, `90` |
| `cublas64_12.dll` | `sm_50` … `sm_100`, `sm_120` | `compute_120` |
| `cublasLt64_12.dll` | `sm_50` … `sm_100`, `sm_120` | `compute_52`, `compute_120` |

`cudart64_12.dll` reports file version `6.14.11.12080`. The last field is `CUDART_VERSION`, which is
`1000·major + 10·minor`, so **12080 is CUDA 12.8** — the first toolkit able to emit `sm_120`.
`cublas64_12.dll` and `cublasLt64_12.dll` both report `6.14.11.1283`, which is *not* that encoding
and is read as cuBLAS 12.8.3 by shape alone: consistent with 12.8, not independent confirmation of
it.

So an RTX 50-series card gets native Blackwell kernels with no JIT, and pre-Blackwell cards older
than `sm_86` get PTX and pay a driver JIT on first use. Confirmed on the hardware: a first-ever CUDA
run and a second run finished 7 s and 6 s wall-clock, and the driver JIT would have cost minutes on
the first and nothing on the second.

**How much to trust the table.** 136 of 136 containers in `parakeet.dll` and 200 of 200 in
`cublas64_12.dll` walked to their stated end exactly; `cublasLt64_12.dll` parsed 3,667 and rejected
48. Rejection is all-or-nothing per container, so **any file with rejections has an architecture
list that is a lower bound, not a census** — read the `containers N parsed, M rejected` line the
script prints. The three files did not produce the *same* vocabulary, which would be surprising for
independently built libraries; they produced coherent and overlapping ones.

What did *not* happen is the stronger check. No cubin payload in this drop could be read back — all
of them are compressed, or the payload offset is wrong — so none was cross-checked against its own
ELF header, and the offsets the walk uses are not published by NVIDIA. **The `sm_120` row is
corroborated by the run, not by a second parse.**

## The second native stack: llama.cpp, for the v2 language-model tier

v2's `llama-server` child process (`docs/V2-ASK-THE-TRANSCRIPT.md`, decision 1) brings a second
vendored stack, under the same rules and its own script — `scripts/vendor-llm-natives.ps1`, which
fails any run whose trusted digest is not in this table, exactly as `vendor-natives.ps1` does with
the table above. The drop lands under `native/win-x64/llm/<backend>/` and is pruned to the server
set — `llama-server.exe` and the DLLs — because a llama.cpp zip carries a dozen lab tools and
`build/NativeAssets.targets` globs every `native/**/*.exe` into every build output.

**What ships (since 2026-08-24): the vulkan drop, in both installer channels.** Two decisions,
recorded in `scripts/package-windows.ps1`'s channel table where they are enforced: no separate
`llm/cpu` drop ships, because these zips are built with `GGML_BACKEND_DL` and the vulkan drop
carries every per-ISA CPU variant beside `ggml-vulkan.dll` — whether the server actually falls
back to them on a machine with a broken Vulkan driver is recorded as unmeasured in
`docs/UNPROVEN.md`; and no `llm/cuda` ships yet, because its cudart-13.3 is a second CUDA
runtime major (~391 MB) beside the ASR tier's cudart-12.8, and that is the maintainer's open
decision, not a packaging line item.

The pin is a release **tag**, never "latest": upstream marks its build releases as prereleases,
so the GitHub `releases/latest` endpoint answers with something that is not a build at all
(observed 2026-08-23). No llama.cpp release zip ships a LICENSE (measured at b10448 and b10603
alike), so the MIT text travels from the source tree at the pinned tag — 1,078 bytes,
`94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d` — and the script writes it
beside the binaries in each backend directory.

| Release tag | Archive | SHA-256 | Vendored on |
|---|---|---|---|
| b10603 | `llama-b10603-bin-win-cpu-x64.zip` | `878efa5bc0cdeb9c3fcb96335521556e06ca9252f83de3a1d924981918607702` | 2026-08-23 |
| b10603 | `llama-b10603-bin-win-vulkan-x64.zip` | `8e2fa4ef100af6e4a08f7d9cf9686ee40b1349e6c11933efd63f4e68f9261d2e` | 2026-08-23 |
| b10603 | `llama-b10603-bin-win-cuda-13.3-x64.zip` | `687a4e750e89790491802fa369f4541763f7e8d43cb27f0d3cf2e4fc4063258d` | 2026-08-24, desktop |
| b10603 | `cudart-llama-bin-win-cuda-13.3-x64.zip` | `1462a050eb4c684921ba51dcc4cc488a036674c3e73e9945ee705b854808d03e` | 2026-08-24, desktop |

The digests are the `digest` field the GitHub releases API serves per asset (read 2026-08-23),
and every download is re-hashed against them locally — 18,063,576 bytes for cpu, 34,400,125 for
vulkan, 146,422,151 and 390,970,417 for the CUDA pair; the desktop's 2026-08-24 vendoring
reproduced both CUDA digests exactly. The cudart archive's bytes are identical to the ones read
beside b10448 on 2026-08-16, so the runtime does not churn with the builds. The b10603
`ggml-cuda.dll` (141,895,168 bytes) was scanned on the desktop on 2026-08-24 with
`scripts/vendor-cuda.ps1 -InspectOnly`: **`sm_86`, `sm_89`, `sm_120`, `sm_121` cubins and PTX
`compute_75`, `compute_80`, `compute_90`** — 142 containers parsed, 0 rejected, the same list the
b10448 scan read, so the reading survives the re-pin. The same caveat as every row above: all
payloads are compressed, so nothing was read back against its own ELF header, and the walker does
not tell `120a` from `120`. The cudart beside it identifies as CUDA 13.3 (`cudart64_13.dll`,
file version 13030), above the 12.8 floor that first emits `sm_120`. Execution is a separate
claim from the scan and is recorded in `docs/UNPROVEN.md` § *The engine on the product path*.
