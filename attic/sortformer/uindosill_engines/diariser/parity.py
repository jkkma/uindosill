"""Does this machine's stack reproduce the diariser the published figures describe?

An execution provider can be catastrophically wrong and indistinguishable from a correct one from
the outside. Measured 2026-08-21: DirectML at ONNX Runtime's default settings scores 53.15%
diarisation error against the CPU's 16.33%, at thirteen times the speed, emitting speaker turns
that look entirely normal. Nothing in that output says it is wrong. A user would never know.

This is the cheap check that catches it. It runs the real streaming loop over a deterministic
synthetic input and compares the probabilities against a committed reference produced on the CPU.

**Why synthetic input rather than a clip.** A committed audio file means committing audio, with a
licence and a megabyte behind it, and it tests nothing the mel numbers do not. The input here comes
from a seed, so the fixture is only the expected *output* — about 24 KB — and there is no corpus to
download and nothing to attribute.

**Why the streaming loop rather than one graph call.** The two failure modes found so far live in
different places. DirectML's is in the graph and shows up on the first chunk; CUDA's is in the
arrival-order speaker cache, which needs history before a tie can be broken differently. One chunk
would catch the first and miss the second, so this runs enough chunks for the cache to matter.

**Where the threshold comes from.** Measured, not chosen. On this project's hardware a faithful
provider lands around 1e-06 (WebGPU 2.7e-06, DirectML unfused 1.6e-06) and a diverging one around
1e-03 after thirty seconds of audio, rising with duration. Three orders of magnitude of daylight,
so the threshold sits at 1e-04 — far above float noise, far below any real divergence.
"""

from __future__ import annotations

import os
from typing import Any

import numpy as np

#: Frames of synthetic mel. Two chunks' worth, so the speaker cache is exercised rather than only
#: the graph — see the module docstring.
FIXTURE_MEL_FRAMES = 6096

#: The seed is part of the fixture: change it and the committed reference means nothing.
FIXTURE_SEED = 20260821

#: Above this, the stack is not reproducing the reference and its labels are its own.
TOLERANCE = 1e-4

FIXTURE_NAME = "parity-reference.npy"


def synthetic_mel() -> np.ndarray:
    """The fixture's input, from a seed.

    Log-mel energies from a real recording are broadly negative with a wide spread; this imitates
    that range so the graph runs in the regime it was trained for rather than somewhere numerically
    exotic where every provider would disagree for uninteresting reasons.
    """
    rng = np.random.default_rng(FIXTURE_SEED)
    mel = rng.standard_normal((FIXTURE_MEL_FRAMES, 128)).astype(np.float32) * 2.5 - 6.0
    return np.ascontiguousarray(mel)


def reference_path() -> str:
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), FIXTURE_NAME)


def compute(engine: Any) -> np.ndarray:
    """Runs the fixture through a loaded engine and returns its probabilities."""
    mel = synthetic_mel()
    return np.asarray(engine.run_mel(mel, FIXTURE_MEL_FRAMES), dtype=np.float32)


def check(engine: Any) -> dict[str, Any]:
    """Compares this engine against the committed reference.

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
        # difference is NaN is one the channel refuses to send — the host would get an error
        # about the reply rather than a failure it can describe.
        return {
            "available": True,
            "passed": False,
            "reason": f"{int((~np.isfinite(actual)).sum())} of {actual.size} probabilities are not finite",
            "tolerance": TOLERANCE,
        }

    difference = np.abs(expected - actual)
    max_abs = float(difference.max())
    flips = float(((expected > 0.5) != (actual > 0.5)).mean() * 100)
    return {
        "available": True,
        "passed": max_abs <= TOLERANCE,
        "maxAbsDiff": max_abs,
        "meanAbsDiff": float(difference.mean()),
        "decisionFlipPercent": flips,
        "tolerance": TOLERANCE,
        "frames": int(actual.shape[0]),
    }
