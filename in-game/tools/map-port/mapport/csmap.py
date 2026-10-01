"""AimMod CS map spec: the `cs` block of `<map>.aimmod.json` that makes a map eligible for CS competitive.

Built from the objective metadata (`objectives.build`), so it can run during a port or on top of
an existing port's `.aimmod.json` (`python -m mapport cs <files or folders>`). Same units and axes
as the rest of the file (KovaaK's map units, Source Y mirrored; times `map_scale` for centimetres).

    "cs": {
      "format": "aimmod.cs-map", "version": 1,
      "spawns": {"T": [[x, y, z, yaw], ...], "CT": [...]},          # at least 5 each
      "bomb_sites": [{"name": "A", "min": [x, y, z], "max": [x, y, z]}, {"name": "B", ...}],
      "buy_zones": {"T": [{"min": [...], "max": [...]}], "CT": [...]},
      "callouts": [{"name": "Long A", "min": [...], "max": [...]}],  # optional
      "derived": ["buy zones from spawn areas", ...],                 # what was not in the map
      "problems": []                                                  # why it is not eligible
    }

Sources: info_player_terrorist / info_player_counterterrorist (spawns), func_bomb_target
(site volumes) or info_bomb_target (a box around the point), func_buyzone with its team
(Source TeamNum 2/3, GoldSrc team 1/2), else a box around each side's spawn area. Sites
are labelled from their targetname (".._a", "..A", "bombsite_b"); otherwise A is the site
farther from the terrorist spawns (true on dust2) and B the nearer one.
"""
from __future__ import annotations

import json
import os
import re
import sys
from typing import Dict, Iterable, List, Optional, Sequence

FORMAT = "aimmod.cs-map"
VERSION = 1
MIN_SPAWNS = 5
POINT_SITE_HALF = 192.0          # info_bomb_target: a box this far around the point (map units)
POINT_SITE_DOWN, POINT_SITE_UP = 32.0, 160.0
SPAWN_BUY_MARGIN = 256.0         # fallback buy zone: the side's spawn area, grown this much
SPAWN_BUY_DOWN, SPAWN_BUY_UP = 64.0, 192.0


def _box(lo: Sequence[float], hi: Sequence[float]) -> dict:
    return {"min": [round(v, 3) for v in lo], "max": [round(v, 3) for v in hi]}


def _side(team: str, goldsrc: bool) -> Optional[str]:
    """objectives.py team string -> T / CT / None (both). GoldSrc func_buyzone uses team 1 (T) and 2 (CT),
    which objectives.py (Source TeamNum 2/3) reads as "team_1" and "terrorist"."""
    if goldsrc:
        return {"team_1": "T", "terrorist": "CT"}.get(team)
    return {"terrorist": "T", "counter_terrorist": "CT"}.get(team)


def _letter(name: str) -> Optional[str]:
    m = re.search(r"(?:^|[_\-\s])([ab])$|site[_\-\s]?([ab])\b|bombsite([ab])|target([ab])\b", name.strip().lower())
    if not m:
        return None
    return next(g for g in m.groups() if g).upper()


def _centre(points: Iterable[Sequence[float]]) -> Optional[List[float]]:
    pts = list(points)
    if not pts:
        return None
    return [sum(p[k] for p in pts) / len(pts) for k in range(3)]


def build(doc: dict, goldsrc: bool = False) -> dict:
    """The `cs` block for an objectives document (eligible or not; see `problems`)."""
    derived: List[str] = []
    spawns: Dict[str, List[List[float]]] = {"T": [], "CT": []}
    for s in doc.get("spawns", []):
        side = {"terrorist": "T", "counter_terrorist": "CT"}.get(s.get("team", ""))
        if side and len(s.get("origin", [])) == 3:
            spawns[side].append([round(v, 3) for v in s["origin"]] + [round(float(s.get("yaw", 0.0)), 2)])

    sites = []
    for z in doc.get("zones", []):
        if z.get("type") == "bomb_site":
            sites.append({"name": z.get("name", ""), **_box(z["aabb"]["min"], z["aabb"]["max"])})
    if not sites:
        for p in doc.get("points", []):
            if p.get("type") == "bomb_target":
                o = p["origin"]
                sites.append({"name": p.get("name", ""), **_box([o[0] - POINT_SITE_HALF, o[1] - POINT_SITE_HALF, o[2] - POINT_SITE_DOWN],
                                                                [o[0] + POINT_SITE_HALF, o[1] + POINT_SITE_HALF, o[2] + POINT_SITE_UP])})
        if sites:
            derived.append("bomb sites from info_bomb_target points")
    # Letters: targetnames first, else A = farther from the terrorist spawns.
    letters = [_letter(s["name"]) for s in sites]
    if len(sites) == 2 and len({l for l in letters if l}) < 2:
        t = _centre(p[:3] for p in spawns["T"])
        mids = [[(a + b) / 2 for a, b in zip(s["min"], s["max"])] for s in sites]
        if t:
            dist = [sum((m[k] - t[k]) ** 2 for k in range(2)) for m in mids]
            letters = ["A", "B"] if dist[0] >= dist[1] else ["B", "A"]
        else:
            letters = ["A", "B"]
        derived.append("site letters from their distance to the T spawns")
    elif len({l for l in letters if l}) < len(sites):
        letters = [chr(ord("A") + i) for i in range(len(sites))]
    for s, l in zip(sites, letters):
        s["name"] = l or s["name"]
    sites.sort(key=lambda s: s["name"])

    buy: Dict[str, List[dict]] = {"T": [], "CT": []}
    for z in doc.get("zones", []):
        if z.get("type") != "buy_zone":
            continue
        side = _side(z.get("team", "any"), goldsrc)
        for s in ([side] if side else ["T", "CT"]):
            buy[s].append(_box(z["aabb"]["min"], z["aabb"]["max"]))
    for side in ("T", "CT"):
        if buy[side] or not spawns[side]:
            continue
        lo = [min(p[k] for p in spawns[side]) for k in range(3)]
        hi = [max(p[k] for p in spawns[side]) for k in range(3)]
        buy[side].append(_box([lo[0] - SPAWN_BUY_MARGIN, lo[1] - SPAWN_BUY_MARGIN, lo[2] - SPAWN_BUY_DOWN],
                              [hi[0] + SPAWN_BUY_MARGIN, hi[1] + SPAWN_BUY_MARGIN, hi[2] + SPAWN_BUY_UP]))
        derived.append(f"{side} buy zone from the {side} spawn area")

    callouts = [{"name": c["name"], **_box(c["min"], c["max"])} for c in doc.get("cs", {}).get("callouts", [])
                if isinstance(c, dict) and c.get("name") and len(c.get("min", [])) == 3 and len(c.get("max", [])) == 3]
    block = {"format": FORMAT, "version": VERSION, "spawns": spawns, "bomb_sites": sites, "buy_zones": buy,
             "callouts": callouts, "derived": derived}
    block["problems"] = problems(block)
    return block


def problems(block: dict) -> List[str]:
    """Why a cs block doesn't make its map eligible (empty: eligible). Same rules as the service."""
    out = []
    names = [s.get("name") for s in block.get("bomb_sites", [])]
    if not names:
        out.append("No bomb sites")
    elif len(names) < 2 or "A" not in names or "B" not in names:
        out.append("Needs bomb sites A and B")
    for side in ("T", "CT"):
        if len(block.get("spawns", {}).get(side, [])) < MIN_SPAWNS:
            out.append(f"Fewer than {MIN_SPAWNS} {side} spawns")
    for side in ("T", "CT"):
        if not block.get("buy_zones", {}).get(side):
            out.append(f"No {side} buy zone")
    return out


def wanted(map_name: str, doc: dict) -> bool:
    """de_ maps, and any map with bomb sites, get a cs block."""
    base = re.sub(r"^aimmod_", "", map_name.lower())
    return base.startswith("de_") or any(z.get("type") == "bomb_site" for z in doc.get("zones", [])) \
        or any(p.get("type") == "bomb_target" for p in doc.get("points", []))


def is_goldsrc(path_or_name: str) -> bool:
    return os.path.basename(path_or_name).lower().split(".")[0].endswith("_cs16")


def apply_file(path: str, out_path: Optional[str] = None) -> Optional[dict]:
    """Adds (or refreshes) the cs block of one .aimmod.json; returns it, or None when the map doesn't want one."""
    with open(path, encoding="utf-8") as fh:
        doc = json.load(fh)
    if doc.get("format") != "aimmod.map-objectives" or not wanted(doc.get("map", os.path.basename(path)), doc):
        return None
    doc["cs"] = build(doc, goldsrc=is_goldsrc(path))
    target = out_path or path
    os.makedirs(os.path.dirname(os.path.abspath(target)), exist_ok=True)
    with open(target, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, indent=1)
    return doc["cs"]


def main(argv: Optional[List[str]] = None) -> int:
    """python -m mapport cs <files or folders> [--out <folder>]: add the cs block to existing ports."""
    import argparse
    ap = argparse.ArgumentParser(prog="map-port cs", description=main.__doc__)
    ap.add_argument("paths", nargs="+")
    ap.add_argument("--out", help="write copies here (<out>/<map folder>/<file>) instead of editing in place")
    args = ap.parse_args(argv)
    files = []
    for p in args.paths:
        if os.path.isdir(p):
            for root, _, names in os.walk(p):
                files += [os.path.join(root, n) for n in names if n.endswith(".aimmod.json")]
        else:
            files.append(p)
    for f in sorted(files):
        target = None
        if args.out:
            target = os.path.join(args.out, os.path.basename(os.path.dirname(os.path.abspath(f))), os.path.basename(f))
        block = apply_file(f, target)
        name = os.path.basename(f)
        if block is None:
            print(f"{name}: not a CS map (no bomb sites)")
            continue
        state = "eligible" if not block["problems"] else "not eligible: " + "; ".join(block["problems"])
        print(f"{name}: sites {','.join(s['name'] for s in block['bomb_sites']) or '-'}, spawns T={len(block['spawns']['T'])} "
              f"CT={len(block['spawns']['CT'])}, buy zones T={len(block['buy_zones']['T'])} CT={len(block['buy_zones']['CT'])}; {state}"
              + (f" (derived: {'; '.join(block['derived'])})" if block["derived"] else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
