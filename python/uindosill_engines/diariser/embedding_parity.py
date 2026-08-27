"""Does this machine's embedder reproduce the one the published figures describe?

The companion to :mod:`.parity`, which asks the same question of the Sortformer graph. The reason is
the same and it is worth restating: **an execution provider can be catastrophically wrong and
indistinguishable from a correct one from the outside.** Measured 2026-08-21, DirectML at ONNX
Runtime's default settings scores 53.15% diarisation error against the CPU's 16.33%, thirteen times
faster, emitting speaker turns that look entirely normal.

**What this fixture compares is not what Sortformer's compares.** Sortformer's reference is the CPU
graph's own output, because that engine is ONNX Runtime on both sides of the comparison. Here the
reference is the **torch** embedder's output — the path that shipped before this one existed and the
path DiariZen's published numbers were produced on. So this asks the stronger question: not "does
WebGPU agree with ORT's CPU" but "does any of this agree with torch". A graph that exported subtly
wrong would pass the weaker question and fail this one.

**Why a waveform and not features.** The fixture enters at `__call__`, the same door the pipeline
uses, so the fbank is inside the comparison rather than beside it. It costs a tenth of a second and
it means a change to the feature extraction cannot slip past a graph-only check.

**The geometry is the pipeline's own**, and it is the part most likely to break silently: a 16 s
window is 1598 fbank frames, the segmentation mask that accompanies it is 799, and the ResNet
downsamples to about 200 before pooling. An export that tied those axes together would pass a
fixture built with matching sizes and fail on the first real chunk.
"""

from __future__ import annotations

import os
from typing import Any

import numpy as np

#: Chunks per fixture batch. **Three, and specifically not two, because two is what `export_graph`
#: traces at.** `torch.export` silently specialises a dimension to the size it was traced at — that
#: is the whole reason the export bypasses `torch.vmap`, see `embedding_onnx._build_export_wrapper`
#: — and a fixture running at the traced size cannot tell a dynamic batch axis from a frozen one.
#: It would pass, and the first real batch would fail inside the pipeline with a shape error, after
#: segmentation had already spent half the run. Any size other than the traced one catches it; three
#: is the cheapest.
FIXTURE_BATCH = 3

#: 16 s at 16 kHz, the window the pipeline embeds.
FIXTURE_SAMPLES = 256_000

#: Frames in the speaker mask: the *segmentation* frame rate, deliberately unequal to the 1598 fbank
#: frames the same window produces.
FIXTURE_MASK_FRAMES = 799

#: The seed is part of the fixture: change it and the committed reference means nothing.
FIXTURE_SEED = 20260826

#: Above this, the stack is not reproducing the reference and its embeddings are its own. The same
#: 1e-4 the Sortformer fixture uses, and a threshold with room in it. **On this fixture**, measured
#: 2026-08-27 at :data:`FIXTURE_BATCH`: ORT's CPU provider lands at **1.3784e-07** and WebGPU at
#: **1.8626e-07** against torch, three orders inside it. The end-to-end figures in
#: `docs/UNPROVEN.md` were taken at batch 32 and are close but not identical — 1.2107e-07 and
#: 1.9372e-07 — so quote whichever matches the batch being discussed rather than mixing them. An
#: earlier draft of this comment carried a third pair belonging to neither.
#:
#: **Passing this is not evidence that the labels agree**, and the distance between the two is the
#: reason `auto` does not select the ONNX embedder: the vectors match to 1e-07 and the diarisation
#: still comes out 222 turns against 225. This gate catches a graph that is *wrong*; it cannot catch
#: a graph that is merely *different enough to matter downstream*, because clustering is a step
#: function and no elementwise tolerance sees a threshold being crossed.
TOLERANCE = 1e-4

FIXTURE_NAME = "embedding-parity-reference.npy"


def synthetic_batch() -> tuple[np.ndarray, np.ndarray]:
    """The fixture's input, from a seed.

    Noise rather than speech, for the reason :mod:`.parity` gives: a committed audio file means
    committing audio, with a licence and a megabyte behind it, and it tests nothing the numbers do
    not. The amplitude imitates ordinary recorded speech so the network runs in the regime it was
    trained for rather than somewhere numerically exotic where every provider would disagree.

    The mask is a genuine mixture of on and off frames. All-ones would leave the weighted pooling
    doing an unweighted mean and would not exercise the weights path at all.
    """
    rng = np.random.default_rng(FIXTURE_SEED)
    waveforms = (rng.standard_normal((FIXTURE_BATCH, 1, FIXTURE_SAMPLES)) * 0.05).astype(np.float32)
    masks = (rng.random((FIXTURE_BATCH, FIXTURE_MASK_FRAMES)) > 0.4).astype(np.float32)
    return np.ascontiguousarray(waveforms), np.ascontiguousarray(masks)


def reference_path() -> str:
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), FIXTURE_NAME)


def compute(engine: Any) -> np.ndarray:
    """Runs the fixture through a loaded engine and returns its embeddings."""
    waveforms, masks = synthetic_batch()
    return np.asarray(engine.embed_for_parity(waveforms, masks), dtype=np.float32)


def check(engine: Any) -> dict[str, Any]:
    """Compares this engine's embedder against the committed reference.

    Returns the numbers rather than a verdict alone, because a caller reporting "failed" without
    saying by how much has told the user nothing they can act on.
    """
    path = reference_path()
    if not os.path.isfile(path):
        return {
            "available": False,
            "reason": f"no parity reference committed at {path}",
        }

    expected = np.load(path).astype(np.float64)
    actual = compute(engine).astype(np.float64)

    if expected.shape != actual.shape:
        return {
            "available": True,
            "passed": False,
            "reason": f"shape {actual.shape} against the reference's {expected.shape}",
            "tolerance": TOLERANCE,
        }

    if not np.isfinite(actual).all():
        # Before the subtraction, because a NaN anywhere makes `max` NaN, and a verdict whose
        # difference is NaN is one the channel refuses to send.
        return {
            "available": True,
            "passed": False,
            "reason": f"{int((~np.isfinite(actual)).sum())} of {actual.size} values are not finite",
            "tolerance": TOLERANCE,
        }

    difference = np.abs(expected - actual)
    max_abs = float(difference.max())

    # Clustering compares these by cosine distance, so the angle between the reference embedding and
    # this one says more about what the pipeline will do with them than any elementwise figure. It
    # is reported beside the gate rather than as the gate: it is the consequence, and `maxAbsDiff`
    # is the thing with three orders of measured daylight behind its threshold.
    flat_expected = expected.reshape(expected.shape[0], -1)
    flat_actual = actual.reshape(actual.shape[0], -1)
    norms = np.linalg.norm(flat_expected, axis=1) * np.linalg.norm(flat_actual, axis=1)
    cosine = np.sum(flat_expected * flat_actual, axis=1) / np.maximum(norms, 1e-12)

    return {
        "available": True,
        "passed": max_abs <= TOLERANCE,
        "maxAbsDiff": max_abs,
        "meanAbsDiff": float(difference.mean()),
        "minCosineSimilarity": float(cosine.min()),
        "tolerance": TOLERANCE,
        "embeddings": int(actual.shape[0]),
        "dimension": int(actual.shape[1]),
    }
