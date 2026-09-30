import json
import os
import sys
import tempfile
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)

from mapport import archive, bsp, cli, geometry as g, kovaaks_json, materials, scenario, scene, vmf  # noqa: E402
import synthetic  # noqa: E402


def convert(data: bytes, **kw):
    sc = bsp.load(data, "synthetic", **kw)
    table = materials.load_table()
    slots, tex_slot = materials.allocate(sc, table, 2)
    return sc, slots, tex_slot, kovaaks_json.build(sc, slots, tex_slot, 2, 1.0, 4.0)


class GeometryTests(unittest.TestCase):
    def test_box_faces(self):
        planes = []
        for axis in range(3):
            n = [0.0, 0.0, 0.0]
            n[axis] = 1.0
            planes += [(n[0], n[1], n[2], 10.0), (-n[0], -n[1], -n[2], 10.0)]
        faces = g.brush_faces(planes)
        self.assertTrue(all(f and len(f) == 4 for f in faces))
        for pl, f in zip(planes, faces):
            nrm = g.normalize(g.cross(g.sub(f[1], f[0]), g.sub(f[2], f[0])))
            self.assertGreater(g.dot(nrm, pl[:3]), 0.99, "faces are counter-clockwise around the outward normal")


class BspTests(unittest.TestCase):
    def check(self, version, compress):
        data = synthetic.build_bsp(version=version, compress=compress)
        sc, slots, tex_slot, doc = convert(data)
        kinds = sorted(b.kind for b in sc.brushes if b.source != "displacement")
        self.assertEqual(kinds, [scene.CLIP, scene.SOLID])
        self.assertEqual(sc.stats.get("displacements"), 1)
        self.assertGreaterEqual(sc.stats.get("kept_displacement_slab", 0), 2)
        self.assertEqual(sorted(s.team for s in sc.spawns), [1, 2])
        return doc

    def test_versions(self):
        for version in (19, 20, 21):
            with self.subTest(version=version):
                self.check(version, False)

    def test_lzma_lumps(self):
        doc = self.check(21, True)
        self.assertTrue(any(o["type"] == "brush" for o in doc["objects"]))

    def test_decompress_roundtrip(self):
        raw = bytes(range(256)) * 20
        self.assertEqual(bsp.decompress_lump(synthetic._lzma_lump(raw)), raw)

    def test_rejects_other_versions(self):
        data = bytearray(synthetic.build_bsp())
        data[4] = 29
        with self.assertRaises(bsp.BspError):
            bsp.Bsp(bytes(data))

    def test_displacement_heights(self):
        sc, *_ = convert(synthetic.build_bsp())
        tops = [p[2] for b in sc.brushes if b.source == "displacement" for p in b.faces[0].polygon]
        self.assertAlmostEqual(max(tops), 24.0, places=3)  # middle row lifted by 8 above z=16
        self.assertAlmostEqual(min(tops), 16.0, places=3)


class JsonTests(unittest.TestCase):
    def setUp(self):
        self.sc, self.slots, self.tex_slot, self.doc = convert(synthetic.build_bsp(with_displacement=False))
        self.brushes = [o for o in self.doc["objects"] if o["type"] == "brush"]

    def vec(self, s):
        return tuple(float(x) for x in s.split(",")[:3])

    def test_structure(self):
        self.assertEqual(self.doc["version"], "1.0.0")
        self.assertEqual(len(self.doc["materialSets"]), 3)
        solid = next(o for o in self.brushes if o["name"] == "Default")
        clip = next(o for o in self.brushes if o["name"] == "Clip")
        self.assertEqual(len(solid["procedural"]), 6)
        self.assertEqual(len(solid["materialSets"]), 6)
        self.assertNotIn("materialSets", clip)
        for ms in solid["materialSets"]:
            self.assertIn(ms["surface"], materials.SURFACES)
            self.assertIn(ms["group"], (0, 1))

    def test_editor_winding(self):
        for o in self.brushes:
            for sec in o["procedural"]:
                v = [self.vec(x["location"]) for x in sec["vertices"]]
                n = self.vec(sec["vertices"][0]["normal"])
                i0, i1, i2 = sec["indices"][:3]
                c = g.cross(g.sub(v[i1], v[i0]), g.sub(v[i2], v[i0]))
                self.assertLess(g.dot(c, n), 0, "editor winding: triangle normal opposes vertex normal")

    def test_not_mirrored(self):
        # The floor spans y -32..96 in Source; Unreal mirrors Y, so it must span -96..32.
        solid = next(o for o in self.brushes if o["name"] == "Default")
        loc = self.vec(solid["location"])
        # Procedural vertices are multiplied by MapScale (4) on load, on top of the location.
        ys = [loc[1] + 4.0 * self.vec(x["location"])[1] for s in solid["procedural"] for x in s["vertices"]]
        self.assertAlmostEqual(min(ys), -96.0, places=2)
        self.assertAlmostEqual(max(ys), 32.0, places=2)
        # Facing Source north (+Y) is Unreal yaw -90; its right vector (yaw 0) is +X: east stays on the right.
        m = [kovaaks_json.to_ue(e, 1.0) for e in ((1, 0, 0), (0, 1, 0), (0, 0, 1))]
        self.assertEqual(m[0], (1.0, 0.0, 0.0))
        self.assertEqual(m[1], (0.0, -1.0, 0.0))

    def test_vertex_scale_matches_editor(self):
        # The editor stores a 100-unit cube as 100 / MapScale; so a 128 x 128 x 16 floor at MapScale 4
        # must be stored as 32 x 32 x 4.
        solid = next(o for o in self.brushes if o["name"] == "Default")
        pts = [self.vec(x["location"]) for s in solid["procedural"] for x in s["vertices"]]
        ext = [round(max(p[k] for p in pts) - min(p[k] for p in pts), 3) for k in range(3)]
        self.assertEqual(ext, [32.0, 32.0, 4.0])

    def test_spawns(self):
        spawns = [o for o in self.doc["objects"] if o["type"] == "gameObject"]
        masks = sorted(next(p["value"] for p in s["properties"] if p["name"] == "TeamMask") for s in spawns)
        self.assertEqual(masks, [1, 2])
        t = next(s for s in spawns if s["properties"][1]["value"] == 1)
        self.assertEqual(self.vec(t["rotation"]), (0.0, 0.0, -90.0))
        self.assertEqual(self.vec(t["scale"]), (0.25, 0.25, 0.25))

    def test_dumps_is_json(self):
        self.assertEqual(json.loads(kovaaks_json.dumps(self.doc)), self.doc)


class SpawnTests(unittest.TestCase):
    def test_nudged_out_of_brush(self):
        from mapport import spawns
        sc = bsp.load(synthetic.build_bsp(with_displacement=False), "s")
        sc.spawns = [scene.Spawn(origin=(16.0, 16.0, 16.0), yaw=0.0, team=1)]  # inside the clip cube
        spawns.fix_spawns(sc)
        self.assertEqual(sc.stats.get("spawns_nudged"), 1)
        solids = spawns._solids(sc)
        self.assertIsNone(spawns.blocked(spawns.hull_box(sc.spawns[0].origin), solids))

    def test_free_spawn_untouched(self):
        from mapport import spawns
        sc = bsp.load(synthetic.build_bsp(with_displacement=False), "s")
        before = [s.origin for s in sc.spawns]
        spawns.fix_spawns(sc)
        self.assertEqual([s.origin for s in sc.spawns], before)


class ReflexTests(unittest.TestCase):
    def test_axes_match_json(self):
        # KovaaK's loads Reflex (a, b, c) as Unreal (c, a, b); both writers must agree.
        from mapport import reflex
        p = (10.0, 20.0, 30.0)
        a, b, c = reflex.to_reflex(p, 1.0)
        self.assertEqual((c, a, b), kovaaks_json.to_ue(p, 1.0))


class MaterialTests(unittest.TestCase):
    def test_rules(self):
        table = materials.load_table()
        self.assertEqual(materials.rule_for("brick/brickwall001a", table)["category"], "brick")
        self.assertEqual(materials.rule_for("de_dust/sandwall01", table)["category"], "sand")
        self.assertEqual(materials.rule_for("nature/blendgrassdirt", table)["category"], "grass")
        self.assertEqual(materials.rule_for("something/unknown", table)["category"], "concrete")

    def test_slot_cap(self):
        faces_per = []
        names = ["brick/a", "wood/b", "metal/c", "concrete/d", "sand/e", "grass/f", "tile/g", "stone/h",
                 "plaster/i", "dirt/j", "dev/dev_k", "fabric/cloth_l"]
        sq = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]
        for i, n in enumerate(names):
            faces_per.append(scene.Brush(faces=[scene.Face(polygon=sq, normal=(0, 0, 1), texture=n,
                                                           reflectivity=(0.1 * (i % 5), 0.3, 0.3))] * 4))
        sc = scene.Scene(name="x", brushes=faces_per)
        slots, tex_slot = materials.allocate(sc, materials.load_table(), 2)
        self.assertLessEqual(len(slots), 8)
        self.assertEqual(set(tex_slot), set(names))
        self.assertEqual(len({(s.group, s.surface) for s in slots}), len(slots))


class ArchiveTests(unittest.TestCase):
    def test_gma_and_zip(self):
        data = synthetic.build_bsp()
        with tempfile.TemporaryDirectory() as tmp:
            gma = os.path.join(tmp, "addon.gma")
            with open(gma, "wb") as fh:
                fh.write(synthetic.build_gma([("maps/test_map.bsp", data), ("materials/x.vmt", b"x")]))
            found = archive.extract(gma, os.path.join(tmp, "g"))
            self.assertEqual([os.path.basename(p) for p in found], ["test_map.bsp"])
            with open(found[0], "rb") as fh:
                self.assertEqual(fh.read(), data)
            zp = os.path.join(tmp, "m.zip")
            with zipfile.ZipFile(zp, "w") as z:
                z.writestr("inner/aim_x.bsp", data)
            found = archive.extract(zp, os.path.join(tmp, "z"))
            self.assertEqual([os.path.basename(p) for p in found], ["aim_x.bsp"])

    def test_zip_slip(self):
        with tempfile.TemporaryDirectory() as tmp:
            zp = os.path.join(tmp, "bad.zip")
            with zipfile.ZipFile(zp, "w") as z:
                z.writestr("../evil.bsp", b"x")
            with self.assertRaises(archive.ArchiveError):
                archive.extract(zp, os.path.join(tmp, "out"))


class VmfTests(unittest.TestCase):
    def test_box(self):
        sc = vmf.load(synthetic.VMF_BOX, "vmfbox")
        self.assertEqual(len(sc.brushes), 1)
        b = sc.brushes[0]
        self.assertEqual(len(b.faces), 6)
        lo, hi = b.bounds()
        self.assertEqual(tuple(round(x) for x in lo), (-64, -64, 0))
        self.assertEqual(tuple(round(x) for x in hi), (64, 64, 64))
        self.assertEqual(len(sc.spawns), 1)


class ScenarioTests(unittest.TestCase):
    def test_profile_units(self):
        text = scenario.build("Test", "t.json", '{"objects": []}', 4.0, scenario.PRESETS["cs"])
        kv = {}
        for line in text.split("\r\n"):
            if "=" in line and not line.startswith("{"):
                k, v = line.split("=", 1)
                kv.setdefault(k, v)
        self.assertEqual(kv["MapScale"], "4.0")
        self.assertEqual(kv["MaxSpeed"], "1000.0")
        self.assertEqual(kv["StepUpHeight"], "72.0")
        self.assertEqual(kv["EnableQuakeMovement"], "true")
        self.assertEqual(kv["ScaledGroundAcceleration"], "5.20")
        self.assertEqual(kv["ContinuousGroundFriction"], "4.00")
        jump = float(kv["JumpVelocityMax"])
        grav = float(kv["Gravity"]) * scenario.UE_GRAVITY
        self.assertAlmostEqual(jump ** 2 / (2 * grav) / 4.0, 57.0, places=1)
        self.assertTrue(text.rstrip().endswith('{"objects": []}'))
        self.assertIn("[Map Data]", text)


class CliTests(unittest.TestCase):
    def test_end_to_end(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(tmp, "aim_test.bsp")
            with open(src, "wb") as fh:
                fh.write(synthetic.build_bsp())
            out = os.path.join(tmp, "out")
            self.assertEqual(cli.main([src, "--out", out, "--format", "both"]), 0)
            for rel in ("maps/aim_test.json", "maps/aim_test.map", "Scenarios/aim_test CS Movement.sce",
                        "aim_test.preview.png", "aim_test.report.json"):
                self.assertTrue(os.path.isfile(os.path.join(out, rel)), rel)


if __name__ == "__main__":
    unittest.main()
