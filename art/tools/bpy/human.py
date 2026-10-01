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
BODY_TRIS = 5600    # body + arms + head; armor gets the rest of ~15k

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
    "ivory": ((0.86, 0.82, 0.70), 0.5, 0.0),    # orc tusks
}

KEEP_GROUPS = ("body", "helper-l-eye", "helper-r-eye")

# Every humanoid comes from this file. A variant changes the MakeHuman
# sliders, the eye height (overall size), material colours, whether the head
# has hair, flat (machine) shading, and adds rigid head parts. Bones, clips,
# hand mounts and contact-point grips are shared, so every body holds a gun
# and wears armor the same way. Armor is authored on char.player's build;
# variants that keep that build (gunner) can wear it, bigger ones (orc) not.
VARIANTS = {
    "char.player": {},
    "npc.shopkeeper": {                        # Quartermaster Vex: older, heavier, khaki
        "macro": {"age": 0.75, "weight": 0.72, "muscle": 0.45, "height": 0.45},
        "materials": {"suit": (0.46, 0.41, 0.30), "hair": (0.55, 0.53, 0.50), "skin": (0.72, 0.55, 0.45)},
    },
    "npc.dispatcher": {                        # Dispatcher Oru: an android
        "macro": {"gender": 0.35, "weight": 0.35, "muscle": 0.45, "proportions": 0.9},
        "materials": {"skin": (0.86, 0.88, 0.92), "suit": (0.88, 0.89, 0.91), "glove": (0.70, 0.72, 0.76),
                      "eye": (0.25, 0.90, 1.00), "boot": (0.30, 0.32, 0.36)},
        "hair": False,
        "head_parts": "android",
    },
    "npc.grunt": {                             # melee raider: an orc, big and heavy
        "macro": {"muscle": 1.0, "weight": 0.78, "height": 1.0, "proportions": 0.4, "age": 0.4},
        "eye": 1.82,
        "materials": {"skin": (0.40, 0.55, 0.32), "suit": (0.45, 0.20, 0.14), "hair": (0.08, 0.07, 0.06),
                      "eye": (0.70, 0.12, 0.08)},
        "hair": "topknot",
        "head_parts": "orc",
    },
    "npc.gunner": {                            # ranged raider: a combat robot (player's build, so armor fits)
        "materials": {"skin": (0.48, 0.50, 0.54), "suit": (0.30, 0.32, 0.35), "glove": (0.40, 0.42, 0.45),
                      "boot": (0.22, 0.23, 0.25), "eye": (1.00, 0.45, 0.10)},
        "metallic": {"skin": 0.85, "suit": 0.6, "glove": 0.8},
        "hair": False,
        "flat": True,
        "head_parts": "robot",
    },
}
ACTIVE = {}


# ---- materials ----------------------------------------------------------------
def material(name):
    m = bpy.data.materials.get(name)
    if m is None:
        col, rough, metal = MATERIALS[name]
        col = ACTIVE.get("materials", {}).get(name, col)
        metal = ACTIVE.get("metallic", {}).get(name, metal)
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
    macro = dict(MACRO)
    macro.update(ACTIVE.get("macro", {}))
    h = HumanService.create_human(scale=0.1, macro_detail_dict=macro, feet_on_ground=True)
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
    s = ACTIVE.get("eye", EYE) / eye_z
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
            hair = ACTIVE.get("hair", True)
            if hair is True:
                m = "hair" if (r.z > 0.045 and r.y < 0.07) or (r.z > -0.03 and r.y < -0.03) else "skin"
            elif hair == "topknot":
                m = "hair" if (r.z > 0.07 and abs(r.x) < 0.03 and -0.06 < r.y < 0.02) else "skin"
            else:
                m = "skin"
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


# The pistol: its `fore` (tools/gen_weapon.py) is where the support palm cups
# the firing fist, just under and in front of the grip.
PISTOL_FORE = (-0.02, -0.045)          # (along the barrel, up) from grip


def pistol(rig, G, forward, pole_r=(0.6, -0.4, -0.7), pole_l=(-0.6, -0.4, -0.7)):
    """Two hands on a pistol, square stance: the right fist round the grip,
    the left hand wrapped over the right fingers from below and the left,
    palm toward the gun, thumbs forward along the frame."""
    f = Vector(forward).normalized()
    up = Vector((0, 0, 1))
    up = (up - f * up.dot(f)).normalized()
    right = f.cross(up)
    F = Vector(G) + f * PISTOL_FORE[0] + up * PISTOL_FORE[1]
    grip(rig, "r", G, (f - up * 0.25).normalized(), -right, pole_r,
         fingers=(15, 80, 85, 85), thumb=40)
    grip(rig, "l", F, (f - up * 0.35 + right * 0.25).normalized(), (right * 0.85 + up * 0.5).normalized(), pole_l,
         fingers=(70, 75, 75, 75), thumb=25)
    lay_thumb(rig, "r", (f - right * 0.35).normalized())
    lay_thumb(rig, "l", (f - right * 0.15 + up * 0.05).normalized())


P_AIM3P = dict(G=(0.08, 0.34, 1.20), forward=(-0.10, 1.0, -0.20))   # third person: pistol at the ready
P_FP_HOLD = dict(G=(0.13, 0.36, 1.30), forward=(-0.02, 1.0, 0.03))   # elbows bent: forearms clear of the lens   # arms near straight: forearms clear of the eye
P_FP_ADS = dict(G=(0.00, 0.47, 1.55), forward=(0.0, 1.0, 0.02))     # client puts the rear sight 0.42 m out
P_FP_LOWER = dict(G=(0.08, 0.26, 1.10), forward=(-0.20, 0.60, -0.75))

# Weapon classes: a clip-name suffix, the hold, its targets and where its
# magazine is (reload). The client picks the suffix from the weapon's def.
CLASSES = {
    "": dict(hold=rifle, aim3p=AIM3P, fp=FP_HOLD, ads=FP_ADS, lower=FP_LOWER,
             fore=(FORE_ALONG, FORE_UP), mag=(0.12, -0.04)),
    "_pistol": dict(hold=pistol, aim3p=P_AIM3P, fp=P_FP_HOLD, ads=P_FP_ADS, lower=P_FP_LOWER,
                    fore=PISTOL_FORE, mag=(0.0, -0.09)),
}


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
        armed[0](rig, **armed[1])
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
    for sfx, C in CLASSES.items():
        hold3 = (C["hold"], C["aim3p"])
        clip(rig, "idle_armed" + sfx, 3.0, lambda t, h=hold3: (breathe(t), h[0](rig, **h[1])), Q)
        clip(rig, "walk_armed" + sfx, 0.70, lambda t, h=hold3: gait(rig, t, 26, 40, 0, 3, armed=h), Q)
        clip(rig, "sprint_armed" + sfx, 0.50, lambda t, h=hold3: gait(rig, t, 42, 65, 0, 10, armed=h), Q)

    def ease(a, b, t):
        x = max(0.0, min(1.0, (t - a) / (b - a)))
        return x * x * (3 - 2 * x)

    def drop(dz):
        """Lower the whole body (Root) by dz metres."""
        p = rig.pose.bones["Root"]
        update()
        p.matrix = Matrix.Translation((0, 0, -dz)) @ p.matrix
        update()

    # Hit: a short flinch -- the chest snaps back and twists, the head
    # follows late. Unarmed and holding a rifle (arms stay on the gun).
    def flinch(t, armed, C=CLASSES[""]):
        k = math.sin(math.pi * min(1.0, t * 1.4)) * (1.0 - 0.4 * t)
        if armed:
            C["hold"](rig, **C["aim3p"])
        else:
            arms_down(rig)
        swing(rig, "spine_02", (1, 0, 0), 16 * k)
        swing(rig, "spine_03", (0, 0, 1), 11 * k)
        swing(rig, "head", (1, 0, 0), 20 * ease(0.1, 0.5, t) * (1 - ease(0.6, 1.0, t)))
        swing(rig, "calf_r", (1, 0, 0), -10 * k)
        swing(rig, "calf_l", (1, 0, 0), -10 * k)
    clip(rig, "hit", 0.35, lambda t: flinch(t, False), (0.0, 0.25, 0.5, 0.75, 1.0), loop=False)
    for sfx, C in CLASSES.items():
        clip(rig, "hit_armed" + sfx, 0.35, lambda t, C=C: flinch(t, True, C), (0.0, 0.25, 0.5, 0.75, 1.0), loop=False)

    # Death: the knees go, the body drops onto them, then topples back and
    # to one side, arms falling loose -- not a stiff plank pivoting at the feet.
    def die(t):
        k1 = ease(0.0, 0.38, t)                 # knees buckle
        k2 = ease(0.30, 1.0, t)                 # topple
        arms_down(rig)
        swing(rig, "thigh_r", (1, 0, 0), 55 * k1)
        swing(rig, "thigh_l", (1, 0, 0), 48 * k1)
        swing(rig, "calf_r", (1, 0, 0), -95 * k1)
        swing(rig, "calf_l", (1, 0, 0), -85 * k1)
        swing(rig, "spine_01", (1, 0, 0), -18 * k1 + 10 * k2)
        swing(rig, "head", (1, 0, 0), -20 * k1 + 25 * k2)
        swing(rig, "upperarm_r", (0, 1, 0), 55 * k2)
        swing(rig, "upperarm_l", (0, 1, 0), -40 * k2)
        drop(0.42 * k1 * (1.0 - k2))            # down onto the knees, back up as it tips over
        swing(rig, "Root", (1, 0, 0), 78 * k2)       # positive tips the body backward
        swing(rig, "Root", (0, 1, 0), 18 * k2)
    clip(rig, "die", 1.1, die, (0.0, 0.15, 0.3, 0.45, 0.6, 0.8, 1.0), loop=False)

    # First person: only the arms are ever seen, but every bone is keyed so a
    # clip fully overrides the one before it. One set per weapon class.
    for sfx, C in CLASSES.items():
        hold, fp = C["hold"], C["fp"]

        def held(t, bob=0.0, amp=0.0, hold=hold, fp=fp):
            G = Vector(fp["G"]) + Vector((amp * math.sin(2 * math.pi * t), 0, bob * math.sin(4 * math.pi * t)))
            hold(rig, G, fp["forward"])
        clip(rig, "fp_idle" + sfx, 3.0, lambda t, h=held: h(t, bob=0.004), Q)
        clip(rig, "fp_walk" + sfx, 0.70, lambda t, h=held: h(t, bob=0.008, amp=0.010), Q)
        clip(rig, "fp_sprint" + sfx, 0.50, lambda t, h=held: h(t, bob=0.014, amp=0.020), Q)
        clip(rig, "fp_ads" + sfx, 1.0, lambda t, C=C: C["hold"](rig, **C["ads"]), (0.0,))
        clip(rig, "fp_lower" + sfx, 1.0, lambda t, C=C: C["hold"](rig, **C["lower"]), (0.0,))

        def fire(t, hold=hold, fp=fp):
            k = math.sin(math.pi * min(1.0, t * 1.6))       # back and up, then settle
            G = Vector(fp["G"]) + Vector((0, -0.03 * k, 0.012 * k))
            hold(rig, G, Vector(fp["forward"]) + Vector((0, 0, 0.06 * k)))
        clip(rig, "fp_fire" + sfx, 0.12, fire, (0.0, 0.3, 0.6, 1.0), loop=False)

        def reload(t, C=C):
            C["hold"](rig, **C["fp"])
            # The left hand drops to the magazine, works it, and comes back.
            k = math.sin(math.pi * min(1.0, max(0.0, (t - 0.1) / 0.8)))
            if k > 0:
                f = Vector(C["fp"]["forward"]).normalized()
                up = (Vector((0, 0, 1)) - f * f.z).normalized()
                G = Vector(C["fp"]["G"])
                fore = G + f * C["fore"][0] + up * C["fore"][1]
                mag = G + f * C["mag"][0] + up * C["mag"][1]
                grip(rig, "l", fore.lerp(mag, k), (f - up * 0.6).normalized(), (f.cross(up)).normalized() * -1,
                     (-0.7, 0.2, -0.7), fingers=(45, 50, 50, 50), thumb=30)
        clip(rig, "fp_reload" + sfx, 2.0, reload, (0.0, 0.1, 0.3, 0.5, 0.7, 0.9, 1.0), loop=False)

    def unarmed(t):
        # Loose fists low in the view, knuckles forward, thumbs up and in.
        b = 0.006 * math.sin(2 * math.pi * t)
        reach(rig, "r", (0.20, 0.30, 1.10 + b), (0.6, -0.2, -0.8), hand_dir=(-0.15, 1, -0.35))
        reach(rig, "l", (-0.20, 0.30, 1.10 - b), (-0.6, -0.2, -0.8), hand_dir=(0.15, 1, -0.35))
        curl(rig, "r", CURL * 1.2, thumb=CURL * 0.4)
        curl(rig, "l", CURL * 1.2, thumb=CURL * 0.4)
    clip(rig, "fp_unarmed", 3.0, unarmed, Q)
    rig.animation_data.action = None


# ---- build ------------------------------------------------------------------------
def head_box(name, center, size, mat, rot=(0.0, 0.0, 0.0), bevel=0.004):
    """A small rigid part on the head bone (tusk, ear, brow, antenna)."""
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=center, rotation=rot)
    o = bpy.context.object
    o.name = name
    o.scale = Vector(size)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    for n in MATERIALS:
        o.data.materials.append(material(n))
    for p in o.data.polygons:
        p.material_index = list(MATERIALS).index(mat)
    if bevel:
        bv = o.modifiers.new("bevel", "BEVEL")
        bv.width = bevel
        bv.segments = 2
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.modifier_apply(modifier=bv.name)
    g = o.vertex_groups.new(name="head")
    g.add([v.index for v in o.data.vertices], 1.0, "REPLACE")
    return o


def head_parts(kind, head, eye_l, eye_r):
    """Race features, built round the eyes and the skull's measured size."""
    eye = (eye_l + eye_r) * 0.5
    pts = [v.co for v in head.data.vertices]
    # Measured on the skull, not the whole head mesh (it includes the neck).
    ring = [p for p in pts if abs(p.z - eye.z) < 0.025 and p.y > eye.y - 0.02]   # front half: no ears
    half = max(abs(p.x - eye.x) for p in ring)
    front = max(p.y for p in ring if abs(p.x - eye.x) < 0.02)
    hi = Vector((0.0, 0.0, max(p.z for p in pts)))
    out = []
    if kind == "orc":
        out.append(head_box("brow", (eye.x, eye.y + 0.012, eye.z + 0.028), (0.11, 0.04, 0.025), "skin", rot=(0.25, 0, 0)))
        for sx in (1, -1):
            out.append(head_box("tusk", (eye.x + 0.022 * sx, front - 0.012, eye.z - 0.085), (0.012, 0.012, 0.035), "ivory",
                                rot=(0.35, 0, -0.25 * sx)))
            out.append(head_box("ear", (half * 0.98 * sx, eye.y - 0.085, eye.z + 0.01), (0.012, 0.035, 0.06), "skin",
                                rot=(-0.6, 0, 0.5 * sx)))
    elif kind == "android":
        out.append(head_box("seam", (0.0, eye.y - 0.06, hi.z - 0.012), (0.006, 0.17, 0.012), "suit", bevel=0.0))
        out.append(head_box("brow_line", (eye.x, front - 0.004, eye.z + 0.022), (0.075, 0.006, 0.004), "eye", bevel=0.0))
    elif kind == "robot":
        out.append(head_box("visor", (eye.x, front - 0.018, eye.z), (half * 1.9, 0.03, 0.028), "eye", bevel=0.003))
        out.append(head_box("antenna", (half * 0.6, eye.y - 0.07, hi.z + 0.05), (0.01, 0.01, 0.10), "glove", bevel=0.0))
        out.append(head_box("antenna_tip", (half * 0.6, eye.y - 0.07, hi.z + 0.105), (0.02, 0.02, 0.02), "eye"))
        out.append(head_box("jaw", (eye.x, front - 0.03, eye.z - 0.075), (half * 1.3, 0.05, 0.035), "suit"))
    return out


def build():
    h, rig, (eye_l, eye_r) = make_human()
    dress(h, eye_l, eye_r)
    parts = split(h)
    kind = ACTIVE.get("head_parts")
    if kind:
        extra = head_parts(kind, parts["head"], eye_l, eye_r)
        bpy.ops.object.select_all(action="DESELECT")
        for o in extra:
            o.select_set(True)
        parts["head"].select_set(True)
        bpy.context.view_layer.objects.active = parts["head"]
        bpy.ops.object.join()
        parts["head"] = bpy.context.view_layer.objects.active
        parts["head"].name = parts["head"].data.name = "head"
        bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35))
    if ACTIVE.get("flat"):
        for o in parts.values():
            bpy.ops.object.select_all(action="DESELECT")
            o.select_set(True)
            bpy.context.view_layer.objects.active = o
            bpy.ops.object.shade_flat()
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
    # `-- npc.grunt npc.gunner`: build only those; default is every variant.
    want = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else list(VARIANTS)
    for asset_id in want:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        ACTIVE.clear()
        ACTIVE.update(VARIANTS[asset_id])
        rig, parts = build()
        tris = {n: sum(len(p.vertices) - 2 for p in o.data.polygons) for n, o in parts.items()}
        out = os.path.join(ART, "build", asset_id + ".raw.glb")
        export(out)
        print("wrote", out, tris)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
