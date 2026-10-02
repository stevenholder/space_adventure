// Phase 4 — rover prediction + replay (ROADMAP task 13), the same
// snap-then-replay contract as Prediction.cs, over Drive.Apply instead of
// Step.Apply. We never blend: blending leaves the rover somewhere neither
// end believes in, which turns every terrain contact into a disagreement
// that grows with latency (Prediction.cs has the full argument).
//
// No UnityEngine: engine-free so SimDump can drive it headless.

using System.Collections.Generic;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public sealed class RoverPredictor
    {
        private readonly struct Pending
        {
            public readonly ushort Seq;
            public readonly double Throttle, Steer;
            public Pending(ushort seq, double throttle, double steer)
            {
                Seq = seq; Throttle = throttle; Steer = steer;
            }
        }

        private const int MaxPending = 100;

        private readonly List<Pending> _pending = new List<Pending>();
        private RoverState _state;
        private TerrainField _terrain;
        private int _lastAck = -1;
        private bool _seeded;

        public RoverState State => _state;

        /// <summary>True once terrain is set and one snapshot reconciled.</summary>
        public bool Ready => _terrain != null && _seeded;

        public void Seed(TerrainField terrain) => _terrain = terrain;

        /// <summary>
        /// Everything solid but this rover (Boot: static + moving bodies),
        /// set before each Apply so prediction hits what the server's hits.
        /// </summary>
        public Collider[] Colliders;

        /// <summary>One driven tick, with the seq that goes on the wire.</summary>
        public void Apply(ushort seq, double throttle, double steer)
        {
            if (!Ready) return;
            Drive.Apply(ref _state, throttle, steer, _terrain, Rules.DT, 0, Colliders);
            _pending.Add(new Pending(seq, throttle, steer));
            if (_pending.Count > MaxPending) _pending.RemoveAt(0);
        }

        /// <summary>
        /// Snap to the server's rover row, drop acknowledged inputs, replay
        /// the rest. Grounded comes off the row's flag — the server mirrors
        /// the carried state onto the wire so this never re-derives it at
        /// the snap boundary.
        /// </summary>
        public bool Reconcile(Vec3 pos, Vec3 vel, Quat quat, bool grounded, ushort ackSeq)
        {
            if (_terrain == null) return false;
            if (_seeded && !Predictor.SeqNewer(ackSeq, (ushort)_lastAck)) return false;
            _lastAck = ackSeq;
            _seeded = true;

            _state = new RoverState { Pos = pos, Vel = vel, Quat = quat, Grounded = grounded };
            _pending.RemoveAll(p => !Predictor.SeqNewer(p.Seq, ackSeq));

            var s = _state;
            foreach (var p in _pending)
                Drive.Apply(ref s, p.Throttle, p.Steer, _terrain, Rules.DT, 0, Colliders);
            _state = s;
            return true;
        }

        /// <summary>Drops state on disembark or disconnect.</summary>
        public void Reset()
        {
            _pending.Clear();
            _lastAck = -1;
            _seeded = false;
        }
    }
}
