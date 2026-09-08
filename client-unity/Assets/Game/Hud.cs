// U15 grown up — HUD state. The pixels moved to UI Toolkit in Phase 8
// (UI/HudView.cs); what remains here is what the events MEAN: the rolling
// log, the F3 debug text, and the health value the debug line reports.

using System.Collections.Generic;
using SpaceAdventure.Net;

namespace SpaceAdventure.Game
{
    public sealed class Hud
    {
        private const int MaxLog = 6;

        private readonly List<string> _log = new List<string>();

        public IReadOnlyList<string> Lines => _log;

        public ushort Health { get; set; }

        public void OnEvent(EventMsg ev, uint selfId)
        {
            switch (ev.EventId)
            {
                case EventId.ShotFired:
                    if (ev.EntityId != selfId) Log($"shot from {ev.EntityId}");
                    break;
                case EventId.Hit:
                {
                    // PROTOCOL: u32 shooter | f32 point[3] | u16 damage | u16 health_after
                    if (ev.Data.Length >= 20)
                    {
                        var r = new WireReader(ev.Data);
                        uint shooter = r.ReadU32();
                        r.ReadF32(); r.ReadF32(); r.ReadF32();
                        ushort damage = r.ReadU16();
                        Log(shooter == selfId
                            ? $"hit {ev.EntityId} for {damage}"
                            : $"{ev.EntityId} took {damage}");
                    }
                    break;
                }
                case EventId.Death:
                    Log(ev.EntityId == selfId ? "you died" : $"{ev.EntityId} died");
                    break;
                case EventId.LootDropped:
                    Log($"loot dropped ({ev.EntityId})");
                    break;
                case EventId.Equipped:
                {
                    string item = WireReader.Utf8.GetString(ev.Data);
                    Log($"{ev.EntityId} equipped {(item.Length == 0 ? "nothing" : item)}");
                    break;
                }
            }
        }

        public void OnCmdResult(CmdResult r)
        {
            // Bodies can be whole JSON documents (mission_list); the log is
            // a glance, not a wire dump.
            string body = r.Body ?? "";
            if (body.Length > 48) body = body.Substring(0, 48) + "…";
            Log($"cmd {r.Opcode} -> {(r.Ok ? "ok" : $"status {r.StatusCode}")} {body}");
        }

        private void Log(string line)
        {
            _log.Add(line);
            if (_log.Count > MaxLog) _log.RemoveAt(0);
        }

        /// <summary>
        /// Phase 8: the status block is a DEBUG overlay (F3). Health, ammo
        /// and credits live on the UI Toolkit HUD; this is diagnostics
        /// (link, rtt, prediction) and the controls reference —
        /// genre-standard behind a key, invisible by default.
        /// </summary>
        public bool DebugOpen;

        /// <summary>The F3 overlay's text, or null while hidden.</summary>
        public string DebugText(NetClient net, Predictor predictor, Character character)
        {
            if (!DebugOpen) return null;
            string rtt = net.RttMs >= 0 ? $"{net.RttMs} ms" : "—";
            string ammo = character.Magazine < 0
                ? "ammo: — (R to load)"
                : $"ammo: {character.Magazine} / {character.Reserve}";
            string err = string.IsNullOrEmpty(net.LastError) ? "" : $"last error: {net.LastError}\n";
            return $"link: {net.State}   rtt: {rtt}   reconnects: {net.Reconnects}\n" +
                   $"entity: {net.EntityId}   health: {Health}   {ammo}\n" +
                   $"pending inputs: {predictor.PendingCount}   " +
                   $"last correction: {predictor.LastCorrection:F3} m\n" + err +
                   "WASD move · shift sprint · space jump · LMB fire\n" +
                   "R reload · E talk/shop · B bags · C character · M map · esc · F3 hide";
        }
    }
}
