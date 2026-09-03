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

        private void EnsureStyles()
        {
            if (_style != null) return;
            _style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = false };
            _style.normal.textColor = Color.white;
            _panel = new Texture2D(1, 1);
            _panel.SetPixel(0, 0, new Color(0, 0, 0, 0.55f));
            _panel.Apply();
        }

        public void OnCmdResult(CmdResult r)
            => Log($"cmd {r.Opcode} -> {(r.Ok ? "ok" : $"status {r.StatusCode}")} {r.Body}");

        private void Log(string line)
        {
            _log.Add(line);
            if (_log.Count > MaxLog) _log.RemoveAt(0);
        }

        /// <summary>
        /// Health bars above the wounded.
        ///
        /// Drawn in screen space from the entity's own world position rather
        /// than as world-space quads: a billboard has to be re-oriented every
        /// frame and still shears when you look up, and on a sphere "up" is a
        /// different direction for every body on screen.
        /// </summary>
        public void DrawHealthBars(Camera cam, EntityViews views)
        {
            if (cam == null || views == null) return;
            EnsureStyles();

            foreach (EntityView v in views.All)
            {
                if (!v.ShowHealthBar || v.Root == null) continue;

                // Above the crown: bodies are 1.8 m and the bar clears the head.
                Vector3 world = v.Root.transform.position;
                Vector3 above = world + world.normalized * 2.05f;
                Vector3 screen = cam.WorldToScreenPoint(above);
                if (screen.z <= 0f) continue; // behind the camera

                // Shrink with distance, but never to nothing: a 2-pixel bar on
                // a distant grunt says less than no bar at all.
                float width = Mathf.Clamp(900f / screen.z, 26f, 90f);
                const float height = 5f;
                float x = screen.x - width * 0.5f;
                float y = Screen.height - screen.y; // GUI space is y-down

                float frac = v.HealthFraction;
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.DrawTexture(new Rect(x - 1, y - 1, width + 2, height + 2), Texture2D.whiteTexture);
                // Green at full, red at empty, through amber.
                GUI.color = Color.Lerp(new Color(0.85f, 0.15f, 0.12f),
                                       new Color(0.30f, 0.85f, 0.30f), frac);
                GUI.DrawTexture(new Rect(x, y, width * frac, height), Texture2D.whiteTexture);
                GUI.color = Color.white;
            }
        }

        /// <summary>
        /// Phase 8: the status block is a DEBUG overlay now (F3). Health,
        /// ammo and credits live on the UI Toolkit HUD; what remains here is
        /// diagnostics (link, rtt, prediction) and the controls reference —
        /// genre-standard behind a key, invisible by default.
        /// </summary>
        public bool DebugOpen;

        public void Draw(NetClient net, Predictor predictor, Character character)
        {
            EnsureStyles();
            if (!DebugOpen) { DrawLogAndToasts(net); return; }

            GUI.DrawTexture(new Rect(8, 8, 330, 136), _panel);
            GUILayout.BeginArea(new Rect(16, 12, 320, 132));
            string rtt = net.RttMs >= 0 ? $"{net.RttMs} ms" : "—";
            GUILayout.Label($"link: {net.State}   rtt: {rtt}   reconnects: {net.Reconnects}", _style);
            string ammo = character.Magazine < 0
                ? "ammo: — (R to load)"
                : $"ammo: {character.Magazine} / {character.Reserve}";
            GUILayout.Label($"entity: {net.EntityId}   health: {Health}   {ammo}", _style);
            GUILayout.Label($"pending inputs: {predictor.PendingCount}   " +
                            $"last correction: {predictor.LastCorrection:F3} m", _style);
            if (!string.IsNullOrEmpty(net.LastError)) GUILayout.Label($"last error: {net.LastError}", _style);
            GUILayout.Label("WASD move · shift sprint · space jump · LMB fire", _style);
            GUILayout.Label("R reload · E talk/shop · B bags · C character · M map · esc · F3 hide", _style);
            GUILayout.EndArea();

            DrawLogAndToasts(net);
        }

        /// <summary>The always-on remainder: event log and the crosshair.</summary>
        private void DrawLogAndToasts(NetClient net)
        {
            if (_log.Count > 0)
            {
                GUI.DrawTexture(new Rect(8, 152, 330, 18 * _log.Count + 8), _panel);
                GUILayout.BeginArea(new Rect(16, 156, 320, 18 * _log.Count + 4));
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
