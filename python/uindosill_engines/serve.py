"""The sidecar's dispatch loop.

Started by the .NET host as `python -m uindosill_engines`, kept alive for a whole run, and driven
over stdin/stdout by :mod:`.protocol`. It holds the loaded models so a batch pays the 453 MiB
diariser load and the 1.34 GiB translator load once rather than per file.

Nothing here decides anything. Which model to use, where the weights are, what post-processing to
apply and what to do about a speaker count the model cannot honour are all the host's business and
arrive in the request — the same division `ISpeakerLabeller` already draws on the .NET side, kept
deliberately, so that moving the engine across a process boundary does not also move the policy.
"""

from __future__ import annotations

import platform
import sys
from typing import Any

from .diariser import Diariser
from .protocol import PROTOCOL_VERSION, Channel, RequestError, claim_stdout, serve


class Session:
    """One sidecar's worth of state."""

    def __init__(self) -> None:
        self.diariser = Diariser()

    # -- handlers ---------------------------------------------------------------------------

    def hello(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """Identifies the sidecar before any weights are touched.

        The host calls this first and refuses a protocol number it does not know, which is what
        stops a stale bundled Python from being driven by a newer host — a failure that would
        otherwise surface as a confusing error several megabytes into a model load.
        """
        return {
            "protocol": PROTOCOL_VERSION,
            "python": platform.python_version(),
            "implementation": platform.python_implementation(),
            "platform": platform.platform(),
            "engines": ["diariser"],
        }

    def providers(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """What ONNX Runtime can actually reach on this machine.

        Asked rather than inferred. The host could look for a driver or shell out to nvidia-smi and
        would still be guessing at the thing that matters — whether *this* ONNX Runtime build, with
        the CUDA and cuDNN libraries it links but does not ship, can initialise the provider. Only
        onnxruntime knows that, so onnxruntime is asked.

        `available` is what is present; `usable` is what this host is willing to select without
        being told to. DirectML is deliberately absent from `usable`: measured 2026-08-21 it is
        faithful only with the graph optimiser disabled, and that finding is from one NVIDIA card
        and one driver. Until it has been measured on an AMD GPU — the case DirectML exists for —
        selecting it automatically would be shipping an unproven path to exactly the users who
        cannot check it.

        **The AMD path is an open question and DirectML is not assumed to be its answer.** Decided
        2026-08-21: WebGPU is to be evaluated first. It is vendor-neutral like DirectML, sits on
        D3D12 or Vulkan underneath, and `onnxruntime-webgpu` is published at 1.27.0 against
        DirectML's 1.24.4 — closer to this project's 1.29.0 pin. Whichever wins has to clear the
        same bar: probabilities matching the CPU reference, on an AMD GPU, before it is selected
        for anyone. The Ryzen AI NPU (`VitisAIExecutionProvider`) is a separate question and is
        deferred past v1.0.
        """
        import onnxruntime as ort

        available = list(ort.get_available_providers())
        usable = [p for p in ("CUDAExecutionProvider", "CPUExecutionProvider") if p in available]
        return {
            "available": available,
            "usable": usable,
            "preferred": "cuda" if "CUDAExecutionProvider" in usable else "cpu",
            "onnxruntime": ort.__version__,
        }

    def load(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        engine = message.get("engine")
        if engine == "diariser":
            capabilities = self.diariser.load(
                path=message.get("path", ""),
                model_id=message.get("modelId", ""),
                threads=int(message.get("threads", 0) or 0),
                provider=message.get("provider", "cpu"),
                graph_optimization=message.get("graphOptimization"),
            )
            return {"capabilities": capabilities}
        raise RequestError("request", f"unknown engine {engine!r}")

    def parity(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """Checks the loaded engine against the committed reference.

        Cheap enough — two chunks — to run before every non-CPU diarisation rather than only when
        somebody thinks to ask. That matters because the failure it catches is silent: a provider
        can score 53% diarisation error while emitting speaker turns that read as perfectly
        ordinary.
        """
        if not self.diariser.loaded:
            raise RequestError("model", "parity was asked for before load")

        from .diariser import parity as parity_check

        result = parity_check.check(self.diariser._engine)
        result["backend"] = self.diariser.capabilities()["backend"]
        return result

    def write_parity_reference(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """Regenerates the committed reference from the loaded engine.

        Maintenance, not a user path. The reference is what every other stack is judged against, so
        it is produced on the CPU deliberately and refuses to be produced on anything else — a
        reference taken from a diverging provider would bless that provider's divergence and fail
        every faithful machine.
        """
        import numpy as np

        from .diariser import parity as parity_check

        if not self.diariser.loaded:
            raise RequestError("model", "the engine must be loaded first")

        backend = self.diariser.capabilities()["backend"]
        if backend != "cpu":
            raise RequestError(
                "request",
                f"the parity reference must be produced on the cpu, not {backend}: a reference taken "
                "from a provider that diverges would bless its divergence and fail every faithful machine.")

        probabilities = parity_check.compute(self.diariser._engine)
        path = parity_check.reference_path()
        np.save(path, probabilities.astype(np.float32))
        return {"path": path, "frames": int(probabilities.shape[0]), "backend": backend}

    def label(self, message: dict[str, Any], channel: Channel) -> None:
        """Diarises one file, reporting progress as it goes.

        Replies for itself rather than returning, because progress has to interleave with the
        work: a two-and-a-half-hour recording is minutes of silence otherwise, and the window has
        a progress bar to feed.
        """
        request_id = message.get("id")
        last = [-1]

        def progress(completed: int, total: int) -> None:
            # One message per whole percent. The host reads these on a background thread and a
            # chunk-per-message on a long file is thousands of wake-ups for no extra information.
            percent = int(100 * completed / total) if total else 0
            if percent != last[0]:
                last[0] = percent
                channel.progress(request_id, completed, total)

        turns = self.diariser.label(
            wav_path=message.get("wav", ""),
            post_processing=message.get("postProcessing"),
            progress=progress,
        )
        channel.result(request_id, turns=turns)
        return None


def main(argv: list[str] | None = None) -> int:
    # Before anything imports torch: see protocol.claim_stdout for why this is the first thing.
    channel = Channel(claim_stdout())
    session = Session()
    return serve(
        channel,
        {
            "hello": session.hello,
            "providers": session.providers,
            "load": session.load,
            "label": session.label,
            "parity": session.parity,
            "writeParityReference": session.write_parity_reference,
        },
    )


if __name__ == "__main__":
    sys.exit(main())
