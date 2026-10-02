"""Collision audit: where what you see and what you bump into disagree.

The report's `collision` block lists, inside the playable height band (spawns - 128 to + 256):

* `visible_no_collision`: visible prop objects players and shots pass through, by model. `reason`
  says why: `nonsolid` (the map makes it non-solid: `solid 0`, debris, open doors; as in CS),
  `detail` (a small part of a colliding model: trims, handles, rods), `standin` (a name-sized box
  for a missing model whose real shape may have openings) or `unported` (a colliding prop that only
  has a visual hull). Only `unported` is unexpected.
* `missing_models`: prop models nothing provides (not packed, no installed game has them): neither
  visible nor solid.
* `invisible_collision`: invisible player clips, by what they sit against: `boundary` (sky or
  wall-sized clips that keep players in the map), `stairs_edges` (sloped clips, and clips against
  world geometry or other clips that smooth stairs, ledges and corners), `prop` (clips around
  props) and `stray` (touching nothing visible and no other clip). `remove_stray_clips` drops the
  stray ones before the report, so a port's audit lists those it could not explain.
"""
from __future__ import annotations

from typing import Dict, List, Tuple

from . import scene

TOUCH = 2.0
BOUNDARY = 512.0
BAND = (-128.0, 256.0)
SKY = ("tools/toolsskybox", "tools/toolsskybox2d")


def _overlap(a, b, m: float) -> bool:
    return all(a[0][k] - m <= b[1][k] and b[0][k] - m <= a[1][k] for k in range(3))


def _model(b: scene.Brush) -> str:
    t = b.faces[0].texture if b.faces else ""
    return t[5:] if t.startswith("prop:") else t


def _band(sc: scene.Scene):
    zs = [s.origin[2] for s in sc.spawns] or [0.0]
    return (min(zs) + BAND[0], max(zs) + BAND[1])


def classify_clips(sc: scene.Scene) -> List[Tuple[scene.Brush, str]]:
    """(clip brush, category) for player clips in the playable band; see the module notes."""
    band = _band(sc)
    boxes = [(b, b.bounds()) for b in sc.brushes if b.faces]
    visible = [(b, bb) for b, bb in boxes if b.kind != scene.CLIP and b.source != "backdrop"]
    clip_boxes = [(b, bb) for b, bb in boxes if b.kind == scene.CLIP]
    out = []
    for b, bb in clip_boxes:
        if bb[1][2] < band[0] or bb[0][2] > band[1]:
            continue
        size = [bb[1][k] - bb[0][k] for k in range(3)]
        if any(f.texture in SKY for f in b.faces) or max(size[0], size[1]) > BOUNDARY:
            out.append((b, "boundary"))
            continue
        touching = [v for v, vb in visible if _overlap(bb, vb, TOUCH)]
        if any(v.source == "prop" for v in touching):
            out.append((b, "prop"))
        elif (touching or any(0.2 < abs(f.normal[2]) < 0.98 for f in b.faces)
              or any(o is not b and _overlap(bb, ob, TOUCH) for o, ob in clip_boxes)):
            out.append((b, "stairs_edges"))
        else:
            out.append((b, "stray"))
    return out


def remove_stray_clips(sc: scene.Scene) -> int:
    """Drop player clips in the playable band that touch nothing visible and no other clip: in the
    port they are invisible walls in the open. Clips on stairs, ledges, walls and props stay."""
    stray = {id(b) for b, cat in classify_clips(sc) if cat == "stray"}
    if stray:
        sc.brushes = [b for b in sc.brushes if id(b) not in stray]
        sc.bump("stray_clips_removed", len(stray))
    return len(stray)


def audit(sc: scene.Scene) -> Dict:
    band = _band(sc)
    vis_noc: Dict[tuple, int] = {}
    for b in sc.brushes:
        if b.source != "prop" or b.kind != scene.NONSOLID or not b.faces:
            continue
        lo, hi = b.bounds()
        if hi[2] >= band[0] and lo[2] <= band[1]:
            key = (_model(b), getattr(b, "tag", "") or "unported")
            vis_noc[key] = vis_noc.get(key, 0) + 1
    clips = {"boundary": 0, "stairs_edges": 0, "prop": 0, "stray": 0}
    stray: List[Dict] = []
    for b, cat in classify_clips(sc):
        clips[cat] += 1
        if cat == "stray":
            lo, hi = b.bounds()
            stray.append({"center": [round((lo[k] + hi[k]) / 2) for k in range(3)],
                          "size": [round(hi[k] - lo[k]) for k in range(3)]})
    rows = [{"model": m, "reason": r, "count": n} for (m, r), n in sorted(vis_noc.items())]
    return {
        "visible_no_collision": rows,
        "visible_no_collision_unexpected": sum(r["count"] for r in rows if r["reason"] == "unported"),
        "missing_models": dict(sorted(getattr(sc, "missing_models", {}).items())),
        "invisible_collision": clips,
        "stray_clips": stray[:20],
    }
