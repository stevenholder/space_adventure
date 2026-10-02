// Phase 5 — ship prediction + replay, the same snap-then-replay contract
// as the body and rover predictors, over Flight.Apply.
//
// One wrinkle the others don't have: ω is carried state that is NOT on
// the wire. The snapshot snaps pos/quat/vel (GDD "Prediction"), so the
// replay's starting ω comes from our own record — each pending input
// stores the ω that held AFTER it applied, and the entry matching the
// ack seq is bit-identical to the server's ω at that tick, because both
// sides ran the same first-order response over the same inputs from the
// same ω=0 start (an unpiloted ship's ω decays to zero, and boarding is
// only possible when parked).
//
// No UnityEngine: engine-free so SimDump can drive it headless.

using System.Collections.Generic;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public sealed class ShipPredictor
    {
        private readonly struct Pending
        {
            public readonly ushort Seq;
            public readonly FlightInput Input;
            public readonly Vec3 OmegaAfter;
            public Pending(ushort seq, FlightInput input, Vec3 omegaAfter)
            {
                Seq = seq; Input = input; OmegaAfter = omegaAfter;
            }
        }

        private const int MaxPending = 100;

        private readonly List<Pending> _pending = new List<Pending>();
        private ShipSimState _state;
        private TerrainField _terrain;
        private int _lastAck = -1;
        private bool _seeded;

        public ShipSimState State => _state;
        public bool Ready => _terrain != null && _seeded;

        /// <summary>Inputs still unacked — the replay depth (see Predictor).</summary>
        public int PendingCount => _pending.Count;

        public void Seed(TerrainField terrain) => _terrain = terrain;

        /// <summary>Everything solid but this ship, set before each Apply (RoverPredictor.Colliders).</summary>
        public Collider[] Colliders;

        public void Apply(ushort seq, FlightInput input)
        {
            if (!Ready) return;
            Flight.Apply(ref _state, input, _terrain, Rules.DT, Colliders);
            _pending.Add(new Pending(seq, input, _state.Omega));
            if (_pending.Count > MaxPending) _pending.RemoveAt(0);
        }

        public bool Reconcile(Vec3 pos, Vec3 vel, Quat quat, bool grounded, bool space, ushort ackSeq)
        {
            if (_terrain == null) return false;
            if (_seeded && !Predictor.SeqNewer(ackSeq, (ushort)_lastAck)) return false;

            // The replay's ω: our own record at the acked input, which is the
            // server's ω at that tick (see the file comment). First reconcile
            // (nothing pending yet) starts from zero — a parked ship's ω.
            Vec3 omega = Vec3.Zero;
            foreach (var p in _pending)
                if (p.Seq == ackSeq) { omega = p.OmegaAfter; break; }

            _lastAck = ackSeq;
            _seeded = true;

            _state = new ShipSimState
            {
                Pos = pos, Vel = vel, Quat = quat,
                Omega = omega, Grounded = grounded, Space = space,
            };
            _pending.RemoveAll(p => !Predictor.SeqNewer(p.Seq, ackSeq));

            var s = _state;
            foreach (var p in _pending)
                Flight.Apply(ref s, p.Input, _terrain, Rules.DT, Colliders);
            _state = s;
            return true;
        }

        public void Reset()
        {
            _pending.Clear();
            _lastAck = -1;
            _seeded = false;
        }
    }
}
