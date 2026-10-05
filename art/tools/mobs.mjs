#!/usr/bin/env node
/**
 * The mob library's second half, after tools/bpy/mobs.py:
 *
 *   node tools/mobs.mjs [mob.id ...]      (run from art/; npm run mobs does both)
 *
 * From mobs/mobs.json and the Blender pass's build/mobs/<id>.raw.glb + .json:
 *   - a manifest row per mob (created here: the catalog IS the deliberate
 *     decision import_pack otherwise asks for), finished by importPack into
 *     mobs/<family>_<name>.glb;
 *   - ../server/data/mobs.json, one hostile archetype per mob: size from the
 *     measured model, stats from its role (ROLES below) scaled by height;
 *   - mobs/CATALOG.md, the browsable list with thumbnails.
 * With ids given, only those are re-imported; the archetypes and catalog are
 * always rewritten whole from what is on disk.
 */
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { importPack, updateManifest } from "./import_pack.mjs";

const artDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const lib = JSON.parse(readFileSync(path.join(artDir, "mobs/mobs.json"), "utf8"));
const only = process.argv.slice(2);

// Base stats per role at the role's reference height `ref` (m). Health and
// damage scale with height/ref (clamped), so a 2.8 m brute hits harder than
// a 2.0 m one. Melee reach adds the mob's own radius.
const ROLES = {
  swarm:  { ref: 1.0, kind: "melee",  max_health: 30,  move_speed: 3.6, aggro_radius: 18, leash_radius: 40, attack_range: 1.2, attack_damage: 6,  attack_interval: 1.0, attack_windup: 0.3,  turn_rate: 420, loot: "loot.wild.small" },
  brute:  { ref: 2.3, kind: "melee",  max_health: 110, move_speed: 3.2, aggro_radius: 20, leash_radius: 40, attack_range: 1.6, attack_damage: 18, attack_interval: 1.6, attack_windup: 0.5,  turn_rate: 240, loot: "loot.wild.big" },
  flyer:  { ref: 1.4, kind: "melee",  max_health: 35,  move_speed: 4.6, aggro_radius: 24, leash_radius: 45, attack_range: 1.4, attack_damage: 8,  attack_interval: 1.1, attack_windup: 0.3,  turn_rate: 480, loot: "loot.wild.small" },
  raider: { ref: 1.8, kind: "melee",  max_health: 60,  move_speed: 4.0, aggro_radius: 22, leash_radius: 45, attack_range: 1.6, attack_damage: 12, attack_interval: 1.2, attack_windup: 0.35, turn_rate: 360, loot: "loot.grunt" },
  lurker: { ref: 2.0, kind: "melee",  max_health: 70,  move_speed: 2.2, aggro_radius: 16, leash_radius: 30, attack_range: 1.8, attack_damage: 14, attack_interval: 1.8, attack_windup: 0.5,  turn_rate: 200, loot: "loot.wild.big" },
  drone:  { ref: 1.0, kind: "ranged", max_health: 30,  move_speed: 4.0, aggro_radius: 28, leash_radius: 45, attack_range: 22,  attack_damage: 6,  attack_interval: 1.6, attack_windup: 0.3,  turn_rate: 360, projectile_speed: 40, loot: "loot.wild.mech" },
  walker: { ref: 1.5, kind: "ranged", max_health: 80,  move_speed: 3.0, aggro_radius: 30, leash_radius: 45, attack_range: 26,  attack_damage: 9,  attack_interval: 1.4, attack_windup: 0.25, turn_rate: 240, projectile_speed: 45, loot: "loot.wild.mech" },
};

const r2 = (x) => Math.round(x * 100) / 100;
const clamp = (x, a, b) => Math.min(b, Math.max(a, x));
const stem = (id) => id.split(".").slice(1).join("_");

function size(meta) {
  // A T-posed humanoid is arm-span wide; its depth is the honest body width.
  const radius = r2(clamp(0.4 * Math.min(meta.width, meta.depth), 0.25, 1.5));
  return { radius, height: r2(meta.height), eye_height: r2(0.85 * meta.height) };
}

function archetype(mob, meta) {
  const role = ROLES[mob.role];
  if (!role) throw new Error(`${mob.id}: unknown role "${mob.role}"`);
  const s = size(meta);
  const k = clamp(s.height / role.ref, 0.6, 1.8);
  const { ref, ...base } = role;
  const a = {
    id: mob.id,
    name: mob.name,
    asset: mob.id,
    ...base,
    max_health: Math.round(base.max_health * k),
    attack_damage: Math.round(base.attack_damage * k),
    attack_range: base.kind === "melee" ? r2(base.attack_range + s.radius) : base.attack_range,
    ...s,
    ...(mob.stats ?? {}),
  };
  return a;
}

function describe(mob, meta) {
  const pack = lib.packs[mob.pack];
  return `${mob.name}: ${lib.families[mob.family].title.toLowerCase()} from Quaternius ${pack.title}, ` +
    `role ${mob.role}. Own skeleton and clips (${meta.clips.join(", ")}); turned to face -Z, feet at 0, ` +
    `scale ${meta.scale}, built by tools/bpy/mobs.py.` + (mob.notes ? ` ${mob.notes}` : "");
}

const manifestPath = path.join(artDir, "manifest.json");
const archetypes = [];
const rows = [];
for (const mob of lib.mobs) {
  const metaPath = path.join(artDir, "build/mobs", `${mob.id}.json`);
  if (!existsSync(metaPath)) throw new Error(`${mob.id}: no Blender build (run tools/bpy/mobs.py)`);
  const meta = JSON.parse(readFileSync(metaPath, "utf8"));
  const pack = lib.packs[mob.pack];
  const out = `mobs/${stem(mob.id)}.glb`;

  if (!only.length || only.includes(mob.id)) {
    const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
    let row = manifest.assets.find((a) => a.id === mob.id);
    if (!row) {
      row = { id: mob.id };
      manifest.assets.push(row);
    }
    Object.assign(row, { file: out, tris: 0, license: pack.license, source: "mobs/mobs.json", description: describe(mob, meta) });
    writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");

    const recipe = {
      id: mob.id, src: `build/mobs/${mob.id}.raw.glb`, out, pbr: true, rig: "creature",
      license: pack.license, author: pack.author, source_url: pack.source_url, recipe_path: "mobs/mobs.json",
    };
    const result = await importPack(recipe);
    updateManifest(result, recipe);
    console.log(`${mob.id}: ${out}  ${result.tris} tris  ${meta.height} m`);
  }
  const a = archetype(mob, meta);
  archetypes.push(a);
  rows.push({ mob, meta, a, out });
}

writeFileSync(path.join(artDir, "../server/data/mobs.json"), JSON.stringify({
  $comment: "Generated by art/tools/mobs.mjs from art/mobs/mobs.json -- do not edit by hand. One hostile archetype per mob in the library; place them with zone entities (type npc, def <id>) or a bounty archetype. Size (radius/height/eye_height) is measured from the model; stats come from the role table in mobs.mjs.",
  version: 1,
  npcs: archetypes,
}, null, 1) + "\n");

// ---- the catalog ---------------------------------------------------------------
const lines = [
  "# Mob library",
  "",
  `Generated by \`art/tools/mobs.mjs\` from \`art/mobs/mobs.json\` -- edit that, then \`npm --prefix art run mobs\`. ` +
  `${rows.length} mobs; each is an art asset (\`art/mobs/*.glb\`) and a hostile archetype in \`server/data/mobs.json\` ` +
  "with the same id, ready to place in a zone (`{\"type\": \"npc\", \"def\": \"<id>\", \"pos\": [...], \"yaw\": 0}`) or use as a bounty archetype.",
  "",
  "Clips the client plays: `idle`, `walk`, `sprint`, `die`, `hit` (and `attack`, not yet driven by the server). " +
  "Missing ones fall back (sprint -> walk -> idle; no `die` -> a procedural fall). Other clips each model ships are kept under snake_case names.",
  "",
  "| role | kind | base health | speed | damage | reach |",
  "|---|---|---|---|---|---|",
  ...Object.entries(ROLES).map(([n, r]) => `| ${n} | ${r.kind} | ${r.max_health} @ ${r.ref} m | ${r.move_speed} | ${r.attack_damage} | ${r.attack_range} m${r.projectile_speed ? " (projectile)" : " + radius"} |`),
  "",
  "## Packs",
  "",
  "| pack | licence | where it came from |",
  "|---|---|---|",
  ...Object.values(lib.packs).map((p) => `| [${p.title}](${p.source_url}) | ${p.license} | ${p.fetched} |`),
  "",
];
for (const [fam, f] of Object.entries(lib.families)) {
  const mine = rows.filter((r) => r.mob.family === fam);
  if (!mine.length) continue;
  lines.push(`## ${f.title} (${mine.length})`, "");
  if (f.note) lines.push(f.note, "");
  lines.push("| | name | id | height | tris | role | health | dmg | clips | notes |", "|---|---|---|---|---|---|---|---|---|---|");
  for (const { mob, meta, a } of mine) {
    lines.push(`| <img src="thumbs/${stem(mob.id)}.png" width="96"> | ${mob.name} | \`${mob.id}\` | ${a.height} m | ${meta.tris} | ${mob.role} (${a.kind}) | ${a.max_health} | ${a.attack_damage} | ${meta.clips.join(", ")} | ${mob.notes ?? ""} |`);
  }
  lines.push("");
}
writeFileSync(path.join(artDir, "mobs/CATALOG.md"), lines.join("\n"));
console.log(`${archetypes.length} archetypes -> server/data/mobs.json, catalog -> mobs/CATALOG.md`);
