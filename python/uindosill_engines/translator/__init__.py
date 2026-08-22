"""Translation into English, as the sidecar exposes it.

Thin, on the diariser's terms: everything that decodes lives in :mod:`.engine`, and what this module
adds is the shape the host wants — capabilities, a source string in, a translation out.

**The policy is not here and is not supposed to be.** The target token every source must carry, the
refusal of a source past the tokenizer's limit, and the refusal of the word-timed subtitle format
are all decisions, and decisions stayed in C# when the engine crossed the process boundary — the
same division the diariser draws. What this side does is the two things only the model can do:
count the tokens in a string, and translate it. The host is told the count and decides what it
means.

**One request per segment rather than one per batch.** A decode is about half a second and a
protocol line is microseconds, so nothing is bought by batching them, and a great deal is lost: the
host yields each translated segment as it arrives so a long transcript renders while it is being
written, and a batch op would make that impossible without inventing a second way to stream.
"""

from __future__ import annotations

import os
from typing import Any

from ..protocol import RequestError


class Translator:
    """Holds the loaded checkpoint for the life of the sidecar.

    Loaded once and reused across every segment of every file in a batch — which is the whole
    reason the host keeps this process alive instead of spawning one per file. The two graphs come
    to 1.34 GiB and building their sessions is not free.
    """

    def __init__(self) -> None:
        self._engine: Any = None
        self._model_id: str = ""
        self._fell_back_from: list[str] = []

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
        if not path or not os.path.isdir(path):
            raise RequestError("model", f"the translation checkpoint is not at {path}")

        # Imported here rather than at module scope so that starting the sidecar costs nothing until
        # a model is actually asked for. torch and transformers alone are seconds of import, and a
        # run that only diarises should not pay them.
        from .engine import MarianEngine, missing_files, resolve_auto

        absent = missing_files(path)
        if absent:
            raise RequestError(
                "model",
                f"{path} is not a complete translation checkpoint: {', '.join(absent)} "
                f"{'is' if len(absent) == 1 else 'are'} missing. Eight of the checkpoint's nine files "
                "are required — two graphs, two configs and four of the five tokenizer files — and a "
                "partial set loads until it does not.")

        # A shortlist to try rather than a prediction — see resolve_auto. An explicit provider is one
        # candidate and is never fallen back from: somebody who typed `cuda` and silently got the CPU
        # has been told nothing.
        candidates = resolve_auto() if provider == "auto" else [provider]

        failures = []
        for candidate in candidates:
            try:
                self._engine = MarianEngine(
                    model_dir=path,
                    threads=threads or 0,
                    provider=candidate,
                    graph_optimization=graph_optimization,
                )
                break
            except Exception as exc:  # noqa: BLE001
                failures.append(f"{candidate}: {exc}")
        else:
            raise RequestError("model", "could not load the translation graphs. " + "; ".join(failures))

        # What `auto` passed over on the way to the provider that built, with the reasons — kept
        # for the capabilities and not only for the case where nothing built; see the diariser's
        # twin. Capped per entry: an ONNX Runtime message can run to a screenful.
        self._fell_back_from = [failure[:300] for failure in failures]

        self._model_id = model_id or os.path.basename(path.rstrip("/\\"))
        return self.capabilities()

    def capabilities(self) -> dict[str, Any]:
        # The backend is reported rather than assumed, and it travels into the transcript's
        # provenance beside the model id — a translation that cannot say what produced it is one
        # nobody can re-examine. Measured 2026-08-21: WebGPU returns the CPU's own translations on
        # 32 of 32 FLEURS sentences and DirectML on 0 of 32, so which provider ran is not a detail
        # about speed.
        from . import engine as engine_module

        engine = self._engine
        return {
            "engineName": "marian-onnx-python",
            "modelId": self._model_id,
            "backend": getattr(engine, "provider", "cpu"),
            "graphOptimization": getattr(engine, "graph_optimization", None),
            # The tokenizer's own declared limit, read rather than guessed. The host refuses a
            # source past it; this side only says what it is.
            "maxSourceTokens": engine.max_source_tokens if engine is not None else None,
            # The decode, reported so that a transcript's provenance can carry the search that
            # produced it and not only the graph. Six beams is the measured number and not the
            # config file's four; see engine.py.
            "beams": engine_module.NUM_BEAMS,
            "maxNewTokens": engine_module.MAX_NEW_TOKENS,
            "lengthPenalty": engine_module.LENGTH_PENALTY,
            "earlyStopping": engine_module.EARLY_STOPPING,
            # The providers `auto` tried first and could not build, with their reasons; empty when
            # the first candidate built or the provider was named.
            "fellBackFrom": list(self._fell_back_from),
        }

    def translate(self, source: str, max_tokens: int | None = None) -> dict[str, Any]:
        """Counts a source's tokens, and decodes it unless the host's limit says not to.

        The count comes back either way, because it is the number the host's refusal has to name
        and the tokenizer is the only thing that can produce it. When the count is past
        ``max_tokens`` nothing is decoded and ``text`` is absent: a source the host is about to
        refuse is not one worth spending half a second on, and truncating it to fit would return
        fluent English with no sign that half the sentence was dropped — the failure the whole
        contract exists to avoid.
        """
        if self._engine is None:
            raise RequestError("model", "translate was asked for before load")
        if not isinstance(source, str):
            raise RequestError("request", "translate needs a 'source' string")

        tokens = self._engine.count(source)
        if max_tokens is not None and max_tokens > 0 and tokens > max_tokens:
            return {"tokens": tokens, "refused": True}

        return {"tokens": tokens, "text": self._engine.translate(source), "refused": False}
