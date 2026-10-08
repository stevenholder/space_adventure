// Phase 22 (GDD "The refinery"): the recipe list every station draws — the
// relay's bench, the scrapyard forge, and the hands (the backpack's CRAFT
// tab). The RULES are static and engine-free so -selftest can hold them
// without a server; CraftList is the dumb view that both panels embed.
//
// The server re-checks everything (level, inputs, station, range); this copy
// only decides what is greyed and why. A row greyed wrongly costs a refusal
// round trip, never a grant.

using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using SpaceAdventure.Net;
using Action = System.Action;

namespace SpaceAdventure.Game.UI
{
    /// <summary>The station filter, the greying verdict, the bar text.</summary>
    public static class CraftRules
    {
        public const int MaxQty = 10;

        /// <summary>A recipe's station; an older server sends none, which is Phase 12's bench.</summary>
        public static string StationOf(RecipeDef r) => string.IsNullOrEmpty(r?.Station) ? "bench" : r.Station;

        /// <summary>A recipe's skill; an older server sends none, which is Phase 12's Engineering.</summary>
        public static string SkillOf(RecipeDef r) => string.IsNullOrEmpty(r?.Skill) ? "engineering" : r.Skill;

        /// <summary>
        /// What station an NPC archetype is: its `kind` when defs carry it,
        /// else the two world stations by id. "" for anyone who is not one.
        /// </summary>
        public static string StationOfNpc(Defs defs, string archetype)
        {
            string kind = defs?.Npcs != null && archetype != null && defs.Npcs.TryGetValue(archetype, out NpcDef d) ? d?.Kind ?? "" : "";
            if (kind == "bench" || kind == "forge") return kind;
            return archetype switch
            {
                "npc.workbench" => "bench",
                "npc.forge" => "forge",
                _ => "",
            };
        }

        /// <summary>The recipes made at `station`, in file order.</summary>
        public static List<RecipeDef> For(IEnumerable<RecipeDef> recipes, string station)
        {
            var list = new List<RecipeDef>();
            if (recipes == null) return list;
            foreach (RecipeDef r in recipes)
                if (r != null && StationOf(r) == station) list.Add(r);
            return list;
        }

        /// <summary>One input row: what the bag holds against what `qty` units need.</summary>
        public struct Need
        {
            public string Item;
            public int Have;
            public int Want;
            public bool Short => Have < Want;
        }

        /// <summary>Whether a row is live, why not, and its counts.</summary>
        public sealed class Verdict
        {
            public bool Ok;
            /// <summary>"" when Ok; else "NEEDS SMITHING 3" or "MISSING MATERIALS".</summary>
            public string Reason = "";
            public readonly List<Need> Inputs = new List<Need>();
        }

        /// <summary>
        /// The verdict for `qty` units: the level comes first (a locked row
        /// says what unlocks it), then the inputs, counted against the bag
        /// for all `qty` — the server takes each unit's inputs as it starts,
        /// and a run that cannot finish is a run the player did not ask for.
        /// </summary>
        public static Verdict Check(RecipeDef r, int qty, Func<string, int> held, Func<string, int> level, Func<string, string> skillName)
        {
            var v = new Verdict();
            qty = Math.Clamp(qty, 1, MaxQty);
            bool missing = false;
            if (r?.Inputs != null)
                foreach (ItemQtyDef input in r.Inputs)
                {
                    var n = new Need { Item = input.Item, Have = held(input.Item), Want = input.Qty * qty };
                    missing |= n.Short;
                    v.Inputs.Add(n);
                }
            string skill = SkillOf(r);
            if (level(skill) < (r?.Level ?? 0)) v.Reason = "NEEDS " + SkillLabel(skillName(skill), r.Level);
            else if (missing) v.Reason = "MISSING MATERIALS";
            v.Ok = v.Reason == "";
            return v;
        }

        /// <summary>"SMITHING 3".</summary>
        public static string SkillLabel(string name, int level) => $"{(name ?? "").ToUpperInvariant()} {level}";

        /// <summary>The channel bar while units run: "MAKING STEEL PLATE 2/4".</summary>
        public static string BarText(string itemName, int n, int m) => $"MAKING {(itemName ?? "").ToUpperInvariant()} {n}/{m}";

        /// <summary>A unit's base time as the row shows it: "6 s", "1.5 s".</summary>
        public static string Seconds(double s) => s > 0 ? s.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s" : "";

        /// <summary>The input line: "2/4 Iron Ingot · 0/2 Parts" (have/need).</summary>
        public static string InputLine(Defs defs, Verdict v)
        {
            var sb = new StringBuilder();
            foreach (Need n in v.Inputs)
            {
                if (sb.Length > 0) sb.Append("  ·  ");
                sb.Append(n.Have).Append('/').Append(n.Want).Append(' ').Append(defs != null ? defs.ItemName(n.Item) : n.Item);
            }
            return sb.ToString();
        }

        /// <summary>A skill's display name from defs, else its id.</summary>
        public static string SkillName(Defs defs, string id)
        {
            if (defs?.Skills != null)
                foreach (SkillDef s in defs.Skills)
                    if (s.Id == id && !string.IsNullOrEmpty(s.Name)) return s.Name;
            return id ?? "";
        }

        /// <summary>The `craft` cmd body; npc 0 is the hands.</summary>
        public static string CmdBody(uint npc, string recipe, int qty) =>
            $"{{\"npc\":{npc},\"recipe\":\"{recipe}\",\"qty\":{Math.Clamp(qty, 1, MaxQty)}}}";
    }

    /// <summary>
    /// The list both panels embed: a QTY stepper over recipe cards, each
    /// with the output, the skill and level, the seconds, the inputs as
    /// have/need, and CRAFT or the reason it is greyed.
    /// </summary>
    public sealed class CraftList
    {
        private readonly Character _character;
        private readonly SkillSheet _skills;
        private readonly Icons _icons;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        /// <summary>Units per CRAFT, 1–10, kept across rebuilds.</summary>
        public int Qty = 1;

        /// <summary>Called after CRAFT sends (the panel rebuilds or closes).</summary>
        public Action<RecipeDef, int> Crafted;

        public CraftList(Character character, SkillSheet skills, Icons icons, Func<ushort> nextSeq, Action<byte[]> send)
        {
            _character = character;
            _skills = skills;
            _icons = icons;
            _nextSeq = nextSeq;
            _send = send;
        }

        /// <summary>How many of an item the bag holds, across stacks.</summary>
        public int Held(string item)
        {
            int n = 0;
            ItemStack[] inv = _character.Inventory;
            if (inv == null) return 0;
            foreach (ItemStack s in inv)
                if (s != null && s.item == item) n += s.qty;
            return n;
        }

        public CraftRules.Verdict Verdict(RecipeDef r) =>
            CraftRules.Check(r, Qty, Held, _skills.Level, id => CraftRules.SkillName(_character.Defs, id));

        /// <summary>Sends `craft` for a recipe by id when its row is live; false (with nothing sent) otherwise. The rig's press.</summary>
        public bool Press(uint npc, string recipeId, out string why)
        {
            why = "";
            RecipeDef r = _character.Defs.Recipes?.Find(x => x.Id == recipeId);
            if (r == null) { why = "unknown recipe"; return false; }
            CraftRules.Verdict v = Verdict(r);
            if (!v.Ok) { why = v.Reason; return false; }
            _send(Encode.Cmd(_nextSeq(), Op.Craft, CraftRules.CmdBody(npc, r.Id, Qty)));
            Crafted?.Invoke(r, Qty);
            return true;
        }

        /// <summary>Draws the stepper and the `station`'s rows into body; `rebuild` redraws the host panel.</summary>
        public void Fill(VBoxContainer body, string station, uint npc, Action rebuild)
        {
            Defs defs = _character.Defs;
            var head = Styles.Row(6);
            head.AddChild(Styles.Grow(Styles.Display_(station == "hand" ? "by hand  ·  nothing in range needed" : $"{station} recipes", 12, Styles.Dust)));
            head.AddChild(Styles.Display_("QTY", 12, Styles.Dust));
            head.AddChild(Styles.Button("−", false, () => { Qty = Math.Max(1, Qty - 1); rebuild(); }));
            var qtyLabel = Styles.Display_(Qty.ToString(), 14, Styles.Amber);
            qtyLabel.CustomMinimumSize = new Vector2(22, 0);
            qtyLabel.HorizontalAlignment = HorizontalAlignment.Center;
            head.AddChild(qtyLabel);
            head.AddChild(Styles.Button("+", false, () => { Qty = Math.Min(CraftRules.MaxQty, Qty + 1); rebuild(); }));
            body.AddChild(head);
            body.AddChild(Styles.Gap(4));

            List<RecipeDef> rows = CraftRules.For(defs.Recipes, station);
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 400), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var list = Styles.Column(4);
            list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(list);
            if (rows.Count == 0)
                list.AddChild(Styles.Display_(station == "hand" ? "nothing to make by hand" : "nothing to make here", 12, Styles.Dust));
            foreach (RecipeDef r in rows)
            {
                string id = r.Id;
                string output = r.Output?.Item ?? "";
                int outQty = r.Output?.Qty ?? 0;
                CraftRules.Verdict v = Verdict(r);
                string skill = CraftRules.SkillOf(r);
                bool locked = _skills.Level(skill) < r.Level;

                var slot = new ItemSlot { Defs = defs, Icons = _icons, Static = true, Item = output, Qty = outQty };
                var meta = Styles.Column(0);
                var level = Styles.Display_(CraftRules.SkillLabel(CraftRules.SkillName(defs, skill), r.Level), 12, locked ? Styles.Danger : Styles.Amber);
                level.HorizontalAlignment = HorizontalAlignment.Right;
                meta.AddChild(level);
                var secs = Styles.Display_(CraftRules.Seconds(r.Seconds), 12, Styles.Dust);
                secs.HorizontalAlignment = HorizontalAlignment.Right;
                meta.AddChild(secs);
                meta.CustomMinimumSize = new Vector2(110, 0);

                Control act = v.Ok
                    ? Styles.Button("CRAFT", false, () =>
                    {
                        _send(Encode.Cmd(_nextSeq(), Op.Craft, CraftRules.CmdBody(npc, id, Qty)));
                        Crafted?.Invoke(r, Qty);
                    })
                    : Styles.Display_(v.Reason, 11, Styles.Danger);
                if (act is Label why)
                {
                    why.CustomMinimumSize = new Vector2(84, 0);
                    why.AutowrapMode = TextServer.AutowrapMode.Word;
                    why.HorizontalAlignment = HorizontalAlignment.Center;
                }

                Color rarity = Styles.Rarity(defs.ItemRarity(output));
                string title = outQty > 1 ? $"{r.Name}  ×{outQty}" : r.Name;
                PanelContainer card = Styles.Card(v.Ok ? rarity : Styles.Dust, slot, title, v.Ok ? rarity : Styles.Dust, CraftRules.InputLine(defs, v), meta, act);
                if (!v.Ok) card.Modulate = new Color(1, 1, 1, 0.72f);
                list.AddChild(card);
            }
            body.AddChild(scroll);
        }
    }
}
