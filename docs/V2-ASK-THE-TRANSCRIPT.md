# v2 — asking questions about a transcript

Nothing here is built. This is the decision register: what v2 is for, the one property that makes
it harder than v1, and the questions that have to be answered before any code is written. Where a
question has evidence behind it the evidence is here; where it does not, it says so.

## What it is

**A chat panel beside a finished transcript.** You ask a question in your own words; you get an
answer whose every claim carries a clickable timestamp back into the recording.

Not a summary button. Summarization is one question you can ask — *"what are the main topics?"* —
and treating it as the whole feature gets the shape wrong. The reference for this is YouTube's
"Ask about this video": a few suggested questions to start from, a free-text box, and answers
built as short labelled bullets with inline ranges like `16:42 – 20:50` or a single point like
`45:05`.

That reference was observed against **CSB384**, which is the same episode as `CSB384.mp3` in this
repository's root — the 2:55:23 file behind every three-hour figure in `docs/UNPROVEN.md`. So a
comparison already exists: 1,488 segments and 29,926 words of that episode have been transcribed
here, and the same questions can be put to both. That is not a benchmark and there is no scoring
rubric behind it, but it is a concrete thing to look at on day one rather than a hypothetical.

### What the interaction has to do, from the reference

- **Take an arbitrary question**, not a fixed menu. The menu is an entry point for people who do
  not yet know what to ask.
- **Answer with citations attached to individual claims**, not a bibliography at the end. A bullet
  without a timestamp is a bullet nobody can check.
- **Cite ranges and points**, whichever the claim needs — a topic spans minutes, a specific remark
  does not.
- **Make the citation clickable**, seeking the audio. A citation that has to be typed into a
  seek bar by hand is one nobody will follow.
- **Stay open**. The value is in the second and third question, which is what makes this a panel
  rather than a button.

### The citation primitive already exists

Half of it does. `vtt-words` carries the timestamps and `scripts/preview-words-vtt.html` already
highlights the current word as audio plays, so mapping a time to a place in the text is solved.

**Seeking is not.** That page only reads `player.currentTime`; it never assigns to it, so nothing
in this repository turns a click into a jump. `Parakeet.App` has no audio playback at all — no
player, no transport, no seek — and `Parakeet.Audio`'s Media Foundation reader decodes for
transcription rather than playing anything. So the citation being *correct* is nearly free here,
and the citation being *clickable* is new surface. Worth sequencing first: a transcript you can
click to hear is useful before any language model is involved.

### The model never writes a timestamp

**A rule, taken from reading and not yet from running anything.** The language model cites by an
opaque segment id — `[S12]`, `[S12-S15]` — and never by a time. The app resolves each id to a
`TranscriptSegment`'s `Start` and `End`, renders the range, and makes it seek. An id that does not
resolve, or a claim that carries none, is rendered as unresolved (`[?]`) or refused; it is never
rendered as a bare timestamp a reader might take for a real one. Where the model can be
constrained to emit only ids that exist — a grammar enumerating the live ids, with a production
for *not in the recording* — it is.

Why: a model-written timestamp is a fluent number in the exact shape of a checkable one, which is
the failure described in the next section in its purest form. The one open-source local
implementation found that gets citations right — `PaulBratslavsky/yt-local-llm-knowledge-base`,
MIT, TypeScript over an Ollama daemon — states the rule in its README ("Timecodes are
deterministic, not model-generated. The model is explicitly instructed NOT to emit timecodes in its
output") and recovers each section's time by BM25 against the transcript chunks after generation.
The reference product's own help page says "Quality and accuracy may vary." Both read on
2026-08-15; neither is a measurement made here, and no measurement of citation precision on any
quantised open model was found. What is worth copying from that repository is this discipline,
not its runtime.

What the rule does to the decisions below: decision 1's binding has to expose grammar-constrained
decoding, which is why the comparison there tracks GBNF; decision 3's retrieval hands back the ids
the model cites, so a retrieved segment is citable by construction; and decision 6's strongest test
— every citation resolves — stops being a check run afterwards and becomes the mechanism.

### Not in v2: who said it

**Speaker attribution is a non-goal for v2.** The pipeline has no notion of a speaker:
`TranscriptSegment` carries `Start`, `End`, `Text` and `Words`, and nothing under `src/` produces
or stores a speaker label (checked 2026-08-15). The catalogue in `models.json` is transcription and
end-of-utterance models; none of them diarises. So a question of the form *"who said X?"* is
answered with when it was said and what was said — a range and a quote — or refused. **The model
must never name a speaker the transcript does not carry**, and the answer must not imply one. The
discipline is the same as for timestamps above, and the failure would look the same: a fluent,
plausible attribution nobody can check.

Every neighbouring product surveyed in the maintainer's v2 research notes labels speakers, and
questions of this shape will arrive on the first day. Recorded here so that the panel's answer to
them is designed rather than improvised, and so nobody builds towards diarisation by accident: it
is another model — its own native, its own licence gate, its own place in decision 4's residency
budget — and it is out of scope until v2 has shipped what it can already cite honestly.

## Why this is harder than v1, and it is not the modelling

**A transcript can be wrong loudly. A summary cannot.**

When the ASR mishears, the output is visibly odd — a mangled proper noun, a word that does not fit
the sentence. A reader notices. When a summarizer is wrong it produces a fluent, plausible,
well-formed bullet that says something the speaker never said, and there is nothing in the text to
notice.

This is not a hypothetical risk borrowed from elsewhere. `docs/UNPROVEN.md` already records this
project's own instance of the shape: the analogous ONNX INT8 Parakeet export measured **24.8%
long-audio WER against 7.8% for fp32**, and it collapsed *silently*, producing fluent wrong text
rather than obvious garbage. That was a failure mode for the ASR. For a summarizer it is the
default behaviour, not the failure mode.

And the discipline this repository runs on does not transfer. Every claim here is either measured
or explicitly marked unproven — but there is no WER for "is this summary right". The honest
position is that summary *quality* will be unmeasured for the same reason quantisation quality is
unmeasured, and unlike quantisation there is no obvious harness that would fix it.

Hence citations. They do not make an answer correct. They make it **checkable**, which is the most
this project can honestly offer, and they are the difference between a feature and a liability.

**A question-and-answer panel suits that discipline better than a summary button does**, which is
worth stating because it is a reason to prefer this shape rather than merely a description of it. A
summary implicitly claims completeness — *these are the main points* — and completeness is exactly
what cannot be checked. An answer to a specific question claims much less, and it puts the reader
in a position to check the one thing it does claim: they asked, they got a timestamp, they click it
and hear whether it holds. Failure is one click from being caught rather than invisible.

## What already exists that helps

- The transcript arrives as timestamped segments — 1,488 of them on the three-hour file — so the
  unit a citation points at already exists and is already verified. `docs/UNPROVEN.md` records that
  every segment boundary lands on the analysis-frame grid across three hours.
- Word-level timings exist too (`vtt-words`), so a citation could be tighter than a segment if that
  ever turns out to matter.
- `Parakeet.Core` takes no dependencies and the build enforces it, so a summarizer engine goes
  behind an interface in Core exactly the way `ITranscriptionEngine` does, in its own project.
- The vendoring pattern is established: `native/<rid>/<backend>/`, a pinned release, a recorded
  SHA-256, a fallback chain, and `docs/NATIVE-BINARIES.md` explaining all of it. llama.cpp is the
  same ggml family, so every trap already paid for on the ASR native applies unchanged.

## What it actually costs

Not the C#. **A second native stack**: its own vendored binaries per backend, its own pinned
release and digest table, its own ISA-baseline question, its own version of every gotcha the CUDA
work just went through. Budget for that, not for the code.

Sizing, on the machine this project is developed against (16 GB VRAM): the candidate decision 2 now
names is 8.87 GiB at Q8_0, plus 1.25 GiB of cache for three hours of transcript, on top of the
1.34 GiB ASR model. On paper both fit at once; whether they do is decision 4.

Three hours of transcript is roughly 30k words, about 40k tokens. That is either a long-context
model or a map-reduce, which is decision 3.

## The open decisions

### 1. Bindings: LLamaSharp, hand-rolled P/Invoke, or `llama-server` as a child process

**Measured, 2026-08-14, from the NuGet v3 API.** LLamaSharp is at **0.27.0**, and the three
backends this project would care about all ship at that same version:

| Package | Latest | `.nupkg` size |
|---|---|---|
| `LLamaSharp` | 0.27.0 | 368,708 bytes (0.4 MB) |
| `LLamaSharp.Backend.Cpu` | 0.27.0 | 36,337,071 bytes (36.3 MB; all RIDs in one package) |
| `LLamaSharp.Backend.Vulkan` | 0.27.0 | 48 KB metapackage |
| `LLamaSharp.Backend.Cuda12` | 0.27.0 | 48 KB metapackage |
| `LLamaSharp.Backend.Cuda11` | **0.24.0** | — stopped; CUDA 11 is no longer carried forward |

The two GPU backends are metapackages that pull a per-RID package apiece —
`LLamaSharp.Backend.Vulkan.Windows` at **20,194,168 bytes (20.2 MB)** and
`LLamaSharp.Backend.Cuda12.Windows` at **224,196,120 bytes (224.2 MB)**. Sizes are the
`Content-Length` of each `.nupkg` on the NuGet flat container, read 2026-08-15. An earlier revision
of this table said 35 MB, 19 MB and 214 MB; those were the MiB figures (34.7, 19.3 and 213.8) with
the wrong unit on them. Corrected here rather than quietly rewritten, because the numbers beside
them are in MB.

**The CUDA package has now been looked inside, and the size comparison an earlier draft drew from it
does not survive that.** It ships **no cudart** — it finds the runtime through `%CUDA_PATH%`, so it
needs a CUDA Toolkit installed on the machine, which is exactly the install this project's own CUDA
tier avoids by vendoring the 553 MB cudart archive; and its natives are built with CUDA 12.4.0 and
carry **no `sm_120`**, so the RTX 5080 is not among their native targets (what they do carry, and
what that costs on this card, is under *CUDA on the RTX 5080* below). Its natives are llama.cpp
**b8816** (2026-04-16), four months behind upstream on the day this was read. So the honest scale
against the stack already vendored here is: Vulkan comparable (20.2 MB against
`parakeet-v0.5.0-lib-win-vulkan-x64.zip` at 17.1 MB), and CUDA **not smaller** — 224 MB without a
runtime against 149 MB plus 553 MB with one; the cost has moved onto the user's machine rather than
gone. Read from `SciSharp/LLamaSharp` at tag `v0.27.0` and the package contents on 2026-08-15; none
of it was run. A download size is still not a measurement of anything that matters.

**The objection in `docs/NATIVE-BINARIES.md` still stands, but it is narrower than it looks.** That
document argues against natives arriving through a channel on somebody else's schedule: "a build
that follows tags takes whatever an untested-on-Windows release produced". A NuGet package version
is immutable and pinned exactly in `Directory.Packages.props`, so the *schedule* objection is
answered. What is not answered is provenance: this repository has **no `packages.lock.json` and no
`RestorePackagesWithLockFile`**, so no NuGet content is digest-checked today, while every native
under `native/` is. Turning lock files on would close that gap and is worth doing regardless of
this decision.

The real cost of hand-rolling is upstream churn. `llama.h` on master is **1,629 lines** against
parakeet.cpp's much smaller C ABI, and it changes far more often.

The version story is the part that matters, and it is a difference of kind rather than of presence.
llama.cpp does expose `LLAMA_API const char * llama_version(void)` — but it returns a **version
string, not an integer ABI number**, and it is weaker than even that sounds. **Measured, 2026-08-15,
on the laptop:** `llama.dll` from the upstream Windows CPU release `b10448`, called through
P/Invoke, returns **`"0.1.0-dev"`** — and that is the string on every release build, not just this
one (read on 2026-08-15; measured here on one). So it does not identify the build, let alone the
contract; the release tag in the zip's file name is more information than the function is.
`parakeet_capi_abi_version()` returns a value the binding compares against the ABI it was compiled
for and refuses loudly on a mismatch, which is the single check that makes the existing interop safe
to pin. Nothing on the llama.cpp side plays that role, so a guard, if there is to be one, has to be
built here — hash the DLL set against a pin table, and compare `llama_model_default_params()` /
`llama_context_default_params()` against recorded values before loading anything — whichever
binding is chosen. (`LLAMA_SESSION_VERSION` and `LLAMA_STATE_SEQ_VERSION` exist too, and version
the state-serialisation format rather than the C ABI.) So a hand-rolled binding would be chasing a
much larger moving header with no guard against it moving underneath.

Read from `include/llama.h` at `ggml-org/llama.cpp` master on 2026-08-14. An earlier draft of this
file said there was no version entry point at all, which was wrong; a later one said the string
identifies the build, which was also wrong.

#### The binding was tested against one specific capability: MoE offload

A mixture-of-experts model replaces each layer's feed-forward block with many parallel experts and
a router that picks a few per token, so **total parameters set the memory bill and active
parameters set the compute bill**. A 30B-total model might compute with 3B per token. Offloading
exploits that gap: attention, KV cache and the router stay in VRAM because every token touches
them, and the expert weights — most of the parameters — sit in system RAM, with only the selected
slice read per token.

It matters here because it moves the quality ceiling. 16 GB of VRAM caps a dense model at roughly
14B at Q4 once the KV cache has its room. With expert offload a 30B-total, 3B-active model becomes
reachable on the same card, with the bulk resident in system RAM.

**Checked, because it is exactly the kind of capability a binding can silently lack.** LLamaSharp
**v0.27.0** — the released tag, not master — exposes it as a first-class managed API:

```csharp
// LLama/Abstractions/IModelParams.cs
/// Equivalent to --override-tensor or -ot on the llama.cpp command line
/// or tensor_buft_overrides internally.
List<TensorBufferOverride> TensorBufferOverrides { get; }

// LLama/Abstractions/TensorBufferOverride.cs
public string Pattern    { get; set; }   // regex over tensor names
public string BufferType { get; set; }   // "CPU", "GPU0", "GPU1"
```

so `-ot "exps=CPU"` becomes `new TensorBufferOverride("exps", "CPU")`, and the interop struct wires
through to `llama_model_tensor_buft_overrides` at `llama.h:312`. `--n-cpu-moe` has no separate
counterpart and needs none: upstream it is a convenience that expands into the same tensor patterns.
Writing those patterns by hand is a per-model tuning burden rather than a missing feature, because
tensor names vary by architecture.

**One real gap, and it is instructive.** `LLamaModelParams` carries the device list as
`private IntPtr devices` with the comment `todo: add support for llama_model_params.devices`. That
is a different feature — choosing which backends participate — and it is not surfaced. Probably
irrelevant on a single-GPU machine. It illustrates the actual risk of taking a binding, which is
not that it lags upstream by a version but that it can carry a field it does not expose, and you
find that out by reading the struct.

**And it introduces a measurement problem this project does not currently have.** With experts in
system RAM, throughput becomes a function of **memory bandwidth** rather than of the GPU. Rough
arithmetic and not a measurement: DDR5-6000 dual channel is about 96 GB/s theoretical, and roughly
1 GB of expert weights read per token puts a ceiling somewhere in the tens of tokens per second,
with real throughput well below its own ceiling. The machine these figures would be taken on has
DDR5-6000 CL28; a user on DDR4 gets a materially different experience from the same build, with
nothing on screen to say why. That is gotcha 20's shape — a number that measures the machine rather
than the software — on an axis that appears in no machine table in this repository. If MoE offload
is used, RAM speed and channel count join the machine block in `docs/UNPROVEN.md` before any
throughput figure is quoted.

Read from `SciSharp/LLamaSharp` at tag `v0.27.0` and from `ggml-org/llama.cpp` master on
2026-08-15.

**Recommendation, which is a recommendation and not a decision — and it has changed.** An earlier
revision of this paragraph said LLamaSharp, and gave as the deciding fact that a hand-rolled binding
would lack an ABI guard. That fact is gone, because nothing on either side has one. LLamaSharp's
structs match its own natives and nothing checks that they do — the four-month lag above already
spans layout changes: between `b8816` and `b10448`, `llama_model_params` traded `use_mmap`,
`use_direct_io` and `use_mlock` for `load_mode` and `load_mtp`, and `llama_context_params` grew
five fields (read from `include/llama.h` at both tags, 2026-08-15), and nothing about the next such
change will announce itself — and `llama_version()` cannot be made to guard anything. So the guard
gets built here whichever way this goes, and the choice is between two ways of needing it rarely,
not between having one and not.

That opens a third option the earlier draft did not have in view: **`llama-server` as a bundled
child process.** The same upstream release zips, vendored under `native/` with the same pin, digest
and LICENSE-beside-the-binary rules as parakeet's; the app starts `llama-server.exe` on `127.0.0.1`
with a random port and an api-key, talks HTTP to it, and kills it on exit. Measured from the
`b10448` Windows CPU zip on 2026-08-15: `llama-server.exe` is a 9 KB stub over
`llama-server-impl.dll` (10.0 MB) and `llama-common.dll` (8.1 MB); with `llama.dll`, ggml, every
CPU variant, OpenMP and `mtmd`, the server needs 41.4 MB across 22 files; the whole zip is 47.3 MB
in 51 files, and it ships no LICENSE file, so the MIT text travels from the source tree at the
pinned commit. What it buys: no struct layout crosses the process boundary, and the REST surface
changes far less often than `llama.h`; the pieces that live in llama.cpp's `common/` rather than in
`libllama` — JSON-schema-to-grammar, the chat-template-aware `/v1/rerank`, slot save and restore —
come with it, where an in-process binding gets GBNF and nothing else; and the `GGML_VK_*` knobs are
a child environment, so gotcha 21's `_putenv` path is not needed. What it costs: a process lifecycle
to own (start, health-check, notice a crash, kill on exit), a loopback socket, and an unsigned
`.exe` that SmartScreen would notice where a DLL loaded in-process would not. This is not the Ollama
objection below: nothing is installed, nothing listens off the machine, and the process does not
outlive the app.

LLamaSharp keeps what it had — the offload API above, streaming, GBNF, embeddings, KV manipulation
— and it will load natives this project vendors itself, through
`NativeLibraryConfig.All.WithLibrary(...)`, so the pin, digest and LICENSE rules apply to it too.
But its structs are the ones its own natives were built with; vendoring a newer upstream build
under it is the struct-layout risk, not the fix for it. So under LLamaSharp the pin is to
LLamaSharp's release, and the natives are as old as it is.

#### CUDA on the RTX 5080, read off the binaries — and it decides between them

**The desktop tier runs on CUDA.** The maintainer's requirement, stated 2026-08-16. Vulkan stays the
portable default and the laptop's only path, exactly as `docs/NATIVE-BINARIES.md` has it for the
ASR tier — this narrows the desktop question, not the policy.

What upstream ships, read from the GitHub releases API on 2026-08-16 — release **b10448**, published
2026-08-15T20:48Z, the latest on that day: two Windows x64 CUDA zips, `cuda-12.4`
(250,791,166 bytes) and `cuda-13.3` (146,699,660 bytes), each with a runtime zip beside it
(`cudart-llama-bin-win-cuda-12.4-x64.zip` 391,443,627 bytes; `…-13.3-x64.zip` 390,970,417 bytes),
plus `vulkan` at 34,807,759 and `cpu` at 18,464,245. Those are download sizes; the CPU zip is the
one that unpacks to the 47.3 MB measured above.

How they are built, from `.github/workflows/release.yml` at that tag: `-DGGML_BACKEND_DL=ON
-DGGML_NATIVE=OFF -DGGML_CPU=OFF -DGGML_CUDA=ON`, with **no `CMAKE_CUDA_ARCHITECTURES`**, so ggml's
default in `ggml/src/ggml-cuda/CMakeLists.txt` decides: for a toolkit below 13, `50/61/70-virtual`;
always `75-virtual 80-virtual 86-real`; from 11.8, `89-real 90-virtual`; **from 12.8, `120a-real`**;
from 12.9, `121a-real`. So the `cuda-12.4` zip cannot carry Blackwell code — on this card it would
run, if at all, through a driver JIT of `compute_90` PTX — and the `cuda-13.3` zip should carry
`sm_120a`. The CUDA job packs `ggml-cuda.dll` alone, and the release job then unpacks the CPU zip
into every `llama-bin-win-*-<arch>.zip`; the CUDA zip is therefore the whole CPU drop with one DLL
on top, and needs nothing but its cudart zip beside it.

**Scanned, 2026-08-16, on the laptop**, which is the second machine and has no CUDA device — so this
is a reading of the file, not a run. `llama-b10448-bin-win-cuda-13.3-x64.zip` downloaded to
146,699,660 bytes, matching the API, SHA-256
`56bef9038109ccae82e1c3843d400d6ca51aee406649a69c206769c8cbc7c89c`; unpacked to 52 files, 180.2 MB —
`ggml-cuda.dll` at 141,679,616 bytes plus the 51 files of the CPU zip (all fourteen `ggml-cpu-*.dll`
variants, `llama-server.exe` and `llama-server-impl.dll`, `llama-common.dll`, `llama.dll`, and no
LICENSE), which confirms the merge. Then `scripts/vendor-cuda.ps1 -InspectOnly` pointed at that
directory — the same fat-binary walker that produced the parakeet table in
`docs/NATIVE-BINARIES.md`:

| File | Containers | Cubins | PTX |
|---|---|---|---|
| `ggml-cuda.dll` | 141 parsed, 0 rejected | `sm_86`, `sm_89`, **`sm_120`**, `sm_121` | `compute_75`, `compute_80`, `compute_90` |

That is the list CMake predicts for a ≥ 12.9 toolkit, read out of the binary; the walker reports the
SM number and does not tell `120a` from `120`. The cross-check that would confirm it is the same one
parakeet's row lacks — every payload is compressed, so nothing was read back against its own ELF
header — with one difference that matters: parakeet's `sm_120` row was corroborated by a run on the
5080, and **this one has been run nowhere**. Its corroboration is the first thing the desktop
produces. The desktop's driver is 610.88; the minimum a 13.3 runtime needs was not looked up here,
and `nvidia-smi`'s header on that machine answers it in one line.

LLamaSharp, for the comparison this section exists to make: `.github/workflows/compile.yml` at
`v0.27.0` installs CUDA **12.4.0** and passes `-DGGML_NATIVE=OFF -DLLAMA_BUILD_TESTS=OFF
-DLLAMA_OPENSSL=OFF -DBUILD_SHARED_LIBS=ON -DGGML_CUDA=ON` — no `CMAKE_CUDA_ARCHITECTURES` — so its
natives get the 12.4 list: `sm_86` and `sm_89` cubins and PTX up to `compute_90`. Read from the
workflow and from ggml's default *at b10448*, where its natives are b8816 and the default may have
read differently there; its 224 MB package was not scanned. On this card that is a JIT at first
load at best, no Blackwell kernels ever, and the toolkit-install requirement above; and vendoring
the 13.3 natives under it is the struct-layout mismatch above. Nothing here is a fault in
LLamaSharp; it is a toolkit choice made on somebody else's schedule.

**What CUDA does not change, and one thing it adds.** It adds no VRAM — decision 2's arithmetic on
the 27B is backend-independent. What it adds is a way to be fooled: on Windows the NVIDIA driver can
let allocations spill into system RAM when the card is full (the driver's "sysmem fallback" policy —
general knowledge, not measured here), and this build's `llama-server` has `--fit on` by default,
which trims layers and context to what fits. Both mean a model that does not fit will still *run*.
The honest reading is ggml's own `model buffer size` / KV-buffer lines against `nvidia-smi` — the
VRAM counter decision 4 says this project does not have, once more.

Read from `tools/server/README.md` at b10448 the same day, because they bear on this document:
`--ctx-checkpoints N` (default 32) with `--checkpoint-min-step N` (default 8192) is the server's
mechanism for hybrid and recurrent models on follow-up turns — such models cannot roll their
recurrent state back, so without checkpoints a second question re-prefills the whole transcript;
whether it holds at 40k is unmeasured. `--host` defaults to `127.0.0.1`; `--api-key`,
`--slot-save-path`, `--cache-reuse`, `-fit`, `-ot` and `-ncmoe` are all flags. Read, not run.

**Recommendation, revised again on 2026-08-16, and narrower this time: `llama-server`.** The
revision before this said spike LLamaSharp and `llama-server` a day each and let the table decide;
it was written before the desktop tier was required to run on CUDA. Under that requirement the
table has one clean column: only the child process gets the `cuda-13.3` build's native `sm_120`
kernels without a toolkit install. The four checks the spike was to run collapse to one — the same
GGUF loads on both machines, CUDA on the desktop and Vulkan on the laptop — because the other three
are answered by the shape: unload is a kill, so the VRAM comes back by construction; two ggml
instances never share a process; environment knobs are the child's environment. What replaces them
is the lifecycle work only running it answers: a Job Object so the child dies with the app;
`/health` before the first request; what SmartScreen does with an unsigned `llama-server.exe`
started from under the app; and cold-load time for a ~9 GB file as a wait the user sits through.
**Do not hand-roll** — unchanged, and for the same reasons: about 73 `llama_*` and 13
`ggml_backend_*` functions and nine structs, roughly ten times parakeet's C ABI, and one person to
keep it current; hand-rolling stays the answer if the language model ever becomes load-bearing
rather than a v2 feature. LLamaSharp would come back if a release tracked a ≥ 12.8 toolkit and
shipped its runtime — read its compile workflow before assuming either.

Read from `SciSharp/LLamaSharp` at tag `v0.27.0`, `ggml-org/llama.cpp` at `b10448` and master, and
NuGet on 2026-08-15; the server sizes were measured from the CPU release zip the same day, and the
CUDA zip was scanned on 2026-08-16.

**Rejected without much investigation**, and worth saying so plainly: Ollama (a separate daemon to
install, against the no-install positioning), ONNX Runtime GenAI (a different model format and a
second ecosystem), and the Windows-native AI APIs (hardware-gated, not general).

### 2. Which model, at which quantisation, and how the catalogue represents it

Open. `models.json` describes ASR models — `languages`, and an `attributionId` that must resolve
against `Attributions.ById` because the app renders that notice. A summarizer is not an ASR model
and would need either a `kind` discriminator or a second array.

Note the trap that is already documented: `docs/UNPROVEN.md` records quantisation quality on the
*ASR* model as entirely unmeasured, and the same caution applies here and is **harder to
discharge** — an ASR regression at least has WER as a concept, and this does not.

MoE offload (decision 1) widens the candidate set past what fits in VRAM, so "which model" is no
longer bounded by the card. It also means a catalogue entry cannot describe its own requirements
with a single size: the same weights need very different amounts of VRAM and of system RAM
depending on where the experts are placed, and the placement is a regex the user never sees.

Licensing is a hard gate, not a formality. Every entry in `models` carries a licence and a
registered attribution, and `DeferredModelPin` deliberately has no licence property so a pin cannot
assert one carelessly. Any summarizer model has to clear that bar before it can be an entry.

#### One candidate, recorded and then retired

`Qwen/Qwen3.8-27B` — **apache-2.0**, which clears the licensing gate outright, and that is rarer
than it sounds among capable local models. Read from the hub on 2026-08-15; nothing below was run.
**Retired as the working candidate on 2026-08-15**, for the arithmetic under the table.

| | |
|---|---|
| Parameters | 27,781M, **dense** — no experts, so decision 1's offload does not apply |
| Architecture | `qwen3_5`; `llama.cpp` master registers `QWEN35` and `QWEN35MOE` |
| Layers | 64, hybrid — three `linear_attention` to each `full_attention` |
| Context | **262,144** |
| Vision | a VLM, but the GGUF ships the tower as a separate `mmproj-F16.gguf` (928 MB); omit it and it is text-only |
| Published | 2026-08-05, 9,026 likes; the FP8 and every GGUF are from 13–15 August |

Sizes from `unsloth/Qwen3.8-27B-GGUF`, which is LFS-backed, so `docs/MODELS.md`'s pinning procedure
reads its digests exactly as written — the same arrangement as `mudler/parakeet-cpp-gguf`, which is
itself a third-party conversion of NVIDIA's checkpoint.

| Quant | Size | On 16 GB, at 40k tokens |
|---|---|---|
| `Q4_K_M` | 17.1 GB | does not fit |
| `IQ4_XS` | 15.7 GB (15,705,861,088 bytes, 14.63 GiB) | does not fit once the cache is counted |
| **`UD-Q3_K_XL`** | **13.4 GB** (13,441,059,904 bytes, 12.52 GiB) | **does not fit** — see below |
| `UD-IQ3_XXS` | 11.9 GB (11.08 GiB) | fits — 13.6 GiB with the f16 cache; a Q3 |

**An earlier revision of this table said `UD-Q3_K_XL` "fits, ~2.5 GB for KV". The 2.5 GB was not
headroom; it is the KV cache itself, and it was the entire margin.** Corrected here rather than
quietly rewritten. From the model's `config.json` (read from the hub 2026-08-15): 64 layers with
`full_attention_interval` 4, so **16 full-attention layers**, each with **4 KV heads of `head_dim`
256**. llama.cpp keeps a growing KV cache only for those sixteen — the 48 linear-attention layers
carry recurrent state that does not grow with the prompt — so at 40,960 tokens the cache is

    16 layers × 2 (K, V) × 4 heads × 256 × 40,960 tokens × 2 bytes = 2,684,354,560 bytes = 2.50 GiB at f16

or **1.33 GiB at q8_0**. Weights plus cache is therefore **15.02 GiB (f16) or 13.85 GiB (q8_0)** on
a card that reports 16,302 MiB (15.92 GiB), before the compute buffer, before the 1.34 GiB ASR model,
and before anything else on the display. That is arithmetic, not a measurement, and it does not
need to be one: no configuration of this file at this length leaves room. The one row that fits,
`UD-IQ3_XXS`, is a Q3, and the next paragraph is why that is the wrong place to be.

**Two things follow, and they now point the same way.**

The architecture suits this feature unusually well. A 256k window puts a three-hour transcript —
about 40k tokens — inside a single pass, which is decision 3's long-context option actually existing
on this hardware rather than in principle. And with 48 of 64 layers on linear attention the KV cache
at that length is 2.5 GiB rather than the 10 GiB a fully-attentive 64-layer model of this shape would
need — the hybrid layout is the right idea; this particular size of it is too big for this card.

And **no Q4 fits, so this would run at Q3**, and that is the wrong end of the quantisation scale to
be at for this particular feature. `docs/UNPROVEN.md` records the analogous ONNX INT8 export at
24.8% long-audio WER against 7.8% for fp32, collapsing *silently* into fluent wrong text. That was
an ASR, where a mistake is visible on the page. Here wrong output is fluent by default, there is no
WER to catch it, and the citations would be carrying more weight than they were designed to.

There is no smaller sibling in the 3.8 family to retreat to: the family is this and
`Qwen3.8-2.4T-A95B`, which is a 2.4-trillion-parameter MoE and is `license:other` rather than
apache-2.0. The neighbouring family has one, and it is the working candidate now — next section.
The wider comparison — dense models that fit at Q6, mixtures that fit with experts in system RAM —
is in the maintainer's v2 research notes, which stay outside this repository until something in
them has been run.

**Retired, and the slot is filled below on the same arithmetic.** Nothing here has been run; what
retired the candidate is arithmetic on its own `config.json` — the cheapest possible way to lose
one — and the first draft missed it by writing the cache down as headroom. The Q3 question —
decision 6 arriving early and attached to a specific model — was the one any replacement had to
answer first, and the replacement answers it by not being at Q3.

#### The working candidate now: `Qwen/Qwen3.5-9B` at Q8_0

The same architecture in the size this card takes. Read from the hub on 2026-08-16; nothing run.

| | |
|---|---|
| Parameters | 9,653M, **dense**; `qwen3_5` — the architecture llama.cpp registers as `QWEN35`, the 27B's own |
| Licence | apache-2.0 on `Qwen/Qwen3.5-9B` and on `unsloth/Qwen3.5-9B-GGUF` alike |
| Layers | 32, hybrid — `full_attention_interval` 4, so **8 full-attention layers**, each **4 KV heads of `head_dim` 256** |
| Context | **262,144** |
| Vision | a VLM like its sibling; the GGUF ships the tower as separate `mmproj-*.gguf` files — omit them and it is text-only |
| Published | model card and GGUF repository both last updated 2026-03-02 |

Sizes from `unsloth/Qwen3.5-9B-GGUF`, LFS-backed, so `docs/MODELS.md`'s pinning procedure applies
unchanged:

| Quant | Size | With 40k tokens of cache, on 16 GB |
|---|---|---|
| **`Q8_0`** | **9,527,502,048 bytes (8.87 GiB)** | **10.12 GiB at f16 — fits, and fits beside the 1.34 GiB ASR model** |
| `Q6_K` | 7,458,301,152 bytes (6.95 GiB) | 8.20 GiB — past the laptop's 7.36 GiB fast-heap budget, so it would spill there |
| `Q4_K_M` | 5,680,522,464 bytes (5.29 GiB) | 6.54 GiB — the laptop candidate; inside its fast heap on paper, with under a GiB left for everything else |

The cache, from `config.json`:

    8 layers × 2 (K, V) × 4 heads × 256 × 40,960 tokens × 2 bytes = 1,342,177,280 bytes = 1.25 GiB at f16

or about 0.66 GiB at q8_0 — half the 27B's, because half as many full-attention layers. The 24
linear-attention layers hold fixed-size recurrent state (about 2 MiB each at f32, for 32 value heads
of 128 × 128) that does not grow with the prompt. Weights plus cache at Q8_0 is 10.12 GiB; with a
1.5 GiB compute-buffer allowance — an assumption, anchored on nothing measured here — and the ASR
model resident, about 13 GiB on a card that reports 15.92 GiB. Arithmetic, and the first CUDA run on
the desktop measures it.

Why Q8_0 rather than smaller: it fits, so the quantisation question this document cannot discharge
is asked at the end of the scale where it is smallest — the opposite corner from where the 27B put
it. Why this and not the wider field: it needs no expert offload, no second memory budget and no
unload dance, and it is the retired candidate's own family, so everything read about the 27B's
shape transfers. What it does not have is any measurement of what it does with CSB384's questions —
which is the only question that matters, and the one nothing on this page answers.

**The second file to run, in the same session: `google/gemma-4-12B-it` at Q6_K** —
`unsloth/gemma-4-12b-it-GGUF`, 9,786,022,720 bytes (9.11 GiB), apache-2.0 on the source and the GGUF
alike (Gemma 4 is Apache; the Gemma Terms stop at Gemma 3). Read from the hub on 2026-08-16; nothing
run. 11,960M dense, 48 layers — 8 full-attention, each one KV head of 512 with `attention_k_eq_v`,
and 40 sliding-window at 1,024, each 8 KV heads of 256 — 262,144 context; vision and audio towers
in separate `mmproj-*.gguf` files, and a multi-token-prediction head as a separate 465 MB file that
this document has not checked the server for. Cache at 40k is about 1.1 GiB at f16: 0.63 GiB for
the eight growing layers, counting K and V, and 0.31–0.47 GiB of constant window for the forty
others. Weights plus cache is 10.2 GiB — with the same allowances, about 13.1 GiB beside the ASR
model — which is **the 9B's envelope spent differently: more parameters at a lower quantisation.**
Which of those buys more for citing a transcript is unmeasured, and the two cards' own numbers do
not compare (different benchmarks, self-reported), which is why this is a second file and not a
second candidate: same folder, same `-c 40960 -fa on`, same CSB384 questions, diff the citations.
Its Q8_0 (12,669,647,680 bytes, 11.80 GiB) fits alone at about 14.4 GiB, and not beside the ASR
model.

Two reasons it is second and not first. Everything read about the 27B transfers to its sibling and
nothing transfers here — sliding-window layers, K = V global attention and a separate MTP head are
each a place where llama.cpp's support is younger. And Vulkan on AMD has two reports against Gemma 4
where none was looked for against the 9B: `ggml-org/llama.cpp` #24311 (the 12B QAT on **Windows,
Vulkan**, an AMD dGPU beside an iGPU, garbage output on partial offload; closed **stale** 2026-07-24
with no fix named) and #27007 (the 26B-A4B on Vulkan/RADV, a Radeon 890M — the laptop's own gfx1150
— **open**, citing #24311 as the same class). Neither touches the desktop's CUDA path; both touch
the laptop's, so on the laptop it stays out until reproduced or ruled out there. Read 2026-08-16.

### 3. Retrieval, whole transcript, or both

Reframed by the interaction, and this is the decision that changed most when v2 stopped being a
summary button.

A question like *"what did they say about Tokon?"* is **retrieval**: find the segments that discuss
it, feed those, answer from them. That is cheap, it is citable by construction — the citation is
the segment you retrieved — and three hours stops being a context problem. A question like *"what
are the main topics?"* is **global**: no retrieval over 1,488 segments answers it, because the
answer is a property of the whole recording. That one wants a map-reduce or a long context.

So the honest answer is probably both, and the open question is whether one mechanism can be made
to serve both without the global path quietly degrading into "the model saw a tenth of the
transcript and guessed the rest". That failure would look exactly like a good answer.

If retrieval is in, it brings a question the summary framing never had: **what does the retrieval
itself run on.** Embeddings mean a second model on top of the summarizer, which is a third model in
the product. Lexical search over 1,488 segments needs no model at all and is unglamorous and might
simply be enough at this scale. Nobody has tried either here.

**Whatever is chosen, the three-hour case is the requirement**, not the ten-minute one.

### 4. Are the two models ever resident at once

Open, and interactivity sharpens it. A one-shot summary could load a model, run, and unload. A chat
panel means the model has to be **resident for as long as somebody is asking questions**, and the
wait before the first answer is a wait the user is sitting through rather than one hidden inside a
transcription job.

16 GB of VRAM holds the 1.34 GiB ASR model and the candidate decision 2 now names — 8.87 GiB at
Q8_0, 10.12 GiB with three hours of cache — in sequence comfortably; both at once is about 13 GiB
with a 1.5 GiB compute allowance, inside the card on paper. Since transcription finishes before the
questions start, sequential is still the obvious default — unload the ASR model, load the language
model — and the cost is that re-transcribing during a chat session means paying both loads again.
Under `llama-server` (decision 1) an unload is a process exit, which is the one form of unload that
cannot leak.

MoE offload changes the shape of this question rather than answering it. Experts in system RAM
lower the VRAM pressure and raise the RAM pressure instead — a 30B-total model at Q4 is roughly
17 GB of weights, against 32 GB installed, alongside Windows and the app. That is workable and it
is not roomy, and it is a second budget to track.

Worth recording now: **`docs/UNPROVEN.md` says VRAM has never been measured at all** — the harness
samples host working set only, so it cannot see either side of a split placement. This decision
currently has no data under it in any direction. The first `llama-server` run on the desktop
changes that cheaply: ggml logs its model, KV and compute buffer sizes at load, and `nvidia-smi`
shows what the card is holding; those two, side by side, with and without the ASR model loaded,
are the first VRAM figures this project will have had — and the desktop is also where the CUDA
`sm_120` reading in decision 1 gets its corroborating run.

**The laptop is a different budget, and its one published number needs its footnote.** The 7.36 GiB
that `docs/UNPROVEN.md` quotes for the second machine is **heap 0's `VK_EXT_memory_budget` budget**
— the device-local heap, 7.75 GiB in size — and heap 0 is the **8 GB the BIOS carves out of the
24 GB installed for the iGPU**: the driver reports 8,589,934,592 bytes dedicated and Windows sees
15,994 MB of physical memory. There is also heap 1, host-visible, 7.81 GiB, and a 2 GiB
single-allocation cap. All measured on that machine with `vulkaninfo`, the driver's registry key and
`Win32_ComputerSystem` on 2026-08-15. What that means for a language model there: on a device that
reports `uma: 1`, ggml's Vulkan allocator tries three memory types in order for every buffer —
device-local-and-host-visible, then device-local, then host-visible — and **no environment knob is
involved**; `GGML_VK_ALLOW_SYSMEM_FALLBACK` is the switch for the *non*-UMA branch (read from
`ggml_vk_create_buffer_device` in `ggml/src/ggml-vulkan/ggml-vulkan.cpp`, `ggml-org/llama.cpp`
master, 2026-08-15). So weights larger than heap 0 load by spilling into the host heap, at the cost
of whatever the OS was using that memory for and of a bandwidth the fast heap does not have. So the
laptop question is not "does it fit in 7.36 GiB" but how much of the fast heap the language model
takes beside — or instead of — the 1.34 GiB ASR model, and what the spill costs; none of it
measured, sequential the only defensible default, and **the UMA carve-out belongs in the machine
block before any laptop figure for a language model is quoted.** It is there now.

### 5. What, if anything, is persisted

Open, and mostly reframed. A chat is a conversation, not an artefact — so the first question is
whether anything is written to disk at all, or whether the panel is transient and the transcript
remains the only output.

If a conversation is saved, the requirement from before stands unchanged and is *more* pressing,
because generated prose in a chat log looks even less like a file than generated prose in a
document: **nobody can be allowed to mistake a model's answer for transcribed speech**, including
after somebody copies half of it into an email. Whatever carries an answer records which model and
which quantisation produced it, for the same reason `TranscriptDocument` does.

There is a related question with no obvious answer: whether an exported conversation keeps its
citations as clickable references, as plain timestamps, or as quoted transcript text.

### 6. What can actually be tested

Less than for v1, and it is worth writing down the little that is testable rather than pretending
the rest is.

- The output is well-formed.
- **Every citation resolves to a real timestamp range inside the transcript.** This is the strong
  one and it is mechanically checkable: a citation pointing past the end of the recording, or at a
  range containing no words, is a defect that can be caught without judging the prose at all. It is
  the same class of check as the WebVTT ordering invariant in `scripts/measure-transcribe.ps1`.
  Under the rule above — the model never writes a timestamp — this is also the runtime path: an id
  that does not resolve is never rendered as a time, so the test and the mechanism are one thing.
- Citations are monotonic and non-overlapping where an answer claims to follow the recording.
- A question about an empty transcript is answered with "nothing here", not with invention.
- A *who said* question yields a range and a quote, or a refusal — never a name. Checkable without
  judging the prose, because the transcript carries no speaker to name.
- **Retrieval, if it exists, is separately testable.** Given a question whose answer is known to be
  at a known timestamp, does the retrieval return that segment? That is an ordinary
  information-retrieval measurement, unlike summary quality, and it does not need a language model
  to evaluate.

What cannot be tested is whether an answer is *right*. Do not build a harness that appears to.

## Where dictation went

Push-to-talk dictation is now v3 — `docs/V3-DICTATION.md`. Nothing about the reordering changes
what it needs; the Win32 risk surface is the same and the pinned streaming weights are unaffected.
