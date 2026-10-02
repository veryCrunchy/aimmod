"""Build tiny synthetic Source BSP / GMA files for tests (no game data involved)."""
from __future__ import annotations

import lzma
import struct
from typing import Dict, List, Tuple

BOX_MIN = (-64.0, -32.0, 0.0)
BOX_MAX = (64.0, 96.0, 16.0)


def _box_planes(lo, hi):
    out = []
    for axis in range(3):
        n = [0.0, 0.0, 0.0]
        n[axis] = 1.0
        out.append((n[0], n[1], n[2], hi[axis]))
        out.append((-n[0], -n[1], -n[2], -lo[axis]))
    return out


def build_ibsp(version: int = 46) -> bytes:
    """Quake 3 IBSP: a floor brush, a player-clip brush, one 3x3 patch arch, a jump pad trigger."""
    shaders = [("textures/base_floor/concrete", 0, 1), ("textures/common/clip", 0x80, 0x10000),
               ("textures/gothic_trim/arch", 0, 1), ("textures/common/trigger", 0x80, 0x40000000)]
    lumps: Dict[int, bytes] = {}
    lumps[1] = b"".join(struct.pack("<64sii", n.encode(), s, c) for n, s, c in shaders)
    planes, sides, brushes = [], [], []

    def brush(lo, hi, shader):
        first = len(sides)
        for pl in _box_planes(lo, hi):
            planes.append(pl)
            sides.append((len(planes) - 1, shader))
        brushes.append((first, 6, shader))

    brush((-256, -256, -16), (256, 256, 0), 0)     # floor
    brush((100, 100, 0), (140, 140, 64), 1)         # clip
    brush((-200, -32, 0), (-150, 32, 8), 3)         # trigger_push (model 1)
    lumps[2] = b"".join(struct.pack("<ffff", *p) for p in planes)
    lumps[8] = b"".join(struct.pack("<iii", *b) for b in brushes)
    lumps[9] = b"".join(struct.pack("<ii", *s) for s in sides)
    # 3x3 patch: an arch from x=-64 to x=64 over y in [-32, 32], peak z=96, normals pointing down
    verts = []
    for r, y in enumerate((-32.0, 0.0, 32.0)):
        for c, (x, z) in enumerate(((-64.0, 0.0), (0.0, 128.0), (64.0, 0.0))):
            verts.append(struct.pack("<3f2f2f3f4B", x, y, z, 0, 0, 0, 0, 0.0, 0.0, -1.0, 255, 255, 255, 255))
    lumps[10] = b"".join(verts)
    surf = struct.pack("<12i3f9f2i", 2, -1, 2, 0, 9, 0, 0, -1, 0, 0, 0, 0, *([0.0] * 12), 3, 3)
    lumps[13] = surf
    lumps[7] = (struct.pack("<6fiiii", -256, -256, -16, 256, 256, 128, 0, 1, 0, 2)
                + struct.pack("<6fiiii", -200, -32, 0, -150, 32, 8, 1, 0, 2, 1))
    ents = ('{\n"classname" "worldspawn"\n}\n'
            '{\n"classname" "info_player_deathmatch"\n"origin" "0 -128 24"\n"angle" "90"\n}\n'
            '{\n"classname" "info_player_deathmatch"\n"origin" "0 128 24"\n"angle" "270"\n}\n'
            '{\n"classname" "trigger_push"\n"model" "*1"\n"target" "pad1"\n}\n'
            '{\n"classname" "target_position"\n"targetname" "pad1"\n"origin" "0 0 200"\n}\n'
            '{\n"classname" "weapon_railgun"\n"origin" "32 32 16"\n}\n')
    lumps[0] = ents.encode() + b"\0"
    head_size = 8 + 17 * 8
    body = b""
    dirs = []
    for i in range(17):
        raw = lumps.get(i, b"")
        dirs.append((head_size + len(body), len(raw)))
        body += raw + b"\0" * (-len(raw) % 4)
    head = b"IBSP" + struct.pack("<i", version) + b"".join(struct.pack("<ii", o, n) for o, n in dirs)
    return head + body


def build_goldsrc() -> bytes:
    """GoldSrc v30: one node (plane z=0) with solid below and empty above, and a textured floor face."""
    lumps: Dict[int, bytes] = {}
    lumps[1] = struct.pack("<ffffi", 0.0, 0.0, 1.0, 0.0, 2)
    # miptex lump with one embedded 16x16 texture, all pixels palette index 1 (a sandy colour)
    w = h = 16
    mip = struct.pack("<16sII", b"sandwall01", w, h)
    o0 = 16 + 8 + 16
    sizes = [w * h, (w // 2) * (h // 2), (w // 4) * (h // 4), (w // 8) * (h // 8)]
    offs, cur = [], o0
    for s in sizes:
        offs.append(cur)
        cur += s
    mip += struct.pack("<4I", *offs) + b"".join(bytes([1]) * s for s in sizes)
    pal = bytearray(768)
    pal[3:6] = bytes((200, 170, 110))
    mip += struct.pack("<H", 256) + bytes(pal)
    lumps[2] = struct.pack("<ii", 1, 8) + mip
    verts = [(-64, -64, 0), (64, -64, 0), (64, 64, 0), (-64, 64, 0)]
    lumps[3] = b"".join(struct.pack("<fff", *v) for v in verts)
    lumps[5] = struct.pack("<ihh6hHH", 0, -2, -1, -64, -64, -64, 64, 64, 64, 0, 1)  # front: leaf 1, back: leaf 0
    lumps[6] = struct.pack("<8fii", 1, 0, 0, 0, 0, 1, 0, 0, 0, 0)
    lumps[7] = struct.pack("<HHiHH4Bi", 0, 0, 0, 4, 0, 0, 0, 0, 0, -1)
    lumps[10] = (struct.pack("<ii6hHH4B", -2, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
                 + struct.pack("<ii6hHH4B", -1, -1, -64, -64, 0, 64, 64, 64, 0, 0, 0, 0, 0, 0))
    lumps[12] = struct.pack("<HH", 0, 0) + b"".join(struct.pack("<HH", i, (i + 1) % 4) for i in range(4))
    lumps[13] = b"".join(struct.pack("<i", i) for i in (1, 2, 3, 4))
    lumps[14] = struct.pack("<9f4iiii", -64, -64, -64, 64, 64, 64, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1)
    ents = ('{\n"classname" "worldspawn"\n}\n'
            '{\n"classname" "info_player_start"\n"origin" "0 0 36"\n"angles" "0 90 0"\n}\n'
            '{\n"classname" "info_player_deathmatch"\n"origin" "32 32 36"\n}\n')
    lumps[0] = ents.encode() + b"\0"
    head_size = 4 + 15 * 8
    body = b""
    dirs = []
    for i in range(15):
        raw = lumps.get(i, b"")
        dirs.append((head_size + len(body), len(raw)))
        body += raw + b"\0" * (-len(raw) % 4)
    return struct.pack("<i", 30) + b"".join(struct.pack("<ii", o, n) for o, n in dirs) + body


def _lzma_lump(raw: bytes) -> bytes:
    enc = lzma.LZMACompressor(format=lzma.FORMAT_ALONE, filters=[{"id": lzma.FILTER_LZMA1, "dict_size": 1 << 16}])
    alone = enc.compress(raw) + enc.flush()
    props, body = alone[:5], alone[13:]
    return b"LZMA" + struct.pack("<II", len(raw), len(body)) + props + body


def build_bsp(version: int = 20, compress: bool = False, with_displacement: bool = True,
              extra_entities: str = "", water=None) -> bytes:
    """water: optional (lo, hi) box added as a CONTENTS_WATER brush (a pool)."""
    lumps: Dict[int, bytes] = {}
    names = [b"concrete/concretefloor001a", b"tools/toolsplayerclip", b"brick/brickwall001a"]
    sdata, table = b"", []
    for n in names:
        table.append(len(sdata))
        sdata += n + b"\0"
    lumps[43] = sdata
    lumps[44] = b"".join(struct.pack("<i", t) for t in table)
    lumps[2] = b"".join(struct.pack("<fffiiiii", r, g, b, i, 512, 512, 512, 512)
                        for i, (r, g, b) in enumerate([(0.4, 0.4, 0.4), (0.5, 0.2, 0.5), (0.5, 0.25, 0.15)]))

    def texinfo(td: int, flags: int = 0) -> bytes:
        return struct.pack("<16fii", 1, 0, 0, 0, 0, -1, 0, 0, *([0.0] * 8), flags, td)

    lumps[6] = texinfo(0) + texinfo(1) + texinfo(2)

    planes: List[Tuple[float, float, float, float]] = []
    sides = []
    lo, hi = BOX_MIN, BOX_MAX
    for axis in range(3):
        n = [0.0, 0.0, 0.0]
        n[axis] = 1.0
        planes.append((n[0], n[1], n[2], hi[axis]))
        planes.append((-n[0], -n[1], -n[2], -lo[axis]))
    # brush 0: floor slab (concrete, brick on +y face); brush 1: player clip cube
    for i in range(6):
        sides.append((i, 2 if i == 2 else 0, 0, 0))
    clip_lo, clip_hi = (0.0, 0.0, 16.0), (32.0, 32.0, 48.0)
    for axis in range(3):
        n = [0.0, 0.0, 0.0]
        n[axis] = 1.0
        planes.append((n[0], n[1], n[2], clip_hi[axis]))
        planes.append((-n[0], -n[1], -n[2], -clip_lo[axis]))
        sides.append((len(planes) - 2, 1, 0, 0))
        sides.append((len(planes) - 1, 1, 0, 0))
    brushes = struct.pack("<iii", 0, 6, 0x1) + struct.pack("<iii", 6, 6, 0x10000)
    if water:
        first = len(sides)
        for pl in _box_planes(*water):
            planes.append(pl)
            sides.append((len(planes) - 1, 0, 0, 0))
        brushes += struct.pack("<iii", first, 6, 0x20)
    lumps[1] = b"".join(struct.pack("<ffffi", *p, 0) for p in planes)
    lumps[19] = b"".join(struct.pack("<Hhhh", *s) for s in sides)
    lumps[18] = brushes
    lumps[17] = struct.pack("<HH", 0, 1)
    leaf = struct.pack("<ihh6hHHHHh2x", 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0)
    if version == 19:
        leaf += bytes(24)
    lumps[10] = leaf
    lumps[5] = b""
    lumps[14] = struct.pack("<9fiii", *BOX_MIN, *BOX_MAX, 0, 0, 0, -1, 0, 0)

    verts = [(-64.0, -32.0, 16.0), (64.0, -32.0, 16.0), (64.0, 96.0, 16.0), (-64.0, 96.0, 16.0)]
    lumps[3] = b"".join(struct.pack("<fff", *v) for v in verts)
    lumps[12] = struct.pack("<HH", 0, 0) + b"".join(struct.pack("<HH", i, (i + 1) % 4) for i in range(4))
    lumps[13] = b"".join(struct.pack("<i", i) for i in (1, 2, 3, 4))
    if with_displacement:
        face = struct.pack("<HBBihhhh4BifiiiiiHHI", 4, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0,
                           -1, 0.0, 0, 0, 0, 0, -1, 0, 0, 0)
        lumps[7] = face
        # power 1: 3x3 vertices lifting the middle row by 8 units
        dv = []
        for i in range(3):
            for j in range(3):
                dv.append((0.0, 0.0, 1.0, 8.0 if i == 1 else 0.0, 0.0))
        lumps[33] = b"".join(struct.pack("<fffff", *v) for v in dv)
        info = struct.pack("<3fiiiifiH", *verts[0], 0, 0, 1, 0, 0.0, 1, 0)
        lumps[26] = info + bytes(176 - len(info))
    ents = ('{\n"classname" "worldspawn"\n}\n'
            '{\n"classname" "info_player_terrorist"\n"origin" "-32 0 16"\n"angles" "0 90 0"\n}\n'
            '{\n"classname" "info_player_counterterrorist"\n"origin" "32 64 16"\n"angles" "0 270 0"\n}\n'
            + extra_entities)
    lumps[0] = ents.encode() + b"\0"

    header_size = 8 + 64 * 16 + 4
    body = b""
    dirs = []
    for i in range(64):
        raw = lumps.get(i, b"")
        if compress and raw and i != 0:
            raw = _lzma_lump(raw)
        dirs.append((header_size + len(body), len(raw), (0 if version == 19 else 1) if i == 10 else 0))
        body += raw
        body += b"\0" * (-len(body) % 4)
    head = b"VBSP" + struct.pack("<i", version)
    for ofs, ln, ver in dirs:
        head += struct.pack("<iiii", ofs, ln, ver, 0)
    head += struct.pack("<i", 1)
    return head + body


def build_gma(files: List[Tuple[str, bytes]]) -> bytes:
    out = b"GMAD" + bytes([3]) + struct.pack("<QQ", 0, 0) + b"\0"
    out += b"test addon\0{}\0author\0" + struct.pack("<i", 1)
    for i, (name, data) in enumerate(files, 1):
        out += struct.pack("<I", i) + name.encode() + b"\0" + struct.pack("<qI", len(data), 0)
    out += struct.pack("<I", 0)
    for _, data in files:
        out += data
    return out + struct.pack("<I", 0)


def build_phy_box(half_inches: float = 16.0) -> bytes:
    """A .phy with one convex box ledge (IVP layout: metres, IVP axes)."""
    return build_phy_boxes([((0.0, 0.0, 0.0), half_inches)])


def build_phy_boxes(boxes) -> bytes:
    """A .phy with one solid of several convex box ledges [(centre, half size in inches)]. As in
    vphysics output, the ledges come first, back to back, then every ledge's points, and the size
    in each ledge header also counts its points. A third item True marks the box as the ledge tree's
    hull of other ledges (has-children flag)."""
    quads = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    tris = []
    for a, b, c, d in quads:
        tris += [(a, b, c), (a, c, d)]
    ledge_size = 16 + 16 * len(tris)
    ledges_len = ledge_size * len(boxes)
    ledges, points = b"", b""
    for n, box in enumerate(boxes):
        centre, half = box[0], box[1]
        tree_hull = len(box) > 2 and box[2]
        # Source (x, y, z) = (ivp_x, ivp_z, -ivp_y) / 0.0254  =>  ivp = (x, -z, y) * 0.0254
        corners = [((centre[0] + sx * half) * 0.0254, (centre[1] + sy * half) * 0.0254, (centre[2] + sz * half) * 0.0254)
                   for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
        here = ledge_size * n
        point_ofs = ledges_len + len(points) - here
        flags = (((ledge_size + 8 * 16) // 16) << 8) | 4 | (1 if tree_hull else 0)  # is_compact, has_children
        ledge = struct.pack("<iiIhh", point_ofs, 0, flags, len(tris), 0)
        for i, (a, b, c) in enumerate(tris):
            ledge += struct.pack("<I", i) + b"".join(struct.pack("<I", v) for v in (a, b, c))
        ledges += ledge
        points += b"".join(struct.pack("<ffff", x, -z, y, 0.0) for x, y, z in corners)
    surface = struct.pack("<3f3ffIi3i", 0, 0, 0, 0, 0, 0, 1.0, 0, 48 + len(ledges) + len(points), 0, 0, 0)
    body = b"VPHY" + struct.pack("<hhi3fi", 0x100, 0, 0, 0, 0, 0, 0) + surface + ledges + points
    return struct.pack("<iiii", 16, 0, 1, 0) + struct.pack("<i", len(body)) + body


def kv3_encode(root) -> bytes:
    """Binary KeyValues 3 (version 4, stored, blobs as blocks) for dicts, lists, ints, floats,
    strings, bools and bytes: the subset the Source 2 physics reader uses."""
    one, ints, eights, types, strings, blocks = bytearray(), [0], bytearray(), bytearray(), [], []

    def sidx(t):
        if t not in strings:
            strings.append(t)
        return strings.index(t)

    def put(v):
        if v is None:
            types.append(1)
        elif isinstance(v, bool):
            types.append(13 if v else 14)
        elif isinstance(v, int):
            types.append(11)
            ints.append(v)
        elif isinstance(v, float):
            types.append(19)
            ints.append(struct.unpack("<i", struct.pack("<f", v))[0])
        elif isinstance(v, str):
            types.append(6)
            ints.append(sidx(v))
        elif isinstance(v, (bytes, bytearray)):
            types.append(7)
            blocks.append(bytes(v))
        elif isinstance(v, list):
            types.append(8)
            ints.append(len(v))
            for x in v:
                put(x)
        elif isinstance(v, dict):
            types.append(9)
            ints.append(len(v))
            for k, x in v.items():
                ints.append(sidx(k))
                put(x)
        else:
            raise TypeError(type(v))

    put(root)
    ints[0] = len(strings)
    buf = bytearray(one)
    buf += bytes((-len(buf)) % 4)
    buf += struct.pack(f"<{len(ints)}i", *ints)
    buf += bytes((-len(buf)) % 8)
    buf += eights
    strtypes = b"".join(t.encode() + b"\0" for t in strings) + bytes(types)
    buf += strtypes
    if blocks:
        buf += struct.pack(f"<{len(blocks)}I", *[len(b) for b in blocks]) + struct.pack("<I", 0xFFEEDD00)
        buf += struct.pack(f"<{len(blocks)}H", *[len(b) for b in blocks])
    head = b"\x043VK" + bytes(16) + struct.pack("<IHH", 0, 0, 16384)
    head += struct.pack("<IIIIHH", len(one), len(ints), len(eights) // 8, len(strtypes), 0, 0)
    head += struct.pack("<IIII", len(buf), len(buf), len(blocks), sum(len(b) for b in blocks))
    head += struct.pack("<II", 0, 0)
    return head + bytes(buf) + b"".join(blocks)


def build_vmdl_c(hulls, meshes=(), group: str = "default") -> bytes:
    """A compiled Source 2 model with only a PHYS block: convex hulls [(lo, hi) boxes] and triangle
    meshes [(vertices, triangles)], in model space."""
    def hull(lo, hi):
        verts = [(x, y, z) for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])]
        planes = []
        for k in range(3):
            n = [0.0, 0.0, 0.0]
            n[k] = 1.0
            planes.append((n[0], n[1], n[2], hi[k]))
            planes.append((-n[0], -n[1], -n[2], -lo[k]))
        return {"m_nCollisionAttributeIndex": 0, "m_Hull": {
            "m_Vertices": b"".join(struct.pack("<3f", *v) for v in verts),
            "m_Planes": b"".join(struct.pack("<4f", *p) for p in planes)}}

    def mesh(verts, tris):
        return {"m_nCollisionAttributeIndex": 0, "m_Mesh": {
            "m_Vertices": b"".join(struct.pack("<3f", *v) for v in verts),
            "m_Triangles": b"".join(struct.pack("<3i", *t) for t in tris)}}

    root = {"m_boneNames": [], "m_bindPose": [],
            "m_parts": [{"m_rnShape": {"m_hulls": [hull(lo, hi) for lo, hi in hulls],
                                       "m_meshes": [mesh(v, t) for v, t in meshes]},
                         "m_nCollisionAttributeIndex": 0}],
            "m_collisionAttributes": [{"m_CollisionGroupString": group}]}
    phys = kv3_encode(root)
    # header: size, header version 12, version 1, block offset 8 (from offset 8), 1 block
    table = b"PHYS" + struct.pack("<II", 8, len(phys))  # data right after the table entry
    data = struct.pack("<IHHII", 0, 12, 1, 8, 1) + table + phys
    return struct.pack("<I", len(data)) + data[4:]


def box_mesh(lo, hi, offset=(0.0, 0.0, 0.0)):
    """Vertices and triangles of a closed box."""
    verts = [(x + offset[0], y + offset[1], z + offset[2])
             for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])]
    quads = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    tris = []
    for a, b, c, d in quads:
        tris += [(a, b, c), (a, c, d)]
    return verts, tris


def build_vvd(points) -> bytes:
    head = b"IDSV" + struct.pack("<iii", 4, 0, 1) + struct.pack("<8i", len(points), 0, 0, 0, 0, 0, 0, 0)
    head += struct.pack("<iiii", 0, 0, 64, 0)
    verts = b"".join(bytes(16) + struct.pack("<fff", *p) + struct.pack("<fff", 0, 0, 1) + struct.pack("<ff", 0, 0)
                     for p in points)
    return head + verts


VMF_BOX = """
versioninfo { "editorversion" "400" }
world
{
    "id" "1"
    "classname" "worldspawn"
    solid
    {
        "id" "2"
        side { "id" "1" "plane" "(-64 64 64) (64 64 64) (64 -64 64)" "material" "BRICK/BRICKWALL001A"
               "uaxis" "[1 0 0 0] 0.25" "vaxis" "[0 -1 0 0] 0.25" }
        side { "id" "2" "plane" "(-64 -64 0) (64 -64 0) (64 64 0)" "material" "CONCRETE/CONCRETEFLOOR001A"
               "uaxis" "[1 0 0 0] 0.25" "vaxis" "[0 -1 0 0] 0.25" }
        side { "id" "3" "plane" "(-64 64 64) (-64 -64 64) (-64 -64 0)" "material" "BRICK/BRICKWALL001A"
               "uaxis" "[0 1 0 0] 0.25" "vaxis" "[0 0 -1 0] 0.25" }
        side { "id" "4" "plane" "(64 64 0) (64 -64 0) (64 -64 64)" "material" "BRICK/BRICKWALL001A"
               "uaxis" "[0 1 0 0] 0.25" "vaxis" "[0 0 -1 0] 0.25" }
        side { "id" "5" "plane" "(64 64 64) (-64 64 64) (-64 64 0)" "material" "BRICK/BRICKWALL001A"
               "uaxis" "[1 0 0 0] 0.25" "vaxis" "[0 0 -1 0] 0.25" }
        side { "id" "6" "plane" "(64 -64 0) (-64 -64 0) (-64 -64 64)" "material" "BRICK/BRICKWALL001A"
               "uaxis" "[1 0 0 0] 0.25" "vaxis" "[0 0 -1 0] 0.25" }
    }
}
entity
{
    "id" "3"
    "classname" "info_player_counterterrorist"
    "origin" "0 0 64"
    "angles" "0 0 0"
}
"""
