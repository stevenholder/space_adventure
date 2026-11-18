#!/usr/bin/env bash
# fetch_vendor.sh [dir ...] -- recreate the gitignored art/vendor/<dir> packs
# from art/vendor.lock.json, each from its own source (itch.io or the
# author's Google Drive). Nothing is rehosted. A dir that exists and is not
# empty is skipped. Every download is sha256-checked before it is unpacked,
# and a pack lands in art/vendor/<dir> only whole (staged, then moved).
#
#   VENDOR_DIR=/some/where art/tools/fetch_vendor.sh   # other destination
#   art/tools/fetch_vendor.sh ubc ual1                 # just these
#
# Needs curl, unzip, sha256sum, python3. Run it on demand (1.0 GB).
# (fetch runs under "|| rc=$?", where set -e is off: hence the explicit
# "|| return 1" on every step inside it.)
set -euo pipefail

art=$(cd "$(dirname "$0")/.." && pwd)
lock=$art/vendor.lock.json
dest=${VENDOR_DIR:-$art/vendor}
UA="Mozilla/5.0 (X11; Linux x86_64) curl"
mkdir -p "$dest"

# q <dir> <key>: one field of a lock row; for "files", TSV lines path, id, sha256.
q() {
  python3 -I -c '
import json, sys
row = next(r for r in json.load(open(sys.argv[1]))["packs"] if r["dir"] == sys.argv[2])
v = row.get(sys.argv[3], "")
if sys.argv[3] == "files": print("\n".join("\t".join(f) for f in v))
elif isinstance(v, dict): print(v.get("root", ""))
else: print(v)' "$lock" "$1" "$2"
}

# itch.io free download: csrf token from the game page, POST for a ~60 s
# signed CDN link, fetch it. (The download_url + ?key= route says
# "invalid key" for free packs.)
itch_get() { # <game> <upload_id> <out>
  local jar=$tmp/jar base=https://quaternius.itch.io/$1 tok url
  tok=$(curl -fsS -A "$UA" -c "$jar" -b "$jar" "$base" |
        grep -o 'name="csrf_token" value="[^"]*"' | head -1 | sed 's/.*value="//; s/"$//') || return 1
  [ -n "$tok" ] || { echo "  no csrf_token on $base" >&2; return 1; }
  url=$(curl -fsS -A "$UA" -c "$jar" -b "$jar" -X POST --data-urlencode "csrf_token=$tok" \
        "$base/file/$2?source=view_game" | python3 -I -c 'import json,sys; print(json.load(sys.stdin)["url"])') || return 1
  curl -fsSL -A "$UA" -o "$3" "$url"
}

check() { # <file> <sha256>
  local got; got=$(sha256sum "$1" | cut -d' ' -f1)
  [ "$got" = "$2" ] && return 0
  echo "  sha256 MISMATCH for $1: want $2, got $got" >&2
  grep -qs 'Quota exceeded' "$1" && echo "  (Google Drive: download quota exceeded for this file; try again later, up to 24 h)" >&2
  return 1
}

fetch() { # <dir>: download + verify + unpack into $tmp/x
  local dir=$1 kind; kind=$(q "$dir" fetch)
  mkdir "$tmp/x"
  case $kind in
    itch)
      itch_get "$(q "$dir" game)" "$(q "$dir" upload_id)" "$tmp/pack.zip" || return 1
      check "$tmp/pack.zip" "$(q "$dir" sha256)" || return 1
      unzip -q "$tmp/pack.zip" -d "$tmp/x" || return 1
      local root; root=$(q "$dir" unpack)
      if [ -n "$root" ]; then mv "$tmp/x/$root" "$tmp/y" && rm -rf "$tmp/x" && mv "$tmp/y" "$tmp/x" || return 1; fi ;;
    gdrive)
      local path id sum
      while IFS=$'\t' read -r path id sum; do
        mkdir -p "$tmp/x/$(dirname "$path")"
        curl -fsSL -A "$UA" -o "$tmp/x/$path" "https://drive.usercontent.google.com/download?id=$id&export=download&confirm=t" || return 1
        check "$tmp/x/$path" "$sum" || return 1
        sleep 1
      done < <(q "$dir" files) ;;
    manual)
      echo "  manual: $(q "$dir" reason)"; echo "  get it from $(q "$dir" source) and unpack into $dest/$dir"
      return 2 ;;
    *) echo "  unknown fetch '$kind'" >&2; return 1 ;;
  esac
}

if [ $# -gt 0 ]; then dirs=("$@")
else mapfile -t dirs < <(python3 -I -c 'import json,sys; [print(r["dir"]) for r in json.load(open(sys.argv[1]))["packs"]]' "$lock")
fi

tmp=
trap 'if [ -n "$tmp" ]; then rm -rf "$tmp"; fi' EXIT
failed=() manual=() itch_n=0
for dir in "${dirs[@]}"; do
  if [ -n "$(ls -A "$dest/$dir" 2>/dev/null)" ]; then echo "$dir: present, skipped"; continue; fi
  echo "$dir: fetching $(q "$dir" pack) ($(q "$dir" bytes) bytes) from $(q "$dir" source)"
  # itch.io answers 429 to back-to-back downloads; space them out.
  if [ "$(q "$dir" fetch)" = itch ]; then [ $itch_n -gt 0 ] && sleep 30; itch_n=$((itch_n + 1)); fi
  tmp=$(mktemp -d "$dest/.fetch-$dir.XXXXXX")
  rc=0; fetch "$dir" || rc=$?
  if [ $rc -eq 0 ]; then
    rmdir "$dest/$dir" 2>/dev/null || true
    mv "$tmp/x" "$dest/$dir"; echo "$dir: done"
  elif [ $rc -eq 2 ]; then manual+=("$dir")
  else echo "$dir: FAILED, nothing unpacked" >&2; failed+=("$dir")
  fi
  rm -rf "$tmp"; tmp=
done

[ ${#manual[@]} -gt 0 ] && echo "manual (see above): ${manual[*]}"
[ ${#failed[@]} -gt 0 ] && { echo "FAILED: ${failed[*]} -- re-run later for just these" >&2; exit 1; }
exit 0
