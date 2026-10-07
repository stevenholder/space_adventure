#!/usr/bin/env -S blender -b --python
"""Hair as worn pieces: build/hair.<style>[@<body>].raw.glb for every
Quaternius hairstyle (art/vendor/ubc, "Rigged to Head Bone") on every body.

    blender -b --python tools/bpy/hair.py                       (run from art/)
    blender -b --python tools/bpy/hair.py -- char.player        one body ("base" = the old build)

A hairstyle is authored on a UBC Superhero head. For each body this builds
that body (tools/bpy/human.py, undecimated: the same shape the shipped one
was decimated from) and moves the style onto ITS head:

  1. per axis, the source skull's box onto the target's (width above the
     ears, front-to-back depth, eye line to crown), so a bun stays a bun;
  2. human.refit(): vertices near the scalp keep the height they had above
     the source skin, now above this skin (a gap or a poke-through on a
     MakeHuman head is where the two skulls differ), smoothed so strands
     move together, nothing left under the skin;
  3. decimated under BUDGET, one mesh named `hair`, material `hair` (the
     pack's strand texture x HAIR_TINT; a helmet's `covers: head/hair`
     hides it), 100% on the `head` bone, exported with the body's rig.

The client wears it like armor: slot `hair`, item `hair.<style>`, and the
`<id>@<wearer>` variant when one exists (Entities.Dress). `hair.none` has
no file.
"""
import importlib.util
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("human", os.path.join(HERE, "human.py"))
human = importlib.util.module_from_spec(spec)
spec.loader.exec_module(human)

ART = human.ART
BUDGET = 1500        # tris per piece (verify.mjs hair.*)
GAP = 0.002          # m: hair never closer to the skin than this

# style id -> pack file. Every glTF in the folder but the eyebrows (those
# live inside the bodies' `head`).
STYLES = {
    "hair.buzzed": "Hair_Buzzed",
    "hair.buzzed_female": "Hair_BuzzedFemale",
    "hair.simple_parted": "Hair_SimpleParted",
    "hair.long": "Hair_Long",
    "hair.buns": "Hair_Buns",
    "hair.beard": "Hair_Beard",
}
# "" is the old MakeHuman build (armor.py's base body): the unsuffixed piece.
BODIES = ("", "char.player", "char.player.f", "char.ubc", "char.ubc.f")
# The cap-like pieces reach down over the body's painted hairline
# (skin.HAIR_M / HAIR_F): their lower band is stretched until the edge sits
# HAIR_MARGIN under the line (its feather and noise end ~4.5 mm under it).
# The strand pieces (long, buns) hang past it; the beard is a face piece.
CAPS = ("hair.buzzed", "hair.buzzed_female", "hair.simple_parted")
HAIR_MARGIN = 0.005  # m
EDGE_WIN = 8         # deg each side: the window the drop takes the edge's maximum over
FLOOR = 0.008        # m: no vertex stretched further than this under the line
LIE = 0.012          # m: hair this close to the scalp covers it
BAND = 0.030         # m: the stretch fades out this far above the old edge (at least 2x the drop)
HEAD_BONES = ("head", "neck_01")
SHOULDER_BONES = ("spine_03", "clavicle_l", "clavicle_r")   # long hair rests on these


def body_surface(h, eyes):
    """The target: head, neck and shoulders (long hair lies on them), the
    eyeballs left out. Plus the skull's points above the eye line - 2 cm."""
    dom = human.dominant_bones(h)
    verts = [v.co.copy() for v in h.data.vertices]
    polys = []
    for p in h.data.polygons:
        bones = [dom[i] for i in p.vertices]
        b = max(set(bones), key=bones.count)
        if b in HEAD_BONES + SHOULDER_BONES and min((p.center - e).length for e in eyes) > 0.0152:
            polys.append(p.vertices[:])
    ez = (eyes[0].z + eyes[1].z) / 2
    head = {i for p in polys for i in p if dom[i] in HEAD_BONES}
    return human.surface(verts, polys), [verts[i] for i in head if verts[i].z > ez - 0.02]


def shipped_body(body_id):
    """The finished body (manifest file) as one mesh in the build frame,
    bone weights kept, or None for the base build (no file of its own)."""
    if not body_id:
        return None
    import json
    rows = json.load(open(os.path.join(ART, "manifest.json")))["assets"]
    path = os.path.join(ART, next(a["file"] for a in rows if a["id"] == body_id))
    objs = human._import(path)
    meshes = [o for o in objs if o.type == "MESH" and o.name.split(".")[0] in ("head", "chest", "torso", "body")]   # chest: the shoulders (Phase 19); body: a pre-split build
    human._drop([o for o in objs if o not in meshes])
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.join()
    return bpy.context.view_layer.objects.active


def skull_box(pts, eyes):
    """(eye midpoint, half-width above the ears, (back y, front y), crown z)."""
    mid = (eyes[0] + eyes[1]) / 2
    top = [p for p in pts if p.z > mid.z + 0.04]
    half = max(abs(p.x - mid.x) for p in top)
    return mid, half, (min(p.y for p in top), max(p.y for p in top)), max(p.z for p in pts)


def fit(obj, src, dst):
    """Move a piece from the source head onto the target (module doc)."""
    (sb, seyes, spts), (db, deyes, dpts) = src, dst
    sm, sh, (sy0, sy1), sz = skull_box(spts, seyes)
    dm, dh, (dy0, dy1), dz = skull_box(dpts, deyes)
    kx, ky, kz = dh / sh, (dy1 - dy0) / (sy1 - sy0), (dz - dm.z) / (sz - sm.z)

    def place(co):
        return Vector((dm.x + (co.x - sm.x) * kx,
                       dy0 + (co.y - sy0) * ky,
                       dm.z + (co.z - sm.z) * kz))
    human.refit(obj, sb, db, place, (kx + ky + kz) / 3, reach=0.04, gap=GAP, smooth=8)
    return kx, ky, kz


def _ray(bvh, o, d, far=0.3):
    hit = bvh.ray_cast(o, d, far)
    return None if hit[0] is None else (hit[0] - o).length


def lower_edge(obj, db, mid, yc):
    """Per azimuth (-180..180 by 2 deg, about the vertical axis yc behind the
    eyes), the lowest height above the eyes (m) down to which horizontal rays
    from that axis meet the piece outside the scalp, walking down from the
    crown (gaps under 4 mm -- a strand's edge, a hole -- are bridged)."""
    me = obj.data
    pt = BVHTree.FromPolygons([v.co for v in me.vertices], [p.vertices[:] for p in me.polygons])
    az = np.arange(-180, 181, 2)
    edge = np.full(len(az), np.nan)
    for i, a in enumerate(az):
        d = Vector((math.sin(math.radians(a)), math.cos(math.radians(a)), 0))
        last, miss = None, 0
        for zmm in range(110, -150, -1):
            o = Vector((mid.x, mid.y + yc, mid.z + zmm / 1000))
            hs = _ray(db, o, d)
            if hs is None:
                if last is None:
                    continue
                break
            hp = _ray(pt, o, d)
            if hp is not None and hs - 0.004 < hp < hs + LIE:
                last, miss = zmm, 0
            elif hp is not None and hp >= hs + LIE:
                continue                  # volume standing off the scalp: neither cover nor a gap
            elif last is not None:
                miss += 1
                if miss > 4:
                    break
        if last is not None:
            edge[i] = last / 1000
    return az, edge


def reach_down(obj, db, eyes, line):
    """Stretch a cap piece's lower band down over the painted hairline
    (CAPS): per azimuth the measured edge goes to the line - HAIR_MARGIN,
    the shift fading out over BAND (or twice the drop) above it; every vertex
    keeps its height off the scalp (horizontally, from the axis); nothing
    ends up closer than GAP. Returns the largest drop (m)."""
    mid = (eyes[0] + eyes[1]) / 2
    yc = line["yc"]
    az, edge = lower_edge(obj, db, mid, yc)
    k = np.array(line["line"])
    ok = ~np.isnan(edge)
    edge = np.interp(az, az[ok], edge[ok])
    edge = np.array([np.median(edge[max(0, i - 1):i + 2]) for i in range(len(edge))])   # one-ray blips
    # The drop from the edge's running maximum (+-EDGE_WIN): the piece's own
    # ragged edge (strand tips, a peak) moves down whole, its highest
    # points onto the target, the notches below it.
    w = EDGE_WIN // 2                         # azimuth steps of 2 deg
    top = np.array([edge[max(0, i - w):i + w + 1].max() for i in range(len(edge))])
    drop = np.maximum(0.0, top - (np.interp(np.abs(az), k[:, 0], k[:, 1]) - HAIR_MARGIN))

    def radius(a, z):
        d = Vector((math.sin(math.radians(a)), math.cos(math.radians(a)), 0))
        return _ray(db, Vector((mid.x, mid.y + yc, z)), d)
    for v in obj.data.vertices:
        rel = v.co - Vector((mid.x, mid.y + yc, mid.z))
        a = math.degrees(math.atan2(rel.x, rel.y))
        e, dz = float(np.interp(a, az, edge)), float(np.interp(a, az, drop))
        if dz <= 0:
            continue
        band = max(BAND, 2 * dz)
        z = rel.z
        ramp = 1.0 if z <= e else max(0.0, 1.0 - (z - e) / band)
        if ramp <= 0:
            continue
        r0, r_head0 = math.hypot(rel.x, rel.y), radius(a, v.co.z)
        # never below the line - FLOOR at its own azimuth unless it already
        # was (a neighbouring azimuth's drop threw sideburn tips to the jaw)
        floor = mid.z + float(np.interp(abs(a), k[:, 0], k[:, 1])) - FLOOR
        z1 = max(v.co.z - dz * ramp, min(v.co.z, floor))
        r_head1 = radius(a, z1)
        r1 = r0 if r_head0 is None or r_head1 is None else r_head1 + (r0 - r_head0)
        if abs(r1 - r0) > 0.01:          # the ray met an ear, or left the head: keep the radius
            r1 = r0
        s = r1 / max(r0, 1e-9)
        v.co = Vector((mid.x + rel.x * s, mid.y + yc + rel.y * s, z1))
    for v in obj.data.vertices:
        loc, nrm, d = human.signed(db, v.co)
        if d < GAP:
            v.co = loc + nrm * GAP
    lift_faces(obj, db)
    obj.data.update()
    return float(drop.max())


def lift_faces(obj, db, passes=4):
    """The vertices clear the skin, but a big hair triangle's middle can
    dip under a denser scalp's bulge (skin slivers through the crown):
    sample each face's centre and edge midpoints, lift its vertices by the
    worst shortfall."""
    me = obj.data
    for _ in range(passes):
        push = {}
        for p in me.polygons:
            cs = [me.vertices[i].co for i in p.vertices]
            pts = [sum(cs, Vector()) / len(cs)] + [(cs[i] + cs[(i + 1) % len(cs)]) / 2 for i in range(len(cs))]
            for q in pts:
                loc, nrm, d = human.signed(db, q)
                if d < GAP and (q - loc).length < 0.02:
                    for i in p.vertices:
                        if push.get(i, (0, None))[0] < GAP - d:
                            push[i] = (GAP - d, nrm)
        if not push:
            break
        for i, (k, nrm) in push.items():
            me.vertices[i].co += nrm * k
    return len(push)


def source_for(style, sources):
    """The UBC head a style was authored on (the pack does not say): the one
    fewest of its vertices sink into. Long and buzzed_female sit 1-5 cm
    inside the male head and read as bald patches when fitted from it."""
    obj, _ = human.import_piece(STYLES[style])
    inside = {sex: sum(human.signed(bvh, v.co)[2] < -0.0005 for v in obj.data.vertices)
              for sex, (bvh, _, _) in sources.items()}
    bpy.data.objects.remove(obj, do_unlink=True)
    return min(inside, key=inside.get)


def reduce(obj):
    tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    if tris > BUDGET:
        bpy.context.view_layer.objects.active = obj
        dc = obj.modifiers.new("dec", "DECIMATE")
        dc.ratio = (BUDGET - 10) / tris
        bpy.ops.object.modifier_apply(modifier=dc.name)


def build_for(body_id, sources, src_of):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    human.ACTIVE.clear()
    if body_id:
        v = human.VARIANTS[body_id]
        human.ACTIVE.update({k: v[k] for k in ("macro", "targets", "eye", "ubc") if k in v})
    # The rig (and, for the base build, the surface) from human.py; the
    # surface for a shipped body from its finished glb: the head the hair
    # will actually sit on, after decimation and the import's simplify
    # (fitting to the raw head left the crown poking through in places).
    h, rig, eyes = human.make_human(decimate=not body_id)
    h.data.update()
    shipped = shipped_body(body_id)
    db, dpts = body_surface(shipped or h, eyes)
    dst = (db, eyes, dpts)
    if shipped:
        bpy.data.objects.remove(shipped, do_unlink=True)
    suffix = "@" + body_id if body_id else ""
    for style, pack in STYLES.items():
        obj, tex = human.import_piece(pack)
        reduce(obj)                  # first: the fit's no-poke-through holds for the vertices that ship
        k = fit(obj, sources[src_of[style]], dst)
        line = human.VARIANTS.get(body_id, {}).get("hairline")
        if line and style in CAPS:
            print(f"EXTEND {style}{suffix}: lower edge down by up to {reach_down(obj, db, eyes, line) * 1000:.1f} mm")
        obj.name = obj.data.name = "hair"
        obj.data.materials.clear()
        obj.data.materials.append(human.hair_material(tex))
        g = obj.vertex_groups.new(name="head")
        g.add([v.index for v in obj.data.vertices], 1.0, "REPLACE")
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.ops.object.shade_smooth()
        human.bind(obj, rig)
        out = os.path.join(ART, "build", style + suffix + ".raw.glb")
        rig.select_set(True)
        bpy.ops.export_scene.gltf(
            filepath=out, export_format="GLB", use_selection=True, export_apply=True, export_yup=True,
            export_animations=False, export_skins=True, export_def_bones=False, export_texcoords=True,
            export_normals=True, export_materials="EXPORT", export_image_format="AUTO",
        )
        tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        print(f"HAIR {style}{suffix}: {tris} tris, from UBC {src_of[style]}, scale x{k[0]:.3f} y{k[1]:.3f} z{k[2]:.3f}")
        me = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.meshes.remove(me)          # or the next style's mesh is "hair.001"
        bpy.data.materials.remove(bpy.data.materials["hair"])   # the next style has its own texture


def main():
    only = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sources = {sex: human.ubc_source(sex) for sex in ("m", "f")}
    src_of = {s: source_for(s, sources) for s in STYLES}
    for body_id in BODIES:
        if only and (body_id or "base") not in only:
            continue
        # factory reset clears the scene, not these: BVH trees and vectors are plain data
        build_for(body_id, sources, src_of)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
