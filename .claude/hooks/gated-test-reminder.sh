#!/bin/sh
# PostToolUse hook (Edit|MultiEdit|Write): the working agreement ties a handful of paths to
# checks CI cannot run, or runs only after a push, and the tests tree to a count three
# documents quote. Those rules exist because they get forgotten; this prints the matching
# reminder into context when such a path is touched - and where the check itself is cheap
# and needs nothing installed, it runs the check and reports the result instead of asking:
#
#   - an edit to the diariser's election file runs scripts/check-diariser-auto.py, the same
#     script CI runs, which needs no torch and no model and finishes in well under a second;
#   - an edit to any .ps1 parses that one file with PowerShell's own parser, because nothing
#     in CI or the suite parses scripts/*.ps1 (the CLAUDE.md one-liner still parses them all).
#
# Plain sh with cat/grep/sed/tr/head for the routing, because it must behave identically
# under Git Bash on Windows and sh on the Linux cloud runner. The two checks need python3
# and pwsh respectively; when one is not on PATH the hook falls back to the reminder.
#
# Stdin is the hook event JSON. Plain reminders are spliced into JSON verbatim, so their
# text must stay free of double quotes and backslashes. A check's output is arbitrary text,
# so when a check ran the whole payload is JSON-encoded by python3 instead; without python3
# the output is stripped of those two characters and spliced like a reminder.

input=$(cat)

# grep -o so the FIRST "file_path" wins (tool_input's) whatever comes after it in the event;
# a greedy sed across the whole line would take the last one instead.
path=$(printf '%s' "$input" \
  | grep -o '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | head -n 1 \
  | sed 's/^"file_path"[[:space:]]*:[[:space:]]*"//; s/"$//')
[ -n "$path" ] || exit 0

# Windows paths arrive JSON-escaped (C:\\Users\\...); fold every backslash run into one
# forward slash so the patterns below need a single spelling. pwsh accepts the result too.
p=$(printf '%s' "$path" | tr '\\' '/' | tr -s '/')

# The repository root, for the scripts the checks run. Claude Code sets it for every hook;
# the working directory is the fallback.
root=$(printf '%s' "${CLAUDE_PROJECT_DIR:-.}" | tr '\\' '/' | tr -s '/')

notes=''
add() { notes="$notes$1 "; }
checks=''
ran() { checks="$checks$1

"; }

case "$p" in
  *GermanNumberWords*) add "CLAUDE.md: after any change to GermanNumberWords, run the FLEURS-gated test (UINDOSILL_FLEURS_DIR=<google/fleurs data dir> dotnet test Uindosill.slnx -c Release) - it is the only check that the German number rewrite stays a no-op on written text." ;;
esac
case "$p" in
  *Parakeet.Engine.SileroVad*) add "CLAUDE.md: after any change to the Silero VAD engine, run the gated tests (UINDOSILL_SILERO_VAD=<path to silero_vad.onnx> dotnet test Uindosill.slnx -c Release) and drive 'uindosill transcribe' over a real file with the model installed - nothing else in the suite runs the graph." ;;
esac
case "$p" in
  *Parakeet.Engine.LlamaServer*) add "CLAUDE.md: after any change to the llama-server engine, run the four gated end-to-end tests (UINDOSILL_LLM_SERVER_ROOT and UINDOSILL_LLM_TEST_MODEL set; add UINDOSILL_LLM_TEST_BACKEND=vulkan or cuda on a machine that has one - the only place child-process argument changes are really tested)." ;;
esac
case "$p" in
  *uindosill_engines/translator/*) add "CLAUDE.md: the translator has no CI coverage - drive the sidecar parity fixture by hand after this change: one load on CPU and one on webgpu, each reporting parity." ;;
esac
case "$p" in
  */tests/*|tests/*) add "If this change added or removed tests, run python3 scripts/check-test-counts.py after the next test run - three documents quote the count and CI fails on a stale one." ;;
esac

# The diariser's election is pure logic over two filesystem checks, guarded by a script that
# needs nothing installed - so it runs here rather than being asked for.
case "$p" in
  *uindosill_engines/diariser/pyannote_engine.py)
    if command -v python3 >/dev/null 2>&1 && [ -f "$root/scripts/check-diariser-auto.py" ]; then
      out=$(cd "$root" && python3 scripts/check-diariser-auto.py 2>&1)
      rc=$?
      if [ "$rc" -eq 0 ]; then
        ran "check-diariser-auto.py ran after this edit to pyannote_engine.py and passed (exit 0): $(printf '%s' "$out" | tail -n 1)"
      else
        ran "check-diariser-auto.py ran after this edit to pyannote_engine.py and FAILED (exit $rc). CI runs this same script and will fail on it. Its output:
$out"
      fi
    else
      add "CLAUDE.md: after any change to the diariser election run python3 scripts/check-diariser-auto.py - python3 was not on PATH here, so the hook could not run it for you."
    fi ;;
esac

# A PowerShell script is parsed on the spot, since nothing in CI or the suite does.
case "$p" in
  *.ps1)
    if command -v pwsh >/dev/null 2>&1 && [ -f "$p" ]; then
      out=$(HOOK_PS1_PATH="$p" pwsh -NoProfile -NonInteractive -Command '$t = $err = $null; [Management.Automation.Language.Parser]::ParseFile($env:HOOK_PS1_PATH, [ref]$t, [ref]$err) > $null; $err | ForEach-Object { "{0}:{1}: {2}" -f $_.Extent.File, $_.Extent.StartLineNumber, $_.Message }; exit ($err | Measure-Object).Count' 2>&1)
      rc=$?
      out=$(printf '%s' "$out" | tr -d '\r')
      if [ "$rc" -eq 0 ]; then
        ran "PowerShell parsed $p after this edit: 0 parse errors."
      else
        ran "PowerShell parsed $p after this edit and found $rc parse error(s) - fix them before anything runs it:
$out"
      fi
    else
      add "CLAUDE.md: after editing a .ps1 parse them all with the one-liner in the Building and testing section - pwsh was not on PATH here, so the hook could not parse it for you."
    fi ;;
esac

[ -n "$notes$checks" ] || exit 0

if [ -n "$checks" ]; then
  if command -v python3 >/dev/null 2>&1; then
    HOOK_NOTES="$notes" HOOK_CHECKS="$checks" python3 -c 'import json, os, sys; text = (os.environ["HOOK_NOTES"] + os.environ["HOOK_CHECKS"]).strip(); sys.stdout.write(json.dumps({"hookSpecificOutput": {"hookEventName": "PostToolUse", "additionalContext": text}}))'
    exit 0
  fi
  # No python3: the check output can still travel once the two characters JSON cannot take
  # verbatim are removed and the lines joined.
  flat=$(printf '%s%s' "$notes" "$checks" | tr -d '"\\' | tr '\n' ' ')
  printf '{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"%s"}}' "${flat% }"
  exit 0
fi

printf '{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"%s"}}' "${notes% }"
