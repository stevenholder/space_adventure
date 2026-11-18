# The development bench (ROADMAP Phase 26). Build context is the repo root, so
# the versions are READ from the files that already pin them:
#   Go     server/go.mod
#   Godot  client/.godot-version
# .NET and Node come from devcontainer features (devcontainer.json), pinned as
# ci.yml pins them. Blender and kind are the two versions written here.
FROM mcr.microsoft.com/devcontainers/base:ubuntu-24.04

ARG BLENDER_VERSION=4.5.14
ARG KIND_VERSION=v0.33.0

# Godot and Blender both draw through Mesa. llvmpipe (software GL) is the
# renderer every rig shot has been taken on, so no GPU is required; xvfb is the
# display when the host offers none (Codespaces).
RUN apt-get update && export DEBIAN_FRONTEND=noninteractive && apt-get install -y --no-install-recommends \
      ca-certificates curl unzip xz-utils make python3 ffmpeg postgresql-client gawk \
      xvfb xauth libgl1 libgl1-mesa-dri libegl1 libgles2 libglx-mesa0 mesa-utils \
      libx11-6 libxcursor1 libxinerama1 libxrandr2 libxi6 libxext6 libxrender1 libxfixes3 \
      libxxf86vm1 libxkbcommon0 libsm6 libice6 libwayland-client0 libwayland-cursor0 libwayland-egl1 \
      libasound2t64 libpulse0 libfontconfig1 libfreetype6 libudev1 libdbus-1-3 \
    && rm -rf /var/lib/apt/lists/*

# Go, at the version server/go.mod names.
COPY server/go.mod /tmp/pins/go.mod
RUN v="$(awk '$1 == "go" { print $2 }' /tmp/pins/go.mod)" \
    && curl -fsSL "https://go.dev/dl/go${v}.linux-amd64.tar.gz" | tar -xz -C /usr/local
ENV PATH=/usr/local/go/bin:$PATH

# Godot (.NET build), at the version client/.godot-version names. GodotSharp/
# must stay beside the binary, so /usr/local/bin/godot is a wrapper, not a
# symlink (godot.sh: it also supplies xvfb when there is no display). GODOT
# points at it; godot-cli and fp_sheet.sh take it from there.
COPY client/.godot-version /tmp/pins/godot-version
RUN v="$(tr -d '[:space:]' < /tmp/pins/godot-version)" \
    && curl -fsSL -o /tmp/godot.zip "https://github.com/godotengine/godot-builds/releases/download/${v}/Godot_v${v}_mono_linux_x86_64.zip" \
    && unzip -q /tmp/godot.zip -d /opt && mv "/opt/Godot_v${v}_mono_linux_x86_64" /opt/godot && rm /tmp/godot.zip
COPY --chmod=755 .devcontainer/godot.sh /usr/local/bin/godot
ENV GODOT=/usr/local/bin/godot

# Blender: the art pipeline (art/package.json reads BLENDER).
RUN curl -fsSL "https://download.blender.org/release/Blender${BLENDER_VERSION%.*}/blender-${BLENDER_VERSION}-linux-x64.tar.xz" | tar -xJ -C /opt \
    && mv "/opt/blender-${BLENDER_VERSION}-linux-x64" /opt/blender
ENV BLENDER=/opt/blender/blender

RUN curl -fsSL -o /usr/local/bin/kind "https://kind.sigs.k8s.io/dl/${KIND_VERSION}/kind-linux-amd64" && chmod +x /usr/local/bin/kind

# Export templates where godot-cli looks for them (same unpacking as
# `godot-cli templates`), trimmed to the two presets the project exports:
# Linux and Windows x86_64. The full set is 1.9 GB, most of it mobile.
USER vscode
RUN v="$(tr -d '[:space:]' < /tmp/pins/godot-version)" \
    && t="$HOME/.local/share/godot/export_templates/${v%-*}.${v#*-}.mono" \
    && curl -fsSL -o /tmp/t.tpz "https://github.com/godotengine/godot-builds/releases/download/${v}/Godot_v${v}_mono_export_templates.tpz" \
    && mkdir -p "$t" /tmp/t && unzip -q /tmp/t.tpz -d /tmp/t \
    && mv /tmp/t/templates/version.txt /tmp/t/templates/icudt_godot.dat /tmp/t/templates/linux_*.x86_64 /tmp/t/templates/windows_*_x86_64*.exe "$t/" \
    && rm -rf /tmp/t /tmp/t.tpz

# MPFB (MakeHuman for Blender): art/tools/bpy/human.py builds every body from
# it. Pinned by the hash in its extensions.blender.org URL, as human.py's
# header names the version.
ARG MPFB_VERSION=2.0.17
ARG MPFB_SHA256=4f0a879d64a39bf646fbf5f53601ac678855da329d650617dca5737548239a87
RUN curl -fsSL -o /tmp/mpfb.zip "https://extensions.blender.org/download/sha256:${MPFB_SHA256}/add-on-mpfb-v${MPFB_VERSION}.zip" \
    && echo "${MPFB_SHA256}  /tmp/mpfb.zip" | sha256sum -c - \
    && /opt/blender/blender --command extension install-file -r user_default -e /tmp/mpfb.zip \
    && rm /tmp/mpfb.zip
