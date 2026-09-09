#!/bin/sh
# Compatibility entry point; hooks.json runs the shared Python parser directly.
root=$(git rev-parse --show-toplevel) || exit 2
exec python3 "$root/.codex/hooks/edit-hooks.py" PreToolUse
