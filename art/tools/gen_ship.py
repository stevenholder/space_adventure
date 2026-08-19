#!/usr/bin/env python3
"""Generate ships/v1.glb -- the hero scout ship (id `ship.v1`).

M1 has no vehicles (docs/ROADMAP.md), but M2 starts from this asset, so it
is built now against the frozen seat contract instead of blocking M2 on art:

    - forward is -Z, up is +Y; origin at ground level (the ship sits at
      y = 0 on the planet, as a world entity you walk up to)
    - `seat.pilot` and `seat.passenger.0/1` are empty nodes at the eye
      point, carrying no rotation, so the camera mount inherits -Z forward
    - the cockpit is a real space you sit in: open canopy with a frame,
      floor, dash and seats; the hull is double-sided, so the interior
      walls render correctly from the pilot's seat
    - the canopy is a transparent glTF material (alphaMode BLEND)

Crew of three (pilot + 2): the GDD leaves final crew size open for M2;
the interior is a bench layout that grows to 4 if the answer is bigger.
"""
from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb import Node, box, cap, out_path, tube, write_glb

# Palette -- slate hull with orange accents (ties to the player suit),
# teal seats (the backpack), a bright console panel that reads as "lit".
HULL = (0.42, 0.46, 0.52)
ACCENT = (0.91, 0.53, 0.17)
DARK = (0.23, 0.25, 0.27)
INTERIOR = (0.33, 0.31, 0.29)
PANEL = (0.49, 0.95, 0.60)
SEAT = (0.17, 0.55, 0.50)
ENGINE = (0.30, 0.32, 0.35)
INTAKE = (0.12, 0.13, 0.14)
GLOW = (0.35, 0.95, 0.95)

MATERIALS = [
    {"name": "hull", "double_sided": True},
    {"name": "glass", "double_sided": True, "alpha": 0.28,
     "tint": (0.62, 0.85, 0.91)},
]


def hex_ring(x, y, z, r):
    """Six-gon ring (flat bottom/top) centred at (x, y, z)."""
    return [(x + r * math.cos(math.radians(d)),
             y + r * math.sin(math.radians(d)), z)
            for d in (0, 60, 120, 180, 240, 300)]


def engine(x):
    """Hex engine nacelle + flared nozzle at wing position x, axis +Z."""
    tris = []
    e0, e1 = hex_ring(x, 0.5, 1.15, 0.30), hex_ring(x, 0.5, 1.85, 0.30)
    tris += tube(e0, e1, ENGINE)
    tris += cap(e0, (x, 0.5, 1.10), (0, 0, -1), INTAKE)
    tris += cap(e1, (x, 0.5, 1.90), (0, 0, 1), GLOW)
    n0, n1 = hex_ring(x, 0.5, 1.85, 0.34), hex_ring(x, 0.5, 2.02, 0.34)
    tris += tube(n0, n1, DARK)
    tris += cap(n1, (x, 0.5, 2.07), (0, 0, 1), DARK)
    return tris


def build() -> Node:
    root = Node("ship.v1")
    hull = root.child("hull")
    cockpit = root.child("cockpit")

    # -- Fuselage: 6-sided tube, flat bottom, centreline y = 0.6 ----------
    def ring(z, r):
        return [(r * math.cos(math.radians(d)),
                 0.6 + 0.7 * r * math.sin(math.radians(d)), z)
                for d in (0, 60, 120, 180, 240, 300)]

    rings = [ring(-3.2, 0.55), ring(-1.6, 0.95), ring(0.4, 1.0),
             ring(1.8, 0.9), ring(3.2, 0.5)]
    for i in range(len(rings) - 1):
        hull.add("hull", tube(rings[i], rings[i + 1], HULL))
    hull.add("hull", cap(rings[0], (0.0, 0.6, -3.55), (0, 0, -1), ACCENT))
    hull.add("hull", cap(rings[-1], (0.0, 0.6, 3.55), (0, 0, 1), DARK))

    # -- Wings: swept boxes (rotated about their own centre) --------------
    hull.add("hull", box(2.0, 0.5, 0.55, 2.3, 0.12, 1.5, HULL,
                         angle=math.radians(18)))
    hull.add("hull", box(-2.0, 0.5, 0.55, 2.3, 0.12, 1.5, HULL,
                         angle=-math.radians(18)))
    hull.add("hull", box(2.86, 0.62, 1.62, 0.18, 0.3, 0.5, ACCENT,
                         angle=math.radians(18)))
    hull.add("hull", box(-2.86, 0.62, 1.62, 0.18, 0.3, 0.5, ACCENT,
                         angle=-math.radians(18)))

    # -- Engines, tail, landing gear --------------------------------------
    hull.add("hull", engine(2.5) + engine(-2.5))
    hull.add("hull", box(0.0, 1.35, 2.7, 0.1, 0.9, 0.9, ACCENT,
                         angle=math.radians(20)))
    hull.add("hull", box(0.0, 0.75, 2.75, 2.4, 0.08, 0.7, HULL))
    for x in (0.45, -0.45):
        hull.add("hull", box(x, 0.25, -1.2, 0.1, 0.4, 0.25, DARK))
        hull.add("hull", box(x, 0.05, -0.7, 0.12, 0.1, 1.8, DARK))

    # -- Cockpit cabin: floor, sides, dash, pillars, header, rear ---------
    cockpit.add("hull", box(0.0, 1.26, -1.5, 1.6, 0.2, 2.3, INTERIOR))
    for x in (0.85, -0.85):
        cockpit.add("hull", box(x, 1.6, -1.5, 0.12, 1.8, 2.3, INTERIOR))
    cockpit.add("hull", box(0.0, 1.5, -2.69, 1.5, 0.4, 0.12, INTERIOR))  # dash
    for x in (0.675, -0.675):
        cockpit.add("hull", box(x, 1.6, -2.69, 0.15, 1.8, 0.12, INTERIOR))
    cockpit.add("hull", box(0.0, 2.4, -2.69, 1.5, 0.15, 0.12, INTERIOR))  # header
    cockpit.add("hull", box(0.0, 1.6, -0.31, 1.5, 1.8, 0.12, INTERIOR))   # rear
    # Canopy glass across the open roof.
    cockpit.add("glass", box(0.0, 2.44, -1.5, 1.72, 0.05, 2.4, None))
    # Console panel, lit.
    cockpit.add("hull", box(0.0, 1.71, -2.6, 1.1, 0.02, 0.25, PANEL))

    # -- Seats -------------------------------------------------------------
    cockpit.add("hull", box(0.0, 1.44, -1.9, 0.5, 0.14, 0.5, SEAT))       # pilot pan
    cockpit.add("hull", box(0.0, 1.75, -1.62, 0.5, 0.6, 0.12, SEAT))      # pilot back
    cockpit.add("hull", box(0.0, 1.44, -0.75, 1.4, 0.14, 0.5, SEAT))      # bench
    cockpit.add("hull", box(0.0, 1.7, -0.47, 1.4, 0.55, 0.12, SEAT))      # bench back

    # -- Seat nodes (the M2 camera-mount contract) -------------------------
    root.child("seat.pilot", (0.0, 2.21, -1.9))
    root.child("seat.passenger.0", (-0.35, 2.21, -0.75))
    root.child("seat.passenger.1", (0.35, 2.21, -0.75))

    return root


if __name__ == "__main__":
    n = write_glb(out_path("ships", "v1.glb"), build(), MATERIALS)
    print(f"ships/v1.glb: {n} tris (budget 2000)")
