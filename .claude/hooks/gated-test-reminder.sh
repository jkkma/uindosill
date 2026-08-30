#!/bin/sh
# PostToolUse hook (Edit|MultiEdit|Write): the working agreement ties four paths to gated
# checks CI cannot run, and the tests tree to a count three documents quote. Those rules
# exist because they get forgotten; this prints the matching reminder into context when
# such a path is touched. Plain sh with cat/grep/sed/tr/head, because it must behave
# identically under Git Bash on Windows and sh on the Linux cloud runner.
#
# Stdin is the hook event JSON. Reminders go out as additionalContext, spliced into JSON
# verbatim - so the text below must stay free of double quotes and backslashes.

input=$(cat)

# grep -o so the FIRST "file_path" wins (tool_input's) whatever comes after it in the event;
# a greedy sed across the whole line would take the last one instead.
path=$(printf '%s' "$input" \
  | grep -o '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | head -n 1 \
  | sed 's/^"file_path"[[:space:]]*:[[:space:]]*"//; s/"$//')
[ -n "$path" ] || exit 0

# Windows paths arrive JSON-escaped (C:\\Users\\...); fold every backslash run into one
# forward slash so the patterns below need a single spelling.
p=$(printf '%s' "$path" | tr '\\' '/' | tr -s '/')

notes=''
add() { notes="$notes$1 "; }

case "$p" in
  *GermanNumberWords*) add "CLAUDE.md: after any change to GermanNumberWords, run the FLEURS-gated test (UINDOSILL_FLEURS_DIR=<google/fleurs data dir> dotnet test Uindosill.slnx -c Release) - it is the only check that the German number rewrite stays a no-op on written text." ;;
esac
case "$p" in
  *Parakeet.Engine.SileroVad*) add "CLAUDE.md: after any change to the Silero VAD engine, run the gated tests (UINDOSILL_SILERO_VAD=<path to silero_vad.onnx> dotnet test Uindosill.slnx -c Release) and drive 'uindosill transcribe' over a real file with the model installed - nothing else in the suite runs the graph." ;;
esac
case "$p" in
  *Parakeet.Engine.LlamaServer*) add "CLAUDE.md: after any change to the llama-server engine, run the three gated end-to-end tests (UINDOSILL_LLM_SERVER_ROOT and UINDOSILL_LLM_TEST_MODEL set; add UINDOSILL_LLM_TEST_BACKEND=vulkan or cuda on a machine that has one - the only place child-process argument changes are really tested)." ;;
esac
case "$p" in
  *uindosill_engines/translator/*) add "CLAUDE.md: the translator has no CI coverage - drive the sidecar parity fixture by hand after this change: one load on CPU and one on webgpu, each reporting parity." ;;
esac
case "$p" in
  */tests/*|tests/*) add "If this change added or removed tests, run python3 scripts/check-test-counts.py after the next test run - three documents quote the count and CI fails on a stale one." ;;
esac

[ -n "$notes" ] || exit 0
printf '{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"%s"}}' "${notes% }"
