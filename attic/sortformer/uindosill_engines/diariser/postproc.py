"""Turn cached [F, 4] Sortformer probabilities into an RTTM.

The knobs and their order are NeMo's `ts_vad_post_processing` -- `binarization` then
`filtering` from nemo/collections/asr/parts/utils/vad_utils.py -- so a tuned result here stays
comparable to NVIDIA's own published AMI figures, which were produced with post-processing.

Two things were checked at source rather than assumed, because both are easy to get backwards:

  * Ordering. `filtering` defaults to filter_speech_first=1.0: drop short SPEECH first
    (min_duration_on), then fill short NON-SPEECH gaps (min_duration_off).  Doing it the other
    way round lets a gap-fill rescue a segment that should have been deleted.
  * Naming. The comments in NeMo's own post_processing YAMLs have min_duration_on and
    min_duration_off swapped relative to the docstring in `filtering`.  The docstring and the
    code agree with each other, so the code is what is followed here.

Resolution: NeMo repeat_interleaves the 80 ms predictions to 10 ms before binarizing.  For a
hysteresis with offset <= onset that is a no-op -- repeating a value cannot move a crossing --
so this works on the 80 ms grid directly and lands on the same boundaries.
"""
import numpy as np

FRAME_SEC = 0.08   # 8x subsampling of 10 ms hops


def binarize(prob, onset, offset):
    """Hysteresis, as NeMo's binarization: open above `onset`, close below `offset`.

    Vectorised.  Since offset <= onset, `p > onset` and `p < offset` are mutually exclusive, so
    the state only ever changes on a frame satisfying one of them: collect those frames, drop
    the runs that repeat the current state, and the survivors alternate open/close.  Checked
    against the scalar loop on random and real sequences in postproccheck.py.
    """
    a = prob > onset
    b = prob < offset
    idx = np.flatnonzero(a | b)
    if idx.size == 0:
        return []
    sign = np.where(a[idx], 1, -1)
    keep = np.empty(sign.size, bool)
    keep[0] = True
    np.not_equal(sign[1:], sign[:-1], out=keep[1:])
    idx, sign = idx[keep], sign[keep]
    if sign[0] == -1:                       # closing event before any opening
        idx, sign = idx[1:], sign[1:]
    starts = idx[::2]
    ends = idx[1::2]
    if ends.size < starts.size:             # still speaking at the end of the recording
        ends = np.append(ends, prob.size)
    return [(s * FRAME_SEC, e * FRAME_SEC) for s, e in zip(starts.tolist(), ends.tolist())]


def pad_and_merge(segs, pad_onset, pad_offset, limit):
    """Pad each side, then merge whatever the padding made overlap (merge_overlap_segment)."""
    segs = [(max(0.0, a - pad_onset), min(limit, b + pad_offset)) for a, b in segs]
    return _merge(segs, 0.0)


def _merge(segs, gap):
    if not segs:
        return []
    segs = sorted(segs)
    out = [list(segs[0])]
    for a, b in segs[1:]:
        if a - out[-1][1] <= gap:
            out[-1][1] = max(out[-1][1], b)
        else:
            out.append([a, b])
    return [tuple(s) for s in out]


def drop_short(segs, min_on):
    return [s for s in segs if s[1] - s[0] >= min_on] if min_on > 0 else segs


def fill_gaps(segs, min_off):
    return _merge(segs, min_off) if min_off > 0 else segs


def to_segments(probs, onset=0.5, offset=0.5, pad_onset=0.0, pad_offset=0.0,
                min_on=0.0, min_off=0.0):
    """probs: [F, 4].  Returns [(start, end, speaker_index)] over all four columns."""
    limit = probs.shape[0] * FRAME_SEC
    out = []
    for c in range(probs.shape[1]):
        s = binarize(probs[:, c].astype(np.float32), onset, offset)
        s = pad_and_merge(s, pad_onset, pad_offset, limit)
        s = drop_short(s, min_on)      # filter_speech_first = 1.0
        s = fill_gaps(s, min_off)
        out += [(a, b, c) for a, b in s]
    return sorted(out)


def write_rttm(segs, file_id, path):
    with open(path, "w", newline="\n") as f:
        for a, b, c in segs:
            f.write(f"SPEAKER {file_id} 1 {a:.3f} {b - a:.3f} <NA> <NA> spk{c} <NA> <NA>\n")
