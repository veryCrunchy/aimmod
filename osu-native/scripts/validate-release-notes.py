#!/usr/bin/env python3
"""Require concise, version-specific notes before packaging an AimMod release."""

import re
import sys
from pathlib import Path


def validate(version: str, directory: Path) -> Path:
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?", version):
        raise ValueError("Invalid release version")
    path = directory / f"{version}.md"
    if not path.is_file():
        raise ValueError(f"Add changelogs/{version}.md before releasing this version")
    content = path.read_text(encoding="utf-8")
    if not content.startswith(f"# AimMod {version}\n"):
        raise ValueError(f"Release notes must start with '# AimMod {version}'")
    if not any(line.startswith("- ") and len(line) > 12 for line in content.splitlines()):
        raise ValueError("Release notes need at least one description of a change")
    if len(content) > 24000 or len(content.splitlines()) > 300:
        raise ValueError("Keep release notes within 24000 characters and 300 lines")
    return path


if __name__ == "__main__":
    try:
        if len(sys.argv) != 2:
            raise ValueError("Usage: validate-release-notes.py VERSION")
        validate(sys.argv[1], Path(__file__).resolve().parents[1] / "changelogs")
    except (ValueError, OSError) as error:
        sys.exit(str(error))
