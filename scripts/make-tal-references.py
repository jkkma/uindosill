#!/usr/bin/env python3
"""Turn the This American Life release into diarisation references this project can score.

TAL (Mao, Li, McAuley & Cottrell 2020, arXiv:2005.08072 — 663 episodes, 637 h) is the only free
podcast corpus with a *human* speaker reference, and it is the corpus the Spotify sparse-optimisation
paper reported its 0.35 DER on. Kaggle ships one JSON per split — `{episode: [turns]}` — and each
turn carries, per the release's own README:

    episode, act, act_title, role, speaker, utterance_start, utterance_end, duration,
    utterance, n_sentences, n_words, alignments, has_q, ends_q

**Everything below was measured on `test-transcripts-aligned.json` (36 episodes, 9,356 turns) on
2026-08-25, not read off the paper.** Four results decide how this script works:

  * **Acts are real and they are the right unit.** 175 acts across the 36 episodes, median 5 per
    episode. **109 hold at most four speakers, 84 of those run 120 s or longer, and those 84 are
    13.57 hours** with a median length of 8.9 minutes. At most two speakers: 39 stretches, 5.45 h.
    That is from the *test split alone* — a twentieth of the corpus.
  * **Whole episodes are useless for the speaker criterion and acts are not.** Speakers per episode:
    median 19, mean 19.4, max 41. Against a four-speaker cap that is the arithmetic that removed
    VoxConverse from the gate. Per act it is a different corpus.
  * **The word alignments do NOT give a silence-tight reference, and that was worth checking rather
    than hoping.** `alignments` is one span per word, and across 54,837 consecutive word pairs
    sampled from six episodes **100.0% touch exactly** — maximum gap 0.00 s. They *tile* the
    utterance, absorbing every pause into a word's span, so summing them (34.68 h) is the same
    total as the utterance spans (34.73 h), a ratio of 1.00. **There is no `only_words`-equivalent
    obtainable from this release.** The convention gap against AMI is real and cannot be closed
    here; `docs/UNPROVEN.md` records the same class of difference costing 13.59 points on identical
    hypotheses.
  * **`utterance_end` is never NaN in the test split** — zero of 9,356, and every turn has
    alignments. `--nan-end` is kept because train and valid are unmeasured, not because this split
    needs it. Whatever it does is counted and printed.

Two more things this reference is not, both structural rather than fixable:

  * **Overlap is unmarked.** Turns are sequential; crosstalk is not annotated. This project's
    headline convention scores overlap, so overlapped speech here reads as one speaker.
  * **Ads are in the audio and not in the transcript.** The TAL authors discarded 38 episodes over
    alignment errors "primarily stemming from inserted advertising". An ad inside a cut is speech
    the reference does not claim, and a diariser is charged false alarm for hearing it. `--max-gap`
    splits a run wherever the transcript goes quiet, on the theory that an unlabelled ad is a hole.

**Licence: the release states "All data is distributed exclusively for the purpose of
non-commercial, research usage."** Audio is not distributed at all and is copyright This American
Life; the annotations are copyright Shuyang Li & Henry Mao 2020. Measuring with it locally is a
research use; nothing derived from it may be redistributed, and that includes the RTTMs this writes.
Output goes under `runs/`, which is gitignored.

    # What the corpus actually holds, before any filtering picks a default for you.
    python3 scripts/make-tal-references.py --data <split>.json --out runs/tal-raw --min-seconds 0 --max-gap 0

    # The usable set: acts of at most four voices, two minutes and up. Four because the diariser of
    # the day was capped there; the cut is kept as it was so the corpus stays the one that was
    # measured, not because the cap still exists.
    python3 scripts/make-tal-references.py --data <split>.json --out runs/tal

    # Two-voice material, the shape the over-segmentation repair needs.
    python3 scripts/make-tal-references.py --data <split>.json --out runs/tal2 --max-speakers 2

Requires nothing but the standard library.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path

# The RTTM line, byte for byte as python/uindosill_engines/diariser/postproc.py writes it. Two
# writers of one format is how they drift, and this is the one the scorer was validated against.
RTTM = "SPEAKER {file_id} 1 {start:.3f} {duration:.3f} <NA> <NA> {speaker} <NA> <NA>\n"

# RTTM is whitespace-delimited and TAL speakers are people's names — "ira glass" is two columns
# unless this runs. Collapsed rather than rejected: the name stays legible, the mapping is reported,
# and no episode is lost to its cast list.
WHITESPACE = re.compile(r"\s+")


def sanitise(speaker: str) -> str:
    """A speaker label RTTM can carry. Empty names become a constant rather than an empty column."""
    collapsed = WHITESPACE.sub("_", speaker.strip())
    return collapsed or "UNKNOWN"


def slice_id(episode: str, index: int) -> str:
    """`ep-441-a`, `ep-441-b`, … as the dev manifest names its stretches, then `-aa` past 26."""
    if index < 26:
        return f"{episode}-{chr(ord('a') + index)}"
    first, second = divmod(index - 26, 26)
    return f"{episode}-{chr(ord('a') + first)}{chr(ord('a') + second)}"


def load_episodes(path: Path) -> dict[str, list[dict]]:
    """Read the release, in whichever shape is on disk.

    Kaggle ships `{test,train,valid}-transcripts-aligned.json`, each one dictionary of episode id →
    list of turns; that is the normal case. A directory of per-episode `.jsonl` is also accepted,
    since the authors' own pipeline works from that shape. A pickle is not accepted: unpickling a
    downloaded file executes it.
    """
    if path.is_file():
        if path.suffix == ".jsonl":
            return {path.stem: read_jsonl(path)}
        loaded = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(loaded, dict):
            raise SystemExit(f"{path} holds a {type(loaded).__name__}, not an episode → turns mapping.")
        return {str(k): list(v) for k, v in loaded.items()}

    if not path.is_dir():
        raise SystemExit(f"No TAL data at {path}.")

    files = sorted(path.rglob("*.jsonl"))
    if files:
        return {f.stem: read_jsonl(f) for f in files}
    merged: dict[str, list[dict]] = {}
    for candidate in sorted(path.glob("*transcripts*.json")):
        merged.update(load_episodes(candidate))
    if not merged:
        raise SystemExit(f"No transcripts under {path}. Point --data at a split JSON or the release directory.")
    return merged


def read_jsonl(path: Path) -> list[dict]:
    out = []
    with path.open(encoding="utf-8") as handle:
        for number_, line in enumerate(handle, 1):
            line = line.strip()
            if not line:
                continue
            try:
                out.append(json.loads(line))
            except json.JSONDecodeError as exc:
                raise SystemExit(f"{path}:{number_}: {exc}") from exc
    return out


def number(value) -> float | None:
    """A finite float, or None for anything that cannot be one — NaN included, which is the point."""
    if value is None:
        return None
    try:
        f = float(value)
    except (TypeError, ValueError):
        return None
    return None if math.isnan(f) or math.isinf(f) else f


def clean(turns: list[dict], nan_end: str) -> tuple[list[dict], dict[str, int]]:
    """Drop what cannot be a turn, resolve missing ends, and count every intervention.

    The counts are returned rather than logged away because each one changes the reference: a
    dropped turn is speech the reference no longer claims, and a diariser that hears it is charged
    false alarm for being right.
    """
    stats = {"read": len(turns), "noStart": 0, "nanEnd": 0, "inferred": 0, "dropped": 0, "nonPositive": 0}
    staged = []

    for raw in turns:
        start = number(raw.get("utterance_start"))
        end = number(raw.get("utterance_end"))
        if start is None:
            stats["noStart"] += 1
            continue
        if end is None:
            stats["nanEnd"] += 1
            if nan_end == "drop":
                stats["dropped"] += 1
                continue
        staged.append({
            "start": start,
            "end": end,
            "speaker": str(raw.get("speaker") or "").strip(),
            "role": str(raw.get("role") or "").strip(),
            "act": str(raw.get("act") or "").strip(),
            "actTitle": str(raw.get("act_title") or "").strip(),
            "duration": number(raw.get("duration")),
        })

    staged.sort(key=lambda t: (t["start"], t["end"] if t["end"] is not None else t["start"]))

    # `infer`: a turn with no end runs to the next one's start. Never past it, and never backwards —
    # a nominal duration is still a fabrication, and a fabricated turn overlapping its neighbour
    # would be scored as crosstalk nobody annotated.
    if nan_end == "infer":
        for i, turn in enumerate(staged):
            if turn["end"] is not None:
                continue
            following = staged[i + 1]["start"] if i + 1 < len(staged) else None
            end = following if following is not None else (
                turn["start"] + turn["duration"] if turn["duration"] else None)
            if end is not None and end > turn["start"]:
                turn["end"] = end
                stats["inferred"] += 1

    out = []
    for turn in staged:
        if turn["end"] is None:
            stats["dropped"] += 1
        elif turn["end"] <= turn["start"]:
            stats["nonPositive"] += 1
        else:
            out.append(turn)

    return out, stats


def carve(turns: list[dict], mode: str, max_speakers: int, max_gap: float) -> list[list[dict]]:
    """Candidate runs, either the episode's own acts or maximal runs inside the speaker cap.

    `acts` is the default and is what the measurement supports: the release carries an `act` field,
    TAL episodes hold a median of five, and 109 of the test split's 175 acts already sit inside a
    four-speaker cap. `speakers` ignores act structure and cuts maximal contiguous runs instead —
    greedy, left to right, the offending turn opening the next run — which is what to reach for on
    material with no act field at all.

    `max_gap` applies in both: a run splits wherever the transcript goes quiet for longer, which is
    what an unlabelled ad looks like from here.
    """
    runs: list[list[dict]] = []
    current: list[dict] = []
    voices: set[str] = set()
    last_end = None
    act = None

    for turn in turns:
        breaks = False
        if current:
            if max_gap > 0 and last_end is not None and turn["start"] - last_end > max_gap:
                breaks = True
            elif mode == "acts":
                breaks = turn["act"] != act
            elif turn["speaker"] not in voices and len(voices) >= max_speakers:
                breaks = True

        if breaks:
            runs.append(current)
            current, voices, last_end = [], set(), None

        current.append(turn)
        voices.add(turn["speaker"])
        act = turn["act"]
        last_end = turn["end"] if last_end is None else max(last_end, turn["end"])

    if current:
        runs.append(current)
    return runs


def union_seconds(spans: list[tuple[float, float]]) -> float:
    total, cursor = 0.0, None
    for start, end in sorted(spans):
        if cursor is None or start > cursor:
            total += end - start
            cursor = end
        elif end > cursor:
            total += end - cursor
            cursor = end
    return total


def describe(run: list[dict]) -> dict:
    """The facts about one run that decide whether it is worth cutting."""
    spans = [(t["start"], t["end"]) for t in run]
    speech = sum(end - start for start, end in spans)
    shares: dict[str, float] = {}
    roles: dict[str, str] = {}
    for turn in run:
        shares[turn["speaker"]] = shares.get(turn["speaker"], 0.0) + turn["end"] - turn["start"]
        roles.setdefault(turn["speaker"], turn["role"])
    return {
        "onset": run[0]["start"],
        "end": max(end for _, end in spans),
        "speakers": sorted(shares),
        "turns": len(run),
        "speechSeconds": speech,
        # Sum minus union. With sequential turns this should be near zero; whatever it is, it is what
        # the *alignment* produced and not annotated crosstalk, which this release does not carry.
        "apparentOverlapSeconds": max(0.0, speech - union_seconds(spans)),
        "shares": shares,
        "roles": roles,
        "act": run[0]["act"],
        "actTitle": run[0]["actTitle"],
    }


def write_rttm(run: list[dict], file_id: str, onset: float, path: Path) -> None:
    """Times rebased to the cut. A reference on the episode's clock scores a cut file as noise."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        for turn in run:
            handle.write(RTTM.format(
                file_id=file_id,
                start=max(0.0, turn["start"] - onset),
                duration=turn["end"] - turn["start"],
                speaker=sanitise(turn["speaker"]),
            ))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data", required=True, type=Path,
                        help="A split JSON (test/train/valid-transcripts-aligned.json), the release directory, or a directory of per-episode .jsonl.")
    parser.add_argument("--out", type=Path, default=Path("runs/tal"),
                        help="Where references and the manifest land. Default runs/tal, gitignored.")
    parser.add_argument("--slice-by", choices=("acts", "speakers"), default="acts",
                        help="Cut on the episode's own act boundaries (default), or on maximal runs inside the speaker cap.")
    parser.add_argument("--max-speakers", type=int, default=4,
                        help="Most distinct voices a stretch may hold. Default 4 — which was the diariser's "
                             "cap until 2026-08-27 and is now only the number this corpus was cut at.")
    parser.add_argument("--min-seconds", type=float, default=120.0,
                        help="Shortest stretch kept. Default 120.")
    parser.add_argument("--max-seconds", type=float, default=0.0,
                        help="Longest stretch kept, 0 for no limit. Default 0 — the duration effect is the thing under test.")
    parser.add_argument("--max-gap", type=float, default=20.0,
                        help="Split a run where the transcript goes quiet longer than this. Default 20; 0 disables. See the docstring on ads.")
    parser.add_argument("--nan-end", choices=("drop", "infer"), default="drop",
                        help="What to do with a turn whose utterance_end is NaN. Default drop. Zero occurrences in the test split.")
    parser.add_argument("--whole-episode", action="store_true",
                        help="Also write one reference per whole episode, unsliced, under rttm-episodes/.")
    parser.add_argument("--episodes", default="",
                        help="Comma-separated episode ids to keep. Default all.")
    # What you download from TAL is an MP3, and the dev manifest pins `.mp3` episodes too. The
    # paper's 16 kHz WAVs are a preprocessing step of theirs, not something the release ships.
    parser.add_argument("--episode-ext", default=".mp3",
                        help="Extension the manifest gives episode audio. Default .mp3, what TAL distributes.")
    args = parser.parse_args(argv)

    wanted = {e.strip() for e in args.episodes.split(",") if e.strip()}
    episodes = load_episodes(args.data)
    if wanted:
        missing = wanted - set(episodes)
        if missing:
            raise SystemExit(f"Not in the release: {', '.join(sorted(missing))}")
        episodes = {k: v for k, v in episodes.items() if k in wanted}

    out = args.out
    (out / "rttm").mkdir(parents=True, exist_ok=True)

    stretches, per_episode, renamed = [], [], {}
    totals = {"read": 0, "noStart": 0, "nanEnd": 0, "inferred": 0, "dropped": 0, "nonPositive": 0}

    for episode in sorted(episodes):
        turns, stats = clean(episodes[episode], args.nan_end)
        for key in totals:
            totals[key] += stats[key]

        for turn in turns:
            safe = sanitise(turn["speaker"])
            if safe != turn["speaker"]:
                renamed[turn["speaker"]] = safe

        cast = {t["speaker"] for t in turns}
        kept = 0

        if args.whole_episode and turns:
            write_rttm(turns, episode, 0.0, out / "rttm-episodes" / f"{episode}.rttm")

        for run in carve(turns, args.slice_by, args.max_speakers, args.max_gap):
            facts = describe(run)
            duration = facts["end"] - facts["onset"]
            if len(facts["speakers"]) > args.max_speakers:
                continue
            if duration < args.min_seconds:
                continue
            if args.max_seconds > 0 and duration > args.max_seconds:
                continue

            identifier = slice_id(episode, kept)
            kept += 1
            write_rttm(run, identifier, facts["onset"], out / "rttm" / f"{identifier}.rttm")

            stretches.append({
                "id": identifier,
                "episode": f"{episode}{args.episode_ext}",
                "onsetSeconds": round(facts["onset"], 3),
                "durationSeconds": round(duration, 3),
                "nominalVoices": len(facts["speakers"]),
                "labelled": True,
                "reference": f"rttm/{identifier}.rttm",
                "act": facts["act"],
                "actTitle": facts["actTitle"],
                "speakers": [sanitise(s) for s in facts["speakers"]],
                "roles": {sanitise(k): v for k, v in sorted(facts["roles"].items()) if v},
                "turns": facts["turns"],
                "speechSeconds": round(facts["speechSeconds"], 3),
                "apparentOverlapSeconds": round(facts["apparentOverlapSeconds"], 3),
                "shares": {sanitise(k): round(v, 1) for k, v in sorted(
                    facts["shares"].items(), key=lambda kv: -kv[1])},
                "why": (f"{len(facts['speakers'])} reference voices over {duration / 60:.1f} min, "
                        f"{facts['turns']} turns, "
                        f"{facts['speechSeconds'] / max(duration, 1e-9) * 100:.0f}% speech."),
            })

        per_episode.append({
            "episode": episode,
            "turns": len(turns),
            "castSize": len(cast),
            "stretchesKept": kept,
            "cleaning": stats,
        })

    manifest = {
        "schema": 1,
        "comment": [
            "This American Life diarisation references, generated by scripts/make-tal-references.py.",
            "Times in each RTTM are rebased to that stretch's onsetSeconds, so a cut WAV scores directly.",
            "",
            "NON-COMMERCIAL RESEARCH USE ONLY, per the release's own README. Audio is copyright This",
            "American Life and is not distributed; annotations are copyright Shuyang Li & Henry Mao 2020.",
            "Nothing derived from this may be redistributed, these RTTMs included.",
            "",
            "The reference spans whole utterances and does not annotate overlap. The release's word",
            "`alignments` do not help: they tile the utterance rather than excluding silence (measured",
            "2026-08-25 -- 100.0% of consecutive word pairs touch exactly). A DER taken here is NOT",
            "comparable to any AMI figure in this repository; docs/UNPROVEN.md records the same class of",
            "convention gap costing 13.59 points on identical hypotheses.",
            "",
            "Fetch the episode MP3s yourself (download_page_snapshot.html in the release has the links),",
            "then cut with the ffmpeg line below. Nothing under runs/ is an input to anything.",
        ],
        "ffmpeg": {
            "line": "ffmpeg -ss <onsetSeconds> -t <durationSeconds> -i <episode> -ac 1 -ar 16000 -c:a pcm_s16le <id>.wav",
            "note": ("Re-encode, never -c copy: with -ss before -i, ffmpeg seeks sample-accurately only when "
                     "transcoding. No digests are pinned here because no audio was read -- measure-der.ps1 -Cut "
                     "treats an unpinned stretch as one being added and prints the pin to paste back."),
        },
        "settings": {
            "sliceBy": args.slice_by,
            "maxSpeakers": args.max_speakers,
            "minSeconds": args.min_seconds,
            "maxSeconds": args.max_seconds,
            "maxGapSeconds": args.max_gap,
            "nanEnd": args.nan_end,
        },
        "cleaning": totals,
        # NOT `episodes`. `scripts/measure-der.ps1 -Cut` reads a top-level `episodes` as a dict keyed
        # by audio filename holding the byte size it verifies a source against, and nothing here has
        # read a byte of audio, so that key is deliberately absent. Reusing the name for a per-episode
        # statistics array would have been silently accepted by its guarded lookup and then meant two
        # different things in one manifest.
        "episodeStats": per_episode,
        "stretches": stretches,
    }
    (out / "stretches.json").write_text(
        json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")

    by_voices: dict[int, int] = {}
    for stretch in stretches:
        by_voices[stretch["nominalVoices"]] = by_voices.get(stretch["nominalVoices"], 0) + 1
    hours = sum(s["durationSeconds"] for s in stretches) / 3600
    lengths = sorted(s["durationSeconds"] / 60 for s in stretches)

    lines = [
        "# TAL diarisation references",
        "",
        f"- episodes read: **{len(episodes)}**",
        f"- sliced by: **{args.slice_by}**",
        f"- stretches kept: **{len(stretches)}** ({hours:.2f} h) at most {args.max_speakers} voices, "
        f"at least {args.min_seconds:.0f} s",
        "- by reference speaker count: " + (
            ", ".join(f"**{v}** voices x{by_voices[v]}" for v in sorted(by_voices))
            if by_voices else "none kept"),
    ]
    if lengths:
        lines.append(f"- stretch length: min {lengths[0]:.1f}, median {lengths[len(lengths) // 2]:.1f}, "
                     f"max {lengths[-1]:.1f} min")

    # The single most important number on this page. TAL turns tile: a stretch's turns typically
    # cover every instant of it, so the reference claims the whole cut is speech and a diariser that
    # correctly finds silence is charged missed speech for it. Measured on the test split, the median
    # stretch claims 100.0% speech. Printed on every run so no DER is ever read without it.
    coverage = sorted(s["speechSeconds"] / max(s["durationSeconds"], 1e-9) for s in stretches)
    if coverage:
        saturated = sum(1 for c in coverage if c > 0.999)
        lines += [
            f"- reference speech coverage: min {coverage[0] * 100:.1f}%, "
            f"median {coverage[len(coverage) // 2] * 100:.1f}%, "
            f"**{saturated} of {len(coverage)} stretches claim >99.9% speech**",
        ]
    lines += [
        "",
        "## Cleaning",
        "",
        f"- turns read: {totals['read']}",
        f"- no `utterance_start`: {totals['noStart']}",
        f"- `utterance_end` NaN: {totals['nanEnd']} (`--nan-end {args.nan_end}`; inferred {totals['inferred']})",
        f"- dropped: {totals['dropped']}; non-positive duration: {totals['nonPositive']}",
        "",
        "Every line above changes the reference. A dropped turn is speech the reference no longer",
        "claims, and a diariser that hears it is charged false alarm for being right.",
        "",
        "## What this cannot be used for",
        "",
        "**Counts, not DER.** The turns tile, so the reference claims nearly the whole cut is speech;",
        "a diariser that correctly reports silence is charged missed speech for it, and the metric",
        "rewards calling everything speech. Add no overlap annotation, whole-utterance spans, and word",
        "`alignments` that tile rather than exclude silence, and a DER here is not comparable to any",
        "AMI figure in this repository. The speaker *sets* carry none of those problems.",
        "",
        "Non-commercial research use only, and nothing here may be redistributed.",
    ]
    if renamed:
        lines += ["", "## Speaker labels collapsed for RTTM", ""]
        lines += [f"- `{k}` → `{v}`" for k, v in sorted(renamed.items())][:40]
    (out / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"episodes {len(episodes)}  stretches {len(stretches)}  {hours:.2f} h  (by {args.slice_by})")
    print(f"  cleaning: read {totals['read']}, NaN ends {totals['nanEnd']}, dropped {totals['dropped']}")
    # Plain ASCII on purpose: this prints to a Windows console that is not always UTF-8, and a
    # mangled multiplication sign in a run's own log is a small lie about what the run reported.
    if by_voices:
        print("  voices: " + ", ".join(f"{v}x{by_voices[v]}" for v in sorted(by_voices)))
    print(f"  wrote {out / 'stretches.json'} and {out / 'summary.md'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
