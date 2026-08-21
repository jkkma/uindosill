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
from .translator import Translator


class Session:
    """One sidecar's worth of state."""

    def __init__(self) -> None:
        self.diariser = Diariser()
        self.translator = Translator()

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
            "engines": ["diariser", "translator"],
        }

    def providers(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """What ONNX Runtime can actually reach on this machine.

        Asked rather than inferred. The host could look for a driver or shell out to nvidia-smi and
        would still be guessing at the thing that matters — whether *this* ONNX Runtime build, with
        the CUDA and cuDNN libraries it links but does not ship, can initialise the provider. Only
        onnxruntime knows that, so onnxruntime is asked.

        `available` is what is present; `usable` is what this host is willing to select without
        being told to; and `auto` is what each engine's own resolver would actually settle on right
        now, asked of the resolvers rather than restated here so that what this op reports and what
        a load does cannot drift apart. That costs this op the engines' imports — seconds of torch
        — which is the right trade for a diagnostic that is asked once and must not lie.

        DirectML is deliberately absent from `usable`, and for two separate measured reasons. For
        the diariser it is faithful only with the graph optimiser disabled, and that finding is from
        one NVIDIA card and one driver; until it has been measured on an AMD GPU — the case DirectML
        exists for — selecting it automatically would be shipping an unproven path to exactly the
        users who cannot check it. For the translator it is not faithful at all: 0 of 32 FLEURS
        sentences matched the CPU, the decoder falling into a repetition loop, at 21.5x *slower*.

        **The AMD path is still an open question, and WebGPU won the part of it that was asked.**
        WebGPU was evaluated ahead of DirectML on 2026-08-21 and took both engines: it is
        vendor-neutral like DirectML, sits on D3D12 or Vulkan underneath, and reproduces the CPU on
        both models where DirectML reproduces neither. **What has not been asked is the part the
        question was about** — no AMD GPU has run any of this, and DirectML's diarisation defect was
        driver-mediated, so a result from one NVIDIA card is a prior rather than an answer. The bar
        is unchanged: probabilities matching the CPU reference, on an AMD GPU. The Ryzen AI NPU
        (`VitisAIExecutionProvider`) is a separate question and is deferred past v1.0.
        """
        import onnxruntime as ort

        from .diariser import resolve_auto as diariser_auto
        from .translator.engine import resolve_auto as translator_auto

        available = list(ort.get_available_providers())
        usable = [
            p for p in ("WebGpuExecutionProvider", "CUDAExecutionProvider", "CPUExecutionProvider")
            if p in available
        ]
        return {
            "available": available,
            "usable": usable,
            # Lists, best first, because `auto` tries rather than predicts — the first entry is what
            # a load will attempt and the rest are what it will fall through to.
            "auto": {"diariser": diariser_auto(), "translator": translator_auto()},
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
        if engine == "translator":
            capabilities = self.translator.load(
                path=message.get("path", ""),
                model_id=message.get("modelId", ""),
                threads=int(message.get("threads", 0) or 0),
                provider=message.get("provider", "cpu"),
                graph_optimization=message.get("graphOptimization"),
            )
            return {"capabilities": capabilities}
        raise RequestError("request", f"unknown engine {engine!r}")

    def parity(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """Checks a loaded engine against its committed reference.

        Cheap enough — two chunks of mel, or six short sentences — to run before every non-CPU run
        rather than only when somebody thinks to ask. That matters because the failure it catches is
        silent: a provider can score 53% diarisation error while emitting speaker turns that read as
        perfectly ordinary, or return 512 tokens of one repeated phrase that is still a sentence.

        The two engines' checks are not the same instrument, and their modules say so — one compares
        probabilities with three orders of magnitude of daylight, the other compares strings with
        none. The host's question is the same either way, so it is one op.
        """
        engine, module = self._engine_for(message.get("engine", "diariser"))
        result = module.check(engine._engine)
        result["backend"] = engine.capabilities()["backend"]
        return result

    def _engine_for(self, name: str) -> tuple[Any, Any]:
        """The loaded engine and its parity module, or the reason there is not one."""
        if name == "diariser":
            if not self.diariser.loaded:
                raise RequestError("model", "parity was asked for before the diariser was loaded")

            from .diariser import parity as diariser_parity

            return self.diariser, diariser_parity

        if name == "translator":
            if not self.translator.loaded:
                raise RequestError("model", "parity was asked for before the translator was loaded")

            from .translator import parity as translator_parity

            return self.translator, translator_parity

        raise RequestError("request", f"unknown engine {name!r}")

    def write_parity_reference(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """Regenerates a committed reference from the loaded engine.

        Maintenance, not a user path. The reference is what every other stack is judged against, so
        it is produced on the CPU deliberately and refuses to be produced on anything else — a
        reference taken from a diverging provider would bless its divergence and fail every faithful
        machine.
        """
        name = message.get("engine", "diariser")
        engine, module = self._engine_for(name)

        backend = engine.capabilities()["backend"]
        if backend != "cpu":
            raise RequestError(
                "request",
                f"the parity reference must be produced on the cpu, not {backend}: a reference taken "
                "from a provider that diverges would bless its divergence and fail every faithful machine.")

        produced = module.compute(engine._engine)
        path = module.reference_path()

        if name == "diariser":
            import numpy as np

            np.save(path, produced.astype(np.float32))
            return {"path": path, "frames": int(produced.shape[0]), "backend": backend}

        import json as json_io

        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            json_io.dump({"translations": list(produced)}, handle, ensure_ascii=False, indent=1)
            handle.write("\n")
        return {"path": path, "translations": len(produced), "backend": backend}

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

    def translate(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """Translates one already-marked source string.

        One segment per request rather than a batch, so the host can yield each translated segment
        as it arrives and a long transcript renders while it is still being written. A decode is
        about half a second and a protocol line is microseconds; there is nothing to save by
        batching and a way of streaming to lose.

        The source arrives carrying its target token — `TranslationRequest.Build` on the host is the
        only thing that constructs one — and `maxTokens` is the host's limit, sent so that a source
        it is about to refuse is not decoded first. The decision is still the host's: the count
        comes back either way.
        """
        max_tokens = message.get("maxTokens")
        return self.translator.translate(
            source=message.get("source", ""),
            max_tokens=int(max_tokens) if max_tokens is not None else None,
        )


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
            "translate": session.translate,
            "parity": session.parity,
            "writeParityReference": session.write_parity_reference,
        },
    )


if __name__ == "__main__":
    sys.exit(main())
