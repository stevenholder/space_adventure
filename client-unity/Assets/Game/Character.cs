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
using UnityEngine;

namespace SpaceAdventure.Game
{
    /// <summary>Credits, inventory and equipment: one copy, read by everything.</summary>
    public sealed class Character
    {
        /// <summary>items.json inv_slots.</summary>
        public const int InventorySlots = 20;

        private GUIStyle _style, _heading, _dim;
        private Texture2D _panel;

        public int Credits { get; private set; } = -1;
        public ItemStack[] Inventory { get; private set; }

        /// <summary>The primary-slot item, from the `equipped` event. Empty when unarmed.</summary>
        public string Primary { get; set; } = "";

        /// <summary>The `defs` blob, for names and slots.</summary>
        public string Defs { get; set; } = "";

        public ushort Health { get; set; }

        public bool BagsOpen { get; private set; }
        public bool SheetOpen { get; private set; }
        public bool AnyOpen => BagsOpen || SheetOpen;

        public int UsedSlots => Inventory?.Length ?? 0;

        public void ToggleBags() { BagsOpen = !BagsOpen; if (BagsOpen) SheetOpen = false; }
        public void ToggleSheet() { SheetOpen = !SheetOpen; if (SheetOpen) BagsOpen = false; }
        public void CloseAll() { BagsOpen = false; SheetOpen = false; }

        /// <summary>Rounds in the magazine, and carried ammunition.</summary>
        public int Magazine { get; private set; } = -1;
        public int Reserve { get; private set; } = -1;

        /// <summary>Sends a reload. The server moves carried ammo into the magazine.</summary>
        public static byte[] ReloadCmd(ushort seq) => Encode.Cmd(seq, Op.Reload, "{}");

        /// <summary>Takes the magazine and reserve out of a reload result.</summary>
        public void OnReload(string body)
        {
            var a = JsonUtility.FromJson<AmmoResult>(body);
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
            var w = JsonUtility.FromJson<WalletResult>(body);
            if (w == null) return;
            Credits = w.credits;
            if (w.inventory != null) Inventory = w.inventory;
        }

        /// <summary>Draws whichever panel is open. Returns a cmd to send, or null.</summary>
        public byte[] Draw(Func<ushort> nextSeq)
        {
            if (!AnyOpen) return null;
            EnsureStyles();
            return BagsOpen ? DrawBags(nextSeq) : DrawSheet();
        }

        private byte[] DrawBags(Func<ushort> nextSeq)
        {
            byte[] send = null;
            int rows = Mathf.Max(UsedSlots, 1);
            float w = 420, h = 96 + rows * 24 + 40;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.DrawTexture(rect, _panel);

            float y = rect.y + 12;
            GUI.Label(new Rect(rect.x + 16, y, w, 22), "BAGS", _heading);
            y += 26;
            GUI.Label(new Rect(rect.x + 16, y, w, 20),
                $"{UsedSlots} of {InventorySlots} slots used · {(Credits < 0 ? "—" : Credits.ToString())} credits",
                _style);
            y += 24;

            if (UsedSlots == 0)
            {
                GUI.Label(new Rect(rect.x + 26, y, w - 42, 20), "empty", _dim);
            }

            for (int i = 0; i < UsedSlots; i++)
            {
                ItemStack it = Inventory[i];
                string slot = ItemDefs.SlotOf(Defs, it.item);
                bool held = !string.IsNullOrEmpty(slot) && it.item == Primary;

                GUI.Label(new Rect(rect.x + 26, y, w - 150, 20),
                    $"{ItemDefs.NameOf(Defs, it.item)}   x{it.qty}", held ? _heading : _style);

                // Only equippable things get a button, and the one already in
                // your hands gets a label instead — a button that re-equips
                // what you are holding does nothing and reads as broken.
                if (!string.IsNullOrEmpty(slot))
                {
                    var btn = new Rect(rect.xMax - 116, y - 2, 96, 22);
                    if (held)
                    {
                        GUI.Label(btn, "  equipped", _dim);
                    }
                    else if (GUI.Button(btn, $"equip {slot}"))
                    {
                        send = EquipCmd(nextSeq(), slot, it.item);
                    }
                }
                y += 24;
            }

            GUI.Label(new Rect(rect.x + 16, rect.yMax - 34, w - 32, 20), "B closes", _dim);
            return send;
        }

        private byte[] DrawSheet()
        {
            float w = 380, h = 210;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.DrawTexture(rect, _panel);

            float y = rect.y + 12;
            GUI.Label(new Rect(rect.x + 16, y, w, 22), "CHARACTER", _heading);
            y += 30;

            GUI.Label(new Rect(rect.x + 16, y, 120, 20), "health", _dim);
            GUI.Label(new Rect(rect.x + 130, y, w, 20), Health.ToString(), _style);
            y += 22;
            GUI.Label(new Rect(rect.x + 16, y, 120, 20), "credits", _dim);
            GUI.Label(new Rect(rect.x + 130, y, w, 20), Credits < 0 ? "—" : Credits.ToString(), _style);
            y += 22;
            GUI.Label(new Rect(rect.x + 16, y, 120, 20), "carrying", _dim);
            GUI.Label(new Rect(rect.x + 130, y, w, 20), $"{UsedSlots} / {InventorySlots} slots", _style);
            y += 30;

            GUI.Label(new Rect(rect.x + 16, y, w, 20), "EQUIPMENT", _heading);
            y += 24;
            GUI.Label(new Rect(rect.x + 16, y, 120, 20), "primary", _dim);
            GUI.Label(new Rect(rect.x + 130, y, w, 20),
                string.IsNullOrEmpty(Primary) ? "— empty —" : ItemDefs.NameOf(Defs, Primary), _style);

            GUI.Label(new Rect(rect.x + 16, rect.yMax - 32, w - 32, 20), "C closes · B for bags", _dim);
            return null;
        }

        private void EnsureStyles()
        {
            if (_panel != null) return;
            _panel = new Texture2D(1, 1);
            _panel.SetPixel(0, 0, new Color(0.03f, 0.04f, 0.06f, 0.9f));
            _panel.Apply();
            _style = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _style.normal.textColor = new Color(0.92f, 0.94f, 1f);
            _heading = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _heading.normal.textColor = new Color(0.95f, 0.86f, 0.55f);
            _dim = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _dim.normal.textColor = new Color(0.60f, 0.64f, 0.72f);
        }
    }
}
