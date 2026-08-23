#!/usr/bin/env python3
"""Generate weapons/pulse.glb -- the phase 2 pulse rifle.

Low-poly, flat-shaded, built entirely from glb.box() calls (12 tris each)
so the triangle budget is exact by construction: 15 boxes x 12 tris = 180,
matching art/manifest.json's weapon.pulse `tris` field.

-Z is forward (barrel points -Z). Roughly 0.9 m long, ~0.12 m tall body
with sights/energy cell pushing a bit above that, ~0.06 m wide core with
thin vent fins. Two empty nodes mark the mount points the client uses:

    grip   - where the character's right hand holds the weapon
    muzzle - barrel tip, at the forward (-Z) end, for flash/tracer origin

All geometry is literal constants -- no clock, no RNG -- so rebuilds are
byte-identical.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import Node, box, out_path, write_glb

MATERIALS = [{"name": "opaque", "double_sided": True}]

# Palette: dark gunmetal body, lighter accents, cyan energy cell.
BODY = (0.16, 0.17, 0.19)
BODY_LT = (0.24, 0.25, 0.28)
GRIP_COL = (0.10, 0.10, 0.11)
BARREL = (0.20, 0.21, 0.23)
FIN = (0.30, 0.31, 0.34)
SIGHT = (0.05, 0.05, 0.06)
CELL = (0.15, 0.55, 0.60)


def main():
    root = Node("weapon.pulse")

    boxes = [
        # -- stock (rear, +Z) -------------------------------------------
        ("stock", 0.0, 0.05, 0.32, 0.045, 0.075, 0.18, BODY),
        ("stock_pad", 0.0, 0.05, 0.415, 0.05, 0.09, 0.02, GRIP_COL),
        # -- receiver (body) ----------------------------------------------
        ("receiver", 0.0, 0.06, 0.05, 0.06, 0.10, 0.24, BODY_LT),
        # -- pistol grip (below receiver, near trigger) ------------------
        ("grip_box", 0.0, -0.04, 0.09, 0.03, 0.13, 0.045, GRIP_COL, 0.15),
        # -- magazine (under receiver, forward of grip) -------------------
        ("mag", 0.0, -0.05, -0.02, 0.035, 0.11, 0.05, GRIP_COL, -0.08),
        # -- barrel (forward, -Z) ------------------------------------------
        ("barrel", 0.0, 0.06, -0.29, 0.024, 0.024, 0.36, BARREL),
        ("shroud", 0.0, 0.06, -0.50, 0.038, 0.038, 0.10, BODY),
        ("muzzle_cap", 0.0, 0.06, -0.575, 0.046, 0.046, 0.03, SIGHT),
        # -- vent fins on the shroud ---------------------------------------
        ("fin_l", -0.032, 0.06, -0.47, 0.008, 0.03, 0.08, FIN),
        ("fin_r", 0.032, 0.06, -0.47, 0.008, 0.03, 0.08, FIN),
        # -- sights ----------------------------------------------------------
        ("sight_rear", 0.0, 0.125, 0.13, 0.016, 0.025, 0.03, SIGHT),
        ("sight_front", 0.0, 0.12, -0.24, 0.014, 0.02, 0.02, SIGHT),
        # -- energy cell (sci-fi pulse accent, atop the receiver) ---------
        ("cell", 0.0, 0.13, -0.03, 0.032, 0.03, 0.10, CELL),
        # -- trigger guard -----------------------------------------------
        ("guard", 0.0, -0.005, 0.10, 0.032, 0.028, 0.06, GRIP_COL),
        # -- foregrip (under the barrel, forward hand rest) ---------------
        ("foregrip", 0.0, -0.01, -0.30, 0.024, 0.055, 0.035, GRIP_COL),
    ]

    for entry in boxes:
        if len(entry) == 8:
            name, cx, cy, cz, sx, sy, sz, col = entry
            angle = 0.0
        else:
            name, cx, cy, cz, sx, sy, sz, col, angle = entry
        root.add("opaque", box(cx, cy, cz, sx, sy, sz, col, angle))

    # Mount points, both children of the root.
    root.child("grip", (0.0, -0.02, 0.09))
    root.child("muzzle", (0.0, 0.06, -0.59))

    out = out_path("weapons", "pulse.glb")
    n = write_glb(out, root, MATERIALS)
    print(f"weapons/pulse.glb: {n} tris (budget 180)")


if __name__ == "__main__":
    main()
