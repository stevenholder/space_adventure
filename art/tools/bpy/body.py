#!/usr/bin/env -S blender -b --python
"""Generate the humanoid body -- build/char.player.raw.glb -- headless in Blender.

    blender -b --python tools/bpy/body.py          (run from art/)

The raw file is NOT what the game ships: recipes/char.player.json feeds it
through tools/import_pack.mjs like any other source model (bake COLOR_0, add
the eye mount, count triangles). build/ is gitignored; this script plus the
recipe reproduce the shipped chars/player.glb from a clean clone.

ONE CONTINUOUS SKINNED MESH, not a union of primitives. The body is a stick
figure -- one vertex per joint, edges along the limbs, a radius per vertex --
that Blender's Skin modifier wraps in a single closed surface, subdivided once
and flat-shaded so it facets like the terrain. Bones are the same stick
figure; every vertex is weighted to its nearest bone segment, blending across
a joint, so elbows and knees BEND instead of hinging two tubes. The head is a
second mesh on the same armature so the local player can still draw its own
head shadows-only (README "segmented, not skinned" is now "two meshes, one
skeleton": the contract names are bones, and `hand.r` is an empty parented to
the forearm bone, which the glTF exporter and Godot both turn into a
bone-attached node).

CLIPS ARE AUTHORED HERE, as rotation keys on the pose bones, so nothing in
the shipped model comes off a downloaded pack any more.

THE SKELETON IS THE CONTRACT. Every humanoid and every armor piece is authored
against the joints in JOINTS below -- art/README.md prints the same table. An
armor piece is another skin-modifier mesh over the same joints with larger
radii, weighted the same way, so it follows the same bones.

Frame: Blender is Z-up and the exporter maps Blender (x, y, z) to glTF
(x, z, -y), so the model's FRONT is Blender +Y here and lands on glTF -Z,
which is what import_pack.mjs expects at yaw 0. +X is the model's RIGHT.
"""
import math
import os
import sys

import bpy
from mathutils import Vector

# ---- skeleton (metres; x right, y front, z up; final scale) -----------------
EYE = 1.70          # root mount; GDD eye_height, matches server shot origin

# Joint positions. A limb is a chain of these; the stick-figure mesh and the
# bones both come from this one table.
JOINTS = {
    "pelvis":   (0.00, 0.00, 0.92),
    "chest":    (0.00, 0.00, 1.30),
    "neck":     (0.00, 0.00, 1.52),
    "skull":    (0.00, 0.00, 1.68),
    "crown":    (0.00, 0.00, 1.78),
    "shoulder": (0.22, 0.00, 1.45),
    "elbow":    (0.27, 0.00, 1.19),
    "wrist":    (0.28, 0.00, 0.97),
    "hand":     (0.28, 0.02, 0.88),
    "hip":      (0.11, 0.00, 0.90),
    "knee":     (0.12, 0.00, 0.50),
    "ankle":    (0.12, 0.00, 0.10),
    "toe":      (0.12, 0.13, 0.03),
}

# Skin-modifier radius (x, y) per joint: the body's thickness at that joint.
RADII = {
    "pelvis": (0.17, 0.11), "chest": (0.20, 0.12), "neck": (0.07, 0.07),
    "skull": (0.105, 0.11), "crown": (0.085, 0.09),
    "shoulder": (0.075, 0.075), "elbow": (0.05, 0.05), "wrist": (0.04, 0.04),
    "hand": (0.045, 0.03),
    "hip": (0.09, 0.09), "knee": (0.07, 0.07), "ankle": (0.06, 0.06), "toe": (0.065, 0.035),
}

# Bones: name -> (head joint, tail joint, parent). Contract names first.
BONES = [
    ("root",      "ground", "pelvis", None),
    ("torso",     "pelvis", "neck",   "root"),
    ("head",      "neck",   "crown",  "torso"),
    ("arm.r",     "shoulder.r", "elbow.r", "torso"),
    ("forearm.r", "elbow.r",    "wrist.r", "arm.r"),
    ("arm.l",     "shoulder.l", "elbow.l", "torso"),
    ("forearm.l", "elbow.l",    "wrist.l", "arm.l"),
    ("leg.r",     "hip.r",  "knee.r",  "root"),
    ("shin.r",    "knee.r", "ankle.r", "leg.r"),
    ("leg.l",     "hip.l",  "knee.l",  "root"),
    ("shin.l",    "knee.l", "ankle.l", "leg.l"),
]

# ---- palette (linear RGB, baked into COLOR_0 by import_pack) ----------------
PALETTE = {
    "suit": (0.36, 0.44, 0.55),      # jumpsuit, blue-grey
    "trouser": (0.22, 0.24, 0.30),
    "accent": (0.93, 0.42, 0.10),    # chest stripe, cuffs
    "skin": (0.78, 0.60, 0.48),
    "hair": (0.20, 0.12, 0.08),
    "boot": (0.15, 0.15, 0.17),
    "eye": (0.08, 0.08, 0.10),
    "bone": (0.90, 0.86, 0.74),
    "armor": (0.42, 0.46, 0.30),    # scout set, olive canvas
    "iron": (0.38, 0.40, 0.44),
    "strap": (0.20, 0.16, 0.12),
}
MATERIAL_ORDER = ["suit", "trouser", "accent", "skin", "hair", "boot", "eye", "bone", "armor", "iron", "strap"]

# Armor, the way WoW does it: no inflated shell of the body.
#   "paint": the body's own faces at a 3 mm offset with the body's own weights
#            -- cloth and light armor over torso, legs, hands, feet. It moves
#            exactly like the body, so it cannot stretch or tear against it.
#            (The texture layer, until we have textures.)
#   "rigid": pushed out by `grow` + `pad` and weighted 100% to ONE bone --
#            helmet, breastplate, pack. Never deforms; overlaps the joint.
# Rules take a face centre (x right, y front, z up; bulk 1.0), the body part
# (`body_part`) and the face's material. Trims are colour bands.
ARMOR = {
    "armor.helmet.scout": {
        "src": "head", "mode": "rigid", "bone": "head", "grow": 1.20, "pad": 0.025,
        "keep": lambda c, b, m: m == "skin" and c.z > 1.56,
        "mat": lambda c, b: "eye" if (c.y > 0.06 and 1.66 < c.z < 1.73) else "armor",
    },
    "armor.suit.scout": {   # a pressure suit: full sleeves, belt
        "src": "body", "mode": "paint",
        "keep": lambda c, b, m: (b == "torso" and c.z > 0.98) or b == "arm" or (b == "forearm" and c.z > 0.99),
        "mat": lambda c, b: "strap" if c.z < 1.075 else "armor",
    },
    "armor.plate.iron": {   # a breastplate: torso and shoulder caps, rigid on the torso bone
        "src": "body", "mode": "rigid", "bone": "torso", "grow": 1.12, "pad": 0.025,
        "keep": lambda c, b, m: c.z > 1.05 and (b == "torso" or (b == "arm" and c.z > 1.36)),
        "mat": lambda c, b: "strap" if (c.z < 1.09 or (abs(c.x) < 0.03 and c.y > 0)) else "iron",
    },
    "armor.legs.scout": {   # trousers from the belt down, knee bands
        "src": "body", "mode": "paint",
        "keep": lambda c, b, m: (b in ("leg", "shin") or (b == "torso" and c.z < 1.10)) and c.z > 0.15,
        "mat": lambda c, b: "strap" if 0.45 < c.z < 0.55 else "armor",
    },
    "armor.gloves.scout": {
        "src": "body", "mode": "paint",
        "keep": lambda c, b, m: b == "forearm" and c.z < 1.01,
        "mat": lambda c, b: "armor" if c.z > 0.96 else "strap",
    },
    "armor.boots.scout": {
        "src": "body", "mode": "paint",
        "keep": lambda c, b, m: b == "shin" and c.z < 0.21,
        "mat": lambda c, b: "armor" if c.z > 0.16 else "strap",
    },
    "pack.scout": {
        "src": None, "mode": "rigid", "bone": "torso",
        "extras": [("pack", (0, -0.20, 1.24), (0.30, 0.14, 0.36), "armor"),
                   ("strap.r", (0.12, 0.0, 1.35), (0.04, 0.26, 0.05), "strap"),
                   ("strap.l", (-0.12, 0.0, 1.35), (0.04, 0.26, 0.05), "strap")],
    },
}

# One script, every humanoid. A RACE is a head build plus a palette; a
# VARIANT picks a race and overrides palette entries, body thickness (`bulk`,
# not the head) and shoulder width. The joints, bones, weights and clips are
# the shared skeleton underneath all of them, which is what lets one armor
# piece fit every race.
RACES = {
    "human": {},
    "orc": {
        "palette": {"skin": (0.36, 0.52, 0.30), "hair": (0.08, 0.07, 0.06), "eye": (0.55, 0.12, 0.08)},
        "bulk": 1.20, "shoulders": 1.15,
    },
    "robot": {
        "palette": {"skin": (0.22, 0.24, 0.27), "suit": (0.55, 0.58, 0.62), "trouser": (0.36, 0.38, 0.42),
                    "accent": (0.93, 0.60, 0.10), "eye": (1.0, 0.35, 0.10), "boot": (0.20, 0.21, 0.24)},
    },
    "android": {
        "palette": {"skin": (0.80, 0.82, 0.86), "suit": (0.86, 0.87, 0.90), "trouser": (0.30, 0.32, 0.38),
                    "accent": (0.20, 0.85, 0.95), "eye": (0.20, 0.85, 0.95), "boot": (0.18, 0.19, 0.22)},
        "bulk": 0.96,
    },
}

VARIANTS = {
    # char.player and the Scout armor come from tools/bpy/human.py and
    # armor.py (MakeHuman body, game-engine rig) since 2026-10-01. This file
    # still builds the NPC bodies on the old 11-bone skeleton.
    "npc.shopkeeper": {   # the quartermaster: human, khaki, a little stocky
        "race": "human",
        "palette": {"suit": (0.52, 0.46, 0.34), "accent": (0.80, 0.72, 0.55), "hair": (0.62, 0.60, 0.58),
                    "trouser": (0.28, 0.26, 0.24)},
        "bulk": 1.10,
    },
    "npc.dispatcher": {"race": "android"},
    "npc.grunt": {        # the melee raider: an orc in a rust jacket
        "race": "orc",
        "palette": {"suit": (0.58, 0.22, 0.14), "accent": (0.16, 0.12, 0.10), "trouser": (0.20, 0.18, 0.18)},
    },
    "npc.gunner": {       # the ranged raider: a robot, hazard-striped
        "race": "robot",
    },
}
ACTIVE = {"palette": dict(PALETTE), "bulk": 1.0, "shoulders": 1.0, "race": "human"}


def joint(name):
    """A joint by name; `x.r` / `x.l` mirror the table across x."""
    if name == "ground":
        return Vector((0, 0, 0))
    base, _, side = name.partition(".")
    p = Vector(JOINTS[base])
    if base in ("shoulder", "elbow", "wrist", "hand"):
        p.x *= ACTIVE["shoulders"]
    if side == "l":
        p.x = -p.x
    return p


def material(name):
    m = bpy.data.materials.get(name)
    if m is None:
        m = bpy.data.materials.new(name)
        m.use_nodes = True
        bsdf = m.node_tree.nodes["Principled BSDF"]
        bsdf.inputs["Base Color"].default_value = (*ACTIVE["palette"][name], 1.0)
        m.diffuse_color = (*ACTIVE["palette"][name], 1.0)  # Workbench renders (tools/bpy probes) read this one
        bsdf.inputs["Roughness"].default_value = 1.0
    return m


def add_materials(obj):
    for n in MATERIAL_ORDER:
        obj.data.materials.append(material(n))


def apply_all(obj):
    bpy.context.view_layer.objects.active = obj
    for m in list(obj.modifiers):
        bpy.ops.object.modifier_apply(modifier=m.name)


# ---- skin-modifier stick figure ---------------------------------------------
def skin_mesh(name, chain_joints, edges, subdiv=1, grow=1.0, pad=0.0):
    """A closed surface around a stick figure. chain_joints: list of joint
    names (index = vertex); edges: index pairs."""
    verts = [joint(j) for j in chain_joints]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([tuple(v) for v in verts], edges, [])
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)

    skin = obj.modifiers.new("skin", "SKIN")
    skin.use_smooth_shade = False
    # The modifier needs its per-vertex layer; adding it in edit mode is the
    # only way that works headless.
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.object.skin_root_mark()
    bpy.ops.object.mode_set(mode="OBJECT")
    layer = mesh.skin_vertices[0].data
    # One root per connected chain (a leg pair is two): the Skin modifier
    # skips any component without one.
    roots = set()
    seen = set()
    for a, b in edges:
        if a not in seen and b not in seen:
            roots.add(a)
        seen.update((a, b))
    for i, j in enumerate(chain_joints):
        base = j.partition(".")[0]
        r = RADII[base]
        if base not in ("neck", "skull", "crown"):
            r = (r[0] * ACTIVE["bulk"], r[1] * ACTIVE["bulk"])
        layer[i].radius = (r[0] * grow + pad, r[1] * grow + pad)
        layer[i].use_root = i in roots
    sub = obj.modifiers.new("subdiv", "SUBSURF")
    sub.levels = subdiv
    sub.render_levels = subdiv
    apply_all(obj)
    add_materials(obj)
    return obj


def box(center, size, mat, bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=center)
    obj = bpy.context.object
    obj.scale = Vector(size)
    bpy.ops.object.transform_apply(scale=True)
    add_materials(obj)
    for p in obj.data.polygons:
        p.material_index = MATERIAL_ORDER.index(mat)
    if bevel:
        mod = obj.modifiers.new("bevel", "BEVEL")
        mod.width = bevel
        mod.segments = 1
        apply_all(obj)
    return obj


def join(objs, name):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    obj = bpy.context.object
    obj.name = name
    obj.data.name = name
    # The joined object inherits the FIRST part's transform (a box's origin is
    # its centre). Bake it into the vertices: a skinned mesh whose object sits
    # off the armature origin exports inverse binds that carry that offset,
    # and the piece then renders displaced by it (the pack showed up on the
    # chest, in front).
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.ops.object.shade_flat()
    return obj


# ---- dressing: a material per face from where the face is -------------------
def dress_body(obj):
    idx = {n: i for i, n in enumerate(MATERIAL_ORDER)}
    for p in obj.data.polygons:
        c = obj.matrix_world @ p.center
        ax = abs(c.x)
        if c.z < 0.16:
            m = "boot"
        elif ax > 0.19 and c.z < 0.955:
            m = "skin"                        # hands
        elif ax > 0.19 and c.z < 1.01:
            m = "accent"                      # cuffs
        elif c.z < 1.04 and ax < 0.21:
            m = "trouser"                     # up to the belt line: seat and crotch too
        elif c.z > 1.505 and ax < 0.10:
            m = "skin"                        # neck
        else:
            m = "suit"
        p.material_index = idx[m]


# ---- weights: nearest bone segment, blended across the joint ----------------
def seg_dist(p, a, b):
    ab = b - a
    t = max(0.0, min(1.0, (p - a).dot(ab) / ab.length_squared))
    return (p - (a + ab * t)).length


# Garment vertex -> the base body position it was pushed out from, so a
# garment is weighted exactly like the body under it. Weighted from its own
# pushed-out position, a vertex near the armpit sits closer to the arm bone
# than its body vertex does, gets the arm's weight, and flies with the arm
# when it raises -- a hole in the flank in every armed pose.
SHELL_BASE = {}


def weight(obj, bones, blend=0.05):
    segs = [(n, joint(h), joint(t)) for n, h, t, _ in bones]
    groups = {n: obj.vertex_groups.new(name=n) for n, _, _ in segs}
    base = SHELL_BASE.get(obj.name)
    for v in obj.data.vertices:
        p = obj.matrix_world @ (Vector(base[v.index]) if base and v.index < len(base) else v.co)
        d = sorted((seg_dist(p, a, b), n) for n, a, b in segs)
        (d0, n0), (d1, n1) = d[0], d[1]
        gap = d1 - d0
        if gap < blend:
            w1 = 0.5 * (1.0 - gap / blend)
            groups[n0].add([v.index], 1.0 - w1, "REPLACE")
            groups[n1].add([v.index], w1, "REPLACE")
        else:
            groups[n0].add([v.index], 1.0, "REPLACE")


def armature():
    arm = bpy.data.armatures.new("rig")
    obj = bpy.data.objects.new("rig", arm)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    made = {}
    for name, h, t, parent in BONES:
        b = arm.edit_bones.new(name)
        b.head = joint(h)
        b.tail = joint(t)
        b.roll = 0.0
        if parent:
            b.parent = made[parent]
        made[name] = b
    bpy.ops.object.mode_set(mode="OBJECT")
    return obj


def bind(mesh_obj, rig):
    mod = mesh_obj.modifiers.new("rig", "ARMATURE")
    mod.object = rig
    mesh_obj.parent = rig


def mount(name, parent_bone, rig, offset):
    """An empty riding a bone: what Godot turns into a BoneAttachment3D."""
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.05
    bpy.context.scene.collection.objects.link(e)
    e.parent = rig
    e.parent_type = "BONE"
    e.parent_bone = parent_bone
    # Bone-parent space has its origin at the bone's TAIL, y along the bone.
    e.location = Vector(offset)
    return e


# ---- clips ------------------------------------------------------------------
def deg(*a):
    return tuple(math.radians(x) for x in a)


def clip(rig, name, seconds, keys, loop=True):
    """keys: {bone: [(t01, (rx, ry, rz) degrees), ...]}. Every bone is keyed
    at t=0 so a clip fully overrides the one before it."""
    fps = bpy.context.scene.render.fps
    frames = max(1, round(seconds * fps))
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    rig.animation_data_create()
    rig.animation_data.action = act
    for pb in rig.pose.bones:
        pb.rotation_mode = "XYZ"
    for pb in rig.pose.bones:
        ks = keys.get(pb.name, [(0.0, (0, 0, 0))])
        if ks[0][0] != 0.0:
            ks = [(0.0, (0, 0, 0))] + ks
        if loop and ks[-1][0] != 1.0:
            ks = ks + [(1.0, ks[0][1])]
        for t, rot in ks:
            pb.rotation_euler = deg(*rot)
            pb.keyframe_insert("rotation_euler", frame=1 + t * frames)
    bpy.context.scene.frame_end = max(bpy.context.scene.frame_end, 1 + frames)
    for fc in act.fcurves:
        for kp in fc.keyframe_points:
            kp.interpolation = "LINEAR" if not loop else "BEZIER"


# The aim pose: right arm forward on the grip, left arm across to the
# fore-end. Bone x swings a down-pointing bone forward (POSITIVE) or back;
# z swings it across the body. Overrides the arm keys of any gait.
AIM = {
    "arm.r": [(0.0, (12, 0, 15))], "forearm.r": [(0.0, (85, 0, 45))],
    "arm.l": [(0.0, (80, 40, 0))], "forearm.l": [(0.0, (10, 0, 0))],
}

# First-person poses (arm bones only; everything else rests). Found with
# tools/bpy/probe_fp.py, which sweeps angles and ranks them against a wrist
# target in the body frame (+Y front, Z up, eye at 1.70):
#   aim     the hold: forearms forward, elbows down (ViewModel frames it)
#   ads     rifle centred and level; the client lifts it onto the eye line
#   lower   barrel ~45 degrees down, for walls and crowding
#   unarmed empty hands low in frame
FP = {
    # The first-person hold is NOT the third-person low ready: AIM flares the
    # right elbow across the chest, and lifted to eye height (ViewModel
    # framing) that upper arm blocks the view. Forearms forward, elbows down.
    "aim": {
        "arm.r": [(0.0, (0, 0, 60))], "forearm.r": [(0.0, (80, 0, 10))],
        "arm.l": [(0.0, (80, 30, 0))], "forearm.l": [(0.0, (0, 0, 0))],
    },
    # ads: the rifle centred and level (probe: wrist.r (0.01, 0.26, 1.29),
    # barrel dir (-0.07, 0.99, 0.07)). The shoulders are 0.25 m under the eye,
    # past what an arm can reach, so the client lifts the arms holder until
    # the rear sight sits on the eye line (ViewModel ADS offset) -- a real
    # shooter drops the head to the stock; ours brings the stock up.
    "ads": {
        "arm.r": [(0.0, (0, 0, 60))], "forearm.r": [(0.0, (80, 0, 10))],
        "arm.l": [(0.0, (80, 30, 0))], "forearm.l": [(0.0, (0, 0, 0))],
    },
    "lower": {
        "arm.r": [(0.0, (40, 0, 10))], "forearm.r": [(0.0, (30, 0, 20))],
        "arm.l": [(0.0, (45, 20, 0))], "forearm.l": [(0.0, (20, 0, 0))],
    },
    # unarmed: forearms forward and loosely up, hands at the bottom corners
    # of the view (the client lifts the arms like the armed hold).
    "unarmed": {
        "arm.r": [(0.0, (20, 0, 10))], "forearm.r": [(0.0, (70, 0, 0))],
        "arm.l": [(0.0, (20, 0, -10))], "forearm.l": [(0.0, (70, 0, 0))],
    },
}


def gait(rig, name, seconds, leg, knee, arm, elbow, lean, bob, armed=False):
    """A two-beat walk cycle. x rotation swings forward/back for a bone that
    points down; the right leg leads at t=0, the left at t=0.5."""
    def swing(a, phase):
        return [(0.0, (a * phase, 0, 0)), (0.5, (-a * phase, 0, 0)), (1.0, (a * phase, 0, 0))]

    def bend(a, phase):
        # A knee bends most while that leg is swinging forward (behind then
        # coming through), so it peaks a quarter cycle after the back swing.
        if phase > 0:
            return [(0.0, (0, 0, 0)), (0.25, (0, 0, 0)), (0.5, (0, 0, 0)), (0.75, (a, 0, 0)), (1.0, (0, 0, 0))]
        return [(0.0, (0, 0, 0)), (0.25, (a, 0, 0)), (0.5, (0, 0, 0)), (0.75, (0, 0, 0)), (1.0, (0, 0, 0))]

    keys = {
        "leg.r": swing(leg, 1), "leg.l": swing(leg, -1),
        "shin.r": bend(knee, 1), "shin.l": bend(knee, -1),
        "arm.r": swing(arm, -1), "arm.l": swing(arm, 1),
        "forearm.r": [(0.0, (elbow, 0, 0))], "forearm.l": [(0.0, (elbow, 0, 0))],
        "torso": [(0.0, (lean, 0, bob)), (0.5, (lean, 0, -bob)), (1.0, (lean, 0, bob))],
        "head": [(0.0, (-lean * 0.6, 0, 0))],
    }
    if armed:
        keys.update(AIM)
    clip(rig, name, seconds, keys)


def clips(rig):
    bpy.context.scene.render.fps = 30
    clip(rig, "idle", 2.0, {
        "torso": [(0.0, (0, 0, 0)), (0.5, (1.5, 0, 0)), (1.0, (0, 0, 0))],
        "head": [(0.0, (0, 0, 0)), (0.5, (-1.0, 0, 0)), (1.0, (0, 0, 0))],
        "arm.r": [(0.0, (0, 0, -4)), (0.5, (0, 0, -5.5)), (1.0, (0, 0, -4))],
        "arm.l": [(0.0, (0, 0, 4)), (0.5, (0, 0, 5.5)), (1.0, (0, 0, 4))],
        "forearm.r": [(0.0, (8, 0, 0))], "forearm.l": [(0.0, (8, 0, 0))],
    })
    gait(rig, "walk", 0.70, leg=28, knee=38, arm=22, elbow=15, lean=3, bob=2)
    gait(rig, "sprint", 0.50, leg=45, knee=65, arm=45, elbow=40, lean=12, bob=3)
    # Holding a weapon: the same gaits with the arms in the aim pose. The
    # client plays "<gait>_armed" when the body has something in hand.
    clip(rig, "idle_armed", 2.0, {
        "torso": [(0.0, (0, 0, 0)), (0.5, (1.5, 0, 0)), (1.0, (0, 0, 0))],
        "head": [(0.0, (0, 0, 0)), (0.5, (-1.0, 0, 0)), (1.0, (0, 0, 0))],
        **AIM,
    })
    gait(rig, "walk_armed", 0.70, leg=28, knee=38, arm=0, elbow=0, lean=3, bob=2, armed=True)
    gait(rig, "sprint_armed", 0.50, leg=45, knee=65, arm=0, elbow=0, lean=12, bob=3, armed=True)
    # First person: what the player's own arms do, played on a second body
    # instance hung under the camera (client ViewModel). Every bone is keyed
    # like the other clips (full override); torso/head/root stay at rest so
    # the eye-to-shoulder offset never moves. Poses from tools/bpy/probe_fp.py.
    breathe = {
        "torso": [(0.0, (0, 0, 0)), (0.5, (1.0, 0, 0)), (1.0, (0, 0, 0))],
    }
    clip(rig, "fp_idle", 3.0, {
        **FP["aim"],
        "forearm.r": [(0.0, FP["aim"]["forearm.r"][0][1]), (0.5, (FP["aim"]["forearm.r"][0][1][0] + 1.5, 0, FP["aim"]["forearm.r"][0][1][2])), (1.0, FP["aim"]["forearm.r"][0][1])],
        "forearm.l": [(0.0, FP["aim"]["forearm.l"][0][1]), (0.5, (FP["aim"]["forearm.l"][0][1][0] + 1.5, 0, 0)), (1.0, FP["aim"]["forearm.l"][0][1])],
    })
    for name, seconds, amp in (("fp_walk", 0.70, 2.0), ("fp_sprint", 0.50, 4.0)):
        ar, fr, al, fl = (FP["aim"][b][0][1] for b in ("arm.r", "forearm.r", "arm.l", "forearm.l"))
        clip(rig, name, seconds, {
            "arm.r": [(0.0, ar), (0.5, (ar[0], ar[1], ar[2] + amp)), (1.0, ar)],
            "arm.l": [(0.0, al), (0.5, (al[0], al[1], al[2] - amp)), (1.0, al)],
            "forearm.r": [(0.0, fr), (0.25, (fr[0] + amp, fr[1], fr[2])), (0.75, (fr[0] - amp, fr[1], fr[2])), (1.0, fr)],
            "forearm.l": [(0.0, fl), (0.25, (fl[0] + amp, fl[1], fl[2])), (0.75, (fl[0] - amp, fl[1], fl[2])), (1.0, fl)],
        })
    clip(rig, "fp_ads", 1.0, {**FP["ads"], **breathe})
    clip(rig, "fp_lower", 1.0, FP["lower"])
    clip(rig, "fp_unarmed", 3.0, {
        **FP["unarmed"],
        "arm.r": [(0.0, FP["unarmed"]["arm.r"][0][1]), (0.5, (FP["unarmed"]["arm.r"][0][1][0] + 2, 0, FP["unarmed"]["arm.r"][0][1][2])), (1.0, FP["unarmed"]["arm.r"][0][1])],
        "arm.l": [(0.0, FP["unarmed"]["arm.l"][0][1]), (0.5, (FP["unarmed"]["arm.l"][0][1][0] + 2, 0, FP["unarmed"]["arm.l"][0][1][2])), (1.0, FP["unarmed"]["arm.l"][0][1])],
    })
    # One-shots. Recoil: the right forearm jerks back and up and settles;
    # the client plays this at the moment of fire. Reload: the left hand drops
    # to the magazine, works it, comes back to the fore-end; 2.0 s is the
    # weapon's reload_time (server/data/items.json), and the client scales
    # playback to the live value.
    ar, fr, al, fl = (FP["aim"][b][0][1] for b in ("arm.r", "forearm.r", "arm.l", "forearm.l"))
    clip(rig, "fp_fire", 0.12, {
        "arm.r": [(0.0, ar), (0.3, (ar[0] - 4, ar[1], ar[2])), (1.0, ar)],
        "forearm.r": [(0.0, fr), (0.3, (fr[0] + 6, fr[1], fr[2])), (1.0, fr)],
        "arm.l": [(0.0, al), (0.3, (al[0] - 2, al[1], al[2])), (1.0, al)],
        "forearm.l": [(0.0, fl)],
    }, loop=False)
    clip(rig, "fp_reload", 2.0, {
        "arm.r": [(0.0, ar)], "forearm.r": [(0.0, fr)],
        "arm.l": [(0.0, al), (0.25, (40, 15, 0)), (0.6, (42, 15, 5)), (0.9, al), (1.0, al)],
        "forearm.l": [(0.0, fl), (0.25, (60, 0, 20)), (0.45, (70, 0, 25)), (0.6, (60, 0, 20)), (0.9, fl), (1.0, fl)],
    }, loop=False)
    # Falls onto its back: the root pitches over at the ground, the legs
    # come up with it, the arms fling out.
    clip(rig, "die", 0.45, {
        "root": [(0.0, (0, 0, 0)), (0.25, (-25, 0, 0)), (1.0, (-88, 0, 0))],
        "torso": [(0.0, (0, 0, 0)), (1.0, (-6, 0, 0))],
        "head": [(0.0, (0, 0, 0)), (0.6, (12, 0, 0)), (1.0, (18, 0, 0))],
        "arm.r": [(0.0, (0, 0, 0)), (1.0, (-40, 0, -50))],
        "arm.l": [(0.0, (0, 0, 0)), (1.0, (-40, 0, 50))],
        "leg.r": [(0.0, (0, 0, 0)), (1.0, (8, 0, -6))],
        "leg.l": [(0.0, (0, 0, 0)), (1.0, (8, 0, 6))],
    }, loop=False)
    rig.animation_data.action = None



# ---- heads, per race --------------------------------------------------------
def build_head(race):
    """The head mesh: its own object so the local player can hide it (README:
    the camera is inside it). What makes a race read at 20 m lives here."""
    if race == "robot":
        # A box head on a short neck: one wide visor, an antenna. No skin.
        neck = box((0, 0, 1.545), (0.09, 0.09, 0.06), "skin", bevel=0.01)
        skull = box((0, 0, 1.675), (0.21, 0.22, 0.20), "suit", bevel=0.02)
        visor = box((0, 0.105, 1.695), (0.15, 0.02, 0.045), "eye")
        jaw = box((0, 0.10, 1.615), (0.13, 0.03, 0.03), "trouser")
        antenna = box((0.07, -0.04, 1.82), (0.015, 0.015, 0.10), "skin")
        tip = box((0.07, -0.04, 1.875), (0.03, 0.03, 0.02), "accent")
        return join([neck, skull, visor, jaw, antenna, tip], "head")

    head = head_skin()
    for p in head.data.polygons:
        p.material_index = MATERIAL_ORDER.index("skin")
    parts = [head]
    if race == "orc":
        # Heavy brow, tusks up from the jaw, pointed ears, a topknot.
        parts.append(box((0, 0.095, 1.725), (0.17, 0.04, 0.035), "skin", bevel=0.01))
        parts.append(box((-0.035, 0.105, 1.625), (0.02, 0.02, 0.05), "bone"))
        parts.append(box((0.035, 0.105, 1.625), (0.02, 0.02, 0.05), "bone"))
        parts.append(box((-0.115, -0.01, 1.70), (0.035, 0.03, 0.07), "skin"))
        parts.append(box((0.115, -0.01, 1.70), (0.035, 0.03, 0.07), "skin"))
        parts.append(box((0, -0.03, 1.79), (0.06, 0.07, 0.05), "hair", bevel=0.01))
        eye_y = 0.108
    elif race == "android":
        # Smooth synthetic head, no hair, a seam over the scalp, lit eyes.
        parts.append(box((0, 0.0, 1.775), (0.012, 0.22, 0.085), "trouser"))
        parts.append(box((0, 0.108, 1.66), (0.06, 0.012, 0.01), "accent"))
        eye_y = 0.110
    else:
        parts.append(box((0, -0.01, 1.765), (0.215, 0.235, 0.09), "hair", bevel=0.03))
        eye_y = 0.108
    parts.append(box((-0.04, eye_y, 1.695), (0.032, 0.012, 0.028), "eye"))
    parts.append(box((0.04, eye_y, 1.695), (0.032, 0.012, 0.028), "eye"))
    return join(parts, "head")

# ---- the body ---------------------------------------------------------------
BODY_JOINTS = [
    "pelvis", "chest", "neck",
    "shoulder.r", "elbow.r", "wrist.r", "hand.r",
    "shoulder.l", "elbow.l", "wrist.l", "hand.l",
    "hip.r", "knee.r", "ankle.r", "toe.r",
    "hip.l", "knee.l", "ankle.l", "toe.l",
]
_ix = {n: i for i, n in enumerate(BODY_JOINTS)}
BODY_EDGES = [
    (_ix["pelvis"], _ix["chest"]), (_ix["chest"], _ix["neck"]),
    (_ix["chest"], _ix["shoulder.r"]), (_ix["shoulder.r"], _ix["elbow.r"]),
    (_ix["elbow.r"], _ix["wrist.r"]), (_ix["wrist.r"], _ix["hand.r"]),
    (_ix["chest"], _ix["shoulder.l"]), (_ix["shoulder.l"], _ix["elbow.l"]),
    (_ix["elbow.l"], _ix["wrist.l"]), (_ix["wrist.l"], _ix["hand.l"]),
    (_ix["pelvis"], _ix["hip.r"]), (_ix["hip.r"], _ix["knee.r"]),
    (_ix["knee.r"], _ix["ankle.r"]), (_ix["ankle.r"], _ix["toe.r"]),
    (_ix["pelvis"], _ix["hip.l"]), (_ix["hip.l"], _ix["knee.l"]),
    (_ix["knee.l"], _ix["ankle.l"]), (_ix["ankle.l"], _ix["toe.l"]),
]


def body_skin(name="body", grow=1.0, pad=0.0):
    """The whole body as one skin-modifier surface. Armor calls this again
    with grow/pad > 1 and cuts its region out: same construction, same
    smoothness, just a size up."""
    return skin_mesh(name, BODY_JOINTS, BODY_EDGES, grow=grow, pad=pad)


def head_skin(name="head", grow=1.0, pad=0.0):
    return skin_mesh(name, ["neck", "skull", "crown"], [(0, 1), (1, 2)], grow=grow, pad=pad)


def split_arms(body):
    """Both arms and hands (body_part arm/forearm) off into their own mesh
    object `arms`, same vertices, same weights once bound. The first-person
    rig draws ONLY this mesh of a second body instance, and the local body
    turns it shadows-only, so the eye never sees two pairs of arms. The seam
    is the one `armor.suit.scout` already cuts along."""
    bpy.ops.object.select_all(action="DESELECT")
    body.select_set(True)
    bpy.context.view_layer.objects.active = body
    # Edit mode trusts VERTEX flags, and the skin build left every vertex
    # selected: clear all three levels, then pick by face and let edit mode
    # flush from faces. (Setting only polygon.select split off nearly the
    # whole torso.)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_mode(type="FACE")
    bpy.ops.mesh.select_all(action="DESELECT")
    bpy.ops.object.mode_set(mode="OBJECT")
    for p in body.data.polygons:
        p.select = body_part(p.center) in ("arm", "forearm")
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.separate(type="SELECTED")
    bpy.ops.object.mode_set(mode="OBJECT")
    arms = [o for o in bpy.context.selected_objects if o != body][0]
    arms.name = "arms"
    arms.data.name = "arms"
    return arms


def build(arms_split=True):
    body = body_skin()
    dress_body(body)
    stripe = box((0, 0.115, 1.36), (0.22, 0.02, 0.05), "accent", bevel=0.005)
    body = join([body, stripe], "body")
    arms = split_arms(body) if arms_split else None

    head = build_head(ACTIVE["race"])

    rig = armature()
    for m in (body, arms, head):
        if m is None:
            continue
        weight(m, BONES)
        bind(m, rig)

    # hand.r rides the right forearm, just past the wrist: where a weapon's
    # `grip` is lined up. Bone-parent space: origin at the bone's tail (the
    # wrist), y along the bone, so a little further along and forward.
    mount("hand.r", "forearm.r", rig, (0.0, 0.04, 0.03))
    clips(rig)
    return rig


def nearest_bone(p):
    return min(((seg_dist(p, joint(h), joint(t)), n) for n, h, t, _ in BONES))[1]


def body_part(c):
    """Which garment region a face belongs to: head, torso, arm, forearm,
    leg, shin. Nearest bone with height fences on top -- the body's faces
    are big, and a chest-top quad's centre is nearer the neck bone than the
    spine, a belly quad's nearer the root; raw nearest-bone left a V-shaped
    hole under the collar."""
    b = nearest_bone(c)
    if b == "head":
        return "head" if c.z >= 1.56 else "torso"
    if b == "root":
        return "torso" if c.z > 0.98 else "leg"
    if b == "torso":
        return "torso"
    return b.split(".")[0]


PAINT_OFFSET = 0.006   # enough to beat the depth buffer, too small to see


def shell(src, name, keep, mat, grow=1.0, pad=0.0, mode="paint"):
    """The faces of `src` (the base body, exact topology) that `keep(centre,
    part, material)` selects, coloured by `mat(centre, part)`. "paint" keeps
    the vertices where they are (3 mm along the normal); "rigid" pushes them
    away from the bone axes, radial per bone blended by inverse distance^4,
    plus `pad` along the normal."""
    me = src.data
    segs = [(joint(h), joint(t)) for _, h, t, _ in BONES]
    # Unnormalised sum of adjacent face normals per vertex: its length says
    # whether the faces around a vertex agree. In the armpit crease they face
    # opposite ways, the sum is ~0, and a push "along the normal" goes
    # nowhere -- the garment lands on the body and z-fights.
    facesum = [Vector((0, 0, 0)) for _ in me.vertices]
    adjacent = [[] for _ in me.vertices]
    for p in me.polygons:
        for vi in p.vertices:
            facesum[vi] += p.normal
            adjacent[vi].append(p.normal.copy())
    verts, coords, faces, mats, base_co = {}, [], [], [], []
    for p in me.polygons:
        c = p.center
        b = body_part(c)
        if not keep(c, b, MATERIAL_ORDER[p.material_index]):
            continue
        idx = []
        for vi in p.vertices:
            if vi not in verts:
                v = me.vertices[vi].co
                total = Vector((0, 0, 0))
                radial = Vector((0, 0, 0))
                wsum = 0.0
                for a, t in segs:
                    ab = t - a
                    u = max(0.0, min(1.0, (v - a).dot(ab) / ab.length_squared))
                    r = v - (a + ab * u)
                    d = max(r.length, 1e-3)
                    w = 1.0 / d ** 4
                    total += (r * (grow - 1.0) + r / d * pad) * w
                    radial += r / d * w
                    wsum += w
                fs = facesum[vi]
                n = fs.normalized() if fs.length > 0.5 else radial.normalized()
                if mode == "paint":
                    # Lift every adjacent face by at least PAINT_OFFSET: on a
                    # long flank triangle that spans the armpit crease, the
                    # crease vertex's push runs along the face and that corner
                    # touched the body -- the thin wedge seen from the side.
                    lift = min((n.dot(fn) for fn in adjacent[vi]), default=1.0)
                    k = 1.0 / max(lift, 0.25)      # never more than 4x
                    out = v + n * PAINT_OFFSET * k
                else:
                    out = v + total / wsum + n * max(pad, 0.02)
                verts[vi] = len(coords)
                base_co.append(tuple(v))
                coords.append(tuple(out))
            idx.append(verts[vi])
        faces.append(idx)
        mats.append(MATERIAL_ORDER.index(mat(c, b)))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(coords, [], faces)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    add_materials(obj)
    for p, m in zip(obj.data.polygons, mats):
        p.material_index = m
    SHELL_BASE[obj.name] = base_co
    return obj


def weight_rigid(obj, bone):
    """Every vertex 100% on one bone: a hard piece that never deforms."""
    g = obj.vertex_groups.new(name=bone)
    g.add([v.index for v in obj.data.vertices], 1.0, "REPLACE")


def build_armor(asset_id, spec):
    # The body this piece is cut from: built exactly as the player is (arms
    # left on it, so shell() cuts from one surface), then discarded -- only
    # the garment and the armature go out.
    rig = build(arms_split=False)
    parts = []
    if spec["src"]:
        parts.append(shell(bpy.data.objects[spec["src"]], asset_id, spec["keep"], spec["mat"],
                           spec.get("grow", 1.0), spec.get("pad", 0.0), spec["mode"]))
    for name, center, size, mat in spec.get("extras", []):
        parts.append(box(center, size, mat, bevel=0.02))
    for n in ("body", "head", "hand.r"):
        bpy.data.objects.remove(bpy.data.objects[n], do_unlink=True)
    for a in list(bpy.data.actions):
        bpy.data.actions.remove(a)
    base = SHELL_BASE.get(parts[0].name) if spec["src"] else None
    mesh = join(parts, "armor")
    if spec["mode"] == "rigid":
        weight_rigid(mesh, spec["bone"])
    else:
        if base:
            SHELL_BASE[mesh.name] = base   # join keeps the first part's vertices first
        weight(mesh, BONES)
    bind(mesh, rig)
    return rig


def export(out, animations=True):
    os.makedirs(os.path.dirname(out), exist_ok=True)
    bpy.ops.export_scene.gltf(
        filepath=out,
        export_format="GLB",
        export_apply=True,
        export_yup=True,
        export_animations=animations,
        export_animation_mode="ACTIONS",
        export_force_sampling=True,
        export_skins=True,
        export_def_bones=False,
        export_texcoords=False,
        export_normals=True,
        export_materials="EXPORT",
        export_image_format="NONE",
    )


def main():
    art = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    for asset_id, v in VARIANTS.items():
        bpy.ops.wm.read_factory_settings(use_empty=True)
        race = RACES[v.get("race", "human")]
        ACTIVE["race"] = v.get("race", "human")
        ACTIVE["palette"] = {**PALETTE, **race.get("palette", {}), **v.get("palette", {})}
        ACTIVE["bulk"] = v.get("bulk", race.get("bulk", 1.0))
        ACTIVE["shoulders"] = v.get("shoulders", race.get("shoulders", 1.0))
        build()
        out = os.path.join(art, "build", asset_id + ".raw.glb")
        export(out)
        print("wrote", out)
    for asset_id, spec in ():          # armor moved to tools/bpy/armor.py
        bpy.ops.wm.read_factory_settings(use_empty=True)
        ACTIVE.update(palette=dict(PALETTE), bulk=1.0, shoulders=1.0, race="human")
        build_armor(asset_id, spec)
        out = os.path.join(art, "build", asset_id + ".raw.glb")
        export(out, animations=False)
        print("wrote", out)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
