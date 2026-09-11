import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("release_notes", ROOT / "scripts/validate-release-notes.py")
notes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notes)


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


if __name__ == "__main__":
    unittest.main()
