"""Check what the diariser's `auto` elects, on a machine with neither a model nor onnxruntime.

**Why this exists as a script rather than a test.** There is no Python test suite in this
repository — the sidecar is exercised through a fake one from C#, which is the right shape for the
protocol and the wrong shape for this. What `auto` resolves to is pure logic over two filesystem
checks and one provider list, it decides which arithmetic unit a user's diarisation runs on, and it
was wrong for a day in each direction while it was being written. That earns a guard.

**It needs nothing installed.** `uindosill_engines.diariser.pyannote_engine` imports `os` and the
protocol at module scope and defers torch and onnxruntime into the functions that use them, so the
election can be read without either. Both are stubbed per case below, which is also the only way to
check the machines this one is not: a CI runner has no WebGPU adapter and no NVIDIA card, this
desktop has no DirectML build, and all of those cases have to hold.

**Stubbing torch is not optional, it is what makes this deterministic.** `cuda` joined `AUTO_ORDER`
on 2026-08-28, and it is elected on `torch.cuda.is_available()`. Left unstubbed, this script would
answer one way inside `pyannote-cuda-venv` and another inside `pyannote-venv` or on CI — a guard
whose result depends on which interpreter ran it is not a guard. Every case therefore says which
torch it is about, including the ones that are not about torch at all.

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
    TORCH_AUTO_DEVICES,
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


def stub_torch(cuda: bool | BaseException) -> None:
    """Put a fake `torch` in `sys.modules`, so a case can say what CUDA this machine has.

    `_torch_cuda_available` imports torch inside itself, the same deferral `resolve_auto` uses for
    onnxruntime, so this is picked up on the next call with no import order to arrange.

    Passing an exception instead of a bool covers the third state, which is neither "yes" nor "no":
    a torch whose CUDA libraries are present but unusable raises out of `is_available()`. Under
    `auto` that has to read as "not this one" rather than as a diariser that will not load.
    """
    module = types.ModuleType("torch")
    cuda_module = types.ModuleType("torch.cuda")

    def is_available() -> bool:
        if isinstance(cuda, BaseException):
            raise cuda
        return cuda

    cuda_module.is_available = is_available  # type: ignore[attr-defined]
    module.cuda = cuda_module  # type: ignore[attr-defined]
    sys.modules["torch"] = module
    sys.modules["torch.cuda"] = cuda_module


def block_torch() -> None:
    """Make `import torch` fail: the machine with no torch installed at all.

    **`None` rather than deleting the key**, which is the difference between testing that case and
    accidentally testing this interpreter's. CPython raises `ImportError` for a `sys.modules` entry
    that is `None`; popping the key would instead let the import find whatever torch is really
    installed, so inside `pyannote-cuda-venv` this case would quietly become "a real torch with a
    real 5080" and pass for the wrong reason.
    """
    sys.modules["torch"] = None  # type: ignore[assignment]
    sys.modules.pop("torch.cuda", None)


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
    check("AUTO_ORDER is cuda then webgpu (dml stays behind a name)", AUTO_ORDER, ["cuda", "webgpu"])
    check("dml is still reachable by name", "dml" in ONNX_PROVIDERS, True)
    check("cuda is a torch device, not an ONNX provider", "cuda" in ONNX_PROVIDERS, False)
    check("cuda is the only torch device auto elects", TORCH_AUTO_DEVICES, ("cuda",))

    with tempfile.TemporaryDirectory() as stack_name:
        stack = types.SimpleNamespace(name=stack_name)

        bare = model_dir(stack)                                     # no onnx/ at all
        half = model_dir(stack, ONNX_FILES[0])                      # one graph of two
        both = model_dir(stack, *ONNX_FILES)                        # the real thing

        check("graphs_installed: no onnx directory", graphs_installed(bare), False)
        check("graphs_installed: one graph of two", graphs_installed(half), False)
        check("graphs_installed: both graphs", graphs_installed(both), True)

        # ---- The CPU torch build, which is what the bundle ships. -------------------------------
        # A machine with a WebGPU build — this desktop's pyannote-venv, and any bundle since rc.6.
        stub_torch(cuda=False)
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

        # ---- A CUDA torch build, which is pyannote-cuda-venv and no shipped install. -------------
        # **The graphs must not gate it.** `cuda` needs no derived artefact, so the case that would
        # have caught the obvious wrong shape — running the graphs check over every candidate — is
        # the bare model directory, where `cuda` still has to be elected.
        stub_torch(cuda=True)
        stub_onnxruntime([WEBGPU, CPU])
        check("CUDA torch, graphs absent", resolve_auto(bare), ["cuda", "cpu"])
        check("CUDA torch, one graph of two", resolve_auto(half), ["cuda", "cpu"])
        check("CUDA torch, both graphs", resolve_auto(both), ["cuda", "webgpu", "cpu"])
        check("CUDA torch, no model directory", resolve_auto(None), ["cuda", "cpu"])

        # CUDA leads WebGPU on the measurement in AUTO_ORDER's docstring: it moves both neural
        # stages where an ONNX provider moves one, and was 13x the CPU against WebGPU's ~2x.
        stub_onnxruntime([CPU])
        check("CUDA torch, no WebGPU in the build", resolve_auto(both), ["cuda", "cpu"])

        # ---- The two ways torch answers neither yes nor no. --------------------------------------
        # A torch that raises out of `is_available()` — CUDA libraries present but unusable — must
        # read as "not this one" under `auto`, not as a load that fails.
        stub_torch(cuda=RuntimeError("no CUDA-capable device is detected"))
        stub_onnxruntime([WEBGPU, CPU])
        check("torch raises from is_available", resolve_auto(both), ["webgpu", "cpu"])
        check("torch raises, graphs absent", resolve_auto(bare), ["cpu"])

        # No torch installed at all: the machine this script promises to run on.
        block_torch()
        check("no torch installed, both graphs", resolve_auto(both), ["webgpu", "cpu"])
        check("no torch installed, graphs absent", resolve_auto(bare), ["cpu"])

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
