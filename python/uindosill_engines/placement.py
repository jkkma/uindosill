"""Where a graph actually ran, as opposed to which provider was registered against it.

Both engines already refuse a provider that fails to initialise, because a session that silently
falls back to the CPU is indistinguishable from success except in the timings. That assertion asks
``get_providers()``, and **``get_providers()`` reports registration rather than placement**: a
provider can register, build a session, return success from every call the host makes, and own not
one node of the graph. ONNX Runtime places what the provider declined on the CPU and says nothing.

That is not a hypothetical. Measured on an AMD XDNA 2 NPU on 2026-08-25, six of eight minimal graphs
produced accelerator timings within a few per cent of their CPU timings while running entirely on
the CPU — a registered provider, a built session, and no diagnostic anywhere. The same shape of
failure is available to any provider this project can select.

**What this module is for.** This project's rule for automatic selection is that what it picks
unasked reproduces the figure it publishes. `parity` answers half of that — whether the numbers come
out the same. This answers the other half, which nothing has checked until now: whether the graph ran
where the answer says it did. A provider that reproduces the CPU's numbers *because it is the CPU*
passes parity and means nothing.

**Why profiling and not something cheaper.** Nothing cheaper is available at the version this
project pins. ONNX Runtime gained a real API for this — ``session.record_ep_graph_assignment_info``
with ``Session_GetEpGraphAssignmentInfo`` — in 1.25, and the bundle is on 1.27 for Python but the
C# side is on a build whose managed binding does not surface it. ``session.disable_cpu_ep_fallback``
looks like the obvious answer and is not: it fails a session when *any* node lands on the CPU, and
ONNX Runtime deliberately places shape operators there, so a graph genuinely running almost entirely
on an accelerator would refuse to open. It is a probe for a purpose-built graph, not a setting for a
real one.

So: build the session with profiling on, run the parity check, end profiling, count the nodes. The
cost is bounded because :func:`end` stops the recording — profiling is not left on for the run.
"""

from __future__ import annotations

import json
import os
from typing import Any

#: Providers whose presence in a profile means the graph reached the accelerator it named. The CPU
#: provider is not absent from a healthy accelerated session — ONNX Runtime places shape operators
#: and anything the provider declined there — so the question is never "is the CPU absent" but "did
#: the named provider get a meaningful share".
_CPU = "CPUExecutionProvider"


def enable(options: Any) -> None:
    """Turns profiling on for a session about to be built.

    Must be called before the session is created; ONNX Runtime reads this at construction and there
    is no way to switch it on afterwards. Pair with :func:`end`.
    """
    options.enable_profiling = True


def end(session: Any) -> dict[str, int]:
    """Stops profiling on a session that has run at least once, and counts nodes by provider.

    Returns a mapping of provider name to the number of graph nodes ONNX Runtime executed on it.
    **A session that has not run yet yields an empty mapping**, because the profile records
    executions rather than the partition plan — which is a real limitation and the reason callers
    run the parity check first.

    The profile file is deleted; it is a diagnostic, and `runs/` is where this project's output
    belongs rather than beside a model.
    """
    path = session.end_profiling()
    if not path or not os.path.isfile(path):
        return {}
    try:
        with open(path, encoding="utf-8") as fh:
            events = json.load(fh)
    except (OSError, ValueError):
        return {}
    finally:
        try:
            os.remove(path)
        except OSError:
            # Losing the temporary file matters less than losing the answer, and on Windows a
            # profile can still be held briefly after end_profiling returns.
            pass

    counts: dict[str, int] = {}
    for event in events:
        if not isinstance(event, dict) or event.get("cat") != "Node":
            continue
        provider = (event.get("args") or {}).get("provider")
        if provider:
            counts[provider] = counts.get(provider, 0) + 1
    return counts


def summarise(counts: dict[str, int], wanted: str) -> dict[str, Any]:
    """What the counts mean for the provider that was asked for.

    ``fraction`` is the share of executed nodes the named provider owned, and ``ran_there`` is
    whether it owned any at all. The two are reported separately on purpose: zero is a different
    failure from a small share, and only the first is unambiguous. A graph can legitimately leave
    shape operators on the CPU, so **no threshold is asserted here** — this reports, and the caller
    decides, because what counts as a healthy share is a per-graph question this module cannot
    answer.
    """
    total = sum(counts.values())
    on_wanted = counts.get(wanted, 0)
    return {
        "nodes": counts,
        "total": total,
        "wanted": wanted,
        "onWanted": on_wanted,
        "onCpu": counts.get(_CPU, 0),
        # None rather than 0.0 when nothing ran: a fraction of a run that did not happen is not
        # zero, it is unknown, and the two must not read the same downstream.
        "fraction": (on_wanted / total) if total else None,
        "ranThere": on_wanted > 0,
        "measured": total > 0,
    }
