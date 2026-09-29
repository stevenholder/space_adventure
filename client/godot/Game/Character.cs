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
        public string Primary { get; set; } = "";

        /// <summary>The `defs` blob, for names and slots.</summary>
        public Defs Defs { get; set; } = Defs.Empty;

        public ushort Health { get; set; }

        public int UsedSlots => Inventory?.Length ?? 0;

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
        private string _pendingEquip;

        /// <summary>Builds an equip cmd and remembers what it asked for.</summary>
        public byte[] EquipCmd(ushort seq, string slot, string item)
        {
            _pendingEquip = item;
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
        public void OnEquipAccepted()
        {
            if (!string.IsNullOrEmpty(_pendingEquip)) Primary = _pendingEquip;
            _pendingEquip = null;
        }

        /// <summary>
        /// Takes credits and inventory out of any reply that carries them —
        /// `inventory` and `shop_buy` both do.
        /// </summary>
        public void OnWallet(string body)
        {
            var w = JsonConvert.DeserializeObject<WalletResult>(body);
            if (w == null) return;
            Credits = w.credits;
            if (w.inventory != null) Inventory = w.inventory;
        }

    }
}
