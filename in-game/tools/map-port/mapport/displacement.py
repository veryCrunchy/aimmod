"""Turn Source displacement surfaces into convex slabs that KovaaK's can collide with."""
from __future__ import annotations

from typing import List, Optional, Sequence, Tuple

from . import geometry as g
from . import scene
from .geometry import Vec

PLANAR_TOLERANCE = 0.35  # Source units a merged patch may deviate from its plane.


def grid_points(corners: Sequence[Vec], start: Vec, verts: Sequence[tuple], power: int) -> List[Vec]:
    """Displaced vertex positions, row-major, following Valve's CCoreDispInfo layout."""
    best = min(range(4), key=lambda i: sum((corners[i][k] - start[k]) ** 2 for k in range(3)))
    pts = [corners[(best + i) % 4] for i in range(4)]
    n = (1 << power) + 1
    out: List[Vec] = []
    for i in range(n):
        t = i / (n - 1)
        a = g.lerp(pts[0], pts[1], t)
        b = g.lerp(pts[3], pts[2], t)
        for j in range(n):
            base = g.lerp(a, b, j / (n - 1))
            vx, vy, vz, dist, _alpha = verts[i * n + j]
            out.append((base[0] + vx * dist, base[1] + vy * dist, base[2] + vz * dist))
    return out


def _plane_of(points: Sequence[Vec], hint: Vec) -> Optional[Tuple[Vec, float]]:
    """Best-effort plane through a polygon (Newell's method), normal oriented along hint."""
    nx = ny = nz = 0.0
    for i, p in enumerate(points):
        q = points[(i + 1) % len(points)]
        nx += (p[1] - q[1]) * (p[2] + q[2])
        ny += (p[2] - q[2]) * (p[0] + q[0])
        nz += (p[0] - q[0]) * (p[1] + q[1])
    n = g.normalize((nx, ny, nz))
    if n == (0.0, 0.0, 0.0):
        return None
    if g.dot(n, hint) < 0:
        n = g.mul(n, -1)
    c = g.centroid(points)
    return n, g.dot(n, c)


def _is_planar(points: Sequence[Vec], plane: Tuple[Vec, float]) -> bool:
    n, d = plane
    return all(abs(g.dot(n, p) - d) <= PLANAR_TOLERANCE for p in points)


def _is_convex(poly: Sequence[Vec], normal: Vec) -> bool:
    sign = 0
    for i in range(len(poly)):
        a, b, c = poly[i], poly[(i + 1) % len(poly)], poly[(i + 2) % len(poly)]
        s = g.dot(g.cross(g.sub(b, a), g.sub(c, b)), normal)
        if abs(s) < 1e-6:
            continue
        if sign == 0:
            sign = 1 if s > 0 else -1
        elif (s > 0) != (sign > 0):
            return False
    return True


def _drop_collinear(poly: List[Vec]) -> List[Vec]:
    out = list(poly)
    changed = True
    while changed and len(out) > 3:
        changed = False
        for i in range(len(out)):
            a, b, c = out[i - 1], out[i], out[(i + 1) % len(out)]
            if g.length(g.cross(g.sub(b, a), g.sub(c, b))) < 1e-3 * max(1.0, g.length(g.sub(c, a))):
                out.pop(i)
                changed = True
                break
    return out


def prism(top: List[Vec], up: Vec, thickness: float, texture: str, refl, uv_axes=None,
          tex_size=(512, 512)) -> Optional[scene.Brush]:
    """Convex prism whose top face is `top` (outward normal ~ up), extruded against `up`."""
    pl = _plane_of(top, up)
    if pl is None or g.polygon_area(top) < 0.01:
        return None
    n_top = pl[0]
    ext = up if g.dot(n_top, up) > 0.2 else n_top
    ext = g.normalize(ext)
    # Top winding must be counter-clockwise around its outward normal.
    if g.dot(g.cross(g.sub(top[1], top[0]), g.sub(top[2], top[0])), n_top) < 0:
        top = list(reversed(top))
    bottom = [g.sub(p, g.mul(ext, thickness)) for p in top]
    faces = [scene.Face(polygon=list(top), normal=n_top, texture=texture, reflectivity=refl,
                        uv_axes=uv_axes, tex_size=tex_size)]
    faces.append(scene.Face(polygon=list(reversed(bottom)), normal=g.mul(n_top, -1), texture=texture,
                            reflectivity=refl, uv_axes=uv_axes, tex_size=tex_size))
    k = len(top)
    for i in range(k):
        a, b = top[i], top[(i + 1) % k]
        quad = [b, a, bottom[i], bottom[(i + 1) % k]]
        nrm = g.normalize(g.cross(g.sub(quad[1], quad[0]), g.sub(quad[2], quad[0])))
        faces.append(scene.Face(polygon=quad, normal=nrm, texture=texture, reflectivity=refl,
                                uv_axes=uv_axes, tex_size=tex_size))
    return scene.Brush(faces=faces, kind=scene.SOLID, source="displacement")


def slabs(grid: Sequence[Vec], n: int, up: Vec, texture: str, refl, thickness: float = 8.0,
          step: int = 1, uv_axes=None, tex_size=(512, 512)) -> List[scene.Brush]:
    """Greedy-merge planar cells of the displacement grid and emit one prism per patch."""
    step = max(1, min(step, n - 1))
    idx = list(range(0, n, step))
    if idx[-1] != n - 1:
        idx.append(n - 1)
    m = len(idx) - 1  # cells per side

    def p(ci: int, cj: int) -> Vec:
        return grid[idx[ci] * n + idx[cj]]

    def boundary(i0: int, j0: int, i1: int, j1: int) -> List[Vec]:
        pts = [p(i0, j) for j in range(j0, j1 + 1)]
        pts += [p(i, j1) for i in range(i0 + 1, i1 + 1)]
        pts += [p(i1, j) for j in range(j1 - 1, j0 - 1, -1)]
        pts += [p(i, j0) for i in range(i1 - 1, i0, -1)]
        return pts

    def patch_ok(i0: int, j0: int, i1: int, j1: int) -> Optional[List[Vec]]:
        inner = [p(i, j) for i in range(i0, i1 + 1) for j in range(j0, j1 + 1)]
        poly = boundary(i0, j0, i1, j1)
        pl = _plane_of(poly, up)
        if pl is None or not _is_planar(inner, pl):
            return None
        poly = _drop_collinear(g.dedupe(poly))
        if len(poly) < 3 or not _is_convex(poly, pl[0]):
            return None
        return poly

    used = [[False] * m for _ in range(m)]
    out: List[scene.Brush] = []
    for i in range(m):
        for j in range(m):
            if used[i][j]:
                continue
            poly = patch_ok(i, j, i + 1, j + 1)
            if poly is None:
                # Non-planar cell: split into two triangles.
                for tri in ((p(i, j), p(i, j + 1), p(i + 1, j + 1)), (p(i, j), p(i + 1, j + 1), p(i + 1, j))):
                    b = prism(list(tri), up, thickness, texture, refl, uv_axes, tex_size)
                    if b:
                        out.append(b)
                used[i][j] = True
                continue
            j1 = j + 1
            while j1 < m and not used[i][j1]:
                cand = patch_ok(i, j, i + 1, j1 + 1)
                if cand is None:
                    break
                poly, j1 = cand, j1 + 1
            i1 = i + 1
            while i1 < m and not any(used[i1][jj] for jj in range(j, j1)):
                cand = patch_ok(i, j, i1 + 1, j1)
                if cand is None:
                    break
                poly, i1 = cand, i1 + 1
            for ii in range(i, i1):
                for jj in range(j, j1):
                    used[ii][jj] = True
            b = prism(poly, up, thickness, texture, refl, uv_axes, tex_size)
            if b:
                out.append(b)
    return out
