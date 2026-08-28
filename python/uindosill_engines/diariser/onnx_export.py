"""Derive the diariser's two ONNX graphs from the installed pyannote pipeline.

**One implementation, two callers.** `scripts/export-diariser-onnx.py` is a thin command line over
this, and the sidecar's `exportDiariserGraphs` op calls it directly so the application can produce
the graphs without anybody running a script. Two copies of an export are two graphs that can differ,
which is the one thing a parity number would then be measuring.

**What is exported, and what deliberately is not.**

  * **Segmentation — the whole model.** `PyanNet.forward` is SincNet, LSTM, linear, activation: one
    tensor in, one out, nothing dynamic. It exports whole.
  * **Embedding — the ResNet only, not the featuriser.** `WeSpeakerResNet34.compute_fbank` runs
    `torch.vmap`, which has no ONNX lowering. Not a workaround: wespeaker's own `infer_onnx.py`
    computes fbank outside the graph too, and the ResNet is where the arithmetic is. fbank stays in
    torch on the CPU and the graph takes features.

The pipeline itself is untouched — sliding window, powerset decoding, PLDA and VBx clustering all
stay upstream's, and only the two forward passes move. See :mod:`.pyannote_engine` for the other
half, which loads what this writes.
"""

from __future__ import annotations

import hashlib
import json
import os
import time
from typing import Any, Callable

from ..protocol import RequestError

#: The seed every random tensor here is drawn under.
#:
#: **A parity figure taken on unseeded noise cannot be reproduced, and this repository publishes
#: parity figures.** The first sweep reported 2.67e-05 for the CPU provider and a re-run of the same
#: code on the same graphs reported 3.48e-05 — both true, neither checkable against the other, and
#: the difference is which random waveform happened to be drawn. Seeded, the number in a manifest is
#: a number somebody else can get back.
#:
#: It also fixes the traced example, which matters for a different reason: the exporter bakes the
#: example's *shape* into the graph and its values into nothing, but a bit-identical trace makes the
#: graph's own SHA-256 stable, so two exports from the same weights produce the same file.
SEED = 0

#: Opset 18. The dynamo exporter emits 18 for these graphs and the downgrade to 17 fails on
#: `Resize` ("No Adapter To Version 17"), leaving the file at 18 anyway — so 18 is what is asked for
#: rather than what is silently settled on. ONNX Runtime 1.27.0 implements it for both providers,
#: LSTM included, which is the operator whose coverage was actually in question.
DEFAULT_OPSET = 18

#: The window the pipeline asks the segmentation model for, when its config carries none.
DEFAULT_DURATION = 10.0

#: The batch the traced example uses. Nothing depends on it — the batch axis is dynamic and the
#: parity sweep checks that — but a trace has to pick one.
DEFAULT_TRACE_BATCH = 4

#: Batch sizes the parity sweep covers for the segmentation graph, and why it is a sweep: the
#: TorchScript exporter warns that an LSTM traced at batch N can bake N into its initial states, and
#: the pipeline runs `segmentation_batch_size` (32) then a final padded chunk at batch 1.
PARITY_SEGMENTATION_BATCHES = (1, 2, 4, 8, 32)

#: `(batch, mask_frames)` pairs for the embedding graph. The mask length and the fbank length are
#: independent in the pipeline — `StatsPool` interpolates one to the other — so they are swept
#: independently rather than together.
PARITY_EMBEDDING_SHAPES = ((1, 589), (32, 589), (7, 300), (32, 1000))

SEGMENTATION_FILE = "segmentation.onnx"
EMBEDDING_FILE = "embedding.onnx"
MANIFEST_FILE = "manifest.json"


def sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def _export_one(module, args, path, input_names, output_names, dynamic_axes, opset) -> str:
    """Export one module, preferring the dynamo exporter and reporting which one produced the file.

    Recorded because the two exporters do not always emit the same graph, and a parity number is
    only comparable to another taken from the same route.
    """
    import torch

    # **`external_data=False` is not a preference.** The dynamo exporter defaults to writing weights
    # into a sibling `<name>.onnx.data`, and a graph in two files is a graph that installs as one and
    # fails at session creation with "External data path does not exist". These are small enough — a
    # 26 MiB ResNet, a 6 MiB PyanNet — that there is nothing to weigh.
    try:
        torch.onnx.export(
            module, args, path,
            input_names=input_names, output_names=output_names,
            dynamic_axes=dynamic_axes, opset_version=opset, dynamo=True,
            external_data=False,
        )
        route = "dynamo"
    except Exception:  # noqa: BLE001
        torch.onnx.export(
            module, args, path,
            input_names=input_names, output_names=output_names,
            dynamic_axes=dynamic_axes, opset_version=opset, dynamo=False,
        )
        route = "torchscript"

    # Checked rather than trusted: either exporter leaving a sidecar behind would ship a graph that
    # only loads from the directory it was written in.
    if os.path.isfile(path + ".data"):
        raise RequestError(
            "internal",
            f"{os.path.basename(path)} was written with external data; the export must produce a "
            "single self-contained file.",
        )

    return route


def export(
    model_dir: str,
    out_dir: str | None = None,
    opset: int = DEFAULT_OPSET,
    trace_batch: int = DEFAULT_TRACE_BATCH,
    parity: bool = True,
    progress: Callable[[int, int], None] | None = None,
) -> dict[str, Any]:
    """Write both graphs and return the manifest describing them.

    `out_dir` defaults to `<model_dir>/onnx`, which is where :mod:`.pyannote_engine` looks. `parity`
    runs the sweep against torch on every available provider; the command line leaves it on, and the
    application turns it off because it is the slow half and the graphs it would be checking are the
    ones it just wrote from the same weights.
    """
    if out_dir is None:
        out_dir = os.path.join(model_dir, "onnx")

    os.makedirs(out_dir, exist_ok=True)

    # Imported here rather than at module scope: the sidecar loads this module to answer an op, and
    # importing torch costs seconds whether or not an export was asked for.
    import torch

    torch.manual_seed(SEED)

    from .pyannote_engine import _silence_telemetry

    _silence_telemetry()

    from pyannote.audio import Pipeline

    total = 4 if parity else 2
    step = 0

    def advance() -> None:
        nonlocal step
        step += 1
        if progress is not None:
            progress(step, total)

    try:
        pipeline = Pipeline.from_pretrained(model_dir)
    except Exception as exc:  # noqa: BLE001
        raise RequestError("model", f"pyannote could not load a pipeline from {model_dir}: {exc}") from exc

    if pipeline is None:
        raise RequestError("model", f"pyannote returned no pipeline from {model_dir}.")

    inference = pipeline._segmentation
    segmentation = inference.model.eval()
    duration = float(getattr(inference, "duration", None) or DEFAULT_DURATION)
    sample_rate = int(segmentation.audio.sample_rate)
    num_samples = int(round(duration * sample_rate))

    inner = pipeline._embedding.model_.eval()
    resnet = inner.resnet.eval()

    manifest: dict[str, Any] = {
        "source": model_dir,
        "opset": opset,
        "torch": torch.__version__,
        "sample_rate": sample_rate,
        "segmentation_duration_s": duration,
        "graphs": {},
    }

    try:
        import pyannote.audio as _pyannote_audio

        manifest["pyannote_audio"] = _pyannote_audio.__version__
    except Exception:  # noqa: BLE001
        manifest["pyannote_audio"] = None

    # ---- segmentation ---------------------------------------------------------------------
    seg_dummy = torch.randn(trace_batch, 1, num_samples)
    with torch.inference_mode():
        seg_reference = segmentation(seg_dummy)

    seg_path = os.path.join(out_dir, SEGMENTATION_FILE)
    seg_route = _export_one(
        segmentation, (seg_dummy,), seg_path,
        ["waveforms"], ["scores"],
        {"waveforms": {0: "batch"}, "scores": {0: "batch"}},
        opset,
    )
    manifest["graphs"]["segmentation"] = {
        "file": SEGMENTATION_FILE,
        "exporter": seg_route,
        "sha256": sha256(seg_path),
        "bytes": os.path.getsize(seg_path),
        "num_frames": int(seg_reference.shape[1]),
        "num_classes": int(seg_reference.shape[2]),
    }
    advance()

    # ---- embedding ------------------------------------------------------------------------
    emb_wave = torch.randn(trace_batch, 1, num_samples)
    with torch.inference_mode():
        fbank = inner.compute_fbank(emb_wave)

    # **The weights are the segmentation's frames, not the fbank's, and the two differ** — 589
    # against 998 on a 10 s window. Tracing with one axis name for both would bake in an equality the
    # shipping path breaks on its first call.
    mask_frames = int(seg_reference.shape[1])
    weights = torch.rand(trace_batch, mask_frames)
    with torch.inference_mode():
        emb_reference = resnet(fbank, weights=weights)[1]

    class ResnetEmbedding(torch.nn.Module):
        """The `[1]` of the ResNet's tuple, as a graph with one output."""

        def __init__(self, wrapped) -> None:
            super().__init__()
            self.wrapped = wrapped

        def forward(self, fbank, weights):
            return self.wrapped(fbank, weights=weights)[1]

    emb_path = os.path.join(out_dir, EMBEDDING_FILE)
    emb_route = _export_one(
        ResnetEmbedding(resnet).eval(), (fbank, weights), emb_path,
        ["fbank", "weights"], ["embedding"],
        {
            "fbank": {0: "batch", 1: "fbank_frames"},
            "weights": {0: "batch", 1: "mask_frames"},
            "embedding": {0: "batch"},
        },
        opset,
    )
    manifest["graphs"]["embedding"] = {
        "file": EMBEDDING_FILE,
        "exporter": emb_route,
        "sha256": sha256(emb_path),
        "bytes": os.path.getsize(emb_path),
        "mel_bins": int(fbank.shape[2]),
        "dimension": int(emb_reference.shape[-1]),
        "mask_frames_example": mask_frames,
    }
    advance()

    if parity:
        manifest["parity"] = _sweep(
            seg_path, emb_path, segmentation, inner, resnet, num_samples, advance
        )

    with open(os.path.join(out_dir, MANIFEST_FILE), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=2)

    manifest["out_dir"] = out_dir
    return manifest


def _sweep(seg_path, emb_path, segmentation, inner, resnet, num_samples, advance) -> dict[str, Any]:
    """Score both graphs against torch on every available provider, across batch sizes."""
    import numpy as np
    import onnxruntime as ort
    import torch

    # Re-seeded rather than relying on the export's call: the sweep draws its own tensors, and the
    # number of draws before it depends on which exporter route ran.
    torch.manual_seed(SEED)

    providers = {
        "cpu": ["CPUExecutionProvider"],
        "webgpu": ["WebGpuExecutionProvider", "CPUExecutionProvider"],
    }

    results: dict[str, Any] = {}
    for name, provider_list in providers.items():
        if provider_list[0] not in ort.get_available_providers():
            results[name] = {"status": "provider not available in this build"}
            advance()
            continue

        try:
            seg_session = ort.InferenceSession(seg_path, providers=provider_list)
            emb_session = ort.InferenceSession(emb_path, providers=provider_list)
        except Exception as exc:  # noqa: BLE001
            results[name] = {"error": f"{type(exc).__name__}: {exc}"}
            advance()
            continue

        entry: dict[str, Any] = {
            "provider_used": seg_session.get_providers()[0],
            "segmentation": {},
            "embedding": {},
        }

        for batch in PARITY_SEGMENTATION_BATCHES:
            wave = torch.randn(batch, 1, num_samples)
            with torch.inference_mode():
                reference = segmentation(wave).numpy()
            try:
                started = time.perf_counter()
                produced = seg_session.run(None, {"waveforms": wave.numpy()})[0]
                entry["segmentation"][str(batch)] = {
                    "max_abs_diff_vs_torch": float(np.max(np.abs(produced - reference))),
                    "seconds": round(time.perf_counter() - started, 4),
                }
            except Exception as exc:  # noqa: BLE001
                entry["segmentation"][str(batch)] = {"error": f"{type(exc).__name__}: {exc}"}

        for batch, mask_length in PARITY_EMBEDDING_SHAPES:
            wave = torch.randn(batch, 1, num_samples)
            with torch.inference_mode():
                batch_fbank = inner.compute_fbank(wave)
            batch_weights = torch.rand(batch, mask_length)
            with torch.inference_mode():
                reference = resnet(batch_fbank, weights=batch_weights)[1].numpy()
            key = f"batch{batch}_mask{mask_length}"
            try:
                started = time.perf_counter()
                produced = emb_session.run(
                    None, {"fbank": batch_fbank.numpy(), "weights": batch_weights.numpy()}
                )[0]
                entry["embedding"][key] = {
                    "max_abs_diff_vs_torch": float(np.max(np.abs(produced - reference))),
                    "seconds": round(time.perf_counter() - started, 4),
                }
            except Exception as exc:  # noqa: BLE001
                entry["embedding"][key] = {"error": f"{type(exc).__name__}: {exc}"}

        worst = [
            value["max_abs_diff_vs_torch"]
            for stage in ("segmentation", "embedding")
            for value in entry[stage].values()
            if "max_abs_diff_vs_torch" in value
        ]
        entry["worst_max_abs_diff"] = max(worst) if worst else None
        results[name] = entry
        advance()

    return results
