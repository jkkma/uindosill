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

**And it is also off because, on the one machine that could bind, binding crashed.** Measured
2026-08-23 on the RTX 5080 with onnxruntime-gpu 1.29.0, optimum 2.1.0 and torch 2.13.0+cu130 — CUDA
torch, so `optimum` had a device to bind to: flipping `use_io_binding` on after the load, through
`optimum`'s own setter, aborted the process on the first decode step — `Non-zero status code
returned while running Mul node. Name:'/Mul' ... CUDA error cudaErrorIllegalAddress: an illegal
memory access was encountered`, caught by torch's abort handler rather than raised to Python. A
native abort is not something the sidecar can report and fall back from; it is the host losing its
translator mid-run. So the sentence above is true and incomplete: binding would be faster *if it
ran*, and on the stack it was tried on it does not. Turning it on is a measurement to take again,
against a different ORT or optimum, not a flag to flip.
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

#: Re-normalise the scores after the logits processors have run, so a beam's cumulative log
#: probability is a log probability rather than whatever the processors left behind.
#:
#: **Measured 2026-09-04, and it is not a preference.** A Marian checkpoint's
#: ``decoder_start_token_id`` is its pad token, and ``bad_words_ids`` bans that token so it cannot
#: be emitted mid-sequence. Where the model puts most of its mass on pad at the first decoder step,
#: masking it leaves a distribution that no longer sums to one — correctly *ordered*, but shifted
#: down by a large constant. Beam search divides a hypothesis' cumulative score by its length, so
#: that constant is diluted by generating more tokens, and the search runs away into repetition.
#:
#: ``staka/fugumt-ja-en`` is such a checkpoint: pad scores 0.000 at step one and every real token
#: −44 or worse, and at beam 6 猫はかわいいです。 decodes to "The Cat is slurpy slurp slurb slur
#: slur sl sl s s". With this flag it decodes to "The cat is cute." Greedy was always fine, because
#: greedy does not normalise by length.
#:
#: **The shipped checkpoint is unaffected, and that was checked rather than assumed**:
#: ``opus-mt-tc-bible-big-mul-deu_eng_nld`` scores its correct first token at −0.175 and pad at
#: −10.840, so masking pad changes almost nothing and re-normalising changes nothing at all — its
#: six recorded smoke sentences come back **identical** with the flag on and off. Every figure in
#: ``docs/UNPROVEN.md`` § *Translating into English* therefore still describes what this code does.
RENORMALIZE_LOGITS = True

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
#: CUDA leads, as of 2026-08-23, and WebGPU is the provider the bundle actually runs — both of
#: those are true at once, and the reason is `resolve_auto`: it keeps only what the wheel was
#: compiled with, so this order decides nothing on the shipped `onnxruntime-webgpu` wheel, where
#: CUDA is not on the list and WebGPU is tried first as it always was. What the order decides is a
#: machine running the sidecar on a CUDA wheel, which today is the maintainer's desktop.
#:
#: Both are faithful, which is the precondition: on 32 FLEURS es_419 sentences at beam 6, WebGPU
#: returned the CPU's own translations on 32 of 32 at 1.30x the speed, and CUDA on 240 of 240
#: (2026-08-21). CUDA is then put first on speed, measured 2026-08-23 on the RTX 5080 over eight
#: Spanish sentences: 0.142 s/sentence against WebGPU's 0.189 and the CPU's 0.289 — 1.33x WebGPU —
#: with the committed parity fixture passing 6 of 6 on the same stack, string-identical to a CPU
#: control that also passed 6 of 6. That is a timing and a smoke test, not the gate corpus; the
#: 240 of 240 is what makes it safe to prefer, and the 0.142 is only what makes it worth preferring.
#:
#: It stays out of the bundle for the reason it was never in it: about 1.65 GB of CUDA and cuDNN
#: libraries in the installer. This order costs that nothing.
#:
#: **DirectML is deliberately not here and cannot be reached by `auto`.** Measured the same day it
#: agreed with the CPU on 0 of 32 sentences — the decoder falls into a repetition loop — while
#: running 21.5x *slower*. The same study reports the encoder and the decoder each clean on DirectML
#: at full optimisation when driven directly, which puts the fault in `optimum`'s merged KV-cache
#: path rather than in the provider, and is why disabling the graph optimiser does not rescue it the
#: way it rescues the diariser. It stays selectable by name so that measuring it stays possible.
AUTO_ORDER = ["cuda", "webgpu"]


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
                 graph_optimization: str | None = None, profile: bool = False):
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

        if profile:
            # Off unless the host asks. `optimum` hands these options to every session it builds, so
            # one flag covers the encoder and the decoder alike — which is what makes a per-session
            # answer possible below.
            from .. import placement
            placement.enable(options)

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
        #
        # **Registration is not placement.** A provider can pass this check and own no node of the
        # graph, everything it declined placed on the CPU without a word — measured on an NPU
        # 2026-08-25. Parity would still pass, because a provider that is secretly the CPU
        # reproduces the CPU. `uindosill_engines.placement` answers the other half.
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
        """What each of `optimum`'s sessions was actually given.

        Registration, not placement — see :mod:`uindosill_engines.placement` and :meth:`sessions_by_part`.
        """
        found: dict[str, list[str]] = {}
        for name, session in self.sessions_by_part().items():
            found[name] = list(session.get_providers())
        return found

    def sessions_by_part(self) -> dict[str, Any]:
        """The underlying ONNX Runtime sessions `optimum` built, by the part each drives.

        The merged export legitimately yields two rather than three — `decoder_with_past` is folded
        into `decoder` — so a caller must not assume a count.
        """
        found: dict[str, Any] = {}
        for name in ("encoder", "decoder", "decoder_with_past"):
            part = getattr(self.model, name, None)
            session = getattr(part, "session", None) if part is not None else None
            if session is not None:
                found[name] = session
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
                renormalize_logits=RENORMALIZE_LOGITS,
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
