"""Source engine BSP reader (versions 19-21, including LZMA-compressed CS:GO lumps)."""
from __future__ import annotations

import lzma
import re
import struct
from typing import Dict, List, Optional, Sequence, Tuple

from . import classify, displacement, geometry, objectives, props, scene
from .geometry import Plane, Vec

LUMP_ENTITIES, LUMP_PLANES, LUMP_TEXDATA, LUMP_VERTEXES = 0, 1, 2, 3
LUMP_NODES, LUMP_TEXINFO, LUMP_FACES = 5, 6, 7
LUMP_LEAFS, LUMP_EDGES, LUMP_SURFEDGES, LUMP_MODELS = 10, 12, 13, 14
LUMP_LEAFBRUSHES, LUMP_BRUSHES, LUMP_BRUSHSIDES = 17, 18, 19
LUMP_DISPINFO, LUMP_DISP_VERTS = 26, 33
LUMP_GAME_LUMP, LUMP_PAKFILE = 35, 40
LUMP_TEXDATA_STRING_DATA, LUMP_TEXDATA_STRING_TABLE = 43, 44

SURF_SKY2D, SURF_SKY, SURF_NODRAW = 0x2, 0x4, 0x80


class BspError(Exception):
    pass


def decompress_lump(data: bytes) -> bytes:
    """Undo Valve's LZMA lump wrapper ("LZMA", actual size, lzma size, 5 property bytes)."""
    if data[:4] != b"LZMA":
        return data
    actual, packed = struct.unpack_from("<II", data, 4)
    props = data[12:17]
    # Rebuild a classic .lzma ("alone") header so the stdlib can decode it.
    body = data[17:17 + packed]
    try:
        out = lzma.LZMADecompressor(format=lzma.FORMAT_ALONE).decompress(props + struct.pack("<Q", actual) + body)
    except lzma.LZMAError:
        # Streams written with an end marker need the "unknown size" header instead.
        dec = lzma.LZMADecompressor(format=lzma.FORMAT_ALONE)
        out = dec.decompress(props + struct.pack("<q", -1) + body)
    out = out[:actual]
    if len(out) != actual:
        raise BspError(f"LZMA lump decoded to {len(out)} bytes, expected {actual}")
    return out


class Bsp:
    def __init__(self, data: bytes):
        if data[:4] != b"VBSP":
            raise BspError("not a Source BSP (missing VBSP ident)")
        (self.version,) = struct.unpack_from("<i", data, 4)
        if not 19 <= self.version <= 21:
            raise BspError(f"unsupported BSP version {self.version} (supported: 19-21)")
        self.data = data
        self.lumps: List[Tuple[int, int, int]] = []
        swapped = self._lump_order_swapped()
        for i in range(64):
            a, b, c, _ = struct.unpack_from("<iiii", data, 8 + 16 * i)
            if swapped:  # Left 4 Dead 2 style: version, offset, length
                a, b, c = b, c, a
            self.lumps.append((a, b, c))

    def _lump_order_swapped(self) -> bool:
        if self.version != 21:
            return False
        size = len(self.data)
        ofs, ln, ver, _ = struct.unpack_from("<iiii", self.data, 8)
        plausible = 0 < ofs < size and 0 <= ln <= size - ofs
        if plausible:
            return False
        ver2, ofs2, ln2, _ = struct.unpack_from("<iiii", self.data, 8)
        return 0 < ofs2 < size and 0 <= ln2 <= size - ofs2

    def lump(self, i: int) -> bytes:
        ofs, ln, _ = self.lumps[i]
        if ln <= 0:
            return b""
        return decompress_lump(self.data[ofs:ofs + ln])

    def lump_version(self, i: int) -> int:
        return self.lumps[i][2]

    @staticmethod
    def _records(buf: bytes, fmt: str) -> List[tuple]:
        size = struct.calcsize(fmt)
        return [struct.unpack_from(fmt, buf, o) for o in range(0, len(buf) - size + 1, size)]

    def read(self) -> None:
        self.entities = parse_entities(self.lump(LUMP_ENTITIES).decode("latin1", "replace"))
        self.planes: List[Plane] = [r[:4] for r in self._records(self.lump(LUMP_PLANES), "<ffffi")]
        self.vertexes: List[Vec] = self._records(self.lump(LUMP_VERTEXES), "<fff")
        self.edges = self._records(self.lump(LUMP_EDGES), "<HH")
        self.surfedges = [r[0] for r in self._records(self.lump(LUMP_SURFEDGES), "<i")]
        sdata = self.lump(LUMP_TEXDATA_STRING_DATA)
        self.texnames = []
        for (sid,) in self._records(self.lump(LUMP_TEXDATA_STRING_TABLE), "<i"):
            end = sdata.find(b"\0", sid)
            self.texnames.append(sdata[sid:end if end >= 0 else None].decode("latin1").lower().replace("\\", "/"))
        # dtexdata_t: reflectivity, name id, width, height, view width, view height
        self.texdata = self._records(self.lump(LUMP_TEXDATA), "<fffiiiii")
        # texinfo_t: texture vecs[2][4], lightmap vecs[2][4], flags, texdata
        self.texinfo = self._records(self.lump(LUMP_TEXINFO), "<16fii")
        self.brushes = self._records(self.lump(LUMP_BRUSHES), "<iii")
        self.brushsides = self._records(self.lump(LUMP_BRUSHSIDES), "<Hhhh")
        self.models = self._records(self.lump(LUMP_MODELS), "<9fiii")
        self.nodes = self._records(self.lump(LUMP_NODES), "<iii6hHHhh")
        leaf_fmt = "<ihh6hHHHHh2x"
        if self.version == 19 and self.lump_version(LUMP_LEAFS) == 0:
            leaf_fmt = "<ihh6hHHHHh2x24x"  # early leaves carry an ambient light cube
        self.leafs = self._records(self.lump(LUMP_LEAFS), leaf_fmt)
        self.leafbrushes = [r[0] for r in self._records(self.lump(LUMP_LEAFBRUSHES), "<H")]
        # dface_t (56 bytes)
        self.faces = self._records(self.lump(LUMP_FACES), "<HBBihhhh4BifiiiiiHHI")
        self.dispinfo = [struct.unpack_from("<3fiiiifiH", r, 0) for r in _chunks(self.lump(LUMP_DISPINFO), 176)]
        self.dispverts = self._records(self.lump(LUMP_DISP_VERTS), "<fffff")

    # -- helpers -------------------------------------------------------------------------

    def tex_of(self, ti: int):
        """Return (name, reflectivity, flags, texture axes, size) for a texinfo index."""
        if ti < 0 or ti >= len(self.texinfo):
            return "", (0.5, 0.5, 0.5), 0, None, (512, 512)
        info = self.texinfo[ti]
        flags, tdi = info[16], info[17]
        if tdi < 0 or tdi >= len(self.texdata):
            return "", (0.5, 0.5, 0.5), flags, None, (512, 512)
        td = self.texdata[tdi]
        name = self.texnames[td[3]] if 0 <= td[3] < len(self.texnames) else ""
        axes = (tuple(info[0:4]), tuple(info[4:8]))
        return name, tuple(td[0:3]), flags, axes, (max(1, td[4]), max(1, td[5]))

    def model_brushes(self) -> Dict[int, int]:
        """Map brush index -> model index by walking every model's BSP tree."""
        owner: Dict[int, int] = {}
        for mi, model in enumerate(self.models):
            head = model[9]
            stack = [head]
            seen = set()
            while stack:
                n = stack.pop()
                if n < 0:
                    leaf = -1 - n
                    if leaf >= len(self.leafs):
                        continue
                    lf = self.leafs[leaf]
                    first, num = lf[11], lf[12]
                    for k in range(first, first + num):
                        b = self.leafbrushes[k]
                        owner.setdefault(b, mi)
                    continue
                if n in seen or n >= len(self.nodes):
                    continue
                seen.add(n)
                stack.extend(self.nodes[n][1:3])
        return owner

    def face_points(self, fi: int) -> List[Vec]:
        f = self.faces[fi]
        first, num = f[3], f[4]
        pts = []
        for k in range(first, first + num):
            se = self.surfedges[k]
            e = self.edges[abs(se)]
            pts.append(self.vertexes[e[0] if se >= 0 else e[1]])
        return pts


def _chunks(buf: bytes, size: int):
    return [buf[o:o + size] for o in range(0, len(buf) - size + 1, size)]


def parse_entities(text: str) -> List[Dict[str, str]]:
    ents = []
    for block in re.findall(r"\{([^{}]*)\}", text):
        kv: Dict[str, str] = {}
        for k, v in re.findall(r'"([^"]*)"\s+"([^"]*)"', block):
            kv.setdefault(k.lower(), v)
        ents.append(kv)
    return ents


def _vec(s: str, default=(0.0, 0.0, 0.0)) -> Vec:
    try:
        parts = [float(x) for x in s.split()]
        return (parts[0], parts[1], parts[2])
    except (ValueError, IndexError):
        return default


def load(data: bytes, name: str, disp_step: int = 1, disp_thickness: float = 8.0,
         with_props: bool = True) -> scene.Scene:
    bsp = Bsp(data)
    bsp.read()
    sc = scene.Scene(name=name, entities=bsp.entities)
    sc.version = bsp.version
    sc.notes.append(f"BSP version {bsp.version}")

    model_ent: Dict[int, Dict[str, str]] = {}
    for ent in bsp.entities:
        m = ent.get("model", "")
        if m.startswith("*"):
            try:
                model_ent[int(m[1:])] = ent
            except ValueError:
                pass
    owner = bsp.model_brushes()

    for bi, (first, num, contents) in enumerate(bsp.brushes):
        mi = owner.get(bi, 0)
        ent = model_ent.get(mi, {"classname": "worldspawn"}) if mi else {"classname": "worldspawn"}
        cls = ent.get("classname", "worldspawn")
        sides = bsp.brushsides[first:first + num]
        planes, texes = [], []
        for planenum, texinfo, dispinfo, bevel in sides:
            if bevel & 0xFF:
                continue
            if planenum >= len(bsp.planes):
                continue
            planes.append(bsp.planes[planenum])
            texes.append(bsp.tex_of(texinfo))
        if not planes:
            sc.bump("dropped_empty")
            continue
        if cls.lower().startswith("func_ladder") or contents & scene.CONTENTS_LADDER:
            pts = [q for poly in geometry.brush_faces(planes) if poly for q in poly]
            if mi:
                org = _vec(ent.get("origin", ""))
                pts = [geometry.add(q, org) for q in pts]
            scene.add_ladder(sc, pts)
        if cls.lower() in objectives.VOLUME_CLASSES:
            pts = [q for poly in geometry.brush_faces(planes) if poly for q in poly]
            if pts:
                if mi:
                    org = _vec(ent.get("origin", ""))
                    ang = _vec(ent.get("angles", ""))
                    pts = [geometry.add(geometry.rotate_zyx(q, *ang), org) for q in pts]
                sc.volumes.append((ent, pts))
        kind = classify.classify([t[0] for t in texes], cls, contents)
        if kind is None:
            sc.bump(f"dropped_{_reason(cls, texes, contents)}")
            continue
        polys = geometry.brush_faces(planes)
        faces = []
        for pl, poly, tex in zip(planes, polys, texes):
            if poly is None:
                continue
            tname = tex[0]
            if tex[2] & (SURF_SKY | SURF_SKY2D) and not tname.startswith("tools/"):
                tname = "tools/toolsskybox"
            faces.append(scene.Face(polygon=poly, normal=pl[:3], texture=tname, reflectivity=tex[1],
                                    uv_axes=tex[3], tex_size=tex[4]))
        if len(faces) < 4:
            sc.bump("dropped_degenerate")
            continue
        if mi:
            faces = _place_entity_faces(faces, ent)
        sc.brushes.append(scene.Brush(faces=faces, kind=kind, source=cls if mi else "world"))
        sc.bump(f"kept_{kind}")

    # Displacements become triangulated convex slabs.
    for di, info in enumerate(bsp.dispinfo):
        start, vstart, power, face_i = info[0:3], info[3], info[5], info[9]
        if face_i >= len(bsp.faces):
            continue
        corners = bsp.face_points(face_i)
        if len(corners) != 4:
            sc.bump("dropped_displacement_bad_face")
            continue
        f = bsp.faces[face_i]
        plane = bsp.planes[f[0]]
        normal = plane[:3] if f[1] == 0 else geometry.mul(plane[:3], -1)
        n = (1 << power) + 1
        verts = bsp.dispverts[vstart:vstart + n * n]
        if len(verts) != n * n:
            sc.bump("dropped_displacement_bad_verts")
            continue
        tex = bsp.tex_of(f[5])
        grid = displacement.grid_points(corners, start, verts, power)
        for b in displacement.slabs(grid, n, normal, tex[0], tex[1], disp_thickness, disp_step, tex[3], tex[4]):
            sc.brushes.append(b)
            sc.bump("kept_displacement_slab")
        sc.bump("displacements")

    if with_props:
        try:
            phys = props.read_pakfile(bsp.lump(LUMP_PAKFILE))
            found = props.read_static_props(bsp.lump(LUMP_GAME_LUMP), bsp.data, bsp.lumps[LUMP_GAME_LUMP][0],
                                            decompress_lump)
        except (struct.error, BspError, lzma.LZMAError) as exc:
            sc.notes.append(f"static props unreadable: {exc}")
            phys, found = {}, []
        found += props.entity_props(bsp.entities)
        sc.stats["props_in_map"] = len(found)
        sc.stats["prop_phy_files_packed"] = len(phys)
        props.add_props(sc, found, phys)

    _spawns(sc)
    return sc


def _reason(cls: str, texes, contents: int) -> str:
    if cls.lower().startswith(classify.SKIP_ENTITIES):
        return cls.lower().split("_")[0] + "_" + cls.lower().split("_", 1)[-1].split("_")[0]
    names = [t[0] for t in texes]
    if names and all(t.startswith(classify.SKIP_TOOLS) for t in names):
        return "tool_" + names[0].rsplit("/", 1)[-1]
    if contents & (classify.CONTENTS_WATER | classify.CONTENTS_SLIME):
        return "water"
    return "nonsolid_contents"


def _place_entity_faces(faces: List[scene.Face], ent: Dict[str, str]) -> List[scene.Face]:
    """Brush entities with an origin store geometry relative to it."""
    org = _vec(ent.get("origin", ""))
    ang = _vec(ent.get("angles", ""))
    if org == (0.0, 0.0, 0.0) and ang == (0.0, 0.0, 0.0):
        return faces
    out = []
    for f in faces:
        poly = [geometry.add(geometry.rotate_zyx(p, *ang), org) for p in f.polygon]
        nrm = geometry.rotate_zyx(f.normal, *ang)
        out.append(scene.Face(polygon=poly, normal=nrm, texture=f.texture, reflectivity=f.reflectivity,
                              uv_axes=f.uv_axes, tex_size=f.tex_size))
    return out


SPAWN_CLASSES = {
    "info_player_terrorist": 1,
    "info_player_counterterrorist": 2,
    "info_player_deathmatch": 0,
    "info_player_start": 0,
    "info_deathmatch_spawn": 0,
    "info_armsrace_counterterrorist": 2,
    "info_armsrace_terrorist": 1,
}


def _spawns(sc: scene.Scene) -> None:
    for ent in sc.entities:
        cls = ent.get("classname", "").lower()
        if cls not in SPAWN_CLASSES or "origin" not in ent:
            continue
        if ent.get("startdisabled", "0") == "1" and cls.startswith("info_player_") and cls != "info_player_start":
            # CS:GO keeps extra disabled spawns for larger player counts; still usable.
            pass
        ang = _vec(ent.get("angles", ""))
        sc.spawns.append(scene.Spawn(origin=_vec(ent["origin"]), yaw=ang[1], team=SPAWN_CLASSES[cls], classname=cls))
