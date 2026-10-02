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


class PropTests(unittest.TestCase):
    def test_phy_box(self):
        from mapport import props
        solids = props.parse_phy(synthetic.build_phy_box(16.0))
        self.assertEqual(len(solids), 1)
        pts = [p for tri in solids[0][0] for p in tri]
        for k in range(3):
            self.assertAlmostEqual(min(p[k] for p in pts), -16.0, places=3)
            self.assertAlmostEqual(max(p[k] for p in pts), 16.0, places=3)
        self.assertEqual(len(props.piece_planes(solids[0][0])), 6)

    def test_add_props(self):
        from mapport import props
        sc = scene.Scene(name="p")
        trim = [(x, 0, 0) for x in range(0, 65, 4)] + [(64, 8, 8), (500, 0, 0), (510, 10, 10)]
        files = {"models/crate.phy": synthetic.build_phy_box(16.0), "models/trim.vvd": synthetic.build_vvd(trim)}
        found = [{"model": "models/crate.mdl", "origin": (100.0, 0.0, 0.0), "angles": (0.0, 90.0, 0.0),
                  "solid": 6, "scale": 1.0},
                 {"model": "models/trim.mdl", "origin": (0.0, 0.0, 0.0), "angles": (0.0, 0.0, 0.0),
                  "solid": 0, "scale": 1.0},
                 {"model": "models/stock.mdl", "origin": (0.0, 0.0, 0.0), "angles": (0.0, 0.0, 0.0),
                  "solid": 6, "scale": 1.0}]
        props.add_props(sc, found, files)
        kinds = sorted(b.kind for b in sc.brushes)
        self.assertEqual(kinds, [scene.NONSOLID, scene.SOLID])
        trim = next(b for b in sc.brushes if b.kind == scene.NONSOLID)
        self.assertGreaterEqual(len(trim.faces), 12)  # two separate parts in one non-colliding object
        crate = next(b for b in sc.brushes if b.kind == scene.SOLID)
        lo, _hi = crate.bounds()
        self.assertAlmostEqual(lo[0], 84.0, places=2)
        self.assertEqual(sc.stats.get("props_not_packed"), 1)

    def test_name_standins_and_stair_ramps(self):
        from mapport import props
        self.assertEqual(props.name_dims("models/x/dust_crate_style_01_37x37x74.mdl"), (37.0, 37.0, 74.0))
        self.assertEqual(props.name_dims("models/x/dust_door_80x128_05.mdl"), (props.THIN, 80.0, 128.0))
        self.assertIsNone(props.name_dims("models/x/dust_stairs001_128.mdl"))
        sc = bsp.load(synthetic.build_bsp(with_displacement=False), "s")  # floor top at z=16
        crate = {"model": "models/x/crate_32x32x32.mdl", "origin": (0.0, 0.0, 16.0), "angles": (0.0, 0.0, 0.0),
                 "solid": 6, "scale": 1.0}
        stairs = {"model": "models/x/dust_stairs001_128.mdl", "origin": (16.0, 16.0, 16.0),
                  "angles": (0.0, 0.0, 0.0), "solid": 6, "scale": 1.0}
        clip = next(b for b in sc.brushes if b.kind == scene.CLIP)
        clip.faces[0].normal = (0.0, 0.7071, 0.7071)  # pretend the clip is a ramp
        props.add_props(sc, [crate, stairs], {})
        box = next(b for b in sc.brushes if b.source == "prop")
        lo, _hi = box.bounds()
        self.assertAlmostEqual(lo[2], 16.0, places=2)  # sits on the floor, not half buried
        self.assertEqual(sc.stats.get("stair_ramps_shown"), 1)
        self.assertEqual(clip.kind, scene.SOLID)

    def test_connected_parts(self):
        from mapport import props
        verts = [(0, 0, 0), (64, 0, 0), (0, 64, 0), (200, 0, 0), (264, 0, 0), (200, 64, 0), (300, 0, 0),
                 (301, 0, 0), (300, 1, 0)]
        parts = props.connected_parts(verts, [(0, 1, 2), (3, 4, 5), (6, 7, 8)])
        self.assertEqual(len(parts), 2)  # the 1-unit speck is dropped


class PropCollisionTests(unittest.TestCase):
    """Props collide like the model they show (see props.add_props and collision.py)."""

    def _prop(self, model, origin=(0.0, 0.0, 0.0), yaw=0.0, solid=6, **kw):
        return dict({"model": model, "origin": origin, "angles": (0.0, yaw, 0.0), "solid": solid, "scale": 1.0}, **kw)

    def test_phy_with_several_ledges(self):
        from mapport import props
        data = synthetic.build_phy_boxes([((0.0, 0.0, 8.0), 8.0), ((0.0, 0.0, 40.0), 8.0),
                                          ((0.0, 0.0, 24.0), 24.0, True)])
        pieces = props.parse_phy(data)[0]
        self.assertEqual(len(pieces), 2, "every ledge is read, not just the first; the tree's hull is not one")
        tops = sorted(max(p[2] for t in piece for p in t) for piece in pieces)
        self.assertAlmostEqual(tops[0], 16.0, places=3)
        self.assertAlmostEqual(tops[1], 48.0, places=3)

    def test_lz4_block(self):
        from mapport import source2
        out = bytearray(b"ab")  # earlier output is the dictionary
        # token: 3 literals, match length 4 + 3; literals "cde"; offset 5 (back to "a")
        source2.lz4_block(bytes([0x33]) + b"cde" + bytes([5, 0]) + bytes([0x10]) + b"!", out, 100)
        self.assertEqual(bytes(out), b"abcdeabcdeab!")

    def test_source2_hulls_and_meshes(self):
        from mapport import source2
        verts, tris = synthetic.box_mesh((0, 0, 0), (32, 32, 4))
        data = synthetic.build_vmdl_c([((-16, -16, 0), (16, 16, 48))], [(verts, tris)])
        shapes = source2.physics_shapes(data)
        self.assertEqual(len(shapes.hulls), 1)
        hverts, planes = shapes.hulls[0]
        self.assertEqual(len(hverts), 8)
        self.assertEqual(len(planes), 6)
        self.assertTrue(all(max(g.dot(p[:3], v) - p[3] for p in planes) <= 1e-4 for v in hverts))
        self.assertEqual(len(shapes.meshes), 1)
        self.assertEqual(len(shapes.meshes[0][1]), 12)
        debris = source2.physics_shapes(synthetic.build_vmdl_c([((0, 0, 0), (8, 8, 8))], group="debris"))
        self.assertFalse(debris)
        self.assertEqual(debris.nonsolid_pieces, 1)

    def test_stock_source2_model_collides(self):
        from mapport import props, vpk
        import struct
        data = synthetic.build_vmdl_c([((-16, -16, 0), (16, 16, 48))])
        with tempfile.TemporaryDirectory() as tmp:
            game = os.path.join(tmp, "steamapps", "common", "SomeSource2Game", "game", "csgo")
            os.makedirs(game)
            # the model moved into a sub-folder: found by its unique file name
            tree = b"vmdl_c\0models/props/x/moved\0crate\0" + struct.pack("<IHHIIH", 0, 0, 0x7FFF, 0, len(data), 0xFFFF) \
                + b"\0\0\0"
            with open(os.path.join(game, "pak01_dir.vpk"), "wb") as fh:
                fh.write(struct.pack("<III", 0x55AA1234, 1, len(tree)) + tree + data)
            stock = vpk.StockModels.discover(tmp)
            self.assertIsNotNone(stock)
            self.assertEqual(stock.source2_path("models/props/x/crate.mdl"), "models/props/x/moved/crate.vmdl_c")
            sc = scene.Scene(name="s2")
            props.add_props(sc, [self._prop("models/props/x/crate.mdl", (100.0, 0.0, 0.0), yaw=90.0)],
                            vpk.ChainFiles({}, stock))
            self.assertEqual([b.kind for b in sc.brushes], [scene.SOLID])
            lo, hi = sc.brushes[0].bounds()
            self.assertAlmostEqual(lo[0], 84.0, places=2)
            self.assertAlmostEqual(hi[2], 48.0, places=2)
            self.assertEqual(sc.stats.get("props_collision_source2"), 1)
            self.assertEqual(stock.used, {"models/props/x/crate.mdl": "SomeSource2Game/csgo"})

    def test_mesh_without_collision_model_keeps_openings(self):
        from mapport import props
        # an arch: two pillars and a lintel, one connected mesh, no .phy
        parts = [synthetic.box_mesh((0, 0, 0), (16, 16, 96)), synthetic.box_mesh((80, 0, 0), (96, 16, 96)),
                 synthetic.box_mesh((0, 0, 96), (96, 16, 112))]
        verts, tris = [], []
        for v, t in parts:
            tris += [(a + len(verts), b + len(verts), c + len(verts)) for a, b, c in t]
            verts += v
        tri_pts = [(verts[a], verts[b], verts[c]) for a, b, c in tris]
        shapes = props.solid_part_shapes(verts, tri_pts)
        self.assertGreater(len(shapes), 1, "a hollow part becomes boxes, not one hull")
        for kind, corners in shapes:
            lo = [min(p[k] for p in corners) for k in range(3)]
            hi = [max(p[k] for p in corners) for k in range(3)]
            self.assertFalse(all(lo[k] < (48, 8, 40)[k] < hi[k] for k in range(3)), "the opening stays open")
        cube = synthetic.box_mesh((0, 0, 0), (32, 32, 32))
        self.assertEqual(len(props.solid_part_shapes(cube[0], [tuple(cube[0][i] for i in t) for t in cube[1]])), 1)

    def test_solidity_flags(self):
        from mapport import props
        ents = [{"classname": "prop_physics_multiplayer", "model": "models/a.mdl", "spawnflags": "4"},
                {"classname": "prop_dynamic", "model": "models/b.mdl", "solid": "0"},
                {"classname": "prop_dynamic", "model": "models/c.mdl", "solid": "2"},
                {"classname": "prop_dynamic", "model": "models/d.mdl", "StartDisabled": "1"},
                {"classname": "prop_door_rotating", "model": "models/door.mdl", "angles": "0 90 0", "distance": "90"}]
        found = {p["model"]: p for p in props.entity_props(ents)}
        self.assertEqual(found["models/a.mdl"]["solid"], 0, "debris physics props don't collide")
        self.assertEqual(found["models/b.mdl"]["solid"], 0)
        self.assertEqual(found["models/c.mdl"]["solid"], props.SOLID_BBOX)
        self.assertNotIn("models/d.mdl", found)
        door = found["models/door.mdl"]
        self.assertEqual((door["solid"], door["angles"][1]), (0, 180.0), "doors open and stop colliding")
        # solid 2 collides as the model's bounding box
        files = {"models/c.phy": synthetic.build_phy_boxes([((0.0, 0.0, 8.0), 8.0), ((0.0, 0.0, 40.0), 8.0)])}
        sc = scene.Scene(name="bbox")
        props.add_props(sc, [self._prop("models/c.mdl", solid=props.SOLID_BBOX)], files)
        self.assertEqual(len(sc.brushes), 1)
        lo, hi = sc.brushes[0].bounds()
        self.assertAlmostEqual(hi[2] - lo[2], 48.0, places=2)
        sc = scene.Scene(name="nonsolid")
        props.add_props(sc, [self._prop("models/c.mdl", solid=0)], files)
        self.assertTrue(sc.brushes and all(b.kind == scene.NONSOLID and b.tag == "nonsolid" for b in sc.brushes))

    def test_crate_standins_collide_and_prop_clips_show(self):
        from mapport import props
        self.assertEqual(props.name_dims("models/props/de_dust/du_crate_64x64.mdl"), (64.0, 64.0, 64.0))
        sc = bsp.load(synthetic.build_bsp(with_displacement=False), "s")  # floor top at z=16
        clip = next(b for b in sc.brushes if b.kind == scene.CLIP)
        lo, hi = clip.bounds()
        inside = ((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2])
        props.add_props(sc, [self._prop("models/x/crate_32x32.mdl", (-40.0, 0.0, 16.0)),
                             self._prop("models/x/door_32x64.mdl", (52.0, 70.0, 16.0)),
                             self._prop("models/x/unknown_plant.mdl", inside)], {})
        kinds = {b.faces[0].texture: (b.kind, b.tag) for b in sc.brushes if b.source == "prop"}
        self.assertEqual(kinds["prop:models/x/crate_32x32.mdl"], (scene.SOLID, ""))
        self.assertEqual(kinds["prop:models/x/door_32x64.mdl"], (scene.NONSOLID, "standin"))
        self.assertEqual(clip.kind, scene.SOLID, "the clip around a missing prop is shown in its place")
        self.assertEqual(clip.faces[0].texture, "prop:models/x/unknown_plant.mdl")
        self.assertEqual(sc.missing_models, {"models/x/unknown_plant.mdl": 1}, "stand-ins are not missing")

    def test_floating_check_keeps_multi_piece_props(self):
        from mapport import checks, props
        sc = scene.Scene(name="f", brushes=[_box((-256, -256, -16), (256, 256, 0))])
        files = {"models/tall.phy": synthetic.build_phy_boxes([((0.0, 0.0, 8.0), 8.0), ((0.0, 0.0, 120.0), 8.0)])}
        props.add_props(sc, [self._prop("models/tall.mdl"), self._prop("models/tall.mdl", (0.0, 0.0, 400.0))], files)
        self.assertEqual(sum(b.source == "prop" for b in sc.brushes), 4)
        checks.remove_floating(sc)
        kept = [b for b in sc.brushes if b.source == "prop"]
        self.assertEqual(len(kept), 2, "the upper piece stays with its supported model; the floating copy goes")
        self.assertEqual({b.instance for b in kept}, {0})

    def test_stray_clips_and_audit(self):
        from mapport import collision
        sc = scene.Scene(name="c", brushes=[
            _box((-512, -512, -16), (512, 512, 0)),
            _box((0, 0, 0), (16, 64, 64), "tools/toolsplayerclip", scene.CLIP),        # against the floor
            _box((200, 200, 300), (232, 232, 340), "tools/toolsplayerclip", scene.CLIP),  # in mid-air
            _box((300, 0, 0), (320, 900, 200), "tools/toolsskybox", scene.CLIP),        # boundary
            _box((-100, 0, 0), (-80, 20, 30), "prop:models/x.mdl", scene.NONSOLID, "prop")])
        sc.brushes[-1].tag = ""
        sc.spawns = [scene.Spawn(origin=(0.0, 0.0, 50.0), yaw=0.0, team=1)]
        cats = sorted(c for _b, c in collision.classify_clips(sc))
        self.assertEqual(cats, ["boundary", "stairs_edges", "stray"])
        self.assertEqual(collision.remove_stray_clips(sc), 1)
        rep = collision.audit(sc)
        self.assertEqual(rep["invisible_collision"], {"boundary": 1, "stairs_edges": 1, "prop": 0, "stray": 0})
        self.assertEqual(rep["visible_no_collision_unexpected"], 1)
        self.assertEqual(rep["visible_no_collision"][0]["model"], "models/x.mdl")


class CsMapTests(unittest.TestCase):
    def _doc(self, n=5):
        sp = [{"team": "terrorist", "origin": [-700.0 + i, 800.0, 170.0], "yaw": 0.0} for i in range(n)] +              [{"team": "counter_terrorist", "origin": [300.0 + i, -2300.0, -90.0], "yaw": 90.0} for i in range(n)]
        return {"format": "aimmod.map-objectives", "map": "aimmod_de_test_css", "spawns": sp, "points": [], "zones": [
            {"type": "bomb_site", "team": "any", "name": "", "aabb": {"min": [-1728.0, -2864.0, 0.0], "max": [-1288.0, -2496.0, 96.0]}},
            {"type": "bomb_site", "team": "any", "name": "", "aabb": {"min": [1072.0, -2624.0, 96.0], "max": [1264.0, -2336.0, 192.0]}},
            {"type": "buy_zone", "team": "terrorist", "name": "", "aabb": {"min": [-1088.0, 632.0, 0.0], "max": [-384.0, 1016.0, 288.0]}}]}

    def test_dust2_like(self):
        from mapport import csmap
        cs = csmap.build(self._doc())
        a = next(s for s in cs["bomb_sites"] if s["name"] == "A")
        self.assertGreater(a["min"][0], 0, "A is the site farther from the T spawns (dust2)")
        self.assertEqual(len(cs["buy_zones"]["T"]), 1)
        self.assertEqual(len(cs["buy_zones"]["CT"]), 1, "a missing CT buy zone comes from the CT spawn area")
        self.assertIn("CT buy zone from the CT spawn area", cs["derived"])
        self.assertEqual(cs["problems"], [])
        self.assertEqual(len(cs["spawns"]["T"][0]), 4)

    def test_names_and_problems(self):
        from mapport import csmap
        doc = self._doc(4)
        doc["zones"][0]["name"] = "bombsite_b"
        doc["zones"][1]["name"] = "bombsite_a"
        cs = csmap.build(doc)
        self.assertEqual([s["name"] for s in cs["bomb_sites"]], ["A", "B"])
        self.assertLess(next(s for s in cs["bomb_sites"] if s["name"] == "B")["min"][0], 0, "targetnames win over position")
        self.assertIn("Fewer than 5 T spawns", cs["problems"])
        doc["zones"] = doc["zones"][2:]
        self.assertIn("No bomb sites", csmap.build(doc)["problems"])

    def test_goldsrc_buyzone_teams_and_points(self):
        from mapport import csmap
        doc = self._doc()
        doc["zones"] = [{"type": "buy_zone", "team": "terrorist", "name": "", "aabb": {"min": [0, 0, 0], "max": [10, 10, 10]}}]
        doc["points"] = [{"type": "bomb_target", "name": "", "origin": [1000.0, -2500.0, 100.0]},
                         {"type": "bomb_target", "name": "", "origin": [-1500.0, -2700.0, 0.0]}]
        cs = csmap.build(doc, goldsrc=True)
        self.assertEqual(len(cs["buy_zones"]["CT"]), 1, "GoldSrc team 2 is CT")
        self.assertIn("T buy zone from the T spawn area", cs["derived"])
        self.assertEqual(len(cs["bomb_sites"]), 2)
        self.assertIn("bomb sites from info_bomb_target points", cs["derived"])
        self.assertTrue(csmap.wanted("de_x", {}) and not csmap.wanted("aim_map", {"zones": [], "points": []}))


class SpawnFloorTests(unittest.TestCase):
    def test_spawns_stand_on_the_floor(self):
        from mapport import spawns
        sc = scene.Scene(name="s")
        # A floor slab with its top at z = 0 (a real convex box, so the downward ray finds it).
        lo, hi = (-512.0, -512.0, -32.0), (512.0, 512.0, 0.0)
        faces = []
        for axis in range(3):
            for sign, v in ((-1.0, lo[axis]), (1.0, hi[axis])):
                n = [0.0, 0.0, 0.0]; n[axis] = sign
                corner = list(lo if sign < 0 else hi)
                faces.append(scene.Face(polygon=[tuple(corner)] * 3, normal=tuple(n), texture="floor"))
        sc.brushes.append(scene.Brush(faces=faces))
        sc.spawns = [scene.Spawn(origin=(0.0, 0.0, 48.0), yaw=0.0, team=1), scene.Spawn(origin=(100.0, 0.0, 17.0), yaw=0.0, team=2)]
        self.assertEqual(spawns.floating_spawns(sc), 2, "origins 48 and 17 units up float")
        self.assertEqual(spawns.snap_to_floor(sc), 2)
        self.assertEqual([round(s.origin[2], 1) for s in sc.spawns], [0.5, 0.5])
        self.assertEqual(spawns.floating_spawns(sc), 0)


class BuriedLiquidTests(unittest.TestCase):
    def test_water_under_a_floor_is_dropped_pools_stay(self):
        from mapport import cleanup
        sc = scene.Scene(name="w")
        def slab(lo, hi):
            sc.brushes.append(scene.Brush(faces=[scene.Face(polygon=[lo, hi], normal=(0.0, 0.0, 1.0), texture="floor")]))
        # Buried: 400x400 water, top at 0; a floor slab from 0 to 16 over all of it.
        scene.add_liquid(sc, "water", [(-200.0, -200.0, -100.0), (200.0, 200.0, 0.0)])
        slab((-210.0, -210.0, 0.0), (210.0, 210.0, 16.0))
        # A pool far away, open to the air (walls around, nothing on top).
        scene.add_liquid(sc, "water", [(1000.0, 1000.0, -100.0), (1200.0, 1200.0, 0.0)])
        slab((990.0, 990.0, -100.0), (1000.0, 1210.0, 64.0))
        # A kill volume under everything is never touched.
        scene.add_liquid(sc, "hurt", [(-5000.0, -5000.0, -3000.0), (5000.0, 5000.0, -2000.0)], damage=1000.0)
        self.assertEqual(cleanup.remove_buried_liquids(sc), 1)
        kept = sorted((go["kind"], round(go["origin"][0])) for go in sc.gameobjects)
        self.assertEqual(kept, [("hurt", 0), ("water", 1100)])


class ObjectiveTests(unittest.TestCase):
    def test_zones_points_items(self):
        from mapport import objectives
        sc = scene.Scene(name="o")
        site = {"classname": "func_bomb_target", "targetname": "bombsite_a"}
        sc.volumes.append((site, [(0.0, 0.0, 0.0), (128.0, 64.0, 32.0)]))
        sc.entities = [{"classname": "info_bomb_target", "origin": "10 20 0"},
                       {"classname": "weapon_ak47", "origin": "1 2 3", "angles": "0 90 0"},
                       {"classname": "item_teamflag", "origin": "0 0 0", "teamnum": "2"}]
        sc.spawns = [scene.Spawn(origin=(5.0, 6.0, 0.0), yaw=90.0, team=2, classname="info_player_counterterrorist")]
        doc = objectives.build(sc, "o", 4.0)
        self.assertEqual(doc["format"], objectives.FORMAT)
        self.assertEqual(doc["version"], 1)
        z = doc["zones"][0]
        self.assertEqual((z["type"], z["name"]), ("bomb_site", "bombsite_a"))
        self.assertEqual(z["aabb"], {"min": [0.0, -64.0, 0.0], "max": [128.0, 0.0, 32.0]})  # Y mirrored
        self.assertEqual({p["type"] for p in doc["points"]}, {"bomb_target", "flag"})
        self.assertEqual(doc["items"][0]["classname"], "weapon_ak47")
        self.assertEqual(doc["spawns"][0]["team"], "counter_terrorist")


class MovementTests(unittest.TestCase):
    def test_presets_have_shift_modes(self):
        for name, mv in scenario.PRESETS.items():
            self.assertIn(mv.shift, ("walk", "sprint"), name)
        self.assertEqual(scenario.PRESETS["cs"].shift, "walk")
        self.assertLess(scenario.PRESETS["cs"].shift_speed_mult, 1.0)
        self.assertGreater(scenario.PRESETS["sprint"].shift_speed_mult, 1.0)

    def test_source_reference_model(self):
        from mapport import movesim
        mv = scenario.PRESETS["cs"]
        run = movesim.run_up(mv)
        self.assertAlmostEqual(run, 250.0, delta=5.0)
        t, apex, land = movesim.jump(mv, run)
        self.assertAlmostEqual(apex, 57.0, delta=1.0)
        self.assertAlmostEqual(t, 0.755, delta=0.03)
        self.assertLessEqual(land, run + 1e-6, "a straight jump never gains speed")
        _t, _a, strafed = movesim.jump(mv, run, strafe_turn_deg_per_s=180.0)
        self.assertLess(strafed - run, 60.0, "one strafe jump only gains a little")

    def test_profile_and_shift_ability(self):
        text = scenario.build("T", "t.json", "{}", 4.0, scenario.PRESETS["cs"])
        self.assertIn("ClampVelocityToInputSpeed=true", text)
        self.assertIn("AbilityProfileNames=CS Walk.abilsprint;;;", text)
        self.assertIn("[Sprint Ability Profile]", text)
        self.assertIn("SpeedModifier=0.52", text)
        self.assertIn("MaxCrouchSpeed=340.0", text)
        self.assertIn("CharacterModel=Meso", text)
        sprint = scenario.build("T", "t.json", "{}", 4.0, scenario.PRESETS["sprint"])
        self.assertIn("AbilityProfileNames=Sprint.abilsprint;;;", sprint)
        self.assertIn("SpeedModifier=1.30", sprint)


class ViewTests(unittest.TestCase):
    def test_view_holes(self):
        from mapport import cleanup, views
        sc = bsp.load(synthetic.build_bsp(with_displacement=False), "v")
        cleanup.add_ground_plane(sc)
        png, on_floor = views.render_view(sc, [], {}, (0.0, 32.0, 16.0), 90.0, pitch=-89.0, fov=60.0, w=80, h=45)
        _png, off_floor = views.render_view(sc, [], {}, (1000.0, 0.0, 16.0), 0.0, pitch=-70.0, w=80, h=45)
        self.assertTrue(png.startswith(b"\x89PNG"))
        self.assertLess(on_floor, 0.05)
        self.assertGreater(off_floor, 0.9)


class Quake3Tests(unittest.TestCase):
    def test_ibsp(self):
        from mapport import quake3, objectives
        for version in (46, 47):
            with self.subTest(version=version):
                sc = quake3.load(synthetic.build_ibsp(version), "q")
                kinds = sorted(b.kind for b in sc.brushes if b.source == "world")
                self.assertEqual(kinds, [scene.CLIP, scene.SOLID])
                self.assertGreaterEqual(sc.stats.get("kept_patch_slabs", 0), 2)
                top = max(p[2] for b in sc.brushes if b.source == "patch" for f in b.faces for p in f.polygon)
                # bezier peak of a 128-high control point is 64; the slab grows away from the visible (lower) side
                self.assertTrue(58.0 <= top <= 64.0 + quake3.PATCH_THICKNESS + 6.0, top)
                self.assertEqual([s.origin[2] for s in sc.spawns], [0.0, 0.0])  # 24 above the feet
                kinds = sorted(go["kind"] for go in sc.gameobjects)
                self.assertEqual(kinds, ["jumppad", "waypoint"])
                doc = objectives.build(sc, "q", 4.0)
                self.assertEqual(doc["items"][0]["classname"], "weapon_railgun")
                self.assertEqual(len(doc["movers"]), 2)

    def test_jump_pad_objects(self):
        from mapport import quake3
        sc = quake3.load(synthetic.build_ibsp(), "q")
        doc = kovaaks_json.build(sc, [], {}, 2, 1.0, 4.0)
        pad = next(o for o in doc["objects"] if o.get("name") == "JumpPad")
        wp = next(o for o in doc["objects"] if o.get("name") == "Waypoint")
        target = next(p["value"] for p in pad["properties"] if p["name"] == "Target")
        self.assertEqual(target, next(p["value"] for p in wp["properties"] if p["name"] == "Name"))

    def test_quake_names_and_preset(self):
        from mapport import naming
        self.assertEqual(naming.guess_game(46), "Q3")
        self.assertEqual(naming.guess_game(47), "QL")
        self.assertEqual(naming.guess_game(30), "CS16")
        self.assertEqual(naming.scenario_name("Blood Run", "Q3", scenario.PRESETS["quake"].variant),
                         "AimMod - Blood Run (Q3) - Quake Movement")
        q = scenario.PRESETS["quake"]
        self.assertFalse(q.clamp_air_speed)
        text = scenario.build("T", "t.json", "{}", 4.0, q)
        self.assertIn("ClampVelocityToInputSpeed=false", text)
        self.assertIn("MaxSpeed=1280.0", text)


class GoldSrcTests(unittest.TestCase):
    def test_cells_textures_spawns(self):
        from mapport import goldsrc, materials
        sc = goldsrc.load(synthetic.build_goldsrc(), "g")
        self.assertEqual(len(sc.brushes), 1)
        b = sc.brushes[0]
        lo, hi = b.bounds()
        self.assertAlmostEqual(hi[2], 0.0, places=3)
        top = next(f for f in b.faces if f.normal[2] > 0.9)
        self.assertEqual(top.texture, "goldsrc/sandwall01")
        self.assertGreater(top.reflectivity[0], top.reflectivity[2])  # embedded miptex colour (sandy)
        self.assertEqual(materials.rule_for(top.texture, materials.load_table())["category"], "sand")
        teams = sorted((s.team, s.origin[2]) for s in sc.spawns)
        self.assertEqual(teams, [(1, 0.0), (2, 0.0)])


def _vec(text: str):
    return tuple(float(v) for v in text.split(","))


def _object_box(o: dict):
    """World box (Unreal axes, map units) a map-creator object covers, from the game's own meshes:
    Water is a centred 200-unit cube, Hurt / JumpPad / Teleporter a 100-unit cube on its min corner."""
    loc, scale = _vec(o["location"]), _vec(o["scale"])
    if o["name"] == "Water":
        half = [s * kovaaks_json.WATER_MESH / 2 for s in scale]
        return tuple(loc[k] - half[k] for k in range(3)), tuple(loc[k] + half[k] for k in range(3))
    size = [s * kovaaks_json.VOLUME_MESH for s in scale]
    return loc, tuple(loc[k] + size[k] for k in range(3))


def _ue_box(lo, hi):
    """Source AABB -> Unreal AABB (Y mirrored)."""
    return (lo[0], -hi[1], lo[2]), (hi[0], -lo[1], hi[2])


class LiquidTests(unittest.TestCase):
    POOL = ((-48.0, 0.0, 4.0), (40.0, 64.0, 14.0))  # a pool on the fixture floor (top at z=16)

    def assertBox(self, got, want):
        for a, b in zip(got[0] + got[1], want[0] + want[1]):
            self.assertAlmostEqual(a, b, places=2)

    def test_water_covers_the_brush_exactly(self):
        sc = scene.Scene(name="w")
        scene.add_liquid(sc, "water", [(0.0, 0.0, -96.0), (200.0, 100.0, -40.0)])
        doc = kovaaks_json.build(sc, [], {}, 2, 1.0, 4.0)
        water = next(o for o in doc["objects"] if o.get("name") == "Water")
        self.assertEqual(water["location"], "100, -50, -68")
        self.assertEqual(water["scale"], "1, 0.5, 0.28")
        self.assertEqual(water["rotation"], "0, 0, 0")
        box = _object_box(water)
        self.assertBox(box, _ue_box((0.0, 0.0, -96.0), (200.0, 100.0, -40.0)))
        self.assertAlmostEqual(box[1][2], -40.0, places=3)  # the surface is the brush's top face
        props = {p["name"]: p["value"] for p in water["properties"]}
        self.assertEqual(props["WaveHeight"], 0.0)  # no waves lifting the surface above the brush
        for name in ("BaseColor", "DepthFadeColor", "HighlightColor1", "HighlightColor2",
                     "RippleShadowColor", "RippleHighlightColor", "MurkColor"):
            self.assertRegex(props[name], r"^[0-9a-f]{8}$")

    def test_hurt_and_pads_use_corner_pivots(self):
        sc = scene.Scene(name="h")
        lava = [(0.0, 0.0, 0.0), (200.0, 100.0, 50.0)]
        scene.add_liquid(sc, "lava", lava)
        scene.add_liquid(sc, "hurt", lava, damage=5.0)
        sc.gameobjects.append({"kind": "jumppad", "origin": (10.0, 20.0, 8.0), "size": (64.0, 32.0, 16.0),
                               "name": "pad", "target": "t", "yaw": 0.0})
        sc.gameobjects.append({"kind": "teleporter", "origin": (0.0, 0.0, 64.0), "size": (32.0, 32.0, 128.0),
                               "name": "tp", "target": "t", "yaw": 0.0})
        doc = kovaaks_json.build(sc, [], {}, 2, 1.0, 4.0)
        hurts = [o for o in doc["objects"] if o.get("name") == "Hurt"]
        self.assertEqual(len(hurts), 2)
        for h in hurts:
            self.assertBox(_object_box(h), _ue_box(*lava))
        kill = {p["name"]: p["value"] for p in hurts[0]["properties"]}
        self.assertTrue(kill["Kill"])
        hurt = {p["name"]: p["value"] for p in hurts[1]["properties"]}
        self.assertFalse(hurt["Kill"])
        self.assertEqual(hurt["Damage"], 5.0)
        pad = next(o for o in doc["objects"] if o.get("name") == "JumpPad")
        self.assertBox(_object_box(pad), _ue_box((-22.0, 4.0, -4.5), (42.0, 36.0, 20.5)))  # 25 thick
        tp = next(o for o in doc["objects"] if o.get("name") == "Teleporter")
        self.assertBox(_object_box(tp), _ue_box((-16.0, -16.0, 0.0), (16.0, 16.0, 128.0)))

    def test_source_water_brush_becomes_water(self):
        sc = bsp.load(synthetic.build_bsp(with_displacement=False, water=self.POOL), "pool")
        waters = [go for go in sc.gameobjects if go["kind"] == "water"]
        self.assertEqual(len(waters), 1)
        self.assertEqual(sc.stats.get("water_volumes"), 1)
        self.assertFalse(any(b.kind == scene.SOLID and b.bounds()[1][2] == 14.0 for b in sc.brushes))  # not a solid
        doc = kovaaks_json.build(sc, [], {}, 2, 1.0, 4.0)
        water = next(o for o in doc["objects"] if o.get("name") == "Water")
        self.assertBox(_object_box(water), _ue_box(*self.POOL))
        # Map scale: the scenario's MapScale multiplies the locations and the object scale alike.
        doc4 = kovaaks_json.build(sc, [], {}, 2, 1.0, 5.0)
        self.assertEqual(next(o for o in doc4["objects"] if o.get("name") == "Water")["scale"], water["scale"])

    def test_goldsrc_fixture_has_no_water(self):
        from mapport import goldsrc
        sc = goldsrc.load(synthetic.build_goldsrc(), "g")
        self.assertFalse(any(go["kind"] == "water" for go in sc.gameobjects))


class CheckTests(unittest.TestCase):
    def test_reachability_and_floating(self):
        from mapport import checks
        sc = bsp.load(synthetic.build_bsp(with_displacement=False), "c")
        res, reached = checks.reachability(sc)
        self.assertTrue(res["spawns_connected"])
        sq = [(0.0, 0.0, 500.0), (10.0, 0.0, 500.0), (10.0, 10.0, 500.0), (0.0, 10.0, 500.0)]
        floater = scene.Brush(faces=[scene.Face(polygon=sq, normal=(0, 0, 1), texture="prop:x")] * 4,
                              kind=scene.NONSOLID, source="prop")
        sc.brushes.append(floater)
        self.assertEqual(checks.remove_floating(sc), 1)
        self.assertNotIn(floater, sc.brushes)


def _header(text: str) -> dict:
    out = {}
    for line in text.splitlines():
        if line.startswith("["):
            break
        k, _, v = line.partition("=")
        out.setdefault(k, v)
    return out


class TagTests(unittest.TestCase):
    def tags_for(self, mapid, game, preset, objects=(), classnames=()):
        from mapport import tags
        mv = scenario.PRESETS[preset]
        return tags.search_tags(mapid, game, mv.model, mv.clamp_air_speed, mv.variant, list(objects), classnames)

    def test_quake_duel_with_pads(self):
        objs = [{"kind": "jumppad", "name": "pad0"}, {"kind": "teleporter", "name": "tp0"},
                {"kind": "hurt", "liquid": "lava", "name": "lava0"}, {"kind": "water", "liquid": "water", "name": "w"}]
        got = self.tags_for("hub3aeroq3", "Q3", "quake", objs)
        self.assertEqual(got, ["AimMod", "Map port", "Quake 3", "Quake", "Quake movement", "Strafe jumping", "Bunny hop",
                               "Duel", "Water", "Lava", "Jump pads", "Teleporters"])
        self.assertNotIn("Counter-Strike", got)
        self.assertNotIn("CS movement", got)
        self.assertIn("Deathmatch", self.tags_for("ztn3dm1", "Q3", "quake"))
        self.assertIn("Duel", self.tags_for("pro_q3tourney7", "Q3", "quake"))
        self.assertEqual(self.tags_for("q3dm17", "QL", "quake")[2:4], ["Quake Live", "Quake"])

    def test_counter_strike_types_and_weapons(self):
        got = self.tags_for("aim_deagle7k_2067", "CSS", "cs", [{"kind": "jumppad", "name": "ladder_pad0"}])
        self.assertEqual(got, ["AimMod", "Map port", "Counter-Strike: Source", "Counter-Strike", "CS movement",
                               "Aim map", "Deagle", "Ladders"])
        self.assertEqual(self.tags_for("awp_lego", "CS16", "cs")[2:], ["Counter-Strike 1.6", "Counter-Strike",
                                                                       "CS movement", "AWP"])  # map type and weapon deduped
        self.assertIn("Fight yard", self.tags_for("fy_pool_day", "CSS", "cs", [{"kind": "water"}]))
        de = self.tags_for("de_d2_remake", "CSS", "cs", classnames=["func_bomb_target"])
        self.assertEqual(de.count("Defuse"), 1)
        self.assertIn("Hostage", self.tags_for("mymap", "CSGO", "cs", classnames=["hostage_entity"]))
        self.assertIn("Sprint movement", self.tags_for("aim_map", "CSS", "sprint"))
        self.assertNotIn("Strafe jumping", self.tags_for("aim_map", "CSS", "cs"))

    def test_limits_and_dedupe(self):
        from mapport import tags
        many = ["AimMod", "aimmod", "a,b", ""] + [f"Tag {i}" for i in range(40)] + ["x" * 40]
        got = tags.clean(many)
        self.assertEqual(got[:2], ["AimMod", "a b"])
        self.assertLessEqual(len(got), tags.MAX_TAGS)
        self.assertLessEqual(len(", ".join(got)), tags.MAX_TAGS_LENGTH)
        self.assertTrue(all(len(t) <= tags.MAX_TAG_LENGTH and "," not in t for t in got))
        self.assertEqual(len({t.lower() for t in got}), len(got))

    def test_scenario_header_per_game(self):
        from mapport import tags
        q = scenario.PRESETS["quake"]
        t = tags.search_tags("hub3aeroq3", "Q3", q.model, q.clamp_air_speed, q.variant)
        d = tags.description("Aerowalk", "Q3", q.label, q.shift, ["Jump pads"], "hub3aeroq3.bsp")
        h = _header(scenario.build("AimMod - Aerowalk (Q3) - Quake Movement", "m.json", "{}", 4.0, q, 1, d, t))
        self.assertTrue(h["SearchTags"].startswith("AimMod, Map port, Quake 3, Quake, Quake movement"))
        self.assertIn("Quake 3", h["Description"])
        self.assertNotIn("Source", h["Description"].replace("Source file", ""))
        self.assertNotIn("Counter-Strike", h["Description"])
        self.assertEqual(h["DifficultyTag"], "3")
        self.assertEqual((h["AimTypeTag"], h["AimSubTypeTag"]), ("Clicking", "Dynamic"))
        cs = scenario.PRESETS["cs"]
        h = _header(scenario.build("AimMod - x (CSS) - CS Movement", "m.json", "{}", 4.0, cs, 1,
                                   tags.description("x", "CSS", cs.label, cs.shift), None))
        self.assertEqual(h["SearchTags"], "AimMod, Map port, CS movement")
        self.assertIn("Counter-Strike: Source", h["Description"])
        self.assertEqual(h["DifficultyTag"], "2")
        long = tags.description("y" * 400, "CSS", cs.label, cs.shift)
        self.assertLessEqual(len(long), tags.MAX_DESCRIPTION)

    def test_cli_writes_tags_from_the_map(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = os.path.join(tmp, "fy_pool_test.bsp")
            with open(src, "wb") as fh:
                # Standing on the fixture floor (top at z=16), open to the air: LiquidTests.POOL sits inside
                # the floor slab, which the port now drops as water sealed under the floor.
                fh.write(synthetic.build_bsp(with_displacement=False, water=((-48.0, 0.0, 16.0), (40.0, 64.0, 40.0))))
            out = os.path.join(tmp, "out")
            cli.main([src, "--out", out, "--no-preview", "--no-thumbnail", "--allow-check-fail"])
            sce = os.path.join(out, "Scenarios", "AimMod - fy_pool_test (CSS) - CS Movement.sce")
            with open(sce, encoding="utf-8") as fh:
                h = _header(fh.read())
            self.assertEqual(h["SearchTags"], "AimMod, Map port, Counter-Strike: Source, Counter-Strike, CS movement, "
                                              "Fight yard, Water")
            self.assertIn("fy_pool_test from Counter-Strike: Source", h["Description"])
            self.assertIn("water", h["Description"])


def _box(lo, hi, texture="concrete/floor01", kind=scene.SOLID, source="world"):
    planes = []
    for axis in range(3):
        n = [0.0, 0.0, 0.0]
        n[axis] = 1.0
        planes.append((n[0], n[1], n[2], hi[axis]))
        planes.append((-n[0], -n[1], -n[2], -lo[axis]))
    faces = [scene.Face(polygon=p, normal=pl[:3], texture=texture) for pl, p in zip(planes, g.brush_faces(planes))]
    return scene.Brush(faces=faces, kind=kind, source=source)


def _area(faces):
    return sum(g.polygon_area(f.polygon) for f in faces)


class CullTests(unittest.TestCase):
    def test_subtract_pieces_cover_the_rest(self):
        from mapport import cull
        sq = [(0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)]
        hole = [(2.0, 2.0), (4.0, 2.0), (4.0, 4.0), (2.0, 4.0)]
        pieces = cull.subtract(sq, hole)
        self.assertAlmostEqual(sum(abs(cull._area2(p)) for p in pieces), 96.0, places=6)
        self.assertEqual(cull.subtract(hole, sq), [])  # fully covered
        far = [(20.0, 20.0), (30.0, 20.0), (30.0, 30.0)]
        self.assertAlmostEqual(sum(abs(cull._area2(p)) for p in cull.subtract(sq, far)), 100.0, places=6)

    def test_touching_boxes_lose_their_shared_faces(self):
        from mapport import cull
        a = _box((0, 0, 0), (64, 64, 64))
        b = _box((64, 0, 0), (128, 64, 64))   # shares the x=64 face exactly
        c = _box((0, 0, 64), (32, 32, 96))    # sits on a's top, covering a quarter of it
        sc = scene.Scene(name="t", brushes=[a, b, c])
        faces, stats = cull.visible_faces(sc)
        fa = faces[id(a)]
        self.assertFalse(any(f.normal == (1.0, 0.0, 0.0) and abs(f.polygon[0][0] - 64) < 1e-6 for f in fa))
        top = [f for f in fa if f.normal == (0.0, 0.0, 1.0)]
        self.assertAlmostEqual(_area(top), 64 * 64 - 32 * 32, places=3)
        self.assertNotIn((0.0, 0.0, -1.0), [f.normal for f in faces[id(c)]])  # c's bottom is on a
        # Every corner of every brush is still on an exported face.
        for brush in (a, b, c):
            kept = {tuple(round(x, 3) for x in p) for f in faces.get(id(brush), brush.faces) for p in f.polygon}
            for p in brush.points():
                self.assertIn(tuple(round(x, 3) for x in p), kept)
        self.assertGreaterEqual(stats["faces_hidden"], 3)

    def test_doubled_faces_keep_the_displacement(self):
        from mapport import cull
        floor = _box((0, 0, -16), (128, 128, 0), "concrete/floor01")
        disp = _box((0, 0, -8), (64, 64, 0), "nature/blendgrass", source="displacement")
        sc = scene.Scene(name="t", brushes=[floor, disp])
        faces, stats = cull.visible_faces(sc)
        floor_top = [f for f in faces[id(floor)] if f.normal == (0.0, 0.0, 1.0)]
        self.assertAlmostEqual(_area(floor_top), 128 * 128 - 64 * 64, places=3)
        self.assertIn((0.0, 0.0, 1.0), [f.normal for f in faces.get(id(disp), disp.faces)])
        self.assertGreaterEqual(stats["faces_doubled"], 1)  # the top, and the sides on x=0 and y=0

    def test_clip_and_glass_do_not_hide_faces(self):
        from mapport import cull
        wall = _box((0, 0, 0), (16, 64, 64))
        clip = _box((16, 0, 0), (32, 64, 64), "tools/toolsplayerclip", kind=scene.CLIP)
        glass = _box((-8, 0, 0), (0, 64, 64), "glass/window01", kind=scene.GLASS)
        faces, _ = cull.visible_faces(scene.Scene(name="t", brushes=[wall, clip, glass]))
        self.assertNotIn(id(wall), faces)  # nothing opaque touches it

    def test_export_leaves_hidden_faces_out(self):
        a, b = _box((0, 0, 0), (64, 64, 64)), _box((64, 0, 0), (128, 64, 64))
        sc = scene.Scene(name="t", brushes=[a, b])
        doc = kovaaks_json.build(sc, [], {}, 2, 1.0, 4.0)
        self.assertEqual([len(o["procedural"]) for o in doc["objects"]], [5, 5])
        self.assertEqual(len(kovaaks_json.build(sc, [], {}, 2, 1.0, 4.0, cull_hidden=False)["objects"][0]["procedural"]), 6)


class DoorTests(unittest.TestCase):
    def test_rotating_door_swings_open_and_stops_colliding(self):
        faces = _box((0, 0, 0), (4, 48, 96), "wood/door").faces
        ent = {"classname": "func_door_rotating", "origin": "0 0 0", "distance": "90"}
        out, kind = bsp.open_door(faces, ent, scene.SOLID)
        self.assertEqual(kind, scene.NONSOLID)
        pts = [p for f in out for p in f.polygon]
        self.assertAlmostEqual(min(p[0] for p in pts), -48.0, places=3)  # swung 90 degrees about Z
        self.assertAlmostEqual(max(abs(p[1]) for p in pts), 4.0, places=3)
        out, kind = bsp.open_door(faces, dict(ent, spawnpos="1"), scene.SOLID)
        self.assertEqual(out, faces)

    def test_sliding_door_moves_by_its_size_minus_lip(self):
        faces = _box((0, 0, 0), (8, 64, 96), "metal/door").faces
        out, kind = bsp.open_door(faces, {"classname": "func_door", "movedir": "-90 0 0", "lip": "8"}, scene.SOLID)
        self.assertEqual(kind, scene.SOLID)
        self.assertAlmostEqual(min(p[2] for f in out for p in f.polygon), 88.0, places=3)  # up by 96 - 8


class StockModelTests(unittest.TestCase):
    def test_vpk_directory_and_chain(self):
        from mapport import vpk
        import struct
        with tempfile.TemporaryDirectory() as tmp:
            game = os.path.join(tmp, "steamapps", "common", "SomeSourceGame", "hl2")
            os.makedirs(game)
            payload = b"MDLDATA"
            tree = b"mdl\0models/props_x\0barrel\0" + struct.pack("<IHHIIH", 0, 2, 0, 0, len(payload) - 2, 0xFFFF) \
                + payload[:2] + b"\0\0\0"
            with open(os.path.join(game, "hl2_misc_dir.vpk"), "wb") as fh:
                fh.write(struct.pack("<III", 0x55AA1234, 1, len(tree)) + tree)
            with open(os.path.join(game, "hl2_misc_000.vpk"), "wb") as fh:
                fh.write(payload[2:])
            stock = vpk.StockModels.discover(tmp)
            self.assertIsNotNone(stock)
            self.assertEqual(stock.get("models/props_x/barrel.mdl"), payload)
            self.assertIsNone(stock.get("models/props_x/missing.mdl"))
            self.assertIsNone(stock.get("materials/x.vtf"))  # models only
            self.assertEqual(stock.used, {"models/props_x/barrel.mdl": "SomeSourceGame/hl2"})
            chain = vpk.ChainFiles({"models/props_x/barrel.mdl": b"PACKED"}, stock)
            self.assertEqual(chain.get("models/props_x/barrel.mdl"), b"PACKED")
            self.assertIsNone(vpk.StockModels.discover("none"))


class BackdropSlotTests(unittest.TestCase):
    def test_ground_plane_shares_the_floor_slot(self):
        from mapport import cleanup
        sc = scene.Scene(name="t", brushes=[_box((0, 0, -16), (512, 512, 0), "concrete/floor01"),
                                           _box((0, 0, 0), (16, 512, 128), "brick/brickwall01")])
        cleanup.add_ground_plane(sc)
        slots, tex_slot = materials.allocate(sc, materials.load_table(), 2)
        self.assertNotIn(cleanup.GROUND_TEXTURE, [t for s in slots for t in s.textures])
        self.assertEqual(tex_slot[cleanup.GROUND_TEXTURE], tex_slot["concrete/floor01"])


class ThumbnailTests(unittest.TestCase):
    def test_views_in_game_space(self):
        from mapport import thumbnail
        v = {"x": 10.0, "y": 20.0, "z": 30.0, "yaw": 90.0, "pitch": -10.0, "fov": 90.0}
        self.assertEqual(thumbnail.to_game(v, 4.0), {"x": 40.0, "y": -80.0, "z": 120.0, "pitch": -10.0,
                                                     "yaw": -90.0, "fov": 90.0})
        doc = thumbnail.views_document("AimMod Capture - x", "x", [v], 4.0)
        self.assertEqual(doc["request"]["action"], "capture-thumbnail")
        self.assertTrue(doc["request"]["out"].endswith(".png"))
        self.assertLessEqual(len(doc["request"]["views"]), 4)

    def test_choose_views(self):
        from mapport import checks, thumbnail
        sc = bsp.load(synthetic.build_bsp(with_displacement=False), "c")
        _res, reached = checks.reachability(sc)
        views = thumbnail.choose_views(sc, reached)
        self.assertTrue(1 <= len(views) <= 3)
        for v in views:
            self.assertTrue(-90 <= v["pitch"] <= 90 and 5 <= v["fov"] <= 170)


try:
    import PIL  # noqa: F401
    HAVE_PIL = True
except ImportError:
    HAVE_PIL = False


@unittest.skipUnless(HAVE_PIL, "needs Pillow")
class CardArtTests(unittest.TestCase):
    def _port(self, root, name, display, game, branded=True, unbranded=False):
        from PIL import Image
        d = os.path.join(root, name)
        os.makedirs(d)
        with open(os.path.join(d, name + ".report.json"), "w", encoding="utf-8") as fh:
            json.dump({"name": name, "display_name": display, "game": game, "variant": "CS Movement"}, fh)
        if branded:
            img = Image.new("RGB", (1920, 1080), (200, 30, 30))  # template strips in red
            img.paste((30, 160, 60), (0, 100, 1920, 800))         # the view in green
            img.save(os.path.join(d, name + ".workshop-thumb-16x9.jpg"))
        if unbranded:
            Image.new("RGB", (1600, 900), (20, 40, 220)).save(os.path.join(d, name + ".card-art.jpg"))
        return d

    def test_export_writes_small_jpegs_and_a_manifest(self):
        from PIL import Image
        from mapport import cardart
        with tempfile.TemporaryDirectory() as root:
            a = self._port(root, "aimmod_de_d2_remake_css", "de_d2_remake", "CSS")
            b = self._port(root, "aimmod_ztn3dm1_q3", "Blood Run", "Q3", unbranded=True)
            out = os.path.join(root, "out")
            done = cardart.export([a, b], out)
            self.assertEqual([d["source"] for d in done], ["workshop-thumb", "render"])
            with open(os.path.join(out, "maps.json"), encoding="utf-8") as fh:
                manifest = json.load(fh)
            self.assertEqual(manifest, [{"key": "aimmod_de_d2_remake_css", "name": "Dust2 Remake", "game": "CS:S"},
                                        {"key": "aimmod_ztn3dm1_q3", "name": "Blood Run", "game": "Quake 3"}])
            for key in ("aimmod_de_d2_remake_css", "aimmod_ztn3dm1_q3"):
                path = os.path.join(out, key + ".jpg")
                self.assertLessEqual(os.path.getsize(path), cardart.MAX_BYTES)
                with Image.open(path) as img:
                    self.assertEqual((img.format, img.size), ("JPEG", (1280, 720)))
                    self.assertNotIn("exif", img.info)
            # The Workshop template's logo and title strips are cropped away.
            with Image.open(os.path.join(out, "aimmod_de_d2_remake_css.jpg")) as img:
                for xy in ((5, 5), (640, 360), (1275, 715)):
                    r, g_, _b = img.convert("RGB").getpixel(xy)
                    self.assertGreater(g_, r, xy)
            # Re-running adds to the manifest instead of replacing it.
            c = self._port(root, "aimmod_aim_map_css", "aim_map", "CSS")
            cardart.export([c], out)
            with open(os.path.join(out, "maps.json"), encoding="utf-8") as fh:
                self.assertEqual(len(json.load(fh)), 3)

    def test_names_and_errors(self):
        from mapport import cardart
        self.assertEqual(cardart.pretty_name("de_dust2"), "Dust2")
        self.assertEqual(cardart.pretty_name("Blood Run Tourney"), "Blood Run Tourney")
        self.assertEqual(cardart.pretty_name("aim_unknown_map"), "aim_unknown_map")
        self.assertEqual([cardart.game_label(t) for t in ("CSS", "CSGO", "CS16", "Q3")], ["CS:S", "CS:GO", "CS 1.6", "Quake 3"])
        with tempfile.TemporaryDirectory() as root:
            empty = self._port(root, "aimmod_x_css", "x", "CSS", branded=False)
            with self.assertRaises(FileNotFoundError):
                cardart.export([empty], os.path.join(root, "out"))
            odd = self._port(root, "Not A Key", "x", "CSS")
            with self.assertRaises(ValueError):
                cardart.export([odd], os.path.join(root, "out"))


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
        # CS presets use Unreal movement so the Shift (Ability 1) walk multiplier applies
        self.assertEqual(kv["EnableQuakeMovement"], "false")
        self.assertEqual(kv["BrakingDeceleration"], "1200.0")
        quake = scenario.build("Q", "q.json", "{}", 4.0, scenario.PRESETS["quake"])
        self.assertIn("EnableQuakeMovement=true", quake)
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
            for rel in ("maps/aimmod_aim_test_css.json", "maps/aimmod_aim_test_css.map",
                        "Scenarios/AimMod - aim_test (CSS) - CS Movement.sce", "Abilities/CS Walk.abilsprint",
                        "aimmod_aim_test_css.aimmod.json", "aimmod_aim_test_css.preview.png",
                        "aimmod_aim_test_css.report.json"):
                self.assertTrue(os.path.isfile(os.path.join(out, rel)), rel)
            with open(os.path.join(out, "Scenarios/AimMod - aim_test (CSS) - CS Movement.sce"), encoding="utf-8", newline="") as fh:
                text = fh.read()
            self.assertTrue(text.startswith("Name=AimMod - aim_test (CSS) - CS Movement\r\n"))
            self.assertIn("MapName=aimmod_aim_test_css.json\r\n", text)


class NamingTests(unittest.TestCase):
    def test_exact_names(self):
        from mapport import naming
        self.assertEqual(naming.scenario_name(naming.display_name("de_dust2"), "CSGO", "CS Movement"),
                         "AimMod - Dust2 (CSGO) - CS Movement")
        self.assertEqual(naming.scenario_name(naming.display_name("aim_map"), "CSS", "CS Movement"),
                         "AimMod - aim_map (CSS) - CS Movement")
        self.assertEqual(naming.file_id("de_dust2", "CSGO"), "aimmod_de_dust2_csgo")
        self.assertEqual(naming.file_id("aim_map", "css"), "aimmod_aim_map_css")
        self.assertEqual(naming.game_tag("CS:GO"), "CSGO")
        self.assertEqual(naming.game_tag("gmod"), "GMod")

    def test_illegal_characters(self):
        from mapport import naming
        for bad in ("AimMod - A:B (CSGO) - X", 'q"uote', "a/b", "a\\b", "a|b", "a?b", "a*b", "a<b", "tab\there",
                    "trailing.", " lead"):
            with self.assertRaises(naming.PortNameError, msg=bad):
                naming.check(bad)
        with self.assertRaises(naming.PortNameError):
            naming.scenario_name("Dust2", "CS:GO", "Bad: Variant")
        with self.assertRaises(naming.PortNameError):
            naming.game_tag("Quake")


if __name__ == "__main__":
    unittest.main()
