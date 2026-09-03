#!/usr/bin/env python3
"""Check that the machine-readable ppy package manifest matches the project."""

from __future__ import annotations

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def fail(message: str) -> None:
    print(f"pin check failed: {message}", file=sys.stderr)
    raise SystemExit(1)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()

    root = args.root.resolve()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    global_config = json.loads((root / "global.json").read_text(encoding="utf-8"))
    expected_sdk = manifest.get("dotnetSdk")
    actual_sdk = global_config.get("sdk", {}).get("version")
    if expected_sdk != actual_sdk:
        fail(f"manifest SDK {expected_sdk!r} does not match global.json {actual_sdk!r}")

    declared: dict[str, dict[str, object]] = {}
    for project in root.glob("src/**/*.csproj"):
        tree = ET.parse(project)
        for reference in tree.findall(".//PackageReference"):
            package_id = reference.get("Include") or reference.get("Update")
            version = reference.get("Version")
            if package_id and package_id.startswith("ppy."):
                if not version:
                    fail(f"{project.relative_to(root)} leaves {package_id} unpinned")
                project_path = project.relative_to(root).as_posix()
                existing = declared.setdefault(package_id, {"version": version, "usedBy": []})
                if existing["version"] != version:
                    fail(
                        f"{package_id} uses both {existing['version']} and {version}; "
                        "all consumers must use one exact version"
                    )
                used_by = existing["usedBy"]
                assert isinstance(used_by, list)
                used_by.append(project_path)

    recorded: dict[str, dict[str, object]] = {}
    for package in manifest.get("packages", []):
        recorded[package["id"]] = {
            "version": package["version"],
            "usedBy": sorted(package["usedBy"]),
        }

    for package in declared.values():
        used_by = package["usedBy"]
        assert isinstance(used_by, list)
        used_by.sort()

    if declared != recorded:
        fail(f"manifest packages {recorded!r} do not match project packages {declared!r}")

    print(f"Verified {len(recorded)} pinned ppy packages and .NET SDK {actual_sdk}.")


if __name__ == "__main__":
    main()
