#!/usr/bin/env python3
"""Offline render check for art/ assets -- no GPU, no WebGL, pure stdlib.

Parses each .glb (JSON + BIN chunk), composes the node tree (translations
only: this project's assets carry no rotations in the file), and rasterises
a few perspective views with a z-buffer and flat lighting into
art/cache/*.png. The PNGs are for an eyeball (or inspect_image) to check
silhouette, proportions, palette and the from-inside views the GDD demands
(own body seen from above; cockpit seen from the pilot seat).
"""
from __future__ import annotations

import json
import math
import os
import struct
import sys
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import out_path

# ------------------------------------------------------------- glb reading

def read_glb(path):
    with open(path, "rb") as f:
        data = f.read()
    magic, version, total = struct.unpack_from("<III", data, 0)
    assert magic == 0x46546C67 and version == 2 and total == len(data), "bad glb"
    off = 12
    doc, binbuf = None, b""
    while off < len(data):
        ln, typ = struct.unpack_from("<II", data, off)
        off += 8
        chunk = data[off:off + ln]
        off += ln
        if typ == 0x4E4F534A:
            doc = json.loads(chunk)
        elif typ == 0x004E4942:
            binbuf = chunk
    assert doc is not None and binbuf, "missing glb chunk"
    return doc, binbuf


# Component type -> (struct code, bytes, normalising divisor).
_CT = {
    5120: ("b", 1, 127.0),
    5121: ("B", 1, 255.0),
    5122: ("h", 2, 32767.0),
    5123: ("H", 2, 65535.0),
    5125: ("I", 4, 1.0),
    5126: ("f", 4, 1.0),
}


def accessor_array(doc, binbuf, acc, per):
    """
    Read one accessor.

    Honours the accessor's OWN byteOffset and the bufferView's byteStride,
    both of which the earlier version ignored. That was invisible while every
    .glb here came from tools/glb.py, which writes one tightly packed
    bufferView per accessor -- so accessor byteOffset was always 0 and there
    was never a stride. Every other glTF writer in the world packs several
    accessors into one bufferView, and reading from the view's start then
    returns a different attribute's bytes: colours outside [0,1], positions
    that are garbage, and a crash somewhere downstream rather than here.
    """
    bv = doc["bufferViews"][acc["bufferView"]]
    n = acc["count"]
    code, size, divisor = _CT[acc["componentType"]]
    base = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    stride = bv.get("byteStride") or (size * per)
    scale = (1.0 / divisor) if acc.get("normalized") else 1.0

    out = []
    for i in range(n):
        vals = struct.unpack_from("<%d%s" % (per, code), binbuf, base + i * stride)
        out.append([v * scale for v in vals])
    return out


def _node_matrix(nd):
    """A node's local transform as a column-major 4x4, from `matrix` or TRS."""
    if "matrix" in nd:
        return list(nd["matrix"])
    tx, ty, tz = nd.get("translation", (0.0, 0.0, 0.0))
    qx, qy, qz, qw = nd.get("rotation", (0.0, 0.0, 0.0, 1.0))
    sx, sy, sz = nd.get("scale", (1.0, 1.0, 1.0))
    x2, y2, z2 = qx + qx, qy + qy, qz + qz
    xx, xy, xz = qx * x2, qx * y2, qx * z2
    yy, yz, zz = qy * y2, qy * z2, qz * z2
    wx, wy, wz = qw * x2, qw * y2, qw * z2
    return [
        (1 - (yy + zz)) * sx, (xy + wz) * sx, (xz - wy) * sx, 0.0,
        (xy - wz) * sy, (1 - (xx + zz)) * sy, (yz + wx) * sy, 0.0,
        (xz + wy) * sz, (yz - wx) * sz, (1 - (xx + yy)) * sz, 0.0,
        tx, ty, tz, 1.0,
    ]


def _mat_mul(a, b):
    out = [0.0] * 16
    for c in range(4):
        for r in range(4):
            out[c * 4 + r] = sum(a[k * 4 + r] * b[c * 4 + k] for k in range(4))
    return out


def _xform(m, v):
    return (
        m[0] * v[0] + m[4] * v[1] + m[8] * v[2] + m[12],
        m[1] * v[0] + m[5] * v[1] + m[9] * v[2] + m[13],
        m[2] * v[0] + m[6] * v[1] + m[10] * v[2] + m[14],
    )


def _xform_dir(m, v):
    """Direction transform: rotation and scale, no translation."""
    x = m[0] * v[0] + m[4] * v[1] + m[8] * v[2]
    y = m[1] * v[0] + m[5] * v[1] + m[9] * v[2]
    z = m[2] * v[0] + m[6] * v[1] + m[10] * v[2]
    n = math.sqrt(x * x + y * y + z * z)
    return (x / n, y / n, z / n) if n > 1e-12 else (0.0, 1.0, 0.0)


def load_triangles(path):
    """
    World-space triangles: (a, b, c, normal, color, is_glass).

    Walks the SCENE GRAPH rather than the mesh list, so a mesh referenced by
    two nodes is drawn twice, once per instance. The earlier version built a
    mesh -> node map, which silently kept only the last node of any shared
    mesh; that never came up while each model had its own unique meshes, and
    it does now that the import pipeline runs dedup().
    """
    doc, binbuf = read_glb(path)
    mats = doc.get("materials", [])
    nodes = doc.get("nodes", [])
    tris = []

    def emit(mesh_index, world):
        mesh = doc["meshes"][mesh_index]
        for prim in mesh["primitives"]:
            attrs = prim["attributes"]
            mat = mats[prim["material"]] if mats and "material" in prim else {}
            is_glass = mat.get("alphaMode") == "BLEND"

            pos = accessor_array(doc, binbuf, doc["accessors"][attrs["POSITION"]], 3)
            nrm = (accessor_array(doc, binbuf, doc["accessors"][attrs["NORMAL"]], 3)
                   if "NORMAL" in attrs else None)
            col = None
            if "COLOR_0" in attrs:
                acc = doc["accessors"][attrs["COLOR_0"]]
                per = 4 if acc["type"] == "VEC4" else 3
                col = accessor_array(doc, binbuf, acc, per)

            if "indices" in prim:
                idx = [int(r[0]) for r in
                       accessor_array(doc, binbuf, doc["accessors"][prim["indices"]], 1)]
            else:
                idx = list(range(len(pos)))

            wp = [_xform(world, p) for p in pos]
            for i in range(0, len(idx), 3):
                k0, k1, k2 = idx[i], idx[i + 1], idx[i + 2]
                if nrm:
                    n = _xform_dir(world, nrm[k0])
                else:
                    # Face normal from winding, for a mesh with no NORMAL.
                    a, b, c = wp[k0], wp[k1], wp[k2]
                    u = (b[0] - a[0], b[1] - a[1], b[2] - a[2])
                    v = (c[0] - a[0], c[1] - a[1], c[2] - a[2])
                    n = _xform_dir(
                        [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
                        (u[1] * v[2] - u[2] * v[1],
                         u[2] * v[0] - u[0] * v[2],
                         u[0] * v[1] - u[1] * v[0]))
                # Colour is clamped on the way in: a value outside [0,1] is
                # out of spec, and letting one through only turns into a
                # "byte must be in range(0, 256)" a hundred lines later.
                c0 = col[k0] if col else (1.0, 1.0, 1.0)
                c0 = tuple(min(1.0, max(0.0, x)) for x in c0[:3])
                tris.append((wp[k0], wp[k1], wp[k2], n, c0, is_glass))

    def walk(i, parent):
        nd = nodes[i]
        world = _mat_mul(parent, _node_matrix(nd))
        if "mesh" in nd:
            emit(nd["mesh"], world)
        for c in nd.get("children", []):
            walk(c, world)

    ident = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
    for root_i in doc["scenes"][0]["nodes"]:
        walk(root_i, ident)
    return tris


# ---------------------------------------------------------------- rendering

def fit_center(tris):
    pts = [p for t in tris for p in t[:3]]
    return [sum(p[i] for p in pts) / len(pts) for i in range(3)]


def look_basis(d, up=(0.0, 1.0, 0.0)):
    dl = math.sqrt(sum(x * x for x in d))
    d = tuple(x / dl for x in d)
    rx = up[1] * d[2] - up[2] * d[1]
    ry = up[2] * d[0] - up[0] * d[2]
    rz = up[0] * d[1] - up[1] * d[0]
    rl = math.sqrt(rx * rx + ry * ry + rz * rz) or 1.0
    r = (rx / rl, ry / rl, rz / rl)
    u = (d[1] * r[2] - d[2] * r[1],
         d[2] * r[0] - d[0] * r[2],
         d[0] * r[1] - d[1] * r[0])
    return d, r, u

LIGHT = (0.4, 0.85, 0.45)
LIGHT = tuple(x / math.sqrt(sum(y * y for y in LIGHT)) for x in LIGHT)
BG = (16, 20, 26)


def render(tris, W, H, cam_pos, cam_dir, cam_up, fovy=50.0, near=0.02):
    d, r, u = look_basis(cam_dir, cam_up)
    f = (H / 2.0) / math.tan(math.radians(fovy) / 2.0)
    depth = [1e30] * (W * H)
    img = bytearray(W * H * 3)
    for i in range(0, len(img), 3):
        img[i] = BG[0]; img[i + 1] = BG[1]; img[i + 2] = BG[2]

    def project(p):
        x = p[0] - cam_pos[0]; y = p[1] - cam_pos[1]; z = p[2] - cam_pos[2]
        zc = x * d[0] + y * d[1] + z * d[2]
        if zc < near:
            return None
        xc = x * r[0] + y * r[1] + z * r[2]
        yc = x * u[0] + y * u[1] + z * u[2]
        s = f / zc
        return (W / 2.0 + xc * s, H / 2.0 - yc * s, zc, 1.0 / zc)

    def draw(t, write_z, alpha_mix=None):
        a, b, c, n, col, _ = t
        pa, pb, pc = project(a), project(b), project(c)
        if pa is None or pb is None or pc is None:
            return
        ax, ay, az, ai = pa
        bx, by, bz, bi = pb
        cx, cy, cz, ci = pc
        x0 = max(0, int(min(ax, bx, cx)))
        x1 = min(W - 1, int(max(ax, bx, cx)))
        y0 = max(0, int(min(ay, by, cy)))
        y1 = min(H - 1, int(max(ay, by, cy)))
        if x1 < x0 or y1 < y0:
            return
        # double-sided flat shading
        nsign = 1.0 if (n[0] * -d[0] + n[1] * -d[1] + n[2] * -d[2]) > 0 else -1.0
        diff = max(0.0, nsign * (n[0] * LIGHT[0] + n[1] * LIGHT[1] + n[2] * LIGHT[2]))
        shade = 0.30 + 0.72 * diff
        cr = min(255, int(col[0] * 255 * shade))
        cg = min(255, int(col[1] * 255 * shade))
        cb = min(255, int(col[2] * 255 * shade))
        if alpha_mix is not None:
            cr = int(cr * alpha_mix + BG[0] * (1 - alpha_mix))
            cg = int(cg * alpha_mix + BG[1] * (1 - alpha_mix))
            cb = int(cb * alpha_mix + BG[2] * (1 - alpha_mix))
        # barycentric edge weights (unnormalised; sign-consistent pair)
        denom = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax)
        if denom == 0:
            return
        for y in range(y0, y1 + 1):
            row = y * W
            for x in range(x0, x1 + 1):
                u0 = (bx - x) * (cy - y) - (by - y) * (cx - x)
                u1 = (cx - x) * (ay - y) - (cy - y) * (ax - x)
                u2 = denom - u0 - u1
                if (u0 >= 0 and u1 >= 0 and u2 >= 0) or \
                   (u0 <= 0 and u1 <= 0 and u2 <= 0):
                    s = 1.0 / denom
                    z = (ai * u0 + bi * u1 + ci * u2) * s
                    if z >= 0 and (not write_z or z < depth[row + x]):
                        if write_z:
                            depth[row + x] = z
                        i3 = (row + x) * 3
                        img[i3] = cr; img[i3 + 1] = cg; img[i3 + 2] = cb

    for t in tris:
        if not t[5]:
            draw(t, write_z=True)
    for t in tris:
        if t[5]:
            draw(t, write_z=False, alpha_mix=0.35)
    return img


def write_png(path, W, H, img):
    raw = b"".join(b"\x00" + bytes(img[y * W * 3:(y + 1) * W * 3])
                   for y in range(H))

    def chunk(typ, payload):
        return (struct.pack(">I", len(payload)) + typ + payload +
                struct.pack(">I", zlib.crc32(typ + payload) & 0xFFFFFFFF))

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


# ------------------------------------------------------------------- views
# (label, cam_pos, cam_dir, cam_up, fovy). The lookdown/cockpit cameras sit
# at real contract points: the character's eye node (1.7 m, -Z forward) and
# the ship's seat.pilot node.

def views_for(asset_id, tris):
    # (label, cam_pos, cam_dir, cam_up, fovy); cam_dir points from cam_pos
    # at the model, so cam_pos + cam_dir ~= model centre.
    if asset_id == "char.player":
        return [
            ("front", (0.0, 0.9, -6.0), (0, 0, 1), (0, 1, 0), 35),
            ("three_quarter", (-3.4, 1.6, -4.6), (3.4, -0.8, 4.6), (0, 1, 0), 40),
            # own-body check: camera at the eye, pitched ~55 deg down
            ("lookdown", (0.0, 1.7, 0.25), (0, -0.9, -0.5), (0, 0.55, -0.84), 70),
        ]
    if asset_id == "ship.v1":
        return [
            ("three_quarter", (-6.5, 3.6, -8.0), (6.5, -2.4, 8.0), (0, 1, 0), 40),
            ("side", (9.5, 1.6, 0.0), (-1, 0, 0), (0, 1, 0), 40),
            # pilot view: camera at seat.pilot, looking out -Z
            ("cockpit", (0.0, 2.21, -1.9), (0, 0, -1), (0, 1, 0), 60),
        ]
    c = fit_center(tris)
    ext = max(max(abs(p[i] - c[i]) for p in [q for t in tris for q in t[:3]])
              for i in range(3))
    dist = ext * 3.2
    return [("three_quarter",
             (c[0] - dist * 0.6, c[1] + dist * 0.35, c[2] - dist * 0.8),
             (0.6, -0.35, 0.8), (0, 1, 0), 40)]


def main():
    cache = out_path("cache")
    os.makedirs(cache, exist_ok=True)
    targets = [
        ("char.player", "chars/player.glb"),
        ("ship.v1", "ships/v1.glb"),
        ("prop.rock.a", "props/rock_a.glb"),
        ("prop.rock.b", "props/rock_b.glb"),
        ("prop.rock.c", "props/rock_c.glb"),
    ]
    W, H = 400, 300
    for asset_id, rel in targets:
        tris = load_triangles(out_path(rel))
        frames = []
        for (label, cam_pos, cam_dir, cam_up, fovy) in views_for(asset_id, tris):
            img = render(tris, W, H, cam_pos, cam_dir, cam_up, fovy)
            frames.append((label, img))
            write_png(os.path.join(cache,
                                   f"{asset_id.replace('.', '_')}_{label}.png"),
                      W, H, img)
        sheet = bytearray(W * len(frames) * H * 3)
        for fi, (_, img) in enumerate(frames):
            for y in range(H):
                src = y * W * 3
                dst = (fi * H + y) * W * 3
                sheet[dst:dst + W * 3] = img[src:src + W * 3]
        p = os.path.join(cache, f"{asset_id.replace('.', '_')}_sheet.png")
        write_png(p, W * len(frames), H, sheet)
        print(f"{asset_id}: {len(frames)} views -> {p}")


if __name__ == "__main__":
    main()
