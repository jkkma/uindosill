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

#: The two diarisers this sidecar can load, named by the host on `load`. Everything below the
#: `kind` switch in :meth:`Diariser.load` belongs to exactly one of them: Sortformer is a streaming
#: ONNX graph with four speaker slots, DiariZen is an offline torch pipeline that clusters and has
#: no total cap. They share this class, the protocol and nothing else.
SORTFORMER = "sortformer"
DIARIZEN = "diarizen"

#: The cap is architectural: the graph has four speaker slots. A fifth voice is merged into one of
#: the four rather than reported, and the host says so rather than presenting labels as complete.
MAX_SPEAKERS = 4

#: Where the evidence stops, not where the model does. Measured 2026-08-20 by growing a window from
#: a fixed onset: right at 10, 30, 40 and 50 minutes across two episodes, then wrong past an hour.
RELIABLE_UP_TO_SECONDS = 50 * 60

#: What `auto` will settle on, best first, before the CPU. WebGPU and nothing else: it is the one
#: provider that reproduces the published figure, and the `providers` op reports what this resolves
#: to, so what the host is told `auto` picks and what it picks cannot drift apart. CUDA is not in
#: it — decided 2026-08-22 — because it does not reproduce the figure; it is reachable by name.
AUTO_ORDER = ["webgpu"]

#: What `threads: 0` means for this engine — and not "let ONNX Runtime choose", which is what the
#: translator's 0 means. Every CPU figure in this project was measured with 12, so 12 is the number
#: a host that names nothing gets; the host's help says so.
DEFAULT_THREADS = 12


def resolve_auto() -> list[str]:
    """The providers `auto` will try for the diariser on this machine, best first.

    Resolved here rather than by the host, because a host inspecting drivers would be guessing at
    what only ONNX Runtime can answer. Only providers this project has measured are reachable this
    way; the rest must be asked for by name.

    **`get_available_providers()` is a weaker signal than it looks, so this is a shortlist rather
    than an answer.** It reports the providers compiled into the wheel, not the ones this machine can
    create — and since the bundle ships `onnxruntime-webgpu`, `WebGpuExecutionProvider` is in that
    list on every machine, including one with no usable adapter at all. So the candidates are *tried*
    in order by :meth:`Diariser.load`, which moves to the next when a session refuses to build.
    Predicting instead of trying would leave both opt-ins dead on a VM or an RDP session, with the
    CPU path — the reference path — unreachable.

    **Measured is not the same as passing, and CUDA is the case that makes the difference.** It
    *fails* the parity fixture — 8.143e-04 against a threshold of 1e-4 — and measured 2026-08-21 on
    AMI test it moves the number: WebGPU 16.3319% DER against the CPU's 16.3324%, a difference of
    0.0005 points, while CUDA lands at 16.1021%. A provider that reproduces the CPU's answer lets one
    published figure describe every machine; one that does not means the figure describes whoever
    measured it. So CUDA is **not** in this list — decided 2026-08-22, after it had been second in
    it: on a machine with CUDA and no working WebGPU, `auto` now settles on the CPU, the reference
    path, at the CPU's speed, rather than on a provider whose answer is its own. CUDA is reachable
    by name (`--speaker-backend cuda`), and a run that names it is warned on the backend and again on
    the parity result. Until that day the code kept CUDA second and three documents said it was
    out; the documents were right about what this project's rule requires.

    It is also the only provider here that is correct at ONNX Runtime's *default* optimisation level
    — DirectML is catastrophically wrong there — and it needs no CUDA or cuDNN libraries, which is
    about 1.65 GB of installer.
    """
    import onnxruntime as ort

    from .engine import PROVIDERS

    available = set(ort.get_available_providers())
    return [p for p in AUTO_ORDER if PROVIDERS[p][0] in available] + ["cpu"]


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
        self._fell_back_from: list[str] = []
        self._kind: str = SORTFORMER

    @property
    def loaded(self) -> bool:
        return self._engine is not None

    @property
    def kind(self) -> str:
        """Which of the two is loaded, for the handlers that must not treat them alike."""
        return self._kind

    def load(
        self,
        path: str,
        model_id: str,
        threads: int,
        provider: str = "cpu",
        graph_optimization: str | None = None,
        profile: bool = False,
        kind: str = SORTFORMER,
    ) -> dict[str, Any]:
        """Loads one of the two diarisers. `kind` decides which, and the host decides `kind`.

        **Asked for rather than sniffed.** The two are distinguishable by shape — Sortformer is one
        `.onnx` file and DiariZen is a directory of five — and guessing from that would work until
        the day a third entry looked like either. The host already knows which catalogue entry it
        resolved, so it says; a sidecar that inferred it would be a second place the answer lives.
        """
        if kind == DIARIZEN:
            return self._load_diarizen(path, model_id, threads)
        if kind != SORTFORMER:
            raise RequestError("request", f"unknown diariser kind '{kind}'")

        if not path or not os.path.isfile(path):
            raise RequestError("model", f"the diarisation model is not at {path}")

        # Imported here rather than at module scope so that starting the sidecar costs nothing
        # until a model is actually asked for. torch alone is seconds of import.
        from .engine import SortformerEngine

        # See resolve_auto: a shortlist to try rather than a prediction, because whether a provider
        # will build a session is not a question `get_available_providers()` answers.
        #
        # **An explicit provider is one candidate and is never fallen back from.** Somebody who typed
        # `cuda` and silently got the CPU has been told nothing, which is the failure this engine's
        # registration assertion exists to prevent; only `auto` promised a choice, so only `auto`
        # gets to make a second one.
        candidates = resolve_auto() if provider == "auto" else [provider]

        failures = []
        for candidate in candidates:
            try:
                self._engine = SortformerEngine(
                    onnx_path=path,
                    threads=threads or DEFAULT_THREADS,
                    provider=candidate,
                    graph_optimization=graph_optimization,
                    profile=profile,
                )
                break
            except Exception as exc:  # noqa: BLE001
                failures.append(f"{candidate}: {exc}")
        else:
            raise RequestError("model", "could not load the diarisation graph. " + "; ".join(failures))

        # What `auto` passed over on the way to the provider that built, each with its reason —
        # kept for the capabilities rather than only for the case where nothing built, because a
        # run that landed on the CPU is explained by exactly these and the host has no other way to
        # learn them. Capped per entry: an ONNX Runtime message can run to a screenful.
        self._fell_back_from = [failure[:300] for failure in failures]

        self._model_id = model_id or os.path.splitext(os.path.basename(path))[0]
        self._model_path = path
        self._kind = SORTFORMER
        return self.capabilities()

    def _load_diarizen(self, path: str, model_id: str, threads: int) -> dict[str, Any]:
        """The DiariZen arm of :meth:`load`.

        Shorter than the Sortformer arm by everything to do with execution providers: there is no
        provider list to walk, nothing to fall back from and no graph optimisation to set, because
        this pipeline is torch and the bundle's torch is the CPU build. Reporting an empty
        `fellBackFrom` is therefore the truth rather than a stub.
        """
        if not path or not os.path.isdir(path):
            raise RequestError("model", f"the DiariZen model directory is not at {path}")

        from .diarizen import DiarizenEngine

        self._engine = DiarizenEngine(model_dir=path, threads=threads)
        self._model_id = model_id or os.path.basename(os.path.normpath(path))
        self._model_path = path
        self._kind = DIARIZEN
        self._fell_back_from = []
        return self.capabilities()

    def capabilities(self) -> dict[str, Any]:
        if self._kind == DIARIZEN:
            return self._diarizen_capabilities()

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
            # The providers `auto` tried first and could not build, with their reasons; empty when
            # the first candidate built or the provider was named.
            "fellBackFrom": list(self._fell_back_from),
        }

    def _diarizen_capabilities(self) -> dict[str, Any]:
        """What DiariZen can do, in the same vocabulary and with the same refusals to guess.

        Three fields differ from the Sortformer arm and each difference is load-bearing:
        `maxSpeakers` is null because there is no total cap rather than because nobody looked;
        `reliableUpToSeconds` is null because nothing has been measured, which the host renders as
        "no bound established" rather than as "any length"; and `honoursPostProcessing` is false
        because this pipeline binarizes internally at parameters its published figures describe.
        """
        from . import diarizen as engine_module

        engine = self._engine
        return {
            "engineName": "diarizen-torch-python",
            "modelId": self._model_id,
            "backend": getattr(engine, "backend", "cpu"),
            "graphOptimization": None,
            # VBxClustering takes num_clusters, min_clusters and max_clusters and its own comment
            # says "not used but kept for compatibility". A count cannot reach this model, so the
            # host is told so and reports that it folded one afterwards rather than honoured it.
            "supportsFixedSpeakerCount": False,
            "maxSpeakers": engine_module.MAX_SPEAKERS,
            "maxConcurrentSpeakers": engine_module.MAX_CONCURRENT_SPEAKERS,
            "reliableUpToSeconds": engine_module.RELIABLE_UP_TO_SECONDS,
            "honoursPostProcessing": False,
            # Nothing to fall back from: torch, one device, no provider negotiation.
            "fellBackFrom": [],
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

        if self._kind == DIARIZEN:
            # post_processing is deliberately dropped rather than applied. See
            # `diarizen.py`'s module docstring: this pipeline's binarisation is internal, and the
            # host's Sortformer defaults would merge turns the published figures keep apart.
            return self._engine.label(wav_path, progress=progress)

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

        if wav.size == 0:
            # No samples, no turns. The host refuses an empty WAV before it stages one, so this is a
            # container with no frames reaching the sidecar some other way — and the featurizer's
            # `x[:, 0]` on a (1, 0) tensor was an IndexError reported as `internal` until 2026-08-22.
            return []

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
