"""Preview check: top-down (radar orientation) and side views with the player hull at every spawn.

Everything is drawn from the converted scene in Source units, so the red/blue hull boxes (32 x 72,
the CS standing hull) show at a glance whether doorways, ceilings and spawns fit the player.
"""
from __future__ import annotations

import struct
import zlib
from typing import Dict, List, Tuple

from . import scene
from .materials import Slot
from .spawns import FLOOR_GAP, HULL

BG = (24, 24, 24)
TEAM_COL = {1: (235, 60, 40), 2: (60, 120, 245), 0: (240, 230, 60)}


def _png(width: int, height: int, rows: List[bytearray]) -> bytes:
    raw = b"".join(b"\0" + bytes(r) for r in rows)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


class Canvas:
    def __init__(self, w: int, h: int):
        self.w, self.h = w, h
        self.z = [[-1e30] * w for _ in range(h)]
        self.px = [bytearray(bytes(BG) * w) for _ in range(h)]

    def put(self, x: int, y: int, col) -> None:
        if 0 <= x < self.w and 0 <= y < self.h:
            self.px[y][x * 3:x * 3 + 3] = bytes(col)

    def rect(self, x0, y0, x1, y1, col, fill=False) -> None:
        x0, x1 = sorted((int(round(x0)), int(round(x1))))
        y0, y1 = sorted((int(round(y0)), int(round(y1))))
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                if fill or x in (x0, x1) or y in (y0, y1):
                    self.put(x, y, col)

    def tri(self, a, b, c, col, shade_lo, shade_hi) -> None:
        minx, maxx = max(0, int(min(a[0], b[0], c[0]))), min(self.w - 1, int(max(a[0], b[0], c[0])) + 1)
        miny, maxy = max(0, int(min(a[1], b[1], c[1]))), min(self.h - 1, int(max(a[1], b[1], c[1])) + 1)
        den = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
        if abs(den) < 1e-9:
            return
        span = (shade_hi - shade_lo) or 1.0
        for y in range(miny, maxy + 1):
            zrow, prow = self.z[y], self.px[y]
            for x in range(minx, maxx + 1):
                px, py = x + 0.5, y + 0.5
                l1 = ((b[1] - c[1]) * (px - c[0]) + (c[0] - b[0]) * (py - c[1])) / den
                l2 = ((c[1] - a[1]) * (px - c[0]) + (a[0] - c[0]) * (py - c[1])) / den
                l3 = 1 - l1 - l2
                if l1 < -1e-6 or l2 < -1e-6 or l3 < -1e-6:
                    continue
                z = l1 * a[2] + l2 * b[2] + l3 * c[2]
                if z <= zrow[x]:
                    continue
                zrow[x] = z
                s = 0.4 + 0.6 * max(0.0, min(1.0, (z - shade_lo) / span))
                prow[x * 3:x * 3 + 3] = bytes(min(255, int(v * s)) for v in col)


def _colour(b: scene.Brush, f: scene.Face, slots: List[Slot], tex_slot: Dict[str, int]):
    if b.kind == scene.GLASS:
        return (120, 190, 230)
    sl = slots[tex_slot[f.texture]] if f.texture in tex_slot and slots else None
    return (int(sl.tint[0:2], 16), int(sl.tint[2:4], 16), int(sl.tint[4:6], 16)) if sl else (150, 150, 150)


def _view(sc, slots, tex_slot, axis_u, axis_v, depth, facing, flip_v, scale, lo, hi, dlo, dhi, keep=None):
    w, h = int((hi[0] - lo[0]) * scale) + 1, int((hi[1] - lo[1]) * scale) + 1
    cv = Canvas(w, h)

    def proj(p) -> Tuple[float, float, float]:
        u = (axis_u(p) - lo[0]) * scale
        v = (hi[1] - axis_v(p)) * scale if flip_v else (axis_v(p) - lo[1]) * scale
        return (u, v, depth(p))

    for b in sc.brushes:
        if b.kind == scene.CLIP or b.source == "backdrop":
            continue
        stand_in = all(f.texture.startswith("tools/") for f in b.faces)
        for f in b.faces:
            if not facing(f.normal) or (keep and not keep(f.polygon)):
                continue
            if f.texture.startswith("tools/") and not stand_in:
                continue  # hidden faces (outer shells, caulk) would cover the map from above
            col = _colour(b, f, slots, tex_slot)
            pts = [proj(p) for p in f.polygon]
            for i in range(1, len(pts) - 1):
                cv.tri(pts[0], pts[i], pts[i + 1], col, dlo, dhi)
    return cv, proj, scale


def render(sc: scene.Scene, slots: List[Slot], tex_slot: Dict[str, int], size: int = 1400) -> bytes:
    pts = [p for b in sc.brushes if b.kind != scene.CLIP and b.source != "backdrop"
           for f in b.faces for p in f.polygon]
    pts += [s.origin for s in sc.spawns]
    if not pts:
        return _png(1, 1, [bytearray(bytes(BG))])
    mn = [min(p[k] for p in pts) for k in range(3)]
    mx = [max(p[k] for p in pts) for k in range(3)]
    scale = (size - 1) / max(mx[0] - mn[0], mx[1] - mn[1], 1.0)
    # top view: u = x (east right), v = y (north up); depth = z
    top, tproj, tscale = _view(sc, slots, tex_slot, lambda p: p[0], lambda p: p[1], lambda p: p[2],
                               lambda n: n[2] > 0.3, True, scale, (mn[0], mn[1]), (mx[0], mx[1]), mn[2], mx[2])
    for sp in sc.spawns:
        col = TEAM_COL.get(sp.team, TEAM_COL[0])
        x, y, _z = sp.origin
        r = HULL["half"]
        a, b = tproj((x - r, y - r, 0)), tproj((x + r, y + r, 0))
        top.rect(a[0] - 1, a[1] + 1, b[0] + 1, b[1] - 1, col, fill=True)
    # 256-unit scale bar (white) in the top view's corner
    bar = 256 * tscale
    top.rect(10, top.h - 14, 10 + bar, top.h - 10, (255, 255, 255), fill=True)
    views = [top]
    # One zoomed side section (looking north) per team, cut through its spawn area.
    for team in sorted({s.team for s in sc.spawns}):
        group = [s for s in sc.spawns if s.team == team]
        y0 = min(s.origin[1] for s in group) - 160
        y1 = max(s.origin[1] for s in group) + 160
        x0 = min(s.origin[0] for s in group) - 768
        x1 = max(s.origin[0] for s in group) + 768
        z0 = min(s.origin[2] for s in group) - 128
        z1 = max(s.origin[2] for s in group) + 512
        sscale = (size - 1) / (x1 - x0)

        def keep(poly, y0=y0, y1=y1, x0=x0, x1=x1, z0=z0, z1=z1):
            return (max(p[1] for p in poly) >= y0 and min(p[1] for p in poly) <= y1
                    and max(p[0] for p in poly) >= x0 and min(p[0] for p in poly) <= x1
                    and max(p[2] for p in poly) >= z0 and min(p[2] for p in poly) <= z1)

        side, sproj, _ = _view(sc, slots, tex_slot, lambda p: p[0], lambda p: p[2], lambda p: -p[1],
                               lambda n: n[1] < -0.3 or abs(n[2]) > 0.7, True, sscale, (x0, z0), (x1, z1),
                               -y1, -y0, keep)
        col = TEAM_COL.get(team, TEAM_COL[0])
        for s in group:
            x, _y, z = s.origin
            r = HULL["half"]
            a = sproj((x - r, 0, z + FLOOR_GAP))
            b = sproj((x + r, 0, z + FLOOR_GAP + HULL["height"]))
            side.rect(a[0], a[1], b[0], b[1], col)
            side.rect(a[0] + 1, a[1] - 1, b[0] - 1, b[1] + 1, col)
        # white bar = one CS player height (72 units)
        bh = HULL["height"] * sscale
        side.rect(6, side.h - 6 - bh, 10, side.h - 6, (255, 255, 255), fill=True)
        views.append(side)
    w = max(v.w for v in views)
    rows = []
    for cv in views:
        for r in cv.px:
            rows.append(r + bytearray(bytes(BG) * (w - cv.w)))
        rows.append(bytearray(b"\x80" * (w * 3)))
    return _png(w, len(rows), rows)
