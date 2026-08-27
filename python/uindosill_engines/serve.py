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

from .diariser import DIARIZEN, SORTFORMER, Diariser
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
                profile=bool(message.get("profile", False)),
                # Which of the two diarisers, named by the host because the host is what resolved
                # the catalogue entry. Defaulted rather than required so that the field reads the
                # same as every other optional one here; the protocol number is what actually stops
                # a stale sidecar being asked for the engine it does not have.
                kind=message.get("kind", SORTFORMER),
            )
            return {"capabilities": capabilities}
        if engine == "translator":
            capabilities = self.translator.load(
                path=message.get("path", ""),
                model_id=message.get("modelId", ""),
                threads=int(message.get("threads", 0) or 0),
                provider=message.get("provider", "cpu"),
                graph_optimization=message.get("graphOptimization"),
                profile=bool(message.get("profile", False)),
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

    def placement(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """Where the graph actually ran, for an engine loaded with `profile: true`.

        **The other half of `parity`, and the half nothing checked until 2026-08-25.** Parity asks
        whether a provider reproduces the published figure; this asks whether it ran the graph at
        all. The two are independent, and the gap between them is not hypothetical: a provider that
        registers, builds a session and then owns no node — everything it declined placed on the CPU
        without a word — passes parity *because* it is the CPU. Measured on an NPU that day, six of
        eight graphs did exactly that, at CPU speed, with nothing in any log to say so.

        Runs the engine's own parity check first, because ONNX Runtime's profile records executions
        rather than the partition plan: a session that has not run yields nothing to count. That is
        also why this is cheap — the check is two chunks of mel or six short sentences, which is what
        `parity` already costs.

        **No threshold is asserted.** A healthy accelerated session legitimately leaves shape
        operators on the CPU, so what counts as a good share is a per-graph question this side cannot
        answer; the counts go to the host with `ranThere` — the one unambiguous signal — beside them.
        Profiling ends here, so the cost does not follow the run.
        """
        from . import placement as placement_mod

        name = message.get("engine", "diariser")
        engine, module = self._engine_for(name)
        module.check(engine._engine)

        inner = engine._engine
        wanted = engine.capabilities()["backend"]
        sessions = (
            inner.sessions_by_part() if hasattr(inner, "sessions_by_part") else {"model": inner.sess}
        )

        # **"No sessions" and "sessions that recorded nothing" are different failures and only one
        # of them is about profiling.** The second diariser runs its segmentation in torch and, on
        # the default `auto`, its embedder too — so it owns no ONNX session at all, and answering
        # that with "reload with `profile: true`" prescribes a fix that cannot work no matter how
        # many times it is tried. Separated 2026-08-27, after a review pointed out that the default
        # configuration of one engine hits the wrong branch.
        if not sessions:
            raise RequestError(
                "request",
                f"the {name} has no ONNX Runtime session to measure: it is running entirely in "
                "torch, which `placement` cannot see into because there are no graph nodes for "
                "ONNX Runtime to have placed. Load with an execution provider first.")

        parts = {part: placement_mod.end(session) for part, session in sessions.items()}
        if not any(parts.values()):
            raise RequestError(
                "request",
                f"the {name} reported no profile, which is what happens when it was loaded without "
                "`profile: true`. Placement cannot be measured after the fact — ONNX Runtime reads "
                "the setting when the session is built — so reload with it set.")

        from .translator.engine import PROVIDERS as TRANSLATOR_PROVIDERS
        from .diariser.engine import PROVIDERS as DIARISER_PROVIDERS
        table = DIARISER_PROVIDERS if name == "diariser" else TRANSLATOR_PROVIDERS
        provider_name = table[wanted][0]

        return {
            "engine": name,
            "backend": wanted,
            "parts": {
                part: placement_mod.summarise(counts, provider_name)
                for part, counts in parts.items()
            },
        }

    def _engine_for(self, name: str) -> tuple[Any, Any]:
        """The loaded engine and its parity module, or the reason there is not one."""
        if name == "diariser":
            if not self.diariser.loaded:
                raise RequestError("model", "parity was asked for before the diariser was loaded")

            # **The two diarisers have different fixtures, because they are different instruments.**
            # Sortformer's is two chunks of synthetic mel through the streaming loop, comparing the
            # probabilities one ONNX graph returns against the same graph on the CPU. DiariZen's is
            # a batch of synthetic waveforms through the embedder, comparing against **torch** —
            # the path that shipped before the ONNX embedder existed and the one its published
            # figures describe. Running Sortformer's against DiariZen would fail somewhere inside on
            # a missing attribute and read as a bug in this project rather than as the wrong
            # question. Until 2026-08-26 there was no second fixture and this refused instead.
            if self.diariser.kind == DIARIZEN:
                from .diariser import embedding_parity

                return self.diariser, embedding_parity

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

        capabilities = engine.capabilities()
        backend = capabilities["backend"]
        if backend != "cpu":
            raise RequestError(
                "request",
                f"the parity reference must be produced on the cpu, not {backend}: a reference taken "
                "from a provider that diverges would bless its divergence and fail every faithful machine.")

        # **`backend == "cpu"` is not sufficient for the second diariser, and the gap is exactly the
        # one this guard exists to close.** Its reference is the *torch* embedder, and ONNX Runtime's
        # CPU provider also reports `cpu` — so without this, a maintenance run made while an ONNX
        # embedder was installed would overwrite the committed reference with ONNX output and bless
        # the very divergence the fixture is there to detect. Every later machine would then be
        # judged against it, including the torch path, which would start failing its own gate.
        embedding_backend = capabilities.get("embeddingBackend", "")
        if embedding_backend and not embedding_backend.startswith("torch:"):
            raise RequestError(
                "request",
                f"the diariser's parity reference must be produced by the torch embedder, not "
                f"{embedding_backend}: it is what every ONNX embedder is judged against, and taking "
                "it from one of them would bless that one's divergence. Load with provider 'torch'.")

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
            "placement": session.placement,
            "writeParityReference": session.write_parity_reference,
        },
    )


if __name__ == "__main__":
    sys.exit(main())
