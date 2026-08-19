/**
 * Deterministic PRNG for client-side decoration (props, mock world).
 * Seeded from world_seed so every client scatters identically
 * (PROTOCOL: world_seed seeds client-side decoration only).
 */

/** mulberry32 — small, fast, deterministic. Returns floats in [0, 1). */
export function mulberry32(seed: number): () => number {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/** Deterministic hash of an integer triplet to [0, 1). */
export function hash3(x: number, y: number, z: number): number {
  let n =
    (Math.imul(x, 0x27d4eb2d) ^ Math.imul(y, 0x165667b1) ^ Math.imul(z, 0x9e3779b1) ^ 0x85ebca6b) | 0
  n = Math.imul(n ^ (n >>> 15), 0x2c1b3c6d)
  n = Math.imul(n ^ (n >>> 12), 0x297a2d39)
  n ^= n >>> 15
  return (n >>> 0) / 4294967296
}
