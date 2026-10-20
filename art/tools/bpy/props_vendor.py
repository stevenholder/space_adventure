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
        bpy.ops.import_scene.gltf(filepath=stage(f"{oid}/{i}", dict(src=src, textures=p["textures"])))
        new = [o for o in bpy.data.objects if o not in before]
        dg = bpy.context.evaluated_depsgraph_get()
        # glTF frame -> Blender: (x, y, z) -> (x, -z, y).
        to_b = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
        r = Euler([math.radians(a) for a in rot], "XYZ").to_matrix().to_4x4()
        place = (Matrix.Translation((pos[0], -pos[2], pos[1]))
                 @ to_b @ r @ Matrix.Diagonal((scl[0], scl[1], scl[2], 1)) @ to_b.inverted())
        for o in new:
            if o.type != "MESH":
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
