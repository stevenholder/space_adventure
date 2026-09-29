// U13 — the render clock remote entities are drawn on.
//
// THE RULE THIS FILE OWES THE SERVER: remotes are drawn at
// `serverClock - interp_delay`, on a clock synchronised to the server's. NOT
// at a fixed offset behind whenever a packet happened to arrive locally.
//
// That is not a stylistic choice. The server rewinds every shot by exactly
// interp_delay plus the one-way trip (GDD "Lag compensation"), so it is
// reconstructing this render point. Render on local arrival instead and the
// client sits a whole one-way trip further into the past than the server
// rewinds to — a player then misses every moving target, and no server-side
// test can see it. The retired TypeScript client had exactly that bug and C14
// caught it only at the wire level (docs/QA-STATUS.md).
//
// No UnityEngine here: the caller supplies the clock (`now`), so the timeline
// is arithmetic over Net/Sim types and SimDump can drive it headless — which
// is what lets U13 be verified at all.

using System.Collections.Generic;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    /// <summary>An entity's pose at the render instant.</summary>
    public struct Pose
    {
        public Vec3 Pos;
        public Vec3 Facing;
        public ushort Health;
        public bool Dead;

        // Phase 4 occupancy, carried through so the renderer can apply the
        // GDD rule "a seated body is not rendered" without a second lookup.
        public uint ParentId;
        public ushort Seat;
    }

    /// <summary>
    /// Keeps the last few snapshots and answers "where was everything at
    /// `serverClock - interp_delay`".
    ///
    /// The clock is estimated, not measured: a snapshot for tick T that
    /// arrives now means the server was at T one one-way trip ago, so it is at
    /// about T + oneWay now. Adding the elapsed time since arrival keeps the
    /// estimate moving between snapshots instead of stepping at 20 Hz.
    /// </summary>
    public sealed class SnapshotTimeline
    {
        /// <summary>
        /// interp_delay, in seconds. The GDD's number, and the server rewinds
        /// by it — changing this here alone silently breaks hit registration
        /// for this client only.
        /// </summary>
        public const double InterpDelaySeconds = 0.1;

        private const int MaxBuffered = 32;

        private readonly List<(uint Tick, float At, Dictionary<uint, Pose> Poses)> _buf =
            new List<(uint, float, Dictionary<uint, Pose>)>();

        private readonly Dictionary<uint, Pose> _out = new Dictionary<uint, Pose>();

        /// <summary>One-way trip in seconds, from the transport's RTT.</summary>
        public double OneWaySeconds { get; set; }

        public void Add(Snapshot snap, float atTime)
        {
            var poses = new Dictionary<uint, Pose>(snap.Entities.Length);
            foreach (var e in snap.Entities)
            {
                poses[e.Id] = new Pose
                {
                    Pos = new Vec3(e.PosX, e.PosY, e.PosZ),
                    Facing = FacingOf(e),
                    Health = e.Health,
                    Dead = e.Dead,
                    ParentId = e.ParentId,
                    Seat = e.Seat,
                };
            }
            _buf.Add((snap.Tick, atTime, poses));
            if (_buf.Count > MaxBuffered) _buf.RemoveAt(0);
        }

        public void Clear() => _buf.Clear();

        /// <summary>
        /// The world at `serverClock - interp_delay`, interpolated between the
        /// two snapshots that bracket it, on the caller's clock (`now`, the
        /// same timebase the `atTime` values passed to Add came from). Clamps
        /// to the newest snapshot when the render point is past it, which
        /// happens whenever the one-way trip exceeds interp_delay — i.e. on
        /// any connection worse than 200 ms round trip. (Clamping, not
        /// extrapolating: on such a connection remotes run one snapshot
        /// staler than the server's rewind point.)
        /// </summary>
        public IEnumerable<KeyValuePair<uint, Pose>> Interpolate(float now)
        {
            _out.Clear();
            if (_buf.Count == 0) return _out;

            var newest = _buf[_buf.Count - 1];
            double tickSeconds = 1.0 / Rules.TickHz;
            double elapsed = now - newest.At;

            // Server tick now, then the render point interp_delay behind it,
            // expressed on the same tick timeline the snapshots carry.
            double serverNow = newest.Tick + (elapsed + OneWaySeconds) / tickSeconds;
            double renderTick = serverNow - InterpDelaySeconds / tickSeconds;

            if (_buf.Count == 1 || renderTick >= newest.Tick)
            {
                foreach (var kv in newest.Poses) _out[kv.Key] = kv.Value;
                return _out;
            }

            for (int i = _buf.Count - 1; i >= 1; i--)
            {
                var b = _buf[i];
                var a = _buf[i - 1];
                if (renderTick < a.Tick) continue;

                double span = b.Tick - a.Tick;
                float k = span > 0 ? (float)((renderTick - a.Tick) / span) : 1f;
                foreach (var kv in b.Poses)
                {
                    if (a.Poses.TryGetValue(kv.Key, out var from))
                    {
                        _out[kv.Key] = new Pose
                        {
                            Pos = Lerp(from.Pos, kv.Value.Pos, k),
                            Facing = Lerp(from.Facing, kv.Value.Facing, k),
                            Health = kv.Value.Health,
                            Dead = kv.Value.Dead,
                        };
                    }
                    else
                    {
                        _out[kv.Key] = kv.Value; // appeared this tick
                    }
                }
                return _out;
            }

            foreach (var kv in _buf[0].Poses) _out[kv.Key] = kv.Value;
            return _out;
        }

        private static Vec3 Lerp(Vec3 a, Vec3 b, float k) => a + (b - a) * k;

        /// <summary>
        /// The row's quaternion turned back into a facing direction, in SIM
        /// space — the caller converts.
        ///
        /// The rotation is applied with Sim.Quat, not UnityEngine.Quaternion.
        /// The wire quaternion describes a right-handed basis whose Z axis is
        /// the facing (Step.OrientationQuat), and feeding those components to
        /// a left-handed Quaternion mixes the two conventions in a way that
        /// happens to look plausible and points bodies the wrong way.
        /// </summary>
        public static Vec3 FacingOf(EntityRow e)
        {
            if (e.QuatX == 0 && e.QuatY == 0 && e.QuatZ == 0 && e.QuatW == 0) return new Vec3(0, 0, 1);
            var q = new Quat(e.QuatX, e.QuatY, e.QuatZ, e.QuatW);
            return Quat.Rotate(q, new Vec3(0, 0, 1));
        }
    }
}
