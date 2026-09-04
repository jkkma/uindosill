"""Does this machine's stack reproduce the translator the published figures describe?

`auto` selects WebGPU, and WebGPU's faithfulness was measured on one RTX 5080 with one driver. That
is a prior, not a guarantee: DirectML's diarisation defect turned out to be driver-mediated, so
"faithful where it was measured" does not transfer, and a wrong translator produces English rather
than an error. This is the cheap check that stands between a user and a translation that is wrong in
a way nothing in it reveals. **It is the only such check left**: the diariser had one until
2026-08-27 and it went to `attic/sortformer/` with the ONNX engine it compared, since the pipeline
that replaced it is torch on both stages and has one path where parity needs two.

**It was never that check in a different costume, and the difference is why only this one survived
being useful.** The diariser's compared probabilities, with three orders of magnitude of daylight
between a faithful provider (about 1e-06) and a diverging one (about 1e-03), so its threshold was a
measurement. A translation is a string: the comparison here is identical-or-not, per sentence, with no margin at all. A provider that
diverges only on long or unusual inputs passes this and fails a corpus. What it does catch is the
failure that has actually been observed — DirectML's repetition-loop collapse, which was wrong on
**all 32** sentences measured, not on a subtle few.

**What actually establishes the translator on a machine is the gate corpus**, through
`measure-translation-agreement.ps1` over 8,149 sentences. This is a smoke test with a good reason to
exist, and calling it "parity" should not be read as claiming the diariser fixture's sensitivity.

**One fixture per checkpoint, since 2026-09-04.** Each is six sentences, four of them real output
from this project's own ASR rather than written text — which is the input the shipping path actually
sends — and each is the shape the host sends its checkpoint: marked with `>>eng<<` for the
many-to-one checkpoint, bare for the single-direction Japanese one, whose vocabulary has no such
piece. The first fixture's sources come from the committed tokenizer fixture and are duplicated
here rather than read out of the test tree, because a sidecar reaching into `tests/` is a sidecar
that stops working the day the tree is packaged.

**The fixture is chosen by vocabulary size**, because that is the one identity a checkpoint
directory reliably carries: the exports write no name into `config.json`, and the two checkpoints
differ by 26,433 pieces. A checkpoint with a fixture's vocabulary and different weights would fail
the check loudly rather than pass it, which is the safe direction; a checkpoint with no fixture is
reported as unchecked rather than as either.
"""

from __future__ import annotations

import glob
import json
import os
from typing import Any

#: One sources file per checkpoint: `parity-sources.json` for the many-to-one checkpoint that
#: shipped first, `parity-sources.<family>.json` for each one since. The reference beside each is
#: the same name with `reference` in place of `sources`.
SOURCES_PATTERN = "parity-sources*.json"

SOURCES_PREFIX = "parity-sources"

REFERENCE_PREFIX = "parity-reference"


def _directory() -> str:
    return os.path.dirname(os.path.abspath(__file__))


def fixtures() -> list[dict[str, Any]]:
    """Every committed fixture, oldest name first, each with the path its reference should be at."""
    found: list[dict[str, Any]] = []
    for path in sorted(glob.glob(os.path.join(_directory(), SOURCES_PATTERN))):
        with open(path, encoding="utf-8") as handle:
            fixture = dict(json.load(handle))
        name = os.path.basename(path)
        fixture["sourcesPath"] = path
        fixture["referencePath"] = os.path.join(
            _directory(), name.replace(SOURCES_PREFIX, REFERENCE_PREFIX, 1))
        found.append(fixture)
    return found


def fixture_for(engine: Any) -> dict[str, Any] | None:
    """The fixture whose checkpoint the loaded engine is, or None when none is committed for it."""
    size = int(engine.vocab_size)
    matching = [fixture for fixture in fixtures() if int(fixture.get("vocabSize", -1)) == size]
    if len(matching) > 1:
        # Two fixtures claiming one vocabulary is an authoring error, and picking the first would
        # make the check pass or fail on file order.
        names = ", ".join(os.path.basename(fixture["sourcesPath"]) for fixture in matching)
        raise ValueError(f"{len(matching)} parity fixtures claim a vocabulary of {size} pieces: {names}")
    return matching[0] if matching else None


def sources(fixture: dict[str, Any]) -> list[str]:
    return list(fixture["sources"])


def compute(engine: Any, fixture: dict[str, Any]) -> list[str]:
    """Translates a fixture's sources through a loaded engine."""
    return [engine.translate(source) for source in sources(fixture)]


def check(engine: Any) -> dict[str, Any]:
    """Compares this engine's translations against the committed reference for its checkpoint.

    Returns what differed rather than a count alone. "Two of six differ" tells a user nothing they
    can judge; the sentence that came back instead tells them immediately whether they are looking
    at a rounding difference or at a decoder repeating itself for 512 tokens.
    """
    fixture = fixture_for(engine)
    if fixture is None:
        return {
            "available": False,
            "reason": f"no parity fixture is committed for a checkpoint with a vocabulary of "
                      f"{engine.vocab_size} pieces",
        }

    path = fixture["referencePath"]
    if not os.path.isfile(path):
        return {"available": False, "reason": f"no parity reference committed at {path}"}

    with open(path, encoding="utf-8") as handle:
        expected = list(json.load(handle)["translations"])

    actual = compute(engine, fixture)
    if len(expected) != len(actual):
        return {
            "available": True,
            "passed": False,
            "reason": f"{len(actual)} translations against the reference's {len(expected)}",
        }

    differing = [
        {"source": source, "expected": want, "actual": got}
        for source, want, got in zip(sources(fixture), expected, actual)
        if want != got
    ]

    return {
        "available": True,
        "passed": not differing,
        "identical": len(actual) - len(differing),
        "total": len(actual),
        # Capped, and capped rather than omitted: a collapsed decoder returns 512 tokens of the same
        # phrase six times over, and the whole of that in an error message buries the one line that
        # says which provider produced it.
        "differing": [
            {key: value[:200] for key, value in row.items()} for row in differing[:3]
        ],
    }
