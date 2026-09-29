// Headless runner for the Sim assembly.
//
// Two modes, both runnable with no Unity Editor anywhere -- which is the whole
// point: C40 (this sim matching Go within 1e-10 m) and C44 (Sim and Net build
// and test in CI) both have to work on a machine with no Editor installed.
//
//   --selftest                        assert-style checks, exits non-zero
//   --dump <script.jsonl> --world <world.json>
//                                     replays the C5 conformance script and
//                                     writes {tick,pos,vel,grounded} JSONL to
//                                     stdout, in the Go dump's exact shape
//
// No test framework, no NuGet: same shape as the test/*.mjs harnesses.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using SpaceAdventure.Game;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;

internal static class Program
{
    private static int _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? "  " + detail : "")}");
        if (!ok) _failed++;
    }

    private static int Main(string[] args)
    {
        int dumpAt = Array.IndexOf(args, "--dump");
        if (dumpAt >= 0) return Dump(Arg(args, "--dump"), Arg(args, "--world"));
        if (Array.IndexOf(args, "--selftest") >= 0) return SelfTest();
        if (Array.IndexOf(args, "--drive") >= 0) return DriveDump(Arg(args, "--drive"), Arg(args, "--world"));
        if (Array.IndexOf(args, "--flight") >= 0) return FlightDump(Arg(args, "--flight"), Arg(args, "--world"));
        if (Array.IndexOf(args, "--pilot") >= 0)
            return Pilot(Arg(args, "--pilot"), uint.Parse(Arg(args, "--ship")),
                         Arg(args, "--token"), Arg(args, "--evidence"));
        if (Array.IndexOf(args, "--codec-decode") >= 0) return CodecDecode(Arg(args, "--codec-decode"));
        if (Array.IndexOf(args, "--codec-encode") >= 0) return CodecEncode(Arg(args, "--codec-encode"));
        if (Array.IndexOf(args, "--join") >= 0) return Join(Arg(args, "--join"));
        if (Array.IndexOf(args, "--predict") >= 0)
            return Predict(Arg(args, "--predict"), Arg(args, "--evidence"),
                           Array.IndexOf(args, "--server-origin") >= 0
                               ? Arg(args, "--server-origin") : null);
        if (Array.IndexOf(args, "--authority") >= 0)
            return Authority(Arg(args, "--authority"), Arg(args, "--evidence"));
        int colAt = Array.IndexOf(args, "--collide");
        if (colAt >= 0 && colAt + 2 < args.Length) return Collide(args[colAt + 1], args[colAt + 2]);

        Console.Error.WriteLine("usage: SimDump --selftest");
        Console.Error.WriteLine("       SimDump --dump <script.jsonl> --world <world.json>");
        Console.Error.WriteLine("       SimDump --drive <script> --world <world>  C30 drive dump");
        Console.Error.WriteLine("       SimDump --flight <script> --world <world> C34 flight dump");
        Console.Error.WriteLine("       SimDump --pilot <ws> --ship <id> --token <t> --evidence <out>  C39");
        Console.Error.WriteLine("       SimDump --codec-decode <go.hex>   decode Go's S->C frames");
        Console.Error.WriteLine("       SimDump --codec-encode <out.hex>  write C->S frames for Go");
        Console.Error.WriteLine("       SimDump --join <ws-url>           join a live server, report what arrives");
        Console.Error.WriteLine("       SimDump --collide <in.json> <out.json>  run the collider scenarios");
        Console.Error.WriteLine("       SimDump --authority <ws-url> --evidence <out.json>  C3 server authority");
        Console.Error.WriteLine("       SimDump --predict <ws-url> --evidence <out.json>    C6 prediction quality");
        return 2;
    }

    private static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length) throw new ArgumentException($"missing {name} <value>");
        return args[i + 1];
    }

    private static Vec3 ReadVec(JsonElement e) =>
        new Vec3(e[0].GetDouble(), e[1].GetDouble(), e[2].GetDouble());

    private static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Replays the C5 script. Mirrors server/cmd/server/dump.go line for line,
    /// including that the FIRST step's prevLook is the script state's facing --
    /// which is what the server does on join.
    /// </summary>
    private static int Dump(string scriptPath, string worldPath)
    {
        // The field MUST be the quantised one a client receives, not the f64
        // one the generator produces. The Go dump round-trips through the wire
        // encoding for exactly this reason; skipping it here would make the
        // diff measure the representation gap on top of any real divergence.
        using var wdoc = JsonDocument.Parse(File.ReadAllBytes(worldPath));
        var w = wdoc.RootElement;
        var codesEl = w.GetProperty("radii");
        var codes = new ushort[codesEl.GetArrayLength()];
        for (int i = 0; i < codes.Length; i++) codes[i] = (ushort)codesEl[i].GetUInt32();
        var field = TerrainField.FromWire(codes,
            w.GetProperty("radius_min").GetDouble(),
            w.GetProperty("radius_max").GetDouble());

        var state = Step.SpawnState(field);
        Vec3 prevLook = Vec3.Zero;
        bool havePrevLook = false;
        int tick = 0;
        var outBuf = new StringBuilder();

        foreach (string line in File.ReadLines(scriptPath))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            using var doc = JsonDocument.Parse(t);
            var root = doc.RootElement;

            if (root.TryGetProperty("state", out var st))
            {
                state = new State
                {
                    Pos = ReadVec(st.GetProperty("pos")),
                    Vel = ReadVec(st.GetProperty("vel")),
                    Grounded = st.GetProperty("grounded").GetBoolean(),
                    Facing = ReadVec(st.GetProperty("facing")),
                };
                continue;
            }
            if (!root.TryGetProperty("input", out var ie)) continue;

            var inp = new Input
            {
                MoveX = ie.GetProperty("move_x").GetDouble(),
                MoveY = ie.GetProperty("move_y").GetDouble(),
                LookDir = ReadVec(ie.GetProperty("look")),
                ActionMask = ie.GetProperty("action_mask").GetInt32(),
                SprintMult = ie.TryGetProperty("sprint_mult", out var sm) ? sm.GetDouble() : 0,
            };
            if (!havePrevLook) { prevLook = state.Facing; havePrevLook = true; }
            prevLook = Step.Apply(ref state, inp, prevLook, field, Rules.DT);

            outBuf.Append("{\"tick\":").Append(tick)
                  .Append(",\"pos\":[").Append(F(state.Pos.X)).Append(',').Append(F(state.Pos.Y)).Append(',').Append(F(state.Pos.Z))
                  .Append("],\"vel\":[").Append(F(state.Vel.X)).Append(',').Append(F(state.Vel.Y)).Append(',').Append(F(state.Vel.Z))
                  .Append("],\"grounded\":").Append(state.Grounded ? "true" : "false")
                  .Append("}\n");
            tick++;
        }
        Console.Out.Write(outBuf.ToString());
        Console.Error.WriteLine($"simdump: {tick} ticks");
        return 0;
    }

    private static int SelfTest()
    {
        var a = new Vec3(1, 2, 3);
        var b = new Vec3(-4, 5, 6);

        Check("dot matches the hand-computed value", Vec3.Dot(a, b) == 1 * -4 + 2 * 5 + 3 * 6);
        Check("cross is perpendicular to both inputs",
            Vec3.Dot(Vec3.Cross(a, b), a) == 0 && Vec3.Dot(Vec3.Cross(a, b), b) == 0);
        Check("cross anticommutes", Vec3.Cross(a, b).Equals(-Vec3.Cross(b, a)));

        // The 1e-10 conformance bar is the reason Vec3 is double, so prove the
        // arithmetic actually is. 1e-9 is far below f32 epsilon (~1.19e-7), so
        // in f32 this difference is exactly 0 and the check fails hard. It is
        // deliberately NOT an equality: 1 + 1e-9 is not representable even in
        // f64, so the difference is 1.0000000827e-9 and testing for exactly
        // 1e-9 would fail on correct arithmetic.
        double delta = (new Vec3(1, 0, 0) + new Vec3(1e-9, 0, 0)).X - 1.0;
        Check("f64 precision, not f32", delta > 0.9e-9 && delta < 1.1e-9, $"got {F(delta)}");

        Check("normalize returns a unit vector", Math.Abs(new Vec3(3, 0, 0).Normalized().Length - 1) < 1e-15);
        Check("normalize of zero is zero, not NaN", Vec3.Zero.Normalized().Equals(Vec3.Zero));
        Check("rejection removes the normal component",
            Math.Abs(Vec3.Dot(new Vec3(2, 7, -1).RejectFrom(new Vec3(0, 1, 0)), new Vec3(0, 1, 0))) < 1e-15);

        // Go's math.Hypot scales by the larger magnitude instead of squaring
        // both, so it survives inputs where x*x would overflow to infinity.
        Check("hypot matches Go on the overflow case",
            !double.IsInfinity(Step.Hypot(1e200, 1e200)), $"got {F(Step.Hypot(1e200, 1e200))}");
        Check("hypot is exact on a 3-4-5 triangle", Step.Hypot(3, 4) == 5);

        // Phase 11: the curve is RuneScape's, pinned at the same landmarks
        // server/internal/skills/curve_test.go pins (C79).
        Check("curve: level 2 at 83", SkillCurve.PointsForLevel(2) == 83);
        Check("curve: level 50 at 101,333", SkillCurve.PointsForLevel(50) == 101333);
        Check("curve: level 99 at 13,034,431", SkillCurve.PointsForLevel(99) == 13034431);
        Check("curve: 92 is half of 99",
            Math.Abs(SkillCurve.PointsForLevel(92) * 2 - SkillCurve.PointsForLevel(99)) < 100,
            $"92→{SkillCurve.PointsForLevel(92)}");
        Check("curve: LevelForXP inverts", SkillCurve.LevelForXP(82) == 1 && SkillCurve.LevelForXP(83) == 2
            && SkillCurve.LevelForXP(13034431) == 99 && SkillCurve.LevelForXP(101332) == 49);

        // FaceOf/DirOf must invert each other, or seam crossings drift.
        foreach (var d in new[] { new Vec3(1, 0.3, -0.2), new Vec3(-0.1, 1, 0.4), new Vec3(0.2, -0.3, -1) })
        {
            Vec3 u = d.Normalized();
            TerrainField.FaceOf(u, out int face, out double fu, out double fv);
            Vec3 back = TerrainField.DirOf(face, fu, fv);
            Check($"FaceOf/DirOf round-trips {u}", (back - u).Length < 1e-15);
        }

        // The rng is decoration-only, but "decoration" still has to agree
        // between clients or two players see different worlds. Golden values
        // come from the TypeScript generator, captured into
        // test/out/rng-ts-golden.json -- comparing the port against itself
        // would prove nothing.
        double[] wantMul = { 0.1844118325971067, 0.18998925131745636, 0.8104719922412187,
                             0.6437488221563399, 0.430774615611881 };
        var next = Rng.Mulberry32(1337);
        bool mulOK = true;
        for (int i = 0; i < wantMul.Length; i++) if (next() != wantMul[i]) mulOK = false;
        Check("mulberry32 matches the TypeScript generator", mulOK);

        Check("hash3 matches the TypeScript generator",
            Rng.Hash3(1, 2, 3) == 0.5442115059122443 &&
            Rng.Hash3(-7, 11, 0) == 0.8620048023294657 &&
            Rng.Hash3(65535, 65535, 65535) == 0.009098467649891973);

        FrameConventionChecks();
        PredictionChecks();
        RockScatterChecks();
        TimelineChecks();
        RoverPredictionChecks();
        BearingChecks();

        Console.WriteLine(_failed == 0 ? "\nOVERALL: PASS" : $"\nOVERALL: FAIL ({_failed})");
        return _failed == 0 ? 0 : 1;
    }

    // ---- frame conventions -------------------------------------------------
    //
    // The Godot client's Frame.cs treats sim space as engine space (the
    // identity), and that is only correct while these two facts hold. Both
    // live in Sim, both are invisible from the engine side, and getting either wrong
    // shows up as inverted strafe or bodies facing backwards -- neither of
    // which any conformance diff can see, because the server agrees with the
    // sim by construction.

    private static void FrameConventionChecks()
    {
        var up = new Vec3(0, 1, 0);
        var facing = new Vec3(0, 0, -1); // sim forward is -Z: the sim is right-handed

        // Movement right is facing x up. Step.cs says the opposite order
        // inverted A and D for the whole of M1 in both sims at once.
        Vec3 right = Vec3.Cross(facing, up);
        Check("sim right is +X for -Z facing",
            Math.Abs(right.X - 1) < 1e-12 && Math.Abs(right.Y) < 1e-12 && Math.Abs(right.Z) < 1e-12,
            right.ToString());

        // The orientation quaternion's Z axis is the facing. FacingOf in the
        // Godot client (Frame.OrientationBasis) builds a body's frame from exactly this.
        var state = new State { Pos = up * 150.0, Vel = Vec3.Zero, Facing = facing, Grounded = true };
        Vec3 back = Quat.Rotate(Step.OrientationQuat(state), new Vec3(0, 0, 1));
        Check("the orientation quat's Z axis is the facing",
            (back - facing).Length < 1e-12, $"{back} vs {facing}");
    }

    // ---- U11 prediction ----------------------------------------------------

    private static void PredictionChecks()
    {
        // u16 seq comparison must be wrap-safe. Plain `>` makes the client
        // ignore every snapshot for the rest of the session the first time the
        // counter passes 65535 -- roughly 55 minutes in at 20 Hz, which is
        // exactly the kind of bug that never shows up in a five-minute test.
        Check("seq 1 is newer than 0", Predictor.SeqNewer(1, 0));
        Check("seq 0 is not newer than 1", !Predictor.SeqNewer(0, 1));
        Check("seq is not newer than itself", !Predictor.SeqNewer(7, 7));
        Check("seq 0 is newer than 65535 (wrap)", Predictor.SeqNewer(0, 65535));
        Check("seq 65535 is not newer than 0 (wrap)", !Predictor.SeqNewer(65535, 0));

        var terrain = TerrainField.FromWire(FlatCodes(65), 150, 150);
        var p = new Predictor();
        p.Seed(terrain, System.Array.Empty<SpaceAdventure.Sim.Collider>());
        Vec3 spawn = p.State.Pos;

        // Walk forward for five ticks; the body must actually move and every
        // input must still be pending, because nothing has been acked.
        var fwd = Step.Tangential(new Vec3(1, 0, 0), spawn.Normalized()).Normalized();
        for (ushort seq = 1; seq <= 5; seq++)
        {
            p.Apply(seq, new Input { MoveX = 0, MoveY = 1, LookDir = fwd, ActionMask = 0 });
        }
        Check("prediction moves the body", (p.State.Pos - spawn).Length > 0.1);
        Check("unacked inputs are all pending", p.PendingCount == 5, $"{p.PendingCount}");

        // Reconciling at ack 3 drops 1-3 and replays 4-5 on the server state.
        // Feeding back the state prediction itself produced must leave the
        // body where it already was: replay is deterministic, so a correction
        // of zero is the proof that client and server run the same rules.
        var predicted = p.State;
        Predictor twin = Replay(terrain, fwd);
        twin.Reconcile(SpawnAfter(terrain, fwd, 3), VelAfter(terrain, fwd, 3),
                       FacingAfter(terrain, fwd, 3), true, 3);
        Check("reconcile keeps the two unacked inputs", twin.PendingCount == 2, $"{twin.PendingCount}");
        Check("replaying the server's own answer moves nothing",
              (twin.State.Pos - predicted.Pos).Length < 1e-9,
              $"{(twin.State.Pos - predicted.Pos).Length:E2} m");

        // A stale snapshot must be ignored outright, never blended in.
        Check("a stale ack is rejected", !twin.Reconcile(spawn, Vec3.Zero, fwd, true, 2));
    }

    // ---- rock scatter ------------------------------------------------------
    //
    // The scatter is never sent over the wire. The server ships `world_seed`
    // and every client scatters its own rocks from it, so the ONLY thing
    // keeping two clients showing the same planet is that this function is a
    // pure, stable function of (terrain, seed). Nothing checks it at runtime:
    // rocks have no collision, so two clients disagreeing about where four
    // hundred of them sit is completely silent.
    //
    // Which makes the rng draw order load-bearing. Adding one rng() call, or
    // moving one, shifts every rock after it.

    // ---- U13 render clock --------------------------------------------------
    //
    // The contract Timeline.cs owes the server: remotes are drawn at
    // `serverClock - interp_delay`, on a clock estimated from snapshot
    // arrivals plus the one-way trip. interp_delay is 0.1 s = 2 ticks at
    // 20 Hz, so with snapshots for ticks 10..12 buffered and the newest
    // freshly arrived, the render point sits exactly on tick 10 — and it
    // moves with the caller's clock, not with packet arrivals. This is the
    // C#-path check U13 owed since the C14 lag-comp fix; t21 exercises the
    // same contract at the wire level through the .mjs harness.

    private static Snapshot TimelineSnap(uint tick, params (uint Id, float X)[] ents)
    {
        var rows = new EntityRow[ents.Length];
        for (int i = 0; i < ents.Length; i++)
            rows[i] = new EntityRow { Id = ents[i].Id, PosX = ents[i].X, QuatW = 1f, Health = 100 };
        return new Snapshot { Tick = tick, Entities = rows };
    }

    private static double TimelineX(IEnumerable<KeyValuePair<uint, Pose>> poses, uint id)
    {
        foreach (var kv in poses)
            if (kv.Key == id) return kv.Value.Pos.X;
        return double.NaN;
    }

    // ---- compass bearings (C63) --------------------------------------------

    private static void BearingChecks()
    {
        // Standing at (0, 150, 0): up = +Y, facing +Z. In THIS game's
        // frame, right = Cross(facing, up) = −X (Step.cs's own convention,
        // sign-bug comment and all) — so −X is 90° RIGHT (positive), +X is
        // left, behind is ±π.
        var pos = new Vec3(0, 150, 0);
        var fwd = new Vec3(0, 0, 1);
        double ahead = Bearing.To(pos, fwd, new Vec3(0, 150, 10));
        double right = Bearing.To(pos, fwd, new Vec3(-10, 150, 0));
        double left = Bearing.To(pos, fwd, new Vec3(10, 150, 0));
        double behind = Bearing.To(pos, fwd, new Vec3(0, 150, -10));
        Check("bearing dead ahead is zero", Math.Abs(ahead) < 1e-12, F(ahead));
        Check("bearing to the right is +90°", Math.Abs(right - Math.PI / 2) < 1e-12, F(right));
        Check("bearing to the left is −90°", Math.Abs(left + Math.PI / 2) < 1e-12, F(left));
        Check("bearing behind is ±180°", Math.Abs(Math.Abs(behind) - Math.PI) < 1e-12, F(behind));
        // The radial component is ignored: a target 40 m overhead at the
        // same tangent point still reads dead ahead.
        double lofted = Bearing.To(pos, fwd, new Vec3(0, 190, 10));
        Check("bearing ignores altitude", Math.Abs(lofted) < 1e-12, F(lofted));
        // Degenerate → NaN, never a lie.
        Check("bearing overhead is NaN", double.IsNaN(Bearing.To(pos, fwd, new Vec3(0, 190, 0))));
    }

    // ---- rover prediction --------------------------------------------------
    //
    // Snap-then-replay must be a no-op when nothing new happened: predicting
    // N inputs and then reconciling against the state the server reached
    // after K < N of them must land exactly where continuous prediction did,
    // because the replay runs the same arithmetic over the same inputs.

    private static void RoverPredictionChecks()
    {
        // A uniform 150 m sphere: min == max makes every u16 code decode to
        // the same radius, so terrain contributes nothing but the contact.
        var field = TerrainField.FromWire(
            new ushort[6 * TerrainField.FaceGrid * TerrainField.FaceGrid], 150, 150);

        var start = new RoverState
        {
            Pos = new Vec3(0, 150, 0),
            Vel = Vec3.Zero,
            Quat = Quat.FromBasis(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
            Grounded = true,
        };

        var a = new RoverPredictor();
        a.Seed(field);
        a.Reconcile(start.Pos, start.Vel, start.Quat, true, 0);
        for (ushort seq = 1; seq <= 10; seq++) a.Apply(seq, 1, 0.3);
        RoverState continuous = a.State;

        // The server's own view after the first 4 inputs.
        var server = start;
        for (int i = 0; i < 4; i++) Drive.Apply(ref server, 1, 0.3, field, Rules.DT);

        var b = new RoverPredictor();
        b.Seed(field);
        b.Reconcile(start.Pos, start.Vel, start.Quat, true, 0);
        for (ushort seq = 1; seq <= 10; seq++) b.Apply(seq, 1, 0.3);
        b.Reconcile(server.Pos, server.Vel, server.Quat, server.Grounded, 4);

        Check("rover reconcile+replay matches continuous prediction exactly",
            (b.State.Pos - continuous.Pos).Length == 0 &&
            (b.State.Vel - continuous.Vel).Length == 0,
            $"dPos {F((b.State.Pos - continuous.Pos).Length)}");
        Check("rover predictor moved at all", (continuous.Pos - start.Pos).Length > 0.1);
        Check("a stale ack is ignored",
            !b.Reconcile(start.Pos, start.Vel, start.Quat, true, 2));
    }

    private static void TimelineChecks()
    {
        var tl = new SnapshotTimeline { OneWaySeconds = 0 };
        tl.Add(TimelineSnap(10, (1u, 10f)), 0.00f);
        tl.Add(TimelineSnap(11, (1u, 11f), (2u, 5f)), 0.05f);
        tl.Add(TimelineSnap(12, (1u, 12f), (2u, 6f)), 0.10f);

        Check("timeline renders exactly interp_delay behind the estimated server clock",
            Math.Abs(TimelineX(tl.Interpolate(0.10f), 1) - 10.0) < 1e-6);
        Check("timeline interpolates between the bracketing snapshots",
            Math.Abs(TimelineX(tl.Interpolate(0.125f), 1) - 10.5) < 1e-6);
        Check("an entity that appeared mid-bracket takes the newer pose",
            Math.Abs(TimelineX(tl.Interpolate(0.125f), 2) - 5.0) < 1e-6);

        // The one-way trip pushes the estimated server clock forward: at the
        // same wall instant, a 100 ms one-way (200 ms RTT) puts the render
        // point ON the newest snapshot, and anything worse clamps there
        // (Timeline.cs documents the clamp-not-extrapolate choice).
        tl.OneWaySeconds = 0.1;
        Check("one-way delay advances the render point to the newest snapshot",
            Math.Abs(TimelineX(tl.Interpolate(0.10f), 1) - 12.0) < 1e-6);
        tl.OneWaySeconds = 0.5;
        Check("past the newest snapshot the timeline clamps, never extrapolates",
            Math.Abs(TimelineX(tl.Interpolate(0.10f), 1) - 12.0) < 1e-6);
    }

    private static void RockScatterChecks()
    {
        var terrain = TerrainField.FromWire(FlatCodes(65), 150, 150);

        var a = RockScatter.Scatter(terrain, 12345);
        var b = RockScatter.Scatter(terrain, 12345);
        var c = RockScatter.Scatter(terrain, 12346);

        Check("scatter fills its target on open ground",
              a.Count == RockScatter.TargetCount, $"got {a.Count}");

        bool same = a.Count == b.Count;
        for (int i = 0; same && i < a.Count; i++)
            same = a[i].Pos.Equals(b[i].Pos) && a[i].Variant == b[i].Variant
                   && a[i].Spin == b[i].Spin;
        Check("same seed gives byte-identical placements", same);

        bool differs = false;
        for (int i = 0; i < Math.Min(a.Count, c.Count) && !differs; i++)
            differs = !a[i].Pos.Equals(c[i].Pos);
        Check("a different seed gives a different scatter", differs);

        // Every rock seated on the surface, upright along its own radius, and
        // within the size band the GDD gives (0.3-1.5 m, times the per-axis
        // jitter, so 0.165 at the smallest and 1.8 at the largest).
        bool seated = true, sized = true, radial = true;
        var used = new bool[3];
        foreach (var p in a)
        {
            used[p.Variant] = true;
            double r = p.Pos.Length;
            if (r < 150.0 || r > 150.0 + 1.5 * 0.05 + 1e-9) seated = false;
            if (p.Scale.X < 0.16 || p.Scale.X > 1.81
                || p.Scale.Y < 0.16 || p.Scale.Y > 1.81) sized = false;
            if (Math.Abs(p.Dir.Length - 1) > 1e-12) radial = false;
        }
        Check("every rock sits on the surface, not in or above it", seated);
        Check("every rock is within the GDD size band", sized);
        Check("every rock direction is a unit vector", radial);
        Check("all three variants are used", used[0] && used[1] && used[2]);

        // The slope rule is the one rule with a number in it from the GDD, and
        // it has to be checked against terrain that actually HAS slopes -- on
        // the flat fixture above the assertion holds whether the filter exists
        // or not.
        var rough = TerrainField.FromWire(BumpyCodes(65), 124, 190);
        var onRough = RockScatter.Scatter(rough, 99);

        bool steepExists = false;
        for (int i = 0; i < 2000 && !steepExists; i++)
        {
            double z = (i % 41) / 20.0 - 1.0, th = i * 0.37;
            double rr = Math.Sqrt(Math.Max(0, 1 - z * z));
            var d = new Vec3(rr * Math.Cos(th), z, rr * Math.Sin(th)).Normalized();
            if (rough.Slope(d) > RockScatter.SlopeMax) steepExists = true;
        }
        Check("the rough fixture really does have unwalkable ground", steepExists);

        bool slopeOk = true;
        foreach (var p in onRough)
            if (rough.Slope(p.Dir) > RockScatter.SlopeMax) slopeOk = false;
        Check("no rock on a slope above 35 degrees", slopeOk,
              $"{onRough.Count} placed on rough ground");
    }

    /// <summary>A fresh predictor that has applied the same five inputs.</summary>
    private static Predictor Replay(TerrainField t, Vec3 fwd)
    {
        var p = new Predictor();
        p.Seed(t, System.Array.Empty<SpaceAdventure.Sim.Collider>());
        for (ushort seq = 1; seq <= 5; seq++)
        {
            p.Apply(seq, new Input { MoveX = 0, MoveY = 1, LookDir = fwd, ActionMask = 0 });
        }
        return p;
    }

    /// <summary>The state the SERVER would hold having applied n inputs.</summary>
    private static State ServerAfter(TerrainField t, Vec3 fwd, int n)
    {
        var s = Step.SpawnState(t);
        Vec3 look = s.Facing;
        for (int i = 0; i < n; i++)
        {
            look = Step.Apply(ref s, new Input { MoveX = 0, MoveY = 1, LookDir = fwd, ActionMask = 0 },
                              look, t, Rules.DT);
        }
        return s;
    }

    private static Vec3 SpawnAfter(TerrainField t, Vec3 fwd, int n) => ServerAfter(t, fwd, n).Pos;
    private static Vec3 VelAfter(TerrainField t, Vec3 fwd, int n) => ServerAfter(t, fwd, n).Vel;
    private static Vec3 FacingAfter(TerrainField t, Vec3 fwd, int n) => ServerAfter(t, fwd, n).Facing;

    /// <summary>A perfectly spherical radius field, for tests about motion.</summary>
    private static ushort[] FlatCodes(int faceGrid)
    {
        var codes = new ushort[6 * faceGrid * faceGrid];
        for (int i = 0; i < codes.Length; i++) codes[i] = 0; // min == max == 150
        return codes;
    }

    /// <summary>
    /// A field with real slopes in it, for checks that a flat ball makes
    /// vacuous.
    ///
    /// On FlatCodes every slope is 0 and every curvature is 0, so "no rock on
    /// a slope above 35 degrees" passes whether or not the filter is there --
    /// which makes it not a check. This corrugates each face hard enough that
    /// a good part of the surface is unwalkable, so deleting the filter turns
    /// the assertion red.
    /// </summary>
    private static ushort[] BumpyCodes(int faceGrid)
    {
        var codes = new ushort[6 * faceGrid * faceGrid];
        for (int f = 0; f < 6; f++)
        {
            for (int j = 0; j < faceGrid; j++)
            {
                for (int i = 0; i < faceGrid; i++)
                {
                    double w = Math.Sin(i * 0.9 + f) * Math.Cos(j * 0.9 - f);
                    double t = 0.5 + 0.5 * w;               // 0..1
                    codes[(f * faceGrid + j) * faceGrid + i] =
                        (ushort)Math.Round(t * ushort.MaxValue);
                }
            }
        }
        return codes;
    }

    // ---- C6 prediction quality ------------------------------------------------
    //
    // The client's prediction must stay within 0.25 m of the server's at 100 ms
    // injected latency, and must never be yanked BACKWARD along the player's
    // own track. Ported from the TypeScript harness with the metric and every
    // one of its guards intact -- those guards are the hard-won part.
    //
    // THE GATE IS SAME-INSTANT PAIRING. The obvious metric, |P_M - snapshot(ack
    // M)|, pairs two different instants: P_M is the client state when input M
    // was sent, the snapshot is the server ~RTT later. Under sustained sprint
    // that gap is exactly one tick of motion (7.5 m/s x 0.05 s = 0.375 m),
    // which is why the old metric read a flat 0.375 m no matter what the client
    // did. The same-wall-time metric it reached for is unachievable by ANY
    // client: the server's state is a 20 Hz step function lagging continuous
    // motion by U(0, 50 ms), so p95 = 0.356 m > 0.25 for a perfect client. A
    // criterion nothing can pass is not a gate.
    //
    // So: the client's POST-reconcile state at ack M is its belief about "now",
    // and the server's belief about that same "now" is in the NEXT snapshot,
    // ack M+1. That difference is real prediction error, and it separates exact
    // replay (clears to wire precision) from blending (leaves a residual).

    private const double GateP95M = 0.25;
    private const double SnapbackM = -0.15;
    private const double TickDt = 0.05;
    private const int IdleTicks = 40;
    private const int LegSprint = 100, LegReversal = 80, LegSlope = 100, LegCoast = 40;
    private const int TotalTicks = IdleTicks + LegSprint + LegReversal + LegSlope + LegCoast;
    private const double JumpLeadM = 5.5;
    private const double Deg = Math.PI / 180.0;

    private sealed class Route
    {
        public double Az, R0, FlatLen, SlopeFrom, SlopeTo;
        public double SlopeLen => SlopeTo - SlopeFrom;
    }

    private sealed class Script
    {
        public Input[] Inputs;
        public int JumpTick;
        public double MaxSprint, MinReversalTrackVel;
        public int AirTicks;
        public double LandingS, LandingSlopeDeg;
        public Route Route;
    }

    private static Vec3 TrackDirAt(double azRad, double s, double r0)
    {
        double th = s / r0;
        return new Vec3(Math.Sin(th) * Math.Sin(azRad), Math.Cos(th), Math.Sin(th) * Math.Cos(azRad));
    }

    private static Vec3 TrackTangent(double azRad, double s, double r0)
    {
        double th = s / r0;
        return new Vec3(Math.Cos(th) * Math.Sin(azRad), -Math.Sin(th), Math.Cos(th) * Math.Cos(azRad))
            .Normalized();
    }

    private static double ArcOf(Vec3 pos, double azRad, double r0)
    {
        var t0 = new Vec3(Math.Sin(azRad), 0, Math.Cos(azRad));
        Vec3 d = pos.Normalized();
        return Math.Atan2(Vec3.Dot(d, t0), Vec3.Dot(d, new Vec3(0, 1, 0))) * r0;
    }

    /// <summary>
    /// Great-circle routes out of spawn: a flat run, then a sustained walkable
    /// slope to jump onto. Nothing steeper than max_slope and no step the body
    /// cannot climb anywhere along it.
    /// </summary>
    private static System.Collections.Generic.List<Route> FindRoutes(TerrainField t)
    {
        double r0 = t.SampleRadius(new Vec3(0, 1, 0));
        var found = new System.Collections.Generic.List<Route>();

        for (double az = 0; az < 360; az += 2.5)
        {
            double azRad = az * Deg;
            var sS = new System.Collections.Generic.List<double>();
            var sR = new System.Collections.Generic.List<double>();
            var sSlope = new System.Collections.Generic.List<double>();
            for (double s = 0; s <= 90.001; s += 0.5)
            {
                Vec3 d = TrackDirAt(azRad, s, r0);
                sS.Add(s);
                sR.Add(t.SampleRadius(d));
                sSlope.Add(t.Slope(d) / Deg);
            }

            double flatEnd = 80;
            for (int i = 0; i < sS.Count; i++)
                if (sSlope[i] >= 8) { flatEnd = sS[i]; break; }

            double runStart = -1, runEnd = -1;
            for (int i = 0; i < sS.Count; i++)
            {
                if (sS[i] < flatEnd) continue;
                bool inSlope = sSlope[i] >= 15 && sSlope[i] <= 45;
                if (inSlope)
                {
                    if (runStart < 0) runStart = sS[i];
                    runEnd = sS[i];
                }
                else if (sS[i] > flatEnd + 1.0) break;
            }

            bool ok = runStart >= 0 && runEnd - runStart >= 8 && runEnd - runStart <= 25;
            if (ok)
            {
                for (int i = 0; i < sS.Count && sS[i] <= runEnd; i++)
                    if (sSlope[i] >= 50) ok = false;
                for (int i = 1; ok && i < sS.Count && sS[i] <= runEnd; i++)
                    if (Math.Abs(sR[i] - sR[i - 1]) > 0.31) ok = false;
            }
            if (ok)
                found.Add(new Route { Az = az, R0 = r0, FlatLen = flatEnd, SlopeFrom = runStart, SlopeTo = runEnd });
        }

        found.Sort((a, b) => (b.FlatLen + b.SlopeLen).CompareTo(a.FlatLen + a.SlopeLen));
        return found;
    }

    /// <summary>
    /// Runs the scripted inputs through the real sim to place the jump and
    /// check the route actually produces the scenario. Two passes: the first
    /// maps arc length per tick, the second jumps and validates.
    /// </summary>
    private static Script BuildScript(TerrainField t, Route route)
    {
        double azRad = route.Az * Deg;

        double[] Pass(int jumpTick, out Input[] inputs, out double maxSprint,
                      out double minRev, out int airTicks, out int landTick,
                      out double landS, out double landSlope)
        {
            State st = Step.SpawnState(t);
            Vec3 lastLook = st.Facing;
            var sPer = new double[TotalTicks];
            inputs = new Input[TotalTicks];
            maxSprint = 0; minRev = double.PositiveInfinity; airTicks = 0;
            landTick = -1; landS = 0; landSlope = 0;
            bool airborne = false;

            for (int k = 0; k < TotalTicks; k++)
            {
                double s = ArcOf(st.Pos, azRad, route.R0);
                sPer[k] = s;

                double moveY = 0;
                int mask = 0;
                int sprintStart = IdleTicks;
                int revStart = IdleTicks + LegSprint;
                int slopeStart = revStart + LegReversal;
                int coastStart = slopeStart + LegSlope;
                if (k >= sprintStart && k < coastStart)
                {
                    mask = SpaceAdventure.Sim.Action.Sprint;
                    moveY = (k >= revStart && k < slopeStart) ? -1 : 1;
                }
                if (jumpTick >= 0 && (k == jumpTick || k == jumpTick + 1)) mask |= SpaceAdventure.Sim.Action.Jump;

                var inp = new Input
                {
                    MoveX = 0,
                    MoveY = moveY,
                    LookDir = TrackTangent(azRad, s, route.R0),
                    ActionMask = mask,
                };
                inputs[k] = inp;
                lastLook = Step.Apply(ref st, inp, lastLook, t, TickDt);

                double spd = st.Vel.Length;
                Vec3 T = TrackTangent(azRad, s, route.R0);
                double trackVel = Vec3.Dot(st.Vel, T);
                if (k >= sprintStart && k < revStart) maxSprint = Math.Max(maxSprint, spd);
                if (k >= revStart && k < slopeStart) minRev = Math.Min(minRev, trackVel);

                if (!st.Grounded) { airborne = true; airTicks++; }
                else if (airborne)
                {
                    airborne = false;
                    landTick = k;
                    landS = ArcOf(st.Pos, azRad, route.R0);
                    landSlope = t.Slope(st.Pos.Normalized()) / Deg;
                }
            }
            return sPer;
        }

        double[] s1 = Pass(-1, out _, out _, out _, out _, out _, out _, out _);

        int slopeLeg = IdleTicks + LegSprint + LegReversal;
        double target = route.FlatLen - JumpLeadM;
        int jump = -1;
        for (int k = slopeLeg; k < slopeLeg + LegSlope - 10; k++)
            if (s1[k] >= target) { jump = k; break; }
        if (jump < 0) return null;

        Pass(jump, out Input[] inputs2, out double maxSprint2, out double minRev2,
             out int air2, out int landTick2, out double landS2, out double landSlope2);

        bool flatOk = route.FlatLen >= 42 && route.SlopeLen >= 8;
        bool sprintOk = maxSprint2 >= 7.0 && maxSprint2 <= 7.9;
        bool revOk = minRev2 <= -6.0;
        bool jumpOk = landTick2 >= 0 && air2 >= 10 && landTick2 >= jump + 5
                      && landS2 >= route.FlatLen - 1.5 && landSlope2 >= 8 && landSlope2 < 50;
        if (!(flatOk && sprintOk && revOk && jumpOk)) return null;

        return new Script
        {
            Inputs = inputs2, JumpTick = jump, MaxSprint = maxSprint2,
            MinReversalTrackVel = minRev2, AirTicks = air2,
            LandingS = landS2, LandingSlopeDeg = landSlope2, Route = route,
        };
    }

    private sealed class Snap
    {
        public uint T;
        public int M = -1;
        public Vec3 Auth, Post, Trk;
        public bool HasAuth, HasTrk;

        /// <summary>
        /// Inputs still unacked AFTER this reconcile, i.e. how many ticks of
        /// replay stand between the server's state and the client's belief.
        /// This is what says which future snapshot describes the same instant.
        /// </summary>
        public int Ahead;
    }

    private static int Predict(string url, string evidencePath, string serverOrigin)
    {
        using var net = new NetClient();
        net.Connect(url, "qa-t6", $"t6-{Guid.NewGuid():N}");

        TerrainField terrain = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (terrain == null && DateTime.UtcNow < deadline)
        {
            while (net.Poll(out var f))
            {
                if (f.Type != Msg.Terrain) continue;
                var t = Decode.Terrain(f.Reader);
                terrain = TerrainField.FromWire(t.Radii, t.RadiusMin, t.RadiusMax);
            }
            System.Threading.Thread.Sleep(5);
        }
        if (terrain == null) { Console.Error.WriteLine("t6: no terrain"); return 1; }

        Script script = null;
        foreach (Route r in FindRoutes(terrain))
        {
            script = BuildScript(terrain, r);
            if (script != null) break;
        }
        if (script == null)
        {
            Console.Error.WriteLine("t6: no route produced the scenario");
            return 1;
        }
        Console.WriteLine(
            $"route az={script.Route.Az:F1} flat={script.Route.FlatLen:F1} m " +
            $"slope={script.Route.SlopeFrom:F1}..{script.Route.SlopeTo:F1} m jumpTick={script.JumpTick}");

        var predictor = new Predictor();
        predictor.Seed(terrain, Array.Empty<SpaceAdventure.Sim.Collider>());

        var snaps = new System.Collections.Generic.List<Snap>();
        ushort seq = 0;
        int k = 0;
        var start = DateTime.UtcNow;
        double Elapsed() => (DateTime.UtcNow - start).TotalMilliseconds;
        double nextTickAt = 0;

        while (k < TotalTicks || Elapsed() < TotalTicks * 50 + 1500)
        {
            while (net.Poll(out var f))
            {
                if (f.Type != Msg.Snapshot) continue;
                Snapshot snap = Decode.Snapshot(f.Reader);
                var rec = new Snap { T = snap.Tick, M = snap.AckSeq };
                foreach (EntityRow row in snap.Entities)
                {
                    if (row.Id != net.EntityId) continue;
                    rec.Auth = new Vec3(row.PosX, row.PosY, row.PosZ);
                    rec.HasAuth = true;

                    // The track the player actually has, evaluated BEFORE the
                    // reconcile. Tangential velocity when there is any; else
                    // the wish direction; else facing. Mid-jump the motion is
                    // mostly radial and a tangential track is ill-defined --
                    // counting snap-backs there produced an intermittent 1
                    // while the position error stayed at wire precision.
                    State st = predictor.State;
                    Vec3 up = st.Pos.Normalized();
                    Vec3 vt = st.Vel - up * Vec3.Dot(st.Vel, up);
                    if (vt.Length > 0.3) { rec.Trk = vt.Normalized(); rec.HasTrk = true; }

                    int pendBefore = predictor.PendingCount;
                    predictor.Reconcile(rec.Auth, new Vec3(row.VelX, row.VelY, row.VelZ),
                                        st.Facing, row.Grounded, snap.AckSeq);
                    rec.Post = predictor.State.Pos;
                    rec.Ahead = predictor.PendingCount;
                    if (Environment.GetEnvironmentVariable("T6_DEBUG") != null && snaps.Count < 12)
                        Console.WriteLine($"  dbg M={snap.AckSeq} seqSent={seq} pendBefore={pendBefore} ahead={rec.Ahead}");
                    break;
                }
                snaps.Add(rec);
            }

            if (k < TotalTicks && Elapsed() >= nextTickAt)
            {
                nextTickAt = Elapsed() + 50;
                Input inp = script.Inputs[k];
                seq++;
                predictor.Apply(seq, inp);
                net.Send(Encode.Input(0f, (float)inp.MoveY,
                                      (float)inp.LookDir.X, (float)inp.LookDir.Y, (float)inp.LookDir.Z,
                                      (ushort)inp.ActionMask, seq));
                k++;
            }
            System.Threading.Thread.Sleep(2);
        }

        // ---- the gate
        var errs = new System.Collections.Generic.List<double>();
        int snapbacks = 0;
        for (int i = 0; i < snaps.Count; i++)
        {
            Snap a = snaps[i];
            if (a.Post.Length == 0 || a.M <= 0) continue;

            // WHICH future snapshot describes the same instant.
            //
            // After reconciling ack M the client has snapped to the server's
            // state and replayed everything still unacked, so its belief is
            // about tick M + pending. The TypeScript harness wrote that as a
            // literal M+1, which was right for ITS in-flight depth: one input
            // outstanding. Through a 100 ms proxy this client carries two, and
            // pairing against M+1 measured the client one tick in the FUTURE
            // against the server -- p50 came out at 0.375 m, exactly
            // 7.5 m/s x 0.05 s, which is the cross-instant artifact this metric
            // exists to avoid, and 271 phantom snap-backs from the server
            // appearing to lag its own prediction.
            //
            // So pair by replay depth rather than by a constant. It reduces to
            // M+1 whenever one input is in flight, and stays the same-instant
            // comparison at any RTT.
            //
            // The tick must advance in step with the ack too: when the server
            // carries an ack across two ticks, auth is not `ahead` steps on
            // from post and the difference reads as whole ticks of motion
            // again.
            int ahead = a.Ahead;
            if (ahead <= 0) continue;
            Snap b = null;
            for (int j = i + 1; j < snaps.Count; j++)
            {
                if (snaps[j].M != a.M + ahead) continue;
                if (snaps[j].HasAuth && snaps[j].T == a.T + ahead) b = snaps[j];
                break;
            }
            if (b == null) continue;

            errs.Add((a.Post - b.Auth).Length);
            if (a.HasTrk && Vec3.Dot(b.Auth - a.Post, a.Trk) < SnapbackM) snapbacks++;
        }

        errs.Sort();
        double P(double q)
        {
            if (errs.Count == 0) return double.NaN;
            double idx = (errs.Count - 1) * q;
            int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            return errs[lo] + (errs[hi] - errs[lo]) * (idx - lo);
        }

        double p95 = P(0.95);
        _failed = 0;
        Check("c6 pairs enough snapshots to mean anything", errs.Count >= 50, $"n={errs.Count}");
        Check("c6 p95 prediction error under the budget", errs.Count > 0 && p95 < GateP95M,
              $"p95={p95:E3} m (budget {GateP95M} m), p50={P(0.5):E3}, max={(errs.Count > 0 ? errs[^1] : double.NaN):E3}");
        Check("c6 the server never pulls the player backward", snapbacks == 0, $"{snapbacks} snap-backs");
        // Without this the criterion can pass by not being under latency at
        // all: the whole point of C6 is prediction quality AT 100 ms, and a
        // proxy that failed to start would otherwise read as a clean run.
        Check("the run was actually under injected latency", net.RttMs >= 80,
              $"rtt={net.RttMs:F1} ms (expect ~100 through the proxy)");
        Check("scenario: sprint reached full speed", script.MaxSprint >= 7.0 && script.MaxSprint <= 7.9,
              $"{script.MaxSprint:F2} m/s");
        Check("scenario: the reversal actually reversed", script.MinReversalTrackVel <= -6.0,
              $"{script.MinReversalTrackVel:F2} m/s along track");
        Check("scenario: the jump landed on walkable slope", script.LandingSlopeDeg >= 8 && script.LandingSlopeDeg < 50,
              $"{script.LandingSlopeDeg:F1} deg after {script.AirTicks} air ticks");

        if (!string.IsNullOrEmpty(evidencePath))
        {
            var evidence = new
            {
                criterion = "C6 - prediction quality at 100 ms injected latency",
                harness = "SimDump --predict (C# Predictor, Game/Core/Prediction.cs)",
                url,
                server_build = ServerBuild(url, serverOrigin),
                pairs = errs.Count,
                p50 = P(0.5),
                p95,
                max = errs.Count > 0 ? errs[^1] : double.NaN,
                snapbacks,
                route_az = script.Route.Az,
                sprint_max = script.MaxSprint,
                reversal_min_track_vel = script.MinReversalTrackVel,
                air_ticks = script.AirTicks,
                landing_slope_deg = script.LandingSlopeDeg,
                verdict = _failed == 0 ? "PASS" : "FAIL",
            };
            System.IO.File.WriteAllText(evidencePath,
                Newtonsoft.Json.JsonConvert.SerializeObject(evidence, Newtonsoft.Json.Formatting.Indented) + "\n");
            Console.WriteLine($"evidence: {evidencePath}");
        }

        Console.WriteLine(_failed == 0 ? "OVERALL: PASS" : $"OVERALL: FAIL ({_failed})");
        return _failed == 0 ? 0 : 1;
    }

    // ---- C3 server authority -------------------------------------------------
    //
    // The server is authoritative: a state forced client-side is dragged back
    // to the server's within one tick.
    //
    // Method, unchanged from the TypeScript harness this replaces: join, walk
    // for 1.5 s so the body is somewhere non-trivial, then shove the PREDICTED
    // position 4 m sideways along the surface and watch what the next snapshot
    // does to it. The prediction is client-side, so forcing it grants nothing
    // and the assertion is exactly that -- the next reconcile puts it back.
    //
    // The unit under test is the predictor that ships. That used to be
    // the TypeScript client's predictor and is now Game/Core/Prediction.cs, which is
    // why this harness moved rather than being deleted with the browser
    // client.

    private const double CorruptDist = 4.0;      // m, the ?corrupt override's offset
    private const double SnapEps = 1e-3;         // m, C3 snap tolerance
    private const int WalkMs = 1500;
    private const int OneTickNetMs = 100;        // one 50 ms tick + network
    private const int WatchdogMs = 8000;

    private static int Authority(string url, string evidencePath)
    {
        using var net = new NetClient();
        net.Connect(url, "t3", $"t3-{Guid.NewGuid():N}");

        var predictor = new Predictor();
        TerrainField terrain = null;
        Vec3 look = default;
        ushort seq = 0;

        var start = DateTime.UtcNow;
        double Elapsed() => (DateTime.UtcNow - start).TotalMilliseconds;

        bool forced = false, corrected = false;
        double preDist = 0, snapDist = 0, correctedAtMs = 0, forcedAtMs = 0;
        uint forcedTick = 0, correctedTick = 0;
        Vec3 forcedDir = default;
        var nextInputAt = 0.0;

        while (Elapsed() < WatchdogMs && !corrected)
        {
            while (net.Poll(out var frame))
            {
                switch (frame.Type)
                {
                    case Msg.Terrain:
                    {
                        var t = Decode.Terrain(frame.Reader);
                        terrain = TerrainField.FromWire(t.Radii, t.RadiusMin, t.RadiusMax);
                        predictor.Seed(terrain, Array.Empty<SpaceAdventure.Sim.Collider>());
                        look = predictor.State.Facing;
                        Console.WriteLine($"terrain + predictor seed: spawn pos={V(predictor.State.Pos)}");
                        break;
                    }
                    case Msg.Colliders:
                        predictor.SetColliders(ToSim(Decode.Colliders(frame.Reader)));
                        break;
                    case Msg.Snapshot:
                    {
                        Snapshot snap = Decode.Snapshot(frame.Reader);
                        foreach (EntityRow row in snap.Entities)
                        {
                            if (row.Id != net.EntityId) continue;
                            var authPos = new Vec3(row.PosX, row.PosY, row.PosZ);

                            if (forced && !corrected)
                            {
                                // The reconcile that answers the corruption.
                                predictor.Reconcile(
                                    authPos, new Vec3(row.VelX, row.VelY, row.VelZ),
                                    look, row.Grounded, snap.AckSeq);
                                snapDist = (predictor.State.Pos - authPos).Length;
                                corrected = true;
                                correctedAtMs = Elapsed();
                                correctedTick = snap.Tick;
                                break;
                            }

                            predictor.Reconcile(
                                authPos, new Vec3(row.VelX, row.VelY, row.VelZ),
                                look, row.Grounded, snap.AckSeq);

                            // Corrupt once, after the walk, on a snapshot so
                            // the offset is measured against a fresh truth.
                            if (!forced && Elapsed() > WalkMs)
                            {
                                Vec3 up = predictor.State.Pos.Normalized();
                                // Any direction along the surface will do; the
                                // facing projected onto the tangent plane is
                                // the one that reads as "the player moved".
                                Vec3 tang = (look - up * Vec3.Dot(look, up)).Normalized();
                                predictor.ForceOffset(tang * CorruptDist);
                                preDist = (predictor.State.Pos - authPos).Length;
                                forced = true;
                                forcedAtMs = Elapsed();
                                forcedTick = snap.Tick;
                                forcedDir = tang;
                                Console.WriteLine(
                                    $"CORRUPT at tick {snap.Tick}: auth={V(authPos)} -> " +
                                    $"forced={V(predictor.State.Pos)} preDist={preDist:F4} m");
                            }
                            break;
                        }
                        break;
                    }
                    default:
                        break; // everything else is not this criterion
                }
            }

            // Inputs on the 20 Hz grid: walk forward until the corruption, then
            // stand, so the only thing moving the body afterwards is the
            // server's correction.
            if (terrain != null && Elapsed() >= nextInputAt)
            {
                nextInputAt = Elapsed() + 50;
                double moveY = forced ? 0 : 1;
                seq++;
                predictor.Apply(seq, new Input { MoveX = 0, MoveY = moveY, LookDir = look, ActionMask = 0 });
                net.Send(Encode.Input(0f, (float)moveY,
                                      (float)look.X, (float)look.Y, (float)look.Z, 0, seq));
            }
            System.Threading.Thread.Sleep(2);
        }

        _failed = 0;
        Check("c3a: the forced offset reached the live predicted state",
              forced && Math.Abs(preDist - CorruptDist) < 1e-2,
              $"preDist={preDist:F4} m (target {CorruptDist})");
        Check("c3b: one reconcile snaps back onto the authoritative position",
              corrected && snapDist < SnapEps,
              $"snapDist={snapDist:E3} m (limit {SnapEps})");
        double dt = correctedAtMs - forcedAtMs;
        Check("c3c: the correction lands within one tick plus network",
              corrected && dt <= OneTickNetMs,
              $"{dt:F1} ms (budget {OneTickNetMs} ms), tick {forcedTick} -> {correctedTick}");

        if (!string.IsNullOrEmpty(evidencePath))
        {
            var evidence = new
            {
                harness = "SimDump --authority (C# Predictor, Game/Core/Prediction.cs)",
                url,
                server_build = ServerBuild(url),
                corrupt_dist_m = CorruptDist,
                pre_dist_m = preDist,
                snap_dist_m = snapDist,
                correction_ms = dt,
                forced_tick = forcedTick,
                corrected_tick = correctedTick,
                forced_dir = new[] { forcedDir.X, forcedDir.Y, forcedDir.Z },
                pass = _failed == 0,
            };
            System.IO.File.WriteAllText(evidencePath,
                Newtonsoft.Json.JsonConvert.SerializeObject(evidence, Newtonsoft.Json.Formatting.Indented) + "\n");
            Console.WriteLine($"evidence: {evidencePath}");
        }

        Console.WriteLine(_failed == 0 ? "OVERALL: PASS" : $"OVERALL: FAIL ({_failed})");
        return _failed == 0 ? 0 : 1;
    }

    private static string V(Vec3 v) => $"({v.X:F3},{v.Y:F3},{v.Z:F3})";

    /// <summary>
    /// Asks the server what build it is, over HTTP, for the evidence file.
    ///
    /// Recorded rather than asserted: this harness cannot know which build it
    /// SHOULD have reached -- that is `make check-server`'s job, which knows
    /// the working tree. What it can do is stop producing evidence that does
    /// not say what it measured. A green result against a server nobody can
    /// identify afterwards is not evidence, and a packaged client was verified
    /// three times against a stale image before anyone noticed.
    ///
    /// Never fatal: a server too old to have /version is worth a note in the
    /// file, not a failed criterion.
    /// </summary>
    private static string ServerBuild(string wsUrl, string originOverride = null)
    {
        try
        {
            // A harness that goes through a latency proxy reaches the server on
            // a port that speaks WebSocket and nothing else, so deriving the
            // HTTP origin from the connect URL asks the PROXY what build it is
            // and times out. The caller that set the proxy up knows what is
            // behind it and can say so.
            if (!string.IsNullOrEmpty(originOverride))
            {
                using var direct = new System.Net.Http.HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(5),
                };
                return direct.GetStringAsync(originOverride.TrimEnd('/') + "/version")
                    .GetAwaiter().GetResult().Trim();
            }

            var u = new UriBuilder(wsUrl)
            {
                Scheme = wsUrl.StartsWith("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http",
                Path = "/version",
            };
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            return http.GetStringAsync(u.Uri).GetAwaiter().GetResult().Trim();
        }
        catch (Exception e)
        {
            return $"unknown ({e.GetType().Name})";
        }
    }

    private static SpaceAdventure.Sim.Collider[] ToSim(SpaceAdventure.Net.Collider[] rows)
    {
        var outp = new SpaceAdventure.Sim.Collider[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            outp[i] = new SpaceAdventure.Sim.Collider
            {
                Kind = (SpaceAdventure.Sim.ColliderKind)rows[i].Kind,
                Center = new Vec3(rows[i].CenterX, rows[i].CenterY, rows[i].CenterZ),
                Half = new Vec3(rows[i].HalfX, rows[i].HalfY, rows[i].HalfZ),
                Rot = new Quat(rows[i].QuatX, rows[i].QuatY, rows[i].QuatZ, rows[i].QuatW),
            };
        }
        return outp;
    }

    // ---- collider parity ----------------------------------------------------
    //
    // The C# half of the cross-language collider check. Go writes the same
    // shape from `server collide`, and test/t13-collide-parity.mjs diffs them.
    //
    // Why a separate harness at all, when both sides have unit tests: those
    // exercise one implementation against itself and say nothing about the two
    // AGREEING. This project has been bitten by that twice -- the strafe axis
    // was wrong in both sims for all of M1, so they agreed and every criterion
    // stayed green. And the C5 trajectory route touches no collider on any
    // tick, so the conformance diff cannot close this gap.
    //
    // This replaces the TypeScript half, which went with the browser client.

    private sealed class CollideScenario
    {
        public string name { get; set; }
        public double[] pos { get; set; }
        public double[] vel { get; set; }
        public double[] up { get; set; }
        public bool grounded { get; set; }
        public double radius { get; set; }
        public CollideJson[] colliders { get; set; }
    }

    private sealed class CollideJson
    {
        public int kind { get; set; }
        public double[] center { get; set; }
        public double[] half { get; set; }
        public double[] quat { get; set; }
    }

    private sealed class CollideResult
    {
        public string Name { get; set; }
        public double[] Pos { get; set; }
        public double[] Vel { get; set; }
        public bool Grounded { get; set; }
    }

    private static int Collide(string inPath, string outPath)
    {
        var scenarios = Newtonsoft.Json.JsonConvert.DeserializeObject<CollideScenario[]>(
            System.IO.File.ReadAllText(inPath));

        var results = new System.Collections.Generic.List<CollideResult>(scenarios.Length);
        foreach (CollideScenario s in scenarios)
        {
            var cs = new SpaceAdventure.Sim.Collider[s.colliders?.Length ?? 0];
            for (int i = 0; i < cs.Length; i++)
            {
                CollideJson c = s.colliders[i];
                // Narrowed to f32 exactly as Go does, because collider fields
                // are f32 ON THE WIRE. Skipping this would make the two sides
                // disagree about arithmetic that is not actually different.
                cs[i] = new SpaceAdventure.Sim.Collider
                {
                    Kind = (SpaceAdventure.Sim.ColliderKind)c.kind,
                    Center = new Vec3((float)c.center[0], (float)c.center[1], (float)c.center[2]),
                    Half = new Vec3((float)c.half[0], (float)c.half[1], (float)c.half[2]),
                    Rot = new Quat((float)c.quat[0], (float)c.quat[1],
                                   (float)c.quat[2], (float)c.quat[3]),
                };
            }

            var pos = new Vec3(s.pos[0], s.pos[1], s.pos[2]);
            var vel = new Vec3(s.vel[0], s.vel[1], s.vel[2]);
            var up = new Vec3(s.up[0], s.up[1], s.up[2]);
            bool grounded = s.grounded;
            double radius = s.radius;

            SpaceAdventure.Sim.Collide.ResolveColliders(
                ref pos, ref vel, up, ref grounded, cs, _ => radius);

            results.Add(new CollideResult
            {
                Name = s.name,
                Pos = new[] { pos.X, pos.Y, pos.Z },
                Vel = new[] { vel.X, vel.Y, vel.Z },
                Grounded = grounded,
            });
        }

        System.IO.File.WriteAllText(outPath,
            Newtonsoft.Json.JsonConvert.SerializeObject(results, Newtonsoft.Json.Formatting.Indented) + "\n");
        return 0;
    }

    // ---- C41 codec parity --------------------------------------------------
    //
    // Same shape as test/t12-codec-parity.mjs did for TypeScript: Go writes
    // `name hex` lines and this decodes them, then this writes `name hex`
    // lines and Go parses them. Each side implements PROTOCOL.md
    // independently, so only a cross-check can catch a framing or offset slip
    // -- a codec's own round-trip test agrees with its own bug.

    private static Dictionary<string, byte[]> ReadHexLines(string path)
    {
        var frames = new Dictionary<string, byte[]>();
        foreach (string line in File.ReadAllLines(path))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            string[] f = t.Split(' ');
            if (f.Length != 2) throw new InvalidDataException($"bad line: {t}");
            var bytes = new byte[f[1].Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(f[1].Substring(i * 2, 2), 16);
            }
            frames[f[0]] = bytes;
        }
        return frames;
    }

    private static string Hex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string G(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Decodes Go's S->C frames and prints one line per message.</summary>
    private static int CodecDecode(string path)
    {
        var frames = ReadHexLines(path);

        // The frame type is checked against the name on every message. A
        // decoder that ignores it will happily read a payload at the wrong
        // offset and report plausible numbers.
        void Expect(string name, ushort want, out WireReader r)
        {
            ushort got = SpaceAdventure.Net.Wire.ReadFrameType(frames[name], out r);
            if (got != want) throw new InvalidDataException($"{name}: frame type 0x{got:x4}, want 0x{want:x4}");
        }

        Expect("cmd_result", Msg.CmdResult, out var r1);
        var cr = Decode.CmdResult(r1);
        Console.WriteLine($"cmd_result seq={cr.Seq} opcode={cr.Opcode} status={cr.StatusCode} data={cr.Body}");

        Expect("defs", Msg.Defs, out var r2);
        // Raw, not the parsed tables: C41 compares BYTES with Go, and
        // re-serialising what this client understood would compare the client
        // with itself.
        Console.WriteLine($"defs data={Decode.Defs(r2).Raw}");

        Expect("colliders", Msg.Colliders, out var r3);
        foreach (var c in Decode.Colliders(r3))
        {
            Console.WriteLine($"collider kind={c.Kind} center={G(c.CenterX)},{G(c.CenterY)},{G(c.CenterZ)}" +
                              $" half={G(c.HalfX)},{G(c.HalfY)},{G(c.HalfZ)}" +
                              $" quat={G(c.QuatX)},{G(c.QuatY)},{G(c.QuatZ)},{G(c.QuatW)}");
        }

        Expect("props", Msg.Props, out var r4);
        foreach (var p in Decode.Props(r4))
        {
            Console.WriteLine($"prop asset={p.Asset} pos={G(p.PosX)},{G(p.PosY)},{G(p.PosZ)}" +
                              $" quat={G(p.QuatX)},{G(p.QuatY)},{G(p.QuatZ)},{G(p.QuatW)}" +
                              $" scale={G(p.Scale)}");
        }

        Expect("seat_result", Msg.SeatResult, out var r5);
        var sr = Decode.SeatResult(r5);
        Console.WriteLine($"seat_result entity={sr.EntityId} seat={sr.Seat} result={sr.Result}");

        return 0;
    }

    /// <summary>
    /// The C# half of the C30 drive-conformance diff: replays a
    /// {"input":{"throttle","steer"}} JSONL script through Drive.Apply on the
    /// quantised wire field, from the same mirrored start `server drive`
    /// uses (spawn point, facing the spawn bearing, grounded).
    /// </summary>
    private static int DriveDump(string scriptPath, string worldPath)
    {
        using var wdoc = JsonDocument.Parse(File.ReadAllBytes(worldPath));
        var w = wdoc.RootElement;
        var codesEl = w.GetProperty("radii");
        var codes = new ushort[codesEl.GetArrayLength()];
        for (int i = 0; i < codes.Length; i++) codes[i] = (ushort)codesEl[i].GetUInt32();
        var field = TerrainField.FromWire(codes,
            w.GetProperty("radius_min").GetDouble(),
            w.GetProperty("radius_max").GetDouble());

        var start = Step.SpawnState(field);
        Vec3 up = start.Pos.Normalized();
        var s = new RoverState
        {
            Pos = start.Pos,
            Vel = Vec3.Zero,
            Quat = Quat.FromBasis(Vec3.Cross(up, start.Facing), up, start.Facing),
            Grounded = true,
        };

        int tick = 0;
        var outBuf = new StringBuilder();
        foreach (string line in File.ReadLines(scriptPath))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            using var doc = JsonDocument.Parse(t);
            if (!doc.RootElement.TryGetProperty("input", out var ie)) continue;

            Drive.Apply(ref s,
                ie.GetProperty("throttle").GetDouble(),
                ie.GetProperty("steer").GetDouble(),
                field, Rules.DT,
                ie.TryGetProperty("eff_mult", out var dem) ? dem.GetDouble() : 0);

            outBuf.Append("{\"tick\":").Append(tick)
                  .Append(",\"pos\":[").Append(F(s.Pos.X)).Append(',').Append(F(s.Pos.Y)).Append(',').Append(F(s.Pos.Z))
                  .Append("],\"vel\":[").Append(F(s.Vel.X)).Append(',').Append(F(s.Vel.Y)).Append(',').Append(F(s.Vel.Z))
                  .Append("],\"quat\":[").Append(F(s.Quat.X)).Append(',').Append(F(s.Quat.Y)).Append(',').Append(F(s.Quat.Z)).Append(',').Append(F(s.Quat.W))
                  .Append("],\"grounded\":").Append(s.Grounded ? "true" : "false")
                  .Append("}\n");
            tick++;
        }
        Console.Out.Write(outBuf.ToString());
        Console.Error.WriteLine($"simdump: {tick} drive ticks");
        return 0;
    }

    // ---- C39: pilot prediction error under latency ------------------------
    //
    // The same instrument as Predict (C6), retargeted at the ship: join
    // through the latency proxy, walk to the owned ship, take seat 1, fly a
    // scripted run predicting with ShipPredictor, and pair each
    // post-reconcile belief against the FUTURE snapshot that describes the
    // same instant (by replay depth — Predict's comment has the full
    // argument; a constant M+1 measures cross-instant artifacts at any RTT
    // other than the one it was written for).

    private static int Pilot(string url, uint shipId, string token, string evidencePath)
    {
        using var net = new NetClient();
        net.Connect(url, "c39-pilot", token);

        TerrainField terrain = null;
        var rows = new System.Collections.Generic.Dictionary<uint, EntityRow>();
        ushort myAck = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (terrain == null && DateTime.UtcNow < deadline)
        {
            while (net.Poll(out var f))
            {
                if (f.Type != Msg.Terrain) continue;
                var t = Decode.Terrain(f.Reader);
                terrain = TerrainField.FromWire(t.Radii, t.RadiusMin, t.RadiusMax);
            }
            System.Threading.Thread.Sleep(5);
        }
        if (terrain == null) { Console.Error.WriteLine("c39: no terrain"); return 1; }

        void Drain()
        {
            while (net.Poll(out var f))
            {
                if (f.Type != Msg.Snapshot) continue;
                Snapshot snap = Decode.Snapshot(f.Reader);
                myAck = snap.AckSeq;
                foreach (EntityRow row in snap.Entities) rows[row.Id] = row;
            }
        }

        // Walk to the ship: greedy wish toward it, server-authoritative.
        ushort seq = 0;
        for (int i = 0; i < 600; i++)
        {
            Drain();
            if (rows.TryGetValue(net.EntityId, out var me) && rows.TryGetValue(shipId, out var sh))
            {
                var mp = new Vec3(me.PosX, me.PosY, me.PosZ);
                var sp = new Vec3(sh.PosX, sh.PosY, sh.PosZ);
                if ((sp - mp).Length < 5) break;
                Vec3 up = mp.Normalized();
                Vec3 look = (sp - mp).RejectFrom(up);
                if (look.Length > 1e-6) look = look.Normalized(); else look = new Vec3(1, 0, 0);
                seq++;
                net.Send(Encode.Input(0f, 1f, (float)look.X, (float)look.Y, (float)look.Z,
                                      SpaceAdventure.Net.Action.Sprint, seq));
            }
            System.Threading.Thread.Sleep(50);
        }

        net.Send(Encode.Board(shipId, 1));
        deadline = DateTime.UtcNow.AddSeconds(5);
        bool seated = false;
        while (!seated && DateTime.UtcNow < deadline)
        {
            Drain();
            seated = rows.TryGetValue(net.EntityId, out var me) && me.ParentId == shipId && me.Seat == 1;
            System.Threading.Thread.Sleep(20);
        }
        if (!seated) { Console.Error.WriteLine("c39: never seated"); return 1; }

        // The flight: nose-up climb, then thrust with gentle turns — enough
        // dynamics that a prediction bug shows, tame enough to stay stable.
        const int FlyTicks = 400;
        var predictor = new ShipPredictor();
        predictor.Seed(terrain);
        var snaps = new System.Collections.Generic.List<Snap>();
        int k = 0;
        var start = DateTime.UtcNow;
        double Elapsed() => (DateTime.UtcNow - start).TotalMilliseconds;
        double nextTickAt = 0;

        while (k < FlyTicks || Elapsed() < FlyTicks * 50 + 1500)
        {
            while (net.Poll(out var f))
            {
                if (f.Type != Msg.Snapshot) continue;
                Snapshot snap = Decode.Snapshot(f.Reader);
                foreach (EntityRow row in snap.Entities)
                {
                    if (row.Id != shipId) continue;
                    var rec = new Snap { T = snap.Tick, M = snap.AckSeq };
                    rec.Auth = new Vec3(row.PosX, row.PosY, row.PosZ);
                    rec.HasAuth = true;

                    var st = predictor.State;
                    if (st.Vel.Length > 0.3) { rec.Trk = st.Vel.Normalized(); rec.HasTrk = true; }

                    predictor.Reconcile(rec.Auth,
                        new Vec3(row.VelX, row.VelY, row.VelZ),
                        new Quat(row.QuatX, row.QuatY, row.QuatZ, row.QuatW),
                        row.Grounded, row.Space, snap.AckSeq);
                    rec.Post = predictor.State.Pos;
                    rec.Ahead = predictor.PendingCount;
                    snaps.Add(rec);
                    break;
                }
            }

            if (k < FlyTicks && Elapsed() >= nextTickAt)
            {
                nextTickAt = Elapsed() + 50;
                var inp = new FlightInput
                {
                    Thrust = k < 20 ? 1 : 1,
                    PitchRate = k < 20 ? -1.2 : 0,
                    YawRate = k >= 100 && k < 250 ? 0.6 : 0,
                    Roll = 0,
                    Boost = false,
                };
                seq++;
                predictor.Apply(seq, inp);
                net.Send(Encode.Input((float)inp.Thrust, 0,
                    (float)inp.YawRate, (float)inp.PitchRate, 0, 0, seq, 1));
                k++;
            }
            System.Threading.Thread.Sleep(2);
        }

        var errs = new System.Collections.Generic.List<double>();
        int snapbacks = 0;
        for (int i = 0; i < snaps.Count; i++)
        {
            Snap a = snaps[i];
            if (a.Post.Length == 0 || a.M <= 0 || a.Ahead <= 0) continue;
            Snap b = null;
            for (int j = i + 1; j < snaps.Count; j++)
            {
                if (snaps[j].M != a.M + a.Ahead) continue;
                if (snaps[j].HasAuth && snaps[j].T == a.T + (uint)a.Ahead) b = snaps[j];
                break;
            }
            if (b == null) continue;
            errs.Add((a.Post - b.Auth).Length);
            if (a.HasTrk && Vec3.Dot(b.Auth - a.Post, a.Trk) < SnapbackM) snapbacks++;
        }

        errs.Sort();
        double Pq(double q) => errs.Count == 0 ? double.NaN : errs[Math.Min(errs.Count - 1, (int)(q * errs.Count))];
        double p50 = Pq(0.50), p95 = Pq(0.95);

        Check("c39 measured enough paired samples", errs.Count >= 100, $"{errs.Count} samples");
        Check("c39 p95 pilot prediction error under 0.5 m", p95 < 0.5, $"p95 {F(p95)} m");
        Check("c39 no snap-backs", snapbacks == 0, $"{snapbacks}");

        File.WriteAllText(evidencePath,
            $"{{\"criterion\":\"C39\",\"harness\":\"SimDump --pilot\",\"samples\":{errs.Count}," +
            $"\"p50\":{F(p50)},\"p95\":{F(p95)},\"snapbacks\":{snapbacks}}}\n");
        Console.WriteLine(_failed == 0 ? "\nOVERALL: PASS" : $"\nOVERALL: FAIL ({_failed})");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The C# half of the C34 flight-conformance diff: replays a pilot
    /// input script through Flight.Apply on the quantised wire field, from
    /// the same mirrored start `server flight` uses.
    /// </summary>
    private static int FlightDump(string scriptPath, string worldPath)
    {
        using var wdoc = JsonDocument.Parse(File.ReadAllBytes(worldPath));
        var w = wdoc.RootElement;
        var codesEl = w.GetProperty("radii");
        var codes = new ushort[codesEl.GetArrayLength()];
        for (int i = 0; i < codes.Length; i++) codes[i] = (ushort)codesEl[i].GetUInt32();
        var field = TerrainField.FromWire(codes,
            w.GetProperty("radius_min").GetDouble(),
            w.GetProperty("radius_max").GetDouble());

        var start = Step.SpawnState(field);
        Vec3 up = start.Pos.Normalized();
        var s = new ShipSimState
        {
            Pos = start.Pos,
            Vel = Vec3.Zero,
            Quat = Quat.FromBasis(Vec3.Cross(up, start.Facing), up, start.Facing),
            Omega = Vec3.Zero,
            Grounded = true,
        };

        int tick = 0;
        var outBuf = new StringBuilder();
        foreach (string line in File.ReadLines(scriptPath))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            using var doc = JsonDocument.Parse(t);
            if (!doc.RootElement.TryGetProperty("input", out var ie)) continue;

            var inp = new FlightInput
            {
                Thrust = ie.GetProperty("thrust").GetDouble(),
                Roll = ie.GetProperty("roll").GetDouble(),
                YawRate = ie.GetProperty("yaw_rate").GetDouble(),
                PitchRate = ie.GetProperty("pitch_rate").GetDouble(),
                Boost = ie.GetProperty("boost").GetBoolean(),
                EffMult = ie.TryGetProperty("eff_mult", out var fem) ? fem.GetDouble() : 0,
            };
            Flight.Apply(ref s, inp, field, Rules.DT);

            outBuf.Append("{\"tick\":").Append(tick)
                  .Append(",\"pos\":[").Append(F(s.Pos.X)).Append(',').Append(F(s.Pos.Y)).Append(',').Append(F(s.Pos.Z))
                  .Append("],\"vel\":[").Append(F(s.Vel.X)).Append(',').Append(F(s.Vel.Y)).Append(',').Append(F(s.Vel.Z))
                  .Append("],\"quat\":[").Append(F(s.Quat.X)).Append(',').Append(F(s.Quat.Y)).Append(',').Append(F(s.Quat.Z)).Append(',').Append(F(s.Quat.W))
                  .Append("],\"omega\":[").Append(F(s.Omega.X)).Append(',').Append(F(s.Omega.Y)).Append(',').Append(F(s.Omega.Z))
                  .Append("],\"grounded\":").Append(s.Grounded ? "true" : "false")
                  .Append(",\"space\":").Append(s.Space ? "true" : "false")
                  .Append("}\n");
            tick++;
        }
        Console.Out.Write(outBuf.ToString());
        Console.Error.WriteLine($"simdump: {tick} flight ticks");
        return 0;
    }

    /// <summary>Writes C->S frames for Go to parse.</summary>
    private static int CodecEncode(string path)
    {
        var sb = new StringBuilder();
        sb.Append("cmd ").Append(Hex(Encode.Cmd(4097, Op.ShopBuy,
            "{\"npc\":7,\"item\":\"weapon.pulse\",\"qty\":1}"))).Append('\n');
        sb.Append("fire ").Append(Hex(Encode.Fire(513, 0, 0, 1))).Append('\n');
        sb.Append("board ").Append(Hex(Encode.Board(0x0A0B0C0D, 2))).Append('\n');
        sb.Append("disembark ").Append(Hex(Encode.Disembark())).Append('\n');
        File.WriteAllText(path, sb.ToString());
        return 0;
    }


    // ---- U10: join a live server -------------------------------------------
    //
    // The transport's only real test is a real server. This joins, waits for
    // the handshake and the first snapshots, and reports what it decoded --
    // headless, so it runs in CI and from a terminal without the Editor.

    private static int Join(string url)
    {
        using var net = new NetClient();
        net.Connect(url, "headless", $"headless-{Guid.NewGuid():N}");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        int snapshots = 0, spawns = 0, entities = 0;
        bool terrain = false, defs = false, colliders = false;

        while (DateTime.UtcNow < deadline && (snapshots < 40 || net.RttMs < 0))
        {
            while (net.Poll(out var frame))
            {
                switch (frame.Type)
                {
                    case Msg.HelloAck:
                        Console.WriteLine($"joined: entity={net.EntityId} tickHz={net.TickHz} seed={net.WorldSeed}");
                        break;
                    case Msg.Terrain:
                    {
                        var t = Decode.Terrain(frame.Reader);
                        Console.WriteLine($"terrain: face_grid={t.FaceGrid} radii={t.Radii.Length}" +
                                          $" range=[{t.RadiusMin:F1}, {t.RadiusMax:F1}]");
                        terrain = true;
                        break;
                    }
                    case Msg.Defs:
                    {
                        // Not just "bytes arrived": the blob has to PARSE, and
                        // the tables the client renders from have to be in it.
                        // The old check was `.Length > 0`, which a truncated or
                        // reshaped payload would still have passed.
                        Defs d = Decode.Defs(frame.Reader);
                        int items = d.Items?.Count ?? 0;
                        int npcs = d.Npcs?.Count ?? 0;
                        Console.WriteLine($"defs: items={items} entities={d.Entities?.Count ?? 0}" +
                                          $" npcs={npcs}");
                        defs = items > 0 && npcs > 0;
                        break;
                    }
                    case Msg.Colliders:
                    {
                        var list = Decode.Colliders(frame.Reader);
                        Console.WriteLine($"colliders: {list.Length}");
                        colliders = true;
                        break;
                    }
                    case Msg.Spawn:
                        Decode.Spawn(frame.Reader);
                        spawns++;
                        break;
                    case Msg.Snapshot:
                    {
                        var snap = Decode.Snapshot(frame.Reader);
                        entities = snap.Entities.Length;
                        snapshots++;
                        break;
                    }
                    case Msg.Event:
                        Decode.Event(frame.Reader);
                        break;
                }
            }
            Thread.Sleep(10);
        }

        Console.WriteLine($"snapshots={snapshots} spawns={spawns} entities={entities} rtt={net.RttMs}ms");
        Check("joined and got an entity id", net.EntityId != 0);
        Check("terrain arrived", terrain);
        Check("defs arrived", defs);
        Check("colliders arrived", colliders);
        Check("spawns arrived", spawns > 0);
        Check("snapshots arrived", snapshots >= 40, $"{snapshots}");
        Check("own body is in the snapshot", entities > 0);
        Check("ping measured an RTT", net.RttMs >= 0, $"{net.RttMs} ms");

        Console.WriteLine(_failed == 0 ? "\nOVERALL: PASS" : $"\nOVERALL: FAIL ({_failed})");
        return _failed == 0 ? 0 : 1;
    }

}
