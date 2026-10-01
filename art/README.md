# art — low-poly assets

glTF 2.0 binary (`.glb`) assets, shaders, and the asset manifest.

- Owner: `art`
- Manifest: `art/manifest.json` — asset id → file, tri count, license, source
  script
- Generation scripts: `art/tools/` (committed; assets rebuild from a clean
  clone)
- Budgets: player character ≤ 1,500 tris; hero ship ≤ 2,000 tris; props
  (rocks, debris) ≤ 500 tris; flat-shaded, vertex colors, bold silhouettes
- Terrain is **not** an art asset — the server generates the cube-sphere radius
  field and the client builds the mesh from it. `art` owns its material and
  palette, not its geometry.
- The M1 world is a 150 m-radius asteroid: horizon ~23 m away, ground curving
  away visibly in every direction. No distance fog — there is no distance.

## Manifest contract (frozen at M0)

`manifest.json` is a **contract, not an inventory** — it is committed with the
asset ids wave 2 uses before any `.glb` exists, so `frontend` and `art` work in
parallel without a handoff.

- Ids are stable and namespaced: `ship.*`, `prop.*`.
- `file` is a path relative to `art/`, and **may not exist yet**. `frontend`
  resolves geometry by id and falls back to a flat-shaded `BoxGeometry`
  placeholder when the file is missing — a missing asset is never an error.
- `tris: 0` means "not generated yet". `art` fills it in with the real count.
- Adding an id is an `art` change; renaming or removing one breaks `frontend`
  and goes through the main thread.

Node-name contract:

- **`char.player`** (and the humanoid NPCs) are **skinned since 2026-09-30**: one
  mesh on an armature whose bones carry the names below, plus a separate `head`
  mesh (see "Humanoid skeleton"). The text that follows describes the segmented
  layout the contract names came from; the names and the eye/hand.r rules still
  hold, the per-limb nodes are now bones. It DOES carry animation clips: they are
  transform tracks on those nodes, retargeted from another CC0 pack by
  `tools/import_pack.mjs` (see `ATTRIBUTION.md`). An asset that carries them
  says so with `rig: "animated"` in the manifest, and `verify.mjs` then
  requires the clips `idle`, `walk`, `sprint`, `die` and checks every track
  lands on a node that exists.

  Segmented rather than skinned is not a limitation here, it is what lets the
  local player draw its own head shadows-only while the rest of the body still
  renders — one `SkinnedMeshRenderer` is all or nothing.

  | node | what |
  |---|---|
  | `eye` | empty at the view point, `-Z` forward — the camera mounts here, so eye height lives in the model. A ROOT mount, deliberately not parented under `head`: the local player draws its head shadows-only and would take the camera with it |
  | `hand.r` | empty in the right hand, parented under `arm.r` so it swings with the arm. A weapon's `grip` node is aligned to this |
  | `head` | hidden for the local player (the camera is inside it); visible on everyone else |
  | `torso` | body |
  | `arm.l` / `arm.r` | pivot at the shoulder, so a rotation swings the arm |
  | `leg.l` / `leg.r` | pivot at the hip, same |

  Pivots matter more than geometry here: each limb rotates about its own node
  origin, so a shoulder placed at the wrist swings the arm from the wrong end.

- The player sees this model **from the inside and from above** — looking down
  at their own torso and legs from ~0.3 m. It must read from that angle, not
  only in silhouette from 20 m: no hollow shell, no missing top faces, no
  geometry that only works when viewed from outside.
- From M2, vehicles carry **`seat.pilot`** and `seat.passenger.N` under the
  same convention — the camera mount moves to those nodes when a player takes
  a seat (GDD "Vehicles and crew"). Not needed for M1.
- Placeholder fallback: when the `.glb` or the node is missing, `frontend`
  mounts the camera at the GDD `eye_height` above the entity origin.

## The player body and its armor (MakeHuman, from 2026-10-01)

`char.player` and the Scout set target a Star Citizen read at modest cost:
real anatomy, hard-surface plates over a fitted undersuit, smooth shading,
real materials, about 18k triangles dressed.

- **Body** (`tools/bpy/human.py`): a MakeHuman human from MPFB2's sliders
  (`MACRO`), MPFB's 53-bone `game_engine` rig with its weights, turned to +Y
  front and scaled so the eyes are at 1.70 m, helpers removed, decimated to
  6k triangles, split into `body`/`arms`/`head`. Undersuit, gloves and boots
  are materials on the body. Clips are posed by world-space swings and an
  analytic two-bone IK toward WRIST targets (shoulders at ±0.20, 0.02, 1.45;
  wrist reach 0.54 m), and both hands carry mounts (`hand.r`, `hand.l`): the
  client runs a two-handed weapon's barrel from the right hand toward the
  left, every frame.
- **Armor** (`tools/bpy/armor.py`): hard plates are clean grids laid on a
  cylinder around their bone, masked to a rounded rectangle, projected
  inward along their normals onto a SMOOTHED copy of the body with a gap,
  given thickness with a darker rim and bevelled, rigid on one bone. Gloves,
  boots and the belt are cut from the body and keep its weights. The helmet
  is a smooth shell with a separate visor strip. Things that did not work,
  so they are not retried: cutting plates straight out of the body (they
  carried its anatomy, and un-subdividing an irregular region tore it),
  nearest-point shrinkwrap (dragged the chest plate onto the neck),
  decimating after Solidify and Solidify's even offset (both made spikes).
- **Holding a weapon**: the rifle has contact points, `grip` (mid pistol
  grip), `fore` (under the fore-end), `sight`/`front` (line of sight) and
  `muzzle`. human.py's `grip()` puts each hand MOUNT on its contact point by
  iterating the arm IK and orienting the hand (palm/knuckle frame from the
  hand bone; palm normal is the bone's −Z), right fist round the grip with the
  trigger finger straighter, left palm up under the fore-end. A bladed stance
  (chest turned 25°, head turned back) lets the left arm reach. The client
  places the gun grip→hand.r and fore→hand.l every frame; while aiming it
  aligns sight→front with the view instead (grip→muzzle climbs ~12° against
  the sights).
- **Boots** are built (rounded foot shell with a lowered toe box, sole, ankle
  shaft with a cuff), not cut from the bare foot.
- **Materials** (`pbr` in the recipe): the import keeps the glTF materials
  (base colour, roughness, metallic) and the client draws them as they are;
  first person gets a depth-squeezed copy of each. Palettes are written as
  display colours and converted to linear on the way out.
- **MPFB2** is a build tool (GPL, not shipped); the MakeHuman mesh and
  targets it uses are CC0. Install once into the local Blender:
  `blender --command extension install-file -r user_default -e add-on-mpfb-v2.0.17.zip`
  (extensions.blender.org). The shipped `.glb` files are committed, so only
  regenerating needs it.

Every humanoid now comes from human.py's VARIANTS: the player, the
quartermaster (older, heavier, khaki), the dispatcher (android: slim, pale
synthetic skin, no hair, lit eyes, scalp seam), the grunt (orc: bigger and
heavier, eyes at 1.82, green skin, brow, tusks, ears, topknot) and the
gunner (robot: the player's build in flat-shaded gunmetal with a visor band
and antenna, so the Scout/iron armor fits it). A variant sets MakeHuman
sliders, eye height, colours/metalness, hair, flat shading and rigid head
parts; rig, clips, mounts and grips are shared. `blender -b --python
tools/bpy/human.py -- npc.grunt` rebuilds one. body.py now only builds the
retired 11-bone skeleton and is kept for its older docs.

## Humanoid skeleton (own bodies, from 2026-09-30)

`char.player` is no longer a downloaded pack and no longer a stack of parts:
`tools/bpy/body.py` builds it headless in Blender (`npm run gen:bpy`, needs
`BLENDER` or `~/opt/blender/4.5.14/blender`) as ONE continuous skinned mesh
-- a stick figure of joints with a radius each, wrapped by the Skin modifier,
subdivided once, flat-shaded -- on an armature whose bones carry the contract
names, plus a separate head mesh on the same armature so the local player can
still draw its own head shadows-only. Weights are deterministic (nearest bone
segment, blended across the joint), so elbows and knees bend. The clips
(`idle`, `walk`, `sprint`, `die`) are authored in the same script as bone
rotations; nothing comes off a pack. The raw export lands in the gitignored
`build/` and the recipe feeds it through `import_pack.mjs` like any other
source; only `eye` is added there.

The joint table below is the contract every humanoid and every armor piece is
authored against -- same bone names, same joints, same metres -- so an armor
piece is another skin-modifier mesh over the same joints with larger radii,
weighted the same way, and follows the same bones. Change it in `body.py`
JOINTS and here together.

| bone | from → to (x, y, z in metres; model faces −Z, +X is its right) | parent |
|---|---|---|
| `root` | ground 0,0,0 → pelvis 0,0.92,0 | — |
| `torso` | pelvis → neck 0,1.52,0 | `root` |
| `head` | neck → crown 0,1.78,0 (own mesh; hair to 1.81) | `torso` |
| `arm.r` / `arm.l` | shoulder ±0.22,1.45,0 → elbow ±0.27,1.19,0 | `torso` |
| `forearm.r` / `.l` | elbow → wrist ±0.28,0.97,0 (hand mesh to 0.85) | `arm.*` |
| `leg.r` / `leg.l` | hip ±0.11,0.90,0 → knee ±0.12,0.50,0 | `root` |
| `shin.r` / `.l` | knee → ankle ±0.12,0.10,0 (boot to the ground) | `leg.*` |
| `eye` | 0, 1.70, 0 root mount (recipe) | — |
| `hand.r` | empty on `forearm.r`, just past the wrist; Godot makes it a BoneAttachment3D | — |

Every humanoid comes out of this one script. `body.py` RACES defines a race
as a head build plus a palette (and a default bulk): `human`, `orc` (green,
brow, tusks, ears, topknot, heavy), `robot` (box head, one visor, antenna,
chassis grey with hazard accents, no skin), `android` (pale synthetic skin,
scalp seam, lit cyan eyes, no hair). VARIANTS then picks a race per asset and
overrides palette, `bulk` (body thickness) and `shoulders` (width):

| asset | race | who |
|---|---|---|
| `char.player` | human | players |
| `npc.shopkeeper` | human, khaki, stocky | Quartermaster Vex |
| `npc.dispatcher` | android | Dispatcher Oru |
| `npc.grunt` | orc, rust jacket | camp melee raiders |
| `npc.gunner` | robot | camp ranged raiders |

All of them share the joints, bones, weights and clips, so one armor piece
fits every race.

### Armor (wearables)

Armor works the way World of Warcraft's does: nothing is an inflated shell
of the body.

- **paint** pieces (Scout suit, trousers, gloves, boots) are the body's own
  faces at a 6 mm offset with the body's own weights (`SHELL_BASE` maps each
  garment vertex to its base vertex), so they move exactly like the body and
  cannot stretch or tear against it. This is WoW's texture layer until we
  have textures. The wearer's surfaces under a piece are not drawn: the
  recipe's `covers` (`body/suit`, `head/hair`, …) names them, `import_pack`
  records every mesh's surface names in the manifest before the bake erases
  the materials (Godot keeps primitive order as surface order), and the
  client (`EntityViews.Cover`) puts a collapse shader on those surfaces. That
  is what removes the last slivers, where a raised arm's skin pushes into the
  torso's and no offset can win.
- **rigid** pieces (helmet, breastplate, pack) are pushed out (radial per
  bone blended by inverse distance^4, plus `pad` along the normal) and
  weighted 100 % to one bone. They never deform and overlap the joint.

`body.py` ARMOR holds the rules: `keep(centre, part, material)`, `mat(centre,
part)`, `mode`, `bone`, `grow`, `pad`, and box `extras`. Faces classify by
`body_part` (nearest bone with height fences: the mesh is coarse and a
chest-top quad's centre is nearer the neck bone than the spine). Test with a
close side view, armed: the `tools/bpy` probes render the body with its
colours swapped to magenta/cyan so poke-through is unmissable, and in game
the recruit at (0, 0, 3.3) yaw 80 / 260 on a review server with
`-uiFace hostile`. Far shots hid every one of these. Ids match the item ids
in `server/data/items.json` (`asset`); budget 1500 tris (`pack.*` 800);
authored at bulk 1.0, so an orc would clip through them.

### First person

Every humanoid has three meshes: `body`, `arms` (both arms and hands,
split along the sleeve seam by `split_arms`) and `head`. The client hangs a
second `char.player` under the camera and draws only its `arms`, so worn
sleeves and gloves cover them like on any body (`armor.suit.scout` and
`armor.gloves.scout` cover `arms/*`). The `fp_*` clips (`fp_idle`,
`fp_walk`, `fp_sprint`, `fp_ads`, `fp_lower`, `fp_unarmed`, and the one-shots
`fp_fire` 0.12 s, `fp_reload` 2.0 s) pose only the arms, from the `FP` table.
Tune poses with `blender -b --python tools/bpy/probe_fp.py -- ads|aim|lower|unarmed`
(run from art/): it sweeps the arm angles against a wrist target and prints
the best. The shoulders sit 0.25 m under the eye, beyond what an arm can
reach, so the client frames the result (ViewModel): the rifle by its rear
sight, empty hands by the right hand.

The pulse rifle is ours (`tools/gen_weapon.py`, pure glb.py boxes, 156 tris):
−Z forward, `grip` on top of the pistol grip just under the bore, `muzzle` at
the barrel tip, a notched rear sight so aiming sees through it.

### Holding a weapon

`body.py` also exports `idle_armed`, `walk_armed` and `sprint_armed`: the same
gaits with the arms in the AIM pose: right elbow tucked with the forearm
angled across (the grip near the chest), left arm extended and swept in
(rotation about the arm's own Y is what swings it inward). Reach caps the
pose: a straight arm reaches 0.48 m ahead of the shoulder, so the fore-end
comes to the left hand, not the reverse. The pulse rifle is fitted to 0.55 m
(`weapon.pulse.json`; its muzzle is the +Z end, re-measured after the stock
led when held); after the barrel is turned onto the forearm the client
rolls it about the barrel so its top faces the wearer's up. The client plays the `_armed` variant whenever the body has
something in hand (`CharacterAnim.Armed`), and turns the weapon so its
barrel runs along the forearm: as exported, the `hand.r` mount's local −Z is
the elbow→wrist axis (measured in Godot; the bone-parented empty carries a
rotation of its own), so the weapon holder gets a 180° turn about X before
its `grip` is lined up with the hand. The turn is computed, not fixed: the
weapon's own `grip`→`muzzle` line is rotated onto the wearer's elbow→wrist
line two frames after attach (the bone attachment is not posed before the
skeleton's first update; a guessed fixed turn put the barrel vertical). Bone
x swings a down-pointing bone FORWARD for positive values. NPC archetypes
hold a weapon via `npcs.json` `primary`, replayed as `equipped` at join.
The LOCAL body holds it too (`ViewModel.Hold`): the real weapon mounted on
its hand shadows-only, in the armed gaits, so your shadow holds the gun the
way everyone else's does; the first-person rig (rifle and hands) casts no
shadow at all, since a picture for the eye that cast one put a floating gun
a metre ahead of the body. Ids match the item ids
in `server/data/items.json` (`asset` field): `armor.helmet.scout`,
`armor.suit.scout`, `armor.legs.scout`, `armor.gloves.scout`,
`armor.boots.scout`, `pack.scout`, `armor.plate.iron`. Budget 1500 tris
(`pack.*` 800). Authored at bulk 1.0; an orc would clip through them, and
NPCs wear nothing on the wire today. A new race is a `build_head` branch and a RACES row; a new
humanoid is a VARIANTS row plus a recipe pointing at `build/<id>.raw.glb`.

M1 needs no ship. Vehicles are M2 — see `docs/ROADMAP.md`.

M1 adds the real player character + surface props behind these ids.
