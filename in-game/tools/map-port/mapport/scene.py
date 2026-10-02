"""Engine-neutral scene built from a Source map, in Source coordinates (Z up, inches)."""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

from .geometry import Vec

# How a converted brush behaves in KovaaK's.
SOLID = "solid"            # visible, collides
CLIP = "clip"              # invisible, blocks players
WEAPON_CLIP = "weaponclip"  # visible barrier that blocks shots
GLASS = "glass"            # visible see-through barrier that blocks everything
NONSOLID = "nonsolid"      # visible, no collision


@dataclass
class Face:
    polygon: List[Vec]            # outward, counter-clockwise around normal
    normal: Vec
    texture: str                  # lower-case Source material path
    reflectivity: Tuple[float, float, float] = (0.5, 0.5, 0.5)
    uv_axes: Optional[Tuple[Tuple[float, float, float, float], Tuple[float, float, float, float]]] = None
    tex_size: Tuple[int, int] = (512, 512)
    hidden: bool = False          # never seen (e.g. the underside of a displacement slab)


@dataclass
class Brush:
    faces: List[Face]
    kind: str = SOLID
    source: str = "world"         # world, entity classname or "displacement"
    # why a prop brush has no collision: "nonsolid" (solid 0, debris, open door), "detail" (a small
    # part of a colliding model), "standin" (a name-sized box for a missing model); "" otherwise
    tag: str = ""
    instance: int = -1            # prop instance the brush belongs to (a model is several brushes)

    def points(self) -> List[Vec]:
        return [p for f in self.faces for p in f.polygon]

    def bounds(self) -> Tuple[Vec, Vec]:
        pts = self.points()
        return (tuple(min(p[k] for p in pts) for k in range(3)),  # type: ignore[return-value]
                tuple(max(p[k] for p in pts) for k in range(3)))


@dataclass
class Spawn:
    origin: Vec
    yaw: float
    team: int  # 1 = terrorist, 2 = counter-terrorist, 0 = any
    classname: str = ""


@dataclass
class Scene:
    name: str
    version: int = 0  # BSP version (0 for VMF)
    brushes: List[Brush] = field(default_factory=list)
    spawns: List[Spawn] = field(default_factory=list)
    entities: List[Dict[str, str]] = field(default_factory=list)
    stats: Dict[str, int] = field(default_factory=dict)
    notes: List[str] = field(default_factory=list)
    # Objective brush volumes (bomb sites, buy zones, ...): classname -> list of (entity, points)
    volumes: List[Tuple[Dict[str, str], List[Vec]]] = field(default_factory=list)
    # KovaaK's map-creator game objects (jump pads, teleporters and their target waypoints):
    # {"kind": "jumppad"|"teleporter"|"waypoint", "origin": Vec, "size": Vec, "name": str,
    #  "target": str, "yaw": float}. Origins and sizes are Source-style map coordinates.
    gameobjects: List[Dict] = field(default_factory=list)
    # Prop models no packed or installed content provides: model -> instance count
    missing_models: Dict[str, int] = field(default_factory=dict)

    def bump(self, key: str, n: int = 1) -> None:
        self.stats[key] = self.stats.get(key, 0) + n


CONTENTS_LADDER = 0x20000000


def add_ladder(sc: "Scene", points) -> None:
    """KovaaK's cannot climb: a ladder becomes a jump pad at its foot aimed just above its top."""
    if not points:
        return
    lo = [min(p[k] for p in points) for k in range(3)]
    hi = [max(p[k] for p in points) for k in range(3)]
    if hi[2] - lo[2] < 48:
        return
    n = sum(1 for go in sc.gameobjects if go["kind"] == "waypoint" and go["name"].startswith("ladder"))
    cx, cy = (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2
    name = f"ladder{n}"
    sc.gameobjects.append({"kind": "waypoint", "origin": (cx, cy, hi[2] + 40.0), "size": (0, 0, 0),
                           "name": name, "target": "", "yaw": 0.0})
    sc.gameobjects.append({"kind": "jumppad", "origin": (cx, cy, lo[2]),
                           "size": (max(32.0, hi[0] - lo[0]), max(32.0, hi[1] - lo[1]), 16.0),
                           "name": f"ladder_pad{n}", "target": name, "yaw": 0.0})
    sc.bump("ladders_as_jump_pads")


def add_liquid(sc: "Scene", liquid: str, points, damage: Optional[float] = None) -> None:
    """Water becomes a KovaaK's Water volume; lava, slime and trigger_hurt become a Hurt volume
    (lava, and hurt triggers doing 100+ damage, kill)."""
    if not points:
        return
    lo = [min(p[k] for p in points) for k in range(3)]
    hi = [max(p[k] for p in points) for k in range(3)]
    if min(hi[k] - lo[k] for k in range(3)) < 1.0:
        return
    centre = tuple((lo[k] + hi[k]) / 2 for k in range(3))
    size = tuple(hi[k] - lo[k] for k in range(3))
    n = sum(1 for go in sc.gameobjects if go["kind"] in ("water", "hurt"))
    sc.gameobjects.append({"kind": "water" if liquid == "water" else "hurt", "origin": centre, "size": size,
                           "name": f"{liquid}{n}", "target": "", "yaw": 0.0, "liquid": liquid,
                           "damage": damage})
    sc.bump(f"{liquid}_volumes")
