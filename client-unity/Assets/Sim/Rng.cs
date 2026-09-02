// Deterministic PRNG for client-side decoration (props, scatter).
//
// Ported from the retired TypeScript client's sim/rng.ts. NOT on the
// conformance path: the server
// owns every random outcome that affects the world (weapon spread, loot rolls
// are rolled server-side and arrive over the wire). This exists so every
// client scatters decoration identically from world_seed, which is the only
// thing PROTOCOL promises it for.
//
// The bit-twiddling is JavaScript's, exactly: Math.imul is a 32-bit signed
// wrapping multiply and >>> is an unsigned shift. Getting either wrong gives a
// generator that looks random and disagrees with every other client.

using System;

namespace SpaceAdventure.Sim
{
    public static class Rng
    {
        /// <summary>Math.imul: 32-bit signed wrapping multiply.</summary>
        private static int Imul(int a, int b) => unchecked(a * b);

        /// <summary>
        /// mulberry32. Returns a closure producing doubles in [0, 1), matching
        /// the TypeScript generator value for value from the same seed.
        /// </summary>
        public static Func<double> Mulberry32(uint seed)
        {
            uint a = seed;
            return () =>
            {
                unchecked
                {
                    a = a + 0x6d2b79f5u;
                    int ai = (int)a;
                    int t = Imul(ai ^ (int)((uint)ai >> 15), 1 | ai);
                    t = (t + Imul(t ^ (int)((uint)t >> 7), 61 | t)) ^ t;
                    return ((uint)(t ^ (int)((uint)t >> 14))) / 4294967296.0;
                }
            };
        }

        /// <summary>Deterministic hash of an integer triplet to [0, 1).</summary>
        public static double Hash3(int x, int y, int z)
        {
            unchecked
            {
                int n = Imul(x, 0x27d4eb2d) ^ Imul(y, 0x165667b1) ^ Imul(z, unchecked((int)0x9e3779b1)) ^ unchecked((int)0x85ebca6b);
                n = Imul(n ^ (int)((uint)n >> 15), 0x2c1b3c6d);
                n = Imul(n ^ (int)((uint)n >> 12), 0x297a2d39);
                n ^= (int)((uint)n >> 15);
                return ((uint)n) / 4294967296.0;
            }
        }
    }
}
