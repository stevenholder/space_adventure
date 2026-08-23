#!/usr/bin/env python3
"""Generate chars/shopkeeper.glb -- the Phase 2 quartermaster NPC.

Same segmented, unrigged node layout as char.player (art/tools/gen_player.py,
art/README.md node contract, frozen):

    eye     empty node at the view point (1.7 m above the feet), -Z forward
    head    sibling of `eye` under torso -- never a parent of it
    torso   body; waist pivot; arms, head and eye are its children
    arm.l   pivot at the shoulder
    arm.r   pivot at the shoulder
    leg.l   pivot at the hip
    leg.r   pivot at the hip

The client's procedural walk cycle and nametag code touch these nodes by
name, so the layout must match char.player's exactly even though this is an
NPC, not a player-controlled body.

Palette is a muted green/grey quartermaster's work coat -- no orange, no
teal backpack -- so this never reads as a player character at a glance.

Forward is -Z, up is +Y, origin at the feet.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import Node, box, cap, out_path, write_glb

# Palette -- muted green/grey work coat, dull leather satchel. Nothing here
# is orange or teal, so it never gets mistaken for char.player's suit.
COAT = (0.34, 0.40, 0.33)         # main coat green
COAT_DARK = (0.23, 0.28, 0.23)    # sleeves, collar, upper legs
CAP_GREY = (0.52, 0.52, 0.49)     # cap/skull
APRON = (0.58, 0.57, 0.52)        # front apron patch
SATCHEL = (0.42, 0.34, 0.22)      # leather tool satchel
SATCHEL_DARK = (0.29, 0.23, 0.15) # belt pouch
DARK = (0.15, 0.14, 0.13)         # pelvis, belt, boots, gloves, brim

MATERIALS = [{"name": "opaque", "double_sided": True}]


def build() -> Node:
    root = Node("npc.shopkeeper")

    # Legs -- node origin at the hip (0.95 m), same pivot height as
    # char.player so the client's shared walk-cycle rotation reads right.
    for side, x in (("l", -0.14), ("r", 0.14)):
        leg = root.child(f"leg.{side}", (x, 0.95, 0.0))
        leg.add("opaque", box(0.0, -0.415, 0.0, 0.16, 0.83, 0.18, COAT_DARK))
        leg.add("opaque", box(0.0, -0.06, -0.06, 0.18, 0.12, 0.30, DARK))  # boot

    # Torso -- node origin at the waist (1.0 m). A slightly stockier build
    # than the player's, befitting a quartermaster standing behind a counter.
    torso = root.child("torso", (0.0, 1.0, 0.0))
    torso.add("opaque", box(0.0, -0.07, 0.0, 0.38, 0.18, 0.26, DARK))    # pelvis
    torso.add("opaque", box(0.0, 0.03, 0.0, 0.40, 0.08, 0.28, DARK))     # belt
    torso.add("opaque", box(0.0, 0.23, 0.0, 0.46, 0.42, 0.30, COAT))     # coat body
    torso.add("opaque", box(0.0, 0.14, -0.17, 0.28, 0.20, 0.05, APRON))  # apron patch
    torso.add("opaque", box(0.0, 0.02, 0.0, 0.10, 0.10, 0.06, SATCHEL_DARK))  # belt pouch
    torso.add("opaque", box(0.14, 0.20, 0.16, 0.20, 0.24, 0.14, SATCHEL))     # tool satchel
    torso.add("opaque", box(0.0, 0.42, 0.0, 0.30, 0.06, 0.24, COAT_DARK))     # collar

    # Satchel flap: a small forward-bulging quad fan (4 tris), fixed count
    # so the model can land on an exact triangle budget.
    flap = [(0.04, 0.28, 0.24), (0.24, 0.28, 0.24),
            (0.24, 0.12, 0.24), (0.04, 0.12, 0.24)]
    torso.add("opaque", cap(flap, (0.14, 0.20, 0.26), (0.0, 0.0, 1.0), SATCHEL_DARK))

    # Arms -- node origin at the shoulder (1.38 m).
    for side, x in (("l", -0.29), ("r", 0.29)):
        arm = torso.child(f"arm.{side}", (x, 0.38, 0.0))
        arm.add("opaque", box(0.0, -0.30, 0.0, 0.13, 0.55, 0.14, COAT_DARK))
        arm.add("opaque", box(0.0, -0.63, 0.0, 0.12, 0.14, 0.13, DARK))  # glove

    # Head -- node origin at the neck (1.47 m). Sibling of `eye`, so hiding
    # the head (as the client does for a local body) never hides the camera.
    head = torso.child("head", (0.0, 0.47, 0.0))
    head.add("opaque", box(0.0, 0.0, 0.0, 0.13, 0.08, 0.13, COAT))       # neck
    head.add("opaque", box(0.0, 0.16, 0.0, 0.27, 0.28, 0.28, CAP_GREY))  # skull
    head.add("opaque", box(0.0, 0.28, -0.06, 0.30, 0.05, 0.20, DARK))    # cap brim

    # Eye -- empty node at world (0, 1.7, 0) = GDD eye_height, -Z forward.
    torso.child("eye", (0.0, 0.7, 0.0))

    return root


if __name__ == "__main__":
    n = write_glb(out_path("chars", "shopkeeper.glb"), build(), MATERIALS)
    print(f"chars/shopkeeper.glb: {n} tris (target 220)")
