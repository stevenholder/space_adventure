#!/usr/bin/env python3
"""Generate the pulse rifle -- weapons/pulse.glb -- the one weapon model both
first and third person hold.

Replaces the Kenney Space Kit blaster: a stubby toy whose stock led when it
was held, however the mounts were measured. This one is authored in the
frame the client assumes for every weapon (art/README.md): -Z forward, +Y up,
X centred, the pistol grip's top at `GRIP` and the barrel's tip at `MUZZLE`.
`AlignToForearm` (client) turns the grip->muzzle line onto the wearer's
forearm, so GRIP sits just under the bore and the line runs along the barrel.

Pure glb.py boxes, same as the drill and cutter: no Blender, deterministic,
rebuilt by `npm run gen`. ~0.64 m long, well under the 300-tri budget.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import box, out_path, write_glb
from gen_nodes import MATERIALS, _tool, tilt_x

# Palette: the Scrapyard Comic rifle the box model had (client Models.Rifle).
# Display colours, written straight into COLOR_0 like every glb.py asset
# (the client reads them that way; converting to linear rendered it black).
BODY = (0.46, 0.49, 0.53)     # gunmetal receiver and stock
METAL = (0.30, 0.31, 0.34)    # barrel, sights, magazine
ACCENT = (0.35, 0.66, 0.85)   # charge rail, front post
GRIP_C = (0.22, 0.21, 0.21)   # polymer furniture

# Contact points. The client puts `grip` in the right fist (hand.r) and
# `fore` on the left palm (hand.l), so the rifle sits IN both hands; `sight`
# is the rear notch first person lines up with the eye; `muzzle` is where
# shots are drawn from. Keep human.py FORE_ALONG/FORE_UP equal to fore - grip.
GRIP = (0.0, 0.06, 0.005)      # middle of the pistol grip, where a fist closes
FORE = (0.0, 0.115, -0.29)     # under the fore-end, where a palm supports it
SIGHT = (0.0, 0.22, -0.02)     # rear sight notch
FRONT = (0.0, 0.22, -0.37)     # front post tip: SIGHT -> FRONT is the line of sight
MUZZLE = (0.0, 0.155, -0.425)  # barrel tip


def build_rifle():
    t = []
    t += box(0.0, 0.13, 0.14, 0.05, 0.09, 0.12, BODY)          # stock
    t += box(0.0, 0.12, 0.21, 0.06, 0.12, 0.03, GRIP_C)        # butt plate
    t += box(0.0, 0.15, -0.04, 0.06, 0.08, 0.30, BODY)         # receiver
    t += box(0.0, 0.195, -0.20, 0.015, 0.015, 0.12, ACCENT)    # charge rail
    t += tilt_x(box(0.0, 0.05, 0.01, 0.04, 0.10, 0.05, GRIP_C), 14)  # pistol grip, raked back
    t += box(0.0, 0.09, -0.03, 0.015, 0.03, 0.02, METAL)       # trigger
    t += tilt_x(box(0.0, 0.06, -0.12, 0.04, 0.11, 0.06, METAL), -8)  # magazine
    t += box(0.0, 0.145, -0.29, 0.055, 0.06, 0.20, GRIP_C)     # fore-end
    t += box(0.0, 0.155, -0.40, 0.03, 0.03, 0.06, METAL)       # barrel
    t += box(0.0, 0.155, -0.425, 0.04, 0.04, 0.03, METAL)      # muzzle brake
    t += box(-0.012, 0.205, -0.02, 0.008, 0.03, 0.03, METAL)   # rear sight: a notch,
    t += box(0.012, 0.205, -0.02, 0.008, 0.03, 0.03, METAL)    # so ADS sees through it
    t += box(0.0, 0.205, -0.37, 0.015, 0.03, 0.02, ACCENT)     # front post
    return t


# ---- the rest of the armoury ---------------------------------------------------
# Long guns keep the rifle's contact geometry -- `fore` 0.295 m down and
# 0.055 m up from `grip` (human.py FORE_ALONG / FORE_UP) -- so one set of
# hold clips fits all of them. The pistol has its own class and clips.

SMG_BODY = (0.38, 0.36, 0.30)       # tan polymer
SMG_ACCENT = (0.95, 0.62, 0.15)


def build_smg():
    """Scrap SMG: short receiver, folded wire stock, long stick magazine,
    a stubby shrouded barrel. Fore-end ends near the muzzle."""
    t = []
    t += box(0.0, 0.14, 0.10, 0.012, 0.012, 0.16, METAL)       # wire stock, top bar
    t += box(0.0, 0.10, 0.10, 0.012, 0.012, 0.16, METAL)       # wire stock, bottom bar
    t += box(0.0, 0.12, 0.185, 0.03, 0.07, 0.015, GRIP_C)      # butt pad
    t += box(0.0, 0.15, -0.07, 0.055, 0.075, 0.22, SMG_BODY)   # receiver
    t += tilt_x(box(0.0, 0.05, 0.01, 0.04, 0.10, 0.05, GRIP_C), 14)  # pistol grip
    t += box(0.0, 0.09, -0.03, 0.015, 0.03, 0.02, METAL)       # trigger
    t += tilt_x(box(0.0, 0.03, -0.10, 0.032, 0.18, 0.045, METAL), -4)  # stick magazine
    t += box(0.0, 0.145, -0.27, 0.06, 0.06, 0.13, GRIP_C)      # fore-end shroud
    t += box(0.0, 0.155, -0.36, 0.035, 0.035, 0.05, METAL)     # barrel shroud
    t += box(0.0, 0.155, -0.39, 0.024, 0.024, 0.02, SMG_ACCENT)  # muzzle ring
    t += box(-0.011, 0.20, -0.04, 0.007, 0.025, 0.025, METAL)  # rear notch
    t += box(0.011, 0.20, -0.04, 0.007, 0.025, 0.025, METAL)
    t += box(0.0, 0.20, -0.33, 0.012, 0.025, 0.015, SMG_ACCENT)  # front post
    t += box(0.032, 0.16, -0.10, 0.01, 0.02, 0.05, SMG_ACCENT)  # charging handle
    return t


DMR_BODY = (0.24, 0.27, 0.22)       # dark olive
DMR_GLASS = (0.20, 0.55, 0.75)


def build_dmr():
    """Longshot DMR: full stock, long receiver, a scope on a rail, a long
    fluted barrel with a brake, a bipod folded under the fore-end."""
    t = []
    t += box(0.0, 0.12, 0.17, 0.05, 0.11, 0.18, DMR_BODY)      # stock
    t += box(0.0, 0.17, 0.12, 0.04, 0.03, 0.08, GRIP_C)        # cheek rest
    t += box(0.0, 0.11, 0.265, 0.055, 0.13, 0.03, GRIP_C)      # butt plate
    t += box(0.0, 0.15, -0.06, 0.06, 0.08, 0.34, DMR_BODY)     # receiver
    t += tilt_x(box(0.0, 0.05, 0.01, 0.04, 0.10, 0.05, GRIP_C), 14)  # pistol grip
    t += box(0.0, 0.09, -0.03, 0.015, 0.03, 0.02, METAL)       # trigger
    t += box(0.0, 0.08, -0.12, 0.04, 0.07, 0.07, METAL)        # box magazine
    t += box(0.0, 0.145, -0.31, 0.055, 0.06, 0.20, GRIP_C)     # fore-end
    t += box(0.0, 0.155, -0.58, 0.026, 0.026, 0.34, METAL)     # barrel
    t += box(0.0, 0.155, -0.765, 0.04, 0.035, 0.05, METAL)     # muzzle brake
    t += box(0.0, 0.205, -0.08, 0.014, 0.02, 0.24, METAL)      # rail
    # Scope: a HOLLOW tube (four walls) with a reticle at the objective, so
    # aiming down it looks THROUGH the tube instead of at a solid block.
    for z0, ln, half in ((-0.08, 0.20, 0.020), (0.03, 0.03, 0.025), (-0.19, 0.03, 0.0275)):
        w = 0.005
        t += box(0.0, 0.245 + half - w / 2, z0, half * 2, w, ln, METAL)   # top
        t += box(0.0, 0.245 - half + w / 2, z0, half * 2, w, ln, METAL)   # bottom
        t += box(half - w / 2, 0.245, z0, w, half * 2, ln, METAL)         # right
        t += box(-half + w / 2, 0.245, z0, w, half * 2, ln, METAL)        # left
    t += box(0.0, 0.245, -0.18, 0.001, 0.036, 0.002, (0.02, 0.02, 0.02))  # reticle: vertical
    t += box(0.0, 0.245, -0.18, 0.036, 0.001, 0.002, (0.02, 0.02, 0.02))  # reticle: horizontal
    t += box(0.0, 0.245, -0.18, 0.003, 0.003, 0.002, DMR_GLASS)          # glowing centre dot
    t += box(0.0, 0.205, -0.03, 0.02, 0.04, 0.02, METAL)       # scope mount rear
    t += box(0.0, 0.205, -0.15, 0.02, 0.04, 0.02, METAL)       # scope mount front
    t += box(0.0, 0.10, -0.36, 0.03, 0.02, 0.12, METAL)        # folded bipod
    return t


PISTOL_BODY = (0.40, 0.42, 0.46)


def build_pistol():
    """Pocket Pulser: a blocky slide over a frame, raked grip, a glowing
    cell in the grip base."""
    t = []
    t += box(0.0, 0.105, -0.075, 0.03, 0.035, 0.19, PISTOL_BODY)  # slide
    t += box(0.0, 0.08, -0.06, 0.028, 0.02, 0.15, METAL)       # frame
    t += tilt_x(box(0.0, 0.025, 0.0, 0.028, 0.10, 0.04, GRIP_C), 16)  # grip
    t += box(0.0, -0.025, 0.012, 0.03, 0.012, 0.045, ACCENT)   # cell in the grip base
    t += box(0.0, 0.06, -0.035, 0.012, 0.02, 0.012, METAL)     # trigger
    t += box(0.0, 0.055, -0.05, 0.006, 0.006, 0.05, METAL)     # trigger guard
    t += box(0.0, 0.105, -0.175, 0.02, 0.02, 0.012, METAL)     # muzzle face
    t += box(-0.008, 0.13, -0.005, 0.006, 0.014, 0.012, METAL)  # rear notch
    t += box(0.008, 0.13, -0.005, 0.006, 0.014, 0.012, METAL)
    t += box(0.0, 0.13, -0.16, 0.006, 0.014, 0.01, ACCENT)     # front post
    return t


# (asset id, file, builder, contact points)
WEAPONS = [
    ("weapon.pulse", "pulse.glb", build_rifle,
     dict(grip=GRIP, muzzle=MUZZLE, fore=FORE, sight=SIGHT, front=FRONT)),
    ("weapon.smg", "smg.glb", build_smg,
     dict(grip=GRIP, muzzle=(0.0, 0.155, -0.40), fore=FORE, sight=(0.0, 0.212, -0.04), front=(0.0, 0.212, -0.33))),
    ("weapon.dmr", "dmr.glb", build_dmr,
     dict(grip=GRIP, muzzle=(0.0, 0.155, -0.79), fore=FORE, sight=(0.0, 0.245, 0.05), front=(0.0, 0.245, -0.21))),
    # Pistol: `fore` is where the support palm cups the firing fist -- just
    # under and in front of the grip. human.py PISTOL_FORE matches it.
    ("weapon.sidearm", "sidearm.glb", build_pistol,
     dict(grip=(0.0, 0.03, 0.0), muzzle=(0.0, 0.105, -0.18), fore=(0.0, -0.015, -0.02),
          sight=(0.0, 0.137, -0.005), front=(0.0, 0.137, -0.16))),
]


def main():
    for asset_id, fname, build, nodes in WEAPONS:
        node = _tool(asset_id, build(), nodes["grip"], nodes["muzzle"])
        for name in ("fore", "sight", "front"):
            node.child(name, nodes[name])
        n = write_glb(out_path("weapons", fname), node, MATERIALS)
        print(f"weapons/{fname}: {n} tris (budget 400)")
        if n > 400:
            raise SystemExit(f"weapons/{fname}: {n} tris breaks the 400-tri budget")


if __name__ == "__main__":
    main()
