// Drawing the rock scatter.
//
// Where the rocks go is RockScatter, under Assets/Game/Core, engine-free and
// checked headless. This file is only the part that needs Unity: turning sim
// -space placements into transforms and submitting them.
//
// The Unity client had no scatter at all. The asteroid is a 150 m ball with a
// 23 m horizon and nothing on it but six landmarks, so there was no near-field
// detail anywhere -- nothing to judge speed or distance against while walking,
// which on a sphere this small is most of what tells you that you are moving.
//
// Graphics.RenderMeshInstanced, not GameObjects: four hundred rocks is three
// draw calls and no transforms. Four hundred GameObjects would be four hundred
// renderers to cull and re-evaluate every frame, for props that never move.

using System;
using System.Collections.Generic;
using SpaceAdventure.Sim;
using UnityEngine;

namespace SpaceAdventure.Game
{
    public sealed class Rocks
    {
        private static readonly string[] VariantAssets =
            { "prop.rock.a", "prop.rock.b", "prop.rock.c" };

        private readonly Material _material;
        private readonly AssetRegistry _assets;

        // Instance matrices per variant, and the mesh each batch draws with
        // once its .glb has landed.
        private readonly List<Matrix4x4>[] _batches =
            { new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>() };
        private readonly Mesh[] _meshes = new Mesh[3];
        private RenderParams _rp;
        private bool _ready;

        public Rocks(Material material, AssetRegistry assets)
        {
            _material = material;
            _assets = assets;
        }

        /// <summary>
        /// Scatter for this world, then start loading the three rock models.
        ///
        /// A variant whose .glb has not arrived is simply not drawn. That is
        /// deliberately unlike every other asset here, which shows a box until
        /// its model lands: one box in place of one crate reads as a model
        /// still loading, but four hundred cubes strewn across the planet read
        /// as a bug, and they would be the most visible thing on screen.
        /// </summary>
        public void Build(TerrainField terrain, uint worldSeed)
        {
            foreach (List<Matrix4x4> b in _batches) b.Clear();

            foreach (RockPlacement p in RockScatter.Scatter(terrain, worldSeed))
            {
                // The one handedness flip: ToUnity mirrors Z, so the rotation
                // is rebuilt from converted vectors rather than by converting a
                // quaternion. Local +Y goes along the surface direction, then
                // the spin turns about that same axis.
                Vector3 up = TerrainMesh.ToUnity(p.Dir);
                Quaternion rot = Quaternion.AngleAxis((float)(p.Spin * Mathf.Rad2Deg), up)
                               * Quaternion.FromToRotation(Vector3.up, up);

                _batches[p.Variant].Add(Matrix4x4.TRS(
                    TerrainMesh.ToUnity(p.Pos), rot,
                    new Vector3((float)p.Scale.X, (float)p.Scale.Y, (float)p.Scale.Z)));
            }

            // worldBounds has to be set. RenderMeshInstanced does not derive
            // it from the instance list -- it culls against whatever is in
            // RenderParams, and the default is a zero-size box at the origin,
            // which culls every rock on the planet and looks exactly like the
            // scatter never ran. The planet fits inside RadiusMax, so a box
            // that contains the whole sphere is both correct and the least
            // that can be said about props spread over all of it.
            float extent = (float)TerrainField.RadiusMax * 2f;
            _rp = new RenderParams(_material)
            {
                receiveShadows = true,
                worldBounds = new Bounds(Vector3.zero, new Vector3(extent, extent, extent)),
            };
            _ready = true;

            for (int v = 0; v < 3; v++)
            {
                int variant = v;
                _assets.Mesh(VariantAssets[v], m => _meshes[variant] = m);
            }
            Debug.Log($"rocks: {_batches[0].Count}/{_batches[1].Count}/{_batches[2].Count}");
        }

        /// <summary>Once per frame. Cheap while nothing has loaded yet.</summary>
        public void Render()
        {
            if (!_ready) return;
            try
            {
                for (int v = 0; v < 3; v++)
                {
                    if (_meshes[v] == null || _batches[v].Count == 0) continue;
                    // Each batch is at most TargetCount, well under
                    // RenderMeshInstanced's 1023-per-call limit.
                    Graphics.RenderMeshInstanced(_rp, _meshes[v], 0, _batches[v]);
                }
            }
            catch (Exception e)
            {
                // Draw calls happen every frame, so anything that throws here
                // throws sixty times a second and buries the console -- which
                // is exactly what a material without instancing enabled did.
                // Say it once, then stop drawing rocks. Scenery is not worth
                // hiding every other message in the log.
                _ready = false;
                Debug.LogError($"rock scatter disabled after a draw error: {e.Message}");
            }
        }
    }
}
