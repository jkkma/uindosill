# What is unproven

Read this before quoting any number from this repository.

## What has now actually been run

One 30-second clip, once, on one machine. Recorded precisely because it is the only real evidence
this project has:

| | |
|---|---|
| Machine | Windows 11 (10.0.26200), x64, Ryzen 9 9950X (16 cores / 32 threads), 32 GB — full specification below |
| Runtime | .NET 10.0.11, SDK 10.0.400 |
| Native | parakeet.cpp v0.5.0, `lib-win-cpu-x64`, CPU backend, ABI 6 |
| Weights | `tdt-0.6b-v3-f16` (1.34 GiB) |
| Audio | 30.014 s, 16 kHz mono PCM16, real speech from a video |
| Result | Correct fluent transcript, 2 segments, 89 words with confidences |
| **Real-time factor** | **0.1005** (3.015 s to decode 30.014 s) |

What that establishes: the P/Invoke bindings, the SafeHandle marshalling, the ABI check, the batch
JSON contract, the word-timestamp arithmetic and the segment-offset shifting all work against a real
binary and real weights. Word times came back on an exact 0.08 s grid, which confirms `frame_sec`
without having to trust it.

RTF 0.10 sits in the middle of the desktop range others report (0.03–0.12). It is **one machine, one
clip, one quantisation** — it is not a benchmark and must not be quoted as one.

### Ten minutes of real podcast, through Media Foundation

| | |
|---|---|
| Audio | 600 s from a two-host podcast, 48 kHz mono mp3 → AAC/m4a |
| Path | Media Foundation decode, 48 kHz → 16 kHz resampled inside parakeet.cpp |
| **Real-time factor** | **0.0829** (49.7 s to decode 600 s) |
| Segments | 98, longest 27.99 s, largest gap between them 2.37 s |
| Coverage | 574.7 s of 600 s emitted as segments (95.8%); the rest dropped as silence |
| Words | 1573, **0 non-monotonic**, 0 past the end of the audio |
| Cues | 158, **0 overlapping**, none over the 84-character capacity |

**No duplicated or dropped words at any segment join.** That was the largest open correctness risk
and this is real evidence against it.

**But the cap never fired.** Zero of the 98 segments reached 30 s — the detector found a silence
first, every time. The forced-cut path (cut at the quietest frame within four seconds of the cap)
is therefore *still unexercised on real audio*. Conversational speech may simply never need it;
a lecture, an audiobook or a single-speaker monologue would. Do not read this run as evidence that
forced cuts are safe, only that they were not needed here.

A nine-second gap in the subtitles at 3:29–3:39 is **not** dropped audio: the largest gap between
segments is 2.37 s, so that span sits inside a segment for which the model returned no words —
laughter, most likely.

`audioDurationSec` comes from the container and the segment timeline comes from decoded samples;
on this file they disagree by 21 ms, so the last segment nominally ends at 600.021 s. That is
normal for AAC and affects no cue, because cues are built from word times.

### Two hours fifty-five minutes, end to end

The whole podcast, unattended, through `scripts/measure-transcribe.ps1`.

| | |
|---|---|
| Audio | 10,523.376 s (2:55:23) of two-host conversation, 48 kHz mp3 |
| Path | Media Foundation decode, resampled inside parakeet.cpp |
| Backend | **CPU** — this run predates the Vulkan library being vendored |
| **Real-time factor** | **0.0790** (831.2 s to decode 10,523.4 s) |
| Segments | 1,488 — mean 6.79 s, median 4.77 s, longest 29.97 s |
| Coverage | 10,102.1 s emitted (96.0%), largest gap between segments 4.50 s |
| Words | 29,926, **0 non-monotonic**, 0 past the end of the audio |
| Outputs | 250 KB SRT, 170 KB text, 4.4 MB JSON |

Structural checks over the whole file: no segment out of order, none of non-positive duration, and
no word outside the bounds of the segment carrying it. All 1,488 segment boundaries land on the
0.03 s analysis-frame grid and all 29,926 word starts land on the 0.08 s model-frame grid measured
from their own segment's start — the two grids stay locked together across three hours, which is
what would drift first if the segment-offset arithmetic were wrong by a frame anywhere.

RTF improved with length (0.1005 at 30 s, 0.0829 at 10 min, 0.0790 here) as the fixed model-load
cost amortises. Steady-state throughput on this machine is about 12.7× real time.

#### The forced cut finally ran

Four of the 1,488 segments reached the cap — at 8:51, 18:40, 41:20 and 47:46 — and were cut
mid-sentence because no silence arrived in time. **All four joins read through cleanly.** The
clearest: `…somebody like Max, who is running just this fucking super professional gargantuan
setup.` / `has like dozens of fucking sound devices to scroll through…` — one sentence across the
cut, no word repeated and none lost.

That is the largest caveat in this file discharged. It is discharged by **four samples on one
file**, so it is evidence the path works, not proof it always will.

Three joins in 1,487 repeat their neighbour's last words. All three are the second host echoing the
first — `Oh, that's on the right side.` / `That's on the right side? Yeah, yeah, yeah.` — with
non-overlapping timestamps either side of a real gap. Genuine speech, not a decode artefact. Worth
recording because it is exactly what a duplicated-audio bug would look like in a diff.

#### Memory: bounded, and it does not accumulate

Peak working set is 2,914 MB on one run of this file and 3,004 MB on another — about 1.5 GB above
the 1.34 GiB resident model. Measured with the fixed harness; the first attempt read
`PeakWorkingSet64` after the process exited, which reports zero (gotcha 15).

Against the ten-minute file measured the same way, peak grew 535 MB for 17.5× the audio. That rules
out accumulation proportional to duration — which would have been about 16 GB — but two peaks
cannot rule out a slow leak, because a peak is a high-water mark with no time axis. The working-set
profile can, and does:

```
 10%  2,403 MB      60%  2,897 MB   <- maximum
 20%  2,564 MB      70%  2,817 MB
 30%  2,764 MB      80%  2,727 MB
 40%  2,756 MB      90%  2,702 MB
 50%  2,887 MB     100%  2,650 MB
```

**The curve rises for the first half and falls for the second.** It peaks around 60% of the way
through and gives back 247 MB before the run ends. A leak cannot do that; leaked memory is never
returned. This is the .NET heap growing under sustained allocation until the collector catches up,
then releasing — and the 535 MB between the two file lengths is a higher equilibrium, not
accumulation.

Two supporting details point the same way. Peak varied 90 MB between two runs of the identical file
(2,914 and 3,004 MB), so the figure carries about 3% of run-to-run noise, which is GC scheduling
rather than anything deterministic. And the last decile sits 247 MB *below* the mid-run maximum, so
the trajectory at the end of three hours is downward.

What this does not establish is a bound for arbitrarily long input. The equilibrium was higher at
three hours than at ten minutes; whether it keeps stepping up at ten hours is unmeasured. It is no
longer the open question it was, because whatever sets the equilibrium is clearly not per-segment
retention.

#### The pipeline is deterministic

The two runs of this file produced **byte-identical SRT and text output**, and JSON differing only
in `processingSec` and `realTimeFactor`. All 1,488 segments and all 29,926 words with their
timestamps and confidences are identical. Greedy decoding on CPU should be reproducible and now
demonstrably is, end to end — through the Media Foundation decode, the adaptive-threshold VAD, the
batching and the cue builder, any of which could have carried run-to-run state and does not.

Run-to-run timing variance was 0.6% (831.2 s against 836.1 s, RTF 0.0790 against 0.0795).

## All three backends, measured on one machine

Everything above this heading is the CPU backend. `lib-win-vulkan-x64` and `lib-win-cuda-x64` have
now both been vendored on the same machine and run against the same ten-minute file.

The CPU column below is a **fresh run** of that file, not the one recorded above: 49.1 s against
49.7 s and a peak of 2,397 MB against 2,379 MB — 1.2% and 0.8% apart, inside the run-to-run noise
already recorded for this backend. The earlier figures are left as they were rather than overwritten,
and everything derived from them still refers to that run.

This is the machine every figure in this file comes from. It is recorded in full because a number
without the machine under it cannot be reproduced or argued with, and because most of what is
surprising about these figures is a property of this hardware rather than of the software.

| | Machine |
|---|---|
| OS | Windows 11 (10.0.26200), x64 |
| CPU | AMD Ryzen 9 9950X — **16 cores, 32 threads**, Zen 5, 4.3 GHz base |
| Memory | 32 GB DDR5-6000 CL28 (2 × 16 GB) |
| GPU | GeForce RTX 5080, 16 GB (16,302 MiB reported), **driver 610.88** |
| Storage | 2 TB PCIe 5.0 ×2 NVMe SSD |
| Runtime | .NET 10.0.11, SDK 10.0.400 |
| Native | parakeet.cpp v0.5.0, ABI 6 |
| Weights | `tdt-0.6b-v3-f16` (1.34 GiB) |
| Audio | `chunk.m4a`, 600.0 s, two-host podcast, 48 kHz mp3 → AAC |

**Vendor and model names are withheld from both machine tables** — board partner, SSD model, and the
second machine's hostname. None of them conditions a figure in this file, and what does is still
here: core and thread counts, memory configuration, driver versions, and the PCIe generation the
model loads across. Do not fill them back in.

Four things follow from that specification, and each of them limits a figure recorded below.

**It is 16 cores, not 32.** The 32 is threads. Anything extrapolating from "a 32-core desktop" to a
small machine is comparing against half the cores it thinks it is.

**32 GB of RAM means the memory figures never approached a limit.** Peak working set across three
hours is about 3 GB — under a tenth of installed memory — so nothing here was ever near paging, and
the working-set profile that rises and falls is the collector's behaviour with room to spare. On an
8 GB machine the same run has a different question to answer, and this file cannot answer it.

**The CPU is Zen 5, so it has AVX2 and AVX-512.** That is why the instruction-set baseline of the
vendored natives is still unconfirmed — the one machine that has run them could not have exposed an
AVX2 requirement even if there is one. See gotcha 1.

**Model load comes off a PCIe 5.0 NVMe.** Cold-load timings are a best case for a 1.34 GiB read and
should not be quoted as typical.

| | CPU | Vulkan | CUDA |
|---|---|---|---|
| Real-time factor | 0.0818 | 0.0110 | **0.0064** |
| Decode time for 600 s | 49.1 s | 6.57 s | **3.86 s** |
| Runs behind that figure | 1 | 5 | 5 |
| Range across runs | — | 6.38–6.77 s (5.9%) | 3.75–3.99 s (6.2%) |
| First run on a cold machine | 49.1 s | **14.07 s** | 3.90 s |
| Peak host working set | 2,397 MB | 2,473 MB | 1,685 MB |
| Sustained host working set | ~2,300 MB | ~350 MB | ~720 MB, still rising |
| Segments / words | 98 / 1,573 | 98 / 1,573 | 98 / 1,573 |

Range is `(max − min) / mean` over the runs behind the figure, and the figure itself is their mean.
Sustained is the mean of the working-set deciles after the staging ramp — and CUDA's is not a
plateau: it climbs from 596 MB to 790 MB across the run rather than settling, which nothing here
explains. The three-repetition sweep alternated `vulkan, cuda, vulkan, cuda, …` so thermal or clock
drift landed on both; the remaining runs behind these means were not alternated, including the
Vulkan pair from the shader-cache experiment below. Every decode time here is `processingSec` from
the transcript, which excludes model load.

The Vulkan column was measured with bf16 enabled (`bf16: 1` in the device banner), which was the
product's configuration until 2026-08-16. The default is now to disable bf16 before loading — the
workaround the second machine needs — and it was re-measured on this machine before it changed:
0.31% apart and byte-identical transcripts, six interleaved runs each way on a different ten-minute
file. The table and the reasoning are in *The workaround is in the product, and — since 2026-08-16 —
on by default*, in the second machine's section. Nothing in this table was re-taken.

**CUDA is 12.7× CPU and 1.70× Vulkan in the steady state.** The gap between the two GPU backends is
several times their run-to-run range, so it is not noise. It is still one machine, one file, one
quantisation, one driver.

**On a machine that has never run either backend, CUDA is 3.6× Vulkan**, because Vulkan pays a
one-time shader compilation that CUDA does not. That is a real difference in what a new user
experiences on their first transcription, and it is not the same claim as the steady-state ratio.
Both are below.

### CUDA loaded, and the sm_120 question is answered

The library reports `ggml_cuda_init: found 1 CUDA devices … Device 0: NVIDIA GeForce RTX 5080,
compute capability 12.0` and `[parakeet] pk::Backend using device: CUDA0`, and the transcript's
`backend` field reads `cuda`. That field is the backend that *loaded*, not the one requested, which
matters because a CUDA library that fails to load is answered by running on CPU with nothing said.

**The prebuilt v0.5.0 CUDA library contains native Blackwell kernels.** `parakeet.dll` carries
cubins for `sm_86`, `sm_89` and `sm_120`, and the cudart shipped beside it is CUDA 12.8 — the first
toolkit that can emit `sm_120`. Method and caveats in `docs/NATIVE-BINARIES.md`.

Nothing measurable was JIT-compiled. The first CUDA run ever performed on this machine decoded in
3.90 s and the second in 3.84 s. A driver JIT runs at the first kernel launch, which is inside the
decode timer, so **any JIT cost on this card is bounded by that 0.06 s**. Wall clock is not the
evidence — the harness truncates elapsed to whole seconds, and the 7 s and 6 s the two runs reported
differ by exactly one truncation step. What this does not establish is the cost on a card that
actually takes the PTX path: no JIT was ever induced and timed here.

**Host memory on a GPU backend is not the same measurement and is not comparable.** Working set is
host RAM, and the 1.34 GiB of weights live in VRAM where this counter cannot see them. The harness
says so on any non-CPU run. Note that CUDA's peak is *lower* than Vulkan's (1,685 against 2,473 MB)
while its sustained figure is *higher* (~720 against ~350 MB) — two different staging strategies,
and neither number describes what the GPU is holding. **VRAM was not measured at all.**

### The Vulkan figure first recorded here was a cold shader cache, and that is now demonstrated

This file previously recorded Vulkan at **RTF 0.0230, 13.8 s**. Re-measured on the same machine, the
same binary and the same file about an hour later: **0.0110, 6.57 s across five runs**, none slower
than 6.77 s. That is 2.1x apart, and it was not a fluke of either measurement.

The cause is the NVIDIA driver's on-disk shader cache. Emptying
`%LOCALAPPDATA%\NVIDIA\GLCache` (45 MB, 40 files) and running Vulkan twice:

| | Decode |
|---|---|
| First run, cache emptied | **14.071 s** |
| Second run | 6.766 s |

**14.071 s against the 13.8 s originally recorded** — the original figure reproduces exactly when
the condition that produced it is restored. It was the first Vulkan run ever performed on that
machine, and roughly 7.3 s of it was the driver compiling pipelines rather than the model decoding
audio.

That cost lands **inside `processingSec`**, which is the number RTF is computed from. It is not in
the model-load figure and not in the separately reported warm-up decode, so nothing in the harness
separated it from decoding. See gotcha 20.

Two consequences worth keeping apart.

**For the measurements here:** the steady-state Vulkan figure is 6.57 s and the CUDA-versus-Vulkan
ratio stands. The largest of the five warm samples, 6.77 s, is the run immediately after the cache
was emptied, which is consistent with the cache still filling; excluding it moves the mean to 6.53 s
and the ratio from 1.70x to 1.69x, which is smaller than the run-to-run spread either way.

**For the product:** on a machine that has never run this, **the first Vulkan transcription is about
2.1x slower than every one after it**, and there is no warning anywhere that this is happening.
`SegmentingTranscriptionEngine.WarmUpAsync` runs one throwaway decode over ~0.5 s of dither, and it
demonstrably does not compile the pipelines the real workload needs. **CUDA has no equivalent
penalty**: its kernels ship as precompiled `sm_120` cubins, and its first-ever run on this machine
decoded in 3.90 s against 3.84 s for the second.

There is still a process failure underneath this, and it is the reason it took an experiment rather
than a lookup to resolve: **the original figure was recorded without the driver version, the GPU
power state, or any note of whether it was a first run.** `scripts/measure-transcribe.ps1` now
records the GPU and driver on any non-CPU run. What is *not* recorded, and cannot easily be, is
whether the driver's shader cache was warm — so a single GPU timing from an unfamiliar machine is
still worth distrusting until it has been run twice.

### Backends do not agree byte for byte, and CUDA agrees with the CPU

Three CPU runs of the three-hour file were byte-identical. Across backends that does not hold, so
the earlier reproducibility finding should be read as *reproducible per backend* rather than
absolute. On the ten-minute file every pairing agrees on the one thing that must not vary: **all 98
segment boundaries are identical in all four comparisons**. Segmentation runs in managed code on the
CPU whatever the backend, and this confirms it.

| Comparison | Word tokens | Timestamps (largest) | Confidences (mean delta, maximum) |
|---|---|---|---|
| CUDA vs CUDA | 0 of 1,573 | 0 | 0 |
| **CPU vs CUDA** | **0 of 1,573** | 3 (0.080 s) | 731 (0.0007, 0.0189) |
| CPU vs Vulkan | 2 of 1,573 | 15 (0.160 s) | 836 (0.0029, 0.3729) |
| Vulkan vs CUDA | 2 of 1,573 | 12 (0.160 s) | 871 (0.0029, 0.3728) |

Produced by `scripts/compare-transcripts.ps1`, which aligns two transcript JSONs by word index and
reports segment-boundary, token, timestamp and confidence deltas.

Vulkan is reported by ggml as `NVIDIA GeForce RTX 5080 ... fp16: 1 | bf16: 1 | matrix cores:
NV_coopmat2`, so it is using cooperative-matrix kernels rather than a generic fallback, and the
comparison is between two real GPU implementations rather than one of them limping.

Two results worth separating.

**CUDA is deterministic run to run.** Two CUDA runs produced zero differing tokens, zero differing
timestamps and zero differing confidences, and byte-identical text. Per-backend reproducibility now
holds for CUDA as well as for CPU.

**CUDA tracks the CPU reference far more closely than Vulkan does — 4× on the mean confidence delta
and 20× on the worst case, though in almost as many places (731 confidences against 836).** Against CPU
it changes *no word at all* -- the joined text is byte-identical -- moves 3 timestamps rather than
15, and its confidence deltas are a mean of 0.0007 and a maximum of 0.0189 against Vulkan's 0.0029
and 0.3729. The single disputed token pair is one event at 493 s, `right,` / `uh` against `right.` /
`Uh`: a sentence boundary landing differently with capitalisation following. **CUDA and CPU agree on
it; Vulkan is the outlier.**

That is the expected shape of different floating-point kernels producing marginally different logits
and occasionally flipping a near-tie, not evidence of a defect. It does mean **f16 on CPU, on Vulkan
and on CUDA are three references rather than one**, which matters for the WER harness that still
does not exist: whichever backend it measures against is the one its numbers describe. On this
evidence CUDA is the closest stand-in for CPU -- on one file, which is not enough to make it the
reference.

**It was not enough, and a longer file has since shown it.** The byte-identical CPU/CUDA text above
holds on ten minutes and **breaks on 2 h 55 m**: the same comparison over `CSB384.mp3` gives 47
differing tokens raw, 33 with case and punctuation set aside, out of 29,945 — 0.110% rather than
zero. So "CUDA changes no word at all" is a ten-minute result and must not be quoted as a general
one; the honest general claim is that CPU and CUDA diverge far less than either diverges from
Vulkan, and that the gap grows with the length of the audio. The three-hour figure is in the
quantisation section above, where it serves as the noise floor.

### What that costs the word-timed WebVTT output

`vtt-words` (`WordTimedVttFormatter`) writes an inline timestamp for every word after the first of
its cue — the first takes none by design, and any tag that cannot be placed strictly inside the cue
and strictly after the one before it is dropped rather than nudged. That makes it the first
*subtitle* format whose **bytes** depend on the per-word timings rather than only on the text —
`json` has carried them all along, which is what `scripts/compare-transcripts.ps1` reads, but no
player consumes it and a JSON diff carries `processingSec` and `realTimeFactor` with it.
The table above is therefore also a statement about that file, and its timestamp column is the one
that matters. Restated for it — the same measurement re-read, not a new one:

| Comparison | Word timestamps differing | Largest difference |
|---|---|---|
| CPU vs CUDA | 3 of 1,573 | 0.080 s — one model frame |
| CPU vs Vulkan | 15 of 1,573 | 0.160 s — two frames |
| Vulkan vs CUDA | 12 of 1,573 | 0.160 s |

So **a `chunk.words.vtt` from a CUDA run and one from a Vulkan run are not byte-identical.** That
is a stronger statement than it looks, because this format is the most sensitive output the project
has: it carries every word timestamp, so it differs wherever *any one* of them does. `srt` and
plain `vtt` carry only cue boundaries and differ only where a moved timestamp happens to fall on
one; `txt` carries no *word* timings — only each segment's start time, which the managed segmenter
produces identically whatever the backend — so between CPU and CUDA, which agree on every token, it
cannot differ. Whether the `srt` and plain `vtt` of these particular runs are byte-identical was
not measured: `scripts/compare-transcripts.ps1` aligns the JSON transcripts, not the subtitle
files. A diff of two `.words.vtt` files compares two backends rather than two builds, and this is
the first format for which that is reliably true. CUDA tracks the CPU here as it does everywhere
else in this comparison — 3 timestamps against Vulkan's 15, at half the deviation.

One or two model frames of jitter in where a word lights up is invisible to somebody watching it
play. That is a judgement about perception, not a measurement, and **nobody has watched both** — a
`.words.vtt` has now been watched (below), but only a CPU one, so no observer has compared two
backends' files and the jitter claim stands unexamined.

**This is one file:** 1,573 words of two-host conversation, ten minutes, one machine, one
quantisation, one driver. It does not bound the jitter on material the model finds harder, and it
says nothing about the three-hour file, which has only ever been run on CPU.

### The cue builder's no-word path is reachable, and has never been observed

`SubtitleCueBuilder` has two ways to make a cue. When the engine reported word timestamps the cue
times come from the words. When it did not, `AppendProportionalCues` splits the segment text by
capacity and times each piece by its share of the characters. There are no `TranscriptWord`
instances on that second path, so `vtt-words` writes those cues with no inline timestamps at all
rather than tagging them with character-share guesses dressed up as measured ones.

The path is reachable by construction. parakeet.cpp returns `text` and `words` as independent
fields (`ParakeetJson.ParseClip`), and `SegmentingTranscriptionEngine` copies an empty word list
straight through to the segment, so a clip carrying text and no words produces proportional cues.

**No run recorded in this file has been shown to reach it,** and the nine-second gap at 3:29–3:39
of the ten-minute file is not the evidence it looks like. A segment that reaches the proportional
path produces cues tiling its whole duration, so it would appear as approximately-timed subtitles,
not as a gap. A gap means no cue was emitted across that span at all — which is what happens when
a segment's text is empty, since `SegmentingTranscriptionEngine` yields nothing for
`text.Length == 0`, or when a word-timed segment simply has no words there. Either way the
proportional path did not run.

Settling it costs one command against a transcript that already exists, since the JSON writes no
`words` array at all for a segment that had none:

```powershell
# Against any CPU transcript. The harness writes runs/<timestamp>-cpu/<stem>.json; the
# hand-placed runs/chunk-cpu.json from the backend comparison works just as well.
@((Get-Content <transcript>.json -Raw | ConvertFrom-Json).segments |
    Where-Object { -not $_.PSObject.Properties['words'] }).Count
```

Non-zero means the degradation path has been exercised on real audio and is worth watching in the
output. Zero across the ten-minute and the three-hour transcripts means it is held up by its unit
test alone, which is worth knowing before relying on it.

### The CUDA teardown abort now takes the exit code with it, exactly as gotcha 19 predicted

Gotcha 19 records ggml's CUDA buffer destructor failing during process teardown — `cudaFree`
returning `cudaErrorCudartUnloading` after the driver has begun unloading — and notes that the two
harness runs behind it printed that error and still **exited 0**. It also says why that was luck
rather than design: ggml's error path is `GGML_ABORT`, and the abort merely landed after the exit
code was set, so *"on another runtime or another driver the same teardown could plausibly present as
a crashed process with complete, correct output on disk."*

That case has now been observed. Measured 2026-08-15 on the machine in the table above, the same
error is followed by exit code **-1073740791** (`0xC0000409`, `STATUS_STACK_BUFFER_OVERRUN` — the
MSVC fast-fail path an `abort()` takes on Windows; a shell truncating it to 8 bits reports 127).

**The consequence is that `measure-transcribe.ps1` calls a valid run a failure.** It branches on
`$process.ExitCode -ne 0` and prints `THE RUN FAILED. Nothing below measures anything - see the
error above.` The run under one such banner reported decode 3.4 s, RTF 0.0047, 179 segments, 2,203
words, and wrote both output files at full size. **Do not discard a CUDA measurement here on the
exit code alone.** Gotcha 19's rule — judge a run by its exit code *and its outputs* — is what
survives this; the exit code on its own no longer does.

The output is not merely present, it is right: on the file below, CUDA and CPU text came out
**byte-identical**, 0 of 2,203 word tokens differing. The abort is in a destructor, after decode,
and it costs nothing but the exit code.

What it does and does not depend on:

| Varied | Result |
|---|---|
| 6 runs, 728.8 s file, f16 | abort every time, correct output every time |
| 30 s clip, f16 | abort |
| 30 s clip, q8_0 | abort |
| Same two files on `vulkan` and `cpu` | exit 0 |

So it is neither length-dependent nor quantisation-dependent, and across those eight runs it was
deterministic rather than intermittent. `uindosill doctor` still reports `cuda ok — abi 6`,
correctly: its probe calls only `parakeet_capi_abi_version`, never allocates a device buffer, and so
never runs the destructor that aborts. That is gotcha 18 with a sharper edge — `ok` from doctor does
not mean the process will come back cleanly from a decode.

**What changed between exit 0 and exit `0xC0000409` is not identified, and the obvious suspects are
ruled out.** Same machine, same **driver 610.88**, same .NET 10.0.11, same vendored v0.5.0 binaries
— the archive digests were re-verified against `docs/NATIVE-BINARIES.md` before this run. Gotcha 19
calls the exit-0 outcome an ordering accident, and an accident that resolves one way for two runs
and the other way for eight is the least strained reading, but nothing here demonstrates that.

Two limits on the comparison. The later sweep runs behind the 0.0064 figure above were never
inspected for this error at all, so only two CUDA runs are known to have exited 0. And the audio
differs:
the file used here is a 728.8 s public-domain LibriVox chapter at 22.05 kHz mono mp3, **not**
`chunk.m4a`, which is gitignored and did not survive the working copy being recreated. A same-file
comparison against the original runs is therefore no longer possible on this machine, and nobody has
tried to reproduce this on a second one.

### Root-caused and fixed, 2026-08-16 — the abort is a static destructor, and upstream ships the remedy

Measured on the machine in the table above, same driver 610.88, same v0.5.0 binaries.

**Root cause, from the WER minidumps under `cdb`.** The desktop app's one recorded crash
(`Uindosill.exe`, 8/15 08:08, a CUDA session — `native\win-x64\cuda\parakeet.dll`, `cudart64_12`
and `nvcuda` in its module list; no `vulkan-1.dll`) and the CLI's CUDA crashes have the same stack,
frame for frame:

```
ucrtbase!common_exit → kernel32!ExitProcess → ntdll!LdrShutdownProcess
  → parakeet.dll DLL_PROCESS_DETACH → ucrtbase!execute_onexit_table
    → parakeet!pk::Backend::~Backend → … → ucrtbase!abort   (FAST_FAIL_FATAL_APP_EXIT, 0xC0000409)
```

`pk::Backend` is parakeet.cpp's process-global compute backend (`src/ggml_graph.cpp`: a static
`std::unique_ptr<Backend> g_backend` holding the ggml backend and a persistent gallocr device
buffer). Its destructor runs at DLL unload, after the CUDA driver's own teardown, and ggml aborts on
the failed `cudaFree`. It is not the `parakeet_ctx` this codebase owns — the CLI has always freed
that and aborted regardless. Upstream's comment on the function it added for this says as much:
*"Relying on static destruction frees it during process exit, AFTER the driver's atexit handler,
which aborts with 'driver shutting down'. Call from main() before returning."*

**The fix is that call**, `pk::shutdown_backend()`, made through its MSVC-decorated export
`?shutdown_backend@pk@@YAXXZ` — it is not in the C ABI, and all three vendored builds export it only
because they export everything (2,090 symbols). Runs below drove the real Avalonia window and the
real `MainWindowViewModel` from a harness (plus a no-Avalonia console mode), q8_0 model, two runs
per row unless stated:

| process | backend | teardown | exit |
|---|---|---|---|
| GUI | none loaded | close | 0 |
| GUI | Vulkan | close with model resident (×5) | 0 |
| GUI | CPU | close with model resident | 0 |
| GUI | **CUDA** | close with model resident, before the fix | **0xC0000409** |
| GUI | **CUDA** | `Session.DisposeAsync()` then close, before the fix | **0xC0000409** |
| console | **CUDA** | dispose or leak, no shutdown call | **0xC0000409** (×4) |
| GUI / console | CUDA | dispose, then `pk::shutdown_backend()` | 0 (×4) |
| GUI | CUDA | `pk::shutdown_backend()` with the model still resident | 0 |
| GUI / console | Vulkan, CPU | dispose, then `pk::shutdown_backend()` | 0 (×8) |
| GUI, fixed build | CUDA | close with model resident (×3); close 2 s into a batch (×2) | 0 |
| GUI, fixed build | Vulkan, CPU | close with model resident; CPU close 2 s into a batch | 0 |
| CLI, fixed build | CUDA | `transcribe` of the ten-minute file (×2) | 0, RTF 0.007–0.008, no error line |

Eight of eight aborted without the call and none of the twenty-six with it or the fixed build did.
Every aborting process had already logged its `Main` returning 0 — the crash was entirely after
this codebase's last instruction. Avalonia is not a factor: the console mode aborts and recovers
identically. The mid-batch closes took 0.1–0.3 s from close request to window gone.

**What this does not settle.** The one-in-five exit-0 CUDA run recorded above stands as recorded
and is still unexplained; today's eight unfixed runs were all aborts. The Itanium spelling of the
export (`_ZN2pk16shutdown_backendEv`) is what the mangling rules give and no non-Windows build has
been inspected. The wait on a mid-batch close is bounded by one native batch call, which on CPU
could be many seconds on long segments; the two-second-in closes measured here were short and say
nothing about that worst case. And a future vendored build that stops exporting the symbol reverts
to the abort — `uindosill doctor` now warns under the backend's line when that is so.

### What is still unmeasured about CUDA

- **VRAM.** Not measured, and invisible to the harness, which samples host working set only. How
  close 16 GB comes to being a constraint is unknown. The counter that would measure it exists and
  is vendor-neutral — the WDDM performance counters `\GPU Process Memory(pid_*)\Dedicated Usage` /
  `Shared Usage` per process and `\GPU Adapter Memory(*)\Dedicated Usage` per adapter, verified on
  the second machine on 2026-08-16 (its idle figures are in the machine block below) — and the
  harness does not read it yet. On this machine even the idle figure — what the desktop holds
  before any model loads — is unmeasured, and it is the term every "fits" in
  `docs/V2-ASK-THE-TRANSCRIPT.md` decision 4 depends on.
- **Long audio on CUDA.** The three-hour file has been run on CPU only. Ten minutes says nothing
  about whether device memory accumulates over hours.
- **Any other GPU.** `sm_86` and `sm_89` cubins are present, so 30-series and 40-series cards should
  work, and neither has been run. Anything older gets PTX and a driver JIT that these measurements
  deliberately never triggered; the first-run cost on such a card is unknown and could be minutes.
- **The x86 instruction-set baseline**, exactly as for the other two backends.

### All five quantisations, diffed against f16 over 2 h 55 m — with a noise floor under them

Measured 2026-08-15 on the machine in the table above. This is the first time every catalogue entry
has been run against the same long file, and the first time the comparison has had a **control**
under it.

Audio is `CSB384.mp3`, the same episode as the three-hour CPU run near the top of this file — the
duration reads `10523.376000`, matching that record exactly — 48 kHz mono, two hosts, disfluent and
overlapping. That is the material `docs/MODELS.md` asks for and the ten-minute chunk could not
supply.

**Method, because the obvious tool is wrong here.** `scripts/compare-transcripts.ps1` aligns by word
index, and the section on the ten-minute laptop comparison records why that overstates a
quantisation diff: one insertion desynchronises every pair after it, and the total-count guard
cannot fire when insertions and deletions cancel. So these are **word-level Levenshtein distances**
over token sequences, which assume no alignment. Two figures, as that earlier analysis reported —
raw tokens, and with case and all non-alphanumeric characters removed.

`scripts/word-distance.ps1` is that measure, and reproduces every figure below:

```powershell
.\scripts\word-distance.ps1 -Reference runs\csb-f16-cuda\CSB384.json `
                            -Candidates runs\csb-q8_0-cuda\CSB384.json,runs\csb-q4_k-cuda\CSB384.json
```

It reads `segments[].words[].w` — the model's own token stream, the same one
`compare-transcripts.ps1` counts, which is why the token totals here are not `wc -w` of the `.txt`.
It will also read the `.txt`, and that gives *slightly different* numbers (q8_0 0.408% rather than
0.415%) because the rendered form tokenises differently. **The figures below are the JSON ones**;
the script prints which form it read so the two can never be quietly mixed.

**The control first, because it bounds everything else.** Same weights (`f16`), same audio, same
machine — only the backend differs:

| Same model, different backend | Raw edits | Raw % | Normalised | **Normalised %** |
|---|---|---|---|---|
| CPU f16 vs CUDA f16 | 47 | 0.157% | 33 | **0.110%** |

That is the floor. Any quantisation figure below is only meaningful to the extent it stands above
it.

| vs CUDA f16 (29,915 tokens) | Raw edits | Raw % | Normalised | **Normalised %** | × floor |
|---|---|---|---|---|---|
| q8_0 | 216 | 0.722% | 124 | **0.415%** | 3.8× |
| q6_k | 427 | 1.427% | 259 | **0.866%** | 7.9× |
| q5_k | 770 | 2.574% | 505 | **1.688%** | 15× |
| q4_k | 1,256 | 4.199% | 804 | **2.688%** | 24× |

**The ladder is monotonic and roughly doubles per step down**, which is the shape a well-behaved
quantisation series should have. Referencing the *CPU* f16 run instead of the CUDA one moves the
figures only slightly — q8_0 to 0.358% and q4_k to 2.603% — so the ranking does not depend on which
f16 run is treated as the reference.

**Nothing resembling the failure this file warns about occurred.** The analogous ONNX INT8 export
collapsed to 24.8% long-audio WER against 7.8% for fp32, fluently and silently. Across three hours
of hard audio the worst entry here diverges from f16 by 2.69% of tokens. Whatever these
quantisations cost, it is not that.

**What the differences are.** Sampled from q4_k: a large share is punctuation and capitalisation
(`eight forty, as opposed` against `eight forty as opposed`), some are real substitutions
(`I was like` → `it was like`, `podcast` → `podcasting`), and at least one is a dropped fragment —
f16's `It's all self-influenced. And were like, let's let's zygote` loses its tail entirely. So the
normalised column is not merely a punctuation artefact being tidied away; content is lost too, and
the raw column is the one that counts if you care about the text as written.

**What this is not.** It is divergence from f16, **not** a WER: there is no ground truth for this
episode, both transcripts can be wrong in the same place, and the f16 reference has never been
checked against a human transcript. It is one file, one machine, one backend for the ladder itself.
And q8_0 at 3.8× the backend noise floor is a real signal but the same order of magnitude as it —
which is the honest reading of the smallest number in the table, and a reason not to treat 0.415% as
precise.

Speed did not separate them: every run sat at RTF 0.004–0.005, because on this GPU the decoder is
not the bottleneck. The speed case for quantisation lives on CPU, where the second machine measured
q4_k at 1.5× f16.

## Still open

### The CUDA drop's licensing — read, recorded, and the notice gap closed

`cublas64_12.dll`, `cublasLt64_12.dll` and `cudart64_12.dll` are NVIDIA proprietary binaries under
the CUDA Toolkit EULA, not MIT. `NOTICE.md`, `docs/LICENSING.md` and
`src/Parakeet.Core/Licensing/Attribution.cs` between them listed five MIT components and no NVIDIA
entry, and `Attributions.Components` is what `uindosill notice` and the app's Licences tab render —
so the gap reached the shipped product.

**Closed 2026-08-15.** The EULA was read at https://docs.nvidia.com/cuda/eula/index.html: §2.6
(Attachment A) lists `cudart`, `cublas` and `cublasLt` as redistributable with an application, in
version-numbered variants; §1.1.2 and §1.2 attach the conditions (material additional functionality,
the files reached only by the application, no stand-alone distribution, no implied endorsement).
`docs/LICENSING.md` records the reading, how this product stands against each condition, and the one
clause — the `"This software contains source code provided by NVIDIA Corporation."` notice — that is
scoped to sample source code and does not apply here. The component entry now renders in both
surfaces, verified through `uindosill notice`, and two tests hold it up.

**Three things are still unverified and are not paperwork.** No lawyer has read any of it. The text
read was the current online EULA rather than a copy contemporaneous with the CUDA 12.8 toolkit these
binaries came from. And the archives came from `mudler/parakeet.cpp`'s release rather than from
NVIDIA, so this project's compliance rests on an upstream redistribution it did not perform and has
not audited.

A fourth thing was checked and found absent rather than assumed: **nothing in the EULA as read
requires the licence text to travel beside the binaries**, the way MIT does for the other backends.
So no file is dropped into `native/win-x64/cuda/`, and the cudart archive shipping no licence text
of its own is consistent with that rather than a gap to be patched.

**The MIT half of this is fixed.** `build/NativeAssets.targets` used to glob only native binaries
(`*.dll`, `*.so`, `*.dylib`), so the `LICENSE` shipping inside every `lib-` archive never reached
the build output: an MIT binary redistributed without the notice MIT requires. The glob now also
takes `native/**/LICENSE`, confirmed 2026-08-15 by building both apps and reading the output
directory — one LICENSE per backend, beside the binary it covers. Two things that fix does *not*
cover, both outside the build: a vendorer who unpacks only `parakeet.dll` puts the breach back, and
nothing in the build can detect it, which is why `docs/NATIVE-BINARIES.md` now says to keep the
LICENSE beside the binary; and the cudart archive ships **no licence text at all**, so the NVIDIA
paragraph above is not solved by anything here.

### The parity result proves less than the headline

parakeet.cpp's `docs/parity.md` records every published Parakeet checkpoint validated byte-for-byte
against NeMo 2.7.3 at WER 0.0. That is a real result and it is why this engine was chosen over the
alternatives. It is also **one 7.4-second LibriSpeech fixture, CPU, batch 1, greedy**. It proves the
port is numerically faithful to NeMo on that input. It says nothing about quantisation quality and
nothing about long audio.

### GGUF quantisation quality is unmeasured

There is no measured WER for q8_0 or q4_k on Parakeet. The analogous ONNX INT8 export was measured
at **24.8% long-audio WER against 7.8% for fp32** — and it collapsed *silently*, producing fluent
wrong text rather than obvious garbage. Assume nothing. Measure against f16, on real audio, before
recommending any quantisation. The catalogue says so on every entry.

**The heading is now too strong, and is kept for the part of it that still holds.** There is still no
**WER** for any quantisation on Parakeet, because no ground truth exists for any audio this project
has run — that is the sense in which quantisation quality remains unmeasured, and it is the sense
that matters before recommending one.

What does now exist is a full **divergence-from-f16 ladder across all five entries on 2 h 55 m of
real disfluent speech**, with a backend noise floor measured under it: q8_0 0.415%, q6_k 0.866%,
q5_k 1.688%, q4_k 2.688% of normalised tokens, against a CPU-versus-CUDA floor of 0.110%. Monotonic,
no collapse, and nothing like the ONNX INT8 precedent. The measurement, its method and its limits are
in the desktop section above; an earlier and much smaller signal — q4_k at 1.62% on ten minutes — is
in the second-machine section below.

None of that clears any quantisation. Divergence from f16 is not error, and f16 itself is unverified
against a human transcript.

### Memory beyond three hours

Settled for the durations measured: memory does not accumulate, and the profile falls over the
second half of a three-hour run (above). What remains open is only whether the equilibrium keeps
stepping up with length — it was 941 MB above the model at ten minutes and about 1.5 GB at three
hours. Nothing rules out a further step at ten or twenty hours, and nothing suggests one.

### Long-audio behaviour beyond the one long file

Three hours is now measured and clean, forced cuts included (above). That is **one file, one pair
of speakers, one quantisation, one machine**. What it does not cover: single-speaker material with
no conversational pauses, where the cap would fire hundreds of times instead of four, and where
whatever the forced cut costs in accuracy would actually be visible. A lecture or an audiobook is
still the case nobody has run.

### Real Windows hardware, beyond the runs above

The CLI and the desktop app both run on Windows, and Media Foundation decoding is exercised by the
ten-minute and three-hour runs. What remains untested is breadth: those runs were mp3 and AAC.
mp4, mkv, wmv and flac all go through the same reader and none has been opened.

### Laptop CPU performance

RTF 0.10 on a 16-core desktop says nothing about a 4-core laptop, which is what most users have. No
independent, methodology-disclosed Parakeet benchmark on a 4–8 core mobile chip exists; the most
conservative published claim is around 0.2. Since the ABI exposes no thread count, a small machine
cannot even be tuned — it gets whatever ggml picks.

A laptop has now been measured — **RTF 0.1417 on CPU** — which narrows this without closing it. That
machine is a 10-core/20-thread Zen 5 part, not the 4-core case the paragraph above is about. See
below.

### The second machine, measured

| | |
|---|---|
| Host | the laptop — hostname withheld |
| OS | Windows 11 Home (10.0.26200), x64, Spanish locale |
| CPU | AMD Ryzen AI 9 365 w/ Radeon 880M — **10 cores, 20 threads**, 2.0 GHz base |
| Memory | 24 GB across 4 modules, configured 7500 MT/s (rated 7500) — **of which the BIOS carves out 8 GB for the iGPU** (UMA frame buffer: the driver reports 8,589,934,592 bytes dedicated and Windows sees 15,994 MB of physical memory; the BIOS setting itself was not read, this is what the OS and driver report) |
| GPU | AMD Radeon 880M (integrated), driver 32.0.13022.3006 dated **2025-01-22** (Vulkan `driverInfo` 24.30.22.03) — **no NVIDIA device**. Vulkan heaps, from `vulkaninfo` 2026-08-15: heap 0 device-local 7.75 GiB with a `VK_EXT_memory_budget` budget of **7.36 GiB** — that is the carve-out; heap 1 host-visible 7.81 GiB (budget 7.42 GiB); heap 2 device-local 256 MiB; `maxMemoryAllocationSize` 2 GiB; `VK_KHR_cooperative_matrix` revision 2 and **no bfloat16 extension** |
| GPU memory held, idle | From the Windows performance counters on 2026-08-16, about 01:00 local, no model loaded, a browser and an editor open: **`\GPU Adapter Memory(*)\Dedicated Usage` 2,149 MiB, `Shared Usage` 191 MiB**; by process, `\GPU Process Memory(pid_*)\Dedicated Usage` has `dwm` at 2,548 MiB, `firefox` 1,087 MiB, `explorer` 166 MiB. The per-process figures are commitments and sum past the adapter's held total; the adapter figure is what the carve-out is holding. Since heap 0's 7.36 GiB budget above is `VK_EXT_memory_budget`'s moment-to-moment figure net of other holders, with 2.1 GiB held the budget is nearer 5.6 GiB (arithmetic) — a number that moves with what is open, not a constant of the machine |
| Storage | 954 GB NVMe SSD |
| Runtime | .NET 10.0.11, SDK 10.0.400, PowerShell 7.6.4 |
| Weights | `tdt-0.6b-v3-f16`, 1.34 GiB, sha256 `8ba47343…fc5abb22` — matches the catalogue pin |
| Native | `cpu` and `vulkan` vendored from parakeet.cpp v0.5.0, ABI 6; **`cuda` deliberately not vendored** |

`uindosill doctor` reports **`cpu ok — abi 6`** and **`vulkan ok — abi 6`**. CUDA is not applicable:
there is no NVIDIA device, so `-Backends vulkan,cpu` is the only sensible sweep here.

Audio is the same episode as the three-hour file above — `CSB384.mp3`, 48 kHz mono, whose duration
reads `10523.376000` and matches the recorded 10,523.376 s exactly. The ten-minute chunk is **not**
the desktop's `chunk.m4a` and must not be compared word-for-word with it: **the offset used to cut
the original is recorded nowhere in this repository**, so it cannot be reproduced. This one is
`ffmpeg -ss 8293 -t 600 -vn -c:a aac -b:a 128k -ar 48000 -ac 1`, written down so that this chunk, at
least, can be. It also reaches AAC via **opus** rather than the documented mp3 → AAC chain.

| | Desktop (9950X / RTX 5080) | **This laptop (Ryzen AI 9 365 / 880M)** |
|---|---|---|
| Real-time factor, CPU | 0.0818 | **0.1417** |
| Decode time for 600 s, CPU | 49.1 s | **85.0 s** |
| Second CPU run | — | 0.1360 / 81.6 s (**4.1% apart**) |
| Peak host working set, CPU | 2,397 MB | 2,360 MB / 2,332 MB |
| Real-time factor, Vulkan | 0.0110 | **0.0349** (needs a knob, since made the default — below) |
| Vulkan against own CPU | 7.4x | **4.06x** |
| Cores / threads | 16 / 32 | 10 / 20 |

**CPU is 1.73x slower than the desktop's CPU** on the same episode and the same model. Structural
invariants all held: 106 segments, 1,606 words, **0 non-monotonic**, 0 past the end of the audio,
96.5% coverage, longest segment 26.43 s. No segment reached the 30 s cap, so the forced-cut path did
not run here either. Segment and word counts are not comparable to the desktop's 98 / 1,573 because
this is a different 600 seconds of the episode.

Two caveats on that number. It is **one clip on one machine on mains power** with the Windows power
scheme on high performance, recorded because a laptop figure without the power state is the same
species of unreproducible number as a GPU figure without a driver version. And the working-set
profile is **still rising at 100% of the run** (+546 MB last tenth against first) where the desktop's
three-hour run rose and then fell. On ten minutes that is most likely the collector not yet having
been pushed — the desktop profile only turned over around 60% of a far longer run — so it is neither
evidence of accumulation nor evidence against it.

#### Vulkan needs one environment knob on a Radeon 880M, and then it is 4x the CPU

Out of the box, **no Parakeet model loads on Vulkan here**, and not for a timing reason. The device
enumerates fine:

```
ggml_vulkan: 0 = AMD Radeon(TM) 880M Graphics (AMD proprietary driver) | uma: 1 |
             fp16: 1 | bf16: 0 | warp size: 64 | shared memory: 32768 |
             int dot: 1 | matrix cores: KHR_coopmat
```

and then the model load fails and the process dies with `0xC0000409`
(`STATUS_STACK_BUFFER_OVERRUN`), after a `vkDestroyFence: Invalid device` validation error. It
reproduces exactly outside the harness, so it is deterministic rather than a harness artefact.

**The model file is not the problem.** Its SHA-256 matched the catalogue pin at install, and the CPU
backend decoded 1,606 words from it on the same machine minutes later.

**It is not the model, and it is not the model's size.** `tdt-0.6b-v3-q4_k` — 643.92 MiB, less than
half of f16, and a different quantisation entirely — **fails identically**: same load message, same
`vkDestroyFence` error, no output. The CPU backend then decoded that same file in the same run. So
two different quantisations at 1.34 GiB and 0.63 GiB both fail on Vulkan and both succeed on CPU.

| Model | Size | Vulkan | CPU |
|---|---|---|---|
| `tdt-0.6b-v3-f16` | 1.34 GiB | fails at load | RTF 0.1417 |
| `tdt-0.6b-v3-q4_k` | 643.92 MiB | fails at load | RTF 0.0944 |

That kills the size hypothesis outright — 644 MiB against heap 0's 7.36 GiB budget (the 8 GB UMA
carve-out in the machine block above; a UMA device can also spill into the 7.81 GiB host-visible
heap, without any knob) and a 2 GiB single-allocation cap is not a memory problem — and it kills
"f16 specifically", since q4_k is not f16. **What is left is the device or the vendored Vulkan
build, not the weights.**

**It is the bf16 coopmat shader variants, and there is a workaround.** The vendored `parakeet.dll`
carries 25 `GGML_VK_*` environment knobs. Two of them make the model load and decode; the rest
change nothing:

| Setting | Result | Device reports |
|---|---|---|
| *(none)* | fails at load | `matrix cores: KHR_coopmat` |
| `GGML_VK_DISABLE_COOPMAT=1` | **loads and decodes** | `matrix cores: none` |
| `GGML_VK_DISABLE_BFLOAT16=1` | **loads and decodes** | `matrix cores: KHR_coopmat` |
| `GGML_VK_DISABLE_F16=1` | fails at load | — |
| `GGML_VK_DISABLE_INTEGER_DOT_PRODUCT=1` | fails at load | — |
| `GGML_VK_PREFER_HOST_MEMORY=1` | fails at load | — |
| `GGML_VK_ALLOW_SYSMEM_FALLBACK=1` | fails at load | — |
| `GGML_VK_DISABLE_HOST_VISIBLE_VIDMEM=1` | fails at load | — |

Disabling bfloat16 fixes it **while leaving `KHR_coopmat` enabled**, so the broken thing is not
cooperative-matrix support in general.

**It fails in `vkCreateDevice`, before any shader is compiled.** That was not visible through these
bindings — the C ABI only returns NULL — and it took upstream's own `parakeet-cli` to surface:

```
transcribe failed: vk::PhysicalDevice::createDevice: ErrorExtensionNotPresent
```

`VK_ERROR_EXTENSION_NOT_PRESENT` means device creation was handed a device extension this driver does
not expose. `vulkaninfo` confirms the shape: the device exposes **`VK_KHR_cooperative_matrix`
(revision 2)** and **no bfloat16 extension of any kind**.

**So this is an upstream defect, not a misconfiguration.** ggml reports **`bf16: 0`** — it has
already determined bfloat16 is unsupported here — and then still requests a bfloat16 device extension
at `vkCreateDevice`, which cannot succeed. Nothing about the model, the weights or the shaders is
involved; the device is never created, so every model fails identically, which is exactly what f16
and q4_k both did.

An earlier revision of this section said the bf16 *shader variants* failed to build. That was wrong,
and it was wrong because the binding's NULL return carried no information. Corrected here rather than
quietly rewritten: the reproduction that settled it is upstream's binary, not this one.

**Why it asks for an extension it has just said is unsupported — and why the upstream workaround is
a newer driver.** Read from `ggml/src/ggml-vulkan/ggml-vulkan.cpp` at `ggml-org/llama.cpp` master
on 2026-08-15, the day it was last touched: the backend requests `VK_KHR_shader_bfloat16` at
`vkCreateDevice` in **two** places. One is gated on the extension actually appearing in the
device's extension list — that is the check behind the `bf16: 0` in the banner, and on this driver
it correctly stays off. The other is gated on `coopmat_bf16_support`, which is set while walking
`vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR`: if the driver enumerates a bf16 × bf16 → f32
subgroup-scope shape, the flag is set and the extension is requested, **and the extension list is
never consulted for it**. The only thing that clears that flag is `GGML_VK_DISABLE_BFLOAT16`, which
is exactly why that knob — and disabling coopmat wholesale, which skips the walk — are the two
settings that load. So this driver enumerates such a shape while exposing no such extension. Two
things are inferred rather than observed here: that enumeration was not dumped on this unit (the
`vulkaninfo` build here does not print the cooperative-matrix property list), and the source read
is master, not the older ggml snapshot inside the vendored `parakeet.dll` — the same shape is
inferred for that build from the identical symptom and the identical knob.

"Upstream defect" stands — the second request should be gated on the extension list too. The
implication that nothing upstream fixes it does not: **AMD added `VK_KHR_shader_bfloat16` to its
Windows driver in Adrenalin 25.8.1** — the release note, read 2026-08-15, lists it under "Expanded
Vulkan Extension Support" — and the driver on this laptop is dated **2025-01-22**, which predates it.
On a driver that ships the extension the request should succeed and the knob become unnecessary.
**Whether this unit is fixed by a driver update is not measured.** It is the cheapest experiment in this
repository: update, run `uindosill bench` on Vulkan with `--vk-bf16` (bf16 left on) and without it
(the default, bf16 disabled), and record the driver floor in the machine block above. Until somebody
does, the knob stays and every Vulkan figure in this section is a figure for driver 32.0.13022.3006.

**Upstream's own server reproduces it on this driver — 2026-08-16.** llama.cpp b10448's
`llama-server`, from the Windows Vulkan release zip, loading `Qwen3-0.6B-Q8_0.gguf` with `-ngl 99`:
without the knob, `ERROR: vkDestroyFence: Invalid device [VUID-vkDestroyFence-device-parameter]`
and the process never reached `/health` in 300 s; with `GGML_VK_DISABLE_BFLOAT16=1` in its
environment it served in 2.61 s. Same driver, same knob, a different binary and a different symptom
text — a fence destroyed on an invalid device is what a failed `vkCreateDevice` looks like from a
caller that does not check the return, and it is inferred rather than observed that the create call
failed the same way here. The run is in *Upstream llama.cpp on the second machine* below.

##### The workaround is in the product, and — since 2026-08-16 — on by default

`ParakeetCppOptions.DisableVulkanBFloat16` sets the knob before the model is loaded. Measured through
the CLI on this machine when it was still an opt-in flag: **exit 0, `backend=vulkan`, RTF 0.0352**,
106 segments, 1,605 words — matching the environment-variable figure it replaces.

**It was off by default deliberately, and that was a judgement rather than an oversight.** Turning
it on for every Vulkan device would have changed the configuration under which every Vulkan figure
in this file was measured, on an RTX 5080 that this machine cannot re-measure. Flipping the default
was a one-command experiment on the desktop, and defaulting it on before that experiment would have
been exactly the unmeasured claim this file exists to prevent.

**That experiment has now been run, on the desktop, and the default is on.** Machine: the 9950X /
RTX 5080 desktop from the first machine table, driver 610.88 — the same driver every earlier Vulkan
figure names — Windows power scheme *High performance*, `tdt-0.6b-v3-f16`, Vulkan backend, one fresh
process per run so each run pays its own device initialisation and model load, and the figure is
`processingSec` from the transcript, as everywhere else in this file. **The audio is not
`chunk.m4a`**, which no longer exists (see the Audio row of that table): it is 600 s of the same
episode, cut from the audio-only stream of the published video (`p2-xdg_JMfg`, format 140, AAC-LC
44.1 kHz stereo, 10,523.4 s) with

```
ffmpeg -ss 8438 -i csb384-p2-xdg_JMfg.m4a -t 600 -vn -c:a aac -b:a 128k -ar 48000 -ac 1 csb384-8438.m4a
```

— the laptop's own cut, recorded at the top of *The second machine, measured*, with a different
offset and written down for the same reason. The offset is the timestamp in the link the maintainer
supplied; whether it coincides with the lost `chunk.m4a`'s offset is not knowable from this
repository, so **the 6.57 s figure in the desktop table is not a like-for-like baseline for anything
below** and is treated as indicative only. The decision rests on the two arms against each other, on
one file, which is self-consistent.

The shader cache was handled by emptying `%LOCALAPPDATA%\NVIDIA\GLCache` before each arm's first
run rather than by discarding a run and hoping, so both arms have a labelled cold figure. That first
OFF run came back at 14.286 s — within 1.5% of the 14.07 s cold figure recorded above, which is what
a genuinely cold cache looks like on this driver. Before the ON arm's cold run the cache — by then
holding only what the OFF run had compiled — was emptied again; one further OFF run afterwards
(6.669 s, discarded) left it warm for both arms. The twelve timed runs were **interleaved** — OFF,
ON, OFF, ON, OFF, ON, then ON, OFF, ON, OFF, ON, OFF — so thermal or clock drift lands on both
arms. The device banner is the evidence the knob took: `bf16: 1` on every OFF run, `bf16: 0` on
every ON run, `matrix cores: NV_coopmat2` on all fifteen.

| Vulkan on the RTX 5080, `csb384-8438.m4a` | bf16 left on (`--vk-bf16`) | bf16 disabled (**the default**) |
|---|---|---|
| Cold shader cache, first run, discarded | 14.286 s (0.0238) | 13.606 s (0.0227) |
| Warm, six runs each, interleaved | 6.692, 6.707, 6.700, 6.760, 6.781, 6.711 s | 6.780, 6.699, 6.769, 6.705, 6.759, 6.763 s |
| **Mean** | **6.725 s, RTF 0.01121** | **6.746 s, RTF 0.01124** |
| Range, `(max − min) / mean` | 1.3% | 1.2% |
| Standard deviation | 0.036 s | 0.035 s |
| Segments / words | 114 / 1,632 | 114 / 1,632 |

**Disabling bf16 costs 0.021 s on 6.7 s — 0.31% — which is smaller than one standard deviation of
either arm, a quarter of either arm's own range, and under a tenth of the 5.9% run-to-run range
this file already records for Vulkan on this machine.** The two arms' ranges overlap almost
entirely (6.692–6.781 s against 6.699–6.780 s). If there is a cost it is under half a percent, and
six runs each cannot resolve it from noise. So the default is the setting that loads on every device
measured so far, and it now is: on.

**It also changes nothing in the output.** `scripts/compare-transcripts.ps1` on an OFF transcript
against an ON one: all 114 segment boundaries identical, **0 of 1,632 word tokens, 0 timestamps
and 0 confidences differing**, joined text byte-identical. That is not a near-tie surviving —
it is the same result as OFF against OFF, and across all fifteen runs (both cold, the re-warm and
the twelve timed) the `.txt` and `.srt` outputs hash identically and the JSON differs only in
`processingSec` and `realTimeFactor`. Two things follow, one of them inferred. Measured: **Vulkan
on this device is deterministic run to run**, which the table of backend comparisons above had
established for CPU and CUDA but never for Vulkan. Inferred, and marked so: an f16 model producing
identical tokens, timestamps and confidences with bf16 cooperative matrices on and off suggests
this model's kernels do not take the bf16 path on this device at all, which would also be why the
knob costs nothing. Nothing here confirms that reading; a bf16 model, or a profile, would.

The cold figures are a bonus rather than a claim: the ON arm's cold run was 0.68 s faster than the
OFF arm's, consistent with fewer pipeline variants to compile, but that is one run against one run
and inside the day-to-day scatter of cold runs, so it is recorded and not relied on.

One more thing the session showed, recorded because it is larger than the effect being measured.
Three verification runs of the rebuilt binary — default, `--vk-bf16`, `--vk-disable-bf16` — taken
straight after `dotnet build` and `dotnet test` came in at **7.197, 7.282 and 7.025 s**, all three
arms alike, and three more default runs a minute later, back to back, at 6.739, 6.790 and 6.776 s.
That is a 7% swing from whatever the machine was still doing after a build, landing in
`processingSec` on both settings equally. It is why the twelve timed runs above were interleaved
rather than run as two blocks, and why a single Vulkan timing from a machine that has just done
something else is worth a second run before it is believed — the same lesson as gotcha 20, from a
different cause.

What the flip means in the code: `ParakeetCppOptions.DisableVulkanBFloat16` defaults to `true`;
the desktop app, which sets nothing, inherits it — so the laptop's app can now load a model on
Vulkan, which it could not before because it had no flag to pass. `--vk-disable-bf16` on
`transcribe` and `bench` is kept and now only spells the default out. **`--vk-bf16` leaves bf16
enabled**, so the arm this measurement needed stays reachable — for repeating it after a driver
or ggml bump, or on a driver that has fixed the extension request — and giving both flags at once
is a usage error rather than a precedence rule. A value already in the environment still wins over
the option either way, as before.

Auto-detection was considered and is not possible: ABI v6 exposes no way to ask a device about
bf16 before loading a model, and a failed Vulkan load cannot be retried in the same process. So the
choice has to be made before there is any evidence to make it with, and now it is made the way that
loads. The failure text still says what happened: a Vulkan load that fails with the workaround
applied says so and rules the bf16 path out; one that fails with it turned off names the knob, says
the retry is impossible and why, and suggests the cpu backend to tell a device problem from a bad
model file.

Two mechanism findings came out of building it, both recorded as gotcha 21:
**`Environment.SetEnvironmentVariable` does not reach the native `getenv`** on Windows and reports
no error, so the knob has to go through `ucrtbase!_putenv`; and **a failed Vulkan load poisons the
device**, so a second `parakeet_capi_load` in the same process dies in `vkCreateFence` rather than
returning NULL again.

One difference between the two failures is recorded without explanation: the f16 attempt died with
`0xC0000409` (`STATUS_STACK_BUFFER_OVERRUN`) while the q4_k attempt exited `1`. Same load message,
different process ends.

**Vulkan on this machine, once it loads**, against the CPU figures above:

| | Cold (cache emptied) | Warm, mean of 3 | Range | vs CPU |
|---|---|---|---|---|
| `GGML_VK_DISABLE_BFLOAT16=1` | 0.0417 (25.0 s) | **0.0349** (20.9 s) | 3.2% | **4.06x** |
| `GGML_VK_DISABLE_COOPMAT=1` | 0.0513 (30.8 s) | 0.0473 (28.4 s) | 5.3% | 3.00x |

**Keeping coopmat and dropping only bfloat16 is 1.36x faster** than disabling cooperative matrices
wholesale, so the choice of workaround is worth getting right. The cold figure in the first row was
taken through `measure-second-machine.ps1` and labelled by it; the second row's cold figure is a
cache-emptied repeat with the CLI, which is the same method used for the NVIDIA experiment above.

**The cold-shader penalty on this AMD driver is small: 1.20x, against NVIDIA's 2.1x.** That is one
machine and one driver, and it was measured by emptying `%LOCALAPPDATA%\AMD\VkCache` rather than by
finding the machine pristine — the same substitution the NVIDIA finding rests on. It does mean the
one-shot cold-run hazard, which on the desktop doubled the first transcription, costs about 20% here.

**How the pristine cold run was actually spent, and recovered.** It was not lost to a casual
transcription — it was lost to *this investigation*. The knob-matrix probe ran through the CLI rather
than the harness, and the first setting that worked decoded the whole file and compiled the
pipelines, taking `AMD\VkCache` from 282,624 bytes to 802,816. Emptying the cache and re-running
through the harness recovered a properly labelled cold figure, which is why one exists at all. The
lesson is the one this file already records for NVIDIA: a first-run figure is a property of the
shader cache, not of the machine, so it is reproducible as long as the cache can be cleared.

Before the workaround was found, three failed Vulkan attempts grew the cache by only ~20 KB, because
a load that never completes builds no pipelines.

#### The first quantisation signal on this engine — and a tool that overstates it

`docs/UNPROVEN.md` has said throughout that GGUF quantisation quality here is unmeasured. It still is
not *measured* — there is no ground truth on this audio, so nothing below is a WER — but for the
first time an f16 and a q4_k transcript of identical audio exist on identical hardware, and they can
be diffed.

`scripts/compare-transcripts.ps1` reports **727 of 1,606 word tokens differing**. That figure is an
artefact and should not be quoted. The script aligns the two transcripts **by word index**, so one
insertion desynchronises everything after it and every subsequent pair is counted as a difference.
The tell is in its own output: joined text of 8,326 against 8,319 characters, which is not what 727
genuinely different words looks like.

**The script does guard against this, and the guard has a hole worth knowing.** Line 165 compares
`$left.Words.Count -ne $right.Words.Count`, and when the totals differ it refuses the per-word
figures outright — *"index alignment is not valid … would be an artefact of the offset rather than a
measurement"* — and prints the first divergence instead. That guard fired correctly on the CPU
versus Vulkan comparison below, at 1,606 against 1,605 words. It cannot fire here, because f16 and
q4_k both produced **exactly 1,606** words: the insertions and deletions cancel in the total while
leaving the sequence misaligned. **A total-count check cannot detect offsetting edits**, so the one
case that defeats the guard is two transcripts of coincidentally equal length — which is the likely
case whenever two variants of the same model are compared.

Measured instead as a word-level edit distance, which does not assume alignment:

| | Edits | Share of 1,606 |
|---|---|---|
| Raw tokens | 50 | 3.11% |
| Case- and punctuation-insensitive | **26** | **1.62%** |

So **24 of the 50 differences are punctuation or capitalisation only**, and the real divergence
between q4_k and the f16 reference is 26 tokens in 1,606. The first divergence is a comma. All 106
segment boundaries are identical, as expected — segmentation is energy-based and runs on the audio,
not the model.

**What that does and does not say.** It is one 10-minute file, one machine, one backend, no ground
truth, and 1.62% divergence from f16 is not 1.62% error — both transcripts could be wrong in the same
places. What it does do is fail to reproduce the failure mode this file warns about: the analogous
ONNX INT8 export collapsed to 24.8% long-audio WER while staying fluent. Nothing resembling that
happened here. That is one piece of evidence against a silent collapse on this material, not a
clearance for q4_k.

q4_k is also **1.5x faster on CPU** (RTF 0.0944 against 0.1417) with a lower peak working set
(1,598 MB against 2,360 MB), which is the trade the quantisation is for.

**The comparison table earlier in this file is unaffected.** Those backend comparisons reported 0, 2
and 2 differing tokens, so there was no insertion to desynchronise the alignment and the index
assumption held. The tool is reliable when transcripts are nearly identical and misleading exactly
when they are not — which is the regime a quantisation comparison lives in.

#### CPU against Vulkan on this machine

The same comparison the desktop table makes, repeated here with the workaround in place. Both f16,
same audio, same machine, Vulkan under `GGML_VK_DISABLE_COOPMAT=1`:

| | |
|---|---|
| Segment boundaries | **all 106 identical** |
| Words | 1,606 on CPU, **1,605** on Vulkan |
| First divergence | word 336, around 131.89 s — `'guess,'` against `'guess'` |

The guard fired, so no per-word deltas are quoted: unequal totals mean index alignment is invalid and
the script says so rather than producing numbers. The desktop's CPU-versus-Vulkan row (2 differing
tokens of 1,573) is therefore **not** comparable with this one — that pair had equal word counts and
this one does not, so the two lines are measuring different things. What both show is that
segmentation is identical across backends, which is expected: it runs in managed code on the audio.

**A second hazard, found the same way.** The harness writes a collected `chunk-cpu.json` at
`runs/<machine>/`, and the printed cross-machine command points at it. A second run with a
*different model* silently overwrote it: that file now holds the q4_k transcript, not the f16 one.
Anyone following the printed command would diff a desktop f16 transcript against a laptop q4_k
transcript and read the result as a machine difference. The per-run copies under `cpu/` survived
correctly as `chunk.json` and `chunk (2).json`, so nothing was lost — but the collected file is
model-blind and its name does not say so.

#### The three-hour file, end to end on Vulkan here — and it is a new encode

The episode came back to this machine on 2026-08-16, but not the file. The `CSB384.mp3` behind
every figure above went with the old clones — gitignored files go when the folder does — and was
re-obtained from the episode's public source (format 140, AAC 129k m4a) and extracted with ffmpeg
to mp3 at 48 kHz mono: **108,002,975 bytes where the maintainer's archived copy reads
105,643,318**, container duration `10523.399563` where every record above says `10523.376000`.
Same episode, same shape, **a different encode, 24 ms apart** — so no transcript of this file is
word-for-word or id-for-id comparable with the desktop's 1,488-segment transcript or with anything
else measured against the original, and the pin decision 6 requires will say which file the labels
are against. Whether the archived copy is byte-identical to the original was not checkable from
here.

Through `lab.ps1 measure -Backend vulkan`, `tdt-0.6b-v3-f16` (re-downloaded, digest matched the
pin), with `GGML_VK_DISABLE_BFLOAT16=1` in the environment. This run predates the default flip in
`66b5291`: on the build it ran, nothing set the knob for you, and the first attempt without the
variable died at load in one second, exactly the failure the knob section above records. On the
current build the engine disables bf16 itself; the environment variable remains what the
llama.cpp scripts below need, since upstream's binaries have no such default:

| | |
|---|---|
| Audio | 10,523.4 s (2:55:23), 48 kHz mono mp3 — the new encode above |
| **Real-time factor** | **0.0316** (332.4 s to decode 10,523.4 s) |
| Segments | 1,378 — longest 30.00 s, **11 at the cap**, largest gap 4.62 s |
| Coverage | 10,118.2 s emitted (96.1%) |
| Words | 29,909 — 0 non-monotonic, 0 past the end of the audio |
| Host working set | peaked 699 MB at 50% of the run, settled to 627 MB (+177 MB last tenth against first) |

Three hours through the iGPU in five and a half minutes, structural invariants clean, and the
forced cut ran eleven times on this machine's first long file. Two of the ten-minute caveats above
now have longer answers: the RTF (0.0349 on the chunk) came down to 0.0316 as the load cost
amortised, the same shape as the desktop's CPU sequence; and the working-set profile that was
"still rising at 100%" on ten minutes **turned over at 50% here**, which is the desktop's
three-hour pattern — evidence against accumulation where the chunk could give none. The transcript
trio is under `runs/20260816-033132-vulkan/` (gitignored).

#### The harness itself, on its first Windows run

`measure-second-machine.ps1` had never executed on Windows. It behaved correctly, including in the
failure it was not written for:

- **All eight machine-block probes returned a value.** None threw, none degraded to `not reported`,
  so the wrapping that stops a failing probe aborting the run it describes is *still* unexercised.
- **The cold-Vulkan guard works.** It printed the warning, ordered Vulkan first, and then **refused
  to mark the cold run spent on a run that measured nothing** — which is the behaviour that matters,
  and it was reached by accident rather than by design.
- **It refused to derive anything from the failed run**, printing `THE RUN FAILED. Nothing below
  measures anything.` and leaving the Vulkan row as `— <- the run failed; nothing was measured`.
- **It correctly reported no GPU or driver line**, there being no `nvidia-smi` here — the one probe
  whose degraded path did fire.

#### The dispatcher forwards an array, on real input

`lab.ps1 measure -Path chunk.m4a -Backend cpu -Formats srt,txt,json,vtt,vtt-words` reported
`formats srt,txt,json,vtt,vtt-words` and wrote all five files. The bug that design exists to prevent
does not occur.

#### What the word-timed WebVTT actually contains

From that run, and this is bytes rather than perception:

| | |
|---|---|
| Cues | 163, of which **0** had no word timings to carry |
| Words tagged | 1,606 |
| Inline timestamps | 1,442 of 1,443 possible — **1 dropped** |
| Ordering | every timestamp strictly inside its cue and strictly increasing |
| Alignment | tags stripped, byte-identical to the plain `vtt` |

The single dropped tag is the drop-rather-than-nudge path firing on real audio for the first time
recorded here.

#### Someone has now watched it play

This is the one claim in the project that no test can make, and it is no longer unasserted. A human
loaded `chunk.m4a` and its `chunk.words.vtt` into `scripts/preview-words-vtt.html` and watched, in
both browsers the page is written for:

| | `::cue(:past)` / `::cue(:future)` in the video element | The page's own panel |
|---|---|---|
| **Edge** | **highlights word by word** | highlights word by word |
| **Firefox** | no highlight | highlights word by word |

**The advance was smooth** — one word at a time, not jumping and not lagging.

Three things follow, and they are worth keeping apart.

**The format works in a real player.** Every other assertion about `vtt-words` is about bytes. This
one is about a word lighting up on a screen in time with speech, and it now has an observer behind
it rather than a timestamp read back out of the file it came from.

**Firefox's blank result is the documented behaviour, not a defect**, and this is the first time that
has been confirmed rather than assumed. Firefox implements neither pseudo-class, so a *correct* file
shows nothing there. The page's two-view design exists precisely to tell that apart from bad
timings, and it did its job: the disagreement resolved the way the design predicts.

**The page's parser independently agrees with the harness.** It reported *163 cues, 1442 inline
timestamps* — the same figures `measure-transcribe.ps1` computed from the same file, by separate
code.

What this does **not** establish: it is one file, one backend, one browser pair, one observer. The
file watched came from a **CPU** run, so it says nothing about whether the one-to-two-frame timestamp
differences between backends recorded above are perceptible — that comparison still has no observer.

#### Two open items, checked against this transcript

**The cue builder's no-word path still has not been observed.** Of 106 segments, **0** lack a `words`
array, matching the 163-cue figure above. The proportional path remains held up by its unit test
alone, now on a second machine and a second file.

**Provenance survives to the output.** `quantisation` reads `f16` and `backend` reads `cpu` in the
transcript JSON, so the dropped-provenance defect recorded below is fixed in real output rather than
only in principle.

#### A default that contradicts the obvious command

`uindosill transcribe <audio>` uses the **recommended** catalogue entry, `tdt-0.6b-v3-q8_0` — not
whatever is installed. With only `f16` present it fails with `Model 'tdt-0.6b-v3-q8_0' is not
installed`, and `-m tdt-0.6b-v3-f16` is required. The measurement scripts pass the model explicitly
and never hit this; anyone following a bare `transcribe` instruction will.

**The recommended entry became `tdt-0.6b-v3-f16` on 2026-08-15**, which removes this failure on the
machine above: f16 was the model installed there, so a bare `transcribe` would now find it. The
defect underneath is unchanged and was not the flag — `transcribe` still resolves to the
*recommended* entry rather than to whatever is installed, so a user whose only installed model is a
quantisation meets the same error with the two ids swapped. That path has not been re-run here; it
is the same code with different data in it.

### The rest of the dispatcher, checked on the same machine

`lab.ps1` had never executed on Windows either. Beyond the array forwarding recorded above:

**The listing works and is accurate.** It renders every task and marks nothing unforwardable.
Verified independently rather than taken on trust: the dispatcher declares 23 task parameters plus
`-Task`, and the union of what the target scripts declare via `Get-Command` is exactly those 23 —
nothing a task accepts is missing from the dispatcher, and nothing the dispatcher declares is
accepted by no task.

That check first ran against **four** tasks and 22 parameters. `word-distance` and `vendor-cuda`
have been added since; re-run 2026-08-15 against the current five — `measure`, `machine`, `compare`,
`word-distance`, `vendor-cuda` — the invariant holds unchanged. What moved is the count, not the
property being asserted.

**The drift path works.** `lab.ps1 compare -Formats srt,txt -Backend cpu` throws `Task 'compare'
(compare-transcripts.ps1) does not take: -Formats, -Backend`, naming both and listing the six it does
take.

**The documented failure mode is real on PowerShell 7.6.4.** Checked in isolation before audio was
available: a `ValueFromRemainingArguments` dispatcher splatting `@Rest` binds the array's elements
positionally and lands `chunk.m4a` in `-Backend`, failing its `ValidateSet`, while declaring the
parameters and splatting `$PSBoundParameters` delivers `String[]` with all 5 elements. The header's
reasoning is therefore not folklore, and the real run above confirms the fix end to end.

Two details a second machine was supposed to surface, and did:

- **Configured equals rated** (7500 MT/s both) on soldered LPDDR5. The script's comment expects these
  to diverge under XMP/EXPO; here they agree, so the distinction is real but unillustrated.
- **The block is locale-dependent.** On Spanish Windows `OSArchitecture` renders as `64 bits` and the
  active power scheme as `Alto rendimiento`. Machine blocks from different locales are therefore not
  textually comparable — not a bug, but it means a machine block is not a diff target.

**What the guard still has not done is refuse a warm run.** It was exercised only on the cold path
and on a failure, both recorded above. No run on this machine has ever reached the state where the
guard is supposed to say no, so that branch stays untested.

### Vendoring on the second machine, and what it confirmed

`cpu` and `vulkan` only. Four things that had been recorded from one machine are now independently
checked on a second.

**The pinned digests reproduce.** Both archives hash to exactly the SHA-256 recorded above —
`0e9b8a30…` for cpu, `45278980…` for vulkan — and each contains exactly the four documented files,
with `parakeet.dll` at exactly the documented byte counts (2,008,064 and 59,453,952). That table was
written from one machine's download and now reproduces on another.

**A rebuild is required after vendoring, and the symptom is indistinguishable from not vendoring.**
Dropping the DLLs into `native/` and running `doctor` still reports all three backends unavailable,
because the loader searches `AppContext.BaseDirectory` while `build/NativeAssets.targets` evaluates
its glob at project-evaluation time. The targets file says so in a comment. It is repeated here
because the error text points at vendoring, which has already been done correctly, and says nothing
about the rebuild that is actually missing.

**The LICENSE gap was real rather than inferred — and both mechanisms behind it are now closed.**
After the rebuild the output held `native/win-x64/<backend>/parakeet.dll` and nothing else: the
`LICENSE` inside each archive was not copied, exactly as the licensing section predicted from
reading the glob. That was observed rather than argued, which is why it stays recorded here rather
than being deleted.

Two separate mechanisms dropped that file, and **neither was closed by the other's fix**, which is
the part worth keeping:

- **The build never copied it.** `build/NativeAssets.targets` globbed binaries only, so an MIT
  binary shipped without the notice MIT requires. Fixed — the licensing section above records the
  change and how it was verified.
- **The repository never ignored it.** `.gitignore` matched `native/**/*.dll` alone, so unpacking an
  archive whole left its `LICENSE`, `README.md` and `parakeet_capi.h` *untracked* rather than
  ignored, where a `git add -A` would sweep them into the repository the vendoring exists to keep
  them out of. The rule is now `/native/` — the whole directory. Checked 2026-08-15 with
  `git check-ignore`: `LICENSE`, `README.md`, `parakeet_capi.h` and `parakeet.dll` under
  `native/win-x64/<backend>/` all report ignored.

**The ISA baseline is still unconfirmed.** The CPU backend loads here, but the Ryzen AI 9 365 is
Zen 5 and has AVX2 and AVX-512, so this machine cannot expose an AVX2 requirement either. Two
machines have now run these binaries and neither can answer the question.

The suite passes on this machine both before and after vendoring — **247 tests, 246 passed, 1
skipped, 0 failed**, Release, from a 0-warning build. The skip is
`CompressedFormatsExplainWhyTheyCannotBeOpenedHere`, which is `Assert.SkipWhen(IsWindows)` by design
because Media Foundation handles those formats here. Weights and natives being present changes
nothing about the suite, which confirms the "no weights" claim in `CLAUDE.md` from the other
direction.

### Upstream llama.cpp on the second machine — the first model loaded here, and what it needed

Nothing under `docs/V2-ASK-THE-TRANSCRIPT.md` had been run against a language model on either
machine until 2026-08-16, when `scripts/spike-llama-server.ps1` (`lab.ps1 spike`) was run twice on
this laptop with a model chosen for its size, not its relevance: `Qwen/Qwen3-0.6B-GGUF`'s
`Qwen3-0.6B-Q8_0.gguf` (639,446,688 bytes), a 7,779-token stand-in prompt, `-c 16384`, `-fa on`,
`--fit off`, `--reasoning-budget 0`. Upstream release **b10448**, unpacked from its own zips:

- cpu — `llama-b10448-bin-win-cpu-x64.zip`, 18,464,245 bytes, sha256
  `9038c34d23769ac04a1f59835f41129f3810b3144bb8edc35183507baf827435`
- vulkan — `llama-b10448-bin-win-vulkan-x64.zip`, 34,807,759 bytes, sha256
  `cbe06a7a2fce85aaf625aed29eff730e07a6c8257a07e4e3b6b54cb1e9fbd9dd`

Both byte counts match the releases API; both digests are first readings — no other machine has
hashed them yet.

| | cpu | vulkan, no knob | vulkan, `GGML_VK_DISABLE_BFLOAT16=1` |
|---|---|---|---|
| Seconds to `/health`, first start | 1.12 | **never** — `vkDestroyFence: Invalid device`, killed at 300 s | 2.61 |
| Second start | 1.02 | — | 1.02 |
| Prefill, 7,779 tokens | 210.7 tok/s (36.9 s) | — | **807.1 tok/s** (9.6 s) |
| Decode, 160 tokens | 25.2 tok/s | — | **37.0 tok/s** |
| Second request, same prefix | prompt 43 ms — the cache was reused | — | prompt 29 ms |

**Three things this is the first measurement of here.**

- **Upstream Vulkan does not load a model on this driver without the knob, and does with it** — the
  bf16 mechanism above, seen on llama.cpp's own binary rather than parakeet's. Prefill is 3.8× and
  decode 1.5× the CPU figure on this 0.6B file; nothing here says what a 9B does, and the 40k
  prefill the feature needs was not run.
- **The per-process GPU counter sees the server, and the memory comes back when it is killed.**
  `\GPU Adapter Memory(*)\Dedicated Usage` read 1,126.5 MiB idle, **3,583.0 MiB** with the model
  loaded, 3,582.9 after the prefill and the answer, and **1,126.0 MiB after `Stop-Process`**;
  `\GPU Process Memory(pid_<server>_*)\Dedicated Usage` read **2,456.8 MiB** for the server, plus
  194.3 MiB shared, rising by 30 MiB during the prefill. The 2,457 MiB is consistent with the file
  (610 MiB) plus a 16,384-token f16 cache for this model's 28 layers of 8 KV heads × 128 (1.75 GiB
  by arithmetic) plus a compute buffer. On the iGPU "dedicated" is the carve-out, so this is the fast
  heap being spent, and it is what decision 4 of the v2 note calls the measurement it did not have.
- **`--reasoning-budget 0` does not stop this model reasoning aloud.** Both answers began "Okay,
  let's see. The user wants…" — the thinking pushed into the answer channel rather than removed.
  A 0.6B model, twice, one prompt; recorded because it is exactly the grammar-versus-reasoning
  interaction the v2 note's decision 6 flags as unchecked, and a grammar would have stopped it.

The runs are under `runs/20260816-012603-spike-cpu/` and `runs/20260816-012811-spike-vulkan/` on
this machine (gitignored); each holds the server's stderr, `samples.csv`, `spike.json` and the
Markdown block above's source. Not run here, and not runnable here: the CUDA branch of that script,
which is the desktop's.

#### A grammar over live ids, against the same server — measured the same night

Same machine, same Vulkan server, same 0.6B model, `--reasoning-budget 0` throughout; each figure
is one run.

**The A/B that decision 6 of the v2 note called unchecked.** The same 7,779-token prompt and
question, once unconstrained and once under a GBNF whose citation rule enumerates exactly the 180
live segment ids: unconstrained, the model reasoned aloud ("Okay, let's see. The user wants…") and
hit the 160-token cap **mid-citation** at 34.2 tok/s; under the grammar it produced two clean
bullets citing `[S1]` and `[S12]`, finished at 60 tokens, 30.1 tok/s. So on this model the grammar
did what the budget could not — the reasoning prose is gone, only live ids are possible, and the
output terminates — at a 12% decode cost on this run. The `[S12]` bullet was vacuous and its
citation supports nothing, which is the citation-precision problem in one line: a grammar makes
citations *resolvable*, not *right*. Also measured rather than read: **`grammar` is accepted on
`/v1/chat/completions`** at b10448 — the server README documents it on `/completion` only.

**The abstain branch, through `scripts/measure-answers.ps1`'s self-test.** A synthetic
120-segment transcript with three planted facts and a four-question labelled set (two pointed with
verbatim answers in the prompt, one adversarial, one needle). With the grammar's abstain
production present — decision 6's design — the model abstained on **all four**, both false
abstains included, under two different system-prompt phrasings. With the production removed
(`-NoAbstainBranch`): it answered everything — one pointed answer correct (S40, gold S40–S41),
one on the wrong segment, the needle missed, and the adversarial question **answered with an
invented citation**, because the exit no longer existed. False abstention and invention traded directly
against each other by one grammar rule, on a model this small. The grammar cost on that run was
larger than the morning's figure: 68.9 tok/s unconstrained against 38.9 under the grammar. Two
one-run figures, 12% and 44%, same model, same backend, different prompts; the spread is the reason
the lab script measures it per run instead of quoting either number.

A 0.6B model finding one needle in 120 segments would have been the surprise; none of this says
anything about the v2 candidates. What it establishes is the harness: the pin check, the plant, the
citation parsing, pass and fail both observed, and a label-validation path (the gold quote must
appear in the gold span's text) that reports a bad label as a labelling error rather than a model
failure.

#### The real transcript through the spike, and the first candidate — 2026-08-16, later the same day

The stand-in prompt retired when the episode's transcript came back to this machine (the
three-hour Vulkan run in the second-machine section above — a new encode, new ids). Same release
b10448, same knob, `runs/20260816-033917-spike-vulkan/` and `runs/20260816-035050-spike-vulkan/`.
The candidate is the v2 note's laptop row made real: `unsloth/Qwen3.5-9B-GGUF`'s
`Qwen3.5-9B-Q4_K_M.gguf`, 5,680,522,464 bytes — the byte count the note already records — sha256
`03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8`, a first reading, taken from
the hub listing's LFS oid on 2026-08-16 per `docs/MODELS.md`'s procedure and verified against the
downloaded file twice (once at download, once by the spike).

**The transcript does not fit the number the v2 note carries.** The 169,291-byte `.txt` is
**50,892 tokens** under the 0.6B's chat template and **51,712** under the 9B's — the note's "about
40k tokens" (its line "Three hours of transcript is roughly 30k words, about 40k tokens") is
word-count arithmetic, and the measured figure is a quarter larger. Every "fits at 40k" line in
that note inherits this correction for this episode.

**Raising `-c` does not raise the ceiling: `n_ctx_train` does.** At `-c 53248` the 0.6B server
allocated the larger cache (6,332.9 MiB committed against 5,179.5 at 40,960) and then refused the
50,892-token request at the same 40,960 — Qwen3-0.6B's training context. The cache fit; the model
did not. So the 0.6B ran a 124,757-byte head of the transcript cut at a line boundary, and the
candidate — 262,144 native context — ran the whole file.

| | 0.6B Q8_0, 37,062-token head, `-c 40960` | **Qwen3.5-9B Q4_K_M, 51,712 tokens, `-c 53248`** |
|---|---|---|
| Prefill | 193.1 s (191.9 tok/s) | **467.9 s (110.7 tok/s)** |
| Decode, 160 tokens | 11.0 tok/s | **9.8 tok/s** |
| Same prefix again | prompt 95 ms | prompt 566 ms |
| `/health`, first / second start | 1.08 / 1.03 s | 3.64 / 2.54 s |
| Server committed, loaded | 5,179.5 MiB + 218.3 shared | **6,310.2 MiB + 1,237.2 shared** |
| Adapter dedicated, loaded | 6,122.6 MiB | **7,211.1 MiB** |

**The spill the laptop paragraph predicted is now a measurement.** Loaded, the adapter held
7,211.1 MiB dedicated — the fast heap's 7.36 GiB budget, spent — with **1.4 GiB pushed into
shared memory**, exactly the UMA overflow the "does not fit in the fast heap with a browser open"
paragraph reasons through. Memory did not grow past the pre-allocated cache during prefill
(+4.2 MiB), and after `Stop-Process` the adapter returned to 900.9 MiB. And the depth cost that
was invisible at 7,779 tokens is the story at 37k+: the 0.6B's prefill fell from 807.1 to
191.9 tok/s and its decode from 37.0 to 11.0 tok/s between the stand-in and the head run — same
model, same server, deeper context. So the laptop conclusion now has its numbers: the candidate
prefills the whole episode in **7 minutes 48 seconds**, decodes at **9.8 tok/s** afterwards, and a
follow-up question against the cached prefix costs half a second of prompt time — an opt-in with a
progress bar, and a usable conversation once paid.

**`--reasoning-budget 0` does not bind the candidate's template, and the tokens go somewhere the
morning's runs could not see.** Both spike answer rows above generated their full 160 tokens into
**empty `message.content`**: under `--jinja`, Qwen3.5-9B's template forces the think block open,
the budget flag does not close it, and the server files everything under `reasoning_content` —
where the spike, the answers script and any client reading `content` see nothing. Probed by hand
against the same server to settle it: at `max_tokens` 300 the whole budget burned as reasoning and
finished `length` with empty content; at 2,000 the model thought for ~550 tokens, closed the
block, and answered the toy question correctly with a citation; **with a grammar attached, the
grammar shaped the thinking** — 52 grammar-legal tokens, `finish_reason` stop, all filed as
reasoning, content still empty. The 0.6B's morning finding ("reasoning pushed into the answer
channel rather than removed") is the same defect with opposite plumbing: its template emits
`<think></think>` closed, so its overflow lands where a client looks. `measure-answers.ps1` now
passes `--reasoning-format none`, which keeps the stream in `content` where the grammar's shape is
the answer; the run that found this (`runs/20260816-040827-answers-vulkan/`, four empty answers
scored as four failures) is kept as the evidence. Two smaller harness findings from the same hour:
an answer with no `[S<n>]` citation at all crashed the script's strict mode (`Parse-Citations`
returned an empty list through the pipeline as `$null`; the 0.6B always cited, so the first
model to produce one found it), and a forced-open template puts a literal `<think>` in front of
`content` under `--reasoning-format none`, which would defeat the abstain exact-match — stripped
now, constructed from the probe rather than observed, since the 9B never abstained.

**The self-test pair, same set, same flags, same day** (`runs/20260816-042020-answers-vulkan/` and
`runs/20260816-041546-answers-vulkan/`; the synthetic 120-segment transcript and four-question
set from the morning): the 0.6B still abstains on all four — correct once, on the adversarial
question. The 9B passes both pointed questions (S40/S41 and S75 against gold), **finds the
planted needle** (S91 cited five times), and **fails the adversarial question by inventing
citations with the abstain exit available** — `[S1]` three times for a ferry schedule the
transcript never mentions. One model prefers the exit it should not take; the other declines the
exit it should. The abstain production traded failure modes with scale, on four questions, once —
which is exactly what the thirty labelled CSB384 questions exist to measure properly. Read the 9B's
answer texts before quoting its passes: under the grammar the bullet prose is narrated thinking
("I will scan the transcript for keywords…") — the ids resolve and overlap gold while the words
support nothing, the citation-precision problem in its purest form yet. Grammar decode cost, one
run each: 65.4 → 44.0 tok/s on the 0.6B (33%, a third figure inside the recorded 12–44% spread)
and 15.6 → 14.6 tok/s on the 9B (**6.4%**) — the rejection-sampling cost shrinking as the model
grows is one run's evidence, not a law.

### The confidence threshold is set by guess, and the first real data disagrees

`TranscriptionOptions.LowConfidenceThreshold` defaults to 0.45. In the one real transcript, the
words the model actually got wrong — mangled proper nouns and a channel handle — came back at
**0.47, 0.53, 0.62 and 0.64**, all of them *above* the threshold, while everything it got right sat
at 0.98–1.00. On that evidence 0.45 flags nothing useful and the right value is nearer 0.7. One clip
is not enough to retune a default on, so it has not been changed; it is written down here so the
next person with a corpus knows where to start.

### The native library's instruction-set baseline

Resolved for v0.5.0: the Windows library asset is `parakeet-v0.5.0-lib-win-<backend>-x64.zip` and it
contains a single self-contained `parakeet.dll`, which is the first name the loader tries. Its
shipped `parakeet_capi.h` is byte-identical to the header these bindings were written against, at
ABI 6. Digests are in `docs/NATIVE-BINARIES.md`.

What is still open is the **ISA baseline of those binaries**. Upstream has no Windows CI —
`ci.yml` runs `ubuntu-latest` only, and Windows binaries are built only at release-tag time — so
nobody has confirmed whether they require AVX2. There is also **no `win-arm64` asset in v0.5.0**, so
Windows on ARM needs a source build before that RID means anything.

### The CI publish artefact carries the natives — observed

Since 2026-08-15 the `publish-windows` job runs `scripts/vendor-natives.ps1` before the `win-x64`
publish and then asserts `native/win-x64/{cpu,vulkan}/{parakeet.dll,LICENSE}` in both apps' output.
It was first written and checked against the local equivalent on one Windows machine — the same
script, the same two `dotnet publish` commands, `uindosill doctor` run from the published CLI — and
the two steps that left unobserved have both since been observed, the same day:

- **The script ran on Linux `pwsh`**, in the first CI run after the commit
  (https://github.com/jkkma/uindosill/actions/runs/31914418118): both archives downloaded, both
  hashed to their pins, four files extracted per backend, `parakeet.dll` at 2,008,064 and
  59,453,952 bytes with `LICENSE` beside each, both digests found in `docs/NATIVE-BINARIES.md`, and
  the assertion step saw all four files in both apps' output. The whole job took a minute.
- **The artefact that run uploaded was downloaded and run.** `uindosill-win-x64` from that run,
  fetched with `gh run download` onto the second machine: `doctor` from its `cli-win-x64/`
  reported `ok — abi 6` for cpu and vulkan, each from its own directory under the artefact, and the
  vulkan `parakeet.dll` in it is byte-identical to the one vendored locally.

That is the chain end to end: Linux runner, pinned download, digest check, cross-publish, upload,
download, load on Windows. What it is not is a transcription: `doctor` proves the library and its
imports resolve and the ABI is 6, not that it decodes — and nothing has been transcribed from a
CI-built binary. The `win-arm64` leg still publishes with an empty `native/`, because upstream has
no arm64 asset.

### The model catalogue — resolved

All five entries now pin the byte size and SHA-256 read from the repository's LFS listing, and the
file names that had been conventional guesses are confirmed by it. The f16 pin is corroborated
independently: an installed copy that transcribed three hours of audio hashes to the value the
repository publishes.

What this does **not** settle is quantisation quality. A pinned digest proves you received the file
upstream serves; it says nothing about whether q4_k transcribes correctly. See the section above.

## Resolved while building this — with evidence

These were open in the founding brief and are now settled, by reading the upstream source rather
than by assuming.

### Threading: one context, one thread at a time — verified

`src/parakeet_capi.cpp` contains no mutex, no lock guard and no thread-local state, and
`struct parakeet_ctx` holds one shared `std::unique_ptr<pk::Model>` plus a mutable `last_error`
string. Two concurrent decodes on one context race on both. `ParakeetCppEngine` serialises calls
with a semaphore and says so in its documentation.

### Cancellation: not possible mid-decode — verified

No entry point in ABI v6 takes an abort callback or a cancellation flag. A decode already running
must finish. `EngineCapabilities.SupportsDecodeCancellation` is `false`, cancellation is checked
between batches, and the result of an in-flight decode is discarded.

### Thread count: not settable at all — verified, and it contradicts the plan

The founding brief says to cap decode threads at about eight. **No entry point in
`include/parakeet_capi.h` (ABI v6) takes a thread count, and `include/parakeet.h`'s C++ surface
exposes none either.** ggml decides. The policy is still right, so `DecodeThreadPlanner` implements
it and `EngineCapabilities.SupportsThreadCount` reports `false`; `uindosill transcribe --threads`
prints a line saying the value does not reach the decoder, and the UI shows no thread control.
Applying the cap needs an upstream change.

### The language hint: the ABI takes it, this model declines it — verified, and measured

`--threads` above is the neighbouring finding, and the two are **not** the same shape. No entry
point takes a thread count at all. A target language it does take: `ParakeetCppEngine` calls
`parakeet_capi_transcribe_pcm_batch_json_lang` and hands it `TranscriptionOptions.Language`
unchanged. The catch is in the header's own words — `target_lang` is *"Ignored by non-prompt
models"*, honoured only by multilingual prompt-conditioned (nemotron) checkpoints, and an unknown
locale on a prompt model *"returns NULL and sets the context's last error"*.

`tdt-0.6b-v3` is not such a checkpoint, and the **invalid locale is what proves it**. Four runs over
one 70 s English clip, Vulkan with bf16 disabled on an AMD Radeon 880M — no hint, `-l en`, `-l de`,
and the nonexistent `-l zz` — produced byte-identical output, SHA-256 `B116B0168BF54F94…`, every one
at exit 0. A prompt-conditioned model would have failed the `zz` decode through the NULL path at
`ParakeetCppEngine.cs:368`. It did not, so the string was never inspected. Reading the header alone
would not have settled this; only the locale that should have been rejected does.

So `--language` is inert for the whole catalogue, which is one checkpoint in five quantisations. An
upstream nemotron checkpoint would honour it with no change on this side.

Two consequences are recorded rather than fixed. `EngineCapabilities.SupportsLanguageSelection` is
`true` unconditionally at `ParakeetCppEngine.cs:124` — true of the ABI, false of the model, where
the sibling `SupportsThreadCount` is documented as true only when the value *reaches the decoder*.
And the JSON `language` field records the **request**, not a detection: `-l en` writes `"en"`, no
hint writes `null`. Correct as plumbing, and a claim the transcript cannot support, because this
model detects per segment and can disagree with the field. The symptom that started this: on an
English-only press conference, one segment came back in Cyrillic script, identically in all four
runs. Nothing in the CLI can currently constrain that.

### Batch resampling: pass the real sample rate — verified

`parakeet_capi_transcribe_pcm_batch_json_lang` forwards its `sample_rate` argument straight to
`Model::transcribe_pcm_batch_with_timestamps`, so the documented internal resampling applies to the
batch path too. Audio is handed over at its native rate and no resampler exists in this codebase.

## Found by the first real run

Both were invisible to 215 tests and to every synthetic fixture, and both showed up the moment a
real transcript was read closely.

### Provenance was silently dropped

`"quantisation": null` in every transcript ever written. The CLI and the app both passed the
quantisation into the engine, but `EngineCapabilities` had no field to carry it, so it never reached
`TranscriptDocument`. The one field that matters most for judging a transcript later — quantisation
quality on this engine being entirely unmeasured — was the one field the header could not report.

### The last segment ended after the file did

The final partial analysis frame is zero-padded so its energy is measured on the same basis as every
other frame. That padding was reaching the output: on a 30.014 s clip the last segment ended at
30.030 s. A transcript whose last timestamp is past the end of the media, and a subtitle cue a
player has nowhere to show. The padding is now trimmed from the emitted segment, with a test across
several partial-frame sizes.

## Found while building this

Two failures were found by writing tests rather than by reading anything, and both are the kind that
ship silently.

### An adaptive gate seeded on the first frame loses whole files

The first energy detector seeded its noise floor from the first analysis frame. Feed it a recording
that opens on speech — a clip already trimmed to the interesting part, which is the common case —
and it concludes that speech is the noise floor, sets the threshold above it, and returns **nothing
for the entire recording**. No error, no warning, an empty transcript. The fix is a fixed pessimistic
seed plus an absolute "this is definitely speech" ceiling the adaptive threshold may never rise
above. Regression tests: `RecordingThatStartsOnSpeechIsStillDetected`,
`SustainedLoudPassageDoesNotHideTheSpeechAfterIt`.

### A publish that is green in CI and framework-dependent everywhere

`SelfContained` was set in `Directory.Build.props` under a condition on a property the project files
set. `props` is imported *before* the project body, so the condition was evaluated against an empty
value and did nothing: `dotnet publish -r win-x64` produced eleven files and no runtime. It is the
same shape as the dropped `--self-contained` flag the brief warns about, arrived at from the
opposite direction. The settings now live in `Directory.Build.targets`, which is imported after the
project body, and the comment there records how to check.
