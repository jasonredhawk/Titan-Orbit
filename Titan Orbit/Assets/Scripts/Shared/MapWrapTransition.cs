using Unity.Mathematics;

namespace TitanOrbit.Shared
{
    /// <summary>
    /// Client fade when a mover wraps across a map seam. Presentation snaps pose the same
    /// frame; the gameplay camera draws this overlay to hide the world pop for a short beat.
    /// Not networked — each observer fades when *their* view jumps.
    /// </summary>
    public static class MapWrapTransition
    {
        /// <summary>How long the fade stays visible after <see cref="NotifyWrap"/> (seconds).</summary>
        public const float DurationSeconds = 0.18f;

        /// <summary>Peak overlay opacity (black). High enough to hide the pop, low enough to read the new tile.</summary>
        public const float PeakAlpha = 0.55f;

        static float s_Remaining;

        /// <summary>1 at wrap, 0 when idle. Camera overlay multiplies this by <see cref="PeakAlpha"/>.</summary>
        public static float Fade01 =>
            DurationSeconds <= 1e-4f ? 0f : math.saturate(s_Remaining / DurationSeconds);

        /// <summary>True while a wrap fade is still drawing.</summary>
        public static bool IsActive => s_Remaining > 1e-4f;

        /// <summary>
        /// Starts (or restarts) the fade. Call once when the local ship or follow target
        /// jumps by more than half a map side.
        /// </summary>
        public static void NotifyWrap()
        {
            s_Remaining = DurationSeconds;
        }

        /// <summary>
        /// Advances the fade. Call once per presentation frame from the gameplay camera.
        /// </summary>
        /// <param name="dt">Unscaled or scaled frame delta (seconds).</param>
        public static void Tick(float dt)
        {
            if (s_Remaining <= 0f)
                return;
            s_Remaining = math.max(0f, s_Remaining - math.max(0f, dt));
        }

        /// <summary>Clears any in-flight fade (leave match / camera teardown).</summary>
        public static void Reset()
        {
            s_Remaining = 0f;
        }
    }
}
