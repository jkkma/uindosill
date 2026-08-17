# Speaker diarisation: the pre-v1 study

Run 2026-08-16, as `docs/PHASES.md` item 4 asked: a study, not a build. Eight research agents
surveyed the field, the runtimes, the licences, this repository's seam and the measurement
question; a second, adversarial pass then re-read every load-bearing claim at its primary source —
the licence text, the Hugging Face file listing, the NuGet page, the paper — on that date, and a
completeness critique ran over the whole. Where the verification pass corrected a claim, the
corrected version is what appears below. **Every DER and RTF in this document is somebody else's
number on somebody else's data, named as such. This project has measured nothing about diarisation
yet**; the dev/held-out podcast set described below is the instrument that changes that, and the
`docs/UNPROVEN.md` discipline applies to every figure here.

The shape being optimised for, unchanged from the plan: a **two-host podcast with overlapping,
disfluent speech**, robustness across two to five voices via guest episodes, the earnings-call
corpus as a robustness check only. Inference on **CPU for every machine** — the models are one to
two orders of magnitude smaller than the ASR. No Python at runtime. Models arrive as plain files
downloaded by URL and pinned by SHA-256, which makes **Hugging Face gating as decisive as the
licence itself**: a repo behind an accept-terms form cannot be vendored, whatever its licence says.

## What the study settled

1. **The consequential unknown is settled: Sortformer runs without NeMo.** No official ONNX
   export exists — the three NVIDIA repos hold `.nemo` files, plus, in the v2 repo only, an
   official `q8_0` GGUF (147,075,776 bytes) — but the export recipe is public and closed-out
   (NeMo issues #15077/#15536/#14733, an NVIDIA engineer's one-line workaround, Nov 2025), and
   community exports were verified file-by-file on 2026-08-16, including a 141 MB int8 of v2.1.
   The export takes mel spectrograms, not audio: a C# host owns the 128-bin log-mel front-end,
   the chunk/state bookkeeping and the sigmoid post-processing.
2. **Gating sorts the field as sharply as licences do.** Every official pyannote pipeline repo is
   gated (`speaker-diarization-3.1`, `segmentation-3.0`, `community-1` — all `gated: auto`, read
   2026-08-16); only the wespeaker embedding repo is not. The NVIDIA Sortformer repos are all
   ungated. sherpa-onnx redistributes the MIT segmentation model un-gated as a plain GitHub
   release asset, which its MIT licence permits — that dissolves the gate for the pipeline route.
3. **A packaged C# route exists today.** NuGet `org.k2fsa.sherpa.onnx` 1.13.5 (2026-08-11,
   Apache-2.0, win-x64 natives) exposes `OfflineSpeakerDiarization` with a maintained example.
   Its quality on overlap-heavy audio is unmeasured anywhere and one open issue reports poor
   crosstalk behaviour — the route is cheap to try and unproven where this product lives.
4. **Two candidate families are licence-dead for shipping**: every competitive DiariZen
   checkpoint and both Rev reverb models are non-commercial (Rev's also gated).
5. **Nobody's number is on this material.** No published DER exists for any candidate on podcast
   audio. The external benchmarks are telephone calls, meetings and web video; the dev/held-out
   set below remains the deciding instrument, exactly as the plan said.
6. **The architecture this study converges on already runs elsewhere.** Vernacula
   (github.com/christopherthompson81/vernacula) is a .NET 10/Avalonia app shipping Parakeet TDT v3
   with Sortformer as default diariser and DiariZen as an NC-labelled opt-in — independent
   evidence that the exact C#/ONNX stack contemplated here works, and a reference to read, not a
   dependency to take.

## The candidates

Licence and gating read at source 2026-08-16; DER columns name their dataset and convention
because the conventions differ enough to invert comparisons — a "Full" (no collar, overlap
scored) number can be triple the same system's 0.25 s-collar number, so nothing in one row is
comparable to another row without checking the convention first.

| Candidate | Licence (weights) | Gated | Best external evidence | Verdict |
|---|---|---|---|---|
| **Sortformer streaming v2** (nvidia, HF) | CC-BY-4.0 | no | v2.1 card, 30.4 s buffer, collar 0.25, overlap in: CALLHOME-part2 2/3/4-spk 5.65/10.03/12.33, CH109 5.04; v2 within ~1 pt on 2-spk | **lead candidate** — the clean licence choice |
| **Sortformer streaming v2.1** (nvidia, HF) | NVIDIA Open Model License | no | same card; only variant with a verified 141 MB int8 ONNX | licence read, terms below — v2 preferred until OML is read the way LICENSING.md reads licences |
| Sortformer offline v1 (nvidia, HF) | CC-BY-**NC**-4.0 | no | — | out: non-commercial |
| **sherpa-onnx pipeline** (pyannote seg-3.0 + embedding + AHC) | Apache-2.0 runtime; MIT seg; CC-BY-4.0 embedding | no (converted copies) | its docs: RTF 0.110–0.297 on a 56.9 s clip, one thread, hardware unnamed; DER vs pyannote 3.1 unmeasured, issue #1708 open on crosstalk | **first spike** — cheapest to run, quality unproven |
| pyannote 3.1 (official) | MIT | **yes** | card, "Full" (collar 0, overlap in): AMI-IHM 18.8, DIHARD-3 21.7, VoxConverse 11.3 | un-vendorable as published; its pieces reach us through sherpa-onnx/onnx-community mirrors |
| pyannote community-1 | CC-BY-4.0 | **yes** | README benchmark 2025-09, same convention: AMI-IHM 17.0, DIHARD-3 20.2, CALLHOME-pt2 26.7 | watch: CC-BY-4.0 makes an un-gated self-mirror legal; VBx clustering is real reimplementation cost; no proven ONNX of the full pipeline |
| DiariZen (BUT-FIT) | code MIT, **weights CC-BY-NC-4.0** | no | own card, collar 0: DIHARD3 14.5, AMI 13.9; holds to high speaker counts via VBx (cap 20) | out for shipping; possible NC-labelled opt-in later — a policy question, not a technical one |
| Rev reverb-diarization v1/v2 | non-commercial | **yes** | — | out twice over |
| MOSS-Transcribe-Diarize 0.9B (joint ASR+diariser) | Apache-2.0 | no | own card (cpCER, Chinese-heavy sets); GGUF runs in ggml `transcribe.cpp` at 1.6× real-time speed on a Ryzen 4750U per its card | out for v1: replaces the ASR, abandoning the measured Parakeet WER catalogue for self-reported numbers, at roughly an eighth of parakeet.cpp's measured CPU throughput; re-examine at v2 |
| pyannoteAI precision-2 | commercial cloud API | — | benchmark leader (arXiv 2509.26177: 11.2% DER, collar 0.25, overlap in) | out: cloud; useful only as the accuracy ceiling |
| mago-ai 8-spk Sortformer fine-tune | tagged Apache-2.0 over an NVIDIA-OML base | no | none — no benchmark exists | watch only: provenance unverified, relicensing legally murky |

The cross-dataset anchor is arXiv 2509.26177 (Sep 2025; CALLHOME, VoxConverse, AMI, AliMeeting;
196.6 h; pyannote.metrics, collar 0.25 s, overlap scored): pyannoteAI cloud 11.2%, DiariZen
13.3%, Sortformer v2-streaming comparable to DiariZen and fastest (its RTF figure is GPU-only).
Its diagnosis matters more than its leaderboard: the dominant error everywhere is missed speech
from imprecise onsets and offsets, and Sortformer's confusion explodes past four speakers.

**The 4-speaker cap is real and sits on this product's edge by design.** The v2.1 card says
performance degrades at five or more (DIHARD-3 ≥5-spk 38.90); a fifth voice is merged or lost by
construction. The five-voice guest episodes in the measurement set exist precisely to price that.

## The two routes, and two watched

**Route A — sherpa-onnx, the packaged pipeline.** Everything already exists: the NuGet, the C#
API, un-gated model files with checksums, `NumClusters = 2` for the known-two-hosts case and a
threshold knob for the rest. What is unproven is exactly the thing this product needs: behaviour
on overlapping speech (open issue #1708 reports poor crosstalk results with the same segmentation
model, unresolved). Its clustering is complete-linkage AHC with an untuned 0.5 default — simpler
than pyannote 3.1's centroid linkage with a tuned threshold (0.7046, min cluster size 12; read
from 3.1's `config.yaml` on 2026-08-16 through an HF account that has accepted that repo's gate —
those two thresholds are linkage-specific and do not transfer between pipelines).

**Route B — Sortformer over ONNX Runtime, called directly from C#.** The expected quality winner
on two-to-four-voice overlap-heavy dialogue, and the real engineering route: the host implements
the 128-bin log-mel front-end (NeMo's own exporter shipped an 80-vs-128 mel bug once — issue
#15536 — so the front-end must be validated against reference vectors, not assumed), the
arrival-order speaker-cache state across buffers, and threshold/onset-offset post-processing.
Export fidelity has been parity-checked only for v1 (max error < 0.0001, altunenes); **v2/v2.1
parity is unproven** — the one v2.1 export publishing a figure reports 0.985 decision agreement
against NeMo (soniqo), which is a report, not a guarantee. sherpa-onnx does not support
Sortformer (feature request #3497, open since 2026-04), so this route is ONNX Runtime directly —
`Microsoft.ML.OnnxRuntime`, MIT, a dependency nobody has sized against this app yet.

**Watched, not planned: NeMo-Speech.cpp.** NVIDIA's official native C++ runtime (Apache-2.0)
with a stable C SDK, documented Windows builds, CPU/Vulkan/CUDA backends, and
`nemo-speech diarize x.wav --model sortformer.gguf` emitting RTTM — architecturally the perfect
sibling to parakeet.cpp, and the official q8_0 GGUF already sits in the v2 repo. But it has zero
releases and six commits (2026-08-16). Re-check when the spike starts; adopt only if it has cut a
release, otherwise drop it from consideration without further evaluation time.

**Watched, contingent: a direct C# port of the pyannote-3.1-style pipeline.** The un-gated ONNX
pieces exist (onnx-community: segmentation 5,986,908 B, wespeaker embedding 26,535,549 B, int8
variants beside them) and centroid-linkage AHC is a few hundred lines with no maths library. Only
worth doing if the sherpa spike shows the packaged clustering is the weak link — that isolates
one variable instead of rewriting the world.

**How the routes get decided:** the dev-set DER delta between Route A and Route B, against the
engineering cost difference — Route A is days, Route B is the mel front-end, the cache port and
its validation. If A is within reach of B on the two-host dev material after tuning, A ships
first and B stays a v1.x upgrade; if A fails on overlap the way #1708 hints, B is the route and
the cost is paid once.

## The licence readings

To be re-read the way `docs/LICENSING.md` reads licences before anything is vendored; what
follows is what the study established at source on 2026-08-16.

- **Sortformer v2: CC-BY-4.0** (HF API, ungated). The obligations are the ones this project
  already implements to the letter for the Parakeet weights — the seven-element §3(a) notice via
  `CcByAttribution`, modification disclosure included (an ONNX re-export *is* a modification).
  This is why v2 is the clean choice: zero new licence machinery.
- **Sortformer v2.1: NVIDIA Open Model License.** The text was read 2026-08-16: models are
  "commercially usable", reproduction and distribution of the model and derivatives are granted,
  attribution requires a notice line ("Licensed by NVIDIA Corporation under the NVIDIA Open Model
  License"). It also carries a guardrail-circumvention termination clause and a
  litigation-termination clause. Nothing read forbids this app's use, but how automatic
  termination clauses sit inside an MIT product deserves the LICENSING.md treatment before v2.1
  specifically is vendored; v2 avoids the question entirely, at the cost of the newest weights.
- **pyannote segmentation-3.0: MIT, but the official repo is gated.** The sherpa-onnx converted
  copy carries the MIT LICENSE (© 2022 CNRS) and is redistributed un-gated; MIT permits exactly
  that. Vendoring the converted copy is licence-clean; the gate was the authors' data-collection
  form, not a licence term.
- **WeSpeaker ResNet34-LM embedding: treat as CC-BY-4.0, with an open question.** The pyannote
  and Wespeaker HF copies are tagged CC-BY-4.0 (ungated); WeSpeaker's docs say VoxCeleb-trained
  weights follow the VoxCeleb dataset's CC BY 4.0 — yet sibling Wespeaker repos are tagged
  Apache-2.0, and VoxCeleb's own licence-over-YouTube-audio provenance is a known grey area.
  Taking the strictest plausible reading (CC-BY-4.0, full notice package) costs nothing given the
  machinery exists; the inconsistency is recorded here rather than resolved.
- **DiariZen: weights CC-BY-NC-4.0** by the repo's MODEL_LICENSE (training data includes
  RAMC/MSDWild/DIHARD-3, which restrict commercial use). The lone MIT-tagged checkpoint
  (`diarizen-meeting-base`) predates that licence file and is weaker and meeting-domain; whether
  its MIT tag reflects intent is unconfirmed — ask before relying on it.
- **Rev reverb: non-commercial and gated.** The LICENSE text itself sits behind the gate;
  "non-commercial" rests on the sherpa-onnx conversion README and Rev's own docs page. Either way
  it is out.
- **TitaNet-S ONNX exports** float around (sherpa zoo and at least three third-party repos) but
  the NGC upstream licence was not read — unproven, and unneeded given the alternatives.

## The measurement plan

This section is the instrument the plan called for, sharpened by the study; it supersedes the
sketch that lived in `docs/PHASES.md` item 4.

**The set.** Several ten-minute stretches across more than one podcast — different speaker pairs,
same-room and remote, music beds and ad reads — and, within shows that have them, guest episodes,
so the set is stratified by speaker count: two hosts, then three, four and five voices with the
same hosts, microphones and habits held constant. That stratification isolates the variable that
separates the candidates and lands on the 4-speaker cap on purpose: the five-voice episodes
decide whether Sortformer needs a fallback behind it. **Split by show, not by stretch**: stretches
of one show share voices, microphones, rooms and beds, so a per-stretch split leaks show identity
into tuned thresholds and overstates robustness. Held-out must contain at least one entirely
unseen show; the speaker-count-stratified curve lives inside a dev show and is reported
separately from the held-out robustness number. Post-processing knobs (clustering threshold,
speaker-count bounds, activity thresholds, min-duration) may be tuned on dev only; the scoring
convention is never tuned; held-out is never relabelled after scores are seen. Any model
fine-tuning is NeMo/PyTorch on a GPU, outside this repo's toolchain, inside the ask-first rule,
on episodes disjoint from all evaluation.

**The labels.** RTTM turn files — one line per turn,
`SPEAKER <file-id> 1 <onset> <duration> <NA> <NA> <speaker> <NA> <NA>`, LF endings, three-decimal
invariant formatting — committed as fixtures beside the ffmpeg cut line that reproduces each
stretch's audio, without shipping a byte of anyone's recording. The cut must re-encode, never
`-c copy`: ffmpeg's `-ss` before `-i` is sample-accurate when transcoding but snaps to a seek
point under stream copy (ffmpeg docs, read 2026-08-16). So:
`ffmpeg -ss <onset> -t 600 -i <src> -ac 1 -ar 16000 -c:a pcm_s16le <stretch>.wav`, with the
ffmpeg version and the cut WAV's SHA-256 recorded in the fixture — bit-identical lossy decode
across ffmpeg versions is unproven, and the pin makes the question moot. Labelling method:
Audacity label tracks, one track per speaker with the speaker's name in the label text (export
merges all tracks into one tab-delimited file — a twenty-line converter yields RTTM); label each
speaker independently so overlap falls out for free; count back-channels as speech — hosts
back-channel constantly and a 0.25 s collar does not hide a 300 ms "yeah"; bridge intra-speaker
pauses under ~0.3–0.5 s. Labelling effort is **unproven** — the working estimate is 30–60 minutes
per ten-minute stretch; measure it on the first stretch and re-plan if it is wrong. CSB384 has no
speaker ground truth today; its ten-minute cuts are candidates for the first dev stretches.

**The scoring.** DER gates ship/no-ship. Headline convention: **collar 0.25 s, overlap included**
— the same convention as arXiv 2509.26177, so the one external comparison stays meaningful, and
the collar forgives evening-grade boundary jitter in hand labels. Log the strict collar-0 number
beside it, and — because the target is overlap-heavy audio — an overlap-region breakdown (DER and
miss computed over reference-overlap regions only), so the headline concern is measured directly
rather than averaged away. The optimal speaker mapping is required (greedy mapping is not DER);
at ≤5 reference speakers brute force over ≤120 permutations is exact, so the scorer is a few
hundred lines of C# inside the existing harness shape — a `measure-der.ps1` beside
`measure-wer.ps1`, one `lab.ps1` dispatcher entry, `runs/` output — validated once against
pyannote.metrics at the benchmark's exact settings on committed fixture pairs before its numbers
are trusted. Speaker-attributed WER (cpWER: concatenate per-speaker utterances, WER over all
speaker permutations, take the minimum — CHiME-6 §4.6) is the corroborating number on the
earnings corpus only, whose per-token `speaker` column makes it nearly free **once the column's
population is verified** — `corpus/` is gitignored and absent from this tree, `uindosill wer`
discards the column today (`WerCommand.cs`), and the "4 speakers on one call, 17 on another"
counts are prose no one here has re-counted. The podcast gate never depends on cpWER, because
podcast labels are turns, not transcripts, and cpWER would let an ASR regression move a
diarisation gate.

**The spike, in order.** CPU-only, on both machines, before any product code:

1. **sherpa-onnx spike (days).** NuGet `org.k2fsa.sherpa.onnx` 1.13.5;
   `sherpa-onnx-pyannote-segmentation-3-0.tar.bz2` (6,958,444 B, GitHub release
   `speaker-segmentation-models`); `wespeaker_en_voxceleb_resnet34_LM.onnx` (26,530,550 B,
   release `speaker-recongition-models` — the typo is in the real tag), with 3D-Speaker CAM++
   (29,596,978 B) as the A/B embedder. Score the dev stretches at `NumClusters = 2` and with a
   threshold sweep; record RTF and peak working set beside the ASR's. The only external CPU
   priors are the project docs' 0.110–0.297 RTF on unnamed hardware and one user's 0.2 on an
   i7-1165G7 — treat both as order-of-magnitude.
2. **Sortformer ONNX spike (the real candidate).** Regenerate the export from
   `nvidia/diar_streaming_sortformer_4spk-v2` rather than trusting a third-party file: the
   export script is published (`altunenes/parakeet-rs`, `scripts/export_diar_sortformer.py`, MIT
   OR Apache-2.0), the NeMo version and the `concat_and_pad()` workaround get pinned in the
   generator note, and the resulting file is digest-pinned and hosted where the project can
   guarantee it (an HF repo of the maintainer's, or a GitHub release — `models.json` needs a
   plain https URL; nothing multi-hundred-MB enters this repo). Archive the source `.nemo` and
   its digest at the same time — today's un-gated status is a snapshot, not a promise. Validate
   the C# 128-bin log-mel front-end against reference vectors from the export environment before
   believing any DER; run the batch 30.4 s-buffer configuration first (the card's best numbers,
   and this app transcribes files, not calls); port the arrival-order speaker-cache update and
   test identity stability across a full 2–3 h episode — a subtly mis-ported cache silently
   swaps the two hosts mid-file, which is the worst failure this feature can have. CPU prior:
   one export reports 64× real time at 12 threads on a 24-core x86 (soniqo, 2026-08); nothing
   laptop-class exists. int8-vs-fp32 goes on the dev set as its own A/B — the plausible ship
   artifact is the ~140 MB int8, and its DER cost is unmeasured.
3. **Decide.** Dev-set DER delta between the two routes against their engineering cost, then the
   held-out number on the chosen route against the ratified gate.

**The gate, proposed.** To be ratified by the maintainer *before* the held-out set is scored, so
the bar is not chosen after seeing the number: **held-out two-host DER ≤ 10% (collar 0.25 s,
overlap included) ships the feature**; the 3–4-voice strata reported beside it with no stratum
above 20%; the five-voice stratum reported with a named behaviour — graceful degradation means
the four dominant voices keep stable identities and the fifth merges into one of them, not
identity churn across the file. The anchor for 10%: Sortformer's home-turf two-speaker numbers
are 5.04–6.65 (CH109/CALLHOME, its card's convention), and doubling a home-turf number is the
allowance for domain shift to wide-band podcast audio with beds and ad reads. If five-voice
episodes matter in practice and the cap fails them, the fallback decision (clustering route
behind Sortformer, an NC-labelled DiariZen opt-in — a maintainer policy call — or shipping the
cap with honest UI) is taken on that evidence, not before it.

## The seam, mapped

Concrete integration points, from the repository read (file:line as of 2026-08-16):

- `ISpeakerLabeller` lives in Core (`EnsureCoreHasNoPackageDependencies`,
  `src/Parakeet.Core/Parakeet.Core.csproj:27` — enforced, so ONNX Runtime or sherpa-onnx go in a
  new sibling of `Parakeet.Engine.ParakeetCpp`; `docs/ARCHITECTURE.md:16` calls that project
  "the only project with native interop", which a second native project falsifies — update it
  when building).
- **Both `IAudioSource` implementations are single-read** and throw on a second `ReadAsync`
  (`WavAudioSource.cs:300`, `MediaFoundationAudioSource.cs:65`). A diarisation pass reopens via
  `AudioSources.Open(path)` or the pipeline tees the float stream; a multi-hour file therefore
  decodes twice unless teeing is built. Word times are file-relative when segments are yielded
  (`SegmentingTranscriptionEngine.cs:164`), so speaker-to-word assignment is a pure post-step.
  The CLI buffers the full stream (`TranscriptionRunner.RunAsync`) — a natural merge point; the
  App renders segments live (`TranscribeViewModel.cs:337`), so second-pass labels need a
  late-update path in the UI that does not exist yet.
- A `Speaker` field on `TranscriptSegment`/`TranscriptWord` composes cleanly (records, `with`
  through `Shift()`), then touches all six formatters (`TranscriptFormats.All`) — the JSON
  formatter's hand-written schema makes `"speaker"` a deliberate contract change —
  `SubtitleCueBuilder` (a cue should not merge across a speaker change; the word-timed VTT
  strict-inside-cue rule constrains per-word markup), the fake engine (a deterministic fake
  speaker sequence for CI), and the tests: 78 test methods in the three segment-constructing
  Core files plus 23 CLI end-to-end tests assert on today's shapes. Word-level policy during
  overlap (dominant speaker at word midpoint vs dual attribution) is a build-time design
  decision, noted, not taken.
- A second native follows the parakeet.cpp pattern in five registration places:
  `scripts/vendor-natives.ps1` pins, the `docs/NATIVE-BINARIES.md` digest table (the script
  fails if they disagree), the `build/NativeAssets.targets` glob, a `doctor` child-process probe
  (AVX-baseline crashes want the same isolation the parakeet backends needed), and
  LICENSE-beside-binary plus an `Attribution.Components` entry so `uindosill notice` and the
  Licences tab render it. **Tension to resolve at build time**: sherpa-onnx's natives arrive
  inside its NuGet package, which bypasses the vendor-script/digest-table convention — either
  vendor the DLLs out of the release archives by URL+digest to keep the convention, or record
  the NuGet package version+hash as the pin; and note the sherpa package ships its own ONNX
  Runtime, which collides with `Microsoft.ML.OnnxRuntime` if both routes load in one process
  during A/B spikes — spike them in separate processes.
- The model catalogue takes diarisation files mechanically (URL, sizeBytes, sha256, licence,
  attributionId all fit) but has no task discriminator and `Recommended` falls back to
  `Models.First()` (`ModelCatalog.cs:39`) — a `"task"` field is parse-compatible (the parser
  ignores unknown fields) but old builds would surface diarisation entries as selectable ASR
  models; a discriminator or a parallel list is a schema decision to take when building.
  Transcript provenance should carry the diariser model id and digest the way it carries the
  ASR's — no schema slot exists today.
- When segments carry speakers, `docs/V2-ASK-THE-TRANSCRIPT.md` § *Not in v2: who said it* flips
  from "never name a speaker" to "name exactly what the transcript carries" — that section holds
  until this feature ships and must be revisited the day it does.

## Risks the plan carries deliberately

- **Music beds and produced ad reads**: segmentation false alarms on music with vocals, and a
  third ad-read voice inflating speaker counts — the label set includes such stretches on
  purpose, and the labelling guide needs a written policy for sung/produced speech before the
  first stretch is labelled.
- **Remote-guest audio**: VoIP codecs and noise suppression are neither CALLHOME telephony nor
  VoxCeleb; the "remote" stretches in the set are the only evidence that will exist.
- **Front-end mismatch is silent**: a wrong mel configuration or resampler degrades DER without
  any error — NeMo's own exporter shipped an 80-vs-128 mel bug. Reference-vector validation is
  not optional.
- **Memory beside the ASR**: ~505 MB resident for fp32 Sortformer in ONNX Runtime (one report),
  ~150 MB for int8/q8_0 — a combined peak-RSS number on the laptop belongs in the spike results.
- **Third-party artifacts are evanescent**: single-user HF repos can vanish or re-upload
  different bytes; digest pins then fail closed. The plan already answers this — regenerate,
  self-host, archive the `.nemo` — but the answer only works if done at spike time, not ship time.
- **A stereo shortcut goes unexamined**: two-host episodes mastered with panned voices are
  separable per channel before any model runs, and the current mono downmix discards that. One
  session of checking whether the target shows publish usable stereo is cheap and could change
  the two-host problem entirely; guest episodes would still need the model.
- **End-to-end time budget is unset**: ASR + a second decode + diarisation + assignment is a real
  multiplier on today's measured RTF 0.079–0.10; what slowdown is acceptable before speakers ship
  is a product decision the spike numbers should provoke, not answer.

## The recommendation, and the decision

The study's recommendation was v1.1: build Phase 5, apply to SignPath, run the first spike during
the signing wait, ship v1.0 when signing lands, let speakers ride the auto-update once the
held-out number clears the ratified gate — because no route has a measured number on the target
material, and shipping v1.0 behind that measurement would gate a working product on the one thing
this project refuses to do, which is claim a number nobody has measured.

**The maintainer decided otherwise the same day, 2026-08-16: diarisation ships in v1.0, as an
option the user turns on.** Recorded with the recommendation it overrode left intact above, so
the two stay distinguishable. Two more facts arrived with the decision. The feature is **opt-in**
in the product, not always-on. And the maintainer supplied test material the same evening: four
full episodes, uploaded to the maintainer's Drive (no ids here — this repository is public),
whose filenames carry the stratification the measurement plan asked for — two hosts alone, then
the same two hosts plus one, three and five guests: nominally 2, 3, 5 and 7 voices. That brackets
the 4-speaker cap from both sides, and the 7-voice episode is a harsher stress than the plan's
own 5-voice ceiling. Everything about them beyond the filenames — actual speaker counts, same-room
versus remote guests, beds and ad reads — is unverified until they are heard, and audio alone is
not ground truth: the RTTM labelling pass over cut stretches is still the work the plan says it
is. One gap the supplied set does not close: all four appear to be one show, so the stratified
dev curve is covered, but the held-out-robustness claim still wants at least one entirely unseen
show later.

What the decision changes, and what it cannot:

- **v1.0 now gates on the measurement.** Signing left the critical path the same day — the
  maintainer also decided v1.0 ships unsigned (`docs/PHASES.md`, *Decisions taken 2026-08-16*,
  decision 2) — so the release ships when Phase 5's Velopack packaging and the held-out number
  passing the pre-ratified gate both land. The decision moves the feature into v1.0; it does not
  waive the gate — the decision was to ship speakers, not to ship a claim, and an opt-in that
  mislabels speakers breaks the product's one rule as surely as a default would.
- **The critical path inverts.** Cutting and labelling stretches from the supplied episodes, and
  the sherpa-onnx spike, are the front of the queue — there is no signing wait left to fill — in
  the order §*The spike, in order* already fixes. The DER harness and the seam work need no
  labels at all and can start immediately.
- **The integration build joins v1 scope**: everything §*The seam, mapped* lists — the Core
  interface, the speaker on segments and words, six formatters, the fake engine and the tests,
  the second native drop with its doctor probe, the Phase 5 packaging entry.
- **Opt-in shapes the seam.** A transcribe-time option — a CLI flag and an app setting, off by
  default; the diarisation model files are an on-demand download when the option is first
  enabled, not part of the default install, the same shape the CUDA-tier decision took. That is
  the concrete reason the catalogue needs its task discriminator: a diarisation entry must be
  installable without ever surfacing as a selectable ASR model. The second decode and the
  diariser's memory are costs only an enabled option pays.
- **The five-voice-and-beyond fallback becomes a v1.0 decision** instead of a v1.x one if the
  guest episodes defeat the 4-speaker cap — and the supplied 7-voice episode exists to force
  that question early: clustering route behind Sortformer, an NC-labelled DiariZen opt-in (a
  maintainer policy call), or shipping the cap with honest UI.
