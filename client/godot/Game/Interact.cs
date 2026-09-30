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
using Godot;
using Newtonsoft.Json;

namespace SpaceAdventure.Game
{
    internal class StockEntry { public string item { get; set; } public int price { get; set; } }
    internal class ShopStock { public StockEntry[] stock { get; set; } }
    public class ItemStack { public string item; public int qty; }
    public class WalletResult { public int credits = -1; public ItemStack[] inventory; public System.Collections.Generic.Dictionary<string, string> equipped; }
    public class AmmoResult { public int magazine; public int reserve; }

    /// <summary>Look-at targeting, the E prompt, and the shop panel.</summary>
    public sealed class Interaction
    {
        /// <summary>GDD "Interaction": interact_dist.</summary>
        private const float InteractDist = 3.0f;

        /// <summary>cos(interact_cone), interact_cone = 20 degrees half-angle.</summary>
        private static readonly float ConeCosMin = Mathf.Cos(20f * Mathf.Pi / 180f);

        private const float EyeHeight = 1.7f;

        private readonly EntityViews _views;

        private readonly Character _character;

        private uint _shopNpc;
        private StockEntry[] _stock;
        private string _status = "";

        // Phase 8: the UI Toolkit shop view reads state from here and builds
        // the SAME cmd bytes the IMGUI panel did (t14 stays byte-identical).
        internal StockEntry[] Stock => _stock;
        public string Status => _status;

        /// <summary>Phase 12: sell qty of item at the open shop (mirror of BuyCmd).</summary>
        public byte[] SellCmd(ushort seq, string item, int qty)
        {
            _status = "selling...";
            return Encode.Cmd(seq, Op.ShopSell,
                $"{{\"npc\":{_shopNpc},\"item\":\"{item}\",\"qty\":{qty}}}");
        }

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
        /// Picks the look-at target. `eye` and `look` are in world (Sim) space.
        /// </summary>
        public void Update(Vector3 eye, Vector3 look)
        {
            Target = 0;
            Prompt = "";
            float best = ConeCosMin;

            TargetType = 0;
            foreach (EntityView v in _views.All)
            {
                if (v.Root == null || !v.Root.Visible) continue;
                if (v.Type != EntityType.Npc && v.Type != EntityType.Loot &&
                    v.Type != EntityType.Vehicle && v.Type != EntityType.Ship &&
                    v.Type != EntityType.Player && v.Type != EntityType.Node) continue;
                if (v.Type == EntityType.Player && v.Id == _selfId) continue;

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
                Vector3 targetPos = v.Root.GlobalPosition;
                // A node is a metre of rock and the bench a slab at 0.9 m:
                // aim at those, not 1.7 m over them (server cmd.go aimHeight).
                float aim = v.Type == EntityType.Node ? 0.6f
                    : v.Type == EntityType.Npc && v.Label == "npc.workbench" ? 0.9f
                    : EyeHeight;
                Vector3 targetEye = targetPos + targetPos.Normalized() * aim;
                Vector3 to = targetEye - eye;
                float d = to.Length();
                // A vehicle is boarded from board_dist (GDD, 8 m), not
                // conversation range — the server measures the same 8 m.
                float maxDist = v.Type == EntityType.Vehicle || v.Type == EntityType.Ship
                    ? BoardDist : InteractDist;
                if (d > maxDist || d < 1e-4f) continue;

                float dot = look.Dot(to / d);
                if (dot < best) continue;

                best = dot;
                Target = v.Id;
                TargetType = v.Type;
                Prompt = v.Type switch
                {
                    EntityType.Loot => $"{InteractKey}  ·  pick up",
                    EntityType.Vehicle => $"{InteractKey}  ·  drive",
                    EntityType.Ship => $"{InteractKey}  ·  fly",
                    EntityType.Player => $"{InteractKey}  ·  invite {v.Label} to party",
                    EntityType.Node => NodePrompt(v),
                    _ => $"{InteractKey}  ·  {Verb(v.Label)} {Nice(v.Label)}",
                };
            }
        }

        /// <summary>Own entity id, so the cone never offers self-invites.</summary>
        public uint _selfId;

        /// <summary>Phase 13: the key label that holds `interact` on the hotbar; the prompt reads it.</summary>
        public string InteractKey = "E";

        private string Nice(string def)
        {
            if (string.IsNullOrEmpty(def)) return "them";
            if (_character.Defs.Npcs != null && _character.Defs.Npcs.TryGetValue(def, out var n) && !string.IsNullOrEmpty(n.Name)) return n.Name;
            return def switch
            {
                "npc.quartermaster" => "Quartermaster Vex",
                "npc.dispatcher" => "Dispatcher Oru",
                _ => def,
            };
        }

        /// <summary>The NPC's own verb from defs ("talk to", "use"), lower-cased.</summary>
        private string Verb(string def)
        {
            string v = _character.Defs.Npcs != null && _character.Defs.Npcs.TryGetValue(def ?? "", out var n) ? n.Verb : "";
            v = (v ?? "").Trim().ToLowerInvariant();
            return v == "" || v == "talk" ? "talk to" : v;
        }

        /// <summary>
        /// Phase 12: the node's verb from its skill, with the reason it will
        /// refuse shown up front — a bar that never starts needs no round trip
        /// to explain itself. The server re-checks all of it.
        /// </summary>
        private string NodePrompt(EntityView v)
        {
            var nd = _character.Defs.Node(v.Label);
            string verb = nd?.Skill == "salvaging" ? "cut" : "drill";
            if (v.Depleted) return $"{Nice(nd?.Name ?? v.Label)}  ·  depleted";
            if (nd != null && !_character.Defs.ToolSatisfies(_character.Worn("tool"), nd.Tool))
                return $"{InteractKey}  ·  {verb}  ·  needs {_character.Defs.ItemName(nd.Tool)}";
            return $"{InteractKey}  ·  {verb} {nd?.Name?.ToLowerInvariant() ?? v.Label}";
        }

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
                    _stock = JsonConvert.DeserializeObject<ShopStock>(r.Body)?.stock;
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

    }
}
