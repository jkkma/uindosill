#!/usr/bin/env python3
"""Check the shipped sidecar bootstrap with an isolated real Python interpreter.

No weights, packages or network are needed. CI uses its normal Python in isolated
mode; bundle-python.ps1 also runs this against the actual embedded interpreter.
Pass --interpreter to check another interpreter without modifying its ._pth file.
"""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parent.parent
BOOTSTRAP = ROOT / "src/Parakeet.Engine.Python/sidecar_bootstrap.py"

PROBE = '''\
import importlib.metadata
import importlib.util
import json
import sys
import audit_torch

request = json.loads(sys.stdin.readline())
print(json.dumps({
    "backend": audit_torch.backend,
    "version": importlib.metadata.version("audit-torch"),
    "echo": request["text"],
    "isolated": sys.flags.isolated,
    "untrusted_visible": importlib.util.find_spec("untrusted_marker") is not None,
    "stdin_encoding": sys.stdin.encoding,
    "stdout_encoding": sys.stdout.encoding,
}, ensure_ascii=False))
'''


def stage_distribution(root: Path, backend: str, version: str) -> None:
    root.mkdir(parents=True, exist_ok=True)
    (root / "audit_torch.py").write_text(f"backend = {backend!r}\n", encoding="utf-8")
    metadata = root / f"audit_torch-{version}.dist-info"
    metadata.mkdir()
    (metadata / "METADATA").write_text(
        f"Metadata-Version: 2.1\nName: audit-torch\nVersion: {version}\n", encoding="utf-8"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--interpreter", default=sys.executable)
    args = parser.parse_args()
    interpreter = str(Path(args.interpreter).resolve())
    bootstrap = BOOTSTRAP.read_text(encoding="utf-8")
    with tempfile.TemporaryDirectory(prefix="uindosill-python-launch-") as temporary:
        root = Path(temporary)
        packages = root / "packages with spaces ' café"
        overlay = root / "overlay with spaces ' café"
        untrusted = root / "untrusted"
        stage_distribution(packages, "cpu", "1.0+cpu")
        stage_distribution(overlay, "cuda", "1.0+cuda")
        engines = packages / "uindosill_engines"
        engines.mkdir()
        (engines / "__init__.py").write_text("", encoding="utf-8")
        (engines / "__main__.py").write_text(PROBE, encoding="utf-8")
        untrusted.mkdir()
        (untrusted / "untrusted_marker.py").write_text("", encoding="utf-8")
        env = {
            **os.environ,
            "PYTHONPATH": str(untrusted),
            "PYTHONHOME": str(untrusted),
            "PYTHONIOENCODING": "ascii",
        }
        message = "José 日本語 🎵"
        for backend, paths in (("cpu", [packages]), ("cuda", [overlay, packages])):
            child = subprocess.run(
                [interpreter, "-I", "-X", "utf8", "-u", "-c", bootstrap, *map(str, paths)],
                input=json.dumps({"text": message}, ensure_ascii=False) + "\n",
                capture_output=True, text=True, encoding="utf-8", env=env,
                cwd=untrusted, timeout=30, check=True,
            )
            observed = json.loads(child.stdout)
            expected = {
                "backend": backend, "version": f"1.0+{backend}", "echo": message,
                "isolated": 1, "untrusted_visible": False,
                "stdin_encoding": "utf-8", "stdout_encoding": "utf-8",
            }
            if observed != expected:
                raise AssertionError(f"{backend}: expected {expected!r}, got {observed!r}")
            print(f"PASS {backend}: import and metadata precedence, isolation, UTF-8")


if __name__ == "__main__":
    main()
