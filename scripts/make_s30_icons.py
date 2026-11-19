#!/usr/bin/env python3
"""Writes the blue S30.1 icons from the green originals.

The game icon uses the hue rule of Themes/ThemePalette.cs (hue 90..180 turned by +60).
The launcher icon's tile is the launcher's button blue, #276AFC.
Every frame of each .ico is kept.
Requires Pillow. Usage: python scripts/make_s30_icons.py
"""
import colorsys
import io
import struct
from collections import Counter
from pathlib import Path

from PIL import Image

ASSETS = Path(__file__).resolve().parent.parent / "src" / "R5Flowstate.Shell" / "Assets"
TILE_BLUE = (0x27, 0x6A, 0xFC)
ROTATION = 60 / 360
HUE_MIN, HUE_MAX = 90 / 360, 180 / 360


def rotate_frame(image):
    image = image.convert("RGBA")
    pixels = image.load()
    cache = {}
    for y in range(image.height):
        for x in range(image.width):
            rgba = pixels[x, y]
            out = cache.get(rgba)
            if out is None:
                h, l, s = colorsys.rgb_to_hls(rgba[0] / 255, rgba[1] / 255, rgba[2] / 255)
                out = rgba
                if s > 0 and HUE_MIN <= h <= HUE_MAX:
                    r, g, b = colorsys.hls_to_rgb((h + ROTATION) % 1.0, l, s)
                    out = (round(r * 255), round(g * 255), round(b * 255), rgba[3])
                cache[rgba] = out
            pixels[x, y] = out
    return image


def retile_frame(image):
    """Green tile to TILE_BLUE; edge pixels keep their mix with the white mark."""
    image = image.convert("RGBA")
    pixels = image.load()
    tile = Counter(
        px[:3] for px in image.getdata()
        if px[3] == 255 and px[1] > px[0] and px[1] > px[2]
    ).most_common(1)[0][0]
    tile_l = colorsys.rgb_to_hls(*(c / 255 for c in tile))[1]
    for y in range(image.height):
        for x in range(image.width):
            r, g, b, a = pixels[x, y]
            h, l, s = colorsys.rgb_to_hls(r / 255, g / 255, b / 255)
            if s > 0 and HUE_MIN <= h <= HUE_MAX:
                t = max(0.0, (l - tile_l) / (1 - tile_l))
                pixels[x, y] = tuple(round(c + (255 - c) * t) for c in TILE_BLUE) + (a,)
    return image


def read_ico(path):
    data = path.read_bytes()
    count = struct.unpack_from("<H", data, 4)[0]
    frames = []
    for i in range(count):
        size, offset = struct.unpack_from("<II", data, 6 + i * 16 + 8)
        frames.append(Image.open(io.BytesIO(data[offset:offset + size])))
    return frames


def write_ico(path, frames):
    blobs = []
    for frame in frames:
        out = io.BytesIO()
        frame.save(out, "PNG", optimize=True)
        blobs.append(out.getvalue())
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = len(header) + 16 * len(frames)
    entries = b""
    for frame, blob in zip(frames, blobs):
        w, h = frame.size
        entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    path.write_bytes(header + entries + b"".join(blobs))


for source, dest, convert in (
    ("app.ico", "app_s30.ico", retile_frame),
    ("client.ico", "client_s30.ico", rotate_frame),
):
    frames = [convert(f) for f in read_ico(ASSETS / source)]
    write_ico(ASSETS / dest, frames)
    print(dest, [f.size[0] for f in frames])
