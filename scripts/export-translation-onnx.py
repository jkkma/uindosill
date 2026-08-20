#!/usr/bin/env python3
"""Export the translation checkpoint to ONNX, and record exactly what came out.

`Helsinki-NLP/opus-mt-tc-bible-big-mul-deu_eng_nld` is the route decided on 2026-08-19
(`docs/PHASES.md` § *Decided 2026-08-19 — translating the transcript into English*). It ships as an
in-house ONNX export onto the ONNX Runtime the diariser already carries, which means this repository
distributes an artefact no upstream publishes — so the script that produces it is committed beside
the code that loads it, and its output is a manifest of names, byte counts and SHA-256s rather than
a directory somebody eyeballs. The catalogue pins exact bytes; a rounded or remembered number is a
rejected download.

## The failure this script exists to defeat, and what it actually was

Recorded 2026-08-19: `optimum` 2.1.0 against `transformers` 4.57.6 failed on this architecture
inside optimum's own config normaliser, through the Python API and `optimum-cli` alike —

    File "optimum/exporters/base.py", line 151, in __init__
        self._normalized_config = self.NORMALIZED_CONFIG_CLASS(self._config)
    TypeError: NormalizedConfig.__init__() got multiple values for argument 'allow_new'

That is not a version skew between optimum and transformers, and no pinned pair of them fixes it.
It is **CPython 3.14**. `functools.partial` implements the descriptor protocol as of 3.14, so a
partial stored as a class attribute now binds the instance as its first positional argument when it
is read through `self.`. optimum stores every `NORMALIZED_CONFIG_CLASS` as exactly such a partial —
`NormalizedSeq2SeqConfig.with_args(allow_new=False, encoder_num_layers="encoder_layers", ...)` —
and reads it as `self.NORMALIZED_CONFIG_CLASS(self._config)`. Under 3.14 that call arrives as
`NormalizedSeq2SeqConfig(marian_config_object, config, allow_new=False, ...)`: the config lands in
the `allow_new` slot, which is also passed by keyword, and the constructor says so. Six lines
reproduce it with no optimum in the room at all:

    class Holder:
        ATTR = functools.partial(f, allow_new=False)
        def call(self): return self.ATTR("x")     # TypeError under 3.14, fine under 3.13

`unbind_partial_class_attributes` below restores the pre-3.14 reading by re-wrapping each partial in
`staticmethod`, whose `__get__` hands back the underlying object untouched and which stays callable
off the class. It is applied only when `functools.partial` is a descriptor, so this script is
correct on 3.13 and 3.12 as well and becomes a no-op the day optimum stops handing 3.14 a bare
partial. Nothing in the venv is modified: the fix lives here, in the caller.

## What it produces

Graphs exported with the KV cache exposed, because the decode loop needs beam search and beam-6 was
measured to keep content greedy drops (`docs/UNPROVEN.md` § *Translating into English*). optimum
offers two layouts and they are a different number of files, so both are built rather than assumed:

    split                              merged
    encoder_model.onnx                 encoder_model.onnx
    decoder_model.onnx                 decoder_model_merged.onnx
    decoder_with_past_model.onnx

The split pair stores the decoder's weights twice, once in each graph; the merged one keeps a single
decoder behind a `use_cache_branch` input. Beside the graphs, either layout carries `config.json`,
`generation_config.json` and the tokenizer, which is itself more than one file.

Each layout is built at three precisions. The int8 spread the study could not close — 227.3 MiB if
every tensor quantises, 404.4 MiB if the embeddings stay fp32, they being 26% of this model — turns
on whether `Gather` is in the quantiser's operator set, which is a knob rather than a fate. Both
ends are exported and both are measured; **this script does not choose**, because download size
against translation quality is the maintainer's call and a script that picked one would be making it
silently. Neither end came out at either of those two figures, for the reason `initialiser_report`
below explains and measures.

Writes `manifest.json` into the output directory: every file with its exact byte count and SHA-256,
the toolchain that produced it, the checkpoint revision, and per graph a census of the initialisers
by element type with every vocabulary-sized one named, so what did and did not quantise is a
reading rather than a subtraction.

## Modes

    (default)                 export every variant in --variants and write the manifest
    --variants fp32,int8      which layout/precision pairs to produce; run with --list to see them
    --out DIR                 where they go; nothing is written into the working tree
    --smoke                   translate with onnxruntime and diff against fp32 PyTorch, in-process
    --reference-json PATH     additionally diff the smoke output against a recorded decode run
                              (the 2026-08-19 spike's decode-comparison.json has this shape)
    --tokenizer-fixture PATH  write a committed token-id fixture a C# tokenizer can be held to
    --skip-export             manifest/smoke/fixture against artefacts already on disk
    --list                    print the layout/precision pairs and exit

Needs torch, transformers, optimum, optimum-onnx, onnx, onnxruntime and sentencepiece. Make a venv
OUTSIDE the working tree for it, never the system Python, and never the one a recorded finding came
out of:

    python -m venv %USERPROFILE%\\marian-onnx-venv
    %USERPROFILE%\\marian-onnx-venv\\Scripts\\pip install torch --index-url https://download.pytorch.org/whl/cpu
    %USERPROFILE%\\marian-onnx-venv\\Scripts\\pip install transformers optimum optimum-onnx onnx onnxruntime sentencepiece
    %USERPROFILE%\\marian-onnx-venv\\Scripts\\python scripts\\export-translation-onnx.py --smoke

The versions the committed manifest was produced with are recorded in the manifest itself, not here,
so this docstring cannot go stale against them.
"""

from __future__ import annotations

import argparse
import functools
import hashlib
import json
import platform
import shutil
import sys
import time
from pathlib import Path

MODEL = "Helsinki-NLP/opus-mt-tc-bible-big-mul-deu_eng_nld"

# Measured 2026-08-19 and not optional: without the target token the same Spanish segments come back
# as fluent German, the checkpoint's first declared target, and nothing downstream would catch it.
# TranslationRequest.Build in Parakeet.Core is where C# puts it on.
TARGET_TOKEN = ">>eng<<"

# The embedding lookup is a Gather over an initialiser. ORT's dynamic quantiser takes Gather by
# default, which is the 227 MiB end of the spread; dropping it from the operator set is the 404 MiB
# end. Everything else in the list is left exactly as ORT ships it.
DEFAULT_DYNAMIC_OPS = ["Attention", "Conv", "EmbedLayerNormalization", "Gather", "LSTM", "MatMul", "Transpose"]
OPS_WITHOUT_GATHER = [op for op in DEFAULT_DYNAMIC_OPS if op not in ("Gather", "EmbedLayerNormalization")]

class Variant:
    """One precision at one graph layout, and the source it is quantised from."""

    def __init__(self, merged: bool, operators: list[str] | None, what: str, quantise_from: str | None = None):
        self.merged = merged
        self.operators = operators          # None means "do not quantise"
        self.what = what
        self.quantise_from = quantise_from  # which fp32 variant is the quantiser's input


VARIANTS = {
    # Split layout: three graphs. The decoder appears twice on disk — once without past and once
    # with — because they are separate graphs over almost the same weights.
    "fp32": Variant(False, None, "split layout, no quantisation"),
    "int8": Variant(False, DEFAULT_DYNAMIC_OPS,
                    "split layout, dynamic int8, ORT's default operator set — Gather included", "fp32"),
    "int8-fp32-embeddings": Variant(False, OPS_WITHOUT_GATHER,
                                    "split layout, dynamic int8 with Gather dropped — embeddings stay fp32", "fp32"),
    # Merged layout: two graphs. optimum folds decoder and decoder-with-past into one graph behind a
    # `use_cache_branch` input, so the decoder's weights are stored once instead of twice.
    "fp32-merged": Variant(True, None, "merged layout, no quantisation"),
    "int8-merged": Variant(True, DEFAULT_DYNAMIC_OPS,
                           "merged layout, dynamic int8, ORT's default operator set — Gather included",
                           "fp32-merged"),
    "int8-merged-fp32-embeddings": Variant(True, OPS_WITHOUT_GATHER,
                                           "merged layout, dynamic int8 with Gather dropped — embeddings stay fp32",
                                           "fp32-merged"),
}

# Fixed sentences for the smoke test and the tokenizer fixture. The first four are real ASR output
# from this project's own pipeline (a CC0 narration of the Spanish Wikipedia article on Caracas and
# a CC BY-SA 4.0 narration of the German article on Ralf Dahrendorf, transcribed 2026-08-19); the
# rest exercise what the spike found: English passthrough, the German-numbers-as-words interaction,
# and a segment short enough that beam search has somewhere to go.
SMOKE_SENTENCES = [
    ("es", "Caracas es la capital y la ciudad más poblada de Venezuela."),
    ("es", "Desde el siglo XIX es considerada el centro del poder político y económico de Venezuela."),
    ("es", "Se encuentra ubicada en la zona centro."),
    ("de", "Ralf Dahrendorf wurde neunzehnhundertneunundzwanzig in Hamburg geboren."),
    ("de", "Die Funktion sozialer Konflikte ist das Thema seines Buches."),
    ("en", "This sentence is already in English and should pass through unchanged."),
]


# --------------------------------------------------------------------------------------------- #
# The 3.14 shim
# --------------------------------------------------------------------------------------------- #

def partial_is_a_descriptor() -> bool:
    """True on CPython 3.14+, where a class-attribute partial binds the instance when read."""
    return hasattr(functools.partial, "__get__")


def unbind_partial_class_attributes(package: str = "optimum") -> list[str]:
    """Re-wrap every `functools.partial` class attribute under `package` in `staticmethod`.

    A partial stored as a class attribute in a library written before CPython 3.14 was never meant
    to bind anything; 3.14 made it do so. `staticmethod.__get__` returns the wrapped object, and a
    `staticmethod` is itself callable since 3.10, so both `Cls.ATTR` and `self.ATTR` keep working.
    Returns the qualified names patched, so a run can say what it touched rather than assert it.
    """
    patched: set[str] = set()
    for module_name, module in list(sys.modules.items()):
        if not (module_name == package or module_name.startswith(package + ".")):
            continue
        for obj_name in dir(module):
            obj = getattr(module, obj_name, None)
            if not isinstance(obj, type):
                continue
            for attr_name, attr in list(vars(obj).items()):
                if isinstance(attr, functools.partial):
                    setattr(obj, attr_name, staticmethod(attr))
                    patched.add(f"{obj.__module__}.{obj.__qualname__}.{attr_name}")
    return sorted(patched)


# --------------------------------------------------------------------------------------------- #
# Provenance
# --------------------------------------------------------------------------------------------- #

def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def describe_directory(directory: Path) -> dict:
    """Every file in the directory with its exact byte count and SHA-256, sorted by name."""
    files = []
    for path in sorted(directory.rglob("*")):
        if path.is_file() and path.name != "manifest.json":
            files.append({
                "fileName": str(path.relative_to(directory)).replace("\\", "/"),
                "sizeBytes": path.stat().st_size,
                "sha256": sha256_of(path),
            })
    return {
        "files": files,
        "fileCount": len(files),
        "totalBytes": sum(entry["sizeBytes"] for entry in files),
    }


def toolchain() -> dict:
    import importlib.metadata as metadata

    versions = {}
    for package in ("torch", "transformers", "optimum", "optimum-onnx", "onnx", "onnxruntime",
                    "sentencepiece", "numpy", "protobuf"):
        try:
            versions[package] = metadata.version(package)
        except metadata.PackageNotFoundError:
            versions[package] = None
    return {
        "python": sys.version.split()[0],
        "platform": platform.platform(),
        "packages": versions,
    }


def checkpoint_revision() -> str | None:
    """The resolved commit of the checkpoint in the HF cache, so the export names its input."""
    try:
        from huggingface_hub.constants import HF_HUB_CACHE
    except ImportError:
        return None
    ref = Path(HF_HUB_CACHE) / f"models--{MODEL.replace('/', '--')}" / "refs" / "main"
    return ref.read_text(encoding="utf-8").strip() if ref.exists() else None


# --------------------------------------------------------------------------------------------- #
# What the weights actually came out as
# --------------------------------------------------------------------------------------------- #

def initialiser_report(onnx_path: Path, vocab_size: int, d_model: int) -> dict:
    """Group a graph's initialisers by element type, and pick out every vocab-sized one.

    This is what settles the 227-or-404 question, and it needs more than one bit per graph. The
    checkpoint ties its embedding matrix to its output projection, but the ONNX export does not:
    each decoder graph carries the matrix **twice**, once as a `[vocab, d_model]` table read by
    `Gather` and once as a `[d_model, vocab]` weight read by `MatMul`. They quantise under different
    rules — dropping `Gather` from the operator set leaves the table in fp32 while the MatMul weight
    still goes to int8 — so a single "did the embeddings quantise" boolean would be false either
    way it was answered. Both are reported, identified by shape rather than by name, because the
    name is optimum's business and the shape is the model's.

    In the merged layout the nodes live inside `If` subgraphs, so the top-level consumer scan sees
    no reader for these initialisers; that is a property of this scan, not dead weight in the graph,
    and `readBy` says `subgraph` rather than pretending to know.
    """
    import onnx

    model = onnx.load(str(onnx_path), load_external_data=False)

    consumers: dict[str, set[str]] = {}
    for node in model.graph.node:
        for name in node.input:
            consumers.setdefault(name, set()).add(node.op_type)

    by_type: dict[str, dict] = {}
    vocab_sized = []
    for initialiser in model.graph.initializer:
        type_name = onnx.TensorProto.DataType.Name(initialiser.data_type)
        dims = list(initialiser.dims)
        elements = 1
        for dim in dims:
            elements *= dim
        entry = by_type.setdefault(type_name, {"count": 0, "elements": 0})
        entry["count"] += 1
        entry["elements"] += elements
        if sorted(dims) == sorted([vocab_size, d_model]):
            read_by = sorted(consumers.get(initialiser.name, set())) or ["subgraph"]
            vocab_sized.append({
                "name": initialiser.name,
                "elementType": type_name,
                "dims": dims,
                "elements": elements,
                "readBy": read_by,
                "quantised": type_name in ("INT8", "UINT8"),
            })

    return {
        "byElementType": by_type,
        "vocabSizedInitialisers": vocab_sized,
        "vocabSizedCount": len(vocab_sized),
        "vocabSizedAllQuantised": (all(entry["quantised"] for entry in vocab_sized)
                                   if vocab_sized else None),
        "vocabSizedAnyFloat": (any(not entry["quantised"] for entry in vocab_sized)
                               if vocab_sized else None),
    }


# --------------------------------------------------------------------------------------------- #
# Export
# --------------------------------------------------------------------------------------------- #

def export_fp32(destination: Path, merged: bool) -> float:
    """Export the graphs and save the tokenizer beside them.

    `use_cache=True` is what puts past-key-values on the decoder's interface. An export without it
    would load and translate and quietly foreclose beam search, which is the failure this step is
    meant not to hand forward. `use_merged` decides whether the with-past and without-past decoders
    are one graph behind a `use_cache_branch` input or two graphs over nearly the same weights.
    """
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer

    started = time.time()
    model = ORTModelForSeq2SeqLM.from_pretrained(MODEL, export=True, use_cache=True, use_merged=merged)
    destination.mkdir(parents=True, exist_ok=True)
    model.save_pretrained(destination)
    AutoTokenizer.from_pretrained(MODEL).save_pretrained(destination)
    return time.time() - started


def quantise(source: Path, destination: Path, operators: list[str]) -> float:
    """Dynamic int8 over every .onnx graph in `source`, copying the non-graph files across."""
    from optimum.onnxruntime import ORTQuantizer
    from optimum.onnxruntime.configuration import AutoQuantizationConfig

    config = AutoQuantizationConfig.avx512_vnni(
        is_static=False, per_channel=False, operators_to_quantize=list(operators)
    )
    destination.mkdir(parents=True, exist_ok=True)
    started = time.time()
    for graph in sorted(source.glob("*.onnx")):
        quantizer = ORTQuantizer.from_pretrained(source, file_name=graph.name)
        quantizer.quantize(save_dir=destination, quantization_config=config, file_suffix=None)
    elapsed = time.time() - started

    for extra in sorted(source.iterdir()):
        if extra.is_file() and extra.suffix != ".onnx" and not extra.name.endswith(".onnx_data"):
            shutil.copy2(extra, destination / extra.name)
    return elapsed


# --------------------------------------------------------------------------------------------- #
# Smoke: does it still translate?
# --------------------------------------------------------------------------------------------- #

def translate_with_pytorch(sentences: list[str], num_beams: int) -> tuple[list[str], float]:
    from transformers import AutoTokenizer, MarianMTModel

    tokenizer = AutoTokenizer.from_pretrained(MODEL)
    model = MarianMTModel.from_pretrained(MODEL)
    model.eval()
    return _generate(model, tokenizer, sentences, num_beams)


def translate_with_onnx(directory: Path, sentences: list[str], num_beams: int) -> tuple[list[str], float]:
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(directory)
    model = ORTModelForSeq2SeqLM.from_pretrained(directory, use_cache=True)
    return _generate(model, tokenizer, sentences, num_beams)


def _generate(model, tokenizer, sentences: list[str], num_beams: int) -> tuple[list[str], float]:
    import torch

    marked = [f"{TARGET_TOKEN} {sentence}" for sentence in sentences]
    batch = tokenizer(marked, return_tensors="pt", padding=True)
    started = time.time()
    with torch.no_grad():
        generated = model.generate(**batch, num_beams=num_beams, max_new_tokens=256)
    elapsed = time.time() - started
    return tokenizer.batch_decode(generated, skip_special_tokens=True), elapsed


def degenerate_repetition(text: str, unit: int = 2, runs: int = 4) -> str | None:
    """Return the repeated unit if `text` contains a short chunk repeated back to back, else None.

    A quantised seq2seq that has lost precision does not usually go quiet; it loops, and the loop is
    the one failure a per-segment exact-match count cannot tell apart from a paraphrase. Four or
    more consecutive repeats of a two-to-four character chunk is well outside anything English does
    on its own, so this flags "Genocococococ" and leaves "that that" alone. It is a detector for a
    specific collapse, not a quality metric — nothing here scores a translation.
    """
    for size in range(unit, 5):
        for start in range(len(text) - size * runs + 1):
            chunk = text[start:start + size]
            if len(set(chunk)) == 1 and chunk[0] in " .-\n":
                continue
            if text[start:start + size * runs] == chunk * runs:
                return chunk
    return None


def smoke(directory: Path, num_beams: int, reference_json: Path | None) -> dict:
    """Translate the same sentences twice — PyTorch fp32 and these graphs — and diff, verbatim.

    An export that loads and produces fluent nonsense is the failure mode that matters here; the
    analogous ONNX int8 export of the ASR model collapsed silently, which is why this repository has
    a WER harness at all. So the diff is reported string by string and never characterised.
    """
    sentences = [text for _, text in SMOKE_SENTENCES]
    pytorch_out, pytorch_seconds = translate_with_pytorch(sentences, num_beams)
    onnx_out, onnx_seconds = translate_with_onnx(directory, sentences, num_beams)

    rows = []
    for (language, source), reference, hypothesis in zip(SMOKE_SENTENCES, pytorch_out, onnx_out):
        rows.append({
            "language": language,
            "source": source,
            "pytorchFp32": reference,
            "onnx": hypothesis,
            "identical": reference == hypothesis,
            "degenerate": degenerate_repetition(hypothesis),
        })

    result = {
        "variant": directory.name,
        "numBeams": num_beams,
        "targetToken": TARGET_TOKEN,
        "identical": sum(1 for row in rows if row["identical"]),
        "degenerate": sum(1 for row in rows if row["degenerate"]),
        "total": len(rows),
        "pytorchSeconds": round(pytorch_seconds, 2),
        "onnxSeconds": round(onnx_seconds, 2),
        "rows": rows,
    }

    if reference_json is not None and reference_json.exists():
        recorded = json.loads(reference_json.read_text(encoding="utf-8"))
        result["againstRecordedRun"] = diff_against_recorded(directory, recorded, num_beams)
    return result


def diff_against_recorded(directory: Path, recorded: dict, num_beams: int) -> dict:
    """Diff these graphs against a decode run recorded earlier, on that run's own sentences.

    Shape expected is the 2026-08-19 spike's decode-comparison.json: {"files": {lang: {"pairs":
    [{"source", "greedy", "beam6"}]}}}. Every pair is re-translated through ONNX and compared to the
    `beam6` string the spike's fp32 PyTorch run produced.
    """
    per_language = {}
    for language, block in recorded.get("files", {}).items():
        pairs = block.get("pairs", [])
        if not pairs:
            continue
        sources = [pair["source"] for pair in pairs]
        hypotheses, seconds = translate_with_onnx(directory, sources, num_beams)
        rows = []
        for pair, hypothesis in zip(pairs, hypotheses):
            reference = pair.get("beam6", "")
            rows.append({
                "source": pair["source"],
                "recordedBeam6": reference,
                "onnx": hypothesis,
                "identical": reference == hypothesis,
                "degenerate": degenerate_repetition(hypothesis),
                "referenceDegenerate": degenerate_repetition(reference),
            })
        per_language[language] = {
            "segments": len(rows),
            "identical": sum(1 for row in rows if row["identical"]),
            "degenerate": sum(1 for row in rows if row["degenerate"]),
            "referenceDegenerate": sum(1 for row in rows if row["referenceDegenerate"]),
            "onnxSeconds": round(seconds, 2),
            "rows": rows,
        }
    return per_language


# --------------------------------------------------------------------------------------------- #
# The tokenizer fixture
# --------------------------------------------------------------------------------------------- #

def write_tokenizer_fixture(path: Path) -> dict:
    """Token ids from HuggingFace's MarianTokenizer, for a C# SentencePieceTokenizer to be held to.

    Whether a C# tokenizer reproduces this is unestablished (`docs/UNPROVEN.md` § *Translating into
    English*), and it cannot be established against a description. The diariser's featurizer is held
    to committed fixtures for the same reason. Ids are recorded with the target token already on,
    because that is the only string the product ever tokenises.
    """
    from transformers import AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(MODEL)
    cases = []
    for language, sentence in SMOKE_SENTENCES:
        marked = f"{TARGET_TOKEN} {sentence}"
        encoded = tokenizer(marked)
        cases.append({
            "language": language,
            "source": sentence,
            "markedSource": marked,
            "inputIds": encoded["input_ids"],
            "tokens": tokenizer.convert_ids_to_tokens(encoded["input_ids"]),
            "decoded": tokenizer.decode(encoded["input_ids"], skip_special_tokens=True),
        })

    fixture = {
        "model": MODEL,
        "revision": checkpoint_revision(),
        "tokenizerClass": type(tokenizer).__name__,
        "targetToken": TARGET_TOKEN,
        "vocabSize": tokenizer.vocab_size,
        "modelMaxLength": tokenizer.model_max_length,
        "eosTokenId": tokenizer.eos_token_id,
        "padTokenId": tokenizer.pad_token_id,
        "unkTokenId": tokenizer.unk_token_id,
        "producedBy": toolchain(),
        "cases": cases,
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(fixture, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return fixture


# --------------------------------------------------------------------------------------------- #

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    default_out = Path(__file__).resolve().parent.parent / "runs" / "translation-onnx"
    parser.add_argument("--out", type=Path, default=default_out,
                        help="where the artefacts go (default: runs/translation-onnx, gitignored)")
    parser.add_argument("--variants", default=",".join(VARIANTS),
                        help="comma-separated: " + ", ".join(VARIANTS))
    parser.add_argument("--list", action="store_true", help="print the variants and exit")
    parser.add_argument("--skip-export", action="store_true",
                        help="manifest, smoke and fixture against artefacts already on disk")
    parser.add_argument("--smoke", action="store_true", help="translate and diff against fp32 PyTorch")
    parser.add_argument("--smoke-variants", default=None,
                        help="which variants to smoke (default: all that were built)")
    parser.add_argument("--num-beams", type=int, default=6,
                        help="beam-6 is what the 2026-08-19 spike measured; greedy drops content")
    parser.add_argument("--reference-json", type=Path, default=None,
                        help="a recorded decode run to diff against as well")
    parser.add_argument("--tokenizer-fixture", type=Path, default=None,
                        help="write a committed token-id fixture to this path")
    args = parser.parse_args()

    if args.list:
        for name, variant in VARIANTS.items():
            print(f"  {name:30} {variant.what}")
        return 0

    wanted = [name.strip() for name in args.variants.split(",") if name.strip()]
    unknown = [name for name in wanted if name not in VARIANTS]
    if unknown:
        parser.error(f"unknown variant(s): {', '.join(unknown)}; known: {', '.join(VARIANTS)}")

    print(f"python       {sys.version.split()[0]}")
    print(f"checkpoint   {MODEL}")
    print(f"revision     {checkpoint_revision()}")
    print(f"out          {args.out}")

    patched: list[str] = []
    if partial_is_a_descriptor():
        import optimum.exporters.onnx.model_configs  # noqa: F401  populate the class registry
        import optimum.onnxruntime  # noqa: F401
        patched = unbind_partial_class_attributes()
        print(f"3.14 shim    unbound {len(patched)} partial class attributes under optimum")
    else:
        print("3.14 shim    not needed on this interpreter")

    from transformers import AutoConfig
    config = AutoConfig.from_pretrained(MODEL)

    manifest = {
        "model": MODEL,
        "revision": checkpoint_revision(),
        "producedBy": toolchain(),
        "shim": {
            "needed": partial_is_a_descriptor(),
            "reason": "CPython 3.14 gave functools.partial the descriptor protocol; optimum stores "
                      "NORMALIZED_CONFIG_CLASS as a class-attribute partial and reads it through self",
            "patchedAttributes": patched,
        },
        "config": {
            "vocabSize": config.vocab_size,
            "dModel": config.d_model,
            "encoderLayers": config.encoder_layers,
            "decoderLayers": config.decoder_layers,
            "maxPositionEmbeddings": config.max_position_embeddings,
            "shareEncoderDecoderEmbeddings": config.share_encoder_decoder_embeddings,
        },
        "variants": {},
    }

    for name in wanted:
        variant = VARIANTS[name]
        directory = args.out / name
        print(f"\n=== {name} — {variant.what} ===", flush=True)

        if not args.skip_export:
            if variant.operators is None:
                print(f"exporting, merged={variant.merged} ...", flush=True)
                seconds = export_fp32(directory, variant.merged)
            else:
                source = args.out / variant.quantise_from
                if not source.exists():
                    print(f"{variant.quantise_from} not on disk; exporting it as the quantiser's input",
                          flush=True)
                    export_fp32(source, VARIANTS[variant.quantise_from].merged)
                print(f"quantising {variant.quantise_from}, operators = "
                      f"{', '.join(variant.operators)} ...", flush=True)
                seconds = quantise(source, directory, variant.operators)
            print(f"took {seconds:.0f}s", flush=True)
        else:
            seconds = None

        described = describe_directory(directory)
        graphs = {}
        for entry in described["files"]:
            if entry["fileName"].endswith(".onnx"):
                graphs[entry["fileName"]] = initialiser_report(
                    directory / entry["fileName"], config.vocab_size, config.d_model)

        manifest["variants"][name] = {
            "what": variant.what,
            "layout": "merged" if variant.merged else "split",
            "quantisedFrom": variant.quantise_from,
            "operatorsToQuantize": variant.operators,
            "secondsToProduce": round(seconds, 1) if seconds is not None else None,
            **described,
            "graphs": graphs,
        }

        for entry in described["files"]:
            print(f"  {entry['fileName']:38} {entry['sizeBytes']:>13,} B  {entry['sha256'][:16]}...")
        print(f"  {'TOTAL':38} {described['totalBytes']:>13,} B "
              f"= {described['totalBytes'] / 1048576:.1f} MiB over {described['fileCount']} files")
        for graph_name, graph in graphs.items():
            print(f"  {graph_name}: "
                  + ", ".join(f"{t}={v['elements']:,}" for t, v in sorted(graph["byElementType"].items())))
            for vocab_sized in graph["vocabSizedInitialisers"]:
                print(f"    vocab-sized {vocab_sized['dims']} read by {'/'.join(vocab_sized['readBy'])}"
                      f" -> {vocab_sized['elementType']}")

    if args.smoke:
        smoke_names = ([name.strip() for name in args.smoke_variants.split(",")]
                       if args.smoke_variants else wanted)
        manifest["smoke"] = {}
        for name in smoke_names:
            print(f"\n=== smoke: {name}, beam-{args.num_beams} against fp32 PyTorch ===", flush=True)
            result = smoke(args.out / name, args.num_beams, args.reference_json)
            manifest["smoke"][name] = result
            for row in result["rows"]:
                mark = "same" if row["identical"] else "DIFF"
                print(f"  [{mark}] {row['language']}  {row['source'][:60]}")
                print(f"         pytorch: {row['pytorchFp32']}")
                print(f"         onnx   : {row['onnx']}")
            print(f"  identical {result['identical']}/{result['total']}, "
                  f"degenerate {result['degenerate']}; "
                  f"pytorch {result['pytorchSeconds']}s, onnx {result['onnxSeconds']}s")
            for language, block in result.get("againstRecordedRun", {}).items():
                print(f"  recorded {language}: identical {block['identical']}/{block['segments']}, "
                      f"degenerate {block['degenerate']} (reference {block['referenceDegenerate']})")

    if args.tokenizer_fixture is not None:
        fixture = write_tokenizer_fixture(args.tokenizer_fixture)
        print(f"\nwrote {args.tokenizer_fixture} — {len(fixture['cases'])} cases, "
              f"vocab {fixture['vocabSize']}, model_max_length {fixture['modelMaxLength']}")

    args.out.mkdir(parents=True, exist_ok=True)
    manifest_path = args.out / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"\nwrote {manifest_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
