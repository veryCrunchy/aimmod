"""Minimal PNG reader/writer for preview images (8-bit RGB/RGBA, standard library only)."""
from __future__ import annotations

import struct
import zlib
from typing import List, Tuple

SIGNATURE = b"\x89PNG\r\n\x1a\n"
Image = Tuple[int, int, List[bytearray]]  # width, height, RGB rows


class PngError(ValueError):
    pass


def _paeth(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def read(data: bytes) -> Image:
    if not data.startswith(SIGNATURE):
        raise PngError("not a PNG file")
    pos, idat, header = 8, bytearray(), None
    while pos + 8 <= len(data):
        length, tag = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if tag == b"IHDR":
            header = struct.unpack(">IIBBBBB", body)
        elif tag == b"IDAT":
            idat += body
        elif tag == b"IEND":
            break
    if header is None:
        raise PngError("missing IHDR")
    width, height, depth, color, _comp, _filter, interlace = header
    if depth != 8 or color not in (2, 6) or interlace != 0:
        raise PngError("only 8-bit, non-interlaced RGB or RGBA PNGs are supported")
    if width * height > 40_000_000:
        raise PngError("image too large")
    channels = 3 if color == 2 else 4
    stride = width * channels
    raw = zlib.decompress(bytes(idat))
    if len(raw) < height * (stride + 1):
        raise PngError("truncated image data")
    rows: List[bytearray] = []
    previous = bytearray(stride)
    for y in range(height):
        kind = raw[y * (stride + 1)]
        line = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        if kind == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 255
        elif kind == 2:
            for i in range(stride):
                line[i] = (line[i] + previous[i]) & 255
        elif kind == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + previous[i]) >> 1)) & 255
        elif kind == 4:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                up_left = previous[i - channels] if i >= channels else 0
                line[i] = (line[i] + _paeth(left, previous[i], up_left)) & 255
        elif kind != 0:
            raise PngError(f"unknown filter {kind}")
        previous = line
        if channels == 4:
            rgb = bytearray(width * 3)
            rgb[0::3], rgb[1::3], rgb[2::3] = line[0::4], line[1::4], line[2::4]
            line = rgb
        rows.append(line)
    return width, height, rows


def write(image: Image) -> bytes:
    width, height, rows = image

    def chunk(tag: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + tag + body + struct.pack(">I", zlib.crc32(tag + body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + bytes(row) for row in rows)
    return (SIGNATURE + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def fit(image: Image, limit: int) -> Image:
    """Area-average downscale so neither side exceeds `limit` (never upscales)."""
    width, height, rows = image
    scale = max(width, height) / limit
    if scale <= 1:
        return image
    out_w, out_h = max(1, round(width / scale)), max(1, round(height / scale))
    out: List[bytearray] = []
    for oy in range(out_h):
        y0, y1 = oy * height // out_h, max(oy * height // out_h + 1, (oy + 1) * height // out_h)
        sums = [0] * (out_w * 3)
        counts = [0] * out_w
        for y in range(y0, y1):
            row = rows[y]
            for ox in range(out_w):
                x0, x1 = ox * width // out_w, max(ox * width // out_w + 1, (ox + 1) * width // out_w)
                segment = row[x0 * 3:x1 * 3]
                sums[ox * 3] += sum(segment[0::3])
                sums[ox * 3 + 1] += sum(segment[1::3])
                sums[ox * 3 + 2] += sum(segment[2::3])
                counts[ox] += x1 - x0
        line = bytearray(out_w * 3)
        for ox in range(out_w):
            n = counts[ox] or 1
            line[ox * 3:ox * 3 + 3] = bytes((sums[ox * 3] // n, sums[ox * 3 + 1] // n, sums[ox * 3 + 2] // n))
        out.append(line)
    return out_w, out_h, out
