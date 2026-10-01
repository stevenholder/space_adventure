# Attribution

Where the art came from, and under what licence.

**Every model the game ships is our own** (2026-10-01). The last Kenney Space
Kit props were replaced by `tools/gen_props.py`, and the vendored packs and
the import paths that adapted them are gone. Two outside sources remain, both
free to use without attribution:

| what | source | licence |
|---|---|---|
| The human base mesh under every body (`tools/bpy/human.py`) | MakeHuman, via the MPFB2 Blender add-on (makehumancommunity.org, extensions.blender.org/add-ons/mpfb) | CC0 |
| The display font, Staatliches (`client/godot/fonts/`) | Google Fonts | SIL Open Font License 1.1 (`OFL.txt` ships beside it) |

## Generators

| assets | generator |
|---|---|
| `char.player`, `npc.*` | `tools/bpy/human.py` (Blender, headless) |
| `armor.*`, `pack.scout` | `tools/bpy/armor.py`, textures and decals by `tools/bpy/textures.py` |
| `weapon.*` | `tools/gen_weapon.py` |
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
