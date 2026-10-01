"""Map art for AimMod Hub's Discord invite cards (/og/invite.png, /og/maps/<key>.jpg).

    python -m mapport.cardart <port-dir> [<port-dir> ...] --out <dir>

For each map-port output folder (with <map>.report.json) this writes <dir>/<map>.jpg (1280 x 720,
JPEG, at most MAX_BYTES) and adds the map to <dir>/maps.json: {"key", "name", "game"} per map, the
pretty name and game label the card prints. Copy the folder's files into the Hub's
api/internal/http/og_maps/ to publish them.

The image is, in order of preference:
1. <map>.card-art.jpg: the unbranded render the port tool writes next to its Workshop thumbnails;
2. --capture <png>: an in-game screenshot (only with a single port folder);
3. <map>.workshop-thumb-16x9.jpg, cropped to the area between the AimMod logo and the map title
   that the Workshop template draws, so the card's own text never sits on top of it.

The renders are AimMod's own pictures of its ports; nothing from the source game is included. Needs
Pillow (`pip install pillow`).
"""
from __future__ import annotations

import argparse
import io
import json
import os
import re
import sys
from typing import Dict, List, Optional, Tuple

from . import naming

WIDTH, HEIGHT = 1280, 720
QUALITY = 80
MAX_BYTES = 200_000

# Game tag (naming.GAME_TAGS) -> label on the card.
GAME_LABELS = {"CSGO": "CS:GO", "CSS": "CS:S", "CS2": "CS2", "CS16": "CS 1.6", "GMod": "GMod", "Q3": "Quake 3", "QL": "Quake Live"}

# Familiar names for shipped ports whose port name keeps the original map id. Only for display: the
# scenario name (the leaderboard key, naming.scenario_name) never changes. Keep in step with
# PrettyNames in in-game/native-service/DiscordPresence.cs.
PRETTY_NAMES = {
    "de_d2_remake": "Dust2 Remake", "de_d2_beta": "Dust2 Beta", "de_dust2": "Dust2",
    "aim_redline": "Redline", "aim_map": "aim_map", "aim_ag_texture2": "AG Texture 2",
    "aim_deagle7k_2067": "Deagle 7k", "awp_lego": "AWP Lego", "fy_pool_day": "Pool Day",
}
KEY = re.compile(r"^aimmod_[a-z0-9_]{1,80}$")


def pretty_name(display: str) -> str:
    """The port's display name (naming.display_name) as the card prints it."""
    return PRETTY_NAMES.get(display.lower(), naming.display_name(display))


def game_label(tag: str) -> str:
    return GAME_LABELS.get(naming.game_tag(tag), tag)


def _branded_crop(img):
    """The middle of a Workshop 16:9 thumbnail: below the logo strip, above the title block."""
    w, h = img.size
    top, bottom = int(h * 0.11), int(h * 0.73)
    ch = bottom - top
    cw = min(w, int(ch * WIDTH / HEIGHT))
    x0 = (w - cw) // 2
    return img.crop((x0, top, x0 + cw, bottom))


def _fit(img):
    from PIL import Image
    sw, sh = img.size
    scale = max(WIDTH / sw, HEIGHT / sh)
    img = img.resize((max(WIDTH, round(sw * scale)), max(HEIGHT, round(sh * scale))), Image.LANCZOS)
    x0, y0 = (img.size[0] - WIDTH) // 2, (img.size[1] - HEIGHT) // 2
    return img.crop((x0, y0, x0 + WIDTH, y0 + HEIGHT))


def encode(img) -> bytes:
    """JPEG at QUALITY, lowered until it fits MAX_BYTES. Metadata is never written."""
    img = _fit(img.convert("RGB"))
    q = QUALITY
    while True:
        buf = io.BytesIO()
        img.save(buf, "JPEG", quality=q, optimize=True, progressive=True)
        if buf.tell() <= MAX_BYTES or q <= 40:
            return buf.getvalue()
        q -= 8


def source_image(port_dir: str, base: str, capture: Optional[str] = None) -> Tuple[object, str]:
    from PIL import Image
    unbranded = os.path.join(port_dir, f"{base}.card-art.jpg")
    if os.path.exists(unbranded):
        return Image.open(unbranded), "render"
    if capture:
        return Image.open(capture), "capture"
    branded = os.path.join(port_dir, f"{base}.workshop-thumb-16x9.jpg")
    if os.path.exists(branded):
        return _branded_crop(Image.open(branded).convert("RGB")), "workshop-thumb"
    raise FileNotFoundError(f"{port_dir}: no {base}.card-art.jpg or {base}.workshop-thumb-16x9.jpg")


def export(port_dirs: List[str], out: str, capture: Optional[str] = None) -> List[Dict[str, str]]:
    if capture and len(port_dirs) != 1:
        raise ValueError("--capture needs exactly one port folder")
    os.makedirs(out, exist_ok=True)
    manifest_path = os.path.join(out, "maps.json")
    maps: Dict[str, Dict[str, str]] = {}
    if os.path.exists(manifest_path):
        with open(manifest_path, encoding="utf-8") as fh:
            maps = {m["key"]: m for m in json.load(fh)}
    done = []
    for port_dir in port_dirs:
        reports = sorted(f for f in os.listdir(port_dir) if f.endswith(".report.json"))
        if not reports:
            raise FileNotFoundError(f"{port_dir}: no .report.json")
        with open(os.path.join(port_dir, reports[0]), encoding="utf-8") as fh:
            rep = json.load(fh)
        base = rep["name"]
        if not KEY.match(base):
            raise ValueError(f"unexpected map key {base!r}")
        img, source = source_image(port_dir, base, capture)
        data = encode(img)
        with open(os.path.join(out, base + ".jpg"), "wb") as fh:
            fh.write(data)
        entry = {"key": base, "name": pretty_name(rep["display_name"]), "game": game_label(rep["game"])}
        maps[base] = entry
        done.append(dict(entry, source=source, bytes=str(len(data))))
    with open(manifest_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(sorted(maps.values(), key=lambda m: m["key"]), fh, indent=1, ensure_ascii=False)
        fh.write("\n")
    return done


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m mapport.cardart", description="Export map art for AimMod Hub's Discord invite cards.")
    ap.add_argument("port_dirs", nargs="+", help="map-port output folders (each with <map>.report.json)")
    ap.add_argument("--out", required=True, help="folder for <map>.jpg and maps.json")
    ap.add_argument("--capture", help="in-game screenshot to use instead (one port folder only)")
    args = ap.parse_args(argv)
    for entry in export(args.port_dirs, args.out, args.capture):
        print(f"{entry['key']}.jpg  {entry['name']} ({entry['game']})  {entry['bytes']} bytes from {entry['source']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
