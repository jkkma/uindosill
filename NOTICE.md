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

## Deliberately not used

**TEN-VAD.** Its modified Apache-2.0 carries an Agora non-compete clause. Voice activity detection
here is a plain energy gate written for this project
(`src/Parakeet.Core/Segmentation/StreamingSegmenter.cs`).
