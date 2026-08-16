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

Sizing, on the machine this project is developed against (16 GB VRAM): a competent summarizer at
Q4_K_M is 2–5 GB on top of the 1.34 GiB ASR model. That fits if the two run in sequence rather
than staying resident together — which is decision 4.

Three hours of transcript is roughly 30k words, about 40k tokens. That is either a long-context
model or a map-reduce, which is decision 3.

## The open decisions

### 1. Bindings: LLamaSharp or hand-rolled P/Invoke

**Measured, 2026-08-14, from the NuGet v3 API.** LLamaSharp is at **0.27.0**, and the three
backends this project would care about all ship at that same version:

| Package | Latest | Size |
|---|---|---|
| `LLamaSharp` | 0.27.0 | 0.4 MB |
| `LLamaSharp.Backend.Cpu` | 0.27.0 | 35 MB (all RIDs in one package) |
| `LLamaSharp.Backend.Vulkan` | 0.27.0 | 48 KB metapackage |
| `LLamaSharp.Backend.Cuda12` | 0.27.0 | 48 KB metapackage |
| `LLamaSharp.Backend.Cuda11` | **0.24.0** | — stopped; CUDA 11 is no longer carried forward |

The two GPU backends are metapackages that pull a per-RID package apiece —
`LLamaSharp.Backend.Vulkan.Windows` at **19 MB** and `LLamaSharp.Backend.Cuda12.Windows` at
**214 MB** (compressed `.nupkg` sizes; neither was unpacked and neither was inspected for what is
inside it). For scale against the stack already vendored here: `parakeet-v0.5.0-lib-win-vulkan-x64.zip`
is 17.1 MB, and CUDA is 149 MB plus a 553 MB cudart archive, 931 MB on disk. So the Vulkan tiers
are comparable and the CUDA tier is *smaller*, which is the opposite of what a second stack
usually costs. That number is a download size and not a measurement of anything that matters.

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
string, not an integer ABI number**. `parakeet_capi_abi_version()` returns a value the binding
compares against the ABI it was compiled for and refuses loudly on a mismatch, which is the single
check that makes the existing interop safe to pin. A string has to be parsed and interpreted before
it can guard anything, and it identifies the build rather than the contract.
(`LLAMA_SESSION_VERSION` and `LLAMA_STATE_SEQ_VERSION` exist too, and version the
state-serialisation format rather than the C ABI.) So a hand-rolled binding would be chasing a
much larger moving header with a weaker guard against it moving underneath.

Read from `include/llama.h` at `ggml-org/llama.cpp` master on 2026-08-14. An earlier draft of this
file said there was no version entry point at all, which was wrong.

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

**Recommendation, which is a recommendation and not a decision:** LLamaSharp. The missing ABI
guard is the deciding fact — it turns "full control" into "full responsibility for noticing a
signature change", and this project has one person to notice it. The offload check above is
supporting evidence rather than the reason: it was the most plausible capability for a binding to
be missing, and it is not missing. Hand-roll if the summarizer ever becomes load-bearing rather
than a v2 feature.

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

#### One candidate, recorded rather than chosen

`Qwen/Qwen3.8-27B` — **apache-2.0**, which clears the licensing gate outright, and that is rarer
than it sounds among capable local models. Read from the hub on 2026-08-15; nothing below was run.

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

| Quant | Size | On 16 GB |
|---|---|---|
| `Q4_K_M` | 17.1 GB | does not fit |
| `IQ4_XS` | 15.7 GB | fits with nothing left for context |
| **`UD-Q3_K_XL`** | **13.4 GB** | fits, ~2.5 GB for KV |
| `UD-IQ3_XXS` | 11.9 GB | comfortable |

**Two things follow, and they pull against each other.**

The architecture suits this feature unusually well. A 256k window puts a three-hour transcript —
about 40k tokens — inside a single pass with room to spare, which is decision 3's long-context
option actually existing on this hardware rather than in principle. And with 48 of 64 layers on
linear attention the KV cache at that length is far smaller than a normal transformer's, which is
what makes 2.5 GB of headroom enough.

But **no Q4 fits, so this would run at Q3**, and that is the wrong end of the quantisation scale to
be at for this particular feature. `docs/UNPROVEN.md` records the analogous ONNX INT8 export at
24.8% long-audio WER against 7.8% for fp32, collapsing *silently* into fluent wrong text. That was
an ASR, where a mistake is visible on the page. Here wrong output is fluent by default, there is no
WER to catch it, and the citations would be carrying more weight than they were designed to.

There is no smaller sibling to retreat to: the family is this and `Qwen3.8-2.4T-A95B`, which is a
2.4-trillion-parameter MoE and is `license:other` rather than apache-2.0.

**Recorded as a candidate, not a choice.** Nothing here has been run, and the Q3 question is the
one that would have to be answered first — which is the same question as decision 6, arriving early
and attached to a specific model.

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

16 GB of VRAM holds the 1.34 GiB ASR model and a 2–5 GB summarizer in sequence comfortably; both at
once is tighter and depends on the summarizer. Since transcription finishes before the questions
start, sequential is the obvious answer — unload the ASR model, load the language model — and the
cost is that re-transcribing during a chat session means paying both loads again.

MoE offload changes the shape of this question rather than answering it. Experts in system RAM
lower the VRAM pressure and raise the RAM pressure instead — a 30B-total model at Q4 is roughly
17 GB of weights, against 32 GB installed, alongside Windows and the app. That is workable and it
is not roomy, and it is a second budget to track.

Worth recording now: **`docs/UNPROVEN.md` says VRAM has never been measured at all** — the harness
samples host working set only, so it cannot see either side of a split placement. This decision
currently has no data under it in any direction.

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
- Citations are monotonic and non-overlapping where an answer claims to follow the recording.
- A question about an empty transcript is answered with "nothing here", not with invention.
- **Retrieval, if it exists, is separately testable.** Given a question whose answer is known to be
  at a known timestamp, does the retrieval return that segment? That is an ordinary
  information-retrieval measurement, unlike summary quality, and it does not need a language model
  to evaluate.

What cannot be tested is whether an answer is *right*. Do not build a harness that appears to.

## Where dictation went

Push-to-talk dictation is now v3 — `docs/V3-DICTATION.md`. Nothing about the reordering changes
what it needs; the Win32 risk surface is the same and the pinned streaming weights are unaffected.
