---
name: wrap-runs
description: End-of-measurement-session wrap-up - push new run summaries and research to the maintainer's Drive over rclone, update the Drive README index, and account for anything too big to travel. Invoke after a measuring session.
disable-model-invocation: true
---

# Wrap up a measuring session

CLAUDE.md's **"Where output goes"** section is the source of truth for every rule this
checklist orders. If this file and CLAUDE.md ever disagree, CLAUDE.md wins and this file is the
one that needs fixing. Read that section first; this skill only sequences it.

## 1. Inventory what is new

`runs/` is gitignored and machine-local, so git will not tell you — go by modification time
against when the session started. The harnesses each use their own shape inside `runs/`
(per-timestamp, per-machine, `wer/`, `der/`, `tidy-units/` — CLAUDE.md names the common ones, and
each script's header names its own). Collect the new run summaries; the route carries each run's
`summary.json`, `summary.md` and markdown, nothing else.

## 2. Name the machine

The Drive folder is per machine: `runs-laptop` or `runs-desktop`, inside the `uindosill`
folder, beside the v2 handoff. Say which machine this is before pushing; do not guess from the
path alone if anything looks off.

## 3. Push over rclone, never the connector

Transfers go through `scripts/sync-drive.ps1` (or `lab.ps1 drive`) — rclone with `--checksum`.
The Drive connector is only for finding folders by name and creating them; it never carries
file bodies. Research goes up as markdown, no conversion step.

If the rclone remote is not configured on this machine, **stop and say so.** Setup is a human
step — one browser consent per machine — and `rclone config` output is a credential, so you do
not run it (the maintainer's machines additionally deny it via user-level settings, but the
instruction stands on its own everywhere). Tell the user what to run in their own terminal and
end there.

## 4. What travels and what does not

- Run summaries: always — the route copies each run's `summary.json`, `summary.md` and any
  markdown, and nothing else. Anything larger stays on the machine and goes in the README with
  how to regenerate it.
- Bulk regenerable artifacts — the gigabyte-scale exports and their kind — stay local: list
  them in the Drive folder's README with how to regenerate them instead.
- Byte-exact fixtures: upload a generator validated against the pin, not a copy.
- Where these bullets seem to collide, CLAUDE.md's own paragraph settles it, not this file.

## 5. Update the README index

The `runs-<machine>` folder's README indexes what is there. Add the new entries, and keep its
note current on which working-tree changes are not yet pushed to the repository.

## 6. Session memory, if asked

`lab.ps1 drive -Memory <machine>` pushes this machine's session memory to
`session-memory/<machine>`. Push only — merging is by hand via `-Fetch` into a scratch folder.
Do this only when the user asks; it is not part of every wrap-up.

## 7. The line that must never move

**No Drive URL and no file id goes into this repository.** It is public. Pointers name
folders; the connector finds them by name.
