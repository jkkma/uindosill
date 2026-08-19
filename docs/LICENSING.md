# Licensing obligations

The code is MIT. The weights are not, and their obligations are the ones worth reading twice.

**Two model licences ship, not one.** The transcription weights are CC BY 4.0 and want a
seven-element notice package. The speaker diarisation weights are under the NVIDIA Open Model
License and want one verbatim sentence plus a copy of the agreement — and, unlike CC BY, they are
revocable and carry a use restriction about biometrics. Which entry has which licence is asserted by
a test, so adding a third is a deliberate act rather than a drift.

## The transcription weights are CC BY 4.0 (NVIDIA)

Commercial redistribution and bundling **are** permitted. The condition is **not** "just
attribution".

§3(a) is a seven-element notice package:

1. identification of the creator(s)
2. a copyright notice
3. a notice referring to the licence
4. a notice referring to the **disclaimer of warranties**
5. a URI to the material
6. **an indication that the material was modified** — GGUF conversion and quantisation are
   modifications
7. a statement that it is licensed under CC BY 4.0, with the licence text or a link

Elements 4 and 6 are the two most commonly missed.

**How this project handles it.** `CcByAttribution` in
`src/Parakeet.Core/Licensing/Attribution.cs` has nine `required` properties — one per element,
`LicenceStatement` and `LicenceUri` splitting element 7, plus a `Title`. A record that cannot be
constructed without all of them cannot silently ship with five. There is one renderer, and both the
CLI (`uindosill notice`) and the application's Licences tab call it, so the two cannot drift.
`NOTICE.md` is the same text.

Two tests hold it up, at different layers. `AttributionTests.RenderedNoticeContainsAllSevenRequiredElements`
(`tests/Parakeet.Core.Tests/PipelineTests.cs`) renders the real notice and asserts one string per
§3(a) element, each line commented with the element it covers — so the claim that all seven reach
the output is asserted, and asserted where it matters, on the rendered text rather than on the
record. `ModelCatalogTests.EveryCatalogueEntryHasAnAttributionRegistered` asserts separately that
every model names an attribution that actually resolves.

The `required` properties are the other half: they hold the package together at construction, so a
notice cannot be built with five elements in the first place.

## Two restrictions that constrain the product, not just the paperwork

**§2(a)(5)(B) forbids effective technological measures.** No encrypted model blobs, no
licence-locked weights, no DRM wrapper. There is no code path here that could apply one, and adding
one would be a licence breach rather than a feature.

**§2(b) withholds patent and trademark rights.** Nothing may imply NVIDIA endorsement or
sponsorship. The product is "Uindosill", it says it uses NVIDIA Parakeet weights under CC BY 4.0, and
it does not use NVIDIA branding.

## The diarisation weights are NOT CC BY 4.0 — they are the NVIDIA Open Model License

Read in full at NVIDIA's own URL on 2026-08-19, version **dated 24 October 2025**, the same way the
CUDA EULA below was read. `soniqo/Sortformer-Diarization-4spk-ONNX` and the
`nvidia/diar_streaming_sortformer_4spk-v2.1` it is exported from both declare `license: other`,
`license_name: nvidia-open-model-license`. **Neither HuggingFace repository ships a LICENSE file** —
the licence exists there only as frontmatter metadata — and soniqo's declared `license_link` is a
404, so NVIDIA's canonical URL is what was read and what is cited.

**It clears, and redistribution is permitted outright.** §3: *"You may reproduce and distribute
copies of the Model or Derivative Models thereof in any medium, with or without modifications,
provided that You meet the following conditions"*. There is no field-of-use restriction, no
non-commercial clause and no acceptable-use list. The preamble states the intent: *"Models are
commercially usable."*

**The conditions are two, and one of them is a file.** §3.1: *"If you distribute the Model, You must
give any other recipients of the Model a copy of this Agreement and include the following attribution
notice within a "Notice" text file with such copies: "Licensed by NVIDIA Corporation under the NVIDIA
Open Model License""*.

- The sentence is **mandated verbatim**. `OpenModelLicenceAttribution.RequiredNotice` holds it, and
  the renderer emits it on its own line without a prefix — every other field is labelled
  (`Source:`, `Provenance:`), and prefixing this one would stop it being the required string. A test
  asserts it appears character for character on a line of its own.
- The Agreement is **a copy, not a link**. It ships at
  `licences/NVIDIA-Open-Model-License-2025-10-24.txt`; `build/Licences.targets` copies the directory
  into every build output; `scripts/package-windows.ps1` refuses to pack a publish without it; and a
  test resolves the path the notice prints and reads the mandated sentence out of the file it names.
  A notice pointing at a file that is not there is worse than no notice.

**This project does not host the weights.** The installer fetches them from soniqo's URL, pinned to
revision `db3a7b54` rather than `main` because it is a single-maintainer third-party repository. On
the plain text, §3.1's *"If you distribute the Model"* is not triggered by linking, and §2.2's
*"(through multiple tiers of distribution)"* shows the drafters contemplated distribution chains
without imposing anything extra downstream. **That is a reading, not something the text settles** —
the Agreement contains no clause distinguishing hosting from linking — so the notice and the copy
ship regardless, which is the same posture this project takes on CC BY.

**Three ways it is stricter than CC BY 4.0, and all three are recorded rather than absorbed.**

1. **The grant is revocable** (§2.1), where CC BY 4.0 is irrevocable. NVIDIA may also update the
   Agreement for legal or regulatory reasons, and *"You agree to either comply with any updated
   license or cease Your copying, use, and distribution."* A shipping product whose diariser can be
   withdrawn is a different risk from one whose ASR weights cannot.
2. **It terminates automatically** on filing patent or copyright litigation against anyone over the
   model, and on bypassing *"any technical limitation, safety guardrail ... encryption, security,
   digital rights management, or authentication mechanism"* in it without a substantially similar
   replacement.
3. **§2.3 incorporates NVIDIA's Trustworthy AI terms** (last modified 27 June 2024), which forbid use
   *"in violation of applicable law or regulation"* and name *"illegal collection or processing of
   biometric information without the consent of the subject where required under applicable law"*.
   **Speaker diarisation is voice biometrics**, so this is the one clause here that is about what the
   product does rather than what it prints. It is in `Attributions.WeightUsageRestrictions` beside the
   DRM and endorsement clauses, so both notice surfaces render it.

**What it does not claim.** §2.4: *"NVIDIA claims no ownership rights in outputs. You are responsible
for outputs and their subsequent uses."* And §1: *"An output is not a Derivative Model."* So a
transcript's speaker labels are the user's.

**The export is a third party's, and that changes nothing.** soniqo's ONNX export is a Model
Derivative under §1 — it composes the pre-encoder and head into one graph and traces it at static
shapes. §3 applies identical conditions to *"the Model or Derivative Models thereof"*, and §3.3 would
have let soniqo impose different terms but soniqo did not: the export declares the same licence. One
Agreement to satisfy, NVIDIA's, and the §3.1 notice names NVIDIA rather than soniqo.

**Why the notice record grew a second shape.** `Attributions.ById` was a dictionary of
`CcByAttribution`, whose nine required properties are the seven §3(a) elements plus a title.
Rendering an NVIDIA Open Model License entry through it would have printed headings — *Modifications*,
*Warranties* in CC BY's own words — that this licence never asked for, which is a false notice in
front of a user and the exact failure `models.json`'s own comment about the deferred Nemotron entries
warns against. So `IModelAttribution` was extracted and `OpenModelLicenceAttribution` sits beside
`CcByAttribution`: each licence gets a record shaped like its own obligations, and the two rendering
surfaces depend only on the interface.

**Two things still unverified**, stated as such. No archived copy of the Agreement pinned to the
export's own date (2 August 2026) was consulted — the text read is the current one, which §2.1's
unilateral-update clause arguably makes the operative one anyway. And soniqo's authority to publish
the export was not verified, only that the licence permits Model Derivatives and that soniqo declares
the same terms. As with the CUDA analysis below: **no lawyer has read any of this.**

## ONNX Runtime is MIT, and carries 69 licences that are not

The diariser runs on `onnxruntime.dll` from `Microsoft.ML.OnnxRuntime` 1.29.0 — the same source
commit as the Python `onnxruntime` 1.29 the spike measured on, so the graph the product runs is the
graph that was scored. The package itself is MIT, *Copyright (c) Microsoft Corporation*, and MIT
requires the copyright notice **and the permission text** to travel with the binary, not the licence
name. `licences/onnxruntime-LICENSE.txt` is that file, copied out of the restored package rather than
from the repository, since a package and its repository can disagree.

It also statically links 69 third-party components — Intel MKL, protobuf, Eigen, oneDNN, abseil,
XNNPACK, mimalloc and the rest — whose own notices are in a 343 KB `ThirdPartyNotices.txt`. That file
is **redistributed verbatim** at `licences/onnxruntime-ThirdPartyNotices.txt` rather than summarised
into the component table. Summarising it would mean transcribing 69 licences by hand, and getting one
wrong is the same breach as omitting it.

## Display it in the application

The notice has to be present where the material is used, not only in a file in the source
repository. It is in the **Licences** tab of the app and in `uindosill notice`, and a headless UI
test (`LicenceTabCarriesTheFullNoticeInsideTheApplication`) asserts the text the view model renders
carries it. That test checks a representative six strings rather than all seven elements; the
element-by-element assertion is the one above, on the shared renderer both surfaces call.

## Dependencies

parakeet.cpp MIT, ggml MIT, Avalonia MIT, NAudio MIT, CommunityToolkit.Mvvm MIT, Velopack MIT.
Listed in `NOTICE.md` and rendered in the same panel.

Velopack is the newest and the only one that is not in every artefact: it builds the installer and
performs the update check, so it ships in the desktop application and not in the CLI zip. Its
copyright line travels with it — `Attributions.Components` carries
*Copyright (c) Velopack Ltd. All rights reserved.* in the Notes field, which both surfaces render,
because MIT requires the notice and not merely the licence name. The licence was read off the
restored package's own `velopack.nuspec` (`<license type="expression">MIT`) rather than the
repository, since a package and its repository can disagree about what was actually shipped.

## The CUDA runtime is proprietary, and it is redistributable

The three files the CUDA backend needs beside `parakeet.dll` — `cudart64_12.dll`,
`cublas64_12.dll` and `cublasLt64_12.dll`, 807 MB of the 931 MB CUDA drop — are **NVIDIA
proprietary binaries, not MIT**. They arrive in `cudart-parakeet-bin-win-cuda-x64.zip`, which ships
**no licence text of any kind**, so nothing in the vendored drop states its own terms. That is why
this section exists rather than a `LICENSE` file doing the work, as it does for the MIT backends.

**Read against the EULA on 2026-08-15**, at https://docs.nvidia.com/cuda/eula/index.html:

- **§2.6, Attachment A** lists `cudart`, `cublas` and `cublasLt` among the files that "may be
  distributed with applications developed by you", and covers version-numbered variants of those
  names explicitly — its own example is `cudart64_90.dll`, which is the same shape as the
  `cudart64_12.dll` here.
- **§1.1.2** attaches the conditions: the application "must have material additional functionality,
  beyond the included portions of the SDK"; "the distributable portions of the SDK shall only be
  accessed by your application"; and the terms under which they are distributed must be consistent
  with the EULA.
- **§1.2** forbids distributing the SDK "as a stand-alone product", and forbids indicating that an
  application built with the SDK "is sponsored or endorsed by NVIDIA" absent an agreement.

**How this product stands against each.** Uindosill is a transcription application whose
functionality is overwhelmingly its own, so the material-additional-functionality condition is met
on any ordinary reading. The three DLLs are loaded in-process by `parakeet.dll` and reached by
nothing else — they sit in `native/win-x64/cuda/` and no code path exposes them as a general CUDA
facility. They are shipped as part of the application, never on their own. And the no-endorsement
condition is already a standing constraint here for a different reason: CC BY 4.0 §2(b) imposes the
same thing on the weights, and the product carries no NVIDIA branding.

**One clause is quoted more often than it applies.** The EULA's
`"This software contains source code provided by NVIDIA Corporation."` notice is scoped, in the text,
to "modifications and derivative works of sample source code distributed". This project distributes
no CUDA sample source code and no derivative of any, so on the text as read that notice is not
triggered — and it is recorded here because reaching for it reflexively would put a claim in the
notice package that does not describe what is shipped.

**What is unverified, and it is the part that matters.**

- **No lawyer has read any of this.** The above is a careful reading of a licence by the people
  building the thing, which is the same standard the rest of this repository holds itself to and is
  not the same as advice.
- **The text read was the current online EULA**, not a copy pinned to the CUDA 12.8 toolkit these
  binaries came from (`cudart64_12.dll` reports `CUDART_VERSION` 12080 — see
  `docs/NATIVE-BINARIES.md`). NVIDIA revises this document, and no archived copy of the version
  contemporaneous with that toolkit was consulted.
- **The archives came from `mudler/parakeet.cpp`'s release, not from NVIDIA.** Whether that upstream
  redistribution is itself compliant is not this project's determination to make, but it is the
  provenance of every byte vendored here.

**What this changes about shipping.** The notice gap is closed: `Attributions.Components` carries
the entry, so `uindosill notice` and the app's Licences tab both render it, and a test asserts the
entry survives. Nothing found in the EULA requires the licence *text* to travel beside the binaries
the way MIT requires for the other backends, so no file is dropped into `native/win-x64/cuda/`. If a
later reading finds such a requirement, that is a `build/NativeAssets.targets` change and the glob
already has the shape for it.

## The evaluation corpus is someone else's, and it does not ship

`scripts/measure-wer.ps1` scores the models against Rev.com's **Earnings-22 Subset 10** — ten
earnings calls and their human transcripts, pinned by digest in `scripts/wer-corpus.json` and
fetched from `github.com/revdotcom/speech-datasets` at one commit. Its `LICENSE.md` puts *"the
transcripts and associated text files that are used for alignment"* under **CC BY-SA 4.0**; it says
nothing about the recordings, which Rev publishes in the same repository as an ASR benchmark, so
their terms are **not stated** and this project does not claim to know them. What this project does
with the corpus is bounded accordingly: it is downloaded to the machine that runs the harness, into
a directory that is gitignored; nothing from it is committed, packaged or served; the numbers
computed from it are published with the citation the dataset asks for (in the manifest). The
share-alike clause attaches to the transcripts and would attach to a redistribution of them, which
never happens here.

## Deliberately not used: TEN-VAD

Its modified Apache-2.0 carries an Agora non-compete clause. Voice activity detection here is a plain
energy gate written for this project. If you ever reach for an off-the-shelf VAD, read its licence
first — this is a category where the popular choice has a rider on it.

## Language claims are a licensing-adjacent honesty problem

`parakeet-tdt-0.6b-v3` covers 25 European languages and does not cover Chinese, Japanese, Korean,
Arabic, Hindi or Thai. Advertising them would be false rather than merely optimistic. A test asserts
no catalogue entry claims those tags.
