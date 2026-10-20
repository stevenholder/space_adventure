// Phase 24: building sites. DeliverRules is the engine-free part (the cmd
// body, the result parse, pct from the snapshot's health, the thirds the
// ghost fills by, the label and prompt words) that -selftest pins;
// DeliverView is the site's panel — the bag grid beside the bill, a
// right-click on a bag stack delivering as much of it as its line still
// wants. DUMB like Chest.cs: it hands finished cmd bytes to Send; Boot feeds
// it each `deliver` result and closes it when you walk away or the site
// becomes the building.

using System;
using System.Collections.Generic;
using Godot;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpaceAdventure.Net;

namespace SpaceAdventure.Game.UI
{
    /// <summary>Phase 24's wire body and result shape, and the site's progress rules.</summary>
    public static class DeliverRules
    {
        /// <summary>The dock prop's top-level nodes, in the order they turn solid (art contract).</summary>
        public static readonly string[] Parts = { "pad", "gantry", "beacon" };

        /// <summary>A demolish is a pick_up of a `demolish` structure; its bar reads this.</summary>
        public const string DemolishBar = "DEMOLISHING";

        /// <summary>A site is a structure whose item id starts `site.`.</summary>
        public static bool IsSite(string def) => def != null && def.StartsWith("site.", StringComparison.Ordinal);

        /// <summary>The snapshot row's `health` for a site IS its percent (PROTOCOL); clamped to 0..100.</summary>
        public static int PctOf(int health) => Math.Clamp(health, 0, 100);

        /// <summary>How many of Parts draw solid: pad at 34 %, gantry at 67 %, all three at 100.</summary>
        public static int SolidParts(int pct) => pct >= 100 ? 3 : pct >= 67 ? 2 : pct >= 34 ? 1 : 0;

        /// <summary>`deliver`: {"structure","item","qty"}.</summary>
        public static string DeliverBody(uint structure, string item, int qty) =>
            JsonConvert.SerializeObject(new { structure, item = item ?? "", qty });

        public static byte[] DeliverCmd(ushort seq, uint structure, string item, int qty) =>
            Encode.Cmd(seq, Op.Deliver, DeliverBody(structure, item, qty));

        /// <summary>Opening the panel asks for the bill: a `deliver` of qty 0 (nothing moves).</summary>
        public static byte[] PeekCmd(ushort seq, uint structure, string item) => DeliverCmd(seq, structure, item, 0);

        /// <summary>A deliver result: the REMAINING count of every bill line, and pct. Null when unreadable or no bill.</summary>
        public static (List<ItemStack> Remaining, int Pct)? ParseResult(string body)
        {
            try
            {
                var o = JObject.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                if (o["bill"] is not JArray a) return null;
                var list = new List<ItemStack>();
                foreach (JToken t in a)
                {
                    string item = (string)t["item"] ?? "";
                    if (item != "") list.Add(new ItemStack { item = item, qty = Math.Max(0, (int?)t["qty"] ?? 0) });
                }
                return (list, PctOf((int?)o["pct"] ?? 0));
            }
            catch (JsonException) { }
            catch (FormatException) { }
            catch (InvalidCastException) { }
            catch (ArgumentException) { }
            catch (OverflowException) { }
            return null;
        }

        /// <summary>How many of `item` the bill still wants; 0 when not on it (or no result yet).</summary>
        public static int Want(List<ItemStack> remaining, string item)
        {
            if (remaining == null) return 0;
            foreach (var st in remaining) if (st.item == item) return st.qty;
            return 0;
        }

        /// <summary>What a right-click on a bag stack of `have` delivers: as much as its line wants.</summary>
        public static int Qty(List<ItemStack> remaining, string item, int have) => Math.Min(Math.Max(have, 0), Want(remaining, item));

        /// <summary>The building's name: the def `becomes`, else the site's name without " Site".</summary>
        public static string BuildingName(Defs defs, string site)
        {
            ItemDef it = defs?.Item(site ?? "");
            string becomes = it?.Site?.Becomes ?? "";
            string name = becomes != "" ? defs.Item(becomes)?.Name ?? "" : "";
            if (name == "") name = (it?.Name ?? "").Replace(" Site", "");
            return name == "" ? "site" : name;
        }

        /// <summary>The label over the ghost: `DOCK 43 %`.</summary>
        public static string Label(Defs defs, string site, int pct) => $"{BuildingName(defs, site).ToUpperInvariant()} {PctOf(pct)} %";

        /// <summary>The panel title (ModalView upper-cases it): `Dock Site · 43 %`.</summary>
        public static string Title(Defs defs, string site, int pct)
        {
            string name = defs?.Item(site ?? "")?.Name ?? "";
            return $"{(name == "" ? "Site" : name)}  ·  {PctOf(pct)} %";
        }

        /// <summary>Refusals a site's deliver / place / demolish can draw, in words; "" when not one of them.</summary>
        public static string Explain(string code, bool site) => code switch
        {
            "not_wanted" => "the site does not need that",
            "not_yours" => "not yours",
            "too_many" when site => "you already have a dock",
            _ => "",
        };
    }

    /// <summary>The site: BAG grid left, BILL list right; right-click a bag stack to deliver it.</summary>
    public sealed class DeliverView : ModalView
    {
        /// <summary>The site's entity id and item id; set by OpenOn.</summary>
        public uint Site;
        public string Def = "";
        /// <summary>The bill's remaining counts as the last result said; null until one arrives.</summary>
        public List<ItemStack> Remaining;
        public int Pct;
        /// <summary>A refusal or notice line from Boot.</summary>
        public string Status = "";

        private readonly Character _character;
        private readonly Icons _icons;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public DeliverView(Control root, Character character, Icons icons, Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Dock Site", 5 * (ItemSlot.Cell + 6) + 340 + 48, 0.18f, 0.5f)
        {
            _character = character;
            _icons = icons;
            _nextSeq = nextSeq;
            _send = send;
        }

        /// <summary>The site's bill as the def has it (what is needed in all).</summary>
        private List<ItemQtyDef> Bill => _character.Defs.Item(Def)?.Site?.Bill ?? new List<ItemQtyDef>();

        /// <summary>Opens on a site: clears the old bill and peeks at this one.</summary>
        public void OpenOn(uint site, string def, int pct)
        {
            Site = site;
            Def = def ?? "";
            Remaining = null;
            Pct = pct;
            Status = "";
            Show(true);
            var bill = Bill;
            _send(DeliverRules.PeekCmd(_nextSeq(), site, bill.Count > 0 ? bill[0].Item : ""));
        }

        /// <summary>A result's bill; redraws when open.</summary>
        public void OnResult((List<ItemStack> Remaining, int Pct)? r)
        {
            if (r == null) return;
            Remaining = r.Value.Remaining;
            Pct = r.Value.Pct;
            if (Open) Rebuild();
        }

        /// <summary>Delivers as much of a bag stack as its line wants; true when a cmd went out.</summary>
        public bool Deliver(string item, int have)
        {
            if (Site == 0 || string.IsNullOrEmpty(item) || have <= 0) return false;
            int qty = Remaining == null ? have : DeliverRules.Qty(Remaining, item, have);
            if (qty <= 0)
            {
                Status = DeliverRules.Explain("not_wanted", true);
                if (Open) Rebuild();
                return false;
            }
            _send(DeliverRules.DeliverCmd(_nextSeq(), Site, item, qty));
            return true;
        }

        /// <summary>The rig's hand: right-click every bag stack the bill wants. Returns how many cmds went out.</summary>
        public int DeliverAllWanted()
        {
            int sent = 0;
            var left = new Dictionary<string, int>();
            if (Remaining != null) foreach (var st in Remaining) left[st.item] = st.qty;
            foreach (var st in BagStacks())
            {
                if (!left.TryGetValue(st.item, out int want) || want <= 0) continue;
                int qty = Math.Min(want, st.qty);
                _send(DeliverRules.DeliverCmd(_nextSeq(), Site, st.item, qty));
                left[st.item] = want - qty;
                sent++;
            }
            return sent;
        }

        protected override void Fill(VBoxContainer body)
        {
            SetTitle(DeliverRules.Title(_character.Defs, Def, Pct));
            var cols = Styles.Row(24);
            cols.AddChild(BagColumn());
            cols.AddChild(BillColumn());
            body.AddChild(cols);
            body.AddChild(Styles.Gap(4));
            if (!string.IsNullOrEmpty(Status)) Line(body, Status, Styles.Amber, 12);
            Line(body, "right-click a stack delivers what the bill wants  ·  F or walking away closes", Styles.Dust, 11);
        }

        /// <summary>The bag as the backpack shows it: the worn copy is on the doll, not here.</summary>
        private List<ItemStack> BagStacks()
        {
            var shown = new List<ItemStack>();
            if (_character.Inventory == null) return shown;
            foreach (var st in _character.Inventory)
            {
                int qty = st.qty;
                if (!string.IsNullOrEmpty(_character.SlotHolding(st.item))) qty -= 1;
                if (qty > 0) shown.Add(new ItemStack { item = st.item, qty = qty });
            }
            return shown;
        }

        private Control BagColumn()
        {
            var col = Styles.Column(4);
            var head = Styles.Row(8);
            head.AddChild(Styles.Grow(Styles.Display_("BAG", 14, Styles.Cream)));
            head.AddChild(Styles.Display_($"{_character.UsedSlots} / {Character.InventorySlots}", 12, Styles.Dust));
            col.AddChild(head);
            var grid = new GridContainer { Columns = 5 };
            grid.AddThemeConstantOverride("h_separation", 6);
            grid.AddThemeConstantOverride("v_separation", 6);
            var stacks = BagStacks();
            for (int i = 0; i < Character.InventorySlots; i++)
            {
                var cell = new ItemSlot { Defs = _character.Defs, Icons = _icons, Pool = "bag" };
                if (i < stacks.Count) { cell.Item = stacks[i].item; cell.Qty = stacks[i].qty; }
                string item = cell.Item;
                int qty = cell.Qty;
                cell.OnAlt = () => Deliver(item, qty);
                grid.AddChild(cell);
            }
            col.AddChild(grid);
            return col;
        }

        private Control BillColumn()
        {
            var col = Styles.Column(8);
            col.CustomMinimumSize = new Vector2(340, 0);
            var head = Styles.Row(8);
            head.AddChild(Styles.Grow(Styles.Display_("BILL", 14, Styles.Cream)));
            head.AddChild(Styles.Display_($"{DeliverRules.PctOf(Pct)} %", 12, Styles.Amber));
            col.AddChild(head);
            foreach (ItemQtyDef line in Bill)
            {
                int need = Math.Max(line.Qty, 0);
                int done = Remaining == null ? -1 : Math.Clamp(need - DeliverRules.Want(Remaining, line.Item), 0, need);
                var row = Styles.Row(8);
                row.AddChild(Styles.Grow(Styles.Display_(_character.Defs.ItemName(line.Item), 13, done >= need ? Styles.Good : Styles.Cream)));
                row.AddChild(Styles.Display_(done < 0 ? $"… / {need}" : $"{done} / {need}", 13, Styles.Dust));
                col.AddChild(row);
                float frac = need > 0 && done > 0 ? (float)done / need : 0f;
                col.AddChild(Styles.Progress(frac, done >= need ? Styles.Good : Styles.Amber, "", 340f));
            }
            return col;
        }
    }
}
