# Diarisation fixtures

Two directories, two purposes. Nothing here is audio: the audio lives at the repository root
(gitignored) and on the maintainer's Drive, and everything in here either points at it by digest or
is small enough to read.

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
2. **One label track per speaker**, named for the speaker, and **every label's text is the
   speaker's name** (`host_a`, `host_b`, `guest_1` — no spaces; the converter turns any into
   underscores). Label each speaker independently, so overlap falls out on its own: where two people
   talk at once, two tracks carry labels over the same seconds. Do not try to decide who "has the
   floor".
3. **Back-channels are speech.** A 300 ms "yeah" gets a label; hosts do it constantly and a 0.25 s
   collar does not hide it.
4. **Bridge a speaker's own pauses under about 0.3–0.5 s** — draw one label across "so, um, the
   thing is" rather than three — and cut at pauses longer than that. Whichever threshold is used,
   use it for the whole stretch and record it in the fixture's note; `uindosill rttm --bridge` can
   apply it mechanically to labels drawn tighter than that.
5. **Produced speech policy.** A pre-recorded ad read, a jingle with lyrics, a played clip: label the
   voice as its own speaker (`ad_read`, `clip`) rather than as a host, so the labels say what the
   audio contains and the scorer's speaker count is honest. Music without words is not speech and
   gets no label. This is the policy the study said had to be written before the first stretch;
   it is written here and can be changed before, not after, held-out is scored.
6. Export with *File → Export Other → Export Labels* (the path moved there in Audacity 3.4; every
   label track merges into one tab-separated file, uppermost track first — the converter sorts by
   time, so track order does not matter), then:

   ```
   uindosill rttm <export>.txt --file-id <id> --out tests\fixtures\diarisation\dev\<id>.rttm
   ```

   which merges same-speaker overlaps, drops point labels, prints who spoke how much and how many
   seconds are overlapped, and writes RTTM with LF endings and three decimals. Read the summary: a
   speaker with two seconds of speech is usually a mislabelled track.
7. Flip `"labelled": true` on the stretch in `stretches.json`, note the bridge and anything unusual
   (a guest who arrives halfway; a stretch that turned out to have six voices), and commit the
   `.rttm` — it is the reference, and it stays fixed once a held-out score has been seen.

Post-processing knobs may be tuned on these dev stretches; the scoring convention is never tuned;
and the held-out set — at least one entirely unseen show, still to be sourced — is never relabelled
after scores are seen.
