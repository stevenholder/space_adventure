// Making bodies walk instead of slide.
//
// The models carry clips -- idle, walk, sprint, die -- retargeted onto them by
// art/tools/import_pack.mjs, and Godot's glTF importer puts them on an
// AnimationPlayer under the generated scene with no asset anywhere. This
// drives that player directly.
//
// DRIVEN BY OBSERVED SPEED, not by a state flag on the wire. The protocol
// carries no gait: it sends positions, and remotes are drawn at an
// interpolated pose (see Entities.cs). So the speed that should pick the clip
// is precisely the speed the body APPEARS to move at on screen. Reading a
// movement state off the wire would be a second opinion about the same thing,
// free to disagree with what the player can see -- feet skating during a walk
// is exactly that disagreement.

using Godot;

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
        internal const float WalkAt = 0.35f;
        internal const float SprintAt = 6.0f;

        /// <summary>Clips that play once and hold: dying, and the first-person fire and reload.</summary>
        internal static bool OneShot(string name) =>
            name == "die" || name.StartsWith("fp_fire") || name.StartsWith("fp_reload") || name.StartsWith("hit");

        /// <summary>
        /// Hold family suffix ("" or "_pistol"): `name + Class` when the body
        /// has it, else the plain clip.
        /// </summary>
        public string Class = "";

        internal static string Clip(AnimationPlayer p, string name, string cls) =>
            cls != "" && p.HasAnimation(name + cls) ? name + cls : name;

        private double _flinchUntil;

        /// <summary>
        /// The body was hit: a short flinch (`hit`, or `hit_armed` with the
        /// arms kept on the gun), then Drive takes over again. Never during
        /// death, and a burst re-starts it rather than stacking.
        /// </summary>
        public void Hit(double now)
        {
            if (_current == "die") return;
            string clip = Armed && _player.HasAnimation("hit_armed") ? Clip(_player, "hit_armed", Class) : "hit";
            if (!_player.HasAnimation(clip)) return;
            _player.Play(clip, 0.04);
            _player.Seek(0, true);
            _current = clip;
            _flinchUntil = now + _player.GetAnimation(clip).Length;
        }

        /// <summary>Long enough not to snap, short enough not to moonwalk.</summary>
        private const double Fade = 0.15;

        private readonly AnimationPlayer _player;
        private string _current;

        private CharacterAnim(AnimationPlayer player)
        {
            _player = player;
            // Godot's glTF import drops constant tracks, so idle carries no
            // Root track and a non-deterministic mixer leaves any bone the new
            // clip doesn't key wherever the last clip put it: a body revived
            // from `die` played idle lying flat on its back (playtest
            // 2026-10-01). Deterministic blends every untracked bone to rest.
            _player.Deterministic = true;
            // Everything loops except dying, which holds its last frame -- a
            // body that loops its own death animation is a bug you cannot unsee.
            foreach (string name in _player.GetAnimationList())
                _player.GetAnimation(name).LoopMode =
                    OneShot(name) ? Animation.LoopModeEnum.None : Animation.LoopModeEnum.Linear;
        }

        /// <summary>
        /// Wraps the AnimationPlayer under a freshly loaded model, or returns
        /// null when the model carries no clips -- the normal case for a prop.
        /// </summary>
        public static CharacterAnim For(Node model)
        {
            foreach (AnimationPlayer ap in AssetRegistry.Descendants<AnimationPlayer>(model))
                return ap.GetAnimationList().Length == 0 ? null : new CharacterAnim(ap);
            return null;
        }

        /// <summary>
        /// How long the death clip runs, so a caller knows when a body has
        /// finished dying. Zero when there is no death clip: "do not wait".
        /// </summary>
        public float DeathLength =>
            _player.HasAnimation("die") ? (float)_player.GetAnimation("die").Length : 0f;

        /// <summary>
        /// Something in hand: the gaits switch to their "_armed" variants (arms
        /// in the aim pose) when the model carries them.
        /// </summary>
        public bool Armed;

        /// <summary>
        /// Seated: hold `sit`, `sit_drive` or `sit_armed` (with the weapon
        /// class suffix) instead of a gait.
        /// </summary>
        public void Sit(string clip)
        {
            string want = clip == "sit_armed" ? Clip(_player, clip, Class) : clip;
            if (want == _current || !_player.HasAnimation(want)) return;
            _player.Play(want, Fade);
            _current = want;
        }

        /// <summary>Call every frame with the body's observed ground speed.</summary>
        public void Drive(float speed, bool dead)
        {
            string want = dead ? "die"
                        : speed > SprintAt ? "sprint"
                        : speed > WalkAt ? "walk"
                        : "idle";
            if (Armed && !dead && _player.HasAnimation(want + "_armed")) want = Clip(_player, want + "_armed", Class);
            if (!dead && Clock.Now < _flinchUntil) return;   // let the flinch finish
            if (want == _current) return;
            if (!_player.HasAnimation(want)) return;
            _player.Play(want, Fade);
            _current = want;
        }
    }
}
