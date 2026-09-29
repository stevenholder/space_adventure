#!/usr/bin/env node
/**
 * Turn a downloaded CC0 model into an asset this game can actually use.
 *
 * Everything in art/ up to now was generated: a Python script stacking
 * axis-aligned boxes, exact by construction, 180 triangles for a rifle. That
 * ceiling is the reason this file exists. Real assets come from CC0 packs
 * (Kenney, Quaternius, Poly Pizza) instead, and a downloaded model never
 * arrives in the shape this project needs — wrong scale, wrong origin, wrong
 * forward axis, textured materials, and node names nobody here mounts things
 * by. This applies the fixups, deterministically, from a recipe.
 *
 * THE ONE THAT MATTERS IS `bake`. The client draws every model with ONE
 * material that reads colour out of the vertex stream (Godot:
 * StandardMaterial3D with VertexColorUseAsAlbedo, built in C#, no shader
 * resources in the project). This started as a Unity constraint — Unity
 * stripped every shader nothing referenced, so per-model PBR materials went
 * MAGENTA in packaged builds — and it stays because the Godot client
 * (AssetRegistry) still reads COLOR_0 and nothing else. So the colour is baked
 * down into COLOR_0 here, at import, and the client never needs a second
 * material.
 *
 * Usage:
 *   node tools/import_pack.mjs <recipe.json>       apply one recipe
 *   node tools/import_pack.mjs --selftest          run the checks below
 *
 * A recipe is JSON:
 *
 *   {
 *     "id":     "npc.grunt",              // art/manifest.json id
 *     "src":    "vendor/quaternius/Grunt.glb",
 *     "out":    "chars/grunt.glb",
 *     "height": 1.8,                      // scale so the model is this tall
 *     "ground": true,                     // put the origin between the feet
 *     "yaw":    180,                      // degrees, to bring forward to -Z
 *     "budget": 6000,                     // max triangles
 *     "rename": { "Hand_R": "hand.r" },   // node contract
 *     "license": "CC0",
 *     "source_url": "https://quaternius.com/...",
 *     "author": "Quaternius"
 *   }
 *
 * Paths in a recipe are relative to art/. `vendor/` is gitignored: the packs
 * are re-downloadable from `source_url` and do not belong in git, but the
 * fixed-up output does — it is what the game ships.
 */
import { readFileSync, writeFileSync, mkdirSync, existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { NodeIO, Document } from "@gltf-transform/core";
import { ALL_EXTENSIONS } from "@gltf-transform/extensions";
import { dedup, join, prune, weld, simplify } from "@gltf-transform/functions";
import { MeshoptSimplifier } from "meshoptimizer";

const artDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const io = new NodeIO().registerExtensions(ALL_EXTENSIONS);

// ---------------------------------------------------------------------------
// transforms
// ---------------------------------------------------------------------------

/**
 * Fold every material's base colour into the mesh's COLOR_0 stream, then throw
 * the materials away and leave one untextured opaque material behind.
 *
 * A pack model carries its colour in `baseColorFactor` (flat-shaded packs) or
 * in a texture atlas (most Kenney kits). Only the factor is read here: a
 * texture would need the UV sampled per vertex, and the vertex-colour renderer
 * this feeds has nowhere to put a texture anyway. A model whose colour lives
 * ONLY in an atlas therefore comes out flat grey, which is visible immediately
 * rather than subtly wrong — see `--selftest`.
 *
 * COLOR_0 is multiplied into the base colour by the glTF spec, so an existing
 * vertex colour is preserved and tinted rather than overwritten.
 *
 * `tint` multiplies the whole palette on the way through. Two enemy archetypes
 * cut from the same source model have to be told apart at a glance -- the box
 * models did it by tinting one red and one purple (Models.cs Grunt/Gunner) --
 * and re-tinting is the only recolour available once the colour has been baked
 * into vertices. It scales the source palette rather than replacing it, so the
 * model keeps its internal contrast instead of going flat.
 */
function bakeVertexColors(doc, tint) {
  const root = doc.getRoot();
  const t = tint ?? [1, 1, 1];
  let baked = 0;

  for (const mesh of root.listMeshes()) {
    for (const prim of mesh.listPrimitives()) {
      const material = prim.getMaterial();
      const factor = material ? material.getBaseColorFactor() : [1, 1, 1, 1];

      const position = prim.getAttribute("POSITION");
      if (!position) continue;
      const count = position.getCount();

      const existing = prim.getAttribute("COLOR_0");
      const out = new Float32Array(count * 4);
      for (let i = 0; i < count; i++) {
        let r = factor[0] * t[0], g = factor[1] * t[1], b = factor[2] * t[2], a = factor[3];
        // COLOR_0 is normalised in glTF: anything outside [0,1] is out of
        // spec. A tint above 1.0 is the easy way to produce one, and it does
        // not fail loudly -- it sails through the exporter and only surfaces
        // downstream, as a renderer throwing on a >255 colour byte or a
        // clamp appearing somewhere in the engine that nobody chose.
        const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
        if (existing) {
          const e = [0, 0, 0, 1];
          existing.getElement(i, e);
          // A 3-component COLOR_0 leaves alpha at the initialised 1.
          r *= e[0]; g *= e[1]; b *= e[2];
          if (existing.getElementSize() === 4) a *= e[3];
        }
        out.set([clamp01(r), clamp01(g), clamp01(b), clamp01(a)], i * 4);
      }

      const accessor = doc
        .createAccessor()
        .setType("VEC4")
        .setArray(out)
        .setBuffer(root.listBuffers()[0]);
      prim.setAttribute("COLOR_0", accessor);
      baked++;
    }
  }

  // One material for everything, matching the client's single material.
  const flat = doc.createMaterial("opaque").setDoubleSided(true);
  for (const mesh of root.listMeshes()) {
    for (const prim of mesh.listPrimitives()) prim.setMaterial(flat);
  }
  for (const m of root.listMaterials()) if (m !== flat) m.dispose();
  for (const t of root.listTextures()) t.dispose();

  return baked;
}

/** World-space bounding box over every mesh in the scene, transforms applied. */
function bounds(doc) {
  const min = [Infinity, Infinity, Infinity];
  const max = [-Infinity, -Infinity, -Infinity];

  const visit = (node, parent) => {
    const m = mul(parent, node.getWorldMatrix ? null : null) || null;
    // gltf-transform exposes a node's own matrix; compose it with the parent's.
    const local = node.getMatrix();
    const world = parent ? mul(parent, local) : local;

    const mesh = node.getMesh();
    if (mesh) {
      for (const prim of mesh.listPrimitives()) {
        const pos = prim.getAttribute("POSITION");
        if (!pos) continue;
        const v = [0, 0, 0];
        for (let i = 0; i < pos.getCount(); i++) {
          pos.getElement(i, v);
          const p = apply(world, v);
          for (let k = 0; k < 3; k++) {
            if (p[k] < min[k]) min[k] = p[k];
            if (p[k] > max[k]) max[k] = p[k];
          }
        }
      }
    }
    for (const child of node.listChildren()) visit(child, world);
  };

  for (const scene of doc.getRoot().listScenes()) {
    for (const node of scene.listChildren()) visit(node, null);
  }
  return { min, max };
}

/** Column-major 4x4 multiply, matching glTF's matrix layout. */
function mul(a, b) {
  if (!a) return b;
  if (!b) return a;
  const out = new Array(16).fill(0);
  for (let c = 0; c < 4; c++) {
    for (let r = 0; r < 4; r++) {
      let s = 0;
      for (let k = 0; k < 4; k++) s += a[k * 4 + r] * b[c * 4 + k];
      out[c * 4 + r] = s;
    }
  }
  return out;
}

function apply(m, v) {
  if (!m) return v.slice();
  return [
    m[0] * v[0] + m[4] * v[1] + m[8] * v[2] + m[12],
    m[1] * v[0] + m[5] * v[1] + m[9] * v[2] + m[13],
    m[2] * v[0] + m[6] * v[1] + m[10] * v[2] + m[14],
  ];
}

/**
 * Scale to a target height, drop the model onto Y=0 and centre it on XZ.
 *
 * Height, not a scale factor, because the number that matters is fixed by the
 * SERVER: items.json gives every humanoid a capsule of radius 0.35 and height
 * 1.8, and that capsule is what shots are resolved against. A model that is
 * not 1.8 m teaches the wrong aim — you learn to shoot at the shape you see
 * while the server tests the capsule. Same reasoning as Models.cs:3-6.
 *
 * Applied by wrapping the scene in one node rather than rewriting vertex data,
 * so an armature and its skinned mesh cannot drift apart.
 */
function fit(doc, { height, axis = "y", ground = true, yaw = 0 }) {
  const { min, max } = bounds(doc);
  if (!isFinite(min[1]) || !isFinite(max[1])) return { scale: 1 };

  // `height` is measured along `axis`, because "how big is it" is a different
  // question per asset class. A humanoid is sized by how tall it is (the
  // server's capsule is 1.8 m). A rifle is sized by how LONG it is -- scaling
  // a gun by its height would size it by its sights.
  const k = { x: 0, y: 1, z: 2 }[axis];
  const h = max[k] - min[k];
  const scale = height && h > 1e-9 ? height / h : 1;

  const cx = ((min[0] + max[0]) / 2) * scale;
  const cz = ((min[2] + max[2]) / 2) * scale;
  const dy = ground ? -min[1] * scale : 0;

  const rad = (yaw * Math.PI) / 180;
  const cos = Math.cos(rad);
  const sin = Math.sin(rad);

  for (const scene of doc.getRoot().listScenes()) {
    const wrapper = doc
      .createNode("fit")
      .setScale([scale, scale, scale])
      .setRotation([0, Math.sin(rad / 2), 0, Math.cos(rad / 2)])
      // Undo the centring in the wrapper's own frame, after rotation.
      .setTranslation([-(cx * cos + cz * sin), dy, -(-cx * sin + cz * cos)]);

    for (const child of scene.listChildren()) {
      scene.removeChild(child);
      wrapper.addChild(child);
    }
    scene.addChild(wrapper);
  }
  return { scale };
}

/**
 * Add the empty mount nodes the client mounts things by, at explicit positions
 * in the FINISHED model's coordinates.
 *
 * A downloaded model never has these — no pack ships a node called `eye` at
 * your eye height — and they are not decoration: `eye` is where the camera
 * goes, `grip` and `muzzle` are where a weapon is held and where its tracer
 * starts, `seat.pilot` is where a pilot's view sits. art/README.md calls the
 * eye "the view point, so eye height lives in the model".
 *
 * Added at the SCENE ROOT, after `fit`, so the coordinates you write in a
 * recipe are the ones you can measure off the finished 1.8 m model rather than
 * whatever scale the pack happened to use. That also puts `eye` outside the
 * subtree holding `head`, which is the contract verify.mjs checks: the local
 * player hides its own head, and an eye parented under it would take the
 * camera along.
 *
 * A mount can instead name a PARENT, and then it is added under that node and
 * MOVES WITH IT. `hand.r` under `arm.r` is the case that needs it: a weapon
 * parented to the scene root would hang in the air while the arm swings past
 * it. Two consequences follow, and both are easy to get wrong:
 *
 *   - Its position is in the PARENT's local frame, not the finished model's.
 *     `fit` scales the whole scene from above, so a parented mount inherits
 *     that scale; write the number you measure off the SOURCE geometry, not
 *     off the 1.8 m result.
 *   - Anything hidden by hiding the parent hides it too. That is why `eye` is
 *     a root mount and not parented under `head` -- the local player draws its
 *     own head shadows-only.
 */
function addMounts(doc, mounts) {
  if (!mounts) return 0;

  const byName = new Map();
  for (const node of doc.getRoot().listNodes()) byName.set(node.getName(), node);

  let n = 0;
  for (const [name, spec] of Object.entries(mounts)) {
    const pos = Array.isArray(spec) ? spec : spec.pos;
    const parentName = Array.isArray(spec) ? null : spec.parent;
    const node = doc.createNode(name).setTranslation(pos);

    if (parentName) {
      const parent = byName.get(parentName);
      if (!parent) throw new Error(`mount ${name}: no node named ${parentName} to parent it to`);
      parent.addChild(node);
    } else {
      for (const scene of doc.getRoot().listScenes()) scene.addChild(node);
    }
    n++;
  }
  return n;
}

/**
 * Copy animation clips off a DIFFERENT model and re-point them at this one.
 *
 * The good-looking CC0 characters and the animated CC0 characters are not the
 * same characters. Kenney's Space Kit astronaut is the right look for this
 * game and ships no animation at all; Kenney's Blocky Characters ship 27 clips
 * -- idle, walk, sprint, die, holding-both-shoot -- on six boxes that would be
 * a downgrade to look at. Retargeting takes the clips from one and leaves the
 * geometry of the other.
 *
 * That works here because both packs use the same rig convention, which was
 * checked before relying on it: limb nodes sit AT THE JOINT and their mesh
 * hangs off the node origin (a leg's vertices run from y=-1 to y=0 below its
 * hip node). Rotating such a node swings the limb from the shoulder or the
 * hip. A pack that instead baked limb positions into vertices and left every
 * node at the origin would spin its arms around the character's feet, and no
 * amount of renaming would fix it.
 *
 * ROTATION CHANNELS ONLY, and that is the other thing that makes this safe.
 * The source clips also carry a translation track on `root` -- the vertical
 * bob of a walk -- authored in units where the character is 2.2 tall. This
 * model is 0.79 before it is fitted to 1.8. A translation is in metres and
 * does not scale with the model, so importing that track would bob the
 * astronaut by most of its own height. A rotation is scale-free and means the
 * same thing on any rig, so rotations come across and nothing else does. The
 * cost is a walk with no bob, which is a small loss next to one that pogos.
 */
async function retargetAnimations(doc, spec) {
  if (!spec) return { clips: 0, channels: 0, dropped: 0 };

  const src = await io.read(path.resolve(artDir, spec.src));
  const buffer = doc.getRoot().listBuffers()[0];

  // Destination nodes by name, so a mapped target that does not exist is
  // caught here rather than becoming a channel that animates nothing.
  const byName = new Map();
  for (const node of doc.getRoot().listNodes()) byName.set(node.getName(), node);

  const keep = spec.keep ? new Set(spec.keep) : null;
  let clips = 0, channels = 0, dropped = 0;

  for (const anim of src.getRoot().listAnimations()) {
    const name = anim.getName();
    if (keep && !keep.has(name)) continue;

    const out = doc.createAnimation(name);
    let kept = 0;

    for (const channel of anim.listChannels()) {
      if (channel.getTargetPath() !== "rotation") { dropped++; continue; }

      const from = channel.getTargetNode()?.getName();
      const to = from != null ? spec.map?.[from] : undefined;
      const node = to != null ? byName.get(to) : undefined;
      if (!node) { dropped++; continue; }

      const s = channel.getSampler();
      const input = doc
        .createAccessor()
        .setType("SCALAR")
        .setArray(new Float32Array(s.getInput().getArray()))
        .setBuffer(buffer);
      const output = doc
        .createAccessor()
        .setType("VEC4")
        .setArray(new Float32Array(s.getOutput().getArray()))
        .setBuffer(buffer);

      const sampler = doc
        .createAnimationSampler()
        .setInterpolation(s.getInterpolation())
        .setInput(input)
        .setOutput(output);

      out.addSampler(sampler);
      out.addChannel(
        doc.createAnimationChannel().setTargetNode(node).setTargetPath("rotation").setSampler(sampler),
      );
      kept++;
    }

    if (kept === 0) { out.dispose(); continue; }
    clips++;
    channels += kept;
  }

  return { clips, channels, dropped };
}

/** Apply the node-name contract: `grip`, `muzzle`, `hand.r`, `seat.pilot`. */
function renameNodes(doc, map) {
  if (!map) return 0;
  let n = 0;
  for (const node of doc.getRoot().listNodes()) {
    const to = map[node.getName()];
    if (to) {
      node.setName(to);
      n++;
    }
  }
  return n;
}

/**
 * Triangles as DRAWN, counted by walking the scene graph — so a mesh two nodes
 * share counts twice, once per instance.
 *
 * That definition is not a choice, it is verify.mjs's: it traverses the
 * three.js scene and sums every `isMesh` it meets (tools/verify.mjs:66), then
 * asserts the manifest's `tris` equals what it counted. Counting unique meshes
 * here instead would disagree with it the moment `dedup()` merges two
 * identical boxes — the import would write 168, verify would count 192, and
 * the failure would look like a corrupt asset rather than two tools measuring
 * different things.
 */
function triangleCount(doc) {
  let tris = 0;
  const visit = (node) => {
    const mesh = node.getMesh();
    if (mesh) {
      for (const prim of mesh.listPrimitives()) {
        const indices = prim.getIndices();
        const count = indices ? indices.getCount() : prim.getAttribute("POSITION")?.getCount() ?? 0;
        tris += count / 3;
      }
    }
    for (const child of node.listChildren()) visit(child);
  };
  for (const scene of doc.getRoot().listScenes()) {
    for (const node of scene.listChildren()) visit(node);
  }
  return Math.round(tris);
}

// ---------------------------------------------------------------------------
// the pipeline
// ---------------------------------------------------------------------------

export async function importPack(recipe) {
  const src = path.resolve(artDir, recipe.src);
  if (!existsSync(src)) throw new Error(`source not found: ${src}`);

  const doc = await io.read(src);

  // Housekeeping first: a pack model routinely ships duplicate accessors and
  // materials.
  //
  // Two tempting cleanups are deliberately NOT here, because both destroy the
  // node-name contract this whole pipeline exists to satisfy:
  //
  //   flatten()  collapses the hierarchy and bakes transforms into meshes. The
  //              limb pivots ARE the hierarchy — art/README.md: "a shoulder
  //              placed at the wrist swings the arm from the wrong end".
  //   keepLeaves:false  (prune's default) deletes childless nodes with no
  //              mesh. Every mount point in this project is exactly that: an
  //              empty. `eye` is where the camera goes, `grip` and `muzzle`
  //              are where the weapon is held and fired from, `seat.pilot` is
  //              where a pilot sits. Pruning them leaves a model that loads
  //              fine and mounts nothing.
  await doc.transform(dedup(), prune({ keepLeaves: true }));

  const baked = bakeVertexColors(doc, recipe.tint);
  const { scale } = fit(doc, recipe);
  const renamed = renameNodes(doc, recipe.rename);
  const mounted = addMounts(doc, recipe.mounts);
  // After the renames: the map in a recipe is written in terms of this
  // project's node names, not the source pack's.
  const anim = await retargetAnimations(doc, recipe.animations);

  // `merge` collapses the model to ONE mesh, for props the client draws with
  // Graphics.RenderMeshInstanced.
  //
  // That path takes a single Mesh and a list of transforms, so a model split
  // across several meshes gets only its first one drawn -- four hundred rocks
  // rendering their first third and nothing saying why. AssetRegistry warns
  // when it has to pick, and this is what stops it having to: after the bake
  // every primitive shares one material, so they join cleanly.
  //
  // Opt-in, NOT the default: joining a character would weld the limbs into one
  // mesh and take its animation with them. Only props that are instanced ask
  // for it.
  if (recipe.merge) await doc.transform(join({ keepNamed: false }));

  // Weld before simplify: meshoptimizer needs shared vertices to collapse
  // edges, and a flat-shaded export has none.
  let tris = triangleCount(doc);
  if (recipe.budget && tris > recipe.budget) {
    await MeshoptSimplifier.ready;
    await doc.transform(
      weld(),
      simplify({ simplifier: MeshoptSimplifier, ratio: recipe.budget / tris, error: 0.01 }),
    );
    tris = triangleCount(doc);
  }

  const out = path.resolve(artDir, recipe.out);
  mkdirSync(path.dirname(out), { recursive: true });
  await io.write(out, doc);

  return { id: recipe.id, out: recipe.out, tris, scale, baked, renamed, mounted, anim };
}

/**
 * Write the import's result back into art/manifest.json, which is the contract
 * `verify.mjs` and the Godot client both read. `tris` has to be exact — verify
 * compares it against the file — and the provenance fields are the record of
 * where a model came from and under what licence.
 */
export function updateManifest(result, recipe) {
  const file = path.join(artDir, "manifest.json");
  const manifest = JSON.parse(readFileSync(file, "utf8"));
  const row = manifest.assets.find((a) => a.id === recipe.id);
  if (!row) throw new Error(`manifest has no id ${recipe.id} (adding one is a deliberate change)`);

  row.file = recipe.out;
  row.tris = result.tris;
  if (recipe.license) row.license = recipe.license;
  if (recipe.source_url) row.source_url = recipe.source_url;
  if (recipe.author) row.author = recipe.author;
  if (recipe.rig) row.rig = recipe.rig;
  // `source` used to name the generator script that built the model. An
  // imported asset has no generator; the recipe is what reproduces it.
  row.source = recipe.recipe_path ?? row.source;

  writeFileSync(file, JSON.stringify(manifest, null, 2) + "\n");
}

// ---------------------------------------------------------------------------
// selftest
// ---------------------------------------------------------------------------

/**
 * The check, run against a model already in the tree so it needs no download.
 *
 * It reimports art/chars/player.glb at a deliberately WRONG scale and asserts
 * the pipeline puts it right: one material, no textures, COLOR_0 on every
 * primitive, 1.8 m tall, feet on Y=0, and the renamed node present. If any of
 * those regress, a packaged build renders magenta or the player stands the
 * wrong height against the server's capsule — both are expensive to notice
 * any later than here.
 */
async function selftest() {
  const os = await import("node:os");
  const tmp = path.join(os.tmpdir(), `art-selftest-${process.pid}.glb`);

  // Counted from the source rather than hardcoded. The obvious version of this
  // check asserted a literal 192, which was the triangle count of the
  // generated box player -- and then the pipeline replaced that very file with
  // an imported model and the check failed on its own success. An assertion
  // about "the input" must be measured from the input.
  const SRC = "chars/player.glb";
  const before = triangleCount(await io.read(path.resolve(artDir, SRC)));
  let failures = 0;
  const check = (name, cond, detail = "") => {
    console.log(`${cond ? "PASS" : "FAIL"} ${name}${detail ? "  " + detail : ""}`);
    if (!cond) failures++;
  };

  const result = await importPack({
    id: "char.player",
    src: SRC,
    out: path.relative(artDir, tmp),
    height: 1.8,
    ground: true,
    rename: { torso: "chest" },
    mounts: { probe: [0, 1.7, 0] },
  });

  const doc = await io.read(tmp);
  const root = doc.getRoot();

  check("exactly one material", root.listMaterials().length === 1,
        `got ${root.listMaterials().length}`);
  check("no textures survive the bake", root.listTextures().length === 0,
        `got ${root.listTextures().length}`);

  let prims = 0, coloured = 0;
  for (const mesh of root.listMeshes()) {
    for (const prim of mesh.listPrimitives()) {
      prims++;
      if (prim.getAttribute("COLOR_0")) coloured++;
    }
  }
  check("every primitive carries COLOR_0", prims > 0 && coloured === prims,
        `${coloured}/${prims}`);

  const { min, max } = bounds(doc);
  const height = max[1] - min[1];
  check("scaled to 1.8 m", Math.abs(height - 1.8) < 1e-3, `got ${height.toFixed(4)}`);
  check("feet sit on Y=0", Math.abs(min[1]) < 1e-3, `got ${min[1].toFixed(4)}`);

  const names = root.listNodes().map((n) => n.getName());
  check("rename applied", names.includes("chest") && !names.includes("torso"));

  // The regression this catches: prune's default deletes childless meshless
  // nodes, and every mount point in this project is one. `eye` going missing
  // costs the camera its mount and nothing errors.
  check("the empty mount node survives", names.includes("eye"),
        `nodes: ${names.join(" ")}`);
  check("added mount is present", names.includes("probe"));

  // The mount must land where the recipe put it, in FINISHED model units --
  // that is the whole point of adding it after the scale is applied.
  const probe = root.listNodes().find((n) => n.getName() === "probe");
  check("added mount is at the position asked for",
        probe && Math.abs(probe.getTranslation()[1] - 1.7) < 1e-4,
        `y=${probe?.getTranslation()[1]}`);
  // Counted as drawn, not as stored: dedup() legitimately merges identical
  // meshes, which drops the MESH count without dropping a single triangle off
  // the screen. This is the number verify.mjs will independently count.
  check("no geometry lost", result.tris === before,
        `${result.tris} tris, source has ${before}`);
  check("reports a triangle count", result.tris > 0, `got ${result.tris}`);

  // Colour must SURVIVE the bake: a model whose vertices all come out black or
  // all identical has lost the thing the whole renderer reads.
  const seen = new Set();
  for (const mesh of root.listMeshes()) {
    for (const prim of mesh.listPrimitives()) {
      const c = prim.getAttribute("COLOR_0");
      const v = [0, 0, 0, 0];
      for (let i = 0; i < Math.min(c.getCount(), 500); i++) {
        c.getElement(i, v);
        seen.add(v.slice(0, 3).map((x) => x.toFixed(3)).join(","));
      }
    }
  }
  check("more than one colour survives", seen.size > 1, `${seen.size} distinct`);

  console.log(failures === 0 ? "\nOVERALL: PASS" : `\nOVERALL: FAIL (${failures})`);
  return failures;
}

// ---------------------------------------------------------------------------

const arg = process.argv[2];
if (!arg) {
  console.error("usage: import_pack.mjs <recipe.json> | --selftest");
  process.exit(2);
}
if (arg === "--selftest") {
  process.exit((await selftest()) === 0 ? 0 : 1);
}

const recipePath = path.resolve(process.cwd(), arg);
const recipe = JSON.parse(readFileSync(recipePath, "utf8"));
recipe.recipe_path = path.relative(artDir, recipePath);
const result = await importPack(recipe);
updateManifest(result, recipe);
console.log(
  `${result.id}: ${result.out}  ${result.tris} tris  scale x${result.scale.toFixed(3)}` +
  `  baked ${result.baked} prims  renamed ${result.renamed}  mounts ${result.mounted}` +
  (result.anim.clips ? `  clips ${result.anim.clips} (${result.anim.channels} tracks)` : ""),
);
