# Uindosill

A Windows desktop app that transcribes audio and video files **locally** with NVIDIA Parakeet.
Drop files in, get text out — plain text, SRT, VTT, word-timed VTT for karaoke-style highlighting,
JSON with timestamps, Markdown. No cloud, no Python, no account.

> **Status: the CLI and the desktop app both produce correct transcripts from real weights on real
> Windows.** Ten minutes of podcast through Media Foundation, parakeet.cpp v0.5.0,
> `tdt-0.6b-v3-f16`, on one 16-core x64 desktop with an RTX 5080 — **RTF 0.082 on CPU, 0.011 on
> Vulkan, 0.0064 on CUDA** — 98 segments and 1,573 words on all three, no duplicated or dropped
> words at any segment join. Three hours has been run end to end on CPU. That is one machine and
> one file, not a benchmark, and the Vulkan figure is steady-state: the *first* Vulkan run on a
> fresh machine takes 14 s rather than 6.6 s, because the driver is compiling shaders inside the
> number that looks like decode time. Every figure here carries its backend and its caveats in
> [docs/UNPROVEN.md](docs/UNPROVEN.md). All five quantisations have now been run against 2 h 55 m of
> real podcast and diffed against f16 — 0.42% of tokens for q8_0 rising to 2.69% for q4_k, over a
> CPU-versus-CUDA noise floor of 0.11%, with no sign of the silent collapse that sank the analogous
> ONNX INT8 export. That is divergence from f16, **not** a word error rate: no ground truth exists
> for that audio, so nothing here is a quality clearance for any quantisation.

## What it does

- **v1 is file transcription.** No global hotkeys, no text injection, no overlay HUD, no microphone
  capture.
- **v2 is asking questions about a transcript.** A chat panel beside the text, where every answer
  cites timestamps you can click. Not built; the open decisions are in
  [docs/V2-ASK-THE-TRANSCRIPT.md](docs/V2-ASK-THE-TRANSCRIPT.md).
- **v3 is push-to-talk dictation.** Not built, not architected out —
  [docs/V3-DICTATION.md](docs/V3-DICTATION.md) records what it will need.

That order is deliberate, and for two different reasons. The entire Win32 risk surface — global
keyboard hooks that get flagged as keyloggers, text injection that fails silently under UIPI,
overlay windows that steal focus — lives on the dictation path and none of it on the file path,
which is why dictation is last. Asking questions about a transcript sits in front of it because it
needs none of that: it reads a transcript this product already produces, and what it costs instead
is a second native stack and an honesty problem, since a wrong answer is fluent rather than
obviously broken.

## Quick start

```bash
dotnet build Uindosill.slnx
dotnet test  Uindosill.slnx          # 289 tests, no weights needed, runs on Linux

# See the whole pipeline work without a model: real WAVE parsing, real segmentation,
# real subtitle output, canned words.
dotnet run --project src/Parakeet.Cli -- transcribe --fake -f srt,json recording.wav
```

To do real work you need two things this repository does not contain: the parakeet.cpp native
library ([docs/NATIVE-BINARIES.md](docs/NATIVE-BINARIES.md)) and a GGUF model
([docs/MODELS.md](docs/MODELS.md)).

```bash
uindosill doctor                          # what this machine has, and which backends load
uindosill models list
uindosill models download tdt-0.6b-v3-f16
uindosill transcribe -f srt,txt *.mp4
uindosill bench recording.wav
```

### In a container with no toolchain

Every test here runs on Linux with no weights and no display, which is a design constraint rather
than a convenience — a test needing 670 MB of weights is one CI will never run. That constraint is
what makes an ephemeral container a usable place to work, so the toolchain is installed by the
environment's own setup script. Paste `scripts/cloud-setup.sh` into that field: it unpacks a
pinned SDK 10.0.400 and PowerShell 7.6.4 from `packages.microsoft.com` Debian packages, checks
each against the SHA-256 the feed publishes, and warms the NuGet cache. Not the vendor's installer, because `dot.net` and every host it redirects
to are refused by the network policy there; not the Ubuntu feed either, though the image is
Ubuntu. That file's header records both, and the digests are pinned for the same reason
`docs/NATIVE-BINARIES.md` pins a parakeet.cpp release.

So a session in such a container **builds the solution and runs the full suite**, and the `--fake`
pipeline above works there end to end. What it cannot do is transcribe anything real: that needs
the Windows natives and a model, neither of which is in the clone.

`scripts/lab.ps1` is one entry point for the five of them — run it bare to list the tasks, each
with the parameters its own script declares. It dispatches and nothing else, so every task is still
runnable on its own.

The scripts divide along the same line rather than all being out of reach.
`scripts/compare-transcripts.ps1` reads two transcript JSONs and needs nothing else, so it runs
there — including against JSONs the `--fake` engine produced. `scripts/word-distance.ps1` is the
same shape and runs there too: it answers the one question `compare-transcripts.ps1` gets wrong,
how far apart two transcripts are when they are *not* nearly identical, by word-level edit distance
rather than by index alignment.
`scripts/measure-transcribe.ps1` will parse and report on outputs, but cannot produce them.
`scripts/measure-second-machine.ps1` probes hardware through CIM and is Windows throughout, as is
`scripts/vendor-cuda.ps1`, which reads a PE import table. For those last three, `pwsh` at least
parses them, which is enough to keep a syntax error off the machine that can run them.

## Layout

```
src/
  Parakeet.Core/               net10.0            contracts + pure logic; no NuGet, no platform, no UI
  Parakeet.Audio/              net10.0            WAV/RF64 parser + Media Foundation decoding
  Parakeet.Engine.ParakeetCpp/ net10.0            the ONLY project that touches native interop
  Parakeet.Cli/                net10.0            transcribe / models / bench / doctor
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
| Deployment | Self-contained + ReadyToRun, `win-x64` / `win-arm64` | No single-file, no trimming, no NativeAOT. |

Why parakeet.cpp and not the obvious alternatives is recorded in
[docs/ENGINE-CHOICE.md](docs/ENGINE-CHOICE.md).

## Documentation

| Document | What is in it |
|---|---|
| [docs/UNPROVEN.md](docs/UNPROVEN.md) | Everything asserted here that nobody has measured. Read first. |
| [docs/GOTCHAS.md](docs/GOTCHAS.md) | The silent failures, and where each one is handled in this codebase. |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | The seams, the contracts, and why segmentation is not optional. |
| [docs/NATIVE-BINARIES.md](docs/NATIVE-BINARIES.md) | Vendoring a pinned parakeet.cpp release. |
| [docs/MODELS.md](docs/MODELS.md) | The catalogue, and how to pin a digest properly. |
| [docs/LICENSING.md](docs/LICENSING.md) | The CC BY 4.0 obligations, which are not "just attribution". |
| [docs/V2-ASK-THE-TRANSCRIPT.md](docs/V2-ASK-THE-TRANSCRIPT.md) | The open decisions for v2, and the problem that makes it hard. |
| [docs/V3-DICTATION.md](docs/V3-DICTATION.md) | What v3 will need, and the traps waiting there. |
| [docs/PHASES.md](docs/PHASES.md) | The phase plan and what is actually done. |
| [CLAUDE.md](CLAUDE.md) | Working agreement for an agent session: budget, how to build, the one rule. |

## Licence

This project is MIT — see [LICENSE](LICENSE).

The **model weights are not**. They are CC BY 4.0 from NVIDIA, which permits commercial
redistribution and bundling but attaches a seven-element notice requirement, forbids DRM on the
weights, and withholds patent and trademark rights. The notice is shown inside the application (the
Licences tab, and `uindosill notice`), not only in this repository. See
[NOTICE.md](NOTICE.md) and [docs/LICENSING.md](docs/LICENSING.md).

`parakeet-tdt-0.6b-v3` covers 25 European languages. It does **not** cover Chinese, Japanese,
Korean, Arabic, Hindi or Thai, and this product does not offer them.
