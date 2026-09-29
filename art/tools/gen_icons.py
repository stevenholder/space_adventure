#!/usr/bin/env python3
"""Item icons for the character panel and backpack (Phase 11.7).

Every item in server/data/items.json that names an `asset` is rendered by
render_check's rasteriser -- one three-quarter view, flat lit, on the UI's
slate -- to art/icons/<item id>.png at ICON px. Items with no asset get no
file; the client draws a rarity tile with the item's initials instead.
Stdlib only, like render_check. Staged beside the executable with art/.
"""
from __future__ import annotations
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import render_check as rc  # noqa: E402

ICON = 96
ART = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ITEMS = os.path.join(os.path.dirname(ART), "server", "data", "items.json")


def main():
    manifest = json.load(open(os.path.join(ART, "manifest.json")))
    files = {a["id"]: a["file"] for a in manifest["assets"]}
    items = json.load(open(ITEMS))["items"]
    out_dir = os.path.join(ART, "icons")
    os.makedirs(out_dir, exist_ok=True)
    rc.BG = (27, 32, 41)  # slate, the tile behind every icon
    made = 0
    for it in items:
        asset = it.get("asset")
        if not asset or asset not in files:
            continue
        path = os.path.join(ART, files[asset])
        if not os.path.exists(path):
            print(f"icons: {it['id']}: missing {files[asset]}", file=sys.stderr)
            continue
        tris = rc.load_triangles(path)
        c = rc.fit_center(tris)
        ext = max(max(abs(p[i] - c[i]) for p in [q for t in tris for q in t[:3]]) for i in range(3))
        dist = ext * 3.6
        cam = (c[0] - dist * 0.62, c[1] + dist * 0.42, c[2] - dist * 0.72)
        img = rc.render(tris, ICON, ICON, cam, (0.62, -0.42, 0.72), (0, 1, 0), 30)
        rc.write_png(os.path.join(out_dir, f"{it['id']}.png"), ICON, ICON, img)
        made += 1
    print(f"icons: {made} rendered -> art/icons/")


if __name__ == "__main__":
    main()
