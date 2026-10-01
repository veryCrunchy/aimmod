"""GoldSrc BSP v30 reader (Half-Life, Counter-Strike 1.6).

GoldSrc maps keep no brushes, only BSP trees. Every path from a model's head node to a solid
(or sky) leaf bounds a convex cell, the intersection of the half-spaces along the path. Those cells
are exactly the solid volume of the map, so they feed the same convex brush pipeline. Each cell face
takes the texture of the rendered face lying on the same plane. Embedded miptex (or .wad files next
to the map) give each texture's average colour.
"""
from __future__ import annotations

import os
import struct
from typing import Dict, List, Optional, Sequence, Tuple

from . import classify, geometry as g, objectives, scene
from .bsp import BspError, parse_entities

CONTENTS_EMPTY, CONTENTS_SOLID, CONTENTS_WATER, CONTENTS_SLIME, CONTENTS_LAVA, CONTENTS_SKY = -1, -2, -3, -4, -5, -6
LIQUIDS = {CONTENTS_WATER: "water", CONTENTS_SLIME: "slime", CONTENTS_LAVA: "lava"}
FEET_OFFSET = 36.0  # HL hull is -36..36 around the origin
MARGIN = 16.0

SPAWNS = {"info_player_deathmatch": 1, "info_player_start": 2}  # CS 1.6: deathmatch = T, start = CT


def texture_name(n: str) -> str:
    n = n.lower()
    if n.startswith("sky"):
        return "tools/toolsskybox"
    if n in ("aaatrigger", "trigger"):
        return "tools/toolstrigger"
    if n in ("clip",):
        return "tools/toolsplayerclip"
    if n in ("null", "nodraw", "bevel", "skip", "hint", "origin"):
        return "tools/toolsnodraw" if n in ("null", "nodraw", "bevel") else f"tools/tools{n}"
    return "goldsrc/" + n


def _miptex_colour(buf: bytes, base: int) -> Optional[Tuple[float, float, float]]:
    """Average linear colour of a miptex (smallest mip level), or None if it is not embedded."""
    try:
        w, h = struct.unpack_from("<II", buf, base + 16)
        offs = struct.unpack_from("<4I", buf, base + 24)
    except struct.error:
        return None
    if not offs[0] or w == 0 or h == 0:
        return None
    mw, mh = max(1, w // 8), max(1, h // 8)
    pix_at = base + offs[3]
    pal_at = pix_at + mw * mh + 2
    if pal_at + 768 > len(buf):
        return None
    pal = buf[pal_at:pal_at + 768]
    idx = buf[pix_at:pix_at + mw * mh]
    if not idx:
        return None
    acc = [0.0, 0.0, 0.0]
    for i in idx:
        for k in range(3):
            acc[k] += (pal[3 * i + k] / 255.0) ** 2.2
    return tuple(a / len(idx) for a in acc)  # type: ignore[return-value]


def read_wads(folder: str) -> Dict[str, Tuple[float, float, float]]:
    """name -> average colour for every miptex in .wad files in the map's folder."""
    out: Dict[str, Tuple[float, float, float]] = {}
    if not folder or not os.path.isdir(folder):
        return out
    for fn in os.listdir(folder):
        if not fn.lower().endswith(".wad"):
            continue
        with open(os.path.join(folder, fn), "rb") as fh:
            buf = fh.read()
        if buf[:4] not in (b"WAD3", b"WAD2"):
            continue
        n, table = struct.unpack_from("<ii", buf, 4)
        for k in range(n):
            pos, _disk, _size, typ, _comp, _pad, name = struct.unpack_from("<iiibbh16s", buf, table + 32 * k)
            if typ != 0x43:
                continue
            col = _miptex_colour(buf, pos)
            if col:
                out[name.split(b"\0", 1)[0].decode("latin1").lower()] = col
    return out


class GoldSrcBsp:
    def __init__(self, data: bytes):
        (self.version,) = struct.unpack_from("<i", data, 0)
        if self.version != 30:
            raise BspError(f"not a GoldSrc BSP (version {self.version}, expected 30)")
        self.data = data
        self.lumps = [struct.unpack_from("<ii", data, 4 + 8 * i) for i in range(15)]

    def lump(self, i: int) -> bytes:
        ofs, ln = self.lumps[i]
        return self.data[ofs:ofs + ln]

    @staticmethod
    def recs(buf: bytes, fmt: str) -> List[tuple]:
        size = struct.calcsize(fmt)
        return [struct.unpack_from(fmt, buf, o) for o in range(0, len(buf) - size + 1, size)]

    def read(self, wad_colours: Dict[str, Tuple[float, float, float]]) -> None:
        self.entities = parse_entities(self.lump(0).decode("latin1", "replace"))
        self.planes = [r[:4] for r in self.recs(self.lump(1), "<ffffi")]
        tex = self.lump(2)
        self.textures: List[Tuple[str, Tuple[float, float, float]]] = []
        if len(tex) >= 4:
            (n,) = struct.unpack_from("<i", tex, 0)
            for k in range(n):
                (ofs,) = struct.unpack_from("<i", tex, 4 + 4 * k)
                if ofs < 0:
                    self.textures.append(("", (0.5, 0.5, 0.5)))
                    continue
                raw = tex[ofs:ofs + 16].split(b"\0", 1)[0].decode("latin1").lower()
                col = _miptex_colour(tex, ofs) or wad_colours.get(raw) or (0.45, 0.42, 0.38)
                self.textures.append((raw, col))
        self.vertexes = self.recs(self.lump(3), "<fff")
        self.nodes = self.recs(self.lump(5), "<ihh6hHH")
        self.texinfo = self.recs(self.lump(6), "<8fii")
        self.faces = self.recs(self.lump(7), "<HHiHH4Bi")
        self.leafs = self.recs(self.lump(10), "<ii6hHH4B")
        self.edges = self.recs(self.lump(12), "<HH")
        self.surfedges = [r[0] for r in self.recs(self.lump(13), "<i")]
        self.models = self.recs(self.lump(14), "<9f4iiii")

    def face_poly(self, fi: int) -> List[Vec]:
        f = self.faces[fi]
        pts = []
        for k in range(f[2], f[2] + f[3]):
            se = self.surfedges[k]
            e = self.edges[abs(se)]
            pts.append(self.vertexes[e[0] if se >= 0 else e[1]])
        return pts


Vec = g.Vec


def _cells(q: GoldSrcBsp, head: int, box: Tuple[Vec, Vec]):
    """Yield (contents, [(plane, planenum, front_side)]) for every solid/sky leaf under head."""
    lo, hi = box
    bounds = []
    for k in range(3):
        n = [0.0, 0.0, 0.0]
        n[k] = 1.0
        bounds.append(((n[0], n[1], n[2], hi[k] + MARGIN), -1, True))
        bounds.append(((-n[0], -n[1], -n[2], -(lo[k] - MARGIN)), -1, True))
    stack = [(head, [])]
    while stack:
        node, path = stack.pop()
        if node < 0:
            leaf = -1 - node
            contents = q.leafs[leaf][0] if leaf < len(q.leafs) else CONTENTS_SOLID
            if contents in (CONTENTS_SOLID, CONTENTS_SKY, CONTENTS_WATER, CONTENTS_SLIME, CONTENTS_LAVA):
                yield contents, bounds + path
            continue
        pn, c0, c1 = q.nodes[node][0], q.nodes[node][1], q.nodes[node][2]
        a, b, c, d = q.planes[pn]
        # front child: n.x >= d  ->  (-n).x <= -d ; back child: n.x <= d
        stack.append((c0, path + [((-a, -b, -c, -d), pn, True)]))
        stack.append((c1, path + [((a, b, c, d), pn, False)]))


def load(data: bytes, name: str, folder: str = "") -> scene.Scene:
    q = GoldSrcBsp(data)
    q.read(read_wads(folder))
    sc = scene.Scene(name=name, entities=q.entities)
    sc.version = 30
    sc.notes.append("GoldSrc BSP v30: solids rebuilt from BSP leaves; player-clip hulls are not used")

    # Rendered faces by plane, for texturing cell faces.
    by_plane: Dict[Tuple[int, bool], List[Tuple[Vec, int]]] = {}
    for fi, f in enumerate(q.faces):
        poly = q.face_poly(fi)
        if len(poly) < 3:
            continue
        by_plane.setdefault((f[0], bool(f[1])), []).append((g.centroid(poly), f[4]))

    def tex_for(pn: int, outward_is_plane_normal: bool, centre: Vec) -> Tuple[str, Tuple[float, float, float]]:
        # A rendered face with side 0 faces along the plane normal.
        cands = by_plane.get((pn, not outward_is_plane_normal), [])
        if not cands:
            return "tools/toolsnodraw", (0.4, 0.4, 0.4)
        _c, ti = min(cands, key=lambda e: sum((e[0][k] - centre[k]) ** 2 for k in range(3)))
        mi = q.texinfo[ti][8] if ti < len(q.texinfo) else -1
        if 0 <= mi < len(q.textures):
            raw, col = q.textures[mi]
            return texture_name(raw), col
        return "tools/toolsnodraw", (0.4, 0.4, 0.4)

    model_ent: Dict[int, Dict[str, str]] = {}
    for ent in q.entities:
        m = ent.get("model", "")
        if m.startswith("*"):
            try:
                model_ent[int(m[1:])] = ent
            except ValueError:
                pass

    world_box = (tuple(q.models[0][0:3]), tuple(q.models[0][3:6])) if q.models else ((0, 0, 0), (0, 0, 0))
    ladder_pts: Dict[int, List[Vec]] = {}
    for mi, m in enumerate(q.models):
        ent = model_ent.get(mi, {"classname": "worldspawn"}) if mi else {"classname": "worldspawn"}
        cls = ent.get("classname", "worldspawn")
        if mi and not ent.get("model"):
            continue
        box = (tuple(m[0:3]), tuple(m[3:6]))
        if mi == 0:
            box = world_box
        for contents, cons in _cells(q, m[9], box):
            planes = [c[0] for c in cons]
            polys = g.brush_faces(planes)
            faces = []
            for (pl, pn, front_child), poly in zip(cons, polys):
                if not poly:
                    continue
                if pn < 0:
                    # map-bounds face: never visible from inside
                    faces.append(scene.Face(polygon=poly, normal=pl[:3], texture="tools/toolsnodraw"))
                    continue
                # Cell is on the front side -> outward normal is -plane normal.
                tex, col = tex_for(pn, not front_child, g.centroid(poly))
                faces.append(scene.Face(polygon=poly, normal=pl[:3], texture=tex, reflectivity=col))
            if len(faces) < 4:
                continue
            if mi and ent.get("origin"):
                # Models with an origin brush are stored relative to the entity origin.
                try:
                    org = tuple(float(v) for v in ent["origin"].split()[:3])
                except ValueError:
                    org = (0.0, 0.0, 0.0)
                if any(org) and not all(box[0][k] - 1 <= org[k] <= box[1][k] + 1 for k in range(3)):
                    faces = [scene.Face(polygon=[g.add(p, org) for p in f.polygon], normal=f.normal,
                                        texture=f.texture, reflectivity=f.reflectivity) for f in faces]
            if contents in LIQUIDS:
                scene.add_liquid(sc, LIQUIDS[contents], [p for f in faces for p in f.polygon])
                continue
            if cls.lower() == "func_water":
                scene.add_liquid(sc, "water", [p for f in faces for p in f.polygon])
                continue
            if cls.lower().startswith("func_ladder"):
                ladder_pts.setdefault(mi, []).extend(p for f in faces for p in f.polygon)
            if cls.lower() in objectives.VOLUME_CLASSES:
                sc.volumes.append((ent, [p for f in faces for p in f.polygon]))
            texes = [f.texture for f in faces]
            if contents == CONTENTS_SKY:
                kind = scene.CLIP
            else:
                kind = classify.classify(texes, cls, 1)
            if kind is None:
                sc.bump("dropped_" + cls.lower())
                continue
            visible = [t for t in texes if not classify.is_tool(t)]
            if kind == scene.SOLID and visible and all(t.split("/")[-1].startswith("{") for t in visible):
                kind = scene.GLASS  # "{" textures are alpha-tested fences and grates
            if kind == scene.SOLID and not visible:
                # Cell faces with no rendered face are buried inside other solids or face the void.
                sc.bump("dropped_hidden_cells")
                continue
            sc.brushes.append(scene.Brush(faces=faces, kind=kind, source="world" if not mi else cls))
            sc.bump(f"kept_{kind}")

    for pts in ladder_pts.values():
        scene.add_ladder(sc, pts)
    for ent in q.entities:
        cls = ent.get("classname", "").lower()
        if cls in SPAWNS and "origin" in ent:
            try:
                x, y, z = (float(v) for v in ent["origin"].split()[:3])
            except ValueError:
                continue
            yaw = float((ent.get("angles", "0 0 0").split() + ["0", "0"])[1] or 0)
            if "angle" in ent:
                yaw = float(ent["angle"] or 0)
            sc.spawns.append(scene.Spawn(origin=(x, y, z - FEET_OFFSET), yaw=yaw, team=SPAWNS[cls], classname=cls))
    return sc
