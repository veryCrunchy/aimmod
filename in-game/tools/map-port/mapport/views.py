"""First-person check renders: what a player standing at a spawn sees, with holes highlighted.

Faces are drawn one-sided, as the game draws them, from eye height (64 units above the feet).
Holes are counted as pixels where the view reaches the backdrop ground plane (orange) or nothing at
all below the horizon (magenta). A hole means missing geometry (often an unported model) or a face
wound the wrong way. Sky above the horizon is expected.
"""
from __future__ import annotations

import math
from typing import Dict, List, Sequence, Tuple

from . import geometry as g
from . import scene
from .materials import Slot
from .preview import _colour, _png

EYE = 64.0
VOID = (255, 0, 255)
BACKDROP = (255, 140, 0)
SKY = (40, 52, 80)
NEAR = 2.0


def _clip_near(poly: List[Tuple[float, float, float]]) -> List[Tuple[float, float, float]]:
    out = []
    for i, p in enumerate(poly):
        q = poly[(i + 1) % len(poly)]
        if p[2] >= NEAR:
            out.append(p)
        if (p[2] >= NEAR) != (q[2] >= NEAR):
            t = (NEAR - p[2]) / (q[2] - p[2])
            out.append((p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t, NEAR))
    return out


def render_view(sc: scene.Scene, slots: List[Slot], tex_slot: Dict[str, int], feet, yaw: float,
                pitch: float = -8.0, fov: float = 100.0, w: int = 400, h: int = 225,
                max_dist: float = 6000.0) -> Tuple[bytes, float]:
    eye = (feet[0], feet[1], feet[2] + EYE)
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    fwd = (cp * cy, cp * sy, sp)
    right = (sy, -cy, 0.0)
    up = g.cross(right, fwd)
    f = (w / 2) / math.tan(math.radians(fov / 2))
    zbuf = [[1e30] * w for _ in range(h)]
    img = [bytearray(bytes(VOID) * w) for _ in range(h)]
    sun = g.normalize((0.4, 0.3, 0.85))
    for b in sc.brushes:
        if b.kind == scene.CLIP:
            continue
        lo, hi = b.bounds()
        if any(lo[k] > eye[k] + max_dist or hi[k] < eye[k] - max_dist for k in range(3)):
            continue
        for face in b.faces:
            if face.texture == "tools/toolsskybox":
                continue  # left out of the exported mesh too
            if g.dot(face.normal, g.sub(face.polygon[0], eye)) >= 0:
                continue  # back face: the game culls it too
            cam = []
            for p in face.polygon:
                d = g.sub(p, eye)
                cam.append((g.dot(d, right), g.dot(d, up), g.dot(d, fwd)))
            cam = _clip_near(cam)
            if len(cam) < 3:
                continue
            scr = [(w / 2 + f * x / z, h / 2 - f * y / z, z) for x, y, z in cam]
            if b.source == "backdrop":
                col = BACKDROP
            else:
                col = _colour(b, face, slots, tex_slot)
                shade = 0.55 + 0.45 * max(0.0, g.dot(face.normal, sun))
                col = tuple(int(c * shade) for c in col)
            for i in range(1, len(scr) - 1):
                _tri(scr[0], scr[i], scr[i + 1], col, zbuf, img, w, h)
    void = below = 0
    for yy in range(h):
        # elevation of this pixel row's ray (approximate: centre column)
        ray_up = (h / 2 - yy) / f
        elev = math.atan2(sp + ray_up * cp, cp - ray_up * sp)
        for xx in range(w):
            if zbuf[yy][xx] < 1e30:
                if elev <= 0 and img[yy][xx * 3:xx * 3 + 3] == bytes(BACKDROP):
                    void += 1
                continue
            if elev > 0:
                img[yy][xx * 3:xx * 3 + 3] = bytes(SKY)
            else:
                void += 1
        if elev <= 0:
            below += w
    return _png(w, h, img), (void / below if below else 0.0)


def _tri(a, b, c, col, zbuf, img, w, h):
    minx, maxx = max(0, int(min(a[0], b[0], c[0]))), min(w - 1, int(max(a[0], b[0], c[0])) + 1)
    miny, maxy = max(0, int(min(a[1], b[1], c[1]))), min(h - 1, int(max(a[1], b[1], c[1])) + 1)
    if minx > maxx or miny > maxy:
        return
    den = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
    if abs(den) < 1e-9:
        return
    ia, ib, ic = 1 / a[2], 1 / b[2], 1 / c[2]
    cb = bytes(col)
    for y in range(miny, maxy + 1):
        zr, ir = zbuf[y], img[y]
        py = y + 0.5
        for x in range(minx, maxx + 1):
            px = x + 0.5
            l1 = ((b[1] - c[1]) * (px - c[0]) + (c[0] - b[0]) * (py - c[1])) / den
            l2 = ((c[1] - a[1]) * (px - c[0]) + (a[0] - c[0]) * (py - c[1])) / den
            l3 = 1 - l1 - l2
            if l1 < -1e-6 or l2 < -1e-6 or l3 < -1e-6:
                continue
            z = 1 / (l1 * ia + l2 * ib + l3 * ic)
            if z < zr[x]:
                zr[x] = z
                ir[x * 3:x * 3 + 3] = cb


def floor_points(sc: scene.Scene, count: int = 10) -> List[Tuple[float, float, float]]:
    """Well-spread standing spots on upward faces with room for a player hull."""
    from .spawns import _solids, blocked, hull_box
    cands = []
    for b in sc.brushes:
        if b.kind != scene.SOLID or b.source == "backdrop":
            continue
        for f in b.faces:
            if f.normal[2] > 0.85 and g.polygon_area(f.polygon) > 64 * 64:
                cands.append(g.centroid(f.polygon))
    if not cands or not sc.spawns:
        return []
    solids = _solids(sc)
    chosen: List[Tuple[float, float, float]] = [s.origin for s in sc.spawns]
    out: List[Tuple[float, float, float]] = []
    pool = cands[:: max(1, len(cands) // 2000)]
    lo_z = min(s.origin[2] for s in sc.spawns) - 256
    hi_z = max(s.origin[2] for s in sc.spawns) + 256
    pool = [p for p in pool if lo_z <= p[2] <= hi_z]
    while pool and len(out) < count:
        best = max(pool, key=lambda p: min((p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2 for q in chosen))
        pool.remove(best)
        near = [s for s in solids if all(s.lo[k] < best[k] + 128 and s.hi[k] > best[k] - 128 for k in range(3))]
        if blocked(hull_box(best), near):
            continue
        chosen.append(best)
        out.append(best)
    return out


def viewpoints(sc: scene.Scene, extra: Sequence[Tuple[str, Tuple[float, float, float], float]] = (),
               samples: int = 10):
    """Each team's central spawn looking four ways, well-spread floor spots, and any extra views."""
    out = []
    for team in sorted({s.team for s in sc.spawns}):
        grp = [s for s in sc.spawns if s.team == team]
        mid = g.centroid([q.origin for q in grp])
        c = min(grp, key=lambda s: (s.origin[0] - mid[0]) ** 2 + (s.origin[1] - mid[1]) ** 2)
        for yaw in (0, 90, 180, 270):
            out.append((f"team{team}_yaw{yaw}", c.origin, float(yaw)))
    if not extra:
        for i, p in enumerate(floor_points(sc, samples)):
            for yaw in (0, 90, 180, 270):
                out.append((f"spot{i}_{int(p[0])}_{int(p[1])}_{int(p[2])}_yaw{yaw}", p, float(yaw)))
    return out + list(extra)


def render_all(sc: scene.Scene, slots: List[Slot], tex_slot: Dict[str, int], extra=()) -> List[Tuple[str, bytes, float]]:
    return [(name, *render_view(sc, slots, tex_slot, feet, yaw)) for name, feet, yaw in viewpoints(sc, extra)]
