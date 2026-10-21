#!/usr/bin/env node
/**
 * Finish the world props tools/bpy/props_vendor.py re-framed from vendored
 * packs (build/props/<id>.raw.glb + <id>.json) into props/<name>.glb, and
 * write their manifest rows (created here: this list IS the decision to add
 * them).
 *
 *   node tools/props_vendor.mjs          (npm run props:vendor runs Blender first)
 */
import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { importPack, updateManifest } from "./import_pack.mjs";

const artDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const MEGAKIT = {
  license: "CC0",
  author: "Quaternius",
  source_url: "https://quaternius.com/packs/modularscifimegakit.html",
};

const PROPS = {
  "prop.chest": {
    out: "props/chest.glb",
    pack: MEGAKIT,
    describe: (m) =>
      `Placeable storage chest (Phase 23), the Quaternius Modular SciFi MegaKit's Props/Prop_Chest.gltf ` +
      `(red trim-sheet PBR, textures capped ${m.textures[0].split(" ")[1]}), re-framed by tools/bpy/props_vendor.py. ` +
      `Base at origin, up +Y, latch faces -Z; ${m.width} x ${m.depth} m, ${m.height} m tall. ` +
      `Nodes \`chest\` (the body) and \`lid\`, the lid's origin on its hinge at the back (+Z), ` +
      `[${m.pivots.lid.join(", ")}]: rotate it about X to open.`,
  },
  "prop.dock": {
    out: "props/dock.glb",
    budget: 3000,
    pack: MEGAKIT,
    describe: (m) =>
      `The dock (Phase 24), composed by tools/bpy/props_vendor.py from Quaternius Modular SciFi MegaKit pieces ` +
      `(Platforms/Platform_Round1 on a Platform_DarkPlates apron, Columns/Column_Simple + Column_MetalSupport, ` +
      `Props/Prop_Light_Floor; red trim-sheet PBR, textures capped ${m.textures[0].split(" ")[1]}). ` +
      `Base at origin, up +Y, front +Z; ${m.width} x ${m.depth} m, ${m.height} m tall. Three top-level nodes in ` +
      `fill order: \`pad\` (x/z ±3, y ${m.nodes.pad[0][1]}..${m.nodes.pad[1][1]}: the collider box), ` +
      `\`gantry\` (post at the back, -Z, its truss boom out over the pad) and \`beacon\` (the lamp on the post, ` +
      `emissive material \`M_Beacon\`, flame-amber #FFAE19).`,
  },
};

const manifestPath = path.join(artDir, "manifest.json");
const only = process.env.PROPS_ONLY?.split(","); // match tools/bpy/props_vendor.py
for (const [id, p] of Object.entries(PROPS)) {
  if (only && !only.includes(id)) continue;
  const meta = JSON.parse(readFileSync(path.join(artDir, "build/props", `${id}.json`), "utf8"));
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  let row = manifest.assets.find((a) => a.id === id);
  if (!row) {
    row = { id };
    manifest.assets.push(row);
  }
  row.description = p.describe(meta);
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");

  const recipe = {
    id, src: `build/props/${id}.raw.glb`, out: p.out, pbr: true, shared_textures: true, painted: true, budget: p.budget ?? 1000,
    ...p.pack, recipe_path: "tools/bpy/props_vendor.py",
  };
  const result = await importPack(recipe);
  updateManifest(result, recipe);
  console.log(`${id}: ${p.out}  ${result.tris} tris`);
}
