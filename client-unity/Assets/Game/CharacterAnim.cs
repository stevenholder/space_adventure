// Making bodies walk instead of slide.
//
// Until now nothing in this client animated a character at all: remote players
// and NPCs were static meshes translated across the ground, which reads as
// furniture on rails rather than as someone running at you. The models now
// carry clips -- idle, walk, sprint, die -- retargeted onto them by
// art/tools/import_pack.mjs.
//
// LEGACY ANIMATION, not Mecanim, and not by preference. Mecanim needs an
// AnimatorController, which is an asset under Assets/, which CONVENTIONS.md
// rule 1 forbids anyone here from authoring. glTFast's Legacy import puts real
// AnimationClips on a plain Animation component with no asset anywhere, and
// this drives that component directly.
//
// DRIVEN BY OBSERVED SPEED, not by a state flag on the wire. The protocol
// carries no gait: it sends positions, and remotes are drawn at an
// interpolated pose (see Entities.cs). So the speed that should pick the clip
// is precisely the speed the body APPEARS to move at on screen, which is what
// the interpolated positions already encode. Reading a movement state off the
// wire and animating to that would be a second opinion about the same thing,
// free to disagree with what the player can see -- feet skating during a
// walk is exactly that disagreement.

using UnityEngine;

namespace SpaceAdventure.Game
{
    /// <summary>
    /// Picks a clip for one body from how fast it is moving and whether it is
    /// dead, and cross-fades to it.
    /// </summary>
    public sealed class CharacterAnim
    {
        /// <summary>
        /// GDD "M1 on-foot movement": walk_speed 4.5 m/s, sprint_speed 7.5.
        /// The sprint cut sits between them so neither gait has to be hit
        /// exactly, and the walk cut is low enough that a body nudged by
        /// interpolation jitter does not twitch into a walk while standing.
        /// </summary>
        private const float WalkAt = 0.35f;
        private const float SprintAt = 6.0f;

        /// <summary>Long enough not to snap, short enough not to moonwalk.</summary>
        private const float Fade = 0.15f;

        private readonly Animation _anim;
        private string _current;

        private CharacterAnim(Animation anim)
        {
            _anim = anim;

            // Everything loops except dying, which holds its last frame --
            // a body that loops its own death animation is a bug you cannot
            // unsee.
            foreach (AnimationState state in _anim)
                state.wrapMode = state.name == "die" ? WrapMode.ClampForever : WrapMode.Loop;

            _anim.playAutomatically = false;
        }

        /// <summary>
        /// Wraps the Animation component on a freshly loaded model, or returns
        /// null when the model carries no clips -- which is the normal case for
        /// a prop and not worth reporting.
        /// </summary>
        public static CharacterAnim For(GameObject model)
        {
            Animation anim = model == null ? null : model.GetComponentInChildren<Animation>();
            return anim == null || anim.GetClipCount() == 0 ? null : new CharacterAnim(anim);
        }

        /// <summary>
        /// How long the death clip runs, so a caller knows when a body has
        /// finished dying and can be taken off screen. Zero when there is no
        /// death clip, which means "do not wait for one".
        /// </summary>
        public float DeathLength
        {
            get
            {
                AnimationClip clip = _anim.GetClip("die");
                return clip == null ? 0f : clip.length;
            }
        }

        /// <summary>Call every frame with the body's observed ground speed.</summary>
        public void Drive(float speed, bool dead)
        {
            string want = dead ? "die"
                        : speed > SprintAt ? "sprint"
                        : speed > WalkAt ? "walk"
                        : "idle";

            if (want == _current) return;
            if (_anim.GetClip(want) == null) return;

            _anim.CrossFade(want, Fade);
            _current = want;
        }
    }
}
