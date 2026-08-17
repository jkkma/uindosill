#!/usr/bin/env python3
"""Validate the C# diarisation scorer against pyannote.metrics, on committed fixture pairs.

`uindosill der` computes DER the way pyannote.metrics does, and the number the ship gate is written
in is only worth trusting once that has been checked rather than reasoned about. This script is the
check: it scores every pair in tests/fixtures/diarisation/scorer/ with pyannote.metrics at the
benchmark's exact settings — DiarizationErrorRate(collar=0.25, skip_overlap=False), the convention
of arXiv 2509.26177 — plus the strict collar-0 number and the overlap-region breakdown, and writes
what it got to expected.json beside the fixtures. The C# test suite then asserts the scorer
reproduces every figure in that file, so CI holds the validation without needing Python.

Two conventions are pinned here and everywhere the number appears:

  * pyannote's `collar` is a TOTAL width centred on each reference boundary — collar=0.25 forgives
    0.125 s either side. NIST md-eval's `-c 0.25` and NeMo's `collar=0.25` are half-widths, i.e.
    pyannote's 0.5. NeMo's own docstring says so ("No-score collar half-width in seconds").
  * The overlap-region breakdown is the same components restricted to regions where two or more
    distinct reference speakers talk at once, under the SAME speaker mapping as the whole-file
    score. In pyannote terms: the mapping DiarizationErrorRate finds on the collar-extruded pair
    (uemify with the headline collar, then optimal_mapping — compute_components does the same
    before its Hungarian search), the hypothesis renamed with it, IdentificationErrorRate over
    uem = reference.get_overlap(). It is additive with the rest of the file, which a per-region
    re-mapping would not be.
  * skip_overlap extrudes every pairwise overlap of reference TRACKS, whatever their labels — a
    speaker overlapping themselves is skipped too — while get_overlap (the breakdown) is over
    distinct labels. Both rules are validated: a fourth block scores each pair with skip_overlap=True.

Modes:

  --generate       (re)write the synthetic fixture pairs from the recipes below. Deterministic:
                   the same bytes every time, so a regenerated fixture diffs empty.
  (default)        score every pair with pyannote.metrics and write expected.json.
  --exe PATH       additionally run `PATH der --json` on every pair and compare, printing a table.
  --check          compare against the existing expected.json instead of rewriting it.

Needs pyannote.metrics (4.1 was used; the version is recorded in expected.json). Make a venv
OUTSIDE the working tree for it, never the system Python:

  python -m venv %USERPROFILE%\\pyannote-metrics-venv
  %USERPROFILE%\\pyannote-metrics-venv\\Scripts\\pip install pyannote.metrics==4.1
  %USERPROFILE%\\pyannote-metrics-venv\\Scripts\\python scripts\\validate-der.py --exe src\\Parakeet.Cli\\bin\\Release\\net10.0\\uindosill.exe
"""

from __future__ import annotations

import argparse
import json
import random
import subprocess
import sys
import warnings
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FIXTURES = ROOT / "tests" / "fixtures" / "diarisation" / "scorer"
EXPECTED = FIXTURES / "expected.json"

HEADLINE_COLLAR = 0.25  # pyannote semantics: 0.125 s either side of every reference boundary
TOLERANCE = 1e-6  # seconds; pyannote.core's own segment precision


# ── the fixture recipes ─────────────────────────────────────────────────────────────────────────
#
# Each recipe returns (reference turns, hypothesis turns) as lists of (start, end, speaker). They
# are chosen to put every branch of the scorer in front of pyannote: overlap between speakers,
# same-speaker self-overlap (counted twice by both), boundary jitter inside and outside the collar,
# confusion, missed and false-alarm turns, extra hypothesis speakers, fewer hypothesis speakers,
# hypothesis speech outside the reference's extent, and one long jittered conversation so the
# floating-point paths are exercised over a few hundred boundaries.


def rttm_lines(file_id: str, turns) -> str:
    lines = []
    for start, end, speaker in sorted(turns, key=lambda t: (t[0], t[1], t[2])):
        lines.append(f"SPEAKER {file_id} 1 {start:.3f} {end - start:.3f} <NA> <NA> {speaker} <NA> <NA>")
    return "\n".join(lines) + "\n"


def recipe_perfect_relabelled():
    ref = [(0.0, 4.0, "host_a"), (4.0, 7.5, "host_b"), (7.0, 12.0, "host_a"), (12.5, 20.0, "host_b")]
    hyp = [(s, e, {"host_a": "SPEAKER_01", "host_b": "SPEAKER_00"}[k]) for s, e, k in ref]
    return ref, hyp


def recipe_two_speakers_typical():
    ref = [
        (0.50, 6.20, "host_a"),
        (6.00, 9.80, "host_b"),      # 0.2 s overlap with the turn before
        (9.90, 10.30, "host_a"),     # a 400 ms back-channel — the collar does not hide it
        (10.10, 18.40, "host_b"),    # overlaps the back-channel
        (18.90, 25.00, "host_a"),
        (25.30, 31.70, "host_b"),
        (31.70, 40.00, "host_a"),
    ]
    hyp = [
        (0.42, 6.05, "SPEAKER_00"),   # boundaries jittered inside the collar
        (6.05, 9.95, "SPEAKER_01"),
        (10.20, 18.30, "SPEAKER_01"), # the back-channel is missed entirely
        (18.60, 22.00, "SPEAKER_00"),
        (22.00, 25.10, "SPEAKER_01"), # confusion: host_a's turn ends up under the other label
        (25.40, 31.60, "SPEAKER_01"),
        (31.60, 40.20, "SPEAKER_00"),
        (41.00, 43.50, "SPEAKER_00"), # false alarm past the reference's end
    ]
    return ref, hyp


def recipe_three_speakers_merged_to_two():
    ref = [
        (0.0, 5.0, "host_a"), (5.0, 9.0, "guest"), (9.0, 14.0, "host_b"),
        (14.0, 17.0, "guest"), (17.0, 22.0, "host_a"), (22.0, 26.0, "host_b"), (26.0, 30.0, "guest"),
    ]
    # The guest is folded into whichever host the hypothesis heard last.
    hyp = [
        (0.0, 9.0, "1"), (9.0, 17.0, "2"), (17.0, 22.0, "1"), (22.0, 30.0, "2"),
    ]
    return ref, hyp


def recipe_over_clustered():
    ref = [(0.0, 10.0, "host_a"), (10.0, 20.0, "host_b"), (20.0, 30.0, "host_a"), (30.0, 40.0, "host_b")]
    hyp = [
        (0.0, 10.0, "c0"), (10.0, 15.0, "c1"), (15.0, 20.0, "c2"),  # host_b split in two
        (20.0, 30.0, "c0"), (30.0, 33.0, "c1"), (33.0, 40.0, "c3"),  # and again, into a fourth
    ]
    return ref, hyp


def recipe_crosstalk():
    # Two hosts talking over each other for most of the stretch; the hypothesis hears one at a time.
    ref = [
        (0.0, 8.0, "host_a"), (3.0, 12.0, "host_b"), (10.0, 20.0, "host_a"), (15.0, 26.0, "host_b"),
        (24.0, 30.0, "host_a"), (30.0, 34.0, "host_b"), (33.0, 40.0, "host_a"),
    ]
    hyp = [
        (0.0, 5.5, "s0"), (5.5, 11.0, "s1"), (11.0, 17.5, "s0"), (17.5, 27.0, "s1"),
        (27.0, 32.0, "s0"), (32.0, 36.5, "s1"), (36.5, 40.0, "s0"),
    ]
    return ref, hyp


def recipe_self_overlap():
    # A labelling slip: one speaker's turns overlap themselves. Both scorers count that stretch
    # twice; the C# scorer says so in a warning and this fixture pins that it agrees anyway.
    ref = [(0.0, 10.0, "host_a"), (5.0, 15.0, "host_a"), (15.0, 20.0, "host_b")]
    hyp = [(0.0, 15.0, "x"), (15.0, 20.0, "y")]
    return ref, hyp


def recipe_hypothesis_outside_extent():
    ref = [(10.0, 20.0, "host_a"), (20.0, 30.0, "host_b")]
    hyp = [(2.0, 8.0, "q"), (10.0, 20.0, "q"), (20.0, 30.0, "r"), (31.0, 38.0, "r")]
    return ref, hyp


def recipe_short_turns_collar_heavy():
    ref = [(i * 1.0, i * 1.0 + 0.6, "host_a" if i % 2 == 0 else "host_b") for i in range(30)]
    hyp = [(s + 0.05, e - 0.05, "A" if k == "host_a" else "B") for s, e, k in ref]
    return ref, hyp


def recipe_long_jittered_conversation():
    rng = random.Random(20260817)
    speakers = ["host_a", "host_b", "guest"]
    ref = []
    t = 0.0
    last = None
    while t < 600.0:
        speaker = rng.choice([s for s in speakers if s != last])
        length = rng.uniform(0.8, 9.0)
        start = t
        end = min(600.0, t + length)
        ref.append((round(start, 3), round(end, 3), speaker))
        # Sometimes the next speaker starts before this one finishes (crosstalk), sometimes a pause.
        roll = rng.random()
        if roll < 0.18:
            t = end - rng.uniform(0.2, 1.5)
        elif roll < 0.55:
            t = end
        else:
            t = end + rng.uniform(0.1, 1.2)
        last = speaker
    ref = [(s, e, k) for s, e, k in ref if e > s]

    hyp = []
    for start, end, speaker in ref:
        roll = rng.random()
        if roll < 0.06:
            continue  # missed
        label = {"host_a": "SPEAKER_00", "host_b": "SPEAKER_01", "guest": "SPEAKER_02"}[speaker]
        if roll < 0.14:
            label = rng.choice(["SPEAKER_00", "SPEAKER_01", "SPEAKER_02"])  # confused
        jitter_s = rng.uniform(-0.18, 0.18)
        jitter_e = rng.uniform(-0.18, 0.18)
        s = max(0.0, start + jitter_s)
        e = min(600.0, end + jitter_e)
        if e - s > 0.15:
            hyp.append((round(s, 3), round(e, 3), label))
    for _ in range(6):
        s = rng.uniform(0.0, 595.0)
        hyp.append((round(s, 3), round(s + rng.uniform(0.3, 2.5), 3), "SPEAKER_03"))  # false alarms, a fourth voice
    return ref, hyp


def recipe_mapping_tipped_by_collar():
    # Found by search: the one-to-one speaker mapping that maximises co-occurrence on the raw
    # annotations (x→A, y→B) is not the one on the collar-extruded annotations (x→A, z→B), and the
    # overlap-region breakdown differs under the two. pyannote finds its mapping after extruding the
    # collar, and so does the C# scorer; this pair is what stops either from quietly doing otherwise.
    ref = [
        (0.0, 4.298, "A"), (3.064, 7.757, "B"), (7.67, 13.531, "A"), (12.233, 14.549, "B"),
        (14.502, 18.831, "A"), (18.222, 20.648, "B"), (3.601, 4.11, "A"), (20.133, 20.455, "A"), (14.841, 15.471, "A"),
    ]
    hyp = [(0.0, 4.085, "x"), (4.085, 10.693, "x"), (10.693, 15.643, "z"), (15.643, 19.236, "y"), (19.236, 23.512, "y")]
    return ref, hyp


RECIPES = {
    "perfect-relabelled": recipe_perfect_relabelled,
    "two-speakers-typical": recipe_two_speakers_typical,
    "three-speakers-merged-to-two": recipe_three_speakers_merged_to_two,
    "over-clustered": recipe_over_clustered,
    "crosstalk": recipe_crosstalk,
    "self-overlap": recipe_self_overlap,
    "hypothesis-outside-extent": recipe_hypothesis_outside_extent,
    "short-turns-collar-heavy": recipe_short_turns_collar_heavy,
    "long-jittered-conversation": recipe_long_jittered_conversation,
    "mapping-tipped-by-collar": recipe_mapping_tipped_by_collar,
}


def generate() -> None:
    FIXTURES.mkdir(parents=True, exist_ok=True)
    for name, recipe in RECIPES.items():
        ref, hyp = recipe()
        (FIXTURES / f"{name}.ref.rttm").write_text(rttm_lines(name, ref), encoding="utf-8", newline="\n")
        (FIXTURES / f"{name}.hyp.rttm").write_text(rttm_lines(name, hyp), encoding="utf-8", newline="\n")
        print(f"  wrote {name}: {len(ref)} reference turns, {len(hyp)} hypothesis turns")


# ── pyannote ────────────────────────────────────────────────────────────────────────────────────


def score_with_pyannote(ref_path: Path, hyp_path: Path) -> dict:
    # Imported here so the recipes above can be read and the module imported without pyannote; every
    # mode that scores — which is every mode, --generate included, since it recomputes expected.json
    # afterwards — needs the venv the header describes.
    from pyannote.database.util import load_rttm
    from pyannote.metrics.diarization import DiarizationErrorRate
    from pyannote.metrics.identification import IdentificationErrorRate

    reference = one_file(load_rttm(str(ref_path)), ref_path)
    hypothesis = one_file(load_rttm(str(hyp_path)), hyp_path)

    def components(detail: dict) -> dict:
        total = detail["total"]
        missed = detail["missed detection"]
        fa = detail["false alarm"]
        conf = detail["confusion"]
        return {
            "referenceSpeechSeconds": round(total, 6),
            "missedSeconds": round(missed, 6),
            "falseAlarmSeconds": round(fa, 6),
            "confusionSeconds": round(conf, 6),
            "rate": None if total <= 0 else round((missed + fa + conf) / total, 9),
        }

    with warnings.catch_warnings():
        warnings.simplefilter("ignore")  # the "uem approximated by the union of extents" notice, which is the point

        headline_metric = DiarizationErrorRate(collar=HEADLINE_COLLAR, skip_overlap=False)
        strict_metric = DiarizationErrorRate(collar=0.0, skip_overlap=False)
        skip_metric = DiarizationErrorRate(collar=HEADLINE_COLLAR, skip_overlap=True)
        headline = headline_metric(reference, hypothesis, detailed=True)
        strict = strict_metric(reference, hypothesis, detailed=True)
        skipped = skip_metric(reference, hypothesis, detailed=True)

        # Overlap-region breakdown under the whole-file mapping. Labels are made disjoint first,
        # exactly as DiarizationErrorRate does internally, so an unmapped hypothesis label can
        # never collide with a reference label by accident; and the mapping is found on the
        # collar-extruded pair, exactly as compute_components does before its Hungarian search —
        # optimal_mapping on the raw annotations would be the collar-0 mapping, which is a
        # different question and, on a near-tie inside the collar, a different answer.
        ref_named = reference.rename_labels(generator="string")
        hyp_named = hypothesis.rename_labels(generator="int")
        ref_extruded, hyp_extruded = headline_metric.uemify(ref_named, hyp_named, collar=HEADLINE_COLLAR, skip_overlap=False)
        mapping = headline_metric.optimal_mapping(ref_extruded, hyp_extruded)
        mapped = hyp_named.rename_labels(mapping=mapping)
        overlap_uem = ref_named.get_overlap()
        if overlap_uem:
            ier = IdentificationErrorRate(collar=HEADLINE_COLLAR, skip_overlap=False)
            overlap = components(ier(ref_named, mapped, uem=overlap_uem, detailed=True))
        else:
            overlap = {"referenceSpeechSeconds": 0.0, "missedSeconds": 0.0, "falseAlarmSeconds": 0.0, "confusionSeconds": 0.0, "rate": None}

    return {"headline": components(headline), "strict": components(strict), "overlapRegions": overlap, "skipOverlap": components(skipped)}


def one_file(loaded: dict, path: Path):
    if len(loaded) != 1:
        sys.exit(f"error: {path} carries {len(loaded)} file ids; the fixtures hold one file per RTTM.")
    return next(iter(loaded.values()))


def pyannote_versions() -> dict:
    import pyannote.core
    import pyannote.metrics

    return {"pyannote.metrics": pyannote.metrics.__version__, "pyannote.core": pyannote.core.__version__}


def compute_expected() -> dict:
    cases = {}
    for ref_path in sorted(FIXTURES.glob("*.ref.rttm")):
        name = ref_path.name[: -len(".ref.rttm")]
        hyp_path = FIXTURES / f"{name}.hyp.rttm"
        if not hyp_path.exists():
            sys.exit(f"error: {ref_path.name} has no {hyp_path.name} beside it.")
        cases[name] = score_with_pyannote(ref_path, hyp_path)
    return {
        "comment": [
            "Expected diarisation error rate components for the fixture pairs in this directory, computed by",
            "scripts/validate-der.py with pyannote.metrics — the reference implementation this project's scorer",
            "is validated against. tests/Parakeet.Core.Tests/DiarisationTests.cs asserts the C# scorer reproduces",
            "every figure here; do not edit these values by hand, regenerate them with the script.",
            "",
            "headline: DiarizationErrorRate(collar=0.25, skip_overlap=False) — pyannote semantics, a 0.25 s no-score",
            "zone centred on every reference boundary (0.125 s either side); the convention of arXiv 2509.26177.",
            "strict: the same with collar 0. overlapRegions: IdentificationErrorRate over uem = reference.get_overlap()",
            "with the hypothesis renamed by the whole-file optimal mapping (found on the collar-extruded pair, as",
            "DiarizationErrorRate itself finds it), collar 0.25 — the same components over regions where two or more",
            "distinct reference speakers talk at once, under the same mapping as headline. skipOverlap:",
            "DiarizationErrorRate(collar=0.25, skip_overlap=True) — every pairwise overlap of reference turns removed,",
            "same speaker or not, which is pyannote's rule and differs from get_overlap's distinct-label rule.",
        ],
        "conventions": {
            "headline": {"collarSeconds": HEADLINE_COLLAR, "collarSemantics": "pyannote.metrics: total width centred on each reference boundary", "skipOverlap": False},
            "strict": {"collarSeconds": 0.0, "skipOverlap": False},
            "overlapRegions": "reference.get_overlap(), whole-file optimal mapping (collar-extruded) held fixed, collar 0.25",
            "skipOverlap": {"collarSeconds": HEADLINE_COLLAR, "skipOverlap": True},
        },
        "producedBy": pyannote_versions(),
        "cases": cases,
    }


# ── the C# scorer, cross-checked live ───────────────────────────────────────────────────────────


def run_cli(exe: str, ref_path: Path, hyp_path: Path) -> dict:
    def run(*extra):
        result = subprocess.run(
            [str(Path(exe).resolve()), "der", "--reference", str(ref_path), "--collar", str(HEADLINE_COLLAR), "--json", *extra, str(hyp_path)],
            capture_output=True, text=True, encoding="utf-8", check=False,
        )
        if result.returncode != 0:
            sys.exit(f"error: {exe} der failed on {hyp_path.name} (exit {result.returncode}):\n{result.stderr}")
        return json.loads(result.stdout)["hypotheses"][0]

    scored = run()
    scored["skipOverlap"] = run("--skip-overlap")["headline"]
    return scored


def compare(expected: dict, actual_by_case: dict) -> int:
    failures = 0
    print(f"{'case':<32} {'block':<15} {'total':>10} {'missed':>10} {'FA':>10} {'conf':>10} {'DER':>9}  vs pyannote")
    for name, blocks in expected["cases"].items():
        actual = actual_by_case[name]
        for block in ("headline", "strict", "overlapRegions", "skipOverlap"):
            want = blocks[block]
            got = actual[block]
            fields = ["referenceSpeechSeconds", "missedSeconds", "falseAlarmSeconds", "confusionSeconds"]
            deltas = [abs(float(want[f]) - float(got[f])) for f in fields]
            want_rate = want["rate"]
            got_rate = got["rate"]
            # The CLI's JSON rounds rates to six decimals (a millionth), so that is the precision compared.
            rate_ok = (want_rate is None and got_rate is None) or (
                want_rate is not None and got_rate is not None and abs(want_rate - got_rate) <= 1e-6)
            ok = all(d <= TOLERANCE for d in deltas) and rate_ok
            failures += 0 if ok else 1
            rate_text = "n/a" if got_rate is None else f"{100 * got_rate:8.4f}%"
            print(f"{name:<32} {block:<15} {got['referenceSpeechSeconds']:>10.4f} {got['missedSeconds']:>10.4f} "
                  f"{got['falseAlarmSeconds']:>10.4f} {got['confusionSeconds']:>10.4f} {rate_text:>9}  "
                  f"{'agrees' if ok else 'DIFFERS: ' + str([round(d, 6) for d in deltas]) + ' rate ' + str(want_rate) + ' vs ' + str(got_rate)}")
    return failures


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--generate", action="store_true", help="rewrite the synthetic fixture pairs from the recipes")
    parser.add_argument("--check", action="store_true", help="compare against the existing expected.json instead of rewriting it")
    parser.add_argument("--exe", help="path to the built uindosill executable, to cross-check `der --json` live")
    args = parser.parse_args()

    if args.generate:
        print(f"Generating fixtures into {FIXTURES}")
        generate()

    expected = compute_expected()

    if args.check:
        if not EXPECTED.exists():
            sys.exit(f"error: {EXPECTED} does not exist; run without --check to write it.")
        on_disk = json.loads(EXPECTED.read_text(encoding="utf-8"))
        if on_disk["cases"] != expected["cases"]:
            print("expected.json is out of date with what pyannote.metrics computes now:")
            for name in sorted(set(on_disk["cases"]) | set(expected["cases"])):
                if on_disk["cases"].get(name) != expected["cases"].get(name):
                    print(f"  {name}: on disk {on_disk['cases'].get(name)}\n{'':>{len(name) + 4}}now     {expected['cases'].get(name)}")
            return 1
        print(f"expected.json agrees with pyannote.metrics {expected['producedBy']['pyannote.metrics']} on {len(expected['cases'])} pairs.")
    else:
        EXPECTED.write_text(json.dumps(expected, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
        print(f"Wrote {EXPECTED} — {len(expected['cases'])} pairs, pyannote.metrics {expected['producedBy']['pyannote.metrics']}.")

    if args.exe:
        actual = {}
        for name in expected["cases"]:
            actual[name] = run_cli(args.exe, FIXTURES / f"{name}.ref.rttm", FIXTURES / f"{name}.hyp.rttm")
        print()
        failures = compare(expected, actual)
        print()
        if failures:
            print(f"{failures} block(s) DIFFER from pyannote.metrics. The scorer is wrong until shown otherwise.")
            return 1
        print(f"The C# scorer agrees with pyannote.metrics on every block of every pair (tolerance {TOLERANCE} s).")

    return 0


if __name__ == "__main__":
    sys.exit(main())
