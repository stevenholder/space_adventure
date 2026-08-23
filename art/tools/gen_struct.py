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

WALL_COLOR = (0.55, 0.55, 0.58)
POST_COLOR = (0.42, 0.44, 0.48)


def _fan_tri(tris, center, p, q, color):
    """Append (center, p, q) after orienting it outward from the origin."""
    n = face_normal(center, p, q)
    centroid = ((center[0] + p[0] + q[0]) / 3.0,
                (center[1] + p[1] + q[1]) / 3.0,
                (center[2] + p[2] + q[2]) / 3.0)
    if n[0] * centroid[0] + n[1] * centroid[1] + n[2] * centroid[2] < 0.0:
        p, q = q, p
    tris.append((center, p, q, color))


def wall_box(color):
    """Unit box, base at the origin, up +Y: 6 faces x 4 fan triangles = 24."""
    v = [(-0.5, 0.0, -0.5), (0.5, 0.0, -0.5), (0.5, 1.0, -0.5), (-0.5, 1.0, -0.5),
         (-0.5, 0.0, 0.5), (0.5, 0.0, 0.5), (0.5, 1.0, 0.5), (-0.5, 1.0, 0.5)]
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
    wall.add("opaque", wall_box(WALL_COLOR))
    n = write_glb(out_path("structs", "wall.glb"), wall, MATERIALS)
    print(f"structs/wall.glb: {n} tris (manifest 24)")

    post = Node("struct.post")
    post.add("opaque", post_sphere(POST_COLOR))
    n = write_glb(out_path("structs", "post.glb"), post, MATERIALS)
    print(f"structs/post.glb: {n} tris (manifest 48)")


if __name__ == "__main__":
    main()
