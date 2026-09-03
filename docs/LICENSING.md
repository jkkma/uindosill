# Licensing obligations

The code is MIT — **except that a build carrying the video player is distributed under the GPL**, which is the first section below. The weights are neither, and their obligations are the ones worth reading twice.

**Three model licences ship, not one** — five until 2026-08-27, when the only non-commercial one
left with DiariZen in the morning and the NVIDIA Open Model License left with Sortformer that
afternoon. **The transcription and speaker diarisation weights are both CC BY 4.0** and want a
seven-element notice package each. Nothing in this product is revocable, and no licence here
imposes a use restriction — the biometrics caution in `NOTICE.md` is now this project's own
statement rather than a term of anyone's grant, kept because separating people by their voices is
voice biometrics whichever model does it. The translation weights are Apache-2.0 and
want a copy of the licence, a statement of what was changed, and the notices the source carried.
The speech-detection weights are MIT and want the copyright line and the permission notice, which
is the whole of it — the fourth licence, added with Silero VAD on 2026-08-23. Which entry has which
licence is asserted by a test, so a fifth was always going to be a deliberate act rather than a
drift, and it was: **the second diariser's weights were CC BY-NC 4.0**, added with DiariZen on
2026-08-26, and they were the first here to restrict *use* rather than only paperwork.

**That licence left the product on 2026-08-27, and nothing replaced it.** DiariZen was shelved to
`attic/diarizen/` and `pyannote/speaker-diarization-community-1` took the second diariser's place
under **CC BY 4.0** — attribution only, commercial use permitted. So this product now has **no
non-commercial component at all**, rather than one it was careful not to bundle, and the test that
used to assert "any NC entry is unbundled" asserts the stronger thing: that the set is empty. That
entry's own section below is the record, and it keeps the NC history because a licence that was
once carried is worth being able to find again.

**One of the four is distributed rather than downloaded, which changes who owes the notice.**
Since the installer began carrying weights on 2026-08-23 every channel ships the speech-detection
graph, which is 2.2 MiB and would otherwise be a dead checkbox on a fresh install. Everything else
is fetched by the user on request. A downloaded weight is the user's copy; a bundled one is this
project redistributing someone else's model, so it is the **MIT** obligation — Silero's copyright
line and permission notice — that attaches to the build. The other four attach to the download.

**The diarisation weights stopped being bundled on 2026-08-26, and the obligation they carried ended
outright on 2026-08-27.** Until the first date the default channel carried Sortformer, so this
project was redistributing NVIDIA Open Model License material and owed §3.1's verbatim notice and a
copy of the Agreement *with every build*; unbundling moved that obligation to the user who fetched
the file, and the notice and Agreement kept shipping anyway, because a revocable grant is one this
project would rather over-notice than under-notice. The next day those weights were retired
altogether — `attic/sortformer/` — and **no model in this product is under that Agreement now**, so
the copy that used to ship at `licences/` went with them. The reason for the original unbundling was
not licensing: speaker labelling had two models and neither was better, so bundling one would have
made it the answer on every fresh install
by default rather than by choice. `docs/PHASES.md` § *Decided 2026-08-26* records it.

**And since 2026-08-23 an installer that carries a Python has shipped, so this obligation is live
rather than pending.** The diariser and the translator moved out of C# and into a bundled
interpreter on 2026-08-21; `scripts/bundle-python.ps1` assembles one and
`scripts/package-windows.ps1` puts it in the publish. What was outstanding when this paragraph was
first written — a packaging run — happened: `v1.0.0-rc.3` was published on 2026-08-23 as the first
release packed with the bundle, which turned fifty third-party wheels and a CPython from things this
project *depends on* into things it *redistributes*. **The obligation is not discharged.** The
section below is the record of what is owed, and it is now owed against a release that exists
rather than against a decision — a stronger claim on this project than the one this paragraph used
to make. `NOTICE.md` says the same in its own words and does not pretend otherwise either.

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
  is absent, and the About window's Licences pane lists libmpv only when it is present, so a reader can tell which
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
CLI (`uindosill notice`) and the application's About window call it, so the two cannot drift.
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

## The diarisation weights were NOT CC BY 4.0 — retired 2026-08-27, and the reading is kept

**Nothing in this product is under the NVIDIA Open Model License any more.** The Sortformer weights
this section reads were shelved to `attic/sortformer/` on 2026-08-27, and the Agreement copy §3.1
required went with them. The section stays because the reading took work and is the thing a future
NVIDIA checkpoint would need — `OpenModelLicenceAttribution` is kept unused in the code for the same
reason — and because two of its findings outlived the licence: the biometric caution, which is now
carried on this project's own authority in `NOTICE.md`, and the revocability comparison, which is why
CC BY 4.0 is the bar a replacement is held to. Read it in the past tense.

### What was read

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
- The Agreement was **a copy, not a link**. It shipped from `licences/`, `build/Licences.targets`
  copied the directory into every build output, `scripts/package-windows.ps1` refused to pack a
  publish without it, and a test resolved the path the notice printed and read the mandated sentence
  out of the file it named. **All four went on 2026-08-27**, when the weights that owed the copy were
  retired; the text is kept at `attic/sortformer/NVIDIA-Open-Model-License-2025-10-24.txt` and ships
  nowhere.
  A notice pointing at a file that is not there is worse than no notice.

**This project distributes the weights, as of 2026-08-23.** Until then it did not: the application
fetched them from soniqo's URL, pinned to revision `db3a7b54` rather than `main` because it is a
single-maintainer third-party repository, and the question of whether linking counted as
distributing was left as an explicit reading rather than a settled one. **Bundling the file inside
the installer removes the question rather than answering it** — §3.1's *"If you distribute the
Model"* is now plainly triggered, and both of its conditions were already being met by machinery
that exists:

- The **copy of the Agreement** shipped from `licences/`, in every build output, and
  `scripts/package-windows.ps1` refused to pack a publish without it.
- The **verbatim attribution notice** was emitted by `OpenModelLicenceAttribution.RequiredNotice`,
  on its own line, asserted character for character by a test.

Both were removed on 2026-08-27 with the weights that owed them. `OpenModelLicenceAttribution`
remains in the code, constructed by nothing, for the reason given at the top of this section.

So the posture that was adopted as insurance — *"the notice and the copy ship regardless"* — is what
makes the bundling lawful without anything new being written. The pinned revision still decides
which bytes travel: the packaging step verifies the file against the SHA-256 in `models.json` before
copying it, so what ships is the revision this document names and not whatever the URL serves later.
§2.2's *"(through multiple tiers of distribution)"* remains the clause showing the drafters
contemplated exactly this.

The recogniser's CC BY 4.0 weights are **not** bundled and this paragraph does not reach them — for
size reasons rather than licensing ones, which `docs/PHASES.md` records.

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
detection added 2026-08-23 (the default in the app and on the command line whenever the model is
installed; `--vad energy` asks for the gate), installed by URL from `snakers4/silero-vad` at commit
`6478567951ae5c9979ad7b234185b5515f4be7a1` (tag v5.1.2) and pinned by SHA-256. Its `LICENSE` at that
commit is the MIT License, *Copyright (c) 2020-present Silero Team*, fetched the same day and
shipped byte for byte as `licences/silero-vad-LICENSE.txt`.

**What MIT asks is narrower than CC BY's seven elements and wider than a licence name:** "the above
copyright notice and this permission notice shall be included in all copies or substantial portions
of the Software." So two things travel with the graph — the copyright line and the permission text —
and both do: `Licences.targets` copies the file into every build output, `package-windows.ps1`
refuses a publish without it, and `Attributions.ById["silero-vad"]` is an `MitAttribution`, a record
that cannot be constructed without the copyright line and the path to the text. `uindosill notice`
and the About window render it with the other three.

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

## The diariser is CC BY 4.0 (pyannote), and it is now the only one — 2026-08-27

`pyannote-speaker-diarization-community-1` replaced `diarizen-wavlm-large-s80-md-v2` as the
speaker-labelling alternative beside Sortformer in the morning, and became the whole of speaker
labelling that afternoon when Sortformer was retired. **It is CC BY 4.0**: attribution, no use
restriction, the same shape the transcription weights already ship under and the same
`CcByAttribution` record renders. There is nothing in this section a commercial user must refuse.

**Why it changed, in one paragraph.** `pyannote.audio` 4.x floors four `pyannote.*` distributions
above the versions DiariZen's 3.1.1 fork needs, so one bundle cannot hold both engines — the swap
was forced by packaging rather than chosen for licensing. The licence improvement came with it:
pyannote 4 upstreamed the VBx clustering that BUT Speech@FIT contributed, which is the algorithm
DiariZen's own pipeline used, so the capability that mattered — clustering rather than tracking, no
total speaker cap — survived the move. `docs/UNPROVEN.md` carries what has and has not been run.

**One artefact, one licence, one notice.**

| Artefact | Licence | Where it is stated |
|---|---|---|
| `pyannote.audio` (the package, from PyPI) | MIT, *Copyright (c) 2020 CNRS* | the wheel's own `LICENSE` |
| **The `community-1` pipeline: both checkpoints, both PLDA files, `config.yaml`** | **CC BY 4.0** | the model card's frontmatter |

**Two upstream behaviours are switched off, and both are licensing-adjacent enough to record here.**

- **Usage reporting ships enabled.** `pyannote/audio/telemetry/config.yaml` carries
  `metrics_enabled: true` and an endpoint at `https://otel.pyannote.ai/v1/traces`, and
  `track_pipeline_apply` reports the **duration of every file processed** with a per-process session
  id. For a product whose claim is that audio never leaves the machine, inheriting that default
  would make the claim false. The engine sets `PYANNOTE_METRICS_ENABLED=false` before importing the
  package. **Unverified by observation** — see `docs/UNPROVEN.md`.
- **TorchCodec is a required dependency and decodes through FFmpeg**, the LGPL component this
  product removed in 1702d9e. It does **not** redistribute one: its own PyPI description says
  *"TorchCodec uses the version of FFmpeg you already have installed"*, so the wheel carries no
  FFmpeg binary and installing it adds no LGPL object to this distribution. The engine feeds the
  pipeline in-memory waveforms — upstream's own documented route when TorchCodec is absent — so no
  decode path reaches it. **The LGPL exit stands**, on the strength of that description and a read
  of upstream's `core/io.py`; neither an assembled wheel nor a running pipeline has been inspected,
  and `docs/UNPROVEN.md` carries the gap.

### The history: DiariZen was CC BY-NC 4.0, and it was the first non-commercial licence here

Kept because a licence this product once carried is worth being able to find, and because the
weights are still installable by anyone who fetches the shelved engine out of `attic/diarizen/`.

`diarizen-wavlm-large-s80-md-v2` — the speaker-labelling alternative added beside Sortformer on
2026-08-26 and shelved on 2026-08-27 — was the **fifth** model licence, and the first one that
restricted *use* rather than only paperwork. That made it the entry to read twice, so this section
stated the restriction before anything else:

> **The weights may not be used commercially.** CC BY-NC 4.0, upstream's own `MODEL_LICENSE`:
> *"The pre-trained model weights released in this repository ('the Models') are licensed under the
> Creative Commons Attribution-NonCommercial 4.0 International License (CC BY-NC 4.0)."*
> Upstream states the reason: *"some training datasets are research-only or non-commercial, so the
> released weights cannot be used commercially."*

**Decided at the time: it was downloaded, never bundled — and that decision is what kept the
restriction off this project's distribution.** Every other model here is either fetched by the user
or carried in the installer; that one could only ever be the first. A bundled NC weight would make
each Uindosill build a redistribution of non-commercial material inside an otherwise MIT/GPL
distribution, and would hand every commercial recipient a file they may not use. Downloaded, the
copy was the user's and the obligation to stay non-commercial was theirs, so this project shipped
nothing under NC terms even then; since 2026-08-27 it carries no NC entry to be careful about.
`BundledModels` therefore does not list it — and **as of 2026-08-26 it lists no diariser at all**:
Sortformer left the installer the same day and the catalogue the day after, so a fresh install
downloads the one entry that remains. That makes the non-commercial rule easy to state and impossible
to breach by accident: no build of this project carries speaker-labelling weights of any licence, and
since 2026-08-27 there are no weights in this product under any licence but CC BY 4.0, Apache-2.0 and
MIT.

**Four upstream licences meet in one catalogue entry, and the entry owes all four.**

| Artefact | Licence | Where it is stated |
|---|---|---|
| DiariZen source (`diarizen/`) | MIT, *Copyright (c) 2024 BUT Speech@FIT* | `LICENSE` at the repository root |
| Its `pyannote-audio` fork (3.1.1 + `VBxClustering`) | MIT, *Copyright (c) 2020 CNRS* | `pyannote-audio/LICENSE` in-tree |
| `diarizen/clustering/VBx.py` | Apache-2.0 (BUT Speech@FIT) | the file's own header |
| **`pytorch_model.bin` (the checkpoint)** | **CC BY-NC 4.0** | `MODEL_LICENSE` and the model card's frontmatter |
| `pyannote-wespeaker-voxceleb-resnet34-LM.bin` (the embedder) | CC BY 4.0 (pyannote) | the model card's frontmatter |

The last two are the ones that ship as *weights*, and they are **not the same licence** — which is
why the entry carries two attribution records rather than one. The embedder is plain CC BY 4.0, the
shape `CcByAttribution` already renders for the transcription weights; the checkpoint is the new
`CcByNcAttribution`, which is CC BY's notice package plus the non-commercial term stated in the
user's own words rather than left implicit in a licence name. **A record that could be constructed
without saying "non-commercial" would be the whole failure mode of this section**, so it cannot be.

**Why the entry is one and not two.** The pipeline does not run without both files: DiariZen supplies
segmentation, the wespeaker model supplies the embeddings VBx clusters. Split across two catalogue
entries, the Models tab would offer a user two "SPEAKERS" rows either of which alone installs half a
diariser, and there is no dependency concept in the catalogue to stop that. One entry, two notices,
is the shape that matches what is actually being installed. `docs/MODELS.md` records the same in the
catalogue's own terms.

**The embedder is renamed on download and that is deliberate.** Both upstream repositories call their
weights `pytorch_model.bin`; one directory cannot hold two. It arrives as
`pyannote-wespeaker-voxceleb-resnet34-LM.bin`, which also means the file says whose it is without reference to
a manifest — and renaming is a "change" under CC BY 4.0 §3(a)(1)(B), so the attribution record says
that the file was renamed and nothing inside it altered.

**What CC BY-NC asks that CC BY does not.** The BY half is the same seven-element package the
transcription weights already discharge. The NC half is a use restriction, and it reaches surfaces
rather than files: `uindosill notice` and the About window state it, and the Models tab states it
before the download rather than after, because a user who learns of it afterwards has already
fetched it. It is **not** revocable the way the NVIDIA Open Model License is, and it carries no
biometrics term — the two things that made the Sortformer entry's shape what it is do not apply
here.

**What is not claimed.** No lawyer has read this. Neither HuggingFace repository ships a `LICENSE`
file — for both, the licence exists as card frontmatter, `cc-by-nc-4.0` and `cc-by-4.0`
respectively — and for DiariZen the `MODEL_LICENSE` in the GitHub repository is the fuller statement
and the one quoted above. Whether "non-commercial" reaches a user transcribing their own work
recordings is a question this project does not answer for them; it states the term and leaves it
there.

## The ask engine is MIT (llama.cpp), and its notice is fetched rather than found

`llama-server.exe` and the DLLs beside it — the child process behind the Ask panel, vendored
under `native/win-x64/llm/<backend>/` since 2026-08-24 and shipped per channel: the vulkan drop
in the default channel, the CUDA drop in win-cuda (`scripts/package-windows.ps1` holds the
channel table and the decisions beside it) — are llama.cpp's own Windows release binaries, MIT,
*Copyright (c) 2023-2026 The ggml authors*.

**The obligation has one wrinkle the other natives do not: no llama.cpp release zip carries a
LICENSE file** — measured at b10448 and at the b10603 pin alike — so there is nothing in the
archive for an unpacking to keep. `scripts/vendor-llm-natives.ps1` therefore fetches the MIT
text from the source tree at the pinned tag, verifies it against its own recorded digest
(1,078 bytes; the digest is in `docs/NATIVE-BINARIES.md` beside the archive pins), and writes it
as `LICENSE` beside the binaries in each backend directory, where `build/NativeAssets.targets`
carries it into every output and `package-windows.ps1` refuses a package without it — the same
travel arrangement as parakeet.cpp's, differing only in where the text comes from.

**The component is rendered with the others**: `Attributions.Components` carries the row, so
`uindosill notice` and the About window both say it, and a suite test holds the row to the
copyright line and the travelling text. The binaries are unmodified upstream release builds,
pinned by the release API's own per-asset digests and re-hashed locally on every vendoring; what
this project adds is the C# that starts, asks and kills the process, which is this project's own
MIT code.

**The win-cuda channel's ask tier redistributes a second CUDA runtime, and it is
NVIDIA-proprietary, not MIT.** The maintainer decided on 2026-08-24 that win-cuda ships
`llm/cuda`: the b10603 CUDA drop together with the DLLs of upstream's
`cudart-llama-bin-win-cuda-13.3-x64.zip` — a CUDA 13.3 runtime (`cudart64_13.dll`, file version
13030, read on the desktop that vendored it) beside the ASR tier's 12.8 trio, two CUDA runtime
majors in one package. The legal basis is the ASR tier's reading, unchanged: §2.6 (Attachment A)
of the CUDA Toolkit EULA names `cudart`, `cublas` and `cublasLt` as redistributable in
version-numbered variants, and the §1.1.2/§1.2 conditions are met the same way — the
application is the material functionality, the files sit in the application's own native tree
and are loaded only by its `llama-server` child, and nothing ships them stand-alone. What is
*not* yet done: the cudart archive's full DLL inventory is not recorded in this repository's
tables, so reconciling each shipped DLL name against Attachment A's list, on a machine holding
the drop, is owed before the first win-cuda tag. `NOTICE.md` carries the component's row either
way, for the same reason the ASR row is unconditional.

**What is not claimed.** No lawyer has read this either. The zips also carry OpenMP runtime
DLLs, covered by the same reading as the ASR tier's.

## The bundled Python is 99 more redistributions, and the notice half is discharged again

**Re-enumerated twice on 2026-08-26**, each time against a bundle assembled that day. The second
diariser's stack took it from the fifty verified on 2026-08-21 to 108; removing librosa later the
same day took it to **99**, and 1.40 GB to 1.26 GB. Everything below describes the 99, and the
reading has been redone rather than scaled.

**108 and not the 112 a resolve predicted**, and the difference is worth stating because it is the
kind of gap that makes a projected number untrustworthy. A `--dry-run` of the requirements reported
112 while speechbrain was still pinned. Dropping it — it makes DiariZen unloadable in the bundle,
`docs/GOTCHAS.md` #36 — took `hyperpyyaml` and `ruamel.yaml` with it, because nothing else wanted
them. **A resolver's count is not an enumeration**, and the number here is the one an assembled
`Lib/site-packages` actually holds.

**99 is behind by two pins as of 2026-08-27.** The speaker embedder's ONNX export added `onnx` and
`onnxscript`, which bring `onnx_ir` and `ml_dtypes` with them. No bundle has been assembled since,
so the enumeration has not been redone and the paragraph above describes a `Lib/site-packages` that
no longer matches the requirements. The figure is a floor until one is — stated rather than
adjusted, because a count arrived at by adding to a verified one is exactly the projected number
the paragraph above declines to trust.

**Depending on a package and shipping it are different obligations, and this is the change that
crosses from one to the other.** `PythonRuntime.Resolve` looks for `<app>/python/python.exe` — an
interpreter beside the application, so a user installs nothing and no system Python is consulted.
What that interpreter is made of is something this project hands to people.

**The notices themselves are assembled, and they are in `NOTICE.md`.**
`scripts/collect-python-notices.py` reads the installed `METADATA` of every distribution in an
assembled bundle and writes the table there between two markers, with `--check` failing when the
document and the bundle disagree. **That guard did its job on this re-enumeration** rather than
merely existing: it refused to write until `antlr4-python3-runtime` and `primePy` — two newcomers
shipping no licence text at all — were named with their reasons. What stays here is what a generator
cannot write: what each licence *obliges*, which are not simply permissive, and what is unresolved.

**Three are not simply permissive, and every stated licence was scanned for a copyleft family
rather than the four being assumed.** Sixty new distributions brought no new one, and the removal of
librosa took the hardest of the old ones with it.

1. **soundfile is BSD-3-Clause and its wheel is not.** `_soundfile_data/libsndfile_x64.dll` ships
   with a `COPYING` beside it that is the **LGPL-2.1** — confirmed still present in this bundle. A
   table that read package metadata and stopped there would record this one as BSD and miss it,
   which is why the check walks the tree.
2. **certifi is MPL-2.0** — file-level copyleft, weaker than the LGPL and still not MIT.
3. **tqdm is MPL-2.0 AND MIT** — the same shape, and an `AND` rather than an `OR`: both apply.

The LGPL is the one with a shape this product has to think about: it attaches conditions about
relinking to a binary a recipient receives. **That question is now read rather than deferred, and
the answer is below.** The reading is kept in the present tense it was written in, because it is
what justified the removal that followed; where it says "two components", one of them left the
bundle the same day and the closing paragraphs say so.

### The LGPL question, read against what actually ships — 2026-08-26

The paragraph above used to end by saying this had not been worked out. It has now been read, against
the binaries in an assembled bundle rather than against the idea of them, and **the two LGPL
components turn out to be in different positions.** What follows is a careful reading by the people
writing the code, which is the standing of every licence reading in this file; no lawyer has seen it.

**Neither library is linked by this project.** Both arrive as prebuilt binaries inside wheels that
`pip` installs unmodified, and `scripts/bundle-python.ps1` copies the tree whole. So the act being
performed is *redistribution of the Library in object form* — LGPL-2.1 §4 — and, for the application
that calls it, distribution of a "work that uses the Library" combined with it — §6. Nothing here
compiles, patches or statically incorporates either library by its own hand.

**libsndfile is dynamically loaded and replaceable, and that is the good case.** `soundfile` is
BSD-3-Clause Python; `_soundfile_data/libsndfile_x64.dll` is LGPL-2.1 and `soundfile.py` reaches it
with `_ffi.dlopen(_full_path)` at run time — and falls back to `_ffi.dlopen(_libname)`, a system
copy, when the packaged file is absent. That is a shared library mechanism in the sense §6(b)(2)
asks for: **a user can drop an interface-compatible libsndfile into `_soundfile_data/` and the
product will use it**, which was verified by reading the loader rather than assumed. What §6(b) does
*not* cleanly cover is its own clause (1) — "uses at run time a copy of the library **already present
on the user's computer system**, rather than copying library functions into the executable" — and
this installer ships the DLL rather than finding one. The mechanism is right and the provenance of
the copy is not what 6(b)(1) describes, so **6(b) is arguable here and is not relied on below.**

**libsoxr was statically linked, and that was the harder case — and the reason it is gone.**
`soxr` shipped as `soxr/soxr_ext.pyd` — 354,304 bytes, and its import table named only `KERNEL32`,
the MSVC runtime, the `api-ms-win-crt-*` stubs and `python3.dll`. **There was no libsoxr DLL
anywhere in the bundle**, so the library was inside that binary. §6(b) was therefore unavailable outright: nothing was
shared-linked and library functions *were* copied into the executable. The wrapper is itself
LGPL-2.1 (Python-SoXR, Copyright (c) 2021 Myungchul Keum), so the `.pyd` is a work under the LGPL
rather than a proprietary work that merely uses one — which simplifies the question rather than
complicating it.

**Three of §6's conditions are already met, by construction rather than by intent.**

- *A copy of the License, supplied.* `_soundfile_data/COPYING` ships, because `pip install
  --target` keeps what the wheel carries and the packaging step copies the tree whole. While `soxr`
  was here, `soxr-1.1.0.dist-info/licenses/COPYING.LGPL` and libsoxr's and PFFFT's own notices
  shipped beside it.
- *Prominent notice that the Library is used and is covered by this License.* `NOTICE.md` names it
  and says how it is linked.
- *Terms that permit modification for the customer's own use and reverse engineering for debugging
  those modifications.* This product imposes no terms that forbid either: the source is MIT, there
  is no EULA, and `docs/LICENSING.md` already records that no technological measure restricts the
  weights. **An LGPL component inside a product whose own licence forbade reverse engineering would
  be the breach; this one does not.**

**What is not met is the one that matters: none of §6(a) to §6(e) has been done.** No corresponding
source accompanies a release, no written offer exists, and no equivalent source access is offered
from the place the releases are distributed. For libsndfile that gap is arguable, because 6(b) may
carry it. **For libsoxr it is not arguable at all** — the static link closes 6(b), and nothing else
has been provided.

**The cheapest correct discharge is a written offer, and this repository already knows the shape.**
`licences/mpv-WRITTEN-OFFER.txt` satisfies GPLv2 §3(b) for libmpv by naming exact revisions of
everything GPL in the distribution. §6(c) is the same instrument for the LGPL: an offer valid three
years to supply the §6(a) materials — for each library, its complete corresponding source, and for
`soxr_ext.pyd` the "work that uses the Library" in a form allowing relink, which is Python-SoXR's own
C++ wrapper and is public. §6(d) is the alternative and is *narrower* than it looks: it wants
equivalent access **"from the same place"**, so pointing at upstream's site would not be 6(d) while
attaching a source archive to the same GitHub release would.

**The alternative was to stop shipping them, and for one of the two that turned out to be nearly
free.** **`soxr` was never loaded.** `uindosill_engines` did not import it, and `librosa` did not
pull it at import or for the single call this project made — `librosa.filters.mel` — which was
checked by running it and reading `sys.modules`. It was in the bundle only because `librosa`
declared it as a hard dependency, and `librosa` was here for that one call, which
`python/requirements-bundle.txt` already recorded as replaceable by a committed filterbank, for
size. libsndfile is a real dependency by comparison — `soundfile` reads every WAV the host writes —
though those are 16-bit PCM mono, which the standard library's `wave` module can also read.

**Both exits were taken, in that order.** `soxr` is gone — see below — so the statically-linked
half of this problem no longer exists, and what remains is libsndfile, the replaceable half. The
written offer stays, because a replaceable DLL is still an LGPL binary this project distributes and
because §6(b)(1)'s "already present on the user's computer system" does not describe a copy the
installer ships.

**Discharged 2026-08-26 by a written offer under §6(c).**
`licences/LGPL-WRITTEN-OFFER.txt` names libsndfile at the exact version shipped, with the SHA-256
and byte count of the binary, says how it is linked and that it is replaceable, and offers the
§6(a) materials for three years. **It extends the same offer to libsoxr for anyone holding a copy
that contained it** — including the "work that uses the Library" in a form allowing relink, which is
Python-SoXR's own wrapper source — because a recipient of an older release is owed the offer that
applied to their copy, and a removal does not reach backwards. It travels through `Licences.targets`
like every other notice, and `scripts/package-windows.ps1` now **refuses a publish without it**, on
the same terms as the NVIDIA Agreement: a build that silently stopped copying it would produce a
package that looks complete.

That is the whole obligation for libsndfile too. §6(b) may well have carried it — the DLL is
separate and replaceable — but the offer covers both, so nothing here rests on a reading of
6(b)(1) that a shipped copy is "already present on the user's computer system".

**The second exit was taken the same day.** `librosa.filters.mel` in `diariser/feats.py` became a
committed `mel-filterbank.npy`, librosa left `python/requirements-bundle.txt`, and **`soxr` left with
it** — along with numba, llvmlite, pooch and audioread. An assembled bundle went from 108
distributions and 1.40 GB to **99 and 1.26 GB**, and **nothing statically linked in this product is
under the LGPL any more.**

**Both of those files left `python/` the next day, and the conclusion above is unaffected.** On
2026-08-27 `feats.py` and the `mel-filterbank.npy` that replaced its librosa call moved to
`attic/sortformer/uindosill_engines/diariser/` with the engine that called them;
`python/uindosill_engines/diariser/` holds `pyannote_engine.py`, `onnx_export.py` and `__init__.py`,
and **the shipping diariser builds no filterbank of this project's own at all.**
`PyannoteEngine.label` reads the host's 16 kHz mono WAV with `soundfile` and hands `pyannote.audio`
an in-memory waveform; pyannote's own features are `torchaudio.functional.resample` and
`torchaudio.compliance.kaldi`, both pure torch. What the sentence above turns on is not that file
but the pins: librosa and `soxr` are absent from `python/requirements-bundle.txt`, which is where
that file's own comment now says the reason has outlived the argument that removed it. **Re-adding
librosa, for the diariser or for anything else, would reopen this.**

**Two things had to be true first, and both were measured rather than argued.** *That the features
do not move*: `librosa.filters.mel` at this project's parameters produces a `(128, 257)` float32
matrix that is deterministic across calls and round-trips through `.npy` bit-for-bit, and the old
code and the new produce **the same 12,016 x 128 mel array to the last bit over two minutes of real
audio**, with librosa hard-blocked at `sys.meta_path` so the new path could not quietly fall back to
it. That check was run before the edit on sixty seconds and again after it on two minutes, because a
proof of the version you are about to write is not a proof of the version you wrote. Committing the
matrix is therefore not a loss of the fidelity `python/requirements-bundle.txt` protects but a
strengthening of it: it pins the exact array the 16.3324% figure was produced with, where the
present code depended on a library continuing to produce it. *And that nothing else wanted librosa*:
removed from an assembled bundle together with `soxr`, the engine still returned 19 turns and 3
speakers, the reference result, because `torchmetrics` guards its own `import librosa` behind an
availability check and simply stops exposing `dnsmos`. `docs/PHASES.md` carries the decision.



**Five ship no licence text anywhere, up from three, and the two new ones are the weakest claims in
the bundle.** `flatbuffers`, `sentencepiece` and `tokenizers` are the originals and all name Apache,
whose text travels several times over in this bundle — so a recipient has the licence even without
a copy attached. The newcomers do not have that luxury:

- **`antlr4-python3-runtime` says only `BSD`.** That names a family, not a licence: BSD-2 and BSD-3
  differ by a clause, and nothing in the wheel says which. It arrives because every `omegaconf`
  release pins `==4.9.*`, and it is one of two packages built from source through the allowlist in
  `scripts/bundle-python.ps1`.
- **`primePy`'s `License` field is the literal `UNKNOWN`**, with a trove classifier alone claiming
  MIT. It reaches the bundle transitively, through `torch-pitch-shift` under `torch-audiomentations`.
  **This is the least-established licence claim in the product**, and it is recorded here rather
  than rounded up to MIT.

**Three groups are read rather than resolved, and the newcomers enlarged the middle one.** Where a
wheel gives a PEP 639 `License-Expression` the identifier is exact; where it gives the legacy field
or a classifier it is often a family. `mpmath`, `numba`, `sympy` and now `antlr4-python3-runtime`
say only "BSD"; `accelerate`, `huggingface_hub` and `optimum` say only "Apache"; `python-dateutil`
says the literal **"Dual License"**, which states that there are two and names neither. Ten more
arrive with a classifier and no field at all — `Jinja2`, `colorama`, `contourpy`, `cycler`,
`kiwisolver`, `omegaconf`, `pandas`, `scipy`, `semver` and `torchaudio` among them. **These are
recorded as read**, and that is a weaker check than an SPDX expression.

**Two large bodies of third-party notice ship inside this bundle and nobody here has read either.**
`torch`'s wheel carries **107 licence files** in its `.dist-info` — its own plus the vendored
projects its SPDX expression is a summary of — and `onnxruntime-webgpu` ships a
**331 KB `ThirdPartyNotices.txt`** in the package directory — 331,175 bytes, the same file the
section above compares against the committed 1.29.0 copy, quoted there in the same decimal kB
so the two readings cannot look like a disagreement. Both travel with the product, so the
texts reach a recipient; what has not happened is anyone reading them to find out whether any
imposes a condition beyond attribution. That is the same unresolved item the previous enumeration
recorded, restated against this bundle: the earlier note said "thirty-four third-party licence
directories" for torch, which does not describe the 2.13.0 wheel — its licences live in the
`.dist-info` and there are 107 of them. **Counts about someone else's wheel go stale when the wheel
does**, which is the argument for the generated table doing the counting.

**What this re-enumeration does not settle** is that it stays 108. It is one resolution, on one day,
on Windows on x86-64, against an index that moves, and `pip` is free to bring in a new transitive
dependency next time. What has changed since the fifty is that the failure mode is now caught rather
than merely described: `--check` compares a fresh enumeration against `NOTICE.md` and fails on a
mismatch, and an unlisted textless distribution fails the run outright. **A hundred-and-ninth
arriving silently is no longer the risk; a hundred-and-ninth arriving and nobody running the script
is.** The release workflow runs it after packing, which is the only point in CI where a bundle
exists.

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

**The vendored NeMo was the one part of the bundle whose obligation was already written down — and
it left the bundle on 2026-08-27.** `python/uindosill_engines/_vendor/nemo/` held two of NVIDIA's
Apache-2.0 files and thirteen of this project's own; that tree moved to
`attic/sortformer/uindosill_engines/_vendor/nemo/` with the diariser that imported it, the `_vendor/`
directory is gone, and **no bundle carries NeMo now**. `NOTICE.md` carries the entry and the §4 check
against it, and neither is repeated here — but read there, as here, as a statement about what the
source tree still contains rather than about what an installer would ship. **This paragraph's claim
was about the bundle, and to that extent it no longer holds**: what the Apache-2.0 reading now
governs is a redistribution of this repository, which carries the files whether or not a bundle is
ever built.

**Nothing above is discharged.** No notice package has been assembled for any of it, no audit has
been run, and `uindosill notice` and the About window say nothing about the Python. This section is
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

The weights sections above were untouched by the move, and that is the point worth stating: the same
CC BY 4.0, NVIDIA Open Model License and Apache-2.0 material was used for the same purpose against
the same conditions. **What changed is which process loads it, not who the licensee is or what the
licence asks for.**

(The NVIDIA Open Model License material named there is the Sortformer diariser, retired on
2026-08-27. Nothing in this product is under that Agreement now — see the section above — which does
not affect the argument this paragraph makes about the move.)

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
the entry, so `uindosill notice` and the app's About window both render it, and a test asserts the
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
energy gate written for this project and, since 2026-08-23, Silero VAD — the default on both routes
whenever its model is installed — which is
MIT and has its own section above; its licence was read before the graph was. If you ever reach for
an off-the-shelf VAD, read its licence first — this is a category where the popular choice has a
rider on it.

## Language claims are a licensing-adjacent honesty problem

`parakeet-tdt-0.6b-v3` covers 25 European languages and does not cover Chinese, Japanese, Korean,
Arabic, Hindi or Thai. Advertising them would be false rather than merely optimistic. A test asserts
no catalogue entry claims those tags.
