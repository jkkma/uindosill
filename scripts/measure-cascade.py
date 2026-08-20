#!/usr/bin/env python3
"""What the speech recogniser's errors cost the translation, measured rather than anecdotal.

`scripts/measure-translation.py` scores the translation model alone: FLEURS transcripts go in,
English comes out, and its own docstring says the figure it produces is a **lower bound on the
cascade penalty**, because the ASR half of the real pipeline is absent entirely. This harness
supplies the other half. Nothing in this repository had measured it, and the only evidence that the
cascade fails differently from either component was one sentence:

    Ralf Dahrendorf wurde neunzehnhundertneunundzwanzig in Hamburg geboren.
    -> Ralf Dahrendorf was born in Hamburg in the nineteenth century.

1929, spelled the way a speaker says it, became a century. Neither model is wrong on its own
metric — the ASR wrote what was said, and the translator translated what it was given. **n = 1,
which is the problem this exists to fix.**

## Why FLEURS makes it nearly free

FLEURS is n-way parallel: the same sentence ids exist as Spanish audio, as Spanish text, and as
English reference text. So the same sentences can be scored twice, in the same units, in one run:

    text-in    es_419 transcript -> translator -> English -> chrF++ vs en_us
    cascade    es_419 audio -> Parakeet -> Spanish -> translator -> English -> chrF++ vs en_us

and the gap between the two **is** the cascade penalty. Both arms are computed here rather than one
of them being quoted from an earlier run: same sentence ids, same process, same machine, same beam
width, same chrF++ signature. A number lifted from another run would differ by whatever else
differed, and the whole point is that the difference is the ASR.

The text-in arm doubles the translation time and is worth every second of it. It also self-checks:
it should reproduce `docs/UNPROVEN.md`'s published Spanish 56.17 and German 63.64, because it is the
same corpus through the same code, and a disagreement means something moved that nobody meant to
move.

**The ASR's word error rate on the same sentences comes free**, and it is what makes the penalty
decomposable. A large gap with a small WER says the translator is brittle to text that is slightly
off; a large gap with a large WER says the recogniser is the problem. Without it the penalty is one
number that could mean either.

## What this measures, and what it does not

  * **It is still a lower bound**, for the reason the translation harness gives: FLEURS is read
    speech of Wikipedia-derived sentences, which is the easy end for a recogniser. Spontaneous
    speech would be worse at the ASR step and therefore worse again after it.
  * **`es_419` is FLEURS' only Spanish config**, so the driving case is one variety.
  * **The WER normaliser is this project's own and it is English-oriented** — see
    `TranscriptNormalizer`, whose number-word rule knows English cardinals and no others. On Spanish
    and German it lower-cases, strips punctuation and drops fillers, which is the bulk of what it
    does, and its number rule is inert. A figure from here is comparable to another figure from here
    and to nothing published.
  * **One recording per sentence.** FLEURS gives three speakers per sentence; the first row is used,
    which is the same row `read_sentences` keeps the text from, so audio and reference agree.
  * **The recogniser is not told the language.** `--language` is inert for this checkpoint
    (`docs/UNPROVEN.md` § *The language hint*), and the product does not detect it either. What goes
    through is what a user would get.

## Usage

    python scripts/measure-cascade.py                     # es and de, every shared sentence
    python scripts/measure-cascade.py --languages es
    python scripts/measure-cascade.py --sentences 40      # a shakedown

CPU only, deliberately: `--backend cpu` is passed to the recogniser so the figure describes a
machine anybody has, and so a session can run this without asking for the GPU.

Needs the venv `scripts/export-translation-onnx.py` describes, plus sacrebleu, and a built
`uindosill` (Release) with an ASR model installed.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import re
import shutil
import subprocess
import sys
import tarfile
import time
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# The audio halves of the two configs, pinned the way `measure-translation.py` pins the TSVs — by
# the digest Hugging Face publishes for the LFS object, which for an LFS object IS its SHA-256, so
# the check is against what the repository publishes rather than against what one download produced.
# The revision is pinned too: `main` is not a corpus.
FLEURS_REVISION = "70bb2e84b976b7e960aa89f1c648e09c59f894dd"
FLEURS_AUDIO_PINS = {
    "es_419": {
        "path": "data/es_419/audio/test.tar.gz",
        "sizeBytes": 582112372,
        "sha256": "981802f6c828fd214fcf8bfc1036d80c9184b6eeb5650b3f7882f8affec046c9",
    },
    "de_de": {
        "path": "data/de_de/audio/test.tar.gz",
        "sizeBytes": 568734559,
        "sha256": "e86b42dfcdef749926cd92135045f87c25966c09e50d01c401adb04ee7d8628f",
    },
}

# The two languages this project has ever put real audio through, which is why they are the two
# scored here: Spanish is the driving case and carries the text-in figure to subtract from, and
# German is where the observed failure lives. Extending the map is not the hard part; having audio
# whose transcription anybody has looked at is.
CASCADE_LANGUAGES = ("es", "de")


def load_translation_harness():
    """The gate's own harness, loaded by path because its file name has a hyphen in it.

    Imported rather than reimplemented, and that is the load-bearing decision in this file. The
    text-in arm has to be the *same* translate-and-score as the run that produced the published
    figures, or the subtraction is between two things that differ by more than the ASR. So
    `translate`, `score`, `read_sentences`, `fetch_split` and the config map all come from there,
    and this file adds only the audio.
    """
    path = ROOT / "scripts" / "measure-translation.py"
    spec = importlib.util.spec_from_file_location("measure_translation", path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def fetch_audio(config: str) -> Path:
    """The config's test audio tarball, checked against the digest the repository publishes."""
    from huggingface_hub import hf_hub_download

    pin = FLEURS_AUDIO_PINS[config]
    path = Path(hf_hub_download(
        "google/fleurs", pin["path"], repo_type="dataset", revision=FLEURS_REVISION))

    size = path.stat().st_size
    if size != pin["sizeBytes"]:
        raise SystemExit(f"{config}: audio is {size} bytes, pinned at {pin['sizeBytes']}")

    digest = sha256_of(path)
    if digest != pin["sha256"]:
        raise SystemExit(
            f"{config}: audio SHA-256 {digest} does not match the pin {pin['sha256']}. "
            "Nothing should be measured against it.")
    return path


def read_rows(path: Path) -> dict[str, tuple[str, str]]:
    """Sentence id -> (audio file name, punctuated transcript), first row per id.

    The first row is the one `measure-translation.read_sentences` keeps the text from, so taking the
    audio from the same row is what makes the recognised text and the reference the same sentence
    rather than two recordings of it.
    """
    import csv

    rows: dict[str, tuple[str, str]] = {}
    with path.open(encoding="utf-8", newline="") as handle:
        for row in csv.reader(handle, delimiter="\t", quoting=csv.QUOTE_NONE):
            if len(row) < 3:
                continue
            sentence_id, filename, raw = row[0], row[1].strip(), row[2].strip()
            if raw and filename and sentence_id not in rows:
                rows[sentence_id] = (filename, raw)
    return rows


def extract(tarball: Path, wanted: dict[str, str], into: Path) -> dict[str, Path]:
    """Pull just the wanted members out, named by sentence id rather than by FLEURS' hash.

    Named by id because everything downstream — the ASR output file, the WER reference, the
    hypothesis row — is keyed by id, and a directory of 348 files called `10084850086754329587.wav`
    is a directory nobody can check by eye.
    """
    into.mkdir(parents=True, exist_ok=True)
    by_name = {filename: sentence_id for sentence_id, filename in wanted.items()}
    found: dict[str, Path] = {}

    with tarfile.open(tarball, "r:gz") as archive:
        for member in archive:
            if not member.isfile():
                continue
            base = Path(member.name).name
            sentence_id = by_name.get(base)
            if sentence_id is None:
                continue
            target = into / f"{sentence_id}.wav"
            if not target.exists() or target.stat().st_size != member.size:
                source = archive.extractfile(member)
                if source is None:
                    continue
                with target.open("wb") as handle:
                    shutil.copyfileobj(source, handle)
            found[sentence_id] = target

    return found


def uindosill(*args: str, cwd: Path | None = None) -> subprocess.CompletedProcess:
    """The built CLI, run from the repository. Release, because that is what the numbers describe."""
    binary = ROOT / "src" / "Parakeet.Cli" / "bin" / "Release" / "net10.0" / "uindosill.exe"
    if not binary.exists():
        raise SystemExit(
            f"{binary} is not there. Build first:  dotnet build Uindosill.slnx -c Release")
    return subprocess.run(
        [str(binary), *args], capture_output=True, text=True, encoding="utf-8",
        cwd=str(cwd) if cwd else None)


def transcribe(audio: dict[str, Path], into: Path, threads: int) -> tuple[dict[str, str], float]:
    """Recognise every clip through the product, on the CPU, and read the text back.

    One invocation over every file rather than one per file: the ASR weights are 1.34 GiB and
    loading them 348 times would measure the loader. `--backend cpu` is not a default here, it is
    the point — every figure in this file is meant to be reproducible without a GPU.
    """
    into.mkdir(parents=True, exist_ok=True)

    # Bare file names with the working directory set to where they live, not absolute paths.
    # Windows caps a command line at 32,767 characters, and 348 absolute paths under runs/cascade/
    # is about 31 KB of it — close enough that a longer timestamp or a deeper checkout would push
    # it over and the failure would look like a CLI bug rather than an argument-length one. Bare
    # names are a quarter of that, and one invocation is the point: the ASR weights are 1.34 GiB
    # and loading them once per batch would measure the loader.
    names = [audio[key].name for key in sorted(audio)]
    directory = next(iter(audio.values())).parent

    started = time.perf_counter()
    result = uindosill(
        "transcribe", "--backend", "cpu", "--threads", str(threads),
        "-f", "txt", "-o", str(into.resolve()), *names, cwd=directory)
    elapsed = time.perf_counter() - started

    if result.returncode != 0:
        sys.stderr.write(result.stdout)
        sys.stderr.write(result.stderr)
        raise SystemExit(f"transcribe exited {result.returncode}")

    recognised: dict[str, str] = {}
    for sentence_id in audio:
        written = into / f"{sentence_id}.txt"
        if not written.exists():
            recognised[sentence_id] = ""
            continue
        # The .txt carries [hh:mm:ss] prefixes per segment; the sentence is what is left.
        text = written.read_text(encoding="utf-8")
        text = re.sub(r"^\[\d{2}:\d{2}:\d{2}\]\s*", "", text, flags=re.MULTILINE)
        recognised[sentence_id] = " ".join(text.split())

    return recognised, elapsed


def word_error_rate(hypotheses: Path, references: dict[str, str], into: Path) -> dict:
    """WER through the product's own scorer, so the normalisation is the one it documents."""
    reference_dir = into / "wer-reference"
    reference_dir.mkdir(parents=True, exist_ok=True)
    for sentence_id, text in references.items():
        (reference_dir / f"{sentence_id}.txt").write_text(text, encoding="utf-8")

    files = sorted(path.name for path in hypotheses.glob("*.txt"))
    if not files:
        return {"error": "no hypotheses to score"}

    # Bare names again, for the argument-length reason in `transcribe` above.
    result = uindosill(
        "wer", "--reference-dir", str(reference_dir.resolve()), "--json", *files,
        cwd=hypotheses)
    if result.returncode != 0:
        return {"error": result.stderr.strip() or f"wer exited {result.returncode}"}

    try:
        parsed = json.loads(result.stdout)
    except json.JSONDecodeError:
        return {"error": "wer did not return JSON", "stdout": result.stdout[:2000]}

    # The per-hypothesis rows are 348 objects per language and say nothing sentences.jsonl does not.
    # What the result keeps is the summed counts, which is the figure anybody quotes, and the
    # normaliser's own name, because a WER without its normalisation named is not a number.
    return {
        "normaliser": parsed.get("normaliser"),
        "scored": len(parsed.get("hypotheses", [])),
        "summed": parsed.get("summed"),
    }


NEWLINE = chr(10)

NUMERAL = re.compile(r"\d+")


def numerals_of(text: str) -> list[str]:
    """Every run of digits, separators removed, so 1,000 and 1.000 both read as 1000."""
    return NUMERAL.findall(text.replace(",", "").replace(".", ""))


def numeral_recall(hypotheses: list[str], references: list[str]) -> tuple[int, int]:
    """How many of the reference's numbers appear as digits in the hypothesis.

    A crude measure and deliberately so: it does not care where in the sentence the number lands,
    only whether it survived at all. That is the thing chrF++ cannot report — the difference between
    *"in 1889"* and *"in the eighteenth century"* is a handful of character n-grams to it, and the
    whole point of the German rewrite is that those n-grams are the ones a listener checks.
    """
    hit = total = 0
    for hypothesis, reference in zip(hypotheses, references):
        present = set(numerals_of(hypothesis))
        for wanted in numerals_of(reference):
            total += 1
            hit += 1 if wanted in present else 0
    return hit, total


def compare_normaliser(run: Path, language: str) -> int:
    """Score a finished cascade run's own text through the shipping C# path and diff the two.

    The cascade arm above translates in Python, so it does **not** apply
    `GermanNumberWords` — that rewrite lives in `TranslationRequest.Mark`, on the C# side. Running
    the same recognised text through `uindosill translate` therefore produces the *shipping*
    output, and the difference between the two is what the rewrite buys. Both halves come from one
    finished run, so the sentences, the recogniser output and the references are identical and the
    only variable is the path.

    What the difference is **not** cleanly attributable to: the C# beam search is a port, and its
    agreement with Python was established on FLEURS transcripts rather than on recogniser output.
    So this reports how many of the differing lines carry a German compound number token, which is
    what says whether the rewrite explains them or something else does.
    """
    directory = run / language
    sentences = [json.loads(line) for line in (directory / "sentences.jsonl").read_text(encoding="utf-8").splitlines()]
    if not sentences:
        raise SystemExit(f"no sentences in {directory / 'sentences.jsonl'}")

    out = run / "normaliser"
    out.mkdir(parents=True, exist_ok=True)
    source = out / f"{language}-asr.txt"
    source.write_text(NEWLINE.join(s["recognised"] for s in sentences) + NEWLINE, encoding="utf-8")

    print(f"translating {len(sentences)} {language} lines through the C# shipping path ...", flush=True)
    result = uindosill("translate", "-o", str(out.resolve()), source.name, cwd=out)
    if result.returncode != 0:
        sys.stderr.write(result.stdout)
        sys.stderr.write(result.stderr)
        raise SystemExit(f"translate exited {result.returncode}")

    # The command's own numeral flag, which is the other thing worth recording: how often it fires
    # on real recogniser output, which nothing had measured.
    flag = [line for line in result.stderr.splitlines() if "a number the English does not" in line]

    shipping = (out / f"{language}-asr.en.txt").read_text(encoding="utf-8").splitlines()
    if len(shipping) != len(sentences):
        raise SystemExit(f"{len(shipping)} lines back for {len(sentences)} in")

    harness = load_translation_harness()
    references = [s["reference"] for s in sentences]
    python_arm = [s["cascade"] for s in sentences]

    without, signature = harness.score(python_arm, references)
    with_it, _ = harness.score(shipping, references)

    changed = [i for i in range(len(sentences)) if python_arm[i] != shipping[i]]
    compound = [i for i in changed if GERMAN_COMPOUND.search(sentences[i]["recognised"])]

    hit_without, total = numeral_recall(python_arm, references)
    hit_with, _ = numeral_recall(shipping, references)
    changed_without, changed_total = numeral_recall(
        [python_arm[i] for i in changed], [references[i] for i in changed])
    changed_with, _ = numeral_recall(
        [shipping[i] for i in changed], [references[i] for i in changed])

    report = {
        "language": language,
        "sentences": len(sentences),
        "chrF2ppWithoutNormaliser": without,
        "chrF2ppWithNormaliser": with_it,
        "chrF2ppDelta": round(with_it - without, 2),
        "linesChanged": len(changed),
        "linesChangedCarryingACompoundNumber": len(compound),
        "numeralRecallAll": {"without": [hit_without, total], "with": [hit_with, total]},
        "numeralRecallChangedLines": {
            "without": [changed_without, changed_total], "with": [changed_with, changed_total]},
        "numeralFlagLines": flag,
        "metric": signature,
    }
    (out / f"{language}-normaliser.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"  chrF++ {without} -> {with_it}  ({with_it - without:+.2f})")
    print(f"  {len(changed)} lines changed, {len(compound)} of them carrying a German compound number")
    print(f"  numeral recall, all {len(sentences)} sentences: "
          f"{hit_without}/{total} -> {hit_with}/{total}")
    print(f"  numeral recall, the {len(changed)} changed lines: "
          f"{changed_without}/{changed_total} -> {changed_with}/{changed_total}")
    for line in flag:
        print(f"  flag: {line}")
    print()
    print(f"wrote {out / (language + '-normaliser.json')}")
    return 0


# Not the parser — a screen, so "did a compound number appear in this line at all" can be asked of
# the recogniser's output without reimplementing GermanNumberWords in a second language. It over-
# matches on purpose (Jahrhundert has `hundert` in it and the real parser rejects it); what it is
# for is attributing a *difference*, not deciding a rewrite.
GERMAN_COMPOUND = re.compile(
    r"[A-Za-zÄÖÜäöüß]*"
    r"(?:und(?:zwanzig|dreißig|dreissig|vierzig|fünfzig|fuenfzig|sechzig|siebzig|achtzig|neunzig)"
    r"|hundert|tausend)"
    r"[A-Za-zÄÖÜäöüß]*",
    re.IGNORECASE)


def wer_rate(wer: dict) -> float | str:
    """The normalised summed rate, or `?` when the scorer could not produce one."""
    summed = wer.get("summed") if isinstance(wer, dict) else None
    normalised = summed.get("normalised") if isinstance(summed, dict) else None
    rate = normalised.get("rate") if isinstance(normalised, dict) else None
    return rate if isinstance(rate, (int, float)) else "?"


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--variant", type=Path,
                        default=ROOT / "runs" / "translation-onnx" / "fp32-merged",
                        help="the exported ONNX directory, which must be the one that ships")
    parser.add_argument("--languages", default=",".join(CASCADE_LANGUAGES))
    parser.add_argument("--sentences", type=int, default=0,
                        help="sentences per language; 0 (the default) for every shared sentence")
    parser.add_argument("--seed", type=int, default=20260820)
    parser.add_argument("--num-beams", type=int, default=6)
    parser.add_argument("--batch-size", type=int, default=1)
    parser.add_argument("--threads", type=int, default=12,
                        help="ASR decode threads, passed straight to transcribe")
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--compare-normaliser", type=Path, default=None,
                        help="a finished run directory: re-translate its recognised text through "
                             "the C# shipping path and report what GermanNumberWords buys")
    args = parser.parse_args()

    if args.compare_normaliser is not None:
        return compare_normaliser(args.compare_normaliser, "de")

    harness = load_translation_harness()

    languages = [code.strip() for code in args.languages.split(",") if code.strip()]
    unknown = [code for code in languages
               if harness.FLEURS_CONFIGS.get(code) not in FLEURS_AUDIO_PINS]
    if unknown:
        parser.error(
            f"no pinned audio for: {', '.join(unknown)}. Add it to FLEURS_AUDIO_PINS with the "
            "digest the repository publishes, and say in the result which languages were fetched.")

    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    out = args.out or (ROOT / "runs" / "cascade" / f"{stamp}-{args.variant.name}")
    out.mkdir(parents=True, exist_ok=True)

    print(f"variant    {args.variant}")
    print(f"languages  {', '.join(languages)}  beams {args.num_beams}  batch {args.batch_size}")
    print(f"out        {out}\n")

    print(f"fetching FLEURS en ({harness.FLEURS_CONFIGS['en']}) ...", flush=True)
    english_path, english_digest = harness.fetch_split(harness.FLEURS_CONFIGS["en"], "test")
    english = harness.read_sentences(english_path)
    print(f"  {len(english)} reference sentences  {english_digest[:16]}...")

    corpus = {harness.FLEURS_CONFIGS["en"]: {"sha256": english_digest, "sentences": len(english)}}

    print("\nloading the exported model ...", flush=True)
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(args.variant)
    model = ORTModelForSeq2SeqLM.from_pretrained(args.variant, use_cache=True)
    print("  loaded")

    signature = None
    per_language: dict[str, dict] = {}

    for code in languages:
        config = harness.FLEURS_CONFIGS[code]
        print(f"\n{code} ({config})", flush=True)

        tsv_path, tsv_digest = harness.fetch_split(config, "test")
        rows = read_rows(tsv_path)
        shared = sorted(set(rows) & set(english))
        ids = harness.choose_ids(shared, args.sentences, args.seed)
        corpus[config] = {"sha256": tsv_digest, "sentences": len(rows), "sharedWithEnglish": len(shared)}
        print(f"  {len(rows)} sentences, {len(shared)} shared with English, scoring {len(ids)}")

        tarball = fetch_audio(config)
        corpus[config]["audio"] = {
            "path": FLEURS_AUDIO_PINS[config]["path"],
            "revision": FLEURS_REVISION,
            "sizeBytes": FLEURS_AUDIO_PINS[config]["sizeBytes"],
            "sha256": FLEURS_AUDIO_PINS[config]["sha256"],
        }
        print(f"  audio verified against the published digest")

        audio_dir = out / "audio" / code
        wanted = {sentence_id: rows[sentence_id][0] for sentence_id in ids}
        clips = extract(tarball, wanted, audio_dir)
        missing = [sentence_id for sentence_id in ids if sentence_id not in clips]
        if missing:
            print(f"  {len(missing)} clips missing from the tarball; they are dropped and counted")
        ids = [sentence_id for sentence_id in ids if sentence_id in clips]
        print(f"  {len(clips)} clips extracted")

        sources = [rows[sentence_id][1] for sentence_id in ids]
        references = [english[sentence_id] for sentence_id in ids]

        # ── the recogniser ────────────────────────────────────────────────────────────────────
        print(f"  transcribing on the CPU ...", flush=True)
        text_dir = out / "asr" / code
        recognised_by_id, asr_seconds = transcribe(clips, text_dir, args.threads)
        recognised = [recognised_by_id[sentence_id] for sentence_id in ids]
        empty = sum(1 for text in recognised if not text)
        print(f"    {asr_seconds:.1f} s, {empty} empty")

        wer = word_error_rate(text_dir, {sentence_id: rows[sentence_id][1] for sentence_id in ids}, out / code)
        print(f"    WER {wer_rate(wer)}   ({wer.get('normaliser')})")

        # ── both arms of the translation, same sentences, same call ───────────────────────────
        print(f"  translating the reference transcripts (text-in arm) ...", flush=True)
        text_in, text_in_seconds = harness.translate(
            model, tokenizer, sources, args.num_beams, args.batch_size)
        text_in_score, signature = harness.score(text_in, references)
        print(f"\n    chrF++ {text_in_score}   {text_in_seconds:.0f} s")

        print(f"  translating the recognised text (cascade arm) ...", flush=True)
        cascade, cascade_seconds = harness.translate(
            model, tokenizer, recognised, args.num_beams, args.batch_size)
        cascade_score, _ = harness.score(cascade, references)
        print(f"\n    chrF++ {cascade_score}   {cascade_seconds:.0f} s")

        penalty = round(text_in_score - cascade_score, 2)
        print(f"  cascade penalty  {penalty:+.2f} chrF++")

        (out / code).mkdir(parents=True, exist_ok=True)
        with (out / code / "sentences.jsonl").open("w", encoding="utf-8") as handle:
            for index, sentence_id in enumerate(ids):
                handle.write(json.dumps({
                    "id": sentence_id,
                    "audio": wanted[sentence_id],
                    "source": sources[index],
                    "recognised": recognised[index],
                    "textIn": text_in[index],
                    "cascade": cascade[index],
                    "reference": references[index],
                }, ensure_ascii=False) + "\n")

        per_language[code] = {
            "config": config,
            "scoredSentences": len(ids),
            "clipsMissing": len(missing),
            "emptyTranscripts": empty,
            "chrF2ppTextIn": text_in_score,
            "chrF2ppCascade": cascade_score,
            "cascadePenalty": penalty,
            "wordErrorRate": wer,
            "asrSeconds": round(asr_seconds, 1),
            "textInSeconds": round(text_in_seconds, 1),
            "cascadeSeconds": round(cascade_seconds, 1),
        }

    result = {
        "producedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "model": harness.MODEL,
        "variant": str(args.variant),
        "variantManifest": harness.manifest_digests(args.variant),
        "corpus": {"dataset": "google/fleurs", "split": "test", "revision": FLEURS_REVISION,
                   "files": corpus},
        "decode": {"numBeams": args.num_beams, "batchSize": args.batch_size,
                   "targetToken": harness.TARGET_TOKEN},
        "asr": {"backend": "cpu", "threads": args.threads,
                "languageHint": None,
                "note": "--language is inert for this checkpoint; the recogniser is told nothing."},
        "metric": {"chrF2pp": signature},
        "producedBy": harness.toolchain(),
        "languages": per_language,
    }

    (out / "cascade.json").write_text(
        json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")

    lines = [
        "# The cascade penalty — what ASR error costs the translation",
        "",
        f"Produced {result['producedAt']}, {args.variant.name}, beam-{args.num_beams}, batch "
        f"{args.batch_size}, ASR on the CPU.",
        "",
        "Both arms are computed in this run over the same sentence ids, so the difference between "
        "them is the recogniser and nothing else.",
        "",
        "| | sentences | text-in chrF++ | cascade chrF++ | penalty | ASR WER |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for code, value in per_language.items():
        rate = wer_rate(value["wordErrorRate"])
        rate = f"{rate:.2%}" if isinstance(rate, float) else "?"
        lines.append(
            f"| {code} | {value['scoredSentences']} | {value['chrF2ppTextIn']} | "
            f"{value['chrF2ppCascade']} | {value['cascadePenalty']:+.2f} | {rate} |")

    lines += [
        "",
        f"chrF++ signature `{signature}`.",
        "",
        "**This is a lower bound.** FLEURS is read speech of Wikipedia-derived sentences, which is "
        "the easy end for a recogniser; spontaneous speech is worse at the ASR step and worse again "
        "after it. The WER beside each penalty is what makes it decomposable: a large penalty with a "
        "small WER is a translator brittle to slightly-off text, and a large penalty with a large "
        "WER is a recogniser problem.",
    ]
    (out / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"\nwrote {out / 'cascade.json'} and summary.md")
    return 0


if __name__ == "__main__":
    sys.exit(main())
