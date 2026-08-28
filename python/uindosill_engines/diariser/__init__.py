"""Speaker diarisation, as the sidecar exposes it.

Thin on purpose. Everything numerical lives in :mod:`.pyannote_engine`, which wraps
`pyannote.audio`'s own pipeline rather than reimplementing any part of it.

**One diariser, since 2026-08-27.** This module held a two-arm switch on a `kind` the host sent,
because the sidecar could load either NVIDIA's Streaming Sortformer — an ONNX graph with four
speaker slots — or pyannote's offline torch pipeline. Sortformer was shelved to
`attic/sortformer/` and the switch went with it, so `load` no longer asks which engine it is
loading and the protocol no longer carries the word. What that removed is described in
`attic/README.md`; what it cost is recorded in `docs/UNPROVEN.md`, and the short version belongs
here too, because it governs how anything in this package may be described:

**The 16.33% AMI figure this project published was Sortformer's**, produced by files that are now
in the attic, and it does not transfer to this pipeline by sitting in the directory the old one used
to occupy. What this engine has of its own is **one meeting**: AMI ES2004a scored 14.38% at collar
0.25 and 18.76% at collar 0 on 2026-08-27, returning 5 speakers against a reference 4. A single
meeting is not the sixteen-meeting test set the speaker gate names, so the two numbers must not be
set beside each other and the gate is unmet rather than passed. `docs/UNPROVEN.md` is the record.
"""

from __future__ import annotations

import os
from typing import Any, Callable

from ..protocol import RequestError


def resolve_auto() -> list[str]:
    """What `auto` resolves to for the diariser on this machine.

    A list of one, and it is a list only so that the `providers` op reports the same shape for both
    engines. **This is not the shortlist it used to be.** While the diariser was an ONNX graph,
    `auto` was candidates *tried* in order, because `get_available_providers()` reports what a wheel
    was compiled with rather than what a machine can create and only a session build settles it.
    Nothing here is negotiable: this pipeline is torch, the bundled torch is the CPU build, and
    `auto` is therefore the CPU by construction rather than by election.

    `cuda` remains reachable by name on a machine whose torch build has it — see
    :meth:`PyannoteEngine._resolve_device`, which is also where `webgpu` and `dml` are refused
    rather than quietly treated as the CPU.
    """
    return ["cpu"]


class Diariser:
    """Holds the loaded pipeline for the life of the sidecar.

    Loaded once and reused across every file in a batch — which is the whole reason the host keeps
    this process alive instead of spawning one per file.
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
        provider: str = "auto",
        profile: bool = False,
        batch_size: int | None = None,
    ) -> dict[str, Any]:
        """Loads the pipeline.

        **`path` is a directory**, and it is checked as one. It was a `.onnx` file for the engine
        that left, which is why the sidecar and the host once had to agree about which of two
        meanings the field carried; with one engine the meaning is fixed and the check can be flat.

        **`provider` names a torch device, not an execution provider.** Both neural stages are
        torch on the same device, so there is no half to negotiate separately: `auto` is the CPU,
        `cuda` is reachable by name on a machine whose torch build has it, and `webgpu` and `dml`
        are refused rather than quietly treated as the CPU.

        **`batch_size` of `None` means the model's own value**, which is its config's.
        """
        if not path or not os.path.isdir(path):
            raise RequestError("model", f"the diarisation model directory is not at {path}")

        # Imported here rather than at module scope so that starting the sidecar costs nothing
        # until a model is actually asked for. torch alone is seconds of import.
        from .pyannote_engine import PyannoteEngine

        self._engine = PyannoteEngine(
            model_dir=path, threads=threads, provider=provider, profile=profile,
            batch_size=batch_size,
        )
        self._model_id = model_id or os.path.basename(os.path.normpath(path))
        self._model_path = path
        return self.capabilities()

    def capabilities(self) -> dict[str, Any]:
        """What this engine can do, with every refusal to guess left in place.

        Three fields are null or false on purpose. `maxSpeakers` is null because there is no total
        cap rather than because nobody looked; `reliableUpToSeconds` is null because nothing has
        been measured, which the host renders as "no bound established" rather than as "any
        length"; and `honoursPostProcessing` is false because this pipeline binarizes internally at
        parameters its published figures describe.
        """
        from . import pyannote_engine as engine_module

        engine = self._engine
        return {
            "engineName": "pyannote-torch-python",
            "modelId": self._model_id,
            "backend": getattr(engine, "backend", "cpu"),
            # **False because this engine does not pass a count, not because the model cannot take
            # one.** `VBxClustering.expects_num_clusters = False` means a count is not *required*,
            # not that it is ignored: 4.0.7's `VBxClustering.__call__` clamps to
            # `min_clusters`/`max_clusters` and, when `num_clusters` disagrees with what VBx
            # derived, re-clusters the normalised embeddings with `KMeans(n_clusters=num_clusters)`.
            # So the capability is genuinely available upstream.
            #
            # It is reported False anyway because :meth:`PyannoteEngine.label` calls the pipeline
            # with no `num_speakers`, so no count reaches it on this path — which is a true
            # statement about what this build does. Claiming True would promise a behaviour nothing
            # here has ever exercised. Wiring the host's `--speaker-count` through is the obvious
            # next thing to want; `docs/UNPROVEN.md` carries it as a gap rather than this comment
            # carrying it as a limitation of the model.
            "supportsFixedSpeakerCount": False,
            "maxSpeakers": engine_module.MAX_SPEAKERS,
            "reliableUpToSeconds": engine_module.RELIABLE_UP_TO_SECONDS,
            "honoursPostProcessing": False,
            # **Both stages, both torch, both on the device named here.** They agree by
            # construction; an ONNX embedder is what would separate them again.
            #
            # `embeddingBackend` is read by the host; **`segmentationBackend` is read by nothing**,
            # on either side, and is sent so that a capabilities dump says which runtime ran the
            # half nobody chose.
            "segmentationBackend": getattr(engine, "segmentation_backend", "torch:cpu"),
            "embeddingBackend": getattr(engine, "embedding_backend", "torch:cpu"),
            # **Read off the loaded pipeline, not echoed back from the request.** The host may have
            # sent nothing, in which case this is the config's own value and the host has no other
            # way to learn it; and when the host did send one, this is what confirms it reached the
            # pipeline's batch attributes rather than merely arriving.
            "batchSize": getattr(engine, "batch_size", None),
        }

    def label(
        self,
        wav_path: str,
        post_processing: dict[str, Any] | None,
        progress: Callable[[int, int], None] | None = None,
    ) -> list[dict[str, Any]]:
        """Labels one file.

        `post_processing` is accepted and **deliberately dropped** rather than applied. See
        :mod:`.pyannote_engine`'s module docstring: this pipeline's binarisation is internal, and
        the thresholds the host still knows how to send are the shelved engine's defaults, which
        would merge turns the published figures keep apart. `honoursPostProcessing` reports it, and **nothing on
        the host side reads that field** — `ReadCapabilities` has no case for it and
        `SpeakerLabellerCapabilities` has no such member. It is sent for the same reason
        `segmentationBackend` is: so a capabilities dump says what happened to a field somebody
        supplied. The argument stays in this signature because the protocol still carries it, and
        silently accepting one that had been removed would be the worse of the two shapes.
        """
        if self._engine is None:
            raise RequestError("model", "label was asked for before load")
        if not wav_path or not os.path.isfile(wav_path):
            raise RequestError("audio", f"no audio at {wav_path}")

        return self._engine.label(wav_path, progress=progress)
