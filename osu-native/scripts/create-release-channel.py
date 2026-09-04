#!/usr/bin/env python3
"""Create a deterministic AimMod osu release-channel manifest."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
from pathlib import Path
from urllib.parse import quote


VERSION_PATTERN = re.compile(
    r"^(?P<core>0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?P<prerelease>-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)
REPOSITORY_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
ASSET_PATTERN = re.compile(r"^(windows|linux),(win-x64|linux-x64|linux-arm64),(zip|tar\.gz),(.+)$")
INSTALLER_PATTERN = re.compile(r"^(windows|linux),(win-x64|linux-x64),(exe|AppImage),(.+)$")


def digest(path: Path) -> str:
    checksum = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(chunk)
    return checksum.hexdigest()


def parse_asset(value: str) -> tuple[str, str, str, Path]:
    match = ASSET_PATTERN.fullmatch(value)
    if match is None:
        raise argparse.ArgumentTypeError(
            "asset must be OPERATING_SYSTEM,RUNTIME_ID,FORMAT,PATH"
        )

    operating_system, runtime_id, archive_format, raw_path = match.groups()
    path = Path(raw_path).resolve()
    if not path.is_file():
        raise argparse.ArgumentTypeError(f"asset does not exist: {path}")
    return operating_system, runtime_id, archive_format, path


def parse_installer(value: str) -> tuple[str, str, str, Path]:
    match = INSTALLER_PATTERN.fullmatch(value)
    if match is None:
        raise argparse.ArgumentTypeError(
            "installer must be OPERATING_SYSTEM,RUNTIME_ID,FORMAT,PATH"
        )

    operating_system, runtime_id, package_format, raw_path = match.groups()
    path = Path(raw_path).resolve()
    if not path.is_file():
        raise argparse.ArgumentTypeError(f"installer does not exist: {path}")
    return operating_system, runtime_id, package_format, path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--channel", choices=("stable", "preview"), required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--published-at", required=True)
    parser.add_argument("--pins", type=Path, required=True)
    parser.add_argument("--asset", action="append", type=parse_asset, required=True)
    parser.add_argument("--installer", action="append", type=parse_installer, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    version_match = VERSION_PATTERN.fullmatch(args.version)
    if version_match is None:
        parser.error("version must be valid SemVer without a leading v")
    if not REPOSITORY_PATTERN.fullmatch(args.repository):
        parser.error("repository must be in OWNER/REPOSITORY form")
    if not re.fullmatch(r"[0-9a-f]{40}", args.commit):
        parser.error("commit must be a full lowercase Git commit SHA")
    try:
        published_at = dt.datetime.fromisoformat(args.published_at.replace("Z", "+00:00"))
    except ValueError:
        parser.error("published-at must be an ISO-8601 timestamp")
    if published_at.tzinfo is None:
        parser.error("published-at must include a timezone")
    published_at = published_at.astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z")
    if not args.pins.is_file():
        parser.error(f"pin manifest does not exist: {args.pins}")
    pins = json.loads(args.pins.read_text(encoding="utf-8"))
    if args.tag != f"aimmod-osu-v{args.version}":
        parser.error("tag must exactly match aimmod-osu-vVERSION")
    is_prerelease = version_match.group("prerelease") is not None
    if args.channel == "stable" and is_prerelease:
        parser.error("stable channel versions cannot contain a prerelease suffix")
    if args.channel == "preview" and not is_prerelease:
        parser.error("preview channel versions require a prerelease suffix")

    seen_runtimes: set[str] = set()
    assets: list[dict[str, object]] = []
    release_base = f"https://github.com/{args.repository}/releases/download/{quote(args.tag, safe='')}"

    for operating_system, runtime_id, archive_format, path in sorted(args.asset, key=lambda item: item[1]):
        if runtime_id in seen_runtimes:
            parser.error(f"runtime identifier appears more than once: {runtime_id}")
        seen_runtimes.add(runtime_id)
        expected_fragment = f"-{args.version}-{runtime_id}."
        if expected_fragment not in path.name:
            parser.error(f"asset name must include {expected_fragment}: {path.name}")
        assets.append(
            {
                "operatingSystem": operating_system,
                "runtimeIdentifier": runtime_id,
                "architecture": runtime_id.rsplit("-", 1)[-1],
                "format": archive_format,
                "fileName": path.name,
                "size": path.stat().st_size,
                "sha256": digest(path),
                "downloadUrl": f"{release_base}/{quote(path.name, safe='')}",
                "entrypoint": "app/AimMod.exe" if operating_system == "windows" else "app/AimMod",
                "selfContained": True,
            }
        )

    required_runtimes = {"win-x64", "linux-x64"}
    if not required_runtimes.issubset(seen_runtimes):
        missing = ", ".join(sorted(required_runtimes - seen_runtimes))
        parser.error(f"required release assets are missing: {missing}")

    installer_runtimes: set[str] = set()
    installers: list[dict[str, object]] = []
    for operating_system, runtime_id, package_format, path in sorted(args.installer, key=lambda item: item[1]):
        if runtime_id in installer_runtimes:
            parser.error(f"installer runtime identifier appears more than once: {runtime_id}")
        expected_name = f"-{runtime_id.split('-', 1)[0]}-{args.channel}"
        if expected_name not in path.name:
            parser.error(f"installer name must identify its platform and channel ({expected_name}): {path.name}")
        installer_runtimes.add(runtime_id)
        installers.append(
            {
                "operatingSystem": operating_system,
                "runtimeIdentifier": runtime_id,
                "architecture": runtime_id.rsplit("-", 1)[-1],
                "format": package_format,
                "fileName": path.name,
                "size": path.stat().st_size,
                "sha256": digest(path),
                "downloadUrl": f"{release_base}/{quote(path.name, safe='')}",
                "supportsInAppUpdates": True,
            }
        )
    if installer_runtimes != required_runtimes:
        missing = ", ".join(sorted(required_runtimes - installer_runtimes))
        parser.error(f"required installers are missing: {missing}")

    document = {
        "schemaVersion": 1,
        "product": "aimmod-osu",
        "channel": args.channel,
        "version": args.version,
        "tag": args.tag,
        "publishedAt": published_at,
        "commitSha": args.commit,
        "releaseUrl": f"https://github.com/{args.repository}/releases/tag/{quote(args.tag, safe='')}",
        "build": {
            "dotnetSdk": pins["dotnetSdk"],
            "ppyVersion": pins["packages"][0]["version"],
        },
        "updateFeeds": {
            "windows": {
                "channel": f"win-{args.channel}",
                "url": f"https://github.com/{args.repository}/releases/download/aimmod-osu-{args.channel}/releases.win-{args.channel}.json",
            },
            "linux": {
                "channel": f"linux-{args.channel}",
                "url": f"https://github.com/{args.repository}/releases/download/aimmod-osu-{args.channel}/releases.linux-{args.channel}.json",
            },
        },
        "installers": installers,
        "assets": assets,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
