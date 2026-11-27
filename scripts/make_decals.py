"""Draws the starter decal images in data/decals (the project's own work, see data/decals/LICENSE.md).

Run: python scripts/make_decals.py   (standard library only; writes 512 x 512 RGBA PNGs, 4x4 supersampled)
"""
import math
import os
import struct
import zlib

SIZE = 512
SAMPLES = 4
OUT = os.path.join(os.path.dirname(__file__), "..", "data", "decals")


def png(path, pixels):
    raw = b"".join(b"\x00" + bytes(pixels[y * SIZE * 4:(y + 1) * SIZE * 4]) for y in range(SIZE))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)))
        f.write(chunk(b"IDAT", zlib.compress(raw, 9)))
        f.write(chunk(b"IEND", b""))


def render(shade):
    """shade(x, y) -> (r, g, b, a) in 0..1 for x, y in -1..1 (y up); supersampled into bytes."""
    pixels = bytearray(SIZE * SIZE * 4)
    for py in range(SIZE):
        for px in range(SIZE):
            r = g = b = a = 0.0
            for sy in range(SAMPLES):
                for sx in range(SAMPLES):
                    x = ((px + (sx + 0.5) / SAMPLES) / SIZE) * 2 - 1
                    y = 1 - ((py + (sy + 0.5) / SAMPLES) / SIZE) * 2
                    cr, cg, cb, ca = shade(x, y)
                    r += cr * ca
                    g += cg * ca
                    b += cb * ca
                    a += ca
            n = SAMPLES * SAMPLES
            o = (py * SIZE + px) * 4
            if a > 0:
                pixels[o:o + 3] = bytes(round(255 * c / a) for c in (r, g, b))
            pixels[o + 3] = round(255 * a / n)
    return pixels


def emblem(x, y):
    """A ring round a double chevron pointing up, off-white."""
    white = (0.94, 0.93, 0.88, 1.0)
    d = math.hypot(x, y)
    if 0.80 <= d <= 0.94:
        return white
    for top in (0.45, 0.0):
        # A chevron: two bars meeting at (0, top), 0.16 thick, reaching 0.55 out.
        if abs(x) <= 0.55 and 0 <= (top - abs(x) * 0.8) - y <= 0.2:
            return white
    return (0, 0, 0, 0)


def roundel(x, y):
    """Three rings: rust, off-white, slate, with a slate centre dot."""
    d = math.hypot(x, y)
    if d > 0.96:
        return (0, 0, 0, 0)
    if d > 0.66:
        return (0.70, 0.22, 0.12, 1.0)
    if d > 0.36:
        return (0.94, 0.93, 0.88, 1.0)
    return (0.20, 0.26, 0.33, 1.0)


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    png(os.path.join(OUT, "emblem.png"), render(emblem))
    png(os.path.join(OUT, "roundel.png"), render(roundel))
    print("wrote emblem.png, roundel.png")
