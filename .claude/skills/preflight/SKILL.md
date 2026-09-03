---
name: preflight
description: Run the working agreement's checks in order - the Release build, the tests with a TRX log, the test-count guard and its self-check, the diariser election guard, a parse of every scripts/*.ps1, and a check that the reminder hook still mirrors CLAUDE.md - and report each result exactly as it came out. Invoke before a commit or a handoff.
disable-model-invocation: true
---

# Preflight: every check the agreement names, in order, reported as it came out

CLAUDE.md's **"Building and testing"** section is the source of truth for every command here.
If this file and CLAUDE.md disagree, CLAUDE.md wins and this file is the one to fix. Read that
section first; this skill only sequences it and adds one check nothing else runs.

## Current state

- Branch: !`git branch --show-current`
- Working tree: !`git status --short`

## The rule for reporting

Every step's result goes into the table exactly as it came out — exit code, counts, the first
error. A red step is reported red. Never summarise a failed step as "mostly fine", never skip a
later step because an earlier one failed (the later ones are cheap and independent), and never
quote a count from a document when the run just produced one.

## The steps

On the desktop on 2026-09-03 a warm Release build took 6 s and the suite 16 s; a cold build and
another machine take longer, so give each a ten-minute timeout and run them in the foreground so
the output is captured whole.

1. **Build** — `dotnet build Uindosill.slnx -c Release`. TreatWarningsAsErrors is on, so a
   successful build is a zero-warning build; record the exit code and the warning and error
   counts from the summary lines anyway.
2. **Test** —
   `dotnet test Uindosill.slnx -c Release --no-build --logger "trx;LogFileName=results.trx"`.
   Record total, passed, failed and skipped. The TRX files are what step 3 reads, which is why
   this step runs the suite rather than trusting whatever is on disk (gotcha 28).
3. **Counts** — `python3 scripts/check-test-counts.py --no-run`, then
   `python3 scripts/check-test-counts.py --self-check`. The first holds the documents that quote
   a count to the run that just happened; the second proves the guard's own rules still fire. If
   the first fails it prints what each document must say — report that text; do not edit the
   documents unless asked.
4. **Diariser election** — `python3 scripts/check-diariser-auto.py`. Needs nothing installed.
5. **Scripts parse** — the one-liner in CLAUDE.md's "Building and testing" section, copied from
   there because that is the ground truth; its exit code is the number of parse errors, each
   named.
6. **The hook mirrors the agreement** — CLAUDE.md names paths that owe a check "after any change
   to" them, and `.claude/hooks/gated-test-reminder.sh` has a `case` for each. The documents wrap
   at 100 columns and the phrase itself wraps, so join the lines before grepping:
   ```bash
   tr -s '[:space:]' ' ' < CLAUDE.md | grep -oiE 'after any change to( [a-z]+){0,4} `[^`]+`'
   ```
   (five paths on 2026-09-03). Compare that list with the `case` patterns in the hook; the hook's
   `tests/` and `.ps1` cases answer other sentences in the same section, so check those sentences
   are still there too. A path named in one and not the other is a finding, and the direction says
   which file needs the line.

## The table

| Step | Result | Time |
|---|---|---|
| Build | exit code; warnings and errors, or the first error line | |
| Test | total / passed / failed / skipped | |
| Counts (`--no-run`) | ok, or the sentence it printed | |
| Counts (`--self-check`) | ok, or the rule that did not fire | |
| Diariser election | its last line | |
| Scripts parse | 0 errors, or each named | |
| Hook mirrors CLAUDE.md | matched, or the odd path out | |

Then one line: green throughout, or the list of red rows. What to do about a red row is the
next conversation, not this skill's.

## What this does not do

It does not run the gated tests (FLEURS, Silero, llama-server), which need assets and are named
per path by the reminder hook when such a path is edited; it does not transcribe anything; it does
not push run reports (`/wrap-runs`); and it does not commit.
