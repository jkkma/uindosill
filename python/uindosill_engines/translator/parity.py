"""Does this machine's stack reproduce the translator the published figures describe?

`auto` selects WebGPU, and WebGPU's faithfulness was measured on one RTX 5080 with one driver. That
is a prior, not a guarantee: DirectML's diarisation defect turned out to be driver-mediated, so
"faithful where it was measured" does not transfer, and a wrong translator produces English rather
than an error. This is the cheap check that stands between a user and a translation that is wrong in
a way nothing in it reveals — the same job :mod:`..diariser.parity` does, run for the same reason.

**It is not that check in a different costume, and the difference matters.** The diariser compares
probabilities and has three orders of magnitude of daylight between a faithful provider (about
1e-06) and a diverging one (about 1e-03), so its threshold is a measurement. A translation is a
string: the comparison here is identical-or-not, per sentence, with no margin at all. A provider that
diverges only on long or unusual inputs passes this and fails a corpus. What it does catch is the
failure that has actually been observed — DirectML's repetition-loop collapse, which was wrong on
**all 32** sentences measured, not on a subtle few.

**What actually establishes the translator on a machine is the gate corpus**, through
`measure-translation-agreement.ps1` over 8,149 sentences. This is a smoke test with a good reason to
exist, and calling it "parity" should not be read as claiming the diariser fixture's sensitivity.

**Why these sources.** Six sentences, already marked with the target token, four of them real output
from this project's own ASR rather than written text — which is the input the shipping path actually
sends. They come from the committed tokenizer fixture and are duplicated here rather than read out
of the test tree, because a sidecar reaching into `tests/` is a sidecar that stops working the day
the tree is packaged.
"""

from __future__ import annotations

import json
import os
from typing import Any

FIXTURE_NAME = "parity-reference.json"

SOURCES_NAME = "parity-sources.json"


def _path(name: str) -> str:
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), name)


def reference_path() -> str:
    return _path(FIXTURE_NAME)


def sources() -> list[str]:
    with open(_path(SOURCES_NAME), encoding="utf-8") as handle:
        return list(json.load(handle)["sources"])


def compute(engine: Any) -> list[str]:
    """Translates the fixture's sources through a loaded engine."""
    return [engine.translate(source) for source in sources()]


def check(engine: Any) -> dict[str, Any]:
    """Compares this engine's translations against the committed reference.

    Returns what differed rather than a count alone. "Two of six differ" tells a user nothing they
    can judge; the sentence that came back instead tells them immediately whether they are looking
    at a rounding difference or at a decoder repeating itself for 512 tokens.
    """
    path = reference_path()
    if not os.path.isfile(path):
        return {"available": False, "reason": f"no parity reference committed at {path}"}

    with open(path, encoding="utf-8") as handle:
        expected = list(json.load(handle)["translations"])

    actual = compute(engine)
    if len(expected) != len(actual):
        return {
            "available": True,
            "passed": False,
            "reason": f"{len(actual)} translations against the reference's {len(expected)}",
        }

    differing = [
        {"source": source, "expected": want, "actual": got}
        for source, want, got in zip(sources(), expected, actual)
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
