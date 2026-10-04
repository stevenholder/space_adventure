# Attribution

Where the art came from, and under what licence.

**Every model the game ships is our own** (2026-10-01), except the bodies and
the mob library listed below. The last Kenney Space Kit props were replaced by
`tools/gen_props.py`. Outside sources, all free to use without attribution:

| what | source | licence |
|---|---|---|
| The human base mesh under every body (`tools/bpy/human.py`) | MakeHuman, via the MPFB2 Blender add-on (makehumancommunity.org, extensions.blender.org/add-ons/mpfb) | CC0 |
| The display font, Staatliches (`client/godot/fonts/`) | Google Fonts | SIL Open Font License 1.1 (`OFL.txt` ships beside it) |
| Spike: the `char.ubc` body (Superhero male, its head textures and Hair_Buzzed), worn by `npc.veteran` | Quaternius, Universal Base Characters, Standard (quaternius.com/packs/universalbasecharacters.html; unpacked into gitignored `vendor/ubc`) | CC0 |
| The mob library (`mob.*`, `mobs/`): Ultimate Monsters, Ultimate Modular Men, the Sci-Fi Essentials Kit's enemies, the Modular Sci-Fi MegaKit's aliens (catalog and where each came from: `mobs/mobs.json`, `mobs/CATALOG.md`) | Quaternius (quaternius.com/packs: ultimatemonsters, ultimatemodularcharacters, scifiessentialskit, modularscifimegakit) | CC0 |
| `mob.dungeon.imp`, `mob.dungeon.puglin` | Quaternius, Bestiary - Dungeon Monsters Kit, Standard | Quaternius Asset License v1.0: free for use in games; the assets themselves may not be redistributed |
| The clips on `mob.dungeon.*` (retargeted onto them) | Quaternius, Universal Animation Library, Standard | CC0 |
| Hand weapons and flasks (`melee.*`, `throw.fire`, `throw.acid`, `potion.heal`; `items/`), recoloured and re-framed by `tools/bpy/rpg_items.py` | Quaternius, Ultimate RPG pack (quaternius.com/packs/ultimaterpg.html; FBX unpacked into gitignored `vendor/ultimate-rpg`) | CC0 |

## Generators

| assets | generator |
|---|---|
| `char.player`, `npc.*` | `tools/bpy/human.py` (Blender, headless) |
| `armor.*`, `pack.scout` | `tools/bpy/armor.py`, textures and decals by `tools/bpy/textures.py` |
| `weapon.*` | `tools/gen_weapon.py` |
| `throw.frag` | `tools/bpy/rpg_items.py` (primitives) |
| `tool.*`, `prop.node.*`, `prop.wreck`, `prop.bench` | `tools/gen_nodes.py` |
| `prop.rock.*`, `prop.loot.crate`, `prop.barrel(s)`, `prop.generator`, `prop.dish`, `prop.bones`, `vehicle.rover.v1` | `tools/gen_props.py` |
| `struct.*` | `tools/gen_struct.py`, `tools/gen_kit.py` |
| `ship.v1`, `prop.target` | `tools/gen_ship.py`, `tools/gen_target.py` |

Each row in `art/manifest.json` names its generator in `source`, with
`author: "Space Adventure"` and `license: "CC0"`.

## If an outside asset is ever added again

Verify the licence per model at download time and record it here and in the
manifest row (`license`, `source_url`, `author`). Do not assume a pack's
licence covers every model in it; aggregators mix CC0 and CC-BY, and CC-BY
obliges a credit line in the shipped game. `tools/import_pack.mjs` only
finishes our own Blender exports now: an outside model needs its own
conversion step.
