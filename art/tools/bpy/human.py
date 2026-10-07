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
     (undersuit sleeves, bare hands), then the trunk cut level twice:
     `chest` above the mid-chest line (EYE - CHEST_DROP), `torso` down to
     the hip joints, `legs` below -- five meshes (chest capped at neck and
     shoulders, torso capped on top, legs open), as the client's
     first-person body needs (art/README.md);
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
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import textures  # noqa: E402 -- generated surface maps
import skin as skinpaint  # noqa: E402 -- the Colonist's painted face and eyes

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
    "eye":   ((0.16, 0.10, 0.06), 0.15, 0.0),   # iris (variants recolour it: android cyan, orc red)
    "boot":  ((0.07, 0.07, 0.08), 0.85, 0.0),
    "ivory": ((0.86, 0.82, 0.70), 0.5, 0.0),    # orc tusks
    "sclera": ((0.88, 0.86, 0.82), 0.3, 0.0),   # the white of the eye
}

KEEP_GROUPS = ("body", "helper-l-eye", "helper-r-eye")

# Character, not caricature: a brow ridge and cheekbones that catch light,
# a defined jaw. MakeHuman target names (MPFB data/targets/<group>/<name>).
COLONIST_FACE = {
    "eyebrows/eyebrows-trans-forward": 0.45, "forehead/forehead-nubian-incr": 0.25,
    "cheek/l-cheek-bones-incr": 0.5, "cheek/r-cheek-bones-incr": 0.5,
    "cheek/l-cheek-inner-decr": 0.3, "cheek/r-cheek-inner-decr": 0.3,
    "chin/chin-bones-incr": 0.35, "chin/chin-prominent-incr": 0.25,
    "nose/nose-hump-incr": 0.15,
    # a more open eye (the lids narrowed to slits at game distance)
    "eyes/l-eye-height2-incr": 0.45, "eyes/r-eye-height2-incr": 0.45,
    "eyes/l-eye-scale-incr": 0.15, "eyes/r-eye-scale-incr": 0.15,
}
COLONIST_FACE_F = {
    "eyebrows/eyebrows-trans-forward": 0.25,
    "cheek/l-cheek-bones-incr": 0.5, "cheek/r-cheek-bones-incr": 0.5,
    "cheek/l-cheek-inner-decr": 0.3, "cheek/r-cheek-inner-decr": 0.3,
    "chin/chin-prominent-incr": 0.15,
    "eyes/l-eye-height2-incr": 0.4, "eyes/r-eye-height2-incr": 0.4,
    "eyes/l-eye-scale-incr": 0.15, "eyes/r-eye-scale-incr": 0.15,
}
# Head-weighted decimation (`lod` variants): the decimator's vertex-group
# factor makes collapsing a head edge this much costlier than a torso one;
# hands sit between (they fill the first-person view).
HEAD_TRIS = 4050   # the face's share of BUDGET (head + neck, before eyeballs); +600 since the brows are painted
LOD_WEIGHT = {"feature": 0.05, "ear": 0.04, "head": 0.2, "hand": 0.6}   # lower = kept; 0 would lock a vertex outright
BUDGET = 9000      # body + arms + head for a `lod` variant (the Vanguard's ceiling)

# Every humanoid comes from this file. A variant changes the MakeHuman
# sliders, the eye height (overall size), material colours, whether the head
# has hair, flat (machine) shading, and adds rigid head parts. Bones, clips,
# hand mounts and contact-point grips are shared, so every body holds a gun
# and wears armor the same way. Armor is authored on char.player's build;
# variants that keep that build (gunner) can wear it, bigger ones (orc) not.
VARIANTS = {
    # The Colonist v2 (GDD "Faces and hair"): a slimmer, shorter build than
    # the Vanguard (eyes at 1.65), most of the 9000-tri budget spent on the
    # face (head-weighted decimation, `lod`), modest bone structure from
    # MakeHuman's face targets, eyebrows painted into the skin (skin.BROW_*).
    # Bald: hair is its own piece (tools/bpy/hair.py).
    "char.player": {
        "macro": {"muscle": 0.40, "weight": 0.45, "proportions": 0.75, "age": 0.5},
        "targets": dict(COLONIST_FACE, **{"torso/measure-shoulder-dist-decr": 0.45, "torso/torso-vshape-decr": 0.25}),
        "eye": 1.65, "hair": False, "lod": True,
        "paint_brows": skinpaint.BROW_M,
        "materials": {"skin": (0.78, 0.58, 0.46), "eye": (0.44, 0.32, 0.21)},
    },
    "char.player.f": {                         # the player body, female (GDD "Bodies": COLONIST F)
        "macro": {"gender": 0.0, "muscle": 0.38, "weight": 0.42, "proportions": 0.8, "age": 0.5},
        "targets": dict(COLONIST_FACE_F, **{"torso/measure-shoulder-dist-decr": 0.25}),
        "eye": 1.65, "hair": False, "lod": True,
        "paint_brows": skinpaint.BROW_F,
        "materials": {"skin": (0.80, 0.60, 0.48), "eye": (0.42, 0.46, 0.28)},
    },
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
    "char.ubc": {                              # spike: Quaternius Universal Base Characters body (CC0)
        "ubc": {"body": "Superhero_Male_FullBody"},     # hair: tools/bpy/hair.py
    },
    "char.ubc.f": {                            # UBC Superhero female (GDD "Bodies": VANGUARD F)
        "ubc": {"body": "Superhero_Female_FullBody"},
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
        if name in SURFACE:
            textures.dress(m, SURFACE[name], col)
    return m


# Materials that get a generated surface texture (tools/bpy/textures.py).
SURFACE = {"suit": "suit", "glove": "fabric", "boot": "fabric"}


# ---- the human ----------------------------------------------------------------
UBC = os.path.join(ART, "vendor", "ubc")


def ubc_human():
    """A Universal Base Characters body (art/vendor/ubc, CC0 from
    quaternius.com) in place of MakeHuman. Its 65-bone skeleton is the
    game_engine rig's 53 plus finger/toe leaf bones, so after two renames
    and folding the leaves into their parents the rest of this file runs
    unchanged. It keeps its own textured skin, eyes and hair (dress())."""
    spec = ACTIVE["ubc"]
    bpy.ops.import_scene.gltf(filepath=os.path.join(UBC, "Base Characters", "Godot - UE", spec["body"] + ".gltf"))
    rig = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
    for o in list(bpy.context.scene.objects):            # the pack ships a stray Icosphere
        if o.type == "MESH" and o.parent != rig:
            bpy.data.objects.remove(o, do_unlink=True)
    if spec.get("hair"):
        before = set(bpy.context.scene.objects)
        bpy.ops.import_scene.gltf(filepath=os.path.join(UBC, "Hairstyles", "Rigged to Head Bone", "glTF (Godot -Unreal)",
                                                        spec["hair"] + ".gltf"))
        for o in set(bpy.context.scene.objects) - before:
            if o.type == "MESH":
                o.parent = rig
                o.matrix_parent_inverse = Matrix.Identity(4)
                # Named "hair" so a helmet's `covers: head/hair` hides it
                # (Hair_Buns stands out through every helmet otherwise).
                for m in o.data.materials:
                    if m:
                        m.name = "hair"
            else:
                bpy.data.objects.remove(o, do_unlink=True)
    # Some materials point at "<name>_png.png"; the pack ships "<name>.png".
    for img in bpy.data.images:
        if img.filepath and not os.path.exists(bpy.path.abspath(img.filepath)):
            img.filepath = img.filepath.replace("_png.png", ".png")
            img.reload()
        if img.size[0] > 1024:                           # 2-4K maps: 16 MB glb; 1K is plenty at game scale
            img.scale(1024, 1024 * img.size[1] // img.size[0])
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    for m in bpy.data.materials:
        m["keep_uv"] = True                              # textures.uv_box leaves these faces' UVs alone

    eyes_obj = next(o for o in meshes if o.name.startswith("Eyes"))
    pts = [eyes_obj.matrix_world @ v.co for v in eyes_obj.data.vertices]
    eye_z = sum(p.z for p in pts) / len(pts)

    # One mesh; vertex groups merge by name.
    for o in meshes:
        for mod in list(o.modifiers):
            o.modifiers.remove(mod)
        o.data.transform(o.matrix_world)
        o.matrix_world = Matrix.Identity(4)
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    h = next(o for o in meshes if o.name.lower().startswith("superhero") or len(o.data.vertices) > 5000)
    bpy.context.view_layer.objects.active = h
    bpy.ops.object.join()
    # The pack's all-white vertex colours make Godot set "vertex colour as
    # albedo", and ViewModel.FpOverride then draws the first-person arms flat white.
    while h.data.color_attributes:                       # removing one renames/invalidates the rest
        h.data.color_attributes.remove(h.data.color_attributes[0])
    # Four UV sets ship; the textures read the render one, uv_box writes the active one.
    keep = next(u.name for u in h.data.uv_layers if u.active_render)
    for name in [u.name for u in h.data.uv_layers if u.name != keep]:
        h.data.uv_layers.remove(h.data.uv_layers[name])
    h.data.uv_layers.active = h.data.uv_layers[keep]
    # The pack's eyebrows: greyscale strands the pack tints in its own
    # shader (white when drawn raw). Same tinted `hair` material as every
    # hair piece, so a helmet hides them alike.
    for i, m in enumerate(h.data.materials):
        if m and m.name.startswith("MI_Hair"):
            tex = next(n.image for n in m.node_tree.nodes if n.type == "TEX_IMAGE" and n.image and "Normal" not in n.image.name)
            h.data.materials[i] = hair_material(tex)

    # Leaf bones carry finger-tip weights: fold them into the parent, then drop them.
    parents = {b.name: b.parent.name for b in rig.data.bones if "leaf" in b.name}
    groups = {g.name: g for g in h.vertex_groups}
    for leaf, parent in parents.items():
        if leaf in groups:
            gi = groups[leaf].index
            for v in h.data.vertices:
                w = sum(g.weight for g in v.groups if g.group == gi)
                if w > 0:
                    groups[parent].add([v.index], w, "ADD")
            h.vertex_groups.remove(groups[leaf])
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    for leaf in parents:
        rig.data.edit_bones.remove(rig.data.edit_bones[leaf])
    bpy.ops.object.mode_set(mode="OBJECT")
    for old, new in (("Head", "head"), ("root", "Root")):
        rig.data.bones[old].name = new
        if old in h.vertex_groups:
            h.vertex_groups[old].name = new

    # Front -Y -> +Y and eyes to EYE, mesh and bones together (as make_human).
    s = ACTIVE.get("eye", EYE) / eye_z
    M = Matrix.Diagonal((s, s, s, 1.0)) @ Matrix.Rotation(math.pi, 4, "Z")
    h.data.transform(M)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    rig.data.transform(M)
    bpy.ops.object.mode_set(mode="OBJECT")
    rig.matrix_world = Matrix.Identity(4)
    h.parent = rig
    h.matrix_parent_inverse = Matrix.Identity(4)
    pts = [M @ p for p in pts]
    eyes = tuple(sum(q, Vector()) / len(q) for q in ([p for p in pts if p.x < 0], [p for p in pts if p.x > 0]))

    # UBC rests in a T-pose; armor.py and every clip were tuned on MakeHuman's
    # A-pose rest. Pose the arms to MakeHuman's rest directions and make that
    # the rest (mesh through the armature, then the pose applied to the bones).
    for p in rig.pose.bones:
        p.rotation_mode = "QUATERNION"
    for side, sx in (("r", 1), ("l", -1)):
        aim(rig, "upperarm_" + side, (0.657 * sx, 0.001, -0.754))
        aim(rig, "lowerarm_" + side, (0.502 * sx, 0.718, -0.482))
        aim(rig, "hand_" + side, (0.327 * sx, 0.827, -0.457))
    mod = h.modifiers.new("rest", "ARMATURE")
    mod.object = rig
    bpy.context.view_layer.objects.active = h
    bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    bpy.ops.pose.armature_apply(selected=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    h.name = "human"
    return h, rig, eyes


# ---- refitting Quaternius head pieces (hair; used by hair.py) -----------------
HAIR_DIR = os.path.join(UBC, "Hairstyles", "Rigged to Head Bone", "glTF (Godot -Unreal)")
UBC_BODY = {"m": "Superhero_Male_FullBody", "f": "Superhero_Female_FullBody"}
HAIR_TINT = (0.60, 0.45, 0.32)      # display colour multiplied into the pack's greyscale strand texture
HAIR_TEX = 256                       # px; strands at game scale, and every hair glb embeds its own copy


def _import(path):
    """Import a glTF; return its new objects (meshes baked to world, no modifiers)."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        if o.type == "MESH":
            for mod in list(o.modifiers):
                o.modifiers.remove(mod)
            o.data.transform(o.matrix_world)
            o.parent = None
            o.matrix_world = Matrix.Identity(4)
    return new


def _drop(objs):
    for o in objs:
        if o.name in bpy.data.objects:
            bpy.data.objects.remove(o, do_unlink=True)


TURN = Matrix.Rotation(math.pi, 4, "Z")     # the pack faces -Y after import; this project +Y


def surface(verts, polys):
    from mathutils.bvhtree import BVHTree
    return BVHTree.FromPolygons([tuple(v) for v in verts], [tuple(p) for p in polys])


def ubc_source(sex):
    """The UBC Superhero head a pack piece was authored on, turned to +Y
    front: (surface BVH of the skin above the shoulders, (eye_l, eye_r),
    skin points above the eyes' level - 2 cm)."""
    objs = _import(os.path.join(UBC, "Base Characters", "Godot - UE", UBC_BODY[sex] + ".gltf"))
    meshes = [o for o in objs if o.type == "MESH"]
    eyes_o = next(o for o in meshes if o.name.startswith("Eyes"))
    skin = max(meshes, key=lambda o: len(o.data.vertices))
    pts = [TURN @ v.co for v in eyes_o.data.vertices]
    eyes = tuple(sum(q, Vector()) / len(q) for q in ([p for p in pts if p.x < 0], [p for p in pts if p.x > 0]))
    ez = eyes[0].z
    verts = [TURN @ v.co for v in skin.data.vertices]
    polys = [p.vertices[:] for p in skin.data.polygons if min(verts[i].z for i in p.vertices) > ez - 0.30]
    out = (surface(verts, polys), eyes, [v for v in verts if v.z > ez - 0.02])
    _drop(objs)
    return out


def head_target(head, eyes):
    """The same for a built head mesh (eyeball spheres left out)."""
    verts = [v.co.copy() for v in head.data.vertices]
    polys = [p.vertices[:] for p in head.data.polygons
             if min((p.center - e).length for e in eyes) > 0.0152]
    used = {i for p in polys for i in p}
    ez = (eyes[0].z + eyes[1].z) / 2
    return surface(verts, polys), eyes, [verts[i] for i in used if verts[i].z > ez - 0.02]


def signed(bvh, co):
    loc, n, _, _ = bvh.find_nearest(co)
    return loc, n, (co - loc).dot(n)


def import_piece(name):
    """A pack hair/brow piece as one mesh in the +Y frame, its own armature
    gone, extra UV sets and vertex colours stripped (Godot reads vertex
    colour as albedo; uv_box writes the active set)."""
    objs = _import(os.path.join(HAIR_DIR, name + ".gltf"))
    me = next(o for o in objs if o.type == "MESH")
    _drop([o for o in objs if o is not me])
    me.data.transform(TURN)
    while me.data.color_attributes:
        me.data.color_attributes.remove(me.data.color_attributes[0])
    keep = next(u.name for u in me.data.uv_layers if u.active_render)
    for n in [u.name for u in me.data.uv_layers if u.name != keep]:
        me.data.uv_layers.remove(me.data.uv_layers[n])
    me.data.uv_layers[keep].name = "UVMap"
    for g in list(me.vertex_groups):
        me.vertex_groups.remove(g)
    tex = next((n.image for m in me.data.materials if m and m.use_nodes for n in m.node_tree.nodes
                if n.type == "TEX_IMAGE" and n.image and "Normal" not in n.image.name), None)
    return me, tex


def hair_material(tex, name="hair"):
    """The pack's greyscale strand texture times HAIR_TINT (the pack tints
    it in its own shader; drawn raw it is white). glTF: baseColorTexture x
    baseColorFactor. keep_uv: uv_box leaves the strands' UVs alone."""
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    b = nt.nodes["Principled BSDF"]
    if tex.size[0] > HAIR_TEX:
        tex = tex.copy()
        tex.scale(HAIR_TEX, HAIR_TEX)
    t = nt.nodes.new("ShaderNodeTexImage")
    t.image = tex
    mix = nt.nodes.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    mix.blend_type = "MULTIPLY"
    mix.inputs["Factor"].default_value = 1.0
    nt.links.new(t.outputs["Color"], mix.inputs[6])
    mix.inputs[7].default_value = (*(c ** 2.2 for c in HAIR_TINT), 1.0)
    nt.links.new(mix.outputs[2], b.inputs["Base Color"])
    b.inputs["Roughness"].default_value = 0.75
    b.inputs["Metallic"].default_value = 0.0
    m.diffuse_color = (*(c ** 2.2 for c in HAIR_TINT), 1.0)
    m["keep_uv"] = True
    return m


def refit(obj, src_bvh, dst_bvh, place, k, reach=None, gap=0.0008, smooth=6):
    """Move a pack piece from the UBC head onto another: `place` maps it
    (similarity or per-axis), then each vertex is lifted along the new skin's
    normal so it sits as high above THIS skin as it sat above the source one
    (x k). Tangential positions stay where `place` put them: snapping to the
    nearest skin point folded the brows' strips over each other. The
    corrections are smoothed over the mesh's edges so neighbouring strands
    move together, then nothing may end up under the skin (`gap`). With
    `reach`, only vertices within that height of the source skin are lifted
    (fully under reach/2, fading to none at reach): a bun or a ponytail keeps
    the shape `place` gave it."""
    me = obj.data
    n = len(me.vertices)
    src_d = [signed(src_bvh, v.co)[2] * k for v in me.vertices]
    for v in me.vertices:
        v.co = place(v.co)
    corr = []
    for v, d0 in zip(me.vertices, src_d):
        loc, nrm, d1 = signed(dst_bvh, v.co)
        w = 1.0 if reach is None else max(0.0, min(1.0, 2.0 - 2.0 * d0 / reach))
        corr.append(nrm * ((d0 - d1) * w))
    nbr = [[] for _ in range(n)]
    for e in me.edges:
        a, b = e.vertices
        nbr[a].append(b)
        nbr[b].append(a)
    for _ in range(smooth):
        corr = [(c + sum((corr[j] for j in nb), Vector()) / len(nb)) * 0.5 if nb else c
                for c, nb in zip(corr, nbr)]
    for v, c in zip(me.vertices, corr):
        v.co = v.co + c
        loc, nrm, d = signed(dst_bvh, v.co)
        if d < gap:
            v.co = loc + nrm * gap
    me.update()


def make_human(decimate=True):
    if ACTIVE.get("ubc"):
        return ubc_human()      # ponytail: no decimation, ~14k tris; decimate if the budget holds after the look is judged
    macro = dict(MACRO)
    macro.update(ACTIVE.get("macro", {}))
    h = HumanService.create_human(scale=0.1, macro_detail_dict=macro, feet_on_ground=True)
    # Detail targets on top of the macros, before the rig is fitted to the shape.
    for name, w in ACTIVE.get("targets", {}).items():
        path = TargetService.target_full_path(name.split("/")[-1])
        if path is None or name.split("/")[0] not in path:
            raise SystemExit(f"no MakeHuman target {name}")
        TargetService.load_target(h, path, weight=w)
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

    # The eyes, in the final frame, before their helper groups go, and the
    # eyeball's radius (eye targets change it).
    eyes = (vertex_group_centroid(h, "helper-l-eye"), vertex_group_centroid(h, "helper-r-eye"))
    gi = h.vertex_groups["helper-l-eye"].index
    ball = [v.co for v in h.data.vertices if any(g.group == gi and g.weight > 0.5 for g in v.groups)]
    # The helper's mean radius includes its cornea bulge: x EYEBALL_FIT is
    # the ball that sits under the lids (14.5 mm on the default eye).
    ACTIVE["_eye_r"] = sum((c - eyes[0]).length for c in ball) / len(ball) * EYEBALL_FIT
    gis = {h.vertex_groups[g].index for g in ("helper-l-eye", "helper-r-eye")}
    ACTIVE["_eyeball"] = [any(g.group in gis and g.weight > 0.5 for g in v.groups) for v in h.data.vertices]
    print(f"eyeball radius {ACTIVE['_eye_r'] * 1000:.2f} mm")

    # Only bone groups survive (MPFB adds helper/joint groups too).
    bones = {b.name for b in rig.data.bones}
    if ACTIVE.get("lod") and "lips" in h.vertex_groups:
        h.vertex_groups["lips"].name = "_lips"      # lod_decimate keeps them; "_" groups are not bones
        h.vertex_groups["ears"].name = "_ears"      # painted (skin.py) and kept smoother in decimation
        h.vertex_groups["scalp"].name = "_scalp"    # painted: a faint stubble shadow (male)
    for g in list(h.vertex_groups):
        if g.name not in bones and not g.name.startswith("_"):
            h.vertex_groups.remove(g)

    if not decimate:                     # armor.py cuts panels from the clean quads
        h.name = "human"
        return h, rig, eyes
    if ACTIVE.get("lod"):
        paint_skin(h, eyes, rig)
        lod_decimate(h, eyes)
        new_eyeballs(h, eyes)
        h.name = "human"
        return h, rig, eyes
    # Decimate to budget; vertex groups are interpolated through it.
    tris = sum(len(p.vertices) - 2 for p in h.data.polygons)
    dec = h.modifiers.new("decimate", "DECIMATE")
    dec.ratio = min(1.0, BODY_TRIS / tris)
    bpy.context.view_layer.objects.active = h
    bpy.ops.object.modifier_apply(modifier=dec.name)
    new_eyeballs(h, eyes)
    h.name = "human"
    return h, rig, eyes


def eye_r():
    return ACTIVE.get("_eye_r", EYEBALL_R)


def skin_faces(h, eyes):
    """Full-resolution faces the painted skin covers: head and neck down to
    a little under the collar line (the cut comes later), not the eyeballs."""
    dom = dominant_bones(h)
    r = eye_r() + 0.001
    ball = ACTIVE["_eyeball"]
    out = []
    for p in h.data.polygons:
        bones = [dom[v] for v in p.vertices]
        b = max(set(bones), key=bones.count)
        c = p.center
        out.append(b in ("head", "neck_01") and collar_side(c, eyes[0]) > -0.015
                   and min((c - e).length for e in eyes) > r and not any(ball[i] for i in p.vertices))
    return out


def paint_skin(h, eyes, rig):
    base = ACTIVE.get("materials", {}).get("skin", MATERIALS["skin"][0])
    female = ACTIVE.get("macro", {}).get("gender", MACRO["gender"]) < 0.5
    # The gloves (dress(): every face whose dominant bone is a hand or finger).
    dom = dominant_bones(h)
    glove = []
    for p in h.data.polygons:
        bones = [dom[v] for v in p.vertices]
        b = max(set(bones), key=bones.count)
        glove.append(bool(b) and b.startswith(FINGER_BONES))
    knuckles = []
    for side in ("r", "l"):
        dorsal = -palm_normal(rig, side)
        for f in ("index", "middle", "ring", "pinky"):
            for j, r in (("01", 0.012), ("02", 0.008), ("03", 0.006)):
                knuckles.append((tuple(pb(rig, f"{f}_{j}_{side}").matrix.translation), tuple(dorsal), r))
    skinpaint.paint(h, eyes, eye_r(), base, female, skin_faces(h, eyes), ACTIVE["_eyeball"],
                    glove, ACTIVE.get("materials", {}).get("glove", MATERIALS["glove"][0]), knuckles,
                    brow=ACTIVE.get("paint_brows"))


EYEBALL_TRIS = 2 * (16 * 10 * 2 + 2 * 16)     # new_eyeballs(): two 16x12 UV spheres
CAP_TRIS = 230                                # split()+cap(): the chest line's bisect (~110) and the caps -- neck and shoulders on `chest`, the chest line on `torso` (~100) -- measured on `lod` bodies


def lod_decimate(h, eyes):
    """Decimate with the budget spent on the face: the eyeballs go first
    (they come back as clean spheres -- deleting them after decimation by
    distance took eyelid faces with them at head resolution), then one
    collapse pass where a vertex group makes head edges LOD_FACTOR times
    costlier to collapse than torso ones, hands in between. The total lands
    on BUDGET less what the eyeballs will add."""
    bm = bmesh.new()
    bm.from_mesh(h.data)
    doomed = [f for f in bm.faces if min((f.calc_center_median() - e).length for e in eyes) < eye_r() + 0.001]
    bmesh.ops.delete(bm, geom=doomed, context="FACES")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    bm.to_mesh(h.data)
    bm.free()
    g = h.vertex_groups.new(name="_lod")
    lips = h.vertex_groups["_lips"].index if "_lips" in h.vertex_groups else -1
    ears = h.vertex_groups["_ears"].index if "_ears" in h.vertex_groups else -1
    for v, b in zip(h.data.vertices, dominant_bones(h)):
        part = "head" if b in ("head", "neck_01") else "hand" if b and b.startswith(FINGER_BONES) else None
        # Eyelids and lips are what a face reads by: kept longest of all.
        if part == "head" and (min((v.co - e).length for e in eyes) < 0.032
                               or any(x.group == lips and x.weight > 0.3 for x in v.groups)):
            part = "feature"
        elif part == "head" and any(x.group == ears and x.weight > 0.3 for x in v.groups):
            part = "ear"
        g.add([v.index], LOD_WEIGHT.get(part, 1.0), "REPLACE")
    want = BUDGET - EYEBALL_TRIS - CAP_TRIS - 40
    tris = sum(len(p.vertices) - 2 for p in h.data.polygons)

    def collapse(obj, factor):
        dec = obj.modifiers.new("decimate", "DECIMATE")
        dec.ratio = min(1.0, want / tris)
        dec.vertex_group = "_lod"
        dec.vertex_group_factor = factor
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.modifier_apply(modifier=dec.name)

    def head_tris(obj):
        dom = dominant_bones(obj)
        return sum(len(p.vertices) - 2 for p in obj.data.polygons
                   if sum(part_of(dom[i]) == "head" for i in p.vertices) * 2 > len(p.vertices))

    # The factor that lands the head on HEAD_TRIS: bisect on trial copies.
    # Measured on a sphere: a LOWER weight is collapsed later, 0 never; the
    # factor grades it (0.01 mild .. 10 total). Bisect on its log.
    lo, hi = -5.0, 1.0
    for _ in range(8):
        f = 10 ** ((lo + hi) / 2)
        t = h.copy()
        t.data = h.data.copy()
        bpy.context.scene.collection.objects.link(t)
        collapse(t, f)
        n = head_tris(t)
        bpy.data.objects.remove(t, do_unlink=True)
        print(f"LOD factor {f:.2e}: head {n}")
        lo, hi = ((lo + hi) / 2, hi) if n < HEAD_TRIS else (lo, (lo + hi) / 2)
    collapse(h, 10 ** ((lo + hi) / 2))
    smooth_ears(h)
    for name in ("_lod", "_lips", "_ears", "_scalp"):
        if name in h.vertex_groups:
            h.vertex_groups.remove(h.vertex_groups[name])
    collar(h, eyes[0])
    print(f"LOD {tris} -> {sum(len(p.vertices) - 2 for p in h.data.polygons)} tris (want {want}), head {head_tris(h)}")


def smooth_ears(h):
    """The decimated concha is a few big triangles at odd angles (faceted
    under a key light): relax the ear's inner vertices. The painted AO
    carries the folds."""
    if "_ears" not in h.vertex_groups:
        return
    gi = h.vertex_groups["_ears"].index
    bm = bmesh.new()
    bm.from_mesh(h.data)
    bm.verts.ensure_lookup_table()
    ear = [bm.verts[v.index] for v in h.data.vertices
           if any(g.group == gi and g.weight > 0.6 for g in v.groups)]
    ear = [v for v in ear if not v.is_boundary]
    for _ in range(EAR_SMOOTH):
        bmesh.ops.smooth_vert(bm, verts=ear, factor=0.5, use_axis_x=True, use_axis_y=True, use_axis_z=True)
    bm.to_mesh(h.data)
    bm.free()


EAR_SMOOTH = 2
COLLAR = (0.165, 0.25)     # under the eyes at the throat; rises this much per metre toward the nape


def collar_side(c, eye):
    """> 0 above the collar line (skin), < 0 under it (suit)."""
    return c.z - (eye.z - COLLAR[0] + COLLAR[1] * (eye.y - c.y))


def collar(h, eye):
    """Cut the neck along the collar plane so the suit's edge is a clean
    line, not the decimated faces' stair steps."""
    n = Vector((0.0, COLLAR[1], 1.0)).normalized()
    co = Vector((0.0, eye.y, eye.z - COLLAR[0]))
    bm = bmesh.new()
    bm.from_mesh(h.data)
    near = [f for f in bm.faces if abs(collar_side(f.calc_center_median(), eye)) < 0.03 and f.calc_center_median().z > eye.z - 0.3]
    geom = list({v for f in near for v in f.verts}) + list({e for f in near for e in f.edges}) + near
    bmesh.ops.bisect_plane(bm, geom=geom, plane_co=co, plane_no=n)
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
    bm.to_mesh(h.data)
    bm.free()


FINGER_BONES = ("hand", "thumb", "index", "middle", "ring", "pinky")


EYEBALL_FIT = 0.926    # measured: 14.5 mm ball / 15.66 mm mean helper radius
EYEBALL_R = 0.0145     # MakeHuman's eyeball, measured: every face within 1.6 cm of an eye centre


def new_eyeballs(h, eyes):
    """Decimation leaves each eyeball a handful of big triangles (the iris was
    one jagged one). Swap them for clean spheres, pole forward (+Y) so the
    iris dress() paints is a round cap, rigid on the head bone."""
    bm = bmesh.new()
    bm.from_mesh(h.data)
    doomed = [f for f in bm.faces if min((f.calc_center_median() - e).length for e in eyes) < eye_r() + 0.0015]
    bmesh.ops.delete(bm, geom=doomed, context="FACES")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    deform = bm.verts.layers.deform.verify()
    head = h.vertex_groups["head"].index
    for e in eyes:
        M = Matrix.Translation(e) @ Matrix.Rotation(-math.pi / 2, 4, "X")   # +Z pole -> +Y
        ball = bmesh.ops.create_uvsphere(bm, u_segments=16, v_segments=12, radius=eye_r(), matrix=M)
        for v in ball["verts"]:
            v[deform][head] = 1.0
        if ACTIVE.get("lod"):
            skinpaint.eye_uvs(bm, {f for v in ball["verts"] for f in v.link_faces}, e)
    bm.to_mesh(h.data)
    bm.free()


def vertex_group_centroid(obj, name):
    gi = obj.vertex_groups[name].index
    pts = [v.co for v in obj.data.vertices if any(g.group == gi and g.weight > 0.5 for g in v.groups)]
    return sum(pts, Vector()) / len(pts)


def dominant_bones(obj):
    """Per vertex, the bone with the largest weight."""
    names = {g.index: g.name for g in obj.vertex_groups}
    out = []
    for v in obj.data.vertices:
        best = max((g for g in v.groups if not names.get(g.group, "_").startswith("_")),
                   key=lambda g: g.weight, default=None)
        out.append(names.get(best.group) if best else None)
    return out


ARM_BONES = ("upperarm", "lowerarm", "hand", "thumb", "index", "middle", "ring", "pinky")
LEG_BONES = ("thigh", "calf", "foot", "ball")


def part_of(bone):
    """By dominant bone; split() then re-cuts the trunk level into chest/torso/legs."""
    if bone is None:
        return "torso"
    if bone in ("head", "neck_01"):
        return "head"
    if bone.startswith(ARM_BONES):
        return "arms"
    if bone.startswith(LEG_BONES):
        return "legs"
    return "torso"


def dress(h, eye_l, eye_r):
    """Material per face from its dominant bone and where it is. The
    undersuit runs from the jaw line down; armor adds the detail, so there
    are no painted seams (bands on decimated triangles read as tears)."""
    base = len(h.data.materials)            # an imported body (UBC) keeps its own slots first
    eye_r_ = ACTIVE.get("_eye_r", EYEBALL_R)   # (eye_r is the right eye here)
    for n in MATERIALS:
        h.data.materials.append(material(n))
    idx = {n: base + i for i, n in enumerate(MATERIALS)}
    dom = dominant_bones(h)
    head_pts = [v.co for v, b in zip(h.data.vertices, dom)          # not the eyeballs: their dense
                if b == "head" and min((v.co - eye_l).length, (v.co - eye_r).length) > 0.016]   # spheres pull the hairline
    hc = sum(head_pts, Vector()) / len(head_pts)       # skull centre: hair is measured from here
    for p in h.data.polygons:
        c = p.center
        bones = [dom[v] for v in p.vertices]
        b = max(set(bones), key=bones.count)
        r = c - hc
        if ACTIVE.get("ubc") and b == "head":
            continue                                     # the pack's textured skin, eyes, brows and hair
        if (c - eye_l).length < eye_r_ + 0.0007 or (c - eye_r).length < eye_r_ + 0.0007:
            # Every face this close is eyeball (r ~0.0145). One dark colour
            # over the whole ball read as an empty socket in game; the front
            # cap (within ~25 degrees of straight ahead, +Y) is the iris.
            e = eye_l if (c - eye_l).length < (c - eye_r).length else eye_r
            m = "eye" if (c - e).normalized().y > 0.9 else "sclera"
        elif ACTIVE.get("lod") and b in ("head", "neck_01"):
            # The undersuit's collar: a clean line cut by collar() in lod_decimate.
            # (A painted lip -- MakeHuman's `lips` group -- read as a smudge:
            # its faces run past the vermilion. The geometry carries the mouth.)
            m = "suit" if collar_side(c, eye_l) < 0 else "skin"
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
    if ACTIVE.get("lod"):
        skinpaint.dress_eyes([material("eye"), material("sclera")],
                             ACTIVE.get("materials", {}).get("eye", MATERIALS["eye"][0]), TEX_DIR, ACTIVE["_id"])


TEX_DIR = os.path.join(ART, "build", "tex")     # the baked maps (the glb embeds its own copy)


CHEST_DROP = 0.40     # the mid-chest cut: this far below the eye (GDD "First-person body, in the world")


def chest_line(h):
    """z (h's local frame) of the flat mid-chest cut. Every face crossing it
    is bisected first, so `chest` and `torso` meet on one exactly level
    ring: no teeth to tidy, a flat cap, chest bottom == torso top."""
    return (h.matrix_world.inverted() @ Vector((0, 0, ACTIVE.get("eye", EYE) - CHEST_DROP))).z


def bisect_chest(h, z):
    """Split the trunk's faces (not the arms' or head's: their ring sits
    above the line, and an arm hanging at the side must not gain a seam)
    on the plane z. Weights and UVs are interpolated on the new vertices."""
    dom = dominant_bones(h)
    bm = bmesh.new()
    bm.from_mesh(h.data)
    bm.verts.ensure_lookup_table()
    trunk = [f for f in bm.faces
             if all(part_of(dom[v.index]) in ("torso", "legs") for v in f.verts)
             and min(v.co.z for v in f.verts) < z < max(v.co.z for v in f.verts)]
    geom = list({e for f in trunk for e in f.edges}) + trunk + list({v for f in trunk for v in f.verts})
    before = sum(len(f.verts) - 2 for f in bm.faces)
    bmesh.ops.bisect_plane(bm, geom=geom, plane_co=(0, 0, z), plane_no=(0, 0, 1), dist=1e-5)
    # A quad cut across two neighbouring edges leaves a pentagon: tangents
    # (the normal map) want tris and quads only.
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4],
                          quad_method="BEAUTY", ngon_method="BEAUTY")
    bm.to_mesh(h.data)
    after = sum(len(f.verts) - 2 for f in bm.faces)
    bm.free()
    print(f"split: chest line bisects {len(trunk)} faces, +{after - before} tris")


def split(h, rig):
    """`head`, `arms`, `chest` and `legs` off into their own meshes; the
    rest is `torso`. Phase 19 (GDD "First-person body, in the world"):
    remote players draw all five; the local player draws `torso` and
    `legs` through the near-cut material, so looking down shows the
    capped mid-chest, the thighs and the feet."""
    # The chest line is a true plane cut, made before the bone weights are
    # read (the new vertices carry interpolated weights).
    chest = chest_line(h)
    bisect_chest(h, chest)
    # The whole body's smooth normals, kept per vertex: the cut parts are
    # shaded with them (keep_normals), so no seam or sliver shades on its own.
    vn = h.data.attributes.new("_vn", "FLOAT_VECTOR", "POINT")
    vn.data.foreach_set("vector", [c for v in h.data.vertex_normals for c in v.vector])
    dom = dominant_bones(h)
    # The waist cut is LEVEL, at the hip joints (the thighs' heads), not
    # the pelvis/thigh weight border: that border runs down the groin
    # crease, two rings that leave the buttocks on the torso. Level, it is
    # one ring round the hips, one flat cap, and looking down sees the
    # thighs from the top.
    inv = h.matrix_world.inverted()
    cut = sum((inv @ (rig.matrix_world @ rig.data.bones[b].head_local)).z for b in ("thigh_l", "thigh_r")) / 2
    label = []
    for p in h.data.polygons:
        bones = [dom[v] for v in p.vertices]
        part = part_of(max(set(bones), key=bones.count))
        if part in ("torso", "legs"):
            part = "legs" if p.center.z < cut else "chest" if p.center.z > chest else "torso"
        label.append(part)
    # A face with most of its edges on a part it hands teeth to, or a corner
    # no other face of its own part holds, is a tooth of the ragged cut: it
    # goes to that part. The drawn side gives: the chest to the head and arms
    # (third person sees the neck and shoulders), the legs to the torso (the
    # local player looks down at the waist). The chest line needs none: it
    # was bisected flat, and moving a face across it would only make teeth.
    # Two passes: more would start eating along a diagonal cut.
    gives = {"chest": ("head", "arms"), "torso": ("head", "arms"), "legs": ("torso",)}
    faces_of = {}
    for p in h.data.polygons:
        for k in p.edge_keys:
            faces_of.setdefault(k, []).append(p.index)
    fan = {}
    for p in h.data.polygons:
        for v in p.vertices:
            fan.setdefault(v, []).append(p.index)
    for _ in range(2):
        moved = {}
        for p in h.data.polygons:
            own = label[p.index]
            if own not in gives:
                continue
            n = {}
            for k in p.edge_keys:
                for q in faces_of[k]:
                    if q != p.index and label[q] in gives[own]:
                        n[label[q]] = n.get(label[q], 0) + 1
            for part, c in n.items():
                if c * 2 > len(p.vertices):
                    moved[p.index] = part
            # A spike: a decimation sliver reaching out along the shoulder.
            for v in p.vertices:
                others = [label[q] for q in fan[v] if q != p.index]
                if others and own not in others:
                    best = max(set(others), key=others.count)
                    if best in gives[own]:
                        moved[p.index] = best
        for i, part in moved.items():
            label[i] = part
        print(f"split: {len(moved)} teeth moved")
    # The arms' ring must sit wholly above the chest line, or the torso's
    # top ring would run into an armpit hole and cap as one.
    pit = min((min(h.data.vertices[v].co.z for v in p.vertices) for p in h.data.polygons
               if label[p.index] == "arms" and any(label[q] in ("chest", "torso")
                                                   for k in p.edge_keys for q in faces_of[k])), default=chest + 1)
    print(f"split: waist cut at z {cut:.3f} (hip joints), chest line at z {chest:.3f}, lowest armpit z {pit:.3f}")
    if pit < chest + 0.005:
        raise SystemExit(f"split: the armpit (z {pit:.3f}) reaches the chest line (z {chest:.3f})")
    ids = ("torso", "legs", "head", "arms", "chest")
    tag = h.data.attributes.new("_part", "INT", "FACE")      # survives separate(), unlike indices
    tag.data.foreach_set("value", [ids.index(x) for x in label])
    parts = {}
    for name in ("head", "arms", "legs", "chest"):
        bpy.ops.object.select_all(action="DESELECT")
        h.select_set(True)
        bpy.context.view_layer.objects.active = h
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_mode(type="FACE")
        bpy.ops.mesh.select_all(action="DESELECT")
        bpy.ops.object.mode_set(mode="OBJECT")
        tag = h.data.attributes["_part"].data
        for p in h.data.polygons:
            p.select = tag[p.index].value == ids.index(name)
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.separate(type="SELECTED")
        bpy.ops.object.mode_set(mode="OBJECT")
        new = [o for o in bpy.context.selected_objects if o != h][0]
        new.name = name
        new.data.name = name
        parts[name] = new
    h.name = "torso"
    h.data.name = "torso"
    parts["torso"] = h
    for o in parts.values():
        o.data.attributes.remove(o.data.attributes["_part"])
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.shade_smooth()
    # `chest` closes its neck and shoulders and stays OPEN at the chest
    # line (remote players see the torso beneath it; the local player hides
    # the chest). `torso` is capped at the chest line -- the solid suit face
    # the local player looks down at -- and OPEN at the hips (the legs are
    # drawn beneath it). `legs` is open at the waist, as before.
    n0 = {n: len(o.data.polygons) for n, o in parts.items()}
    print("caps", cap(parts["chest"], open_at=(chest,)), "tris on chest,",
          cap(parts["torso"], open_at=(cut,)), "on torso")
    for n, o in parts.items():
        if n in ("chest", "torso", "legs") and not ACTIVE.get("flat"):
            keep_normals(o, n0[n])
        o.data.attributes.remove(o.data.attributes["_vn"])
    return parts


def keep_normals(obj, n0):
    """Every corner of the part's own faces (index < n0) takes the whole
    body's vertex normal (split()'s `_vn`); a cap face (n0 on) keeps its flat
    normal. Cut apart, a part's rim vertices would average only its own
    faces -- and a decimation sliver left alone on the rim (the grunt's
    shoulder folds) would shade flat, and black. Kept, the parts shade as
    the one body did."""
    me = obj.data
    vn = me.attributes["_vn"].data
    loops = [None] * len(me.loops)
    for p in me.polygons:
        for li in p.loop_indices:
            loops[li] = tuple(vn[me.loops[li].vertex_index].vector) if p.index < n0 else tuple(p.normal)
    me.normals_split_custom_set(loops)


def cap(obj, open_at=()):
    """Close a part's open rings -- where split() cut the other parts off --
    with undersuit faces, so no view of a body drawn whole looks into a
    hollow cut. A ring that runs round the body (spans x = 0) within 6 cm
    of a height in `open_at` is left open: the part beneath or above it is
    drawn there. The cap's vertices are the ring's own, so it carries the
    ring's weights. Returns the tris added. (Coincident boundary vertices
    are welded first: UBC's imported mesh is split at its UV seams, which
    breaks each ring into pieces.)"""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    ring = list({v for e in bm.edges if e.is_boundary for v in e.verts})
    bmesh.ops.remove_doubles(bm, verts=ring, dist=1e-5)
    # Walk each hole by its faces' half-edges (rotating round the vertex to
    # the next open edge), so two rings that touch at a vertex stay two.
    rim = [e for e in bm.edges if e.is_boundary]
    todo = {e.link_loops[0] for e in rim}
    new = []
    kept = set()

    def fill(cycle):
        # The hole runs against its faces' winding: reversed, the cap's normal
        # points out of the part like theirs. (A 3-vertex hole can be the
        # back of a lone torso triangle: no cap.)
        if len(cycle) < 3 or bm.faces.get(cycle):
            return
        xs = [v.co.x for v in cycle]
        mz = sum(v.co.z for v in cycle) / len(cycle)
        if min(xs) < 0 < max(xs) and any(abs(mz - z) < 0.06 for z in open_at):
            kept.update(cycle)
            return
        new.append(bm.faces.new(list(reversed(cycle))))

    while todo:
        start = lp = todo.pop()
        ring = []
        while True:
            ring.append(lp.vert)
            nxt = lp.link_loop_next
            for _ in range(64):
                if nxt.edge.is_boundary:
                    break
                nxt = nxt.link_loop_radial_next.link_loop_next
            else:
                raise SystemExit(f"cap: no open edge round vertex {lp.link_loop_next.vert.index}")
            if nxt is start or len(ring) > len(bm.verts):
                break
            todo.discard(nxt)
            lp = nxt
        # A ring that touches itself is several holes: one face per simple cycle.
        path = []
        for v in ring:
            if v in path:
                i = path.index(v)
                fill(path[i:])
                path = path[:i]
            path.append(v)
        fill(path)
    new = bmesh.ops.triangulate(bm, faces=new, quad_method="BEAUTY", ngon_method="BEAUTY")["faces"]
    suit = next(i for i, m in enumerate(obj.data.materials) if m and m.name == "suit")
    for f in new:
        f.material_index = suit
        f.smooth = True
    # A sharp rim: smooth across it, the cap took the torso's sideways
    # vertex normals and shaded near-black from above.
    for e in rim:
        if not (set(e.verts) & kept):
            e.smooth = False
    bm.to_mesh(obj.data)
    bm.free()
    return len(new)


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
    return k, palm_normal(rig, side)


def palm_normal(rig, side):
    """World palm normal. MakeHuman: the hand bone's +Z (every grip was
    tuned on it; the knuckle row is ~40 degrees off it). UBC's hand axes
    differ, so there it comes from the knuckle row: a right hand, fingers
    forward with the index toward -X (thumb side, toward the body), palm
    down, has knuckles x (index - pinky) = +Z, so the palm is its negative;
    a left hand is the mirror."""
    update()
    if not ACTIVE.get("ubc"):
        return (pb(rig, "hand_" + side).matrix.to_3x3() @ Vector((0, 0, 1))).normalized()
    h = pb(rig, "hand_" + side).matrix.translation
    k = pb(rig, "middle_01_" + side).matrix.translation - h
    v = pb(rig, "index_01_" + side).matrix.translation - pb(rig, "pinky_01_" + side).matrix.translation
    n = k.cross(v).normalized()
    return -n if side == "r" else n


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
    palm = palm_normal(rig, side)
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
    palm = palm_normal(rig, side)
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
# Seated: the body drops this far (the client's Entities.SitDrop), and the
# rover's wheel rim relative to the seated eye (gen_props.py rover()).
SIT_DROP = 0.45
WHEEL_R, WHEEL_AHEAD, WHEEL_BELOW = 0.17, 0.30, 0.40

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

# ---- melee -------------------------------------------------------------------
# A hand weapon sits in the fist with its blade (haft) out of the THUMB side
# and its edge toward the KNUCKLES. Both read off the bones the same way here
# and in the client (EntityViews.BladeFrame): blade = index_01 - pinky_01
# (the knuckle row, thumb side), made square to knuckles = middle_01 - hand.
# Poses below say where the fist goes and where the blade/edge point; the
# client hangs the weapon on hand.r along that frame, so the clip IS the hold.
MELEE_SPACING = 0.18   # a two-hander: the left fist this far down the haft (rpg_items.py fore)


def blade_frame(rig, side):
    """(blade, knuckles), world: the fist's blade axis and edge direction."""
    update()
    P = lambda n: pb(rig, n + "_" + side).matrix.translation
    k = (P("middle_01") - P("hand")).normalized()
    row = P("index_01") - P("pinky_01")
    return (row - k * row.dot(k)).normalized(), k


def edge_for(blade):
    """The default edge: forward (+Y), square to the blade; down for a blade
    that itself points forward."""
    a = Vector(blade).normalized()
    e = Vector((0, 1, 0)) - a * a.y
    if e.length < 0.3:
        e = Vector((0, 0, -1)) - a * -a.z
    return e.normalized()


def grip_blade(rig, side, want, blade, edge, pole):
    """Close a fist on `want` with the blade along `blade` and the edge along
    `edge` (world): grip()'s loop, turning the hand by its knuckle row."""
    A, E = Vector(blade).normalized(), Vector(edge).normalized()
    target = Vector(want) - E * 0.07
    for _ in range(5):
        rest_fingers(rig, side)
        reach(rig, side, target, pole)
        a0, k0 = blade_frame(rig, side)
        rotate_about_head(rig, "hand_" + side, (frame(E, A) @ frame(k0, a0).inverted()).to_quaternion())
        curl_each(rig, side, (85, 90, 90, 90), 55)
        miss = Vector(want) - mount_at(rig, side)
        if miss.length < 0.002:
            break
        target += miss


def melee1(rig, G, forward, edge=None, pole_r=(0.7, -0.3, -0.6)):
    """One hand on the hilt at G, blade along `forward`; the left hand loose
    at the hip, a fist."""
    grip_blade(rig, "r", G, forward, edge if edge is not None else edge_for(forward), pole_r)
    reach(rig, "l", (-0.24, 0.16, 1.02), (-0.6, -0.2, -0.8), hand_dir=(0.1, 0.5, -0.85))
    curl(rig, "l", CURL * 1.6, thumb=CURL * 0.4)


def melee2(rig, G, forward, edge=None, pole_r=(0.7, -0.3, -0.6), pole_l=(-0.7, -0.2, -0.7)):
    """Both fists on the haft: the right at G (nearer the blade), the left
    MELEE_SPACING down toward the pommel, same blade and edge."""
    A = Vector(forward).normalized()
    E = Vector(edge) if edge is not None else edge_for(A)
    grip_blade(rig, "r", G, A, E, pole_r)
    grip_blade(rig, "l", Vector(G) - A * MELEE_SPACING, A, E, pole_l)


def keyed(keys, t):
    """Piecewise-linear pose targets: keys = [(t, G, blade, edge|None), ...]
    -> (G, blade, edge) at t, eased in each span."""
    for (t0, g0, a0, e0), (t1, g1, a1, e1) in zip(keys, keys[1:]):
        if t <= t1:
            x = 0.0 if t1 <= t0 else max(0.0, min(1.0, (t - t0) / (t1 - t0)))
            x = x * x * (3 - 2 * x)
            a = Vector(a0).normalized().lerp(Vector(a1).normalized(), x).normalized()
            e0 = Vector(e0) if e0 is not None else edge_for(a0)
            e1 = Vector(e1) if e1 is not None else edge_for(a1)
            e = e0.lerp(e1, x)
            e = (e - a * e.dot(a)).normalized()
            return Vector(g0).lerp(Vector(g1), x), a, e
    _, g, a, e = keys[-1]
    return Vector(g), Vector(a).normalized(), (Vector(e) if e is not None else edge_for(a))


M1_AIM3P = dict(G=(0.24, 0.28, 1.05), forward=(0.05, 0.55, 0.83))      # blade up and forward at the hip
M1_FP = dict(G=(0.20, 0.38, 1.12), forward=(0.48, -0.50, 0.72))   # first person: upright on screen, leaning right, clear of the crosshair (tipped back: the view pulls a forward lean left)
M1_LOWER = dict(G=(0.20, 0.24, 1.00), forward=(0.0, 0.85, -0.5))
M2_AIM3P = dict(G=(0.16, 0.30, 1.08), forward=(-0.25, 0.45, 0.86))      # two-hander held up across the body
M2_FP = dict(G=(0.16, 0.38, 1.16), forward=(0.44, -0.50, 0.74))
M2_LOWER = dict(G=(0.14, 0.26, 0.98), forward=(-0.2, 0.8, -0.55))

# Swings: (t, fist, blade, edge). The edge leads the motion. Third person
# twists the chest with it (TWIST, degrees, negative = turned right).
SWING1 = [(0.0, *M1_AIM3P.values(), None),
          (0.30, (0.32, 0.00, 1.55), (0.2, -0.5, 0.84), (0.0, 0.86, 0.5)),
          (0.55, (0.00, 0.48, 1.25), (-0.75, 0.65, 0.1), (-0.4, -0.45, -0.8)),
          (0.78, (-0.22, 0.32, 0.95), (-0.55, 0.1, -0.83), (-0.6, -0.6, 0.35)),
          (1.0, *M1_AIM3P.values(), None)]
SWING2 = [(0.0, *M2_AIM3P.values(), None),
          (0.35, (0.30, -0.02, 1.60), (0.35, -0.55, 0.75), (0.1, 0.8, 0.55)),
          (0.60, (0.02, 0.50, 1.15), (-0.55, 0.8, -0.2), (-0.3, 0.0, -0.95)),
          (0.82, (-0.18, 0.30, 0.88), (-0.5, 0.2, -0.85), (-0.7, -0.6, 0.3)),
          (1.0, *M2_AIM3P.values(), None)]
SPIN = [(0.0, *M2_AIM3P.values(), None),                     # out to the right, flat, then all the way round
        (0.15, (0.30, 0.30, 1.18), (0.80, 0.55, 0.20), (-0.5, 0.75, 0.0)),
        (0.85, (0.30, 0.30, 1.18), (0.80, 0.55, 0.20), (-0.5, 0.75, 0.0)),
        (1.0, *M2_AIM3P.values(), None)]
FP_SWING1 = [(0.0, *M1_FP.values(), None),
             (0.25, (0.30, 0.20, 1.55), (0.15, -0.2, 0.97), (0.0, 0.98, 0.2)),
             (0.55, (0.00, 0.50, 1.35), (-0.85, 0.5, 0.1), (-0.3, -0.35, -0.9)),
             (0.80, (-0.20, 0.36, 1.05), (-0.5, 0.3, -0.8), (-0.7, -0.5, 0.25)),
             (1.0, *M1_FP.values(), None)]
FP_SWING2 = [(0.0, *M2_FP.values(), None),
             (0.30, (0.28, 0.16, 1.58), (0.3, -0.45, 0.84), (0.1, 0.85, 0.5)),
             (0.58, (0.02, 0.52, 1.25), (-0.5, 0.85, -0.1), (-0.3, 0.1, -0.95)),
             (0.82, (-0.16, 0.34, 0.95), (-0.5, 0.25, -0.83), (-0.7, -0.6, 0.3)),
             (1.0, *M2_FP.values(), None)]
FP_SPIN = [(0.0, *M2_FP.values(), None),                     # first person: a wide flat sweep right to left
           (0.30, (0.34, 0.22, 1.30), (0.9, 0.3, 0.3), (-0.3, 0.95, 0.0)),
           (0.60, (0.00, 0.52, 1.28), (-0.2, 1.0, 0.05), (-0.98, 0.2, 0.0)),
           (0.82, (-0.30, 0.30, 1.22), (-0.9, 0.1, 0.2), (-0.1, -0.98, 0.0)),
           (1.0, *M2_FP.values(), None)]

# Weapon classes: a clip-name suffix, the hold, its targets and where its
# magazine is (reload). The client picks the suffix from the weapon's def.
# A melee class has swings instead of fire/reload/ads.
CLASSES = {
    "": dict(hold=rifle, aim3p=AIM3P, fp=FP_HOLD, ads=FP_ADS, lower=FP_LOWER,
             fore=(FORE_ALONG, FORE_UP), mag=(0.12, -0.04)),
    "_pistol": dict(hold=pistol, aim3p=P_AIM3P, fp=P_FP_HOLD, ads=P_FP_ADS, lower=P_FP_LOWER,
                    fore=PISTOL_FORE, mag=(0.0, -0.09)),
    "_melee": dict(hold=melee1, aim3p=M1_AIM3P, fp=M1_FP, ads=M1_FP, lower=M1_LOWER,
                   melee={"attack": (SWING1, 0.55), "fp_attack": (FP_SWING1, 0.45)}),
    "_melee2h": dict(hold=melee2, aim3p=M2_AIM3P, fp=M2_FP, ads=M2_FP, lower=M2_LOWER,
                     melee={"attack": (SWING2, 0.85), "fp_attack": (FP_SWING2, 0.75),
                            "attack_spin": (SPIN, 1.0), "fp_attack_spin": (FP_SPIN, 0.8)}),
}
TWIST = {"attack": 25, "attack_spin": 0}


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


def quat_continuity(act):
    """Flip each quaternion key into the previous key's hemisphere: a pose
    past 180 degrees (a spin) decomposes to the opposite sign, and the
    curves then interpolate the long way round between two near keys."""
    curves = {}
    for fc in act.fcurves:
        if fc.data_path.endswith("rotation_quaternion"):
            curves.setdefault(fc.data_path, [None] * 4)[fc.array_index] = fc
    for fcs in curves.values():
        if None in fcs:
            continue
        n = len(fcs[0].keyframe_points)
        for i in range(1, n):
            prev = [fc.keyframe_points[i - 1].co[1] for fc in fcs]
            cur = [fc.keyframe_points[i].co[1] for fc in fcs]
            if sum(a * b for a, b in zip(prev, cur)) < 0:
                for fc in fcs:
                    kp = fc.keyframe_points[i]
                    kp.co[1] = -kp.co[1]
                    kp.handle_left[1] = -kp.handle_left[1]
                    kp.handle_right[1] = -kp.handle_right[1]
        for fc in fcs:
            fc.update()


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

    # Seated (playtest 2026-10-02: drivers stood on the roof, then vanished).
    # Thighs level, shins down, the whole body dropped SIT_DROP so the eye
    # sits SIT_DROP under EYE -- the client lines that eye up on the seat's
    # eye mount. `sit_drive` puts both fists on the rover's rim (the wheel
    # sits 0.30 ahead of and 0.40 under the driver's eye, rim radius 0.17:
    # tools/gen_props.py rover()); `sit_armed` is a passenger at the low
    # ready; `sit` rests the forearms on the thighs.
    def seat_legs():
        for side in ("r", "l"):
            swing(rig, "thigh_" + side, (1, 0, 0), 88)
            swing(rig, "calf_" + side, (1, 0, 0), -84)
        drop(SIT_DROP)

    def rest_hands():
        arms_down(rig)
        for side, sx in (("r", 1), ("l", -1)):
            aim(rig, "lowerarm_" + side, (0.10 * sx, 0.90, -0.40))
            aim(rig, "hand_" + side, (0.05 * sx, 0.95, -0.30))

    eye_seated = ACTIVE.get("eye", EYE) - SIT_DROP
    clip(rig, "sit", 2.0, lambda t: (seat_legs(), rest_hands(), breathe(t)), Q)

    def on_wheel(t):
        seat_legs()
        breathe(t)
        for side, sx in (("r", 1), ("l", -1)):
            rim = (sx * WHEEL_R, WHEEL_AHEAD, eye_seated - WHEEL_BELOW)
            grip(rig, side, rim, (0.0, 0.35, 0.94), (-sx, 0.0, 0.0), (sx * 0.7, -0.1, -0.7),
                 fingers=(70, 75, 75, 75), thumb=30)
    clip(rig, "sit_drive", 2.0, on_wheel, Q)
    for sfx, C in CLASSES.items():
        a3 = C["aim3p"]
        held = dict(G=(a3["G"][0], a3["G"][1], a3["G"][2] - SIT_DROP), forward=a3["forward"])
        clip(rig, "sit_armed" + sfx, 2.0, lambda t, C=C, h=held: (seat_legs(), breathe(t), C["hold"](rig, **h)), Q)

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

        if "melee" in C:
            continue

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

    # Melee swings. Third person twists the chest with the blade (a spin
    # turns the whole body round once); first person only moves the arms.
    for sfx, C in CLASSES.items():
        for name, (keys, secs) in C.get("melee", {}).items():
            fp = name.startswith("fp_")
            spin = name.endswith("spin")

            def swing_at(t, keys=keys, hold=C["hold"], fp=fp, spin=spin):
                G, A, E = keyed(keys, t)
                if not fp and not spin:
                    tw = 25 * math.sin(math.pi * t) * (1 if t > 0.4 else -0.8)
                    swing(rig, "spine_02", (0, 0, 1), tw * 0.5)
                    swing(rig, "spine_03", (0, 0, 1), tw * 0.5)
                if spin and not fp:
                    k = max(0.0, min(1.0, (t - 0.15) / 0.7))
                    swing(rig, "Root", (0, 0, 1), -360 * k * k * (3 - 2 * k))
                hold(rig, G, A, E)
            n = 16 if spin else 10
            clip(rig, name + sfx, secs, swing_at, tuple(i / n for i in range(n + 1)), loop=False)
            quat_continuity(bpy.data.actions[name + sfx])

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
    parts = split(h, rig)
    if ACTIVE.get("lod"):
        skinpaint.finish_glove(material("glove"), TEX_DIR, ACTIVE["_id"])
        skinpaint.finish(parts["head"], material("skin"), TEX_DIR, ACTIVE["_id"])
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
    for o in bpy.context.scene.objects:
        if o.type == "MESH":
            textures.uv_box(o)
    bpy.ops.export_scene.gltf(
        filepath=out, export_format="GLB", export_apply=True, export_yup=True,
        export_animations=True, export_animation_mode="ACTIONS", export_force_sampling=True,
        export_skins=True, export_def_bones=False, export_texcoords=True,
        export_normals=True, export_tangents=bool(ACTIVE.get("lod")), export_materials="EXPORT", export_image_format="AUTO",
    )


def main():
    # `-- npc.grunt npc.gunner`: build only those; default is every variant.
    want = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else list(VARIANTS)
    for asset_id in want:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        ACTIVE.clear()
        ACTIVE.update(VARIANTS[asset_id])
        ACTIVE["_id"] = asset_id
        rig, parts = build()
        tris = {n: sum(len(p.vertices) - 2 for p in o.data.polygons) for n, o in parts.items()}
        out = os.path.join(ART, "build", asset_id + ".raw.glb")
        export(out)
        print("wrote", out, tris, "total", sum(tris.values()))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
