# Phase plan and where this repository actually stands

Exit criteria are evidence, not opinion. What follows is the plan and an honest mark against each
step.

## Phase 0 — spike, no UI — **DONE**

Console harness: load a GGUF through P/Invoke, transcribe a real file, print the JSON.

*Exit:* non-empty, correct text from real weights on real Windows, **before anything else is built**.
Then a benchmark matrix (warmed up) and a WER harness over a corpus that includes real disfluent
accented audio and at least two files over ten minutes. Record RTF, cold load, peak RSS, and
long-file WER at each quantisation against f16.

**Status:** the first half is done. A 30-second clip decoded correctly on Windows x64 against
parakeet.cpp v0.5.0 and `tdt-0.6b-v3-f16`, at RTF 0.10 — the full record is in `docs/UNPROVEN.md`.
That settles the question this phase exists to answer: the engine produces correct text through
these bindings.

The timing and memory half is now done, though not through `bench`. Three real files were measured
end to end — 30 s, 10 min and 2 h 55 m — giving RTF at three durations (0.1005, 0.0829, 0.0790),
peak working set at two (2,379 MB and ~2,950 MB), and a working-set profile across three hours
showing memory peaks mid-run and falls. `scripts/measure-transcribe.ps1` is that harness.

**The WER half is done, 2026-08-16.** `scripts/measure-wer.ps1` is the harness and
`scripts/wer-corpus.json` the corpus — Rev.com's Earnings-22 Subset 10, ten human-transcribed
earnings calls of 58–78 minutes from five countries, 11.12 hours, two transcript styles, every file
pinned by digest — which is real, disfluent, accented and long-form, as this phase asked. Every
catalogue entry was scored on the RTX 5080 desktop on CUDA: **f16 10.21%, q8_0 10.23%, q6_k
10.17%, q5_k 10.17%, q4_k 10.15%** against the verbatim transcripts, 13.34–13.43% against the
non-verbatim ones — a 0.08-point spread with no ordering, so on this material no quantisation in
the catalogue costs measurable accuracy against f16. The method, its normaliser (deliberately not
the leaderboard's, so the figures are not comparable to a published one), the per-file table, the
CPU control and the limits are in `docs/UNPROVEN.md`. `uindosill bench` has still only run against
the canned engine; the timings this phase wanted came from `transcribe` runs instead.

## Phase 1 — core — **DONE**

Contracts, WAVE decoding, formatters, model catalogue and resumable download with SHA-256, the fake
engine.

*Exit:* `dotnet test` green on Linux with no weights present.

**Status:** met. 451 tests, no weights, no display, no network. One of them — the Media Foundation
extension list — is Windows-only and skips itself here, so a Linux run reports 450 passed and
1 skipped.

## Phase 2 — engine — **DONE**

`Parakeet.Engine.ParakeetCpp`: SafeHandles, the marshalling layer, VAD segmentation with a 30-second
cap, batch decode, timestamps from `frame_sec`.

*Exit:* the CLI transcribes a real file to correct SRT.

**Status:** exit criterion **met**, on a 30-second WAV, ten minutes of podcast, and a full
2 h 55 m episode through Media Foundation: 1,488 segments, 29,926 words, no word out of order, none
past the end of the audio, and no duplication or loss at any join. All segment boundaries land on
the 0.03 s analysis-frame grid and all word starts on the 0.08 s model-frame grid relative to their
segment, so the two clocks stay locked across three hours.

The caveat carried through Phases 2–4 is now discharged: **four segments reached the 30-second cap**
on the long file and were cut mid-sentence, and all four joins read through cleanly. Three runs of
that file produced byte-identical output. See `docs/UNPROVEN.md`.

## Phase 3 — CLI — **DONE**

`transcribe`, `models`, `bench`, plus `doctor`, `notice`, `wer` — since 2026-08-16 — which
scores a transcript against a human reference and is what the Phase 0 harness is built on, and —
since 2026-08-17 — `der` and `rttm`, the diarisation error rate and the Audacity-labels-to-RTTM
converter the speaker measurement is scored with.

*Exit:* usable on its own; `bench` reproduces Phase 0.

**Status:** usable, tested end to end against the canned engine (55 of the project's 81 CLI
tests drive the real entry point; the other 26 never construct it — 17 parser unit tests, 7 that
check `--vk-disable-bf16` and its opposite `--vk-bf16` against the real command specs through
`CommandLineParser`, and 2 on the resolver that turns the pair into the engine option). `bench` has not yet been pointed at real weights, so the RTF 0.10 figure above came
from a plain `transcribe` run rather than from a warmed-up timed sweep.

One deviation from the plan worth recording: **`bench` does not sweep thread counts.** The founding
plan called for a thread-count × machine matrix, but no entry point in the parakeet.cpp ABI takes a
thread count, so such a sweep would be measuring nothing. It sweeps batch size instead and prints a
line saying why.

## Phase 4 — UI — **DONE**

Avalonia: drop zone, job queue with continue-on-error, streaming transcript, model manager showing
the licence, settings.

*Exit:* a human uses it on Windows to transcribe a real file.

**Status:** exit criterion **met**. Run on Windows 11: a file dropped on the window, decoded with
a live progress bar and streaming transcript, the model list showing the installed weights and the
Licences tab rendering the full CC BY notice.

Two defects that only a real launch could show, both since fixed: the Models tab was read-only —
its download, remove and unverified-opt-in controls existed on the view model and were bound to
nothing, while its own text told the reader the opt-in was "below" — and Start was enabled with an
empty queue, so pressing it did nothing and read as a broken button.

## Phase 5 — ship — **STARTED**

Velopack, signing every PE, SmartScreen reputation, auto-update.

**Status:** started 2026-08-15, with the piece that has no external dependency. What existed before
was the groundwork: publish is self-contained + ReadyToRun and verified to cross-publish from Linux
for `win-x64`, single-file and trimming are off (and documented as deliberately off), and every
native lives under `native/<rid>/<backend>/` where a signing step can enumerate it.

What is new is that **the natives no longer arrive by hand.** `scripts/vendor-natives.ps1`
downloads the pinned parakeet.cpp v0.5.0 archives, refuses to unpack anything whose byte count or
SHA-256 is not the one recorded in `docs/NATIVE-BINARIES.md`, unpacks flat into the layout the
loader searches, and reads the drop back — `parakeet.dll` at the documented size, `LICENSE` beside
it. CI runs it before the `win-x64` publish and then asserts both files, for both backends, in both
apps' output. Verified locally the same way on 2026-08-15: a `win-x64` publish of the CLI and the app
each carried `native/win-x64/{cpu,vulkan}/{parakeet.dll,LICENSE}`, and `uindosill doctor` run from
the published CLI reported `ok — abi 6` for cpu and vulkan from those directories.

The first CI run after that commit did the same on Linux, and the artefact it uploaded was
downloaded and run on Windows: `doctor` from it reported `ok — abi 6` for cpu and vulkan from their
own directories. `docs/UNPROVEN.md` has the run and what it does and does not prove.

Two things that is not. It is not an installer — the artefact is a directory you unzip, and no
transcription has yet been made from a CI-built binary. And it is not signed: the repository
still has no signing identity, and the vendored `parakeet.dll`s are unsigned third-party binaries
— `Get-AuthenticodeSignature` reports `NotSigned` for both the cpu and vulkan builds — which is the
shape Smart App Control blocks.

Remember that signing `Setup.exe` alone is not enough. Smart App Control and WDAC evaluate every
loaded binary, unsigned native DLLs are exactly what gets blocked, and a signed installer dropping
unsigned executables is itself a recognised malware shape.

### Decisions taken 2026-08-16

These close the two questions this phase was waiting on. **Nothing below is built**; this is the
plan, recorded so the next session builds what was decided rather than re-deciding it.

1. **The CUDA tier is a second download flavour.** Two Velopack channels from the same publish: the
   default carries `cpu` and `vulkan`; the second carries `cpu`, `vulkan` and `cuda`. The choice is
   made at download time, so the default download stays clear of the ~700 MB the CUDA archives add
   (`docs/NATIVE-BINARIES.md`). It reuses `scripts/vendor-cuda.ps1`, the `native/**` glob and the
   NVIDIA attribution unchanged, and it keeps the runtime inside a whole-application package rather
   than a download of its own — the shape `docs/LICENSING.md` reads the EULA's stand-alone clause
   against. Cost: a ~1 GB release asset per version, deltas after the first. Not chosen: an in-app
   download of the CUDA archives (best experience, most new code — a user-writable search path for
   the loader, a Backends control, tests) and deferring CUDA past v1.
2. **Signing takes the free route: the SignPath Foundation's open-source programme.** No certificate
   is bought. Its terms, read at signpath.org/terms on 2026-08-16, decide what "signed" can mean
   here, and two of them cut across item 1:
   - *"Sign your own binaries only."* Upstream binaries may ship unsigned inside a signed package,
     but the project may not sign them. So `parakeet.dll` stays unsigned on this route, and Smart
     App Control — which evaluates every loaded binary — is not answered by it. What it does answer
     is SmartScreen's "unknown publisher" on the installer and the app.
   - The project may not contain a *"proprietary, non open-source component"* — which the CUDA
     flavour does, in the three NVIDIA DLLs. On that reading only the default flavour is eligible
     and the CUDA flavour ships unsigned. This has not been put to SignPath.
   - The certificate is issued to SignPath Foundation, so that is the publisher name a user sees;
     every release needs manual approval; the build must be verifiable from source (the CI publish
     is); a code-signing policy and a privacy statement have to be published — and the statement
     cannot be "transfers nothing", because the app downloads models and, per item 4, checks for
     updates.

   Eligibility is not established: nobody has applied. If SignPath declines, the alternative that
   costs nothing is to ship unsigned, and that would be a further decision, not this one.

   That further decision arrived the same day, taken rather than forced: **v1.0 ships unsigned.**
   The maintainer decided it on 2026-08-16, independent of SignPath — no application gates v1.0,
   and the cost accepted is the known one, SmartScreen's "unknown publisher" on the installer and
   the app for every v1.0 user. The reading above stays for whenever signing is taken up, because
   nothing about the programme's terms changed.
3. **The installer is the desktop app only.** The CLI stays a zip beside it on the release, as the
   CI artefact is today: a smaller download and one thing to sign and update. Not chosen: both in
   one package with a PATH entry (Velopack has no PATH feature, so that is custom code on install
   and on uninstall), or two installers.
4. **Updates: check on launch, install on a click.** One HTTPS request to GitHub Releases at
   startup, a visible notice when there is a newer version, download and restart only when the user
   asks, and a setting that turns the check off. That request is the one thing the app does on the
   network unprompted, and the documentation will say so. Not chosen: Velopack's silent
   download-and-apply, and manual-only.

Defaults taken without a decision, all cheap to reverse: GitHub Releases is the host and the update
feed; a `v*` tag builds the release; the installer carries no weights — the Models tab downloads
them, as now; winget can follow later. **One thing to verify before anything is built:** Velopack
installs under `%LOCALAPPDATA%\<package id>`, and `%LOCALAPPDATA%\Uindosill\models` already exists
on every machine that has run this product. The package id or the layout has to keep the installer
from touching those files, and uninstall has to leave them.

## The honest summary

| Phase | Planned exit criterion | Met? |
|---|---|---|
| 0 — spike | Correct text from real weights on real Windows | Yes |
| 0 — spike | Timing and memory over real long audio | Yes |
| 0 — spike | A WER harness, so quantisation can be judged | Yes — all five entries within 0.08 points of f16 on 11 h of human-transcribed calls |
| 1 — core | `dotnet test` green on Linux, no weights | Yes |
| 2 — engine | CLI transcribes a real file to correct SRT | Yes, up to 2 h 55 m |
| 3 — CLI | Usable on its own | Yes (against the canned engine) |
| 4 — UI | A human transcribes a real file on Windows | Yes |
| 5 — ship | Signed, updating installer | Started: CI publish carries the natives; no installer, no signing |
| speakers | **AMI test DER within 5 points of the best published figure on the same audio at the same convention** — pyannote 3.1's 18.8 on Mix-Headset at collar 0 with overlap scored, so ≤ 23.8; collar 0 because half-width and total-width definitions agree there, which is what makes the comparison convention-proof — with this project's own headline (collar 0.25 pyannote semantics, 0.125 s either side, overlap included) reported beside it. **NOTSOFAR-1 is the crosstalk check** (39% of union speech overlapped, against AMI's 14.58%) and **VoxConverse the web-video and beyond-four-speakers check** (AMI test is 15/16 four-speaker and cannot price the cap). **Podcasts are ungated**, for want of any labelled material. The 5-point margin is pre-ratification. Opt-in aboard v1.0. | Instrument built and validated, AMI set up and verified, seam in; sherpa-onnx measured and far off, no candidate passing |

### The dictation seam

The brief said push-to-talk dictation must not be built and must not be architected out. It is now
v3, behind the question-answering panel, and nothing about that ordering changes what it will
need. Two
things it would need are recorded rather than assumed: the streaming ASR and end-of-utterance
weights are pinned in the `deferred` array of `models.json` with exact sizes and digests, and the
loader, installer and digest checking reach them unchanged once a licence is established for each.
Neither is installable in this build, and the reason is written down in `docs/MODELS.md`.

**Two things now stand between this and a v1 anybody can install: Phase 5, and speakers.**
Phase 5 because everything the product does, it does, but it cannot arrive on a machine that has
no .NET SDK and no git clone. Speakers because the maintainer decided on 2026-08-16 — overriding
the study's v1.1 recommendation, item 4 below — that **v1.0 does not ship without diarisation**:
opt-in in the product, an option the user turns on, but aboard from the first release.

The next actions, in order:

1. **Phase 5 itself** — Velopack, and signing every PE rather than only `Setup.exe`. ~~A build
   that vendors the natives instead of expecting a manual copy~~ — done 2026-08-15, above. ~~What
   signing waits on is a certificate, which is a purchase rather than a commit; what Velopack
   waits on is a decision about how the opt-in CUDA tier arrives, since 700 MB does not belong in
   the default download.~~ Both decided 2026-08-16 — see *Decisions taken* above: the CUDA tier is a
   second download flavour, and signing goes through SignPath Foundation's free programme, which
   waits on an application to SignPath rather than on a purchase, and which by its own terms signs
   this project's binaries and not the upstream natives. **Later the same day the signing half
   left v1 entirely: v1.0 ships unsigned** (decision 2 above records what that accepts), so
   Phase 5 for v1 is Velopack without signing and the SignPath application is post-v1 work.
2. ~~**A WER harness**, which gates *recommending* q8_0 or q4_k~~ — **done 2026-08-16**, Phase 0
   above and `docs/UNPROVEN.md`. It was moved behind v1 on 2026-08-15 by making f16 the default,
   and it has now been built and run anyway: every catalogue entry scores within 0.08 points of f16
   against eleven hours of human-transcribed accented English, so the evidence a recommendation
   needed exists.

   **f16 stays the default for now.** `tdt-0.6b-v3-f16` carries `"recommended": true` and
   `tdt-0.6b-v3-q8_0` carries `false`, so `ModelCatalog.Recommended` — which `EngineFactory`
   resolves an unspecified `--model` to — returns f16. That was chosen on 2026-08-15 because f16
   was the one entry whose quality was not an open question, at the cost of a 1.34 GiB download
   instead of 941 MB and a slower CPU decode. The measurement removes the *reason* for that
   choice without making the other one: whether to make q8_0 (or smaller) the default is a
   product decision about download size and CPU speed against one corpus's worth of evidence, and
   it has not been taken. The catalogue's entries no longer say "unmeasured"; they say what was
   measured and where.
3. ~~**Settle the CUDA drop's licensing**~~ — **done 2026-08-15.** The EULA was read against what
   this product actually ships, `Attributions.Components` carries the NVIDIA entry so both
   `uindosill notice` and the Licences tab render it, and two tests hold it up. The reading, and
   the three things about it that remain unverified — no legal review, an EULA revision not
   contemporaneous with the CUDA 12.8 binaries, and an upstream redistribution this project did not
   perform — are in `docs/LICENSING.md` and `docs/UNPROVEN.md`. What is left is Phase 5 packaging
   the result.
4. ~~**Before v1 ships: a research workflow on how best to implement speaker diarisation in this
   app.**~~ — **the study ran 2026-08-16**; its result lives in the maintainer's diarisation
   research on the Drive, outside this repository the way the v2 research is (moved out the same
   evening at the maintainer's ask — the convention `CLAUDE.md` now names), and the measurement
   design that used to live in this item moved there with it, sharpened. Asked for by
   the maintainer on 2026-08-16, after the WER work; a study, not a build. What it settled: the
   single most consequential unknown resolved — Sortformer runs without NeMo, because although no
   official ONNX exists the export recipe is public, community exports were verified
   file-by-file, and streaming v2 is CC-BY-4.0 and un-gated; every official pyannote pipeline
   repo is HF-gated, which the by-URL installer cannot use, while sherpa-onnx redistributes the
   same MIT segmentation model un-gated behind a maintained C# NuGet; DiariZen and Rev are
   non-commercial and out for shipping; and no candidate has a published number on podcast
   material, so the dev/held-out podcast set — stratified two to five voices through guest
   episodes, split by show, scored by a collar-0.25 overlap-included DER harness — remains the
   deciding instrument, with a proposed gate written down to be ratified before held-out is ever
   scored. **The study recommended v1.1; the maintainer decided otherwise the same day: speakers
   ship in v1.0, as an option the user turns on, and the maintainer sources the test data.** The
   study's machinery survives the override unchanged — the spike order, the exact artifacts, the
   gate ratified before held-out is ever scored — but the critical path inverts: labelling and
   the spikes move to the front of the queue beside Phase 5, and — signing having also been
   dropped from v1 the same day — v1.0 ships when Velopack packaging and the passed gate both
   land. The research's *The recommendation, and the decision* section carries the record, and
   the four stratified test episodes the maintainer supplied the same evening — two hosts plus
   zero, one, three and five guests, one show — live on the Drive beside it.
   (`docs/V2-ASK-THE-TRANSCRIPT.md` § *Not in v2: who said it* holds until the feature actually
   ships.)

   **Built 2026-08-17 — the instrument, the material, and the seam; nothing measured.** The
   laptop half of the build/measure split, in the order the plan fixed:

   - **The DER scorer** is `uindosill der` over `Parakeet.Core.Diarisation`: pyannote.metrics'
     algorithm — the union of both extents as the scored region, a collar cut out around every
     reference boundary, elementary intervals, the one-to-one speaker mapping that maximises
     co-occurring speech found by exhaustive search rather than greedily — and **validated against
     pyannote.metrics 4.1 on ten committed fixture pairs, all four blocks of each — headline,
     collar 0, overlap regions, and skip-overlap — agreeing to a microsecond** (`tests/fixtures/diarisation/scorer/`, `scripts/validate-der.py`; the C# test
     suite re-asserts the agreement on every run). It prints three numbers together: the headline
     at collar 0.25 s with overlap included, the strict number at collar 0, and the same components
     over reference-overlap regions only. `scripts/measure-der.ps1` — `lab.ps1 der`, the eleventh
     dispatcher task — cuts the stretches and scores hypothesis directories into
     `runs/der/`; `uindosill rttm` converts an Audacity label export.
   - **One convention finding worth reading before the gate is ratified.** pyannote's `collar` is a
     *total* width centred on the boundary — `collar=0.25` forgives 0.125 s either side — while NIST
     md-eval and NeMo quote a *half*-width, so a Sortformer model card's "collar 0.25" is this
     scorer's `--collar 0.5`. The benchmark the plan anchors to, arXiv 2509.26177, states it uses
     pyannote.metrics at `collar=0.25, skip_overlap=False`, and that is the headline convention
     here; the card numbers the proposed 10% was derived from sit on the other scale. Neither number
     is wrong; they are not on one scale, and the gate should say which it means.
   - **Five development stretches**, ten minutes each, are pinned in
     `tests/fixtures/diarisation/dev/stretches.json` — episode, onset, the exact ffmpeg line,
     ffmpeg version, byte count, and two SHA-256s (whole file, and PCM alone, because ffmpeg copies
     the episode's tags into the WAV header). `lab.ps1 der -Cut` re-creates and verifies them
     from the episodes at the repository root. Two from the two-host episode, one from each guest
     episode; onsets chosen by transcribing three candidate windows per episode and reading them
     for conversation over ad reads and for guests evidently present — text-only inference,
     recorded as such. The labelling guide is `tests/fixtures/diarisation/README.md`; no stretch is
     labelled yet, and labelling effort remains unmeasured.
   - **The seam** is in: `ISpeakerLabeller` in Core beside `ITranscriptionEngine`; a nullable
     `Speaker` on `TranscriptSegment` and `TranscriptWord`; `SpeakerAssignment` attributing words to
     turns and cutting segments where the speaker changes; every formatter naming the speaker when
     one is known and byte-identical to before when none is; `SubtitleCueBuilder` never merging a
     cue across a speaker change; a seventh format, `rttm`, writing the labeller's turns; a canned
     labeller for CI; the catalogue's `"task"` discriminator, so a diarisation entry can be
     installed through the same digest checks and never surface as a selectable ASR model. The
     opt-in shapes it: `transcribe --speakers` and a checkbox on the Transcribe tab, both off by
     default, and both honest about the fact that this build has no real labeller — the flag says so
     and stops, the checkbox is disabled with the reason. The suite grew from 359 to 451 tests.
   - **Not done, by design:** the sherpa-onnx and Sortformer spikes belong to the desktop, which is
     where the measuring half of the split runs; every DER, RTF and memory figure for a real
     candidate is still zero measurements.

   **The target domain widened on 2026-08-17 — meetings and web video beside podcasts, and the gate
   covers one of the three.** The maintainer named the feature's target as meetings, podcasts and
   YouTube. Everything written above that date describes podcasts alone, and so does every artifact
   under it: four podcast episodes, five podcast stretches, and a gate phrased around two hosts,
   which is not the shape of a meeting or a panel. That leaves the ≤ 10% figure covering one domain
   of three, the other two carrying no material, no reference labels and no gate. It is recorded
   here rather than closed: whether meetings and web video get gates of their own, or the gate is
   restated in terms that span all three, belongs to the ratification that has not happened.

   **What follows from it.** Hand-labelling does not scale to three domains at the plan's own
   estimate of thirty to sixty minutes per ten minutes of audio — an estimate still unproven, and
   one the maintainer declined to spend against on 2026-08-17, before the first stretch was
   labelled. Material for the two new domains therefore has to come from existing corpora carrying
   human, time-stamped references, and a survey of them was commissioned the same day; its product
   lives on the Drive with the rest of the research. The podcast set stays what it was — the
   deciding instrument for its own domain, and the only material this project controls end to end.
   Across all three domains the measurement count is unchanged: zero.

   **What the survey found, the same day.** Forty-three corpora surveyed, forty audited against
   licence text and live download pages. Meetings and web video are covered by free CC-BY material
   carrying human, time-stamped references: AMI, which is also the only corpus where several
   toolkits publish figures whose convention can be established at source, and which is effectively
   a four-speaker set and therefore cannot price the four-speaker cap; NOTSOFAR-1, whose measured
   39% overlap makes it the crosstalk instrument and whose far-field capture is the closest public
   proxy to what this product records; and VoxConverse for web video, whose measured 3% overlap
   makes it domain coverage rather than a crosstalk test. **Podcasts came back with nothing free and
   usable** — the one podcast-specific candidate and the Spotify set both failed, the latter
   withdrawn outright. The labelling declined for three domains is therefore not avoidable for one:
   podcast material remains only what this project labels itself. The survey also sharpened the
   convention finding above — one corpus carries published figures four-fold apart on convention
   alone, and a second scoring pass at collar 0, where half-width and total-width definitions
   agree, is what buys comparability. The report is on the Drive with the rest of the research.

   **The gate was restated on 2026-08-18, against corpora that exist.** Hand-labelling was declined
   that day — the ten-minute stretch first, then a two-minute pilot after it had been cut, pinned,
   transcribed and written up — and that closes the podcast route entirely, because the held-out
   set the old gate named would have needed labelling exactly as the development stretches did.
   *Held-out two-host podcast DER ≤ 10%* had become a criterion that could never be evaluated, and
   since v1.0 ships when packaging and the passed gate both land, v1.0 was gated on something that
   could not happen. What replaces it is scored on material this project already holds and has
   verified: **AMI** as the ranking corpus, because it is the only one where several toolkits
   publish figures whose convention can be established at source; **NOTSOFAR-1** for crosstalk,
   because AMI's 14.58% overlap is mild against its 39%; **VoxConverse** for web video and for
   speaker counts past four, which AMI cannot reach at 15 of 16 test meetings holding exactly four.

   **What the restatement costs, said plainly.** The gate no longer asserts anything about podcast
   audio — the domain this feature was first scoped to, and the one the four stratified episodes
   were sourced for. A pass now means a diariser is close to the state of the art on meetings and
   on web video, and it means nothing about two hosts talking over each other. That is a real
   reduction in what a pass is worth, recorded here rather than absorbed quietly. The 10% figure
   goes with the material it named; it was in any case derived from Sortformer model-card numbers
   on the half-width scale, which this document had already flagged as not the scale the gate is
   written in. The 5-point margin replacing it is a proposal awaiting ratification, and it is
   relative rather than absolute on purpose: what a shipping product has to answer is whether a
   local, redistributable pipeline lands near what the best available one would give the same user
   — not whether it clears a threshold picked before any measurement existed. The five podcast
   stretches stay pinned and cut, measurable for real-time factor and memory and not for DER,
   exactly as `stretches.json` says.

### After v1

**v2 is asking questions about a transcript** and **v3 is push-to-talk dictation.** v2 went in front
because it needs none of the Win32 surface below — it reads a transcript this product already
produces — while what it does need is a second native stack and an answer to a harder honesty
problem than v1 ever posed. Its open decisions are recorded in `docs/V2-ASK-THE-TRANSCRIPT.md` and none
of them is settled. Neither version starts before Phase 5 ships.

**A research workflow on offloading to the NPU — asked for 2026-08-16, deferred until it is
relevant.** The second machine carries an XDNA 2 NPU (`NPU Compute Accelerator Device`, PCI
`VEN_1022&DEV_17F0`, driver 32.0.20102.3930 of 2026-05-06), and nothing this product runs can
reach it: parakeet.cpp is ggml, and ggml's backend list, read at source that day, is cpu, blas,
cuda, hip, musa, vulkan, opencl, metal, sycl, openvino, cann, hexagon, zdnn, zendnn, rpc, webgpu,
virtgpu and et — no XDNA. The route would be `docs/ENGINE-CHOICE.md`'s escape hatch — ONNX Runtime
with a hand-written TDT decoder — under AMD's Vitis AI execution provider, reached either through
the Ryzen AI SDK (Python and conda) or through Windows ML (C#, the EP managed by Windows, 24H2 or
later, a driver-version window). AMD publishes a demo of exactly this on exactly this model
(`amd/RyzenAI-SW`, `Demos/ASR/Parakeet-TDT`, weights `istupakov/parakeet-tdt-0.6b-v3-onnx`):
conformer encoder on the NPU at BF16, LSTM decoder on the iGPU, mel on the CPU, static 15-second
chunks, a first run that pays a cached compile — and its README says RTF 0.023–0.030 on 16.5
minutes of audio, hardware unnamed. This laptop's Vulkan tier already measures RTF 0.035, so on
speed the ceiling is about 1.5× and even that is a cross-machine, cross-chunking comparison; what an
NPU actually buys is watts and a free CPU and GPU, which matters for an always-on stream and hardly
at all for a batch job that finishes a ten-minute file in ~21 s. Nothing was run; the marker is in
`docs/UNPROVEN.md` § *NPU offload*. **The maintainer asked to be reminded to run the study when it
becomes relevant, which is any of:** v3 dictation being planned; a battery, thermals or
keep-the-CPU-free question about the app; v1.0 shipped and the next research item being chosen; a
second inference stack (ONNX Runtime, Windows ML) proposed for any other reason. What the study
has to carry: BF16 with per-operator CPU fallback as a new state for the WER gate (the ONNX INT8
export collapsed silently); static shapes forcing a segment length against the segmenter's join
guarantee; hardware gating — no NPU on the desktop, the reason the Windows-native AI APIs were
rejected in `docs/V2-ASK-THE-TRANSCRIPT.md` § 1; the Ryzen AI runtime and `flexml-lite`
redistribution licences, unread; and that AMD's LLM-on-NPU path is ONNX Runtime GenAI hybrid or
the Lemonade daemon, both shapes v2 already rejected. Cheapest first measurement: AMD's own demo
on this laptop against the same ten-minute file, its RTF beside 0.035 — a dev-machine experiment,
not a shippable path. It runs under `CLAUDE.md`'s convention: the product to a dated Drive folder,
the decision record and the unproven markers here.

**Pinning the model digests used to head this list** and is done: all five entries carry the exact
byte size and the SHA-256 read from the repository's LFS listing, `"verified": true`, and no entry
needs `--allow-unverified`. `docs/MODELS.md` has the table. That settles *provenance* and settles
nothing about quantisation quality, which is what item 2 is for.
