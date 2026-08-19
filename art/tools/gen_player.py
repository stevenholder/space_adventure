#!/usr/bin/env python3
"""Generate chars/player.glb -- the M1 player character.

Segmented, not rigged (art/README.md node contract, frozen):

    eye     empty node at the view point (1.7 m above the feet), -Z forward
    head    hidden for the local player only; visible on everyone else
    torso   body; waist pivot; arms, head and eye are its children
    arm.l   pivot at the shoulder
    arm.r   pivot at the shoulder
    leg.l   pivot at the hip
    leg.r   pivot at the hip

The client rotates these nodes procedurally for the walk cycle; there is no
skeleton, no skinning, no animation clip. The local player sees the model
from inside and from above (camera at `eye`, looking down ~0.3 m at the
torso), so every part is a closed solid with real top faces.

Forward is -Z, up is +Y, origin at the feet.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import Node, box, out_path, write_glb

# Palette -- a bold orange suit: it must stand out against grey-green
# asteroid terrain from 5 m, and read against the sky in silhouette.
SUIT = (0.91, 0.39, 0.17)        # main orange
SUIT_DARK = (0.69, 0.28, 0.10)   # limbs, gloves
SUIT_LIGHT = (0.97, 0.58, 0.26)  # chest plate
PACK = (0.17, 0.55, 0.50)        # teal backpack
DARK = (0.16, 0.15, 0.14)        # pelvis, belt, boots
VISOR = (0.08, 0.22, 0.24)       # dark cyan visor

MATERIALS = [{"name": "opaque", "double_sided": True}]


def build() -> Node:
    root = Node("char.player")

    # Legs -- node origin at the hip (0.95 m), so a rotation swings the
    # leg from the hip, not the ankle.
    for side, x in (("l", -0.13), ("r", 0.13)):
        leg = root.child(f"leg.{side}", (x, 0.95, 0.0))
        leg.add("opaque", box(0.0, -0.415, 0.0, 0.15, 0.83, 0.17, SUIT_DARK))
        # Boot: closed, toe toward -Z, sole exactly at y = 0.
        leg.add("opaque", box(0.0, -0.06, -0.06, 0.17, 0.12, 0.30, DARK))

    # Torso -- node origin at the waist (1.0 m); a small sway/bob rotation
    # here carries the arms, head and eye with it.
    torso = root.child("torso", (0.0, 1.0, 0.0))
    torso.add("opaque", box(0.0, -0.07, 0.0, 0.36, 0.18, 0.24, DARK))   # pelvis
    torso.add("opaque", box(0.0, 0.03, 0.0, 0.38, 0.08, 0.26, DARK))    # belt
    torso.add("opaque", box(0.0, 0.22, 0.0, 0.42, 0.40, 0.28, SUIT))    # chest
    torso.add("opaque", box(0.0, 0.24, -0.16, 0.30, 0.26, 0.06, SUIT_LIGHT))
    torso.add("opaque", box(0.0, 0.20, 0.19, 0.30, 0.34, 0.12, PACK))   # backpack

    # Arms -- node origin at the shoulder (1.38 m).
    for side, x in (("l", -0.27), ("r", 0.27)):
        arm = torso.child(f"arm.{side}", (x, 0.38, 0.0))
        arm.add("opaque", box(0.0, -0.30, 0.0, 0.12, 0.55, 0.13, SUIT_DARK))
        arm.add("opaque", box(0.0, -0.63, 0.0, 0.11, 0.14, 0.12, DARK))  # glove

    # Head -- node origin at the neck (1.47 m). Sibling of `eye`, so hiding
    # the head for the local player never hides the camera.
    head = torso.child("head", (0.0, 0.47, 0.0))
    head.add("opaque", box(0.0, 0.0, 0.0, 0.12, 0.08, 0.12, SUIT))      # neck
    head.add("opaque", box(0.0, 0.16, 0.0, 0.26, 0.28, 0.27, SUIT))     # skull
    head.add("opaque", box(0.0, 0.17, -0.15, 0.20, 0.12, 0.04, VISOR))  # visor

    # Eye -- empty node at world (0, 1.7, 0) = GDD eye_height, -Z forward.
    torso.child("eye", (0.0, 0.7, 0.0))

    return root


if __name__ == "__main__":
    n = write_glb(out_path("chars", "player.glb"), build(), MATERIALS)
    print(f"chars/player.glb: {n} tris (budget 1500)")
