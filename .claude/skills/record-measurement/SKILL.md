---
name: record-measurement
description: Land a finished harness run in the documents - the dated block in docs/UNPROVEN.md, the "Measured" entry and the roadmap row in docs/PHASES.md, the two README rows - with every figure copied from the run's own summary and every real-time factor naming its backend, then hand off to claims-auditor. Invoke once a run's summary.md exists under runs/.
disable-model-invocation: true
---

# Record a measurement in the documents

CLAUDE.md's **"The rule this project runs on"** governs every line this skill writes: every claim
measured or marked unproven, every figure measuring the thing it claims, no real-time factor
without its backend. `/wrap-runs` moves the run reports to the Drive; this skill moves the finding
into the repository's record. It exists because the request-unit measurement of 2026-09-03 landed
in UNPROVEN.md and PHASES.md and missed both roadmap rows — the rows a reader meets first — until
a second commit the same day.

## 1. Find the run and read its record

Newest entries under `runs/`: !`ls -t runs 2>/dev/null | head -8`

Each harness uses its own shape under `runs/` (CLAUDE.md's "Where output goes" lists them). Read
the run's `summary.md` and `summary.json`. The JSON is the ground truth for every number, because
`ConvertTo-Json` is culture-invariant and the markdown once was not (gotcha 42). Establish from the
record, not from memory: what was measured, on which machine, on which backend, over which corpus
or file, on which date, by which script at which commit. If any of those is missing from the
record, that gap is the first thing you write down.

## 2. Say what the finding is, in one sentence

The sentence a reader should take away — the number, its backend, and what it was measured
against. Write it before touching a document. If the run answers a question a document poses
("the open question then"), the sentence names that question. If it leaves a rule needing a
decision, the sentence says "decision owed"; it never takes the decision.

## 3. Land it, in this order

1. **`docs/UNPROVEN.md`, the record.** A dated block in the section the measurement belongs to,
   headed like its neighbours with the date in the heading, numbers copied from the JSON, a
   backend on every RTF. If a claim above it was marked unproven and this run measures it, the
   marker is now stale in the other direction: strike it or annotate it inside the block; do not
   delete it.
2. **`docs/PHASES.md`, the running log.** A `### Measured YYYY-MM-DD — <the sentence from step 2>`
   entry in the phase's log, in the voice of the entries around it: what was measured, what it
   found, what it changes, and what it leaves owed.
3. **`docs/PHASES.md`, the roadmap row.** The table row for that feature carries a *Still owed:*
   clause. Remove what this run paid, add what it created, and point at the new entry with the
   italic dated pointer (`*Measured YYYY-MM-DD*`), which must be a prefix of the heading you just
   wrote.
4. **`README.md`.** Two rows per feature: the roadmap row carries the result and its own owed
   list; the feature-table row carries the result and delegates the owed list to PHASES. Read
   both, change what each one's job requires, and do not paste the PHASES entry into either.
5. **Anything else quoting the superseded figure.** Grep for the old number before finishing. A
   document quoting a number a later measurement replaced is a finding `claims-auditor` will
   raise, so raise it yourself first.

## 4. Hand off

Run the `claims-auditor` agent over the documents you touched — one agent, read-only. If you
added or renamed a heading, run `reference-auditor` after it, not beside it. Report both results
as they came. Then, if the session is ending, `/wrap-runs`.

## What never happens here

No figure is retyped from memory or rounded differently from the record; no decision is taken on
the measurement's behalf; no test count is changed (that is `check-test-counts.py`'s job, after a
test run); and nothing under `runs/` is committed.
