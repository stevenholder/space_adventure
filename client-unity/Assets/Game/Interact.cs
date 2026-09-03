// Looking at things, and the shop that opens when you press E.
//
// The candidate rule is the SERVER's rule, copied from GDD "Interaction" and
// server/internal/server/cmd.go: within interact_dist of the eye, inside the
// look cone, and among those the largest dot product wins — the thing you are
// most directly looking at, not the nearest. Nearest picks the wrong target
// when two NPCs stand together.
//
// It is duplicated here on purpose, and only to decide what the PROMPT says.
// The server re-checks range and cone on every cmd and refuses with
// out_of_range, so this copy can never grant anything; the worst a drift
// between the two can do is offer a prompt that then gets refused. Sending a
// cmd the server would reject is not an exploit, it is a wasted round trip.

using System;
using SpaceAdventure.Net;
using SpaceAdventure.Sim;
using UnityEngine;

namespace SpaceAdventure.Game
{
    [Serializable] internal class StockEntry { public string item; public int price; }
    [Serializable] internal class ShopStock { public StockEntry[] stock; }
    [Serializable] public class ItemStack { public string item; public int qty; }
    [Serializable] public class WalletResult { public int credits; public ItemStack[] inventory; }
    [Serializable] public class AmmoResult { public int magazine; public int reserve; }

    /// <summary>Look-at targeting, the E prompt, and the shop panel.</summary>
    public sealed class Interaction
    {
        /// <summary>GDD "Interaction": interact_dist.</summary>
        private const float InteractDist = 3.0f;

        /// <summary>cos(interact_cone), interact_cone = 20 degrees half-angle.</summary>
        private static readonly float ConeCosMin = Mathf.Cos(20f * Mathf.Deg2Rad);

        private const float EyeHeight = 1.7f;

        private readonly EntityViews _views;

        private GUIStyle _style, _heading;
        private Texture2D _panel;

        private readonly Character _character;

        private uint _shopNpc;
        private StockEntry[] _stock;
        private string _status = "";

        // Phase 8: the UI Toolkit shop view reads state from here and builds
        // the SAME cmd bytes the IMGUI panel did (t14 stays byte-identical).
        internal StockEntry[] Stock => _stock;
        public string Status => _status;

        public byte[] BuyCmd(ushort seq, string item, int price)
        {
            _lastBought = item;
            _status = "buying...";
            return Encode.Cmd(seq, Op.ShopBuy,
                $"{{\"npc\":{_shopNpc},\"item\":\"{item}\",\"qty\":1}}");
        }

        public Interaction(EntityViews views, Character character)
        {
            _views = views;
            _character = character;
        }

        /// <summary>The entity the player is looking at, or 0.</summary>
        public uint Target { get; private set; }

        /// <summary>The current target's EntityType, 0 when none.</summary>
        public ushort TargetType { get; private set; }

        /// <summary>
        /// Overrides the cone prompt when set (seated exit hint, seat_result
        /// refusals). Owned by Boot; cleared by Boot when it stops applying.
        /// </summary>
        public string Notice = "";

        /// <summary>board_dist (GDD "Seats and occupancy").</summary>
        private const float BoardDist = 8f;

        /// <summary>What the prompt should say, or empty when there is nothing to say.</summary>
        public string Prompt { get; private set; } = "";

        public bool ShopOpen => _stock != null;

        public void CloseShop()
        {
            _stock = null;
            _shopNpc = 0;
            _status = "";
        }

        /// <summary>
        /// Picks the look-at target. `eye` and `look` are in Unity space.
        /// </summary>
        public void Update(Vector3 eye, Vector3 look)
        {
            Target = 0;
            Prompt = "";
            float best = ConeCosMin;

            TargetType = 0;
            foreach (EntityView v in _views.All)
            {
                if (v.Root == null || !v.Root.activeSelf) continue;
                if (v.Type != EntityType.Npc && v.Type != EntityType.Loot &&
                    v.Type != EntityType.Vehicle && v.Type != EntityType.Ship) continue;

                // A corpse is not a conversation. This used to be implied by
                // activeSelf -- a dead body was switched off, so it fell out of
                // the loop for free -- and stopped being implied the moment
                // bodies started staying up to play a death animation. Then a
                // dead grunt lying on top of the loot it dropped won the cone
                // test against the crate, and the prompt offered to talk to it
                // instead of picking the loot up.
                if (v.Dead) continue;

                // Aim at the target's EYE, not its feet. The server does the
                // same, and against a 1.8 m body at 2 m the difference is most
                // of the cone.
                Vector3 targetPos = v.Root.transform.position;
                Vector3 targetEye = targetPos + targetPos.normalized * EyeHeight;
                Vector3 to = targetEye - eye;
                float d = to.magnitude;
                // A vehicle is boarded from board_dist (GDD, 8 m), not
                // conversation range — the server measures the same 8 m.
                float maxDist = v.Type == EntityType.Vehicle || v.Type == EntityType.Ship
                    ? BoardDist : InteractDist;
                if (d > maxDist || d < 1e-4f) continue;

                float dot = Vector3.Dot(look, to / d);
                if (dot < best) continue;

                best = dot;
                Target = v.Id;
                TargetType = v.Type;
                Prompt = v.Type switch
                {
                    EntityType.Loot => "E  ·  pick up",
                    EntityType.Vehicle => "E  ·  drive",
                    EntityType.Ship => "E  ·  fly",
                    _ => $"E  ·  talk to {Nice(v.Label)}",
                };
            }
        }

        private static string Nice(string def) => def switch
        {
            "npc.quartermaster" => "Quartermaster Vex",
            "" or null => "them",
            _ => def,
        };

        /// <summary>Opens the shop on the current target. Returns the cmd to send, or null.</summary>
        public byte[] OpenShop(ushort seq)
        {
            if (Target == 0) return null;
            _shopNpc = Target;
            _status = "asking...";
            return Encode.Cmd(seq, Op.ShopList, $"{{\"npc\":{Target}}}");
        }

        /// <summary>
        /// Asks what we are carrying. Sent alongside shop_list so the panel
        /// knows the wallet before the first purchase — otherwise every item
        /// looks affordable until one is refused.
        /// </summary>
        public static byte[] InventoryCmd(ushort seq) => Encode.Cmd(seq, Op.Inventory, "{}");

        /// <summary>Feeds a cmd_result back in. Returns a follow-up cmd, or null.</summary>
        public byte[] OnCmdResult(CmdResult r, Func<ushort> nextSeq)
        {
            if (!r.Ok)
            {
                _status = Explain(r.Body);
                return null;
            }

            switch (r.Opcode)
            {
                case Op.ShopList:
                    _stock = JsonUtility.FromJson<ShopStock>(r.Body)?.stock;
                    _status = _stock == null || _stock.Length == 0 ? "nothing for sale" : "";
                    return null;

                case Op.ShopBuy:
                {
                    _character.OnWallet(r.Body);
                    _status = $"bought {_character.Defs.ItemName(_lastBought)}";

                    // Equip only what CAN be equipped. Auto-equipping whatever
                    // was just bought sent ammunition to the primary slot and
                    // the server answered wrong_slot, so a perfectly good
                    // purchase reported an error.
                    //
                    // Equipping at all is worth doing: buying a rifle that
                    // leaves your hands empty reads as a shop that failed.
                    string slot = _character.Defs.SlotOf(_lastBought);
                    if (string.IsNullOrEmpty(slot)) return null;
                    // Through Character, so the accepted result updates what
                    // the client believes it is holding.
                    return _character.EquipCmd(nextSeq(), slot, _lastBought);
                }

                case Op.Inventory:
                    _character.OnWallet(r.Body);
                    return null;

                case Op.Equip:
                    _status = "equipped";
                    return null;

                default:
                    return null;
            }
        }

        private string _lastBought = "";

        /// <summary>
        /// The server's refusal reasons, in words a player can act on.
        /// "no_space" in particular is not obvious: weapon.pulse has
        /// stack_max 1, so every rifle bought takes a fresh slot out of 20.
        /// </summary>
        private static string Explain(string body) => body switch
        {
            var b when b.Contains("no_ammo") => "no ammunition left to load",
            var b when b.Contains("no_space") => "inventory full — 20 slots, and a rifle takes one each",
            var b when b.Contains("insufficient_credits") => "not enough credits",
            var b when b.Contains("wrong_slot") => "that does not go in this slot",
            var b when b.Contains("out_of_range") => "too far away",
            var b when b.Contains("not_owned") => "you do not have one",
            var b when b.Contains("unknown_item") => "no such item",
            _ => body,
        };

        /// <summary>Draws the prompt and, when open, the shop. Returns a cmd to send, or null.</summary>
        public byte[] Draw(Func<ushort> nextSeq)
        {
            EnsureStyles();
            int credits = _character.Credits;

            if (!ShopOpen)
            {
                // Notice outranks the cone prompt: it is either the seated
                // "E · exit rover" hint or a seat_result refusal, both set by
                // Boot, both more current than what the cone test saw.
                string text = string.IsNullOrEmpty(Notice) ? Prompt : Notice;
                if (string.IsNullOrEmpty(text)) return null;
                var size = _style.CalcSize(new GUIContent(text));
                float w = size.x + 24, h = 26;
                var at = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.62f, w, h);
                GUI.DrawTexture(at, _panel);
                GUI.Label(new Rect(at.x + 12, at.y + 4, w, h), text, _style);
                return null;
            }

            byte[] send = null;
            ItemStack[] inventory = _character.Inventory;
            int carried = _character.UsedSlots;
            float panelW = 400;
            float panelH = 84 + _stock.Length * 26 + 26 + carried * 20 + 46;
            var rect = new Rect((Screen.width - panelW) * 0.5f, (Screen.height - panelH) * 0.5f, panelW, panelH);
            GUI.DrawTexture(rect, _panel);

            float y = rect.y + 10;
            GUI.Label(new Rect(rect.x + 16, y, panelW, 22), "QUARTERMASTER VEX", _heading);
            y += 24;
            GUI.Label(new Rect(rect.x + 16, y, panelW, 20),
                credits >= 0 ? $"credits: {credits}" : "credits: —", _style);
            y += 26;

            for (int i = 0; i < _stock.Length; i++)
            {
                StockEntry e = _stock[i];
                var row = new Rect(rect.x + 16, y, panelW - 32, 22);
                GUI.enabled = credits < 0 || credits >= e.price;
                if (GUI.Button(row, $"{_character.Defs.ItemName(e.item)}   —   {e.price} cr"))
                {
                    _lastBought = e.item;
                    _status = "buying...";
                    send = Encode.Cmd(nextSeq(), Op.ShopBuy,
                        $"{{\"npc\":{_shopNpc},\"item\":\"{e.item}\",\"qty\":1}}");
                }
                GUI.enabled = true;
                y += 26;
            }

            // What you are carrying, and how full the pack is. Without this a
            // refusal for "no space" is a mystery: nothing on screen ever said
            // how many of the twenty slots were gone, or that a second rifle
            // costs a whole slot because it does not stack.
            y += 6;
            GUI.Label(new Rect(rect.x + 16, y, panelW - 32, 20),
                $"carrying  ({carried}/{Character.InventorySlots} slots)   ·   B for bags", _style);
            y += 20;
            if (inventory != null)
            {
                foreach (ItemStack it in inventory)
                {
                    GUI.Label(new Rect(rect.x + 26, y, panelW - 42, 18),
                        $"{_character.Defs.ItemName(it.item)}  x{it.qty}", _style);
                    y += 20;
                }
            }

            GUI.Label(new Rect(rect.x + 16, rect.yMax - 42, panelW - 32, 20), _status, _style);
            if (GUI.Button(new Rect(rect.xMax - 90, rect.yMax - 32, 74, 24), "close")) CloseShop();
            return send;
        }

        private void EnsureStyles()
        {
            if (_panel != null) return;
            _panel = new Texture2D(1, 1);
            _panel.SetPixel(0, 0, new Color(0.03f, 0.04f, 0.06f, 0.88f));
            _panel.Apply();
            _style = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _style.normal.textColor = new Color(0.92f, 0.94f, 1f);
            _heading = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _heading.normal.textColor = new Color(0.95f, 0.86f, 0.55f);
        }
    }
}
