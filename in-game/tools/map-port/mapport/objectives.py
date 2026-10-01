"""Game-mode metadata (<map>.aimmod.json): objective zones, team spawns, flags and item spawns.

Nothing here is rendered as geometry. Positions use KovaaK's map units and axes (Unreal X forward,
Y right, Z up, one unit per Source unit); multiply by `map_scale` for Unreal centimetres.
"""
from __future__ import annotations

from typing import Dict, List

from . import scene
from .geometry import Vec

FORMAT = "aimmod.map-objectives"
VERSION = 1

VOLUME_CLASSES = {
    "func_bomb_target": "bomb_site",
    "func_buyzone": "buy_zone",
    "func_hostage_rescue": "hostage_rescue",
    "func_no_defuse": "no_defuse",
    "func_ctf_capture": "flag_capture",  # not stock CS; seen in community CTF maps
    "trigger_capture_area": "capture_area",
}
POINT_CLASSES = {
    "info_bomb_target": "bomb_target",
    "info_hostage_spawn": "hostage_spawn",
    "hostage_entity": "hostage_spawn",
    "info_ctf_flag": "flag",
    "item_teamflag": "flag",
    "ctf_flag": "flag",
    "info_ctf_flag_spawn": "flag",
}
ITEM_PREFIXES = ("weapon_", "item_", "game_weapon_", "info_weapon_", "ammo_", "holdable_")

TEAM_NAMES = {0: "any", 1: "terrorist", 2: "counter_terrorist"}


def _team(ent: Dict[str, str]) -> str:
    num = ent.get("teamnum") or ent.get("team") or ent.get("teamnumber")
    if num:
        return {"2": "terrorist", "3": "counter_terrorist"}.get(str(num).strip(), f"team_{num}")
    return "any"


def _ue(p: Vec) -> List[float]:
    return [round(p[0], 3), round(-p[1], 3), round(p[2], 3)]


def _vec(s: str) -> Vec:
    try:
        x, y, z = (float(v) for v in s.split()[:3])
        return (x, y, z)
    except ValueError:
        return (0.0, 0.0, 0.0)


def build(sc: scene.Scene, map_name: str, map_scale: float) -> dict:
    zones: Dict[int, dict] = {}
    for ent, pts in sc.volumes:
        key = id(ent)
        z = zones.get(key)
        if z is None:
            z = zones[key] = {"type": VOLUME_CLASSES.get(ent.get("classname", "").lower(), "zone"),
                              "classname": ent.get("classname", ""), "team": _team(ent),
                              "name": ent.get("targetname", ""), "_pts": []}
        z["_pts"] += [_ue(p) for p in pts]
    zone_list = []
    for z in zones.values():
        pts = z.pop("_pts")
        lo = [min(p[k] for p in pts) for k in range(3)]
        hi = [max(p[k] for p in pts) for k in range(3)]
        hull = sorted({tuple(p) for p in pts})
        z.update({"aabb": {"min": lo, "max": hi}, "points": [list(p) for p in hull]})
        zone_list.append(z)
    points, items = [], []
    for ent in sc.entities:
        cls = ent.get("classname", "").lower()
        if "origin" not in ent:
            continue
        if cls in POINT_CLASSES:
            points.append({"type": POINT_CLASSES[cls], "classname": cls, "team": _team(ent),
                           "name": ent.get("targetname", ""), "origin": _ue(_vec(ent["origin"]))})
        elif cls.startswith(ITEM_PREFIXES):
            items.append({"classname": cls, "origin": _ue(_vec(ent["origin"])),
                          "yaw": round(-_vec(ent.get("angles", ""))[1], 2), "name": ent.get("targetname", "")})
    spawns = [{"team": TEAM_NAMES.get(s.team, "any"), "classname": s.classname, "origin": _ue(s.origin),
               "yaw": round(-s.yaw, 2)} for s in sc.spawns]
    movers = [{"type": go["kind"], "name": go["name"], "target": go["target"], "origin": _ue(go["origin"]),
               "size": [round(v, 3) for v in go["size"]], "yaw": round(-go.get("yaw", 0.0), 2)}
              for go in sc.gameobjects]
    return {"format": FORMAT, "version": VERSION, "map": map_name, "units": "kovaaks_map_units",
            "map_scale": map_scale, "axes": "unreal: x forward, y right, z up (source y mirrored)",
            "zones": zone_list, "points": points, "items": items, "spawns": spawns, "movers": movers}
