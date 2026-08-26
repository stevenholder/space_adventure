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

        Console.Error.WriteLine("usage: SimDump --selftest");
        Console.Error.WriteLine("       SimDump --dump <script.jsonl> --world <world.json>");
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

        Console.WriteLine(_failed == 0 ? "\nOVERALL: PASS" : $"\nOVERALL: FAIL ({_failed})");
        return _failed == 0 ? 0 : 1;
    }
}
