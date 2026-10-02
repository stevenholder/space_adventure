// Local player prediction + replay reconciliation (ARCHITECTURE "Network
// model"; C42).
//
// The rule, and the reason it is a rule: when a snapshot disagrees with what
// we predicted, we SNAP to the server's state and replay every input the
// server has not acknowledged yet. We never blend toward it. Blending looks
// smoother in a screenshot and is wrong in motion — it leaves the local body
// somewhere neither the client nor the server believes in, which turns every
// collision and every shot into a disagreement that grows with latency.
// (The DRAWN body does ease out of the snap -- Smoothing.cs -- but nothing
// reads that back; the simulated state never blends.)
//
// Mirrors the retired TypeScript client's predictor (git history), which C42
// compares against on an identical input trace.
//
// No UnityEngine here either: this is arithmetic over Sim types, and keeping
// it engine-free means it can be replayed headless against a recorded trace.

using System;
using System.Collections.Generic;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    /// <summary>One input we sent and may have to replay.</summary>
    internal readonly struct PendingInput
    {
        public readonly ushort Seq;
        public readonly Input Input;

        public PendingInput(ushort seq, Input input) { Seq = seq; Input = input; }
    }

    /// <summary>
    /// Predicts the local body forward from its own inputs and reconciles
    /// against the server's authoritative snapshots.
    /// </summary>
    public sealed class Predictor
    {
        /// <summary>
        /// How many unacked inputs to keep. At 20 Hz this is 5 s of input,
        /// far more than any survivable RTT; the cap exists so a stalled
        /// connection cannot grow the list without bound.
        /// </summary>
        private const int MaxPending = 100;

        private readonly List<PendingInput> _pending = new List<PendingInput>();

        private State _state;
        private Vec3 _prevLook;
        private TerrainField _terrain;
        private Collider[] _colliders = Array.Empty<Collider>();

        private int _lastAck = -1;

        /// <summary>The predicted state right now. What the camera follows.</summary>
        public State State => _state;

        /// <summary>The drawn position: blended between ticks, corrections eased out.</summary>
        public readonly RenderSmoother Smooth = new RenderSmoother();

        /// <summary>
        /// Shoves the predicted position off the truth, on purpose.
        ///
        /// This is the C# half of the retired TypeScript client's `?corrupt`
        /// dev override, and it exists for one caller: the authority harness,
        /// which forces the prediction 4 m off and then asserts the server
        /// drags it back within a tick. C3 is "the server is authoritative",
        /// and the only way to test that is to lie to the server and watch it
        /// refuse — which needs a client that CAN lie.
        ///
        /// Prediction is client-side by definition, so this grants nothing:
        /// the server never reads it, and the very next snapshot overwrites
        /// it. That is the whole assertion.
        /// </summary>
        public void ForceOffset(Vec3 delta) => _state.Pos += delta;

        /// <summary>Unacknowledged inputs currently being replayed on top of the server's state.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Distance the last reconcile moved the body, in metres. HUD/telemetry.</summary>
        public double LastCorrection { get; private set; }

        public bool Ready => _terrain != null;

        /// <summary>
        /// Seeds prediction from the terrain's spawn rule. Until the first
        /// snapshot reconciles, this is all the client has.
        /// </summary>
        public void Seed(TerrainField terrain, Collider[] colliders)
        {
            _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
            _colliders = colliders ?? Array.Empty<Collider>();
            _state = Step.SpawnState(_terrain);
            _prevLook = _state.Facing;
            _pending.Clear();
            _lastAck = -1;
            Smooth.Reset();
        }

        /// <summary>Replaces the collider set (arrives after terrain).</summary>
        public void SetColliders(Collider[] colliders) => _colliders = colliders ?? Array.Empty<Collider>();

        /// <summary>
        /// Applies one input locally and remembers it for replay. Call once
        /// per fixed tick, with the same seq that goes on the wire.
        /// </summary>
        public void Apply(ushort seq, Input input)
        {
            if (_terrain == null) return;
            input.Colliders = _colliders;
            _prevLook = Step.Apply(ref _state, input, _prevLook, _terrain, Rules.DT);

            _pending.Add(new PendingInput(seq, input));
            if (_pending.Count > MaxPending) _pending.RemoveAt(0);
            Smooth.Stepped(_state.Pos, Quat.Identity);
        }

        /// <summary>
        /// Reconciles against the server's row for our own body.
        ///
        /// Snap, then replay: adopt the server's state verbatim, drop every
        /// input it has already applied, and re-run the rest. The result is
        /// the state the server WILL reach once it has seen those inputs, so
        /// the local body stays ahead of the wire without ever diverging from
        /// the rules the server uses.
        /// </summary>
        /// <returns>true when the snapshot was fresh enough to act on.</returns>
        public bool Reconcile(Vec3 serverPos, Vec3 serverVel, Vec3 serverFacing, bool grounded, ushort ackSeq)
        {
            if (_terrain == null) return false;

            // Sequence numbers are u16 and wrap. Comparing them as plain
            // integers makes the client ignore every snapshot for the rest of
            // the session the first time the counter passes 65535.
            if (_lastAck >= 0 && !SeqNewer(ackSeq, (ushort)_lastAck)) return false;
            _lastAck = ackSeq;

            Vec3 before = _state.Pos;

            _state = new State
            {
                Pos = serverPos,
                Vel = serverVel,
                Facing = serverFacing,
                Grounded = grounded,
            };

            // Everything the server has already folded in is history.
            _pending.RemoveAll(p => !SeqNewer(p.Seq, ackSeq));

            Vec3 look = serverFacing;
            foreach (var p in _pending)
            {
                var input = p.Input;
                input.Colliders = _colliders;
                look = Step.Apply(ref _state, input, look, _terrain, Rules.DT);
            }
            _prevLook = look;

            LastCorrection = (_state.Pos - before).Length;
            Smooth.Corrected(_state.Pos, Quat.Identity);
            return true;
        }

        /// <summary>
        /// Wrap-safe "is a newer than b" over a u16 counter: true when the
        /// forward distance from b to a is less than half the space.
        /// </summary>
        public static bool SeqNewer(ushort a, ushort b) => (ushort)(a - b) != 0 && (ushort)(a - b) < 0x8000;

        /// <summary>Drops prediction state on disconnect; the rejoin re-seeds.</summary>
        public void Reset()
        {
            _pending.Clear();
            _lastAck = -1;
            LastCorrection = 0;
            Smooth.Reset();
        }
    }
}
