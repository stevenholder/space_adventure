"""Contact sheet of finished bodies, side by side at the same scale.

    blender -b --python tools/render_sheet.py -- <out.png> <group> [<group> ...]

A group is a comma-separated list of .glb files rendered together: a body
first, then whatever it wears (armor, hair). Everything is imported at its
REST pose (A-pose), as built; nothing is re-bound. Each group becomes one row
of the sheet: front / side / back / three-quarter full-length (all groups at
the same orthographic scale, so heights compare honestly), then four head
close-ups (front, side, three-quarter, back) at the same scale too.

EEVEE with a key/fill/rim rig, so a brow ridge or a cheekbone shows as
light and shadow, and material factors (a tinted hair texture) render as
the game draws them. Prints each group's eye height and bounds.

Env: SHEET_TILE (px, default 400); SHEET_FACE (close-up width, m, default
0.34) and SHEET_FACE_DZ (its centre above the eye, default 0.02).
"""
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
out, groups = args[0], [g.split(",") for g in args[1:]]
T = int(os.environ.get("SHEET_TILE", "400"))
tmp = out[:-4] + "_tiles"
os.makedirs(tmp, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE_NEXT"
scene.eevee.taa_render_samples = 16
scene.render.film_transparent = False
world = bpy.data.worlds.new("w")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.05, 0.055, 0.06, 1)
world.node_tree.nodes["Background"].inputs[1].default_value = 0.6
scene.world = world
scene.view_settings.view_transform = "AgX"


def sun(name, rot, energy, col=(1, 1, 1)):
    d = bpy.data.lights.new(name, "SUN")
    d.energy = energy
    d.color = col
    d.angle = math.radians(8)
    o = bpy.data.objects.new(name, d)
    o.rotation_euler = [math.radians(a) for a in rot]
    scene.collection.objects.link(o)


# Front is +Y (the model faces glTF -Z). Key from front-right above, a cool
# fill from the left, a rim from behind.
sun("key", (-55, 0, 155), 4.0, (1.0, 0.96, 0.9))
sun("fill", (-75, 0, -120), 1.2, (0.8, 0.88, 1.0))
sun("rim", (-60, 0, 10), 2.5)

loaded = []
for files in groups:
    objs = []
    for f in files:
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=f)
        objs += list(set(bpy.data.objects) - before)
    for o in objs:
        if o.type == "ARMATURE":
            o.data.pose_position = "REST"
            if o.animation_data:
                o.animation_data.action = None
    loaded.append(objs)
bpy.context.view_layer.update()

cam_data = bpy.data.cameras.new("cam")
cam_data.type = "ORTHO"
cam = bpy.data.objects.new("cam", cam_data)
scene.collection.objects.link(cam)
scene.camera = cam
bpy.ops.mesh.primitive_plane_add(size=4, location=(0, 0, 0))
floor = bpy.context.object


def show(i):
    for j, objs in enumerate(loaded):
        for o in objs:
            o.hide_render = j != i


def shoot(path, target, direction, ortho, w, h):
    scene.render.resolution_x, scene.render.resolution_y = w, h
    cam_data.ortho_scale = ortho
    pos = Vector(target) + Vector(direction).normalized() * 6
    cam.location = pos
    cam.rotation_euler = (Vector(target) - pos).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def load(path):
    im = bpy.data.images.load(path)
    w, h = im.size
    a = np.array(im.pixels[:], dtype=np.float32).reshape(h, w, 4)
    bpy.data.images.remove(im)
    return a


VIEWS = {"front": (0, 1, 0), "side": (1, 0, 0), "back": (0, -1, 0), "q34": (1, 1, 0.25)}
rows = []
for i, objs in enumerate(loaded):
    show(i)
    eye = next((o for o in objs if o.name.startswith("eye")), None)
    ez = eye.matrix_world.translation.z if eye else 1.7
    print(f"GROUP {i} {groups[i][0]} eye {ez:.3f}")
    full, faces = [], []
    for name, d in VIEWS.items():
        p = f"{tmp}/{i}_{name}.png"
        shoot(p, (0, 0, 0.98), d, 2.05, T, 2 * T)
        full.append(load(p))
    floor.hide_render = True
    for name, d in VIEWS.items():
        p = f"{tmp}/{i}_face_{name}.png"
        shoot(p, (0, 0.0, ez + float(os.environ.get("SHEET_FACE_DZ", "0.02"))), d, float(os.environ.get("SHEET_FACE", "0.34")), T, T)
        faces.append(load(p))
    floor.hide_render = False
    # pixels are bottom-up: the lower tile goes first in a vertical stack
    col_a = np.concatenate([faces[1], faces[0]], axis=0)       # front over side
    col_b = np.concatenate([faces[2], faces[3]], axis=0)       # q34 over back
    rows.append(np.concatenate(full + [col_a, col_b], axis=1))
sheet = np.concatenate(rows[::-1], axis=0)
h, w = sheet.shape[:2]
img = bpy.data.images.new("sheet", w, h)
img.pixels = sheet.ravel()
img.filepath_raw = out
img.file_format = "PNG"
img.save()
print("SHEET", out)
