"""DiariZen's speaker embedder on ONNX Runtime, so that half of it can leave the CPU.

DiariZen is torch and has no GPU path on the machines this project ships to. **WebGPU is an ONNX
Runtime execution provider**, so the only way to reach the Radeon 880M is to stop running that stage
in torch. A study on 2026-08-26 measured both neural stages and found the two halves answer
differently:

* **Segmentation** — pruned WavLM-large plus a Conformer — exports faithfully but gains nothing.
  torch CPU, ORT CPU and ORT WebGPU all land within about 10% of each other, which is the machine's
  own run-to-run variance, and WebGPU scales linearly with batch, so it is bandwidth-bound rather
  than dispatch-bound and there is no overhead to recover. It stays in torch.
* **Embedding** — a wespeaker ResNet34 — reproduces torch's embedding vectors to **1.94e-07** on
  WebGPU and **1.21e-07** on ORT's CPU provider, both measured at batch 32, and runs about a third
  faster end to end. That is this module.

**It is not what `auto` selects, and the reason is in `diariser/__init__.py`**: reproducing the
vectors is not the same as reproducing the labels, and the labels move — 222 speaker turns against
torch's 225 on the one recording where both were run.

`docs/UNPROVEN.md` carries the figures and the limits; the study is on the maintainer's Drive. Quote
numbers from there rather than from here: an earlier draft of this header carried figures from a
prototype export that did not take the `weights` input, and they were wrong by the time it shipped.

**Why the graph is derived here rather than downloaded.** pyannote publishes a
`speaker-embedding.onnx`, and the vendored fork has a loader for it, but **that path computes a
different answer** and cannot be used: `ONNXWeSpeakerPretrainedSpeakerEmbedding.__call__`
binarises the speaker mask at 0.5, *selects* the surviving frames and runs the session one item at a
time with no weights at all, where the torch path passes the soft mask into a weighted statistics
pooling over every frame. Two different algorithms, and only one of them is what this project's
diarisation figures describe. So the graph is exported from the checkpoint the catalogue already
downloads and digest-verifies, which also keeps this project from redistributing a second copy of a
CC BY 4.0 model.

**What the export is a function of, and what the cache key actually covers.** The graph depends on
four things: `pyannote-wespeaker-voxceleb-resnet34-LM.bin` (whose SHA-256 is pinned in
`models.json`), :func:`_build_export_wrapper` here, the vendored `StatsPool._pool` it calls
directly, and the torch / onnx / onnxscript versions that do the tracing.

**The key covers the first and, by hand, the second.** It is the checkpoint's size and mtime plus
:data:`GRAPH_VERSION`, so replacing the weights re-derives automatically — but a change to the
vendored pooling code or a toolchain upgrade does not, and would silently reuse a graph derived from
something else. **Bump :data:`GRAPH_VERSION` when any of those three change.** An earlier draft of
this paragraph claimed a stale cache "cannot outlive the thing it was derived from", which is true of
the checkpoint and false of the other three.
"""

from __future__ import annotations

import hashlib
import os
from typing import Any

import numpy as np

#: The derived graph's file name, inside the model directory beside the checkpoint it came from.
GRAPH_FILENAME = "wespeaker-resnet34.onnx"

#: Bumped whenever :func:`export_graph` would produce a different graph from the same checkpoint.
#: It is part of the cache key, so a change here re-derives rather than silently reusing.
GRAPH_VERSION = 2

#: The two inputs and the one output the graph is exported with. Named rather than positional
#: because ONNX Runtime binds by name and a silent reordering would be a wrong answer, not an error.
INPUT_FEATS = "feats"
INPUT_WEIGHTS = "weights"
OUTPUT_EMBEDDING = "embedding"


def _checkpoint_fingerprint(checkpoint_path: str) -> str:
    """What the cached graph is keyed on.

    The file's size and modification time rather than its SHA-256: the catalogue has already
    verified the digest at download, re-hashing 25 MiB on every load would cost more than the export
    it saves, and this only has to notice that the file underneath changed.
    """
    stat = os.stat(checkpoint_path)
    key = f"{GRAPH_VERSION}:{stat.st_size}:{int(stat.st_mtime)}"
    return hashlib.sha256(key.encode("utf-8")).hexdigest()[:16]


def _build_export_wrapper(resnet: Any) -> Any:
    """`resnet(feats, weights)` reduced to the one tensor the pipeline consumes, without the vmap.

    Two departures from calling `resnet.forward` directly, and only the first is cosmetic.

    **It returns the embedding rather than the tuple.** `ResNet.forward` returns
    `(torch.tensor(0.0), embed_a)` when `two_emb_layer` is False, which the wespeaker ResNet34 is,
    and `BaseWeSpeakerResNet.forward` takes `[1]`. Exporting the tuple would put a constant scalar
    in the graph's signature for every caller to skip.

    **It replaces `StatsPool`'s `torch.vmap` with the single call that vmap makes here, and that is
    what makes a dynamic batch possible at all.** `StatsPool.forward` vmaps `_pool` over the speaker
    axis, and `torch.export` cannot see through it: measured 2026-08-26, the traced graph comes back
    with the batch dimension **specialised to whatever it was traced at**, regardless of the `Dim`
    declared for it, so a pipeline batch of 8 or 32 would meet a graph that accepts 2. The pipeline
    embeds one speaker at a time — `get_embeddings` iterates `masks.T` — so that axis is always 1
    and the vmap is one call to `_pool`. Bypassing it is an identity, and it is checked rather than
    asserted: `max|d| 0.000e+00` against the vendored path, and the parity fixture drives the whole
    embedder against torch on every load.

    None of this edits `_vendor/`. The wrapper reads the vendored modules and calls their own
    `_pool`, so the code the published figures describe is still the code that runs.
    """
    import torch
    import torch.nn.functional as F

    if getattr(resnet, "two_emb_layer", False):
        # The one-output shape below is only right for the wespeaker ResNet34's configuration. A
        # checkpoint built with the second embedding layer would need `seg_bn_1` and `seg_2` here,
        # and silently exporting `embed_a` for it would be a wrong answer rather than a failure.
        raise ValueError("two_emb_layer is on; this export produces the wrong output for it")

    class Wrapper(torch.nn.Module):
        def __init__(self, inner: Any) -> None:
            super().__init__()
            self.inner = inner

        def forward(self, feats: Any, weights: Any) -> Any:
            inner = self.inner
            x = feats.permute(0, 2, 1).unsqueeze(1)
            out = F.relu(inner.bn1(inner.conv1(x)))
            out = inner.layer1(out)
            out = inner.layer2(out)
            out = inner.layer3(out)
            out = inner.layer4(out)

            # `TSTP.forward`'s rearrange, written as a reshape so einops stays out of the trace.
            batch, dimension, channel, frames = out.shape
            sequences = out.reshape(batch, dimension * channel, frames)

            # `StatsPool.forward`, for the two-dimensional weights this pipeline always passes.
            w = weights.unsqueeze(1)
            if w.shape[-1] != frames:
                w = F.interpolate(w, size=frames, mode="nearest")
            stats = inner.pool.stats_pool._pool(sequences, w.squeeze(1))

            return inner.seg_1(stats)

    return Wrapper(resnet).eval()


def export_graph(torch_model: Any, path: str) -> None:
    """Write the ONNX graph for `torch_model.resnet` to `path`.

    **The two frame axes are independent, and that is the whole subtlety of this export.** The
    pipeline hands the embedder a mask at the *segmentation* frame rate — 799 frames for a
    16 s window — while the fbank of the same window is 1598 frames, and the ResNet then downsamples
    time by eight before pooling. `StatsPool.forward` reconciles them by nearest-interpolating the
    weights to whatever the pooled sequence has. Tracing with the two axes tied to one dimension
    produces a graph that silently requires them to be equal, which is a shape error on the first
    real chunk at best and a wrong answer at worst. So they are exported as separate dimensions and
    traced with genuinely different sizes, which also fixes the `num_frames != num_weights` branch
    in the state the pipeline actually uses.
    """
    import torch

    resnet = torch_model.resnet.eval()
    wrapper = _build_export_wrapper(resnet)

    # Deliberately mismatched, for the reason in the docstring: 16 s of audio is 1598 fbank frames
    # and the segmentation mask that accompanies it is 799.
    #
    # **Batch is 2 and not 1 on purpose.** `torch.export` specialises any traced dimension of size
    # 0 or 1 to a constant, whatever `Dim` is declared for it, so tracing one chunk yields a graph
    # that accepts exactly one chunk and rejects the pipeline's batch with a shape error.
    feats = torch.zeros(2, 1598, torch_model.hparams.num_mel_bins, dtype=torch.float32)
    weights = torch.ones(2, 799, dtype=torch.float32)

    # `batch` is named because it is the axis with a contract -- the pipeline varies it and the last
    # batch of a file is short. The two frame axes are `AUTO` because naming them with an explicit
    # range makes the solver reject a guard it cannot discharge over that range
    # (`min(n, 10 + 10n) == n`, from the pooling), while `AUTO` lets it choose the range itself and
    # still comes out symbolic and independent.
    batch = torch.export.Dim("batch", min=1, max=256)
    auto = torch.export.Dim.AUTO

    os.makedirs(os.path.dirname(os.path.abspath(path)) or ".", exist_ok=True)
    temporary = f"{path}.partial"
    try:
        _write(wrapper, feats, weights, temporary, batch, auto)
    except BaseException:
        # A failed export otherwise leaves its scratch file in the model directory, where nothing
        # will ever read it and nothing will ever remove it — one per attempt, beside the weights.
        try:
            os.remove(temporary)
        except OSError:
            pass
        raise

    # Rename last: a half-written graph that kept the final name would be loaded as a good one by
    # the next process to start, and the failure would surface as a corrupt model rather than as the
    # interrupted export it was.
    os.replace(temporary, path)


def _write(wrapper: Any, feats: Any, weights: Any, path: str, batch: Any, auto: Any) -> None:
    import torch

    torch.onnx.export(
        wrapper,
        (feats, weights),
        path,
        input_names=[INPUT_FEATS, INPUT_WEIGHTS],
        output_names=[OUTPUT_EMBEDDING],
        dynamic_shapes=(
            {0: batch, 1: auto},
            {0: batch, 1: auto},
        ),
        dynamo=True,
        external_data=False,
    )


def ensure_graph(model_dir: str, checkpoint_path: str, torch_model: Any) -> str:
    """Return the path to a graph derived from this checkpoint, exporting it if need be.

    The cache is keyed on the checkpoint and on :data:`GRAPH_VERSION` through a marker file beside
    the graph, so replacing the weights or changing the export re-derives instead of reusing.

    **This writes into the model directory, which assumes the model directory is writable.** It is
    for a downloaded model, which is where this one lives — the catalogue puts it under the user's
    own data directory, and keeping the derived graph beside the weights means removing the model
    removes it too. A model shipped inside the installation directory would not be writable, and the
    export would raise here; the caller turns that into a refusal naming the provider, since a named
    provider is never fallen back from.

    **The two files it writes are invisible to everything that reports a model's size.** The store
    sizes a multi-file entry from the catalogue manifest — `model.Files.Sum(...)` — so this graph
    (about 26.7 MB) and its `.key` marker are counted by neither the Models tab's disk total, nor
    `uindosill models`, nor `uindosill doctor`. They are still deleted with the model, because they
    live inside its directory, so the under-report is a display gap rather than a leak. It is
    recorded here and in `docs/UNPROVEN.md` rather than fixed, because the manifest sum is what
    makes those figures agree with the catalogue before anything is downloaded.
    """
    graph_path = os.path.join(model_dir, GRAPH_FILENAME)
    marker_path = f"{graph_path}.key"
    fingerprint = _checkpoint_fingerprint(checkpoint_path)

    if os.path.isfile(graph_path) and os.path.isfile(marker_path):
        try:
            with open(marker_path, encoding="utf-8") as handle:
                if handle.read().strip() == fingerprint:
                    return graph_path
        except OSError:
            pass

    export_graph(torch_model, graph_path)
    with open(marker_path, "w", encoding="utf-8") as handle:
        handle.write(fingerprint)
    return graph_path


class OnnxSpeakerEmbedding:
    """A drop-in for `PyannoteAudioPretrainedSpeakerEmbedding`, on ONNX Runtime.

    **It wraps the torch embedder rather than replacing it**, and delegates everything except
    `__call__`. The pipeline reads `sample_rate`, `metric`, `dimension` and `min_num_samples` off
    this object, and the last of those is a binary search that calls the model; reproducing any of
    them here would be four more chances to differ from the path the published figures describe.
    Only the one method that does the arithmetic is overridden.

    The fbank stays in torch on the CPU. It is `torchaudio.compliance.kaldi.fbank` computed per item
    in a Python loop, it does not export — `aten.hamming_window` has no ONNX lowering, which is why
    upstream's own graph takes features rather than a waveform — and it was measured at **1.2%** of
    this stage's compute, so it bounds nothing worth restructuring for.
    """

    def __init__(self, inner: Any, session: Any, provider: str) -> None:
        self._inner = inner
        self._session = session
        self.provider = provider

    def __getattr__(self, name: str) -> Any:
        # Reached only for attributes this class does not define, which is everything the pipeline
        # asks of the embedder apart from `__call__`.
        return getattr(self._inner, name)

    def to(self, device: Any) -> "OnnxSpeakerEmbedding":
        """The pipeline moves its embedder; the provider is fixed at construction.

        Returning self rather than raising keeps `SpeakerDiarization.to` working, and there is
        nothing to move: the session's placement was decided when it was built.
        """
        return self

    def __call__(self, waveforms: Any, masks: Any = None) -> np.ndarray:
        """Embeddings for a batch of chunks, matching `self._inner(waveforms, masks)`.

        **`masks=None` is passed to the graph as ones, which agrees with `StatsPool`'s None branch
        everywhere the pipeline goes and not everywhere.** That branch takes an unweighted mean and
        `std(correction=1)`; the weighted branch divides by `v1 - v2 / v1`, which for all-ones
        weights is `n - n / n = n - 1`, so the two compute the same statistic and differ only in
        float ordering and in the `1e-8` guards. Carrying an optional input through ONNX Runtime to
        express the same thing would cost a second graph signature for no difference in the answer.

        **Where they part is a single pooled frame**, `n = 1`: the None branch divides by `n - 1 = 0`
        and yields NaN, while this one divides by `1e-8` with a zero numerator and yields 0. The
        pipeline cannot reach it — `min_num_samples` is 400 samples and the ResNet pools 1598 fbank
        frames down to about 200 — but a caller embedding a few milliseconds directly would see the
        two paths disagree rather than round differently.

        **The parity fixture does not test this**, and an earlier draft of this docstring said it
        did. The fixture always supplies a mask, deliberately a 0/1 mixture, precisely so that the
        weighted path is exercised; all-ones would reduce it to an unweighted mean and test less. The
        equivalence above is established by reading `StatsPool`, not by the fixture.
        """
        import torch

        with torch.inference_mode():
            feats = self._inner.model_.compute_fbank(waveforms)

        feats_array = np.ascontiguousarray(feats.numpy(force=True), dtype=np.float32)
        if masks is None:
            weights_array = np.ones(feats_array.shape[:2], dtype=np.float32)
        else:
            weights_array = np.ascontiguousarray(masks.numpy(force=True), dtype=np.float32)

        outputs = self._session.run(
            [OUTPUT_EMBEDDING],
            {INPUT_FEATS: feats_array, INPUT_WEIGHTS: weights_array},
        )
        return np.asarray(outputs[0], dtype=np.float32)
