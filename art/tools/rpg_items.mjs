#!/usr/bin/env node
/**
 * Finish the hand weapons and throwables tools/bpy/rpg_items.py exported
 * (build/rpg/<id>.raw.glb + <id>.json) into art/items/<id>.glb: mount empties
 * (`grip`, `muzzle` = the tip, `fore` = a two-hander's second hand), manifest
 * rows (created here: this catalogue IS the decision to add them).
 *
 *   node tools/rpg_items.mjs          (npm run rpg runs Blender first)
 */
import { readFileSync, writeFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { importPack, updateManifest } from "./import_pack.mjs";

const artDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const build = path.join(artDir, "build/rpg");
const PACK = {
  license: "CC0",
  author: "Quaternius",
  source_url: "https://quaternius.com/packs/ultimaterpg.html",
};

const ids = readdirSync(build).filter((f) => f.endsWith(".json")).map((f) => f.slice(0, -5)).sort();
for (const id of ids) {
  const meta = JSON.parse(readFileSync(path.join(build, `${id}.json`), "utf8"));
  const own = id === "throw.frag";
  const out = `items/${id}.glb`;
  const manifestPath = path.join(artDir, "manifest.json");
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  let row = manifest.assets.find((a) => a.id === id);
  if (!row) {
    row = { id };
    manifest.assets.push(row);
  }
  row.description = id.startsWith("melee.")
    ? `${meta.hands}-handed melee weapon, ${meta.length} m (tools/bpy/rpg_items.py from the Quaternius Ultimate RPG pack): ` +
      "blade/haft along -Z out of the fist, edge +Y, `grip` at the origin, `muzzle` = tip" + (meta.hands === 2 ? ", `fore` = the second hand." : ".")
    : own
      ? "Frag grenade built from primitives in tools/bpy/rpg_items.py; upright, centred."
      : "Throwable / drinkable flask (tools/bpy/rpg_items.py from the Quaternius Ultimate RPG pack); upright, centred.";
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");

  const recipe = {
    id, src: `build/rpg/${id}.raw.glb`, out, pbr: true, budget: 2000, mounts: meta.mounts,
    ...(own ? { license: "CC0", author: "Space Adventure", source_url: "tools/bpy/rpg_items.py" } : PACK),
    recipe_path: "tools/bpy/rpg_items.py",
  };
  const result = await importPack(recipe);
  updateManifest(result, recipe);
  console.log(`${id}: ${out}  ${result.tris} tris  mounts ${Object.keys(meta.mounts).join(",")}`);
}
