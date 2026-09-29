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

        protected override void Fill(VBoxContainer body)
        {
            uint board = _boardNpc();

            // Priority offer first — it is the shout.
            if (_log.PriorityMission != null)
            {
                var row = LabelRow(body, $"PRIORITY: warlord at {_log.PriorityPoi.ToUpperInvariant()}", 14, Styles.Amber);
                string pid = _log.PriorityMission;
                row.AddChild(Styles.Button("CLAIM", false, () =>
                {
                    _send(Cmd(Op.MissionAccept, $"{{\"id\":\"{pid}\"}}"));
                    Rebuild();
                }));
            }

            // Active missions, with progress.
            bool anyActive = false;
            foreach (var kv in _log.State)
            {
                if (!kv.Value.active) continue;
                anyActive = true;
                var offer = _log.Offers.Find(o => o.id == kv.Key);
                string name = offer?.name ?? kv.Key;
                string progress = offer != null && offer.count > 0 ? $"{kv.Value.count}/{offer.count}" : "";
                var row = LabelRow(body, $"{name}  {progress}", 13, Styles.Cream);
                string mid = kv.Key;
                if (_party.InParty && (offer == null || offer.type != "bounty"))
                {
                    row.AddChild(Styles.Button("SHARE", false, () =>
                    {
                        _send(Cmd(Op.MissionShare, $"{{\"id\":\"{mid}\"}}"));
                        Rebuild();
                    }));
                }
                if (board != 0 && offer != null && offer.type == "fetch")
                {
                    row.AddChild(Styles.Button("TURN IN", false, () =>
                    {
                        _send(Cmd(Op.MissionTurnin, $"{{\"npc\":{board},\"id\":\"{mid}\"}}"));
                        Rebuild();
                    }));
                }
                row.AddChild(Styles.Button("DROP", true, () =>
                {
                    _send(Cmd(Op.MissionAbandon, $"{{\"id\":\"{mid}\"}}"));
                    if (_log.State.TryGetValue(mid, out var st)) { st.active = false; st.count = 0; }
                    Rebuild();
                }));
            }
            if (!anyActive) Line(body, "no active missions", Styles.Dust);

            // The board's offers, when standing at one.
            body.AddChild(Styles.Gap(4));
            body.AddChild(Styles.Rule());
            body.AddChild(Styles.Gap(4));
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
                    var row = LabelRow(body, $"{o.name}  ·  {o.reward} cr", 13, active ? Styles.Dust : Styles.Cream);
                    if (!active)
                    {
                        string mid = o.id;
                        row.AddChild(Styles.Button("ACCEPT", false, () =>
                        {
                            _send(Cmd(Op.MissionAccept, $"{{\"id\":\"{mid}\"}}"));
                            if (_log.State.TryGetValue(mid, out var st2)) st2.active = true;
                            else _log.State[mid] = new MissionStateRow { active = true };
                            Rebuild();
                        }));
                    }
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

        protected override void Fill(VBoxContainer body)
        {
            if (_party.PendingFrom != 0)
            {
                var row = LabelRow(body, $"{_party.PendingName} invites you", 13, Styles.Amber);
                row.AddChild(Styles.Button("ACCEPT", false, () =>
                {
                    _send(Cmd(Op.PartyRespond, "{\"accept\":true}"));
                    _party.PendingFrom = 0;
                    Rebuild();
                }));
                row.AddChild(Styles.Button("DECLINE", true, () =>
                {
                    _send(Cmd(Op.PartyRespond, "{\"accept\":false}"));
                    _party.PendingFrom = 0;
                    Rebuild();
                }));
            }

            if (_party.InParty)
            {
                foreach (var (id, name) in _party.Members) Line(body, name, Styles.Cream);
                body.AddChild(Styles.Gap(4));
                var leave = Styles.Button("LEAVE PARTY", true, () =>
                {
                    _send(Cmd(Op.PartyLeave, "{}"));
                    Rebuild();
                });
                leave.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                body.AddChild(leave);
            }
            else
            {
                Line(body, "not in a party", Styles.Dust);
            }

            body.AddChild(Styles.Gap(4));
            body.AddChild(Styles.Rule());
            body.AddChild(Styles.Gap(4));
            Line(body, "NEARBY", Styles.Dust, 12);
            var near = _nearby();
            if (near.Count == 0) Line(body, "nobody in sight", Styles.Dust, 12);
            foreach (var (id, name) in near)
            {
                var row = LabelRow(body, name, 13, Styles.Cream);
                uint pid = id;
                row.AddChild(Styles.Button("INVITE", false, () =>
                {
                    _send(Cmd(Op.PartyInvite, $"{{\"target\":{pid}}}"));
                    Rebuild();
                }));
            }
            body.AddChild(Styles.Gap(4));
            Line(body, "P closes · look at a player and press E to invite", Styles.Dust, 12);
        }
    }
}
