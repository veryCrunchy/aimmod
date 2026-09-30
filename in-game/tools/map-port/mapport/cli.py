"""map-port: convert Source maps (.bsp/.vmf, or .gma/.zip/.rar/.7z containing them) to KovaaK's."""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from typing import List, Optional

from . import archive, bsp, cleanup, kovaaks_json, materials, preview, reflex, scenario, vmf


def _safe_name(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", name).strip("._") or "map"

def _file_name(name: str) -> str:
    """Keep spaces (scenario names use them) but drop characters Windows forbids."""
    return "".join("_" if c in '<>:"/\\|?*' or ord(c) < 32 else c for c in name).strip() or "scenario"



def convert_file(path: str, out: str, args) -> dict:
    base = _safe_name(args.name or os.path.splitext(os.path.basename(path))[0])
    with open(path, "rb") as fh:
        data = fh.read()
    if path.lower().endswith(".vmf"):
        sc = vmf.load(data.decode("utf-8", "replace"), base, args.disp_step, args.disp_thickness)
    else:
        sc = bsp.load(data, base, args.disp_step, args.disp_thickness)
    if not args.keep_skybox:
        cleanup.remove_3d_skybox(sc)
        cleanup.remove_detached(sc)
    table = materials.load_table(args.materials)
    slots, tex_slot = materials.allocate(sc, table, args.groups)
    os.makedirs(out, exist_ok=True)
    report = {"input": os.path.basename(path), "name": base, "brushes": len(sc.brushes),
              "spawns": {"T": sum(s.team == 1 for s in sc.spawns), "CT": sum(s.team == 2 for s in sc.spawns),
                         "any": sum(s.team == 0 for s in sc.spawns)},
              "stats": dict(sorted(sc.stats.items())), "notes": sc.notes,
              "slots": [{"group": s.group, "surface": s.surface, "material": s.material, "tint": s.tint,
                         "category": s.category, "textures": s.textures} for s in slots],
              "files": {}}
    if args.format in ("json", "both"):
        doc = kovaaks_json.build(sc, slots, tex_slot, args.groups, 1.0, args.map_scale)
        text = kovaaks_json.dumps(doc)
        mp = os.path.join(out, "maps", base + ".json")
        os.makedirs(os.path.dirname(mp), exist_ok=True)
        with open(mp, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        report["files"]["map_json"] = os.path.relpath(mp, out)
        if not args.no_scenario:
            mv = scenario.PRESETS[args.movement]
            sce_name = args.scenario_name or f"{base} CS Movement"
            sce = scenario.build(sce_name, base + ".json", text, args.map_scale, mv, args.bots,
                                 f"Port of {os.path.basename(path)} with Counter-Strike movement.")
            sp = os.path.join(out, "Scenarios", _file_name(sce_name) + ".sce")
            os.makedirs(os.path.dirname(sp), exist_ok=True)
            with open(sp, "w", encoding="utf-8", newline="") as fh:
                fh.write(sce)
            report["files"]["scenario"] = os.path.relpath(sp, out)
    if args.format in ("reflex", "both"):
        rp = os.path.join(out, "maps", base + ".map")
        os.makedirs(os.path.dirname(rp), exist_ok=True)
        with open(rp, "w", encoding="utf-8", newline="") as fh:
            fh.write(reflex.write(sc, table))
        report["files"]["map_reflex"] = os.path.relpath(rp, out)
    if args.preview:
        pp = os.path.join(out, base + ".preview.png")
        with open(pp, "wb") as fh:
            fh.write(preview.render(sc, slots, tex_slot))
        report["files"]["preview"] = os.path.relpath(pp, out)
    with open(os.path.join(out, base + ".report.json"), "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2)
    return report


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="map-port", description=__doc__)
    ap.add_argument("input", help=".bsp, .vmf, or a .gma/.zip/.rar/.7z containing them")
    ap.add_argument("--out", required=True, help="output folder (gets maps/ and Scenarios/ subfolders)")
    ap.add_argument("--name", help="output map name (default: input file name)")
    ap.add_argument("--scenario-name", help="scenario name (default: '<map> CS Movement')")
    ap.add_argument("--format", choices=("json", "reflex", "both"), default="json",
                    help="json = textured map-creator map (default); reflex = legacy untextured .map")
    ap.add_argument("--map-scale", type=float, default=4.0, help="Unreal units per Source unit (default 4)")
    ap.add_argument("--groups", type=int, default=2,
                    help="material slot groups; the Default pack has 2 (x4 surfaces = 8 materials)")
    ap.add_argument("--movement", choices=sorted(scenario.PRESETS), default="cs",
                    help="cs = 250 u/s, accel 5.2, friction 4 (default); css / csgo = game defaults")
    ap.add_argument("--bots", type=int, default=5, help="target bots in the scenario")
    ap.add_argument("--disp-step", type=int, default=2, help="displacement grid step (1 = full detail)")
    ap.add_argument("--disp-thickness", type=float, default=8.0, help="displacement slab thickness (units)")
    ap.add_argument("--materials", help="alternative material mapping table (JSON)")
    ap.add_argument("--keep-skybox", action="store_true", help="keep the 3D skybox room and detached areas")
    ap.add_argument("--no-scenario", action="store_true")
    ap.add_argument("--preview", action="store_true", help="also write a top-down PNG (radar orientation)")
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
    for p in paths:
        try:
            rep = convert_file(p, args.out, args if len(paths) == 1 else _named(args, None))
        except (bsp.BspError, archive.ArchiveError) as exc:
            print(f"{os.path.basename(p)}: {exc}", file=sys.stderr)
            return 1
        print(f"{rep['input']}: {rep['brushes']} brushes, spawns T={rep['spawns']['T']} "
              f"CT={rep['spawns']['CT']} any={rep['spawns']['any']}")
        for k, v in rep["stats"].items():
            print(f"  {k}: {v}")
        for s in rep["slots"]:
            print(f"  slot g{s['group']}/{s['surface']}: {s['material']} #{s['tint']} "
                  f"({s['category']}, {len(s['textures'])} textures)")
        for k, v in rep["files"].items():
            print(f"  wrote {v}")
    return 0


def _named(args, name):
    ns = argparse.Namespace(**vars(args))
    ns.name = name
    ns.scenario_name = None
    return ns


if __name__ == "__main__":
    sys.exit(main())
