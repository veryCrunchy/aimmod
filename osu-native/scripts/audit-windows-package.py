#!/usr/bin/env python3
"""Validate and inventory an AimMod osu Windows release tree."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path


def fail(message: str) -> None:
    print(f"package audit failed: {message}", file=sys.stderr)
    raise SystemExit(1)


def digest(path: Path) -> str:
    checksum = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(chunk)
    return checksum.hexdigest()


def contains_marker(path: Path, markers: list[bytes]) -> bytes | None:
    overlap = max((len(marker) for marker in markers), default=1) - 1
    previous = b""
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            window = previous + chunk
            lowered = window.lower()
            for marker in markers:
                if marker in lowered:
                    return marker
            previous = window[-overlap:] if overlap else b""
    return None


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("package", type=Path)
    parser.add_argument("--policy", type=Path, required=True)
    parser.add_argument("--pins", type=Path, required=True)
    parser.add_argument("--inventory", type=Path)
    args = parser.parse_args()

    package = args.package.resolve()
    if not package.is_dir():
        fail(f"release tree does not exist: {package}")

    policy = json.loads(args.policy.read_text(encoding="utf-8"))
    pins = json.loads(args.pins.read_text(encoding="utf-8"))
    allowed_metadata = set(policy["allowedMetadataFiles"])
    allowed_roots = {value.casefold() for value in policy["allowedRoots"]}
    allowed_patterns = [re.compile(pattern, re.IGNORECASE) for pattern in policy["allowedFilePatterns"]]
    denied_fragments = [value.casefold() for value in policy["deniedPathFragments"]]
    denied_extensions = {value.casefold() for value in policy["deniedExtensions"]}
    denied_assemblies = {value.casefold() for value in policy["deniedAssemblyNames"]}
    denied_file_names = {value.casefold() for value in policy["deniedFileNames"]}
    markers = [value.casefold().encode("utf-8") for value in policy["forbiddenContentMarkers"]]

    entries: list[dict[str, object]] = []
    paths: set[str] = set()
    component_bytes = {"app": 0, "metadata": 0}
    inventory_path = args.inventory.resolve() if args.inventory else None

    for path in sorted(package.rglob("*"), key=lambda value: value.as_posix().casefold()):
        relative = path.relative_to(package).as_posix()
        if path.is_symlink():
            fail(f"symbolic links are not allowed: {relative}")
        if path.is_dir():
            continue
        if inventory_path and path.resolve() == inventory_path:
            continue
        if not path.is_file():
            fail(f"unsupported filesystem entry: {relative}")

        lowered = relative.casefold()
        root = relative.partition("/")[0].casefold()
        if "/" in relative and root not in allowed_roots:
            fail(f"path uses an unapproved release root: {relative}")
        if path.name.casefold() in denied_file_names:
            fail(f"denied file in {relative}")
        if path.name.casefold() in denied_assemblies:
            fail(f"denied osu assembly in {relative}")
        if any(fragment in lowered for fragment in denied_fragments):
            fail(f"denied path fragment in {relative}")
        if path.suffix.casefold() in denied_extensions:
            fail(f"denied frontend or debug extension in {relative}")
        if relative not in allowed_metadata and not any(pattern.fullmatch(relative) for pattern in allowed_patterns):
            fail(f"path is outside the release allowlist: {relative}")

        marker = contains_marker(path, markers)
        if marker is not None:
            fail(f"forbidden content marker {marker.decode('utf-8')!r} in {relative}")

        size = path.stat().st_size
        entries.append(
            {
                "path": relative,
                "bytes": size,
                "sha256": digest(path),
                "executable": path.suffix.casefold() == ".exe",
            }
        )
        component_bytes["app" if relative.startswith("app/") else "metadata"] += size
        paths.add(relative)

    missing = sorted(set(policy["requiredFiles"]) - paths)
    if missing:
        fail(f"required files are missing: {', '.join(missing)}")

    if pins.get("distribution") != policy.get("distribution"):
        fail("pin manifest and package policy name different distributions")

    logical_bytes = sum(int(entry["bytes"]) for entry in entries)
    inventory = {
        "schemaVersion": 1,
        "distribution": policy["distribution"],
        "runtimeIdentifier": "win-x64",
        "fileCount": len(entries),
        "logicalBytes": logical_bytes,
        "physicalBytes": logical_bytes,
        "componentLogicalBytes": component_bytes,
        "pinnedPackages": pins["packages"],
        "files": entries,
    }

    if inventory_path:
        try:
            inventory_path.relative_to(package)
        except ValueError:
            fail("inventory must be written inside the release tree")
        inventory_path.write_text(json.dumps(inventory, indent=2) + "\n", encoding="utf-8")

    print(
        f"Package audit passed: {inventory['fileCount']} files, "
        f"{inventory['physicalBytes']} bytes, no web, Tauri, KovaaK, or debug payloads."
    )


if __name__ == "__main__":
    main()
