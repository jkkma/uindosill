#!/usr/bin/env python3
"""Hold the test counts written into the documentation against the counts the suite reports.

Three documents quote a test total, and all three drifted: they said 258 for several commits
after the suite reached 265, including one commit whose own message said 264. A number nobody
checks is a number that goes stale, and this repository's whole claim is that its figures are
measured. So the figures get measured here.

What is checked, and against what:

  README.md, CLAUDE.md, docs/PHASES.md   "N tests"       the whole suite's total
  docs/PHASES.md                         "N CLI tests"   Parakeet.Cli.Tests alone
  docs/PHASES.md                         "N passed and M skipped"

A claim that matches nothing is a failure too, not a pass. Rewording a sentence out of the reach
of these patterns would otherwise silently retire the check, which is the failure mode that makes
a stale-number guard worse than none.

`docs/UNPROVEN.md` is deliberately not scanned. Its counts (247, 215) are dated records of one run
on one machine, in sections that are retrospective by construction; updating them to match today
would destroy the measurement rather than refresh it.

Reads the TRX files `dotnet test --logger trx` leaves under `tests/*/TestResults/`, and runs the
suite itself if none are there — or if the ones there are older than the assemblies they would be
counted as, which is the same situation wearing a green tick. Pass --no-run to fail instead, which
is what CI does: the workflow has already run the tests by this point, and a silent re-run would
hide a reordered job.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TRX_GLOB = "tests/*/TestResults/*.trx"
TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

# (file, pattern, the counter each capture group must equal). The patterns tolerate a line break
# inside the phrase because these documents wrap at 100 columns and the numbers land wherever the
# wrapping puts them.
CLAIMS: list[tuple[str, str, tuple[str, ...]]] = [
    ("README.md", r"(\d+)\s+tests\b", ("total",)),
    ("CLAUDE.md", r"(\d+)\s+tests\b", ("total",)),
    ("docs/PHASES.md", r"(\d+)\s+tests\b", ("total",)),
    ("docs/PHASES.md", r"(\d+)\s+CLI\s+tests\b", ("Parakeet.Cli.Tests",)),
    ("docs/PHASES.md", r"(\d+)\s+passed\s+and\s+(\d+)\s+skipped", ("passed", "skipped")),
]


def find_trx() -> list[Path]:
    """The newest TRX per test project, so a stale results file from an earlier run cannot vote."""
    newest: dict[Path, Path] = {}
    for path in ROOT.glob(TRX_GLOB):
        directory = path.parent
        if directory not in newest or path.stat().st_mtime > newest[directory].stat().st_mtime:
            newest[directory] = path
    return sorted(newest.values())


def stale_trx(files: list[Path]) -> list[tuple[str, Path]]:
    """Results files older than the assembly they claim to count.

    Picking the newest TRX per project is not enough, and the gap is not hypothetical: a plain
    `dotnet test` writes no TRX at all, so after one this script reads whatever an earlier session
    left behind and reports it as today's number. On 2026-08-21 that made it pass three documents
    that were four tests out of date — the guard failing open, which is worse than no guard,
    because a green check is taken as an answer.

    The assembly's timestamp is the comparison because it is what was actually run: results that
    predate the binary describe a different binary. Sources are deliberately not compared — a source
    newer than the assembly is a stale *build*, which the suite fixes by rebuilding, and folding the
    two together would make this fail for a reason it cannot name precisely.
    """
    stale: list[tuple[str, Path]] = []
    for trx in files:
        project = trx.parent.parent
        built = [
            candidate for candidate in project.glob(f"bin/**/{project.name}.dll")
        ]
        if not built:
            # Nothing to compare against — a results file with no build beside it is a clone that
            # has been cleaned, not a stale number, and read_counters will still describe it.
            continue

        newest = max(candidate.stat().st_mtime for candidate in built)
        if newest > trx.stat().st_mtime:
            stale.append((project.name, trx))

    return sorted(stale)


def run_suite() -> None:
    print("No TRX files found; running the suite to produce them.", flush=True)
    subprocess.run(
        [
            "dotnet", "test", "Uindosill.slnx",
            "--configuration", "Release",
            "--logger", "trx;LogFileName=results.trx",
        ],
        cwd=ROOT,
        check=True,
    )


def read_counters(files: list[Path]) -> tuple[dict[str, int], dict[str, int]]:
    """Sum the TRX counters, and key each project's total by its test project directory name."""
    totals = {"total": 0, "passed": 0, "failed": 0, "skipped": 0}
    per_project: dict[str, int] = {}

    for path in files:
        counters = ET.parse(path).getroot().find(f"{TRX_NS}ResultSummary/{TRX_NS}Counters")
        if counters is None:
            sys.exit(f"error: {path} has no <Counters> element; is it a TRX file?")

        total = int(counters.get("total", 0))
        executed = int(counters.get("executed", 0))
        totals["total"] += total
        totals["passed"] += int(counters.get("passed", 0))
        totals["failed"] += int(counters.get("failed", 0))
        # Skips are total-minus-executed rather than the notExecuted attribute, which xUnit's
        # dynamic skips (Assert.SkipUnless) leave at zero while still not executing the test.
        totals["skipped"] += total - executed

        # tests/<Project>/TestResults/results.trx
        per_project[path.parent.parent.name] = total

    return totals, per_project


def line_of(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--no-run",
        action="store_true",
        help="fail if no TRX files exist rather than running the suite to produce them",
    )
    args = parser.parse_args()

    files = find_trx()
    stale = stale_trx(files)

    # Missing and stale are the same situation — there are no results for the code as it stands —
    # so they get the same treatment: run the suite, unless the caller said not to.
    if (not files or stale) and not args.no_run:
        run_suite()
        files = find_trx()
        stale = stale_trx(files)

    if not files:
        print(f"::error::No TRX files under {TRX_GLOB}. Run the suite with --logger trx first.")
        return 1

    if stale:
        for project, trx in stale:
            print(
                f"::error::{trx.relative_to(ROOT)} is older than the {project} assembly it would "
                f"be counted as, so it describes a build that no longer exists."
            )
        print(
            "::error::Run `dotnet test Uindosill.slnx -c Release --logger trx` and check again. "
            "A plain `dotnet test` writes no results file, which is how these came to be stale."
        )
        return 1

    totals, per_project = read_counters(files)

    # A project whose results are missing makes every total here too low, and the documents would
    # be blamed for it. Fail on the real cause instead: this is a partial run, not a stale number.
    missing = sorted({p.parent.name for p in ROOT.glob("tests/*/*.csproj")} - per_project.keys())
    if missing:
        print(
            f"::error::No test results for {', '.join(missing)}. That is a partial run rather "
            f"than a stale document — run the whole suite before checking the counts."
        )
        return 1

    expected = {**totals, **per_project}

    print(
        f"{totals['total']} tests: {totals['passed']} passed, {totals['skipped']} skipped, "
        f"{totals['failed']} failed, across {len(files)} assemblies"
    )
    for name, count in sorted(per_project.items()):
        print(f"  {count:>5}  {name}")
    print()

    annotate = os.environ.get("GITHUB_ACTIONS") == "true"
    failures = 0

    for relative, pattern, counters in CLAIMS:
        path = ROOT / relative
        text = path.read_text(encoding="utf-8")
        matches = list(re.finditer(pattern, text))

        if not matches:
            failures += 1
            message = (
                f"{relative} states no /{pattern}/ any more. Either the sentence lost its count "
                f"or the wording moved out of reach of this check; fix the document, or fix the "
                f"pattern in scripts/check-test-counts.py so the count stays checked."
            )
            print(f"::error file={relative}::{message}" if annotate else f"FAIL  {message}")
            continue

        for match in matches:
            line = line_of(text, match.start())
            claimed = [int(group) for group in match.groups()]
            want = [expected[counter] for counter in counters]
            phrase = " ".join(match.group(0).split())

            if claimed == want:
                print(f"  ok    {relative}:{line}  \"{phrase}\"")
                continue

            failures += 1
            detail = ", ".join(
                f"{counter} is {right}, document says {left}"
                for counter, left, right in zip(counters, claimed, want)
                if left != right  # a phrase can carry two numbers and get only one of them wrong
            )
            message = f"\"{phrase}\" is out of date: {detail}."
            print(
                f"::error file={relative},line={line}::{message}"
                if annotate
                else f"  FAIL  {relative}:{line}  {message}"
            )

    if failures:
        print(f"\n{failures} stale test count(s). Update the documents to the numbers above.")
        return 1

    print("\nEvery documented test count matches the suite.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
