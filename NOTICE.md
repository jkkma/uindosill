# Notices

`LICENSE` covers the Uindosill source code, and records the one thing that is not simply MIT: **a
build that vendors libmpv is distributed under GPLv2-or-later**, because that binary and the FFmpeg
libraries inside it are GPL. A build without it contains no GPL component. Neither licence covers
the model weights, the parakeet.cpp native binaries or the other third-party components below, each
of which carries its own terms. Those terms are what this file is.

The same text is shown inside the application — the **Licences** pane of the About window, opened
from the Settings tab, and `uindosill notice`. Both
render it from `src/Parakeet.Core/Licensing/Attribution.cs`, so there is exactly one copy and it
cannot drift.

## Model weights — CC BY 4.0

**Parakeet TDT 0.6B v3 (speech recognition model weights)**

- **Creator:** NVIDIA Corporation
- **Copyright:** Copyright (c) NVIDIA Corporation.
- **Licence notice:** This material is made available under the Creative Commons Attribution 4.0
  International licence (CC BY 4.0).
- **Warranty:** The material is provided as-is and without warranties of any kind, express or
  implied, to the extent permitted under the CC BY 4.0 disclaimer of warranties and limitation of
  liability (section 5 of the licence).
- **Source:** https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3
- **Modifications:** Modified — the original NVIDIA NeMo checkpoint was converted to the GGUF
  format and, for some builds, quantised (q8_0, q6_k, q5_k, q4_k). Uindosill redistributes these
  converted files and does not redistribute the original checkpoint.
- **Licence:** Creative Commons Attribution 4.0 International (CC BY 4.0),
  https://creativecommons.org/licenses/by/4.0/

Those seven items are the §3(a) notice package, not a formatting choice. The warranty notice and the
statement that the material was modified are the two most commonly omitted, and GGUF conversion and
quantisation are modifications.

### Restrictions that come with these weights

- **§2(a)(5)(B) forbids effective technological measures.** Model files must not be encrypted,
  licence-locked or otherwise DRM-wrapped. There is no code path in this project that could do so.
- **§2(b) withholds patent and trademark rights.** Nothing in this product may imply NVIDIA
  endorsement or sponsorship.
- **Language coverage.** `parakeet-tdt-0.6b-v3` covers 25 European languages. It does not cover
  Chinese, Japanese, Korean, Arabic, Hindi or Thai, and the product must not offer them. A test
  asserts that no catalogue entry claims those tags.

## Speaker diarisation weights — CC BY 4.0

**pyannote speaker-diarization-community-1 (speaker diarisation pipeline: segmentation and embedding
model weights, PLDA matrices and pipeline configuration)**

- **Creator:** The pyannote authors (`pyannote/speaker-diarization-community-1`); the VBx clustering
  it uses was contributed by Petr Pálka and Jiangyu Han (Brno University of Technology, Speech@FIT)
  per pyannote.audio's 4.0 release notes.
- **Copyright:** Copyright (c) pyannote contributors.
- **Licence notice:** Licensed under the Creative Commons Attribution 4.0 International License
  (CC BY 4.0).
- **Warranty:** Provided as-is and without warranties or conditions of any kind, to the extent
  possible under law.
- **Source:** https://huggingface.co/pyannote/speaker-diarization-community-1
- **Modifications:** Unmodified — the pipeline configuration, both model checkpoints and both PLDA
  files are installed by URL from the upstream repository at commit
  `3533c8cf8e369892e6b79ff1bf80f7b0286a54ee`, keeping that repository's directory layout, and driven
  as published. Uindosill hosts no copy of any of them. Upstream ships usage reporting enabled, and
  Uindosill switches it off before the package is loaded so that it sends nothing.
- **Licence:** Creative Commons Attribution 4.0 International,
  https://creativecommons.org/licenses/by/4.0/legalcode

**The creator line is what a public source supports, and the card that would settle it is gated.**
CC BY 4.0 §3(a)(1)(A) asks for the credits the licensor designates, and an unauthenticated read of
the model card returns HTTP 401 — so those designations have not been read. Read the card on the
first authenticated install and correct this to what it designates.

### The restriction that comes with speaker labelling

**Speaker diarisation is voice biometrics.** It works by telling people's voices apart, which several
jurisdictions treat as processing biometric information and which may require the consent of the
people recorded. No licence in this product imposes that — it is the law of the place the recording
was made, and it is the user's responsibility on their own material.

That caution used to be a licence term. Until 2026-08-27 speaker labelling was NVIDIA's **Streaming
Sortformer Diarizer 4spk v2.1** under the **NVIDIA Open Model License**, whose §2.3 incorporated
NVIDIA's Trustworthy AI terms and named biometric processing specifically, and whose §2.1 made the
grant revocable where CC BY 4.0 is not. Those weights were retired to `attic/sortformer/` and
**nothing in this product is under that Agreement now**, so the copy of it that §3.1 required no
longer ships. The caution survived the paperwork because it was never really about the paperwork.
`docs/LICENSING.md` keeps the reading of the retired licence.

## Translation weights — Apache-2.0

**OPUS-MT tc-bible-big mul→deu+eng+nld (machine translation model weights)**

- **Creator:** Helsinki-NLP, the Language Technology Research Group at the University of Helsinki
- **Licence:** Apache License, Version 2.0, https://www.apache.org/licenses/LICENSE-2.0 — §4(a)
  wants a **copy** rather than a link, so one ships at `licences/Apache-License-2.0.txt`.
- **Source:** https://huggingface.co/Helsinki-NLP/opus-mt-tc-bible-big-mul-deu_eng_nld
- **Modifications (§4(b)):** the original Marian checkpoint at revision `bb1ef830d5` was exported to
  ONNX in the merged decoder layout by `scripts/export-translation-onnx.py`, which splits it into an
  encoder graph and a decoder graph with past key values exposed. The weights are unchanged and
  unquantised — float32 in, float32 out. Uindosill redistributes the exported graphs and does not
  redistribute the original checkpoint.
- **Warranty:** the work is provided on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
  KIND, either express or implied (§7).

### What the source carries, and what it does not (§4(c) and §4(d))

**Read at the pinned revision `bb1ef830d5` on 2026-08-20** — the file listing and every text file in
it. There is **no `NOTICE` file**, so §4(d) has nothing to reproduce, and there is **no copyright,
patent or trademark notice anywhere in the repository**, so none is reproduced here. The negative
half is stated rather than left silent, because a notice that omits a NOTICE file and one that
records there is none read identically downstream, and only the second says the check was performed.
Inventing a plausible copyright line to fill the gap would be a false notice in front of a user,
which is the failure `models.json`'s own comment about the deferred entries refuses.

The attribution notices it **does** carry are retained, per §4(c):

- Developed by the Language Technology Research Group at the University of Helsinki, as part of the
  [OPUS-MT project](https://github.com/Helsinki-NLP/Opus-MT). Originally trained with Marian NMT and
  converted to PyTorch with the transformers library; training data from [OPUS](https://opus.nlpl.eu/).
- Original model: `opusTCv20230926max50+bt+jhubc_transformer-big_2024-08-18.zip`, at
  https://object.pouta.csc.fi/Tatoeba-MT-models/mul-deu+eng+nld/opusTCv20230926max50+bt+jhubc_transformer-big_2024-08-18.zip
- The source asks to be cited: Tiedemann et al., "Democratizing neural machine translation with
  OPUS-MT" (Language Resources and Evaluation 58, 2023, doi:10.1007/s10579-023-09704-w); Tiedemann
  and Thottingal, "OPUS-MT – Building open translation services for the World" (EAMT 2020); and
  Tiedemann, "The Tatoeba Translation Challenge – Realistic Data Sets for Low Resource and
  Multilingual MT" (WMT 2020).
- Acknowledgements, in the source's own words: "The work is supported by the HPLT project, funded by
  the European Union’s Horizon Europe research and innovation programme under grant agreement
  No 101070350. We are also grateful for the generous computational resources and IT infrastructure
  provided by CSC -- IT Center for Science, Finland, and the EuroHPC supercomputer LUMI."

## Speech-detection weights — MIT

**Silero VAD v5 (voice activity detection model, `silero_vad.onnx`)**

- **Creator:** Silero Team
- **Copyright:** Copyright (c) 2020-present Silero Team
- **Licence:** MIT License — the permission notice and disclaimer ship with this application at
  `licences/silero-vad-LICENSE.txt`, which is the upstream `LICENSE` file byte for byte (SHA-256
  `2e63e9a3…e925520b`, fetched 2026-08-23 from commit `6478567951ae5c9979ad7b234185b5515f4be7a1`,
  tag v5.1.2).
- **Source:** https://github.com/snakers4/silero-vad
- **Modifications:** Unmodified. The ONNX graph is installed by URL from the upstream repository at
  that commit and driven as published; Uindosill hosts no copy of it, and what this project adds is
  the C# that feeds it (`src/Parakeet.Engine.SileroVad/`), which is this project's own.

MIT asks for less than CC BY's seven elements and more than a licence name: the copyright notice and
the permission text travel with the material. `Licences.targets` copies the file into every build
output, `scripts/package-windows.ps1` refuses to pack a publish without it, and
`Licensing/Attribution.cs` carries the entry as `MitAttribution`, the fourth licence shape there — a
record that cannot be constructed without the copyright line and the path to the permission text.

The graph runs on ONNX Runtime **in this process**, not in the bundled Python: since 2026-08-23
`Microsoft.ML.OnnxRuntime` 1.29.0 is beside the .NET assemblies again, and the runtime's own MIT
notice and `ThirdPartyNotices.txt` in `licences/` cover that copy directly — see the table below and
`docs/LICENSING.md`.

## Third-party components

| Component | Licence | Source |
|---|---|---|
| parakeet.cpp (ggml port of NeMo Parakeet) | MIT | https://github.com/mudler/parakeet.cpp |
| ggml | MIT | https://github.com/ggml-org/ggml |
| llama.cpp (the `llama-server` child process behind the Ask panel; the default channel ships the vulkan drop, win-cuda the CUDA drop) | MIT — Copyright (c) 2023-2026 The ggml authors. The release archives ship no licence file, so the MIT text is fetched from the source tree at the pinned tag and travels at `native/win-x64/llm/<backend>/LICENSE`; `docs/NATIVE-BINARIES.md` holds the pin and the digests | https://github.com/ggml-org/llama.cpp |
| Avalonia | MIT | https://github.com/AvaloniaUI/Avalonia |
| NAudio (Windows media decoding only) | MIT | https://github.com/naudio/NAudio |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Instrument Sans (typeface; desktop application only) | OFL-1.1 — Copyright 2022 The Instrument Sans Project Authors; `licences/InstrumentSans-OFL.txt` travels with it | https://github.com/Instrument/instrument-sans |
| Chivo Mono (typeface; desktop application only) | OFL-1.1 — Copyright 2019 The Chivo Project Authors; `licences/ChivoMono-OFL.txt` travels with it | https://github.com/Omnibus-Type/Chivo |
| Velopack (installer and update framework; desktop application only) | MIT — Copyright (c) Velopack Ltd. All rights reserved. | https://github.com/velopack/velopack |
| ONNX Runtime (`Microsoft.ML.OnnxRuntime` 1.29.0 beside the .NET assemblies, running the speech-detection graph in process; and `onnxruntime-webgpu` 1.27.0 in the bundled Python, running the translator; the diariser is torch and runs no graph on it) | MIT — Copyright (c) Microsoft Corporation. Bundles third-party components under their own licences; `licences/onnxruntime-LICENSE.txt` and `licences/onnxruntime-ThirdPartyNotices.txt` are the 1.29.0 package's own (69 blocks, verbatim), which is the in-process copy's notice exactly; the wheel ships its own `ThirdPartyNotices.txt` | https://github.com/microsoft/onnxruntime |
| NVIDIA CUDA runtime (`cudart64_12.dll`, `cublas64_12.dll`, `cublasLt64_12.dll`) | NVIDIA CUDA Toolkit EULA — proprietary, not MIT; redistributable under Attachment A | https://docs.nvidia.com/cuda/eula/index.html |
| NVIDIA CUDA runtime 13.3 (the ask tier's runtime in the win-cuda channel's `llm/cuda`, beside `llama-server.exe` — `cudart64_13.dll` and the rest of upstream's `cudart-llama` archive) | NVIDIA CUDA Toolkit EULA — proprietary, not MIT; redistributable under Attachment A; `docs/LICENSING.md` records what about the inventory is still owed | https://docs.nvidia.com/cuda/eula/index.html |
| CPython (embeddable 3.12.10; the interpreter the diariser and the translator run in) | PSF License Agreement Version 2, plus the Microsoft Distributable Code conditions its Windows binary build adds | https://www.python.org/ |
| The bundled Python packages (pinned in `python/requirements-bundle.txt`) | Mostly MIT, BSD and Apache-2.0 — but `soundfile`'s wheel carries an LGPL-2.1 `libsndfile`. **All 99 are listed below**, generated from an assembled bundle. That one is the only LGPL component: a separate DLL loaded at run time and replaceable, which `licences/LGPL-WRITTEN-OFFER.txt` discharges under §6(c) and `docs/LICENSING.md` reads. A statically linked libsoxr was a second until 2026-08-26, when librosa and `soxr` left the bundle | https://pypi.org/ |
| NVIDIA NeMo (source; ran the retired diariser's speaker cache. **Not redistributed since 2026-08-27** — it moved to `attic/sortformer/uindosill_engines/_vendor/nemo/` with that engine and no build or package carries it. Listed because the source tree still contains it) | Apache-2.0 — Copyright (c) 2025, NVIDIA CORPORATION | https://github.com/NVIDIA/NeMo |
| pyannote.audio (the released wheel, pinned in `python/requirements-bundle.txt`; the segmentation, embedding and VBx clustering pipeline the second diariser runs on) | MIT — *Copyright (c) 2020 CNRS*, reproduced as the repository's `LICENSE` states it; the licence text travels in the wheel's own `.dist-info`. Its `pipelines/clustering.py` carries the VBx implementation contributed by BUT Speech@FIT under the same MIT terms — unlike DiariZen's vendored `clustering/VBx.py`, which was Apache-2.0 under its own header and left with that engine. **The model weights are not here** — they are CC BY 4.0 and are downloaded, never bundled; see `docs/LICENSING.md` | https://github.com/pyannote/pyannote-audio |

### The CUDA runtime is the one component here that is not MIT

Builds that vendor the **opt-in CUDA backend** ship three NVIDIA proprietary binaries beside
`parakeet.dll`. The CPU and Vulkan backends contain none of them, and the row above is listed
unconditionally anyway: a notice that appears only when a build flag says so is a notice that can go
missing. Since 2026-08-24 the win-cuda channel also carries a **second CUDA runtime major — 13.3,
inside `llm/cuda` beside `llama-server.exe`** — for the Ask panel's model; same EULA, same
Attachment A basis, its own row above.

§2.6 (Attachment A) of the CUDA Toolkit EULA lists `cudart`, `cublas` and `cublasLt` as files that
may be distributed with applications, and says so for version-numbered variants of those names
explicitly. The conditions that come with that permission are in §1.1.2 and §1.2: the application
must have material additional functionality beyond the included portions, the redistributed files
must be accessed only by that application, and the SDK may not be distributed as a stand-alone
product. `docs/LICENSING.md` records how this product meets each, and what about that reading is
still unverified.

### The bundled Python is a redistribution, and these are its notices

The diariser and the translator run out of process in an interpreter the installer carries, so the
packages they import stop being dependencies and become files a recipient receives. That set is
a CPython 3.12.10 and the wheels `python/requirements-bundle.txt` pins — **109 distributions**
once the transitive set is resolved, read off an assembled bundle on 2026-08-28. **Releases have
now shipped with it**: `v1.0.0-rc.3`, published 2026-08-23, was the first packed with the bundle,
so these files have reached recipients, and `v1.0.0-rc.6` of 2026-08-28 carries the 109 below.

**They are assembled below, from a bundle rather than from memory.** The reasoning that once kept
this material in `docs/LICENSING.md` — that a notice file should say what travels with the product,
and until an installer carried a Python none of it did — expired on 2026-08-23 when one shipped. The
table is generated by `scripts/collect-python-notices.py`, which reads the installed `METADATA` of
every distribution in an assembled bundle and splices the result between the markers below;
`--check` fails when the two disagree, so a changed pin cannot leave a stale notice behind.

**The table was stale for one day, and a warning saying so stood here until 2026-08-28.** `7d43e08`
moved the second diariser from DiariZen's vendored fork of pyannote-audio 3.1.1 to the released
`pyannote.audio==4.0.7` wheel on 2026-08-27, and no assembled bundle existed that day to read the
new closure from. `5cbcb7d` built one and regenerated the table on 2026-08-28: 99 distributions
became 109 and 231 licence files became 248. The four pinned `pyannote.*` rows gave way to that
wheel's own closure, with `lightning`, `matplotlib`, `rich`, three `opentelemetry` packages,
`torch-audiomentations`, `pytorch-metric-learning`, `asteroid-filterbanks`, `pyannoteai-sdk` and
`torchcodec` beside them, and `onnx`, `onnxscript`, `onnx-ir` and `ml_dtypes` arriving with the
diariser's ONNX export route.

**Checked against what shipped, not only against a local bundle.** The published
`uindosill-python-win-x64.zip` for `v1.0.0-rc.6` was read on 2026-08-28 — its central directory
alone, by HTTP range request, rather than its 459,584,428 bytes — and it holds exactly these 109
distributions directly under `Lib/site-packages`. **A whole-archive sweep finds 121 instead**, and
the extra twelve are `setuptools/_vendor/`'s own bundled metadata rather than installed
distributions: `site.glob("*.dist-info")` in the generating script is not recursive and does not see
them. Recorded because 121 is what a reader who greps the archive will get, and the difference
between the two numbers is a counting rule rather than a discrepancy.

`torchcodec` is the addition worth naming for licensing, and it redistributes no FFmpeg: its own
description says it uses whatever FFmpeg is already installed, so the LGPL exit of 1702d9e stands.
That is a reading of upstream's documentation, not of an assembled wheel.

`docs/LICENSING.md` keeps what each licence *obliges*, which is the part no generator can write.

<!-- BEGIN bundled-python-notices -->

**109 distributions, read off an assembled bundle** by `scripts/collect-python-notices.py`, which is what keeps this list from being a recollection. Every licence below is the one the installed package states in its own `METADATA` — PEP 639's `License-Expression` where the wheel has one, the legacy `License` field or the classifier where it does not, which is why some rows read as an SPDX expression and others as a category.

**The texts themselves already travel with the product.** `pip install --target` keeps each wheel's `.dist-info`, the bundling script prunes only `__pycache__`, and the packaging script copies the tree whole — so 248 licence and notice files ship inside the interpreter directory. The paths below are relative to `python/Lib/site-packages` in an installed copy.

| Distribution | Version | Licence, as the package states it | Text that ships |
|---|---|---|---|
| accelerate | 1.14.0 | Apache | `accelerate-1.14.0.dist-info/licenses/LICENSE` |
| aiohappyeyeballs | 2.7.1 | PSF-2.0 | `aiohappyeyeballs-2.7.1.dist-info/licenses/LICENSE` |
| aiohttp | 3.14.3 | Apache-2.0 AND MIT | `aiohttp-3.14.3.dist-info/licenses/LICENSE.txt` and 1 more |
| aiosignal | 1.4.0 | Apache 2.0 | `aiosignal-1.4.0.dist-info/licenses/LICENSE` |
| alembic | 1.19.1 | MIT | `alembic-1.19.1.dist-info/licenses/LICENSE` |
| antlr4-python3-runtime | 4.9.3 | BSD | **none ships** — METADATA says `BSD` with no version; the sdist carries no licence file. Built from source because it publishes no wheel — see the allowlist in `scripts/bundle-python.ps1`. Reached through `omegaconf`, which pins `==4.9.*`. |
| asteroid-filterbanks | 0.4.0 | MIT | `asteroid_filterbanks-0.4.0.dist-info/LICENSE` |
| attrs | 26.1.0 | MIT | `attrs-26.1.0.dist-info/licenses/LICENSE` |
| certifi | 2026.7.22 | MPL-2.0 | `certifi-2026.7.22.dist-info/licenses/LICENSE` |
| cffi | 2.1.1 | MIT-0 | `cffi-2.1.1.dist-info/licenses/LICENSE` |
| charset-normalizer | 3.5.1 | MIT | `charset_normalizer-3.5.1.dist-info/licenses/LICENSE` |
| colorama | 0.4.6 | BSD License | `colorama-0.4.6.dist-info/licenses/LICENSE.txt` |
| colorlog | 6.12.0 | MIT License | `colorlog-6.12.0.dist-info/licenses/LICENSE` |
| contourpy | 1.3.3 | BSD 3-Clause License | `contourpy-1.3.3.dist-info/LICENSE` |
| cycler | 0.12.1 | Copyright (c) 2015, matplotlib project | `cycler-0.12.1.dist-info/LICENSE` |
| einops | 0.8.2 | MIT | `einops-0.8.2.dist-info/licenses/LICENSE` |
| filelock | 3.32.4 | MIT | `filelock-3.32.4.dist-info/licenses/LICENSE` |
| flatbuffers | 25.12.19 | Apache 2.0 | **none ships** — METADATA says `Apache 2.0`; the wheel carries no licence file. |
| fonttools | 4.63.0 | MIT | `fonttools-4.63.0.dist-info/licenses/LICENSE` and 1 more |
| frozenlist | 1.8.0 | Apache-2.0 | `frozenlist-1.8.0.dist-info/licenses/LICENSE` |
| fsspec | 2026.7.0 | BSD-3-Clause | `fsspec-2026.7.0.dist-info/licenses/LICENSE` |
| googleapis-common-protos | 1.75.2 | Apache 2.0 | `googleapis_common_protos-1.75.2.dist-info/licenses/LICENSE` |
| greenlet | 3.5.5 | MIT AND PSF-2.0 | `greenlet-3.5.5.dist-info/licenses/LICENSE` and 1 more |
| grpcio | 1.83.1 | Apache-2.0 | `grpcio-1.83.1.dist-info/licenses/LICENSE` |
| huggingface_hub | 0.36.2 | Apache | `huggingface_hub-0.36.2.dist-info/licenses/LICENSE` |
| idna | 3.19 | BSD-3-Clause | `idna-3.19.dist-info/licenses/LICENSE.md` |
| Jinja2 | 3.1.6 | BSD License | `jinja2-3.1.6.dist-info/licenses/LICENSE.txt` |
| joblib | 1.5.3 | BSD-3-Clause | `joblib-1.5.3.dist-info/licenses/LICENSE.txt` |
| julius | 0.2.8 | MIT License | `julius-0.2.8.dist-info/licenses/LICENSE` |
| kiwisolver | 1.5.0 | ========================= | `kiwisolver-1.5.0.dist-info/licenses/LICENSE` |
| lightning | 2.6.5 | Apache-2.0 | `lightning-2.6.5.dist-info/licenses/LICENSE` |
| lightning-utilities | 0.15.3 | Apache-2.0 | `lightning_utilities-0.15.3.dist-info/licenses/LICENSE` |
| Mako | 1.4.1 | MIT | `mako-1.4.1.dist-info/licenses/LICENSE` |
| markdown-it-py | 4.2.0 | MIT License | `markdown_it_py-4.2.0.dist-info/licenses/LICENSE` and 1 more |
| MarkupSafe | 3.0.3 | BSD-3-Clause | `markupsafe-3.0.3.dist-info/licenses/LICENSE.txt` |
| matplotlib | 3.11.1 | License agreement for matplotlib versions 1.3.0 and later | `matplotlib-3.11.1.dist-info/LICENSE` |
| mdurl | 0.1.2 | MIT License | `mdurl-0.1.2.dist-info/LICENSE` |
| ml_dtypes | 0.6.0 | Apache-2.0 | `ml_dtypes-0.6.0.dist-info/licenses/LICENSE` and 1 more |
| mpmath | 1.3.0 | BSD | `mpmath-1.3.0.dist-info/LICENSE` |
| multidict | 6.7.1 | Apache License 2.0 | `multidict-6.7.1.dist-info/licenses/LICENSE` |
| narwhals | 2.25.0 | MIT | `narwhals-2.25.0.dist-info/licenses/LICENSE.md` |
| networkx | 3.6.1 | BSD-3-Clause | `networkx-3.6.1.dist-info/licenses/LICENSE.txt` |
| numpy | 2.5.2 | BSD-3-Clause AND 0BSD AND MIT AND Zlib AND CC0-1.0 | `numpy-2.5.2.dist-info/licenses/LICENSE.txt` and 16 more |
| omegaconf | 2.3.1 | BSD License | `omegaconf-2.3.1.dist-info/licenses/LICENSE` |
| onnx | 1.22.0 | Apache-2.0 | `onnx-1.22.0.dist-info/licenses/LICENSE` and 1 more |
| onnx-ir | 1.0.0 | Apache-2.0 | `onnx_ir-1.0.0.dist-info/licenses/LICENSE` |
| onnxruntime-webgpu | 1.27.0 | MIT License | `onnxruntime/LICENSE` |
| onnxscript | 0.7.1 | MIT License | `onnxscript-0.7.1.dist-info/licenses/LICENSE` |
| opentelemetry-api | 1.44.0 | Apache-2.0 | `opentelemetry_api-1.44.0.dist-info/licenses/LICENSE` |
| opentelemetry-exporter-otlp | 1.44.0 | Apache-2.0 | `opentelemetry_exporter_otlp-1.44.0.dist-info/licenses/LICENSE` |
| opentelemetry-exporter-otlp-proto-common | 1.44.0 | Apache-2.0 | `opentelemetry_exporter_otlp_proto_common-1.44.0.dist-info/licenses/LICENSE` |
| opentelemetry-exporter-otlp-proto-grpc | 1.44.0 | Apache-2.0 | `opentelemetry_exporter_otlp_proto_grpc-1.44.0.dist-info/licenses/LICENSE` |
| opentelemetry-exporter-otlp-proto-http | 1.44.0 | Apache-2.0 | `opentelemetry_exporter_otlp_proto_http-1.44.0.dist-info/licenses/LICENSE` |
| opentelemetry-proto | 1.44.0 | Apache-2.0 | `opentelemetry_proto-1.44.0.dist-info/licenses/LICENSE` |
| opentelemetry-sdk | 1.44.0 | Apache-2.0 | `opentelemetry_sdk-1.44.0.dist-info/licenses/LICENSE` |
| opentelemetry-semantic-conventions | 0.65b0 | Apache-2.0 | `opentelemetry_semantic_conventions-0.65b0.dist-info/licenses/LICENSE` |
| optimum | 2.1.0 | Apache | `optimum-2.1.0.dist-info/licenses/LICENSE` |
| optimum-onnx | 0.1.0 | Apache-2.0 | `optimum_onnx-0.1.0.dist-info/licenses/LICENSE` |
| optuna | 4.9.0 | MIT License | `optuna-4.9.0.dist-info/licenses/LICENSE` and 1 more |
| packaging | 26.3 | Apache-2.0 OR BSD-2-Clause | `packaging-26.3.dist-info/licenses/LICENSE` and 2 more |
| pandas | 3.0.5 | BSD 3-Clause License | `pandas-3.0.5.dist-info/LICENSE` |
| pillow | 12.3.0 | MIT-CMU | `pillow-12.3.0.dist-info/licenses/LICENSE` |
| primePy | 1.3 | UNKNOWN | **none ships** — **The weakest provenance in the bundle.** METADATA's `License` field is the literal `UNKNOWN` and only a trove classifier claims MIT; the wheel carries no licence file. Reached transitively through `torch-pitch-shift` under `torch-audiomentations`. |
| propcache | 0.5.2 | Apache-2.0 | `propcache-0.5.2.dist-info/licenses/LICENSE` and 1 more |
| protobuf | 7.36.0 | 3-Clause BSD License | `protobuf-7.36.0.dist-info/LICENSE` |
| psutil | 7.2.2 | BSD-3-Clause | `psutil-7.2.2.dist-info/LICENSE` |
| pyannote-audio | 4.0.7 | (none stated) | `pyannote_audio-4.0.7.dist-info/licenses/LICENSE` |
| pyannote-core | 6.0.1 | (none stated) | `pyannote_core-6.0.1.dist-info/licenses/LICENSE` |
| pyannote-database | 6.1.1 | (none stated) | `pyannote_database-6.1.1.dist-info/licenses/LICENSE` |
| pyannote-metrics | 4.1 | (none stated) | `pyannote_metrics-4.1.dist-info/licenses/LICENSE` |
| pyannote-pipeline | 4.0.0 | (none stated) | `pyannote_pipeline-4.0.0.dist-info/licenses/LICENSE` |
| pyannoteai-sdk | 0.4.0 | (none stated) | `pyannoteai_sdk-0.4.0.dist-info/licenses/LICENSE` |
| pycparser | 3.0 | BSD-3-Clause | `pycparser-3.0.dist-info/licenses/LICENSE` |
| Pygments | 2.21.0 | BSD-2-Clause | `pygments-2.21.0.dist-info/licenses/AUTHORS` and 1 more |
| pyparsing | 3.3.2 | MIT | `pyparsing-3.3.2.dist-info/licenses/LICENSE` |
| python-dateutil | 2.9.0.post0 | Dual License | `python_dateutil-2.9.0.post0.dist-info/LICENSE` |
| pytorch-lightning | 2.6.5 | Apache-2.0 | `pytorch_lightning-2.6.5.dist-info/licenses/LICENSE` |
| pytorch-metric-learning | 2.9.0 | UNKNOWN | `pytorch_metric_learning-2.9.0.dist-info/LICENSE` |
| PyYAML | 6.0.3 | MIT | `pyyaml-6.0.3.dist-info/licenses/LICENSE` |
| regex | 2026.7.19 | Apache-2.0 AND CNRI-Python | `regex-2026.7.19.dist-info/licenses/LICENSE.txt` |
| requests | 2.34.2 | Apache-2.0 | `requests-2.34.2.dist-info/licenses/LICENSE` and 1 more |
| rich | 15.0.0 | MIT | `rich-15.0.0.dist-info/licenses/LICENSE` |
| safetensors | 0.8.0 | Apache Software License | `safetensors-0.8.0.dist-info/licenses/LICENSE` |
| scikit-learn | 1.9.0 | BSD-3-Clause | `scikit_learn-1.9.0.dist-info/licenses/COPYING` |
| scipy | 1.18.1 | BSD License | `scipy-1.18.1.dist-info/LICENSE.txt` |
| semver | 3.0.4 | Copyright (c) 2013, Konstantine Rybnikov | `semver-3.0.4.dist-info/LICENSE.txt` |
| sentencepiece | 0.2.2 | Apache-2.0 | **none ships** — METADATA says `Apache-2.0`; the wheel carries no licence file. |
| setuptools | 84.0.0 | MIT | `setuptools-84.0.0.dist-info/licenses/LICENSE` |
| six | 1.17.0 | MIT | `six-1.17.0.dist-info/LICENSE` |
| sortedcontainers | 2.4.0 | Apache 2.0 | `sortedcontainers-2.4.0.dist-info/LICENSE` |
| soundfile | 0.14.0 | BSD 3-Clause License | `soundfile-0.14.0.dist-info/LICENSE` |
| SQLAlchemy | 2.0.52 | MIT | `sqlalchemy-2.0.52.dist-info/licenses/LICENSE` |
| sympy | 1.14.0 | BSD | `sympy-1.14.0.dist-info/licenses/AUTHORS` and 1 more |
| tensorboardX | 2.6.5 | MIT | `tensorboardx-2.6.5.dist-info/licenses/LICENSE` |
| threadpoolctl | 3.6.0 | BSD-3-Clause | `threadpoolctl-3.6.0.dist-info/licenses/LICENSE` |
| tokenizers | 0.22.2 | Apache Software License | **none ships** — Classifier says `Apache Software License`; the wheel carries no licence file. |
| toml | 0.10.2 | MIT | `toml-0.10.2.dist-info/LICENSE` |
| torch | 2.13.0+cpu | Apache-2.0 AND Apache-2.0 WITH LLVM-exception AND BSD-2-Clause AND BSD-3-Clause AND BSL-1.0 AND MIT | `torch-2.13.0+cpu.dist-info/licenses/LICENSE` and 106 more |
| torch-audiomentations | 0.12.0 | MIT | `torch_audiomentations-0.12.0.dist-info/LICENSE` |
| torch_pitch_shift | 1.2.5 | MIT License | `torch_pitch_shift-1.2.5.dist-info/LICENSE` |
| torchaudio | 2.11.0+cpu | BSD License | `torchaudio-2.11.0+cpu.dist-info/licenses/LICENSE` |
| torchcodec | 0.16.0+cpu | (none stated) | `torchcodec-0.16.0+cpu.dist-info/licenses/LICENSE` and 7 more |
| torchmetrics | 1.9.0 | Apache-2.0 | `torchmetrics-1.9.0.dist-info/licenses/LICENSE` |
| tqdm | 4.70.0 | MPL-2.0 AND MIT | `tqdm-4.70.0.dist-info/licenses/LICENCE` |
| transformers | 4.57.6 | Apache 2.0 License | `transformers-4.57.6.dist-info/licenses/LICENSE` |
| typing_extensions | 4.16.0 | PSF-2.0 | `typing_extensions-4.16.0.dist-info/licenses/LICENSE` |
| tzdata | 2026.3 | Apache-2.0 | `tzdata-2026.3.dist-info/licenses/LICENSE` and 1 more |
| urllib3 | 2.7.0 | MIT | `urllib3-2.7.0.dist-info/licenses/LICENSE.txt` |
| yarl | 1.24.5 | Apache-2.0 | `yarl-1.24.5.dist-info/licenses/LICENSE` and 1 more |

**5 of the 109 ship no licence text of their own**: `antlr4-python3-runtime`, `flatbuffers`, `primePy`, `sentencepiece`, `tokenizers`. What each claims instead is in `KNOWN_TEXTLESS` in the script that writes this, with the route by which it arrives. Most name Apache, and the Apache-2.0 text travels in this bundle several times over — `onnx`, `optimum` and `transformers` each carry a copy — so for those a recipient has the licence even without one attached. **That is not true of all of them**: `antlr4-python3-runtime` says only `BSD`, which names a family rather than one of two licences that differ by a clause, and `primePy`'s `License` field is the literal `UNKNOWN` with a classifier alone claiming MIT. Upstream's omission in every case, recorded rather than papered over.

**Three of these are not simply permissive, and they are the rows to read twice.** `soundfile` is BSD-3-Clause but carries an LGPL-2.1 `libsndfile` as a *separate, dynamically loaded and replaceable* DLL, whose `COPYING` ships at `_soundfile_data/COPYING` — which this table does not show because it belongs to no `.dist-info`. `certifi` and `tqdm` are MPL-2.0, file-level copyleft. **`soxr` was a fourth until 2026-08-26**, and it was the difficult one: its libsoxr was statically linked into `soxr/soxr_ext.pyd`, where LGPL-2.1 §6(b) could not reach it. It arrived only because librosa declared it, and librosa left when the one call it was here for became a committed matrix. **Nothing statically linked in this product is under the LGPL.** `licences/LGPL-WRITTEN-OFFER.txt` discharges what remains and `docs/LICENSING.md` reads it.

Re-running against a bundle built from the same pins must produce this section unchanged; `--check` is what holds it, and a changed pin is expected to change this table. The bundle's own location is deliberately not printed — it is a path on whoever ran the script's machine, this repository is public, and a guard that embeds one fails on every other machine for a reason that has nothing to do with notices.

<!-- END bundled-python-notices -->

### NeMo was vendored as source, and it left with the engine that called it

**Nothing under `python/` vendors NeMo since 2026-08-27.** The tree moved to
`attic/sortformer/uindosill_engines/_vendor/nemo/` with the diariser that imported it, and the
`_vendor/` directory it lived in is gone — the bundled Python now installs every package it runs
from PyPI. The Apache-2.0 reading below is kept because the files are still in this repository and a
redistribution of the source tree still carries them; it is a statement about what is in the attic
rather than about what the bundle ships.

That tree holds fifteen files, and **two of them are NVIDIA's**:
`collections/asr/modules/sortformer_modules.py` (1,307 lines) and
`collections/asr/parts/preprocessing/features.py` (494 lines), both copied **verbatim** with their
copyright headers intact. They are here so the diariser's Arrival-Order Speaker Cache is NVIDIA's
`streaming_update_async` *imported and called* rather than a port of it, which is what lets this
project's diarisation error rate be a statement about NVIDIA's algorithm rather than about somebody's
reading of it.

**The other thirteen are this project's own** and carry no NVIDIA claim: eight empty `__init__.py`
and five stubs totalling twenty-four lines, written so those two files import without the whole
toolkit behind them. Two of the five stand in for real NeMo classes — `NeuralModule` becomes a plain
`torch.nn.Module` and `Exportable` becomes an empty class — which is a substitution rather than a
copy, and is faithful only for the speaker-cache path that is actually exercised. What underwrites
that is the measurement: the assembled tree reproduces the project's AMI figure of 16.33%.

The four conditions in Apache-2.0 §4 were checked before this was committed, not after:

- **§4(a)**, a copy of the licence, ships at `licences/Apache-License-2.0.txt`.
- **§4(b)**, prominent notices on changed files, is satisfied vacuously for the two NVIDIA files:
  **neither was modified.** Both are byte-identical to what the diarisation spike carried. The
  stubs are not NVIDIA's work and so are not modifications of it.
- **§4(c)**, retaining attribution notices, holds — both NVIDIA files keep the copyright header and
  the Apache-2.0 block they arrived with.
- **§4(d)** does not apply: **NeMo ships no NOTICE file.** Its repository root carries `LICENSE` and
  nothing else of the kind, checked 2026-08-21 against the GitHub contents API. That is the same
  answer the Marian weights gave, and it is recorded here rather than assumed for the same reason.

The model weights were a separate question with a separate answer — Streaming Sortformer was under
the **NVIDIA Open Model License**, not Apache-2.0. Both are retired; `attic/sortformer/` holds the
Agreement copy that used to ship at `licences/`.

## Deliberately not used

**TEN-VAD.** Its modified Apache-2.0 carries an Agora non-compete clause. Voice activity detection
here is a plain energy gate written for this project
(`src/Parakeet.Core/Segmentation/StreamingSegmenter.cs`) by default and, as an opt-in since
2026-08-23, Silero VAD — which is MIT and has its own section above. TEN-VAD was not the model
considered for that opt-in and is still not used.
