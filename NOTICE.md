# Notices

`LICENSE` covers the Uindosill source code only. It does not cover the model weights, nor the
parakeet.cpp native binaries and the other third-party components below, each of which carries its
own terms. Those terms are what this file is.

The same text is shown inside the application — the **Licences** tab, and `uindosill notice`. Both
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

## Speaker diarisation weights — NVIDIA Open Model License

**Streaming Sortformer Diarizer 4spk v2.1 (speaker diarisation model weights)**

> Licensed by NVIDIA Corporation under the NVIDIA Open Model License

That sentence is quoted rather than paraphrased because §3.1 of the Agreement mandates it verbatim,
in a Notice file, alongside a **copy of the Agreement** — which is why one ships, at
`licences/NVIDIA-Open-Model-License-2025-10-24.txt`, and why the build copies it into every publish.
A link is not a copy.

- **Agreement:** version dated 24 October 2025,
  https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/
- **Source:** https://huggingface.co/soniqo/Sortformer-Diarization-4spk-ONNX
- **Provenance:** NVIDIA trained `diar_streaming_sortformer_4spk-v2.1`; a third party, soniqo,
  exported it to ONNX with no retraining and no change to weights, architecture or configuration.
  That export is a Model Derivative under §1 and is published under the same terms. Uindosill hosts
  neither file and installs soniqo's copy by URL.
- **Warranty:** NVIDIA provides the model on an "AS IS" basis, without warranties or conditions of
  any kind, express or implied (§6).

### Two things this licence does that CC BY 4.0 does not

- **§2.3 incorporates NVIDIA's Trustworthy AI terms**, which forbid use in violation of applicable
  law and name illegal surveillance and the illegal collection or processing of biometric
  information without consent where consent is required. **Speaker diarisation is voice
  biometrics.** Whether recording and separating people's voices needs their consent is a question
  about the user's own material and the user's own jurisdiction, and it is theirs to answer.
- **§2.1 makes the grant revocable** and lets NVIDIA update the Agreement for legal or regulatory
  reasons; it also terminates automatically on filing patent or copyright litigation over the model,
  or on circumventing a safety guardrail. CC BY 4.0 is irrevocable. The two are not interchangeable,
  and `docs/LICENSING.md` records what that difference means for shipping.

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

## Third-party components

| Component | Licence | Source |
|---|---|---|
| parakeet.cpp (ggml port of NeMo Parakeet) | MIT | https://github.com/mudler/parakeet.cpp |
| ggml | MIT | https://github.com/ggml-org/ggml |
| Avalonia | MIT | https://github.com/AvaloniaUI/Avalonia |
| NAudio (Windows media decoding only) | MIT | https://github.com/naudio/NAudio |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Velopack (installer and update framework; desktop application only) | MIT — Copyright (c) Velopack Ltd. All rights reserved. | https://github.com/velopack/velopack |
| ONNX Runtime (`onnxruntime.dll`; runs the speaker diarisation model) | MIT — Copyright (c) Microsoft Corporation. Bundles 69 components under their own licences; its `ThirdPartyNotices.txt` ships verbatim at `licences/onnxruntime-ThirdPartyNotices.txt` | https://github.com/microsoft/onnxruntime |
| NVIDIA CUDA runtime (`cudart64_12.dll`, `cublas64_12.dll`, `cublasLt64_12.dll`) | NVIDIA CUDA Toolkit EULA — proprietary, not MIT; redistributable under Attachment A | https://docs.nvidia.com/cuda/eula/index.html |
| NVIDIA NeMo (source, vendored at `python/uindosill_engines/_vendor/nemo/`; runs the diariser's speaker cache) | Apache-2.0 — Copyright (c) 2025, NVIDIA CORPORATION | https://github.com/NVIDIA/NeMo |

### The CUDA runtime is the one component here that is not MIT

Builds that vendor the **opt-in CUDA backend** ship three NVIDIA proprietary binaries beside
`parakeet.dll`. The CPU and Vulkan backends contain none of them, and the row above is listed
unconditionally anyway: a notice that appears only when a build flag says so is a notice that can go
missing.

§2.6 (Attachment A) of the CUDA Toolkit EULA lists `cudart`, `cublas` and `cublasLt` as files that
may be distributed with applications, and says so for version-numbered variants of those names
explicitly. The conditions that come with that permission are in §1.1.2 and §1.2: the application
must have material additional functionality beyond the included portions, the redistributed files
must be accessed only by that application, and the SDK may not be distributed as a stand-alone
product. `docs/LICENSING.md` records how this product meets each, and what about that reading is
still unverified.

### NeMo is vendored as source, and it is deliberately not a rewrite

`python/uindosill_engines/_vendor/nemo/` holds fifteen files, and **two of them are NVIDIA's**:
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

The model weights are a separate question with a separate answer — Streaming Sortformer is under the
**NVIDIA Open Model License**, not Apache-2.0, and has its own section above.

## Deliberately not used

**TEN-VAD.** Its modified Apache-2.0 carries an Agora non-compete clause. Voice activity detection
here is a plain energy gate written for this project
(`src/Parakeet.Core/Segmentation/StreamingSegmenter.cs`).
