#!/usr/bin/env node
/**
 * Verify art/ assets against the manifest and the project budgets.
 *
 * For every asset in art/manifest.json:
 *   1. the file exists and parses as glTF 2.0 binary (Three.js GLTFLoader)
 *   2. the triangle count matches the manifest's `tris` exactly
 *   3. the triangle count is within the id-class budget
 *        char.* <= 1500, ship.* <= 2000, prop.* <= 500
 *        (see BUDGETS below for the current table; tool.* is a hand tool)
 *   4. the node-name contract holds:
 *        char.player -> eye, head, torso, arm.l, arm.r, leg.l, leg.r
 *                       (eye and head must be siblings: the client hides
 *                        head for the local player, so eye must not be
 *                        under it)
 *        ship.v1     -> seat.pilot, seat.passenger.0, seat.passenger.1
 *
 * Exit code 0 = all green, 1 = any failure.
 *
 * Usage: node tools/verify.mjs   (from art/, three.js installed)
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { Box3 } from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { Texture } from "three";

const artDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const manifest = JSON.parse(readFileSync(path.join(artDir, "manifest.json"), "utf8"));

// Triangle budgets, per id class.
//
// These were raised when the models stopped being generated box stacks. The
// old numbers (char 1500, ship 2000, prop 500) were set for a browser client
// delivered over the network, and that client is retired -- ROADMAP Phase 3.5
// drops browser delivery for a packaged native desktop build. A real CC0
// character lands around 2-5k triangles and there is no reason left to
// decimate it to 1500.
//
// They are still ENFORCED, and deliberately: the failure mode this guards is a
// 50k-tri pack model landing unnoticed because nothing counted it. Raising a
// budget is a decision; drifting past one is an accident.
// Phase 9 kit pieces carry `cell` ([w, d] in 4 m module-grid cells) and
// `height` in the manifest. The verify gate holds every such GLB inside its
// declared cells (+0.45 m skirt margin) and under its height: a piece that
// leaks past its cell WILL interpenetrate its neighbour when tiled, which is
// the exact clipping this kit exists to end.
const CELL = 4.0;
const CELL_MARGIN = 0.45;

const BUDGETS = [
  [/^char\./, 9000],
  [/^npc\./, 9000],
  [/^ship\./, 15000],
  [/^vehicle\./, 15000],
  [/^weapon\./, 3000],
  [/^tool\./, 1000],
  [/^struct\./, 1500],
  [/^prop\./, 1000],
  [/^armor\./, 5000],
  [/^pack\./, 800],
];

// Node-name contracts: the client mounts things by these names, so a rename
// in a generator is a silent runtime break. `eyeHeadSiblings` additionally
// asserts eye is not under head — hiding head for a local body must not hide
// the camera.
// Clip contracts. An asset whose manifest row says `rig: "animated"` must
// carry these, and every track in them must land on a node that exists --
// retargeting animation from another model is a rename away from producing
// clips that animate nothing, and a body that slides instead of walking looks
// exactly like a body that was never animated.
const CLIP_CONTRACTS = {
  // Gaits for every body, armed gaits, death, and the first-person set the
  // client plays on a second instance under the camera (ViewModel).
  animated: ["idle", "walk", "sprint", "die",
    "idle_armed", "walk_armed", "sprint_armed",
    "fp_idle", "fp_walk", "fp_sprint", "fp_ads", "fp_lower", "fp_unarmed", "fp_fire", "fp_reload"],
};

const NODE_CONTRACTS = {
  // The MakeHuman body (tools/bpy/human.py): game-engine bone names, both
  // hand mounts (grip in the right, barrel toward the left).
  "char.player": {
    nodes: ["eye", "head", "arms", "body", "hand.r", "hand.l", "upperarm_r", "lowerarm_r", "hand_r", "spine_03"],
    eyeHeadSiblings: true,
  },
  "npc.shopkeeper": {
    nodes: ["eye", "head", "arms", "body", "hand.r", "hand.l", "upperarm_r", "lowerarm_r", "hand_r", "spine_03"],
    eyeHeadSiblings: true,
  },
  "npc.dispatcher": {
    nodes: ["eye", "head", "arms", "body", "hand.r", "hand.l", "upperarm_r", "lowerarm_r", "hand_r", "spine_03"],
    eyeHeadSiblings: true,
  },
  // The hostiles carry the same layout on purpose: the client's nametag,
  // health-bar and animation code walks these names and does not care which
  // archetype it is looking at.
  "npc.grunt": {
    nodes: ["eye", "head", "arms", "body", "hand.r", "hand.l", "upperarm_r", "lowerarm_r", "hand_r", "spine_03"],
    eyeHeadSiblings: true,
  },
  "npc.gunner": {
    nodes: ["eye", "head", "arms", "body", "hand.r", "hand.l", "upperarm_r", "lowerarm_r", "hand_r", "spine_03"],
    eyeHeadSiblings: true,
  },
  "ship.v1": { nodes: ["seat.pilot", "seat.passenger.0", "seat.passenger.1"] },
  "vehicle.rover.v1": { nodes: ["seat.driver", "seat.passenger.0"] },
  "weapon.pulse": { nodes: ["grip", "muzzle", "fore", "sight", "front"] },
  "weapon.smg": { nodes: ["grip", "muzzle", "fore", "sight", "front"] },
  "weapon.dmr": { nodes: ["grip", "muzzle", "fore", "sight", "front"] },
  "weapon.sidearm": { nodes: ["grip", "muzzle", "fore", "sight", "front"] },
  "weapon.pulse.dune": { nodes: ["grip", "muzzle", "fore", "sight", "front"] },
  "weapon.smg.frost": { nodes: ["grip", "muzzle", "fore", "sight", "front"] },
  // Phase 12 hand tools ride the weapon mount path: the client lines `grip`
  // up with hand.r and the channel effect starts at `muzzle`.
  "tool.drill": { nodes: ["grip", "muzzle"] },
  "tool.cutter": { nodes: ["grip", "muzzle"] },
  "prop.target": { nodes: ["plate"] },
};

// Optional id argument: verify one asset. Lets an asset be verified while its
// siblings do not exist yet, so a generator task is not gated on the rest of
// its wave.
const onlyId = process.argv[2] ?? null;

function budgetFor(id) {
  for (const [re, n] of BUDGETS) if (re.test(id)) return n;
  return null;
}

function countTriangles(object) {
  let n = 0;
  object.traverse((o) => {
    if (!o.isMesh) return;
    const g = o.geometry;
    n += g.index ? g.index.count / 3 : g.attributes.position.count / 3;
  });
  return n;
}

function findByName(root, name) {
  const out = [];
  root.traverse((o) => { if (o.name === name) out.push(o); });
  return out;
}

// A byte COLOR_0 must say normalized (glTF 2.0). Without it Godot reads
// 0..255 as floats and the mesh renders pure white -- the kit did, C106.
// three.js normalises on its own, so this reads the raw accessor JSON.
function rawColorProblems(file) {
  const buf = readFileSync(path.join(artDir, file));
  const jsonLen = buf.readUInt32LE(12);
  const g = JSON.parse(buf.toString("utf8", 20, 20 + jsonLen));
  const out = [];
  for (const mesh of g.meshes ?? []) {
    for (const prim of mesh.primitives ?? []) {
      const idx = prim.attributes?.COLOR_0;
      if (idx === undefined) continue;
      const a = g.accessors[idx];
      if ((a.componentType === 5121 || a.componentType === 5123) && !a.normalized)
        out.push(`COLOR_0 is ${a.componentType === 5121 ? "u8" : "u16"} but not normalized (renders white)`);
    }
  }
  return out;
}

const loader = new GLTFLoader();
// Textures are not checked here, and decoding an image wants a browser
// (`self`, ImageBitmap): every texture loads as a blank one.
loader.register(() => ({ name: "stub-textures", loadTexture: () => Promise.resolve(new Texture()) }));
function loadGlb(file) {
  const buf = readFileSync(path.join(artDir, file));
  const ab = buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength);
  return new Promise((resolve, reject) => {
    loader.parse(ab, file, resolve, reject);
  });
}

const failures = [];
const rows = [];

const selected = onlyId
  ? manifest.assets.filter((a) => a.id === onlyId)
  : manifest.assets;
if (onlyId && selected.length === 0) {
  console.error(`no manifest asset with id ${onlyId}`);
  process.exit(1);
}

for (const asset of selected) {
  const budget = budgetFor(asset.id);
  if (budget === null) {
    failures.push(`${asset.id}: no budget rule for id class`);
    continue;
  }
  let gltf;
  try {
    gltf = await loadGlb(asset.file);
  } catch (e) {
    failures.push(`${asset.id}: failed to load ${asset.file}: ${e.message ?? e}`);
    continue;
  }

  const tris = countTriangles(gltf.scene);
  const problems = [];

  // Clips, for anything declaring a rig. GLTFLoader hands animations back as
  // THREE.AnimationClip, and a track name is "<node>.<property>" -- so a
  // track whose node is missing from the scene is one that will silently do
  // nothing at runtime.
  const rig = asset.rig ?? null;
  if (rig && CLIP_CONTRACTS[rig]) {
    const clips = new Map((gltf.animations ?? []).map((c) => [c.name, c]));
    for (const want of CLIP_CONTRACTS[rig]) {
      const clip = clips.get(want);
      if (!clip) {
        problems.push(`missing clip "${want}"`);
        continue;
      }
      if (clip.tracks.length === 0) problems.push(`clip "${want}" has no tracks`);
      for (const track of clip.tracks) {
        const node = track.name.split(".")[0];
        if (findByName(gltf.scene, node).length === 0)
          problems.push(`clip "${want}" targets missing node "${node}"`);
      }
    }
  }


  if (tris !== asset.tris)
    problems.push(`manifest tris ${asset.tris} != counted ${tris}`);
  if (tris > budget)
    problems.push(`${tris} tris exceeds budget ${budget}`);

  // Cell bounds, for kit pieces (Phase 9). Bounding box computed from the
  // actual geometry; the piece must sit centred on its cells and inside them.
  if (asset.cell) {
    const b = new Box3().setFromObject(gltf.scene);
    const [cw, cd] = asset.cell;
    const hx = (cw * CELL) / 2 + CELL_MARGIN;
    const hz = (cd * CELL) / 2 + CELL_MARGIN;
    if (b.min.x < -hx || b.max.x > hx || b.min.z < -hz || b.max.z > hz)
      problems.push(
        `leaks its ${cw}x${cd} cell: x [${b.min.x.toFixed(2)}, ${b.max.x.toFixed(2)}] ` +
        `z [${b.min.z.toFixed(2)}, ${b.max.z.toFixed(2)}] vs ±${hx.toFixed(2)}/±${hz.toFixed(2)}`);
    if (b.min.y < -0.35) problems.push(`skirt too deep: min y ${b.min.y.toFixed(2)}`);
    problems.push(...rawColorProblems(asset.file));
    if (asset.height && b.max.y > asset.height)
      problems.push(`taller than declared: ${b.max.y.toFixed(2)} > ${asset.height}`);
  }
  // Node names are checked against the original glTF JSON: three.js's
  // GLTFLoader sanitizes Object3D names on load (strips the dots in
  // arm.l / seat.pilot), but the .glb file is the contract.
  const names = new Set(gltf.parser.json.nodes.map((n) => n.name));
  const contract = NODE_CONTRACTS[asset.id];
  if (contract) {
    for (const need of contract.nodes)
      if (!names.has(need)) problems.push(`missing node ${need}`);
    if (contract.eyeHeadSiblings && names.has("eye") && names.has("head")) {
      const eye = findByName(gltf.scene, "eye")[0];
      if (eye.parent && eye.parent.name === "head")
        problems.push("eye is a child of head: hiding head would hide the camera");
    }
  }

  rows.push({ id: asset.id, file: asset.file, tris, budget, ok: problems.length === 0 });
  for (const p of problems) failures.push(`${asset.id}: ${p}`);
}

console.log("asset verification (Three.js GLTFLoader)");
console.log("-".repeat(58));
for (const r of rows) {
  console.log(
    `${r.ok ? "PASS" : "FAIL"}  ${r.id.padEnd(14)} ${String(r.tris).padStart(5)}` +
    ` / ${String(r.budget).padStart(5)} tris  ${r.file}`
  );
}
console.log("-".repeat(58));
if (failures.length) {
  console.error(`FAIL: ${failures.length} problem(s)`);
  for (const f of failures) console.error("  " + f);
  process.exit(1);
}
console.log(`PASS: ${rows.length}/${rows.length} assets load, match manifest, within budget`);
