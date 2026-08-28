<p align="center">
  <img src="brand/uindosill-mark.png" alt="Uindosill" width="128">
</p>

<h1 align="center">Uindosill</h1>

<p align="center">
  <strong>Local speech-to-text for Windows.</strong><br>
  Drop in audio or video — get a timestamped transcript, optional speaker labels,<br>
  an English translation, and a chat panel that answers questions about the recording<br>
  and cites the moment each answer came from.
</p>

<p align="center">
  <a href="https://github.com/jkkma/uindosill/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/jkkma/uindosill/actions/workflows/ci.yml/badge.svg"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="Windows x64" src="https://img.shields.io/badge/platform-Windows%20x64-0078D4">
  <img alt="Source licence" src="https://img.shields.io/badge/source-MIT-green">
  <img alt="Video build licence" src="https://img.shields.io/badge/builds%20with%20video-GPLv2%2B-green">
</p>

---

## What it does

| | |
|---|---|
| **Transcribe** | Audio and video files, locally, with NVIDIA Parakeet TDT 0.6B v3 through [`parakeet.cpp`](https://github.com/mudler/parakeet.cpp) — 25 European languages, on CPU, Vulkan or CUDA. Seven output formats: `txt`, `md`, `srt`, `vtt`, word-timed `vtt-words` for karaoke-style highlighting, `json` with timestamps, and `rttm`. |
| **Paste a link** | A URL behaves like a file: the audio track is downloaded and queued. On the Ask tab the picture streams from the link rather than landing on disk, so a three-hour video costs megabytes instead of gigabytes. |
| **Label who spoke** | Opt-in, off by default. `pyannote/speaker-diarization-community-1` adds `Speaker 1:` to every format and unlocks `rttm`. It clusters rather than tracks, so there is **no ceiling on how many voices** it can separate — and it has been scored on **one meeting**, not on a corpus, which the product says rather than leaving you to assume. |
| **Translate to English** | Opt-in. A Marian checkpoint exported here to ONNX, decoded at beam 6. The English arrives *beside* the transcript rather than instead of it, sentence for sentence, with the same speakers on both sides. |
| **Find the speech** | Silero VAD (2.2 MiB, MIT, on the CPU) cuts on pauses in speech rather than on quiet, so narration over a music bed no longer comes out in thirty-second blocks. Default wherever its model is installed; `--vad energy` asks for the loudness gate on purpose. |
| **Ask the transcript** | A chat panel beside the text. A local `llama-server` answers, the model cites opaque segment ids the application resolves to times — it never writes a timestamp of its own — a claim it cannot anchor renders as unresolved, and every answer says it was generated, not transcribed. |
| **Play it back** | The recording plays beside its transcript, one clickable cue per sentence, the spoken line lighting up as it goes, with a find box that marks every mention and steps between them. Video draws its picture too, through a vendored libmpv. |

Speaker labels and translation run in a Python interpreter that ships *inside* the application —
first packaged in `v1.0.0-rc.3` on 2026-08-23. A build from source carries none, and the code says
so beside those two opt-ins rather than failing when one is started.

## Measured

**Speed** — real-time factor, lower is faster. Ten minutes of podcast, `tdt-0.6b-v3-f16`,
parakeet.cpp v0.5.0. Every figure is the *whole* pass: container decode, resampling and
segmentation run inside the timed stretch, serialised with the model.

| Machine | CPU | Vulkan | CUDA |
|---|---:|---:|---:|
| Desktop — 16-core x64, RTX 5080 | 0.0818 | 0.0110 | **0.0064** |
| Laptop — Ryzen AI 9 365, Radeon 880M | 0.1417 | 0.0349 | — |

The same ten minutes produced 98 segments and 1,573 words on all three desktop backends, with no
duplicated or dropped word at any segment join. The Vulkan figure is steady-state — a *first* run
on a fresh machine spends about seven extra seconds compiling shaders inside what looks like
decode time. On the laptop, Vulkan does not load the model at all unless bf16 is disabled first,
an upstream defect in how the driver's bf16 support is requested; that workaround is now the
default, measured at 0.3% cost and byte-identical transcripts on the RTX 5080, and `--vk-bf16`
turns it back off.

**Accuracy**

| Task | Result | Corpus | Beside it |
|---|---|---|---|
| Word error rate | **10.21%** (f16, CUDA) | Earnings-22 subset — 11 h of accented English earnings calls, human transcripts | 13.40% against the same corpus's non-verbatim transcript style; all five quantisations land within **0.08 points** of one another |
| Diarisation error rate | **14.38%** (collar 0.25, overlap scored) — **one meeting, not a corpus** | AMI `ES2004a`, 17.5 min | 18.76% at collar 0. The engine that carried this project's 16.33% over the whole AMI test set was retired on 2026-08-27; see below |
| Translation | chrF++ clears its per-language bar in **23 of 24** languages, with **zero collapses** | FLEURS `test` — all 8,149 sentences, beam 6 | Margins over each language's floor run +28.15 to +60.53, median +42.76; Slovak misses by 0.74 |

**The diarisation figure is one meeting rather than a corpus, and that is a change rather than an
omission.** Until 2026-08-27 speaker labelling was NVIDIA's Streaming Sortformer, which scored
**16.33%** DER over the whole AMI test set — sixteen meetings — against a best published figure of
18.8% on the same audio, at 16.3324% on the CPU and 16.3319% on WebGPU, which is why WebGPU was
chosen automatically over CUDA's faster but divergent 16.1021%. That engine was shelved to
`attic/sortformer/`, and **not one of those numbers describes what ships now**. The pipeline that
replaced it has been scored on one of those sixteen meetings, and a single meeting is not a corpus:
the two figures must not be set beside each other. `docs/UNPROVEN.md` records what a comparable
number would take, and `docs/PHASES.md` records that the speaker gate is unmet until it exists.

The translation decode is still held to that standard: it reproduced all 8,149 recorded hypotheses across 24 languages character
for character.

Slovak is the single language the translation gate fails, by 0.74 of a chrF++ point against
margins that reach +60 — and it is the one language absent from the model card's source list, so
the miss was predicted from the record before it was measured. The gate's other criterion, a human
adequacy rating, was **declined with finality on 2026-08-23**: v1.0 ships with the gate unpassed by
its own ratified definition rather than with the definition rewritten to fit. Both are the same
choice, and it is why the 23-of-24 figure never travels without them.

**Two machines and two ten-minute files is not a benchmark.** Every figure above carries its
backend, its corpus and its caveats in **[UNPROVEN.md](docs/UNPROVEN.md)** — read that before
quoting any number from this repository.

## The rule this project runs on

> **Every claim is either measured or explicitly marked unproven.**

That is not a slogan; it is enforced. [`UNPROVEN.md`](docs/UNPROVEN.md) is the longest document
here and exists so no figure can be quoted without its backend and its limits travelling with it.
`scripts/check-test-counts.py` runs in CI and fails the build when a documented test count drifts
from what the suite actually reports — including when a sentence is *reworded out of reach of the
check*, because silently retiring a guard is worse than never having one.

Three habits fall out of it, and they show up throughout the codebase:

- **A provider can be catastrophically wrong and look healthy.** At ONNX Runtime's default
  settings DirectML scored 53.15% DER while returning plausible speaker turns, a clean exit and a
  13× speed-up — measured on the diariser retired in August 2026. So the translator checks a
  committed parity fixture at load on every provider but the CPU, and `dml` is refused by name
  until a second flag unlocks it. **The diariser no longer has such a check**: it is a torch
  pipeline with one path, and parity needs two. That is a gap rather than a resolution, and
  [UNPROVEN.md](docs/UNPROVEN.md) carries it.
- **An exclusion list fails safely; an inclusion list fails silently.** The first release
  candidate shipped without three whole features because a packaging step pruned everything it did
  not recognise. The prune now deletes only *named* backends a channel does not carry, and the
  read-back opens the built package and requires what it promised.
- **A fake must not be more forgiving than the real thing.** The test double abstains exactly
  where the real engine abstains — an earlier version quietly invented its own evidence, which is
  the shape of fake that lets defects through a green suite.

## Roadmap

| | | |
|---|---|---|
| **v1** | File transcription, speaker labels, English translation, speech detection | Built. Released as a candidate |
| **v2** | Ask the transcript: playback, cues, and a cited chat panel | Built. Which language model to recommend is still open — [V2-ASK-THE-TRANSCRIPT.md](docs/V2-ASK-THE-TRANSCRIPT.md) |
| **v3** | Push-to-talk dictation | Not built, not architected out — [V3-DICTATION.md](docs/V3-DICTATION.md) |

That order is deliberate. The entire Win32 risk surface — global keyboard hooks that get flagged
as keyloggers, text injection that fails silently under UIPI, overlay windows that steal focus —
lives on the dictation path and none of it on the file path, which is why dictation is last.
Asking questions about a transcript sits in front of it because it needs none of that: it reads a
transcript this product already produces. What it costs instead is a second native stack and an
honesty problem, since a wrong answer is fluent rather than obviously broken.

## Quick start

Everything here builds and tests on Linux with no weights and no display — a design constraint
rather than a convenience, since a test needing 670 MB of weights is a test CI will never run.

```bash
dotnet build Uindosill.slnx
dotnet test  Uindosill.slnx          # 1438 tests, no weights needed, runs on Linux

# See the whole pipeline work without a model: real WAVE parsing, real segmentation,
# real subtitle output, canned words.
dotnet run --project src/Parakeet.Cli -- transcribe --fake -f srt,json recording.wav
```

Real work needs two things this repository does not contain: the parakeet.cpp native library
([NATIVE-BINARIES.md](docs/NATIVE-BINARIES.md)) and a GGUF model ([MODELS.md](docs/MODELS.md)).
Vendor *before* you build — the build is what copies `native/` into the output.

```bash
pwsh scripts/vendor-natives.ps1           # cpu and vulkan natives, ~18 MB, verified against the pins
dotnet build Uindosill.slnx -c Release    # copies native/ into the output
uindosill doctor                          # models installed, and which transcription backends load
uindosill models download tdt-0.6b-v3-f16
uindosill transcribe -f srt,txt *.mp4
uindosill bench recording.wav
```

Speaker labels are a separate model and a separate download:

```bash
uindosill models download pyannote-speaker-diarization-community-1   # 31 MiB; needs a Hugging Face token
uindosill transcribe --speakers -f srt,rttm meeting.wav
uindosill diarise meeting.wav                    # speaker turns only, no transcription
```

`diarise` exists because scoring a diariser through `transcribe` means paying for an ASR pass that
contributes nothing to a speaker turn; it is what the AMI measurement runs through, and
`uindosill der` scores its output.

The translator takes an execution provider — `auto|cpu|cuda|webgpu`, resolved inside the
sidecar, because the only thing that knows whether a provider will initialise is the ONNX Runtime
that would have to initialise it:

```bash
uindosill transcribe --speakers --speaker-backend cpu meeting.wav     # two engines, so two flags
uindosill transcribe --translate --translate-backend webgpu call.mp4
uindosill diarise --backend cuda meeting.wav                          # one engine each, so --backend
uindosill translate --backend cpu segments.txt
```

`doctor` is narrower than it sounds: it probes the three parakeet.cpp backends, each in a child
process, and reports the runtime, the audio extensions this machine can open and the models
installed. It does **not** start the Python sidecar, so it says nothing about the speaker and
English passes — those answer at load, and the window disables both opt-ins with the reason when
there is no interpreter.

## How it's built

```
src/
  Parakeet.Core/                net10.0   contracts + pure logic; no NuGet, no platform, no UI
  Parakeet.Audio/               net10.0   WAV/RF64 parser + Media Foundation decoding
  Parakeet.Engine.ParakeetCpp/  net10.0   the ONLY project that touches native ASR interop
  Parakeet.Engine.Python/       net10.0   the ONLY project that starts the sidecar process
  Parakeet.Engine.SileroVad/    net10.0   speech detection, ONNX Runtime in process on the CPU
  Parakeet.Engine.LlamaServer/  net10.0   the v2 answer engine's child llama-server
  Parakeet.Cli/                 net10.0   transcribe / diarise / translate / models / bench /
                                          doctor / notice / formats / wer / der / rttm / retrieve
  Parakeet.App/                 net10.0   Avalonia desktop UI
python/
  uindosill_engines/            the sidecar — serve.py + protocol.py, diariser/, translator/,
                                and a vendored slice of NeMo under _vendor/
tools/
  FakeSidecar/                  net10.0   a scripted stand-in for that process, so the tests
                                          need no Python and still run on Linux
tests/                                    one per src project, all runnable on Linux
attic/                                    the retired C# diariser and translator; unbuilt,
                                          referenced by nothing — see attic/README.md
```

**The one rule that matters: `Parakeet.Core` references no engine, no platform and no UI.** It is
enforced by the build rather than by convention — adding a `PackageReference` to
`Parakeet.Core.csproj` fails the build with an explanation. That seam is what keeps an engine swap
to one project instead of a rewrite.

A second seam runs alongside it, drawn where it is on purpose: **the sidecar does the two things
only a model can do** — turn a WAV into speaker turns, and count and translate a string — and is
told nothing about what either means. The `>>eng<<` target token, the length a source is refused
against rather than truncated at, the refusal of the word-timed format under `--translate`, the
speaker count folded down afterwards and every warning owed before a run are all still C#. Moving
the engines across a process boundary did not move the decisions with them.

### Stack

| Layer | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (`net10.0`) | .NET 8 and 9 both reach EOL 2026-11-10. |
| UI | Avalonia 12.1.1 | Plain `net10.0` TFM on Windows, so the desktop app cross-builds from Linux CI. |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | Avalonia's documented default; source-generator based. |
| ASR engine | `mudler/parakeet.cpp` via P/Invoke | MIT, ABI v6, and the only candidate with a published decode-parity result. |
| The other two models | A bundled Python sidecar — one child process per run, JSON lines over stdin and stdout | The C# ports were ~7,400 lines reimplementing what NVIDIA and HuggingFace already ship. **ONNX Runtime lives in that process: no .NET project in this solution references it.** The translator is an ONNX Runtime model and defaults to WebGPU, which reproduces the CPU's output where CUDA and DirectML do not. The diariser is torch on both stages and defaults to the CPU, because the bundled torch is the CPU build. |
| Model format | GGUF | `mudler/parakeet-cpp-gguf`, f16 only — the quantisations were withdrawn from the catalogue on 2026-08-20, a product decision the WER measurement above is exactly what made cheap. |
| Audio decoding | Managed WAVE reader + NAudio 2.3.0 Media Foundation | No ASR library in this space reads audio files. |
| Deployment | Self-contained + ReadyToRun + single-file, `win-x64` | Managed assemblies inside the executable; the vendored natives are not, and are found by path. No trimming, no NativeAOT. |

Why parakeet.cpp and not the obvious alternatives is recorded in
[ENGINE-CHOICE.md](docs/ENGINE-CHOICE.md).

## Getting it

**`v1.0.0-rc.3`, published 2026-08-23, is the only release so far** — a Windows installer in two
flavours, the CLI as a zip beside it, and the bundled Python as a third zip for CLI users.

Installing it found five defects, three of them one packaging fault that had silently removed
whole features from the build. All are fixed on `master` and held by assertions that open the
built package and require what it promised. `rc.4` was then tagged twice and failed twice in CI,
the second time on GitHub's 2 GiB per-asset limit — so on 2026-08-25 the release workflow was run
in dispatch mode with publishing skipped, which built every asset whole for the first time and
measured the win-cuda installer at 1.959 GB, clearing the limit by about 180 MiB. Until the next
candidate, rc.3 is worth installing only to look at it.

The other two routes: build from source above, or take the `uindosill-win-x64` artefact from any
CI run of `master` — a self-contained publish of the CLI and the desktop app with the cpu and
vulkan natives in place, kept for seven days.

Three things worth knowing before downloading:

- **It is unsigned**, by decision rather than oversight, so expect Windows to warn about an
  unknown publisher. [PHASES.md](docs/PHASES.md) records what shipping unsigned accepts.
- **Two flavours.** The default installer carries the CPU and Vulkan backends with the bundled
  Python inside; `win-cuda` adds the NVIDIA CUDA runtime. Take the first unless you know you want
  CUDA. Whichever you install keeps updating from the same channel, recorded at install time.
- **One of the four models comes with it.** Speech detection (2.2 MiB) is inside the installer, so
  that opt-in works the moment you first open the app. **Speaker labelling does not** — it is a
  31 MiB download that needs a free Hugging Face account and an accepted user agreement, so that
  opt-in is dead until you fetch it from the Models tab.
  Speech recognition (1.34 GiB) and English translation (1.34 GiB) are downloads from the Models
  tab, because a GitHub release asset has to stay under 2 GiB.

**Nothing this application does unattended deletes a file on your disk.** That is a rule rather
than an observation, and it decides how the folders are arranged: the application installs into
`%LOCALAPPDATA%\UindosillDesktop`, everything you download lives in `%LOCALAPPDATA%\Uindosill`,
and the second survives an update, a reinstall and an uninstall alike — measured byte-identical
across an update against 4.3 GiB of weights. Weights go when you remove them on the Models tab.
[GOTCHAS.md](docs/GOTCHAS.md) has the reasoning, including a cleanup feature that was tried and
withdrawn for breaking it.

The application asks GitHub once, at startup, whether a newer version exists. That is the only
thing it does on the network unasked: it shows a notice, downloads nothing until you press the
button, and the Updates tab has a switch that turns the check off.

## Working on it

**In a container with no toolchain.** Paste `scripts/cloud-setup.sh` into the environment's setup
field: it unpacks a pinned SDK 10.0.400 and PowerShell 7.6.4 from `packages.microsoft.com` Debian
packages, checks each against the SHA-256 the feed publishes, and warms the NuGet cache — not the
vendor's installer, because `dot.net` and every host it redirects to are refused by the network
policy there. A session in such a container builds the solution, runs the full suite and drives
the `--fake` pipeline end to end. What it cannot do is transcribe anything real.

**The scripts.** `scripts/` holds seventeen PowerShell tasks — five for vendoring, four
measurement harnesses (speed and memory, the second machine, word error rate against human
transcripts, diarisation error rate against hand-labelled speaker turns), two transcript
comparisons, one that holds `uindosill translate` against the hypotheses the translation gate
recorded, two for the v2 answer tier, one that moves run reports over rclone, one that assembles
the bundled Python and one that builds the installer. `scripts/lab.ps1` is one entry point for
them: run it bare to list the tasks, each with the parameters its own script declares. It
dispatches and nothing else, so every task stays runnable on its own.

They divide along the same container line. `compare-transcripts.ps1` and `word-distance.ps1` read
transcript JSONs and need nothing else, so they run anywhere — they align two word streams by
word-level edit distance, compiled straight from `src/Parakeet.Core/Text/` with `Add-Type`, so no
build is needed. `vendor-natives.ps1` needs only `pwsh` and a route to `github.com`, which is how
the Linux CI runner vendors natives before it publishes. `measure-wer.ps1` and the cutting half of
`measure-der.ps1` need a machine that can transcribe; `measure-second-machine.ps1` probes hardware
through CIM and `vendor-cuda.ps1` reads a PE import table, so both are Windows throughout.

## Documentation

The ten notes live in [`docs/`](docs/); the last two rows are at the repository root.

| Document | What is in it |
|---|---|
| [UNPROVEN.md](docs/UNPROVEN.md) | Everything asserted here that nobody has measured. **Read first.** |
| [GOTCHAS.md](docs/GOTCHAS.md) | The silent failures, and where each one is handled in this codebase. |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | The seams, the contracts, and why segmentation is not optional. |
| [ENGINE-CHOICE.md](docs/ENGINE-CHOICE.md) | Why parakeet.cpp, and not the alternatives that looked obvious. |
| [NATIVE-BINARIES.md](docs/NATIVE-BINARIES.md) | Vendoring the pinned natives — the scripts, the layout, the digests, and the licence libmpv brings. |
| [MODELS.md](docs/MODELS.md) | The catalogue, and how to pin a digest properly. |
| [LICENSING.md](docs/LICENSING.md) | The CC BY 4.0 obligations, which are not "just attribution", and the CUDA EULA reading. |
| [V2-ASK-THE-TRANSCRIPT.md](docs/V2-ASK-THE-TRANSCRIPT.md) | The open decisions for v2, and the problem that makes it hard. |
| [V3-DICTATION.md](docs/V3-DICTATION.md) | What v3 will need, and the traps waiting there. |
| [PHASES.md](docs/PHASES.md) | The phase plan and what is actually done. |
| [NOTICE.md](NOTICE.md) | Third-party notices as shipped: the CC BY weights, the MIT components, GPL libmpv, the CUDA runtime. |
| [CLAUDE.md](CLAUDE.md) | Working agreement for an agent session: budget, how to build, the one rule. |

## Licence

**Which licence governs a copy depends on one thing: whether that copy carries the video player.**

| The copy you have | Licence | |
|---|---|---|
| This repository's source | **MIT** | |
| `uindosill-cli-win-x64.zip` and the `uindosill-win-x64` CI artefact | **MIT** | neither vendors the player |
| The two desktop installers | **GPLv2-or-later** | both carry it |

libmpv, the player behind the Ask tab, is GPLv2-or-later and links FFmpeg-GPL. Putting it in the
same distribution as this application makes the combined work a GPL distribution — so the binary
that draws video is GPL and the binaries that do not are not. You can tell which kind of copy you
have by looking: the About window's Licences pane lists libmpv only when it is there.

**None of that revokes the MIT grant on the source**, and a recipient of a GPL build may take the
Uindosill source under either set of terms. What cannot be separated from the GPL is the
*combination* with libmpv. The alternatives were weighed on 2026-08-23 — ship no video, or
maintain an LGPL mpv build, which exists as no prebuilt Windows binary — and this one was taken
deliberately. The obligations it creates, including where the corresponding source lives, are in
[LICENSE](LICENSE), [licences/mpv-WRITTEN-OFFER.txt](licences/mpv-WRITTEN-OFFER.txt) and
[LICENSING.md](docs/LICENSING.md), which also says plainly that nobody with a professional opinion
has read any of it.

**The four sets of model weights carry three different licences, and none of them is the code's:**

| Weights | Licence |
|---|---|
| Transcription — `parakeet-tdt-0.6b-v3` | CC BY 4.0 |
| Speaker labelling — `pyannote/speaker-diarization-community-1` | CC BY 4.0 |
| Translation — Marian / OPUS-MT | Apache-2.0 |
| Speech detection — Silero VAD | MIT |

The NVIDIA Open Model License was the fourth until 2026-08-27, when the Sortformer weights it covered
were retired; nothing in this product is under it now.

CC BY 4.0 permits commercial redistribution and bundling but attaches a seven-element notice
requirement, forbids DRM on the weights, and withholds patent and trademark rights. The NVIDIA
licence is **not** interchangeable with it: it makes the grant revocable where CC BY 4.0 is
irrevocable, and incorporates terms naming biometric processing — and speaker diarisation is voice
biometrics. Every notice is shown inside the application, through the About window's Licences pane
and `uindosill notice`, not only in this repository. [NOTICE.md](NOTICE.md) carries them;
[LICENSING.md](docs/LICENSING.md) carries what each obliges.

`parakeet-tdt-0.6b-v3` covers 25 European languages. It does **not** cover Chinese, Japanese,
Korean, Arabic, Hindi or Thai, and this product does not offer them.
