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
"""
import json
import math
import os
import shutil

import bpy
from mathutils import Matrix, Vector

ART = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ART, "build", "props")
TEX_MAX = 1024
MEGAKIT = "vendor/scifi-megakit/Modular SciFi MegaKit[Standard]"

PROPS = {
    "prop.chest": dict(
        src=MEGAKIT + "/glTF/Props/Prop_Chest.gltf",
        textures=MEGAKIT + "/Textures",
        # pack mesh object -> our node name, and the bone its origin sits on
        parts={"Prop_Chest": dict(name="chest"), "Prop_Chest_Top": dict(name="lid", pivot="Chest_Top")},
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


def build(oid, p):
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
    for oid, p in PROPS.items():
        build(oid, p)


main()
