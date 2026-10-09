#!/usr/bin/env python3
"""Generate the world props that used to come from Kenney's Space Kit.

Eleven files from one script, in the style of gen_nodes.py / gen_kit.py:

    props/rock_a.glb       prop.rock.a        rounded boulder
    props/rock_b.glb       prop.rock.b        broken slab cluster
    props/rock_c.glb       prop.rock.c        boulder with crystals
    props/loot_crate.glb   prop.loot.crate    dropped-loot case
    props/barrel.glb       prop.barrel        fuel drum
    props/barrels.glb      prop.barrels       two drums standing, one fallen
    props/generator.glb    prop.generator     skid-mounted generator
    props/dish.glb         prop.dish          comms dish on a tripod
    props/bones.glb        prop.bones         skull, ribs, long bones
    vehicles/rover.v1.glb  vehicle.rover.v1   open buggy with seat mounts
    props/forge.glb        prop.forge         scrapyard forge: crucible, anvil

Contract (art/README.md): base at the origin, up +Y, faces -Z, flat-shaded
vertex colours under one white double-sided material. Rocks are exactly
1.0 m tall (the client scales them 0.3-1.5 m). The rover keeps its seat
contract: `seat.driver` and `seat.passenger.0` empties at the GDD eye
points, art frame.

Deterministic: fixed seeds, no clock.
"""
from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import Node, Rng, box, cap, icosphere, out_path, tube, write_glb  # noqa: E402
from gen_nodes import MATERIALS, _rot_x, _rot_z, chunk, outward, shift, xform  # noqa: E402

# ---- palette ---------------------------------------------------------------
ROCK_DARK = (0.24, 0.22, 0.24)
ROCK_MID = (0.33, 0.31, 0.32)
ROCK_DUST = (0.42, 0.37, 0.33)
CRYSTAL = (0.30, 0.78, 0.86)
CRYSTAL_DEEP = (0.16, 0.45, 0.62)
STEEL = (0.46, 0.49, 0.53)
STEEL_DARK = (0.26, 0.28, 0.31)
RUBBER = (0.10, 0.10, 0.11)
DRUM = (0.70, 0.30, 0.16)
DRUM_DARK = (0.48, 0.20, 0.11)
HAZARD = (0.92, 0.70, 0.12)
OLIVE = (0.42, 0.46, 0.33)
OLIVE_DARK = (0.30, 0.33, 0.24)
GLOW = (0.35, 0.85, 0.95)
GREEN_LIGHT = (0.35, 0.95, 0.45)
LAMP = (1.0, 0.92, 0.65)
BONE = (0.86, 0.82, 0.70)
BONE_DARK = (0.62, 0.57, 0.47)
SEAT = (0.20, 0.18, 0.17)
# Scrapyard (GDD "World art style guide"): rust #8C4A2F, scorch #3A2E28,
# hazard #D9A013, the flame-amber #FFAE19 glow.
RUST = (0.55, 0.29, 0.18)
RUST_DARK = (0.42, 0.21, 0.13)
SCORCH = (0.23, 0.18, 0.16)
HAZARD_SY = (0.85, 0.63, 0.07)
FLAME = (1.0, 0.68, 0.10)
EMBER = (1.0, 0.45, 0.08)
ANVIL = (0.20, 0.21, 0.24)
WOOD = (0.42, 0.29, 0.18)
WOOD_DARK = (0.30, 0.20, 0.12)
LEATHER = (0.36, 0.22, 0.14)


# ---- shapes ----------------------------------------------------------------
def ring(cx, y, cz, r, sides, phase=0.0):
    return [(cx + r * math.cos(phase + 2 * math.pi * i / sides), y,
             cz + r * math.sin(phase + 2 * math.pi * i / sides)) for i in range(sides)]


def cyl(cx, y0, cz, r, h, sides, col, top=None, bottom=None):
    """Vertical cylinder from y0 to y0 + h, capped."""
    a, b = ring(cx, y0, cz, r, sides), ring(cx, y0 + h, cz, r, sides)
    t = tube(a, b, col)
    t += cap(b, (cx, y0 + h, cz), (0, 1, 0), top or col)
    t += cap(a, (cx, y0, cz), (0, -1, 0), bottom or col)
    return outward(t, (cx, y0 + h / 2, cz))


def cyl_x(cx, cy, cz, r, w, sides, col, cap_col=None):
    """Cylinder lying along X (a wheel, a fallen drum), centred."""
    t = cyl(0, -w / 2, 0, r, w, sides, col, cap_col, cap_col)
    return shift(xform(t, lambda p: _rot_z(p, math.pi / 2)), cx, cy, cz)


def beam(p0, p1, thick, col):
    """A square strut from p0 to p1 (a box along Y, turned onto the segment)."""
    d = [p1[i] - p0[i] for i in range(3)]
    ln = math.sqrt(sum(v * v for v in d))
    d = [v / ln for v in d]
    t = box(0, ln / 2, 0, thick, ln, thick, col)
    ax = (d[2], 0.0, -d[0])                       # (0,1,0) x d
    s_ = math.sqrt(ax[0] ** 2 + ax[2] ** 2)
    if s_ > 1e-9:
        k = (ax[0] / s_, 0.0, ax[2] / s_)
        c, s2 = d[1], s_                           # cos, sin of the turn

        def rot(p):                                # Rodrigues about unit k
            kx, ky, kz = k
            dot = kx * p[0] + ky * p[1] + kz * p[2]
            cr = (ky * p[2] - kz * p[1], kz * p[0] - kx * p[2], kx * p[1] - ky * p[0])
            return tuple(p[i] * c + cr[i] * s2 + k[i] * dot * (1 - c) for i in range(3))
        t = xform(t, rot)
    return shift(t, *p0)


def normalise(tris, height):
    """Uniform scale so the top sits at `height`."""
    top = max(p[1] for t in tris for p in t[:3])
    k = height / top
    return [tuple((p[0] * k, p[1] * k, p[2] * k) for p in t[:3]) + (t[3],) for t in tris]


def boulder(rng, r, squash, seed_jitter=0.18, cx=0.0, cz=0.0):
    """A jittered icosphere with a flat base: a rounded rock, 80 faces."""
    V, F = icosphere(1.0, 1)
    pts = []
    for (x, y, z) in V:
        k = 1.0 + rng.uniform(-seed_jitter, seed_jitter)
        pts.append((x * r * k, y * r * k * squash, z * r * k))
    floor = -0.45 * r * squash
    pts = [(x + cx, max(y, floor) - floor, z + cz) for (x, y, z) in pts]
    tris = []
    for (a, b, c) in F:
        pa, pb, pc = pts[a], pts[b], pts[c]
        up = (pa[1] + pb[1] + pc[1]) / 3 / (r * squash * 1.4)
        col = ROCK_DUST if up > 0.85 else ROCK_MID if rng.next() < 0.45 else ROCK_DARK
        tris.append((pa, pb, pc, col))
    return outward(tris, (cx, r * squash * 0.5, cz))


def crystal(cx, cz, r, h, lean_x, lean_z, col):
    """Six-sided prism with a pointed tip, leaning."""
    base = ring(cx, 0.0, cz, r, 6, 0.3)
    mid = ring(cx + lean_x * 0.7, h * 0.75, cz + lean_z * 0.7, r * 0.9, 6, 0.3)
    tip = (cx + lean_x, h, cz + lean_z)
    t = tube(base, mid, col) + cap(mid, tip, (0, 1, 0), CRYSTAL)
    return outward(t, (cx, h * 0.4, cz))


# ---- rocks -----------------------------------------------------------------
def rock_a():
    return normalise(boulder(Rng(0xA0C1), 0.95, 0.72), 1.0)


def rock_b():
    rng = Rng(0xB0C2)
    t = []
    for (cx, cz, r, h, lean) in ((0.0, 0.0, 0.85, 1.0, (0.12, -0.05)),
                                 (-0.95, 0.55, 0.55, 0.62, (-0.15, 0.08)),
                                 (0.90, 0.65, 0.45, 0.45, (0.10, 0.12))):
        t += chunk(rng, cx, cz, r, h, lean, ROCK_DUST, sides=7)
    return normalise(t, 1.0)


def rock_c():
    rng = Rng(0xC0C3)
    t = boulder(rng, 0.9, 0.55)
    for (cx, cz, r, h, lx, lz, col) in ((0.15, -0.20, 0.13, 1.45, 0.10, -0.15, CRYSTAL),
                                       (-0.25, -0.05, 0.10, 1.10, -0.20, -0.05, CRYSTAL_DEEP),
                                       (0.40, 0.15, 0.09, 0.95, 0.20, 0.05, CRYSTAL),
                                       (-0.05, 0.30, 0.08, 0.80, 0.0, 0.18, CRYSTAL_DEEP)):
        t += crystal(cx, cz, r, h, lx, lz, col)
    return normalise(t, 1.0)


# ---- containers ------------------------------------------------------------
def loot_crate():
    t = []
    t += box(0, 0.22, 0, 0.40, 0.40, 0.32, OLIVE)                    # body
    t += box(0, 0.45, 0, 0.42, 0.08, 0.34, OLIVE_DARK)               # lid
    for sx in (-1, 1):                                               # corner posts
        for sz in (-1, 1):
            t += box(sx * 0.195, 0.24, sz * 0.155, 0.04, 0.48, 0.04, STEEL_DARK)
    t += box(0, 0.22, -0.165, 0.30, 0.04, 0.01, GLOW)                # glowing seal, front
    t += box(0, 0.49, -0.08, 0.14, 0.02, 0.04, STEEL)                # handle
    for sx in (-1, 1):
        t += box(sx * 0.21, 0.30, 0, 0.02, 0.05, 0.12, STEEL)        # side grips
    return t


def drum(cx, cz, col=DRUM, h=0.92, r=0.30):
    t = cyl(cx, 0.0, cz, r, h, 12, col, top=DRUM_DARK, bottom=DRUM_DARK)
    for y in (0.24, 0.62):                                           # rolling hoops
        t += cyl(cx, y, cz, r + 0.015, 0.035, 12, DRUM_DARK)
    t += cyl(cx, 0.40, cz, r + 0.006, 0.10, 12, HAZARD)              # hazard band
    t += cyl(cx + 0.14, h, cz - 0.08, 0.04, 0.025, 6, STEEL)         # bung
    return t


def barrel():
    return drum(0, 0)


def barrels():
    t = drum(-0.33, 0.12) + drum(0.33, 0.18, col=(0.32, 0.45, 0.58))
    fallen = drum(0, 0)
    fallen = xform(fallen, lambda p: _rot_z((p[0], p[1] - 0.46, p[2]), math.pi / 2))
    return t + shift(fallen, 0.05, 0.30, -0.55)


# ---- machines --------------------------------------------------------------
def generator():
    t = []
    for sz in (-1, 1):                                               # skids
        t += box(0, 0.05, sz * 0.38, 2.0, 0.10, 0.10, STEEL_DARK)
    t += box(-0.15, 0.55, 0, 1.40, 0.90, 0.85, OLIVE)                # housing
    for i in range(5):                                               # vent slats, front
        t += box(-0.45 + i * 0.12, 0.62, -0.43, 0.07, 0.50, 0.02, OLIVE_DARK)
    t += box(0.30, 0.65, -0.43, 0.36, 0.30, 0.02, STEEL_DARK)        # control panel
    t += box(0.20, 0.72, -0.445, 0.06, 0.06, 0.01, GREEN_LIGHT)
    t += box(0.36, 0.72, -0.445, 0.06, 0.06, 0.01, HAZARD)
    t += cyl_x(0.85, 0.45, 0.0, 0.26, 0.55, 10, DRUM, STEEL_DARK)    # fuel tank
    t += cyl(-0.55, 1.0, 0.20, 0.06, 0.30, 8, STEEL_DARK)            # exhaust stack
    t += cyl(-0.55, 1.30, 0.20, 0.09, 0.04, 8, RUBBER)
    t += box(-0.15, 1.02, 0, 1.30, 0.04, 0.78, OLIVE_DARK)           # roof plate
    t += box(-0.15, 1.12, -0.20, 0.08, 0.16, 0.08, LAMP)             # work lamp
    return t


def dish():
    t = []
    hub_y = 1.20
    for k in range(3):                                               # tripod legs
        a = 2 * math.pi * k / 3 + math.pi / 6
        fx, fz = 0.85 * math.cos(a), 0.85 * math.sin(a)
        t += beam((fx, 0.0, fz), (0.0, hub_y - 0.05, 0.0), 0.06, STEEL_DARK)
        t += box(fx, 0.03, fz, 0.16, 0.06, 0.16, STEEL_DARK)          # feet
    t += cyl(0, hub_y - 0.1, 0, 0.12, 0.25, 8, STEEL)                # head unit
    # Dish: a shallow bowl of three rings, tilted up and toward -Z.
    rings = [(0.0, 0.10), (0.08, 0.55), (0.20, 0.95)]
    bowl = []
    prev = None
    for (y, r) in rings:
        cur = ring(0, y, 0, r, 12)
        if prev is not None:
            bowl += tube(prev, cur, (0.82, 0.84, 0.86))
        prev = cur
    bowl += cap(ring(0, 0.0, 0, 0.10, 12), (0, -0.02, 0), (0, -1, 0), STEEL)
    bowl += [(c, b, a, STEEL) for (a, b, c, _) in bowl]              # back side, darker
    bowl += cyl(0, 0.0, 0, 0.025, 0.55, 6, STEEL_DARK)               # feed arm
    bowl += cyl(0, 0.55, 0, 0.05, 0.08, 6, GLOW)                     # feed horn
    bowl = xform(bowl, lambda p: _rot_x(p, -math.radians(35)))
    t += shift(bowl, 0, hub_y + 0.18, 0)
    return t


# ---- bones -----------------------------------------------------------------
def bones():
    t = []
    V, F = icosphere(1.0, 0)
    skull = [tuple((V[i][0] * 0.11, V[i][1] * 0.09 + 0.09, V[i][2] * 0.13 - 0.05) for i in f) + (BONE,) for f in F]
    t += shift(outward(skull, (0, 0.09, -0.05)), 0.22, 0, -0.10)
    t += box(0.22, 0.10, -0.20, 0.035, 0.025, 0.01, RUBBER)          # eye sockets
    t += box(0.22, 0.04, -0.17, 0.14, 0.035, 0.06, BONE_DARK)        # jaw
    for i in range(5):                                               # ribs: arcs of three segments
        z = -0.12 + i * 0.06
        for (x0, y0, x1, y1) in ((-0.30, 0.02, -0.22, 0.14), (-0.22, 0.14, -0.08, 0.17), (-0.08, 0.17, 0.0, 0.06)):
            mx, my = (x0 + x1) / 2, (y0 + y1) / 2
            ln = math.hypot(x1 - x0, y1 - y0)
            seg = box(0, 0, 0, ln, 0.02, 0.02, BONE)
            seg = xform(seg, lambda p, a=math.atan2(y1 - y0, x1 - x0): _rot_z(p, a))
            t += shift(seg, mx - 0.05, my, z)
    t += box(-0.10, 0.02, 0.0, 0.03, 0.03, 0.34, BONE_DARK)          # spine
    for (x, z, a) in ((0.05, 0.22, 0.4), (-0.30, 0.20, -0.7)):       # long bones with knobs
        lb = box(0, 0.025, 0, 0.36, 0.03, 0.03, BONE)
        lb += box(-0.18, 0.03, 0, 0.05, 0.05, 0.05, BONE_DARK) + box(0.18, 0.03, 0, 0.05, 0.05, 0.05, BONE_DARK)
        lb = xform(lb, lambda p, a=a: (p[0] * math.cos(a) - p[2] * math.sin(a), p[1], p[0] * math.sin(a) + p[2] * math.cos(a)))
        t += shift(lb, x, 0, z)
    return t


# ---- rover -----------------------------------------------------------------
ROVER_SEATS = {"seat.driver": (0.35, 1.55, -0.1), "seat.passenger.0": (-0.35, 1.55, 0.4),
               # The client grows a speed bar from here along +X, on the gauge
               # panel's driver-facing side (its face is z -0.585).
               "dash.speed": (0.12, 1.29, -0.585),
               # Where the driver's hands go: either side of the rim.
               "wheel.l": (0.18, 1.15, -0.40), "wheel.r": (0.52, 1.15, -0.40)}


def rover():
    t = []
    t += box(0, 0.50, 0.05, 1.30, 0.30, 2.10, OLIVE)                 # tub
    t += box(0, 0.36, 0.05, 1.10, 0.10, 2.20, STEEL_DARK)            # belly pan
    t += box(0, 0.70, -0.85, 1.32, 0.12, 0.40, OLIVE_DARK)           # bonnet
    t += box(0, 0.48, -1.04, 1.20, 0.16, 0.08, STEEL_DARK)           # bumper
    for sx in (-1, 1):
        t += box(sx * 0.45, 0.58, -1.08, 0.18, 0.10, 0.03, LAMP)     # headlights
        t += box(sx * 0.55, 0.58, 1.11, 0.12, 0.06, 0.02, (0.9, 0.2, 0.15))  # tail lights
    for (x, z) in ((0.78, -0.72), (-0.78, -0.72), (0.78, 0.80), (-0.78, 0.80)):
        t += cyl_x(x, 0.38, z, 0.38, 0.26, 10, RUBBER, STEEL)        # wheels
        t += box(x * 0.95, 0.80, z, 0.30, 0.04, 0.70, OLIVE_DARK)    # fenders
    for (x, z) in ((0.35, -0.1), (-0.35, 0.4)):                     # under the seat mounts
        t += box(x, 0.74, z + 0.05, 0.42, 0.10, 0.42, SEAT)          # seat pan
        t += box(x, 0.98, z + 0.26, 0.42, 0.42, 0.08, SEAT)          # back
    t += box(0, 0.93, -0.66, 1.18, 0.30, 0.08, OLIVE_DARK)           # dash
    # Gauge panel on a stalk over the dash, ~25 degrees under the driver's
    # eye: the speed bar has to clear the HUD's hotbar at a level look.
    t += box(0.35, 1.29, -0.61, 0.54, 0.12, 0.05, STEEL_DARK)          # gauge panel
    t += box(0.35, 1.14, -0.63, 0.05, 0.20, 0.04, STEEL_DARK)          # its stalk
    # Steering wheel: a rim of struts and two spokes, its face tipped 60
    # degrees back toward the driver (the first one was a disc on X --
    # edge-on to the driver, like a fifth road wheel).
    hub = (0.35, 1.15, -0.40)
    tilt = lambda p: _rot_x(p, math.radians(60))
    rim = [(0.17 * math.cos(2 * math.pi * k / 10), 0.0, 0.17 * math.sin(2 * math.pi * k / 10)) for k in range(10)]
    for k in range(10):
        a, b = tilt(rim[k]), tilt(rim[(k + 1) % 10])
        t += beam(tuple(hub[i] + a[i] for i in range(3)), tuple(hub[i] + b[i] for i in range(3)), 0.03, RUBBER)
    for k in (0, 5):
        a = tilt(rim[k])
        t += beam(hub, tuple(hub[i] + a[i] for i in range(3)), 0.02, STEEL)
    col = tilt((0.0, -0.32, 0.0))
    t += beam(tuple(hub[i] + col[i] for i in range(3)), hub, 0.04, STEEL_DARK)  # steering column
    # Roll cage: two hoops and two rails, ABOVE the 1.55 m seat eye -- at
    # 1.40 the driver's camera sat over the rails, perched on the roof.
    # The front hoop stands ahead of the driver (eye z -0.10), not beside
    # the head, so a glance sideways is not a face full of post.
    for z in (-0.60, 0.75):
        for sx in (-1, 1):
            t += box(sx * 0.66, 1.25, z, 0.06, 1.10, 0.06, STEEL)
        t += box(0, 1.80, z, 1.38, 0.06, 0.06, STEEL)
    for sx in (-1, 1):
        t += box(sx * 0.66, 1.80, 0.075, 0.06, 0.06, 1.41, STEEL)
    t += box(0, 0.75, 0.95, 1.10, 0.20, 0.30, STEEL_DARK)            # cargo rack
    return t


# ---- forge -----------------------------------------------------------------
def forge():
    """Phase 22's scrapyard forge (npc.forge, kind `forge`). 2.2 x 1.4 m,
    under 1.6 m: a brick crucible at -X with its glowing mouth on the -Z
    face, a bellows on its +X flank, an anvil on a stump in front of the
    work ledge at +X. Anvil face and ledge sit at ~0.9 m, the bench's slab
    height, so the server's bench aim point (0.9 m) lands on the work. The
    crooked thing: the scrap chimney leans off the crucible's back."""
    t = []
    # Scorched ground plate under the lot -- hides the seam with the dirt.
    t += box(0.0, 0.02, 0.0, 2.16, 0.04, 1.36, SCORCH)
    # Crucible: brick courses, each a touch off the last (hand-laid).
    fx, fz = -0.52, 0.06
    for i, (w, d, dx, col) in enumerate(((1.00, 0.96, 0.00, RUST),
                                         (0.96, 0.92, 0.02, RUST_DARK),
                                         (0.94, 0.90, -0.02, RUST),
                                         (0.90, 0.86, 0.01, RUST_DARK))):
        t += box(fx + dx, 0.04 + 0.11 + i * 0.22, fz, w, 0.22, d, col, angle=0.03 * (i - 1.5))
    t += box(fx, 0.97, fz, 0.98, 0.06, 0.94, SCORCH)                 # coping
    # The mouth on the -Z face: scorch arch, glowing heart, hazard sill.
    mz = fz - 0.43
    t += box(fx, 0.48, mz - 0.02, 0.58, 0.46, 0.04, SCORCH)
    t += box(fx, 0.46, mz - 0.045, 0.42, 0.32, 0.02, FLAME)
    t += box(fx, 0.38, mz - 0.06, 0.30, 0.10, 0.02, EMBER)          # coals
    t += box(fx, 0.22, mz - 0.07, 0.62, 0.06, 0.12, HAZARD_SY)       # sill
    # Crucible lip on top with molten metal.
    t += cyl(fx, 1.00, fz - 0.08, 0.26, 0.12, 8, STEEL_DARK)
    t += cyl(fx, 1.12, fz - 0.08, 0.21, 0.01, 8, FLAME)
    # Chimney: a scrap pipe off the back corner, leaning back and out.
    pipe = cyl(0, 0, 0, 0.12, 0.62, 8, RUST_DARK, top=SCORCH)
    pipe += cyl(0, 0.40, 0, 0.135, 0.06, 8, STEEL_DARK)              # a strap
    pipe = xform(pipe, lambda p: _rot_z(_rot_x(p, math.radians(12)), math.radians(14)))
    t += shift(pipe, fx - 0.16, 0.95, fz + 0.30)
    # Bellows on the crucible's +X flank: boards, leather, a nozzle.
    bx = fx + 0.62
    t += box(bx, 0.36, 0.20, 0.22, 0.04, 0.46, WOOD, angle=0.0)
    t += box(bx, 0.52, 0.20, 0.20, 0.04, 0.44, WOOD)
    t += box(bx, 0.44, 0.22, 0.18, 0.12, 0.36, LEATHER)
    t += box(bx - 0.10, 0.44, 0.00, 0.10, 0.05, 0.05, STEEL_DARK)   # nozzle
    t += box(bx, 0.20, 0.20, 0.10, 0.32, 0.10, WOOD_DARK)           # stand
    # Anvil on a stump, front of the ledge.
    ax, az = 0.52, -0.20
    t += cyl(ax, 0.04, az, 0.22, 0.52, 8, WOOD, top=WOOD_DARK)
    t += box(ax, 0.62, az, 0.34, 0.08, 0.24, ANVIL)                  # foot
    t += box(ax, 0.72, az, 0.18, 0.12, 0.14, ANVIL)                  # waist
    t += box(ax - 0.02, 0.84, az, 0.42, 0.12, 0.20, ANVIL)           # face
    horn_base = [(0.0, 0.07 * math.cos(math.pi / 4 + k * math.pi / 2),
                  0.07 * math.sin(math.pi / 4 + k * math.pi / 2)) for k in range(4)]
    t += shift(cap(horn_base, (0.20, 0.0, 0.0), (1, 0, 0), ANVIL), ax + 0.19, 0.86, az)  # horn
    t += box(ax + 0.05, 0.92, az, 0.20, 0.03, 0.03, STEEL)           # a hammer on it
    t += box(ax + 0.14, 0.93, az, 0.06, 0.05, 0.06, STEEL_DARK)
    # Work ledge along the back: a plank on two posts, an ingot, a plate.
    lx, lz = 0.62, 0.46
    for dx in (-0.36, 0.36):
        t += box(lx + dx, 0.43, lz, 0.08, 0.86, 0.08, STEEL_DARK)
    t += box(lx, 0.89, lz, 0.90, 0.06, 0.40, WOOD)
    t += box(lx, 0.30, lz, 0.80, 0.04, 0.30, WOOD_DARK)              # low shelf
    t += box(lx - 0.20, 0.95, lz, 0.18, 0.06, 0.08, RUST)            # ingot
    t += box(lx + 0.18, 0.93, lz + 0.02, 0.28, 0.02, 0.22, STEEL, angle=0.2)  # plate
    t += box(lx + 0.36, 0.33, lz, 0.14, 0.02, 0.18, HAZARD_SY)       # tongs on the shelf
    # Quench bucket at the front corner.
    t += cyl(0.98, 0.04, -0.48, 0.12, 0.30, 8, STEEL_DARK, top=(0.12, 0.16, 0.20))
    return t


def _prop(asset_id, tris, mounts=None):
    node = Node(asset_id)
    node.add("opaque", tris)
    for name, p in (mounts or {}).items():
        node.child(name, p)
    return node


FILES = {
    "props/rock_a.glb": (lambda: _prop("prop.rock.a", rock_a()), 400),
    "props/rock_b.glb": (lambda: _prop("prop.rock.b", rock_b()), 400),
    "props/rock_c.glb": (lambda: _prop("prop.rock.c", rock_c()), 400),
    "props/loot_crate.glb": (lambda: _prop("prop.loot.crate", loot_crate()), 400),
    "props/barrel.glb": (lambda: _prop("prop.barrel", barrel()), 400),
    "props/barrels.glb": (lambda: _prop("prop.barrels", barrels()), 800),
    "props/generator.glb": (lambda: _prop("prop.generator", generator()), 800),
    "props/dish.glb": (lambda: _prop("prop.dish", dish()), 800),
    "props/bones.glb": (lambda: _prop("prop.bones", bones()), 800),
    "props/forge.glb": (lambda: _prop("prop.forge", forge()), 800),
    "vehicles/rover.v1.glb": (lambda: _prop("vehicle.rover.v1", rover(), ROVER_SEATS), 1500),
}


def main():
    for rel, (build, budget) in FILES.items():
        n = write_glb(out_path(*rel.split("/")), build(), MATERIALS)
        print(f"{rel}: {n} tris (budget {budget})")
        if n > budget:
            raise SystemExit(f"{rel}: {n} tris breaks the {budget}-tri budget")


if __name__ == "__main__":
    main()
