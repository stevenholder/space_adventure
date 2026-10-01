#!/usr/bin/env node
/**
 * Finish a model our Blender generators exported (tools/bpy/*.py ->
 * art/build/*.raw.glb) into the asset the game ships, and record it in
 * art/manifest.json.
 *
 * Every model here is our own now; this used to adapt downloaded CC0 packs
 * (rescale, re-axis, bake colour to vertices, borrow animation clips), and
 * all of that went with the last Kenney asset. What is left:
 *
 *   - dedup and prune, keeping empty mount nodes,
 *   - record each mesh's surface (material) names, for `covers`,
 *   - add the recipe's mount empties,
 *   - simplify to the triangle budget if over it,
 *   - write the manifest row (tris, surfaces, flags, provenance).
 *
 * Usage:
 *   node tools/import_pack.mjs <recipe.json>       finish one recipe
 *   node tools/import_pack.mjs --selftest          run the checks below
 *
 * A recipe is JSON:
 *
 *   {
 *     "id":     "armor.suit.scout",          // art/manifest.json id
 *     "src":    "build/armor.suit.scout.raw.glb",
 *     "out":    "armor/suit_scout.glb",
 *     "budget": 5000,                        // max triangles
 *     "pbr":    true,                        // real materials, drawn as authored
 *     "mounts": { "probe": [0, 1.7, 0] },    // optional empties
 *     "covers": ["body/suit"],               // optional: surfaces this hides
 *     "arms":   true,                        // optional: first-person arms wear it
 *     "rig":    "animated",                  // optional manifest flag
 *     "license": "CC0", "author": "Space Adventure", "source_url": "tools/bpy/armor.py"
 *   }
 */
import { readFileSync, writeFileSync, mkdirSync, existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { NodeIO } from "@gltf-transform/core";
import { ALL_EXTENSIONS } from "@gltf-transform/extensions";
import { dedup, prune, weld, simplify } from "@gltf-transform/functions";
import { MeshoptSimplifier } from "meshoptimizer";

const artDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const io = new NodeIO().registerExtensions(ALL_EXTENSIONS);

// ---------------------------------------------------------------------------
// transforms
// ---------------------------------------------------------------------------

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

  // Housekeeping first: the Blender exporter can write duplicate accessors
  // and materials.
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

  // Surface names: the client hides a body's covered surfaces (`covers` on a
  // worn piece) by index, and this is the record of which index is which.
  // Godot keeps a glTF mesh's primitive order as its surface order.
  const surfaces = {};
  for (const mesh of doc.getRoot().listMeshes()) {
    const names = mesh.listPrimitives().map((p) => p.getMaterial()?.getName() ?? "");
    if (names.some((n) => n)) surfaces[mesh.getName()] = names;
  }

  if (!recipe.pbr) throw new Error(`${recipe.id}: every model is pbr now (the vertex-colour bake went with the Kenney packs)`);
  const mounted = addMounts(doc, recipe.mounts);

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

  return { id: recipe.id, out: recipe.out, tris, mounted, surfaces };
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
  if (result.surfaces && Object.keys(result.surfaces).length) row.surfaces = result.surfaces;
  else delete row.surfaces;
  if (recipe.covers) row.covers = recipe.covers;
  else delete row.covers;
  if (recipe.pbr) row.pbr = true;
  else delete row.pbr;
  // `arms`: a worn piece with parts on the arms (pauldrons, bracers) that the
  // first-person arms should wear too, whether or not it hides anything.
  if (recipe.arms) row.arms = true;
  else delete row.arms;
  if (recipe.license) row.license = recipe.license;
  if (recipe.source_url) row.source_url = recipe.source_url;
  if (recipe.author) row.author = recipe.author;
  if (recipe.rig) row.rig = recipe.rig;
  // The recipe is what reproduces the shipped file from the Blender export.
  row.source = recipe.recipe_path ?? row.source;

  writeFileSync(file, JSON.stringify(manifest, null, 2) + "\n");
}

// ---------------------------------------------------------------------------
// selftest
// ---------------------------------------------------------------------------

/**
 * The check, run against a model already in the tree so it needs no Blender:
 * re-finish art/chars/player.glb with a probe mount and assert the pipeline
 * keeps what the client depends on -- the real materials and their textures,
 * the surface names, the empty mount nodes, and every triangle.
 */
async function selftest() {
  const os = await import("node:os");
  const tmp = path.join(os.tmpdir(), `art-selftest-${process.pid}.glb`);

  // Counted from the source, not hardcoded: an assertion about the input is
  // measured from the input.
  const SRC = "chars/player.glb";
  const srcDoc = await io.read(path.resolve(artDir, SRC));
  const before = triangleCount(srcDoc);
  const srcMaterials = srcDoc.getRoot().listMaterials().length;
  let failures = 0;
  const check = (name, cond, detail = "") => {
    console.log(`${cond ? "PASS" : "FAIL"} ${name}${detail ? "  " + detail : ""}`);
    if (!cond) failures++;
  };

  const result = await importPack({
    id: "char.player", src: SRC, out: path.relative(artDir, tmp), pbr: true,
    mounts: { probe: [0, 1.7, 0] },
  });
  const root = (await io.read(tmp)).getRoot();

  check("materials kept", root.listMaterials().length === srcMaterials,
        `${root.listMaterials().length} of ${srcMaterials}`);
  check("textures kept", root.listTextures().length > 0, `got ${root.listTextures().length}`);
  check("surface names recorded", Object.values(result.surfaces).some((n) => n.includes("suit")),
        JSON.stringify(result.surfaces));
  const names = root.listNodes().map((n) => n.getName());
  // prune's default deletes childless meshless nodes, and every mount point
  // in this project is one: `eye` going missing costs the camera its mount.
  check("the empty mount node survives", names.includes("eye"));
  const probe = root.listNodes().find((n) => n.getName() === "probe");
  check("added mount is where the recipe put it",
        probe && Math.abs(probe.getTranslation()[1] - 1.7) < 1e-4, `y=${probe?.getTranslation()[1]}`);
  // Counted as drawn: dedup() may merge meshes without dropping a triangle.
  check("no geometry lost", result.tris === before, `${result.tris} tris, source has ${before}`);

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
console.log(`${result.id}: ${result.out}  ${result.tris} tris  mounts ${result.mounted}`);
