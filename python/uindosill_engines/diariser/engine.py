"""Streaming driver for the Sortformer ONNX export.

This mirrors SortformerEncLabelModel.forward_streaming / forward_streaming_step, with the ONNX
graph standing in for pre-encode + encoder + head.  The speaker cache is NOT reimplemented here:
`nemostub` lets us import NVIDIA's own SortformerModules and call the real
`streaming_update_async`, so the Arrival-Order Speaker Cache is the reference code, not a port
of it.  What is written here is only the loop around it.

Three things a host must do that the graph does not:
  * chunk the mel exactly as streaming_feat_loader does (asymmetric first and last chunks),
  * trim the fixed 381-frame embedding output back to chunk_pre_encode_lengths, because
    streaming_update_async derives max_chunk_len from the tensor's physical width,
  * apply_mask_to_preds -- forward_for_export omits it, and the packed predictions past the
    total valid length are otherwise fed into the cache as real frames.
"""

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
        speaker probabilities at 80 ms per frame."""
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
            if n_emb - lc_enc - rc_enc <= 0:
                break   # nothing but context left; NeMo's loop cannot reach here either

            # Trim to the physical width NeMo would have had: streaming_update_async takes
            # max_chunk_len from chunk.shape[1], so a fixed 381 would over-count by one on
            # the first chunk and by more on the last.
            chunk_embs = torch.from_numpy(embs[:, :n_emb].copy())
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
            step += 1
            if progress:
                progress(step, n_steps)

        out = np.concatenate(total, axis=0) if total else np.zeros((0, N_SPK), np.float32)
        return out[: math.ceil(valid_frames / SUBSAMPLING)]

    def run_wav(self, wav, progress=None):
        mel, valid = self.feat(wav)
        return self.run_mel(mel, valid, progress=progress)
