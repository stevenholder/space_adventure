#!/usr/bin/env -S blender -b --python
"""Pose probe for the first-person arm clips (body.py FP table).

    blender -b --python tools/bpy/probe_fp.py -- ads        (run from art/)
    blender -b --python tools/bpy/probe_fp.py -- lower|unarmed|aim

Builds the body, sweeps the right arm's euler angles against a wrist target
and barrel direction, then sweeps the left arm so its wrist lands on the
fore-end (0.28 m down the barrel from the right wrist), and prints the best
eight as PROBE lines. Numbers are in the body frame: x right, +Y FRONT, Z
up, eye at 1.70, shoulders at (±0.22, 0, 1.45). Reach is 0.48 m from the
shoulder, so expect a residual: the fore-end comes to the left hand, not
the reverse. Bone x swings a down-pointing bone forward for positive
values; y on the raised arm swings it inward.
"""
import sys

import bpy
from mathutils import Vector

src = open("tools/bpy/body.py").read().replace('if __name__ == "__main__":', "if False:")
exec(src)  # noqa: S102 -- the body builder is the thing under test

POSE = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else "ads"
TARGET = {
    # wrist.r, barrel direction, fore-end distance
    "aim": (Vector((0.21, 0.28, 1.24)), Vector((0, 1, 0.2)).normalized(), 0.28),
    "ads": (Vector((0.04, 0.24, 1.63)), Vector((0, 1, 0)), 0.28),
    "lower": (Vector((0.20, 0.25, 1.10)), Vector((0, 0.7, -0.7)).normalized(), 0.28),
    "unarmed": (Vector((0.22, 0.20, 1.05)), Vector((0, 1, -0.3)).normalized(), 0.0),
}[POSE]

bpy.ops.wm.read_factory_settings(use_empty=True)
ACTIVE.update(palette=dict(PALETTE), bulk=1.0, shoulders=1.0, race="human")
rig = build()
rig.animation_data.action = None
for pb in rig.pose.bones:
    pb.rotation_euler = (0, 0, 0)


def wrist(bone):
    bpy.context.view_layer.update()
    pb = rig.pose.bones[bone]
    m = rig.matrix_world @ pb.matrix
    return m @ Vector((0, pb.length, 0)), (m.to_3x3() @ Vector((0, 1, 0))).normalized()


def grid(lo, hi, step):
    v = lo
    while v <= hi:
        yield v
        v += step


want, want_dir, fore = TARGET
right = []
for ax in grid(0, 90, 10):
    for az in grid(-20, 60, 10):
        for fx in grid(0, 100, 10):
            for fz in grid(0, 70, 10):
                rig.pose.bones["arm.r"].rotation_euler = deg(ax, 0, az)
                rig.pose.bones["forearm.r"].rotation_euler = deg(fx, 0, fz)
                w, d = wrist("forearm.r")
                right.append(((w - want).length + 0.3 * (d - want_dir).length, (ax, 0, az), (fx, 0, fz), w, d))
right.sort(key=lambda r: r[0])
best = []
for score, ar, fr, w, d in right[:4]:
    rig.pose.bones["arm.r"].rotation_euler = deg(*ar)
    rig.pose.bones["forearm.r"].rotation_euler = deg(*fr)
    target_l = w + d * fore
    for ax in grid(20, 90, 10):
        for ay in grid(-20, 60, 10):
            for fx in grid(0, 70, 10):
                rig.pose.bones["arm.l"].rotation_euler = deg(ax, ay, 0)
                rig.pose.bones["forearm.l"].rotation_euler = deg(fx, 0, 0)
                wl, _ = wrist("forearm.l")
                gap = (wl - target_l).length if fore else (wl - Vector((-want.x, want.y, want.z))).length
                best.append((score + gap, ar, fr, (ax, ay, 0), (fx, 0, 0), w, d, wl))
best.sort(key=lambda b: b[0])
for s, ar, fr, al, fl, w, d, wl in best[:8]:
    f = lambda v: tuple(round(x, 2) for x in v)
    print(f"PROBE {POSE} score={s:.2f} arm.r={ar} forearm.r={fr} arm.l={al} forearm.l={fl} wrist.r={f(w)} dir={f(d)} wrist.l={f(wl)}")
