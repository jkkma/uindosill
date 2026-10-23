#!/usr/bin/env python3
"""Prepare release-local update feeds, check the release, and record measured file digests."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re


ASSET_LIMIT = 2 * 1024**3


def validate_version(version: str) -> None:
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version):
        raise ValueError("Expected a release version such as 1.0.0 or 1.0.0-rc.1.")


def prepare_feeds(root: Path, version: str) -> list[dict]:
    """After delta generation, keep only packages published on this GitHub release.

    GitHubSource resolves each feed's filenames within its originating release. Older
    releases supply the previous delta steps; their rows must not point into this one.
    Keep suspicious current filenames as well, so verification still rejects a wrong
    version instead of preparation silently hiding it.
    """
    validate_version(version)
    current_names = {
        f"UindosillDesktop-{version}{channel}-{kind}.nupkg"
        for channel in ("", "-win-cuda")
        for kind in ("full", "delta")
    }
    prepared = []
    for channel in ("win", "win-cuda"):
        name = f"releases.{channel}.json"
        path = root / name
        feed = json.loads(path.read_text(encoding="utf-8-sig"))
        if not isinstance(feed, dict):
            raise ValueError(f"{name}: expected an update feed object.")
        asset_keys = [key for key in feed if key.lower() == "assets"]
        if len(asset_keys) != 1 or not isinstance(feed[asset_keys[0]], list):
            raise ValueError(f"{name}: expected one update assets list.")
        entries = feed[asset_keys[0]]
        retained = []
        for entry in entries:
            if not isinstance(entry, dict):
                raise ValueError(f"{name}: expected an update asset object.")
            fields = {key.lower(): value for key, value in entry.items()}
            if not isinstance(fields.get("version"), str) or not fields["version"]:
                raise ValueError(f"{name}: update asset has no release version.")
            if not isinstance(fields.get("filename"), str) or not fields["filename"]:
                raise ValueError(f"{name}: update asset has no filename.")
            if fields["version"] == version or fields.get("filename") in current_names:
                retained.append(entry)
        feed[asset_keys[0]] = retained
        prepared.append((path, feed, {"name": name, "removed": len(entries) - len(retained),
                                      "retained": len(retained)}))

    # Parse both feeds before changing either, and finish before verify measures their bytes.
    for path, feed, _ in prepared:
        path.write_text(json.dumps(feed, indent=2) + "\n", encoding="utf-8")
    return [summary for _, _, summary in prepared]


def verify(root: Path, version: str, commit: str) -> dict:
    validate_version(version)
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


def self_check() -> bool:
    import tempfile
    import unittest

    class ReleaseAssetsTests(unittest.TestCase):
        version = "1.0.1"

        def setUp(self):
            scratch = tempfile.TemporaryDirectory(prefix="uindosill-release-assets-")
            self.addCleanup(scratch.cleanup)
            self.root = Path(scratch.name)
            self.seed()

        def package(self, version, channel, kind):
            suffix = "-win-cuda" if channel == "win-cuda" else ""
            name = f"UindosillDesktop-{version}{suffix}-{kind}.nupkg"
            payload = name.encode("utf-8")
            (self.root / name).write_bytes(payload)
            return {"FileName": name, "Version": version, "Type": kind.title(),
                    "Size": len(payload), "SHA1": hashlib.sha1(payload).hexdigest(),
                    "SHA256": hashlib.sha256(payload).hexdigest(),
                    "NotesMarkdown": "Preserve the original package metadata."}

        def seed(self):
            for name in ("UindosillDesktop-win-Setup.exe", "UindosillDesktop-win-cuda-Setup.exe",
                         "uindosill-cli-win-x64.zip", "uindosill-python-win-x64.zip"):
                (self.root / name).write_bytes(name.encode("utf-8"))
            for channel in ("win", "win-cuda"):
                entries = [self.package("1.0.0", channel, "full"),
                           self.package(self.version, channel, "full"),
                           self.package(self.version, channel, "delta")]
                self.write_feed(channel, {"Assets": entries, "FeedMetadata": {"keep": True}})

        def read_feed(self, channel="win"):
            return json.loads((self.root / f"releases.{channel}.json").read_text(encoding="utf-8"))

        def write_feed(self, channel, feed):
            (self.root / f"releases.{channel}.json").write_text(
                json.dumps(feed) + "\n", encoding="utf-8")

        def test_default_verification_rejects_predecessors_without_writing(self):
            before = {path.name: path.read_bytes() for path in self.root.glob("releases.*.json")}
            with self.assertRaisesRegex(ValueError, "not a current package"):
                verify(self.root, self.version, "test-commit")
            self.assertEqual(before, {path.name: path.read_bytes()
                                      for path in self.root.glob("releases.*.json")})

        def test_seeded_predecessors_are_removed_after_delta_generation(self):
            before = {channel: self.read_feed(channel) for channel in ("win", "win-cuda")}
            summaries = prepare_feeds(self.root, self.version)
            self.assertEqual([1, 1], [item["removed"] for item in summaries])
            self.assertEqual([2, 2], [item["retained"] for item in summaries])
            report = verify(self.root, self.version, "test-commit")
            self.assertEqual("test-commit", report["commit"])
            self.assertEqual(10, len(report["assets"]))
            for channel in ("win", "win-cuda"):
                expected = before[channel]
                predecessor_name = expected["Assets"][0]["FileName"]
                expected["Assets"] = expected["Assets"][1:]
                self.assertEqual(expected, self.read_feed(channel))
                # The seed remains available as a packaging input, but is not a release asset.
                self.assertTrue((self.root / predecessor_name).is_file())
                feed_name = f"releases.{channel}.json"
                measured = next(item for item in report["assets"] if item["name"] == feed_name)
                self.assertEqual(hashlib.sha256((self.root / feed_name).read_bytes()).hexdigest(),
                                 measured["sha256"])
            self.assertFalse(any("1.0.0" in asset["name"] for asset in report["assets"]))

        def test_first_release_does_not_require_deltas(self):
            for channel in ("win", "win-cuda"):
                feed = self.read_feed(channel)
                for entry in (feed["Assets"][0], feed["Assets"][2]):
                    (self.root / entry["FileName"]).unlink()
                feed["Assets"] = [feed["Assets"][1]]
                self.write_feed(channel, feed)
            prepare_feeds(self.root, self.version)
            report = verify(self.root, self.version, "test-commit")
            self.assertEqual(8, len(report["assets"]))
            self.assertTrue(all(len(feed["packages"]) == 1 for feed in report["feeds"]))

        def test_malformed_current_rows_are_not_hidden(self):
            mutations = (
                ({"FileName": "unexpected-current.nupkg"}, "not a current package"),
                ({"FileName": f"UindosillDesktop-{self.version}-win-cuda-full.nupkg"},
                 "not a current package"),
                ({"Version": "1.0.0"}, "another release version"),
                ({"Type": "Delta"}, "wrong package type"),
                ({"Size": 1}, "different byte count"),
                ({"SHA1": "0" * 40}, "different SHA1"),
                ({"SHA256": "0" * 64}, "different SHA256"),
                ({"SHA1": None, "SHA256": None}, "no digest"),
            )
            for fields, message in mutations:
                with self.subTest(fields=fields):
                    self.seed()
                    feed = self.read_feed()
                    feed["Assets"][1].update(fields)
                    self.write_feed("win", feed)
                    prepare_feeds(self.root, self.version)
                    with self.assertRaisesRegex(ValueError, message):
                        verify(self.root, self.version, "test-commit")

        def test_current_full_package_is_mandatory(self):
            feed = self.read_feed()
            feed["Assets"].pop(1)
            self.write_feed("win", feed)
            prepare_feeds(self.root, self.version)
            with self.assertRaisesRegex(ValueError, "current full package is missing"):
                verify(self.root, self.version, "test-commit")

        def test_duplicate_current_packages_are_rejected(self):
            feed = self.read_feed()
            feed["Assets"].append(dict(feed["Assets"][1]))
            self.write_feed("win", feed)
            prepare_feeds(self.root, self.version)
            with self.assertRaisesRegex(ValueError, "duplicate package"):
                verify(self.root, self.version, "test-commit")

        def test_invalid_second_feed_does_not_partially_prepare_first(self):
            self.write_feed("win-cuda", {"Assets": [{"FileName": "unversioned.nupkg"}]})
            before = {path.name: path.read_bytes() for path in self.root.glob("releases.*.json")}
            with self.assertRaisesRegex(ValueError, "no release version"):
                prepare_feeds(self.root, self.version)
            self.assertEqual(before, {path.name: path.read_bytes()
                                      for path in self.root.glob("releases.*.json")})

        def test_invalid_version_does_not_prepare_feeds(self):
            before = (self.root / "releases.win.json").read_bytes()
            with self.assertRaisesRegex(ValueError, "Expected a release version"):
                prepare_feeds(self.root, "../1.0.1")
            self.assertEqual(before, (self.root / "releases.win.json").read_bytes())

    suite = unittest.defaultTestLoader.loadTestsFromTestCase(ReleaseAssetsTests)
    return unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--commit")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--prepare-feeds", action="store_true",
                        help="After delta generation, remove predecessor rows before checking and hashing.")
    parser.add_argument("--self-check", action="store_true", help="Run isolated release-feed regressions.")
    args = parser.parse_args()
    if args.self_check:
        return 0 if self_check() else 1
    if any(getattr(args, name) is None for name in ("root", "version", "commit", "output")):
        parser.error("--root, --version, --commit and --output are required unless --self-check is used")
    if args.prepare_feeds:
        for feed in prepare_feeds(args.root, args.version):
            print(f'{feed["name"]}: retained {feed["retained"]} current rows; removed '
                  f'{feed["removed"]} predecessor rows.')
    report = verify(args.root, args.version, args.commit)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    for asset in report["assets"]:
        print(f'{asset["name"]}: {asset["sizeBytes"]} bytes; SHA256 {asset["sha256"]}')
    print(f'Checked {len(report["assets"])} assets and both update feeds.')
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
