// The v2 wire messages (docs/PROTOCOL.md "Message types"), encoded and
// decoded byte-for-byte against the Go server.
//
// NO UnityEngine — see Wire.cs. This is a transliteration of the protocol
// table, not a redesign: field order, widths and names follow the document so
// that a disagreement with Go is traceable to one row of one table.
//
// Frames carry their own u16 type and no length prefix, because the WebSocket
// transport preserves message boundaries. Every encoder here returns a FULL
// frame including that type. Phase 2's first cut had the TypeScript encoders
// return bare payloads, which put a seq on the wire where the type belonged
// and was invisible to both sides' own unit tests (test/t12-codec-parity.mjs
// exists because of it), so this file never returns a payload alone.

using System;

namespace SpaceAdventure.Net
{
    /// <summary>Message type ids (PROTOCOL.md "Message types").</summary>
    public static class Msg
    {
        public const ushort Hello = 0x0001;
        public const ushort HelloAck = 0x0002;
        public const ushort Input = 0x0003;
        public const ushort Snapshot = 0x0004;
        public const ushort Spawn = 0x0005;
        public const ushort Despawn = 0x0006;
        public const ushort Event = 0x0007;
        public const ushort Ping = 0x0008;
        public const ushort Pong = 0x0009;
        public const ushort Terrain = 0x000A;
        public const ushort Board = 0x000B;      // Phase 4
        public const ushort Disembark = 0x000C;  // Phase 4
        public const ushort SeatResult = 0x000D; // Phase 4
        public const ushort Cmd = 0x000E;
        public const ushort CmdResult = 0x000F;
        public const ushort Defs = 0x0010;
        public const ushort Props = 0x0013;
        public const ushort Fire = 0x0011;
        public const ushort Colliders = 0x0012;
    }

    /// <summary>entity_type values (PROTOCOL.md constants).</summary>
    public static class EntityType
    {
        public const ushort Player = 0x0001;
        public const ushort Ship = 0x0002;     // Phase 5
        public const ushort Npc = 0x0003;
        public const ushort Target = 0x0004;
        public const ushort Vehicle = 0x0005;  // Phase 4
        public const ushort Loot = 0x0006;
        public const ushort Projectile = 0x0007;
    }

    /// <summary>action_mask bits.</summary>
    public static class Action
    {
        public const ushort Sprint = 0x0001;
        public const ushort Jump = 0x0002;
        public const ushort Boost = 0x0004; // Phase 5
    }

    /// <summary>Entity row flag bits.</summary>
    public static class EntityFlags
    {
        public const byte Grounded = 0x01;
        public const byte Sprinting = 0x02;
        public const byte Dead = 0x04;
        public const byte Firing = 0x08;
        public const byte Space = 0x10; // Phase 5: ship in the space regime
    }

    /// <summary>cmd opcodes.</summary>
    public static class Op
    {
        public const ushort ShopList = 0x0001;
        public const ushort ShopBuy = 0x0002;
        public const ushort Equip = 0x0003;
        public const ushort Inventory = 0x0004;
        public const ushort Reload = 0x0005;
        // Phase 10 (PROTOCOL.md): parties and missions, JSON bodies.
        public const ushort PartyInvite = 0x0006;
        public const ushort PartyRespond = 0x0007;
        public const ushort PartyLeave = 0x0008;
        public const ushort MissionList = 0x0009;
        public const ushort MissionAccept = 0x000A;
        public const ushort MissionAbandon = 0x000B;
        public const ushort MissionTurnin = 0x000C;
        public const ushort MissionShare = 0x000D;
        public const ushort Skills = 0x000E; // Phase 11: the full sheet
    }

    /// <summary>cmd_result status codes.</summary>
    public static class Status
    {
        public const byte Ok = 0;
        public const byte UnknownOpcode = 1;
        public const byte Malformed = 2;
        public const byte Refused = 3;
        public const byte RateLimited = 4;
        public const byte NotFound = 5;
    }

    /// <summary>event_id values.</summary>
    public static class EventId
    {
        public const ushort Explosion = 0x0001; // reserved
        public const ushort ShotFired = 0x0002;
        public const ushort Hit = 0x0003;
        public const ushort Death = 0x0004;
        public const ushort LootDropped = 0x0005;
        public const ushort Equipped = 0x0006; // Phase 3.5
        // Phase 10: JSON payloads.
        public const ushort MissionProgress = 0x0007;
        public const ushort MissionComplete = 0x0008;
        public const ushort PartyUpdate = 0x0009;
        public const ushort PriorityOffer = 0x000A;
        public const ushort PartyInvited = 0x000B;
        public const ushort MissionShared = 0x000C; // data = the full template
        public const ushort SkillXP = 0x000D; // Phase 11: {skill,xp,level,next_at,leveled}
    }

    /// <summary>collider kinds.</summary>
    public static class ColliderKind
    {
        public const byte Box = 0;
        public const byte Sphere = 1;
    }

    public static class Wire
    {
        /// <summary>Protocol version this client speaks (PROTOCOL.md v2).</summary>
        public const ushort VersionPhase2 = 2;

        /// <summary>Max WebSocket message size; a larger frame closes with 1009.</summary>
        public const int MaxMessageSize = 64 * 1024;

        /// <summary>Bytes in one snapshot entity row. Fixed, and asserted on decode.</summary>
        public const int EntitySize = 54;

        /// <summary>Bytes in one collider row.</summary>
        public const int ColliderSize = 42;

        /// <summary>Splits a frame into its type and a reader over the payload.</summary>
        public static ushort ReadFrameType(byte[] frame, out WireReader payload)
        {
            if (frame == null || frame.Length < 2)
            {
                throw new WireException($"frame: need 2 bytes of type, have {frame?.Length ?? 0}");
            }
            ushort type = (ushort)(frame[0] | (frame[1] << 8));
            payload = new WireReader(frame, 2, frame.Length - 2);
            return type;
        }
    }

    // ---- C→S encoders ------------------------------------------------------

    public static class Encode
    {
        /// <summary>`hello`: u16 client_ver | u32 name_len | name | u32 token_len | token.</summary>
        public static byte[] Hello(string name, string token)
            => new WireWriter(64)
                .U16(Msg.Hello)
                .U16(Wire.VersionPhase2)
                .LengthPrefixedUtf8(name)
                .LengthPrefixedUtf8(token)
                .ToArray();

        /// <summary>
        /// `input`: f32 v[5] | u16 action_mask | u16 seq | u8 mode.
        ///
        /// The mode byte is LAST and optional — appended, not prepended, so
        /// every field kept the offset it has had since Phase 1 and a 24-byte
        /// payload still reads as mode 0. This client always writes the 25-byte
        /// form; the server accepts exactly 24 or exactly 25 and rejects
        /// anything else, because a wrong length is nearly always a field
        /// alignment bug rather than a longer message.
        /// </summary>
        public static byte[] Input(float moveX, float moveY,
                                   float lookX, float lookY, float lookZ,
                                   ushort actionMask, ushort seq, byte mode = 0)
            => new WireWriter(27)
                .U16(Msg.Input)
                .F32(moveX).F32(moveY)
                .F32(lookX).F32(lookY).F32(lookZ)
                .U16(actionMask).U16(seq).U8(mode)
                .ToArray();

        /// <summary>`ping`: u32 ts_ms. The server echoes it as `pong`.</summary>
        public static byte[] Ping(uint tsMs)
            => new WireWriter(6).U16(Msg.Ping).U32(tsMs).ToArray();

        /// <summary>
        /// `board`: u32 vehicle_id | u16 seat. An event, not command state:
        /// sent once per request, answered by a unicast `seat_result`
        /// (PROTOCOL "board / disembark").
        /// </summary>
        public static byte[] Board(uint vehicleId, ushort seat)
            => new WireWriter(8)
                .U16(Msg.Board).U32(vehicleId).U16(seat)
                .ToArray();

        /// <summary>`disembark`: no payload — the server knows the seat.</summary>
        public static byte[] Disembark()
            => new WireWriter(2).U16(Msg.Disembark).ToArray();

        /// <summary>`cmd`: u16 seq | u16 opcode | u32 data_len | UTF-8 JSON body.</summary>
        public static byte[] Cmd(ushort seq, ushort opcode, string json)
            => new WireWriter(32)
                .U16(Msg.Cmd).U16(seq).U16(opcode)
                .LengthPrefixedUtf8(json)
                .ToArray();

        /// <summary>
        /// `fire`: u16 seq | f32 dir[3]. `seq` is the input.seq in effect when
        /// the trigger was pulled — the server rewinds by how far back that
        /// input executed, so sending a stale or invented seq changes where the
        /// shot lands (PROTOCOL.md "fire"; GDD "Lag compensation").
        /// </summary>
        public static byte[] Fire(ushort seq, float dirX, float dirY, float dirZ)
            => new WireWriter(16)
                .U16(Msg.Fire).U16(seq)
                .F32(dirX).F32(dirY).F32(dirZ)
                .ToArray();
    }

    // ---- S→C decoded messages ---------------------------------------------

    public struct HelloAck
    {
        public ushort ServerVer;
        public ushort TickHz;
        public uint WorldSeed;
        public uint EntityId;
    }

    /// <summary>One snapshot row — PROTOCOL.md's 54-byte entity, exactly.</summary>
    public struct EntityRow
    {
        public uint Id;
        public float PosX, PosY, PosZ;
        public float QuatX, QuatY, QuatZ, QuatW;
        public float VelX, VelY, VelZ;
        public uint ParentId; // Phase 4; zero until then
        public ushort Seat;   // Phase 4; zero until then
        public ushort Health;
        public byte Flags;
        public sbyte PitchQ;

        public bool Grounded => (Flags & EntityFlags.Grounded) != 0;
        public bool Sprinting => (Flags & EntityFlags.Sprinting) != 0;
        public bool Dead => (Flags & EntityFlags.Dead) != 0;
        public bool Firing => (Flags & EntityFlags.Firing) != 0;
        public bool Space => (Flags & EntityFlags.Space) != 0;

        /// <summary>
        /// View pitch in radians, un-quantising PROTOCOL's i8. Visual only —
        /// hit resolution never reads it, the server uses its own record of the
        /// shooter's aim.
        /// </summary>
        public float Pitch => PitchQ * ((float)Math.PI / 2f) / 127f;
    }

    public struct Snapshot
    {
        public uint Tick;
        public ushort AckSeq;
        public EntityRow[] Entities;
    }

    public struct Spawn
    {
        public uint EntityId;
        public ushort EntityType;
        public byte[] Data; // display name for a player; archetype id otherwise
        public string DataUtf8 => WireReader.Utf8.GetString(Data ?? Array.Empty<byte>());
    }

    public struct EventMsg
    {
        public uint EntityId;
        public ushort EventId;
        public byte[] Data;
    }

    /// <summary>
    /// The decoded `terrain` message. Named apart from Sim.TerrainField on
    /// purpose: this is the wire payload, that is the sampled field built from
    /// it, and the two live in assemblies a single file often references
    /// together.
    /// </summary>
    public struct TerrainMsg
    {
        public ushort FaceGrid;
        public float RadiusMin, RadiusMax;
        public ushort[] Radii; // 6 * FaceGrid * FaceGrid, face-major
    }

    public struct CmdResult
    {
        public ushort Seq;
        public ushort Opcode;
        public byte StatusCode;
        public string Body; // UTF-8 JSON
        public bool Ok => StatusCode == Status.Ok;
    }

    /// <summary>
    /// `seat_result` (Phase 4): the unicast answer to one board or disembark
    /// request. EntityId is the vehicle for board, the requester's own body
    /// for disembark. The authoritative occupancy change is what the next
    /// snapshot's parent_id/seat say — this only explains the refusal.
    /// </summary>
    public struct SeatResult
    {
        public const byte Granted = 0;
        public const byte Occupied = 1;
        public const byte OutOfRange = 2;
        public const byte Invalid = 3;

        public uint EntityId;
        public ushort Seat;
        public byte Result;
        public bool Ok => Result == Granted;
    }

    /// <summary>
    /// One piece of zone dressing (PROTOCOL `props`): a model id from
    /// art/manifest.json, where it stands, and how it is turned.
    ///
    /// Visual only. Props carry no collider and the sim never sees them, so a
    /// client that dropped this message entirely would still agree with the
    /// server about everything that can be walked into or shot.
    /// </summary>
    public struct Prop
    {
        public string Asset;
        public float PosX, PosY, PosZ;
        public float QuatX, QuatY, QuatZ, QuatW;
        public float Scale;
    }

    public struct Collider
    {
        public byte Kind;
        public float CenterX, CenterY, CenterZ;
        public float HalfX, HalfY, HalfZ; // sphere: HalfX is the radius
        public float QuatX, QuatY, QuatZ, QuatW;
    }

    // ---- S→C decoders ------------------------------------------------------

    public static class Decode
    {
        public static HelloAck HelloAck(WireReader r)
        {
            var h = new HelloAck
            {
                ServerVer = r.ReadU16("hello_ack server_ver"),
                TickHz = r.ReadU16("hello_ack tick_hz"),
                WorldSeed = r.ReadU32("hello_ack world_seed"),
                EntityId = r.ReadU32("hello_ack entity_id"),
            };
            r.ExpectEnd("hello_ack");
            return h;
        }

        public static Snapshot Snapshot(WireReader r)
        {
            var s = new Snapshot
            {
                Tick = r.ReadU32("snapshot tick"),
                AckSeq = r.ReadU16("snapshot ack_seq"),
            };
            int count = r.ReadU16("snapshot count");
            // Check the whole body up front. Sizing the array from a count the
            // payload cannot back is how a truncated frame becomes a
            // multi-megabyte allocation.
            int need = count * Wire.EntitySize;
            if (r.Remaining != need)
            {
                throw new WireException(
                    $"snapshot: {count} entities need {need} bytes, {r.Remaining} present");
            }
            var rows = new EntityRow[count];
            for (int i = 0; i < count; i++)
            {
                rows[i] = new EntityRow
                {
                    Id = r.ReadU32("entity id"),
                    PosX = r.ReadF32("pos.x"), PosY = r.ReadF32("pos.y"), PosZ = r.ReadF32("pos.z"),
                    QuatX = r.ReadF32("quat.x"), QuatY = r.ReadF32("quat.y"),
                    QuatZ = r.ReadF32("quat.z"), QuatW = r.ReadF32("quat.w"),
                    VelX = r.ReadF32("vel.x"), VelY = r.ReadF32("vel.y"), VelZ = r.ReadF32("vel.z"),
                    ParentId = r.ReadU32("parent_id"),
                    Seat = r.ReadU16("seat"),
                    Health = r.ReadU16("health"),
                    Flags = r.ReadU8("flags"),
                    PitchQ = r.ReadI8("pitch_q"),
                };
            }
            s.Entities = rows;
            return s;
        }

        public static Spawn Spawn(WireReader r)
        {
            uint id = r.ReadU32("spawn entity_id");
            ushort type = r.ReadU16("spawn entity_type");
            uint len = r.ReadU32("spawn data_len");
            if (len > (uint)r.Remaining)
            {
                throw new WireException($"spawn: data_len {len} exceeds {r.Remaining} remaining");
            }
            var sp = new Spawn { EntityId = id, EntityType = type, Data = r.ReadBytes((int)len, "spawn data") };
            r.ExpectEnd("spawn");
            return sp;
        }

        public static uint Despawn(WireReader r)
        {
            uint id = r.ReadU32("despawn entity_id");
            r.ExpectEnd("despawn");
            return id;
        }

        public static EventMsg Event(WireReader r)
        {
            uint id = r.ReadU32("event entity_id");
            ushort ev = r.ReadU16("event event_id");
            uint len = r.ReadU32("event data_len");
            if (len > (uint)r.Remaining)
            {
                throw new WireException($"event: data_len {len} exceeds {r.Remaining} remaining");
            }
            var e = new EventMsg { EntityId = id, EventId = ev, Data = r.ReadBytes((int)len, "event data") };
            r.ExpectEnd("event");
            return e;
        }

        public static uint Pong(WireReader r)
        {
            uint ts = r.ReadU32("pong ts_ms");
            r.ExpectEnd("pong");
            return ts;
        }

        public static TerrainMsg Terrain(WireReader r)
        {
            var t = new TerrainMsg
            {
                FaceGrid = r.ReadU16("terrain face_grid"),
                RadiusMin = r.ReadF32("terrain radius_min"),
                RadiusMax = r.ReadF32("terrain radius_max"),
            };
            int n = 6 * t.FaceGrid * t.FaceGrid;
            if (r.Remaining != n * 2)
            {
                throw new WireException(
                    $"terrain: face_grid {t.FaceGrid} needs {n * 2} bytes of radii, {r.Remaining} present");
            }
            var radii = new ushort[n];
            for (int i = 0; i < n; i++) radii[i] = r.ReadU16("radius");
            t.Radii = radii;
            return t;
        }

        public static CmdResult CmdResult(WireReader r)
        {
            var c = new CmdResult
            {
                Seq = r.ReadU16("cmd_result seq"),
                Opcode = r.ReadU16("cmd_result opcode"),
                StatusCode = r.ReadU8("cmd_result status"),
            };
            c.Body = r.ReadLengthPrefixedUtf8("cmd_result data");
            r.ExpectEnd("cmd_result");
            return c;
        }

        public static Defs Defs(WireReader r)
        {
            string json = r.ReadLengthPrefixedUtf8("defs data");
            r.ExpectEnd("defs");
            return SpaceAdventure.Net.Defs.Parse(json);
        }

        public static SeatResult SeatResult(WireReader r)
        {
            var s = new SeatResult
            {
                EntityId = r.ReadU32("seat_result entity_id"),
                Seat = r.ReadU16("seat_result seat"),
                Result = r.ReadU8("seat_result result"),
            };
            r.ExpectEnd("seat_result");
            return s;
        }

        /// <summary>
        /// Decodes `props`. Variable-length rows, unlike colliders, so this
        /// cannot check the total size up front -- it walks the count and lets
        /// the reader's own bounds checks catch a truncated frame.
        /// </summary>
        public static Prop[] Props(WireReader r)
        {
            int count = r.ReadU16("props count");
            var list = new Prop[count];
            for (int i = 0; i < count; i++)
            {
                list[i] = new Prop
                {
                    PosX = r.ReadF32("prop pos.x"),
                    PosY = r.ReadF32("prop pos.y"),
                    PosZ = r.ReadF32("prop pos.z"),
                    QuatX = r.ReadF32("prop quat.x"),
                    QuatY = r.ReadF32("prop quat.y"),
                    QuatZ = r.ReadF32("prop quat.z"),
                    QuatW = r.ReadF32("prop quat.w"),
                    Scale = r.ReadF32("prop scale"),
                    Asset = r.ReadU16Utf8("prop asset"),
                };
            }
            r.ExpectEnd("props");
            return list;
        }

        public static Collider[] Colliders(WireReader r)
        {
            int count = r.ReadU16("colliders count");
            int need = count * Wire.ColliderSize;
            if (r.Remaining != need)
            {
                throw new WireException(
                    $"colliders: {count} rows need {need} bytes, {r.Remaining} present");
            }
            var list = new Collider[count];
            for (int i = 0; i < count; i++)
            {
                byte kind = r.ReadU8("collider kind");
                r.ReadU8("collider _pad"); // sent as 0, keeps the row 4-byte aligned
                list[i] = new Collider
                {
                    Kind = kind,
                    CenterX = r.ReadF32("center.x"), CenterY = r.ReadF32("center.y"), CenterZ = r.ReadF32("center.z"),
                    HalfX = r.ReadF32("half.x"), HalfY = r.ReadF32("half.y"), HalfZ = r.ReadF32("half.z"),
                    QuatX = r.ReadF32("quat.x"), QuatY = r.ReadF32("quat.y"),
                    QuatZ = r.ReadF32("quat.z"), QuatW = r.ReadF32("quat.w"),
                };
            }
            return list;
        }
    }
}
