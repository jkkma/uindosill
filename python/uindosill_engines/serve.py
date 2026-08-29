"""The sidecar's dispatch loop.

Started by the .NET host as `python -m uindosill_engines`, kept alive for a whole run, and driven
over stdin/stdout by :mod:`.protocol`. It holds the loaded models so a batch pays the diariser
load and the 1.34 GiB translator load once rather than per file. (The diariser's cost used to be
stated here as 453 MiB; that was the ONNX graph now in `attic/sortformer/`, and the resident size of
the pipeline that replaced it has not been measured.)

Nothing here decides anything. Which model to use, where the weights are, what post-processing to
apply and what to do about a speaker count the model cannot honour are all the host's business and
arrive in the request — the same division `ISpeakerLabeller` already draws on the .NET side, kept
deliberately, so that moving the engine across a process boundary does not also move the policy.
"""

from __future__ import annotations

import os
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

        **Mostly the translator's question now.** The diariser is a torch pipeline since
        2026-08-27, so its `auto` mostly settles on a torch device — the CPU, or `cuda` where a
        CUDA torch is installed — but since 2026-08-28 it is not "the CPU by construction": with
        the two derived graphs exported into the model directory, its resolver can elect an ONNX
        provider for the embedding stage. `auto` reports whatever its resolver settles on, asked
        rather than restated, which is exactly why this paragraph does not enumerate the cases.

        DirectML is deliberately absent from `usable`: for the translator it is not faithful at all,
        0 of 32 FLEURS sentences matching the CPU, the decoder falling into a repetition loop, at
        21.5x *slower*. **The measured diarisation reason that stood beside it has gone to the
        attic with the graph it was measured on** — DirectML scoring 53.15% DER against the CPU's
        16.33% is a fact about Sortformer, and repeating it here, where the only diariser is one
        DirectML cannot run at all, would be attaching a number to the wrong engine.

        **The AMD path is still an open question, and WebGPU won the part of it that was asked.**
        WebGPU was evaluated ahead of DirectML on 2026-08-21 and took both engines then; one of
        those engines has since left. It is vendor-neutral like DirectML, sits on D3D12 or Vulkan
        underneath, and reproduces the CPU on the translator where DirectML does not. **What has not
        been asked is the part the question was about** — no AMD GPU has run any of this. The bar is
        unchanged: output matching the CPU reference, on an AMD GPU. The Ryzen AI NPU
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
            # The diariser's `auto` depends on whether the loaded model directory holds the
            # derived graphs, so the loaded path is passed when there is one. With nothing loaded
            # it answers `["cpu"]`, which is what a load would take on a machine that has never
            # exported them — the honest answer to a question asked without a model.
            "auto": {
                "diariser": diariser_auto(
                    self.diariser.model_path if self.diariser.loaded else None
                ),
                "translator": translator_auto(),
            },
            "onnxruntime": ort.__version__,
        }

    def load(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        engine = message.get("engine")
        if engine == "diariser":
            capabilities = self.diariser.load(
                path=message.get("path", ""),
                model_id=message.get("modelId", ""),
                threads=int(message.get("threads", 0) or 0),
                provider=message.get("provider", "auto"),
                profile=bool(message.get("profile", False)),
                # Absent means "the model's own", which is not the same as any number this could
                # default to — so it stays None rather than acquiring a value here. The diariser
                # reads it as the checkpoint's `batch_size`.
                batch_size=(
                    int(message["batchSize"])
                    if message.get("batchSize") is not None
                    else None
                ),
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

    def export_diariser_graphs(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """Derives the diariser's ONNX graphs so an execution provider has something to run.

        **Here rather than in a script the user is told to run.** The graphs are the only way to the
        GPU on a machine whose torch is the CPU build, and telling somebody to run a Python script
        is not a feature — the application calls this and the Models tab shows the progress.

        Needs no loaded engine, and deliberately: it is a property of the weights on disk, not of a
        session, and asking for it while a pipeline is loaded for labelling would hold two copies of
        the checkpoints in memory for no reason.

        `parity` defaults off. The sweep is the slow half and its value is in the lab, where the
        question is whether an export route can be trusted at all; the application is re-deriving
        graphs from the same weights by the same code and has nothing new to learn from it.
        """
        from .diariser import onnx_export

        path = message.get("path", "")
        if not path or not os.path.isdir(path):
            raise RequestError("model", f"the diarisation model directory is not at {path}")

        request_id = message.get("id")

        def progress(done: int, total: int) -> None:
            channel.progress(request_id, done, total)

        manifest = onnx_export.export(
            model_dir=path,
            out_dir=message.get("out") or None,
            parity=bool(message.get("parity", False)),
            progress=progress,
        )
        return {"manifest": manifest}

    def parity(self, message: dict[str, Any], channel: Channel) -> dict[str, Any]:
        """Checks a loaded engine against its committed reference.

        Cheap enough — two chunks of mel, or six short sentences — to run before every non-CPU run
        rather than only when somebody thinks to ask. That matters because the failure it catches is
        silent: a provider can score 53% diarisation error while emitting speaker turns that read as
        perfectly ordinary, or return 512 tokens of one repeated phrase that is still a sentence.

        The two engines' checks are not the same instrument, and their modules say so — one compares
        probabilities with three orders of magnitude of daylight, the other compares strings with
        none. The host's question is the same either way, so it is one op.

        **Only the translator has a fixture**, and since 2026-08-27 that is the whole list. The
        refusal for the diariser lives in :meth:`_engine_for` rather than here — see the reasoning
        there, which changed when the ONNX diariser was shelved and the same sentence became the
        right answer for `placement` too.
        """
        name = message.get("engine", "diariser")
        engine, module = self._engine_for(name)
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

        # The check's result is read, not just its side effect. It exists here to make the
        # profiled sessions execute, but when the parity fixture is missing it reports that
        # precisely and decodes nothing — and a discarded report turned that into the empty
        # profile below, diagnosed as "loaded without `profile: true`": a false claim
        # prescribing a reload that cannot ever fix it.
        checked = module.check(engine._engine)
        if not checked.get("available", True):
            raise RequestError(
                "request",
                f"placement needs the {name}'s parity check to run the sessions, and it could "
                f"not: {checked.get('reason', 'no reason given')}")

        inner = engine._engine
        wanted = engine.capabilities()["backend"]
        sessions = (
            inner.sessions_by_part() if hasattr(inner, "sessions_by_part") else {"model": inner.sess}
        )

        # **"No sessions" and "sessions that recorded nothing" are different failures and only one
        # of them is about profiling**, and answering the first with "reload with `profile: true`"
        # prescribes a fix that cannot work no matter how many times it is tried. Separated
        # 2026-08-27, when the diariser was a torch pipeline that could still reach this code and
        # hit the wrong branch. It cannot reach it now — `_engine_for` turns it away first — so this
        # guards the translator alone, which is a narrower job than the one it was written for and
        # is kept because a future torch-backed engine would want it back rather than want it
        # rediscovered.
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

        # One table, because `_engine_for` above admits one engine. The diariser had the other and
        # it went to `attic/sortformer/` with the graph it described.
        from .translator.engine import PROVIDERS as TRANSLATOR_PROVIDERS
        provider_name = TRANSLATOR_PROVIDERS[wanted][0]

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
            # **The diariser has no ONNX graph, so both questions this helper serves are the wrong
            # question for it**, and one sentence answers both. `parity` compares two paths to one
            # answer; `placement` asks which provider owned which node. This pipeline is torch on
            # both stages, so it has one path and no nodes to place, and neither op has anything to
            # report rather than having something to report that happens to be empty.
            #
            # **Refused here rather than in each caller, which is a reversal.** While the ONNX
            # diariser was still loadable the refusal had to sit in `parity` and
            # `write_parity_reference` separately, because a parity-shaped sentence about fixtures
            # is not an answer to a `placement` request — a distinction a review caught minutes
            # after the pyannote swap landed. Shelving Sortformer removed the case that made them
            # differ: the reason is now the same reason for every caller, and stating it once is
            # what stops the three copies drifting.
            raise RequestError(
                "request",
                "the diariser is a torch pipeline with no ONNX graph: it has no execution provider "
                "to place nodes on and no second path to compare against, so it has no parity "
                "fixture. Both ops apply to the translator.",
            )

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

        # **The embedder guard that stood here went with the diariser on 2026-08-27.** It existed
        # because DiariZen's reference was its *torch* embedder while ONNX Runtime's CPU provider
        # also reported `cpu`, so a maintenance run with an ONNX embedder installed would have
        # overwritten the committed reference and blessed the divergence the fixture detects. The
        # translator has one runtime and no such ambiguity, and `_engine_for` is what makes that a
        # closed set rather than an assumption. An engine with a negotiable embedder is what would
        # need it again; `attic/sortformer/` is where the reasoning is kept.
        produced = module.compute(engine._engine)
        path = module.reference_path()

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
            "exportDiariserGraphs": session.export_diariser_graphs,
            "placement": session.placement,
            "writeParityReference": session.write_parity_reference,
        },
    )


if __name__ == "__main__":
    sys.exit(main())
