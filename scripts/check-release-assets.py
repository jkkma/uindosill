#!/usr/bin/env python3
"""Check the packaged release and its update feeds, and record the files' measured digests."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re


ASSET_LIMIT = 2 * 1024**3


def verify(root: Path, version: str, commit: str) -> dict:
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version):
        raise ValueError("Expected a release version such as 1.0.0 or 1.0.0-rc.1.")
    required = {
        "UindosillDesktop-win-Setup.exe",
        "UindosillDesktop-win-cuda-Setup.exe",
        f"UindosillDesktop-{version}-full.nupkg",
        f"UindosillDesktop-{version}-win-cuda-full.nupkg",
        "releases.win.json",
        "releases.win-cuda.json",
        "uindosill-cli-win-x64.zip",
        "uindosill-python-win-x64.zip",
    }
    optional = {
        f"UindosillDesktop-{version}-delta.nupkg",
        f"UindosillDesktop-{version}-win-cuda-delta.nupkg",
    }
    selected = required | {name for name in optional if (root / name).is_file()}
    measured: dict[str, dict] = {}

    def measure(name: str) -> dict:
        if name in measured:
            return measured[name]
        if not name or Path(name).name != name or "\\" in name:
            raise ValueError(f"Invalid release filename: {name!r}")
        path = root / name
        size = path.stat().st_size
        if not 0 < size < ASSET_LIMIT:
            raise ValueError(f"{name}: {size} bytes; expected a nonempty file below 2 GiB.")
        sha1, sha256 = hashlib.sha1(), hashlib.sha256()
        with path.open("rb") as stream:
            while chunk := stream.read(4 * 1024**2):
                sha1.update(chunk)
                sha256.update(chunk)
        measured[name] = {
            "name": name,
            "sizeBytes": size,
            "sha1": sha1.hexdigest(),
            "sha256": sha256.hexdigest(),
        }
        return measured[name]

    for name in sorted(selected):
        measure(name)

    feeds = []
    for channel, full_name in (
        ("win", f"UindosillDesktop-{version}-full.nupkg"),
        ("win-cuda", f"UindosillDesktop-{version}-win-cuda-full.nupkg"),
    ):
        feed_name = f"releases.{channel}.json"
        raw_feed = json.loads((root / feed_name).read_text(encoding="utf-8-sig"))
        entries = {key.lower(): value for key, value in raw_feed.items()}.get("assets")
        if not isinstance(entries, list) or not entries:
            raise ValueError(f"{feed_name}: no update assets.")
        allowed = {
            full_name: "full",
            full_name.removesuffix("-full.nupkg") + "-delta.nupkg": "delta",
        }
        seen = set()
        packages = []
        for entry in entries:
            fields = {key.lower(): value for key, value in entry.items()}
            name = fields["filename"]
            if name not in allowed:
                raise ValueError(f"{feed_name}: {name} is not a current package in this channel.")
            if str(fields.get("type", "")).lower() != allowed[name]:
                raise ValueError(f"{feed_name}: {name} has the wrong package type.")
            if name in seen:
                raise ValueError(f"{feed_name}: duplicate package {name}.")
            seen.add(name)
            actual = measure(name)
            if fields["size"] != actual["sizeBytes"]:
                raise ValueError(f"{feed_name}: {name} has a different byte count.")
            digests = [algorithm for algorithm in ("sha1", "sha256") if fields.get(algorithm)]
            if not digests:
                raise ValueError(f"{feed_name}: {name} has no digest.")
            for algorithm in digests:
                if fields[algorithm].lower() != actual[algorithm]:
                    raise ValueError(f"{feed_name}: {name} has a different {algorithm.upper()}.")
            if str(fields["version"]) != version:
                raise ValueError(f"{feed_name}: {name} names another release version.")
            packages.append({"name": name, "version": fields["version"], "digests": digests})
        if full_name not in seen:
            raise ValueError(f"{feed_name}: the current full package is missing.")
        feeds.append({"name": feed_name, "packages": packages})

    return {
        "schema": 1,
        "version": version,
        "commit": commit,
        "assets": [measured[name] for name in sorted(selected)],
        "feeds": feeds,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = verify(args.root, args.version, args.commit)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    for asset in report["assets"]:
        print(f'{asset["name"]}: {asset["sizeBytes"]} bytes; SHA256 {asset["sha256"]}')
    print(f'Checked {len(report["assets"])} assets and both update feeds.')


if __name__ == "__main__":
    main()
