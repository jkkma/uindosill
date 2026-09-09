#!/bin/sh
# Compatibility entry point; all AGENTS.md reminder rules live in edit-hooks.py.
root=$(git rev-parse --show-toplevel) || exit 2
exec python3 "$root/.codex/hooks/edit-hooks.py" PostToolUse
