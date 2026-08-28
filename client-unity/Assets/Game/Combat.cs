// U16 — what a shot looks like.
//
// Every visual here is driven by a SERVER event, never by the local trigger
// press. That is the whole design rule of this file.
//
// The server owns spread, so the direction a shot actually took is not the
// direction the client aimed. `shot_fired` carries the ray the server
// resolved — its rewound origin and its post-spread direction — precisely so
// every client can draw the same tracer as the one the hit markers agree with
// (PROTOCOL.md, "fire"). Drawing the local aim instead puts your tracer along
// a line the shot did not take, and that reads as broken hit registration and
// sends you debugging the netcode.
//
// The one exception is the muzzle flash, which is a local trigger effect with
// no authority attached: it says "you pulled the trigger", not "a shot went
// there".

using System.Collections.Generic;
using SpaceAdventure.Net;
using UnityEngine;

namespace SpaceAdventure.Game
{
    /// <summary>Tracers, impact marks and muzzle flash, all on a timer.</summary>
    public sealed class CombatFx
    {
        private const float TracerSeconds = 0.06f;
        private const float ImpactSeconds = 0.35f;
        private const float FlashSeconds = 0.05f;

        /// <summary>A live effect and when it dies.</summary>
        private struct Fx
        {
            public GameObject Go;
            public float DiesAt;
            public float BornAt;
            public bool ScaleOut;
        }

        private readonly List<Fx> _live = new List<Fx>();
        private readonly Transform _parent;
        private readonly Material _lineMaterial;

        private GameObject _flash;
        private float _flashDiesAt;

        public CombatFx(Transform parent)
        {
            _parent = parent;
            // Unlit and vertex-coloured: a tracer that responds to the sun is
            // invisible on the night side of a planet, which is exactly where
            // you need to see where you shot.
            _lineMaterial = new Material(Shader.Find("Sprites/Default"));
        }

        /// <summary>
        /// Draws the ray the SERVER resolved. Payload per PROTOCOL.md:
        /// f32 origin[3] | f32 dir[3] | f32 dist.
        ///
        /// `drawFrom` moves only where the line STARTS. The server resolves a
        /// shot from the shooter's eye, which is right for hit registration
        /// and wrong to draw: a line from your own eye is a dot in the middle
        /// of the screen, in front of the gun that supposedly fired it. For
        /// your own shots the line starts at the barrel instead and ends
        /// exactly where the server said it ended — the endpoint is what the
        /// hit markers agree with, so it is never moved.
        /// </summary>
        public void OnShotFired(EventMsg ev, Vector3? drawFrom = null)
        {
            if (ev.Data.Length < 28) return;
            var r = new WireReader(ev.Data);
            var origin = new Vector3(r.ReadF32(), r.ReadF32(), r.ReadF32());
            var dir = new Vector3(r.ReadF32(), r.ReadF32(), r.ReadF32());
            float dist = r.ReadF32();
            if (dir.sqrMagnitude < 1e-8f) return;

            Vector3 end = origin + dir.normalized * dist;
            Vector3 start = drawFrom ?? origin;

            var go = new GameObject($"tracer-{ev.EntityId}");
            go.transform.SetParent(_parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.material = _lineMaterial;
            line.widthMultiplier = 0.03f;
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.SetPosition(0, start);
            line.SetPosition(1, end);
            line.startColor = new Color(1f, 0.85f, 0.35f, 0.95f);
            line.endColor = new Color(1f, 0.55f, 0.10f, 0.15f);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;

            Add(go, TracerSeconds, scaleOut: false);
        }

        /// <summary>
        /// Marks where a shot landed. Payload: u32 shooter | f32 point[3] |
        /// u16 damage | u16 health_after.
        /// </summary>
        public void OnHit(EventMsg ev, uint selfId)
        {
            if (ev.Data.Length < 20) return;
            var r = new WireReader(ev.Data);
            uint shooter = r.ReadU32();
            var point = new Vector3(r.ReadF32(), r.ReadF32(), r.ReadF32());

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(go.GetComponent<UnityEngine.Collider>());
            go.name = "impact";
            go.transform.SetParent(_parent, false);
            go.transform.position = point;
            go.transform.localScale = Vector3.one * 0.22f;

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(_lineMaterial)
            {
                // Your own hits read white; someone else's read orange, so a
                // crossfire is legible without a damage-number system.
                color = shooter == selfId ? new Color(1f, 1f, 1f, 0.9f) : new Color(1f, 0.5f, 0.2f, 0.9f),
            };
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            Add(go, ImpactSeconds, scaleOut: true);
        }

        /// <summary>
        /// A brief flash at the barrel. Local, cosmetic, no authority.
        ///
        /// It is PARENTED to the muzzle rather than positioned each shot, so
        /// it inherits the rig's sway and bob and stays welded to the barrel
        /// instead of drifting off it during a turn. That also puts it on the
        /// viewmodel layer, where it draws over the gun rather than being
        /// swallowed by the overlay camera's depth clear.
        /// </summary>
        public void OnLocalFire(Transform muzzle)
        {
            if (muzzle == null) return;
            if (_flash == null)
            {
                _flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(_flash.GetComponent<UnityEngine.Collider>());
                _flash.name = "muzzle-flash";
                _flash.transform.localScale = Vector3.one * 0.055f; // metres: the muzzle empty is unscaled
                var mr = _flash.GetComponent<MeshRenderer>();
                mr.sharedMaterial = new Material(_lineMaterial) { color = new Color(1f, 0.92f, 0.55f, 0.95f) };
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            _flash.transform.SetParent(muzzle, false);
            _flash.transform.localPosition = Vector3.zero;
            _flash.layer = muzzle.gameObject.layer;
            _flash.SetActive(true);
            _flashDiesAt = Time.time + FlashSeconds;
        }

        private void Add(GameObject go, float seconds, bool scaleOut)
            => _live.Add(new Fx { Go = go, BornAt = Time.time, DiesAt = Time.time + seconds, ScaleOut = scaleOut });

        /// <summary>Ages every effect. Call once per frame.</summary>
        public void Tick()
        {
            if (_flash != null && _flash.activeSelf && Time.time >= _flashDiesAt) _flash.SetActive(false);

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Fx fx = _live[i];
                if (fx.Go == null) { _live.RemoveAt(i); continue; }
                if (Time.time >= fx.DiesAt)
                {
                    Object.Destroy(fx.Go);
                    _live.RemoveAt(i);
                    continue;
                }
                if (fx.ScaleOut)
                {
                    float k = 1f - Mathf.InverseLerp(fx.BornAt, fx.DiesAt, Time.time);
                    fx.Go.transform.localScale = Vector3.one * (0.22f * Mathf.Max(k, 0.05f));
                }
            }
        }
    }
}
