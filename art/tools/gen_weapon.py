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


def main():
    node = _tool("weapon.pulse", build_rifle(), GRIP, MUZZLE)
    node.child("fore", FORE)
    node.child("sight", SIGHT)
    node.child("front", FRONT)
    n = write_glb(out_path("weapons", "pulse.glb"), node, MATERIALS)
    print(f"weapons/pulse.glb: {n} tris (budget 300)")
    if n > 300:
        raise SystemExit(f"weapons/pulse.glb: {n} tris breaks the 300-tri budget")


if __name__ == "__main__":
    main()
