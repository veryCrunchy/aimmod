"""Quake 3 / Quake Live BSP reader (IBSP v46 and v47).

Q3 BSPs keep their brushes, so the convex brush pipeline applies directly. Shader names are
normalised to Source-style tool names ("common/caulk" -> "tools/toolsnodraw", ...) so the shared
classification and material rules work. Bezier patches are tessellated and turned into slabs like
displacements. Jump pads and teleporters become KovaaK's JumpPad / Teleporter game objects.
"""
from __future__ import annotations

import math
import struct
from typing import Dict, List, Sequence, Tuple

from . import classify, displacement, geometry as g, objectives, scene
from .bsp import BspError, parse_entities

# Q3 content / surface flags
C_SOLID, C_LAVA, C_SLIME, C_WATER = 0x1, 0x8, 0x10, 0x20
C_PLAYERCLIP, C_MONSTERCLIP, C_DETAIL, C_TRANSLUCENT = 0x10000, 0x20000, 0x8000000, 0x20000000
SURF_SKY, SURF_NODRAW = 0x4, 0x80
MST_PATCH = 2

FEET_OFFSET = 24.0      # Q3 player origin sits 24 units above the feet (bbox -24..32)
PATCH_THICKNESS = 4.0

TOOL_MAP = {
    "common/caulk": "tools/toolsnodraw", "common/caulkshadow": "tools/toolsnodraw",
    "common/nodraw": "tools/toolsnodraw", "common/nodrawnonsolid": "tools/toolsnodraw",
    "common/clip": "tools/toolsplayerclip", "common/full_clip": "tools/toolsplayerclip",
    "common/weapclip": "tools/toolsplayerclip", "common/invisible": "tools/toolsinvisible",
    "common/botclip": "tools/toolsnpcclip", "common/trigger": "tools/toolstrigger",
    "common/hint": "tools/toolshint", "common/skip": "tools/toolsskip",
    "common/areaportal": "tools/toolsareaportal", "common/clusterportal": "tools/toolsareaportal",
    "common/nodrop": "tools/toolstrigger", "common/donotenter": "tools/toolstrigger",
    "common/origin": "tools/toolsorigin", "common/lightgrid": "tools/toolsskip",
    "common/antiportal": "tools/toolshint", "common/nolightmap": "tools/toolsnodraw",
}


def shader_texture(name: str, surf: int) -> str:
    n = name.lower().replace("\\", "/")
    if n.startswith("textures/"):
        n = n[len("textures/"):]
    if n in TOOL_MAP:
        return TOOL_MAP[n]
    if surf & SURF_SKY or n.startswith("skies/") or "/sky" in n:
        return "tools/toolsskybox"
    if surf & SURF_NODRAW:
        return "tools/toolsnodraw"
    return n


def source_contents(c: int) -> int:
    """Translate Q3 content flags to the Source flags classify() understands."""
    out = c & C_SOLID
    if c & (C_LAVA | C_SLIME | C_WATER):
        out |= classify.CONTENTS_WATER
    if c & C_PLAYERCLIP:
        out |= classify.CONTENTS_PLAYERCLIP
    if c & C_MONSTERCLIP:
        out |= classify.CONTENTS_MONSTERCLIP
    if c & C_DETAIL:
        out |= classify.CONTENTS_DETAIL
    if c & C_TRANSLUCENT and c & C_SOLID:
        out |= classify.CONTENTS_WINDOW
    return out


class Q3Bsp:
    def __init__(self, data: bytes):
        if data[:4] != b"IBSP":
            raise BspError("not a Quake 3 BSP (missing IBSP ident)")
        (self.version,) = struct.unpack_from("<i", data, 4)
        if self.version not in (46, 47):
            raise BspError(f"unsupported IBSP version {self.version} (supported: 46, 47)")
        self.data = data
        self.lumps = [struct.unpack_from("<ii", data, 8 + 8 * i) for i in range(17)]

    def lump(self, i: int) -> bytes:
        ofs, ln = self.lumps[i]
        return self.data[ofs:ofs + ln]

    @staticmethod
    def recs(buf: bytes, fmt: str) -> List[tuple]:
        size = struct.calcsize(fmt)
        return [struct.unpack_from(fmt, buf, o) for o in range(0, len(buf) - size + 1, size)]

    def read(self) -> None:
        self.entities = parse_entities(self.lump(0).decode("latin1", "replace"))
        self.shaders = [(r[0].split(b"\0", 1)[0].decode("latin1"), r[1], r[2]) for r in self.recs(self.lump(1), "<64sii")]
        self.planes = [r[:4] for r in self.recs(self.lump(2), "<ffff")]
        self.models = self.recs(self.lump(7), "<6fiiii")
        self.brushes = self.recs(self.lump(8), "<iii")
        self.brushsides = self.recs(self.lump(9), "<ii")
        self.verts = self.recs(self.lump(10), "<3f2f2f3f4B")
        self.surfaces = self.recs(self.lump(13), "<12i3f9f2i")


def _bezier(p0, p1, p2, t: float):
    a, b, c = (1 - t) ** 2, 2 * t * (1 - t), t * t
    return tuple(a * p0[k] + b * p1[k] + c * p2[k] for k in range(3))


def tessellate(ctrl: Sequence[Sequence[Vec]], level: int) -> Tuple[List[Vec], int, int]:
    """Row-major grid of points for a w x h control grid of biquadratic patches."""
    h, w = len(ctrl), len(ctrl[0])
    rows = (h - 1) // 2 * level + 1
    cols = (w - 1) // 2 * level + 1
    out = [None] * (rows * cols)
    for pi in range((h - 1) // 2):
        for pj in range((w - 1) // 2):
            for a in range(level + 1):
                v = a / level
                col_pts = [_bezier(ctrl[2 * pi][2 * pj + k], ctrl[2 * pi + 1][2 * pj + k],
                                   ctrl[2 * pi + 2][2 * pj + k], v) for k in range(3)]
                for b in range(level + 1):
                    u = b / level
                    out[(pi * level + a) * cols + pj * level + b] = _bezier(*col_pts, u)
    return out, rows, cols


def _patch_level(ctrl) -> int:
    # Curvier patches get a finer tessellation (2..6 steps per bezier segment).
    worst = 0.0
    for row in ctrl:
        for k in range(0, len(row) - 2, 2):
            mid = g.lerp(row[k], row[k + 2], 0.5)
            worst = max(worst, g.length(g.sub(row[k + 1], mid)))
    return max(2, min(6, int(math.ceil(worst / 16.0)) + 1))


def _vec(s: str) -> Vec:
    try:
        x, y, z = (float(v) for v in s.split()[:3])
        return (x, y, z)
    except ValueError:
        return (0.0, 0.0, 0.0)


SPAWNS = {"info_player_deathmatch": 0, "info_player_start": 0, "team_ctf_redplayer": 1, "team_ctf_redspawn": 1,
          "team_ctf_blueplayer": 2, "team_ctf_bluespawn": 2}


def load(data: bytes, name: str, patch_thickness: float = PATCH_THICKNESS) -> scene.Scene:
    q = Q3Bsp(data)
    q.read()
    sc = scene.Scene(name=name, entities=q.entities)
    sc.version = q.version
    sc.notes.append(f"Quake 3 IBSP version {q.version}")
    model_ent: Dict[int, Dict[str, str]] = {}
    for ent in q.entities:
        m = ent.get("model", "")
        if m.startswith("*"):
            try:
                model_ent[int(m[1:])] = ent
            except ValueError:
                pass

    def shader(i: int) -> Tuple[str, int, int]:
        return q.shaders[i] if 0 <= i < len(q.shaders) else ("", 0, 0)

    model_bounds: Dict[int, Tuple[Vec, Vec]] = {}
    for mi, m in enumerate(q.models):
        ent = model_ent.get(mi, {"classname": "worldspawn"}) if mi else {"classname": "worldspawn"}
        cls = ent.get("classname", "worldspawn")
        first_brush, nbrush = m[8], m[9]
        pts_all: List[Vec] = []
        for bi in range(first_brush, first_brush + nbrush):
            fs, ns, sh = q.brushes[bi]
            bname, bsurf, bcont = shader(sh)
            planes, texes = [], []
            for side in q.brushsides[fs:fs + ns]:
                if side[0] >= len(q.planes):
                    continue
                planes.append(q.planes[side[0]])
                sn, ss, _sc = shader(side[1])
                texes.append(shader_texture(sn, ss))
            if len(planes) < 4:
                continue
            polys = g.brush_faces(planes)
            pts_all += [p for poly in polys if poly for p in poly]
            if cls.lower() in objectives.VOLUME_CLASSES:
                sc.volumes.append((ent, [p for poly in polys if poly for p in poly]))
            kind = classify.classify(texes, cls, source_contents(bcont))
            if kind is None:
                sc.bump("dropped_" + (cls.lower() if cls != "worldspawn" else "nonsolid_or_tool"))
                continue
            faces = [scene.Face(polygon=poly, normal=pl[:3], texture=t, reflectivity=(0.5, 0.5, 0.5))
                     for pl, poly, t in zip(planes, polys, texes) if poly]
            if len(faces) < 4:
                sc.bump("dropped_degenerate")
                continue
            sc.brushes.append(scene.Brush(faces=faces, kind=kind, source="world" if not mi else cls))
            sc.bump(f"kept_{kind}")
        if pts_all:
            model_bounds[mi] = (tuple(min(p[k] for p in pts_all) for k in range(3)),
                                tuple(max(p[k] for p in pts_all) for k in range(3)))

    # Curved surfaces (patches) of the world model.
    world = q.models[0] if q.models else None
    world_surfs = range(world[6], world[6] + world[7]) if world else range(0)
    for si in world_surfs:
        s = q.surfaces[si]
        if s[2] != MST_PATCH:
            continue
        sname, ssurf, scont = shader(s[0])
        tex = shader_texture(sname, ssurf)
        if classify.is_tool(tex):
            continue
        pw, ph = s[24], s[25]
        if pw < 3 or ph < 3:
            continue
        verts = q.verts[s[3]:s[3] + pw * ph]
        if len(verts) != pw * ph:
            continue
        ctrl = [[verts[r * pw + c][0:3] for c in range(pw)] for r in range(ph)]
        nrm = [[verts[r * pw + c][7:10] for c in range(pw)] for r in range(ph)]
        level = _patch_level(ctrl)
        grid, rows, cols = tessellate(ctrl, level)
        ngrid, _r, _c = tessellate(nrm, level)
        ngrid = [g.normalize(n) for n in ngrid]
        avg = g.normalize(tuple(sum(n[k] for n in ngrid) for k in range(3)))
        kind = scene.SOLID if scont & C_SOLID or scont == 0 else scene.NONSOLID
        made = displacement.slabs(grid, rows, avg, tex, (0.5, 0.5, 0.5), patch_thickness, 1, cols=cols,
                                  normals=ngrid)
        for b in made:
            b.kind = kind
            b.source = "patch"
            sc.brushes.append(b)
        sc.bump("patches")
        sc.bump("kept_patch_slabs", len(made))

    _spawns_and_objects(sc, model_ent, model_bounds)
    return sc


def _spawns_and_objects(sc: scene.Scene, model_ent, model_bounds) -> None:
    by_name = {e.get("targetname", ""): e for e in sc.entities if e.get("targetname")}
    for ent in sc.entities:
        cls = ent.get("classname", "").lower()
        if cls in SPAWNS and "origin" in ent:
            o = _vec(ent["origin"])
            yaw = float(ent.get("angle", _vec(ent.get("angles", ""))[1]) or 0)
            sc.spawns.append(scene.Spawn(origin=(o[0], o[1], o[2] - FEET_OFFSET), yaw=yaw, team=SPAWNS[cls],
                                         classname=cls))
    n = 0
    for mi, ent in model_ent.items():
        cls = ent.get("classname", "").lower()
        if cls not in ("trigger_push", "trigger_teleport") or mi not in model_bounds:
            continue
        dest = by_name.get(ent.get("target", ""))
        if not dest or "origin" not in dest:
            sc.bump(f"{cls}_without_target")
            continue
        lo, hi = model_bounds[mi]
        wp = f"{'jp' if cls == 'trigger_push' else 'tp'}{n}"
        n += 1
        yaw = float(dest.get("angle", _vec(dest.get("angles", ""))[1]) or 0)
        d = _vec(dest["origin"])
        if cls == "trigger_teleport":
            d = (d[0], d[1], d[2] - FEET_OFFSET)
        sc.gameobjects.append({"kind": "waypoint", "origin": d, "size": (0, 0, 0), "name": wp, "target": "",
                               "yaw": yaw})
        sc.gameobjects.append({"kind": "jumppad" if cls == "trigger_push" else "teleporter",
                               "origin": ((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2]),
                               "size": tuple(hi[k] - lo[k] for k in range(3)), "name": f"{cls}_{n}",
                               "target": wp, "yaw": 0.0})
        sc.bump("jump_pads" if cls == "trigger_push" else "teleporters")
