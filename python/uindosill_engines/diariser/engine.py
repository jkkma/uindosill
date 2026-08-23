"""Streaming driver for the Sortformer ONNX export.

This mirrors SortformerEncLabelModel.forward_streaming / forward_streaming_step, with the ONNX
graph standing in for pre-encode + encoder + head.  The speaker cache is NOT reimplemented here:
`nemostub` lets us import NVIDIA's own SortformerModules and call the real
`streaming_update_async`, so the Arrival-Order Speaker Cache is the reference code, not a port
of it.  What is written here is only the loop around it.

Three things a host must do that the graph does not:
  * chunk the mel exactly as streaming_feat_loader does (asymmetric first and last chunks),
  * trim the fixed 381-frame embedding output back to the pre-encode length of the piece's
    *physical* width — padding included, which is not the valid length the graph reports as
    `elen` — because streaming_update_async derives max_chunk_len from the tensor's physical
    width and clamps the valid chunk_lengths to it (trimmed to `elen` until 2026-08-22, which
    lost one or two frames on 7.3% of durations),
  * apply_mask_to_preds -- forward_for_export omits it, and the packed predictions past the
    total valid length are otherwise fed into the cache as real frames.
"""

import contextlib
import math
import os
import sys

import numpy as np
import onnxruntime as ort
import torch

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "_vendor"))
from nemo.collections.asr.modules.sortformer_modules import SortformerModules  # noqa: E402

from .feats import MelFeaturizer  # noqa: E402

SUBSAMPLING = 8
N_SPK = 4
SPKCACHE_LEN = 188
FIFO_LEN = 40
CHUNK_LEN = 340           # encoder frames of new audio per step
LEFT_CTX = 1
RIGHT_CTX = 40
MEL_FRAMES = 3048         # (LEFT_CTX + CHUNK_LEN + RIGHT_CTX) * SUBSAMPLING
EMB_DIM = 512
FRAME_SEC = SUBSAMPLING * 0.01   # 80 ms per prediction frame


def pre_encode_len(mel_frames: int) -> int:
    """Encoder frames the pre-encoder produces from `mel_frames` of mel: three stride-2
    convolutions, each `floor((n - 1) / 2) + 1`.

    This is the graph's own `elen` — checked against it on the CPU, 2026-08-22, for the chunk
    lengths the loop actually produces: 2720 → 340, 2736 → 342, 2888 → 361, 2904 → 363,
    3040 → 380, 3048 → 381, all equal. Written here as arithmetic rather than read back from the
    graph because the loop needs it for the *physical* width of a piece, which the graph is never
    asked about: `elen` answers for the valid length only.
    """
    n = mel_frames
    for _ in range(3):
        n = (n - 1) // 2 + 1
    return n


def build_modules():
    """SortformerModules with the v2.1 checkpoint's parameters and this export's geometry.

    The scoring constants (pred_score_threshold, the two boost rates, sil_threshold, ...) are
    the checkpoint's; the streaming geometry (chunk_len 340, fifo_len 40, right context 40) is
    the export's `default` variant.  Mixing those two is the failure the export's README warns
    about, so both are named explicitly here rather than left to defaults.
    """
    m = SortformerModules(
        num_spks=N_SPK, dropout_rate=0.5, fc_d_model=EMB_DIM, tf_d_model=192,
        subsampling_factor=SUBSAMPLING,
        spkcache_len=SPKCACHE_LEN, fifo_len=FIFO_LEN, chunk_len=CHUNK_LEN,
        spkcache_update_period=188,
        chunk_left_context=LEFT_CTX, chunk_right_context=RIGHT_CTX,
        spkcache_sil_frames_per_spk=3,
        scores_add_rnd=0, pred_score_threshold=0.25, max_index=99999,
        scores_boost_latest=0.05, sil_threshold=0.2,
        strong_boost_rate=0.75, weak_boost_rate=1.5, min_pos_scores_rate=0.5,
        use_learnable_sil_emb=False,
    )
    m.eval()   # _compress_spkcache injects random noise under self.training
    return m


#: Execution providers the host may ask for. The CPU list is what every published figure in this
#: repository was produced on; the others are opt-in and carry their own measurement.
PROVIDERS = {
    "cpu": ["CPUExecutionProvider"],
    "cuda": ["CUDAExecutionProvider", "CPUExecutionProvider"],
    "dml": ["DmlExecutionProvider", "CPUExecutionProvider"],
    "webgpu": ["WebGpuExecutionProvider", "CPUExecutionProvider"],
}

#: DirectML fuses this graph into a single node at any optimisation level above none, and the fused
#: path computes a different function: measured 2026-08-21 on ONNX Runtime 1.24.4, AMI test DER
#: 53.1522% against the same build's CPU 16.3347%, while running 13x faster and emitting entirely
#: plausible RTTMs. Over the 16 test meetings the fused probabilities differ from the CPU's by up to
#: 0.9996 and flip 23.70% of frame-speaker cells. ORT_DISABLE_ALL is the only lever that moves it —
#: metacommands, dynamic fusion and seven named ORT passes were each disabled individually and every
#: one reproduced the defect to four decimal places.
#:
#: Unfused it scores 16.3319% and is close, but **not to 1.6e-06 and not with nothing flipping**:
#: that pair is the two-chunk parity fixture's number, and on the 16 AMI meetings the same
#: comparison is a maximum difference of 0.4159 with 104 of 1,631,220 frame-speaker cells differing,
#: 101 of them in two meetings. Ordinary float divergence amplified where the speaker cache breaks a
#: tie, in other words, rather than the exactness the fixture suggests. Said here because the
#: difference between "agrees to 1.6e-06" and "agrees except where it does not" is what somebody
#: would decide to relax this default on. Do not relax it without re-scoring AMI.
DEFAULT_GRAPH_OPTIMIZATION = {"dml": "ORT_DISABLE_ALL"}

#: Threads torch is given for the chunk loop, and the reason it is not the default.
#:
#: **Measured 2026-08-23 on the desktop (9950X, 32 logical), WebGPU, 10 minutes of audio.** The loop
#: is one ONNX call per chunk with a small torch state update between them, and the ONNX call is
#: where the wall time is: 0.95 s of a 0.99 s loop, against 0.03 s in `streaming_update_async`.
#: Torch's pool is sixteen threads by default — one per physical core, which nothing here sets —
#: and it *spins* while waiting, so those threads busy-wait through every one of those 0.95 s.
#:
#: The sweep, at 16/8/4/2/1 threads: wall 0.99, 0.98, 0.99, 1.01, 1.01 s — flat — while CPU fell
#: 14.95 -> 7.25 -> 3.41 -> 1.45 -> 0.52 seconds. Sixteen threads bought nothing and cost about
#: fifteen of this machine's cores. Every setting produced bit-identical probabilities.
#:
#: **This is the loop only, and that scoping is the whole point.** `feats.py` is the opposite case:
#: over 30 minutes of audio its wall time is 0.19 s at sixteen threads and 0.94 s at one, a real
#: 5x, so the featurizer keeps the default and this is restored the moment the loop ends.
LOOP_TORCH_THREADS = 1


@contextlib.contextmanager
def torch_threads(count):
    """Runs a block with torch's intra-op pool narrowed, and puts it back afterwards.

    Restored in a `finally` rather than after the block: an engine that raised mid-loop would
    otherwise leave the whole process single-threaded, and the next thing to want threads is the
    featurizer, which is the one stage here that genuinely uses them.
    """
    was = torch.get_num_threads()
    torch.set_num_threads(count)
    try:
        yield
    finally:
        torch.set_num_threads(was)


class SortformerEngine:
    def __init__(self, onnx_path="model/sortformer-default.onnx", threads=12,
                 provider="cpu", graph_optimization=None):
        if provider not in PROVIDERS:
            raise ValueError(f"unknown provider {provider!r}; choose one of {sorted(PROVIDERS)}")

        # onnxruntime-gpu links CUDA and cuDNN DLLs it does not ship. Without this the session
        # falls back to the CPU with the failure written only to stderr — which is precisely the
        # silent fallback the assertion below exists to catch.
        if provider != "cpu":
            ort.preload_dlls()

        so = ort.SessionOptions()
        so.intra_op_num_threads = threads
        level = graph_optimization or DEFAULT_GRAPH_OPTIMIZATION.get(provider, "ORT_ENABLE_ALL")
        so.graph_optimization_level = getattr(ort.GraphOptimizationLevel, level)

        if provider != "cpu":
            # The intra-op pool busy-waits by default, which is right when it is about to be handed
            # more work and wrong when the work is on a GPU. Measured 2026-08-23 on the desktop
            # (9950X, 32 logical), WebGPU, 10 minutes of audio, three runs each: with spinning on
            # the loop cost 23.4 CPU seconds, with it off 15.6 — a third of the CPU was threads
            # waiting — for wall times of 0.96 s and 1.02 s, and that 6% is one outlier (the walls
            # were 0.84/1.01/1.03 against 1.02/1.00/1.02). Probabilities were bit-identical: 0 of
            # 30,000 cells differed and the argmax agreed on all 7,500 frames, which is what one
            # expects from a change to how a thread waits rather than to what it computes.
            #
            # **Not on the CPU provider**, where those threads are the ones doing the arithmetic
            # rather than waiting on somebody else's, and where every published figure in this
            # repository was produced. Nothing has measured what taking their spin away costs there.
            so.add_session_config_entry("session.intra_op.allow_spinning", "0")
            so.add_session_config_entry("session.inter_op.allow_spinning", "0")

        if provider == "dml":
            # DirectML's documented requirements, not preferences: the EP does its own allocation
            # planning and ORT's memory pattern optimiser is incompatible with it.
            so.enable_mem_pattern = False
            so.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL

        self.sess = ort.InferenceSession(onnx_path, so, providers=PROVIDERS[provider])

        # A provider that failed to initialise is dropped and the session runs on the CPU with no
        # error anywhere. That is indistinguishable from success except in the timings, and a
        # mistyped option during the 2026-08-21 study did exactly this and reported a *perfect*
        # result. Refusing here is the difference between a slow run and a wrong belief.
        registered = self.sess.get_providers()
        wanted = PROVIDERS[provider][0]
        if wanted not in registered:
            raise RuntimeError(
                f"asked for {wanted} and onnxruntime registered {registered}. The provider did not "
                "initialise; running on the CPU instead would be silent and is refused.")

        self.provider = provider
        self.graph_optimization = level
        self.feat = MelFeaturizer()
        self.mods = build_modules()

    def mel(self, wav):
        return self.feat(wav)

    @torch.no_grad()
    def run_mel(self, mel, valid_frames, progress=None):
        """mel: [T, 128] float32 (already padded to a multiple of 16).  Returns [F, 4] float32
        speaker probabilities at 80 ms per frame.

        Narrowing torch's pool is done here rather than around `run_wav` because the featurizer is
        the other half of that call and wants the threads this gives up — see LOOP_TORCH_THREADS.
        Here rather than in the caller, too, so that a host driving the loop directly, and the
        parity fixture, both get the same conditions the product runs under.
        """
        with torch_threads(LOOP_TORCH_THREADS):
            return self._chunk_loop(mel, valid_frames, progress)

    @torch.no_grad()
    def _chunk_loop(self, mel, valid_frames, progress=None):
        mods = self.mods
        state = mods.init_streaming_state(batch_size=1, async_streaming=True, device="cpu")

        feat_len = mel.shape[0]
        buf = np.zeros((1, MEL_FRAMES, 128), dtype=np.float32)
        total = []
        stt = end = 0
        step = 0
        n_steps = math.ceil(feat_len / (CHUNK_LEN * SUBSAMPLING))

        while end < feat_len:
            left_offset = min(LEFT_CTX * SUBSAMPLING, stt)
            end = min(stt + CHUNK_LEN * SUBSAMPLING, feat_len)
            right_offset = min(RIGHT_CTX * SUBSAMPLING, feat_len - end)
            piece = mel[stt - left_offset: end + right_offset]
            width = piece.shape[0]
            # streaming_feat_loader: valid length counts from the chunk's own start
            chunk_len_frames = int(np.clip(valid_frames - stt + left_offset, 0, width))
            stt = end

            buf[0, :width] = piece
            buf[0, width:] = 0.0
            preds, embs, elen = self.sess.run(None, {
                "chunk": buf,
                "chunk_lengths": np.array([chunk_len_frames], dtype=np.int64),
                "spkcache": state.spkcache.numpy(),
                "spkcache_lengths": state.spkcache_lengths.numpy(),
                "fifo": state.fifo.numpy(),
                "fifo_lengths": state.fifo_lengths.numpy(),
            })

            lc_enc = round(left_offset / SUBSAMPLING)
            rc_enc = math.ceil(right_offset / SUBSAMPLING)
            n_emb = int(elen[0])
            step += 1
            if progress:
                # Counted before the break below, so a last chunk that is context only still
                # brings the bar to n of n rather than leaving it at n-1.
                progress(step, n_steps)
            if n_emb - lc_enc - rc_enc <= 0:
                break   # nothing but context left; NeMo's loop cannot reach here either

            # Trim to the physical width NeMo would have had — the pre-encode length of the whole
            # piece, padding included — and not to the valid length the graph reports in `elen`.
            # streaming_update_async takes max_chunk_len from chunk.shape[1] and clamps the valid
            # chunk_lengths to it, so the physical width decides how many frames a chunk can
            # contribute and the valid length how many of them are real. The two differ on every
            # file, because the featurizer pads to a multiple of 16 and the STFT is one frame
            # longer than the valid count: trimmed to `elen` until 2026-08-22, the loop lost one or
            # two frames on 7.3% of durations — a 600 s file came out 7,498 frames where 7,500 are
            # due, and the last chunk's rows were concatenated 160 ms early.
            n_phys = pre_encode_len(width)
            chunk_embs = torch.from_numpy(embs[:, :n_phys].copy())
            preds_t = torch.from_numpy(preds.copy())

            # forward_for_export skips apply_mask_to_preds; do it here.
            total_valid = int(state.spkcache_lengths[0]) + int(state.fifo_lengths[0]) + n_emb
            preds_t = mods.apply_mask_to_preds(preds_t, torch.tensor([total_valid]))

            state, chunk_preds = mods.streaming_update_async(
                streaming_state=state,
                chunk=chunk_embs,
                chunk_lengths=torch.tensor([n_emb], dtype=torch.long),
                preds=preds_t,
                lc=lc_enc,
                rc=rc_enc,
            )
            total.append(chunk_preds[0].numpy().copy())

        out = np.concatenate(total, axis=0) if total else np.zeros((0, N_SPK), np.float32)
        return out[: math.ceil(valid_frames / SUBSAMPLING)]

    def run_wav(self, wav, progress=None):
        mel, valid = self.feat(wav)
        return self.run_mel(mel, valid, progress=progress)
