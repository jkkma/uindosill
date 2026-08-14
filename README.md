# Uindosill

A Windows desktop app that transcribes audio and video files **locally** with NVIDIA Parakeet.
Drop files in, get text out — plain text, SRT, VTT, JSON with timestamps, Markdown. No cloud, no
Python, no account.

> **Status: nothing here has ever run against real model weights.** Every performance and accuracy
> number you find in this repository is borrowed until somebody measures it. The pipeline, the
> formats, the segmentation and the UI are exercised end to end in CI against a canned engine; the
> native decode path is written against the published C ABI and has not been executed. See
> [docs/UNPROVEN.md](docs/UNPROVEN.md), which is the honest list.

## What it does

- **v1 is file transcription.** No global hotkeys, no text injection, no overlay HUD, no microphone
  capture.
- **v2 is push-to-talk dictation.** Not built, not architected out —
  [docs/V2-DICTATION.md](docs/V2-DICTATION.md) records what it will need.

That order is deliberate. The entire Win32 risk surface — global keyboard hooks that get flagged as
keyloggers, text injection that fails silently under UIPI, overlay windows that steal focus — lives
on the dictation path and none of it on the file path, while the inference engine is identical for
both.

## Quick start

```bash
dotnet build Uindosill.slnx
dotnet test  Uindosill.slnx          # 211 tests, no weights needed, runs on Linux

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
uindosill models download tdt-0.6b-v3-q8_0
uindosill transcribe -f srt,txt *.mp4
uindosill bench recording.wav
```

## Layout

```
src/
  Parakeet.Core/               net10.0            contracts + pure logic; no NuGet, no platform, no UI
  Parakeet.Audio/              net10.0(-windows)  WAV/RF64 parser + Media Foundation decoding
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
| [docs/V2-DICTATION.md](docs/V2-DICTATION.md) | What v2 will need, and the traps waiting there. |
| [docs/PHASES.md](docs/PHASES.md) | The phase plan and what is actually done. |

## Licence

This project is MIT — see [LICENSE](LICENSE).

The **model weights are not**. They are CC BY 4.0 from NVIDIA, which permits commercial
redistribution and bundling but attaches a seven-element notice requirement, forbids DRM on the
weights, and withholds patent and trademark rights. The notice is shown inside the application (the
Licences tab, and `uindosill notice`), not only in this repository. See
[NOTICE.md](NOTICE.md) and [docs/LICENSING.md](docs/LICENSING.md).

`parakeet-tdt-0.6b-v3` covers 25 European languages. It does **not** cover Chinese, Japanese,
Korean, Arabic, Hindi or Thai, and this product does not offer them.
