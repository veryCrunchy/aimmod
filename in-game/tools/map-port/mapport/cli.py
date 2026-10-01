"""map-port: convert Source maps (.bsp/.vmf, or .gma/.zip/.rar/.7z containing them) to KovaaK's."""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from typing import List, Optional

from . import (archive, bsp, checks, cleanup, goldsrc, kovaaks_json, materials, naming, objectives, preview, quake3,
               reflex, scenario, spawns, thumbnail, views, vmf)


def _safe_name(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", name).strip("._") or "map"

def _thumbnails(sc, slots, tex_slot, reached, out, base, sce_name, display, game, variant, mv, map_text, args,
                report) -> None:
    """Capture views (+ a bot-free capture scenario) and the software-rendered Workshop thumbnails."""
    eye = mv.hull_height * 0.89
    cams = thumbnail.choose_views(sc, reached, eye)
    if not cams:
        report["notes"].append("no thumbnail views: no walkable area found")
        return
    cap_name = naming.check(f"AimMod Capture - {base}")
    if map_text is not None:
        cap = scenario.build(cap_name, base + ".json", map_text, args.map_scale, mv, 0,
                             "Thumbnail capture scenario (no bots). Not for upload.")
        cp = os.path.join(out, "Capture", cap_name + ".sce")
        os.makedirs(os.path.dirname(cp), exist_ok=True)
        with open(cp, "w", encoding="utf-8", newline="") as fh:
            fh.write(cap)
        report["files"]["capture_scenario"] = os.path.relpath(cp, out)
    vp = os.path.join(out, base + ".thumb-views.json")
    with open(vp, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(thumbnail.views_document(cap_name, base, cams, args.map_scale), fh, indent=1)
    report["files"]["thumb_views"] = os.path.relpath(vp, out)
    try:
        import PIL  # noqa: F401
    except ImportError:
        report["notes"].append("Pillow is not installed: Workshop thumbnails skipped (pip install pillow)")
        return
    image = thumbnail.render_image(sc, slots, tex_slot, cams[0], 1600, 900)
    files = thumbnail.write_thumbnails(out, base, image, display, game, variant)
    report["files"].update(files)
    report["files"]["preview"] = files["workshop_thumb"]  # what the Workshop bundle helper picks up


def convert_file(path: str, out: str, args) -> dict:
    mapid = naming.map_id(args.name or os.path.splitext(os.path.basename(path))[0])
    with open(path, "rb") as fh:
        data = fh.read()
    if path.lower().endswith(".vmf"):
        sc = vmf.load(data.decode("utf-8", "replace"), mapid, args.disp_step, args.disp_thickness)
    elif data[:4] == b"IBSP":
        sc = quake3.load(data, mapid)
    elif data[:4] == b"\x1e\x00\x00\x00":
        sc = goldsrc.load(data, mapid, os.path.dirname(path))
    else:
        sc = bsp.load(data, mapid, args.disp_step, args.disp_thickness, with_props=not args.no_props)
    game = naming.game_tag(args.game) if args.game else naming.guess_game(sc.version)
    movement = args.movement or ("quake" if game in ("Q3", "QL") else "cs")
    mv = scenario.PRESETS[movement]
    variant = args.variant or mv.variant
    display = args.display_name or naming.display_name(mapid)
    sce_name = naming.scenario_name(display, game, variant)
    base = naming.file_id(mapid, game)
    spawns.configure(mv.hull_radius, mv.hull_height, mv.crouch_height)
    if not args.keep_skybox:
        cleanup.remove_3d_skybox(sc)
        cleanup.remove_detached(sc)
    if args.kill_below is not None:
        cleanup.add_kill_below(sc, args.kill_below)
    if not args.no_ground:
        cleanup.add_ground_plane(sc)
    spawns.fix_spawns(sc)
    checks.remove_floating(sc)
    table = materials.load_table(args.materials)
    slots, tex_slot = materials.allocate(sc, table, args.groups)
    os.makedirs(out, exist_ok=True)
    report = {"input": os.path.basename(path), "name": base, "scenario": sce_name, "display_name": display,
              "game": game, "variant": variant, "brushes": len(sc.brushes),
              "spawns": {"T": sum(s.team == 1 for s in sc.spawns), "CT": sum(s.team == 2 for s in sc.spawns),
                         "any": sum(s.team == 0 for s in sc.spawns)},
              "stats": dict(sorted(sc.stats.items())), "notes": sc.notes,
              "slots": [{"group": s.group, "surface": s.surface, "material": s.material, "tint": s.tint,
                         "category": s.category, "textures": s.textures} for s in slots],
              "files": {}}
    text_for_capture = None
    if args.format in ("json", "both"):
        doc = kovaaks_json.build(sc, slots, tex_slot, args.groups, 1.0, args.map_scale)
        text = kovaaks_json.dumps(doc)
        text_for_capture = text
        mp = os.path.join(out, "maps", base + ".json")
        os.makedirs(os.path.dirname(mp), exist_ok=True)
        with open(mp, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        report["files"]["map_json"] = os.path.relpath(mp, out)
        if not args.no_scenario:
            sce = scenario.build(sce_name, base + ".json", text, args.map_scale, mv, args.bots,
                                 f"Port of {os.path.basename(path)}. Movement: {mv.label}. "
                                 f"Shift: {mv.shift}, Ctrl: crouch.")
            sp = os.path.join(out, "Scenarios", sce_name + ".sce")
            os.makedirs(os.path.dirname(sp), exist_ok=True)
            with open(sp, "w", encoding="utf-8", newline="") as fh:
                fh.write(sce)
            report["files"]["scenario"] = os.path.relpath(sp, out)
            ability = scenario.shift_ability(mv)
            ap_ = os.path.join(out, "Abilities", ability[0][1] + ".abilsprint")
            os.makedirs(os.path.dirname(ap_), exist_ok=True)
            with open(ap_, "w", encoding="utf-8", newline="") as fh:
                fh.write("\r\n".join(f"{k}={v}" for k, v in ability) + "\r\n")
            report["files"]["ability"] = os.path.relpath(ap_, out)
    if args.format in ("reflex", "both"):
        rp = os.path.join(out, "maps", base + ".map")
        os.makedirs(os.path.dirname(rp), exist_ok=True)
        with open(rp, "w", encoding="utf-8", newline="") as fh:
            fh.write(reflex.write(sc, table))
        report["files"]["map_reflex"] = os.path.relpath(rp, out)
    if not args.no_preview:
        pp = os.path.join(out, base + ".preview.png")
        with open(pp, "wb") as fh:
            fh.write(preview.render(sc, slots, tex_slot))
        report["files"]["preview_check"] = os.path.relpath(pp, out)
        report["files"].setdefault("preview", report["files"]["preview_check"])
    # strafe-jumping presets cover longer gaps than CS's capped air speed
    result = checks.run(sc, slots, tex_slot, jump_up=mv.jump_height * 0.95,
                        gap_cells=checks.GAP_CELLS if mv.clamp_air_speed else checks.GAP_CELLS + 3)
    reached = result.pop("_reached")
    report["checks"] = result
    if not args.no_thumbnail:
        _thumbnails(sc, slots, tex_slot, reached, out, base, sce_name, display, game, variant, mv, text_for_capture,
                    args, report)
    if args.views:
        vd = os.path.join(out, base + ".views")
        os.makedirs(vd, exist_ok=True)
        extra = []
        mid = (sum(p[0] for p in result["view_spots"]) / max(1, len(result["view_spots"])),
               sum(p[1] for p in result["view_spots"]) / max(1, len(result["view_spots"])))
        import math
        for i, p in enumerate(result["view_spots"]):
            yaw = math.degrees(math.atan2(mid[1] - p[1], mid[0] - p[0]))
            extra.append((f"walk{i}_{int(p[0])}_{int(p[1])}_{int(p[2])}", tuple(p), yaw))
        for spec in args.view or []:
            x, y, z, yaw = (float(v) for v in spec.split(","))
            extra.append((f"view_{len(extra)}", (x, y, z), yaw))
        report["views"] = {}
        for name, png, void in views.render_all(sc, slots, tex_slot, extra):
            with open(os.path.join(vd, name + ".png"), "wb") as fh:
                fh.write(png)
            report["views"][name] = round(void, 4)
    op = os.path.join(out, base + ".aimmod.json")
    with open(op, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(objectives.build(sc, base, args.map_scale), fh, indent=1)
    report["files"]["objectives"] = os.path.relpath(op, out)
    with open(os.path.join(out, base + ".report.json"), "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2)
    return report


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="map-port", description=__doc__)
    ap.add_argument("input", help=".bsp, .vmf, or a .gma/.zip/.rar/.7z containing them")
    ap.add_argument("--out", required=True, help="output folder (gets maps/ and Scenarios/ subfolders)")
    ap.add_argument("--name", help="map id (default: input file name, e.g. de_dust2)")
    ap.add_argument("--display-name", help="map display name in the scenario name (default: Dust2, Mirage, ... "
                                           "for known maps, else the map id)")
    ap.add_argument("--game", help=f"game tag: {', '.join(naming.GAME_TAGS)} (default: CSGO for BSP v21, else CSS)")
    ap.add_argument("--variant", help="scenario variant (default: 'CS Movement', or 'Sprint Movement')")
    ap.add_argument("--format", choices=("json", "reflex", "both"), default="json",
                    help="json = textured map-creator map (default); reflex = legacy untextured .map")
    ap.add_argument("--map-scale", type=float, default=4.0, help="Unreal units per Source unit (default 4)")
    ap.add_argument("--groups", type=int, default=2,
                    help="material slot groups; the Default pack has 2 (x4 surfaces = 8 materials)")
    ap.add_argument("--movement", choices=sorted(scenario.PRESETS),
                    help="movement preset from movement_presets.json (default: quake for Q3/QL maps, else cs)")
    ap.add_argument("--bots", type=int, default=5, help="target bots in the scenario")
    ap.add_argument("--disp-step", type=int, default=2, help="displacement grid step (1 = full detail)")
    ap.add_argument("--disp-thickness", type=float, default=16.0, help="displacement slab thickness (units)")
    ap.add_argument("--materials", help="alternative material mapping table (JSON)")
    ap.add_argument("--keep-skybox", action="store_true", help="keep the 3D skybox room and detached areas")
    ap.add_argument("--no-scenario", action="store_true")
    ap.add_argument("--views", action="store_true",
                    help="render first-person check views from the spawns; void below the horizon is magenta")
    ap.add_argument("--view", action="append", metavar="X,Y,Z,YAW",
                    help="extra check view from Source feet position and yaw (with --views)")
    ap.add_argument("--no-thumbnail", action="store_true", help="skip the Workshop thumbnail and capture views")
    ap.add_argument("--allow-check-fail", action="store_true",
                    help="write outputs and exit 0 even when the hard checks fail")
    ap.add_argument("--no-props", action="store_true", help="skip prop hulls from models packed in the BSP")
    ap.add_argument("--kill-below", type=float, metavar="Z",
                    help="add a kill volume under the map up to this height (Source units), for maps where "
                         "falling off should mean death")
    ap.add_argument("--no-ground", action="store_true", help="skip the backdrop ground plane under the map")
    ap.add_argument("--no-preview", action="store_true",
                    help="skip the preview check PNG (top-down + side view with the player hull at every spawn)")
    ap.add_argument("--pick", help="when an archive holds several maps, convert only names containing this")
    args = ap.parse_args(argv)

    src = args.input
    if not os.path.isfile(src):
        ap.error(f"no such file: {src}")
    if src.lower().endswith(archive.MAP_EXTS):
        paths = [src]
    else:
        paths = archive.extract(src, os.path.join(args.out, "_extracted", _safe_name(os.path.basename(src))))
        if args.pick:
            paths = [p for p in paths if args.pick.lower() in os.path.basename(p).lower()]
        if not paths:
            print("no .bsp or .vmf found in archive", file=sys.stderr)
            return 1
    failed = False
    for p in paths:
        try:
            rep = convert_file(p, args.out, args if len(paths) == 1 else _named(args, None))
        except (bsp.BspError, archive.ArchiveError, naming.PortNameError) as exc:
            print(f"{os.path.basename(p)}: {exc}", file=sys.stderr)
            return 1
        print(f"{rep['input']}: {rep['brushes']} brushes, spawns T={rep['spawns']['T']} "
              f"CT={rep['spawns']['CT']} any={rep['spawns']['any']}")
        for k, v in rep["stats"].items():
            print(f"  {k}: {v}")
        for s in rep["slots"]:
            print(f"  slot g{s['group']}/{s['surface']}: {s['material']} #{s['tint']} "
                  f"({s['category']}, {len(s['textures'])} textures)")
        chk = rep.get("checks", {})
        print(f"  checks: {'PASS' if chk.get('pass') else 'FAIL'} {chk.get('problems')} "
              f"reach={chk.get('reachability')} dark_faces={chk.get('dark_faces')}")
        if not chk.get("pass") and not args.allow_check_fail:
            failed = True
        for k, v in rep.get("views", {}).items():
            print(f"  view {k}: {v * 100:.1f}% holes below the horizon")
        for k, v in rep["files"].items():
            print(f"  wrote {v}")
    if failed:
        print("hard checks failed (see report 'checks'); use --allow-check-fail to accept", file=sys.stderr)
        return 3
    return 0


def _named(args, name):
    ns = argparse.Namespace(**vars(args))
    ns.name = name
    ns.display_name = None
    return ns


if __name__ == "__main__":
    sys.exit(main())
