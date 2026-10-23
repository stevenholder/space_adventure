#!/usr/bin/env -S blender -b --python
"""World props from vendored glTF packs -> build/props/<id>.raw.glb + <id>.json.

    blender -b --python tools/bpy/props_vendor.py     (run from art/; npm run props:vendor)

tools/props_vendor.mjs then finishes each into props/<file>.glb via importPack
and writes its manifest row. The packs are gitignored under vendor/; the
finished .glb is what is committed.

Per prop:
  1. the pack's .gltf/.bin are staged into build/props/<id>/ with the image
     URIs pointed at the pack's Textures/ folder (the MegaKit's glTF folder
     references textures it does not ship beside the model);
  2. skinned parts (the MegaKit animates a chest's lid with a bone) are frozen
     at their rest pose into plain meshes, each renamed per `parts`; a part
     with a `pivot` bone gets its origin there (the lid's hinge), so the
     client can swing it later without a rig;
  3. re-framed like tools/bpy/mobs.py: the packs face glTF +Z, ours face -Z,
     so a half turn about up; base at y=0; the packs are already in metres;
  4. textures capped at TEX_MAX, PBR materials kept as authored.

A prop with `pieces` instead of `src` is COMPOSED from several pack files
(prop.dock, Phase 24): each piece is imported, frozen to a plain mesh, scaled
and moved to its place (given in glTF coordinates of the FINISHED prop, +Z
front, no half turn), then every piece of one `node` is joined into a single
mesh object of that name. Materials that come in twice (every MegaKit file
carries its own MI_Trim_01) are folded onto the first copy, so the pack's
trim sheets are embedded once.
"""
import json
import math
import os
import shutil

import bpy
from mathutils import Euler, Matrix, Vector

ART = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ART, "build", "props")
TEX_MAX = 1024
MEGAKIT = "vendor/scifi-megakit/Modular SciFi MegaKit[Standard]"
MK = MEGAKIT + "/glTF/"
ESS = "vendor/scifi-essentials/glTF/"  # shares the MegaKit's trim sheets (same image names)
COL = MK + "Columns/Column_Simple.gltf"  # 0.5 x 5 x 1 m; the kit's all-purpose post
DISH = ESS + "Prop_SatelliteDish.gltf"  # 1.8 x 5.8 x 2.8 m, 3.3k tris
NAT = "vendor/stylized-nature/glTF/"  # the free 68-model tier: textures sit beside the glTFs
MKP = MK + "Props/"
DRUM = MKP + "Prop_Barrel_Large.gltf"  # 0.51 x 1.10 x 0.53 m, 502 tris, base y 0.008
DESERT = {"Rocks_Diffuse.png": "Rocks_Desert_Diffuse.png"}  # the Nature kit's second rock sheet
# Nature rocks as shipped (glTF frame, metres): (file, lo xyz, hi xyz).
ROCK1 = ("Rock_Medium_1", (-1.727, -0.271, -1.150), (1.498, 1.989, 1.839))  # 342 tris
ROCK2 = ("Rock_Medium_2", (-1.714, -0.051, -1.159), (1.335, 1.848, 1.320))  # 244 tris
ROCK3 = ("Rock_Medium_3", (-1.824, -0.316, -0.890), (1.596, 2.001, 2.586))  # 522 tris


def skirt(file, w, d):
    """The ground plinth (GDD skirt rule): a Bottom*_Straight (stock x -2..-1.4,
    z +-2, 0.02 tall) scaled to w x 0.3 x d, centred, top at y=0."""
    s = w / 0.6
    return ("skirt", MK + "Walls/" + file + ".gltf", (s, 15, d / 4), (1.7 * s, -0.3, 0), (0, 0, 0))


def sides(node, file, h, n=1, s=1.0, sy=1.0, y=0.0, dx=0.0, lean=0.0):
    """A kit *_Straight piece (stock: the cell's x=-2 edge, z +-2, 4 m long) on
    all four edges of a square of half-width h: n pieces per edge, scaled s
    along it, sy tall. `lean` (deg) tips the +-x walls outward at the top (the
    Scrapyard tell); the +-z walls cannot lean with XYZ eulers, so they stand.
    Wall detail protrudes past the stock x=-2 plane, i.e. OUTWARD on every edge."""
    d, out = h - 2, []
    for k in range(n):
        o = (k - (n - 1) / 2) * 4 * s
        out += [(node, file, (1, sy, s), (-d + dx, y, o), (0, 0, lean)),
                (node, file, (1, sy, s), (d + dx, y, o), (0, 180, -lean)),
                (node, file, (1, sy, s), (o + dx, y, d), (0, 90, 0)),
                (node, file, (1, sy, s), (o + dx, y, -d), (0, -90, 0))]
    return out


def rock(node, r, h=1.0, sxz=None, at=(0, 0), rot=(0, 0, 0), dy=0.0):
    """A Nature rock scaled to h metres tall (x/z by `sxz`, default the same
    factor), its base at y=0 and its footprint centred on `at` (x, z); a
    tipped one (`rot`) dips its low edge, so `dy` lifts it back out."""
    name, lo, hi = r
    s = h / (hi[1] - lo[1])
    k = s if sxz is None else sxz
    return (node, NAT + name + ".gltf", (k, s, k),
            (at[0] - k * (lo[0] + hi[0]) / 2, dy - s * lo[1], at[1] - k * (lo[2] + hi[2]) / 2), rot)


PROPS = {
    "prop.chest": dict(
        src=MEGAKIT + "/glTF/Props/Prop_Chest.gltf",
        textures=MEGAKIT + "/Textures",
        # pack mesh object -> our node name, and the bone its origin sits on
        parts={"Prop_Chest": dict(name="chest"), "Prop_Chest_Top": dict(name="lid", pivot="Chest_Top")},
    ),
    # The dock (Phase 24): a 6 m round MegaKit pad on a 6 x 6 m dark-plate
    # apron (the collider box is the square, x/z +-3, y 0..0.25), a gantry at
    # the back (-Z; the +Z front stays clear for the vehicles it makes) and a
    # floor lamp on the post as its beacon. Node order pad, gantry, beacon is
    # the order the client's site ghost fills in.
    # piece: (node, file, scale xyz, position xyz, rotation xyz degrees), all in
    # the finished glTF frame (Y up, +Z front); scale applies first, then XYZ euler.
    "prop.dock": dict(
        textures=MEGAKIT + "/Textures",
        pieces=[
            ("pad", MK + "Platforms/Platform_DarkPlates.gltf", (1.5, 1, 1.5), (0, 0.02, 0), (0, 0, 0)),
            ("pad", MK + "Platforms/Platform_Round1.gltf", (1, 1.4689, 1), (0, 0.0420, 0), (0, 0, 0)),
            ("gantry", MK + "Columns/Column_Simple.gltf", (0.6, 0.46, 0.6), (0, 0, -2.6), (0, 0, 0)),
            # the truss stood on edge (a roll about its length), cantilevered out over the pad
            ("gantry", MK + "Columns/Column_MetalSupport.gltf", (0.5, 1, 0.55), (0, 2.05, -1.5), (0, 0, 90)),
            ("beacon", MK + "Props/Prop_Light_Floor.gltf", (0.55, 0.9, 0.9), (0, 2.3, -2.6), (0, 90, 0)),
        ],
        # The kit lamp's white M_Light becomes the Scrapyard flame-amber
        # #FFAE19 (GDD world art style guide, "light"), named for the client.
        tint={"M_Light": ("M_Beacon", (1.0, 0.423, 0.010))},
    ),
    # ---- The kit swap (Phase 25): the Phase 9 roles rebuilt from MegaKit
    # pieces at the SAME footprints (manifest `cell`/`height`, the verify
    # gate), so the zone JSON and the colliders do not move. Our wall4 is
    # 4 m along +X, centred in a 1x1 cell; a kit `*_Straight` wall is 4 m
    # along Z on the cell's x = -2 edge, so every wall piece turns 90 about
    # Y and shifts 2.1 m to the centre. Scrapyard reaches for the `_Red`
    # sheets, Colony for the default ones (`sheet`); GDD "The two builders".
    "struct.wall4.scrap": dict(
        textures=MEGAKIT + "/Textures",
        pieces=[
            # the broken band wall: 4.82 m long as shipped (the rip overhangs
            # one end); 0.92 along its length keeps the rip and fits the cell
            ("wall", MK + "Walls/WallBand_Straight_Broken.gltf", (1, 1, 0.92), (0.375, 0, -2.1), (0, 90, 0)),
            ("skirt", MK + "Walls/BottomMetal_Straight.gltf", (1.5, 15, 1.1), (0, -0.3, -2.55), (0, 90, 0)),
        ],
    ),
    "struct.wall4.colony": dict(
        textures=MEGAKIT + "/Textures",
        sheet="default",
        pieces=[
            ("wall", MK + "Walls/WallAstra_Straight_Flat.gltf", (1, 1, 1), (0, 0, -2.05), (0, 90, 0)),
            ("skirt", MK + "Walls/BottomSimple_Straight.gltf", (1.5, 15, 1.1), (0, -0.3, -2.55), (0, 90, 0)),
        ],
    ),
    # ---- the other roles (wave 1), same cells and heights as Phase 9 ----------
    # corner: a post (layout.go places corners WITHOUT a yaw, so it must read the
    # same from every side; a kit *_Corner_Outer L would need one). 1.1 m square,
    # 3.6 m, a 0.4 m cap. Scrapyard: post leans 4 deg, cap sits askew.
    "struct.corner.scrap": dict(textures=MEGAKIT + "/Textures", pieces=[
        skirt("BottomMetal_Straight", 1.7, 1.7),
        ("post", COL, (2.2, 0.72, 1.12), (0, 0, 0.02), (0, 0, 4)),
        ("cap", COL, (2.7, 0.08, 1.38), (-0.25, 3.4, 0.02), (0, 25, 0)),
    ]),
    "struct.corner.colony": dict(textures=MEGAKIT + "/Textures", sheet="default", pieces=[
        skirt("BottomSimple_Straight", 1.7, 1.7),
        ("post", COL, (2.2, 0.72, 1.12), (0, 0, 0.02), (0, 0, 0)),
        ("cap", COL, (2.7, 0.08, 1.38), (0, 3.4, 0.02), (0, 0, 0)),
    ]),
    # gate4: a kit Door_Frame (4.86 x 5 m stock) scaled into the 4 x 3.2 m wall
    # module, open (the server's collider splits around the opening). Scrapyard
    # hangs one DarkMetal leaf 60 deg ajar on the left jamb.
    "struct.gate4.colony": dict(textures=MEGAKIT + "/Textures", sheet="default", pieces=[
        skirt("BottomSimple_Straight", 4.3, 1.3),
        ("frame", MK + "Platforms/Door_Frame_Square.gltf", (0.82, 0.64, 1.2), (0, 0, 0), (0, 0, 0)),
    ]),
    "struct.gate4.scrap": dict(textures=MEGAKIT + "/Textures", pieces=[
        skirt("BottomMetal_Straight", 4.3, 1.3),
        ("frame", MK + "Platforms/Door_Frame_A.gltf", (1, 0.64, 1), (0, 0, 0), (0, 0, 0)),  # stock width: its 2.27 m opening matches the 2.4 m collider gap
        ("leaf", MK + "Platforms/Door_DarkMetal.gltf", (0.6, 0.64, 1), (-1.15, 0, 0.3), (0, 60, 0)),
    ]),
    # cover4: a closed 3.8 m box of 2 m ShortWalls with a Platform lid -- the
    # fight-geometry piece. Scrapyard: walls lean, lid turned 6 deg.
    "struct.cover4.colony": dict(textures=MEGAKIT + "/Textures", sheet="default", pieces=[
        skirt("BottomSimple_Straight", 4.3, 4.3),
        *sides("wall", MK + "Walls/ShortWall_WhitePlate2_Straight.gltf", 1.9, s=0.95),
        ("top", MK + "Platforms/Platform_Squares.gltf", (0.95, 1, 0.95), (0, 2.0, 0), (0, 0, 0)),
    ]),
    "struct.cover4.scrap": dict(textures=MEGAKIT + "/Textures", pieces=[
        skirt("BottomMetal_Straight", 4.3, 4.3),
        *sides("wall", MK + "Walls/ShortWall_DarkMetal2_Straight.gltf", 1.9, s=0.95, lean=4),
        ("top", MK + "Platforms/Platform_DarkPlates.gltf", (0.95, 1, 0.95), (0, 2.0, 0), (0, 6, 0)),
    ]),
    # tower: 2x2 cells. Four posts, an 8 m Platform deck (a second one flipped
    # for the underside), 1 m ShortWall rails, a post-box cabin. Scrapyard: the
    # whole thing leans 2 deg, the cabin is shoved off-centre.
    "struct.tower.colony": dict(textures=MEGAKIT + "/Textures", sheet="default", pieces=[
        skirt("BottomSimple_Straight", 8.6, 8.6),
        *[("leg", COL, (1.6, 1.16, 1.2), (sx, 0, sz), (0, 0, 0)) for sx in (-3.2, 3.2) for sz in (-3.2, 3.2)],
        ("deck", MK + "Platforms/Platform_Simple.gltf", (2, 1, 2), (0, 6.2, 0), (0, 0, 0)),
        ("deck", MK + "Platforms/Platform_Simple.gltf", (2, -1, 2), (0, 5.8, 0), (0, 0, 0)),
        *sides("rail", MK + "Walls/ShortWall_Simple1_Straight.gltf", 3.9, s=2.0, y=5.8),
        ("cabin", COL, (4, 0.3, 2), (0, 6.2, 0.04), (0, 0, 0)),
    ]),
    "struct.tower.scrap": dict(textures=MEGAKIT + "/Textures", pieces=[
        skirt("BottomMetal_Straight", 8.6, 8.6),
        *[("leg", COL, (1.6, 1.16, 1.2), (sx, 0, sz), (0, 0, 2)) for sx in (-3.2, 3.2) for sz in (-3.2, 3.2)],
        ("deck", MK + "Platforms/Platform_DarkPlates.gltf", (2, 1, 2), (-0.2, 6.2, 0), (0, 0, 0)),
        ("deck", MK + "Platforms/Platform_DarkPlates.gltf", (2, -1, 2), (-0.2, 5.8, 0), (0, 0, 0)),
        *sides("rail", MK + "Walls/ShortWall_DarkMetal2_Straight.gltf", 3.9, s=2.0, sy=0.5, y=5.8, dx=-0.2),
        ("cabin", COL, (4.4, 0.26, 1.8), (0.9, 6.2, -0.8), (0, 12, 0)),
    ]),
    # mast: THE landmark, 12.3 m, the Essentials dish on top (the relay).
    # Colony: one Column_Large_Straight. Scrapyard: two posts stacked by eye,
    # each leaning further, the dish tipped the other way.
    "struct.mast.colony": dict(textures=MEGAKIT + "/Textures", sheet="default", pieces=[
        skirt("BottomSimple_Straight", 2.0, 2.0),
        ("mast", MK + "Columns/Column_Large_Straight.gltf", (1, 1, 1), (0, 0, -0.14), (0, 0, 0)),
        ("dish", DISH, (0.4, 0.4, 0.4), (0, 10, -0.04), (0, 0, 0)),
    ]),
    "struct.mast.scrap": dict(textures=MEGAKIT + "/Textures", pieces=[
        skirt("BottomMetal_Straight", 2.0, 2.0),
        ("mast", COL, (1.4, 1, 1.4), (0, 0, 0.03), (0, 0, 2)),
        ("mast", COL, (1.2, 1, 1.2), (-0.17, 5, 0.02), (0, 15, 5)),
        ("dish", DISH, (0.4, 0.4, 0.4), (-0.6, 9.95, -0.04), (0, 30, -8)),
    ]),
    # hab (Colony): 2x2 closed hut filling its cells -- eight Astra walls, eight
    # TopSimple copings (3..5 m), a Platform roof at 5 m, a door leaf on +z.
    "struct.hab.colony": dict(textures=MEGAKIT + "/Textures", sheet="default", pieces=[
        skirt("BottomSimple_Straight", 8.6, 8.6),
        *sides("wall", MK + "Walls/WallAstra_Straight_Flat.gltf", 4.0, n=2),
        *sides("coping", MK + "Walls/TopSimple_Straight.gltf", 4.0, n=2),
        ("roof", MK + "Platforms/Platform_Simple.gltf", (2, 1, 2), (0, 5.0, 0), (0, 0, 0)),
        ("door", MK + "Platforms/Door_Simple.gltf", (0.8, 0.72, 1), (0.85, 0, 4.1), (0, 0, 0)),
    ]),
    # shack (Scrapyard): 2x2 leaning hut, 5.6 x 4.8 m of band walls (two of them
    # the Broken ones, rips overhanging the corners), a tipped roof plate, an
    # awning on a post, a door leaf, a stovepipe.
    "struct.shack.scrap": dict(textures=MEGAKIT + "/Textures", pieces=[
        skirt("BottomMetal_Straight", 8.0, 8.0),
        ("wall", MK + "Walls/WallBand_Straight.gltf", (1, 0.9, 1.2), (-0.8, 0, 0), (0, 0, 3)),
        ("wall", MK + "Walls/WallBand_Straight_Broken.gltf", (1, 0.9, 1.2), (0.8, 0, 0), (0, 180, -3)),
        ("wall", MK + "Walls/WallBand_Straight.gltf", (1, 0.9, 1.4), (0, 0, 0.8), (0, 90, 0)),
        ("wall", MK + "Walls/WallBand_Straight_Broken.gltf", (1, 0.9, 1.4), (0, 0, -0.8), (0, -90, 0)),
        ("roof", MK + "Platforms/Platform_DarkPlates.gltf", (1.6, 1, 1.4), (-0.3, 2.75, 0), (0, 0, 4)),
        ("leanto", MK + "Platforms/Platform_DarkPlates.gltf", (0.5, 1, 0.5), (3.3, 1.9, -1.4), (0, 0, -30)),
        ("leanto", COL, (0.6, 0.3, 0.6), (4.0, 0, -1.4), (0, 0, 0)),
        ("door", MK + "Platforms/Door_DarkMetal.gltf", (0.65, 0.6, 1), (0.7, 0, 3.05), (0, 0, 2)),
        ("pipe", COL, (0.5, 0.35, 0.3), (-2.2, 2.6, 1.4), (0, 0, 6)),
    ]),
    # ---- camp props (wave 2), each filling its solid box (sim/props.go
    # PropBoxes = Collide.cs, the model's own frame) without passing it. The
    # MegaKit drum, not the Essentials one: Scrapyard dressing on the trim
    # sheets the camp already loads, 502 tris against 2352. Scaled 1.2 across
    # (a squat, real-drum 0.6 x 0.95 m) so it fills the 0.64 m box.
    "prop.barrel": dict(textures=MEGAKIT + "/Textures", pieces=[
        ("drum", DRUM, (1.2, 0.855, 1.2), (0, -0.007, 0), (0, 0, 0)),
    ]),
    # two standing toward +Z, one fallen across the front (-Z), its axis on X
    "prop.barrels": dict(textures=MEGAKIT + "/Textures", pieces=[
        ("drums", DRUM, (1.2, 0.855, 1.2), (-0.335, -0.007, 0.16), (0, 0, 0)),
        ("drums", DRUM, (1.2, 0.855, 1.2), (0.335, -0.007, 0.16), (0, 40, 0)),
        ("drums", DRUM, (1.2, 0.855, 1.2), (-0.478, 0.305, -0.54), (0, 0, -90)),
    ]),
    # the generator: nothing in Essentials, so MegaKit parts in the old
    # layout -- a Crate3 housing on -X, the small fan on its roof as the
    # cooling fan, a drum on its side on the +X end as the fuel tank.
    "prop.generator": dict(textures=MEGAKIT + "/Textures", pieces=[
        # Crate3, not the 972-tri Crate4: with Crate4 the lot is 1.9k and
        # meshopt stalls at ~1.7k (UV seams), over the 1600 budget
        ("housing", MKP + "Prop_Crate3.gltf", (1.546, 1.1, 0.85), (-0.226, 0.55, 0), (0, 0, 0)),
        ("fan", MKP + "Prop_Fan_Small.gltf", (0.5, 0.5, 0.5), (-0.225, 1.13, 0), (0, 0, 0)),
        ("tank", DRUM, (1.2, 0.515, 1.2), (0.546, 0.31, 0), (0, 0, -90)),
    ]),
    # the comms dish: the Essentials dish (already the masts' top) at 0.36,
    # shipped bowl toward -Z, on a DarkPlates pad that is the box's footprint.
    "prop.dish": dict(textures=MEGAKIT + "/Textures", pieces=[
        ("pad", MK + "Platforms/Platform_DarkPlates.gltf", (0.475, 1, 0.3975), (0, 0.02, -0.135), (0, 0, 0)),
        ("dish", DISH, (0.36, 0.36, 0.36), (0, 0.012, -0.135), (0, 0, 0)),
    ]),
    # dropped loot: the MegaKit's Crate3 (a 1 m cube, 204 tris) squeezed into
    # the 0.44 x 0.50 x 0.34 m box. Not the Essentials Prop_Crate: its 3984
    # tris stop simplifying at ~2.8k (UV seams pin the edges), 3x the budget.
    "prop.loot.crate": dict(textures=MEGAKIT + "/Textures", pieces=[
        ("crate", MKP + "Prop_Crate3.gltf", (0.44, 0.5, 0.34), (0, 0.25, 0), (0, 0, 0)),
    ]),
    # ---- Nature (wave 2): the Stylized Nature MegaKit rocks, exactly 1 m tall
    # (the client scales them 0.3-1.5 m and tints each one; Rocks.cs).
    "prop.rock.a": dict(textures=NAT, pieces=[rock("rock", ROCK1)]),
    "prop.rock.b": dict(textures=NAT, pieces=[rock("rock", ROCK2)]),
    "prop.rock.c": dict(textures=NAT, images=DESERT, pieces=[rock("rock", ROCK3)]),
    # ore nodes: inside verify's NODE_BOUNDS (top 0.9..1.05, |x|,|z| <= 0.8),
    # each a different silhouette from the other and from a plain rock at
    # 20 m (the Phase 11 discovery read), the ore told by its `base_color`
    # (tools/props_vendor.mjs; import_pack multiplies the sheet by it).
    # Iron: a tall split pair leaning apart, the taller toward -Z, the
    # desert sheet, rust.
    "prop.node.ore.iron": dict(textures=NAT, images=DESERT, pieces=[
        rock("ore", ROCK3, h=0.97, sxz=0.2, at=(0.0, -0.3), rot=(-12, 0, 0), dy=0.033),
        rock("ore", ROCK2, h=0.75, sxz=0.2, at=(0.05, 0.35), rot=(12, 30, 0), dy=0.033),
    ]),
    # Copper: three low, wide chunks in a ring, the default sheet, teal.
    "prop.node.ore.copper": dict(textures=NAT, pieces=[
        rock("ore", ROCK2, h=0.95, sxz=0.22, at=(-0.28, -0.32), rot=(0, 0, 0)),
        rock("ore", ROCK2, h=0.6, sxz=0.2, at=(0.30, -0.15), rot=(0, 120, 0)),
        rock("ore", ROCK2, h=0.5, sxz=0.2, at=(-0.18, 0.30), rot=(0, 240, 0)),
    ]),
}


def stage(oid, p):
    """Copy the glTF + buffers into build/props/<id>/, images pointed at the pack's Textures/."""
    src = os.path.join(ART, p["src"])
    d = os.path.join(OUT, oid)
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d)
    g = json.load(open(src))
    for b in g.get("buffers", []):
        shutil.copy(os.path.join(os.path.dirname(src), b["uri"]), d)
    for img in g.get("images", []):
        if p.get("sheet") == "default":  # Colony: the un-tinted trim sheets
            img["uri"] = img["uri"].replace("_Red.png", ".png").replace("_Blue.png", ".png")
            img.pop("name", None)
        if img["uri"] in p.get("images", {}):  # a recipe's sheet swap (the Nature desert rocks)
            img["uri"] = p["images"][img["uri"]]
            img["name"] = os.path.splitext(img["uri"])[0]
        shutil.copy(os.path.join(ART, p["textures"], img["uri"]), d)
    path = os.path.join(d, os.path.basename(src))
    json.dump(g, open(path, "w"))
    return path


def build_pieces(oid, p):
    """A prop composed of several pack files (see the module doc)."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mats = {}
    by_node = {}
    for i, (node, src, scl, pos, rot) in enumerate(p["pieces"]):
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=stage(f"{oid}/{i}", dict(src=src, textures=p["textures"], sheet=p.get("sheet"), images=p.get("images", {}))))
        new = [o for o in bpy.data.objects if o not in before]
        dg = bpy.context.evaluated_depsgraph_get()
        # glTF frame -> Blender: (x, y, z) -> (x, -z, y).
        to_b = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
        r = Euler([math.radians(a) for a in rot], "XYZ").to_matrix().to_4x4()
        place = (Matrix.Translation((pos[0], -pos[2], pos[1]))
                 @ to_b @ r @ Matrix.Diagonal((scl[0], scl[1], scl[2], 1)) @ to_b.inverted())
        # A skinned piece (Prop_Fan_Small) brings its bones' display shape
        # (an Icosphere mesh object) along; that is not geometry.
        shapes = {pb.custom_shape for o in new if o.type == "ARMATURE" for pb in o.pose.bones if pb.custom_shape}
        for o in new:
            if o.type != "MESH" or o in shapes:
                continue
            me = bpy.data.meshes.new_from_object(o.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
            me.transform(place @ o.matrix_world)
            if place.determinant() < 0:
                me.flip_normals()
            for k, m in enumerate(me.materials):
                base = m.name.split(".")[0]
                me.materials[k] = mats.setdefault(base, m)
            by_node.setdefault(node, []).append(me)
        for o in new:
            bpy.data.objects.remove(o, do_unlink=True)

    pts = []
    for node, meshes in by_node.items():
        obs = []
        for me in meshes:
            ob = bpy.data.objects.new(node, me)
            bpy.context.scene.collection.objects.link(ob)
            obs.append(ob)
        bpy.ops.object.select_all(action="DESELECT")
        for ob in obs:
            ob.select_set(True)
        bpy.context.view_layer.objects.active = obs[0]
        if len(obs) > 1:
            bpy.ops.object.join()
        ob = bpy.context.view_layer.objects.active
        ob.name = ob.data.name = node
        pts += [v.co.copy() for v in ob.data.vertices]
    bpy.data.orphans_purge(do_recursive=True)
    for src_name, (name, rgb) in p.get("tint", {}).items():
        m = bpy.data.materials[src_name]
        m.name = name
        bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
        bsdf.inputs["Emission Color"].default_value = (*rgb, 1)
        bsdf.inputs["Emission Strength"].default_value = 1.0
    for img in bpy.data.images:
        if img.size[0] > TEX_MAX:
            img.scale(TEX_MAX, max(1, TEX_MAX * img.size[1] // img.size[0]))

    def box(vs):
        return ([round(min(v.x for v in vs), 3), round(min(v.z for v in vs), 3), round(min(-v.y for v in vs), 3)],
                [round(max(v.x for v in vs), 3), round(max(v.z for v in vs), 3), round(max(-v.y for v in vs), 3)])
    lo, hi = box(pts)
    meta = {
        "id": oid,
        "src": sorted({src for _, src, _, _, _ in p["pieces"]}),
        "width": round(hi[0] - lo[0], 3), "height": round(hi[1] - lo[1], 3), "depth": round(hi[2] - lo[2], 3),
        "tris": sum(len(f.vertices) - 2 for o in bpy.data.objects for f in o.data.polygons),
        "nodes": {o.name: box([v.co for v in o.data.vertices]) for o in bpy.data.objects},
        "pivots": {},
        "textures": sorted(f"{i.name} {i.size[0]}x{i.size[1]}" for i in bpy.data.images),
    }
    out = os.path.join(OUT, oid + ".raw.glb")
    bpy.ops.export_scene.gltf(
        filepath=out, export_format="GLB", use_selection=False, export_yup=True,
        export_texcoords=True, export_normals=True, export_materials="EXPORT",
        export_image_format="AUTO", export_animations=False,
    )
    json.dump(meta, open(os.path.join(OUT, oid + ".json"), "w"), indent=1)
    print("props_vendor:", oid, "->", out, meta)


def build(oid, p):
    if "pieces" in p:
        return build_pieces(oid, p)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=stage(oid, p))
    arm = next((o for o in bpy.data.objects if o.type == "ARMATURE"), None)
    dg = bpy.context.evaluated_depsgraph_get()

    # Freeze every part at rest into a plain mesh in world space.
    if arm:
        arm.data.pose_position = "REST"
        bpy.context.view_layer.update()
    parts = []
    for src_name, part in p["parts"].items():
        o = bpy.data.objects[src_name]
        me = bpy.data.meshes.new_from_object(o.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
        me.transform(o.matrix_world)
        me.name = part["name"]
        pivot = None
        if part.get("pivot"):
            pivot = arm.matrix_world @ arm.data.bones[part["pivot"]].head_local
        parts.append((part["name"], me, pivot))
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)

    # Our frame: front +Y in Blender (-Z in glTF), base at z=0.
    lo_z = min(v.co.z for _, me, _ in parts for v in me.vertices)
    frame = Matrix.Translation((0, 0, -lo_z)) @ Matrix.Rotation(math.pi, 4, "Z")
    pts = []
    for name, me, pivot in parts:
        me.transform(frame)
        at = frame @ pivot if pivot is not None else Vector((0, 0, 0))
        me.transform(Matrix.Translation(-at))
        ob = bpy.data.objects.new(name, me)
        ob.location = at
        bpy.context.scene.collection.objects.link(ob)
        pts += [v.co + at for v in me.vertices]

    for img in bpy.data.images:
        if img.size[0] > TEX_MAX:
            img.scale(TEX_MAX, max(1, TEX_MAX * img.size[1] // img.size[0]))

    lo = Vector((min(q.x for q in pts), min(q.y for q in pts), min(q.z for q in pts)))
    hi = Vector((max(q.x for q in pts), max(q.y for q in pts), max(q.z for q in pts)))
    size = hi - lo
    # Pivots in glTF coordinates: Blender (x, y, z) -> glTF (x, z, -y).
    pivots = {n: [round(o.location.x, 4), round(o.location.z, 4), round(-o.location.y, 4)]
              for n, o in ((n, bpy.data.objects[n]) for n, _, _ in parts) if o.location.length > 0}
    meta = {
        "id": oid,
        "src": p["src"],
        "width": round(size.x, 3), "height": round(size.z, 3), "depth": round(size.y, 3),
        "tris": sum(len(f.vertices) - 2 for _, me, _ in parts for f in me.polygons),
        "pivots": pivots,
        "textures": sorted(f"{i.name} {i.size[0]}x{i.size[1]}" for i in bpy.data.images),
    }
    out = os.path.join(OUT, oid + ".raw.glb")
    bpy.ops.export_scene.gltf(
        filepath=out, export_format="GLB", use_selection=False, export_yup=True,
        export_texcoords=True, export_normals=True, export_materials="EXPORT",
        export_image_format="AUTO", export_animations=False,
    )
    json.dump(meta, open(os.path.join(OUT, oid + ".json"), "w"), indent=1)
    print("props_vendor:", oid, "->", out, meta)


def main():
    os.makedirs(OUT, exist_ok=True)
    only = os.environ.get("PROPS_ONLY")  # e.g. PROPS_ONLY=prop.dock: rebuild just that one
    for oid, p in PROPS.items():
        if not only or oid in only.split(","):
            build(oid, p)


main()
