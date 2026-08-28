// U15 — the HUD, on IMGUI.
//
// IMGUI rather than UI Toolkit or uGUI on purpose: both of those want assets —
// a UXML tree or a Canvas prefab — and CONVENTIONS.md forbids agent-authored
// prefabs because Unity stores them as GUID-keyed YAML that cannot be reviewed
// in a diff (C47). OnGUI is the one UI path that is entirely C#.
//
// It is not the final HUD. It is the one that can ship in a text file, and it
// shows the numbers you actually need while play-testing: link state, RTT, how
// far the last reconcile moved you, health, and the last few events.

using System.Collections.Generic;
using SpaceAdventure.Net;
using UnityEngine;

namespace SpaceAdventure.Game
{
    public sealed class Hud
    {
        private const int MaxLog = 6;

        private readonly List<string> _log = new List<string>();
        private GUIStyle _style;
        private Texture2D _panel;

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
            => Log($"cmd {r.Opcode} -> {(r.Ok ? "ok" : $"status {r.StatusCode}")} {r.Body}");

        private void Log(string line)
        {
            _log.Add(line);
            if (_log.Count > MaxLog) _log.RemoveAt(0);
        }

        public void Draw(NetClient net, Predictor predictor, FpsController fps)
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = false };
                _style.normal.textColor = Color.white;
                _panel = new Texture2D(1, 1);
                _panel.SetPixel(0, 0, new Color(0, 0, 0, 0.55f));
                _panel.Apply();
            }

            GUI.DrawTexture(new Rect(8, 8, 330, 120), _panel);
            GUILayout.BeginArea(new Rect(16, 12, 320, 116));
            string rtt = net.RttMs >= 0 ? $"{net.RttMs} ms" : "—";
            GUILayout.Label($"link: {net.State}   rtt: {rtt}   reconnects: {net.Reconnects}", _style);
            GUILayout.Label($"entity: {net.EntityId}   health: {Health}", _style);
            GUILayout.Label($"pending inputs: {predictor.PendingCount}   " +
                            $"last correction: {predictor.LastCorrection:F3} m", _style);
            if (!string.IsNullOrEmpty(net.LastError)) GUILayout.Label($"last error: {net.LastError}", _style);
            GUILayout.Label("WASD move · shift sprint · space jump · LMB fire · M map · esc cursor", _style);
            GUILayout.EndArea();

            if (_log.Count > 0)
            {
                GUI.DrawTexture(new Rect(8, 136, 330, 18 * _log.Count + 8), _panel);
                GUILayout.BeginArea(new Rect(16, 140, 320, 18 * _log.Count + 4));
                foreach (string line in _log) GUILayout.Label(line, _style);
                GUILayout.EndArea();
            }

            // Crosshair. Drawn last so nothing overlaps it.
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            GUI.DrawTexture(new Rect(cx - 1, cy - 6, 2, 12), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 6, cy - 1, 12, 2), Texture2D.whiteTexture);
        }
    }
}
