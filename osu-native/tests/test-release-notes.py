import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("release_notes", ROOT / "scripts/validate-release-notes.py")
notes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notes)
prepare_spec = importlib.util.spec_from_file_location("prepare_notes", ROOT / "scripts/prepare-release-notes.py")
prepare = importlib.util.module_from_spec(prepare_spec)
prepare_spec.loader.exec_module(prepare)


class ReleaseNotesTests(unittest.TestCase):
    def test_published_notes_are_valid(self):
        notes.validate("0.2.11", ROOT / "changelogs")

    def test_requires_notes_for_exact_version(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(ValueError):
                notes.validate("1.2.3", root)
            path = root / "1.2.3.md"
            for content in ["", "# AimMod 1.2.2\n- A useful change.", "# AimMod 1.2.3\n"]:
                path.write_text(content, encoding="utf-8")
                with self.assertRaises(ValueError):
                    notes.validate("1.2.3", root)
            path.write_text("# AimMod 1.2.3\n\n- A useful change for players.\n", encoding="utf-8")
            self.assertEqual(notes.validate("1.2.3", root), path)

    def test_rejects_path_traversal_and_oversized_notes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(ValueError):
                notes.validate("../../1.2.3", root)
            (root / "1.2.3.md").write_text("# AimMod 1.2.3\n- " + "x" * 25000, encoding="utf-8")
            with self.assertRaises(ValueError):
                notes.validate("1.2.3", root)


class PrepareReleaseNotesTests(unittest.TestCase):
    def _root(self, directory: str, unreleased: str, changelog: str = "") -> Path:
        root = Path(directory)
        (root / "changelogs").mkdir()
        (root / "changelogs/unreleased.md").write_text(unreleased, encoding="utf-8")
        if changelog:
            (root / "CHANGELOG.md").write_text(changelog, encoding="utf-8")
        return root

    def test_moves_curated_notes_and_resets_unreleased(self):
        with tempfile.TemporaryDirectory() as directory:
            root = self._root(directory, "# Unreleased\n\n## Replays\n\n- The whole playfield now fits the viewer.\n")
            path = prepare.prepare("1.4.0", root)
            self.assertEqual(path.read_text(encoding="utf-8"),
                             "# AimMod 1.4.0\n\n## Replays\n\n- The whole playfield now fits the viewer.\n")
            self.assertEqual((root / "changelogs/unreleased.md").read_text(encoding="utf-8"), "# Unreleased\n")

    def test_falls_back_to_generated_changelog_without_links(self):
        generated = ("# Changelog\n\n## [1.4.0](https://example.invalid/compare) (2026-10-01)\n\n### Features\n\n"
                     "* **osu:** show where each tap landed in trainer results ([abc1234](https://example.invalid/c))\n\n"
                     "## [1.3.0](https://example.invalid/compare) (2026-09-01)\n\n### Fixes\n\n* older entry\n")
        with tempfile.TemporaryDirectory() as directory:
            root = self._root(directory, "# Unreleased\n", generated)
            content = prepare.prepare("1.4.0", root).read_text(encoding="utf-8")
            self.assertEqual(content, "# AimMod 1.4.0\n\n## Features\n\n- show where each tap landed in trainer results\n")
            self.assertEqual((root / "changelogs/unreleased.md").read_text(encoding="utf-8"), "# Unreleased\n")

    def test_keeps_existing_notes_and_rejects_missing_notes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = self._root(directory, "# Unreleased\n\n- A newer change that is not part of this release.\n")
            existing = root / "changelogs/1.4.0.md"
            existing.write_text("# AimMod 1.4.0\n\n- Edited notes for this release.\n", encoding="utf-8")
            prepare.prepare("1.4.0", root)
            self.assertIn("Edited notes", existing.read_text(encoding="utf-8"))
            self.assertIn("newer change", (root / "changelogs/unreleased.md").read_text(encoding="utf-8"))
            with self.assertRaises(ValueError):
                prepare.prepare("1.5.0", self._root(tempfile.mkdtemp(), "# Unreleased\n"))


if __name__ == "__main__":
    unittest.main()
