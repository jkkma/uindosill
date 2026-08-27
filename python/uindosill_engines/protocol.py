"""The line protocol between the .NET host and this process.

One JSON object per line, UTF-8, `\n`-terminated, requests on stdin and responses on stdout. No
framing beyond the newline, because every payload here is small: audio arrives as a path and
results are a few thousand numbers. Nothing streams bytes over this channel on purpose — a pipe
that carries both a protocol and a megabyte of PCM is a pipe with two failure modes.

**stdout belongs to the protocol and to nothing else.** That is the one rule this file exists to
enforce. `torch`, `librosa` and `numba` all print to stdout given the right provocation, and a
single stray line of theirs lands in the middle of a JSON stream and desynchronises the host for
the rest of the run. :func:`claim_stdout` takes a duplicate of the real handle for the channel at
start-up and then points file descriptor 1 itself at stderr, not only `sys.stdout` — so a `print`,
an `os.write(1, ...)`, a C extension's `printf` and a child process this one spawns all get logged
instead of corrupting the channel.

Every message carries the `id` of the request it belongs to and a `type`:

    -> {"id": 1, "op": "hello"}
    <- {"id": 1, "type": "result", "protocol": 1, "python": "3.12.10", ...}

    -> {"id": 2, "op": "load", "model": "sortformer", "path": "C:/.../sortformer-default.onnx"}
    <- {"id": 2, "type": "result", "capabilities": {...}}

    -> {"id": 3, "op": "label", "wav": "C:/.../tmp.wav", "postProcessing": {...}}
    <- {"id": 3, "type": "progress", "completed": 4, "total": 61}
    <- {"id": 3, "type": "result", "turns": [...]}

A failure is a message, not a crash: the host has to tell "this file could not be read" from "the
sidecar died", and only the first of those should let a batch continue.

    <- {"id": 3, "type": "error", "kind": "audio", "message": "...", "traceback": "..."}
"""

from __future__ import annotations

import json
import os
import sys
import traceback
from typing import Any, Callable, Iterator

#: Bumped when a field changes meaning or disappears. The host refuses a sidecar whose number it
#: does not know, which is what stops a stale bundled Python from being driven by a newer host.
#:
#: **2 — the diariser gained a second engine, and `path` changed meaning with it.** A `load` for
#: `engine: "diariser"` now carries `kind`, and `path` is a `.onnx` file when that is `sortformer`
#: and a directory of five files when it is `diarizen`. A version-1 sidecar ignores `kind`, reads
#: the directory as a file and fails on a message about a model that is plainly there — which is
#: precisely the confusing failure several megabytes in that this number exists to turn into a
#: refusal at `hello`.
#:
#: **3 — a `load` for `engine: "diariser"` may carry `batchSize`.** Absent it means the model's own
#: value, which is the checkpoint's for `diarizen` and the exported graph's geometry for
#: `sortformer` — the latter refuses the field rather than ignoring it. A version-2 sidecar has no
#: such field and would drop it silently, leaving a host that had offered the choice, and a person
#: who had made it, both believing a number was in force that was not. That is the class of failure
#: this constant exists to convert into a refusal at `hello`, and an optional field whose absence
#: is indistinguishable from acceptance is exactly the case where it earns its keep.
PROTOCOL_VERSION = 3


class RequestError(Exception):
    """A failure that belongs to one request. Reported as an error message, not an exit.

    `kind` is what the host switches on, so it is a closed vocabulary rather than free text:
    ``request`` (malformed or unknown op), ``model`` (weights missing or unloadable), ``audio``
    (the file could not be read) and ``internal`` (anything unforeseen, which is a bug here).
    """

    def __init__(self, kind: str, message: str) -> None:
        super().__init__(message)
        self.kind = kind


def claim_stdout() -> Any:
    """Take the real stdout for the protocol and point file descriptor 1 at stderr.

    Returns the handle to write protocol messages to. Called once, before any model library is
    imported — importing is itself enough to make some of them print.

    Replacing `sys.stdout` alone is not enough, and until 2026-08-22 it was all this did: the
    descriptor underneath still led to the pipe, so `os.write(1, ...)`, `sys.__stdout__`, a C
    extension's `printf` and every child process this one spawned wrote into the protocol — and a
    write without a newline glued onto the next reply, which the host then could not read. The
    `dup2` makes descriptor 1 *be* stderr, for the C runtime and for inherited handles too, so the
    only route to the channel is the duplicated descriptor this returns.
    """
    channel = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", newline="\n")
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    sys.stdout = sys.stderr
    sys.__stdout__ = sys.stderr
    return channel


class Channel:
    """Reads requests and writes replies, and flushes every line.

    The flush is not a detail. The host blocks reading a line, so a buffered reply is a deadlock
    that looks exactly like a slow model.
    """

    def __init__(self, out: Any, inp: Any | None = None) -> None:
        self._out = out
        self._in = inp if inp is not None else sys.stdin

    def send(self, message: dict[str, Any]) -> None:
        try:
            line = json.dumps(message, ensure_ascii=False, separators=(",", ":"), allow_nan=False)
        except ValueError as exc:
            # Left to itself `json.dumps` writes `NaN` or `Infinity`, which is not JSON: the host
            # records the line as noise and the request it answered waits on for a reply that has
            # already been sent. An error for the same id reaches the host as a reply, and it says
            # what was wrong with the one it replaces.
            line = json.dumps(
                {
                    "id": message.get("id"),
                    "type": "error",
                    "kind": "internal",
                    "message": f"the reply to request {message.get('id')!r} carried a number JSON cannot: {exc}",
                },
                ensure_ascii=False,
                separators=(",", ":"),
            )
        self._out.write(line + "\n")
        self._out.flush()

    def result(self, request_id: Any, **fields: Any) -> None:
        self.send({"id": request_id, "type": "result", **fields})

    def progress(self, request_id: Any, completed: int, total: int) -> None:
        self.send({"id": request_id, "type": "progress", "completed": completed, "total": total})

    def error(self, request_id: Any, kind: str, message: str, tb: str | None = None) -> None:
        payload: dict[str, Any] = {"id": request_id, "type": "error", "kind": kind, "message": message}
        if tb:
            payload["traceback"] = tb
        self.send(payload)

    def requests(self) -> Iterator[dict[str, Any]]:
        """Yields one parsed request per line until stdin closes.

        A closed stdin is how the host says "stop" when it is being torn down without ceremony —
        killed, crashed, or the user closed the window — so it ends the loop rather than raising.
        A line that is not JSON is reported and skipped: it cannot be attributed to a request id,
        but exiting on it would turn one corrupt line into a dead sidecar.
        """
        for line in self._in:
            line = line.strip()
            if not line:
                continue
            try:
                message = json.loads(line)
            except ValueError as exc:
                self.error(None, "request", f"not JSON: {exc}")
                continue
            if not isinstance(message, dict):
                self.error(None, "request", "a request must be a JSON object")
                continue
            yield message


def serve(channel: Channel, handlers: dict[str, Callable[[dict[str, Any], Channel], dict[str, Any]]]) -> int:
    """Dispatches until stdin closes or a handler asks to stop.

    A handler returning ``None`` has already replied for itself (the streaming ones do, because
    they interleave progress). Anything it raises becomes an error message addressed to the
    request that caused it, so one bad file does not end the process.
    """
    for message in channel.requests():
        request_id = message.get("id")
        op = message.get("op")
        if op == "shutdown":
            channel.result(request_id, stopped=True)
            return 0

        handler = handlers.get(op)
        if handler is None:
            channel.error(request_id, "request", f"unknown op {op!r}")
            continue

        try:
            reply = handler(message, channel)
        except RequestError as exc:
            channel.error(request_id, exc.kind, str(exc), traceback.format_exc())
        except Exception as exc:  # noqa: BLE001 - the boundary; nothing above this catches
            channel.error(request_id, "internal", f"{type(exc).__name__}: {exc}", traceback.format_exc())
        else:
            if reply is not None:
                channel.result(request_id, **reply)
    return 0
