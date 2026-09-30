// The journal (J) and the party panel (P), Scrapyard Comic like everything
// else. Dumb views over MissionLog/PartyState; every button hands finished
// cmd bytes to the send callback.

using System;
using System.Collections.Generic;
using Godot;
using SpaceAdventure.Net;

namespace SpaceAdventure.Game.UI
{
    /// <summary>The journal: active missions always; board offers when at one.</summary>
    public sealed class JournalView : ModalView
    {
        private readonly MissionLog _log;
        private readonly PartyState _party;
        private readonly Func<uint> _boardNpc; // nearest board's entity id, 0 = none
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public JournalView(Control root, MissionLog log, PartyState party,
            Func<uint> boardNpc, Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Journal", 420)
        {
            _log = log;
            _party = party;
            _boardNpc = boardNpc;
            _nextSeq = nextSeq;
            _send = send;
        }

        private byte[] Cmd(ushort op, string body) => Encode.Cmd(_nextSeq(), op, body);

        private static Color TypeColor(string type) => type switch
        {
            "kill" => Styles.Danger, "bounty" => Styles.Amber, "scout" => Styles.Shield, "fetch" => Styles.Good, _ => Styles.Dust,
        };

        private static string TypeGlyph(string type) => type switch
        {
            "kill" => "K", "bounty" => "B", "scout" => "S", "fetch" => "F", _ => "?",
        };

        protected override void Fill(VBoxContainer body)
        {
            uint board = _boardNpc();

            // Priority offer first — it is the shout.
            if (_log.PriorityMission != null)
            {
                string pid = _log.PriorityMission;
                body.AddChild(Styles.Card(Styles.Amber, Styles.Tile("!", Styles.Amber),
                    $"Warlord sighted at {_log.PriorityPoi.ToUpperInvariant()}", Styles.Amber, "priority bounty · first party to claim it",
                    Styles.Button("CLAIM", false, () =>
                    {
                        _log.OnAcceptSent(pid);
                        _send(Cmd(Op.MissionAccept, $"{{\"id\":\"{pid}\"}}"));
                        Rebuild();
                    })));
            }
            if (_log.ClaimNote != null) Line(body, _log.ClaimNote, Styles.Danger, 12);

            body.AddChild(Styles.Header("Active"));
            bool anyActive = false;
            foreach (var kv in _log.State)
            {
                if (!kv.Value.active) continue;
                anyActive = true;
                var offer = _log.Offers.Find(o => o.id == kv.Key);
                string name = offer?.name ?? kv.Key;
                string type = offer?.type ?? "";
                string mid = kv.Key;
                var trailing = new List<Control>();
                if (offer != null && offer.count > 0)
                    trailing.Add(Styles.Progress(Mathf.Clamp((float)kv.Value.count / offer.count, 0f, 1f), TypeColor(type), $"{kv.Value.count} / {offer.count}", 110));
                if (_party.InParty && (offer == null || offer.type != "bounty"))
                    trailing.Add(Styles.Button("SHARE", false, () => { _send(Cmd(Op.MissionShare, $"{{\"id\":\"{mid}\"}}")); Rebuild(); }));
                if (board != 0 && offer != null && offer.type == "fetch")
                    trailing.Add(Styles.Button("TURN IN", false, () => { _send(Cmd(Op.MissionTurnin, $"{{\"npc\":{board},\"id\":\"{mid}\"}}")); Rebuild(); }));
                trailing.Add(Styles.Button("DROP", true, () =>
                {
                    _send(Cmd(Op.MissionAbandon, $"{{\"id\":\"{mid}\"}}"));
                    if (_log.State.TryGetValue(mid, out var st)) { st.active = false; st.count = 0; }
                    Rebuild();
                }));
                string sub = offer == null ? "" : (string.IsNullOrEmpty(offer.text) ? $"{offer.reward} cr" : $"{offer.text}  ·  {offer.reward} cr");
                body.AddChild(Styles.Card(TypeColor(type), Styles.Tile(TypeGlyph(type), TypeColor(type)), name, Styles.Cream, sub, trailing.ToArray()));
            }
            if (!anyActive) Line(body, "no active missions", Styles.Dust);

            // The board's offers, when standing at one.
            body.AddChild(Styles.Gap(4));
            body.AddChild(Styles.Header("Board"));
            if (board == 0)
            {
                Line(body, "visit the dispatcher at the relay for work", Styles.Dust, 12);
            }
            else if (_log.Offers.Count == 0)
            {
                Line(body, "asking the board…", Styles.Dust, 12);
            }
            else
            {
                foreach (var o in _log.Offers)
                {
                    bool active = _log.State.TryGetValue(o.id, out var st) && st.active;
                    if (active || o.type == "bounty") continue;
                    string mid = o.id;
                    body.AddChild(Styles.Card(TypeColor(o.type), Styles.Tile(TypeGlyph(o.type), TypeColor(o.type)), o.name, Styles.Cream,
                        (string.IsNullOrEmpty(o.text) ? "" : o.text + "  ·  ") + $"{o.reward} cr",
                        Styles.Button("ACCEPT", false, () =>
                        {
                            _log.OnAcceptSent(mid);
                            _send(Cmd(Op.MissionAccept, $"{{\"id\":\"{mid}\"}}"));
                            if (_log.State.TryGetValue(mid, out var st2)) st2.active = true;
                            else _log.State[mid] = new MissionStateRow { active = true };
                            Rebuild();
                        })));
                }
            }
            body.AddChild(Styles.Gap(4));
            Line(body, "J closes", Styles.Dust, 12);
        }
    }

    /// <summary>The party panel: roster, pending invite, nearby players.</summary>
    public sealed class PartyView : ModalView
    {
        private readonly PartyState _party;
        private readonly Func<List<(uint id, string name)>> _nearby;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public PartyView(Control root, PartyState party,
            Func<List<(uint id, string name)>> nearby,
            Func<ushort> nextSeq, Action<byte[]> send)
            : base(root, "Party", 340)
        {
            _party = party;
            _nearby = nearby;
            _nextSeq = nextSeq;
            _send = send;
        }

        private byte[] Cmd(ushort op, string body) => Encode.Cmd(_nextSeq(), op, body);

        private static Control Face(string name, Color c) => Styles.Tile(string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant(), c, 36);

        protected override void Fill(VBoxContainer body)
        {
            if (_party.PendingFrom != 0)
            {
                body.AddChild(Styles.Card(Styles.Amber, Face(_party.PendingName, Styles.Amber), $"{_party.PendingName} invites you", Styles.Amber, "to their party",
                    Styles.Button("ACCEPT", false, () => { _send(Cmd(Op.PartyRespond, "{\"accept\":true}")); _party.PendingFrom = 0; Rebuild(); }),
                    Styles.Button("DECLINE", true, () => { _send(Cmd(Op.PartyRespond, "{\"accept\":false}")); _party.PendingFrom = 0; Rebuild(); })));
                body.AddChild(Styles.Gap(4));
            }
            body.AddChild(Styles.Header("Members"));
            if (_party.InParty)
            {
                foreach (var (id, name) in _party.Members)
                    body.AddChild(Styles.Card(Styles.Shield, Face(name, Styles.Shield), name, Styles.Cream, "in your party"));
                body.AddChild(Styles.Gap(4));
                var leave = Styles.Button("LEAVE PARTY", true, () => { _send(Cmd(Op.PartyLeave, "{}")); Rebuild(); });
                leave.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                body.AddChild(leave);
            }
            else
            {
                Line(body, "not in a party", Styles.Dust);
            }
            body.AddChild(Styles.Gap(4));
            body.AddChild(Styles.Header("Nearby"));
            var near = _nearby();
            if (near.Count == 0) Line(body, "nobody in sight", Styles.Dust, 12);
            foreach (var (id, name) in near)
            {
                uint pid = id;
                body.AddChild(Styles.Card(Styles.Dust, Face(name, Styles.Cream), name, Styles.Cream, "",
                    Styles.Button("INVITE", false, () => { _send(Cmd(Op.PartyInvite, $"{{\"target\":{pid}}}")); Rebuild(); })));
            }
            body.AddChild(Styles.Gap(4));
            Line(body, "P closes", Styles.Dust, 12);
        }
    }
}
