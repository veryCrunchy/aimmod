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


class LiquidTests(unittest.TestCase):
    def test_water_and_lava_objects(self):
        sc = scene.Scene(name="w")
        cube = [(0.0, 0.0, 0.0), (200.0, 100.0, 50.0)]
        scene.add_liquid(sc, "water", cube)
        scene.add_liquid(sc, "lava", cube)
        doc = kovaaks_json.build(sc, [], {}, 2, 1.0, 4.0)
        water = next(o for o in doc["objects"] if o.get("name") == "Water")
        hurt = next(o for o in doc["objects"] if o.get("name") == "Hurt")
        self.assertEqual(water["scale"], "2, 1, 0.5")
        self.assertEqual(water["location"], "100, -50, 25")
        self.assertTrue(next(p["value"] for p in hurt["properties"] if p["name"] == "Kill"))

    def test_source_water_brush_becomes_volume(self):
        from mapport import goldsrc
        sc = goldsrc.load(synthetic.build_goldsrc(), "g")
        self.assertFalse(any(go["kind"] == "water" for go in sc.gameobjects))  # no water in the fixture


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
