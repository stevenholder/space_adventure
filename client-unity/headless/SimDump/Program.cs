// Headless runner for the Sim assembly.
//
// Console app with assert-style checks that exit non-zero, matching the
// test/*.mjs harnesses: no test framework, no fixtures, no NuGet. It runs with
// no Unity Editor anywhere, which is the point -- C44 requires Sim and Net to
// build and test in CI, and C40's conformance dump has to run there too.
//
//   dotnet run --project client-unity/headless/SimDump -- --selftest

using System;
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
        if (Array.IndexOf(args, "--selftest") < 0)
        {
            Console.Error.WriteLine("usage: SimDump --selftest");
            Console.Error.WriteLine("  (the C40 trajectory dump lands here with the step port)");
            return 2;
        }

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
        var tiny = new Vec3(1e-9, 0, 0);
        var big = new Vec3(1, 0, 0);
        double delta = (big + tiny).X - big.X;
        Check("f64 precision, not f32", delta > 0.9e-9 && delta < 1.1e-9, $"got {delta:R}");

        var n = new Vec3(3, 0, 0).Normalized();
        Check("normalize returns a unit vector", Math.Abs(n.Length - 1) < 1e-15);
        Check("normalize of zero is zero, not NaN", Vec3.Zero.Normalized().Equals(Vec3.Zero));

        var up = new Vec3(0, 1, 0);
        var v = new Vec3(2, 7, -1);
        Check("rejection removes the normal component",
            Math.Abs(Vec3.Dot(v.RejectFrom(up), up)) < 1e-15);

        Console.WriteLine(_failed == 0 ? "\nOVERALL: PASS" : $"\nOVERALL: FAIL ({_failed})");
        return _failed == 0 ? 0 : 1;
    }
}
