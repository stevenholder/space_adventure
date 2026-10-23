# Attribution

Where the art came from, and under what licence.

Most models the game ships are our own; since Phase 25 (2026-10-20..23) the
world itself -- the structures, the camp props, the rocks and ore nodes --
is composed from three Quaternius kits, alongside the bodies and the mob
library listed below. Outside sources, all free to use without attribution:

| what | source | licence |
|---|---|---|
| The human base mesh under every body (`tools/bpy/human.py`) | MakeHuman, via the MPFB2 Blender add-on (makehumancommunity.org, extensions.blender.org/add-ons/mpfb) | CC0 |
| The display font, Staatliches (`client/godot/fonts/`) | Google Fonts | SIL Open Font License 1.1 (`OFL.txt` ships beside it) |
| Spike: the `char.ubc` body (Superhero male, its head textures and Hair_Buzzed), worn by `npc.veteran` | Quaternius, Universal Base Characters, Standard (quaternius.com/packs/universalbasecharacters.html; unpacked into gitignored `vendor/ubc`) | CC0 |
| The mob library (`mob.*`, `mobs/`): Ultimate Monsters, Ultimate Modular Men, the Sci-Fi Essentials Kit's enemies, the Modular Sci-Fi MegaKit's aliens (catalog and where each came from: `mobs/mobs.json`, `mobs/CATALOG.md`) | Quaternius (quaternius.com/packs: ultimatemonsters, ultimatemodularcharacters, scifiessentialskit, modularscifimegakit) | CC0 |
| `mob.dungeon.imp`, `mob.dungeon.puglin` | Quaternius, Bestiary - Dungeon Monsters Kit, Standard | Quaternius Asset License v1.0: free for use in games; the assets themselves may not be redistributed |
| The clips on `mob.dungeon.*` (retargeted onto them) | Quaternius, Universal Animation Library, Standard | CC0 |
| Hand weapons and flasks (`melee.*`, `throw.fire`, `throw.acid`, `potion.heal`; `items/`), recoloured and re-framed by `tools/bpy/rpg_items.py` | Quaternius, Ultimate RPG pack (quaternius.com/packs/ultimaterpg.html; FBX unpacked into gitignored `vendor/ultimate-rpg`) | CC0 |
| World art: every `struct.*` kit role (walls, corners, gates, cover, towers, masts, hab, shack), `prop.dock`, `prop.chest`, `prop.barrel`, `prop.barrels`, `prop.generator`, `prop.loot.crate`, the dish's pad; composed and re-framed by `tools/bpy/props_vendor.py` (base at y=0, kit materials painted, trim sheets shared in `tex/`, textures capped at 1024) | Quaternius, Modular SciFi MegaKit, Standard (quaternius.com/packs/modularscifimegakit.html; glTF unpacked into gitignored `vendor/scifi-megakit`): `glTF/{Walls,Columns,Platforms,Props}/*` + `Textures/T_Trim_0*` | CC0 |
| `prop.dish` and the masts' dish (`Prop_SatelliteDish`, on the MegaKit's trim sheets) | Quaternius, Sci-Fi Essentials Kit (quaternius.com/packs/scifiessentialskit.html; glTF unpacked into gitignored `vendor/scifi-essentials`) | CC0 |
| `prop.rock.a/b/c` (`Rock_Medium_1/2/3`), `prop.node.ore.iron`, `prop.node.ore.copper`; the `Rocks_Diffuse` / `Rocks_Desert_Diffuse` sheets in `tex/` | Quaternius, Stylized Nature MegaKit, Standard -- the free 68-model tier (quaternius.com/packs/stylizednaturemegakit.html; glTF unpacked into gitignored `vendor/stylized-nature`) | CC0 |

The Nature pack is fetched by hand: the quaternius.com page's Download button
leads to itch.io, where the free (Standard) tier is the one used here. It has
no crystals, so `prop.node.crystal` stays our own (`tools/gen_nodes.py`).

## Generators

| assets | generator |
|---|---|
| `char.player`, `npc.*` | `tools/bpy/human.py` (Blender, headless) |
| `armor.*`, `pack.scout` | `tools/bpy/armor.py`, textures and decals by `tools/bpy/textures.py` |
| `weapon.*` | `tools/gen_weapon.py` |
| `throw.frag` | `tools/bpy/rpg_items.py` (primitives) |
| `tool.*`, `prop.node.crystal`, `prop.wreck`, `prop.bench` | `tools/gen_nodes.py` |
| From the kits: `struct.*` (the 14 kit roles), `prop.dock`, `prop.chest`, `prop.rock.*`, `prop.barrel(s)`, `prop.generator`, `prop.dish`, `prop.loot.crate`, `prop.node.ore.*` | `tools/bpy/props_vendor.py` + `tools/props_vendor.mjs` (`npm run props:vendor`) |
| `prop.bones`, `prop.forge`, `vehicle.rover.v1` | `tools/gen_props.py` |
| `struct.post`, `struct.wall` (pre-Phase-9 legacy rows) | `tools/gen_struct.py` |
| `ship.v1`, `prop.target` | `tools/gen_ship.py`, `tools/gen_target.py` |

Each row in `art/manifest.json` names its generator in `source`; our own
models carry `author: "Space Adventure"`, the kit ones `author: "Quaternius"`
and the pack's page in `source_url`; both are `license: "CC0"` (the two
dungeon mobs above are the exception).

## If an outside asset is ever added again

Verify the licence per model at download time and record it here and in the
manifest row (`license`, `source_url`, `author`). Do not assume a pack's
licence covers every model in it; aggregators mix CC0 and CC-BY, and CC-BY
obliges a credit line in the shipped game. `tools/import_pack.mjs` only
finishes our own Blender exports now: an outside model needs its own
conversion step.
