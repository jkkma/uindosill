#!/usr/bin/env python3
"""Codex edit hooks: JSON in, hookSpecificOutput out; no third-party dependencies.

Codex supplies apply_patch text in tool_input.command.
Read every file header, including both ends of a move. The shell entry points are
compatibility wrappers; hooks.json invokes Python directly on Windows.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
HEADERS = re.compile(r"^\*\*\* (?:Add File|Update File|Delete File|Move to): (.+)$", re.MULTILINE)

# Every AGENTS.md gated path has a rule here. Multiple files share one reminder.
REMINDERS = (
    ("GermanNumberWords", "after changing GermanNumberWords, run the FLEURS-gated test "
     "(UINDOSILL_FLEURS_DIR=<google/fleurs data dir> dotnet test Uindosill.slnx -c Release)."),
    ("Parakeet.Engine.SileroVad", "after changing Silero VAD, run its gated tests "
     "(UINDOSILL_SILERO_VAD=<silero_vad.onnx> dotnet test Uindosill.slnx -c Release) "
     "and drive uindosill transcribe over a real file with the model installed."),
    ("Parakeet.Engine.LlamaServer", "after changing llama-server, run the four gated tests "
     "with UINDOSILL_LLM_SERVER_ROOT and UINDOSILL_LLM_TEST_MODEL set; use "
     "UINDOSILL_LLM_TEST_BACKEND=vulkan or cuda on a machine with that backend."),
    ("uindosill_engines/translator/", "the translator has no CI coverage: for each checkpoint, "
     "drive parity.check directly on CPU and load through uindosill translate --backend webgpu."),
    ("Parakeet.App/Views/", "after changing Views, run "
     "dotnet run --project tools/measure-lines -c Release; every description outside Models "
     "must fit in two lines at both widths."),
    ("Parakeet.App/ViewModels/", "after changing ViewModels, run "
     "dotnet run --project tools/measure-lines -c Release; every description outside Models "
     "must fit in two lines at both widths."),
    ("/tests/", "if tests were added or removed, run python3 scripts/check-test-counts.py "
     "after a test run with a TRX log; README.md, AGENTS.md and docs/PHASES.md "
     "quote the count."),
)


def changed_paths(event: dict) -> list[Path]:
    tool_input = event.get("tool_input", {})
    if not isinstance(tool_input, dict):
        raise ValueError("tool_input must be an object")
    command = tool_input.get("command")
    if command is not None:
        if not isinstance(command, str):
            raise ValueError("tool_input.command must be patch text")
        raw = HEADERS.findall(command.replace("\r\n", "\n"))
        if not raw:
            raise ValueError("patch has no supported file headers")
    elif isinstance(tool_input.get("file_path"), str):
        # Compatibility with tools that still send a single file path.
        raw = [tool_input["file_path"]]
    else:
        raise ValueError("expected tool_input.command or tool_input.file_path")
    cwd = Path(event.get("cwd") or ROOT)
    return list(dict.fromkeys((cwd / path.replace("\\", "/")).resolve() for path in raw))


def normalized(path: Path) -> str:
    text = path.as_posix()
    return text.casefold() if os.name == "nt" else text


def attic_guard(paths: list[Path]) -> dict | None:
    retired = [str(path) for path in paths if "/attic/" in normalized(path)]
    if not retired:
        return None
    return {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": (
            "attic/ holds retired code that nothing builds, tests or ships. "
            "This patch targets: " + ", ".join(retired) + ". Stop and confirm these are "
            "the intended files, rather than their live counterparts. Codex hooks cannot ask "
            "for approval; an intentional attic edit needs explicit user approval before "
            "arranging a scoped exception to this guard."
        ),
    }


def run_check(args: list[str], *, env: dict | None = None) -> str:
    try:
        result = subprocess.run(args, cwd=ROOT, env=env, capture_output=True,
                                text=True, errors="replace", timeout=20)
    except (OSError, subprocess.TimeoutExpired) as exc:
        return f"Check could not finish; run it manually: {exc}"
    status = "passed" if result.returncode == 0 else "FAILED"
    return f"{status} (exit {result.returncode}):\n{result.stdout.strip()}\n{result.stderr.strip()}".strip()


def gated_reminders(paths: list[Path]) -> dict | None:
    names = [normalized(path) for path in paths]
    notes = []
    for fragment, reminder in REMINDERS:
        needle = fragment.casefold() if os.name == "nt" else fragment
        if any(needle in name for name in names):
            notes.append("AGENTS.md: " + reminder)

    if any(name.endswith("/uindosill_engines/diariser/pyannote_engine.py") for name in names):
        notes.append("check-diariser-auto.py after this edit: " +
                     run_check([sys.executable, str(ROOT / "scripts/check-diariser-auto.py")]))

    scripts = [path for path in paths if path.suffix.lower() == ".ps1" and path.is_file()]
    if scripts:
        pwsh = shutil.which("pwsh")
        if not pwsh:
            notes.append("AGENTS.md: pwsh is unavailable; parse the changed PowerShell scripts "
                         "manually with the one-liner in Building and testing.")
        else:
            # Paths travel as JSON in the environment, never as PowerShell source text.
            env = {**os.environ, "HOOK_PS1_PATHS": json.dumps([str(path) for path in scripts])}
            code = (
                "$errorsFound = @(); foreach ($scriptPath in (ConvertFrom-Json $env:HOOK_PS1_PATHS)) { "
                "$t = $err = $null; "
                "[Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$t, [ref]$err) > $null; "
                "$errorsFound += $err }; $errorsFound | ForEach-Object { "
                "'{0}:{1}: {2}' -f $_.Extent.File, $_.Extent.StartLineNumber, $_.Message }; "
                "exit $errorsFound.Count"
            )
            notes.append("PowerShell parsed " + ", ".join(str(path) for path in scripts) +
                         ": " + run_check([pwsh, "-NoProfile", "-NonInteractive", "-Command", code], env=env))
    if not notes:
        return None
    return {"hookEventName": "PostToolUse", "additionalContext": "\n\n".join(notes)}


def main() -> int:
    phase = sys.argv[1]
    try:
        event = json.load(sys.stdin)
        paths = changed_paths(event)
    except (ValueError, TypeError, AttributeError) as exc:
        # Exit 2 blocks PreToolUse and reports a required follow-up for PostToolUse.
        print(f"Could not inspect the edit: {exc}. Repair the hook input before proceeding.", file=sys.stderr)
        return 2
    result = attic_guard(paths) if phase == "PreToolUse" else gated_reminders(paths)
    if result:
        print(json.dumps({"hookSpecificOutput": result}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
