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
   │     │     │     └── Parakeet.Engine.ParakeetCpp    (the only project with native interop)
   │     │     └──────── Parakeet.Engine.Sortformer     (ONNX Runtime; the speaker diariser)
   │     └────────────── Parakeet.Audio                 (WAVE reader + Media Foundation, one net10.0)
   └──────────────────── Parakeet.Cli / Parakeet.App
```

**One project per model, and that is the pattern rather than a coincidence.** `Parakeet.Core`
declares `ITranscriptionEngine`, `ISpeakerLabeller` and `ITranscriptTranslator` and knows nothing
about what implements any of them; parakeet.cpp's interop is in one project and ONNX Runtime's is in
another, so neither can leak into the other or into anything above them. The diariser's project also
owns the three things its ONNX graph does *not* — a NeMo-faithful mel featurizer, the Arrival-Order
Speaker Cache, and the streaming chunk loop — because all three are that model's business and none
of them is diarisation in general. The translator's project does not exist yet: its contract and a
fake shipped ahead of it, the way the diarisation discriminator shipped ahead of any diarisation
entry, and when it arrives it is a fourth box on that diagram rather than a second responsibility
for one of the three.

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
the labeller behind `--speakers` is `SortformerSpeakerLabeller` in `Parakeet.Engine.Sortformer`,
and the window's checkbox loads the same one.

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
assumed. A UI that offers a control the engine ignores is worse than one that offers nothing.

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

1. **Audio classified as speech is never dropped.** Tested.
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

`Parakeet.Engine.ParakeetCpp` is the only project that touches native code.

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

`Parakeet.Engine.Sortformer` touches native code too, but through ONNX Runtime's own NuGet package
rather than a vendored drop: the natives travel per RID inside the package and a `-r win-x64` publish
resolves them without help, which is why nothing about vendoring or the resolver changes. Only one
file in that project knows ONNX Runtime exists.

## The diariser owns three things its graph does not

The ONNX export runs the pre-encoder, the encoder and the head. Everything else is the host's, and
each piece is a place where a plausible implementation gives a worse diarisation error rate without
failing — so each is held against the reference implementation by committed fixtures rather than by
a reading of it.

- **The mel featurizer** reproduces NeMo's `FilterbankFeatures` for this checkpoint: pre-emphasis,
  a 400-sample Hann window with `periodic=false` inside a 512-point transform, 128 Slaney mel
  filters, `log(x + 2^-24)`, and **no normalisation** — the checkpoint says `normalize: NA`, where
  nearly every NeMo ASR config says `per_feature`, and using the common setting makes a correct
  model look mediocre. It streams: the samples pass through and features exist only for the chunk
  being fed to the graph, so three hours costs megabytes rather than the 690 MB the reference
  implementation's load-it-all approach would.
- **The Arrival-Order Speaker Cache** is what makes speaker 2 the same person at minute thirty as at
  minute one. The graph takes the cache and the FIFO as inputs and returns embeddings; it does not
  update them. Eviction is not first-in-first-out — frames are scored by how confidently exactly one
  speaker is active, boosted twice so every speaker keeps a floor and none dominates, and the best
  188 survive in arrival order with unused slots filled by a running mean of silence.
- **The chunk loop** slices the mel spectrogram with asymmetric first and last chunks, trims the
  graph's fixed 381-frame output back to the length it reports, and applies the prediction mask the
  export omits.

## Output

`SubtitleCueBuilder` turns transcript segments into cues: split on word boundaries using the engine's
word timestamps, capped at 42 characters × 2 lines and 7 seconds, wrapped at a balanced break rather
than greedily, and tidied so cues never overlap — overlapping cues make players drop one silently.
Segments that arrive without word timestamps are still split and timed by character share, because a
thirty-second wall of text is not a subtitle and dropping the text is worse than approximate timing.

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
