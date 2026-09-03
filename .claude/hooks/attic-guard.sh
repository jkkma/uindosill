#!/bin/sh
# PreToolUse hook (Edit|MultiEdit|Write): attic/ holds retired code - engines and their tests
# that nothing builds, tests or ships - kept readable rather than deleted. An edit aimed there
# is almost always an edit that meant the live tree: a retired engine still has files named
# like the ones that replaced it, and a search finds both. So an edit under attic/ is not
# refused; it is put to the person first, as a permission prompt naming the file.
#
# Plain sh, same shape and same reasons as gated-test-reminder.sh beside it. Stdin is the
# hook event JSON; the decision goes out as hookSpecificOutput.permissionDecision, and the
# reason text is spliced into JSON verbatim, so it must stay free of double quotes and
# backslashes.

input=$(cat)

path=$(printf '%s' "$input" \
  | grep -o '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | head -n 1 \
  | sed 's/^"file_path"[[:space:]]*:[[:space:]]*"//; s/"$//')
[ -n "$path" ] || exit 0

p=$(printf '%s' "$path" | tr '\\' '/' | tr -s '/')
root=$(printf '%s' "${CLAUDE_PROJECT_DIR:-.}" | tr '\\' '/' | tr -s '/')

# Relative to the repository when the path is inside it. The second pattern covers a path
# whose drive letter or case differs from the root's, which Windows allows and sh cannot see.
rel=${p#"$root"/}
case "$rel" in
  attic/*|*/attic/*)
    printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"%s"}}' \
      "attic/ holds retired code that nothing builds, tests or ships. This edit targets $rel - confirm it is meant for the attic and not for the live file of the same name under src/, tests/ or python/." ;;
esac
exit 0
