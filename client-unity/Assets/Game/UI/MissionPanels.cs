// Phase 10 UI: the journal (J) and the party panel (P), Scrapyard Comic
// like everything else. Dumb views over MissionLog/PartyState; every button
// hands finished cmd bytes to the send callback.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
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

        public JournalView(VisualElement root, MissionLog log, PartyState party,
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

        protected override void Fill(VisualElement body)
        {
            uint board = _boardNpc();

            // Priority offer first — it is the shout.
            if (_log.PriorityMission != null)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                var l = Styles.Display_($"PRIORITY: warlord at {_log.PriorityPoi.ToUpperInvariant()}", 14, Styles.Amber);
                l.style.flexGrow = 1;
                row.Add(l);
                string pid = _log.PriorityMission;
                var claim = new Button(() =>
                {
                    _send(Cmd(Op.MissionAccept, $"{{\"id\":\"{pid}\"}}"));
                    Rebuild();
                }) { text = "CLAIM" };
                ItemCard.StyleButton(claim, false);
                row.Add(claim);
                body.Add(row);
            }

            // Active missions, with progress.
            bool anyActive = false;
            foreach (var kv in _log.State)
            {
                if (!kv.Value.active) continue;
                anyActive = true;
                var offer = _log.Offers.Find(o => o.id == kv.Key);
                string name = offer?.name ?? kv.Key;
                string progress = offer != null && offer.count > 0
                    ? $"{kv.Value.count}/{offer.count}" : "";
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 3;
                var l = Styles.Display_($"{name}  {progress}", 13, Styles.Cream);
                l.style.flexGrow = 1;
                row.Add(l);
                string mid = kv.Key;
                if (_party.InParty && (offer == null || offer.type != "bounty"))
                {
                    var share = new Button(() =>
                    {
                        _send(Cmd(Op.MissionShare, $"{{\"id\":\"{mid}\"}}"));
                        Rebuild();
                    }) { text = "SHARE" };
                    ItemCard.StyleButton(share, false);
                    row.Add(share);
                }
                if (board != 0 && offer != null && offer.type == "fetch")
                {
                    var turnin = new Button(() =>
                    {
                        _send(Cmd(Op.MissionTurnin, $"{{\"npc\":{board},\"id\":\"{mid}\"}}"));
                        Rebuild();
                    }) { text = "TURN IN" };
                    ItemCard.StyleButton(turnin, false);
                    row.Add(turnin);
                }
                var drop = new Button(() =>
                {
                    _send(Cmd(Op.MissionAbandon, $"{{\"id\":\"{mid}\"}}"));
                    if (_log.State.TryGetValue(mid, out var st)) { st.active = false; st.count = 0; }
                    Rebuild();
                }) { text = "DROP" };
                ItemCard.StyleButton(drop, true);
                row.Add(drop);
                body.Add(row);
            }
            if (!anyActive) Line(body, "no active missions", Styles.Dust);

            // The board's offers, when standing at one.
            var rule = new VisualElement();
            rule.style.height = 2;
            rule.style.backgroundColor = Styles.Ink;
            rule.style.marginTop = 6;
            rule.style.marginBottom = 6;
            body.Add(rule);
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
                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.alignItems = Align.Center;
                    row.style.marginBottom = 3;
                    var l = Styles.Display_($"{o.name}  ·  {o.reward} cr", 13,
                        active ? Styles.Dust : Styles.Cream);
                    l.style.flexGrow = 1;
                    row.Add(l);
                    if (!active)
                    {
                        string mid = o.id;
                        var take = new Button(() =>
                        {
                            _send(Cmd(Op.MissionAccept, $"{{\"id\":\"{mid}\"}}"));
                            if (_log.State.TryGetValue(mid, out var st2)) st2.active = true;
                            else _log.State[mid] = new MissionStateRow { active = true };
                            Rebuild();
                        }) { text = "ACCEPT" };
                        ItemCard.StyleButton(take, false);
                        row.Add(take);
                    }
                    body.Add(row);
                }
            }
            Line(body, "J closes", Styles.Dust, 12).style.marginTop = 6;
        }
    }

    /// <summary>The party panel: roster, pending invite, nearby players.</summary>
    public sealed class PartyView : ModalView
    {
        private readonly PartyState _party;
        private readonly Func<List<(uint id, string name)>> _nearby;
        private readonly Func<ushort> _nextSeq;
        private readonly Action<byte[]> _send;

        public PartyView(VisualElement root, PartyState party,
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

        protected override void Fill(VisualElement body)
        {
            if (_party.PendingFrom != 0)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                var l = Styles.Display_($"{_party.PendingName} invites you", 13, Styles.Amber);
                l.style.flexGrow = 1;
                row.Add(l);
                var yes = new Button(() =>
                {
                    _send(Cmd(Op.PartyRespond, "{\"accept\":true}"));
                    _party.PendingFrom = 0;
                    Rebuild();
                }) { text = "ACCEPT" };
                ItemCard.StyleButton(yes, false);
                var no = new Button(() =>
                {
                    _send(Cmd(Op.PartyRespond, "{\"accept\":false}"));
                    _party.PendingFrom = 0;
                    Rebuild();
                }) { text = "DECLINE" };
                ItemCard.StyleButton(no, true);
                row.Add(yes);
                row.Add(no);
                body.Add(row);
            }

            if (_party.InParty)
            {
                foreach (var (id, name) in _party.Members)
                    Line(body, name, Styles.Cream);
                var leave = new Button(() =>
                {
                    _send(Cmd(Op.PartyLeave, "{}"));
                    Rebuild();
                }) { text = "LEAVE PARTY" };
                ItemCard.StyleButton(leave, true);
                leave.style.marginTop = 6;
                body.Add(leave);
            }
            else
            {
                Line(body, "not in a party", Styles.Dust);
            }

            var rule = new VisualElement();
            rule.style.height = 2;
            rule.style.backgroundColor = Styles.Ink;
            rule.style.marginTop = 6;
            rule.style.marginBottom = 6;
            body.Add(rule);
            Line(body, "NEARBY", Styles.Dust, 12);
            var near = _nearby();
            if (near.Count == 0) Line(body, "nobody in sight", Styles.Dust, 12);
            foreach (var (id, name) in near)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 3;
                var l = Styles.Display_(name, 13, Styles.Cream);
                l.style.flexGrow = 1;
                row.Add(l);
                uint pid = id;
                var invite = new Button(() =>
                {
                    _send(Cmd(Op.PartyInvite, $"{{\"target\":{pid}}}"));
                    Rebuild();
                }) { text = "INVITE" };
                ItemCard.StyleButton(invite, false);
                row.Add(invite);
                body.Add(row);
            }
            Line(body, "P closes · look at a player and press E to invite", Styles.Dust, 12).style.marginTop = 6;
        }
    }
}
