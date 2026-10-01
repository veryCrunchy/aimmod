"""Stable names for published ports. The scenario name is the KovaaK's leaderboard key: never change it.

Scenario (also the .sce file name and the Workshop title):  AimMod - <Map> (<Game>) - <Variant>
Map file:                                                   aimmod_<mapid>_<game>.json
Metadata:                                                   aimmod_<mapid>_<game>.aimmod.json
"""
from __future__ import annotations

import re

GAME_TAGS = ("CSGO", "CSS", "CS2", "GMod")
ILLEGAL = set('<>:"/\\|?*')

DISPLAY_NAMES = {
    "de_dust2": "Dust2", "de_dust": "Dust", "de_mirage": "Mirage", "de_inferno": "Inferno",
    "de_nuke": "Nuke", "de_overpass": "Overpass", "de_train": "Train", "de_vertigo": "Vertigo",
    "de_ancient": "Ancient", "de_anubis": "Anubis", "de_cache": "Cache", "de_cbble": "Cobblestone",
    "de_aztec": "Aztec", "cs_office": "Office", "cs_italy": "Italy", "cs_assault": "Assault",
    "cs_militia": "Militia", "de_tuscan": "Tuscan", "de_prodigy": "Prodigy",
}


class PortNameError(ValueError):
    pass


def check(name: str) -> str:
    bad = sorted({c for c in name if c in ILLEGAL or ord(c) < 32})
    if bad or not name.strip() or name != name.strip() or name.endswith("."):
        raise PortNameError(f"illegal name {name!r}: characters {bad}" if bad else f"illegal name {name!r}")
    return name


def map_id(name: str) -> str:
    return re.sub(r"[^a-z0-9_]+", "_", name.lower()).strip("_") or "map"


def display_name(mapid: str) -> str:
    """Familiar name for well-known maps; aim/other maps keep their original id."""
    return DISPLAY_NAMES.get(mapid.lower(), mapid)


def game_tag(tag: str) -> str:
    for t in GAME_TAGS:
        if tag.replace(":", "").replace(" ", "").lower() == t.lower():
            return t
    raise PortNameError(f"unknown game tag {tag!r}; use one of {', '.join(GAME_TAGS)}")


def guess_game(bsp_version: int) -> str:
    return "CSGO" if bsp_version >= 21 else "CSS"


def scenario_name(display: str, game: str, variant: str) -> str:
    return check(f"AimMod - {display} ({game_tag(game)}) - {variant}")


def file_id(mapid: str, game: str) -> str:
    return check(f"aimmod_{map_id(mapid)}_{game_tag(game).lower()}")
