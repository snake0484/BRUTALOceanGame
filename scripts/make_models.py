"""Writes the brush models in data/models (the project's own work, see data/models/LICENSE.md).

Run: python scripts/make_models.py   (standard library only)

A model is JSON: positions in 1/1024 m ({"unitsPerMetre": 1024}), +Y out of the hull, +Z along the stroke, origin on
the surface; faces are polygons (3-16 corners) wound counter-clockwise seen from outside.
"""
import json
import math
import os

UNITS = 1024
OUT = os.path.join(os.path.dirname(__file__), "..", "data", "models")


def ring(radius, y, sides=6, turn=0.0):
    return [
        [round(radius * math.cos(turn + i * math.tau / sides) * UNITS), round(y * UNITS), round(radius * math.sin(turn + i * math.tau / sides) * UNITS)]
        for i in range(sides)
    ]


def nut_head():
    """A hex nut head 0.1 m across flats: a skirt under the surface (seats on curved hulls), the hex, a chamfered top."""
    across_flats = 0.10
    corner = across_flats / 2 / math.cos(math.pi / 6)
    skirt, height, chamfer = -0.01, 0.05, 0.02
    rings = [ring(corner, skirt), ring(corner, height - chamfer), ring(corner * 0.8, height)]
    vertices = [v for r in rings for v in r]
    faces = []

    for r in range(len(rings) - 1):
        for j in range(6):
            jn = (j + 1) % 6
            # Seen from outside the side wall: lower j, upper j, upper jn, lower jn.
            faces.append([r * 6 + j, (r + 1) * 6 + j, (r + 1) * 6 + jn, r * 6 + jn])

    faces.append([0, 1, 2, 3, 4, 5])                      # bottom, seen from below
    top = (len(rings) - 1) * 6
    faces.append([top + 5, top + 4, top + 3, top + 2, top + 1, top])  # top, seen from above
    return {"unitsPerMetre": UNITS, "vertices": vertices, "faces": faces}


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    model = nut_head()
    with open(os.path.join(OUT, "nut_hex.json"), "w", newline="\n") as f:
        f.write('{\n  "unitsPerMetre": %d,\n' % model["unitsPerMetre"])
        f.write('  "vertices": [\n    ' + ',\n    '.join(json.dumps(v) for v in model["vertices"]) + '\n  ],\n')
        f.write('  "faces": [\n    ' + ',\n    '.join(json.dumps(v) for v in model["faces"]) + '\n  ]\n}\n')
    print("wrote nut_hex.json")
