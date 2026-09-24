// Wall-clock seconds since the process started, as a double, read from one
// place. Unity's Time.time was a float that every subsystem read directly;
// here the frame loop sets Dt once per frame and everyone reads Now.

using Godot;

namespace SpaceAdventure.Game
{
    public static class Clock
    {
        /// <summary>Seconds since startup. Monotonic.</summary>
        public static double Now => Time.GetTicksUsec() / 1_000_000.0;

        /// <summary>This frame's delta, set by Boot._Process before anything reads it.</summary>
        public static double Dt { get; internal set; }
    }
}
