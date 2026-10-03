"""Procedural surface textures for the armor and the undersuit.

Each material gets a TILING 256 px albedo and normal map, generated with
numpy from a height field: plate panel lines with rivets and scratches,
brushed metal, hammered iron, woven straps and gloves, a quilted suit.
Meshes get a box projection in METRES (uv_box), so every part shows the
same texel size and a plate's panel lines line up with its neighbour's.

    import textures
    textures.dress(material, "plate", (0.42, 0.46, 0.33))   # display colour
    textures.uv_box(obj)                                      # before export

ponytail: tiling, not baked per part -- no edge-aware wear. Bake from
Cycles (pointiness) if the wear must follow edges.
"""
import bpy
import bmesh
import numpy as np

N = 256          # texels per side
TILE = 0.4       # metres of surface one texture repeat covers


# ---- height fields -------------------------------------------------------------
def _smooth(rng, cells):
    """Tileable value noise, `cells` lattice points per side, bilinear."""
    g = rng.random((cells, cells))
    x = np.arange(N) * cells / N
    i0 = x.astype(int) % cells
    i1 = (i0 + 1) % cells
    f = x - x.astype(int)
    f = f * f * (3 - 2 * f)
    rows = g[i0] * (1 - f)[:, None] + g[i1] * f[:, None]          # (N, cells)
    return rows[:, i0] * (1 - f)[None, :] + rows[:, i1] * f[None, :]


def _fbm(rng, base, octaves=4):
    h, amp, tot = np.zeros((N, N)), 1.0, 0.0
    for o in range(octaves):
        h += _smooth(rng, base * 2 ** o) * amp
        tot += amp
        amp *= 0.5
    return h / tot


def _lines(pos, width):
    """1 on a groove `width` texels wide at each fractional position, else 0."""
    out = np.zeros(N)
    for p in pos:
        c = p * N
        d = np.abs((np.arange(N) - c + N / 2) % N - N / 2)
        out = np.maximum(out, np.clip(1 - d / width, 0, 1))
    return out


def _scratches(rng, count, length):
    s = np.zeros((N, N))
    for _ in range(count):
        x, y = rng.random(2) * N
        a = rng.random() * np.pi
        for t in np.linspace(0, length, int(length) * 2):
            s[int(y + t * np.sin(a)) % N, int(x + t * np.cos(a)) % N] = 1
    return s


def height_tint(kind, rng):
    """(height 0..1, tint multiplier) for a surface kind."""
    low = _fbm(rng, 4)
    if kind == "plate":
        gu, gv = _lines((0.0, 0.5), 1.6), _lines((0.0, 0.36, 0.71), 1.6)
        groove = np.maximum(gu[None, :], gv[:, None])
        # rivets: small domes beside the vertical lines
        yy, xx = np.mgrid[0:N, 0:N]
        riv = np.zeros((N, N))
        for cx in (0.035, 0.535):
            for cy in np.arange(0.06, 1.0, 0.125):
                d = np.hypot(((xx - cx * N + N / 2) % N - N / 2), ((yy - cy * N + N / 2) % N - N / 2))
                riv = np.maximum(riv, np.clip(1 - d / 2.2, 0, 1))
        scr = _scratches(rng, 18, 10)
        h = 0.55 + 0.05 * low - 0.5 * groove + 0.35 * riv
        tint = (1 + 0.10 * (low - 0.5)) * (1 - 0.45 * groove) * (1 + 0.12 * scr) * (1 + 0.15 * riv)
    elif kind == "trim":
        h = 0.5 + 0.1 * _fbm(rng, 16)
        tint = 1 + 0.08 * (_fbm(rng, 16) - 0.5)
    elif kind in ("metal", "iron"):
        brushed = _smooth(rng, 128)[:, :1] * np.ones((1, N))            # streaks along u
        brushed = 0.5 * brushed + 0.5 * _smooth(rng, 64)
        h = 0.5 + 0.06 * brushed
        tint = 1 + 0.10 * (brushed - 0.5)
        if kind == "iron":
            pits = _fbm(rng, 24, 2)
            h = h - 0.25 * np.clip(pits - 0.62, 0, 1) * 4
            tint = tint * (1 - 0.35 * np.clip(pits - 0.6, 0, 1) * 4) * (1 + 0.12 * _scratches(rng, 30, 8))
    elif kind in ("fabric", "suit"):
        k = 48 if kind == "fabric" else 72                                # threads per repeat
        u = np.arange(N) / N
        weave = np.sin(2 * np.pi * u * k)[None, :] * np.sin(2 * np.pi * u * k)[:, None]
        h = 0.5 + 0.03 * weave
        tint = (1 + 0.03 * weave) * (1 + 0.05 * (low - 0.5))
        if kind == "suit":
            # quilted seams every 0.1 m, with a dashed stitch beside each
            seam = np.maximum(_lines((0.0, 0.25, 0.5, 0.75), 1.4)[None, :], _lines((0.0, 0.5), 1.4)[:, None])
            dash = (np.arange(N) // 4 % 2)[:, None] * _lines((0.02, 0.27, 0.52, 0.77), 0.7)[None, :]
            h = h - 0.4 * seam + 0.15 * dash
            tint = tint * (1 - 0.35 * seam) * (1 + 0.4 * dash)
    else:
        raise ValueError(kind)
    return np.clip(h, 0, 1), tint


def normal_from(h, strength):
    dx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * strength
    dy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * strength
    n = np.stack([-dx, -dy, np.ones_like(h)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


# Normal-map strength per kind: plates carry deep grooves, cloth barely any
# (a strong weave read as zebra stripes on the first-person gloves).
STRENGTH = {"plate": 6.0, "trim": 2.0, "metal": 2.0, "iron": 4.0, "fabric": 1.0, "suit": 2.0}


# ---- Blender -------------------------------------------------------------------
def _image(name, rgb, non_color):
    img = bpy.data.images.get(name)
    if img:
        bpy.data.images.remove(img)
    img = bpy.data.images.new(name, N, N, alpha=False)
    img.colorspace_settings.name = "Non-Color" if non_color else "sRGB"
    px = np.concatenate([rgb, np.ones((N, N, 1))], -1).astype(np.float32)
    img.pixels.foreach_set(px[::-1].ravel())      # Blender's rows run bottom-up
    img.pack()
    return img


def dress(mat, kind, display_col, seed=1):
    """Drive the material's base colour and normal from generated maps."""
    rng = np.random.default_rng(seed + sum(map(ord, mat.name)))
    h, tint = height_tint(kind, rng)
    rgb = np.clip(np.array(display_col)[None, None, :] * tint[..., None], 0, 1)
    nt = mat.node_tree
    b = nt.nodes["Principled BSDF"]
    col = nt.nodes.new("ShaderNodeTexImage")
    col.image = _image("tex_" + mat.name, rgb, False)
    nt.links.new(col.outputs["Color"], b.inputs["Base Color"])
    nrm = nt.nodes.new("ShaderNodeTexImage")
    nrm.image = _image("nrm_" + mat.name, normal_from(h, STRENGTH[kind]), True)
    nm = nt.nodes.new("ShaderNodeNormalMap")
    nt.links.new(nrm.outputs["Color"], nm.inputs["Color"])
    nt.links.new(nm.outputs["Normal"], b.inputs["Normal"])


def uv_box(obj):
    """UVs by box projection in world metres: each face takes the two axes
    its normal is NOT along, divided by TILE."""
    mw = obj.matrix_world
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    uv = bm.loops.layers.uv.verify()
    slots = obj.material_slots
    for f in bm.faces:
        mat = slots[f.material_index].material if f.material_index < len(slots) else None
        if mat and (mat.name == "armor_decal" or mat.get("keep_uv")):
            continue                                    # decals and imported (UBC) textures keep their UVs
        n = mw.to_3x3() @ f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        a, b = [i for i in range(3) if i != ax]
        for lp in f.loops:
            co = mw @ lp.vert.co
            lp[uv].uv = (co[a] / TILE, co[b] / TILE)
    bm.to_mesh(obj.data)
    bm.free()


# ---- decals --------------------------------------------------------------------
# A 2x2 atlas of stencils: 0 squad number "07", 1 chevron, 2 hazard stripes,
# 3 insignia (ring and triangle). RGBA; transparent outside the mark.
DECAL_CELLS = 2
STENCIL = (0.86, 0.83, 0.72)
HAZARD = (0.92, 0.70, 0.12)


def _seg_digit(d, x0, y0, w, h, t):
    """Seven-segment digit `d` as a boolean mask on an (n, n) cell grid."""
    segs = {"0": "abcdef", "7": "abc"}[d]
    n = N // DECAL_CELLS
    yy, xx = np.mgrid[0:n, 0:n]
    m = np.zeros((n, n), bool)
    box = lambda x1, y1, x2, y2: (xx >= x1) & (xx < x2) & (yy >= y1) & (yy < y2)
    if "a" in segs: m |= box(x0, y0, x0 + w, y0 + t)
    if "b" in segs: m |= box(x0 + w - t, y0, x0 + w, y0 + h // 2)
    if "c" in segs: m |= box(x0 + w - t, y0 + h // 2, x0 + w, y0 + h)
    if "d" in segs: m |= box(x0, y0 + h - t, x0 + w, y0 + h)
    if "e" in segs: m |= box(x0, y0 + h // 2, x0 + t, y0 + h)
    if "f" in segs: m |= box(x0, y0, x0 + t, y0 + h // 2)
    return m


def decal_atlas():
    n = N // DECAL_CELLS
    yy, xx = np.mgrid[0:n, 0:n] / n                     # 0..1 within a cell, y down
    cells = []
    # 0: "07", stencil gaps (every segment split by a 2 px bridge)
    m = _seg_digit("0", 14, 24, 44, 80, 10) | _seg_digit("7", 70, 24, 44, 80, 10)
    m &= ~((np.mgrid[0:n, 0:n][0] % 40) < 3)
    cells.append((m, STENCIL))
    # 1: chevron, two stacked V bars
    v = np.abs(xx - 0.5)
    m = np.zeros((n, n), bool)
    for y0 in (0.25, 0.52):
        yv = y0 + (0.5 - v) * 0.5
        m |= (yy > yv - 0.06) & (yy < yv + 0.06) & (v < 0.42)
    cells.append((m, HAZARD))
    # 2: hazard stripes in a band (yellow; the dark stripes are see-through
    #    to the dark trim-coloured plate beneath only where painted)
    m = ((xx + yy) * 6 % 1 < 0.5) & (yy > 0.3) & (yy < 0.7) & (xx > 0.05) & (xx < 0.95)
    cells.append((m, HAZARD))
    # 3: insignia, a ring round a triangle
    r = np.hypot(xx - 0.5, yy - 0.5)
    ring = (r > 0.36) & (r < 0.44)
    tri = (yy > 0.3) & (yy < 0.68) & (np.abs(xx - 0.5) < (yy - 0.3) * 0.62)
    cells.append((ring | tri, STENCIL))
    rgba = np.zeros((N, N, 4))
    for k, (m, col) in enumerate(cells):
        cy, cx = divmod(k, DECAL_CELLS)
        sl = (slice(cy * n, (cy + 1) * n), slice(cx * n, (cx + 1) * n))
        wear = np.random.default_rng(k).random((n, n)) > 0.12          # chipped paint
        rgba[sl][..., :3] = col
        rgba[sl][..., 3] = (m & wear).astype(float)
    return rgba


def decal_material():
    m = bpy.data.materials.get("armor_decal")
    if m:
        return m
    m = bpy.data.materials.new("armor_decal")
    m.use_nodes = True
    nt = m.node_tree
    b = nt.nodes["Principled BSDF"]
    b.inputs["Roughness"].default_value = 0.6
    img = bpy.data.images.get("tex_decal") or bpy.data.images.new("tex_decal", N, N, alpha=True)
    img.pixels.foreach_set(decal_atlas()[::-1].astype(np.float32).ravel())
    img.pack()
    tx = nt.nodes.new("ShaderNodeTexImage")
    tx.image = img
    rnd = nt.nodes.new("ShaderNodeMath")
    rnd.operation = "ROUND"                         # the glTF exporter reads this as alphaMode MASK
    nt.links.new(tx.outputs["Color"], b.inputs["Base Color"])
    nt.links.new(tx.outputs["Alpha"], rnd.inputs[0])
    nt.links.new(rnd.outputs[0], b.inputs["Alpha"])
    return m


def decal(obj, cell, direction, size, offset=(0.0, 0.0), up=(0.0, 0.0, 1.0)):
    """Stick stencil `cell` onto `obj` where a ray along `direction` (world,
    pointing INTO the part) first hits it, `offset` (sideways, up) metres
    from the part's centre. Returns the decal object (not yet joined)."""
    from mathutils import Vector
    d = Vector(direction).normalized()
    u = Vector(up)
    side = d.cross(u).normalized()
    mw = obj.matrix_world
    centre = sum((mw @ Vector(c) for c in obj.bound_box), Vector()) / 8
    origin = centre + side * offset[0] + u * offset[1] - d * 0.8
    inv = mw.inverted()
    hit, loc, nrm, _ = obj.ray_cast(inv @ origin, (inv.to_3x3() @ d).normalized())
    if not hit:
        raise RuntimeError(f"decal on {obj.name}: ray missed")
    p = mw @ loc
    n = (inv.to_3x3().transposed() @ nrm).normalized()
    if n.dot(d) > 0:
        n = -n
    t = (u - n * u.dot(n)).normalized()                 # the stencil's up
    r = (-n).cross(t).normalized()                      # its right, seen from outside
    c = p + n * 0.0015
    h = size / 2
    verts = [c - r * h - t * h, c + r * h - t * h, c + r * h + t * h, c - r * h + t * h]
    cy, cx = divmod(cell, DECAL_CELLS)
    w = 1.0 / DECAL_CELLS
    u0, u1, v1 = cx * w, (cx + 1) * w, 1 - cy * w
    v0 = v1 - w
    me = bpy.data.meshes.new(obj.name + "_decal")
    me.from_pydata([tuple(v) for v in verts], [], [(0, 1, 2, 3)])
    uvl = me.uv_layers.new(name="UVMap")
    for li, (uu, vv) in enumerate(((u0, v0), (u1, v0), (u1, v1), (u0, v1))):
        uvl.data[li].uv = (uu, vv)
    me.materials.append(decal_material())
    ob = bpy.data.objects.new(me.name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


if __name__ == "__main__":
    # Self-check: every kind yields a bounded height field and a sane normal map.
    r = np.random.default_rng(0)
    for k in ("plate", "trim", "metal", "iron", "fabric", "suit"):
        h, t = height_tint(k, r)
        assert h.shape == (N, N) and 0 <= h.min() and h.max() <= 1, k
        assert 0.3 < t.mean() < 1.5, (k, t.mean())
        n = normal_from(h, 6.0)
        assert np.all(n[..., 2] > 0.5), k      # normals point out of the surface
    a = decal_atlas()
    assert a.shape == (N, N, 4)
    for k in range(DECAL_CELLS ** 2):                   # every stencil paints something
        cy, cx = divmod(k, DECAL_CELLS)
        n = N // DECAL_CELLS
        assert a[cy * n:(cy + 1) * n, cx * n:(cx + 1) * n, 3].mean() > 0.03, k
    print("textures: self-check PASS")
