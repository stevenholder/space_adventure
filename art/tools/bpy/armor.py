#!/usr/bin/env -S blender -b --python
"""Generate the Scout armor set (and the iron breastplate) for the MakeHuman
body -- build/<id>.raw.glb -- on the same 53-bone rig as tools/bpy/human.py.

    blender -b --python tools/bpy/armor.py          (run from art/)

Star Citizen-style hard surface at modest cost: every piece is made of
PANELS cut from the UNDECIMATED body (its quads follow the anatomy, so a
region's outline is clean, and the outline is relaxed further), pushed out
from the body, given real thickness with a darker rim (Solidify's rim
material is the panel line), bevelled, decimated to budget. Hard panels ride
ONE bone each and never deform -- plates on a bent arm overlap or gap like
real plates, they do not stretch. Gloves and boots are the exception: they
bend with the body, so they keep its weights.

Slots (server/data/items.json):
  armor.helmet.scout  head   shell + visor; covers the head's hair
  armor.suit.scout    chest  chest/back plates, ab plates, pauldrons, bracers
  armor.plate.iron    chest  heavy breastplate and backplate
  armor.legs.scout    legs   belt with pouches, thigh plates, knee pads, shin guards
  armor.gloves.scout  hands  gloves (skinned) with knuckle plates; covers bare hands
  armor.boots.scout   feet   boots (skinned) with toe caps; covers the suit's feet
  pack.scout          back   a pack on the back plate
"""
import importlib.util
import math
import os
import sys

import bpy
import bmesh
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("human", os.path.join(HERE, "human.py"))
human = importlib.util.module_from_spec(spec)
spec.loader.exec_module(human)

ART = human.ART
EYE = human.EYE

# name: (base colour, roughness, metallic)
MATERIALS = {
    "plate":  ((0.42, 0.46, 0.33), 0.55, 0.15),   # olive composite
    "trim":   ((0.16, 0.17, 0.19), 0.65, 0.2),    # rims, panel lines
    "metal":  ((0.32, 0.33, 0.35), 0.35, 0.85),   # fittings
    "visor":  ((0.10, 0.22, 0.28), 0.06, 0.6),
    "strap":  ((0.25, 0.21, 0.16), 0.9, 0.0),     # webbing, pouches
    "iron":   ((0.42, 0.43, 0.45), 0.4, 0.9),
    "glove":  ((0.18, 0.18, 0.20), 0.8, 0.0),
    "boot":   ((0.16, 0.15, 0.14), 0.85, 0.0),
}
ORDER = list(MATERIALS)


def material(name):
    m = bpy.data.materials.get("armor_" + name)
    if m is None:
        col, rough, metal = MATERIALS[name]
        m = bpy.data.materials.new("armor_" + name)
        m.use_nodes = True
        b = m.node_tree.nodes["Principled BSDF"]
        lin = tuple(c ** 2.2 for c in col)      # the palette is written as display (sRGB) colour; glTF stores linear
        b.inputs["Base Color"].default_value = (*lin, 1.0)
        b.inputs["Roughness"].default_value = rough
        b.inputs["Metallic"].default_value = metal
        m.diffuse_color = (*lin, 1.0)
        if name in SURFACE:
            human.textures.dress(m, SURFACE[name], col)
    return m


# Materials that get a generated surface texture (tools/bpy/textures.py).
SURFACE = {"plate": "plate", "trim": "trim", "metal": "metal", "iron": "iron",
           "strap": "fabric", "glove": "fabric", "boot": "fabric"}


def setup_materials(obj, main, rim):
    obj.data.materials.clear()
    obj.data.materials.append(material(main))
    obj.data.materials.append(material(rim))


# ---- panels --------------------------------------------------------------------
class Body:
    """The undecimated body, its dominant bone per face, and landmarks."""

    def __init__(self, obj, rig):
        self.obj = obj
        self.rig = rig
        dom = human.dominant_bones(obj)
        self.face_bone = []
        for p in obj.data.polygons:
            bones = [dom[v] for v in p.vertices]
            self.face_bone.append(max(set(bones), key=bones.count))

    def bone(self, name):
        b = self.rig.data.bones[name]
        return b.head_local.copy(), b.tail_local.copy()

    def faces(self, pred):
        return [p.index for p in self.obj.data.polygons if pred(p.center, self.face_bone[p.index] or "")]


def cut(body, faces, name):
    """A copy of the body holding only `faces` (vertex groups kept)."""
    me = body.obj.data.copy()
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    # Weights live in the mesh, their NAMES on the object: same order, same names.
    for g in body.obj.vertex_groups:
        obj.vertex_groups.new(name=g.name)
    keep = set(faces)
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.index not in keep], context="FACES")
    loose = [v for v in bm.verts if not v.link_faces]
    bmesh.ops.delete(bm, geom=loose, context="VERTS")
    bm.to_mesh(me)
    bm.free()
    return obj


def relax_outline(obj, iterations=6):
    """Smooth the panel's outline: boundary vertices move toward the average
    of their boundary neighbours. Turns a stair-stepped quad edge into a
    clean curve."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    for _ in range(iterations):
        moves = {}
        for v in bm.verts:
            if not v.is_boundary:
                continue
            ns = [e.other_vert(v) for e in v.link_edges if e.is_boundary]
            if len(ns) == 2:
                moves[v] = (ns[0].co + ns[1].co) * 0.5 * 0.6 + v.co * 0.4
        for v, co in moves.items():
            v.co = co
    bm.to_mesh(obj.data)
    bm.free()


def mould(obj, repeat):
    """Smooth the sheet: a moulded plate does not follow every muscle (the
    chest plate followed the pecs). Laplacian-style, outline included."""
    if repeat <= 0:
        return
    bpy.context.view_layer.objects.active = obj
    sm = obj.modifiers.new("mould", "SMOOTH")
    sm.factor = 0.6
    sm.iterations = repeat
    bpy.ops.object.modifier_apply(modifier=sm.name)


def push(obj, offset):
    """Move every vertex out along its normal."""
    obj.data.update()
    for v in obj.data.vertices:
        v.co = v.co + v.normal * offset


def reduce(obj, tris):
    """Bring a flat sheet near budget BEFORE it gets thickness (decimating a
    solid collapses its thin rim into spikes). Collapse only: un-subdividing
    an irregular region of the body tore it."""
    bpy.context.view_layer.objects.active = obj
    est = sum(len(p.vertices) - 2 for p in obj.data.polygons) * 2.2   # two sheets + rim
    if est > tris:
        dc = obj.modifiers.new("dec", "DECIMATE")
        dc.ratio = tris / est
        bpy.ops.object.modifier_apply(modifier=dc.name)


def finish(obj, thickness, bevel):
    """Thickness with a darker rim, bevelled edges, smooth with hard edges
    past 35 degrees."""
    bpy.context.view_layer.objects.active = obj
    so = obj.modifiers.new("solid", "SOLIDIFY")
    so.thickness = thickness
    so.offset = 1.0                 # grow outward from the pushed surface
    so.use_rim = True
    so.material_offset_rim = 1      # the rim draws in material 1 (trim)
    so.use_even_offset = False      # even offset explodes at sharp corners (toes, fingers)
    bpy.ops.object.modifier_apply(modifier=so.name)
    if bevel > 0:
        bv = obj.modifiers.new("bevel", "BEVEL")
        bv.width = bevel
        bv.segments = 1
        bv.limit_method = "ANGLE"
        bv.angle_limit = math.radians(50)
        bpy.ops.object.modifier_apply(modifier=bv.name)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35))


def rigid(obj, bone):
    for g in list(obj.vertex_groups):
        obj.vertex_groups.remove(g)
    g = obj.vertex_groups.new(name=bone)
    g.add([v.index for v in obj.data.vertices], 1.0, "REPLACE")
    obj["bone"] = bone


def panel(body, name, pred, offset, thickness, mat="plate", rim="trim", bone=None,
          bevel=0.003, tris=600, relax=10, smooth=8, unsub=0):
    """One armor panel. `bone` = rigid on that bone; None = keep body weights.
    `unsub` > 0 reduces by un-subdividing the quad grid instead of collapsing
    edges: for skinned pieces over fingers, where a collapse bridges two
    fingers and the bridge stretches into a spike when they curl apart."""
    faces = body.faces(pred)
    if not faces:
        raise SystemExit(f"{name}: region selected no faces")
    obj = cut(body, faces, name)
    if unsub:
        bpy.context.view_layer.objects.active = obj
        dc = obj.modifiers.new("unsub", "DECIMATE")
        dc.decimate_type = "UNSUBDIV"
        dc.iterations = unsub
        bpy.ops.object.modifier_apply(modifier=dc.name)
    else:
        reduce(obj, tris)
    relax_outline(obj, relax)
    mould(obj, smooth)
    push(obj, offset)
    setup_materials(obj, mat, rim)
    for p in obj.data.polygons:
        p.material_index = 0
    finish(obj, thickness, bevel)
    print(f"PANEL {name}: {len(faces)} faces -> {sum(len(p.vertices) - 2 for p in obj.data.polygons)} tris")
    if bone:
        rigid(obj, bone)
    return obj


def smoothed_body(body):
    """A copy of the body with the anatomy smoothed off: what hard plates
    are wrapped onto. Nipples, pecs and knuckles do not show through armor."""
    me = body.obj.data.copy()
    obj = bpy.data.objects.new("smooth_body", me)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    sm = obj.modifiers.new("sm", "SMOOTH")
    sm.factor = 0.8
    sm.iterations = 12
    bpy.ops.object.modifier_apply(modifier=sm.name)
    obj.hide_set(True)
    return obj


def plate(name, wrap_onto, a, b, ref, theta, t, gap, thickness, bone, mat="plate", rim="trim",
          cols=9, rows=7, squareness=4.0, bevel=0.003, relax=4, start=None):
    """A hard plate: a grid laid on a cylinder around the axis a->b, angles
    `theta` (degrees, 0 = `ref`) by fractions `t` along the axis (may run
    past 0..1), masked to a rounded rectangle (|u|^p + |v|^p <= 1), shrink-
    wrapped `gap` metres outside the smoothed body, given thickness, rigid
    on `bone`. Clean outline and clean topology by construction."""
    a, b = Vector(a), Vector(b)
    d = (b - a)
    length = d.length
    d.normalize()
    f0 = Vector(ref) - d * Vector(ref).dot(d)
    f0.normalize()
    sx = d.cross(f0)
    # Start just outside the body: torso plates from 0.30 m, limb plates
    # from 0.15 m off the bone, so every inward ray meets the right surface.
    r0 = start if start is not None else (0.30 if length > 0.4 else 0.15)
    verts, faces = [], []
    for j in range(rows + 1):
        tt = t[0] + (t[1] - t[0]) * j / rows
        for i in range(cols + 1):
            th = math.radians(theta[0] + (theta[1] - theta[0]) * i / cols)
            radial = f0 * math.cos(th) + sx * math.sin(th)
            verts.append(tuple(a + d * (tt * length) + radial * r0))
    for j in range(rows):
        for i in range(cols):
            u = ((i + 0.5) / cols) * 2 - 1
            v = ((j + 0.5) / rows) * 2 - 1
            if abs(u) ** squareness + abs(v) ** squareness > 1.0:
                continue
            k = j * (cols + 1) + i
            faces.append((k, k + 1, k + cols + 2, k + cols + 1))
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    # Faces must point away from the axis for "outside" to mean outside.
    me.update()
    centre = a + d * (sum(t) / 2 * length)
    flip = sum((p.center - centre).dot(p.normal) for p in me.polygons) < 0
    if flip:
        for p in me.polygons:
            p.flip()
    bpy.context.view_layer.objects.active = obj
    sw = obj.modifiers.new("wrap", "SHRINKWRAP")
    sw.target = wrap_onto
    # Straight in along each vertex's normal (radial from the axis): the
    # nearest-point mode dragged the chest plate's top onto the neck.
    sw.wrap_method = "PROJECT"
    sw.use_negative_direction = True
    sw.use_positive_direction = False
    sw.use_project_x = sw.use_project_y = sw.use_project_z = False
    sw.wrap_mode = "OUTSIDE_SURFACE"
    sw.offset = gap
    bpy.ops.object.modifier_apply(modifier=sw.name)
    relax_outline(obj, relax)
    sm = obj.modifiers.new("sm", "SMOOTH")
    sm.factor = 0.5
    sm.iterations = 3
    bpy.ops.object.modifier_apply(modifier=sm.name)
    setup_materials(obj, mat, rim)
    finish(obj, thickness, bevel)
    rigid(obj, bone)
    print(f"PLATE {name}: {sum(len(p.vertices) - 2 for p in obj.data.polygons)} tris")
    return obj


def block(name, center, size, mat, bone, bevel=0.006, rot=(0, 0, 0)):
    """A bevelled box (pouches, pack) riding one bone."""
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=center, rotation=rot)
    obj = bpy.context.object
    obj.name = name
    obj.scale = Vector(size)
    bpy.ops.object.transform_apply(scale=True, rotation=True, location=False)
    obj.data.transform(Matrix.Translation(Vector(center)))
    obj.location = (0, 0, 0)
    setup_materials(obj, mat, "trim")
    bv = obj.modifiers.new("bevel", "BEVEL")
    bv.width = bevel
    bv.segments = 2
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=bv.name)
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35))
    rigid(obj, bone)
    return obj


def boot_foot(name, lo, hi, bone):
    """A boot's foot: a box over the foot's bounds (+1.5 cm all round),
    rounded hard by a bevel, the toe end pulled down into a toe box, on a
    darker sole slab."""
    pad = Vector((0.015, 0.015, 0.012))
    a, b = lo - pad, hi + pad
    a.z = 0.012                                   # the sole carries the bottom
    centre, size = (a + b) * 0.5, b - a
    bpy.ops.mesh.primitive_cube_add(size=1.0)
    obj = bpy.context.object
    obj.name = name
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=2, use_grid_fill=True)
    for v in bm.verts:
        p = Vector((v.co.x * size.x, v.co.y * size.y, v.co.z * size.z)) + centre
        t = (p.y - a.y) / size.y                  # 0 heel .. 1 toe
        if t > 0.55:                              # the toe box drops toward the tip
            k = (t - 0.55) / 0.45
            p.z = a.z + (p.z - a.z) * (1.0 - 0.55 * k * k)
        v.co = p
    bm.to_mesh(obj.data)
    bm.free()
    setup_materials(obj, "boot", "trim")
    bv = obj.modifiers.new("round", "BEVEL")
    bv.width = 0.025
    bv.segments = 2
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=bv.name)
    sole = block(name + "_sole", (centre.x, centre.y, 0.009), (size.x + 0.008, size.y + 0.012, 0.018), "trim", bone, bevel=0.005)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35))
    rigid(obj, bone)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    sole.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.join()
    return obj


def boot_shaft(name, base, radius, height, bone):
    """A boot's shaft: a slightly tapered tube round the ankle and lower
    shin, with a rolled cuff in the trim colour."""
    bpy.ops.mesh.primitive_cone_add(vertices=12, radius1=radius, radius2=radius * 0.93, depth=height,
                                    end_fill_type="NOTHING", location=(base.x, base.y, base.z + height / 2))
    obj = bpy.context.object
    obj.name = name
    obj.data.transform(obj.matrix_world)
    obj.matrix_world = Matrix.Identity(4)
    setup_materials(obj, "boot", "trim")
    for p in obj.data.polygons:
        p.material_index = 0
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.ops.object.shade_smooth()
    finish(obj, 0.008, 0.002)
    rigid(obj, bone)
    cuff = bpy.ops.mesh.primitive_torus_add(major_radius=radius * 0.95, minor_radius=0.010,
                                            major_segments=12, minor_segments=4,
                                            location=(base.x, base.y, base.z + height))
    cuff = bpy.context.object
    cuff.data.transform(cuff.matrix_world)
    cuff.matrix_world = Matrix.Identity(4)
    setup_materials(cuff, "trim", "trim")
    bpy.ops.object.shade_smooth()
    rigid(cuff, bone)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    cuff.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.join()
    return obj


def side_x(sign, c):
    return c.x * sign > 0


# ---- the pieces --------------------------------------------------------------------
def pieces(body):
    pelvis, _ = body.bone("pelvis")
    s3h, s3t = body.bone("spine_03")
    knee_r, _ = body.bone("calf_r")
    ankle_r, _ = body.bone("foot_r")
    elbow_r, wrist_r = body.bone("lowerarm_r")
    shoulder_r, _ = body.bone("upperarm_r")
    head_c = (Vector(body.bone("head")[0]) + Vector(body.bone("head")[1])) * 0.5

    out = {}

    # Helmet: a smooth shell around the skull (not the face's shape -- a
    # helmet does not follow the nose), open at the neck, with a visor band.
    head_pts = [body.obj.data.vertices[v].co for i in body.faces(lambda c, b: b == "head" and c.z > EYE - 0.12)
                for v in body.obj.data.polygons[i].vertices]
    lo = Vector((min(p.x for p in head_pts), min(p.y for p in head_pts), min(p.z for p in head_pts)))
    hi = Vector((max(p.x for p in head_pts), max(p.y for p in head_pts), max(p.z for p in head_pts)))
    centre = (lo + hi) * 0.5
    radii = (hi - lo) * 0.5 + Vector((0.035, 0.03, 0.03))
    bpy.ops.mesh.primitive_uv_sphere_add(segments=18, ring_count=12, radius=1.0, location=(0, 0, 0))
    hel = bpy.context.object
    hel.name = "helmet"
    hel.data.transform(Matrix.Translation(centre) @ Matrix.Diagonal((radii.x, radii.y, radii.z, 1.0)))
    bm = bmesh.new()
    bm.from_mesh(hel.data)
    cut_at = EYE - 0.115                     # under the jaw at the front, a little lower behind
    doomed = [f for f in bm.faces if f.calc_center_median().z < cut_at - (0.03 if f.calc_center_median().y < centre.y else 0.0)]
    bmesh.ops.delete(bm, geom=doomed, context="FACES")
    bm.to_mesh(hel.data)
    bm.free()
    setup_materials(hel, "plate", "trim")
    reduce(hel, 1100)
    finish(hel, 0.008, 0.002)
    rigid(hel, "head")
    # The visor: a curved strip on the shell's surface, a little proud of it,
    # wrapping 120 degrees across the eyes. Built as its own grid so its
    # edges are straight rows and columns, not the sphere's faces.
    shell_r = radii + Vector((0.012, 0.012, 0.012))
    cols, rows = 12, 2
    verts, faces_ = [], []
    for j in range(rows + 1):
        z = EYE - 0.045 + 0.08 * j / rows
        k = max(0.0, 1.0 - ((z - centre.z) / shell_r.z) ** 2) ** 0.5
        for i in range(cols + 1):
            a = math.radians(-60 + 120 * i / cols)
            verts.append((centre.x + shell_r.x * k * math.sin(a), centre.y + shell_r.y * k * math.cos(a), z))
    for j in range(rows):
        for i in range(cols):
            a = j * (cols + 1) + i
            faces_.append((a, a + 1, a + cols + 2, a + cols + 1))
    me = bpy.data.meshes.new("visor")
    me.from_pydata(verts, [], faces_)
    vis = bpy.data.objects.new("visor", me)
    bpy.context.scene.collection.objects.link(vis)
    setup_materials(vis, "visor", "metal")
    finish(vis, 0.006, 0.0015)
    rigid(vis, "head")
    out["armor.helmet.scout"] = [hel, vis]

    # Hard plates are wrapped onto a smoothed copy of the body.
    sb = smoothed_body(body)
    neck_z = body.bone("neck_01")[0].z
    spine_a = Vector((0, 0.0, pelvis.z))
    spine_b = Vector((0, 0.0, neck_z))
    span = spine_b.z - spine_a.z
    tz = lambda z: (z - spine_a.z) / span            # height -> fraction along the spine
    chest_lo = s3h.z + 0.02

    # Suit (chest slot): chest and back plates, two abdominal plates,
    # pauldrons, forearm bracers.
    suit = [
        plate("chest", sb, spine_a, spine_b, (0, 1, 0), (-62, 62), (tz(chest_lo), tz(neck_z - 0.04)), 0.022, 0.012, "spine_03",
              cols=10, rows=7),
        plate("back", sb, spine_a, spine_b, (0, -1, 0), (-58, 58), (tz(chest_lo - 0.08), tz(neck_z - 0.05)), 0.020, 0.010, "spine_03",
              cols=9, rows=7),
        plate("abs_hi", sb, spine_a, spine_b, (0, 1, 0), (-42, 42), (tz(chest_lo - 0.115), tz(chest_lo - 0.025)), 0.020, 0.009, "spine_02",
              cols=7, rows=3, squareness=6),
        plate("abs_lo", sb, spine_a, spine_b, (0, 1, 0), (-38, 38), (tz(chest_lo - 0.215), tz(chest_lo - 0.125)), 0.018, 0.009, "spine_01",
              cols=7, rows=3, squareness=6),
    ]
    for side, sign in (("r", 1), ("l", -1)):
        sh, el = body.bone("upperarm_" + side)
        lo_h, lo_t = body.bone("lowerarm_" + side)
        out_dir = (sign, 0.0, 0.35)
        suit.append(plate("pauldron_" + side, sb, sh, el, out_dir, (-105, 105), (-0.22, 0.42), 0.024, 0.011, "upperarm_" + side,
                          cols=9, rows=5, squareness=3))
        suit.append(plate("bracer_" + side, sb, lo_h, lo_t, (sign, 0.0, 0.6), (-110, 110), (0.25, 0.85), 0.014, 0.008, "lowerarm_" + side,
                          cols=8, rows=4, squareness=5))
    out["armor.suit.scout"] = suit

    # Iron breastplate: one heavy chest shell front and back.
    out["armor.plate.iron"] = [
        plate("iron_front", sb, spine_a, spine_b, (0, 1, 0), (-75, 75), (tz(chest_lo - 0.13), tz(neck_z - 0.035)), 0.030, 0.016, "spine_03",
              mat="iron", cols=12, rows=8, squareness=5),
        plate("iron_back", sb, spine_a, spine_b, (0, -1, 0), (-70, 70), (tz(chest_lo - 0.13), tz(neck_z - 0.045)), 0.028, 0.014, "spine_03",
              mat="iron", cols=10, rows=8, squareness=5),
    ]

    # Legs: belt with pouches, thigh plates, knee pads, shin guards.
    def belt(c, b):
        return b in ("pelvis", "spine_01") and abs(c.z - (pelvis.z + 0.06)) < 0.035
    legs = [panel(body, "belt", belt, 0.012, 0.008, mat="strap", rim="metal", bone="pelvis", tris=260, bevel=0.0, smooth=2)]
    front_y = max(body.obj.data.polygons[i].center.y for i in body.faces(lambda c, b: b == "pelvis"))
    for sign in (1, -1):
        legs.append(block("pouch", (0.13 * sign, front_y - 0.01, pelvis.z + 0.04), (0.07, 0.04, 0.08), "strap", "pelvis"))
        legs.append(block("pouch_side", (0.17 * sign, 0.0, pelvis.z + 0.03), (0.04, 0.07, 0.08), "strap", "pelvis"))
    for side, sign in (("r", 1), ("l", -1)):
        th_h, th_t = body.bone("thigh_" + side)
        ca_h, ca_t = body.bone("calf_" + side)
        legs.append(plate("thigh_" + side, sb, th_h, th_t, (0.35 * sign, 1, 0), (-80, 80), (0.18, 0.72), 0.018, 0.009, "thigh_" + side,
                          cols=8, rows=5, squareness=4))
        legs.append(plate("knee_" + side, sb, ca_h, ca_t, (0, 1, 0), (-60, 60), (-0.10, 0.10), 0.028, 0.012, "calf_" + side,
                          mat="trim", rim="metal", cols=6, rows=3, squareness=2.5))
        legs.append(plate("shin_" + side, sb, ca_h, ca_t, (0.15 * sign, 1, 0), (-60, 60), (0.18, 0.78), 0.018, 0.009, "calf_" + side,
                          cols=7, rows=5, squareness=5))
    out["armor.legs.scout"] = legs

    # Gloves: the hands (skinned, they bend) plus a rigid knuckle plate.
    gloves = []
    for side in ("r", "l"):
        def hand(c, b, side=side):
            return b.endswith("_" + side) and b.startswith(("hand", "thumb", "index", "middle", "ring", "pinky"))
        # No skinned hand shell: decimated over the fingers it bridged them,
        # and the bridges stretched into spikes when the hand closed on a
        # grip. The suit already has gloves; these are hard pieces over them:
        # a knuckle plate and a wrist cuff, both rigid.
        lo_h, lo_t = body.bone("lowerarm_" + side)
        gloves.append(plate("cuff_" + side, sb, lo_h, lo_t, (1 if side == "r" else -1, 0.0, 0.6), (-178, 178), (0.80, 1.02),
                            0.010, 0.007, "lowerarm_" + side, mat="trim", rim="metal", cols=8, rows=3, squareness=12))
        h_h, h_t = body.bone("hand_" + side)
        sign = 1 if side == "r" else -1
        gloves.append(plate("knuckle_" + side, sb, h_h, h_t, (0.0, 0.3, 1.0), (-70, 70), (0.35, 1.0), 0.010, 0.006, "hand_" + side,
                            mat="trim", rim="metal", cols=6, rows=3, squareness=4))
    out["armor.gloves.scout"] = gloves

    # Boots: built, not cut from the bare foot (toes showed through, and a
    # toe cap on top read as a sandal strap). A rounded foot shell sized to
    # the foot with the toe lowered, a rubber sole, both on the foot bone; a
    # shaft round the ankle on the calf bone, overlapping the shell.
    boots = []
    for side in ("r", "l"):
        pts = [body.obj.data.vertices[v].co for i in body.faces(lambda c, b, side=side: b in ("foot_" + side, "ball_" + side))
               for v in body.obj.data.polygons[i].vertices]
        lo = Vector((min(p.x for p in pts), min(p.y for p in pts), 0.0))
        hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
        boots.append(boot_foot("boot_" + side, lo, hi, "foot_" + side))
        a_h, a_t = body.bone("calf_" + side)
        ank = body.bone("foot_" + side)[0]
        shaft_pts = [body.obj.data.vertices[v].co for i in body.faces(lambda c, b, side=side, z=ank.z: b == "calf_" + side and z < c.z < z + 0.17)
                     for v in body.obj.data.polygons[i].vertices]
        cx = sum((p.x for p in shaft_pts)) / len(shaft_pts)
        cy = sum((p.y for p in shaft_pts)) / len(shaft_pts)
        r = max(((p.x - cx) ** 2 + (p.y - cy) ** 2) ** 0.5 for p in shaft_pts)
        boots.append(boot_shaft("shaft_" + side, Vector((cx, cy, ank.z - 0.02)), r + 0.012, 0.19, "calf_" + side))
    out["armor.boots.scout"] = boots

    # Pack: two bevelled blocks on the back plate.
    back_plate_y = min(body.obj.data.polygons[i].center.y for i in body.faces(lambda c, b: b == "spine_03")) - 0.03
    pz = s3h.z + 0.06
    out["pack.scout"] = [
        block("pack", (0, back_plate_y - 0.10, pz), (0.30, 0.13, 0.36), "plate", "spine_03", bevel=0.012),
        block("pack_lid", (0, back_plate_y - 0.10, pz + 0.19), (0.31, 0.14, 0.05), "trim", "spine_03", bevel=0.01),
        block("pack_cell", (0.0, back_plate_y - 0.175, pz - 0.02), (0.16, 0.03, 0.22), "metal", "spine_03", bevel=0.006),
    ]
    # Decals: stencilled markings painted onto the plates (textures.py atlas).
    def part(asset, name):
        return next(p for p in out[asset] if p.name == name)

    def stick(obj, cell, into, size, offset=(0.0, 0.0)):
        d = human.textures.decal(obj, cell, into, size, offset)
        rigid(d, obj["bone"])
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        d.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.join()

    stick(part("armor.suit.scout", "chest"), 3, (0, -1, 0), 0.075, (0.075, 0.02))     # insignia, wearer's left
    stick(part("armor.suit.scout", "back"), 0, (0, 1, 0), 0.11, (0.0, 0.03))          # squad number
    stick(part("armor.suit.scout", "pauldron_r"), 1, (-1, 0, -0.35), 0.07)             # rank chevrons
    stick(part("armor.suit.scout", "pauldron_l"), 1, (1, 0, -0.35), 0.07)
    stick(part("armor.helmet.scout", "helmet"), 0, (-1, 0, 0), 0.06, (0.0, 0.01))      # number on both sides
    stick(part("armor.helmet.scout", "helmet"), 0, (1, 0, 0), 0.06, (0.0, 0.01))
    stick(part("armor.plate.iron", "iron_front"), 2, (0, -1, 0), 0.10, (0.0, -0.07))  # hazard band
    return out


# ---- build -------------------------------------------------------------------------
def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    h, rig, _ = human.make_human(decimate=False)
    h.data.update()
    body = Body(h, rig)
    built = pieces(body)
    h_name = h.name
    for asset_id, parts in built.items():
        # Hide everything, show this piece and the rig, export, then remove it.
        for o in bpy.context.scene.objects:
            o.hide_set(False)
        for p in parts:
            human.bind(p, rig)
        bpy.ops.object.select_all(action="DESELECT")
        for p in parts:
            p.select_set(True)
        # One mesh per part, named "<bone>__<part>" (skinned parts: "skin__"):
        # the first-person arms keep only the parts on the forearms and
        # hands (ViewModel), so a suit's chest plate and pauldrons never sit
        # in front of the camera.
        for p in parts:
            p.name = f"{p.get('bone', 'skin')}__{p.name}"
            p.data.name = p.name
        for p in parts:
            human.textures.uv_box(p)
        out = os.path.join(ART, "build", asset_id + ".raw.glb")
        bpy.ops.object.select_all(action="DESELECT")
        for p in parts:
            p.select_set(True)
        rig.select_set(True)
        bpy.context.view_layer.objects.active = parts[0]
        bpy.ops.export_scene.gltf(
            filepath=out, export_format="GLB", use_selection=True, export_apply=True, export_yup=True,
            export_animations=False, export_skins=True, export_def_bones=False, export_texcoords=True,
            export_normals=True, export_materials="EXPORT", export_image_format="AUTO",
        )
        tris = sum(sum(len(f.vertices) - 2 for f in p.data.polygons) for p in parts)
        print(f"wrote {out}: {tris} tris in {len(parts)} parts")
        for p in parts:
            bpy.data.objects.remove(p, do_unlink=True)


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
