#!/usr/bin/env python3
"""Regression checks for Codex edit hooks and their test-count contract.

Run from any directory with Python's standard library. These checks never edit
product files, start models, or run the .NET suite.
"""

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parent.parent


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


hooks = load("edit_hooks", ROOT / ".codex/hooks/edit-hooks.py")
counts = load("test_counts", ROOT / "scripts/check-test-counts.py")


def event(*headers, cwd=ROOT):
    return {"cwd": str(cwd), "tool_name": "apply_patch", "tool_input": {
        "command": "*** Begin Patch\n" + "\n".join(headers) + "\n*** End Patch"}}


class HookChecks(unittest.TestCase):
    def test_all_files_and_both_ends_of_rename_are_checked(self):
        paths = hooks.changed_paths(event(
            "*** Add File: src/new.cs", "*** Update File: src/old.cs",
            "*** Move to: attic/old.cs", "*** Delete File: tests/old.cs"))
        self.assertEqual(len(paths), 4)
        result = hooks.attic_guard(paths)
        self.assertEqual(result["permissionDecision"], "deny")
        self.assertIn("attic", result["permissionDecisionReason"])
        self.assertIn("check-test-counts.py", hooks.gated_reminders(paths)["additionalContext"])

    def test_rename_out_of_attic_still_blocks(self):
        paths = hooks.changed_paths(event("*** Update File: attic/old.cs", "*** Move to: src/live.cs"))
        self.assertEqual(hooks.attic_guard(paths)["permissionDecision"], "deny")

    def test_normalization_catches_parent_segments_from_subdirectory(self):
        paths = hooks.changed_paths(event("*** Update File: ../attic/old.cs", cwd=ROOT / "src"))
        self.assertEqual(paths, [(ROOT / "attic/old.cs").resolve()])
        self.assertIsNotNone(hooks.attic_guard(paths))

    def test_patch_contents_cannot_masquerade_as_file_headers(self):
        paths = hooks.changed_paths(event("*** Update File: README.md", "@@",
                                         "+*** Add File: attic/fiction.cs"))
        self.assertIsNone(hooks.attic_guard(paths))
        self.assertIsNone(hooks.gated_reminders(paths))

    def test_json_escaping_and_crlf_do_not_drop_paths(self):
        payload = event('*** Update File: attic/name "quoted".cs')
        payload["tool_input"]["command"] = payload["tool_input"]["command"].replace("\n", "\r\n")
        result = hooks.attic_guard(hooks.changed_paths(json.loads(json.dumps(payload))))
        self.assertIn('name "quoted".cs', json.loads(json.dumps(result))["permissionDecisionReason"])

    @unittest.skipUnless(os.name == "nt", "Windows path casing")
    def test_windows_paths_are_case_insensitive(self):
        paths = hooks.changed_paths(event("*** Update File: " + str(ROOT / "ATTIC/old.cs")))
        self.assertIsNotNone(hooks.attic_guard(paths))

    def test_single_file_compatibility_ignores_response_file_path(self):
        payload = {"cwd": str(ROOT), "tool_input": {"file_path": "tests/one.cs"},
                   "tool_response": {"file_path": "attic/unrelated.cs"}}
        paths = hooks.changed_paths(payload)
        self.assertIsNone(hooks.attic_guard(paths))
        self.assertIn("AGENTS.md", hooks.gated_reminders(paths)["additionalContext"])

    def test_one_patch_emits_every_gated_obligation(self):
        paths = hooks.changed_paths(event(*["*** Update File: " + p for p in (
            "src/Parakeet.Core/GermanNumberWords.cs", "src/Parakeet.Engine.SileroVad/engine.cs",
            "src/Parakeet.Engine.LlamaServer/engine.cs", "python/uindosill_engines/translator/engine.py",
            "src/Parakeet.App/Views/View.axaml", "src/Parakeet.App/ViewModels/ViewModel.cs",
            "tests/Example.Tests/one.cs", "tests/Example.Tests/two.cs")]))
        context = hooks.gated_reminders(paths)["additionalContext"]
        for obligation in ("UINDOSILL_FLEURS_DIR", "UINDOSILL_SILERO_VAD", "UINDOSILL_LLM_SERVER_ROOT",
                           "parity.check", "Views", "ViewModels", "check-test-counts.py"):
            self.assertIn(obligation, context)
        self.assertEqual(context.count("check-test-counts.py"), 1)

    def test_changed_diariser_and_powershell_run_both_checks(self):
        paths = hooks.changed_paths(event("*** Update File: python/uindosill_engines/diariser/pyannote_engine.py",
                                         "*** Update File: scripts/lab.ps1"))
        with patch.object(hooks, "run_check", return_value="passed (exit 0)") as run, \
                patch.object(hooks.shutil, "which", return_value="pwsh"):
            context = hooks.gated_reminders(paths)["additionalContext"]
        self.assertEqual(run.call_count, 2)
        self.assertIn("check-diariser-auto.py", context)
        self.assertIn("PowerShell parsed", context)

    def test_missing_powershell_emits_manual_obligation(self):
        paths = hooks.changed_paths(event("*** Update File: scripts/lab.ps1"))
        with patch.object(hooks.shutil, "which", return_value=None):
            self.assertIn("parse", hooks.gated_reminders(paths)["additionalContext"])

    def test_unrecognized_event_blocks_instead_of_silently_succeeding(self):
        with patch.object(sys, "argv", ["edit-hooks.py", "PreToolUse"]), \
                patch.object(sys, "stdin", io.StringIO('{"tool_input":{}}')), \
                contextlib.redirect_stderr(io.StringIO()) as error:
            self.assertEqual(hooks.main(), 2)
        self.assertIn("Could not inspect", error.getvalue())

    def test_count_guard_rejects_stale_and_missing_agents_claims(self):
        docs = {"README.md": "7 tests", "CLAUDE.md": "7 tests", "AGENTS.md": "7 tests",
                "PHASES.md": "7 tests, 3 CLI tests, 6 passed and 1 skipped"}
        totals = {"total": 7, "passed": 6, "skipped": 1, "failed": 0}
        with patch.object(counts, "find_trx", return_value=([ROOT / "results.trx"], [])), \
                patch.object(counts, "stale_trx", return_value=[]), \
                patch.object(counts, "project_dirs", return_value=set()), \
                patch.object(counts, "read_counters", return_value=(totals, {"Parakeet.Cli.Tests": 3})), \
                patch.object(Path, "read_text", lambda p, **kw: docs[p.name]), \
                patch.object(sys, "argv", ["check-test-counts.py", "--no-run"]):
            for text, expected in [("7 tests", 0), ("6 tests", 1), ("no count", 1)]:
                docs["AGENTS.md"] = text
                with self.subTest(text=text), contextlib.redirect_stdout(io.StringIO()) as output:
                    self.assertEqual(counts.main(), expected)
                    self.assertIn("AGENTS.md", output.getvalue())


if __name__ == "__main__":
    unittest.main(verbosity=2)
