#!/usr/bin/env python3
"""Score the translation model into English against the gate, on FLEURS, per language.

The gate was ratified on 2026-08-19 before any score existed (`docs/PHASES.md` § *Decided
2026-08-19*), and it has two criteria that must both hold. This harness is the first of them:

  **chrF++ into English clears the per-language source-copy floor by a margin fixed per language.**

The floor is what a hypothesis earns by echoing its own source untranslated — scoring the source
text itself against the English reference. It exists because no published chrF++ or BLEU for any
candidate on FLEURS X→en at a stated signature was found, so unlike the DER gate this one cannot be
pinned to somebody else's number and is anchored from inside its own measurement. It is per language
because the floor is a property of the language pair: Dutch shares far more character n-grams with
English than Greek does, and one number across 25 languages would be a different bar in each.

The second criterion — a human adequacy check on the Spanish → English driving case — is a person's
job. `--adequacy-sheet` writes the sheet to rate.

## What this measures, and four things it does not

It measures **the translation model alone**. FLEURS source transcripts go in and English comes out;
no audio is involved and no ASR runs. That is deliberate — the gate is about translation — but it
means this is **not the cascade**, and the cascade is what a user gets.

  * **Any FLEURS figure is a lower bound on the cascade penalty.** It is read speech of
    Wikipedia-derived sentences, so the ASR half of the real pipeline would be correspondingly easy,
    and here it is absent entirely.
  * **`es_419` is FLEURS' only Spanish config**, so the driving case is measured on one variety.
  * **The n-way alignment across configs is asserted by FLEURS' card.** This harness checks it as far
    as ids allow — that the sentence ids of each config overlap English, and by how much — and
    refuses to score a language whose overlap is too small to mean anything. What ids cannot prove is
    that the same id is the same sentence in both files; that is taken on the card's word and said so.
  * **English is not scored.** It is the target, the reference side, and a passthrough the spike
    measured as byte-identical.

## Corpus pinning

FLEURS is fetched as its per-language `test.tsv` and nothing else — the transcripts are all this
needs, and the audio is a terabyte nobody here has a use for. Every file's SHA-256 goes in the
result, so a later run against a moved corpus is visible rather than silent.

## Usage, and what a full run costs

    python scripts/measure-translation.py --variant runs/translation-onnx/int8-merged
    python scripts/measure-translation.py --floor-only          # no model needed, minutes not hours
    python scripts/measure-translation.py --adequacy-sheet      # the Spanish sheet for a human
    python scripts/measure-translation.py --languages es,de,nl  # chunk it across sessions

**A full run is hours, and it was measured before it was started.** 2.16 s per sentence on the
laptop's CPU at int8 and beam-6, over FLEURS' ~340-sentence test split in 24 languages, is about
five hours per precision. `--floor-only` computes every floor in minutes and needs no model at all,
which is worth doing first: the floors are half the gate and they do not depend on which artefact
ships. `--languages` splits the rest across sessions.

Needs torch, transformers, optimum, onnxruntime, sentencepiece, sacrebleu and huggingface_hub. Use
the venv `scripts/export-translation-onnx.py` describes, plus `pip install sacrebleu`.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import platform
import random
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

# The 25 languages parakeet-tdt-0.6b-v3 claims, mapped to FLEURS configs. English is the target and
# the reference side rather than a row to score. es_419 is the only Spanish FLEURS publishes.
FLEURS_CONFIGS = {
    "bg": "bg_bg", "cs": "cs_cz", "da": "da_dk", "de": "de_de", "el": "el_gr",
    "en": "en_us", "es": "es_419", "et": "et_ee", "fi": "fi_fi", "fr": "fr_fr",
    "hr": "hr_hr", "hu": "hu_hu", "it": "it_it", "lt": "lt_lt", "lv": "lv_lv",
    "mt": "mt_mt", "nl": "nl_nl", "pl": "pl_pl", "pt": "pt_br", "ro": "ro_ro",
    "ru": "ru_ru", "sk": "sk_sk", "sl": "sl_si", "sv": "sv_se", "uk": "uk_ua",
}
TARGET = "en"

MODEL = "Helsinki-NLP/opus-mt-tc-bible-big-mul-deu_eng_nld"
TARGET_TOKEN = ">>eng<<"

# Beam-6, not greedy. Over 44 real segments the 2026-08-19 spike measured greedy dropping content
# beam-6 kept, at 2.1x to 2.3x the time. Scoring a decode nobody would ship is scoring nothing.
DEFAULT_BEAMS = 6

# The tokenizer declares 512 even though config.json says max_position_embeddings 1024. 512 is the
# number to design against; a source past it is skipped and counted rather than truncated, because
# a truncated source scores as a bad translation and is really a harness defect.
MAX_SOURCE_TOKENS = 512

# Below this many sentences shared with English, a language is not scored at all. A chrF++ over
# thirty sentences is a number with an error bar nobody states, which is worse than no number.
MIN_SHARED_SENTENCES = 100


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def fetch_split(config: str, split: str) -> tuple[Path, str]:
    """The one TSV this needs, and its digest. Audio is never fetched."""
    from huggingface_hub import hf_hub_download

    path = Path(hf_hub_download("google/fleurs", f"data/{config}/{split}.tsv", repo_type="dataset"))
    return path, sha256_of(path)


def read_sentences(path: Path) -> dict[str, str]:
    """Sentence id -> punctuated transcript.

    FLEURS carries several rows per sentence, one per speaker, with identical text; the first is
    kept and the rest are identical by construction. `raw_transcription` is the punctuated form
    rather than the lowercased, punctuation-stripped `transcription`, because a translation model
    reads sentences and chrF++ is case- and punctuation-sensitive at its default signature.
    """
    sentences: dict[str, str] = {}
    with path.open(encoding="utf-8", newline="") as handle:
        for row in csv.reader(handle, delimiter="\t", quoting=csv.QUOTE_NONE):
            if len(row) < 3:
                continue
            sentence_id, raw = row[0], row[2].strip()
            if raw and sentence_id not in sentences:
                sentences[sentence_id] = raw
    return sentences


def choose_ids(shared: list[str], count: int, seed: int) -> list[str]:
    """A deterministic sample of the shared ids.

    Seeded shuffle rather than the first N by id: FLEURS ids index into the underlying FLoRes
    sentence list, which is ordered by source document, so the first N would be a sample of a
    handful of Wikipedia articles rather than of the corpus.
    """
    ordered = sorted(shared, key=lambda value: (len(value), value))
    if count <= 0 or count >= len(ordered):
        return ordered
    return sorted(random.Random(seed).sample(ordered, count), key=lambda value: (len(value), value))


def translate(model, tokenizer, sentences: list[str], beams: int, batch_size: int) -> tuple[list[str], float]:
    """Translate every sentence, in length-sorted batches, and return them in the original order.

    Sorting by length before batching is not a micro-optimisation here. A padded batch at beam-6
    runs every member until the longest one finishes, so a batch holding a six-word sentence beside
    a sixty-word one pays sixty words for both; mixing lengths at random made one FLEURS language
    take longer than ten minutes. Sorting makes each batch nearly uniform, and the outputs are put
    back in order afterwards so nothing downstream can tell.

    It does not change what comes out. Beam search is deterministic given a sequence and its
    attention mask, and the mask is what makes padding inert — which the export's own smoke run
    demonstrated from the other side, reproducing a one-at-a-time reference run exactly from
    batches of six.
    """
    import torch

    order = sorted(range(len(sentences)), key=lambda i: len(sentences[i]))
    outputs: list[str | None] = [None] * len(sentences)

    started = time.perf_counter()
    for start in range(0, len(order), batch_size):
        indices = order[start:start + batch_size]
        chunk = [f"{TARGET_TOKEN} {sentences[i]}" for i in indices]
        batch = tokenizer(chunk, return_tensors="pt", padding=True)
        with torch.no_grad():
            generated = model.generate(**batch, num_beams=beams, max_new_tokens=512)
        for index, text in zip(indices, tokenizer.batch_decode(generated, skip_special_tokens=True)):
            outputs[index] = text
        done = min(start + batch_size, len(order))
        rate = (time.perf_counter() - started) / done
        print(f"      {done}/{len(order)}  {rate:.2f} s/sentence", end="\r", flush=True)

    return [text or "" for text in outputs], time.perf_counter() - started


def score(hypotheses: list[str], references: list[str]) -> tuple[float, str]:
    """chrF++ and the signature it was computed under, which travels with every number."""
    import sacrebleu

    metric = sacrebleu.CHRF(word_order=2)
    result = metric.corpus_score(hypotheses, [references])
    return round(result.score, 2), str(metric.get_signature())


def toolchain() -> dict:
    import importlib.metadata as metadata

    versions = {}
    for package in ("torch", "transformers", "optimum", "optimum-onnx", "onnxruntime",
                    "sentencepiece", "sacrebleu", "numpy"):
        try:
            versions[package] = metadata.version(package)
        except metadata.PackageNotFoundError:
            versions[package] = None
    return {"python": sys.version.split()[0], "platform": platform.platform(), "packages": versions}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    root = Path(__file__).resolve().parent.parent
    parser.add_argument("--variant", type=Path, default=root / "runs" / "translation-onnx" / "int8-merged",
                        help="the exported ONNX directory to score")
    parser.add_argument("--languages", default=None,
                        help="comma-separated source languages (default: all but English)")
    parser.add_argument("--sentences", type=int, default=0,
                        help="sentences per language; 0 (the default) for every shared sentence, "
                             "which is what FLEURS' ~350-sentence test split makes affordable")
    parser.add_argument("--split", default="test", choices=["test", "dev", "train"])
    parser.add_argument("--seed", type=int, default=20260820, help="sampling seed, recorded in the result")
    parser.add_argument("--num-beams", type=int, default=DEFAULT_BEAMS)
    # One, and measured rather than assumed. Batching is normally free speed and here it is the
    # opposite: on the laptop's CPU the same 32 Spanish sentences took 12.75 s each at batch 16 and
    # 2.16 s each at batch 1, a factor of six the wrong way. A padded beam-search batch decodes
    # until every member has finished, so one long sentence holds up fifteen short ones while
    # beam-6 keeps 96 sequences in flight. Raise it only after measuring on the machine in question.
    parser.add_argument("--batch-size", type=int, default=1)
    parser.add_argument("--floor-only", action="store_true",
                        help="compute the source-copy floors and stop; loads no model")
    parser.add_argument("--adequacy-sheet", action="store_true",
                        help="also write the Spanish sheet for the human adequacy check")
    parser.add_argument("--adequacy-rows", type=int, default=60)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()

    languages = ([code.strip() for code in args.languages.split(",") if code.strip()]
                 if args.languages else [code for code in FLEURS_CONFIGS if code != TARGET])
    unknown = [code for code in languages if code not in FLEURS_CONFIGS]
    if unknown:
        parser.error(f"unknown language(s): {', '.join(unknown)}; known: {', '.join(FLEURS_CONFIGS)}")

    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    out = args.out or (root / "runs" / "translation" / f"{stamp}-{args.variant.name}")
    out.mkdir(parents=True, exist_ok=True)

    print(f"variant    {args.variant}")
    print(f"languages  {len(languages)}  split {args.split}  sentences {args.sentences or 'all'}  seed {args.seed}")
    print(f"out        {out}\n")

    print(f"fetching FLEURS {TARGET} ({FLEURS_CONFIGS[TARGET]}) ...", flush=True)
    english_path, english_digest = fetch_split(FLEURS_CONFIGS[TARGET], args.split)
    english = read_sentences(english_path)
    print(f"  {len(english)} reference sentences  {english_digest[:16]}...")

    corpus = {FLEURS_CONFIGS[TARGET]: {"sha256": english_digest, "sentences": len(english)}}
    per_language: dict[str, dict] = {}

    model = tokenizer = None
    if not args.floor_only:
        print("\nloading the exported model ...", flush=True)
        from optimum.onnxruntime import ORTModelForSeq2SeqLM
        from transformers import AutoTokenizer

        tokenizer = AutoTokenizer.from_pretrained(args.variant)
        model = ORTModelForSeq2SeqLM.from_pretrained(args.variant, use_cache=True)
        print("  loaded")

    signature = None
    for code in languages:
        config = FLEURS_CONFIGS[code]
        print(f"\n{code} ({config})", flush=True)

        path, digest = fetch_split(config, args.split)
        sentences = read_sentences(path)
        corpus[config] = {"sha256": digest, "sentences": len(sentences)}

        # The alignment check. FLEURS' card asserts the configs are n-way parallel; this is how far
        # ids can carry that, and a language that does not clear the bar is refused rather than
        # scored on whatever happened to overlap.
        shared = sorted(set(sentences) & set(english))
        print(f"  {len(sentences)} sentences, {len(shared)} shared with English", flush=True)
        if len(shared) < MIN_SHARED_SENTENCES:
            print(f"  REFUSED: fewer than {MIN_SHARED_SENTENCES} shared sentences")
            per_language[code] = {
                "config": config,
                "refused": f"only {len(shared)} sentences shared with English",
                "sharedSentences": len(shared),
            }
            continue

        chosen = choose_ids(shared, args.sentences, args.seed)
        sources = [sentences[i] for i in chosen]
        references = [english[i] for i in chosen]

        skipped = 0
        if tokenizer is not None:
            keep = []
            for index, text in enumerate(sources):
                length = len(tokenizer(f"{TARGET_TOKEN} {text}")["input_ids"])
                if length <= MAX_SOURCE_TOKENS:
                    keep.append(index)
                else:
                    skipped += 1
            if skipped:
                print(f"  {skipped} source(s) over {MAX_SOURCE_TOKENS} tokens, skipped rather than truncated")
            chosen = [chosen[i] for i in keep]
            sources = [sources[i] for i in keep]
            references = [references[i] for i in keep]

        # The floor: the untranslated source scored against the English reference. Whatever the
        # model earns has to beat this, or it has not demonstrated that it translated anything.
        floor, signature = score(sources, references)
        entry = {
            "config": config,
            "sharedSentences": len(shared),
            "scoredSentences": len(sources),
            "skippedTooLong": skipped,
            "sourceCopyFloor": floor,
        }
        print(f"  source-copy floor  chrF++ {floor:6.2f}")

        if model is not None:
            hypotheses, seconds = translate(model, tokenizer, sources, args.num_beams, args.batch_size)
            hypothesis_score, signature = score(hypotheses, references)
            entry |= {
                "chrF2pp": hypothesis_score,
                "marginOverFloor": round(hypothesis_score - floor, 2),
                "seconds": round(seconds, 1),
                "secondsPerSentence": round(seconds / max(1, len(sources)), 3),
            }
            print(f"  translated         chrF++ {hypothesis_score:6.2f}"
                  f"   margin {hypothesis_score - floor:+6.2f}   {seconds:.0f}s")

            (out / "hypotheses").mkdir(exist_ok=True)
            with (out / "hypotheses" / f"{code}.jsonl").open("w", encoding="utf-8") as handle:
                for sentence_id, source, hypothesis, reference in zip(chosen, sources, hypotheses, references):
                    handle.write(json.dumps({
                        "id": sentence_id, "source": source,
                        "hypothesis": hypothesis, "reference": reference,
                    }, ensure_ascii=False) + "\n")

        per_language[code] = entry

    result = {
        "producedAt": datetime.now(timezone.utc).isoformat(),
        "model": MODEL,
        "variant": str(args.variant),
        "variantManifest": manifest_digests(args.variant),
        "corpus": {"dataset": "google/fleurs", "split": args.split, "files": corpus},
        "sampling": {"sentencesPerLanguage": args.sentences or "all", "seed": args.seed},
        "decode": {"numBeams": args.num_beams, "targetToken": TARGET_TOKEN, "batchSize": args.batch_size},
        "metric": {"name": "chrF2++", "signature": signature},
        "producedBy": toolchain(),
        "languages": per_language,
        "notMeasured": [
            "The cascade. No audio and no ASR: FLEURS source transcripts go in, so every figure "
            "here is a lower bound on what a user's transcript would score.",
            "Spanish is es_419, the only Spanish config FLEURS publishes.",
            "Whether the same sentence id is the same sentence across configs. The id overlap is "
            "checked and reported; semantic alignment is taken on the dataset card's word.",
            "The per-language margins the gate requires. This computes the floors; what margin "
            "clears them is the maintainer's to ratify.",
        ],
    }

    (out / "translation-quality.json").write_text(
        json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    write_summary(out / "summary.md", result)
    print(f"\nwrote {out / 'translation-quality.json'} and summary.md")

    if args.adequacy_sheet:
        written = write_adequacy_sheet(out / "adequacy-es-en.md", out, args.adequacy_rows, args.seed)
        print(f"wrote {out / 'adequacy-es-en.md'} — {written} rows to rate" if written
              else "no Spanish hypotheses to build an adequacy sheet from (run without --floor-only)")

    return 0


def manifest_digests(variant: Path) -> dict | None:
    """What the scored artefact was, read from the export's own manifest if it is beside it."""
    manifest = variant.parent / "manifest.json"
    if not manifest.exists():
        return None

    data = json.loads(manifest.read_text(encoding="utf-8"))
    entry = data.get("variants", {}).get(variant.name)
    if entry is None:
        return None

    return {
        "revision": data.get("revision"),
        "layout": entry.get("layout"),
        "totalBytes": entry.get("totalBytes"),
        "files": {f["fileName"]: f["sha256"] for f in entry.get("files", [])},
    }


def write_summary(path: Path, result: dict) -> None:
    scored = {code: entry for code, entry in result["languages"].items() if "chrF2pp" in entry}
    lines = [
        f"# Translation into English — {result['variant']}",
        "",
        f"chrF++ against the per-language source-copy floor. Metric signature `{result['metric']['signature']}`.",
        f"FLEURS `{result['corpus']['split']}`, {result['sampling']['sentencesPerLanguage']} sentences per "
        f"language, seed {result['sampling']['seed']}, beam-{result['decode']['numBeams']}.",
        "",
        "**This is the translation model alone — no audio, no ASR, so it is not the cascade.**",
        "",
        "| language | scored | source-copy floor | chrF++ | margin |",
        "|---|---:|---:|---:|---:|",
    ]

    for code, entry in sorted(result["languages"].items()):
        if "refused" in entry:
            lines.append(f"| {code} | — | — | — | refused: {entry['refused']} |")
            continue
        if "chrF2pp" not in entry:
            lines.append(f"| {code} | {entry['scoredSentences']} | {entry['sourceCopyFloor']:.2f} | — | — |")
            continue
        lines.append(
            f"| {code} | {entry['scoredSentences']} | {entry['sourceCopyFloor']:.2f} | "
            f"{entry['chrF2pp']:.2f} | {entry['marginOverFloor']:+.2f} |")

    if scored:
        margins = [entry["marginOverFloor"] for entry in scored.values()]
        worst = min(scored.items(), key=lambda item: item[1]["marginOverFloor"])
        lines += [
            "",
            f"{len(scored)} languages scored. Margin over floor: "
            f"worst {min(margins):+.2f} ({worst[0]}), median {sorted(margins)[len(margins) // 2]:+.2f}, "
            f"best {max(margins):+.2f}.",
            "",
            "**No margin has been ratified**, so nothing here passes or fails the gate yet. "
            "`docs/PHASES.md` carries the criterion; the number that clears it is the maintainer's.",
        ]

    lines += ["", "## Not measured", ""] + [f"- {note}" for note in result["notMeasured"]] + [""]
    path.write_text("\n".join(lines), encoding="utf-8")


def write_adequacy_sheet(path: Path, out: Path, rows: int, seed: int) -> int:
    """The Spanish sheet for criterion two, which is a person's judgement and not a metric.

    Source, hypothesis and reference side by side with somewhere to write. The reference is shown
    because withholding it makes the rater a translator; it is shown *last* because reading it first
    makes them a proofreader of the reference instead.
    """
    source_file = out / "hypotheses" / "es.jsonl"
    if not source_file.exists():
        return 0

    records = [json.loads(line) for line in source_file.read_text(encoding="utf-8").splitlines() if line]
    chosen = random.Random(seed).sample(records, min(rows, len(records)))

    lines = [
        "# Human adequacy check — Spanish to English",
        "",
        "Criterion two of the translation gate (`docs/PHASES.md` § *Decided 2026-08-19*), and the",
        "half no metric can answer. For each row, rate **adequacy** — does the English carry what the",
        "Spanish says — and tick **not English** for any output that is not English at all, which is",
        "the failure mode the 2026-08-19 spike found when the target token goes missing.",
        "",
        "Adequacy: 4 all of it, 3 most, 2 some, 1 little, 0 none.",
        "",
        "The reference is the last column on purpose. It is here so a rater is not doing the",
        "translation themselves, and it is last so they are not proofreading it instead.",
        "",
        "| # | adequacy | not English | Spanish source | model output | FLEURS English reference |",
        "|---|---|---|---|---|---|",
    ]

    def cell(text: str) -> str:
        return text.replace("|", "\\|").replace("\n", " ")

    for index, record in enumerate(chosen, start=1):
        lines.append(
            f"| {index} |  |  | {cell(record['source'])} | {cell(record['hypothesis'])} | "
            f"{cell(record['reference'])} |")

    lines += ["", f"Rated: __ / {len(chosen)}.  Mean adequacy: ____.  Not English: ____.", ""]
    path.write_text("\n".join(lines), encoding="utf-8")
    return len(chosen)


if __name__ == "__main__":
    sys.exit(main())
