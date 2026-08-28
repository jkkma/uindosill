"""Check what the diariser's `auto` elects, on a machine with neither a model nor onnxruntime.

**Why this exists as a script rather than a test.** There is no Python test suite in this
repository — the sidecar is exercised through a fake one from C#, which is the right shape for the
protocol and the wrong shape for this. What `auto` resolves to is pure logic over two filesystem
checks and one provider list, it decides which arithmetic unit a user's diarisation runs on, and it
was wrong for a day in each direction while it was being written. That earns a guard.

**It needs nothing installed.** `uindosill_engines.diariser.pyannote_engine` imports `os` and the
protocol at module scope and defers torch and onnxruntime into the functions that use them, so the
election can be read without either. `onnxruntime` is stubbed per case below, which is also the only
way to check the machines this one is not: a CI runner has no WebGPU adapter and this desktop has no
DirectML build, and both of those cases have to hold.

    python3 scripts/check-diariser-auto.py
"""

from __future__ import annotations

import os
import sys
import tempfile
import types

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "python"))

from uindosill_engines.diariser.pyannote_engine import (  # noqa: E402
    AUTO_ORDER,
    ONNX_FILES,
    ONNX_PROVIDERS,
    graphs_installed,
    resolve_auto,
)

WEBGPU = "WebGpuExecutionProvider"
DML = "DmlExecutionProvider"
CPU = "CPUExecutionProvider"

failures: list[str] = []


def stub_onnxruntime(providers: list[str]) -> None:
    """Put a fake `onnxruntime` in `sys.modules`, so a case can name the providers it is about.

    `resolve_auto` imports onnxruntime inside itself rather than at module scope, so this is picked
    up on the next call without any import order to arrange.
    """
    module = types.ModuleType("onnxruntime")
    module.get_available_providers = lambda: list(providers)  # type: ignore[attr-defined]
    sys.modules["onnxruntime"] = module


def check(label: str, got: object, want: object) -> None:
    mark = "ok  " if got == want else "FAIL"
    print(f"  {mark}  {label}\n          got {got!r}, want {want!r}")
    if got != want:
        failures.append(label)


def model_dir(stack: tempfile.TemporaryDirectory, *graphs: str) -> str:
    """A model directory holding the named graphs under `onnx/`, and nothing else."""
    root = tempfile.mkdtemp(dir=stack.name)
    if graphs:
        onnx = os.path.join(root, "onnx")
        os.makedirs(onnx)
        for name in graphs:
            open(os.path.join(onnx, name), "wb").close()
    return root


def main() -> int:
    print(__doc__.strip().splitlines()[0])
    print()

    # The constants this check is written against. A provider added to AUTO_ORDER without a case
    # below would otherwise pass unnoticed, which is the failure this guard exists to prevent.
    check("AUTO_ORDER is webgpu alone (dml stays behind a name)", AUTO_ORDER, ["webgpu"])
    check("dml is still reachable by name", "dml" in ONNX_PROVIDERS, True)

    with tempfile.TemporaryDirectory() as stack_name:
        stack = types.SimpleNamespace(name=stack_name)

        bare = model_dir(stack)                                     # no onnx/ at all
        half = model_dir(stack, ONNX_FILES[0])                      # one graph of two
        both = model_dir(stack, *ONNX_FILES)                        # the real thing

        check("graphs_installed: no onnx directory", graphs_installed(bare), False)
        check("graphs_installed: one graph of two", graphs_installed(half), False)
        check("graphs_installed: both graphs", graphs_installed(both), True)

        # A machine with a WebGPU build — this desktop, and any bundle since rc.6.
        stub_onnxruntime([WEBGPU, CPU])
        check("no model directory at all", resolve_auto(None), ["cpu"])
        check("default argument, for a caller with no model", resolve_auto(), ["cpu"])
        check("model present, graphs absent", resolve_auto(bare), ["cpu"])
        check("model present, one graph of two", resolve_auto(half), ["cpu"])
        check("model present, both graphs", resolve_auto(both), ["webgpu", "cpu"])

        # A build with no WebGPU: the graphs exist and there is still nothing to elect. This is the
        # case a CI runner is in, and the reason the election cannot assume the wheel it wants.
        stub_onnxruntime([CPU])
        check("both graphs, no WebGPU in the build", resolve_auto(both), ["cpu"])

        # DirectML must never be elected, however available it is. It is exported for and has never
        # been executed on these graphs; the precaution it carries is inherited from the diariser in
        # `attic/sortformer/` rather than earned here.
        stub_onnxruntime([DML, CPU])
        check("both graphs, DirectML available", resolve_auto(both), ["cpu"])
        stub_onnxruntime([WEBGPU, DML, CPU])
        check("both graphs, WebGPU and DirectML", resolve_auto(both), ["webgpu", "cpu"])

    print()
    if failures:
        print(f"{len(failures)} check(s) failed:")
        for name in failures:
            print(f"  - {name}")
        return 1
    print("The diariser's `auto` elects what it is documented to elect.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
