"""Recognize only unchanged files from the restored, pinned browser helper."""

import hashlib
import json
import os
from pathlib import Path


def is_browser_runtime(path: Path, relative: str) -> bool:
    prefix = "app/.playwright/"
    setup_script = relative == "app/playwright.ps1"
    if not setup_script and not relative.startswith(prefix):
        return False
    lock = Path(__file__).resolve().parents[1] / "src/AimMod.Desktop/packages.lock.json"
    dependencies = json.loads(lock.read_text(encoding="utf-8"))["dependencies"]
    versions = {packages["Microsoft.Playwright"]["resolved"]
                for packages in dependencies.values() if "Microsoft.Playwright" in packages}
    if len(versions) != 1:
        raise ValueError("Browser helper must have exactly one pinned version")
    cache = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
    package = cache / "microsoft.playwright" / versions.pop()
    source = package / "build/playwright.ps1" if setup_script else package / ".playwright" / relative[len(prefix):]
    if not source.is_file() or source.is_symlink():
        raise ValueError(f"Unrecognized browser helper file: {relative}")
    def sha256(file: Path) -> bytes:
        with file.open("rb") as stream:
            digest = hashlib.sha256()
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
            return digest.digest()
    if sha256(path) != sha256(source):
        raise ValueError(f"Modified browser helper file: {relative}")
    return True
