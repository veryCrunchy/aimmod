"""Unpack map archives: zip (stdlib), rar/7z (bsdtar or 7-Zip if installed), Garry's Mod .gma."""
from __future__ import annotations

import lzma
import os
import shutil
import struct
import subprocess
import zipfile
from typing import List

MAP_EXTS = (".bsp", ".vmf")


class ArchiveError(Exception):
    pass


def _safe_join(root: str, name: str) -> str:
    name = name.replace("\\", "/").lstrip("/")
    dst = os.path.normpath(os.path.join(root, name))
    if not dst.startswith(os.path.normpath(root) + os.sep) and dst != os.path.normpath(root):
        raise ArchiveError(f"refusing to extract outside the target folder: {name}")
    return dst


def read_gma(data: bytes) -> List[tuple]:
    """Return [(path, bytes)] from a GMod addon. Handles LZMA-wrapped workshop downloads."""
    if data[:4] != b"GMAD":
        try:
            data = lzma.decompress(data, format=lzma.FORMAT_ALONE)
        except lzma.LZMAError as exc:
            raise ArchiveError("not a GMA file") from exc
        if data[:4] != b"GMAD":
            raise ArchiveError("not a GMA file")
    o = 4
    version = data[o]
    o += 1 + 8 + 8  # version, steam id, timestamp

    def cstr() -> str:
        nonlocal o
        end = data.index(b"\0", o)
        s = data[o:end].decode("utf-8", "replace")
        o = end + 1
        return s

    if version > 1:
        while cstr():
            pass
    cstr(); cstr(); cstr()  # name, description, author
    o += 4  # addon version
    entries = []
    while True:
        (num,) = struct.unpack_from("<I", data, o)
        o += 4
        if num == 0:
            break
        name = cstr()
        size, _crc = struct.unpack_from("<qI", data, o)
        o += 12
        entries.append((name, size))
    out = []
    for name, size in entries:
        out.append((name, data[o:o + size]))
        o += size
    return out


def _external(path: str, dst: str) -> None:
    tools = []
    for exe in ("7z", "7za"):
        if shutil.which(exe):
            tools.append([exe, "x", "-y", f"-o{dst}", path])
    sys_tar = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "tar.exe")
    for exe in (sys_tar, "bsdtar", "tar"):
        if os.path.isfile(exe) or shutil.which(exe):
            tools.append([exe, "-xf", path, "-C", dst])
    if shutil.which("unrar"):
        tools.append(["unrar", "x", "-o+", path, dst + os.sep])
    for cmd in tools:
        try:
            if subprocess.run(cmd, capture_output=True).returncode == 0:
                return
        except OSError:
            continue
    raise ArchiveError(f"cannot unpack {os.path.basename(path)}: install 7-Zip or use a tar with rar support")


def extract(path: str, dst: str) -> List[str]:
    """Extract an archive into dst and return the map files found inside (recursively)."""
    os.makedirs(dst, exist_ok=True)
    low = path.lower()
    if low.endswith(".gma"):
        with open(path, "rb") as fh:
            for name, blob in read_gma(fh.read()):
                p = _safe_join(dst, name)
                os.makedirs(os.path.dirname(p), exist_ok=True)
                with open(p, "wb") as out:
                    out.write(blob)
    elif zipfile.is_zipfile(path):  # also .pk3 (Quake 3)
        with zipfile.ZipFile(path) as z:
            for info in z.infolist():
                if info.is_dir():
                    continue
                p = _safe_join(dst, info.filename)
                os.makedirs(os.path.dirname(p), exist_ok=True)
                with z.open(info) as src, open(p, "wb") as out:
                    shutil.copyfileobj(src, out)
    else:
        _external(path, dst)
    found = []
    for root, _dirs, files in os.walk(dst):
        for f in files:
            full = os.path.join(root, f)
            if f.lower().endswith(MAP_EXTS):
                found.append(full)
            elif f.lower().endswith((".zip", ".rar", ".7z", ".gma", ".pk3")) and full != path:
                found += extract(full, full + "_x")
    return sorted(set(found))
