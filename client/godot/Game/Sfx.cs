// Sound, code-first: every sound is synthesized into an AudioStreamWav at
// startup (C91: no resource files), then played flat (our own) or placed in
// the world (everyone else's). Shots differ by weapon family, so a DMR crack
// and an SMG rattle read apart before you see who fired.
//
// Placed sounds are muffled when something solid stands between them and the
// ear: a structure or rock (the sim colliders) or the ground itself (a hill,
// the planet's curve). Distance already dulls them -- AudioStreamPlayer3D's
// attenuation filter is on by default.
//
// ponytail: synthesized one-shots, one straight-line occlusion test, no
// reverb. Swap a stream for a recorded .wav under art/ when real sound
// design arrives; add reverb when there are interiors to ring.

using System;
using System.Collections.Generic;
using Godot;
using Collider = SpaceAdventure.Sim.Collider;
using TerrainField = SpaceAdventure.Sim.TerrainField;

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
            // Skins ("weapon.smg.frost") sound like the gun they reskin.
            item.StartsWith("weapon.smg") ? "smg" : item.StartsWith("weapon.dmr") ? "dmr"
            : item.StartsWith("weapon.sidearm") ? "pistol" : "rifle";

        public void OwnShot(string item) => Play2D(_shot[Family(item)], -4f);
        public void ShotAt(Vector3 at, string item) => PlayAt(at, _shot[Family(item)], 2f, 120f);
        public void ImpactAt(Vector3 at) => PlayAt(at, _impact, -2f, 40f);
        // Steps are background: -18/-10 dB was the loudest thing in the
        // 2026-10-01 playtest, -30/-24 still "a little loud" on 10-02. Others' carry 12 m, not 18, so a camp of
        // patrolling NPCs is not a drum line.
        public void StepAt(Vector3 at, int n) => PlayAt(at, _steps[n & 3], -27f, 12f);
        public void OwnStep(int n) => Play2D(_steps[n & 3], -33f);
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

        /// <summary>Where the listener is and what the world is made of; null = never muffle.</summary>
        public Func<Vector3> Ear;
        public Collider[] Colliders;
        public TerrainField Terrain;

        /// <summary>How much quieter, and how dull, a sound behind cover is.</summary>
        internal const float MuffleDb = -9f, MuffleHz = 700f;

        private void PlayAt(Vector3 at, AudioStreamWav s, float db, float range)
        {
            var p = new AudioStreamPlayer3D { Stream = s, VolumeDb = db, MaxDistance = range, UnitSize = 4f };
            if (Ear != null && Occluded(Ear(), at, Colliders, Terrain))
            {
                p.VolumeDb += MuffleDb;
                p.AttenuationFilterCutoffHz = MuffleHz;
            }
            _root.AddChild(p);
            p.GlobalPosition = at;
            p.Finished += p.QueueFree;
            p.Play();
        }

        /// <summary>
        /// Is the straight line from the ear to a sound blocked? Both ends are
        /// lifted 1 m off the ground (a footstep is AT the ground) and the
        /// last half metre at each end is ignored, so the body making the
        /// sound, or the one hearing it, never hides it from itself.
        /// </summary>
        public static bool Occluded(Vector3 ear, Vector3 at, Collider[] colliders, TerrainField terrain)
        {
            Vector3 from = ear, to = at + at.Normalized();
            Vector3 d = to - from;
            float len = d.Length();
            if (len < 1.5f) return false;
            Vector3 dir = d / len;
            if (ViewModel.Blocked(from + dir * 0.5f, dir, len - 1f, colliders, null) < len - 1f) return true;
            if (terrain == null) return false;
            // ponytail: a sample every ~2 m catches hills and the horizon; a
            // ridge thinner than that can leak, which nobody will hear.
            int n = Math.Max(2, (int)(len / 2f));
            for (int i = 1; i < n; i++)
            {
                Vector3 q = from + d * ((float)i / n);
                if (terrain.SampleRadius(Frame.ToSim(q.Normalized())) > q.Length()) return true;
            }
            return false;
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

        /// <summary>
        /// A boot on dust: mostly a low, soft thump (a falling sine around
        /// 80 Hz) with a little dull grit on top. The first step sound was
        /// pure low-passed noise at 500 Hz -- a hiss, and the playtest's
        /// most-hated sound twice over.
        /// </summary>
        private static AudioStreamWav Step(Random rng, double pitch)
        {
            int n = (int)(Rate * 0.14);
            var x = new float[n];
            double lp = 0, lp2 = 0, a = Math.Exp(-2 * Math.PI * 700 * pitch / Rate), ph = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                lp = a * lp + (1 - a) * (rng.NextDouble() * 2 - 1);
                lp2 = a * lp2 + (1 - a) * lp;                              // two poles: grit, not hiss
                ph += 2 * Math.PI * 80 * pitch * (1.0 + 0.6 * Math.Exp(-t * 60)) / Rate;
                double attack = Math.Min(1.0, t / 0.006);
                double thump = Math.Sin(ph) * Math.Exp(-t / 0.035);
                double grit = lp2 * 6.0 * Math.Exp(-t / 0.018);
                x[i] = (float)(attack * (0.8 * thump + 0.35 * grit));
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
