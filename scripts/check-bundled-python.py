#!/usr/bin/env python3
"""Smoke-import the real Windows bundle and pinned CUDA overlay without weights or GPU work.

The release workflow supplies packaging/import-smoke/ for temporary downloads/extraction and
writes its report under packaging/. No installed user Python or model store is consulted.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile


ROOT = Path(__file__).resolve().parent.parent
PROBE = '''\
import importlib.metadata
import importlib.util
import json
from pathlib import Path
import torch
import torchaudio
from pyannote.audio import Pipeline
import pyannote.audio

codec = {"status": "imported"}
try:
    import torchcodec
except Exception as error:
    codec = {"status": "unavailable", "exception": type(error).__name__, "message": str(error)}

print(json.dumps({
    "versions": {name: importlib.metadata.version(name) for name in ("torch", "torchaudio", "torchcodec")},
    "origins": {"torch": torch.__file__, "torchaudio": torchaudio.__file__, "pyannote.audio": pyannote.audio.__file__,
                "torchcodec": importlib.util.find_spec("torchcodec").origin},
    "torchRuntimeVersion": str(torch.__version__),
    "cpuTensorSum": torch.tensor([1.0, 2.0, 3.0], device="cpu").sum().item(),
    "pipelineImported": Pipeline is not None,
    "torchcodecImport": codec,
}))
'''


def probe(bundle: Path, overlay: Path | None, scratch: Path) -> dict:
    interpreter = bundle / "python.exe"
    packages = bundle / "Lib/site-packages"
    with tempfile.TemporaryDirectory(prefix="probe-", dir=scratch) as temporary:
        package = Path(temporary) / "uindosill_engines"
        package.mkdir()
        (package / "__init__.py").write_text("", encoding="utf-8")
        (package / "__main__.py").write_text(PROBE, encoding="utf-8")
        roots = [Path(temporary), *([overlay] if overlay else []), packages]
        env = {**os.environ, "PYANNOTE_METRICS_ENABLED": "false"}
        env.pop("PYTHONPATH", None)
        child = subprocess.run(
            [str(interpreter), "-I", "-X", "utf8", "-u", "-c",
             (ROOT / "src/Parakeet.Engine.Python/sidecar_bootstrap.py").read_text(encoding="utf-8"),
             *map(str, roots)],
            capture_output=True, text=True, encoding="utf-8", timeout=120, env=env,
        )
        if child.returncode:
            raise RuntimeError(f"Real package import failed ({child.returncode}): {child.stderr}")
        observed = json.loads(child.stdout.splitlines()[-1])
        observed["stderr"] = child.stderr
        expected_root = overlay or packages
        for name in ("torch", "torchaudio", "torchcodec"):
            if not Path(observed["origins"][name]).resolve().is_relative_to(expected_root):
                raise ValueError(f"{name} was imported from outside the selected package root.")
        if not Path(observed["origins"]["pyannote.audio"]).resolve().is_relative_to(packages):
            raise ValueError("pyannote was imported from outside the actual bundle.")
        if observed["cpuTensorSum"] != 6.0 or not observed["pipelineImported"]:
            raise ValueError("CPU tensor arithmetic or pyannote Pipeline import failed.")
        if observed["torchRuntimeVersion"] != observed["versions"]["torch"]:
            raise ValueError("The loaded torch version differs from its selected metadata.")
        return observed


def fetch_overlay(manifest: dict, scratch: Path, repo: str) -> Path:
    tag = manifest["baseUrl"].rstrip("/").split("/")[-1]
    archive = scratch / manifest["archiveName"]
    whole = hashlib.sha256()
    total = 0
    with archive.open("wb") as output:
        for part in manifest["parts"]:
            subprocess.run(
                ["gh", "release", "download", tag, "--repo", repo, "--pattern", part["fileName"],
                 "--dir", str(scratch), "--clobber"], check=True, timeout=300,
            )
            path = scratch / part["fileName"]
            if path.stat().st_size != part["sizeBytes"]:
                raise ValueError(f"Downloaded size differs: {path.name}")
            digest = hashlib.sha256()
            with path.open("rb") as source:
                while chunk := source.read(4 * 1024**2):
                    digest.update(chunk)
                    whole.update(chunk)
                    output.write(chunk)
                    total += len(chunk)
            if digest.hexdigest() != part["sha256"]:
                raise ValueError(f"Downloaded SHA256 differs: {path.name}")
            path.unlink()
    if total != manifest["archiveBytes"] or whole.hexdigest() != manifest["archiveSha256"]:
        raise ValueError("The reconstructed CUDA archive differs from the shipped pin.")
    overlay = scratch / "overlay"
    with zipfile.ZipFile(archive) as zipped:
        zipped.extractall(overlay)
    archive.unlink()
    return overlay.resolve()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.scratch.mkdir(parents=True, exist_ok=True)
    report = {"qualification": "real package imports and CPU arithmetic only; no weights or GPU inference"}
    try:
        bundle = args.bundle.resolve()
        report["cpu"] = probe(bundle, None, args.scratch)
        manifest = json.loads((ROOT / "src/Parakeet.Engine.Python/cuda-pack.json").read_text())
        overlay = fetch_overlay(manifest, args.scratch, args.repo)
        report["cudaOverlay"] = probe(bundle, overlay, args.scratch)
        if report["cudaOverlay"]["versions"] != manifest["packages"]:
            raise ValueError("The imported CUDA overlay metadata differs from the shipped manifest.")
        report["passed"] = True
        shutil.rmtree(overlay)
        print("PASS real CPU/CUDA package roots, torch/torchaudio/pyannote imports and CPU arithmetic")
    except Exception as error:
        report["passed"] = False
        report["error"] = str(error)
        raise
    finally:
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
