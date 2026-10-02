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
        public Vec3 Vel; // the newest row's; extrapolation only
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
    /// about T + oneWay now. Each arrival is one noisy sample of the offset
    /// between our clock and the server's; the render clock follows their
    /// running average, so arrival jitter no longer jerks remotes back and
    /// forth (it used to re-anchor on every packet).
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

        /// <summary>Per-snapshot weight of a new clock sample: ~1 s to settle at 20 Hz.</summary>
        private const double OffsetGain = 0.05;

        /// <summary>A sample this far off the average is a real clock change (stall, rejoin): adopt it.</summary>
        private const double OffsetResync = 0.25;

        /// <summary>
        /// How far past the newest snapshot a remote may be carried along its
        /// last velocity before it holds. Covers a late packet or a one-way
        /// trip up to interp_delay + this; beyond, remotes hold (the old rule
        /// for every connection over 200 ms RTT).
        /// </summary>
        public const double MaxExtrapolateSeconds = 0.1;

        private readonly List<(uint Tick, double At, Dictionary<uint, Pose> Poses)> _buf =
            new List<(uint, double, Dictionary<uint, Pose>)>();

        // Server seconds (tick / TickHz) minus local seconds, averaged.
        private double _offset;
        private bool _haveOffset;

        private readonly Dictionary<uint, Pose> _out = new Dictionary<uint, Pose>();

        /// <summary>One-way trip in seconds, from the transport's RTT.</summary>
        public double OneWaySeconds { get; set; }

        public void Add(Snapshot snap, double atTime)
        {
            var poses = new Dictionary<uint, Pose>(snap.Entities.Length);
            foreach (var e in snap.Entities)
            {
                poses[e.Id] = new Pose
                {
                    Pos = new Vec3(e.PosX, e.PosY, e.PosZ),
                    Facing = FacingOf(e),
                    Vel = new Vec3(e.VelX, e.VelY, e.VelZ),
                    Health = e.Health,
                    Dead = e.Dead,
                    ParentId = e.ParentId,
                    Seat = e.Seat,
                };
            }
            _buf.Add((snap.Tick, atTime, poses));
            if (_buf.Count > MaxBuffered) _buf.RemoveAt(0);

            double sample = snap.Tick / (double)Rules.TickHz - atTime;
            if (!_haveOffset || System.Math.Abs(sample - _offset) > OffsetResync) _offset = sample;
            else _offset += (sample - _offset) * OffsetGain;
            _haveOffset = true;
        }

        public void Clear() { _buf.Clear(); _haveOffset = false; }

        /// <summary>
        /// The world at `serverClock - interp_delay`, interpolated between the
        /// two snapshots that bracket it, on the caller's clock (`now`, the
        /// same timebase the `atTime` values passed to Add came from). Runs
        /// past the newest snapshot when the render point is past it, which
        /// happens whenever the one-way trip exceeds interp_delay — i.e. on
        /// any connection worse than 200 ms round trip — or a packet is late.
        /// Past the newest snapshot it carries each entity along its wire
        /// velocity, for at most MaxExtrapolateSeconds, then holds.
        /// </summary>
        public IEnumerable<KeyValuePair<uint, Pose>> Interpolate(double now)
        {
            _out.Clear();
            if (_buf.Count == 0) return _out;

            var newest = _buf[_buf.Count - 1];
            double tickSeconds = 1.0 / Rules.TickHz;

            // Server tick now, then the render point interp_delay behind it,
            // expressed on the same tick timeline the snapshots carry.
            double serverNow = (now + _offset + OneWaySeconds) / tickSeconds;
            double renderTick = serverNow - InterpDelaySeconds / tickSeconds;

            if (renderTick >= newest.Tick)
            {
                // Along the wire velocity, not the last two positions: a
                // respawn is a jump with zero velocity, and must not overshoot.
                double ahead = System.Math.Min((renderTick - newest.Tick) * tickSeconds, MaxExtrapolateSeconds);
                foreach (var kv in newest.Poses)
                {
                    Pose p = kv.Value;
                    if (p.ParentId == 0 && !p.Dead) p.Pos += p.Vel * ahead;
                    _out[kv.Key] = p;
                }
                return _out;
            }

            for (int i = _buf.Count - 1; i >= 1; i--)
            {
                var b = _buf[i];
                var a = _buf[i - 1];
                if (renderTick < a.Tick) continue;

                double span = b.Tick - a.Tick;
                float k = span > 0 ? (float)((renderTick - a.Tick) / span) : 1f;
                return Blend(a.Poses, b.Poses, k);
            }

            foreach (var kv in _buf[0].Poses) _out[kv.Key] = kv.Value;
            return _out;
        }

        /// <summary>
        /// Every entity in <paramref name="to"/> at k along from→to.
        /// Discrete fields are the newer row's.
        /// </summary>
        private Dictionary<uint, Pose> Blend(Dictionary<uint, Pose> fromPoses, Dictionary<uint, Pose> to, float k)
        {
            foreach (var kv in to)
            {
                Pose p = kv.Value; // appeared this tick: the newer pose
                if (fromPoses.TryGetValue(kv.Key, out var from))
                {
                    // Occupancy is discrete: the newer row's. Dropping it
                    // drew every seated remote standing on its vehicle.
                    p.Pos = Lerp(from.Pos, p.Pos, k);
                    p.Facing = Lerp(from.Facing, p.Facing, k);
                }
                _out[kv.Key] = p;
            }
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
