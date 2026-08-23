"""NeMo-faithful mel featurizer for diar_streaming_sortformer_4spk-v2.1.

Every constant here comes from the `preprocessor:` block of model_config.yaml, which was
extracted from the .nemo checkpoint itself (a range request over the tar header), not from a
model card.  The two that are easy to get wrong and expensive to get wrong:

  normalize: NA   -> normalize_batch() falls to its else branch and returns x UNCHANGED.
                     Most NeMo ASR configs say per_feature.  This one does not normalise.
  dither: 1e-5    -> FilterbankFeatures applies dither only under `self.training`.
                     At inference it is a no-op, so features are deterministic.

Reference: nemo/collections/asr/parts/preprocessing/features.py, FilterbankFeatures.forward.
"""

import librosa
import numpy as np
import torch

SAMPLE_RATE = 16000
WINDOW_SIZE = 0.025          # -> 400 samples
WINDOW_STRIDE = 0.01         # -> 160 samples
N_FFT = 512
N_MELS = 128
PREEMPH = 0.97
LOG_ZERO_GUARD = 2.0 ** -24  # log_zero_guard_type "add"
MAG_POWER = 2.0
MEL_NORM = "slaney"
PAD_TO = 16
LOWFREQ = 0
HIGHFREQ = SAMPLE_RATE / 2

N_WINDOW_SIZE = int(WINDOW_SIZE * SAMPLE_RATE)      # 400
N_WINDOW_STRIDE = int(WINDOW_STRIDE * SAMPLE_RATE)  # 160

#: Frames per STFT block — about 82 s of audio, 17 MB of complex spectrum — chosen for memory and
#: nothing else: the block boundary is invisible to the result, because every frame sees only its
#: own n_fft samples.
STFT_BLOCK_FRAMES = 8192


class MelFeaturizer:
    def __init__(self):
        # periodic=False is NeMo's choice (torch_windows[window](win_length, periodic=False)).
        # librosa's own stft default is periodic=True; using it here would be a silent mismatch.
        self.window = torch.hann_window(N_WINDOW_SIZE, periodic=False)
        fb = librosa.filters.mel(
            sr=SAMPLE_RATE, n_fft=N_FFT, n_mels=N_MELS,
            fmin=LOWFREQ, fmax=HIGHFREQ, norm=MEL_NORM,
        )
        self.fb = torch.tensor(fb, dtype=torch.float32)

    @staticmethod
    def seq_len(n_samples: int) -> int:
        """FilterbankFeatures.get_seq_len: floor((L + n_fft//2*2 - n_fft) / hop) == floor(L/hop)."""
        pad_amount = (N_FFT // 2) * 2
        return (n_samples + pad_amount - N_FFT) // N_WINDOW_STRIDE

    def __call__(self, wav: np.ndarray, pad_to_multiple: bool = True):
        """wav: float32 mono at 16 kHz.  Returns (feats [T, 128] float32, valid_frames int)."""
        x = torch.from_numpy(np.ascontiguousarray(wav, dtype=np.float32)).unsqueeze(0)
        n_samples = x.shape[1]
        valid = self.seq_len(n_samples)

        # no dither: FilterbankFeatures gates it on self.training

        # preemphasis
        x = torch.cat((x[:, 0].unsqueeze(1), x[:, 1:] - PREEMPH * x[:, :-1]), dim=1)

        # The STFT in hop-aligned blocks rather than over the whole file at once. Whole-file, the
        # complex spectrum (257 bins x 2 x 4 bytes per 10 ms frame) and the intermediates behind
        # `pow`, `sum`, `sqrt`, `pow` and the filterbank matmul are all alive together — measured
        # 2026-08-22 on the bundled torch, 60 min of audio peaked 2,337 MB above the working set it
        # started from, about 665 kB per second of audio, where the mel itself is 50 kB/s. Each block
        # here sees exactly the samples its frames would have seen under `center=True` — the signal
        # zero-padded by n_fft/2 at each end, as `pad_mode="constant"` pads it, and a frame at every
        # hop — so the transform is the same one frame by frame, and it is held to the whole-file
        # result bit for bit before it is trusted (PHASES, 2026-08-22).
        half = N_FFT // 2
        padded = torch.nn.functional.pad(x, (half, half), mode="constant", value=0.0)
        frames = 1 + (padded.shape[1] - N_FFT) // N_WINDOW_STRIDE   # == 1 + n_samples // hop
        rows = frames
        if pad_to_multiple and PAD_TO > 0 and frames % PAD_TO != 0:
            rows = frames + (PAD_TO - frames % PAD_TO)

        # Written straight into the (frames, mels) layout the caller wants, with the multiple-of-16
        # padding rows allocated here and left at zero, so the result is handed over without a
        # transposed copy of itself; and the pre-emphasised signal goes once it has been padded.
        out = torch.zeros((rows, N_MELS), dtype=torch.float32)
        del x
        for start in range(0, frames, STFT_BLOCK_FRAMES):
            stop = min(frames, start + STFT_BLOCK_FRAMES)
            first_sample = start * N_WINDOW_STRIDE
            last_sample = (stop - 1) * N_WINDOW_STRIDE + N_FFT
            spec = torch.stft(
                padded[:, first_sample:last_sample], n_fft=N_FFT, hop_length=N_WINDOW_STRIDE,
                win_length=N_WINDOW_SIZE, center=False, window=self.window, return_complex=True,
            )
            spec = torch.view_as_real(spec)
            mag = torch.sqrt(spec.pow(2).sum(-1))          # guard=0: use_grads is False
            power = mag.pow(MAG_POWER)
            block = torch.matmul(self.fb, power)
            out[start:stop] = torch.log(block[0] + LOG_ZERO_GUARD).transpose(0, 1)

        # normalize: "NA" -> normalize_batch returns x unchanged.  Nothing here on purpose.

        # mask frames at or beyond seq_len to pad_value 0; the rows past `frames` are the
        # multiple-of-16 padding and were never written.
        if valid < frames:
            out[valid:frames] = 0.0
        else:
            valid = frames

        return out.numpy(), int(valid)
