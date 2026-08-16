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
`--slot-save-path`, `--cache-reuse`, `-fit`, `-ot` and `-ncmoe` are all flags. The same README
documents a **router mode** — start with no model, point it at `--models-dir` or a
`--models-preset`, load and unload models over the API — so the language model, an embedder and a
reranker (decision 3) can sit behind one server. An earlier revision of this paragraph said that
how it isolates them, one process per model or one for all, was not read. **It has been now, from
`tools/server/server-models.cpp` at b10448 on 2026-08-16, and it is one child `llama-server`
process per model**: `server_models::load()` takes a free loopback port with
`common_http_get_free_port()`, hands it to the child as `LLAMA_ARG_PORT` with `LLAMA_ARG_HOST` on
the loopback address, and spawns with `subprocess_option_no_window |
subprocess_option_combined_stdout_stderr`; unload writes `CMD_ROUTER_TO_CHILD_EXIT` to the child's
stdin and calls `terminate()` once the preset's `stop-timeout` (default 10 s) has elapsed. The
README beside it: `--models-max` caps concurrently loaded models (default 4), `--models-autoload`
loads on first request, `--sleep-idle-seconds` unloads an idle model, `POST /models/load` and
`/models/unload` do it by hand, and a request is routed by its `model` field (POST) or query
parameter (GET); presets are an `.ini`, one section per model, keys as command-line arguments. So
the claim below — that an unload is a kill and the VRAM comes back by construction — holds for the
router by the same construction it holds for a single child, and the one arrangement it rests on is
**measured on the laptop, Vulkan, 2026-08-16: adapter dedicated 1,126 MiB idle, 3,583 MiB loaded,
1,126 MiB after `Stop-Process`**. Two things the read leaves for the run: the router pipes its
children's combined stdout and stderr, so ggml's `model buffer size` and KV-buffer lines that
decision 4 reads for VRAM land in the router's pipe rather than in a file this project controls;
and the per-process GPU counters in decision 4 want the grandchildren's pids, which a Job Object or
`Win32_Process` by parent enumerates. Otherwise read, not run.

**A separate model-swapping proxy in front of `llama-server` — `mostlygeek/llama-swap` was the
one considered, 2026-08-16 — is not taken**, and the reason is recorded because the question will
come back. Read from its README the same day: Go, one binary, MIT, Windows builds on the release
page, a YAML `cmd:` per model, swap by killing the wrong upstream and starting the right one,
`ttl` unloading, groups of co-resident models, an api-key on its own listener, `/health` and
`/upstream/:model`. It clears the no-install bar the way `llama-server` does — bundled, loopback,
dies with the app — and it is still the wrong shape here for three reasons. The swap decision 4 is
about is the ASR model against the language model, and the ASR model runs in this process, under
`parakeet.dll`, where no proxy can see it; what a proxy could swap is the language model against
decision 3's embedder and reranker, which are 0.6 GiB apiece and sit beside the 9B without touching
the budget. It removes none of the lifecycle work in the recommendation above — Job Object,
`/health`, notice a crash, kill on exit — but nests it, the app owning the proxy owning the server,
and adds a YAML surface, a second unsigned `.exe` for SmartScreen and a third native to pin, digest
and carry a LICENSE for, against the thirty-odd lines the two lab scripts already spend on start,
`/health`, ask and `Stop-Process`. And the router mode above is the same mechanism in the binary
already chosen, from the same release zip under the same pin. Where a proxy would earn a look is as
a lab convenience for hopping between decision 2's six files from a browser, and a `--models-preset`
`.ini` under the router does that too. Nothing swap-shaped changes the arithmetic: the card is the
card, and a swap costs a load of about 9 GB, which is the wait decision 4 already names.

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

**The spike is now a script, and it has run on one of the two machines.**
`scripts/spike-llama-server.ps1` (`lab.ps1 spike`) does the sitting mechanically — fetches the
release zips for a backend and checks their byte counts against the releases API and the recorded
digest where there is one, unpacks flat, scans the CUDA backend's architectures, starts
`llama-server` as a child on `127.0.0.1` with a random port and api-key, `--fit off` and
`CUDA_CACHE_DISABLE=1` on CUDA so that a PTX-only backend would JIT on every start, waits for
`/health`, prefills, asks, stops, starts again, and samples the GPU counters at every phase — and
prints the block a document should say. Run on the laptop on 2026-08-16 with a 0.6B test model:
the sequence works end to end on cpu and on Vulkan; the counters see the server's memory and the
adapter returns to idle on the kill; **and Vulkan on the laptop does not load a model at all
without `GGML_VK_DISABLE_BFLOAT16=1`** — `docs/UNPROVEN.md`, *Upstream llama.cpp on the second
machine*, has the run — so on the laptop path that knob goes into the child's environment, which
is one line under this arrangement and was gotcha 21's whole problem in-process. Two first-reading
digests came out of it (the cpu and vulkan zips; recorded there). The CUDA branch of the script is
first exercised on the desktop, and the header says so.

Read from `SciSharp/LLamaSharp` at tag `v0.27.0`, `ggml-org/llama.cpp` at `b10448` and master, and
NuGet on 2026-08-15; the server sizes were measured from the CPU release zip the same day, the
CUDA zip was scanned on 2026-08-16, and the cpu and vulkan zips were run on the laptop the same day.

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

**The third file, and not before decision 3 has retrieval: `Qwen/Qwen3.6-35B-A3B` with the experts
in system RAM.** `unsloth/Qwen3.6-35B-A3B-GGUF`, `UD-IQ4_XS` 17,730,509,792 bytes (16.51 GiB) first,
`UD-Q4_K_M` 22,134,528,992 bytes (20.61 GiB) if it earns it; apache-2.0 on source and GGUF alike.
Read from the hub on 2026-08-16; nothing run. 35,952M total, `qwen3_5_moe` — the 9B's family with a
mixture in place of the dense feed-forward, so what this note worked out about linear attention
transfers — 40 layers, 10 full-attention with 2 KV heads of 256, 256 experts with 8 routed and one
shared per token, 262,144 context. Cache at 40k is 0.78 GiB f16. The experts are 256 × 3 × 2048 ×
512 × 40 ≈ 32.2B of the 35.95B parameters, so with `-ot exps=CPU` (a flag under `llama-server`, and
`-ncmoe N` beside it) roughly 15–16 GB of the IQ4_XS file lives in system RAM and what stays on the
card is attention, the untied 248,320 × 2048 embeddings, routers and shared experts — 2–3 GiB by
shape, depending on how the UD quant treats them — plus cache and compute: **about 5 GiB of VRAM,
the ASR model resident, and 15–20 GB of the 32 GB of RAM as the second budget**, which has never
been measured on this machine and joins the machine block, with RAM speed and channel count, before
any figure is quoted.

What it costs is time, and it is the wrong time for the whole-transcript path. Decode is bound by
DDR5 bandwidth — ~1.0B active expert parameters a token at 4.5–5 bits is ~0.6 GB read per token,
a ceiling near 150 tokens/s at 96 GB/s theoretical and real throughput well under it. Prefill is the
bill: with experts in RAM a prompt streams expert weights per micro-batch, and the one clean
published 16 GB row the maintainer's research found — an RX 7800 XT on Vulkan, Q4_K_M, `-ncmoe 12`,
pp512 265 t/s — puts a 40k prompt near two and a half minutes; not this card, not CUDA, and the
shape holds: three hours in one pass is minutes here where a dense model in VRAM is seconds, neither
measured on the 5080. Retrieval turns that around — a prompt of a few thousand tokens makes the
prefill small, the decode ceiling ample, and 35B-class answers available with the ASR model still
loaded — which is why this file waits for decision 3 rather than competing with the two above.

Two things read the same day. `ggml-org/llama.cpp` #26609, **open** since 2026-08-05 and updated
2026-08-15: **Windows 11, an RTX 5070 on driver 610.47** — this card's generation and driver family
— `Qwen3.6-35B-A3B-UD-Q4_K_M`, `-fa on` with q8_0 cache and `--override-tensor
"blk\.(16|…|39)\.ffn_.*_exps\.=CPU"` → CUDA illegal memory access, deterministic across two builds,
gone with `-fa off`. That is exactly the configuration this paragraph describes; the workaround
costs little on this model because the cache is 0.78 GiB at f16 anyway, and some prefill speed.
Whether b10448 carries it was not checked. And #21831, **open** since 2026-04-13 — the server
forcing a full re-process on follow-up turns for SWA and recurrent models — is the follow-up-turn
cost decision 1 hedges, and it applies to the 9B and to Gemma's sliding-window layers as much as
here, so it separates none of the three. Run with `-fa off` until #26609 closes, `-ot exps=CPU`
before `-ncmoe`, and host working set and page cache measured beside `nvidia-smi`.

**The fourth file, and the control: `openai/gpt-oss-20b`.** `ggml-org/gpt-oss-20b-GGUF`,
`gpt-oss-20b-MXFP4.gguf` 12,109,566,624 bytes (11.28 GiB); apache-2.0 on source and GGUF alike.
Read from the hub on 2026-08-16; nothing run. 21,512M total, `gpt_oss`; 24 layers alternating
sliding-window at 128 and full attention — 12 full, 8 KV heads of 64 — 32 experts with 4 active;
native context **4,096, extended by YaRN ×32 to 131,072**; released 2025-08, the oldest here by a
year. Cache at 40k is 0.94 GiB f16 and the sliding layers are noise. **What it has that nothing
above has: no quantisation decision.** MXFP4 is how the experts were trained and shipped, so this
file is the model as released — the ~1.9 GB of attention and embeddings at Q8_0 by the byte count,
the experts native — from the llama.cpp maintainers, revision-pinned. The question this document
says it cannot discharge does not arise for it. Fully in VRAM it is 13.7 GiB with cache and
compute: fits alone, **not beside the ASR model** (15.1). With `-ot exps=CPU` — experts are 24 × 32
× 3 × 2880² ≈ 19.1B of the 21.5B — about 9.5 GiB goes to RAM and 4 GiB stays on the card, the ASR
model resident, at the price of the slowest decode ceiling of the offloaded options: four wide
experts a token is ~1.27 GB read per token, near 76 tokens/s at 96 GB/s theoretical.

Why fourth. Its long context is extrapolated thirty-two-fold from a 4k native window, its card
publishes no long-context or instruction-following number, and the one figure the maintainer's
research found is a competitor's table placing it below the 9B at length; its model card describes a
mostly-English, text-only training set — as remembered, not re-read here — against an app that
transcribes twenty-five languages; and it always reasons in a harmony "analysis" channel before the
"final" one, which the grammar-over-output design in *The model never writes a timestamp* has to be
checked against specifically, since here the channel does not turn off. **Its job is to be the
control.** If the `qwen3_5` family's CUDA or Vulkan support proves immature, or a citation failure
is ever suspected of being a quantisation artefact, this is the file that rules quantisation out,
because it has none. Same session, same questions; `-ot exps=CPU` only if the ASR model must stay
loaded. The same repository ships EAGLE-3 draft GGUFs beside it for speculative decoding — noted,
not checked.

**The fifth file: `google/gemma-4-26B-A4B-it` at `UD-IQ4_XS`, every expert on the card.**
`unsloth/gemma-4-26B-A4B-it-GGUF`, 13,597,177,568 bytes (12.66 GiB); apache-2.0 on source and GGUF
alike. Read from the hub on 2026-08-16; nothing run. 26,544M total with about 3.8B active, `gemma4`
— the 12B's family with a mixture in place of the dense feed-forward: 30 layers, **5
full-attention** with 2 KV heads of 512 and `attention_k_eq_v`, 25 sliding-window at 1,024 with 8
KV heads of 256; 128 experts, 8 routed; 262,144 context; vision tower and MTP head as separate
files. Cache at 40k is 0.78 GiB f16 for the five growing layers plus 0.29 GiB of constant window —
1.07 GiB. Weights plus cache plus the compute allowance is **15.2 GiB: fits alone, with under a GiB
to spare, and not beside the ASR model.** That is the hypothesis the third file cannot test —
25B-class answers with every expert on the GPU, so decode is the card's and not the RAM's — at the
price of the unload dance. With experts in RAM instead (128 × 3 × 2816 × 704 × 30 ≈ 22.8B of the
26.5B; ~11 GiB of the file, ~4.4 GiB left on the card, ~0.77 GB read a token) it is a lighter
sibling of the third file, and second to it. Its `UD-Q4_K_M` (16,947,541,728 bytes) and
`MXFP4_MOE` (16,551,048,928 bytes) do not fit on the card at all. Fifth because it is a second
Gemma and inherits every caveat of the second file — and because **#27007 is this model**: the
Vulkan output corruption on a Radeon 890M was reported on `gemma-4-26B-A4B-it-qat-q4_0.gguf`, so on
the laptop it is out until that issue moves. Desktop, CUDA, same session, same questions; ASR
unloaded first.

**The sixth file, and the architecture control: `mistralai/Ministral-3-14B-Instruct-2512` at
Q4_K_M.** `mistralai/Ministral-3-14B-Instruct-2512-GGUF` — the vendor's own conversion —
8,239,593,024 bytes (7.67 GiB); apache-2.0 on source and GGUF alike. Read from the hub on
2026-08-16; nothing run. 13,945M dense, `mistral3`; **40 layers of full attention and nothing else**
(`sliding_window: null`), 8 KV heads of 128; 262,144 context by YaRN ×16 from a **16,384 native
window**; a Pixtral vision tower as a separate `mmproj` file; the card lists **eleven languages**
(en, fr, es, de, it, pt, nl, zh, ja, ko, ar) against the twenty-five this app transcribes. Its cost
is the cache, and that is the point of running it:

    40 layers × 2 (K, V) × 8 heads × 128 × 40,960 tokens × 2 bytes = 6,710,886,400 bytes = 6.25 GiB at f16

— **five times the 9B's**, 3.32 GiB at q8_0. At Q4_K_M with a q8_0 cache: 12.5 GiB alone, **13.8 GiB
beside the ASR model — fits on paper**; at f16 the cache alone pushes it to 15.4 GiB and the ASR
model out. `Q5_K_M` (9,621,091,904 bytes) fits alone; `Q8_0` (14,359,836,224 bytes) does not fit
with any cache. Its job: every other file here has linear-attention or sliding-window layers, and
this note preferred them for the cache arithmetic above — if the hybrids all cite badly at 40k, this
is the file that says whether the architecture is why. Sixth because it is that control and nothing
more: a Q4 against the 9B's Q8, eleven languages, and 40k tokens is 2.5× its native window.
Whether llama.cpp honours the `llama_4_scaling_beta` term in its rope parameters was not checked.
Same session, same questions, `-fa on -ctk q8_0 -ctv q8_0`, or the cache does not fit.

**Not a seventh file: NVFP4.** The question, since the desktop is Blackwell: NVIDIA's 4-bit format
— E2M1 values with an E4M3 scale per sixteen — is what the card's tensor cores execute natively,
and the issue that asked llama.cpp for it (`ggml-org/llama.cpp` #18250, 2025-12-21) names SM120,
the RTX 50 series, in its title. Read from `ggml/include/ggml.h` at b10448 and the repository's
issue and pull-request list on 2026-08-16: **the type exists** — `GGML_TYPE_NVFP4 = 40 // NVFP4 (4
blocks, E4M3 scale)`, added by #19769, merged 2026-03-11 — and conversion of NVIDIA ModelOpt and
compressed-tensors NVFP4 checkpoints works, including the `qwen3_5` dense and mixture families
(#20505, #20506, March). **What does not exist is the tensor-core path.** On CUDA the type runs
through integer `dp4a`/MMQ kernels (#20644, merged 2026-03-26; #25730 W4A4 activation quantisation,
merged 2026-07-22). The SM120 proof of concept (#20247) closed unmerged; the live attempt is #26704,
"CUDA: Add experimental SM120 CUTLASS MoE prefill for MXFP4 and NVFP4" — a **draft** opened
2026-08-07 claiming 2.19× prefill on `gpt-oss-120b`'s MXFP4 — beside two more drafts (#26159,
#26311). Vulkan gained native E2M1/E4M3 conversions (#25338, merged 2026-07-13), so the laptop can
read the type too, unmeasured. `llama-quantize` cannot yet emit it (#26556 open, #25153
imatrix-aware open; #26989 asks for an NVFP4 cache), so every NVFP4 GGUF is two conversions deep —
NVIDIA's calibration, then somebody's `convert` — and the hub's fifteen most-downloaded are from
individual accounts, none from a model vendor, ggml-org or unsloth: `Qwen3.6-35B-A3B`,
`Qwen3.6-27B`, a `gemma-4-26B-A4B` QAT, and a `Qwen3.8-27B` uploaded 2026-08-15; none of the 9B,
which fits at Q8 and needs none.

So on this card, today, NVFP4 is one more 4-bit format run through integer math: the same bytes and
the same bandwidth as `IQ4_XS`, `Q4_K_M` or MXFP4, and no tensor-core gain until a draft merges.
Four bits do not rescue the 27B — the format does not change the byte count. Its real claim is
quality per bit at four bits with NVIDIA-calibrated scales, which is worth something exactly where
this run order is already at four bits — the third and fifth files — and nothing to the first (Q8)
or the fourth (native MXFP4). It is a variant to swap in against those two and diff, provenance
pinned by digest with the quantiser's notice travelling as `docs/LICENSING.md` requires; not a
seventh file. The pull request to watch is #26704, because a native mixture-of-experts prefill on
this card is precisely the cost that makes the third file wait for retrieval.

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

#### The stack, tiered by what it costs — read on 2026-08-16, none of it run

**Tier 0 — windowed BM25, no model, and the first thing to build.** Segments here average about
27 tokens — too small to be a hit on their own — so the unit is a **window of about 60 s, about 240
tokens, at 50 % overlap**: roughly 600 windows over CSB384's 1,488 segments, with a 120 s variant to
compare. Each window carries the ids of the segments inside it, so a retrieved window *is* the
citation, and the grammar in *The model never writes a timestamp* can enumerate exactly the ids
that are live. Hand-rolled — BM25 is about two hundred lines — and in `Parakeet.Core`, which takes
no dependencies and whose build enforces it; Lucene.NET was still `4.8.0-beta` when the
maintainer's research read NuGet on 2026-08-15. Tokenising is Unicode word breaks and
lower-casing, with a Snowball stemmer per language for **twenty of the twenty-five in
`models.json`** — `hr`, `mt`, `sk`, `sl` and `uk` have no stemmer in Snowball or Lucene.NET (the
research's count against that list; not re-derived here) — and the first run is unstemmed, so that
stemming's contribution to recall is a measurement rather than an assumption. Why it might simply
be enough: no study measures BM25 against dense retrieval on transcript question-answering at
segment granularity; BEIR's "BM25 is a robust baseline" is the nearest evidence, and a 2026 study
finds read-everything competitive at the smallest corpus scales, which a 40k-token transcript is
(all from the research's reading). Cost: no bytes, no VRAM, and it is the one part of v2 that is
testable with no language model in the room.

**Tier 1 — dense retrieval, only if paraphrase recall demands it.**
`Qwen/Qwen3-Embedding-0.6B-GGUF`, `Q8_0`, **639,150,592 bytes**, apache-2.0, the vendor's own GGUF
— read from the hub on 2026-08-16. Last-token pooling with an instruction prefix, per its card
(research); served by `llama-server --embedding --pooling last` on `/v1/embeddings` (flags and
endpoint read from `tools/server/README.md` at b10448 the same day), fused with tier 0 by
reciprocal-rank fusion. About 600 windows are embedded once per transcript: seconds on the desktop,
unknown on the laptop, both unmeasured. It is the third model in the product, at 0.6 GiB beside the
9B — decision 4's budget, barely touched. Alternatives the research cleared on licence:
`nomic-embed-text-v2-moe` (apache-2.0, a 512-token cap, 24 of the 25 languages) and `bge-m3` (MIT);
refused: jina v3/v5 and its reranker (CC-BY-NC); not convertible to GGUF: gte-multilingual-base and
arctic-embed-m-v2.0. **Maltese appears in no embedding model's enumerated language list** that the
research found — worth knowing before it is promised.

**Tier 2 — a reranker, last, and only if precision at the top of the list is the problem rather
than recall.** `ggml-org/Qwen3-Reranker-0.6B-Q8_0-GGUF`, **639,153,184 bytes**, apache-2.0 — read
2026-08-16. `/v1/rerank` needs the server started with `--embedding --pooling rank` (README,
b10448), and it applies the model's template, where LLamaSharp's reranker concatenated raw query
and document (research) — one more thing the child process brings. Reranker support in llama.cpp
merged 2025-09-25; community GGUFs older than that give meaningless scores (research). A fourth
model.

**The global path is not retrieval.** *"What are the main topics?"* wants the whole recording: one
pass inside the 9B's window — 40k fits — or a map-reduce over the windows above with ids carried
through the reduce. Which of the two is decided by measuring the 9B's *effective* length on CSB384,
not its label: RULER puts Llama-3.1-8B's effective length at 32k of a claimed 128k and NoLiMa
drops it from 76.7 to 14.2 at 32k (research), and nothing says the 9B is different until it is
run. Between the two paths sits a router that does not exist: a heuristic first — *what did they
say about, when, did they* → retrieve; *main topics, summarise, overall* → global — and the model
itself as the classifier if the heuristic fails; unmeasured either way. Both paths cite by id, and
the global path's citations are the ones decision 6's tests will find wanting first.

**Why retrieval at all when the working candidate reads 262k.** Four reasons this document already
carries in pieces: the laptop cannot prefill 40k in acceptable time (75–190 s per prompt on that
class of hardware — arithmetic on third-party rates, in the research); the third file's whole cost
is prefill with experts in RAM; a grammar over *live* ids is only possible when the candidate set
is small; and global answers degrade past 32k for open models. Retrieval is the laptop tier, and
the fast path everywhere.

**What gets tested, and it is the first real test in v2:** a hand-labelled set of about thirty
question → segment pairs on CSB384, giving recall@10 for tier 0 on the first day, plus a planted
needle at a known segment and an abstain on an empty transcript and on empty retrieval — the
bullets decision 6 already lists, with numbers on them. Public seeds (AMI/ICSI, CC BY 4.0, joined
with QMSum's spans) are English-only, so this set is home-made, and in more than one of the
twenty-five languages before anything is claimed about them.

Order: tier 0, the thirty questions, recall@10; tier 1 only if recall is the problem; tier 2 only
if precision is. Nothing needs a GPU before tier 1, and nothing here is measured.

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

#### Sharpened, 2026-08-16: a per-model property, a measurement, and a policy

**Per file, on paper**, from decision 2's run order under its own allowances (a 1.5 GiB compute
buffer, the 1.34 GiB ASR model, 15.92 GiB reported):

| File | Beside the ASR model? |
|---|---|
| 1 `Qwen3.5-9B` Q8_0 | yes — ~13.0 GiB |
| 2 `gemma-4-12b-it` Q6_K | yes — ~13.1 GiB |
| 3 `Qwen3.6-35B-A3B`, experts in RAM | yes on the card (~5 GiB) — on a second budget of 15–20 GB of RAM |
| 4 `gpt-oss-20b` MXFP4 | **no** — alone at 13.7 GiB |
| 5 `gemma-4-26B-A4B-it` IQ4_XS, on the card | **no** — alone at 15.2 GiB |
| 6 `Ministral-3-14B` Q4_K_M, q8_0 cache | yes — ~13.8 GiB; not with an f16 cache |

So *are the two ever resident at once* is a **catalogue property** — VRAM at 40k, RAM if offloaded,
resident-with-ASR or not — which is what decision 2's remark that an entry cannot describe its
requirements with a single size was asking for.

**The usable figure is not 15.92 GiB, and that is the finding.** Every "fits at ~13 GiB" above
assumes the display costs little. Measured on the laptop on 2026-08-16, idle, with a browser and an
editor open, through the Windows performance counters named below: **the adapter is holding
2,149 MiB before any model loads, and the compositor alone commits 2,548 MiB** by the per-process
counter (per-process "dedicated usage" is *committed*, and the processes sum past the adapter's
*held* total — read the adapter counter for what is on the card and the process counter for who).
On the desktop that idle number is **unknown**, so every both-resident figure in the table is really
"~13 GiB plus whatever the desktop already holds", and that term is the largest unmeasured one in
the arithmetic. One command puts it in `docs/UNPROVEN.md`'s machine block; until then, "fits" above
means "fits if the desktop holds under about 2.5 GiB idle".

**The measurement exists, is vendor-neutral, and works on both machines.** Verified on the laptop
2026-08-16 with `Get-Counter -ListSet`: `\GPU Process Memory(pid_<pid>_luid_…_phys_N)\Dedicated
Usage`, `\Shared Usage`, `\Local Usage`, `\Non Local Usage`, `\Total Committed` per process, and
`\GPU Adapter Memory(*)\Dedicated Usage`, `\Shared Usage`, `\Total Committed` per adapter — WDDM
counters, so CUDA or Vulkan, NVIDIA or AMD. Under `llama-server` the child's pid gives the language
model's memory cleanly and the app's pid gives parakeet's — the two sides of a split placement that
this section said the harness could not see. `scripts/measure-transcribe.ps1` samples the host
working set only today; the same call adds these to it and to decision 6's `measure-answers.ps1`,
with ggml's model, KV and compute buffer log lines (allocation, not pressure) and `nvidia-smi` on
the desktop as cross-checks. On the iGPU "dedicated" is the carve-out and "shared" is the host heap,
so a Vulkan run there shows in both columns. **The counters have since returned real figures**:
through `scripts/spike-llama-server.ps1` on the laptop, 2026-08-16, a 0.6B test model with a
16,384-token cache showed as **2,456.8 MiB dedicated and 194.3 MiB shared on the server's pid**,
the adapter rising from 1,126.5 to 3,583.0 MiB and falling back to 1,126.0 MiB after the kill —
consistent with the file plus the cache plus a compute buffer by arithmetic, and the run is in
`docs/UNPROVEN.md`. The mechanism works; the desktop's numbers for the files above are what is
still missing.

**Policy.** Sequential stays the default. Both-resident is allowed per model where the arithmetic
says so *and the counter confirms it*, and never on the strength of "it loaded" (decision 1's
sysmem-fallback note). The ASR model unloads when the chat opens unless the user keeps it; the
language model stays resident for the session. `--slot-save-path` (`tools/server/README.md` at
b10448) is what makes closing and reopening a session cheap on the whole-transcript path — the
prefilled 40k state, 1.25 GiB at f16 for the 9B, goes to disk instead of being prefilled again;
unmeasured. Decision 3's residents are small — 0.6 GiB each at Q8 — but the embedder is needed per
question, so it is resident through the chat or reloaded per question, unmeasured either way, and
the server's router mode — one child process per model, decision 1 — is where they would all live.

**The laptop: sequential is forced, not chosen, and its budget moves.** The 7.36 GiB this section
quotes is heap 0's `VK_EXT_memory_budget` figure — a *moment's* budget, net of what other processes
hold in the same carve-out. Tonight they hold 2.1 GiB, so the budget now is nearer **5.6 GiB**
(arithmetic on the measured occupancy), and the 9B at Q4_K_M with 40k of cache — 6.54 GiB — **does
not fit in the fast heap with a browser open**; it spills into the host heap exactly as the UMA
paragraph below says it can. Add the 75–190 s that the maintainer's research puts on a 40k prefill
for that class of hardware (arithmetic on third-party rates), and the conclusion the research
reached has a measured reason under it: on the laptop the tier is retrieval, and the
whole-transcript path is an opt-in with a progress bar and a saved slot.

**First two things to run:** the desktop's idle adapter figure, one command, into the machine
block; then decision 2's first file with the counters sampled at each phase — idle, ASR loaded,
language model loaded, after the 40k prefill, after an answer — with and without the ASR model
resident. That is the first VRAM data this project has had, and it is what decides this decision.

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

#### The answer, as far as reading the code takes it — 2026-08-16

**Transient by default; nothing on disk unless the user exports; and an export is a formatter, in
the mould this repository already has.** `ITranscriptFormatter` exists six times over — txt, md,
json, srt, vtt, vtt-words — and `MarkdownFormatter` says of its provenance header that it "is not
decoration". An answer export is the same class of thing over an `AnswerDocument`, and it carries
five things, one more than the paragraph above asks for:

1. **The "generated, not transcribed" line first**, before anything a reader might quote.
2. **Provenance**: the language model's id, quantisation and backend, the **mode** — retrieval,
   whole transcript, or map-reduce — and the date. The same fields `TranscriptDocument` already
   carries for the ASR, plus the mode, because the mode decides how much of the recording the
   answer could have seen.
3. **A pin to the transcript it was answered against — and this is the gap.** `TranscriptDocument`
   has no identity: `SourceName`, `AudioDuration`, `ModelId`, `Quantisation`, `Backend`,
   `Language`, `ProcessingTime`, and nothing that says *which segmentation*. A segment id is only
   meaningful against one transcript; transcribe the same audio again with another model and the
   same ids point at different words while looking perfectly fine. So an export pins the source
   name, the ASR model and quantisation, and a hash over the segments — or embeds the cited spans
   outright, which is simpler and survives everything, and is what the next item does anyway.
4. **Citations rendered as plain timestamps with the quoted transcript span**, never as clickable
   references. A clickable one needs the app, the audio and the same transcript — three things
   an email does not carry — which is the answer to the open sub-question above.
5. **Copy to the clipboard emits the same rendered form, marker included.** "Somebody copies
   half of it into an email" is the scenario this decision was written for, and copy is how it
   happens.

Why transient: a chat is a conversation, saved answers accumulate against transcripts that get
regenerated, and every saved answer is a saved liability with no WER behind it. Among the
neighbours the maintainer's research surveyed, Granola and Notion export answers as links into the
transcript and YouTube exports nothing. And per `docs/LICENSING.md`, Apache-2.0 wants the licence
*text* to travel with copies, so the language model joins the attribution registry the way the ASR
models did — an embedded licence-text field, not a URI.

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

#### Where each test runs — 2026-08-16

The bullets above stand. What sharpens them is splitting them by where they run, which is how this
repository already works: a fast suite with no weights, and lab scripts that write to `runs/`.

**In the suite, no model, and CI runs them** — pure functions over `AnswerDocument`:

- the grammar generator: every live id in, no other id possible, and the output parses under it;
- every id resolves; the range is non-empty, inside `AudioDuration`, `Start ≤ End`; ids are
  monotone where the answer claims chronology;
- a required verbatim quote is a *normalised* substring of the cited span — the normalisation
  (case, whitespace, punctuation) defined once and tested on its own;
- *who said* yields a range and a quote or a refusal, never a name; an empty transcript and an
  empty retrieval both yield an abstention;
- the export carries the marker line, the provenance and the transcript pin from decision 5.

**In the lab, model in the loop, a script under `scripts/` writing to `runs/`, never CI** —
**`measure-answers.ps1` exists (`lab.ps1 answers`), and has run against a synthetic labelled set on
the laptop (2026-08-16)**. It checks the question set's transcript pin before asking anything,
builds the grammar from exactly the live ids, asks each question through `llama-server`, scores
what a machine can score (citations resolve, ranges forward, overlap with gold, adversarial
abstained, a planted needle cited), validates each label's quote against its own span so a bad
label reports as a labelling error rather than a model failure, measures the grammar's decode cost
per run, and prints a summary whose citation-precision column is deliberately blank for a person.
`-PrintPin` computes the transcript pin block for the labelling session. **recall@10 is a stub that
says so**: tier 0 belongs in `Parakeet.Core`, and a BM25 reimplemented in the script would measure
the script's tokenizer rather than the product's. What it measures, with the same discipline about
naming the backend beside every number:

- recall@10 for retrieval on the thirty CSB384 questions (decision 3); planted-needle hit rate;
  abstain rate on adversarial questions;
- **citation precision by human spot-check on N answers** — the one quality number this feature
  can have, and it is a person's, labelled as such in the run's output;
- the grammar's cost in tokens per second through the binding — 80 → 13 tok/s was reported for
  Llama-3 in April 2024, before `common/sampling.cpp` moved grammar to rejection sampling; the
  first post-rewrite figures now exist and are one-run numbers on a 0.6B model on the laptop, **12%
  on one prompt and 44% on another** (`docs/UNPROVEN.md`), a spread that is why the lab script
  measures it per run — plus prefill, decode and VRAM per file in decision 2's run order, and the
  follow-up-turn cost that #21831 describes.

#### The thirty-question set — the file exists, the labels do not (2026-08-16)

`tests/fixtures/csb384/questions.json` is the set's shape with one placeholder per kind and
`status: template`; the suite validates it (`QuestionSetTests`, six tests) so that a labelling
session cannot leave a file the lab script would misread. Two facts read the same day decided the
format. **The JSON transcript carries no segment id** — `segments[]` has `start`, `end`, `text`,
`conf`, `words` and nothing else — so `S<n>` is the 1-based position in that array and the file is
only meaningful against one transcript: it **pins** the transcript it was labelled against (source,
ASR model, quantisation, backend, segment count, a SHA-256 over each segment's start, end and text
in order), which is decision 5's gap arriving one artefact early; the natural target is the
desktop's f16 reference. And **`language` is empty in the transcript JSON** on the second machine —
`TranscriptDocument.Language` is a field the pipeline fills only from a hint — so "prompt in the
transcript's language" needs a source for the language that the transcript itself does not yet
supply.

The composition, thirty by what each one tests: **16 pointed** (*what did they say about X* — gold
is one or more `[from, to]` ranges and a verbatim quote of at most twelve words from inside them,
for the substring check), **4 paraphrase** (pointed, asked without the transcript's own vocabulary,
written last — where BM25 is expected to miss and tier 1 earns or does not earn its place),
**3 global** (*main topics* — a set of ranges an answer must touch, judged by a person; the router
must send them global), **3 adversarial** (plausible and unanswered, one on a topic the episode
mentions but does not answer — gold is an abstention), **2 who-said** (range and quote; a name is a
failure) and **2 needle** (not hand-labelled: the evaluator plants a synthetic segment after a
given index in a copy of the transcript and expects that id cited). Positions spread across the
three hours, because the effective-length problem is at depth and questions written after reading
drift toward what was memorable, which is what retrieval finds anyway.

The session is a person's — about two or three hours: read the f16 transcript's `md` (a timestamp
per segment), write questions *while* reading, note the index range and copy the quote, do the
paraphrase ones last by rewording pointed ones, write the adversarial ones from adjacent topics.
English, because CSB384 is; the twenty-five-language claim needs a second set later. Once
`status` is `labelled` the suite enforces the composition and the pin. What is consumed by whom:
the suite checks shape only; `measure-answers.ps1` checks ranges against the transcript when it is
present, runs tier 0 for recall@10 once BM25 exists in `Parakeet.Core`, plants the needles, and
diffs every model run's citations against gold. One question the maintainer decides, not this
document: a labelled file carries about thirty short quotes from a podcast in a public repository.
It is a fixture and not a measurement product, so the `runs/` rule does not cover it; the
alternatives are beside the transcript under `runs/`, unversioned, or with the research outside the
repository.

**The mechanism exists server-side.** `tools/server/README.md` at b10448 (read 2026-08-16)
documents `grammar` and `json_schema` on `/completion` and `response_format` with a schema on
`/v1/chat/completions`, so "the test and the mechanism are one thing" is available and not a
design hope. **What the README does not say is how a grammar interacts with reasoning output.**
The working candidate thinks by default; `--reasoning-budget 0` is documented as "immediate end"
of thinking and `reasoning_format` as how thought tags are extracted. **Grammar with the budget at
zero now behaves on one machine and one model**: on the laptop on 2026-08-16, `Qwen3-0.6B` under
`--reasoning-budget 0` began its unconstrained answers "Okay, let's see. The user wants…" — the
thinking moved into the answer channel rather than going away, and one ran off the token cap
mid-citation — while the same question under a grammar over the live ids produced clean cited
bullets that terminated (`docs/UNPROVEN.md` has the A/B). The constraint is a grammar and not a
budget. Still unshown: lazy grammar after a thinking span when the budget is *not* zero, and the
fourth file's harmony format, the hard case, because there the channel does not turn off. Measured
beside it: `grammar` is accepted on `/v1/chat/completions` at b10448, which the README documents
for `/completion` only.

**And the grammar's abstain production is a measured trade, not a formality.** The lab script's
self-test (below) put the same four questions to the same small model with and without the
`NOT_IN_TRANSCRIPT` production: with it, the model abstained on everything, including two
questions answered verbatim in the prompt; without it, it answered everything, including the
adversarial question, with an invented citation. False abstention and invention traded against
each other by one grammar rule. A 0.6B result — but it means the abstain design is a dial the
CSB384 run has to measure per model, and `measure-answers.ps1 -NoAbstainBranch` exists to
separate "cannot find it" from "prefers the exit".

Three things from the maintainer's research notes, carried in because they change what gets
built rather than what gets measured: **cite segment runs, not sentences** — a 2026 study across
8B–120B models finds enforcing sentence-level citation degrades attribution by 16–276% against
paragraph-level, so `[S12-S15]` is the right shape and `vtt-words` is for rendering, not for the
model; **check and mark, do not trust** — FullCite found about 40% of forced verbatim snippets from
an 8B model still fail to match, which is why the substring check above is a mechanism and not a
test; and **grammar hygiene across twenty-five languages** — GBNF works on code points, so
citation tokens stay ASCII and free text is any code point except `[`, `]` and newline; the prompt
and few-shot go in the transcript's language, which `TranscriptDocument.Language` already carries;
and the answer's language is checked mechanically, by a means not yet chosen. NLI-style
verification (MiniCheck and kin) is English-trained and is not an answer for this language list.

What cannot be tested is whether an answer is *right*. Do not build a harness that appears to.

## Where dictation went

Push-to-talk dictation is now v3 — `docs/V3-DICTATION.md`. Nothing about the reordering changes
what it needs; the Win32 risk surface is the same and the pinned streaming weights are unaffected.
