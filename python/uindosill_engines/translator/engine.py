"""The Marian checkpoint, loaded by `optimum` and decoded by `transformers`.

What this replaces is ~2,760 lines of C#: a SentencePiece implementation, a Marian tokenizer, a
beam search and an ONNX decoder loop, all of them reimplementing something HuggingFace already
ships and all of them a second place for a decode to drift. Here the search is
`transformers.generate` and the sessions are `optimum`'s, so the only thing written down is which
decode was measured.

**The decode settings below are not defaults.** The graphs are pinned and the search over them is
not, so every one of them is a degree of freedom that changes the English and would quietly stop it
being the thing that was scored. They are what the 2026-08-20 gate run passed — 8,149 sentences
across 24 languages — and changing one is a decision to be recorded rather than a knob to be
turned. `generation_config.json` says `num_beams: 4` and nothing this project has measured used it.

**IO binding is off, and that is a floor rather than a ceiling.** `optimum` binds the KV cache to a
torch device, the bundle ships CPU torch, and the WebGPU figure this project publishes was measured
with binding off for exactly that reason: 0.459 s/sentence against the CPU's 0.595. A machine that
could bind would be faster than that number, never slower.
"""

from __future__ import annotations

import os
from typing import Any

#: Beam width. Six — see the module docstring, and do not take it from the config file.
NUM_BEAMS = 6

#: The longest continuation, in tokens. Matches the `max_new_tokens=512` the gate run passed rather
#: than `generation_config.json`'s `max_length`, which counts the prompt too.
MAX_NEW_TOKENS = 512

#: Exponent on the length a finished hypothesis is divided by, and whether to stop as soon as every
#: beam has finished. Both are HuggingFace's own defaults, which is precisely why they are written
#: down: they were never chosen, they were inherited, and an inherited value nobody wrote down is
#: one somebody later changes thinking it was arbitrary. Together they mean a finished hypothesis is
#: scored by its mean log probability per token, and that the search runs on while an open beam
#: could still beat the worst finished one.
LENGTH_PENALTY = 1.0
EARLY_STOPPING = False

#: Execution providers the host may ask for, in the order ONNX Runtime is given them. Every one
#: carries its own measurement; see :data:`AUTO_ORDER` for which are reachable without being named.
PROVIDERS = {
    "cpu": ["CPUExecutionProvider"],
    "cuda": ["CUDAExecutionProvider", "CPUExecutionProvider"],
    "dml": ["DmlExecutionProvider", "CPUExecutionProvider"],
    "webgpu": ["WebGpuExecutionProvider", "CPUExecutionProvider"],
}

#: What `auto` will settle on, best first. Separate from the diariser's list of the same name and
#: deliberately not shared with it: the two engines are cleared by different evidence — the
#: diariser's is a diarisation error rate and this one's is string identity against the CPU — and a
#: single shared order would make one engine's automatic choice rest on the other's measurement.
#:
#: WebGPU leads because it is the only non-CPU provider measured to return the CPU's own
#: translations: on 32 FLEURS es_419 sentences at beam 6, 32 of 32 were string-identical to the
#: CPU's at 1.30x the speed (2026-08-21). CUDA also matched, on 240 of 240, but it needs about
#: 1.65 GB of CUDA and cuDNN libraries in the installer to do it.
#:
#: **DirectML is deliberately not here and cannot be reached by `auto`.** Measured the same day it
#: agreed with the CPU on 0 of 32 sentences — the decoder falls into a repetition loop — while
#: running 21.5x *slower*. The same study reports the encoder and the decoder each clean on DirectML
#: at full optimisation when driven directly, which puts the fault in `optimum`'s merged KV-cache
#: path rather than in the provider, and is why disabling the graph optimiser does not rescue it the
#: way it rescues the diariser. It stays selectable by name so that measuring it stays possible.
AUTO_ORDER = ["webgpu", "cuda"]


def resolve_auto() -> list[str]:
    """The providers `auto` will try on this machine, best first.

    A shortlist rather than an answer, for the reason the diariser's twin gives:
    `get_available_providers()` reports what the wheel was compiled with, not what this machine can
    create, so `WebGpuExecutionProvider` is in it even where no adapter can back it. The candidates
    are tried in order by :meth:`Translator.load`, which moves to the next when a session refuses to
    build; without that, a machine whose WebGPU cannot initialise would have no translator at all
    rather than a CPU one.
    """
    import onnxruntime as ort

    available = set(ort.get_available_providers())
    return [p for p in AUTO_ORDER if PROVIDERS[p][0] in available] + ["cpu"]


class MarianEngine:
    """One loaded checkpoint: two graphs, a tokenizer, and the decode that was measured over them."""

    def __init__(self, model_dir: str, threads: int = 0, provider: str = "cpu",
                 graph_optimization: str | None = None):
        if provider not in PROVIDERS:
            raise ValueError(f"unknown provider {provider!r}; choose one of {sorted(PROVIDERS)}")

        import onnxruntime as ort

        # onnxruntime-gpu links CUDA and cuDNN DLLs it does not ship. Without this the session falls
        # back to the CPU with the failure written only to stderr — which is precisely the silent
        # fallback the assertion below exists to catch.
        if provider != "cpu":
            ort.preload_dlls()

        options = ort.SessionOptions()
        if threads:
            options.intra_op_num_threads = threads
        if graph_optimization:
            options.graph_optimization_level = getattr(ort.GraphOptimizationLevel, graph_optimization)

        from optimum.onnxruntime import ORTModelForSeq2SeqLM
        from transformers import AutoTokenizer

        self.tokenizer = AutoTokenizer.from_pretrained(model_dir)
        self.model = ORTModelForSeq2SeqLM.from_pretrained(
            model_dir,
            use_cache=True,
            provider=PROVIDERS[provider][0],
            session_options=options,
            # See the module docstring: the published figure is a binding-off figure.
            use_io_binding=False,
        )

        # A provider that failed to initialise is dropped and the session runs on the CPU with no
        # error anywhere. That is indistinguishable from success except in the timings, and during
        # the 2026-08-21 study a mistyped option did exactly this and reported flawless parity.
        # `optimum` builds one session per graph, so every one of them is asked.
        self.sessions = self._registered_providers()
        if not self.sessions:
            # An empty dict makes the comparison below vacuously true, which is the day `optimum`
            # renames its parts and this check silently stops checking. The merged export
            # legitimately yields two sessions rather than three — `decoder_with_past` is folded into
            # `decoder` — so the rule is "at least one, and every one found agrees", never "three".
            raise RuntimeError(
                "no onnxruntime sessions were found on the optimum model, so the provider could not "
                "be checked. A silent fallback to the CPU is indistinguishable from success except in "
                "the timings, and is refused.")

        wanted = PROVIDERS[provider][0]
        wrong = {name: got for name, got in self.sessions.items() if wanted not in got}
        if wrong:
            raise RuntimeError(
                f"asked for {wanted} and onnxruntime registered {wrong}. The provider did not "
                "initialise; running on the CPU instead would be silent and is refused.")

        # The two fields `generation_config.json` contributes to the decode, checked rather than
        # assumed present. They are what make the search the one the gate was scored with, they are
        # the only part of it that does not come from the constants above, and a checkpoint that
        # lost them would translate fluently and differently. Read from the model rather than the
        # file so that what is checked is what `generate` will actually use.
        generation = self.model.generation_config
        if generation.bad_words_ids != [[58433]] or generation.forced_eos_token_id != 430:
            raise RuntimeError(
                f"{model_dir} declares bad_words_ids={generation.bad_words_ids} and "
                f"forced_eos_token_id={generation.forced_eos_token_id}; the checkpoint every published "
                "figure was produced on declares [[58433]] and 430. This is a different decode, so it "
                "would translate fluently and differently and nothing downstream would notice.")

        self.provider = provider
        self.graph_optimization = graph_optimization
        self.model_dir = model_dir

    def _registered_providers(self) -> dict[str, list[str]]:
        """What each of `optimum`'s sessions was actually given."""
        found: dict[str, list[str]] = {}
        for name in ("encoder", "decoder", "decoder_with_past"):
            part = getattr(self.model, name, None)
            session = getattr(part, "session", None) if part is not None else None
            if session is not None:
                found[name] = list(session.get_providers())
        return found

    @property
    def max_source_tokens(self) -> int:
        """The tokenizer's own declared limit — `model_max_length`, 512 on this checkpoint.

        Read rather than assumed, and reported to the host rather than enforced here: refusing a
        source, truncating it or warning about it is the host's decision, and this side only knows
        the number.
        """
        return int(self.tokenizer.model_max_length)

    def count(self, source: str) -> int:
        """How many tokens the model would read. The one question only the tokenizer can answer."""
        return len(self.tokenizer(source)["input_ids"])

    def translate(self, source: str) -> str:
        """One source string in, one translation out.

        One sentence per decode, which is measured rather than convenient. Batching a beam search
        pads every member to the longest and decodes until the last one finishes: on this project's
        CPU, sixteen Spanish sentences at a time cost 12.75 s each against 2.16 s each one at a
        time, a factor of six the wrong way.
        """
        import torch

        inputs = self.tokenizer([source], return_tensors="pt")
        with torch.no_grad():
            generated = self.model.generate(
                **inputs,
                num_beams=NUM_BEAMS,
                max_new_tokens=MAX_NEW_TOKENS,
                length_penalty=LENGTH_PENALTY,
                early_stopping=EARLY_STOPPING,
            )

        return self.tokenizer.batch_decode(generated, skip_special_tokens=True)[0]


#: The files a checkpoint directory must hold: eight of the nine an export produces.
#: `special_tokens_map.json` is the ninth and is genuinely redundant — `tokenizer_config.json`
#: carries the eos, pad and unk tokens — so it is not required, which is why this list is eight and
#: the count elsewhere is nine. Named here so that a partial install is refused with a list rather
#: than diagnosed from whatever `optimum` says when a graph is missing.
#:
#: **`generation_config.json` is on this list because in Python it *is* the decode.** Without it
#: `optimum` catches the missing file and builds a configuration from `config.json`, which carries no
#: `bad_words_ids` and a null `forced_eos_token_id` — so the pad-token ban and the forced end of
#: sequence both disappear, at INFO level, and the English stays fluent. In C# the file was optional
#: because the search hard-coded what it did not find there; here nothing does.
REQUIRED_FILES = (
    "encoder_model.onnx", "decoder_model_merged.onnx", "config.json",
    "generation_config.json",
    "source.spm", "target.spm", "vocab.json", "tokenizer_config.json",
)


def missing_files(model_dir: str) -> list[str]:
    return [name for name in REQUIRED_FILES if not os.path.isfile(os.path.join(model_dir, name))]
