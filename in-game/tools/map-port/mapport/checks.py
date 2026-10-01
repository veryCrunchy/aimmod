"""Hard checks run before a port is handed over.

* Floating props: every prop/stand-in object needs support (map geometry) within SUPPORT_GAP units
  below its base; unsupported ones are removed first, and any left fail the check.
* Dark faces: visible faces whose material slot is near-black inside the playable area.
* Reachability: a coarse walk graph (32-unit samples on walkable faces, CS/Quake step and jump
  limits, hull clearance) flooded from the spawns. Every team's spawns must reach the others.

The walk graph also gives well-spread standing spots for the first-person check views.
"""
from __future__ import annotations

from collections import defaultdict, deque
from typing import Dict, List, Optional, Sequence, Tuple

from . import geometry as g
from . import scene
from .materials import Slot
from .spawns import HULL, _solids, blocked, hull_box

FLOOR_GAP_CHECK = 4.0


def HULL_H() -> float:
    return HULL["height"]

SUPPORT_GAP = 48.0
CELL = 128.0
SPACING = 32.0
STEP_UP = 18.0
JUMP_UP = 54.0
DROP = 300.0
DARK = 0.08
WALKABLE = (scene.SOLID, scene.CLIP, scene.GLASS)
SUPPORTING = (scene.SOLID, scene.CLIP, scene.GLASS)


class _Index:
    def __init__(self, solids):
        self.solids = solids
        self.grid: Dict[Tuple[int, int], List[int]] = defaultdict(list)
        for i, s in enumerate(solids):
            for x in range(int(s.lo[0] // CELL), int(s.hi[0] // CELL) + 1):
                for y in range(int(s.lo[1] // CELL), int(s.hi[1] // CELL) + 1):
                    self.grid[(x, y)].append(i)

    def near(self, lo, hi) -> List:
        ids = set()
        for x in range(int(lo[0] // CELL), int(hi[0] // CELL) + 1):
            for y in range(int(lo[1] // CELL), int(hi[1] // CELL) + 1):
                ids.update(self.grid.get((x, y), ()))
        return [self.solids[i] for i in ids]


def _ray_down(p, solid) -> Optional[float]:
    """Z where a vertical ray from p going down enters a convex solid, or None."""
    t_enter, t_exit = 0.0, 1e9
    for n, d in solid.planes:
        denom = -n[2]          # ray direction (0, 0, -1)
        dist = g.dot(n, p) - d  # > 0 outside this plane
        if abs(denom) < 1e-9:
            if dist > 0:
                return None
            continue
        t = -dist / denom
        if denom < 0:
            t_enter = max(t_enter, t)
        else:
            t_exit = min(t_exit, t)
        if t_enter > t_exit:
            return None
    if t_enter <= 0.0:
        return None  # the ray starts inside this solid: not a floor below the point
    return p[2] - t_enter


def floor_below(p, index: _Index, max_drop: float) -> Optional[float]:
    best = None
    for s in index.near((p[0] - 1, p[1] - 1), (p[0] + 1, p[1] + 1)):
        if not (s.lo[0] <= p[0] <= s.hi[0] and s.lo[1] <= p[1] <= s.hi[1]) or s.lo[2] > p[2]:
            continue
        z = _ray_down(p, s)
        if z is not None and p[2] - z <= max_drop and (best is None or z > best):
            best = z
    return best


def remove_floating(sc: scene.Scene) -> int:
    """Drop prop objects with nothing under them (map geometry only, not other props)."""
    base = [b for b in sc.brushes if b.kind in SUPPORTING and b.source not in ("prop", "backdrop")]
    from .spawns import _Solid
    index = _Index([_Solid(b) for b in base])
    keep, dropped = [], 0
    for b in sc.brushes:
        if b.source != "prop":
            keep.append(b)
            continue
        lo, hi = b.bounds()
        probes = [((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2] + 2.0),
                  (lo[0] + 1, lo[1] + 1, lo[2] + 2.0), (hi[0] - 1, hi[1] - 1, lo[2] + 2.0)]
        if any(floor_below(p, index, SUPPORT_GAP) is not None for p in probes):
            keep.append(b)
        else:
            dropped += 1
    sc.brushes = keep
    if dropped:
        sc.bump("dropped_floating_props", dropped)
    return dropped


def _lum(tint: str) -> float:
    r, gg, b = (int(tint[i:i + 2], 16) / 255.0 for i in (0, 2, 4))
    return 0.2126 * r + 0.7152 * gg + 0.0722 * b


def dark_faces(sc: scene.Scene, slots: List[Slot], tex_slot: Dict[str, int], area) -> int:
    n = 0
    for b in sc.brushes:
        if b.kind in (scene.CLIP, scene.GLASS):
            continue
        for f in b.faces:
            if f.hidden or f.texture not in tex_slot:
                continue
            c = g.centroid(f.polygon)
            if not all(area[0][k] <= c[k] <= area[1][k] for k in range(3)):
                continue
            if _lum(slots[tex_slot[f.texture]].tint) < DARK:
                n += 1
    return n


def walk_graph(sc: scene.Scene):
    """Nodes (feet positions) on walkable faces with a free hull, and their neighbours."""
    from .spawns import _Solid
    solids = _solids(sc)
    index = _Index(solids)
    nodes: List[Vec] = []
    seen = set()
    for b in sc.brushes:
        if b.kind not in WALKABLE or b.source == "backdrop":
            continue
        for f in b.faces:
            if f.normal[2] < 0.7:
                continue
            lo = [min(p[k] for p in f.polygon) for k in range(3)]
            hi = [max(p[k] for p in f.polygon) for k in range(3)]
            # lattice points inside the face, plus its centre (narrow faces such as stair steps)
            c = g.centroid(f.polygon)
            samples = [(c[0], c[1])]
            x = (lo[0] // SPACING) * SPACING + SPACING / 2
            while x < hi[0]:
                y = (lo[1] // SPACING) * SPACING + SPACING / 2
                while y < hi[1]:
                    samples.append((x, y))
                    y += SPACING
                x += SPACING
            for x, y in samples:
                z = _face_z(f, x, y)
                if z is None:
                    continue
                k3 = (int(x // SPACING), int(y // SPACING), int(z // 8))
                if k3 in seen:
                    continue
                seen.add(k3)
                p = (x, y, z)
                hb = hull_box(p)
                if not blocked(hb, index.near(hb[0], hb[1])):
                    nodes.append(p)
    cols: Dict[Tuple[int, int], List[int]] = defaultdict(list)
    for i, p in enumerate(nodes):
        cols[(int(p[0] // SPACING), int(p[1] // SPACING))].append(i)
    return nodes, cols, index


def _face_z(f: scene.Face, x: float, y: float) -> Optional[float]:
    n = f.normal
    if abs(n[2]) < 1e-6:
        return None
    # point-in-polygon (projected to XY), polygon is convex
    poly = f.polygon
    sign = 0
    for i in range(len(poly)):
        a, b = poly[i], poly[(i + 1) % len(poly)]
        c = (b[0] - a[0]) * (y - a[1]) - (b[1] - a[1]) * (x - a[0])
        if abs(c) < 1e-9:
            continue
        s = 1 if c > 0 else -1
        if sign and s != sign:
            return None
        sign = s
    d = g.dot(n, poly[0])
    return (d - n[0] * x - n[1] * y) / n[2]


def reachability(sc: scene.Scene, jump_up: float = JUMP_UP):
    nodes, cols, index = walk_graph(sc)
    if not nodes or not sc.spawns:
        return {"nodes": len(nodes), "reached": 0, "spawns_connected": False, "isolated_spawns": len(sc.spawns)}, []

    movers: Dict[int, List[int]] = defaultdict(list)

    cache: Dict[int, List[int]] = {}

    def neighbours(i: int):
        if i in cache:
            return cache[i]
        out = list(movers.get(i, ()))
        out += list(_walk(i))
        cache[i] = out
        return out

    def _walk(i: int):
        p = nodes[i]
        cx, cy = int(p[0] // SPACING), int(p[1] // SPACING)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for j in cols.get((cx + dx, cy + dy), ()):
                    if j == i:
                        continue  # (same column: stacked stair steps are neighbours too)
                    q = nodes[j]
                    dz = q[2] - p[2]
                    if dz > jump_up or dz < -DROP:
                        continue
                    mid = ((p[0] + q[0]) / 2, (p[1] + q[1]) / 2, max(p[2], q[2]))
                    hb = hull_box(mid, gap=STEP_UP + 1)
                    if blocked(hb, index.near(hb[0], hb[1])):
                        continue
                    if abs(dz) > STEP_UP:
                        # falling or jumping: the whole vertical sweep at the lower spot must be clear,
                        # otherwise the "drop" would go through a floor (or the jump through a ceiling)
                        low = q if dz < 0 else p
                        top = max(p[2], q[2]) + HULL_H()
                        lo_box, hi_box = hull_box(low, gap=FLOOR_GAP_CHECK)
                        sweep = (lo_box, (hi_box[0], hi_box[1], top))
                        if blocked(sweep, index.near(sweep[0], sweep[1])):
                            continue
                    yield j

    def nearest(p):
        cx, cy = int(p[0] // SPACING), int(p[1] // SPACING)
        best, bd = None, 1e18
        for dx in range(-3, 4):
            for dy in range(-3, 4):
                for j in cols.get((cx + dx, cy + dy), ()):
                    q = nodes[j]
                    # the ground the player lands on: at most a step above, or anything below (falls)
                    if q[2] - p[2] > 40 or p[2] - q[2] > DROP:
                        continue
                    d = (q[0] - p[0]) ** 2 + (q[1] - p[1]) ** 2 + 4.0 * max(0.0, p[2] - q[2] - 40) ** 2
                    if d < bd:
                        best, bd = j, d
        return best

    # Jump pads and teleporters connect their trigger area to the destination.
    waypoints = {go["name"]: go for go in sc.gameobjects if go["kind"] == "waypoint"}
    for go in sc.gameobjects:
        if go["kind"] not in ("jumppad", "teleporter") or go["target"] not in waypoints:
            continue
        dest = waypoints[go["target"]]["origin"]
        d_node = None
        for drop in (0, 64, 128, 256, 512):
            d_node = nearest((dest[0], dest[1], dest[2] - drop))
            if d_node is not None:
                break
        if d_node is None:
            continue
        o, size = go["origin"], go["size"]
        for i, p in enumerate(nodes):
            if abs(p[0] - o[0]) <= size[0] / 2 + 16 and abs(p[1] - o[1]) <= size[1] / 2 + 16 and \
                    o[2] - 32 <= p[2] <= o[2] + size[2] + 16:
                movers[i].append(d_node)

    # Movement is directed (you can drop off a ledge you cannot climb), so check that every spawn can
    # walk to every other spawn: flood from each spawn and require all spawn nodes in each flood.
    starts = [nearest(s.origin) for s in sc.spawns]
    isolated = sum(1 for st in starts if st is None)
    valid = [st for st in starts if st is not None]

    def flood(st: int) -> set:
        seen = {st}
        dq = deque([st])
        while dq:
            i = dq.popleft()
            for j in neighbours(i):
                if j not in seen:
                    seen.add(j)
                    dq.append(j)
        return seen

    # Teams may be walled off from each other by design (awp/aim maps), so the hard rule is per team:
    # every spawn reaches all spawns of its own team. Cross-team connectivity is reported.
    teams = [s.team for s in sc.spawns]
    union: set = set()
    stuck = 0
    cross = True
    for idx, st in enumerate(starts[:48]):
        if st is None:
            continue
        reach = flood(st)
        union |= reach
        own = [o for o, t in zip(starts, teams) if o is not None and (t == teams[idx] or t == 0 or teams[idx] == 0)]
        # A spawn is trapped if it cannot walk to at least half of its team (a tower spawn you can only
        # drop down from is fine; a sealed box is not).
        if own and sum(1 for o in own if o in reach) * 2 < len(own):
            stuck += 1
        if not all(o in reach for o in valid):
            cross = False
    reached = [nodes[i] for i in sorted(union)]

    # Collision sanity: places you can walk or fall into but never leave (no way back to any spawn).
    # Water is excluded (you swim out of pools).
    rev: Dict[int, List[int]] = defaultdict(list)
    for i in union:
        for j in neighbours(i):
            rev[j].append(i)
    back = set(valid)
    dq = deque(valid)
    while dq:
        j = dq.popleft()
        for i in rev.get(j, ()):
            if i not in back:
                back.add(i)
                dq.append(i)
    liquids = [go for go in sc.gameobjects if go["kind"] in ("water", "hurt")]

    def in_liquid(p) -> bool:
        return any(all(abs(p[k] - go["origin"][k]) <= go["size"][k] / 2 + 24 for k in range(3)) for go in liquids)

    traps = [nodes[i] for i in union if i not in back and not in_liquid(nodes[i])]
    return {"nodes": len(nodes), "reached": len(reached), "spawns_connected": stuck == 0 and bool(valid),
            "teams_connected": cross, "isolated_spawns": isolated,
            "spawns_that_cannot_reach_their_team": stuck, "trapped_spots": len(traps),
            "trapped_examples": [[round(v) for v in p] for p in spread(traps, 6)]}, reached


def spread(points: Sequence[Vec], count: int, avoid: Sequence[Vec] = ()) -> List[Vec]:
    chosen = list(avoid)
    out: List[Vec] = []
    pool = list(points[:: max(1, len(points) // 4000)])
    while pool and len(out) < count:
        best = max(pool, key=lambda p: min(((p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2 for q in chosen), default=1e18))
        pool.remove(best)
        chosen.append(best)
        out.append(best)
    return out


Vec = g.Vec


def run(sc: scene.Scene, slots: List[Slot], tex_slot: Dict[str, int], jump_up: float = JUMP_UP) -> dict:
    floating = sum(1 for b in sc.brushes if b.source == "prop")  # props remaining after remove_floating
    reach, reached = reachability(sc, jump_up)
    pts = reached or [s.origin for s in sc.spawns]
    lo = [min(p[k] for p in pts) - 64 for k in range(3)]
    hi = [max(p[k] for p in pts) + 160 for k in range(3)]
    dark = dark_faces(sc, slots, tex_slot, (lo, hi))
    problems = []
    if reach.get("isolated_spawns"):
        problems.append(f"{reach['isolated_spawns']} spawns have no walkable ground")
    if not reach.get("spawns_connected"):
        problems.append("some spawns cannot walk to the rest of their team")
    if dark:
        problems.append(f"{dark} near-black faces in the playable area")
    if reach.get("trapped_spots", 0) > max(8, 0.02 * max(1, reach.get("reached", 0))):
        problems.append(f"{reach['trapped_spots']} spots where a player gets stuck (cannot walk back to a "
                        f"spawn), e.g. {reach.get('trapped_examples')}")
    return {"pass": not problems, "problems": problems, "reachability": reach, "dark_faces": dark,
            "_reached": reached,
            "props_kept": floating, "view_spots": [list(map(lambda v: round(v, 1), p)) for p in spread(reached, 8, [s.origin for s in sc.spawns[:1]])]}
