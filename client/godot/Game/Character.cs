// Bags (B) and the character sheet (C).
//
// Both panels, the shop, and the HUD all want the same three facts — credits,
// what you are carrying, what you are holding — so they are kept HERE, once,
// and everything reads them from this object. The alternative is each panel
// parsing its own copy out of whatever cmd_result it happened to see, which is
// how two views of the same thing start disagreeing.
//
// The state is refreshed by the `inventory` cmd, which returns all of it in
// one reply. The equipped weapon is the exception: it arrives on the
// `equipped` event instead, because the server broadcasts that on every change
// and replays it at join, so it is already correct without asking.

using System;
using SpaceAdventure.Net;
using Newtonsoft.Json;

namespace SpaceAdventure.Game
{
    /// <summary>Credits, inventory and equipment: one copy, read by everything.</summary>
    public sealed class Character
    {
        /// <summary>items.json inv_slots.</summary>
        public const int InventorySlots = 20;

        public int Credits { get; private set; } = -1;
        public ItemStack[] Inventory { get; private set; }

        /// <summary>The primary-slot item, from the `equipped` event. Empty when unarmed.</summary>
        /// <summary>Every worn slot, slot → item id (Phase 11.7). The server's map, verbatim.</summary>
        public readonly System.Collections.Generic.Dictionary<string, string> Equipped =
            new System.Collections.Generic.Dictionary<string, string>();

        /// <summary>The weapon in hand: the primary slot, the one slot the rig and HUD care about.</summary>
        public string Primary
        {
            get => Equipped.TryGetValue("primary", out string p) ? p : "";
            set { if (string.IsNullOrEmpty(value)) Equipped.Remove("primary"); else Equipped["primary"] = value; }
        }

        /// <summary>The hand weapon (slot melee), or "".</summary>
        public string Melee => Worn("melee");

        /// <summary>Tab's choice: the melee weapon in hand rather than the gun.</summary>
        public bool WieldMelee { get; set; }

        /// <summary>
        /// What is in the hand: the melee weapon when asked for (or no gun is
        /// worn), else the gun. The server's heldItem, mirrored.
        /// </summary>
        public string Held =>
            WieldMelee && Melee != "" ? Melee : Primary != "" ? Primary : Melee;

        /// <summary>The `wield` cmd: which weapon goes in the hand.</summary>
        public static byte[] WieldCmd(ushort seq, bool melee) =>
            Encode.Cmd(seq, Op.Wield, melee ? "{\"slot\":\"melee\"}" : "{\"slot\":\"primary\"}");

        /// <summary>
        /// Our own `equipped` event: what the server says we hold. A melee
        /// item means the melee hand; anything else is the gun (and is the
        /// join replay's only word on it before the inventory arrives).
        /// </summary>
        public void OnHeld(string item)
        {
            if (!string.IsNullOrEmpty(item) && Defs.Item(item)?.Melee != null)
            {
                Equipped["melee"] = item;
                WieldMelee = true;
                return;
            }
            WieldMelee = false;
            if (!string.IsNullOrEmpty(item)) Primary = item;
        }

        /// <summary>Phase 21: our own `skin` / `suit` palette ids, from the `worn` frames ("" = as baked).</summary>
        public string Skin { get; set; } = "";
        public string Suit { get; set; } = "";

        /// <summary>The item worn in a slot, or "".</summary>
        public string Worn(string slot) => Equipped.TryGetValue(slot, out string p) ? p : "";

        /// <summary>Which slot holds this item, or "".</summary>
        public string SlotHolding(string item)
        {
            foreach (var kv in Equipped) if (kv.Value == item) return kv.Key;
            return "";
        }

        /// <summary>The `defs` blob, for names and slots.</summary>
        public Defs Defs { get; set; } = Defs.Empty;

        public ushort Health { get; set; }

        public int UsedSlots => Inventory?.Length ?? 0;

        /// <summary>How many of an item the bag holds (Phase 13: hotbar counts).</summary>
        public int Count(string item)
        {
            int n = 0;
            if (Inventory != null) foreach (var st in Inventory) if (st.item == item) n += st.qty;
            return n;
        }

        /// <summary>Phase 13: the `use` cmd for a consumable or a worn ability.</summary>
        public static byte[] UseCmd(ushort seq, string item) => Encode.Cmd(seq, Op.Use, $"{{\"item\":\"{item}\"}}");

        /// <summary>Rounds in the magazine, and carried ammunition.</summary>
        public int Magazine { get; private set; } = -1;
        public int Reserve { get; private set; } = -1;

        /// <summary>Sends a reload. The server moves carried ammo into the magazine.</summary>
        public static byte[] ReloadCmd(ushort seq) => Encode.Cmd(seq, Op.Reload, "{}");

        /// <summary>Takes the magazine and reserve out of a reload result.</summary>
        public void OnReload(string body)
        {
            var a = JsonConvert.DeserializeObject<AmmoResult>(body);
            if (a == null) return;
            Magazine = a.magazine;
            Reserve = a.reserve;
        }

        /// <summary>
        /// One round left the magazine. Driven by our own `shot_fired`, not by
        /// the trigger: the server drops shots for cadence, an empty magazine
        /// or no weapon, and counting trigger pulls would drift below the real
        /// count every time it did.
        /// </summary>
        public void OnShotFired()
        {
            if (Magazine > 0) Magazine--;
        }

        /// <summary>Asks the server for credits and inventory.</summary>
        public static byte[] RefreshCmd(ushort seq) => Encode.Cmd(seq, Op.Inventory, "{}");

        /// <summary>The item an equip was last requested for, pending its result.</summary>

        /// <summary>Builds an equip cmd and remembers what it asked for.</summary>
        public byte[] EquipCmd(ushort seq, string slot, string item)
        {
            return Encode.Cmd(seq, Op.Equip, $"{{\"slot\":\"{slot}\",\"item\":\"{item}\"}}");
        }

        /// <summary>
        /// Applies an accepted equip.
        ///
        /// The `equipped` event cannot carry this on its own: the server
        /// broadcasts on CHANGE, so equipping what you already have equipped
        /// is correctly silent — and that is exactly the case where a client
        /// that never learned the state has no other way to find out. An
        /// accepted result is the server agreeing, so it counts.
        /// </summary>
        /// <summary>An empty item clears the slot (PROTOCOL: equip).</summary>
        public byte[] UnequipCmd(ushort seq, string slot) => EquipCmd(seq, slot, "");

        /// <summary>The equip result carries the whole worn map; take it verbatim.</summary>
        public void OnEquipResult(string body)
        {
            var r = JsonConvert.DeserializeObject<EquipResult>(body ?? "");
            if (r?.equipped == null) return;
            string before = Primary;
            Equipped.Clear();
            foreach (var kv in r.equipped) Equipped[kv.Key] = kv.Value;
            if (Primary != before) Magazine = -1; // a new gun: full on its first shot
            SyncAmmo();
        }
        private class EquipResult { public System.Collections.Generic.Dictionary<string, string> equipped { get; set; } }

        /// <summary>
        /// Takes credits and inventory out of any reply that carries them —
        /// `inventory` and `shop_buy` both do.
        /// </summary>
        public void OnWallet(string body)
        {
            var w = JsonConvert.DeserializeObject<WalletResult>(body);
            if (w == null) return;
            if (w.credits >= 0) Credits = w.credits; // a craft reply carries no credits
            if (w.inventory != null) Inventory = w.inventory;
            if (w.equipped != null)
            {
                Equipped.Clear();
                foreach (var kv in w.equipped) Equipped[kv.Key] = kv.Value;
            }
            SyncAmmo();
        }

        /// <summary>
        /// The ammo box from the bag: the reserve is the carried ammo, and a
        /// magazine the server has not reported yet (join, or an equip) is
        /// full — the server hands a freshly equipped weapon a full magazine
        /// on its first shot (fireLocked), so this is the number it will use.
        /// </summary>
        public void SyncAmmo()
        {
            ItemDef weapon = Defs.Item(Primary);
            if (weapon?.Weapon == null) { Magazine = -1; Reserve = -1; return; }
            Reserve = Count(weapon.Weapon.AmmoItem);
            if (Magazine < 0) Magazine = weapon.Weapon.Magazine + (Defs.Item(Worn("mod"))?.Mod?.Magazine ?? 0);
        }

    }
}
