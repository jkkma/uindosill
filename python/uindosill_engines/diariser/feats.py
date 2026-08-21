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

        spec = torch.stft(
            x, n_fft=N_FFT, hop_length=N_WINDOW_STRIDE, win_length=N_WINDOW_SIZE,
            center=True, window=self.window, return_complex=True, pad_mode="constant",
        )
        spec = torch.view_as_real(spec)
        mag = torch.sqrt(spec.pow(2).sum(-1))          # guard=0: use_grads is False
        power = mag.pow(MAG_POWER)
        mel = torch.matmul(self.fb, power)
        mel = torch.log(mel + LOG_ZERO_GUARD)

        # normalize: "NA" -> normalize_batch returns x unchanged.  Nothing here on purpose.

        # mask frames at or beyond seq_len to pad_value 0
        t = mel.shape[-1]
        if valid < t:
            mel[:, :, valid:] = 0.0
        else:
            valid = t

        if pad_to_multiple and PAD_TO > 0:
            rem = mel.shape[-1] % PAD_TO
            if rem != 0:
                mel = torch.nn.functional.pad(mel, (0, PAD_TO - rem), value=0.0)

        return mel[0].transpose(0, 1).contiguous().numpy(), int(valid)
