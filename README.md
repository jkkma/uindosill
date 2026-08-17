# Uindosill

A Windows desktop app that transcribes audio and video files **locally** with NVIDIA Parakeet.
Drop files in, get text out — plain text, SRT, VTT, word-timed VTT for karaoke-style highlighting,
JSON with timestamps, Markdown. No cloud, no Python, no account.

> **Status: the CLI and the desktop app both produce correct transcripts from real weights on real
> Windows.** Ten minutes of podcast through Media Foundation, parakeet.cpp v0.5.0,
> `tdt-0.6b-v3-f16`, on one 16-core x64 desktop with an RTX 5080 — **RTF 0.082 on CPU, 0.011 on
> Vulkan, 0.0064 on CUDA** — 98 segments and 1,573 words on all three, no duplicated or dropped
> words at any segment join. Three hours has been run end to end on CPU. The Vulkan figure is
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
> exactly what it does and does not cover.

## What it does

- **v1 is file transcription.** No global hotkeys, no text injection, no overlay HUD, no microphone
  capture.
- **v2 is asking questions about a transcript.** A chat panel beside the text, where every answer
  cites timestamps you can click. Not built; the open decisions are in
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

**There is no installer yet.** Packaging, signing and auto-update are Phase 5 in
[PHASES.md](docs/PHASES.md), and that phase has started but not shipped. Until it does there
are two ways to run this: build it from source, below, or take the `uindosill-win-x64` artefact
from any CI run of `master` — a self-contained publish of the CLI and the desktop app with the cpu
and vulkan natives already in place, kept for seven days, unsigned. Both still need a model, which
the CLI or the app's Models tab downloads.

## Quick start

```bash
dotnet build Uindosill.slnx
dotnet test  Uindosill.slnx          # 451 tests, no weights needed, runs on Linux

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
uindosill doctor                          # what this machine has, and which backends load
uindosill models list
uindosill models download tdt-0.6b-v3-f16
uindosill transcribe -f srt,txt *.mp4
uindosill bench recording.wav
```

`uindosill` is the CLI's assembly name: after that build it is
`src/Parakeet.Cli/bin/Release/net10.0/uindosill.exe`, and `dotnet run --project src/Parakeet.Cli --`
runs the same thing. Vulkan runs with bf16 disabled by default, which is what lets the model load
on an AMD integrated GPU whose driver mishandles it; `--vk-bf16` on `transcribe` or `bench` leaves
bf16 on, for measuring the difference or for a driver known to have fixed it. Why the default is
what it is, and what it measured, is in [UNPROVEN.md](docs/UNPROVEN.md).

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

`scripts/` holds eleven PowerShell tasks — two for vendoring, four measurement harnesses (speed
and memory, the second machine, word error rate against human transcripts, and diarisation error
rate against hand-labelled speaker turns), two transcript comparisons, two for the v2 spike, and
one that moves run reports and test material over rclone — and `scripts/lab.ps1` is one entry
point for them: run it bare to list the tasks, each with the parameters its own script declares.
It dispatches and nothing else, so every task is still runnable on its own.

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
`scripts/measure-second-machine.ps1` probes hardware through CIM and
is Windows throughout, as is `scripts/vendor-cuda.ps1`, which reads a PE import table. For
`measure-wer.ps1`, `measure-second-machine.ps1`, `vendor-cuda.ps1` and `measure-der.ps1`'s cutting
half, `pwsh` at least parses them, which is enough to keep a syntax error off the machine that can
run them.

## Layout

```
src/
  Parakeet.Core/               net10.0            contracts + pure logic; no NuGet, no platform, no UI
  Parakeet.Audio/              net10.0            WAV/RF64 parser + Media Foundation decoding
  Parakeet.Engine.ParakeetCpp/ net10.0            the ONLY project that touches native interop
  Parakeet.Cli/                net10.0            transcribe / models / bench / doctor / notice / wer / der / rttm
  Parakeet.App/                net10.0            Avalonia desktop UI
tests/                                            one per project, all runnable on Linux
```

The one rule that matters: **`Parakeet.Core` references no engine, no platform and no UI.** That is
enforced by the build, not by convention — adding a `PackageReference` to `Parakeet.Core.csproj`
fails the build with an explanation. That seam is what keeps an engine swap to one project instead
of a rewrite.

## Stack

| Layer | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (`net10.0`) | .NET 8 and 9 both reach EOL 2026-11-10. |
| UI | Avalonia 12.1.1 | Plain `net10.0` TFM on Windows, so the desktop app cross-builds from Linux CI. |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | Avalonia's documented default; source-generator based. |
| Engine | `mudler/parakeet.cpp` via P/Invoke | MIT, ABI v6, and the only candidate with a published decode-parity result. |
| Model format | GGUF | `mudler/parakeet-cpp-gguf` (f16, q8_0, q6_k, q5_k, q4_k). |
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
| [NATIVE-BINARIES.md](docs/NATIVE-BINARIES.md) | Vendoring a pinned parakeet.cpp release: the script, the layout, the digests. |
| [MODELS.md](docs/MODELS.md) | The catalogue, and how to pin a digest properly. |
| [LICENSING.md](docs/LICENSING.md) | The CC BY 4.0 obligations, which are not "just attribution", and the CUDA EULA reading. |
| [V2-ASK-THE-TRANSCRIPT.md](docs/V2-ASK-THE-TRANSCRIPT.md) | The open decisions for v2, and the problem that makes it hard. |
| [V3-DICTATION.md](docs/V3-DICTATION.md) | What v3 will need, and the traps waiting there. |
| [PHASES.md](docs/PHASES.md) | The phase plan and what is actually done. |
| [NOTICE.md](NOTICE.md) | The third-party notices as shipped: the CC BY weights, five MIT components, the CUDA runtime. |
| [CLAUDE.md](CLAUDE.md) | Working agreement for an agent session: budget, how to build, the one rule. |

## Licence

This project is MIT — see [LICENSE](LICENSE).

The **model weights are not**. They are CC BY 4.0 from NVIDIA, which permits commercial
redistribution and bundling but attaches a seven-element notice requirement, forbids DRM on the
weights, and withholds patent and trademark rights. The notice is shown inside the application (the
Licences tab, and `uindosill notice`), not only in this repository. See
[NOTICE.md](NOTICE.md) and [LICENSING.md](docs/LICENSING.md).

`parakeet-tdt-0.6b-v3` covers 25 European languages. It does **not** cover Chinese, Japanese,
Korean, Arabic, Hindi or Thai, and this product does not offer them.
