"""Collision shapes of Source 2 models (compiled `.vmdl_c`), read from an installed Source 2 game.

Counter-Strike 2 still ships most of the CS:S / CS:GO prop models under their old names
(`models/props/de_nuke/crate_large.vmdl_c`), so a CS:S map's stock props that no installed Source 1
game provides can take their shape from CS2's physics data. Only the PHYS block is read: its convex
hulls (vertices and planes) and triangle meshes, in model space and Source units. Nothing is copied
into the port except hull geometry built from it, as for Source 1 stock models.

Format notes:
* A compiled resource is a header (size, header version, version, block offset, block count) and a
  table of blocks (4-character type, offset relative to the field, size).
* PHYS is binary KeyValues 3 (versions 3 and 4 are read). Its main buffer is LZ4-compressed (or
  stored), and holds, in order: single bytes, 2-byte values (v4), 4-byte values, 8-byte values,
  the strings (the first 4-byte value is their count) and the type bytes. Binary blobs (hull
  vertices, planes, mesh triangles) are separate blocks after the main buffer, compressed as one
  LZ4 stream in chunks whose sizes follow the 0xFFEEDD00 trailer.
"""
from __future__ import annotations

import struct
from typing import Dict, List, Optional, Sequence, Tuple

Vec = Tuple[float, float, float]
Plane = Tuple[float, float, float, float]

TRAILER = 0xFFEEDD00
# collision groups that never stop a player
NONSOLID_GROUPS = ("debris", "trigger", "nonsolid", "conditionallysolid")


class Source2Error(ValueError):
    pass


def lz4_block(src: bytes, out: bytearray, limit: int) -> None:
    """Decompress one LZ4 block onto `out`. Back-references may reach into what `out` already holds
    (LZ4 streaming: each chunk uses the earlier output as its dictionary)."""
    i, n = 0, len(src)
    target = len(out) + limit
    while i < n:
        tok = src[i]
        i += 1
        lit = tok >> 4
        if lit == 15:
            while True:
                b = src[i]
                i += 1
                lit += b
                if b != 255:
                    break
        out += src[i:i + lit]
        i += lit
        if i >= n or len(out) >= target:
            break
        off = src[i] | (src[i + 1] << 8)
        i += 2
        ml = tok & 15
        if ml == 15:
            while True:
                b = src[i]
                i += 1
                ml += b
                if b != 255:
                    break
        ml += 4
        start = len(out) - off
        if off <= 0 or start < 0:
            raise Source2Error("bad LZ4 offset")
        if off >= ml:
            out += out[start:start + ml]
        else:
            for k in range(ml):
                out.append(out[start + k])


def resource_blocks(data: bytes) -> Dict[str, bytes]:
    if len(data) < 16:
        raise Source2Error("not a compiled resource")
    _size, _hv, _ver, bofs, bcount = struct.unpack_from("<IHHII", data, 0)
    at = 8 + bofs
    if bcount > 64 or at + 12 * bcount > len(data):
        raise Source2Error("not a compiled resource")
    out: Dict[str, bytes] = {}
    for _ in range(bcount):
        kind = data[at:at + 4].decode("latin-1")
        o, s = struct.unpack_from("<II", data, at + 4)
        start = at + 4 + o
        out[kind] = data[start:start + s]
        at += 12
    return out


class _KV3:
    def __init__(self, blk: bytes):
        if len(blk) < 72 or blk[1:4] != b"3VK" or blk[0] not in (3, 4):
            raise Source2Error("unsupported KV3 block")
        ver = blk[0]
        o = 20  # magic + format GUID
        method, _dict, frame = struct.unpack_from("<IHH", blk, o)
        o += 8
        nbytes, nints, neights, _strtypes = struct.unpack_from("<IIII", blk, o)
        o += 16 + 4  # + two u16 preallocation hints
        usize, csize, nblocks, _btotal = struct.unpack_from("<IIII", blk, o)
        o += 16
        nshorts = 0
        if ver >= 4:
            nshorts, _unk = struct.unpack_from("<II", blk, o)
            o += 8
        if method == 0:
            buf = bytearray(blk[o:o + usize])
            o += usize
        elif method == 1:
            buf = bytearray()
            lz4_block(blk[o:o + csize], buf, usize)
            o += csize
        else:
            raise Source2Error(f"KV3 compression {method} is not supported")
        if len(buf) != usize:
            raise Source2Error("KV3 buffer size mismatch")
        p = nbytes
        self.bytes_ = buf[:nbytes]
        self.shorts: Sequence[int] = ()
        if nshorts:
            p = (p + 1) & ~1
            self.shorts = struct.unpack_from(f"<{nshorts}h", buf, p)
            p += 2 * nshorts
        p = (p + 3) & ~3
        self.ints = struct.unpack_from(f"<{nints}i", buf, p)
        p += 4 * nints
        p = (p + 7) & ~7
        self.eights = bytes(buf[p:p + 8 * neights])
        p += 8 * neights
        self.ii = 1
        self.strings: List[str] = []
        for _ in range(self.ints[0] if nints else 0):
            e = buf.index(0, p)
            self.strings.append(buf[p:e].decode("utf-8", "replace"))
            p = e + 1
        end = len(buf)
        self.blocks: List[bytes] = []
        if nblocks:
            idx = buf.rfind(struct.pack("<I", TRAILER))
            if idx < p + 4 * nblocks:
                raise Source2Error("KV3 block trailer not found")
            sizes = struct.unpack_from(f"<{nblocks}I", buf, idx - 4 * nblocks)
            end = idx - 4 * nblocks
            chunks = struct.unpack_from(f"<{(len(buf) - idx - 4) // 2}H", buf, idx + 4)
            stream = bytearray()
            q, ci = o, 0
            for s in sizes:
                start = len(stream)
                while len(stream) - start < s:
                    if ci >= len(chunks):
                        raise Source2Error("KV3 block chunks ran out")
                    c = chunks[ci]
                    ci += 1
                    if method == 1:
                        lz4_block(blk[q:q + c], stream, min(frame or 16384, s - (len(stream) - start)))
                    else:
                        stream += blk[q:q + c]
                    q += c
                self.blocks.append(bytes(stream[start:start + s]))
        self.types = buf[p:end]
        self.ti = self.bi = self.ei = self.si = self.blk = 0
        self.root = self._value()

    def _type(self) -> int:
        t = self.types[self.ti]
        self.ti += 1
        if t & 0x80:  # a flag byte follows (resource / panorama / soundevent strings)
            t &= 0x3F
            self.ti += 1
        return t

    def _int(self) -> int:
        v = self.ints[self.ii]
        self.ii += 1
        return v

    def _eight(self, fmt: str):
        v = struct.unpack_from(fmt, self.eights, 8 * self.ei)[0]
        self.ei += 1
        return v

    def _byte(self) -> int:
        v = self.bytes_[self.bi]
        self.bi += 1
        return v

    def _value(self, t: Optional[int] = None):
        if t is None:
            t = self._type()
        if t == 1:
            return None
        if t == 2:
            return bool(self._byte())
        if t == 3:
            return self._eight("<q")
        if t == 4:
            return self._eight("<Q")
        if t == 5:
            return self._eight("<d")
        if t == 6:
            i = self._int()
            return "" if i < 0 else self.strings[i]
        if t == 7:
            if self.blocks:
                b = self.blocks[self.blk]
                self.blk += 1
                return b
            n = self._int()
            v = bytes(self.bytes_[self.bi:self.bi + n])
            self.bi += n
            return v
        if t == 8:
            return [self._value() for _ in range(self._int())]
        if t == 9:
            obj = {}
            for _ in range(self._int()):
                k = self.strings[self._int()]
                obj[k] = self._value()
            return obj
        if t in (10, 24):  # typed array; 24 stores its length in a byte
            n = self._int() if t == 10 else self._byte()
            sub = self._type()
            return [self._value(sub) for _ in range(n)]
        if t == 11:
            return self._int()
        if t == 12:
            return self._int() & 0xFFFFFFFF
        if t in (13, 14, 15, 16, 17, 18):
            return {13: True, 14: False, 15: 0, 16: 1, 17: 0.0, 18: 1.0}[t]
        if t == 19:
            return struct.unpack("<f", struct.pack("<i", self._int()))[0]
        if t in (20, 21):
            v = self.shorts[self.si]
            self.si += 1
            return v if t == 20 else v & 0xFFFF
        if t == 22:
            v = self._byte()
            return v - 256 if v > 127 else v
        if t == 23:
            return self._byte()
        raise Source2Error(f"unsupported KV3 type {t}")


def read_kv3(blk: bytes):
    try:
        return _KV3(blk).root
    except (struct.error, IndexError, KeyError, ValueError) as exc:
        raise Source2Error(str(exc)) from exc


def _floats(blob: bytes, n: int) -> List[tuple]:
    return [struct.unpack_from(f"<{n}f", blob, i) for i in range(0, len(blob) - 4 * n + 1, 4 * n)]


class Shapes:
    """A model's collision: convex hulls (vertices + planes) and triangle meshes, in model space."""

    def __init__(self):
        self.hulls: List[Tuple[List[Vec], List[Plane]]] = []
        self.meshes: List[Tuple[List[Vec], List[Tuple[int, int, int]]]] = []
        self.nonsolid_pieces = 0

    def __bool__(self) -> bool:
        return bool(self.hulls or self.meshes)


def _matrix(m) -> Optional[List[List[float]]]:
    flat = [float(v) for row in m for v in (row if isinstance(row, list) else [row])] if isinstance(m, list) else []
    if len(flat) == 12:
        return [flat[0:4], flat[4:8], flat[8:12]]
    return None


def _apply(mat, p: Vec) -> Vec:
    return tuple(mat[r][0] * p[0] + mat[r][1] * p[1] + mat[r][2] * p[2] + mat[r][3] for r in range(3))  # type: ignore


def physics_shapes(vmdl: bytes) -> Shapes:
    """Collision of a compiled model (an empty Shapes when it has no physics)."""
    shapes = Shapes()
    blocks = resource_blocks(vmdl)
    if "PHYS" not in blocks:
        return shapes
    root = read_kv3(blocks["PHYS"])
    attrs = root.get("m_collisionAttributes") or []
    groups = [str(a.get("m_CollisionGroupString", "")).lower() for a in attrs if isinstance(a, dict)]
    poses = [_matrix(m) for m in root.get("m_bindPose") or []]
    for pi, part in enumerate(root.get("m_parts") or []):
        mat = poses[pi] if pi < len(poses) else None
        shape = part.get("m_rnShape") or {}

        def solid(piece) -> bool:
            i = piece.get("m_nCollisionAttributeIndex", part.get("m_nCollisionAttributeIndex", 0)) or 0
            grp = groups[i] if 0 <= i < len(groups) else ""
            return not any(w in grp for w in NONSOLID_GROUPS)

        for h in shape.get("m_hulls") or []:
            hull = h.get("m_Hull") or {}
            verts = [tuple(v) for v in _floats(hull.get("m_Vertices") or b"", 3)]
            if not verts and hull.get("m_VertexPositions"):
                verts = [tuple(v) for v in _floats(hull["m_VertexPositions"], 3)]
            planes = [tuple(p) for p in _floats(hull.get("m_Planes") or b"", 4)]
            if len(verts) < 4:
                continue
            if not solid(h):
                shapes.nonsolid_pieces += 1
                continue
            if mat:
                verts = [_apply(mat, v) for v in verts]
                moved = []
                for nx, ny, nz, d in planes:
                    n = _apply(mat, (nx, ny, nz))
                    n = (n[0] - mat[0][3], n[1] - mat[1][3], n[2] - mat[2][3])  # rotate only
                    moved.append((n[0], n[1], n[2], d + n[0] * mat[0][3] + n[1] * mat[1][3] + n[2] * mat[2][3]))
                planes = moved
            shapes.hulls.append((verts, planes))  # type: ignore[arg-type]
        for m in shape.get("m_meshes") or []:
            mesh = m.get("m_Mesh") or {}
            verts = [tuple(v) for v in _floats(mesh.get("m_Vertices") or b"", 3)]
            tri_blob = mesh.get("m_Triangles") or b""
            tris = [struct.unpack_from("<3i", tri_blob, i) for i in range(0, len(tri_blob) - 11, 12)]
            tris = [t for t in tris if all(0 <= k < len(verts) for k in t)]
            if not verts or not tris:
                continue
            if not solid(m):
                shapes.nonsolid_pieces += 1
                continue
            if mat:
                verts = [_apply(mat, v) for v in verts]
            shapes.meshes.append((verts, tris))  # type: ignore[arg-type]
    return shapes
