#!/usr/bin/env python3
"""server/data/recipes.json from items.json and the rule table craft.json.

    python3 tools/gen_recipes.py           # rewrite recipes.json
    python3 tools/gen_recipes.py --check   # exit 1 (with a diff) if it is stale

Phase 22 (GDD "The refinery -- make everything, from nothing"): every item
past the raws is the output of exactly one recipe, and nobody writes those
recipes by hand. craft.json holds the tree (`materials`), one base shape per
item kind (`shapes`), the rarity ladder (`gates`, `scale`) and the per-id
exceptions (`items`); this script is the only thing that writes recipes.json,
and defs/recipes_test.go refuses a server whose copy differs.

Rules, in order, for an item that is not in `skip`:
  material   -> its row in `materials` (explicit station/skill/level/seconds)
  else       -> a shape by kind: weapon/primary (pistol or rifle by class),
                weapon/melee (1h or 2h by `melee.hands`), armor by slot
                (head/hands/feet small, chest/legs big, back), tool, gadget,
                mod, consumable (throw.*, medkit, potion.*), ammo, accessory;
                `items[id].shape` overrides.
  scaled     -> every shape input count and the seconds x scale[rarity];
                an epic adds `epic_core` mat.core; `items[id].extra` inputs
                are added unscaled (a tool that eats its predecessor).
  level      -> gates[rarity] (materials carry their own)
  xp         -> ceil(value x output qty / xp_divisor), or `items[id].xp`
  id         -> `items[id].id` or "recipe.<item id>"
Stdlib only; deterministic; item order is items.json's.
"""
import json
import math
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, "server", "data")


def shape_for(item, rules):
    kind, slot, iid = item["kind"], item.get("slot"), item["id"]
    forced = rules["items"].get(iid, {}).get("shape")
    if forced:
        return forced
    if kind == "weapon" and slot == "primary":
        return "weapon.pistol" if (item.get("weapon") or {}).get("class") == "pistol" else "weapon.rifle"
    if kind == "weapon" and slot == "melee":
        return "melee.2h" if (item.get("melee") or {}).get("hands") == 2 else "melee.1h"
    if kind == "armor":
        return {"head": "armor.small", "hands": "armor.small", "feet": "armor.small",
                "chest": "armor.big", "legs": "armor.big", "back": "armor.back"}[slot]
    if kind in ("tool", "gadget", "mod", "ammo", "accessory"):
        return kind
    if kind == "consumable":
        if iid.startswith("throw."):
            return "throwable"
        if iid.endswith(".medkit"):
            return "medkit"
        if ".potion." in iid:
            return "potion"
    raise SystemExit(f"gen_recipes: no rule for {iid} (kind {kind}, slot {slot}); add a shape or skip it")


def generate():
    items = json.load(open(os.path.join(DATA, "items.json"), encoding="utf-8"))["items"]
    rules = json.load(open(os.path.join(DATA, "craft.json"), encoding="utf-8"))
    by_id = {i["id"]: i for i in items}
    for mid in [m["output"] for m in rules["materials"]] + list(rules["items"]) + rules["skip"]:
        if mid not in by_id:
            raise SystemExit(f"gen_recipes: craft.json names {mid}, not in items.json")
    mats = {m["output"]: m for m in rules["materials"]}
    out = []
    for item in items:
        iid = item["id"]
        if iid in rules["skip"]:
            continue
        exc = rules["items"].get(iid, {})
        rid = exc.get("id", "recipe." + iid)
        if iid in mats:
            m = mats[iid]
            inputs, qty, station, skill, level, seconds = m["inputs"], m.get("qty", 1), m["station"], m["skill"], m["level"], m["seconds"]
        else:
            shape = rules["shapes"][shape_for(item, rules)]
            k = rules["scale"][item["rarity"]]
            inputs = {i: n * k for i, n in shape["inputs"].items()}
            if item["rarity"] == "epic":
                inputs["mat.core"] = inputs.get("mat.core", 0) + rules["epic_core"]
            qty, station, skill = shape.get("qty", 1), shape["station"], shape["skill"]
            level, seconds = rules["gates"][item["rarity"]], shape["seconds"] * k
        for i, n in exc.get("extra", {}).items():
            inputs[i] = inputs.get(i, 0) + n
        for i in inputs:
            if i not in by_id:
                raise SystemExit(f"gen_recipes: {rid} wants {i}, not in items.json")
        xp = exc.get("xp")
        if xp is None:
            if "value" not in item:
                raise SystemExit(f"gen_recipes: {iid} has no value and no xp exception")
            xp = math.ceil(item["value"] * qty / rules["xp_divisor"])
        out.append({
            "id": rid, "name": item["name"], "level": level, "skill": skill, "station": station,
            "seconds": seconds,
            "inputs": [{"item": i, "qty": n} for i, n in inputs.items()],
            "output": {"item": iid, "qty": qty},
            "xp": xp,
        })
    return {
        "$comment": "GENERATED by tools/gen_recipes.py from items.json + craft.json (Phase 22). Do not edit: change craft.json and regenerate; defs/recipes_test.go checks.",
        "version": 2,
        "recipes": out,
    }


def main():
    path = os.path.join(DATA, "recipes.json")
    text = json.dumps(generate(), indent=2, ensure_ascii=False) + "\n"
    if "--check" in sys.argv:
        have = open(path, encoding="utf-8").read() if os.path.exists(path) else ""
        if have != text:
            import difflib
            sys.stdout.writelines(difflib.unified_diff(have.splitlines(True), text.splitlines(True), "recipes.json", "generated"))
            sys.exit(1)
        print(f"recipes.json up to date ({len(generate()['recipes'])} recipes)")
        return
    open(path, "w", encoding="utf-8").write(text)
    print(f"wrote {path}: {len(generate()['recipes'])} recipes")


if __name__ == "__main__":
    main()
