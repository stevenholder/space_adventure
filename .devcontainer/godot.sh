#!/bin/sh
# /usr/local/bin/godot in the dev image; GODOT points here.
# With a display (the x11/ config) or --headless this is Godot, untouched.
# With neither, a windowed run gets a virtual screen, so a rig shot
# (-uiShot, fp-sheet) works the same in Codespaces as on a desktop.
bin=$(echo /opt/godot/Godot_v*_mono_linux.x86_64)
if [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then exec "$bin" "$@"; fi
for a in "$@"; do [ "$a" = "--headless" ] && exec "$bin" "$@"; done
exec xvfb-run -a -s "-screen 0 2560x1440x24" "$bin" "$@"
