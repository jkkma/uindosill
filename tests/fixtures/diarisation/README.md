# Diarisation fixtures

Three directories, three purposes. Nothing here is audio: the audio lives at the repository root
(gitignored) and on the maintainer's Drive, and everything in here either points at it by digest,
is generated from a formula, or is small enough to read.

## `scorer/` — the scorer's validation pairs

Ten synthetic RTTM pairs (`<case>.ref.rttm`, `<case>.hyp.rttm`) and `expected.json`, the diarisation
error rate components pyannote.metrics computed for each. `scripts/validate-der.py` wrote both — the
pairs from recipes in the script, deterministically, and the expected values by running pyannote.metrics
4.1 at the benchmark's exact settings — and `tests/Parakeet.Core.Tests/DiarisationTests.cs` asserts
that the scorer behind `uindosill der` (`DiarisationErrorRate`) reproduces every figure. That test is
what makes a DER from this repository a number rather than an opinion; if it fails, the scorer is
wrong until shown otherwise. `validate-der.py --exe` is the check of the command itself, end to end
through the CLI's own JSON, and it is what a session runs after touching either.

The pairs put the scorer's branches in front of the reference implementation: speaker overlap,
same-speaker self-overlap (which both count twice when overlap is scored, and both skip when it is
not), boundary jitter inside and outside the collar, confusion, missed and false-alarm speech, more
hypothesis speakers than reference speakers and fewer, hypothesis speech outside the reference's
extent, a pair whose optimal speaker mapping differs before and after the collar is cut out, and a
ten-minute jittered three-voice conversation with a hundred-odd turns each side. Each pair is scored
in four blocks: the headline, the strict collar-0 number, the overlap-region breakdown, and the
whole file at `skip_overlap=True`. Regenerate with:

```
%USERPROFILE%\pyannote-metrics-venv\Scripts\python scripts\validate-der.py --generate
%USERPROFILE%\pyannote-metrics-venv\Scripts\python scripts\validate-der.py --check --exe src\Parakeet.Cli\bin\Release\net10.0\uindosill.exe
```

The venv lives outside the working tree on purpose; the script's header says how to make it. Do not
edit `expected.json` by hand.

**Two rules that look like one.** pyannote's `skip_overlap` extrudes every pairwise overlap of
reference *turns*, whatever their labels — a speaker overlapping themselves is skipped too — while
`get_overlap`, which the overlap-region breakdown is taken over, is defined over *distinct* labels.
The scorer copies each rule where pyannote uses it; the `self-overlap` pair is what holds them apart.

**The convention, because it inverts comparisons.** pyannote's `collar` is a *total* width centred
on each reference boundary: `collar=0.25` forgives 0.125 s either side. NIST md-eval's `-c 0.25` and
NeMo's `collar=0.25` are *half*-widths — NeMo's own docstring says so — which is this scorer's
`--collar 0.5`. The headline here is pyannote `collar=0.25, skip_overlap=False`, the convention of
arXiv 2509.26177 (which states it uses pyannote.metrics with exactly those settings). A number from a
NeMo model card is therefore not on this scale until it is rescored at `--collar 0.5`.

## `sortformer/` — what the reference diariser computed

Eight files, 859 KiB, written by `scripts/make-diariser-fixtures.py` and asserted by
`tests/Parakeet.Engine.Sortformer.Tests/`. They are the whole of the correctness claim for the C#
port of Streaming Sortformer, and they exist because the ONNX graph does not own the pipeline: the
mel featurizer, the Arrival-Order Speaker Cache and the chunk loop are all the host's, and each is a
place where a plausible implementation produces a worse DER without failing.

The generator **imports the reference and runs it** — NVIDIA's own `SortformerModules` and NeMo's
own `FilterbankFeatures` — and commits what they returned. It needs torch, numpy, librosa and a
`nemostub/`-shaped tree; CI never runs it, for the same reason it never runs `validate-der.py`.

```
python scripts/make-diariser-fixtures.py --reference C:/Users/ayymanPC/spike-sortformer
```

| file | what it holds |
|---|---|
| `expected.json` | the manifest: geometry, per-case metadata, tensor offsets, expected post-processing segments |
| `mel-filterbank.f32` | NeMo's 128 × 257 Slaney-normalised mel filterbank |
| `mel-window.f32` | its 400-sample Hann window, `periodic=false` |
| `mel-tones/noise/silence/ramp.f32` | log-mel features for four formula-defined signals |
| `speaker-cache.f32` | ten steps of `streaming_update_async`, every input and output tensor |

Four things about their design, each of which was arrived at by getting it wrong first.

**Inputs are formulae, not files.** Every signal and probability sequence is an exact expression
evaluated identically in Python and C# (`DeterministicInputs.cs` mirrors the generator's functions),
so only the *expected output* is committed. A fixture cannot drift from the input that produced it,
and no audio enters the repository.

**The speaker cache is exercised at embedding dimension 8, not 512.** The algorithm never does
arithmetic across that dimension except one masked mean, so every index computation, score, boost,
top-k and eviction is identical at 8 — and the oracle is half a megabyte instead of fifty. The
embeddings carry `frame + dimension/16` rather than noise, so a gather that reads the wrong frame,
or reads down the wrong stride, is visible in the value itself.

**The predictions are checked to be tie-free, using the reference's own scoring.** This is the part
that is easy to get wrong. The eviction score floors both of its logs at 0.25, so a frame whose
three other speakers are all above 0.75 scores on its own probability alone — and two such frames
landing on the same float32 score identically. `torch.topk` does not define its order among equal
values, so if such a pair straddles a top-k boundary the reference picks one arbitrarily and the
fixture is asking a question with no answer. The generator runs `_get_log_pred_scores`,
`_disable_low_scores` and `_boost_topk_scores` over the candidate sets the compression actually saw
and rerolls the seed until no boundary is tied. Two input distributions were rejected by that check;
both looked fine.

**The last step is short.** A real recording's last chunk has less lookahead and fewer frames than
the ones before it, and buffers sized for a full chunk keep stale data past the new logical length.
Step 9 is 69 frames against 381 for exactly that reason.

**The port is not bit-identical to the reference, and cannot be.** It computes its transform in
double where PyTorch's is single, sums in a different order, and calls a different runtime's `log`.
So the featurizer tests assert on the *size* of the deviation, with tolerances set from measurement
rather than chosen in advance: 1e-3 overall and 2e-4 in bands carrying real energy, against measured
worsts of 3.0e-4 and 8.0e-5 and log-mel values spanning −16.6 to +5. The speaker cache's tensors agree exactly except the
running silence mean, which is an average. What settles the question is not any of these: it is that
the port scored **16.3368%** on AMI test against the Python reference's **16.3324%**.

## `dev/` — the development stretches

`stretches.json` pins five ten-minute stretches cut from the four stratified test episodes: which
episode, which onset, the exact ffmpeg line, the ffmpeg version, the byte count and two SHA-256s of
the WAV (whole file, and PCM data alone). `scripts/measure-der.ps1 -Cut` — `lab.ps1 der -Cut` —
re-creates and verifies them into `runs/der/stretches/`; the manifest's own comment explains the two
digests and how the onsets were chosen. The reference labels go beside the manifest as `<id>.rttm`,
one per stretch, and `measure-der.ps1` scores any hypothesis directory against whichever of them
exist.

### Labelling a stretch

The measurement plan's method, made concrete. Time it on the first stretch — the working estimate
is thirty to sixty minutes per ten minutes of audio, and it is unproven.

1. `lab.ps1 der -Cut`, then open `runs/der/stretches/<id>.wav` in Audacity.
2. **One label track per speaker**, and **the label's *text* is what names the speaker** — the
   export carries each label's text and not its track's name, so the text is the only thing that
   says whose label a line is, and which track a label lands on does not matter. Keep the names
   short: `A` and `B` cost two keystrokes a label where `host_a` costs eight, and across the
   hundreds of labels in a ten-minute stretch that is real time. The choice moves no figure — the
   scorer maps speaker names optimally, and the `perfect-relabelled` fixture pins that at DER 0
   across a complete renaming — so use distinct names, use the same ones for the whole stretch, and
   record in the fixture's note which name was which voice. No spaces (the converter turns any into
   underscores), and no blank labels: an empty text is an error rather than a default, so the
   converter refuses the file and names the line. Label each speaker independently, so overlap falls
   out on its own: where two people talk at once, two tracks carry labels over the same seconds. Do
   not try to decide who "has the floor".
3. **Back-channels are speech.** A 300 ms "yeah" gets a label; hosts do it constantly and a 0.25 s
   collar does not hide it.
4. **Bridge a speaker's own pauses under about 0.3–0.5 s** — draw one label across "so, um, the
   thing is" rather than three — and cut at pauses longer than that. Whichever threshold is used,
   use it for the whole stretch and record it in the fixture's note; `uindosill rttm --bridge` can
   apply it mechanically to labels drawn tighter than that.
5. **Produced speech policy.** A pre-recorded ad read, a jingle with lyrics, a played clip: label the
   voice as its own speaker (`ad`, `clip` — any name that is not a host's) rather than as a host, so
   the labels say what the audio contains and the scorer's speaker count is honest. Music without
   words is not speech and gets no label. This is the policy the study said had to be written before
   the first stretch; it is written here and can be changed before, not after, held-out is scored.
6. Export with *File → Export Other → Export Labels* (the path moved there in Audacity 3.4; every
   label track merges into one tab-separated file, uppermost track first — the converter sorts by
   time, so track order does not matter), then:

   ```
   uindosill rttm <export>.txt --file-id <id> --out tests\fixtures\diarisation\dev\<id>.rttm
   ```

   which merges same-speaker overlaps, drops point labels, prints who spoke how much and how many
   seconds are overlapped, and writes RTTM with LF endings and three decimals. Read the summary: a
   speaker with two seconds of speech usually means labels carrying the wrong text, which is the one
   mistake this convention is exposed to.
7. Flip `"labelled": true` on the stretch in `stretches.json`, note the bridge and anything unusual
   (a guest who arrives halfway; a stretch that turned out to have six voices), and commit the
   `.rttm` — it is the reference, and it stays fixed once a held-out score has been seen.

Post-processing knobs may be tuned on these dev stretches; the scoring convention is never tuned;
and the held-out set — at least one entirely unseen show, still to be sourced — is never relabelled
after scores are seen.
