// Sound, code-first: every sound is synthesized into an AudioStreamWav at
// startup (C91: no resource files), then played flat (our own) or placed in
// the world (everyone else's). Shots differ by weapon family, so a DMR crack
// and an SMG rattle read apart before you see who fired.
//
// ponytail: synthesized one-shots, no mixer buses or occlusion. Swap a
// stream for a recorded .wav under art/ when real sound design arrives.

using System;
using System.Collections.Generic;
using Godot;

namespace SpaceAdventure.Game
{
    public sealed class Sfx
    {
        private const int Rate = 22050;
        private readonly Node _root;
        private readonly Dictionary<string, AudioStreamWav> _shot = new Dictionary<string, AudioStreamWav>();
        private readonly AudioStreamWav[] _steps = new AudioStreamWav[4];
        private readonly AudioStreamWav _impact, _magOut, _magIn, _rack, _dry;

        public Sfx(Node root)
        {
            _root = root;
            var rng = new Random(7);
            // weapon family -> (crack pitch Hz, length s, body thump Hz, noise share)
            _shot["rifle"] = Shot(rng, 900, 0.22, 110, 0.65);
            _shot["smg"] = Shot(rng, 1300, 0.12, 150, 0.7);
            _shot["dmr"] = Shot(rng, 600, 0.45, 70, 0.6);
            _shot["pistol"] = Shot(rng, 1100, 0.16, 130, 0.65);
            for (int i = 0; i < _steps.Length; i++) _steps[i] = Step(rng, 0.85 + 0.1 * i);
            _impact = Impact(rng);
            _magOut = Click(rng, 1800, 0.05, 2);
            _magIn = Click(rng, 1200, 0.07, 2);
            _rack = Click(rng, 2400, 0.09, 3);
            _dry = Click(rng, 3000, 0.03, 1);
        }

        /// <summary>Every sound by name (self-test, -dumpSfx).</summary>
        public (string name, AudioStreamWav wav)[] All()
        {
            var all = new List<(string, AudioStreamWav)>();
            foreach (var kv in _shot) all.Add(("shot_" + kv.Key, kv.Value));
            for (int i = 0; i < _steps.Length; i++) all.Add(($"step_{i}", _steps[i]));
            all.Add(("impact", _impact)); all.Add(("mag_out", _magOut)); all.Add(("mag_in", _magIn));
            all.Add(("rack", _rack)); all.Add(("dry", _dry));
            return all.ToArray();
        }

        /// <summary>The family a weapon item sounds like.</summary>
        public static string Family(string item) =>
            item == "weapon.smg" ? "smg" : item == "weapon.dmr" ? "dmr" : item == "weapon.sidearm" ? "pistol" : "rifle";

        public void OwnShot(string item) => Play2D(_shot[Family(item)], -4f);
        public void ShotAt(Vector3 at, string item) => PlayAt(at, _shot[Family(item)], 2f, 120f);
        public void ImpactAt(Vector3 at) => PlayAt(at, _impact, -2f, 40f);
        public void StepAt(Vector3 at, int n) => PlayAt(at, _steps[n & 3], -10f, 18f);
        public void OwnStep(int n) => Play2D(_steps[n & 3], -18f);
        public void DryFire() => Play2D(_dry, -8f);

        /// <summary>Mag out, mag in, rack -- timed to the reload.</summary>
        public void Reload(double seconds)
        {
            Later(seconds * 0.22, () => Play2D(_magOut, -8f));
            Later(seconds * 0.60, () => Play2D(_magIn, -6f));
            Later(seconds * 0.85, () => Play2D(_rack, -6f));
        }

        private void Later(double s, Action a)
        {
            if (!_root.IsInsideTree()) return;
            _root.GetTree().CreateTimer(s).Timeout += a;
        }

        private void Play2D(AudioStreamWav s, float db)
        {
            var p = new AudioStreamPlayer { Stream = s, VolumeDb = db };
            _root.AddChild(p);
            p.Finished += p.QueueFree;
            p.Play();
        }

        private void PlayAt(Vector3 at, AudioStreamWav s, float db, float range)
        {
            var p = new AudioStreamPlayer3D { Stream = s, VolumeDb = db, MaxDistance = range, UnitSize = 4f };
            _root.AddChild(p);
            p.GlobalPosition = at;
            p.Finished += p.QueueFree;
            p.Play();
        }

        // ---- synthesis ---------------------------------------------------------

        /// <summary>A crack (bright decaying noise) over a thump (falling sine).</summary>
        private static AudioStreamWav Shot(Random rng, double crack, double len, double thump, double noise)
        {
            int n = (int)(Rate * len);
            var x = new float[n];
            double lp = 0, ph = 0, a = Math.Exp(-2 * Math.PI * crack / Rate);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                lp = a * lp + (1 - a) * (rng.NextDouble() * 2 - 1);      // one-pole low-pass noise
                double env = Math.Exp(-t / (len * 0.18));
                ph += 2 * Math.PI * thump * (1.0 + 2.0 * Math.Exp(-t * 40)) / Rate;
                x[i] = (float)(noise * lp * 3.0 * env + (1 - noise) * Math.Sin(ph) * Math.Exp(-t / (len * 0.35)));
            }
            return Wav(x);
        }

        /// <summary>A soft boot on ground: low noise, fast decay.</summary>
        private static AudioStreamWav Step(Random rng, double pitch)
        {
            int n = (int)(Rate * 0.12);
            var x = new float[n];
            double lp = 0, a = Math.Exp(-2 * Math.PI * 500 * pitch / Rate);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                lp = a * lp + (1 - a) * (rng.NextDouble() * 2 - 1);
                x[i] = (float)(lp * 4.0 * Math.Exp(-t / 0.025) * Math.Min(1.0, t / 0.004));
            }
            return Wav(x);
        }

        /// <summary>A round striking a body or rock: a dull smack.</summary>
        private static AudioStreamWav Impact(Random rng)
        {
            int n = (int)(Rate * 0.15);
            var x = new float[n];
            double lp = 0, a = Math.Exp(-2 * Math.PI * 1500 / Rate), ph = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                lp = a * lp + (1 - a) * (rng.NextDouble() * 2 - 1);
                ph += 2 * Math.PI * 220 / Rate;
                x[i] = (float)((lp * 2.5 + 0.5 * Math.Sin(ph)) * Math.Exp(-t / 0.03));
            }
            return Wav(x);
        }

        /// <summary>A mechanical click: `taps` short bright ticks a few ms apart.</summary>
        private static AudioStreamWav Click(Random rng, double hz, double len, int taps)
        {
            int n = (int)(Rate * len);
            var x = new float[n];
            for (int k = 0; k < taps; k++)
            {
                int at = (int)(Rate * (0.012 * k));
                for (int i = at; i < n; i++)
                {
                    double t = (double)(i - at) / Rate;
                    x[i] += (float)((Math.Sin(2 * Math.PI * hz * t) * 0.6 + (rng.NextDouble() * 2 - 1) * 0.4) * Math.Exp(-t / 0.004));
                }
            }
            return Wav(x);
        }

        internal static AudioStreamWav Wav(float[] x)
        {
            float peak = 1e-6f;
            foreach (float v in x) peak = Math.Max(peak, Math.Abs(v));
            var data = new byte[x.Length * 2];
            for (int i = 0; i < x.Length; i++)
            {
                short s = (short)Math.Round(x[i] / peak * 0.9f * short.MaxValue);
                data[2 * i] = (byte)(s & 0xff);
                data[2 * i + 1] = (byte)((s >> 8) & 0xff);
            }
            return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = data };
        }
    }
}
