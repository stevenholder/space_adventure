# Game design document — Space Adventure

Status: **v0 seed.** Owner: `game` agent. This file is the design contract:
implementers build from it; when it and code disagree, **this file wins**.

## Vision

A low-poly MMO where you fly between star systems, land on fantasy planets,
and adventure with other players. Space is the connective tissue; planets are
the destination. Charm comes from style (low-poly, flat-shaded, bold color)
and feel (responsive flight, smooth multiplayer) — not from detail.

## Pillars

1. **Feel first** — flight is responsive and weighty; latency is hidden,
   never felt.
2. **Legible low-poly** — every shape reads at a glance; silhouette over
   detail.
3. **Shared world** — other players are the content: visible, present,
   persistent.
4. **Small worlds, deep pockets** — one system at a time, fully built, not
   half-finished infinity.

## Core loop (target)

Fly between points of interest in a system → dock/land → explore and interact
on a fantasy planet → gather/quest → return to space → repeat — with other
players visible throughout.

## M1 scope (space flight)

**In scope**

- One open space volume (no planets in M1): starfield + asteroid-belt props.
- Fly a low-poly ship: arcade 6-DOF-lite (thrust + yaw/pitch/roll rates).
- Multiple players in the same space simultaneously; server-authoritative at
  20 Hz.
- Minimal HUD: speed, distance to nearest player, connection state.

**Parked (explicitly out of M1 scope)**

- Planets, gravity, walking, docking.
- Combat, weapons, damage.
- Economy, inventory, accounts, persistence, chat.

## M1 flight model (spec for `netcode` + `frontend`)

- Controls: W/S — thrust forward/back; mouse or arrow keys — yaw/pitch/roll
  rates; Shift — boost.
- Units: 1 unit = 1 m.
- World: 2,000 × 2,000 × 2,000 volume; positions clamp at ±1,000 (no walls or
  wrap in M1).
- Rotation: first-order response toward the input's target angular velocity
  (time constant τ).
- Translation: acceleration along ship forward; speed clamped; exponential
  decay when unthrottled (drift with a game-feel half-life).
- Spawn: world center, random yaw, zero velocity.

Rule table (implementers treat as spec — every value is named):

| name | value | unit | note |
|------|-------|------|------|
| `accel` | 12 | u/s² | base forward acceleration |
| `accel_boost` | 24 | u/s² | with `action_mask` boost |
| `vmax` | 40 | u/s | speed clamp |
| `vmax_boost` | 80 | u/s | speed clamp while boosting |
| `damp` | 0.5 | 1/s | `v *= exp(-damp·dt)` when unthrottled (half-life ≈ 1.4 s) |
| `angvel_max` | 4 | rad/s | target angular velocity per axis |
| `angvel_tau` | 0.15 | s | first-order response time constant |
| `world_half` | 1000 | u | position clamp radius |
| `spawn_pos` | (0, 0, 0) | u | world center |
| `spawn_yaw` | random ∈ [0, 2π) | rad | |
| `tick_hz` | 20 | Hz | server tick (matches PROTOCOL) |

Edge cases:

- Backward thrust (S): `accel · 0.5`, same `vmax` clamp (asymmetric for
  control feel).
- Boost with no thrust input: no effect (boost scales thrust, not speed).
- Input missing (client silent): server holds last input state; heartbeat
  timeout despawns (PROTOCOL semantics).
- Client prediction uses the identical rule table — divergence is corrected
  by snapshot blend (ARCHITECTURE "Network model").

## Open questions (main thread decides)

- Arcade vs true 6-DOF: M1 is arcade (above); revisit after an M1 feel pass.
- Concurrent player target for local dev: assume 10–50.
- Ship customization: parked until M1 ships.
