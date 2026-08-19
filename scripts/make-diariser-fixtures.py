#!/usr/bin/env python3
"""Generate the committed fixtures the C# Sortformer diariser is held against.

The C# port reimplements three things the ONNX graph does not own — the mel featurizer, the
Arrival-Order Speaker Cache, and the chunk loop — plus NeMo's post-processing. Each is a place
where a plausible-looking implementation produces a worse DER without failing, so each is held
against the reference implementation rather than against a reading of it. This script runs the
reference and writes what it got into tests/fixtures/diarisation/sortformer/; the C# suite then
asserts the port reproduces every figure there, on Linux, with no weights and no network.

That constraint is why the fixtures look the way they do:

  * **Inputs are formulae, not files.** Every input signal and probability sequence is defined by
    an exact expression evaluated identically in Python and C# (see `deterministic.py` mirrored by
    DeterministicInputs.cs). Nothing but the *expected output* is committed, so a fixture cannot
    drift from the input that produced it and no audio enters the repository.
  * **The speaker cache is exercised at emb_dim 8, not 512.** `streaming_update_async` never does
    arithmetic across the embedding dimension except a masked mean, so every index computation,
    score, boost, top-k and eviction it performs is identical at 8. Committing the 512-wide tensors
    would be 50 MB for the same coverage. The embeddings carry `f + d/16` rather than noise, so a
    gather that reads the wrong frame or the wrong stride is visible in the value itself.
  * **The reference is imported, not transcribed.** NVIDIA's own `SortformerModules` and NeMo's own
    `FilterbankFeatures` are called here. What is committed is their output.

Requires torch, numpy and librosa, plus a directory holding NeMo's `sortformer_modules.py` and
`features.py` importable as `nemo.collections.asr.*` — the spike's `nemostub/` tree, or a NeMo
install. CI never runs this, for the same reason it never runs `validate-der.py`: the check it
performs is committed, so the suite holds it without Python.

    python scripts/make-diariser-fixtures.py --reference C:/Users/ayymanPC/spike-sortformer

Re-running it must be a no-op on a correct tree. It prints a diff summary and exits non-zero if
anything changed, so a fixture that moved is a reviewable event rather than a silent one.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tests" / "fixtures" / "diarisation" / "sortformer"

# ── the export's geometry ────────────────────────────────────────────────────────────────────
# Every value read from soniqo/Sortformer-Diarization-4spk-ONNX's config.json (the `default`
# variant) or from the v2.1 checkpoint's own `sortformer_modules` block. Named here rather than
# defaulted, because pairing one variant's geometry with another's update period evicts the wrong
# frames while looking healthy — the failure the export's README warns about.
GEOM = dict(
    num_spks=4,
    fc_d_model=512,
    tf_d_model=192,
    subsampling_factor=8,
    spkcache_len=188,
    fifo_len=40,
    chunk_len=340,
    spkcache_update_period=188,
    chunk_left_context=1,
    chunk_right_context=40,
    spkcache_sil_frames_per_spk=3,
    scores_add_rnd=0,
    pred_score_threshold=0.25,
    max_index=99999,
    scores_boost_latest=0.05,
    sil_threshold=0.2,
    strong_boost_rate=0.75,
    weak_boost_rate=1.5,
    min_pos_scores_rate=0.5,
    use_learnable_sil_emb=False,
)

# The featurizer's constants, from the checkpoint's `preprocessor:` block.
SAMPLE_RATE = 16000
N_WINDOW_SIZE = 400
N_WINDOW_STRIDE = 160
N_FFT = 512
N_MELS = 128
PREEMPH = 0.97
LOG_ZERO_GUARD = 2.0**-24
PAD_TO = 16

FIXTURE_EMB_DIM = 8  # see the module docstring


# ── deterministic inputs, mirrored exactly in C# ─────────────────────────────────────────────
# A 32-bit linear congruential generator with the glibc constants, stepped in exact integer
# arithmetic so Python and C# cannot disagree. Only the top bits are used, which is the standard
# way round an LCG's weak low-order bits.
def lcg(seed: int, count: int):
    """Yield `count` values in [-1, 1), from the sequence s <- (1103515245 s + 12345) mod 2^31."""
    state = seed & 0x7FFFFFFF
    for _ in range(count):
        state = (1103515245 * state + 12345) & 0x7FFFFFFF
        yield (state >> 7) / float(1 << 23) - 1.0


def signal(name: str, n: int):
    """The audio fixtures' input signals. Every one is exactly reproducible in C#."""
    import numpy as np

    if name == "silence":
        return np.zeros(n, dtype=np.float32)
    if name == "ramp":
        return np.asarray([-1.0 + 2.0 * i / (n - 1) for i in range(n)], dtype=np.float32)
    if name == "noise":
        return np.asarray(list(lcg(20260819, n)), dtype=np.float32) * 0.25
    if name == "tones":
        # Three partials under a slow envelope: a log-mel with real structure across the bank,
        # which flat white noise does not give.
        out = np.empty(n, dtype=np.float32)
        for i in range(n):
            t = i / SAMPLE_RATE
            env = 0.5 + 0.5 * math.sin(2 * math.pi * 3.0 * t)
            out[i] = env * (
                0.30 * math.sin(2 * math.pi * 220.0 * t)
                + 0.20 * math.sin(2 * math.pi * 1310.0 * t + 0.7)
                + 0.10 * math.sin(2 * math.pi * 3700.0 * t + 1.9)
            )
        return out
    raise ValueError(name)


def probabilities(frames: int, n_spk: int, phase: float = 0.0, seed: int = 770415):
    """Per-frame speaker activity in [0, 1].

    Slow square-ish runs of speech per speaker, at co-prime periods so speakers overlap and fall
    silent independently, plus jitter large enough to cross a threshold inside a run. That jitter
    is the point: it is what separates a hysteresis from a plain comparison, what makes
    `min_duration_on` have something to delete, and what makes `min_duration_off` have something to
    fill. A clean square wave would pass every implementation.

    `phase` and `seed` exist so the speaker-cache fixture can give every step *different*
    predictions. They must be different, and that was learned the hard way: an earlier version of
    this script reused one pattern rolled by a fixed offset per step, which left 307 of 528
    candidate rows sharing a prediction vector with another row. Identical predictions are identical
    scores, and `torch.topk` does not define its order among equal values — so the fixture demanded
    a choice the reference itself does not make, and no port could have reproduced it except by
    luck. See `cache_probabilities`, which is what the speaker-cache fixture uses instead.
    """
    import numpy as np

    jitter = np.asarray(list(lcg(seed, frames * n_spk)), dtype=np.float64).reshape(frames, n_spk)
    out = np.empty((frames, n_spk), dtype=np.float32)
    for c in range(n_spk):
        period = (97, 131, 163, 211)[c]
        for f in range(frames):
            base = math.sin(2 * math.pi * (f + phase) / period + 0.9 * c)
            # A soft square: mostly saturated near 0 or 1, with a fast transition through the middle.
            value = 1.0 / (1.0 + math.exp(-6.0 * base))
            out[f, c] = min(1.0, max(0.0, value + 0.14 * jitter[f, c]))
    return out


def cache_probabilities(frames: int, n_spk: int, phase: float, seed: int):
    """The speaker-cache fixture's predictions: like `probabilities`, but never tied and never silent
    for only one speaker.

    Two differences, both forced by what the cache does with them rather than by taste.

    **Nothing saturates.** The activity is squeezed into (0.005, 0.995) and the jitter is small
    enough that it stays there, so no value is ever clamped. Clamping is what makes two frames
    identical — a run of frames all pinned to exactly 1.0 and 0.0 is a run of identical prediction
    vectors — and identical predictions are identical scores. `torch.topk` does not define its order
    among equal values, so a fixture containing them demands a choice the reference itself does not
    make: it is not merely hard for a port to reproduce, it is undefined. Measured on an earlier
    version of this function: 234 of 528 candidate rows duplicated another, and three of the ten
    compressions had a finite tie sitting exactly on a top-k boundary.

    **Every speaker falls quiet together, in stretches.** Four speakers on co-prime periods almost
    never do that by chance, so without the gate the running silence profile collects frames in the
    first step and never again — leaving the branch that folds new silence into an existing mean
    unexercised. With it, all ten steps contribute.
    """
    import numpy as np

    jitter = np.asarray(list(lcg(seed, frames * n_spk)), dtype=np.float64).reshape(frames, n_spk)
    out = np.empty((frames, n_spk), dtype=np.float32)
    for c in range(n_spk):
        period = (97, 131, 163, 211)[c]
        for f in range(frames):
            base = math.tanh(2.0 * math.sin(2 * math.pi * (f + phase) / period + 0.9 * c))
            # The sine sets the mean and the jitter carries most of the spread, deliberately: it is
            # the spread that keeps values apart once they are float32, and a saturating activity
            # curve pins hundreds of frames within an ulp of each other.
            value = 0.5 + 0.30 * base + 0.19 * jitter[f, c]           # (0.02, 0.98)
            quiet = math.sin(2 * math.pi * (f + phase) / 173.0 + 2.1) <= -0.6
            out[f, c] = value * (0.03 if quiet else 1.0)              # sums below 0.2 when quiet
    return out


def assert_no_ambiguous_ties(modules, candidates) -> bool:
    """True when no top-k boundary in any compression falls on a tie between two finite scores.

    Run against the candidate sets `_compress_spkcache` actually saw, using the reference's own
    `_get_log_pred_scores`, `_disable_low_scores` and `_boost_topk_scores` rather than a
    reimplementation of them — the point is what the reference does, not what this script thinks it
    does.

    Why it is needed at all: the score is **not injective in the predictions**. Both logs are floored
    at 0.25, so once a speaker is above 0.75 its own complement term is pinned to log 0.25, and when
    the other three are too the score collapses to a function of that one probability. Two frames
    agreeing in one column then score identically however much they differ in the rest — and
    `torch.topk` does not define its order among equal values, so if such a pair straddles a boundary
    the reference picks one arbitrarily and the fixture is asking a question with no answer. Two
    input generators were rejected before this check existed, both of which looked fine.
    """
    import torch

    n_spk = modules.n_spk
    per_spk = modules.spkcache_len // n_spk - modules.spkcache_sil_frames_per_spk
    boosts = (
        (math.floor(per_spk * modules.strong_boost_rate), 2.0),
        (math.floor(per_spk * modules.weak_boost_rate), 1.0),
    )
    min_pos = math.floor(per_spk * modules.min_pos_scores_rate)

    def tied_at(values, k):
        ordered, _ = torch.sort(values, descending=True, stable=True)
        if k >= ordered.numel():
            return None
        if ordered[k - 1] == ordered[k] and torch.isfinite(ordered[k - 1]):
            return float(ordered[k - 1])
        return None

    for index, preds in enumerate(candidates):
        scores = modules._get_log_pred_scores(preds)
        scores = modules._disable_low_scores(preds, scores, min_pos)
        scores[:, modules.spkcache_len :, :] += modules.scores_boost_latest

        for k, scale in boosts:
            for spk in range(n_spk):
                value = tied_at(scores[0, :, spk], k)
                if value is not None:
                    return False
            scores = modules._boost_topk_scores(scores, k, scale_factor=scale)

        pad = torch.full((1, modules.spkcache_sil_frames_per_spk, n_spk), float("inf"))
        flat = torch.cat([scores, pad], dim=1).permute(0, 2, 1).reshape(1, -1)[0]
        value = tied_at(flat, modules.spkcache_len)
        if value is not None:
            return False

    return True


# ── writers ──────────────────────────────────────────────────────────────────────────────────
def write_f32(path: Path, array) -> dict:
    import numpy as np

    data = np.ascontiguousarray(array, dtype="<f4")
    path.write_bytes(data.tobytes())
    return {"file": path.name, "shape": list(data.shape), "dtype": "float32-le"}


def write_json(path: Path, payload) -> None:
    path.write_text(json.dumps(payload, indent=1, sort_keys=False) + "\n", newline="\n")


# ── 1. the filterbank and the window ─────────────────────────────────────────────────────────
def make_filterbank(ref_module) -> dict:
    """NeMo's own mel filterbank and analysis window.

    Both are tables the port has to rebuild from scratch, and both are silent when wrong: a
    filterbank built with librosa's `htk` mel scale instead of Slaney's, or a window built
    `periodic=True` the way librosa's own STFT defaults, changes every feature by a few percent
    and nothing else. Committed as the numbers rather than as a description of them.
    """
    import torch

    fb = ref_module.fb[0] if ref_module.fb.dim() == 3 else ref_module.fb
    entries = {
        "filterbank": write_f32(OUT / "mel-filterbank.f32", fb.numpy()),
        "window": write_f32(OUT / "mel-window.f32", ref_module.window.numpy()),
    }
    assert entries["filterbank"]["shape"] == [N_MELS, N_FFT // 2 + 1], entries["filterbank"]["shape"]
    assert entries["window"]["shape"] == [N_WINDOW_SIZE], entries["window"]["shape"]
    return entries


# ── 2. the mel features ──────────────────────────────────────────────────────────────────────
def make_features(ref_module) -> dict:
    """NeMo `FilterbankFeatures` output for four formula-defined signals.

    The lengths are deliberately not round: 16 077 samples is neither a multiple of the 160-sample
    hop nor of the 16-frame pad, so the two places a port truncates instead of padding — the valid
    length and the pad-to-multiple — are both exercised.
    """
    import numpy as np
    import torch

    cases = [("tones", 16077), ("noise", 16000), ("silence", 12800), ("ramp", 9999)]
    out = []
    for name, n in cases:
        wav = signal(name, n)
        with torch.no_grad():
            feats, lengths = ref_module(
                torch.from_numpy(wav).unsqueeze(0), torch.tensor([n], dtype=torch.long)
            )
        feats = feats[0].transpose(0, 1).contiguous().numpy()  # [T, 128]
        entry = write_f32(OUT / f"mel-{name}.f32", feats)
        entry.update(signal=name, samples=n, validFrames=int(lengths[0]))
        out.append(entry)
        print(f"  mel {name:8s} {n:6d} samples -> {feats.shape} valid {int(lengths[0])}")
    return {"cases": out}


# ── 3. the chunk plan ────────────────────────────────────────────────────────────────────────
def make_chunk_plan() -> dict:
    """The streaming loop's slicing arithmetic, for several audio lengths.

    A transcription of `streaming_feat_loader`'s asymmetric first and last chunks as the spike's
    driver calls it. No model and no tensors are involved — it is pure index arithmetic — but it is
    arithmetic with four off-by-one opportunities in it (the left context that does not exist on the
    first chunk, the right context that runs out on the last, the valid length measured from the
    chunk's own start, and the encoder-frame rounding, which is `round` on the left and `ceil` on
    the right), and each of them degrades the result without breaking it.
    """
    sub = GEOM["subsampling_factor"]
    chunk_len, lc_cfg, rc_cfg = GEOM["chunk_len"], GEOM["chunk_left_context"], GEOM["chunk_right_context"]

    def plan(valid_frames: int, feat_len: int):
        steps, stt, end = [], 0, 0
        while end < feat_len:
            left_offset = min(lc_cfg * sub, stt)
            end = min(stt + chunk_len * sub, feat_len)
            right_offset = min(rc_cfg * sub, feat_len - end)
            width = end + right_offset - (stt - left_offset)
            chunk_len_frames = int(min(max(valid_frames - stt + left_offset, 0), width))
            steps.append(
                dict(
                    melStart=stt - left_offset,
                    melWidth=width,
                    chunkLengthFrames=chunk_len_frames,
                    leftContextEncoderFrames=round(left_offset / sub),
                    rightContextEncoderFrames=math.ceil(right_offset / sub),
                )
            )
            stt = end
        return steps

    cases = []
    for seconds in (5, 31, 62, 121, 600):
        samples = seconds * SAMPLE_RATE
        valid = (samples + (N_FFT // 2) * 2 - N_FFT) // N_WINDOW_STRIDE
        stft_frames = samples // N_WINDOW_STRIDE + 1
        feat_len = stft_frames + (-stft_frames % PAD_TO)
        cases.append(
            dict(
                seconds=seconds,
                samples=samples,
                validFrames=valid,
                paddedFrames=feat_len,
                steps=plan(valid, feat_len),
            )
        )
        print(f"  plan {seconds:4d} s -> {len(cases[-1]['steps'])} chunks, {feat_len} mel frames")
    return {"cases": cases}


# ── 4. the Arrival-Order Speaker Cache ───────────────────────────────────────────────────────
def make_speaker_cache(modules_cls) -> dict:
    """Ten steps of NVIDIA's own `streaming_update_async`, state captured after each.

    Ten is chosen rather than two: the cache reaches its 188-frame capacity on the first step and
    is compressed on every step after it, so the eviction path, the two boosts, the silence
    profile and the `spkcache_compressed` latch — which changes *which* predictions the first
    compression scores — are all exercised repeatedly rather than once. The last step is short, as
    a real recording's last chunk is.

    Retried with fresh seeds until `assert_no_ambiguous_ties` passes. A seed search rather than a
    cleverer input distribution because the degeneracy cannot be designed away: the score floors
    both logs at 0.25, so any frame whose three other speakers are all above 0.75 scores on its own
    probability alone, and two such frames landing on the same float32 is a birthday problem over
    a few thousand frames. Whether a given seed produces one *on a top-k boundary* is luck, so the
    honest move is to check and reroll rather than to reason about it.
    """
    for attempt in range(64):
        result = _speaker_cache_attempt(modules_cls, 770415 + 7919 * attempt)
        if result is not None:
            section, trace = result
            for line in trace:
                print(line)
            print(f"  seed offset {attempt}: no ambiguous ties in any compression")
            return section

    raise SystemExit("no seed in 64 attempts produced a tie-free speaker-cache fixture.")


def _speaker_cache_attempt(modules_cls, seed_base: int):
    """One run of the ten steps. Returns the manifest section, or None if the run is ambiguous."""
    import numpy as np
    import torch

    n_spk, emb_dim = GEOM["num_spks"], FIXTURE_EMB_DIM
    spkcache_len, fifo_len, chunk_len = GEOM["spkcache_len"], GEOM["fifo_len"], GEOM["chunk_len"]
    rc_cfg = GEOM["chunk_right_context"]

    mods = modules_cls(**{**GEOM, "fc_d_model": emb_dim})
    mods.eval()  # `_compress_spkcache` injects random noise under self.training

    # Every candidate set the compression actually sees, so the tie check below runs against what
    # happened rather than against a reconstruction of it.
    seen_candidates = []
    compress = mods._compress_spkcache

    def spy(emb_seq, preds, mean_sil_emb, permute_spk=False):
        seen_candidates.append(preds.clone())
        return compress(emb_seq, preds, mean_sil_emb, permute_spk)

    mods._compress_spkcache = spy

    state = mods.init_streaming_state(batch_size=1, async_streaming=True, device="cpu")
    # What the graph returns, whatever this step's own geometry: 188 + 40 + (1 + 340 + 40).
    preds_width = spkcache_len + fifo_len + GEOM["chunk_left_context"] + chunk_len + rc_cfg

    steps, blobs, trace = [], {}, []
    frame_base = 0
    every_prediction = []
    for step in range(10):
        first, last = step == 0, step == 9
        lc = 0 if first else GEOM["chunk_left_context"]
        rc = 7 if last else rc_cfg
        physical = lc + (61 if last else chunk_len) + rc
        max_chunk_len = physical - lc - rc

        # Embeddings carry their own coordinates: emb[f, d] = (frame_base + f) + d/16, exact in
        # float32. A gather that reads the wrong frame, or reads down the wrong stride, shows up
        # in the value rather than in a statistic.
        chunk = np.empty((1, physical, emb_dim), dtype=np.float32)
        for f in range(physical):
            for d in range(emb_dim):
                chunk[0, f, d] = (frame_base + f) + d / 16.0
        frame_base += physical

        preds = np.zeros((1, preds_width, n_spk), dtype=np.float32)
        # A distinct pattern per step: a non-integer phase shift and a fresh jitter stream, so no
        # two candidate rows in any compression share a prediction vector. See `probabilities`.
        active = cache_probabilities(preds_width, n_spk, phase=step * 13.7, seed=seed_base + 1009 * step)
        total_valid = int(state.spkcache_lengths[0]) + int(state.fifo_lengths[0]) + physical
        preds[0, :total_valid] = active[:total_valid]
        every_prediction.extend(map(tuple, preds[0, :total_valid].tolist()))

        before = dict(
            spkcacheLength=int(state.spkcache_lengths[0]),
            fifoLength=int(state.fifo_lengths[0]),
            spkcacheCompressed=bool(state.spkcache_compressed[0]),
            silenceFrames=int(state.n_sil_frames[0]),
        )
        tensors = {}
        tensors["chunk"] = chunk[0]
        tensors["preds"] = preds[0]
        tensors["inSpkcache"] = state.spkcache[0].numpy().copy()
        tensors["inSpkcachePreds"] = state.spkcache_preds[0].numpy().copy()
        tensors["inFifo"] = state.fifo[0].numpy().copy()
        tensors["inMeanSilence"] = state.mean_sil_emb[0].numpy().copy()

        with torch.no_grad():
            state, chunk_preds = mods.streaming_update_async(
                streaming_state=state,
                chunk=torch.from_numpy(chunk),
                chunk_lengths=torch.tensor([physical], dtype=torch.long),
                preds=torch.from_numpy(preds),
                lc=lc,
                rc=rc,
            )

        tensors["outChunkPreds"] = chunk_preds[0].numpy().copy()
        tensors["outSpkcache"] = state.spkcache[0].numpy().copy()
        tensors["outSpkcachePreds"] = state.spkcache_preds[0].numpy().copy()
        tensors["outFifo"] = state.fifo[0].numpy().copy()
        tensors["outFifoPreds"] = state.fifo_preds[0].numpy().copy()
        tensors["outMeanSilence"] = state.mean_sil_emb[0].numpy().copy()

        placed = {}
        for key, array in tensors.items():
            array = np.ascontiguousarray(array, dtype="<f4")
            placed[key] = dict(offset=sum(a.size for a in blobs.values()), shape=list(array.shape))
            blobs[f"{step:02d}.{key}"] = array

        steps.append(
            dict(
                step=step,
                leftContext=lc,
                rightContext=rc,
                physicalChunkFrames=physical,
                maxChunkLength=max_chunk_len,
                chunkLengthsInput=physical,
                before=before,
                after=dict(
                    spkcacheLength=int(state.spkcache_lengths[0]),
                    fifoLength=int(state.fifo_lengths[0]),
                    spkcacheCompressed=bool(state.spkcache_compressed[0]),
                    silenceFrames=int(state.n_sil_frames[0]),
                ),
                tensors=placed,
            )
        )
        trace.append(
            f"  aosc step {step}: lc {lc} rc {rc} chunk {physical} -> cache "
            f"{int(state.spkcache_lengths[0])} fifo {int(state.fifo_lengths[0])} "
            f"sil {int(state.n_sil_frames[0])}"
        )

    # The guard that keeps this fixture answerable. Two frames with the same prediction vector score
    # the same, and `torch.topk` does not define its order among equal values — so a duplicate here
    # asks the port to reproduce a choice the reference made arbitrarily. Checked rather than
    # trusted, because two earlier versions of the input generator produced them and neither was
    # obvious from reading it.
    distinct = len(set(every_prediction))
    if distinct != len(every_prediction):
        return None

    if not assert_no_ambiguous_ties(mods, seen_candidates):
        return None

    # One blob rather than 120 files: the offsets are in the manifest, so a reader seeks instead
    # of opening, and a fixture directory stays readable in a listing.
    flat = np.concatenate([a.reshape(-1) for a in blobs.values()])
    write_f32(OUT / "speaker-cache.f32", flat)

    return dict(
        embeddingDimension=emb_dim,
        predictionWidth=preds_width,
        blob="speaker-cache.f32",
        blobFloats=int(flat.size),
        steps=steps,
    ), trace


# ── 5. post-processing ───────────────────────────────────────────────────────────────────────
def make_post_processing(postproc) -> dict:
    """NeMo's `ts_vad_post_processing` over a formula-defined probability sequence.

    Seven parameter sets, not one. The set that produced the passing DER (onset 0.5, offset 0.5,
    pad_onset 0.05, min_duration_off 1.0) happens to have onset equal to offset, which degenerates
    the hysteresis into a plain comparison — so a port with the hysteresis backwards would pass a
    test written only against it. The other six separate the thresholds and move each filter in
    turn, including the ordering the two filters are applied in, which NeMo's own YAML comments
    have the wrong way round.
    """
    import numpy as np

    frames, n_spk = 3000, GEOM["num_spks"]
    probs = probabilities(frames, n_spk)

    parameter_sets = [
        dict(onset=0.5, offset=0.5, pad_onset=0.05, pad_offset=0.0, min_on=0.0, min_off=1.0),
        dict(onset=0.5, offset=0.5, pad_onset=0.0, pad_offset=0.0, min_on=0.0, min_off=0.0),
        dict(onset=0.7, offset=0.3, pad_onset=0.0, pad_offset=0.0, min_on=0.0, min_off=0.0),
        dict(onset=0.8, offset=0.2, pad_onset=0.12, pad_offset=0.08, min_on=0.0, min_off=0.0),
        dict(onset=0.6, offset=0.4, pad_onset=0.0, pad_offset=0.0, min_on=0.5, min_off=0.0),
        dict(onset=0.6, offset=0.4, pad_onset=0.0, pad_offset=0.0, min_on=0.0, min_off=0.4),
        dict(onset=0.6, offset=0.4, pad_onset=0.04, pad_offset=0.04, min_on=0.32, min_off=0.72),
    ]

    results = []
    for params in parameter_sets:
        segments = postproc.to_segments(probs, **params)
        results.append(
            dict(
                parameters=params,
                segments=[
                    dict(start=round(a, 6), end=round(b, 6), speaker=int(c)) for a, b, c in segments
                ],
            )
        )
        print(f"  postproc {params} -> {len(segments)} segments")

    return dict(frames=frames, speakers=n_spk, frameSeconds=0.08, sets=results)


# ── driver ───────────────────────────────────────────────────────────────────────────────────
def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        "--reference",
        required=True,
        help="Directory holding the reference implementation: a `nemostub/` (or NeMo) tree with "
        "nemo.collections.asr.modules.sortformer_modules and "
        "nemo.collections.asr.parts.preprocessing.features importable from it, plus postproc.py.",
    )
    parser.add_argument("--check", action="store_true", help="Fail if any fixture would change.")
    args = parser.parse_args()

    reference = Path(args.reference).resolve()
    for candidate in (reference / "nemostub", reference):
        if (candidate / "nemo").is_dir():
            sys.path.insert(0, str(candidate))
            break
    sys.path.insert(0, str(reference))

    from nemo.collections.asr.modules.sortformer_modules import SortformerModules
    from nemo.collections.asr.parts.preprocessing.features import FilterbankFeatures
    import postproc

    import torch

    torch.manual_seed(0)

    before = {p.name: p.read_bytes() for p in OUT.glob("*") if p.is_file()} if OUT.is_dir() else {}
    OUT.mkdir(parents=True, exist_ok=True)

    featurizer = FilterbankFeatures(
        sample_rate=SAMPLE_RATE,
        n_window_size=N_WINDOW_SIZE,
        n_window_stride=N_WINDOW_STRIDE,
        window="hann",
        normalize="NA",
        n_fft=N_FFT,
        preemph=PREEMPH,
        nfilt=N_MELS,
        lowfreq=0,
        highfreq=None,
        log=True,
        log_zero_guard_type="add",
        log_zero_guard_value=LOG_ZERO_GUARD,
        dither=1e-5,
        pad_to=PAD_TO,
        frame_splicing=1,
        exact_pad=False,
        pad_value=0,
        mag_power=2.0,
        mel_norm="slaney",
    )
    featurizer.eval()
    assert featurizer.normalize == "NA", featurizer.normalize
    assert not featurizer.training

    print("filterbank and window")
    manifest = {
        "comment": [
            "Generated by scripts/make-diariser-fixtures.py from the reference implementation:",
            "NVIDIA's own SortformerModules and NeMo's own FilterbankFeatures, imported and run.",
            "Do not hand-edit. Inputs are formulae evaluated identically in Python and C# — only",
            "the expected output is committed. See the script's docstring for why.",
        ],
        "geometry": GEOM,
        "featurizer": dict(
            sampleRate=SAMPLE_RATE,
            windowSize=N_WINDOW_SIZE,
            windowStride=N_WINDOW_STRIDE,
            nFft=N_FFT,
            nMels=N_MELS,
            preemphasis=PREEMPH,
            logZeroGuard=LOG_ZERO_GUARD,
            padToMultiple=PAD_TO,
            normalize="NA",
            melNorm="slaney",
            window="hann-periodic-false",
        ),
        "tables": make_filterbank(featurizer),
    }

    print("mel features")
    manifest["features"] = make_features(featurizer)
    print("chunk plan")
    manifest["chunkPlan"] = make_chunk_plan()
    print("arrival-order speaker cache")
    manifest["speakerCache"] = make_speaker_cache(SortformerModules)
    print("post-processing")
    manifest["postProcessing"] = make_post_processing(postproc)

    write_json(OUT / "expected.json", manifest)

    after = {p.name: p.read_bytes() for p in OUT.glob("*") if p.is_file()}
    changed = sorted(
        (set(before) ^ set(after))
        | {k for k in set(before) & set(after) if before[k] != after[k]}
    )
    total = sum(len(v) for v in after.values())
    print(f"\n{len(after)} files, {total / 1024:.0f} KiB in {OUT.relative_to(ROOT)}")
    if changed:
        print("changed: " + ", ".join(changed))
        if args.check:
            return 1
    else:
        print("unchanged")
    return 0


if __name__ == "__main__":
    sys.exit(main())
