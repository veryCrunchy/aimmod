"""Scenario tags and description for a port: SearchTags, AimTypeTag, AimSubTypeTag, DifficultyTag.

KovaaK's 3.9.11 stores SearchTags as one comma-separated string. Its scenario editor offers the aim
types "Clicking", "Tracking" and "Target Switching" (subtypes Static/Dynamic, Smoothness/Reactivity,
Slow/Fast/Both, or Other) and limits the description to 352 characters. The game sets no tag limit,
so ports keep to a conservative one: at most 16 tags of up to 32 characters, 255 characters in all.
"""
from __future__ import annotations

import re
from typing import Iterable, List, Optional, Sequence

MAX_TAGS = 16
MAX_TAG_LENGTH = 32
MAX_TAGS_LENGTH = 255
MAX_DESCRIPTION = 352

AIM_TYPE, AIM_SUBTYPE = "Clicking", "Dynamic"  # moving target bots, one hitscan click per shot

# Game tag -> (full name, family). The scenario name keeps the short tag (naming.GAME_TAGS).
GAMES = {
    "CSS": ("Counter-Strike: Source", "Counter-Strike"),
    "CSGO": ("Counter-Strike: Global Offensive", "Counter-Strike"),
    "CS2": ("Counter-Strike 2", "Counter-Strike"),
    "CS16": ("Counter-Strike 1.6", "Counter-Strike"),
    "GMod": ("Garry's Mod", "Source"),
    "Q3": ("Quake 3", "Quake"),
    "QL": ("Quake Live", "Quake"),
}

# Map-name prefixes and words -> map type (checked in order; the first prefix match wins).
PREFIXES = (("aim_", "Aim map"), ("fy_", "Fight yard"), ("awp_", "AWP"), ("de_", "Defuse"), ("cs_", "Hostage"),
            ("dm_", "Deathmatch"), ("ka_", "Knife arena"), ("surf_", "Surf"), ("bhop_", "Bunny hop"),
            ("kz_", "Climb"), ("gg_", "Gun game"), ("ctf", "Capture the flag"))
TOURNEY = re.compile(r"tourney|duel")
DEATHMATCH = re.compile(r"(^|[^a-z])dm\d|^dm|deathmatch")
# Words in the map name that imply a weapon.
WEAPONS = (("deagle", "Deagle"), ("awp", "AWP"), ("scout", "Scout"), ("ak47", "AK-47"), ("m4a1", "M4A1"),
           ("usp", "USP"), ("glock", "Glock"), ("rail", "Railgun"))
DUEL_MAPS = {"hub3aeroq3", "pro_q3tourney7", "pro_q3tourney2", "q3tourney2", "ztn3tourney1"}


def game_names(game: str) -> tuple:
    return GAMES.get(game, (game, game))


OBJECTIVE_CLASSES = (
    (("func_bomb_target", "info_bomb_target"), "Defuse"),
    (("func_hostage_rescue", "info_hostage_spawn", "hostage_entity"), "Hostage"),
    (("info_ctf_flag", "item_teamflag", "ctf_flag", "func_ctf_capture"), "Capture the flag"),
)


def map_types(mapid: str, classnames: Iterable[str] = ()) -> List[str]:
    """Map type from the map id (prefix, then Quake naming) and objective entities (bomb sites, ...)."""
    m = mapid.lower()
    out = [t for p, t in PREFIXES if m.startswith(p)][:1]
    if TOURNEY.search(m) or m in DUEL_MAPS:
        out.append("Duel")
    elif DEATHMATCH.search(m):
        out.append("Deathmatch")
    present = {c.lower() for c in classnames}
    out += [t for names, t in OBJECTIVE_CLASSES if present.intersection(names)]
    return out


def weapon_hints(mapid: str) -> List[str]:
    m = mapid.lower()
    return [t for w, t in WEAPONS if w in m]


def movement_tags(model: str, clamp_air_speed: bool, variant: str) -> List[str]:
    if model == "quake":
        out = ["Quake movement"]
    elif variant.lower().startswith("sprint"):
        out = ["Sprint movement"]
    else:
        out = ["CS movement"]
    if not clamp_air_speed:
        out += ["Strafe jumping", "Bunny hop"]  # air speed is not capped at the input speed
    return out


def feature_tags(gameobjects: Sequence[dict]) -> List[str]:
    out = []
    kinds = [(g.get("kind"), g.get("liquid"), str(g.get("name", ""))) for g in gameobjects]
    if any(k == "water" for k, _, _ in kinds):
        out.append("Water")
    if any(k == "hurt" and liquid == "lava" for k, liquid, _ in kinds):
        out.append("Lava")
    if any(k == "hurt" and liquid == "slime" for k, liquid, _ in kinds):
        out.append("Slime")
    if any(k == "jumppad" and not name.startswith("ladder") for k, _, name in kinds):
        out.append("Jump pads")
    if any(k == "teleporter" for k, _, _ in kinds):
        out.append("Teleporters")
    if any(k == "jumppad" and name.startswith("ladder") for k, _, name in kinds):
        out.append("Ladders")
    return out


def clean(tags: Iterable[str]) -> List[str]:
    """Dedupe (case-insensitive), drop separators and over-long tags, and keep within the limits."""
    out: List[str] = []
    seen = set()
    total = 0
    for t in tags:
        t = re.sub(r"\s+", " ", t.replace(",", " ")).strip()
        if not t or len(t) > MAX_TAG_LENGTH or t.lower() in seen:
            continue
        extra = len(t) + (2 if out else 0)
        if len(out) >= MAX_TAGS or total + extra > MAX_TAGS_LENGTH:
            break
        out.append(t)
        seen.add(t.lower())
        total += extra
    return out


def search_tags(mapid: str, game: str, model: str, clamp_air_speed: bool, variant: str,
                gameobjects: Sequence[dict] = (), classnames: Iterable[str] = ()) -> List[str]:
    full, family = game_names(game)
    tags = ["AimMod", "Map port", full, family]
    tags += movement_tags(model, clamp_air_speed, variant)
    tags += map_types(mapid, classnames)
    tags += weapon_hints(mapid)
    tags += feature_tags(gameobjects)
    return clean(tags)


def difficulty(model: str, clamp_air_speed: bool) -> int:
    """1-5: CS movement 2, strafe-jumping Quake movement 3."""
    return 3 if model == "quake" or not clamp_air_speed else 2


def description(display: str, game: str, movement_label: str, shift: str, features: Sequence[str] = (),
                source: Optional[str] = None) -> str:
    full, _ = game_names(game)
    text = f"{display} from {full}, ported to KovaaK's by AimMod. Movement: {movement_label}. "
    text += f"Shift: {shift}, Ctrl: crouch."
    if features:
        text += " Has " + ", ".join(f.lower() for f in features) + "."
    if "Water" in features:
        text += " Swimming needs AimMod."
    if source:
        text += f" Source file: {source}."
    if len(text) > MAX_DESCRIPTION:
        text = text[:MAX_DESCRIPTION - 3].rstrip() + "..."
    return text
