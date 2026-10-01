"""Builds a Steam Workshop publish bundle for an AimMod map port.

A bundle is a folder the user reviews and publishes; nothing here talks to Steam:

    <out>/<scenario name>/
        content/<scenario>.sce      the Workshop item content (KovaaK's items hold one .sce;
                                    the map and the Shift ability are embedded in it)
        extras/maps/<map>.json      map-creator map and ability, for manual installs
        extras/Abilities/*.abilsprint
        preview.png                 Workshop preview, at most 1024 px and under 1 MB
        description.txt             Steam BBCode description with credits and attribution
        item.json                   title, tags, visibility, change note, checks
        workshop_item.vdf           optional steamcmd workshop_build_item input
        PUBLISH.txt                 how to publish (in-game upload recommended)
"""
from __future__ import annotations

import json
import os
import re
import shutil
from dataclasses import dataclass, field
from typing import Dict, List, Optional

from . import png

APP_ID = 824270
TITLE_MAX = 128          # k_cchPublishedDocumentTitleMax
DESCRIPTION_MAX = 8000   # k_cchPublishedDocumentDescriptionMax
CHANGE_NOTE_MAX = 8000   # k_cchPublishedDocumentChangeDescriptionMax
TAG_MAX = 255            # k_cchTagListMax per tag
PREVIEW_MAX_BYTES = 1024 * 1024
VISIBILITY = {"public": 0, "friends": 1, "private": 2, "unlisted": 3}
GAMES = {"CSGO": ("CS:GO", "Counter-Strike: Global Offensive"), "CSS": ("CS:S", "Counter-Strike: Source"),
         "CS2": ("CS2", "Counter-Strike 2"), "GMod": ("GMod", "Garry's Mod")}
NAME = re.compile(r"^AimMod - (?P<map>.+) \((?P<game>CSGO|CSS|CS2|GMod)\) - (?P<variant>.+)$")


class BundleError(ValueError):
    pass


@dataclass
class Source:
    author: str = ""
    url: str = ""
    license: str = ""
    credits: List[str] = field(default_factory=list)


def read_sce(path: str) -> Dict[str, str]:
    """Top-level key=value fields of a scenario (up to the first [section])."""
    fields: Dict[str, str] = {}
    with open(path, encoding="utf-8-sig", errors="replace") as fh:
        for line in fh:
            line = line.rstrip("\r\n")
            if line.startswith("["):
                break
            key, sep, value = line.partition("=")
            if sep and key and key not in fields:
                fields[key] = value
    return fields


def _split(value: str) -> List[str]:
    return [t.strip() for t in value.split(",") if t.strip()]


def tags_for(sce: Dict[str, str], game: str) -> List[str]:
    """Suggested tags from the scenario's own tag fields. The in-game uploader may set its own."""
    tags: List[str] = []
    for value in [sce.get("AimTypeTag", ""), sce.get("AimSubTypeTag", ""), *_split(sce.get("SearchTags", "")),
                  "AimMod", "Map Port", GAMES.get(game, (game,))[0]]:
        value = value.strip()
        if value and len(value) <= TAG_MAX and value.lower() not in {t.lower() for t in tags}:
            tags.append(value)
    return tags[:10]


def description_for(title: str, map_id: str, game: str, variant: str, source: Source, report: dict) -> str:
    short, full = GAMES.get(game, (game, game))
    movement = "Counter-Strike movement (Shift walks, Ctrl crouches)" if "CS" in variant else variant
    lines = [
        f"[h1]{title}[/h1]",
        f"A KovaaK's port of [b]{map_id}[/b] from {full} with {movement}.",
        "",
        "[h2]Source map[/h2]",
        "[list]",
        f"[*]Map: {map_id} ({short})",
        f"[*]Original author(s): {source.author or 'TODO: name the original map authors'}",
    ]
    if source.url:
        lines.append(f"[*]Original release: [url={source.url}]{source.url}[/url]")
    lines += [
        f"[*]Permission / license: {source.license or 'TODO: confirm the authors allow ports'}",
        "[/list]",
        "",
        "[h2]Credits[/h2]",
        "[list]",
        *[f"[*]{c}" for c in source.credits],
        "[*]Converted with AimMod map-port.",
        "[/list]",
        "",
        "[h2]About this port[/h2]",
        "Brushes are rebuilt as KovaaK's map-creator geometry and packed models as simplified hulls; "
        "lighting, decals, water and ladders are not converted.",
    ]
    bots = report.get("spawns") or {}
    if bots:
        lines.append(f"Spawns: {bots.get('T', 0)} T, {bots.get('CT', 0)} CT.")
    lines += ["", f"The scenario name ({title}) is its leaderboard key and never changes."]
    return "\n".join(lines) + "\n"


def _vdf_escape(text: str) -> str:
    return text.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")


def _preview(source: str, target: str) -> None:
    with open(source, "rb") as fh:
        image = png.read(fh.read())
    for limit in (1024, 768, 512, 384, 256):
        data = png.write(png.fit(image, limit))
        if len(data) < PREVIEW_MAX_BYTES:
            with open(target, "wb") as fh:
                fh.write(data)
            return
    raise BundleError("the preview image cannot be made smaller than 1 MB")


def prepare(port_dir: str, out_dir: str, scenario: Optional[str] = None, source: Optional[Source] = None,
            preview: Optional[str] = None, published_file_id: Optional[str] = None, change_note: str = "",
            visibility: str = "private", include_map_files: bool = False) -> List[str]:
    """Prepares one bundle per map-port report in `port_dir` (or just `scenario`). Returns bundle folders."""
    source = source or Source()
    if visibility not in VISIBILITY:
        raise BundleError(f"visibility must be one of {', '.join(VISIBILITY)}")
    if published_file_id is not None and not re.fullmatch(r"[1-9][0-9]{0,19}", published_file_id):
        raise BundleError("the Workshop item id is a number")
    if len(change_note.encode("utf-8")) > CHANGE_NOTE_MAX:
        raise BundleError("the change note is too long")
    reports = sorted(f for f in os.listdir(port_dir) if f.endswith(".report.json"))
    if not reports:
        raise BundleError(f"no map-port reports (*.report.json) in {port_dir}")
    made: List[str] = []
    for name in reports:
        with open(os.path.join(port_dir, name), encoding="utf-8") as fh:
            report = json.load(fh)
        title = report.get("scenario", "")
        if scenario and title != scenario:
            continue
        files = report.get("files", {})
        if "scenario" not in files:
            raise BundleError(f"{name}: the port has no scenario (run map-port without --no-scenario)")
        match = NAME.match(title)
        if not match:
            raise BundleError(f"{title!r} is not an AimMod port name (AimMod - <Map> (<Game>) - <Variant>)")
        if len(title.encode("utf-8")) > TITLE_MAX:
            raise BundleError(f"{title!r} is longer than the Workshop title limit")
        sce_path = os.path.join(port_dir, files["scenario"])
        sce = read_sce(sce_path)
        if sce.get("Name") != title or os.path.splitext(os.path.basename(sce_path))[0] != title:
            raise BundleError(f"{sce_path}: the scenario name, file name and report disagree; the name is the leaderboard key")
        with open(sce_path, encoding="utf-8", errors="replace") as fh:
            embedded = "[Map Data]" in fh.read()
        if not embedded:
            raise BundleError(f"{sce_path}: the map is not embedded; Workshop downloads only receive the .sce")

        bundle = os.path.join(out_dir, title)
        if os.path.exists(bundle):
            shutil.rmtree(bundle)
        os.makedirs(os.path.join(bundle, "content"))
        shutil.copy2(sce_path, os.path.join(bundle, "content", os.path.basename(sce_path)))
        for key, folder in (("map_json", "maps"), ("ability", "Abilities")):
            if key in files:
                target = os.path.join(bundle, "content" if include_map_files else "extras", folder)
                os.makedirs(target, exist_ok=True)
                shutil.copy2(os.path.join(port_dir, files[key]), target)

        todo: List[str] = []
        preview_source = preview or (os.path.join(port_dir, files["preview"]) if "preview" in files else None)
        if preview_source and os.path.exists(preview_source):
            _preview(preview_source, os.path.join(bundle, "preview.png"))
            if not preview:
                todo.append("Consider an in-game screenshot as the preview; preview.png is the converter's top-down check.")
        else:
            todo.append("Add preview.png (an in-game screenshot, under 1 MB).")
        if not source.author:
            todo.append("Name the original map authors (--source-author).")
        if not source.license:
            todo.append("Confirm the authors allow ports and note it (--source-license).")

        map_id = report.get("input", match["map"]).rsplit(".", 1)[0]
        description = description_for(title, map_id, match["game"], match["variant"], source, report)
        if len(description.encode("utf-8")) > DESCRIPTION_MAX:
            raise BundleError("the description is longer than the Workshop limit")
        with open(os.path.join(bundle, "description.txt"), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(description)
        tags = tags_for(sce, match["game"])
        item = {
            "appId": APP_ID,
            "publishedFileId": published_file_id,
            "title": title,
            "descriptionFile": "description.txt",
            "previewFile": "preview.png" if os.path.exists(os.path.join(bundle, "preview.png")) else None,
            "contentFolder": "content",
            "tags": tags,
            "visibility": visibility,
            "changeNote": change_note or ("First release." if published_file_id is None else ""),
            "language": "english",
            "metadata": {"kind": "aimmod.map-port", "scenario": title, "mapFile": os.path.basename(files.get("map_json", "")),
                         "game": match["game"], "source": {"map": map_id, "author": source.author, "url": source.url, "license": source.license}},
            "checks": {"ready": not [t for t in todo if not t.startswith("Consider")], "todo": todo},
        }
        with open(os.path.join(bundle, "item.json"), "w", encoding="utf-8", newline="\n") as fh:
            json.dump(item, fh, indent=2)
            fh.write("\n")
        vdf = [
            '"workshopitem"', "{",
            f'\t"appid"\t\t"{APP_ID}"',
            f'\t"publishedfileid"\t\t"{published_file_id or 0}"',
            f'\t"contentfolder"\t\t"{_vdf_escape(os.path.abspath(os.path.join(bundle, "content")))}"',
        ]
        if item["previewFile"]:
            vdf.append(f'\t"previewfile"\t\t"{_vdf_escape(os.path.abspath(os.path.join(bundle, "preview.png")))}"')
        vdf += [f'\t"visibility"\t\t"{VISIBILITY[visibility]}"', f'\t"title"\t\t"{_vdf_escape(title)}"',
                f'\t"description"\t\t"{_vdf_escape(description)}"', f'\t"changenote"\t\t"{_vdf_escape(item["changeNote"])}"', "}"]
        with open(os.path.join(bundle, "workshop_item.vdf"), "w", encoding="utf-8", newline="\n") as fh:
            fh.write("\n".join(vdf) + "\n")
        with open(os.path.join(bundle, "PUBLISH.txt"), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(_instructions(title, os.path.basename(sce_path), item, todo))
        made.append(bundle)
    if scenario and not made:
        raise BundleError(f"no port named {scenario!r} in {port_dir}")
    return made


def _instructions(title: str, sce: str, item: dict, todo: List[str]) -> str:
    update = item["publishedFileId"] is not None
    lines = [f"Publishing {title}", "=" * (11 + len(title)), ""]
    if todo:
        lines += ["Before publishing:"] + [f"  - {t}" for t in todo] + [""]
    lines += [
        "Recommended: KovaaK's own uploader",
        f"  1. Copy content\\{sce} to FPSAimTrainer\\Saved\\SaveGames\\Scenarios.",
        "  2. Start KovaaK's, find the scenario in the scenario browser and play it once.",
        "  3. Use the Workshop prompt in the scenario details (\"... is not on the workshop. Click here",
        "     to upload it.\"; for an update: \"Click to upload your local version\").",
        "  4. On the item's Steam page, paste description.txt, set preview.png, and check the tags:",
        "     " + ", ".join(item["tags"]),
        f"  5. Keep it {item['visibility']} until you have downloaded it on a second account and played it.",
        "",
        "Alternative: steamcmd (untested with KovaaK's; it does not set tags)",
        "  steamcmd +login <your account> +workshop_build_item <full path to workshop_item.vdf> +quit",
        "  steamcmd writes the new item id back into the .vdf. Keep that id for updates" + (" (this is an update)." if update else "."),
        "",
        "Never rename the scenario: its name is the leaderboard key.",
    ]
    return "\n".join(lines) + "\n"
