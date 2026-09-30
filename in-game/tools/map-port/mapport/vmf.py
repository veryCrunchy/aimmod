"""Hammer .vmf reader (brushes, displacements, spawns)."""
from __future__ import annotations

import re
from typing import Dict, List, Optional, Tuple

from . import bsp, classify, displacement, geometry as g, scene

_TOKEN = re.compile(r'"((?:[^"\\]|\\.)*)"|([{}])|([^\s{}"]+)')


class Node:
    __slots__ = ("name", "kv", "children")

    def __init__(self, name: str):
        self.name = name
        self.kv: Dict[str, str] = {}
        self.children: List["Node"] = []

    def get(self, key: str, default: str = "") -> str:
        return self.kv.get(key.lower(), default)

    def all(self, name: str) -> List["Node"]:
        return [c for c in self.children if c.name == name]


def parse(text: str) -> Node:
    root = Node("root")
    stack = [root]
    pending: Optional[str] = None
    for m in _TOKEN.finditer(text):
        quoted, brace, bare = m.groups()
        if brace == "{":
            node = Node((pending or "").lower())
            stack[-1].children.append(node)
            stack.append(node)
            pending = None
        elif brace == "}":
            if len(stack) > 1:
                stack.pop()
            pending = None
        else:
            tok = quoted if quoted is not None else bare
            if pending is None:
                pending = tok
            else:
                stack[-1].kv.setdefault(pending.lower(), tok)
                pending = None
    return root


def _points(s: str) -> List[Tuple[float, float, float]]:
    return [tuple(float(x) for x in grp.split()) for grp in re.findall(r"\(([^)]*)\)", s)]  # type: ignore


def _axis(s: str):
    m = re.match(r"\[\s*([-\d.eE+]+)\s+([-\d.eE+]+)\s+([-\d.eE+]+)\s+([-\d.eE+]+)\s*\]\s*([-\d.eE+]+)", s or "")
    if not m:
        return None
    x, y, z, off, scale = (float(v) for v in m.groups())
    scale = scale or 0.25
    return (x / scale, y / scale, z / scale, off)


def _solid(solid: Node, cls: str, sc: scene.Scene, disp_step: int, disp_thickness: float) -> None:
    sides = solid.all("side")
    planes, texes, disps = [], [], []
    for s in sides:
        pts = _points(s.get("plane"))
        if len(pts) != 3:
            continue
        pl = g.plane_from_points(pts[0], pts[2], pts[1])  # Hammer winds plane points clockwise
        if pl is None:
            continue
        planes.append(pl)
        tex = s.get("material").lower().replace("\\", "/")
        ua, va = _axis(s.get("uaxis")), _axis(s.get("vaxis"))
        texes.append((tex, (ua, va) if ua and va else None))
        d = s.all("dispinfo")
        disps.append(d[0] if d else None)
    if len(planes) < 4:
        sc.bump("dropped_empty")
        return
    polys = g.brush_faces(planes)
    if sum(p is not None for p in polys) < 4:
        # Unknown winding convention in this file: try the other orientation.
        planes = [(-a, -b, -c, -d) for a, b, c, d in planes]
        polys = g.brush_faces(planes)
    if any(disps):
        for pl, poly, (tex, axes), d in zip(planes, polys, texes, disps):
            if d is None or poly is None or len(poly) != 4:
                continue
            _displacement(d, poly, pl[:3], tex, axes, sc, disp_step, disp_thickness)
        return
    kind = classify.classify([t for t, _ in texes], cls)
    if kind is None:
        sc.bump("dropped_" + (cls if cls != "worldspawn" else "tool"))
        return
    faces = [scene.Face(polygon=poly, normal=pl[:3], texture=tex, uv_axes=axes)
             for pl, poly, (tex, axes) in zip(planes, polys, texes) if poly is not None]
    if len(faces) < 4:
        sc.bump("dropped_degenerate")
        return
    sc.brushes.append(scene.Brush(faces=faces, kind=kind, source=cls))
    sc.bump(f"kept_{kind}")


def _rows(node: Optional[Node], n: int, width: int) -> List[List[float]]:
    rows = []
    for r in range(n):
        vals = [float(x) for x in (node.get(f"row{r}") if node else "").split()]
        vals += [0.0] * (n * width - len(vals))
        rows.append(vals)
    return rows


def _displacement(d: Node, poly, normal, tex, axes, sc, step, thickness) -> None:
    power = int(float(d.get("power", "3")))
    n = (1 << power) + 1
    start = _points(d.get("startposition"))
    start = start[0] if start else poly[0]
    elev = float(d.get("elevation", "0") or 0)
    normals = _rows(next(iter(d.all("normals")), None), n, 3)
    dists = _rows(next(iter(d.all("distances")), None), n, 1)
    offsets = _rows(next(iter(d.all("offsets")), None), n, 3)
    verts = []
    for i in range(n):
        for j in range(n):
            nx, ny, nz = normals[i][j * 3:j * 3 + 3]
            dist = dists[i][j]
            ox, oy, oz = offsets[i][j * 3:j * 3 + 3]
            # Fold offset and elevation into a single displacement vector.
            vec = (nx * dist + ox + normal[0] * elev, ny * dist + oy + normal[1] * elev, nz * dist + oz + normal[2] * elev)
            ln = g.length(vec)
            verts.append((vec[0] / ln, vec[1] / ln, vec[2] / ln, ln, 0.0) if ln > 1e-9 else (0.0, 0.0, 0.0, 0.0, 0.0))
    # Compiled faces are wound clockwise; our clipped polygon is counter-clockwise.
    grid = displacement.grid_points(list(reversed(poly)), start, verts, power)
    for b in displacement.slabs(grid, n, normal, tex, (0.5, 0.5, 0.5), thickness, step, axes):
        sc.brushes.append(b)
        sc.bump("kept_displacement_slab")
    sc.bump("displacements")


def load(text: str, name: str, disp_step: int = 1, disp_thickness: float = 8.0) -> scene.Scene:
    root = parse(text)
    sc = scene.Scene(name=name)
    sc.notes.append("VMF source: texture reflectivity unknown, tints use category defaults")
    for world in root.all("world"):
        for solid in world.all("solid"):
            _solid(solid, "worldspawn", sc, disp_step, disp_thickness)
    for ent in root.all("entity"):
        kv = dict(ent.kv)
        sc.entities.append(kv)
        cls = kv.get("classname", "")
        if ent.get("hidden") == "1":
            continue
        for solid in ent.all("solid"):
            _solid(solid, cls, sc, disp_step, disp_thickness)
    bsp._spawns(sc)
    return sc
