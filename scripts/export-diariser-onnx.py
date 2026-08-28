"""Export pyannote community-1's two neural stages to ONNX, so the diariser has a route an
ONNX Runtime execution provider can select.

**A command line over `uindosill_engines.diariser.onnx_export`, and nothing more.** The export
itself lives in the package because the application calls it too, through the sidecar's
`exportDiariserGraphs` op — two implementations would be two graphs that can differ, which is the
one thing a parity number would then be measuring. Read that module for what is exported and why the
featuriser is not.

**Why the route exists.** `pyannote-speaker-diarization-community-1` is torch on both stages, so
`--speaker-backend webgpu` had nothing to name and was refused outright. That refusal is correct for
a torch-only pipeline and wrong as a permanent answer on a machine whose only GPU is an integrated
Radeon: there is no CUDA torch for it and `torch-directml` pins `torch==2.4.1` against this bundle's
2.13.0, so ONNX Runtime is the only way the GPU gets used at all.

Writes to `runs/diariser-onnx/<variant>/` by default, per CLAUDE.md: nothing a measurement produces
belongs in the working tree. `--install` writes to `<model-dir>/onnx/` instead, which is where the
engine looks — the same place the application's own export puts them.

    python scripts/export-diariser-onnx.py --model-dir <pyannote dir> --out runs/diariser-onnx/community-1
    python scripts/export-diariser-onnx.py --model-dir <pyannote dir> --install
"""

from __future__ import annotations

import argparse
import json
import os
import sys

# The package is beside this script's parent, and this script is run from a checkout rather than
# from an installed bundle.
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "python"))

from uindosill_engines.diariser import onnx_export  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-dir", required=True, help="the installed pyannote pipeline directory")
    parser.add_argument("--out", help="output directory; default runs/diariser-onnx/community-1")
    parser.add_argument(
        "--install",
        action="store_true",
        help="write to <model-dir>/onnx/ instead, where the engine looks for them",
    )
    parser.add_argument("--opset", type=int, default=onnx_export.DEFAULT_OPSET)
    parser.add_argument("--trace-batch", type=int, default=onnx_export.DEFAULT_TRACE_BATCH)
    parser.add_argument(
        "--no-parity",
        action="store_true",
        help="skip the sweep against torch. The graphs are then unchecked; the manifest says so by "
             "carrying no parity block.",
    )
    options = parser.parse_args()

    if options.install and options.out:
        parser.error("--install and --out name two different destinations; pass one.")

    out = os.path.join(options.model_dir, "onnx") if options.install else (
        options.out or os.path.join("runs", "diariser-onnx", "community-1")
    )

    def progress(done: int, total: int) -> None:
        print(f"  [{done}/{total}]", flush=True)

    print(f"loading  {options.model_dir}", flush=True)
    manifest = onnx_export.export(
        model_dir=options.model_dir,
        out_dir=out,
        opset=options.opset,
        trace_batch=options.trace_batch,
        parity=not options.no_parity,
        progress=progress,
    )

    for stage, graph in manifest["graphs"].items():
        print(f"{stage:13s} {graph['file']:20s} {graph['bytes'] / (1 << 20):6.2f} MiB  "
              f"({graph['exporter']})", flush=True)

    for name, entry in manifest.get("parity", {}).items():
        if "worst_max_abs_diff" in entry and entry["worst_max_abs_diff"] is not None:
            print(f"parity {name:8s} worst max|Δ| {entry['worst_max_abs_diff']:.3e}  "
                  f"({entry['provider_used']})", flush=True)
        else:
            print(f"parity {name:8s} {json.dumps(entry)[:120]}", flush=True)

    print(f"\nwrote {manifest['out_dir']}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
