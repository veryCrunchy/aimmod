"""Make sure every spawn has room for a standing CS player hull."""
from __future__ import annotations

import math
from typing import List, Optional, Tuple

from . import geometry as g
from . import scene

FLOOR_GAP = 4.0   # the hull is tested from this far above the spawn origin (feet)
# Player hull used by every check; set from the movement preset (CS 32 x 72, Quake 30 x 56).
HULL = {"half": 16.0, "height": 72.0}


def configure(radius: float, height: float) -> None:
    HULL["half"], HULL["height"] = float(radius), float(height)
BLOCKING = (scene.SOLID, scene.CLIP, scene.GLASS)


class _Solid:
    __slots__ = ("lo", "hi", "planes")

    def __init__(self, b: scene.Brush):
        self.lo, self.hi = b.bounds()
        self.planes = [(f.normal, g.dot(f.normal, f.polygon[0])) for f in b.faces]


def _solids(sc: scene.Scene) -> List[_Solid]:
    return [_Solid(b) for b in sc.brushes if b.kind in BLOCKING]


def hull_box(feet, gap: float = FLOOR_GAP) -> Tuple[tuple, tuple]:
    x, y, z = feet
    h, r = HULL["height"], HULL["half"]
    return ((x - r, y - r, z + gap), (x + r, y + r, z + gap + h))


def blocked(box, solids: List[_Solid], eps: float = 0.05) -> Optional[_Solid]:
    lo, hi = box
    for s in solids:
        if any(lo[k] >= s.hi[k] - eps or hi[k] <= s.lo[k] + eps for k in range(3)):
            continue
        separated = False
        for n, d in s.planes:
            # smallest signed distance of any box corner to the plane
            m = sum(n[k] * (lo[k] if n[k] > 0 else hi[k]) for k in range(3)) - d
            if m >= -eps:
                separated = True
                break
        if not separated:
            return s
    return None


def fix_spawns(sc: scene.Scene) -> None:
    """Nudge spawns whose hull overlaps a brush: first upwards, then sideways in growing rings."""
    solids = _solids(sc)
    for sp in sc.spawns:
        o = sp.origin
        near = [s for s in solids if all(s.lo[k] < o[k] + 256 and s.hi[k] > o[k] - 256 for k in range(3))]
        if not blocked(hull_box(sp.origin), near):
            continue
        found = None
        for dz in range(2, 66, 2):
            p = (sp.origin[0], sp.origin[1], sp.origin[2] + dz)
            if not blocked(hull_box(p), near):
                found = p
                break
        if found is None:
            for r in range(8, 136, 8):
                for i in range(16):
                    a = 2 * math.pi * i / 16
                    for dz in (0, 8, 16, 32):
                        p = (sp.origin[0] + r * math.cos(a), sp.origin[1] + r * math.sin(a), sp.origin[2] + dz)
                        if not blocked(hull_box(p), near):
                            found = p
                            break
                    if found:
                        break
                if found:
                    break
        if found is None:
            sc.bump("spawns_stuck")
            sc.notes.append(f"spawn {sp.classname} at {tuple(round(c) for c in sp.origin)} has no free hull nearby")
            continue
        sc.bump("spawns_nudged")
        sp.origin = found
