# Uindosill

A Windows desktop app that transcribes audio and video files **locally** with NVIDIA Parakeet, and plays them back beside their transcript.
Drop files in, get text out — plain text, SRT, VTT, word-timed VTT for karaoke-style highlighting,
JSON with timestamps, Markdown. No cloud, no account, and **no Python you have to install** — the
two opt-ins below run in an interpreter that ships inside the application. That bundle does not
exist yet: the code looks for it beside the executable and says so when it is not there, and
nothing has packaged one.

> **Status: the CLI and the desktop app both produce correct transcripts from real weights on real
> Windows.** Ten minutes of podcast through Media Foundation, parakeet.cpp v0.5.0,
> `tdt-0.6b-v3-f16`, on one 16-core x64 desktop with an RTX 5080 — **RTF 0.082 on CPU, 0.011 on
> Vulkan, 0.0064 on CUDA** — 98 segments and 1,573 words on all three, no duplicated or dropped
> words at any segment join. Every RTF here is the whole pass — the container decode, the
> resampling and the segmentation run inside the timed stretch, serialised with the model — which
> is a rounding error against a CPU decode and a material share of a fast GPU one; since 2026-08-22
> the transcript also carries the model's own decode time beside it; re-timed on the desktop
> 2026-08-22 with the read separated (on a different 600 s cut of the same episode), the model is
> about two thirds of the CUDA pass — 2.59 s of 3.95 s, RTF 0.0043 against 0.0066 — three quarters
> of the Vulkan pass and 96 % of the CPU one, and `docs/UNPROVEN.md` says what each figure contains
> and which have been re-timed. Three hours has been run end to end on CPU. The Vulkan figure is
> steady-state: the *first* Vulkan run on a fresh machine takes 14 s rather than 6.6 s, because the
> driver is compiling shaders inside the number that looks like decode time. A second machine — a
> Ryzen AI 9 365 laptop with an integrated Radeon 880M — has since been measured: **RTF 0.14 on
> CPU, and Vulkan does not load the model there at all** unless bf16 is disabled before the load,
> an upstream defect in how the driver's bf16 support is requested (RTF 0.035 with it disabled).
> That workaround is now the default: on the RTX 5080 it measured at 0.3% and byte-identical
> transcripts against leaving bf16 on, and `--vk-bf16` turns it back off. Two machines and two
> ten-minute files is still not a benchmark. Every figure here carries its backend and its
> caveats in [UNPROVEN.md](docs/UNPROVEN.md). All five quantisations have now been run
> against 2 h 55 m of real podcast and diffed against f16 — 0.42% of tokens for q8_0 rising to
> 2.69% for q4_k, over a CPU-versus-CUDA noise floor of 0.11%, with no sign of the silent collapse
> that sank the analogous ONNX INT8 export. That is divergence from f16, not a word error rate —
> and the word error rate now exists too: **all five entries scored against eleven hours of
> human-transcribed accented English earnings calls (Rev's Earnings-22 subset, two transcript
> styles), on CUDA — f16 10.2%, and every quantisation within 0.08 points of it**, so on that
> corpus no quantisation costs measurable accuracy. The normaliser is this project's own, not the
> leaderboard's, so that figure is comparable to itself and not to a published one; one English
> corpus of one genre is the whole of the evidence, and [UNPROVEN.md](docs/UNPROVEN.md) says
> exactly what it does and does not cover. **The four quantisations were withdrawn from the
> catalogue on 2026-08-20 and the product now offers f16 alone** — a product decision rather
> than a quality one, since the measurement above is exactly what makes it cheap; the reasoning
> is in [PHASES.md](docs/PHASES.md).

## What it does

- **A link works like a file.** Paste one and the audio track is downloaded and queued for
  transcription; on the Ask tab the picture streams back from the same link rather than being kept
  on disk, so a three-hour video costs a few megabytes instead of a few gigabytes. Two pinned
  binaries do it — yt-dlp and the Deno runtime it needs for YouTube — both permissively licensed,
  and a build without them says so instead of offering a dead box. One link has been tried;
  [UNPROVEN.md](docs/UNPROVEN.md) says what that does and does not cover.
- **v1 is file transcription, with optional speaker labels.** No global hotkeys, no text injection,
  no overlay HUD, no microphone capture. **Who spoke when is an opt-in**, off by default: turn it on
  and every format gains `Speaker 1:`, and `rttm` becomes available. It costs a second read of the
  file and a second model — NVIDIA's Streaming Sortformer, a separate 453 MiB download — and it
  **tells apart at most four speakers**, which is architectural rather than a setting: a fifth voice
  is merged into one of the four and the product says so rather than degrading quietly. Its labels
  are also **established only up to fifty minutes** — past that this model tends to hear one person
  as two, so the window asks how many speakers there are and folds its labels down to that, rather
  than estimating a number it is measured to get wrong. Measured on
  the AMI meeting corpus at **16.3% diarisation error rate** (collar 0, overlap scored) against the
  best published figure on the same audio, 18.8%. **That figure names its backend**: 16.3319% on
  WebGPU, which is the default, and 16.3324% on the CPU — 0.0005 points apart, and that agreement
  is why the default is WebGPU rather than CUDA's faster 16.1021%, since a figure only one provider
  reproduces describes whoever measured it. What that does and does not cover is in
  [UNPROVEN.md](docs/UNPROVEN.md), and it covers no podcast audio at all.
- **An English version of the transcript is v1's second opt-in, and it now translates.** Decided
  2026-08-19 and aboard v1.0 rather than deferred, because a release that transcribes 25 languages
  and can only hand back 25 languages is a narrower product than the one intended. `--translate`
  reads real weights as of 2026-08-20: a Marian checkpoint exported here to ONNX, nine files and
  1.34 GiB, decoded at beam 6 by HuggingFace's own beam search inside the bundled Python — on
  WebGPU where it loads, then CUDA, then the CPU. **The provider changes the English, not only the
  speed**, so it is picked for faithfulness: on 32 FLEURS sentences WebGPU returned the CPU's own
  translations on 32 of 32 at 1.30x the speed, and DirectML on 0 of 32 while falling into a
  repetition loop, so DirectML is refused by name. The SentencePiece tokenizer and the beam search
  written for this project decoded it until 2026-08-21 and are now in `attic/`.
  `uindosill translate` runs the same pass over a text file with no audio at all. In the app it is
  a checkbox beside the speaker one, and the English arrives *beside* the transcript rather than
  instead of it — a switcher over the transcript pane shows either, with the same times and the
  same speakers on both sides.
  **The gate it was written against is not passed**, and that is a statement about a criterion
  nobody has performed rather than about a score: chrF++ clears its per-language bar in 23 of 24
  languages and Slovak misses by 0.74, and the human adequacy check has been declined. Those scores
  came from `optimum` and `transformers` over these same graphs at beam 6, which is the decode the
  sidecar now runs — and on 2026-08-21 the 8,149-sentence run **was** repeated against the sidecar
  itself, which reproduced every recorded hypothesis character for character: **8,149 of 8,149, all
  24 languages at 100%**, on WebGPU against the gate's CPU. One machine, so the figures describe
  this decode rather than every machine's. Translation carries no word timings, and the
  word-timed subtitle format is refused rather than written against times that no longer fit the
  words. What is measured and what is not is in
  [UNPROVEN.md](docs/UNPROVEN.md); the decision is in [PHASES.md](docs/PHASES.md).
- **v2 is asking questions about a transcript.** A chat panel beside the text, where every answer
  cites timestamps you can click. **The asking is not built and the panel says so** — it is drawn,
  disabled, and covered by a work-in-progress notice, because there is no language model in this
  application and which one it should be is still open. **The half that needs no model shipped
  2026-08-22**: the app has an Ask tab where a recording plays, its transcript sits beside it as
  cues you click to jump to that moment, the line being spoken lights up as it goes, and a find box
  marks every mention of a word and steps between them with Enter. All of that runs on times v1
  already writes. **A video plays its picture too, as of 2026-08-23**, through a vendored libmpv —
  which is why a build carrying it is GPL rather than MIT; see the licence section below. A build
  without it plays a video's sound and says on the tab that it is not drawing the picture.
  Playback needs a Windows audio device and, for video, the vendored library, so **nothing in the
  suite runs either player** and both were driven by hand instead — an m4a, an mp3, a WAVE file and
  an H.264 mp4 on one laptop, where the device opens, the clock runs at real time, seeks land where
  the transcript says, and 30 fps video renders at the full rate. That found two defects in the play
  button, both fixed. **Nobody has yet written down that they heard or watched it**, which is a
  different claim; [UNPROVEN.md](docs/UNPROVEN.md) keeps the two apart. The open decisions are in
  [V2-ASK-THE-TRANSCRIPT.md](docs/V2-ASK-THE-TRANSCRIPT.md).
- **v3 is push-to-talk dictation.** Not built, not architected out —
  [V3-DICTATION.md](docs/V3-DICTATION.md) records what it will need.

That order is deliberate, and for two different reasons. The entire Win32 risk surface — global
keyboard hooks that get flagged as keyloggers, text injection that fails silently under UIPI,
overlay windows that steal focus — lives on the dictation path and none of it on the file path,
which is why dictation is last. Asking questions about a transcript sits in front of it because it
needs none of that: it reads a transcript this product already produces, and what it costs instead
is a second native stack and an honesty problem, since a wrong answer is fluent rather than
obviously broken.

## Getting it

**There is an installer, and no release to download it from yet.** Packaging is built —
a `v*` tag produces a Windows installer for the desktop app in two flavours, the CLI as a zip beside
it, and the bundled Python as a third zip for CLI users — unpack that one into
`%LOCALAPPDATA%\Uindosill` and `uindosill diarise` and `transcribe --translate` find it, since the
CLI zip carries no interpreter of its own — but no version has been tagged, so the releases page is
empty. Until one is, there are
two ways to run this: build it from source, below, or take the `uindosill-win-x64` artefact from any
CI run of `master` — a self-contained publish of the CLI and the desktop app with the cpu and vulkan
natives already in place, kept for seven days. Both still need a model, which the CLI or the app's
Models tab downloads.

When a release does arrive, three things are worth knowing before you download it:

- **It is unsigned**, by decision rather than oversight, so expect Windows to warn about an unknown
  publisher. Nobody here has seen that dialog — both installers were only ever run silently — so
  what it says exactly is one of the things [UNPROVEN.md](docs/UNPROVEN.md) records.
  [PHASES.md](docs/PHASES.md) records what shipping unsigned accepts, and why signing left v1.
- **Two flavours.** The default installer carries the CPU and Vulkan backends and is about 82 MB;
  the `win-cuda` one adds the NVIDIA CUDA runtime and is about 819 MB. **Both figures were measured
  before the Python bundle and neither includes it**, because no installer has been packed with one
  yet; the bundle itself measures **1.20 GB**, and [UNPROVEN.md](docs/UNPROVEN.md) carries what that
  means for a download nobody has built.
  Take the first unless you know you want CUDA. Whichever you install is meant to keep updating
  itself from the same flavour: the channel is recorded at install time and the app never overrides
  it, which was read off an installed copy — but no release exists yet, so no update has ever been
  fetched from one.
- **Your models are not in it, and not touched by it.** The application installs into
  `%LOCALAPPDATA%\UindosillDesktop`; downloaded weights live in `%LOCALAPPDATA%\Uindosill\models`.
  Uninstalling deletes the first and leaves the second — that was measured against 4.3 GiB of
  weights, and [UNPROVEN.md](docs/UNPROVEN.md) has the record, including what it does not prove.

The application asks GitHub once, when it starts, whether a newer version exists. That is the only
thing it does on the network without being asked: it shows a notice, downloads nothing until you
press the button, and the Updates tab has a switch that turns the check off.

## Quick start

```bash
dotnet build Uindosill.slnx
dotnet test  Uindosill.slnx          # 949 tests, no weights needed, runs on Linux

# See the whole pipeline work without a model: real WAVE parsing, real segmentation,
# real subtitle output, canned words.
dotnet run --project src/Parakeet.Cli -- transcribe --fake -f srt,json recording.wav
```

To do real work you need two things this repository does not contain: the parakeet.cpp native
library ([NATIVE-BINARIES.md](docs/NATIVE-BINARIES.md)) and a GGUF model
([MODELS.md](docs/MODELS.md)). The first is one script — it downloads the pinned release,
verifies it against the recorded digests and unpacks it where the build expects — and the second
is one command. Vendor *before* you build: the build is what copies `native/` into the output, so
natives dropped after a build are not seen until the next one.

```bash
pwsh scripts/vendor-natives.ps1           # cpu and vulkan natives, ~18 MB, verified against the pins
dotnet build Uindosill.slnx -c Release    # copies native/ into the output
uindosill doctor                          # models installed, and which transcription backends load
uindosill models list
uindosill models download tdt-0.6b-v3-f16
uindosill transcribe -f srt,txt *.mp4
uindosill bench recording.wav
```

`doctor` is narrower than it sounds. It probes the three parakeet.cpp backends — cpu, vulkan, cuda
— each in a child process, and reports the runtime, the audio extensions this machine can open and
the models installed. **It does not start the Python sidecar**, so it says nothing about whether
the speaker and English passes will run here or which execution provider they would pick; those
answer at load, and the window disables both opt-ins with the reason when there is no interpreter.

Speaker labels are a separate model and a separate download, and everything about the opt-in stays
off until it is there:

```bash
uindosill models download sortformer-4spk-v2.1   # 453 MiB; not a transcription model
uindosill transcribe --speakers -f srt,rttm meeting.wav
uindosill diarise meeting.wav                    # speaker turns only, no transcription
```

`diarise` exists because scoring a diariser through `transcribe` means paying for an ASR pass that
contributes nothing to a speaker turn; it is what the AMI measurement runs through, and
`uindosill der` scores its output.

`uindosill` is the CLI's assembly name: after that build it is
`src/Parakeet.Cli/bin/Release/net10.0/uindosill.exe`, and `dotnet run --project src/Parakeet.Cli --`
runs the same thing. Vulkan runs with bf16 disabled by default, which is what lets the model load
on an AMD integrated GPU whose driver mishandles it; `--vk-bf16` on `transcribe` or `bench` leaves
bf16 on, for measuring the difference or for a driver known to have fixed it. Why the default is
what it is, and what it measured, is in [UNPROVEN.md](docs/UNPROVEN.md).

The two sidecar engines each take an execution provider, and it is a **faithfulness** setting
before it is a speed one — `auto|cpu|cuda|webgpu`, defaulting to `auto`, which is resolved inside
the sidecar because the only thing that knows whether a provider will initialise is the ONNX
Runtime that would have to initialise it:

```bash
uindosill transcribe --speakers --speaker-backend cpu meeting.wav     # two engines, so two flags
uindosill transcribe --translate --translate-backend webgpu call.mp4
uindosill diarise --backend cuda meeting.wav                          # one engine each, so --backend
uindosill translate --backend cpu segments.txt
```

`dml` is a fifth name each of them refuses, and unlocking it takes a second flag —
`--speaker-backend-unverified`, `--translate-backend-unverified`, or `--backend-unverified` on the
two standalone commands. That is not caution about a slow backend. At ONNX Runtime's default
settings DirectML scores **53.15% DER** while returning plausible speaker turns, a clean exit and a
13x speed-up, and on the translator it returned none of the CPU's 32 sentences. **A provider can be
catastrophically wrong and look healthy**, which is why both engines check a committed parity
fixture at load on every provider but the CPU, and why measuring DirectML stays possible while
reaching it by accident does not. A run whose numbers are going to be written down should name the
provider that produced them.

### In a container with no toolchain

Every test here runs on Linux with no weights and no display, which is a design constraint rather
than a convenience — a test needing 670 MB of weights is one CI will never run. That constraint is
what makes an ephemeral container a usable place to work, so the toolchain is installed by the
environment's own setup script. Paste `scripts/cloud-setup.sh` into that field: it unpacks a
pinned SDK 10.0.400 and PowerShell 7.6.4 from `packages.microsoft.com` Debian packages, checks
each against the SHA-256 the feed publishes, and warms the NuGet cache. Not the vendor's
installer, because `dot.net` and every host it redirects to are refused by the network policy
there; not the Ubuntu feed either, though the image is Ubuntu. That file's header records both, and
the digests are pinned for the same reason `docs/NATIVE-BINARIES.md` pins a parakeet.cpp release.

So a session in such a container **builds the solution and runs the full suite**, and the `--fake`
pipeline above works there end to end. What it cannot do is transcribe anything real: that needs
the Windows natives and a model, neither of which is in the clone.

### The scripts

`scripts/` holds fourteen PowerShell tasks — two for vendoring, four measurement harnesses (speed
and memory, the second machine, word error rate against human transcripts, and diarisation error
rate against hand-labelled speaker turns), two transcript comparisons, one that holds
`uindosill translate` against the hypotheses the translation gate itself recorded, two for the v2
spike, one that moves run reports and test material over rclone, one that assembles the bundled
Python, and one that builds the installer
— and `scripts/lab.ps1` is one entry point for them: run it bare to list the tasks, each with the
parameters its own script declares. It dispatches and nothing else, so every task is still runnable
on its own.

They divide along the same container line rather than all being out of reach.
`scripts/compare-transcripts.ps1` reads two transcript JSONs and needs nothing else, so it runs
there — including against JSONs the `--fake` engine produced. It aligns the two word streams by
word-level edit distance (the same code the CLI's `wer` command uses, compiled from
`src/Parakeet.Core/Text/` with `Add-Type`, so no build is needed) and reports substitutions,
deletions and insertions with raw and normalised counts. `scripts/word-distance.ps1` is the same
shape and runs there too: several candidates against one reference in one table — the
quantisation ladder — and it reads the `.txt` output as well. `scripts/vendor-natives.ps1` needs only `pwsh` and a route to
`github.com` for its cpu and vulkan backends, which is how the Linux CI runner vendors the natives
before it publishes; whether the container's network policy allows that route has not been checked.
`scripts/measure-transcribe.ps1` will parse and report on outputs, but cannot produce them.
`scripts/measure-wer.ps1` needs a machine that can transcribe — it fetches the pinned Earnings-22
subset (`scripts/wer-corpus.json`, ~190 MB, verified by digest, into the gitignored `corpus/`),
runs every catalogue model over its eleven hours and scores each against two human transcript
styles with `uindosill wer`. `scripts/measure-der.ps1` scores speaker-turn hypotheses with
`uindosill der` — a diarisation error rate validated against pyannote.metrics on committed fixture
pairs (`tests/fixtures/diarisation/`) — and cuts the pinned development stretches from the test
episodes with ffmpeg; the scoring half needs only the built CLI and runs anywhere.
`scripts/measure-translation-agreement.ps1` needs no audio at all but does need the exported
checkpoint and a Python the sidecar can run in, since what it drives is `uindosill translate`.
`scripts/measure-second-machine.ps1` probes hardware through CIM and
is Windows throughout, as is `scripts/vendor-cuda.ps1`, which reads a PE import table.
`scripts/package-windows.ps1` builds the installer. It passes `vpk`'s `[win]` directive, which is
documented to cross-build a Windows package from Linux, so it should run on either — but every pack
here has been on Windows and the Linux route is untried (`UNPROVEN.md`); its CUDA channel needs
`vendor-cuda.ps1` in any case, so that half is Windows-only. For
`measure-wer.ps1`, `measure-second-machine.ps1`, `vendor-cuda.ps1` and `measure-der.ps1`'s cutting
half, `pwsh` at least parses them, which is enough to keep a syntax error off the machine that can
run them.

## Layout

```
src/
  Parakeet.Core/               net10.0            contracts + pure logic; no NuGet, no platform, no UI
  Parakeet.Audio/              net10.0            WAV/RF64 parser + Media Foundation decoding
  Parakeet.Engine.ParakeetCpp/ net10.0            the ONLY project that touches native interop
  Parakeet.Engine.Python/      net10.0            the ONLY project that starts the sidecar process
  Parakeet.Cli/                net10.0            transcribe / diarise / translate / models / bench /
                                                  doctor / notice / formats / wer / der / rttm
  Parakeet.App/                net10.0            Avalonia desktop UI
python/
  uindosill_engines/           the sidecar        serve.py + protocol.py, diariser/, translator/,
                                                  and a vendored slice of NeMo under _vendor/
tools/
  FakeSidecar/                 net10.0            a scripted stand-in for that process, so the
                                                  tests need no Python and still run on Linux
tests/                                            one per src project, all runnable on Linux
attic/                                            the retired C# diariser and translator; unbuilt,
                                                  referenced by nothing — see attic/README.md
```

The one rule that matters: **`Parakeet.Core` references no engine, no platform and no UI.** That is
enforced by the build, not by convention — adding a `PackageReference` to `Parakeet.Core.csproj`
fails the build with an explanation. That seam is what keeps an engine swap to one project instead
of a rewrite.

A second seam runs alongside it now, and it is drawn where it is on purpose: **the sidecar does the
two things only a model can do** — turn a WAV into speaker turns, count a string's tokens and
translate it — and is told nothing about what either means. The `>>eng<<` target token, the length
a source is refused against rather than truncated at, the refusal of the word-timed format under
`--translate`, the speaker count folded down afterwards and every warning owed before a run are all
still C#. Moving the engines across a process boundary did not move the decisions with them.

## Stack

| Layer | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (`net10.0`) | .NET 8 and 9 both reach EOL 2026-11-10. |
| UI | Avalonia 12.1.1 | Plain `net10.0` TFM on Windows, so the desktop app cross-builds from Linux CI. |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | Avalonia's documented default; source-generator based. |
| Engine | `mudler/parakeet.cpp` via P/Invoke | MIT, ABI v6, and the only candidate with a published decode-parity result. |
| The other two models | Diariser and translator in a bundled Python sidecar — one child process per run, JSON lines over stdin and stdout, WebGPU by default | Both are ONNX Runtime models, and the C# ports of them were about 7,400 lines reimplementing what NVIDIA and HuggingFace already ship. **ONNX Runtime lives in that process now: no .NET project in this solution references it.** WebGPU because it reproduces the CPU's answer to 0.0005 DER points and CUDA does not (2026-08-21; `attic/README.md` has what was retired). |
| Model format | GGUF | `mudler/parakeet-cpp-gguf`, f16 only — the quantisations were withdrawn from the catalogue 2026-08-20. |
| Audio decoding | Managed WAVE reader + NAudio 2.3.0 Media Foundation | No ASR library in this space reads audio files. |
| Deployment | Self-contained + ReadyToRun, `win-x64` (`win-arm64` publishes but has no natives — upstream ships none — so it cannot transcribe) | No single-file, no trimming, no NativeAOT. |

Why parakeet.cpp and not the obvious alternatives is recorded in
[ENGINE-CHOICE.md](docs/ENGINE-CHOICE.md).

## Documentation

The ten notes live in [`docs/`](docs/); the last two rows are at the repository root.

| Document | What is in it |
|---|---|
| [UNPROVEN.md](docs/UNPROVEN.md) | Everything asserted here that nobody has measured. Read first. |
| [GOTCHAS.md](docs/GOTCHAS.md) | The silent failures, and where each one is handled in this codebase. |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | The seams, the contracts, and why segmentation is not optional. |
| [ENGINE-CHOICE.md](docs/ENGINE-CHOICE.md) | Why parakeet.cpp, and not the alternatives that looked obvious. |
| [NATIVE-BINARIES.md](docs/NATIVE-BINARIES.md) | Vendoring the pinned natives — parakeet.cpp and libmpv: the scripts, the layout, the digests, and the licence libmpv brings. |
| [MODELS.md](docs/MODELS.md) | The catalogue, and how to pin a digest properly. |
| [LICENSING.md](docs/LICENSING.md) | The CC BY 4.0 obligations, which are not "just attribution", and the CUDA EULA reading. |
| [V2-ASK-THE-TRANSCRIPT.md](docs/V2-ASK-THE-TRANSCRIPT.md) | The open decisions for v2, and the problem that makes it hard. |
| [V3-DICTATION.md](docs/V3-DICTATION.md) | What v3 will need, and the traps waiting there. |
| [PHASES.md](docs/PHASES.md) | The phase plan and what is actually done. |
| [NOTICE.md](NOTICE.md) | The third-party notices as shipped: the CC BY weights, the MIT components, GPL libmpv, the CUDA runtime. |
| [CLAUDE.md](CLAUDE.md) | Working agreement for an agent session: budget, how to build, the one rule. |

## Licence

**Two licences, and which one governs a copy depends on whether it carries the video player.** The
source code in this repository is MIT. **A build that vendors libmpv — the video player behind the
Ask tab — is distributed under GPLv2-or-later**, because libmpv and the FFmpeg libraries linked
into it are GPL and the GPL governs the combined work. A build without it contains no GPL component
and is MIT throughout; the Licences tab lists libmpv only when it is there. That was decided on
2026-08-23 in preference to shipping no video or maintaining an LGPL mpv build of our own — see
[PHASES.md](docs/PHASES.md) — and the obligations it creates, including where the corresponding
source lives, are in [LICENSE](LICENSE), [licences/mpv-WRITTEN-OFFER.txt](licences/mpv-WRITTEN-OFFER.txt)
and [LICENSING.md](docs/LICENSING.md).

The **model weights are not**. They are CC BY 4.0 from NVIDIA, which permits commercial
redistribution and bundling but attaches a seven-element notice requirement, forbids DRM on the
weights, and withholds patent and trademark rights. The notice is shown inside the application (the
Licences tab, and `uindosill notice`), not only in this repository. See
[NOTICE.md](NOTICE.md) and [LICENSING.md](docs/LICENSING.md).

`parakeet-tdt-0.6b-v3` covers 25 European languages. It does **not** cover Chinese, Japanese,
Korean, Arabic, Hindi or Thai, and this product does not offer them.
