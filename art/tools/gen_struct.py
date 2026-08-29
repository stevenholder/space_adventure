#!/usr/bin/env python3
"""Generate structs/wall.glb and structs/post.glb -- range-wall and cover-post
visual geometry (GDD "Phase 2" combat range).

Both shapes are dumb placeholders: the authoritative geometry is the
`colliders` message (docs/GDD.md, "Static colliders"), and the client scales
these unit meshes to each collider's half-extents / radius at placement time.
No collision data lives in either file -- visual only.

    struct.wall -- unit box, base at the origin, up +Y. Each of the 6 faces
                   is a 4-triangle fan (not the usual 2), to land the
                   manifest's exact 24-triangle budget while keeping the
                   flat-faced box silhouette (glb.py's `box()` gives 12).
    struct.post -- unit sphere, radius 1, centred at the origin. A UV sphere
                   (8 longitude segments x 3 latitude rings) lands the
                   manifest's exact 48-triangle budget.

Both are built from plain coordinates -- no RNG involved -- so every clone
regenerates byte-identical files.
"""
from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import Node, face_normal, out_path, write_glb

MATERIALS = [{"name": "opaque", "double_sided": True}]

# Three bands up the wall. The mid tone is the old flat colour; the base is
# darker so the wall sits ON the ground rather than floating against it, and
# the coping is lighter so there is a readable top edge against the sky --
# which is the line that tells you how tall the thing you are behind is.
WALL_BASE = (0.34, 0.34, 0.37)
WALL_COLOR = (0.55, 0.55, 0.58)
WALL_CAP = (0.70, 0.69, 0.66)
POST_COLOR = (0.42, 0.44, 0.48)

# Fractions of the wall's height. Proportional, not absolute, because the
# client scales this to walls of different heights.
BASE_TOP = 0.14
CAP_BOTTOM = 0.88


def _fan_tri(tris, center, p, q, color):
    """Append (center, p, q) after orienting it outward from the origin."""
    n = face_normal(center, p, q)
    centroid = ((center[0] + p[0] + q[0]) / 3.0,
                (center[1] + p[1] + q[1]) / 3.0,
                (center[2] + p[2] + q[2]) / 3.0)
    if n[0] * centroid[0] + n[1] * centroid[1] + n[2] * centroid[2] < 0.0:
        p, q = q, p
    tris.append((center, p, q, color))


def wall_box(color, y0=0.0, y1=1.0, inset=0.0):
    """
    Box spanning y0..y1, footprint 1x1 shrunk by `inset` on each side.

    Banded rather than plain, and the bands run in Y for a reason: the client
    scales this unit mesh to each collider's half-extents, and a camp wall is
    32 m long, 3 m tall and 0.8 m thick. Any detail in X or Z is stretched
    forty-fold and turns to mush; a band in Y stays a band, because Y is
    scaled by the wall's height and nothing else.

    One flat slab 32 m across in a single colour has no edge you can read at
    the top and nothing to give it a base, and under this shader's ambient
    (0.30 + 0.72 * lambert) its shaded side sits at 0.16 of its own colour --
    which is why the camp read as a black void rather than as a wall.
    """
    lo, hi = -0.5 + inset, 0.5 - inset
    v = [(lo, y0, lo), (hi, y0, lo), (hi, y1, lo), (lo, y1, lo),
         (lo, y0, hi), (hi, y0, hi), (hi, y1, hi), (lo, y1, hi)]
    # Each 4-tuple is a face loop (a, b, c, d) walked CCW as seen from outside.
    loops = [(0, 3, 2, 1),   # -Z
             (4, 5, 6, 7),   # +Z
             (0, 4, 7, 3),   # -X
             (1, 2, 6,5),    # +X
             (0, 1, 4, 5),   # -Y
             (2, 3, 6, 7)]   # +Y
    tris = []
    for (a, b, c, d) in loops:
        corners = (v[a], v[b], v[c], v[d])
        center = tuple(sum(p[i] for p in corners) / 4.0 for i in range(3))
        for i in range(4):
            _fan_tri(tris, center, corners[i], corners[(i + 1) % 4], color)
    return tris


def post_sphere(color, segments=8, rings=3):
    """Unit UV sphere: `segments` longitude divisions, `rings` latitude
    rings between the poles. Triangle count is 2 * segments * rings."""
    north, south = (0.0, 1.0, 0.0), (0.0, -1.0, 0.0)
    lat = []
    for i in range(1, rings + 1):
        phi = i * math.pi / (rings + 1)
        y, r = math.cos(phi), math.sin(phi)
        lat.append([(r * math.cos(2.0 * math.pi * j / segments), y,
                     r * math.sin(2.0 * math.pi * j / segments))
                    for j in range(segments)])

    tris = []
    for j in range(segments):
        jn = (j + 1) % segments
        _fan_tri(tris, north, lat[0][jn], lat[0][j], color)
    for i in range(rings - 1):
        ra, rb = lat[i], lat[i + 1]
        for j in range(segments):
            jn = (j + 1) % segments
            _fan_tri(tris, ra[j], ra[jn], rb[jn], color)
            _fan_tri(tris, ra[j], rb[jn], rb[j], color)
    for j in range(segments):
        jn = (j + 1) % segments
        _fan_tri(tris, south, lat[-1][j], lat[-1][jn], color)
    return tris


def main():
    wall = Node("struct.wall")
    # The base is inset slightly so its edge catches light differently from
    # the shaft above it, which reads as a plinth rather than as a colour
    # change painted on a flat face.
    wall.add("opaque", wall_box(WALL_BASE, 0.0, BASE_TOP, inset=-0.03))
    wall.add("opaque", wall_box(WALL_COLOR, BASE_TOP, CAP_BOTTOM))
    wall.add("opaque", wall_box(WALL_CAP, CAP_BOTTOM, 1.0, inset=-0.04))
    n = write_glb(out_path("structs", "wall.glb"), wall, MATERIALS)
    print(f"structs/wall.glb: {n} tris")

    post = Node("struct.post")
    post.add("opaque", post_sphere(POST_COLOR))
    n = write_glb(out_path("structs", "post.glb"), post, MATERIALS)
    print(f"structs/post.glb: {n} tris (manifest 48)")


if __name__ == "__main__":
    main()
