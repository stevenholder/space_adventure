#!/usr/bin/env -S blender -b --python
"""Hand weapons and throwables: Quaternius Ultimate RPG pack (CC0) -> build/rpg/<id>.raw.glb.

    blender -b --python tools/bpy/rpg_items.py        (run from art/; npm run rpg)

The pack (vendor/ultimate-rpg/*.fbx, gitignored; quaternius.com/packs/ultimaterpg.html)
ships flat models whose materials carry only names (every base colour is 0.8
grey), so each model is:
  1. imported, its materials recoloured by NAME from PALETTE (sRGB in,
     linear out -- Principled wants linear);
  2. scaled to `length` metres along its long axis;
  3. turned into the frame every held item uses (art/README.md): the blade or
     haft along glTF -Z (Blender +Y) out of the fist, the edge / axe bit /
     hammer face toward glTF +Y (Blender +Z), the GRIP point at the origin.
     Pack frame: length along +Z, edge toward +X, flat across Y;
  4. exported. tools/rpg_items.mjs adds the mount empties (`grip`, `muzzle`
     = the tip, `fore` = the second hand on a two-hander) and the manifest rows.

The frag grenade is not in the pack; it is built here from primitives.
Throwables stay upright (glTF +Y up), centred on their middle.
"""
import json
import math
import os

import bpy
from mathutils import Matrix, Vector

ART = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PACK = os.path.join(ART, "vendor", "ultimate-rpg")
OUT = os.path.join(ART, "build", "rpg")

# Material name -> sRGB colour (and alpha). The pack's own names.
PALETTE = {
    "LightSteel": (0.78, 0.80, 0.83), "Steel": (0.56, 0.58, 0.62), "DarkSteel": (0.28, 0.29, 0.32),
    "LightWood": (0.62, 0.44, 0.27), "DarkWood": (0.33, 0.20, 0.12), "Brown": (0.42, 0.27, 0.16),
    "Glass": (0.80, 0.90, 0.95, 0.35),
    "Liquid_Red": (0.90, 0.22, 0.10), "Liquid_Green": (0.45, 0.92, 0.20), "Liquid_Yellow": (0.98, 0.80, 0.20),
    "Liquid_Cyan": (0.25, 0.85, 0.95), "Liquid_Magenta": (0.92, 0.25, 0.70),
}
LIQUID_GLOW = 0.6   # liquids glow a little: a flask reads as full at a distance

# id: pack file, length (m) along the long axis, grip in PACK-z (the pack's
# own units, measured from the width profile along z). A two-hander (`fore`)
# puts its second hand SPACING metres down the haft from the grip: the same
# spacing human.py's two-handed poses close the left fist at
# (MELEE_SPACING), so one set of clips holds every two-hander.
SPACING = 0.18
WEAPONS = {
    "melee.dagger": dict(src="Dagger", length=0.38, grip=-0.12),
    "melee.sword": dict(src="Sword", length=0.95, grip=-0.10),
    "melee.axe": dict(src="Axe_small", length=0.62, grip=-0.30),
    "melee.greatsword": dict(src="Sword_big", length=1.45, grip=0.38, fore=True),
    "melee.greataxe": dict(src="Axe_Double", length=1.10, grip=0.20, fore=True),
    "melee.hammer": dict(src="Hammer_Double", length=1.20, grip=0.45, fore=True),
}
THROWN = {
    "throw.fire": dict(src="Potion1_Filled", height=0.20),
    "throw.acid": dict(src="Potion9_Filled", height=0.18),
    "potion.heal": dict(src="Potion8_Filled", height=0.14),
}


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def recolour(obj):
    for slot in obj.material_slots:
        m = slot.material
        if m is None:
            continue
        name = m.name.split(".")[0]
        col = PALETTE.get(name)
        if col is None:
            raise SystemExit(f"rpg_items: {obj.name}: no palette entry for material {name!r}")
        m.use_nodes = True
        bsdf = m.node_tree.nodes.get("Principled BSDF")
        bsdf.inputs["Base Color"].default_value = (*[srgb_to_linear(x) for x in col[:3]], 1.0)
        bsdf.inputs["Metallic"].default_value = 0.6 if "Steel" in name else 0.0
        bsdf.inputs["Roughness"].default_value = 0.35 if "Steel" in name or name == "Glass" else 0.7
        # The FBX importer carries the pack's transparency into Alpha (0 on
        # every material): exported as is, every model was invisible.
        bsdf.inputs["Alpha"].default_value = col[3] if len(col) == 4 else 1.0
        m.blend_method = "BLEND" if len(col) == 4 else "OPAQUE"
        if name.startswith("Liquid_"):
            bsdf.inputs["Emission Color"].default_value = (*[srgb_to_linear(x) for x in col[:3]], 1.0)
            bsdf.inputs["Emission Strength"].default_value = LIQUID_GLOW


def load(src):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.join(PACK, src + ".fbx"))
    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise SystemExit(f"rpg_items: {src}: expected one mesh, got {len(meshes)}")
    o = meshes[0]
    # Bake the importer's object transform into the mesh: from here on the
    # mesh's own coordinates are the pack frame.
    o.data.transform(o.matrix_world)
    o.matrix_world = Matrix.Identity(4)
    recolour(o)
    return o


def extent(o, axis):
    vs = [v.co[axis] for v in o.data.vertices]
    return min(vs), max(vs)


def export(oid, meta):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, oid + ".raw.glb")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=False,
                              export_apply=True, export_yup=True)
    with open(os.path.join(OUT, oid + ".json"), "w") as f:
        json.dump(meta, f, indent=2)
    print(f"rpg_items: {oid} -> {path}")


def weapon(oid, w):
    o = load(w["src"])
    z0, z1 = extent(o, 2)
    k = w["length"] / (z1 - z0)
    # pack (x edge, y flat, z length) -> Blender (x flat, y length, z edge),
    # grip at the origin. A cyclic permutation: a proper rotation.
    turn = Matrix(((0, 1, 0, 0), (0, 0, 1, 0), (1, 0, 0, 0), (0, 0, 0, 1)))
    o.data.transform(Matrix.Scale(k, 4) @ turn @ Matrix.Translation((0, 0, -w["grip"])))
    o.name = oid
    # Mounts in glTF coordinates (Blender (x, y, z) -> glTF (x, z, -y)).
    tip = (z1 - w["grip"]) * k
    mounts = {"grip": [0, 0, 0], "muzzle": [0, 0, round(-tip, 4)]}
    if w.get("fore"):
        mounts["fore"] = [0, 0, SPACING]
    export(oid, {"mounts": mounts, "hands": 2 if "fore" in w else 1, "length": w["length"]})


def thrown(oid, t):
    o = load(t["src"])
    z0, z1 = extent(o, 2)
    k = t["height"] / (z1 - z0)
    o.data.transform(Matrix.Scale(k, 4) @ Matrix.Translation((0, 0, -(z0 + z1) / 2)))
    o.name = oid
    export(oid, {"mounts": {"grip": [0, 0, 0]}, "height": t["height"]})


def mat(name, srgb, metal=0.0, rough=0.6, glow=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    lin = (*[srgb_to_linear(x) for x in srgb], 1.0)
    b.inputs["Base Color"].default_value = lin
    b.inputs["Metallic"].default_value = metal
    b.inputs["Roughness"].default_value = rough
    if glow:
        b.inputs["Emission Color"].default_value = lin
        b.inputs["Emission Strength"].default_value = glow
    return m


def frag():
    """A stubby sci-fi frag: olive body with grip bands, a steel fuse cap,
    the spoon down one side, a pin ring, and an amber arming light."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    olive = mat("Body", (0.33, 0.38, 0.22))
    band = mat("Band", (0.20, 0.22, 0.16))
    steel = mat("Steel", (0.62, 0.64, 0.68), metal=0.7, rough=0.35)
    amber = mat("Light", (1.0, 0.62, 0.12), glow=2.0)
    parts = []

    def add(op, m, **kw):
        op(**kw)
        o = bpy.context.active_object
        o.data.materials.append(m)
        parts.append(o)
        return o

    add(bpy.ops.mesh.primitive_cylinder_add, olive, vertices=16, radius=0.032, depth=0.085, location=(0, 0, 0))
    for z in (-0.026, 0.0, 0.026):
        add(bpy.ops.mesh.primitive_cylinder_add, band, vertices=16, radius=0.034, depth=0.008, location=(0, 0, z))
    add(bpy.ops.mesh.primitive_cylinder_add, steel, vertices=12, radius=0.016, depth=0.022, location=(0, 0, 0.053))
    add(bpy.ops.mesh.primitive_uv_sphere_add, amber, segments=8, ring_count=4, radius=0.006, location=(0, 0, 0.066))
    spoon = add(bpy.ops.mesh.primitive_cube_add, steel, size=1, location=(0.036, 0, 0.012))
    spoon.scale = (0.006, 0.016, 0.075)
    ring = add(bpy.ops.mesh.primitive_torus_add, steel, major_radius=0.011, minor_radius=0.0022,
               major_segments=10, minor_segments=4, location=(-0.022, 0, 0.058))
    ring.rotation_euler = (math.radians(90), 0, 0)
    bpy.ops.object.select_all(action="DESELECT")
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.ops.object.join()
    bpy.context.active_object.name = "throw.frag"
    export("throw.frag", {"mounts": {"grip": [0, 0, 0]}, "height": 0.09})


def main():
    for oid, w in WEAPONS.items():
        weapon(oid, w)
    for oid, t in THROWN.items():
        thrown(oid, t)
    frag()


main()
