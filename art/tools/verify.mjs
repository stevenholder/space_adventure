#!/usr/bin/env node
/**
 * Verify art/ assets against the manifest and the project budgets.
 *
 * For every asset in art/manifest.json:
 *   1. the file exists and parses as glTF 2.0 binary (Three.js GLTFLoader)
 *   2. the triangle count matches the manifest's `tris` exactly
 *   3. the triangle count is within the id-class budget
 *        char.* <= 1500, ship.* <= 2000, prop.* <= 500
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
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";

const artDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const manifest = JSON.parse(readFileSync(path.join(artDir, "manifest.json"), "utf8"));

const BUDGETS = [
  [/^char\./, 1500],
  [/^ship\./, 2000],
  [/^prop\./, 500],
];

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

const loader = new GLTFLoader();
function loadGlb(file) {
  const buf = readFileSync(path.join(artDir, file));
  const ab = buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength);
  return new Promise((resolve, reject) => {
    loader.parse(ab, file, resolve, reject);
  });
}

const failures = [];
const rows = [];

for (const asset of manifest.assets) {
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

  if (tris !== asset.tris)
    problems.push(`manifest tris ${asset.tris} != counted ${tris}`);
  if (tris > budget)
    problems.push(`${tris} tris exceeds budget ${budget}`);
  // Node names are checked against the original glTF JSON: three.js's
  // GLTFLoader sanitizes Object3D names on load (strips the dots in
  // arm.l / seat.pilot), but the .glb file is the contract.
  const names = new Set(gltf.parser.json.nodes.map((n) => n.name));
  if (asset.id === "char.player") {
    for (const need of ["eye", "head", "torso", "arm.l", "arm.r", "leg.l", "leg.r"])
      if (!names.has(need)) problems.push(`missing node ${need}`);
    if (names.has("eye") && names.has("head")) {
      const eye = findByName(gltf.scene, "eye")[0];
      if (eye.parent && eye.parent.name === "head")
        problems.push("eye is a child of head: hiding head would hide the camera");
    }
  }
  if (asset.id === "ship.v1") {
    for (const need of ["seat.pilot", "seat.passenger.0", "seat.passenger.1"])
      if (!names.has(need)) problems.push(`missing node ${need}`);
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
