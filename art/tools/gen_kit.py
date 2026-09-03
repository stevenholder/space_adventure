#!/usr/bin/env python3
"""Generate the Phase 9 structure kit -- structs/<piece>.<faction>.glb.

Contract: docs/GDD.md "World art style guide". Seven pieces, two faction
skins each. The kit exists to END the stretched-unit-box era: the client
TILES these at their authored size (module grid 4 m), so every piece is
modelled at final scale, base at y=0, and carries the ground skirt the
style guide requires (0.3 m plinth below y=0, wider than the piece, to
hide the flatten seam).

The factions must read at silhouette range:
    Scrapyard -- one thing is always CROOKED. Lean and jag happen INSIDE
                 the piece's own cell, never across a boundary.
    Colony    -- one thing is always SYMMETRIC. Level copings, closed
                 corners, repeated panels.

Deterministic: no RNG, every lean is a written constant. Every clone
regenerates byte-identical files.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import Node, box, out_path, write_glb

MATERIALS = [{"name": "opaque", "double_sided": True}]

# ---- palettes (GDD table, verbatim) ----------------------------------------
RUST = (0.55, 0.29, 0.18)
SCORCH = (0.23, 0.18, 0.16)
HAZARD = (0.85, 0.63, 0.07)
GLOW_SCRAP = (1.00, 0.68, 0.10)

HULL = (0.72, 0.74, 0.77)
PANEL = (0.37, 0.42, 0.46)
TRIM = (0.18, 0.43, 0.55)
GLOW_COLONY = (0.81, 0.91, 0.95)

SKIRT = (0.16, 0.16, 0.18)  # near-ink: the piece's shadow line at the ground

CELL = 4.0


def skirt(tris, w, d, cx=0.0, cz=0.0):
    """The ground plinth: 0.3 m tall, half of it below y=0, wider than the
    piece it carries. Every piece gets one; it is what hides the flatten
    band's residual slope."""
    tris += box(cx, -0.0, cz, w, 0.6, d, SKIRT)


# ---- wall4: 4.0 x 0.8 x 3.0 ------------------------------------------------

def wall4_colony():
    t = []
    skirt(t, 4.3, 1.3)
    t += box(0, 0.55, 0, 4.0, 0.5, 0.94, PANEL)          # plinth
    t += box(0, 1.85, 0, 4.0, 2.1, 0.80, HULL)           # shaft
    t += box(0, 3.05, 0, 4.1, 0.4, 0.94, TRIM)           # coping, proud
    return t


def wall4_scrap():
    t = []
    skirt(t, 4.3, 1.3)
    # Three welded panels, none the same height, each leaning its own way
    # -- but every extreme stays inside the 4 m cell.
    t += box(-1.32, 1.45, 0.02, 1.28, 2.5, 0.78, RUST, angle=0.05)
    t += box(0.00, 1.65, -0.03, 1.30, 2.9, 0.80, SCORCH, angle=-0.04)
    t += box(1.32, 1.55, 0.03, 1.28, 2.7, 0.76, RUST, angle=0.06)
    # The hazard stripe: one band on the middle panel, the faction accent.
    t += box(0.00, 0.85, -0.03, 1.34, 0.35, 0.84, HAZARD, angle=-0.04)
    return t


# ---- corner: 1.2 x 1.2 x 3.6 post. Corners BELONG to posts (GDD): walls
# butt into these and never meet each other. ---------------------------------

def corner_colony():
    t = []
    skirt(t, 1.7, 1.7)
    t += box(0, 1.85, 0, 1.0, 3.1, 1.0, PANEL)
    t += box(0, 3.55, 0, 1.3, 0.4, 1.3, TRIM)            # square cap
    return t


def corner_scrap():
    t = []
    skirt(t, 1.7, 1.7)
    t += box(0, 1.85, 0, 1.0, 3.1, 1.0, SCORCH, angle=0.12)
    t += box(0.06, 3.55, -0.04, 1.35, 0.4, 1.25, RUST, angle=0.38)  # crooked cap
    return t


# ---- gate4: wall4 with a 2.4 m opening -------------------------------------

def _gate(jamb, header, accent):
    t = []
    skirt(t, 0.9, 1.3, cx=-1.7)
    skirt(t, 0.9, 1.3, cx=1.7)
    t += box(-1.7, 1.5, 0, 0.6, 3.0, 0.8, jamb)
    t += box(1.7, 1.5, 0, 0.6, 3.0, 0.8, jamb)
    t += box(0, 2.75, 0, 4.0, 0.5, 0.9, header)
    t += box(0, 3.1, 0, 4.1, 0.2, 0.95, accent)          # the lintel line
    return t


def gate4_colony():
    return _gate(PANEL, HULL, TRIM)


def gate4_scrap():
    t = _gate(SCORCH, RUST, HAZARD)
    # A scrap gate is never quite finished: one extra plate slung on a jamb.
    t += box(-1.55, 1.1, 0.25, 0.5, 1.2, 0.12, RUST, angle=0.2)
    return t


# ---- tower: 2x2 cells (8 m plan), 8 m tall ---------------------------------

def _tower(leg, deck, rail, lean=0.0):
    t = []
    skirt(t, 8.4, 8.4)
    for sx in (-3.2, 3.2):
        for sz in (-3.2, 3.2):
            t += box(sx, 3.0, sz, 0.6, 6.0, 0.6, leg, angle=lean)
    t += box(0, 6.2, 0, 8.0, 0.4, 8.0, deck)
    for sx, sz, w, d in ((0, 3.85, 8.0, 0.3), (0, -3.85, 8.0, 0.3),
                         (3.85, 0, 0.3, 7.4), (-3.85, 0, 0.3, 7.4)):
        t += box(sx, 6.9, sz, w, 1.0, d, rail)
    return t


def tower_colony():
    t = _tower(PANEL, HULL, TRIM)
    t += box(0, 7.6, 0, 2.0, 1.4, 2.0, PANEL)            # centered cabin
    return t


def tower_scrap():
    t = _tower(SCORCH, RUST, RUST, lean=0.06)
    t += box(1.1, 7.5, -0.8, 2.2, 1.3, 1.8, SCORCH, angle=0.22)  # cabin, shoved
    t += box(0, 6.55, 0, 8.2, 0.25, 8.2, HAZARD)          # hazard deck edge
    return t


# ---- mast: 1 cell, 12 m -- THE landmark. One per POI, glow tip. ------------

def mast_colony():
    t = []
    skirt(t, 2.0, 2.0)
    t += box(0, 2.0, 0, 0.5, 4.0, 0.5, PANEL)
    t += box(0, 6.0, 0, 0.4, 4.0, 0.4, HULL)
    t += box(0, 10.0, 0, 0.3, 4.0, 0.3, PANEL)
    t += box(0, 9.6, 0.5, 1.6, 1.0, 0.15, HULL)          # dish plate
    t += box(0, 12.2, 0, 0.5, 0.5, 0.5, GLOW_COLONY)     # the tip
    return t


def mast_scrap():
    t = []
    skirt(t, 2.0, 2.0)
    # Lattice sections, each offset a little further -- a mast that was
    # straightened by eye. Total drift stays well inside the cell.
    t += box(0.00, 2.0, 0.00, 0.55, 4.0, 0.55, SCORCH, angle=0.1)
    t += box(0.25, 6.0, 0.15, 0.45, 4.0, 0.45, RUST, angle=0.3)
    t += box(0.45, 10.0, 0.28, 0.38, 4.0, 0.38, SCORCH, angle=0.5)
    t += box(0.30, 5.0, 0.10, 1.8, 0.2, 0.2, RUST, angle=0.35)   # crossbar
    t += box(0.40, 8.6, 0.22, 1.5, 0.2, 0.2, RUST, angle=0.15)   # crossbar
    t += box(0.55, 12.2, 0.34, 0.55, 0.55, 0.55, GLOW_SCRAP)     # the tip
    return t


# ---- hab (Colony only): 2x2 cells, the closed hut --------------------------

def hab_colony():
    t = []
    skirt(t, 7.8, 7.8)
    t += box(0, 1.5, 0, 7.0, 2.6, 7.0, HULL)
    t += box(0, 0.75, 0, 7.2, 1.1, 7.2, PANEL)           # wainscot band
    t += box(0, 3.0, 0, 7.6, 0.5, 7.6, TRIM)             # eave
    t += box(0, 3.5, 0, 5.4, 0.7, 5.4, PANEL)            # roof step 1
    t += box(0, 4.0, 0, 3.2, 0.5, 3.2, HULL)             # roof step 2
    t += box(0, 1.4, 3.55, 1.6, 2.2, 0.2, PANEL)         # door plate
    t += box(1.9, 4.6, 1.9, 0.2, 1.4, 0.2, TRIM)         # vent stub
    return t


# ---- shack (Scrapyard only): 2x2 cells, the leaning hut --------------------

def shack_scrap():
    t = []
    skirt(t, 7.8, 7.8)
    t += box(-0.4, 1.3, 0.2, 6.2, 2.4, 4.8, SCORCH, angle=0.07)
    t += box(-0.3, 2.75, 0.2, 7.0, 0.35, 5.8, RUST, angle=0.12)  # roof, overhung
    t += box(2.6, 0.9, -1.4, 2.0, 1.6, 2.6, RUST, angle=-0.18)   # lean-to
    t += box(-2.2, 1.2, 2.68, 1.4, 1.8, 0.18, HAZARD, angle=0.07)  # door plate
    t += box(-2.9, 3.6, 1.4, 0.25, 1.6, 0.25, SCORCH, angle=0.3)   # stovepipe
    return t


PIECES = {
    "structs/wall4.colony.glb": ("struct.wall4.colony", wall4_colony),
    "structs/wall4.scrap.glb": ("struct.wall4.scrap", wall4_scrap),
    "structs/corner.colony.glb": ("struct.corner.colony", corner_colony),
    "structs/corner.scrap.glb": ("struct.corner.scrap", corner_scrap),
    "structs/gate4.colony.glb": ("struct.gate4.colony", gate4_colony),
    "structs/gate4.scrap.glb": ("struct.gate4.scrap", gate4_scrap),
    "structs/tower.colony.glb": ("struct.tower.colony", tower_colony),
    "structs/tower.scrap.glb": ("struct.tower.scrap", tower_scrap),
    "structs/mast.colony.glb": ("struct.mast.colony", mast_colony),
    "structs/mast.scrap.glb": ("struct.mast.scrap", mast_scrap),
    "structs/hab.colony.glb": ("struct.hab.colony", hab_colony),
    "structs/shack.scrap.glb": ("struct.shack.scrap", shack_scrap),
}


def main():
    for rel, (asset_id, build) in PIECES.items():
        node = Node(asset_id)
        node.add("opaque", build())
        parts = rel.split("/")
        n = write_glb(out_path(*parts), node, MATERIALS)
        print(f"{rel}: {n} tris")


if __name__ == "__main__":
    main()
