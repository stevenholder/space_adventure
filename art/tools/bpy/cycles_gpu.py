"""Cycles on the GPU when Blender can see one, else the CPU.

Under WSL2 the NVIDIA driver's user-space libraries live in /usr/lib/wsl/lib
and Blender finds them only with that directory on LD_LIBRARY_PATH -- the
package.json scripts and the README set it. OptiX is not exposed through
WSL (libnvoptix is there, discovery returns nothing); CUDA is, and an RTX
is still several times the 16-core CPU on a bake.
"""
import bpy


def cycles_device(scene):
    """Point `scene` at the best Cycles device and return "GPU" or "CPU"."""
    prefs = bpy.context.preferences.addons["cycles"].preferences
    for kind in ("OPTIX", "CUDA", "HIP", "METAL", "ONEAPI"):
        try:
            prefs.compute_device_type = kind
        except TypeError:
            continue  # this build has no such backend
        prefs.get_devices()
        gpus = [d for d in prefs.devices if d.type == kind]
        if not gpus:
            continue
        for d in prefs.devices:
            d.use = d.type == kind
        scene.cycles.device = "GPU"
        print(f"cycles: GPU ({kind}: {gpus[0].name})")
        return "GPU"
    scene.cycles.device = "CPU"
    print("cycles: CPU (no GPU device; under WSL2 is /usr/lib/wsl/lib on LD_LIBRARY_PATH?)")
    return "CPU"
