// The drawn pose of a predicted body, as opposed to its simulated one.
//
// Prediction steps at 20 Hz and the screen draws at up to 120, so drawing
// the predicted state directly moves the camera in 50 ms stairs. This draws
// it blended between the last two predicted ticks (ARCHITECTURE "render loop
// interpolating between the last two predicted states"), one tick behind.
//
// A reconcile still SNAPS the simulated state (Prediction.cs has the reason).
// Only the drawn pose carries the jump as an offset that bleeds out over
// ~100 ms, so a small correction slides instead of popping. Nothing reads the
// drawn pose back into the sim.
//
// No UnityEngine/Godot: engine-free so SimDump can check it.

using System;
using SpaceAdventure.Sim;

namespace SpaceAdventure.Game
{
    public sealed class RenderSmoother
    {
        /// <summary>Correction time constant: ~95% gone after 3τ = 300 ms.</summary>
        public const double CorrectionTau = 0.1;

        /// <summary>A jump this big is a teleport (respawn, boarding), not an error: no slide.</summary>
        public const double SnapDistance = 4.0;

        private Vec3 _prev, _cur, _err;
        private Quat _prevQ = Quat.Identity, _curQ = Quat.Identity, _errQ = Quat.Identity;
        private bool _has;

        /// <summary>The pose to draw, set by Advance.</summary>
        public Vec3 Pos { get; private set; }
        public Quat Rot { get; private set; } = Quat.Identity;

        /// <summary>After each predicted tick, with the new state.</summary>
        public void Stepped(Vec3 pos, Quat q)
        {
            if (!_has) { Teleport(pos, q); return; }
            _prev = _cur; _prevQ = _curQ;
            _cur = pos; _curQ = q;
        }

        /// <summary>
        /// After a reconcile moved the predicted state. The drawn pose does
        /// not move this instant: the jump goes into the offset.
        /// </summary>
        public void Corrected(Vec3 pos, Quat q)
        {
            if (!_has || (pos - _cur).Length > SnapDistance) { Teleport(pos, q); return; }
            Vec3 d = pos - _cur;
            _err -= d;
            _prev += d;
            _cur = pos;
            // Same for rotation: err·new = old, and prev shifts with it.
            Quat dq = Mul(_curQ, Conj(q));
            _errQ = Norm(Mul(_errQ, dq));
            _prevQ = Norm(Mul(Conj(dq), _prevQ));
            _curQ = q;
        }

        /// <summary>
        /// Once per frame. <paramref name="alpha"/> is the fraction of a tick
        /// since the last predicted tick (accumulator / DT).
        /// </summary>
        public void Advance(double alpha, double dt)
        {
            double keep = Math.Exp(-dt / CorrectionTau);
            _err *= keep;
            _errQ = Nlerp(Quat.Identity, _errQ, keep);
            alpha = Math.Clamp(alpha, 0, 1);
            Pos = _prev + (_cur - _prev) * alpha + _err;
            Rot = Norm(Mul(_errQ, Nlerp(_prevQ, _curQ, alpha)));
        }

        public void Reset() => _has = false;

        private void Teleport(Vec3 pos, Quat q)
        {
            _prev = _cur = Pos = pos;
            _prevQ = _curQ = Rot = q;
            _err = Vec3.Zero; _errQ = Quat.Identity;
            _has = true;
        }

        private static Quat Mul(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        private static Quat Conj(Quat q) => new Quat(-q.X, -q.Y, -q.Z, q.W);

        private static Quat Norm(Quat q)
        {
            double n = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            return n < 1e-12 ? Quat.Identity : new Quat(q.X / n, q.Y / n, q.Z / n, q.W / n);
        }

        // ponytail: nlerp, not slerp -- the arcs are one 50 ms tick or a small
        // correction, where the speed error is invisible.
        private static Quat Nlerp(Quat a, Quat b, double t)
        {
            double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            double s = dot < 0 ? -1 : 1; // shortest way round
            return Norm(new Quat(
                a.X + (s * b.X - a.X) * t, a.Y + (s * b.Y - a.Y) * t,
                a.Z + (s * b.Z - a.Z) * t, a.W + (s * b.W - a.W) * t));
        }
    }
}
