#!/usr/bin/env -S blender -b --python
"""The mob library's Blender pass: art/mobs/mobs.json -> build/mobs/<id>.raw.glb.

    blender -b --python tools/bpy/mobs.py [-- mob.big.orc mob.blob.cat ...]   (run from art/)

For each mob, from its pack file under vendor/ (gitignored; mobs.json `packs`
says where each came from):
  1. clips renamed to the names the client plays (mobs.json `clips`: ours ->
     theirs: idle/walk/sprint/die/hit/attack); the pack's other clips stay,
     snake_cased. A model shipped without clips gets them retargeted from the
     Universal Animation Library (`retarget`), same UE5 bone names;
  2. turned to this project's frame (every pack faces glTF +Z; ours is -Z)
     and lifted so its lowest rest-pose point sits at 0;
  3. textures capped at 1024 px;
  4. measured (height, footprint) into build/mobs/<id>.json, and photographed
     for the catalog (mobs/thumbs/<stem>.png);
  5. exported with every clip. tools/mobs.mjs finishes the import.
"""
import json
import math
import os
import re
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector

ART = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TEX_MAX = 1024
THUMB = 256


def snake(s):
    s = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", s).replace("-", "_").replace(" ", "_")
    return re.sub(r"_+", "_", s).lower().strip("_")


def import_file(path):
    before = set(bpy.data.objects)
    acts = set(bpy.data.actions)
    bpy.ops.import_scene.gltf(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    for o in [o for o in new if o.name.startswith("Icosphere")]:   # the importer's bone widget
        new.remove(o)
        bpy.data.objects.remove(o, do_unlink=True)
    return new, [a for a in bpy.data.actions if a not in acts]


def clip_name(action):
    """The pack's clip name: Blender suffixes duplicates (.001) and some
    exporters prefix 'Armature|'."""
    return action.name.split("|")[-1].split(".")[0]


# ---- retarget ---------------------------------------------------------------
def retarget(src, dst, clip, ours):
    """Bake `clip` from armature `src` onto `dst` as action `ours`. Same bone
    names, different rest poses and proportions: each bone takes the source
    bone's world rotation relative to ITS rest, applied to the target's rest;
    the pelvis also moves, scaled by pelvis height."""
    act = next(a for a in bpy.data.actions if clip_name(a) == clip and a.get("from_src"))
    src.animation_data.action = act
    f0, f1 = (int(round(x)) for x in act.frame_range)
    sb, db = src.data.bones, dst.data.bones
    order = [b.name for b in db if b.name in sb]          # parents before children
    s_pelvis = sb["pelvis"].head_local.z
    d_pelvis = db["pelvis"].head_local.z
    k = d_pelvis / s_pelvis
    out = bpy.data.actions.new(ours)
    dst.animation_data_create()
    dst.animation_data.action = out
    for p in dst.pose.bones:
        p.rotation_mode = "QUATERNION"
    prev = {}
    sc = bpy.context.scene
    for f in range(f0, f1 + 1):
        sc.frame_set(f)
        want = {}
        for n in order:
            sp = src.pose.bones[n]
            delta = sp.matrix.to_quaternion() @ sb[n].matrix_local.to_quaternion().inverted()
            want[n] = delta @ db[n].matrix_local.to_quaternion()
        for n in order:
            b = db[n]
            rest = b.matrix_local.to_quaternion()
            if b.parent and b.parent.name in want:
                pr = b.parent.matrix_local.to_quaternion()
                frame = want[b.parent.name] @ pr.inverted() @ rest
            else:
                frame = rest
            q = frame.inverted() @ want[n]
            if n in prev:
                q.make_compatible(prev[n])
            prev[n] = q
            p = dst.pose.bones[n]
            p.rotation_quaternion = q
            p.keyframe_insert("rotation_quaternion", frame=f - f0 + 1)
        d = (src.pose.bones["pelvis"].head - sb["pelvis"].head_local) * k
        p = dst.pose.bones["pelvis"]
        p.location = db["pelvis"].matrix_local.to_3x3().inverted() @ d
        p.keyframe_insert("location", frame=f - f0 + 1)
    return out


# ---- per mob ------------------------------------------------------------------
def build(mob, scale):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    objs, acts = import_file(os.path.join(ART, mob["src"]))
    arm = next(o for o in objs if o.type == "ARMATURE")
    meshes = [o for o in objs if o.type == "MESH"]

    clips = dict(mob.get("clips", {}))
    if mob.get("retarget"):
        r = mob["retarget"]
        sobjs, sacts = import_file(os.path.join(ART, r["from"]))
        for a in sacts:
            a["from_src"] = True
        src = next(o for o in sobjs if o.type == "ARMATURE")
        made = [retarget(src, arm, theirs, ours) for ours, theirs in r["clips"].items()]
        for o in sobjs:
            bpy.data.objects.remove(o, do_unlink=True)
        for a in sacts:
            bpy.data.actions.remove(a)
        acts = made
        clips = {ours: ours for ours in r["clips"]}

    # Rename: ours <- theirs, the rest snake_cased; every clip on an NLA track
    # so the exporter writes it.
    theirs = {v: k for k, v in clips.items()}
    arm.animation_data_create()
    for t in list(arm.animation_data.nla_tracks):
        arm.animation_data.nla_tracks.remove(t)
    names = []
    for a in acts:
        n = theirs.get(clip_name(a)) or snake(clip_name(a))
        a.name = n
        names.append(a.name)
        t = arm.animation_data.nla_tracks.new()
        t.name = a.name
        t.strips.new(a.name, int(a.frame_range[0]), a)
    missing = [k for k in clips if k not in names]
    if missing:
        raise SystemExit(f"{mob['id']}: clips not found: {missing} (have {sorted(names)})")

    # Rest-pose bounds, in the import frame (glTF +Z front = Blender -Y).
    arm.animation_data.action = None
    arm.data.pose_position = "REST"
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    pts = []
    for m in meshes:
        ev = m.evaluated_get(dg)
        me = ev.to_mesh()
        pts += [ev.matrix_world @ v.co for v in me.vertices]
        ev.to_mesh_clear()
    arm.data.pose_position = "POSE"
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))

    # Our frame: front +Y in Blender (-Z in glTF), feet at 0, family scale
    # (Ultimate Monsters is authored at toy scale; mobs.json `families`).
    arm.matrix_world = (Matrix.Translation((0, 0, -lo.z * scale)) @ Matrix.Rotation(math.pi, 4, "Z")
                        @ Matrix.Scale(scale, 4) @ arm.matrix_world)

    for img in bpy.data.images:
        if img.size[0] > TEX_MAX:
            img.scale(TEX_MAX, max(1, TEX_MAX * img.size[1] // img.size[0]))

    size = (hi - lo) * scale
    meta = {
        "id": mob["id"],
        "scale": scale,
        "height": round(size.z, 3),
        "width": round(size.x, 3),
        "depth": round(size.y, 3),
        "clips": sorted(names),
        "tris": sum(len(p.vertices) - 2 for m in meshes for p in m.data.polygons),
    }
    stem = mob["id"].split(".", 1)[1].replace(".", "_")
    thumb(arm, meshes, size, os.path.join(ART, "mobs", "thumbs", stem + ".png"))

    out = os.path.join(ART, "build", "mobs", mob["id"] + ".raw.glb")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    for o in [arm] + meshes:
        o.select_set(True)
    bpy.ops.export_scene.gltf(
        filepath=out, export_format="GLB", use_selection=True, export_yup=True,
        export_animations=True, export_animation_mode="ACTIONS", export_force_sampling=True,
        export_skins=True, export_def_bones=False, export_texcoords=True, export_normals=True,
        export_materials="EXPORT", export_image_format="AUTO",
    )
    json.dump(meta, open(out.replace(".raw.glb", ".json"), "w"), indent=1)
    print("wrote", out, meta["height"], "m", meta["tris"], "tris", meta["clips"])


def thumb(arm, meshes, size, path):
    """A front three-quarter photo in the idle pose, framed on the model."""
    sc = bpy.context.scene
    idle = bpy.data.actions.get("idle")
    if idle:
        arm.animation_data.action = idle
        sc.frame_set(int(sum(idle.frame_range) / 2))
    bpy.context.view_layer.update()
    h = max(size.z, 0.3)
    span = max(size.x, size.y, size.z)
    centre = Vector((0, 0, h * 0.5))
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    sc.collection.objects.link(cam)
    cam.data.lens = 50
    d = Vector((0.55, 1.0, 0.35)).normalized()
    cam.location = centre + d * span * 1.55
    cam.rotation_euler = (centre - cam.location).to_track_quat("-Z", "Y").to_euler()
    sc.camera = cam
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 3.0
    sun.rotation_euler = (math.radians(50), math.radians(15), math.radians(150))
    sc.collection.objects.link(sun)
    w = bpy.data.worlds.new("w")
    w.use_nodes = True
    w.node_tree.nodes["Background"].inputs[0].default_value = (0.20, 0.22, 0.26, 1)
    w.node_tree.nodes["Background"].inputs[1].default_value = 1.0
    sc.world = w
    for eng in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
        try:
            sc.render.engine = eng
            break
        except TypeError:
            pass
    sc.view_settings.view_transform = "Standard"
    sc.render.resolution_x = sc.render.resolution_y = THUMB
    sc.render.image_settings.file_format = "PNG"
    sc.render.filepath = path
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.render.render(write_still=True)
    for o in (cam, sun):
        bpy.data.objects.remove(o, do_unlink=True)
    arm.animation_data.action = None
    sc.frame_set(1)


def main():
    lib = json.load(open(os.path.join(ART, "mobs", "mobs.json")))
    want = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    for mob in lib["mobs"]:
        if want and mob["id"] not in want:
            continue
        build(mob, mob.get("scale") or lib["families"][mob["family"]]["scale"])


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
