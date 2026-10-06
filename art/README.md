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
tools/bpy/human.py -- npc.grunt` rebuilds one.

The four player bodies (GDD "Bodies"): `char.player` and `char.player.f`
(the Colonist v2 MakeHuman build, `gender` 1.0 / 0.0, below), `char.ubc`
and `char.ubc.f` (Quaternius UBC Superhero male / female; since Phase 17
their hair is a separate worn piece, `tools/bpy/hair.py`, and they ship
bald with their own eyebrows). armor.py builds every piece for all of them (`BODIES`); per-body
fit lives in its `FITS`: UBC wraps onto a body smoothed 4 passes, not 12,
and the UBC female's torso plates `drape` (each vertex clears the torso
within 8 cm along the spine) so the chest plate rides over the bust
instead of being cut by it. `blender -b --python tools/bpy/armor.py --
char.ubc.f` rebuilds one body's set (`base` = the unsuffixed pieces, the
original MakeHuman build the gunner and shopkeeper wear; the Colonist v2
has its own `@char.player` set). body.py now only builds the
retired 11-bone skeleton and is kept for its older docs.

## Colonist v2, faces and hair (Phase 17, 2026-10-06)

GDD "Faces and hair". The Colonist (`char.player`, `char.player.f`) is a
deliberately different body from the Vanguard (UBC): slimmer and shorter,
eyes at **1.65 m** (`eye` per VARIANT in human.py; UBC stays 1.70), the
same 9000-tri ceiling.

- **Build**: MakeHuman macros muscle 0.40 / 0.38, weight 0.45 / 0.42,
  proportions 0.75 / 0.8, plus detail targets (`targets` in the variant,
  loaded before the rig is fitted): `torso/measure-shoulder-dist-decr`,
  `eyebrows-trans-forward` (brow ridge), `cheek/*-cheek-bones-incr`,
  `chin-bones-incr`, `chin-prominent-incr`, small `nose-hump-incr`.
- **Head-weighted decimation** (`lod`): the eyeballs come off first (they
  return as clean spheres), then one collapse pass with a vertex group:
  eyelids and lips 0.05, head 0.2, hands 0.6, everything else 1.0. Blender's
  collapse decimator collapses LOWER weights later and never collapses a
  weight of exactly 0; the group factor grades it, so human.py bisects the
  factor until the head lands on `HEAD_TRIS` (3300). Result: head ~3400 of
  ~8900, the suited torso and legs coarse.
- **Collar**: the neck is cut along a plane (`collar()`, bmesh bisect) so the
  undersuit's edge is a clean line.
- **Eyebrows**: Quaternius `Eyebrows_Female` on BOTH Colonists, less its
  eyeliner wings (loose parts that never reach 12 mm above the eyes; the
  male `Eyebrows_Regular` ended in an upturned clump at the temple and read
  stern, and `Eyebrows_Female` with its liner read made-up). Refitted by
  `refit()`: scaled by the eyes' spacing (across, by the forehead's width;
  the male 1.4x thicker), lifted (`brow_lift`, 6 mm F / 5 mm M), the inner
  ends raised a little (`brow_flat`), the outer tails thinned toward the
  brow's midline (`brow_tail`), and every vertex held within 1.8x the
  brow's median half-thickness under its top edge (`brow_clamp`: the inner
  end hooked down toward the nose). Then every vertex keeps the height it
  had above the UBC skin, measured above THIS skin along its normal, at
  least 1.5 mm off it (0.8 mm let the skin show through in patches).
  Joined into `head` on the head bone, material `hair`, so the local player
  never sees them and a helmet's `covers: head/hair` hides them. The UBC
  bodies' own brows wear the same tinted `hair` material.
- **Eyes**: MPFB `eyes/*-eye-height2-incr` (0.45 M / 0.4 F) and
  `*-eye-scale-incr` 0.15 open the lids; the eyeball sphere's radius is the
  helper's mean radius x `EYEBALL_FIT` (0.926: the helper includes a cornea
  bulge). The spheres get an azimuthal UV (front pole at the centre) and one
  512 px texture (`skin.eye_image`): pupil, striated iris in the variant's
  `eye` colour (brown M, hazel-green F) with a lighter collarette and a dark
  limbal ring, white sclera, on both `eye` and `sclera` (roughness 0.08 /
  0.18 -- distinct, or import_pack's dedup() merges them and the head's
  surface list changes).
- **Ears**: decimation weight 0.04 (`LOD_WEIGHT["ear"]`, MakeHuman's `ears`
  group), then `smooth_ears()` relaxes the ear's inner vertices twice: the
  decimated concha was a few big triangles that faceted under a key light.
  The painted AO carries the folds.
- **Painted skin** (`tools/bpy/skin.py`, `lod` variants only): on the
  FULL-resolution mesh, before decimation, the skin faces' MakeHuman UV
  islands are packed alone into the unit square (decimation carries the
  UVs). Per-vertex masks -- the `lips` group feathered over the mesh, warm
  nose / cheeks / ears / chin (soft blobs round landmarks found from the
  eyes), eye sockets, the lid margin (skin resting on the eyeball inside the
  front cone, feathered over the lid: the lash line), a faint beard and
  scalp-stubble shadow on the male -- are mixed into colour attributes and
  baked with Cycles (EMIT) into a 2048 px atlas, an ambient-occlusion bake
  (10 cm reach: nostrils, sockets, ear folds) multiplied in (not across the
  lips: their seam read as an open mouth), plus low-frequency blotches and
  fine pores. After the brows are fitted their footprint is splatted in as
  a soft shadow. A tangent-space normal map is baked from the dense skin
  onto the decimated head (selected-to-active, 2 mm cage), flattened on the
  lip seam and the ears (rays there hit the wrong fold). Both maps are JPEG
  (q90) in `build/tex/` and packed into the glb; the export writes tangents
  for `lod` variants. Costs: +~0.43 MB per Colonist glb (2.13 -> 2.56 MB).
  No MPFB skin textures are used (its masks ship under the GPL with the
  add-on); everything is derived from the CC0 base mesh.
- **Gloves** (`skin.paint_glove`): the suit's gloves are what first person
  sees most. Their faces' MakeHuman UVs are packed into a 1024 px map of
  their own (the tiling fabric maps, on box UVs, are dropped for them):
  the glove colour with a worn, lighter sheen over each finger joint on the
  back of the hand (soft blobs at the finger bones' heads, facing away from
  the palm), an AO bake (finger creases, the gaps between fingers) and a
  fine weave. No nails: the hands are gloved.
- **Texture budget** (verify.mjs): every embedded image <= 2048 px a side,
  every body glb (`char.*`, `npc.*`) <= 8 MB.
- **Hair material**: the pack's strand textures are greyscale (the pack
  tints them in its own shader; raw they draw white). `hair_material()`
  multiplies the texture (256 px) by `HAIR_TINT`, exported as
  baseColorTexture x baseColorFactor.

### Hair (`tools/bpy/hair.py`)

Hair is a worn piece, never part of a body (`hair.none` = bald). Every
glTF in the vendored "Rigged to Head Bone" folder except the eyebrows is a
style: `hair.buzzed`, `hair.buzzed_female`, `hair.simple_parted`,
`hair.long`, `hair.buns`, `hair.beard`. For each body (`BODIES`: the base
MakeHuman build -> the unsuffixed `hair.<style>`, then `@char.player`,
`@char.player.f`, `@char.ubc`, `@char.ubc.f`) hair.py:

1. builds the body's rig (human.py) and takes the head + shoulders of the
   SHIPPED body glb as the target surface (the decimated / simplified head
   the hair will sit on);
2. picks the UBC head the style was authored on (the one fewest of its
   vertices sink into: `long`, `buns`, `buzzed_female` are female);
3. decimates under 1500 tris, maps the source skull's box onto the
   target's per axis, then `refit()` (reach 4 cm: buns and ponytails keep
   their shape, the inner surface follows the scalp), nothing closer than
   2 mm to the skin;
4. one mesh `hair`, material `hair`, 100 % on `head`, exported with the
   body's rig to `build/hair.<style>[@<body>].raw.glb`.

Recipes `recipes/hair.<style>.json` (`bodies`) finish them to
`hair/<style>[.<body>].glb`. The client wears hair exactly like armor:
the server's `worn` event for slot `hair` names `hair.<style>`, and
`Entities.Dress` takes `hair.<style>@<wearer>` when the manifest has it
(the unsuffixed piece otherwise). `hair.none` is a manifest row with
`file: null` and `tris: 0`; the client's registry skips rows without a
file, so wearing it hangs nothing. verify.mjs holds every `hair.*` glb to
one mesh named `hair`, every primitive in material `hair`, weights on the
`head` bone only, 1500 tris.

    blender -b --python tools/bpy/hair.py [-- char.player ...|base]
    for r in recipes/hair.*.json; do node tools/import_pack.mjs $r; done

Contact sheets for any of this: `blender -b --python tools/render_sheet.py
-- out.png body.glb,piece.glb[,…] [another group …]` (EEVEE, key/fill/rim,
every group a row at the same scale with four head close-ups). Rebuilding
one body's armor without re-finishing the others:
`ONLY=<id>@<body>,… node tools/import_pack.mjs recipes/<id>.json`.

## Surface textures and decals (2026-10-01)

`tools/bpy/textures.py` paints every armor and suit material with numpy
inside Blender. Each material gets a TILING 256 px albedo and a normal map
built from one height field:

- **plate**: panel lines, rivets and light scratches.
- **trim**: fine noise.
- **metal**: brushed.
- **iron**: brushed, with pits and scratches.
- **fabric** (straps, gloves, boots): a weave.
- **suit**: a weave with quilted seams and stitching.

`uv_box` projects UVs in world METRES (one repeat = 0.4 m), so texel size
matches across parts. Normal strength is set per kind (`STRENGTH`). A
strong cloth weave read as zebra stripes on the first-person gloves.

Decals come from a 2x2 stencil atlas: the squad number "07", a chevron,
hazard stripes and an insignia. `decal()` raycasts onto a plate and lays a
quad 1.5 mm above it. The quad is joined into the part, so it rides the
same bone. The material is alpha-clipped (glTF `MASK`). Placements are at
the end of armor.py `pieces()`. Both generators export texcoords and
embedded PNGs. verify.mjs stubs texture decoding, since three.js wants a
browser for images. The first-person shader samples the albedo and normal
maps too.

Self-check: `blender -b --python tools/bpy/textures.py`.

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
The same file builds the rest of the armoury: `weapon.smg` (168 tris),
`weapon.dmr` (348, a HOLLOW scope tube with a reticle so aiming looks
through it) and `weapon.sidearm` (120). The long guns share the rifle's
`grip`→`fore` geometry, so one set of hold clips fits all of them. The
pistol's `fore` is where the support palm cups the fist (under the grip).
That is too short to steer the barrel, so the client rides the barrel
along the forearm for it. human.py builds a `*_pistol` copy of every armed
clip (`CLASSES`), and `weapon.class` picks it. Aimed, a pistol's rear
sight sits 0.42 m out (arm's length), a long gun's 0.20 m.

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

## The mob library (from 2026-10-03)

Creatures and characters from Quaternius packs, each on **its own skeleton
with its own clips** (not the humanoid rig above). The catalog is
`mobs/mobs.json` (source file, clip map, role, family scale, provenance per
pack); `mobs/CATALOG.md` is the generated, browsable list with thumbnails.

    npm --prefix art run mobs                  # everything
    blender -b --python tools/bpy/mobs.py -- mob.big.orc && node tools/mobs.mjs mob.big.orc

- **Blender pass** (`tools/bpy/mobs.py`): imports the pack file from
  `vendor/` (gitignored; mobs.json `packs` says where each came from), renames
  the clips we play to `idle`/`walk`/`sprint`/`die`/`hit`/`attack` and the rest
  to snake_case, turns the model to face -Z (every pack faces glTF +Z), puts
  its lowest rest-pose point at 0, applies the family scale (Ultimate Monsters
  is authored at toy scale), caps textures at 1024 px, measures it and renders
  `mobs/thumbs/<family>_<name>.png`. A model shipped without clips (Bestiary)
  gets them retargeted from the Universal Animation Library: same UE5 bone
  names, each bone takes the source bone's rotation relative to its rest, the
  pelvis moves scaled by pelvis height.
- **Finish** (`tools/mobs.mjs`): manifest row (rig `creature`), import_pack's
  dedup/prune into `mobs/*.glb`, and one hostile archetype per mob in
  `../server/data/mobs.json` (generated; do not edit) with the measured
  radius/height/eye height and stats from the role table, scaled by height.
- **verify**: `mob.*` budget 20k; rig `creature` requires only `idle`. The
  client falls back sprint -> walk -> idle and fells a body that has no `die`.
- **Licences**: all CC0 except the Bestiary (Quaternius Asset License v1.0:
  use in games, no redistribution of the assets themselves), committed at the
  owner's decision.
