#!/usr/bin/env python3
"""Generate the Phase 12 artisan-loop props and hand tools (ROADMAP task 11).

Seven files from one script, like gen_kit.py:

    props/node_ore_iron.glb     prop.node.ore.iron   rock cluster, rust streaks
    props/node_ore_copper.glb   prop.node.ore.copper rock cluster, teal streaks
    props/node_crystal.glb      prop.node.crystal    hex prisms out of a rock
    props/wreck.glb             prop.wreck           hull section, snapped strut
    props/bench.glb             prop.bench           workbench with vise + rack
    weapons/drill.glb           tool.drill           hand drill, grip/muzzle
    weapons/cutter.glb          tool.cutter          plasma cutter, grip/muzzle

Contract (art/README.md): base at the origin, up +Y, faces -Z, flat-shaded
vertex colours under one white double-sided material, every model under
200 triangles. The hand tools follow weapon.pulse: -Z forward, the pistol
grip bottoming out at y=0, and two translation-only mount nodes -- `grip`
(the client lines it up with hand.r) and `muzzle` (the working tip at the
-Z end).

Deterministic: placements are written constants; the rock jitter comes from
glb.Rng with a fixed seed per node, so every clone regenerates byte-identical
files. No clock, no unseeded RNG.
"""
from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import Node, Rng, box, cap, face_normal, out_path, tube, write_glb

MATERIALS = [{"name": "opaque", "double_sided": True}]

# ---- palette ---------------------------------------------------------------
ROCK_DARK = (0.22, 0.21, 0.23)
ROCK_MID = (0.30, 0.29, 0.31)
IRON = (0.74, 0.36, 0.13)        # rust-orange iron streak
COPPER = (0.16, 0.64, 0.54)      # green-teal copper streak
CRYSTAL_PALE = (0.66, 0.90, 0.96)    # pale cyan facet
CRYSTAL_VIOLET = (0.62, 0.52, 0.90)  # violet facet
CRYSTAL_CORE = (0.88, 0.98, 1.00)    # the lit core: the tips and the inner shard

SCRAP = (0.52, 0.54, 0.57)
SCRAP_DARK = (0.34, 0.36, 0.39)
SCORCH = (0.12, 0.10, 0.09)

STEEL = (0.64, 0.66, 0.70)
NAVY = (0.12, 0.17, 0.36)        # bench legs
VISE = (0.28, 0.29, 0.32)

YELLOW = (0.93, 0.72, 0.10)
ORANGE = (0.91, 0.42, 0.09)
BIT = (0.72, 0.74, 0.78)
HANDLE = (0.15, 0.15, 0.17)


# ---- helpers ---------------------------------------------------------------

def _rot_x(p, a):
    c, s = math.cos(a), math.sin(a)
    return (p[0], p[1] * c - p[2] * s, p[1] * s + p[2] * c)


def _rot_z(p, a):
    c, s = math.cos(a), math.sin(a)
    return (p[0] * c - p[1] * s, p[0] * s + p[1] * c, p[2])


def xform(tris, fn):
    """Apply a point function to every vertex of a triangle list."""
    return [(fn(a), fn(b), fn(c), col) for (a, b, c, col) in tris]


def tilt_x(tris, deg):
    return xform(tris, lambda p: _rot_x(p, math.radians(deg)))


def tilt_z(tris, deg):
    return xform(tris, lambda p: _rot_z(p, math.radians(deg)))


def shift(tris, dx, dy, dz):
    return xform(tris, lambda p: (p[0] + dx, p[1] + dy, p[2] + dz))


def outward(tris, centre):
    """Fix every triangle's winding so its face normal points away from
    `centre` -- the same trick as glb.cap, applied to a whole hull, so the
    flat-shaded normals light from outside whichever way a ring was wound."""
    out = []
    for (a, b, c, col) in tris:
        n = face_normal(a, b, c)
        m = ((a[0] + b[0] + c[0]) / 3.0 - centre[0],
             (a[1] + b[1] + c[1]) / 3.0 - centre[1],
             (a[2] + b[2] + c[2]) / 3.0 - centre[2])
        if n[0] * m[0] + n[1] * m[1] + n[2] * m[2] < 0:
            b, c = c, b
        out.append((a, b, c, col))
    return out


# ---- ore nodes -------------------------------------------------------------

def chunk(rng, cx, cz, r, h, lean, streak, sides=6):
    """One angular rock: a jittered hexagonal base ring, a bulged mid ring,
    a narrow top ring and a peak, the whole upper half leaning by `lean`
    (dx, dz). 36 tris; roughly a quarter of the side facets take the
    streak colour, chosen by the seeded rng so the pattern is fixed."""
    step = 2.0 * math.pi / sides
    phase = rng.uniform(0.0, step)

    def ring(y, rad, ox, oz):
        pts = []
        for i in range(sides):
            a = phase + i * step
            k = rng.uniform(0.78, 1.22)
            pts.append((cx + ox + rad * k * math.cos(a), y,
                        cz + oz + rad * k * math.sin(a)))
        return pts

    base = ring(0.0, r, 0.0, 0.0)
    mid = ring(h * 0.55, r * 1.08, lean[0] * 0.5, lean[1] * 0.5)
    top = ring(h * 0.88, r * 0.45, lean[0], lean[1])
    peak = (cx + lean[0] * 1.15, h, cz + lean[1] * 1.15)

    sides_tris = tube(base, mid, None) + tube(mid, top, None)
    coloured = []
    for i, (a, b, c, _) in enumerate(sides_tris):
        tone = ROCK_MID if i % 3 == 0 else ROCK_DARK
        col = streak if rng.next() < 0.28 else tone
        coloured.append((a, b, c, col))
    lid = cap(top, peak, (0.0, 1.0, 0.0), ROCK_MID)
    floor = cap(base, (cx, 0.0, cz), (0.0, -1.0, 0.0), ROCK_DARK)
    centre = (cx + lean[0] * 0.4, h * 0.4, cz + lean[1] * 0.4)
    return outward(coloured + lid + floor, centre)


# (cx, cz, radius, height, lean) -- the big chunk sits toward -Z, the face.
IRON_CHUNKS = [
    (0.02, -0.12, 0.34, 1.00, (0.06, -0.08)),
    (-0.42, 0.18, 0.24, 0.62, (-0.10, 0.06)),
    (0.40, 0.22, 0.22, 0.56, (0.08, 0.10)),
    (0.14, 0.44, 0.15, 0.34, (0.02, 0.06)),
]

COPPER_CHUNKS = [
    (-0.10, 0.08, 0.32, 0.96, (-0.04, -0.10)),
    (0.40, -0.16, 0.25, 0.66, (0.10, -0.06)),
    (-0.36, -0.36, 0.16, 0.38, (-0.06, -0.04)),
]


# Crystal node (Phase 22, node.crystal): a squat two-chunk rock base and
# hex prisms leaning out of it -- (cx, cz, radius, height, lean_x, lean_z,
# body colour). The tallest stands toward -Z, the face, like the ores'.
CRYSTAL_BASE = [
    (0.0, 0.02, 0.42, 0.40, (0.04, -0.02)),
    (0.36, 0.26, 0.24, 0.26, (0.06, 0.05)),
]
CRYSTAL_PRISMS = [
    (0.02, -0.08, 0.13, 1.00, 0.06, -0.14, CRYSTAL_PALE),
    (-0.22, 0.06, 0.10, 0.78, -0.26, 0.02, CRYSTAL_VIOLET),
    (0.22, -0.02, 0.09, 0.70, 0.24, -0.10, CRYSTAL_PALE),
    (-0.06, 0.24, 0.09, 0.62, -0.06, 0.24, CRYSTAL_VIOLET),
    (0.38, 0.28, 0.06, 0.44, 0.18, 0.12, CRYSTAL_PALE),
]


def prism(cx, cz, r, h, lx, lz, body):
    """A six-sided crystal with a pointed tip, leaning by (lx, lz) at the
    tip. Its foot is buried in the base, so no floor cap: 18 tris. The
    facets alternate the body colour and the core colour -- the faint lit
    core reads through every other face -- and the tip is the core."""
    def ring(y, rad, ox, oz):
        return [(cx + ox + rad * math.cos(0.3 + i * math.pi / 3.0), y,
                 cz + oz + rad * math.sin(0.3 + i * math.pi / 3.0)) for i in range(6)]
    base = ring(0.0, r, 0.0, 0.0)
    shoulder = ring(h * 0.78, r * 0.92, lx * 0.78, lz * 0.78)
    side = tube(base, shoulder, None)
    side = [(a, b, c, CRYSTAL_CORE if (i // 2) % 3 == 1 else body)
            for i, (a, b, c, _) in enumerate(side)]
    tip = cap(shoulder, (cx + lx, h, cz + lz), (lx, 1.0, lz), CRYSTAL_CORE)
    return outward(side + tip, (cx + lx * 0.4, h * 0.4, cz + lz * 0.4))


def build_crystal():
    rng = Rng(0x22C7)
    tris = []
    for (cx, cz, r, h, lean) in CRYSTAL_BASE:
        tris += chunk(rng, cx, cz, r, h, lean, ROCK_MID)
    for (cx, cz, r, h, lx, lz, col) in CRYSTAL_PRISMS:
        tris += prism(cx, cz, r, h, lx, lz, col)
    return tris


def build_ore(chunks, streak, seed):
    rng = Rng(seed)
    tris = []
    for (cx, cz, r, h, lean) in chunks:
        tris += chunk(rng, cx, cz, r, h, lean, streak)
    return tris


# ---- wreck -----------------------------------------------------------------

LEAN = 18.0    # the panel leans back (+Z, away from the viewer)


def build_wreck():
    panel = []
    panel += box(-0.10, 0.50, 0.0, 1.50, 1.00, 0.08, SCRAP)          # main plate
    panel += shift(tilt_z(box(0.0, 0.0, 0.0, 0.46, 0.72, 0.08, SCRAP), 9.0),
                   0.86, 0.40, 0.0)                                  # broken shard
    for x in (-0.72, -0.34, 0.06, 0.46):                              # ribs
        panel += box(x, 0.50, -0.065, 0.06, 0.94, 0.05, SCRAP_DARK)
    panel += box(-0.10, 0.64, -0.045, 1.44, 0.22, 0.012, SCORCH)     # scorch band
    panel = shift(tilt_x(panel, LEAN), 0.0, 0.015, 0.0)  # lean, then lift the
    # tilted plate's low edge to y=0 (base at origin)

    strut = shift(tilt_z(box(0.0, 0.0, 0.0, 0.09, 0.78, 0.09, SCRAP_DARK), 34.0),
                  -0.90, 0.35, 0.08)                                 # snapped strut
    snap = shift(tilt_z(box(0.0, 0.0, 0.0, 0.09, 0.16, 0.09, SCORCH), 62.0),
                 -1.14, 0.70, 0.08)                                  # its broken tip
    rubble = box(0.62, 0.03, -0.36, 0.48, 0.06, 0.34, SCRAP_DARK, angle=0.45)
    return panel + strut + snap + rubble


# ---- bench -----------------------------------------------------------------

def build_bench():
    t = []
    t += box(-0.62, 0.41, 0.0, 0.22, 0.82, 0.58, NAVY)      # legs
    t += box(0.62, 0.41, 0.0, 0.22, 0.82, 0.58, NAVY)
    t += box(0.0, 0.32, 0.06, 1.02, 0.04, 0.46, NAVY)       # shelf
    t += box(0.0, 0.86, 0.0, 1.60, 0.08, 0.70, STEEL)       # slab top
    t += box(0.56, 0.96, -0.14, 0.16, 0.12, 0.18, VISE)     # vise body
    t += box(0.56, 0.95, -0.29, 0.16, 0.10, 0.06, VISE)     # vise jaw
    t += box(0.56, 0.99, -0.37, 0.024, 0.024, 0.12, STEEL)  # vise screw
    t += box(-0.72, 1.06, 0.26, 0.06, 0.32, 0.06, NAVY)     # rack post
    t += box(-0.56, 1.21, 0.26, 0.40, 0.05, 0.06, NAVY)     # rack bar
    t += box(-0.66, 1.15, 0.22, 0.02, 0.08, 0.02, STEEL)    # pegs
    t += box(-0.46, 1.15, 0.22, 0.02, 0.08, 0.02, STEEL)
    return t


# ---- hand tools ------------------------------------------------------------

def taper(z0, r0, z1, cy, color, sides=4):
    """Square spike along -Z: a ring of `sides` at z0 closing to a point at
    z1. `sides` side tris + `sides` back-cap tris."""
    step = 2.0 * math.pi / sides
    ring = [(r0 * math.cos(i * step + step / 2.0),
             cy + r0 * math.sin(i * step + step / 2.0), z0)
            for i in range(sides)]
    tip = (0.0, cy, z1)
    tris = cap(ring, tip, (0.0, 0.0, -1.0), color)
    tris += cap(ring, (0.0, cy, z0), (0.0, 0.0, 1.0), color)
    return tris


DRILL_GRIP = (0.0, 0.035, 0.03)
DRILL_MUZZLE = (0.0, 0.085, -0.35)


def build_drill():
    t = []
    t += box(0.0, 0.085, -0.04, 0.08, 0.07, 0.28, YELLOW)     # body
    t += box(0.0, 0.035, 0.03, 0.05, 0.07, 0.06, HANDLE)      # pistol grip
    t += box(0.0, 0.05, -0.02, 0.02, 0.03, 0.02, HANDLE)      # trigger
    t += box(0.0, 0.10, 0.11, 0.07, 0.05, 0.06, HANDLE)       # motor cap
    t += box(0.0, 0.085, -0.205, 0.05, 0.05, 0.05, BIT)       # chuck
    t += taper(-0.23, 0.014, -0.35, 0.085, BIT)               # bit
    t += box(0.0, 0.125, -0.04, 0.03, 0.01, 0.20, HANDLE)     # top strip
    return t


CUTTER_GRIP = (0.0, 0.035, 0.06)
CUTTER_MUZZLE = (0.0, 0.085, -0.30)


def build_cutter():
    t = []
    t += box(0.0, 0.085, -0.04, 0.09, 0.08, 0.22, ORANGE)     # body
    t += box(0.0, 0.035, 0.06, 0.05, 0.07, 0.06, HANDLE)      # rear grip
    t += box(0.075, 0.09, -0.02, 0.06, 0.03, 0.03, HANDLE)    # side handle
    t += box(0.0, 0.14, -0.02, 0.05, 0.03, 0.14, HANDLE)      # gas cell
    t += box(0.0, 0.085, -0.18, 0.05, 0.05, 0.06, BIT)        # nozzle collar
    t += taper(-0.21, 0.016, -0.30, 0.085, BIT)               # nozzle
    t += box(0.0, 0.05, -0.02, 0.02, 0.03, 0.02, HANDLE)      # trigger
    return t


# ---- output ----------------------------------------------------------------

def _tool(asset_id, tris, grip, muzzle):
    node = Node(asset_id)
    node.add("opaque", tris)
    node.child("grip", grip)
    node.child("muzzle", muzzle)
    return node


def _prop(asset_id, tris):
    node = Node(asset_id)
    node.add("opaque", tris)
    return node


FILES = {
    "props/node_ore_iron.glb":
        lambda: _prop("prop.node.ore.iron", build_ore(IRON_CHUNKS, IRON, 0x1207)),
    "props/node_ore_copper.glb":
        lambda: _prop("prop.node.ore.copper", build_ore(COPPER_CHUNKS, COPPER, 0x12C0)),
    "props/node_crystal.glb": lambda: _prop("prop.node.crystal", build_crystal()),
    "props/wreck.glb": lambda: _prop("prop.wreck", build_wreck()),
    "props/bench.glb": lambda: _prop("prop.bench", build_bench()),
    "weapons/drill.glb":
        lambda: _tool("tool.drill", build_drill(), DRILL_GRIP, DRILL_MUZZLE),
    "weapons/cutter.glb":
        lambda: _tool("tool.cutter", build_cutter(), CUTTER_GRIP, CUTTER_MUZZLE),
}


def main():
    for rel, build in FILES.items():
        node = build()
        n = write_glb(out_path(*rel.split("/")), node, MATERIALS)
        print(f"{rel}: {n} tris (budget 200)")
        if n >= 200:
            raise SystemExit(f"{rel}: {n} tris breaks the 200-tri budget")


if __name__ == "__main__":
    main()
