#!/usr/bin/env python3
"""Create changelogs/VERSION.md for a release PR from unreleased.md or the release-please changelog."""

import importlib.util
import re
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("validate_release_notes", SCRIPTS / "validate-release-notes.py")
validation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(validation)

UNRELEASED_HEADING = "# Unreleased\n"
COMMIT_LINK = re.compile(r"\s*\(\[[0-9a-f]{7,40}\]\([^)]*\)\)")
ISSUE_LINK = re.compile(r"\s*\(\[#\d+\]\([^)]*\)\)|,?\s*closes \[#\d+\]\([^)]*\)", re.IGNORECASE)
SCOPE = re.compile(r"^(- )\*\*[^*]+:\*\*\s*")


def _body(markdown: str) -> str:
    lines = markdown.splitlines()
    if lines and lines[0].startswith("# "):
        lines = lines[1:]
    return "\n".join(lines).strip()


def _has_entries(body: str) -> bool:
    return any(line.startswith("- ") for line in body.splitlines())


def _generated_section(changelog: str, version: str) -> str:
    heading = re.compile(rf"^## \[?{re.escape(version)}\]?(?:\(|\s|$)")
    lines = changelog.splitlines()
    for start, line in enumerate(lines):
        if heading.match(line):
            section = []
            for entry in lines[start + 1:]:
                if entry.startswith("## "):
                    break
                if entry.startswith("### "):
                    entry = "## " + entry[4:]
                if entry.startswith("* "):
                    entry = "- " + entry[2:]
                entry = SCOPE.sub(r"\1", ISSUE_LINK.sub("", COMMIT_LINK.sub("", entry)))
                section.append(entry.rstrip())
            return "\n".join(section).strip()
    return ""


def prepare(version: str, root: Path) -> Path:
    changelogs = root / "changelogs"
    target = changelogs / f"{version}.md"
    if target.exists():
        return validation.validate(version, changelogs)

    unreleased = changelogs / "unreleased.md"
    curated = _body(unreleased.read_text(encoding="utf-8")) if unreleased.exists() else ""
    body = curated
    if not _has_entries(body):
        changelog = root / "CHANGELOG.md"
        body = _generated_section(changelog.read_text(encoding="utf-8"), version) if changelog.exists() else ""
    if not _has_entries(body):
        raise ValueError(f"No release notes for {version}: add entries to changelogs/unreleased.md")

    target.write_text(f"# AimMod {version}\n\n{body}\n", encoding="utf-8", newline="\n")
    try:
        validation.validate(version, changelogs)
    except ValueError:
        target.unlink()
        raise
    if _has_entries(curated):
        unreleased.write_text(UNRELEASED_HEADING, encoding="utf-8", newline="\n")
    return target


if __name__ == "__main__":
    try:
        if len(sys.argv) != 2:
            raise ValueError("Usage: prepare-release-notes.py VERSION")
        print(prepare(sys.argv[1], SCRIPTS.parent))
    except (ValueError, OSError) as error:
        sys.exit(str(error))
