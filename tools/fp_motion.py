#!/usr/bin/env python3
"""fp_motion.py <motion.csv> [--max-gap 0.02] [--max-pop 8]

Reads the rig's `-uiMotion` record and prints, per phase, what a still frame
cannot show (ROADMAP Phase 27):

  gapR/gapL  worst distance (cm) from a hand to its socket on the weapon
  step       worst frame-to-frame move of the sight, the bore and each hand,
             as a speed (cm/s, deg/s): a pop is one frame far above the rest
  pop        that worst step over the phase's median step: ~1-3 is smooth
             motion, 10+ is a visible jump

Exit 1 when --max-gap (m) or --max-pop is exceeded. Stdlib only.
"""
import csv, math, statistics, sys

def main():
    args = sys.argv[1:]
    if not args: sys.exit(__doc__)
    opt = {"--max-gap": None, "--max-pop": None}
    path = args[0]
    for i, a in enumerate(args):
        if a in opt: opt[a] = float(args[i + 1])
    rows = list(csv.DictReader(open(path)))
    phases = []
    for r in rows:
        if not phases or phases[-1][0] != r["phase"]: phases.append((r["phase"], []))
        phases[-1][1].append({k: (v if k == "phase" else float(v)) for k, v in r.items()})
    bad = []
    print(f"{'phase':8} {'fps':>4} {'gapR':>6} {'gapL':>6} | {'sight cm/s':>10} {'pop':>5} | {'bore deg/s':>10} {'pop':>5} | {'handL cm/s':>10} {'pop':>5}")
    for name, fr in phases:
        if len(fr) < 4: continue
        dts = [b["t"] - a["t"] for a, b in zip(fr, fr[1:]) if b["t"] > a["t"]]
        fps = 1 / statistics.median(dts)
        def speeds(keys, angular=False):
            out = []
            for a, b in zip(fr, fr[1:]):
                dt = b["t"] - a["t"]
                if dt <= 0: continue
                va, vb = [a[k] for k in keys], [b[k] for k in keys]
                if angular:
                    dot = max(-1.0, min(1.0, sum(x * y for x, y in zip(va, vb))))
                    out.append(math.degrees(math.acos(dot)) / dt)
                else:
                    out.append(100 * math.dist(va, vb) / dt)
            return out
        def peak_pop(v):
            # The worst step against the typical one; the 1.0 floor (cm/s or
            # deg/s) keeps a still phase from dividing by nothing.
            return max(v), max(v) / max(statistics.median(v), 1.0)
        gr = max(f["gapR"] for f in fr); gl = max(f["gapL"] for f in fr)
        s, sp = peak_pop(speeds(("sx", "sy", "sz")))
        b, bp = peak_pop(speeds(("bx", "by", "bz"), angular=True))
        h, hp = peak_pop(speeds(("hlx", "hly", "hlz")))
        print(f"{name:8} {fps:4.0f} {gr*100:6.1f} {gl*100:6.1f} | {s:10.1f} {sp:5.1f} | {b:10.1f} {bp:5.1f} | {h:10.1f} {hp:5.1f}")
        if opt["--max-gap"] is not None and max(gr, gl) > opt["--max-gap"]: bad.append(f"{name}: hand {max(gr, gl)*100:.1f} cm off its socket")
        if opt["--max-pop"] is not None and max(sp, bp, hp) > opt["--max-pop"]: bad.append(f"{name}: pop {max(sp, bp, hp):.1f}")
    if bad:
        print("fp_motion: FAIL\n  " + "\n  ".join(bad)); sys.exit(1)
    if any(v is not None for v in opt.values()): print("fp_motion: within limits")

if __name__ == "__main__":
    main()
