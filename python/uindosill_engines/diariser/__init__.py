"""Speaker diarisation, as the sidecar exposes it.

Thin on purpose. Everything numerical lives in :mod:`.engine`, :mod:`.feats` and :mod:`.postproc`,
which are the spike's files carried across unchanged — `feats.py` and `postproc.py` byte for byte,
`engine.py` with one import path edited. That fidelity is the point: those files are what the
measured 16.33% AMI figure was produced by, and a rewrite would be a different artefact needing a
different measurement.

What this module adds is only the shape the host wants — capabilities, a WAV in, turns out.
"""

from __future__ import annotations

import os
from typing import Any, Callable

from ..protocol import RequestError

#: The cap is architectural: the graph has four speaker slots. A fifth voice is merged into one of
#: the four rather than reported, and the host says so rather than presenting labels as complete.
MAX_SPEAKERS = 4

#: Where the evidence stops, not where the model does. Measured 2026-08-20 by growing a window from
#: a fixed onset: right at 10, 30, 40 and 50 minutes across two episodes, then wrong past an hour.
RELIABLE_UP_TO_SECONDS = 50 * 60


class Diariser:
    """Holds the loaded model for the life of the sidecar.

    Loaded once and reused across every file in a batch — which is the whole reason the host keeps
    this process alive instead of spawning one per file. The graph is 453 MiB and the session build
    is not free.
    """

    def __init__(self) -> None:
        self._engine: Any = None
        self._model_id: str = ""
        self._model_path: str = ""

    @property
    def loaded(self) -> bool:
        return self._engine is not None

    def load(
        self,
        path: str,
        model_id: str,
        threads: int,
        provider: str = "cpu",
        graph_optimization: str | None = None,
    ) -> dict[str, Any]:
        if not path or not os.path.isfile(path):
            raise RequestError("model", f"the diarisation model is not at {path}")

        # Imported here rather than at module scope so that starting the sidecar costs nothing
        # until a model is actually asked for. torch alone is seconds of import.
        from .engine import SortformerEngine

        # "auto" is resolved here rather than by the host, because the only thing that knows whether
        # a provider will actually initialise is the ONNX Runtime that would have to initialise it.
        # A host inspecting drivers would be guessing at that, and guessing wrong lands on a silent
        # CPU fallback. Only providers whose parity with the CPU has been measured are reachable
        # this way; the rest must be asked for by name.
        if provider == "auto":
            import onnxruntime as ort

            available = set(ort.get_available_providers())

            # WebGPU before CUDA, and not because it is faster — it is not. Measured 2026-08-21 on
            # AMI test: WebGPU 16.3319% DER against the CPU's 16.3347%, a difference of 0.0028
            # points, while CUDA moves the number to 16.1021%. A provider that reproduces the CPU's
            # answer lets one published figure describe every machine; one that does not means the
            # figure describes whoever measured it. CUDA buys 1.6x over WebGPU and costs that.
            #
            # It is also the only provider here that is correct at ONNX Runtime's *default*
            # optimisation level — DirectML is catastrophically wrong there — and it needs no CUDA
            # or cuDNN libraries, which is about 1.65 GB of installer.
            if "WebGpuExecutionProvider" in available:
                provider = "webgpu"
            elif "CUDAExecutionProvider" in available:
                provider = "cuda"
            else:
                provider = "cpu"

        try:
            self._engine = SortformerEngine(
                onnx_path=path,
                threads=threads or 12,
                provider=provider,
                graph_optimization=graph_optimization,
            )
        except Exception as exc:  # noqa: BLE001
            raise RequestError("model", f"could not load the diarisation graph: {exc}") from exc

        self._model_id = model_id or os.path.splitext(os.path.basename(path))[0]
        self._model_path = path
        return self.capabilities()

    def capabilities(self) -> dict[str, Any]:
        # The backend is reported rather than assumed, and it travels into the transcript's
        # provenance beside the model id. Two providers give this graph different probabilities,
        # so a diarisation that cannot say which one produced it is one nobody can re-examine.
        engine = self._engine
        return {
            "engineName": "sortformer-onnx-python",
            "modelId": self._model_id,
            "backend": getattr(engine, "provider", "cpu"),
            "graphOptimization": getattr(engine, "graph_optimization", None),
            # The model estimates the count and cannot be told one. Saying so is what makes
            # --speaker-count report that it was folded afterwards rather than appear to work.
            "supportsFixedSpeakerCount": False,
            "maxSpeakers": MAX_SPEAKERS,
            "reliableUpToSeconds": RELIABLE_UP_TO_SECONDS,
        }

    def label(
        self,
        wav_path: str,
        post_processing: dict[str, Any] | None,
        progress: Callable[[int, int], None] | None = None,
    ) -> list[dict[str, Any]]:
        if self._engine is None:
            raise RequestError("model", "label was asked for before load")
        if not wav_path or not os.path.isfile(wav_path):
            raise RequestError("audio", f"no audio at {wav_path}")

        import numpy as np
        import soundfile as sf_io

        from . import postproc

        try:
            wav, sample_rate = sf_io.read(wav_path, dtype="float32")
        except Exception as exc:  # noqa: BLE001
            raise RequestError("audio", f"could not read {wav_path}: {exc}") from exc

        if wav.ndim > 1:
            wav = wav[:, 0]
        if sample_rate != 16000:
            # The host resamples; reaching here means the two sides disagree about the contract,
            # and guessing would put a silent pitch shift into a measured pipeline.
            raise RequestError("audio", f"expected 16 kHz mono, got {sample_rate} Hz")

        probs = self._engine.run_wav(np.ascontiguousarray(wav), progress=progress)

        options = post_processing or {}
        segments = postproc.to_segments(
            probs,
            onset=float(options.get("onset", 0.5)),
            offset=float(options.get("offset", 0.5)),
            pad_onset=float(options.get("padOnset", 0.05)),
            pad_offset=float(options.get("padOffset", 0.0)),
            min_on=float(options.get("minimumSpeechSeconds", 0.0)),
            min_off=float(options.get("minimumSilenceSeconds", 1.0)),
        )
        return [{"start": start, "end": end, "speaker": f"spk{index}"} for start, end, index in segments]
