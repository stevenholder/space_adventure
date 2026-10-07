"""Painted skin and eyes for the Colonist (`lod` variants of human.py).

The UBC face reads because its albedo is painted: lip colour, warm nose
and cheeks, dark lash lines, eye whites, an iris, crevice shading. This
module paints the same things onto the MakeHuman head, from the mesh
itself (nothing from MPFB's GPL masks):

  1. on the FULL-resolution MakeHuman mesh (before decimation), the skin
     faces' MakeHuman UVs are packed into their own square (head, neck front,
     the nape piece cut out of the torso island);
  2. per-vertex masks -- lips (the `lips` group, feathered over the mesh),
     nose and cheeks (soft blobs round landmarks found from the eyes),
     eye sockets, lash lines, ears (the `ears` group), a faint beard shadow
     (male) -- mixed into a colour attribute and baked (Cycles EMIT) into
     SKIN_TEX px, with an ambient-occlusion bake (nostrils, sockets, ear
     folds, lip line) multiplied in;
  3. eyebrows painted per texel (paint_brows): the surface POSITION is baked
     too, so each texel knows where it sits above the eye, and a soft-edged
     stroke (thickness, arch, taper per body: BROW_M / BROW_F) with strand
     noise is mixed in, a faint shadow under it. Then it is written as a JPEG
     and linked as the `skin` material's base colour (keep_uv: uv_box leaves
     these UVs alone). Decimation carries the packed UVs through.

Eyes: the clean eyeball spheres (human.new_eyeballs) get an azimuthal UV
(front pole at the centre) and one EYE_TEX px texture -- pupil, a striated
iris with a limbal ring, white sclera -- on both the `eye` and `sclera`
materials, so the cap/rest split no longer shows; low roughness gives the
highlight.
"""
import math
import os

import bpy
import bmesh
import cycles_gpu  # noqa: E402 -- beside this file; human.py puts tools/bpy on sys.path
import numpy as np
from mathutils import Vector, geometry
from mathutils.bvhtree import BVHTree

SKIN_TEX = 2048      # px; verify.mjs holds body textures to <= 2048
EYE_TEX = 512
QUALITY = 90         # JPEG
AO_DIST = 0.10       # m: crevices, not the whole body's shadow
AO_SAMPLES = 64

STATE = {}           # the composed albedo between paint() and finish()


def srgb_to_lin(c):
    c = np.asarray(c, dtype=np.float64)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def lin_to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


# ---- mesh helpers --------------------------------------------------------------
def _neighbours(me):
    e = np.empty(len(me.edges) * 2, dtype=np.int64)
    me.edges.foreach_get("vertices", e)
    return e.reshape(-1, 2)


def smooth(vals, edges, n, passes):
    """Average each vertex with its edge neighbours `passes` times (feathering)."""
    v = vals.astype(np.float64)
    deg = np.bincount(edges.ravel(), minlength=n).astype(np.float64)
    for _ in range(passes):
        acc = np.zeros(n)
        np.add.at(acc, edges[:, 0], v[edges[:, 1]])
        np.add.at(acc, edges[:, 1], v[edges[:, 0]])
        v = np.where(deg > 0, (v + acc) / (1 + deg), v)
    return v


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def group_weights(obj, name):
    out = np.zeros(len(obj.data.vertices))
    if name not in obj.vertex_groups:
        return out
    gi = obj.vertex_groups[name].index
    for v in obj.data.vertices:
        for g in v.groups:
            if g.group == gi:
                out[v.index] = g.weight
    return out


# ---- UVs ------------------------------------------------------------------------
def pack_skin_uvs(h, skin):
    """Pack the skin faces' UV islands alone into the unit square (the
    other faces' UVs stay put; the glove's are packed by a second call)."""
    bpy.ops.object.select_all(action="DESELECT")
    h.select_set(True)
    bpy.context.view_layer.objects.active = h
    bpy.context.scene.tool_settings.use_uv_select_sync = True
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_mode(type="FACE")
    bm = bmesh.from_edit_mesh(h.data)
    bm.faces.ensure_lookup_table()
    for v in bm.verts:
        v.select = False
    for e in bm.edges:
        e.select = False
    for f in bm.faces:
        f.select = bool(skin[f.index])
    bm.select_flush_mode()
    bmesh.update_edit_mesh(h.data)
    bpy.ops.uv.pack_islands(udim_source="CLOSEST_UDIM", rotate=True, scale=True, margin=0.004, shape_method="CONCAVE")
    bpy.ops.object.mode_set(mode="OBJECT")


# ---- masks ------------------------------------------------------------------------
def masks(h, eyes, R, female, ball):
    """Per-vertex 0..1 masks on the full mesh, final frame (front +Y, Z up)."""
    me = h.data
    n = len(me.vertices)
    co = np.empty(n * 3)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    edges = _neighbours(me)
    el, er = np.array(eyes[0]), np.array(eyes[1])
    mid = (el + er) / 2
    rel = co - mid
    near = lambda p, s: np.exp(-np.sum((co - p) ** 2, axis=1) / (2 * s * s))
    front = rel[:, 1] > -0.02

    # landmarks from the eyes
    band = (np.abs(rel[:, 0]) < 0.008) & (rel[:, 2] < -0.02) & (rel[:, 2] > -0.07)
    nose = co[band][np.argmax(co[band][:, 1])]
    m = {}
    m["lips"] = smoothstep(0.25, 0.85, smooth(group_weights(h, "_lips"), edges, n, 2))
    m["ears"] = smooth(group_weights(h, "_ears"), edges, n, 2)

    def surf(p):            # nearest front vertex to a point
        d = np.sum((co - p) ** 2, axis=1) + (~front) * 1.0
        return co[np.argmin(d)]
    cheeks = [surf(e + np.array([np.sign(e[0] - mid[0]) * 0.012, 0.03, -0.035])) for e in (el, er)]
    # cooler (less red) over the forehead and the chin: skin is not one tone
    m["cool"] = np.clip(0.8 * near(surf(mid + np.array([0, 0.03, 0.045])), 0.022)
                        + 0.6 * near(surf(mid + np.array([0, 0.05, -0.125])), 0.014), 0, 1) * front.clip(0.6)
    # the hollows under the cheekbones and the temples: a shade darker
    m["hollow"] = np.clip(sum(0.8 * near(surf(e + np.array([np.sign(e[0] - mid[0]) * 0.022, 0.0, -0.06])), 0.016)
                              + 0.85 * near(surf(e + np.array([np.sign(e[0] - mid[0]) * 0.035, -0.03, 0.025])), 0.018)
                              for e in (el, er)), 0, 1) * front.clip(0.6)
    m["red"] = np.clip(0.9 * near(nose, 0.011) + sum(0.7 * near(c, 0.018) for c in cheeks)
                       + 0.6 * m["ears"] + 0.25 * near(surf(mid + np.array([0, 0.05, -0.105])), 0.015), 0, 1) * front.clip(0.6)
    d = np.minimum(np.linalg.norm(co - el, axis=1), np.linalg.norm(co - er, axis=1))
    eye_near = np.where(np.linalg.norm(co - el, axis=1) < np.linalg.norm(co - er, axis=1), 0, 1)
    ez = np.where(eye_near == 0, el[2], er[2])
    infront = co[:, 1] > np.where(eye_near == 0, el[1], er[1]) - 0.004
    m["socket"] = (1 - smoothstep(R + 0.003, R + 0.017, d)) * infront
    upper = smoothstep(-0.004, 0.001, co[:, 2] - ez)
    # The lid margin: skin (not eyeball) resting on the ball, inside the
    # front cone; feathered outward over the lid, the upper heavier.
    near_e = np.where((eye_near == 0)[:, None], el, er)
    cosang = (co - near_e)[:, 1] / np.maximum(d, 1e-9)
    rim = (~ball) & (d < R + 0.0025) & (cosang > math.cos(math.radians(60)))
    lash = smooth(rim.astype(float), edges, n, 4 if female else 3) * (~ball)
    lash = np.clip(lash / max(lash.max(), 1e-6) * 3.0, 0, 1)
    m["lash"] = lash * (0.35 + 0.65 * upper)
    # Painted modelling (the Vanguard's face is hand-shaded darker in its
    # hollows): surfaces turned down under the jaw and chin, with the throat
    # below them in their shadow (darkest under the jaw, lighter toward the
    # collar); the upper half of the eye socket under the brow ridge, deeper
    # at the inner corner; the nasolabial fold, nose wing to mouth corner.
    nr = np.empty(n * 3)
    me.vertices.foreach_get("normal", nr)
    nr = nr.reshape(-1, 3)
    nz = nr[:, 2]
    away = (np.linalg.norm(co - nose, axis=1) > 0.022) * (1 - m["lips"])
    under = smoothstep(-0.1, -0.6, nz) * (rel[:, 2] < -0.06) * away
    neck = smoothstep(-0.19, -0.115, rel[:, 2]) * smoothstep(-0.005, -0.03, rel[:, 1]) * (rel[:, 2] < -0.08)
    m["under"] = np.clip(smooth(np.maximum(under, neck), edges, n, 4), 0, 1) * (rel[:, 1] > -0.11)
    # the face turning away from the front toward the ears and the jaw's side
    side = smoothstep(0.6, 0.1, nr[:, 1]) * smoothstep(-0.16, -0.12, rel[:, 2]) * smoothstep(0.05, 0.0, rel[:, 2])
    m["side"] = smooth(side * (rel[:, 1] > -0.07) * (1 - m["ears"]) * away, edges, n, 3)
    # painted top light: the face below the cheekbones a shade darker than the brow
    m["low"] = smoothstep(-0.025, -0.095, rel[:, 2]) * (rel[:, 1] > -0.09) * (1 - 0.6 * m["lips"])
    inner_up = sum(near(e + np.array([-np.sign(e[0] - mid[0]) * 0.008, 0.004, 0.009]), 0.007) for e in (el, er))
    m["orbit"] = np.clip((1 - smoothstep(R + 0.002, R + 0.016, d)) * smoothstep(-0.001, 0.006, co[:, 2] - ez)
                         * (0.6 + 0.6 * inner_up), 0, 1) * infront * (1 - m["lash"])
    # Painted light, as on the Vanguard's hand-painted face: a lit centre
    # panel (brow, nose, upper lip, chin) with the planes darker toward the
    # sides. The crisp accents are painted per texel (accents()) from these
    # landmarks.
    facef = (rel[:, 1] > -0.05) * smoothstep(-0.16, -0.12, rel[:, 2]) * smoothstep(0.075, 0.05, rel[:, 2])
    m["plane"] = smoothstep(0.016, 0.062, np.abs(rel[:, 0])) * facef * (1 - m["ears"])
    # the nose's side walls (turned sideways, along the bridge to the tip)
    top = mid + np.array([0.0, nose[1] - mid[1] - 0.012, -0.012])
    ab = nose - top
    t = np.clip(((co - top) @ ab) / (ab @ ab), 0, 1)
    dseg = np.linalg.norm(co - (top + t[:, None] * ab), axis=1)
    m["noseside"] = smooth(smoothstep(0.25, 0.7, np.abs(nr[:, 0])) * smoothstep(0.02, 0.011, dseg) * front, edges, n, 1)
    lipw = group_weights(h, "_lips") > 0.5
    lc = co[lipw] if lipw.any() else co[[np.argmax(co[:, 1])]]
    m["_lm"] = {"mid": mid, "nose": nose,
                "corners": [lc[np.argmax(lc[:, 0] * sd)] for sd in (1, -1)],
                "low": lc[np.argmin(lc[:, 2] - 0.3 * np.abs(lc[:, 0] - mid[0]))]}
    if female:
        m["beard"] = np.zeros(n)
    else:
        jaw = (rel[:, 2] < -0.05) & (rel[:, 2] > -0.16) & (rel[:, 1] > -0.04) & (np.abs(rel[:, 0]) < 0.065)
        m["beard"] = smooth(jaw.astype(float) * (1 - m["lips"]) * (1 - near(nose, 0.012)), edges, n, 4)
    return m


def paint_colours(m, base, female):
    """Mix the masks into a linear RGB per vertex."""
    lin = lambda c: srgb_to_lin(np.array(c))[None, :]
    col = np.repeat(lin(base), len(m["lips"]), axis=0)
    def mix(c, w):
        nonlocal col
        col = col * (1 - w[:, None]) + lin(c) * w[:, None]
    mix((0.80, 0.49, 0.36), 0.50 * m["red"])           # warm: nose, cheeks, ears
    mix((0.70, 0.56, 0.43), 0.40 * m["cool"])          # a shade less red: forehead, chin
    mix((0.56, 0.42, 0.36), 0.45 * m["socket"])
    mix((0.58, 0.42, 0.32), 0.30 * m["hollow"])
    mix((0.50, 0.45, 0.41), 0.30 * m["beard"])
    mix((0.72, 0.43, 0.37) if female else (0.66, 0.42, 0.35), (0.7 if female else 0.75) * m["lips"])
    # modelling: darker, and warmer as skin shades (MODEL: weight per mask)
    for k, w in MODEL.items():
        col = col * (1 - w * m[k][:, None] * SHADE_TINT)
    mix((0.07, 0.05, 0.05), 0.92 * m["lash"])
    return col


# Painted modelling (masks()): fraction darker at a mask's core.
# Painted modelling: fraction darker at a mask's core -- per vertex
# (masks()) and per texel (accents(); `bridge` is lighter).
MODEL = {"under": 0.58, "side": 0.30, "orbit": 0.50, "hollow": 0.42, "low": 0.15, "plane": 0.20, "noseside": 0.28}
ACCENT = {"naso": 0.40, "alar": 0.45, "sulcus": 0.40, "corners": 0.40, "bridge": -0.12}
SHADE_TINT = np.array([0.82, 1.0, 1.08])   # skin shades warmer


def accents(P, lm, lips):
    """Per-texel masks from the baked surface position P (S, S, 3): the
    nasolabial fold (nose wing to just outside the mouth corner), the nose
    wings and the nostril shadow, the crease under the lower lip, the mouth
    corners, the lit nose bridge."""
    mid, nose = lm["mid"], lm["nose"]
    rel = P - mid
    front = smoothstep(-0.03, -0.01, rel[..., 1])
    near = lambda p, sg: np.exp(-np.sum((P - p) ** 2, axis=-1) / (2 * sg * sg))
    out = {k: np.zeros(P.shape[:2]) for k in ACCENT}
    for sd, corner in zip((1, -1), lm["corners"]):
        a = np.array([mid[0] + sd * 0.017, nose[1] - 0.012, nose[2] - 0.006])     # beside the nose wing
        b = corner + np.array([sd * 0.007, 0.0, -0.004])                          # just outside the mouth corner
        ab = b - a
        t = np.clip(((P - a) @ ab) / (ab @ ab), 0, 1)
        dist = np.linalg.norm(P - (a + t[..., None] * ab), axis=-1)
        out["naso"] = np.maximum(out["naso"], np.exp(-(dist / 0.0036) ** 2) * (0.45 + 0.55 * (1 - t)))
        out["alar"] = np.maximum(out["alar"], near(np.array([mid[0] + sd * 0.0135, nose[1] - 0.013, nose[2] - 0.009]), 0.0035))
        out["corners"] = np.maximum(out["corners"], near(corner + np.array([sd * 0.0015, -0.001, 0.0]), 0.0028))
    out["alar"] = np.maximum(out["alar"], 0.7 * near(nose + np.array([0, -0.010, -0.013]), 0.0035))
    low = lm["low"]
    d = P - np.array([mid[0], low[1] - 0.002, low[2] - 0.0055])
    out["sulcus"] = np.exp(-(d[..., 0] / 0.011) ** 2 - (d[..., 2] / 0.0035) ** 2 - (d[..., 1] / 0.01) ** 2)
    out["bridge"] = np.exp(-(rel[..., 0] / 0.0045) ** 2) * smoothstep(-0.004, -0.012, rel[..., 2]) \
        * smoothstep(-0.05, -0.036, rel[..., 2]) * (rel[..., 1] > 0.0)
    keep = 1 - smoothstep(0.3, 0.7, lips)
    for k in ("naso", "sulcus", "alar"):
        out[k] = out[k] * keep
    return {k: v * front for k, v in out.items()}
AO_WEIGHT = 0.95     # crevice darkening (1 - AO_WEIGHT x occlusion)


# ---- baking -----------------------------------------------------------------------
def _float_image(name, size):
    img = bpy.data.images.get(name)
    if img:
        bpy.data.images.remove(img)
    img = bpy.data.images.new(name, size, size, alpha=False, float_buffer=True)
    img.colorspace_settings.name = "Non-Color"
    return img


def _pixels(img):
    a = np.empty(img.size[0] * img.size[1] * 4, dtype=np.float32)
    img.pixels.foreach_get(a)
    return a.reshape(img.size[1], img.size[0], 4)[..., :3].astype(np.float64)


def bake(h, skin, layers, size=None):
    """EMIT each per-vertex layer ({name: (n, 3) linear}) and AO into
    SKIN_TEX images; returns {name: (S, S, 3)} plus "AO": (S, S)."""
    scene = bpy.context.scene
    prev_engine = scene.render.engine
    scene.render.engine = "CYCLES"
    cycles_gpu.cycles_device(scene)
    if scene.world is None:
        scene.world = bpy.data.worlds.new("bake")
    scene.world.light_settings.distance = AO_DIST

    vi = h.data.attributes.new("_vi", "INT", "POINT")
    vi.data.foreach_set("value", np.arange(len(h.data.vertices), dtype=np.int32))

    # target: the skin faces only; occluder: everything else (eyeballs, suit)
    def part(keep, name):
        o = h.copy()
        o.data = h.data.copy()
        o.name = name
        scene.collection.objects.link(o)
        bm = bmesh.new()
        bm.from_mesh(o.data)
        bm.faces.ensure_lookup_table()
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if bool(skin[f.index]) != keep], context="FACES")
        bm.to_mesh(o.data)
        bm.free()
        return o
    tgt = part(True, "_bake_skin")
    occ = part(False, "_bake_occ")
    h.hide_render = True

    src = np.empty(len(tgt.data.vertices), dtype=np.int32)       # h's vertex index, carried by the copy
    tgt.data.attributes["_vi"].data.foreach_get("value", src)
    for name, colours in layers.items():
        ca = tgt.data.color_attributes.new(name, "FLOAT_COLOR", "POINT")
        data = np.ones((len(tgt.data.vertices), 4))
        data[:, :3] = colours[src]
        ca.data.foreach_set("color", data.astype(np.float32).ravel())

    mat = bpy.data.materials.new("_bake")
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    attr = nt.nodes.new("ShaderNodeAttribute")
    em = nt.nodes.new("ShaderNodeEmission")
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(attr.outputs["Color"], em.inputs["Color"])
    nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    tex = nt.nodes.new("ShaderNodeTexImage")
    nt.nodes.active = tex
    tgt.data.materials.clear()
    tgt.data.materials.append(mat)
    for p in tgt.data.polygons:
        p.material_index = 0

    bpy.ops.object.select_all(action="DESELECT")
    tgt.select_set(True)
    bpy.context.view_layer.objects.active = tgt
    res = {}
    for name in list(layers) + ["AO"]:
        img = _float_image("_bake_" + name, size or SKIN_TEX)
        tex.image = img
        attr.attribute_name = name
        scene.cycles.samples = AO_SAMPLES if name == "AO" else 1
        bpy.ops.object.bake(type="AO" if name == "AO" else "EMIT", margin=24, margin_type="EXTEND", use_clear=True)
        res[name] = _pixels(img)
        bpy.data.images.remove(img)
    bpy.data.objects.remove(occ, do_unlink=True)
    scene.collection.objects.unlink(tgt)
    res["_target"] = tgt
    h.hide_render = False
    h.data.attributes.remove(h.data.attributes["_vi"])
    scene.render.engine = prev_engine
    res["AO"] = res["AO"][..., 0]
    return res


def _box_blur(a, r):
    """Separable box blur, radius r px (edges clamp)."""
    for ax in (0, 1):
        p = np.concatenate([np.repeat(a.take([0], ax), r + 1, ax), a, np.repeat(a.take([-1], ax), r, ax)], ax)
        c = np.cumsum(p, axis=ax)
        a = (c.take(range(2 * r + 1, c.shape[ax]), ax) - c.take(range(0, c.shape[ax] - 2 * r - 1), ax)) / (2 * r + 1)
    return a


def blur(a, r):
    for _ in range(3):
        a = _box_blur(a, max(1, r))
    return a


def _noise(rng, size, cells):
    g = rng.random((cells + 1, cells + 1))
    x = np.linspace(0, cells, size, endpoint=False)
    i = x.astype(int)
    f = x - i
    f = f * f * (3 - 2 * f)
    rows = g[i] * (1 - f)[:, None] + g[i + 1] * f[:, None]
    return rows[:, i] * (1 - f)[None, :] + rows[:, i + 1] * f[None, :]


def paint(h, eyes, R, base, female, skin, ball, glove=None, glove_col=None, knuckles=(), brow=None, hairline=None):
    """Steps 1-2: pack the UVs, bake, compose. Leaves the albedo in STATE.
    With `glove` (faces) the gloved hands get their own GLOVE_TEX map."""
    pack_skin_uvs(h, skin)
    if glove is not None:
        pack_skin_uvs(h, glove)
        paint_glove(h, glove, glove_col, knuckles)
    m = masks(h, eyes, R, female, np.array(ball, dtype=bool))
    aux = np.stack([m["lips"], m["lash"], m["ears"]], -1)
    co = np.empty(len(h.data.vertices) * 3)
    h.data.vertices.foreach_get("co", co)
    mid = (np.array(eyes[0]) + np.array(eyes[1])) / 2
    pos = co.reshape(-1, 3) - mid + 0.5               # positive: an emission colour
    aux2 = np.stack([m["red"], m["cool"], np.zeros(len(m["red"]))], -1)
    nrm = np.empty(len(h.data.vertices) * 3)
    h.data.vertices.foreach_get("normal", nrm)
    nrm = nrm.reshape(-1, 3) * 0.5 + 0.5               # 0..1: an emission colour
    res = bake(h, skin, {"paint": paint_colours(m, base, female), "aux": aux, "aux2": aux2, "pos": pos, "nrm": nrm})
    emit, ao, aux = res["paint"], res["AO"], res["aux"]
    STATE["hi"] = res["_target"]          # the dense skin: normal_map()'s source
    # Crevices: darker and a little warmer (light scattering in the skin);
    # only lightly across the lips (with the mouth parted, their seam read
    # as an open mouth; closed, a dark seam keeps the inner lip from glinting).
    occ = (1 - ao) * (1 - 0.3 * aux[..., 0])
    shade = 1 - AO_WEIGHT * occ
    alb = emit * shade[..., None] * (1 + occ[..., None] * np.array([0.10, -0.05, -0.08]))
    # Inside the closed lips (deep AO within the lip mask): dark, or the
    # lower lip's upturned inner face glints through the seam under a key light.
    # Also the lips' inner faces (turned away from the front, into the
    # mouth): the closed lips' seam shows a sliver of them.
    ny = res["nrm"][..., 1] * 2 - 1
    seam = aux[..., 0] * np.maximum(smoothstep(0.45, 0.15, ao), smoothstep(0.35, 0.0, ny))
    alb = alb * (1 - 0.85 * seam)[..., None]
    P = res["pos"] + mid - 0.5                        # each texel's surface position
    for k, v in accents(P, m["_lm"], aux[..., 0]).items():
        w = ACCENT[k] * v[..., None]
        alb = alb * (1 - w * (SHADE_TINT if ACCENT[k] > 0 else 1.0))
    alb = mottle(alb, P, res["aux2"], 17 if female else 7)
    if hairline:
        alb = paint_hairline(alb, P, eyes, hairline, aux[..., 2])
    if brow:
        alb = paint_brows(alb, P, eyes, R, brow)
    STATE.update(alb=alb, ao=ao, lips=aux[..., 0], ears=aux[..., 2])


GLOVE_TEX = 1024
GLOVE_CREASE = 0.45      # darkening in the knuckle creases


def creases(P, knuckles):
    """Fine fold lines across each finger joint (per texel, from the baked
    surface position P): two or three bands square to the bone, ~1 mm wide,
    deepest on the back of the hand, fading round the finger's sides."""
    out = np.zeros(P.shape[:2])
    for p, dorsal, r, axis in knuckles:
        d = P - np.array(p)
        ax, dn = np.array(axis), np.array(dorsal)
        t = d @ ax                                   # along the finger
        rad = d - t[..., None] * ax
        near = np.linalg.norm(rad, axis=-1) < 1.6 * r
        if not near.any():
            continue
        facing = np.clip((rad @ dn) / np.maximum(np.linalg.norm(rad, axis=-1), 1e-6), 0, 1)
        band = sum(np.exp(-((t - o) / 0.0007) ** 2) * k for o, k in ((0.0, 1.0), (0.0019, 0.6), (-0.0019, 0.6)))
        out = np.maximum(out, band * smoothstep(0.1, 0.6, facing) * near)
    print(f"glove creases: {int((out > 0.5).sum())} texels")
    return out


def _vnoise3(p, cell, seed):
    """Value noise over 3D positions (S, S, 3), `cell` m, smooth, 0..1. In
    space, not UV: no seams where the islands are cut."""
    q = p / cell
    i = np.floor(q)
    f = q - i
    f = f * f * (3 - 2 * f)
    h = lambda dx, dy, dz: np.modf(np.abs(np.sin((i[..., 0] + dx) * 127.1 + (i[..., 1] + dy) * 311.7
                                                + (i[..., 2] + dz) * 74.7 + seed * 19.3) * 43758.5453))[0]
    out = 0
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                w = (f[..., 0] if dx else 1 - f[..., 0]) * (f[..., 1] if dy else 1 - f[..., 1]) * (f[..., 2] if dz else 1 - f[..., 2])
                out = out + w * h(dx, dy, dz)
    return out


# Skin variation, in space (cells in m). Hue shifts are per channel (R, G, B)
# multipliers per unit of centred noise: warm patches go red, cool ones not.
MOTTLE = {
    "blotch": ((0.030, 0.14, (1.0, 1.15, 1.25)), (0.010, 0.07, (0.9, 1.0, 1.15))),
    "hue": (0.020, 0.06),            # red vs. yellow drift, low frequency
    "pores": (0.0007, 0.06),         # cell >= 2 texels (texel ~0.25 mm on the face): never a comb
    "freckles": (0.0016, 0.78, 0.10),   # cell, threshold, darkening; on the warm areas
}


def mottle(alb, P, aux2, seed):
    """Low-frequency tonal variation, pores and a few freckles."""
    d = np.linalg.norm(np.diff(P[:, :, :], axis=1), axis=-1)
    print(f"skin texel ~{np.median(d[d < 0.01]) * 1000:.2f} mm")
    out = alb
    for k, (cell, amp, tint) in enumerate(MOTTLE["blotch"]):
        n = _vnoise3(P, cell, seed + k) - 0.5
        out = out * (1 + amp * n[..., None] * np.array(tint))
    cell, amp = MOTTLE["hue"]
    n = _vnoise3(P, cell, seed + 7) - 0.5
    out = out * (1 + amp * n[..., None] * np.array([1.0, -0.2, -1.0]))
    cell, amp = MOTTLE["pores"]
    pores = (_vnoise3(P, cell, seed + 11) - 0.5) + 0.5 * (_vnoise3(P, cell * 0.6, seed + 13) - 0.5)
    out = out * (1 + amp * pores[..., None])
    cell, thr, dark = MOTTLE["freckles"]
    fr = smoothstep(thr, thr + 0.12, _vnoise3(P, cell, seed + 17)) * np.clip(aux2[..., 0] * 1.5, 0, 1)
    out = out * (1 - dark * fr[..., None] * np.array([0.7, 1.0, 1.2]))
    return out


# ---- painted hairline ------------------------------------------------------------------
# A shaved scalp: where hair would grow is a soft band darker than the skin,
# so `hair.none` reads shaved, not egg. The line is a height above the eyes'
# midpoint per azimuth round the head (0 = front, 90 = over the ear, 180 =
# nape), measured about a vertical axis `yc` behind the eyes (m).
# It sits where a hairline grows: the front ~60 mm above the eyes, a
# temple recess, a sideburn to below the eye line, an arc over the ear, the
# nape under the ear lobes. The hair pieces are made to cover it, not the
# other way round: hair.py stretches every cap-like piece (hair.CAPS) down
# until its edge is HAIR_MARGIN under this line -- change the line, refit
# the hair.
#   line     (azimuth deg, height m) knots, interpolated
#   soft     the edge's feather (m); `edge` its irregularity (m)
#   col      sRGB of the stubble; `density` its opacity at the core
#   grain    stubble dots' cell (m): >= 2 texels (~0.18 mm)
HAIR_M = {
    "yc": -0.078, "soft": 0.003, "edge": 0.0015, "col": (0.12, 0.10, 0.09), "density": 0.85, "grain": 0.0006,
    "line": ((0, 0.061), (8, 0.063), (16, 0.063), (24, 0.061), (30, 0.058), (36, 0.053), (44, 0.046), (50, 0.038),
             (56, 0.026), (62, 0.012), (67, -0.002), (71, -0.014), (75, -0.014), (78, 0.000), (82, 0.014),
             (88, 0.019), (96, 0.021), (104, 0.018), (110, 0.010), (116, -0.008), (122, -0.026),
             (130, -0.038), (145, -0.047), (162, -0.053), (180, -0.056)),
}
# the female line: a touch lower and rounder (no temple recess), a shorter sideburn
HAIR_F = {
    "yc": -0.078, "soft": 0.003, "edge": 0.0012, "col": (0.13, 0.10, 0.09), "density": 0.78, "grain": 0.0006,
    "line": ((0, 0.056), (10, 0.057), (20, 0.057), (28, 0.055), (34, 0.051), (42, 0.045), (49, 0.036), (55, 0.025),
             (61, 0.012), (66, 0.001), (70, -0.007), (74, -0.007), (78, 0.004), (83, 0.014), (90, 0.018),
             (98, 0.019), (106, 0.015), (112, 0.006), (118, -0.012), (124, -0.026), (132, -0.036),
             (146, -0.044), (162, -0.049), (180, -0.051)),
}


def paint_hairline(alb, P, eyes, b, ears=None):
    mid = (np.array(eyes[0]) + np.array(eyes[1])) / 2
    rel = P - mid
    az = np.degrees(np.arctan2(np.abs(rel[..., 0]), rel[..., 1] - b["yc"]))
    k = np.array(b["line"])
    line = np.interp(az, k[:, 0], k[:, 1])
    line = line + b["edge"] * (_vnoise3(P, 0.003, 31) - 0.5) + 0.5 * b["edge"] * (_vnoise3(P, 0.0012, 37) - 0.5)
    w = smoothstep(line - b["soft"], line + b["soft"], rel[..., 2])
    w = w * (rel[..., 2] > -0.2)
    if ears is not None:                     # no stubble on the ear over which the line arcs
        w = w * (1 - smoothstep(0.2, 0.6, ears))
    # two octaves at unrelated cells: no lattice squares
    grain = 0.6 * _vnoise3(P, b["grain"], 41) + 0.4 * _vnoise3(P, b["grain"] * 0.63, 43)
    dens = b["density"] * (0.8 + 0.3 * smoothstep(0.3, 0.7, grain))
    k_ = (w * dens)[..., None]
    col = srgb_to_lin(np.array(b["col"]))
    print(f"hairline: {int((w > 0.5).sum())} texels")
    return alb * (1 - k_) + (alb * 0.22 + col * 0.78) * k_


# ---- painted eyebrows ----------------------------------------------------------------
# One stroke per side, in the frame of that eye: `u` runs 0 (inner end) .. 1
# (outer end) across `ax` (distance from the face's midline, m). Lengths in m.
#   inner, outer  ax of the two ends, as offsets from the eye centre's ax
#   lift          the stroke's centre line above the eye centre at the inner end
#   arch, peak    extra lift (parabola, 0 at both ends) and where along u it peaks
#   tilt          drop of the outer end below the inner (a straight-line slope)
#   th_in, th_out half-thickness at the inner and outer end (linear between)
#   taper         the last fraction of u where it thins to `tip` x and fades
#   head          round-off length of the inner end
#   soft          edge feather; `col` sRGB; `density` opacity at the core
#   strand        the hairs' angle (deg, up from the outward line) inner .. outer
#   shadow        darkening of the skin just under the stroke
BROW_M = {
    "inner": -0.019, "outer": 0.024, "lift": 0.0160, "arch": 0.0012, "peak": 0.45, "tilt": -0.0005,
    "th_in": 0.0040, "th_out": 0.0031, "taper": 0.18, "tip": 0.45, "head": 0.0040,
    "soft": 0.0009, "col": (0.13, 0.09, 0.065), "density": 0.92, "strand": (60.0, 12.0), "shadow": 0.10,
}
BROW_F = {
    "inner": -0.017, "outer": 0.025, "lift": 0.0175, "arch": 0.0035, "peak": 0.65, "tilt": 0.0010,
    "th_in": 0.0025, "th_out": 0.0025, "taper": 0.20, "tip": 0.35, "head": 0.0030,
    "soft": 0.0007, "col": (0.14, 0.095, 0.07), "density": 0.92, "strand": (55.0, 10.0), "shadow": 0.08,
}


def _vnoise(x, y, seed):
    """Value noise on the unit lattice, smooth-interpolated, 0..1."""
    xi, yi = np.floor(x), np.floor(y)
    fx, fy = x - xi, y - yi
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    h = lambda i, j: np.modf(np.abs(np.sin(i * 127.1 + j * 311.7 + seed * 74.7) * 43758.5453))[0]
    a, b = h(xi, yi), h(xi + 1, yi)
    c, d = h(xi, yi + 1), h(xi + 1, yi + 1)
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def paint_brows(alb, P, eyes, R, b):
    """Mix a brow stroke into the albedo; P is each texel's surface position
    (S, S, 3, the build frame: front +Y, Z up)."""
    el, er = np.array(eyes[0]), np.array(eyes[1])
    mid = (el + er) / 2
    rel = P - mid
    w = np.zeros(P.shape[:2])
    shadow = np.zeros(P.shape[:2])
    noise = np.zeros(P.shape[:2])
    for e in (el, er):
        side = np.sign(e[0] - mid[0])
        ex = abs(e[0] - mid[0])
        ax = rel[..., 0] * side
        a0, a1 = ex + b["inner"], ex + b["outer"]
        u = (ax - a0) / (a1 - a0)
        cand = (ax > a0 - 0.006) & (ax < a1 + 0.004) & (rel[..., 1] > e[1] - mid[1] - 0.04) \
            & (np.abs(rel[..., 2] - (e[2] - mid[2]) - b["lift"]) < 0.02)
        if not cand.any():
            continue
        uc, rc, ac = u[cand], rel[cand], ax[cand]
        pk = b["peak"]
        bump = 1 - ((uc - pk) / np.where(uc < pk, pk, 1 - pk)) ** 2
        zc = (e[2] - mid[2]) + b["lift"] + b["arch"] * np.clip(bump, 0, 1) - b["tilt"] * np.clip(uc, 0, 1)
        v = rc[:, 2] - zc
        ue = np.clip(uc, 0, 1)
        s_ = ac - a0
        th = (b["th_in"] + (b["th_out"] - b["th_in"]) * ue) * (1 - (1 - b["tip"]) * smoothstep(1 - b["taper"], 1, ue))
        th = th * np.sqrt(np.clip((s_ + b["head"]) / (2.5 * b["head"]), 0.05, 1))   # a rounded inner head, not a square end
        # hairs: fine streaks along the growth direction, steep at the inner end
        ang = np.radians(b["strand"][0] + (b["strand"][1] - b["strand"][0]) * smoothstep(0.0, 0.35, ue))
        along = s_ * np.cos(ang) + v * np.sin(ang)
        perp = -s_ * np.sin(ang) + v * np.cos(ang)
        n1 = _vnoise(perp / 0.0006, along / 0.0035, 1)       # >= 2 texels across: finer aliased into a comb
        n2 = _vnoise(perp / 0.0011, along / 0.004, 2)
        ragged = th * (0.85 + 0.3 * n2)                      # an uneven edge, not a ruled one
        core = smoothstep(ragged + b["soft"], ragged - b["soft"], np.abs(v))
        ends = smoothstep(-b["head"], 1.5 * b["head"], s_) * smoothstep(1.0, 1 - 0.5 * b["taper"], uc)
        a = core * ends
        sh = smoothstep(-th - 0.0045, -th - 0.0005, v) * smoothstep(-th + 0.001, -th - 0.0005, v) * ends
        idx = np.nonzero(cand)
        w[idx] = np.maximum(w[idx], a)
        shadow[idx] = np.maximum(shadow[idx], sh)
        noise[idx] = n1
    dens = b["density"] * np.clip(0.8 + 0.3 * noise, 0, 1)
    k = (w * dens)[..., None]
    sk = (b["shadow"] * shadow * (1 - w))[..., None]
    alb = alb * (1 - sk * np.array([0.9, 1.0, 1.05]))     # a touch warm, as skin shades
    col = srgb_to_lin(np.array(b["col"])) * (0.85 + 0.25 * noise[..., None])
    print(f"brows: {int((w > 0.5).sum())} texels")
    return alb * (1 - k) + col * k


def paint_glove(h, glove, col, knuckles):
    """The suit's gloves, seen up close in first person: AO (finger creases,
    the gaps between fingers), a worn, lighter sheen over the knuckles (back
    of the hand: a soft blob at each finger joint, facing away from the
    palm), a fine weave. (Hard-edged knuckle plates from vertex masks came
    out blotchy at the hand's vertex density.) Leaves STATE["glove"]."""
    me = h.data
    n = len(me.vertices)
    co = np.empty(n * 3)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    nr = np.empty(n * 3)
    me.vertices.foreach_get("normal", nr)
    nr = nr.reshape(-1, 3)
    edges = _neighbours(me)
    pad = np.zeros(n)
    for p, dorsal, r, _ in knuckles:
        d2 = np.sum((co - np.array(p)) ** 2, axis=1)
        facing = np.clip(nr @ np.array(dorsal), 0, 1)
        pad = np.maximum(pad, np.exp(-d2 / (2 * r * r)) * smoothstep(0.2, 0.6, facing))
    pad = smooth(pad, edges, n, 3)
    pad = pad / max(pad.max(), 1e-6)
    lin = srgb_to_lin(np.array(col))
    pad_col = srgb_to_lin(np.array(col) * 1.5 + 0.03)          # worn, lighter over the knuckles
    w = 0.6 * pad[:, None]
    layer = np.repeat(lin[None, :], n, axis=0) * (1 - w) + pad_col[None, :] * w
    aux = np.stack([pad, np.zeros(n), np.zeros(n)], -1)
    pos = co + 0.5                                   # positive: an emission colour
    res = bake(h, glove, {"paint": layer, "aux": aux, "pos": pos}, GLOVE_TEX)
    bpy.data.objects.remove(res["_target"], do_unlink=True)
    occ = 1 - res["AO"]
    alb = res["paint"] * (1 - 0.85 * occ)[..., None]
    alb = alb * (1 - GLOVE_CREASE * creases(res["pos"] - 0.5, knuckles))[..., None]
    rng = np.random.default_rng(5)
    S = GLOVE_TEX
    weave = (np.sin(np.arange(S) * 2 * np.pi / 3.0)[None, :] * np.sin(np.arange(S) * 2 * np.pi / 3.0)[:, None])
    alb = alb * (1 + 0.04 * weave[..., None]) * (1 + 0.08 * (_noise(rng, S, 32) - 0.5)[..., None])
    STATE["glove"] = alb


def normal_map(head, skin_mat):
    """Bake the full-resolution skin's normals onto the decimated head's skin
    faces (tangent space, OpenGL / glTF convention): the decimated ears,
    nostrils and lids shade like the dense mesh. Returns (S, S, 3) 0..1."""
    scene = bpy.context.scene
    prev_engine = scene.render.engine
    scene.render.engine = "CYCLES"
    cycles_gpu.cycles_device(scene)
    hi = STATE["hi"]
    scene.collection.objects.link(hi)
    lo = head.copy()
    lo.data = head.data.copy()
    lo.name = "_bake_lo"
    scene.collection.objects.link(lo)
    for mod in list(lo.modifiers):
        lo.modifiers.remove(mod)
    si = [i for i, m in enumerate(lo.data.materials) if m == skin_mat]
    bm = bmesh.new()
    bm.from_mesh(lo.data)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index not in si], context="FACES")
    bm.to_mesh(lo.data)
    bm.free()
    mat = bpy.data.materials.new("_bake_n")
    mat.use_nodes = True
    tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
    img = _float_image("_bake_normal", SKIN_TEX)
    tex.image = img
    mat.node_tree.nodes.active = tex
    lo.data.materials.clear()
    lo.data.materials.append(mat)
    for p in lo.data.polygons:
        p.material_index = 0
    bpy.ops.object.select_all(action="DESELECT")
    hi.select_set(True)
    lo.select_set(True)
    bpy.context.view_layer.objects.active = lo
    scene.cycles.samples = 1
    bpy.ops.object.bake(type="NORMAL", normal_space="TANGENT", use_selected_to_active=True,
                        cage_extrusion=0.002, max_ray_distance=0.008, margin=24, margin_type="EXTEND", use_clear=True)
    nrm = _pixels(img)
    bpy.data.images.remove(img)
    for o in (lo, hi):
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.data.materials.remove(mat)
    scene.render.engine = prev_engine
    return nrm


def finish_glove(glove_mat, out_dir, name):
    """Replace the glove's tiling fabric maps (box UVs) with the baked map."""
    if "glove" not in STATE:
        return
    path = os.path.join(out_dir, name + "_glove.jpg")
    _save_jpeg(lin_to_srgb(STATE.pop("glove")), path)
    nt = glove_mat.node_tree
    for nd in [nd for nd in nt.nodes if nd.type in ("TEX_IMAGE", "NORMAL_MAP")]:
        nt.nodes.remove(nd)
    _link(glove_mat, path, roughness=0.7)


def finish(head, skin_mat, out_dir, name):
    """Bake the normal map, write both maps as JPEGs and drive the skin
    material with them."""
    os.makedirs(out_dir, exist_ok=True)
    nrm = normal_map(head, skin_mat)
    # The lip seam: the decimated mouth folds where the dense one is a slit;
    # its baked normals tilt up and drew a bright line. Flat there.
    # Ears too: the relaxed low-poly concha and the dense one disagree
    # (speckled rays); the painted AO carries the folds.
    flat = np.maximum(STATE["lips"] * smoothstep(0.75, 0.35, STATE["ao"]), np.clip(STATE["ears"] * 1.5, 0, 1))[..., None]
    nrm = nrm * (1 - flat) + np.array([0.5, 0.5, 1.0]) * flat
    path = os.path.join(out_dir, name + "_skin.jpg")
    _save_jpeg(lin_to_srgb(STATE["alb"]), path)
    _link(skin_mat, path, roughness=0.58)
    npath = os.path.join(out_dir, name + "_skin_n.jpg")
    _save_jpeg(nrm, npath)
    img = bpy.data.images.load(npath, check_existing=False)
    img.colorspace_settings.name = "Non-Color"
    img.pack()
    nt = skin_mat.node_tree
    t = nt.nodes.new("ShaderNodeTexImage")
    t.image = img
    nm = nt.nodes.new("ShaderNodeNormalMap")
    nt.links.new(t.outputs["Color"], nm.inputs["Color"])
    nt.links.new(nm.outputs["Normal"], nt.nodes["Principled BSDF"].inputs["Normal"])
    STATE.clear()


def _save_jpeg(srgb, path):
    h, w = srgb.shape[:2]
    img = bpy.data.images.new("_out", w, h, alpha=False)
    px = np.concatenate([srgb, np.ones((h, w, 1))], -1).astype(np.float32)
    img.pixels.foreach_set(px.ravel())
    img.file_format = "JPEG"
    img.save(filepath=path, quality=QUALITY)
    bpy.data.images.remove(img)


def _link(mat, path, roughness):
    img = bpy.data.images.load(path, check_existing=False)
    img.pack()
    nt = mat.node_tree
    b = nt.nodes["Principled BSDF"]
    t = nt.nodes.new("ShaderNodeTexImage")
    t.image = img
    nt.links.new(t.outputs["Color"], b.inputs["Base Color"])
    b.inputs["Roughness"].default_value = roughness
    mat["keep_uv"] = True


# ---- eyes ---------------------------------------------------------------------------
PUPIL = 9.5       # degrees from the eye's axis
IRIS = 34.0
def eye_image(iris_col, seed=3):
    """Pupil, iris (radial striations, a lighter collarette, darker rim),
    limbal ring, sclera -- on the azimuthal projection eye_uvs() writes."""
    n = EYE_TEX
    yy, xx = (np.mgrid[0:n, 0:n] + 0.5) / n - 0.5
    th = np.hypot(xx, yy) / 0.5 * 180.0          # degrees from the front pole
    ph = np.arctan2(yy, xx)
    rng = np.random.default_rng(seed)
    k = np.arange(1, 60)
    amp = rng.random(len(k)) / k ** 0.6
    pha = rng.random(len(k)) * 2 * np.pi
    stri = sum(a * np.cos(kk * ph + p) for a, p, kk in zip(amp, pha, k))
    stri = (stri - stri.min()) / (stri.max() - stri.min())
    t = np.clip((th - PUPIL) / (IRIS - PUPIL), 0, 1)        # 0 at the pupil .. 1 at the rim
    iris = srgb_to_lin(np.array(iris_col))[None, None, :] * (
        (0.75 + 0.5 * stri[..., None]) * (1.25 - 0.55 * t[..., None])
        + 0.35 * np.exp(-((t - 0.18) / 0.08) ** 2)[..., None])
    sclera = srgb_to_lin(np.array([0.93, 0.91, 0.88]))[None, None, :] * (
        1 - 0.12 * smoothstep(60, 120, th)[..., None] * np.array([0.0, 0.6, 0.6]))
    limbal = srgb_to_lin(np.array([0.10, 0.08, 0.07]))
    px = 180.0 / (n / 2)                                     # degrees per texel
    a_pupil = smoothstep(PUPIL - px, PUPIL + px, th)[..., None]
    a_limb = np.exp(-((th - IRIS + 1.0) / 2.6) ** 2)[..., None]
    a_scl = smoothstep(IRIS - px, IRIS + 1.5 * px, th)[..., None]
    col = np.array([0.012, 0.010, 0.010]) * (1 - a_pupil) + iris * a_pupil
    col = col * (1 - 0.8 * a_limb) + limbal * 0.8 * a_limb
    col = col * (1 - a_scl) + sclera * a_scl
    return lin_to_srgb(col)


def eye_uvs(bm, faces, centre):
    """Azimuthal-equidistant UV round the front pole (+Y): the pole at
    (0.5, 0.5), the back pole on the unit circle's rim."""
    uv = bm.loops.layers.uv.verify()
    for f in faces:
        for lp in f.loops:
            d = (lp.vert.co - centre).normalized()
            th = math.acos(max(-1.0, min(1.0, d.y)))
            ph = math.atan2(d.z, d.x)
            r = th / math.pi * 0.5
            lp[uv].uv = (0.5 + r * math.cos(ph), 0.5 + r * math.sin(ph))


def dress_eyes(mats, iris_col, out_dir, name):
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, name + "_eye.jpg")
    _save_jpeg(eye_image(iris_col), path)
    img = None
    for m in mats:
        nt = m.node_tree
        b = nt.nodes["Principled BSDF"]
        t = nt.nodes.new("ShaderNodeTexImage")
        if img is None:
            img = bpy.data.images.load(path, check_existing=False)
            img.pack()
        t.image = img
        nt.links.new(t.outputs["Color"], b.inputs["Base Color"])
        # distinct roughness keeps the two materials apart: import_pack's
        # dedup() would merge identical ones and the head's surface list
        # (eye, sclera) is a client contract
        b.inputs["Roughness"].default_value = 0.08 if m.name == "eye" else 0.18
        m["keep_uv"] = True
