"""Legacy KovaaK's map writer ("reflex map version 8").

The current build renders every legacy brush with one world material (the dev grid, tinted by
the face colour), so this writer is only a fallback for tools that still expect .map files.
"""
from __future__ import annotations

from typing import Dict, List

from . import classify, scene
from .geometry import dot, cross, sub
from .materials import rule_for, srgb

CLIP_MATERIAL = "internal/editor/textures/editor_clip"


def to_reflex(p, unit: float):
    # Source Z-up -> Reflex Y-up; the mirror on the last axis keeps the layout unmirrored.
    return (p[0] * unit, p[2] * unit, -p[1] * unit)


def _colour(refl) -> str:
    r, g, b = (int(round(c * 255)) for c in srgb(refl))
    return f"0xff{r:02x}{g:02x}{b:02x}"


def write(sc: scene.Scene, table: dict, unit: float = 1.0) -> str:
    reflex_names: Dict[str, str] = table.get("reflex", {})
    out: List[str] = ["reflex map version 8", "global", "\tentity", "\t\ttype WorldSpawn",
                      f"\t\tString256 title {sc.name}", "\t\tUInt8 playersMax 16"]
    for b in sc.brushes:
        verts: List[tuple] = []
        faces: List[str] = []
        for f in b.faces:
            idx = []
            for p in f.polygon:
                q = to_reflex(p, unit)
                for i, v in enumerate(verts):
                    if max(abs(v[k] - q[k]) for k in range(3)) < 1e-3:
                        idx.append(i)
                        break
                else:
                    verts.append(q)
                    idx.append(len(verts) - 1)
            if b.kind == scene.CLIP or classify.is_tool(f.texture):
                mat, col = CLIP_MATERIAL if b.kind == scene.CLIP else "structural/dev/dev_grey128", "0x00000000"
            else:
                cat = rule_for(f.texture, table)["category"]
                mat = reflex_names.get(cat, reflex_names.get("default", "structural/dev/dev_grey128"))
                col = _colour(f.reflectivity)
            faces.append((idx, mat, col))
        # Reflex expects faces wound so the first triangle faces outward from the brush centre.
        centre = tuple(sum(v[k] for v in verts) / len(verts) for k in range(3))
        lines = []
        for idx, mat, col in faces:
            a, b_, c = (verts[i] for i in idx[:3])
            fc = tuple(sum(verts[i][k] for i in idx) / len(idx) for k in range(3))
            if dot(cross(sub(b_, a), sub(c, a)), sub(fc, centre)) < 0:
                idx = list(reversed(idx))
            lines.append("\t\t\t0.000000 0.000000 1.000000 1.000000 0.000000 " + " ".join(map(str, idx))
                         + f" {col} {mat}")
        out += ["\tbrush", "\t\tvertices"] + [f"\t\t\t{v[0]:.6f} {v[1]:.6f} {v[2]:.6f}" for v in verts]
        out += ["\t\tfaces"] + lines
    for sp in sc.spawns:
        x, y, z = to_reflex(sp.origin, unit)
        out += ["\tentity", "\t\ttype PlayerSpawn", f"\t\tVector3 position {x:.6f} {y:.6f} {z:.6f}",
                f"\t\tVector3 angles {-sp.yaw + 90:.6f} 0.000000 0.000000"]
        if sp.team == 1:
            out.append("\t\tBool8 teamB 0")
        elif sp.team == 2:
            out.append("\t\tBool8 teamA 0")
    return "\r\n".join(out) + "\r\n"
