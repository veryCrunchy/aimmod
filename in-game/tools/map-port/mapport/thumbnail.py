"""Workshop thumbnails for ported maps.

1. Pick 1-3 camera views (a high vantage looking across the main area, a spawn view, an opposite
   vantage) and write them to <map>.thumb-views.json in KovaaK's world coordinates, ready for
   AimModCore's `capture-thumbnail` game command.
2. Until a real capture exists, software-render the first view (shaded faces in the materials'
   colours, sun light, sky gradient and distance haze).
3. Composite the AimMod template on top: logo, map name, source-game chip and movement variant.
   Outputs <map>.workshop-thumb.png/.jpg (1024 x 1024) and <map>.workshop-thumb-16x9.png/.jpg
   (1920 x 1080), each under 1 MB.

The branding step needs Pillow (`pip install pillow`); everything else is standard library.
`python -m mapport.thumbnail <port-dir> --capture shot.png` re-composites from a captured screenshot.
"""
from __future__ import annotations

import argparse
import io
import json
import math
import os
import sys
from typing import Dict, List, Optional, Sequence, Tuple

from . import geometry as g
from . import scene
from .materials import Slot
from .views import _clip_near

ASSET_LOGO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..",
                                           "ue4ss", "AimModNativeUI", "Assets", "aimmod-horizontal.png"))
MINT = (0, 245, 160)
TEAL = (56, 129, 132)
DEEP = (6, 6, 14)
SUB = (179, 179, 204)
SKY_TOP, SKY_HORIZON = (118, 160, 196), (214, 226, 232)
MAX_BYTES = 1_000_000
EYE = 64.0

View = Dict[str, float]  # Source-space camera: x, y, z (eye), yaw, pitch (degrees, +up), fov


# ---------------------------------------------------------------------------------------------
# Camera selection

def _camera_basis(yaw: float, pitch: float):
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    fwd = (cp * cy, cp * sy, sp)
    right = (sy, -cy, 0.0)
    up = g.cross(right, fwd)
    return fwd, right, up


def _look(eye, target, fov: float = 90.0) -> View:
    d = g.sub(target, eye)
    yaw = math.degrees(math.atan2(d[1], d[0]))
    pitch = math.degrees(math.atan2(d[2], math.hypot(d[0], d[1])))
    pitch = max(-35.0, min(10.0, pitch))
    return {"x": eye[0], "y": eye[1], "z": eye[2], "yaw": yaw, "pitch": pitch, "fov": fov}


def choose_views(sc: scene.Scene, reached: Sequence[Tuple[float, float, float]], eye_height: float = EYE,
                 slots=None, tex_slot=None) -> List[View]:
    pts = list(reached) or [s.origin for s in sc.spawns]
    if not pts:
        return []
    cx = sum(p[0] for p in pts) / len(pts)
    cy = sum(p[1] for p in pts) / len(pts)
    zs = sorted(p[2] for p in pts)
    zmid = zs[len(zs) // 2]
    centre = (cx, cy, zmid + eye_height * 0.5)
    ext = max(max(p[0] for p in pts) - min(p[0] for p in pts), max(p[1] for p in pts) - min(p[1] for p in pts), 1.0)
    zlo, zhi = zs[0], zs[-1]

    def score(p):
        height = (p[2] - zlo) / max(1.0, zhi - zlo)
        edge = math.hypot(p[0] - cx, p[1] - cy) / ext
        return height + 0.8 * edge

    cands = sorted(pts[:: max(1, len(pts) // 3000)], key=score, reverse=True)[:14]
    best, best_open = None, -1.0
    for p in cands:
        eye = (p[0], p[1], p[2] + eye_height + 24.0)
        base = _look(eye, centre)
        for dyaw in (0.0, -35.0, 35.0):
            v = dict(base, yaw=base["yaw"] + dyaw, pitch=max(-18.0, base["pitch"]))
            openness = _openness(sc, v)
            if openness > best_open:
                best, best_open = v, openness
    views = [best] if best else []
    # spawn view: the central spawn of the first team, looking at the centre
    if sc.spawns:
        team = sorted({s.team for s in sc.spawns})[0]
        grp = [s for s in sc.spawns if s.team == team]
        mx = sum(s.origin[0] for s in grp) / len(grp)
        my = sum(s.origin[1] for s in grp) / len(grp)
        sp = min(grp, key=lambda s: (s.origin[0] - mx) ** 2 + (s.origin[1] - my) ** 2)
        eye = (sp.origin[0], sp.origin[1], sp.origin[2] + eye_height)
        views.append(_look(eye, centre))
    if best:
        far = max(cands, key=lambda p: (p[0] - best["x"]) ** 2 + (p[1] - best["y"]) ** 2)
        views.append(_look((far[0], far[1], far[2] + eye_height + 24.0), centre))
    return views[:3]


def _openness(sc: scene.Scene, v: View) -> float:
    """How much of the view sees far geometry: share of pixels deeper than 400 units, then median depth.
    Sky counts as nothing seen, so a view into the void or the sky does not win."""
    depth, _rgb = render(sc, [], {}, v, 64, 36, shade=False)
    vals = [d for row in depth for d in row]
    hit = [d for d in vals if d < 1e29]
    if len(hit) < len(vals) * 0.6:
        return 0.0
    far = sum(1 for d in hit if d > 400.0) / len(vals)
    med = sorted(min(d, 3000.0) for d in hit)[len(hit) // 2]
    return far + med / 3000.0


def to_game(v: View, map_scale: float) -> dict:
    """Source camera -> KovaaK's world (Unreal cm): (x, -y, z) * MapScale, yaw mirrored."""
    return {"x": round(v["x"] * map_scale, 1), "y": round(-v["y"] * map_scale, 1), "z": round(v["z"] * map_scale, 1),
            "pitch": round(v["pitch"], 2), "yaw": round(-v["yaw"], 2), "fov": round(v["fov"], 1)}


# ---------------------------------------------------------------------------------------------
# Software render

def _shade(col, normal, dist, sun, haze_dist):
    lambert = max(0.0, g.dot(normal, sun))
    sky = 0.5 + 0.5 * normal[2]
    k = 0.42 + 0.48 * lambert + 0.16 * sky
    fog = min(1.0, max(0.0, dist / haze_dist)) ** 1.5 * 0.55
    return tuple(int(min(255, (c * k) * (1 - fog) + SKY_HORIZON[i] * fog)) for i, c in enumerate(col))


def _face_colour(b: scene.Brush, f: scene.Face, slots, tex_slot):
    if b.source == "backdrop":
        return (176, 160, 132)
    if b.kind == scene.GLASS:
        return (150, 200, 225)
    sl = slots[tex_slot[f.texture]] if slots and f.texture in tex_slot else None
    if sl:
        return (int(sl.tint[0:2], 16), int(sl.tint[2:4], 16), int(sl.tint[4:6], 16))
    if f.reflectivity != (0.5, 0.5, 0.5):
        return tuple(int(255 * min(1.0, c ** (1 / 2.2))) for c in f.reflectivity)
    return (180, 176, 168)


def render(sc: scene.Scene, slots, tex_slot, v: View, w: int, h: int, shade: bool = True):
    """Returns (depth rows, rgb rows as bytearrays)."""
    eye = (v["x"], v["y"], v["z"])
    fwd, right, up = _camera_basis(v["yaw"], v["pitch"])
    f = (w / 2) / math.tan(math.radians(v["fov"] / 2))
    zbuf = [[1e30] * w for _ in range(h)]
    img = []
    for y in range(h):
        t = max(0.0, min(1.0, y / max(1, h - 1) - (v["pitch"] / 90.0) * 0.5))
        col = tuple(int(SKY_TOP[i] + (SKY_HORIZON[i] - SKY_TOP[i]) * t) for i in range(3))
        img.append(bytearray(bytes(col) * w))
    sun = g.normalize((0.35, 0.25, 0.9))
    haze = 6000.0
    for b in sc.brushes:
        if b.kind == scene.CLIP:
            continue
        lo, hi = b.bounds()
        for face in b.faces:
            if face.texture == "tools/toolsskybox":
                continue
            if face.texture.startswith("tools/") and not all(x.texture.startswith("tools/") for x in b.faces):
                continue
            if g.dot(face.normal, g.sub(face.polygon[0], eye)) >= 0:
                continue
            cam = []
            for p in face.polygon:
                d = g.sub(p, eye)
                cam.append((g.dot(d, right), g.dot(d, up), g.dot(d, fwd)))
            cam = _clip_near(cam)
            if len(cam) < 3:
                continue
            scr = [(w / 2 + f * x / z, h / 2 - f * y / z, z) for x, y, z in cam]
            if shade:
                dist = g.length(g.sub(g.centroid(face.polygon), eye))
                col = _shade(_face_colour(b, face, slots, tex_slot), face.normal, dist, sun, haze)
            else:
                col = (0, 0, 0)
            cb = bytes(col)
            for i in range(1, len(scr) - 1):
                _tri(scr[0], scr[i], scr[i + 1], cb, zbuf, img, w, h)
    return zbuf, img


def _tri(a, b, c, cb, zbuf, img, w, h):
    minx, maxx = max(0, int(min(a[0], b[0], c[0]))), min(w - 1, int(max(a[0], b[0], c[0])) + 1)
    miny, maxy = max(0, int(min(a[1], b[1], c[1]))), min(h - 1, int(max(a[1], b[1], c[1])) + 1)
    if minx > maxx or miny > maxy:
        return
    den = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
    if abs(den) < 1e-9:
        return
    ia, ib, ic = 1 / a[2], 1 / b[2], 1 / c[2]
    for y in range(miny, maxy + 1):
        zr, ir = zbuf[y], img[y]
        py = y + 0.5
        for x in range(minx, maxx + 1):
            px = x + 0.5
            l1 = ((b[1] - c[1]) * (px - c[0]) + (c[0] - b[0]) * (py - c[1])) / den
            l2 = ((c[1] - a[1]) * (px - c[0]) + (a[0] - c[0]) * (py - c[1])) / den
            l3 = 1 - l1 - l2
            if l1 < -1e-6 or l2 < -1e-6 or l3 < -1e-6:
                continue
            z = 1 / (l1 * ia + l2 * ib + l3 * ic)
            if z < zr[x]:
                zr[x] = z
                ir[x * 3:x * 3 + 3] = cb


def render_image(sc, slots, tex_slot, v: View, w: int, h: int):
    from PIL import Image
    _z, rows = render(sc, slots, tex_slot, v, w, h)
    return Image.frombytes("RGB", (w, h), b"".join(bytes(r) for r in rows))


# ---------------------------------------------------------------------------------------------
# Branding

def _font(size: int):
    from PIL import ImageFont
    return ImageFont.load_default(size=size)


def _fit(img, w: int, h: int):
    from PIL import Image
    sw, sh = img.size
    scale = max(w / sw, h / sh)
    img = img.resize((max(w, int(sw * scale + 0.5)), max(h, int(sh * scale + 0.5))), Image.LANCZOS)
    x0 = (img.size[0] - w) // 2
    y0 = (img.size[1] - h) // 2
    return img.crop((x0, y0, x0 + w, y0 + h))


def compose(base, display: str, game: str, variant: str, w: int, h: int):
    from PIL import Image, ImageDraw
    img = _fit(base.convert("RGB"), w, h).convert("RGBA")
    unit = min(w, h) / 1024.0
    over = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(over)
    band = int(h * (0.42 if w == h else 0.38))
    for i in range(band):  # bottom gradient band for readable text
        a = int(240 * min(1.0, (i / band) * 1.25) ** 1.2)
        d.line([(0, h - band + i), (w, h - band + i)], fill=DEEP + (a,))
    for i in range(int(110 * unit)):  # top fade behind the logo
        a = int(150 * (1 - i / (110 * unit)))
        d.line([(0, i), (w, i)], fill=DEEP + (a,))
    d.rectangle([0, h - int(8 * unit), w, h], fill=MINT + (255,))  # accent bar
    img = Image.alpha_composite(img, over)
    d = ImageDraw.Draw(img)
    pad = int(56 * unit)
    # logo
    if os.path.exists(ASSET_LOGO):
        logo = Image.open(ASSET_LOGO).convert("RGBA")
        lh = int(64 * unit)
        logo = logo.resize((int(logo.size[0] * lh / logo.size[1]), lh), Image.LANCZOS)
        img.alpha_composite(logo, (pad, int(34 * unit)))
    # map name (shrink to fit)
    size = int(118 * unit)
    while size > 40:
        fnt = _font(size)
        if d.textlength(display, font=fnt) <= w - 2 * pad:
            break
        size -= 4
    fnt = _font(size)
    chip_font = _font(int(40 * unit))
    var_font = _font(int(44 * unit))
    y_name = h - int(150 * unit) - size
    d.text((pad + int(3 * unit), y_name + int(4 * unit)), display, font=fnt, fill=DEEP + (180,))  # shadow
    d.text((pad, y_name), display, font=fnt, fill=(245, 248, 250), stroke_width=max(1, int(2 * unit)),
           stroke_fill=(245, 248, 250))  # stroke in the fill colour reads as a bold weight
    # chips: game tag (outlined) then the variant (filled mint)
    y_chip = h - int(118 * unit)
    x = pad
    for text, filled in ((game, False), (variant, True)):
        tw = d.textlength(text, font=chip_font if not filled else var_font)
        fh = int(58 * unit)
        box = [x, y_chip, x + tw + int(36 * unit), y_chip + fh]
        if filled:
            d.rounded_rectangle(box, radius=int(14 * unit), fill=MINT)
            d.text((x + int(18 * unit), y_chip + int(6 * unit)), text, font=var_font, fill=DEEP)
        else:
            d.rounded_rectangle(box, radius=int(14 * unit), outline=MINT, width=max(2, int(4 * unit)),
                                fill=DEEP + (200,))
            d.text((x + int(18 * unit), y_chip + int(8 * unit)), text, font=chip_font, fill=MINT)
        x = box[2] + int(18 * unit)
    return img.convert("RGB")


def _save_small(img, path_png: str, path_jpg: str) -> Tuple[int, int]:
    """PNG (quantised if needed) and JPEG, both under MAX_BYTES."""
    from PIL import Image
    buf = io.BytesIO()
    img.save(buf, "PNG", optimize=True)
    if buf.tell() > MAX_BYTES:
        buf = io.BytesIO()
        img.quantize(colors=256, method=Image.MEDIANCUT, dither=Image.FLOYDSTEINBERG).save(buf, "PNG", optimize=True)
    with open(path_png, "wb") as fh:
        fh.write(buf.getvalue())
    q = 90
    while True:
        jb = io.BytesIO()
        img.save(jb, "JPEG", quality=q, optimize=True, progressive=True)
        if jb.tell() <= MAX_BYTES or q <= 50:
            break
        q -= 8
    with open(path_jpg, "wb") as fh:
        fh.write(jb.getvalue())
    return os.path.getsize(path_png), os.path.getsize(path_jpg)


def write_thumbnails(out: str, base: str, image, display: str, game: str, variant: str) -> Dict[str, str]:
    files = {}
    for suffix, (w, h) in (("", (1024, 1024)), ("-16x9", (1920, 1080))):
        img = compose(image, display, game, variant, w, h)
        png = os.path.join(out, f"{base}.workshop-thumb{suffix}.png")
        jpg = os.path.join(out, f"{base}.workshop-thumb{suffix}.jpg")
        _save_small(img, png, jpg)
        files[f"workshop_thumb{suffix.replace('-', '_')}"] = os.path.relpath(png, out)
        files[f"workshop_thumb{suffix.replace('-', '_')}_jpg"] = os.path.relpath(jpg, out)
    # The same view without the template, for AimMod Hub's Discord invite cards (mapport.cardart).
    from . import cardart
    art = os.path.join(out, f"{base}.card-art.jpg")
    with open(art, "wb") as fh:
        fh.write(cardart.encode(image))
    files["card_art_jpg"] = os.path.relpath(art, out)
    return files


def views_document(scenario: str, base: str, views: List[View], map_scale: float) -> dict:
    game_views = [to_game(v, map_scale) for v in views]
    return {
        "format": "aimmod.thumb-views", "version": 1, "scenario": scenario,
        "space": "KovaaK's world (Unreal cm): x = source_x * map_scale, y = -source_y * map_scale, "
                 "z = source_z * map_scale; yaw = -source_yaw; pitch positive looks up",
        "map_scale": map_scale,
        "views": game_views,
        "request": {"action": "capture-thumbnail", "scenario": scenario, "width": 1920, "height": 1080,
                    "out": f"{base}.png", "views": game_views},
    }


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m mapport.thumbnail",
                                 description="Re-composite a port's Workshop thumbnails from a captured screenshot.")
    ap.add_argument("port_dir", help="a map-port output folder (with <map>.report.json)")
    ap.add_argument("--capture", required=True, help="in-game screenshot (PNG) to use as the image")
    args = ap.parse_args(argv)
    reports = [f for f in os.listdir(args.port_dir) if f.endswith(".report.json")]
    if not reports:
        print("no .report.json in that folder", file=sys.stderr)
        return 1
    with open(os.path.join(args.port_dir, reports[0]), encoding="utf-8") as fh:
        rep = json.load(fh)
    from PIL import Image
    files = write_thumbnails(args.port_dir, rep["name"], Image.open(args.capture), rep["display_name"],
                             rep["game"], rep["variant"])
    for v in files.values():
        print("wrote", v)
    return 0


if __name__ == "__main__":
    sys.exit(main())
