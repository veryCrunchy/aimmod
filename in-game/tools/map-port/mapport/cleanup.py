"""Scene cleanup: drop the 3D skybox room and other detached areas (test rooms, props rooms)."""
from __future__ import annotations

from collections import defaultdict
from typing import Dict, List, Optional, Tuple

from . import scene

CELL = 256.0
TOUCH = 2.0  # brushes within this many units of each other are connected


def _vec(s: str) -> Optional[Tuple[float, float, float]]:
    try:
        x, y, z = (float(v) for v in s.split()[:3])
        return (x, y, z)
    except ValueError:
        return None


def components(sc: scene.Scene):
    """Union brushes whose (slightly grown) bounding boxes touch. Returns (boxes, root per brush, comp boxes)."""
    boxes = [b.bounds() for b in sc.brushes]
    parent = list(range(len(boxes)))

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    grid: Dict[Tuple[int, int, int], List[int]] = defaultdict(list)
    for i, (lo, hi) in enumerate(boxes):
        r = [range(int((lo[k] - TOUCH) // CELL), int((hi[k] + TOUCH) // CELL) + 1) for k in range(3)]
        for x in r[0]:
            for y in r[1]:
                for z in r[2]:
                    grid[(x, y, z)].append(i)
    for members in grid.values():
        for a_i in range(len(members)):
            a = members[a_i]
            la, ha = boxes[a]
            for b in members[a_i + 1:]:
                lb, hb = boxes[b]
                if all(la[k] - TOUCH <= hb[k] and lb[k] - TOUCH <= ha[k] for k in range(3)):
                    ra, rb = find(a), find(b)
                    if ra != rb:
                        parent[ra] = rb
    roots = [find(i) for i in range(len(boxes))]
    comp_box: Dict[int, list] = {}
    for i, (lo, hi) in enumerate(boxes):
        cb = comp_box.setdefault(roots[i], [list(lo), list(hi)])
        for k in range(3):
            cb[0][k] = min(cb[0][k], lo[k])
            cb[1][k] = max(cb[1][k], hi[k])
    return boxes, roots, comp_box


def _inside(p, box, margin: float = 0.0) -> bool:
    return all(box[0][k] - margin <= p[k] <= box[1][k] + margin for k in range(3))


def _overlaps(a, b, margin: float) -> bool:
    return all(a[0][k] - margin <= b[1][k] and b[0][k] - margin <= a[1][k] for k in range(3))


def remove_detached(sc: scene.Scene, margin: float = 512.0) -> int:
    """Keep the spawn areas plus everything overlapping their bounds; drop the rest.

    Removes the 3D skybox room (it sits far outside the playable map) and hidden rooms such as the
    cubemap/showcase rooms some maps carry. Falls back to the sky_camera heuristic if there are no spawns.
    """
    if not sc.brushes:
        return 0
    if not sc.spawns:
        return remove_3d_skybox(sc)
    boxes, roots, comp_box = components(sc)
    main = {r for r, box in comp_box.items() for s in sc.spawns if _inside(s.origin, box, 64.0)}
    if not main:
        return remove_3d_skybox(sc)
    lo = [min(comp_box[r][0][k] for r in main) for k in range(3)]
    hi = [max(comp_box[r][1][k] for r in main) for k in range(3)]
    area = [lo, hi]
    keep = {r for r, box in comp_box.items() if r in main or _overlaps(box, area, margin)}
    before = len(sc.brushes)
    sc.brushes = [b for i, b in enumerate(sc.brushes) if roots[i] in keep]
    dropped = before - len(sc.brushes)
    sc.bump("dropped_detached_area", dropped)
    return dropped


def remove_3d_skybox(sc: scene.Scene) -> int:
    """Remove the brush component that encloses sky_camera and contains no spawn."""
    cam = None
    for e in sc.entities:
        if e.get("classname") == "sky_camera" and "origin" in e:
            cam = _vec(e["origin"])
    if cam is None or not sc.brushes:
        return 0
    boxes, roots, comp_box = components(sc)
    spawn_comps = {r for r, box in comp_box.items() for s in sc.spawns if _inside(s.origin, box)}
    sky = [r for r, box in comp_box.items() if _inside(cam, box) and r not in spawn_comps]
    if not sky:
        return 0
    target = min(sky, key=lambda r: sum(comp_box[r][1][k] - comp_box[r][0][k] for k in range(3)))
    drop = {i for i in range(len(boxes)) if roots[i] == target}
    sc.brushes = [b for i, b in enumerate(sc.brushes) if i not in drop]
    sc.bump("dropped_3d_skybox", len(drop))
    return len(drop)
