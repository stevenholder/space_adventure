#!/usr/bin/env python3
"""Generate props/target.glb -- the range target dummy (manifest "prop.target").

A concentric plate mounted on a post: base at the origin, up +Y, faces -Z,
about 1.8 m tall. `plate` is its own named node, separate from `post`, so the
client can recolour it in place to show hit/reset state without touching the
post's material. Exactly 64 triangles (post 24 + plate 40), built from plain
trig -- same seed, same file, every clone.
"""
from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import Node, box, cap, face_normal, out_path, write_glb

MATERIALS = [{"name": "opaque", "double_sided": True}]

STEEL = (0.35, 0.35, 0.38)
RED = (0.75, 0.12, 0.10)
WHITE = (0.85, 0.85, 0.80)
BACK = (0.25, 0.25, 0.27)

N = 10                # plate sides
R_OUTER = 0.40         # bullseye plate radius
R_INNER = 0.16         # inner ring radius
PLATE_Y = 1.40         # plate centre height -> top at 1.80 m
FRONT_Z = -0.015       # plate faces -Z
BACK_Z = 0.015


def _band(inner, outer, hint, color):
    """Flat annulus between two same-count rings, winding fixed against
    `hint` (same trick as glb.cap, generalised to a quad strip)."""
    tris = []
    n = len(inner)
    for i in range(n):
        j = (i + 1) % n
        for a, b, c in ((inner[i], inner[j], outer[j]),
                        (inner[i], outer[j], outer[i])):
            if sum(x * y for x, y in zip(face_normal(a, b, c), hint)) < 0:
                b, c = c, b
            tris.append((a, b, c, color))
    return tris


def build_post():
    shaft = box(0.0, 0.75, 0.0, 0.12, 1.30, 0.12, STEEL)     # 12 tris
    foot = box(0.0, 0.05, 0.0, 0.50, 0.10, 0.35, STEEL)      # 12 tris
    return shaft + foot


def build_plate():
    angles = [2.0 * math.pi * i / N for i in range(N)]

    def ring(r, z):
        return [(r * math.cos(a), PLATE_Y + r * math.sin(a), z) for a in angles]

    front_inner = ring(R_INNER, FRONT_Z)
    front_outer = ring(R_OUTER, FRONT_Z)
    back_outer = ring(R_OUTER, BACK_Z)

    bullseye = cap(front_inner, (0.0, PLATE_Y, FRONT_Z), (0.0, 0.0, -1.0), RED)
    ring_band = _band(front_inner, front_outer, (0.0, 0.0, -1.0), WHITE)
    backing = cap(back_outer, (0.0, PLATE_Y, BACK_Z), (0.0, 0.0, 1.0), BACK)
    return bullseye + ring_band + backing  # 10 + 20 + 10 = 40 tris


def main():
    root = Node("prop.target")
    root.child("post").add("opaque", build_post())
    root.child("plate").add("opaque", build_plate())
    out = out_path("props", "target.glb")
    n = write_glb(out, root, MATERIALS)
    print(f"props/target.glb: {n} tris (budget 64)")


if __name__ == "__main__":
    main()
