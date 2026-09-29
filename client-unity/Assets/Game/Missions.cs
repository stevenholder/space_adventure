// Phase 10 client state: the journal and the party, as VIEWS of what the
// server said. Nothing here decides anything — cmds go out, events and
// results come back, these classes remember the latest word.

using System;
using System.Collections.Generic;
using SpaceAdventure.Net;
using UnityEngine;

namespace SpaceAdventure.Game
{
    [Serializable]
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

    [Serializable]
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
        public float PriorityUntil; // Time.time when the toast stops shouting

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
            var p = JsonUtility.FromJson<ProgressEvent>(data);
            if (p == null || string.IsNullOrEmpty(p.id)) return;
            if (!State.TryGetValue(p.id, out var st))
                State[p.id] = st = new MissionStateRow { active = true };
            st.count = p.count;
        }

        public void OnComplete(string data)
        {
            var p = JsonUtility.FromJson<CompleteEvent>(data);
            if (p == null || string.IsNullOrEmpty(p.id)) return;
            if (State.TryGetValue(p.id, out var st))
            {
                st.active = false;
                st.count = 0;
                st.done++;
            }
            LastCompleted = p.id;
            LastCompletedCredits = p.credits;
            LastCompletedAt = Time.time;
            if (p.id == PriorityMission) PriorityMission = null;
        }

        public void OnPriorityOffer(string data)
        {
            var p = JsonUtility.FromJson<OfferEvent>(data);
            if (p == null) return;
            PriorityMission = p.id;
            PriorityPoi = p.poi;
            PriorityUntil = Time.time + 12f;
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
            LastSharedAt = Time.time;
        }

        [Serializable] private class ProgressEvent { public string id; public int count; public int goal; }
        [Serializable] private class CompleteEvent { public string id; public long credits; }
        [Serializable] private class OfferEvent { public string id; public string poi; public int expires_s; }
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
            var p = JsonUtility.FromJson<InviteEvent>(data);
            if (p == null) return;
            PendingFrom = p.from;
            PendingName = p.name ?? "";
            PendingAt = Time.time;
        }

        [Serializable] private class InviteEvent { public uint from; public string name; }
    }
}
