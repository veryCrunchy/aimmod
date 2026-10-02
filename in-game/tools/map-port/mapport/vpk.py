"""Stock Source models from the user's own game installs (VPK archives), read at port time.

A map's static props mostly use the game's stock models, which the BSP does not pack. When a Source
game with those models is installed (Half-Life 2 content ships with Portal, Half-Life 2, Garry's
Mod, Counter-Strike: Source, ...), map-port reads the model files (.mdl, .vvd, .dx90.vtx, .phy)
from its VPKs so oil drums, crates, cars and containers come back as hulls with collision instead
of going missing. Nothing is copied: the port contains only hull geometry built from them.

Counter-Strike 2 still carries most CS:S / CS:GO prop models under their old names as compiled
Source 2 models (`.vmdl_c`). Their physics hulls are read from CS2's `game/<content>/pak01_dir.vpk`
for models no Source 1 game provides (see `source2.py`): first by the same path, else by a unique
file name outside the remade `hr_` folders.

Directory VPKs (v1 and v2) are indexed by path; file data is read from the numbered archives on
demand.
"""
from __future__ import annotations

import os
import re
import struct
from typing import Dict, Iterable, List, Optional, Tuple

# Content folders that carry Source 1 models, most specific first (CS:S before HL2 base content).
CONTENT_DIRS = ("cstrike", "csgo", "garrysmod", "hl2mp", "episodic", "ep2", "hl2", "portal", "tf")
MODEL_EXTS = (".mdl", ".vvd", ".dx90.vtx", ".vtx", ".phy")


class VpkError(ValueError):
    pass


class Vpk:
    def __init__(self, dir_path: str):
        self.path = dir_path
        self.base = dir_path[:-len("_dir.vpk")]
        with open(dir_path, "rb") as fh:
            head = fh.read(28)
            sig, ver, tree = struct.unpack_from("<III", head, 0)
            if sig != 0x55AA1234 or ver not in (1, 2):
                raise VpkError(f"not a VPK directory: {os.path.basename(dir_path)}")
            off = 12 if ver == 1 else 28
            fh.seek(off)
            data = fh.read(tree)
        self.data_offset = off + tree
        self.entries: Dict[str, Tuple[int, int, int, bytes]] = {}
        pos = 0

        def cstr() -> str:
            nonlocal pos
            end = data.index(b"\0", pos)
            s = data[pos:end].decode("latin-1")
            pos = end + 1
            return s
        while pos < len(data):
            ext = cstr()
            if not ext:
                break
            while True:
                folder = cstr()
                if not folder:
                    break
                while True:
                    name = cstr()
                    if not name:
                        break
                    _crc, pre, arch, aoff, alen, _term = struct.unpack_from("<IHHIIH", data, pos)
                    pos += 18
                    preload = data[pos:pos + pre]
                    pos += pre
                    path = (folder + "/" if folder.strip() else "") + name + "." + ext
                    self.entries[path.lower()] = (arch, aoff, alen, preload)

    def read(self, path: str) -> Optional[bytes]:
        e = self.entries.get(path.lower())
        if e is None:
            return None
        arch, aoff, alen, preload = e
        if alen == 0:
            return preload
        if arch == 0x7FFF:
            src, off = self.path, self.data_offset + aoff
        else:
            src, off = f"{self.base}_{arch:03d}.vpk", aoff
        try:
            with open(src, "rb") as fh:
                fh.seek(off)
                return preload + fh.read(alen)
        except OSError:
            return None


def steam_libraries() -> List[str]:
    """Steam library folders from the environment (the default install and libraryfolders.vdf)."""
    roots = []
    for var in ("PROGRAMFILES(X86)", "PROGRAMFILES"):
        base = os.environ.get(var)
        if base:
            roots.append(os.path.join(base, "Steam"))
    home = os.path.expanduser("~")
    roots += [os.path.join(home, ".steam", "steam"), os.path.join(home, ".local", "share", "Steam")]
    libs: List[str] = []
    for r in roots:
        vdf = os.path.join(r, "steamapps", "libraryfolders.vdf")
        if not os.path.isfile(vdf):
            continue
        libs.append(r)
        try:
            with open(vdf, encoding="utf-8", errors="replace") as fh:
                for m in re.finditer(r'"path"\s+"([^"]+)"', fh.read()):
                    libs.append(m.group(1).replace("\\\\", "\\"))
        except OSError:
            pass
    seen, out = set(), []
    for lib in libs:
        key = os.path.normcase(os.path.abspath(lib))
        if key not in seen and os.path.isdir(os.path.join(lib, "steamapps", "common")):
            seen.add(key)
            out.append(lib)
    return out


def find_content(roots: Iterable[str]) -> List[str]:
    """*_dir.vpk files of Source content folders under game install roots or library folders.
    Source 2 games keep theirs in game/<content>/ (CS2: game/csgo/pak01_dir.vpk); those come last."""
    found: List[Tuple[int, str]] = []
    for root in roots:
        common = os.path.join(root, "steamapps", "common")
        games = [os.path.join(common, d) for d in sorted(os.listdir(common))] if os.path.isdir(common) else [root]
        for game in games:
            for rank, sub in enumerate(CONTENT_DIRS):
                for folder, r in ((os.path.join(game, sub), rank), (os.path.join(game, "game", sub), 100 + rank)):
                    if not os.path.isdir(folder):
                        continue
                    for f in sorted(os.listdir(folder)):
                        if f.endswith("_dir.vpk") and "sound" not in f and "texture" not in f and "shader" not in f:
                            found.append((r, os.path.join(folder, f)))
    return [p for _r, p in sorted(found)]


class StockModels:
    """Model files by path from installed VPKs (first archive that has the file wins)."""

    def __init__(self, vpk_paths: Iterable[str]):
        self.vpks: List[Vpk] = []
        self.source2: List[Vpk] = []  # compiled Source 2 models (.vmdl_c)
        self.s2_names: Dict[str, List[str]] = {}  # file name -> paths, for models CS2 moved
        for p in vpk_paths:
            try:
                v = Vpk(p)
            except (OSError, VpkError, struct.error, ValueError):
                continue
            if any(k.endswith(".mdl") for k in v.entries):
                self.vpks.append(v)
            elif any(k.endswith(".vmdl_c") for k in v.entries):
                self.source2.append(v)
                for k in v.entries:
                    if k.endswith(".vmdl_c"):
                        self.s2_names.setdefault(k.rsplit("/", 1)[-1], []).append(k)
        self.used: Dict[str, str] = {}  # model -> content label, for the report
        self._shapes: Dict[str, object] = {}

    @classmethod
    def discover(cls, spec: str) -> Optional["StockModels"]:
        """spec: "auto" (Steam libraries), "none", or a folder (a game install or content folder)."""
        if not spec or spec == "none":
            return None
        if spec == "auto":
            paths = find_content(steam_libraries())
        elif spec.endswith("_dir.vpk"):
            paths = [spec]
        else:
            paths = find_content([spec]) or [os.path.join(spec, f) for f in sorted(os.listdir(spec))
                                              if f.endswith("_dir.vpk")] if os.path.isdir(spec) else []
        sm = cls(paths)
        return sm if sm.vpks or sm.source2 else None

    def label(self, v: Vpk) -> str:
        parts = os.path.normpath(v.path).split(os.sep)
        if len(parts) >= 4 and parts[-3].lower() == "game":
            return parts[-4] + "/" + parts[-2]  # Source 2 layout: <game>/game/<content>
        return "/".join(parts[-3:-1])  # "<game>/<content>", never a full path

    def source2_path(self, model: str) -> Optional[str]:
        """The .vmdl_c that stands for a Source 1 model path: the same path, else the only model
        with that file name outside the remade hr_ folders (CS2 moved some, e.g. into window/)."""
        want = model.lower()[:-4] + ".vmdl_c" if model.lower().endswith(".mdl") else model.lower() + ".vmdl_c"
        for v in self.source2:
            if want in v.entries:
                return want
        cands = [p for p in self.s2_names.get(want.rsplit("/", 1)[-1], []) if "/hr_" not in p]
        return cands[0] if len(cands) == 1 else None

    def source2_shapes(self, model: str):
        """Collision shapes (source2.Shapes) of a model from Source 2 content, or None."""
        from . import source2
        key = model.lower()
        if key in self._shapes:
            return self._shapes[key]
        found = None
        path = self.source2_path(key) if self.source2 else None
        if path:
            for v in self.source2:
                data = v.read(path)
                if data is None:
                    continue
                try:
                    shapes = source2.physics_shapes(data)
                except source2.Source2Error:
                    shapes = None
                if shapes:
                    found = shapes
                    self.used[key] = self.label(v)
                break
        self._shapes[key] = found
        return found

    def get(self, path: str, default=None):
        if not any(path.lower().endswith(e) for e in MODEL_EXTS):
            return default
        for v in self.vpks:
            data = v.read(path)
            if data is not None:
                if path.lower().endswith(".mdl"):
                    self.used[path.lower()] = self.label(v)
                return data
        return default


class ChainFiles:
    """Packed map files first, then stock models."""

    def __init__(self, packed: Dict[str, bytes], stock: Optional[StockModels]):
        self.packed, self.stock = packed, stock

    def get(self, path: str, default=None):
        data = self.packed.get(path)
        if data is None and self.stock is not None:
            data = self.stock.get(path)
        return default if data is None else data

    def source2_shapes(self, model: str):
        return self.stock.source2_shapes(model) if self.stock is not None else None

    def __len__(self) -> int:
        return len(self.packed)
