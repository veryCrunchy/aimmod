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


GROUND_TEXTURE = "backdrop/ground_sand"


def add_ground_plane(sc: scene.Scene, margin: float = 16384.0, drop: float = 32.0, thickness: float = 16.0) -> None:
    """A wide floor under the whole map: holes in the port show ground instead of the void."""
    from .geometry import brush_faces  # local import keeps module load light
    pts = [p for b in sc.brushes if b.kind != scene.CLIP for f in b.faces for p in f.polygon]
    if not pts:
        return
    lo = [min(p[k] for p in pts) for k in range(3)]
    hi = [max(p[k] for p in pts) for k in range(3)]
    top = lo[2] - drop
    box = ((lo[0] - margin, lo[1] - margin, top - thickness), (hi[0] + margin, hi[1] + margin, top))
    planes = []
    for k in range(3):
        n = [0.0, 0.0, 0.0]
        n[k] = 1.0
        planes.append((n[0], n[1], n[2], box[1][k]))
        planes.append((-n[0], -n[1], -n[2], -box[0][k]))
    faces = [scene.Face(polygon=poly, normal=pl[:3], texture=GROUND_TEXTURE, reflectivity=(0.45, 0.38, 0.28))
             for pl, poly in zip(planes, brush_faces(planes)) if poly]
    sc.brushes.append(scene.Brush(faces=faces, kind=scene.SOLID, source="backdrop"))
    sc.bump("added_ground_plane")


def add_kill_below(sc: scene.Scene, z: float, margin: float = 1024.0) -> None:
    """A kill volume under the whole map up to height z (Source units): falling off kills."""
    pts = [p for b in sc.brushes if b.source != "backdrop" for f in b.faces for p in f.polygon]
    if not pts:
        return
    lo = [min(p[k] for p in pts) - margin for k in range(2)] + [min(p[2] for p in pts) - 256.0]
    hi = [max(p[k] for p in pts) + margin for k in range(2)] + [z]
    corners = [(x, y, zz) for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for zz in (lo[2], hi[2])]
    scene.add_liquid(sc, "hurt", corners, damage=1000.0)
    sc.bump("kill_volumes")


def remove_buried_liquids(sc: scene.Scene, cover: float = 0.8) -> int:
    """Drops liquid volumes (water, slime, lava) sealed under solid ground: liquid brushes a mapper left
    under a floor. In Source the floor hides them; KovaaK's draws its water through the floor. A volume
    is buried when solid geometry covers `cover` of its top, laid at or just above the water line.
    Pools and puddles open to the air stay; hurt triggers and kill volumes are never touched."""
    keep, removed = [], 0
    for go in sc.gameobjects:
        if go.get("liquid") not in ("water", "slime", "lava"):
            keep.append(go)
            continue
        o, s = go["origin"], go["size"]
        lo, hi, top = (o[0] - s[0] / 2, o[1] - s[1] / 2), (o[0] + s[0] / 2, o[1] + s[1] / 2), o[2] + s[2] / 2
        area = max(1.0, s[0] * s[1])
        covered = 0.0
        for b in sc.brushes:
            if b.kind != scene.SOLID:
                continue
            (bx0, by0, bz0), (bx1, by1, bz1) = b.bounds()
            # A lid: solid starting at the water line (or just under it) and above. A floor around or
            # under a pool also has its top near the water line, but it isn't over the water.
            if not -8.0 <= bz0 - top <= 96.0:
                continue
            ix = min(hi[0], bx1) - max(lo[0], bx0)
            iy = min(hi[1], by1) - max(lo[1], by0)
            if ix > 0 and iy > 0:
                covered += ix * iy / area
        if covered >= cover:
            removed += 1
            sc.bump(f"buried_{go['liquid']}_removed")
        else:
            keep.append(go)
    if removed:
        sc.gameobjects[:] = keep
        sc.notes.append(f"dropped {removed} liquid volume(s) sealed under the floor")
    return removed
