"""Small vector and convex-polyhedron helpers (pure Python, no dependencies)."""
from __future__ import annotations

import math
from typing import Iterable, List, Optional, Sequence, Tuple

Vec = Tuple[float, float, float]
Plane = Tuple[float, float, float, float]  # normal x, y, z and distance: n . p = d

EPS = 0.01
HUGE = 65536.0


def add(a: Vec, b: Vec) -> Vec:
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def sub(a: Vec, b: Vec) -> Vec:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def mul(a: Vec, s: float) -> Vec:
    return (a[0] * s, a[1] * s, a[2] * s)


def dot(a: Sequence[float], b: Sequence[float]) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def cross(a: Vec, b: Vec) -> Vec:
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def length(a: Vec) -> float:
    return math.sqrt(dot(a, a))


def normalize(a: Vec) -> Vec:
    n = length(a)
    return (0.0, 0.0, 0.0) if n < 1e-12 else (a[0] / n, a[1] / n, a[2] / n)


def lerp(a: Vec, b: Vec, t: float) -> Vec:
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t)


def centroid(points: Iterable[Vec]) -> Vec:
    pts = list(points)
    n = float(len(pts))
    return (sum(p[0] for p in pts) / n, sum(p[1] for p in pts) / n, sum(p[2] for p in pts) / n)


def polygon_area(poly: Sequence[Vec]) -> float:
    if len(poly) < 3:
        return 0.0
    acc = (0.0, 0.0, 0.0)
    for i in range(1, len(poly) - 1):
        acc = add(acc, cross(sub(poly[i], poly[0]), sub(poly[i + 1], poly[0])))
    return 0.5 * length(acc)


def plane_from_points(a: Vec, b: Vec, c: Vec) -> Optional[Plane]:
    n = normalize(cross(sub(b, a), sub(c, a)))
    if n == (0.0, 0.0, 0.0):
        return None
    return (n[0], n[1], n[2], dot(n, a))


def base_winding(plane: Plane, size: float = HUGE) -> List[Vec]:
    """A huge square lying on the plane, wound counter-clockwise around the plane normal."""
    n = plane[:3]
    ax = max(range(3), key=lambda i: abs(n[i]))
    up = (0.0, 0.0, 1.0) if ax != 2 else (1.0, 0.0, 0.0)
    right = normalize(cross(up, n))
    up = cross(n, right)
    org = mul(n, plane[3])
    r, u = mul(right, size), mul(up, size)
    return [sub(sub(org, r), u), sub(add(org, r), u), add(add(org, r), u), add(sub(org, r), u)]


def clip_polygon(poly: List[Vec], plane: Plane, eps: float = EPS) -> List[Vec]:
    """Keep the part of the polygon behind the plane (n . p <= d)."""
    if not poly:
        return poly
    n, d = plane[:3], plane[3]
    dists = [dot(p, n) - d for p in poly]
    if all(x <= eps for x in dists):
        return poly
    if all(x > -eps for x in dists):
        return []
    out: List[Vec] = []
    for i, p in enumerate(poly):
        q = poly[(i + 1) % len(poly)]
        dp, dq = dists[i], dists[(i + 1) % len(poly)]
        if dp <= eps:
            out.append(p)
        if (dp < -eps and dq > eps) or (dp > eps and dq < -eps):
            t = dp / (dp - dq)
            out.append(lerp(p, q, t))
    return dedupe(out)


def dedupe(poly: List[Vec], eps: float = 1e-3) -> List[Vec]:
    out: List[Vec] = []
    for p in poly:
        if not out or max(abs(p[k] - out[-1][k]) for k in range(3)) > eps:
            out.append(p)
    while len(out) > 1 and max(abs(out[0][k] - out[-1][k]) for k in range(3)) <= eps:
        out.pop()
    return out


def brush_faces(planes: Sequence[Plane]) -> List[Optional[List[Vec]]]:
    """For each bounding plane of a convex brush return its face polygon (outward CCW) or None."""
    faces: List[Optional[List[Vec]]] = []
    for i, pl in enumerate(planes):
        w = base_winding(pl)
        for j, other in enumerate(planes):
            if i == j:
                continue
            # Skip exact duplicates of this plane; an opposite plane still clips.
            if dot(pl[:3], other[:3]) > 0.9999 and abs(pl[3] - other[3]) < 1e-4:
                if j < i:
                    w = []
                    break
                continue
            w = clip_polygon(w, other)
            if not w:
                break
        faces.append(w if len(w) >= 3 and polygon_area(w) > 1e-3 else None)
    return faces


def triangulate_fan(n: int) -> List[Tuple[int, int, int]]:
    return [(0, i, i + 1) for i in range(1, n - 1)]


def rotate_zyx(p: Vec, pitch: float, yaw: float, roll: float) -> Vec:
    """Rotate a point by Source/Hammer entity angles (degrees)."""
    if not (pitch or yaw or roll):
        return p
    cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    cr, sr = math.cos(math.radians(roll)), math.sin(math.radians(roll))
    x, y, z = p
    # roll about X
    y, z = y * cr - z * sr, y * sr + z * cr
    # pitch about Y
    x, z = x * cp + z * sp, -x * sp + z * cp
    # yaw about Z
    x, y = x * cy - y * sy, x * sy + y * cy
    return (x, y, z)
