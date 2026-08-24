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
| GPU memory held, idle | From the Windows performance counters on 2026-08-24, about 03:13 local, no model loaded, no browser open — the Claude desktop app, Windows Settings and the NVIDIA overlay the only windows: **`\GPU Adapter Memory(*)\Dedicated Usage` 2,115 MiB, `Shared Usage` 51 MiB** (mean of 3 samples at 1 s); by process, `\GPU Process Memory(pid_*)\Dedicated Usage` has `dwm` at 6,704 MiB, `NVIDIA Overlay` 781 MiB, the Claude app 177 MiB. As on the laptop's entry, the per-process figures are commitments and sum far past the adapter's held total; the adapter figure is what the card is holding. `nvidia-smi` read 1,832 MiB used at the same moment — the two count differently, and the WDDM adapter counter is the one every fit figure in the v2 register is stated against. This was the largest unmeasured term in decision 4's arithmetic; at ~2.1 GiB it sits under the ~2.5 GiB that register's "fits" lines assumed, and it is a number that moves with what is open, not a constant of the machine |
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

**`processingSec` is the whole pass, not the model — found 2026-08-22, and every "decode time" in
this file is that figure.** The stopwatch that produces it wraps `TranscribeAsync` end to end, and
inside that stretch the container is decoded through Media Foundation, the audio mixed down and
resampled, and the segmenter run, block by block and serialised with the model: the read of a block
and the decode of the batch before it never overlap. Measured on the second machine with the canned
engine — which decodes nothing — `sample.m4a` (600.004 s of AAC) costs **1.77 s** of `processingSec`
on its own, `full-source.m4a` (3,287.6 s) 11.7 s and `two-hosts.mp3` (10,523 s) 9.1 s. Against 49 s
of CPU decode that is a rounding error; against this table's **3.86 s** on CUDA it is most of the
number, and that share has not been measured on the desktop. Since 2026-08-22 the transcript also
carries `decodeSec` and `decodeRealTimeFactor` — the time inside the model's decode calls alone,
summed over the pass — beside `processingSec`, which keeps its meaning so that every figure already
recorded stays comparable with every new one. Re-timed on the second machine the same day, same
file, same model, one run each, with the new field: **CPU `processingSec` 77.3 s (RTF 0.1288)
against `decodeSec` 74.78 s (0.1246), 2.5 s and 3.3 % outside the model; Vulkan 24.78 s (0.0413)
against 23.14 s (0.0386), 1.65 s and 6.6 % outside it.** (That CPU run is 9 % faster than the 85.0 s
in the second-machine table below — one run against one, on a laptop, and nothing here says which
of the two is the typical one.) **Re-timed on the desktop 2026-08-22 with the read separated** —
the machine in the table above, same driver 610.88, same v0.5.0 natives, Windows *High performance*,
`tdt-0.6b-v3-f16`, one fresh process per run, nothing else running — but **not on `chunk.m4a`, which
no longer exists** (see the Audio row): on `csb384-8438.m4a`, the 600.0 s cut of the same episode the
bf16 experiment below was run on, written down there with its ffmpeg line. One warm-up run each on
CUDA and Vulkan (3.834 s / 2.524 s and 6.448 s / 4.924 s, discarded), then CUDA and Vulkan
alternated five times each, then the CPU three times:

| `csb384-8438.m4a`, 600.0 s | CPU | Vulkan | CUDA |
|---|---|---|---|
| `processingSec` — the whole pass | 47.18 s | 6.90 s | **3.95 s** |
| `decodeSec` — the model's decode calls alone | 45.41 s | 5.29 s | **2.59 s** |
| Outside the model | 1.77 s (3.8 %) | 1.61 s (23.3 %) | **1.36 s (34.4 %)** |
| Real-time factor, pipeline / model | 0.0786 / 0.0757 | 0.0115 / 0.0088 | **0.0066 / 0.0043** |
| Runs, and range across them, pipeline / model | 3 — 1.9 % / 1.5 % | 5 — 4.6 % / 3.5 % | 5 — 10.8 % / 10.4 % |

Range is (max − min) / mean as in the table above; every run exited 0, and every run produced 113
segments — 1,637 words on the CPU and CUDA, 1,632 on Vulkan, a difference not investigated here. **So
on this file about a third of the CUDA pass and a quarter of the Vulkan pass is the read — the
container decode, mixdown, resampling and segmentation — and the model itself runs at RTF 0.0043 on
CUDA, 0.0088 on Vulkan and 0.0757 on the CPU.** The 1.36–1.77 s outside the model is the same order
as the 1.77 s the canned engine measured for the read alone on the second machine. What the table
above's **3.86 s** contains cannot be split after the fact, because its file is gone, and it is not
replaced: the 3.95 s here is a different cut of the same episode, and that the two land within 2.5 %
of each other is noted rather than relied on. Everywhere else in this file and in the README a
"decode time" or an RTF is still the pipeline figure, and this table is the only place any of them
has been split.

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

Produced by `scripts/compare-transcripts.ps1` as it was then, which aligned two transcript JSONs by
word index and reported segment-boundary, token, timestamp and confidence deltas. Since 2026-08-16
the script aligns by word-level edit distance instead (why: the ten-minute laptop section below).
On these four pairings the two methods coincide — the word counts are equal and the only token
differences are substitutions at the same positions — so the table stands as recorded; it has not
been re-run under the new script, because these transcripts did not survive the working copy
being recreated.

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

**Method, because the obvious tool was wrong here.** `scripts/compare-transcripts.ps1` aligned by
word index at the time, and the section on the ten-minute laptop comparison records why that
overstates a quantisation diff: one insertion desynchronises every pair after it, and the
total-count guard cannot fire when insertions and deletions cancel. So these are **word-level
Levenshtein distances** over token sequences, which assume no alignment. Two figures, as that
earlier analysis reported — raw tokens, and with case and all non-alphanumeric characters removed.
(Since 2026-08-16 `compare-transcripts.ps1` aligns the same way, with the same code; these
figures predate that and were never in doubt, because they never came from it.)

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

### Word error rate against human transcripts — all five entries, eleven hours, measured 2026-08-16

The first WER this project has. Everything above is divergence from f16, which says how far a
variant is from the reference model and nothing about whether either is right; this is each model
against what a human wrote down, on the same machine as the ladder above (Ryzen 9 9950X, RTX 5080,
driver 610.88), **CUDA** backend, and it is what `docs/PHASES.md` Phase 0 asked for — real,
disfluent, accented, with files over ten minutes.

**The corpus** is Rev.com's Earnings-22 Subset 10, pinned in `scripts/wer-corpus.json`: ten
English-language earnings calls, 58–78 minutes each, 40,037 s = 11.12 h in all, two companies each
from South Africa, the UK, the United States, India and Australia, at 16–44.1 kHz mono and stereo,
each with **two human transcripts** — verbatim (fillers, stutters and repetitions written down;
101,509 reference words after normalisation) and non-verbatim (lightly edited for readability;
96,181). Both are scored, because the model sits between them and the gap is information. Every
media file and transcript is pinned by byte count and SHA-256 at one upstream commit and checked
before scoring; the licence position is in `docs/LICENSING.md`. `scripts/measure-wer.ps1 -Backend
cuda` is the whole measurement, and `runs/wer/ladder-cuda/summary.{json,md}` is its output.

**The normaliser is stated, and it is not the leaderboard's.** `uindosill wer` scores tokens
lower-cased with punctuation removed, hyphens split, bracketed annotations dropped, six fillers
(uh, um, hmm, mm, mhm, mmm) dropped, `%` read as `percent`, and English cardinal number words
rendered as digits — all applied to both sides alike. The last rule exists because this model writes
numbers as words and the transcripts write digits: on the first call scored, `two hundred and fifty
two cents` against `262 cents` was five errors and is now the one substitution it should be, and that
one call moved from 15.7% to 13.9%. What is still not done — a year said in pairs (`twenty twenty-one`
→ `20 21`), contractions against their expansions (`gonna` / `going to`), spelling variants — the
published Open ASR normaliser does do, so **these figures are comparable to each other and not to a
leaderboard entry for the same model.** The raw figure, whitespace tokens with nothing normalised, is
printed beside every row so the size of the normalisation is visible: 29–30% for every model.

| Model | WER vs verbatim | S / D / I | WER vs non-verbatim | S / D / I | RTF (CUDA) |
|---|---|---|---|---|---|
| **f16** | **10.21%** | 5,599 / 1,927 / 2,840 | **13.40%** | 4,493 / 1,079 / 7,320 | 0.0040 |
| q8_0 | 10.23% | 5,594 / 1,936 / 2,855 | 13.41% | 4,505 / 1,071 / 7,318 | 0.0038 |
| q6_k | 10.17% | 5,603 / 1,895 / 2,827 | 13.34% | 4,520 / 1,024 / 7,284 | 0.0039 |
| q5_k | 10.17% | 5,593 / 1,882 / 2,844 | 13.43% | 4,536 / 1,047 / 7,337 | 0.0038 |
| q4_k | 10.15% | 5,659 / 1,832 / 2,814 | 13.40% | 4,609 / 985 / 7,295 | 0.0039 |
| f16, **CPU** (control) | 10.21% | 5,601 / 1,923 / 2,840 | 13.41% | 4,491 / 1,079 / 7,324 | 0.0687 (CPU) |

The last row is the backend control, run afterwards on the same machine's CPU
(`scripts/measure-wer.ps1 -Backend cpu -Models tdt-0.6b-v3-f16`, 45 m 51 s wall): f16 scores the
same on CPU as on CUDA to two decimals, and per file the two backends are within 0.04 points on
every call. Whatever separates the quantisations, if anything does, is therefore not the backend
they happened to run on. The RTF in that row is CPU and is not comparable with the column above it.

Per file, WER against the verbatim transcript, f16 first and the four quantisations beside it:

| Call | Country | f16 | q8_0 | q6_k | q5_k | q4_k |
|---|---|---|---|---|---|---|
| 4485192 | United States | 5.43% | 5.44% | 5.45% | 5.43% | 5.47% |
| 4483937 | UK | 6.81% | 6.91% | 6.87% | 6.76% | 6.68% |
| 4470684 | UK | 7.19% | 7.07% | 7.15% | 6.91% | 6.64% |
| 4474506 | United States | 8.06% | 8.11% | 8.05% | 8.20% | 8.02% |
| 4481952 | Australia | 9.05% | 9.05% | 8.66% | 8.55% | 8.60% |
| 4482383 | Australia | 9.43% | 9.47% | 9.43% | 9.43% | 9.45% |
| 4469088 | South Africa | 10.77% | 10.76% | 10.80% | 10.84% | 10.67% |
| 4453225 | South Africa | 13.87% | 13.88% | 13.72% | 13.95% | 13.91% |
| 4479944 | India | 15.06% | 15.15% | 15.10% | 15.17% | 15.14% |
| 4482613 | India | 15.93% | 15.96% | 15.98% | 15.85% | 16.48% |

**What it says about quantisation, which is what it was built to say.** Across eleven hours the
five entries land within **0.08 points** of one another against either transcript — 10.15% to
10.23% verbatim, 13.34% to 13.43% non-verbatim — and q4_k, the smallest, is not the worst on either.
Per file, no quantisation is systematically above f16: the mean per-file difference from f16 is
+0.02 points for q8_0 and −0.04 to −0.05 for the other three, and the largest single-file move in
either direction is 0.55 points (q4_k on the two UK/India calls, one each way), against a
between-file spread of ten points. So the divergence ladder above — 0.42% to 2.69% of tokens
differing from f16 — is real, and it is divergence in *which* words are wrong, not in how many:
q4_k disagrees with f16 on 2.7% of tokens and matches the human transcript exactly as often. On this
material there is no measurable accuracy cost to any quantisation in the catalogue, and nothing
resembling the silent collapse this file has warned about since the beginning.

**What it says about the model.** f16 at 10.2% against verbatim transcripts of accented earnings
calls, spread from 5.4% (a US call) to 15.9% (an Indian one) — the two US and two UK calls are the
four lowest, the two Indian calls the two highest, and how that compares with the paper's own
per-region results has not been checked. Against the non-verbatim transcript every model scores about three
points worse, and the counts say why: insertions go from ~2,800 to ~7,300 while substitutions fall.
The model writes down the repetitions and false starts that a readability edit removes — it is
closer to a verbatim transcriber — but it also writes `going to` where the verbatim transcript
has `gonna`, so it sits between the two styles and neither number is "the" WER; the pair is.

**What this does not settle.** One corpus, English, financial calls, one machine, one backend for
the ladder (with the CPU control for f16 in the table). The normaliser is this project's, and its residue —
paired years, contractions, spellings — is in every figure on both sides. The references are
human and not infallible, and the same alignment scores every model against the same imperfections.
The verbatim/non-verbatim gap is a property of this corpus's two styles as much as of the model.
And "no measurable cost" is a statement at the resolution this corpus gives — roughly a tenth of a
point over eleven hours — not a proof that no input separates them.

### The installer was built, run, updated and uninstalled — observed 2026-08-19

On the RTX 5080 desktop, against that machine's real
`%LOCALAPPDATA%\Uindosill\models` — **five files, 4.295 GiB**, the whole quantisation ladder. Every
weight was SHA-256'd before anything was installed, and again after each step. `scripts/package-windows.ps1`
built the packages; nothing here was done by hand except staging the second version.

| Step | What was run | Weights: files / differences from the baseline |
|---|---|---|
| baseline | — | 5 / — |
| install | `UindosillDesktop-win-Setup.exe --silent` → exit 0 | 5 / **0** |
| update | `Update.exe apply --silent --norestart`, 1.0.0-rc.1 → 1.0.0-rc.2 → exit 0 | 5 / **0** |
| uninstall | `Update.exe --uninstall --silent` → exit 0 | 5 / **0** |

"Differences" is `Compare-Object` over name, byte length **and SHA-256** for all five files. Zero at
every step means every weight is byte-identical to the baseline: `tdt-0.6b-v3-f16.gguf` still hashes
to `8BA47343…ABB22` after the uninstall, and the directory is still 4.295 GiB.

**The footprint, observed rather than read.** After install:

```
%LOCALAPPDATA%\UindosillDesktop\        236 files, 257.9 MB
  current\                              the published app, incl. native\win-x64\{cpu,vulkan}\{parakeet.dll,LICENSE}
  current\sq.version                    <id>UindosillDesktop</id> <version>1.0.0-rc.1</version> <channel>win</channel>
  packages\                             UindosillDesktop-1.0.0-rc.1-full.nupkg, .velopack_lock
  Update.exe                            3,866,112 bytes
  Uindosill.exe                         392,192 bytes — the root execution stub
HKCU\…\Uninstall\UindosillDesktop       DisplayName "Uindosill", InstallLocation, UninstallString
Desktop\Uindosill.lnk                   }  the vpk default --shortcuts Desktop,StartMenuRoot
Start Menu\Programs\Uindosill.lnk       }
```

After the uninstall, all four of those are gone — install directory, registry key, both shortcuts —
and `%TEMP%\velopack_UindosillDesktop` with them. **One thing is left behind:**
`%LOCALAPPDATA%\velopack\velopack_UindosillDesktop.log` (21 KB), which is Velopack's own log and
outside everything uninstall deletes. Worth knowing before someone reports the uninstall as
incomplete; it is 21 KB of text, and nothing reads it afterwards.

The update moved `current\sq.version` from `1.0.0-rc.1` to `1.0.0-rc.2` with `<channel>win</channel>`
unchanged, and `current\native\win-x64` still held `cpu, vulkan` afterwards. The Add/Remove Programs
`DisplayVersion` read `1.0.0` throughout, because both builds are `1.0.0` with different prerelease
tags and Velopack writes the numeric part — not a defect, but it means that field cannot be used to
tell two prereleases apart.

**What this establishes.** That the package id `UindosillDesktop` keeps uninstall away from
`%LOCALAPPDATA%\Uindosill\models` — the thing the whole packaging design turns on — is now an
observation on a machine with 4.295 GiB at stake, not an argument from reading Velopack's source. So
is the shape of the install, the fact that an update replaces `current\` while leaving the channel
and the natives intact, and the fact that uninstall removes what it is supposed to. (The build
observed here predates the uninstall cleanup hook — the next section — so "leaves the models" was
the correct outcome for it. On a current build the intended outcome is different, and unobserved.)

**What it does not.**

- **It is one machine, and that machine is not clean.** The desktop has the .NET SDK, the natives,
  and a `%LOCALAPPDATA%\Uindosill` that predates the installer. Nobody has run `Setup.exe` on a
  Windows machine with no toolchain, which is the machine the installer exists for.
- **Neither installer was run interactively.** Both went through `--silent`, so no dialog, no
  SmartScreen prompt and no splash screen has been seen by anyone. What an unsigned `Setup.exe`
  actually shows a first-time user on a current Windows build is unobserved, and it is the single
  most likely thing to surprise someone about a v1.0 download.
- **The application was never launched from the install.** The installed tree was inspected file by
  file; the window was not opened, nothing was transcribed, and no engine was loaded from an
  installed copy. The gap `docs/PHASES.md` has recorded since 2026-08-15 — no transcription from a
  CI-built binary — is unchanged and now also applies to an installed one.
- **The CUDA channel was built but never installed.** `UindosillDesktop-win-cuda-Setup.exe`
  (818.6 MB) was produced and its package contents verified; it has not been run, so nothing
  establishes that a CUDA install works, or how long installing 730 MB of NVIDIA runtime takes.
- **The update was staged by hand.** The second version's `.nupkg` was copied into `packages\` and
  `Update.exe apply` was run directly. That exercises Velopack's apply machinery on real packages of
  this application, and it is what `DownloadUpdatesAsync` leaves behind — but the app's own path to
  it did not run. *The update check has never found an update*, under **Still open**, is where that
  gap is recorded.
- **The delta package was generated, and never applied.** Packing 1.0.0-rc.2 after 1.0.0-rc.1
  produced `UindosillDesktop-1.0.0-rc.2-delta.nupkg` at **74,470 bytes** against a 77,462,188-byte
  full package, so the delta machinery works on this machine with vpk's bundled zstd. Whether
  `Update.exe` can apply that delta is untested — and it is exactly the thing velopack/velopack#1008
  reports broken for bsdiff deltas in this version line.

### ~~The uninstall cleanup hook has never run on a real machine~~ — it ran, it was unreliable, and the feature is gone

**Superseded 2026-08-23, the same night.** The section below was written before any installer
carried the hook. It then ran on a real machine and behaved in two incompatible ways on the same
build: against a synthetic directory of 43,789 files it deleted everything in 6.3 s, and against the
real 43,789-file directory it returned in 98 ms having deleted nothing. Every mechanism that would
explain the difference was tested and eliminated — scale, an exception in the callback (which exits
-1 and is reported as a failure, not as success), a reparse point, missing assembly metadata, a lost
registration, and Velopack declining to invoke the hook at all. **The failure never reproduced, and
the feature was removed rather than shipped in that state** (`docs/PHASES.md`, *Removed
2026-08-23*). What is left proven is only the negative: nothing this application does unattended
deletes a user's files, because there is no longer any code that does.

**The behaviour that replaced it is measured, on a locally packed installer, 2026-08-23.** A real
`vpk` package of the post-removal build, installed silently, with a 2,000-file decoy planted where
the data directory goes, then uninstalled through `Update.exe --uninstall --silent`: the decoy came
through **2,000 of 2,000 files**, and the install directory, the Add/Remove Programs key and both
shortcuts were gone. That is the whole of the promise — the uninstaller removes what it installed
and touches nothing the user downloaded. **It was packed here rather than by CI**, so what the next
release candidate adds is that the same holds for a package this machine did not build.

**One thing that run explains, and it is worth writing down.** With no callback registered, Velopack
still logs `Found fast exit hook: --veloapp-uninstall`, `Completed hook, exiting...` and
`Hook executed successfully (took 91.1288ms)` — the message names the *argument* it recognised, not
a handler it found. That is indistinguishable from the failing rc.3 run's own log (98.6846 ms,
"successfully", nothing deleted), so the log this project reasoned from could never have told a
registered-but-inert callback from an unregistered one. Any future hook needs its own evidence that
it ran, written by the hook, not by the installer.

The record below stands as what was believed at the time.

### The uninstall cleanup hook has never run on a real machine — noted 2026-08-23

Since 2026-08-23 the application registers Velopack's `OnBeforeUninstallFastCallback`, and
`UninstallCleanup` deletes `%LOCALAPPDATA%\Uindosill` — models, settings, the Python bundle — when
the product is uninstalled (`docs/GOTCHAS.md` gotcha 8 has the design). What is proven: the delete
and each of its guards run against rebuilt directory trees in `UninstallCleanupTests`, including
the real lock semantics on Windows and a real symbolic link elsewhere.

What is not: no `Update.exe --uninstall` has ever invoked it. An installer carrying the hook now
exists — `v1.0.0-rc.3`, packed 2026-08-23 — and has never been installed, so the gap moved from
"no installer" to "no install". Unobserved, specifically:

- **That the hook fires at all from a real uninstall.** The callback chain runs inside the
  installed stub under Velopack's 30-second fast-callback budget; nothing here has watched it do
  so, or timed the delete against a real multi-gigabyte `models\` directory.
- **What a hook failure looks like.** The cleanup swallows everything by design, so a machine
  where it silently achieves nothing would show a completed uninstall and an intact data
  directory — indistinguishable, from the uninstaller's exit code, from success.

The proof is the 2026-08-19 procedure rerun on a current build: hash the weights, install, uninstall,
and find `%LOCALAPPDATA%\Uindosill` gone rather than intact. Until someone does that, "uninstalling
removes your models" is a claim about code paths, not about a machine.

### rc.3 shipped three features that could not run, and the fixes are unreleased — observed 2026-08-23

Installing `v1.0.0-rc.3` on the laptop is the first time any installer this project built has been
run interactively by anybody, and it found five defects in one sitting (`docs/PHASES.md`, *Fixed
2026-08-23*). Three of them were one packaging fault: the package carried no yt-dlp, no Deno, no
ffmpeg and no libmpv, so opening a link, drawing a video's picture and adding a transcript to a
recording were all dead in it.

**What is established.** That the channel prune deleted the three companion directories, on a
machine where they had been vendored — read out of the script rather than inferred, and the CI
workflow separately never vendored them at all. That the shipped build falls back to
`SystemAudioPlayer` when libmpv is absent, which is why video failed silently rather than loudly.
That the download-and-verify step for the bundled weights works against the real pin: the speech
detection graph was fetched from the catalogue's URL on 2026-08-23 and matched both the pinned
2,327,524 bytes and the pinned SHA-256 exactly.

**What is not.**

- **No package has been built with any of these fixes.** The prune, the companion assertions, the
  bundled weights, the icon and the splash are all held by tests and by a parse check; none has been
  through `vpk`. The 455 MiB the bundled weights add to each channel is arithmetic from
  `models.json`, not a figure read off an asset.
- **The 2 GiB release-asset limit is GitHub's published one, not one this repository has hit.** The
  largest asset it has ever uploaded is 1187.9 MiB. The limit is what keeps the recogniser and the
  translator out of the installer, so it decides a design question on documentation rather than on
  an observation here.
- **Nothing has resolved a bundled weight from a real install.** `BundledModels` is exercised
  against a temporary directory and an environment override; that the packaging script writes into
  `<app>/models` and that the installed application finds it there is untested outside the script's
  own read-back.
- **The icon has been rendered and read back, and never seen on Windows.** The `.ico` parses, its
  nine frames are PNGs whose dimensions agree with their directory entries, and the 256px frame was
  looked at. Whether Windows draws it in the taskbar, the shortcut, Explorer and the Add/Remove
  Programs row — the four places that were blank — is what installing the next candidate shows.
- **The splash has never been shown by Setup.exe**, and `--splashImage`'s behaviour with a 360×220
  PNG is taken from `vpk pack --help` rather than from a run.

### Velopack reaches the network exactly once, and that was checked twice

The user-facing claim is that the launch update check is the only thing this application does on the
network unprompted. Two independent readings support it, and both have limits worth stating.

**From source, at tag `1.2.0`.** `Setup.exe` and `Update.exe` link exactly one HTTP client (`ureq`),
via one wrapper module, with exactly two call sites in the binaries — both inside the
runtime-prerequisite bootstrapper, which is gated on a dependency list that is empty unless
`vpk pack --framework` is passed, and then on a modal dialog. This publish is self-contained and the
packaging script must never pass `--framework`; **that is now an invariant rather than a preference,
because passing it would create a second class of unprompted call and make the documentation false.**
`Update.exe` has no update-check verb at all — its whole surface is `apply`, `start`, `patch`,
`uninstall`, `update-self` — so nothing in the shipped native tooling can reach a release feed on its
own. No telemetry, analytics or crash reporting appears anywhere in those sources.

**From the shipped bytes.** A `strings` sweep of `setup.exe` and `update.exe` as they ship in the
`vpk` 1.2.0 NuGet package found the Microsoft prerequisite hosts (`aka.ms`,
`download.microsoft.com`, `go.microsoft.com`, `dotnetcli.blob.core.windows.net`,
`builds.dotnet.microsoft.com`), **zero** occurrences of `api.velopack.io`, and no telemetry,
analytics or Sentry string. This is what closes the gap between "the source at that tag does not do
it" and "the binary a user runs does not do it".

Three limits. Velopack's transitive Rust dependencies were not audited — only its own code and the
shipped binaries' strings. `vpk` itself **does** phone `api.nuget.org` on every invocation to check
for a newer `vpk`; that is build-time only, never on a user's machine, and the packaging script
passes `--skip-updates`, but it means a CI log will show a NuGet request. And the strings sweep
proves the absence of a hostname, not the absence of a runtime-constructed one; nothing here is a
substitute for watching the process on a network.

## Still open

### The update check has never found an update

`VelopackUpdater` asks `GithubSource` for the release feed of `jkkma/uindosill`, and that repository
has exactly one release — `v1.0.0-rc.3`, published 2026-08-23, marked prerelease, which the
constructor's `prerelease: false` filters out before it is a candidate. Two draft releases were
built and deleted on 2026-08-19 (below), and a draft is not a release for this purpose — `vpk`
could not see one either. So the path from *a newer version exists* to *it is installed* has never run end to
end: no `CheckForUpdatesAsync` has returned a non-null `UpdateInfo`, no `DownloadUpdatesAsync` has
downloaded anything, and `ApplyUpdatesAndRestart` has never been called by this application.

What is tested is the layer above it: `tests/Parakeet.App.Tests/UpdateTests.cs` drives
`UpdatesViewModel` against a fake updater and holds down the behaviour the decision specifies — a
newer version becomes a visible notice, the setting being off makes **no request at all** rather than
one whose answer is discarded, a copy no installer put there checks nothing, a failed check is a line
of text rather than an exception, nothing downloads or applies without the click, and the engine
shutdown happens *before* the restart. All of that is our code. None of it is Velopack's.

`VelopackUpdater` itself — the twenty lines that turn `UpdateManager` into that interface — has no
test at all, because every route to one needs either a network or a fabricated release feed. The
first real release is what will exercise it, and it will exercise it on users.

**The fabricated feed turns out to be cheap, and it answered a hosting question on 2026-08-20.**
`GithubSource`'s fourth constructor parameter is an `IFileDownloader`, so the shipped Velopack 1.2.0
binary can be driven against a canned GitHub releases response with no network at all. That was
done to settle whether hosting the ONNX translation export as a weights-only GitHub release on this
repository would break the update path, and it settles three things about **Velopack 1.2.0**
specifically. A release carrying no `releases.{channel}.json` asset is **skipped**: the source logs
the miss at Trace and walks on to the next release, and with a real feed behind it the check
succeeds normally. With no such asset in the whole list it returns an **empty feed rather than
throwing**, which is the "you are up to date" answer and not a crash. And — the one that constrains
the plan — `GithubSource` requests **`?per_page=10&page=1` and does not paginate**: with ten
feed-less releases above the newest installer release, page 2 is never asked for and the check
comes back empty and silent. Marking such a release `prerelease: true` is cleaner still — it is
filtered out before it is even a candidate, and the Trace miss is not logged — but it does **not**
buy back a page slot: ten prerelease releases on page 1 produce the same silent empty feed, with a
`No releases found` warning. So weights releases on this repository are safe as long as fewer than
ten of them sit above the newest installer release. What this does not establish is any of the
network behaviour, the GitHub API's own ordering, or anything about a Velopack version other than
the pinned one. It does establish that the sentence above — no test is possible without a network —
is no longer true, and a `VelopackUpdater` test against a canned feed is now a thing somebody
declined to write rather than a thing that cannot be written.

### The release workflow was run twice, and one step in it still has not been

**Run for real on 2026-08-23: a `v1.0.0-rc.3` tag took the tag path end to end and published.**
All steps green in 28m2s — against the rehearsals' 7–10 minutes, the difference being the first
CI assembly of the Python bundle — the suite green on `windows-latest`, eight assets on a release
marked prerelease, and the seeding step reported "No releases found" a third time, correctly:
there was still nothing to diff against, so this release ships full packages only and the delta
path below remains unexercised. The observed sizes are in § *The bundled interpreter is 1.20 GB*.
One asset moved against the rehearsal without explanation being established here: the CLI zip is
**60.7 MB** where 2026-08-19 produced 53.9 MB — under its 400 MB guard either way, and the growth
is not attributed further. Everything below about the rehearsals stands as written.

Rehearsed on 2026-08-19 through `workflow_dispatch` with the `draft` input, twice — 1.0.0-rc.1 and
then 1.0.0-rc.2 — on `windows-latest`. Both runs went green through all twelve steps, and both
draft releases were deleted afterwards. What that establishes, on a clean runner rather than on the
desktop where the packaging was written:

- The two channels build and their assets coexist on one release. Seven assets each time, all names
  distinct: `UindosillDesktop-win-Setup.exe` (81.9 MB) and `-win-cuda-Setup.exe` (818.6 MB), both
  full packages, both `releases.<channel>.json`, and the CLI zip.
- The channel separation holds off this machine. The read-back reported `cpu, vulkan` for the
  default package and `cpu, cuda, vulkan` for the other, from inside the built `.nupkg`s.
- The CLI zip does not carry the CUDA drop it inherits from the `win-cuda` vendoring. The prune
  fired and the zip came out at **53.9 MB**; without it the same zip is around a gigabyte.
- A prerelease is marked as one. Both runs set `isPrerelease: true` off the hyphen in the version.
- A draft creates no tag. `git ls-remote --tags origin` was empty before and after.

**The delta path was not exercised, and the rehearsal cannot exercise it.** `vpk` builds a delta by
diffing against packages already in its output directory, so the job downloads the previous release
first. Both runs reported the same thing:

```
[INF] Fetching releases for channel win...
[WRN] No releases found at 'https://github.com/jkkma/uindosill'.
[WRN] No full / applicable release was found to download. Aborting.
```

The second run said it with a draft release sitting in the repository, which is the finding:
**`vpk download github` does not see draft releases.** So the seeding step has now run twice and
found nothing both times, and no `-delta.nupkg` has ever been built in CI — only on the desktop,
where the previous package was on disk beside it.

That is a limit of the rehearsal rather than a defect, and it is deliberately not "fixed" by making
the step succeed against drafts: the step is right, and it degrades the way it should. But it means
the first real release will ship full packages only — correct, and 77 MB where a delta would be
74 KB — and **the delta path first runs on the second real release**, unobserved until then. The
step to watch on that release is *Seed the previous release so deltas can be built*: if it reports
"No releases found" again, deltas are silently not being built and every user is re-downloading the
whole application.

### Packing a Windows release on Linux is documented, and has never been run here

`vpk`'s `[win]` directive cross-builds a Windows package from any host, Velopack's docs say so
explicitly, its own release pipeline builds the tool package on `ubuntu-latest`, and the Setup stub
is shipped prebuilt inside the `vpk` NuGet package rather than compiled at pack time — all read at
tag `1.2.0`. `scripts/package-windows.ps1` passes the directive unconditionally for that reason.

**Every pack in this repository has run on Windows.** `.github/workflows/release.yml` uses
`windows-latest` deliberately (the reasons are in `docs/PHASES.md`), so the Linux route is a
documented capability this project does not use and has not verified. If it is ever used, the thing
to watch is `zstd`: without it on `PATH` vpk does not fail, it warns and falls back to bsdiff deltas
that `Update.exe` cannot apply in the 1.2.0 line. The script warns; nothing enforces it.

### Speaker diarisation — studied 2026-08-16, instrument built 2026-08-17, gate passed 2026-08-18 on meetings only

The pre-v1 study — run 2026-08-16; it lives in the maintainer's diarisation research on the
Drive, outside this repository the way research does per `CLAUDE.md` — surveyed candidates,
licences, gates and routes, with every load-bearing claim re-read at its primary source that day. What it did not and could not
produce is a single number of this project's own: every DER and RTF in that document is an
external benchmark on external material, named with its dataset and scoring convention, and none
of it is podcast audio. Until the dev/held-out podcast set exists and the spike runs on these
machines, this project knows nothing measured about diarisation quality or cost — not the
sherpa-onnx pipeline's DER on overlapping speech, not Sortformer's CPU real-time factor on either
machine, not the int8 export's cost against fp32, not even the labelling effort per stretch. The
study's remaining unknowns are marked inline in that document where each claim stands.

**What changed on 2026-08-17 is the instrument, not the evidence.** `uindosill der` now exists and
is validated: on ten committed RTTM fixture pairs (`tests/fixtures/diarisation/scorer/`) it
reproduces every component pyannote.metrics 4.1 computes — reference speech, missed, false alarm,
confusion — to within a microsecond, in four blocks each: at the headline collar of 0.25 s with
overlap included, at collar 0, over reference-overlap regions under the whole-file mapping, and
over the whole file with overlap skipped; `scripts/validate-der.py`
ran the comparison and the C# suite re-asserts it on every run. That is a validated scorer, and on
that day it was still zero measurements: the five development stretches are pinned and cut
(`tests/fixtures/diarisation/dev/`) but **none is labelled**, so no DER of anything had been
computed on this material, and the labelling effort per stretch was as unmeasured as it was. The
first half of that changed the next day, on other material; the podcast half has not changed at all.

**Two things about the number itself, recorded before any is produced.** First, the collar
convention: pyannote.metrics' `collar` is a total width centred on each reference boundary
(`collar=0.25` forgives 0.125 s either side, confirmed in its source), whereas NIST md-eval's
`-c 0.25` and NeMo's `collar=0.25` are half-widths — NeMo's docstring says so — i.e. this scorer's
`--collar 0.5`. arXiv 2509.26177 states it uses pyannote.metrics at `collar=0.25, skip_overlap=False`,
so its figures and this scorer's headline share a scale; the Sortformer model-card figures the
proposed 10% gate was anchored on do not, and a candidate rescored at `--collar 0.5` is what those
cards should be compared to. Second, what "validated" covers: pyannote.metrics is the reference
implementation and the fixture pairs are synthetic; agreement with it on hand labels of real
podcast audio is exactly what agreement on synthetic turns predicts and has not been separately
shown, and nothing about the *labels* is validated by any of this — inter-labeller agreement on
this material is unmeasured, and with one labeller it will stay so.

Also unmeasured, and cheap to measure once a stretch is labelled: the product's own opt-in with the
canned labeller scores badly by construction (it hears nothing), which is a smoke test of the
harness and not a number about anything; and the second decode the opt-in costs — a whole extra
read of the file — has a real-time-factor cost nobody has timed on either machine.

**The first candidate number exists as of 2026-08-18, it is on meeting audio, and it is not a
verdict.** sherpa-onnx 1.13.5 through its C# NuGet — pyannote segmentation-3.0 (MIT) with 3D-Speaker
CAM++ English embeddings, CPU only — was scored by `uindosill der` at the headline convention
against the pyannote AMI-diarization-setup reference for ES2004a, seventeen and a half minutes of
four-speaker meeting audio with 15.8% of its union speech overlapped. **DER 54.04% with the speaker
count supplied** (miss 9.70%, false alarm 3.11%, confusion 41.23%), and **62.69% with the count left
unknown**, where it resolved 35 speakers. Real-time factor 0.0355–0.0417 on CPU. For scale, pyannote
3.1 publishes 18.8 on AMI Mix-Headset at its own stated convention, which is not this one.

What that establishes is narrow and worth stating exactly: the scorer scores a real candidate on
real audio end to end; the failure is in speaker identity rather than segmentation, because miss and
false alarm hold at roughly 9.7% and 3.1% across every configuration tried while confusion carries
the rest; and the caller is not the cause, since the same models and parameters through
sherpa-onnx's own Python API produce byte-identical RTTM, same SHA-256.

**What it does not establish is that sherpa-onnx is unfit.** One meeting of the sixteen in the test
split, one embedding model of the ten in the zoo, the int8 segmentation model untried, and an
unswept hyperparameter space — no clustering threshold tried produced four clusters, and
`MinDurationOn`/`MinDurationOff` were never moved off the example defaults. It says nothing whatever
about podcast audio. The artifacts are `runs/spike-sherpa/` on the desktop and travel no further:
`runs/` is gitignored and machine-local.

**Two properties of AMI bound what any AMI number can mean**, both recomputed from the references on
this machine rather than quoted. It is effectively a **four-speaker corpus** — 15 of the 16 test
meetings have exactly four speakers, EN2002c has three — so no AMI figure can price the four-speaker
cap that the five-guest stretch was pinned for. And per-meeting overlap runs from 4.3% to 30.0% of
union speech, a factor of seven, so which meeting is scored moves the number more than most
post-processing would.

**A candidate passed the gate on 2026-08-18, and what stays unproven is specific.** Streaming
Sortformer 4spk v2.1, through the community ONNX export of its 30.4 s configuration, scored 16.33%
on AMI test at collar 0 with overlap and 0.06 on the speaker criterion, dev-tuned and held out;
`docs/PHASES.md` carries the result and its reasoning. Three things underwrite it rather than being
asserted: the mel featurizer is bit-exact against NeMo's own `FilterbankFeatures`, the speaker cache
is NVIDIA's `streaming_update_async` imported and called rather than reimplemented, and re-tuned on
the forced-alignment references NVIDIA actually score against it reproduces their published 15.90%
to the decimal. **That last point is itself a convention trap worth carrying:** NVIDIA's AMI figures
are on `nttcslab-sp/diar-forced-alignment` RTTMs and this project's are on pyannote
AMI-diarization-setup `only_words`; the same hypotheses score **13.59 points apart** across the two,
so a figure quoted from one against the other means nothing.

**What that pass does not establish, stated as narrowly as it should be.** The four-speaker cap is
**unpriced and unpriceable within this gate**. AMI test is 15/16 four-speaker and the model reported
four speakers on all sixteen, so the speaker criterion was satisfied by a corpus that cannot vary
rather than by evidence of counting. Below four the evidence is real and good — on 25 cut stretches
holding one or two distinct reference speakers the model never over-counted once — but a cut stretch
is not a recording and no DER is claimed from it. Above four there is nothing, and after the narrowing of
2026-08-18 there is nothing in the gate that could produce anything: VoxConverse was the
beyond-four check and left the gate with web video, having been both in v2.1's training-data list
and arithmetically unreachable — 63% of its test files hold more than four speakers, so a
four-capped model's best possible mean speaker error there is 3.02 against a criterion of 1.0.
**The cap is therefore scoped around rather than tested, and both of the gate's remaining corpora
are meeting sets.** That is a deliberate loss of coverage, recorded here so it is not mistaken for
an absence of risk: a five-person meeting is outside what any measurement in this repository
covers, and the only figures that exist for it are NVIDIA's own. NOTSOFAR-1 is likewise in the
training list, as is AMI — AMI *test* is safe, being the split NVIDIA and pyannote both evaluate on,
but AMI *dev* plausibly was not held out, so the dev figure of 11.91% should not be read as a clean
generalisation estimate. **No measurement anywhere in this repository prices what this model does
with five or more speakers**, and the only figures that exist are NVIDIA's own: 38.90% on DIHARD III
eval 5–9 spk and 34.81% on NOTSOFAR1 eval ≥5 spk, against 14.84% and 15.95% below the cap.

**NOTSOFAR-1 itself has never been scored here, and as of 2026-08-23 that is scheduled rather than
open-ended: it is scored after v1.0.** An obligation, not a waiver — the corpus stays in the gate's
definition and the AMI pass is the whole of what the gate holds until the score exists.
`docs/PHASES.md` records the decision, under the same *After v1* section that holds the NPU study.

**Measured 2026-08-20: on whole podcast episodes this model returns four speakers whatever the
truth is, and the reason is not the cap.** The four episodes at the repository root went through
`uindosill diarise` on the desktop's CPU — 2, 3, 5 and 7 speakers, counts the maintainer confirmed
that day as the episodes' own, no ad-reads and no jingle voices, rather than anything this
repository derived. **No DER is claimed from any of them: there are no per-turn references, and
there will not be until somebody labels one.** What they give is a count.

| episode | true | labels | ≥1% of speech | shares |
|---|---:|---:|---:|---|
| `two-hosts` | 2 | 4 | **3** | 37.6 / 32.6 / 29.6 / 0.1 |
| `two-hosts-one-guest` | 3 | 4 | **4** | 49.5 / 23.9 / 17.0 / 9.6 |
| `two-hosts-three-guests` | 5 | 4 | 4 | 37.3 / 26.3 / 19.4 / 17.0 |
| `two-hosts-five-guests` | 7 | 4 | 4 | 37.6 / 23.2 / 21.9 / 17.3 |

**Four labels on all four, for two different reasons, and only the bottom two are the cap.** Above
the cap a fifth voice is merged, which is what the model says it does. Below it the model
*over*-counts: two hosts produce three substantial clusters and one sliver, three speakers produce
four. So on this material the number four carries almost no information, and a user reading "4
speakers" cannot tell which of the two things happened.

**It is duration, not the cap and not the domain, and a ladder says so.** Same audio, same onset
(2104 s of `two-hosts`), window grown, counting only labels holding ≥1% of speech:

| window | 10 min | 30 min | 40 min | 50 min | 60 min | 120 min | full 175 min |
|---|---:|---:|---:|---:|---:|---:|---:|
| speakers ≥1% (true 2) | **2** | **2** | **2** | **2** | 3 | 3 | 3 |

Correct to fifty minutes and wrong from an hour. `two-hosts-one-guest` from onset 600 s gives the
same shape one rung later — correct at 30 and 60 minutes, wrong at 120. **AMI, the corpus the gate
was passed on, has meetings averaging about half an hour**, which is inside the range where this is
right; the gate could not have caught it. AMI dev re-scored the same day against its references gives
DER **8.62%** at collar 0.25 and **11.91%** at collar 0, confusion **0.94%**, and 4 reference
speakers against 4 found on all eighteen meetings — so this is not a model that mixes speakers up in
general.

**It is a threshold nobody should read as sharp.** Two of three one-hour windows of `two-hosts` came
out correct — onsets 200 s and 6000 s — and only the one at 2104 s failed, so an hour is where the
answer becomes window-dependent rather than where it turns wrong. Past two hours nothing tested has
been correct.

**Two diagnostics that rule out the obvious explanations.** The spurious cluster is **not localised**:
in the failing hour-long window its first turn is at 2138 s and its last at 5650 s, spread across the
whole span rather than appearing after long exposure. And the stretch a failing window contains that
a passing one does not — 5000 to 5800 s — **is correct in isolation**, 54.1% / 45.0% over two
speakers, so no content in it introduces a third voice. What that leaves is over-segmentation of one
host into two labels, which the near-complementary time distributions of the two largest clusters in
the full episode support and nothing here proves.

**What this does not establish.** No DER on any podcast, so none of it says how *good* the labels
are, only how many there are. One show, four episodes, one recording setup. The true counts are the
maintainer's word rather than a labelled reference. No root cause: the streaming buffer, the
four-slot arrival-order speaker cache and the post-processing thresholds are all plausible and none
was tested. The ladder's cuts were made with the manifest's own ffmpeg line but only the 600-second
one is pinned — it reproduced `two-hosts-a.wav`'s byte count exactly, which is a check on the
others rather than a pin for them. And **nothing was re-tuned**: the post-processing is the one
fixed on the 18 AMI dev meetings and applied unchanged, because changing it would invalidate the
gate this model passed.

**The cause is in the model's geometry, and it is 3.5 seconds long.** `SpeakerCacheLength` is 188
encoder frames — **15.0 s in total, 44 frames or 3.52 s per speaker** — and
`ArrivalOrderSpeakerCache` exists because the ONNX graph takes the cache as an *input* and does not
update it: the host scores every frame, boosts twice and keeps the highest 188. So a speaker's whole
identity is 3.5 seconds of *recent* audio with **no long-term anchor**. Over hours, drift or a long
silence leaves the cached exemplar no longer matching, and with two real speakers there are two free
slots for the drifted voice to claim. That is the streaming design working as designed, and this
project's port is fixture-validated against NVIDIA's own function, so it is not a defect here.

**Shortening the window helps and does not fix it.** Tiled across `two-hosts` and counting only
labels holding ≥1% of speech: **8 of 8 five-minute tiles correct, 7 of 9 at twenty minutes, 4 of 6
at thirty, 3 of 5 at fifty, 0 of 3 past two hours.** Every 5-minute probe of the first twenty minutes
is correct where the same audio as one 20-minute window is not, so the extra cluster is created by
window length rather than by anything in the content.

**Windowing and stitching was the obvious fix and it is measured to be a bad one.** `two-hosts` cut
into 8-minute windows with 2 minutes of overlap gives 26 of 30 windows internally correct, and
adjacent windows can be linked by matching labels on the overlap, where the same audio is labelled
twice — no embedding model needed. Scored on AMI dev, which has references, that pipeline returns
**DER 23.53% against the single pass's 8.62%**. Missed speech and false alarm barely move (5.50%
against 5.29%, 2.28% against 2.39%); **confusion goes from 0.94% to 15.76%**. The segmentation is
fine and the speaker identities are destroyed, because a mapping error at one junction propagates
through every window after it. **Windowing is rejected on that number.**

**What ships instead is a repair, not a re-run: `SpeakerTurns.FoldDownTo`.** The failure is always
**over**-segmentation, and over-segmentation is the direction that can be repaired afterwards — two
labels merge, where one label cannot be split back into two people. So the labels are folded down to
a requested count by repeatedly merging the pair that talk over each other least. With no speaker
embeddings available — the graph returns per-frame activity for four slots and nothing identifying a
voice — the timeline is the only evidence there is, and it is good evidence: two labels that are one
drifted person are never simultaneously active, while two people in conversation collide constantly.

**It is a no-op wherever the model was already within the count**, which on all 18 AMI dev meetings
is every one of them — four labels, cap four, nothing merged, DER unchanged at 8.62%. That is what
makes it safe to ship against a passed gate, and it is the property windowing lacks.

**It never fires on its own, and that is measured rather than cautious.** An automatic rule — merge
any pair that never overlaps — would be wrong on AMI: in `IS1008a` the least-colliding pair of
*genuinely different* speakers overlaps by **0.0 s across the whole meeting**. One meeting in
eighteen. The rest of that distribution runs 2.8 s to 57.6 s, so the signal is real and simply not
clean enough to act on unasked. The fold therefore requires an explicit `--speaker-count`, which
until 2026-08-20 was a flag this labeller reported as ignored.

**Each merge is reported with its margin, because the raw seconds mislead.** On `two-hosts` the
merged pair overlapped 131.8 s, which sounds alarming until you see the alternatives at 277.9 s and
334.8 s — it is the clearest pair in the file by 2.1×. What the CLI prints is that ratio. A merge
with no margin is a merge the count forced rather than one the evidence supports, and the sliver
merge in that same run shows exactly that: 2.1 s against a next-closest 2.2 s, 1.0×.

**What the fold does not establish.** It fixes the **count**, and a count is not a DER. `two-hosts`
folds to 62.3% / 37.6% and `two-hosts-one-guest` to 49.5% / 26.6% / 23.9%, and **nothing here can
say whether those attributions are right**, because no podcast in this project has a per-turn
reference. Its failure mode is real and one AMI meeting demonstrates it. It needs the user to know
the speaker count, and for a user who does not, the duration warning below is the whole of what this
product offers.

**The product now says so before it runs.** `SpeakerLabellerCapabilities.ReliableUpTo` is **fifty
minutes** for this model and `SpeakerLabelling.DescribeDurationRisk` turns it into a sentence, fired
by `diarise`, by `transcribe --speakers` and by the window on a file longer than that, before the
labeller decodes a sample. Fifty rather than a rounder sixty because fifty is the longest length at
which every window tested came out right — an hour is where one of four failed, and a bound set
there would have let that window through without a word.

**The command line warns and continues; the window refuses to start, and the asymmetry is the
point.** Past the bound a blank count is not "let the model decide", it is "let the model do the one
thing it is measured to get wrong here", silently, on a recording somebody is about to spend half an
hour transcribing. A window has a person in front of it who can answer, so it asks: set a count, or
turn labelling off and take the transcript without names. A command line is scripted, and a refusal
there breaks a pipeline that has been running for months, so `--speakers` keeps warning and running.
Both ways out of the window's refusal are decisions rather than guesses, and **the field is
deliberately not defaulted to two**: the fold merges whichever pair collides least whether or not
the evidence supports it, so a guessed count does not estimate the answer, it forces one — two
people under one name with no margin behind the merge, which is worse than the over-segmentation it
was reaching for. Inside the bound the estimate is measured to work, and **since 2026-08-22 the window
refuses a blank count there too** (`docs/PHASES.md`, *Settled 2026-08-22*) — not because the estimate
fails inside the bound but because when it fails anywhere it fails silently, and a transcript made
with a count and one made without cannot be told apart afterwards. The command line is where the
estimate still runs, and it is where every figure on this page was taken.

**None of that changes what is known.** The refusal is a user-interface decision about when to ask a
question, not evidence about the labels: a count given past the bound is still unpriced, there is
still no DER on any podcast, and a folded transcript is still a count rather than a measurement.

**Two further limits of the passing configuration.** It buffers 30.4 s, so the diariser trails the
audio by half a minute — adequate for file transcription and not a live-captioning latency, and the
1.04 s graph is a different export this project does not hold. And the model's four-speaker cap is
architectural, so everything above about it applies to the shipped product exactly as it applied to
the spike.

### The C# port — landed 2026-08-19, and what it settles

**It reproduces the Python, and that is measured rather than argued.** Scored on the same 16 AMI
test meetings through `uindosill der`, with the post-processing fixed on dev and untouched: DER
**16.3368%** at collar 0 with overlap against the Python's 16.3324%, **13.5995%** at the headline
collar 0.25 against 13.5963%, **26.7986%** over overlap regions against 26.7926%, and the same
speaker error, 0.0625. Four of the sixteen meetings agree exactly; the worst per-meeting divergence
is 0.0335 points. Both gate criteria hold. The run summary is in the maintainer's Drive per
`CLAUDE.md`; `runs/` is gitignored and machine-local.

**The port is not bit-identical to the reference and cannot be**, and the three reasons are named
rather than left to be discovered. The mel featurizer computes its transform in double where
PyTorch's is single, so log-mel values differ by up to **3.0e-4** overall and **8.0e-5** in bands
carrying real energy, against values spanning −16.6 to +5. Both of those are measurements; the suite
asserts bounds a little above them, 1e-3 and 2e-4, so the figures quoted here are what it is and the
assertions are what would have to move before a regression went unnoticed. The speaker cache's running silence mean is accumulated in double for the same reason. And where
two frames score identically, `torch.topk` leaves the order among equal values undefined, so which
of them takes a cache slot is **not something any port can be held to**; this one breaks ties towards
the earlier frame, which is at least reproducible. The Python spike could claim bit-exactness against
NeMo because both ran the same PyTorch kernels. This cannot, and does not.

**What the committed fixtures do and do not cover.** They hold the featurizer, the chunk loop, the
post-processing and the speaker cache against the reference implementations, at the real geometry,
with no weights — which is what lets CI check them. They do **not** exercise the 474 MB graph, the
512-wide embeddings, or any real audio: the speaker cache's oracle is at embedding dimension 8,
which costs no coverage of the algorithm (it does no arithmetic across that axis but one masked
mean) and is not the same as running it. What covers that gap is the AMI number above and nothing
else, so a change that passes the suite has not been shown to preserve the DER.

**The port's cost, and one figure that is worse.** **65x realtime** on CPU with 12 intra-op threads
on the desktop, against the Python's **74x** on the same machine and the same thread count — about
**12% slower, and nothing here says why.** The mel featurizer is a plain scalar implementation where
NumPy's is vectorised, and the two spend different amounts of time outside ONNX Runtime, but neither
was profiled. **Both figures were re-measured on 2026-08-20 and both held** — 67x and 76.6x over the
same 16 meetings, the same 12.5% gap, with the DER identical to four decimal places; that run is in
§ *The execution provider changes the diariser's answer* below, which is also where the same graph's
GPU figure now sits. Peak working set **1 261 MB**, measured on a 34-minute meeting in a single process;
for scale the spike measured a bare ONNX Runtime session at 1 315 MB in steady state and the export's
README states a 1 251 MB peak, so the footprint is the graph's rather than the host's. **Turning the
ONNX Runtime memory arena off is the documented lever against that number and it has not been
pulled**: the option exists, its default matches what the spike measured so the figures stay
comparable, and what it would cost in throughput is unmeasured.

**Two things about the port are untested by anything.** The **resampler** — every DER above is on
16 kHz AMI audio, where it is bypassed entirely, so nothing measured has been through it. Its
arithmetic is tested (sample counts, passband gain, that a 15 kHz tone in a 48 kHz file is filtered
rather than folded down onto speech) and its effect on a transcript is not. Its **cost** is not
measured either, only counted: the kernel stretches with the decimation ratio, so a 48 kHz file
costs about 193 taps per output sample and three transcendental calls per tap — roughly 9 million
of them per second of audio, against the ~15 ms the diariser itself spends on that second. That
arithmetic says it should not matter and no profile says whether it does. And the **second decode**
the opt-in costs on a real file has still not been timed on either machine, which was already
recorded above and is not changed by the port.

**The install path and the whole opt-in have been run end to end, once, on one machine.**
`uindosill models download sortformer-4spk-v2.1` fetched the 474 MB graph from the pinned revision,
printed the licence notice before downloading it, checked the SHA-256 against the catalogue and moved
it into place; `transcribe --backend cpu --speakers -f txt,rttm` then produced a named transcript and
an RTTM with overlapping turns from a minute of AMI audio, with the four-speaker warning shown. What
that establishes is that the path works, on Windows, on the desktop, once. It is not a claim about
the app's own Models tab (which shares the installer but was not driven), about a resumed or
interrupted download, or about any other machine.

**The podcast half is unchanged and now has no shortcut.** The corpus survey of 2026-08-17 found
free, time-stamped, human-labelled material for meetings and for web video, and none for podcasts.
So the labelling effort per stretch remains unmeasured, no podcast DER of anything exists, and the
only podcast reference this project will ever have is the one it labels itself.

### The C# diariser retired 2026-08-21, and the two DER figures became one

**The divergence the entry above records closed by removing one of its arms.** From 2026-08-19 this
repository carried two DER figures for one model — the Python reference's **16.3324%** and the C#
port's **16.3368%**, 0.0044 points apart — and which one described a given surface depended on the
surface, because the CLI and the window ran the port while every published figure came from the
Python. On 2026-08-21 both moved onto the same bundled-Python sidecar (`src/Parakeet.Engine.Python`
driving `python/uindosill_engines/diariser/`) and the port went to `attic/`. The sidecar measures
**16.3324% at collar 0 with overlap on the CPU** — the Python reference's figure to four decimals —
and it is now the only one the product can produce. `docs/PHASES.md` § *Decided 2026-08-21* carries
the reasoning; `attic/README.md` carries what the port was.

**The entry above stays as written, and it is not merely history.** Three of its findings outlive
the code they were measured on: the mel featurizer's double-precision transform, the speaker cache's
double-accumulated silence mean, and `torch.topk` leaving the order among equal scores undefined are
properties of the model rather than of C#, so they bound whatever reads this graph next. And the
0.0044 points they cost is the only figure this project has for how faithfully a reimplementation of
this model can be done — which is why the directory was kept unbuilt rather than deleted. **What
closed is the divergence, not the reasons for it.**

**Two of that entry's cost figures now describe the attic.** **65x** realtime with 12 intra-op
threads, and a peak working set of **1 261 MB** measured on a 34-minute meeting in a single process,
are the C# port's. The shipping path is a host process and a child, and **nobody has measured what
the pair holds** — which makes the standing gap recorded under the translation entry below (*Peak
memory with the ASR model, the diariser and a translator resident is still unmeasured on both
machines*) larger rather than smaller, there now being a second process to account for. The ONNX
Runtime memory arena lever the entry above records as never pulled is still never pulled, and is now
a Python-side option rather than a C# one.

### The chunk loop lost one or two frames on 7.3 % of durations — found 2026-08-22; AMI test re-scored on the fixed loop the same day: 16.3324 %, unchanged to four decimals, and the arithmetic says why

**What was wrong.** The sidecar's loop trimmed the graph's 381-frame embedding output to the
pre-encode length of the chunk's *valid* frames — the `elen` the graph reports — where NeMo's
`streaming_update_async` takes a chunk's capacity (`max_chunk_len`) from the tensor's physical
width and clamps the valid length to it. The two differ on every file: the featurizer pads the mel
to a multiple of 16 and the STFT is one frame longer than the valid count, so the last piece is
wider than its valid part, and on the first chunk of a short file too. Verified on the installed
graph on the CPU: `elen` for chunk lengths 2720, 2736, 2888, 2904, 3040 and 3048 is 340, 342, 361,
363, 380 and 381 — exactly ⌊(n − 1)/2⌋ + 1 applied three times — and `run_mel` on a 2736-frame
piece with 2720 valid returned **338 rows where 340 are due**; a 600.0 s file returned **7,498
rows where 7,500 are due**, its last chunk's rows concatenated 160 ms early. Over every duration
from 1 s to 2 h at 0.1 s steps, 7.3 % lose one or two frames; the committed chunk plan's 600 s
case is one of them.

**What was done.** The loop trims to the pre-encode length of the piece's physical width
(`pre_encode_len`, the same arithmetic, checked against `elen` on the graph) and keeps the valid
length as `chunk_lengths`; the progress step is counted before the context-only break, so the bar
reaches n of n. Re-run on the graph: 340 of 340 and 7,500 of 7,500. The committed parity fixture's
geometry has no padding to trim — 6,096 valid of 6,096 — so its reference is unchanged, and the
check passes with a maximum difference of 0.0 before and after; it did not and could not see this.

**What was owed, and is now measured — re-scored on the desktop 2026-08-22.** `engine.py` had not
changed since the spike, so until that evening the 16.3324 % AMI figure and every row of the
provider table above described the pre-fix loop. The same evening all five arms were re-run on the
fixed loop through the product path — `uindosill diarise --threads 12 --backend <provider> --id
<meeting>` per meeting, one process each, scored by `scripts/measure-der.ps1` against the pyannote
`only_words` test references over the 16 meetings — on the same machine, the same graph and the same
ONNX Runtime builds as the table: 1.27.0 for cpu and webgpu, 1.29.0 for cuda, 1.24.4 for the two
DirectML-build arms, dml at `ORT_DISABLE_ALL` behind `--backend-unverified`. Collar 0 with overlap,
pooled:

| provider | ONNX Runtime | published | re-scored 2026-08-22 | collar 0.25 / overlap regions / speaker error |
|---|---|---:|---:|---|
| cpu | 1.27.0 | 16.3324 % | **16.3324 %** | 13.5964 % / 26.7926 % / 0.0625 |
| webgpu | 1.27.0 | 16.3319 % | **16.3319 %** | 13.5950 % / 26.7945 % / 0.0625 |
| cuda | 1.29.0 | 16.1021 % | **16.1021 %** | 13.3701 % / 26.9722 % / 0.0625 |
| cpu | 1.24.4 | 16.3347 % | **16.3347 %** | 13.5983 % / 26.7962 % / 0.0625 |
| directml, `ORT_DISABLE_ALL` | 1.24.4 | 16.3319 % | **16.3319 %** | 13.5950 % / 26.7945 % / 0.0625 |

**Unchanged to four decimals on every arm, and per meeting too.** On the three arms whose
2026-08-21 hypotheses are still on disk — cpu 1.27.0, webgpu, cuda — every per-meeting collar-0
figure agrees to four decimals and all 48 RTTMs are byte-identical to that day's; the DirectML arm's
16 RTTMs are byte-identical to WebGPU's, and the two CPU builds agree on 13 of 16 files, which is the
0.0023 points an ONNX Runtime version is worth. **And that is what the arithmetic predicts rather
than luck.** Which durations lose rows is decided by the chunk plan alone: the loss sits in the piece
whose right context reaches into the multiple-of-16 padding, and every row after it lands early. Re-running that plan on the 16 test meetings' exact sample counts —
it reproduces the 338 / 340, the 7,498 / 7,500 and the 7.3 % above (5,280 of 71,991 durations from
1 s to 2 h at 0.1 s) — shows **none of the sixteen is one of the 7.3 %**: the pre-fix loop delivered
every row due on all sixteen, and the fixed loop adds one trailing padding row on nine of them that
the final truncation removes. So the 16.33 % describes the fixed loop because the fixed loop produces
the same output on these files, **and the re-score therefore says nothing about what the fix does
where it bites** — that remains the per-file arithmetic above: one or two rows, 80–160 ms, on 7.3 %
of durations. One bound on that is recorded rather than left to be found: of the 18 AMI *dev*
meetings one, IS1008b, is a losing duration (one row), so the dev-tuned post-processing grid was
chosen on probabilities one frame short on one meeting; it was not re-tuned, and that it would not
move is an expectation. The per-file realtime the sidecar itself reports, for the record: 68–73x on
cpu 1.27.0, 72x on cpu 1.24.4, 608–639x on webgpu, 867–982x on cuda, 507–545x on DirectML unfused.
The runs are `runs/der/20260822-22*-sortformer-sidecar-*` with their hypotheses in
`runs/der/hyp-*`, on the desktop and in the Drive's `runs-desktop` folder.

**Beside it, the featurizer's peak working set — measured for the first time the same day.** The
architecture note said "about 51 kB per second of audio" for the mel and that nothing had profiled
the peak. Whole-file, on the bundled torch, thirty minutes of real 16 kHz audio peaked **1,317 MB
above the resting working set — about 730 kB per second**; the complex spectrum and the
intermediates behind `pow`, `sum`, `sqrt`, `pow` and the filterbank matmul are all alive together.
The STFT now runs in hop-aligned blocks and the mel is written into its final layout: **bit-identical
to the whole-file result on those thirty minutes** (`numpy.array_equal`, 180,016 × 128), and
**551 MB above resting at peak — about 306 kB per second**, the remainder being the samples
themselves and the 88 MB mel. Bit-identical means the 16.33 % is untouched by this part.

### The handoff WAV was 16-bit PCM, and on decoded input that moved the answer — measured 2026-08-22

**The one place the sidecar path differed from the reference path was the file between them.** The
host decodes, resamples to 16 kHz in float and writes a temporary WAV the sidecar reads with
soundfile; until 2026-08-22 that WAV was 16-bit PCM (`WavWriter.WriteFile` → `WritePcm16`, read back
as `int16/32768`, written as `*32767` rounded), while the reference reads its audio straight into
float. Two regimes, same recording path, same implementation — the Python reference on the CPU,
ONNX Runtime 1.27.0, scored with `uindosill der` against the float arm as reference:

| input | samples the handoff changed | DER @0.25 | DER @0 | overlap DER | frame-speaker cells flipped at 0.5 |
|---|---|---|---|---|---|
| 48 kHz MP3, decoded and resampled (two-hosts-new-episode, 157 min) | **99.9%**, max 4.27e-05, mean 7.99e-06 | **2.50%** | 2.85% | 6.66% | **5,189 of 472,432 (1.10%)** |
| 16 kHz 16-bit PCM (two-hosts-b stretch, 10 min) | **0.25%**, one LSB, only \|x\| ≥ 16369 | **0.00%** | 0.00% | 0.00% | 0 of 29,992 |

On the episode the components were miss 0.28%, false alarm 0.39%, **confusion 1.83%** — about 159 s
of speech carrying a different label — with the speaker count and mapping unchanged (998 → 1005
turns) and a maximum per-cell probability difference of 0.95. **The second row is why nobody saw
the first.** AMI is 16 kHz 16-bit PCM, so every published figure was taken in the regime where the
round trip nudges a quarter of one percent of samples on loud peaks and moves nothing — the
sidecar's 16.3324% matched the reference to four decimals *through* this handoff. The product's
everyday input is the first regime.

**What it is on the project's own scale.** WebGPU against the CPU moved AMI DER by 0.0005 points
and was admitted to `auto`; CUDA moved it 0.23 points and was not. The PCM16 handoff on decoded input
moves the output 2.50 points from the reference's — ten times the gap that keeps CUDA out — on the
path `diarise` and the window both take. **What it is not:** an accuracy figure. Both arms are
hypotheses; no podcast reference exists; the quantised answer is not known to be worse, only
different. The standard this repository holds backends to is reproducing the reference's answer, and
by that standard it was a defect.

**Since 2026-08-22 the handoff is 32-bit float** (`WavWriter.WriteFloat32File`), so the sidecar
reads the bytes the host produced — soundfile returns IEEE float WAV unchanged, and reads 16-bit PCM
as `int16/32768` exactly as `WavAudioSource` does, both checked. A test holds the handoff to the
host's samples bit for bit. The file is twice the size (302 → 605 MB for the episode; three hours is
the ~690 MB the method already accepts in memory), and **the time to write it is unmeasured**.

**What this does not establish.** One episode and one stretch, one machine, the CPU provider. The
2.50% is a distance, not an error. And nothing here says what the decoded-input regime does to
*accuracy* on either side of the fix — that needs a podcast reference this project does not have.

**What the move did not touch.** The **resampler** is still on the C# side of the seam — the sidecar
is handed a 16 kHz mono WAV by path — so every DER above is still on 16 kHz AMI audio that bypasses
it, and its effect on a transcript is still untested. The **second decode** the opt-in costs on a
real file has still not been timed on either machine. The four-speaker cap and the fifty-minute
duration bound are unchanged, because neither was in the port. The install path for the 474 MB graph
is unchanged too — `models download` and the catalogue are C# and untouched — but **the end-to-end
run recorded above, which produced a named transcript and an RTTM from a minute of AMI audio, went
through the port**, and no equivalent run through the sidecar is recorded here.

### The execution provider changes the diariser's answer — measured 2026-08-20

**A CUDA build of ONNX Runtime runs this graph 21.8x faster and does not produce the same
diarisation.** Both halves of that are measured, on the desktop, against the 16 AMI test meetings,
with one thing changed: the same Python driver, the same `onnxruntime-gpu` 1.29.0 install, the same
mel featurizer and NVIDIA's own speaker cache on CPU torch in both arms, and only the
`InferenceSession` provider list swapped. Nothing in the product moved —
`Microsoft.ML.OnnxRuntime` 1.29.0 is still pinned and `Directory.Packages.props` is untouched.

**The speed.** 9.062 h of audio: **76.6x realtime on the CPU EP and 1230.9x on the CUDA EP** for the
whole pass, **78.1x against 1705.7x** for the ONNX graph alone. The two ratios differ because the
featurizer and the cache stay on the CPU, so once the graph is 22x faster the featurizer is what is
left. The shipping C# path, `uindosill diarise --threads 12`, measures **67x** over the same audio —
9.06 h in 8.2 min — which is the number every claim below is relative to, and which confirms the
**65x** already recorded above rather than replacing it. **Both of those now describe `attic/`**: the
C# path was retired on 2026-08-21 and what ships measures **70.2x** on the CPU and **593.7x** on
WebGPU through the sidecar (§ *That provider comparison is superseded*). The 76.6x above is the
closest thing to a like-for-like predecessor, being the Python driver rather than the port. Per file it runs 57–58x for the first two
meetings and 69x thereafter, so a one-file figure is a cold-cache figure.

**The GPU demonstrably ran, which is checked rather than assumed** — a silent per-operator CPU
fallback would look exactly like a GPU that is not much faster. Counted out of ONNX Runtime's own
profile JSON, per node: **5,601 nodes on `CUDAExecutionProvider` and 3 on `CPUExecutionProvider`**,
the three being shape operators ORT always places there. **sm_120 cost almost nothing and is not
free of PTX**: the provider DLL's newest embedded PTX target is `sm_90` — `.target sm_70/75/80/86/89/90`
and no `sm_120` — and one 5,406-byte entry appeared in the driver's compute cache at the minute the
first CUDA session was built, so at least one module was JIT-compiled forward. What it cost is the
part that matters: **session build 0.72 s against the CPU's 0.63 s**, first inference 0.088 s against
1.431 s. Whether the rest came from `sm_120` cubins is **not established** — there is no CUDA
toolkit here to dump the fatbins. **VRAM is about 1,385 MiB**, from adapter memory sampled at 100 ms
across a full run (1,355 MiB idle to 2,740 MiB peak); that is a whole-adapter delta with other
applications resident, because `nvidia-smi` returns `[N/A]` for per-process memory under WDDM.

**The answer moves, and by far more than any port difference.** Post-processing is the dev-chosen
configuration applied to both arms unchanged. **Zero of sixteen meetings produced identical
probabilities**; the largest probability difference is **0.964**, and 0.57% of binarised
frame-speaker cells differ. Pooled: **DER at collar 0 is 16.3324% on the CPU EP and 15.7062% on
CUDA**, 13.5964% against 12.9451% at collar 0.25, with the same speaker error 0.0625. **That
apparent improvement is one meeting**: TS3003c goes 24.8040% to 13.6666%, an **11.14-point** swing,
and over the other fifteen the ordering reverses — 15.7756% on the CPU against 15.8403% on CUDA, so
CUDA is 0.0647 points *worse* everywhere else. **"CUDA is more accurate" is not a claim any of this
supports.**

**Why it can move that far was already written down here.** The arrival-order speaker cache is
stateful, and where two frames score identically `torch.topk` leaves the order among equals
undefined — recorded above as something no port can be held to. A different execution provider is a
different set of floating-point reductions, a different reduction flips a tie, and a flipped tie
hands a cache slot to the other speaker for the rest of the recording. For scale, the C#-against-
Python port difference on the same meetings is **0.0044 points**; changing the provider is **142
times** that.

**It is not run-to-run noise.** The CUDA arm was run twice over all 16 meetings and the two are
byte-identical in all 16, `maxAbsDiff` exactly 0.0, same DER to four decimals. CUDA computes a
different but perfectly reproducible answer, so a GPU DER can be measured once and trusted — it
just cannot be inherited from the CPU one.

**What is still unmeasured about it.** The CUDA DER above uses **post-processing tuned on CPU
probabilities**, which is right for a controlled comparison and wrong for a shipping configuration:
an honest CUDA figure needs the 18-meeting dev grid re-run on CUDA probabilities and the test set
scored once after. **DirectML was never tried, because CUDA cooperated — and DirectML is the one
that would ship**, the product being .NET and CUDA not being the Windows GPU path for it. So every
figure in this entry is about a provider the product would not use, and none of it transfers:
`Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 was never installed, no C# code ran on any GPU, and the
study that would settle it is queued in `docs/PHASES.md` § *After v1*. The longest file here is 49.5 minutes, so accumulation over hours on a GPU is untouched.

**And re-running the AMI test is not expensive, which corrects an assumption rather than a
measurement.** It costs **8.2 minutes** through the product on CPU, 7.1 through the Python driver,
and **26 seconds** on CUDA. Whatever argues against adopting a GPU provider, the price of
re-measuring the gate is not it.

### That provider comparison is superseded — the same question, re-measured on the shipping path 2026-08-21

**One sentence in the entry above is now false, and the one beside it is true in a way that
misleads.** *"DirectML was never tried, because CUDA cooperated — and DirectML is the one that would
ship"* is wrong in both halves: DirectML has been tried, and it is **refused by name in both
engines**. *"`Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 was never installed, no C# code ran on any
GPU"* is still literally true — the package was never adopted, and no C# runs on a GPU now either,
because both graphs left C# altogether — but what it was written to say, that no shipping
configuration of this product touches a GPU, stopped being true on 2026-08-21. **The provider that
ships is WebGPU**, and the C# host drives all four of them through the sidecar over one protocol.
The study that entry queued in `docs/PHASES.md` § *After v1* has been run — it is
§ *Decided 2026-08-21*. The original is left as written because the reasoning that produced the
wrong expectation is worth reading beside the measurement that refuted it.

Measured on the desktop, 16 AMI test meetings, 9.062 h, collar 0 with overlap, the dev-chosen
post-processing applied unchanged to every arm:

**The five arms are not all from one ONNX Runtime install, and the table says which is which
because the deltas are not comparable across the two.** `onnxruntime-webgpu` is published at 1.27.0
and `onnxruntime-directml` at 1.24.4, so the DirectML arms were run against a different build — with
its own CPU arm, which is not the same number:

| provider | ONNX Runtime | DER | vs its own cpu arm | realtime |
|---|---|---:|---:|---:|
| cpu | 1.27.0 | 16.3324% | — | 70.2x |
| **webgpu** | 1.27.0 | **16.3319%** | **−0.0005** | **593.7x** |
| cuda | 1.29.0 | 16.1021% | −0.2303 | 971.7x |
| cpu | 1.24.4 | 16.3347% | — | 71.5x |
| directml, ONNX Runtime's defaults | 1.24.4 | **53.1522%** | **+36.8175** | 945.6x |
| directml, `ORT_DISABLE_ALL` | 1.24.4 | 16.3319% | −0.0028 | 619.0x |

**Every row was re-scored on 2026-08-22 on the fixed chunk loop and is unchanged to four decimals** —
§ *The chunk loop lost one or two frames on 7.3 % of durations* below has the run, and the arithmetic
that says why none of the sixteen meetings could have moved.

**Two CPU numbers, both correct, and mixing them is the trap.** 16.3324% is the CPU on 1.27.0 and
16.3347% on 1.24.4 — 0.0023 points apart, which is what an ONNX Runtime version is worth on this
graph. Reading DirectML-unfused's −0.0028 as WebGPU's −0.0005 (or the reverse) is comparing arms
that never ran on the same runtime, and it was written that way in this project's own source
comments until 2026-08-21.

**The 1.27.0 CPU row is the same 16.3324% the entry above measured on 2026-08-20, and the same
figure the gate was passed on** — so the CPU answer has now reproduced across three runs, two ONNX Runtime
installations and two process architectures. It is the fixed point everything else here is measured
against, and it is the reason a moved figure can be attributed to a provider at all.

**The two CUDA figures are two different arms, and this document is not to be read as holding one
number for them.** **15.7062%** above is the arm of 2026-08-20 — `onnxruntime-gpu` 1.29.0 through
that day's Python driver. **16.1021%** is the arm of 2026-08-21, through the sidecar. Both are
collar 0 with overlap over the same sixteen meetings with the same post-processing, and they are
**0.3959 points apart** — ninety times the C#-against-Python port difference the entry above uses
for scale. What moved between them was not isolated; what did *not* move is the CPU arm, which rules
out the corpus, the scorer and the post-processing and leaves the CUDA stack. **So a CUDA DER from
this project does not survive a change of driver and library**, and neither figure may be quoted
without naming the run it came from. That non-portability is an argument against a CUDA *figure*
rather than against CUDA, and it is a property of any provider whose answer differs from the CPU's —
which is the property the choice below turns on.

**WebGPU was chosen for agreement rather than for speed.** CUDA is 1.6x faster than it and moves the
number the gate is written in; a provider that reproduces the CPU's answer lets **one** published
figure describe every machine, and one that does not means the figure describes whoever measured it.
The 1.6x of speed is the price, paid deliberately.

**The parity fixture is what makes that checkable, and its threshold is measured rather than
chosen.** It compares head probabilities over synthetic mel generated from a seed — no audio, no
licence, 12 KB — at a tolerance of **1e-4**:

| | maximum absolute difference | |
|---|---:|---|
| cpu, ONNX Runtime 1.27.0 against 1.29.0 | **0.000e+00** | bit-identical |
| webgpu | 1.073e-06 | pass |
| cuda | 8.143e-04 | **fail** |

Two orders of magnitude separate a faithful provider from a diverging one, which is what makes 1e-4
a threshold rather than a guess — and the CPU row says the fixture does not drift across ONNX
Runtime versions, so a failure is the provider and not the pin. **CUDA fails it**, consistently with
the 0.2303 points it moves the DER by. It stays reachable, and the window reports the diariser's
backend and a failed parity check on the finished job rather than deciding for the user.

**The DirectML defect is located, and it is the reason a provider is now checked before it is
believed.** At optimisation level `BASIC` or above DirectML fuses the whole graph into **one node**
whose head output differs from the CPU by up to **0.796 on a probability**, with **2.997% of frame
decisions flipping** on the first chunk with an empty cache. Metacommands off, dynamic fusion off
and seven named ORT passes disabled individually all reproduced it exactly; only `ORT_DISABLE_ALL`
moves it, and at that level the same provider lands on 16.3319% — the WebGPU figure. So the
wrongness is in the optimiser rather than in the hardware. **53% DER arrived with plausible RTTMs, a
clean exit and a 13x speed-up**: nothing about the run looked like a failure, which is the finding
that shaped the rest of this.

**What is still unmeasured about the provider that ships.** The post-processing WebGPU runs is the
grid tuned on **CPU** probabilities, which is the objection the entry above raises against its own
CUDA figure and which applies here unchanged — the 18-meeting dev grid has not been re-run on
WebGPU probabilities and the test set has not been scored once after. What stands in its place is
that WebGPU reproduces the CPU to 1.073e-06 and −0.0005 points, which is an argument that a re-tune
would change nothing rather than a measurement that it does not. **The longest file in this
comparison is 49.5 minutes**, so accumulation over hours on a GPU is as untouched as it was. And
every row above is one card, one driver, one run — see *No AMD GPU has run any of this* below.

### The crosstalk tie-break changed 2026-08-20, and nothing scores whether it is right

**Who gets the words while two people are talking at once was decided by an accident of the
tie-break, and now it is decided on purpose — but "on purpose" is an argument, not a
measurement.** It was found by ear rather than by any check here: on a 2 h 37 m two-host podcast a
listener heard the second host take over in the middle of a subtitle cue that carried the first
host's name to its end. The diariser had not missed it. Its turns put the incoming speaker's start
at 1750.35 s, inside a cue running 1748.13–1753.65; the attribution is what lost it.

**Why it was lost, and it is structural rather than a slip.** `SpeakerAssignment.Dominant` gives a
word to the turn that overlaps it most, which decides nothing inside crosstalk: while two turns
both contain a word, the overlap is that word's own length for each, so every word in the stretch
ties and the tie-break alone picks the name. It picked the turn that **started earlier**, which is
right for a back-channel — someone's "yeah" inside another's turn, where the words belong to the
speaker being interrupted — and wrong for a handoff, where it holds the outgoing name across the
whole overlap and moves the change to the crosstalk's end. The tie-break is now the turn that
**ends later**, which separates the two shapes rather than trading one bias for its mirror: the
container of a back-channel also outlasts it, so that case is unchanged, and at a handoff the
incoming speaker takes the overlapped words.

**What is measured is the size of the change, on one file.** Replaying both rules over that
episode's 26,105 attributed words: **465 words change label (1.78%), touching 138 of 1,874
segments (7.4%)** and 83.4 s of word audio, 304 one way and 161 the other. The raw diarisation
under it holds **7.0 minutes of double-active speech across 505 stretches — 4.8% of the 146.2
minutes of speech — median 0.53 s, longest 7.0 s**, so most handoffs move the name by about half a
second and a few by seconds. On the instance that prompted it, the change now lands on the word the
listener said it should.

**What is not measured is whether the new name is the correct one, and this repository cannot
settle it.** Inside a handoff overlap both people really are speaking, so neither rule is right for
every word in it — one speaker per word cannot represent two — and the new rule mislabels the
outgoing speaker's tail words exactly as the old one mislabelled the incoming speaker's opening
ones. **No speaker-attributed reference transcript exists here**, and the diarisation error rate
cannot stand in for one: `uindosill der` scores turns, not attribution, so the passing AMI figure
of 16.33% is untouched by this change and equally unable to detect it. The whole of the evidence is
one episode, one language, two speakers, and one listener on one instance.

**What would settle it** is a reference transcript whose words carry speaker labels — AMI's
annotations do — scored for attribution accuracy rather than for turns, with both tie-breaks run
over it; or, failing that, a listening test over the 465 words that moved. Neither has been done.

### NPU offload — assessed 2026-08-16, nothing measured

The second machine's XDNA 2 NPU is idle under this product, and that much is settled rather than
unproven: ggml has no backend for it (its `ggml/src` listing read at source 2026-08-16), so
parakeet.cpp cannot use it and neither can the llama.cpp server v2 contemplates. Everything past
that is unmeasured. The one figure in the record — AMD's own Parakeet-TDT 0.6b v3 demo at RTF
0.023–0.030 on 16.5 minutes, encoder on the NPU at BF16, decoder on the iGPU, static 15-second
chunks — is AMD's number on unnamed hardware with a different chunking, and it has not been run
on this laptop, where the Vulkan tier measures 0.035; "about 1.5× at best" is arithmetic across two
machines, not a measurement. Unknown until a study runs: that demo's RTF here; what BF16 on the NPU
with per-operator CPU fallback costs against f16 on the WER corpus; the power draw either way,
which is the quantity an NPU exists for; and whether Windows ML's Vitis AI EP accepts this
machine's driver, 32.0.20102.3930, whose numbering does not match the window the Microsoft EP page
listed that day (32.00.0203.280 to .297). The research item, and when it becomes relevant, is in
`docs/PHASES.md` § *After v1*.

### Translating into English — spiked 2026-08-19, exported and scored 2026-08-20, decoded in C# 2026-08-20

**This product translates.** The claim this entry carried from the day the route was chosen —
*nothing this product ships has translated a word* — closed on 2026-08-20: a SentencePiece tokenizer
and a beam search written for this project drive the exported ONNX graphs from
`Parakeet.Engine.Marian`, `--translate` reads real weights, and `uindosill translate` runs the same
pass over a text file with no audio at all. What that does **not** mean is that the feature is
proven; it means the unproven parts moved. They are below, and the two that matter most are that
**the gate is still not passed** — its human criterion is unperformed — and that **the English this
loop produces is the English the gate was scored on only to the extent that a measurement says so**,
which is a number rather than an assumption.

**Superseded in part on 2026-08-21, and the pointer is here rather than at the end because this
entry is long.** The C# decode loop described below was retired that day and both engines moved into
a bundled Python. Everything measured here stands as measured; which of it still describes the
product is § *The C# translator retired 2026-08-21, and what the entry above now describes*, below.
Read that before quoting anything here about the beam search, the tokenizer or DirectML.

What has *not* happened: **no real-time factor for a translation pass has been measured** end to
end, only per-sentence times; no translated transcript has been produced from real audio and
compared with anything; and the 23 of 25 languages that have never had audio through this pipeline
still have not. The study behind `docs/PHASES.md` § *Decided 2026-08-19* is separate again:
every model claim in it was read off a card, a config, a vocabulary file or a repository listing
fetched that day, and where the spike has since contradicted it, the number below is the one that
holds.

**The beam search is a port of one implementation, and the difference is not pedantry.** The two
ONNX graphs are pinned by digest; the search over them is not, and it is a real degree of freedom.
Whether a finished hypothesis is scored by its total or its mean log probability, whether the loop
stops when the beams are full or when they can no longer improve, how equal-scoring candidates are
ordered, whether a beam that has just emitted the end token can still be continued, how
`bad_words_ids` and `forced_eos_token_id` are applied — each changes the English while leaving it
looking entirely correct, and the diariser has already shown what that costs here: one numerical
tie-break moved a meeting by 11 DER points. So what is in `Parakeet.Engine.Marian` reproduces
transformers 4.57.6's `GenerationMixin._beam_search` specifically — the vectorised rewrite rather
than the older `BeamSearchScorer`, which is a different algorithm with the same name — read out of
the installed source on 2026-08-20 rather than recalled. Its shape is not the textbook one: it keeps
`2 x beams` candidates per step so that a step in which every top beam ends the sentence still
leaves live continuations, finished hypotheses occupy a second set of `beams` slots that a new one
must outscore to enter, and only a candidate from the step's top `beams` may enter that set at all.
One quirk was reproduced rather than corrected: an unfilled finished slot holds −1e9, so the
early-stop heuristic cannot fire until `beams` complete hypotheses exist. Fixing that would be a
different search.

**8,148 of the 8,149 reproduce the recorded hypothesis character for character — 99.99%, with 23 of
the 24 languages at exactly 100%.** Run on the desktop's CPU on 2026-08-20 against `fp32-merged`,
beam-6, one sentence at a time, at a mean 0.532 s per sentence against the gate run's own 0.618.

**The single disagreement is worth more than the rate is.** It is Hungarian sentence 1818, and it is
one of the 31 hypotheses the gate run had already flagged as degenerate — its recorded row carries
`degenerate: ". "`, a trailing punctuation run rather than a collapse. Both implementations produce
the same English:

> People probably don't think that homebound travelers need patience and understanding.

and then neither stops. They are **character-identical for 427 characters** — the whole sentence
plus 171 trailing ` .` — and differ only in when the runaway ends: **171 dots from the port against
248 recorded.** So the two searches agree everywhere the model is translating, and diverge only
after it has finished translating and will not stop emitting. That is exactly where divergence
should be expected: hundreds of steps of near-identical log probabilities, where a difference far
below any decision the search makes accumulates until one comparison flips.

**What it costs the published figures, computed rather than asserted: +0.04 chrF++, in the port's
favour.** Rescoring Hungarian's 348 sentences at the gate's own signature gives **56.7485** for the
recorded hypotheses — which is the 56.75 the gate published, so the scoring is the gate's — and
**56.7883** for the port's. Against a required margin of 29.55 that moves nothing: Hungarian's
verdict is unchanged, and the other 23 languages' figures are unchanged *by construction*, because
their hypotheses are the same strings.

**So the chrF++ table above describes what the product ships.** That is the sentence this
measurement exists to be able to write, and it is now a measured claim rather than an assumption —
with the one exception stated in full rather than rounded away.

**The first English this product produced, and the one line of it that is wrong.** On 2026-08-20 the
six sentences of `tests/fixtures/translation/marian-tokenizer.json` — four of them real ASR output
from this project's own pipeline, Spanish and German — went through `uindosill translate` against
`fp32-merged` on the desktop's CPU at 0.522 s per line. Five came back right. The German one did
not:

| | |
|---|---|
| in | `Ralf Dahrendorf wurde neunzehnhundertneunundzwanzig in Hamburg geboren.` |
| out | *Ralf Dahrendorf was born in Hamburg in the nineteenth century.* |

**1929, spelled as a word, became a century.** That is the failure the fixture was built to contain a
case of — its README says the German number sentence is "where the ASR and the translator interact
worst" — and it is now measured rather than predicted. It is also a *cascade* failure rather than a
translation one: the ASR wrote the year as `neunzehnhundertneunundzwanzig` because that is how the
speaker said it, and the translator then had to read a nineteen-character compound number that
almost certainly never appeared in a Bible corpus. Nothing here prices how often it happens — one
sentence is one sentence — and no gate criterion looks for it: chrF++ against an English reference
scores a wrong date as a few bad character n-grams, and the corpus the gate ran on is written text
where numbers are digits. **The two things that would price it were a cascade measurement and the
human adequacy check. The cascade one has since been done — it is the next entry — and the adequacy check
has not.**

English passed through byte-identical, which reproduces the spike's finding through the product
rather than beside it.

**The cascade penalty is measured as of 2026-08-20, and it is small.** What ASR error costs the
translation, in the same units as the gate and on the same sentences.
`scripts/measure-cascade.py`, on the desktop's CPU, `fp32-merged`, beam-6, batch 1. FLEURS is
n-way parallel, so the same sentence ids exist as Spanish audio, as Spanish text and as English
reference text, and both arms run in one process over one id set:

| | sentences | text-in chrF++ | cascade chrF++ | **penalty** | ASR WER |
|---|---:|---:|---:|---:|---:|
| es (`es_419`) | 348 | 56.17 | 53.22 | **−2.95** | 6.12% |
| de (`de_de`) | 347 | 63.64 | 59.30 | **−4.34** | 9.93% |

chrF++ at `nrefs:1|case:mixed|eff:yes|nc:6|nw:2|space:no|version:2.6.0`.

**The text-in arm reproduced the gate's published figures exactly — 56.17 and 63.64 — and that is
what makes the subtraction mean anything.** It is recomputed here rather than quoted, over the same
ids in the same process on the same machine, so the difference between the two columns is the
recogniser and not whatever else differed between two runs. That it lands on the same numbers the
gate published is a check that this harness is measuring the gate's object.

**The penalty decomposes, and it decomposes the reassuring way.** German has 1.62× Spanish's word
error rate and 1.47× its penalty, so the loss scales roughly with how wrong the input is: the
translator is not disproportionately brittle to slightly-off text, which was the alternative the
measurement was built to distinguish. **Neither language's verdict moves.** Spanish clears its
required margin by +34.77 text-in and +31.82 after the cascade against a bar of +23.60; German by
+42.86 and +38.52 against +24.22.

**The word error rates are the first this project has measured outside English.** Every WER here
until today was Earnings-22, which is English; **2 of the 24 source languages now have one**, at
6.12% and 9.93%, through `uindosill wer` — whose normaliser is this project's own and
English-oriented, so on Spanish and German its number rule is inert and what it does is lower-case,
strip punctuation and drop fillers. Comparable to another figure from here and to nothing published.

**It is a lower bound and the harness says so in its own docstring.** FLEURS is read speech of
Wikipedia-derived sentences, which is the easy end for a recogniser; spontaneous speech is worse at
the ASR step and worse again after it. `es_419` is FLEURS' only Spanish config, so the driving case
is one variety. One recording per sentence is used — the first of the three FLEURS supplies, the
same row the text is taken from. And **this is the pipeline without the German number rewrite**,
which is measured separately above, because the cascade arm translates in Python and the rewrite is
on the C# side.

**Per the decision of 2026-08-20 this is recorded and is not a gate criterion.** A bar argued for
after seeing the number is not a bar, and the gate already carries one criterion nobody has
performed.

**That failure now has a repair and an alarm, and the repair is measured to cost nothing.**
`GermanNumberWords` rewrites German **compound** cardinals as digits in
`TranslationRequest.Mark` — the one funnel every source string passes through — so
`neunzehnhundertneunundzwanzig` reaches the model as `1929`. It fires only on a token that parses
*completely* as a German cardinal *and* is built from two or more number words, which leaves
`zwei`, `zwanzig`, `neunzehn` and `hundert` untouched and leaves `Achtung`, `Dreieck`, `Zweifel`
and `dreißigjährige` untouched because each has a remainder the grammar cannot eat. **The condition
on shipping it was that it change nothing on written text**, since the chrF++ table above was scored
on FLEURS `raw_transcription` where numbers are already digits: over all 25 FLEURS `test` configs —
**20,146 rows, 8,499 distinct sentences, all 24 sources plus English** — it changed **nothing**, so
the sentences the shipping path sends the translator are still the sentences those figures describe.
That check is a re-runnable opt-in test rather than a note.
It is German-only, so the other 23 languages' number words are untouched, and it is unconditional
because nothing in this pipeline knows the source language — which the 25-config result is the
argument for and not a proof, since FLEURS is written prose and ASR output is not.

**And the repair is now measured to help, on the corpus the cascade run produced.** The cascade arm
above translates in Python and therefore does **not** apply the rewrite, which lives on the C# side
in `TranslationRequest.Mark`; putting the same 347 recognised German sentences through
`uindosill translate` produces the shipping output, and the two differ only in the path.
`scripts/measure-cascade.py --compare-normaliser` does exactly that and wrote what follows.

**chrF++ moves +0.15, from 59.30 to 59.45, and that number understates it by design.** A corpus
metric cannot report a number error — the difference between *"in 1889"* and *"in the eighteenth
century"* is a handful of character n-grams — which is the same reason the degenerate-repetition
counter sits beside the score rather than inside it. **Numeral recall is the measure that can see
it**: of the numbers the English reference carries, how many survive as digits into the hypothesis.
Over all 347 German sentences it goes from **46 of 105 to 62 of 105**, 43.8% to 59.0%. Over the
**17 sentences the rewrite changed** it goes from **2 of 29 to 18 of 29**, 7% to 62%.

**All 17 of the changed lines carry a German compound number token**, which is what attributes the
difference to the rewrite rather than to the port: the C# beam search's agreement with Python was
established on FLEURS transcripts and has never been established on recogniser output, so a
difference that landed anywhere else would have been unexplained. None did.

**The failure this was built for was caught again, in the wild, and repaired.** FLEURS German
sentence 1723 came through the recogniser as `im Jahr achtzehnhundertneunundachtzig`; without the
rewrite the translator returned *"in the eighteenth century"*, with it *"in 1889"*, and the English
reference says *"in 1889"*. That is a second instance of the 1929 failure, found rather than
constructed, and the first evidence that the repair works on the thing it was written for.
`neunzehnhundertsechsundsiebzig` → *1976*, `vierzigtausend` → *40,000* and `zehntausend` →
*10,000* are the same story in the same run.

**What this still does not establish.** Numeral recall is a crude measure defined in this
repository and comparable to nothing published: it asks whether a number survived, not whether it
landed in the right place, so a hypothesis carrying `1889` in the wrong clause scores as a hit.
Nobody has rated the 17 changed sentences for whether every change is an improvement — three of them
are cases where the German itself was misrecognised, and what a rewrite does to already-wrong input
is not something a recall figure answers. It is one language of 24 and one corpus of one, and
FLEURS is read speech, so the compound numbers in it are the easy end. And **the +0.15 does not
touch the gate**: German's margin was +42.86 against a required +24.22, and nothing here moves a
verdict.

**The alarm is `TranslationNumerals` and it is deliberately not a metric.** If the source carries a
numeral and the English does not, the segment is flagged — no per-language grammar, so it works for
all 24. The English side goes through `TranscriptNormalizer`'s existing English number-word rule
first, so a translation rendering `12` as *twelve* is not a false alarm, and separators are dropped
on both sides so German `1.000` against English `1,000` is not one either. It is one-directional: a
number the English *added* is not reported, because invention is a different defect and has not been
observed.

**Its rate on real recogniser output is now measured once, and it is low: 3 lines of 347.** On the
German cascade corpus `uindosill translate` flagged lines 88, 223 and 335, naming `2335`, `22` and
`130000`. That is 0.9%, which is the right order for something meant to be read rather than skipped
past. **What is not measured is how many of those three are right.** Nobody has checked them against
the audio, so the false-alarm rate is unknown — 0.9% is how often it *fires*, not how often it is
correct to fire, and those are different numbers. One of the three is suggestive rather than
reassuring: line 223's German was itself misrecognised, so the flag may be reporting a number the
recogniser invented rather than one the translator lost.

**What that number does not cover, and the list is longer than the number.** It is agreement on
**FLEURS transcripts**, which are read Wikipedia-derived prose — punctuated, well formed, and
nothing like ASR output with its missing final stops and its disfluencies; **no ASR output has been
put through both implementations and compared.** It is agreement **on this machine**: ONNX Runtime
partitions a matmul's reductions by thread count, so a machine with a different core count computes
slightly different logits, and the only thing standing between that and a different sentence is that
no two candidates were close enough to swap. It is agreement **at beam 6 with no context**, which is
the only configuration anything here has measured. And it says **nothing about quality** — the
chrF++ figures above are the quality claim, and what this establishes is that they describe the
loop that ships rather than only the Python that produced them. **No sentence in the corpus exceeds
512 tokens**, so the two implementations' different treatment of one that does — the harness skips
it, the product refuses it with `SegmentTooLongException` — has never been exercised against
anything.

**The feature's founding premise is settled — upstream, and on this stack.** That
`parakeet-tdt-0.6b-v3` writes each of its 25 languages *in* that language, rather than normalising
toward English, is what makes a translation pass necessary at all, and four things carry it, fetched
or run on 2026-08-19. NVIDIA's Granary dataset card defines the ASR target as same-language in its
own schema — `"target_lang": str, # Target language ("de" for ASR, "en" for AST)` — and the
technical report says of this checkpoint "we trained exclusively on the ASR subset of the
Canary-1B-v2 training set" (arXiv:2509.14128v1 § 3), which is an explicit statement about the
training objective rather than about runtime output. The card's 25-row multilingual table —
`bg 12.64%`, `el 20.70%`, `ru 5.51%` on FLEURS, scored against same-language references with only
punctuation and capitalisation stripped — is inferential, but English or transliterated output would
score near 100% on the Cyrillic and Greek rows, so it carries native script as well as language.
parakeet.cpp's own committed benchmark is the worked example: `antirez_italian.wav` comes back as
Italian prose, identical between NeMo and the ggml engine at f32, f16 and the quantisations, under a
protocol that passes no `--lang` at all — and the same clip through the English-only
`parakeet-tdt-0.6b-v2` in the same file set comes back as English word-salad, which is the control
that makes the Italian row mean anything. And two files were transcribed here, on the laptop's CPU
backend under `tdt-0.6b-v3-f16`: 61 s of a CC0 human narration of the Spanish Wikipedia article on
Caracas, and 75 s of a CC BY-SA 4.0 human narration of the German article on Ralf Dahrendorf. Both
came back in their own language, accented and punctuated, with numbers written as the speaker said
them and English proper nouns left in English inside the German. Neither was scored against a
reference: what those two runs establish is the output *language*, not accuracy.

**What the premise does not cover stays unproven, and one part of it is worse than unproven.**
NVIDIA states nowhere that the output is in the source language, and nowhere that the model cannot
translate; what the model card and the corporate blog say is that it "automatically detects the
language of the audio and transcribes it without requiring additional prompting". No
target-language parameter and no AST row exist for this checkpoint — an absence rather than a
denial, and recorded as one. Visible same-language output now covers three languages of the 25 —
Italian upstream, Spanish and German here — and the other 22 rest on the WER table's arithmetic
alone. **And same-language is the default rather than the whole distribution.** A third party
running this checkpoint through CoreML and FluidAudio on macOS publishes fluent English *inside*
French transcripts at 0%, 0%, 7.1%, 18.2%, 16.7% and 31.3% across six recordings, worst on
spontaneous rather than read speech (thoth-app.com, 2026-05-19, fetched that day). That is a
different runtime and a rate nobody has reproduced here, and nothing on this side can constrain it,
because `--language` is inert for this checkpoint (§ *The language hint* below) — the Cyrillic
segment on English-only audio is the same absence of conditioning seen from the other side. So the
premise holds as *same-language by default*, which is enough to justify a translation pass, and not
as *same-language*, which would let that pass assume its own input.

**No into-English quality figure exists for the recommended checkpoint.**
`opus-mt-tc-bible-big-mul-deu_eng_nld` publishes a single aggregate that mixes German, English and
Dutch targets, which says nothing about into-English alone; the recommendation rests on licence,
coverage and architecture rather than on a score. Its card disclaims its own coverage list in as
many words — "for a large number of language pairs it will not work at all" — so 25 of 25 is list
membership rather than capability. Its training-data composition was not examined either: the series
is named for a corpus whose register is nothing like spoken conversation, and whether that costs
anything on podcast or meeting audio is unmeasured. The sibling `opus-mt-mul-en` does publish
Tatoeba-test BLEU into English for 22 of the 25, and those numbers are **not** comparable to FLORES,
to WMT, or to this project's own WER normaliser; Croatian has no row at all — the 46.7 on that card
is the Serbo-Croatian macrolanguage and must not be quoted as a Croatian figure — and neither does
Slovak, consistent with its absence from that card's source list. Every one of those figures is a
beam-6 figure, which is now known to matter — the greedy-against-beam-6 delta is measured below.

**The C# tokenizer reproduces HuggingFace's `MarianTokenizer`, and that is now established rather
than hoped for.** It was the open question from the day the route was chosen, and it was answered in
the order the fixture was built for: `tests/fixtures/translation/marian-tokenizer.json` recorded the
ids `MarianTokenizer` emits for six fixed sentences at revision `bb1ef830d5` with nothing reading
it, so the port was written against a fixed target rather than against its own first output. It
matched all six — ids, pieces and the round-tripped decode — on its first run, and the trap the
fixture was built around was avoided by construction: `>>eng<<` is a single token, id 693, split off
the front of the string before SentencePiece sees anything.

**Six sentences is a start and not a proof, so the same tokenizer was held to 8,149.** What the
fixture cannot cover is the tail — a character no piece covers, a normalisation rule that fires once
in a corpus — and the tail is where a Unigram port goes wrong. The agreement measurement below is
what covers it, because a decode that reproduces a recorded hypothesis character for character
cannot have tokenised its source differently on the way in.

**What the C# port is, precisely.** A protobuf reader for the three fields of `ModelProto` that
matter; SentencePiece's compiled `nmt_nfkc` character map, read as the darts-clone double-array trie
it is stored as rather than reimplemented as rules; the Unigram Viterbi, advancing one UTF-8
character at a time with `min_score − 10` for a character no piece covers; byte fallback applied
after the search rather than inside it, because the 256 `<0xNN>` pieces are kept out of the trie so
the search cannot prefer a cheap pile of bytes to a real piece; and Marian's own language-code rule,
which is a prefix test and not a regular expression. **The Moses punctuation normaliser is not on
this path** — `MarianTokenizer` builds one and, in transformers 4.57.6, never calls it from
`_tokenize`, which was checked in the installed source rather than assumed, and is true whether or
not `sacremoses` is present.

**What it costs is measured, and it is not either of the two numbers this entry used to carry.** The
export exists as of 2026-08-20 — `scripts/export-translation-onnx.py`, run on the laptop against
checkpoint revision `bb1ef830d5`, with the artefacts left outside the working tree and their names,
byte counts and SHA-256s in the manifest that run wrote. **The route is nine or ten files, not
five**: two ONNX graphs in the merged layout (`encoder_model.onnx`, `decoder_model_merged.onnx`) or
three in the split one (`decoder_model.onnx` and `decoder_with_past_model.onnx` in place of the
merged decoder), plus `config.json`, `generation_config.json`, an `ort_config.json` the quantiser
adds, and a tokenizer that is **five** files rather than one — `source.spm` 736,809 B,
`target.spm` 808,244 B, `vocab.json` 1,514,254 B, `tokenizer_config.json` and
`special_tokens_map.json`. Measured directory totals, merged layout: **1369.1 MiB** at fp32,
**345.9 MiB** at int8, **694.3 MiB** at int8 with the embedding table left in fp32. The split layout
is the same graphs with the decoder stored twice — 2166.2, 545.7 and 1068.3 MiB — and produced
byte-identical translations to the merged one at every precision, so it costs about 800 MiB to buy
nothing that has been measured.

**There is no fp16 route, and that is the toolchain's fault rather than the model's.** Asked for on
2026-08-20 once int8 was dropped and the middle of the size range went empty, since half precision
reaches about the same place by a different road — a narrower float rather than a lossy quantisation
with scales and zero points, so not the kind of change that makes a decoder loop. `fp16-merged` was
added to the export and **it does not load.** It converts, it weighs the expected **686.4 MiB**, and
ONNX Runtime refuses the decoder. Two independent defects, both from ONNX Runtime's own float16
converter not accounting for the merged decoder's `If` subgraphs: it renames the `If` node's outputs
and inserts matching casts inside each branch while leaving the branches' declared output names
pointing at values nothing inside produces — `Subgraph output (logits) is an outer scope value being
returned directly` — and its dead-cast pass removes an outer-graph `Cast` that a subgraph still
consumes, so after the first is repaired the second surfaces as `Node input
'/model/decoder/Cast_2_output_0' is not a graph input, initializer, or output of a previous node`.
`keep_io_types=False` changes neither. **The encoder converts and loads cleanly**, 545.9 MiB to
**260.4**, because it has no subgraphs — so an **encoder-fp16 hybrid at about 1108.9 MiB** exists,
saving 19%, and **its quality has not been measured and it has not been run through anything**. The
broken variant is kept in the export table rather than deleted, and every converted graph is now
load-checked at export time, so the next person meets the failure at export rather than at scoring.
Nothing here was tried on the split layout, whose decoders carry no `If` node and might therefore
convert — at a layout this project measured to buy nothing for 800 MiB.

**The export reproduces its byte counts on a second machine and not all of its digests.** Re-run on
the desktop on 2026-08-20 against the same checkpoint revision, every byte count matched the
laptop's exactly — 1,435,604,524 B over 9 files at fp32-merged, 362,749,280 B over 10 at
int8-merged — and **eight of the nine files matched by SHA-256**, including both configs, all five
tokenizer files and `encoder_model.onnx`. **`decoder_model_merged.onnx` did not, at identical size,
at both precisions**: `1ff241e1…` there against `f7f63166…` here at fp32, `33dccc76…` against
`0a36526c…` at int8. The differing file is the one optimum *merges*, and the two runs used different
interpreters — CPython 3.14.7 there, 3.12.10 here, where the shim reports itself unnecessary and the
export runs unmodified, which is the first confirmation that a pre-3.14 interpreter needs none.

**And the interpreter is not the cause: the merged decoder export is simply not deterministic.** The
deciding test was run the same day — `fp32-merged` exported **twice on this machine**, same
interpreter, same cached checkpoint, minutes apart. Byte count identical both times, eight of nine
files identical by digest, and `decoder_model_merged.onnx` different again: `f7f63166…` against
`a2dea65e…`. Same size, different bytes, on one machine. So the laptop-against-desktop mismatch was
never about the machines, and **the file optimum merges comes out differently every time it is
merged.** What varies inside it was not identified — equal size with unequal bytes points at
ordering or at names of fixed length rather than at content, and that was not chased.

**So the scored copy was uploaded on 2026-08-20, and that is a change of practice rather than a
transfer.** Until then the graphs were deliberately kept off the Drive — the export folder's own
README says they are "nowhere on Drive and are not meant to be" — on the reasoning that the script
reproduces them and the script is therefore the artefact. That reasoning was wrong for one file of
nine. The nine files the gate was scored against are now in the dated folder
`translation-weights-fp32-merged-2026-08-20`, verified file by file against the digests the gate run
itself recorded, so the bytes every figure below describes still exist somewhere other than one
gitignored directory on one desktop.

**That changes what the export script is for, and it is worth being exact about.** `models.json`
pins a URL and a SHA-256 **together**, and a multi-file entry pins one per file. Eight of the nine
files are reproducible and verifiable by anyone; the ninth is not. So the artefact is something
**built once, uploaded, and pinned to that build** — the script records what an artefact *is* and
lets a reader check 8/9 of it, and it does **not** let a second party rebuild a byte-identical copy.
That is a defensible position and it is not the one two READMEs describe: `runs-laptop/README.md`
and `translation-onnx-export-2026-08-20/README.md` both say the script reproduces every byte count
*and digest*. Byte counts, yes. Digests, eight of nine.

Nothing measured on this machine is affected — the copy that was scored reproduced the CPU reference
on all 240 identity sentences below — but the laptop's own 44-segment smoke check has not been
re-run against it, and **no two copies of this graph have ever been shown to translate identically**;
what has been shown is that each copy reproduces fp32 PyTorch.

**The 227.3-or-404.4 spread was the right question about the wrong object, and both ends were low.**
Those figures count each parameter once; the ONNX export does not tie what PyTorch ties. The
`[58434, 1024]` matrix is emitted **three times** in the merged layout — as a `Gather` table in the
encoder, as a `Gather` table in the decoder, and again transposed as the output projection's
`MatMul` weight — and four times in the split one. So the toolchain's choice is real and it is a
single knob, `operators_to_quantize`: with ONNX Runtime's default dynamic set the `Gather` tables
quantise to UINT8 and the route is 345.9 MiB; dropping `Gather` leaves them FLOAT and the route is
694.3 MiB. But dropping `Gather` does **not** leave "the embeddings" in fp32, because the output
projection is a `MatMul` and quantises either way — which is why the intermediate variant is 694.3
rather than the 404.4 an untied count predicts. **Which end ships was decided on 2026-08-20 and it
is fp32** — `docs/PHASES.md` § *Decided 2026-08-20 — int8 is dropped*. The export script still
produces both and still picks neither, which is right: it records what the options are, and choices
belong where decisions are written down. **int8's chrF++ was never measured.** It was dropped on
speed, on a silent GPU collapse and on the export smoke, with the quality run that would have priced
it stopped in its second language. So "int8 scores worse against the gate" is not a claim this
repository makes, and the 1023.2 MiB the decision costs every download was paid without that number
in hand.

**Half the gate is now a number, and it is the half that needed no model.** The per-language
source-copy floor — what a hypothesis earns by echoing its untranslated source — was computed on
2026-08-20 for all 24 source languages by `scripts/measure-translation.py --floor-only`, over
FLEURS `test` in full (251 to 350 sentences per language, every sentence shared with `en_us` by id),
chrF++ at `nrefs:1|case:mixed|eff:yes|nc:6|nw:2|space:no|version:2.6.0`. **The floors run from 2.00
to 23.10**, and the shape is script rather than language family: Ukrainian 2.00, Russian 2.10,
Bulgarian 2.13 and Greek 2.37 — a non-Latin source shares almost no character n-grams with an
English reference — against French 23.10, Italian 22.42, Dutch 22.07, Portuguese 21.77, Romanian
21.76, Danish 21.64, Spanish 21.40, Swedish 20.91 and German 20.78, with the Latin-script Slavic,
Baltic and Finno-Ugric languages between at 14.54 (Latvian) to 16.84 (Estonian), and Maltese 19.42.
**That is an 11.5× spread, and it is why the gate was written per language on 2026-08-19 before any
of it was known.** A single bar of, say, 30 would have been a formality for a Cyrillic source and a
real test for French; the decision to refuse one number across 25 languages is now measured rather
than argued. Two things about the corpus travel with these figures: Dutch has only **251** sentences
shared with English where every other language has 329 or more, and the floors are scored against
FLEURS' punctuated `raw_transcription` on both sides.

**Criterion one is scored in every language, the margin was ratified the same day, and 23 of 24 pass.** The bar is `margin_L = 45 − floor_L` — one absolute chrF++ bar behind 24 per-language margins, because the scores turned out script-independent while the floors are not — plus a third criterion added at ratification: **zero degenerate collapses**. **Slovak fails**, by 0.74: +28.15 against +28.89 required.
Run on the desktop's CPU on 2026-08-20 against `fp32-merged`, FLEURS `test` in full, beam-6, batch 1:
**8,149 sentences in 1.40 h at a mean 0.618 s per sentence**, none skipped for length, chrF++ at
`nrefs:1|case:mixed|eff:yes|nc:6|nw:2|space:no|version:2.6.0`.

| | chrF++ | floor | margin | | chrF++ | floor | margin |
|---|---:|---:|---:|---|---:|---:|---:|
| bg | 62.66 | 2.13 | +60.53 | lv | 57.67 | 14.54 | +43.13 |
| cs | 60.88 | 15.96 | +44.92 | mt | 65.22 | 19.42 | +45.80 |
| da | 67.66 | 21.64 | +46.02 | nl | 57.53 | 22.07 | +35.46 |
| de | 63.64 | 20.78 | +42.86 | pl | 53.47 | 15.54 | +37.93 |
| el | 54.42 | 2.37 | +52.05 | pt | **68.52** | 21.77 | +46.75 |
| es | 56.17 | 21.40 | +34.77 | ro | 64.52 | 21.76 | +42.76 |
| et | 58.58 | 16.84 | +41.74 | ru | 57.61 | 2.10 | +55.51 |
| fi | 56.67 | 15.92 | +40.75 | sk | **44.26** | 16.11 | **+28.15** |
| fr | 63.95 | 23.10 | +40.85 | sl | 56.23 | 16.42 | +39.81 |
| hr | 59.00 | 16.44 | +42.56 | sv | 66.43 | 20.91 | +45.52 |
| hu | 56.75 | 15.45 | +41.30 | uk | 59.42 | 2.00 | +57.42 |
| it | 58.06 | 22.42 | +35.64 | lt | 54.35 | 15.47 | +38.88 |

**The scores are script-independent and the floors are not, which changes what a per-language margin
is.** chrF++ into English lands between 44.26 and 68.52 whatever the source script, while the floors
run 2.00 to 23.10 — so `margin = score − floor` is a roughly constant number minus a floor that
varies 11.5x, and a table of 24 margins is a **single absolute bar in disguise**. That is worth
knowing before anybody ratifies 24 numbers believing they are making 24 decisions.

**Slovak is the outlier and the record predicted it.** 44.26 against a next-lowest 53.47, nine
points clear of the pack, on a language this entry already noted has **no row on the sibling
`opus-mt-mul-en` card's Tatoeba table, consistent with its absence from that card's source list**.
That is one corpus and one metric, and it does not establish *why* — but a language the training
data was thin on scoring worst is the outcome that absence predicts, and it was written down before
the score existed.

**Criterion two has a sheet, no ratings, and nobody scheduled to write any.** `--adequacy-sheet`
wrote **60 Spanish rows** — source, model output and FLEURS reference side by side — and the
maintainer declined to rate them on 2026-08-20. That is recorded as the state it is rather than left
looking pending: the sheet exists, it is 30 minutes of work, and **it is not queued to anybody**.
Declined again on 2026-08-23, and this time with finality: **v1.0 ships without the check** — the
decision is in `docs/PHASES.md` — so this stops being an open item and becomes a permanent marker.
The gate stays unpassed by its own definition rather than being redefined to fit.
**So the gate is not passed.** Criterion one clears in 23 of 24 languages, the collapse ceiling
clears, and criterion two is unperformed — which is not the same as failed and is not the same as
met. Anyone reading a chrF++ table here and concluding the translation gate holds is reading two
thirds of it. What the missing third would have caught is the one thing no metric here looks at:
whether the English carries what the Spanish said, judged by somebody who can read both — and the
adjacent failure mode the spike found, output that is fluent German rather than English at all,
which chrF++ against an English reference scores as merely bad rather than as wrong. **The 32-sentence
Spanish shakedown slice that used to be recorded here is superseded**: Spanish over the full split
scores **56.17** against its 21.40 floor, where the subsample said 53.78 against 20.80.

**The 2026-08-19 spike's `int8` figures were never turned into a score.** int8 was dropped on
2026-08-20 before its quality run reached a second language, so no chrF++ exists for it — see
*Which end ships* above.

**fp32 collapsed nowhere in 8,149 sentences, and the 31 things the detector flagged are a different
defect wearing the same shape.** The harness counts degenerate repetitions beside the score because
chrF++ cannot report one — a single collapsed sentence among three hundred moves a corpus figure by
a fraction of a point. It flagged **31 of 8,149, 0.38%**, concentrated in Maltese (**17 of 348, 4.9%**)
and Greek (5). **Every one of the 31 is a run of trailing punctuation** — `Wild children may have
experienced child abuse or severe trauma before they have been abandoned or evicted. . . . . . .` —
and **not one is the semantic collapse the detector was calibrated on**, the int8
`...Genocococococeaea` the export found. Those are two failures: one is a decoder that has finished
translating and will not stop emitting, the other is a decoder that has lost the sentence. The first
would look broken in a subtitle and costs no meaning; the second costs the meaning. **So the honest
statement is that fp32 produced zero collapses and 31 punctuation runs**, and the detector's own
guard is why they were counted together — it skips a repeated chunk that is all one character from
`" .-\n"`, which catches `"..."` and misses `". "`, two characters. Both counts are worth having and
they should not share a column, so as of the same day the harness reports them as two —
`collapse` and `punctuationRun`, with `kind` on every row of `degenerate.jsonl`. **The run's own
`summary.md` predates that split** and reports the combined 31; the split above was computed from
that run's saved per-sentence hypotheses, which carry the repeated unit, and it is a relabelling of
the same 31 rather than a second measurement.

**Batching this model on a CPU is six times slower than not batching, which is the opposite of the
usual.** The same 32 Spanish sentences took 12.75 s each at batch 16 and 2.16 s each at batch 1. A
padded beam-search batch decodes until every member finishes, so one long sentence holds up fifteen
short ones while beam-6 keeps 96 sequences in flight. The harness defaults to batch 1 because of
that measurement.

**It reproduces on the desktop, slightly worse, and it inverts on a GPU.** Same protocol, same 32
sentences, `int8-merged`, beam-6, on the desktop's CPU: **1.279 s at batch 1, 2.348 at 4, 3.897 at 8
and 8.949 at 16** — a factor of **7.0** the wrong way against the laptop's 5.9. So batch 1 is the
right default on two CPUs rather than one. But it is a **property of the CPU, not of the model**: on
the CUDA EP the same padded batch runs **0.163 s per sentence at batch 1 and 0.021 at batch 16**,
7.8x *faster* for batching, because the lanes a padded batch wastes are lanes a GPU had anyway.
Anyone carrying "batching this model is six times slower" forward as a fact about the model will be
wrong on a GPU by about fifty times. The batch-16 CUDA figure prices a configuration nobody should
ship, for the reason two paragraphs below.

**The catalogue holds those nine files, they are published, and one has now been installed from a
real URL.** The entry is `opus-mt-tc-bible-big-mul-en-fp32`, the manifest's first multi-file entry,
nine files in a directory of its own with a size and a SHA-256 each, totalling 1,435,604,524 B. Its
digests were taken off the bytes the gate was scored against and re-hashed from disk; on 2026-08-20
the files went to Hugging Face and **every one of the nine LFS `oid`s the repository publishes
matched** — an LFS oid is the file's SHA-256, so the pin is attested by the repository as well as by
the machine that built it, which no other entry here can say. `"verified": true` means both checks
agreed, and the URLs pin the commit sha rather than `main`.

**The first real multi-file install happened the same day and it worked.** `models download`
assembled the nine in an `opus-mt-tc-bible-big-mul-en.part` staging folder, fetched, sized and
hashed each one, renamed the folder into place, and printed all nine digests; `models verify`
re-hashed them off disk afterwards and reported **9 of 9 files match**. So the staging-directory
install, the per-file pins and the all-or-nothing rename are experience now rather than only tests
against a stub HTTP handler.

**And the graphs were then loaded out of that assembled directory and used.** The normaliser
comparison above ran `uindosill translate` with no `--translate-model-path`, so the translator
resolved through the catalogue to the store — `TranslatorFactory` refuses outright when the entry
is not installed — and produced 347 English lines at 0.467 s each. That closes the last of it: the
route from a pinned manifest entry to a running ONNX session has now been walked end to end, where
before today every figure in this document reached the checkpoint through `--translate-model-path`
against a directory the export script left behind.

**What that install did not exercise is interruption.** Resume-per-file, and the guarantee that a
crash leaves a staging folder rather than a half-installed model an engine would try to load, are
still code with tests against a stub HTTP handler behind them: the one install that has ever run
ran to completion.

**The recorded export failure was misdiagnosed, and the correct diagnosis is cheap to act on.** It
is not a skew between `optimum` 2.1.0 and `transformers` 4.57.6, and no pinned pair of them fixes
it: it is **CPython 3.14**, which gave `functools.partial` the descriptor protocol. optimum stores
every `NORMALIZED_CONFIG_CLASS` as a class-attribute partial and reads it as
`self.NORMALIZED_CONFIG_CLASS(self._config)`, so under 3.14 the instance binds as the first
positional argument, the config lands in the `allow_new` slot, and the constructor reports
`got multiple values for argument 'allow_new'` from inside the normaliser — which is where the
traceback pointed and why the cause looked like optimum's. Re-wrapping those 24 partials in
`staticmethod` at the caller restores the pre-3.14 reading; the export then runs unmodified, and
nothing in the venv is touched. The "one build-time Optimum run" the plan assumes is a command
again — about 30 s for the fp32 export and 15 s per quantisation on this laptop — with a
twelve-line interpreter shim in front of it. A 3.13 interpreter would need no shim at all, and that
route was not taken or tested here.

**The export reproduces fp32 PyTorch exactly, and int8 does not.** Both layouts at fp32 returned
strings identical to the fp32 PyTorch reference on all 50 segments put through them: the six fixed
smoke sentences in-process, and all 44 real segments — 13 Spanish, 31 German — that the 2026-08-19
spike recorded at beam-6, re-translated and diffed string by string. That is the check the ASR's
silent int8 collapse is the reason for, and fp32 passes it outright. **int8 does not**: with the
default operator set 5 of 13 Spanish and 5 of 31 German segments matched, and with the embedding
table left in fp32, 5 of 13 and 8 of 31. Most of the disagreements read as paraphrase —
`the North Coast of the country` against `the north coast of the country`, `twelve kilometres`
against `12 kilometres` — but not all of them: one German segment came back as
`...Attribution Share Alike 4.0 Genocococococococeaea` under both int8 variants, a degenerate
repetition the fp32 reference does not produce, and another turned `separated from the central
coast by the mountain range of the coast` into `Separated from the central coastline by the coast
coastline`. **What none of this is, is a quality measurement.** An exact-match rate against fp32 is
not chrF++, it does not touch the gate, and paraphrase and corruption are counted the same by it;
the only claim it supports is that the int8 route changes the output on most segments and collapses
on at least one in 44. Whether that costs anything against the gate is unmeasured, and it is the
next thing the harness is for.

**int8 is also slower here, which leaves it with only the download argument.** Per-segment beam-6
decode over the same 44 recorded segments, one segment per call after an uncounted warm-up, best of
three passes on this laptop's CPU: PyTorch fp32 **1426.9 ms**, ONNX fp32 **579.9 ms**, ONNX int8
**630.5 ms**, ONNX int8 with the embedding table in fp32 **624.8 ms**. So the export is worth about
**2.4×** against PyTorch on its own, and int8 gives about **9%** of that back. Passes within a
process agreed to under 2%, and a second process reproduced the ordering and the ratios while the
absolute figures moved by up to 3.5%, so the direction is not noise — but it is one machine, one
thread setting, and no CPU pinning. **These are not real-time factors**: nothing here divides by an
audio duration, and they are not directly comparable to the spike's 0.579 s Spanish and 1.44 s
German PyTorch figures, whose mix and protocol differ. Peak memory with the ASR model, the diariser
and a translator resident is still unmeasured on both
machines. **ONNX Runtime having no Vulkan execution provider is now verified** — its
execution-provider list carries CUDA, TensorRT, OpenVINO, DirectML, oneDNN, QNN, CoreML, ROCm,
MIGraphX, Vitis AI, WebGPU and others, and no Vulkan — but the conclusion drawn from that was wrong:
**DirectML is the Windows GPU path**, it covers this laptop's Radeon, and it ships for .NET. Its
package lags the one it would replace — `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 against the
pinned `Microsoft.ML.OnnxRuntime` 1.29.0 (`Directory.Packages.props:47`) — and the two are
alternative builds of a single native library, so adopting it moves the diariser onto it as well.

**The desktop's fp32-against-int8 gap is 2.1x on Spanish, and it grows to 8.3x on longer decodes.**
Same beam-6 decode, 32 FLEURS Spanish sentences, batch 1, on the desktop's CPU: `fp32-merged`
**0.602 s per sentence** against `int8-merged` **1.279**. The laptop's 9% and that are not the same
measurement — the laptop timed 44 recorded ASR segments — but a factor of two does not come out of a
corpus change. **And 2.1x is the flattering end of it.** Both precisions were then started on the
full FLEURS run, which sorts by length, and the cumulative rate was read off at matched indices in
the same language (Bulgarian, the first scored):

| sentences in | int8 s/sentence | fp32 s/sentence | ratio |
|---:|---:|---:|---:|
| 100 / 344 | 1.73 | 0.41 | 4.2x |
| 150 / 344 | 2.90 | 0.44 | 6.6x |
| 200 / 344 | 3.49 | 0.47 | 7.4x |
| 239 / 344 | 4.07 | 0.49 | 8.3x |

**The gap widens with decode length**, which is what a per-step cost looks like: dynamic
quantisation re-quantises activations on every decoder step, beam-6 takes one set of steps per
output token, and a Cyrillic source takes more tokens than a Spanish one. Those are cumulative
averages over a length-sorted run, so the marginal ratio at the long end is steeper still. The int8
column stops at 239 because that run was stopped there when the precision was dropped; the numbers
above are what it had produced by then, on the same machine, corpus, ordering and code as the fp32
column beside it.

**On CUDA, int8 does not merely lose — it produces gibberish, and says nothing.** `int8-merged`
through the CUDA EP returned **0 of 32 sentences matching the CPU**, none of them empty:
`These couples may choose to plan the adoption of a baby` came back as `The so for`, and another
sentence as a run of German function words and punctuation. No exception, no warning that the
results were wrong — the only signal was a throughput too good to be true, 0.033 s per sentence at
batch 16, which is the decoder giving up rather than the GPU being quick. The suspect is the
dynamic-quantisation operator set under the merged decoder's `If` node on that provider; it was
**not root-caused**, because what decides anything is that the cheap artefact does not run correctly
there. This is the same shape as the ASR's silent int8 collapse, and the reason a speed figure
without an identity check beside it is not evidence.

**`fp32-merged` on CUDA is string-identical to the CPU, and its speed-up is 1.2x to 1.5x once it is
configured so it does not crash.** Identity first: **240 of 240 sentences matched** — 60 each of
Spanish, German, Russian and Greek, chosen to span Latin, Cyrillic and Greek script, beam-6, batch 1.
So unlike the diariser, a GPU-produced hypothesis set for this component *could* stand in for a
CPU-produced one. The speed is the disappointment. With IO binding on, where the KV cache stays on
the device between decode steps, it is 0.163 s per sentence against the CPU's 0.602 — and it
**crashes**, input-dependently, inside optimum rather than ORT: `the output OrtValue provided for
output 'present.0.decoder.key' of node 'optimum::if' (If) has shape {6,16,22,64} but the computed
output shape for this run is {6,16,1,64}`, a pre-allocated buffer whose shape went stale across the
merged decoder's branch. It completed 32 Spanish, 60 German and 60 Russian and failed on 60 Spanish
and 60 Greek, so it is the sequence of lengths rather than the language. With IO binding off — the
only configuration that survives every input — the cache round-trips through host memory each step
and the margin is **1.54x on Spanish, 1.53x German, 1.41x Russian, 1.24x Greek**. That is what a
second native stack and a driver dependency would buy, for a component that is one of two sharing
the native it would replace.

**The encoder's position limit is settled, and it was never the largest gap.** The recommended
checkpoint's `config.json` sets `max_position_embeddings` to **1024**, not the 512 the study carried
over from its sibling; its tokenizer still declares 512, which is the number to design against.
Token density measured with that tokenizer puts Maltese highest at 0.431 tokens per character, then
Greek 0.417 and Ukrainian 0.399, against German 0.307 and Spanish 0.285 — so at the 14.6 characters
per second this project's own ASR output runs to, a full 30-second segment projects to **at most
about 190 tokens**. Measured peaks on real segments agree: 6.3 tokens per second in both Spanish and
German, or 189 tokens across 30 s. That is 2.7× headroom against the stricter of the two limits.
Two things keep it a projection rather than a measurement — the density figures come from written
text rather than speech, and 23 of the 25 languages have had no audio through this pipeline at all —
so `MaxSourceTokens` still belongs on the capability, and an over-long segment is still refused
rather than truncated. It guards an edge now, not the common case.

**Three decode facts, measured on this project's own transcripts.** First, **the target token is not
optional, and its absence is invisible**: the same Spanish segments without `>>eng<<` come back as
fluent German, the checkpoint's first declared target, so the prefix has to be an invariant the
translator enforces and a test asserts rather than a convention a caller remembers. Second,
**English input is returned byte-identical**, which closes the drift question this entry opens
above — a segment the ASR wrongly emitted in English costs nothing to pass through, and no language
detection is needed to protect the pass from it. Third, **greedy decoding is not safe.** Over 44
real segments, 13 Spanish and 31 German, greedy and beam-6 disagreed on 26, and the disagreements
are content rather than register: greedy rendered `por la cordillera de la costa` as "by the
coastline", dropped "in Hamburg" from a birth date, and wrote 1921 for a year beam-6 read correctly,
while beam-6 came out materially shorter than greedy once against greedy's six. Beam-6 costs 2.1×
to 2.3× the time — 0.28 s to 0.58 s per segment on Spanish, 0.63 s to 1.44 s on German, fp32 on this
laptop's CPU — so the decode loop needs beam search or ONNX Runtime's contrib operator, and neither
is the afternoon a greedy loop would have been.

**And the cascade has an interaction that neither model shows on its own.** This ASR writes German
numbers as words — `neunzehnhundertneununundzwanzig` — and both decodes mangle exactly those, into
"nineteen-ninety-nine", "nineteen and twenty-nine" and "194". Dates and figures are where the two
models meet worst, they are what a listener checks a transcript for, and no metric proposed in the
study is pointed at them.

**The measurement's own inputs are not all resolved.** CoVoST-2's licence is stated two ways across
four sources — CC0 in its README and its paper, CC BY-NC 4.0 in its `LICENSE` file and on its
Hugging Face mirror — and was not resolved; it also covers 11 of the 25, which is why the study
proposes FLEURS pinned by digest instead. Any FLEURS figure is a lower bound on the cascade penalty,
because it is read speech of Wikipedia-derived sentences and the ASR half of the cascade is
correspondingly easy; `es_419` is its only Spanish config, so the driving case would be measured on
one variety; and its n-way alignment across configs is asserted by its card and unverified here, so
a harness has to check it and refuse to score on a mismatch. This project's own ASR error rate was
measured in English only until 2026-08-20; the cascade run above adds Spanish at 6.12% and German at
9.93%, so it is **2 of 24 now and 22 still have none**. COMET cannot score Maltese,
because the encoder underneath it was never trained on it. And no published chrF++ or BLEU for any
candidate on FLEURS X→en at a stated signature was found, so this gate cannot be anchored to
somebody else's number the way the DER gate is. The gate ratified on 2026-08-19 answers that by
anchoring inside the measurement instead — the per-language source-copy floor, and a human adequacy
check on the driving case, both of which must hold — and `docs/PHASES.md` carries it as a row. What
the floor's per-language margins actually are is not yet set, and cannot be until the floor is
computed.

**Output length into English is unmeasured for the languages where it matters most** — Finnish,
Hungarian, Estonian and every Slavic source have no figure at all — and the cue-readability check
that would turn a length figure into a subtitle claim does not exist yet, so nothing can be said
about translated subtitle quality in either direction. A figure on five of 25 languages is not a
figure on the feature, and the unscored ones cannot simply be withheld: gating per language means
knowing the source language, which this pipeline does not, so with a many-to-one model the honesty
has to live in the text rather than in a control. That is a cost of the one-checkbox design, and it
is recorded as one.

**The MADLAD-400 exclusion is dated rather than permanent.** It rests on a byte count and a grep
taken from `llama.cpp` `master` on 2026-08-19 with no commit pin, against an upstream issue that is
open, so it says what that binary could not do that day and nothing about what it will do.

### The C# translator retired 2026-08-21, and what the entry above now describes

**`Parakeet.Engine.Marian` went to `attic/`, and what ships is the library the gate ran on.** The
decode is `optimum`'s `ORTModelForSeq2SeqLM` driven by `transformers.generate` at **beam 6,
`max_new_tokens` 512, length penalty 1.0, early stopping off, IO binding off**, inside the same
bundled Python as the diariser. `docs/PHASES.md` § *Decided 2026-08-21* carries the reasoning.

**Four passages above are now history rather than description**, and they stand because each is the
record of a question that had to be answered: *The beam search is a port of one implementation*,
*8,148 of the 8,149 reproduce the recorded hypothesis character for character*, *The C# tokenizer
reproduces HuggingFace's `MarianTokenizer`*, and *What the C# port is, precisely*. None of them
describes the loop that runs. The one worth keeping in reach is the account of which logits
processors this checkpoint's decode applies — two, and no more — because nothing in this repository
reads that off the library any more; `attic/README.md` says where the reading lives.

**The chrF++ table describes the shipping decode, and as of 2026-08-21 that is measured rather than
argued.** The whole gate corpus went back through `uindosill translate` against the sidecar — 24
languages, 8,149 sentences, held to the hypotheses the 2026-08-20 run itself recorded — and **8,149
of 8,149 reproduce the recorded hypothesis character for character: 100%, every one of the 24
languages at exactly 100%, and not one disagreeing pair to write out.** Run on the desktop against
`fp32-merged` on **WebGPU**, at a mean **0.526 s per sentence** against the gate run's own 0.618 on
the CPU. `runs/translation-agreement/20260821-081700-fp32-merged/` holds it.

**The one disagreement the C# port carried is gone, which is the result the retirement predicted.**
Hungarian 1818 — the already-degenerate sentence where the port wrote 171 trailing dots against the
recorded 248 — is now inside hu's 348 of 348. So the +0.04 chrF++ that the entry above computes in
the port's favour describes nothing that ships, and all 24 published figures are the shipping
decode's own rather than 23 of them.

**What the run crossed, and the one thing it did not.** Same machine, and the same three packages
that decide the decode as the gate — torch 2.13.0, transformers 4.57.6, optimum 2.1.0, on the same
CPython 3.12.10 — but **a different ONNX Runtime and a different execution provider: 1.27.0 on
WebGPU here against 1.29.0 on the CPU there**. So agreement survives a runtime version and a
provider change, which is more than was asked of it. What it does not touch is the caveat the port's
own agreement measurement carried, which applies unchanged: **ONNX Runtime partitions a matmul's
reductions by thread count**, so a machine with a different core count computes slightly different
logits, and the only thing between that and a different sentence is that no two candidates were
close enough to swap. **No second machine has run this**, and the interpreter was a virtual
environment matching `python/requirements-bundle.txt` line for line rather than an assembled bundle.

**Before that corpus run there was a smoke test, and it is worth keeping at its real size.** On the
desktop on 2026-08-21, against the real `fp32-merged` checkpoint through the sidecar: on the **CPU**,
load 10.1 s, the six committed parity sentences back 6 of 6 identical, and 0.30 s for one 18-token
Spanish sentence; on **WebGPU**, load 5.7 s, 6 of 6, and 0.19 s for the same sentence. **One
machine, one checkpoint, six sentences and a stopwatch.** It is not a corpus score, and the loads
were against a virtual environment rather than an assembled bundle.

**One thing that run did close.** *No sentence in the corpus exceeds 512 tokens*, above, records
that the over-long path had never been exercised against anything. It has now: an **802-token source
was refused without being decoded**. The limit travels with the request so the sidecar counts before
it translates, and the refusal is still thrown on the C# side — the same division of labour the rest
of the seam keeps. One source, once: the path is exercised, not characterised, and nothing has
checked the count the refusal turns on against what the model would actually have accepted.

**And one conclusion above is now measured to be wrong.** *ONNX Runtime having no Vulkan execution
provider is now verified* ends by concluding **"DirectML is the Windows GPU path"**, and reasons from
there about `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 lagging the pinned 1.29.0 and about adopting
it moving the diariser as well. DirectML was measured on 2026-08-21 and is refused by both engines:
on the translator it returned **0 of 32** sentences matching the CPU — a repetition-loop collapse —
at **21.5x slower** than the CPU. The encoder and the decoder are each clean on DirectML at full
optimisation when driven directly, so that collapse is in `optimum`'s merged KV-cache path rather
than in the provider, which is the opposite of the diariser's defect and is why the two engines
refuse it for different reasons. The package was never adopted and the question is moot on this
side: **no project in `Uindosill.slnx` references ONNX Runtime at all**, the pin surviving in
`Directory.Packages.props` with nothing consuming it. The provider table and the diariser's DirectML
defect are in *That provider comparison is superseded*, above.

**What the retirement cost the suite.** The seven checkpoint tests that read real translation
weights — the tokenizer against its committed fixture, the translator against the checkpoint — went
to the attic with the code they exercised, and **nothing in the suite replaces them**. Nothing in CI
now reads real translation weights at all. What stands in their place is the parity fixture below,
which needs a checkpoint and an interpreter and therefore fires at load on a real machine.

### The translator's parity fixture has no margin, and says so

**Six sentences, compared by string equality.**
`python/uindosill_engines/translator/parity-sources.json` carries the six sources verbatim from
`tests/fixtures/translation/marian-tokenizer.json` — four of them real ASR output from this
project's own pipeline, Spanish and German — and `parity-reference.json` carries what the CPU
produced for them. A provider either reproduces those six strings or it does not.

**That is a verdict with no distance in it, and the diariser's fixture is the contrast.** The
diariser compares probabilities against 1e-4 and reports how far it missed by, so a provider
drifting towards wrongness is visible before it arrives. A string matches or it does not, so **there
is no number to watch**, and a provider wrong only on long or unusual input passes. It catches the
failure that has actually been seen — DirectML wrong on all 32 sentences of the provider comparison
— and nothing subtler. That is the whole of its claim, and it is made here so that a green parity
line is not read as a quality result.

**It also cannot run in CI**, needing the fp32 checkpoint and an interpreter, which is why
`CLAUDE.md` asks for a CPU load and a WebGPU load by hand after any change to
`python/uindosill_engines/translator/`. Six sentences on demand is what replaced seven hermetic
checkpoint tests. It is less coverage, recorded as less.

**What would give it a margin** is a distance rather than a verdict — token-level agreement, or a
score against a slice of the gate run's own recorded per-sentence hypotheses, which that run wrote
down. Neither has been built.

### The bundled interpreter is 1.20 GB, and no installer has been built with one in it

**The bundle itself is measured.** `scripts/bundle-python.ps1` assembles it — pinned embeddable
CPython 3.12.10 with its SHA-256 checked before unpacking, the pins in
`python/requirements-bundle.txt` installed with `pip install --target`, and the `uindosill_engines`
source beside them — and on 2026-08-21 it produced **1.20 GB in 43,760 files**. That is **not** the
~0.55 GB the migration budgeted: the estimate counted `onnxruntime-webgpu` at 0.07 and a CPU-only
torch at 0.48 and missed the transitive set. Where the difference goes is measured too: about
**330 MB** is `librosa` and what it drags in — numba, llvmlite, scipy, scikit-learn, soxr, pooch —
for exactly one call, `librosa.filters.mel`; about **95 MB** more is `sympy` and `networkx`, which
are torch's.

**The assembled bundle was driven end to end the same day**: it answered the handshake, loaded the
real translation checkpoint on WebGPU in 5.5 s, passed the translation parity fixture 6 of 6, and
translated a sentence in 0.15 s — and the shipping CLI drove it through `UINDOSILL_PYTHON` and
produced correct English. So the bundle works; what has not been measured is everything downstream
of it.

**What is still unmeasured is the installer.** No package has been built with a bundle inside it, so
nothing is known about how long it takes to install, what the sidecar costs to start from a cold
one, what an unsigned 1.2 GB of interpreter and native wheels does to a virus scanner or to
SmartScreen, or what it does to Velopack's delta packages — which is the figure most likely to be
unpleasant, since a delta against a tree of 43,760 mostly-unchanging files is a question nobody has
asked.

**The third artefact is written and has never been produced.** The CLI zip carries no interpreter,
so on 2026-08-21 the same bundle became its own download — `uindosill-python-win-x64.zip`, packed by
`scripts/package-windows.ps1` from the publish it already assembles, `python/` at its root, unpacked
into `%LOCALAPPDATA%\Uindosill` where `PythonRuntime` now looks (`docs/PHASES.md` § *Decided
2026-08-21*). The resolver's three-place search is covered by tests and the packing step reads its
own zip back, but **the script has not been run with that step in it**: the zip has never been
built, never unpacked, and never resolved from on a machine that did not also have a repository
checkout. Its size is assumed equal to the bundle's 1.20 GB and has not been observed after
compression.

**Superseded in part, 2026-08-23: the `v1.0.0-rc.3` release built all of it, and the sizes are now
observed.** CI packed both channels with the bundle inside and produced the zip for the first time:
`uindosill-python-win-x64.zip` is **400.2 MB** — a third of the unpacked 1.20 GB, not the ~1.2 GB
download the plan had assumed — with `UindosillDesktop-win-Setup.exe` at **485.4 MB** and the CUDA
flavour at **1187.9 MB**, against 81.9 MB and 818.6 MB bundle-less. What remains unobserved is
everything past the byte counts: the zip has still never been unpacked and resolved from on a
machine without a checkout, and no bundle-carrying installer has ever been installed.

**Every execution-provider figure still comes from a development virtual environment**, not from a
bundle: the AMI arms, the 32-sentence translator arms and the diariser's parity numbers were all
taken against `~/webgpu-venv`. The bundle reproduced the translator's parity fixture and its
per-sentence time; the diariser has not been re-scored from one.

**For scale, the installer this repository has actually built and installed** is 81.9 MB for the
default channel and 818.6 MB for the CUDA one (§ *The installer was built, run, updated and
uninstalled*). Adding 1.2 GB to the default channel is a different download from the one
that was measured, and the delta figures recorded there — 74,470 bytes against a 77,462,188-byte
full package — describe a tree with no interpreter in it.

**Both opt-ins are disabled when the bundled Python is absent, with that as the stated reason.**
That is the right behaviour and it has a consequence worth writing down: until a bundle exists,
speaker labelling and translation are reachable only on a machine carrying an interpreter and the
packages by hand, so **nothing has been observed from an install** — which is the same gap
`docs/PHASES.md` has recorded since 2026-08-15 about transcription from a CI-built binary, now
extended to two more features.

**One consequence of the bundle being CPU-only is already priced.** The translator's IO binding
needs a torch device the CPU-only bundle will not have, so every translator figure here is measured
with **IO binding off** — a floor rather than a ceiling, as the CUDA measurement that reached
0.163 s per sentence with binding on, and crashed input-dependently inside `optimum`, shows.

### No AMD GPU has run any of this, and WebGPU's faithfulness is one card

**The provider that ships was chosen because it reproduces the CPU's answer, and that agreement is
one NVIDIA card, one driver, one run.** 16.3319% against 16.3324% on the diariser, 1.073e-06 on the
diariser's parity fixture, 32 of 32 string-identical on the translator: every one of those figures
comes from the RTX 5080 desktop on 2026-08-21. **Nothing about a WebGPU implementation on another
vendor's driver follows from them**, and the reason is measured rather than cautious — DirectML, on
that same card and that same graph, is wrong by 36.82 DER points at ONNX Runtime's default
optimisation and right at `ORT_DISABLE_ALL`. What a provider does to this graph is a property of the
stack under it.

**The machine that would answer it is already in this document.** The second machine's Radeon 880M
is the only non-NVIDIA GPU this project has, and it has never run either engine. The cheapest first
step is the diariser's parity fixture — synthetic mel from a seed, no audio, no licence, and a
number rather than a verdict — which would separate a faithful implementation near 1e-06 from a
diverging one near 1e-03 before any corpus is scored.

Until that is done, the honest statement is that **WebGPU is verified on the one card it has been
verified on**, and anybody running another is running a configuration this project has not measured.

### No Apple platform has ever been attempted

**Recorded as never attempted, which is not the same as a gap and not the same as a regression.**
macOS is not a target: the CLI publishes `win-x64;win-arm64;linux-x64` and the desktop app
`win-x64;win-arm64`, the natives this project vendors are parakeet.cpp v0.5.0's Windows assets, and
nothing here has ever been built, run or measured on Apple hardware. CoreML appears in the
execution-provider list recorded above and nothing has asked for it.

The move of 2026-08-21 changes none of that in either direction: a sidecar would need an interpreter
bundle for the platform, and **no bundle exists for any platform yet** (above). This entry exists so
that the absence of any Apple figure reads as a target that was never in scope, rather than as
something that once worked.

### Cancelling a translation now waits for a decode in another process

**`SupportsCancellation` is false for the translator as of 2026-08-21.** The in-process beam search
it replaced declared it true and meant it — the search polled between steps, so a long segment did
not have to finish. A decode running in a child process cannot be interrupted, so a cancel now stops
the next segment being sent and **the one in flight finishes**. That is a behaviour rather than a
defect, and it is written down because what is not measured is how long the wait is.

**Nothing here bounds one segment's decode.** The figures that exist are means over corpora of
written sentences — 0.618 s per sentence over the 8,149-sentence gate run, 0.595 s on the CPU over
32 FLEURS Spanish sentences — and a mean over FLEURS is not the tail of the distribution a
30-second segment of real speech at beam 6 sits in, on a machine slower than this one. It is the
same shape as the mid-batch window close bounded by one native batch call, recorded above, and it
has the same answer: the worst case has not been timed.

The ASR's own `SupportsDecodeCancellation` is a different thing and is unchanged —
§ *Cancellation: not possible mid-decode*.

### The CUDA drop's licensing — read, recorded, and the notice gap closed

`cublas64_12.dll`, `cublasLt64_12.dll` and `cudart64_12.dll` are NVIDIA proprietary binaries under
the CUDA Toolkit EULA, not MIT. `NOTICE.md`, `docs/LICENSING.md` and
`src/Parakeet.Core/Licensing/Attribution.cs` between them listed five MIT components and no NVIDIA
entry, and `Attributions.Components` is what `uindosill notice` and the app's About window render —
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

### GGUF quantisation quality — measured on one corpus, 2026-08-16

This item read "unmeasured" from the first commit until 2026-08-16, and the reason was always the
same: no ground truth existed for any audio this project had run, so every figure was divergence
from f16 rather than error. The analogous ONNX INT8 export had been measured at **24.8% long-audio
WER against 7.8% for fp32** — and it collapsed *silently*, producing fluent wrong text rather than
obvious garbage — which is why the catalogue said "assume nothing" on every entry below f16.

**It is now measured, once.** The section *Word error rate against human transcripts* above has
all five entries against eleven hours of human-transcribed accented English on CUDA: **f16 10.21%,
q8_0 10.23%, q6_k 10.17%, q5_k 10.17%, q4_k 10.15%** against verbatim transcripts, and 13.34–13.43%
against non-verbatim ones — a spread of 0.08 points, no ordering, no collapse. The divergence
ladder that preceded it (q8_0 0.42% to q4_k 2.69% of tokens differing from f16 over 2 h 55 m,
above a 0.11% backend floor) stands as measured and turns out to be divergence in *which* words are
wrong rather than in how many.

What is cleared and what is not, precisely: on this corpus, with this normaliser, no catalogue
quantisation costs measurable accuracy against f16. That is one English corpus of one genre on one
machine, at a resolution of about a tenth of a point; it is not a clearance for other languages,
other material, or a claim about the leaderboard figure for the model. It is enough to stop the
catalogue saying "unmeasured", and it says so instead.

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

**The script did guard against this, and the guard had a hole worth knowing.** Line 165 compared
`$left.Words.Count -ne $right.Words.Count`, and when the totals differed it refused the per-word
figures outright — *"index alignment is not valid … would be an artefact of the offset rather than a
measurement"* — and printed the first divergence instead. That guard fired correctly on the CPU
versus Vulkan comparison below, at 1,606 against 1,605 words. It could not fire here, because f16
and q4_k both produced **exactly 1,606** words: the insertions and deletions cancel in the total
while leaving the sequence misaligned. **A total-count check cannot detect offsetting edits**, so
the one case that defeated the guard was two transcripts of coincidentally equal length — which is
the likely case whenever two variants of the same model are compared.

**Fixed 2026-08-16.** `compare-transcripts.ps1` now aligns the two word streams by word-level
Levenshtein distance — the same code as `word-distance.ps1` and the CLI's `wer` command,
`src/Parakeet.Core/Text/WordAlignment.cs`, loaded into the script with `Add-Type` from the source
tree — reports substitutions, deletions and insertions separately with raw and normalised counts,
names the first divergence by kind, and computes its timestamp and confidence figures over the
pairs the alignment made rather than over positions. The laptop pair that produced the 727 is
gone (`runs/` is gitignored and machine-local), so the fixed tool's report on *that* pair cannot be
shown. On a fresh f16-against-q4_k pair on the desktop — the ten-minute `csb384-8438.m4a` cut,
**CPU** backend, both transcribed 2026-08-16 — it reports 1,637 against 1,643 words, **59 raw
edits (3.60%): 35 substituted, 9 deleted, 15 inserted**, 48 (2.93%) with case and punctuation set
aside, all 114 segment boundaries identical, first divergence `Um` / `Um,` at 29.4 s;
`word-distance.ps1` gives the same 59 / 48 on the pair. The old guard would have refused that pair
outright, the counts differing by six, and passed the laptop's — which is the hole. Different
clip, different machine, so nothing below is re-derived from it; the 50 / 26 in the table stands
as measured by the alignment-free tool on the original pair.

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

**The transcript does not fit the number the v2 note carried.** The 169,291-byte `.txt` is
**50,892 tokens** under the 0.6B's chat template and **51,712** under the 9B's — the note's "about
40k tokens" (its line then read "Three hours of transcript is roughly 30k words, about 40k tokens")
was word-count arithmetic, and the measured figure is a quarter larger. Every "fits at 40k" line in
that note inherits this correction for this episode; **the note carries it since 2026-08-23** — its
decision 2 correction block scales the standing arithmetic and names the verdicts that tighten.

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

#### The engine on the product path — the vendored drop's first runs, 2026-08-23

Everything above ran from scratch directories and lab scripts; this is the first time the product
path ran. `scripts/vendor-llm-natives.ps1` vendored the **b10603** cpu and vulkan server sets into
`native/win-x64/llm/` (digests in `docs/NATIVE-BINARIES.md`), and
`Parakeet.Engine.LlamaServer`'s gated integration test drove `LlamaServerAnswerEngine` end to end
— locate the drop, start the child, `/health`, a grammar-constrained ask, stream, parse, validate
— on **cpu and on vulkan**, each passing, with test wall-clock of about 5 s and 7 s respectively
(the whole test: start, load, ask, kill — not a throughput figure). The model is
`ggml-org/Qwen3-0.6B-GGUF`'s `Qwen3-0.6B-Q8_0.gguf`, 804,753,632 bytes, sha256
`361cc68159042c36ebff7715dc5a2e4612153e88f3e9c9c234820849d6dc9e1d` — a different conversion from
the 639,446,688-byte Qwen-repo file the spike used, hashed against the hub's own LFS digest at
download. On Vulkan the engine's *default* child environment carries
`GGML_VK_DISABLE_BFLOAT16=1` — the knob is now product behaviour, not a lab flag — and the load
succeeded on the driver that was measured above to never become healthy without it.

Two observations from the first constrained run, both for decision 6's ledger:

- **The grammar's id guarantee held and its quote production did not.** Every id the 0.6B
  emitted was live (`S1`, `S2` — the two evidence windows), and every one resolved; the verbatim
  quote it was forced to produce was `«S2>»`, a fluent three-character invention the normalised
  substring check caught. FullCite's ~40 % quote failure on an 8B reads as roughly 100 % at
  0.6B, one run, one machine — the check is the mechanism working, not the model failing to
  matter.
- **The whole-answer abstain is first-token-only by construction, and the model that misses its
  chance smuggles the sentinel into prose.** Six of eight bullets read
  `- NOT_IN_TRANSCRIPT. «S1>» [S1]` — parsed as ordinary bullets, cited, resolving, and failing
  nothing but the quote check. A sentinel inside a bullet is not an abstention and nothing flags
  it yet; whether the thirty-question harness should score it as one is a question this run
  hands the register rather than answers.

#### CUDA executed for the first time — desktop, 2026-08-24, and the sm_120 scan is corroborated

Until this run the cuda-13.3 drop had been scanned and never run on any machine. Three claims,
each now measured on the RTX 5080 (driver 610.88), all from the b10603 pin:

- **The vendoring reproduces.** `vendor-llm-natives.ps1 -Backends cuda` hashed both archives to
  exactly the digests recorded in `docs/NATIVE-BINARIES.md`, and
  `scripts/vendor-cuda.ps1 -InspectOnly` against the b10603 `ggml-cuda.dll` (141,895,168 bytes)
  read the same architecture list the b10448 scan did: `sm_86`, `sm_89`, `sm_120`, `sm_121`
  cubins, PTX `compute_75/80/90`, 142 containers parsed, 0 rejected.
- **The kernels are native, not a JIT.** The gated integration test — the product path:
  `LlamaServerAnswerEngine` on `ComputeBackend.Cuda` over the vendored drop, load, `/health`,
  a grammar-constrained ask, stream, parse, validate — **passed in about 1 s of test time**
  (4.5 s of `dotnet test` wall-clock) on the machine's first-ever CUDA execution, where no
  driver JIT cache could have existed. The spike
  (`runs/20260824-032020-spike-cuda`, `Qwen3-0.6B-Q8_0.gguf`, 804,753,632 bytes, hash matching
  the pin) then made the same point mechanically: **two starts to `/health` in 1.12 s and
  1.03 s with `CUDA_CACHE_DISABLE=1`** — a PTX-only backend would have JIT'd on both. This is
  the corroboration the register's decision 1 had been waiting for since 2026-08-16.
- **The first VRAM figures on the desktop, backend named (cuda).** The 0.6B at `-c 40960`,
  f16 cache, all 29 layers offloaded: **server dedicated 5,415.7 MiB** (+126.0 MiB shared);
  adapter dedicated 1,367.2 MiB idle → 6,782.9 loaded → 6,786.9 after prefill and answer →
  **1,367.2 after the kill**, with `nvidia-smi` tracking beside it (1,357 → 6,772 → 1,357 MiB).
  The unload-is-a-kill claim now holds measured on both machines and both GPU backends.
  The 5.4 GiB is consistent with arithmetic — ~0.77 GiB of weights plus a 4.375 GiB f16 cache
  for 28 layers of 8 KV heads × 128 at 40,960 tokens plus compute — and it is why the test
  model is no sizing guide: its per-token KV cost is nearly four times the 9B's.
- Peak under the gated test, sampled by `nvidia-smi` at 1 s: 2,676 MiB used, 30 °C, 44.9 W —
  a 0.6B at `-c 4096` barely wakes the card.

What this run does not establish: any timing at depth (the spike ran the built-in stand-in
prompt — 119 tokens; its 8,043.8 tok/s prefill and 620.2 tok/s decode are a 0.6B at trivial
depth and mean nothing for the feature), and nothing about answer quality. One operational
note for the next run: b10603's default server verbosity does not emit ggml's
`model/KV/compute buffer size` lines — `-lv 5` restores them (probed separately: at `-c 4096`
the 0.6B reports CUDA0 model 604.15 MiB, KV 448.00 MiB, compute 30.01 MiB).

#### Decision 2's first file at depth — the 9B on the desktop tier, 2026-08-24, same sitting

`unsloth/Qwen3.5-9B-GGUF`'s `Qwen3.5-9B-Q8_0.gguf`, 9,527,502,048 bytes, sha256
`809626574d0cb43d4becfa56169980da2bb448f2299270f7be443cb89d0a6ae4` — the LFS `oid` read from
the hub's tree listing on 2026-08-24 per `docs/MODELS.md`'s procedure and verified against the
downloaded file. Run through `scripts/spike-llama-server.ps1` (`runs/20260824-033145-spike-cuda`),
release b10603, `-c 53248 -fa on`, f16 cache, `--fit off`, `--reasoning-format none`,
`CUDA_CACHE_DISABLE=1`, backend **cuda**. The prompt is this desktop's own f16 transcript,
produced the same sitting (`runs/csb384-f16`: `tdt-0.6b-v3-f16` on **cuda**, the silero neural
detector, 1,023 segments, RTF 0.0092) — **47,721 tokens under the 9B's template, not the
laptop's 51,712**: a different machine's encode and a different segmentation (neural detector
against the gate), so the two counts describe two documents, not a disagreement.

| | this desktop, CUDA, Q8_0 | the laptop, Vulkan, Q4_K_M (2026-08-16) |
|---|---|---|
| `/health`, first / second start | **3.61 / 3.60 s** | 3.64 / 2.54 s |
| Prefill, whole transcript | **7.93 s (6,017.7 tok/s, 47,721 tokens)** | 467.9 s (110.7 tok/s, 51,712 tokens) |
| Decode after it, 160 tokens | **75.4 tok/s** | 9.8 tok/s |
| Follow-up on the cached prefix | prompt 40.7 ms | prompt 566 ms |

One run per figure, different quantisations and prompts — a tier description, not a controlled
comparison. What it settles about the shape: **the whole-transcript path on the desktop tier is
an eight-second wait, not the laptop's eight minutes**, and the decode after a full-depth
prefill is interactive. The `--reasoning-budget 0` finding reproduces on CUDA: the 9B's 160
answer tokens were all visible thinking under `--reasoning-format none` — the budget does not
bind this template, and the grammar (which the product path applies and this spike does not)
remains the mechanism.

**The first per-buffer VRAM figures, from ggml's own allocation lines (`-lv 5`), backend cuda:**
CUDA0 model buffer **8,045.05 MiB** (plus 1,030.62 MiB CPU-mapped), KV buffer **1,664.00 MiB —
exactly the register's arithmetic** for 8 growing layers of 4 KV heads × 256 at 53,248 tokens
f16, recurrent-state buffer **201.00 MiB** (the 24 linear-attention layers, at the server's
default `n_slots = 4`), compute buffer **140.02 MiB** — an order of magnitude under the 1.5 GiB
allowance every fit line in the register assumed. Counters beside them: server dedicated
10,319.7 MiB loaded → 10,333.7 after the 47.7k prefill; adapter dedicated 1,381.4 idle →
11,677.4 loaded → 11,719.9 after prefill → **1,358.3 after the kill**, `nvidia-smi` tracking
within ~10 MiB throughout. With the model resident the card holds ~11.7 of 16,303 MiB — about
4.6 GiB of headroom, before anything else opens. Under the prefill, sampled at 1 s:
53 °C, 402.8 W, 98 % GPU utilisation at peak.

What none of this measures: answer quality (the CSB384 question set is still a template, so
recall and citation precision have no numbers), variance (every figure is one run), and the
panel path — this is the lab script; no question has gone through the Ask tab's input box on
any machine.

#### The same file over Vulkan on the same card — the win-cuda channel's current ask path, priced

The shipped channels carry the vulkan drop only, so until the cudart-13.3 decision an NVIDIA
card asks over Vulkan — and what that costs had been measured nowhere. Same sitting, same
machine, same model, same transcript, same flags (`runs/20260824-034354-spike-vulkan`; the
vulkan drop vendored on this machine first, digest reproducing), with
**`GGML_VK_DISABLE_BFLOAT16=1` in the child's environment because that is the engine's shipped
default** — and the model loads and runs under it on this driver, so the knob keeps costing
nothing observable on NVIDIA:

| Qwen3.5-9B Q8_0, `-c 53248`, 47,721 tokens | CUDA | Vulkan |
|---|---|---|
| `/health`, first / second start | 3.61 / 3.60 s | 5.63 / 4.04 s |
| Prefill, whole transcript | **7.93 s (6,017.7 tok/s)** | 19.05 s (2,504.6 tok/s) |
| Decode after it, 160 tokens | 75.4 tok/s | 69.2 tok/s |
| Follow-up on the cached prefix | prompt 40.7 ms | prompt 51.2 ms |
| Server dedicated, loaded | 10,319.7 MiB | 10,126.3 MiB |
| Adapter dedicated, loaded | 11,677.4 MiB | 11,422.5 MiB |
| Peak under prefill (1 s samples) | 53 °C, 402.8 W, 98 % | 50 °C, 307.0 W, 99 % |

So the +391 MB cudart question now has its price attached: **CUDA buys 2.40× on the prefill —
eleven seconds on a three-hour transcript, 19.1 s against 7.9 s — about 9 % on decode, and
about two seconds on the load**; the adapter returns to idle on the kill on both. One run per
figure, this card and driver only, and the number that matters for the decision is arguably the
Vulkan column standing alone: the fallback path is not a degraded mode, it is a 19-second
prefill and a 69 tok/s conversation.

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

**The pessimistic seed had a smaller version of the same failure, found 2026-08-22.** Seeded *on*
the absolute line (−55 dBFS), the gate opened at line plus margin, −47 dBFS, and quiet speech sat
under it until a sub-floor pause let the floor fall: a −45.6 dBFS tone from the first sample
produced nothing for its first ten seconds, and after a −26 dBFS passage the floor is held at the
ceiling and a −46 dBFS stretch that follows with no gap is never speech at all — with no warning in
either case, because the only sentence about the gate needed an empty transcript. Since 2026-08-22
the floor is seeded one margin below the line, so the gate opens at −55 dBFS and the first case is
segmented from its first frame; and the segmenter counts every frame above the line that ended
outside every segment, so the second case — which an energy gate cannot fix, because it cannot
tell quiet speech from a fan — is reported: "N s of audio above −55 dBFS sat below the
voice-activity gate and was not decoded", on the command line and in the window, when that is at
least a second and a tenth of what was segmented. **What this changes on real files is unmeasured.**
The opening threshold is lower for the first seconds of every file, until the floor climbs, so a
recording whose room tone sits between −55 and −47 dBFS now decodes some of it at the start; the
WER figures above were taken with the previous gate and have not been re-run. The expected effect
is confined to file starts and to quiet material after a loud passage, and it is written here
rather than assumed to be nothing. Tests: `QuietSpeechAtTheStartOfAFileIsSegmentedFromTheFirstFrame`,
`AudibleMaterialTheGateKeptOutIsCountedAndSaidToBeMaterial`.

### A publish that is green in CI and framework-dependent everywhere

`SelfContained` was set in `Directory.Build.props` under a condition on a property the project files
set. `props` is imported *before* the project body, so the condition was evaluated against an empty
value and did nothing: `dotnet publish -r win-x64` produced eleven files and no runtime. It is the
same shape as the dropped `--self-contained` flag the brief warns about, arrived at from the
opposite direction. The settings now live in `Directory.Build.targets`, which is imported after the
project body, and the comment there records how to check.

## Playing a recording — built 2026-08-22, video added 2026-08-23, driven against real files both days

The Ask tab plays the file it is showing, through **`SystemAudioPlayer` in
`src/Parakeet.App/Services/IAudioPlayer.cs`**. **Nothing in the suite runs a line of it**, and that
is structural rather than an oversight: it ends at WASAPI, which needs a Windows audio endpoint, and
CI has none. Every test drives `FakeAudioPlayer`, a clock with no sound that moves only when it is
told to.

So it was driven by hand instead, on the laptop (Ryzen AI 9 365, Radeon 880M, Windows 11), through a
scratch program outside the repository, against three files chosen to cover both reader branches —
`sample.m4a` (10:00.0036) and `two-hosts.mp3` (2:55:23.376) through Media Foundation, and an 8 s
16 kHz mono WAVE tone through the managed reader.

**What that established.** On all three: the device opens; `IsPlaying` goes true; the clock advances
at real time — 25 samples 100 ms apart returned 0.30 s to 2.88 s, so the render thread is pulling
buffers at the rate a device consuming audio would; pause stops the clock and holds it across
400 ms; a seek to 60 s lands on 60.00 s exactly and resumes from there; a seek *while playing* — the
path that pauses the render around the move — lands and carries on; a seek past the end clamps to
the duration; and play at the end starts the recording over. Nothing threw, and `Close` returned
cleanly each time.

**It found two defects, and neither was visible from the suite.** Both are in `Play`, both fixed the
same day, and both are the same shape: the fake player was more forgiving than the real one, so the
tests were green over them.

- **Play at the end only wrapped when the device had stopped by itself.** The wrap was inside the
  branch that creates an output, which is reached after a recording runs out — so the common path
  looked right. Drag the bar to the end or pause there and the device is *paused* rather than
  stopped: play resumed a reader with nothing left to read, made no sound, and left the button
  looking broken. The wrap is now on every play.
- **The at-the-end test was a coin toss at the boundary.** A seek to the end lands on a frame
  boundary rather than on the duration, and which side of it that falls on depends on the container:
  measured, the mp3 and the WAVE landed exactly on the duration and the m4a landed **0.006 ms**
  short, so `CurrentTime >= TotalTime` wrapped two of the three. It now allows one millisecond —
  48 frames at 48 kHz, inaudible, and 160 times the largest gap seen.

### Video, added 2026-08-23

The picture goes through **libmpv** (`Services/Mpv/MpvMediaPlayer`), and the same rule applies to it:
**nothing in the suite runs a line of it**, because it needs the vendored library and an audio
endpoint. It was driven by hand on the laptop against a 12 s H.264/AAC mp4 generated by ffmpeg, and
against `two-hosts.mp3` through the same player.

**What that established.** The library is found and `MediaPlayers.ForThisBuild()` picks the mpv
player; the mp4 opens with `HasVideo` true and a 12 s duration; the clock runs at real time;
**75 frames arrived in 2.5 s of 30 fps source**, so the software render path holds the full rate at
pane size; frames come out at **462×260** from a 640×360 source in a 640×260 box, so the aspect is
preserved and nothing is upscaled; a frame copied the way the window copies it had **106,455 of
120,120 pixels non-black and every alpha byte opaque**, which is a real picture and a working alpha
fill; a mis-sized destination was refused rather than overrun; a seek to 6.00 s landed at 6.00 s;
and play at the end wrapped. The mp3 through the same player reported no video track — cover art
does not become one, which is what `audio-display=no` is for — and seeked and wrapped identically.
The same mp4 forced onto the audio-only player played its sound with `HasVideo` false.

**What that leaves unproven, and it is a longer list than the audio one:**

- **That anybody has watched it.** Frames reach a buffer and the buffer has a picture in it. Nobody
  has confirmed that the moving image on screen is smooth, in step with the sound, right way up, or
  the right colours. Every one of those is a thing this probe cannot see.
- **Audio/video sync.** mpv owns it and mpv is good at it, which is the reason for choosing mpv and
  not a measurement. Nothing here has compared the two clocks.
- **Any codec but H.264/AAC in MP4.** HEVC, VP9, AV1, MKV, WebM and interlaced content are all
  expected to work — that is what linking FFmpeg buys — and none has been opened.
- **Performance at a real size on a real recording.** 640×360 upscaled from a synthetic ffmpeg
  test pattern is the easy case. A 4K source, a maximised window, and the CPU cost of the software
  render beside a transcription pass are all unmeasured, and the software path is the one that
  would show it first.
- **The 114 MB binary in an installer.** No packaging run has included it. What it does to the
  channel size, to the Velopack delta, and to the signing question is unknown.
- **The teardown.** `Dispose` sequences quit, event-thread join, render-thread stop, context free,
  handle destroy. It ran cleanly every time in the probe; it has not been run under a seek storm or
  a mid-load close.
- **The licence reading itself**, which is in `docs/LICENSING.md` and is a careful reading by the
  people writing the code rather than a professional opinion.

### Fetching a link, added 2026-08-23

`YtDlpMediaUrlFetcher` runs the vendored yt-dlp as a child process. **Nothing in the suite runs it**
— it needs the tools and a live site — so the window's link path is tested against a fake that
writes a real WAVE file, and the real one was driven by hand.

**What that established**, on the laptop against a Creative Commons YouTube link (Big Buck Bunny,
9:56): the tools are found; the link resolves with Deno wired in as the JS runtime; the audio comes
down as a **9 MB m4a in 3.6 seconds**; the title comes back and is what the queue row shows; the
downloaded file opens in `SystemAudioPlayer` at the correct duration; and progress arrives as 22
reports from "Reading the link" through the download percentages. Separately, the same URL streamed
through mpv with picture and seeked exactly.

**What is unproven:**

- **Any site but YouTube.** yt-dlp supports well over a thousand, and one has been tried. Sites with
  no m4a at all will fall through the format selector to whatever is best, which may be a container
  this pipeline then refuses — that path has not been walked.
- **A link that needs credentials, a region check, or an age gate.** None has been tried, and the
  error a person would see is unexamined.
- **A long download.** Ten minutes of audio arrived in under four seconds. Cancellation mid-download
  kills the process tree and deletes the directory; it has been reasoned about and not exercised
  against a real transfer.
- **The temporary directory's lifetime.** Downloads land under `%TEMP%/Uindosill/links` and nothing
  deletes them afterwards. That is deliberate for now — the Ask tab plays the file after the run,
  and a transcript's audio disappearing under it would be worse — but there is no sweep, so the
  directory grows until the user or Windows clears it. It should get the same once-per-process
  sweep the sidecar's staged files got.
- **Whether `--no-playlist` covers every shape of playlist link.** It covers the `list=` parameter;
  a channel or a mix URL has not been tried.
- **What the tools do to an installer.** About 115 MB on top of libmpv's 114 MB, and no packaging
  run has included either. The channel size, the Velopack delta and whether an antivirus objects to
  a bundled yt-dlp are all unknown.

**What is not this project's to establish.** yt-dlp downloads what its user asks it to; whether a
given download is permitted by a site's terms or by local copyright is the user's responsibility,
and nothing here checks it or could.

**What is still not established** about the audio path, and the list is shorter than it was:

- **That the sound is audible.** The clock advancing at real time says the endpoint accepted the
  stream and consumed it at the rate audio is consumed; it does not say anything came out of a
  speaker at a level a person heard, on the device they expected, undistorted. Nobody has written
  down that they heard it.
- **Whether a seek is heard as a click.** `Seek` pauses the render, moves the reader and resumes, so
  that a move cannot land between the render thread's read of the position and its read of the
  bytes. Whether that is audible, and whether it is needed at all, is untested — the alternative is
  to trust the reader's own deferred reposition, which is a claim about NAudio's internals nobody
  here has read.
- **Playing to the natural end.** Every run above was cut short deliberately; no file has been left
  to run out, so the path where WASAPI stops itself and the next play rebuilds the device has been
  reasoned about rather than watched. On a 3 h file that is a 3 h test.
- **A recording playing while another file is being transcribed.** Both readers open with
  `FileShare.Read`, so neither should take the file from the other, and the cost of a decode running
  beside a transcription pass is unmeasured. Playing *the file currently being transcribed* is
  reachable from the tab and has not been tried.
- **Whether 200 ms of shared-mode latency is the right figure.** Chosen to be comfortably larger
  than a UI stall and smaller than a seek that feels late. Nothing was measured to pick it.
- **The 100 ms transport tick**, which scans the transcript for the line holding the current
  position on every tick that finds something moved. On a three-hour transcript that is 1,488
  comparisons ten times a second; it is a linear scan and no profile has been taken of it. Since
  2026-08-23 it also walks the active line's own words for the one being said, which is a
  sentence's worth of comparisons on top of that scan and is unprofiled for the same reason.
- **Every container other than the three above**, and any machine other than this laptop.

**What would settle the first of those:** play something in the built application with the volume
up and write down that it was heard. That is the whole check, and until it is done no release note
may say the tab plays audio — only that it drives an audio device.

**One thing here is measured rather than unproven.** The taro ramp's agreement with matcha — every
step within 0.0014 of lightness and 0.0010 of chroma, and 7.48:1 and 5.24:1 on white for taro-700
and taro-600 — was computed from the shipped hex values by the same conversion used on the matcha
ramp, and the round trip reproduces the design sheet's own coordinates. See `docs/PHASES.md`
§ *Built 2026-08-22 — the Ask tab*.

### The word being spoken, added 2026-08-23

The mark on the one word being said inside the line being played is the one part of this tab that
**is** under test end to end, and it is worth saying why: it is a position turned into a place in
the text and drawn by a converter, so there is no device anywhere in it, and the headless window
runs both halves for real. Eleven tests — six driving the view model against a fake clock, three
reading the runs a live window's `TextBlock` actually holds, including the pastel yellow read back
off the rendered run so a brush that failed to resolve cannot pass, and two driving a sixty-line
transcript through a live scroller for the pane that follows the playhead. Deliberately breaking the
one assignment that sets the word fails eight of the nine word tests; the ninth is the one asserting
that a transcript *without* word timings marks nothing, and it correctly still passes. The follow
rule was broken both ways too — never following fails both scroll tests, following unconditionally
fails the one that scrolls away.

**What none of that establishes:**

- **That anybody has watched it move.** Every test advances a fake clock by whole and half seconds
  and asks what is marked. Nobody has watched the mark track a voice at the window's 100 ms tick,
  on a real recording, with the sound audible — and screen capture is not available in this
  session, so nobody could here. Until someone has, a release note may say the transcript marks the
  word the timings name, not that the mark keeps up with the speech.
- **Whether the engine's word timings are good enough to be pointed at.** They are the same
  timings `vtt-words` has always written; § *What that costs the word-timed WebVTT output* records
  what quantisation moves them by, and this project has never checked that a mark drawn from them
  lands on the word a listener hears. Pointing at a word is a stricter use of a timestamp than
  writing it into a subtitle file, and nothing has been re-measured for it.
- **A word shorter than the tick.** At 10 Hz a word under 100 ms can be stepped straight over
  without ever being drawn. How often that happens in real speech is unknown, and the tick has not
  been raised to find out — that would cost a transcript scan ten more times a second, which the
  bullet above about the transport tick is already unprofiled about.
- **That the pane follows the playhead in a way anybody wants.** The rule — follow while the line
  just left is still on screen — is asserted against a scroller in a headless window, where the
  offsets are numbers. Whether it feels right to a person reading along, whether the jump as a line
  leaves the pane is jarring at the bottom edge, and whether a reader who scrolls back expects it to
  resume, are all judgements nobody has made against the built application.
- **Whether the located-not-assumed path ever fires on real data.** Each word is found in the
  segment's text at a word boundary or skipped; no real transcript has been searched for a segment
  whose words do not spell its text, so the fallback is reasoned about and exercised only against a
  fixture. The same is true of a segment carrying text and no words at all — see § *The cue
  builder's no-word path is reachable, and has never been observed*, which is the same gap seen
  from the subtitle side.

- **CUDA-first `auto` for the translator rests on eight sentences, six fixture lines and one
  machine.** The faithfulness that justifies it is the 2026-08-21 study's 240 of 240; the 2026-08-23
  additions are a timing (0.142 s/sentence vs WebGPU's 0.189, RTX 5080, onnxruntime-gpu 1.29.0) and
  the six-sentence parity fixture passing string-identically. No gate-corpus run has been made on
  this exact stack, no second CUDA machine exists to try it, and the shipped bundle cannot reach the
  CUDA branch at all — so the reorder is exercised by exactly one working copy in the world.
- **The IO-binding crash is recorded, not root-caused.** `cudaErrorIllegalAddress` in `/Mul` on the
  first decode step with binding on, ORT 1.29.0 + optimum 2.1.0 + torch 2.13.0+cu130, reproduced
  twice, never chased into which of the three is at fault or whether a different version pairing
  survives. The docstring now warns rather than promises; nothing more is claimed.

- **The English pane on the Ask tab has not been read against a real translated recording.** Every
  test of it drives a fixture whose "translation" is two hand-written English segments. That the
  pane switches, searches, highlights by line and marks no word is asserted headlessly; that reading
  along in English while a Spanish recording plays is a thing anybody wants, and that the line-level
  highlight is enough to follow without the word mark, are judgements nobody has made.
- **The splitter fix is verified against a simulated drag, not a hand on a mouse.** The test presses,
  moves in six steps with the clock ticking between them, and releases. Whether the drag now *feels*
  smooth while a video plays — and whether publishing the picture size only on release makes the
  video visibly stretch during the gesture, which it may — has not been looked at.

## Putting a transcript back inside a recording — built 2026-08-23

`docs/PHASES.md` has what the feature is and every container rule it follows, each one measured
against FFmpeg 9.0.1 rather than reasoned about. What belongs here is the gap between what was
measured and what would be needed to claim it works.

**What is measured.** The container rules are pure logic with 19 tests over them, and the argument
list they produce was driven against the vendored binary over eight input-and-format routes — video
and audio, SRT and both WebVTTs, and the ASF fallback — all exiting 0, with the word-level routes
returning 60 of 60 inline timestamps and the others none. A renamed speaker reaching the rendered
transcript is tested end to end. The yt-dlp comparison is a real download of a real video, decoded
through this application's own reader.

**What none of that establishes:**

- **Nothing has been muxed from the window by a person.** The button, the format choice and every
  sentence it shows are tested against a fake muxer; the real one is tested by driving it directly.
  The two halves have never been run as one, and screen capture is unavailable in this session by
  standing rule, so nobody has watched a file appear.
- **No output has been opened in another player.** The whole argument for the feature is that a file
  with the words inside it plays anywhere. What is checked is that FFmpeg reads back what FFmpeg
  wrote, and that the codec identifiers are the ones FFmpeg maps. Whether VLC, Windows' Movies & TV,
  QuickTime or a phone shows these subtitle tracks — and whether any of them renders word-level
  WebVTT cues at all rather than ignoring the inline timestamps — is unchecked. **The word-by-word
  timing may well be invisible everywhere except this application**, which would make the Matroska
  route's whole justification theoretical.
- **Nothing has been muxed at a real size.** Every measurement above is on a 60-second clip or a
  9.6 MB podcast. A remux rewrites the entire file: a three-hour video is gigabytes of copying, and
  what that costs in time, what it does to a disk that is nearly full, and whether the staged write
  and rename behave under that load are all unmeasured. There is no progress reporting inside the
  mux at all — the window says "Adding the transcript to the recording" and then nothing until it
  finishes.
- **A cancel mid-mux has never been exercised.** The code kills the child, deletes the staging file
  and leaves the original alone, and none of that has been run.
- **The 23 ms cue drift on an MP3 into Matroska is recorded and not explained.** A cue that starts at
  00:00:37.250 comes back at 00:00:37.273. It does not happen on the video route, so it is presumed
  to be MP3 encoder delay against Matroska's timecode scale; it has not been chased, and whether it
  accumulates over three hours is unknown.
- **The remux yt-dlp now performs is unmeasured on a long download.** Seconds on a 9.6 MB file;
  nothing has been timed on a three-hour one, and it is now on the path of every link fetch.
- **`FfmpegSubtitleMuxer` has no test at all**, by construction: it needs the vendored binary and
  real media. What it does was driven by hand on 2026-08-23. Its error path — reporting ffmpeg's last
  stderr line rather than a wall of text — has been exercised only through the fake.

## The Export and Settings tabs and the About window, 2026-08-23 — measured headlessly, not looked at

The Transcribe tab's options column became two tabs and the Licences tab became a modal window;
`docs/PHASES.md` has what moved where. Every figure quoted for it is measured through the headless
host, and **nothing in it has been seen on a screen**, because screen capture is unavailable in this
session by standing rule. Two new pages and a new window is the largest visual change since the Ask
tab, and the same gap applies to it.

**What is measured.** That each new page draws the controls it claims, by walking the visual tree
rather than the name scope — `FindControl` answers for every page whether or not it is drawn, which
is gotcha 31 and was itself measured (`ctor-scope=found after-show=found inVisualTree=False`). That
each moved control still writes through to the view model. That the Transcribe tab draws none of
them. That every switcher pill selects the `TabItem` whose header it names. That the six pills clear
the wordmark and the window buttons at 1080 and at `MinWidth`, from the pills' own arranged
positions — the 4px overlap at the old 820 was found that way, not by eye.

**Later the same day the two extra passes moved back to the Transcribe tab** — translation first,
speakers last, the English box renamed 'Translate to English'; `docs/PHASES.md` has the reasoning —
and the assertions moved with them: the Transcribe tab now draws exactly those two checkboxes, with
their order pinned from drawn geometry, and the Settings page keeps the cut and the way to About.
The rearranged Transcribe tab, unlike the pages this entry opened with, **was looked at on a
screen**: the built application was launched on the laptop and the window captured, which retires
none of the judgement gaps below for the two reading pages and the About window.

**What is not.**

- **The two pages and the window have not been looked at.** `docs/PHASES.md` says both pages are
  drawn as reading pages and that the About window "wears the main window's chrome exactly". A
  headless host has no compositor: what backs those sentences is that the same classes and the same
  margins are in the markup, not a rendering. Whether the Settings page reads as one page or as four
  stacked strips, and whether the About window's identity block is the right size beside its
  switcher, are judgements nobody has made.
- **The About window has never been opened.** `MainWindow.OnShowAbout` calls `ShowDialog`, and no
  test presses the button or runs that handler. The button is wired by name in the constructor and
  the lookup is null-tolerant, so renaming it in the markup yields a live-looking button that does
  nothing — the failure this window's own comments say it refuses to ship. What is asserted is that
  the button is drawn on the Settings page and that the window builds and binds when constructed
  directly. **The only in-app route to the notice package now runs through that unexercised click**,
  and `NOTICE.md`, `LICENSE` and `docs/LICENSING.md` all rest on the notice being present in the
  application.
- **Nothing has been copied to a clipboard.** `AboutWindow.OnCopySystemReport` builds a
  `DataTransfer` and calls `IClipboard.SetDataAsync`; the test asserts the button is drawn and that
  every line the System pane draws appears in `SystemReport`. The call itself, the Avalonia 12
  ownership contract around the transfer, and the "Copied." confirmation becoming visible are
  unexercised. All clipboard failures are swallowed by design, so a broken call is silent.
- **Escape has not been pressed.** `IsCancel="True"` on the dialog's Close button is asserted as a
  property, not as a keystroke; the headless host routes no accelerator here.
- **The About window's square corner and shadow are unmeasured.** It makes the same two DWM calls
  the main window makes on open, and nothing reads either answer. The main window's `S_OK` and
  read-back were confirmed on Windows 11; a modal owned window has not been checked, and the corner
  preference is not documented to be inherited.
- **The window has not been dragged to 920.** `MinWidth` is enforced by the toolkit and the pill
  geometry is measured at that width headlessly, but whether a 920-unit window is usable — whether
  the Transcribe tab's 344px queue column and its transcript still read side by side there — has not
  been looked at.

## The Ask tab rebuilt 2026-08-23 — measured headlessly, not looked at

The cue height defect, the column's new order, the draggable picture edge, the seek handle and the
editable speaker names all landed together; `docs/PHASES.md` has what each one is. What belongs here
is the same gap as the section below it — every figure quoted is measured, and **nothing in it has
been seen on a screen**, because screen capture is unavailable in this session by standing rule.

**What is measured.** The cue defect and its fix are both measured through the headless window
rather than argued: one line at every window width from 820 to 1400 before, two at 1400 and twelve
at 820 after, reflowing both ways; a speaker label arranged 12.00 tall around text that lays out at
14.64 before, 15.00 after. The two hypotheses were crossed against each other in a probe — the
timestamp beside the words, and the surrounding `ScrollViewer`, are both irrelevant; the pinned
`Height="30"` on the base `Button` style is the whole of it. Three tests fail without the fix.

**What none of that establishes:**

- **That anybody has read a wrapped cue.** A transcript that has always drawn one line per segment
  now draws as many as it needs, which changes the density of the whole pane and every scroll extent
  in it. Whether a fifty-line cue at the window's minimum width is something a person can read, and
  whether the line the playhead is on can still be found among them, are judgements nobody has made
  against the built application. **At 820 units the words column measures 28 units wide** — the
  recordings list and the chat panel are fixed at 228 and 330 — and a 221-character segment lays out
  on **fifty lines** there. That is arithmetic, not a defect report; nobody has looked at it, and
  whether the fixed columns should yield at that width has not been decided.

  **That figure is superseded and has not been re-measured.** `MinWidth` went from 820 to 920 on
  2026-08-23, when a sixth switcher pill stopped fitting in the headerbar, so 820 is a width the
  window can no longer be dragged to and the fifty-line case is no longer reachable. The direction
  is the safe one — the real worst case is now a hundred units wider than the one recorded — but the
  sentence above measures a width the product does not have, which is the failure this file exists
  to catch, so it stays marked rather than quietly re-derived. Re-measuring it needs a job with a
  transcript loaded at the new minimum; that has not been done.
- **That the splitter has ever been dragged by a person.** It is driven in a test through one
  pointer press, move and release, and the picture grows and the transcript gives up exactly what
  the picture takes. Whether the grip is findable, whether the video's aspect at an extreme split
  looks like a bug, and whether the reading row's 140-unit floor is the right one, are all unlooked
  at. The floor and the picture's 120-unit minimum are **chosen, not derived**; what is checked is
  only that the three rows still add up inside the smallest window this application allows — which
  that test now reads off `MinWidth` and `MinHeight` rather than repeating them, so raising either
  cannot leave it checking a size the window has stopped having.
- **The seek handle's convention is unchecked.** It is inset by its own width rather than centred on
  the playhead, so at 0% and 100% it disagrees with the bar's fill edge, and with where a press
  lands, by up to its own radius. Both conventions are defensible and the design sheet has not been
  read — it is on the Drive, not in this repository. Its diameter here is 13; the only other
  diameter recorded anywhere for it is 10.
- **`PlacePuck` writes a `Margin`, which invalidates the transport's measure ten times a second
  while a recording plays.** Bounded — `AskViewModel.Tick` raises nothing when the clock, the play
  state and the picture are all unchanged, so a paused transport costs nothing — and never profiled.
  A `Canvas.Left` would invalidate arrange only. Recorded rather than acted on.
- **That a rename is something anybody finds.** The strip is four fields that obviously take typing,
  which was chosen over a chip that becomes a field when clicked precisely because the second says
  nothing about being editable. That reasoning has not been tested on a person.
- **A renamed speaker reaches nothing outside the window, and this is the sharpest edge on the
  feature.** `TranscriptWriter.WriteAsync` runs *before* `JobViewModel.Complete`, and the job does
  not retain the `TranscriptDocument` — it renders the transcript and the line collections and drops
  it. So there is no object in the window a rename could be re-exported from, and going further
  needs document retention, a re-export command, and a decision about `OverwritePolicy`, whose
  default would write `a (2).srt` rather than replacing `a.srt`. A name is also **not** carried
  across a re-run, deliberately: a second pass need not give `Speaker 1` to the same person. The
  window says all of this in a notice, and **whether a notice is enough — against somebody who names
  four voices, closes the window and expects to find them again — is the open question**, not a
  settled trade.
- **That the two panes of a translated transcript really do rename together on screen.** They share
  one voice object per speaker and a test asserts both panes' lines report the new name, but the
  Transcribe tab's chips have not been watched changing while the Ask tab's strip is typed into.

**Superseded again later on 2026-08-23, the same way as the 820 figure above.** The recordings
list left the page for a drawer behind a button — the tab's columns are now `*,330` — the
transport moved into the picture's row, and the picture and reading rows are stars opening at an
even split, with floors of 210 (picture and transport together) and 140 in place of the 120-unit
picture minimum this section reasons about. The 28-unit words column is therefore doubly
unreachable, the new floors are as chosen-not-derived as the ones they replace, and none of the
new arrangement — the drawer floating over the words, the even split at any real window size —
has been seen on a screen either; what holds it is the same headless suite, with the geometry
assertions rewritten to the new rows.

## The chat panel — built 2026-08-24, measured headlessly, a human has asked it nothing

The Ask tab's covered third went live: retrieval over the open recording's document, a streamed
answer, parsed bullets with citation chips that seek, the abstain sentence, the provenance line,
decision 5's copy form. What stands behind the claims:

- **Thirteen view-model and window tests drive the seam against the fakes** — the canned answer
  engine and the fake player — and they are the whole of what "the panel works" means today:
  streaming replaced by bullets, a resolved chip seeking and playing, the quote arriving in
  English on a translated recording, R9 unloading the transcription model on the first question
  and the language model on a transcription starting, empty retrieval abstaining, the cover
  naming its missing prerequisite, Enter asking, a suggestion chip asking its question, a dead
  engine dropped so the next question starts fresh, and copy leading with the marker line.
- **The real engine behind the same seam ran under its gated integration test** (cpu and vulkan,
  the 0.6B, the section above) — but never through this panel. No question has gone from this
  input box through a real `llama-server` on any machine.
- **The plan's exit criterion is therefore open**: a human asks three questions of a real
  transcript on Windows and follows a citation into the audio. The path for that run: put a
  GGUF in the models folder (the About window names it), open the Ask tab, ask. Nothing about
  it has been observed, including what a 9B's load and prefill waits feel like against the
  panel's status line, and whether the streamed raw text reads as provisional or as an answer.
- **The window's real clipboard is unexercised**: the headless tests replace the copy delegate,
  so `Clipboard.SetDataAsync` on a real desktop has never run. The residency handoff when a
  transcription starts mid-chat is fire-and-forget and best-effort by design; whether the kill
  always lands before the ASR load peaks is unmeasured, and on a 16 GB card with a 9 GB model
  resident the overlap instant is exactly the arithmetic decision 4 warns about.
- Streaming was asserted at completion granularity — the fake completes in microseconds — so
  "the answer visibly streams" is a design intention the real model run has to show.

### The shipped ask tier — packaged 2026-08-24, and three things no release has observed

`package-windows.ps1` now vendors the vulkan `llama-server` drop into both channels, prunes what
a channel does not promise, and refuses a package without `llama-server.exe` and the MIT text —
all of which has run exactly nowhere: the last release (rc.3) predates it, so every size in that
record — 485.4 MB default installer, 60.7 MB CLI zip — is a pre-ask-tier figure, and what the
~94 MB unpacked drop does to the installer, the delta packages and the CLI zip (which inherits
whatever is vendored) is the next release's first observation. Second: the channels ship the
vulkan drop alone, on the reading that `GGML_BACKEND_DL` builds fall back to the bundled per-ISA
CPU variants when Vulkan cannot initialise — structurally sound, since the vulkan zip is the cpu
zip plus one DLL, and **run on no machine with a broken or absent Vulkan driver**. Third: the
win-cuda channel's ask tier is the same vulkan drop, because the LLM's cudart-13.3 beside the
ASR's cudart-12.8 is the maintainer's open decision; an NVIDIA card asks over Vulkan in that
channel until it is taken. **What that costs is measured as of 2026-08-24** — on the desktop's
5080, CUDA buys 2.40× on the whole-transcript prefill (7.9 s against 19.1 s) and about 9 % on
decode; § *The same file over Vulkan on the same card* below the engine section has the table.
What remains unobserved is the release itself.

## The four window defects fixed 2026-08-23 — tested headlessly, not looked at

Four things the maintainer found by running the built application were fixed the same day: the
transcript pane not filling during a decode, the row sitting at a full bar under "Labelling
speakers", the Models tab not accounting for weights the catalogue no longer claims, and Start
refusing until a model had been loaded from another tab. `docs/PHASES.md` has what each turned out
to be and why each change is the shape it is. What belongs here is the gap between what was tested
and what was claimed.

**Two engine timings are real, on this desktop, through the product path.** `csb384-8438.m4a`
diarised on WebGPU in **25.3 s for 10 min of audio**, exit 0; the same file through
`transcribe --speakers --speaker-count 2` — the window's exact path, fold included — finished in
**37.7 s**, exit 0. Those are what establish that "stuck on labelling speakers" was a reporting
defect rather than a hang, and they are single runs quoted for that purpose rather than benchmarks:
no repeat, no variance, and the ASR half ran on CUDA because that is what the command line picked.

**Nobody has looked at any of it.** The twenty-one new tests are headless — view models and Avalonia
controls under the test host — so what is established is that `JobViewModel.Lines` gains rows while
a file is decoding, that a second pass clears the bar and names its two halves, that the sideloaded
section lists and deletes, that opening the Models tab re-reads the folder, that Start refuses
only when no weights are installed, and that a finished row reports the backend behind each opt-in
pass — the labels' and the English's, on separate lines. **What is not established is that any of
it appears on screen.**
Screen capture is not available in this session, so no one has watched the pane fill, seen the
labelling bar move, seen the sideloaded section draw, or read either backend off a finished row. That distinction has caught this project
before: the two contrast defects below, and the window-frame bits that no headless render could
show, were both invisible to exactly this kind of test.

### The staging stage was decode-then-resample and is now both at once — measured 2026-08-23

**Split before it was changed, because a 35-second stage does not say which part to attack.** On the
desktop over the 157-minute podcast (44.1 kHz AAC, 145 MB), timed by reading the source and
discarding to get the decode alone, then the real path, then the write:

| stage | seconds | share | rate |
| --- | --- | --- | --- |
| decode | **19.76** | 59.4% | 478x realtime |
| resample | **13.09** | 39.3% | 722x realtime |
| WAV write (577 MB) | 0.42 | 1.3% | 1383 MB/s |
| reading it back | 0.10 | — | — |

Two things that decided the change: the resampler's 722x is exactly what
§ *The resampler was most of a diarisation* measured in isolation, so the filter was not the
problem; and the WAV round trip is **1.3%**, so the 577 MB handoff file — which looks like the
obvious waste — is not worth touching.

**After overlapping the two halves**, the same file through `uindosill diarise` on WebGPU:
**52.7 s becomes 39.2 s**, 179x to 241x realtime. The saving is 13.5 s against the resample's
measured 13.09 s, which is what full overlap predicts. Peak host CPU rose from 1.55 to 2.73 cores.

**The output did not move, checked on the real recording rather than a fixture.** The RTTM for the
157-minute podcast is byte-identical before and after — same MD5, 1,001 turns — and two CI tests
pin the property without a model: a staged 44.1 kHz WAV against one unbroken pass of the resampler,
and cancellation reaching both halves. Dropping one sample per block fails both.

**What is not established:**

- **Single runs.** Every figure above is one run. The split harness was run once, and the
  before/after pair is one run each. Nothing here has a variance.
- **One file, one container, one rate.** 44.1 kHz AAC in an m4a on this desktop's NVMe. A 48 kHz
  source takes the resampler's one-phase path and a different share; a 16 kHz source takes the
  identity path and has no resample to hide at all, which means this change is worth nothing on
  AMI — where every published DER in this repository was measured.
- **Whether the decode is the floor.** 478x realtime is what Media Foundation gave on this AAC
  stream on this machine; nothing has been tried against it. **It is not the downmix**, which was
  the obvious suspect and was measured on 2026-08-23 rather than assumed: the fold-to-mono loop
  inside `MediaFoundationAudioSource` runs at **0.77 ns per frame**, which is **0.32 s** over this
  157-minute file against the decoder's 19.48 s — **1.6%**, and less than a fifth of the decode's own
  run-to-run spread (19.48 / 21.18 / 21.21 s over three runs). Taking the per-sample `IsFinite`
  guard away costs the file 0.11 s and unrolling the stereo case 0.09 s, so the whole loop is worth
  a tenth of a second and is **not worth touching**. What is left in the 19.48 s is Media
  Foundation's AAC decoder, and nothing here has been past it.
- **That negative result took two attempts, and the first one was wrong by 7x** — worth recording
  because the method is the trap rather than the code. Timing a decode-only run and subtracting it
  from a decode-plus-downmix run put the loop at 2.32 s, and the same subtraction said removing the
  `IsFinite` guard made it *slower*, which is impossible. Two twenty-second measurements carrying a
  second of variance each cannot be subtracted to find a difference of tenths. The figures above
  come from running the loop alone over a megabyte of real decoded audio, forty times, best-of.
- **The queue depth is eight and nothing measured it.** Chosen so neither half waits on a jitter in
  the other while the bound stays trivial beside the output buffer. Two would probably do; sixty-four
  would probably also do.
- **Memory is unchanged and still large.** The staging holds the whole 16 kHz recording as a
  `List<float>` — 577 MB for this file, 690 MB for three hours — before writing it. The pipeline adds
  half a megabyte of queue to that and does not address it.

### Nearly all of the diariser's CPU load was two thread pools spinning — measured and fixed 2026-08-23

**The maintainer's monitor showed CPU and GPU both past 80 % during the labelling pass and asked
whether that was normal.** It was not: on the desktop (9950X, 32 logical, RTX 5080) the chunk loop
held about 23 of 32 cores while doing roughly half a core of arithmetic. The rest was two
independent thread pools busy-waiting on a GPU.

**Where it came from.** Only the Sortformer ONNX graph runs on WebGPU. Around it sit ONNX Runtime's
own intra-op pool at 12 threads and PyTorch's at 16 — one per physical core, which nothing in the
sidecar ever set — and **both spin while they wait**. The loop is one `sess.run` per chunk with a
small torch state update between them, so for the 95 % of each iteration spent inside the graph
call, 28 threads were spinning.

**The split, timed at the three call sites inside the loop** (WebGPU, 10 min of synthetic audio,
`run_mel` only so the featurizer could not dilute it): `sess.run` **0.95 s** of a 0.99 s loop,
`streaming_update_async` **0.03 s**, `apply_mask_to_preds` **0.00 s**. Sixteen torch threads were
producing thirty milliseconds of arithmetic.

**What each lever is worth**, same conditions, wall against CPU seconds:

| | wall | CPU s | cores of 32 |
| --- | --- | --- | --- |
| spinning on, torch 16 — what shipped | 0.96 s | 23.4 | 24.4 |
| spinning off, torch 16 | 1.02 s | 15.6 | 15.4 |
| spinning off, torch 1 | 1.01 s | 0.5 | 0.5 |

The torch sweep at 16/8/4/2/1 gives walls of 0.99 / 0.98 / 0.99 / 1.01 / 1.01 s — flat — against
CPU of 14.95 / 7.25 / 3.41 / 1.45 / 0.52 s. The spinning rows are three runs each; the sweep rows
one apiece. The 6 % wall difference in the first two rows is one outlier: the individual walls were
0.84 / 1.01 / 1.03 against 1.02 / 1.00 / 1.02.

**The featurizer is the opposite case, which is why the fix is scoped rather than global.** Over
30 minutes of audio `feats.py` runs in **0.19 s at sixteen threads and 0.94 s at one** — a real 5x,
bit-identical at every setting. Cutting torch's pool for the whole pass would have paid for the
loop's spin by making the featurizer five times slower, so the narrowing is inside `run_mel` and is
restored in a `finally` — checked by hand, including when the loop raises.

**On the whole `run_wav` path, before against after**, 10 minutes of audio, three runs each:
**1.09 s wall / 24.9 CPU s / 22.9 cores** becomes **1.08 s wall / 4.5 CPU s / 4.2 cores**. Five and
a half times less CPU for the same wall clock.

**On the recording that raised the question** — the 157-minute podcast, through `uindosill
diarise` on WebGPU, sampled at 400 ms — the GPU sits at 85–90 % for about 15 s while the sidecar
uses **0.31 to 0.55 cores**. The featurizer's one second beforehand still takes its 16. What is left
of the pass is what it always was and is now plainly the largest part of it: **35 seconds of
single-threaded decode and resample**, 1.4 cores, GPU idle, before the sidecar is handed anything.

**It changes nothing the model computes, and that was checked rather than argued.** Spinning is how
a thread waits and torch's thread count does not reach the graph, but this project's own record has
DirectML scoring 53 % while looking healthy, so: the committed parity fixture passes on both
providers after the change — **CPU maxAbsDiff 0.0, WebGPU 1.0729e-06, 0 % decision flips** — and the
baseline measured on this machine immediately before the change is **the same 1.0729e-06, identical
across three runs**. Separately, a direct comparison of the probabilities with spinning on against
off was bit-equal: 0 of 30,000 cells differing, argmax agreeing on all 7,500 frames.

*(The parity module's own note records WebGPU at 2.7e-06. That is another machine's figure and
nothing here moved it; the desktop reports 1.0729e-06 deterministically, before and after.)*

**What is not established:**

- **The CPU provider is untouched, deliberately, and unmeasured.** Spinning stays on there because
  those threads are the ones doing the arithmetic rather than waiting on somebody else's, and every
  published figure in this repository was produced on that path. What taking their spin away would
  cost has not been measured. Torch's narrowing *does* apply on the CPU provider — it is a property
  of the loop rather than of the backend — and the parity fixture passing at 0.0 there is the only
  evidence that it is harmless.
- **CUDA and DirectML get the same change and neither was re-run.** Both take the non-CPU branch by
  provider name. Nothing has measured what spinning was worth to either.
- **One machine.** 9950X and RTX 5080, ONNX Runtime 1.27.0, `onnxruntime-webgpu`. A part with fewer
  cores has a smaller pool to waste and a proportionally smaller prize; a slower single thread makes
  the one torch thread in the loop matter more than 0.03 s.
- **Synthetic audio for every timing above.** Fair for this question — the chunk shapes are fixed,
  so what the graph costs does not depend on what was said — but it is not the podcast, and only the
  157-minute run above is real audio.
- **No AMI re-score.** The probabilities are bit-equal on the fixture and on a direct comparison, so
  the labels cannot move; that is an argument from the outputs rather than a run of the gate.

### The resampler was most of a diarisation, and the figures that hid it — measured 2026-08-23

**Every "the diariser runs at Nx realtime" sentence this project has written was timing two things
and naming one.** The labelling pass decodes and resamples the whole recording to 16 kHz before the
sidecar is handed anything, and `Resampler` evaluated its Blackman-windowed sinc per tap per output
sample — a sine and two cosines each time, about 9.3 million transcendental calls per second of
audio at 48 kHz. Benchmarked alone on the desktop it ran at **25.7x realtime at 48 kHz and 25.6x at
44.1 kHz**, which is the same order as the whole pass was reported at.

**Tabulating the kernel by phase moved it to 722x (48 kHz) and 784x (44.1 kHz)** — 28 to 30 times
faster — and the effect on the product path, on `csb384-8438.m4a`, 10 minutes, through
`uindosill diarise`:

| | before | after |
|---|---|---|
| reported pass | 25.3 s — 24x realtime | **3.3 s — 183x realtime** |
| wall clock | 28.3 s | **6.7 s** |
| turns / speakers | 60 / 2 | 60 / 2 |

So roughly nine tenths of what was being called the diariser was the resampler. **The model's own
speed had never been measured apart from it**, and two things follow that are worth stating rather
than leaving to be re-derived:

- **It hid the GPU.** CPU against WebGPU was 37.0 s to 25.3 s — a ratio of 1.5x, which reads as "the
  provider barely matters on this model". With the shared bottleneck gone it is **10.6 s (57x) to
  3.2 s (187x)**, a 3.3x speedup, which is an ordinary GPU result. The earlier ratio was an artefact
  of both runs paying the same single-threaded CPU filter.
- **It makes the catalogue's own sentence true again.** `models.json` tells users the speaker pass
  "roughly doubles how long a file takes". Against the ASR pass's RTF of 0.006 (~167x realtime) that
  was about 8x before this change and is about double now.

**What is not established.** These are single runs on one machine and one file, quoted to show a
change of order rather than as benchmarks — no repeats, no variance, and the ASR half of the
comparison ran on CUDA because that is what the command line picks. The DER is untouched and
unre-scored, and does not need to be: AMI is 16 kHz, so the resampler is bypassed on the only corpus
this project scores against, which is the same reason recorded above for why nothing measured has
ever been through this code. At 48 kHz the new filter is **bit-identical** to the old one and the
question does not arise; at 44.1 kHz the worst single sample moved by **5.96e-08** and at 22.05 kHz
by **1.19e-07**, which is half an ulp and one ulp of a float at unit magnitude. No listening test,
and no comparison of speaker turns on a 44.1 kHz file.

**One number in the labelling bar is a judgement, not a measurement.** The staging half and the
sidecar half report separately, each sweeping 0–100%, because there is no measured ratio between
them to combine them with. On the one file timed here the sidecar half was 25.3 s and the staging
half was not separately timed at all. A single combined bar would need a weight, and there is no
evidence for one.

## The Ask tab's lines are sentences — measured on one broadcast documentary, 2026-08-23

The Ask tab drew one line per segment until 2026-08-23 and draws one per sentence now, where a
segment's word timings can tell its sentences apart — `SentenceSplitter`, applied to the lines and
never to the document; `docs/PHASES.md` § *Built 2026-08-23 — the Ask tab reads by the sentence* has
what and why. Everything here was measured on **one file**: NDR's *Hinter den Kulissen von Hamburgs
Kantinen & Co.*, 28 min 49 s, fetched from its YouTube link and transcribed with `tdt-0.6b-v3-f16`
on **Vulkan** on the desktop (RTF 0.011, 285 segments, 68 carrying a word below 0.45 confidence).
Segmentation does not depend on the backend, and the boundaries matched the app's own run on the
same link to the second — 0:00, 0:08, 0:37, 1:03, 1:11, 1:26, 1:31 — so this is the segmentation the
report was about.

**Since later the same day the English is translated by these sentences too.**
`TranscriptTranslation` splits the source with the same splitter before the translator sees it, so
the English pane reads one line per sentence at the sentence's own time and `.en.srt` cues stop
straddling a sentence end; the English JSON's segments are sentences and pair with the transcript's
by time — each lies inside its source segment's span — rather than one to one (the source index is
carried in the document model and is not written to the JSON). Driven once on the real path the day
it landed: the ten-minute podcast cut, Vulkan for the recogniser and WebGPU for the translator, exit
0 in 51 s; the detector's 78 segments became **162 English sentences**, two of them cut after `vs.`
and `Mr.` — the abbreviation-before-a-capital weakness the splitter's own record names — and nothing
else read wrong on a pass over the lines. **None of it is measured.** No chrF++ and no
adequacy check has been run with the sentence as the unit: the FLEURS figures (§ *Translating into
English*) are per sentence by construction, since FLEURS is sentence-level, and the cascade penalty
(Spanish −2.95, German −4.34 chrF++) was measured per ASR segment — whether a shorter, single-sentence
input moves either is not established on any file. It is more requests of less text each — on the
gate's segmentation of this file, 478 sentences where there were 285 segments — and the time that
costs was not measured. The German number rewrite (`TranslationRequest.Mark`) runs per request and
reads nothing across a sentence boundary, so it is unchanged by where the boundary falls.

**What the detector did on it.** The Ask tab's second line was the segment 8.52–37.95 s: 29.43 s,
389 characters, 9 sentences, 58 words — the thirty-second cap, cut at the quietest frame in the last
four seconds. Why the energy gate never closed it earlier, in 30 ms frames of RMS level (ffmpeg
`astats` over the decoded mono 16 kHz audio, outside the app):

| stretch | min / median / max dBFS | what the gate saw |
|---|---|---|
| 8.5–38 s, the 29 s segment | −43.9 / **−23.1** / −8.3 | 131 of 1,270 frames below −35; longest run below it 450 ms |
| 16.1–19.6 s, the 1.84 s gap after "Jackfrucht." | −40.3 / −25.8 / −12.4 | no frame below −35 for 420 ms together |
| 55–61 s, six seconds with no word after "gesehen." | −34.8 / −30.6 / −15.8 | music bed, read as speech throughout |

The gate's "definitely speech" line is −35 dBFS (`AbsoluteSpeechDb`) and its adaptive floor is
capped so the threshold can never sit above it; a bed at −23 to −31 dBFS is speech to it. The
silence rule needs 420 ms; the one 450 ms run is where the segment ended. The recogniser heard what
the gate did not: inside that segment its word timings leave 0.96 s after "aussieht.", 0.96 s after
"lieber.", 1.84 s after "Jackfrucht." and 0.48–0.64 s after three more sentence-final words, and the
next segment holds 6.08 s with no word after "gesehen." Over the whole file, 91 of 3,151
within-segment word gaps are 420 ms or longer and 33 are a second or longer. Across all 285 segments
the cap is the exception — median 3.5 s, mean 5.4 s, 34 of ten seconds or more, 10 of twenty or
more, 2 of twenty-eight or more — and the opening montage is where it bites. `§ Ten minutes of real
podcast` records that on conversational audio the cap never fired in 98 segments; this is the first
material here on which it does, and it is the first material here with a bed.

**What the rule did on it, through the C# itself.** A scratch console over the CLI's JSON, calling
`SentenceSplitter.Split` and nothing of its own: 285 segments → **478 lines**, 80 segments cut, 193
cuts; every segment's words reproduce its text (285 of 285), so no segment was left whole for that
reason; every segment's pieces join back to its text with single spaces (0 failures). The longest
line fell from 29.4 s to **17.0 s** — one spoken sentence of 45 words — and lines of ten seconds or
more from 34 to **4** (17.0, 14.3, 14.0 and 13.3 s; 45, 13, 18 and 9 words; each one sentence);
twenty seconds or more from 10 to 0. Mean line 2.9 s and 43 characters, against the segment's 5.4 s
and 73. The rule declined exactly one terminal mark, `bzw. die`, and that is right. Its first draft
did not let a number open a sentence, to protect `ca. 40`; that draft declined five marks, and four
of them — `Genau. 50`, `habe. 17`, `Westen. 4.41`, `Schanzenviertel. 7` — were sentence ends, while
no `ca. 40` occurred. The measurement won: a number opens a sentence, and `ca. 40` and `Nr. 5` join
`Dr. Müller` as the rule's recorded false cuts (a test names all four). **All 193 cuts were listed
and read**, shortest token first: none stands at a token that is an abbreviation, and the shortest
are `ab. | Aber`, `ne? | Das`, `da. | Ich`, `du. | Hab` — sentence ends all.

**What that reading is not.** Nobody compared a cut to the audio. The rule follows the model's
punctuation, so where the model wrote a full stop that a listener would not — `Abd. | Ciao.` looks
like a name the recogniser broke — the line breaks there too, and it is counted above as a sentence
end. The rule's accuracy is the punctuation's accuracy, and this project has never measured the
punctuation.

**What none of it establishes:**

- **One file, one language, one genre, one backend.** The refusals (a single letter, digits alone, a
  stop inside the word) and the known false cuts were chosen on German and English examples and
  exercised on German. None of the other twenty-three languages the model transcribes has been run
  through it. The failure on a language it handles badly is "the lines stay as long as they were",
  never a wrong cut of the text — the words reproduce the text or the segment is left whole — but
  that is an argument from the code, not a measurement.
- **That a person has read the result.** Every figure above is a count over a JSON; nobody has
  watched the highlight step from one sentence to the next while this recording plays, and screen
  capture is unavailable in this session by standing rule. The line being played is found by the
  same rule as before over shorter lines, and the word mark inside it is the same mark — both under
  test — but "it reads well" is a judgement nobody has made.
- **The detector is unchanged, and so is everything downstream of it.** Segments on audio with a bed
  still run to the cap; a cap cut still lands at the quietest frame and not in a pause; the subtitle
  files still break cues mid-sentence — on this file **24 % of the German `.srt` cues and 29 % of
  the English `.en.srt` cues open in lower case** (101 of 418 and 116 of 407), because
  `SubtitleCueBuilder` splits on characters and seconds and never on punctuation. That was offered
  as a second change on 2026-08-23 and declined for now; it is not done, and not half done.
- **The four lines still ten seconds or longer are single sentences**, 9 to 45 words. Nothing here
  shortens a sentence the speaker made long, and nothing should; a reader who wants a shorter unit
  wants the word mark, which still runs inside the line.
- **The English pane is one line per segment**, which on this file is up to 29 s of English, and
  says so in its notice. A translation carries no word times and does not hold its source's sentence
  count, so there is nothing to cut it by that is not a guess; that is a decision, not a gap, and it
  is recorded here because a reader of the English will meet the old shape there.

## The neural speech detector — measured on one documentary and one podcast, 2026-08-23

`--vad neural` cuts the audio on Silero VAD v5 (ONNX Runtime 1.29.0, CPU, one thread, in process) in
place of the energy gate; `docs/PHASES.md` § *Built 2026-08-23 — a neural speech detector* has what
and why. Everything here is **one machine** (the RTX 5080 desktop), **two files**, one day. **Since
later the same day the detector is the default on both routes whenever its model is installed** —
the app's checkbox and `--vad` alike — so every figure earlier in this document is the gate's and
stands as a record, and a default run of any harness is no longer a re-run of one: pass `-Vad
energy` (`--vad energy`) to reproduce a figure, and read `speechDetector` in the run's JSON to know
which detector you got. The JSON did not carry that field before this day; a transcript without it
was cut by the gate or, under `--no-vad`, by fixed windows, and the flags that made it are the only
record.

**On the documentary that raised it** — NDR's *Hinter den Kulissen von Hamburgs Kantinen & Co.*,
28:49, fetched from its link; `tdt-0.6b-v3-f16` on Vulkan; both runs on the same m4a:

| | energy gate | Silero VAD |
|---|---|---|
| segments | 285 | 342 |
| mean / median / longest | 5.4 / 3.5 / **29.4 s** | 3.8 / 3.0 / **21.7 s** |
| segments ≥ 10 s / ≥ 20 s / ≥ 28 s | 34 / 10 / 2 | 17 / 1 / 0 |
| audio decoded (segmented) | 1,529.9 s | 1,309.0 s |
| words decoded | 3,436 | 3,387 (−1.4 %) |
| real-time factor (whole pass) | 0.0109 | 0.0126 |
| decode seconds / processing seconds | 14.8 / 18.9 | 9.9 / 21.7 |

416.8 s of audio above −55 dBFS was judged not speech and not decoded — the bed, on the reading of
the first ten segments, which now begin "Die Erbsenkresse." / "damit es einfach geiler aussieht." /
"Moin Moin. Eine Langkolle zum Mitnehmen, bitte." where the gate's second segment had been 29 s and
nine sentences. The decoded text is **not the same text**: a bag-of-words comparison finds 211 word
tokens in the gate's transcript that are not in the detector's and 164 the other way, and among the
gate-only tokens are *yeah* (7), *so* (6), *you* (6), *thank* (5) — English filler the recogniser
wrote over stretches the detector did not decode, which reads as hallucination over music, and is
not proven to be. Other differences are the recogniser decoding different segment boundaries
("Erbstegressel" / "Erbsenkresse", "Gemüsekurry" / "Gemüsecurry"). **No reference transcript was
scored**, so −1.4 % is a word count and not an error rate, and which of the two transcripts is the
more correct is not established.

**On the ten-minute podcast the speed figures come from** — `csb384-8438.m4a`, 600 s, the cut that
stands in for `chunk.m4a` — the detector is slower and, on conversation, cuts *longer*:

| | energy gate | Silero VAD |
|---|---|---|
| Vulkan RTF, two runs each | 0.0103, 0.0102 | 0.0148, 0.0146 |
| Vulkan processing / decode seconds | 6.2 / 4.6 | 8.9 / 4.9 |
| CPU RTF, one run each | 0.0823 | 0.0902 |
| CPU processing / decode seconds | 49.4 / 47.5 | 54.1 / 49.9 |
| segments | 113 | 78 |
| mean / median / longest | 5.1 / 3.9 / 29.9 s | 7.4 / 4.6 / 30.0 s |
| segments ≥ 10 s / ≥ 20 s | 11 / 1 | 23 / 7 |
| words decoded (Vulkan) | 1,632 | 1,621 |

Text byte-identical between the two Vulkan runs of each mode, so the figures are of the method and
not of a run. **The detector costs about 2.7 s per 600 s of audio here — RTF ≈ 0.0045 of its own**,
serialised with the read and the decode on one CPU thread, which is 44 % of a Vulkan pass and 9.5 %
of a CPU one; on the documentary it looked cheaper only because less audio reached the decoder.
**And it holds speech open across the short pauses the gate cuts at**: 78 segments where the gate
made 113, seven of them twenty seconds or more where the gate made one. That is the upstream
thresholds — speech opens at 0.5, closes below 0.35, `neg_threshold = threshold − 0.15` in
`utils_vad.py` at the pinned commit — applied under this segmenter's 420 ms silence rule, and
nothing here tuned them. This table is the case against the detector as a default for conversation,
and it was made the default anyway — both routes, the same day, on the maintainer's call and the
documentary's table; `docs/PHASES.md` has the decision. The longer cuts are a tuning question
(`SpeechProbability`, `SilenceProbability`) that nothing here has measured.

**What the rule was checked against.** The graph's inputs and outputs were read off the file with
`onnxruntime` 1.27.0 before a line of C# was written — `input [None, None]`, `state [2, None, 128]`,
`sr []`; `output [None, 1]`, `stateN` — and against upstream's wrapper at the pinned commit (window
512, context 64, state `(2, batch, 128)`); a silent window scores 0.012 and a window of white noise
at 0.3 peak scores 0.113 there, and the same shapes in C# score silence below 0.1 at 16, 44.1 and 48
kHz (the two env-gated tests, run against the graph on this machine and green). The model was
installed through `ModelInstaller` from the pinned URL and accepted against the pin, and `doctor`
reports it loading.

**What none of it establishes:**

- **Whether the detector's transcript is more or less correct than the gate's.** Word counts moved
  by 1.4 % and 0.7 %; nothing scored either against a reference, on either file.
- **Whether the thresholds are right for anything but the upstream demo.** 0.5 / 0.35 were not tuned
  here, and the podcast says they merge pauses the gate cuts at; `SpeechProbability` and
  `SilenceProbability` are options, and no value of them has been measured against another.
- **Anything about a provider.** CPU, one thread, by decision; no GPU provider was loaded for this
  graph and none is offered. The `onnxruntime` that scored the sanity windows above is the Python
  1.27.0 wheel, not the .NET 1.29.0 that ships — the shapes agree; the probabilities were not
  compared to the last digit.
- **Two files, one language each, one machine.** No other genre, no other rate than the two files'
  own (the 44.1/48 kHz figures are synthetic silence through the resampler), no laptop.
- **That the detector never drops speech.** The documentary's 416.8 s of undecoded audible material
  is read as the bed from ten segments; nobody listened to it. The CLI says the figure on every run,
  with the remedy, rather than hiding it.
- **The segment cap is still there, and the podcast's detector run reached it** (longest 30.0 s)
  where the documentary's did not (21.7 s); the forced cut still lands at the quietest frame. A
  detector changes what is speech, not what happens when speech runs thirty seconds without a pause.

## The interface design, and the one claim in it that is not checked

The design decided 2026-08-19 is recorded in `docs/PHASES.md`; its sources are off this repository
by the research convention. Almost all of it was measured — every contrast ratio computed from the
hex values, every oklch conversion done rather than eyeballed, and every layout number read out of
a headless browser with the webfonts confirmed loaded, which matters because an unloaded webface
silently measures the fallback and every number comes out wrong. Three real defects were caught
that way and none of them by looking: an artboard frame 1288 px shorter than its content, silently
clipping two whole sections; a list clipping 20 px because `width:100%` met content-box sizing; and
a lane running to a third line and clipping 34 px.

One claim is not checked, and it is the one the whole window frame rests on.

### The window's corner — settled 2026-08-20, except for snap layouts

The design specifies no OS title bar, the application's own shadow, and — since 2026-08-19 — a
**square** window corner. This was the one claim in the design that nothing had rendered. The
window was built and run on Windows 11 on 2026-08-20, and most of it is now measured.

**Going square inverted the problem, which is the useful part.** The earlier 12 px design was
unreachable that way round, because DWM does not take an arbitrary radius — it would have needed a
borderless window with a transparent backdrop painting its own shadow, a larger piece of work with
its own snap-layout and DPI consequences. DWM does expose a corner *preference*, and one of its
values is do-not-round. So square is something the window can ask for, where 12 px was not, and
this design is cheaper to build than the one it replaced rather than dearer.

**What is now measured.** `DwmSetWindowAttribute` with `DWMWA_WINDOW_CORNER_PREFERENCE` returns
`S_OK` from `Services/WindowFrame.cs`, called on `OnOpened` once the window has a handle, and
`DwmGetWindowAttribute` reads the preference back as `DWMWCP_DONOTROUND` — so it reaches an
Avalonia window with an extended client area and is retained rather than silently ignored. The
corner **draws square**, confirmed by looking at the running window. Per-monitor DPI is not a
problem either: the display it was run on reports 240 DPI, which is 250% scaling, and the layout
scales cleanly with no clipping and no blurring — the manifest declares `permonitorv2` and Windows
honours it.

**What is still not established**, and is a smaller list than before:

- **Snap layouts have not been exercised.** Hovering the maximise button for the Windows 11 snap
  flyout, and the quarter-tiling it offers, is untested. The window now uses
  `WindowDecorations="None"` with `WindowDecorationProperties.ElementRole="MaximizeButton"` on its
  own button, which is the arrangement the toolkit documents for exactly this, but documented is
  not measured.
- **Only one scaling factor has been seen.** 250% works. 100% and 150% are untested, as is moving
  the window between monitors of different DPI while it is open.
- **The shadow is the compositor's, not the design's.** The design specifies four layers; DWM
  draws the one it draws, and there is no way to hand it a stack. What ships is the platform's
  shadow plus a 1px edge inside the window, and the two together were confirmed by eye on
  2026-08-20 to answer the question the design's shadow was there to answer — where does the
  application end. The four-layer figure in the token sheet describes a browser mock-up and should
  not be repeated as a description of this window.

**What would settle the rest:** exercise the snap flyout, and look at the frame at 100% and 150%
and across a DPI boundary. A release note may describe the corner and the fact that the window has
a visible edge; it may not describe the shadow as the design's, because it is not.

### The two contrast defects are measured, not unproven — and now fixed

`#D9A441` at 2.25:1 and `#D9534F` at 3.96:1 on white were computed from the shipped hex values and
were never in question. **Both were replaced the same day** — `#966C13` at 4.72:1 and `#B84E45` at
4.98:1, the same hues taken dark enough to clear the 4.5:1 an audit asks of body text — along with
the third defect beside them, a verified provenance line painted in the warning colour. All three
are recorded in `docs/PHASES.md` with how they closed. Nothing about the ratios above is unproven;
they are kept here because a defect recorded as unfixed is a defect that gets fixed twice.
