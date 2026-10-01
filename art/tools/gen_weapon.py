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
BODY = (0.26, 0.28, 0.32)
METAL = (0.17, 0.18, 0.21)
ACCENT = (0.32, 0.56, 0.72)
GRIP_C = (0.14, 0.13, 0.13)

GRIP = (0.0, 0.12, 0.0)        # top of the pistol grip, 3.5 cm under the bore
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
    n = write_glb(out_path("weapons", "pulse.glb"), node, MATERIALS)
    print(f"weapons/pulse.glb: {n} tris (budget 300)")
    if n > 300:
        raise SystemExit(f"weapons/pulse.glb: {n} tris breaks the 300-tri budget")


if __name__ == "__main__":
    main()
