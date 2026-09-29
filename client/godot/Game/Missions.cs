// Phase 10 client state: the journal and the party, as VIEWS of what the
// server said. Nothing here decides anything — cmds go out, events and
// results come back, these classes remember the latest word.

using System;
using System.Collections.Generic;
using SpaceAdventure.Net;
using Newtonsoft.Json;

namespace SpaceAdventure.Game
{
    public class MissionRow
    {
        public string id;
        public string type;
        public string name;
        public string text;
        public int count;
        public string item;
        public string poi;
        public long reward;
    }

    public class MissionStateRow
    {
        public bool active;
        public int count;
        public int done;
    }

    /// <summary>The journal: offers from the last board visit, live state.</summary>
    public sealed class MissionLog
    {
        public readonly List<MissionRow> Offers = new List<MissionRow>();
        public readonly Dictionary<string, MissionStateRow> State =
            new Dictionary<string, MissionStateRow>();

        /// <summary>The live priority offer, null when none/claimed away.</summary>
        public string PriorityMission;
        public string PriorityPoi;
        public float PriorityUntil; // (float)Clock.Now when the toast stops shouting

        public void OnListResult(string body)
        {
            // Newtonsoft: offers is an array, state a map — JsonUtility can
            // read neither shape.
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(body);
            Offers.Clear();
            var offers = parsed["offers"];
            if (offers != null)
            {
                foreach (var o in offers)
                    Offers.Add(o.ToObject<MissionRow>());
            }
            var state = parsed["state"];
            if (state != null)
            {
                foreach (var kv in (Newtonsoft.Json.Linq.JObject)state)
                    State[kv.Key] = kv.Value.ToObject<MissionStateRow>();
            }
        }

        public void OnProgress(string data)
        {
            var p = JsonConvert.DeserializeObject<ProgressEvent>(data);
            if (p == null || string.IsNullOrEmpty(p.id)) return;
            if (!State.TryGetValue(p.id, out var st))
                State[p.id] = st = new MissionStateRow { active = true };
            st.count = p.count;
        }

        public void OnComplete(string data)
        {
            var p = JsonConvert.DeserializeObject<CompleteEvent>(data);
            if (p == null || string.IsNullOrEmpty(p.id)) return;
            if (State.TryGetValue(p.id, out var st))
            {
                st.active = false;
                st.count = 0;
                st.done++;
            }
            LastCompleted = p.id;
            LastCompletedCredits = p.credits;
            LastCompletedAt = (float)Clock.Now;
            if (p.id == PriorityMission) PriorityMission = null;
        }

        public void OnPriorityOffer(string data)
        {
            var p = JsonConvert.DeserializeObject<OfferEvent>(data);
            if (p == null) return;
            PriorityMission = p.id;
            PriorityPoi = p.poi;
            PriorityUntil = (float)Clock.Now + 12f;
            ClaimNote = null;
            // A bounty is never listed at a board, so the journal would show
            // the raw id once it is active; give it a row with a name.
            if (Offers.Find(o => o.id == p.id) == null)
                Offers.Add(new MissionRow { id = p.id, type = "bounty", name = $"Bounty: warlord at {p.poi}", poi = p.poi });
        }

        /// <summary>The accept/claim in flight, so its result can land on the right row.</summary>
        public string PendingAccept;
        /// <summary>Why the last claim was refused, for the journal; null when none.</summary>
        public string ClaimNote;

        public void OnAcceptSent(string id)
        {
            PendingAccept = id;
            ClaimNote = null;
        }

        /// <summary>
        /// The server's answer to mission_accept carries no id and no state,
        /// and nothing else tells this client the journal changed until the
        /// next board listing -- so the row is updated here. A refusal
        /// reverts the board's optimistic activation and says why.
        /// </summary>
        public void OnAcceptResult(bool ok, string body)
        {
            string id = PendingAccept;
            PendingAccept = null;
            if (id == null) return;
            if (ok)
            {
                if (State.TryGetValue(id, out var st)) { st.active = true; st.count = 0; }
                else State[id] = new MissionStateRow { active = true };
                if (id == PriorityMission) PriorityMission = null;
                return;
            }
            if (State.TryGetValue(id, out var was)) was.active = false;
            string reason = "refused";
            try { reason = Newtonsoft.Json.Linq.JObject.Parse(body ?? "{}")["reason"]?.ToString() ?? reason; } catch (JsonException) { }
            ClaimNote = reason switch
            {
                "claimed" => "bounty already claimed by another party",
                "no_bounty" => "that bounty is gone",
                _ => $"refused: {reason}",
            };
            // A dead bounty offer stops shouting.
            if (id == PriorityMission && (reason == "claimed" || reason == "no_bounty")) PriorityMission = null;
        }

        public string LastCompleted;
        public long LastCompletedCredits;
        public float LastCompletedAt = -999f;

        /// <summary>A party member pushed a quest into this journal.</summary>
        public string LastSharedBy;
        public string LastSharedName;
        public float LastSharedAt = -999f;

        public void OnShared(string data)
        {
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(data);
            var row = parsed["mission"]?.ToObject<MissionRow>();
            if (row == null || string.IsNullOrEmpty(row.id)) return;
            // The recipient may never have visited a board: the template
            // rides the event so the journal can NAME the quest.
            if (Offers.Find(o => o.id == row.id) == null) Offers.Add(row);
            if (!State.TryGetValue(row.id, out var st))
                State[row.id] = st = new MissionStateRow();
            st.active = true;
            st.count = 0;
            LastSharedBy = (string)parsed["from"] ?? "";
            LastSharedName = row.name;
            LastSharedAt = (float)Clock.Now;
        }

        private class ProgressEvent { public string id { get; set; } public int count { get; set; } public int goal { get; set; } }
        private class CompleteEvent { public string id { get; set; } public long credits { get; set; } }
        private class OfferEvent { public string id { get; set; } public string poi { get; set; } public int expires_s { get; set; } }
    }

    /// <summary>The party roster and the pending invite, from events.</summary>
    public sealed class PartyState
    {
        public readonly List<(uint id, string name)> Members = new List<(uint, string)>();
        public uint PendingFrom;
        public string PendingName = "";
        public float PendingAt = -999f;

        public bool InParty => Members.Count > 0;

        public void OnUpdate(string data)
        {
            Members.Clear();
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(data);
            var members = parsed["members"];
            if (members == null) return;
            foreach (var m in members)
                Members.Add(((uint)m["id"], (string)m["name"]));
        }

        public void OnInvited(string data)
        {
            var p = JsonConvert.DeserializeObject<InviteEvent>(data);
            if (p == null) return;
            PendingFrom = p.from;
            PendingName = p.name ?? "";
            PendingAt = (float)Clock.Now;
        }

        private class InviteEvent { public uint from { get; set; } public string name { get; set; } }
    }
}
