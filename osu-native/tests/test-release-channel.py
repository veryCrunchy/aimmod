#!/usr/bin/env python3

from __future__ import annotations

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "create-release-channel.py"


class ReleaseChannelManifestTests(unittest.TestCase):
    def test_creates_stable_manifest_with_verified_assets(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            windows = root / "aimmod-osu-1.2.3-win-x64.zip"
            linux = root / "aimmod-osu-1.2.3-linux-x64.tar.gz"
            windows.write_bytes(b"windows")
            linux.write_bytes(b"linux")
            windows_installer = root / "AimMod.Osu-win-stable-Setup.exe"
            linux_installer = root / "AimMod.Osu-linux-stable.AppImage"
            windows_installer.write_bytes(b"windows-installer")
            linux_installer.write_bytes(b"linux-installer")
            output = root / "aimmod-osu-stable.json"

            completed = self.run_script(
                "--repository", "veryCrunchy/aimmod",
                "--tag", "aimmod-osu-v1.2.3",
                "--version", "1.2.3",
                "--channel", "stable",
                "--commit", "a" * 40,
                "--published-at", "2026-09-04T12:30:00Z",
                "--pins", str(ROOT / "packaging" / "ppy-packages.json"),
                "--asset", f"windows,win-x64,zip,{windows}",
                "--asset", f"linux,linux-x64,tar.gz,{linux}",
                "--installer", f"windows,win-x64,exe,{windows_installer}",
                "--installer", f"linux,linux-x64,AppImage,{linux_installer}",
                "--output", str(output),
            )

            self.assertEqual(0, completed.returncode, completed.stderr)
            manifest = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("stable", manifest["channel"])
            self.assertEqual("1.2.3", manifest["version"])
            self.assertEqual("a" * 40, manifest["commitSha"])
            self.assertEqual("2026-09-04T12:30:00Z", manifest["publishedAt"])
            self.assertEqual("8.0.424", manifest["build"]["dotnetSdk"])
            self.assertEqual("win-stable", manifest["updateFeeds"]["windows"]["channel"])
            self.assertEqual(["linux-x64", "win-x64"], [item["runtimeIdentifier"] for item in manifest["installers"]])
            self.assertTrue(all(item["supportsInAppUpdates"] for item in manifest["installers"]))
            self.assertEqual(["linux-x64", "win-x64"], [item["runtimeIdentifier"] for item in manifest["assets"]])
            windows_asset = next(item for item in manifest["assets"] if item["runtimeIdentifier"] == "win-x64")
            self.assertEqual(hashlib.sha256(b"windows").hexdigest(), windows_asset["sha256"])
            self.assertTrue(windows_asset["downloadUrl"].endswith(windows.name))

    def test_rejects_prerelease_on_stable_channel(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            windows = root / "aimmod-osu-1.2.3-preview.1-win-x64.zip"
            linux = root / "aimmod-osu-1.2.3-preview.1-linux-x64.tar.gz"
            windows.touch()
            linux.touch()
            windows_installer = root / "AimMod.Osu-win-stable-Setup.exe"
            linux_installer = root / "AimMod.Osu-linux-stable.AppImage"
            windows_installer.touch()
            linux_installer.touch()

            completed = self.run_script(
                "--repository", "veryCrunchy/aimmod",
                "--tag", "aimmod-osu-v1.2.3-preview.1",
                "--version", "1.2.3-preview.1",
                "--channel", "stable",
                "--commit", "a" * 40,
                "--published-at", "2026-09-04T12:30:00Z",
                "--pins", str(ROOT / "packaging" / "ppy-packages.json"),
                "--asset", f"windows,win-x64,zip,{windows}",
                "--asset", f"linux,linux-x64,tar.gz,{linux}",
                "--installer", f"windows,win-x64,exe,{windows_installer}",
                "--installer", f"linux,linux-x64,AppImage,{linux_installer}",
                "--output", str(root / "channel.json"),
            )

            self.assertNotEqual(0, completed.returncode)
            self.assertIn("stable channel versions", completed.stderr)

    def test_requires_both_desktop_platforms(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            windows = root / "aimmod-osu-1.2.3-win-x64.zip"
            windows.touch()
            windows_installer = root / "AimMod.Osu-win-stable-Setup.exe"
            windows_installer.touch()

            completed = self.run_script(
                "--repository", "veryCrunchy/aimmod",
                "--tag", "aimmod-osu-v1.2.3",
                "--version", "1.2.3",
                "--channel", "stable",
                "--commit", "a" * 40,
                "--published-at", "2026-09-04T12:30:00Z",
                "--pins", str(ROOT / "packaging" / "ppy-packages.json"),
                "--asset", f"windows,win-x64,zip,{windows}",
                "--installer", f"windows,win-x64,exe,{windows_installer}",
                "--output", str(root / "channel.json"),
            )

            self.assertNotEqual(0, completed.returncode)
            self.assertIn("linux-x64", completed.stderr)

    @staticmethod
    def run_script(*arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(SCRIPT), *arguments],
            capture_output=True,
            text=True,
            check=False,
        )


if __name__ == "__main__":
    unittest.main()
