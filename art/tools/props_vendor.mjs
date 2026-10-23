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
  // The kit swap (Phase 25): Phase 9's roles at the same cells.
  "struct.wall4.scrap": {
    out: "structs/wall4.scrap.glb",
    budget: 400,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit wall, 4 m module (Phase 25, the Scrapyard hand): the Quaternius Modular SciFi MegaKit's ` +
      `Walls/WallBand_Straight_Broken on a BottomMetal_Straight skirt (0.3 m below y=0), the \`_Red\` trim sheets, ` +
      `composed by tools/bpy/props_vendor.py. MODELLED AT FINAL SCALE: the client tiles these along +X (the run), ` +
      `never stretches them; ${m.width} x ${m.depth} m, ${m.height} m tall, centred in its 1x1 cell.`,
  },
  "struct.wall4.colony": {
    out: "structs/wall4.colony.glb",
    budget: 400,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit wall, 4 m module (Phase 25, the Colony hand): the Quaternius Modular SciFi MegaKit's ` +
      `Walls/WallAstra_Straight_Flat on a BottomSimple_Straight skirt (0.3 m below y=0), the default trim sheets, ` +
      `composed by tools/bpy/props_vendor.py. MODELLED AT FINAL SCALE: the client tiles these along +X (the run), ` +
      `never stretches them; ${m.width} x ${m.depth} m, ${m.height} m tall, centred in its 1x1 cell.`,
  },
  "struct.corner.scrap": {
    out: "structs/corner.scrap.glb",
    budget: 400,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit corner post, the Scrapyard hand: a leaning Column_Simple with an askew cap, the _Red trim sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.corner.colony": {
    out: "structs/corner.colony.glb",
    budget: 400,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit corner post, the Colony hand: a Column_Simple with a square cap, the default trim sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.gate4.scrap": {
    out: "structs/gate4.scrap.glb",
    budget: 400,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit gate, 4 m module, the Scrapyard hand: Door_Frame_A with one DarkMetal leaf hung ajar, _Red sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.gate4.colony": {
    out: "structs/gate4.colony.glb",
    budget: 400,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit gate, 4 m module, the Colony hand: an open Door_Frame_Square, default sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.cover4.scrap": {
    out: "structs/cover4.scrap.glb",
    budget: 400,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit cover, 1 cell, the Scrapyard hand: leaning ShortWall_DarkMetal2 box under a turned DarkPlates lid, _Red sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.cover4.colony": {
    out: "structs/cover4.colony.glb",
    budget: 400,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit cover, 1 cell, the Colony hand: a ShortWall_WhitePlate2 box under a Platform_Squares lid, default sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.tower.scrap": {
    out: "structs/tower.scrap.glb",
    budget: 1500,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit watchtower, 2x2 cells, the Scrapyard hand: four leaning posts, DarkPlates deck, DarkMetal rails, a shoved cabin, _Red sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.tower.colony": {
    out: "structs/tower.colony.glb",
    budget: 1500,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit watchtower, 2x2 cells, the Colony hand: four posts, a Platform_Simple deck, Simple1 rails, a centred cabin, default sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.mast.scrap": {
    out: "structs/mast.scrap.glb",
    budget: 3500,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit mast, 1 cell, the Scrapyard hand: two Column_Simple stacked by eye, the Essentials Prop_SatelliteDish tipped on top, _Red sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.mast.colony": {
    out: "structs/mast.colony.glb",
    budget: 3500,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit mast, 1 cell, the Colony hand: Column_Large_Straight with the Essentials Prop_SatelliteDish on top, default sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.hab.colony": {
    out: "structs/hab.colony.glb",
    budget: 1500,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit hab, 2x2 cells, Colony only: eight WallAstra_Straight_Flat, TopSimple copings, a Platform roof, a Door_Simple leaf on +Z, default sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
  },
  "struct.shack.scrap": {
    out: "structs/shack.scrap.glb",
    budget: 1500,
    pack: MEGAKIT,
    describe: (m) =>
      `Kit shack, 2x2 cells, Scrapyard only: WallBand walls (two Broken), a tipped DarkPlates roof, an awning, a DarkMetal door, a stovepipe, _Red sheets (Phase 25 kit swap), composed by tools/bpy/props_vendor.py from Quaternius MegaKit pieces; ` +
      `MODELLED AT FINAL SCALE, the client tiles it, never stretches it; ${m.width} x ${m.depth} m, ${m.height} m tall, ` +
      `centred on its cell(s), base at y=0 on a 0.3 m skirt.`,
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
