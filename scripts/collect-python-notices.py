#!/usr/bin/env python3
"""Assemble the bundled Python's third-party notices from an assembled bundle.

The diariser and the translator run in an interpreter the installer carries, so the wheels they
import are not dependencies but files a recipient receives. That turns fifty distributions into a
notice obligation, and this script is how it is discharged: it reads the *installed* metadata out
of a real bundle and writes the result into `NOTICE.md` between two markers. Nothing here is
recalled — every licence name and every path printed was read off the bundle it was run against.

The obligation is mostly discharged by construction, which is worth knowing before reading the
table. `pip install --target` keeps each wheel's `.dist-info`, `scripts/bundle-python.ps1` prunes
only `__pycache__` under the engines directory, and `scripts/package-windows.ps1` puts the tree in
the publish whole — so the licence texts already travel *inside the product*. What this script adds
is the index saying so, and the names of the distributions whose upstream wheel carries no licence
text at all, which is the part a reader cannot check for themselves.

Usage:

    python3 scripts/collect-python-notices.py --bundle <path to an assembled bundle>
    python3 scripts/collect-python-notices.py --bundle <path> --check

A bundle is what `scripts/bundle-python.ps1 -Destination <dir>` produces, or an installed copy at
`%LOCALAPPDATA%\\Uindosill\\python`. `--check` writes nothing and exits non-zero when the section in
`NOTICE.md` no longer matches the bundle, which is what makes this a guard rather than a one-off:
re-running it after a pin changes must be a no-op, and when it is not, the document is wrong.

Two rules are enforced rather than reported, because a notice that quietly degrades is worse than
one that is missing:

  * A distribution with no licence text anywhere in the bundle fails the run unless it is named in
    KNOWN_TEXTLESS below with the reason. Upstream omitting a licence file is upstream's business;
    this project not noticing is not.
  * A name in KNOWN_TEXTLESS that *does* now ship a text also fails, so the exception list shrinks
    when upstream fixes something instead of outliving the problem.
"""

from __future__ import annotations

import argparse
import email
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NOTICE = ROOT / "NOTICE.md"

BEGIN = "<!-- BEGIN bundled-python-notices -->"
END = "<!-- END bundled-python-notices -->"

# Distributions whose upstream wheel ships no licence text anywhere in the bundle — not in the
# dist-info, not in the package directory. Checked by walking the tree, not by recollection. Each
# entry records what the metadata claims instead, because that claim is all a recipient gets.
#
# All three are Apache-2.0 by their own metadata, and the Apache-2.0 text does travel in this
# bundle several times over (`onnx`, `optimum`, `transformers` and others carry it), so a recipient
# has the licence; what they do not get is a per-package copy attached to these three.
KNOWN_TEXTLESS = {
    "flatbuffers": "METADATA says `Apache 2.0`; the wheel carries no licence file.",
    "sentencepiece": "METADATA says `Apache-2.0`; the wheel carries no licence file.",
    "tokenizers": "Classifier says `Apache Software License`; the wheel carries no licence file.",
    # Added 2026-08-26 with the second diariser's stack. Neither is Apache, which is why the
    # paragraph this list feeds no longer says they all are.
    "antlr4-python3-runtime": (
        "METADATA says `BSD` with no version; the sdist carries no licence file. Built from source "
        "because it publishes no wheel — see the allowlist in `scripts/bundle-python.ps1`. Reached "
        "through `omegaconf`, which pins `==4.9.*`."
    ),
    "primePy": (
        "**The weakest provenance in the bundle.** METADATA's `License` field is the literal "
        "`UNKNOWN` and only a trove classifier claims MIT; the wheel carries no licence file. "
        "Reached transitively through `torch-pitch-shift` under `torch-audiomentations`."
    ),
}

# Files that look like a licence when they are sitting at a dist-info root. PEP 639 wheels put the
# real ones under `licenses/` instead, and both shapes are walked.
LICENCE_NAMES = ("LICENSE", "LICENCE", "COPYING", "NOTICE", "AUTHORS", "COPYRIGHT")


def read_metadata(dist: Path) -> email.message.Message:
    text = (dist / "METADATA").read_text(encoding="utf-8", errors="replace")
    return email.message_from_string(text)


def licence_label(meta: email.message.Message) -> str:
    """The licence as the package states it, preferring the field that is meant to be machine-read.

    PEP 639's `License-Expression` is an SPDX expression and is authoritative where present. The
    legacy `License` field is free text and sometimes holds an entire licence, so only its first
    line is taken and only when it is short enough to be a label rather than a document. The
    classifiers are the last resort, and they are a category rather than an identifier — which is
    why a classifier-only row reads `Apache Software License` and not `Apache-2.0`.
    """
    expression = meta.get("License-Expression")
    if expression:
        return expression.strip()

    legacy = meta.get("License")
    if legacy:
        first = legacy.strip().splitlines()[0].strip()
        if first and len(first) <= 60:
            return first

    classifiers = [c for c in meta.get_all("Classifier", []) if c.startswith("License ::")]
    if classifiers:
        # "License :: OSI Approved :: MIT License" -> "MIT License"
        return " / ".join(c.split("::")[-1].strip() for c in classifiers)

    return "(none stated)"


def licence_files(dist: Path, site: Path) -> list[Path]:
    """Every licence or notice file that ships for this distribution, dist-info first.

    Falls back to the installed package directory, because a wheel may ship its licence beside its
    code rather than in the metadata — `onnxruntime` does exactly that, and counting it as textless
    on the strength of an empty dist-info would report a discharged obligation as an open one.
    """
    found: list[Path] = []

    for path in sorted(dist.rglob("*")):
        if not path.is_file():
            continue
        if path.parent.name == "licenses" or "licenses" in path.relative_to(dist).parts[:-1]:
            found.append(path)
        elif any(path.name.upper().startswith(n) for n in LICENCE_NAMES):
            found.append(path)

    if found:
        return found

    for package in top_level_dirs(dist, site):
        for path in sorted(package.rglob("*")):
            if path.is_file() and any(path.name.upper().startswith(n) for n in LICENCE_NAMES):
                found.append(path)

    return found


def top_level_dirs(dist: Path, site: Path) -> list[Path]:
    """The installed directories this distribution owns, from `top_level.txt` or the RECORD."""
    names: set[str] = set()

    top_level = dist / "top_level.txt"
    if top_level.exists():
        names |= {n.strip() for n in top_level.read_text(encoding="utf-8").splitlines() if n.strip()}

    record = dist / "RECORD"
    if not names and record.exists():
        for line in record.read_text(encoding="utf-8", errors="replace").splitlines():
            head = line.split(",", 1)[0]
            first = head.split("/", 1)[0]
            if first and not first.endswith(".dist-info"):
                names.add(first)

    return [site / n for n in sorted(names) if (site / n).is_dir()]


def collect(bundle: Path) -> tuple[list[dict], list[str]]:
    site = bundle / "Lib" / "site-packages"
    if not site.is_dir():
        sys.exit(f"error: {site} is not a directory. Point --bundle at an assembled bundle.")

    rows: list[dict] = []
    problems: list[str] = []

    for dist in sorted(site.glob("*.dist-info"), key=lambda p: p.name.lower()):
        if not (dist / "METADATA").exists():
            problems.append(f"{dist.name} has no METADATA")
            continue

        meta = read_metadata(dist)
        name = meta.get("Name", dist.name)
        files = licence_files(dist, site)

        if files:
            shown = sorted(f.relative_to(site).as_posix() for f in files)
            # torch alone ships over a hundred third-party texts; naming the first and counting the
            # rest keeps the row readable without pretending the others are not there.
            where = f"`{shown[0]}`" + (f" and {len(shown) - 1} more" if len(shown) > 1 else "")
            if name in KNOWN_TEXTLESS:
                problems.append(
                    f"{name} is listed in KNOWN_TEXTLESS but now ships {shown[0]} — "
                    f"remove it from the exception list."
                )
        elif name in KNOWN_TEXTLESS:
            where = f"**none ships** — {KNOWN_TEXTLESS[name]}"
        else:
            where = "**none ships**"
            problems.append(
                f"{name} ships no licence text anywhere in the bundle and is not in "
                f"KNOWN_TEXTLESS. Either vendor its text or record the gap with its reason."
            )

        rows.append(
            {
                "name": name,
                "version": meta.get("Version", "?"),
                "licence": licence_label(meta),
                "where": where,
                "count": len(files),
            }
        )

    return rows, problems


def render(rows: list[dict], bundle: Path) -> str:
    total_files = sum(r["count"] for r in rows)
    textless = [r["name"] for r in rows if r["count"] == 0]

    lines = [
        BEGIN,
        "",
        f"**{len(rows)} distributions, read off an assembled bundle** by "
        "`scripts/collect-python-notices.py`, which is what keeps this list from being a"
        " recollection. Every licence below is the one the installed package states in its own"
        " `METADATA` — PEP 639's `License-Expression` where the wheel has one, the legacy `License`"
        " field or the classifier where it does not, which is why some rows read as an SPDX"
        " expression and others as a category.",
        "",
        f"**The texts themselves already travel with the product.** `pip install --target` keeps"
        f" each wheel's `.dist-info`, the bundling script prunes only `__pycache__`, and the"
        f" packaging script copies the tree whole — so {total_files:,} licence and notice files ship"
        " inside the interpreter directory. The paths below are relative to"
        " `python/Lib/site-packages` in an installed copy.",
        "",
        "| Distribution | Version | Licence, as the package states it | Text that ships |",
        "|---|---|---|---|",
    ]

    for r in rows:
        lines.append(f"| {r['name']} | {r['version']} | {r['licence']} | {r['where']} |")

    lines += [
        "",
        f"**{len(textless)} of the {len(rows)} ship no licence text of their own**: "
        + ", ".join(f"`{n}`" for n in textless)
        + ". What each claims instead is in `KNOWN_TEXTLESS` in the script that writes this, with"
        " the route by which it arrives. Most name Apache, and the Apache-2.0 text travels in this"
        " bundle several times over — `onnx`, `optimum` and `transformers` each carry a copy — so"
        " for those a recipient has the licence even without one attached. **That is not true of"
        " all of them**: `antlr4-python3-runtime` says only `BSD`, which names a family rather than"
        " one of two licences that differ by a clause, and `primePy`'s `License` field is the"
        " literal `UNKNOWN` with a classifier alone claiming MIT. Upstream's omission in every"
        " case, recorded rather than papered over.",
        "",
        "**Four of these are not simply permissive, and they are the rows to read twice.**"
        " `soxr` is LGPL-2.1-or-later and its wheel bundles libsoxr and PFFFT; `soundfile` carries"
        " an LGPL-2.1 `libsndfile` whose `COPYING` ships at `_soundfile_data/COPYING`, which the"
        " table does not show because it belongs to no `.dist-info`; and `certifi` and `tqdm` are"
        " MPL-2.0, file-level copyleft. `docs/LICENSING.md` records what each obliges.",
        "",
        "Re-running against a bundle built from the same pins must produce this section unchanged;"
        " `--check` is what holds it, and a changed pin is expected to change this table. The"
        " bundle's own location is deliberately not printed — it is a path on whoever ran the"
        " script's machine, this repository is public, and a guard that embeds one fails on every"
        " other machine for a reason that has nothing to do with notices.",
        "",
        END,
    ]

    return "\n".join(lines)


def read_notice() -> tuple[str, str]:
    """NOTICE.md with newlines normalised, plus the ending it is actually stored with.

    Read and compared in `\\n` throughout, then written back in whatever the file already used.
    `.gitattributes` leaves this file to `core.autocrlf`, so a checkout here is CRLF and a
    generator that writes `\\n` would rewrite all 300 lines as a side effect of updating a table —
    a diff that hides the change it was run to make.
    """
    raw = NOTICE.read_bytes().decode("utf-8")
    ending = "\r\n" if "\r\n" in raw else "\n"
    return raw.replace("\r\n", "\n"), ending


def splice(section: str, text: str) -> str:
    pattern = re.compile(re.escape(BEGIN) + r".*?" + re.escape(END), re.DOTALL)
    if not pattern.search(text):
        sys.exit(
            f"error: NOTICE.md has no {BEGIN} / {END} pair. Add the markers where the section "
            f"should live; this script will not guess a location."
        )
    return pattern.sub(lambda _: section, text)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--bundle",
        type=Path,
        default=Path(os.environ.get("UINDOSILL_PYTHON_BUNDLE", "")) or None,
        help="an assembled bundle (or set UINDOSILL_PYTHON_BUNDLE)",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="write nothing; fail if NOTICE.md no longer matches the bundle",
    )
    args = parser.parse_args()

    if not args.bundle:
        sys.exit(
            "error: no bundle given. Pass --bundle <dir>, or set UINDOSILL_PYTHON_BUNDLE. "
            "A bundle is what scripts/bundle-python.ps1 produces, or an installed copy at "
            "%LOCALAPPDATA%\\Uindosill\\python."
        )

    rows, problems = collect(args.bundle)

    for problem in problems:
        print(f"::error::{problem}")

    current, ending = read_notice()
    section = render(rows, args.bundle)
    updated = splice(section, current)

    if args.check:
        if updated != current:
            print("::error::NOTICE.md's bundled-Python section does not match the bundle. "
                  "Re-run scripts/collect-python-notices.py without --check.")
            return 1
        print(f"NOTICE.md matches the bundle: {len(rows)} distributions.")
        return 1 if problems else 0

    if updated != current:
        NOTICE.write_bytes(updated.replace("\n", ending).encode("utf-8"))
        print(f"NOTICE.md updated: {len(rows)} distributions, "
              f"{sum(r['count'] for r in rows):,} licence files counted.")
    else:
        print(f"NOTICE.md already matches: {len(rows)} distributions.")

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
