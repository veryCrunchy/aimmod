"""Run after NuGet restore: python tests/test-browser-runtime-policy.py."""

import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
from browser_runtime_policy import is_browser_runtime


class BrowserRuntimePolicyTests(unittest.TestCase):
    def test_only_unchanged_pinned_runtime_files_are_exempt(self):
        lock = json.loads((ROOT / "src/AimMod.Desktop/packages.lock.json").read_text())
        version = next(packages["Microsoft.Playwright"]["resolved"]
                       for packages in lock["dependencies"].values()
                       if "Microsoft.Playwright" in packages)
        cache = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
        source = cache / "microsoft.playwright" / version / ".playwright/package/cli.js"
        with tempfile.TemporaryDirectory(prefix="aimmod-browser-policy-") as directory:
            candidate = Path(directory) / "cli.js"
            shutil.copyfile(source, candidate)
            self.assertTrue(is_browser_runtime(candidate, "app/.playwright/package/cli.js"))
            self.assertFalse(is_browser_runtime(candidate, "app/frontend/cli.js"))
            self.assertFalse(is_browser_runtime(candidate, "app/.playwright-other/cli.js"))
            with self.assertRaises(ValueError):
                is_browser_runtime(candidate, "app/.playwright/package/unapproved.exe")
            candidate.write_text("modified helper")
            with self.assertRaises(ValueError):
                is_browser_runtime(candidate, "app/.playwright/package/cli.js")
            shutil.copyfile(cache / "microsoft.playwright" / version / "build/playwright.ps1", candidate)
            self.assertTrue(is_browser_runtime(candidate, "app/playwright.ps1"))
            candidate.write_text("modified setup script")
            with self.assertRaises(ValueError):
                is_browser_runtime(candidate, "app/playwright.ps1")


if __name__ == "__main__":
    unittest.main()
