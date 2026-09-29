#!/usr/bin/env python3
"""Minimal glTF 2.0 binary (.glb) writer for Space Adventure assets.

Pure stdlib -- no Blender, no pip installs. The generator scripts build
flat-shaded, vertex-coloured meshes from boxes, tubes and icospheres, and
this module packs them into self-contained .glb files that the client loads
with zero external dependencies.

Flat shading is baked into the normals: every triangle owns its three
vertices and its face normal, so the faceted look needs no loader-side
material flag. The palette rides in COLOR_0 (VEC3 uint8), multiplied by a
white PBR material; materials differ only by double-sidedness, tint and
alpha (the ship's canopy glass).
"""
from __future__ import annotations

import json
import math
import os
import struct

Vec = tuple[float, float, float]


def _norm(v):
    l = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    if l < 1e-12:
        return (0.0, 0.0, 1.0)
    return (v[0] / l, v[1] / l, v[2] / l)


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def face_normal(a, b, c):
    return _norm(_cross((b[0] - a[0], b[1] - a[1], b[2] - a[2]),
                        (c[0] - a[0], c[1] - a[1], c[2] - a[2])))


def rot_y(p, ang):
    if ang == 0.0:
        return p
    c, s = math.cos(ang), math.sin(ang)
    return (p[0] * c - p[2] * s, p[1], p[0] * s + p[2] * c)


# ---------------------------------------------------------------- primitives
# All builders return lists of (a, b, c, color) triangles; color is an
# (r, g, b) float triple in 0..1 or None (tint comes from the material).

def box(cx, cy, cz, sx, sy, sz, color, angle=0.0):
    """Closed box (12 tris) centred at (cx, cy, cz), rotated about its own
    centre by `angle` radians around Y."""
    hx, hy, hz = sx / 2.0, sy / 2.0, sz / 2.0
    raw = [(-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz),
           (-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz)]
    v = []
    for (x, y, z) in raw:
        rx, ry, rz = rot_y((x, y, z), angle)
        v.append((rx + cx, ry + cy, rz + cz))
    t = [(0, 3, 2), (0, 2, 1),   # -Z
         (4, 5, 6), (4, 6, 7),   # +Z
         (0, 4, 7), (0, 7, 3),   # -X
         (1, 2, 6), (1, 6, 5),   # +X
         (0, 1, 4), (0, 4, 5),   # -Y
         (2, 3, 6), (2, 6, 7)]   # +Y
    return [(v[i0], v[i1], v[i2], color) for i0, i1, i2 in t]


def tube(ring_a, ring_b, color):
    """Side walls between two same-count rings, each CCW viewed from +axis.
    Outward winding assumes ring_a precedes ring_b along +axis."""
    n = len(ring_a)
    out = []
    for i in range(n):
        j = (i + 1) % n
        out.append((ring_a[i], ring_a[j], ring_b[j], color))
        out.append((ring_a[i], ring_b[j], ring_b[i], color))
    return out


def cap(points, center, hint, color):
    """Fan cap closing a ring; winding is fixed against `hint`."""
    out = []
    for i in range(len(points)):
        j = (i + 1) % len(points)
        a, b, c = center, points[j], points[i]
        if _dot(face_normal(a, b, c), hint) < 0:
            b, c = c, b
        out.append((a, b, c, color))
    return out


def icosphere(r, detail=1):
    """Icosphere: (vertices, faces). detail 0/1/2 -> 20/80/320 faces."""
    t = (1.0 + math.sqrt(5.0)) / 2.0
    V = [(-1, t, 0), (1, t, 0), (-1, -t, 0), (1, -t, 0),
         (0, -1, t), (0, 1, t), (0, -1, -t), (0, 1, -t),
         (t, 0, -1), (t, 0, 1), (-t, 0, -1), (-t, 0, 1)]
    V = [_norm(v) for v in V]
    F = [(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
         (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
         (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
         (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)]
    for _ in range(detail):
        cache = {}
        F2 = []
        for a, b, c in F:
            def mid(i, j, _V=V, _cache=cache):
                key = (i, j) if i < j else (j, i)
                if key not in _cache:
                    m = _norm((_V[i][0] + _V[j][0],
                               _V[i][1] + _V[j][1],
                               _V[i][2] + _V[j][2]))
                    _V.append(m)
                    _cache[key] = len(_V) - 1
                return _cache[key]
            ab, bc, ca = mid(a, b), mid(b, c), mid(c, a)
            F2 += [(a, ab, ca), (b, bc, ab), (c, ca, bc), (ab, bc, ca)]
        F = F2
    return [(x * r, y * r, z * r) for (x, y, z) in V], F


class Rng:
    """Deterministic xorshift32: same seed, same asset, every clone."""

    def __init__(self, seed):
        self.s = seed & 0xFFFFFFFF or 1

    def next(self):
        s = self.s
        s ^= (s << 13) & 0xFFFFFFFF
        s ^= s >> 17
        s ^= (s << 5) & 0xFFFFFFFF
        self.s = s
        return s / 4294967295.0

    def uniform(self, lo, hi):
        return lo + (hi - lo) * self.next()


# ---------------------------------------------------------------------- nodes

class Node:
    """A glTF node: translation only (this project's assets rotate at
    placement time, not in the file), an optional set of mesh parts and
    children. `add` appends triangles under a named material."""

    def __init__(self, name, t=(0.0, 0.0, 0.0)):
        self.name = name
        self.t = t
        self.children = []
        self.parts = []  # [(material_name, [tri, ...])]

    def child(self, name, t=(0.0, 0.0, 0.0)):
        n = Node(name, t)
        self.children.append(n)
        return n

    def add(self, material, tris):
        self.parts.append((material, tris))


# ------------------------------------------------------------------ glb writer

def write_glb(path, root, materials):
    """Pack the node tree rooted at `root` into a .glb at `path`.

    `materials` is a list of dicts: name, double_sided, alpha, tint
    (r, g, b, optional), metallic, roughness. Returns the triangle count.
    """
    mat_idx = {m["name"]: i for i, m in enumerate(materials)}

    ordered = []

    def walk(n):
        ordered.append(n)
        for c in n.children:
            walk(c)

    walk(root)

    buffer_views, accessors, meshes, nodes_json = [], [], [], []
    bin_buf = bytearray()

    def add_view(data):
        nonlocal bin_buf
        pad = (-len(bin_buf)) % 4
        if pad:
            bin_buf += b"\x00" * pad
        off = len(bin_buf)
        bin_buf += data
        buffer_views.append({"buffer": 0, "byteOffset": off,
                             "byteLength": len(data)})
        return len(buffer_views) - 1

    total_tris = 0
    for node in ordered:
        nj = {"name": node.name}
        if any(node.t):
            nj["translation"] = [float(x) for x in node.t]
        if node.parts:
            prims = []
            for mat_name, tris in node.parts:
                if not tris:
                    continue
                n = len(tris)
                total_tris += n
                has_color = all(t[3] is not None for t in tris)
                pos, nrm, col = [], [], []
                for (a, b, c, cr) in tris:
                    f = face_normal(a, b, c)
                    for p in (a, b, c):
                        pos.append(p[0]); pos.append(p[1]); pos.append(p[2])
                        nrm.append(f[0]); nrm.append(f[1]); nrm.append(f[2])
                    if has_color:
                        for _ in (a, b, c):
                            col.append(int(round(cr[0] * 255.0)))
                            col.append(int(round(cr[1] * 255.0)))
                            col.append(int(round(cr[2] * 255.0)))
                idx = [k for t in range(n) for k in (3 * t, 3 * t + 1, 3 * t + 2)]
                ib = add_view(struct.pack("<%dH" % (3 * n), *idx))
                pb = add_view(struct.pack("<%df" % (9 * n), *pos))
                nb = add_view(struct.pack("<%df" % (9 * n), *nrm))
                mn = [min(pos[i::3]) for i in range(3)]
                mx = [max(pos[i::3]) for i in range(3)]
                ia = len(accessors)
                accessors.append({"bufferView": ib, "componentType": 5123,
                                  "count": 3 * n, "type": "SCALAR"})
                accessors.append({"bufferView": pb, "componentType": 5126,
                                  "count": 3 * n, "type": "VEC3",
                                  "min": mn, "max": mx})
                accessors.append({"bufferView": nb, "componentType": 5126,
                                  "count": 3 * n, "type": "VEC3"})
                attrs = {"POSITION": ia + 1, "NORMAL": ia + 2}
                if has_color:
                    cb = add_view(bytes(col))
                    ca = len(accessors)
                    # normalized is REQUIRED for a byte COLOR_0 (glTF 2.0):
                    # without it Godot read 0..255 as floats and every kit
                    # piece rendered pure white (C106).
                    accessors.append({"bufferView": cb, "componentType": 5121,
                                      "normalized": True,
                                      "count": 3 * n, "type": "VEC3"})
                    attrs["COLOR_0"] = ca
                prims.append({"attributes": attrs, "indices": ia,
                              "material": mat_idx[mat_name]})
            meshes.append({"name": node.name, "primitives": prims})
            nj["mesh"] = len(meshes) - 1
        if node.children:
            nj["children"] = [ordered.index(c) for c in node.children]
        nodes_json.append(nj)

    mats_json = []
    for m in materials:
        tint = m.get("tint", (1.0, 1.0, 1.0))
        alpha = m.get("alpha", 1.0)
        mj = {"name": m["name"],
              "pbrMetallicRoughness": {
                  "baseColorFactor": [float(tint[0]), float(tint[1]),
                                      float(tint[2]), float(alpha)],
                  "metallicFactor": float(m.get("metallic", 0.0)),
                  "roughnessFactor": float(m.get("roughness", 0.95))}}
        if m.get("double_sided"):
            mj["doubleSided"] = True
        if alpha < 1.0:
            mj["alphaMode"] = "BLEND"
        mats_json.append(mj)

    gltf = {
        "asset": {"version": "2.0",
                  "generator": "space-adventure art/tools (glb.py)"},
        "scene": 0,
        "scenes": [{"name": root.name, "nodes": [0]}],
        "nodes": nodes_json,
        "materials": mats_json,
        "meshes": meshes,
        "buffers": [{"byteLength": len(bin_buf)}],
        "bufferViews": buffer_views,
        "accessors": accessors,
    }

    js = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    js += b" " * ((-len(js)) % 4)
    bpad = (-len(bin_buf)) % 4
    total = 12 + 8 + len(js) + 8 + len(bin_buf) + bpad
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, total))
        f.write(struct.pack("<II", len(js), 0x4E4F534A))
        f.write(js)
        f.write(struct.pack("<II", len(bin_buf) + bpad, 0x004E4942))
        f.write(bytes(bin_buf))
        f.write(b"\x00" * bpad)
    return total_tris


def out_path(*parts):
    """Path relative to art/ (one level above tools/)."""
    return os.path.join(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))), *parts)
