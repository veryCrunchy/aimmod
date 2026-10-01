"""Map Source textures onto the few material slots a KovaaK's map has."""
from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Sequence, Tuple

from . import classify, scene
from .geometry import polygon_area

SURFACES = ("ground", "wall", "ceiling", "ramp")
NODRAW = "tools/toolsnodraw"
UNKNOWN = (0.5, 0.5, 0.5)  # Face.reflectivity default: the source format gave no texture colour
DEFAULT_TABLE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "materials.json")

Colour = Tuple[float, float, float]


def load_table(path: Optional[str] = None) -> dict:
    with open(path or DEFAULT_TABLE, encoding="utf-8") as fh:
        return json.load(fh)


def rule_for(texture: str, table: dict) -> dict:
    t = texture.lower()
    if t == NODRAW:
        return table.get("stand_in", table["default"])
    base = t.rsplit("/", 1)[-1]
    for pass_no, target in enumerate((base, t)):
        for rule in table["rules"]:
            if pass_no == 1 and rule.get("basename_only"):
                continue
            if any(k in target for k in rule["keywords"]):
                return rule
    return table["default"]


def srgb(c: Colour) -> Colour:
    return tuple(max(0.0, min(1.0, x)) ** (1 / 2.2) for x in c)  # type: ignore[return-value]


def tint_hex(c: Colour) -> str:
    return "".join(f"{int(round(max(0.0, min(1.0, x)) * 255)):02x}" for x in c) + "ff"


def auto_tint(refl: Colour, pure: bool) -> Colour:
    s = srgb(refl)
    if pure:
        return tuple(min(1.0, x * 1.1) for x in s)  # type: ignore[return-value]
    m = max(s)
    if m < 1e-3:
        return (0.4, 0.4, 0.4)
    hue = tuple(x / m for x in s)
    strength = 0.9  # follow the source colour closely; textured materials keep their detail
    bright = max(0.45, min(1.0, 0.3 + 0.85 * m))
    return tuple((1 - strength * (1 - h)) * bright for h in hue)  # type: ignore[return-value]


@dataclass
class Cluster:
    category: str
    rule: dict
    textures: Dict[str, Tuple[float, Colour]] = field(default_factory=dict)
    orient: Dict[str, float] = field(default_factory=lambda: {s: 0.0 for s in SURFACES})

    @property
    def area(self) -> float:
        return sum(a for a, _ in self.textures.values())

    def mean(self) -> Colour:
        tot = self.area or 1.0
        return tuple(sum(a * c[k] for a, c in self.textures.values()) / tot for k in range(3))  # type: ignore

    def spread(self) -> float:
        m = self.mean()
        tot = self.area or 1.0
        return sum(a * sum((c[k] - m[k]) ** 2 for k in range(3)) for a, c in self.textures.values()) / tot


@dataclass
class Slot:
    group: int
    surface: str
    material: str
    tint: str
    scale: float
    roughness: float
    metallic: float
    category: str
    textures: List[str]
    fullbright: float = 0.0


def _saturation(c) -> float:
    hi, lo = max(c), min(c)
    return 0.0 if hi < 1e-6 else (hi - lo) / hi


def _orientation(normal) -> str:
    z = normal[2]
    if z > 0.7:
        return "ground"
    if z < -0.7:
        return "ceiling"
    if abs(z) > 0.2:
        return "ramp"
    return "wall"


def allocate(sc: scene.Scene, table: dict, groups: int = 2) -> Tuple[List[Slot], Dict[str, int]]:
    """Cluster the visible textures into at most groups*4 slots. Returns slots and texture->slot index."""
    clusters: Dict[str, Cluster] = {}
    backdrop = set()
    for b in sc.brushes:
        if b.kind in (scene.CLIP, scene.GLASS):
            continue
        if b.source == "backdrop":
            # The ground plane under the map borrows the main floor's slot instead of using one up.
            backdrop |= {f.texture for f in b.faces}
            continue
        stand_in = all(classify.is_tool(f.texture) for f in b.faces)
        for f in b.faces:
            if classify.is_tool(f.texture) and not (stand_in and f.texture == NODRAW):
                continue
            rule = rule_for(f.texture, table)
            cat = rule["category"]
            cl = clusters.setdefault(cat, Cluster(cat, rule))
            area = polygon_area(f.polygon)
            prev = cl.textures.get(f.texture, (0.0, f.reflectivity))
            cl.textures[f.texture] = (prev[0] + area, f.reflectivity)
            cl.orient[_orientation(f.normal)] += area
    capacity = max(1, groups) * len(SURFACES)
    items: List[Cluster] = list(clusters.values())
    if not items:
        d = table["default"]
        items = [Cluster(d["category"], d)]

    # Merge the smallest categories into their fallbacks until everything fits.
    while len(items) > capacity:
        items.sort(key=lambda c: c.area)
        small = items.pop(0)
        target = None
        for fb in small.rule.get("fallback", []):
            target = next((c for c in items if c.category == fb), None)
            if target:
                break
        if target is None:
            m = small.mean()
            target = min(items, key=lambda c: sum((c.mean()[k] - m[k]) ** 2 for k in range(3)))
        for tex, v in small.textures.items():
            target.textures[tex] = v
        for s in SURFACES:
            target.orient[s] += small.orient[s]

    # Spend spare slots on splitting the most colour-diverse categories.
    while len(items) < capacity:
        cand = [c for c in items if len(c.textures) > 1 and c.spread() > 0.0008]
        if not cand:
            break
        big = max(cand, key=lambda c: c.area * c.spread())
        a, b = _split(big)
        if a is None:
            break
        items.remove(big)
        items += [a, b]

    slots = _assign_surfaces(items, table, groups)
    tex_slot: Dict[str, int] = {}
    for i, sl in enumerate(slots):
        for t in sl.textures:
            tex_slot[t] = i
    if backdrop and slots:
        floor = max(range(len(items)), key=lambda i: (items[i].orient["ground"], items[i].area))
        for t in backdrop:
            tex_slot.setdefault(t, floor)
    return slots, tex_slot


def _split(c: Cluster):
    texs = sorted(c.textures.items(), key=lambda kv: sum(kv[1][1]))
    lum = lambda kv: sum(kv[1][1]) / 3
    lo, hi = lum(texs[0]), lum(texs[-1])
    if hi - lo < 0.02:
        return None, None
    centres = [texs[0][1][1], texs[-1][1][1]]
    for _ in range(8):
        parts: List[List] = [[], []]
        for kv in texs:
            col = kv[1][1]
            k = min((0, 1), key=lambda i: sum((col[j] - centres[i][j]) ** 2 for j in range(3)))
            parts[k].append(kv)
        if not parts[0] or not parts[1]:
            return None, None
        for i in (0, 1):
            tot = sum(kv[1][0] for kv in parts[i]) or 1.0
            centres[i] = tuple(sum(kv[1][0] * kv[1][1][j] for kv in parts[i]) / tot for j in range(3))
    out = []
    for i in (0, 1):
        nc = Cluster(c.category, c.rule)
        nc.textures = dict(parts[i])
        share = sum(kv[1][0] for kv in parts[i]) / (c.area or 1.0)
        nc.orient = {s: v * share for s, v in c.orient.items()}
        out.append(nc)
    return out[0], out[1]


def _assign_surfaces(items: Sequence[Cluster], table: dict, groups: int) -> List[Slot]:
    free = {(g, s) for g in range(groups) for s in SURFACES}
    pairs = []
    for ci, c in enumerate(items):
        tot = sum(c.orient.values()) or 1.0
        for s in SURFACES:
            pairs.append((c.orient[s] / tot * (1 + c.area * 1e-9), ci, s))
    pairs.sort(reverse=True)
    placed: Dict[int, Tuple[int, str]] = {}
    for _, ci, s in pairs:
        if ci in placed:
            continue
        spot = next(((g, s2) for g in range(groups) for s2 in (s,) if (g, s2) in free), None)
        if spot:
            placed[ci] = spot
            free.discard(spot)
    for ci in range(len(items)):
        if ci not in placed:
            spot = sorted(free)[0]
            placed[ci] = spot
            free.discard(spot)
    slots = []
    for ci, c in enumerate(items):
        g, s = placed[ci]
        rule = c.rule
        mat = rule["material"]
        tint = rule.get("tint", "auto")
        mean = c.mean()
        if all(col == UNKNOWN for _a, col in c.textures.values()) and rule.get("colour"):
            # No texture colour in the source format: start from the category's typical colour.
            hx = rule["colour"]
            mean = tuple((int(hx[i:i + 2], 16) / 255.0) ** 2.2 for i in (0, 2, 4))
        sat = _saturation(srgb(mean))
        if tint == "auto" and sat > 0.38 and mat != "MI_WA_PureColor" and c.category not in ("grass", "wood"):
            mat = "MI_WA_PureColor"  # strongly coloured paint: keep the colour itself
        if tint == "auto":
            hexv = tint_hex(auto_tint(mean, mat == "MI_WA_PureColor"))
        elif tint == "none":
            hexv = "ffffffff"
        else:
            hexv = tint.lower().lstrip("#")[:6] + "ff"
        slots.append(Slot(group=g, surface=s, material=mat, tint=hexv, scale=float(rule.get("scale", 1.0)),
                          roughness=float(rule.get("roughness", 0.8)), metallic=float(rule.get("metallic", 0.0)),
                          category=c.category, textures=sorted(c.textures),
                          fullbright=float(rule.get("fullbright", 0.0))))
    return slots
