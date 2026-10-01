#!/usr/bin/env -S blender -b --python
"""Generate the player body -- build/char.player.raw.glb -- from MakeHuman.

    blender -b --python tools/bpy/human.py          (run from art/)

Needs the MPFB2 extension (2.0.17, extensions.blender.org, GPL tool; the
MakeHuman base mesh and targets it ships are CC0, so its OUTPUT is ours to
ship). Install once:  blender --command extension install-file -r user_default -e add-on-mpfb-v2.0.17.zip

What it builds, in order:
  1. a MakeHuman body from the MACRO sliders, with MPFB's 53-bone
     `game_engine` rig and its weights;
  2. turned to this project's frame (front +Y, right +X, Z up -- the glTF
     export makes that -Z forward) and scaled so the eyes are at EYE;
  3. helpers, tongue, teeth and lashes removed, eyeballs kept, decimated to
     BODY_TRIS, smooth-shaded;
  4. regions by dominant bone: `head` (skin, hair cap, eyes), `arms`
     (undersuit sleeves, bare hands), `body` (undersuit, boots) -- three
     meshes, as the client's first-person split needs (art/README.md);
  5. `hand.r` / `hand.l` empties riding the hand bones at the palm: the
     weapon's grip goes in the right, its barrel points at the left;
  6. clips: idle/walk/sprint (+_armed), die, and the fp_* set, posed by
     world-space swings and an analytic two-bone IK toward hand targets.
Materials carry real roughness/metallic (the recipe says `pbr`, so the
import keeps them instead of baking to vertex colour).

Armor for this body is tools/bpy/armor.py, built on the same rig.
"""
import math
import os
import sys

import bpy
import bmesh
from mathutils import Matrix, Quaternion, Vector

from bl_ext.user_default.mpfb.services.humanservice import HumanService
from bl_ext.user_default.mpfb.services.targetservice import TargetService

ART = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

EYE = 1.70          # GDD eye_height; the camera and the server's shot origin
BODY_TRIS = 6000    # body + arms + head; armor gets the rest of ~15k

# MakeHuman sliders: an adult, fit, slightly tall build.
MACRO = {
    "gender": 1.0, "age": 0.45, "muscle": 0.62, "weight": 0.5, "proportions": 0.7,
    "height": 0.6, "cupsize": 0.5, "firmness": 0.5,
    "race": {"asian": 0.33, "caucasian": 0.34, "african": 0.33},
}

# name: (base colour, roughness, metallic)
MATERIALS = {
    "suit":  ((0.20, 0.23, 0.27), 0.75, 0.0),   # undersuit, dark slate
    "skin":  ((0.80, 0.62, 0.50), 0.55, 0.0),
    "glove": ((0.16, 0.16, 0.18), 0.7, 0.0),    # the suit's own gloves (a bare-hand seam showed at the wrist)
    "hair":  ((0.10, 0.07, 0.05), 0.8, 0.0),
    "eye":   ((0.05, 0.05, 0.06), 0.15, 0.0),
    "boot":  ((0.07, 0.07, 0.08), 0.85, 0.0),
}

KEEP_GROUPS = ("body", "helper-l-eye", "helper-r-eye")


# ---- materials ----------------------------------------------------------------
def material(name):
    m = bpy.data.materials.get(name)
    if m is None:
        col, rough, metal = MATERIALS[name]
        m = bpy.data.materials.new(name)
        m.use_nodes = True
        b = m.node_tree.nodes["Principled BSDF"]
        lin = tuple(c ** 2.2 for c in col)      # the palette is written as display (sRGB) colour; glTF stores linear
        b.inputs["Base Color"].default_value = (*lin, 1.0)
        b.inputs["Roughness"].default_value = rough
        b.inputs["Metallic"].default_value = metal
        m.diffuse_color = (*lin, 1.0)
    return m


# ---- the human ----------------------------------------------------------------
def make_human(decimate=True):
    h = HumanService.create_human(scale=0.1, macro_detail_dict=MACRO, feet_on_ground=True)
    rig = HumanService.add_builtin_rig(h, "game_engine")
    TargetService.bake_targets(h)

    # Drop every modifier (mask, armature); the armature goes back on later.
    for m in list(h.modifiers):
        h.modifiers.remove(m)

    # Keep the body and the eyeballs; everything else is helper geometry.
    keep_idx = {h.vertex_groups[g].index for g in KEEP_GROUPS}
    bm = bmesh.new()
    bm.from_mesh(h.data)
    deform = bm.verts.layers.deform.active
    doomed = [v for v in bm.verts if not any(gi in keep_idx and w > 0.5 for gi, w in v[deform].items())]
    bmesh.ops.delete(bm, geom=doomed, context="VERTS")
    bm.to_mesh(h.data)
    bm.free()

    # Eye height in MakeHuman's own frame, before the turn.
    eye_z = vertex_group_centroid(h, "helper-l-eye").z

    # Front -Y -> +Y (a half turn about Z) and eyes to EYE: same matrix on the
    # mesh data and on the armature's bones, so weights stay valid.
    s = EYE / eye_z
    M = Matrix.Diagonal((s, s, s, 1.0)) @ Matrix.Rotation(math.pi, 4, "Z")
    h.data.transform(M)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    rig.data.transform(M)
    bpy.ops.object.mode_set(mode="OBJECT")
    for o in (h, rig):
        o.matrix_world = Matrix.Identity(4)
    h.parent = rig
    h.matrix_parent_inverse = Matrix.Identity(4)

    # The eyes, in the final frame, before their helper groups go.
    eyes = (vertex_group_centroid(h, "helper-l-eye"), vertex_group_centroid(h, "helper-r-eye"))

    # Only bone groups survive (MPFB adds helper/joint groups too).
    bones = {b.name for b in rig.data.bones}
    for g in list(h.vertex_groups):
        if g.name not in bones:
            h.vertex_groups.remove(g)

    if not decimate:                     # armor.py cuts panels from the clean quads
        h.name = "human"
        return h, rig, eyes
    # Decimate to budget; vertex groups are interpolated through it.
    tris = sum(len(p.vertices) - 2 for p in h.data.polygons)
    dec = h.modifiers.new("decimate", "DECIMATE")
    dec.ratio = min(1.0, BODY_TRIS / tris)
    bpy.context.view_layer.objects.active = h
    bpy.ops.object.modifier_apply(modifier=dec.name)
    h.name = "human"
    return h, rig, eyes


def vertex_group_centroid(obj, name):
    gi = obj.vertex_groups[name].index
    pts = [v.co for v in obj.data.vertices if any(g.group == gi and g.weight > 0.5 for g in v.groups)]
    return sum(pts, Vector()) / len(pts)


def dominant_bones(obj):
    """Per vertex, the bone with the largest weight."""
    names = {g.index: g.name for g in obj.vertex_groups}
    out = []
    for v in obj.data.vertices:
        best = max(v.groups, key=lambda g: g.weight, default=None)
        out.append(names.get(best.group) if best else None)
    return out


ARM_BONES = ("upperarm", "lowerarm", "hand", "thumb", "index", "middle", "ring", "pinky")


def part_of(bone):
    if bone is None:
        return "body"
    if bone in ("head", "neck_01"):
        return "head"
    if bone.startswith(ARM_BONES):
        return "arms"
    return "body"


def dress(h, eye_l, eye_r):
    """Material per face from its dominant bone and where it is. The
    undersuit runs from the jaw line down; armor adds the detail, so there
    are no painted seams (bands on decimated triangles read as tears)."""
    for n in MATERIALS:
        h.data.materials.append(material(n))
    idx = {n: i for i, n in enumerate(MATERIALS)}
    dom = dominant_bones(h)
    head_pts = [v.co for v, b in zip(h.data.vertices, dom) if b == "head"]
    hc = sum(head_pts, Vector()) / len(head_pts)       # skull centre: hair is measured from here
    for p in h.data.polygons:
        c = p.center
        bones = [dom[v] for v in p.vertices]
        b = max(set(bones), key=bones.count)
        r = c - hc
        if (c - eye_l).length < 0.016 or (c - eye_r).length < 0.016:
            m = "eye"
        elif b == "head":
            # Short hair: the crown, and the back of the skull above the nape.
            m = "hair" if (r.z > 0.045 and r.y < 0.07) or (r.z > -0.03 and r.y < -0.03) else "skin"
        elif b and b.startswith(("hand", "thumb", "index", "middle", "ring", "pinky")):
            m = "glove"
        elif (b and b.startswith(("foot", "ball"))) or c.z < 0.13:
            m = "boot"
        else:
            m = "suit"
        p.material_index = idx[m]


def split(h):
    """`head` and `arms` off into their own meshes; the rest is `body`."""
    dom = dominant_bones(h)
    parts = {}
    for name in ("head", "arms"):
        bpy.ops.object.select_all(action="DESELECT")
        h.select_set(True)
        bpy.context.view_layer.objects.active = h
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_mode(type="FACE")
        bpy.ops.mesh.select_all(action="DESELECT")
        bpy.ops.object.mode_set(mode="OBJECT")
        dom = dominant_bones(h)
        for p in h.data.polygons:
            bones = [dom[v] for v in p.vertices]
            p.select = part_of(max(set(bones), key=bones.count)) == name
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.separate(type="SELECTED")
        bpy.ops.object.mode_set(mode="OBJECT")
        new = [o for o in bpy.context.selected_objects if o != h][0]
        new.name = name
        new.data.name = name
        parts[name] = new
    h.name = "body"
    h.data.name = "body"
    parts["body"] = h
    for o in parts.values():
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.shade_smooth()
    return parts


def bind(obj, rig):
    obj.parent = rig
    obj.matrix_parent_inverse = Matrix.Identity(4)
    mod = obj.modifiers.new("rig", "ARMATURE")
    mod.object = rig


def mount(name, rig, bone, at):
    """An empty on a bone at a world position (the palm)."""
    e = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(e)
    e.parent = rig
    e.parent_type = "BONE"
    e.parent_bone = bone
    bpy.context.view_layer.update()
    e.matrix_world = Matrix.Translation(at)
    return e


def palm(parts, side):
    """Centre of the vertices weighted to hand_<side> (and its fingers' roots)."""
    arms = parts["arms"]
    gi = arms.vertex_groups["hand_" + side].index
    pts = [arms.matrix_world @ v.co for v in arms.data.vertices
           if any(g.group == gi and g.weight > 0.5 for g in v.groups)]
    return sum(pts, Vector()) / len(pts)


# ---- posing ---------------------------------------------------------------------
def pb(rig, name):
    return rig.pose.bones[name]


def update():
    bpy.context.view_layer.update()


def rotate_about_head(rig, name, q):
    p = pb(rig, name)
    update()
    m = p.matrix.copy()
    h = m.translation.copy()
    p.matrix = Matrix.Translation(h) @ q.to_matrix().to_4x4() @ Matrix.Translation(-h) @ m
    update()


def swing(rig, name, axis, degrees):
    """Rotate a bone about a WORLD axis through its head (children follow).
    Front is +Y, so about +X a positive angle swings a down-pointing bone
    forward."""
    rotate_about_head(rig, name, Quaternion(Vector(axis).normalized(), math.radians(degrees)))


def aim(rig, name, direction):
    """Point a bone (head -> tail) along a world direction, minimal turn."""
    p = pb(rig, name)
    update()
    cur = (p.matrix.to_3x3() @ Vector((0, 1, 0))).normalized()
    rotate_about_head(rig, name, cur.rotation_difference(Vector(direction).normalized()))


def head_of(rig, name):
    update()
    return pb(rig, name).matrix.translation.copy()


def reach(rig, side, target, pole, hand_dir=None):
    """Two-bone IK: upperarm + lowerarm so the wrist lands on `target`
    (clamped to reach), the elbow toward `pole`, the hand along hand_dir."""
    up, lo, ha = "upperarm_" + side, "lowerarm_" + side, "hand_" + side
    S = head_of(rig, up)
    l1 = pb(rig, up).bone.length
    l2 = pb(rig, lo).bone.length
    d = Vector(target) - S
    dist = max(1e-3, min(d.length, (l1 + l2) * 0.999))
    dn = d.normalized()
    cos_a = max(-1.0, min(1.0, (l1 * l1 + dist * dist - l2 * l2) / (2 * l1 * dist)))
    a = math.acos(cos_a)
    pv = Vector(pole)
    pn = (pv - dn * pv.dot(dn))
    pn = pn.normalized() if pn.length > 1e-6 else Vector((0, 0, -1))
    elbow = S + dn * math.cos(a) * l1 + pn * math.sin(a) * l1
    aim(rig, up, elbow - S)
    aim(rig, lo, (S + dn * dist) - head_of(rig, lo))
    if hand_dir is not None:
        aim(rig, ha, hand_dir)


def arms_down(rig):
    """From MakeHuman's A-pose to arms hanging at the sides, fingers relaxed."""
    for side, sx in (("r", 1), ("l", -1)):
        aim(rig, "upperarm_" + side, (0.18 * sx, 0.02, -1))
        aim(rig, "lowerarm_" + side, (0.08 * sx, 0.12, -1))
        aim(rig, "hand_" + side, (0.05 * sx, 0.10, -1))
        curl(rig, side, CURL * 0.35)


FINGERS = ("index", "middle", "ring", "pinky")
CURL = 35   # degrees per finger segment for a grip (positive closes toward the palm, +Z)


def flex(rig, name, palm, degrees):
    """Bend one finger segment toward the palm: about its own hinge, the axis
    perpendicular to the segment and the palm normal. (One shared axis -- the
    hand bone's X, ~40 degrees off the knuckle row -- fanned the fingers
    sideways instead of closing them.)"""
    update()
    d = (pb(rig, name).matrix.to_3x3() @ Vector((0, 1, 0))).normalized()
    axis = d.cross(palm)
    if axis.length > 1e-6:
        swing(rig, name, axis, degrees)


def curl(rig, side, degrees, thumb=None):
    """Close the fingers, knuckle first, each segment about its own hinge."""
    curl_each(rig, side, (degrees,) * 4, thumb if thumb is not None else 0)


def hand_frame(rig, side):
    """The hand's knuckle direction (wrist -> middle-finger base) and palm
    normal, world. The palm is the hand bone's +Z: checked by handedness (a
    right hand, fingers forward, +Z down, has its thumb toward the body).
    Taking -Z mirrored every grip: pinky over index on the pistol grip, the
    fingers closing backwards."""
    update()
    h = pb(rig, "hand_" + side)
    k = (pb(rig, "middle_01_" + side).matrix.translation - h.matrix.translation).normalized()
    palm = (h.matrix.to_3x3() @ Vector((0, 0, 1))).normalized()
    return k, palm


def frame(fwd, up):
    f = Vector(fwd).normalized()
    u = (Vector(up) - f * Vector(up).dot(f)).normalized()
    return Matrix((f, u, f.cross(u))).transposed()


def orient_hand(rig, side, fwd, palm):
    """Turn the hand so its knuckles point along `fwd` and its palm faces
    `palm` (both world), about the wrist."""
    k, p = hand_frame(rig, side)
    rotate_about_head(rig, "hand_" + side, (frame(fwd, palm) @ frame(k, p).inverted()).to_quaternion())


def rest_fingers(rig, side):
    for f in FINGERS + ("thumb",):
        for seg in ("01", "02", "03"):
            n = f"{f}_{seg}_{side}"
            if n in rig.pose.bones:
                pb(rig, n).matrix_basis = Matrix.Identity(4)
    update()


def curl_each(rig, side, degrees, thumb):
    """curl() with a per-finger amount (index first): a trigger finger lies
    straighter than the three wrapped round the grip."""
    update()
    palm = (pb(rig, "hand_" + side).matrix.to_3x3() @ Vector((0, 0, 1))).normalized()
    for f, deg_ in zip(FINGERS, degrees):
        for seg in ("01", "02", "03"):
            flex(rig, f"{f}_{seg}_{side}", palm, deg_)
    for seg in ("01", "02", "03"):
        flex(rig, f"thumb_{seg}_{side}", palm, thumb * (0.6 if seg == "01" else 1.0))


def mount_at(rig, side):
    update()
    return bpy.data.objects["hand." + side].matrix_world.translation.copy()


def grip(rig, side, want, fwd, palm, pole, fingers, thumb):
    """Put the hand's MOUNT (palm/fist centre) on `want`: solve the arm for a
    wrist target, orient and close the hand, measure where the mount landed,
    move the wrist target by the miss, repeat."""
    target = Vector(want) - Vector(fwd).normalized() * 0.07
    for _ in range(5):
        rest_fingers(rig, side)
        reach(rig, side, target, pole)
        orient_hand(rig, side, fwd, palm)
        curl_each(rig, side, fingers, thumb)
        miss = Vector(want) - mount_at(rig, side)
        if miss.length < 0.002:
            break
        target += miss


def lay_thumb(rig, side, direction):
    """Point the thumb's last two segments along a world direction (a slight
    bend kept at the tip)."""
    d = Vector(direction).normalized()
    aim(rig, "thumb_02_" + side, d)
    aim(rig, "thumb_03_" + side, d)
    palm = (pb(rig, "hand_" + side).matrix.to_3x3() @ Vector((0, 0, 1))).normalized()
    flex(rig, "thumb_03_" + side, palm, 15)


# The rifle's contact points, in its own frame (tools/gen_weapon.py): `fore`
# sits this far from `grip`, along the barrel and up.
FORE_ALONG = 0.295
FORE_UP = 0.055
BLADE = 25          # degrees the chest turns for a two-handed hold


def rifle(rig, G, forward, pole_r=(0.6, -0.3, -0.8), pole_l=(-0.7, 0.2, -0.7)):
    """Both hands ON the gun: the right fist round the pistol grip at G
    (index along the trigger), the left palm up under the fore-end. The
    client puts the gun's `grip` on hand.r and its `fore` on hand.l, so this
    is the whole hold."""
    # Bladed stance: the chest turns right so the left shoulder comes forward
    # (an arm alone cannot reach a fore-end held out in front), the neck and
    # head turn back so the face stays on the target.
    swing(rig, "spine_02", (0, 0, 1), -BLADE * 0.5)
    swing(rig, "spine_03", (0, 0, 1), -BLADE * 0.5)
    swing(rig, "neck_01", (0, 0, 1), BLADE * 0.6)
    swing(rig, "head", (0, 0, 1), BLADE * 0.4)
    f = Vector(forward).normalized()
    up = Vector((0, 0, 1))
    up = (up - f * up.dot(f)).normalized()
    right = f.cross(up)
    F = Vector(G) + f * FORE_ALONG + up * FORE_UP
    # Right: knuckles forward and down the raked grip, palm in toward the gun.
    # The pistol grip runs (nearly) vertically through the fist: knuckles
    # point forward, a little down, so the hand's width lines up with it.
    grip(rig, "r", G, (f - up * 0.25).normalized(), -right, pole_r,
         fingers=(15, 80, 85, 85), thumb=70)
    # Left: fingers forward and a little across, palm up under the fore-end.
    grip(rig, "l", F, (f + right * 0.35).normalized(), up, pole_l,
         fingers=(40, 45, 45, 45), thumb=25)
    # Thumbs lie ALONG the gun, not up its side (seen end-on, an upright
    # thumb read far too long): the support thumb runs forward beside the
    # fore-end, the trigger-hand thumb wraps across the left of the grip.
    lay_thumb(rig, "l", (f + up * 0.10 - right * 0.15).normalized())
    lay_thumb(rig, "r", (-right * 0.75 + f * 0.45 - up * 0.25).normalized())


# Grip points (where the right fist closes), body frame (+Y front, Z up,
# eye at 1.70). Shoulders sit at (+-0.20, 0.02, 1.45) and the wrist reaches
# 0.54 m; the fore-end lands 0.30 m down the barrel, so the left hand has to
# be able to get there or the IK clamps it short.
AIM3P = dict(G=(0.17, 0.24, 1.12), forward=(-0.35, 0.92, 0.18))    # third person, low ready
FP_HOLD = dict(G=(0.14, 0.30, 1.26), forward=(-0.03, 1.0, 0.03))    # first person hold: straight down the view (the client converges it on the crosshair)
FP_ADS = dict(G=(0.03, 0.30, 1.38), forward=(0.0, 1.0, 0.02))       # sights; the client lifts it to the eye
FP_LOWER = dict(G=(0.12, 0.22, 1.10), forward=(-0.25, 0.60, -0.75))


def pose_rest(rig):
    for p in rig.pose.bones:
        p.rotation_mode = "QUATERNION"
        p.matrix_basis = Matrix.Identity(4)
    update()


def key(rig, frame):
    for p in rig.pose.bones:
        p.keyframe_insert("rotation_quaternion", frame=frame)
        p.keyframe_insert("location", frame=frame)


def clip(rig, name, seconds, pose_at, keys, loop=True):
    """pose_at(t) poses the rig from rest for t in [0, 1]; keyed at `keys`."""
    fps = bpy.context.scene.render.fps
    frames = max(1, round(seconds * fps))
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    rig.animation_data_create()
    rig.animation_data.action = act
    for t in keys:
        pose_rest(rig)
        pose_at(t)
        key(rig, 1 + t * frames)
    if loop:
        pose_rest(rig)
        pose_at(0.0)
        key(rig, 1 + frames)
    bpy.context.scene.frame_end = max(bpy.context.scene.frame_end, 1 + frames)
    pose_rest(rig)


def gait(rig, t, leg, knee, arm, lean, armed=None):
    c = math.cos(2 * math.pi * t)
    swing(rig, "spine_01", (1, 0, 0), lean)
    swing(rig, "thigh_r", (1, 0, 0), leg * c)
    swing(rig, "thigh_l", (1, 0, 0), -leg * c)
    # A knee bends while its leg comes through (behind -> forward).
    bend_r = max(0.0, math.sin(2 * math.pi * t))
    bend_l = max(0.0, -math.sin(2 * math.pi * t))
    swing(rig, "calf_r", (1, 0, 0), -knee * bend_r)
    swing(rig, "calf_l", (1, 0, 0), -knee * bend_l)
    if armed:
        rifle(rig, **armed)
    else:
        arms_down(rig)
        swing(rig, "upperarm_r", (1, 0, 0), -arm * c)
        swing(rig, "upperarm_l", (1, 0, 0), arm * c)


def clips(rig):
    bpy.context.scene.render.fps = 30
    Q = (0.0, 0.25, 0.5, 0.75)

    def breathe(t):
        swing(rig, "spine_03", (1, 0, 0), 1.2 * math.sin(2 * math.pi * t))

    clip(rig, "idle", 3.0, lambda t: (arms_down(rig), breathe(t)), Q)
    clip(rig, "walk", 0.70, lambda t: gait(rig, t, 26, 40, 18, 3), Q)
    clip(rig, "sprint", 0.50, lambda t: gait(rig, t, 42, 65, 35, 10), Q)
    clip(rig, "idle_armed", 3.0, lambda t: (breathe(t), rifle(rig, **AIM3P)), Q)
    clip(rig, "walk_armed", 0.70, lambda t: gait(rig, t, 26, 40, 0, 3, armed=AIM3P), Q)
    clip(rig, "sprint_armed", 0.50, lambda t: gait(rig, t, 42, 65, 0, 10, armed=AIM3P), Q)

    def die(t):
        swing(rig, "Root", (1, 0, 0), 88 * min(1.0, t * 1.2) ** 1.5)
        swing(rig, "head", (1, 0, 0), -15 * t)
        arms_down(rig)
        swing(rig, "upperarm_r", (0, 1, 0), 50 * t)
        swing(rig, "upperarm_l", (0, 1, 0), -50 * t)
    clip(rig, "die", 0.5, die, (0.0, 0.3, 0.6, 1.0), loop=False)

    # First person: only the arms are ever seen, but every bone is keyed so a
    # clip fully overrides the one before it.
    def hold(t, bob=0.0, amp=0.0):
        G = Vector(FP_HOLD["G"]) + Vector((amp * math.sin(2 * math.pi * t), 0, bob * math.sin(4 * math.pi * t)))
        rifle(rig, G, FP_HOLD["forward"])
    clip(rig, "fp_idle", 3.0, lambda t: hold(t, bob=0.004), Q)
    clip(rig, "fp_walk", 0.70, lambda t: hold(t, bob=0.008, amp=0.010), Q)
    clip(rig, "fp_sprint", 0.50, lambda t: hold(t, bob=0.014, amp=0.020), Q)
    clip(rig, "fp_ads", 1.0, lambda t: rifle(rig, **FP_ADS), (0.0,))
    clip(rig, "fp_lower", 1.0, lambda t: rifle(rig, **FP_LOWER), (0.0,))

    def unarmed(t):
        # Loose fists low in the view, knuckles forward, thumbs up and in.
        b = 0.006 * math.sin(2 * math.pi * t)
        reach(rig, "r", (0.20, 0.30, 1.10 + b), (0.6, -0.2, -0.8), hand_dir=(-0.15, 1, -0.35))
        reach(rig, "l", (-0.20, 0.30, 1.10 - b), (-0.6, -0.2, -0.8), hand_dir=(0.15, 1, -0.35))
        curl(rig, "r", CURL * 1.2, thumb=CURL * 0.4)
        curl(rig, "l", CURL * 1.2, thumb=CURL * 0.4)
    clip(rig, "fp_unarmed", 3.0, unarmed, Q)

    def fire(t):
        k = math.sin(math.pi * min(1.0, t * 1.6))       # back and up, then settle
        G = Vector(FP_HOLD["G"]) + Vector((0, -0.03 * k, 0.012 * k))
        rifle(rig, G, Vector(FP_HOLD["forward"]) + Vector((0, 0, 0.06 * k)))
    clip(rig, "fp_fire", 0.12, fire, (0.0, 0.3, 0.6, 1.0), loop=False)

    def reload(t):
        rifle(rig, **FP_HOLD)
        # The left hand drops to the magazine (under the receiver, ~12 cm
        # ahead of the grip), works it, and comes back to the fore-end.
        k = math.sin(math.pi * min(1.0, max(0.0, (t - 0.1) / 0.8)))
        if k > 0:
            f = Vector(FP_HOLD["forward"]).normalized()
            up = (Vector((0, 0, 1)) - f * f.z).normalized()
            G = Vector(FP_HOLD["G"])
            fore = G + f * FORE_ALONG + up * FORE_UP
            mag = G + f * 0.12 - up * 0.04
            grip(rig, "l", fore.lerp(mag, k), (f - up * 0.6).normalized(), (f.cross(up)).normalized() * -1,
                 (-0.7, 0.2, -0.7), fingers=(45, 50, 50, 50), thumb=30)
    clip(rig, "fp_reload", 2.0, reload, (0.0, 0.1, 0.3, 0.5, 0.7, 0.9, 1.0), loop=False)
    rig.animation_data.action = None


# ---- build ------------------------------------------------------------------------
def build():
    h, rig, (eye_l, eye_r) = make_human()
    dress(h, eye_l, eye_r)
    parts = split(h)
    for o in parts.values():
        bind(o, rig)
    # Mounts: the right at the centre of a closed fist (a grip sits in the
    # palm, the fingers round it), the left where a fore-end rests on the
    # palm. Both are palm centre pushed out along the palm normal.
    for side in ("r", "l"):
        k, palm_n = hand_frame(rig, side)
        mount("hand." + side, rig, "hand_" + side, palm(parts, side) + palm_n * (0.025 if side == "r" else 0.03))
    clips(rig)
    return rig, parts


def vertex_group_centroid_world(obj, name):
    return obj.matrix_world @ vertex_group_centroid(obj, name)


def export(out):
    os.makedirs(os.path.dirname(out), exist_ok=True)
    bpy.ops.export_scene.gltf(
        filepath=out, export_format="GLB", export_apply=True, export_yup=True,
        export_animations=True, export_animation_mode="ACTIONS", export_force_sampling=True,
        export_skins=True, export_def_bones=False, export_texcoords=False,
        export_normals=True, export_materials="EXPORT", export_image_format="NONE",
    )


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    rig, parts = build()
    tris = {n: sum(len(p.vertices) - 2 for p in o.data.polygons) for n, o in parts.items()}
    print("parts", tris, "bones", len(rig.data.bones))
    out = os.path.join(ART, "build", "char.player.raw.glb")
    export(out)
    print("wrote", out)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
