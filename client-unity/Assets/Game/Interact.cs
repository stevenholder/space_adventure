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
    [Serializable] internal class WalletResult { public int credits; }

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

        private uint _shopNpc;
        private StockEntry[] _stock;
        private string _status = "";
        private int _credits = -1;

        public Interaction(EntityViews views) => _views = views;

        /// <summary>The entity the player is looking at, or 0.</summary>
        public uint Target { get; private set; }

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

            foreach (EntityView v in _views.All)
            {
                if (v.Root == null || !v.Root.activeSelf) continue;
                if (v.Type != EntityType.Npc && v.Type != EntityType.Loot) continue;

                // Aim at the target's EYE, not its feet. The server does the
                // same, and against a 1.8 m body at 2 m the difference is most
                // of the cone.
                Vector3 targetPos = v.Root.transform.position;
                Vector3 targetEye = targetPos + targetPos.normalized * EyeHeight;
                Vector3 to = targetEye - eye;
                float d = to.magnitude;
                if (d > InteractDist || d < 1e-4f) continue;

                float dot = Vector3.Dot(look, to / d);
                if (dot < best) continue;

                best = dot;
                Target = v.Id;
                Prompt = v.Type == EntityType.Loot
                    ? "E  ·  pick up"
                    : $"E  ·  talk to {Nice(v.Label)}";
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
                _status = $"refused: {r.Body}";
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
                    var w = JsonUtility.FromJson<WalletResult>(r.Body);
                    if (w != null) _credits = w.credits;
                    _status = "bought";
                    // Equipping is a second command; a purchase does not put
                    // the thing in your hands, and a shop that leaves you
                    // holding nothing reads as a shop that failed.
                    return Encode.Cmd(nextSeq(), Op.Equip,
                        "{\"slot\":\"primary\",\"item\":\"" + _lastBought + "\"}");
                }

                case Op.Inventory:
                {
                    var w = JsonUtility.FromJson<WalletResult>(r.Body);
                    if (w != null) _credits = w.credits;
                    return null;
                }

                case Op.Equip:
                    _status = "equipped";
                    return null;

                default:
                    return null;
            }
        }

        private string _lastBought = "";

        /// <summary>Draws the prompt and, when open, the shop. Returns a cmd to send, or null.</summary>
        public byte[] Draw(Func<ushort> nextSeq, int credits)
        {
            EnsureStyles();
            if (credits >= 0) _credits = credits;

            if (!ShopOpen)
            {
                if (string.IsNullOrEmpty(Prompt)) return null;
                var size = _style.CalcSize(new GUIContent(Prompt));
                float w = size.x + 24, h = 26;
                var at = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.62f, w, h);
                GUI.DrawTexture(at, _panel);
                GUI.Label(new Rect(at.x + 12, at.y + 4, w, h), Prompt, _style);
                return null;
            }

            byte[] send = null;
            float panelW = 340, panelH = 62 + _stock.Length * 26 + 44;
            var rect = new Rect((Screen.width - panelW) * 0.5f, (Screen.height - panelH) * 0.5f, panelW, panelH);
            GUI.DrawTexture(rect, _panel);

            GUI.Label(new Rect(rect.x + 16, rect.y + 10, panelW, 22), "QUARTERMASTER VEX", _heading);
            GUI.Label(new Rect(rect.x + 16, rect.y + 32, panelW, 20),
                _credits >= 0 ? $"credits: {_credits}" : "credits: —", _style);

            for (int i = 0; i < _stock.Length; i++)
            {
                StockEntry e = _stock[i];
                var row = new Rect(rect.x + 16, rect.y + 60 + i * 26, panelW - 32, 22);
                bool afford = _credits < 0 || _credits >= e.price;
                GUI.enabled = afford;
                if (GUI.Button(row, $"{e.item}   —   {e.price} cr"))
                {
                    _lastBought = e.item;
                    _status = "buying...";
                    send = Encode.Cmd(nextSeq(), Op.ShopBuy,
                        $"{{\"npc\":{_shopNpc},\"item\":\"{e.item}\",\"qty\":1}}");
                }
                GUI.enabled = true;
            }

            GUI.Label(new Rect(rect.x + 16, rect.yMax - 40, panelW - 32, 20), _status, _style);
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
