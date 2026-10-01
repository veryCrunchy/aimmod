import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from aimmod_workshop import bundle, png  # noqa: E402
from aimmod_workshop.cli import main  # noqa: E402

TITLE = "AimMod - Dust2 (CSGO) - CS Movement"


def synthetic_png(width, height, seed=0):
    rows = [bytearray(((x * 7 + y * 13 + seed) % 256 if c == 0 else (x ^ y) % 256 if c == 1 else (x * y) % 256)
                      for x in range(width) for c in range(3)) for y in range(height)]
    return png.write((width, height, rows))


def port_folder(root, title=TITLE, embed=True, name_field=None, preview=(600, 900)):
    """A map-port --out folder as convert_file writes it (synthetic content)."""
    base = "aimmod_de_dust2_csgo"
    os.makedirs(os.path.join(root, "Scenarios"))
    os.makedirs(os.path.join(root, "maps"))
    os.makedirs(os.path.join(root, "Abilities"))
    sce = [f"Name={name_field or title}", "AimTypeTag=Clicking", "AimSubTypeTag=Dynamic",
           "SearchTags=Map port, Counter-Strike, Movement", "Description=Ported Source map.", "GameVersion=3.9.11", ""]
    if embed:
        sce += ["[Map Data]", "{}"]
    with open(os.path.join(root, "Scenarios", title + ".sce"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(sce) + "\n")
    with open(os.path.join(root, "maps", base + ".json"), "w") as fh:
        fh.write("{}")
    with open(os.path.join(root, "Abilities", "CS Walk.abilsprint"), "w") as fh:
        fh.write("x")
    files = {"map_json": f"maps/{base}.json", "scenario": f"Scenarios/{title}.sce", "ability": "Abilities/CS Walk.abilsprint"}
    if preview:
        with open(os.path.join(root, base + ".preview.png"), "wb") as fh:
            fh.write(synthetic_png(*preview))
        files["preview"] = base + ".preview.png"
    report = {"input": "de_dust2.bsp", "name": base, "scenario": title, "spawns": {"T": 5, "CT": 5, "any": 0}, "files": files}
    with open(os.path.join(root, base + ".report.json"), "w") as fh:
        json.dump(report, fh)
    return root


def text(path):
    with open(path, encoding="utf-8") as fh:
        return fh.read()


def load(path):
    return json.loads(text(path))


def data(path):
    with open(path, "rb") as fh:
        return fh.read()


class WorkshopTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="aimmod-workshop-")
        self.port = os.path.join(self.tmp, "port")
        self.out = os.path.join(self.tmp, "bundles")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_png_round_trip_and_fit(self):
        image = png.read(synthetic_png(40, 30, 5))
        self.assertEqual(image[:2], (40, 30))
        self.assertEqual(png.read(png.write(image)), image)
        small = png.fit(image, 10)
        self.assertEqual(small[:2], (10, 8))
        self.assertEqual(png.fit(image, 100), image)
        with self.assertRaises(png.PngError):
            png.read(b"GIF89a")

    def test_bundle_contents(self):
        port_folder(self.port)
        made = bundle.prepare(self.port, self.out, source=bundle.Source("Synthetic Author", "https://example.invalid/dust2", "Ported with permission", ["Playtesting: synthetic tester"]))
        self.assertEqual(made, [os.path.join(self.out, TITLE)])
        b = made[0]
        self.assertEqual(os.listdir(os.path.join(b, "content")), [TITLE + ".sce"])
        self.assertTrue(os.path.exists(os.path.join(b, "extras", "maps", "aimmod_de_dust2_csgo.json")))
        self.assertTrue(os.path.exists(os.path.join(b, "extras", "Abilities", "CS Walk.abilsprint")))
        item = load(os.path.join(b, "item.json"))
        self.assertEqual(item["appId"], 824270)
        self.assertEqual(item["title"], TITLE)
        self.assertEqual(item["visibility"], "private")
        self.assertIsNone(item["publishedFileId"])
        self.assertEqual(item["tags"], ["Clicking", "Dynamic", "Map port", "Counter-Strike", "Movement", "AimMod", "CS:GO"])
        self.assertTrue(item["checks"]["ready"])
        description = text(os.path.join(b, "description.txt"))
        for needle in ("[h1]" + TITLE, "de_dust2", "Counter-Strike: Global Offensive", "Synthetic Author",
                     "[url=https://example.invalid/dust2]", "Ported with permission", "Playtesting: synthetic tester", "leaderboard key"):
            self.assertIn(needle, description)
        self.assertNotIn("TODO", description)
        width, height, _ = png.read(data(os.path.join(b, "preview.png")))
        self.assertLessEqual(max(width, height), 1024)
        self.assertLess(os.path.getsize(os.path.join(b, "preview.png")), bundle.PREVIEW_MAX_BYTES)
        vdf = text(os.path.join(b, "workshop_item.vdf"))
        self.assertIn('"appid"\t\t"824270"', vdf)
        self.assertIn('"publishedfileid"\t\t"0"', vdf)
        self.assertIn('"visibility"\t\t"2"', vdf)
        self.assertIn("Never rename the scenario", text(os.path.join(b, "PUBLISH.txt")))

    def test_large_preview_is_scaled(self):
        port_folder(self.port, preview=(1400, 2100))
        b = bundle.prepare(self.port, self.out)[0]
        width, height, _ = png.read(data(os.path.join(b, "preview.png")))
        self.assertEqual(height, 1024)
        self.assertLess(os.path.getsize(os.path.join(b, "preview.png")), bundle.PREVIEW_MAX_BYTES)

    def test_missing_attribution_is_flagged(self):
        port_folder(self.port, preview=None)
        b = bundle.prepare(self.port, self.out)[0]
        item = load(os.path.join(b, "item.json"))
        self.assertFalse(item["checks"]["ready"])
        self.assertTrue(any("authors" in t for t in item["checks"]["todo"]))
        self.assertTrue(any("preview" in t for t in item["checks"]["todo"]))
        self.assertIsNone(item["previewFile"])
        self.assertIn("TODO: name the original map authors", text(os.path.join(b, "description.txt")))
        self.assertNotIn("previewfile", text(os.path.join(b, "workshop_item.vdf")))

    def test_update_and_map_files_in_content(self):
        port_folder(self.port)
        b = bundle.prepare(self.port, self.out, published_file_id="3333089221", change_note="Fixed spawns.",
                           visibility="public", include_map_files=True)[0]
        item = load(os.path.join(b, "item.json"))
        self.assertEqual((item["publishedFileId"], item["changeNote"], item["visibility"]), ("3333089221", "Fixed spawns.", "public"))
        self.assertTrue(os.path.exists(os.path.join(b, "content", "maps", "aimmod_de_dust2_csgo.json")))
        self.assertIn('"publishedfileid"\t\t"3333089221"', text(os.path.join(b, "workshop_item.vdf")))

    def test_names_are_leaderboard_keys(self):
        port_folder(self.port, name_field="AimMod - Dust2 (CSGO) - Renamed")
        with self.assertRaises(bundle.BundleError):
            bundle.prepare(self.port, self.out)

    def test_rejects_unembedded_map_and_bad_input(self):
        port_folder(self.port, embed=False)
        with self.assertRaises(bundle.BundleError):
            bundle.prepare(self.port, self.out)
        with self.assertRaises(bundle.BundleError):
            bundle.prepare(self.port, self.out, published_file_id="12; rm")
        with self.assertRaises(bundle.BundleError):
            bundle.prepare(self.port, self.out, visibility="everyone")
        empty = os.path.join(self.tmp, "empty")
        os.makedirs(empty)
        with self.assertRaises(bundle.BundleError):
            bundle.prepare(empty, self.out)

    def test_non_aimmod_title_rejected(self):
        port_folder(self.port, title="Dust2 port")
        with self.assertRaises(bundle.BundleError):
            bundle.prepare(self.port, self.out)

    def test_cli(self):
        port_folder(self.port)
        self.assertEqual(main(["prepare", self.port, "--out", self.out, "--source-author", "A", "--source-license", "OK"]), 0)
        self.assertEqual(main(["prepare", self.port, "--out", self.out, "--scenario", "AimMod - Nope (CSS) - CS Movement"]), 1)


if __name__ == "__main__":
    unittest.main()
