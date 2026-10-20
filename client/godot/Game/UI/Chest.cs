// Phase 23: placeable stations. StructureRules is the engine-free part (cmd
// bodies, result parsing) the -selftest pins; ChestView is the chest panel —
// the bag grid beside the chest grid, a whole stack moving across on a drag
// or a right-click. DUMB like the other panels: it hands finished cmd bytes
// to Send; Boot feeds it the chest stacks from each chest_put/chest_take
// result and closes it when you walk away.

using System;
using System.Collections.Generic;
using Godot;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SpaceAdventure.Net;

namespace SpaceAdventure.Game.UI
{
    /// <summary>Phase 23's wire bodies and result shapes, kept out of the views.</summary>
    public static class StructureRules
    {
        /// <summary>A chest holds 20 stacks (GDD "Chest").</summary>
        public const int ChestSlots = 20;

        /// <summary>Hold F this long on your own structure and it is a pick-up, not a use.</summary>
        public const double HoldSeconds = 0.35;

        public static string PlaceBody(string item) => JsonConvert.SerializeObject(new { item = item ?? "" });

        public static byte[] PlaceCmd(ushort seq, string item)
        {
            LastPlaceItem = item ?? "";
            return Encode.Cmd(seq, Op.Place, PlaceBody(item));
        }

        /// <summary>Phase 24: the item the last place cmd named, so its refusal can be worded for a site.</summary>
        public static string LastPlaceItem = "";

        /// <summary>`chest_put` / `chest_take`: {"structure","item","qty"}.</summary>
        public static string MoveBody(uint structure, string item, int qty) =>
            JsonConvert.SerializeObject(new { structure, item = item ?? "", qty });

        /// <summary>A whole stack, bag -> chest (put) or chest -> bag (take).</summary>
        public static byte[] MoveCmd(ushort seq, bool put, uint structure, string item, int qty) =>
            Encode.Cmd(seq, put ? Op.ChestPut : Op.ChestTake, MoveBody(structure, item, qty));

        /// <summary>
        /// Opening the panel asks for the contents. The contract has no list
        /// op; this is a `chest_take` of nothing (qty 0), which the server
        /// answers with both inventories. INTEGRATION: confirm with server.
        /// </summary>
        public static byte[] PeekCmd(ushort seq, uint structure) =>
            Encode.Cmd(seq, Op.ChestTake, MoveBody(structure, "", 0));

        public static string PickUpBody(uint structure) => JsonConvert.SerializeObject(new { structure });

        public static byte[] PickUpCmd(ushort seq, uint structure) => Encode.Cmd(seq, Op.PickUp, PickUpBody(structure));

        /// <summary>The `chest` array of a chest_put/chest_take body; null when absent or unreadable.</summary>
        public static List<ItemStack> ParseChest(string body)
        {
            try
            {
                var o = JObject.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
                if (o["chest"] is not JArray a) return null;
                var list = new List<ItemStack>();
                foreach (JToken t in a)
                {
                    string item = (string)t["item"] ?? "";
                    int qty = (int?)t["qty"] ?? 0;
                    if (item != "" && qty > 0) list.Add(new ItemStack { item = item, qty = qty });
                }
                return list;
            }
            catch (JsonException) { }
            catch (FormatException) { }
            catch (InvalidCastException) { }
            catch (ArgumentException) { }
            return null;
        }

        /// <summary>The pick-up channel's length from its reply ({"seconds"}); 1.5 when absent.</summary>
        public static double PickUpSeconds(string body)
        {
            try { return (double?)JObject.Parse(string.IsNullOrEmpty(body) ? "{}" : body)["seconds"] ?? 1.5; }
            catch (JsonException) { }
            catch (FormatException) { }
            catch (InvalidCastException) { }
            catch (ArgumentException) { }
            return 1.5;
        }

        /// <summary>pick_up_end: {structure, ok, inventory} or {structure, ok:false, reason}. Fails soft.</summary>
        public static (uint Structure, bool Ok, string Reason) ParsePickUpEnd(string json)
        {
            try
            {
                var o = JObject.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
                return ((uint?)o["structure"] ?? 0, (bool?)o["ok"] ?? false, (string)o["reason"] ?? "");
            }
            catch (JsonException) { }
            catch (FormatException) { }
            catch (InvalidCastException) { }
            catch (ArgumentException) { }
            catch (OverflowException) { }
            return (0, false, "");
        }
    }

    /// <summary>The chest: BAG grid left, CHEST grid right; drag or right-click moves a stack across.</summary>
    public sealed class ChestView : ModalView
    {
        /// <summary>The chest's entity id; Boot sets it before Show.</summary>
        public uint Chest;
        /// <summary>Its contents as the last result said; null until one arrives.</summary>
        public List<ItemStack> Stacks;
        /// <summary>A refusal or notice line from Boot.</summary>
        public string Status = "";

        private readonly Character _character;
        private readonly Icons _icons;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public ChestView(Control root, Character character, Icons icons, Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Chest", 2 * (5 * (ItemSlot.Cell + 6)) + 48, 0.18f, 0.5f)
        {
            _character = character;
            _icons = icons;
            _nextSeq = nextSeq;
            _send = send;
        }

        /// <summary>Opens on a chest: clears the old contents and asks for these.</summary>
        public void OpenOn(uint chest)
        {
            Chest = chest;
            Stacks = null;
            Status = "";
            Show(true);
            _send(StructureRules.PeekCmd(_nextSeq(), chest));
        }

        /// <summary>A result's chest array; redraws when open.</summary>
        public void OnChest(List<ItemStack> stacks)
        {
            if (stacks == null) return;
            Stacks = stacks;
            if (Open) Rebuild();
        }

        /// <summary>Sends one whole stack across; true when a cmd went out.</summary>
        public bool Move(bool put, string item, int qty)
        {
            if (Chest == 0 || string.IsNullOrEmpty(item) || qty <= 0) return false;
            _send(StructureRules.MoveCmd(_nextSeq(), put, Chest, item, qty));
            return true;
        }

        protected override void Fill(VBoxContainer body)
        {
            var cols = Styles.Row(24);
            cols.AddChild(Column("BAG", $"{_character.UsedSlots} / {Character.InventorySlots}", "bag", BagStacks(), Character.InventorySlots));
            cols.AddChild(Column("CHEST", Stacks == null ? "…" : $"{Stacks.Count} / {StructureRules.ChestSlots}", "chest",
                Stacks ?? new List<ItemStack>(), StructureRules.ChestSlots));
            body.AddChild(cols);
            body.AddChild(Styles.Gap(4));
            if (!string.IsNullOrEmpty(Status)) Line(body, Status, Styles.Amber, 12);
            Line(body, "drag or right-click moves a whole stack  ·  F or walking away closes", Styles.Dust, 11);
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

        private Control Column(string title, string count, string pool, List<ItemStack> stacks, int slots)
        {
            var col = Styles.Column(4);
            var head = Styles.Row(8);
            head.AddChild(Styles.Grow(Styles.Display_(title, 14, Styles.Cream)));
            head.AddChild(Styles.Display_(count, 12, Styles.Dust));
            col.AddChild(head);
            var grid = new GridContainer { Columns = 5 };
            grid.AddThemeConstantOverride("h_separation", 6);
            grid.AddThemeConstantOverride("v_separation", 6);
            bool put = pool == "bag"; // a bag stack goes INTO the chest
            for (int i = 0; i < slots; i++)
            {
                var cell = new ItemSlot { Defs = _character.Defs, Icons = _icons, Pool = pool };
                if (i < stacks.Count) { cell.Item = stacks[i].item; cell.Qty = stacks[i].qty; }
                string item = cell.Item;
                int qty = cell.Qty;
                cell.OnAlt = () => Move(put, item, qty);
                // A drop lands on the OTHER grid: dropped on the chest = put.
                cell.OnDropStack = (dropped, n) => Move(!put, dropped, n);
                grid.AddChild(cell);
            }
            col.AddChild(grid);
            return col;
        }
    }
}
