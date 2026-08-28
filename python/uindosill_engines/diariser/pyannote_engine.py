"""pyannote.audio's own diariser, as the sidecar drives it: segmentation, wespeaker, VBx.

The only diariser, since 2026-08-27. It stood where DiariZen stood that morning and became the
whole of speaker labelling that afternoon, when the Sortformer engine moved to `attic/sortformer/`.
It answers the question that engine's geometry could not — **how many voices are in this
recording** — and it clusters rather than tracks, so the count is an output rather than a ceiling.

**What it does not answer is what that engine did.** Every diarisation figure this project has
published — 16.33% DER on AMI test above all — was measured on Sortformer, and none of it describes
this pipeline.

**One meeting is measured, and it is not a corpus.** This said "nothing here is measured yet" until
the engine was first run on 2026-08-27; what that run produced is AMI ES2004a alone — 14.38% DER at
collar 0.25 with overlap, 18.76% at collar 0, 5 speakers against a reference 4, at 5.2x realtime on
the CPU. **It must not be set beside Sortformer's 16.33%**, which is a sixteen-meeting mean over the
whole AMI test set, and it does not answer the speaker gate, which names that test set and a
protocol this did not follow.

Everything else this module would want to state is still absent on purpose rather than estimated,
and the capabilities it reports say `None` where DiariZen's said `None` for the same reason: no
bound has been established. `docs/UNPROVEN.md` is the record.

**Why it replaced DiariZen rather than joining it.** DiariZen's `pyannote-audio` is a fork of 3.1.1
and pyannote's own release line is at 4.x; the two cannot share an interpreter. It is not only the
`pyannote.audio` name — `pyannote.audio` 4.0.7 floors `pyannote.core>=6.0.1`, `pyannote.database>=6.1.1`,
`pyannote.metrics>=4.0.0` and `pyannote.pipeline>=4.0.0`, against the `5.0.0`, `5.1.3`, `3.2.1` and
`3.0.1` the fork needs. Five shared import names, five incompatible floors. The `pyannote.__path__`
graft that used to make one namespace out of two sources arbitrated exactly one of them.

**The upstream absorbed the fork's idea, which is what makes this a replacement and not a loss.**
pyannote 4's `speaker-diarization-community-1` clusters with VBx — contributed by the same BUT
Speech@FIT researchers who publish DiariZen — so the algorithm family, the wespeaker embedder and
the PLDA files are the ones this project already carried. What changes is the segmentation model
and the licence: **CC BY 4.0 rather than CC BY-NC 4.0**, which is the first time this project's
speaker-labelling alternative has been usable commercially. `docs/LICENSING.md` is the record.

**Two upstream behaviours are switched off here, and both are deliberate.**

* **Telemetry, which ships enabled.** `pyannote/audio/telemetry/config.yaml` carries
  `metrics_enabled: true` and an endpoint at `https://otel.pyannote.ai/v1/traces`, and
  `track_pipeline_apply` reports the **duration of every file processed** alongside a session id and
  the requested speaker counts. For a product whose whole claim is that the audio never leaves the
  machine, that is not a default to inherit. :func:`_silence_telemetry` sets
  `PYANNOTE_METRICS_ENABLED=false` before the package is imported; upstream's module-level guard
  only fills that variable in when it is *absent*, so setting it first wins, and every `track_*`
  is gated on `is_metrics_enabled()` reading it back.
* **torchcodec's decode path, which is an FFmpeg one.** `pyannote.audio` requires
  `torchcodec>=0.7.0` and reaches it to decode audio, and TorchCodec decodes through FFmpeg — the
  LGPL component this product removed in 1702d9e. **The bundle installs torchcodec**, because it is
  a hard dependency of the pin and `bundle-python.ps1` resolves the closure; what it does not
  install is an FFmpeg, and TorchCodec ships none — its own description says it "uses the version
  of FFmpeg you already have installed", so no LGPL object is redistributed either way.
  Upstream's `core/io.py` names the route around it in its import guard: *"use audio preloaded
  in-memory as a {'waveform': (channel, time) torch.Tensor, 'sample_rate': int} dictionary"*. The
  host already writes 16 kHz mono PCM, so :meth:`PyannoteEngine.label` reads it with `soundfile`
  and hands the pipeline a waveform. **No decode path here reaches TorchCodec** — that is a reading
  of upstream's source, not an observation; `docs/UNPROVEN.md` carries it, and
  `docs/LICENSING.md` records what it buys.

**Three things it did not share with the Sortformer engine, all structural** — and all three were
the same three DiariZen had, because they follow from clustering rather than from the checkpoint.
Written as contrasts because that is how the difference was found, and kept now that the comparison
is with something shelved: each one is a property of this pipeline that a reader would otherwise
have to infer from its absence.

* **It is torch, and there is no ONNX half.** DiariZen's speaker embedder had an ONNX route with a
  parity fixture; this pipeline's embedder is reached through pyannote's own model loader and no
  such route has been built. The provider therefore names a torch device, not an execution
  provider, and `auto` is the CPU.
* **It is offline, not streaming.** It reads the whole file, embeds every chunk, then clusters
  globally — so there is no duration drift to bound, and equally it holds the whole embedding set
  in memory.
* **Its binarisation is internal and the host's post-processing does not reach it.** The pipeline
  binarizes at the parameters its own `config.yaml` carries, which is what its published figures
  describe. Applying the defaults the host still knows how to send — a one-second minimum silence,
  which merges turns, tuned for the shelved engine — would produce output no measurement
  describes. So the options are reported
  as not honoured rather than quietly applied, and dropped rather than passed on.

**The layout this module expects is unverified.** `pyannote/speaker-diarization-community-1` is a
gated repository: an unauthenticated read of its `config.yaml` returns HTTP 401, so the file list
below comes from the repository's public file *index* rather than from having opened them. The
loader therefore reports what it could not find rather than asserting a shape, and the first run on
a machine with a token is what confirms it.
"""

from __future__ import annotations

import os
from typing import Any, Callable

from ..protocol import RequestError

#: No total speaker cap. The clustering is never given a count, and VBx returns as many speakers as
#: it finds. None is what the host renders as "no such limit" rather than as a number nobody
#: measured — the same distinction DiariZen's entry drew, and for the same reason.
MAX_SPEAKERS = None

#: Where the evidence stops. **No accuracy figure exists for this model on any recording measured by
#: this project**, so there is no bound to state — which is not the same as "any length". Upstream
#: publishes DER figures for `community-1` on several corpora; none of them were produced here, on
#: this project's material, through this project's audio path, so none of them are quoted as this
#: engine's. `docs/UNPROVEN.md` carries the gap.
RELIABLE_UP_TO_SECONDS = None

#: What `threads: 0` means for this engine, matching the other diariser arms rather than torch's
#: own default of every core on the machine. A default that varies by machine makes two runs
#: incomparable for a reason nobody records.
DEFAULT_THREADS = 12

#: The pipeline entry point. `Pipeline.from_pretrained` accepts "a path to a local directory
#: containing such a file", which is the offline route pyannote 4.0 added and the one this project
#: uses; nothing here contacts the hub at load time.
CONFIG_FILE = "config.yaml"

#: The four weight files the pipeline's `config.yaml` refers to, in the subdirectory layout the
#: upstream repository publishes. **Kept as relative paths rather than flattened**, unlike the
#: DiariZen entry, because `from_pretrained` resolves them through the config rather than by
#: convention — flattening them would mean rewriting a config the catalogue pins the size of.
#:
#: **The catalogue fetches these file by file, at these paths.** `ModelFile.FileName` was a bare
#: name by contract until 2026-08-27; it was widened to accept a `/`-separated relative path in the
#: same change that added this entry, precisely so that the layout survives installation
#: (`ModelCatalog.IsSafeRelativeFileName` is what bounds it). An earlier draft of this comment said
#: the install route was a `huggingface_hub` snapshot — it is not, and never was in shipped code.
#: What the gate does force is authentication: every one of these URLs answers 401 without the
#: user's own token, which `ModelInstaller` attaches for Hugging Face hosts only.
WEIGHT_FILES = (
    "segmentation/pytorch_model.bin",
    "embedding/pytorch_model.bin",
    "plda/plda.npz",
    "plda/xvec_transform.npz",
)

REQUIRED_FILES = (CONFIG_FILE, *WEIGHT_FILES)

#: The environment variable upstream reads to decide whether to export a span. Named here rather
#: than written inline at its one call site so that the grep for it lands on this comment.
TELEMETRY_SWITCH = "PYANNOTE_METRICS_ENABLED"


def _silence_telemetry() -> None:
    """Turn off pyannote's usage reporting before the package that reads it is imported.

    **Order is the whole mechanism.** `pyannote/audio/telemetry/metrics.py` runs

        if "PYANNOTE_METRICS_ENABLED" not in os.environ:
            os.environ["PYANNOTE_METRICS_ENABLED"] = str(CONFIG["metrics_enabled"]).lower()

    at module scope, where `CONFIG` is the shipped `config.yaml` and `metrics_enabled` is `true`.
    The guard fills the variable in only when it is absent, so a value set *before* the import
    survives and a value set after it is already too late to have prevented the default. Every
    `track_model_init`, `track_pipeline_init` and `track_pipeline_apply` then reads it back through
    `is_metrics_enabled()`, so one variable covers all three.

    **What it stops being sent.** `track_pipeline_apply` computes the audio's duration and attaches
    it to a span along with a per-process session id, the package version, the pipeline's origin and
    the requested speaker counts, and posts to `https://otel.pyannote.ai/v1/traces`. The exporter is
    still *constructed* at import — a `TracerProvider` and a `BatchSpanProcessor` — but construction
    opens no socket, and with the switch off no span is ever started.

    Set unconditionally rather than only when unset: a bundle that had somehow acquired a `true`
    from the environment would otherwise inherit it.
    """
    os.environ[TELEMETRY_SWITCH] = "false"


#: **There is no `torchaudio.load` shim here, and its absence is the decision.**
#:
#: The DiariZen engine carried one from 2026-08-26: pyannote-audio 3.1.1 reached `torchaudio.load`,
#: which delegates to TorchCodec since torchaudio 2.9, which decodes through FFmpeg — the LGPL
#: component this product removed in 1702d9e. Routing it through `soundfile` kept that dependency
#: out.
#:
#: **pyannote.audio 4.0.7 does not call it.** Grepped across the 4.0.7 source, `torchaudio` appears
#: only as `functional.resample` (`core/io.py`), `compliance.kaldi` (the wespeaker embedder and
#: `pipelines/speaker_verification.py`), `transforms.MFCC` and `torchaudio.pipelines`/`models` in
#: SSeRiouSS — all pure torch, none of them a decoder. So a shim here would patch a function nothing
#: reaches.
#:
#: **And a shim would be worse than useless, which is why it was removed rather than kept "just in
#: case".** `torchaudio.load` takes `frame_offset`, `num_frames` and `channels_first`; a soundfile
#: replacement that ignores them answers a request for one second of audio with the whole file, and
#: does it process-globally, to any caller. A monkeypatch that silently returns the wrong data is a
#: worse failure than the ImportError it was written to avoid — that one at least names TorchCodec.
#:
#: What actually keeps FFmpeg out is :meth:`PyannoteEngine.label` handing the pipeline a
#: `{"waveform", "sample_rate"}` dict, which upstream's own `core/io.py` import guard names as the
#: supported route when TorchCodec is absent. That is one mechanism rather than two, and it is the
#: one the licensing note in `docs/LICENSING.md` rests on.
_NO_TORCHAUDIO_SHIM = None


class PyannoteEngine:
    """One loaded pyannote pipeline, held for the life of the sidecar.

    Construction is the expensive part — a segmentation checkpoint, a wespeaker model and the PLDA
    matrices — so the sidecar builds this once per batch, which is the reason the host keeps the
    process alive rather than spawning one per file.
    """

    def __init__(
        self,
        model_dir: str,
        threads: int,
        provider: str = "cpu",
        profile: bool = False,
        batch_size: int | None = None,
    ) -> None:
        missing = [
            name
            for name in REQUIRED_FILES
            if not os.path.isfile(os.path.join(model_dir, *name.split("/")))
        ]
        if missing:
            raise RequestError(
                "model",
                f"the pyannote model directory {model_dir} is missing {', '.join(sorted(missing))}. "
                "The catalogue installs these files individually at these relative paths, keeping "
                "the upstream repository's subdirectory layout; a flattened copy will not load.",
            )

        # Before the import, and that is the point of it — see the function's own docstring.
        _silence_telemetry()

        import torch

        # Named rather than left to torch's default, which is every core on the machine. Every CPU
        # figure this project publishes for a diariser was taken at 12, and a default that varies
        # by machine makes two runs incomparable for a reason nobody records.
        torch.set_num_threads(threads or DEFAULT_THREADS)

        from pyannote.audio import Pipeline

        # **Caught rather than guarded against a None return**, which is what stood here until an
        # adversarial review pointed out that `from_pretrained` does not have one on this path: the
        # local-directory branch either builds a pipeline or raises. The raises are what a user will
        # actually meet — malformed YAML, a `pipeline.name` key the config is missing, a `$model/…`
        # reference to a subfolder that did not survive installation, or a `dependencies:` block
        # naming a pyannote.audio the bundle does not carry. Left alone, each of those leaves this
        # constructor as its own exception type, reaches the host as kind `internal` with a
        # traceback, and reads as a bug in this project rather than as a model that will not load.
        #
        # The original exception is chained rather than swallowed: the sidecar puts a traceback in
        # the error message, so the specific cause is still there for whoever needs it.
        try:
            pipeline = Pipeline.from_pretrained(model_dir)
        except Exception as exc:  # noqa: BLE001
            raise RequestError(
                "model",
                f"pyannote could not load a pipeline from {model_dir}: {exc}. All five expected "
                f"files are present, so {CONFIG_FILE} — or a version it names — is the thing to "
                "look at.",
            ) from exc

        if pipeline is None:
            # Defensive, and known to be unreachable on the local-directory path as of 4.0.7. Kept
            # because the next line would otherwise fail with an AttributeError on None, which is
            # the least diagnosable outcome available, and because this is upstream's return type
            # rather than a guarantee it documents.
            raise RequestError(
                "model",
                f"pyannote returned no pipeline from {model_dir}, without raising. The "
                f"{CONFIG_FILE} in it is the thing to look at.",
            )

        self._pipeline = pipeline

        # **Left alone unless the host asks**, so that the default path is the published artefact's
        # own configuration and this project adds nothing to it. When the host does ask, both halves
        # move: `SpeakerDiarization` exposes `segmentation_batch_size` and `embedding_batch_size`,
        # and the segmentation one is baked into an `Inference` at construction — the same
        # three-attribute shape the DiariZen arm needed, for the same reason.
        if batch_size is not None:
            if batch_size < 1:
                raise RequestError("request", f"batch size must be at least 1, got {batch_size}")
            self._pipeline.segmentation_batch_size = batch_size
            self._pipeline.embedding_batch_size = batch_size
            inference = getattr(self._pipeline, "_segmentation", None)
            if inference is not None:
                inference.batch_size = batch_size

        # Read back rather than remembered, so what the engine reports is what the pipeline will
        # actually use — including when nobody asked and the number came from the config.
        inference = getattr(self._pipeline, "_segmentation", None)
        self.batch_size = int(getattr(inference, "batch_size", 0)) or None

        self.threads = threads or DEFAULT_THREADS
        self._profile = profile
        self._model_dir = model_dir

        device = self._resolve_device(provider)
        if device is not None:
            self._pipeline.to(device)
        self.device = str(device) if device is not None else "cpu"

        # **One runtime, both stages**, which is the difference from the DiariZen arm worth stating:
        # there the embedder could be moved to ONNX Runtime independently and the two backends were
        # reported separately because only one of them was chosen. Here both stages are torch on the
        # same device, so one name is the whole truth and the two fields agree by construction.
        self.segmentation_backend = f"torch:{self.device}"
        self.embedding_backend = f"torch:{self.device}"

    @staticmethod
    def _resolve_device(provider: str) -> Any:
        """Map the host's provider name onto a torch device, refusing the ones that mean nothing here.

        **The vocabulary is the host's and it is wider than this engine's**, because it was written
        for an ONNX arm: `webgpu` and `dml` are execution providers, not torch devices, and there is
        no ONNX route in this pipeline for them to name. Refused outright rather than silently
        treated as the CPU — somebody who typed `webgpu` and got the CPU has been told nothing, which
        is the same rule both older arms enforce.
        """
        import torch

        if provider in ("auto", "cpu", "torch"):
            return torch.device("cpu")

        if provider == "cuda":
            if not torch.cuda.is_available():
                raise RequestError(
                    "model",
                    "this torch build reports no CUDA device. The bundled torch is the CPU build, "
                    "so `cuda` is reachable only in an environment that installed a CUDA one.",
                )
            return torch.device("cuda")

        raise RequestError(
            "request",
            f"'{provider}' does not name anything this diariser can run on. It is a torch pipeline "
            "with no ONNX route, so webgpu and dml have nothing to select here. Choose cpu or cuda.",
        )

    @property
    def backend(self) -> str:
        """Where the work happens, as one name the host can parse.

        Unlike the DiariZen arm this is unambiguous: both neural stages are torch on the device this
        returns, so there is no chosen half to distinguish from a fixed one.
        """
        return self.device

    def label(
        self,
        wav_path: str,
        progress: Callable[[int, int], None] | None = None,
    ) -> list[dict[str, Any]]:
        """Turns for one 16 kHz mono WAV, in time order, with the pipeline's own cluster labels."""
        import numpy as np
        import soundfile as sf_io
        import torch

        try:
            wav, sample_rate = sf_io.read(wav_path, dtype="float32", always_2d=True)
        except Exception as exc:  # noqa: BLE001
            raise RequestError("audio", f"could not read {wav_path}: {exc}") from exc

        if sample_rate != 16000:
            # The same contract both other arms assert: the host resamples, and reaching here means
            # the two sides disagree rather than that this engine should guess.
            raise RequestError("audio", f"expected 16 kHz mono, got {sample_rate} Hz")

        if wav.shape[0] == 0:
            return []

        # **A waveform rather than the path, which is what keeps TorchCodec out of the process.**
        # `(channel, time)`, which is the orientation upstream's `validate_file` checks for; the
        # `always_2d` read gives `(time, channel)`, so this transposes rather than trusting a shape.
        waveform = torch.from_numpy(np.ascontiguousarray(wav.T))
        file = {"waveform": waveform, "sample_rate": sample_rate, "uri": "audio"}

        output = self._pipeline(file, hook=self._make_hook(progress))

        # **Two shapes, because the pipeline has two.** pyannote 4 returns a `DiarizeOutput`
        # dataclass carrying `speaker_diarization`, an exclusive variant and the speaker embeddings;
        # with `legacy: true` in its config it returns the bare `Annotation` that 3.x returned.
        # Both are read rather than one assumed, because which arrives is the installed config's
        # choice and not this engine's.
        annotation = getattr(output, "speaker_diarization", output)

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

    @staticmethod
    def _make_hook(progress: Callable[[int, int], None] | None) -> Any:
        """Adapt pyannote's hook to the host's two-integer progress, or return None.

        **This arm needs no monkeypatching, and that is the one place it is simpler than DiariZen's.**
        There, `__call__` called the two staged methods positionally and passed no hook, so the only
        way to get progress out without carrying fifty lines of upstream pipeline logic was to wrap
        the bound methods. `SpeakerDiarization.apply` takes a `hook` and threads it through both
        stages itself, so this passes one and touches nothing.

        Upstream calls it as `hook(step_name, step_artifact, file=..., total=..., completed=...)`
        and emits from more than two steps. Rather than weight named steps — those names are
        upstream's to change — **this forwards each step's own `completed` and `total` unchanged**,
        with no state of any kind. So the bar is monotone *within* a step and restarts at each step
        boundary, and the denominator is whatever upstream passed for the step in flight rather than
        anything about the file as a whole. The host renders a fraction, so a boundary shows as a
        reset rather than as a lie about the whole.

        **How many steps there are, and what they are called, has not been observed** — only that
        there are more than two. Weighting them would need that, and would need it to stay true.
        """
        if progress is None:
            return None

        def hook(
            step_name: str,
            step_artifact: Any = None,
            file: Any = None,
            total: int | None = None,
            completed: int | None = None,
        ) -> None:
            if not total:
                return
            progress(int(min(completed or 0, total)), int(total))

        return hook
