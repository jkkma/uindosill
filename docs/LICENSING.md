# Licensing obligations

The code is MIT — **except that a build carrying the video player is distributed under the GPL**, which is the first section below. The weights are neither, and their obligations are the ones worth reading twice.

**Three model licences ship, not one.** The transcription weights are CC BY 4.0 and want a
seven-element notice package. The speaker diarisation weights are under the NVIDIA Open Model
License and want one verbatim sentence plus a copy of the agreement — and, unlike CC BY, they are
revocable and carry a use restriction about biometrics. The translation weights are Apache-2.0 and
want a copy of the licence, a statement of what was changed, and the notices the source carried.
Which entry has which licence is asserted by a test, so adding a fourth is a deliberate act rather
than a drift.

**And from 2026-08-21 the installer is built to carry a Python — though none has been built yet.**
The diariser and the translator moved out of C# and into a bundled interpreter, and
`scripts/bundle-python.ps1` assembles one and `scripts/package-windows.ps1` puts it in the publish;
what has not happened is a packaging run. The obligation arrives with the decision rather than with
the first release, because it turns fifty third-party wheels and a CPython from things this project
*depends on* into things it *redistributes*. That is a different obligation and it is
not discharged; the section below is the record of what will be owed, not a claim that anything has
been done about it.

## The video player is GPL, and it takes the distribution with it

**Decided 2026-08-23, and it is the only licence decision here that changes what `LICENSE` says.**
The Ask tab plays video through libmpv, which is GPLv2-or-later and links FFmpeg-GPL and other GPL
libraries. Putting that binary in the same distribution as this application makes the combined work
a GPL distribution. The alternatives were weighed — see `docs/PHASES.md` § *Decided 2026-08-23* —
and this one was taken deliberately.

**What changed, precisely.** The source code in this repository is still MIT and nothing revokes
that; a recipient of a GPL build may take the Uindosill source under either set of terms. What
cannot be separated from the GPL is the *combination* with libmpv. So:

- `LICENSE` states both, and which governs which kind of copy.
- A build that has **not** vendored libmpv contains no GPL component and is MIT throughout. This is
  not a theoretical case: `MediaPlayers.ForThisBuild()` picks the audio-only player when the library
  is absent, and the Licences tab lists libmpv only when it is present, so a reader can tell which
  kind of copy they have by looking.
- The GPL notices travel with the binary rather than with the repository.

**Three files ship beside `libmpv-2.dll`, and `scripts/vendor-mpv.ps1` refuses to finish without
them.** That refusal is the point: a missing licence notice is a breach that fails silently, which
is the same reasoning that put the parakeet.cpp `LICENSE` check in `vendor-natives.ps1`.

1. **`GPL-2.0.txt`** — the licence text. GPLv2 §1 requires it to accompany the binary.
2. **`mpv-Copyright.txt`** — mpv's own licensing summary at the pinned commit, which is what
   distinguishes the GPL-only parts from the LGPL-able ones and says that "None of the cases listed
   above affect the final binary if it's built as LGPL. Linked libraries still can affect the final
   license (for example if FFmpeg was built as GPL)" — which, in this build, it was.
3. **`mpv-WRITTEN-OFFER.txt`** — how §3 is satisfied. It names the exact revisions of everything
   GPL in the distribution: the shinchiro build recipe (release `20260814`, asset digest recorded),
   mpv commit `7b8915bc1d`, and FFmpeg's upstream. All of it is public and reachable from the same
   place as the binaries, which is §3(a); the three-year written offer of §3(b) is there as well,
   because belt and braces costs a paragraph.

**The upstream archive carries no licence text at all** — only `libmpv-2.dll` and the headers — so
all three come from this repository's `licences/` directory. `build/NativeAssets.targets` copies
`native/**/*.txt` into the build output for exactly this reason, alongside the `LICENSE` glob that
carries parakeet.cpp's.

**What is not GPL, and the GPL does not claim it.** The model weights are separately licensed
(CC BY 4.0, the NVIDIA Open Model License, Apache-2.0), they are data this application reads rather
than works derived from it, and they are downloaded by the user from the model provider rather than
distributed inside the application. The permissive components — parakeet.cpp, ONNX Runtime,
Avalonia, NAudio, CommunityToolkit.Mvvm, Velopack, the bundled CPython and its wheels — are all
GPL-compatible, which was checked rather than assumed: MIT, BSD and Apache-2.0 are compatible with
GPLv2-or-later (Apache-2.0 with GPLv3 but not GPLv2, which is why the licence is "or later" rather
than "version 2" — the combination resolves at GPLv3 where an Apache component is present).

**Two things about this are not settled, and are marked rather than tidied away.**

- **Nobody with a professional opinion has read any of it.** The reasoning above is a careful
  reading of GPLv2 by the people writing the code, which is the same standing as every other
  licence reading in this file, and it is worth saying plainly rather than implying otherwise.
- **An LGPL libmpv would avoid the question entirely and does not exist as a prebuilt binary.**
  mpv builds LGPL with `-Dgpl=false` against an LGPL FFmpeg; no Windows binary of that shape is
  published (checked 2026-08-23 against shinchiro's releases and the SourceForge mpv-player-windows
  builds). Producing one means owning a cross-compilation toolchain, which is a larger commitment
  than pinning a file. If one appears, the GPL obligation goes away and this section shrinks to a
  paragraph.

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

## The translation weights are Apache-2.0 (Helsinki-NLP), and this project is the one that modified them

The third licence, and the third shape. Section 4 of the Apache License 2.0 attaches four conditions
to redistribution: **(a)** give recipients a copy of the License, **(b)** carry prominent notices
saying the files were changed, **(c)** retain the copyright, patent, trademark and attribution
notices found in the source form, and **(d)** reproduce the attribution notices of any `NOTICE` file
the work ships.

**(a) is a copy, not a link** — the same as the NVIDIA agreement and unlike CC BY — so
`licences/Apache-License-2.0.txt` ships. It was not transcribed from memory: it was extracted verbatim from
`licences/onnxruntime-ThirdPartyNotices.txt`, which this repository already redistributes and which
already carried nineteen copies of the text, and it is 11,357 bytes over 201 lines. Nothing here
has compared those bytes against apache.org — the source is a file already in the tree, which is a
weaker check than fetching the canonical one and is stated as such.

**(b) is about this project's work rather than somebody else's**, and that is what makes this entry
different from the other two. Uindosill does not redistribute Helsinki-NLP's checkpoint. It exports
it — `scripts/export-translation-onnx.py`, against revision `bb1ef830d5`, into two ONNX graphs in
the merged decoder layout with past key values exposed — and redistributes the result. The weights
themselves are unchanged and unquantised, float32 in and float32 out; what changed is the container
and the graph split. `ApacheAttribution.ModificationNotice` says exactly that.

**(c) and (d) were the two outstanding items, and they were closed on 2026-08-20 by reading the
upstream tree rather than by reasoning about it.** The check was done before the weights were
uploaded, which is where it belongs: uploading to Hugging Face **is** redistribution, and §4's
conditions attach to redistribution rather than to the existence of a catalogue entry.

What was read, so a later revision is a visible change rather than a silent one: the file listing
from `huggingface.co/api/models/Helsinki-NLP/opus-mt-tc-bible-big-mul-deu_eng_nld?blobs=true`, whose
`sha` came back as `bb1ef830d540449c89c7ee5b9ea5b1fc666db3d5` — **the same revision the export ran
against**, so the listing is the pinned revision and not merely `main` — and then every text file in
it fetched at that revision: `README.md`, `config.json`, `generation_config.json`,
`tokenizer_config.json`, `special_tokens_map.json`, `benchmark_results.txt` and `.gitattributes`.

**§4(d) is inapplicable: there is no `NOTICE` file.** It is absent from the listing, and `NOTICE`,
`NOTICE.txt` and `NOTICE.md` were each requested at that revision and each returned 404. So did
`LICENSE`, `LICENSE.txt` and `COPYING` — the repository declares `apache-2.0` in card metadata and
ships no copy of it, which is why the copy this project ships under §4(a) came from elsewhere.

**§4(c) splits in two, and both halves are now recorded.** There is **no copyright, patent or
trademark notice** in the repository: a case-insensitive search for `copyright`, `(c)`, `©`,
`patent`, `trademark` and `all rights reserved` across all seven text files returns nothing. The
only occurrences of those words anywhere in the tree are inside `source.spm` and `target.spm`, where
`▁Copyright`, `▁copyright`, `▁trademark` and `▁Helsinki` are SentencePiece **vocabulary pieces** —
each preceded by U+2581 and wrapped in the protobuf framing of a `ModelProto` piece, which is a token
in a subword inventory and not a notice. Nothing is reproduced from them, and nothing is invented to
fill the gap: a copyright line nobody published is a false notice in front of a user, which is the
failure `models.json`'s own comment about the deferred entries refuses.

**But §4(c) says "copyright, patent, trademark, and attribution notices", and the attribution
notices are real.** The card carries four, and all four are now retained rather than summarised: the
developer line and the OPUS-MT/Marian/OPUS provenance; the original model archive's URL on
`object.pouta.csc.fi`; the citation request naming three publications, which the card states as
*"Please, cite if you use this model"*; and the Acknowledgements paragraph crediting the HPLT project
under EU Horizon Europe grant agreement No 101070350, CSC — IT Center for Science, and the EuroHPC
supercomputer LUMI. They live in `ApacheAttribution.RetainedSourceNotices`, in `NOTICE.md`, and in the
`README.md` of the Hugging Face repository the export is published to, so every surface a recipient
can reach carries them.

**The negative finding is a field rather than a silence.** `ApacheAttribution.SourceNoticeFinding` is
`required`, like every other element in this file, and it states the revision read, the date, and
that there is no NOTICE file and no copyright notice. A notice that omits a NOTICE file and one that
records there is none read identically to anyone downstream; only the second says the check was
performed. Two tests hold it up — one asserting all four §4 conditions reach the rendered notice, one
asserting the notice invents no copyright line and that the word appears only in the finding that
says there is none.

The narrow reading — that §4(c) covers only notices in the *files*, and a model card is not a file of
the Work — would have discharged this with nothing retained. It was not taken. Retaining what the
source asks to travel with it costs four paragraphs, and the cost of being wrong the other way is a
licence breach.

One thing that is settled: the licence itself. `docs/PHASES.md` records apache-2.0 for this
checkpoint, read off its model card on 2026-08-19 when the route was chosen, and that is the record
this entry rests on rather than a fresh reading.

As everywhere else here: **no lawyer has read any of this.**

## ONNX Runtime is MIT, and carries 69 licences that are not

**It ships twice.** Until 2026-08-21 the diariser ran `onnxruntime.dll` from
`Microsoft.ML.OnnxRuntime` 1.29.0; it now runs the Python `onnxruntime-webgpu` **1.27.0** wheel
inside the bundled interpreter, and so does the translator. Between that day and 2026-08-23 nothing
in `Uindosill.slnx` referenced ONNX Runtime — the two projects that did are in `attic/` — and since
2026-08-23 one project does again: `Parakeet.Engine.SileroVad`, which runs the speech-detection
graph in process on the .NET package, 1.29.0, beside the managed assemblies. Two binaries in two
processes, one obligation each. Either way the package is MIT,
*Copyright (c) Microsoft Corporation*, and MIT requires the copyright notice **and the permission
text** to travel with the binary, not the licence name.

`licences/onnxruntime-LICENSE.txt` is that file, copied out of the restored .NET package rather than
from the repository, since a package and its repository can disagree — and it is still the right
text: the wheel's own `LICENSE` is byte-identical to it, compared on this machine 2026-08-21.

**Its neighbour is where the version change shows.** ONNX Runtime statically links third-party
components — Intel MKL, protobuf, Eigen, oneDNN, abseil, XNNPACK, mimalloc and the rest — whose own
notices are in one file. `licences/onnxruntime-ThirdPartyNotices.txt` is the 1.29.0 package's, 343 KB
and 69 notice blocks; the 1.27.0 wheel ships its own at 331 KB and 67, inside its package directory.
The two carry the same fifty named components, and the difference is exactly two blocks the older
file has and the newer does not — **Mbed TLS** and `microsoft/cpp_client_telemetry`. So the file the
build copies into every publish **over-discloses rather than under-discloses**, which is the safe
direction to be wrong in and is still not the shipped binary's own notice.

That file is **redistributed verbatim** rather than summarised into the component table. Summarising
it would mean transcribing dozens of licences by hand, and getting one wrong is the same breach as
omitting it.

## The speech-detection graph is MIT (Silero), and the first MIT model here

`silero-vad-v5.1.2` — `silero_vad.onnx`, 2,327,524 bytes — is the model behind the neural speech
detection added 2026-08-23 (`--vad neural` on the command line; on by default in the app once the
model is installed), installed by URL from `snakers4/silero-vad` at commit
`6478567951ae5c9979ad7b234185b5515f4be7a1` (tag v5.1.2) and pinned by SHA-256. Its `LICENSE` at that
commit is the MIT License, *Copyright (c) 2020-present Silero Team*, fetched the same day and
shipped byte for byte as `licences/silero-vad-LICENSE.txt`.

**What MIT asks is narrower than CC BY's seven elements and wider than a licence name:** "the above
copyright notice and this permission notice shall be included in all copies or substantial portions
of the Software." So two things travel with the graph — the copyright line and the permission text —
and both do: `Licences.targets` copies the file into every build output, `package-windows.ps1`
refuses a publish without it, and `Attributions.ById["silero-vad"]` is an `MitAttribution`, a record
that cannot be constructed without the copyright line and the path to the text. `uindosill notice`
and the Licences tab render it with the other three.

**The graph is unmodified and is not hosted here.** It is installed from upstream's own repository
at a pinned commit, which is the arrangement the diariser and the translation weights already use;
what this project adds is the C# that drives it, which is this project's own MIT code.

**It runs on a second copy of ONNX Runtime — the .NET one.** See the section above: since 2026-08-23
`Microsoft.ML.OnnxRuntime` 1.29.0 is a live reference again (`Parakeet.Engine.SileroVad`), beside
the `onnxruntime-webgpu` 1.27.0 wheel inside the bundled Python. The committed
`licences/onnxruntime-LICENSE.txt` and `ThirdPartyNotices.txt` are that .NET package's own, so for
this copy they are exactly the right files; the reconciliation against the wheel's notices is still
the open item it was.

**What is not claimed.** No lawyer has read this either. Upstream's repository root was read at the
pinned commit for a NOTICE file and carries none, so there is nothing under that heading to
reproduce; the MIT text is the whole of the obligation as far as it was read.

## The bundled Python is fifty more redistributions, and none of them is discharged

**Depending on a package and shipping it are different obligations, and this is the change that
crosses from one to the other.** `PythonRuntime.Resolve` looks for `<app>/python/python.exe` — an
interpreter beside the application, so a user installs nothing and no system Python is consulted.
What that interpreter is made of is now something this project hands to people.

**The set is pinned and it is not small.** `python/requirements-bundle.txt` names nine top-level
packages and says why each version is the version it is; `scripts/bundle-python.ps1` unpacks a
pinned embeddable CPython and installs them into it. Resolved against the working venv on
2026-08-21 and then verified against an assembled bundle the same day, the transitive closure of
those nine is **fifty distributions**. The pins live in that file and are not repeated here.

**Every licence below was read off the installed `.dist-info/METADATA` on this machine**, not
recalled. Grouped by what the metadata actually says, with the four that are not simply permissive
held back for the paragraph after:

- **MIT** — charset-normalizer, filelock, narwhals, onnxruntime-webgpu, platformdirs, pyyaml,
  setuptools, urllib3; **MIT-0** — cffi.
- **BSD-3-Clause** — fsspec, idna, joblib, lazy-loader, markupsafe, networkx, pooch, protobuf,
  pycparser, scikit-learn, soundfile, threadpoolctl; **BSD-2-Clause** — decorator.
- **Apache-2.0** — flatbuffers, ml-dtypes, msgpack, onnx, optimum-onnx, requests, sentencepiece,
  transformers.
- **ISC** — librosa. **PSF-2.0** — typing-extensions.
- **Compound, where the expression is the licence** — numpy is *BSD-3-Clause AND 0BSD AND MIT AND
  Zlib AND CC0-1.0*; torch is *Apache-2.0 AND Apache-2.0 WITH LLVM-exception AND BSD-2-Clause AND
  BSD-3-Clause AND BSL-1.0 AND MIT*; llvmlite is *BSD-2-Clause AND Apache-2.0 WITH LLVM-exception*;
  regex is *Apache-2.0 AND CNRI-Python*; packaging is *Apache-2.0 OR BSD-2-Clause*.
- **A family without a version** — mpmath, numba and sympy say only "BSD"; huggingface-hub and
  optimum say only "Apache"; scipy's `License` field is a copyright line rather than an identifier.
  Their classifiers name the same family and nothing narrower. BSD-2 and BSD-3 differ by a clause
  and there is more than one Apache licence, so these are recorded as read rather than resolved.
- **No `License` field at all** — colorama, jinja2, safetensors and tokenizers. All that is known
  about them here is a trove classifier naming a family: BSD for the first two, Apache for the other
  two. **That is a weaker check than the rest of this file and is marked rather than tidied away.**

**Four of the fifty are not simply permissive, and they are the ones to read twice.**

1. **soxr is LGPL-2.1-or-later**, and its wheel bundles libsoxr (LGPL-2.1) and PFFFT. Nothing in
   this project imports it; librosa declares it, and librosa is here for one call.
2. **soundfile is BSD-3-Clause and its wheel is not.** `_soundfile_data/libsndfile_x64.dll` ships
   with a `COPYING` beside it that is the **LGPL-2.1**. A table that read the package metadata and
   stopped there would have recorded this one as BSD and missed it.
3. **certifi is MPL-2.0** — file-level copyleft, weaker than the LGPL and still not MIT.
4. **tqdm is MPL-2.0 AND MIT** — the same shape, and an `AND` rather than an `OR`: both apply.

The LGPL is the one with a shape this product has to think about: it attaches conditions about
relinking to a binary a recipient receives, and both of these arrive as prebuilt DLLs inside wheels.
**Whether shipping them in an installer satisfies those conditions has not been worked out here.**

**Most of the notice texts already travel, by accident of how wheels are built.** Forty-six of the
fifty carry a `LICENSE`, `COPYING` or `NOTICE` inside their `.dist-info`, and `pip install --target`
copies that directory, so those texts land in the bundle without anyone deciding they should.
`onnxruntime-webgpu` is a forty-seventh by another route — its texts are in the package directory
rather than the metadata. torch's wheel carries thirty-four third-party licence directories of its
own and ONNX Runtime's carries 331 KB in one file; **nobody here has read either.**

**Three ship no licence text anywhere: `flatbuffers`, `sentencepiece` and `tokenizers`.** All three
are Apache — the first two say so in their metadata and the third only by classifier — and
Apache-2.0 §4(a) wants a copy rather than a link, which is the same condition the translation weights
already put on this repository. Those three texts have to be supplied by hand or the bundle does not
meet them.

**The closure was checked against an assembled bundle, and it is fifty.** The worry was general and
sound — a set resolved from an existing virtual environment can differ from the one `pip` produces
against `python/requirements-bundle.txt`, and `hf-xet` was the named candidate, since
`huggingface-hub` declares it for x86_64 and amd64 without an extra. A bundle was assembled on
2026-08-21 by `scripts/bundle-python.ps1` and its `Lib/site-packages` enumerated: **exactly the fifty
above, and no `hf-xet`.** So the list is the shipped list rather than an approximation of it.

**What that check does not settle** is that it stays fifty. It is one resolution, on one day, on
Windows on x86-64, against an index that moves; `pip` is free to bring in a new transitive dependency
the next time this runs. Nothing re-enumerates the bundle automatically and nothing compares a fresh
enumeration against this list, so a fifty-first arriving silently is the failure mode that remains —
and `scripts/bundle-python.ps1` is where a check for it would go.

**CPython is the PSF License Agreement, and the Windows build adds a second party.** Version 2 of
the Agreement, read from the installed CPython 3.12.10 on this machine on 2026-08-21: §2 permits
redistribution *provided that* "PSF's License Agreement and PSF's notice of copyright ... are
retained", and §3's summary-of-changes obligation does not arise because nothing is modified. The
Windows binary build's `LICENSE.txt` then adds *Additional Conditions for this Windows binary build*
covering Microsoft Distributable Code linked into every `.exe`, `.dll` and `.pyd`, with four
restrictions: do not alter Microsoft's notices; do not use Microsoft's trademarks in a program's
name or in a way suggesting endorsement; do not distribute the code to run on a non-Microsoft
platform; do not put it in malicious or deceptive programs. The first two are the same shape as the
NVIDIA endorsement clauses above, and this product already meets them for the same reason.

`bundle-python.ps1` unpacks the embeddable zip whole and deletes nothing from it, so whatever licence
text the archive carries arrives in `<app>/python` on its own. **That satisfies §2 by accident**, and
a later step that trims the bundle for size would break it silently. Two things are unverified: the
text read was the *installer* build's `LICENSE.txt` for that version rather than the embeddable
zip's own, and the embeddable zip is the one that ships.

**The vendored NeMo is the one part of the bundle whose obligation is already written down.**
`python/uindosill_engines/_vendor/nemo/` holds two of NVIDIA's Apache-2.0 files and thirteen of this
project's own; `NOTICE.md` carries the entry and the §4 check against it, and neither is repeated
here.

**Nothing above is discharged.** No notice package has been assembled for any of it, no audit has
been run, and `uindosill notice` and the Licences tab say nothing about the Python. This section is
the record of **what will be owed when an installer carries one** — written before the bundle is
built rather than after, which is where the Marian §4(c) check was done and for the same reason.

As everywhere else here: **no lawyer has read any of this.**

## The C# engines moved to `attic/`, and their obligations did not

`Parakeet.Engine.Sortformer` and `Parakeet.Engine.Marian` — the C# diariser and translator — left
`src/` on 2026-08-21 for an unbuilt `attic/`. They are not in `Uindosill.slnx`, nothing references
them, and **nothing ships them**, which is the whole of what changed: they are still files in a
public repository under this project's MIT licence. Their two `PackageReference`s to
`Microsoft.ML.OnnxRuntime` were the last ones in the tree until 2026-08-23, when
`Parakeet.Engine.SileroVad` took a live one for the speech-detection graph — the section above.

The weights sections above are untouched by the move, and that is the point worth stating. The same
CC BY 4.0, NVIDIA Open Model License and Apache-2.0 material is used for the same purpose against
the same conditions. **What changed is which process loads it, not who the licensee is or what the
licence asks for.**

## Display it in the application

The notice has to be present where the material is used, not only in a file in the source
repository. It is in the **Licences** tab of the app and in `uindosill notice`, and a headless UI
test (`LicenceTabCarriesTheFullNoticeInsideTheApplication`) asserts the text the view model renders
carries it. That test checks a representative six strings rather than all seven elements; the
element-by-element assertion is the one above, on the shared renderer both surfaces call.

## Dependencies

parakeet.cpp MIT, ggml MIT, Avalonia MIT, NAudio MIT, CommunityToolkit.Mvvm MIT, Velopack MIT, and
the two typefaces the window is drawn in — Instrument Sans and Chivo Mono — under **OFL-1.1**.
Listed in `NOTICE.md` and rendered in the same panel.

The OFL is the one licence here with a condition about the *name*: §5 forbids redistributing a
modified font under its reserved name, which is why both faces ship whole and unmodified rather than
subsetted to the few hundred glyphs the interface uses. Its copyright notice and licence must travel
with every copy, so `licences/InstrumentSans-OFL.txt` and `licences/ChivoMono-OFL.txt` do; the CLI
zip carries neither font, having no window to draw.

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
energy gate written for this project and, since 2026-08-23 — on request on the command line, by
default in the app once its model is installed — Silero VAD, which is
MIT and has its own section above; its licence was read before the graph was. If you ever reach for
an off-the-shelf VAD, read its licence first — this is a category where the popular choice has a
rider on it.

## Language claims are a licensing-adjacent honesty problem

`parakeet-tdt-0.6b-v3` covers 25 European languages and does not cover Chinese, Japanese, Korean,
Arabic, Hindi or Thai. Advertising them would be false rather than merely optimistic. A test asserts
no catalogue entry claims those tags.
