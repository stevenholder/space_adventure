#!/usr/bin/env python3
"""
Criterion 9 — terrain shape audit of the generated FIELD (wire u16 values),
independent of the generator code. Checks GDD "Must hold":

  1.  >= 70% of surface samples walkable (slope < max_slope 50 deg)
  2.  spawn disc flat to +/-0.5 m, walkable outward in every direction
  3.  all radii inside [radius_min, radius_max]
  4.  expected crater count present (6-12, GDD crater_count)
  5.  all six landmarks present at their specified radii, >= 60 deg apart
  6.  >= 60% of sampled surface points can see a landmark over the horizon

Method: the field is decoded straight from the captured wire payload
(test/out/world-seed1337.json). Placement ground truth (crater count/params,
landmark directions) comes from test/out/t9-probe-1337.json — produced by
test/tools/terrain-probe, a byte-identical copy of server/internal/terrain
(sha-verified). The audit then verifies each placed feature against the FIELD
itself, and cross-checks the independent Python decoder against the probe's
SampleRadius at fixed directions.

Usage: python3 t9-terrain.py [world.json] [probe.json]
"""
import json
import math
import sys

WORLD = sys.argv[1] if len(sys.argv) > 1 else "test/out/world-seed1337.json"
PROBE = sys.argv[2] if len(sys.argv) > 2 else "test/out/t9-probe-1337.json"

# ---------------------------------------------------------------- field
w = json.load(open(WORLD))
G = w["face_grid"]
RMIN = w["radius_min"]
RMAX = w["radius_max"]
RADIU = w["radii"]
RANGE = RMAX - RMIN


def decode(code):
    return RMIN + code / 65535.0 * RANGE


# dominant axis -> (u-axis, v-axis) in ascending axis order
#   x dominant: u = y, v = z | y dominant: u = x, v = z | z dominant: u = x, v = y
AXIS_ORDER = [(1, 2), (0, 2), (0, 1)]


def face_of(d):
    """largest-magnitude component; tie -> earlier face in +X,-X,+Y,-Y,+Z,-Z."""
    ax = [abs(d[0]), abs(d[1]), abs(d[2])]
    axis = 0
    best = ax[0]
    for a in range(1, 3):
        if ax[a] > best:
            best, axis = ax[a], a
    sign = 0 if d[axis] >= 0 else 1  # + dir -> even face, - dir -> odd
    face = 2 * axis + sign
    return face, AXIS_ORDER[axis]


def face_uv(d, face, order):
    a = face // 2
    s = 1.0 if d[a] >= 0 else -1.0
    dom = d[a] * s
    o = AXIS_ORDER[a]
    return d[o[0]] / dom, d[o[1]] / dom


def dir_of(face, u, v):
    s = 1.0 if face % 2 == 0 else -1.0
    a = face // 2
    o = AXIS_ORDER[a]
    d = [0.0, 0.0, 0.0]
    d[a] = s
    d[o[0]] = u
    d[o[1]] = v
    n = math.sqrt(sum(x * x for x in d))
    return [x / n for x in d]


def sample_radius(d):
    n = math.sqrt(sum(x * x for x in d))
    d = [x / n for x in d]
    face, order = face_of(d)
    u, v = face_uv(d, face, order)
    cf = (u + 1) / 2 * (G - 1)
    rf = (v + 1) / 2 * (G - 1)
    c0 = min(G - 2, int(math.floor(cf)))
    r0 = min(G - 2, int(math.floor(rf)))
    tc = cf - c0
    tr = rf - r0
    base = face * G * G
    r = lambda rr, cc: decode(RADIU[base + rr * G + cc])
    a = r(r0, c0) * (1 - tc) + r(r0, c0 + 1) * tc
    b = r(r0 + 1, c0) * (1 - tc) + r(r0 + 1, c0 + 1) * tc
    return a * (1 - tr) + b * tr


def norm3(v):
    n = math.sqrt(sum(x * x for x in v))
    if n < 1e-12:
        return [0, 0, 0]
    return [x / n for x in v]


def cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


NEPS = math.radians(2.0)  # normal_eps


def surface_normal(up):
    u = up
    k = [1, 0, 0] if abs(u[0]) <= 0.9 else [0, 1, 0]
    du = dot(k, u)
    e1 = norm3([k[0] - u[0] * du, k[1] - u[1] * du, k[2] - u[2] * du])
    e2 = cross(u, e1)
    P0 = [u[i] * sample_radius(u) for i in range(3)]
    d1 = norm3([u[i] + e1[i] * NEPS for i in range(3)])
    d2 = norm3([u[i] + e2[i] * NEPS for i in range(3)])
    P1 = [d1[i] * sample_radius(d1) for i in range(3)]
    P2 = [d2[i] * sample_radius(d2) for i in range(3)]
    v1 = [P1[i] - P0[i] for i in range(3)]
    v2 = [P2[i] - P0[i] for i in range(3)]
    nn = cross(v1, v2)
    if dot(nn, u) < 0:
        nn = [-x for x in nn]
    return norm3(nn)


def slope_deg(up):
    n = surface_normal(up)
    c = max(-1.0, min(1.0, dot(n, up)))
    return math.degrees(math.acos(c))


def ang_dist(a, b):
    return math.degrees(math.acos(max(-1.0, min(1.0, dot(a, b)))))


def rot_axis(u, axis, ang_rad):
    """rotate unit vector u by ang_rad around unit axis (Rodrigues)."""
    c, s = math.cos(ang_rad), math.sin(ang_rad)
    k = cross(axis, u)
    kd = dot(axis, u)
    return norm3([
        u[0] * c + k[0] * s + axis[0] * kd * (1 - c),
        u[1] * c + k[1] * s + axis[1] * kd * (1 - c),
        u[2] * c + k[2] * s + axis[2] * kd * (1 - c),
    ])


def ring_stats(d, arc_m, npts=32):
    """mean/max/std radius on a small circle of radius arc_m around d."""
    up = norm3(d)
    k = [1, 0, 0] if abs(up[0]) <= 0.9 else [0, 1, 0]
    du = dot(k, up)
    e1 = norm3([k[0] - up[0] * du, k[1] - up[1] * du, k[2] - up[2] * du])
    e2 = cross(up, e1)
    t = arc_m / 150.0  # arc metres / planet radius = radians (no degrees!)
    ct, st = math.cos(t), math.sin(t)
    vals = []
    for j in range(npts):
        a = 2 * math.pi * j / npts
        dd = norm3([
            up[0] * ct + (e1[0] * math.cos(a) + e2[0] * math.sin(a)) * st,
            up[1] * ct + (e1[1] * math.cos(a) + e2[1] * math.sin(a)) * st,
            up[2] * ct + (e1[2] * math.cos(a) + e2[2] * math.sin(a)) * st,
        ])
        vals.append(sample_radius(dd))
    m = sum(vals) / len(vals)
    var = sum((x - m) ** 2 for x in vals) / len(vals)
    return m, max(vals), math.sqrt(var)


lattice = []
for face in range(6):
    for row in range(G):
        v = 2 * row / (G - 1) - 1
        for col in range(G):
            u = 2 * col / (G - 1) - 1
            lattice.append(dir_of(face, u, v))

probe = json.load(open(PROBE))
probe_craters = probe["craters"]
probe_lms = {l["name"]: l for l in probe["landmarks"]}

results = {}

# --- decoder cross-check vs probe SampleRadius at fixed directions
probe_dirs = [
    [0, 1, 0],
    [0.3333333333, 0.9428090416, 0],
    [1, 0, 0], [-1, 0, 0], [0, 0, 1], [0, 0, -1], [0, -1, 0],
    [0.57735026919, 0.57735026919, 0.57735026919],
    [-0.57735026919, 0.57735026919, -0.57735026919],
    [0.3, 0.9, 0.31622776602],
    [0.5, 0.3, -0.81240384047],
]
max_dd = 0.0
per_dir = []
for i, d in enumerate(probe_dirs):
    mine = sample_radius(d)
    theirs = probe["probe_radius"][str(i)]
    max_dd = max(max_dd, abs(mine - theirs))
    per_dir.append(round(mine - theirs, 6))
results["decoder_crosscheck"] = {
    "max_abs_diff_m": round(max_dd, 9),
    "per_dir_diff_m": per_dir,
    "pass": max_dd < 0.001,
}

# --- 3. radius bounds (all stored samples)
mn = min(decode(c) for c in RADIU)
mx = max(decode(c) for c in RADIU)
# --- 1b. global-floor diagnostic: the spire/twinpeaks closures max() with a
# nominal 150.0 outside their footprint, which (if present) clamps the whole
# planet to >= 150 m and erases the GDD step-1 lowland relief.
dec_all = [decode(c) for c in RADIU]
n_below150 = sum(1 for v in dec_all if v < 149.99)
n_at150 = sum(1 for v in dec_all if 149.99 <= v <= 150.01)
n_above150 = sum(1 for v in dec_all if v > 150.01)
results["radii"] = {
    "min": round(mn, 3), "max": round(mx, 3),
    "bounds": [RMIN, RMAX],
    "nodes_total": len(dec_all),
    "fraction_below_150m": round(n_below150 / len(dec_all), 4),
    "fraction_at_150m_pm0.02": round(n_at150 / len(dec_all), 4),
    "fraction_above_150m": round(n_above150 / len(dec_all), 4),
    "pass": RMIN - 1e-9 <= mn and mx <= RMAX + 1e-9,
}

# --- 1. walkable fraction over ALL lattice nodes (as the generator's Report does)
n = walkable = 0
disc_max_err = 0.0
disc_samples = 0
r_spawn = sample_radius([0, 1, 0])
for face in range(6):
    for row in range(G):
        v = 2 * row / (G - 1) - 1
        for col in range(G):
            u = 2 * col / (G - 1) - 1
            d = dir_of(face, u, v)
            r = decode(RADIU[face * G * G + row * G + col])
            n += 1
            if slope_deg(d) <= 50.0:
                walkable += 1
            a = ang_dist(d, [0, 1, 0])
            if a <= math.degrees(25.0 / 150.0):
                disc_samples += 1
                disc_max_err = max(disc_max_err, abs(r - r_spawn))
walk_frac = walkable / n
results["walkable"] = {
    "samples": n, "walkable": walkable, "fraction": round(walk_frac, 4),
    "probe_report_fraction": probe["report"]["walkable_fraction"],
    "threshold": 0.70, "pass": walk_frac >= 0.70,
}

# --- 2. spawn disc flat to +/-0.5 m
results["spawn_disc"] = {
    "r_spawn": round(r_spawn, 3), "max_dev_m": round(disc_max_err, 3),
    "samples": disc_samples, "tolerance_m": 0.5,
    "pass": disc_max_err <= 0.5,
}

# walkable outward in every direction: slope just outside the disc (26 m)
# for all azimuths, plus max slope through the blend band (26..39 m)
out_max_slope = 0.0
for i in range(72):
    th = 2 * math.pi * i / 72
    t = 26.0 / 150.0
    d = norm3([math.sin(t) * math.cos(th), math.cos(t), math.sin(t) * math.sin(th)])
    out_max_slope = max(out_max_slope, slope_deg(d))
band_max_slope = 0.0
for i in range(144):
    th = 2 * math.pi * i / 144
    for m in (26, 30, 35, 39):
        t = m / 150.0
        d = norm3([math.sin(t) * math.cos(th), math.cos(t), math.sin(t) * math.sin(th)])
        band_max_slope = max(band_max_slope, slope_deg(d))
results["spawn_outward"] = {
    "max_slope_just_outside_deg": round(out_max_slope, 2),
    "max_slope_blend_band_deg": round(band_max_slope, 2),
    "max_slope_deg": 50.0,
    "pass": out_max_slope <= 50.0 and band_max_slope <= 50.0,
}

# --- 4. craters: verify each placed crater appears as a field depression
# (centre depth vs ring at the rim arc), plus an independent basin scan for
# unlisted features.
notch_dir = probe_lms["notch"]["dir"]
crater_checks = []
spawn_hidden = []
for c in probe_craters:
    d = c["dir"]
    rim_arc = c["diam_m"] / 2  # metres
    rc = sample_radius(d)
    ring_m, ring_mx, ring_sd = ring_stats(d, rim_arc)
    depth_obs = ring_m - rc
    # ang_dist() returns DEGREES; metres = deg * pi/180 * PlanetRadius
    spawn_m = ang_dist(d, [0, 1, 0]) * math.pi / 180.0 * 150.0
    ok = depth_obs >= 0.6 * c["depth_m"]
    cause = None
    if not ok:
        if spawn_m <= 25.0:
            cause = "under_spawn_plain"
        elif abs(rc - 150.0) < 0.05 and depth_obs < 0.4 * c["depth_m"]:
            cause = "erased_by_150m_floor"
    crater_checks.append({
        "dir": [round(x, 3) for x in d],
        "diam_m": round(c["diam_m"], 1),
        "depth_expected_m": round(c["depth_m"], 1),
        "depth_observed_m": round(depth_obs, 1),
        "dist_from_spawn_m": round(spawn_m, 1),
        "dist_from_notch_dir_deg": round(ang_dist(d, notch_dir), 1),
        "pass": ok,
        "fail_cause": cause,
    })
    if cause == "under_spawn_plain":
        spawn_hidden.append(c["dir"])
# independent basin scan (circular rims, depth >= 2.5 m) for extra features
basin_cands = []
for d in lattice:
    r0 = sample_radius(d)
    bd, bd_arc, bd_sd = 0.0, 0, 0.0
    for arc in (10, 15, 20, 25, 30, 45, 60):
        m, mx, sd = ring_stats(d, arc, npts=20)
        if m - r0 > bd:
            bd, bd_arc, bd_sd = m - r0, arc, sd
    if bd >= 2.5 and bd_sd <= 2.0:
        basin_cands.append((d, bd, bd_arc))
basins = []
for d, depth, arc in sorted(basin_cands, key=lambda x: -x[1]):
    if all(ang_dist(d, b[0]) >= math.degrees(20.0 / 150.0) for b in basins):
        basins.append((d, depth, arc))
results["craters"] = {
    "placed": len(probe_craters),
    "expected_range": [6, 12],
    "confirmed_in_field": sum(1 for c in crater_checks if c["pass"]),
    "all_confirmed": all(c["pass"] for c in crater_checks),
    "count_in_range": 6 <= len(probe_craters) <= 12,
    "spawn_hidden": len(spawn_hidden),
    "spawn_hidden_dirs": [[round(x, 3) for x in d] for d in spawn_hidden],
    "independent_basins_detected": len(basins),
    "basins": [{"dir": [round(x, 3) for x in d], "depth_m": round(dp, 1), "rim_arc_m": a}
               for d, dp, a in basins],
    "per_crater": crater_checks,
    "pass": 6 <= len(probe_craters) <= 12 and all(c["pass"] for c in crater_checks),
}

# --- 5. landmarks at specified radii, >= 60 deg apart.
# For each probe landmark direction, measure the FIELD signature expected by
# the GDD and compare.
landmark_checks = {}

# spire: peak 188 m at its dir (cone 6 deg)
sd = probe_lms["spire"]["dir"]
smax = max(sample_radius(d) for d in lattice if ang_dist(d, sd) <= 6.0)
landmark_checks["spire"] = {
    "placed_dir": [round(x, 3) for x in sd],
    "field_max_in_cone_m": round(smax, 2), "expected_m": 188,
    "pass": smax >= 184.0,
}

# twinpeaks: two peaks 180/176, 45 m apart, around placed dir (cone 12 deg)
td = probe_lms["twinpeaks"]["dir"]
tpeak = max(sample_radius(d) for d in lattice if ang_dist(d, td) <= 12.0)
landmark_checks["twinpeaks"] = {
    "placed_dir": [round(x, 3) for x in td],
    "field_max_in_cone_m": round(tpeak, 2), "expected_m": [180, 176],
    "pass": tpeak >= 174.0,
}

# mesa: flat top 172 m at its dir (cone 5 deg)
md = probe_lms["mesa"]["dir"]
mmax = max(sample_radius(d) for d in lattice if ang_dist(d, md) <= 5.0)
landmark_checks["mesa"] = {
    "placed_dir": [round(x, 3) for x in md],
    "field_max_in_cone_m": round(mmax, 2), "expected_m": 172,
    "pass": mmax >= 169.0,
}

# greatcrater: floor 132 / rim 170, dia 120 m, at its dir
gd = probe_lms["greatcrater"]["dir"]
gfloor = sample_radius(gd)
gring_m, gring_mx, _ = ring_stats(gd, 60.0)
landmark_checks["greatcrater"] = {
    "placed_dir": [round(x, 3) for x in gd],
    "field_center_m": round(gfloor, 2), "field_rim_m": round(gring_mx, 2),
    "expected_m": {"floor": 132, "rim": 170},
    "pass": 128 <= gfloor <= 136 and 165 <= gring_mx <= 174,
}

# notch: channel floor 138 m through its dir
nd = probe_lms["notch"]["dir"]
nfloor = sample_radius(nd)
landmark_checks["notch"] = {
    "placed_dir": [round(x, 3) for x in nd],
    "field_center_m": round(nfloor, 2), "expected_m": {"floor": 138, "walls": 170},
    "pass": 134 <= nfloor <= 142,
}

# beacon: peak 165 m, 54 m from spawn toward +X (dir fixed, no jitter;
# 54 m = GDD 50 m + the generator's band-slope offset)
bd = norm3([54 / 150, 1, 0])
bmax = max(sample_radius(d) for d in lattice if ang_dist(d, bd) <= 3.0)
landmark_checks["beacon"] = {
    "placed_dir": [round(x, 3) for x in probe_lms["beacon"]["dir"]],
    "field_max_in_cone_m": round(bmax, 2), "expected_m": 165,
    "pass": 160 <= bmax <= 168,
}

# field global extremes (diagnostic)
actual_max_r, actual_max_d = -1.0, None
for d in lattice:
    r = sample_radius(d)
    if r > actual_max_r:
        actual_max_r, actual_max_d = r, d
actual_min_r, actual_min_d = 999.0, None
for d in lattice:
    r = sample_radius(d)
    if r < actual_min_r:
        actual_min_r, actual_min_d = r, d

# separation of the six placed landmark dirs
dirs6 = [probe_lms[k]["dir"] for k in ("spire", "twinpeaks", "mesa", "greatcrater", "notch", "beacon")]
min_sep = min(ang_dist(dirs6[i], dirs6[j]) for i in range(6) for j in range(i + 1, 6))
results["landmarks"] = {
    **landmark_checks,
    "all_present_at_specified_locations": all(v["pass"] for v in landmark_checks.values()),
    "min_separation_placed_deg": round(min_sep, 1),
    "probe_min_sep_deg": probe["report"]["landmark_min_sep_deg"],
    "min_sep_required_deg": 60.0,
    "separation_pass": min_sep >= 60.0,
    "field_global_max": {"r_m": round(actual_max_r, 2), "dir": [round(x, 3) for x in actual_max_d]},
    "field_global_min": {"r_m": round(actual_min_r, 2), "dir": [round(x, 3) for x in actual_min_d]},
    "pass": all(v["pass"] for v in landmark_checks.values()) and min_sep >= 60.0,
}

# --- 6. visibility: >= 60% of sampled surface points see a landmark.
# GDD model (pure cap geometry, no raymarching): a surface point p sees a
# landmark whose peak sits at absolute radius r (= R + H) in direction d iff
#     ang_dist(p, d) <= acos(R/r) + acos(R/(R+eye)),  R = 150 m, eye = 1.7 m.
# Six caps: twin peaks counted once, at the pair centre (the GDD's own 66.5%
# accounting). Bowl features (great crater, notch) use the RIM height — the
# part of the feature that is visible — measured as the ring max at 60 m.
# Sampled on a uniform Fibonacci sphere: the gnomonic face grid thins out
# near face corners, which biases the cap-union fraction down by ~7 points.
EYE = 1.7
R_PLANET = 150.0


def cap_half_angle_deg(r_m):
    return math.degrees(math.acos(R_PLANET / r_m)
                        + math.acos(R_PLANET / (R_PLANET + EYE)))


def fib_sphere(n):
    ga = math.pi * (3.0 - math.sqrt(5.0))
    pts = []
    for i in range(n):
        z = 1.0 - (2.0 * i + 1.0) / n
        r = math.sqrt(max(0.0, 1.0 - z * z))
        phi = ga * i
        pts.append([math.cos(phi) * r, z, math.sin(phi) * r])
    return pts


nring_m, nring_mx, _ = ring_stats(nd, 60.0)  # notch rim (max, not mean)
cap_targets = [
    ("spire", sd, smax),
    ("twinpeaks", td, tpeak),
    ("mesa", md, mmax),
    ("greatcrater", gd, gring_mx),
    ("notch", nd, nring_mx),
    ("beacon", bd, bmax),
]
caps = [(nm, d, h, cap_half_angle_deg(h)) for nm, d, h in cap_targets]
fib = fib_sphere(100000)
vis = 0
for p in fib:
    for _nm, d, _h, half in caps:
        if ang_dist(p, d) <= half:
            vis += 1
            break
vis_frac = vis / len(fib)
results["visibility"] = {
    "model": "gdd 6-cap: ang_dist <= acos(R/(R+H)) + acos(R/(R+eye)), H = r-150, eye=1.7 m",
    "samples": len(fib), "visible": vis, "fraction": round(vis_frac, 4),
    "targets": [{"name": nm, "dir": [round(x, 3) for x in d], "r_m": round(h, 2),
                 "half_angle_deg": round(half, 2)} for nm, d, h, half in caps],
    "threshold": 0.60, "pass": vis_frac >= 0.60,
}

# ---------------------------------------------------------------- report
results["world"] = {"file": WORLD, "face_grid": G, "bounds": [RMIN, RMAX],
                    "probe_file": PROBE, "probe_seed": probe.get("seed")}
overall = (results["decoder_crosscheck"]["pass"] and results["radii"]["pass"]
           and results["walkable"]["pass"] and results["spawn_disc"]["pass"]
           and results["spawn_outward"]["pass"] and results["craters"]["pass"]
           and results["landmarks"]["pass"] and results["visibility"]["pass"])
results["OVERALL"] = "PASS" if overall else "FAIL"

print(json.dumps(results, indent=2))
