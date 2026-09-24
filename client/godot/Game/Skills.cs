// Phase 11 client state: the skill sheet as a VIEW of what the server said
// (the `skills` result and every `skill_xp` event). Levels derive from XP
// through the shared curve; efficacy and synergy numbers derive from the
// levels through the per_level rates in `defs` — the same arithmetic the
// server runs, so a tooltip never disagrees with a hit.

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SpaceAdventure.Net;

namespace SpaceAdventure.Game
{
    public sealed class SkillSheet
    {
        public struct Drip { public string Skill; public long XP; public float At; }

        public readonly Dictionary<string, long> XP = new Dictionary<string, long>();
        public readonly List<string> Discovered = new List<string>();
        /// <summary>True once the sheet has answered; the panel waits on it.</summary>
        public bool Loaded;

        /// <summary>Recent awards for the drip, oldest first; the feed trims them.</summary>
        public readonly List<Drip> Drips = new List<Drip>();
        public string LevelUpSkill = "";
        public int LevelUpLevel;
        public float LevelUpAt = -100f;

        public int Level(string id) => SkillCurve.LevelForXP(XP.TryGetValue(id, out long v) ? v : 0);

        /// <summary>`skills` result: {"xp":{...},"levels":{...},"discovered":[...]}.</summary>
        public void OnSheet(string body)
        {
            JObject o;
            try { o = JObject.Parse(body); } catch (Newtonsoft.Json.JsonException) { return; }
            XP.Clear();
            if (o["xp"] is JObject xp)
                foreach (var kv in xp) XP[kv.Key] = kv.Value.Value<long>();
            Discovered.Clear();
            if (o["discovered"] is JArray d)
                foreach (var t in d) Discovered.Add(t.Value<string>());
            Loaded = true;
        }

        /// <summary>`skill_xp` event: {skill,xp,level,next_at,leveled}.</summary>
        public void OnXP(string body, float now)
        {
            JObject o;
            try { o = JObject.Parse(body); } catch (Newtonsoft.Json.JsonException) { return; }
            string skill = o.Value<string>("skill") ?? "";
            long total = o.Value<long>("xp");
            long before = XP.TryGetValue(skill, out long b) ? b : 0;
            XP[skill] = total;
            if (total > before) Drips.Add(new Drip { Skill = skill, XP = total - before, At = now });
            if (o.Value<bool>("leveled"))
            {
                LevelUpSkill = skill;
                LevelUpLevel = o.Value<int>("level");
                LevelUpAt = now;
            }
        }

        /// <summary>(level−1)·per_level for a roster skill; 0 untrained or unknown.</summary>
        public double EfficacyBonus(Defs defs, string id)
        {
            var sk = defs.Skills?.Find(s => s.Id == id);
            if (sk?.Efficacy == null) return 0;
            return (Level(id) - 1) * sk.Efficacy.PerLevel;
        }

        /// <summary>(source level−1)·per_level for one declared synergy.</summary>
        public double SynergyBonus(SynergyDef sy) => (Level(sy.Source) - 1) * sy.PerLevel;
    }
}
