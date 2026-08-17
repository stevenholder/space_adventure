# Wire protocol (client ↔ server)

Status: **v0 draft — M1 implements exactly this set.** Both ends implement
from this file. A change requires both ends updated in the same milestone;
the main thread coordinates the edit.

## Transport

- WebSocket, binary messages only. No text frames.
- All integers little-endian. No padding, no alignment.

## Frame

```
frame = u16 type | u32 payload_len | payload[payload_len]
```

Max frame size: 64 KiB. A frame that exceeds it closes the connection
(code 1009).

## Message types

| id | name | dir | payload |
|----|------|-----|---------|
| `0x0001` | `hello` | C→S | `u16 client_ver` \| `u32 name_len` \| `bytes name` |
| `0x0002` | `hello_ack` | S→C | `u16 server_ver` \| `u16 tick_hz` \| `u32 world_seed` \| `u32 entity_id` |
| `0x0003` | `input` | C→S | `f32 thrust` \| `f32 yaw_rate` \| `f32 pitch_rate` \| `f32 roll_rate` \| `u16 action_mask` |
| `0x0004` | `snapshot` | S→C | `u32 tick` \| `u16 count` \| `entity × count` |
| `0x0005` | `spawn` | S→C | `u32 entity_id` \| `u16 entity_type` \| `u32 data_len` \| `bytes data` |
| `0x0006` | `despawn` | S→C | `u32 entity_id` |
| `0x0007` | `event` | S→C | `u32 entity_id` \| `u16 event_id` \| `u32 data_len` \| `bytes data` |
| `0x0008` | `ping` | C→S | `u32 ts_ms` |
| `0x0009` | `pong` | S→C | `u32 ts_ms` (echo of ping) |

`entity` (inside `snapshot`):

```
u32 entity_id | f32 pos[3] | f32 quat[4] | f32 vel[3]
```

Constants:

- `entity_type`: `0x0001` ship
- `action_mask` bits: `0x0001` boost
- `event_id`: `0x0001` explosion (reserved for M2+)

## Semantics

- Server ticks at 20 Hz and sends one `snapshot` (full state) per tick.
- A client sends `hello` on connect; the server replies `hello_ack` with the
  client's `entity_id`, then includes the entity in snapshots.
- `input` is **current command state**, not an event stream: the server
  applies the latest received input every tick (constant between inputs).
  No sequence numbers, no replay — see ARCHITECTURE "Network model".
- `spawn`/`despawn` are sent to clients as entities enter/leave the world
  (M1: player join/leave, heartbeat timeout).
- Heartbeat: client sends `ping` every 2 s if no other traffic; server
  answers `pong` and drops connections silent for 10 s.
- Reconnect (M1): connection loss = entity despawns (after heartbeat
  timeout); reconnecting yields a new `entity_id`.

## Versioning

`client_ver` / `server_ver` are u16 protocol versions; M1 = `1`. The server
rejects `hello` with a different major version by closing (code 1002).
