"""Write a KovaaK's map-creator JSON map (the format the in-game editor saves, version 1.0.0).

Every Source brush becomes a "Cube" brush carrying a `procedural` mesh: one section per face,
each with its own `materialSets` entry. This is how the editor itself stores brushes whose
vertices were edited, so arbitrary convex brushes survive with per-face materials.
Coordinates are KovaaK's map units (Unreal axes: X forward, Y right, Z up); the scenario's
MapScale multiplies them into Unreal centimetres at load time.

Procedural vertices are an exception: the loader multiplies them by MapScale once more (world =
location + scale * vertex * MapScale, in map units). The editor therefore stores a unit cube as
100 / MapScale (26.2295 at 3.8125, 20 at 5). Vertices are written divided by MapScale.
"""
from __future__ import annotations

import json
from typing import Dict, List, Optional, Sequence, Tuple

from . import classify, geometry as g, scene
from .materials import NODRAW, SURFACES, Slot

BRUSH_TYPE = {
    scene.SOLID: "Default",
    scene.NONSOLID: "DefaultNoCollision",
    scene.CLIP: "Clip",
    scene.WEAPON_CLIP: "WeaponClip",
    scene.GLASS: "FullClip",
}
SKY_TEXTURE = "tools/toolsskybox"
SPAWN_GAP = 4.0  # spawn points sit at hull centre + this, above the feet (origin is the feet)


def to_ue(p, unit: float) -> Tuple[float, float, float]:
    """Source (right-handed, Z up) -> Unreal (left-handed, Z up). Mirroring Y keeps the layout unmirrored."""
    return (p[0] * unit, -p[1] * unit, p[2] * unit)


def _num(x: float, digits: int) -> str:
    s = f"{x:.{digits}f}"
    if "." in s:
        s = s.rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def _f(v: Sequence[float], digits: int = 4) -> str:
    """Compact "x, y, z" (trailing zeros trimmed; the loader parses each part with stof)."""
    return ", ".join(_num(x, digits) for x in v)


def _material_sets(slots: List[Slot], groups: int) -> List[dict]:
    def entry(material: str, pack: str, tint: str, scale: float, rough: float, metal: float,
              bright: float = 0.0) -> dict:
        return {"material": material, "pack": pack, "properties": [
            {"name": "Tint", "value": tint},
            {"name": "Scale", "value": scale},
            {"name": "Roughness", "value": rough},
            {"name": "Metallic", "value": metal},
            {"name": "FullBright", "value": bright},
        ]}

    sets = []
    for gi in range(groups):
        grp = {}
        for s in SURFACES:
            sl = next((x for x in slots if x.group == gi and x.surface == s), None)
            if sl:
                grp[s] = entry(sl.material, "Default", sl.tint, sl.scale, sl.roughness, sl.metallic, sl.fullbright)
            else:
                grp[s] = entry("MI_WA_ConcretePoured", "Default", "bfbfbfff", 1.0, 0.8, 0.0)
        sets.append(dict(sorted(grp.items())))
    # The editor always writes one extra (DLC pack) group; mirror it with empty slots.
    sets.append({s: {"material": "None", "pack": "None", "properties": [
        {"name": "Tint", "value": "ffffffff"}, {"name": "Scale", "value": 1.0},
        {"name": "Roughness", "value": 0.5}, {"name": "Metallic", "value": 0.5},
        {"name": "FullBright", "value": 0.0}]} for s in sorted(SURFACES)})
    return sets


def _uv(p, face: scene.Face) -> Tuple[float, float]:
    if not face.uv_axes:
        return (p[0] / 128.0, p[2] / 128.0)
    s, t = face.uv_axes
    w, h = face.tex_size
    return ((p[0] * s[0] + p[1] * s[1] + p[2] * s[2] + s[3]) / w,
            (p[0] * t[0] + p[1] * t[1] + p[2] * t[2] + t[3]) / h)


def _tangent(face: scene.Face, n_ue) -> Tuple[float, float, float]:
    axis = face.uv_axes[0][:3] if face.uv_axes else (1.0, 0.0, 0.0)
    a = (axis[0], -axis[1], axis[2])
    t = g.sub(a, g.mul(n_ue, g.dot(n_ue, a)))
    t = g.normalize(t)
    if t == (0.0, 0.0, 0.0):
        t = g.normalize(g.cross(n_ue, (0.0, 0.0, 1.0)))
        if t == (0.0, 0.0, 0.0):
            t = (1.0, 0.0, 0.0)
    return t


def brush_object(b: scene.Brush, unit: float, tex_slot: Dict[str, int], slots: List[Slot],
                 map_scale: float = 1.0, faces: Optional[List[scene.Face]] = None) -> dict:
    """faces: the faces to export (cull.visible_faces); the brush's own faces when None."""
    visible = b.kind not in (scene.CLIP,)
    faces = b.faces if faces is None or not visible else faces
    # Default slot for tool faces on visible brushes: the brush's largest real texture.
    best_slot, best_area = 0, -1.0
    for f in b.faces:
        if f.texture in tex_slot and not classify.is_tool(f.texture):
            a = g.polygon_area(f.polygon)
            if a > best_area:
                best_slot, best_area = tex_slot[f.texture], a
    sections, msets = [], []
    # Sky faces of visible brushes would be painted with the brush material and hide the sky; KovaaK's
    # has no invisible-face material, so those sections are left out (the other faces keep the shape).
    sky = SKY_TEXTURE
    keep = [f for f in faces if not (visible and f.texture == sky)]
    if not keep or (faces is b.faces and len(keep) < 3):
        keep = list(b.faces)
    world = [[to_ue(p, unit) for p in f.polygon] for f in keep]
    lo = tuple(min(p[k] for poly in world for p in poly) for k in range(3))
    pairs = list(zip(keep, world))
    for f, poly in pairs:
        n = (f.normal[0], -f.normal[1], f.normal[2])
        local = [g.mul(g.sub(p, lo), 1.0 / map_scale) for p in poly]
        tris = g.triangulate_fan(len(local))
        # The editor's winding: cross(v1 - v0, v2 - v0) points against the outward normal.
        a, b_, c = local[0], local[1], local[2]
        if g.dot(g.cross(g.sub(b_, a), g.sub(c, a)), n) > 0:
            local.reverse()
            f_poly = list(reversed(f.polygon))
        else:
            f_poly = f.polygon
        indices = [i for tri in tris for i in tri]
        verts = []
        if visible and not f.hidden:
            tan = _tangent(f, n)
            ns, ts = _f(n, 3), _f(tan, 3) + ", false"
            for p_local, p_src in zip(local, f_poly):
                verts.append({"location": _f(p_local, 3), "normal": ns, "tangent": ts, "uv0": _f(_uv(p_src, f), 3)})
        else:
            ns = _f(n, 3)
            verts = [{"location": _f(p, 3), "normal": ns} for p in local]
        sections.append({"indices": indices, "vertices": verts})
        if visible:
            if best_area < 0:
                best_slot = tex_slot.get(NODRAW, 0)
            si = best_slot if classify.is_tool(f.texture) else tex_slot.get(f.texture, best_slot)
            sl = slots[si] if slots else None
            msets.append({"group": sl.group if sl else 0, "surface": sl.surface if sl else "wall"})
    obj = {"location": _f(lo, 2), "mesh": "Cube", "name": BRUSH_TYPE[b.kind], "procedural": sections,
           "rotation": "0.000000, 0.000000, 0.000000", "scale": "1.000000, 1.000000, 1.000000", "type": "brush"}
    if visible:
        obj["materialSets"] = msets
    return dict(sorted(obj.items()))


def spawn_object(sp: scene.Spawn, idx: int, unit: float, map_scale: float, player_profile: str) -> dict:
    from .spawns import HULL
    loc = to_ue((sp.origin[0], sp.origin[1], sp.origin[2] + HULL["height"] / 2 + SPAWN_GAP), unit)
    mask = {1: 1, 2: 2}.get(sp.team, 3)
    inv = 1.0 / map_scale
    return {"location": _f(loc, 6), "name": "SpawnPoint", "properties": [
        {"name": "Name", "value": f"{sp.classname or 'spawn'}_{idx}"},
        {"name": "TeamMask", "value": mask},
        {"name": "Path", "value": ""},
        {"name": "LoopingPath", "value": False},
        {"name": "PermittedCharacterProfiles", "value": ""},
        {"name": "Weight", "value": 1.0}],
        "rotation": _f((0.0, 0.0, -sp.yaw), 6), "scale": _f((inv, inv, inv), 6), "type": "gameObject"}


# Map-creator game object meshes (measured from the 3.9.11 assets):
#  - Water: `water_cube`, 200 units across and centred on the actor (-100..100 on every axis).
#  - Hurt, JumpPad, Teleporter: `SM_Cube_Volumes` (plus a box over it), 100 units across with the
#    pivot on the minimum corner (0..100).
# These rows do not keep their native scale: the actor scale is multiplied by MapScale like the
# locations, so a scale of size / mesh size covers `size` map units.
WATER_MESH = 200.0
VOLUME_MESH = 100.0

# The game's own Water defaults (KovaaKMapCreatorGameObjectTable), as RRGGBBAA. The wave height
# stays 0, so the surface is the flat top face of the liquid brush.
WATER_COLOURS = {
    "BaseColor": "008398ff", "DepthFadeColor": "40dfbeff", "HighlightColor1": "3d71dfff",
    "HighlightColor2": "004ec2ff", "RippleShadowColor": "5ad8fcff", "RippleHighlightColor": "6ecdfaff",
    "MurkColor": "008398ff"}
WATER_WAVE_SPEED = 1.0
WATER_WAVE_HEIGHT = 0.0


def _size(go: dict) -> Tuple[float, float, float]:
    sx, sy, sz = (max(1.0, abs(v)) for v in go["size"])
    return sx, sy, sz


def _corner(go: dict, unit: float, size: Sequence[float]) -> Tuple[float, float, float]:
    """Unreal location of the minimum corner of a box centred on go["origin"] (corner-pivot meshes)."""
    c = to_ue(go["origin"], unit)
    return (c[0] - size[0] * unit / 2, c[1] - size[1] * unit / 2, c[2] - size[2] * unit / 2)


def water_object(go: dict, unit: float) -> dict:
    """KovaaK's Water over a liquid brush. The mesh is centred, so the actor sits on the brush centre
    and its top face is the brush's top face (the water line). In AimMod scenarios AimModCore makes
    these swimmable (in-game/native-mod/DESIGN.md, "Water")."""
    sx, sy, sz = _size(go)
    props = [{"name": k, "value": v} for k, v in WATER_COLOURS.items()]
    props += [{"name": "WaveSpeed", "value": WATER_WAVE_SPEED}, {"name": "WaveHeight", "value": WATER_WAVE_HEIGHT}]
    return {"location": _f(to_ue(go["origin"], unit), 3), "name": "Water", "properties": props,
            "rotation": "0, 0, 0", "scale": _f((sx / WATER_MESH, sy / WATER_MESH, sz / WATER_MESH), 4),
            "type": "gameObject"}


def hurt_object(go: dict, unit: float) -> dict:
    """Hurt volume over a liquid or trigger box: damage, or a kill for lava and 100+ damage."""
    size = _size(go)
    dmg = go.get("damage")
    kill = go.get("liquid") == "lava" or (dmg is not None and dmg >= 100)
    props = [{"name": "Kill", "value": kill},
             {"name": "Damage", "value": float(dmg if dmg is not None else (100.0 if kill else 10.0))},
             {"name": "Cooldown", "value": 1.0}]
    return {"location": _f(_corner(go, unit, size), 3), "name": "Hurt", "properties": props,
            "rotation": "0, 0, 0", "scale": _f(tuple(v / VOLUME_MESH for v in size), 4), "type": "gameObject"}


def game_object(go: dict, unit: float, map_scale: float) -> dict:
    """Water, Hurt, JumpPad / Teleporter (sized to the trigger) and their target Waypoints (native size)."""
    kind = go["kind"]
    inv = 1.0 / map_scale
    if kind == "water":
        return water_object(go, unit)
    if kind == "hurt":
        return hurt_object(go, unit)
    if kind == "waypoint":
        return {"location": _f(to_ue(go["origin"], unit), 3), "name": "Waypoint", "properties": [
            {"name": "Name", "value": go["name"]}, {"name": "BotPauseTimeMin", "value": 0.0},
            {"name": "BotPauseTimeMax", "value": 0.0}],
            "rotation": _f((0.0, 0.0, -go.get("yaw", 0.0)), 3), "scale": _f((inv, inv, inv), 6), "type": "gameObject"}
    sx, sy, sz = _size(go)
    props = [{"name": "Target", "value": go["target"]}]
    if kind == "teleporter":
        props.append({"name": "TeleportDelay", "value": 0.0})
    # Jump pads are 25 units thick around the trigger's centre; teleporters keep their height.
    size = (sx, sy, max(25.0, sz) if kind == "teleporter" else 25.0)
    return {"location": _f(_corner(go, unit, size), 3), "name": "JumpPad" if kind == "jumppad" else "Teleporter",
            "properties": props, "rotation": "0, 0, 0", "scale": _f(tuple(v / VOLUME_MESH for v in size), 4),
            "type": "gameObject"}



def build(sc: scene.Scene, slots: List[Slot], tex_slot: Dict[str, int], groups: int, unit: float,
          map_scale: float, player_profile: str = "", cull_hidden: bool = True) -> dict:
    """cull_hidden: leave out hidden and doubled faces (cull.visible_faces)."""
    faces: Dict[int, List[scene.Face]] = {}
    if cull_hidden:
        from . import cull
        faces, counts = cull.visible_faces(sc)
        for k, v in counts.items():
            sc.stats[k] = v
    objects = [brush_object(b, unit, tex_slot, slots, map_scale, faces.get(id(b))) for b in sc.brushes]
    objects += [spawn_object(sp, i, unit, map_scale, player_profile) for i, sp in enumerate(sc.spawns)]
    objects += [game_object(go, unit, map_scale) for go in sc.gameobjects]
    return {"materialSets": _material_sets(slots, groups), "objects": objects, "version": "1.0.0"}


def dumps(doc: dict) -> str:
    """Pretty enough for humans, compact enough for 10k-brush maps: one object per line."""
    parts = ["{", '    "materialSets": ' + json.dumps(doc["materialSets"], indent=4).replace("\n", "\n    ") + ",",
             '    "objects": [']
    objs = doc["objects"]
    for i, o in enumerate(objs):
        parts.append("        " + json.dumps(o, separators=(",", ":")) + ("," if i + 1 < len(objs) else ""))
    parts += ["    ],", f'    "version": {json.dumps(doc["version"])}', "}"]
    return "\n".join(parts) + "\n"
