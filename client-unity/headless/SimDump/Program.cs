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
        if (Array.IndexOf(args, "--codec-decode") >= 0) return CodecDecode(Arg(args, "--codec-decode"));
        if (Array.IndexOf(args, "--codec-encode") >= 0) return CodecEncode(Arg(args, "--codec-encode"));
        if (Array.IndexOf(args, "--join") >= 0) return Join(Arg(args, "--join"));

        Console.Error.WriteLine("usage: SimDump --selftest");
        Console.Error.WriteLine("       SimDump --dump <script.jsonl> --world <world.json>");
        Console.Error.WriteLine("       SimDump --codec-decode <go.hex>   decode Go's S->C frames");
        Console.Error.WriteLine("       SimDump --codec-encode <out.hex>  write C->S frames for Go");
        Console.Error.WriteLine("       SimDump --join <ws-url>           join a live server, report what arrives");
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

        FrameConventionChecks();
        PredictionChecks();

        Console.WriteLine(_failed == 0 ? "\nOVERALL: PASS" : $"\nOVERALL: FAIL ({_failed})");
        return _failed == 0 ? 0 : 1;
    }

    // ---- frame conventions -------------------------------------------------
    //
    // The Unity client converts sim space to engine space by negating Z, and
    // that conversion is only correct while these two facts hold. Both live in
    // Sim, both are invisible from the Unity side, and getting either wrong
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
        // Unity client rebuilds a body's direction from exactly this.
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
        Console.WriteLine($"defs data={Decode.Defs(r2)}");

        Expect("colliders", Msg.Colliders, out var r3);
        foreach (var c in Decode.Colliders(r3))
        {
            Console.WriteLine($"collider kind={c.Kind} center={G(c.CenterX)},{G(c.CenterY)},{G(c.CenterZ)}" +
                              $" half={G(c.HalfX)},{G(c.HalfY)},{G(c.HalfZ)}" +
                              $" quat={G(c.QuatX)},{G(c.QuatY)},{G(c.QuatZ)},{G(c.QuatW)}");
        }
        return 0;
    }

    /// <summary>Writes C->S frames for Go to parse.</summary>
    private static int CodecEncode(string path)
    {
        var sb = new StringBuilder();
        sb.Append("cmd ").Append(Hex(Encode.Cmd(4097, Op.ShopBuy,
            "{\"npc\":7,\"item\":\"weapon.pulse\",\"qty\":1}"))).Append('\n');
        sb.Append("fire ").Append(Hex(Encode.Fire(513, 0, 0, 1))).Append('\n');
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
                        defs = Decode.Defs(frame.Reader).Length > 0;
                        break;
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
