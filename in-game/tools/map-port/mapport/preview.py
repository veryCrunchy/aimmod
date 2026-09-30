"""Top-down PNG preview of a converted scene (+X right, +Y up, like a CS radar image)."""
from __future__ import annotations

import struct
import zlib
from typing import Dict, List

from . import scene
from .materials import Slot


def _png(width: int, height: int, rows: List[bytearray]) -> bytes:
    raw = b"".join(b"\0" + bytes(r) for r in rows)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


def render(sc: scene.Scene, slots: List[Slot], tex_slot: Dict[str, int], size: int = 1024) -> bytes:
    faces = []
    for b in sc.brushes:
        if b.kind == scene.CLIP:
            continue
        for f in b.faces:
            if f.normal[2] > 0.3:
                faces.append((b, f))
    if not faces:
        return _png(1, 1, [bytearray(3)])
    xs = [p[0] for _, f in faces for p in f.polygon]
    ys = [p[1] for _, f in faces for p in f.polygon]
    zs = [p[2] for _, f in faces for p in f.polygon]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    z0, z1 = min(zs), max(zs)
    scale = (size - 1) / max(x1 - x0, y1 - y0, 1.0)
    w, h = int((x1 - x0) * scale) + 1, int((y1 - y0) * scale) + 1
    zbuf = [[-1e30] * w for _ in range(h)]
    img = [bytearray(24 for _ in range(w * 3)) for _ in range(h)]
    for b, f in faces:
        sl = slots[tex_slot[f.texture]] if f.texture in tex_slot and slots else None
        base = (int(sl.tint[0:2], 16), int(sl.tint[2:4], 16), int(sl.tint[4:6], 16)) if sl else (150, 150, 150)
        if b.kind == scene.GLASS:
            base = (120, 190, 230)
        pts = [((p[0] - x0) * scale, (y1 - p[1]) * scale, p[2]) for p in f.polygon]
        for i in range(1, len(pts) - 1):
            _tri(pts[0], pts[i], pts[i + 1], base, z0, z1, zbuf, img, w, h)
    for sp in sc.spawns:
        cx, cy = int((sp.origin[0] - x0) * scale), int((y1 - sp.origin[1]) * scale)
        col = (230, 60, 40) if sp.team == 1 else (50, 110, 240) if sp.team == 2 else (240, 240, 60)
        for dy in range(-3, 4):
            for dx in range(-3, 4):
                if 0 <= cx + dx < w and 0 <= cy + dy < h:
                    img[cy + dy][(cx + dx) * 3:(cx + dx) * 3 + 3] = bytes(col)
    return _png(w, h, img)


def _tri(a, b, c, col, z0, z1, zbuf, img, w, h):
    minx, maxx = max(0, int(min(a[0], b[0], c[0]))), min(w - 1, int(max(a[0], b[0], c[0])) + 1)
    miny, maxy = max(0, int(min(a[1], b[1], c[1]))), min(h - 1, int(max(a[1], b[1], c[1])) + 1)
    den = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
    if abs(den) < 1e-9:
        return
    for y in range(miny, maxy + 1):
        for x in range(minx, maxx + 1):
            px, py = x + 0.5, y + 0.5
            l1 = ((b[1] - c[1]) * (px - c[0]) + (c[0] - b[0]) * (py - c[1])) / den
            l2 = ((c[1] - a[1]) * (px - c[0]) + (a[0] - c[0]) * (py - c[1])) / den
            l3 = 1 - l1 - l2
            if l1 < -1e-6 or l2 < -1e-6 or l3 < -1e-6:
                continue
            z = l1 * a[2] + l2 * b[2] + l3 * c[2]
            if z <= zbuf[y][x]:
                continue
            zbuf[y][x] = z
            shade = 0.45 + 0.55 * ((z - z0) / (z1 - z0) if z1 > z0 else 1.0)
            img[y][x * 3:x * 3 + 3] = bytes(min(255, int(v * shade)) for v in col)
