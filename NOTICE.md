# Notices

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

## Third-party components

| Component | Licence | Source |
|---|---|---|
| parakeet.cpp (ggml port of NeMo Parakeet) | MIT | https://github.com/mudler/parakeet.cpp |
| ggml | MIT | https://github.com/ggml-org/ggml |
| Avalonia | MIT | https://github.com/AvaloniaUI/Avalonia |
| NAudio (Windows media decoding only) | MIT | https://github.com/naudio/NAudio |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
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
