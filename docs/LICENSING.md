# Licensing obligations

The code is MIT. The weights are not, and their obligations are the ones worth reading twice.

## The model weights are CC BY 4.0 (NVIDIA)

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

## Display it in the application

The notice has to be present where the material is used, not only in a file in the source
repository. It is in the **Licences** tab of the app and in `uindosill notice`, and a headless UI
test (`LicenceTabCarriesTheFullNoticeInsideTheApplication`) asserts the text the view model renders
carries it. That test checks a representative six strings rather than all seven elements; the
element-by-element assertion is the one above, on the shared renderer both surfaces call.

## Dependencies

parakeet.cpp MIT, ggml MIT, Avalonia MIT, NAudio MIT, CommunityToolkit.Mvvm MIT. Listed in
`NOTICE.md` and rendered in the same panel.

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
