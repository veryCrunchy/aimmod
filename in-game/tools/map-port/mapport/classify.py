"""Decide what a Source brush becomes in KovaaK's (shared by the BSP and VMF readers)."""
from __future__ import annotations

from typing import Iterable, Optional

from . import scene

# Tool textures whose brushes never exist in-game.
SKIP_TOOLS = (
    "tools/toolstrigger", "tools/toolshint", "tools/toolsskip", "tools/toolsareaportal",
    "tools/toolsoccluder", "tools/toolsfog", "tools/toolsblocklight", "tools/toolsnpcclip",
    "tools/toolsblock_los", "tools/toolsblocklos", "tools/toolsorigin", "tools/toolsdotted",
    "tools/toolsbuyzone", "tools/toolsbombsite", "tools/climb", "tools/toolsclimb",
    "tools/toolswater", "tools/toolsnavigation",
)
# Tool textures that still stop the player.
CLIP_TOOLS = (
    "tools/toolsplayerclip", "tools/toolsclip", "tools/toolsinvisible", "tools/toolsinvisibleladder",
    "tools/toolsskybox", "tools/toolsskybox2d", "tools/toolsnodraw", "tools/toolsgrenadeclip",
)
HARD_CLIP_TOOLS = tuple(t for t in CLIP_TOOLS if t != "tools/toolsnodraw")
WEAPON_CLIP_TOOLS = ("tools/toolsblockbullets",)
INVISIBLE_FACE = ("tools/", "skybox/")

# Brush entities that are not geometry.
SKIP_ENTITIES = (
    "trigger_", "func_buyzone", "func_bomb_target", "func_hostage_rescue", "func_areaportal",
    "func_occluder", "func_precipitation", "func_smokevolume", "func_dustmotes", "func_dustcloud",
    "func_nav_", "func_ladder", "func_water_analog", "func_viscluster", "func_clip_vphysics",
    "func_no_defuse", "func_cheapwater", "env_", "info_", "func_fish_pool",
)
NONSOLID_ENTITIES = ("func_illusionary",)
GLASS_ENTITIES = ("func_breakable_surf",)
GLASS_WORDS = ("glass", "window")

# Source brush contents flags.
CONTENTS_SOLID = 0x1
CONTENTS_WINDOW = 0x2
CONTENTS_GRATE = 0x8
CONTENTS_SLIME = 0x10
CONTENTS_WATER = 0x20
CONTENTS_PLAYERCLIP = 0x10000
CONTENTS_MONSTERCLIP = 0x20000
CONTENTS_DETAIL = 0x8000000


def is_tool(texture: str) -> bool:
    return texture.startswith(INVISIBLE_FACE) or "toolsnodraw" in texture


def classify(textures: Iterable[str], classname: str = "worldspawn",
             contents: Optional[int] = None) -> Optional[str]:
    """Return a scene brush kind, or None when the brush should be dropped."""
    tex = [t or "" for t in textures]
    cls = classname.lower()
    if cls.startswith(SKIP_ENTITIES):
        return None
    if tex and all(t.startswith(SKIP_TOOLS) for t in tex):
        return None
    if contents is not None:
        if contents & (CONTENTS_WATER | CONTENTS_SLIME) and not contents & CONTENTS_SOLID:
            return None
        if not contents & (CONTENTS_SOLID | CONTENTS_WINDOW | CONTENTS_GRATE | CONTENTS_PLAYERCLIP):
            return None
        if contents & CONTENTS_PLAYERCLIP and not contents & (CONTENTS_SOLID | CONTENTS_WINDOW | CONTENTS_GRATE):
            return scene.CLIP
    if any(t.startswith(WEAPON_CLIP_TOOLS) for t in tex) and all(is_tool(t) for t in tex):
        return scene.WEAPON_CLIP
    if tex and all(t.startswith(HARD_CLIP_TOOLS) or t.startswith(SKIP_TOOLS) for t in tex):
        return scene.CLIP
    if tex and all(is_tool(t) for t in tex):
        # Detail nodraw hulls sit behind models (props) we cannot port; show them as stand-in geometry.
        detail = cls.startswith("func_detail") or (contents is not None and contents & CONTENTS_DETAIL)
        if detail and any("toolsnodraw" in t for t in tex) and not any(t.startswith(HARD_CLIP_TOOLS) for t in tex):
            return scene.SOLID
        return scene.CLIP
    if cls.startswith(NONSOLID_ENTITIES):
        return scene.NONSOLID
    if cls.startswith(GLASS_ENTITIES):
        return scene.GLASS
    visible = [t for t in tex if not is_tool(t)]
    if contents is not None and contents & (CONTENTS_WINDOW | CONTENTS_GRATE) and not contents & CONTENTS_SOLID:
        return scene.GLASS
    if visible and all(any(w in t.rsplit("/", 1)[-1] for w in GLASS_WORDS) for t in visible):
        return scene.GLASS
    return scene.SOLID
