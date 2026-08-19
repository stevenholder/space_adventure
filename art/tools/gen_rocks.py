#!/usr/bin/env python3
"""Generate props/rock_{a,b,c}.glb -- three distinct rock silhouettes.

~400 rocks scattered client-side (GDD "Rocks and surface props"): one
silhouette at 400 scales reads as a repeating texture on a 23 m-horizon
world, so each variant has its own shape language:

    a -- rounded boulder: fine facets (320 tris), low noise
    b -- flat-topped slab: coarse facets, squashed, flattened crown
    c -- sharp angular shard: coarse facets (80 tris), high noise

All variants are unit-sized (~1 m across), base seated at the origin, up
along +Y: the client seats them on the sampled surface, orients them
radially, and scales 0.3--1.5 m. Displacement is a sum of seeded
sine terms over the direction vector -- continuous, pole-free, and
deterministic, so every clone rebuilds byte-identical rocks.
"""
from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import Node, Rng, icosphere, face_normal, out_path, write_glb

MATERIALS = [{"name": "opaque", "double_sided": True}]


def displace(seed, detail, amp, scale=(1.0, 1.0, 1.0), top_squash=0.0):
    """Unit icosphere, vertices displaced radially by seeded noise, then
    anisotropically scaled. Returns (verts, faces) in object space,
    roughly centred on the origin."""
    rng = Rng(seed)
    V, F = icosphere(1.0, detail)
    w = [tuple(rng.uniform(-1.0, 1.0) for _ in range(3)) for _ in range(3)]
    l = [math.sqrt(sum(c * c for c in d)) for d in w]
    w = [(a / l[i], b / l[i], c / l[i]) for i, (a, b, c) in enumerate(w)]
    f = [rng.uniform(2.5, 4.5), rng.uniform(4.5, 7.0), rng.uniform(7.0, 10.0)]
    A = [amp, amp * 0.55, amp * 0.30]
    ph = [rng.next() * 2.0 * math.pi for _ in range(3)]

    def disp(d):
        s = 0.0
        for k in range(3):
            x = d[0] * w[k][0] + d[1] * w[k][1] + d[2] * w[k][2]
            s += A[k] * math.sin(f[k] * x + ph[k])
        return s

    V = [(v[0] * (1.0 + disp(v)) * scale[0],
          v[1] * (1.0 + disp(v)) * scale[1],
          v[2] * (1.0 + disp(v)) * scale[2]) for v in V]
    if top_squash:
        ys = [v[1] for v in V]
        ymid = (min(ys) + max(ys)) / 2.0
        V = [(x, ymid + (y - ymid) * top_squash if y > ymid else y, z)
             for (x, y, z) in V]
    return V, F


def flat_tris(V, F, base, seed):
    """Per-face triangles with flat normals, outward winding, and a small
    per-face luminance jitter so facets read as mineral, not CG."""
    rng = Rng(seed ^ 0x9E3779B9)
    tris = []
    for fi in F:
        a, b, c = V[fi[0]], V[fi[1]], V[fi[2]]
        n = face_normal(a, b, c)
        centroid = ((a[0] + b[0] + c[0]) / 3.0,
                    (a[1] + b[1] + c[1]) / 3.0,
                    (a[2] + b[2] + c[2]) / 3.0)
        if n[0] * centroid[0] + n[1] * centroid[1] + n[2] * centroid[2] < 0.0:
            b, c = c, b
        j = rng.uniform(0.94, 1.06)
        col = (min(1.0, base[0] * j), min(1.0, base[1] * j), min(1.0, base[2] * j))
        tris.append((a, b, c, col))
    return tris


def seat(tris):
    """Shift so the lowest point sits at the origin (base on the ground)."""
    ymin = min(p[1] for t in tris for p in t[:3])
    return [((a[0], a[1] - ymin, a[2]), (b[0], b[1] - ymin, b[2]),
             (c[0], c[1] - ymin, c[2]), col) for (a, b, c, col) in tris]


# Variant palettes sit in the terrain's grey-brown family so rocks read as
# the asteroid's own geology, each tinted slightly differently.
VARIANTS = {
    "a": dict(seed=101, detail=2, amp=0.12, scale=(1.0, 0.78, 0.94),
              base=(0.52, 0.50, 0.44)),
    "b": dict(seed=202, detail=1, amp=0.18, scale=(1.25, 0.55, 0.85),
              top_squash=0.55, base=(0.47, 0.46, 0.43)),
    "c": dict(seed=303, detail=1, amp=0.30, scale=(0.95, 1.05, 0.9),
              base=(0.38, 0.36, 0.34)),
}


def main():
    for letter, cfg in VARIANTS.items():
        V, F = displace(cfg["seed"], cfg["detail"], cfg["amp"],
                        cfg["scale"], cfg.get("top_squash", 0.0))
        tris = seat(flat_tris(V, F, cfg["base"], cfg["seed"]))
        root = Node(f"prop.rock.{letter}")
        root.add("opaque", tris)
        out = out_path("props", f"rock_{letter}.glb")
        n = write_glb(out, root, MATERIALS)
        print(f"props/rock_{letter}.glb: {n} tris (budget 500)")


if __name__ == "__main__":
    main()
