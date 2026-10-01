"""Static/dynamic props -> convex brushes, using the collision hulls (.phy) packed in the BSP.

A prop's .phy holds its physics model as convex pieces ("ledges"). Each piece becomes one convex
KovaaK's brush, which gives the prop both its collision and an approximate (hull-shaped) visual.
Only models packed into the BSP's pakfile can be read; stock models live in the game's VPKs.
"""
from __future__ import annotations

import io
import re
import struct
import zipfile
from typing import Dict, List, Optional, Sequence, Tuple

from . import geometry as g
from . import scene
from .geometry import Vec

INCHES_PER_METER = 1.0 / 0.0254
PROP_REFLECTIVITY = (0.45, 0.40, 0.33)
MIN_PART_SIZE = 12.0   # smaller parts (bolts, wires, specks) are skipped
MAX_PARTS = 40         # per model, largest first


def read_pakfile(data: bytes) -> Dict[str, bytes]:
    """Return {lower-case path: bytes} for model files (.mdl, .vvd, .dx90.vtx, .phy) in the pakfile."""
    out: Dict[str, bytes] = {}
    if not data:
        return out
    try:
        z = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile:
        return out
    for info in z.infolist():
        name = info.filename.lower().replace("\\", "/")
        if name.endswith((".phy", ".vvd", ".mdl", ".dx90.vtx")):
            try:
                out[name] = z.read(info)
            except (zipfile.BadZipFile, NotImplementedError, OSError):
                continue
    return out


def parse_phy(data: bytes) -> List[List[List[Vec]]]:
    """Convex pieces of a .phy: list of solids, each a list of pieces, each piece a list of triangles."""
    if len(data) < 16:
        return []
    hsize, _id, nsolids, _chk = struct.unpack_from("<iiii", data, 0)
    o = hsize
    solids = []
    for _ in range(max(0, nsolids)):
        if o + 4 > len(data):
            break
        (size,) = struct.unpack_from("<i", data, o)
        start = o + 4
        o = start + size
        cs = start + 28 if data[start:start + 4] == b"VPHY" else start
        try:
            solids.append(_ledges(data, cs, o))
        except struct.error:
            continue
    return solids


def _ledges(data: bytes, cs: int, end: int) -> List[List[Vec]]:
    (tree_root,) = struct.unpack_from("<i", data, cs + 32)
    limit = min(end, cs + tree_root) if tree_root > 0 else end
    lo = cs + 48
    first_points = limit
    pieces = []
    while lo + 16 <= min(limit, first_points):
        point_ofs, _client, flags, ntris = struct.unpack_from("<iiIh", data, lo)
        size = (flags >> 8) * 16
        pts_at = lo + point_ofs
        first_points = min(first_points, pts_at)
        tris = []
        for t in range(ntris):
            base = lo + 16 + t * 16
            idx = [struct.unpack_from("<I", data, base + 4 + 4 * e)[0] & 0xFFFF for e in range(3)]
            tri = []
            for i in idx:
                x, y, z, _w = struct.unpack_from("<ffff", data, pts_at + 16 * i)
                tri.append((x * INCHES_PER_METER, z * INCHES_PER_METER, -y * INCHES_PER_METER))
            tris.append(tri)
        if tris:
            pieces.append(tris)
        if size <= 0:
            break
        lo += size
    return pieces


def piece_planes(tris: Sequence[Sequence[Vec]]) -> List[g.Plane]:
    pts = [p for t in tris for p in t]
    c = g.centroid(pts)
    planes: List[g.Plane] = []
    for a, b, cc in tris:
        pl = g.plane_from_points(a, b, cc)
        if pl is None:
            continue
        if g.dot(pl[:3], c) - pl[3] > 0:  # centroid must be behind the plane
            pl = (-pl[0], -pl[1], -pl[2], -pl[3])
        if any(g.dot(pl[:3], q[:3]) > 0.9995 and abs(pl[3] - q[3]) < 0.05 for q in planes):
            continue
        planes.append(pl)
    return planes


def parse_vvd(data: bytes) -> List[Vec]:
    """All vertex positions of a .vvd (model space, Source units)."""
    if data[:4] != b"IDSV" or len(data) < 64:
        return []
    (n_lod0,) = struct.unpack_from("<i", data, 16)
    (vstart,) = struct.unpack_from("<i", data, 56)
    out = []
    for i in range(max(0, n_lod0)):
        o = vstart + 48 * i + 16
        if o + 12 > len(data):
            break
        out.append(struct.unpack_from("<fff", data, o))
    return out


def _vvd_lod0(data: bytes) -> List[Vec]:
    """LOD 0 vertex positions in the order the .mdl/.vtx index them (fixups applied)."""
    if data[:4] != b"IDSV" or len(data) < 64:
        return []
    (n_lod0,) = struct.unpack_from("<i", data, 16)
    nfix, fix_start, vstart = struct.unpack_from("<iii", data, 48)
    raw = [struct.unpack_from("<fff", data, vstart + 48 * i + 16) for i in range(max(0, n_lod0))
           if vstart + 48 * i + 28 <= len(data)]
    if nfix <= 0:
        return raw
    out: List[Vec] = []
    for k in range(nfix):
        lod, src, num = struct.unpack_from("<iii", data, fix_start + 12 * k)
        if lod >= 0:
            out += raw[src:src + num]
    return out


def mesh_triangles(mdl: bytes, vtx: bytes, vvd: bytes) -> Tuple[List[Vec], List[Tuple[int, int, int]]]:
    """Triangles of LOD 0 of every body part / model / mesh, as indices into the VVD vertices."""
    verts = _vvd_lod0(vvd)
    if mdl[:4] != b"IDST" or len(vtx) < 36 or not verts:
        return verts, []
    nbody, body_ofs = struct.unpack_from("<ii", mdl, 232)
    vnbody, vbody_ofs = struct.unpack_from("<ii", vtx, 28)
    tris: List[Tuple[int, int, int]] = []
    for bi in range(min(nbody, vnbody)):
        mb = body_ofs + 16 * bi
        nmodels, _base, model_idx = struct.unpack_from("<iii", mdl, mb + 4)
        vb = vbody_ofs + 8 * bi
        vnm, vmo = struct.unpack_from("<ii", vtx, vb)
        for mi in range(min(nmodels, vnm)):
            mm = mb + model_idx + 148 * mi
            nmesh, mesh_idx, _nv, vert_idx = struct.unpack_from("<iiii", mdl, mm + 72)
            vm = vb + vmo + 8 * mi
            nlod, lod_ofs = struct.unpack_from("<ii", vtx, vm)
            if nlod <= 0:
                continue
            vl = vm + lod_ofs  # LOD 0
            vnmesh, vmesh_ofs = struct.unpack_from("<ii", vtx, vl)
            for me in range(min(nmesh, vnmesh)):
                mesh = mm + mesh_idx + 116 * me
                (vert_ofs,) = struct.unpack_from("<i", mdl, mesh + 12)
                base = vert_idx // 48 + vert_ofs
                vme = vl + vmesh_ofs + 9 * me
                nsg, sg_ofs = struct.unpack_from("<ii", vtx, vme)
                for sgsize in (25, 33):
                    got = _strip_groups(vtx, vme + sg_ofs, nsg, sgsize, base, len(verts))
                    if got is not None:
                        tris += got
                        break
    return verts, tris


def _strip_groups(vtx: bytes, at: int, count: int, size: int, base: int, nverts: int):
    out = []
    try:
        for s in range(count):
            sg = at + size * s
            nv, v_ofs, ni, i_ofs = struct.unpack_from("<iiii", vtx, sg)
            if nv < 0 or ni < 0 or ni % 3:
                return None
            orig = [struct.unpack_from("<H", vtx, sg + v_ofs + 9 * k + 4)[0] for k in range(nv)]
            idx = struct.unpack_from(f"<{ni}H", vtx, sg + i_ofs)
            for k in range(0, ni, 3):
                a, b, c = idx[k], idx[k + 1], idx[k + 2]
                if max(a, b, c) >= nv:
                    return None
                tri = (base + orig[a], base + orig[b], base + orig[c])
                if max(tri) >= nverts:
                    return None
                out.append(tri)
    except struct.error:
        return None
    return out


def connected_parts(verts: Sequence[Vec], tris: Sequence[Tuple[int, int, int]]) -> List[List[Vec]]:
    """Split a mesh into triangle-connected parts (vertices welded by position)."""
    key = {}
    weld = []
    for v in verts:
        k = (round(v[0], 2), round(v[1], 2), round(v[2], 2))
        weld.append(key.setdefault(k, len(key)))
    parent = list(range(len(key)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for a, b, c in tris:
        ra, rb, rc = find(weld[a]), find(weld[b]), find(weld[c])
        parent[rb] = ra
        parent[find(rc)] = ra
    groups: Dict[int, List[Vec]] = {}
    used = {i for tri in tris for i in tri}
    for i in used:
        groups.setdefault(find(weld[i]), []).append(verts[i])
    sized = []
    for gp in groups.values():
        exts = sorted(max(q[k] for q in gp) - min(q[k] for q in gp) for k in range(3))
        # skip specks, and wires/cables (thin in two directions) that would read as floating beams
        if exts[2] >= MIN_PART_SIZE and exts[1] >= 4.0:
            sized.append((exts[2], gp))
    sized.sort(key=lambda e: -e[0])
    return [_extremes(gp) for _ext, gp in sized[:MAX_PARTS]]


def _extremes(group: Sequence[Vec]) -> List[Vec]:
    ext = set()
    for d in _SAMPLE_DIRS:
        ext.add(max(group, key=lambda q: g.dot(q, d)))
        ext.add(min(group, key=lambda q: g.dot(q, d)))
    return list(ext)


CLUSTER_CELL = 8.0
# 18-DOP: axes plus edge diagonals (stairs, ramps and bevels come out right; corners are cut)
_DIRS = [g.normalize(d) for d in (
    (1, 0, 0), (0, 1, 0), (0, 0, 1), (1, 1, 0), (1, -1, 0), (1, 0, 1), (1, 0, -1), (0, 1, 1), (0, 1, -1))]
_SAMPLE_DIRS = _DIRS + [g.normalize(d) for d in (
    (1, 1, 1), (1, 1, -1), (1, -1, 1), (-1, 1, 1),
    (2, 1, 0), (1, 2, 0), (2, -1, 0), (1, -2, 0), (2, 0, 1), (2, 0, -1), (0, 2, 1), (0, 2, -1),
    (1, 0, 2), (1, 0, -2), (0, 1, 2), (0, 1, -2))]


def clusters(points: Sequence[Vec], cell: float = CLUSTER_CELL) -> List[List[Vec]]:
    """Split a model's vertices into spatially connected parts (voxel flood fill), reduced to
    their extreme points so instances can be transformed cheaply."""
    cells: Dict[Tuple[int, int, int], List[Vec]] = {}
    for p in points:
        cells.setdefault((int(p[0] // cell), int(p[1] // cell), int(p[2] // cell)), []).append(p)
    seen = set()
    parts = []
    for start in cells:
        if start in seen:
            continue
        seen.add(start)
        stack, group = [start], []
        while stack:
            c = stack.pop()
            group += cells[c]
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for dz in (-1, 0, 1):
                        nb = (c[0] + dx, c[1] + dy, c[2] + dz)
                        if nb in cells and nb not in seen:
                            seen.add(nb)
                            stack.append(nb)
        parts.append(_extremes(group))
    return parts


def kdop_planes(points: Sequence[Vec], pad: float = 0.25) -> List[g.Plane]:
    planes: List[g.Plane] = []
    for d in _DIRS:
        hi = max(g.dot(p, d) for p in points) + pad
        lo = min(g.dot(p, d) for p in points) - pad
        planes.append((d[0], d[1], d[2], hi))
        planes.append((-d[0], -d[1], -d[2], -lo))
    return planes


def _transform(p: Vec, origin: Vec, angles: Vec, scale: float) -> Vec:
    q = g.rotate_zyx(g.mul(p, scale), *angles)
    return g.add(q, origin)


def read_static_props(gamelump: bytes, filedata: bytes, lump_offset: int, decompress=None) -> List[dict]:
    """Static props from the game lump directory. Entry offsets are file-relative (Valve layout);
    a few tools write lump-relative offsets, which are detected and handled too."""
    if len(gamelump) < 4:
        return []
    (n,) = struct.unpack_from("<i", gamelump, 0)
    for i in range(n):
        gid, _flags, ver, ofs, ln = struct.unpack_from("<4sHHii", gamelump, 4 + 16 * i)
        if gid != b"prps":
            continue
        blob = filedata[ofs:ofs + ln]
        if not (0 < ofs < len(filedata)) or (blob[:4] != b"LZMA" and ofs < lump_offset):
            blob = gamelump[ofs:ofs + ln]
        if decompress:
            blob = decompress(blob)
        return _sprp(blob, ver, 0, len(blob))
    return []


def _sprp(buf: bytes, ver: int, ofs: int, ln: int) -> List[dict]:
    o = ofs
    (nnames,) = struct.unpack_from("<i", buf, o); o += 4
    names = [buf[o + 128 * i:o + 128 * (i + 1)].split(b"\0", 1)[0].decode("latin1").lower().replace("\\", "/")
             for i in range(nnames)]
    o += 128 * nnames
    (nleaf,) = struct.unpack_from("<i", buf, o); o += 4 + 2 * nleaf
    (nprops,) = struct.unpack_from("<i", buf, o); o += 4
    if nprops <= 0:
        return []
    size = (ofs + ln - o) // nprops
    props = []
    for i in range(nprops):
        b = o + i * size
        ox, oy, oz, pa, ya, ra = struct.unpack_from("<6f", buf, b)
        ptype, _fl, _lc, solid = struct.unpack_from("<HHHB", buf, b + 24)
        scale = 1.0
        if ver >= 11 and size >= 80:
            (scale,) = struct.unpack_from("<f", buf, b + size - 4)
            if not 0.01 < scale < 100:
                scale = 1.0
        props.append({"model": names[ptype] if ptype < len(names) else "", "origin": (ox, oy, oz),
                      "angles": (pa, ya, ra), "solid": solid, "scale": scale})
    return props


def _vec(s: str) -> Vec:
    try:
        x, y, z = (float(v) for v in s.split()[:3])
        return (x, y, z)
    except ValueError:
        return (0.0, 0.0, 0.0)


def entity_props(entities: Sequence[Dict[str, str]]) -> List[dict]:
    out = []
    for e in entities:
        cls = e.get("classname", "")
        if not cls.startswith(("prop_dynamic", "prop_physics", "prop_static", "prop_door")) or \
                not e.get("model", "").endswith(".mdl"):
            continue
        solid = int(float(e.get("solid", "6") or 6))
        out.append({"model": e["model"].lower().replace("\\", "/"), "origin": _vec(e.get("origin", "")),
                    "angles": _vec(e.get("angles", "")), "solid": solid,
                    "scale": float(e.get("modelscale", "1") or 1)})
    return out


def _faces(planes: List[g.Plane], model: str) -> List[scene.Face]:
    if len(planes) < 4:
        return []
    polys = g.brush_faces(planes)
    faces = [scene.Face(polygon=poly, normal=pl[:3], texture="prop:" + model, reflectivity=PROP_REFLECTIVITY)
             for pl, poly in zip(planes, polys) if poly is not None and g.polygon_area(poly) > 1.0]
    return faces if len(faces) >= 4 else []


_BOX_QUADS = ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3))
BOX_FILL = 0.7  # use the model-space box when it is at least this full; otherwise the 18-DOP


def _volume(planes: List[g.Plane]) -> float:
    vol = 0.0
    for pl, poly in zip(planes, g.brush_faces(planes)):
        if poly:
            vol += g.polygon_area(poly) * pl[3] / 3.0
    return vol


def part_shape(part: Sequence[Vec]) -> Tuple[str, object]:
    """Pick the cheapest shape that fits a model part: its model-space box (6 faces) when the box is
    nearly full, else an 18-DOP. Boxes keep the file small; 18-DOPs keep ramps and bevels."""
    lo = [min(p[k] for p in part) for k in range(3)]
    hi = [max(p[k] for p in part) for k in range(3)]
    size = [max(hi[k] - lo[k], 0.5) for k in range(3)]
    box_vol = size[0] * size[1] * size[2]
    kd = kdop_planes(part)
    if box_vol <= 0 or _volume(kd) / box_vol >= BOX_FILL:
        corners = [(x, y, z) for x in (lo[0], lo[0] + size[0]) for y in (lo[1], lo[1] + size[1])
                   for z in (lo[2], lo[2] + size[2])]
        return "box", corners
    return "kdop", part


def box_faces(corners: Sequence[Vec], model: str) -> List[scene.Face]:
    c = g.centroid(corners)
    faces = []
    for quad in _BOX_QUADS:
        poly = [corners[i] for i in quad]
        n = g.normalize(g.cross(g.sub(poly[1], poly[0]), g.sub(poly[2], poly[0])))
        if g.dot(n, g.sub(poly[0], c)) < 0:
            poly.reverse()
            n = g.mul(n, -1)
        faces.append(scene.Face(polygon=poly, normal=n, texture="prop:" + model, reflectivity=PROP_REFLECTIVITY))
    return faces


def _brush(planes: List[g.Plane], model: str, kind: str) -> Optional[scene.Brush]:
    faces = _faces(planes, model)
    return scene.Brush(faces=faces, kind=kind, source="prop") if faces else None


def model_parts(files: Dict[str, bytes], base: str) -> List[List[Vec]]:
    """Connected parts of a packed model (by triangle topology when .mdl/.vtx are packed)."""
    vvd = files.get(base + ".vvd")
    if not vvd:
        return []
    mdl, vtx = files.get(base + ".mdl"), files.get(base + ".dx90.vtx")
    if mdl and vtx:
        try:
            verts, tris = mesh_triangles(mdl, vtx, vvd)
        except struct.error:
            verts, tris = [], []
        if tris:
            return connected_parts(verts, tris)
    return clusters(parse_vvd(vvd))


def add_props(sc: scene.Scene, props: List[dict], files: Dict[str, bytes]) -> None:
    """Solid props with a packed .phy become exact convex collision hulls. Other props whose
    vertices are packed (.vvd) become non-colliding k-DOP hulls per connected part, which restores
    stairs, trims and wall pieces visually. The map's own clip brushes provide their collision."""
    phy_cache: Dict[str, list] = {}
    vvd_cache: Dict[str, list] = {}
    missing = set()
    unpacked: List[dict] = []
    for pr in props:
        base = pr["model"][:-4] if pr["model"].endswith(".mdl") else pr["model"]
        made = 0
        if pr["solid"]:
            if base not in phy_cache:
                pieces = []
                data = files.get(base + ".phy")
                if data:
                    for solid in parse_phy(data):
                        pieces += solid
                phy_cache[base] = pieces
            for tris in phy_cache[base]:
                world = [[_transform(p, pr["origin"], pr["angles"], pr["scale"]) for p in tri] for tri in tris]
                b = _brush(piece_planes(world), pr["model"], scene.SOLID)
                if b:
                    sc.brushes.append(b)
                    made += 1
            if made:
                sc.bump("props_collision_hulls")
                sc.bump("kept_prop_hull_brushes", made)
                continue
        if base not in vvd_cache:
            vvd_cache[base] = [part_shape(p) for p in model_parts(files, base)]
        # Non-colliding, so all parts of one instance share a single (non-convex) object.
        faces: List[scene.Face] = []
        for shape, pts in vvd_cache[base]:
            world = [_transform(p, pr["origin"], pr["angles"], pr["scale"]) for p in pts]
            faces += box_faces(world, pr["model"]) if shape == "box" else _faces(kdop_planes(world), pr["model"])
            made += 1
        if faces:
            sc.brushes.append(scene.Brush(faces=faces, kind=scene.NONSOLID, source="prop"))
        if made:
            sc.bump("props_visual_hulls")
            sc.bump("kept_prop_hull_brushes", made)
        else:
            missing.add(pr["model"])
            unpacked.append(pr)
            sc.bump("props_not_packed")
    standins = add_name_standins(sc, unpacked)
    show_stair_ramps(sc, unpacked)
    missing -= standins
    if missing:
        sc.notes.append(f"{len(missing)} prop models are not packed in the map (stock game models); "
                        "they are missing from the port")


# ---------------------------------------------------------------------------------------------
# Stand-ins for stock models that are not packed in the map.

_DIMS = re.compile(r"(?<![0-9])(\d{1,4})x(\d{1,4})(?:x(\d{1,4}))?(?![0-9])")
THIN = 6.0  # thickness used for two-dimension names (doors, windows, frames: width x height)
STAIR_WORDS = ("stair", "step")


def name_dims(model: str) -> Optional[Tuple[float, float, float]]:
    """Size from names like dust_crate_37x37x74 (x, y, z) or dust_door_80x128 (width x height)."""
    m = _DIMS.search(model.rsplit("/", 1)[-1])
    if not m:
        return None
    a, b, c = m.group(1), m.group(2), m.group(3)
    if c:
        return (float(a), float(b), float(c))
    return (THIN, float(a), float(b))


def _box_points(lo: Vec, hi: Vec) -> List[Vec]:
    return [(x, y, z) for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])]


# Where the model origin sits on the box: bottom centre, centre, or on a hinge edge (doors).
PIVOTS = {
    "bottom": lambda d: ((-d[0] / 2, -d[1] / 2, 0.0), (d[0] / 2, d[1] / 2, d[2])),
    "center": lambda d: ((-d[0] / 2, -d[1] / 2, -d[2] / 2), (d[0] / 2, d[1] / 2, d[2] / 2)),
    "hinge+y": lambda d: ((-d[0] / 2, 0.0, 0.0), (d[0] / 2, d[1], d[2])),
    "hinge-y": lambda d: ((-d[0] / 2, -d[1], 0.0), (d[0] / 2, 0.0, d[2])),
}


def _inside_count(points: Sequence[Vec], solids) -> int:
    n = 0
    for p in points:
        for lo, hi, planes in solids:
            if all(lo[k] - 0.5 <= p[k] <= hi[k] + 0.5 for k in range(3)) and \
                    all(g.dot(nrm, p) - d <= 0.5 for nrm, d in planes):
                n += 1
                break
    return n


def add_name_standins(sc: scene.Scene, props: List[dict]) -> set:
    """Boxes for unported models whose names carry their size. The pivot that leaves the box least
    buried in the map's solids is chosen per model (doors hang on a hinge, crates sit on the floor)."""
    by_model: Dict[str, List[dict]] = {}
    for pr in props:
        if name_dims(pr["model"]):
            by_model.setdefault(pr["model"], []).append(pr)
    if not by_model:
        return set()
    solids = [(b.bounds()[0], b.bounds()[1], [(f.normal, g.dot(f.normal, f.polygon[0])) for f in b.faces])
              for b in sc.brushes if b.kind == scene.SOLID and b.source in ("world", "displacement")]
    from .checks import _Index
    from .spawns import _Solid
    floor_index = _Index([_Solid(b) for b in sc.brushes
                          if b.kind in (scene.SOLID, scene.CLIP) and b.source in ("world", "displacement")])
    done = set()
    for model, insts in by_model.items():
        dims = name_dims(model)
        best, best_score = "bottom", None
        for name, piv in PIVOTS.items():
            lo, hi = piv(dims)
            # sample the box interior; fewer samples inside solids = better fit
            samples = [(lo[0] + (hi[0] - lo[0]) * i / 4, lo[1] + (hi[1] - lo[1]) * j / 4, lo[2] + (hi[2] - lo[2]) * k / 4)
                       for i in (1, 2, 3) for j in (1, 2, 3) for k in (1, 2, 3)]
            score = 0
            for pr in insts[:12]:
                world = [_transform(p, pr["origin"], pr["angles"], pr["scale"]) for p in samples]
                near = [s for s in solids if all(s[0][k] < max(p[k] for p in world) + 1 and
                                                 s[1][k] > min(p[k] for p in world) - 1 for k in range(3))]
                score += _inside_count(world, near)
            if best_score is None or score < best_score:
                best, best_score = name, score
        lo, hi = PIVOTS[best](dims)
        for pr in insts:
            world = [_transform(p, pr["origin"], pr["angles"], pr["scale"]) for p in _box_points(lo, hi)]
            world = _snap_to_floor(world, floor_index)
            if world is None:
                sc.bump("props_standins_skipped_no_floor")
                continue
            b = _brush(kdop_planes(world, pad=0.0), model, scene.NONSOLID)
            if b:
                sc.brushes.append(b)
                sc.bump("props_name_standins")
        done.add(model)
    return done


SNAP_UP, SNAP_DOWN = 16.0, 48.0


def _snap_to_floor(world: List[Vec], index) -> Optional[List[Vec]]:
    """Rest a stand-in box on the floor under its base centre; None if there is no floor nearby."""
    from .checks import floor_below
    lo_z = min(p[2] for p in world)
    cx = sum(p[0] for p in world) / len(world)
    cy = sum(p[1] for p in world) / len(world)
    z = floor_below((cx, cy, lo_z + SNAP_UP), index, SNAP_UP + SNAP_DOWN)
    if z is None:
        return None
    dz = z - lo_z
    return [(p[0], p[1], p[2] + dz) for p in world]


def show_stair_ramps(sc: scene.Scene, unported: List[dict]) -> None:
    """CS:GO maps put an invisible clip ramp over model stairs. Where the stairs model is missing,
    show the ramp (with a stone material) so the stairs are visible and walkable as before."""
    stairs = [pr["origin"] for pr in unported if any(w in pr["model"].rsplit("/", 1)[-1] for w in STAIR_WORDS)]
    if not stairs:
        return
    for b in sc.brushes:
        if b.kind != scene.CLIP or b.source == "prop":
            continue
        if not any(0.35 < f.normal[2] < 0.97 for f in b.faces):
            continue
        lo, hi = b.bounds()
        if not any(all(lo[k] - 96 <= s[k] <= hi[k] + 96 for k in range(3)) for s in stairs):
            continue
        b.kind = scene.SOLID
        for f in b.faces:
            f.texture = "prop:stair_ramp_stone"
        sc.bump("stair_ramps_shown")
