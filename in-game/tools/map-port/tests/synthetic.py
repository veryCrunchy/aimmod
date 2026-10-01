"""Build tiny synthetic Source BSP / GMA files for tests (no game data involved)."""
from __future__ import annotations

import lzma
import struct
from typing import Dict, List, Tuple

BOX_MIN = (-64.0, -32.0, 0.0)
BOX_MAX = (64.0, 96.0, 16.0)


def _lzma_lump(raw: bytes) -> bytes:
    enc = lzma.LZMACompressor(format=lzma.FORMAT_ALONE, filters=[{"id": lzma.FILTER_LZMA1, "dict_size": 1 << 16}])
    alone = enc.compress(raw) + enc.flush()
    props, body = alone[:5], alone[13:]
    return b"LZMA" + struct.pack("<II", len(raw), len(body)) + props + body


def build_bsp(version: int = 20, compress: bool = False, with_displacement: bool = True,
              extra_entities: str = "") -> bytes:
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
    lumps[1] = b"".join(struct.pack("<ffffi", *p, 0) for p in planes)
    lumps[19] = b"".join(struct.pack("<Hhhh", *s) for s in sides)
    lumps[18] = struct.pack("<iii", 0, 6, 0x1) + struct.pack("<iii", 6, 6, 0x10000)
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
    h = half_inches * 0.0254
    # Source (x, y, z) = (ivp_x, ivp_z, -ivp_y) / 0.0254  =>  ivp = (x, -z, y) * 0.0254
    corners = [(sx * h, sy * h, sz * h) for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
    ivp = [(x, -z, y) for x, y, z in corners]
    quads = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    tris = []
    for a, b, c, d in quads:
        tris += [(a, b, c), (a, c, d)]
    ledge_size = 16 + 16 * len(tris)
    ledge = struct.pack("<iiIhh", ledge_size, 0, (ledge_size // 16) << 8, len(tris), 0)
    for i, (a, b, c) in enumerate(tris):
        ledge += struct.pack("<I", i) + b"".join(struct.pack("<I", v) for v in (a, b, c))
    points = b"".join(struct.pack("<ffff", *p, 0.0) for p in ivp)
    surface = struct.pack("<3f3ffIi3i", 0, 0, 0, 0, 0, 0, 1.0, 0, 48 + len(ledge) + len(points), 0, 0, 0)
    body = b"VPHY" + struct.pack("<hhi3fi", 0x100, 0, 0, 0, 0, 0, 0) + surface + ledge + points
    return struct.pack("<iiii", 16, 0, 1, 0) + struct.pack("<i", len(body)) + body


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
