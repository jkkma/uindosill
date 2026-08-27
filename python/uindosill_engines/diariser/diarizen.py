"""DiariZen, as the sidecar drives it: WavLM + Conformer segmentation, wespeaker embeddings, VBx.

The second diariser, added beside :mod:`.engine`'s Sortformer rather than in place of it. It answers
a question Sortformer's geometry cannot: **how many voices are in this recording**. Sortformer has
four speaker slots and `docs/PHASES.md` records what that costs on this project's own material —
"all four episodes returned four labels whether there were 2, 3, 5 or 7". DiariZen clusters instead
of tracking, so the count is an output rather than a ceiling.

**Its "4" is a different four, and confusing the two is the mistake this paragraph exists to stop.**
`max_speakers_per_chunk` in the v2 checkpoint's `config.toml` is four voices *talking at once inside
one 16-second window*. The pipeline's own `max_speakers` is 20, and the clustering is not given a
count at all. So there is no total cap here, and :data:`MAX_SPEAKERS` is None — which is what the
host renders as "no such limit" rather than as a number nobody measured.

**Three things this engine does not share with the Sortformer one, all of them structural:**

* **It is torch by default, and half of it can be ONNX Runtime.** Segmentation is always torch;
  the speaker embedder takes an execution provider, has its own parity fixture
  (:mod:`.embedding_parity`) and gives :mod:`..placement` a session to count nodes in. **`auto`
  resolves to torch**, because the ONNX embedder reproduces the embedding vectors to 1e-07 and
  still moves the labels — see `diariser/__init__.py`'s `_load_diarizen`. The backend reported is
  therefore the *embedder's* provider, and on the shipping default that is the torch CPU path.
  This bullet said the opposite until 2026-08-27, which is how an adversarial review found it.
* **It is offline, not streaming.** Sortformer walks the file in chunks with a speaker cache;
  this reads the whole file, embeds every chunk, then clusters globally. That is why it has no
  duration drift to bound — and equally why it holds the whole embedding set in memory.
* **Its binarisation is internal and the host's post-processing does not reach it.** DiariZen's
  own `__call__` binarizes at onset 0.5, offset 0.5 with no minimum durations, and the published
  figures describe that. Applying this project's Sortformer defaults on top — a one-second minimum
  silence, which merges turns — would produce output no measurement describes. So the options are
  reported as not honoured rather than quietly applied, through the `honoursPostProcessing` field
  of `Diariser._diarizen_capabilities`, and dropped rather than passed on by `Diariser.label`.

**Weights are two upstream artefacts under two licences**, which is why the catalogue entry carries
both notices: the DiariZen checkpoint is CC BY-NC 4.0 (non-commercial) and the wespeaker embedding
model is CC BY 4.0. `docs/LICENSING.md` is the record.
"""

from __future__ import annotations

import os
from typing import Any, Callable

from ..protocol import RequestError

#: No total speaker cap. Not "unknown" and not 20: the clustering is never given a count, and the
#: config's `max_speakers = 20` bounds the instantaneous-count clamp rather than the number of
#: clusters VBx may return. None is what the host renders as "no such limit".
MAX_SPEAKERS = None

#: Voices that may overlap *inside one 16-second window*, from the v2 checkpoint's config. A fifth
#: simultaneous talker in one window is not separated there; it says nothing about the file's total.
MAX_CONCURRENT_SPEAKERS = 4

#: Where the evidence stops. **No accuracy figure exists for this model on any recording**, so
#: there is no bound to state — which is not the same as "any length". Its output has been *looked
#: at* since: turn and speaker counts over the ten-minute dev stretches, and a backend comparison on
#: 2026-08-26. None of that is an accuracy measurement, because the stretches carry no reference
#: labels — `stretches.json` marks them `"labelled": false`, and a DER scored against the RTTMs
#: beside them was withdrawn on 2026-08-27 when those turned out to be a previous run's output.
#: A bound here needs a corpus with references, which means AMI.
RELIABLE_UP_TO_SECONDS = None

#: What `threads: 0` means here. The same 12 the Sortformer engine uses, so that a CPU comparison
#: between the two is a comparison of models rather than of thread counts.
DEFAULT_THREADS = 12

#: Windows of audio the segmentation and embedding passes batch together. **Upstream's config
#: says 32 and this project runs 8**, which is a deviation from the published artefact and so is
#: measured rather than asserted. Swept 2026-08-26 over the ten-minute `two-hosts-three-guests-a`
#: on the shipping stack, three arms:
#:
#:     batch  8   peak 3,936 MiB   RTF 0.8486   225 turns   5 speakers
#:     batch 16   peak 6,825 MiB   RTF 0.9275   225 turns   5 speakers
#:     batch 32   peak 11,740 MiB  RTF 0.9860   225 turns   5 speakers
#:
#: **The labels do not move** -- identical turns and speakers at every size -- which is what makes
#: this a free choice rather than a trade, and what would have stopped it had they moved. The
#: default is worse on both axes: a third of the memory and about 14% faster at 8, because the
#: largest batch peaks near 11.7 GB on a 16 GB machine and the slowdown is what that costs.
#:
#: Applied here rather than by editing the installed `config.toml`, deliberately: that file is the
#: upstream artefact the catalogue pins by digest, and a copy this project had rewritten would no
#: longer be the thing the entry's SHA-256 describes. The deviation belongs in this project's own
#: code, next to the three torch shims, where a reader looking for what differs will find it.
BATCH_SIZE = 8

#: The files a model directory must hold. `plda.npz` and `xvec_transform.npz` sit beside the rest
#: rather than in the `plda/` subdirectory the upstream repository uses: the model catalogue refuses
#: a `fileName` that is not a bare file name, and `vbx_setup` reads both by name from whatever
#: directory it is handed, so pointing it at the model directory costs nothing and keeps the
#: catalogue's invariant intact.
CONFIG_FILE = "config.toml"
WEIGHTS_FILE = "pytorch_model.bin"
PLDA_FILES = ("plda.npz", "xvec_transform.npz")

#: The wespeaker embedding checkpoint, renamed on download so that one directory holds two upstream
#: repositories' files without a collision. Both upstreams call their weights `pytorch_model.bin`.
#:
#: **The `pyannote-` prefix is load-bearing and must not be tidied away.**
#: `pyannote.audio.pipelines.speaker_verification.PretrainedSpeakerEmbedding` chooses a loader by
#: substring-matching the *whole path string* it is handed, testing ``"pyannote"`` before
#: ``"wespeaker"``. This file is a torch checkpoint and needs the pyannote branch; a name carrying
#: only "wespeaker" sends it to the ONNX branch, which demands an `onnxruntime` the bundle's
#: DiariZen stack does not import and fails at load with a message about a missing package rather
#: than about a name. Upstream never meets this because its own path is the HuggingFace cache
#: directory ``models--pyannote--wespeaker-...``, which contains both words and matches the first.
EMBEDDING_FILE = "pyannote-wespeaker-voxceleb-resnet34-LM.bin"

REQUIRED_FILES = (CONFIG_FILE, WEIGHTS_FILE, EMBEDDING_FILE, *PLDA_FILES)


def _prepare_imports() -> None:
    """Put the vendored fork on the path and restore the three APIs torch 2.13 took away.

    **Both halves exist so that the vendored copy can stay byte-identical to upstream.** DiariZen's
    published figures describe *that* source; a patched copy would be a different artefact needing
    its own measurement, which is the same rule `diariser/engine.py` follows for NVIDIA's modules.
    So nothing under `_vendor/` is edited and every incompatibility is repaired from here, where it
    is named, dated and testable.

    **The namespace half.** `pyannote` is a namespace shared with the PyPI `pyannote.core`,
    `.database`, `.metrics` and `.pipeline` distributions, and those ship an `nspkg.pth` that fixes
    `pyannote.__path__` at interpreter start-up -- *before* any `sys.path` entry this module could
    add. So a vendored `pyannote/audio` is simply never looked for, and the failure is a bare
    `ModuleNotFoundError: No module named 'pyannote.audio'` with the directory sitting right there.
    Appending to the package's own `__path__` is what makes the two halves of the namespace meet;
    the four PyPI ones keep resolving out of site-packages, which was checked rather than assumed.

    **The torch half.** pyannote-audio 3.1.1 was written against torchaudio 2.1 and this project's
    bundle pins torch 2.13, whose torchaudio is 2.11. Three things it reaches for are gone, and they
    surface one at a time -- each only after the previous is fixed:

      * ``torchaudio.AudioMetaData`` was deleted outright (no mention anywhere in the 2.11 wheel).
      * ``torch.load`` flipped its ``weights_only`` default to True in torch 2.6, and the DiariZen
        checkpoint is a pickle carrying a ``TorchVersion``. It is forced rather than defaulted here
        because Lightning passes the argument explicitly, so a ``setdefault`` is ignored.
      * ``torchaudio.load`` now delegates to TorchCodec, which the bundle does not carry and which
        wants FFmpeg shared libraries of its own. `soundfile` is already a bundle dependency and the
        host always writes 16 kHz mono PCM, so it reads everything this engine is handed.

    **Measured, not assumed:** with these three in place and speechbrain 1.1.0, the engine produced
    turn-for-turn identical labels on numpy 2.5.2 / torch 2.13.0 to the spike's numpy 1.26.4 /
    torch 2.5.1 -- 19 turns and 3 speakers on the same clip. `docs/UNPROVEN.md` records it.
    """
    import os
    import sys

    vendor = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "_vendor"))
    if vendor not in sys.path:
        sys.path.insert(0, vendor)

    import pyannote

    vendored_pyannote = os.path.join(vendor, "pyannote")
    if vendored_pyannote not in pyannote.__path__:
        pyannote.__path__.append(vendored_pyannote)

    import numpy as np
    import soundfile as sf_io
    import torch
    import torchaudio

    if not hasattr(torchaudio, "AudioMetaData"):
        from dataclasses import dataclass

        @dataclass
        class AudioMetaData:
            sample_rate: int
            num_frames: int
            num_channels: int
            bits_per_sample: int
            encoding: str

        torchaudio.AudioMetaData = AudioMetaData

    if not getattr(torch.load, "_uindosill_weights_only_off", False):
        _real_load = torch.load

        def _load(*args, **kwargs):
            kwargs["weights_only"] = False
            return _real_load(*args, **kwargs)

        _load._uindosill_weights_only_off = True
        torch.load = _load

    if not getattr(torchaudio.load, "_uindosill_via_soundfile", False):
        def _audio_load(path, *args, **kwargs):
            data, rate = sf_io.read(str(path), dtype="float32", always_2d=True)
            return torch.from_numpy(np.ascontiguousarray(data.T)), rate

        _audio_load._uindosill_via_soundfile = True
        torchaudio.load = _audio_load


class DiarizenEngine:
    """One loaded DiariZen pipeline, held for the life of the sidecar.

    Construction is the expensive part — a 265 MiB checkpoint through `torch.load` and a wespeaker
    model beside it — so the sidecar builds this once per batch, exactly as it does the Sortformer
    session.
    """

    def __init__(
        self,
        model_dir: str,
        threads: int,
        provider: str = "torch",
        profile: bool = False,
    ) -> None:
        missing = [name for name in REQUIRED_FILES if not os.path.isfile(os.path.join(model_dir, name))]
        if missing:
            raise RequestError(
                "model",
                f"the DiariZen model directory {model_dir} is missing {', '.join(sorted(missing))}",
            )

        import torch

        # Named rather than left to torch's default, which is every core on the machine. Every CPU
        # figure this project publishes for a diariser was taken at 12, and a default that varies
        # by machine makes two runs incomparable for a reason nobody records.
        torch.set_num_threads(threads or DEFAULT_THREADS)

        from pathlib import Path

        _prepare_imports()

        from diarizen.pipelines.inference import DiariZenPipeline

        self._pipeline = DiariZenPipeline(
            diarizen_hub=Path(model_dir),
            embedding_model=os.path.join(model_dir, EMBEDDING_FILE),
        )

        # The flattening described at PLDA_FILES. `plda_dir` is read at clustering time rather than
        # at construction, so overriding it here is enough and there is no re-instantiate to do.
        self._pipeline.clustering.plda_dir = model_dir

        # Both passes, because `SpeakerDiarization.__init__` takes them as two arguments and bakes
        # the segmentation one into its `Inference`. Setting only the attribute would change the
        # embedding half and silently not the other, which is the failure this line's shape avoids.
        self._pipeline.segmentation_batch_size = BATCH_SIZE
        self._pipeline.embedding_batch_size = BATCH_SIZE
        self._pipeline._segmentation.batch_size = BATCH_SIZE

        self.threads = threads or DEFAULT_THREADS
        self.device = str(next(self._pipeline._segmentation.model.parameters()).device)

        # The segmentation half never moves. Measured 2026-08-26: exported to ONNX it is faithful on
        # ORT's CPU provider (1.7e-05) and no faster anywhere — torch CPU, ORT CPU and ORT WebGPU
        # all land within about 10% of each other, which is this machine's own run-to-run variance —
        # and on WebGPU it is additionally wrong on this checkpoint and loses the device at a batch
        # of 8 against the pipeline's configured 32. See `embedding_onnx` for the whole finding.
        self.segmentation_backend = f"torch:{self.device}"
        self.embedding_backend = "torch:cpu"
        self._embedding_session: Any = None
        self._torch_embedder = self._pipeline._embedding
        self._model_dir = model_dir
        self._profile = profile
        self.embedding_fallback_reason = ""

        if provider != "torch":
            self.install_embedding_provider([provider])

    def install_embedding_provider(self, candidates: list[str]) -> list[str]:
        """Try each provider in order and keep the first that builds; return those passed over.

        The pipeline is not rebuilt between attempts. Only the embedder differs, and re-reading a
        265 MiB checkpoint to change which execution provider a 26 MiB graph runs on would be the
        expensive half of the work done for none of the benefit.

        Falls back to torch when the list runs out, which is why this returns reasons rather than
        raising — see :meth:`_install_onnx_embedder`.
        """
        failures: list[str] = []
        for candidate in candidates:
            if candidate == "torch":
                self.embedding_fallback_reason = ""
                return failures
            reason = self._install_onnx_embedder(candidate)
            if not reason:
                self.embedding_fallback_reason = ""
                return failures
            failures.append(reason)
        self.embedding_fallback_reason = failures[-1] if failures else ""
        return failures

    def _install_onnx_embedder(self, provider: str) -> str:
        """Put the wespeaker embedder on ONNX Runtime; return "" or the reason it stayed in torch.

        **This returns a reason rather than raising, and the caller decides what that means.** As
        the code stands the caller always turns a non-empty reason into a `RequestError`, because
        the only way to reach here is to *name* a provider — `auto` resolves to `torch` and never
        attempts an export — and a named provider is never fallen back from: somebody who typed
        `webgpu` and silently got torch has been told nothing.

        The two-step shape is kept anyway because it is what lets `install_embedding_provider` walk
        a list, which is what `auto` would need the day the ONNX embedder earns its place in it. An
        earlier revision of this docstring claimed the fall-back-to-torch outcome was live behaviour;
        it was not reachable in any configuration, and an adversarial review said so on 2026-08-27.

        Either way the engine is left consistent: on failure the torch embedder is restored, the
        session dropped and :attr:`embedding_backend` set back to `torch:cpu`, so a caller that
        chooses to continue gets a working pipeline rather than a half-installed one.
        """
        from . import embedding_onnx

        try:
            import onnxruntime as ort

            from .engine import PROVIDERS

            if provider not in PROVIDERS:
                raise ValueError(f"unknown provider '{provider}'")

            graph_path = embedding_onnx.ensure_graph(
                self._model_dir,
                os.path.join(self._model_dir, EMBEDDING_FILE),
                self._torch_embedder.model_,
            )

            # onnxruntime-gpu links CUDA and cuDNN DLLs it does not ship. Without this the session
            # falls back to the CPU with the failure written only to stderr — which is the silent
            # fallback the registration assertion below exists to catch. The sibling engine does
            # this for the same provider table and this path did not until 2026-08-27, so
            # `--speaker-backend cuda` could fail here while working there.
            if provider != "cpu":
                ort.preload_dlls()

            options = ort.SessionOptions()

            # Set for every provider, not only the CPU one. A non-CPU session still runs its
            # CPU-resident nodes — ONNX Runtime places shape operators there deliberately — and
            # leaving those at ORT's machine default is exactly the "a default that varies by
            # machine makes two runs incomparable" problem `DEFAULT_THREADS` exists to prevent.
            options.intra_op_num_threads = self.threads

            if self._profile:
                from .. import placement

                placement.enable(options)

            session = ort.InferenceSession(graph_path, options, providers=PROVIDERS[provider])

            # The same assertion the Sortformer engine makes: a session that quietly fell back to
            # the CPU is indistinguishable from success except in the timings.
            wanted = PROVIDERS[provider][0]
            if wanted not in session.get_providers():
                raise RuntimeError(
                    f"{wanted} did not register; ONNX Runtime built the session with "
                    f"{session.get_providers()}"
                )

            self._embedding_session = session
            self._pipeline._embedding = embedding_onnx.OnnxSpeakerEmbedding(
                self._torch_embedder, session, provider
            )
            self.embedding_backend = f"onnxruntime:{provider}"
            return ""
        except Exception as exc:  # noqa: BLE001
            # Leave the pipeline exactly as it was. A half-installed embedder — a session that built
            # but a graph that will not run — would be worse than none, because it would fail later,
            # per file, inside the pipeline.
            self._pipeline._embedding = self._torch_embedder
            self._embedding_session = None
            self.embedding_backend = "torch:cpu"
            return f"{provider}: {exc}"[:300]

    #: Set when an ONNX embedder was asked for and could not be built; empty otherwise.
    embedding_fallback_reason: str = ""

    def sessions_by_part(self) -> dict[str, Any]:
        """The ONNX sessions this engine owns, for `placement` to count nodes in.

        Empty when the embedder is in torch, which is what tells `placement` there is nothing to
        measure rather than leaving it to fail on a missing attribute.
        """
        return {"embedding": self._embedding_session} if self._embedding_session else {}

    def embed_for_parity(self, waveforms: Any, masks: Any) -> Any:
        """Runs the fixture's batch through whichever embedder is installed.

        Lives here rather than in `embedding_parity` so that the fixture never has to know which of
        the two paths it is measuring — that is the engine's business, and the point of the check is
        that both answer the same.
        """
        import numpy as np
        import torch

        return self._pipeline._embedding(
            torch.from_numpy(np.asarray(waveforms, dtype="float32")),
            masks=torch.from_numpy(np.asarray(masks, dtype="float32")),
        )

    @property
    def backend(self) -> str:
        """Where the work happens, as one name the host can parse.

        **It reports the embedder's provider, and that is a choice worth stating.** This pipeline has
        two neural stages on two different runtimes now, so no single word is the whole truth; the
        fields :attr:`segmentation_backend` and :attr:`embedding_backend` carry the whole truth and
        the capabilities send both. What this returns is the half that is *chosen* — segmentation is
        torch on the CPU on every machine and always will be — because the host's question when it
        reads `backend` is which execution provider needs checking against the reference, and the
        answer is only ever this one.
        """
        if self._embedding_session is None:
            return "cpu"
        return self.embedding_backend.split(":", 1)[1]

    def label(
        self,
        wav_path: str,
        progress: Callable[[int, int], None] | None = None,
    ) -> list[dict[str, Any]]:
        """Turns for one 16 kHz mono WAV, in time order, with the pipeline's own cluster labels."""
        import soundfile as sf_io

        try:
            info = sf_io.info(wav_path)
        except Exception as exc:  # noqa: BLE001
            raise RequestError("audio", f"could not read {wav_path}: {exc}") from exc

        if info.samplerate != 16000:
            # The same contract the Sortformer engine asserts: the host resamples, and reaching here
            # means the two sides disagree rather than that this engine should guess.
            raise RequestError("audio", f"expected 16 kHz mono, got {info.samplerate} Hz")

        if info.frames == 0:
            return []

        self._install_progress(progress)
        try:
            annotation = self._pipeline(wav_path)
        finally:
            self._restore_progress()

        order: dict[str, str] = {}
        turns: list[dict[str, Any]] = []
        for segment, _, label in annotation.itertracks(yield_label=True):
            if label not in order:
                order[label] = f"spk{len(order)}"
            turns.append(
                {"start": float(segment.start), "end": float(segment.end), "speaker": order[label]}
            )
        return turns

    # -- progress ---------------------------------------------------------------------------

    def _install_progress(self, progress: Callable[[int, int], None] | None) -> None:
        """Thread a pyannote hook through the two stages that report one, without touching `__call__`.

        DiariZen's `__call__` calls `get_segmentations` and `get_embeddings` positionally and passes
        no hook, so there is no argument to supply from outside. Copying that method here to add one
        would mean carrying fifty lines of upstream pipeline logic that can drift from the version
        the published figures describe; wrapping the two bound methods leaves it byte for byte
        upstream and still gets real progress out of the two stages that take the time.

        Clustering is not covered: it reports nothing, and it is the short stage.
        """
        self._saved: dict[str, Any] = {}
        if progress is None:
            return

        # Two stages, weighted equally, so the bar is monotone across both rather than restarting.
        # Which half a message belongs to is decided by the stage that emitted it, not by the name
        # pyannote gives it, because those names are upstream's to change.
        def make(stage_index: int, total_stages: int) -> Callable[..., None]:
            def hook(
                step_name: str,
                step_artifact: Any,
                file: Any = None,
                total: int | None = None,
                completed: int | None = None,
            ) -> None:
                if not total:
                    return
                span = 1.0 / total_stages
                fraction = stage_index * span + span * (min(completed or 0, total) / total)
                progress(int(fraction * 1000), 1000)

            return hook

        pipeline = self._pipeline
        segmentations = pipeline.get_segmentations
        embeddings = pipeline.get_embeddings
        self._saved = {"get_segmentations": segmentations, "get_embeddings": embeddings}

        def with_segmentation_hook(file: Any, hook: Any = None, soft: bool = False) -> Any:
            return segmentations(file, hook=make(0, 2), soft=soft)

        def with_embedding_hook(
            file: Any,
            binary_segmentations: Any,
            exclude_overlap: bool = False,
            hook: Any = None,
        ) -> Any:
            return embeddings(
                file, binary_segmentations, exclude_overlap=exclude_overlap, hook=make(1, 2)
            )

        pipeline.get_segmentations = with_segmentation_hook
        pipeline.get_embeddings = with_embedding_hook

    def _restore_progress(self) -> None:
        for name, bound in getattr(self, "_saved", {}).items():
            setattr(self._pipeline, name, bound)
        self._saved = {}
