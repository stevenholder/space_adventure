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

- **`char.player`** is **segmented, not skinned** — separate named nodes per
  limb, no skeleton and no skinning. It DOES carry animation clips: they are
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

M1 needs no ship. Vehicles are M2 — see `docs/ROADMAP.md`.

M1 adds the real player character + surface props behind these ids.
