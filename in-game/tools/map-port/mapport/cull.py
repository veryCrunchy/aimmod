"""Hidden and doubled faces, removed at export (what vbsp/q3map do with CSG, which a brush-level port lacks).

- A face pressed against an opaque brush (an opposite-facing face on the same plane) can never be
  seen: the covered part is left out. Touching walls, floors under boxes and the inside of stacked
  brushes are most of a map's draw cost.
- Two faces on the same plane facing the same way z-fight where they overlap (overlapping brushes,
  a brush face under a displacement). The covered part of the lower-priority face is left out:
  displacements win over brushes (Source draws the displacement instead of its base face), then the
  larger face.

Only the exported faces change; the scene keeps every face for the checks, spawns and previews.
A brush always keeps every corner on some exported face, so its outline (and any collision KovaaK's
builds from the vertices) is unchanged; covered faces are covered by geometry that still collides.
"""
from __future__ import annotations

import math
from collections import defaultdict
from dataclasses import replace
from typing import Dict, List, Optional, Sequence, Tuple

from . import classify, geometry as g, scene

Vec = Tuple[float, float, float]
P2 = Tuple[float, float]
MIN_AREA = 1.0      # square units: smaller leftovers are dropped
KEEP_RATIO = 0.98   # a face that keeps this much of its area is exported unchanged
MAX_PIECES = 12     # more pieces than this: keep the face whole
OPAQUE = (scene.SOLID, scene.NONSOLID)
EXPORTED = (scene.SOLID, scene.NONSOLID, scene.GLASS, scene.WEAPON_CLIP)
SKY = "tools/toolsskybox"  # never exported on visible brushes (kovaaks_json)


def _key(normal: Vec, d: float) -> Tuple[int, int, int, int]:
    return (round(normal[0] * 1000), round(normal[1] * 1000), round(normal[2] * 1000), round(d * 10))


def _basis(n: Vec) -> Tuple[Vec, Vec]:
    a = (0.0, 0.0, 1.0) if abs(n[2]) < 0.9 else (1.0, 0.0, 0.0)
    u = g.normalize(g.cross(a, n))
    v = g.cross(n, u)
    return u, v


def _to2(poly: Sequence[Vec], u: Vec, v: Vec) -> List[P2]:
    return [(g.dot(p, u), g.dot(p, v)) for p in poly]


def _area2(poly: Sequence[P2]) -> float:
    return 0.5 * sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1]
                     for i in range(len(poly)))


def _ccw(poly: List[P2]) -> List[P2]:
    return poly if _area2(poly) >= 0 else list(reversed(poly))


def _split(poly: List[P2], a: P2, b: P2) -> Tuple[List[P2], List[P2]]:
    """Split a convex polygon by the line a->b: (left/inside part, right/outside part)."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    ln = math.hypot(dx, dy) or 1.0
    side = [(dx * (p[1] - a[1]) - dy * (p[0] - a[0])) / ln for p in poly]
    inside, outside = [], []
    for i, p in enumerate(poly):
        q, sp, sq = poly[(i + 1) % len(poly)], side[i], side[(i + 1) % len(poly)]
        if sp >= -0.01:
            inside.append(p)
        if sp <= 0.01:
            outside.append(p)
        if (sp > 0.01 and sq < -0.01) or (sp < -0.01 and sq > 0.01):
            t = sp / (sp - sq)
            x = (p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t)
            inside.append(x)
            outside.append(x)
    return inside, outside


def subtract(poly: List[P2], cutter: List[P2]) -> List[List[P2]]:
    """Convex polygon minus a convex polygon: convex pieces (both counter-clockwise)."""
    pieces: List[List[P2]] = []
    rest = poly
    for i in range(len(cutter)):
        a, b = cutter[i], cutter[(i + 1) % len(cutter)]
        inside, outside = _split(rest, a, b)
        if len(outside) >= 3 and abs(_area2(outside)) > 1e-6:
            pieces.append(outside)
        if len(inside) < 3 or abs(_area2(inside)) < 1e-6:
            return pieces + ([] if len(inside) < 3 else [])
        rest = inside
    return pieces  # `rest` is inside the cutter: covered


def _boxes_overlap(a: List[P2], b: List[P2]) -> bool:
    return (min(p[0] for p in a) < max(p[0] for p in b) - 0.01 and min(p[0] for p in b) < max(p[0] for p in a) - 0.01
            and min(p[1] for p in a) < max(p[1] for p in b) - 0.01 and min(p[1] for p in b) < max(p[1] for p in a) - 0.01)


def _priority(b: scene.Brush, f: scene.Face) -> Tuple[int, float]:
    return (2 if b.source == "displacement" else 1 if b.source in ("world", "patch") else 0, g.polygon_area(f.polygon))


def visible_faces(sc: scene.Scene) -> Tuple[Dict[int, List[scene.Face]], Dict[str, int]]:
    """id(brush) -> the faces to export, for brushes whose faces changed; and counts for the report."""
    entries = []  # (brush index, face index, key, d)
    planes: Dict[Tuple[int, int, int, int], List[Tuple[int, int]]] = defaultdict(list)
    brushes = sc.brushes
    for bi, b in enumerate(brushes):
        if b.kind not in EXPORTED:
            continue
        for fi, f in enumerate(b.faces):
            if len(f.polygon) < 3 or f.texture == SKY:
                continue
            d = g.dot(f.normal, f.polygon[0])
            planes[_key(f.normal, d)].append((bi, fi))
            entries.append((bi, fi))
    out: Dict[int, List[scene.Face]] = {}
    stats = {"faces_hidden": 0, "faces_trimmed": 0, "faces_doubled": 0}
    new_faces: Dict[int, Dict[int, List[scene.Face]]] = defaultdict(dict)
    for bi, fi in entries:
        b = brushes[bi]
        f = b.faces[fi]
        n = f.normal
        d = g.dot(n, f.polygon[0])
        u, v = _basis(n)
        mine = _ccw(_to2(f.polygon, u, v))
        cutters: List[List[P2]] = []
        doubled = False
        # Opaque geometry pressed against this face.
        for bj, fj in planes.get(_key(g.mul(n, -1.0), -d), ()):
            if bj == bi or brushes[bj].kind not in OPAQUE or b.kind == scene.GLASS and brushes[bj].kind == scene.GLASS:
                continue
            other = _ccw(_to2(brushes[bj].faces[fj].polygon, u, v))
            if _boxes_overlap(mine, other):
                cutters.append(other)
        # A higher-priority face drawn on the same spot (z-fighting).
        pr = _priority(b, f)
        for bj, fj in planes.get(_key(n, d), ()):
            if bj == bi:
                continue
            of = brushes[bj].faces[fj]
            opr = _priority(brushes[bj], of)
            if (opr, -bj) <= (pr, -bi):
                continue
            other = _ccw(_to2(of.polygon, u, v))
            if _boxes_overlap(mine, other):
                cutters.append(other)
                doubled = True
        if not cutters:
            continue
        pieces = [mine]
        for c in cutters:
            nxt: List[List[P2]] = []
            for p in pieces:
                nxt += subtract(p, c) if _boxes_overlap(p, c) else [p]
            pieces = [p for p in nxt if abs(_area2(p)) >= MIN_AREA]
            if len(pieces) > MAX_PIECES:
                break
        if len(pieces) > MAX_PIECES:
            continue
        total = abs(_area2(mine)) or 1.0
        kept = sum(abs(_area2(p)) for p in pieces)
        if kept >= total * KEEP_RATIO:
            continue
        faces = []
        for p in pieces:
            poly3 = [g.add(g.add(g.mul(n, d), g.mul(u, x)), g.mul(v, y)) for x, y in p]
            if g.dot(g.cross(g.sub(poly3[1], poly3[0]), g.sub(poly3[2], poly3[0])), n) < 0:
                poly3.reverse()
            faces.append(replace(f, polygon=g.dedupe(poly3)))
        new_faces[bi][fi] = [x for x in faces if len(x.polygon) >= 3]
        stats["faces_doubled" if doubled else "faces_trimmed"] += 1
        if not new_faces[bi][fi]:
            stats["faces_hidden"] += 1
    for bi, changes in new_faces.items():
        b = brushes[bi]
        faces = []
        for fi, f in enumerate(b.faces):
            faces += changes.get(fi, [f])
        # Keep every corner on an exported face (outline and vertex-built collision unchanged).
        corners = {_round(p) for f in b.faces for p in f.polygon}
        kept_pts = {_round(p) for f in faces for p in f.polygon}
        for fi, f in enumerate(b.faces):
            missing = {_round(p) for p in f.polygon} - kept_pts
            if fi in changes and missing & corners:
                faces = [x for x in faces if x not in changes[fi]] + [f]
                kept_pts |= {_round(p) for p in f.polygon}
        if len(faces) >= 1:
            out[id(b)] = faces
    return out, stats


def _round(p: Vec) -> Tuple[int, int, int]:
    return (round(p[0] * 4), round(p[1] * 4), round(p[2] * 4))
