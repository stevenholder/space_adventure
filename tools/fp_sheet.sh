#!/usr/bin/env bash
# fp_sheet.sh [outdir]: the first-person arms, every body x every hold x every
# clip, frozen at its telling moments and laid out one sheet per body.
#
# The check for docs/GDD.md "The first-person rule". Run it after touching an
# fp_* pose in art/tools/bpy/human.py, a body, a weapon's mounts, or
# ViewModel's framing; LOOK at the sheets (hands closed on the weapon, wrists
# straight, arms leaving through the bottom edge). It fails by itself only on
# what it can measure: a clip the client had to shift more than LIFT_MAX to
# frame (= authored off the view).
#
#   BODIES="char.ubc" HOLDS="weapon.sidearm" tools/fp_sheet.sh /tmp/fp    # a subset
set -u
cd "$(dirname "$0")/.."
out=${1:-/tmp/fp-sheet}; mkdir -p "$out"; out=$(cd "$out" && pwd)
BODIES=${BODIES:-char.player char.player.f char.ubc char.ubc.f}
HOLDS=${HOLDS:-unarmed weapon.pulse weapon.sidearm melee.sword melee.greatsword}
RES=${RES:-1920x1080}
PORT=${PORT:-18091}
LIFT_MAX=${LIFT_MAX:-0.05}
V=$(cat client/.godot-version)   # e.g. 4.7.2-stable
GODOT=${GODOT:-$HOME/opt/godot/${V%-*}/Godot_v${V}_mono_linux.x86_64}
[ -x "$GODOT" ] || { echo "no Godot at $GODOT (set GODOT)"; exit 1; }
FONT=$(find client/godot -name '*.ttf' -not -path '*/.godot/*' | head -1)

(cd server && go build -o "$out/server" ./cmd/server) || exit 1
"$out/server" -listen ":$PORT" > "$out/server.log" 2>&1 &
trap "kill $!" EXIT
dotnet build client/godot/SpaceAdventure.csproj --nologo -v q > "$out/build.log" 2>&1 || { tail "$out/build.log"; exit 1; }
"$GODOT" --headless --path client/godot --import > /dev/null 2>&1

for b in $BODIES; do
  rm -f "$out/$b"-*.png
  for h in $HOLDS; do
    hold=(); [ "$h" = unarmed ] || hold=(-rigHold "$h")
    # One guest, one run at a time: parallel rigs share the guest's body.
    timeout 120 "$GODOT" --path client/godot --resolution "$RES" -- -serverUrl "ws://127.0.0.1:$PORT/ws" \
      -uiShot "$out/$b-$h.png" -uiFpSheet -uiShotAfter 4 -quitAfter 30 -uiYaw 300 -rigBody "$b" "${hold[@]}" \
      > "$out/$b-$h.log" 2>&1
    grep -h '^fp: ' "$out/$b-$h.log" | sed "s/^fp: /$h /" | cut -d' ' -f1-5
  done
  # The lower 60% of each frame (the arms), labelled, six to a row.
  n=0
  for f in "$out/$b"-*-*.png; do
    [ -f "$f" ] || continue
    label=$(basename "$f" .png); label=${label#"$b"-}
    ffmpeg -loglevel error -y -i "$f" -vf "crop=iw:ih*0.6:0:ih*0.4,scale=640:-1,drawtext=fontfile=$FONT:text='$label':x=8:y=8:fontsize=20:fontcolor=white:box=1:boxcolor=black@0.6" \
      "$out/.tile-$(printf %03d $n).png"; n=$((n + 1))
  done
  [ $n -gt 0 ] && ffmpeg -loglevel error -y -i "$out/.tile-%03d.png" -vf "tile=6x$(((n + 5) / 6))" "$out/sheet-$b.png" && echo "sheet $out/sheet-$b.png ($n frames)"
  rm -f "$out"/.tile-*.png
done

bad=$(grep -h '^fp: ' "$out"/*.log | awk -v max="$LIFT_MAX" '{
  if (match($0, /lift=\(([-0-9.,]+)\)/, m)) { split(m[1], v, ","); for (i = 1; i <= 3; i++) if (v[i] > max || v[i] < -max) { print; break } } }')
grep -qh '^fp: ' "$out"/*.log || { echo "fp_sheet: FAIL, no frame was shot (see $out/*.log)"; exit 1; }
[ -z "$bad" ] && echo "fp_sheet: every clip framed within $LIFT_MAX m" || { echo "fp_sheet: FAIL, authored off the view:"; echo "$bad" | cut -c1-90; exit 1; }
