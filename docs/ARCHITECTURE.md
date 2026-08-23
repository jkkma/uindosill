# Architecture

## The seam that matters

`Parakeet.Core` references no engine, no platform and no UI. Its NuGet dependency list is empty and
its project reference list is empty, and both are **enforced by an MSBuild target that fails the
build** with an explanation rather than by a note in a wiki. If you think you need a package in
Core, you want an interface in Core and the package in whatever implements it.

That seam is the whole reason a mid-project engine swap costs one project instead of a rewrite. It is
also what lets every test run on Linux with no weights present.

```
Parakeet.Core           contracts + pure logic          (no dependencies at all)
   ▲     ▲     ▲     ▲
   │     │     │     └── Parakeet.Engine.ParakeetCpp    (the only project that binds a native library)
   │     │     └──────── Parakeet.Engine.Python         (a child process; the diariser and the translator)
   │     └────────────── Parakeet.Audio                 (WAVE reader + Media Foundation, one net10.0)
   └──────────────────── Parakeet.Cli / Parakeet.App
```

**One project per engine boundary, and that is the pattern rather than a coincidence.**
`Parakeet.Core` declares `ITranscriptionEngine`, `ISpeakerLabeller` and `ITranscriptTranslator` and
knows nothing about what implements any of them; parakeet.cpp's interop is in one project and the
bundled Python's is in another, so neither can leak into the other or into anything above them.

**What the second of those owns is a process rather than a library**, which is why two models share
it. The diariser and the translator both moved into a bundled Python on 2026-08-21, and everything
about that — finding the interpreter, the line protocol, the lifetime of the child — is one boundary
rather than two. `Parakeet.Engine.Python` therefore also references `Parakeet.Audio`, because the
host keeps the decode and the resampling and hands the child a finished WAV. It references no ONNX
Runtime, and **neither does anything else in the solution: no .NET project here runs a graph any
more.**

The C# implementations of both models are in `attic/` — unbuilt, unreferenced, and not in
`Uindosill.slnx`. Between them they were about 7,400 lines reimplementing an arrival-order speaker
cache, a mel featurizer, a SentencePiece processor, a Marian tokenizer and a beam search, each of
which is a second place for a measured number to drift from the thing that produced it.
`attic/README.md` says what they carried and names the commit where they last built; there are no
tags in this repository, so a SHA is the only reference there is. That moving two models to another
language cost one project and not a rewrite is the seam at the top of this document being paid for.

## The contracts

```csharp
public interface ITranscriptionEngine : IAsyncDisposable
{
    EngineCapabilities Capabilities { get; }
    ValueTask LoadAsync(CancellationToken ct = default);
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        IAudioSource audio, TranscriptionOptions options,
        IProgress<TranscriptionProgress>? progress = null, CancellationToken ct = default);
}

public interface IAudioSource : IAsyncDisposable      // files today, live mic in v3
{
    int SampleRate { get; }
    TimeSpan? Duration { get; }                        // null when unknown (live capture)
    IAsyncEnumerable<ReadOnlyMemory<float>> ReadAsync(CancellationToken ct = default);
}
```

Streaming segments out through `IAsyncEnumerable` is deliberate: the UI shows text as it is produced
on a long file, and v3 dictation reuses the same shape one utterance at a time.

```csharp
public interface ISpeakerLabeller : IAsyncDisposable   // who spoke when; the opt-in's second pass
{
    SpeakerLabellerCapabilities Capabilities { get; }
    ValueTask LoadAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SpeakerTurn>> LabelAsync(
        IAudioSource audio, SpeakerLabellingOptions options,
        IProgress<TranscriptionProgress>? progress = null, CancellationToken ct = default);
}
```

The second contract sits beside the first for the same reason the first exists: the diarisation
engine behind it was chosen by measurement rather than by argument — sherpa-onnx was scored on AMI
and held out — and that choice must stay one project's business. A labeller reads the audio itself
and returns turns on the file's timeline; it never sees the transcript. Attributing words to turns
and cutting segments where the speaker changes is a pure function of two lists
(`SpeakerAssignment`), so the ASR engine and the labeller stay independently testable, and both
audio sources being single-read means the opt-in opens the file a second time — a cost only the
opt-in pays. `FakeSpeakerLabeller` is to this seam what the fake engine is to the other, and
`--fake` still selects it so the opt-in stays exercisable on a machine with no weights installed;
the labeller behind `--speakers` is `SidecarSpeakerLabeller` in `Parakeet.Engine.Python`, which
drives the model in a child interpreter, and the window's checkbox loads the same one. When that
interpreter is absent both opt-ins are disabled **with the reason beside them** rather than failing
at the moment they are used — which is why `PythonRuntime` answers with a `bool` and a sentence as
well as by throwing. A checkbox cannot be drawn out of an exception.

```csharp
public interface ITranscriptTranslator : IAsyncDisposable   // the transcript in English; the opt-in's last pass
{
    TranslatorCapabilities Capabilities { get; }
    ValueTask LoadAsync(CancellationToken ct = default);
    IAsyncEnumerable<TranscriptSegment> TranslateAsync(
        IReadOnlyList<TranscriptSegment> segments, TranslationOptions options,
        IProgress<TranscriptionProgress>? progress = null, CancellationToken ct = default);
}
```

The third takes segments and never audio: translation reads what the ASR wrote, and a translator
that opened the file would be a second speech model. It returns segments rather than an annotation
because, unlike a speaker turn, a translated segment *is* the displayable artefact — which is why
its return type is the engine's and not the labeller's. It runs last, after the speakers, and that
order belongs to the code: `SpeakerAssignment` attributes a speaker per word, a translated segment
has no words, and translating first would coarsen every label rather than fail visibly.
`TranscriptTranslation` is the driver every caller goes through, and it holds the translator to the
contract — one segment out per segment in, times and speaker unchanged, no word timings carried
across — because each of those failures produces a file that looks entirely correct.

`EngineCapabilities` is not decoration. It carries `SupportsDecodeCancellation` and
`SupportsThreadCount`, both **false** for parakeet.cpp, both verified against the header rather than
assumed. A UI that offers a control the engine ignores is worse than one that offers nothing — and
the same rule is what turned `TranslatorCapabilities.SupportsCancellation` false when the translator
crossed a process boundary. See *The process boundary* below.

## Where the work actually happens

`SegmentingTranscriptionEngine` is an abstract base that owns everything around the model: read the
source, cut it into pieces, decode in batches, lift segment-relative word timings onto the file's
timeline, report progress, and refuse a batch whose result count does not match its input count. An
engine implementation supplies `Capabilities`, `LoadAsync` and `DecodeAsync` and nothing else.

That is why `FakeTranscriptionEngine` is worth its weight: it inherits the same base, so CI exercises
the real reading, the real segmentation, the real progress reporting, the real cancellation and the
real formatters — everything except the model. Build the fake engine early. Without it nothing
downstream is testable until a 670 MB file is in place, which is how a job queue ships having never
been run.

## Segmentation is not a tuning knob

`StreamingSegmenter` is the correctness-critical class in this repository.

Parakeet degrades on long single-pass audio, so a file-transcription product that hands the model a
whole recording produces quietly wrong output on exactly the inputs it exists to serve. The segmenter
therefore has two rules it never breaks:

1. **Audio classified as speech is never dropped — and audio the gate keeps out is counted.**
   Tested. The second half is the honest limit of the first: an energy gate cannot tell quiet
   speech from a fan, so what it kept out above the absolute line is reported as an amount when
   it is material (a second, and a tenth of what was segmented) rather than silently lost — a
   partial loss used to be reported nowhere, since the only sentence about the gate needed an
   empty transcript.
2. **A forced cut goes at the quietest frame nearby, not at an arbitrary sample.** Tested for
   contiguity.

It streams rather than buffering — three hours of 16 kHz mono float32 is 690 MB — and it reports what
it did. `SegmentationReport` distinguishes "this track is digitally silent" from "there is audio here
but the detector found no speech in it", and the CLI and the UI say which. An empty transcript with
no explanation is the single most common way a local transcription tool wastes somebody's afternoon.

The detector itself is a plain adaptive energy gate with hysteresis. TEN-VAD is deliberately not used
(Agora non-compete clause in its modified Apache-2.0). Its one non-obvious property is a hard ceiling
on the adaptive threshold — see `docs/UNPROVEN.md` for the failure that put it there.

Fixed-window mode (`--no-vad`) is the escape hatch for material the energy gate mishandles. It is the
same code path with every frame treated as speech, so segments grow to the cap and are cut at the
quietest nearby frame: one implementation, not two.

## The interop layer

`Parakeet.Engine.ParakeetCpp` is the only project that binds a native library of this product's own.

- `[LibraryImport]` source-generated bindings, not `DllImport`.
- `SafeHandle` for `parakeet_ctx*`, so a context cannot be collected mid-decode or leaked on throw.
- Returned `char*` is declared as `IntPtr` and marshalled by hand with `Marshal.PtrToStringUTF8`,
  then freed with `parakeet_capi_free_string` **in a `finally`**. The default string marshaller
  would free it with `CoTaskMemFree` — a different allocator — and corrupt the heap. The strings are
  `malloc`'d; `dup_to_c` in the upstream source confirms it.
- `parakeet_capi_last_error` returns a pointer owned by the context. It is read, never freed.
- `parakeet_capi_abi_version()` is checked at load and a mismatch is refused loudly. Guessing across
  ABI versions corrupts memory rather than failing cleanly.
- Calls on one context are serialised by a semaphore, because the upstream C API has no
  synchronisation of any kind.
- The batch entry point's unvalidated precondition — the sum of the per-clip lengths must equal the
  number of floats in the concatenated buffer — is upheld by constructing both from the same loop.

The native library is found by an explicit resolver that searches `native/<rid>/<backend>/` in a
documented order (requested backend, then CPU — with Vulkan interposed only for a CPU request, and
never falling *into* CUDA) and reports every path
it tried when it fails. See `docs/NATIVE-BINARIES.md`.

**Nothing else in the solution loads a native library of ours, and nothing loads ONNX Runtime at
all.** The diariser's and the translator's natives are ONNX Runtime's, and since 2026-08-21 they are
`pip`'s business inside a child interpreter rather than this repository's: no project references
`Microsoft.ML.OnnxRuntime`, no RID-specific asset has to be resolved for one, and everything above
applies to exactly one library. What replaced that reference is a process.

## The process boundary

The diariser and the translator do not run in this process. Each is a child interpreter started by
`PythonSidecar`, driven over its stdin and stdout, and kept alive for the whole run.

**One child per engine, and not one per file.** The diariser's graph is 453 MiB and the translator's
1.34 GiB, so a batch that reloaded them per file would spend more of itself loading than working.
The child is started lazily — nothing spawns until a model is actually wanted — and stopped on
dispose: asked to shut down and given five seconds, then killed, or killed at once when a request
was cancelled in flight and never answered, since a child mid-way through work nobody wants cannot
read the shutdown line until it finishes. On Windows it is also in a job object the operating
system kills when this process ends however it ends, so a host that never reaches dispose does not
leave a gigabyte of weights resident behind a closed pipe; and the staged WAVs such a death leaves
behind are swept, once per process, before the first load. The two engines *could* share one — the
constructor takes a sidecar — but neither the command line nor the window passes one, so a run with
both opt-ins on has two children and two sets of resident weights.

**It is `python -m uindosill_engines`, and the interpreter is the bundled one.** Deliberately not
whatever `python` resolves to on PATH — picking that up is how a working install turns into a
support thread about somebody's conda environment. The package root reaches the child through
`PYTHONPATH` rather than through a working directory, because the host's working directory is the
user's and arbitrary, and which code runs must not depend on it. `UINDOSILL_PYTHON` and
`UINDOSILL_PYTHON_PACKAGES` override each half for development and for the measurement harnesses,
and the resolution records that an override was used: a figure taken against an unknown interpreter
is a figure nobody can reproduce.

**The protocol is one JSON object per line, UTF-8, newline-terminated.** No framing beyond the
newline, because every payload on it is small — audio arrives as a path and a diarisation result is
a few thousand numbers. Nothing streams bytes over this channel on purpose.

**stdout belongs to the protocol and to nothing else.** `torch`, `librosa` and `numba` all print to
stdout given the right provocation, and a single stray line of theirs lands in the middle of a JSON
stream and desynchronises the host for the rest of the run. `protocol.claim_stdout` therefore takes
a duplicate of the real handle for the channel and points file descriptor 1 itself at stderr, not
only `sys.stdout` — so an `os.write`, a C extension's `printf` through the interpreter's C runtime
and a child process the sidecar spawns all land on stderr too — before any model library is
imported, because importing is itself enough to make some of them print. The host holds the other
end up: it keeps the last 200 lines of the child's stderr so that a death has a traceback attached
rather than being reported as "it died", and a line on stdout that is not a protocol message — not
JSON, JSON that is not an object, an object with no integer id, a progress report with a count that
is not an integer — is recorded there and skipped rather than ending a run. A reply it can
correlate is read field by field, each only when it has the type the protocol gives it, so a
mistyped field in one message is that message's problem and not the reader's.

**The audio crosses as a file.** The host drains the source, resamples to 16 kHz mono, writes a WAV
into the temporary directory, hands over the path, and deletes it in a `finally`. A pipe carrying
both a protocol and a megabyte of PCM is a pipe with two failure modes; the decode belongs to the
side that already owns Media Foundation; and handing over a finished WAV is what stops the sidecar
from having a second opinion about what the file contains.

**Requests are correlated by id, not by order.** Progress interleaves with results and a second
request may be sent before the first has finished, so neither side assumes a reply arrives before
the next message does. A message that carries no id at all goes into the error tail; a reply to an
id nobody is waiting on is dropped. Either would mean a misbehaving sidecar, and a host that died on
one would turn a misbehaviour into an outage.

**A failure is a message, not a crash**, and that is what lets a batch continue past one bad file.
The `kind` field is a closed vocabulary — `request`, `model`, `audio`, `internal` — so a caller can
tell "this file could not be read" from "the model is not there" without matching on message text,
which is how a reworded message silently changes behaviour. It arrives as `PythonEngineException`
and it is **one file** — and it costs that file its pass, not its transcript. Both surfaces run
each opt-in pass through `OptInPass`, which hands the transcript back as it was together with the
reason, so the file is written without speakers or without English and says so: on stderr and in
exit code 3 on the command line, in the row's status and warning in the window. Until 2026-08-22 a
pass that failed after the ASR pass failed the file, and a finished decode went unwritten. A
failure *of* the sidecar — it would not start, it died, it broke the protocol — arrives as
`PythonSidecarException` and it is **every remaining file**; everything still pending when the
child's stdout closes is failed with that one rather than left waiting on a reply that is never
coming, and the sidecar records the fault so that every later request — and every later
`LoadAsync` on an engine already loaded — is refused at once, before the file behind it is decoded
and staged, rather than by the write that would have followed. The child is not restarted; a
handshake that failed is refused again by the next `StartAsync` rather than forgotten.

**The protocol carries a version and the host refuses a number it does not know.** `hello` is the
first request, before any weights are touched, precisely so that a bundled Python out of step with
the application says so in a sentence about reinstalling rather than several megabytes into a model
load. Shutdown is asked for and then enforced: a `shutdown` op, five seconds, and a kill of the
process tree if the child has not gone.

### What the boundary costs

What it buys is that the numerical core is NVIDIA's and HuggingFace's own code rather than a port of
it — `docs/PHASES.md` § *Decided 2026-08-21* has that argument and the measurements under it. The
bill is here:

- **Cancellation, for the translator.** A decode running in another process cannot be interrupted,
  so `SupportsCancellation` is now **false**: cancelling stops the next segment being sent, and the
  one in flight finishes. The capability says so rather than the UI offering a control that does not
  do what it claims.
- **Memory, at both ends.** The host holds a whole file's samples to write the WAV, and the child
  holds a whole file's mel to chunk it. Neither streams.
- **An interpreter inside the installer, and a third download beside it.** Measured 2026-08-21, the
  assembled bundle is **1.20 GB** — `scripts/bundle-python.ps1` builds it and reads it back —
  against the ~0.55 GB it was budgeted at. The CLI zip carries none, so the same bundle also ships
  as `uindosill-python-win-x64.zip`, unpacked into `%LOCALAPPDATA%\Uindosill`; `PythonRuntime` looks
  there after `UINDOSILL_PYTHON` and the application's own copy. **No installer has been packed with
  one in it yet, and that zip has never been produced by CI.** See `docs/UNPROVEN.md`.
- **A second thing to version**, and a set of failure modes that did not exist in process — a child
  that will not start, a child that dies mid-request, a library that writes to the wrong handle.
  Every one of them is named above because every one of them had to be handled.

### The division that was kept

**The policy did not move with the engines**, and that is the point of drawing the boundary where it
is. Still on this side: the `>>eng<<` target token, which `TranslationRequest.Build` is the only
thing that applies, because a source handed to this checkpoint without it comes back as fluent
German rather than as an error; the token limit a source is **refused** against rather than
truncated at — sent with the request so that a source about to be refused is not decoded first, but
counted, judged and thrown here; refusing `-f vtt-words` under the opt-in, since word timings do not
survive translation; and folding a requested speaker count down afterwards, merging the pair that
talk over each other least, because this model estimates a count and cannot be told one. The sidecar
does the things only a model can do — turn a WAV into speaker turns, count a string's tokens and
translate it — and is told nothing about what any of them means. It is the division `ISpeakerLabeller`
already drew in process, kept deliberately, so that crossing a process boundary did not also move
the decisions.

One consequence looks like a mistake and is not. **`SpeakerLabellerLimits` is a second copy of two
constants that live in the sidecar.** The four-speaker cap and the fifty-minute bound have to be on
screen while a queue is being built and the weights are still on disk — "seven speakers was never
reachable" said afterwards is not a warning, it is an epitaph — and at that moment there may be no
interpreter to ask. So the host *declares* them and `LoadAsync` refuses a sidecar whose answer
differs, which is the only thing that keeps a duplicate honest: the check fires on the machine where
the two halves are actually together. The backend is deliberately **not** in that type. It is the
one thing only the sidecar can answer, and a declared guess at it would be provenance turning into
fiction.

**A non-CPU backend is checked against a committed fixture before it is trusted, and the result is
reported rather than enforced.** Measured 2026-08-21, DirectML at ONNX Runtime's default settings
scores 53.1522% diarisation error on AMI test against the CPU's 16.3324%, while emitting speaker
turns that read as perfectly ordinary — a failure with nothing in its own output to reveal it. So
the diariser compares probabilities against a committed reference at a threshold of 1e-4 that was
measured rather than chosen: WebGPU lands at 1.073e-06 and passes, CUDA at 8.143e-04 and does not,
and two CPU runs across ONNX Runtime 1.27.0 and 1.29.0 are bit-identical. Failing does not stop the
run — under a named provider the user asked for it, and under `auto` the run has already landed on
the best provider that built, which is the one being checked — and what the command line and the
window do is name it and say it disagreed. `--speaker-backend-unverified` lifts the refusal of `dml`
by name and nothing else; it does not lift a failed check, because nothing does. **The translator's fixture is the weaker instrument and says so**: six sentences
compared by string equality, with no margin at all, so a provider wrong only on long or unusual
inputs passes it. It catches the failure that has actually been seen — DirectML wrong on all 32
sentences measured — and nothing subtler. What establishes a translator on a machine is the gate
corpus, and on 2026-08-21 that corpus was run against the sidecar on one machine: **8,149 of 8,149
sentences reproduce the gate's recorded hypothesis character for character, all 24 languages at
exactly 100%**, on WebGPU against the gate's CPU. That is one machine, not the thread-count caveat
retired — see `docs/UNPROVEN.md`.

### Why the boundary is testable without a Python

**`tools/FakeSidecar` is a child process that speaks the line protocol from a script on disk.** A
test writes `script.json` into a temporary directory and hands that directory over as the package
root, which makes `PYTHONPATH` a private channel from one test to one child: no parent-process
environment is touched, so nothing races and no test has to be serialised against another.

What it will not do is interpret. It emits the script's lines verbatim, with only `{id}`
substituted, which is how a test produces a message with no id, a line that is not JSON at all, a
reply to a request that was never made, or a child that dies in the middle of one. None of those
could come from a well-behaved emitter and all of them have to be survived. It deliberately
references nothing from `Parakeet.Engine.Python`: a stand-in that shared types with the thing it
stands in for would agree with it by construction, which is the one thing a stand-in must not do.
It lives under `tools/` rather than `tests/` because `tests/Directory.Build.props` makes every
project there an xUnit project, and a second entry point would collide with the one xUnit generates.

**What it cannot reach is anything numerical.** A clone carries no Python, so the suite has no
weights, no ONNX Runtime and no parity check in it — both fixtures above run at load, on a machine
that has both halves, and CI never sees them. That is a loss rather than a tidy-up: seven checkpoint
tests went to `attic/` with the C# translator and nothing in the suite replaces them.

## The diariser owns three things its graph does not

The ONNX export runs the pre-encoder, the encoder and the head. Everything else is the host's, and
each piece is a place where a plausible implementation gives a worse diarisation error rate without
failing at all. **Since 2026-08-21 that host is the sidecar, and the point of moving was that none
of the three would be written a second time**: `feats.py` and `postproc.py` came across from the
spike byte for byte and `engine.py` with one import path edited, because those files are what the
measured 16.33% was produced by and a rewrite would be a different artefact needing a different
measurement.

- **The mel featurizer** reproduces NeMo's `FilterbankFeatures` for this checkpoint: pre-emphasis,
  a 400-sample Hann window with `periodic=false` inside a 512-point transform, 128 Slaney mel
  filters, `log(x + 2^-24)`, and **no normalisation** — the checkpoint says `normalize: NA`, where
  nearly every NeMo ASR config says `per_feature`, and using the common setting makes a correct
  model look mediocre. Every constant is read out of the `preprocessor:` block of the checkpoint's
  own `model_config.yaml` rather than off a model card. **It no longer streams.** The C# one
  produced features a chunk at a time; this computes the whole file's mel up front, which is 128
  float32 every 10 ms — about 51 kB per second of audio, so hundreds of megabytes on a long
  recording. **Its peak is now profiled, and it was not the mel.** Measured 2026-08-22 on the
  bundled torch, whole-file, the complex spectrum and the intermediates behind it peaked at about
  730 kB per second of audio — 1,317 MB above the resting working set for thirty minutes. Since
  that day the STFT runs in hop-aligned blocks of 8,192 frames, each seeing exactly the samples its
  frames would have seen, and the mel is written straight into its final layout: bit-identical to
  the whole-file result on thirty minutes of real audio, and about 306 kB per second of audio at
  peak (551 MB on the same thirty minutes), the rest being the samples themselves. The figures are
  in `docs/UNPROVEN.md` beside the 16.33 % they do not move.
- **The Arrival-Order Speaker Cache** is what makes speaker 2 the same person at minute thirty as at
  minute one. The graph takes the cache and the FIFO as inputs and returns embeddings; it does not
  update them. Eviction is not first-in-first-out — frames are scored by how confidently exactly one
  speaker is active, boosted twice so every speaker keeps a floor and none dominates, and the best
  188 survive in arrival order with unused slots filled by a running mean of silence. **It is
  NVIDIA's `SortformerModules`, imported and called rather than ported**, and the price of the
  alternative is on record: the C# port of it scored 16.3368% on AMI test against the reference's
  16.3324%, 0.0044 points for a port that was done carefully.
- **The chunk loop** slices the mel spectrogram with asymmetric first and last chunks, trims the
  graph's fixed 381-frame output back to the pre-encode length of the piece's *physical* width, and
  applies the prediction mask the export omits. This one is still this project's code, because it is
  the part NVIDIA ships as a training loop rather than as anything callable — but it is the spike's
  own loop rather than a second reading of it. **The trim is the one place the spike's loop was
  wrong, found 2026-08-22:** it trimmed to the valid length the graph reports, where NeMo's
  `streaming_update_async` takes the chunk's capacity from the tensor's physical width and clamps
  the valid length to it, and the two differ on every file because the featurizer pads to a
  multiple of 16 and the STFT is one frame longer than the valid count. One or two frames were lost
  on 7.3 % of durations — a 600 s file came out 7,498 frames where 7,500 are due — and the last
  chunk's rows landed 160 ms early. The 16.33 % AMI figure was produced by that loop and re-scored
  on the fixed one on 2026-08-22 — unchanged to four decimals on every provider, because none of the
  sixteen test durations is among the 7.3 %; `docs/UNPROVEN.md` has the run and what it does not
  show.

## Output

`SubtitleCueBuilder` turns transcript segments into cues: split on word boundaries using the engine's
word timestamps, capped at 42 characters × 2 lines and 7 seconds, wrapped at a balanced break rather
than greedily, and tidied so cues never overlap — overlapping cues make players drop one silently.
Segments that arrive without word timestamps are still split and timed by character share — under the
same 7 s cap, which until 2026-08-22 only the word-timed path enforced, so every cue of a translated
subtitle spanned its whole segment — because a thirty-second wall of text is not a subtitle and
dropping the text is worse than approximate timing.

A cue also keeps the words each of its lines was wrapped from (`SubtitleCue.LineWords`), which is
what `vtt-words` needs to put a timestamp against each word. That mapping is carried rather than
recovered: deriving it by re-splitting a finished line on whitespace happens to work today and
would break silently the moment the two filters stopped agreeing, moving every timestamp one word
along in output that still reads as correct.

The word times are the engine's, not the cue's. `Tidy` adjusts cue boundaries for readability
after the words are attached, so the two can disagree, and reconciling them by moving the word
times would falsify a measurement to fit a presentation decision. WebVTT's rule — inline
timestamps strictly inside the cue and strictly increasing — is therefore enforced in
`WordTimedVttFormatter`, which drops a tag it cannot place legally rather than inventing one that
fits. SRT has no equivalent construct and does not get one.

`TranscriptDocument` carries provenance — model, quantisation, backend, real-time factor — into the
JSON and Markdown output. Quantisation quality on this engine is unmeasured, so a transcript that
cannot say which weights produced it is not a result anybody can act on later.
